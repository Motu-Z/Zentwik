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

    public void Dispose()
    {
        lock (_gate)
            _device.Dispose();
    }
}
