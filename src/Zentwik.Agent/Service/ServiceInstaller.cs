using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Zentwik.Agent.Service;

internal static partial class NativeService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceRunState = 0x00000004;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ScActionRestart = 1;

    private const int FailureExitCode = 3;

    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(20);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public nint RebootMsg;
        public nint Command;
        public uint ActionsCount;
        public nint Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public nint Description;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateServiceW(
        nint manager,
        string serviceName,
        string displayName,
        uint access,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        nint loadOrderGroup,
        nint tagId,
        nint dependencies,
        nint serviceStartName,
        nint password);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenServiceW(nint manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartServiceW(nint service, uint argumentCount, nint arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(nint service, uint control, ref ServiceStatusProcess status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig2W(nint service, uint infoLevel, nint info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatusEx(
        nint service,
        int level,
        ref ServiceStatusProcess status,
        uint bufferSize,
        out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);

    public static int Install(AgentOptions options)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("安装服务需要管理员权限。");
            return FailureExitCode;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.Error.WriteLine("无法确定可执行文件路径。");
            return FailureExitCode;
        }

        Directory.CreateDirectory(options.DataDirectory);
        var ownerSid = WindowsIdentity.GetCurrent().User?.Value;
        if (ownerSid is not null)
        {
            File.WriteAllText(Path.Combine(options.DataDirectory, "owner.sid"), ownerSid);
        }

        var binaryPath = $"\"{executable}\" --service --name \"{options.ServiceName}\" --data-dir \"{options.DataDirectory}\"";
        var manager = OpenSCManagerW(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager == 0)
        {
            Console.Error.WriteLine($"打开服务管理器失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            return FailureExitCode;
        }

        try
        {
            var existing = OpenServiceW(manager, options.ServiceName, ServiceAllAccess);
            if (existing != 0)
            {
                CloseServiceHandle(existing);
                Console.Error.WriteLine($"服务 {options.ServiceName} 已存在，请先卸载。");
                return FailureExitCode;
            }

            var service = CreateServiceW(
                manager,
                options.ServiceName,
                "Zentwik 调优代理",
                ServiceAllAccess,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                binaryPath,
                0,
                0,
                0,
                0,
                0);

            if (service == 0)
            {
                Console.Error.WriteLine($"创建服务失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
                return FailureExitCode;
            }

            try
            {
                ApplyDescription(service);
                ApplyRecoveryActions(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        var started = Start(options.ServiceName);
        Console.WriteLine(started == 0
            ? $"服务 {options.ServiceName} 已安装并启动。"
            : $"服务 {options.ServiceName} 已安装，但启动失败。");
        return started;
    }

    public static int Uninstall(AgentOptions options)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("卸载服务需要管理员权限。");
            return FailureExitCode;
        }

        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0)
        {
            Console.Error.WriteLine($"打开服务管理器失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            return FailureExitCode;
        }

        try
        {
            var service = OpenServiceW(manager, options.ServiceName, ServiceAllAccess);
            if (service == 0)
            {
                Console.Error.WriteLine($"服务 {options.ServiceName} 不存在。");
                return FailureExitCode;
            }

            try
            {
                var status = default(ServiceStatusProcess);
                if (ControlService(service, ServiceControlStop, ref status))
                {
                    WaitForState(service, ServiceStopped);
                }

                if (!DeleteService(service))
                {
                    Console.Error.WriteLine($"删除服务失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
                    return FailureExitCode;
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }

            Console.WriteLine($"服务 {options.ServiceName} 已卸载。");
            return 0;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static int Start(string serviceName)
    {
        var service = OpenForControl(serviceName, ServiceStart | ServiceQueryStatus);
        if (service == 0)
        {
            return FailureExitCode;
        }

        try
        {
            if (!StartServiceW(service, 0, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1056)
                {
                    Console.Error.WriteLine($"启动服务失败，Win32 错误码 {error}。");
                    return FailureExitCode;
                }
            }

            if (WaitForState(service, ServiceRunState))
            {
                Console.WriteLine($"服务 {serviceName} 已启动。");
                return 0;
            }

            Console.Error.WriteLine($"服务 {serviceName} 未进入运行状态。");
            return FailureExitCode;
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    public static int Stop(string serviceName)
    {
        var service = OpenForControl(serviceName, ServiceStop | ServiceQueryStatus);
        if (service == 0)
        {
            return FailureExitCode;
        }

        try
        {
            var status = default(ServiceStatusProcess);
            if (!ControlService(service, ServiceControlStop, ref status))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1062)
                {
                    Console.Error.WriteLine($"停止服务失败，Win32 错误码 {error}。");
                    return FailureExitCode;
                }
            }

            if (WaitForState(service, ServiceStopped))
            {
                Console.WriteLine($"服务 {serviceName} 已停止。");
                return 0;
            }

            Console.Error.WriteLine($"服务 {serviceName} 未在 {StateTimeout.TotalSeconds:0} 秒内停止。");
            return FailureExitCode;
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static nint OpenForControl(string serviceName, uint access)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("启停服务需要管理员权限。");
            return 0;
        }

        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0)
        {
            Console.Error.WriteLine($"打开服务管理器失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            return 0;
        }

        try
        {
            var service = OpenServiceW(manager, serviceName, access);
            if (service == 0)
            {
                Console.Error.WriteLine($"服务 {serviceName} 不存在或无法访问，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            }

            return service;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool WaitForState(nint service, uint expected)
    {
        var status = default(ServiceStatusProcess);
        var deadline = DateTime.UtcNow + StateTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!QueryServiceStatusEx(service, 0, ref status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            {
                return false;
            }

            if (status.CurrentState == expected)
            {
                return true;
            }

            if (status.CurrentState == ServiceStopped && expected != ServiceStopped)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static void ApplyDescription(nint service)
    {
        var text = Marshal.StringToHGlobalUni("Zentwik Next 系统服务：设备访问、场景自动化与状态账本。");
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceDescription>());
        try
        {
            var description = new ServiceDescription { Description = text };
            Marshal.StructureToPtr(description, buffer, false);
            if (!ChangeServiceConfig2W(service, ServiceConfigDescription, buffer))
            {
                Console.Error.WriteLine($"写入服务描述失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(text);
        }
    }

    private static void ApplyRecoveryActions(nint service)
    {
        ScAction[] actions =
        [
            new() { Type = ScActionRestart, Delay = 5000 },
            new() { Type = ScActionRestart, Delay = 15000 },
            new() { Type = ScActionRestart, Delay = 60000 },
        ];

        var actionSize = Marshal.SizeOf<ScAction>();
        var actionBuffer = Marshal.AllocHGlobal(actionSize * actions.Length);
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActions>());
        try
        {
            for (var i = 0; i < actions.Length; i++)
            {
                Marshal.StructureToPtr(actions[i], actionBuffer + (i * actionSize), false);
            }

            var failure = new ServiceFailureActions
            {
                ResetPeriod = 86400,
                RebootMsg = 0,
                Command = 0,
                ActionsCount = (uint)actions.Length,
                Actions = actionBuffer,
            };

            Marshal.StructureToPtr(failure, buffer, false);
            if (!ChangeServiceConfig2W(service, ServiceConfigFailureActions, buffer))
            {
                Console.Error.WriteLine($"写入服务恢复动作失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(actionBuffer);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
