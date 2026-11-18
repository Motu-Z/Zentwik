using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentwik.UI.Services;

/// <summary>
/// 入口开关：桌面右键菜单、托盘方案切换。
/// 纯界面行为，不进 ui-settings.json 的同步链路 —— 后台服务不需要知道这些。
/// </summary>
public static class UiEntrySettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zentwik",
        "ui-entry.json");

    private static bool _loaded;
    private static bool _desktopMenu;
    private static bool _trayPlanMenu = true;

    /// <summary>桌面空白右键菜单。默认不开：没人希望装个软件桌面就多出一项。</summary>
    public static bool DesktopMenu
    {
        get
        {
            EnsureLoaded();
            return _desktopMenu;
        }

        set
        {
            EnsureLoaded();

            if (_desktopMenu == value)
            {
                return;
            }

            _desktopMenu = value;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>托盘右键菜单里是否列出三类方案。</summary>
    public static bool TrayPlanMenu
    {
        get
        {
            EnsureLoaded();
            return _trayPlanMenu;
        }

        set
        {
            EnsureLoaded();

            if (_trayPlanMenu == value)
            {
                return;
            }

            _trayPlanMenu = value;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>任一开关变了就通知：托盘菜单要重建，行为页的开关要跟上。</summary>
    public static event Action? Changed;

    /// <summary>恢复默认：桌面右键关、托盘方案开。给「清除全部数据」用。</summary>
    public static void Reset()
    {
        EnsureLoaded();

        _desktopMenu = false;
        _trayPlanMenu = true;

        Save();
        Changed?.Invoke();
    }

    private sealed class Snapshot
    {
        [JsonPropertyName("desktopMenu")]
        public bool DesktopMenu { get; set; }

        [JsonPropertyName("trayPlanMenu")]
        public bool TrayPlanMenu { get; set; } = true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            Snapshot? saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath));

            if (saved is not null)
            {
                _desktopMenu = saved.DesktopMenu;
                _trayPlanMenu = saved.TrayPlanMenu;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 文件坏了就用默认值，不能挡住启动。
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(new Snapshot { DesktopMenu = _desktopMenu, TrayPlanMenu = _trayPlanMenu }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去只影响下次启动的默认值，不影响本次生效。
        }
    }
}
