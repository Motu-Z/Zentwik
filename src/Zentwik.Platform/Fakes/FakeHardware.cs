using Zentwik.Domain.Plans;
using Zentwik.Domain.Power;
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

        // 与真实提供者一致：越界钳到支持范围，不拒绝；真实生效值由回读给出。
        var clamped = FMaxRange.Clamp(limitMhz);
        var rounded = FMaxStepMhz > 1 ? clamped / FMaxStepMhz * FMaxStepMhz : clamped;
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

    /// <summary>模拟「从该模板派生出的方案继承隐藏标记」：派生品拿得到 GUID，
    /// 但不出现在 <see cref="ListSchemes"/> 里，只有激活那一刻才可见。</summary>
    public HashSet<Guid> HiddenTemplates { get; } = [];

    /// <summary>这些设置项拒绝写入（模拟受硬件或驱动限制的设置）。</summary>
    public HashSet<Guid> FailingWriteSettings { get; } = [];

    /// <summary>这些设置项的取值读不出来（模拟未定义的设置）。</summary>
    public HashSet<Guid> FailingReadSettings { get; } = [];

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

    /// <remarks>与真实系统同构：模板键始终在注册表里，即使不出现在枚举结果中，也算「存在但不可见」。</remarks>
    public IReadOnlyList<PowerSchemeEntry> ListAllSchemes()
    {
        var entries = _schemes
            .Select(pair => new PowerSchemeEntry(pair.Key, pair.Value, !_hidden.Contains(pair.Key)))
            .ToList();

        foreach (var template in TemplateOnly)
        {
            if (!_schemes.ContainsKey(template))
            {
                entries.Add(new PowerSchemeEntry(template, TemplateName(template), false));
            }
        }

        return entries;
    }

    private static string TemplateName(Guid scheme)
    {
        if (scheme == PowerSaver)
        {
            return "节能（模拟）";
        }

        if (scheme == HighPerformance)
        {
            return "高性能（模拟）";
        }

        return scheme == UltimatePerformance ? "卓越性能（模拟）" : scheme.ToString("D");
    }

    public bool TrySetActiveScheme(Guid scheme, out string? error)
    {
        _active = scheme;
        error = null;
        return true;
    }

    public bool TryReadValue(Guid scheme, Guid subGroup, Guid setting, bool ac, out uint value, out string? error)
    {
        if (FailingReadSettings.Contains(setting))
        {
            value = 0;
            error = "模拟读取失败。";
            return false;
        }

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
        if (FailingWriteSettings.Contains(setting))
        {
            error = "模拟写入被拒。";
            return false;
        }

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

    /// <summary>.pow 是 powercfg 的产物，模拟环境造不出来；返回失败，调用方必须当成非致命。</summary>
    public bool TryExportScheme(string path, Guid scheme, out string? error)
    {
        error = "模拟环境不导出 .pow。";
        return false;
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
/// <summary>仅用于开发与测试：不接触 SMU，读数与写入结果都标记为模拟。</summary>
public sealed class FakePowerLimitProvider : IPowerLimitProvider
{
    private int? _fast = 100;
    private int? _slow = 90;

    public bool RejectWrites { get; set; }

    /// <summary>模拟固件的折算比例。1 表示原样生效，0.8 表示回读为请求值的八成。</summary>
    public double WriteScale { get; set; } = 1;

    public (HardwareAvailability Availability, string? Detail) ProbePowerLimit() =>
        (HardwareAvailability.Available, "模拟提供者，功耗表读数非真实硬件值。");

    public bool TryReadCurrentPowerLimits(out PowerLimitCurrent current, out string? error)
    {
        current = new PowerLimitCurrent(110, _fast, _slow, "模拟读数。");
        error = null;
        return true;
    }

    public bool TryReadFusedPowerLimits(out FusedPowerLimits limits, out string? error)
    {
        limits = new FusedPowerLimits(120, 110, 180, null, "模拟熔断值。");
        error = null;
        return true;
    }

    public PowerLimitApplyResult ApplyPowerLimits(PowerLimitRequest request)
    {
        if (RejectWrites)
        {
            return new PowerLimitApplyResult([], null, null, "模拟拒绝：固件不接受功耗写入。", true);
        }

        if (PowerLimitBounds.Validate(request) is { } invalid)
        {
            return new PowerLimitApplyResult([], null, null, invalid, true);
        }

        var before = new PowerLimitCurrent(110, _fast, _slow, "模拟读数。");
        var items = new List<PowerLimitWrite>(4);

        if (request.FastWatts is int fast)
        {
            var actual = (int)Math.Round(fast * WriteScale);
            _fast = actual;
            items.Add(Simulated("fast", fast, actual));
        }

        if (request.SlowWatts is int slow)
        {
            var actual = (int)Math.Round(slow * WriteScale);
            _slow = actual;
            items.Add(Simulated("slow", slow, actual));
        }

        if (request.TdcAmps is int tdc)
        {
            items.Add(new PowerLimitWrite("tdc", tdc, null, PowerLimitWriteState.Accepted, "模拟接受，无回读通道。"));
        }

        if (request.EdcAmps is int edc)
        {
            items.Add(new PowerLimitWrite("edc", edc, null, PowerLimitWriteState.Accepted, "模拟接受，无回读通道。"));
        }

        return new PowerLimitApplyResult(items, before, new PowerLimitCurrent(110, _fast, _slow, "模拟读数。"), null, true);
    }

    private static PowerLimitWrite Simulated(string key, int requested, int actual) => new(
        key,
        requested,
        actual,
        actual == requested ? PowerLimitWriteState.Applied : PowerLimitWriteState.Diverged,
        actual == requested ? "模拟回读一致。" : "模拟固件折算。");
}

/// <summary>
/// 固定进程表，用于 --fake 与单测：不做任何系统调用，也不模拟 CPU% 的真实变化。
/// </summary>
public sealed class FakeProcessInventory : IProcessInventory
{
    private readonly List<ProcessInventoryEntry> _entries =
    [
        new(4, "System", 8, null, 0.1),
        new(1000, "explorer", 8, @"C:/Windows/explorer.exe", 0.4),
        new(2000, "game", 8, @"D:/Games/game.exe", 12.5),
    ];

    public int SnapshotCount { get; private set; }

    public IReadOnlyList<int> LastDetailPids { get; private set; } = [];

    public IReadOnlyList<ProcessInventoryEntry> Snapshot(IReadOnlyCollection<int> detailProcessIds)
    {
        SnapshotCount++;
        LastDetailPids = [.. detailProcessIds];

        return [.. _entries.Select(entry => entry with
        {
            // CPU% 来自全表快照，所有行都有；路径属于单查范围，范围外不给。
            ImagePath = detailProcessIds.Contains(entry.ProcessId) ? entry.ImagePath : null,
        })];
    }
}
