using System.Windows.Threading;

namespace Zentwik.UI.ViewModels;

public sealed class ToastService : ViewModelBase
{
    public static ToastService Instance { get; } = new();

    private readonly DispatcherTimer _timer;

    private ToastService()
    {
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            IsVisible = false;
        };
    }

    private string _message = string.Empty;

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private bool _isVisible;

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    private bool _isError;

    /// <summary>true = 失败提示，气泡换警示图标并对勾置灰。</summary>
    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public void Show(string message) => ShowCore(message, false);

    /// <summary>错误提示。关掉「发生错误时弹出气泡」后整条丢弃，日志仍有记录。</summary>
    public void ShowError(string message)
    {
        if (!AppSettings.Instance.ErrorBubble)
            return;

        ShowCore(message, true);
    }

    private void ShowCore(string message, bool isError)
    {
        Message = message;
        IsError = isError;
        IsVisible = true;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(
            Math.Clamp(AppSettings.Instance.ToastDurationSeconds, 1, 30));
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        IsVisible = false;
    }
}
