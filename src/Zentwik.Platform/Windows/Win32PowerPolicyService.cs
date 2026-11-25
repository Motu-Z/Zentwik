using System.Runtime.InteropServices;
using System.Text;

namespace Zentwik.Platform.Windows;

/// <summary>读取无需管理员；切换失败返回 Win32 错误码由上层处理。</summary>
public sealed class Win32PowerPolicyService : IPowerPolicyService
{
    public PowerSchemeInfo? GetActiveScheme()
    {
        var result = Native.PowerGetActiveScheme(0, out var pointer);
        if (result != Native.ErrorSuccess || pointer == 0)
        {
            return null;
        }

        try
        {
            var guid = Marshal.PtrToStructure<Guid>(pointer);
            return new PowerSchemeInfo(guid, ReadFriendlyName(guid) ?? guid.ToString("D"));
        }
        finally
        {
            Native.LocalFree(pointer);
        }
    }

    public bool TrySetActiveScheme(Guid scheme, out string? error)
    {
        var result = Native.PowerSetActiveScheme(0, in scheme);
        error = result == Native.ErrorSuccess ? null : $"PowerSetActiveScheme 失败，Win32 错误码 {result}。";
        return result == Native.ErrorSuccess;
    }

    private const uint ErrorMoreData = 234;

    private static string? ReadFriendlyName(Guid scheme)
    {
        var buffer = new byte[256];
        uint size = (uint)buffer.Length;
        var result = Native.PowerReadFriendlyName(0, in scheme, 0, 0, buffer, ref size);

        if (result == ErrorMoreData && size > buffer.Length)
        {
            buffer = new byte[size];
            size = (uint)buffer.Length;
            result = Native.PowerReadFriendlyName(0, in scheme, 0, 0, buffer, ref size);
        }

        if (result != Native.ErrorSuccess || size == 0)
        {
            return null;
        }

        var length = (int)Math.Min(size, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
    }
}
