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
├── a2n.Hangfire.Dashboard.Rollup/         ← Rollup metrics for non-SQL storages
├── a2n.Hangfire.Dashboard.Redis/          ← Redis entry point (UseRedisStorage)
├── a2n.Hangfire.Console/                  ← Drop-in replacement (namespace: Hangfire.Console)
└── a2n.Hangfire.Tags/                     ← Drop-in replacement (namespace: Hangfire.Tags)

tests/
├── a2n.Hangfire.Dashboard.Tests/
├── a2n.Hangfire.Dashboard.SqlServer.Tests/
├── a2n.Hangfire.Dashboard.PostgreSql.Tests/
├── a2n.Hangfire.Dashboard.Rollup.Tests/
├── a2n.Hangfire.Console.Tests/
├── a2n.Hangfire.Tags.Tests/
└── load/                                  ← Python load-test tooling

samples/
├── SampleApp/                             ← Integration test app
├── SampleApp.SharedJobs/                  ← Job types shared across samples
├── SampleAppAuth/                         ← Cookie authentication demo
├── SampleAppBlazor/                       ← Hosting inside a Blazor app
├── SampleAppMvc/                          ← Hosting inside an MVC app
├── SampleAppRazor/                        ← Hosting inside a Razor Pages app
├── SampleAppSpa/                          ← Hosting alongside an SPA
└── SampleAppOrig/                         ← Built-in Hangfire dashboard, for comparison
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
- ✅ `a2n.Hangfire.Dashboard.Rollup` (rollup-based metrics for Redis / in-memory / non-SQL)
- ✅ `a2n.Hangfire.Dashboard.Redis` (convenience `UseRedisStorage()` entry point)
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
- ✅ Job dependency graph (continuations visualized) — `JobGraphViewer` on Job Details page
  - Walks up via Awaiting state's `ParentId`, then expands descendants via `Continuations` parameter
  - Edge labels: `on succeeded`, `on deleted`, `on any` (continuation condition)
  - Bounded traversal: `DashboardUIOptions.JobGraphMaxDepth` (default 5) and `JobGraphMaxNodes` (default 30)
  - "Load more" button when truncated — doubles node budget and adds +3 depth per click, capped at 200 nodes / depth 12
  - Click any node to navigate to that job's details; expired/deleted jobs render as dashed placeholders
  - Storage-agnostic — uses `IMonitoringApi.JobDetails` only
- ✅ Retry summary banner — inline pill above state history
  - Shows retry count, exception consistency (same / different / N unique types), with hover tooltip listing distinct exception types
  - Per-attempt badge (`#1`, `#2`, ...) on Failed/Processing state cards
  - Hidden when there are no retries (zero noise for healthy jobs)
