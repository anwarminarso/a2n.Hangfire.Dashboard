using a2n.Hangfire.Dashboard.Hubs;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Background service that broadcasts analytics data every
/// <see cref="PollingOptions.AnalyticsBroadcastInterval"/> (5 seconds by default).
/// Only registered when IStorageMetricsProvider is available.
/// Broadcasts last 1h throughput, server utilization snapshot, and queue depth snapshot
/// to in-process subscribers (<see cref="DashboardMetricsFeed"/>) and to clients subscribed
/// to the "analytics" SignalR group.
/// </summary>
/// <remarks>
/// The loop is a fixed-cadence <see cref="DashboardPoller"/>: the interval runs from the start of
/// one broadcast to the start of the next, so slow queries don't stretch the push gap by their own
/// duration. A broadcast that fails, or that takes the whole interval, backs the next one off up to
/// <see cref="PollingOptions.MaxBackoffInterval"/> (issue #64); the change into and out of that
/// state is logged once at Warning, not on every tick.
/// </remarks>
public class AnalyticsBroadcastService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardSubscriptionTracker _subscriptions;
    private readonly DashboardMetricsFeed _feed;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<AnalyticsBroadcastService> _logger;

    public AnalyticsBroadcastService(
        IServiceProvider serviceProvider,
        IHubContext<DashboardHub> hubContext,
        DashboardSubscriptionTracker subscriptions,
        DashboardMetricsFeed feed,
        DashboardUIOptions options,
        ILogger<AnalyticsBroadcastService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _subscriptions = subscriptions;
        _feed = feed;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read once at start: UseHangfireDashboardUI has applied the host's options by now.
        var polling = _options?.Polling ?? new PollingOptions();
        var interval = polling.AnalyticsBroadcastInterval;
        if (interval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "AnalyticsBroadcastService is disabled (Polling.AnalyticsBroadcastInterval is zero); " +
                "the Analytics page does not update live.");
            return;
        }

        _logger.LogInformation(
            "AnalyticsBroadcastService started. Broadcasting every {Interval}s",
            interval.TotalSeconds);

        var poller = new DashboardPoller("analytics broadcast", interval, BroadcastAnalytics,
            polling, logger: _logger)
        {
            FixedCadence = true,
            StopOnObjectDisposed = false,
        };
        await poller.RunUntilStoppedAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("AnalyticsBroadcastService stopped.");
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

        // In-process subscribers (the dashboard's own components), then external hub clients.
        _feed.PublishAnalytics(payload);

        await _hubContext.Clients.Group("analytics")
            .SendAsync("AnalyticsUpdate", payload, ct);
    }
}

/// <summary>
/// Payload broadcast to analytics SignalR subscribers every
/// <see cref="PollingOptions.AnalyticsBroadcastInterval"/> (5 seconds by default).
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
