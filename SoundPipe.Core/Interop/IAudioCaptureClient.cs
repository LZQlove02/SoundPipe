using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SoundPipe.Core.Interop;

/// <summary>
/// 自声明 IAudioCaptureClient（NAudio 2.2.1 的同名接口为 internal 不可用）。
/// GUID 与 vtable 顺序与 Windows audioclient.h 完全一致，RCW QI 可正常绑定。
/// </summary>
[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr dataBuffer, out int framesToRead, out AudioClientBufferFlags flags,
        out long devicePosition, out long qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(int framesRead);

    [PreserveSig]
    int GetNextPacketSize(out int framesInNextPacket);
}