- ✅ Stack trace source links — `DashboardUIOptions.SourceLink`
  - Presets: `GitHub`, `GitLab` (with self-hosted host override), `AzureDevOps`, `Bitbucket`, `Local` (vscode://, cursor://, vs://, ...)
  - Custom via `UrlPattern` with `{path}` / `{absolutePath}` / `{line}` placeholders
  - `PathTransform` + `WithPathStrip(folderName)` / `WithPathReplace(regex, repl)` helpers for build-agent path normalization
  - Default null → behavior unchanged; opt-in
  - ~~Retry history with diff~~ — **dropped.** Superseded by the retry summary banner; retried jobs run with identical arguments (so an argument diff is almost always empty), and per-attempt stack traces are already expandable in the state history.
- ~~Job execution duration chart (historical, on Job Details page)~~ — **dropped.** Duplicates `/analytics/performance` (duration trend p50/p95/p99 + duration by type); a per-instance page is the wrong place for type-level aggregates.

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

### v2.1 — Search & Query Refactor ✅- ✅ Unified `IStorageQueryProvider` interface (8 methods → 5 methods)
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

### v2.2 — UX Improvements & Grid Parity ✅
- ✅ Processing page: realtime job progress circle (SVG, color gradient orange→green, requires `UseConsole()`)
- ✅ Processing page: obsolete/expired job handling (colspan, state changed indicator, conditional checkbox)
- ✅ Processing page: server possibly aborted warning (heartbeat threshold check)
- ✅ Deleted page: conditional Exception column (shows exception type when StateData contains Exception)
- ✅ Fetched Jobs page (`/jobs/fetched/{queue}`) — new page matching original Hangfire dashboard
- ✅ Enqueued page: "Fetched" tab shown when fetched count > 0
- ✅ Delete confirmation modal (Bootstrap) on all pages — replaces browser `confirm()`
- ✅ `EnableJobManagement` option (formerly `EnableRecurringJobAdmin`) — toggle Create/Edit/Stop/Start and Enqueue-page visibility (default: true)
- ✅ Mobile offcanvas navigation: auto-close on page navigation (JS interop)
- ✅ Asset cache busting: `?v={version}` query string on custom JS/CSS resources
- ✅ License updated to LGPL-3.0-or-later

### v2.2.1 — Security & Auth Hardening ✅
- ✅ **BREAKING:** `DashboardUIOptions.Authorization` defaults to `LocalRequestsOnlyAuthorizationFilter` (same as Hangfire built-in)
- ✅ `IDashboardAsyncAuthorizationFilter` interface + Hangfire filter adapters
- ✅ `DashboardUIOptions.LoginPath` for redirecting unauthenticated users to a login page
- ✅ SignalR hub and Blazor circuit paths enforce same authorization as dashboard pages
- ✅ Schema/table identifiers validated (`^[a-zA-Z_][a-zA-Z0-9_]*$`) before use in SQL
- ✅ Antiforgery validation skipped for `/_blazor` and `/hubs/*` negotiate endpoints
- ✅ `MetricsQueryCache` with per-key stampede protection (fixes TOCTOU race)
- ✅ `samples/SampleAppAuth` — cookie authentication demo with login form
- ✅ SQL Server: `PERCENTILE_CONT ... OVER (PARTITION BY ...)` fix (valid T-SQL)
- ✅ PostgreSQL: throughput timeline includes daily counter keys (`stats:*:yyyy-MM-dd`)
- ✅ Queue resolution: prefers `Job.Queue` parameter → legacy `CurrentQueue` → state JSON
- ✅ Queue latency: reads `Latency` from Succeeded state (where Hangfire stores it)
- ✅ Recurring job execution history: matches `RecurringJobId` in both plain and JSON-serialized forms
- ✅ Public `JobParameterMatching` helper in `a2n.Hangfire.Dashboard.Storage`
- ✅ Analytics: `await` tasks instead of `.Result` after `WhenAll` (async best practice)
- ✅ Collapsible sidebar navigation groups

### v2.3 — Operational Visibility & Controls ✅

**Goal**: Move from "viewer you open when something breaks" to "first-class operational tool" — at-a-glance health, daily ops controls, and richer job details.

> **v2.3.0 shipped ✅** — health checks, health hero card, queue pause/resume, maintenance mode, and audit log are all released.

> **v2.3.1 shipped ✅** — patch release restoring realtime analytics on SQL Server (the `GetQueueDepthSnapshot`/`GetQueueThroughput` queries put a subquery in the `GROUP BY` list, hitting SQL Server error 144 and silently killing the analytics broadcast). The `AnalyticsBroadcastService` loop also moved to a `PeriodicTimer` with the three metrics queries running concurrently (`Task.WhenAll`) so the SignalR push holds its ~5s cadence under load instead of drifting. NuGet packages now ship XML API documentation.

> **Note:** The notifications, integrations, and customization work originally scoped under "v2.3.x" was promoted to its own themed minor releases. **v2.5 now ships the Recurring Schedule Heatmap** ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)); the originally-planned **Notifications & alert rules has been deferred to the Stretch / Backlog** (not mandatory, no demand, superseded by the v2.6 Prometheus `/metrics` endpoint — see [docs/proposals/notifications-alert-rules.md](proposals/notifications-alert-rules.md)). **v2.6 (Integrations)** and **v2.7 (Customization)** keep their version numbers.

