using System.Text;
using System.Text.Json;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;

// 面向前置验证的客户端：不依赖 UI 即可确认进程边界、协议、能力探测与遥测通路。
// 控制台默认代码页是 GBK，中文描述会乱码。
Console.OutputEncoding = Encoding.UTF8;

var pipe = ProtocolInfo.PipeName;
var json = false;
var arguments = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pipe" when i + 1 < args.Length:
            pipe = args[++i];
            break;
        case "--json":
            json = true;
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            arguments.Add(args[i]);
            break;
    }
}

var command = arguments.Count > 0 ? arguments[0] : "status";

if (command is "help")
{
    PrintUsage();
    return 0;
}

try
{
    await using var connection = await AgentConnection
        .ConnectAsync(pipe, TimeSpan.FromSeconds(3))
        .ConfigureAwait(false);

    switch (command)
    {
        case "status":
            return await StatusAsync(connection, json).ConfigureAwait(false);
        case "caps":
            return await CapabilitiesAsync(connection, json).ConfigureAwait(false);
        case "plans":
            return await PlansAsync(connection, json).ConfigureAwait(false);
        case "scenes":
            return await ScenesAsync(connection, json).ConfigureAwait(false);
        case "settings":
            return await SettingsAsync(connection, arguments, json).ConfigureAwait(false);
        case "watch":
            return await WatchAsync(connection, arguments, json).ConfigureAwait(false);
        default:
            Console.Error.WriteLine($"unknown command: {command}");
            PrintUsage();
            return 2;
    }
}
catch (TimeoutException ex)
{
    Console.Error.WriteLine($"agent unavailable: {ex.Message}");
    Console.Error.WriteLine("start it with: dotnet run --project src/Zentwik.Agent");
    return 1;
}
catch (AgentRequestException ex)
{
    Console.Error.WriteLine($"request failed: {ex.Code} {ex.Detail}");
    return 3;
}

static async Task<int> StatusAsync(AgentConnection connection, bool json)
{
    var status = await connection.GetStatusAsync().ConfigureAwait(false);
    var capabilities = await connection.ListCapabilitiesAsync().ConfigureAwait(false);

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(status, ZentwikJson.Default.AgentStatusDto));
        return 0;
    }

    Console.WriteLine($"agent      {status.Version} (protocol v{status.ProtocolVersion})");
    Console.WriteLine($"machine    {status.MachineName}  elevated={status.Elevated}");
    Console.WriteLine($"processors {status.ProcessorCount}  groups={status.ProcessorGroups}");
    Console.WriteLine($"started    {status.StartedAt:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"data       {status.DataDirectory}");
    Console.WriteLine($"plans      {status.PlanCount}  scenes {status.SceneCount}");
    Console.WriteLine();

    foreach (var capability in capabilities)
    {
        var detail = string.IsNullOrEmpty(capability.Detail) ? string.Empty : $"  {capability.Detail}";
        Console.WriteLine($"  {capability.Id,-32} {capability.State,-12}{detail}");
    }

    var usable = capabilities.Count(static c => c.State is "running" or "degraded");
    Console.WriteLine();
    Console.WriteLine($"usable capabilities: {usable}/{capabilities.Count}");
    return usable > 0 ? 0 : 4;
}

static async Task<int> CapabilitiesAsync(AgentConnection connection, bool json)
{
    var capabilities = await connection.ListCapabilitiesAsync().ConfigureAwait(false);

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(capabilities, ZentwikJson.Default.CapabilityDtoArray));
        return 0;
    }

    foreach (var capability in capabilities)
    {
        Console.WriteLine($"{capability.Id}\t{capability.State}\t{capability.Detail}");
    }

    return 0;
}

static async Task<int> PlansAsync(AgentConnection connection, bool json)
{
    var plans = await connection.ListPlansAsync().ConfigureAwait(false);

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(plans, ZentwikJson.Default.PlanSummaryDtoArray));
        return 0;
    }

    if (plans.Count == 0)
    {
        Console.WriteLine("no plans");
        return 0;
    }

    foreach (var plan in plans)
    {
        Console.WriteLine($"{plan.Kind,-12} {plan.Id,-24} rev={plan.Revision} {plan.State}  {plan.Name}");
    }

    return 0;
}

