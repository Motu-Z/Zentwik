using System.Windows;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var catalog = LocalizationCatalog.CreateDefault();
        var themes = new ThemeService();
        var bridge = new LocalizationBridge(catalog);

        // 资源字典必须在创建窗口前填充，否则首帧会出现资源键名而不是文案。
        bridge.Apply();
        themes.Apply("dark", "blue");

        _viewModel = new MainViewModel(catalog, themes);

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        _ = _viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
