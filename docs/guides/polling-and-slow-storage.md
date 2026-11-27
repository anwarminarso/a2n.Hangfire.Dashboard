# Polling and Slow Storage

Back to the [README](../../README.md).

## Which connection each read uses

**Which connection each read uses.** The connection string passed to `UseSqlServerStorage()` / `UsePostgreSqlStorage()` is used only by the adapter's own queries: search, the Analytics pages, the heatmap's Historical source and estimated durations, plus the failed-job list on PostgreSQL. Everything else goes through Hangfire's `JobStorage` and therefore the host's main Hangfire connection pool, shared with the workers. That includes the realtime counters, the other job lists, job details, recurring jobs, servers and queues, and the ad-hoc demand rollup (apart from its failed-job reads on PostgreSQL). Giving the adapter a separate connection string (for example one with a small `Maximum Pool Size`) isolates those adapter queries only. To keep the remaining dashboard load low:

- `DashboardUIOptions.StatsPollingInterval` sets how often the realtime counters are read: one read per interval for all open tabs, and none while no tab is open.
- `DashboardUIOptions.Heatmap.DemandRollupEnabled = false` stops the ad-hoc demand poll (every `Polling.RollupPollInterval`, 60 seconds by default, runs even with no tab open). The heatmap's Ad-hoc and Combined layers then get no new data. It also doesn't run when the heatmap is disabled (`Heatmap.Enabled = false`).
- `DashboardUIOptions.Polling` bounds how many storage calls the dashboard runs at once and how its pages refresh; see [Polling options](#polling-options) below.

## Polling options

Hangfire's storage APIs are synchronous, so each dashboard read holds a thread-pool thread until storage answers, including any wait for a pooled connection. `DashboardUIOptions.Polling` keeps that cost bounded when storage is slow or down, so open tabs can't starve the host's thread pool or take the connections the Hangfire server needs for its heartbeat ([#64](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/64)):

- **A process-wide limit on concurrent storage calls.** At most `MaxConcurrentStorageCalls` (default 8) of the dashboard's reads run at once, across every tab. Further reads wait without holding a thread. A read keeps its slot until storage actually answers, even after the page has stopped waiting, so the number of threads the dashboard can block stays at the limit. The limit covers the realtime counters, the health checks, the Dashboard page's history chart, and the reads of the job lists, Job Details, Processing, Queues, Retries, Recurring Jobs, the Throttling semaphore page, the sidebar's tag count and the live job console. Search, admin actions (requeue, delete, trigger), the Analytics queries and the background rollups don't go through it.
- **Refresh loops never overlap.** Each page schedules its next refresh only once the previous one has finished. A refresh that fails, or takes at least `SlowCallThreshold` (default 5 s), doubles the delay before the next one, up to `MaxBackoffInterval` (default 60 s). The first refresh that succeeds in time restores the normal interval. The change into and out of back-off is logged once, at Warning and Information, rather than on every tick.
- **Loops pause while the browser is disconnected** and catch up as soon as it reconnects.

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    Polling = new PollingOptions
    {
        MaxConcurrentStorageCalls  = 8,                          // dashboard-wide, all tabs
        SlowCallThreshold          = TimeSpan.FromSeconds(5),
        MaxBackoffInterval         = TimeSpan.FromSeconds(60),
        FastPageRefreshInterval    = TimeSpan.FromSeconds(3),    // Processing, Job Details, Queues
        PageRefreshInterval        = TimeSpan.FromSeconds(5),    // job lists, Retries, maintenance banner
        SlowPageRefreshInterval    = TimeSpan.FromSeconds(10),   // Recurring Jobs, sidebar counts
        ConsoleRefreshInterval     = TimeSpan.FromSeconds(2),    // live job console
        HealthCardRefreshInterval  = TimeSpan.FromSeconds(10),   // Home page health card
        HealthReportCacheTtl       = TimeSpan.FromSeconds(5),    // /healthz, IHealthCheck, health card
        AnalyticsBroadcastInterval = TimeSpan.FromSeconds(5),    // live Analytics ("Last 1h")
        RollupPollInterval         = TimeSpan.FromSeconds(60),   // demand rollup / rollup collector
    },
});
```

The defaults are the intervals earlier versions used. Setting a refresh, broadcast or poll interval to `TimeSpan.Zero` turns that loop off: the page still loads when opened but no longer refreshes on its own (the health card keeps its Refresh button). For `AnalyticsBroadcastInterval` it stops the live updates on Analytics, and for `RollupPollInterval` it stops the rollups, so the heatmap's Ad-hoc layer (SQL Server, PostgreSQL) or Analytics (rollup-based storages) gets no new data. The realtime counters keep following `StatsPollingInterval`. The Throttling pages refresh every 6 seconds, in step with their shared snapshot.

The background loops (realtime counters, live Analytics and the rollups) keep a fixed cadence: their interval runs from the start of one read to the start of the next. A read that fails, or takes the whole interval, backs the loop off in the same way, instead of starting the next read straight away.
