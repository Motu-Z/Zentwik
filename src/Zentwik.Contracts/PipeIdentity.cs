using System.Runtime.InteropServices;

namespace Zentwik.Contracts.Protocol;

/// <summary>校验管道另一端是谁创建的。服务以 SYSTEM 运行，客户端不能沿用"必须属于当前用户"的判定。</summary>
internal static partial class PipeIdentity
{
    public const string SystemSid = "S-1-5-18";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

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

    public static string? ReadServerSid(nint pipeHandle)
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
               || string.Equals(serverSid, SystemSid, StringComparison.Ordinal);
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
                if (user.Sid == 0 || !ConvertSidToStringSidW(user.Sid, out var text))
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
