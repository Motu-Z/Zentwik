using System.IO.Pipes;
using Zentwik.Agent.Capabilities;
using Zentwik.Agent.Diagnostics;
using Zentwik.Agent.State;
using Zentwik.Agent.Telemetry;
using Zentwik.Contracts.Protocol;
using Zentwik.Platform;

namespace Zentwik.Agent;

public sealed class AgentOptions
{
    public required string PipeName { get; init; }

    public required string DataDirectory { get; init; }

    public bool UseFakeTelemetry { get; init; }

    public bool EchoLog { get; init; } = true;

    public static AgentOptions Parse(string[] args)
    {
        var pipe = ProtocolInfo.PipeName;
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZentwikNext");
        var fake = false;
        var echo = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    pipe = args[++i];
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDirectory = args[++i];
                    break;
                case "--fake":
                    fake = true;
                    break;
                case "--quiet":
                    echo = false;
                    break;
            }
        }

        return new AgentOptions
        {
            PipeName = pipe,
            DataDirectory = dataDirectory,
            UseFakeTelemetry = fake,
            EchoLog = echo,
        };
    }
}

/// <summary>每个连接一个管道实例：慢客户端不会阻塞其他人。</summary>
public sealed class AgentServer
{
    private readonly AgentOptions _options;
    private readonly StateStore _store;
    private readonly TelemetryHub _hub;
    private readonly CapabilityService _capabilities;
    private readonly ISystemInfo _system;
    private readonly AgentLog _log;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly List<AgentSession> _sessions = [];
    private readonly Lock _sessionsGate = new();

    public AgentServer(
        AgentOptions options,
        StateStore store,
        TelemetryHub hub,
        CapabilityService capabilities,
        ISystemInfo system,
        AgentLog log)
    {
        _options = options;
        _store = store;
        _hub = hub;
        _capabilities = capabilities;
        _system = system;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($"代理启动：管道 {_options.PipeName}，数据目录 {_options.DataDirectory}，协议 v{ProtocolInfo.Version}。");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _options.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (IOException ex)
                {
                    _log.Error($"创建管道实例失败：{ex.Message}");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                var session = new AgentSession(pipe, this, _log);
                Track(session);
                _ = session.RunAsync(ct).ContinueWith(
                    _ => Untrack(session),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            await ShutdownSessionsAsync().ConfigureAwait(false);
            _log.Info("代理已停止。");
        }
    }

    internal StateStore Store => _store;

    internal TelemetryHub Hub => _hub;

    internal CapabilityService Capabilities => _capabilities;

    internal ISystemInfo System => _system;

    internal AgentOptions Options => _options;

    internal DateTimeOffset StartedAt => _startedAt;

    private void Track(AgentSession session)
    {
        lock (_sessionsGate)
        {
            _sessions.Add(session);
        }
    }

    private void Untrack(AgentSession session)
    {
        lock (_sessionsGate)
        {
            _sessions.Remove(session);
        }
    }

    private async Task ShutdownSessionsAsync()
    {
        AgentSession[] snapshot;
        lock (_sessionsGate)
        {
            snapshot = [.. _sessions];
            _sessions.Clear();
        }

        foreach (var session in snapshot)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
