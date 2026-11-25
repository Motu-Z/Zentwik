using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace Zentwik.Desktop.Tests;

/// <summary>
/// WPF 对象有线程亲和性，Application 每个进程只能有一个，因此所有界面测试共用同一个 STA 线程与调度器。
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Host = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;

        Host.Value.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        failure?.Throw();
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var application = new App();
            application.InitializeComponent();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "zentwik-ui-test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));

        return dispatcher ?? throw new InvalidOperationException("UI 线程未启动。");
    }
}
