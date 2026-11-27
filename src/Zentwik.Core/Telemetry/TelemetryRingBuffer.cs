using Zentwik.Platform;

namespace Zentwik.Core.Telemetry;

/// <summary>
/// 固定容量的采样环形缓冲，满了丢弃最旧一帧。用于监控历史。
/// 服务重启或长时间无订阅后自动清空——不落盘是为了不引入常驻采样开销（见研究仓库 polling-budget）。
/// </summary>
public sealed class TelemetryRingBuffer(int capacity)
{
    /// <summary>默认 3600 帧：按 1 秒推送约 1 小时，按 500 ms 约半小时。</summary>
    public const int DefaultCapacity = 3600;

    private readonly TelemetrySample[] _buffer = new TelemetrySample[capacity];
    private readonly object _gate = new();
    private int _head; // 下一个写入位置
    private int _count;

    public int Capacity => capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(TelemetrySample sample)
    {
        lock (_gate)
        {
            _buffer[_head] = sample;
            _head = (_head + 1) % capacity;
            if (_count < capacity)
            {
                _count++;
            }
        }
    }

    /// <summary>最近 <paramref name="limit"/> 帧，时间升序；limit 超过已有数量时返回全部。</summary>
    public TelemetrySample[] Snapshot(int limit)
    {
        lock (_gate)
        {
            var take = Math.Min(limit, _count);
            if (take <= 0)
            {
                return [];
            }

            var result = new TelemetrySample[take];
            var oldest = _head - take;
            if (oldest < 0)
            {
                oldest += capacity;
            }

            for (var index = 0; index < take; index++)
            {
                result[index] = _buffer[(oldest + index) % capacity];
            }

            return result;
        }
    }
}
