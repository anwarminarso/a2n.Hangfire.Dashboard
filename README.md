# a2n.Hangfire.Dashboard

**The Hangfire dashboard you actually want to use.**

A modern, open-source alternative to the built-in Hangfire Dashboard — with realtime updates, console logs, job tagging, and recurring job management baked in. No extra plugins. No Pro license. Just drop it in.

[![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Hangfire](https://img.shields.io/badge/Hangfire-1.8+-blue.svg)](https://www.hangfire.io/)

---

<!-- TODO: Replace with actual screenshot -->
![Dashboard Overview](docs/screenshots/home.png)

## Why This Exists

The built-in Hangfire dashboard works. But it's jQuery-based, requires separate NuGet packages for console logs and tags, and the Pro dashboard costs money for features that should be standard.

This project replaces all of that with a single package:

- **Blazor Server** — no page reloads, instant interactions
- **Bootstrap 5** — clean, responsive, dark mode out of the box
- **Chart.js** — realtime throughput charts that actually look good
- **SignalR** — live updates without polling hacks
- **Console + Tags + Recurring Admin** — built in, zero extra packages

## Features

### Everything the original has, plus more

| Feature | Built-in Dashboard | This Dashboard |
|---------|:-----------------:|:--------------:|
| Job state pages (enqueued, processing, failed, etc.) | ✅ | ✅ |
| Batch operations (requeue, delete) | ✅ | ✅ |
| Recurring job management | View only | **Full CRUD + Start/Stop** |
| Console output (logs, progress bars) | ❌ (plugin) | ✅ Built-in |
| Job tagging & search | ❌ (plugin) | ✅ Built-in |
| Global search & advanced filters | ❌ | ✅ **Search by ID, name, queue, tag, exception** |
| Filter presets (save & reuse) | ❌ | ✅ Built-in |
| Analytics dashboard | ❌ (Pro only) | ✅ **5 pages: Overview, Performance, Failures, Queues, Recurring** |
| Storage-optimized queries | ❌ | ✅ **SQL Server & PostgreSQL adapters** |
| Dark mode | Auto only (system theme) | ✅ Auto / Light / Dark toggle |
| Mobile responsive | Partial | ✅ Full responsive |
| Modern UI framework | jQuery + Bootstrap 3 | Blazor + Bootstrap 5 |

### Screenshots

<!-- TODO: Add actual screenshots -->

<details>
<summary>📊 Home — Realtime Stats & Charts</summary>

![Home Page](docs/screenshots/home.png)

</details>

<details>
<summary>🖥️ Console Viewer — Live Job Output</summary>

![Console Viewer](docs/screenshots/console-viewer.png)

</details>

<details>
<summary>🏷️ Tags — Search & Filter by Tag</summary>

![Tags Page](docs/screenshots/tags.png)

</details>

<details>
<summary>🔄 Recurring Jobs — Full CRUD</summary>

![Recurring Jobs](docs/screenshots/recurring.png)

</details>

<details>
<summary> 🖥️Auto — ☀️Light — 🌙Dark Mode</summary>

![Dark Mode](docs/screenshots/auto-light-dark-mode.png)

</details>
<details>
<summary>🔍 Advanced Search</summary>

![Recurring Jobs](docs/screenshots/search.png)

</details>

## Quick Start

### 1. Install the package

```bash
# Coming soon to NuGet
dotnet add package a2n.Hangfire.Dashboard
```

### 2. Replace your dashboard setup

```csharp
using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;  // Built-in — same namespace, same API
using Hangfire.Tags;     // Built-in — same namespace, same API

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHangfire(config => config
    .UseYourStorage()    // SQL Server, PostgreSQL, etc.
    .UseConsole()        // Enable console output
    .UseTags());         // Enable job tagging

builder.Services.AddHangfireServer();

// Add the dashboard UI services (Blazor, SignalR, internal services)
// Without storage adapter: basic search works, analytics hidden
builder.Services.AddHangfireDashboardUI();

// OR: With storage adapter for optimized queries + full analytics
// builder.Services.AddHangfireDashboardUI(options =>
// {
//     options.UseSqlServerStorage(connectionString);       // SQL Server
//     // options.UsePostgreSqlStorage(connectionString);   // PostgreSQL
// });

var app = builder.Build();

// Use the dashboard UI at /hangfire (replaces app.UseHangfireDashboard())
app.UseHangfireDashboardUI("/hangfire");

app.Run();
```

### 3. That's it

Navigate to `/hangfire` and enjoy your new dashboard.

### Important: Project Reference (Development)

If you reference `a2n.Hangfire.Dashboard` via `<ProjectReference>` (not NuGet), add this to your host `.csproj`:

```xml
<PropertyGroup>
  <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
</PropertyGroup>
```

This tells MSBuild to include Blazor framework assets (`blazor.web.js`). When consuming via NuGet package, this is handled automatically — no extra configuration needed.

### Configuration Options

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "My Jobs",
    DefaultRecordsPerPage = 20,
    DefaultTheme = "auto",  // "auto", "light", or "dark"
    IsReadOnly = false,
    Authorization = new[] { new MyAuthFilter() },
});
```

## Backward Compatibility

### Drop-in replacement

Already using `Hangfire.Console` or `Hangfire.Tags`? Just swap the NuGet packages. The namespaces are identical — your existing code compiles without changes:

```csharp
// These work exactly the same as the original packages
context.WriteLine("Hello from console!");
context.WriteProgressBar();

