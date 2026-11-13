using System.Numerics;

namespace SoundPipe.Core.Audio;

/// <summary>
/// 纯 DSP 数学函数（可单元测试）。设计 §5.1：增益→叠加→tanh 软限幅→RMS。
/// </summary>
public static class DspMath
{
    /// <summary>软限幅阈值：-1dBFS ≈ 0.8913</summary>
    public const float LimitThreshold = 0.8912509f;

    /// <summary>
    /// tanh 软限幅（零延迟，设计 §5.1 / 附录 B）：
    /// |x| ≤ T（-1dBFS）时线性直通，透明无损；
    /// |x| &gt; T 时进入 tanh 软压区，输出始终 &lt; 1，防硬削波。
    /// </summary>
    public static float SoftLimit(float x)
    {
        var ax = Math.Abs(x);
        if (ax <= LimitThreshold) return x;
        var t = LimitThreshold + (1f - LimitThreshold) * MathF.Tanh((ax - LimitThreshold) / (1f - LimitThreshold));
        // 浮点钳制：极端输入下 1-T 与 tanh≈1 的乘加可能恰好舍入到 1.0f，必须保证输出 < 1
        return Math.Sign(x) * MathF.Min(t, 0.999f);
    }

    /// <summary>RMS 电平（0~1+）。空段返回 0。</summary>
    public static float Rms(ReadOnlySpan<float> data)
    {
        if (data.Length == 0) return 0f;
        double sum = 0;
        for (var i = 0; i < data.Length; i++) sum += (double)data[i] * data[i];
        return (float)Math.Sqrt(sum / data.Length);
    }

    /// <summary>dBFS 换算（用于 UI 显示，-60dB 下限截断）</summary>
    public static float ToDb(float rms)
    {
        if (rms <= 0.0001f) return -60f; // 1e-4 ≈ -80dB，视为静音
        var db = 20f * MathF.Log10(rms);
        return Math.Max(-60f, Math.Min(0f, db));
    }
}
