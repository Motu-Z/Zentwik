using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

/// <summary>调校页的数值字段：电压平面、功耗上限或时长。</summary>
public sealed partial class TuningNumericField : ObservableObject
{
    public TuningNumericField(
        string label,
        string hint,
        string unit,
        int value,
        int minimum,
        int maximum,
        int step)
    {
        Label = label;
        Hint = hint;
        Unit = unit;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Value = value;
        Text = Format(value);
    }

    public string Label { get; }

    public string Hint { get; }
    public string Unit { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public int Step { get; }

    [ObservableProperty]
    private int _value;

    [ObservableProperty]
    private string _text;

    /// <summary>读不到该项时置为真，输入框与滑块一并停用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private bool _isUnavailable;

    public bool IsAvailable => !IsUnavailable;

    public string AutomationName => Label;

    partial void OnTextChanged(string value)
    {
        if (IsUnavailable)
            return;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            Text = Format(Value);
            return;
        }

        int clamped = Math.Clamp(parsed, Minimum, Maximum);
        Value = clamped;

        string expected = Format(clamped);
        if (value != expected)
            Text = expected;
    }

    partial void OnValueChanged(int value)
    {
        string expected = Format(value);
        if (Text != expected)
            Text = expected;
    }

    /// <summary>外部写入（回读）时直接换值，省掉一次往返纠偏。</summary>
    public void SetSilently(int value)
    {
        Value = value;
        Text = Format(value);
    }

    private static string Format(int value)
        => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Intel 睿频档位：<see cref="Slot"/> 0 对应单核激活。</summary>
public sealed partial class IntelRatioItem : ObservableObject
{
    public IntelRatioItem(int slot, int ratio)
    {
        Slot = slot;
        Ratio = ratio;
        Text = ratio.ToString(CultureInfo.InvariantCulture);
    }

    public int Slot { get; }

    public string DisplayName => $"{Slot + 1} 核";

    public string AutomationName => $"{Slot + 1} 核倍频上限";

    [ObservableProperty]
    private int _ratio;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private bool _isUnavailable;

    public bool IsAvailable => !IsUnavailable;

    partial void OnTextChanged(string value)
    {
        if (IsUnavailable)
            return;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            Text = Ratio.ToString(CultureInfo.InvariantCulture);
            return;
        }

        int clamped = Math.Clamp(parsed, TuningViewModel.IntelRatioMinimum, TuningViewModel.IntelRatioMaximum);
        Ratio = clamped;

        string expected = clamped.ToString(CultureInfo.InvariantCulture);
        if (value != expected)
            Text = expected;
    }

    partial void OnRatioChanged(int value)
    {
        string expected = value.ToString(CultureInfo.InvariantCulture);
        if (Text != expected)
            Text = expected;
    }

    public void SetSilently(int ratio)
    {
        Ratio = ratio;
        Text = ratio.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>调校页的 Intel 分支：电压三平面 + 睿频八档。</summary>
public partial class TuningViewModel
{
    public const int IntelVoltageMinimum = -200;
    public const int IntelVoltageMaximum = 50;
    public const int IntelVoltageStep = 5;
    public const int IntelRatioMinimum = 8;
    public const int IntelRatioMaximum = 60;

    public const int IntelPowerMinimum = 5;
    public const int IntelPowerMaximum = 300;
    public const int IntelPowerStep = 1;
    public const int IntelTauMinimum = 0;
    public const int IntelTauMaximum = 128;
    public const int IntelTauStep = 1;

    /// <summary>厂商由 Worker 下发，未知时按 AMD 处理。</summary>
    public bool IsIntel => WorkerStatus.Instance.IsIntel;

    private void OnWorkerStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkerStatus.IsIntel))
            return;

        OnPropertyChanged(nameof(IsIntel));
        OnPropertyChanged(nameof(CanSave));

