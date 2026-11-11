using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Zentwik.UI.Views;

namespace Zentwik.UI.Services;

/// <summary>托盘菜单的宿主与定位</summary>
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

    /// <summary>在鼠标所在的屏幕坐标处弹出菜单</summary>
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

    /// <summary>取鼠标所在显示器的工作区（排除任务栏）</summary>
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
