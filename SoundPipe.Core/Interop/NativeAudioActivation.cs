using System.Runtime.InteropServices;

namespace SoundPipe.Core.Interop;

/// <summary>进程循环捕获激活参数（audioclientactivationparams.h）</summary>
internal static class NativeAudioActivation
{
    /// <summary>虚拟设备路径：进程循环捕获专用。
    /// audioclientactivationparams.h 中 VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK 宏的值为 L"VAD\Process_Loopback"</summary>
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int AudioClientActivationTypeDefault = 0;
    public const int AudioClientActivationTypeProcessLoopback = 1;

    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;
    public const int ProcessLoopbackModeExcludeTargetProcessTree = 1;

    [DllImport("mmdevapi.dll", EntryPoint = "ActivateAudioInterfaceAsync", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid interfaceGuid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr asyncOperation);
}

/// <summary>AUDIOCLIENT_ACTIVATION_PARAMS（VT_BLOB 传入）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public ProcessLoopbackParams ProcessLoopbackParams;
}

/// <summary>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS：注意官方字段顺序为 TargetProcessId 在前、Mode 在后</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessLoopbackParams
{
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

/// <summary>PROPVARIANT（仅支持 VT_BLOB 所需布局，x64）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort VariantType;      // VT_BLOB = 65
    private ushort _r1, _r2, _r3;
    public uint BlobSize;
    public IntPtr BlobData;

    public static PropVariantBlob Create<T>(in T value) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(value, ptr, false);
        return new PropVariantBlob
        {
            VariantType = 65, // VT_BLOB
            BlobSize = (uint)size,
            BlobData = ptr
        };
    }

    public void Free()
    {
        if (BlobData != IntPtr.Zero) Marshal.FreeHGlobal(BlobData);
        BlobData = IntPtr.Zero;
    }
}

/// <summary>IActivateAudioInterfaceCompletionHandler（mmdeviceapi.h，IID 41D949AB-9862-444A-80F6-C261334DA5EB）</summary>
[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted([In, MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>IActivateAudioInterfaceAsyncOperation（mmdeviceapi.h，IID 72A22D78-CDE4-431D-B8CC-843A71199B6D）</summary>
[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, out IntPtr activateInterface);
}

/// <summary>IAgileObject 标记接口（使回调对象免于套间编组，微软官方样例要求）</summary>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}
