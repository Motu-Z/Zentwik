namespace Zentwik.UI.Views;

/// <summary>高级页。独立 UserControl（不是 PlaceholderView 的子类）—— 需要自己的 XAML 布局。</summary>
public partial class AdvancedView : System.Windows.Controls.UserControl
{
    public AdvancedView()
    {
        InitializeComponent();

        // 连续异常启动次数只有后台知道，进页时问一次。
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.AdvancedViewModel vm)
                await vm.RefreshBootGuardAsync();
        };
    }
}
