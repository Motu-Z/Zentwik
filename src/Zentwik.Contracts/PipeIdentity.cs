using System.Runtime.InteropServices;

namespace Zentwik.Contracts.Protocol;

/// <summary>
/// 校验管道另一端是谁创建的。服务以 SYSTEM 运行，客户端不能沿用"必须属于当前用户"的判定。
/// 普通用户无权对 SYSTEM 进程调用 OpenProcess，因此以管道安全描述符的所有者为第一判据。
/// </summary>
internal static partial class PipeIdentity
{
    public const string SystemSid = "S-1-5-18";

    /// <summary>SYSTEM 令牌的默认所有者是 Administrators，服务创建的管道因此归属于该组。</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    private const int KernelObject = 6;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetSecurityInfo(
        nint handle,
        int objectType,
        uint securityInfo,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint descriptor);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, nint buffer, uint length, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSidW(nint sid, out nint text);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    public static string? ReadServerSid(nint pipeHandle) =>
        ReadPipeOwnerSid(pipeHandle) ?? ReadServerProcessSid(pipeHandle);

    public static string? ReadCurrentSid()
    {
        var process = GetCurrentProcess();
        return process == 0 ? null : ReadProcessSid(process);
    }

    public static bool IsTrustedServer(string? serverSid, string? currentSid)
    {
        if (string.IsNullOrEmpty(serverSid) || string.IsNullOrEmpty(currentSid))
        {
            return false;
        }

        return string.Equals(serverSid, currentSid, StringComparison.OrdinalIgnoreCase)
               || string.Equals(serverSid, SystemSid, StringComparison.Ordinal)
               || string.Equals(serverSid, AdministratorsSid, StringComparison.Ordinal);
    }

    private static string? ReadServerProcessSid(nint pipeHandle)
    {
        if (!GetNamedPipeServerProcessId(pipeHandle, out var processId))
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return ReadProcessSid(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? ReadPipeOwnerSid(nint pipeHandle)
    {
        var error = GetSecurityInfo(pipeHandle, KernelObject, OwnerSecurityInformation, out var owner, out _, out _, out _, out var descriptor);
        if (error != 0 || owner == 0 || descriptor == 0)
        {
            return null;
        }

        try
        {
            return ConvertSidToString(owner);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string? ConvertSidToString(nint sid)
    {
        if (!ConvertSidToStringSidW(sid, out var text))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            LocalFree(text);
        }
    }

    private static string? ReadProcessSid(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            GetTokenInformation(token, TokenUser, 0, 0, out var size);
            if (size == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, size, out _))
                {
                    return null;
                }

                var user = Marshal.PtrToStructure<SidAndAttributes>(buffer);
                return user.Sid == 0 ? null : ConvertSidToString(user.Sid);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
