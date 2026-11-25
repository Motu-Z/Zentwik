using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Zentwik.Agent.Service;

internal static partial class NativeService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;

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
            return 5;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.Error.WriteLine("无法确定可执行文件路径。");
            return 5;
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
            return 5;
        }

        try
        {
            var existing = OpenServiceW(manager, options.ServiceName, ServiceAllAccess);
            if (existing != 0)
            {
                CloseServiceHandle(existing);
                Console.Error.WriteLine($"服务 {options.ServiceName} 已存在，请先卸载。");
                return 5;
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
                return 5;
            }

            try
            {
                if (!StartServiceW(service, 0, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"服务已创建但启动失败，Win32 错误码 {error}。");
                    return 5;
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }

            Console.WriteLine($"服务 {options.ServiceName} 已安装并启动。");
            return 0;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static int Uninstall(AgentOptions options)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("卸载服务需要管理员权限。");
            return 5;
        }

        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0)
        {
            Console.Error.WriteLine($"打开服务管理器失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
            return 5;
        }

        try
        {
            var service = OpenServiceW(manager, options.ServiceName, ServiceAllAccess);
            if (service == 0)
            {
                Console.Error.WriteLine($"服务 {options.ServiceName} 不存在。");
                return 5;
            }

            try
            {
                var status = default(ServiceStatusProcess);
                if (ControlService(service, ServiceControlStop, ref status))
                {
                    for (var i = 0; i < 50; i++)
                    {
                        if (!QueryServiceStatusEx(service, 0, ref status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _)
                            || status.CurrentState == ServiceStopped)
                        {
                            break;
                        }

                        Thread.Sleep(100);
                    }
                }

                if (!DeleteService(service))
                {
                    Console.Error.WriteLine($"删除服务失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
                    return 5;
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

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
