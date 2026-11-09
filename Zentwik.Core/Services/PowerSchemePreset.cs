using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 一个内置电源方案模板。
    /// </summary>
    /// <remarks>
    /// 这四个 GUID 是 Windows 自带的方案标识，几十年没变过，OEM 也不会去改。
    /// 从注册表反查名字会在中英文系统上得到不同串（「Balanced」/「平衡」），
    /// 而这里给的是**模板名**，用于复制出来的新方案默认叫什么 —— 与系统语言无关。
    /// </remarks>
    public sealed class PowerSchemePreset
    {
        public PowerSchemePreset(Guid guid, string name)
        {
            Guid = guid;
            Name = name;
        }

        /// <summary>模板方案的 GUID。即使它当前不在方案列表里，注册表中的模板仍然存在。</summary>
        public Guid Guid { get; }

        /// <summary>复制出来的方案默认使用的名字。</summary>
        public string Name { get; }
    }

    /// <summary>
    /// Windows 自带的四个电源方案模板。
    /// </summary>
    /// <remarks>
    /// 方案被删掉、或从未在界面出现过（卓越性能默认隐藏、节能与高性能在高版本
    /// Windows 里被移除入口），它们在 <c>PowerSchemes</c> 键下的模板仍然保留。
    /// 实测四个 GUID 都能作为 <c>PowerDuplicateScheme</c> 的源成功派生，
    /// 所以界面上不需要「先开启再新建」这一套。
    ///
    /// 一个电源方案有几十项设置（硬盘超时、睡眠、USB 选择性暂停、显示亮度……），
    /// 我们只管其中五项。「自己造一个」等于把剩下几十项留成默认值 ——
    /// 派生微软的模板则能完整继承那份调好的配置，两边的差别只在我们改的那五项。
    /// </remarks>
    public static class PowerSchemePresets
    {
        /// <summary>平衡（<c>381b4222-f694-41f0-9685-ff5bb260df2e</c>）。</summary>
        public static readonly PowerSchemePreset Balanced =
            new(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "平衡");

        /// <summary>节能（<c>a1841308-3541-4fab-bc81-f71556f20b4a</c>）。</summary>
        public static readonly PowerSchemePreset PowerSaver =
            new(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), "节能");

        /// <summary>高性能（<c>8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c</c>）。</summary>
        public static readonly PowerSchemePreset HighPerformance =
            new(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "高性能");

        /// <summary>卓越性能（<c>e9a42b02-d5df-448d-aa00-03f14749eb61</c>）。</summary>
        public static readonly PowerSchemePreset UltimatePerformance =
            new(new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61"), "卓越性能");

        /// <summary>按界面顺序排列：从省电到性能。</summary>
        public static IReadOnlyList<PowerSchemePreset> All { get; } = new[]
        {
            PowerSaver,
            Balanced,
            HighPerformance,
            UltimatePerformance
        };

        /// <summary>
        /// 这个 GUID 是不是四个内置模板之一。
        /// </summary>
        /// <remarks>
        /// 内置方案的 GUID 是固定的、全局唯一的，因此「是不是内置」只看 GUID，
        /// 不看名字 —— 用户把「平衡」改成「我的方案」，它仍然是内置的那一份，
        /// 删掉之后系统会失去平衡模式的模板。
        /// </remarks>
        public static bool IsBuiltIn(Guid guid)
        {
            foreach (PowerSchemePreset preset in All)
            {
                if (preset.Guid == guid)
                    return true;
            }

            return false;
        }

        /// <summary>按 GUID 找模板，找不到返回 null。</summary>
        public static PowerSchemePreset? Find(Guid guid)
        {
            foreach (PowerSchemePreset preset in All)
            {
                if (preset.Guid == guid)
                    return preset;
            }

            return null;
        }
    }
}
