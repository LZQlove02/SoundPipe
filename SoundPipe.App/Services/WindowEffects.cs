using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SoundPipe.App.Services;

/// <summary>
/// 窗口合成效果：深色标题栏 + 系统背景材质（Mica）+ 圆角（Windows 11）。
/// 玻璃质感交给系统合成器完成（不是自绘半透明），因此不损失清晰度、不额外耗电。
/// 任意一步失败都静默回退到 XAML 中的深色渐变背景，功能不受影响。
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmcpRound = 2;
    private const int DwmsbtMainWindow = 2; // Mica

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>应用系统材质；返回是否成功透出系统背景（成功时把窗口底色让给合成器）。</summary>
    public static bool TryApply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // Mica 需要 Windows 11；更老的系统保留 XAML 的深色渐变背景
            var build = Environment.OSVersion.Version.Build;
            if (build < 22000) return false;

            var dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var corner = DwmcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            var backdrop = DwmsbtMainWindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
                return false;

            // WPF 关键一步：仅把 Window.Background 设为透明还不够，
            // 合成目标（HwndTarget）自身仍会用不透明色填充客户区，结果是一片黑。
            // 必须把 CompositionTarget.BackgroundColor 也置为透明，系统材质才能透出来。
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is null) return false;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

            // 让出窗口底色，透出系统材质；卡片等表面仍是半透明，形成玻璃层次
            window.Background = Brushes.Transparent;
            return true;
        }
        catch
        {
            return false; // 任意失败：保持 XAML 背景
        }
    }
}