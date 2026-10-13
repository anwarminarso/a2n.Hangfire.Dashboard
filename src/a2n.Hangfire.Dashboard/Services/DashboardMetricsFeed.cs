using global::Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// A point-in-time read of the dashboard's headline metrics: the storage statistics and the server
/// list. Instances are shared across circuits; treat them as immutable.
/// </summary>
public sealed class DashboardMetricsSnapshot
{
    public StatisticsDto Statistics { get; init; } = new();

    public IReadOnlyList<ServerDto> Servers { get; init; } = [];

    public DateTime CapturedAtUtc { get; init; }
}

/// <summary>
/// In-process realtime feed that dashboard components subscribe to directly.
/// </summary>
/// <remarks>
/// <para>
/// The Blazor components run in the server process, next to <see cref="MetricsBroadcastService"/>
/// and <see cref="AnalyticsBroadcastService"/>. They used to receive broadcasts by opening a SignalR
/// client connection back to the dashboard's own public URL. That connection carries no user
/// cookies, so it was rejected by any authorization filter based on <c>HttpContext.User</c>, and
/// each circuit silently fell back to polling storage itself (issue #61). Subscribing here needs
/// no network hop and no credentials.
/// </para>
/// <para>
/// Subscriptions count towards <see cref="DashboardSubscriptionTracker"/> exactly like hub
/// subscriptions, so the broadcast services only query storage while someone is watching.
/// <see cref="Hubs.DashboardHub"/> keeps receiving the same broadcasts for external clients.
/// </para>
/// <para>
/// <see cref="GetMetricsAsync"/> serves the latest snapshot while it is younger than
/// <see cref="Interval"/> and otherwise reads it once for all callers, so N tabs opening at the
/// same time (and the prerender plus interactive render of each) cost one storage read.
/// </para>
/// </remarks>
public sealed class DashboardMetricsFeed : IDisposable
{
    /// <summary>Lower bound for <see cref="DashboardUIOptions.StatsPollingInterval"/>.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DashboardSubscriptionTracker _subscriptions;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<DashboardMetricsFeed> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _handlersLock = new();

    private Func<DashboardMetricsSnapshot, Task>[] _metricsHandlers = [];
    private Func<AnalyticsBroadcastPayload, Task>[] _analyticsHandlers = [];
    private DashboardMetricsSnapshot _latest;

    public DashboardMetricsFeed(
        IServiceScopeFactory scopeFactory,
        DashboardSubscriptionTracker subscriptions,
        DashboardUIOptions options,
        ILogger<DashboardMetricsFeed> logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<DashboardMetricsFeed>.Instance;
    }

    /// <summary>
    /// Metrics refresh interval, from <see cref="DashboardUIOptions.StatsPollingInterval"/>
    /// (milliseconds), clamped to at least <see cref="MinInterval"/>.
    /// </summary>
    public TimeSpan Interval => ResolveInterval(_options.StatsPollingInterval);

    /// <summary>The most recent snapshot, or <c>null</c> when none has been read yet.</summary>
    public DashboardMetricsSnapshot Latest => Volatile.Read(ref _latest);

    internal static TimeSpan ResolveInterval(int milliseconds)
    {
        var interval = TimeSpan.FromMilliseconds(milliseconds);
        return interval < MinInterval ? MinInterval : interval;
    }

    /// <summary>
    /// Returns the latest snapshot when it is younger than <see cref="Interval"/>; otherwise reads
    /// a fresh one (single-flighted across callers).
    /// </summary>
    public async Task<DashboardMetricsSnapshot> GetMetricsAsync(CancellationToken ct = default)
    {
        var latest = Latest;
        if (IsFresh(latest)) return latest;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            latest = Latest;
            if (IsFresh(latest)) return latest;
            return await ReadAndStoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads a fresh snapshot regardless of the cached one. Used by
    /// <see cref="MetricsBroadcastService"/> on every tick.
    /// </summary>
    public async Task<DashboardMetricsSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAndStoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Subscribes to metrics broadcasts. Dispose the returned handle to unsubscribe. The handler
    /// runs on a thread-pool thread; Blazor components should marshal with <c>InvokeAsync</c>.
    /// </summary>
    public IDisposable SubscribeMetrics(Func<DashboardMetricsSnapshot, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlersLock) _metricsHandlers = [.. _metricsHandlers, handler];
        _subscriptions.AddMetricsSubscriber();

        return new Subscription(() =>
        {
            lock (_handlersLock) _metricsHandlers = Remove(_metricsHandlers, handler);
            _subscriptions.RemoveMetricsSubscriber();
        });
    }

