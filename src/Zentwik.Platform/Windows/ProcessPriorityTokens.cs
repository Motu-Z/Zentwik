namespace Zentwik.Platform.Windows;

/// <summary>
/// 调度档位字符串 ↔ Win32 优先级类 + EcoQoS 的映射。数据层只认枚举名与 EcoQoS。
/// </summary>
/// <remarks>
/// 旧版界面用的是带空格的显示文案（"Above Normal"），新版方案/协议里的值是枚举名（"AboveNormal"），
/// 两者不能混用。EcoQoS 不是独立的优先级类：它等于 Idle 类加上 PROCESS_POWER_THROTTLING 的
/// ExecutionSpeed 位，所以只能靠回读该状态位区分。
/// </remarks>
public static class ProcessPriorityTokens
{
    public const string EcoQoS = "EcoQoS";

    private const uint PriorityClassIdle = 0x00000040;
    private const uint PriorityClassBelowNormal = 0x00004000;
    private const uint PriorityClassNormal = 0x00000020;
    private const uint PriorityClassAboveNormal = 0x00008000;
    private const uint PriorityClassHigh = 0x00000080;
    private const uint PriorityClassRealTime = 0x00000100;

    /// <summary>
    /// 档位字符串 → 优先级类。EcoQoS 额外要求置位节流状态；RealTime 照原样通过（不拦，也不对外提供下拉）。
    /// </summary>
    public static bool TryMap(string? token, out uint priorityClass, out bool eco)
    {
        eco = false;
        priorityClass = 0;

        switch (token?.Trim().ToLowerInvariant())
        {
            case "high":
                priorityClass = PriorityClassHigh;
                return true;
            case "abovenormal":
                priorityClass = PriorityClassAboveNormal;
                return true;
            case "normal":
                priorityClass = PriorityClassNormal;
                return true;
            case "belownormal":
                priorityClass = PriorityClassBelowNormal;
                return true;
            case "idle":
                priorityClass = PriorityClassIdle;
                return true;
            case "ecoqos":
                priorityClass = PriorityClassIdle;
                eco = true;
                return true;
            case "realtime":
                priorityClass = PriorityClassRealTime;
                return true;
            default:
                return false;
        }
    }

    /// <summary>优先级类 + 节流状态 → 档位字符串。EcoQoS 与 Idle 的差别只在这个状态位上。</summary>
    public static string Describe(uint priorityClass, bool eco) => priorityClass switch
    {
        PriorityClassIdle => eco ? EcoQoS : "Idle",
        PriorityClassBelowNormal => "BelowNormal",
        PriorityClassNormal => "Normal",
        PriorityClassAboveNormal => "AboveNormal",
        PriorityClassHigh => "High",
        PriorityClassRealTime => "RealTime",
        _ => priorityClass.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>调度页的基础优先级（Toolhelp/NtQuerySystemInformation 的 BasePriority）→ 档位。</summary>
    /// <remarks>取值只有 4/6/8/10/13/24/32 几档，不随线程动态优先级浮动；表外值归 Normal。</remarks>
    public static string DescribeBasePriority(int basePriority) => basePriority switch
    {
        4 => "Idle",
        6 => "BelowNormal",
        8 => "Normal",
        10 => "AboveNormal",
        13 => "High",
        _ => "Normal",
    };
}
