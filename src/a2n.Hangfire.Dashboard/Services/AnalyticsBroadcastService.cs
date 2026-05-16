using a2n.Hangfire.Dashboard.Hubs;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<AnalyticsBroadcastService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    public AnalyticsBroadcastService(
        IServiceProvider serviceProvider,
        IHubContext<DashboardHub> hubContext,
        ILogger<AnalyticsBroadcastService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AnalyticsBroadcastService started. Broadcasting every {Interval}s",
            _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BroadcastAnalytics(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown, exit the loop
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error broadcasting analytics data");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("AnalyticsBroadcastService stopped.");
    }

    private async Task BroadcastAnalytics(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var metricsProvider = scope.ServiceProvider.GetService<IStorageMetricsProvider>();

        if (metricsProvider == null)
        {
            _logger.LogDebug("IStorageMetricsProvider not available, skipping analytics broadcast");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var oneHourAgo = now.AddHours(-1);

        // Query last 1h throughput with OneMinute interval
        var throughput = await metricsProvider.GetThroughputTimelineAsync(
            oneHourAgo, now, MetricsInterval.OneMinute, ct);

        // Query server utilization snapshot
        var serverUtilization = await metricsProvider.GetServerUtilizationSnapshotAsync(ct);

        // Query queue depth snapshot
        var queueDepth = await metricsProvider.GetQueueDepthSnapshotAsync(ct);

        // Build broadcast payload
        var payload = new AnalyticsBroadcastPayload
        {
            Throughput = throughput,
            ServerUtilization = serverUtilization,
            QueueDepth = queueDepth
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
