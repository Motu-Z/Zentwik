using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>
/// 进程页右栏「自定义掩码」页签的内容：已保存掩码列表 ↔ 掩码编辑器两个视图。
/// </summary>
/// <remarks>
/// 【为什么不再是居中弹窗】
/// 掩码是给进程绑核用的，编辑时最好还能看见左边的进程表。改成抽屉内的页签后
/// 一次只占右栏，不再整屏遮罩。滑入动画写在 code-behind，理由同进程页抽屉：
/// 触发器 Storyboard 会被离屏截图拍到中间帧。
///
/// 【Owner 为什么还是依赖属性】
/// 这里的所有绑定都指向 <see cref="ProcessViewModel"/>（掩码管理器实例）。
/// 靠 DataContext 继承在解析期可能还没就绪，所以宿主把 VM 显式塞进来：<c>Owner="{Binding}"</c>。
/// </remarks>
public partial class MaskManagerOverlay : UserControl
{
    /// <summary>宿主页面的视图模型。</summary>
    public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(
        nameof(Owner),
        typeof(object),
        typeof(MaskManagerOverlay),
        new PropertyMetadata(null));

    /// <summary>宿主页面的视图模型。</summary>
    public object? Owner
    {
        get => GetValue(OwnerProperty);
        set => SetValue(OwnerProperty, value);
    }

    public MaskManagerOverlay()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MaskManagerViewModel? Manager => (Owner as ProcessViewModel)?.MaskManager;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MaskManagerViewModel? manager = Manager;

        if (manager is null)
            return;

        manager.EditingChanged -= OnEditingChanged;
        manager.EditingChanged += OnEditingChanged;

        SnapEdit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Manager is not null)
            Manager.EditingChanged -= OnEditingChanged;
    }

    private void OnEditingChanged(bool editing)
    {
        TranslateTransform shift = editing ? EditorShift : ListShift;

        // 诊断模式直接落终值：离屏截图没有渲染帧驱动动画，
        // 停在起始位移（整块被平移到可视区外）会拍成一片空白。
        if (ProcessViewModel.SuppressWorkerInit)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            return;
        }

        // 进入的那个视图从右侧滑进来；Visibility 已由绑定同步切好。
        double from = editing ? ActualWidth : -ActualWidth;

        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = from;

        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        shift.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>直接归零，跳过过渡。首次显示时用，避免截图拍到中间帧。</summary>
    private void SnapEdit()
    {
        ListShift.BeginAnimation(TranslateTransform.XProperty, null);
        EditorShift.BeginAnimation(TranslateTransform.XProperty, null);

        ListShift.X = 0;
        EditorShift.X = 0;
    }
}
