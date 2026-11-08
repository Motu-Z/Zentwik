using System.Windows.Controls;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>
/// 全局 Toast 宿主。窗口级只挂一份，所有页面共用同一个 ToastService。
/// </summary>
public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();

        // 显式指向单例，不靠 DataContext 继承。
        DataContext = ToastService.Instance;
    }
}
