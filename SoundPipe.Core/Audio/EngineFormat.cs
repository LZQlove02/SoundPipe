using NAudio.Wave;

namespace SoundPipe.Core.Audio;

/// <summary>引擎统一音频格式（设计 §N4）：48kHz / 2ch / IEEE float32</summary>
public static class EngineFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public static readonly WaveFormat Wave = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    /// <summary>每帧样本数（= 通道数）</summary>
    public const int SamplesPerFrame = Channels;
}
