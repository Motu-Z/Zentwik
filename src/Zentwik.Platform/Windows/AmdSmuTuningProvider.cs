using System.Globalization;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Power;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>
/// AMD Curve Optimizer 通道。命令族按 CPU family/model 选择，写入后必须回读一致才算成功；
/// 只读失败不会伪造数据，写入失败不回退到「假定成功」。
/// </summary>
public sealed class AmdSmuTuningProvider : ICpuTuningProvider, IFMaxTuningProvider, ICpuCcdTopologyProvider, IPowerLimitProvider
{
    private const uint MessageTest = 0x01;

    /// <summary>PM 表在 DRAM 里，须先请求固件传输一份；不传读到的是上一次的残留内容。</summary>
    private const uint MessageTransferTableToDram = 0x03;

    // Zen4/Zen5 的 RSMU DPTC 接口（ZenStates-Core Zen4Settings.cs；Zen5Settings 直接继承 Zen4）。
    // MP1 侧另有一套（SetStapm 0x4F / SetFast 0x3E / SetSlow 0x5F），但 ioctl_send_smu_command 只能发 RSMU，
    // 套用 MP1 命令号会被当作 RSMU 命令解释，因此不提供 STAPM。
    private const uint MessageSetFastLimit = 0x56;
    private const uint MessageSetSlowLimit = 0xCB;
    private const uint MessageSetTdcLimit = 0x57;
    private const uint MessageSetEdcLimit = 0x58;
    private const uint MessageGetFusedPowerLimit = 0xDC;
    private const uint MessageGetFusedApuSlowLimit = 0xDA;
    private const uint MessageGetFusedVddTdc = 0xDB;
    private const string FunctionSendCommand = "ioctl_send_smu_command";
    private const string FunctionGetVersion = "ioctl_get_smu_version";
    private const string FunctionResolvePmTable = "ioctl_resolve_pm_table";
    private const string FunctionReadPmTable = "ioctl_read_pm_table";
    private const int MaxArgs = 6;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private const string PciMutexName = @"Global\Access_PCI";
    private static readonly TimeSpan PciMutexTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ISystemInfo _info;
    private readonly Lock _gate = new();
    private Mutex? _pciMutex;
    private bool _pciHeld;
    private PawnIoSession? _session;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private CpuTuningStatus? _lastFailure;
    private CoCommands _commands;
    private AmdCoreMap _coreMap = AmdCoreMap.Empty;
    private uint _smuVersion;
    private bool _probed;
    private bool _pmTableReady;
    private uint _pmTableVersion;

    // 本机实测的折算比例（按项分开）与此前成功写入的请求值。只在进程内保留：
    // 落盘要动 state.json 的 schema，而重启后第一次写入就会重新学到比例，不值得为它改数据格式。
    private readonly Dictionary<string, double> _observedScales = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastRequests = new(StringComparer.Ordinal);

    public AmdSmuTuningProvider(ISystemInfo info) => _info = info;

    public CpuTuningStatus Query()
    {
        lock (_gate)
        {
            return EnsureSession(out _);
        }
    }

