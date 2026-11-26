using System.Diagnostics;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>
/// AMD 封装温度与封装功耗。走 AMDFamily17 模块的 <c>ioctl_read_smn</c> / <c>ioctl_read_msr</c>：
/// 温度读 <c>THM_TCON_CUR_TMP</c>，功耗来自 RAPL 封装能量计数器的相邻两帧差分。
/// </summary>
/// <remarks>
/// 与 SMU 邮箱是两条独立通道，各自持有 PawnIO 句柄，共用 <c>Global\Access_PCI</c> 互斥（只有 SMN 读需要）。
/// 不用 PM 表：每次读它要先触发一次传输并等 20–30 ms，且字段口径未经交叉验证。
/// </remarks>
public sealed class AmdSensorSource : ICpuSensorSource, IDisposable
{
    private const uint MsrPowerUnit = 0xC0010299;
    private const uint MsrPackageEnergyStat = 0xC001029B;
    private const uint SmnThmTconCurTmp = 0x00059800;

    private const string FunctionReadSmn = "ioctl_read_smn";
    private const string FunctionReadMsr = "ioctl_read_msr";
    private const string ModuleFileName = "AMDFamily17.bin";
    private const string PciMutexName = @"Global\Access_PCI";

    private static readonly TimeSpan PciMutexTimeout = TimeSpan.FromMilliseconds(500);

    // 会话/模块级失败重试间隔：订阅每拍都重打开一次设备没有意义，但恢复后要能自己接上。
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(10);

    // 短于此窗口的差分量化噪声会盖过信号；沿用上一次结果，不给虚假值。
    private static readonly TimeSpan MinimumPowerWindow = TimeSpan.FromMilliseconds(100);

    private readonly ISystemInfo _info;
    private readonly Lock _gate = new();
    private PawnIoSession? _session;
    private Mutex? _pciMutex;
    private bool _pciHeld;
    private bool _probed;
    private int _energyExponent = -1;
    private bool _hasEnergyBaseline;
    private uint _lastEnergy;
    private long _lastEnergyTicks;
    private double? _lastWatts;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private (HardwareAvailability Availability, string? Detail)? _lastFailure;

    public AmdSensorSource(ISystemInfo info) => _info = info;

    public (HardwareAvailability Availability, string? Detail) ProbeCpuSensors()
    {
        lock (_gate)
        {
            return EnsureSession();
        }
    }

    public bool TrySampleCpuSensors(out CpuSensorReading reading, out string? error)
    {
        reading = new CpuSensorReading(null, null);
        error = null;

        lock (_gate)
        {
            var (availability, detail) = EnsureSession();
            if (availability != HardwareAvailability.Available || _session is null)
            {
                error = detail;
                return false;
            }

            var notes = new List<string>(2);
            double? temperature = null;
            if (TryReadTemperature(out var celsius, out var temperatureError))
            {
                temperature = celsius;
            }
            else if (temperatureError is not null)
            {
                notes.Add(temperatureError);
            }

            if (!TryReadMsr(MsrPackageEnergyStat, out var energy, out var energyError))
            {
                error = energyError;
                reading = new CpuSensorReading(temperature, _lastWatts, JoinNotes(notes));
                return false;
            }

            UpdatePackagePower((uint)energy);
            if (_lastWatts is null)
            {
                notes.Add("首帧没有能耗基线，下一帧起给出封装功耗。");
            }

            reading = new CpuSensorReading(temperature, _lastWatts, JoinNotes(notes));
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposeSession();
            ReleasePci();
            _pciMutex?.Dispose();
            _pciMutex = null;
        }
    }

