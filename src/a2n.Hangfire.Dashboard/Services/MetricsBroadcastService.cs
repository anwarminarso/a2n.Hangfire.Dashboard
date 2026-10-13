using a2n.Hangfire.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Background service that periodically reads the dashboard metrics once and delivers them to
/// in-process subscribers (<see cref="DashboardMetricsFeed"/>, used by the dashboard's own
/// components) and to external SignalR clients in the <c>metrics</c> group of
/// <see cref="DashboardHub"/>. Runs every <see cref="DashboardUIOptions.StatsPollingInterval"/>
/// and only queries storage while someone is subscribed.
/// </summary>
public class MetricsBroadcastService : BackgroundService
{
    private readonly DashboardMetricsFeed _feed;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardSubscriptionTracker _subscriptions;
    private readonly ILogger<MetricsBroadcastService> _logger;

    public MetricsBroadcastService(
        DashboardMetricsFeed feed,
        IHubContext<DashboardHub> hubContext,
        DashboardSubscriptionTracker subscriptions,
        ILogger<MetricsBroadcastService> logger)
    {
        _feed = feed;
        _hubContext = hubContext;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read once at start: UseHangfireDashboardUI has applied the host's options by now.
        var interval = _feed.Interval;
        _logger.LogInformation("MetricsBroadcastService started. Broadcasting every {Interval}ms", interval.TotalMilliseconds);

        // Fixed cadence, like AnalyticsBroadcastService: a slow tick doesn't push the next one back.
        using var timer = new PeriodicTimer(interval);

        while (await SafeWaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                await BroadcastMetrics(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error broadcasting metrics");
            }
        }
    }

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

    private async Task BroadcastMetrics(CancellationToken ct)
    {
        if (!_subscriptions.HasMetricsSubscribers)
            return;

        var snapshot = await _feed.RefreshAsync(ct);
        _feed.PublishMetrics(snapshot);

        await _hubContext.Clients.Group("metrics").SendAsync("MetricsUpdated", ToHubPayload(snapshot), ct);
    }

    /// <summary>The <c>MetricsUpdated</c> payload for hub clients (unchanged wire format).</summary>
    internal static Dictionary<string, object> ToHubPayload(DashboardMetricsSnapshot snapshot)
    {
        var stats = snapshot.Statistics;
        return new Dictionary<string, object>
        {
            ["servers"] = stats.Servers,
            ["recurring"] = stats.Recurring,
            ["enqueued"] = stats.Enqueued,
            ["processing"] = stats.Processing,
            ["succeeded"] = stats.Succeeded,
            ["failed"] = stats.Failed,
            ["scheduled"] = stats.Scheduled,
            ["deleted"] = stats.Deleted,
        };
    }
}
