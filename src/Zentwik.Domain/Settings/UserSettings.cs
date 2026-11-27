namespace Zentwik.Domain.Settings;

public sealed record UserSettings
{
    public string Language { get; init; } = "zh-CN";

    public string Theme { get; init; } = "dark";

    public string Accent { get; init; } = "blue";

    public double UiScale { get; init; } = 1d;

    /// <summary>监控推送间隔。值越小时看到的刷新越快，但推送本身与采样成本同步上升。</summary>
    public int MonitorIntervalMs { get; init; } = UserSettingsRules.DefaultMonitorIntervalMs;

    /// <summary>CPU 温度与封装功耗的读取间隔。读硬件比读系统计数器贵，单独可调。</summary>
    public int SensorIntervalMs { get; init; } = UserSettingsRules.DefaultSensorIntervalMs;
}

public static class UserSettingsRules
{
    public static readonly string[] Languages = ["zh-CN", "zh-TW", "en-US"];

    public static readonly string[] Themes = ["dark", "light"];

    public static readonly string[] Accents = ["blue", "violet", "teal", "amber"];

    public const double MinScale = 0.8d;

    public const double MaxScale = 1.6d;

    /// <summary>推送间隔下限 500 ms：2Hz 已超出界面可辨范围，再快只是按倍数烧采样与 IPC。</summary>
    public const int MinMonitorIntervalMs = 500;

    public const int MaxMonitorIntervalMs = 5000;

    public const int DefaultMonitorIntervalMs = 1000;

    /// <summary>传感器上限 30 秒：温度与功耗是慢变量，更慢就该改成按需读取而不是常驻订阅。</summary>
    public const int MinSensorIntervalMs = 500;

    public const int MaxSensorIntervalMs = 30000;

    public const int DefaultSensorIntervalMs = 1000;

    /// <summary>
    /// 加载状态文件时用：STJ 对缺失的数值属性填 0，不能把 0 当作"用户把间隔调成 0"。
    /// 越界值同样回落默认——新增设置项不能让旧状态文件加载失败。
    /// </summary>
    public static UserSettings Normalize(UserSettings settings) => settings with
    {
        MonitorIntervalMs = settings.MonitorIntervalMs is < MinMonitorIntervalMs or > MaxMonitorIntervalMs
            ? DefaultMonitorIntervalMs
            : settings.MonitorIntervalMs,
        SensorIntervalMs = settings.SensorIntervalMs is < MinSensorIntervalMs or > MaxSensorIntervalMs
            ? DefaultSensorIntervalMs
            : settings.SensorIntervalMs,
    };

    /// <summary>非法值整条拒绝：静默修正会让界面显示与实际生效不一致。</summary>
    public static bool TryApply(
        UserSettings current,
        string? language,
        string? theme,
        string? accent,
        double? uiScale,
        out UserSettings updated,
        out string? error,
        int? monitorIntervalMs = null,
        int? sensorIntervalMs = null)
    {
        updated = current;
        error = null;

        if (language is not null)
        {
            var match = Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"不支持的语言：{language}";
                return false;
            }

            updated = updated with { Language = match };
        }

        if (theme is not null)
        {
            var match = Themes.FirstOrDefault(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"不支持的主题：{theme}";
                return false;
            }

            updated = updated with { Theme = match };
        }

        if (accent is not null)
        {
            var match = Accents.FirstOrDefault(a => string.Equals(a, accent, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"不支持的强调色：{accent}";
                return false;
            }

            updated = updated with { Accent = match };
        }

        if (uiScale is not null)
        {
            if (uiScale is < MinScale or > MaxScale)
            {
                error = $"界面缩放超出范围 {MinScale}–{MaxScale}。";
                return false;
            }

            updated = updated with { UiScale = Math.Round(uiScale.Value, 2) };
        }

        if (monitorIntervalMs is not null)
        {
            if (monitorIntervalMs is < MinMonitorIntervalMs or > MaxMonitorIntervalMs)
            {
                error = $"监控间隔超出范围 {MinMonitorIntervalMs}–{MaxMonitorIntervalMs} ms。";
                return false;
            }

            updated = updated with { MonitorIntervalMs = monitorIntervalMs.Value };
        }

        if (sensorIntervalMs is not null)
        {
            if (sensorIntervalMs is < MinSensorIntervalMs or > MaxSensorIntervalMs)
            {
                error = $"传感器间隔超出范围 {MinSensorIntervalMs}–{MaxSensorIntervalMs} ms。";
                return false;
            }

            updated = updated with { SensorIntervalMs = sensorIntervalMs.Value };
        }

        return true;
    }
}
