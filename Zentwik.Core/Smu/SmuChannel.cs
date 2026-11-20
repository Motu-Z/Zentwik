namespace Zentwik.Core.Smu;

/// <summary>
/// RSMU 命令通道。驱动模块内部完成邮箱握手与超时，驱动不支持的邮箱无法经此发送。
/// </summary>
internal sealed class SmuChannel(PawnIoDevice device)
{
    private const string IoctlSendCommand = "ioctl_send_smu_command";
    private const string IoctlGetVersion = "ioctl_get_smu_version";
    private const string IoctlResolvePmTable = "ioctl_resolve_pm_table";
    private const string IoctlReadPmTable = "ioctl_read_pm_table";
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
        => Send(message, args, result) == 0;

    /// <summary>同上，但保留驱动返回的 HRESULT，用于区分「拒绝」与「命令不存在」。</summary>
    public int Send(uint message, ReadOnlySpan<long> args, Span<long> result)
    {
        long[] input = new long[MaxArgs + 1];
        input[0] = message;

        int argCount = Math.Min(MaxArgs, args.Length);
        for (int i = 0; i < argCount; i++)
            input[i + 1] = args[i];

        long[] output = new long[MaxArgs];
        int hr = device.Execute(IoctlSendCommand, input, output, out int written);
        if (hr != 0)
        {
            result.Clear();
            return hr;
        }

        int copy = Math.Min(result.Length, written == 0 ? MaxArgs : written);
        for (int i = 0; i < copy; i++)
            result[i] = output[i];

        return 0;
    }

    /// <summary>解析 PM 表在 DRAM 中的位置；只需成功一次。</summary>
    public bool TryResolvePmTable(out uint version, out ulong baseAddress)
    {
        version = 0;
        baseAddress = 0;

        long[] output = new long[2];
        if (device.Execute(IoctlResolvePmTable, [], output, out int written) != 0 || written < 2)
            return false;

        version = (uint)output[0];
        baseAddress = (ulong)output[1];
        return baseAddress != 0;
    }

    /// <summary>读取 PM 表前若干项（每项 8 字节，对应两个 float）。</summary>
    public bool TryReadPmTable(Span<long> output)
    {
        if (output.Length == 0)
            return false;

        long[] buffer = new long[output.Length];
        if (device.Execute(IoctlReadPmTable, [], buffer, out int written) != 0 || written < 3)
            return false;

        buffer.AsSpan(0, Math.Min(written, output.Length)).CopyTo(output);
        return true;
    }
}
