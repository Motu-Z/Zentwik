using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Zentwik.Core.Ipc;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>
/// 调校页的功耗上限段（AMD）。取值来自功耗表，写入走 SMU 命令并回读校验。
/// </summary>
public partial class TuningViewModel
{
    public const int PowerLimitMinimum = 5;
    public const int PowerLimitMaximum = 300;
    public const int PowerLimitStep = 1;

    /// <summary>持续、短时、平均三项功耗上限。</summary>
    public ObservableCollection<TuningNumericField> PowerLimits { get; } = new();

    /// <summary>该平台是否有可用的功耗命令组；不支持时整段不显示。</summary>
    [ObservableProperty]
    private bool _powerLimitVisible;

    private bool _powerLimitFieldsReady;
    private int? _baselineStapm;
    private int? _baselineFast;
    private int? _baselineSlow;

    private void InitializePowerLimitFields()
    {
        if (_powerLimitFieldsReady)
            return;

        PowerLimits.Add(new TuningNumericField(
            "持续功耗", "长时间维持的上限", "W", 0, PowerLimitMinimum, PowerLimitMaximum, PowerLimitStep));
        PowerLimits.Add(new TuningNumericField(
            "短时功耗", "短时间可超过的上限", "W", 0, PowerLimitMinimum, PowerLimitMaximum, PowerLimitStep));
        PowerLimits.Add(new TuningNumericField(
            "平均功耗", "按时间窗折算的上限", "W", 0, PowerLimitMinimum, PowerLimitMaximum, PowerLimitStep));

        foreach (TuningNumericField field in PowerLimits)
        {
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TuningNumericField.Value))
                    MarkDirty();
            };
        }

        _powerLimitFieldsReady = true;
    }

    /// <summary>读功耗上限；通道不支持或读不到时整段隐藏。</summary>
    private async Task RefreshPowerLimitsAsync()
    {
        InitializePowerLimitFields();

        IpcOutcome<PowerLimitPayload> outcome = await WorkerBridge.Instance
            .QueryPowerLimitAsync(SnapshotTimeoutMs)
            .ConfigureAwait(true);

        if (outcome.TransportFailed || !outcome.Success || outcome.Value is null)
        {
            PowerLimitVisible = false;
            return;
        }

        PowerLimitPayload payload = outcome.Value;
        PowerLimitVisible = payload.Supported && payload.Available;
        if (!PowerLimitVisible)
            return;

        _suppressDirty = true;
        try
        {
            ApplyPowerLimitField(PowerLimits[0], payload.StapmWatts);
            ApplyPowerLimitField(PowerLimits[1], payload.FastWatts);
            ApplyPowerLimitField(PowerLimits[2], payload.SlowWatts);

            _baselineStapm = payload.StapmWatts;
            _baselineFast = payload.FastWatts;
            _baselineSlow = payload.SlowWatts;
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    /// <summary>把方案里的功耗值铺到界面上；方案没有值就保留读数。</summary>
    private void LoadPowerLimitValues(PlanItem? item)
    {
        if (!PowerLimitVisible || item is null || PowerLimits.Count < 3)
            return;

        _suppressDirty = true;
        try
        {
            if (item.StapmWatts.HasValue)
                ApplyPowerLimitField(PowerLimits[0], item.StapmWatts);

            if (item.FastWatts.HasValue)
                ApplyPowerLimitField(PowerLimits[1], item.FastWatts);

            if (item.SlowWatts.HasValue)
                ApplyPowerLimitField(PowerLimits[2], item.SlowWatts);
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private static void ApplyPowerLimitField(TuningNumericField field, int? watts)
    {
        if (!watts.HasValue)
            return;

        field.IsUnavailable = false;
        field.SetSilently(Math.Clamp(watts.Value, PowerLimitMinimum, PowerLimitMaximum));
    }

    /// <summary>当前界面上的三项功耗；整段不可见时不写入任何一项。</summary>
    private (int? Stapm, int? Fast, int? Slow) CurrentPowerLimits()
    {
        if (!PowerLimitVisible || PowerLimits.Count < 3)
            return (null, null, null);

        return (
            PowerLimits[0].IsUnavailable ? null : PowerLimits[0].Value,
            PowerLimits[1].IsUnavailable ? null : PowerLimits[1].Value,
            PowerLimits[2].IsUnavailable ? null : PowerLimits[2].Value);
    }

    private bool PowerLimitsDiffer(int? stapm, int? fast, int? slow)
        => Differs(stapm, _baselineStapm) || Differs(fast, _baselineFast) || Differs(slow, _baselineSlow);

    private static bool Differs(int? current, int? baseline)
        => current.HasValue && baseline.HasValue && current.Value != baseline.Value;

    private bool PlanPowerLimitsDiffer(PlanItem item)
    {
        (int? stapm, int? fast, int? slow) = CurrentPowerLimits();
        return Differs(stapm, item.StapmWatts) || Differs(fast, item.FastWatts) || Differs(slow, item.SlowWatts);
    }
}
