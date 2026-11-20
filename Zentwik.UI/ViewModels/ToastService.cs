using System.Windows.Threading;

namespace Zentwik.UI.ViewModels;

public sealed class ToastService : ViewModelBase
{
    public static ToastService Instance { get; } = new();

    /// <summary>系统通知通道，由托盘图标提供。为空时退回界面内提示。</summary>
    public static Action<string, string>? SystemNotify { get; set; }

    private const string ErrorTitle = "Zentwik 运行异常";

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

    /// <summary>错误提示走系统通知，托盘不可用时退回界面内。</summary>
    public void ShowError(string message)
    {
        if (!AppSettings.Instance.ErrorBubble)
            return;

        NotifySystem(ErrorTitle, message);
    }

    /// <summary>不受「异常时通知」开关影响的系统通知，用于切换失败一类必须送达的结果。</summary>
    public void NotifySystem(string title, string message)
    {
        if (SystemNotify is { } notify)
        {
            notify(title, message);
            return;
        }

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
