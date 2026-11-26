using Zentwik.Domain.Plans;
using Zentwik.Platform;
using Zentwik.Platform.Windows;

namespace Zentwik.Platform.Fakes;

/// <summary>仅用于开发与测试：不接触硬件，所有结果必须标记为模拟。</summary>
public sealed class FakeCpuTuningProvider : ICpuTuningProvider, IFMaxTuningProvider, ICpuCcdTopologyProvider
{
    private readonly Dictionary<int, int> _offsets = new();
    private readonly Dictionary<int, int> _coreCcds = new();

    public FakeCpuTuningProvider(int coreCount = 8, int coresPerCcd = 4)
    {
        for (var core = 0; core < coreCount; core++)
        {
            _offsets[core] = 0;
            _coreCcds[core] = coresPerCcd > 0 ? core / coresPerCcd : 0;
        }
    }

    public bool TopologyAvailable { get; set; } = true;

    public string PlatformName { get; set; } = "simulated";

    public void SetCoreCcd(int core, int ccd) => _coreCcds[core] = ccd;

    public bool TryGetCoreCcd(int coreIndex, out int ccdIndex)
    {
        if (!TopologyAvailable)
        {
            ccdIndex = 0;
            return false;
        }

        return _coreCcds.TryGetValue(coreIndex, out ccdIndex);
    }

    public CpuTuningStatus Query() => new(
        HardwareAvailability.Available,
        "模拟提供者，未接触硬件。",
        "Fake",
        PlatformName,
        true,
        true);

    /// <summary>置为 true 后所有写入返回失败，用于验证失败保留与重试路径。</summary>
    public bool FailWrites { get; set; }

    /// <summary>模拟部分核读不到：读取时跳过这些核，仍返回其余核。</summary>
    public HashSet<int> UnreadableCores { get; } = [];

    public bool TryReadOffsets(out IReadOnlyList<CoreOffset> offsets, out string? error)
    {
        offsets = _offsets
            .Where(pair => !UnreadableCores.Contains(pair.Key))
            .OrderBy(static pair => pair.Key)
            .Select(static pair => new CoreOffset(pair.Key, pair.Value))
            .ToArray();
        error = UnreadableCores.Count == 0
            ? null
            : $"{UnreadableCores.Count} 个核读取失败：{string.Join("、", UnreadableCores.Order())}。";
        return true;
    }

    public bool TryWriteOffset(int core, int offset, out string? error)
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

        if (!CurveOptimizerBounds.IsWithinL2(offset))
        {
            error = CurveOptimizerBounds.FormatL2Rejection(offset);
            return false;
        }

        _offsets[core] = offset;
        error = null;
        return true;
    }

    /// <summary>模拟 FMax（MHz）与是否配置 FMax 写入命令。</summary>
    public int FMaxMhz { get; set; } = 5700;

    public bool FMaxWriteSupported { get; set; } = true;

    public bool TryReadFMax(out int limitMhz, out string? error)
    {
        limitMhz = FMaxMhz;
        error = null;
        return true;
    }

    public bool TryWriteFMax(int limitMhz, out string? error)
    {
        if (FailWrites)
        {
            error = "模拟写入失败。";
            return false;
        }

        if (!FMaxWriteSupported)
        {
            error = "当前平台未配置 FMax 写入命令，不能将未配置视为硬件不支持。";
            return false;
        }

        if (limitMhz is < 1000 or > 7000)
        {
            error = $"FMax 频率 {limitMhz} MHz 超出有效范围 [1000, 7000]。";
            return false;
        }

        FMaxMhz = limitMhz;
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

public sealed class FakePowerPolicyService : IPowerPolicyService
{
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

public sealed class FakeProcessScheduler : IProcessScheduler, IProcessCpuSetScheduler
{
    private readonly List<ProcessTarget> _targets;
    private readonly Dictionary<int, (string CpuMask, string Priority)> _states = new();
    private readonly Dictionary<int, string> _cpuSets = new();
    private readonly HashSet<int> _exited = [];

    public FakeProcessScheduler(params ProcessTarget[] targets) => _targets = [.. targets];

    public bool CpuSetsSupported { get; set; } = true;

    public List<(int ProcessId, string? CpuMask, string? Priority)> Applied { get; } = [];

    public List<(int ProcessId, string? CpuSets)> AppliedCpuSets { get; } = [];

    public void AddProcess(ProcessTarget target, string initialMask = "0x3", string initialPriority = "Normal")
    {
        _exited.Remove(target.ProcessId);
        if (_targets.All(t => t.ProcessId != target.ProcessId))
        {
            _targets.Add(target);
        }

        _states[target.ProcessId] = (initialMask, initialPriority);
    }

    public void RemoveProcess(int processId)
    {
        _targets.RemoveAll(t => t.ProcessId == processId);
        _states.Remove(processId);
        _exited.Add(processId);
    }

    public void SetProcessState(int processId, string? cpuMask = null, string? priority = null, string? cpuSets = null)
    {
        var (currentMask, currentPriority) = _states.TryGetValue(processId, out var state)
            ? state
            : ("0x3", "Normal");
        _states[processId] = (cpuMask ?? currentMask, priority ?? currentPriority);
        if (cpuSets is not null)
        {
            _cpuSets[processId] = cpuSets;
        }
    }

    public bool IsCpuSetsSupported(out string? detail)
    {
        detail = CpuSetsSupported ? "模拟 CPU Sets 可用。" : "模拟环境禁用 CPU Sets。";
        return CpuSetsSupported;
    }

    public bool TryReadCpuSets(int processId, out string? cpuSets, out string? error)
    {
        if (!CpuSetsSupported)
        {
            cpuSets = null;
            error = "模拟环境不支持 CPU Sets。";
            return false;
        }

        if (_exited.Contains(processId))
        {
            cpuSets = null;
            error = $"进程 {processId} 已退出。";
            return false;
        }

        cpuSets = _cpuSets.GetValueOrDefault(processId, string.Empty);
        error = null;
        return true;
    }

    public bool TryApplyCpuSets(int processId, string? cpuSets, out string? error)
    {
        if (!CpuSetsSupported)
        {
            error = "模拟环境不支持 CPU Sets。";
            return false;
        }

        if (_exited.Contains(processId))
        {
            error = $"进程 {processId} 已退出。";
            return false;
        }

        var normalized = string.Empty;
        if (!string.IsNullOrWhiteSpace(cpuSets))
        {
            if (!CpuSetSpec.TryNormalize(cpuSets, out normalized, out error))
            {
                return false;
            }
        }

        _cpuSets[processId] = normalized;
        AppliedCpuSets.Add((processId, normalized));
        error = null;
        return true;
    }

    public IReadOnlyList<ProcessTarget> Find(string name) =>
        _targets.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();

    public bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error)
    {
        if (_exited.Contains(processId))
        {
            cpuMask = null;
            priority = null;
            error = $"进程 {processId} 已退出。";
            return false;
        }

        var (mask, prio) = _states.TryGetValue(processId, out var state) ? state : ("0x3", "Normal");
        cpuMask = mask;
        priority = prio;
        error = null;
        return true;
    }

    public bool TryApply(int processId, string? cpuMask, string? priority, out string? error)
    {
        if (_exited.Contains(processId))
        {
            error = $"进程 {processId} 已退出。";
            return false;
        }

        var (currentMask, currentPriority) = _states.TryGetValue(processId, out var state)
            ? state
            : ("0x3", "Normal");
        _states[processId] = (cpuMask ?? currentMask, priority ?? currentPriority);
        Applied.Add((processId, cpuMask, priority));
        error = null;
        return true;
    }
}