using System.Windows;
using System.Windows.Controls;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Views;

/// <summary>
/// 调校页。
/// </summary>
/// <remarks>
/// 【code-behind 只做三件事】
/// 建视图模型、挂 DataContext、在第一次显示时触发一次与 Worker 的对数。
/// 刷新 / 应用 / 取消 / 步进全部是 <see cref="TuningViewModel"/> 的命令，
/// 页面不判断任何业务状态 —— 「能不能点」由 CanOperate / CanSave 表达。
///
/// 【为什么不在构造函数里发起 IPC】
/// 构造发生在导航时，此时页面还没上屏。若构造里 await 一次两秒的查询，
/// 首帧会卡在「什么都没有」上；而且离屏截图与自检会构造本页，
/// 它们不该被网络等待污染（截图模式见 <see cref="TuningViewModel.SuppressWorkerInit"/>）。
///
/// 【为什么只初始化一次】
/// 页面实例被外壳缓存，切走再切回来不会重建。重复查询会把用户没下发的改动
/// 用硬件值覆盖掉 —— 这是「切了个页，改的东西没了」这类投诉的来源。
/// </remarks>
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
