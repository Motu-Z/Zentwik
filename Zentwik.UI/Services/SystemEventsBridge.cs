using System.Windows;
using System.Windows.Interop;

namespace Zentwik.UI.Services;

/// <summary>
/// 系统主题变更监听桥。
/// </summary>
/// <remarks>
/// Windows 在用户切换系统明暗 / 强调色时，会向顶层窗口广播 WM_SETTINGCHANGE
/// （lParam = "ImmersiveColorSet" / "SystemUsesLightTheme" 等字符串）。
///
/// 1. SystemEvents.UserPreferenceChanged 在部分 Windows 版本上对「系统深浅色」
///    并不可靠；直接钩 WM_SETTINGCHANGE 是确定性最高的做法。
/// 2. 钩主窗口天然发生在 UI 线程，不需要跨线程 marshalling，
///    也不会引入额外的消息窗口与线程生命周期问题。
///
/// 钩子随窗口销毁自动失效，无需显式卸载。
/// </remarks>
internal static class SystemEventsBridge
{
    private const uint WM_SETTINGCHANGE = 0x001A;

    private static Action? _callback;

    /// <summary>
    /// 在指定窗口上挂载系统主题变更监听。
    /// </summary>
    public static void RegisterThemeChanged(Window window, Action callback)
    {
        _callback = callback;

        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == WM_SETTINGCHANGE)
        {
            _callback?.Invoke();
        }
        return IntPtr.Zero;
    }
}