    public bool TryGetCoreCcd(int coreIndex, out int ccdIndex)
    {
        ccdIndex = 0;
        lock (_gate)
        {
            var status = EnsureSession(out _);
            if (status.Availability != HardwareAvailability.Available || !_coreMap.TryGet(coreIndex, out var address))
            {
                return false;
            }

            ccdIndex = address.Ccd;
            return true;
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

    public FMaxWriteResult TryWriteFMax(int limitMhz, out string? error)
    {
        // 用户给的是绝对频率；越界不拒绝，钳到工具支持范围后再写，真实生效值由回读给出。
        var requested = limitMhz;
        limitMhz = FMaxRange.Clamp(limitMhz);

        lock (_gate)
        {
            var status = EnsureSession(out error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return FMaxWriteResult.Failed;
            }

            if (_commands.SetFMax == 0)
            {
                error = $"平台 {PlatformName(_commands.Family, _commands.Model)} 未配置 FMax 写入命令（GetBoostLimitFrequency=0x{_commands.GetFMax:X2} 可读），不能将未配置视为硬件不支持。";
                return FMaxWriteResult.Unsupported;
            }

            var scratch = new long[MaxArgs];
            if (!TrySend(_commands.GetFMax, [0], scratch, out error))
            {
                return FMaxWriteResult.Failed;
            }

            var baseline = (int)(scratch[0] & 0xFFFF_FFFFL);
            if (baseline == 0)
            {
                // 读到 0 不能证明平台不支持：无法区分"命令不存在""通信失败""固件未实现读"，也不能作为回退基线。
                error = $"平台 {PlatformName(_commands.Family, _commands.Model)} 的 Boost Limit 基线读为 0，基线未知，拒绝写入（不代表平台不支持）。";
                return FMaxWriteResult.Failed;
            }

            Array.Clear(scratch);
            if (!TrySend(_commands.SetFMax, [limitMhz], scratch, out error))
            {
                return FMaxWriteResult.Failed;
            }

            Array.Clear(scratch);
            if (!TrySend(_commands.GetFMax, [0], scratch, out error))
            {
                return FMaxWriteResult.Failed;
            }

            var actual = (int)(scratch[0] & 0xFFFF_FFFFL);
            if (Math.Abs(actual - limitMhz) > IFMaxTuningProvider.ToleranceMhz)
            {
                error = $"FMax 回读 {actual} MHz，与写入 {limitMhz} MHz 相差超过 {IFMaxTuningProvider.ToleranceMhz} MHz（用户指定 {requested} MHz，写前基线 {baseline} MHz）。";
                return FMaxWriteResult.Failed;
            }

            return FMaxWriteResult.Applied;
        }
    }

    public (HardwareAvailability Availability, string? Detail) ProbePowerLimit()
    {
        lock (_gate)
        {
            var status = EnsureSession(out var error);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return (status.Availability, status.Detail ?? error);
            }

            if (!TryResolvePowerLimitCommands(_commands.Family, _commands.Model, out _))
            {
                return (HardwareAvailability.NotSupported,
                    $"Family {_commands.Family:X}h Model {_commands.Model:X}h 的 DPTC 命令位于 MP1 邮箱（0x4F/0x3E/0x5F），"
                    + "当前 RSMU 通道无法送达，不对该平台宣称可用。");
            }

            if (!TryReadPmTable(forceTransfer: false, out var table, out var pmError))
            {
                return (HardwareAvailability.Rejected, $"功耗表读取失败：{pmError}");
            }

            var current = DecodeCurrent(table);
            if (!current.HasAny)
            {
                return (HardwareAvailability.Rejected, $"功耗表 v0x{_pmTableVersion:X8} 内容全为 0，无法建立可回读的基线。");
            }

            return (HardwareAvailability.Available,
                $"功耗表 v0x{_pmTableVersion:X8}：当前 FAST {FormatWatts(current.FastWatts)}，"
                + $"SLOW {FormatWatts(current.SlowWatts)}，STAPM {FormatWatts(current.StapmWatts)}（只读）。"
                + "写入 FAST/SLOW 后回读校验；TDC/EDC 无回读通道。");
        }
    }

    public bool TryReadCurrentPowerLimits(out PowerLimitCurrent current, out string? error)
    {
        lock (_gate)
        {
            var status = EnsureSession(out var statusError);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                error = status.Detail ?? statusError ?? "SMU 通道不可用。";
                current = PowerLimitCurrent.Unreadable(error);
                return false;
            }

            if (!TryReadPmTable(forceTransfer: false, out var table, out error))
            {
                current = PowerLimitCurrent.Unreadable(error ?? "功耗表读取失败。");
                return false;
            }

            current = DecodeCurrent(table);
            return current.HasAny;
        }
    }

