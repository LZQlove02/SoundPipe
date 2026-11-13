using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundPipe.Core.Devices;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Diagnostics;

/// <summary>链路诊断结果</summary>
public enum LinkStatus
{
    /// <summary>桥通：写入目标的声音在录音端可见</summary>
    Ok,

    /// <summary>目标渲染设备不可用</summary>
    TargetMissing,

    /// <summary>录音端不存在（驱动未装/被禁用）</summary>
    CaptureDeviceMissing,

    /// <summary>写入正常但录音端无信号：宿主应用（Voicemeeter）未运行或路由未开</summary>
    BridgeBroken,
}

/// <summary>链路诊断结果</summary>
public sealed record LinkCheckResult(LinkStatus Status, string Message, string? CaptureDeviceName)
{
    public bool IsOk => Status == LinkStatus.Ok;
}

/// <summary>
/// 转发链路端到端诊断（回路测试）：
/// 向目标渲染设备播放 1s 440Hz 正弦，同时从对应录音端（Voicemeeter Output / CABLE Output）采样，
/// 以 RMS 判定桥是否通。桥断时自动拉起本机已装的 Voicemeeter 并重测一次。
/// </summary>
public static class LinkDiagnostics
{
    private const double SineAmplitude = 0.5;
    private const double RmsThreshold = 0.05; // 正弦 0.5 幅度 → RMS≈0.35；噪声底 <0.01

    /// <summary>对指定目标设备做全链路诊断（耗时约 5-8 秒）</summary>
    public static LinkCheckResult RunCheck(string? targetDeviceId)
    {
        // 1. 目标设备（未指定时回退到默认虚拟输入端）
        var id = string.IsNullOrEmpty(targetDeviceId) ? DeviceService.GetDefaultTarget()?.Id : targetDeviceId;
        var target = DeviceService.GetDeviceById(id ?? "");
        if (target is null) return new LinkCheckResult(LinkStatus.TargetMissing, "目标设备不可用，请重新选择", null);
        using (target)
        {
            // 2. 对应录音端
            var capture = FindBridgeCaptureDevice(target.FriendlyName);
            if (capture is null)
                return new LinkCheckResult(LinkStatus.CaptureDeviceMissing,
                    "未找到可用的虚拟声卡录音端（变音麦克风 / Voicemeeter Output / CABLE Output）。\n" +
                    "若未安装虚拟声卡，可从 https://vb-audio.com/Cable/ 免费安装 VB-CABLE（装完重启后选择 CABLE Input 作为转发目标）。",
                    null);
            using (capture)
            {
                // 3. 回路测试
                if (LoopTest(target, capture))
                    return new LinkCheckResult(LinkStatus.Ok, "链路通：目标→录音端信号正常", capture.FriendlyName);

                // 4. 桥断：尝试拉起 Voicemeeter 后重测一次
                if (IsVoicemeeterDevice(capture.FriendlyName) && !IsVoicemeeterRunning())
                {
                    Log.Info("诊断：桥断且 Voicemeeter 未运行，尝试自动启动");
                    if (TryStartVoicemeeter())
                    {
                        Thread.Sleep(4000); // 等虚拟声卡桥就绪
                        if (LoopTest(target, capture))
                            return new LinkCheckResult(LinkStatus.Ok,
                                $"链路通（已自动启动 Voicemeeter，请保持其后台运行）", capture.FriendlyName);
                    }
                }

                var hint = IsVoicemeeterDevice(capture.FriendlyName)
                    ? "请检查 Voicemeeter 面板：最上面虚拟输入条（VAIO）的 B1 按钮需点亮（路由到虚拟输出）"
                    : "请检查宿主应用是否运行";
                return new LinkCheckResult(LinkStatus.BridgeBroken,
                    $"桥不通：声音已写入目标，但录音端无信号。{hint}", capture.FriendlyName);
            }
        }
    }

    // ------------------------------------------------------------------ 录音端匹配

