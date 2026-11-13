using NAudio.Wave;
using SoundPipe.Core;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Output;
using Xunit;

namespace SoundPipe.Tests;

public class DriftCorrectedReaderTests
{
    private static void WriteRange(SpScRingBuffer ring, int count)
    {
        var buf = new float[count];
        for (var i = 0; i < count; i++) buf[i] = i;
        Assert.Equal(count, ring.Write(buf));
    }

    [Fact]
    public void NormalDepth_NoCorrection()
    {
        var ring = new SpScRingBuffer(4096);
        var reader = new DriftCorrectedReader(ring);
        WriteRange(ring, 1000); // 深度在 [960, 2880] 目标带内

        var dest = new float[960];
        var n = reader.Read(dest);
        Assert.Equal(960, n);
        Assert.Equal(0, reader.Skips);
        Assert.Equal(0, reader.Synths);
    }

    [Fact]
    public void DepthTooHigh_SkipsFourWithCrossfade()
    {
        var ring = new SpScRingBuffer(8192);
        var reader = new DriftCorrectedReader(ring);
        WriteRange(ring, 4000); // 4000 > 1920+960 → 触发丢样

        var dest = new float[960];
        var n = reader.Read(dest);

        Assert.Equal(960, n);
        Assert.Equal(1, reader.Skips);
        // 丢样 4 个：环减少 960 + 4
        Assert.Equal(4000 - 960 - 4, ring.Count);
        // crossfade 后输出必须有限且首段被混合
        Assert.All(dest, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void DepthTooLow_SynthesizesFour()
    {
        var ring = new SpScRingBuffer(4096);
        var reader = new DriftCorrectedReader(ring);
        WriteRange(ring, 100); // 100 < 1920-960 → 触发补样

        var dest = new float[200];
        var n = reader.Read(dest);

        // 自然读 100（环里只有 100），合成 4 → 返回 104
        Assert.Equal(104, n);
        Assert.Equal(1, reader.Synths);
        // 合成段为最后帧的过渡（有限值）
        Assert.All(dest.AsSpan(100, 4).ToArray(), v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void ShortDest_NoCorrection()
    {
        var ring = new SpScRingBuffer(8192);
        var reader = new DriftCorrectedReader(ring);
        WriteRange(ring, 4000);

        var dest = new float[4];
        var n = reader.Read(dest);
        Assert.Equal(4, n);
        Assert.Equal(0, reader.Skips);
    }
}

/// <summary>输出格式转换：WASAPI 共享模式必须严格按设备 MixFormat 编码（float32/PCM16/24/32）</summary>
public class OutputWaveProviderTests
{
    private static (byte[] Buffer, int Read) Render(WaveFormat format, float sample = 0.5f)
    {
        var ring = new SpScRingBuffer(8192);
        var data = new float[Mixer.QuantumSamples];
        Array.Fill(data, sample);
        Assert.Equal(data.Length, ring.Write(data));

        var provider = new OutputWriter.OutputWaveProvider(ring, format);
        var buffer = new byte[Mixer.QuantumFrames * format.BlockAlign];
        var read = provider.Read(buffer, 0, buffer.Length);
        return (buffer, read);
    }

    [Fact]
    public void Float32Device_DirectPassthrough()
    {
        var (buffer, read) = Render(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.Equal(buffer.Length, read);
        Assert.Equal(0.5f, BitConverter.ToSingle(buffer, 0), 5);
    }

    [Fact]
    public void Pcm16Device_EncodesInt16()
    {
        var (buffer, read) = Render(new WaveFormat(48000, 16, 2));
        Assert.Equal(buffer.Length, read);
        Assert.Equal(16383, BitConverter.ToInt16(buffer, 0));
    }

    [Fact]
    public void Pcm24Device_Encodes24Bit()
    {
        var fmt = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 48000, 2, 48000 * 6, 6, 24);
        var (buffer, read) = Render(fmt);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(4194303, buffer[0] | buffer[1] << 8 | buffer[2] << 16);
    }

    [Fact]
    public void Pcm32Device_EncodesInt32()
    {
        var fmt = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 48000, 2, 48000 * 8, 8, 32);
        var (buffer, read) = Render(fmt);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(1073741760, BitConverter.ToInt32(buffer, 0));
    }
}

public class RestartPolicyTests
{
    [Fact]
    public void AllowsThreeThenStops()
    {
        var policy = new RestartPolicy();
        Assert.True(policy.ShouldRestart());
        Assert.True(policy.ShouldRestart());
        Assert.True(policy.ShouldRestart());
        Assert.False(policy.ShouldRestart());
        Assert.Equal(4, policy.ConsecutiveFailures);
    }

    [Fact]
    public void Reset_ClearsCounter()
    {
        var policy = new RestartPolicy();
        policy.ShouldRestart();
        policy.ShouldRestart();
        policy.Reset();
        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.True(policy.ShouldRestart());
    }
}
