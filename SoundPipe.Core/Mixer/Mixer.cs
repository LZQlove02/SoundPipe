using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Audio;

/// <summary>
/// 多源混音器（设计 §4.1）：
/// 各源 SPSC 环形缓冲 →（源已统一为 48k/2ch/float）→ 按源增益叠加 → tanh 软限幅 → 写输出环形缓冲。
/// 源为 1.5s 量化块处理：Process 由引擎的混音线程按固定帧数调用，便于单元测试。
/// </summary>
public sealed class Mixer
{
    /// <summary>一次量化的帧数：480 帧 = 10ms @48k。960 个 float。</summary>
    public const int QuantumFrames = 480;
    public const int QuantumSamples = QuantumFrames * EngineFormat.SamplesPerFrame;

    private readonly float[] _accum = new float[QuantumSamples];
    private readonly float[] _tmp = new float[QuantumSamples];
    private SpScRingBuffer[] _outRings;
    private MixerSource[] _sources = Array.Empty<MixerSource>();
    private readonly object _lock = new();

    /// <summary>活跃源判定窗口：最后一次写入在此窗口内视为"仍在供数"，未攒满整块时继续等待。
    /// 取 25ms：覆盖 5–20ms 的常见捕获包周期，又不会为已停止的源无限等待。</summary>
    private const long ActiveSourceWindowMs = 25;

    /// <summary>活跃源一直凑不满整块时的最长等待。超过则强制补零产出，
    /// 防止某源持续以极小包供数导致混音器永不产出（那会让输出环彻底枯竭）。</summary>
    private const long MaxStarveMs = 30;

    /// <summary>本次饥饿开始的时刻（0 = 未处于饥饿状态）</summary>
    private long _starvedSince;

    /// <summary>因等待超时而强制补零产出的块数（诊断：持续增长说明源供数不足，听感会有空洞）</summary>
    public long ForcedSilentBlocks;

    /// <summary>总输出 RMS（混音线程每量化块更新，volatile 供 UI 读取）</summary>
    public volatile float MasterRms;

    /// <summary>输出环写入失败次数（目标环满 = 设备时钟过慢，诊断用）</summary>
    public long OutputOverflowCount;

    /// <param name="outRings">输出环（§5.6 双输出：目标环 + 监听环，各时钟独立）</param>
    public Mixer(params SpScRingBuffer[] outRings)
    {
        if (outRings.Length == 0) throw new ArgumentException("至少需要一个输出环");
        _outRings = outRings;
    }

    /// <summary>线程安全添加源（运行中调用不停流，设计 §5.2）</summary>
    public void AddSource(MixerSource source)
    {
        lock (_lock)
        {
            var copy = new MixerSource[_sources.Length + 1];
            Array.Copy(_sources, copy, _sources.Length);
            copy[^1] = source;
            _sources = copy;
        }
    }

    /// <summary>线程安全移除源</summary>
    public void RemoveSource(MixerSource source)
    {
        lock (_lock)
        {
            _sources = _sources.Where(s => !ReferenceEquals(s, source)).ToArray();
        }
    }

    /// <summary>线程安全移除输出环（监听输出失败后调用，避免混音线程对无人读取的环空转写入）</summary>
    public void RemoveOutputRing(SpScRingBuffer ring)
    {
        lock (_lock)
        {
            var remaining = _outRings.Where(r => !ReferenceEquals(r, ring)).ToArray();
            if (remaining.Length > 0) _outRings = remaining;
        }
    }

    /// <summary>当前源快照（供 UI/诊断）</summary>
    public MixerSource[] SnapshotSources()
    {
        lock (_lock) return _sources;
    }

    /// <summary>
    /// 按需处理一个量化块：只有当所有活跃源都凑满整块时才产出。
    /// 捕获是"每 10ms 突发送一包"，而混音线程产能约 5× 实时——若不等数据齐就取，
    /// 会把半个包读走再补零，音频流被静音空洞切碎（听感：发闷、沙哑），
    /// 且这种欠载不会记日志，极难察觉。返回 false 表示本次未产出，调用方应稍后重试。
    /// </summary>
    public bool TryProcess() => TryProcess(Environment.TickCount64);

