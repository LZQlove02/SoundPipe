using NAudio.Wave;
using SoundPipe.Core.Audio;
using Xunit;

namespace SoundPipe.Tests;

/// <summary>SampleConverter 大包回归：WASAPI 轮询可能攒出远超 20ms 的包（线程被抢占），
/// 曾因固定 20ms 缓冲整包丢弃 → 卡顿。修复后按需扩容，任何包长都不得丢数据。</summary>
public class SampleConverterTests
{
    private static byte[] MakeFloat32Packet(int sampleRate, int channels, int frames, float value)
    {
        var samples = new float[frames * channels];
        Array.Fill(samples, value);
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Theory]
    [InlineData(44100, 2)]   // 重采样 + 大包
    [InlineData(48000, 2)]   // 直通 + 大包
    [InlineData(44100, 1)]   // 单声道 + 重采样
    [InlineData(48000, 6)]   // 6ch 降混 + 大包
    public void LargePacket_Upto100ms_IsConvertedWithoutLoss(int rate, int channels)
    {
        var conv = new SampleConverter();
        conv.Configure(new WaveFormat(rate, 32, channels)); // float32

        // 100ms 大包（捕获缓冲上限）
        var frames = rate / 10;
        var packet = MakeFloat32Packet(rate, channels, frames, 0.5f);

        var ok = conv.TryConvert(packet, out var buffer, out var samples);

        Assert.True(ok, $"大包被丢弃：{rate}Hz/{channels}ch");
        var expectedFrames = rate == 48000 ? frames : (int)(frames * 48000.0 / rate);
        Assert.InRange(samples, (expectedFrames - 8) * 2, (expectedFrames + 8) * 2);

        // 能量保留（降混/重采样后幅度不塌）
        var peak = 0f;
        for (var i = 0; i < samples; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
        Assert.InRange(peak, 0.3f, 0.7f);
    }

    [Fact]
    public void RepeatedLargePackets_StateRemainsConsistent()
    {
        var conv = new SampleConverter();
        conv.Configure(new WaveFormat(44100, 32, 2));

        for (var i = 0; i < 20; i++)
        {
            var packet = MakeFloat32Packet(44100, 2, 4410, i % 2 == 0 ? 0.5f : -0.5f);
            Assert.True(conv.TryConvert(packet, out _, out var samples));
            Assert.InRange(samples, (4760 - 16) * 2, (4800 + 16) * 2);
        }
    }
}
