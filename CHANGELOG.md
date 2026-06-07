# Changelog

## Unreleased

### Added — v2.3.x Operations slice

- **Audit log** — every admin action (job requeue/delete, batch ops, recurring CRUD, recurring stop/start, queue pause/resume, maintenance toggles) is recorded with timestamp, user, client IP, target, and metadata. New page at `/audit` with filter by action prefix, user, target. Storage uses Hangfire's KV primitives (`audit:log` set + `audit:entry:{id}` hashes) — no schema changes. Configurable retention (`Retention`, default 30 days) and max entries (`MaxEntries`, default 10,000).
- **Queue pause / resume** — toggle individual queues from the new `/queues` page. While paused, Hangfire workers reschedule jobs (default +30s) instead of executing them. Requires registering the server filter in the host app: `config.UseDashboardQueuePauseFilter()`.
- **Maintenance mode** — global pause-all toggle from the Queues page header. A persistent yellow banner with a `Manage →` link is rendered on every dashboard page. Optional reason field surfaced in the banner.
- `DashboardUIOptions.AuditLog` (`Enabled`, `Retention`, `MaxEntries`).
- `DashboardUIOptions.QueueOperations` (`Enabled`, `Behavior` — Reschedule or Requeue, `RescheduleDelay`, `PauseStateCacheTtl`).
- `AuditLogService` and `QueueOperationsService` — DI-registered (scoped) services usable from host code. Action attribution uses a per-circuit `AuditActorAccessor` populated from `AuthenticationStateProvider`, so audit entries record the actual signed-in user even though Blazor circuit actions run without an `HttpContext`.
- `QueuePauseServerFilter` — Hangfire `IElectStateFilter` that intercepts the transition into `Processing` and reschedules/re-enqueues jobs on paused queues. It never cancels execution, so paused jobs are never moved to `DeletedState` (no data loss). Pause/maintenance state is read through a per-server cache (default 2s TTL).
- `QueueOperationsStateCache` — process-wide short-TTL cache shared by the nav menu, maintenance banner, and Queues page so pause/maintenance state is read once per interval instead of per circuit.
- New nav entry **Audit Log** under Management; new nav entry **Queues** with paused-count badge.

## 2.3.0

### Added

- **Health check endpoint** at `/{dashboard}/healthz`, `/healthz/ready`, and `/healthz/full`. Returns a structured JSON `HealthReport` covering storage availability, server liveness, queue depth, stuck processing jobs, last-hour failure rate, and missed recurring schedules. HTTP 200 for `Healthy`/`Degraded`, HTTP 503 for `Unhealthy` (K8s probe convention).
- **Health hero card** on the Home page — at-a-glance traffic light (Healthy / Degraded / Critical) with per-issue descriptions, deep-link actions, and an auto-refresh every 10 seconds. The detailed 8-card stat grid is now collapsible behind a "Detailed metrics" toggle.
- `DashboardUIOptions.HealthCheckAuthorizationMode` (`AllowAnonymous` default for K8s, `LocalOnly`, or `RequireDashboardAuth`).
- `DashboardUIOptions.HealthCheckThresholds` to tune what counts as Degraded vs Unhealthy (queue depth, failure rate, stuck-processing minutes, server heartbeat tolerance, recurring missed tolerance, storage response time).
- `HealthCheckService` (DI-registered) — also usable from host code if you want to wire the report into your own ASP.NET Core HealthCheck pipeline.
- `services.AddHealthChecks().AddHangfireDashboard()` adapter — register the dashboard as a single ASP.NET Core `IHealthCheck` for hosts that prefer a unified `/health` endpoint aggregating multiple dependencies. Per-check status surfaces via `HealthCheckResult.Data`.
- `IDashboardAsyncAuthorizationFilter` and Hangfire filter adapters.
- `DashboardUIOptions.LoginPath` for redirecting unauthenticated users to a login page.
- `samples/SampleAppAuth` — cookie authentication demo with login form.
- `MetricsQueryCache` with per-key stampede protection for analytics queries.

### Security

- **BREAKING:** `DashboardUIOptions.Authorization` now defaults to `LocalRequestsOnlyAuthorizationFilter` (same as Hangfire built-in dashboard). Remote hosts receive HTTP 401 unless you set `Authorization = []` or add your own filters.
- SignalR hub and Blazor circuit paths now require the same authorization as dashboard pages.
- Schema/table identifiers from configuration are validated (`^[a-zA-Z_][a-zA-Z0-9_]*$`) before use in SQL.

### Fixed

- `MetricsQueryCache` no longer removes per-key semaphores after release (fixes TOCTOU stampede race).
- SQL Server percentile queries use `PERCENTILE_CONT ... OVER (PARTITION BY ...)` (valid T-SQL).
- Antiforgery validation skipped for `/_blazor` and `/hubs/*` negotiate endpoints.
- PostgreSQL throughput timeline includes daily counter keys (`stats:*:yyyy-MM-dd`).
- Queue resolution prefers `Job.Queue` job parameter, then legacy dashboard `CurrentQueue` (not Hangfire core), then state JSON on Enqueued/Processing.
- Queue latency metrics read `Latency` from **Succeeded** state (Hangfire stores it there), with queue from `Job.Queue` parameter.
- Recurring job execution history matches `RecurringJobId` job parameters in both plain and JSON-serialized forms (Hangfire 1.8+).
- Public `JobParameterMatching` helper in `a2n.Hangfire.Dashboard.Storage` for storage adapter packages.

### Notes for deployments behind reverse proxy

`LocalRequestsOnlyAuthorizationFilter` checks `Connection.RemoteIpAddress`. Behind a reverse proxy without forwarded headers, the remote address may appear as loopback and **allow all clients**. Configure `UseForwardedHeaders()` and restrict access at the proxy when deploying to production.