static async Task<int> ScenesAsync(AgentConnection connection, bool json)
{
    var scenes = await connection.ListScenesAsync().ConfigureAwait(false);

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(scenes, ZentwikJson.Default.SceneSummaryDtoArray));
        return 0;
    }

    if (scenes.Count == 0)
    {
        Console.WriteLine("no scenes");
        return 0;
    }

    foreach (var scene in scenes)
    {
        Console.WriteLine($"p{scene.Priority,-4} {scene.Id,-24} enabled={scene.Enabled} {scene.Name} -> {string.Join(",", scene.TargetPlanIds)}");
    }

    return 0;
}

static async Task<int> SettingsAsync(AgentConnection connection, List<string> arguments, bool json)
{
    var valueArguments = arguments.Skip(1).ToList();
    if (valueArguments.Count == 0)
    {
        var current = await connection.GetSettingsAsync().ConfigureAwait(false);
        Console.WriteLine(json
            ? JsonSerializer.Serialize(current, ZentwikJson.Default.SettingsDto)
            : $"language={current.Language} theme={current.Theme} accent={current.Accent} scale={current.UiScale:0.##}");
        return 0;
    }

    string? language = null;
    string? theme = null;
    string? accent = null;
    double? scale = null;

    for (var i = 0; i < valueArguments.Count; i++)
    {
        switch (valueArguments[i])
        {
            case "--language" when i + 1 < valueArguments.Count:
                language = valueArguments[++i];
                break;
            case "--theme" when i + 1 < valueArguments.Count:
                theme = valueArguments[++i];
                break;
            case "--accent" when i + 1 < valueArguments.Count:
                accent = valueArguments[++i];
                break;
            case "--scale" when i + 1 < valueArguments.Count:
                if (!double.TryParse(valueArguments[++i], out var parsed))
                {
                    Console.Error.WriteLine("--scale expects a number");
                    return 2;
                }

                scale = parsed;
                break;
            default:
                Console.Error.WriteLine($"unexpected argument: {valueArguments[i]}");
                return 2;
        }
    }

    var saved = await connection
        .SetSettingsAsync(new SettingsPatch(language, theme, accent, scale))
        .ConfigureAwait(false);

    Console.WriteLine($"saved language={saved.Language} theme={saved.Theme} accent={saved.Accent} scale={saved.UiScale:0.##}");
    return 0;
}

static async Task<int> WatchAsync(AgentConnection connection, List<string> arguments, bool json)
{
    var count = 5;
    var interval = 500;

    for (var i = 1; i < arguments.Count; i++)
    {
        switch (arguments[i])
        {
            case "--count" when i + 1 < arguments.Count && int.TryParse(arguments[i + 1], out var parsedCount):
                count = Math.Clamp(parsedCount, 1, 10000);
                i++;
                break;
            case "--interval" when i + 1 < arguments.Count && int.TryParse(arguments[i + 1], out var parsedInterval):
                interval = Math.Clamp(parsedInterval, 200, 5000);
                i++;
                break;
            default:
                Console.Error.WriteLine($"unexpected argument: {arguments[i]}");
                return 2;
        }
    }

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

        if (json)
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

    var ack = await connection.SubscribeTelemetryAsync(interval).ConfigureAwait(false);
    Console.Error.WriteLine($"subscribed interval={ack.IntervalMs}ms, expecting {count} samples");

    var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(count * (interval / 1000d) + 5)))
        .ConfigureAwait(false);

    if (completed != done.Task)
    {
        Console.Error.WriteLine($"timeout: received {received}/{count}");
        return 5;
    }

    await connection.RequestAsync(Ops.TelemetryUnsubscribe).ConfigureAwait(false);
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("""
        zentwik — Zentwik agent diagnostic client

          status                  agent identity, capabilities, plan counts
          caps                    capability list
          plans                   plan summaries
          scenes                  scene summaries
          settings [options]      read or patch settings
              --language zh-CN|zh-TW|en-US
              --theme dark|light
              --accent blue|violet|teal|amber
              --scale 0.8..1.6
          watch [--count N] [--interval MS]

          --pipe NAME             agent pipe name (default: zentwik-next-agent)
          --json                  machine-readable output
        """);
}
