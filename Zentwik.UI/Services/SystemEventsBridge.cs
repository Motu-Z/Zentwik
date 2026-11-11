using System.Windows;
using System.Windows.Interop;

namespace Zentwik.UI.Services;

/// <summary>系统主题变更监听：直接钩主窗口 WM_SETTINGCHANGE，SystemEvents 对系统深浅色不可靠。</summary>
internal static class SystemEventsBridge
{
    private const uint WM_SETTINGCHANGE = 0x001A;

    private static Action? _callback;

    /// <summary>在指定窗口上挂载系统主题变更监听</summary>
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