    /// <summary>找虚拟声卡对应的录音端。匹配：Voicemeeter Output / CABLE Output /
    /// 变音麦克风（变声器软件对 VAIO B 端的本地化命名）等 Active 端点。
    /// 传入目标名时优先按桥配对（CABLE Input → CABLE Output），避免多实例 VAIO 时听错端点。</summary>
    public static MMDevice? FindBridgeCaptureDevice(string? targetFriendlyName = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        MMDevice? fallback = null;
        try
        {
            foreach (var d in devices)
            {
                var name = d.FriendlyName;
                if (targetFriendlyName is not null &&
                    targetFriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
                {
                    fallback?.Dispose();
                    return d;
                }

                var isBridge =
                    name.Contains("Voicemeeter Output", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("变音麦克风", StringComparison.Ordinal) ||
                    (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
                     (name.Contains("麦克风", StringComparison.Ordinal) ||
                      name.Contains("Microphone", StringComparison.OrdinalIgnoreCase)));
                if (!isBridge) { d.Dispose(); continue; }

                if (fallback is null) fallback = d;
                else d.Dispose();
            }
        }
        catch
        {
            // 枚举异常时返回已找到的
        }
        return fallback;
    }

    // ------------------------------------------------------------------ 回路测试

    /// <summary>向 target 播 1s 正弦，同时从 capture 录 ~1.8s，返回录音端是否收到信号</summary>
    private static bool LoopTest(MMDevice target, MMDevice capture)
    {
        byte[]? recorded = null;
        WaveFormat? recFormat = null;
        var stopped = new ManualResetEventSlim(false);

        using var recorder = new WasapiCapture(capture);
        recorder.DataAvailable += (_, e) =>
        {
            // 只留最后 1.5s（首次 DataAvailable 可能带 0 长度）
            var old = recorded;
            var merged = new byte[(old?.Length ?? 0) + e.BytesRecorded];
            if (old is not null) Buffer.BlockCopy(old, 0, merged, 0, old.Length);
            Buffer.BlockCopy(e.Buffer, 0, merged, old?.Length ?? 0, e.BytesRecorded);
            recorded = merged;
        };
        recorder.RecordingStopped += (_, e) =>
        {
            recFormat = recorder.WaveFormat;
            if (e.Exception is not null) Log.Warn($"诊断录音异常：{e.Exception.Message}");
            stopped.Set();
        };

        recorder.StartRecording();

        try
        {
            using var ac = target.AudioClient;
            var deviceFormat = ac.MixFormat;

            using var player = new WasapiOut(target, AudioClientShareMode.Shared, true, 50);
            player.Init(new SineWaveProvider(1.0, 440, SineAmplitude, deviceFormat));
            player.Play();
            Thread.Sleep(1600); // 播 1s 波形 + 余量
            player.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"诊断播放失败：{ex.Message}");
        }

        stopped.Wait(2000);
        try { recorder.StopRecording(); } catch { }
        stopped.Wait(2000);
        if (recorded is null || recorded.Length == 0 || recFormat is null) return false;
        var rms = ComputeRms(recorded, recFormat);
        Log.Info($"诊断回路：录音端={capture.FriendlyName}，bytes={recorded.Length}，RMS={rms:0.0000}");
        return rms > RmsThreshold;
    }

    /// <summary>按格式解析 RMS（float32 / pcm16 / pcm24 / pcm32）</summary>
    private static double ComputeRms(byte[] bytes, WaveFormat fmt)
    {
        double sum2 = 0;
        long n = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var floats = MemoryMarshal.Cast<byte, float>(bytes);
            foreach (var v in floats) { sum2 += (double)v * v; n++; }
        }
        else if (fmt.BitsPerSample == 16)
        {
            var shorts = MemoryMarshal.Cast<byte, short>(bytes);
            foreach (var v in shorts) { var f = v / 32768f; sum2 += (double)f * f; n++; }
        }
        else if (fmt.BitsPerSample == 24)
        {
            for (var i = 0; i + 3 <= bytes.Length; i += 3)
            {
                var v = bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                var f = v / 8388608f;
                sum2 += (double)f * f;
                n++;
            }
        }
        else if (fmt.BitsPerSample == 32)
        {
            var ints = MemoryMarshal.Cast<byte, int>(bytes);
            foreach (var v in ints) { var f = v / 2147483648f; sum2 += (double)f * f; n++; }
        }
        return n == 0 ? 0 : Math.Sqrt(sum2 / n);
    }

