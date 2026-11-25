using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

/// <summary>亲和性与优先级只作用于已有进程；跨处理器组时亲和性会被系统忽略。</summary>
public sealed class AffinityScheduler : IProcessScheduler
{
    public IReadOnlyList<ProcessTarget> Find(string name)
    {
        var normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;

        var targets = new List<ProcessTarget>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (string.Equals(process.ProcessName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(new ProcessTarget(process.Id, process.ProcessName));
                }
            }
        }

        return targets;
    }

    public bool TryRead(int processId, out string? cpuMask, out string? priority, out string? error)
    {
        cpuMask = null;
        priority = null;
        error = null;

        try
        {
            using var process = Process.GetProcessById(processId);
            cpuMask = $"0x{(long)process.ProcessorAffinity:X}";
            priority = process.PriorityClass.ToString();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryApply(int processId, string? cpuMask, string? priority, out string? error)
    {
        error = null;
        if (cpuMask is null && priority is null)
        {
            return true;
        }

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
                if (!Enum.TryParse<ProcessPriorityClass>(priority, ignoreCase: true, out var level))
                {
                    error = $"未知优先级：{priority}。";
                    return false;
                }

                process.PriorityClass = level;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseMask(string text, out nint mask)
    {
        mask = 0;
        var span = text.AsSpan().Trim();
        var hex = span.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
        {
            span = span[2..];
        }

        var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!ulong.TryParse(span, style, CultureInfo.InvariantCulture, out var value) || value == 0)
        {
            return false;
        }

        mask = (nint)(long)value;
        return true;
    }
}
