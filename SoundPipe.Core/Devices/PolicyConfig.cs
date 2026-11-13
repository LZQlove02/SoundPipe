using System.Runtime.InteropServices;

namespace SoundPipe.Core.Devices;

/// <summary>
/// IPolicyConfig（未公开 COM 接口，SoundSwitch/AudioSwitcher 等广泛使用）：
/// 程序化设置系统默认音频设备，免去用户去系统设置手动切换。
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    // 以下方法仅占位保证 vtable 顺序，不会被调用
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format, IntPtr oldFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out IntPtr period, out IntPtr minPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role, IntPtr propertyKey, out IntPtr value);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role, IntPtr propertyKey, IntPtr value);

    /// <summary>vtable index 13：设置默认设备</summary>
    [PreserveSig]
    int SetDefaultDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    [PreserveSig]
    int SetVolume([MarshalAs(UnmanagedType.LPWStr)] string deviceId, float volume);
}

/// <summary>默认音频设备切换（IPolicyConfig 封装）</summary>
internal static class PolicyConfig
{
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    /// <summary>把指定设备设为所有角色的默认端点（Console / Multimedia / Communications）</summary>
    public static void SetDefaultDevice(string deviceId)
    {
        var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient)
            ?? throw new InvalidOperationException("PolicyConfigClient 不可用");
        var obj = Activator.CreateInstance(type)!;
        try
        {
            var cfg = (IPolicyConfig)obj;
            for (var role = 0; role <= 2; role++)
            {
                var hr = cfg.SetDefaultDevice(deviceId, role);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(obj);
        }
    }
}
