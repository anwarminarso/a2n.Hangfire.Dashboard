using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SampleApp.SharedJobs;
using SampleAppSpa.Hubs;

namespace SampleAppSpa.Controllers;

/// <summary>
/// A regular ASP.NET Core API controller living in the host app (root routing).
/// Demonstrates that attribute-routed controllers coexist with the dashboard branch
/// and the SPA fallback. Enqueuing a job also pushes a notification over the host's
/// own SignalR hub.
/// </summary>
[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IBackgroundJobClient _jobs;
    private readonly IHubContext<NotificationsHub> _hub;
    private readonly JobStorage _storage;

    public JobsController(IBackgroundJobClient jobs, IHubContext<NotificationsHub> hub, JobStorage storage)
    {
        _jobs = jobs;
        _hub = hub;
        _storage = storage;
    }

    /// <summary>Returns the most recent succeeded jobs from the monitoring API.</summary>
    [HttpGet]
    public IActionResult Recent()
    {
        IMonitoringApi monitor = _storage.GetMonitoringApi();
        var succeeded = monitor.SucceededJobs(0, 10)
            .Select(j => new
            {
                id = j.Key,
                job = j.Value.Job?.ToString() ?? "(unknown)",
                succeededAt = j.Value.SucceededAt,
            });
        return Ok(succeeded);
    }

    /// <summary>Enqueues a fire-and-forget job and notifies all SPA clients over SignalR.</summary>
    [HttpPost("enqueue")]
    public async Task<IActionResult> Enqueue()
    {
        var id = _jobs.Enqueue<SampleJobs>(x => x.SimpleJob());
        await _hub.Clients.All.SendAsync("ReceiveMessage", "system", $"Enqueued job {id}");
        return Ok(new { id });
    }
}
