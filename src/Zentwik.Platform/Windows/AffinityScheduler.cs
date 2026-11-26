using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Zentwik.Domain.Plans;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

public sealed class AffinityScheduler : IProcessScheduler, IProcessCpuSetScheduler
{
    public IReadOnlyList<ProcessTarget> Find(string name)
    {
        var normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;

        var results = new List<ProcessTarget>();
        foreach (var process in Process.GetProcessesByName(normalized))
        {
            using (process)
            {
                results.Add(new ProcessTarget(process.Id, process.ProcessName));
            }
        }

        return results;
    }

    public bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error)
    {
        cpuMask = null;
        priority = null;

        try
        {
            using var process = Process.GetProcessById(processId);
            cpuMask = $"0x{process.ProcessorAffinity.ToInt64():X}";
            priority = process.PriorityClass.ToString();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryApply(int processId, string? cpuMask, string? priority, out string? error)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            if (cpuMask is not null)
            {
                if (!TryParseMask(cpuMask, out var mask))
                {
                    error = $"无法解析 CPU 掩码：{cpuMask}。";
                    return false;
                }

                process.ProcessorAffinity = mask;
            }

            if (priority is not null)
            {
                if (!Enum.TryParse<ProcessPriorityClass>(priority, ignoreCase: true, out var priorityClass))
                {
                    error = $"未知进程优先级：{priority}。";
                    return false;
                }

                process.PriorityClass = priorityClass;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool IsCpuSetsSupported(out string? detail)
    {
        if (!CpuSetsNative.TryGetSystemCpuSetMap(out var logicalToId, out _, out var error))
        {
            detail = error;
            return false;
        }

        detail = $"Windows CPU Sets 可用，共 {logicalToId.Count} 个逻辑处理器集合。";
        return true;
    }

    public bool TryReadCpuSets(int processId, out string? cpuSets, out string? error)
    {
        cpuSets = null;
        if (!CpuSetsNative.TryGetSystemCpuSetMap(out _, out var idToLogical, out error))
        {
            return false;
        }

        var handle = CpuSetsNative.OpenProcess(CpuSetsNative.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            error = $"打开进程 {processId} 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }

        try
        {
            if (!CpuSetsNative.GetProcessDefaultCpuSets(handle, null, 0, out var required))
            {
                var code = Marshal.GetLastWin32Error();
                if (code != 122) // ERROR_INSUFFICIENT_BUFFER
                {
                    error = $"读取进程 {processId} CPU Sets 失败，Win32 错误码 {code}。";
                    return false;
                }
            }

            if (required == 0)
            {
                cpuSets = string.Empty;
                error = null;
                return true;
            }

            var ids = new uint[required];
            if (!CpuSetsNative.GetProcessDefaultCpuSets(handle, ids, required, out _))
            {
                error = $"读取进程 {processId} CPU Sets 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            var logical = ids
                .Where(idToLogical.ContainsKey)
                .Select(id => idToLogical[id])
                .ToArray();
            cpuSets = CpuSetSpec.Format(logical);
            error = null;
            return true;
        }
        finally
        {
            CpuSetsNative.CloseHandle(handle);
        }
    }

    public bool TryApplyCpuSets(int processId, string? cpuSets, out string? error)
    {
        if (!CpuSetsNative.TryGetSystemCpuSetMap(out var logicalToId, out _, out error))
        {
            return false;
        }

        uint[]? ids = null;
        if (!string.IsNullOrWhiteSpace(cpuSets))
        {
            if (!CpuSetSpec.TryParse(cpuSets, out var requested, out error)
                || !CpuSetSpec.ValidateAndResolve(requested, logicalToId, out ids, out error))
            {
                return false;
            }
        }

        var handle = CpuSetsNative.OpenProcess(
            CpuSetsNative.ProcessQueryLimitedInformation | CpuSetsNative.ProcessSetLimitedInformation,
            false,
            processId);
        if (handle == 0)
        {
            error = $"打开进程 {processId} 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }

        try
        {
            var count = (uint)(ids?.Length ?? 0);
            if (!CpuSetsNative.SetProcessDefaultCpuSets(handle, ids, count))
            {
                error = $"设置进程 {processId} CPU Sets 失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            error = null;
            return true;
        }
        finally
        {
            CpuSetsNative.CloseHandle(handle);
        }
    }

    private static bool TryParseMask(string text, out nint mask)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                mask = unchecked((nint)(long)hex);
                return mask != 0;
            }
        }
        else if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
        {
            mask = unchecked((nint)(long)dec);
            return mask != 0;
        }

        mask = 0;
        return false;
    }
}