using Zentwik.Platform;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Fakes;

/// <summary>仅用于开发与测试：不接触硬件，所有结果必须标记为模拟。</summary>
public sealed class FakeCpuTuningProvider : ICpuTuningProvider
{
    private readonly Dictionary<int, int> _offsets = new();

    public FakeCpuTuningProvider(int coreCount = 8)
    {
        for (var core = 0; core < coreCount; core++)
        {
            _offsets[core] = 0;
        }
    }

    public CpuTuningStatus Query() => new(
        HardwareAvailability.Available,
        "模拟提供者，未接触硬件。",
        "Fake",
        "simulated",
        true,
        true);

    /// <summary>置为 true 后所有写入返回失败，用于验证失败保留与重试路径。</summary>
    public bool FailWrites { get; set; }

    public bool TryReadOffsets(out IReadOnlyList<CoreOffset> offsets, out string? error)
    {
        offsets = _offsets.OrderBy(static pair => pair.Key).Select(static pair => new CoreOffset(pair.Key, pair.Value)).ToArray();
        error = null;
        return true;
    }

    public bool TryWriteOffset(int core, int millivolts, out string? error)
    {
        if (FailWrites)
        {
            error = "模拟写入失败。";
            return false;
        }

        if (!_offsets.ContainsKey(core))
        {
            error = $"核 {core} 不存在。";
            return false;
        }

        if (millivolts is < -50 or > 50)
        {
            error = $"偏移量 {millivolts} mV 超出范围。";
            return false;
        }

        _offsets[core] = millivolts;
        error = null;
        return true;
    }

    public void Dispose()
    {
    }
}

public sealed class FakeIntelTuningProvider : IIntelTuningProvider
{
    private long _voltage;
    private long _turbo = 0x002A_0026;   // 8 核 42x / 单核 38x

    public CpuTuningStatus Query() => new(
        HardwareAvailability.Available,
        "模拟提供者，未接触硬件。",
        "Fake",
        "simulated",
        false,
        true);

    public bool TryRead(out IntelTuningState state, out string? error)
    {
        error = null;
        state = new IntelTuningState(
            IntelMsrBits.DecodeVoltageOffset(_voltage),
            IntelMsrBits.DecodeTurboRatio(_turbo, 1),
            IntelMsrBits.DecodeTurboRatio(_turbo, 8));
        return true;
    }

    public bool TryWriteVoltageOffset(int millivolts, out string? error)
    {
        if (millivolts is < -150 or > 100)
        {
            error = $"电压偏移 {millivolts} mV 超出范围。";
            return false;
        }

        _voltage = IntelMsrBits.EncodeVoltageOffset(_voltage, millivolts);
        error = null;
        return true;
    }
}

public sealed class FakePowerPolicyService : IPowerPolicyService{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private readonly Dictionary<(Guid Scheme, Guid Sub, Guid Setting, bool Ac), uint> _values = new();
    private Guid _active = Balanced;

    public PowerSchemeInfo? GetActiveScheme() => new(_active, Name(_active));

    public IReadOnlyList<PowerSchemeInfo> ListSchemes() =>
        [new(Balanced, Name(Balanced)), new(HighPerformance, Name(HighPerformance))];

    public bool TrySetActiveScheme(Guid scheme, out string? error)
    {
        _active = scheme;
        error = null;
        return true;
    }

    public bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error)
    {
        value = _values.GetValueOrDefault((scheme, subGroup, setting, ac));
        error = null;
        return true;
    }

    public bool TryWriteValue(Guid scheme, Guid subGroup, Guid setting, bool ac, uint value, out string? error)
    {
        _values[(scheme, subGroup, setting, ac)] = value;
        error = null;
        return true;
    }

    private static string Name(Guid scheme) => scheme == HighPerformance ? "高性能（模拟）" : "平衡（模拟）";
}

public sealed class FakePowerStatus(bool onAc = true) : IPowerStatus
{
    public bool? OnAcPower { get; set; } = onAc;
}

public sealed class FakeProcessScheduler : IProcessScheduler
{
    private readonly List<ProcessTarget> _targets;

    public FakeProcessScheduler(params ProcessTarget[] targets) => _targets = [.. targets];

    public List<(int ProcessId, string? CpuMask, string? Priority)> Applied { get; } = [];

    public IReadOnlyList<ProcessTarget> Find(string name) =>
        _targets.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();

    public bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error)
    {
        cpuMask = "0x3";
        priority = "Normal";
        error = null;
        return true;
    }

    public bool TryApply(int processId, string? cpuMask, string? priority, out string? error)
    {
        Applied.Add((processId, cpuMask, priority));
        error = null;
        return true;
    }
}
