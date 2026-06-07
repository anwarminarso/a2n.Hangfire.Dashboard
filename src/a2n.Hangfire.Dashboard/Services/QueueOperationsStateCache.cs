using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Process-wide short-TTL cache for the queue pause / maintenance state.
/// </summary>
/// <remarks>
/// <para>
/// The Queues page, the maintenance banner (shown on every page), and the nav menu all need to
/// know the current pause/maintenance state. Without sharing, each component on each circuit would
/// issue its own <see cref="QueueOperationsService.GetState"/> storage read on its own timer,
/// multiplying DB load by the number of open tabs.
/// </para>
/// <para>
/// This singleton coalesces those reads: a single storage round-trip per <see cref="Ttl"/> window
/// serves every caller, with stampede protection. It mirrors <see cref="HealthReportCache"/>.
/// </para>
/// </remarks>
public sealed class QueueOperationsStateCache : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private QueueOperationsState _state;
    private long _computedAtTicks;

    /// <summary>How long a state snapshot is reused before a fresh read. Default: 2 seconds.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(2);

    public QueueOperationsStateCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>
    /// Returns the cached state when fresh; otherwise reads it once (single-flighted) and caches it.
    /// </summary>
    public async Task<QueueOperationsState> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && IsFresh())
            return _state;

        var requestedAtTicks = DateTime.UtcNow.Ticks;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (forceRefresh)
            {
                if (Volatile.Read(ref _computedAtTicks) >= requestedAtTicks && _state is not null)
                    return _state;
            }
            else if (IsFresh())
            {
                return _state;
            }

            var state = await Task.Run(() =>
            {
                using var scope = _scopeFactory.CreateScope();
                var ops = scope.ServiceProvider.GetRequiredService<QueueOperationsService>();
                return ops.GetState();
            }, ct).ConfigureAwait(false);

            _state = state;
            Volatile.Write(ref _computedAtTicks, DateTime.UtcNow.Ticks);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Invalidates the cached snapshot so the next <see cref="GetAsync"/> reads fresh state.</summary>
    public void Invalidate() => Volatile.Write(ref _computedAtTicks, 0);

    private bool IsFresh()
    {
        if (_state is null) return false;
        var ticks = Volatile.Read(ref _computedAtTicks);
        if (ticks == 0) return false;
        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < Ttl;
    }

    public void Dispose() => _gate.Dispose();
}
