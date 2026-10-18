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

> **Authorization:** By default, only **local requests** are allowed (same as the Hangfire dashboard). For remote access, add a filter that checks who the user is — see [Authorization](#authorization) and [`samples/SampleAppAuth`](samples/SampleAppAuth) for a cookie login example. An empty filter list does **not** open the dashboard: it falls back to local requests only.

---

## 🚀 What's New in 2.6.0 — Per-user Read-only, Safer Defaults, Slow-storage Resilience

> **This release fixes security issues, and some fixes change defaults.** Read [Upgrading](CHANGELOG.md#upgrading) in the changelog before deploying.

| | Feature | What you get |
|---|---------|--------------|
| 🔐 | **Per-user read-only** | `IsReadOnlyFunc` is honored with the `DashboardOptions` overload and evaluated on every page load. Write actions are hidden and refused by the services, and the header shows a **Read-only** badge with the reason. `IsReadOnly` now blocks every action that changes jobs, recurring jobs or queues ([#73](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/73)). [→](#authorization) |
| 🛡️ | **Safer authorization defaults** | No filter at all now means local requests only. `AllowAllRequestsAuthorizationFilter` opens the dashboard explicitly and shows a banner on every page. Hangfire filters receive the `DashboardOptions` you configured. [→](#authorization) |
| 🩺 | **Safer health endpoints** | Health responses report exception types, never messages. `/healthz/full` requires the dashboard's authorization by default, while `/healthz` and `/healthz/ready` stay open for probes. [→](#who-sees-what) |
| 🐢 | **Slow-storage resilience** | A dashboard-wide limit on concurrent storage reads, refresh loops that never overlap and back off while storage is failing, and bounded async health checks, so a slow storage can't starve the host's thread pool ([#64](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/64)). [→](#polling-and-slow-storage) |
| ⚡ | **In-process realtime feed** | Components get broadcasts in-process instead of opening SignalR connections to the server's own URL, which failed behind cookie authentication. One storage read per interval, however many tabs are open ([#61](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/61)). |
| 🧯 | **Failed jobs grouped by exception** | An Exception filter on the Failed page groups failures by fingerprint, with counts, labels and linkable groups ([#53](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/53)). [→](#grouping-on-the-failed-page) |
| 🧩 | **Fixes** | .NET 10 hosts without Blazor components of their own get `blazor.web.js` ([→](#the-blazor-script-_frameworkblazorwebjs)); on SQL Server, search and the job lists seek each job's current state instead of scanning `State` ([#84](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/84)); confirmation dialogs work from the keyboard and with screen readers ([#74](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/74)). |

**Earlier releases:** 2.5 added the [Recurring Schedule Heatmap](#-recurring-schedule-heatmap) and rollup metrics for non-SQL storages, 2.4 the [Job Builder](#job-builder), and 2.3 [health checks](#health-checks), queue pause and the audit log ([Operations](#operations)). Every release, including the patch releases, is described in the [changelog](CHANGELOG.md).

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
| Schedule Heatmap | Visualize recurring-job density by queue × day × hour and plan cron around real demand — Planner, Punchcard, Calendar, Concurrency, Recommendations, and more (see [Schedule Heatmap](#-recurring-schedule-heatmap)) |
| Job Builder | Create & schedule jobs **with typed arguments** — guided parameter form (+ JSON), method discovery, overload-safe resolution, and one-off enqueue at `/jobs/enqueue` — see [Job Builder](#job-builder) |
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
| Analytics | Throughput, latency, failures, queue health (requires storage adapter — see [Packages](#packages)) |
| Health checks | `/healthz` endpoints (liveness, readiness, full report) + at-a-glance hero card on Home. 🆕 No exception messages in responses, and the full report requires the dashboard's authorization — see [Health Checks](#health-checks) |
| Queue pause / maintenance | Pause individual queues or enable global maintenance mode — see [Operations](#operations) |
| Audit log | Every admin action recorded (who, when, what) — filterable, paged — see [Operations](#operations) |
| Failure grouping | 🆕 The Failed page groups failed jobs by exception, with counts and linkable groups — see [Grouping on the Failed page](#grouping-on-the-failed-page). The opt-in fingerprint filter gives exact counts — see [Failure fingerprints](#failure-fingerprints) |
| Realtime updates | Live metrics via SignalR, fed in-process to the dashboard's own pages |
| Authorization | 🆕 Local-only default, also when no filter is configured; per-user read-only via `IsReadOnlyFunc`; optional async filters and `LoginPath` redirect — see [Authorization](#authorization) |
| Slow-storage resilience | 🆕 Bounded concurrent storage reads and refresh loops that back off while storage is failing — see [Polling and Slow Storage](#polling-and-slow-storage) |
| Theming | Dark, light, or auto |
| Responsive layout | Fits phones to ultrawide monitors: table columns hide by priority as the table narrows, rows become cards on phones, an icon-rail sidebar on laptops, and centred content on wide screens |
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

## Authorization

By default only local requests are allowed (`LocalRequestsOnlyAuthorizationFilter`, the same rule as Hangfire's dashboard). For remote access, add filters that check who the user is. Every filter in `Authorization` and `AsyncAuthorization` must pass, and they cover the pages, the Blazor circuit and the SignalR hub:

```csharp
public sealed class HangfireAdminFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(HttpContext context)
        => context.User.Identity?.IsAuthenticated == true && context.User.IsInRole("HangfireAdmin");
}

app.UseAuthentication();
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    Authorization = [new HangfireAdminFilter()],
    LoginPath = "/login",   // optional: redirect instead of 401
});
```

[`samples/SampleAppAuth`](samples/SampleAppAuth) is a complete cookie login example.

- **No filter at all means local only.** If `Authorization` and `AsyncAuthorization` are both empty or null, the dashboard accepts local requests only and logs a warning at startup. This is a change from 2.5.x, where an empty list let everyone in.
- **Opening the dashboard to everyone has to be explicit.** When access is enforced elsewhere (an authenticating reverse proxy, a private network), use `Authorization = [new AllowAllRequestsAuthorizationFilter()]`. The dashboard then logs a warning at startup and shows a banner on every page. The health endpoint in `RequireDashboardAuth` mode is open too, since it uses the same filters.
- **Read-only users.** `IsReadOnly = true` makes the dashboard view-only for everyone. With the `DashboardOptions` overload, `IsReadOnlyFunc` decides per user (see [Existing DashboardOptions](#existing-dashboardoptions-still-works)). Either way, write actions are hidden, refused by the services, and the header shows a **Read-only** badge with the reason.
- **Permissions are evaluated when a page loads.** An open tab keeps the access it had when it was loaded: if a user's role is revoked, the change applies at their next page load, not in tabs they already have open.

### Behind a reverse proxy on the same host

A proxy on the same machine forwards every request from `127.0.0.1`, so the default local-only filter would let **every** client in. Make ASP.NET Core use the forwarded client address before mapping the dashboard:

```csharp
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Loopback proxies are trusted by default; add others with options.KnownProxies / KnownNetworks.
});

app.UseForwardedHeaders();               // before UseHangfireDashboardUI
app.UseHangfireDashboardUI("/hangfire");
```

The proxy must set `X-Forwarded-For`. Better still, use a filter that checks the user rather than the address.

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

> **Don't reference `a2n.Hangfire.Console` together with the original `Hangfire.Console`** (or `a2n.Hangfire.Tags` with `Hangfire.Tags`). The bundled packages use the same namespaces so job code compiles unchanged, which means both packages in one project give ambiguous type errors. Pick one per project.

### Existing DashboardOptions? Still works.

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardOptions
{
    DashboardTitle = "My Jobs",
    Authorization = new[] { new MyAuthFilter() },
    // Evaluated once per page load with the request's DashboardContext (since 2.6.0).
    IsReadOnlyFunc = context => !context.GetHttpContext().User.IsInRole("HangfireAdmin"),
});
```

Your Hangfire authorization filters and `IsReadOnlyFunc` receive a `DashboardContext` carrying this exact `DashboardOptions` instance. `IsReadOnlyFunc` is evaluated once when a page loads; the result holds for that tab until it is reloaded. If the function throws, the page is read-only and a warning is logged.

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

### Who sees what

| `HealthCheckAuthorizationMode` | `/healthz`, `/healthz/ready` | `/healthz/full` |
|---|---|---|
| `AnonymousProbes` (default) | any caller | dashboard authorization required (`401` otherwise) |
| `AllowAnonymous` | any caller | any caller |
| `LocalOnly` | local requests only | local requests only |
| `RequireDashboardAuth` | dashboard authorization required | dashboard authorization required |

Whatever the mode, a caller who doesn't pass the dashboard's own authorization filters gets only the overall status and each check's name and status — no descriptions, data, version or timings. Exception messages never appear in a response, not even for dashboard users: a failed probe reports the exception type, and the full exception goes to the server log. `LocalOnly` treats a request without a remote address (for example over a Unix socket behind a reverse proxy) as remote. `RequireDashboardAuth` protects nothing if the dashboard itself is open (see [Authorization](#authorization)).

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

### Plug into ASP.NET Core HealthChecks

If your host already exposes a unified `/health` endpoint aggregating multiple dependencies (database, message broker, Hangfire, ...), register the dashboard as an `IHealthCheck`:

```csharp
builder.Services.AddHealthChecks()
    .AddHangfireDashboard(tags: new[] { "ready" });

app.MapHealthChecks("/health");                                   // all checks
app.MapHealthChecks("/health/ready",                               // readiness subset
    new() { Predicate = c => c.Tags.Contains("ready") });
```

The adapter reads the same cached full report as the dashboard's own endpoint and hero card, so the results match. It never puts an exception message in its description or data. It also leaves `HealthCheckResult.Exception` empty unless you set `DashboardUIOptions.IncludeExceptionInHealthCheckResult = true`; leave that off if your `/health` endpoint is reachable by anyone who shouldn't see connection details, since response writers such as HealthChecks.UI's `UIResponseWriter` serialize `Exception.Message`. For your own code, `HealthCheckService` offers `CheckLivenessAsync`, `CheckReadinessAsync` and `CheckFullAsync`; the synchronous `CheckLiveness()`, `CheckReadiness()` and `CheckFull()` are kept and block the calling thread until the report is ready.

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

With a group selected, **Requeue all** and **Delete all** act on every failed job with that exception, not only the ones on screen. When you click one, the page reads the ids of the group's jobs and asks for confirmation with their exact number. Both actions ask, because requeuing thousands of jobs has an impact too. The confirmation opens with focus on **Cancel**, and Escape cancels it. Only those jobs are changed, and each one only while it is still in the Failed state. A job that was retried, requeued or deleted in the meantime is skipped, and one that fails with the same exception after the click is left alone.

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
| v1.0–v2.0 | ✅ Done | Core pages, Console, Tags, Recurring Admin, global search and advanced filters, Razor Class Library packaging, SQL Server and PostgreSQL adapters, Analytics |
| v2.1–v2.2.x | ✅ Done | Search refactor, `JobDisplayName`, Fetched page, security and auth hardening (local-only default filter, `LoginPath`) |
| v2.3.x | ✅ Done | **Operational visibility & controls**: health checks and hero card, queue pause/resume, maintenance mode, audit log |
| v2.4.x | ✅ Done | **Job Builder**: typed arguments, method discovery, visual cron builder, one-off enqueue page |
| v2.5.x | ✅ Done | **Recurring Schedule Heatmap**, rollup metrics for non-SQL storages (Rollup, Redis), Throttling pages, job arguments and `args:` search, responsive layout, failure fingerprints |
| v2.6.0 | ✅ Done | **Per-user read-only, safer defaults, slow-storage resilience** — `IsReadOnlyFunc` honored per page load with a Read-only badge; health responses without exception messages and `/healthz/full` behind dashboard authorization; no filter means local only ([Upgrading](CHANGELOG.md#upgrading)); bounded storage calls and back-off for slow storage ([#64](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/64)); in-process realtime feed ([#61](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/61)); Failed page grouped by exception ([#53](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/53)); `IsReadOnly` enforced everywhere ([#73](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/73)) |
| v2.7.0 | Planned | **Integrations** — Prometheus `/metrics`, OpenTelemetry trace links, read-only REST API, CSV/JSON export |
| v2.8.0 | Planned | **Customization** — white-label theming, show/hide built-in pages, saved views |
| v3.0 | Planned | Stretch goals & long-term backlog (notifications & alert rules, Gantt timeline, multi-instance federation, replay, etc.) |

See the full [roadmap](docs/ROADMAP.md) for details, and the [changelog](CHANGELOG.md) for every release.

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
- [Hangfire.Community.Dashboard.Heatmap](https://github.com/brodrigz/Hangfire.Community.Dashboard.Heatmap) by Bruno Rodrigues (brodrigz) — design inspiration for the recurring schedule heatmap (independent Blazor implementation)

Community extensions are listed on the [Hangfire Extensions](https://www.hangfire.io/extensions.html) page. This project is community-maintained and is not officially supported by Hangfire OÜ.

---

<p align="center">
  <sub>Built with ☕ by <a href="https://github.com/anwarminarso">Anwar Minarso</a></sub>
</p>
