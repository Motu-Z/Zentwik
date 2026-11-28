using System.Globalization;
using Zentwik.Domain.Ownership;
using Zentwik.Platform;
using Zentwik.Platform.Windows;

namespace Zentwik.Core.Processes;

/// <summary>
/// 一行进程的展示状态。Path/Affinity/CpuSets/CpuPercent 只对「明细范围」内的 PID 填充，
/// 范围外为 null 表示没有去查，不是读取失败。
/// </summary>
public sealed record ProcessRow(
    int Pid,
    string Name,
    string? Path,
    string Priority,
    string? Affinity,
    string? CpuSets,
    double? CpuPercent,
    string? Rule);

/// <summary>
/// 进程列表的只读查询。全表来自一次系统快照（零 OpenProcess）；
/// 路径、亲和性、CPU Sets、精确优先级只对账本持有的 PID 与用户选中的那一行单查。
/// </summary>
/// <remarks>
/// 这条边界是有意的：逐进程开句柄的开销随进程数线性增长，几百个进程每轮就是几百次系统调用，
/// 而绝大多数行用户根本不看。CPU% 不在此列——它来自快照里的累计时间差分，不需要句柄。
/// </remarks>
public sealed class ProcessQueryService(
    IProcessInventory inventory,
    IProcessScheduler scheduler,
    StateLedger ledger)
{
    private const string SchedulingPrefix = "sched.";

    public IReadOnlyList<ProcessRow> List(int? selectedPid)
    {
        var detail = HeldProcessIds();
        if (selectedPid is > 0)
        {
            detail.Add(selectedPid.Value);
        }

        var entries = inventory.Snapshot(detail);
        if (entries.Count == 0)
        {
            return [];
        }

        var cpuSetScheduler = scheduler as IProcessCpuSetScheduler;
        var rules = HeldRuleSummaries();

        var rows = new List<ProcessRow>(entries.Count);
        foreach (var entry in entries)
        {
            var priority = ProcessPriorityTokens.DescribeBasePriority(entry.BasePriority);
            string? affinity = null;
            string? cpuSets = null;

            if (detail.Contains(entry.ProcessId))
            {
                // TryRead 另开 QUERY_LIMITED 句柄读亲和性，顺带给出能区分 EcoQoS 的精确档位。
                if (scheduler.TryRead(entry.ProcessId, out var mask, out var exactPriority, out _))
                {
                    affinity = mask;
                    if (exactPriority is not null)
                    {
                        priority = exactPriority;
                    }
                }

                if (cpuSetScheduler is not null
                    && cpuSetScheduler.TryReadCpuSets(entry.ProcessId, out var sets, out _)
                    && !string.IsNullOrEmpty(sets))
                {
                    cpuSets = sets;
                }
            }

            rows.Add(new ProcessRow(
                entry.ProcessId,
                entry.Name,
                entry.ImagePath,
                priority,
                affinity,
                cpuSets,
                entry.CpuPercent,
                rules.GetValueOrDefault(entry.ProcessId)));
        }

        // 与旧版默认排序一致：占用高的在前，读不到占用的排最后，同值按名字稳定排列。
        return [.. rows
            .OrderByDescending(static row => row.CpuPercent ?? -1d)
            .ThenBy(static row => row.Name, StringComparer.CurrentCulture)
            .ThenBy(static row => row.Pid)];
    }

    private HashSet<int> HeldProcessIds()
    {
        var pids = new HashSet<int>();
        foreach (var entry in ledger.Entries)
        {
            if (StateLedger.IsHeld(entry) && TryParsePid(entry.Target, out var pid))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    private Dictionary<int, string> HeldRuleSummaries()
    {
        var parts = new Dictionary<int, List<string>>();
        foreach (var entry in ledger.Entries)
        {
            if (!StateLedger.IsHeld(entry) || !TryParsePid(entry.Target, out var pid))
            {
                continue;
            }

            var field = entry.Target[(entry.Target.LastIndexOf('.') + 1)..];
            if (entry.DesiredValue is not { } desired)
            {
                continue;
            }

            if (!parts.TryGetValue(pid, out var list))
            {
                list = [];
                parts[pid] = list;
            }

            list.Add($"{field}={desired}");
        }

        return parts.ToDictionary(
            static pair => pair.Key,
            static pair => string.Join(", ", pair.Value));
    }

    private static bool TryParsePid(string target, out int pid)
    {
        pid = 0;
        if (!target.StartsWith(SchedulingPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = target[SchedulingPrefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        return int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
    }
}
