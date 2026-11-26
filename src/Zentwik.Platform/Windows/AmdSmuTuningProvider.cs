using System.Globalization;
using Zentwik.Domain.Plans;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>
/// AMD Curve Optimizer 通道。命令族按 CPU family/model 选择，写入后必须回读一致才算成功；
/// 只读失败不会伪造数据，写入失败不回退到「假定成功」。
/// </summary>
public sealed class AmdSmuTuningProvider : ICpuTuningProvider, IFMaxTuningProvider
{
    private const uint MessageTest = 0x01;
    private const string FunctionSendCommand = "ioctl_send_smu_command";
    private const string FunctionGetVersion = "ioctl_get_smu_version";
    private const int MaxArgs = 6;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly ISystemInfo _info;
    private readonly Lock _gate = new();
    private PawnIoSession? _session;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private CpuTuningStatus? _lastFailure;
    private CoCommands _commands;
    private AmdCoreMap _coreMap = AmdCoreMap.Empty;
    private uint _smuVersion;
    private bool _probed;

    public AmdSmuTuningProvider(ISystemInfo info) => _info = info;

    public CpuTuningStatus Query()
    {
        lock (_gate)
        {
            return EnsureSession(out _);
        }
    }

    public bool TryReadOffsets(out IReadOnlyList<CoreOffset> offsets, out string? error)
    {
        offsets = [];
        lock (_gate)
        {
            var status = EnsureSession(out error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return false;
            }

            var values = new List<CoreOffset>(_coreMap.Cores.Count);
            var failed = new List<string>(_coreMap.Cores.Count);
            var result = new long[MaxArgs];
            foreach (var core in _coreMap.Cores)
            {
                Array.Clear(result);
                var mask = AmdCoreMask.Encode(core, _commands.Family);
                if (!TrySend(_commands.Get, [mask], result, out var failure))
                {
                    failed.Add($"{core.LogicalIndex}(CCD {core.Ccd}/核 {core.Core}, 0x{mask:X8}) {failure}");
                    continue;
                }

                values.Add(new CoreOffset(core.LogicalIndex, SignExtend16(result[0])));
            }

            if (values.Count == 0)
            {
                error = $"{failed.Count} 个核全部读取失败：{string.Join("；", failed)}";
                return false;
            }

            offsets = values;
            error = failed.Count == 0
                ? null
                : $"{failed.Count}/{_coreMap.Cores.Count} 个核读取失败：{string.Join("；", failed)}";
            return true;
        }
    }

    public bool TryWriteOffset(int core, int offset, out string? error)
    {
        if (!CurveOptimizerBounds.IsWithinL2(offset))
        {
            error = CurveOptimizerBounds.FormatL2Rejection(offset);
            return false;
        }

        lock (_gate)
        {
            var status = EnsureSession(out error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return false;
            }

            if (!_coreMap.TryGet(core, out var address))
            {
                error = $"核 {core} 不在当前映射中（共 {_coreMap.Cores.Count} 个物理核）。";
                return false;
            }

            var mask = AmdCoreMask.Encode(address, _commands.Family);
            var payload = (mask & 0xFFF0_0000u) | (unchecked((uint)offset) & 0xFFFFu);
            var scratch = new long[MaxArgs];
            if (!TrySend(_commands.Set, [payload], scratch, out error))
            {
                return false;
            }

            Array.Clear(scratch);
            if (!TrySend(_commands.Get, [mask], scratch, out error))
            {
                return false;
            }

            var actual = SignExtend16(scratch[0]);
            if (actual != offset)
            {
                error = $"核 {core} 回读 {actual}，与写入 {offset} 不一致。";
                return false;
            }

            return true;
        }
    }

    public bool TryReadFMax(out int limitMhz, out string? error)
    {
        limitMhz = 0;
        lock (_gate)
        {
            var status = EnsureSession(out error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return false;
            }

            if (_commands.GetFMax == 0)
            {
                error = $"平台 {PlatformName(_commands.Family, _commands.Model)} 未配置 FMax 读取命令。";
                return false;
            }

            var result = new long[MaxArgs];
            if (!TrySend(_commands.GetFMax, [0], result, out error))
            {
                return false;
            }

            limitMhz = (int)(result[0] & 0xFFFF_FFFFL);
            return true;
        }
    }

