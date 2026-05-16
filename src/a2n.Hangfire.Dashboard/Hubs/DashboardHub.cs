using Microsoft.AspNetCore.SignalR;

namespace a2n.Hangfire.Dashboard.Hubs;

/// <summary>
/// SignalR hub for realtime dashboard updates.
/// </summary>
public class DashboardHub : Hub
{
    /// <summary>
    /// Client can subscribe to metric updates.
    /// </summary>
    public async Task SubscribeToMetrics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "metrics");
    }

    /// <summary>
    /// Client can unsubscribe from metric updates.
    /// </summary>
    public async Task UnsubscribeFromMetrics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "metrics");
    }

    /// <summary>
    /// Client can subscribe to analytics updates (realtime charts when time range is "Last 1h").
    /// </summary>
    public async Task SubscribeToAnalytics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "analytics");
    }

    /// <summary>
    /// Client can unsubscribe from analytics updates.
    /// </summary>
    public async Task UnsubscribeFromAnalytics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "analytics");
    }
}
