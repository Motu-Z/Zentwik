namespace Zentwik.UI.Views;

/// <summary>
/// 高级页。
///
/// <remarks>
/// 独立 UserControl（不是 PlaceholderView 的子类）——
/// WPF 不允许把「用 XAML 定义的 UserControl」当作派生类的 XAML 根，
/// 而本页需要自己的 XAML 布局，不能只设个标题。
/// </remarks>
public partial class AdvancedView : System.Windows.Controls.UserControl
{
    public AdvancedView()
    {
        InitializeComponent();
    }
}
