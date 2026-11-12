namespace Zentwik.Core.Smu;

/// <summary>
/// RSMU 命令通道。驱动模块内部完成邮箱握手与超时，驱动不支持的邮箱无法经此发送。
/// </summary>
internal sealed class SmuChannel(PawnIoDevice device)
{
    private const string IoctlSendCommand = "ioctl_send_smu_command";
    private const string IoctlGetVersion = "ioctl_get_smu_version";
    private const int MaxArgs = 6;

    public bool TryGetVersion(out uint version)
    {
        version = 0;
        long[] output = new long[1];
        if (device.Execute(IoctlGetVersion, [], output, out int written) != 0 || written < 1)
            return false;

        version = (uint)output[0];
        return true;
    }

    /// <summary>发送命令并取回固件写回的参数；失败时清空 result。</summary>
    public bool TrySend(uint message, ReadOnlySpan<long> args, Span<long> result)
    {
        long[] input = new long[MaxArgs + 1];
        input[0] = message;

        int argCount = Math.Min(MaxArgs, args.Length);
        for (int i = 0; i < argCount; i++)
            input[i + 1] = args[i];

        long[] output = new long[MaxArgs];
        if (device.Execute(IoctlSendCommand, input, output, out int written) != 0)
        {
            result.Clear();
            return false;
        }

        int copy = Math.Min(result.Length, written == 0 ? MaxArgs : written);
        for (int i = 0; i < copy; i++)
            result[i] = output[i];

        return true;
    }
}
