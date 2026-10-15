# a2n.Hangfire.Dashboard

**A modern, open-source dashboard extension for [Hangfire](https://www.hangfire.io/)** — analytics, console logs, tags, and recurring job management built in.

Open source (LGPL-3.0-or-later). Requires Hangfire 1.8+ and ASP.NET Core (.NET 8, 9, or 10). Not officially supported by Hangfire OÜ.

[![NuGet](https://img.shields.io/nuget/v/a2n.Hangfire.Dashboard)](https://www.nuget.org/packages/a2n.Hangfire.Dashboard)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-LGPL--3.0--or--later-blue.svg)](LICENSE)
[![Hangfire](https://img.shields.io/badge/Hangfire-1.8+-blue.svg)](https://www.hangfire.io/)

---

![Dashboard Overview](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/overview.png)

## Get Started in 30 Seconds

```bash
dotnet add package a2n.Hangfire.Dashboard
```

```csharp
// Use in place of app.UseHangfireDashboard():
builder.Services.AddHangfireDashboardUI();
app.UseHangfireDashboardUI("/hangfire");
```

Navigate to `/hangfire`. Done.

> **Authorization:** By default, only **local requests** are allowed (same as the Hangfire dashboard). For remote access, set `Authorization = []` or add your own filters. See [CHANGELOG.md](CHANGELOG.md) and [`samples/SampleAppAuth`](samples/SampleAppAuth) for a cookie login example.

---

## 🚀 What's New in 2.5 — Recurring Schedule Heatmap

Plan controllable cron jobs around **real on-demand load**. The new **Schedule Heatmap** projects your recurring jobs — and actual ad-hoc demand — onto a **queue × day × hour** grid so you can spot collisions, find the quietest window to schedule, and rebalance load. See the [showcase below](#-recurring-schedule-heatmap).

> **2.5.7 (patch)** — The dashboard fits every screen, from a phone to an ultrawide monitor. List tables hide their least important columns first as they get narrower, and on phones each row becomes a card that still shows every value; between 992px and 1399px the sidebar becomes an icon rail, and wide screens centre the content. Long job names wrap onto up to three lines instead of being cut off ([#55](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/55)). New opt-in `UseDashboardFailureFingerprintFilter()` records a versioned fingerprint on every failed job, the first step toward grouping failures by exception ([#53](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/53), [#54](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/54)) — see [Failure fingerprints](#failure-fingerprints). See the [changelog](CHANGELOG.md).

> **2.5.6 (patch)** — On PostgreSQL, the Failed Jobs page no longer returns HTTP 500 when a failed job's state payload omits `FailedAt`. `Hangfire.PostgreSql` throws `KeyNotFoundException` for that row and one such job took down the whole page, plus the demand and execution rollup polls that use the same API. The PostgreSQL adapter now reads the page itself: missing exception fields stay empty, and the state row's `createdat` is shown when `FailedAt` is absent. Other storages are unchanged. See the [changelog](CHANGELOG.md).

> **2.5.5 (patch)** — Job arguments are shown on Job Details and under the job name in every list, and you can search them with `args:value` ([#42](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/42)). The hardcoded 5-second search timeout is now `DashboardUIOptions.SearchTimeoutSeconds` (default 30; `0` defers to the storage command timeout) and a running search can be cancelled ([#43](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/43)). The Recurring Jobs grid and the other in-memory tables sort by column ([#41](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/41)); the per-state job lists get a newest/oldest toggle. Search counts at most 1,000 matches and pages newest-first by primary key ([#45](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/45)). See the [changelog](CHANGELOG.md).

> **2.5.4 (patch)** — Recurring Jobs fixes: the **Items per page** selector now actually paginates ([#37](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/37)) — it previously changed only its own value while every job stayed rendered; the job **Id** and **Last Execution** cells link to the latest execution's details ([#38](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/38)); and the editor no longer drops `Dictionary<K,V>` / `List<T>` arguments when switching JSON ▸ Form ([#39](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/39)) — they were mapped to a reflection-driven nested sub-form that could not represent their entries. Also, a blank required parameter no longer blocks the JSON ▸ Form toggle; required-ness is enforced when you save, not when you switch views. See the [changelog](CHANGELOG.md).

> **2.5.3 (patch)** — a **Throttling** page ([#30](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/30), [#31](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/31)) for teams using [Hangfire.Throttling](https://docs.hangfire.io/en/latest/background-processing/throttling.html): semaphores with live occupancy and holders, held mutexes, rate-limit windows, and orphaned-holder detection. It auto-detects, so hosts without the package see no change. Also fixes a DST off-by-one-hour that silently dropped heatmap cells, and unguarded use of Hangfire's *optional* storage API that could take down the whole dashboard shell on third-party storages. See the [changelog](CHANGELOG.md).

> **2.5.2 (patch)** — the rollup collector no longer drops executions beyond its per-poll cap ([#29](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/29)). It scans at most 2 000 jobs per poll but used to advance its watermark past everything it had not reached, so on non-SQL storages a burst above ~2 000 completions per minute was sampled rather than aggregated. It now tracks the range it has covered and drains the backlog across later polls, counting each execution exactly once. See the [changelog](CHANGELOG.md).

> **2.5.1 (patch)** — Redis / rollup analytics fixes: the heatmap's estimated duration was stuck at `1m` ([#27](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/27)); the Duration, Queue latency, and Average state timing panels were empty ([#26](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/26)); Analytics ▸ Recurring never finished loading on large deployments and the last-results strip stayed blank ([#25](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/25)). Also the sidebar nav group no longer tears down the Blazor circuit on a fresh session ([#23](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/23)). See the [changelog](CHANGELOG.md).

| | Feature | What you get |
|---|---------|--------------|
| 🗺️ | **Seven views** | Planner (cron over ad-hoc demand with low-load "safe windows"), Punchcard, Queue × Hour, Per-queue, Calendar, Concurrency, and stagger Recommendations. |
| 🔀 | **Projected + Historical** | A storage-agnostic **Projected** source (from cron expressions) on any storage; a **Historical** source and ad-hoc demand overlay on SQL Server / PostgreSQL — or on any storage via the new rollup adapters. Degrades gracefully where unavailable. |
| 🧰 | **Rollup metrics for non-SQL storage** ([#21](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/21)) | New `a2n.Hangfire.Dashboard.Rollup` / `a2n.Hangfire.Dashboard.Redis` packages bring Analytics, heatmap Historical, and realistic **estimated durations** to Redis, in-memory, and other NoSQL storages — no SQL required. |
| 🔎 | **Drill-down & a11y** | Click any cell to inspect the jobs scheduled in that slot. Honors per-job & viewer time zones, light/dark theme, and is keyboard / screen-reader accessible. |

See the [changelog](CHANGELOG.md) for the full list.

---

## 🚀 What's New in 2.4 — Job Builder

Create and schedule jobs **with their arguments** directly from the dashboard — no code change, no redeploy. This closes a long-standing gap: the recurring editor previously built jobs with empty arguments, so parameterized methods couldn't be scheduled correctly ([#8](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/8)).

> **2.4.3 (patch)** — UI/UX fixes: the Failed Jobs table no longer hides the "Failed" column behind a horizontal scrollbar when an exception is long ([#17](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/17)); the Create Job method dropdown keeps its Contract/Implementation pill aligned for long names ([#18](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/18)); the Recurring Jobs search no longer drops characters when typing quickly ([#19](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/19)); and the dark theme now persists across navigation and new sessions ([#20](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/20)). See the [changelog](CHANGELOG.md).

> **2.4.2 (patch)** — recurring jobs now accept mixed-case/dotted ids and editable never-fire cron expressions ([#11](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/11)); a client-side filter on the Recurring Jobs page ([#13](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/13)); long job names no longer break the table layout ([#12](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/12)); a duplicate-id guard when creating; and the Audit Log page gains an items-per-page selector and paged navigation. See the [changelog](CHANGELOG.md).

> **2.4.1 (patch)** — searchable method picker; contract-aware resolution so interface/abstract targets keep their class-level `[Tag]`/`[Queue]` and display names; fixed editing a recurring job whose method takes an injected parameter ([#10](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/10)); consistent solid-red Delete buttons across all job pages. See the [changelog](CHANGELOG.md).

| | Feature | What you get |
|---|---------|--------------|
| 🧩 | **Guided parameter form** | A type-aware form generated from the method signature — text, numbers, dates, GUIDs, enums, booleans, arrays, and nested objects — with a live **JSON mirror** and a Form ⇄ JSON toggle. [→](#job-builder) |
| 🔎 | **Method discovery** | Pick from methods discovered across loaded assemblies (decorated with `JobDisplayName`, `Tag`, or `Queue`) via a **searchable picker** that badges each entry as Contract or Implementation, with **overload-safe** resolution. Hand-typed arbitrary methods are opt-in via `AllowArbitraryMethodInvocation`. [→](#job-builder) |
| 🕑 | **Visual cron builder** | Build a cron schedule field-by-field (every / specific / range / step) with a human-readable description and **next-run preview** in the selected time zone — or type a cron string manually. [→](#job-builder) |
| ➕ | **Enqueue page** | A new `/jobs/enqueue` page reuses the same builder to fire one-off jobs, not just recurring ones. [→](#job-builder) |
| ✏️ | **Typed args + edit pre-fill** | Values are converted to the method's declared types (`["report", 42]` → `string`, `int`); injected parameters (`PerformContext`, `CancellationToken`) are skipped; existing argument values are pre-filled when editing. [→](#job-builder) |

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    EnableJobManagement           = true,   // show the recurring create/edit builder and Enqueue page
    AllowArbitraryMethodInvocation = false, // opt-in: allow hand-typed type+method (default false)
});
```

See [Job Builder](#job-builder) for the full feature description.

---

## 🚀 What's New in 2.3.0 — Operational Visibility & Controls

The biggest operations-focused release yet. The dashboard goes from a **viewer you open when something breaks** to a **first-class operational tool**.

| | Feature | What you get |
|---|---------|--------------|
| 🩺 | **Health checks** | `/healthz` · `/healthz/ready` · `/healthz/full` endpoints for Kubernetes & load balancers, an ASP.NET Core `IHealthCheck` adapter, and a traffic-light **hero card** on the Home page. [→](#health-checks) |
| ⏸️ | **Queue pause / resume** | Pause a single queue from the new card-based `/queues` page. Workers reschedule jobs instead of running them — **no data loss**. [→](#operations) |
| 🚧 | **Maintenance mode** | One global toggle pauses every queue, with a persistent banner on every page. [→](#operations) |
| 📋 | **Audit log** | Every admin action recorded — who, when, what — on a new `/audit` page, attributed to the real signed-in user. [→](#operations) |
| 🔗 | **Enhanced Job Details** | Continuation **dependency graph**, a **retry summary banner**, and clickable **stack-trace source links** (GitHub/GitLab/Azure DevOps/Bitbucket/local IDE). [→](#job-dependency-graph) |

```csharp
// Liveness/readiness probes + queue-pause enforcement in one place:
builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connStr)
    .UseDashboardQueuePauseFilter());        // honour pause toggles on running servers

builder.Services.AddHealthChecks()
    .AddHangfireDashboard(tags: new[] { "ready" });   // unified /health endpoint
```

See the full [2.3.0 changelog](CHANGELOG.md) for every option and detail.

---

## About

Hangfire ships a capable monitoring UI out of the box. Many teams extend it with community packages for additional dashboard features — for example [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console), [Hangfire.Tags](https://github.com/face-it/Hangfire.Tags), and [Hangfire.RecurringJobAdmin](https://github.com/bamotav/Hangfire.RecurringJobAdmin). Dashboard analytics are also available in [Hangfire Pro](https://www.hangfire.io/pricing/).

**a2n.Hangfire.Dashboard** combines several of these capabilities in one extension: search, filters, analytics (with optional storage adapters), console viewer, tags, recurring job CRUD, SignalR realtime updates, and theme options — while using the same Hangfire job storage and APIs.

---

## Features

| Feature | Description |
|---------|-------------|
| Job monitoring | Job state pages, batch operations, servers, retries |
| Recurring jobs | Create, edit, start, and stop recurring jobs from the UI, with a client-side id/name filter |
| Schedule Heatmap | 🆕 Visualize recurring-job density by queue × day × hour and plan cron around real demand — Planner, Punchcard, Calendar, Concurrency, Recommendations, and more (see [Schedule Heatmap](#-recurring-schedule-heatmap)) |
| Job Builder | 🆕 Create & schedule jobs **with typed arguments** — guided parameter form (+ JSON), method discovery, overload-safe resolution, and one-off enqueue at `/jobs/enqueue` — see [Job Builder](#job-builder) |
| Visual cron builder | 🆕 Build cron schedules field-by-field with a human-readable description and next-run preview |
| Console output | Logs, progress bars, and colors (Hangfire.Console-compatible API) |
| Job tags | Tagging and tag cloud (Hangfire.Tags-compatible storage) |
| Throttling | 🆕 Semaphore occupancy, held mutexes, and rate-limit windows with orphaned-holder detection — appears automatically when the host uses [Hangfire.Throttling](https://docs.hangfire.io/en/latest/background-processing/throttling.html) |
| Job dependency graph | 🆕 Continuation pipeline visualization on the Job Details page (with "Load more" expansion) |
| Retry summary | 🆕 Inline banner above state history showing retry count + exception consistency |
| Stack trace links | 🆕 File references in stack traces become clickable links to GitHub/GitLab/Azure DevOps/Bitbucket/local IDE |
| Job arguments | 🆕 Job lists show each job's arguments, and the Job Details page renders the full method call with type-aware argument formatting — including jobs whose assembly the dashboard does not reference |
| Global search | Search by job ID, name, queue, tag, exception text, or job arguments (`args:value`), and filter by exception (the Exception filter) |
| Advanced filters | Filter by date, duration, state, server, and more |
| Analytics | Throughput, latency, failures, queue health (requires storage adapter — see [Packages](#packages)) |
| Health checks | 🆕 `/healthz` endpoints (liveness, readiness, full report) + at-a-glance hero card on Home — see [Health Checks](#health-checks) |
| Queue pause / maintenance | 🆕 Pause individual queues or enable global maintenance mode — see [Operations](#operations) |
| Audit log | 🆕 Every admin action recorded (who, when, what) — filterable, paged — see [Operations](#operations) |
| Failure fingerprints | 🆕 Opt-in server filter that records a versioned fingerprint of each final failure (exception type, normalized message, top application frame), the basis for grouping failed jobs by exception — see [Failure fingerprints](#failure-fingerprints) |
| Realtime updates | Live metrics via SignalR |
| Authorization | Local-only default (same as Hangfire); optional async filters and `LoginPath` redirect |
| Theming | Dark, light, or auto |
| Responsive layout | 🆕 Fits phones to ultrawide monitors: table columns hide by priority as the table narrows, rows become cards on phones, an icon-rail sidebar on laptops, and centred content on wide screens |
| Tech | Blazor Server, Bootstrap 5, Chart.js |

---

## Screenshots

### ⭐ Recurring Schedule Heatmap

Project your recurring (cron) jobs — and real ad-hoc demand — onto a **queue × day × hour** grid to spot collisions, find the quietest window to schedule, and rebalance load. Multiple views, click-through drill-down, and (with a storage adapter) a Historical/Demand source.

| Planner — cron over ad-hoc demand |
|:---:|
| ![Schedule Heatmap Planner](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/heatmap.png) |

| Punchcard | Calendar |
|:---:|:---:|
| ![Punchcard](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/heatmap-punchcard.png) | ![Calendar](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/heatmap-calendar.png) |

| Concurrency | Overlap Recommendations |
|:---:|:---:|
| ![Concurrency](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/heatmap-concurrency.png) | ![Recommendations](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/heatmap-recommendations.png) |

| Cell drill-down — jobs scheduled in a slot |
|:---:|
| ![Drill-down](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/heatmap-drilldown.png) |

### More

| Home & Realtime Charts | Console Viewer |
|:---:|:---:|
| ![Home](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/home.png) | ![Console](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/console-viewer.png) |

| Tags & Search | Recurring Jobs |
|:---:|:---:|
| ![Tags](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/tags.png) | ![Recurring](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/recurring.png) |

| Advanced Search | Analytics |
|:---:|:---:|
| ![Advanced Search](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/search.png) | ![Analytics](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/performance.png) |

| Enqueue Job (typed args) | Recurring Schedule (Cron Builder) |
|:---:|:---:|
| ![Enqueue Job](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/enqueue-job.png) | ![Recurring Schedule](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/recurring-schedule.png) |

| Light / Dark / Auto |
|:---:|
| ![Light / Dark / Auto theme](https://raw.githubusercontent.com/anwarminarso/a2n.Hangfire.Dashboard/main/docs/screenshots/light-dark.png) |

---

## Packages

| NuGet package | Purpose |
|---------------|---------|
| [`a2n.Hangfire.Dashboard`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard) | Main dashboard UI (search, console, tags, recurring admin) |
| [`a2n.Hangfire.Dashboard.SqlServer`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard.SqlServer) | Storage-specific queries + full analytics for SQL Server |
| [`a2n.Hangfire.Dashboard.PostgreSql`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard.PostgreSql) | Storage-specific queries + full analytics for PostgreSQL |
| [`a2n.Hangfire.Dashboard.Rollup`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard.Rollup) | Rollup-based analytics for non-SQL storages (Redis, in-memory, other NoSQL) |
| [`a2n.Hangfire.Dashboard.Redis`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard.Redis) | Convenience entry point for Redis — registers rollup metrics (`UseRedisStorage()`) |
| [`a2n.Hangfire.Console`](https://www.nuget.org/packages/a2n.Hangfire.Console) | Console integration (Hangfire.Console-compatible API) |
| [`a2n.Hangfire.Tags`](https://www.nuget.org/packages/a2n.Hangfire.Tags) | Tags integration (Hangfire.Tags-compatible storage) |

Without a storage adapter package, search and core dashboard features work; the **Analytics** pages and heatmap **Historical** source require a metrics adapter:

- **SQL Server / PostgreSQL** — `UseSqlServerStorage()` / `UsePostgreSqlStorage()` (direct SQL queries; best accuracy)
- **Redis / in-memory / other non-SQL** — `UseRedisStorage()` or `UseRollupMetrics()` (incremental rollup; forward-only historical data)

**Which connection each read uses.** The connection string passed to `UseSqlServerStorage()` / `UsePostgreSqlStorage()` is used only by the adapter's own queries: search, the Analytics pages, the heatmap's Historical source and estimated durations, plus the failed-job list on PostgreSQL. Everything else goes through Hangfire's `JobStorage` and therefore the host's main Hangfire connection pool, shared with the workers. That includes the realtime counters, the other job lists, job details, recurring jobs, servers and queues, and the ad-hoc demand rollup (apart from its failed-job reads on PostgreSQL). Giving the adapter a separate connection string (for example one with a small `Maximum Pool Size`) isolates those adapter queries only. To keep the remaining dashboard load low:

- `DashboardUIOptions.StatsPollingInterval` sets how often the realtime counters are read: one read per interval for all open tabs, and none while no tab is open.
- `DashboardUIOptions.Heatmap.DemandRollupEnabled = false` stops the ad-hoc demand poll (every `Polling.RollupPollInterval`, 60 seconds by default, runs even with no tab open). The heatmap's Ad-hoc and Combined layers then get no new data. It also doesn't run when the heatmap is disabled (`Heatmap.Enabled = false`).
- `DashboardUIOptions.Polling` bounds how many storage calls the dashboard runs at once and how its pages refresh; see [Polling and Slow Storage](#polling-and-slow-storage).

```csharp
// Redis (Hangfire Pro or community StackExchange.Redis storage already configured)
builder.Services.AddHangfireDashboardUI(options => options.UseRedisStorage());

// Any other non-SQL Hangfire storage
builder.Services.AddHangfireDashboardUI(options => options.UseRollupMetrics());
```

---

## Full Setup

```csharp
using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;  // Hangfire.Console-compatible API (bundled in this package)
using Hangfire.Tags;     // Hangfire.Tags-compatible API (bundled in this package)

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Hangfire")
    ?? throw new InvalidOperationException("Connection string 'Hangfire' not found.");

builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connectionString)  // or .UsePostgreSqlStorage(connectionString)
    .UseConsole()        // Enable console output
    .UseTags());         // Enable job tagging

builder.Services.AddHangfireServer();

// Basic setup (search works; analytics requires a storage adapter — see Packages)
builder.Services.AddHangfireDashboardUI();

// With storage adapter for storage-specific queries + full analytics:
// builder.Services.AddHangfireDashboardUI(options =>
// {
//     options.UseSqlServerStorage(connectionString);
//     // options.UsePostgreSqlStorage(connectionString);
// });

var app = builder.Build();

app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "My Jobs",
    DefaultTheme = "auto",  // "auto", "light", or "dark"
    EnableJobManagement = true,  // set false to hide Create/Edit/Stop and the Enqueue page
    IsReadOnly = false,  // true = view-only: every requeue/delete/enqueue/trigger/stop/start/edit/pause action is hidden and refused by the services
    JobGraphMaxDepth = 5,   // continuation graph traversal depth (default 5)
    JobGraphMaxNodes = 30,  // continuation graph node budget (default 30)
    SearchTimeoutSeconds = 30,  // Search page limit; 0 = no limit, storage command timeout applies (default 30)
    StatsPollingInterval = 2000,  // realtime metrics cadence in ms, one storage read shared by all tabs (default 2000, min 500)
    EnablePrerendering = true,  // false = skip the server prerender pass (default true)
    // Heatmap = new HeatmapOptions { DemandRollupEnabled = false },  // stop the 60 s ad-hoc demand poll
    // Polling = new PollingOptions { MaxConcurrentStorageCalls = 8 },  // storage call limit and refresh intervals, see "Polling and Slow Storage"
    // SourceLink = SourceLinkOptions.GitHub("owner/repo"),  // clickable stack-trace file links
});

app.Run();
```

### The Blazor script (`_framework/blazor.web.js`)

The dashboard's pages go interactive through the Blazor script `_framework/blazor.web.js`. If the dashboard renders but nothing reacts to clicks, a 404 on that script is the usual cause.

- **.NET 8 and .NET 9:** the script is part of the framework. Nothing to do.
- **.NET 10:** the script is a static web asset from the `Microsoft.AspNetCore.App.Internal.Assets` package. The SDK only restores that package for a host that has a `.razor` file or sets `RequiresAspNetWebAssets`, so a Web API or SPA host has no script of its own. A NuGet package can't set the property for its host, because NuGet doesn't read a package's props during restore. The net10.0 build of the dashboard therefore carries a copy of the script, taken from ASP.NET Core 10.0.12, and serves it only when the host doesn't serve its own. It logs this once at startup, at Information. To serve the host's own copy instead, which always matches its runtime, add this to the host `.csproj`:

  ```xml
  <PropertyGroup>
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
  </PropertyGroup>
  ```

  A host referencing the dashboard through `<ProjectReference>` gets its script the same way.

When running from a build output (`dotnet run`, not a `dotnet publish` folder), ASP.NET Core reads the static web assets manifest only in the `Development` environment. In another environment, run the published output or call `builder.WebHost.UseStaticWebAssets()`. On .NET 8 and .NET 9 the dashboard logs a Warning at startup when it finds no script to serve.

---

## Using with existing Hangfire code

### Console & tags compatibility

Already using `Hangfire.Console` or `Hangfire.Tags`? You can keep those packages, or switch to the bundled ones — job code and storage formats are compatible:

```csharp
// Works exactly the same
context.WriteLine("Processing order...");
context.WriteProgressBar();

[Tag("orders")]
public void ProcessOrder() { }
```

### Existing DashboardOptions? Still works.

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardOptions
{
    DashboardTitle = "My Jobs",
    Authorization = new[] { new MyAuthFilter() },
});
```

### Data compatibility

Reads the same storage format as [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console) and [Hangfire.Tags](https://github.com/face-it/Hangfire.Tags). Existing console logs and tags are visible immediately — no migration needed.

---

## Polling and Slow Storage

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

---

## Search on Large Instances

Name search and argument search (`args:`) are substring matches (`LIKE '%value%'` on SQL Server, `ILIKE '%value%'` on PostgreSQL). An ordinary index can't serve a pattern with a leading wildcard, so on a large `Job` table the cost of a search depends on how common the value is:

- **Common values are cheap.** Results come newest first by the job's primary key, so the database can stop as soon as a page is full. The total is counted up to 1,000 matches; beyond that the page shows **1,000+**, and only the first 1,000 can be paged through.
- **Rare values read the whole table.** Finding a handful of matches means checking every row. Adding a **state** or **date** filter narrows the rows that have to be checked.
- **Time limit.** A search is cancelled after `DashboardUIOptions.SearchTimeoutSeconds` (default 30; `0` leaves it to the storage's `Command Timeout`), and a running search can be cancelled from the page.

### Optional: trigram indexes on PostgreSQL

On PostgreSQL, [`pg_trgm`](https://www.postgresql.org/docs/current/pgtrgm.html) GIN indexes let `ILIKE '%value%'` use an index, which makes rare-value searches fast too. The dashboard doesn't create them. They are an opt-in for your DBA, since they take disk space and add a little write cost to every job insert. With the default `hangfire` schema:

```sql
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Argument search (args:)
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_hangfire_job_arguments_trgm
    ON hangfire.job USING gin ((arguments::text) gin_trgm_ops);

-- Name search (type and method name)
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_hangfire_job_invocationdata_trgm
    ON hangfire.job USING gin ((invocationdata::text) gin_trgm_ops);
```

`CONCURRENTLY` builds the index without blocking job processing, but it can't run inside a transaction. Trigram indexes only help once the searched value is at least 3 characters long. SQL Server has no equivalent for `LIKE '%value%'` short of Full-Text Search, which the dashboard doesn't use.

### Filter by exception

The **Exception** filter in **Advanced Filters** on the Search page finds every job that failed with one kind of failure, in any state: still failed, requeued and succeeded since, or deleted. It matches the `FailureFingerprint` parameter that `UseDashboardFailureFingerprintFilter()` stores (see [Failure fingerprints](#failure-fingerprints)), with an exact comparison on the `JobParameter` table, so it is an index lookup per job rather than a substring match. It combines with every other filter, and with the 1,000-match count and newest-first paging above.

The list offers the exceptions the Failed page shows, labeled the same way on one line (short type, message, top frame and number of failed jobs), but only those with at least one stored fingerprint, since only those can match. It is loaded when the panel opens, under `SearchTimeoutSeconds`. When no failed job has a stored fingerprint, the list is disabled with a hint to register the filter. The filter can be linked, `search?exception=v1:…`, as the **Search all jobs with this exception** link on a Failed page group does; a fingerprint in the address that isn't in the list (every failed job of it was requeued, say) still shows as selected. A value that isn't a current fingerprint is ignored.

Failures recorded before the filter was registered, or on a server without it, carry no fingerprint, so Search can't find them; only the Failed page's grouping, which computes their fingerprint from the stored exception, knows them. On storages without the SQL Server or PostgreSQL adapter (Redis and others), the scan stops at 1,000 matches; for a rare exception it reads the parameter of every scanned job, one storage call each, until `SearchTimeoutSeconds`, and a search stopped that way shows as timed out. Every labeled exception is offered there, since stored and computed fingerprints can't be told apart.

On the Failed page, a group whose failures were all recorded without the filter still links to Search, since its requeued or deleted jobs may carry a fingerprint, but says that Search may not find them.

## Job Builder

The Job Builder lets operators construct, schedule, and enqueue Hangfire jobs **with their arguments** from the dashboard — no code change or redeploy. It powers two places:

- **Recurring** — the Create/Edit recurring job form (`/recurring`), gated behind `EnableJobManagement`.
- **Enqueue** — a new one-off job page at `/jobs/enqueue` for fire-and-forget jobs, also gated behind `EnableJobManagement`.

Both share the same method picker, parameter form, and argument conversion, so behavior is identical across the two.

### Method discovery & selection

The picker offers two sources:

- **Registered methods** — discovered by scanning loaded assemblies for `public` instance/static methods whose method or declaring class carries a recognized attribute (`JobDisplayName`, `Tag`, or `Queue`). Discovery is cached for the dashboard's lifetime and resilient to assemblies that fail to load. Interface and abstract-class contracts are surfaced alongside their concrete implementations, each badged as **Contract** or **Implementation**, and the list is searchable by display label, type name, or method name.
- **Custom methods** — a full type name + method name typed by hand, validated on demand. This is **opt-in** and disabled by default:

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    AllowArbitraryMethodInvocation = true,   // default false — keeps arbitrary invocation opt-in
});
```

Resolution is **overload-safe**: when a method name has multiple overloads, the single overload whose parameter count and types match the supplied arguments is selected. Ambiguous, missing, or non-matching methods are rejected with an identifying error and never touch storage.

### Parameter form

A type-aware form is generated from the method signature, one control per parameter:

| Parameter type | Control |
|----------------|---------|
| `string` | text input |
| integers / floats | number input (whole vs. fractional, range-checked) |
| `DateOnly` / `TimeOnly` / `DateTime` / `DateTimeOffset` | date / time / datetime-local picker |
| `Guid` | text input |
| `bool` / `bool?` | checkbox / tri-state (unset = null) |
| enum / `[Flags]` enum | single-select / multi-select |
| scalar arrays (`T[]`) | add/remove list |
| nested object (class, depth ≤ 5) | collapsed placeholder + explicit "Add" to instantiate |
| anything else / depth > 5 | JSON textarea |

Key behaviors:

- **Form ⇄ JSON toggle** — switch to a raw `["value", 42]` JSON textarea at any time; the form and JSON stay in sync (a read-only JSON mirror is shown alongside the form).
- **Typed conversion** — values are converted to each parameter's declared CLR type before the job is stored, matching how Hangfire serializes arguments.
- **Injected parameters skipped** — `PerformContext`, `IJobCancellationToken`, and `CancellationToken` are excluded from the form and filled by Hangfire at runtime.
- **Empty fields are total** — a blank field resolves to `null` for nullable types or `default(T)` for non-nullable types, never an error.
- **Edit pre-fill** — when editing an existing recurring job, current argument values are loaded back into the form.

### Schedule builder (recurring only)

For recurring jobs, the Schedule Builder offers a field-by-field cron editor (minute, hour, day-of-month, month, day-of-week) with an **Every / Specific / Range / Step** mode per field, plus a manual cron input. It shows a human-readable description and the **next occurrence** in the selected time zone (UTC when none is chosen). Unparseable expressions are flagged and block submission. (Cron parsing uses Cronos, already bundled with Hangfire — no new dependency.)

### Queue handling

The queue control is editable with suggestions from current queues (defaulting to `default`). When the target method or its declaring class carries a `[Queue(...)]` attribute, the control becomes read-only and shows a precedence notice, because Hangfire's `QueueAttribute` overrides the stored queue at state election.

### Gating

- **Read-only mode** (`IsReadOnly = true`) — a persistent banner is shown and the submit controls are disabled. Read-only mode applies to the whole dashboard, not only the builder: see `IsReadOnly` in [Full Setup](#full-setup).
- **Job management** (`EnableJobManagement = false`) — the recurring create/edit builder is hidden, the navigation no longer links to the Enqueue page, and the `/jobs/enqueue` route returns Not Found (read-only gating still applies). Defaults to `true`.
- **Arbitrary methods** (`AllowArbitraryMethodInvocation = false`, default) — only discovered registered methods may be selected.

---

## Job Dependency Graph

The Job Details page includes a continuation visualization for jobs created via `BackgroundJob.ContinueJobWith(...)`. The graph walks up to the root parent (via the `Awaiting` state's `ParentId`), then expands all descendants by parsing each job's `Continuations` parameter. Edge labels show the continuation condition (`on succeeded`, `on deleted`, `on any`).

The card only appears when a job is part of a continuation chain — standalone jobs render nothing. Each node is clickable and navigates to that job's details. Expired or deleted jobs render as dashed placeholders so the graph stays consistent.

Traversal is bounded by two options to keep page loads fast:

| Option | Default | Description |
|--------|---------|-------------|
| `JobGraphMaxDepth` | `5` | Maximum hops in either direction (ancestors or descendants) |
| `JobGraphMaxNodes` | `30` | Maximum total nodes materialized |

When either limit is hit, the card shows a `truncated` badge and a **Load more** button that doubles the node budget and adds depth (`+3`) on each click, up to a hard ceiling of 200 nodes / depth 12. Each click triggers exactly one storage round-trip per newly visited node, so expansion is incremental.

---

## Stack Trace Source Links

When a job fails, the Job Details page shows the exception stack trace. By default the stack trace is plain text. Set `DashboardUIOptions.SourceLink` to turn `... in {path}:line {N}` references into clickable links pointing to your source provider:

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "My Jobs",
    SourceLink = SourceLinkOptions.GitHub("anwarminarso/a2n.Hangfire.Dashboard", branch: "main"),
});
```

### Built-in presets

```csharp
SourceLinkOptions.GitHub("owner/repo");                                        // github.com
SourceLinkOptions.GitLab("group/repo");                                        // gitlab.com
SourceLinkOptions.GitLab("group/repo", host: "git.mycompany.com");             // self-hosted GitLab
SourceLinkOptions.AzureDevOps("org", "project", "repo");                       // dev.azure.com
SourceLinkOptions.Bitbucket("workspace/repo");                                 // bitbucket.org
SourceLinkOptions.Local();                                                     // vscode://file/{path}:{line}
SourceLinkOptions.Local(protocol: "vscode-insiders");                          // VS Code Insiders
SourceLinkOptions.Local(protocol: "cursor");                                   // Cursor
```

### Self-hosted / proprietary providers

Use `UrlPattern` directly with `{path}` and `{line}` placeholders:

```csharp
SourceLink = new SourceLinkOptions
{
    UrlPattern = "https://gitea.mycompany.com/team/repo/src/branch/main/{path}#L{line}",
};
```

### Visual Studio (Windows)

Visual Studio does not ship with a built-in URL protocol handler. To enable links that open files in Visual Studio, install a third-party handler such as [VsHandler](https://marketplace.visualstudio.com/items?itemName=Slvier.VsHandler) and configure:

```csharp
SourceLink = new SourceLinkOptions
{
    UrlPattern = "vs://open?File={absolutePath}&Line={line}",
};
```

For VS Code (cross-platform), the built-in `Local()` preset works out of the box.

### Path normalization

Stack traces often contain absolute paths from the build agent (e.g. `C:\jenkins\workspace\proj\src\Foo.cs`) that don't match the repository layout. Use `WithPathStrip("src")` to strip everything before the `/src/` segment:

```csharp
SourceLink = SourceLinkOptions.GitHub("owner/repo")
    .WithPathStrip("src");
// "C:\jenkins\workspace\proj\src\Models\Order.cs" → "src/Models/Order.cs"
```

For more complex transforms, use `WithPathReplace(pattern, replacement)` (regex) or set `PathTransform` to a custom `Func<string, string>`.

### Privacy note

Linked URLs only work when the viewer has access to the source provider. Private repositories return 404 to unauthenticated users. The dashboard does not embed source content — it only generates outbound links.

---

## Health Checks

The dashboard exposes a structured health endpoint suitable for Kubernetes probes, load balancer health checks, and status pages.

| Endpoint | Use case | Checks |
|----------|----------|--------|
| `GET /{dashboard}/healthz` | Liveness probe (K8s) | Storage probe |
| `GET /{dashboard}/healthz/ready` | Readiness probe (K8s) | Storage + servers |
| `GET /{dashboard}/healthz/full` | Status pages, dashboard hero card | All six checks below |

HTTP status follows the K8s convention: `200` for `Healthy` or `Degraded`, `503` for `Unhealthy`.

### Built-in checks

| Key | What it covers |
|-----|----------------|
| `storage` | Round-trip time of a `GetStatistics()` call to the Hangfire storage backend |
| `servers` | Total vs alive vs stale heartbeat (per `ServerHeartbeatTolerance`) |
| `queue_depth` | Highest queue length compared to warn/critical thresholds |
| `stuck_processing` | Processing jobs older than `StuckProcessingMinutes` (sample size: 200) |
| `failure_rate` | Last-hour failure percentage from Hangfire's hourly counters |
| `recurring_jobs` | Recurring jobs whose `NextExecution` is older than `RecurringMissedTolerance` |

**How a slow storage is reported.** The `storage` probe reuses the realtime counters' latest read when it is younger than `StatsPollingInterval`, and reports how long that read took, so frequent probes add no storage load. Every read the checks make waits at most `HealthThresholds.StorageProbeTimeoutMs` (default 10 000 ms) and goes through the dashboard's storage call limit (see [Polling and Slow Storage](#polling-and-slow-storage)), so a stalled storage can't pile up probe threads. When the probe fails or times out, `storage` is `Unhealthy` and the other checks are not run: each is reported `Unhealthy` with `"skipped": true` in its `data`, instead of failing on its own after another timeout. A check other than `storage` that times out is reported `Degraded` with `"timedOut": true`. Reports are cached for `Polling.HealthReportCacheTtl` (default 5 s) and shared by the endpoints, the `IHealthCheck` adapter and the hero card.

### Sample response (`/hangfire/healthz/full`)

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

### Configuration

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    // AllowAnonymous (default) so K8s/LB probes work without auth.
    // Use LocalOnly for loopback-only probes, or RequireDashboardAuth for the full filter chain.
    HealthCheckAuthorizationMode = HealthCheckAuthorization.AllowAnonymous,

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

### Plug into ASP.NET Core HealthChecks

If your host already exposes a unified `/health` endpoint aggregating multiple dependencies (database, message broker, Hangfire, ...), register the dashboard as an `IHealthCheck`:

```csharp
builder.Services.AddHealthChecks()
    .AddHangfireDashboard(tags: new[] { "ready" });

app.MapHealthChecks("/health");                                   // all checks
app.MapHealthChecks("/health/ready",                               // readiness subset
    new() { Predicate = c => c.Tags.Contains("ready") });
```

The adapter reads the same cached full report as the dashboard's own endpoint and hero card, so the results match. For your own code, `HealthCheckService` offers `CheckLivenessAsync`, `CheckReadinessAsync` and `CheckFullAsync`; the synchronous `CheckLiveness()`, `CheckReadiness()` and `CheckFull()` are kept and block the calling thread until the report is ready.

### Hero card

The Home page shows a top-of-page traffic light (Healthy / Degraded / Critical) with per-issue descriptions and deep-link actions ("View processing →", "View failed →"). It refreshes every `Polling.HealthCardRefreshInterval` (10 seconds by default) and backs off while the report can't be read; the Refresh button reads storage at once. Checks skipped because storage is unreachable aren't listed as separate issues. The detailed 8-card stat grid is now collapsed behind a "Detailed metrics" toggle to keep the hero front-and-center.

---

## Operations

The dashboard ships day-to-day operational controls so ops teams don't need to redeploy or write SQL to handle common scenarios.

### Queue pause / resume

The `/queues` page lists every queue with its current status and a per-row pause toggle. Paused queues stop workers from executing jobs — the dashboard's server filter intercepts each fetched job and reschedules it back into the future (default +30s) instead of running it.

```csharp
// Required: register the server filter on the Hangfire pipeline so running servers
// honour the pause toggles set from the dashboard.
builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connStr)
    .UseDashboardQueuePauseFilter());          // <-- here
```

Without this call, the dashboard still records the pause and shows the badge — but jobs keep executing on running workers until the host restarts with the filter enabled.

Customize behaviour via `DashboardUIOptions.QueueOperations`:

```csharp
QueueOperations = new QueueOperationsOptions
{
    Enabled         = true,
    Behavior        = PausedJobBehavior.Reschedule,   // or Requeue (busy-loop, use sparingly)
    RescheduleDelay = TimeSpan.FromSeconds(30),
    PauseStateCacheTtl = TimeSpan.FromSeconds(2),     // server-side cache window
}
```

### Maintenance mode

A single global toggle (top of the `/queues` page) pauses every queue at once. While maintenance is active, a persistent yellow banner is rendered on every dashboard page with the operator's reason and a `Manage →` link. Disable maintenance to resume — individual queue pauses set before maintenance was enabled remain in effect.

### Failure fingerprints

An optional server filter records which kind of failure each failed job had, so failed jobs can be grouped by exception. Register it on every host that runs a Hangfire server:

```csharp
builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connStr)
    .UseDashboardFailureFingerprintFilter());   // <-- here
```

When a job enters the Failed state, the filter stores a `FailureFingerprint` job parameter such as `v1:5a633710a9709394`, which is a hash of three things:

- **The exception type.** When the exception wraps others, the innermost exception's type is included and its message is used. EF Core, for example, wraps every database error in a `DbUpdateException` with the same message, so without this `Column 'Email' cannot be null` and `Column 'Phone' cannot be null` would look identical. Generic arguments are dropped from type names because they carry assembly versions.
- **The first line of the message, with run-specific values replaced.** GUIDs, dates and times, hex values and hashes, e-mail addresses, IPv6 and IPv4 addresses, URL query strings and numbers become placeholders, so `Transaction (Process ID 57) was deadlocked…` and `Transaction (Process ID 112) was deadlocked…` are the same failure. Quoted names are kept, so `Column 'Email' cannot be null` and `Column 'Phone' cannot be null` stay apart.
- **The top stack frame from your own code.** This is the first frame in the root namespace of the job's type, e.g. `MyApp` for `MyApp.Jobs.OrderJob`, so the same database error from two jobs doesn't collapse into one group at a shared driver frame. If no frame is in that namespace, the first frame outside the framework and common libraries is used (`System`, `Microsoft`, `Hangfire`, `Npgsql`, `Dapper`, `Newtonsoft`, `Polly`, `StackExchange`, `Azure`, `Amazon` and others; the full list is `FailureFingerprint.LibraryNamespaces`). Parameters, file paths and line numbers are dropped, and async, lambda and local-function names map to the method that contains them, so a rebuild or a moved line doesn't change the fingerprint.

Hangfire keeps only the first 100 lines of a failure's exception details. In a very deep stack, such as a long async chain through framework code, your own frames can fall past that limit. The fingerprint then uses the first remaining frame outside those libraries, or the first frame.

Only failures that end in the Failed state are fingerprinted, not the attempts that `AutomaticRetry` retries. The parameter stays on the job after it is requeued or deleted, and a later failure overwrites it. It is stored as a plain string and is visible under **Parameters** on the Job Details page.

#### Grouping on the Failed page

The **Exception** filter next to the page-size selector on **Jobs → Failed** groups the failed jobs by fingerprint, most failed jobs first. Each entry shows the short exception type, as `Outer → Inner` when one exception wraps another (full names in the tooltip), the normalized message, the top frame and the number of failed jobs. Selecting one shows that exception's total and the jobs that failed most recently; the page-size selector sets how many. A job that was scheduled, retried or requeued before it failed is placed by when it failed, not when it was created, on every storage. A group can be linked: the address becomes `jobs/failed?exception=v1:…`. The filter works from the keyboard: the arrow keys, Home and End move through the list, Enter or Space selects, and Escape closes it.

"All exceptions" is the default and shows the page as before. The groups are only read when the filter is opened or a group is linked. While a group is shown or the list is open, they are refreshed when the failed count changes; otherwise they are read again the next time the filter is opened. Grouping is stopped after `DashboardUIOptions.SearchTimeoutSeconds`, like a search.

Fingerprints stored by the filter are counted exactly, by a query over the `Job` and `JobParameter` tables. Failures without one (recorded before the filter was registered, or on a server without it) are fingerprinted from their stored exception data and job type when the page is read. Only the 1,000 that failed most recently are read. When there are more, the page says so, the counts are shown as lower bounds (`1,000+`), and older failures may be missing from the list. Registering the filter on every server gives exact counts. Nothing is written back, so grouping also works in read-only mode.

In a deployment where only some servers register the filter, a job keeps the fingerprint of the last failure recorded by a server with the filter. If it is requeued and then fails on a server without it, it is still counted under that earlier failure.

Only the 50 largest groups are labeled, each from the job that failed last. Storages without the SQL Server or PostgreSQL adapter (Redis and others) have no fast way to query job parameters. On those, the latest 1,000 failed jobs are fingerprinted in memory, with or without the filter, so the page doesn't suggest the filter when counts there are lower bounds. A custom `IStorageQueryProvider` doesn't offer the filter unless it implements `SupportsFailureGrouping`, `GetFailureGroupsAsync` and `GetFailureGroupJobsAsync`. It can also implement `GetFailureGroupJobIdsAsync` for the group actions below; by default that takes the ids from `GetFailureGroupJobsAsync`.

#### Requeue or delete a whole group

With a group selected, **Requeue all** and **Delete all** act on every failed job with that exception, not only the ones on screen. When you click one, the page reads the ids of the group's jobs and asks for confirmation with their exact number. Both actions ask, because requeuing thousands of jobs has an impact too. Only those jobs are changed, and each one only while it is still in the Failed state. A job that was retried, requeued or deleted in the meantime is skipped, and one that fails with the same exception after the click is left alone.

The jobs are changed 500 at a time. Each batch is one `jobs.requeued` or `jobs.deleted` entry in the audit log, with the requested and succeeded counts and the group's fingerprint. While it runs, the page shows how far it has got ("Requeued 1,500 of 12,345") and the buttons are disabled. **Cancel**, or leaving the page, stops it after the current batch. At the end the page says how many jobs were changed and how many were skipped because they had left Failed, then reloads the groups and the list. If a state change fails, for example because the storage is unreachable, the run stops there and the page says exactly how many jobs were changed; the batch's audit entry records them too.

The ids come from the same data as the counts: every failed job with the stored fingerprint, plus the jobs without one that the grouping read. When the counts are lower bounds, older failures that weren't read are not included, and the confirmation says so; registering the filter on every server avoids that. The group actions are hidden when `DashboardUIOptions.IsReadOnly` is set.

The rows of a group can also be selected and requeued or deleted with **Requeue** and **Delete**, as on the full list. On the Failed page those buttons also change only jobs that are still failed. A requeued or deleted job keeps its `FailureFingerprint` parameter.

#### Search by exception

Because the parameter survives requeues and deletes, the [Exception filter on Search](#filter-by-exception) finds every job that failed with a given exception, whatever its state now. **Search all jobs with this exception** on a Failed page group opens that search.

### Audit log

Every admin action performed through the dashboard is recorded:

| Category | Actions |
|----------|---------|
| Job actions | requeue, delete, batch requeue, batch delete |
| Recurring | create, update, delete, trigger, stop, start |
| Queue ops | queue pause, queue resume, maintenance enabled, maintenance disabled |

Each entry captures: timestamp (UTC), user (or `(anonymous)` for unauthenticated local requests), client IP, action, target, optional reason, and a small metadata bag (e.g., batch counts).

The `/audit` page filters by action prefix (job/jobs/recurring/queue/maintenance), user (substring), and target, with an items-per-page selector and paged navigation. Storage uses Hangfire's KV primitives — no schema changes. Configurable retention:

```csharp
AuditLog = new AuditLogOptions
{
    Enabled    = true,
    Retention  = TimeSpan.FromDays(30),
    MaxEntries = 10_000,
}
```

Old entries beyond either bound are trimmed on writes (best-effort, ~every 50 entries).

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| UI | Blazor Server (Interactive SSR) |
| Styling | Bootstrap 5.3 + Bootstrap Icons |
| Charts | Chart.js + chartjs-plugin-streaming |
| Realtime | ASP.NET Core SignalR |
| Theme | `data-bs-theme` + localStorage persistence |
| Targets | .NET 8, .NET 9, .NET 10 |

## Project Structure

```
src/
├── a2n.Hangfire.Dashboard/             # Main dashboard (Blazor + SignalR + Analytics)
├── a2n.Hangfire.Dashboard.SqlServer/   # SQL Server adapter (Dapper + T-SQL)
├── a2n.Hangfire.Dashboard.PostgreSql/  # PostgreSQL adapter (Dapper + Npgsql)
├── a2n.Hangfire.Dashboard.Rollup/      # Rollup metrics engine for non-SQL storages
├── a2n.Hangfire.Dashboard.Redis/       # Redis / non-SQL entry point (UseRedisStorage)
├── a2n.Hangfire.Console/               # Console integration (Hangfire.Console-compatible)
└── a2n.Hangfire.Tags/                  # Tags integration (Hangfire.Tags-compatible)

tests/
├── a2n.Hangfire.Dashboard.Tests/
├── a2n.Hangfire.Console.Tests/
└── a2n.Hangfire.Dashboard.PostgreSql.Tests/

samples/
├── SampleApp/          # Full demo (all features)
├── SampleAppAuth/      # Cookie authentication example
├── SampleAppMvc/       # ASP.NET Core MVC host
├── SampleAppRazor/     # Razor Pages host
├── SampleAppBlazor/    # Blazor host
└── SampleAppOrig/      # Startup-class host pattern
```

## Running the Sample

```bash
git clone https://github.com/anwarminarso/a2n.Hangfire.Dashboard.git
cd a2n.Hangfire.Dashboard/samples/SampleApp
dotnet run
```

Open `https://localhost:7100/hangfire` to see it in action.

For authentication with a login page, run `samples/SampleAppAuth` instead.

---

## Roadmap

| Version | Status | Scope |
|---------|--------|-------|
| v1.0 | ✅ Done | Core dashboard pages + Console + Tags + Recurring Admin |
| v1.1 | ✅ Done | Global search & advanced filters |
| v1.2 | ✅ Done | Razor Class Library (NuGet-ready) |
| v1.3–v1.6 | ✅ Done | Storage adapters (SQL Server, PostgreSQL) + Analytics dashboard |
| v2.0 | ✅ Done | Feature-complete for current scope |
| v2.1 | ✅ Done | Search refactor + JobDisplayName + SQL Server fixes |
| v2.1.1 | ✅ Done | WebSocket fix for Startup-pattern host apps |
| v2.2 | ✅ Done | Processing progress circle, Fetched page, delete confirmations, mobile nav fix |
| v2.2.1 | ✅ Done | Security & auth hardening, default auth filter, LoginPath, SignalR/Blazor auth |
| v2.3.0 | ✅ Done | **Operational visibility & controls** — health checks (`/healthz` + `IHealthCheck` adapter) & hero card, queue pause/resume, maintenance mode, audit log |
| v2.3.1 | ✅ Done | Realtime analytics fixes — SQL Server `GROUP BY` (error 144) fix, fixed-cadence broadcast loop, NuGet XML docs |
| v2.4.0 | ✅ Done | **Job Builder** — create/schedule jobs with typed arguments, guided parameter form (+ JSON), method discovery, overload-safe resolution, visual cron builder, one-off enqueue page ([#8](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/8)) |
| v2.4.1 | ✅ Done | **Job Builder follow-up** — searchable method picker, contract-aware (interface/abstract) resolution + display names, injected-parameter edit fix ([#10](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/10)), consistent destructive-action buttons |
| v2.5.0 | ✅ Done | **Recurring Schedule Heatmap** ([#14](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/14)) — visualize recurring-job scheduling density by queue × day × hour: Planner (cron over ad-hoc demand), Punchcard, Queue × Hour, Per-queue, Calendar, Concurrency, and stagger Recommendations; plus rollup metrics adapters (Rollup / Redis) for non-SQL storages, incl. storage-agnostic estimated durations ([#21](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/21)) |
| v2.5.1 | ✅ Done | **Redis / rollup analytics fixes** — heatmap estimated duration stuck at `1m` ([#27](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/27)), empty Duration / Queue latency / State timing panels ([#26](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/26)), Analytics ▸ Recurring hanging on large deployments plus blank last-results strip ([#25](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/25)); also the nav group crash fix ([#23](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/23)) |
| v2.5.2 | ✅ Done | **Rollup completeness** — the rollup collector dropped executions beyond its per-poll cap, so on non-SQL storages a burst of more than 2 000 completions per minute was sampled rather than aggregated ([#29](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/29)). It now tracks the range it has covered and drains the backlog across later polls |
| v2.5.3 | ✅ Done | **Throttling visibility, DST-correct heatmap, storage portability** — auto-detecting Throttling pages for semaphores / mutexes / rate-limit windows with orphaned-holder detection ([#30](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/30), [#31](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/31), Detach deferred); heatmap window is now seven *local* days, fixing cells silently dropped across DST transitions; extended-storage-API guards so third-party storages no longer fail the dashboard shell; first CI workflow |
| v2.5.4 | ✅ Done | **Recurring Jobs fixes** — page-size selector now paginates, with `Pager` on both the active and Stopped tables ([#37](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/37)); Id / Last Execution link to the latest execution's details ([#38](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/38)); `Dictionary<K,V>` / `List<T>` arguments survive the JSON ▸ Form switch instead of being dropped ([#39](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/39)); blank required parameters no longer block the view toggle |
| v2.5.5 | ✅ Done | **Job arguments, search, and sorting** — arguments rendered on Job Details and list rows, plus `args:` search ([#42](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/42)); configurable search timeout with a Cancel button ([#43](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/43)); sortable in-memory grids and a newest/oldest toggle on the job lists ([#41](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/41)); search badge clears its filter input ([#44](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/44)); search counts at most 1,000 matches and pages by primary key ([#45](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/45)) |
| v2.5.6 | ✅ Done | **Failed Jobs on PostgreSQL** — the page no longer returns HTTP 500 when a failed state's payload omits `FailedAt`; the PostgreSQL adapter reads that page itself, and the demand and execution rollups use the same reader |
| v2.5.7 | ✅ Done | **Responsive layout, wrapping job names, failure fingerprints** — priority columns and phone cards on every list table, icon-rail sidebar on laptops, capped content on wide screens ([#59](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/59)); long job names wrap instead of being cut off ([#55](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/55)); opt-in `FailureFingerprintFilter`, step 1 of grouping failed jobs by exception ([#53](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/53), [#54](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/pull/54)) |
| v2.6.0 | Planned | **Integrations** — Prometheus `/metrics`, OpenTelemetry trace links, read-only REST API, CSV/JSON export |
| v2.7.0 | Planned | **Customization** — white-label theming, show/hide built-in pages, saved views |
| v3.0 | Planned | Stretch goals & long-term backlog (notifications & alert rules, Gantt timeline, multi-instance federation, replay, etc.) |

See the full [roadmap](docs/ROADMAP.md) for details.

---

## Contributing

Contributions welcome — bug reports, feature requests, documentation improvements, and pull requests.

```bash
git clone https://github.com/anwarminarso/a2n.Hangfire.Dashboard.git
cd a2n.Hangfire.Dashboard
dotnet build src/Hangfire\ Dashboard.slnx
dotnet test
cd samples/SampleApp && dotnet run
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for fork workflow, code style, and pull request guidelines.

## License

LGPL-3.0-or-later — see [LICENSE](LICENSE).

## Acknowledgments

This project builds on the excellent work of the Hangfire community:

- [Hangfire](https://www.hangfire.io/) — the background job framework
- [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console) — console output for background jobs
- [Hangfire.Tags](https://github.com/face-it/Hangfire.Tags) — job tagging
- [Hangfire.RecurringJobAdmin](https://github.com/bamotav/Hangfire.RecurringJobAdmin) — recurring job management UI
- [Hangfire.Community.Dashboard.Heatmap](https://github.com/brodrigz/Hangfire.Community.Dashboard.Heatmap) by Bruno Rodrigues (brodrigz) — design inspiration for the recurring schedule heatmap (clean-room Blazor implementation, no source code copied)

Community extensions are listed on the [Hangfire Extensions](https://www.hangfire.io/extensions.html) page. This project is community-maintained and is not officially supported by Hangfire OÜ.

---

<p align="center">
  <sub>Built with ☕ by <a href="https://github.com/anwarminarso">Anwar Minarso</a></sub>
</p>
