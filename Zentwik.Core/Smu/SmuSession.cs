namespace Zentwik.Core.Smu;

/// <summary>
/// SMU 门面：测试消息、CO 读写、FMax 读写。所有命令串行执行。
/// </summary>
internal sealed class SmuSession : IDisposable
{
    private const uint MessageTest = 0x1;
    private const int MaxArgs = 6;

    private readonly object _gate = new();
    private readonly PawnIoDevice _device;
    private readonly SmuChannel _channel;
    private readonly SmuMessages _messages;

    private SmuSession(PawnIoDevice device, CpuIdentity cpu)
    {
        _device = device;
        _channel = new SmuChannel(device);
        _messages = SmuMessages.For(cpu.Family);
        Cpu = cpu;
    }

    public CpuIdentity Cpu { get; }

    public bool IsSupported => _messages.SetMargin != 0;

    public bool IsPowerLimitSupported => PowerLimitMessages.IsSupported;

    private SmuPowerLimitMessages PowerLimitMessages => SmuPowerLimitMessages.For(Cpu.FamilyId, Cpu.ModelId);

    /// <summary>PM 表地址只需解析一次，失败下次再试。</summary>
    private bool _pmTableResolved;

    public static SmuSession? TryOpen(string modulePath)
    {
        CpuIdentity cpu = CpuIdentity.Read();
        if (cpu.Family == SmuFamily.Unsupported)
            return null;

        PawnIoDevice? device = PawnIoDevice.TryLoad(modulePath);
        return device is null ? null : new SmuSession(device, cpu);
    }

    /// <summary>固件收到 arg 后回写 arg+1，据此判定邮箱真正应答。</summary>
    public bool TryTestMessage()
    {
        lock (_gate)
        {
            long[] args = [1, 0, 0, 0, 0, 0];
            return _channel.TrySend(MessageTest, args, args) && args[0] == 2;
        }
    }

    public bool TryGetVersion(out uint version)
    {
        lock (_gate)
            return _channel.TryGetVersion(out version);
    }

    public uint MakeCoreMask(uint core, uint ccd, uint ccx) => CoreMask.Encode(core, ccd, ccx, Cpu.FamilyId);

    public uint? GetMargin(uint core, uint ccd, uint ccx)
    {
        if (_messages.GetMargin == 0)
            return null;

        lock (_gate)
        {
            long[] args = [CoreMask.Encode(core, ccd, ccx, Cpu.FamilyId), 0, 0, 0, 0, 0];
            return _channel.TrySend(_messages.GetMargin, args, args) ? (uint)args[0] : null;
        }
    }

    public bool SetMargin(uint core, uint ccd, uint ccx, int margin)
    {
        if (_messages.SetMargin == 0)
            return false;

        lock (_gate)
        {
            uint mask = CoreMask.Encode(core, ccd, ccx, Cpu.FamilyId);
            long[] args = [CoreMask.WithMargin(mask, margin), 0, 0, 0, 0, 0];
            return _channel.TrySend(_messages.SetMargin, args, args);
        }
    }

    public uint GetFMax()
    {
        if (_messages.GetBoostLimit == 0)
            return 0;

        lock (_gate)
        {
            long[] args = new long[MaxArgs];
            return _channel.TrySend(_messages.GetBoostLimit, args, args) ? (uint)args[0] : 0;
        }
    }

    public bool SetFMax(uint mhz)
    {
        if (_messages.SetBoostLimit == 0)
            return false;

        lock (_gate)
        {
            long[] args = new long[MaxArgs];
            args[0] = mhz & 0xFFFFF;
            return _channel.TrySend(_messages.SetBoostLimit, args, args);
        }
    }

    /// <summary>从 PM 表读取功耗限制；表里只有限制值，时间窗读不到。</summary>
    public SmuPowerLimitSnapshot ReadPowerLimits()
    {
        SmuPowerLimitSnapshot snapshot = new() { Supported = PowerLimitMessages.IsSupported };
        if (!snapshot.Supported)
        {
            snapshot.Error = "该平台没有可用的功耗限制命令";
            return snapshot;
        }

        lock (_gate)
        {
            long[] rows = new long[64];
            if (!EnsurePmTableReady() || !_channel.TryReadPmTable(rows))
            {
                snapshot.Error = "无法读取功耗表";
                return snapshot;
            }

            snapshot.StapmMilliWatts = SmuPowerLimitCodec.FromWatts(rows[0], 0);
            snapshot.FastMilliWatts = SmuPowerLimitCodec.FromWatts(rows[1], 0);
            snapshot.SlowMilliWatts = SmuPowerLimitCodec.FromWatts(rows[2], 0);
            snapshot.Available = snapshot.HasAny;

            if (!snapshot.Available)
                snapshot.Error = "功耗表内容为空";
        }

        return snapshot;
    }