#### Health Check ✅
- ✅ HTTP endpoints `/{dashboard}/healthz` (liveness), `/healthz/ready` (readiness), `/healthz/full` (full report)
- ✅ Six built-in checks: storage probe, server liveness, queue depth, stuck processing, last-hour failure rate, recurring missed schedules
- ✅ HTTP 200 for `Healthy`/`Degraded`, HTTP 503 for `Unhealthy` (K8s probe convention)
- ✅ `DashboardUIOptions.HealthCheckAuthorizationMode` (`AllowAnonymous` default for K8s probes, `LocalOnly`, or `RequireDashboardAuth`)
- ✅ `DashboardUIOptions.HealthCheckThresholds` for tuning Degraded vs Unhealthy boundaries
- ✅ `HealthCheckService` (DI-registered, reusable from host code)
- ✅ ASP.NET Core `IHealthCheck` adapter via `services.AddHealthChecks().AddHangfireDashboard()` for unified `/health` endpoints

#### Health Hero Card ✅
- ✅ Top-of-page traffic light (Healthy / Degraded / Critical) on the Home page
- ✅ Per-issue descriptions with deep-link actions (e.g., "View processing →")
- ✅ Auto-refresh every 10s, manual refresh button, relative timestamp
- ✅ Skeleton loader, mobile-responsive (stacked layout)
- ✅ 8-card detailed stat grid collapsed behind a "Detailed metrics" toggle

#### Enhanced Job Details (carryover from earlier scope)
- ✅ Continuation dependency graph (with Load more) — `JobGraphViewer`
- ✅ Retry summary banner — retry count + exception consistency + per-attempt badges
- ✅ Stack trace source links — `DashboardUIOptions.SourceLink`
- ~~Retry history with diff~~ — **dropped** (superseded by the retry banner; retry arguments are identical, per-attempt stack traces already expandable)
- ~~Job execution duration chart per type (on Job Details page)~~ — **dropped** (duplicates `/analytics/performance`; wrong place for type-level aggregates)

#### Operations (P0) ✅

- ✅ **Pause/Resume per queue** — `QueuePauseServerFilter` (`IElectStateFilter`) intercepts the transition into Processing and reschedules paused jobs (default +30s, configurable via `QueueOperationsOptions`) — never cancels them, so no job is ever deleted. Dashboard toggle on the new `/queues` page. Audit-logged. Requires the host to call `config.UseDashboardQueuePauseFilter()` so running servers respect the pause.
- ✅ **Maintenance mode** — global pause-all toggle from the Queues page. Persistent yellow banner with reason field rendered on every dashboard page.
- ✅ **Audit log** — every admin action (delete, requeue, batch ops, recurring CRUD, recurring stop/start, queue pause/resume, maintenance toggles) recorded with user, timestamp, target, client IP, and metadata. User attribution uses a per-circuit `AuditActorAccessor` (from `AuthenticationStateProvider`) since Blazor circuit actions have no `HttpContext`. New `/audit` page with filter by action prefix, user, target. Storage uses Hangfire's KV primitives — no schema changes. Configurable retention (default 30d) and max entries (default 10K).

---

## v2.4 — Job Builder ✅

