namespace Zentwik.Core.Smu;

/// <summary>
/// PawnIO IntelMSR 模块句柄：只做 MSR 读写，所有调用串行。
/// </summary>
internal sealed class IntelMsrDevice : IDisposable
{
    private const string FunctionRead = "ioctl_read_msr";
    private const string FunctionWrite = "ioctl_write_msr";

    private readonly object _gate = new();
    private readonly PawnIoDevice _device;

    private IntelMsrDevice(PawnIoDevice device) => _device = device;

    public bool IsOpen => _device.IsOpen;

    public static IntelMsrDevice? TryOpen(string modulePath)
    {
        PawnIoDevice? device = PawnIoDevice.TryLoad(modulePath);
        return device is null ? null : new IntelMsrDevice(device);
    }

    public bool TryRead(uint msr, out ulong value)
    {
        lock (_gate)
        {
            long[] input = [msr];
            long[] output = new long[1];
            int hr = _device.Execute(FunctionRead, input, output, out int written);
            value = written > 0 ? unchecked((ulong)output[0]) : 0UL;
            return hr == 0 && written > 0;
        }
    }

    public bool TryWrite(uint msr, ulong value)
    {
        lock (_gate)
        {
            long[] input = [unchecked((long)msr), unchecked((long)value)];
            int hr = _device.Execute(FunctionWrite, input, [], out _);
            return hr == 0;
        }
    }

    public void Dispose() => _device.Dispose();
}
