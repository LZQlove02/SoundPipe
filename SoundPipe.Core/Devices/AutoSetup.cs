using NAudio.CoreAudioApi;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Devices;

/// <summary>一键自动配置结果</summary>
public sealed record AutoSetupResult(DeviceService.DeviceInfo? Target, string Message);

/// <summary>
/// 一键自动配置（零操作目标）：用户只需点"开始转发"，其余全自动——
/// 1. 自动选中最佳虚拟输入端为转发目标（桥已验证的实例优先）
/// 2. 自动把系统默认麦克风指向对应录音端（任何软件直接可用）
/// 3. 默认输出被吸进虚拟设备时自动切回物理扬声器（防"没声音"）
/// 全部幂等：已是目标状态则不动用户配置。
/// </summary>
public static class AutoSetup
{
    /// <summary>执行自动配置（快速：仅设备枚举 + COM 调用，无回路测试）</summary>
    public static AutoSetupResult Run()
    {
        var notes = new List<string>();
        try
        {
            var targets = DeviceService.EnumerateRenderDevices();

            // 1. 转发目标：虚拟输入端，按桥可用性打分
            var target = PickBest(targets, d => d.IsVirtualInputEnd, ScoreRender);
            if (target is null)
                return new AutoSetupResult(null, "未找到虚拟声卡输入端（VAIO/CABLE），请先安装");

            // 2. 默认麦克风 → 录音端
            var capture = PickBestCapture();
            if (capture is not null)
            {
                using (capture)
                {
                    if (!IsDefaultEndpoint(capture.ID, DataFlow.Capture))
                    {
                        PolicyConfig.SetDefaultDevice(capture.ID);
                        notes.Add($"默认麦克风 → {capture.FriendlyName}");
                    }
                }
            }
            else
            {
                notes.Add("未找到录音端（Voicemeeter Output/CABLE Output）");
            }

            // 3. 默认输出保护：被切到虚拟设备则改回物理端
            FixDefaultRender(targets, notes);

            return new AutoSetupResult(
                target,
                notes.Count == 0 ? "配置已就绪（无变更）" : string.Join("；", notes));
        }
        catch (Exception ex)
        {
            Log.Warn($"自动配置失败：{ex.Message}");
            return new AutoSetupResult(null, $"自动配置失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 渲染端

    /// <summary>虚拟输入端打分：CABLE Input > "(2-" 第二实例（桥已验证）> 其他虚拟端</summary>
    private static int ScoreRender(DeviceService.DeviceInfo d)
    {
        var name = d.FriendlyName;
        if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("(2-", StringComparison.Ordinal)) return 2;
        return 1;
    }

    private static T? PickBest<T>(IEnumerable<T> list, Func<T, bool> filter, Func<T, int> score) where T : class
    {
        T? best = null;
        var bestScore = -1;
        foreach (var item in list)
        {
            if (!filter(item)) continue;
            var s = score(item);
            if (s > bestScore)
            {
                best = item;
                bestScore = s;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- 捕获端

    /// <summary>录音端打分：CABLE Output > "Output (2-" > 变音麦克风/其他</summary>
    private static int ScoreCapture(string name)
    {
        if (name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("Output", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("(2-", StringComparison.Ordinal)) return 2;
        return 1;
    }

    private static MMDevice? PickBestCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        MMDevice? best = null;
        var bestScore = -1;
        try
        {
            foreach (var d in devices)
            {
                var name = d.FriendlyName;
                var isBridge =
                    name.Contains("Voicemeeter Output", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("变音麦克风", StringComparison.Ordinal) ||
                    (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
                     (name.Contains("麦克风", StringComparison.Ordinal) ||
                      name.Contains("Microphone", StringComparison.OrdinalIgnoreCase)));
                if (!isBridge)
                {
                    d.Dispose();
                    continue;
                }

                var s = ScoreCapture(name);
                if (s > bestScore)
                {
                    best?.Dispose();
                    best = d;
                    bestScore = s;
                }
                else
                {
                    d.Dispose();
                }
            }
        }
        catch
        {
            // 枚举异常时返回已选中的
        }
        return best;
    }

    // ---------------------------------------------------------------- 默认设备

    private static bool IsDefaultEndpoint(string deviceId, DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            return def.ID == deviceId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>默认输出被吸进虚拟设备（VAIO/CABLE）时，切回第一个物理输出</summary>
    private static void FixDefaultRender(IReadOnlyList<DeviceService.DeviceInfo> renderDevices, List<string> notes)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (!LooksVirtual(def.FriendlyName)) return;

            var physical = renderDevices.FirstOrDefault(d =>
                !LooksVirtual(d.FriendlyName) &&
                (d.FriendlyName.Contains("扬声器", StringComparison.Ordinal) ||
                 d.FriendlyName.Contains("耳机", StringComparison.Ordinal) ||
                 d.FriendlyName.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
                 d.FriendlyName.Contains("Headphone", StringComparison.OrdinalIgnoreCase)));

            if (physical is null)
            {
                notes.Add("默认输出在虚拟设备上且未找到物理扬声器，请手动切换");
                return;
            }

            PolicyConfig.SetDefaultDevice(physical.Id);
            notes.Add($"默认输出 → {physical.FriendlyName}");
        }
        catch
        {
            // 默认输出探测失败不影响主流程
        }
    }

    /// <summary>虚拟设备名特征</summary>
    private static bool LooksVirtual(string name) =>
        name.Contains("VAIO", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("变声器", StringComparison.Ordinal);
}

