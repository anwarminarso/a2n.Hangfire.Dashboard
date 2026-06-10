using a2n.Hangfire.Dashboard.Hubs;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Background service that broadcasts analytics data via SignalR every 5 seconds.
/// Only registered when IStorageMetricsProvider is available.
/// Broadcasts last 1h throughput, server utilization snapshot, and queue depth snapshot
/// to clients subscribed to the "analytics" SignalR group.
/// </summary>
public class AnalyticsBroadcastService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardSubscriptionTracker _subscriptions;
    private readonly ILogger<AnalyticsBroadcastService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    public AnalyticsBroadcastService(
        IServiceProvider serviceProvider,
        IHubContext<DashboardHub> hubContext,
        DashboardSubscriptionTracker subscriptions,
        ILogger<AnalyticsBroadcastService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AnalyticsBroadcastService started. Broadcasting every {Interval}s",
            _interval.TotalSeconds);

        // PeriodicTimer fires on a fixed cadence independent of how long each broadcast takes.
        // The previous implementation delayed _interval *after* finishing the work, so the
        // real period was (query_time + _interval). On slow stores (e.g. SQL Server under load)
        // that stretched the analytics push gap well past its target. With a fixed-cadence timer,
        // ticks that arrive while a broadcast is still running are coalesced (PeriodicTimer keeps
        // at most one pending tick), so we self-throttle instead of drifting ever further behind.
        using var timer = new PeriodicTimer(_interval);

        while (await SafeWaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                var startedAt = Stopwatch.GetTimestamp();

                await BroadcastAnalytics(stoppingToken);

                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                if (elapsed > _interval)
                {
                    // The broadcast itself took longer than the cadence, so pushes cannot keep up.
                    // This is the realtime-degrade signal (slow metrics queries) that has no
                    // exception to surface on its own.
                    _logger.LogWarning(
                        "Analytics broadcast took {Elapsed}ms, exceeding the {Interval}s cadence; " +
                        "realtime updates may lag. Consider profiling the metrics provider queries.",
                        (long)elapsed.TotalMilliseconds, _interval.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown, exit the loop
                break;
            }
            catch (Exception ex)
            {
                // Logged at Error: a throwing metrics query (e.g. a malformed provider
                // SQL statement) silently kills the analytics SignalR channel for every
                // client, so it needs to be visible in default production log levels.
                _logger.LogError(ex, "Error broadcasting analytics data");
            }
        }

        _logger.LogInformation("AnalyticsBroadcastService stopped.");
    }

    /// <summary>
    /// Awaits the next timer tick, translating the cancellation that fires on shutdown into a
    /// clean <c>false</c> (loop exit) rather than a thrown <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task BroadcastAnalytics(CancellationToken ct)
    {
        if (!_subscriptions.HasAnalyticsSubscribers)
            return;

        using var scope = _serviceProvider.CreateScope();
        var metricsProvider = scope.ServiceProvider.GetService<IStorageMetricsProvider>();

        if (metricsProvider == null)
        {
            _logger.LogDebug("IStorageMetricsProvider not available, skipping analytics broadcast");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var sixHoursAgo = now.AddHours(-6);

        // The three queries are independent and each opens its own DB connection, so run them
        // concurrently rather than sequentially. Under load on slower stores (e.g. SQL Server)
        // the sequential sum dominated the broadcast time and stretched the push cadence; with
        // fan-out the per-broadcast cost is now bounded by the slowest single query, not their sum.
        //
        // Query last 6h throughput with OneHour interval.
        // Hangfire AggregatedCounter stores data at hourly granularity (stats:succeeded:yyyy-MM-dd-HH),
        // so querying with OneMinute interval would return only 1-2 data points for a 1h window.
        // Using 6h with OneHour gives meaningful chart data (up to 6 points).
        var throughputTask = metricsProvider.GetThroughputTimelineAsync(
            sixHoursAgo, now, MetricsInterval.OneHour, ct);
        var serverUtilizationTask = metricsProvider.GetServerUtilizationSnapshotAsync(ct);
        var queueDepthTask = metricsProvider.GetQueueDepthSnapshotAsync(ct);

        await Task.WhenAll(throughputTask, serverUtilizationTask, queueDepthTask);

        // Build broadcast payload
        var payload = new AnalyticsBroadcastPayload
        {
            Throughput = throughputTask.Result,
            ServerUtilization = serverUtilizationTask.Result,
            QueueDepth = queueDepthTask.Result
        };

        // Broadcast to "analytics" group
        await _hubContext.Clients.Group("analytics")
            .SendAsync("AnalyticsUpdate", payload, ct);
    }
}

/// <summary>
/// Payload broadcast to analytics SignalR subscribers every 5 seconds.
/// Contains last 1h throughput timeline, server utilization snapshot, and queue depth snapshot.
/// </summary>
public class AnalyticsBroadcastPayload
{
    /// <summary>
    /// Last 1 hour throughput data points (succeeded/failed/deleted per minute).
    /// </summary>
    public IReadOnlyList<ThroughputDataPoint> Throughput { get; set; } = Array.Empty<ThroughputDataPoint>();

    /// <summary>
    /// Current server utilization snapshot (busy vs total workers per server).
    /// </summary>
    public SnapshotResult<IReadOnlyList<ServerUtilizationDto>> ServerUtilization { get; set; }

    /// <summary>
    /// Current queue depth snapshot (enqueued count per queue).
    /// </summary>
    public SnapshotResult<IReadOnlyList<QueueDepthDto>> QueueDepth { get; set; }
}