    /// <summary>写入功耗限制并回读校验；null 表示该项不动。</summary>
    public SmuPowerLimitResult ApplyPowerLimits(int? stapm, int? fast, int? slow)
    {
        SmuPowerLimitResult result = new();
        SmuPowerLimitMessages messages = PowerLimitMessages;

        if (!messages.IsSupported)
        {
            result.Failures.Add("该平台没有可用的功耗限制命令");
            return result;
        }

        lock (_gate)
        {
            if (stapm.HasValue)
                WritePowerLimit(messages.Stapm, "持续功耗", stapm.Value, result);

            if (fast.HasValue)
                WritePowerLimit(messages.Fast, "短时功耗", fast.Value, result);

            if (slow.HasValue)
                WritePowerLimit(messages.Slow, "平均功耗", slow.Value, result);

            if (result.Failures.Count > 0)
            {
                result.AllSucceeded = false;
                return result;
            }

            VerifyPowerLimits(stapm, fast, slow, result);
        }

        return result;
    }

    private void WritePowerLimit(uint message, string label, int milliWatts, SmuPowerLimitResult result)
    {
        int value = Math.Clamp(milliWatts, SmuPowerLimitCodec.MinimumMilliWatts, SmuPowerLimitCodec.MaximumMilliWatts);
        long[] args = [value, 0, 0, 0, 0, 0];
        int hr = _channel.Send(message, args, args);
        if (hr != 0)
        {
            result.Failures.Add($"{label}：{DescribeCommandFailure(hr)}");
            return;
        }

        result.Applied[label] = value;
    }

    /// <summary>PM 表由固件周期性刷新，写入后可能短暂仍是旧值。</summary>
    private void VerifyPowerLimits(int? stapm, int? fast, int? slow, SmuPowerLimitResult result)
    {
        SmuPowerLimitSnapshot actual = ReadPowerLimits();
        for (int attempt = 0; attempt < 4 && !Matches(actual, stapm, fast, slow); attempt++)
        {
            Thread.Sleep(40);
            actual = ReadPowerLimits();
        }

        result.AllSucceeded = Matches(actual, stapm, fast, slow);
        if (result.AllSucceeded)
            return;

        if (!actual.Available)
        {
            result.Failures.Add($"回读失败：{actual.Error}");
            return;
        }

        CheckOne("持续功耗", stapm, actual.StapmMilliWatts, result);
        CheckOne("短时功耗", fast, actual.FastMilliWatts, result);
        CheckOne("平均功耗", slow, actual.SlowMilliWatts, result);
    }

    private static bool Matches(SmuPowerLimitSnapshot actual, int? stapm, int? fast, int? slow)
    {
        if (!actual.Available)
            return false;

        return Near(stapm, actual.StapmMilliWatts)
            && Near(fast, actual.FastMilliWatts)
            && Near(slow, actual.SlowMilliWatts);

        static bool Near(int? expected, int? read)
        {
            if (!expected.HasValue)
                return true;

            return read.HasValue && Math.Abs(read.Value - expected.Value) <= SmuPowerLimitCodec.ReadBackTolerance;
        }
    }

    private static void CheckOne(string label, int? expected, int? read, SmuPowerLimitResult result)
    {
        if (!expected.HasValue)
            return;

        if (!read.HasValue)
        {
            result.Failures.Add($"{label}：回读值缺失");
            return;
        }

        if (Math.Abs(read.Value - expected.Value) > SmuPowerLimitCodec.ReadBackTolerance)
            result.Failures.Add($"{label}：回读 {read.Value / 1000} W，与写入 {expected.Value / 1000} W 不一致");
    }

    private bool EnsurePmTableReady()
    {
        if (_pmTableResolved)
            return true;

        _pmTableResolved = _channel.TryResolvePmTable(out _, out _);
        return _pmTableResolved;
    }

    private static string DescribeCommandFailure(int hr) => (uint)hr switch
    {
        0x8007054Fu => "固件拒绝该命令，本机可能由厂商固件接管功耗",
        0x8007000Du => "固件不认识该命令",
        0x80070032u => "该平台不支持该命令",
        0x80070015u => "设备未就绪",
        _ => $"写入失败（0x{(uint)hr:X8}）"
    };

    public void Dispose()
    {
        lock (_gate)
            _device.Dispose();
    }
}