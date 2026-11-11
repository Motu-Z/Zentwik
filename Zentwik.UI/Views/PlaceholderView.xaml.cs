using System.Windows;
using System.Windows.Controls;

namespace Zentwik.UI.Views;

/// <summary>占位页骨架。用 DependencyProperty 暴露标题，避免五个占位页各写一份 XAML。</summary>
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
