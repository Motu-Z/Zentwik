using System.Globalization;
using System.Text.Json;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Power;
using Zentwik.Domain.Scenes;
using Zentwik.Domain.Settings;

namespace Zentwik.Cli;

public sealed class CliApp(AgentConnection connection, CommandLine args, Output output)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var command = args.Positionals.Count > 0 ? args.Positionals[0] : "status";
        return command switch
        {
            "status" => await StatusAsync(ct),
            "caps" or "capabilities" => await CapabilitiesAsync(ct),
            "modules" => await ModulesAsync(ct),
            "monitor" or "watch" => await MonitorAsync(ct),
            "tuning" => await TuningAsync(ct),
            "plans" => await PlansAsync(ct),
            "scenes" => await ScenesAsync(ct),
            "ledger" => await LedgerAsync(ct),
            "power" => await PowerAsync(ct),
            "powerlimit" => await PowerLimitAsync(ct),
            "settings" => await SettingsAsync(ct),
            "doctor" => await DoctorAsync(ct),
            "logs" => await LogsAsync(ct),
            _ => throw new CliUsageException($"未知命令：{command}"),
        };
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var status = await connection.GetStatusAsync(ct);
        var capabilities = await connection.ListCapabilitiesAsync(ct);

        output.JsonValue(status, ZentwikJson.Default.AgentStatusDto);
        if (output.Json)
        {
            return 0;
        }

        Console.WriteLine($"agent       {status.Version}  protocol v{status.ProtocolVersion}");
        Console.WriteLine($"machine     {status.MachineName}  elevated={Output.Status(status.Elevated)}  service={Output.Status(status.RunningAsService)}  simulated={Output.Status(status.Simulated)}");
        Console.WriteLine($"processors  {status.PhysicalCoreCount} cores / {status.ProcessorCount} threads  groups={status.ProcessorGroups}");
        Console.WriteLine($"uptime      {TimeSpan.FromSeconds(status.UptimeSeconds):hh\\:mm\\:ss}");
        Console.WriteLine($"data        {status.DataDirectory}");
        Console.WriteLine($"plans       {status.PlanCount}  scenes {status.SceneCount}  ledger {status.LedgerEntryCount}");
        Console.WriteLine($"scene       {status.ActiveSceneId ?? "(none)"}");
        Console.WriteLine();

        foreach (var capability in capabilities)
        {
            Console.WriteLine($"  {capability.Id,-28} {capability.State,-12} {capability.Detail}");
        }

        var usable = capabilities.Count(static capability => capability.State is "running" or "enabled" or "degraded");
        Console.WriteLine();
        Console.WriteLine($"usable capabilities: {usable}/{capabilities.Count}");
        return usable > 0 ? ExitCodes.Ok : ExitCodes.Unavailable;
    }

    private async Task<int> CapabilitiesAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var capabilities = await connection.ListCapabilitiesAsync(ct);
        output.JsonValue(capabilities.ToArray(), ZentwikJson.Default.CapabilityDtoArray);

        if (!output.Json)
        {
            foreach (var capability in capabilities)
            {
                Console.WriteLine($"{capability.Id}\t{capability.State}\t{capability.Detail}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> ModulesAsync(CancellationToken ct)
    {
        var sub = args.Positionals.Count > 1 ? args.Positionals[1] : "list";
        switch (sub)
        {
            case "list":
            {
                args.EnsureOnly("--pipe");
                PrintModules(await connection.ListModulesAsync(ct));
                return ExitCodes.Ok;
            }

            case "enable":
            case "disable":
            {
                args.EnsureOnly("--pipe");
                var id = args.Positional(2, "模块");
                var modules = sub == "enable"
                    ? await connection.EnableModuleAsync(id, ct)
                    : await connection.DisableModuleAsync(id, ct);
                PrintModules(modules);
                return ExitCodes.Ok;
            }

            default:
                throw new CliUsageException($"未知子命令：modules {sub}");
        }
    }

    private void PrintModules(IReadOnlyList<ModuleDto> modules)
    {
        output.JsonValue(modules.ToArray(), ZentwikJson.Default.ModuleDtoArray);

        if (output.Json)
        {
            return;
        }

        foreach (var module in modules)
        {
            var suffix = module.Optional ? (module.Enabled ? "  [可选·已启用]" : "  [可选·未启用]") : string.Empty;
            Console.WriteLine($"{module.Id,-16} {module.State,-12} {module.Name}{suffix}");
            if (!string.IsNullOrWhiteSpace(module.Detail))
            {
                Console.WriteLine($"  {module.Detail}");
            }
        }
    }

    private async Task<int> MonitorAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--count", "--interval", "--sensor-interval", "--clocks");
        var count = ParseInt(args.Value("--count"), 5, 1, 100000);
        var showClocks = args.Flag("--clocks");

        // 不传就用设置里的默认值：间隔是用户偏好，不该在每个命令上都重复指定。
        var interval = args.Value("--interval") is null
            ? (int?)null
            : ParseInt(args.Value("--interval"), 1000, UserSettingsRules.MinMonitorIntervalMs, UserSettingsRules.MaxMonitorIntervalMs);
        var sensorInterval = args.Value("--sensor-interval") is null
            ? (int?)null
            : ParseInt(args.Value("--sensor-interval"), 1000, UserSettingsRules.MinSensorIntervalMs, UserSettingsRules.MaxSensorIntervalMs);

        var received = 0;
        var lastSensorNote = string.Empty;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.EventReceived += envelope =>
        {
            if (envelope.Op != Events.TelemetrySample || envelope.Data is not { } data)
            {
                return;
            }

            var sample = data.Deserialize(ZentwikJson.Default.TelemetryDto);
            if (sample is null)
            {
                return;
            }

            if (output.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(sample, ZentwikJson.Default.TelemetryDto));
            }
            else
            {
                var memoryUsed = sample.MemoryTotalBytes == 0
                    ? 0d
                    : (1d - (double)sample.MemoryAvailableBytes / sample.MemoryTotalBytes) * 100d;
                var temperature = sample.TemperatureCelsius is { } celsius ? $"  temp {celsius,5:0.0}C" : string.Empty;
                var power = sample.PackagePowerWatts is { } watts ? $"  pkg {watts,6:0.0}W" : string.Empty;
                var clock = DescribeClocks(sample.CoreClocks);
                Console.WriteLine(
                    $"{sample.Timestamp:HH:mm:ss.fff}  cpu {sample.CpuTotalPercent,5:0.0}%  mem {memoryUsed,5:0.0}%  proc {sample.ProcessCount,4}  cores {sample.ProcessorCount}{temperature}{power}{clock}");

                if (showClocks && sample.CoreClocks is { Count: > 0 } clocks)
                {
                    foreach (var group in clocks.GroupBy(static item => item.Ccd).OrderBy(static item => item.Key))
                    {
                        var values = string.Join(' ', group.OrderBy(static item => item.Core)
                            .Select(static item => $"c{item.Core}:{item.Megahertz:0}"));
                        Console.WriteLine($"  ccd{group.Key} {values}");
                    }
                }

                // 传感器不可用与首帧缺基线都走这里，同一句只提示一次。
                if (!string.IsNullOrWhiteSpace(sample.SensorDetail)
                    && !string.Equals(sample.SensorDetail, lastSensorNote, StringComparison.Ordinal))
                {
                    Console.WriteLine($"  sensors: {sample.SensorDetail}");
                    lastSensorNote = sample.SensorDetail;
                }
            }

            if (Interlocked.Increment(ref received) >= count)
            {
                done.TrySetResult();
            }
        };

        var ack = await connection.SubscribeTelemetryAsync(interval, sensorInterval, ct);
        output.Line($"subscribed interval={ack.IntervalMs}ms sensors={ack.SensorIntervalMs}ms, expecting {count} samples");

        // 按服务端实际生效的间隔算超时：请求的间隔可能被抬到下限，用请求值会误报超时。
        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(count * (ack.IntervalMs / 1000d) + 5), ct));
        if (completed != done.Task)
        {
            output.Error("monitor.timeout", $"只收到 {received}/{count} 个样本。");
            return ExitCodes.Timeout;
        }

        await connection.RequestAsync(Ops.TelemetryUnsubscribe, ct);
        return ExitCodes.Ok;
    }

    private async Task<int> TuningAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--intel");
        return args.Flag("--intel")
            ? await IntelTuningAsync(ct)
            : await AmdTuningAsync(ct);
    }

    private async Task<int> IntelTuningAsync(CancellationToken ct)
    {
        var snapshot = await connection.ReadIntelTuningAsync(ct);
        output.JsonValue(snapshot, ZentwikJson.Default.IntelTuningSnapshotDto);

        if (output.Json)
        {
            return snapshot.Availability == "available" ? ExitCodes.Ok : ExitCodes.Unavailable;
        }

        Console.WriteLine($"availability  {snapshot.Availability}");
        Console.WriteLine($"platform      {snapshot.Platform ?? "-"}  vendor={snapshot.Vendor ?? "-"}  simulated={Output.Status(snapshot.Simulated)}");
        Console.WriteLine($"reboot        {(snapshot.PersistsAcrossReboot ? "写入在重启后保持" : "仅当前会话有效")}");
        Console.WriteLine($"detail        {snapshot.Detail}");

        if (snapshot.VoltageOffsetMillivolts is { } offset)
        {
            Console.WriteLine();
            Console.WriteLine($"voltage offset   {offset,4} mV");
            Console.WriteLine($"turbo ratio      1C {snapshot.TurboRatio1Core}x / 8C {snapshot.TurboRatio8Cores}x");
        }

        return snapshot.Availability == "available" ? ExitCodes.Ok : ExitCodes.Unavailable;
    }

    private async Task<int> AmdTuningAsync(CancellationToken ct)
    {
        var snapshot = await connection.ReadTuningAsync(ct);
        output.JsonValue(snapshot, ZentwikJson.Default.TuningSnapshotDto);

        if (output.Json)
        {
            return snapshot.Availability == "available" ? ExitCodes.Ok : ExitCodes.Unavailable;
        }

        Console.WriteLine($"availability  {snapshot.Availability}");
        Console.WriteLine($"platform      {snapshot.Platform ?? "-"}  vendor={snapshot.Vendor ?? "-"}  simulated={Output.Status(snapshot.Simulated)}");
        Console.WriteLine($"reboot        {(snapshot.PersistsAcrossReboot ? "写入在重启后保持" : "仅当前会话有效")}");
        Console.WriteLine($"detail        {snapshot.Detail}");

        if (snapshot.Cores.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("core    co");
            foreach (var core in snapshot.Cores)
            {
                Console.WriteLine($"  {core.Core,4}  {core.Offset,4}");
            }
        }

        return snapshot.Availability == "available" ? ExitCodes.Ok : ExitCodes.Unavailable;
    }

    private async Task<int> PowerLimitAsync(CancellationToken ct)
    {
        var sub = args.Positionals.Count > 1 ? args.Positionals[1] : "get";
        return sub switch
        {
            "get" or "show" => await PowerLimitGetAsync(ct),
            "set" or "apply" => await PowerLimitSetAsync(ct),
            _ => throw new CliUsageException($"未知子命令：powerlimit {sub}"),
        };
    }

    private async Task<int> PowerLimitGetAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var state = await connection.ReadPowerLimitsAsync(ct);
        output.JsonValue(state, ZentwikJson.Default.PowerLimitStateDto);

        if (output.Json)
        {
            return ExitCodes.Ok;
        }

        if (!state.Supported)
        {
            Console.WriteLine("unsupported  该平台未提供可用的功耗命令");
            if (state.Error is { } unsupported)
            {
                Console.WriteLine($"  {unsupported}");
            }

            return ExitCodes.Failed;
        }

        Console.WriteLine($"current      FAST {FormatLimit(state.FastWatts, "W")}  SLOW {FormatLimit(state.SlowWatts, "W")}  STAPM {FormatLimit(state.StapmWatts, "W")}");
        Console.WriteLine($"fused        FAST {FormatLimit(state.FusedFastWatts, "W")}  SLOW {FormatLimit(state.FusedSlowWatts, "W")}  TDC {FormatLimit(state.FusedTdcAmps, "A")}");

        if (state.Error is { } error)
        {
            Console.WriteLine($"  {error}");
        }

        return state.Available ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private async Task<int> PowerLimitSetAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--fast", "--slow", "--tdc", "--edc");
        var fast = OptionalLimit("--fast", PowerLimitBounds.MinWatts, PowerLimitBounds.MaxWatts, "W");
        var slow = OptionalLimit("--slow", PowerLimitBounds.MinWatts, PowerLimitBounds.MaxWatts, "W");
        var tdc = OptionalLimit("--tdc", PowerLimitBounds.MinAmps, PowerLimitBounds.MaxAmps, "A");
        var edc = OptionalLimit("--edc", PowerLimitBounds.MinAmps, PowerLimitBounds.MaxAmps, "A");

        if (fast is null && slow is null && tdc is null && edc is null)
        {
            throw new CliUsageException("powerlimit set 需要至少一项：--fast / --slow / --tdc / --edc。");
        }

        var result = await connection.ApplyPowerLimitsAsync(new PowerLimitApplyRequest(fast, slow, tdc, edc), ct);
        output.JsonValue(result, ZentwikJson.Default.PowerLimitApplyDto);

        if (output.Json)
        {
            return ExitCodes.Ok;
        }

        foreach (var item in result.Items)
        {
            var unit = item.Item is "tdc" or "edc" ? "A" : "W";
            var readback = item.Actual is int actual ? $"实际 {actual} {unit}" : "无回读通道";
            Console.WriteLine($"{item.Item,-5} {item.State,-9} 请求 {item.Requested} {unit}  {readback,-14} {item.Detail}");
        }

        var rejected = result.Items.Count(static item => item.State == "rejected");
        var diverged = result.Items.Count(static item => item.State == "diverged");
        Console.WriteLine($"result       applied {result.Items.Count(static item => item.State == "applied")}，"
            + $"diverged {diverged}，accepted {result.Items.Count(static item => item.State == "accepted")}，rejected {rejected}");

        if (result.Note is { } note)
        {
            Console.WriteLine();
            Console.WriteLine($"note         {note}");
        }

        return rejected > 0 ? ExitCodes.Failed : diverged > 0 ? ExitCodes.Partial : ExitCodes.Ok;
    }

    private int? OptionalLimit(string name, int min, int max, string unit)
    {
        var text = args.Value(name);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new CliUsageException($"{name} 需要整数，收到 {text}。");
        }

        if (value < min || value > max)
        {
            throw new CliUsageException($"{name} 超出 [{min}, {max}] {unit}。");
        }

        return value;
    }

    private static string FormatLimit(int? value, string unit) => value.HasValue ? $"{value} {unit}" : "-";

    private async Task<int> PowerAsync(CancellationToken ct)
    {
        var sub = args.Positionals.Count > 1 ? args.Positionals[1] : "list";
        return sub switch
        {
            "list" or "schemes" => await PowerListAsync(ct),
            "create" => await PowerCreateAsync(ct),
            "rename" => await PowerRenameAsync(ct),
            "delete" => await PowerDeleteAsync(ct),
            "backup" => await PowerBackupAsync(ct),
            "backups" => await PowerBackupsAsync(ct),
            "restore" => await PowerRestoreAsync(ct),
            _ => throw new CliUsageException($"未知子命令：power {sub}"),
        };
    }

    private async Task<int> PowerListAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var schemes = await connection.ListPowerSchemesAsync(ct);
        output.JsonValue(schemes.ToArray(), ZentwikJson.Default.PowerSchemeDtoArray);

        if (!output.Json)
        {
            foreach (var scheme in schemes)
            {
                var flags = string.Join(' ', new[]
                {
                    scheme.Active ? "active" : string.Empty,
                    scheme.BuiltIn ? "builtin" : string.Empty,
                    scheme.Visible ? string.Empty : "hidden",
                }.Where(static flag => flag.Length > 0));
                Console.WriteLine($"{scheme.Guid:D}  {scheme.Name,-24} {flags}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PowerCreateAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--template", "--name");
        var templateText = args.Value("--template")
            ?? throw new CliUsageException("power create 需要 --template <GUID|balanced|saver|performance|ultimate>。");
        var name = args.Value("--name")
            ?? throw new CliUsageException("power create 需要 --name <名称>。");

        var created = await connection.CreatePowerSchemeAsync(ResolveTemplate(templateText), name, ct);
        output.JsonValue(created, ZentwikJson.Default.PowerSchemeDto);

        if (!output.Json)
        {
            Console.WriteLine($"created {created.Guid:D} {created.Name}");
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PowerRenameAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--id", "--name");
        var id = RequireGuid("--id");
        var name = args.Value("--name")
            ?? throw new CliUsageException("power rename 需要 --name <名称>。");

        var renamed = await connection.RenamePowerSchemeAsync(id, name, ct);
        output.JsonValue(renamed, ZentwikJson.Default.PowerSchemeDto);

        if (!output.Json)
        {
            Console.WriteLine($"renamed {renamed.Guid:D} {renamed.Name}");
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PowerDeleteAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--id", "--no-backup");
        var id = RequireGuid("--id");
        var keepBackup = !args.Flag("--no-backup");

        var result = await connection.DeletePowerSchemeAsync(id, keepBackup, ct);
        output.JsonValue(result, ZentwikJson.Default.PowerSchemeDeleteResultDto);

        if (!output.Json)
        {
            Console.WriteLine($"deleted {result.Guid:D}");
            Console.WriteLine(result.Backup is { } backup
                ? $"  backup  {backup.Path}"
                : "  backup  (未留备份)");
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PowerBackupAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--id");
        var id = RequireGuid("--id");

        var backup = await connection.BackupPowerSchemeAsync(id, ct);
        output.JsonValue(backup, ZentwikJson.Default.PowerSchemeBackupDto);

        if (!output.Json)
        {
            Console.WriteLine($"backup {backup.Id}");
            Console.WriteLine($"  settings  {backup.SettingCount}{(backup.UnreadableCount > 0 ? $"（另有 {backup.UnreadableCount} 项读不到，未记录）" : string.Empty)}");
            Console.WriteLine($"  manifest  {backup.Path}");
            Console.WriteLine($"  portable  {backup.PortablePath ?? "(未生成 .pow)"}");
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PowerBackupsAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var backups = await connection.ListPowerSchemeBackupsAsync(ct);
        output.JsonValue(backups.ToArray(), ZentwikJson.Default.PowerSchemeBackupDtoArray);

        if (!output.Json)
        {
            if (backups.Count == 0)
            {
                Console.WriteLine("没有备份。");
                return ExitCodes.Ok;
            }

            foreach (var backup in backups)
            {
                var flags = string.Join(' ', new[]
                {
                    backup.Visible is false ? "hidden" : string.Empty,
                    backup.PortablePath is null ? "no-pow" : string.Empty,
                }.Where(static flag => flag.Length > 0));

                Console.WriteLine($"{backup.CreatedAt:yyyy-MM-dd HH:mm:ss}  {backup.Name,-24} {backup.SettingCount,4} 项  {backup.Id}  {flags}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PowerRestoreAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--backup");
        var backup = args.Value("--backup")
            ?? throw new CliUsageException("power restore 需要 --backup <备份 ID|文件路径>。");

        var result = await connection.RestorePowerSchemeAsync(backup, ct);
        output.JsonValue(result, ZentwikJson.Default.PowerSchemeRestoreResultDto);

        if (!output.Json)
        {
            Console.WriteLine($"restored {result.Scheme.Guid:D} {result.Scheme.Name}");
            Console.WriteLine($"  settings  {result.Applied} 项写入成功，{result.Failed} 项失败");

            // 只有原方案是隐藏的才有歧义：可见方案恢复出来必然可见，不必赘述。
            if (result.SourceVisible is false)
            {
                Console.WriteLine(result.Scheme.Visible
                    ? "  visible   原方案为隐藏方案；恢复结果为可见方案（可见性由系统决定，无法还原）"
                    : "  visible   原方案为隐藏方案；恢复结果同为隐藏方案");
            }

            foreach (var failure in result.Failures)
            {
                Console.WriteLine($"  ! {failure}");
            }
        }

        return result.Failed == 0 ? ExitCodes.Ok : ExitCodes.Partial;
    }

    private Guid RequireGuid(string option)
    {
        var text = args.Value(option) ?? throw new CliUsageException($"需要 {option} <GUID>。");
        return Guid.TryParse(text, out var guid)
            ? guid
            : throw new CliUsageException($"{option} 需要合法 GUID。");
    }

    private static Guid ResolveTemplate(string text) => text.ToLowerInvariant() switch
    {
        "balanced" or "平衡" => PowerSchemePresets.Balanced.Guid,
        "saver" or "powersaver" or "节能" => PowerSchemePresets.PowerSaver.Guid,
        "performance" or "high" or "高性能" => PowerSchemePresets.HighPerformance.Guid,
        "ultimate" or "卓越性能" => PowerSchemePresets.UltimatePerformance.Guid,
        _ => Guid.TryParse(text, out var guid)
            ? guid
            : throw new CliUsageException($"无法识别的模板：{text}。"),
    };

    private async Task<int> PlansAsync(CancellationToken ct)
    {
        var sub = args.Positionals.Count > 1 ? args.Positionals[1] : "list";
        return sub switch
        {
            "list" => await PlanListAsync(ct),
            "show" => await PlanShowAsync(ct),
            "apply" => await PlanApplyAsync(ct),
            "revert" => await PlanRevertAsync(ct),
            "delete" => await PlanDeleteAsync(ct),
            "save" => await PlanSaveAsync(ct),
            "create" => await PlanCreateAsync(ct),
            _ => throw new CliUsageException($"未知子命令：plans {sub}"),
        };
    }

    private async Task<int> PlanListAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var plans = await connection.ListPlansAsync(ct);
        output.JsonValue(plans.ToArray(), ZentwikJson.Default.PlanArray);

        if (!output.Json)
        {
            if (plans.Count == 0)
            {
                Console.WriteLine("no plans");
                return ExitCodes.Ok;
            }

            foreach (var plan in plans)
            {
                Console.WriteLine($"{plan.Id,-20} {KindName(plan),-10} rev={plan.Revision,-3} {StateName(plan.LastApply?.State),-10} {plan.Name}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PlanShowAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "plan id");
        var plan = await connection.GetPlanAsync(id, ct);
        output.JsonValue(plan, ZentwikJson.Default.Plan);

        if (!output.Json)
        {
            PrintPlan(plan);
        }

        return ExitCodes.Ok;
    }

    private async Task<int> PlanApplyAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "plan id");
        var result = await connection.ApplyPlanAsync(id, ct);
        output.JsonValue(result, ZentwikJson.Default.PlanApplyResultDto);

        if (!output.Json)
        {
            Console.WriteLine($"plan    {result.PlanId}");
            Console.WriteLine($"state   {result.State}");
            Console.WriteLine($"reboot  {(result.PersistsAcrossReboot ? "persists" : "session-only")}");
            Console.WriteLine($"outcome {result.Outcome}");
            foreach (var target in result.Targets)
            {
                Console.WriteLine($"  {target.Status,-8} {target.Target,-40} desired={target.Desired ?? "-"} actual={target.Actual ?? "-"} {target.Failure}");
            }
        }

        return result.State switch
        {
            "applied" => ExitCodes.Ok,
            "partial" => ExitCodes.Partial,
            "notApplied" => ExitCodes.Unavailable,
            _ => ExitCodes.Failed,
        };
    }

    private async Task<int> PlanRevertAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "plan id");
        var result = await connection.RevertPlanAsync(id, ct);
        output.JsonValue(result, ZentwikJson.Default.PlanApplyResultDto);

        if (!output.Json)
        {
            Console.WriteLine($"plan {result.PlanId}: {result.Outcome}");
            foreach (var target in result.Targets)
            {
                Console.WriteLine($"  {target.Status,-8} {target.Target,-40} restored={target.Actual ?? "-"} {target.Failure}");
            }
        }

        return result.Targets.Any(static target => target.Status == "failed") ? ExitCodes.Failed : ExitCodes.Ok;
    }

    private async Task<int> PlanDeleteAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "plan id");
        var deleted = await connection.DeletePlanAsync(id, ct);
        output.JsonValue(deleted, ZentwikJson.Default.Boolean);
        output.Line(deleted ? $"deleted {id}" : $"not found: {id}");
        return deleted ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private async Task<int> PlanSaveAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--file");
        var file = args.Require("--file");
        var text = file == "-"
            ? Console.In.ReadToEnd()
            : await File.ReadAllTextAsync(file, ct);

        var plan = JsonSerializer.Deserialize(text, ZentwikJson.Default.Plan)
                   ?? throw new CliUsageException("无法解析方案 JSON。");

        var saved = await connection.SavePlanAsync(plan, ct);
        output.JsonValue(saved, ZentwikJson.Default.Plan);
        output.Line($"saved {saved.Id} rev {saved.Revision}");
        return ExitCodes.Ok;
    }

    private async Task<int> PlanCreateAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--kind", "--id", "--name", "--default-co", "--ccd-co", "--core", "--fmax", "--intel-voltage", "--scheme", "--set", "--rule");
        var kind = args.Require("--kind");
        var id = args.Require("--id");
        var name = args.Require("--name");

        Plan plan = kind switch
        {
            "cpuTuning" or "cpu" => BuildCpuPlan(id, name),
            "power" => BuildPowerPlan(id, name),
            "scheduling" or "sched" => BuildSchedulingPlan(id, name),
            _ => throw new CliUsageException($"未知方案类型：{kind}"),
        };

        var saved = await connection.SavePlanAsync(plan, ct);
        output.JsonValue(saved, ZentwikJson.Default.Plan);
        output.Line($"saved {saved.Id} rev {saved.Revision}");
        return ExitCodes.Ok;
    }

    private CpuTuningPlan BuildCpuPlan(string id, string name)
    {
        int? defaultCo = null;
        if (args.Value("--default-co") is { } defaultCoText)
        {
            if (!int.TryParse(defaultCoText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new CliUsageException("--default-co 需要一个整数。");
            }

            defaultCo = parsed;
        }

        var ccdOffsets = new Dictionary<int, int>();
        foreach (var entry in args.Repeated("--ccd-co"))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ccd)
                || ccd < 0
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            {
                throw new CliUsageException($"无法解析 --ccd-co {entry}，格式为 CCD编号=CO 值（如 0=-20）。");
            }

            ccdOffsets[ccd] = offset;
        }

        var cores = new List<CoreTuning>();
        foreach (var entry in args.Repeated("--core"))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var core))
            {
                throw new CliUsageException($"无法解析 --core {entry}，格式为 核=CO 值 或 核=auto。");
            }

            if (string.Equals(parts[1], "auto", StringComparison.OrdinalIgnoreCase))
            {
                cores.Add(new CoreTuning { CoreIndex = core, Auto = true });
                continue;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            {
                throw new CliUsageException($"无法解析 --core {entry}，格式为 核=CO 值 或 核=auto。");
            }

            cores.Add(new CoreTuning { CoreIndex = core, Offset = offset });
        }

        int? fmax = null;
        if (args.Value("--fmax") is { } fmaxText)
        {
            if (!int.TryParse(fmaxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new CliUsageException("--fmax 需要一个整数。");
            }

            fmax = parsed;
        }

        int? intelVoltage = null;
        if (args.Value("--intel-voltage") is { } mvText)
        {
            if (!int.TryParse(mvText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new CliUsageException("--intel-voltage 需要一个整数。");
            }

            intelVoltage = parsed;
        }

        if (defaultCo is null && ccdOffsets.Count == 0 && cores.Count == 0 && fmax is null && intelVoltage is null)
        {
            throw new CliUsageException("至少需要 --default-co、--ccd-co、--core、--fmax 或 --intel-voltage 之一。");
        }

        return new CpuTuningPlan
        {
            Id = id,
            Name = name,
            DefaultOffset = defaultCo,
            CcdOffsets = ccdOffsets.Count == 0 ? null : ccdOffsets,
            Cores = cores,
            FMaxLimitMhz = fmax,
            IntelVoltageOffsetMillivolts = intelVoltage,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private PowerPlan BuildPowerPlan(string id, string name)
    {
        Guid? scheme = null;
        if (args.Value("--scheme") is { } schemeText)
        {
            if (!Guid.TryParse(schemeText, out var parsed))
            {
                throw new CliUsageException("--scheme 需要一个 GUID。");
            }

            scheme = parsed;
        }

        var settings = new List<PowerSettingEntry>();
        foreach (var entry in args.Repeated("--set"))
        {
            var assignment = entry.Split('=', 2);
            if (assignment.Length != 2)
            {
                throw new CliUsageException($"无法解析 --set {entry}，格式为 子组/设置/ac|dc=值。");
            }

            var parts = assignment[0].Split('/');
            if (parts.Length != 3)
            {
                throw new CliUsageException($"无法解析 --set {entry}，格式为 子组/设置/ac|dc=值。");
            }

            var subGroup = parts[0] == "-" ? null : parts[0];
            if (!Guid.TryParse(parts[1], out _))
            {
                throw new CliUsageException($"设置项 {parts[1]} 不是 GUID。");
            }

            if (parts[2] is not ("ac" or "dc"))
            {
                throw new CliUsageException("电源值后缀必须是 ac 或 dc。");
            }

            settings.Add(new PowerSettingEntry
            {
                SettingId = parts[1],
                SubGroupId = subGroup,
                AcValue = parts[2] == "ac" ? assignment[1] : null,
                DcValue = parts[2] == "dc" ? assignment[1] : null,
            });
        }

        if (settings.Count == 0 && scheme is null)
        {
            throw new CliUsageException("至少需要 --scheme 或一个 --set。");
        }

        return new PowerPlan
        {
            Id = id,
            Name = name,
            BaseSchemeGuid = scheme,
            Settings = settings,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private SchedulingPlan BuildSchedulingPlan(string id, string name)
    {
        var rules = new List<SchedulingRule>();
        foreach (var entry in args.Repeated("--rule"))
        {
            var assignment = entry.Split('=', 2);
            if (assignment.Length != 2)
            {
                throw new CliUsageException($"无法解析 --rule {entry}，格式为 进程=掩码:优先级[:cpusets]。");
            }

            var parts = assignment[1].Split(':', 3);
            var mask = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
            var priority = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
            var cpuSets = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
            if (mask is null && priority is null && cpuSets is null)
            {
                throw new CliUsageException("调度规则至少需要掩码、优先级或 CPU Sets。");
            }

            rules.Add(new SchedulingRule
            {
                ProcessMatch = assignment[0],
                CpuMask = mask,
                Priority = priority,
                CpuSets = cpuSets,
            });
        }

        if (rules.Count == 0)
        {
            throw new CliUsageException("至少需要一个 --rule。");
        }

        return new SchedulingPlan
        {
            Id = id,
            Name = name,
            Rules = rules,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private async Task<int> ScenesAsync(CancellationToken ct)
    {
        var sub = args.Positionals.Count > 1 ? args.Positionals[1] : "list";
        return sub switch
        {
            "list" => await SceneListAsync(ct),
            "show" => await SceneShowAsync(ct),
            "activate" => await SceneActivateAsync(ct),
            "deactivate" => await SceneDeactivateAsync(ct),
            "enable" => await SceneToggleAsync(true, ct),
            "disable" => await SceneToggleAsync(false, ct),
            "delete" => await SceneDeleteAsync(ct),
            "save" => await SceneSaveAsync(ct),
            "create" => await SceneCreateAsync(ct),
            _ => throw new CliUsageException($"未知子命令：scenes {sub}"),
        };
    }

    private async Task<int> SceneListAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var scenes = await connection.ListScenesAsync(ct);
        output.JsonValue(scenes.ToArray(), ZentwikJson.Default.SceneSummaryDtoArray);

        if (!output.Json)
        {
            if (scenes.Count == 0)
            {
                Console.WriteLine("no scenes");
                return ExitCodes.Ok;
            }

            foreach (var scene in scenes)
            {
                var active = scene.Active ? "*" : " ";
                Console.WriteLine($"{active} p{scene.Priority,-3} {scene.Id,-16} enabled={Output.Status(scene.Enabled),-3} {scene.Name} -> {string.Join(",", scene.TargetPlanIds)}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> SceneShowAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "scene id");
        var scene = await connection.GetSceneAsync(id, ct);
        output.JsonValue(scene, ZentwikJson.Default.Scene);

        if (!output.Json)
        {
            Console.WriteLine($"id        {scene.Id}");
            Console.WriteLine($"name      {scene.Name}");
            Console.WriteLine($"priority  {scene.Priority}  enabled={Output.Status(scene.Enabled)}  restoreOnExit={Output.Status(scene.RestoreOnExit)}");
            Console.WriteLine($"trigger   {string.Join(", ", scene.Triggers.Select(static trigger => trigger.Argument is null ? trigger.Kind : $"{trigger.Kind}:{trigger.Argument}"))}");
            Console.WriteLine($"actions   {string.Join(", ", scene.Actions.Select(static action => action.PlanId))}");
            Console.WriteLine($"last      {scene.LastOutcome ?? "-"}");
        }

        return ExitCodes.Ok;
    }

    private async Task<int> SceneActivateAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "scene id");
        var activation = await connection.ActivateSceneAsync(id, ct);
        output.JsonValue(activation, ZentwikJson.Default.SceneActivationDto);
        output.Line($"{activation.SceneId}: {activation.Outcome}");
        return activation.Active ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private async Task<int> SceneDeactivateAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var activation = await connection.DeactivateSceneAsync(ct);
        output.JsonValue(activation, ZentwikJson.Default.SceneActivationDto);
        output.Line(activation.Outcome);
        return ExitCodes.Ok;
    }

    private async Task<int> SceneToggleAsync(bool enabled, CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "scene id");
        var scene = await connection.GetSceneAsync(id, ct);
        var saved = await connection.SaveSceneAsync(scene with { Enabled = enabled }, ct);
        output.JsonValue(saved, ZentwikJson.Default.Scene);
        output.Line($"{saved.Id}: enabled={Output.Status(saved.Enabled)}");
        return ExitCodes.Ok;
    }

    private async Task<int> SceneDeleteAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var id = args.Positional(2, "scene id");
        var deleted = await connection.DeleteSceneAsync(id, ct);
        output.JsonValue(deleted, ZentwikJson.Default.Boolean);
        output.Line(deleted ? $"deleted {id}" : $"not found: {id}");
        return deleted ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private async Task<int> SceneSaveAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--file");
        var file = args.Require("--file");
        var text = file == "-"
            ? Console.In.ReadToEnd()
            : await File.ReadAllTextAsync(file, ct);

        var scene = JsonSerializer.Deserialize(text, ZentwikJson.Default.Scene)
                    ?? throw new CliUsageException("无法解析场景 JSON。");

        var saved = await connection.SaveSceneAsync(scene, ct);
        output.JsonValue(saved, ZentwikJson.Default.Scene);
        output.Line($"saved {saved.Id}");
        return ExitCodes.Ok;
    }

    private async Task<int> SceneCreateAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--id", "--name", "--priority", "--plan", "--trigger");
        var id = args.Require("--id");
        var name = args.Require("--name");
        var priority = ParseInt(args.Value("--priority"), 0, int.MinValue, int.MaxValue);

        var plans = args.Repeated("--plan");
        if (plans.Count == 0)
        {
            throw new CliUsageException("至少需要一个 --plan。");
        }

        var actions = plans.Select(static entry => new SceneAction { PlanId = entry }).ToArray();

        var triggers = args.Repeated("--trigger").Select(static entry =>
        {
            var parts = entry.Split(':', 2);
            return new SceneTrigger
            {
                Kind = parts[0],
                Argument = parts.Length > 1 ? parts[1] : null,
            };
        }).ToArray();

        if (triggers.Length == 0)
        {
            triggers = [new SceneTrigger { Kind = SceneTriggerKinds.Manual }];
        }

        var scene = new Scene
        {
            Id = id,
            Name = name,
            Priority = priority,
            Actions = actions,
            Triggers = triggers,
        };

        var saved = await connection.SaveSceneAsync(scene, ct);
        output.JsonValue(saved, ZentwikJson.Default.Scene);
        output.Line($"saved {saved.Id}");
        return ExitCodes.Ok;
    }

    private async Task<int> LedgerAsync(CancellationToken ct)
    {
        if (args.Positionals.Count > 1 && args.Positionals[1] == "release")
        {
            args.EnsureOnly("--pipe");
            var target = args.Positional(2, "target");
            var released = await connection.ReleaseLedgerAsync(target, ct);
            output.JsonValue(released, ZentwikJson.Default.Boolean);
            output.Line(released ? $"released {target}" : $"not user-owned: {target}");
            return released ? ExitCodes.Ok : ExitCodes.Failed;
        }

        args.EnsureOnly("--pipe");
        var entries = await connection.ListLedgerAsync(ct);
        output.JsonValue(entries.ToArray(), ZentwikJson.Default.LedgerEntryArray);

        if (!output.Json)
        {
            if (entries.Count == 0)
            {
                Console.WriteLine("ledger empty");
                return ExitCodes.Ok;
            }

            foreach (var entry in entries)
            {
                var priorityText = entry.Priority >= int.MaxValue - 1
                    ? "max"
                    : entry.Priority.ToString(CultureInfo.InvariantCulture);
                Console.WriteLine($"{entry.Owner,-6} {entry.Status,-8} p{priorityText,-4} {entry.Target,-40} prev={entry.PreviousValue ?? "-"} desired={entry.DesiredValue ?? "-"} actual={entry.ActualValue ?? "-"} {entry.Failure}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> SettingsAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--language", "--theme", "--accent", "--scale", "--monitor-interval", "--sensor-interval");

        var hasPatch = args.Value("--language") is not null
                       || args.Value("--theme") is not null
                       || args.Value("--accent") is not null
                       || args.Value("--scale") is not null
                       || args.Value("--monitor-interval") is not null
                       || args.Value("--sensor-interval") is not null;

        if (!hasPatch)
        {
            var current = await connection.GetSettingsAsync(ct);
            output.JsonValue(current, ZentwikJson.Default.SettingsDto);
            output.Line($"language={current.Language} theme={current.Theme} accent={current.Accent} scale={current.UiScale:0.##} monitor={current.MonitorIntervalMs}ms sensor={DescribeSensorInterval(current.SensorIntervalMs)}");
            return ExitCodes.Ok;
        }

        double? scale = null;
        if (args.Value("--scale") is { } scaleText)
        {
            if (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new CliUsageException("--scale 需要一个数字。");
            }

            scale = parsed;
        }

        int? monitorInterval = args.Value("--monitor-interval") is { } monitorText
            ? ParseInt(monitorText, 1000, UserSettingsRules.MinMonitorIntervalMs, UserSettingsRules.MaxMonitorIntervalMs)
            : null;
        int? sensorInterval = args.Value("--sensor-interval") is { } sensorText
            ? ParseInt(sensorText, 1000, UserSettingsRules.MinSensorIntervalMs, UserSettingsRules.MaxSensorIntervalMs)
            : null;

        var saved = await connection.SetSettingsAsync(
            new SettingsPatch(args.Value("--language"), args.Value("--theme"), args.Value("--accent"), scale, monitorInterval, sensorInterval),
            ct);

        output.JsonValue(saved, ZentwikJson.Default.SettingsDto);
        output.Line($"saved language={saved.Language} theme={saved.Theme} accent={saved.Accent} scale={saved.UiScale:0.##} monitor={saved.MonitorIntervalMs}ms sensor={DescribeSensorInterval(saved.SensorIntervalMs)}");
        return ExitCodes.Ok;
    }

    private static string DescribeSensorInterval(int milliseconds) => $"{milliseconds}ms";

    /// <summary>
    /// 单行摘要只放"窗口内最高一核的平均频率"：平均值会被睡眠核拉低到接近 0，
    /// 放在监控行里只会让人以为频率掉光了；逐核明细由 --clocks 给。
    /// </summary>
    private static string DescribeClocks(IReadOnlyList<TelemetryCoreClockDto>? clocks)
    {
        if (clocks is not { Count: > 0 })
        {
            return string.Empty;
        }

        var high = clocks.Max(static item => item.Megahertz) / 1000d;
        return $"  clk {high:0.00}G";
    }

    private async Task<int> DoctorAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe");
        var doctor = await connection.GetDoctorAsync(ct);
        output.JsonValue(doctor, ZentwikJson.Default.DoctorDto);

        if (!output.Json)
        {
            Console.WriteLine($"agent       {doctor.Version}  protocol v{doctor.ProtocolVersion}");
            Console.WriteLine($"mode        service={Output.Status(doctor.RunningAsService)}  elevated={Output.Status(doctor.Elevated)}  simulated={Output.Status(doctor.Simulated)}");
            Console.WriteLine($"machine     {doctor.MachineName}");
            Console.WriteLine($"uptime      {TimeSpan.FromSeconds(doctor.UptimeSeconds):hh\\:mm\\:ss}");
            Console.WriteLine($"state       {doctor.StatePath}");
            Console.WriteLine($"logs        {doctor.LogDirectory}");
            Console.WriteLine($"plans       {doctor.PlanCount}  scenes {doctor.SceneCount}  ledger {doctor.LedgerEntryCount}  activeScene={doctor.ActiveSceneId ?? "-"}");
            Console.WriteLine();
            Console.WriteLine("modules");
            foreach (var module in doctor.Modules)
            {
                Console.WriteLine($"  {module.Id,-16} {module.State,-12} {module.Detail}");
            }

            Console.WriteLine();
            if (doctor.RecentErrors.Count == 0)
            {
                Console.WriteLine("recent errors: none");
                return ExitCodes.Ok;
            }

            Console.WriteLine("recent errors");
            foreach (var entry in doctor.RecentErrors)
            {
                Console.WriteLine($"  {entry.Timestamp:yyyy-MM-dd HH:mm:ss} {entry.Message}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> LogsAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--limit");
        var limit = ParseInt(args.Value("--limit"), 50, 1, 200);
        var entries = await connection.GetLogAsync(limit, ct);
        output.JsonValue(entries.ToArray(), ZentwikJson.Default.LogEntryDtoArray);

        if (!output.Json)
        {
            foreach (var entry in entries)
            {
                Console.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {entry.Level,-5} {entry.Message}");
            }
        }

        return ExitCodes.Ok;
    }

    private static void PrintPlan(Plan plan)
    {
        Console.WriteLine($"id        {plan.Id}");
        Console.WriteLine($"name      {plan.Name}");
        Console.WriteLine($"kind      {KindName(plan)}  rev={plan.Revision}  state={StateName(plan.LastApply?.State)}");
        Console.WriteLine($"apply     {plan.LastApply?.Outcome ?? "-"}  reboot={(plan.LastApply?.PersistsAcrossReboot ?? true ? "persists" : "session-only")}");

        switch (plan)
        {
            case CpuTuningPlan cpu:
                var asym = CurveOptimizerConfig.AnalyzeAsymmetry(cpu);
                Console.WriteLine($"default   {cpu.DefaultOffset?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                Console.WriteLine($"ccds      {(cpu.CcdOffsets is { Count: > 0 } ? string.Join(", ", cpu.CcdOffsets.OrderBy(static p => p.Key).Select(static p => $"ccd{p.Key}:{p.Value}")) : "-")}");
                Console.WriteLine($"cores     {(cpu.Cores.Count > 0 ? string.Join(", ", cpu.Cores.Select(static core => core.Auto ? $"{core.CoreIndex}:auto" : $"{core.CoreIndex}:{core.Offset?.ToString(CultureInfo.InvariantCulture) ?? "-"}")) : "-")}");
                Console.WriteLine($"asymmetry {(asym.IsAsymmetric ? $"yes ({asym.MinOffset}..{asym.MaxOffset})" : "no")}");
                Console.WriteLine($"fmax      {cpu.FMaxLimitMhz?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                Console.WriteLine($"intelMv   {cpu.IntelVoltageOffsetMillivolts?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                break;
            case PowerPlan power:
                Console.WriteLine($"scheme    {power.BaseSchemeGuid?.ToString("D") ?? "(active)"}");
                foreach (var setting in power.Settings)
                {
                    Console.WriteLine($"  {setting.SubGroupId ?? "-"}/{setting.SettingId} ac={setting.AcValue ?? "-"} dc={setting.DcValue ?? "-"}");
                }

                break;
            case SchedulingPlan scheduling:
                foreach (var rule in scheduling.Rules)
                {
                    Console.WriteLine($"  {rule.ProcessMatch} mask={rule.CpuMask ?? "-"} priority={rule.Priority ?? "-"} cpusets={rule.CpuSets ?? "-"} enforce={Output.Status(rule.Enforce)}");
                }

                break;
        }
    }

    private static string KindName(Plan plan) => plan.Kind switch
    {
        PlanKind.CpuTuning => "cpuTuning",
        PlanKind.Power => "power",
        _ => "scheduling",
    };

    private static string StateName(PlanApplyState? state) => state switch
    {
        PlanApplyState.Applied => "applied",
        PlanApplyState.Partial => "partial",
        PlanApplyState.Failed => "failed",
        _ => "draft",
    };

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new CliUsageException($"需要整数，收到 {text}。");
        }

        return Math.Clamp(value, min, max);
    }
}
