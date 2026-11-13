using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Audio;

/// <summary>
/// 采样转换器（设计 §5.1 格式协商链的可复用核心）：
/// 解码（float32/PCM16/PCM32）→ 通道降混（&gt;2ch 平均对合并）→ 采样率重采样（WDL）→ 48k/2ch/float。
/// Configure 时预分配全部缓冲，Convert 在音频回调内零分配（设计 §4.2/N5）。
/// 整系统循环捕获与进程循环捕获共用（进程捕获协商失败回退 MixFormat 时使用）。
/// </summary>
public sealed class SampleConverter
{
    private enum DecodeMode { Float32, Pcm16, Pcm32 }

    private int _inChannels;
    private int _inRate;
    private int _inBlockAlign;
    private DecodeMode _decode;

    private float[] _decoded = Array.Empty<float>();   // 解码后（原始通道/原始率）
    private float[] _stereo = Array.Empty<float>();    // 降混后（2ch/原始率）
    private float[] _engine = Array.Empty<float>();    // 最终（2ch/48k）
    private WdlResampler? _resampler;

    /// <summary>输入是否已是引擎格式（无需任何转换）</summary>
    public bool IsPassthrough { get; private set; }

    /// <summary>按源格式配置（预分配缓冲）。sourceFormat 为设备 MixFormat。</summary>
    public void Configure(WaveFormat sourceFormat)
    {
        _inChannels = sourceFormat.Channels;
        _inRate = sourceFormat.SampleRate;
        _inBlockAlign = sourceFormat.BlockAlign;
        _decode = sourceFormat.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => DecodeMode.Float32,
            WaveFormatEncoding.Pcm => sourceFormat.BitsPerSample == 16 ? DecodeMode.Pcm16 : DecodeMode.Pcm32,
            _ => DecodeMode.Float32
        };

        // 按 20ms 输入量预分配（大包由 TryConvert 按需扩容），留 2 倍余量
        var inFramesAt20ms = Math.Max(1, _inRate / 50);
        _decoded = new float[inFramesAt20ms * _inChannels * 2];
        _stereo = new float[inFramesAt20ms * 2 * 2];

        _resampler = null;
        if (_inRate != EngineFormat.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(false);
            _resampler.SetRates(_inRate, EngineFormat.SampleRate);
            _engine = new float[inFramesAt20ms * 2 * 2];
        }
        else
        {
            _engine = _stereo;
        }

        IsPassthrough = _inChannels == EngineFormat.Channels
                        && _inRate == EngineFormat.SampleRate
                        && _decode == DecodeMode.Float32;
    }

    /// <summary>
    /// 转换一段原始字节到引擎格式。返回最终缓冲（内部复用，勿持有），
    /// samples 为有效 float 样本数（帧数 × 2）。
    /// 缓冲按需扩容：WASAPI 轮询可能攒出远超 20ms 的大包（线程被抢占），
    /// 固定容量丢包会造成周期性卡顿。捕获线程单线程调用，扩容安全。
    /// </summary>
    public bool TryConvert(ReadOnlySpan<byte> raw, out float[] buffer, out int samples)
    {
        buffer = Array.Empty<float>();
        samples = 0;

        var bytes = raw.Length;
        bytes -= bytes % _inBlockAlign;
        if (bytes <= 0) return false;

        var inFrames = bytes / _inBlockAlign;
        var inSamples = inFrames * _inChannels;
        if (_decoded.Length < inSamples) _decoded = new float[inSamples];

        // 1) 解码
        switch (_decode)
        {
            case DecodeMode.Float32:
                MemoryMarshal.Cast<byte, float>(raw[..bytes]).CopyTo(_decoded);
                break;
            case DecodeMode.Pcm16:
            {
                var src = MemoryMarshal.Cast<byte, short>(raw[..bytes]);
                for (var i = 0; i < inSamples; i++) _decoded[i] = src[i] / 32768f;
                break;
            }
            case DecodeMode.Pcm32:
            {
                var src = MemoryMarshal.Cast<byte, int>(raw[..bytes]);
                for (var i = 0; i < inSamples; i++) _decoded[i] = src[i] / 2147483648f;
                break;
            }
            default:
                return false;
        }

        // 2) 降混到 2ch
        var stereoFrames = inFrames;
        if (_stereo.Length < inFrames * 2) _stereo = new float[inFrames * 2];
        if (_inChannels == 2)
        {
            _decoded.AsSpan(0, inSamples).CopyTo(_stereo);
        }
        else
        {
            stereoFrames = Downmix(_decoded, inFrames, _inChannels, _stereo);
        }

        // 3) 重采样（输出缓冲同样按需扩容）
        if (_resampler is not null)
        {
            var outFramesWanted = Math.Max(1, stereoFrames * EngineFormat.SampleRate / _inRate) + 4;
            if (_engine.Length < outFramesWanted * 2) _engine = new float[outFramesWanted * 2];
            var outFrames = Resample(_stereo, stereoFrames, _engine);
            buffer = _engine;
            samples = outFrames * 2;
            return true;
        }

        buffer = _stereo;
        samples = stereoFrames * 2;
        return true;
    }

    /// <summary>&gt;2ch 平均对降混（§5.1）；1ch 复制到双声道，返回输出帧数</summary>
    public static int Downmix(float[] input, int inFrames, int channels, float[] output)
    {
        var pairs = channels / 2;
        if (pairs == 0)
        {
            // 单声道：复制到两声道（避免 0 对相除产生 NaN 污染整条混音链）
            for (var f = 0; f < inFrames; f++)
            {
                output[f * 2] = input[f];
                output[f * 2 + 1] = input[f];
            }
            return inFrames;
        }
        for (var f = 0; f < inFrames; f++)
        {
            var baseIdx = f * channels;
            for (var c = 0; c < 2; c++)
            {
                float sum = 0;
                for (var p = 0; p < pairs; p++) sum += input[baseIdx + c + p * 2];
                output[f * 2 + c] = sum / pairs;
            }
        }
        return inFrames;
    }

    /// <summary>WDL 重采样，返回输出帧数</summary>
    private int Resample(float[] input, int inFrames, float[] output)
    {
        var r = _resampler!;
        var outFramesWanted = Math.Max(1, inFrames * EngineFormat.SampleRate / _inRate) + 4;
        if (output.Length < outFramesWanted * 2) return 0;

        var inNeeded = r.ResamplePrepare(outFramesWanted, 2, out var inBuffer, out var inBufferOffset);
        var useFrames = Math.Min(inNeeded, inFrames);
        Array.Copy(input, 0, inBuffer, inBufferOffset, useFrames * 2);
        return r.ResampleOut(output, 0, useFrames, outFramesWanted, 2);
    }
}
