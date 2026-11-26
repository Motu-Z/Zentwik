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

    /// <summary>模拟平台未实现 Boost Limit 消息：基线读为 0。</summary>
    public bool FMaxBaselineZero { get; set; }

    /// <summary>模拟固件按步进取整：写入值向下对齐到该粒度。</summary>
    public int FMaxStepMhz { get; set; } = 1;

    public bool TryReadFMax(out int limitMhz, out string? error)
    {
        limitMhz = FMaxBaselineZero ? 0 : FMaxMhz;
        error = null;
        return true;
    }

    public FMaxWriteResult TryWriteFMax(int limitMhz, out string? error)
    {
        if (FailWrites)
        {
            error = "模拟写入失败。";
            return FMaxWriteResult.Failed;
        }

        if (!FMaxWriteSupported)
        {
            error = "当前平台未配置 FMax 写入命令，不能将未配置视为硬件不支持。";
            return FMaxWriteResult.Unsupported;
        }

        if (FMaxBaselineZero)
        {
            error = "模拟平台 Boost Limit 基线为 0，视为未实现该消息。";
            return FMaxWriteResult.Unsupported;
        }

        if (limitMhz is < 1000 or > 7000)
        {
            error = $"FMax 频率 {limitMhz} MHz 超出输入范围 [1000, 7000] MHz（软件输入边界，不是固件限制）。";
            return FMaxWriteResult.Failed;
        }

        var rounded = FMaxStepMhz > 1 ? limitMhz / FMaxStepMhz * FMaxStepMhz : limitMhz;
        FMaxMhz = rounded;
        error = null;
        return FMaxWriteResult.Applied;
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

    /// <summary>模拟微码忽略写入（UVP/CFG Lock）：写入返回成功，但回读保持旧值。</summary>
    public bool IgnoreVoltageWrites { get; set; }

    public bool TryWriteVoltageOffset(int millivolts, out string? error)
    {
        if (millivolts is < -150 or > 100)
        {
            error = $"电压偏移 {millivolts} mV 超出范围。";
            return false;
        }

        if (IgnoreVoltageWrites)
        {
            error = null;
            return true;
        }

        _voltage = IntelMsrBits.BuildWriteCommand(millivolts);
        error = null;
        return true;
    }
}

