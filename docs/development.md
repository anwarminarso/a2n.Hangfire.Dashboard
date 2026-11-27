# Development

Back to the [README](../README.md). Contribution workflow and code style: [CONTRIBUTING.md](../CONTRIBUTING.md). Architecture notes for AI coding agents: [AGENTS.md](../AGENTS.md).

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
├── a2n.Hangfire.Dashboard.SqlServer.Tests/
├── a2n.Hangfire.Dashboard.PostgreSql.Tests/
├── a2n.Hangfire.Dashboard.Rollup.Tests/
├── a2n.Hangfire.Console.Tests/
├── a2n.Hangfire.Tags.Tests/
├── load/               # Python harness: e2e, load/stress, seeding, release-check
└── reverse-proxy/      # Docker Compose stack: nginx, TLS, path prefix, cookie auth

samples/
├── SampleApp/          # Full demo (all features)
├── SampleAppAuth/      # Cookie authentication example
├── SampleAppMvc/       # ASP.NET Core MVC host
├── SampleAppRazor/     # Razor Pages host
├── SampleAppBlazor/    # Blazor host
├── SampleAppSpa/       # SPA host (dashboard at /hangfire)
├── SampleAppOrig/      # Startup-class host pattern
└── SampleApp.SharedJobs/  # Job classes shared by the samples
```

## Running the Sample

```bash
git clone https://github.com/anwarminarso/a2n.Hangfire.Dashboard.git
cd a2n.Hangfire.Dashboard/samples/SampleApp
dotnet run
```

Open `https://localhost:7100/hangfire` to see it in action.

For authentication with a login page, run [`samples/SampleAppAuth`](../samples/SampleAppAuth) instead.
