using System.Runtime;
using NAudio.CoreAudioApi;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Capture;
using SoundPipe.Core.Devices;
using SoundPipe.Core.Logging;
using SoundPipe.Core.Output;

namespace SoundPipe.Core;

/// <summary>
/// 音频引擎（M2）：
/// 模式一（System）：整系统循环捕获 → 混音 → 目标设备输出
/// 模式二（Apps）：按勾选进程循环捕获（动态增删，不停流）→ 同上
/// 双输出：目标设备 + 可选监听设备，独立缓冲独立时钟，漂移经 DriftCorrectedReader 修正（§5.6）。
/// 异常恢复：捕获 1s×3 重启（超限弃源通知）；目标输出失败 → 引擎停止（§5.6）。
/// 线程模型（§4.2）：调用线程只做启停；NAudio 捕获/输出回调线程 + 本引擎混音线程。
/// 引擎启动时启用 SustainedLowLatency GC（N5）。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public enum ForwardMode { System, Apps }

    private readonly object _gate = new();

    private SpScRingBuffer? _targetRing;   // 混音线程 → 目标输出回调
    private SpScRingBuffer? _monitorRing;  // 混音线程 → 监听输出回调
    private OutputWriter? _targetOut;
    private OutputWriter? _monitorOut;
    private Mixer? _mixer;
    private Thread? _mixerThread;
    private CancellationTokenSource? _cts;

    private SystemLoopbackCapture? _systemCapture;
    private MixerSource? _systemSource;
    private readonly RestartPolicy _systemPolicy = new();
    private long _systemStartedMs;
    private string? _systemCaptureDeviceId;
    private string? _targetDeviceId; // 当前转发目标端点 ID（重建时按 ID 校验，避免同名设备误判）
    private volatile float _musicGain = 1f; // 音乐源统一增益（System / Apps 模式共用）

    // 麦克风混音管线（M4）：真实麦克风 → 转换 → 混音器叠加，实现"人声+音乐"
    private MicrophoneCapture? _micCapture;
    private MixerSource? _micSource;
    private readonly RestartPolicy _micPolicy = new();
    private long _micStartedMs;
    private string? _micDeviceId;

    // 定时精度（winmm）：运行期间 1ms 粒度，保证混音线程 Sleep(1) 节拍产能
    private bool _timerPeriodRaised;

    // GC 延迟模式（进程级）：Start 期间用 SustainedLowLatency 避免音频回调被第 2 代回收打断，
    // Stop 必须还原原值——否则进程（含 WPF UI）会永久停留在非压缩 GC 模式，堆只增不减。
    private GCLatencyMode _prevLatencyMode;
    private bool _gcLatencyRaised;

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern int timeBeginPeriod(uint ms);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern int timeEndPeriod(uint ms);

    private sealed class AppPipeline
    {
        public required string Name;
        public required SpScRingBuffer Ring;
        public required MixerSource Source;
        public required ProcessLoopbackCapture Capture;
        public required RestartPolicy Policy;
        public System.Threading.Timer? RestartTimer;
        public volatile bool Stopping;
        /// <summary>该应用的独立增益（F6；与总音乐增益相乘）</summary>
        public float UserGain = 1f;
        /// <summary>当前捕获实例的启动时刻（TickCount64 ms）。用于判定"稳定运行后失败"重新计数</summary>
        public long StartedMs;
    }

    private readonly Dictionary<int, AppPipeline> _apps = new();
    private readonly object _appGate = new();

    public bool IsRunning { get; private set; }
    public ForwardMode Mode { get; private set; } = ForwardMode.System;
    public string? TargetDeviceName { get; private set; }
    public string? MonitorDeviceName { get; private set; }
    public int LatencyMs { get; private set; } = 20;
    /// <summary>总输出 RMS（UI/CLI 显示）</summary>
    public float MasterRms => _mixer?.MasterRms ?? 0f;
    /// <summary>系统声源 RMS</summary>
    public float SystemSourceRms => _systemSource?.LastRms ?? 0f;
    /// <summary>麦克风源 RMS（混音开启时）</summary>
    public float MicSourceRms => _micSource?.LastRms ?? 0f;
    /// <summary>麦克风混音是否开启</summary>
    public bool MicEnabled => _micCapture is not null;

    /// <summary>把当前按应用转发的 PID 写入调用方列表（先清空），返回条数。
    /// 供电平轮询显示复用缓冲，避免每次轮询分配数组与 LINQ 迭代器。</summary>
    public int CopyTargetPids(List<int> dest)
    {
        lock (_appGate)
        {
            dest.Clear();
            foreach (var pid in _apps.Keys) dest.Add(pid);
            return dest.Count;
        }
    }

    /// <summary>某应用源被放弃（连续 3 次失败，§5.6）。参数：名称+原因</summary>
    public event Action<string, string>? SourceDropped;
    /// <summary>引擎级失败（目标输出不可用）。参数：原因</summary>
    public event Action<string>? EngineFailed;
    /// <summary>一般信息（设备重建等）</summary>
    public event Action<string>? Info;

    // ------------------------------------------------------------------ 启停

    /// <summary>启动转发。mode=System 用整系统捕获；mode=Apps 后续经 AddApp 添加进程。
    /// captureDevice：整系统模式指定监听的渲染设备（null = 系统默认输出）。</summary>
    public void Start(ForwardMode mode, MMDevice? targetDevice = null, MMDevice? monitorDevice = null,
        int latencyMs = 20, float gain = 1f, MMDevice? captureDevice = null)
    {
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("引擎已在运行");
            Mode = mode;
            LatencyMs = latencyMs;
            _musicGain = gain;

            // GC 低延迟模式（设计 N5）：避免音频回调期间发生第 2 代阻塞回收。进程级设置，Stop 还原。
            // 防御式包裹：进程内若已有 no-GC region，赋值会抛 InvalidOperationException；
            // 此时降级为不启用（音频照常工作），而不是让引擎启动失败。
            try
            {
                _prevLatencyMode = GCSettings.LatencyMode;
                if (_prevLatencyMode != GCLatencyMode.NoGCRegion)
                {
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    _gcLatencyRaised = true;
                }
                else
                {
                    Log.Warn("进程内已有 no-GC region，本次不启用 GC 低延迟模式");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"GC 低延迟模式启用失败（已忽略）：{ex.Message}");
            }

            // 定时精度 1ms：混音/捕获轮询节拍依赖（Stop 恢复）
            if (timeBeginPeriod(1) == 0) _timerPeriodRaised = true;

            // 提前置位：启动中途任何一步失败，catch 内用 Stop() 统一回收已建资源
            IsRunning = true;
            try
            {
                var target = targetDevice ?? ResolveDefaultTarget();
                TargetDeviceName = target.FriendlyName;
                _targetDeviceId = target.ID;
                MonitorDeviceName = monitorDevice?.FriendlyName;

                // 环形缓冲：65536 floats = 32768 帧 ≈ 682ms，2 的幂
                _targetRing = new SpScRingBuffer(65536);
                _monitorRing = monitorDevice is null ? null : new SpScRingBuffer(65536);

                _targetOut = new OutputWriter(_targetRing, latencyMs);
                _targetOut.PlaybackFailed += ex => OnOutputFailed(true, ex);
                _targetOut.Start(target);

                if (_monitorRing is not null && monitorDevice is not null)
                {
                    _monitorOut = new OutputWriter(_monitorRing, latencyMs);
                    _monitorOut.PlaybackFailed += ex => OnOutputFailed(false, ex);
                    _monitorOut.Start(monitorDevice);
                }

                _mixer = _monitorRing is not null
                    ? new Mixer(_targetRing, _monitorRing)
                    : new Mixer(_targetRing);

                _cts = new CancellationTokenSource();
                _mixerThread = new Thread(MixerLoop)
                {
                    IsBackground = true,
                    Name = "SoundPipe.Mixer",
                    Priority = ThreadPriority.AboveNormal
                };
                _mixerThread.Start();

                if (mode == ForwardMode.System)
                {
                    _systemSource = new MixerSource("系统声音", new SpScRingBuffer(65536), _musicGain);
                    _mixer.AddSource(_systemSource);
                    _systemCaptureDeviceId = captureDevice?.ID;
                    _systemCapture = new SystemLoopbackCapture(_systemCaptureDeviceId);
                    _systemCapture.RecordingStoppedUnexpectedly += ex => OnSystemCaptureFailed(ex);
                    _systemCapture.Start(_systemSource.Buffer);
                    _systemStartedMs = Environment.TickCount64;
                }

                DeviceService.EnsureNotificationsRegistered();
                DeviceService.DeviceChanged += OnDeviceChanged;
                Log.Info($"引擎启动：mode={mode}，目标={TargetDeviceName}，监听={MonitorDeviceName ?? "关"}，latency={latencyMs}ms，gain={gain}");
            }
            catch
            {
                Stop(); // 重入锁安全（Monitor 可重入），回收全部已建资源
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
            DeviceService.DeviceChanged -= OnDeviceChanged;
            _deviceRebuildTimer?.Dispose(); // 否则定时器持续 root 引擎实例（泄漏）
            _deviceRebuildTimer = null;
            if (_timerPeriodRaised)
            {
                timeEndPeriod(1);
                _timerPeriodRaised = false;
            }
            // 按应用源
            lock (_appGate)
            {
                foreach (var app in _apps.Values)
                {
                    app.Stopping = true;
                    app.RestartTimer?.Dispose();
                    app.Capture.Stop();
                    app.Capture.Dispose();
                }
                _apps.Clear();
            }

            // 系统捕获
            try { _cts?.Cancel(); _mixerThread?.Join(500); } catch { }
            _systemCapture?.Stop();
            _systemCapture?.Dispose();

            // 麦克风混音
            _micCapture?.Stop();
            _micCapture?.Dispose();
            _micCapture = null;
            _micSource = null;

            _targetOut?.Stop();
            _targetOut?.Dispose();
            _monitorOut?.Stop();
            _monitorOut?.Dispose();

            _systemCapture = null;
            _systemSource = null;
            _targetOut = null;
            _monitorOut = null;
            _targetRing = null;
            _monitorRing = null;
            _targetDeviceId = null;
            _mixer = null;
            _mixerThread = null;
            _cts?.Dispose();
            _cts = null;

            // GC 低延迟模式还原：放在拆解末尾——前面的混音线程 join 与设备回调收尾期间
            // 仍需低延迟模式保护；且 Stop 可能正跑在 WASAPI 播放线程上，此处绝不允许抛异常。
            if (_gcLatencyRaised)
            {
                _gcLatencyRaised = false;
                try
                {
                    // 仅当仍是我们设置的模式才还原：期间若有代码进入 no-GC region，
                    // 写回原值会抛 InvalidOperationException，从而中断整个拆解流程。
                    if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
                        GCSettings.LatencyMode = _prevLatencyMode;
                }
                catch (Exception ex)
                {
                    Log.Warn($"GC 延迟模式还原失败（已忽略）：{ex.Message}");
                }
            }

            Log.Info("引擎已停止");
        }
    }

    // ------------------------------------------------------------------ 按应用源管理

    /// <summary>添加按应用转发源（运行中调用不停流，§5.2）。gain 为该应用的独立增益（F6）。</summary>
    public void AddApp(int pid, string name, float gain = 1f)
    {
        // 全程持 _gate：若中途放开，Stop 可能在检查与建流之间插入，留下孤立捕获
        lock (_gate)
        {
            if (!IsRunning) throw new InvalidOperationException("引擎未运行");
            if (Mode != ForwardMode.Apps) throw new InvalidOperationException("当前为整系统模式");

            lock (_appGate)
            {
                if (_apps.ContainsKey(pid)) return;
                var ring = new SpScRingBuffer(65536);
                var pipeline = new AppPipeline
                {
                    Name = name,
                    Ring = ring,
                    UserGain = gain,
                    Source = new MixerSource(name, ring, _musicGain * gain),
                    Capture = new ProcessLoopbackCapture(pid, includeTree: true, name),
                    Policy = new RestartPolicy()
                };
                pipeline.Capture.Failed += ex => OnAppCaptureFailed(pid, pipeline, ex);
                _apps[pid] = pipeline;
                _mixer?.AddSource(pipeline.Source);
                pipeline.StartedMs = Environment.TickCount64;
                pipeline.Capture.Start(pipeline.Ring);
                Log.Info($"按应用捕获已添加：{name}(pid={pid})");
            }
        }
    }

    /// <summary>移除按应用转发源</summary>
    public void RemoveApp(int pid)
    {
        lock (_appGate)
        {
            if (!_apps.Remove(pid, out var app)) return;
            app.Stopping = true;
            app.RestartTimer?.Dispose();
            app.Capture.Stop();
            app.Capture.Dispose();
            _mixer?.RemoveSource(app.Source);
            Log.Info($"按应用捕获已移除：{app.Name}(pid={pid})");
        }
    }

    /// <summary>设置音乐增益（System 模式的系统源与 Apps 模式的全部应用源统一生效）。
    /// 旧名保留以兼容现有调用方；麦克风有独立增益，不受影响。
    /// 不取 _gate：滑条拖动可能在引擎停止（持有 _gate 等 Join）时被阻塞。</summary>
    public void SetSystemGain(float gain)
    {
        _musicGain = gain;
        if (_systemSource is not null) _systemSource.Gain = gain;
        lock (_appGate)
        {
            foreach (var app in _apps.Values) app.Source.Gain = gain * app.UserGain;
        }
    }

    // ------------------------------------------------------------------ 麦克风混音（M4）

    /// <summary>开启麦克风混音（运行中可调）：真实麦克风与音乐叠加进转发流。
    /// micDeviceId=null 用系统默认麦克风。</summary>
    public void EnableMic(string? micDeviceId, float gain)
    {
        lock (_gate)
        {
            if (!IsRunning) throw new InvalidOperationException("引擎未运行");
            if (_micCapture is not null)
            {
                _micSource!.Gain = gain; // 已开启：仅更新增益
                return;
            }

            _micDeviceId = micDeviceId;
            _micSource = new MixerSource("麦克风", new SpScRingBuffer(65536), gain);
            _mixer?.AddSource(_micSource);
            _micCapture = new MicrophoneCapture(micDeviceId);
            _micCapture.RecordingStoppedUnexpectedly += OnMicCaptureFailed;
            _micStartedMs = Environment.TickCount64;
            _micCapture.Start(_micSource.Buffer);
            Log.Info($"麦克风混音已启用：device={micDeviceId ?? "默认"}，gain={gain}");
        }
    }

    /// <summary>关闭麦克风混音（运行中可调）</summary>
    public void DisableMic()
    {
        lock (_gate)
        {
            if (_micCapture is null) return;
            var cap = _micCapture;
            var src = _micSource;
            _micCapture = null;
            _micSource = null;
            if (src is not null) _mixer?.RemoveSource(src);
            cap.Stop();
            cap.Dispose();
            Log.Info("麦克风混音已关闭");
        }
    }

    public void SetMicGain(float gain)
    {
        if (_micSource is not null) _micSource.Gain = gain;
    }

    private void OnMicCaptureFailed(Exception ex)
    {
        if (!IsRunning || _micSource is null || _micCapture is null) return;
        if (Environment.TickCount64 - _micStartedMs >= StableRunMs) _micPolicy.Reset();
        if (!_micPolicy.ShouldRestart())
        {
            // 超限：关闭麦克风混音（转发继续，音乐不受影响）
            Info?.Invoke("麦克风捕获连续失败已停止");
            SourceDropped?.Invoke("麦克风", "连续失败 3 次");
            DisableMic();
            return;
        }

        var failed = _micCapture;
        _micCapture = null;
        if (failed is not null) Task.Run(failed.Dispose);

        Task.Delay(RestartPolicy.RestartDelay).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (!IsRunning || _micCapture is not null || _micSource is null) return;
                _micCapture = new MicrophoneCapture(_micDeviceId);
                _micCapture.RecordingStoppedUnexpectedly += OnMicCaptureFailed;
                _micStartedMs = Environment.TickCount64;
                _micCapture.Start(_micSource.Buffer);
                Log.Info("麦克风捕获已重启");
            }
        }, TaskScheduler.Default);
    }

    public float GetAppRms(int pid)
    {
        lock (_appGate)
        {
            return _apps.TryGetValue(pid, out var a) ? a.Source.LastRms : 0f;
        }
    }

    /// <summary>所有按应用源的最大 RMS（供电平显示轮询，无任何分配）。
    /// 取代"先取 PID 数组再 LINQ Max"的每次轮询分配写法。</summary>
    public float GetMaxAppRms()
    {
        lock (_appGate)
        {
            var max = 0f;
            foreach (var a in _apps.Values)
            {
                var r = a.Source.LastRms;
                if (r > max) max = r;
            }
            return max;
        }
    }

    /// <summary>设置指定应用的独立增益（F6：运行中实时生效，与总音乐增益相乘）。
    /// pid 不存在则忽略。UI 的每应用音量滑条接线于此。</summary>
    public void SetAppGain(int pid, float gain)
    {
        lock (_appGate)
        {
            if (_apps.TryGetValue(pid, out var a))
            {
                a.UserGain = gain;
                a.Source.Gain = _musicGain * gain;
            }
        }
    }

    // ------------------------------------------------------------------ 异常恢复（§5.6）

    private void OnSystemCaptureFailed(Exception ex)
    {
        if (!IsRunning || _systemSource is null) return;

        if (Environment.TickCount64 - _systemStartedMs >= StableRunMs) _systemPolicy.Reset();
        if (!_systemPolicy.ShouldRestart())
        {
            Info?.Invoke("整系统捕获连续失败已停止");
            SourceDropped?.Invoke("系统声音", "连续失败 3 次");
            var dead = _systemCapture;
            _systemCapture = null;
            if (dead is not null) Task.Run(dead.Dispose);
            return;
        }

        // 置空失败实例（续体据此判断可重建）；已停止的实例异步释放，避免在回调线程上死锁
        var failed = _systemCapture;
        _systemCapture = null;
        if (failed is not null) Task.Run(failed.Dispose);

        Task.Delay(RestartPolicy.RestartDelay).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (!IsRunning || _systemCapture is not null || _systemSource is null) return;
                _systemCapture = new SystemLoopbackCapture(_systemCaptureDeviceId);
                _systemCapture.RecordingStoppedUnexpectedly += e2 => OnSystemCaptureFailed(e2);
                _systemStartedMs = Environment.TickCount64;
                _systemCapture.Start(_systemSource.Buffer);
                Log.Info("整系统捕获已重启");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>捕获实例运行满此时长后的失败，视为新的一系列失败（重启计数重新开始）。
    /// 激活/流失败是异步的（Start 成功返回后仍可能在 1s 内报错），不能在 Start 后立即 Reset。</summary>
    private const long StableRunMs = 10_000;

    private void OnAppCaptureFailed(int pid, AppPipeline app, Exception ex)
    {
        if (app.Stopping || !IsRunning) return;
        if (Environment.TickCount64 - app.StartedMs >= StableRunMs) app.Policy.Reset();
        if (!app.Policy.ShouldRestart())
        {
            lock (_appGate)
            {
                if (_apps.Remove(pid, out var removed))
                {
                    _mixer?.RemoveSource(removed.Source);
                    removed.Capture.Dispose();
                }
            }
            SourceDropped?.Invoke(app.Name, "捕获连续失败 3 次");
            return;
        }

        app.RestartTimer?.Dispose();
        app.RestartTimer = new System.Threading.Timer(_ =>
        {
            // 与 RemoveApp/Stop（持 _appGate 置 Stopping/释放 Capture）互斥，
            // 防止替换 app.Capture 时与释放并发 → ObjectDisposedException
            lock (_appGate)
            {
                if (app.Stopping || !IsRunning) return;
                try
                {
                    app.Capture.Dispose();
                    app.Capture = new ProcessLoopbackCapture(pid, includeTree: true, app.Name);
                    app.Capture.Failed += e2 => OnAppCaptureFailed(pid, app, e2);
                    app.StartedMs = Environment.TickCount64;
                    app.Capture.Start(app.Ring);
                    Log.Info($"按应用捕获已重启：{app.Name}(pid={pid})");
                }
                catch (Exception e3)
                {
                    Log.Error($"按应用捕获重启失败：{app.Name}(pid={pid})", e3);
                }
            }
        }, null, RestartPolicy.RestartDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnOutputFailed(bool isTarget, Exception ex)
    {
        if (!IsRunning) return;
        Log.Error($"{(isTarget ? "目标" : "监听")}输出失败", ex);

        if (isTarget)
        {
            // §5.6：目标输出流失败 → 引擎整体停止（转发已不可用）。
            // 不能在 WASAPI 播放回调线程上同步 Stop（Stop 会 Join 播放线程自身）→ 投递线程池，
            // 停止完成后再通知 UI，保证 UI 刷新时 IsRunning 已是 false。
            var reason = $"目标输出不可用：{ex.Message}";
            Task.Run(() =>
            {
                Stop();
                EngineFailed?.Invoke(reason);
            });
        }
        else
        {
            // 监听输出失败 → 关闭监听，转发不受影响。同样避免在播放回调线程上同步停止。
            var dead = Interlocked.Exchange(ref _monitorOut, null);
            var deadRing = Interlocked.Exchange(ref _monitorRing, null);
            // 环一起摘掉：否则混音线程会一直判定"监听环饥饿"，对无人读取的环空转写入
            if (deadRing is not null) _mixer?.RemoveOutputRing(deadRing);
            MonitorDeviceName = null;
            if (dead is not null) Task.Run(dead.Dispose);
            Info?.Invoke("监听输出失败，已自动关闭监听");
        }
    }

    // ------------------------------------------------------------------ 设备热插拔（§6）

    private System.Threading.Timer? _deviceRebuildTimer;

    private void OnDeviceChanged(DeviceService.DeviceChangeKind kind, string deviceId)
    {
        if (!IsRunning) return;
        Log.Info($"设备变化：{kind} {deviceId}");
        Info?.Invoke($"设备变化：{kind}");

        // 事件可能在短时间连发 → 500ms 防抖后重建输出流（§6 自动重建）。
        // 建表必须与 Stop 互斥（Stop 持 _gate 退订并释放定时器）：否则一个在退订之前
        // 就已派发的事件会在 Stop 置空之后重新建出定时器，泄漏照旧。
        lock (_gate)
        {
            if (!IsRunning) return;
            _deviceRebuildTimer?.Dispose();
            _deviceRebuildTimer = new System.Threading.Timer(
                _ => RebuildOutputs(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private void RebuildOutputs()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            try
            {
                // 目标设备失效 → 落到默认目标（§6：气泡提示"可能不再是虚拟输入端"）
                var currentTargetOk = false;
                if (_targetOut is not null && _targetDeviceId is not null)
                {
                    using var dev = DeviceService.GetDeviceById(_targetDeviceId);
                    currentTargetOk = dev is not null;
                }
                if (!currentTargetOk)
                {
                    var newTarget = ResolveDefaultTarget();
                    _targetDeviceId = newTarget.ID;
                    TargetDeviceName = newTarget.FriendlyName;
                    _targetOut?.Stop();
                    _targetOut?.Dispose();
                    _targetOut = new OutputWriter(_targetRing!, LatencyMs);
                    _targetOut.PlaybackFailed += ex => OnOutputFailed(true, ex);
                    _targetOut.Start(newTarget);
                    Info?.Invoke($"目标已切换到 {TargetDeviceName}，可能不再是虚拟输入端");
                }
            }
            catch (Exception ex)
            {
                Log.Error("输出重建失败", ex);
            }
        }
    }

    // ------------------------------------------------------------------ 混音线程

    private void MixerLoop()
    {
        var token = _cts!.Token;
        // 混音节拍（§4.1）：按输出环深度自 pacing（fill-on-demand）。
        // 目标深度 2 个量化块（20ms）：输出每次取走 1 块，余 1 块吸收线程抢占。
        // 原为 3 块（30ms）——那 10ms 是纯排队延迟：输出周期同为 10ms，多出的块对稳定性
        // 没有贡献，只让链路慢了一拍。
        // 不用固定 10ms 睡眠——Thread.Sleep 默认粒度 15.6ms（Win10 2004+ 定时器精度
        // 进程私有后无法依赖），会使产能跌至消费速率 64% → 输出环枯竭持续欠载。
        // 进程启动时 timeBeginPeriod(1) 已将定时精度提升到 1ms。
        const int TargetDepth = Mixer.QuantumSamples * 2;
        var lastDiag = Environment.TickCount64;
        while (!token.IsCancellationRequested)
        {
            // 快照局部引用：Stop 会置空字段（Join(500) 超时时本线程可能仍在跑），避免 NRE
            var mixer = _mixer;
            var targetRing = _targetRing;
            if (mixer is null || targetRing is null) return;

            var monitorRing = _monitorRing;
            var targetHungry = targetRing.Count < TargetDepth;
            var monitorHungry = monitorRing is not null && monitorRing.Count < TargetDepth;
            if (targetHungry || monitorHungry)
            {
                // TryProcess：活跃源没攒够整块时返回 false（不产出、不补零），1ms 后重试。
                // 这一步是音质关键——先前的 Process 会在半个包上补零，把音频流切出静音空洞。
                mixer.TryProcess();
                Thread.Sleep(1);
            }
            else Thread.Sleep(2);

            // 每 10s 一条链路诊断：环深度即真实排队延迟；补零/溢出为 0 才算健康
            var now = Environment.TickCount64;
            if (now - lastDiag >= 10_000)
            {
                lastDiag = now;
                var ms = targetRing.Count * 1000 / (EngineFormat.SampleRate * EngineFormat.SamplesPerFrame);
                Log.Info($"混音诊断：目标环 {ms}ms，强制补零 {mixer.ForcedSilentBlocks} 块，环溢出 {mixer.OutputOverflowCount} 次");
            }
        }
    }

    private static MMDevice ResolveDefaultTarget()
    {
        var info = DeviceService.GetDefaultTarget()
                   ?? throw new InvalidOperationException("没有可用的播放设备");
        var d = DeviceService.GetDeviceById(info.Id);
        return d ?? throw new InvalidOperationException($"设备已不可用：{info.FriendlyName}");
    }

    public void Dispose() => Stop();
}
