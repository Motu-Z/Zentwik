using System.Runtime.InteropServices;

namespace Zentwik.Agent.Service;

/// <summary>SCM 服务入口。非服务进程直接返回 false，由调用方转入控制台模式。</summary>
internal static partial class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint StateStartPending = 0x00000002;
    private const uint StateRunning = 0x00000004;
    private const uint StateStopPending = 0x00000003;
    private const uint StateStopped = 0x00000001;

    private const int ControlStop = 1;
    private const int ControlShutdown = 5;

    private static readonly ServiceMainDelegate ServiceMainCallback = ServiceMain;
    private static readonly HandlerDelegate HandlerCallback = HandleControl;

    private static string _serviceName = "ZentwikAgent";
    private static Func<CancellationToken, Task>? _run;
    private static nint _statusHandle;
    private static CancellationTokenSource? _cts;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerDelegate(int control, int eventType, nint eventData, nint context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(nint table);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RegisterServiceCtrlHandlerExW(string serviceName, nint handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, ref ServiceStatus status);

    public static bool TryRun(string serviceName, Func<CancellationToken, Task> run)
    {
        if (Environment.UserInteractive)
        {
            return false;
        }

        _serviceName = serviceName;
        _run = run;

        var namePointer = Marshal.StringToHGlobalUni(serviceName);
        var mainPointer = Marshal.GetFunctionPointerForDelegate(ServiceMainCallback);
        var table = Marshal.AllocHGlobal(IntPtr.Size * 4);

        try
        {
            Marshal.WriteIntPtr(table, 0, namePointer);
            Marshal.WriteIntPtr(table, IntPtr.Size, mainPointer);
            Marshal.WriteIntPtr(table, IntPtr.Size * 2, 0);
            Marshal.WriteIntPtr(table, IntPtr.Size * 3, 0);

            if (!StartServiceCtrlDispatcherW(table))
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"连接服务控制器失败，Win32 错误码 {error}。");
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private static void ServiceMain(int argumentCount, nint arguments)
    {
        _ = argumentCount;
        _ = arguments;

        _statusHandle = RegisterServiceCtrlHandlerExW(
            _serviceName,
            Marshal.GetFunctionPointerForDelegate(HandlerCallback),
            0);

        if (_statusHandle == 0)
        {
            return;
        }

        ReportState(StateStartPending, 0, waitHint: 30_000);
        _cts = new CancellationTokenSource();

        try
        {
            ReportState(StateRunning, ServiceAcceptStop | ServiceAcceptShutdown);
            _run?.Invoke(_cts.Token).GetAwaiter().GetResult();
            ReportState(StateStopped, 0);
        }
        catch (OperationCanceledException)
        {
            ReportState(StateStopped, 0);
        }
        catch (Exception ex)
        {
            // 只有把失败写进服务状态，SCM 才会按恢复动作重启；静默退出会被当成正常停止。
            Console.Error.WriteLine($"服务异常终止：{ex}");
            ReportState(StateStopped, 0, exitCode: 1066);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static int HandleControl(int control, int eventType, nint eventData, nint context)
    {
        _ = eventType;
        _ = eventData;
        _ = context;

        if (control is ControlStop or ControlShutdown)
        {
            ReportState(StateStopPending, 0, waitHint: 30_000);
            _cts?.Cancel();
        }

        return 0;
    }

    private static void ReportState(uint state, uint accepted, uint exitCode = 0, uint waitHint = 0)
    {
        if (_statusHandle == 0)
        {
            return;
        }

        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = accepted,
            Win32ExitCode = exitCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = 0,
            WaitHint = waitHint,
        };

        SetServiceStatus(_statusHandle, ref status);
    }
}
