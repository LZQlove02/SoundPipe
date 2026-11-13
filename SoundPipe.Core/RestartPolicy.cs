using SoundPipe.Core.Logging;

namespace SoundPipe.Core;

/// <summary>
/// 流异常重启策略（设计 §5.6）：出错后延迟 1s 重启，连续失败 3 次 → 停止该源/输出并提示。
/// 独立类便于单元测试。
/// </summary>
public sealed class RestartPolicy
{
    public const int MaxConsecutiveFailures = 3;
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    private int _consecutive;

    /// <summary>最近连续失败次数</summary>
    public int ConsecutiveFailures => _consecutive;

    /// <summary>记录一次成功（流正常启动/运行），重置计数</summary>
    public void Reset() => _consecutive = 0;

    /// <summary>
    /// 记录一次失败，返回是否允许重启（连续失败未超限）。
    /// 返回 false 时调用方应停止源并通知 UI。
    /// </summary>
    public bool ShouldRestart()
    {
        _consecutive++;
        if (_consecutive > MaxConsecutiveFailures)
        {
            Log.Warn($"流连续失败 {_consecutive} 次，超过上限，不再重启");
            return false;
        }
        Log.Warn($"流失败（第 {_consecutive}/{MaxConsecutiveFailures} 次），{RestartDelay.TotalSeconds}s 后重启");
        return true;
    }
}
