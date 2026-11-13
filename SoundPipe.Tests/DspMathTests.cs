using SoundPipe.Core.Audio;
using Xunit;

namespace SoundPipe.Tests;

public class DspMathTests
{
    [Fact]
    public void SoftLimit_TransparentBelowThreshold()
    {
        // 阈值以下应完全直通（透明）
        Assert.Equal(0.0f, DspMath.SoftLimit(0.0f));
        Assert.Equal(0.5f, DspMath.SoftLimit(0.5f));
        Assert.Equal(-0.89f, DspMath.SoftLimit(-0.89f), 2);
        Assert.Equal(0.89f, DspMath.SoftLimit(0.89f), 2);
    }

    [Fact]
    public void SoftLimit_OutputStaysBelowOne()
    {
        // 极端输入不得削到 >= 1
        Assert.True(DspMath.SoftLimit(10f) < 1f);
        Assert.True(DspMath.SoftLimit(-10f) > -1f);
        Assert.True(DspMath.SoftLimit(1000f) < 1f);
    }

    [Fact]
    public void SoftLimit_SymmetricAndMonotonic()
    {
        var prev = -2f;
        for (var x = -2f; x <= 2f; x += 0.01f)
        {
            var y = DspMath.SoftLimit(x);
            Assert.True(y >= prev - 1e-6f, "输出应单调不减");
            Assert.Equal(DspMath.SoftLimit(-x), -y, 5); // 奇对称
            prev = y;
        }
    }

    [Fact]
    public void Rms_ConstantSignal()
    {
        var data = new float[480];
        Array.Fill(data, 0.5f);
        Assert.Equal(0.5f, DspMath.Rms(data), 4);
        Assert.Equal(0f, DspMath.Rms(Array.Empty<float>()));
    }

    [Fact]
    public void ToDb_ClampedAndCorrect()
    {
        Assert.Equal(0f, DspMath.ToDb(1f));
        Assert.Equal(-6.02f, DspMath.ToDb(0.5f), 2);
        Assert.Equal(-60f, DspMath.ToDb(0f));
        Assert.Equal(-60f, DspMath.ToDb(1e-9f));
    }
}
