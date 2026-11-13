using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using SoundPipe.App.Services;
using SoundPipe.Core;
using SoundPipe.Core.Audio;
using SoundPipe.Core.Devices;
using SoundPipe.Core.Diagnostics;
using SoundPipe.Core.Session;

namespace SoundPipe.App;

/// <summary>
/// 主窗口（重建版）：模式/设备/参数区、麦克风混音、三电平表、发声应用列表、
/// 一键配置/链路诊断、开机自启、托盘常驻、首次引导。
/// 线程约定：引擎/监控回调经 Dispatcher 切回 UI；电平表由 UI 定时器主动拉取。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>下拉框统一条目（Id=null 表示伪条目，如"(无)"）</summary>
    public sealed record ComboItem(string? Id, string Name);

    /// <summary>发声应用行（运行状态用矢量色点 + 文字呈现，不依赖颜色或字形图标）</summary>
    public sealed class AppRow : INotifyPropertyChanged
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string PidText { get; init; } = "";
        public bool IsActive { get; init; }

        private bool _checked;
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
            }
        }

        private double _gain = 1.0;
        /// <summary>每应用独立音量（F6，相对总音乐增益）</summary>
        public double Gain
        {
            get => _gain;
            set
            {
                if (Math.Abs(_gain - value) < 1e-6) return;
                _gain = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gain)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private const string RunKeyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SoundPipe";

    private readonly AudioEngine _engine = new();
    private readonly SessionMonitor _monitor = new();
    private readonly DispatcherTimer _levelTimer;
    private readonly HashSet<int> _checkedPids = new();
    private readonly HashSet<string> _lastAppNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> _appGains = new(StringComparer.OrdinalIgnoreCase);
    private UserSettings _settings = new();
    private IReadOnlyList<AppSessionInfo> _lastSnapshot = new List<AppSessionInfo>();
    private bool _initialized;          // XAML 初始化期间忽略控件事件
    private bool _rebuildingApps;       // 重建应用列表期间忽略勾选事件
    private bool _forceExit;           // 托盘"退出"= 真退出；点 X = 收到托盘
    private bool _balloonShown;
    private bool _autoSetupDone;       // 首次点击"开始转发"时才做系统默认设备配置

    public MainWindow()
    {
        InitializeComponent();
        _initialized = true;

        // 引擎事件 → 状态栏
        _engine.Info += msg => Dispatcher.BeginInvoke(() => SetStatus(msg));
        _engine.EngineFailed += reason => Dispatcher.BeginInvoke(() =>
        {
            SetStatus($"引擎已停止：{reason}");
            RefreshRunState();
        });
        _engine.SourceDropped += (name, reason) => Dispatcher.BeginInvoke(() =>
            SetStatus($"源 [{name}] 已放弃：{reason}"));

        // 会话监控（持续运行，驱动应用列表）
        _monitor.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(() => OnSnapshot(snapshot));

        // 加载持久化设置（须在监控启动前，便于首次快照按名恢复勾选）
        _settings = UserSettings.Load();
        _lastAppNames = _settings.LastAppNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedGains = _settings.AppGains;
        _appGains = savedGains is not null
            ? new Dictionary<string, double>(savedGains, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        _monitor.Start();

        // 设备热插拔 → 刷新下拉
        DeviceService.DeviceChanged += (_, _) =>
            Dispatcher.BeginInvoke(RefreshDevices, DispatcherPriority.Background);

        // 电平表 150ms
        _levelTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _levelTimer.Tick += (_, _) => UpdateLevels();
        _levelTimer.Start();

        RefreshDevices();
        ApplySettings();          // 设备列表填充后，用持久化值覆盖默认选择
        RefreshAutoRunState();
        SetStatus("就绪");
    }

    /// <summary>窗口句柄就绪后应用系统合成效果：深色标题栏 + Mica 背景材质 + 圆角（失败则保留 XAML 背景）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.TryApply(this);
    }

    // ------------------------------------------------------------ 设备与列表

    private void RefreshDevices()
    {
        // 转发目标：渲染设备（虚拟输入端排前，枚举已排序）
        try
        {
            var renders = DeviceService.EnumerateRenderDevices();
            var targetItems = renders.Select(d => new ComboItem(d.Id, d.FriendlyName)).ToList();
            var keepTarget = SelectedId(TargetCombo);
            TargetCombo.ItemsSource = targetItems;
            SelectById(TargetCombo, keepTarget, targetItems);

            // 监听输出：(无) + 全部渲染设备
            var monitorItems = new List<ComboItem> { new(null, "(无)") };
            monitorItems.AddRange(renders.Select(d => new ComboItem(d.Id, d.FriendlyName)));
            var keepMonitor = SelectedId(MonitorCombo);
            MonitorCombo.ItemsSource = monitorItems;
            SelectById(MonitorCombo, keepMonitor, monitorItems);

            RefreshGuideVisibility();
        }
        catch { /* 设备枚举瞬时失败：保留旧列表 */ }

        // 麦克风：(系统默认) + 物理录音端点（过滤虚拟声卡防回环）
        try
        {
            var mics = new List<ComboItem> { new(null, "(系统默认) 麦克风") };
            mics.AddRange(DeviceService.EnumerateCaptureDevices()
                .Where(d => !DeviceService.LooksLikeVirtualCapture(d.FriendlyName))
                .Select(d => new ComboItem(d.Id, d.FriendlyName)));
            var keepMic = SelectedId(MicCombo);
            MicCombo.ItemsSource = mics;
            SelectById(MicCombo, keepMic, mics);
        }
        catch { }
    }

    private static string? SelectedId(ComboBox combo) =>
        combo.SelectedItem is ComboItem item ? item.Id : null;

    private static void SelectById(ComboBox combo, string? id, IReadOnlyList<ComboItem> items)
    {
        combo.SelectedItem = items.FirstOrDefault(i => i.Id == id)
                             ?? items.FirstOrDefault(i => i.Id is not null)
                             ?? items.FirstOrDefault();
    }

    private void RefreshGuideVisibility()
    {
        var hasVirtual = TargetCombo.Items.OfType<ComboItem>()
            .Any(i => i.Id is not null &&
                      DeviceService.LooksLikeVirtualInputEnd(i.Name));
        GuidePanel.Visibility = hasVirtual ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>将持久化设置应用到 UI 控件（设备列表填充后调用）。</summary>
    private void ApplySettings()
    {
        SystemRadio.IsChecked = _settings.SystemMode;
        AppsRadio.IsChecked = !_settings.SystemMode;
        LatencySlider.Value = _settings.LatencyMs;
        GainSlider.Value = _settings.Gain;
        MicCheck.IsChecked = _settings.MicEnabled;
        MicPanel.IsEnabled = _settings.MicEnabled;
        MicPanel.Visibility = _settings.MicEnabled ? Visibility.Visible : Visibility.Collapsed;
        MicSlider.Value = _settings.MicGain;
        SelectById(TargetCombo, _settings.TargetDeviceId, TargetCombo.Items.OfType<ComboItem>().ToList());
        SelectById(MonitorCombo, _settings.MonitorDeviceId, MonitorCombo.Items.OfType<ComboItem>().ToList());
        SelectById(MicCombo, _settings.MicDeviceId, MicCombo.Items.OfType<ComboItem>().ToList());

        // 上次保存的增益若超过 1.0，启动即提示——这是"发闷/失真"最常见的来源
        if (_settings.Gain > 1.05)
            SetStatus($"上次音乐增益为 {_settings.Gain:0.00} x（{20 * Math.Log10(_settings.Gain):+0.0;-0.0;0.0} dB），会压限失真；建议调回 1.00");
    }

    /// <summary>收集当前 UI 状态并持久化（退出前调用）。</summary>
    private void SaveSettings()
    {
        _settings.SystemMode = SystemRadio.IsChecked == true;
        _settings.LatencyMs = LatencySlider.Value;
        _settings.Gain = GainSlider.Value;
        _settings.MicEnabled = MicCheck.IsChecked == true;
        _settings.MicGain = MicSlider.Value;
        _settings.TargetDeviceId = SelectedId(TargetCombo);
        _settings.MonitorDeviceId = SelectedId(MonitorCombo);
        _settings.MicDeviceId = SelectedId(MicCombo);
        // 按 PID 查当前进程名存档（PID 重启失效，名更稳定）
        _settings.LastAppNames = _lastSnapshot
            .Where(s => _checkedPids.Contains(s.Pid))
            .Select(s => s.ProcessName)
            .Distinct()
            .ToList();
        _settings.AppGains = new Dictionary<string, double>(_appGains);
        _settings.Save();
    }

    private void OnSnapshot(IReadOnlyList<AppSessionInfo> snapshot)
    {
        _lastSnapshot = snapshot;
        PruneDeadPids();
        _rebuildingApps = true;
        try
        {
            var rows = snapshot.Select(s =>
            {
                // 按 PID 或 进程名 恢复勾选（重启后按名自动勾回 QQMusic 等）
                var isChecked = _checkedPids.Contains(s.Pid) || _lastAppNames.Contains(s.ProcessName);
                if (isChecked) _checkedPids.Add(s.Pid);
                return new AppRow
                {
                    Pid = s.Pid,
                    Name = s.ProcessName,
                    PidText = $"pid {s.Pid}",
                    IsActive = s.IsActive,
                    Checked = isChecked,
                    Gain = GetAppGain(s.ProcessName)
                };
            }).ToList();
            AppsList.ItemsSource = new ObservableCollection<AppRow>(rows);
            NoAppsHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _rebuildingApps = false;
        }
    }

    /// <summary>移除已退出的 PID：避免 PID 被系统复用后自动勾选/转发到无关进程。</summary>
    private void PruneDeadPids()
    {
        foreach (var pid in _checkedPids.ToList())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
            }
            catch
            {
                _checkedPids.Remove(pid);
            }
        }
    }

    /// <summary>按进程名取持久化的每应用音量（F6），未设置过则 1.0</summary>
    private double GetAppGain(string? name) =>
        name is not null && _appGains.TryGetValue(name, out var g) ? g : 1.0;

    // ------------------------------------------------------------ 启停

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 首次开始转发时才执行自动配置：避免"只是打开界面"就被改掉系统默认麦克风/输出。
            // 幂等；失败不阻断启动。
            if (!_autoSetupDone)
            {
                _autoSetupDone = true;
                SetStatus("正在自动配置…");
                try
                {
                    SetStatus(AutoSetup.Run().Message);
                    RefreshDevices();
                }
                catch (Exception ex)
                {
                    SetStatus($"自动配置失败：{ex.Message}");
                }
            }

            var mode = AppsRadio.IsChecked == true
                ? AudioEngine.ForwardMode.Apps
                : AudioEngine.ForwardMode.System;

            MMDevice? target = null, monitor = null;
            try
            {
                var targetId = SelectedId(TargetCombo);
                target = targetId is null ? null : DeviceService.GetDeviceById(targetId);
                var monitorId = SelectedId(MonitorCombo);
                monitor = monitorId is null ? null : DeviceService.GetDeviceById(monitorId);

                _engine.Start(mode, target, monitor,
                    (int)LatencySlider.Value, (float)GainSlider.Value);

                if (MicCheck.IsChecked == true)
                {
                    var micId = SelectedId(MicCombo);
                    _engine.EnableMic(micId, (float)MicSlider.Value);
                }

                if (mode == AudioEngine.ForwardMode.Apps)
                {
                    PruneDeadPids(); // 避免对已退出（可能被复用）的 PID 建流
                    var names = _lastSnapshot.ToDictionary(s => s.Pid, s => s.ProcessName);
                    foreach (var pid in _checkedPids.ToList())
                    {
                        var name = names.GetValueOrDefault(pid, $"pid:{pid}");
                        _engine.AddApp(pid, name, (float)GetAppGain(name));
                    }
                }

                SetStatus("转发中…");
            }
            finally
            {
                target?.Dispose();
                monitor?.Dispose();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败：{ex.Message}");
        }
        RefreshRunState();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        StopEngine();
        SetStatus("已停止");
        RefreshRunState();
    }

    private void StopEngine()
    {
        try { _engine.Stop(); } catch { }
    }

    private void RefreshRunState()
    {
        var running = _engine.IsRunning;
        StartBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        SystemRadio.IsEnabled = !running;
        AppsRadio.IsEnabled = !running;
        TargetCombo.IsEnabled = !running;
        MonitorCombo.IsEnabled = !running;
        LatencySlider.IsEnabled = !running;

        // 运行状态胶囊：色点 + 文字（文字本身已表意，颜色只做强化）
        RunDot.Fill = (Brush)FindResource(running ? "AccentBrush" : "TextTertiaryBrush");
        RunStateLabel.Text = running ? "转发中" : "待机";
        RunStateLabel.Foreground = (Brush)FindResource(running ? "AccentTextBrush" : "TextSecondaryBrush");

        UpdateLevels();
    }

    // ------------------------------------------------------------ 控件事件

    private void LatencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        LatencyLabel.Text = $"缓冲延迟：{(int)LatencySlider.Value} ms";
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        var g = GainSlider.Value;
        var db = g < 0.01 ? "-∞ dB" : $"{20 * Math.Log10(g):+0.0;-0.0;0.0} dB";
        GainLabel.Text = $"音乐增益：{g:0.00} x（{db}）";
        // 数字域放大不产生响度，只会把峰值推入软限幅区（-1dBFS 起压）→ 动态被压扁、听感发闷。
        // 超过 1.0 必须显式警示，否则用户会把"不够响"误判为软件问题而一路拉满。
        if (g > 1.05)
        {
            GainHint.Text = "已超过 1.00：峰值会被压限，声音发闷/失真。要更响请调高系统音量或通话软件音量";
            GainHint.Foreground = (Brush)FindResource("WarningBrush");
        }
        else
        {
            GainHint.Text = "运行中实时生效";
            GainHint.Foreground = (Brush)FindResource("TextTertiaryBrush");
        }
        if (_engine.IsRunning) _engine.SetSystemGain((float)g);
    }

    private void MicCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var micOn = MicCheck.IsChecked == true;
        MicPanel.IsEnabled = micOn;
        MicPanel.Visibility = micOn ? Visibility.Visible : Visibility.Collapsed; // 渐进披露：关闭时不占版面
        if (_engine.IsRunning)
        {
            try
            {
                if (MicCheck.IsChecked == true)
                    _engine.EnableMic(SelectedId(MicCombo), (float)MicSlider.Value);
                else
                    _engine.DisableMic();
            }
            catch (Exception ex)
            {
                SetStatus($"麦克风混音切换失败：{ex.Message}");
            }
        }
    }

    private void MicSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        MicGainLabel.Text = $"麦克风增益：{MicSlider.Value:0.00} x（说话声大小，与音乐独立）";
        if (_engine.IsRunning && _engine.MicEnabled) _engine.SetMicGain((float)MicSlider.Value);
    }

    private void AppCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_rebuildingApps || !_initialized) return;
        if (sender is not CheckBox { Tag: int pid }) return;
        var check = ((CheckBox)sender).IsChecked == true;
        var appName = _lastSnapshot.FirstOrDefault(s => s.Pid == pid)?.ProcessName;

        if (check)
        {
            _checkedPids.Add(pid);
            if (!string.IsNullOrEmpty(appName)) _lastAppNames.Add(appName);
        }
        else
        {
            _checkedPids.Remove(pid);
            // 仅当同名进程已无任何勾选时才移除"按名恢复"，否则下一次快照会按旧名字
            // 把它重新勾回来（表现为"取消勾选无效"）。
            var stillChecked = appName is not null && _lastSnapshot.Any(s =>
                s.Pid != pid && _checkedPids.Contains(s.Pid) &&
                string.Equals(s.ProcessName, appName, StringComparison.OrdinalIgnoreCase));
            if (appName is not null && !stillChecked) _lastAppNames.Remove(appName);
        }

        if (_engine.IsRunning && _engine.Mode == AudioEngine.ForwardMode.Apps)
        {
            try
            {
                if (check)
                    _engine.AddApp(pid, appName ?? $"pid:{pid}", (float)GetAppGain(appName));
                else
                    _engine.RemoveApp(pid);
            }
            catch (Exception ex)
            {
                SetStatus($"应用切换失败：{ex.Message}");
            }
        }
    }

    /// <summary>每应用音量滑条（F6）：即时生效并持久化（键为进程名，PID 重启失效）</summary>
    private void AppGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_rebuildingApps || !_initialized) return;
        if (sender is not Slider { Tag: int pid }) return;

        var appName = _lastSnapshot.FirstOrDefault(s => s.Pid == pid)?.ProcessName;
        if (appName is not null) _appGains[appName] = e.NewValue;

        if (_engine.IsRunning && _engine.Mode == AudioEngine.ForwardMode.Apps)
            _engine.SetAppGain(pid, (float)e.NewValue);
    }

    private void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"打开浏览器失败：{ex.Message}");
        }
    }

    private void SetupBtn_Click(object sender, RoutedEventArgs e)
    {
        SetupBtn.IsEnabled = false;
        SetStatus("正在一键配置…");
        Task.Run(AutoSetup.Run).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                SetupBtn.IsEnabled = true;
                RefreshDevices();
                SetStatus(t.Result.Message);
            });
        });
    }

    private void DiagBtn_Click(object sender, RoutedEventArgs e)
    {
        var targetId = SelectedId(TargetCombo);
        DiagBtn.IsEnabled = false;
        SetStatus("正在链路诊断（约 5-8 秒，会有一声测试音）…");
        Task.Run(() => LinkDiagnostics.RunCheck(targetId)).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                DiagBtn.IsEnabled = true;
                var r = t.Result;
                SetStatus(r.IsOk ? r.Message : $"诊断：{r.Message}");
                MessageBox.Show(this, r.Message, "链路诊断",
                    MessageBoxButton.OK,
                    r.IsOk ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

    // ------------------------------------------------------------ 电平表

    private void UpdateLevels()
    {
        if (!_engine.IsRunning)
        {
            SourceLevel.Value = 0;
            OutputLevel.Value = 0;
            MicLevel.Value = 0;
            SourceLevelText.Text = "—";
            OutputLevelText.Text = "—";
            MicLevelText.Text = "—";
            LevelHint.Text = "已停止";
            return;
        }

        // 源：系统模式取系统源；按应用模式取已勾选源的最大值（引擎侧求值，不产生每帧数组分配）
        float source = _engine.Mode == AudioEngine.ForwardMode.System
            ? _engine.SystemSourceRms
            : _engine.GetMaxAppRms();
        var mic = _engine.MicEnabled ? _engine.MicSourceRms : 0f;

        SourceLevel.Value = System.Math.Clamp(source * 3f, 0, 1);
        OutputLevel.Value = System.Math.Clamp(_engine.MasterRms * 3f, 0, 1);
        MicLevel.Value = System.Math.Clamp(mic * 3f, 0, 1);

        // 数值读数：由文字承载信息量，颜色/长度只做强化（不靠颜色单独传达）
        SourceLevelText.Text = FormatDb(source);
        OutputLevelText.Text = FormatDb(_engine.MasterRms);
        MicLevelText.Text = _engine.MicEnabled ? FormatDb(mic) : "未启用";
        LevelHint.Text = "每 150 ms 刷新";
    }

    /// <summary>电平读数（dBFS）；低于 -60dB 视为静音</summary>
    private static string FormatDb(float rms) =>
        rms <= 0.0001f ? "静音" : $"{DspMath.ToDb(rms):0.0} dB";

    private void SetStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------ 开机自启

    private static string? ReadAutoRun() =>
        Registry.CurrentUser.OpenSubKey(RunKeyName)?.GetValue(RunValueName) as string;

    private static void WriteAutoRun(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyName, true);
        if (enable)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(RunValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, false);
        }
    }

    private void RefreshAutoRunState()
    {
        AutoRunCheck.IsChecked = ReadAutoRun() is not null;
    }

    private void AutoRun_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        try
        {
            WriteAutoRun(AutoRunCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            SetStatus($"开机自启设置失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------ 托盘与窗口生命周期

    private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _forceExit = true;
        Close();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceExit && _engine.IsRunning)
        {
            // 点 X：收进托盘，转发后台继续
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                TrayIcon.ShowBalloonTip("SoundPipe 仍在运行", "转发继续进行中。双击托盘图标恢复窗口。", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
            return;
        }

        // 真退出：保存设置 → 停引擎、停监控、释放托盘
        SaveSettings();
        _levelTimer.Stop();
        _monitor.Stop();
        StopEngine();
        _engine.Dispose();
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }
}
