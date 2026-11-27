using System.Text.Json;
using Xunit;
using Zentwik.Contracts.Json;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.State;
using Zentwik.Domain.Persistence;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Settings;

namespace Zentwik.Core.Tests;

public sealed class StateMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"zentwik-state-{Guid.NewGuid():N}");

    [Fact]
    public async Task Legacy_Millivolt_Field_Loads_As_Offset()
    {
        Directory.CreateDirectory(_directory);

        var document = new StateDocument
        {
            Plans =
            [
                new CpuTuningPlan
                {
                    Id = "cpu",
                    Name = "CPU",
                    Revision = 1,
                    Cores = [new CoreTuning { CoreIndex = 0, Offset = -25 }],
                },
            ],
        };

        var json = JsonSerializer.Serialize(document, ZentwikJson.Default.StateDocument);
        Assert.Contains("\"offset\":-25", json, StringComparison.Ordinal);
        json = json.Replace("\"offset\":-25", "\"voltageOffsetMillivolts\":-25", StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), json);

        using var log = new DiagnosticLog(_directory, echoToConsole: false);
        var store = new StateStore(_directory, log);
        await store.LoadAsync();

        var plan = Assert.IsType<CpuTuningPlan>(Assert.Single(store.Current.Plans));
        Assert.Equal(-25, Assert.Single(plan.Cores).Offset);
    }

    [Fact]
    public async Task Missing_Interval_Settings_Fall_Back_To_Defaults()
    {
        Directory.CreateDirectory(_directory);

        // 旧状态文件没有这两个字段：STJ 填 0，加载时要回落默认值，不能当成用户把间隔调成 0。
        const string json = """
            {
              "schemaVersion": 1,
              "settings": { "language": "zh-CN", "theme": "dark", "accent": "blue", "uiScale": 1 }
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), json);

        using var log = new DiagnosticLog(_directory, echoToConsole: false);
        var store = new StateStore(_directory, log);
        await store.LoadAsync();

        Assert.Equal(UserSettingsRules.DefaultMonitorIntervalMs, store.Current.Settings.MonitorIntervalMs);
        Assert.Equal(UserSettingsRules.DefaultSensorIntervalMs, store.Current.Settings.SensorIntervalMs);
    }

    [Fact]
    public async Task Legacy_Scene_PlanRevision_Is_Ignored()
    {
        Directory.CreateDirectory(_directory);

        const string json = """
            {
              "schemaVersion": 1,
              "scenes": [
                {
                  "id": "game",
                  "name": "Game",
                  "priority": 10,
                  "enabled": true,
                  "actions": [ { "planId": "cpu", "planRevision": 7 } ],
                  "triggers": [ { "kind": "manual" } ]
                }
              ]
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), json);

        using var log = new DiagnosticLog(_directory, echoToConsole: false);
        var store = new StateStore(_directory, log);
        await store.LoadAsync();

        var scene = Assert.Single(store.Current.Scenes);
        Assert.Equal("cpu", Assert.Single(scene.Actions).PlanId);
        Assert.Empty(store.Current.Plans);
        Assert.Empty(store.Current.Ledger);
    }

    [Fact]
    public async Task Corrupt_State_Is_Backed_Up_Before_Being_Overwritten()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "state.json");
        await File.WriteAllTextAsync(path, "{ not json");

        using var log = new DiagnosticLog(_directory, echoToConsole: false);
        var store = new StateStore(_directory, log);
        await store.LoadAsync();

        Assert.Empty(store.Current.Plans);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path + ".corrupt"));
    }

    [Fact]
    public void Offset_Round_Trips_Through_Json()
    {
        CpuTuningPlan plan = new()
        {
            Id = "cpu",
            Name = "CPU",
            Cores = [new CoreTuning { CoreIndex = 0, Offset = -20 }],
        };

        var json = JsonSerializer.Serialize<Plan>(plan, ZentwikJson.Default.Plan);
        var back = Assert.IsType<CpuTuningPlan>(JsonSerializer.Deserialize(json, ZentwikJson.Default.Plan));

        Assert.Equal(-20, Assert.Single(back.Cores).Offset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
