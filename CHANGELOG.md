# Changelog

## 2.5.1-beta.1 — Nav group crash fix (pre-release)

> **Pre-release.** Fixes a Blazor Server circuit crash on the first dashboard visit when the browser has no saved sidebar nav-group state ([#23](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/23)).

### Fixed

- **Sidebar nav group tore down the Blazor circuit on a fresh session ([#23](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/23)).** When no `hf-nav-group:*` key existed in `localStorage`, `NavMenuGroup` read the persisted expand state via `JS.InvokeAsync<bool?>`. On some `Microsoft.JSInterop` versions, deserializing a JavaScript `null` into `Nullable<bool>` routes through `Convert.ChangeType(null, typeof(bool))` and throws `InvalidCastException`; the surrounding `try/catch` only handled `JSDisconnectedException` / `ObjectDisposedException`, so the resulting `JSException` propagated out of `OnAfterRenderAsync` and terminated the circuit — making sub-pages unreachable. `Content/js/nav.js` now returns a string (`"true"` / `"false"` / `""`) instead of `null`, the component reads it with `JS.InvokeAsync<string>` + `bool.TryParse` (avoiding the value-type conversion path entirely), and a defensive `catch (JSException)` preserves the default expanded state on any runtime.

## 2.5.0 — Recurring Schedule Heatmap

> **Feature release.** The Recurring Schedule Heatmap ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)) is now generally available and merged to `main`. Plan controllable cron jobs around real on-demand load — projected from cron expressions and bucketed by queue, day, and hour — with new rollup-based analytics so Historical and demand views also work on non-SQL storages.

### Added

- **Recurring Schedule Heatmap ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)).** A new **Schedule Heatmap** page (under the Management nav group) visualizing recurring-job scheduling density by queue, day, and hour:
  - **Views:** Planner (projected cron over ad-hoc demand with low-load "safe windows"), Punchcard, Queue × Hour, Per-queue small multiples, Calendar (color by volume/failure/duration), Concurrency (per-queue stacked, duration-aware, with a worker-capacity reference line and over-capacity flagging), and Recommendations (overlapping-cluster detection with a before/after stagger impact).
  - **Sources:** a storage-agnostic **Projected** source (computed from cron expressions) on any storage, plus a **Historical** source and ad-hoc demand overlay on SQL Server / PostgreSQL — or on any storage via the new rollup adapters (degrades gracefully where unavailable).
  - **Controls:** job class (Cron / Ad-hoc / Combined), projection window (idealized week / next 7 days), load metric (fire count / worker-minutes), demand statistic, lookback weeks, worker capacity (detected or overridden), per-queue filtering, hide sub-hourly, and log scale.
  - Honors per-job time zones and a selectable viewer time zone, light/dark theme, deterministic per-queue colors shared across the dashboard, click-to-drill-down into a cell's contributing jobs, and is keyboard / screen-reader accessible.
- **Rollup metrics adapters for non-SQL storages ([#21](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/21)).** New packages `a2n.Hangfire.Dashboard.Rollup` and `a2n.Hangfire.Dashboard.Redis` register a rollup-based `IStorageMetricsProvider` plus a unified `ExecutionRollupCollector` that samples recent succeeded/failed jobs via Hangfire's core `IMonitoringApi` and aggregates per job-type stats (reservoir-sampled percentiles). This makes Analytics and heatmap Historical / ad-hoc demand — and the Planner's **estimated job duration** — work on Redis, in-memory, and other non-SQL Hangfire storages, instead of falling back to the floored 1-minute default.

### Fixed

- **Planner showed the wrong queue for attribute-routed jobs ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14) follow-up).** A recurring job routed by a `[Queue]` attribute appeared under `default` instead of its real queue. `HeatmapService.ResolveQueue` now resolves the `[Queue]` attribute on the method (or its declaring type/interface) ahead of the stored `default` sentinel, matching the create/enqueue paths.
- **Planner estimated durations wrong on non-SQL storage ([#21](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/21)).** Without a metrics provider, every job's estimated duration fell back to the floored 1-minute default (e.g. a ~3-minute job shown as `1m`). With the rollup adapters registered, the Planner and Concurrency views now reflect real processing time on any storage.

### Known limitations

- **Rollup metrics are approximate and forward-only.** Percentiles use reservoir sampling; historical data accumulates from the first collector run. SQL Server / PostgreSQL adapters remain preferable when relational job history is available.

### Includes

- All **2.4.3** dashboard UI/UX fixes ([#17](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/17)–[#20](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/20)) — see the 2.4.3 entry below.

## 2.5.0-beta.2 — Recurring Schedule Heatmap (pre-release)

> **Pre-release.** Second cut of the Recurring Schedule Heatmap ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)), rebased on the 2.4.3 stable line so it now also carries the #17–#20 dashboard fixes. Still on the `feature/recurring-schedule-heatmap` branch; not merged to `main`.

### Fixed

- **Planner showed the wrong queue for attribute-routed jobs ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14) follow-up).** A recurring job routed by a `[Queue]` attribute — e.g. a DI-dispatched interface carrying `[Queue("background")]` — appeared on the Schedule Heatmap (Planner, filters, grouping) under `default` instead of its real queue. `HeatmapService.ResolveQueue` now resolves the `[Queue]` attribute on the method (or its declaring type/interface) ahead of the stored `default` sentinel that Hangfire writes when no explicit queue is given, matching `EffectiveQueue.Resolve` (Req 13.6/13.7) used on the create/enqueue paths.

