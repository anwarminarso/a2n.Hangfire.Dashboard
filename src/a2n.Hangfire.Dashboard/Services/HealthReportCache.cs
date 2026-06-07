using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Process-wide cache for <see cref="HealthReport"/>s, keyed by probe mode (live / ready / full).
/// </summary>
/// <remarks>
/// <para>
/// The dashboard's health report is expensive to compute — a full report issues roughly seven
/// synchronous storage round-trips (statistics, servers, queues, processing jobs, hourly counters,
/// recurring jobs). Without a cache, every browser circuit rendering the Home page hero card and
/// every Kubernetes probe would recompute it independently, multiplying that cost by the number of
/// connected clients.
/// </para>
/// <para>
/// This singleton collapses concurrent and rapid-fire requests into a single computation per mode
/// within a short TTL window, with per-mode stampede protection. It mirrors the centralized
/// broadcast model already used for realtime metrics (see <c>MetricsBroadcastService</c>) instead
/// of per-client polling.
/// </para>
/// <para>
/// The cache resolves a scoped <see cref="HealthCheckService"/> from an injected
/// <see cref="IServiceScopeFactory"/>, so callers do not need their own DI scope.
/// </para>
/// </remarks>
public sealed class HealthReportCache : IDisposable
{
    /// <summary>Probe mode selecting which set of checks to run.</summary>
    public enum Mode
    {
        /// <summary>Storage probe only (liveness).</summary>
        Live,
        /// <summary>Storage + server presence (readiness).</summary>
        Ready,
        /// <summary>All checks (full diagnostic report).</summary>
        Full,
    }

    private sealed class Entry
    {
        public HealthReport Report;
        public DateTime ComputedAtUtc;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Entry[] _entries =
    [
        new Entry(), // Live
        new Entry(), // Ready
        new Entry(), // Full
    ];

    /// <summary>
    /// How long a computed report is reused before a fresh one is produced. Kept short so health
    /// stays current, but long enough to absorb bursts from multiple circuits and probes.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(5);

    public HealthReportCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>
    /// Returns a cached report for the given <paramref name="mode"/> when one was computed within
    /// <see cref="Ttl"/>; otherwise computes a fresh report (single-flighted per mode).
    /// </summary>
    public async Task<HealthReport> GetAsync(Mode mode, CancellationToken ct = default)
    {
        var entry = _entries[(int)mode];

        if (IsFresh(entry))
            return entry.Report;

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate — another caller may have just refreshed it.
            if (IsFresh(entry))
                return entry.Report;

            var report = await Task.Run(() =>
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<HealthCheckService>();
                return mode switch
                {
                    Mode.Live => service.CheckLiveness(),
                    Mode.Ready => service.CheckReadiness(),
                    _ => service.CheckFull(),
                };
            }, ct).ConfigureAwait(false);

            entry.Report = report;
            entry.ComputedAtUtc = DateTime.UtcNow;
            return report;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private bool IsFresh(Entry entry)
        => entry.Report is not null && DateTime.UtcNow - entry.ComputedAtUtc < Ttl;

    public void Dispose()
    {
        foreach (var entry in _entries)
            entry.Gate.Dispose();
    }
}
