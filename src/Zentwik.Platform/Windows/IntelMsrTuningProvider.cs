using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>
/// Intel 电压偏移与睿频上限通道。只读写白名单 MSR，写入前后都由自己回读校验；
/// 平台不支持时给出具体原因，不伪造读数。
/// </summary>
public sealed class IntelMsrTuningProvider : IIntelTuningProvider
{
    private const string FunctionReadMsr = "ioctl_read_msr";
    private const string FunctionWriteMsr = "ioctl_write_msr";
    private const string ModuleFileName = "IntelMSR.bin";
    private const int MinOffset = -150;
    private const int MaxOffset = 100;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly ISystemInfo _info;
    private readonly Lock _gate = new();
    private PawnIoSession? _session;
    private CpuTuningStatus? _lastFailure;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private bool _probed;

    public IntelMsrTuningProvider(ISystemInfo info) => _info = info;

    public CpuTuningStatus Query()
    {
        lock (_gate)
        {
            return EnsureSession(out _);
        }
    }

    public bool TryRead(out IntelTuningState state, out string? error)
    {
        state = default!;
        lock (_gate)
        {
            if (!EnsureReady(out error))
            {
                return false;
            }

            if (!TryReadMsr(IntelMsrBits.OcMailbox, out var voltage, out error)
                || !TryReadMsr(IntelMsrBits.TurboRatioLimit, out var turbo, out error))
            {
                return false;
            }

            state = new IntelTuningState(
                IntelMsrBits.DecodeVoltageOffset(voltage),
                IntelMsrBits.DecodeTurboRatio(turbo, 1),
                IntelMsrBits.DecodeTurboRatio(turbo, 8));
            return true;
        }
    }

    public bool TryWriteVoltageOffset(int millivolts, out string? error)
    {
        if (millivolts is < MinOffset or > MaxOffset)
        {
            error = $"电压偏移 {millivolts} mV 超出允许范围 {MinOffset}..{MaxOffset}。";
            return false;
        }

        lock (_gate)
        {
            if (!EnsureReady(out error))
            {
                return false;
            }

            if (!TryReadMsr(IntelMsrBits.OcMailbox, out var current, out error))
            {
                return false;
            }

            var encoded = IntelMsrBits.EncodeVoltageOffset(current, millivolts);
            var expected = IntelMsrBits.DecodeVoltageOffset(encoded);
            if (Math.Abs(expected - millivolts) > 2)
            {
                error = $"{millivolts} mV 无法用 1/1024 V 步进表示，最接近的是 {expected} mV。";
                return false;
            }

            if (encoded != current && !WriteMsr(IntelMsrBits.OcMailbox, encoded, out error))
            {
                return false;
            }

            if (!TryReadMsr(IntelMsrBits.OcMailbox, out var actual, out error))
            {
                return false;
            }

            var readBack = IntelMsrBits.DecodeVoltageOffset(actual);
            if (readBack != expected)
            {
                error = $"电压偏移回读 {readBack} mV，与写入 {expected} mV 不一致。";
                return false;
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _probed = false;
        }
    }

    private bool EnsureReady(out string? error)
    {
        var status = EnsureSession(out error);
        return status.Availability == HardwareAvailability.Available && _session is not null;
    }

    private CpuTuningStatus EnsureSession(out string? error)
    {
        error = null;

        if (_session is { IsOpen: true } && _probed)
        {
            return Available();
        }

        if (_lastFailure is { } cached && DateTimeOffset.UtcNow - _lastFailureAt < RetryInterval)
        {
            error = cached.Detail;
            return cached;
        }

        var status = OpenAndProbe(out error);
        if (status.Availability != HardwareAvailability.Available)
        {
            _lastFailure = status;
            _lastFailureAt = DateTimeOffset.UtcNow;
        }
        else
        {
            _lastFailure = null;
        }

        return status;
    }

    private CpuTuningStatus OpenAndProbe(out string? error)
    {
        error = null;
        _session?.Dispose();
        _session = null;
        _probed = false;

        var vendor = _info.CpuVendor;
        if (!string.Equals(vendor, "GenuineIntel", StringComparison.OrdinalIgnoreCase))
        {
            error = $"处理器不是 Intel（{_info.CpuName ?? vendor ?? "未知"}）。";
            return new CpuTuningStatus(HardwareAvailability.NotSupported, error, vendor, _info.CpuName, false, false);
        }

        var modulePath = PawnIoModules.Resolve(ModuleFileName, out var searched);
        if (modulePath is null)
        {
            error = $"未找到 {ModuleFileName}，已查找：{searched}。";
            return new CpuTuningStatus(HardwareAvailability.ModuleMissing, error, vendor, _info.CpuName, false, false);
        }

        var open = PawnIoSession.Open(modulePath);
        if (open.Session is null)
        {
            error = open.Detail;
            return new CpuTuningStatus(open.Availability, open.Detail, vendor, _info.CpuName, false, false);
        }

        _session = open.Session;

        if (!TryReadMsr(IntelMsrBits.OcMailbox, out _, out error))
        {
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.ProbeFailed, error, vendor, _info.CpuName, false, false);
        }

        _probed = true;
        return Available();
    }

    private CpuTuningStatus Available() => new(
        HardwareAvailability.Available,
        $"{_info.CpuName ?? "Intel CPU"}：电压偏移与睿频上限可读，写入后回读校验。",
        _info.CpuVendor,
        _info.CpuName,
        false,
        false);

    private bool TryReadMsr(uint msr, out long value, out string? error)
    {
        value = 0;
        error = null;
        if (_session is null)
        {
            error = "MSR 会话未建立。";
            return false;
        }

        var output = new long[1];
        if (!_session.Execute(FunctionReadMsr, [msr], output, out var code))
        {
            error = DescribeFailure("读取", msr, code);
            return false;
        }

        value = output[0];
        return true;
    }

    private bool WriteMsr(uint msr, long value, out string? error)
    {
        error = null;
        if (_session is null)
        {
            error = "MSR 会话未建立。";
            return false;
        }

        if (!_session.Execute(FunctionWriteMsr, [msr, value], [], out var code))
        {
            error = DescribeFailure("写入", msr, code);
            return false;
        }

        return true;
    }

    private static string DescribeFailure(string action, uint msr, int code) => code switch
    {
        5 => $"{action} MSR 0x{msr:X} 被拒绝：需要管理员权限。",
        87 => $"{action} MSR 0x{msr:X} 被拒绝：该地址不在模块白名单内。",
        _ => $"{action} MSR 0x{msr:X} 失败，Win32 错误码 {code}。",
    };

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
        _probed = false;
    }
}
