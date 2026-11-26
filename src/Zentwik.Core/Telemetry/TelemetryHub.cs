using Zentwik.Platform;

namespace Zentwik.Core.Telemetry;

/// <summary>采样串行化：Win32 差分与传感器差分都是跨帧状态，并发调用会错乱。</summary>
public sealed class TelemetryHub(ITelemetrySource source, ICpuSensorSource? sensors = null)
{
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private TelemetrySample? _last;
    private CpuSensorReading _sensorReading = new(null, null);
    private string? _sensorError;

    public TelemetrySample? Last => Volatile.Read(ref _last);

    /// <summary>系统遥测可用但传感器通道缺席时，仅温度与封装功耗为空。</summary>
    public bool HasSensors => sensors is not null;

    /// <summary>采一帧：系统遥测每次都读，<paramref name="readSensors"/> 决定这一拍是否也读 CPU 传感器。</summary>
    public async Task<TelemetrySample> SampleAsync(CancellationToken ct, bool readSensors)
    {
        await _sampleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sample = source.Sample();
            if (sensors is not null)
            {
                if (readSensors)
                {
                    RefreshSensors();
                }

                sample = WithSensors(sample);
            }

            Volatile.Write(ref _last, sample);
            return sample;
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    public Task<TelemetrySample> SampleAsync(CancellationToken ct) => SampleAsync(ct, readSensors: true);

    /// <summary>
    /// 按周期推送。sink 返回 false 时结束订阅。
    /// <paramref name="sensorIntervalMs"/> 小于等于 0 时不读传感器（调用方正常不会传，这里只做防御）；
    /// 读硬件的成本比读系统计数器高一个量级，因此传感器刷新频率独立于推送频率，两次读取之间沿用上一次读数。
    /// </summary>
    public async Task RunSubscriptionAsync(
        int intervalMs,
        int sensorIntervalMs,
        Func<TelemetrySample, bool> sink,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        var sensorInterval = TimeSpan.FromMilliseconds(Math.Max(sensorIntervalMs, 0));
        var lastSensorAt = DateTimeOffset.MinValue;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var readSensors = sensors is not null
                              && sensorIntervalMs > 0
                              && (lastSensorAt == DateTimeOffset.MinValue || now - lastSensorAt >= sensorInterval);

            TelemetrySample sample;
            try
            {
                sample = await SampleAsync(ct, readSensors).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            if (readSensors)
            {
                lastSensorAt = now;
            }

            if (!sink(sample))
            {
                return;
            }
        }
    }

    private void RefreshSensors()
    {
        try
        {
            if (sensors!.TrySampleCpuSensors(out var reading, out var error))
            {
                _sensorReading = reading;
                _sensorError = null;
            }
            else
            {
                // 读不到就置空：留着上一帧会让界面把过期值当现值显示。
                _sensorReading = new CpuSensorReading(null, null);
                _sensorError = error;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _sensorReading = new CpuSensorReading(null, null);
            _sensorError = ex.Message;
        }
    }

    private TelemetrySample WithSensors(TelemetrySample sample) => sample with
    {
        TemperatureCelsius = _sensorReading.TemperatureCelsius,
        PackagePowerWatts = _sensorReading.PackagePowerWatts,
        SensorDetail = _sensorError ?? _sensorReading.Detail,
    };
}
