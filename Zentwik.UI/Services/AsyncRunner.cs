namespace Zentwik.UI.Services;

/// <summary>异步命令的唯一收口</summary>
public static class AsyncRunner
{
    /// <summary>发起一个异步动作，不等待结果；失败转一条 Toast。</summary>
    public static void Fire(Func<Task> action) => _ = Run(action, ViewModels.ToastService.Instance.Show);

    /// <summary>发起一个异步动作，不等待结果；失败走错误气泡， 受「发生错误时弹出气泡」开关约束</summary>
    public static void FireError(Func<Task> action) => _ = Run(action, ViewModels.ToastService.Instance.ShowError);

    /// <summary>执行异步动作，失败转一条 Toast。</summary>
    public static Task Run(Func<Task> action) => Run(action, ViewModels.ToastService.Instance.Show);

    private static async Task Run(Func<Task> action, Action<string> report)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            report(ex.Message);
        }
    }
}
