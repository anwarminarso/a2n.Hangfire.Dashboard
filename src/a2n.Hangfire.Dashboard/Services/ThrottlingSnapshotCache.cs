using Microsoft.Extensions.DependencyInjection;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// A point-in-time view of every throttling primitive, with each mutex holder's job state already
/// resolved.
/// </summary>
public sealed class ThrottlingSnapshot
{
    public IReadOnlyList<SemaphoreDto> Semaphores { get; init; } = [];
    public IReadOnlyList<ThrottleWindowDto> Windows { get; init; } = [];

    /// <summary>One entry per mutex holder, flattened, so a mutex held twice appears twice.</summary>
    public IReadOnlyList<MutexHolder> Mutexes { get; init; } = [];

    public sealed class MutexHolder
    {
        public string MutexId { get; init; }

        /// <summary>Null when the mutex is registered with no current holder.</summary>
        public ThrottleHolderDto Holder { get; init; }
    }
}

/// <summary>
/// Process-wide short-TTL cache for the Throttling page's data.
/// </summary>
/// <remarks>
/// <para>
/// Semaphore occupancy is the fastest-moving thing the dashboard shows — holders appear and vanish
/// as jobs acquire and release — so the page has to refresh on a timer to be worth watching. Doing
/// that per circuit would multiply storage load by the number of open tabs, and the reads are not
/// cheap: a set listing per semaphore, a hash and a set listing per window, plus a
/// <see cref="ThrottlingDataReader.GetHolderDetails"/> pass that resolves every mutex holder's job
/// state and queries the monitoring API for live servers.
/// </para>
/// <para>
/// This singleton coalesces those reads: a single pass per <see cref="Ttl"/> window serves every
/// caller, with stampede protection. It mirrors <see cref="QueueOperationsStateCache"/> and
/// <see cref="HealthReportCache"/>.
/// </para>
/// <para>
/// Detach calls <see cref="Invalidate"/> so the operator sees the result of their own action
/// immediately rather than waiting out the TTL.
/// </para>
/// </remarks>
public sealed class ThrottlingSnapshotCache : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ThrottlingSnapshot _snapshot;
    private long _computedAtTicks;

    /// <summary>How long a snapshot is reused before a fresh read. Default: 5 seconds.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(5);

    public ThrottlingSnapshotCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>
    /// Returns the cached snapshot when fresh; otherwise reads it once (single-flighted) and caches it.
    /// </summary>
    public async Task<ThrottlingSnapshot> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && IsFresh())
            return _snapshot;

        var requestedAtTicks = DateTime.UtcNow.Ticks;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (forceRefresh)
            {
                if (Volatile.Read(ref _computedAtTicks) >= requestedAtTicks && _snapshot is not null)
                    return _snapshot;
            }
            else if (IsFresh())
            {
                return _snapshot;
            }

            var snapshot = await Task.Run(() =>
            {
                using var scope = _scopeFactory.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<ThrottlingDataReader>();
                return Read(reader);
            }, ct).ConfigureAwait(false);

            _snapshot = snapshot;
            Volatile.Write(ref _computedAtTicks, DateTime.UtcNow.Ticks);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Invalidates the cached snapshot so the next <see cref="GetAsync"/> reads fresh state.</summary>
    public void Invalidate() => Volatile.Write(ref _computedAtTicks, 0);

    private static ThrottlingSnapshot Read(ThrottlingDataReader reader)
    {
        var semaphores = reader.GetSemaphores();
        var windows = reader.GetWindows();
        var mutexes = reader.GetMutexes();

        // One GetHolderDetails pass for every holder across every mutex: it resolves job state and
        // reads the server list, so calling it per mutex would repeat that work needlessly.
        var holderDetails = reader
            .GetHolderDetails(mutexes.SelectMany(x => x.HolderJobIds).Distinct())
            .ToDictionary(x => x.JobId);

        var rows = new List<ThrottlingSnapshot.MutexHolder>();
        foreach (var mutex in mutexes)
        {
            if (mutex.HolderJobIds.Count == 0)
            {
                rows.Add(new ThrottlingSnapshot.MutexHolder { MutexId = mutex.Id });
                continue;
            }

            foreach (var jobId in mutex.HolderJobIds)
            {
                rows.Add(new ThrottlingSnapshot.MutexHolder
                {
                    MutexId = mutex.Id,
                    Holder = holderDetails.TryGetValue(jobId, out var holder)
                        ? holder
                        : new ThrottleHolderDto { JobId = jobId },
                });
            }
        }

        return new ThrottlingSnapshot
        {
            Semaphores = semaphores,
            Windows = windows,
            Mutexes = rows,
        };
    }

    private bool IsFresh()
    {
        if (_snapshot is null) return false;
        var ticks = Volatile.Read(ref _computedAtTicks);
        if (ticks == 0) return false;
        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < Ttl;
    }

    public void Dispose() => _gate.Dispose();
}