        if (IsIntel)
            EnsureIntelFields();
    }

    [ObservableProperty]
    private bool _intelAvailable;

    /// <summary>Intel 通道就绪状态变化时整页遮罩跟着切。</summary>
    partial void OnIntelAvailableChanged(bool value)
    {
        if (IsIntel)
            SmuAvailable = value;

        ApplyIntelCommand.NotifyCanExecuteChanged();
        RestoreIntelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>电压平面：核心、核显、缓存。</summary>
    public ObservableCollection<TuningNumericField> IntelVoltages { get; } = new();

    /// <summary>功耗限制：持续功耗、短时功耗、短时功耗时长。</summary>
    public ObservableCollection<TuningNumericField> IntelPowers { get; } = new();

    /// <summary>倍频上限八档。</summary>
    public ObservableCollection<IntelRatioItem> IntelRatios { get; } = new();

    private bool _intelFieldsReady;

    private void EnsureIntelFields()
    {
        if (_intelFieldsReady)
            return;

        IntelVoltages.Add(new TuningNumericField("核心电压", "处理器核心供电", "mV", 0, IntelVoltageMinimum, IntelVoltageMaximum, IntelVoltageStep));
        IntelVoltages.Add(new TuningNumericField("核显电压", "集成显卡供电", "mV", 0, IntelVoltageMinimum, IntelVoltageMaximum, IntelVoltageStep));
        IntelVoltages.Add(new TuningNumericField("缓存电压", "末级缓存供电", "mV", 0, IntelVoltageMinimum, IntelVoltageMaximum, IntelVoltageStep));

        IntelPowers.Add(new TuningNumericField("持续功耗", "长时间维持的上限", "W", 0, IntelPowerMinimum, IntelPowerMaximum, IntelPowerStep));
        IntelPowers.Add(new TuningNumericField("短时功耗", "短时间可超过的上限", "W", 0, IntelPowerMinimum, IntelPowerMaximum, IntelPowerStep));
        IntelPowers.Add(new TuningNumericField("短时功耗时长", "短时功耗维持的秒数，0 表示不限", "秒", 0, IntelTauMinimum, IntelTauMaximum, IntelTauStep));

        for (int i = 0; i < IntelTuningCodec.RatioSlots; i++)
            IntelRatios.Add(new IntelRatioItem(i, IntelRatioMinimum));

        _intelFieldsReady = true;
    }

    /// <summary>Intel 下的「应用」：直接写 MSR，不经过方案。</summary>
    public bool CanApplyIntel => !IsBusy && IntelAvailable;

    [RelayCommand(CanExecute = nameof(CanApplyIntel))]
    private async Task ApplyIntelAsync()
    {
        if (!_intelFieldsReady)
            return;

        IsBusy = true;

        try
        {
            // 读不到的项不发送：发出去会把「未知」写成 0。
            ApplyIntelTuningPayload request = new();

            if (IntelVoltages[0].IsAvailable)
                request.CoreMilliVolts = IntelVoltages[0].Value;
            if (IntelVoltages[1].IsAvailable)
                request.GpuMilliVolts = IntelVoltages[1].Value;
            if (IntelVoltages[2].IsAvailable)
                request.CacheMilliVolts = IntelVoltages[2].Value;

            if (IntelRatios.All(item => item.IsAvailable))
                request.Ratios = IntelRatios.Select(item => item.Ratio).ToArray();

            if (IntelPowers[0].IsAvailable && IntelPowers[1].IsAvailable)
            {
                request.PowerLimit1Watts = IntelPowers[0].Value;
                request.PowerLimit2Watts = IntelPowers[1].Value;
                request.TauSeconds = IntelPowers[2].IsAvailable ? IntelPowers[2].Value : 0;
            }

            IpcOutcome<IntelTuningPayload> outcome = await WorkerBridge.Instance
                .ApplyIntelTuningAsync(request, ApplyTimeoutMs)
                .ConfigureAwait(true);

            await HandleIntelWriteAsync(outcome, "写入 Intel 调校").ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyIntel))]
    private async Task RestoreIntelAsync()
    {
        IsBusy = true;

        try
        {
            IpcOutcome<IntelTuningPayload> outcome = await WorkerBridge.Instance
                .ApplyIntelTuningAsync(
                    new ApplyIntelTuningPayload { Restore = true },
                    ApplyTimeoutMs)
                .ConfigureAwait(true);

            await HandleIntelWriteAsync(outcome, "恢复 Intel 原始值").ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandleIntelWriteAsync(IpcOutcome<IntelTuningPayload> outcome, string action)
    {
        if (outcome.TransportFailed)
        {
            NotifyIf(true, $"{action}失败：后台服务未连接");
            return;
        }

        if (!outcome.Success || outcome.Value is null)
        {
            NotifyIf(true, $"{action}失败：{outcome.Message}");
            return;
        }

        ApplyIntelPayload(outcome.Value);
    }

    /// <summary>与 Worker 对数：Intel 平台走 MSR 通道，不用 SMU 快照。</summary>
    private async Task RefreshIntelAsync(bool notifyOnFailure)
    {
        EnsureIntelFields();

        IpcOutcome<IntelTuningPayload> outcome = await WorkerBridge.Instance
            .QueryIntelTuningAsync(SnapshotTimeoutMs)
            .ConfigureAwait(true);

        if (outcome.TransportFailed)
        {
            MarkWorkerUnreachable("无法读取 Intel 调校值。后台服务未响应");
            NotifyIf(notifyOnFailure, "后台服务未连接，未能读取硬件当前值");
            return;
        }

        if (!outcome.Success || outcome.Value is null || !outcome.Value.Available)
        {
            IntelAvailable = false;
            ClearBlocking();
            BlockingTitle = "调校通道不可用";
            BlockingDetail = outcome.Value?.Error ?? outcome.Message ?? "Intel MSR 模块未就绪";
            NotifyIf(notifyOnFailure, "无法读取 Intel 调校值");
            return;
        }

        IntelAvailable = true;
        WorkerConnected = true;
        ApplyIntelPayload(outcome.Value);
    }

    private void ApplyIntelPayload(IntelTuningPayload payload)
    {
        EnsureIntelFields();

        _suppressDirty = true;
        try
        {
            ApplyNumericField(IntelVoltages[0], payload.CoreMilliVolts, IntelVoltageMinimum, IntelVoltageMaximum);
            ApplyNumericField(IntelVoltages[1], payload.GpuMilliVolts, IntelVoltageMinimum, IntelVoltageMaximum);
            ApplyNumericField(IntelVoltages[2], payload.CacheMilliVolts, IntelVoltageMinimum, IntelVoltageMaximum);

            ApplyNumericField(IntelPowers[0], payload.PowerLimit1Watts, IntelPowerMinimum, IntelPowerMaximum);
            ApplyNumericField(IntelPowers[1], payload.PowerLimit2Watts, IntelPowerMinimum, IntelPowerMaximum);
            ApplyNumericField(IntelPowers[2], payload.TauSeconds, IntelTauMinimum, IntelTauMaximum);

            int[] ratios = payload.Ratios ?? Array.Empty<int>();
            for (int i = 0; i < IntelRatios.Count; i++)
            {
                IntelRatioItem item = IntelRatios[i];
                if (i < ratios.Length && ratios[i] > 0)
                {
                    item.IsUnavailable = false;
                    item.SetSilently(ratios[i]);
                }
                else
                {
                    item.IsUnavailable = true;
                }
            }
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private static void ApplyNumericField(TuningNumericField field, int? value, int minimum, int maximum)
    {
        if (value.HasValue)
        {
            field.IsUnavailable = false;
            field.SetSilently(Math.Clamp(value.Value, minimum, maximum));
        }
        else
        {
            field.IsUnavailable = true;
        }
    }
}