public sealed class FakePowerPolicyService : IPowerPolicyService
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>内置模板键始终存在于注册表，即使不出现在方案枚举里，因此随时可以拿来派生。</summary>
    private static readonly HashSet<Guid> TemplateOnly = [PowerSaver, HighPerformance, UltimatePerformance];

    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid CoreParking = new("0cc5b647-c1df-4637-891a-dec35c318583");

    private readonly Dictionary<(Guid Scheme, Guid Sub, Guid Setting, bool Ac), uint> _values = new();
    private readonly Dictionary<Guid, string> _schemes = new()
    {
        [Balanced] = "平衡（模拟）",
        [HighPerformance] = "高性能（模拟）",
    };

    private readonly Dictionary<Guid, string> _descriptions = new();
    private readonly HashSet<Guid> _hidden = [];

    private Guid _active = Balanced;
    private int _nextCreated;

    /// <summary>核心停泊最小核心数，100 = 不停泊。</summary>
    public uint CoreParkingMinPercent { get; set; } = 100;

    /// <summary>模拟派生失败（模板不存在或系统拒绝）。</summary>
    public bool DuplicateFails { get; set; }

    /// <summary>
    /// 模拟「从该模板派生出的方案继承隐藏标记」：派生品拿得到 GUID，
    /// 但不出现在 <see cref="ListSchemes"/> 里，只有激活那一刻才可见。
    /// </summary>
    public HashSet<Guid> HiddenTemplates { get; } = [];

    /// <summary>登记一个取值，用于验证「搬设置」是否真的搬了。</summary>
    public void SetValue(Guid scheme, Guid subGroup, Guid setting, bool ac, uint value) =>
        _values[(scheme, subGroup, setting, ac)] = value;

    public PowerSchemeInfo? GetActiveScheme() =>
        new(_active, _schemes.TryGetValue(_active, out var name) ? name : _active.ToString("D"));

    public IReadOnlyList<PowerSchemeInfo> ListSchemes() =>
        _schemes
            .Where(pair => !_hidden.Contains(pair.Key))
            .Select(pair => new PowerSchemeInfo(pair.Key, pair.Value))
            .ToArray();

    public bool TrySetActiveScheme(Guid scheme, out string? error)
    {
        _active = scheme;
        error = null;
        return true;
    }

    public bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error)
    {
        if (subGroup == SubProcessor && setting == CoreParking)
        {
            value = CoreParkingMinPercent;
            error = null;
            return true;
        }

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

    public Guid? DuplicateScheme(Guid template, out string? error)
    {
        if (DuplicateFails || (!_schemes.ContainsKey(template) && !TemplateOnly.Contains(template)))
        {
            error = "派生失败。";
            return null;
        }

        var created = new Guid($"00000000-0000-0000-0000-{++_nextCreated:D12}");
        _schemes[created] = (_schemes.TryGetValue(template, out var templateName) ? templateName : "模板") + "（副本）";

        // 与真实系统一致：派生品继承模板的说明；模板被隐藏时派生品也隐藏。
        if (_descriptions.TryGetValue(template, out var description))
        {
            _descriptions[created] = description;
        }

        if (HiddenTemplates.Contains(template))
        {
            _hidden.Add(created);
        }

        error = null;
        return created;
    }

    public IReadOnlyList<Guid> EnumerateSubgroups(Guid scheme) =>
        _values.Keys
            .Where(key => key.Scheme == scheme)
            .Select(key => key.Sub)
            .Distinct()
            .ToArray();

    public IReadOnlyList<Guid> EnumerateSettings(Guid scheme, Guid subGroup) =>
        _values.Keys
            .Where(key => key.Scheme == scheme && key.Sub == subGroup)
            .Select(key => key.Setting)
            .Distinct()
            .ToArray();

    public string? ReadDescription(Guid scheme) => _descriptions.GetValueOrDefault(scheme);

    public bool TryWriteDescription(Guid scheme, string description, out string? error)
    {
        if (!_schemes.ContainsKey(scheme))
        {
            error = "方案不存在。";
            return false;
        }

        _descriptions[scheme] = description;
        error = null;
        return true;
    }

    public bool RenameScheme(Guid scheme, string name, out string? error)
    {
        if (!_schemes.ContainsKey(scheme))
        {
            error = "方案不存在。";
            return false;
        }

        _schemes[scheme] = name;
        error = null;
        return true;
    }

    public bool DeleteScheme(Guid scheme, out string? error)
    {
        if (!_schemes.ContainsKey(scheme) || scheme == _active)
        {
            error = "删除失败。";
            return false;
        }

        _schemes.Remove(scheme);
        error = null;
        return true;
    }
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
    private readonly Dictionary<int, long> _startTicks = new();
    private readonly HashSet<int> _exited = [];
    private long _nextTicks;

    public FakeProcessScheduler(params ProcessTarget[] targets)
    {
        _targets = [];
        foreach (var target in targets)
        {
            _targets.Add(target with { StartedAtTicks = RegisterTicks(target.StartedAtTicks) });
            _startTicks[target.ProcessId] = _targets[^1].StartedAtTicks;
        }
    }

    public bool CpuSetsSupported { get; set; } = true;

    /// <summary>模拟系统静默拒绝 CPU Sets 写入：接口返回成功但值不变，用于验证回读比对。</summary>
    public bool CpuSetsWriteIgnored { get; set; }

    public List<(int ProcessId, string? CpuMask, string? Priority)> Applied { get; } = [];

    public List<(int ProcessId, string? CpuSets)> AppliedCpuSets { get; } = [];

    public void AddProcess(ProcessTarget target, string initialMask = "0x3", string initialPriority = "Normal")
    {
        _exited.Remove(target.ProcessId);
        _targets.RemoveAll(t => t.ProcessId == target.ProcessId);

        var ticks = RegisterTicks(target.StartedAtTicks);
        _targets.Add(target with { StartedAtTicks = ticks });
        _startTicks[target.ProcessId] = ticks;
        _states[target.ProcessId] = (initialMask, initialPriority);
    }

    private long RegisterTicks(long requested) => requested > 0 ? requested : ++_nextTicks;

    public void RemoveProcess(int processId)
    {
        _targets.RemoveAll(t => t.ProcessId == processId);
        _states.Remove(processId);
        _startTicks.Remove(processId);
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

        if (!CpuSetsWriteIgnored)
        {
            _cpuSets[processId] = normalized;
        }

        AppliedCpuSets.Add((processId, normalized));
        error = null;
        return true;
    }

    public bool TryGetStartTicks(int processId, out long ticks)
    {
        ticks = 0;
        return !_exited.Contains(processId) && _startTicks.TryGetValue(processId, out ticks);
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