namespace Zentwik.Platform.Windows;

/// <summary>TickCount64 不含休眠时间，因此休眠唤醒后推算的启动时刻会偏晚；与上次记录比较时留了分钟级容差。</summary>
public sealed class SystemBootInfo : IBootInfo
{
    public DateTimeOffset BootTimeUtc => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
