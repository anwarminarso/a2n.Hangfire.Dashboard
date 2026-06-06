# a2n.Hangfire.Dashboard

**A modern, open-source dashboard extension for [Hangfire](https://www.hangfire.io/)** — analytics, console logs, tags, and recurring job management built in.

Open source (LGPL-3.0-or-later). Requires Hangfire 1.8+ and ASP.NET Core (.NET 8, 9, or 10). Not officially supported by Hangfire OÜ.

[![NuGet](https://img.shields.io/nuget/v/a2n.Hangfire.Dashboard)](https://www.nuget.org/packages/a2n.Hangfire.Dashboard)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-LGPL--3.0--or--later-blue.svg)](LICENSE)
[![Hangfire](https://img.shields.io/badge/Hangfire-1.8+-blue.svg)](https://www.hangfire.io/)

---

![Dashboard Overview](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/overview.png)

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

## About

Hangfire ships a capable monitoring UI out of the box. Many teams extend it with community packages for additional dashboard features — for example [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console), [Hangfire.Tags](https://github.com/face-it/Hangfire.Tags), and [Hangfire.RecurringJobAdmin](https://github.com/bamotav/Hangfire.RecurringJobAdmin). Dashboard analytics are also available in [Hangfire Pro](https://www.hangfire.io/pricing/).

**a2n.Hangfire.Dashboard** combines several of these capabilities in one extension: search, filters, analytics (with optional storage adapters), console viewer, tags, recurring job CRUD, SignalR realtime updates, and theme options — while using the same Hangfire job storage and APIs.

---

## Features

| Feature | Description |
|---------|-------------|
| Job monitoring | Job state pages, batch operations, servers, retries |
| Recurring jobs | Create, edit, start, and stop recurring jobs from the UI |
| Console output | Logs, progress bars, and colors (Hangfire.Console-compatible API) |
| Job tags | Tagging and tag cloud (Hangfire.Tags-compatible storage) |
| Job dependency graph | Continuation pipeline visualization on the Job Details page (with "Load more" expansion) |
| Global search | Search by job ID, name, queue, tag, or exception text |
| Advanced filters | Filter by date, duration, state, server, and more |
| Analytics | Throughput, latency, failures, queue health (requires storage adapter — see [Packages](#packages)) |
| Realtime updates | Live metrics via SignalR |
| Authorization | Local-only default (same as Hangfire); optional async filters and `LoginPath` redirect |
| Theming | Dark, light, or auto; responsive layout |
| Tech | Blazor Server, Bootstrap 5, Chart.js |

---

## Screenshots

| Home & Realtime Charts | Console Viewer |
|:---:|:---:|
| ![Home](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/home.png) | ![Console](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/console-viewer.png) |

| Tags & Search | Recurring Jobs |
|:---:|:---:|
| ![Tags](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/tags.png) | ![Recurring](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/recurring.png) |

| Advanced Search | Analytics |
|:---:|:---:|
| ![Advanced Search](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/search.png) | ![Analytics](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/performance.png) |

| Light / Dark / Auto |
|:---:|
| ![Light / Dark / Auto theme](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/blob/main/docs/screenshots/light-dark.png) |

---

## Packages

| NuGet package | Purpose |
|---------------|---------|
| [`a2n.Hangfire.Dashboard`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard) | Main dashboard UI (search, console, tags, recurring admin) |
| [`a2n.Hangfire.Dashboard.SqlServer`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard.SqlServer) | Storage-specific queries + full analytics for SQL Server |
| [`a2n.Hangfire.Dashboard.PostgreSql`](https://www.nuget.org/packages/a2n.Hangfire.Dashboard.PostgreSql) | Storage-specific queries + full analytics for PostgreSQL |
| [`a2n.Hangfire.Console`](https://www.nuget.org/packages/a2n.Hangfire.Console) | Console integration (Hangfire.Console-compatible API) |
| [`a2n.Hangfire.Tags`](https://www.nuget.org/packages/a2n.Hangfire.Tags) | Tags integration (Hangfire.Tags-compatible storage) |

Without a storage adapter package, search and core dashboard features work; the **Analytics** pages require `a2n.Hangfire.Dashboard.SqlServer` or `a2n.Hangfire.Dashboard.PostgreSql`.

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
    EnableRecurringJobAdmin = true,  // set false to hide Create/Edit/Stop
    JobGraphMaxDepth = 5,   // continuation graph traversal depth (default 5)
    JobGraphMaxNodes = 30,  // continuation graph node budget (default 30)
});

app.Run();
```

### Project Reference (Development Only)

If referencing via `<ProjectReference>` instead of NuGet, add this to your host `.csproj`:

```xml
<PropertyGroup>
  <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
</PropertyGroup>
```

NuGet consumers don't need this — it's handled automatically.

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
| v2.3 | In progress | Enhanced Job Details: continuation dependency graph (with Load more), retry diff, historical duration |
| v3.0 | Planned | Notifications, REST API, Prometheus metrics, theming |

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

Community extensions are listed on the [Hangfire Extensions](https://www.hangfire.io/extensions.html) page. This project is community-maintained and is not officially supported by Hangfire OÜ.

---

<p align="center">
  <sub>Built with ☕ by <a href="https://github.com/anwarminarso">Anwar Minarso</a></sub>
</p>
