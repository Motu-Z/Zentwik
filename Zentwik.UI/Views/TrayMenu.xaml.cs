using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Zentwik.UI.Views;

/// <summary>
/// 托盘右键菜单（自绘）。
/// </summary>
/// <remarks>
/// 外观定义见 TrayMenu.xaml。
/// 宿主的提供与屏幕定位逻辑见 <see cref="Services.TrayMenuPresenter"/>。
/// </remarks>
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

    /// <summary>
    /// 在屏幕坐标处弹出菜单。
    /// </summary>
    /// <param name="screenX">屏幕 X 坐标（物理像素）。</param>
    /// <param name="screenY">屏幕 Y 坐标（物理像素）。</param>
    public void Open(double screenX, double screenY)
    {
        // Popup 的定位偏移是**Popup 窗口**的左上角，
        // 而 Child（面板）带 Margin=10（给阴影留白），
        // WPF 会把面板往内缩 margin —— 于是面板左上角会落在
        // (offset - margin) 处，菜单整体偏左上 10px。
        // 这里把 margin 加回去，保证**面板**左上角正好落在鼠标位置。
        Popup.HorizontalOffset = screenX + Panel.Margin.Left;
        Popup.VerticalOffset = screenY + Panel.Margin.Top;
        Popup.IsOpen = true;

        // 把键盘焦点交给第一个菜单项。
        // 裸 Popup 不会自动接管键盘（ContextMenu 才会），
        // 不给焦点的话：键盘用户无法用 ↑↓ 选择，ESC 也关不掉菜单。
        Dispatcher.BeginInvoke(new Action(() => ShowButton.Focus()));
    }

    /// <summary>
    /// ESC 关闭菜单。
    /// </summary>
    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        Close();
        e.Handled = true;
    }

    /// <summary>
    /// 量出菜单实际占位（含四周留白），供定位时避开屏幕边缘。
    /// </summary>
    /// <remarks>
    /// Popup 打开前没有测量过内容，所以这里手动 Measure 一次。
    /// 留白（Margin）是为阴影预留的 —— 阴影超出 Border 边界，
    /// 不留空间会被 Popup 的边界裁掉。
    /// </remarks>
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
