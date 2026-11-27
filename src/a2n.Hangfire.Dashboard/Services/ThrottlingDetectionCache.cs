using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Process-wide cache for "does this installation use Hangfire.Throttling?", the check that decides
/// whether the Throttling nav item is shown.
/// </summary>
/// <remarks>
/// <para>
/// The check is cheap when the answer is yes — the first set count returns a non-zero and it stops.
/// The expensive case is the common one: an installation without the Throttling package answers no
/// only after counting all five registry sets, and it repeats that on every new circuit, forever,
/// since there is nothing to find and nothing to latch onto.
/// </para>
/// <para>
/// This singleton coalesces those reads: one storage round-trip per <see cref="Ttl"/> window serves
/// every circuit, with stampede protection. It mirrors <see cref="QueueOperationsStateCache"/> and
/// <see cref="HealthReportCache"/>.
/// </para>
/// <para>
/// The TTL is longer than those two because the answer changes at most once in a deployment's life —
/// when the host application first registers a throttling primitive. <see cref="Invalidate"/> exists
/// so anything that creates one can make the nav item appear immediately rather than waiting it out.
/// </para>
/// </remarks>
public sealed class ThrottlingDetectionCache : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _hasData;
    private long _computedAtTicks;

    /// <summary>How long a detection result is reused before a fresh read. Default: 30 seconds.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    public ThrottlingDetectionCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>
    /// Returns the cached result when fresh; otherwise reads it once (single-flighted) and caches it.
    /// </summary>
    public async Task<bool> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && IsFresh())
            return _hasData;

        var requestedAtTicks = DateTime.UtcNow.Ticks;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (forceRefresh)
            {
                if (Volatile.Read(ref _computedAtTicks) >= requestedAtTicks)
                    return _hasData;
            }
            else if (IsFresh())
            {
                return _hasData;
            }

            var hasData = await Task.Run(() =>
            {
                using var scope = _scopeFactory.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<ThrottlingDataReader>();
                return reader.HasThrottlingData();
            }, ct).ConfigureAwait(false);

            _hasData = hasData;
            Volatile.Write(ref _computedAtTicks, DateTime.UtcNow.Ticks);
            return hasData;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Invalidates the cached result so the next <see cref="GetAsync"/> reads fresh state.</summary>
    public void Invalidate() => Volatile.Write(ref _computedAtTicks, 0);

    private bool IsFresh()
    {
        var ticks = Volatile.Read(ref _computedAtTicks);
        if (ticks == 0) return false;
        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < Ttl;
    }

    public void Dispose() => _gate.Dispose();
}
