using System.Text.Json;
using Xunit;
using Zentwik.Contracts.Json;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.State;
using Zentwik.Domain.Persistence;
using Zentwik.Domain.Plans;

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