    /// <summary>带时间戳的重载：测试用，避免真实睡眠带来的时序抖动。</summary>
    internal bool TryProcess(long now)
    {
        var snapshot = Volatile.Read(ref _sources);

        if (ShouldWaitForData(snapshot, now))
        {
            if (_starvedSince == 0) _starvedSince = now;
            if (now - _starvedSince < MaxStarveMs) return false;

            // 等待上限用尽：源确实供不上，强制补零产出保证输出不断流（并记账）
            var total = Interlocked.Increment(ref ForcedSilentBlocks);
            if (total % 200 == 1)
                Log.Warn($"混音源供数不足，强制补零（累计 {total} 块）");
        }

        _starvedSince = 0;
        ProcessCore(snapshot);
        return true;
    }

    /// <summary>
    /// 强制处理一个量化块（不足部分补零）。测试与"必须持续产出"的场景用；
    /// 引擎主循环应使用 <see cref="TryProcess"/> 以免切碎音频流。
    /// </summary>
    public int Process()
    {
        _starvedSince = 0;
        return ProcessCore(Volatile.Read(ref _sources));
    }

    /// <summary>是否应继续等待：仅当某个源最近仍在写入、但还没攒够整块时才等。
    /// 按"距最后一次写入的时长"判断，而不是按"饥饿持续时长"——后者对 5ms 级别的小包
    /// 会在半个量化块上误判为供数不足，周期性插入半块静音并刷日志。</summary>
    private static bool ShouldWaitForData(MixerSource[] sources, long now)
    {
        foreach (var src in sources)
        {
            if (src.Buffer.Count >= QuantumSamples) continue;
            var lastWrite = src.Buffer.LastWriteTicks;
            if (lastWrite == 0) continue;                            // 从未写入：视为未启动
            if (now - lastWrite < ActiveSourceWindowMs) return true; // 仍在供数：等下一包
        }
        return false;
    }

    /// <summary>
    /// 处理一个量化块：读取各源、按增益叠加、软限幅、写各输出环。
    /// 源欠载部分补零（保证输出时钟持续供数，引擎空转输出静音——设计 §6）。
    /// 返回写入输出环的样本数（取所有环写入的最小值）。
    /// </summary>
    private int ProcessCore(MixerSource[] snapshot)
    {
        Array.Clear(_accum, 0, QuantumSamples);

        foreach (var src in snapshot)
        {
            var n = src.Buffer.Read(_tmp.AsSpan(0, QuantumSamples));
            // 欠载部分 _tmp 保持上帧残留 → 手动补零
            if (n < QuantumSamples) Array.Clear(_tmp, n, QuantumSamples - n);

            if (n > 0)
            {
                var g = src.Gain;
                for (var i = 0; i < n; i++) _accum[i] += _tmp[i] * g;
                src.LastRms = DspMath.Rms(_tmp.AsSpan(0, n)) * MathF.Abs(g); // 增益后电平（F6/F7）
            }
            else
            {
                src.LastRms = 0f;
            }
        }

        // 软限幅 + 计算 master RMS
        double sum = 0;
        for (var i = 0; i < QuantumSamples; i++)
        {
            var v = DspMath.SoftLimit(_accum[i]);
            _accum[i] = v;
            sum += (double)v * v;
        }
        MasterRms = (float)Math.Sqrt(sum / QuantumSamples);

        // 写入所有输出环（双输出各自时钟，独立缓冲——§5.6；监听失败后可能已移除监听环）
        var written = int.MaxValue;
        var rings = Volatile.Read(ref _outRings);
        foreach (var ring in rings)
        {
            var w = ring.Write(_accum);
            if (w < QuantumSamples) Interlocked.Increment(ref OutputOverflowCount);
            written = Math.Min(written, w);
        }
        return written;
    }
}

/// <summary>一个混音源：环形缓冲 + 独立增益 + 最近电平</summary>
public sealed class MixerSource
{
    public string Name { get; }
    public SpScRingBuffer Buffer { get; }
    public volatile float Gain;

    /// <summary>最近一个量化块的 RMS（混音线程写，UI 读）</summary>
    public volatile float LastRms;

    public MixerSource(string name, SpScRingBuffer buffer, float gain = 1f)
    {
        Name = name;
        Buffer = buffer;
        Gain = gain;
    }
}
