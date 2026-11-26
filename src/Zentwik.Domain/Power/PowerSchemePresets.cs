namespace Zentwik.Domain.Power;

/// <summary>一个内置电源方案模板。Guid 是系统固定标识，Name 是模板名（与系统语言无关）。</summary>
public sealed record PowerSchemePreset(Guid Guid, string Name);

/// <summary>Windows 自带的四个电源方案模板。仅凭 GUID 判定是否内置，与当前显示名无关。</summary>
public static class PowerSchemePresets
{
    public static readonly PowerSchemePreset Balanced =
        new(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "平衡");

    public static readonly PowerSchemePreset PowerSaver =
        new(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), "节能");

    public static readonly PowerSchemePreset HighPerformance =
        new(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "高性能");

    public static readonly PowerSchemePreset UltimatePerformance =
        new(new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61"), "卓越性能");

    /// <summary>按界面顺序排列：从省电到性能。</summary>
    public static IReadOnlyList<PowerSchemePreset> All { get; } =
        [PowerSaver, Balanced, HighPerformance, UltimatePerformance];

    /// <summary>只看 GUID：用户把“平衡”改名后它仍是内置方案，删掉会失去模板。</summary>
    public static bool IsBuiltIn(Guid guid) => All.Any(preset => preset.Guid == guid);

    public static PowerSchemePreset? Find(Guid guid) => All.FirstOrDefault(preset => preset.Guid == guid);
}
