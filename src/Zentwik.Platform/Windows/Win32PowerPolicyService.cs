using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Zentwik.Platform.Windows;

/// <summary>读取无需管理员；写入失败返回 Win32 错误码由上层处理。</summary>
public sealed class Win32PowerPolicyService : IPowerPolicyService
{
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const uint AccessScheme = 16;
    private const uint AccessSubGroup = 17;
    private const uint AccessIndividualSetting = 18;

    /// <summary>全部方案键（含隐藏的）所在位置。powrprof 没有对应的枚举接口。</summary>
    private const string SchemeRegistryPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

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

    /// <remarks>
    /// 可见性只看 <see cref="ListSchemes"/>；方案是否存在看注册表键。
    /// 注册表里还有 Win11「电源模式」的 overlay 占位键，它们只有名字、没有子组，不是方案，按子组数为 0 排除。
    /// </remarks>
    public IReadOnlyList<PowerSchemeEntry> ListAllSchemes()
    {
        var visible = ListSchemes().Select(static scheme => scheme.Guid).ToHashSet();
        var entries = new List<PowerSchemeEntry>();

        foreach (var (guid, isScheme) in ReadSchemeKeys())
        {
            if (!isScheme)
            {
                continue;
            }

            entries.Add(new PowerSchemeEntry(
                guid,
                ReadFriendlyName(guid) ?? guid.ToString("D"),
                visible.Contains(guid)));
        }

        // 注册表读不到（权限或布局变化）时退回可见列表：列表整个空掉比缺几项更难排查。
        return entries.Count > 0
            ? entries
            : ListSchemes().Select(static scheme => new PowerSchemeEntry(scheme.Guid, scheme.FriendlyName, true)).ToArray();
    }

    /// <remarks>
    /// 用「有没有子组子键」区分真方案与 overlay 占位键。不能用 PowerEnumerate 判断：
    /// 对 overlay 这类不存在的方案，它可能返回成功并给出非空内容（实测如此），会把占位键误判成方案。
    /// </remarks>
    private static IEnumerable<(Guid Guid, bool IsScheme)> ReadSchemeKeys()
    {
        using var schemes = Registry.LocalMachine.OpenSubKey(SchemeRegistryPath);

        if (schemes is null)
        {
            yield break;
        }

        foreach (var name in schemes.GetSubKeyNames())
        {
            if (!Guid.TryParse(name, out var guid))
            {
                continue;
            }

            using var key = schemes.OpenSubKey(name);
            yield return (guid, key is not null && key.SubKeyCount > 0);
        }
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

    public bool RenameScheme(Guid scheme, string name, out string? error) =>
        WriteText(scheme, name, description: false, out error);

    public bool DeleteScheme(Guid scheme, out string? error)
    {
        var result = Native.PowerDeleteScheme(0, in scheme);
        error = result == Native.ErrorSuccess ? null : Describe("PowerDeleteScheme", result);
        return result == Native.ErrorSuccess;
    }

    public IReadOnlyList<Guid> EnumerateSubgroups(Guid scheme) =>
        EnumerateGuids(scheme, null, AccessSubGroup);

    public IReadOnlyList<Guid> EnumerateSettings(Guid scheme, Guid subGroup) =>
        EnumerateGuids(scheme, subGroup, AccessIndividualSetting);

    public string? ReadDescription(Guid scheme)
    {
        var buffer = new byte[256];
        uint size = (uint)buffer.Length;
        var result = Native.PowerReadDescription(0, in scheme, 0, 0, buffer, ref size);

        if (result == ErrorMoreData && size > buffer.Length)
        {
            buffer = new byte[size];
            size = (uint)buffer.Length;
            result = Native.PowerReadDescription(0, in scheme, 0, 0, buffer, ref size);
        }

        if (result != Native.ErrorSuccess || size == 0)
        {
            return null;
        }

        var length = (int)Math.Min(size, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
    }

    public bool TryWriteDescription(Guid scheme, string description, out string? error) =>
        WriteText(scheme, description, description: true, out error);

    /// <summary>枚举方案或子组下的 GUID 列表。找不到更多项即结束，单项异常不中断整体。</summary>
    private static IReadOnlyList<Guid> EnumerateGuids(Guid scheme, Guid? subGroup, uint accessor)
    {
        var guids = new List<Guid>();
        var schemePointer = Marshal.AllocHGlobal(16);
        nint subGroupPointer = 0;

        try
        {
            Marshal.StructureToPtr(scheme, schemePointer, false);
            if (subGroup is not null)
            {
                subGroupPointer = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(subGroup.Value, subGroupPointer, false);
            }

            var buffer = new byte[16];
            for (uint index = 0; ; index++)
            {
                uint size = (uint)buffer.Length;
                var result = Native.PowerEnumerate(0, schemePointer, subGroupPointer, accessor, index, buffer, ref size);
                if (result == ErrorNoMoreItems)
                {
                    break;
                }

                if (result != Native.ErrorSuccess || size < 16)
                {
                    break;
                }

                var guid = new Guid(buffer.AsSpan(0, 16));

                // 传入不存在的方案时 PowerEnumerate 可能返回成功但内容为空。当成「没有子组」，
                // 否则会被记成一个 Guid.Empty，让 ListAllSchemes 把 overlay 占位键误判为方案。
                if (guid == Guid.Empty)
                {
                    break;
                }

                guids.Add(guid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(schemePointer);
            if (subGroupPointer != 0)
            {
                Marshal.FreeHGlobal(subGroupPointer);
            }
        }

        return guids;
    }

    /// <summary>写方案名或说明。长度按字节计且含结尾符：少 2 字节会丢最后一个字符，返回值仍是 0。</summary>
    private static bool WriteText(Guid scheme, string text, bool description, out string? error)
    {
        var bytes = Encoding.Unicode.GetByteCount(text) + sizeof(char);
        var buffer = Marshal.AllocHGlobal(bytes);

        try
        {
            var encoded = Encoding.Unicode.GetBytes(text + '\0');
            Marshal.Copy(encoded, 0, buffer, bytes);

            var result = description
                ? Native.PowerWriteDescription(0, in scheme, 0, 0, buffer, (uint)bytes)
                : Native.PowerWriteFriendlyName(0, in scheme, 0, 0, buffer, (uint)bytes);

            var api = description ? "PowerWriteDescription" : "PowerWriteFriendlyName";
            error = result == Native.ErrorSuccess ? null : Describe(api, result);
            return result == Native.ErrorSuccess;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
