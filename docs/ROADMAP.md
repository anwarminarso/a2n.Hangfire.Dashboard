# Roadmap — a2n.Hangfire.Dashboard

## Vision

A modern, open-source alternative Hangfire dashboard that replaces the built-in dashboard and popular plugins (Console, Tags, RecurringJobAdmin) in a single package — with realtime updates, advanced search, and a clean UI.

## Core Principles

1. **Drop-in replacement** — swap the NuGet package, zero code changes
2. **Data backward compatible** — reads historical data from original plugins without migration
3. **No assembly conflict** — identical namespaces, replaces (not coexists with) original packages
4. **Modern stack** — Blazor Server, Bootstrap 5, Chart.js, SignalR, .NET 8/9/10
5. **Open source** — free alternative to Hangfire Pro dashboard features

## Tech Stack

| Layer | Technology |
|-------|-----------|
| UI Framework | Blazor Server (Interactive) |
| CSS Framework | Bootstrap 5.3 + Bootstrap Icons |
| Charts | Chart.js + chartjs-plugin-streaming + moment.js |
| Realtime | ASP.NET Core SignalR |
| Theme | Bootstrap dark/light mode with localStorage persistence |
| Targets | .NET 8, .NET 9, .NET 10 |

## Solution Structure

```
src/
├── a2n.Hangfire.Dashboard/                ← Core: Blazor UI + SignalR + interfaces
├── a2n.Hangfire.Dashboard.SqlServer/      ← Storage adapter: Dapper + T-SQL
├── a2n.Hangfire.Dashboard.PostgreSql/     ← Storage adapter: Dapper + Npgsql
├── a2n.Hangfire.Console/                  ← Drop-in replacement (namespace: Hangfire.Console)
└── a2n.Hangfire.Tags/                     ← Drop-in replacement (namespace: Hangfire.Tags)

tests/
├── a2n.Hangfire.Dashboard.Tests/
├── a2n.Hangfire.Dashboard.SqlServer.Tests/
├── a2n.Hangfire.Dashboard.PostgreSql.Tests/
├── a2n.Hangfire.Console.Tests/
└── a2n.Hangfire.Tags.Tests/

samples/
└── SampleApp/                             ← Integration test app (net10.0)
```

---

## Phase 1 — Foundation & Parity ✅

**Goal**: Feature parity with the built-in dashboard plus integrated plugins.

- ✅ Blazor Server rewrite with full interactive SSR
- ✅ All job state pages (Enqueued, Processing, Scheduled, Succeeded, Failed, Deleted, Awaiting)
- ✅ Job Details with state history timeline
- ✅ Batch operations (requeue, delete) with checkbox selection
- ✅ Recurring job management (full CRUD + Start/Stop)
- ✅ Console integration (realtime logs, progress bars, colored text)
- ✅ Tags integration (tag cloud, search by tag, auto-tagging)
- ✅ Realtime charts (Chart.js + SignalR streaming)
- ✅ Dark/Light/Auto theme toggle
- ✅ Responsive layout (sidebar + offcanvas mobile)
- ✅ Pagination, relative timestamps, server/worker labels

---

## Phase 2 — Differentiation ✅

**Goal**: Features that don't exist in the built-in dashboard.

### 2.1 Search & Filter ✅
- ✅ Global search bar in header (all pages)
- ✅ Dedicated search page with form + advanced filters
- ✅ Search by job ID (direct lookup), name (substring), queue, tag, exception text
- ✅ Advanced filters: date range, state, server, duration, tags (multi-select OR), queue, recurring job ID
- ✅ Filter presets (save/load/delete via localStorage)
- ✅ Form validation, active parameter badges, read-only result summary
- ✅ Pagination with page size selector (10, 20, 50, 100, 500)

### 2.2 Razor Class Library Conversion ✅
- ✅ Converted from web application to Razor Class Library (dll)
- ✅ Custom middleware pipeline (`app.Map` + `DashboardMiddleware` + `FrameworkScriptMiddleware`)
- ✅ Static assets served via embedded resources (`_content/*`)
- ✅ `_framework/blazor.web.js` served via custom middleware in branched pipeline
- ✅ NuGet `buildTransitive` props for automatic `RequiresAspNetWebAssets` propagation
- ✅ True NuGet drop-in: add package reference + `AddHangfireDashboardUI()` + `UseHangfireDashboardUI()`
- ✅ Relative URL links for path prefix compatibility

