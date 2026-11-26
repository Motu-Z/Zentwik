using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>
/// 核映射的单一来源：优先 APOB 原始表，其次 CPUID 0x8000001E 核号与 L3 缓存域。
/// 指定了 APOB 却不能解析时不改用别的来源——那说明映射假设已不成立。
/// </summary>
internal static class AmdCoreMapResolver
{
    public static AmdCoreMap Resolve(ISystemInfo info, uint family, out string? error)
    {
        ArgumentNullException.ThrowIfNull(info);
        error = null;

        if (AmdApobSource.TryLoad(out var table, out var loadError))
        {
            if (AmdApobCoreMap.TryParse(table!, family, info.ProcessorCount, out var map, out var parseError))
            {
                return map;
            }

            error = parseError;
            return AmdCoreMap.Empty;
        }

        if (loadError is not null)
        {
            error = loadError;
            return AmdCoreMap.Empty;
        }

        return AmdWindowsTopology.Resolve(family, info.PhysicalCoreCount, info.ProcessorCount, out error);
    }
}
