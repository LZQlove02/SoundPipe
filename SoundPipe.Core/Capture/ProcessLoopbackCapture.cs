using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Interop;
using SoundPipe.Core.Logging;

// NAudio 2.2.1 的 IAudioCaptureClient 为 internal → 用自声明接口（Interop.IAudioCaptureClient）
// 注：NAudio.CoreAudioApi.Interfaces 也有同名 IActivateAudioInterfaceCompletionHandler/IAgileObject，
//     与 Interop 自声明版本二义，这里显式指定用自声明版本（含 PreserveSig，签名更可控）。
using CaptureClientAlias = SoundPipe.Core.Interop.IAudioCaptureClient;
using CompletionHandler = SoundPipe.Core.Interop.IActivateAudioInterfaceCompletionHandler;
using AgileObject = SoundPipe.Core.Interop.IAgileObject;
using AsyncOperation = SoundPipe.Core.Interop.IActivateAudioInterfaceAsyncOperation;

namespace SoundPipe.Core.Capture;

/// <summary>
/// 按应用进程循环捕获（设计 F1 / §5.2 / M2）。
/// 通过 ActivateAudioInterfaceAsync（AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK + 目标 PID）
/// 打开指定进程（含子进程树）的捕获流；系统级 API，无需 DLL 注入（与 OBS 应用音频捕获同款技术）。
/// 仅 Windows 10 2004+ 可用；格式协商：请求引擎格式 → 不支持则回退 MixFormat + SampleConverter。
/// 自带捕获线程（MTA），停止安全；异常经 Failed 事件上报（引擎按 §5.6 重启策略处理）。
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const int AudclntEUnsupportedFormat = unchecked((int)0x8889000A);
    private const int BufferDurationHns = 1_000_000; // 100ms 硬件缓冲

    private static readonly Guid AudioCaptureClientIid =
        new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly int _pid;
    private readonly bool _includeTree;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    public string ProcessName { get; }
    public bool IsRunning { get; private set; }

    /// <summary>捕获流异常（含激活失败）。引擎据此执行重启策略（§5.6）。</summary>
    public event Action<Exception>? Failed;

    public ProcessLoopbackCapture(int pid, bool includeTree, string processName)
    {
        _pid = pid;
        _includeTree = includeTree;
        ProcessName = processName;
    }

    /// <summary>启动捕获并写入环形缓冲（内部线程完成激活/初始化）</summary>
    public void Start(SpScRingBuffer ring)
    {
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => CaptureLoop(ring, _cts.Token))
        {
            IsBackground = true,
            Name = $"SoundPipe.AppCap.{ProcessName}",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Stop()
    {
        var cts = _cts;
        var thread = _thread;
        _cts = null;
        _thread = null;
        IsRunning = false;
        if (cts is null) return;

        try { cts.Cancel(); } catch { }
        // Join 超时说明线程可能仍在 WaitHandle 上等待：延迟释放 CTS，
        // 避免 ObjectDisposedException 被误报成"捕获失败"。
        if (thread is null || thread.Join(1000)) cts.Dispose();
        else _ = Task.Delay(5000).ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ 捕获线程

    private void CaptureLoop(SpScRingBuffer ring, CancellationToken token)
    {
        var hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.CoinitMultithreaded);
        var comInitialized = hr >= 0;
        try
        {
            var audioClient = Activate(token);
            try
            {
                RunStream(audioClient, ring, token);
            }
            finally
            {
                Marshal.ReleaseComObject(audioClient);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Log.Error($"进程捕获失败：{ProcessName}(pid={_pid})", ex);
            Failed?.Invoke(ex);
        }
        finally
        {
            if (comInitialized) Ole32.CoUninitialize();
            IsRunning = false;
            Log.Info($"进程捕获线程退出：{ProcessName}(pid={_pid})");
        }
    }

    /// <summary>激活进程循环捕获音频客户端（本线程需 MTA）</summary>
    private IAudioClient Activate(CancellationToken token)
    {
        var handler = new ActivationHandler();
        var riid = typeof(IAudioClient).GUID;

        var activationParams = new AudioClientActivationParams
        {
            ActivationType = NativeAudioActivation.AudioClientActivationTypeProcessLoopback,
            ProcessLoopbackParams = new ProcessLoopbackParams
            {
                ProcessLoopbackMode = _includeTree
                    ? NativeAudioActivation.ProcessLoopbackModeIncludeTargetProcessTree
                    : NativeAudioActivation.ProcessLoopbackModeExcludeTargetProcessTree,
                TargetProcessId = (uint)_pid
            }
        };
        var blob = PropVariantBlob.Create(activationParams);
        var outerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        Marshal.StructureToPtr(blob, outerPtr, false);

        // 参数内存只允许在"激活服务已读完参数"后释放（提前释放 → 系统读到复用内存
        // 的垃圾参数 → 0x80070002）。唯一安全时点是 ActivateCompleted 回调触发后，
        // 因此释放所有权交给回调（OnCompleted），一次性执行。
        var cleanupDone = 0;
        void CleanupParams()
        {
            if (Interlocked.Exchange(ref cleanupDone, 1) != 0) return;
            blob.Free();
            Marshal.FreeHGlobal(outerPtr);
        }

        // 回调可能在另一线程立即触发 → OnCompleted 必须在发起激活前赋值
        handler.OnCompleted = CleanupParams;

        var hr = NativeAudioActivation.ActivateAudioInterfaceAsync(
            NativeAudioActivation.VirtualAudioDeviceProcessLoopback,
            ref riid, outerPtr, handler, out _);
        if (hr < 0)
        {
            // 同步失败：请求未入队，回调不会触发，此处直接回收
            CleanupParams();
            Marshal.ThrowExceptionForHR(hr);
        }

        // 等待激活完成（系统回调 ActivateCompleted）
        if (!handler.Completed.WaitOne(5000))
        {
            // 激活挂起：不能立即释放（服务稍后仍可能读取）。延迟 10s 兜底回收
            //（若回调此后触发，Interlocked 保证只释放一次）
            Task.Delay(10_000).ContinueWith(_ => CleanupParams(), TaskScheduler.Default);
            throw new TimeoutException($"进程捕获激活超时：{ProcessName}(pid={_pid})");
        }
        token.ThrowIfCancellationRequested();

        Marshal.ThrowExceptionForHR(handler.Result);
        var client = (IAudioClient)handler.Interface!;
        IsRunning = true;
        return client;
    }

    /// <summary>初始化 + 事件驱动读取循环（直通或 MixFormat 回退转换，回调内零分配）</summary>
    private void RunStream(IAudioClient audioClient, SpScRingBuffer ring, CancellationToken token)
    {
        SampleConverter? converter = null;
        var streamBlockAlign = 8; // 引擎格式：2ch × float32 = 8 字节/帧；回退时改为 MixFormat.BlockAlign
        var sessionGuid = Guid.Empty;
        var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback;
        var hr = audioClient.Initialize(AudioClientShareMode.Shared, flags,
            BufferDurationHns, 0, EngineFormat.Wave, ref sessionGuid);

        if (hr == AudclntEUnsupportedFormat)
        {
            // §5.1 回退：引擎格式不被支持 → 设备 MixFormat + 转换
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out var mixPtr));
            var mixFormat = WaveFormat.MarshalFromPtr(mixPtr);
            Marshal.FreeHGlobal(mixPtr);
            converter = new SampleConverter();
            converter.Configure(mixFormat);
            streamBlockAlign = mixFormat.BlockAlign;
            Log.Info($"进程捕获格式回退：{ProcessName} mixFormat={mixFormat}");

            hr = audioClient.Initialize(AudioClientShareMode.Shared, flags,
                BufferDurationHns, 0, mixFormat, ref sessionGuid);
        }
        Marshal.ThrowExceptionForHR(hr);

        using var streamEvent = new AutoResetEvent(false);
        Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(streamEvent.SafeWaitHandle.DangerousGetHandle()));

        audioClient.GetService(AudioCaptureClientIid, out var serviceObj);
        var captureClient = (CaptureClientAlias)serviceObj;
        try
        {
            Marshal.ThrowExceptionForHR(audioClient.Start());
            Log.Info($"进程捕获启动：{ProcessName}(pid={_pid})，includeTree={_includeTree}，直通={converter is null}");

            var packetBytes = Array.Empty<byte>();
            var silentFloats = Array.Empty<float>();
            while (!token.IsCancellationRequested)
            {
                // 事件驱动为主；个别驱动不置位事件（整系统 loopback 已实测如此），
                // 超时后仍然排空数据包，等价于轮询兜底，避免"启动成功但没有声音"。
                streamEvent.WaitOne(5);

                // 排空当前所有数据包
                while (true)
                {
                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out var packetFrames));
                    if (packetFrames == 0) break;
                    if (token.IsCancellationRequested) break;

                    Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                        out var dataPtr, out var frames, out var bufFlags, out _, out _));

                    var bytes = frames * streamBlockAlign;
                    if (packetBytes.Length < bytes) packetBytes = new byte[bytes]; // 极少发生

                    if ((bufFlags & AudioClientBufferFlags.Silent) != 0)
                    {
                        // 静音包：写零
                        if (converter is null)
                        {
                            var floatCount = (int)frames * 2;
                            if (silentFloats.Length < floatCount) silentFloats = new float[floatCount];
                            Array.Clear(silentFloats, 0, floatCount);
                            ring.Write(silentFloats.AsSpan(0, floatCount));
                        }
                        else
                        {
                            Array.Clear(packetBytes, 0, bytes);
                            if (converter.TryConvert(packetBytes.AsSpan(0, bytes), out var zb, out var zs))
                                ring.Write(zb.AsSpan(0, zs));
                        }
                    }
                    else
                    {
                        Marshal.Copy(dataPtr, packetBytes, 0, bytes);
                        if (converter is null)
                        {
                            var floats = MemoryMarshal.Cast<byte, float>(packetBytes.AsSpan(0, bytes));
                            ring.Write(floats);
                        }
                        else if (converter.TryConvert(packetBytes.AsSpan(0, bytes), out var buffer, out var samples))
                        {
                            ring.Write(buffer.AsSpan(0, samples));
                        }
                    }

                    Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                }
            }

            audioClient.Stop();
        }
        finally
        {
            Marshal.ReleaseComObject(captureClient);
        }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ 激活回调

    /// <summary>激活完成回调（实现 COM 回调接口 + IAgileObject）</summary>
    private sealed class ActivationHandler : CompletionHandler, AgileObject
    {
        public readonly ManualResetEvent Completed = new(false);
        public int Result;
        public object? Interface;

        /// <summary>激活结束（无论成败）后必须执行的参数内存回收，由 Activate 设置</summary>
        public Action? OnCompleted;

        public int ActivateCompleted(AsyncOperation operation)
        {
            try
            {
                var hr = operation.GetActivateResult(out Result, out var ifacePtr);
                if (hr < 0)
                {
                    Result = hr;
                }
                else if (ifacePtr != IntPtr.Zero)
                {
                    Interface = Marshal.GetObjectForIUnknown(ifacePtr);
                    Marshal.Release(ifacePtr); // 所有权移交 RCW
                }
            }
            catch (Exception ex)
            {
                Result = Marshal.GetHRForException(ex);
            }
            finally
            {
                // 激活服务读完参数后才触发本回调 → 此处释放参数内存是安全的唯一时点
                try { OnCompleted?.Invoke(); } catch { }
                Completed.Set();
            }
            return 0;
        }
    }
}

/// <summary>ole32 声明</summary>
internal static class Ole32
{
    public const int CoinitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();
}
