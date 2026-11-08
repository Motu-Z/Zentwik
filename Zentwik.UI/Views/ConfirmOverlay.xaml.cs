using System;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>
/// 全局危险操作确认叠层。窗口级只挂一份，状态由 <see cref="ConfirmService"/> 提供。
/// </summary>
public partial class ConfirmOverlay : UserControl
{
    public ConfirmOverlay()
    {
        // 必须在 InitializeComponent 之前注入：
        // XAML 解析时就要能求值 Visibility，绑不上会让遮罩常显、整页点不动。
        DataContext = ConfirmService.Instance;
        InitializeComponent();

        ConfirmService.Instance.PropertyChanged += OnServicePropertyChanged;
    }

    /// <summary>
    /// 叠层打开时把焦点移进卡片 —— 否则焦点还留在触发按钮上，
    /// ESC 的按键事件不会经过本叠层。
    /// </summary>
    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfirmService.IsOpen) || !ConfirmService.Instance.IsOpen)
        {
            return;
        }

        // 异步：打开那一刻视觉树可能还没完成布局。
        Dispatcher.BeginInvoke(
            new Action(() => CancelButton.Focus()),
            DispatcherPriority.Input);
    }

    private void OnRootKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !ConfirmService.Instance.IsOpen)
        {
            return;
        }

        ConfirmService.Instance.CancelCommand.Execute(null);
        e.Handled = true;
    }
}
