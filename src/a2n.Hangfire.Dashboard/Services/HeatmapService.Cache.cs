using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

public partial class HeatmapService
{
    /// <summary>
    /// Builds the cache key for an aggregation request from the active source, projection-window
    /// kind, viewer time zone, load metric, the "hide sub-hourly" toggle (which changes the projected
    /// matrix), and the lookback weeks (which change the historical aggregation). The source is passed
    /// explicitly so the Projected fallback served on a historical failure reuses the stable Projected
    /// key rather than the requesting Historical key.
    /// </summary>
    private static string BuildCacheKey(HeatmapSource source, HeatmapQuery query)
    {
        var tz = string.IsNullOrWhiteSpace(query.ViewerTimeZoneId) ? "UTC" : query.ViewerTimeZoneId.Trim();
        var sub = query.HideSubHourly ? 1 : 0;
        return $"heatmap:{source}:{query.WindowKind}:{tz}:{query.LoadMetric}:sub{sub}:lb{query.LookbackWeeks}";
    }

    /// <summary>
    /// Serves the aggregation for <paramref name="key"/> with single-flight + stale-while-revalidate
    /// semantics over <see cref="IMemoryCache"/>:
    /// <list type="bullet">
    /// <item>A fresh cached entry (within its time-to-live) is returned immediately (Req 13.2).</item>
    /// <item>An expired (stale) entry is returned immediately while a single background recomputation
    /// replaces it (Req 13.8).</item>
    /// <item>On a cold key, concurrent callers serialize on a per-key gate so the aggregation is
    /// computed exactly once and the single result is shared (Req 13.4).</item>
    /// </list>
    /// </summary>
    private async Task<HeatmapResult> GetOrCreateWithStaleAsync(
        string key,
        Func<CancellationToken, Task<HeatmapResult>> factory,
        CancellationToken ct)
    {
        // Fast path: an entry exists. Serve it (fresh or stale), kicking off a refresh when stale.
        if (_cache.TryGetValue(key, out CacheEntry entry) && entry is not null)
        {
            if (DateTimeOffset.UtcNow < entry.ExpiresAtUtc)
            {
                return entry.Value; // Fresh hit (Req 13.2).
            }

            // Stale: trigger a single background recomputation and serve the stale value (Req 13.8).
            TriggerBackgroundRefresh(key, factory);
            return entry.Value;
        }

        // Cold path: serialize concurrent callers so the aggregation is computed once (Req 13.4).
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have populated the cache while we waited on the gate.
            if (_cache.TryGetValue(key, out entry) && entry is not null)
            {
                if (DateTimeOffset.UtcNow < entry.ExpiresAtUtc)
                {
                    return entry.Value;
                }
                // Stale entry found after waiting; we own the gate, so recompute synchronously below.
            }

            var value = await factory(ct).ConfigureAwait(false);
            Store(key, value);
            return value;
        }
        finally
        {
            gate.Release();
            // Do not remove the gate from _locks: a TryRemove after Release creates a TOCTOU race
            // where a concurrent caller can GetOrAdd a fresh gate and bypass mutual exclusion.
        }
    }

    /// <summary>
    /// Starts at most one background recomputation for <paramref name="key"/> using the per-key gate.
    /// If a compute/refresh is already in progress the call is a no-op and the caller keeps serving
    /// the stale value, ensuring the aggregation is recomputed exactly once at a time (Req 13.4, 13.8).
    /// </summary>
    private void TriggerBackgroundRefresh(string key, Func<CancellationToken, Task<HeatmapResult>> factory)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            // A compute or refresh is already running for this key; keep serving stale.
            return;
        }

        // Detach from the caller's request lifetime so cancelling the request does not abort the
        // refresh; the stale value remains served until this completes (Req 13.8).
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await factory(CancellationToken.None).ConfigureAwait(false);
                Store(key, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background heatmap aggregation refresh failed for cache key {CacheKey}", key);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <summary>
    /// Stores an aggregation result under <paramref name="key"/>, stamping its freshness expiry at
    /// now + the configured time-to-live (Req 13.5) and retaining the physical entry long enough to
    /// serve stale while a refresh runs (Req 13.8). Sliding expiration bounds memory over time.
    /// </summary>
    private void Store(string key, HeatmapResult value)
    {
        var cacheEntry = new CacheEntry(value, DateTimeOffset.UtcNow.Add(_cacheTtl));
        _cache.Set(key, cacheEntry, new MemoryCacheEntryOptions
        {
            SlidingExpiration = _cacheRetention
        });
    }
}
