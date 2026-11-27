using Xunit;
using Zentwik.Contracts.Dto;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;
using Zentwik.Localization;

namespace Zentwik.Desktop.Tests;

public sealed class MainViewModelTests
{
    private static MainViewModel CreateViewModel(string culture = "zh-CN")
    {
        var catalog = LocalizationCatalog.CreateDefault();
        catalog.CurrentCulture = culture;
        var themes = new ThemeService();
        return new MainViewModel(catalog, themes);
    }

    [Fact]
    public async Task ApplySample_Maps_System_And_Sensor_Telemetry_With_Server_Timestamp()
    {
        var vm = CreateViewModel();
        try
        {
            var serverTime = new DateTimeOffset(2026, 11, 26, 20, 30, 45, TimeSpan.Zero);
            var sample = new TelemetryDto(
                Timestamp: serverTime,
                CpuTotalPercent: 42.4,
                MemoryTotalBytes: 1000,
                MemoryAvailableBytes: 250,
                ProcessCount: 185,
                ProcessorCount: 16,
                TemperatureCelsius: 63.5,
                PackagePowerWatts: 88.2,
                SensorDetail: "ok",
                CoreClocks:
                [
                    new TelemetryCoreClockDto(1, 0, 5125.0),
                    new TelemetryCoreClockDto(0, 2, 5350.0),
                    new TelemetryCoreClockDto(0, 0, 5200.0),
                ]);

            vm.ApplySample(sample);

            Assert.Equal("42%", vm.CpuText);
            Assert.Equal("75%", vm.MemoryText);
            Assert.Equal("185", vm.ProcessText);
            Assert.Equal("63.5 °C", vm.TemperatureText);
            Assert.Equal("88.2 W", vm.PackagePowerText);
            Assert.Equal("5.35 GHz", vm.PeakClockText);
            Assert.Equal("ok", vm.SensorDetailText);
            Assert.Equal($"服务端采样 {serverTime.ToLocalTime():HH:mm:ss}", vm.LastSampleTimeText);

            Assert.Equal(3, vm.CoreClocks.Count);
            Assert.Equal("CCD0 · C0", vm.CoreClocks[0].Label);
            Assert.Equal("CCD0 · C2", vm.CoreClocks[1].Label);
            Assert.Equal("CCD1 · C0", vm.CoreClocks[2].Label);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildPlanFromForm_And_FormatPlanSpecForEdit_RoundTrip_All_Plan_Kinds()
    {
        var vm = CreateViewModel();
        try
        {
            // 1. cpuTuning
            vm.NewPlanKind = "cpuTuning";
            vm.NewPlanId = "co-daily";
            vm.NewPlanName = "Daily CO";
            vm.NewPlanSpec = "default=-15, ccd0=-20, 0=-10, 1=auto, fmax=5250, intel=-50";
            var cpuPlan = Assert.IsType<CpuTuningPlan>(vm.BuildPlanFromForm());
            Assert.Equal(-15, cpuPlan.DefaultOffset);
            Assert.Equal(-20, cpuPlan.CcdOffsets![0]);
            Assert.Equal(5250, cpuPlan.FMaxLimitMhz);
            Assert.Equal(-50, cpuPlan.IntelVoltageOffsetMillivolts);
            Assert.Equal("default=-15, ccd0=-20, 0=-10, 1=auto, fmax=5250, intel=-50", MainViewModel.FormatPlanSpecForEdit(cpuPlan));

            // 2. power
            var guid = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            vm.NewPlanKind = "power";
            vm.NewPlanId = "high-perf";
            vm.NewPlanName = "High Performance";
            vm.NewPlanSpec = guid.ToString("D");
            var powerPlan = Assert.IsType<PowerPlan>(vm.BuildPlanFromForm());
            Assert.Equal(guid, powerPlan.BaseSchemeGuid);
            Assert.Equal(guid.ToString("D"), MainViewModel.FormatPlanSpecForEdit(powerPlan));

            // 3. scheduling
            vm.NewPlanKind = "scheduling";
            vm.NewPlanId = "game-sched";
            vm.NewPlanName = "Game Sched";
            vm.NewPlanSpec = "game.exe=0xFF:High:0-7";
            var schedPlan = Assert.IsType<SchedulingPlan>(vm.BuildPlanFromForm());
            Assert.Single(schedPlan.Rules);
            Assert.Equal("game.exe=0xFF:High:0-7", MainViewModel.FormatPlanSpecForEdit(schedPlan));
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task Clamping_And_Dynamic_Hints_Work_As_Expected()
    {
        var vm = CreateViewModel("zh-CN");
        try
        {
            vm.SelectedMonitorIntervalMs = 100;
            Assert.Equal(500, vm.SelectedMonitorIntervalMs);
            vm.SelectedMonitorIntervalMs = 99999;
            Assert.Equal(5000, vm.SelectedMonitorIntervalMs);

            vm.SelectedSensorIntervalMs = 100;
            Assert.Equal(500, vm.SelectedSensorIntervalMs);
            vm.SelectedSensorIntervalMs = 99999;
            Assert.Equal(30000, vm.SelectedSensorIntervalMs);

            vm.UiScale = 0.2;
            Assert.Equal(0.8, vm.UiScale);
            vm.UiScale = 3.0;
            Assert.Equal(1.6, vm.UiScale);

            vm.NewPlanKind = "power";
            Assert.Contains("GUID", vm.PlanSpecHint);

            vm.NewSceneTriggerKind = SceneTriggerKinds.ProcessExited;
            Assert.Contains("退出", vm.TriggerArgHint);

            vm.SelectedLanguage = "en-US";
            Assert.Contains("exit", vm.TriggerArgHint);
            Assert.Equal("Comfortable", vm.DensityOptions[0].Label);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task UsePowerSchemeForPlanCommand_Prefills_Power_Plan_Form()
    {
        var vm = CreateViewModel();
        try
        {
            var guid = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
            var row = new PowerSchemeRow(guid, "Balanced", Active: true, BuiltIn: true, Visible: true);
            vm.PowerSchemes.Add(row);
            vm.SelectedPowerScheme = row;

            Assert.True(vm.UsePowerSchemeForPlanCommand.CanExecute(null));
            vm.UsePowerSchemeForPlanCommand.Execute(null);

            Assert.Equal("power", vm.NewPlanKind);
            Assert.Equal("Balanced", vm.NewPlanName);
            Assert.Equal(guid.ToString("D"), vm.NewPlanSpec);
            Assert.True(vm.IsCreatePlanOpen);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApplySample_Maps_ProcessorUtilization_And_Clears_On_Null()
    {
        var vm = CreateViewModel();
        try
        {
            var t0 = new DateTimeOffset(2026, 11, 26, 21, 0, 0, TimeSpan.Zero);
            vm.ApplySample(new TelemetryDto(t0, 10d, 1000, 500, 100, 2, ProcessorUtilization: null));
            Assert.Empty(vm.ProcessorLoads);

            vm.ApplySample(new TelemetryDto(t0.AddSeconds(1), 50d, 1000, 500, 100, 2, ProcessorUtilization: [12.3, 87.6]));
            Assert.Equal(2, vm.ProcessorLoads.Count);
            Assert.Equal("CPU 0", vm.ProcessorLoads[0].Label);
            Assert.Equal("12%", vm.ProcessorLoads[0].PercentText);
            Assert.Equal("CPU 1", vm.ProcessorLoads[1].Label);
            Assert.Equal("88%", vm.ProcessorLoads[1].PercentText);

            vm.ApplySample(new TelemetryDto(t0.AddSeconds(2), 20d, 1000, 500, 100, 2, ProcessorUtilization: null));
            Assert.Empty(vm.ProcessorLoads);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadHistorySamples_Populates_History_And_Applies_Latest_Sample()
    {
        var vm = CreateViewModel();
        try
        {
            double[]? latestHistory = null;
            vm.HistoryChanged += points => latestHistory = points;

            var t0 = new DateTimeOffset(2026, 11, 26, 21, 5, 0, TimeSpan.Zero);
            vm.LoadHistorySamples(
            [
                new TelemetryDto(t0, 10d, 1000, 500, 100, 4),
                new TelemetryDto(t0.AddSeconds(1), 25d, 1000, 500, 100, 4),
                new TelemetryDto(t0.AddSeconds(2), 50d, 1000, 500, 100, 4, ProcessorUtilization: [40d, 60d, 50d, 50d]),
            ]);

            Assert.NotNull(latestHistory);
            Assert.Equal([10d, 25d, 50d], latestHistory);
            Assert.Equal("50%", vm.CpuText);
            Assert.Equal(4, vm.ProcessorLoads.Count);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public void LocalizationCatalog_Supports_All_Cultures_Without_Empty_Values()
    {
        var catalog = LocalizationCatalog.CreateDefault();
        var keys = catalog.Keys;
        Assert.NotEmpty(keys);

        foreach (var culture in LocalizationCatalog.SupportedCultures)
        {
            catalog.CurrentCulture = culture;
            foreach (var key in keys)
            {
                var value = catalog[key];
                Assert.False(string.IsNullOrWhiteSpace(value), $"Missing or empty key '{key}' in '{culture}'.");
                Assert.NotEqual(key, value);
            }
        }
    }
}
