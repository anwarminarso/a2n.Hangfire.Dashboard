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
/// <remarks>
/// The loop is a fixed-cadence <see cref="DashboardPoller"/>: a read that fails, or that takes the
/// whole interval, backs the next one off up to <see cref="PollingOptions.MaxBackoffInterval"/>
/// instead of starting another read straight away (issue #64).
/// </remarks>
public class MetricsBroadcastService : BackgroundService
{
    private readonly DashboardMetricsFeed _feed;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardSubscriptionTracker _subscriptions;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<MetricsBroadcastService> _logger;

    public MetricsBroadcastService(
        DashboardMetricsFeed feed,
        IHubContext<DashboardHub> hubContext,
        DashboardSubscriptionTracker subscriptions,
        DashboardUIOptions options,
        ILogger<MetricsBroadcastService> logger)
    {
        _feed = feed;
        _hubContext = hubContext;
        _subscriptions = subscriptions;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read once at start: UseHangfireDashboardUI has applied the host's options by now.
        var interval = _feed.Interval;
        _logger.LogInformation("MetricsBroadcastService started. Broadcasting every {Interval}ms", interval.TotalMilliseconds);

        var poller = new DashboardPoller("metrics broadcast", interval, BroadcastMetrics,
            _options?.Polling, logger: _logger)
        {
            FixedCadence = true,
            StopOnObjectDisposed = false,
        };
        return poller.RunUntilStoppedAsync(stoppingToken);
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
