using System.Globalization;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>
/// AMD Curve Optimizer 通道。命令族按 CPU family/model 选择，写入后必须回读一致才算成功；
/// 只读失败不会伪造数据，写入失败不回退到「假定成功」。
/// </summary>
public sealed class AmdSmuTuningProvider : ICpuTuningProvider
{
    private const uint MessageTest = 0x01;
    private const string FunctionSendCommand = "ioctl_send_smu_command";
    private const string FunctionGetVersion = "ioctl_get_smu_version";
    private const int MinOffset = -50;
    private const int MaxOffset = 50;
    private const int MaxArgs = 6;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly ISystemInfo _info;
    private readonly Lock _gate = new();
    private PawnIoSession? _session;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private CpuTuningStatus? _lastFailure;
    private CoCommands _commands;
    private CoreAddressing _addressing = CoreAddressing.Bitmask;
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

            var values = new List<CoreOffset>(_info.PhysicalCoreCount);
            var failed = new List<string>(_info.PhysicalCoreCount);
            var result = new long[MaxArgs];
            for (var core = 0; core < _info.PhysicalCoreCount; core++)
            {
                Array.Clear(result);
                var mask = EncodeMask((uint)core);
                if (!TrySend(_commands.Get, [mask], result, out var failure))
                {
                    failed.Add($"{core}(0x{mask:X8}) {failure}");
                    continue;
                }

                values.Add(new CoreOffset(core, SignExtend16(result[0])));
            }

            if (values.Count == 0)
            {
                error = $"{failed.Count} 个核全部读取失败：{string.Join("；", failed)}";
                return false;
            }

            offsets = values;
            error = failed.Count == 0
                ? null
                : $"{failed.Count}/{_info.PhysicalCoreCount} 个核读取失败：{string.Join("；", failed)}";
            return true;
        }
    }

    public bool TryWriteOffset(int core, int millivolts, out string? error)
    {
        if (millivolts is < MinOffset or > MaxOffset)
        {
            error = $"偏移量 {millivolts} mV 超出允许范围 {MinOffset}..{MaxOffset}。";
            return false;
        }

        lock (_gate)
        {
            var status = EnsureSession(out error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return false;
            }

            var mask = EncodeMask((uint)core);
            var payload = (mask & 0xFFF0_0000u) | (unchecked((uint)millivolts) & 0xFFFFu);
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
            if (actual != millivolts)
            {
                error = $"核 {core} 回读 {actual} mV，与写入 {millivolts} mV 不一致。";
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
        _addressing = CoreAddressing.Bitmask;

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

        if (!ProbeCoreAccess(out error))
        {
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.Rejected, error, vendor, PlatformName(family, model), true, false);
        }

        _probed = true;
        return Available();
    }

    /// <summary>
    /// 各代固件对核地址的编码并不一致：有的把核序号放在 bit[23:20]，有的用独热位 bit[20+核]。
    /// 用只读命令试出可用的那一种，避免把「掩码 0」当成有效核地址发给固件。
    /// </summary>
    private bool ProbeCoreAccess(out string? error)
    {
        var attempts = new List<string>(2);

        foreach (var addressing in (ReadOnlySpan<CoreAddressing>)[CoreAddressing.Bitmask, CoreAddressing.Index])
        {
            var previous = _addressing;
            _addressing = addressing;
            var mask = EncodeMask(0);

            if (TrySend(_commands.Get, [mask], new long[MaxArgs], out var failure))
            {
                error = null;
                return true;
            }

            _addressing = previous;
            attempts.Add($"0x{mask:X8} → {failure}");
        }

        error = $"物理核 0 的 CO 读取被平台拒绝（{string.Join("；", attempts)}）";
        return false;
    }

    private CpuTuningStatus Available() => new(
        HardwareAvailability.Available,
        $"SMU 0x{_smuVersion:X8}，{_info.PhysicalCoreCount} 个物理核 / {_info.ProcessorCount} 线程。写入后逐核回读校验。",
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

    private uint EncodeMask(uint core)
    {
        var ccd = core / 8;
        var localCore = core % 8;

        if (_addressing == CoreAddressing.Bitmask)
        {
            return _commands.Family > 0x17
                ? (ccd << 28) | (1u << (int)(20 + localCore))
                : (ccd << 28) | (1u << (int)(24 + ((core / 4) % 2))) | (1u << (int)(20 + (core % 4)));
        }

        return _commands.Family > 0x17
            ? (ccd << 28) | (localCore << 20)
            : (ccd << 28) | (((core / 4) % 2) << 24) | ((core % 4) << 20);
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
                commands = new CoCommands(family, model, 0x0A, 0x7C);
                return true;
            case 0x19:
                commands = model is 0x61 or 0x74 or 0x78
                    ? new CoCommands(family, model, 0x06, 0xD5)
                    : new CoCommands(family, model, 0x0A, 0x7C);
                return true;
            case 0x1A:
                commands = new CoCommands(family, model, 0x06, 0xD5);
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
    }

    private enum CoreAddressing
    {
        Bitmask,
        Index,
    }

    private readonly record struct CoCommands(uint Family, uint Model, uint Set, uint Get);
}
