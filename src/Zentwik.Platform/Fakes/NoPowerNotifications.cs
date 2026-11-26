using Zentwik.Platform;

namespace Zentwik.Platform.Fakes;

/// <summary>模拟模式：不订阅系统电源通知，恢复核对只能由测试显式触发。</summary>
public sealed class NoPowerNotifications : IPowerNotificationSource
{
    public event Action? Resumed
    {
        add { }
        remove { }
    }

    public bool Start(out string? error)
    {
        error = null;
        return true;
    }

    public void Dispose()
    {
    }
}
