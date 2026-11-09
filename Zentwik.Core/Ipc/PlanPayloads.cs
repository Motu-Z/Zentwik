using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 方案类别在 IPC 上的字符串写法。
    /// </summary>
    /// <remarks>
    /// 序号一旦调整（比如以后在中间插一类），老 UI 与新 Worker 之间的 1 会指向另一种方案，
    /// 用户看到的是「我明明切的是电源方案，进程绑核被改了」。字符串写错就直接失败，不会错位。
    /// </remarks>
    public static class PlanKinds
    {
        public const string Tuning = "tuning";

        public const string Scheduling = "scheduling";

        public const string Power = "power";

        /// <summary>把线上的字符串折成枚举。无法识别时返回 false。</summary>
        public static bool TryParse(string? value, out Services.PlanKind kind)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case Tuning:
                    kind = Services.PlanKind.Tuning;
                    return true;

                case Scheduling:
                    kind = Services.PlanKind.Scheduling;
                    return true;

                case Power:
                    kind = Services.PlanKind.Power;
                    return true;

                default:
                    kind = Services.PlanKind.Tuning;
                    return false;
            }
        }

        public static string ToWire(Services.PlanKind kind) => kind switch
        {
            Services.PlanKind.Tuning => Tuning,
            Services.PlanKind.Scheduling => Scheduling,
            _ => Power
        };
    }

    /// <summary>列表里的一份方案摘要。</summary>
    public sealed class PlanEntryPayload
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = PlanKinds.Tuning;

        /// <summary>是否当前生效的方案（本类中唯一的一个）。</summary>
        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        /// <summary>逐核 CO。仅调校方案有值；空数组 = 不碰 CO。</summary>
        [JsonPropertyName("coreMargins")]
        public int[] CoreMargins { get; set; } = Array.Empty<int>();

        /// <summary>FMax（MHz）。0 = 不碰。仅调校方案有值。</summary>
        [JsonPropertyName("fmaxMhz")]
        public uint FMaxMHz { get; set; }

        /// <summary>规则条数。仅调度方案使用，界面靠它显示「N 条规则」。</summary>
        [JsonPropertyName("ruleCount")]
        public int RuleCount { get; set; }

        /// <summary>绑定的 Windows 电源方案 GUID。仅电源方案使用。</summary>
        [JsonPropertyName("powerSchemeGuid")]
        public string PowerSchemeGuid { get; set; } = string.Empty;

        /// <summary>存入时的系统名，仅用于显示。</summary>
        [JsonPropertyName("powerSchemeName")]
        public string PowerSchemeName { get; set; } = string.Empty;

        /// <summary>
        /// 五项电源策略的参数快照。仅电源方案使用；null = 这份方案只引用系统计划。
        /// </summary>
        [JsonPropertyName("powerPolicy")]
        public ApplyPowerPolicyPayload? PowerPolicy { get; set; }
    }

    /// <summary><c>query-plans</c> 的请求体：只取某一类方案。</summary>
    public sealed class PlanQueryPayload
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = PlanKinds.Tuning;
    }

    /// <summary><c>query-plans</c> 的返回体。</summary>
    public sealed class PlanListPayload
    {
        /// <summary>这一类里当前生效的方案 Id；没有时为 null。</summary>
        [JsonPropertyName("activeId")]
        public string? ActiveId { get; set; }

        [JsonPropertyName("plans")]
        public List<PlanEntryPayload> Plans { get; set; } = new();
    }

    /// <summary>
    /// <c>save-plan</c> 的请求体：新建或按 Id 覆盖一份方案。
    /// </summary>
    /// <remarks>
    /// 调校页上的值是用户正在编辑的（可能还没应用），必须由界面送上来；
    /// 调度页编辑的就是全局规则集本身，Worker 直接抓当前那份即可 ——
    /// 让 UI 把几十条规则序列化一遍再送回来，只会多一个不同步的机会。
    /// </remarks>
    public sealed class SavePlanPayload
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = PlanKinds.Tuning;

        /// <summary>null / 空 = 新建，由 Worker 生成 Id。</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>逐核 CO。仅调校方案使用。</summary>
        [JsonPropertyName("coreMargins")]
        public int[]? CoreMargins { get; set; }

        /// <summary>FMax（MHz）。仅调校方案使用。</summary>
        [JsonPropertyName("fmaxMhz")]
        public uint? FMaxMHz { get; set; }

        /// <summary>
        /// 可选的 Windows 电源方案 GUID。仅电源方案使用；
        /// 不传表示「不动这一块」，新建时留空即「应用时不切换系统方案」。
        /// </summary>
        [JsonPropertyName("powerSchemeGuid")]
        public string? PowerSchemeGuid { get; set; }

        /// <summary>
        /// 五项电源策略的参数快照。仅电源方案使用；
        /// 不传表示沿用原有快照，传了就整份覆盖。
        /// </summary>
        [JsonPropertyName("powerPolicy")]
        public ApplyPowerPolicyPayload? PowerPolicy { get; set; }
    }

    /// <summary><c>delete-plan</c> / <c>apply-plan</c> 的请求体。</summary>
    public sealed class PlanRefPayload
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = PlanKinds.Tuning;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    /// <summary><c>rename-plan</c> 的请求体。</summary>
    public sealed class RenamePlanPayload
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = PlanKinds.Tuning;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("newName")]
        public string NewName { get; set; } = string.Empty;
    }
}
