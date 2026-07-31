using Microsoft.AspNetCore.SignalR;

namespace SampleAppSpa.Hubs;

/// <summary>
/// The host application's OWN SignalR hub — completely separate from the dashboard's
/// Blazor Server circuit (/hangfire/_blazor) and DashboardHub (/hangfire/hubs/dashboard).
/// Mapped at the root at /hubs/notifications so the SPA can subscribe to live updates.
/// </summary>
public class NotificationsHub : Hub
{
    // Clients may broadcast a chat-style message to everyone.
    public Task Broadcast(string user, string message)
        => Clients.All.SendAsync("ReceiveMessage", user, message);
}
