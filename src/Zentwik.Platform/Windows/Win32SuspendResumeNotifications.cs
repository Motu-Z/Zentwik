using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zentwik.Platform.Windows;

/// <summary>
/// 通过 PowerRegisterSuspendResumeNotification 接收挂起/恢复通知：回调由系统线程触发，
/// 不需要窗口或消息循环，因此可在 LocalSystem 服务中使用。
/// </summary>
public sealed unsafe partial class Win32SuspendResumeNotifications : IPowerNotificationSource
{
    private const uint DeviceNotifyCallback = 0x00000002;
    private const uint PbtApmResumeSuspend = 0x0007;
    private const uint PbtApmResumeAutomatic = 0x0012;
    private static readonly long ResumeDebounceTicks = TimeSpan.FromSeconds(2).Ticks;

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, nint recipient, out nint registration);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(nint registration);

    private nint _registration;
    private nint _parameters;
    private GCHandle _self;
    private long _lastResumeTicks;
    private bool _disposed;

    public event Action? Resumed;

    public bool Start(out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registration != 0)
        {
            error = null;
            return true;
        }

        _self = GCHandle.Alloc(this);
        _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<SubscribeParameters>());
        var parameters = new SubscribeParameters
        {
            Callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, uint>)&OnPowerEvent,
            Context = GCHandle.ToIntPtr(_self),
        };

        Marshal.StructureToPtr(parameters, _parameters, false);
        var code = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration);
        if (code != 0)
        {
            error = $"注册系统挂起/恢复通知失败，Win32 错误码 {code}。";
            Release();
            return false;
        }

        error = null;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registration != 0)
        {
            PowerUnregisterSuspendResumeNotification(_registration);
            _registration = 0;
        }

        Release();
    }

    private void Release()
    {
        if (_parameters != 0)
        {
            Marshal.FreeHGlobal(_parameters);
            _parameters = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void RaiseResumed()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var previous = Interlocked.Exchange(ref _lastResumeTicks, now);
        // 同一次唤醒会依次收到自动恢复与恢复挂起两条通知，只处理第一条。
        if (now - previous < ResumeDebounceTicks)
        {
            return;
        }

        Resumed?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerEvent(nint context, uint type, nint setting)
    {
        try
        {
            if (type is PbtApmResumeSuspend or PbtApmResumeAutomatic
                && GCHandle.FromIntPtr(context).Target is Win32SuspendResumeNotifications source)
            {
                source.RaiseResumed();
            }
        }
        catch (Exception)
        {
            // 回调内的异常不能传回系统。
        }

        return 0;
    }
}