    public bool TryWriteFMax(int limitMhz, out string? error)
    {
        if (limitMhz is < 1000 or > 7000)
        {
            error = $"FMax 频率 {limitMhz} MHz 超出有效范围 [1000, 7000]。";
            return false;
        }

        lock (_gate)
        {
            var status = EnsureSession(out error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return false;
            }

            if (_commands.SetFMax == 0)
            {
                error = $"平台 {PlatformName(_commands.Family, _commands.Model)} 未配置 FMax 写入命令（GetBoostLimitFrequency=0x{_commands.GetFMax:X2} 可读），不能将未配置视为硬件不支持。";
                return false;
            }

            var scratch = new long[MaxArgs];
            if (!TrySend(_commands.SetFMax, [limitMhz], scratch, out error))
            {
                return false;
            }

            Array.Clear(scratch);
            if (!TrySend(_commands.GetFMax, [0], scratch, out error))
            {
                return false;
            }

            var actual = (int)(scratch[0] & 0xFFFF_FFFFL);
            if (actual != limitMhz)
            {
                error = $"FMax 回读 {actual} MHz，与写入 {limitMhz} MHz 不一致。";
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
        _coreMap = AmdCoreMap.Empty;

        var vendor = _info.CpuVendor;
        if (!string.Equals(vendor, "AuthenticAMD", StringComparison.OrdinalIgnoreCase))
        {
            error = $"处理器不是 AMD（{vendor ?? "未知"}）。";
            return new CpuTuningStatus(HardwareAvailability.NotSupported, error, vendor, _info.CpuName, true, false);
        }

        if (!TryParseFamilyModel(out var family, out var model))
        {
            error = $"无法解析处理器标识：{_info.CpuIdentifier ?? "未知"}。";
            return new CpuTuningStatus(HardwareAvailability.NotSupported, error, vendor, _info.CpuName, true, false);
        }

        if (!TryResolveCommands(family, model, out _commands))
        {
            error = $"Family {family:X}h Model {model:X}h 未列入支持的 SMU 命令族。";
            return new CpuTuningStatus(HardwareAvailability.NotSupported, error, vendor, PlatformName(family, model), true, false);
        }

        var modulePath = PawnIoModules.Resolve("RyzenSMU.bin", out var searched);
        if (modulePath is null)
        {
            var detail = $"未找到 PawnIO 模块，已查找：{searched}。可用 ZENTWIK_PAWNIO_MODULE 指定。";
            return new CpuTuningStatus(HardwareAvailability.ModuleMissing, detail, vendor, PlatformName(family, model), true, false);
        }

        var open = PawnIoSession.Open(modulePath);
        if (open.Session is null)
        {
            error = open.Detail;
            return new CpuTuningStatus(open.Availability, open.Detail, vendor, PlatformName(family, model), true, false);
        }

        _session = open.Session;

        var version = new long[1];
        if (!_session.Execute(FunctionGetVersion, [], version, out var versionError))
        {
            error = $"读取 SMU 版本失败，Win32 错误码 {versionError}。";
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.ProbeFailed, error, vendor, PlatformName(family, model), true, false);
        }

        _smuVersion = (uint)version[0];

        var probe = new long[MaxArgs];
        if (!TrySend(MessageTest, [1], probe, out error) || probe[0] != 2)
        {
            error ??= "SMU 就绪探测未返回预期值。";
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.ProbeFailed, error, vendor, PlatformName(family, model), true, false);
        }

        _coreMap = ResolveCoreMap(family, out error);
        if (_coreMap.Cores.Count == 0)
        {
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.Rejected, error, vendor, PlatformName(family, model), true, false);
        }