**Goal**: Let operators create, schedule, and enqueue jobs **with their arguments** from the dashboard — closing [#8](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/8) and removing the code-change-and-redeploy requirement for parameter changes.

Replaces the old `RecurringEditor` (which built jobs with empty `Args` and resolved methods by name, throwing on overloads) with a composable, type-aware builder shared by the recurring editor and a new enqueue page.

#### Phase 1 — Typed arguments & overload-safe resolution ✅
- ✅ `JobArgumentConverter` (Internal) — positional `Args` shaping (one slot per declared parameter), typed conversion to declared CLR types, injected-parameter slots set to `null`
- ✅ Empty-value resolution is total — blank → `null` (nullable) or `default(T)` (non-nullable), never an error
- ✅ `Parameter_JSON` validation — `Malformed` / `NotArray` / `CountMismatch` / `ElementTypeError` / `Valid`
- ✅ Argument pre-fill (canonical JSON) when editing an existing recurring job
- ✅ `HangfireMonitorService.CreateOrUpdateRecurringJob(RecurringJobRequest)` — gates → resolve → build args → store, leaving state unchanged on any failure

#### Phase 2 — Discovery & Method Picker ✅
- ✅ `JobMethodResolver` (singleton, cached, resilient to `ReflectionTypeLoadException`) — discovers methods decorated with `JobDisplayName` / `Tag` / `Queue` (method or declaring class)
- ✅ Overload-safe `ResolveMethod` selecting the single matching overload by job-parameter count + types
- ✅ Custom-method validation (ordered checks) gated behind `DashboardUIOptions.AllowArbitraryMethodInvocation` (default `false`)
- ✅ `MethodPicker` component — Registered vs Custom, empty-state handling, re-emit on selection change
- ✅ `QueueAttribute` reporting (presence + value, including format templates)

#### Phase 3 — Dynamic Parameter Builder ✅
- ✅ `ParameterInputMapper` — pure type → `ParameterInputKind` mapping (Text, Integer, Float, Date/Time/DateTime, Guid, Bool/NullableBool, Enum single/flags, ScalarArray, NestedObject, Json)
- ✅ `ParameterBuilder` component — one control per job parameter; nested objects instantiated explicitly (depth ≤ 5); tri-state nullable bool
- ✅ Form ⇄ JSON toggle with read-only JSON mirror; round-trips when valid, stays in JSON with an error when not

#### Phase 4 — Schedule Builder & Enqueue ✅
- ✅ `CronDescriber` / `CronPreview` (Internal) — build cron from fields (Every/Specific/Range/Step), human-readable description, next occurrence per time zone (Cronos, already bundled — no new dependency)
- ✅ `ScheduleBuilder` component — visual cron builder + manual input with preview (recurring mode only)
- ✅ `JobBuilder` composite with `Mode = Recurring | Enqueue`; queue control read-only when a `QueueAttribute` applies
- ✅ New `/jobs/enqueue` page for one-off jobs; `EnqueueJob(EnqueueJobRequest)` returns the new job id
- ✅ Read-only / recurring-admin / custom-method gates enforced in UI and service

#### Testing ✅
- ✅ 25 FsCheck.Xunit correctness properties over the pure logic (converter, resolver, input mapper, Form/JSON round trip, cron helpers)
- ✅ bunit component tests (MethodPicker, ParameterBuilder, ScheduleBuilder, JobBuilder, RecurringEditor) + service tests (recurring upsert, enqueue)

### v2.4.1 — Job Builder Follow-up ✅

**Goal**: Polish the Job Builder based on real-world targets (interfaces, abstract contracts, injected parameters) and tighten destructive-action UX.

- ✅ **Searchable method picker** — the method dropdown is a searchable combobox filtering by display label, full type name, and method name; entries badged **Contract** (interface/abstract) vs **Implementation**
- ✅ **Contract-aware resolution** — `Job` built against the selected `ResolvedType` (not `Method.DeclaringType`), so inherited/interface/abstract targets keep class-level `[Tag]`/`[Queue]` and match `AddOrUpdate<T>` semantics; `ResolveMethod` allows abstract methods
- ✅ **Display-name resolution** delegates to `JobDisplayNameAttribute.Format` (honoring `ResourceType`), with fallback to the interface contract's attribute when stored against a concrete implementation
- ✅ **Contract discovery** — `JobMethodResolver` surfaces interface and abstract-class methods alongside concrete implementations/overrides, labeled with `JobMethodKind` (Contract/Implementation/Standalone)
- ✅ **Fixed [#10](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/10)** — editing a recurring job whose method takes an injected parameter (`PerformContext`/`CancellationToken`) no longer throws an `IntPtr`/`WaitHandle.Handle` error; edit pre-fill drops injected slots before serialization
- ✅ **Consistent destructive-action buttons** — solid-red `btn-danger` + trash icon across the recurring list, recurring edit form, and all job-list pages (Awaiting/Enqueued/Failed/Fetched/Processing/Scheduled/Retries), disabled until items are selected

---

## v2.4.2 — Recurring & Job Builder Follow-up ✅

**Goal**: Address operator feedback on the recurring jobs surface and the Job Builder form.

- ✅ **Mixed-case recurring job IDs** ([#11](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/11)) — the Create/Edit form's Job ID rule no longer forces lowercase; it now accepts upper/lower-case letters, digits, dot, underscore, and dash (e.g. `IShopifyJob.ShopifyStockSyncFromSapAsync`), matching what Hangfire's `AddOrUpdate<T>` permits. The identifier length cap was raised (50 → 100) since IDs are stored as hash keys, not in the NVARCHAR(50) queue column. The strict lowercase rule still applies to queue names (Hangfire's `EnqueuedState.ValidateQueueName`).
- ✅ **Never-fire cron expressions editable** ([#11](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/11)) — the Schedule Builder now emits its loaded schedule state on initialization, so editing a recurring job and saving without touching the schedule no longer fails with "a valid cron expression is required." Intentionally unreachable expressions (e.g. `0 0 31 2 *`) round-trip as valid — Cronos/Hangfire parse them successfully — with a clear note that the job won't run on a schedule and can be triggered manually.
- ✅ **Long job names / ids no longer break table layout** ([#12](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/12)) — the recurring and job-list name/id columns are constrained with `max-width` + ellipsis (`.hf-job-name`) and a hover tooltip showing the full value, so a single long value can't widen the table and hide later columns.
- ✅ **Recurring jobs filter** ([#13](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/13)) — a client-side filter input on the Recurring Jobs page narrows both the active and stopped lists by job id and resolved job name; selection state follows the filtered view.
- ✅ **Duplicate recurring job id rejected on create** — the Job Builder flags an existing id (active or stopped) inline and blocks submission, re-checking live storage at submit so a create never silently overwrites an existing recurring job. Edit still updates the existing job.
- ✅ **Audit Log grid parity** — the Audit Log page now uses the shared items-per-page selector and numbered pager (matching the job-list grids) via a new `AuditLogService.QueryPage` that returns the page slice plus the filtered total; the existing action/user/target filters are preserved.

---

## v2.4.3 — Dashboard UI/UX Fixes ✅

**Goal**: Operator-feedback UI/UX fixes reported against the 2.4 line.

- ✅ **Failed Jobs table column overflow** ([#17](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/17)) — a long exception message no longer widens the table and hides the "Failed" date column behind a horizontal scrollbar; the message wraps/breaks (`.hf-exception-message`) and is height-capped with its own vertical scroll.
- ✅ **Create Job dropdown pill alignment** ([#18](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/18)) — the registered-method picker's Contract/Implementation pill stays anchored top-right for long job/type names; the label now shrinks and breaks (`min-width: 0`).
- ✅ **Recurring Jobs search dropped characters** ([#19](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/19)) — the filter input is now uncontrolled, so typing quickly over the Blazor Server circuit no longer overwrites in-flight characters; filtering still applies as you type.
- ✅ **Dark theme persistence** ([#20](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/20)) — the persisted theme survives Blazor enhanced navigation and new sessions; it is re-applied on `enhancedload` with a `MutationObserver` guard that restores `data-bs-theme` if it is stripped.

---

## v2.5.0 — Recurring Schedule Heatmap ✅

**Goal**: Visualize recurring-job scheduling density — by queue, day, and hour — to surface overlap and overload hotspots, and plan controllable cron jobs around real on-demand load ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)). Shipped in `2.5.0` and merged to `main`; also adds rollup-based metrics adapters (`a2n.Hangfire.Dashboard.Rollup` / `.Redis`) so Analytics, the Historical/demand sources, and the Planner's estimated durations work on non-SQL storages ([#21](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/21)).

- **Rollup metrics adapters ([#21](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/21))** — a storage-agnostic `IStorageMetricsProvider` backed by an `ExecutionRollupCollector` that samples succeeded/failed jobs via the core `IMonitoringApi`, so Redis / in-memory storages get Analytics, heatmap Historical/demand, and realistic Planner estimated durations (no more floored 1-minute fallback).

- **Views** — Planner (projected cron over ad-hoc demand with low-load "safe windows"), Punchcard, Queue × Hour, Per-queue small multiples, Calendar, Concurrency (duration-aware, with a worker-capacity reference line and over-capacity flagging), and Recommendations (overlapping-cluster detection with a before/after stagger impact).
- **Sources** — a storage-agnostic **Projected** source (computed from cron expressions) on any storage, plus a **Historical** source and ad-hoc demand overlay on SQL Server / PostgreSQL (degrades gracefully — toggles hidden — elsewhere).
- Honors per-job and selectable viewer time zones, light/dark theme, deterministic per-queue colors, click-to-drill-down into a cell's contributing jobs, and is keyboard / screen-reader accessible.

> **Notifications & alert rules** was originally scoped for v2.5 but has been **deferred to the Stretch / Backlog** — not mandatory, no concrete demand, and largely superseded by the v2.6 Prometheus `/metrics` endpoint. The full plan and design notes are preserved in [docs/proposals/notifications-alert-rules.md](proposals/notifications-alert-rules.md) and will be revisited under the demand-driven rule (5+ explicit requests). Version numbers are intentionally **not** renumbered — v2.6 and v2.7 keep their numbers.

---

## v2.5.1 — Redis / rollup analytics fixes ✅

**Goal**: Fix the Analytics regressions reported on Hangfire Pro with Redis storage ([#25](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/25), [#26](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/26), [#27](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/27)), and ship the nav-group circuit crash fix ([#23](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/23)) that was previously a pre-release.

The rollup adapter shipped in v2.5.0 read back several of its own aggregates incorrectly, so the panels it was built to enable stayed empty on non-SQL storages. Fixes are confined to that adapter plus one additive `IStorageMetricsProvider` method and the Recurring Health page.

- ✅ **Duration stats were never read back** ([#27](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/27), [#26](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/26)) — `MetricsRollupStore.ReadJobDurationStats` looked for job-type names among hash fields containing no `:`, but every field is written as `{jobType}:count`, `{jobType}:sum`, … so it always returned an empty list. The heatmap's p95 never arrived (estimated duration fell back to the floored 1-minute default) and Analytics ▸ Performance lost *Duration trend* and *Duration by job type*. Prefixes are now recovered from the trailing `:count` marker, which also keeps job types and queue names containing `:` intact.
- ✅ **Durations were over-reported** — the collector used `SucceededJobDto.TotalDuration`, which Hangfire computes as `PerformanceDuration + Latency`. It now reads `PerformanceDuration` from the succeeded state's data, matching the SQL adapters, and falls back to `TotalDuration` only when the state data is unavailable.
- ✅ **Queue latency and average state timing had no data** ([#26](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/26)) — the collector always passed `latency: null` (it now reads `Latency` from the same state data), and `GetAverageStateTimingsAsync` returned an empty DTO (the enqueued and processing phases are now count-weighted means of the latency and duration rollups). `AvgScheduledMs` stays `0`: the pre-enqueue phase is not tracked by the rollup.
- ✅ **Queue throughput dropped queues whose name contains `:`** — `ReadQueueThroughput` split its `{queue}:{bucket}` field on the first colon, producing an unparseable bucket key that was silently filtered out. Now split on the last colon. Found while auditing the other readers for the same defect class; the rest parse correctly.
- ✅ **Analytics ▸ Recurring never finished loading** ([#25](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/25)) — `GetRecurringJobExecutionsAsync` paged the succeeded and failed lists (2 000 jobs each) and probed the `RecurringJobId` parameter of every job it saw, once per recurring job: O(jobs × 4 000) storage round-trips. The collector now keeps a bounded ring of the 20 newest executions per recurring job and the provider answers from a single hash read.
- ✅ **Recurring health showed no last-results strip or average duration** ([#25](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/25)) — `GetRecurringJobHealthAsync` hard-coded `LastExecutionResults` to an empty array and never set `AverageDurationMs`; both come from the new ring in one hash read for all jobs, reaching parity with the SQL adapters.
- ✅ **Batched recurring history** — new `IStorageMetricsProvider.GetRecurringJobExecutionsBatchAsync` fetches every job's history in one call (a `ROW_NUMBER` query on SQL Server / PostgreSQL, one hash read on the rollup adapter), replacing the page's per-job loop. A default interface method falling back to that loop, so third-party providers keep working unchanged. The page also cancels in-flight queries on dispose.
- ✅ **Rollup sampling is now visible** — each poll scans at most 2 000 succeeded and 2 000 failed jobs, then advances the watermark past anything it did not reach. The collector logs a warning when it hits that cap. Closing the gap needs a resumable two-watermark scan, tracked in [#29](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/29).
- ✅ **First metrics-provider test suite for the PostgreSQL adapter** — 20 tests covering the batched recurring-history query against a real database (asserted equal to the per-job query for every seeded recurring id) plus smoke coverage for the remaining metrics queries.

- ✅ **Sidebar nav group tore down the circuit on a fresh session** ([#23](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/23)) — with no `hf-nav-group:*` key in `localStorage`, `NavMenuGroup` read the expand state via `JS.InvokeAsync<bool?>`; on some `Microsoft.JSInterop` versions, deserializing a JavaScript `null` into `Nullable<bool>` throws `InvalidCastException`, and the uncaught `JSException` terminated the circuit (sub-pages unreachable). `Content/js/nav.js` now returns a string instead of `null`, the component reads it with `JS.InvokeAsync<string>` + `bool.TryParse`, and a defensive `catch (JSException)` preserves the default state on any runtime.

---

## v2.6 — Integrations (Planned)

**Goal**: Plug the dashboard into the modern observability and automation stack.

- [ ] **OpenTelemetry trace linking** — capture `traceparent` on enqueue, restore as child span on execute, render "View distributed trace →" link on Job Details. Shipped as `a2n.Hangfire.Dashboard.OpenTelemetry` package. `DashboardUIOptions.TraceLinkBuilder` for Tempo/Jaeger/Honeycomb URL templates.
- [ ] **Prometheus `/metrics` endpoint** — text format 0.0.4. Exposes `hangfire_jobs_total`, `hangfire_jobs_in_state_count`, `hangfire_queue_length`, `hangfire_servers_count`, `hangfire_workers_count`, `hangfire_recurring_jobs_count`, `hangfire_job_duration_seconds` (histogram). No heavy library — plain string formatter. Sample Grafana dashboard JSON shipped in repo.
- [ ] **REST API** (read-only first, optional package) — wraps existing `IStorageQueryProvider` services with Minimal API endpoints. JWT auth. OpenAPI spec auto-generated.
- [ ] **CSV / JSON export** — stream-based, respects current search criteria.

---

## v2.7 — Customization (Planned)

**Goal**: Make the dashboard fit each team's branding and tenancy needs.

- [ ] White-label theming (custom colors via Bootstrap CSS variables, logo upload via `DashboardUIOptions`)
- [ ] Hide/show built-in pages via options (e.g., disable Analytics for tenants without metrics provider)
- [ ] Saved views — filter + sort + columns saved as named views per user, pinnable to sidebar

---

## Stretch / Backlog

Items considered but explicitly **not prioritized**. Will be reconsidered when 5+ users explicitly request them.

- [ ] **Notifications & alert rules** (deferred from v2.5) — Slack/Teams/Discord/webhook/email channels, 8 trigger types, per-rule cooldown, rule editor + history. Deferred because it is not mandatory, has no concrete demand, and is largely superseded by the v2.6 Prometheus `/metrics` endpoint feeding existing alerting stacks (Grafana Alertmanager, etc.). Full plan + design notes preserved in [docs/proposals/notifications-alert-rules.md](proposals/notifications-alert-rules.md). Revisit on explicit demand (5+ requests); if revived, start from the ramped MVP (one generic webhook channel + 3 cheap triggers, evaluation as a recurring Hangfire job, default off).
- [ ] **Job Execution Timeline (Gantt)** — visually impressive but adoption is estimated to be low for typical small/medium deployments. Reconsider after v2.3 ships and based on demand.
- [ ] **Multi-instance federation** — dashboard switcher for dev/staging/prod or sharded Hangfire deployments. Storage adapter is already modular, so the architecture is ready when demand appears.
- [ ] **Replay with modified arguments** — failed-job rerun with edited arguments (powerful but easy to misuse without RBAC; gate behind the audit log shipped in v2.3 Operations).
- [ ] **Failure clustering / fingerprint** — group Failed page by exception fingerprint (Sentry-style). Significant debug-experience improvement; defer until the v2.3 trigger-engine stabilizes the data path.
- [ ] **Search by job argument value** — index `Job.Arguments` for support-case lookups (`customerId == "C-12345"`). Requires storage adapter changes per provider.
- [x] **Visual cron builder** — interactive recurring-job editor instead of plain cron string input. ✅ **Done in v2.4** (`ScheduleBuilder` — field-by-field Every/Specific/Range/Step + manual input with human-readable description and next-run preview).
- [ ] **Dynamic job chaining / visual chain builder** ([#15](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/15)) — operator-managed continuation/fan-out so downstream jobs can be added without a redeploy. **Out of scope** for the dashboard: building, persisting, and resolving chain definitions at runtime is a Hangfire core concern that sits below the dashboard layer. Recorded as a discussion item only.
- [ ] **Browser push notifications** — explicitly **out of scope**. Ops teams don't monitor via browser tabs; webhook + email cover the use case.
- [ ] **Configurable homepage widgets** — over-engineered for a focused dashboard; revisit only on explicit demand.
- [ ] **CLI companion** (`hangfire-cli` global tool) — depends on the v2.3 REST API.
- [ ] **Public status-page mode** — read-only `/status` route showing health summary without sensitive job data.
- [ ] **Predictive alerts** — queue overflow ETA, anomaly detection on duration/failure rate.
- [ ] **Smart Insights card** — auto-generated observations on Home page using simple anomaly detection (z-score).
- [ ] **Tag-based Analytics** — filter all analytics by tag, failure rate per tag, tag cloud with metrics overlay.
- [ ] **Historical server utilization & queue depth** — requires custom snapshot storage.

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
| v2.6 | Rollup / Redis metrics adapters (`a2n.Hangfire.Dashboard.Rollup`, `.Redis`) | ✅ Done |
| v1.6 | Analytics Dashboard (Overview + Performance + Failures + Queues + Recurring) | ✅ Done |
| v2.0 | Phase 2 complete | ✅ Done |
| v2.1 | Search & query refactor + JobDisplayName + SQL Server fixes | ✅ Done |
| v2.1.1 | WebSocket fix for Startup-pattern host apps (Generic Host compatibility) | ✅ Done |
| v2.2 | UX improvements: progress circle, Fetched page, delete modals, mobile nav fix | ✅ Done |
| v2.2.1 | Security & auth hardening: authorization defaults, SignalR auth, SQL validation | ✅ Done |
| v2.3.0 | **Operational visibility & controls**: health checks + hero card, queue pause/resume, maintenance mode, audit log | ✅ Done |
| v2.3.1 | Realtime analytics fixes: SQL Server `GROUP BY` (error 144), fixed-cadence broadcast loop, NuGet XML docs | ✅ Done |
| v2.4.0 | **Job Builder**: typed arguments, guided parameter form (+ JSON), method discovery, overload-safe resolution, visual cron builder, one-off enqueue page (closes #8) | ✅ Done |
| v2.4.1 | **Job Builder follow-up**: searchable method picker, contract-aware (interface/abstract) resolution + display names, injected-parameter edit fix (#10), consistent destructive-action buttons | ✅ Done |
| v2.4.2 | **Recurring & Job Builder follow-up**: mixed-case job IDs + never-fire cron edit (#11), long-name ellipsis (#12), recurring jobs filter (#13), duplicate-id guard on create, Audit Log grid parity | ✅ Done |
| v2.4.3 | **Dashboard UI/UX fixes**: Failed-table column overflow (#17), Create Job dropdown pill alignment (#18), recurring search dropped characters (#19), dark-theme persistence (#20) | ✅ Done |
| v2.5.0 | **Recurring Schedule Heatmap** (#14) — Planner, Punchcard, Queue × Hour, Per-queue, Calendar, Concurrency, stagger Recommendations; Projected (any storage) + Historical (SQL/PG or rollup adapters) sources; storage-agnostic estimated durations via rollup metrics (#21) | ✅ Done |
| v2.5.1 | **Nav group crash fix** (#23) — sidebar nav group no longer tears down the Blazor circuit on a fresh session with no saved `localStorage` state | ✅ Done |
| v2.6.0 | **Integrations**: Prometheus `/metrics`, OpenTelemetry trace links, read-only REST API, CSV/JSON export | Planned |
| v2.7.0 | **Customization**: white-label theming, show/hide built-in pages, saved views | Planned |
| v3.0 | Stretch goals & long-term backlog (timeline, federation, replay, clustering, ...) | Planned |

---

## Contributing

See [CONTRIBUTING.md](../CONTRIBUTING.md) for guidelines on how to contribute to this project.
