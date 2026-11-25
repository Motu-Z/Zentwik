using System.IO.Pipes;
using System.Text.Json;
using Xunit;
using Zentwik.Agent;
using Zentwik.Agent.Capabilities;
using Zentwik.Agent.Diagnostics;
using Zentwik.Agent.State;
using Zentwik.Agent.Telemetry;
using Zentwik.Contracts.Dto;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;
using Zentwik.Platform;
using Zentwik.Platform.Fakes;

namespace Zentwik.Contracts.Tests;

/// <summary>真实命名管道，不用内存替身：要验证的是进程边界本身。</summary>
public sealed class AgentPipeTests : IAsyncLifetime
{
    private readonly string _pipeName = $"zentwik-test-{Guid.NewGuid():N}";
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"zentwik-test-{Guid.NewGuid():N}");

    private AgentLog _log = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serverTask = null!;

    public async Task InitializeAsync()
    {
        _log = new AgentLog(_dataDirectory, echoToConsole: false);
        var store = new StateStore(_dataDirectory, _log);
        await store.LoadAsync();

        var options = new AgentOptions
        {
            PipeName = _pipeName,
            DataDirectory = _dataDirectory,
            UseFakeTelemetry = true,
            EchoLog = false,
        };

        var server = new AgentServer(
            options,
            store,
            new TelemetryHub(new FakeTelemetrySource(processorCount: 4)),
            new CapabilityService([]),
            new FakeSystemInfo(),
            _log);

        _cts = new CancellationTokenSource();
        _serverTask = server.RunAsync(_cts.Token);
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _cts.Dispose();
        _log.Dispose();

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Status_Returns_Agent_Identity()
    {
        await using var connection = await ConnectAsync();

        var status = await connection.GetStatusAsync();

        Assert.Equal(ProtocolInfo.Version, status.ProtocolVersion);
        Assert.Equal(4, status.ProcessorCount);
        Assert.False(status.Elevated);
        Assert.Equal(_dataDirectory, status.DataDirectory);
    }

    [Fact]
    public async Task Capabilities_Roundtrip_Through_Pipe()
    {
        await using var connection = await ConnectAsync();

        var capabilities = await connection.ListCapabilitiesAsync();

        Assert.Empty(capabilities);
    }

    [Fact]
    public async Task Settings_Patch_Persists_To_Disk_And_Survives_Reconnect()
    {
        await using (var connection = await ConnectAsync())
        {
            var saved = await connection.SetSettingsAsync(new SettingsPatch("en-US", "light", "teal", 1.25));

            Assert.Equal("en-US", saved.Language);
            Assert.Equal(1.25, saved.UiScale);
        }

        var stateFile = Path.Combine(_dataDirectory, "state.json");
        Assert.True(File.Exists(stateFile));
        var json = await File.ReadAllTextAsync(stateFile);
        Assert.Contains("\"language\": \"en-US\"", json, StringComparison.Ordinal);

        await using var second = await ConnectAsync();
        var reloaded = await second.GetSettingsAsync();
        Assert.Equal("teal", reloaded.Accent);
    }

    [Fact]
    public async Task Invalid_Settings_Are_Rejected_With_Stable_ErrorCode()
    {
        await using var connection = await ConnectAsync();

        var error = await Assert.ThrowsAsync<AgentRequestException>(() =>
            connection.SetSettingsAsync(new SettingsPatch("fr-FR", null, null, null)));

        Assert.Equal(ErrorCodes.PayloadInvalid, error.Code);
        Assert.Contains("fr-FR", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telemetry_Subscription_Streams_Events_Until_Count_Reached()
    {
        await using var connection = await ConnectAsync();

        var samples = new List<TelemetryDto>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

            lock (samples)
            {
                samples.Add(sample);
                if (samples.Count >= 3)
                {
                    gate.TrySetResult();
                }
            }
        };

        var ack = await connection.SubscribeTelemetryAsync(200);
        Assert.Equal(200, ack.IntervalMs);

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (samples)
        {
            Assert.True(samples.Count >= 3);
            Assert.All(samples, static s => Assert.InRange(s.CpuTotalPercent, 0d, 100d));
            Assert.Equal(4, samples[0].ProcessorCount);
        }
    }

    [Fact]
    public async Task Concurrent_Clients_Are_Served_Independently()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();

        var results = await Task.WhenAll(first.GetStatusAsync(), second.GetStatusAsync());

        Assert.Equal(results[0].MachineName, results[1].MachineName);
    }

    [Fact]
    public async Task Protocol_Version_Mismatch_Is_Rejected_Without_Timeout()
    {
        await using var pipe = await ConnectRawAsync();

        var request = new RequestEnvelope { V = ProtocolInfo.Version + 1, Id = 99, Op = Ops.Ping };
        var body = JsonSerializer.SerializeToUtf8Bytes(request, ZentwikJson.Default.RequestEnvelope);
        await Frames.WriteAsync(pipe, body, CancellationToken.None);

        var reply = await Frames.ReadAsync(pipe, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.NotNull(reply);

        var response = JsonSerializer.Deserialize(reply, ZentwikJson.Default.ResponseEnvelope);
        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.ProtocolVersionMismatch, response.Error);
        Assert.Equal(99, response.Id);
    }

    private async Task<AgentConnection> ConnectAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await AgentConnection.ConnectAsync(_pipeName, TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException ex)
            {
                last = ex;
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"代理未在期望时间内开始监听：{last?.Message}");
    }

    private async Task<NamedPipeClientStream> ConnectRawAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.ConnectAsync(1000);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("代理未在期望时间内开始监听。");
    }

    private sealed class FakeSystemInfo : ISystemInfo
    {
        public string MachineName => "test-machine";

        public bool IsElevated => false;

        public int ProcessorCount => 4;

        public int ProcessorGroups => 1;

        public string? CpuVendor => "TestVendor";

        public string? CpuName => "Test CPU";
    }
}