### Included from 2.4.3

- Merged the stable **2.4.3** dashboard fixes — Failed-table column overflow ([#17](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/17)), Create Job dropdown pill alignment ([#18](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/18)), recurring search dropped characters ([#19](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/19)), and dark-theme persistence ([#20](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/20)). See the 2.4.3 entry below.

### Added

- **Rollup metrics adapters for non-SQL storages.** New packages `a2n.Hangfire.Dashboard.Rollup` and `a2n.Hangfire.Dashboard.Redis` register rollup-based `IStorageMetricsProvider` plus a unified `ExecutionRollupCollector` so Analytics and heatmap Historical/ad-hoc demand work on Redis, in-memory, and other NoSQL Hangfire storages without SQL queries. Historical metrics are forward-only from first collector run (same model as the demand rollup).

### Known limitations

- **Rollup metrics are approximate and forward-only.** Percentiles use reservoir sampling; historical data accumulates from the first collector run. SQL Server / PostgreSQL adapters remain preferable when relational job history is available.

## 2.5.0-beta.1 — Recurring Schedule Heatmap (pre-release)

> **Pre-release.** First cut of the Recurring Schedule Heatmap ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)) for early testing on the `feature/recurring-schedule-heatmap` branch. Not merged to `main`.

### Added

- **Recurring Schedule Heatmap ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)).** A new **Schedule Heatmap** page (under the Management nav group) visualizing recurring-job scheduling density by queue, day, and hour. Highlights:
  - **Views:** Planner (projected cron over ad-hoc demand with low-load "safe windows"), Punchcard, Queue × Hour, Per-queue small multiples, Calendar (with color-by volume/failure/duration), Concurrency (per-queue stacked, duration-aware, with a worker-capacity reference line and over-capacity flagging), and Recommendations (overlapping-cluster detection with a before/after stagger impact).
  - **Sources:** a storage-agnostic **Projected** source (computed from cron expressions) on any storage, plus a **Historical** source and ad-hoc demand overlay on SQL Server / PostgreSQL (degrades gracefully — toggles hidden — elsewhere).
  - **Controls:** job class (Cron / Ad-hoc / Combined), projection window (idealized week / next 7 days), load metric (fire count / worker-minutes), demand statistic, lookback weeks, worker capacity (detected or overridden), per-queue filtering, hide sub-hourly, and log scale.
  - Honors per-job time zones and a selectable viewer time zone (defaulting to the browser zone, persisted), light/dark theme, deterministic per-queue colors shared across the dashboard, click-to-drill-down into a cell's contributing jobs, and is keyboard / screen-reader accessible.

## 2.4.3 — Dashboard UI/UX Fixes

