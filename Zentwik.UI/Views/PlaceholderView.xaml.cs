using System.Windows;
using System.Windows.Controls;

namespace Zentwik.UI.Views;

/// <summary>
/// 占位页骨架。
///
/// <remarks>
/// 用 DependencyProperty 暴露标题，而不是让每个子类各写一份 XAML ——
/// 五个占位页的结构完全一致，复制五份只会在后续改样式时漏改其中几个。
///
/// DP 可以被样式/绑定覆盖，且不破坏 XAML 设计师的即时预览能力。
/// </remarks>
public partial class PlaceholderView : UserControl
{
    /// <summary>页面标题。</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PlaceholderView),
        new PropertyMetadata(string.Empty));

    public PlaceholderView()
    {
        InitializeComponent();
    }

    /// <summary>页面标题，显示在页面顶部。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}
