using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Zentwik.UI.Services;

/// <summary>
/// 界面自己的「上次看到哪」记忆：三类方案各自选中的那一份。
/// </summary>
/// <remarks>
/// 重启后如果只按「生效方案」恢复选中，用户上回选的那份就被悄悄换掉了 ——
/// 他会认为自己设置的东西被改了。选中的是哪一份属于界面状态，不该由后台决定。
///
/// 后台没有消费方：它只决定面板打开时高亮哪一行。放本地就不会因为后台不可用
/// 或版本不一致而丢掉。
///
/// 文件坏了、方案被删了，都退回「按生效方案选」—— 为一条界面记忆挡住启动不值得。
/// </remarks>
internal static class UiStateStore
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zentwik", "ui-state.json");

    /// <summary>改写落盘路径。只给自检用 —— 断言不该碰用户的真实状态。</summary>
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? DefaultFilePath;

    private sealed class StateFile
    {
        public Dictionary<string, string> Selected { get; set; } = new();
    }

    private static StateFile Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new StateFile();

            return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(FilePath))
                   ?? new StateFile();
        }
        catch (Exception)
        {
            return new StateFile();
        }
    }

    /// <summary>取某一类上次选中的方案 Id；没有记录时返回 null。</summary>
    public static string? SelectedPlanId(string kind)
    {
        StateFile state = Load();
        return state.Selected.TryGetValue(kind, out string? id) ? id : null;
    }

    /// <summary>记住某一类当前选中的方案。</summary>
    public static void SetSelectedPlanId(string kind, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        try
        {
            StateFile state = Load();
            state.Selected[kind] = id;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch (Exception)
        {
            // 记不住下次就退回按生效方案选，不该影响任何操作。
        }
    }
}
