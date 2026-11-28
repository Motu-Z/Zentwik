using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // MainViewModel 在构造时捕获 SynchronizationContext。必须先把 WinForms 的装上，
        // 否则它的 _ui.Post 会落到线程池，回调里改控件直接抛跨线程异常。
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var catalog = LocalizationCatalog.CreateDefault();
        var themes = new ThemeService();
        var viewModel = new MainViewModel(catalog, themes);

        using (var form = new MainForm(viewModel, catalog, themes))
        {
            Application.Run(form);
        }

        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
