namespace Zentwik.Platform.Windows;

/// <summary>
/// 逐逻辑处理器的占用率换算。<c>KernelTime</c> 含 <c>IdleTime</c>，占用率必须先把空闲减掉，
/// 否则每个核都会显示成"至少一半在忙"。
/// </summary>
internal static class ProcessorUsage
{
    /// <summary>三个入参都是两次采样之间的 100 ns 计数差。</summary>
    public static double Percent(long idleDelta, long kernelDelta, long userDelta)
    {
        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return 0d;
        }

        var busy = total - idleDelta;
        return Math.Clamp(busy * 100d / total, 0d, 100d);
    }
}

/// <summary>
/// 逐逻辑处理器占用率。<c>NtQuerySystemInformation(SystemProcessorPerformanceInformation)</c>
/// 一次返回整张表，不需要绑核，也不需要遍历进程。
/// </summary>
internal sealed class ProcessorLoadReader
{
    private Native.ProcessorPerformance[] _current = [];
    private Native.ProcessorPerformance[] _previous = [];
    private bool _hasBaseline;

    /// <summary>
    /// 每个逻辑处理器的占用率（0–100，索引即处理器序号）。首帧、调用失败或处理器数变化时返回 null，
    /// 调用方不得用 0 填充——那会被读成"全部空闲"。
    /// </summary>
    public unsafe double[]? Read()
    {
        var count = Environment.ProcessorCount;
        if (count <= 0)
        {
            return null;
        }

        if (_current.Length != count)
        {
            _current = new Native.ProcessorPerformance[count];
            _previous = new Native.ProcessorPerformance[count];
            _hasBaseline = false;
        }

        var length = (uint)(count * sizeof(Native.ProcessorPerformance));
        int status;
        fixed (Native.ProcessorPerformance* buffer = _current)
        {
            status = Native.NtQuerySystemInformation(
                Native.SystemProcessorPerformanceInformation,
                (nint)buffer,
                length,
                out _);
        }

        if (!Native.IsSuccess(status))
        {
            // 缓冲区与实际处理器数不符（热添加）：丢掉快照，下一拍按新数量重建。
            if (status == Native.StatusInfoLengthMismatch)
            {
                _current = [];
                _hasBaseline = false;
            }

            return null;
        }

        if (!_hasBaseline)
        {
            (_previous, _current) = (_current, _previous);
            _hasBaseline = true;
            return null;
        }

        var result = new double[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = ProcessorUsage.Percent(
                _current[index].IdleTime - _previous[index].IdleTime,
                _current[index].KernelTime - _previous[index].KernelTime,
                _current[index].UserTime - _previous[index].UserTime);
        }

        (_previous, _current) = (_current, _previous);
        return result;
    }
}
