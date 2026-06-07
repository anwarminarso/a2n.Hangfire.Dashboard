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
/// <b>Timing relationships.</b> Three intervals cooperate to keep storage load bounded:
/// the realtime metric broadcast (~2s, centralized), this cache's <see cref="Ttl"/> (default 5s),
/// and the hero card's UI refresh (~10s). The cache TTL sits between the two so a hero refresh
/// almost always hits a warm entry, while an explicit user "Refresh" click bypasses the cache via
/// the <c>forceRefresh</c> parameter on <see cref="GetAsync"/>.
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
        // Stored as ticks and accessed via Volatile/Interlocked so the lock-free fast path in
        // IsFresh never observes a torn DateTime on any platform.
        public long ComputedAtTicks;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Entry[] _entries;

    /// <summary>
    /// How long a computed report is reused before a fresh one is produced. Kept short so health
    /// stays current, but long enough to absorb bursts from multiple circuits and probes.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(5);

    public HealthReportCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

        // One entry per Mode value, indexed by (int)mode. Sized off the enum so adding a mode
        // can't silently produce an index-out-of-range or a mismatched slot.
        var modeCount = Enum.GetValues(typeof(Mode)).Length;
        _entries = new Entry[modeCount];
        for (var i = 0; i < modeCount; i++)
            _entries[i] = new Entry();
    }

    /// <summary>
    /// Returns a cached report for the given <paramref name="mode"/> when one was computed within
    /// <see cref="Ttl"/>; otherwise computes a fresh report (single-flighted per mode).
    /// </summary>
    /// <param name="mode">Which set of checks to run.</param>
    /// <param name="forceRefresh">
    /// When <c>true</c>, bypasses the freshness check and always computes a new report (still
    /// single-flighted so concurrent forced callers share one computation). Use for explicit user
    /// actions such as the hero card's manual "Refresh" button.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<HealthReport> GetAsync(Mode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        var entry = _entries[(int)mode];

        if (!forceRefresh && IsFresh(entry))
            return entry.Report;

        // Captured before contending for the gate. If, by the time we acquire it, the entry was
        // computed at or after this instant, a concurrent caller already produced exactly the fresh
        // report we wanted — reuse it instead of recomputing (coalesces forced refreshes too).
        var requestedAtTicks = DateTime.UtcNow.Ticks;

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (forceRefresh)
            {
                if (Volatile.Read(ref entry.ComputedAtTicks) >= requestedAtTicks && entry.Report is not null)
                    return entry.Report;
            }
            else if (IsFresh(entry))
            {
                return entry.Report;
            }

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
            Volatile.Write(ref entry.ComputedAtTicks, DateTime.UtcNow.Ticks);
            return report;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private bool IsFresh(Entry entry)
    {
        if (entry.Report is null) return false;
        var ticks = Volatile.Read(ref entry.ComputedAtTicks);
        if (ticks == 0) return false;
        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < Ttl;
    }

    public void Dispose()
    {
        foreach (var entry in _entries)
            entry.Gate.Dispose();
    }
}