[Tag("orders")]
public void ProcessOrder() { }
```

### Existing DashboardOptions

If you have an existing `DashboardOptions` configuration, it still works:

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardOptions
{
    DashboardTitle = "My Jobs",
    Authorization = new[] { new MyAuthFilter() },
});
```

### Data compatibility

This dashboard reads the same storage format as the original plugins. Your historical console logs and tags are visible immediately — no data migration needed.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| UI | Blazor Server (Interactive SSR) |
| Styling | Bootstrap 5.3 + Bootstrap Icons |
| Charts | Chart.js + chartjs-plugin-streaming |
| Realtime | ASP.NET Core SignalR |
| Theme | `data-bs-theme` with localStorage persistence |
| Targets | .NET 8, .NET 9, .NET 10 |

## Project Structure

```
src/
├── a2n.Hangfire.Dashboard/           # Main dashboard (Blazor Server + SignalR + Analytics)
├── a2n.Hangfire.Dashboard.SqlServer/  # SQL Server storage adapter (Dapper + T-SQL)
├── a2n.Hangfire.Dashboard.PostgreSql/ # PostgreSQL storage adapter (Dapper + Npgsql)
├── a2n.Hangfire.Console/             # Console integration (drop-in replacement)
└── a2n.Hangfire.Tags/                # Tags integration (drop-in replacement)

tests/
├── a2n.Hangfire.Dashboard.Tests/
├── a2n.Hangfire.Console.Tests/
└── a2n.Hangfire.Tags.Tests/

samples/
├── SampleApp/                        # Demo with all features (InMemory/SqlServer/PostgreSql)
└── SampleAppOrig/                    # Original Hangfire dashboard for comparison
```

## Running the Sample

```bash
git clone https://github.com/anwarminarso/a2n.Hangfire.Dashboard.git
cd a2n.Hangfire.Dashboard/samples/SampleApp
dotnet run
```

Open `https://localhost:7100/hangfire` to see the dashboard in action with sample jobs running.

## Roadmap

### ✅ v1.0 — Feature Parity
Full parity with the built-in dashboard plus Console, Tags, and Recurring Job Admin integrated.

### ✅ v1.1 — Search & Filter
Global search across all job states, advanced filters (date range, state, server, duration, tags, queue, recurring job), saved filter presets.

### ✅ v1.2 — Razor Class Library
Converted to NuGet-distributable Razor Class Library with custom middleware pipeline. True drop-in replacement via package reference.

### ✅ v1.3–v1.6 — Storage Adapters & Analytics
- Storage abstraction layer (`IStorageQueryProvider`, `IStorageMetricsProvider`)
- SQL Server adapter with optimized T-SQL queries (Dapper)
- PostgreSQL adapter with optimized queries (Dapper + Npgsql)
- GenericQueryProvider fallback for InMemory/Redis/other storages
- Full analytics dashboard: Overview, Performance, Failures, Queues, Recurring Health
- Realtime analytics via SignalR (auto-update when viewing last 1h)
- Graceful degradation (analytics hidden without storage adapter)

### ✅ v2.0 — Phase 2 Complete
All differentiation features implemented. Dashboard now offers capabilities beyond the built-in + Pro dashboard.

### 🏢 v3.0 — Extensibility & Integration
Notifications & alerts, REST API, Prometheus metrics, theming & customization.

See the full [roadmap](docs/ROADMAP.md) for details.

## Contributing

Contributions are welcome! Whether it's bug reports, feature requests, or pull requests — all help is appreciated.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'feat: add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Setup

```bash
git clone https://github.com/anwarminarso/a2n.Hangfire.Dashboard.git
cd a2n.Hangfire.Dashboard/samples/SampleApp
dotnet run
```

> **Note:** The SampleApp uses `<ProjectReference>` to the dashboard library. The `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` property in its `.csproj` is required for MSBuild to include `blazor.web.js`. This is only needed for project reference — NuGet consumers get this automatically via the package's `buildTransitive` props.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Hangfire](https://www.hangfire.io/) — the excellent background job framework this dashboard is built for
- [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console) — inspiration for the console integration
- [Hangfire.Tags](https://github.com/face-it/Hangfire.Tags) — inspiration for the tags integration
- [Hangfire.RecurringJobAdmin](https://github.com/nickvdyck/Hangfire.RecurringJobAdmin) — inspiration for recurring job CRUD

---

<p align="center">
  <sub>Built with ☕ by <a href="https://github.com/anwarminarso">Anwar Minarso</a></sub>
</p>
