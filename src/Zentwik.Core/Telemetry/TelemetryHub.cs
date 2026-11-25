using Zentwik.Platform;

namespace Zentwik.Core.Telemetry;

/// <summary>采样串行化：Win32 差分状态在并发调用下会错乱。</summary>
public sealed class TelemetryHub(ITelemetrySource source)
{
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private TelemetrySample? _last;

    public TelemetrySample? Last => Volatile.Read(ref _last);

    public async Task<TelemetrySample> SampleAsync(CancellationToken ct)
    {
        await _sampleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sample = source.Sample();
            Volatile.Write(ref _last, sample);
            return sample;
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    /// <summary>按周期推送。sink 返回 false 时结束订阅。</summary>
    public async Task RunSubscriptionAsync(
        int intervalMs,
        Func<TelemetrySample, bool> sink,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            TelemetrySample sample;
            try
            {
                sample = await SampleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            if (!sink(sample))
            {
                return;
            }
        }
    }
}
