# Features

Back to the [README](../../README.md). Every feature at a glance, with links to the pages that describe it in detail.

| Feature | Description |
|---------|-------------|
| Job monitoring | Job state pages, batch operations, servers, retries |
| Recurring jobs | Create, edit, start, and stop recurring jobs from the UI, with a client-side id/name filter |
| Schedule Heatmap | Visualize recurring-job density by queue × day × hour and plan cron around real demand — Planner, Punchcard, Calendar, Concurrency, Recommendations, and more (see [Schedule Heatmap](heatmap.md)) |
| Job Builder | Create & schedule jobs **with typed arguments** — guided parameter form (+ JSON), method discovery, overload-safe resolution, and one-off enqueue at `/jobs/enqueue` — see [Job Builder](job-builder.md) |
| Visual cron builder | Build cron schedules field-by-field with a human-readable description and next-run preview |
| Console output | Logs, progress bars, and colors (Hangfire.Console-compatible API) |
| Job tags | Tagging and tag cloud (Hangfire.Tags-compatible storage) |
| Throttling | Semaphore occupancy, held mutexes, and rate-limit windows with orphaned-holder detection — appears automatically when the host uses [Hangfire.Throttling](https://docs.hangfire.io/en/latest/background-processing/throttling.html) |
| Job dependency graph | Continuation pipeline visualization on the Job Details page (with "Load more" expansion) |
| Retry summary | Inline banner above state history showing retry count + exception consistency |
| Stack trace links | File references in stack traces become clickable links to GitHub/GitLab/Azure DevOps/Bitbucket/local IDE |
| Job arguments | Job lists show each job's arguments, and the Job Details page renders the full method call with type-aware argument formatting — including jobs whose assembly the dashboard does not reference |
| Global search | Search by job ID, name, queue, tag, exception text, or job arguments (`args:value`), and filter by exception (the Exception filter) |
| Advanced filters | Filter by date, duration, state, server, and more |
| Analytics | Throughput, latency, failures, queue health (requires storage adapter — see [Packages](../../README.md#packages)) |
| Health checks | `/healthz` endpoints (liveness, readiness, full report) + at-a-glance hero card on Home. No exception messages in responses, and the full report requires the dashboard's authorization — see [Health Checks](health-checks.md) |
| Queue pause / maintenance | Pause individual queues or enable global maintenance mode — see [Operations](operations.md) |
| Audit log | Every admin action recorded (who, when, what) — filterable, paged — see [Operations](operations.md) |
| Failure grouping | The Failed page groups failed jobs by exception, with counts and linkable groups — see [Grouping on the Failed page](operations.md#grouping-on-the-failed-page). The opt-in fingerprint filter gives exact counts — see [Failure fingerprints](operations.md#failure-fingerprints) |
| Realtime updates | Live metrics via SignalR, fed in-process to the dashboard's own pages |
| Authorization | Local-only default, also when no filter is configured; per-user read-only via `IsReadOnlyFunc`; optional async filters and `LoginPath` redirect — see [Authorization](../../README.md#authorization) |
| Slow-storage resilience | Bounded concurrent storage reads and refresh loops that back off while storage is failing — see [Polling and Slow Storage](../guides/polling-and-slow-storage.md) |
| Theming | Dark, light, or auto |
| Responsive layout | Fits phones to ultrawide monitors: table columns hide by priority as the table narrows, rows become cards on phones, an icon-rail sidebar on laptops, and centred content on wide screens |
| Tech | Blazor Server, Bootstrap 5, Chart.js |

## Feature pages

- [Recurring Schedule Heatmap](heatmap.md)
- [Job Builder](job-builder.md)
- [Job Dependency Graph](job-dependency-graph.md)
- [Stack Trace Source Links](stack-trace-links.md)
- [Health Checks](health-checks.md)
- [Operations](operations.md): queue pause, maintenance mode, failure fingerprints, audit log
