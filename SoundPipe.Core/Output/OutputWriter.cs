using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Output;

/// <summary>
/// 输出写入器（设计 §4.1 Output/DualOutput 的 M1 单输出版）：
/// WASAPI 共享模式 + 事件驱动回调（AUDCLNT_STREAMFLAGS_EVENTCALLBACK，设计 §4.4），
/// 从输出环形缓冲读取混音结果写入目标设备；欠载补零保活不断流（设计 §6）。
/// NAudio 2.2.1 的 WasapiOut 以设备 MixFormat 打开流，因此格式协商在此完成（§5.1）：
/// 设备格式 == 引擎格式 → 直通；否则按需做采样率转换 / 通道映射 / PCM16 编码。
/// </summary>
public sealed class OutputWriter : IDisposable
{
    private readonly SpScRingBuffer _ring;
    private readonly int _latencyMs;
    private WasapiOut? _player;
    private OutputWaveProvider? _provider;

    /// <summary>输出流异常停止（§5.6：目标失败→引擎停止；监听失败→关监听）</summary>
    public event Action<Exception>? PlaybackFailed;

    public OutputWriter(SpScRingBuffer ring, int latencyMs = 20)
    {
        _ring = ring;
        _latencyMs = latencyMs;
    }

    /// <summary>在目标设备上开始播放混音流</summary>
    public void Start(MMDevice targetDevice)
    {
        // 读取设备混合格式（引擎格式 → 设备格式的转换在 Provider 内完成）
        WaveFormat deviceFormat;
        using (var ac = targetDevice.AudioClient)
        {
            deviceFormat = ac.MixFormat;
        }

        _provider = new OutputWaveProvider(_ring, deviceFormat);
        _player = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, _latencyMs);
        _player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                Log.Error("输出流异常停止", e.Exception);
                PlaybackFailed?.Invoke(e.Exception);
            }
        };
        _player.Init(_provider);
        _player.Play();
        Log.Info($"输出启动：{targetDevice.FriendlyName}，latency={_latencyMs}ms，设备格式={deviceFormat}");
    }

    public void Stop()
    {
        if (_player is null && _provider is null) return; // 防止 Dispose 重复停止
        try
        {
            _player?.Stop();
        }
        catch (Exception ex)
        {
            // 设备已被移除/失效时 Stop 可能抛异常：忽略，继续释放（引擎停止路径不允许被单个设备阻断）
            Log.Warn($"输出停止异常（已忽略）：{ex.Message}");
        }
        try { _player?.Dispose(); } catch { }
        _player = null;
        _provider = null;
        Log.Info("输出已停止");
    }

    public void Dispose() => Stop();

    /// <summary>IWaveProvider：从环形缓冲读（引擎 48k/2ch/float），转换为设备格式，欠载补零。</summary>
    internal sealed class OutputWaveProvider : IWaveProvider
    {
        /// <summary>设备 MixFormat 的样本编码（WASAPI 共享模式必须严格按设备格式输出）</summary>
        private enum SampleFormat { Float32, Pcm16, Pcm24, Pcm32 }

        private readonly DriftCorrectedReader _reader;
        private readonly WaveFormat _deviceFormat;
        private readonly bool _direct;
        private readonly SampleFormat _sampleFormat;
        private readonly int _bytesPerSample;
        private readonly int _devChannels;
        private readonly int _devRate;
        private readonly WdlResampler? _resampler;
        private float[] _conv = new float[9600]; // 转换中间缓冲（帧 × 2ch），大请求时按需扩容
        private float[] _resampleIn = Array.Empty<float>();
        public long Underruns;

        public OutputWaveProvider(SpScRingBuffer ring, WaveFormat deviceFormat)
        {
            _reader = new DriftCorrectedReader(ring);
            _deviceFormat = deviceFormat;
            _devChannels = deviceFormat.Channels;
            _devRate = deviceFormat.SampleRate;

            // 按 BlockAlign/通道数判断实际容器宽度：可覆盖 PCM16、24-bit 打包、32-bit，
            // 以及 24-in-32 等常见布局；不能只看 Encoding 就假定是 float32。
            _bytesPerSample = Math.Max(1, deviceFormat.BlockAlign / Math.Max(1, _devChannels));
            var isFloat = deviceFormat.Encoding == WaveFormatEncoding.IeeeFloat && _bytesPerSample == 4;
            _sampleFormat = isFloat
                ? SampleFormat.Float32
                : _bytesPerSample switch
                {
                    2 => SampleFormat.Pcm16,
                    3 => SampleFormat.Pcm24,
                    4 => SampleFormat.Pcm32,
                    _ => SampleFormat.Float32
                };
            if (!isFloat && _sampleFormat == SampleFormat.Float32)
                Log.Warn($"输出设备格式未识别，按 float32 处理：{deviceFormat}");

            _direct = _sampleFormat == SampleFormat.Float32
                      && _devChannels == EngineFormat.Channels
                      && _devRate == EngineFormat.SampleRate;

            if (!_direct && _devRate != EngineFormat.SampleRate)
            {
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false);
                _resampler.SetFilterParms();
                _resampler.SetFeedMode(false);
                _resampler.SetRates(EngineFormat.SampleRate, _devRate);
            }
        }

        public WaveFormat WaveFormat => _deviceFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / _deviceFormat.BlockAlign;
            if (frames <= 0) return 0;

            var produced = _direct ? ReadDirect(frames, buffer, offset) : ReadConverted(frames, buffer, offset);

            if (produced < frames)
            {
                // 欠载补零（按设备帧大小）
                var blockAlign = _deviceFormat.BlockAlign;
                var start = offset + produced * blockAlign;
                Array.Clear(buffer, start, (frames - produced) * blockAlign);
                if (Interlocked.Increment(ref Underruns) % 200 == 1)
                    Log.Warn($"输出欠载，已补零（计数 {Underruns}）");
            }
            return frames * _deviceFormat.BlockAlign;
        }

        /// <summary>直通：设备格式 == 引擎格式（48k/2ch/float32）</summary>
        private int ReadDirect(int frames, byte[] buffer, int offset)
        {
            var floats = frames * 2;
            var span = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, floats * sizeof(float)));
            return _reader.Read(span) / 2;
        }

        /// <summary>转换：采样率重采样 + 通道映射 + 编码为该设备 MixFormat</summary>
        private int ReadConverted(int frames, byte[] buffer, int offset)
        {
            // 高采样率设备 + 大缓冲时单次请求可能超过预分配，按需扩容（仅首次/极少数请求分配）
            if (_conv.Length < frames * 2) _conv = new float[frames * 2];

            int producedFrames;
            if (_resampler is not null)
            {
                var inNeeded = _resampler.ResamplePrepare(frames, 2, out var inBuf, out var inOff);
                if (_resampleIn.Length < inNeeded * 2) _resampleIn = new float[inNeeded * 2];
                var inRead = _reader.Read(_resampleIn.AsSpan(0, inNeeded * 2));
                Array.Copy(_resampleIn, 0, inBuf, inOff, inRead);
                producedFrames = _resampler.ResampleOut(_conv, 0, inRead / 2, frames, 2);
            }
            else
            {
                producedFrames = _reader.Read(_conv.AsSpan(0, frames * 2)) / 2;
            }

            var bytesPerFrame = _devChannels * _bytesPerSample;
            var dst = buffer.AsSpan(offset, producedFrames * bytesPerFrame);
            switch (_sampleFormat)
            {
                case SampleFormat.Pcm16:
                {
                    var shorts = MemoryMarshal.Cast<byte, short>(dst);
                    for (var f = 0; f < producedFrames; f++)
                    {
                        for (var c = 0; c < _devChannels; c++)
                            shorts[f * _devChannels + c] =
                                (short)(Math.Clamp(MapChannel(_conv, f, c), -1f, 1f) * 32767f);
                    }
                    break;
                }
                case SampleFormat.Pcm24:
                {
                    for (var f = 0; f < producedFrames; f++)
                    {
                        for (var c = 0; c < _devChannels; c++)
                        {
                            var v = (int)(Math.Clamp(MapChannel(_conv, f, c), -1f, 1f) * 8388607f);
                            var at = offset + f * bytesPerFrame + c * 3;
                            buffer[at] = (byte)v;
                            buffer[at + 1] = (byte)(v >> 8);
                            buffer[at + 2] = (byte)(v >> 16);
                        }
                    }
                    break;
                }
                case SampleFormat.Pcm32:
                {
                    var ints = MemoryMarshal.Cast<byte, int>(dst);
                    for (var f = 0; f < producedFrames; f++)
                    {
                        for (var c = 0; c < _devChannels; c++)
                            ints[f * _devChannels + c] =
                                (int)(Math.Clamp(MapChannel(_conv, f, c), -1f, 1f) * 2147483520f);
                    }
                    break;
                }
                default:
                {
                    var floats = MemoryMarshal.Cast<byte, float>(dst);
                    for (var f = 0; f < producedFrames; f++)
                    {
                        for (var c = 0; c < _devChannels; c++)
                            floats[f * _devChannels + c] = MapChannel(_conv, f, c);
                    }
                    break;
                }
            }
            return producedFrames;
        }

        /// <summary>通道映射：设备 1ch 取两声道平均，2ch 直通，>2ch 前两通道其余置 0</summary>
        private float MapChannel(float[] conv, int frame, int channel)
        {
            if (_devChannels == 1) return (conv[frame * 2] + conv[frame * 2 + 1]) * 0.5f;
            if (channel == 0) return conv[frame * 2];
            if (channel == 1) return conv[frame * 2 + 1];
            return 0f;
        }
    }
}