### 2.3 Storage-Specific Query Optimization ✅

**Architecture**: Separate NuGet packages per storage adapter.

**Interfaces** (in core project):
- `IStorageQueryProvider` — search, filter, pagination
- `IStorageMetricsProvider` — analytics/metrics (optional, dashboard gracefully degrades)

**Completed:**
- ✅ `IStorageQueryProvider` interface (unified: 5 async methods)
- ✅ `IStorageMetricsProvider` interface with 16 async methods
- ✅ Supporting DTOs (`PagedResult<T>`, `JobFilterCriteria`, `JobDurationStatsDto`, etc.)
- ✅ `GenericQueryProvider` fallback (IMonitoringApi + client-side filtering)
- ✅ `a2n.Hangfire.Dashboard.SqlServer` (Dapper + T-SQL, JSON_VALUE, PERCENTILE_CONT)
- ✅ `a2n.Hangfire.Dashboard.PostgreSql` (Dapper + Npgsql, ->> operator, PERCENTILE_CONT)
- ✅ LIKE/ILIKE pattern sanitization
- ✅ Parameterized queries only — zero string concatenation
- ✅ DI registration via `DashboardStorageOptionsBuilder` pattern
- ✅ Unified multi-stage `GetJobsWithFilterAsync` (absorbs name/exception/content search)
- ✅ Simplified SearchService (build criteria → delegate to provider)
- ✅ Graceful degradation when `IStorageMetricsProvider` not registered

**Registration:**
```csharp
services.AddHangfireDashboardUI(options =>
{
    options.UseSqlServerStorage(connectionString);   // registers both interfaces
    // or
    options.UsePostgreSqlStorage(connectionString);  // registers both interfaces
    // or nothing → GenericQueryProvider fallback, analytics hidden
});
```

### 2.4 Analytics Dashboard ✅

**Goal**: Analytics pages with Chart.js visualizations. Only visible when `IStorageMetricsProvider` is registered.

**Completed:**
- ✅ Analytics Overview (`/analytics`) — stat cards + throughput + failure rate + hourly activity + top job types
- ✅ Performance (`/analytics/performance`) — duration trend (p50/p95/p99) + duration by type + queue latency + slowest jobs + state timings
- ✅ Failures (`/analytics/failures`) — failure rate by type + top exceptions (doughnut) + retry distribution + recent failures
- ✅ Queues (`/analytics/queues`) — queue throughput (multi-line) + queue status table + server utilization
- ✅ Recurring Health (`/analytics/recurring`) — health status table + execution history sparkline
- ✅ Time range selector (1h, 6h, 24h, 7d, 30d, custom with max 90 days)
- ✅ Realtime update via SignalR when range = "Last 1h" (Live/Reconnecting badges)
- ✅ Graceful degradation: analytics nav hidden + fallback page with install instructions
- ✅ Responsive layout (2-col desktop, 1-col tablet, stacked mobile)
- ✅ Chart.js interop with theme-aware colors (MutationObserver for dark/light switch)
- ✅ AnalyticsBroadcastService (5s interval SignalR push)

### 2.5 Enhanced Job Details
- [ ] Job dependency graph (continuations visualized)
- [ ] Retry history with diff
- [ ] Job execution duration chart (historical)

### Bug Fixes (post v2.0)
- ✅ Fixed realtime chart on Home page not animating (chartjs-plugin-streaming globally disabled by analyticsCharts.js)
- ✅ Fixed PostgreSQL search "storage error" (jsonb column requires `::text` cast for ILIKE)
- ✅ Added logging + fallback to scan-based search when dedicated provider fails
- ✅ Fixed search secondary filters (duration, date, state) not applied when using dedicated provider
- ✅ Added filter-only search support (no text query required if filters are active)
- ✅ Fixed search pagination resetting filter state (switched from URL navigation to in-memory state)
- ✅ Fixed scroll position reset on page/pageSize change (smooth scroll to results)
- ✅ Fixed Analytics "Live" badge vertical alignment (removed nested flex wrapper from TimeRangeSelector)
- ✅ Fixed Analytics charts showing only 1 data point for "Last 1h" (expanded query window to 6h with hourly interval)

