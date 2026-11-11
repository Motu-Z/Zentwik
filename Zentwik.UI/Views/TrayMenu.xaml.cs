using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Zentwik.UI.Views;

/// <summary>托盘右键菜单（自绘）</summary>
public partial class TrayMenu : UserControl
{
    public TrayMenu()
    {
        InitializeComponent();
    }

    /// <summary>请求显示主窗口。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>请求退出应用。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>在屏幕坐标处弹出菜单</summary>
    public void Open(double screenX, double screenY)
    {
        // Popup 的定位偏移是**Popup 窗口**的左上角。
        Popup.HorizontalOffset = screenX + Panel.Margin.Left;
        Popup.VerticalOffset = screenY + Panel.Margin.Top;
        Popup.IsOpen = true;

        // 把键盘焦点交给第一个菜单项。
        Dispatcher.BeginInvoke(new Action(() => ShowButton.Focus()));
    }

    /// <summary>ESC 关闭菜单</summary>
    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        Close();
        e.Handled = true;
    }

    /// <summary>量出菜单实际占位（含四周留白），供定位时避开屏幕边缘</summary>
    public Size MeasureMenuSize()
    {
        Panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(Panel.DesiredSize.Width, Panel.DesiredSize.Height);
    }

    /// <summary>关闭菜单。</summary>
    public void Close()
    {
        Popup.IsOpen = false;
    }

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        Close();
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
