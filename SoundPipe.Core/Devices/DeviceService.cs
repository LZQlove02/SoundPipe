using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SoundPipe.Core.Logging;

namespace SoundPipe.Core.Devices;

/// <summary>
/// 设备服务（设计 F3 / §5.3）：设备枚举、虚拟输入端识别、默认目标选择、
/// 热插拔事件（自实现 IMMNotificationClient，5 个回调齐全——审阅意见 #3）→ 事件驱动刷新流与 UI。
/// </summary>
public static class DeviceService
{
    private static readonly object InitLock = new();
    private static NotificationClient? _notificationClient;

    /// <summary>设备集合变化（增/删/状态变化/默认设备变化）。UI 订阅后刷新下拉列表并重建受影响流。</summary>
    public static event Action<DeviceChangeKind, string>? DeviceChanged;

    public sealed record DeviceInfo(string Id, string FriendlyName, bool IsVirtualInputEnd);

    private static readonly string[] VirtualMarkers =
    {
        "CABLE Input",
        "Voicemeeter Input",
        "Voicemeeter VAIO",
        "Line 1 (Virtual Audio Cable)",
        "Line 2 (Virtual Audio Cable)",
        "Line 3 (Virtual Audio Cable)"
    };

    public static bool LooksLikeVirtualInputEnd(string friendlyName) =>
        VirtualMarkers.Any(m => friendlyName.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>注册设备热插拔通知（幂等）</summary>
    public static void EnsureNotificationsRegistered()
    {
        lock (InitLock)
        {
            if (_notificationClient is not null) return;
            _notificationClient = new NotificationClient(t =>
                DeviceChanged?.Invoke(t.Item1, t.Item2));
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(_notificationClient);
                Log.Info("设备热插拔通知已注册");
            }
            catch (Exception ex)
            {
                Log.Error("注册设备通知失败", ex);
            }
        }
    }

    /// <summary>枚举所有渲染（播放）设备；虚拟输入端排前并标记</summary>
    public static IReadOnlyList<DeviceInfo> EnumerateRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<DeviceInfo>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using var _ = d;
            result.Add(new DeviceInfo(d.ID, d.FriendlyName, LooksLikeVirtualInputEnd(d.FriendlyName)));
        }
        return result
            .OrderByDescending(x => x.IsVirtualInputEnd)
            .ThenBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>枚举录音设备（麦克风混音用）</summary>
    public static IReadOnlyList<DeviceInfo> EnumerateCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<DeviceInfo>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using var _ = d;
            result.Add(new DeviceInfo(d.ID, d.FriendlyName, false));
        }
        return result
            .OrderBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按设计 §5.3 取默认目标：首个虚拟输入端，否则系统默认渲染设备</summary>
    public static DeviceInfo? GetDefaultTarget()
    {
        var list = EnumerateRenderDevices();
        var virtualDevice = list.FirstOrDefault(d => d.IsVirtualInputEnd);
        if (virtualDevice is not null) return virtualDevice;

        using var enumerator = new MMDeviceEnumerator();
        using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        return list.FirstOrDefault(d => d.Id == def.ID);
    }

    /// <summary>判断录音端点名是否为虚拟声卡（VAIO/CABLE/变声器等）——麦克风混音源必须排除，
    /// 否则会捕获到转发输出自身形成回环</summary>
    public static bool LooksLikeVirtualCapture(string name) =>
        name.Contains("VAIO", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("变音", StringComparison.Ordinal) ||
        name.Contains("变声器", StringComparison.Ordinal);

    /// <summary>按 ID 取设备（渲染或录音端点，调用方负责 Dispose）</summary>
    public static MMDevice? GetDeviceById(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (d.ID == id) return d;
            d.Dispose();
        }

        // 麦克风混音等场景按 ID 取录音端点
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (d.ID == id) return d;
            d.Dispose();
        }
        return null;
    }

    public enum DeviceChangeKind
    {
        Added,
        Removed,
        StateChanged,
        DefaultChanged
    }

    /// <summary>自实现 IMMNotificationClient（审阅意见 #3：5 个回调齐全）</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action<(DeviceChangeKind kind, string id)> _dispatch;

        public NotificationClient(Action<(DeviceChangeKind, string)> dispatch) => _dispatch = dispatch;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            _dispatch((DeviceChangeKind.StateChanged, deviceId));

        public void OnDeviceAdded(string deviceId) =>
            _dispatch((DeviceChangeKind.Added, deviceId));

        public void OnDeviceRemoved(string deviceId) =>
            _dispatch((DeviceChangeKind.Removed, deviceId));

        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
        {
            if (dataFlow == DataFlow.Render)
                _dispatch((DeviceChangeKind.DefaultChanged, defaultDeviceId));
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey)
        {
            // 名称/图标变化不影响流，忽略
        }
    }
}
