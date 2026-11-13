using SoundPipe.Core.Audio;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Output;

/// <summary>
/// 时钟漂移修正读取器（设计 §5.6）：
/// 包装 SPSC 输出环，在引擎格式域上做深度自适应——
/// 环过深（下游设备时钟过慢）→ 丢弃 4 样本并与真实流做 crossfade 淡入（避免 click/pop）；
/// 环过浅（设备时钟过快）→ 用最后一帧合成 4 样本补齐。
/// 深度目标约 2×10ms 输出周期，抖动吸收余量约 1×周期。
/// </summary>
public sealed class DriftCorrectedReader
{
    private const int TargetDepth = 1920; // 2×10ms（960 floats/周期）
    // 修正触发带宽 15ms：混音线程把环稳定在 10–20ms（见 AudioEngine.MixerLoop），
    // 原 10ms 带宽会让 10ms 深度的正常工作点紧贴补样门限，线程稍有抖动就误补样。
    // 放宽后环深 5–35ms 均不修正，覆盖整个工作区间。
    private const int SlackDepth = 1440;
    private const int CorrectFloats = 4;  // §5.6：2-4 样本 crossfade

    private readonly SpScRingBuffer _ring;
    private readonly float[] _skipBuf = new float[CorrectFloats];
    private int _skipLogs;
    private int _synthLogs;

    /// <summary>累计丢样次数（诊断）</summary>
    public long Skips;
    /// <summary>累计补样次数（诊断）</summary>
    public long Synths;

    public DriftCorrectedReader(SpScRingBuffer ring) => _ring = ring;

    /// <summary>
    /// 读取（最多 dest.Length 个 float），内部按深度做丢样/补样修正。
    /// 返回实际填充的样本数。
    /// </summary>
    public int Read(Span<float> dest)
    {
        if (dest.Length < CorrectFloats * 2)
            return _ring.Read(dest); // 太短不做修正

        var depth = _ring.Count;
        var skipped = false;

        // 过深：先弹出 CorrectFloats 个样本（它们在时间上先于后续读取的数据）
        if (depth > TargetDepth + SlackDepth && _ring.Read(_skipBuf) == CorrectFloats)
        {
            skipped = true;
            Interlocked.Increment(ref Skips);
        }

        // 过浅：少读 CorrectFloats 个真实样本，尾部合成补齐
        var synth = depth < TargetDepth - SlackDepth;
        var natural = synth ? dest.Length - CorrectFloats : dest.Length;

        var n = _ring.Read(dest[..natural]);

        // crossfade：把被跳过的 4 样本淡出到真实数据开头
        if (skipped && n >= CorrectFloats)
        {
            for (var i = 0; i < CorrectFloats; i++)
            {
                var t = (i + 1) / (CorrectFloats + 1f);
                dest[i] = _skipBuf[i] * (1f - t) + dest[i] * t;
            }
            if (Interlocked.Increment(ref _skipLogs) % 500 == 1)
                Log.Info($"输出漂移修正：丢样（第 {_skipLogs} 次）");
        }

        // 合成尾部：倒数第二帧→最后一帧 两步过渡后保持
        if (synth && n >= 4)
        {
            var slf0 = dest[n - 4];
            var slf1 = dest[n - 3];
            var lf0 = dest[n - 2];
            var lf1 = dest[n - 1];
            dest[n] = (slf0 + lf0) * 0.5f;
            dest[n + 1] = (slf1 + lf1) * 0.5f;
            dest[n + 2] = lf0;
            dest[n + 3] = lf1;
            n += CorrectFloats;
            Interlocked.Increment(ref Synths);
            if (Interlocked.Increment(ref _synthLogs) % 500 == 1)
                Log.Info($"输出漂移修正：补样（第 {_synthLogs} 次）");
        }

        return n;
    }
}
