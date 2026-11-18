using System;
using System.Collections.Generic;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI.Services;

/// <summary>托盘菜单的一项。<see cref="Key"/> 由构建方定义，点击后原样回传。</summary>
public sealed record TrayMenuItem(
    string Key,
    string Label,
    bool IsChecked = false,
    IReadOnlyList<TrayMenuItem>? Children = null,
    bool IsSeparator = false);

/// <summary>
/// 托盘菜单的内容。只产出数据、不碰 Win32 —— 纯函数才断言得到，菜单本身点不了自动化。
/// </summary>
public static class TrayMenuBuilder
{
    public const string KeyShow = "show";

    public const string KeyExit = "exit";

    private const string PlanPrefix = "plan:";

    /// <param name="includePlans">托盘是否列出三类方案。</param>
    public static IReadOnlyList<TrayMenuItem> Build(bool includePlans)
    {
        List<TrayMenuItem> items = new() { new TrayMenuItem(KeyShow, "显示主窗口") };

        if (includePlans)
        {
            List<TrayMenuItem> groups = new();

            AddGroup(groups, PlanHub.Tuning, "调校方案");
            AddGroup(groups, PlanHub.Scheduling, "调度方案");
            AddGroup(groups, PlanHub.Power, "电源方案");

            // 三类都没有方案时整块不出现：空子菜单点开是张空白，比没有更让人困惑。
            if (groups.Count > 0)
            {
                items.Add(Separator);
                items.AddRange(groups);
            }
        }

        items.Add(Separator);
        items.Add(new TrayMenuItem(KeyExit, "退出 Zentwik"));

        return items;
    }

    public static TrayMenuItem Separator => new(string.Empty, string.Empty, IsSeparator: true);

    private static void AddGroup(List<TrayMenuItem> groups, PlanScope scope, string label)
    {
        if (scope.Items.Count == 0)
        {
            return;
        }

        List<TrayMenuItem> children = new(scope.Items.Count);

        foreach (PlanItem plan in scope.Items)
        {
            children.Add(new TrayMenuItem(PlanKey(scope.Kind, plan.Id), plan.Name, plan.IsActive));
        }

        groups.Add(new TrayMenuItem(string.Empty, label, Children: children));
    }

    public static string PlanKey(PlanKind kind, string id) =>
        string.Concat(PlanPrefix, PlanKinds.ToWire(kind), ":", id);

    /// <summary>解析 <c>plan:&lt;kind&gt;:&lt;id&gt;</c>；不是方案项返回 false。</summary>
    public static bool TryParsePlanKey(string? key, out PlanKind kind, out string id)
    {
        kind = PlanKind.Tuning;
        id = string.Empty;

        if (key is null || !key.StartsWith(PlanPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int split = key.IndexOf(':', PlanPrefix.Length);

        if (split < 0)
        {
            return false;
        }

        id = key[(split + 1)..];

        return id.Length > 0 && PlanKinds.TryParse(key[PlanPrefix.Length..split], out kind);
    }
}
