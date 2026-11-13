using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SoundPipe.Core;
using SoundPipe.Core.Capture;
using SoundPipe.Core.Devices;
using SoundPipe.Core.Diagnostics;
using SoundPipe.Core.Interop;
using SoundPipe.Core.Logging;
using SoundPipe.Core.Session;

namespace SoundPipe.Cli;

/// <summary>
/// SoundPipe 命令行验证工具（M1/M2 里程碑验收用）。
/// 用法：
///   list                          枚举播放设备
///   apps                          枚举当前发声应用（PID）
///   run [选项]                    开始转发并显示电平，Ctrl+C 停止
///     --mode system|apps          转发模式（默认 system）
///     --app <pid[,pid...]>        apps 模式：转发的进程 PID
///     --index n / --device <id>   目标设备
///     --monitor <n>               监听设备序号（双输出）
///     --capture <n>               system 模式：指定监听的渲染设备序号
///     --latency ms  --gain x      缓冲与增益
///     --duration sec              到时自动停止（冒烟测试）
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintUsage();
            return;
        }

        switch (args[0])
        {
            case "list": ListDevices(); break;
            case "apps": ListApps(); break;
            case "run": Run(args); break;
            case "diag": Diag(args); break;
            case "linkdiag": LinkDiag(args); break;
            case "setup": Setup(); break;
            default:
                Console.WriteLine($"未知命令：{args[0]}");
                PrintUsage();
                break;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SoundPipe —— 系统声音/应用声音 → 虚拟麦克风转发验证
              list                              枚举播放设备（* 标记虚拟输入端）
              apps                              枚举当前发声应用
              run  --mode system|apps [选项]    开始转发，Ctrl+C 停止
                --app <pid[,pid...]>            apps 模式的目标进程
                --index n / --device <id>       目标设备
                --monitor <n>                   监听设备序号
                --capture <n>                   system 模式：指定监听的渲染设备
                --mic <n>                       麦克风混音：录音设备序号（-1 = 默认麦克风）
                --mic-gain <x>                  麦克风增益（默认 1.0）
                --latency <ms>  --gain <x>      缓冲（10-100）与增益
                --duration <sec>                到时自动停止
              linkdiag [--index n]              转发链路端到端诊断（回路测试）
              setup                             一键配置：默认麦克风指向录音端 + 默认输出保护
            """);
    }

    private static void ListDevices()
    {
        var devices = DeviceService.EnumerateRenderDevices();
        Console.WriteLine("播放设备：");
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var mark = d.IsVirtualInputEnd ? "*" : " ";
            Console.WriteLine($"  [{i}]{mark} {d.FriendlyName}");
        }

        using var def = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var vol = def.AudioEndpointVolume;
        Console.WriteLine();
        Console.WriteLine($"默认输出：{def.FriendlyName}");
        Console.WriteLine($"  主音量 {vol.MasterVolumeLevelScalar * 100:F0}%，静音={vol.Mute}");
        using var mm = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Console.WriteLine($"  Multimedia 角色默认：{mm.FriendlyName}");
        Console.WriteLine();
        Console.WriteLine("* = 虚拟声卡输入端（转发目标）");
    }

    private static void ListApps()
    {
        using var monitor = new SessionMonitor();
        monitor.PollOnce();
        var sessions = monitor.Current;
        if (sessions.Count == 0)
        {
            Console.WriteLine("（当前没有发声应用）");
            return;
        }
        Console.WriteLine("发声应用：");
        foreach (var s in sessions)
        {
            Console.WriteLine($"  {(s.IsActive ? "●" : "○")} pid={s.Pid,-7} {s.ProcessName}");
        }
    }

    private static void Run(string[] args)
    {
        AudioEngine.ForwardMode mode = AudioEngine.ForwardMode.System;
        int[] appPids = [];
        string? deviceId = null;
        int? index = null;
        int? monitorIndex = null;
        int? captureIndex = null;
        int? micIndex = null;
        var latency = 20;
        var gain = 1f;
        var micGain = 1f;
        var durationSec = 0;

        for (var i = 1; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--mode": mode = args[i + 1] == "apps" ? AudioEngine.ForwardMode.Apps : AudioEngine.ForwardMode.System; break;
                case "--app": appPids = args[i + 1].Split(',').Select(int.Parse).ToArray(); break;
                case "--index": index = int.Parse(args[i + 1]); break;
                case "--device": deviceId = args[i + 1]; break;
                case "--monitor": monitorIndex = int.Parse(args[i + 1]); break;
                case "--capture": captureIndex = int.Parse(args[i + 1]); break;
                case "--mic": micIndex = int.Parse(args[i + 1]); break;
                case "--mic-gain": micGain = float.Parse(args[i + 1]); break;
                case "--latency": latency = Math.Clamp(int.Parse(args[i + 1]), 10, 100); break;
                case "--gain": gain = float.Parse(args[i + 1]); break;
                case "--duration": durationSec = int.Parse(args[i + 1]); break;
            }
        }

        // 目标设备
        MMDevice? target = null;
        if (index is not null || deviceId is not null)
        {
            var list = DeviceService.EnumerateRenderDevices();
            var info = deviceId is not null
                ? list.FirstOrDefault(d => d.Id == deviceId)
                : (index!.Value >= 0 && index < list.Count ? list[index.Value] : null);
            if (info is null) { Console.WriteLine("未找到指定设备"); return; }
            target = DeviceService.GetDeviceById(info.Id);
            if (target is null) { Console.WriteLine("设备已不可用"); return; }
            if (!info.IsVirtualInputEnd)
                Console.WriteLine("⚠ 目标不是虚拟输入端，通话软件不会把它识别为麦克风");
        }

        // 监听设备
        MMDevice? monitorDevice = null;
        if (monitorIndex is not null)
        {
            var list = DeviceService.EnumerateRenderDevices();
            if (monitorIndex < list.Count) monitorDevice = DeviceService.GetDeviceById(list[monitorIndex.Value].Id);
            if (monitorDevice is null) { Console.WriteLine("监听设备无效"); target?.Dispose(); return; }
        }

        // 整系统捕获源设备（默认 = 系统默认输出）
        MMDevice? captureDevice = null;
        if (captureIndex is not null)
        {
            var list = DeviceService.EnumerateRenderDevices();
            if (captureIndex >= 0 && captureIndex < list.Count)
                captureDevice = DeviceService.GetDeviceById(list[captureIndex.Value].Id);
            if (captureDevice is null)
            { Console.WriteLine("捕获设备无效"); target?.Dispose(); monitorDevice?.Dispose(); return; }
        }

        using var engine = new AudioEngine();
        engine.SourceDropped += (name, reason) => Console.WriteLine($"\n[源放弃] {name}：{reason}");
        engine.EngineFailed += reason => Console.WriteLine($"\n[引擎停止] {reason}");
        engine.Info += msg => Console.WriteLine($"\n[信息] {msg}");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; engine.Stop(); };

        try
        {
            engine.Start(mode, target, monitorDevice, latency, gain, captureDevice);
            foreach (var pid in appPids)
                engine.AddApp(pid, ResolveProcessName(pid));

            // 麦克风混音（--mic n：录音设备序号，--mic -1 = 默认麦克风）
            if (micIndex is not null)
            {
                string? micId = null;
                if (micIndex >= 0)
                {
                    var caps = DeviceService.EnumerateCaptureDevices();
                    micId = micIndex < caps.Count ? caps[micIndex.Value].Id : null;
                    if (micId is null) { Console.WriteLine("麦克风设备无效，混音未启用"); }
                }
                if (micIndex < 0 || micId is not null)
                    engine.EnableMic(micId, micGain);
            }
        }
        catch (Exception ex)
        {
            Log.Error("引擎启动失败", ex);
            Console.WriteLine($"启动失败：{ex.Message}");
            return;
        }
        finally
        {
            target?.Dispose();
            monitorDevice?.Dispose();
            captureDevice?.Dispose();
        }

        Console.WriteLine($"转发中（{mode}）→ {engine.TargetDeviceName}（latency={latency}ms，gain={gain}，监听={engine.MonitorDeviceName ?? "关"}）");
        if (mode == AudioEngine.ForwardMode.Apps)
            Console.WriteLine($"目标进程：{string.Join(", ", appPids)}");
        Console.WriteLine("Ctrl+C 停止。\n");

        // 电平轮询显示（每 150ms）。所有缓冲在循环外复用：原写法每次轮询都会分配
        // int[]（TargetPids）+ LINQ 迭代器 + N 个字符串 + string.Join 的中间数组。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pidBuf = new List<int>(8);
        var barBuf = new char[24];
        var line = new System.Text.StringBuilder(160);
        while (engine.IsRunning)
        {
            line.Clear();
            line.Append('\r').Append(' ');
            if (mode == AudioEngine.ForwardMode.System)
            {
                line.Append("系统声音 ");
                AppendBar(line, engine.SystemSourceRms, 24, barBuf);
                line.Append("   麦克风 ");
                AppendBar(line, engine.MicSourceRms, 12, barBuf);
                line.Append("   总输出 ");
                AppendBar(line, engine.MasterRms, 20, barBuf);
                line.Append("   ");
            }
            else
            {
                engine.CopyTargetPids(pidBuf);
                foreach (var pid in pidBuf)
                {
                    if (line.Length > 2) line.Append("  ");
                    line.Append(pid).Append(':')
                        .Append((engine.GetAppRms(pid) * 100f).ToString("F0"))
                        .Append('%');
                }
                line.Append("   总输出 ");
                AppendBar(line, engine.MasterRms, 20, barBuf);
                line.Append("   ");
            }
            Console.Write(line.ToString());
            if (durationSec > 0 && sw.Elapsed.TotalSeconds >= durationSec) engine.Stop();
            Thread.Sleep(150);
        }

        Console.WriteLine("\n已停止。日志见 %APPDATA%\\SoundPipe\\logs\\");
    }

    /// <summary>一键配置：选最佳虚拟目标 + 默认麦克风指向录音端 + 默认输出保护</summary>
    private static void Setup()
    {
        var r = AutoSetup.Run();
        if (r.Target is null)
        {
            Console.WriteLine($"[失败] {r.Message}");
            return;
        }
        Console.WriteLine($"[OK] {r.Message}");
        Console.WriteLine($"转发目标：{r.Target.FriendlyName}");
        Console.WriteLine("现在任何软件的默认麦克风都已是虚拟声卡录音端");
    }

    /// <summary>转发链路端到端诊断：目标→（桥）→录音端回路测试，桥断自动拉起 Voicemeeter</summary>
    private static void LinkDiag(string[] args)
    {
        string? targetId = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--index")
            {
                var list = DeviceService.EnumerateRenderDevices();
                var idx = int.Parse(args[i + 1]);
                if (idx >= 0 && idx < list.Count) targetId = list[idx].Id;
            }
            else if (args[i] == "--device")
            {
                targetId = args[i + 1];
            }
        }

        Console.WriteLine("链路诊断中（约 5 秒，会有一声测试音）…");
        var r = LinkDiagnostics.RunCheck(targetId);
        Console.WriteLine();
        Console.WriteLine(r.IsOk ? $"[OK] {r.Message}" : $"[失败] {r.Message}");
        if (r.CaptureDeviceName is not null)
            Console.WriteLine($"录音端：{r.CaptureDeviceName}");
    }

    /// <summary>激活诊断：对指定 PID（默认自身进程）做进程循环捕获激活，打印每步状态。
    /// mode: mta（默认）| sta（STA 线程 + 消息泵）</summary>
    private static void Diag(string[] args)
    {
        var mode = args.Length > 1 ? args[1] : "mta";
        var waitSec = args.Length > 2 ? int.Parse(args[2]) : 15;
        var targetPid = args.Length > 3 ? int.Parse(args[3]) : Environment.ProcessId;

        if (mode is "sta" or "stadev")
        {
            var t = new Thread(() => DiagActivate(targetPid, waitSec, apartment: 0x2, deviceOnly: mode == "stadev"));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
        }
        else
        {
            DiagActivate(targetPid, waitSec, apartment: 0x0, deviceOnly: mode == "dev");
        }
    }

    private static void DiagActivate(int pid, int waitSec, int apartment, bool deviceOnly = false)
    {
        Console.WriteLine($"[diag:{(apartment == 0x2 ? "STA" : "MTA")}{(deviceOnly ? "+dev" : "")}] 目标 pid={pid}，等待上限 {waitSec}s");

        var hrCo = Ole32.CoInitializeEx(IntPtr.Zero, apartment);
        Console.WriteLine($"[diag] CoInitializeEx hr=0x{hrCo:X8}");
        var handler = new CoreCaptureDiagHandler();
        var riid = typeof(IAudioClient).GUID;
        IntPtr outerPtr = IntPtr.Zero;
        var path = deviceOnly
            ? "{2EEF81BE-33FA-4800-9670-1CD474972C3F}" // DEVINTERFACE_AUDIO_RENDER
            : NativeAudioActivation.VirtualAudioDeviceProcessLoopback;
        Console.WriteLine($"[diag] 激活路径：{path}");
        try
        {
            if (!deviceOnly)
            {
                var activationParams = new AudioClientActivationParams
                {
                    ActivationType = NativeAudioActivation.AudioClientActivationTypeProcessLoopback,
                    ProcessLoopbackParams = new ProcessLoopbackParams
                    {
                        ProcessLoopbackMode = NativeAudioActivation.ProcessLoopbackModeIncludeTargetProcessTree,
                        TargetProcessId = (uint)pid
                    }
                };
                var blob = PropVariantBlob.Create(activationParams);
                outerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
                Marshal.StructureToPtr(blob, outerPtr, false);
                // 诊断：验证 PROPVARIANT x64 布局（vt@0=65, cbSize@8=12, pBlobData@16）
                Console.WriteLine($"[diag] PROPVARIANT size={Marshal.SizeOf<PropVariantBlob>()}，" +
                    $"BlobData offset={Marshal.OffsetOf<PropVariantBlob>("BlobData")}，" +
                    $"vt=0x{Marshal.ReadInt16(outerPtr, 0):X}，cbSize={Marshal.ReadInt32(outerPtr, 8)}，" +
                    $"pBlobData@16=0x{Marshal.ReadIntPtr(outerPtr, 16):X}");
                if (Marshal.ReadIntPtr(outerPtr, 16) != IntPtr.Zero)
                {
                    var bp = Marshal.ReadIntPtr(outerPtr, 16);
                    Console.WriteLine($"[diag] 参数内容：@0 ActivationType={Marshal.ReadInt32(bp, 0)}，" +
                        $"@4 Mode={Marshal.ReadInt32(bp, 4)}，@8 TargetPid={Marshal.ReadInt32(bp, 8)}");
                }
                // 注意：blob 数据（含 outerPtr 内指向的参数）必须存活到激活回调触发，
                // 提前释放会被系统读到复用内存 → 0x80070002（见 ProcessLoopbackCapture.Activate）。
            }
            var hr = NativeAudioActivation.ActivateAudioInterfaceAsync(
                path,
                ref riid, outerPtr, handler, out var asyncOp);
            Console.WriteLine($"[diag] ActivateAudioInterfaceAsync hr=0x{hr:X8}，asyncOp={(asyncOp != IntPtr.Zero ? "ok" : "null")}");
            if (hr >= 0)
            {
                Marshal.Release(asyncOp); // 仅诊断，不持有

                for (var i = 0; i < waitSec * 10; i++)
                {
                    // STA：泵消息（完成回调经窗口消息/套间分发）；MTA：纯事件等待
                    if (apartment == 0x2)
                    {
                        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                        {
                            TranslateMessage(ref msg);
                            DispatchMessage(ref msg);
                        }
                    }
                    if (handler.Completed.WaitOne(100)) break;
                    if (i % 10 == 9) Console.WriteLine($"[diag] 等待中… {(i + 1) / 10.0:F0}s");
                }

                if (!handler.CompletedSignaled)
                {
                    Console.WriteLine($"[diag] ✗ {waitSec}s 内回调未触发（激活挂起）");
                }
                else
                {
                    Console.WriteLine($"[diag] 回调已触发：Result hr=0x{handler.Result:X8}，Interface={(handler.Interface is not null ? "ok" : "null")}");
                    if (handler.Interface is IAudioClient client)
                    {
                        try
                        {
                            var mixHr = client.GetMixFormat(out var mixPtr);
                            Console.WriteLine($"[diag] GetMixFormat hr=0x{mixHr:X8}，ptr={mixPtr != IntPtr.Zero}");
                            if (mixPtr != IntPtr.Zero) Marshal.FreeHGlobal(mixPtr);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[diag] GetMixFormat 异常：{ex.Message}");
                        }
                        Marshal.ReleaseComObject(client);
                    }
                }
            }
        }
        finally
        {
            // 回调已触发（或激活同步失败）后才释放参数内存
            if (outerPtr != IntPtr.Zero)
            {
                var blobPtr = Marshal.ReadIntPtr(outerPtr, 16); // PROPVARIANT.blob.pBlobData (x64)
                if (blobPtr != IntPtr.Zero) Marshal.FreeHGlobal(blobPtr);
                Marshal.FreeHGlobal(outerPtr);
            }
            if (hrCo >= 0 && apartment == 0x2) Ole32.CoUninitialize();
            Console.WriteLine("[diag] 完成");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    /// <summary>诊断用激活回调（记录触发状态）</summary>
    private sealed class CoreCaptureDiagHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Completed = new(false);
        public int Result;
        public object? Interface;
        public bool CompletedSignaled;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                var hr = operation.GetActivateResult(out Result, out var ifacePtr);
                if (hr < 0)
                {
                    Result = hr;
                }
                else if (ifacePtr != IntPtr.Zero)
                {
                    Interface = Marshal.GetObjectForIUnknown(ifacePtr);
                    Marshal.Release(ifacePtr); // 所有权移交 RCW
                }
            }
            catch (Exception ex)
            {
                Result = Marshal.GetHRForException(ex);
            }
            finally
            {
                CompletedSignaled = true;
                Completed.Set();
            }
            return 0;
        }
    }

    private static string ResolveProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return string.IsNullOrEmpty(p.ProcessName) ? $"pid:{pid}" : p.ProcessName;
        }
        catch
        {
            return $"pid:{pid}";
        }
    }

    /// <summary>把电平条追加到 StringBuilder（复用 char 缓冲，避免每次轮询生成字符串）</summary>
    private static void AppendBar(System.Text.StringBuilder sb, float rms, int width, char[] buf)
    {
        var db = SoundPipe.Core.Audio.DspMath.ToDb(rms); // -60..0
        var filled = Math.Clamp((int)Math.Round((db + 60) / 60 * width), 0, width);
        var ch = db > -6 ? '█' : db > -18 ? '▓' : '░';
        sb.Append('[');
        for (var i = 0; i < width; i++) buf[i] = i < filled ? ch : ' ';
        sb.Append(buf, 0, width);
        sb.Append(']');
    }
}