    /// <summary>Subscribes to analytics broadcasts. Dispose the returned handle to unsubscribe.</summary>
    public IDisposable SubscribeAnalytics(Func<AnalyticsBroadcastPayload, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlersLock) _analyticsHandlers = [.. _analyticsHandlers, handler];
        _subscriptions.AddAnalyticsSubscriber();

        return new Subscription(() =>
        {
            lock (_handlersLock) _analyticsHandlers = Remove(_analyticsHandlers, handler);
            _subscriptions.RemoveAnalyticsSubscriber();
        });
    }

    /// <summary>Delivers a metrics snapshot to every in-process subscriber.</summary>
    public void PublishMetrics(DashboardMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
        Dispatch(Volatile.Read(ref _metricsHandlers), snapshot);
    }

    /// <summary>Delivers an analytics payload to every in-process subscriber.</summary>
    public void PublishAnalytics(AnalyticsBroadcastPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Dispatch(Volatile.Read(ref _analyticsHandlers), payload);
    }

    /// <summary>Number of in-process metrics subscribers (diagnostics and tests).</summary>
    public int MetricsSubscriberCount => Volatile.Read(ref _metricsHandlers).Length;

    /// <summary>Number of in-process analytics subscribers (diagnostics and tests).</summary>
    public int AnalyticsSubscriberCount => Volatile.Read(ref _analyticsHandlers).Length;

    public void Dispose() => _gate.Dispose();

    private async Task<DashboardMetricsSnapshot> ReadAndStoreAsync(CancellationToken ct)
    {
        var snapshot = await Task.Run(() =>
        {
            using var scope = _scopeFactory.CreateScope();
            var monitor = scope.ServiceProvider.GetRequiredService<HangfireMonitorService>();
            return new DashboardMetricsSnapshot
            {
                Statistics = monitor.GetStatistics(),
                Servers = monitor.GetServers()?.ToArray() ?? [],
                CapturedAtUtc = DateTime.UtcNow,
            };
        }, ct).ConfigureAwait(false);

        Volatile.Write(ref _latest, snapshot);
        return snapshot;
    }

    private bool IsFresh(DashboardMetricsSnapshot snapshot)
        => snapshot is not null && DateTime.UtcNow - snapshot.CapturedAtUtc < Interval;

    private void Dispatch<T>(Func<T, Task>[] handlers, T payload)
    {
        // Fire each handler independently: one slow or broken circuit must not delay the others
        // or the broadcast loop.
        foreach (var handler in handlers)
            _ = InvokeHandlerAsync(handler, payload);
    }

    private async Task InvokeHandlerAsync<T>(Func<T, Task> handler, T payload)
    {
        try
        {
            await handler(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Typically a circuit that is being torn down (ObjectDisposedException,
            // JSDisconnectedException). Not actionable, so keep it out of default log levels.
            _logger.LogDebug(ex, "Dashboard metrics subscriber failed");
        }
    }

    private static Func<T, Task>[] Remove<T>(Func<T, Task>[] handlers, Func<T, Task> handler)
    {
        var index = Array.IndexOf(handlers, handler);
        if (index < 0) return handlers;
        var result = new Func<T, Task>[handlers.Length - 1];
        Array.Copy(handlers, 0, result, 0, index);
        Array.Copy(handlers, index + 1, result, index, handlers.Length - index - 1);
        return result;
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
