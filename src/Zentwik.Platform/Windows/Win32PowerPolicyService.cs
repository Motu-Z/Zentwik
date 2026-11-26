using System.Runtime.InteropServices;
using System.Text;

namespace Zentwik.Platform.Windows;

/// <summary>读取无需管理员；写入失败返回 Win32 错误码由上层处理。</summary>
public sealed class Win32PowerPolicyService : IPowerPolicyService
{
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const uint AccessScheme = 16;

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

    public IReadOnlyList<PowerSchemeInfo> ListSchemes()
    {
        var schemes = new List<PowerSchemeInfo>();
        var buffer = new byte[16];

        for (uint index = 0; ; index++)
        {
            uint size = (uint)buffer.Length;
            var result = Native.PowerEnumerate(0, 0, 0, AccessScheme, index, buffer, ref size);
            if (result == ErrorNoMoreItems)
            {
                break;
            }

            if (result != Native.ErrorSuccess || size < 16)
            {
                break;
            }

            var guid = new Guid(buffer.AsSpan(0, 16));
            schemes.Add(new PowerSchemeInfo(guid, ReadFriendlyName(guid) ?? guid.ToString("D")));
        }

        return schemes;
    }

    public bool TrySetActiveScheme(Guid scheme, out string? error)
    {
        var result = Native.PowerSetActiveScheme(0, in scheme);
        error = result == Native.ErrorSuccess ? null : Describe("PowerSetActiveScheme", result);
        return result == Native.ErrorSuccess;
    }

    public bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error)
    {
        var result = ac
            ? Native.PowerReadACValueIndex(0, in scheme, in subGroup, in setting, out value)
            : Native.PowerReadDCValueIndex(0, in scheme, in subGroup, in setting, out value);

        error = result == Native.ErrorSuccess ? null : Describe("PowerReadValueIndex", result);
        return result == Native.ErrorSuccess;
    }

    public bool TryWriteValue(Guid scheme, Guid subGroup, Guid setting, bool ac, uint value, out string? error)
    {
        var result = ac
            ? Native.PowerWriteACValueIndex(0, in scheme, in subGroup, in setting, value)
            : Native.PowerWriteDCValueIndex(0, in scheme, in subGroup, in setting, value);

        error = result == Native.ErrorSuccess ? null : Describe("PowerWriteValueIndex", result);
        return result == Native.ErrorSuccess;
    }

    public Guid? DuplicateScheme(Guid template, out string? error)
    {
        var result = Native.PowerDuplicateScheme(0, in template, out var created);
        if (result != Native.ErrorSuccess || created == 0)
        {
            if (created != 0)
            {
                Native.LocalFree(created);
            }

            error = Describe("PowerDuplicateScheme", result);
            return null;
        }

        try
        {
            error = null;
            return Marshal.PtrToStructure<Guid>(created);
        }
        finally
        {
            Native.LocalFree(created);
        }
    }

    public bool RenameScheme(Guid scheme, string name, out string? error)
    {
        // 长度按字节计且含结尾符：少 2 字节会丢最后一个字符，返回值仍是 0。
        var bytes = Encoding.Unicode.GetByteCount(name) + sizeof(char);
        var buffer = Marshal.AllocHGlobal(bytes);

        try
        {
            var encoded = Encoding.Unicode.GetBytes(name + '\0');
            Marshal.Copy(encoded, 0, buffer, bytes);

            var result = Native.PowerWriteFriendlyName(0, in scheme, 0, 0, buffer, (uint)bytes);
            error = result == Native.ErrorSuccess ? null : Describe("PowerWriteFriendlyName", result);
            return result == Native.ErrorSuccess;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool DeleteScheme(Guid scheme, out string? error)
    {
        var result = Native.PowerDeleteScheme(0, in scheme);
        error = result == Native.ErrorSuccess ? null : Describe("PowerDeleteScheme", result);
        return result == Native.ErrorSuccess;
    }

    private static string Describe(string api, uint code) => code == 5
        ? $"{api} 失败：拒绝访问（需要管理员权限）。"
        : $"{api} 失败，Win32 错误码 {code}。";

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