### v2.1 — Search & Query Refactor ✅
- ✅ Unified `IStorageQueryProvider` interface (8 methods → 5 methods)
- ✅ Merged `SearchJobsByNameAsync`, `SearchFailedByExceptionAsync`, `SearchByContentAsync` into unified `GetJobsWithFilterAsync`
- ✅ Extended `JobFilterCriteria` with `JobNamePattern`, `ExceptionPattern`, `ContentPattern`, `States` (multi-state)
- ✅ Multi-stage query approach in `GetJobsWithFilterAsync` (basic filters → state data → cross-table → content CTE)
- ✅ Simplified `SearchService` (1536 lines → ~280 lines) — build criteria + delegate to provider
- ✅ Removed scan-based fallback and N+1 `ApplySecondaryFilters` from SearchService
- ✅ Tags page now uses `IStorageQueryProvider` (GetJobsByTagAsync + GetTagCloudAsync) instead of TagsDataReader
- ✅ Tag cloud count now matches actual job count (INNER JOIN to job table, excludes expired)
- ✅ Filtered out numeric-only entries from tag cloud display
- ✅ `JobNameHelper` — unified job name extraction with `JobDisplayNameAttribute` support
- ✅ All job list pages use `JobNameHelper.GetDisplayName()` (fallback to InvocationData when assembly unavailable)
- ✅ Search result filter badges now have distinct colors per filter type
- ✅ Fixed SQL Server content search (bracket escaping conflict with ESCAPE clause)
- ✅ Fixed SQL Server metrics provider GROUP BY full InvocationData (now extracts Type+Method via JSON_VALUE)
- ✅ Fixed SQL Server tag queries (TRY_CAST for non-numeric safety)

---

## Phase 3 — Extensibility & Integration

**Goal**: Features for extensibility and integration with external tools.

### 3.1 Notifications & Alerts
- [ ] Webhook notifications (Slack, Teams, Discord, generic HTTP)
- [ ] Browser push notifications
- [ ] Configurable alert rules
- [ ] Notification history log

### 3.2 API & Metrics
- [ ] REST API for job data (optional package)
- [ ] Prometheus /metrics endpoint
- [ ] Export to CSV/JSON

### 3.3 Customization
- [ ] Theming (custom colors, logo)
- [ ] Configurable homepage widgets
- [ ] Hide/show pages via options

---

## Stretch Goals

Items that may be implemented if there is demand, but are not prioritized.

- [ ] Job Execution Timeline — Gantt-like view for concurrent job execution + server workload distribution
- [ ] Visual cron builder component (interactive UI)
- [ ] Historical server utilization & queue depth (requires custom snapshot storage)
- [ ] Tag-based Analytics — filter all analytics by tag, failure rate per tag, duration per tag, tag cloud with metrics overlay

---

## Milestone Targets

| Version | Scope | Status |
|---------|-------|--------|
| v0.1–v0.7 | Foundation (setup → grid standardization) | ✅ Done |
| v1.0 | Phase 1 complete — full parity + realtime | ✅ Done |
| v1.1 | Global search & advanced filters | ✅ Done |
| v1.2 | Razor Class Library conversion | ✅ Done |
| v1.3 | Storage query interfaces + GenericQueryProvider fallback | ✅ Done |
| v1.4 | SQL Server adapter (`a2n.Hangfire.Dashboard.SqlServer`) | ✅ Done |
| v1.5 | PostgreSQL adapter (`a2n.Hangfire.Dashboard.PostgreSql`) | ✅ Done |
| v1.6 | Analytics Dashboard (Overview + Performance + Failures + Queues + Recurring) | ✅ Done |
| v2.0 | Phase 2 complete | ✅ Done |
| v2.1 | Search & query refactor + JobDisplayName + SQL Server fixes | ✅ Done |
| v3.0 | Phase 3 — extensibility & integration | Planned |

---

## Contributing

See [CONTRIBUTING.md](../CONTRIBUTING.md) for guidelines on how to contribute to this project.
