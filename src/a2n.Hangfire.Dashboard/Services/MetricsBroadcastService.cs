using a2n.Hangfire.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Background service that periodically broadcasts metrics to connected dashboard clients.
/// Replaces the polling mechanism of the original Hangfire dashboard.
/// </summary>
public class MetricsBroadcastService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ILogger<MetricsBroadcastService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    public MetricsBroadcastService(
        IServiceScopeFactory scopeFactory,
        IHubContext<DashboardHub> hubContext,
        ILogger<MetricsBroadcastService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MetricsBroadcastService started. Broadcasting every {Interval}s", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BroadcastMetrics(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Error broadcasting metrics");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task BroadcastMetrics(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<HangfireMonitorService>();

        var stats = monitor.GetStatistics();

        var metrics = new Dictionary<string, object>
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

        await _hubContext.Clients.Group("metrics").SendAsync("MetricsUpdated", metrics, ct);
    }
}
