using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundPipe.App;

/// <summary>用户设置（持久化到 %APPDATA%\SoundPipe\settings.json）。
/// 注意：开机自启开关不在此处，由注册表 HKCU Run 键作为单一来源管理，避免双写冲突。</summary>
public sealed class UserSettings
{
    public bool SystemMode { get; set; } = true;          // true=整系统, false=指定应用
    public double LatencyMs { get; set; } = 20;
    public double Gain { get; set; } = 1;
    public bool MicEnabled { get; set; }
    public double MicGain { get; set; } = 1;
    public string? TargetDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? MicDeviceId { get; set; }

    /// <summary>上次勾选转发的应用进程名（PID 重启后失效，按名恢复更稳定）</summary>
    public List<string> LastAppNames { get; set; } = new();

    /// <summary>每应用独立音量（F6；键为进程名，值 0–2，与总音乐增益相乘）</summary>
    public Dictionary<string, double> AppGains { get; set; } = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SoundPipe", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>从磁盘加载；文件缺失或解析失败时返回默认实例，绝不抛异常。</summary>
    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UserSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        }
        catch
        {
            // 损坏的设置文件不应阻断启动：回退默认
            return new UserSettings();
        }
    }

    /// <summary>保存到磁盘；失败静默（不影响退出流程）。</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 设置保存失败不应阻断退出
        }
    }
}
