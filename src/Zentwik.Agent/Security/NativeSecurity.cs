using System.Runtime.InteropServices;
using System.Text;

namespace Zentwik.Agent.Security;

internal static partial class NativeSecurity
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;

    public const int TokenUser = 1;
    public const int TokenElevation = 20;
    public const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevationValue
    {
        public int TokenIsElevated;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientSessionId(nint pipe, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        [Out] char[] buffer,
        ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        nint token,
        int informationClass,
        nint buffer,
        uint bufferLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll")]
    internal static partial nint GetSidSubAuthority(nint sid, uint index);

    [LibraryImport("advapi32.dll")]
    internal static partial nint GetSidSubAuthorityCount(nint sid);

    internal static string? ReadUserSid(nint token)
    {
        var buffer = ReadTokenBuffer(token, TokenUser);
        if (buffer == 0)
        {
            return null;
        }

        try
        {
            var user = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            return user.Sid == 0 ? null : new System.Security.Principal.SecurityIdentifier(user.Sid).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int ReadIntegrityRid(nint token)
    {
        var buffer = ReadTokenBuffer(token, TokenIntegrityLevel);
        if (buffer == 0)
        {
            return -1;
        }

        try
        {
            var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            if (label.Sid == 0)
            {
                return -1;
            }

            var countPointer = GetSidSubAuthorityCount(label.Sid);
            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return -1;
            }

            var authority = GetSidSubAuthority(label.Sid, (uint)(count - 1));
            return Marshal.ReadInt32(authority);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool ReadElevated(nint token)
    {
        var size = (uint)Marshal.SizeOf<TokenElevationValue>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetTokenInformation(token, TokenElevation, buffer, size, out _))
            {
                return false;
            }

            return Marshal.PtrToStructure<TokenElevationValue>(buffer).TokenIsElevated != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string? ReadImagePath(nint process)
    {
        var buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size)
            ? new string(buffer, 0, (int)size)
            : null;
    }

    private static nint ReadTokenBuffer(nint token, int informationClass)
    {
        GetTokenInformation(token, informationClass, 0, 0, out var size);
        if (size == 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        if (!GetTokenInformation(token, informationClass, buffer, size, out _))
        {
            Marshal.FreeHGlobal(buffer);
            return 0;
        }

        return buffer;
    }
}