    private void UpdatePackagePower(uint current)
    {
        var ticks = Stopwatch.GetTimestamp();
        if (_hasEnergyBaseline)
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastEnergyTicks, ticks);
            if (elapsed >= MinimumPowerWindow)
            {
                var delta = AmdSensorDecoder.EnergyDelta(_lastEnergy, current);
                _lastWatts = AmdSensorDecoder.EnergyToWatts(delta, _energyExponent, elapsed);
            }
        }

        _lastEnergy = current;
        _lastEnergyTicks = ticks;
        _hasEnergyBaseline = true;
    }

    private (HardwareAvailability Availability, string? Detail) EnsureSession()
    {
        if (_session is { IsOpen: true } && _probed)
        {
            return (HardwareAvailability.Available, null);
        }

        if (_lastFailure is { } cached && DateTimeOffset.UtcNow - _lastFailureAt < FailureRetryInterval)
        {
            return cached;
        }

        var result = Open();
        if (result.Availability == HardwareAvailability.Available)
        {
            _lastFailure = null;
            return result;
        }

        _lastFailureAt = DateTimeOffset.UtcNow;
        _lastFailure = result;
        return result;
    }

    private (HardwareAvailability Availability, string? Detail) Open()
    {
        DisposeSession();

        var vendor = _info.CpuVendor;
        if (!string.Equals(vendor, "AuthenticAMD", StringComparison.OrdinalIgnoreCase))
        {
            return (HardwareAvailability.NotSupported, $"处理器不是 AMD（{vendor ?? "未知"}）。");
        }

        if (!AmdCpuIdentifier.TryParse(_info.CpuIdentifier, out var family, out var model))
        {
            return (HardwareAvailability.NotSupported, $"无法解析处理器标识：{_info.CpuIdentifier ?? "未知"}。");
        }

        // Zen 及之后的家族共用这两个寄存器/偏移；更老的家族没有 THM_TCON_CUR_TMP 的同一布局。
        if (family is < 0x17 or > 0x1A)
        {
            return (HardwareAvailability.NotSupported,
                $"Family {family:X}h Model {model:X}h 不在传感器通道支持范围（Family 17h–1Ah）。");
        }

        var modulePath = PawnIoModules.Resolve(ModuleFileName, out var searched);
        if (modulePath is null)
        {
            return (HardwareAvailability.ModuleMissing,
                $"未找到 PawnIO 模块，已查找：{searched}。可用 ZENTWIK_PAWNIO_MODULE 指定。");
        }

        var open = PawnIoSession.Open(modulePath);
        if (open.Session is null)
        {
            return (open.Availability, open.Detail);
        }

        _session = open.Session;

        if (!TryReadMsr(MsrPowerUnit, out var powerUnit, out var unitError))
        {
            DisposeSession();
            return (HardwareAvailability.ProbeFailed, $"读取 MSR_PWR_UNIT 失败：{unitError}");
        }

        _energyExponent = AmdSensorDecoder.EnergyExponent((uint)powerUnit);

        if (!TryReadTemperature(out _, out var temperatureError))
        {
            DisposeSession();
            return (HardwareAvailability.ProbeFailed, $"读取温度失败：{temperatureError}");
        }

        _probed = true;
        return (HardwareAvailability.Available, null);
    }

    private bool TryReadTemperature(out double? celsius, out string? error)
    {
        celsius = null;
        error = null;
        if (_session is null)
        {
            error = "传感器会话未打开。";
            return false;
        }

        if (!TryEnterPci(out error))
        {
            return false;
        }

        try
        {
            var output = new long[1];
            if (!_session.Execute(FunctionReadSmn, [(long)SmnThmTconCurTmp], output, out var code))
            {
                error = $"SMN 0x{SmnThmTconCurTmp:X8} 读取失败，Win32 错误码 {code}。";
                return false;
            }

            celsius = AmdSensorDecoder.DecodeTemperatureCelsius((uint)output[0]);
            return true;
        }
        finally
        {
            ReleasePci();
        }
    }

    private bool TryReadMsr(uint index, out ulong value, out string? error)
    {
        value = 0;
        error = null;
        if (_session is null)
        {
            error = "传感器会话未打开。";
            return false;
        }

        long[] input = [index];
        var output = new long[1];
        if (!_session.Execute(FunctionReadMsr, input, output, out var code))
        {
            error = $"MSR 0x{index:X8} 读取失败，Win32 错误码 {code}。";
            return false;
        }

        value = unchecked((ulong)output[0]);
        return true;
    }

    private bool TryEnterPci(out string? error)
    {
        if (_pciMutex is null)
        {
            try
            {
                _pciMutex = new Mutex(false, PciMutexName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                error = $"无法创建 PCI 互斥锁 {PciMutexName}：{ex.Message}";
                return false;
            }
        }

        try
        {
            if (!_pciMutex.WaitOne(PciMutexTimeout))
            {
                error = $"PCI 访问被其他进程占用，未在 {PciMutexTimeout.TotalMilliseconds:0} 毫秒内取得互斥锁。";
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者退出，互斥锁归当前线程。
        }

        _pciHeld = true;
        error = null;
        return true;
    }

    private void ReleasePci()
    {
        if (_pciMutex is null || !_pciHeld)
        {
            return;
        }

        _pciHeld = false;
        try
        {
            _pciMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 非持有线程调用：忽略。
        }
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
        _probed = false;
        _hasEnergyBaseline = false;
        _lastWatts = null;
    }

    private static string? JoinNotes(List<string> notes) =>
        notes.Count == 0 ? null : string.Join("；", notes);
}
