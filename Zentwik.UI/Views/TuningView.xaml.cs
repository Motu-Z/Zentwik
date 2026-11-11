using System.Windows;
using System.Windows.Controls;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>调校页。 建视图模型、挂 DataContext、在第一次显示时触发一次与 Worker 的对数</summary>
public partial class TuningView : UserControl
{
    public TuningView()
    {
        // FreezeDirty：控件上屏时 WPF 会把绑定读出来的文本原样写回 setter，
        // 那一下不能算用户改动，否则一打开就是「有未保存改动」。
        TuningViewModel vm = new() { FreezeDirty = true };

        DataContext = vm;

        InitializeComponent();

        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        if (DataContext is not TuningViewModel vm)
        {
            return;
        }

        vm.FreezeDirty = false;

        // InitializeAsync 内部吞掉所有异常并折算成界面状态：
        // async void 里逃出去的异常没有调用方能接，会直接结束进程。
        await vm.InitializeAsync();
    }
}
