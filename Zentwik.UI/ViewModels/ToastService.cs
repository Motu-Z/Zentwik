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

    public void Show(string message)
    {
        Message = message;
        IsVisible = true;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(
            Math.Clamp(AppSettings.Instance.ToastDurationSeconds, 1, 30));
        _timer.Start();
    }

    /// <summary>错误提示。关掉「发生错误时弹出气泡」后整条丢弃，日志仍有记录。</summary>
    public void ShowError(string message)
    {
        if (!AppSettings.Instance.ErrorBubble)
            return;

        Show(message);
    }

    public void Hide()
    {
        _timer.Stop();
        IsVisible = false;
    }
}
