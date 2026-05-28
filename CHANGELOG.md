# Changelog

## Unreleased

### Security

- **BREAKING:** `DashboardUIOptions.Authorization` now defaults to `LocalRequestsOnlyAuthorizationFilter` (same as Hangfire built-in dashboard). Remote hosts receive HTTP 401 unless you set `Authorization = []` or add your own filters.
- SignalR hub and Blazor circuit paths now require the same authorization as dashboard pages.
- Schema/table identifiers from configuration are validated (`^[a-zA-Z_][a-zA-Z0-9_]*$`) before use in SQL.

### Added

- `IDashboardAsyncAuthorizationFilter` and Hangfire filter adapters.
- `DashboardUIOptions.LoginPath` for redirecting unauthenticated users to a login page.
- `samples/SampleAppAuth` — cookie authentication demo with login form.
- `MetricsQueryCache` with per-key stampede protection for analytics queries.

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
