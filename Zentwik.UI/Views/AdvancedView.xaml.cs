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

        // 连续异常启动次数只有后台知道，进页时问一次。
        // 外壳缓存本页实例，切走再切回来会重新触发 Loaded —— 每次重读正好反映了
        // 期间可能发生的启动失败。
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.AdvancedViewModel vm)
                await vm.RefreshBootGuardAsync();
        };
    }
}
