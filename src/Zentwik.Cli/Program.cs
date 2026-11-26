using System.Text;
using Zentwik.Cli;
using Zentwik.Contracts.Protocol;

// 控制台默认代码页是 GBK，中文描述会乱码。
Console.OutputEncoding = Encoding.UTF8;

CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (CliUsageException ex)
{
    new Output(args.Contains("--json")).Error("usage", ex.Message);
    return ExitCodes.Usage;
}

var json = line.Flag("--json");
var output = new Output(json);
var command = line.Positionals.Count > 0 ? line.Positionals[0] : "status";

if (line.Help)
{
    Help.Print(command);
    return ExitCodes.Ok;
}

if (command == "help")
{
    Help.Print(line.Positionals.Count > 1 ? line.Positionals[1] : null);
    return ExitCodes.Ok;
}

if (command == "version")
{
    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    Console.WriteLine($"zentwik {version}  protocol v{ProtocolInfo.Version}");
    return ExitCodes.Ok;
}

var pipe = line.Value("--pipe") ?? ProtocolInfo.PipeName;

try
{
    await using var connection = await AgentConnection
        .ConnectAsync(pipe, TimeSpan.FromSeconds(5))
        .ConfigureAwait(false);

    return await new CliApp(connection, line, output).RunAsync().ConfigureAwait(false);
}
catch (CliUsageException ex)
{
    output.Error("usage", ex.Message);
    Help.Print(command);
    return ExitCodes.Usage;
}
catch (TimeoutException ex)
{
    output.Error("agent.unavailable", $"无法连接代理：{ex.Message}");
    return ExitCodes.AgentUnavailable;
}
catch (IpcTransportException ex)
{
    output.Error("agent.unavailable", ex.Message);
    return ExitCodes.AgentUnavailable;
}
catch (AgentRequestException ex)
{
    output.Error(ex.Code, ex.Detail ?? ex.Message);
    return ex.Code is ErrorCodes.HardwareUnavailable or ErrorCodes.ModuleDisabled or ErrorCodes.PowerLimitUnsupported
        ? ExitCodes.Unavailable
        : ExitCodes.Failed;
}
catch (UnauthorizedAccessException ex)
{
    output.Error("agent.rejected", ex.Message);
    return ExitCodes.Failed;
}
catch (IOException ex)
{
    output.Error("agent.unavailable", $"与代理的管道中断：{ex.Message}");
    return ExitCodes.AgentUnavailable;
}
catch (Exception ex)
{
    output.Error("cli.failure", $"{ex.GetType().Name}: {ex.Message}");
    return ExitCodes.Failed;
}
