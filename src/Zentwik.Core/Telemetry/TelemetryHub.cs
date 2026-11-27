using Zentwik.Platform;

namespace Zentwik.Core.Telemetry;

/// <summary>采样串行化：Win32 差分与传感器差分都是跨帧状态，并发调用会错乱。</summary>
public sealed class TelemetryHub(ITelemetrySource source, ICpuSensorSource? sensors = null)
{
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private TelemetrySample? _last;
    private CpuSensorReading _sensorReading = new(null, null);
    private string? _sensorError;

    // 历史缓冲：只在采样时写入。无订阅者时完全不采样，因此历史随订阅积累、断连/重启后清空。
    private readonly TelemetryRingBuffer _history = new(TelemetryRingBuffer.DefaultCapacity);

    public TelemetrySample? Last => Volatile.Read(ref _last);

    /// <summary>历史缓冲容量（帧）。</summary>
    public int HistoryCapacity => _history.Capacity;

    /// <summary>当前缓冲里的帧数。</summary>
    public int HistoryCount => _history.Count;

    /// <summary>最近 <paramref name="limit"/> 帧，时间升序。</summary>
    public TelemetrySample[] HistorySnapshot(int limit) => _history.Snapshot(limit);

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
            _history.Add(sample);
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

        // 按拍数而不是时间戳决定是否读传感器：定时器每拍的实际间隔会在 1000 ms 上下抖几毫秒，
        // 用"距上次读取是否已满 sensorInterval"判断时，994 ms 的那一拍会被判成未到点，
        // 于是每隔一拍才读一次，传感器实际刷新率被悄悄砍半。
        var ticksPerSensor = Math.Max(
            1,
            (int)Math.Round(sensorIntervalMs / (double)Math.Max(intervalMs, 1), MidpointRounding.AwayFromZero));
        var tick = 0;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            tick++;
            var readSensors = sensors is not null
                              && sensorIntervalMs > 0
                              && (tick - 1) % ticksPerSensor == 0;

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
        CoreClocks = _sensorReading.CoreClocks,
        SensorDetail = _sensorError ?? _sensorReading.Detail,
    };
}
