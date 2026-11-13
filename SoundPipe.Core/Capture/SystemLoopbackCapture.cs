using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Devices;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Capture;

/// <summary>
/// 整系统循环捕获（设计 F2 / §5.1）——自研轮询式 WASAPI loopback。
/// 弃用 NAudio WasapiLoopbackCapture：其事件驱动（EVENTCALLBACK）实现实测在真实设备
/// loopback 上事件句柄不被置位（启动后仅收到 1 个空包，播放期间也无数据——冒烟复现），
/// 故改用微软官方 loopback 样例（ApplicationLoopback）同款轮询模式：
/// GetNextPacketSize + 5ms 睡眠，无事件依赖，静音/活动期行为一致。
/// Windows 循环捕获仅接受设备 MixFormat → §5.1 回退链由 SampleConverter 转引擎格式；
/// 读取循环内零分配（设计 §4.2/N5）。
/// 异常经 RecordingStoppedUnexpectedly 上报（引擎据此执行 §5.6 重启策略）。
/// </summary>
public sealed class SystemLoopbackCapture : IDisposable
{
    private const long BufferDurationHns = 1_000_000; // 100ms 硬件缓冲

    /// <summary>捕获目标设备 ID（null = 系统默认输出）。线程内按 ID 重新打开，
    /// 不持有外部 MMDevice 引用（外部可能已 Dispose）。</summary>
    private readonly string? _deviceId;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _dropLogCounter;

    /// <summary>捕获流异常停止（引擎据此执行 §5.6 重启策略）</summary>
    public event Action<Exception>? RecordingStoppedUnexpectedly;

    /// <summary>是否正在捕获</summary>
    public bool IsRunning { get; private set; }

    public SystemLoopbackCapture(string? deviceId = null) => _deviceId = deviceId;

    /// <summary>启动捕获并写入环形缓冲。</summary>
    public void Start(SpScRingBuffer ring)
    {
        _dropLogCounter = 0;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => CaptureLoop(ring, _cts.Token))
        {
            IsBackground = true,
            Name = "SoundPipe.SystemCap",
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

    // ------------------------------------------------------------------ 捕获线程

    private void CaptureLoop(SpScRingBuffer ring, CancellationToken token)
    {
        var hrCo = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.CoinitMultithreaded);
        var comInitialized = hrCo >= 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrEmpty(_deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : DeviceService.GetDeviceById(_deviceId)
                  ?? throw new InvalidOperationException("整系统捕获设备不可用");
            try
            {
                RunStream(device.AudioClient, ring, token);
            }
            finally
            {
                device.Dispose(); // 级联释放其 AudioClient
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Log.Error("整系统循环捕获失败", ex);
            RecordingStoppedUnexpectedly?.Invoke(ex);
        }
        finally
        {
            if (comInitialized) Ole32.CoUninitialize();
            IsRunning = false;
            Log.Info("整系统循环捕获线程退出");
        }
    }

    /// <summary>轮询式读取循环（直通或 MixFormat 回退转换，循环内零分配）</summary>
    private void RunStream(AudioClient audioClient, SpScRingBuffer ring, CancellationToken token)
    {
        // Loopback 仅接受设备 MixFormat（Windows 限制）
        var mixFormat = audioClient.MixFormat;
        var converter = new SampleConverter();
        converter.Configure(mixFormat);
        var blockAlign = mixFormat.BlockAlign;

        audioClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
            BufferDurationHns, 0, mixFormat, Guid.Empty); // 轮询模式：无 EventCallback

        var captureClient = audioClient.AudioCaptureClient;
        try
        {
            audioClient.Start();
            IsRunning = true;
            Log.Info($"整系统循环捕获启动：mixFormat={mixFormat}，直通={converter.IsPassthrough}");

            // 预分配单包上限（100ms 缓冲），正常运行零分配
            var packetBytes = new byte[(int)(BufferDurationHns / 10_000) * mixFormat.SampleRate / 1000 * blockAlign];

            while (!token.IsCancellationRequested)
            {
                if (captureClient.GetNextPacketSize() == 0)
                {
                    token.WaitHandle.WaitOne(5); // 官方样例轮询节奏
                    continue;
                }

                var data = captureClient.GetBuffer(out var frames, out var bufFlags);
                var bytes = frames * blockAlign;
                if (bytes > packetBytes.Length) packetBytes = new byte[bytes]; // 极少发生

                // 只有静音包需要写零；正常包由 Marshal.Copy 覆盖，省掉每包一次的无谓清零
                if ((bufFlags & AudioClientBufferFlags.Silent) != 0)
                    Array.Clear(packetBytes, 0, bytes);
                else
                    Marshal.Copy(data, packetBytes, 0, bytes);

                if (converter.TryConvert(packetBytes.AsSpan(0, bytes), out var buffer, out var samples))
                {
                    var written = ring.Write(buffer.AsSpan(0, samples));
                    if (written < samples && ++_dropLogCounter % 200 == 1)
                        Log.Warn($"捕获环满，丢弃 {samples - written} 样本（累计提示 {_dropLogCounter} 次）");
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