> **Patch release.** Operator-feedback UI/UX fixes reported against the 2.4 line: the Failed Jobs table, the Create Job method dropdown, the Recurring Jobs search box, and dark-theme persistence ([#17](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/17), [#18](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/18), [#19](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/19), [#20](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/20)).

### Fixed

- **Failed Jobs table pushed the "Failed" column off-screen ([#17](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/17)).** A long exception message — especially one long unbroken token — stretched the table wider than the viewport, hiding the date column behind a horizontal scrollbar. The exception text now wraps/breaks (`overflow-wrap: anywhere; word-break: break-word`, `.hf-exception-message`) and is height-capped with its own vertical scroll, so every column stays visible without horizontal scrolling.
- **Create Job dropdown pill misaligned for long names ([#18](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/18)).** In the registered-method picker, a long job/type name pushed the Contract/Implementation pill out of position because the label couldn't shrink. The label now shrinks and breaks (`min-width: 0`), keeping the pill consistently anchored to the top-right regardless of name length.
- **Recurring Jobs search dropped characters when typing quickly ([#19](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/19)).** The filter box echoed a server-side value back to the input on every keystroke over the Blazor Server circuit, so fast typing overwrote in-flight characters (`Hello` → `Hllo`). The input is now uncontrolled — keystrokes are never reset — while filtering still applies as you type.
- **Dark mode reverted to light on a new session / after navigation ([#20](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/20)).** Blazor enhanced navigation reset the `<html>` element to the server-rendered document, which carries no `data-bs-theme`, stripping the persisted theme. The theme is now re-applied on Blazor's `enhancedload` event, with a `MutationObserver` guard (loop-safe) that restores `data-bs-theme` if anything removes it.

## 2.4.2 — Recurring & Audit Follow-up

> **Patch release.** Operator-feedback fixes for the Recurring Jobs surface and the Job Builder form ([#11](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/11), [#12](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/12), [#13](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/13)), plus Audit Log grid parity.

### Fixed

- **Recurring job IDs could not use uppercase or dots ([#11](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/11)).** The Create/Edit form held a new Job ID to a strict lowercase rule (`^[a-z0-9_-]+$`), rejecting the mixed-case/dotted ids that `AddOrUpdate<T>` produces (e.g. `IShopifyJob.ShopifyStockSyncFromSapAsync`). The rule now accepts upper- and lower-case letters, digits, dot, underscore, and dash, and the length cap was raised 50 → 100 (recurring ids are stored as hash keys, not in the NVARCHAR(50) queue column). Queue names keep the strict lowercase rule (`EnqueuedState.ValidateQueueName`).
- **Editing a recurring job and saving without touching the schedule failed ([#11](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/11)).** `ScheduleBuilder` did not emit its loaded schedule on init, so the form had no cron until the operator interacted with a schedule control — saving first reported "a valid cron expression is required." It now emits the loaded schedule state on initialization. Intentionally unreachable "never-fire" expressions (e.g. `0 0 31 2 *`) round-trip as valid with a "no upcoming occurrence" note instead of an error.
- **Long job names broke the recurring table layout ([#12](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/12)).** Job name and id columns are now constrained with `text-overflow: ellipsis` and a hover tooltip showing the full value (`.hf-job-name`), applied across the recurring and job-list grids, so a single long value can no longer widen the table and push later columns off-screen.

### Added

- **Recurring jobs filter ([#13](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/13)).** A client-side filter input on the Recurring Jobs page narrows both the active and stopped lists by job id and resolved job name; selection follows the filtered view.
- **Duplicate recurring job id rejected on create.** Entering an id that already exists (active or stopped) on the create form flags it inline and blocks submission, re-checked against live storage at submit so a create never silently overwrites an existing recurring job. Edit still updates the existing job.
- **Audit Log grid parity.** The Audit Log page now uses the shared items-per-page selector and a numbered pager (matching the job-list grids), backed by a new `AuditLogService.QueryPage` that returns the page slice plus the filtered total. The existing action/user/target filters are preserved.

### Internal

- New bunit/service tests: schedule initial-emit + never-fire validity, Job ID mixed-case acceptance and duplicate rejection, the recurring-list filter, and `AuditLogService.QueryPage`. The backlog now records [#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14) (recurring schedule heatmap) and [#15](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/15) (dynamic job chaining).

## 2.4.1 — Job Builder Follow-up

> **Patch release.** Polishes the v2.4.0 Job Builder: a searchable method picker, correct attribute/display-name resolution for interface and abstract contracts, a fix for editing recurring jobs whose method takes an injected parameter ([#10](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/10)), and a consistent destructive-action button style across all job pages.

### Fixed

- **Editing a recurring job with an injected parameter threw `IntPtr`/`WaitHandle.Handle` ([#10](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/10)).** Edit pre-fill now projects the stored Hangfire `Args` onto `Job_Parameters` through `JobArgumentConverter.ToParameterJsonFromArgs`, dropping injected slots (`PerformContext`, `CancellationToken`, `IJobCancellationToken`) before serialization so a method like `Run(PerformContext ctx, ...)` pre-fills cleanly.
- **Class-level `[Tag]` / `[Queue]` and display names were lost for inherited, interface, and abstract targets.** The Hangfire `Job` is now built against the operator-selected type (`ResolvedType`) rather than `Method.DeclaringType`, matching `AddOrUpdate<T>` semantics so class-level attributes are honored. Display-name resolution delegates to `JobDisplayNameAttribute.Format` (honoring `ResourceType`), falling back to the interface contract's attribute when a job is stored against a concrete implementation.

### Added

- **Searchable method picker.** The method dropdown is now a searchable combobox that filters discovered methods by display label, full type name, and method name. Each entry is badged as **Contract** (interface / abstract) or **Implementation**.
- **Contract discovery.** `JobMethodResolver` now surfaces interface and abstract-class methods alongside their concrete implementations/overrides, each labeled with `JobMethodKind` (Contract / Implementation / Standalone). `ResolveMethod` allows abstract methods so interface/abstract contracts are resolvable.

### Changed

- **Consistent destructive-action buttons.** The solid-red `btn-danger` Delete style with a trash icon is now applied uniformly across the recurring list, the recurring edit form, and every job-list page (Awaiting, Enqueued, Failed, Fetched, Processing, Scheduled, Retries) — each disabled until items are selected.

### Internal

- Added `Issue10EditPrefillTests` reproducing the exact injected-parameter signature, and corrected stale edit-prefill property-test comments. New tests for inherited-method type resolution, interface canonical discovery, and `JobNameHelper` formatting. Added an `IFtpTransferService` interface repro to the sample apps.

## 2.4.0 — Job Builder

> **Create and schedule jobs *with their arguments* directly from the dashboard.** The recurring editor previously built jobs with empty arguments and resolved methods by name (throwing on overloads), so parameterized methods couldn't be scheduled from the UI. v2.4.0 replaces it with a composable, type-aware Job Builder shared by the recurring editor and a new one-off enqueue page. Closes [#8](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/8).

### ✨ Highlights

- **🧩 Guided parameter form** — a type-aware form generated from the method signature (text, numbers, dates, GUIDs, enums, booleans, arrays, nested objects) with a live JSON mirror and a Form ⇄ JSON toggle.
- **🔎 Method discovery** — pick from methods discovered across loaded assemblies, with overload-safe resolution. Hand-typed arbitrary methods are opt-in via `AllowArbitraryMethodInvocation`.
- **🕑 Visual cron builder** — build a schedule field-by-field (every / specific / range / step) with a human-readable description and a next-run preview in the selected time zone.
- **➕ Enqueue page** — the same builder powers a new `/jobs/enqueue` page for one-off (fire-and-forget) jobs.

### Added — Job Builder

- **Typed argument conversion** — operator-supplied values are converted to each parameter's declared CLR type before the job is stored (`["some-value", 42]` → `string`, `int`), matching how Hangfire serializes arguments. Empty fields resolve to `null` for nullable types or `default(T)` for non-nullable types — never an error.
- **Overload-safe method resolution** — `JobMethodResolver` selects the single overload whose job-parameter count and types match the supplied arguments. Ambiguous, missing, or non-matching methods are rejected with an identifying error and never touch storage.
- **Method discovery** — `JobMethodResolver` scans loaded assemblies for public methods whose method or declaring class carries `JobDisplayName`, `Tag`, or `Queue`. The result is cached for the dashboard's lifetime and resilient to assemblies that fail to load (`ReflectionTypeLoadException`).
- **`Parameter_JSON` validation** — well-formedness, "must be an array", argument-count-vs-signature, and per-element convertibility are validated, with errors naming the offending parameter and expected type.
- **Injected parameters skipped** — `PerformContext`, `IJobCancellationToken`, and `CancellationToken` are excluded from the form and filled by Hangfire at runtime.
- **Edit pre-fill** — current argument values are loaded back into the form when editing an existing recurring job.
- **Visual cron builder** — `ScheduleBuilder` offers a field-by-field cron editor plus manual input, with a human-readable description and next-occurrence preview (parsing via Cronos, already bundled with Hangfire — no new dependency). Unparseable expressions are flagged and block submission.
- **Queue handling** — the queue control suggests current queues (defaulting to `default`) and is an editable combobox that also validates free text against Hangfire's queue-name rule (`^[a-z0-9_-]+$`, max 50 chars); when a `[Queue(...)]` attribute applies to the method or its declaring class, the control becomes read-only with a precedence notice, because Hangfire's `QueueAttribute` overrides the stored queue at state election.
- **Searchable time zone + inline validation** — the recurring editor's time zone is a searchable combobox, and the Job ID, queue, and time-zone fields validate inline. A new recurring Job ID is held to the same strict `^[a-z0-9_-]+$` rule (max 50 chars) because it is carried in the `/recurring/edit/{JobId}` route; existing ids are not re-validated on edit so legacy jobs keep working.
- **Enqueue page** at `/jobs/enqueue` — reuses the `JobBuilder` composite in Enqueue mode for one-off jobs; `HangfireMonitorService.EnqueueJob` returns the new job id. Reached via an **Enqueue Job** button on the Enqueued jobs page (the dedicated nav entry was removed).
- `DashboardUIOptions.AllowArbitraryMethodInvocation` (default `false`) — gates whether operators may invoke hand-typed type + method, keeping the arbitrary-invocation surface opt-in.

### Changed

- The recurring **Create/Edit** form is now built on the shared Job Builder, so it supports arguments, method discovery, and the visual cron builder. `HangfireMonitorService.CreateOrUpdateRecurringJob` now takes a `RecurringJobRequest` and persists arguments and the configured queue.
- **Option rename.** `EnableRecurringJobAdmin` → **`EnableJobManagement`**; its scope now also gates the Enqueue page. The old name is kept as an `[Obsolete]` alias for source compatibility and will be removed in a future release. `EnableCustomMethodInvocation` → **`AllowArbitraryMethodInvocation`** (hard rename — this option was introduced in this release).

### Gating

- Read-only mode, `EnableJobManagement`, and `AllowArbitraryMethodInvocation` are all enforced in both the UI (banners + disabled controls) and the service layer.
- When `EnableJobManagement` is `false` the recurring create/edit builder is hidden, the **Enqueue Job** button and nav entry are not shown, and the `/jobs/enqueue` route returns **Not Found**.

### Internal

- New `JobMethodResolver` service; `Internal` helpers `JobArgumentConverter`, `ParameterInputMapper`, `CronDescriber`, `CronPreview`, `EffectiveQueue`; shared models in `Models/JobBuilderModels.cs`; components `JobBuilder`, `MethodPicker`, `ParameterBuilder`, `ScheduleBuilder`.
- Test coverage: 25 FsCheck.Xunit correctness properties over the pure logic (argument converter, resolver, input mapper, Form/JSON round trip, cron helpers), plus bunit component tests and service tests.

## 2.3.1 — Realtime Analytics Fixes

> **Patch release.** Restores realtime analytics on SQL Server and keeps the analytics broadcast on a steady cadence under load. NuGet packages now ship API documentation.

### Fixed

- **Realtime analytics never broadcast on SQL Server.** `GetQueueDepthSnapshotAsync` and `GetQueueThroughputAsync` placed a subquery in the `GROUP BY` list, which SQL Server rejects (error 144). This threw on every `AnalyticsBroadcastService` tick, so the analytics SignalR channel stayed silent on SQL Server while it worked on PostgreSQL. The queue expression is now computed once in a derived table and aggregated in the outer query (mirrors the PostgreSQL provider). Verified `AnalyticsUpdate` broadcasts every 5s on SQL Server.
- **Analytics broadcast drifted off cadence under load.** The broadcast loop delayed the interval *after* finishing its work, so the real period was `query_time + interval` (p95 ~50s instead of the 5s target in a 300s SQL Server load test). The loop now uses a `PeriodicTimer` so ticks fire on a fixed schedule (ticks arriving mid-broadcast are coalesced instead of drifting), and runs the three independent metrics queries (throughput, server utilization, queue depth) concurrently via `Task.WhenAll` so per-broadcast cost is the slowest query rather than their sum. A `Warning` is now logged when a broadcast exceeds the cadence. Verified p95 5.1s on SQL Server in the 300s persona scenario.

### Build / Docs

- **NuGet packages now ship XML API documentation.** `GenerateDocumentationFile` is enabled for Release builds (`NoWarn 1591`), and the XML doc `cref`/`param` references this surfaced were corrected in `PgHelper`, `HangfireDashboardHealthCheckExtensions`, `AuditLogService`, and `JobParameterMatching`.

### Internal

- Added a SQL Server metrics-provider integration test project (`a2n.Hangfire.Dashboard.SqlServer.Tests`) mirroring the PostgreSQL one — a per-run unique schema fixture, a deterministic 100-job seeder, and smoke tests exercising all 15 `IStorageMetricsProvider` queries against a real SQL Server (regression coverage for the error 144 fix). Tests skip rather than fail when no SQL Server is reachable, so CI without a database stays green.
- Added a Python load-testing harness under `tests/load/` (stress, scenario, and end-to-end utilities) used to validate the analytics cadence fixes. Secrets (`config.toml`) and generated artifacts stay untracked.

## 2.3.0 — Operational Visibility & Controls

> **The biggest operations-focused release yet.** v2.3.0 turns the dashboard from a *viewer* into an *operational tool*: health probes for your orchestrator, an at-a-glance health hero card, live queue pause / maintenance mode, and a full audit trail of admin actions.

### ✨ Highlights

- **🩺 Health checks** — `/healthz`, `/healthz/ready`, `/healthz/full` endpoints + an ASP.NET Core `IHealthCheck` adapter, plus a traffic-light **health hero card** on the Home page.
- **⏸️ Queue pause / resume** — pause individual queues from the new `/queues` page; workers reschedule jobs instead of running them (no data loss).
- **🚧 Maintenance mode** — one global toggle pauses every queue, with a persistent banner on every page.
- **📋 Audit log** — every admin action recorded (who, when, what) on a new `/audit` page, attributed to the real signed-in user.

### Added — Health checks

- **Health check endpoint** at `/{dashboard}/healthz`, `/healthz/ready`, and `/healthz/full`. Returns a structured JSON `HealthReport` covering storage availability, server liveness, queue depth, stuck processing jobs, last-hour failure rate, and missed recurring schedules. HTTP 200 for `Healthy`/`Degraded`, HTTP 503 for `Unhealthy` (K8s probe convention).
- **Health hero card** on the Home page — at-a-glance traffic light (Healthy / Degraded / Critical) with per-issue descriptions, deep-link actions, and an auto-refresh every 10 seconds. The detailed 8-card stat grid is now collapsible behind a "Detailed metrics" toggle (its expanded/collapsed state is remembered per browser).
- `DashboardUIOptions.HealthCheckAuthorizationMode` (`AllowAnonymous` default for K8s, `LocalOnly`, or `RequireDashboardAuth`).
- `DashboardUIOptions.HealthCheckThresholds` to tune what counts as Degraded vs Unhealthy (queue depth, failure rate, stuck-processing minutes, server heartbeat tolerance, recurring missed tolerance, storage response time).
- `HealthCheckService` (DI-registered) — also usable from host code if you want to wire the report into your own ASP.NET Core HealthCheck pipeline.
- `HealthReportCache` — process-wide single-flight cache so concurrent circuits and probes share one computed report; manual hero-card refresh bypasses it.
- `services.AddHealthChecks().AddHangfireDashboard()` adapter — register the dashboard as a single ASP.NET Core `IHealthCheck` for hosts that prefer a unified `/health` endpoint aggregating multiple dependencies. Per-check status surfaces via `HealthCheckResult.Data`.

### Added — Operations (queue pause, maintenance, audit)

- **Queue pause / resume** — toggle individual queues from the new `/queues` page (a modern card layout with status dots, live counts, and a summary bar). While paused, Hangfire workers reschedule jobs (default +30s) instead of executing them. Requires registering the server filter in the host app: `config.UseDashboardQueuePauseFilter()`. When the filter is missing, the page shows an actionable warning instead of silently doing nothing.
- **Maintenance mode** — global pause-all toggle from the Queues page header. A persistent yellow banner with a `Manage →` link is rendered on every dashboard page. Optional reason field surfaced in the banner.
- **Audit log** — every admin action (job requeue/delete, batch ops, recurring CRUD, recurring stop/start, queue pause/resume, maintenance toggles) is recorded with timestamp, user, client IP, target, and metadata. New page at `/audit` with filter by action prefix, user, and target. Storage uses Hangfire's KV primitives (`audit:log` set + `audit:entry:{id}` hashes) — no schema changes. Configurable retention (`Retention`, default 30 days) and max entries (`MaxEntries`, default 10,000).
- `DashboardUIOptions.AuditLog` (`Enabled`, `Retention`, `MaxEntries`).
- `DashboardUIOptions.QueueOperations` (`Enabled`, `Behavior` — Reschedule or Requeue, `RescheduleDelay`, `PauseStateCacheTtl`).
- `AuditLogService` and `QueueOperationsService` — DI-registered (scoped) services usable from host code. Action attribution uses a per-circuit `AuditActorAccessor` populated from `AuthenticationStateProvider`, so audit entries record the actual signed-in user even though Blazor circuit actions run without an `HttpContext`.
- `QueuePauseServerFilter` — Hangfire `IElectStateFilter` that intercepts the transition into `Processing` and reschedules/re-enqueues jobs on paused queues. It never cancels execution, so paused jobs are never moved to `DeletedState` (no data loss). Pause/maintenance state is read through a per-server cache (default 2s TTL).
- `QueueOperationsStateCache` — process-wide short-TTL cache shared by the nav menu, maintenance banner, and Queues page so pause/maintenance state is read once per interval instead of per circuit.
- New nav entry **Audit Log** under Management; new nav entry **Queues** with paused-count badge.

### Added — Other

- `IDashboardAsyncAuthorizationFilter` and Hangfire filter adapters.
- `DashboardUIOptions.LoginPath` for redirecting unauthenticated users to a login page.
- `samples/SampleAppAuth` — cookie authentication demo with login form.
- `MetricsQueryCache` with per-key stampede protection for analytics queries.

### Added — Enhanced Job Details

- **Continuation dependency graph** on the Job Details page — visualizes `BackgroundJob.ContinueJobWith(...)` chains. Walks up to the root parent then expands descendants, with edge labels for the continuation condition (`on succeeded` / `on deleted` / `on any`). Clickable nodes, dashed placeholders for expired/deleted jobs, and a **Load more** button bounded by `JobGraphMaxDepth` (default 5) and `JobGraphMaxNodes` (default 30).
- **Retry summary banner** above the state history — shows the retry count and whether every attempt failed with the same exception type (a quick signal for a persistent root cause vs flaky failures). Each Processing/Failed state is numbered with its attempt index.
- **Stack trace source links** — file references in exception stack traces (`... in {path}:line {N}`) become clickable links to your source provider. Built-in presets for GitHub, GitLab (incl. self-hosted), Azure DevOps, Bitbucket, and local IDEs (`vscode://`, etc.), plus a custom `UrlPattern`. Configure via `DashboardUIOptions.SourceLink`; path normalization helpers `WithPathStrip(...)` / `WithPathReplace(...)`. Only safe URL schemes are rendered as links.

### Security

- **BREAKING:** `DashboardUIOptions.Authorization` now defaults to `LocalRequestsOnlyAuthorizationFilter` (same as Hangfire built-in dashboard). Remote hosts receive HTTP 401 unless you set `Authorization = []` or add your own filters.
- SignalR hub and Blazor circuit paths now require the same authorization as dashboard pages.
- Schema/table identifiers from configuration are validated (`^[a-zA-Z_][a-zA-Z0-9_]*$`) before use in SQL.

### Fixed

- Audit log appeared empty on SQL Server / PostgreSQL: `GetRangeFromSet` was called with `int.MaxValue`, which those providers compute as `endingAt + 1` (overflowing to a negative bound and returning zero rows). Now uses a safe upper bound.
- Queues page now lists configured/paused queues even when they hold no jobs (Hangfire's `Queues()` only returns non-empty queues), showing real `0` counts instead of a dash — so a paused, idle queue is still visible and resumable.
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
