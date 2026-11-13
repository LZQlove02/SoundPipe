using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Session;

/// <summary>发声应用快照项（F12：活动状态供 UI 闪烁指示）</summary>
public sealed record AppSessionInfo(int Pid, string ProcessName, bool IsActive);

/// <summary>
/// 应用会话监控（设计 §4.1）：每 1s 轮询默认输出设备的音频会话，
/// 产出正在发声的应用列表（PID、进程名、活动状态）。差异检测后经事件推送 UI。
/// （系统无"应用开始发声"推送事件，轮询是唯一途径——设计 §4.4）
/// </summary>
public sealed class SessionMonitor : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private List<AppSessionInfo> _lastSnapshot = new();
    private readonly object _lock = new();

    /// <summary>会话快照变化（首次也会触发）</summary>
    public event Action<IReadOnlyList<AppSessionInfo>>? SnapshotChanged;

    public SessionMonitor()
    {
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += (_, _) => PollOnce();
    }

    public void Start()
    {
        PollOnce();
        _timer.Start();
        Log.Info("会话监控启动（1s 轮询）");
    }

    public void Stop()
    {
        _timer.Stop();
        Log.Info("会话监控停止");
    }

    /// <summary>当前快照（UI 直接读取）</summary>
    public IReadOnlyList<AppSessionInfo> Current
    {
        get { lock (_lock) return _lastSnapshot; }
    }

    /// <summary>执行一次枚举并做差异比对</summary>
    public void PollOnce()
    {
        List<AppSessionInfo> snapshot;
        try
        {
            snapshot = EnumerateSessions();
        }
        catch (Exception ex)
        {
            // 设备缺失/被占用等瞬时错误：保留旧快照，不中断轮询
            Log.Warn($"会话枚举失败：{ex.Message}");
            return;
        }

        bool changed;
        lock (_lock)
        {
            changed = !SameSnapshot(_lastSnapshot, snapshot);
            if (changed) _lastSnapshot = snapshot;
        }

        // 事件在锁外触发：订阅者即使阻塞（刷新 UI/写日志）也不会卡住 1s 轮询线程
        if (changed) SnapshotChanged?.Invoke(snapshot);
    }

    private static List<AppSessionInfo> EnumerateSessions()
    {
        var result = new List<AppSessionInfo>();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;

        for (var i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl? control = null;
            try
            {
                control = sessions[i];
                var pid = (int)control.GetProcessID;
                if (pid == 0) continue;

                var name = TryGetProcessName(pid);
                result.Add(new AppSessionInfo(
                    pid, name,
                    IsActive: control.State == AudioSessionState.AudioSessionStateActive));
            }
            catch
            {
                // 单个会话失败（如权限）不影响其余
            }
            finally
            {
                if (control is IDisposable d) d.Dispose();
            }
        }

        return result
            .GroupBy(s => s.Pid)
            .Select(g => g.First())
            .OrderBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName; // 不需要 MainModule 权限
            return string.IsNullOrEmpty(name) ? $"pid:{pid}" : name;
        }
        catch
        {
            return $"pid:{pid}"; // 进程可能已退出
        }
    }

    private static bool SameSnapshot(IReadOnlyList<AppSessionInfo> a, IReadOnlyList<AppSessionInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Pid != b[i].Pid || a[i].IsActive != b[i].IsActive) return false;
        }
        return true;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
