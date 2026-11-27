# Health Checks

Back to the [README](../../README.md).

The dashboard exposes a structured health endpoint suitable for Kubernetes probes, load balancer health checks, and status pages.

| Endpoint | Use case | Checks |
|----------|----------|--------|
| `GET /{dashboard}/healthz` | Liveness probe (K8s) | Storage probe |
| `GET /{dashboard}/healthz/ready` | Readiness probe (K8s) | Storage + servers |
| `GET /{dashboard}/healthz/full` | Status pages, dashboard hero card | All six checks below |

HTTP status follows the K8s convention: `200` for `Healthy` or `Degraded`, `503` for `Unhealthy`.

## Who sees what

| `HealthCheckAuthorizationMode` | `/healthz`, `/healthz/ready` | `/healthz/full` |
|---|---|---|
| `AnonymousProbes` (default) | any caller | dashboard authorization required (`401` otherwise) |
| `AllowAnonymous` | any caller | any caller |
| `LocalOnly` | local requests only | local requests only |
| `RequireDashboardAuth` | dashboard authorization required | dashboard authorization required |

Whatever the mode, a caller who doesn't pass the dashboard's own authorization filters gets only the overall status and each check's name and status — no descriptions, data, version or timings. Exception messages never appear in a response, not even for dashboard users: a failed probe reports the exception type, and the full exception goes to the server log. `LocalOnly` treats a request without a remote address (for example over a Unix socket behind a reverse proxy) as remote. `RequireDashboardAuth` protects nothing if the dashboard itself is open (see [Authorization](../../README.md#authorization)).

## Built-in checks

| Key | What it covers |
|-----|----------------|
| `storage` | Round-trip time of a `GetStatistics()` call to the Hangfire storage backend |
| `servers` | Total vs alive vs stale heartbeat (per `ServerHeartbeatTolerance`) |
| `queue_depth` | Highest queue length compared to warn/critical thresholds |
| `stuck_processing` | Processing jobs older than `StuckProcessingMinutes` (sample size: 200) |
| `failure_rate` | Last-hour failure percentage from Hangfire's hourly counters |
| `recurring_jobs` | Recurring jobs whose `NextExecution` is older than `RecurringMissedTolerance` |

**How a slow storage is reported.** The `storage` probe reuses the realtime counters' latest read when it is younger than `StatsPollingInterval`, and reports how long that read took, so frequent probes add no storage load. Every read the checks make waits at most `HealthThresholds.StorageProbeTimeoutMs` (default 10 000 ms) and goes through the dashboard's storage call limit (see [Polling and Slow Storage](../guides/polling-and-slow-storage.md)), so a stalled storage can't pile up probe threads. When the probe fails or times out, `storage` is `Unhealthy` and the other checks are not run: each is reported `Unhealthy` with `"skipped": true` in its `data`, instead of failing on its own after another timeout. A check other than `storage` that times out is reported `Degraded` with `"timedOut": true`. Reports are cached for `Polling.HealthReportCacheTtl` (default 5 s) and shared by the endpoints, the `IHealthCheck` adapter and the hero card.

## Sample response (`/hangfire/healthz/full`)

```json
{
  "status": "Degraded",
  "version": "2.3.1",
  "timestamp": "2026-06-07T01:08:18.96Z",
  "durationMs": 50,
  "checks": {
    "storage":          { "status": "Healthy",  "data": { "responseTimeMs": 3 } },
    "servers":          { "status": "Degraded", "description": "1 of 2 server(s) have stale heartbeats." },
    "queue_depth":      { "status": "Healthy",  "data": { "maxDepth": 0 } },
    "stuck_processing": { "status": "Healthy",  "data": { "stuckCount": 0, "totalProcessing": 0 } },
    "failure_rate":     { "status": "Degraded", "description": "Failure rate 20.0% in last hour (warn threshold 10%)." },
    "recurring_jobs":   { "status": "Healthy",  "data": { "total": 7, "missed": 0 } }
  }
}
```

## Configuration

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    // AnonymousProbes (default): K8s/LB probes work without auth, the full report needs the
    // dashboard's authorization. See "Who sees what" above for the other modes.
    HealthCheckAuthorizationMode = HealthCheckAuthorization.AnonymousProbes,

    HealthCheckThresholds = new HealthThresholds
    {
        StuckProcessingMinutes      = 30,
        QueueDepthWarn              = 1000,
        QueueDepthCritical          = 10000,
        FailureRatePercent          = 10.0,   // Degraded threshold
        FailureRateCritical         = 25.0,   // Unhealthy threshold
        RecurringMissedTolerance    = TimeSpan.FromMinutes(5),
        ServerHeartbeatTolerance    = TimeSpan.FromSeconds(60),
        StorageResponseTimeWarnMs   = 1000,
        StorageResponseTimeCriticalMs = 5000,
    }
});
```

## Plug into ASP.NET Core HealthChecks

If your host already exposes a unified `/health` endpoint aggregating multiple dependencies (database, message broker, Hangfire, ...), register the dashboard as an `IHealthCheck`:

```csharp
builder.Services.AddHealthChecks()
    .AddHangfireDashboard(tags: new[] { "ready" });

app.MapHealthChecks("/health");                                   // all checks
app.MapHealthChecks("/health/ready",                               // readiness subset
    new() { Predicate = c => c.Tags.Contains("ready") });
```

The adapter reads the same cached full report as the dashboard's own endpoint and hero card, so the results match. It never puts an exception message in its description or data. It also leaves `HealthCheckResult.Exception` empty unless you set `DashboardUIOptions.IncludeExceptionInHealthCheckResult = true`; leave that off if your `/health` endpoint is reachable by anyone who shouldn't see connection details, since response writers such as HealthChecks.UI's `UIResponseWriter` serialize `Exception.Message`. For your own code, `HealthCheckService` offers `CheckLivenessAsync`, `CheckReadinessAsync` and `CheckFullAsync`; the synchronous `CheckLiveness()`, `CheckReadiness()` and `CheckFull()` are kept and block the calling thread until the report is ready.

## Hero card

The Home page shows a top-of-page traffic light (Healthy / Degraded / Critical) with per-issue descriptions and deep-link actions ("View processing →", "View failed →"). It refreshes every `Polling.HealthCardRefreshInterval` (10 seconds by default) and backs off while the report can't be read; the Refresh button reads storage at once. Checks skipped because storage is unreachable aren't listed as separate issues. The detailed 8-card stat grid is now collapsed behind a "Detailed metrics" toggle to keep the hero front-and-center.
