using System.Globalization;
using System.Text.Json;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Domain.Plans;
using Zentwik.Domain.Scenes;

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
        args.EnsureOnly("--pipe");
        var modules = await connection.ListModulesAsync(ct);
        output.JsonValue(modules.ToArray(), ZentwikJson.Default.ModuleDtoArray);

        if (!output.Json)
        {
            foreach (var module in modules)
            {
                Console.WriteLine($"{module.Id,-16} {module.State,-12} {module.Name}");
                if (!string.IsNullOrWhiteSpace(module.Detail))
                {
                    Console.WriteLine($"  {module.Detail}");
                }
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> MonitorAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--count", "--interval");
        var count = ParseInt(args.Value("--count"), 5, 1, 100000);
        var interval = ParseInt(args.Value("--interval"), 500, 200, 5000);

        var received = 0;
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
                Console.WriteLine(
                    $"{sample.Timestamp:HH:mm:ss.fff}  cpu {sample.CpuTotalPercent,5:0.0}%  mem {memoryUsed,5:0.0}%  proc {sample.ProcessCount,4}  cores {sample.ProcessorCount}");
            }

            if (Interlocked.Increment(ref received) >= count)
            {
                done.TrySetResult();
            }
        };

        var ack = await connection.SubscribeTelemetryAsync(interval, ct);
        output.Line($"subscribed interval={ack.IntervalMs}ms, expecting {count} samples");

        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(count * (interval / 1000d) + 5), ct));
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
        args.EnsureOnly("--pipe", "--kind", "--id", "--name", "--core", "--fmax", "--scheme", "--set", "--rule");
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
        var cores = new List<CoreTuning>();
        foreach (var entry in args.Repeated("--core"))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var core)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            {
                throw new CliUsageException($"无法解析 --core {entry}，格式为 核=CO 值。");
            }

            cores.Add(new CoreTuning { CoreIndex = core, Offset = offset });
        }

        if (cores.Count == 0)
        {
            throw new CliUsageException("至少需要一个 --core。");
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

        return new CpuTuningPlan
        {
            Id = id,
            Name = name,
            Cores = cores,
            FMaxLimitMhz = fmax,
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
                throw new CliUsageException($"无法解析 --rule {entry}，格式为 进程=掩码:优先级。");
            }

            var parts = assignment[1].Split(':', 2);
            var mask = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
            var priority = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
            if (mask is null && priority is null)
            {
                throw new CliUsageException("调度规则至少需要掩码或优先级。");
            }

            rules.Add(new SchedulingRule
            {
                ProcessMatch = assignment[0],
                CpuMask = mask,
                Priority = priority,
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
            Console.WriteLine($"actions   {string.Join(", ", scene.Actions.Select(static action => $"{action.PlanId}@{action.PlanRevision}"))}");
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

        var actions = plans.Select(static entry =>
        {
            var parts = entry.Split('@', 2);
            var revision = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
            return new SceneAction { PlanId = parts[0], PlanRevision = revision };
        }).ToArray();

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
                Console.WriteLine($"{entry.Owner,-6} {entry.Status,-8} p{entry.Priority,-4} {entry.Target,-40} prev={entry.PreviousValue ?? "-"} desired={entry.DesiredValue ?? "-"} actual={entry.ActualValue ?? "-"} {entry.Failure}");
            }
        }

        return ExitCodes.Ok;
    }

    private async Task<int> SettingsAsync(CancellationToken ct)
    {
        args.EnsureOnly("--pipe", "--language", "--theme", "--accent", "--scale");

        var hasPatch = args.Value("--language") is not null
                       || args.Value("--theme") is not null
                       || args.Value("--accent") is not null
                       || args.Value("--scale") is not null;

        if (!hasPatch)
        {
            var current = await connection.GetSettingsAsync(ct);
            output.JsonValue(current, ZentwikJson.Default.SettingsDto);
            output.Line($"language={current.Language} theme={current.Theme} accent={current.Accent} scale={current.UiScale:0.##}");
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

        var saved = await connection.SetSettingsAsync(
            new SettingsPatch(args.Value("--language"), args.Value("--theme"), args.Value("--accent"), scale),
            ct);

        output.JsonValue(saved, ZentwikJson.Default.SettingsDto);
        output.Line($"saved language={saved.Language} theme={saved.Theme} accent={saved.Accent} scale={saved.UiScale:0.##}");
        return ExitCodes.Ok;
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
                Console.WriteLine($"cores     {string.Join(", ", cpu.Cores.Select(static core => $"{core.CoreIndex}:{core.Offset?.ToString(CultureInfo.InvariantCulture) ?? "-"}"))}");
                Console.WriteLine($"fmax      {cpu.FMaxLimitMhz?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
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
                    Console.WriteLine($"  {rule.ProcessMatch} mask={rule.CpuMask ?? "-"} priority={rule.Priority ?? "-"} enforce={Output.Status(rule.Enforce)}");
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
