using System.Threading;
using System.Windows;

namespace SoundPipe.App;

/// <summary>应用入口：单实例互斥；主窗口手动创建（点 X 只收托盘，Shutdown 由窗口控制）。</summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static bool _ownsMutex;   // createdNew=true 时当前线程拥有所有权，退出需 ReleaseMutex

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已在运行则激活提示后退出
        _singleInstance = new Mutex(true, "SoundPipe.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;       // createdNew=false 时无所有权，ReleaseMutex 会抛异常
        if (!createdNew)
        {
            MessageBox.Show("SoundPipe 已在运行（可能最小化到托盘）。", "SoundPipe",
                MessageBoxButton.OK, MessageBoxImage.Information);
            // 显式释放（不依赖 OnExit 时序），置 null 让 OnExit 跳过重复释放
            ReleaseSingleInstanceMutex();
            Shutdown();
            return;
        }

        // 点 X 只是隐藏窗口：改为显式 Shutdown，避免窗口隐藏后被判"全部窗口关闭"退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ApplyAccessibilityPreferences();
        base.OnStartup(e);

        var window = new MainWindow();
        window.Show();
    }

    /// <summary>
    /// 无障碍适配（必须在主窗口创建前执行，样式在窗口 XAML 解析时按 StaticResource 落定）：
    /// · 系统「动画效果」开启 → 合并 Themes/Controls.Motion.xaml（同键覆盖，提供 150ms 淡入淡出）；
    ///   关闭 → 不合并，控件保持即时状态切换；
    /// · 高对比度主题 → 最后合并 Themes/HighContrast.xaml，把语义色整体换成 SystemColors。
    /// 任一步失败都不影响启动。
    /// </summary>
    private static void ApplyAccessibilityPreferences()
    {
        try
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("Themes/Controls.Motion.xaml", UriKind.Relative)
                });
            }

            if (SystemParameters.HighContrast)
            {
                Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("Themes/HighContrast.xaml", UriKind.Relative)
                });
            }
        }
        catch
        {
            // 适配失败不阻断启动
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常退出路径释放（createdNew=false 路径已在 OnStartup 显式释放并置 null）
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    /// <summary>释放单实例 Mutex：拥有所有权时先 ReleaseMutex，再 Dispose，最后置 null。
    /// 幂等——多次调用安全（OnStartup 提前退出 + OnExit 都可能调用）。</summary>
    private static void ReleaseSingleInstanceMutex()
    {
        var m = _singleInstance;
        if (m is null) return;
        if (_ownsMutex)
        {
            try { m.ReleaseMutex(); } catch { /* 未拥有所有权时忽略 */ }
        }
        m.Dispose();
        _singleInstance = null;
    }
}
