using SoundPipe.Core.Audio;
using Xunit;

namespace SoundPipe.Tests;

public class MixerTests
{
    private static MixerSource MakeSource(SpScRingBuffer ring, string name = "s") =>
        new(name, ring);

    private static void WriteConstant(SpScRingBuffer ring, float value, int samples)
    {
        var buf = new float[samples];
        Array.Fill(buf, value);
        Assert.Equal(samples, ring.Write(buf));
    }

    [Fact]
    public void TwoSources_SumWithGains()
    {
        var outRing = new SpScRingBuffer(1024);
        var mixer = new Mixer(outRing);

        var a = MakeSource(new SpScRingBuffer(1024), "a");
        var b = MakeSource(new SpScRingBuffer(1024), "b");
        a.Gain = 0.5f;
        b.Gain = 0.25f;
        mixer.AddSource(a);
        mixer.AddSource(b);

        WriteConstant(a.Buffer, 1f, Mixer.QuantumSamples);
        WriteConstant(b.Buffer, 1f, Mixer.QuantumSamples);

        var written = mixer.Process();
        Assert.Equal(Mixer.QuantumSamples, written);

        var dest = new float[Mixer.QuantumSamples];
        Assert.Equal(Mixer.QuantumSamples, outRing.Read(dest));
        Assert.All(dest, v => Assert.Equal(0.75f, v, 5));
    }

    [Fact]
    public void Underrun_SourceContributesSilence()
    {
        var outRing = new SpScRingBuffer(1024);
        var mixer = new Mixer(outRing);

        var a = MakeSource(new SpScRingBuffer(1024)); // 不写入 → 欠载
        mixer.AddSource(a);

        mixer.Process();

        var dest = new float[Mixer.QuantumSamples];
        outRing.Read(dest);
        Assert.All(dest, v => Assert.Equal(0f, v));
        Assert.Equal(0f, a.LastRms);
    }

    [Fact]
    public void SoftLimit_EngagedOnSum()
    {
        var outRing = new SpScRingBuffer(1024);
        var mixer = new Mixer(outRing);

        var a = MakeSource(new SpScRingBuffer(1024), "a");
        var b = MakeSource(new SpScRingBuffer(1024), "b");
        mixer.AddSource(a);
        mixer.AddSource(b);

        // 1 + 1 = 2 → 必须被软限幅压回 < 1
        WriteConstant(a.Buffer, 1f, Mixer.QuantumSamples);
        WriteConstant(b.Buffer, 1f, Mixer.QuantumSamples);

        mixer.Process();

        var dest = new float[Mixer.QuantumSamples];
        outRing.Read(dest);
        Assert.All(dest, v => Assert.True(v is >= 0.9f and < 1f, $"限幅输出应在 [0.9,1)：实际 {v}"));
    }

    [Fact]
    public void RemoveSource_StopsContribution()
    {
        var outRing = new SpScRingBuffer(1024);
        var mixer = new Mixer(outRing);

        var a = MakeSource(new SpScRingBuffer(1024), "a");
        mixer.AddSource(a);
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples);
        mixer.Process();

        // 丢弃移除前的第一个量化块
        var first = new float[Mixer.QuantumSamples];
        Assert.Equal(Mixer.QuantumSamples, outRing.Read(first));

