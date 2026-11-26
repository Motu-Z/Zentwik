using Zentwik.Domain.Capabilities;
using Zentwik.Platform;

namespace Zentwik.Core.Capabilities;

/// <summary>探测有成本也有副作用，缓存 5 秒：既不每次重探，也不必重启代理才能看到驱动变化。</summary>
public sealed class CapabilityService(IEnumerable<ICapabilityProbe> probes)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<ICapabilityProbe> _probes = probes.ToList();
    private readonly Lock _gate = new();
    private CapabilityDescriptor[]? _cached;
    private DateTimeOffset _cachedAt;

    public IReadOnlyList<CapabilityDescriptor> Get()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is not null && now - _cachedAt < CacheTtl)
            {
                return _cached;
            }

            var results = new List<CapabilityDescriptor>(_probes.Count);
            foreach (var probe in _probes)
            {
                results.Add(ProbeSafely(probe));
            }

            _cached = [.. results];
            _cachedAt = now;
            return _cached;
        }
    }

    /// <summary>开关类改动之后调用：缓存里存的是改动前的探测结果，不清掉会让响应自相矛盾。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private static CapabilityDescriptor ProbeSafely(ICapabilityProbe probe)
    {
        try
        {
            return probe.Probe();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CapabilityDescriptor
            {
                Id = probe.CapabilityId,
                State = CapabilityState.Failed,
                Detail = ex.Message,
            };
        }
    }
}
