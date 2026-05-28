using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Short-lived in-memory cache for expensive storage metrics queries.
/// Uses per-key semaphores to prevent cache stampede on cold keys.
/// Semaphores are retained for the process lifetime (bounded by the small set of cache key patterns).
/// </summary>
public sealed class MetricsQueryCache
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(15);

    public MetricsQueryCache(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct,
        bool snapshot = false)
    {
        if (_cache.TryGetValue(key, out T cached))
            return cached;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached))
                return cached;

            var value = await factory(ct);
            _cache.Set(key, value, snapshot ? SnapshotTtl : DefaultTtl);
            return value;
        }
        finally
        {
            gate.Release();
            // Do not remove from _locks: TryRemove after Release creates a TOCTOU race where
            // a concurrent caller can GetOrAdd a new semaphore and bypass mutual exclusion.
        }
    }
}
