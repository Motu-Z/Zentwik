using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// <see cref="IPowerPolicyApi"/> 的实装：<c>powrprof.dll</c> 的薄封装，
    /// 外加方案说明文字这一项只能走注册表的读写。
    ///
    /// 调 API、处理非托管内存、把错误码原样返回。任何「这个值合不合法」
    /// 「失败该显示什么」的判断都不在这里 —— 那些属于 <see cref="PowerPolicyService"/>，
    /// 因为它们要能被假数据驱动着测。
    ///
    /// 五个 API 的原型都是 <c>const GUID*</c>，不是按值传结构体。C# 的 <c>Guid</c> 是
    /// 托管结构，取地址要 <c>fixed</c>（需要 unsafe）或 <c>GCHandle</c>（要 Pin 且容易漏 Free）。
    /// 这里统一走 <c>Marshal.AllocHGlobal</c> + <c>StructureToPtr</c>：代价是 16 字节的
    /// 堆分配，收益是每条路径的释放点都能写在同一处 <c>finally</c> 里 ——
    /// 电源 API 一秒钟最多被调几次，这点分配不值得为它引入指针生命周期管理。
    ///
    /// 那个指针是 API 内部用 <c>LocalAlloc</c> 分配的，不是我们分配的。
    /// 用 FreeHGlobal 释放 LocalAlloc 的内存是未定义行为（在启用了堆校验的环境下直接崩），
    /// 两者底层是不同的堆实现，必须配对。
    /// </summary>
    public sealed partial class PowerPolicyApi : IPowerPolicyApi
    {
        private const uint ERROR_SUCCESS = 0;

        /// <summary>
        /// 「缓冲区不够」——<c>PowerReadFriendlyName</c> 用来告知所需长度的返回值。
        ///
        /// 它不是失败：第一次调用故意传空缓冲区，就是为了拿到这个码与长度。
        /// 把它当失败会让 <see cref="ReadFriendlyName"/> 永远返回 null。
        /// </summary>
        private const uint ERROR_MORE_DATA = 234;

        /// <summary>全部电源方案在注册表里的父键。</summary>
        private const string SchemesKeyPath =
            @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        /// <summary>方案说明文字的值名。</summary>
        private const string DescriptionValueName = "Description";

        /// <inheritdoc />
        public Guid? GetActiveScheme()
        {
            uint result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr buffer);

            // 正常实现里失败时 buffer 为 null，但「返回值与输出参数不自洽」
            // 是 P/Invoke 里最常见的泄漏来源之一（上一次调用的残留、或实现变更）。
            // 这里先判指针再判结果，两条路都不漏。
            if (result != ERROR_SUCCESS || buffer == IntPtr.Zero)
            {
                if (buffer != IntPtr.Zero)
                    LocalFree(buffer);

                return null;
            }

            try
            {
                return Marshal.PtrToStructure<Guid>(buffer);
            }
            finally
            {
                LocalFree(buffer);
            }
        }

        /// <inheritdoc />
        public bool SetActiveScheme(Guid schemeGuid)
        {
            IntPtr scheme = AllocGuid(schemeGuid);

            try
            {
                return PowerSetActiveScheme(IntPtr.Zero, scheme) == ERROR_SUCCESS;
            }
            finally
            {
                Marshal.FreeHGlobal(scheme);
            }
        }

        /// <inheritdoc />
        public (uint Result, uint Value) ReadAcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting)
            => ReadValueIndex(schemeGuid, subgroup, setting, dc: false);

        /// <inheritdoc />
        public (uint Result, uint Value) ReadDcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting)
            => ReadValueIndex(schemeGuid, subgroup, setting, dc: true);

        /// <inheritdoc />
        public uint WriteAcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting, uint value)
            => WriteValueIndex(schemeGuid, subgroup, setting, value, dc: false);

        /// <inheritdoc />
        public uint WriteDcValueIndex(Guid schemeGuid, Guid subgroup, Guid setting, uint value)
            => WriteValueIndex(schemeGuid, subgroup, setting, value, dc: true);

        private (uint Result, uint Value) ReadValueIndex(
            Guid schemeGuid, Guid subgroup, Guid setting, bool dc)
        {
            IntPtr scheme = IntPtr.Zero;
            IntPtr sub = IntPtr.Zero;
            IntPtr set = IntPtr.Zero;

            try
            {
                scheme = AllocGuid(schemeGuid);
                sub = AllocGuid(subgroup);
                set = AllocGuid(setting);

                uint result;
                uint value;

                if (dc)
                    result = PowerReadDCValueIndex(IntPtr.Zero, scheme, sub, set, out value);
                else
                    result = PowerReadACValueIndex(IntPtr.Zero, scheme, sub, set, out value);

                // 失败时 out 参数是封送层填的 0，不是系统里的真实值。
                // 原样返回它会让调用方在忘了判 Result 时读到「EPP = 0」这种看似合法的假数据，
                // 所以失败一律归零，让「没读到」在数值上也留不下痕迹。
                return (result, result == ERROR_SUCCESS ? value : 0u);
            }
            finally
            {
                FreeGuid(scheme);
                FreeGuid(sub);
                FreeGuid(set);
            }
        }

        private uint WriteValueIndex(
            Guid schemeGuid, Guid subgroup, Guid setting, uint value, bool dc)
        {
            IntPtr scheme = IntPtr.Zero;
            IntPtr sub = IntPtr.Zero;
            IntPtr set = IntPtr.Zero;

            try
            {
                scheme = AllocGuid(schemeGuid);
                sub = AllocGuid(subgroup);
                set = AllocGuid(setting);

                return dc
                    ? PowerWriteDCValueIndex(IntPtr.Zero, scheme, sub, set, value)
                    : PowerWriteACValueIndex(IntPtr.Zero, scheme, sub, set, value);
            }
            finally
            {
                FreeGuid(scheme);
                FreeGuid(sub);
                FreeGuid(set);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<Guid> EnumerateSubgroups(Guid schemeGuid)
            => EnumerateGuids(schemeGuid, null, AccessSubgroup);

        /// <inheritdoc />
        public IReadOnlyList<Guid> EnumerateSettings(Guid schemeGuid, Guid subgroup)
            => EnumerateGuids(schemeGuid, subgroup, AccessIndividualSetting);

        /// <summary>
        /// 枚举某个方案下的一层 GUID（子组或设置项）。
        ///
        /// 16 枚举方案本身、17 枚举方案下的子组、18 枚举子组下的设置项。
        /// 传错常量不会报错，只会返回空集合 —— 表现为「这个方案一项设置都没有」。
        /// </summary>
        private static IReadOnlyList<Guid> EnumerateGuids(Guid schemeGuid, Guid? subgroup, uint accessFlags)
        {
            List<Guid> result = new();
            IntPtr scheme = IntPtr.Zero;
            IntPtr sub = IntPtr.Zero;

            try
            {
                scheme = AllocGuid(schemeGuid);
                if (subgroup.HasValue)
                    sub = AllocGuid(subgroup.Value);

                for (uint index = 0; index < MaxSchemeCount; index++)
                {
                    uint size = (uint)Marshal.SizeOf<Guid>();
                    IntPtr buffer = Marshal.AllocHGlobal((int)size);

                    try
                    {
                        uint code = PowerEnumerate(
                            IntPtr.Zero, scheme, sub, accessFlags, index, buffer, ref size);

                        if (code != ERROR_SUCCESS)
                            break;

                        Guid guid = Marshal.PtrToStructure<Guid>(buffer);

                        if (guid != Guid.Empty)
                            result.Add(guid);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                FreeGuid(sub);
                FreeGuid(scheme);
            }

            return result;
        }

        /// <inheritdoc />
        public string? ReadFriendlyName(Guid schemeGuid)
        {
            IntPtr scheme = AllocGuid(schemeGuid);
            IntPtr buffer = IntPtr.Zero;

            try
            {
                // 第一次：缓冲区传空，只问长度。
                // 文档口径是「返回 ERROR_SUCCESS 并把所需长度写进 size」，
                // 但实测部分系统返回 ERROR_MORE_DATA —— 两个都当成功处理。
                uint size = 0;
                uint result = PowerReadFriendlyName(
                    IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);

                if ((result != ERROR_SUCCESS && result != ERROR_MORE_DATA) || size == 0)
                    return null;

                buffer = Marshal.AllocHGlobal((int)size);

                result = PowerReadFriendlyName(
                    IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);

                if (result != ERROR_SUCCESS)
                    return null;

                return ParseFriendlyName(buffer, size);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);

                Marshal.FreeHGlobal(scheme);
            }
        }

        /// <inheritdoc />
        public Guid? DuplicateScheme(Guid sourceSchemeGuid)
        {
            IntPtr source = AllocGuid(sourceSchemeGuid);

            try
            {
                uint result = PowerDuplicateScheme(IntPtr.Zero, source, out IntPtr created);

                if (result != ERROR_SUCCESS || created == IntPtr.Zero)
                {
                    if (created != IntPtr.Zero)
                        LocalFree(created);

                    return null;
                }

                try
                {
                    return Marshal.PtrToStructure<Guid>(created);
                }
                finally
                {
                    LocalFree(created);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(source);
            }
        }

        /// <inheritdoc />
        public bool RenameScheme(Guid schemeGuid, string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            IntPtr scheme = AllocGuid(schemeGuid);
            IntPtr buffer = IntPtr.Zero;

            try
            {
                // 长度按字节计，且必须把结尾符算进去：API 不做截断，
                // 少 2 字节会让名字丢掉最后一个字符，而返回值仍然是 0。
                int bytes = Encoding.Unicode.GetByteCount(name) + sizeof(char);
                byte[] encoded = Encoding.Unicode.GetBytes(name + '\0');

                buffer = Marshal.AllocHGlobal(bytes);
                Marshal.Copy(encoded, 0, buffer, bytes);

                return PowerWriteFriendlyName(
                    IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, (uint)bytes) == ERROR_SUCCESS;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);

                Marshal.FreeHGlobal(scheme);
            }
        }

        /// <inheritdoc />
        public string? ReadSchemeDescription(Guid schemeGuid)
        {
            try
            {
                using RegistryKey? baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);

                using RegistryKey? key = baseKey?.OpenSubKey(
                    $@"{SchemesKeyPath}\{schemeGuid:D}");

                return key?.GetValue(DescriptionValueName) as string;
            }
            catch (Exception)
            {
                // 读不到说明不该让新建失败：说明只是控制面板里的一行字。
                return null;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 走 <c>PowerWriteDescription</c> 而不是直接写注册表：这些键的 ACL 只给了
        /// Administrators 读权限（全权在 SYSTEM 手上），提权进程直接 SetValue 会静默失败；
        /// 与改名同样走 API，行为才和系统自带工具一致。
        /// </remarks>
        public bool WriteSchemeDescription(Guid schemeGuid, string description)
        {
            ArgumentNullException.ThrowIfNull(description);

            IntPtr scheme = AllocGuid(schemeGuid);
            IntPtr buffer = IntPtr.Zero;

            try
            {
                int bytes = Encoding.Unicode.GetByteCount(description) + sizeof(char);
                byte[] encoded = Encoding.Unicode.GetBytes(description + '\0');

                buffer = Marshal.AllocHGlobal(bytes);
                Marshal.Copy(encoded, 0, buffer, bytes);

                return PowerWriteDescription(
                    IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, (uint)bytes) == ERROR_SUCCESS;
            }
            catch (Exception)
            {
                // 旧系统上若没导出这个入口，说明文字保持派生源的那份即可。
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);

                Marshal.FreeHGlobal(scheme);
            }
        }

        /// <inheritdoc />
        public bool DeleteScheme(Guid schemeGuid)
        {
            IntPtr scheme = AllocGuid(schemeGuid);

            try
            {
                return PowerDeleteScheme(IntPtr.Zero, scheme) == ERROR_SUCCESS;
            }
            finally
            {
                Marshal.FreeHGlobal(scheme);
            }
        }

        /// <summary>
        /// 把 <c>PowerReadFriendlyName</c> 写出的 UTF-16 缓冲区解析成字符串。
        ///
        /// 这是整条读取路径里唯一「可能悄悄出错但不抛异常」的一步：编码猜错会得到
        /// 一串乱码，长度算错会读到相邻内存。它又完全不需要 powrprof.dll ——
        /// 抽成静态方法之后，测试可以自己造一个 UTF-16 缓冲区把它钉住，
        /// 而不必去改真实电源方案的名字（那需要管理员权限，而且改完还得改回来）。
        ///
        /// 不传长度的重载会一直读到第一个 <c>\0</c>。缓冲区是我们按
        /// <paramref name="byteCount"/> 分配的，一旦 API 没写结尾符（或写少了），
        /// 那个重载就会越过缓冲区继续读 —— 读到的是相邻堆块的内容，
        /// 表现为「方案名后面挂了一串随机汉字」。限长读最多读满自己这块内存。
        ///
        /// <paramref name="byteCount"/> 是**字节**数（API 的 DWORD 口径），
        /// 字符数是它的一半；不足一个字符时视为没有内容。
        /// </summary>
        internal static string? ParseFriendlyName(IntPtr buffer, uint byteCount)
        {
            if (buffer == IntPtr.Zero || byteCount < sizeof(char))
                return null;

            int chars = (int)(byteCount / sizeof(char));
            string raw = Marshal.PtrToStringUni(buffer, chars) ?? string.Empty;

            // 带长度参数的重载是「复制指定个数的字符」，不会在零结尾处停下 ——
            // 而 API 写出的长度是**含**结尾符的。不截断的话，方案名会带上一个
            // 看不见的 U+0000：界面上看不出异常，但它与任何字面量都不相等，
            // 表现为「明明显示的是平衡，却匹配不上」。
            int end = raw.IndexOf('\0');
            if (end >= 0)
                raw = raw[..end];

            // 全是空白等同于没名字：界面上显示一串空格比显示「未知」更糟。
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }

        /// <inheritdoc />
        public IReadOnlyList<(Guid Guid, string? Name)> EnumerateSchemes()
        {
            List<(Guid Guid, string? Name)> schemes = new();
            HashSet<Guid> seen = new();

            // Index 从 0 递增到系统返回 ERROR_NO_MORE_ITEMS 为止。
            // 上限 256 是防御：某些第三方电源管理软件会让索引永不终止，
            // 无限循环会把 Worker 的一次普通查询变成卡死。
            for (uint index = 0; index < MaxSchemeCount; index++)
            {
                uint size = (uint)Marshal.SizeOf<Guid>();
                IntPtr buffer = Marshal.AllocHGlobal((int)size);

                try
                {
                    uint result = PowerEnumerate(
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        AccessScheme,
                        index,
                        buffer,
                        ref size);

                    if (result != ERROR_SUCCESS)
                        break;

                    Guid guid = Marshal.PtrToStructure<Guid>(buffer);

                    if (guid == Guid.Empty || !seen.Add(guid))
                        continue;

                    schemes.Add((guid, ReadFriendlyName(guid)));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return schemes;
        }

        /// <inheritdoc />
        public (bool HasBattery, bool? OnAcPower) ReadPowerSupply()
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
                return (false, null);

            // 255（未知）按「没有电池」处理：这一位读不准时，界面上多出一个
            // 改不动的电池策略比少一个更糟。台式机上这个值通常是 128（无电池）。
            bool hasBattery = status.BatteryFlag is not (BatteryFlagNoBattery or BatteryFlagUnknown);

            // ACLineStatus：0 = 用电池，1 = 交流电，255 = 未知。
            bool? onAc = status.ACLineStatus switch
            {
                AcLineOffline => false,
                AcLineOnline => true,
                _ => null,
            };

            return (hasBattery, onAc);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        /// <summary>
        private static void FreeGuid(IntPtr value)
        {
            if (value != IntPtr.Zero)
                Marshal.FreeHGlobal(value);
        }

        /// 把 <see cref="Guid"/> 复制到非托管堆，返回其地址。调用方负责释放。
        /// </summary>
        private static IntPtr AllocGuid(Guid value)
        {
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(value, buffer, fDeleteOld: false);
            return buffer;
        }

        // ---------------- P/Invoke ----------------

        /// <summary><see cref="PowerEnumerate"/> 的 <c>AccessFlags</c>：枚举方案本身。</summary>
        private const uint AccessScheme = 16;

        /// <summary>枚举方案下的设置子组。</summary>
        private const uint AccessSubgroup = 17;

        /// <summary>枚举子组下的设置项。</summary>
        private const uint AccessIndividualSetting = 18;

        private const int MaxSchemeCount = 256;

        private const byte BatteryFlagNoBattery = 128;
        private const byte BatteryFlagUnknown = 255;

        private const byte AcLineOffline = 0;
        private const byte AcLineOnline = 1;

        /// <summary>
        /// 取当前活动电源方案的 GUID。成功时由系统用 <c>LocalAlloc</c> 分配，调用方必须 <c>LocalFree</c>。
        /// </summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        /// <summary>把指定方案设为活动方案。改过的设置项要重新激活一次才生效。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, IntPtr schemeGuid);

        /// <summary>读某个设置项的交流电（AC）取值。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerReadACValueIndex(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            out uint acValueIndex);

        /// <summary>写某个设置项的交流电（AC）取值。只改方案里的值，不触发激活。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerWriteACValueIndex(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            uint acValueIndex);

        /// <summary>读某个设置项的电池（DC）取值。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerReadDCValueIndex(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            out uint dcValueIndex);

        /// <summary>写某个设置项的电池（DC）取值。只改方案里的值，不触发激活。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerWriteDCValueIndex(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            uint dcValueIndex);

        /// <summary>
        /// 按索引枚举电源数据。三个 GUID 均传 null 且 <c>AccessFlags = 16</c> 时枚举方案。
        /// 索引耗尽返回 259（<c>ERROR_NO_MORE_ITEMS</c>）。
        /// </summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerEnumerate(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            uint accessFlags,
            uint index,
            IntPtr buffer,
            ref uint bufferSize);

        /// <summary>
        /// 以现有方案为模板复制出新方案。成功时用 <c>LocalAlloc</c> 分配新 GUID，调用方必须 <c>LocalFree</c>。
        /// </summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerDuplicateScheme(
            IntPtr rootPowerKey,
            IntPtr sourceSchemeGuid,
            out IntPtr destinationSchemeGuid);

        /// <summary>
        /// 写方案 / 设置项的显示名。缓冲区是 UTF-16 字节流，长度按字节计（含结尾符）。
        /// 子组与设置项都传 null 时改的是方案名。
        /// </summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerWriteFriendlyName(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            IntPtr buffer,
            uint bufferSize);

        /// <summary>写方案 / 设置项的说明文字。缓冲区是 UTF-16 字节流，长度按字节计。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerWriteDescription(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            IntPtr buffer,
            uint bufferSize);

        /// <summary>删除方案。</summary>
        [LibraryImport("powrprof.dll")]
        private static partial uint PowerDeleteScheme(IntPtr rootPowerKey, IntPtr schemeGuid);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>
        /// 读方案 / 设置项的显示名。子组与设置项都传 null 时读的是方案名。
        /// 缓冲区是 UTF-16 字节流，长度由 <paramref name="bufferSize"/> 进出。
        /// </summary>
        [LibraryImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
        private static partial uint PowerReadFriendlyName(
            IntPtr rootPowerKey,
            IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid,
            IntPtr powerSettingGuid,
            IntPtr buffer,
            ref uint bufferSize);

        /// <summary>释放系统分配的内存（与 <c>LocalAlloc</c> 配对）。</summary>
        [LibraryImport("kernel32.dll")]
        private static partial IntPtr LocalFree(IntPtr memory);
    }
}
