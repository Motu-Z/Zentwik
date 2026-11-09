using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Zentwik.UI.Views;

namespace Zentwik.UI.Services;

/// <summary>
/// 托盘菜单的宿主与定位。
/// </summary>
/// <remarks>
/// 托盘右键菜单已改为 TrayIconService 里的 TrackPopupMenuEx（系统原生），
/// 不再需要 WPF 自绘菜单，也就不需要这个 0x1 宿主窗口。
/// 本类与 Views/TrayMenu.xaml 目前没有任何引用点。
///
/// 下面这段说明是自绘方案当时的取舍，留作记录：
///
/// WPF 的 Popup 依附于宿主窗口：宿主一旦隐藏或最小化，Popup 会跟着消失。
/// 而托盘菜单恰恰要在「主窗口已收进托盘（被 Hide）」的状态下弹出，
/// 这是直接冲突。
///
/// 所以这里常驻一个 1x1 的透明窗口，放在屏幕外（-32000, -32000），
/// 肉眼完全不可见，但它给 Popup 提供了一个永远存在的 owner，
/// 于是主窗口收不收进托盘都不影响菜单弹出。
///
/// Placement=AbsolutePoint 时，HorizontalOffset / VerticalOffset
/// 就是屏幕物理像素坐标 —— 与托盘图标所在显示器无关，
/// 多屏场景下不会跑到另一块屏。
/// </remarks>
public sealed class TrayMenuPresenter : IDisposable
{
    private readonly Window _host;
    private readonly TrayMenu _menu;

    public TrayMenuPresenter()
    {
        _menu = new TrayMenu();

        // 菜单项的命令往上抛一层
        _menu.ShowRequested += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _menu.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _host = new Window
        {
            Title = "ZentwikTrayMenuHost",
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent,
        };

        _host.Content = _menu;
        _host.Show();
    }

    /// <summary>请求显示主窗口。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>请求退出应用。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// 在鼠标所在的屏幕坐标处弹出菜单。
    /// </summary>
    /// <param name="screenPoint">鼠标的屏幕坐标（物理像素）。</param>
    public void Open(Point screenPoint)
    {
        var size = _menu.MeasureMenuSize();
        var work = GetWorkArea(screenPoint);

        double x = screenPoint.X;
        double y = screenPoint.Y;

        // 托盘图标在屏幕右下角，菜单默认向右下展开会跑出屏幕 ——
        // 越界时改为向左 / 向上展开。
        if (x + size.Width > work.Right) x = screenPoint.X - size.Width;
        if (y + size.Height > work.Bottom) y = screenPoint.Y - size.Height;

        // 最后再夹一次，保证菜单完整落在工作区里
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        _menu.Open(x, y);
    }

    /// <summary>
    /// 取鼠标所在显示器的工作区（排除任务栏）。
    /// </summary>
    private static Rect GetWorkArea(Point screenPoint)
    {
        var pt = new NativeMethods.POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };

        IntPtr monitor = NativeMethods.MonitorFromPoint(
            pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);
        }

        // 兜底：主显示器工作区
        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);
    }

    public void Dispose()
    {
        _host.Close();
    }
}
