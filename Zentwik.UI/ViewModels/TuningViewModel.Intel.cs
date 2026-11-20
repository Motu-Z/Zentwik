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

/// <summary>Intel 调校字段：一个电压平面或一档倍频。</summary>
public sealed partial class IntelVoltageField : ObservableObject
{
    public IntelVoltageField(string label, string hint, int milliVolts, int minimum, int maximum, int step)
    {
        Label = label;
        Hint = hint;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        MilliVolts = milliVolts;
        Text = Format(milliVolts);
    }

    public string Label { get; }

    public string Hint { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public int Step { get; }

    [ObservableProperty]
    private int _milliVolts;

    [ObservableProperty]
    private string _text;

    /// <summary>读不到该平面时置为真，输入框与滑块一并停用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private bool _isUnavailable;

    public bool IsAvailable => !IsUnavailable;

    public string Unit => "mV";

    public string AutomationName => $"{Label}偏移值";

    partial void OnTextChanged(string value)
    {
        if (IsUnavailable)
            return;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            Text = Format(MilliVolts);
            return;
        }

        int clamped = Math.Clamp(parsed, Minimum, Maximum);
        MilliVolts = clamped;

        string expected = Format(clamped);
        if (value != expected)
            Text = expected;
    }

    partial void OnMilliVoltsChanged(int value)
    {
        string expected = Format(value);
        if (Text != expected)
            Text = expected;
    }

    /// <summary>外部写入（回读）时直接换值，省掉一次往返纠偏。</summary>
    public void SetSilently(int milliVolts)
    {
        MilliVolts = milliVolts;
        Text = Format(milliVolts);
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
    public ObservableCollection<IntelVoltageField> IntelVoltages { get; } = new();

    /// <summary>倍频上限八档。</summary>
    public ObservableCollection<IntelRatioItem> IntelRatios { get; } = new();

    private bool _intelFieldsReady;

    private void EnsureIntelFields()
    {
        if (_intelFieldsReady)
            return;

        IntelVoltages.Add(new IntelVoltageField("核心电压", "处理器核心供电", 0, IntelVoltageMinimum, IntelVoltageMaximum, IntelVoltageStep));
        IntelVoltages.Add(new IntelVoltageField("核显电压", "集成显卡供电", 0, IntelVoltageMinimum, IntelVoltageMaximum, IntelVoltageStep));
        IntelVoltages.Add(new IntelVoltageField("缓存电压", "末级缓存供电", 0, IntelVoltageMinimum, IntelVoltageMaximum, IntelVoltageStep));

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
            ApplyIntelTuningPayload request = new()
            {
                CoreMilliVolts = IntelVoltages[0].MilliVolts,
                GpuMilliVolts = IntelVoltages[1].MilliVolts,
                CacheMilliVolts = IntelVoltages[2].MilliVolts,
                Ratios = IntelRatios.Select(item => item.Ratio).ToArray()
            };

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
            ApplyVoltageField(IntelVoltages[0], payload.CoreMilliVolts);
            ApplyVoltageField(IntelVoltages[1], payload.GpuMilliVolts);
            ApplyVoltageField(IntelVoltages[2], payload.CacheMilliVolts);

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

    private static void ApplyVoltageField(IntelVoltageField field, int? milliVolts)
    {
        if (milliVolts.HasValue)
        {
            field.IsUnavailable = false;
            field.SetSilently(Math.Clamp(
                milliVolts.Value,
                TuningViewModel.IntelVoltageMinimum,
                TuningViewModel.IntelVoltageMaximum));
        }
        else
        {
            field.IsUnavailable = true;
        }
    }
}
