using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Zentwik.Core.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>页头方案条：显示本页那一类方案的当前项，点开是方案面板</summary>
public partial class ProfileBar : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(PlanKind),
        typeof(ProfileBar),
        new PropertyMetadata(PlanKind.Tuning, OnKindChanged));

    /// <summary>面板刚被「点外部」关掉的那一小段时间里，不再重新打开</summary>
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

    /// <summary>确认叠层弹出时收起面板</summary>
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
