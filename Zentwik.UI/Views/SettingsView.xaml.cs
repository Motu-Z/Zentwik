using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>
/// 通用（杂项）设置页。
///
/// <remarks>
/// 视图只负责绑定与事件转发 —— 状态切换与颜色计算全在 SettingsViewModel。
/// 这里的两个事件处理仅做一件事：把「用户何时提交了 HEX」转交给 ViewModel。
///
/// 【彩虹面板的展开/收起动画为什么放在 View 层】
/// 判定「该不该展开」是 ViewModel 的职责（IsColorPanelVisible），
/// 但「怎么展开」是纯表现层的事：Storyboard 要 TargetName 到具体元素，
/// 而 DataTrigger 内的 Storyboard 在页面级 Style 里 TargetName 解析不可靠。
/// 所以拆成两半：ViewModel 决定状态，View 执行动画。
/// </remarks>
public partial class SettingsView : UserControl
{
    /// <summary>面板完全展开时的高度（一行设置行，64）。</summary>
    private const double PanelExpandedHeight = 64;

    public SettingsView()
    {
        InitializeComponent();

        // ============================================================
        // 【必须在这里直接订阅 PropertyChanged】
        //
        // 根因：DataContext 是在 XAML 里通过
        //   <UserControl.DataContext><vm:SettingsViewModel /></UserControl.DataContext>
        // 赋值的 —— 这发生在 InitializeComponent() **内部**，
        // 而 DataContextChanged 事件也是在那一刻触发的。
        //
        // 构造函数末尾再写 `DataContextChanged += OnDataContextChanged;`
        // 已经**错过**了那次触发 → PropertyChanged 从未订阅 →
        // 切换「个性 / 系统」时 IsColorPanelVisible 变了但没人知道 →
        // 彩虹面板永远不展开。
        //
        // 修法：InitializeComponent() 之后立刻取当前 DataContext 订阅。
        // DataContextChanged 仍然保留，作为「后续换 VM」的兜底。
        // ============================================================
        if (DataContext is SettingsViewModel initialVm)
        {
            initialVm.PropertyChanged += OnVmPropertyChanged;
            _subscribedVm = initialVm;
        }

        // 视图加载完成后再解除 HueValue 的初始化保护 ——
        // TwoWay 绑定在加载期会把源值回写一次，
        // 那次回写不应产生副作用（否则初始强调色会被 hsl(h,85,65) 覆盖）。
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.OnViewLoaded();
                ApplyPanelState(vm.IsColorPanelVisible, animate: false);
            }
        };

        // 换 VM 时的兜底（正常路径不会触发，但保留以防万一）
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is SettingsViewModel newVm)
        {
            // 构造函数里已经订阅过初始 VM，这里再订阅一次会重复触发动画。
            // 去重：同一个 VM 不重复订阅。
            if (!ReferenceEquals(newVm, _subscribedVm))
            {
                _subscribedVm?.PropertyChanged -= OnVmPropertyChanged;
                newVm.PropertyChanged += OnVmPropertyChanged;
                _subscribedVm = newVm;
            }

            ApplyPanelState(newVm.IsColorPanelVisible, animate: false);
        }
    }

    /// <summary>当前已订阅 PropertyChanged 的 VM（用于避免重复订阅）。</summary>
    private SettingsViewModel? _subscribedVm;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsColorPanelVisible)
            && sender is SettingsViewModel vm)
        {
            ApplyPanelState(vm.IsColorPanelVisible, animate: true);
        }
    }

    /// <summary>
    /// 按目标状态执行展开 / 收起动画。
    /// </summary>
    private void ApplyPanelState(bool expanded, bool animate)
    {
        if (expanded)
        {
            ColorPanelHost.Visibility = Visibility.Visible;
        }

        var height = new DoubleAnimation
        {
            To = expanded ? PanelExpandedHeight : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(animate ? 180 : 0)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var opacity = new DoubleAnimation
        {
            To = expanded ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(animate ? 180 : 0)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(height);
        storyboard.Children.Add(opacity);
        Storyboard.SetTarget(height, ColorPanelHost);
        Storyboard.SetTargetProperty(height, new PropertyPath(Grid.HeightProperty));
        Storyboard.SetTarget(opacity, ColorPanelBody);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));

        // 收起动画播完再真正隐藏，否则会瞬间消失（没有动画效果）
        if (!expanded)
        {
            storyboard.Completed += (_, _) =>
            {
                if (!IsPanelExpanded())
                {
                    ColorPanelHost.Visibility = Visibility.Collapsed;
                }
            };
        }

        storyboard.Begin(this);
    }

    /// <summary>当前是否处于展开状态（依据目标高度）。</summary>
    private bool IsPanelExpanded() =>
        DataContext is SettingsViewModel vm && vm.IsColorPanelVisible;

    /// <summary>
    /// HEX 输入框失去焦点 → 提交解析。
    /// </summary>
    private void OnHexLostFocus(object sender, RoutedEventArgs e)
    {
        CommitHex();
    }

    /// <summary>
    /// HEX 输入框按回车 → 提交解析。
    /// </summary>
    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHex();

            // 提交后把焦点移走，避免 Enter 之后立刻又触发一次 LostFocus
            if (sender is TextBox tb)
            {
                tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        }
    }

    private void CommitHex()
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CommitHexInput();
        }
    }

    /// <summary>
    /// 「源码」按钮 → 用系统默认浏览器打开项目仓库。
    /// </summary>
    /// <remarks>
    /// 外部 https 链接必须自己用 Process.Start 打开
    /// （UseShellExecute = true 让 Windows 用默认浏览器接管）。
    /// 打不开浏览器不该让应用崩溃 —— 记一笔即可。
    /// </remarks>
    private void OnRepositoryClick(object sender, RoutedEventArgs e)
    {
        OpenUrl((DataContext as SettingsViewModel)?.RepositoryUrl);
    }

    /// <summary>「更新」按钮 → 打开仓库的 releases 页。</summary>
    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        OpenUrl((DataContext as SettingsViewModel)?.ReleasesUrl);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] 打开链接失败: {ex.Message}");
        }
    }
}
