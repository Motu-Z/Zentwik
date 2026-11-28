using Xunit;
using Zentwik.Domain.Scenes;
using Zentwik.Domain.Settings;
using Zentwik.Localization;

namespace Zentwik.Domain.Tests;

public sealed class SceneArbitrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HighestPriority_Wins()
    {
        var winner = SceneArbitration.Winner([
            new SceneCandidate("scene.idle", 1, T0),
            new SceneCandidate("scene.game", 50, T0.AddMinutes(5)),
        ]);

        Assert.Equal("scene.game", winner!.SceneId);
    }

    [Fact]
    public void SamePriority_Keeps_Earlier_Activation_To_Avoid_Flapping()
    {
        var decisions = SceneArbitration.Resolve([
            new SceneCandidate("scene.b", 10, T0.AddSeconds(30)),
            new SceneCandidate("scene.a", 10, T0),
        ]);

        Assert.True(decisions[0].Selected);
        Assert.Equal("scene.a", decisions[0].SceneId);
        Assert.Equal(ArbitrationReason.LaterActivation, decisions[1].Reason);
    }

    [Fact]
    public void LowerPriority_Is_Reported_As_LowerPriority()
    {
        var decisions = SceneArbitration.Resolve([
            new SceneCandidate("scene.game", 30, T0),
            new SceneCandidate("scene.idle", 2, T0),
        ]);

        Assert.Equal(ArbitrationReason.LowerPriority, decisions[1].Reason);
        Assert.False(decisions[1].Selected);
    }

    [Fact]
    public void Tie_OnPriority_And_Time_Falls_Back_To_Ordinal_Id()
    {
        var winner = SceneArbitration.Winner([
            new SceneCandidate("scene.z", 10, T0),
            new SceneCandidate("scene.a", 10, T0),
        ]);

        Assert.Equal("scene.a", winner!.SceneId);
    }

    [Fact]
    public void Empty_Candidates_Return_Null()
    {
        Assert.Null(SceneArbitration.Winner([]));
    }
}

