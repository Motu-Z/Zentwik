using System;
using System.Collections.Generic;
using Zentwik.Core.Ipc;

namespace Zentwik.Core.Helpers;

/// <summary>托盘菜单的一项。<see cref="Key"/> 由构建方定义，点击后原样回传。</summary>
public sealed record TrayMenuItem(
    string Key,
    string Label,
    bool IsChecked = false,
    IReadOnlyList<TrayMenuItem>? Children = null,
    bool IsSeparator = false);

/// <summary>托盘菜单里的一组方案。</summary>
public sealed record TrayPlanGroup(string Label, IReadOnlyList<TrayMenuItem> Items);

/// <summary>托盘菜单内容：只产数据，不碰 Win32 —— 后台服务与诊断代码共用同一份 key 编码。</summary>
public static class TrayMenu
{
    public const string KeyShow = "show";

    public const string KeyExit = "exit";

    private const string PlanPrefix = "plan:";

    public static IReadOnlyList<TrayMenuItem> Build(IReadOnlyList<TrayPlanGroup> groups)
    {
        List<TrayMenuItem> items = new() { new TrayMenuItem(KeyShow, "显示主窗口") };

        List<TrayMenuItem> blocks = new(groups.Count);

        foreach (TrayPlanGroup group in groups)
        {
            // 空组不出现：点开是张空白，比没有更让人困惑。
            if (group.Items.Count > 0)
            {
                blocks.Add(new TrayMenuItem(string.Empty, group.Label, Children: group.Items));
            }
        }

        if (blocks.Count > 0)
        {
            items.Add(Separator);
            items.AddRange(blocks);
        }

        items.Add(Separator);
        items.Add(new TrayMenuItem(KeyExit, "退出 Zentwik"));

        return items;
    }

    public static TrayMenuItem Separator => new(string.Empty, string.Empty, IsSeparator: true);

    public static string PlanKey(Services.PlanKind kind, string id) =>
        string.Concat(PlanPrefix, PlanKinds.ToWire(kind), ":", id);

    /// <summary>解析 <c>plan:&lt;kind&gt;:&lt;id&gt;</c>；不是方案项返回 false。</summary>
    public static bool TryParsePlanKey(string? key, out Services.PlanKind kind, out string id)
    {
        kind = Services.PlanKind.Tuning;
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
