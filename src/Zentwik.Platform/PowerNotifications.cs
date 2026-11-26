namespace Zentwik.Platform;

/// <summary>
/// 系统挂起/恢复通知。实现不得依赖窗口、消息泵或常驻线程，服务会话（Session 0）内同样可用。
/// </summary>
public interface IPowerNotificationSource : IDisposable
{
    event Action? Resumed;

    bool Start(out string? error);
}
