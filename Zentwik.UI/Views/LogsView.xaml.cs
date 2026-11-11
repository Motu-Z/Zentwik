using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zentwik.UI.Services;
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

    /// <summary>点空白处把键盘焦点还回去</summary>
    private void OnSurfacePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeAssist.IsInsideTextBox(e.OriginalSource as DependencyObject))
            return;

        Keyboard.ClearFocus();
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
