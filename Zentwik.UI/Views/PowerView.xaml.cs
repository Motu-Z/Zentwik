using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

public partial class PowerView : UserControl
{
    public PowerView()
    {
        var vm = new PowerViewModel();
        DataContext = vm;
        InitializeComponent();

        // 窗口先上屏再发 IPC：查询要在管道上排在 Worker 前面那条命令之后，
        // 放进构造函数会把首帧卡住。
        Loaded += async (_, _) => await vm.InitializeAsync();
    }

    private void OnSchemeRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PowerScheme scheme } || DataContext is not PowerViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(scheme);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(scheme);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 点别处也算提交。误触时应按 Esc 取消 —— 丢焦即提交是这一页唯一自然的退出方式：
    /// 卡片只有 40 高，再放一对「确定 / 取消」按钮会把名字挤没。
    /// </summary>
    private void OnSchemeRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PowerScheme { IsRenaming: true } scheme }
            && DataContext is PowerViewModel vm)
        {
            vm.CommitRenameCommand.Execute(scheme);
        }
    }
}