    public bool TryReadFusedPowerLimits(out FusedPowerLimits limits, out string? error)
    {
        lock (_gate)
        {
            var status = EnsureSession(out var statusError);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                error = status.Detail ?? statusError ?? "SMU 通道不可用。";
                limits = FusedPowerLimits.None(error);
                return false;
            }

            var notes = new List<string>(3);
            var ppt = ReadFusedValue(MessageGetFusedPowerLimit, "fused PPT", notes);
            var apuSlow = ReadFusedValue(MessageGetFusedApuSlowLimit, "fused APU slow", notes);
            var vddTdc = ReadFusedValue(MessageGetFusedVddTdc, "fused VDD TDC", notes);

            limits = new FusedPowerLimits(
                ppt / 1000,
                apuSlow / 1000,
                vddTdc / 1000,
                null,
                notes.Count == 0 ? "SMU 熔断值，功率已换算为 W、电流为 A。" : string.Join("；", notes));

            error = limits.HasAny ? null : limits.Detail;
            return limits.HasAny;
        }
    }

    public PowerLimitApplyResult ApplyPowerLimits(PowerLimitRequest request)
    {
        lock (_gate)
        {
            var status = EnsureSession(out var statusError);
            if (status.Availability != HardwareAvailability.Available || _session is null)
            {
                return Rejected(status.Detail ?? statusError ?? "SMU 通道不可用。");
            }

            if (!TryResolvePowerLimitCommands(_commands.Family, _commands.Model, out var commands))
            {
                return Rejected("该平台的 DPTC 命令位于 MP1 邮箱，当前 RSMU 通道无法送达。");
            }

            if (PowerLimitBounds.Validate(request) is { } invalid)
            {
                return Rejected(invalid);
            }

            if (!TryReadPmTable(forceTransfer: false, out var beforeTable, out var beforeError))
            {
                // 没有回读基线就不写：写完无法确认，等于把风险留给用户。
                return Rejected($"写前读功耗表失败：{beforeError}");
            }

            var before = DecodeCurrent(beforeTable);
            var items = new List<PowerLimitWrite>(4);

            if (request.FastWatts is int fast)
            {
                WriteLimit(commands.Fast, "fast", "FAST", fast, fast * 1000, items);
            }

            if (request.SlowWatts is int slow)
            {
                WriteLimit(commands.Slow, "slow", "SLOW", slow, slow * 1000, items);
            }

            if (request.TdcAmps is int tdc)
            {
                WriteLimit(commands.Tdc, "tdc", "TDC", tdc, tdc, items);
            }

            if (request.EdcAmps is int edc)
            {
                WriteLimit(commands.Edc, "edc", "EDC", edc, edc, items);
            }

            PowerLimitCurrent? after = null;
            if (TryReadPmTable(forceTransfer: true, out var afterTable, out _))
            {
                after = DecodeCurrent(afterTable);
                Verify(items, before, after, commands);
            }

            return new PowerLimitApplyResult(items, before, after, null, false);
        }
    }

    private static PowerLimitApplyResult Rejected(string reason) =>
        new([], null, null, reason, false);

    private void WriteLimit(uint message, string key, string label, int requested, int wireValue, List<PowerLimitWrite> items)
    {
        if (TrySend(message, [wireValue], new long[MaxArgs], out var error))
        {
            items.Add(new PowerLimitWrite(key, requested, null, PowerLimitWriteState.Accepted, $"{label} 命令已接受，等待回读。"));
            return;
        }

        items.Add(new PowerLimitWrite(key, requested, null, PowerLimitWriteState.Rejected, error));
    }

    /// <summary>
    /// FAST/SLOW 在 PM 表里有对应项，逐项判定：回读一致 → Applied；回读变了但与请求不符 → Diverged
    /// （固件折算，同时记下实测比例）；回读与写前逐字相同 → 先查是不是重复应用或折算撞值，
    /// 两者都解释不了才用探针确认通道是否响应。TDC/EDC 无回读通道，保持 Accepted。
    /// </summary>
    private void Verify(
        List<PowerLimitWrite> items,
        PowerLimitCurrent before,
        PowerLimitCurrent after,
        PowerLimitCommands commands)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.State != PowerLimitWriteState.Accepted)
            {
                continue;
            }

            var (actual, previous) = Compared(item.Item, before, after);
            if (actual is null)
            {
                continue;
            }

            var knownScale = _observedScales.TryGetValue(item.Item, out var scale) ? scale : (double?)null;
            var lastRequested = _lastRequests.TryGetValue(item.Item, out var last) ? last : (int?)null;

            var (state, reason) = PowerLimitReadBack.ClassifyDetailed(
                item.Requested, actual, previous, knownScale, lastRequested);

            string detail;
            switch (reason)
            {
                case PowerLimitReadBackReason.Applied:
                    detail = "回读一致。";
                    break;

                case PowerLimitReadBackReason.Changed:
                    RememberScaling(item.Item, item.Requested, actual.Value);
                    detail = "回读与请求不符，按固件折算后的值生效。";
                    break;

                case PowerLimitReadBackReason.Repeated:
                    detail = "与此前成功写入的请求值相同，回读未变化属重复应用。";
                    break;

                case PowerLimitReadBackReason.ScaledToSame:
                    detail = $"按本机实测折算比例约 {knownScale!.Value:P0} 计算，请求值正好落在当前生效值上，回读未变化属正常。";
                    break;

                default:
                    detail = "回读未变化，写入未生效。";
                    if (TryGetLimitMessage(commands, item.Item, out var message))
                    {
                        var probe = ProbeChannel(item.Item, message, item.Requested, actual.Value);
                        if (probe.Responded)
                        {
                            state = PowerLimitWriteState.Diverged;
                            detail = $"回读未变化，但探针写入 {probe.ProbeRequested} W 后回读变为 {probe.ProbeActual} W，"
                                + "说明写入通道正常；本次请求应是被折算后恰好等于原值。";
                        }
                        else
                        {
                            state = PowerLimitWriteState.Rejected;
                            detail = probe.Detail ?? "回读未变化，探针写入也未引起回读变化。";
                        }
                    }

                    break;
            }

            if (state is PowerLimitWriteState.Applied or PowerLimitWriteState.Diverged)
            {
                _lastRequests[item.Item] = item.Requested;
            }

            items[i] = item with { Actual = actual, State = state, Detail = detail };
        }
    }

    /// <summary>记下本机实测的折算比例（按项分开，不同项可能折算不同）；只用来解释下一次「回读没变」。</summary>
    private void RememberScaling(string item, int requested, int actual)
    {
        if (requested > 0 && actual > 0)
        {
            _observedScales[item] = (double)actual / requested;
        }
    }

    /// <summary>
    /// 回读与写前逐字相同、又找不到解释时，用相邻 1 W 的探针确认通道是否响应。
    /// 探针是真实写入（折算后差 0.8 W，PM 表按 float 记录足以观测），只在无法解释的罕见路径触发一次，
    /// 且无论探针看到什么都要把墙写回用户请求的值——否则会把墙留在探针值上。
    /// </summary>
    private ProbeOutcome ProbeChannel(string item, uint message, int requested, int unchangedActual)
    {
        var probeRequested = requested < PowerLimitBounds.MaxWatts ? requested + 1 : requested - 1;

        if (!TrySend(message, [ToWire(item, probeRequested)], new long[MaxArgs], out var writeError))
        {
            return new ProbeOutcome(false, probeRequested, null, $"探针写入 {probeRequested} 未发出：{writeError}");
        }

        int? probed = null;
        string? readError = null;
        if (TryReadPmTable(forceTransfer: true, out var probeTable, out readError))
        {
            probed = Read(item, DecodeCurrent(probeTable));
        }

        if (!TrySend(message, [ToWire(item, requested)], new long[MaxArgs], out var restoreError))
        {
            return new ProbeOutcome(false, probeRequested, probed,
                $"探针后写回 {requested} 失败：{restoreError}。功耗墙可能停在 {probeRequested} 折算后的值上，请重新应用。");
        }

        // 写回后必须强制重传并确认：PM 表不会自行刷新，不确认就会把探针的中间值留给下一次读，
        // 让下一次写入的「写前值」错位（实测表现为 fast 判成重复应用、slow 判成折算生效）。
        int? restoredTo = null;
        var restoreConfirmed = false;
        for (var attempt = 1; attempt <= 3 && !restoreConfirmed; attempt++)
        {
            if (TryReadPmTable(forceTransfer: true, out var restoredTable, out _)
                && Read(item, DecodeCurrent(restoredTable)) is int value)
            {
                restoredTo = value;
                if (value == unchangedActual)
                {
                    restoreConfirmed = true;
                    break;
                }
            }

            if (attempt < 3 && !TrySend(message, [ToWire(item, requested)], new long[MaxArgs], out _))
            {
                break;
            }
        }

        if (!restoreConfirmed)
        {
            return new ProbeOutcome(false, probeRequested, probed,
                $"探针后已写回 {requested}，但回读为 {(restoredTo?.ToString() ?? "读不到")}，"
                + $"与写前的 {unchangedActual} 不一致，请重新应用。");
        }

        if (probed is null)
        {
            return new ProbeOutcome(false, probeRequested, null, $"探针写入后读表失败：{readError}（已写回 {requested}）。");
        }

        return probed.Value == unchangedActual
            ? new ProbeOutcome(false, probeRequested, probed,
                $"探针写入 {probeRequested} 后回读仍为 {probed}，写入通道无响应（已写回 {requested}）。")
            : new ProbeOutcome(true, probeRequested, probed, null);
    }

    private static (int? Actual, int? Previous) Compared(string item, PowerLimitCurrent before, PowerLimitCurrent after) =>
        item switch
        {
            "fast" => (after.FastWatts, before.FastWatts),
            "slow" => (after.SlowWatts, before.SlowWatts),
            _ => (null, null),
        };

    private static int? Read(string item, PowerLimitCurrent current) =>
        item switch
        {
            "fast" => current.FastWatts,
            "slow" => current.SlowWatts,
            _ => null,
        };

    /// <summary>功率项 SMU 收 mW，电流项收 mA——调用方传进来的已是各自单位，这里只对功率做换算。</summary>
    private static int ToWire(string item, int value) =>
        item is "fast" or "slow" ? value * 1000 : value;

    private static bool TryGetLimitMessage(PowerLimitCommands commands, string item, out uint message)
    {
        message = item switch
        {
            "fast" => commands.Fast,
            "slow" => commands.Slow,
            _ => 0,
        };

        return message != 0;
    }

    /// <summary>
    /// PM 表前六项偏移跨表版本稳定：STAPM/FAST/SLOW 的 limit 与 value 交替，各占一个 float。
    /// 表内容由固件按需传输，读到全 0 说明尚未传输，必须发 0x3 后重读；写入后也要强制重传才看得到新值。
    /// </summary>
    private bool TryReadPmTable(bool forceTransfer, out float[] values, out string? error)
    {
        values = [];
        error = null;

        if (_session is null)
        {
            error = "SMU 会话未建立。";
            return false;
        }

        if (!TryEnterPci(out error))
        {
            return false;
        }

        try
        {
            if (!_pmTableReady)
            {
                var resolve = new long[2];
                if (!_session.Execute(FunctionResolvePmTable, [], resolve, out var resolveError))
                {
                    error = $"解析功耗表地址失败（Win32 {resolveError}）。";
                    return false;
                }

                if (resolve[1] == 0)
                {
                    error = "驱动返回的功耗表地址为 0。";
                    return false;
                }

                _pmTableVersion = (uint)resolve[0];
                _pmTableReady = true;
            }

            if (!TryReadRaw(out values, out error))
            {
                return false;
            }

            if (forceTransfer || !HasLimits(values))
            {
                if (!TryTransferWithRetry(out var transferError))
                {
                    error = transferError;
                    return false;
                }

                // 固件在传输完成后才刷新 DRAM，立刻读会拿到旧内容。
                Thread.Sleep(20);

                if (!TryReadRaw(out values, out error))
                {
                    return false;
                }
            }

            if (!HasLimits(values))
            {
                error = $"功耗表 v0x{_pmTableVersion:X8} 的 limit 字段全为 0，无法作为基线。";
                return false;
            }

            return true;
        }
        finally
        {
            ReleasePci();
        }
    }

    private bool TryReadRaw(out float[] values, out string? error)
    {
        values = [];
        error = null;

        var rows = new long[3];
        if (!_session!.Execute(FunctionReadPmTable, [], rows, out var readError))
        {
            // 地址可能因固件重载失效，下次重新解析。
            _pmTableReady = false;
            error = $"读取功耗表失败（Win32 {readError}）。";
            return false;
        }

        values =
        [
            FloatAt(rows[0], 0), FloatAt(rows[0], 1),
            FloatAt(rows[1], 0), FloatAt(rows[1], 1),
            FloatAt(rows[2], 0), FloatAt(rows[2], 1),
        ];
        return true;
    }

    /// <summary>
    /// 0x03 偶发被驱动判为「已有在途」（实测 Win32 1359），隔一小段时间重发即可成功；一次性失败不该让整帧读数作废。
    /// 传输是幂等的（把固件里的表重抄一遍到 DRAM），重试无副作用，因此可以安全重试。
    /// 调用方须已持有 PCI 互斥，不能再走 TrySend（会重复取锁）。
    /// </summary>
    private bool TryTransferWithRetry(out string? error)
    {
        const int maxAttempts = 3;

        error = null;
        for (var attempt = 1; ; attempt++)
        {
            if (TryTransferTable(out error))
            {
                return true;
            }

            if (attempt >= maxAttempts)
            {
                error = $"{error}（已重试 {maxAttempts - 1} 次）";
                return false;
            }

            Thread.Sleep(100 * attempt);
        }
    }

    /// <summary>调用方须已持有 PCI 互斥，不能再走 TrySend（会重复取锁）。</summary>
    private bool TryTransferTable(out string? error)
    {
        error = null;
        var input = new long[MaxArgs + 1];
        input[0] = MessageTransferTableToDram;

        if (!_session!.Execute(FunctionSendCommand, input, new long[MaxArgs], out var code))
        {
            error = DescribeFailure(MessageTransferTableToDram, code);
            return false;
        }

        return true;
    }

    /// <summary>
    /// limit 位置按 W 计。下界必须是 1 而不是 0：未传输时的残留位模式解出来是 1e-23 这类极小的正数，
    /// 用 <c>raw &gt; 0</c> 判断会被它骗过，舍入成 0 后当成有效基线，从此不再触发强制传输。
    /// </summary>
    private static bool HasLimits(float[] values) =>
        ToWatts(values[2]).HasValue || ToWatts(values[4]).HasValue;

    private static float FloatAt(long row, int index) =>
        BitConverter.Int32BitsToSingle((int)((row >> (index * 32)) & 0xFFFF_FFFFL));

    private PowerLimitCurrent DecodeCurrent(float[] table) => new(
        ToWatts(table[0]),
        ToWatts(table[2]),
        ToWatts(table[4]),
        $"功耗表 v0x{_pmTableVersion:X8}，功率单位 W。");

    /// <summary>低于 1 W 或高于 1000 W 视为未填充，返回 null，不返回 0。</summary>
    internal static int? ToWatts(float raw) =>
        float.IsFinite(raw) && raw >= 1 && raw <= 1000 ? (int)Math.Round(raw) : null;

    /// <summary>熔断值单位：功率 mW、电流 mA；越界视为不可信，不参与展示。</summary>
    private int? ReadFusedValue(uint message, string label, List<string> notes)
    {
        var result = new long[MaxArgs];
        if (!TrySend(message, [], result, out var failure))
        {
            notes.Add($"{label}：{failure}");
            return null;
        }

        var raw = (long)(result[0] & 0xFFFF_FFFFL);
        if (raw <= 0 || raw > 1_000_000)
        {
            notes.Add($"{label} 返回 {raw}，超出可信范围。");
            return null;
        }

        return (int)raw;
    }

    private static string FormatWatts(int? value) => value.HasValue ? $"{value} W" : "读不到";

    /// <summary>Zen4/5 的 DPTC 在 RSMU；Zen3 及更早只在 MP1，当前通道发不出去，不能当作硬件不支持。</summary>
    private static bool TryResolvePowerLimitCommands(uint family, uint model, out PowerLimitCommands commands)
    {
        commands = family switch
        {
            0x1A => new PowerLimitCommands(0x56, 0xCB, 0x57, 0x58),
            0x19 when model == 0x61 => new PowerLimitCommands(0x56, 0xCB, 0x57, 0x58),
            _ => default,
        };

        return commands.Fast != 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _probed = false;
            ReleasePci();
            _pciMutex?.Dispose();
            _pciMutex = null;
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
        _pmTableReady = false;
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

        // 这些型号的 CO 命令在 MP1 邮箱，而 ioctl_send_smu_command 只能发 RSMU；
        // 套用桌面命令号会静默不生效，因此明确报不支持，不发错命令。
        if (RequiresMp1Mailbox(family, model))
        {
            error = $"Family {family:X}h Model {model:X}h 的 CO 命令位于 MP1 邮箱，当前 RSMU 通道无法送达。";
            return new CpuTuningStatus(HardwareAvailability.NotSupported, error, vendor, PlatformName(family, model), true, false);
        }

        var modulePath = PawnIoModules.Resolve("RyzenSMU.bin", out var searched);
        if (modulePath is null)
        {
            var detail = $"未找到 PawnIO 模块，已查找：{searched}。可用 ZENTWIK_PAWNIO_MODULE 指定。";
            error = detail;
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
        if (!_coreMap.IsUsable)
        {
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.Rejected, error ?? _coreMap.Detail, vendor, PlatformName(family, model), true, false);
        }

        if (!ProbeCoreReads(out error))
        {
            DisposeSession();
            return new CpuTuningStatus(HardwareAvailability.Rejected, error, vendor, PlatformName(family, model), true, false);
        }

        _probed = true;
        return Available();
    }

    /// <summary>
    /// 映射优先取 APOB 固件表（由 ZENTWIK_APOB_TABLE 提供），否则由 CPUID 核号与 L3 缓存域推导。
    /// 两种来源都要求通过校验；都不可用时返回不可用，不退回按序号推算。
    /// </summary>
    private AmdCoreMap ResolveCoreMap(uint family, out string? error) =>
        AmdCoreMapResolver.Resolve(_info, family, out error);

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

        // 模块注释要求调用方先取得 \BaseNamedObjects\Access_PCI 互斥；进程内锁挡不住其他工具的半写。
        if (!TryEnterPci(out error))
        {
            return false;
        }

        try
        {
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
        finally
        {
            ReleasePci();
        }
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
            // 非持有线程调用：忽略，避免掩盖真正的写入结果。
        }
    }

    private static string DescribeFailure(uint message, int code)
    {
        var meaning = code switch
        {
            31 => "模块转述 SMU 返回 0xFF（命令执行失败）",
            170 => "模块转述 SMU 返回 0xFC（暂忙）",
            121 => "SMU 未在超时内应答",
            1359 => "驱动报告已有在途操作（ERROR_ALREADY_EXISTS），稍后重试可恢复",
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

    /// <summary>
    /// 已知走 MP1 邮箱的移动/APU 型号（研究仓库 implementation-references §4）。
    /// 只列有上游源码记录的型号，其余保持原有分派，避免把未列出的平台误判为不支持。
    /// </summary>
    private static bool RequiresMp1Mailbox(uint family, uint model) => family switch
    {
        0x19 => model is 0x40 or 0x44 or 0x50 or 0x60 or 0x68 or 0x74 or 0x75 or 0x78,
        0x1A => model is 0x20 or 0x24 or 0x60 or 0x70,
        _ => false,
    };

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
        _pmTableReady = false;
        _coreMap = AmdCoreMap.Empty;
    }

    private readonly record struct CoCommands(uint Family, uint Model, uint Set, uint Get, uint SetFMax, uint GetFMax);

    private readonly record struct PowerLimitCommands(uint Fast, uint Slow, uint Tdc, uint Edc);

    /// <summary>探针结果：Responded 表示通道确实响应了写入；Detail 只在失败或需要提示时非空。</summary>
    private readonly record struct ProbeOutcome(
        bool Responded,
        int ProbeRequested,
        int? ProbeActual,
        string? Detail);
}