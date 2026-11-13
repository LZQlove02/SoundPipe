using SoundPipe.Core.Audio;
using Xunit;

namespace SoundPipe.Tests;

public class SpScRingBufferTests
{
    [Fact]
    public void WriteThenRead_RoundTrip()
    {
        var ring = new SpScRingBuffer(1024);
        var data = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        var dest = new float[100];

        Assert.Equal(100, ring.Write(data));
        Assert.Equal(100, ring.Count);
        Assert.Equal(100, ring.Read(dest));
        Assert.Equal(data, dest);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void WrapAround_KeepsDataIntact()
    {
        var ring = new SpScRingBuffer(256);
        var dest = new float[64];

        // 反复写读 64 个样本，越过掩码回绕点多次
        for (var round = 0; round < 20; round++)
        {
            var data = Enumerable.Range(0, 64).Select(i => (float)(round * 64 + i)).ToArray();
            Assert.Equal(64, ring.Write(data));
            Assert.Equal(64, ring.Read(dest));
            Assert.Equal(data, dest);
        }
    }

    [Fact]
    public void PartialWrite_WhenFull()
    {
        var ring = new SpScRingBuffer(128);
        var data = new float[200];
        Assert.Equal(128, ring.Write(data)); // 只写进 128
        Assert.Equal(0, ring.Free);
        Assert.Equal(0, ring.Write(new float[10])); // 满则写 0
    }

    [Fact]
    public void InvalidCapacity_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new SpScRingBuffer(100)); // 非 2 的幂
        Assert.Throws<ArgumentException>(() => new SpScRingBuffer(0));
    }

    [Fact]
    public async Task ConcurrentSingleProducerSingleConsumer_NoLoss()
    {
        var ring = new SpScRingBuffer(4096);
        const int total = 1_000_000;
        long consumed = 0;
        long checksum = 0;
        const long expected = (long)total * (total - 1) / 2;

        var consumer = Task.Run(async () =>
        {
            var buf = new float[960];
            while (Interlocked.Read(ref consumed) < total)
            {
                var n = ring.Read(buf);
                for (var i = 0; i < n; i++)
                {
                    Interlocked.Add(ref checksum, (long)buf[i]);
                    Interlocked.Increment(ref consumed);
                }
                if (n == 0) await Task.Yield();
            }
        });

        var producer = Task.Run(() =>
        {
            var buf = new float[960];
            var value = 0;
            while (value < total)
            {
                var n = Math.Min(960, total - value);
                for (var i = 0; i < n; i++) buf[i] = value + i;
                value += ring.Write(buf.AsSpan(0, n));
            }
        });

        await producer;
        await consumer;

        Assert.Equal(expected, Interlocked.Read(ref checksum));
    }
}
