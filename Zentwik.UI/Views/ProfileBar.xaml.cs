using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Zentwik.Core.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>
/// 页头方案条：显示本页那一类方案的当前项，点开是方案面板。
/// </summary>
/// <remarks>
/// 【为什么带一个 Kind】
/// 三页各管各的方案：调校页管调校方案、进程页管调度方案、电源页管电源方案。
/// 共用一个不知道类别的控件，就只能共用一个「当前方案」——
/// 那正是「改一个方案，三页全跟着变」的来源。
/// </remarks>
public partial class ProfileBar : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(PlanKind),
        typeof(ProfileBar),
        new PropertyMetadata(PlanKind.Tuning, OnKindChanged));

    /// <summary>
    /// 面板刚被「点外部」关掉的那一小段时间里，不再重新打开。
    /// </summary>
    /// <remarks>
    /// Popup 设了 StaysOpen=False，点方案名按钮时 Popup 会先因为
    /// 「点击落在 Popup 之外」而关闭并触发 Closed；紧接着按钮的 Click 又跑起来，
    /// 于是面板刚关就被重新打开 —— 表现为「点按钮关不掉」。
    /// Closed 一定早于 Click，所以记一个 200ms 的窗口，期间忽略打开请求。
    /// </remarks>
    private DateTime _suppressReopenUntil;

    public ProfileBar()
    {
        InitializeComponent();

        ApplyScope();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>本页管哪一类方案。</summary>
    public PlanKind Kind
    {
        get => (PlanKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private PlanScope Scope => PlanHub.Of(Kind);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProfileBar bar)
            bar.ApplyScope();
    }

    private void ApplyScope()
    {
        if (Overlay is null)
            return;

        DataContext = Scope;
        Overlay.DataContext = Scope;

        // Popup 是独立可视树，继承不到外层 DataContext，必须显式给。
        Scope.EnsurePlaceholder();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConfirmService.Instance.PropertyChanged += OnConfirmStateChanged;
        _ = Scope.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ConfirmService.Instance.PropertyChanged -= OnConfirmStateChanged;
    }

    /// <summary>
    /// 确认叠层弹出时收起面板。
    /// </summary>
    /// <remarks>
    /// 确认卡是窗口内的 Grid 叠层，而 Popup 是独立顶层窗口 —— Popup 永远盖在叠层之上。
    /// 不收起的话，用户点「删除方案」后会看到一个被面板压住的确认框，等于点不动。
    /// </remarks>
    private void OnConfirmStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfirmService.IsOpen) && ConfirmService.Instance.IsOpen)
            ProfilePopup.IsOpen = false;
    }

    private void OnToggleProfilePopup(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < _suppressReopenUntil)
            return;

        ProfilePopup.IsOpen = true;
    }

    private void OnProfilePopupClosed(object? sender, EventArgs e)
    {
        _suppressReopenUntil = DateTime.UtcNow.AddMilliseconds(200);
    }
}
