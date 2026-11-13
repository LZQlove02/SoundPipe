namespace SoundPipe.Core.Audio;

/// <summary>
/// 单生产者/单消费者（SPSC）无锁 float 环形缓冲。
/// 容量固定、构造时一次预分配，音频路径上读写均零分配（设计 §4.2）。
/// 生产者只写 _head，消费者只写 _tail，对方索引仅以 Volatile 读取。
/// 索引单调递增，用掩码映射到数组下标（容量必须为 2 的幂）。
/// </summary>
public sealed class SpScRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _head; // 生产者游标（已写入的样本总数）
    private long _tail; // 消费者游标（已读走的样本总数）
    private long _lastWriteTicks; // 最近一次写入时刻（TickCount64），用于区分"静音"与"数据未到齐"

    /// <param name="capacityFloats">容量（float 个数），必须为 2 的幂</param>
    public SpScRingBuffer(int capacityFloats)
    {
        if (capacityFloats <= 0 || (capacityFloats & (capacityFloats - 1)) != 0)
            throw new ArgumentException("容量必须为 2 的幂", nameof(capacityFloats));
        _buffer = new float[capacityFloats];
        _mask = capacityFloats - 1;
    }

    /// <summary>当前可读样本数</summary>
    public int Count
    {
        get
        {
            var head = Volatile.Read(ref _head);
            var tail = Volatile.Read(ref _tail);
            return (int)(head - tail);
        }
    }

    /// <summary>当前可写空间（样本数）</summary>
    public int Free
    {
        get
        {
            var head = Volatile.Read(ref _head);
            var tail = Volatile.Read(ref _tail);
            return (int)(_buffer.Length - (head - tail));
        }
    }

    /// <summary>最近一次成功写入的时刻（<see cref="Environment.TickCount64"/>）。
    /// 0 = 从未写入。混音器据此区分"源已静音"和"这一块数据还没到齐"。</summary>
    public long LastWriteTicks => Volatile.Read(ref _lastWriteTicks);

    /// <summary>
    /// 生产者写入。返回实际写入样本数（空间不足时部分写入）。
    /// 仅允许单个生产者线程调用。
    /// </summary>
    public int Write(ReadOnlySpan<float> data)
    {
        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);
        var free = (int)(_buffer.Length - (head - tail));
        var n = Math.Min(free, data.Length);
        if (n <= 0) return 0;

        var pos = (int)(head & _mask);
        var first = Math.Min(n, _buffer.Length - pos);
        data[..first].CopyTo(_buffer.AsSpan(pos));
        data[first..n].CopyTo(_buffer.AsSpan());
        Volatile.Write(ref _head, head + n);
        Volatile.Write(ref _lastWriteTicks, Environment.TickCount64);
        return n;
    }

    /// <summary>
    /// 消费者读取。返回实际读取样本数（数据不足时部分读取）。
    /// 仅允许单个消费者线程调用。
    /// </summary>
    public int Read(Span<float> dest)
    {
        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);
        var count = (int)(head - tail);
        var n = Math.Min(count, dest.Length);
        if (n <= 0) return 0;

        var pos = (int)(tail & _mask);
        var first = Math.Min(n, _buffer.Length - pos);
        _buffer.AsSpan(pos, first).CopyTo(dest[..first]);
        _buffer.AsSpan(0, n - first).CopyTo(dest[first..n]);
        Volatile.Write(ref _tail, tail + n);
        return n;
    }
}