        mixer.RemoveSource(a);
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples);
        mixer.Process();

        var dest = new float[Mixer.QuantumSamples];
        outRing.Read(dest);
        Assert.All(dest, v => Assert.Equal(0f, v));
        Assert.Empty(mixer.SnapshotSources());
    }

    [Fact]
    public async Task AddRemove_ConcurrentWithProcess_DoesNotThrow()
    {
        var outRing = new SpScRingBuffer(4096);
        var mixer = new Mixer(outRing);

        var stop = new CancellationTokenSource();
        var processTask = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) mixer.Process();
        });

        // 并发增删 2000 次，同时写入数据
        for (var i = 0; i < 2000; i++)
        {
            var s = MakeSource(new SpScRingBuffer(2048), $"s{i}");
            mixer.AddSource(s);
            WriteConstant(s.Buffer, 0.1f, Mixer.QuantumSamples);
            mixer.RemoveSource(s);
        }

        stop.Cancel();
        await processTask; // 无异常即通过
    }

    [Fact]
    public void PerSourceRms_ReflectsInput()
    {
        var outRing = new SpScRingBuffer(1024);
        var mixer = new Mixer(outRing);

        var a = MakeSource(new SpScRingBuffer(1024), "a");
        a.Gain = 0.5f;
        mixer.AddSource(a);
        WriteConstant(a.Buffer, 0.4f, Mixer.QuantumSamples);
        mixer.Process();

        Assert.Equal(0.2f, a.LastRms, 4); // 增益后 RMS
    }

    [Fact]
    public void GainChange_MidStream_TakesEffectNextQuantum()
    {
        // 验证源增益语义：Process 循环进行中修改 MixerSource.Gain，下一个量化块立即应用新值
        var outRing = new SpScRingBuffer(1024);
        var mixer = new Mixer(outRing);
        var a = MakeSource(new SpScRingBuffer(1024), "a");
        mixer.AddSource(a);

        // 块1：gain=1，输入 0.5 → 输出 0.5
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples);
        mixer.Process();
        var block1 = new float[Mixer.QuantumSamples];
        outRing.Read(block1);
        Assert.All(block1, v => Assert.Equal(0.5f, v, 5));

        // 运行中改 gain=0.5（运行期调音）
        a.Gain = 0.5f;

        // 块2：同样输入 0.5，新 gain → 输出 0.25
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples);
        mixer.Process();
        var block2 = new float[Mixer.QuantumSamples];
        outRing.Read(block2);
        Assert.All(block2, v => Assert.Equal(0.25f, v, 5));
    }

    // ---------------------------------------------------------------- TryProcess（防切碎）

    [Fact]
    public void TryProcess_ActiveSourceIncomplete_DefersInsteadOfPadding()
    {
        // 活跃源只送到半块（真实场景：捕获包还在路上）→ 必须等待，
        // 而不是读走半块再补零（那会把音频流切出静音空洞）。
        var outRing = new SpScRingBuffer(4096);
        var mixer = new Mixer(outRing);
        var a = MakeSource(new SpScRingBuffer(4096), "a");
        mixer.AddSource(a);
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples / 2);

        Assert.False(mixer.TryProcess());
        Assert.Equal(0, outRing.Count);          // 未产出任何样本
        Assert.Equal(0, mixer.ForcedSilentBlocks);
    }

    [Fact]
    public void TryProcess_IdleSource_ProducesSilenceWithoutWaiting()
    {
        // 从未写入的源（未启动/已停止）不参与等待，直接产出静音，避免混音器卡死
        var outRing = new SpScRingBuffer(4096);
        var mixer = new Mixer(outRing);
        var a = MakeSource(new SpScRingBuffer(4096), "a");
        mixer.AddSource(a);

        Assert.True(mixer.TryProcess());

        var dest = new float[Mixer.QuantumSamples];
        Assert.Equal(Mixer.QuantumSamples, outRing.Read(dest));
        Assert.All(dest, v => Assert.Equal(0f, v));
        Assert.Equal(0, mixer.ForcedSilentBlocks);
    }

    [Fact]
    public void TryProcess_StallBeyondGrace_ForcesPaddedBlock()
    {
        // 活跃源持续供不上（时钟偏慢/已停止）→ 超过等待上限必须强制产出，否则输出环会彻底枯竭
        var outRing = new SpScRingBuffer(4096);
        var mixer = new Mixer(outRing);
        var a = MakeSource(new SpScRingBuffer(4096), "a");
        mixer.AddSource(a);
        WriteConstant(a.Buffer, 0.5f, 100); // 远不足一整块

        var now = Environment.TickCount64;
        Assert.False(mixer.TryProcess(now));      // 第一次：源新鲜但不足 → 等待
        Assert.False(mixer.TryProcess(now + 10)); // 仍在活跃窗口内 → 继续等
        Assert.True(mixer.TryProcess(now + 40));  // 源 40ms 未写入 → 视为停滞，补零产出

        // 停滞源按静音处理，不计入"强制补零"——该计数只用于"源仍在供数但持续凑不满"的诊断
        Assert.Equal(0, mixer.ForcedSilentBlocks);
        var dest = new float[Mixer.QuantumSamples];
        Assert.Equal(Mixer.QuantumSamples, outRing.Read(dest));
        Assert.Equal(0.5f, dest[0], 5);
        Assert.All(dest.AsSpan(100).ToArray(), v => Assert.Equal(0f, v)); // 补零段
    }

    [Fact]
    public void TryProcess_SmallPackets_DoNotForcePaddingWithinGrace()
    {
        // 回归：5ms 级别的小包（半块）在旧实现里会在 8ms 宽限期到期时被强制补零；
        // 现在按"距最后一次写入的时长"判断，第二包到达前必须继续等待。
        var outRing = new SpScRingBuffer(4096);
        var mixer = new Mixer(outRing);
        var a = MakeSource(new SpScRingBuffer(4096), "a");
        mixer.AddSource(a);

        // 先取时间戳再写入：写入时刻只会 >= now，避免机器负载高时把"写后取时"也算进
        // "距最后一次写入"，导致年龄超过 25ms 窗口而偶发失败。
        var now = Environment.TickCount64;
        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples / 2);
        Assert.False(mixer.TryProcess(now));     // 半块 → 等待
        Assert.False(mixer.TryProcess(now + 9)); // 超过旧的 8ms 阈值，但源仍新鲜 → 继续等
        Assert.Equal(0, mixer.ForcedSilentBlocks);
        Assert.Equal(0, outRing.Count);

        WriteConstant(a.Buffer, 0.5f, Mixer.QuantumSamples / 2);
        Assert.True(mixer.TryProcess(now + 10)); // 第二包到达 → 正常产出
        Assert.Equal(0, mixer.ForcedSilentBlocks);
        Assert.Equal(Mixer.QuantumSamples, outRing.Count);
    }
}