    /// <summary>440Hz 正弦 IWaveProvider（严格按设备 MixFormat 编码，播完自动返回 0 停止）</summary>
    private sealed class SineWaveProvider : IWaveProvider
    {
        private readonly WaveFormat _fmt;
        private readonly int _bytesPerSample;
        private readonly bool _isFloat;
        private double _phase;
        private int _framesLeft;

        public SineWaveProvider(double seconds, double freq, double amp, WaveFormat fmt)
        {
            _fmt = fmt;
            _bytesPerSample = Math.Max(1, fmt.BlockAlign / Math.Max(1, fmt.Channels));
            _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && _bytesPerSample == 4;
            _framesLeft = (int)(seconds * fmt.SampleRate);
            Frequency = freq;
            Amplitude = amp;
        }

        private double Frequency { get; }
        private double Amplitude { get; }

        public WaveFormat WaveFormat => _fmt;

        public int Read(byte[] buffer, int offset, int count)
        {
            var frameBytes = _fmt.BlockAlign;
            var frames = Math.Min(count / frameBytes, _framesLeft);
            if (frames <= 0) return 0;

            var step = 2 * Math.PI * Frequency / _fmt.SampleRate;
            for (var f = 0; f < frames; f++)
            {
                var v = (float)(Amplitude * Math.Sin(_phase));
                _phase += step;
                for (var c = 0; c < _fmt.Channels; c++)
                    WriteSample(buffer, offset + f * frameBytes + c * _bytesPerSample, v);
            }

            _framesLeft -= frames;
            return frames * frameBytes;
        }

        /// <summary>按设备格式写单个样本：float32 / PCM16 / PCM24 / PCM32。
        /// 旧实现无条件写 float32，设备混音格式为 PCM 时测试音会变成噪声或长度减半。</summary>
        private void WriteSample(byte[] buffer, int at, float v)
        {
            v = Math.Clamp(v, -1f, 1f);
            if (_isFloat)
            {
                MemoryMarshal.Write(buffer.AsSpan(at, 4), in v);
                return;
            }

            switch (_bytesPerSample)
            {
                case 2:
                {
                    var s = (short)(v * 32767f);
                    buffer[at] = (byte)s;
                    buffer[at + 1] = (byte)(s >> 8);
                    break;
                }
                case 3:
                {
                    var i = (int)(v * 8388607f);
                    buffer[at] = (byte)i;
                    buffer[at + 1] = (byte)(i >> 8);
                    buffer[at + 2] = (byte)(i >> 16);
                    break;
                }
                default:
                {
                    var i = (int)(v * 2147483520f);
                    buffer[at] = (byte)i;
                    buffer[at + 1] = (byte)(i >> 8);
                    buffer[at + 2] = (byte)(i >> 16);
                    buffer[at + 3] = (byte)(i >> 24);
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ Voicemeeter 宿主

    public static bool IsVoicemeeterDevice(string captureName) =>
        captureName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        captureName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public static bool IsVoicemeeterRunning() =>
        Process.GetProcessesByName("voicemeeter").Length > 0 ||
        Process.GetProcessesByName("voicemeeter_x64").Length > 0;

    private static readonly string[] VoicemeeterPaths =
    [
        @"C:\Program Files (x86)\VB\Voicemeeter\voicemeeter_x64.exe",
        @"C:\Program Files (x86)\VB\Voicemeeter\voicemeeter.exe",
        @"C:\Program Files\VB\Voicemeeter\voicemeeter_x64.exe",
        @"C:\Program Files\VB\Voicemeeter\voicemeeter.exe",
    ];

    /// <summary>启动本机已安装的 Voicemeeter（不重新分发，仅执行用户已装的程序）</summary>
    public static bool TryStartVoicemeeter()
    {
        foreach (var path in VoicemeeterPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log.Info($"已启动 Voicemeeter：{path}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"启动 Voicemeeter 失败：{ex.Message}");
            }
        }
        return false;
    }
}
