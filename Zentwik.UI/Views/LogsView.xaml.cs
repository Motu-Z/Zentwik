using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>日志页。</summary>
public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewMouseLeftButtonDown += OnSurfacePreviewMouseDown;
    }

    private LogsViewModel? ViewModel => DataContext as LogsViewModel;

    /// <summary>
    /// 点空白处把键盘焦点还回去。
    /// <para>
    /// WPF 点非可聚焦区域不会转移焦点，搜索框的下划线会一直亮着 ——
    /// 看上去像「点了别处也没收起」。
    /// </para>
    /// </summary>
    private void OnSurfacePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideTextBox(e.OriginalSource as DependencyObject))
            return;

        Keyboard.ClearFocus();
    }

    private static bool IsInsideTextBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        ViewModel.StatsStateChanged -= OnStatsStateChanged;
        ViewModel.StatsStateChanged += OnStatsStateChanged;

        SnapStats(ViewModel.IsStatsOpen);

        _ = ViewModel.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.StatsStateChanged -= OnStatsStateChanged;
    }

    private void OnStatsStateChanged(bool open)
    {
        // 诊断模式落终值。
        if (LogsViewModel.SuppressWorkerInit)
        {
            SnapStats(open);
            return;
        }

        double target = open ? LayoutMetrics.LogStatsWidth : 0;

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        StatsHost.BeginAnimation(WidthProperty, animation);
    }

    /// <summary>直接落到终值，跳过过渡。首次显示时用，避免截图拍到中间帧。</summary>
    private void SnapStats(bool open)
    {
        StatsHost.BeginAnimation(WidthProperty, null);
        StatsHost.Width = open ? LayoutMetrics.LogStatsWidth : 0;
    }
}
