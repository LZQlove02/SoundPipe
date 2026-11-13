using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Devices;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Capture;

/// <summary>
/// 麦克风（录音端点）捕获——麦克风混音功能（M4）：
/// 采集真实麦克风，经 SampleConverter 转引擎格式后写入环形缓冲，与音乐在 Mixer 叠加，
/// 实现通话场景"人声 + 音乐"混合转发。轮询式读取（与 SystemLoopbackCapture 同款，
/// 无事件依赖），循环内零分配。
/// 异常经 RecordingStoppedUnexpectedly 上报（引擎据此执行 §5.6 重启策略）。
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private const long BufferDurationHns = 1_000_000; // 100ms 硬件缓冲

    /// <summary>录音设备 ID（null = 系统默认麦克风）</summary>
    private readonly string? _deviceId;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _dropLogCounter;

    /// <summary>捕获流异常停止（引擎据此执行 §5.6 重启策略）</summary>
    public event Action<Exception>? RecordingStoppedUnexpectedly;

    public bool IsRunning { get; private set; }

    public MicrophoneCapture(string? deviceId = null) => _deviceId = deviceId;

    /// <summary>启动捕获并写入环形缓冲。</summary>
    public void Start(SpScRingBuffer ring)
    {
        _dropLogCounter = 0;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => CaptureLoop(ring, _cts.Token))
        {
            IsBackground = true,
            Name = "SoundPipe.MicCap",
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
        if (cts is null) return;

        try { cts.Cancel(); } catch { }
        // Join 超时说明线程可能仍在 WaitHandle 上等待：延迟释放 CTS，
        // 避免 ObjectDisposedException 被误报成"捕获失败"。
        if (thread is null || thread.Join(1000)) cts.Dispose();
        else _ = Task.Delay(5000).ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    private void CaptureLoop(SpScRingBuffer ring, CancellationToken token)
    {
        var hrCo = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.CoinitMultithreaded);
        var comInitialized = hrCo >= 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = ResolveCaptureDevice(enumerator);
            Log.Info($"麦克风混音源：{device.FriendlyName}");
            RunStream(device.AudioClient, ring, token);
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Log.Error("麦克风捕获失败", ex);
            RecordingStoppedUnexpectedly?.Invoke(ex);
        }
        finally
        {
            if (comInitialized) Ole32.CoUninitialize();
            IsRunning = false;
            Log.Info("麦克风捕获线程退出");
        }
    }

    /// <summary>
    /// 解析录音设备。默认时必须选"物理麦克风"：一键配置会把系统默认麦克风指向虚拟录音端，
    /// 若照常使用会捕获到转发输出自身（回环），真实人声反而采不到。
    /// 因此：系统默认端不是虚拟设备 → 直接用它（符合 UI 的"系统默认"语义）；
    /// 是虚拟设备 → 枚举第一个物理录音端点。
    /// </summary>
    private MMDevice ResolveCaptureDevice(MMDeviceEnumerator enumerator)
    {
        if (!string.IsNullOrEmpty(_deviceId))
            return DeviceService.GetDeviceById(_deviceId)
                   ?? throw new InvalidOperationException("麦克风设备不可用");

        MMDevice? systemDefault = null;
        try
        {
            systemDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            if (!DeviceService.LooksLikeVirtualCapture(systemDefault.FriendlyName))
                return systemDefault;
        }
        catch
        {
            systemDefault?.Dispose();
            systemDefault = null;
        }

        // 默认端是虚拟设备（setup 之后）：改用第一个物理麦克风
        systemDefault?.Dispose();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (!DeviceService.LooksLikeVirtualCapture(d.FriendlyName)) return d;
            d.Dispose();
        }

        // 没有任何物理麦克风才退回系统默认（哪怕链路可能回环，至少保持可用）
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    private void RunStream(AudioClient audioClient, SpScRingBuffer ring, CancellationToken token)
    {
        // 共享模式捕获仅接受设备 MixFormat → SampleConverter 转引擎格式
        var mixFormat = audioClient.MixFormat;
        var converter = new SampleConverter();
        converter.Configure(mixFormat);
        var blockAlign = mixFormat.BlockAlign;

        audioClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.NoPersist,
            BufferDurationHns, 0, mixFormat, Guid.Empty); // 轮询模式

        var captureClient = audioClient.AudioCaptureClient;
        try
        {
            audioClient.Start();
            IsRunning = true;
            Log.Info($"麦克风捕获启动：mixFormat={mixFormat}，直通={converter.IsPassthrough}");

            var packetBytes = new byte[(int)(BufferDurationHns / 10_000) * mixFormat.SampleRate / 1000 * blockAlign];

            while (!token.IsCancellationRequested)
            {
                if (captureClient.GetNextPacketSize() == 0)
                {
                    token.WaitHandle.WaitOne(5);
                    continue;
                }

                var data = captureClient.GetBuffer(out var frames, out var bufFlags);
                var bytes = frames * blockAlign;
                if (bytes > packetBytes.Length) packetBytes = new byte[bytes];

                // 只有静音包需要写零；正常包由 Marshal.Copy 覆盖，省掉每包一次的无谓清零
                if ((bufFlags & AudioClientBufferFlags.Silent) != 0)
                    Array.Clear(packetBytes, 0, bytes);
                else
                    Marshal.Copy(data, packetBytes, 0, bytes);

                if (converter.TryConvert(packetBytes.AsSpan(0, bytes), out var buffer, out var samples))
                {
                    var written = ring.Write(buffer.AsSpan(0, samples));
                    if (written < samples && ++_dropLogCounter % 200 == 1)
                        Log.Warn($"麦克风环满，丢弃 {samples - written} 样本");
                }

                captureClient.ReleaseBuffer(frames);
            }

            audioClient.Stop();
        }
        finally
        {
            captureClient.Dispose();
        }
    }

    public void Dispose() => Stop();
}
