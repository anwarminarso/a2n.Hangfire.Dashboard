using a2n.Hangfire.Dashboard.Services;
using Microsoft.AspNetCore.SignalR;

namespace a2n.Hangfire.Dashboard.Hubs;

/// <summary>
/// SignalR hub for realtime dashboard updates.
/// </summary>
public class DashboardHub : Hub
{
    private readonly DashboardSubscriptionTracker _subscriptions;

    public DashboardHub(DashboardSubscriptionTracker subscriptions)
    {
        _subscriptions = subscriptions;
    }

    /// <summary>
    /// Client can subscribe to metric updates.
    /// </summary>
    public async Task SubscribeToMetrics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "metrics");
        if (Context.Items.TryAdd("metrics", true))
            _subscriptions.AddMetricsSubscriber();
    }

    /// <summary>
    /// Client can unsubscribe from metric updates.
    /// </summary>
    public async Task UnsubscribeFromMetrics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "metrics");
        if (Context.Items.Remove("metrics"))
            _subscriptions.RemoveMetricsSubscriber();
    }

    /// <summary>
    /// Client can subscribe to analytics updates (realtime charts when time range is "Last 1h").
    /// </summary>
    public async Task SubscribeToAnalytics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "analytics");
        if (Context.Items.TryAdd("analytics", true))
            _subscriptions.AddAnalyticsSubscriber();
    }

    /// <summary>
    /// Client can unsubscribe from analytics updates.
    /// </summary>
    public async Task UnsubscribeFromAnalytics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "analytics");
        if (Context.Items.Remove("analytics"))
            _subscriptions.RemoveAnalyticsSubscriber();
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        if (Context.Items.Remove("metrics"))
            _subscriptions.RemoveMetricsSubscriber();

        if (Context.Items.Remove("analytics"))
            _subscriptions.RemoveAnalyticsSubscriber();

        await base.OnDisconnectedAsync(exception);
    }
}
