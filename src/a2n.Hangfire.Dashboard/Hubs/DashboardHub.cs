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
}
