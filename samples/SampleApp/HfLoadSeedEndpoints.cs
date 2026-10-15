using System.Net;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using SampleApp.SharedJobs;

/// <summary>
/// Seed endpoints for the load-test harness (<c>tests/load</c>), which writes rows directly on SQL
/// Server and PostgreSQL but can't reach InMemory storage. Mapped only when
/// <c>HfLoad:SeedEndpoints=true</c>, and they answer local requests only. They create jobs, so
/// never enable them on a shared host.
/// </summary>
internal static class HfLoadSeedEndpoints
{
    private const string IdleQueue = "hfload-idle";
    private const string ServedQueue = "default";

    public static void MapHfLoadSeedEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/hfload").AddEndpointFilter(async (context, next) =>
        {
            var remote = context.HttpContext.Connection.RemoteIpAddress;
            return remote is not null && IPAddress.IsLoopback(remote)
                ? await next(context)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        // StressJobs.Fail jobs for the server to fail for real, so the fingerprint filter stores
        // the real fingerprint. kind fixes one kind (one big group); otherwise i % kinds.
        group.MapPost("/failures", (int count, int? kinds, int? kind, IBackgroundJobClient client) =>
        {
            var spread = Math.Clamp(kinds ?? StressJobs.FailureKinds, 1, StressJobs.FailureKinds);
            for (var i = 0; i < count; i++)
            {
                var k = kind ?? i % spread;
                client.Enqueue<StressJobs>(j => j.Fail(k));
            }
            return Results.Ok(new { enqueued = count });
        });

        // Jobs created straight into a final or idle state, without the global filters, so the
        // Failed ones carry no stored fingerprint and nothing retries them.
        group.MapPost("/volume", (int count, JobStorage storage) =>
        {
            var client = new BackgroundJobClient(storage, new JobFilterCollection());
            var written = new Dictionary<string, int>();
            for (var i = 0; i < count; i++)
            {
                var n = i;
                var (name, state, method) = (i % 100) switch
                {
                    < 70 => ("Succeeded", (IState)new SucceededState(null, n % 900 + 5, n % 5000 + 10), "succeed"),
                    < 82 => ("Failed", new FailedState(new TimeoutException("The volume seed gateway did not answer.")), "fail"),
                    < 92 => ("Deleted", new DeletedState(), "succeed"),
                    < 97 => ("Scheduled", new ScheduledState(TimeSpan.FromDays(7)), "succeed"),
                    _ => ("Enqueued", new EnqueuedState(IdleQueue), "succeed"),
                };
                var job = method == "fail"
                    ? Job.FromExpression<StressJobs>(j => j.Fail(n))
                    : Job.FromExpression<StressJobs>(j => j.Succeed(n));
                client.Create(job, state);
                written[name] = written.GetValueOrDefault(name) + 1;
            }
            return Results.Ok(written);
        });

        // StressJobs jobs still waiting in the queue /failures uses or processing, so the harness
        // knows when the server has failed them. Only StressJobs count: SampleApp's other jobs sit
        // in queues its server doesn't listen to, or run for minutes, and would never reach 0.
        group.MapGet("/pending", (JobStorage storage) =>
        {
            // Paged by the actual counts: some storages compute from + count and return nothing for
            // a page size of int.MaxValue.
            var monitoring = storage.GetMonitoringApi();
            var enqueuedCount = (int)Math.Min(monitoring.EnqueuedCount(ServedQueue), int.MaxValue / 2);
            var processingCount = (int)Math.Min(monitoring.ProcessingCount(), int.MaxValue / 2);
            var enqueued = enqueuedCount == 0 ? 0 : monitoring.EnqueuedJobs(ServedQueue, 0, enqueuedCount)
                .Count(j => j.Value?.Job?.Type == typeof(StressJobs));
            var processing = processingCount == 0 ? 0 : monitoring.ProcessingJobs(0, processingCount)
                .Count(j => j.Value?.Job?.Type == typeof(StressJobs));
            return Results.Ok(new { pending = enqueued + processing, failed = monitoring.FailedCount() });
        });
    }
}
