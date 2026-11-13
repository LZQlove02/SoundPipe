using SoundPipe.Core.Audio;
using Xunit;

namespace SoundPipe.Tests;

public class MixerMultiRingTests
{
    private static void WriteConstant(SpScRingBuffer ring, float value, int samples)
    {
        var buf = new float[samples];
        Array.Fill(buf, value);
        Assert.Equal(samples, ring.Write(buf));
    }

    [Fact]
    public void DualRings_BothReceiveMix()
    {
        var targetRing = new SpScRingBuffer(2048);
        var monitorRing = new SpScRingBuffer(2048);
        var mixer = new Mixer(targetRing, monitorRing);

        var a = new MixerSource("a", new SpScRingBuffer(2048), 1f);
        mixer.AddSource(a);
        WriteConstant(a.Buffer, 0.25f, Mixer.QuantumSamples);
        mixer.Process();

        var dest = new float[Mixer.QuantumSamples];
        Assert.Equal(Mixer.QuantumSamples, targetRing.Read(dest));
        Assert.All(dest, v => Assert.Equal(0.25f, v, 5));

        Assert.Equal(Mixer.QuantumSamples, monitorRing.Read(dest));
        Assert.All(dest, v => Assert.Equal(0.25f, v, 5));
    }

    [Fact]
    public void EmptyRings_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new Mixer());
    }

    [Fact]
    public void FullRing_CountsOverflow()
    {
        var ring = new SpScRingBuffer(1024); // 容量 < 量化块 960×2? 1024 floats：写入 960 后余 64
        var mixer = new Mixer(ring);
        Assert.Equal(0, mixer.OutputOverflowCount);

        // 先填满环，再 Process → 溢出计数
        WriteConstant(ring, 0f, 1024);
        var a = new MixerSource("a", new SpScRingBuffer(2048), 1f);
        mixer.AddSource(a);
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples);
        mixer.Process();

        Assert.True(mixer.OutputOverflowCount > 0, "环满时应记录溢出");
    }
}