        if (!ProbeCoreReads(out error))
        {
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.Rejected, error, vendor, PlatformName(family, model), true, false);
        }

        _probed = true;
        return Available();
    }

    private AmdCoreMap ResolveCoreMap(uint family, out string? error)
    {
        error = null;

        if (AmdApobSource.TryLoad(out var table, out var loadError))
        {
            if (AmdApobCoreMap.TryParse(table!, family, _info.ProcessorCount, out var map, out var parseError))
            {
                return map;
            }

            // 指定了 APOB 表却不能解析：说明映射假设不成立，不能改用别的来源掩盖。
            error = parseError;
            return AmdCoreMap.Empty;
        }

        if (loadError is not null)
        {
            error = loadError;
            return AmdCoreMap.Empty;
        }

        var topology = AmdWindowsTopology.Resolve(family, _info.PhysicalCoreCount, _info.ProcessorCount, out var topologyError);
        error = topologyError;
        return topology;
    }

    /// <summary>
    /// 只读探测：按映射逐个核发读命令，任一成功即认为通道可用。单个核（例如掩码为 0 的核 0）被拒
    /// 不代表整条通道不可用，因此不在第一个核上就下结论，也不尝试别的编码。
    /// </summary>
    private bool ProbeCoreReads(out string? error)
    {
        string? firstFailure = null;

        foreach (var core in _coreMap.Cores)
        {
            var mask = AmdCoreMask.Encode(core, _commands.Family);
            if (TrySend(_commands.Get, [mask], new long[MaxArgs], out var failure))
            {
                error = null;
                return true;
            }

            firstFailure ??= $"核 {core.LogicalIndex}（CCD {core.Ccd} / 核 {core.Core}，掩码 0x{mask:X8}）：{failure}";
        }

        error = $"全部 {_coreMap.Cores.Count} 个核的 CO 读取都被拒绝，首个失败为 {firstFailure}";
        return false;
    }

    private CpuTuningStatus Available() => new(
        HardwareAvailability.Available,
        $"SMU 0x{_smuVersion:X8}，{_coreMap.Cores.Count} 个物理核 / {_info.ProcessorCount} 线程。"
        + $"{_coreMap.Detail}L2 输入范围 [{CurveOptimizerBounds.L2MinOffset}, +{CurveOptimizerBounds.L2MaxOffset}]（不对称软限制，非 L1 固件上限）。写入后逐核回读校验。",
        _info.CpuVendor,
        PlatformName(_commands.Family, _commands.Model),
        true,
        false);

    private bool TrySend(uint message, ReadOnlySpan<long> args, long[] result, out string? error)
    {
        error = null;
        if (_session is null)
        {
            error = "SMU 会话未建立。";
            return false;
        }

        var input = new long[MaxArgs + 1];
        input[0] = message;
        for (var i = 0; i < Math.Min(MaxArgs, args.Length); i++)
        {
            input[i + 1] = args[i];
        }

        var output = new long[MaxArgs];
        if (!_session.Execute(FunctionSendCommand, input, output, out var code))
        {
            error = DescribeFailure(message, code);
            return false;
        }

        for (var i = 0; i < Math.Min(result.Length, output.Length); i++)
        {
            result[i] = output[i];
        }

        return true;
    }

    private static string DescribeFailure(uint message, int code)
    {
        var meaning = code switch
        {
            31 => "模块转述 SMU 返回 0xFF（命令执行失败）",
            170 => "模块转述 SMU 返回 0xFC（暂忙）",
            121 => "SMU 未在超时内应答",
            _ => "驱动返回错误",
        };

        return $"SMU 命令 0x{message:X2} 失败：{meaning}（Win32 {code}）。";
    }

    private bool TryParseFamilyModel(out uint family, out uint model)
    {
        family = 0;
        model = 0;

        var identifier = _info.CpuIdentifier;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        family = ReadNumber(identifier, "Family");
        model = ReadNumber(identifier, "Model");
        return family != 0;
    }

    private static uint ReadNumber(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        var start = index + key.Length;
        while (start < text.Length && text[start] == ' ')
        {
            start++;
        }

        var end = start;
        while (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))
        {
            end++;
        }

        var span = text.AsSpan(start, end - start);
        return uint.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static bool TryResolveCommands(uint family, uint model, out CoCommands commands)
    {
        switch (family)
        {
            case 0x17:
                commands = new CoCommands(family, model, 0x0A, 0x7C, SetFMax: 0x00, GetFMax: 0x6E);
                return true;
            case 0x19:
                commands = model is 0x61 or 0x74 or 0x78
                    ? new CoCommands(family, model, 0x06, 0xD5, SetFMax: 0x70, GetFMax: 0x6E)
                    : new CoCommands(family, model, 0x0A, 0x7C, SetFMax: 0x00, GetFMax: 0x6E);
                return true;
            case 0x1A:
                commands = new CoCommands(family, model, 0x06, 0xD5, SetFMax: 0x70, GetFMax: 0x6E);
                return true;
            default:
                commands = default;
                return false;
        }
    }

    private static string PlatformName(uint family, uint model) => $"Family {family:X}h Model {model:X}h";

    private static int SignExtend16(long value)
    {
        var raw = (int)(value & 0xFFFF);
        return raw >= 0x8000 ? raw - 0x10000 : raw;
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
        _probed = false;
        _coreMap = AmdCoreMap.Empty;
    }

    private readonly record struct CoCommands(uint Family, uint Model, uint Set, uint Get, uint SetFMax, uint GetFMax);
}
