namespace Zentwik.UI.Views;

/// <summary>
/// 行为页。
///
/// <remarks>
/// 独立 UserControl（不是 <see cref="PlaceholderView"/> 的子类）——
/// WPF 不允许把「用 XAML 定义的 UserControl」当作派生类的 XAML 根，
/// 而本页需要自己的布局与真实控件，不能只设个标题。
/// </remarks>
public partial class BehaviorView : System.Windows.Controls.UserControl
{
    public BehaviorView()
    {
        InitializeComponent();
    }
}