public sealed class UserSettingsRulesTests
{
    [Fact]
    public void Valid_Patch_Is_Applied()
    {
        var ok = UserSettingsRules.TryApply(new UserSettings(), "en-US", "light", "teal", 1.25, out var updated, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("en-US", updated.Language);
        Assert.Equal("light", updated.Theme);
        Assert.Equal("teal", updated.Accent);
        Assert.Equal(1.25, updated.UiScale);
    }

    [Fact]
    public void Partial_Patch_Keeps_Other_Fields()
    {
        var current = new UserSettings { Language = "zh-TW", Theme = "light", Accent = "amber", UiScale = 1.1 };
        var ok = UserSettingsRules.TryApply(current, "zh-CN", null, null, null, out var updated, out _);

        Assert.True(ok);
        Assert.Equal("zh-CN", updated.Language);
        Assert.Equal("light", updated.Theme);
        Assert.Equal(1.1, updated.UiScale);
    }

    [Theory]
    [InlineData("fr-FR", null, null, null)]
    [InlineData(null, "solarized", null, null)]
    [InlineData(null, null, "pink", null)]
    [InlineData(null, null, null, 3.0)]
    public void Invalid_Values_Are_Rejected_Wholesale(string? language, string? theme, string? accent, double? scale)
    {
        var current = new UserSettings();
        var ok = UserSettingsRules.TryApply(current, language, theme, accent, scale, out var updated, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Same(current, updated);
    }

    [Fact]
    public void Default_Intervals_Are_Both_One_Second()
    {
        var settings = new UserSettings();

        Assert.Equal(1000, settings.MonitorIntervalMs);
        Assert.Equal(1000, settings.SensorIntervalMs);
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(5000, 30000)]
    public void Interval_Edges_Are_Accepted(int monitor, int sensor)
    {
        var ok = UserSettingsRules.TryApply(
            new UserSettings(), null, null, null, null, out var updated, out var error,
            monitorIntervalMs: monitor, sensorIntervalMs: sensor);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(monitor, updated.MonitorIntervalMs);
        Assert.Equal(sensor, updated.SensorIntervalMs);
    }

    [Fact]
    public void Zero_Sensor_Interval_Is_Rejected()
    {
        var ok = UserSettingsRules.TryApply(
            new UserSettings(), null, null, null, null, out var updated, out var error,
            sensorIntervalMs: 0);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(1000, updated.SensorIntervalMs);
    }

    [Fact]
    public void Normalize_Falls_Back_For_Missing_Values()
    {
        // STJ 对缺失的数值字段填 0；加载时不能把它当成用户的选择。
        var normalized = UserSettingsRules.Normalize(new UserSettings { MonitorIntervalMs = 0, SensorIntervalMs = 0 });

        Assert.Equal(UserSettingsRules.DefaultMonitorIntervalMs, normalized.MonitorIntervalMs);
        Assert.Equal(UserSettingsRules.DefaultSensorIntervalMs, normalized.SensorIntervalMs);
    }

    [Fact]
    public void Normalize_Keeps_Valid_Values()
    {
        var normalized = UserSettingsRules.Normalize(new UserSettings { MonitorIntervalMs = 2000, SensorIntervalMs = 5000 });

        Assert.Equal(2000, normalized.MonitorIntervalMs);
        Assert.Equal(5000, normalized.SensorIntervalMs);
    }

    [Theory]
    [InlineData(499, null)]
    [InlineData(5001, null)]
    [InlineData(null, 100)]
    [InlineData(null, 30001)]
    public void Out_Of_Range_Intervals_Are_Rejected(int? monitor, int? sensor)
    {
        var current = new UserSettings();
        var ok = UserSettingsRules.TryApply(
            current, null, null, null, null, out var updated, out var error,
            monitorIntervalMs: monitor, sensorIntervalMs: sensor);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Same(current, updated);
    }
}

public sealed class LocalizationCatalogTests
{
    [Fact]
    public void Flat_Culture_Names_Normalize_To_Supported_Set()
    {
        Assert.Equal("zh-CN", LocalizationCatalog.Normalize("zh"));
        Assert.Equal("zh-CN", LocalizationCatalog.Normalize("zh-HK"));
        Assert.Equal("en-US", LocalizationCatalog.Normalize("en-GB"));
        Assert.Equal("en-US", LocalizationCatalog.Normalize("de-DE"));
        Assert.Equal("zh-TW", LocalizationCatalog.Normalize("zh-TW"));
    }

    [Fact]
    public void Embedded_Resources_Are_Present_In_Main_Assembly_For_All_Supported_Cultures()
    {
        var assembly = typeof(LocalizationCatalog).Assembly;
        var names = assembly.GetManifestResourceNames();

        foreach (var culture in new[] { "en-US", "zh-CN", "zh-TW" })
        {
            var expected = $"Zentwik.Localization.Resources.strings.{culture}.json";
            Assert.Contains(expected, names);

            using var stream = assembly.GetManifestResourceStream(expected);
            Assert.NotNull(stream);
            Assert.True(stream.Length > 0);
        }
    }

    [Fact]
    public void Common_Keys_Return_Real_Translations_Across_All_Cultures()
    {
        var catalog = LocalizationCatalog.CreateDefault();
        Assert.NotEmpty(catalog.Keys);

        string[] commonKeys =
        [
            "nav.monitor",
            "nav.plans",
            "nav.scenes",
            "nav.tuning",
            "common.refresh",
            "common.dismiss",
            "common.error_prefix",
            "plans.title",
            "scenes.title",
            "settings.language",
        ];

        foreach (var key in commonKeys)
        {
            Assert.Contains(key, catalog.Keys);
            foreach (var culture in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                var translated = catalog.Get(key, culture);
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.NotEqual(key, translated);
            }
        }

        Assert.Equal("Refresh", catalog.Get("common.refresh", "en-US"));
        Assert.Equal("刷新", catalog.Get("common.refresh", "zh-CN"));
        Assert.Equal("重新整理", catalog.Get("common.refresh", "zh-TW"));
        Assert.Equal("Dismiss", catalog.Get("common.dismiss", "en-US"));
    }

    [Fact]
    public void Unknown_Key_And_Missing_Culture_Follow_Fallback_Chain()
    {
        var catalog = LocalizationCatalog.CreateDefault();
        Assert.Equal("does.not.exist", catalog.Get("does.not.exist"));
        Assert.Equal("does.not.exist", catalog.Get("does.not.exist", "fr-FR"));

        catalog.Merge("zh-CN", new Dictionary<string, string> { ["fallback.zh"] = "简体兜底" });
        catalog.Merge("en-US", new Dictionary<string, string> { ["fallback.en"] = "English fallback" });

        Assert.Equal("简体兜底", catalog.Get("fallback.zh", "zh-TW"));
        Assert.Equal("English fallback", catalog.Get("fallback.en", "zh-TW"));
        Assert.Equal("Refresh", catalog.Get("common.refresh", "fr-FR"));
    }

    [Fact]
    public void All_Supported_Cultures_Translate_Every_Key()
    {
        var catalog = LocalizationCatalog.CreateDefault();
        Assert.NotEmpty(catalog.Keys);

        foreach (var key in catalog.Keys)
        {
            foreach (var culture in LocalizationCatalog.SupportedCultures)
            {
                Assert.NotEqual(key, catalog.Get(key, culture));
            }
        }
    }

    [Fact]
    public void Culture_Change_Raises_Event_Once_Per_Distinct_Value()
    {
        var catalog = LocalizationCatalog.CreateDefault();
        var raised = 0;
        catalog.CultureChanged += () => raised++;

        // 初始语言随系统区域变化，测试不能假定它是 zh-CN。
        var start = catalog.CurrentCulture;
        var other = start == "en-US" ? "zh-TW" : "en-US";

        catalog.CurrentCulture = other;
        catalog.CurrentCulture = other;
        catalog.CurrentCulture = start;

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Merge_Overrides_Single_Key_Only()
    {
        var catalog = LocalizationCatalog.CreateDefault();
        catalog.Merge("en-US", new Dictionary<string, string> { ["nav.monitor"] = "Telemetry" });

        Assert.Equal("Telemetry", catalog.Get("nav.monitor", "en-US"));
        Assert.NotEqual("Telemetry", catalog.Get("nav.plans", "en-US"));
    }
}
