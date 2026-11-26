# AGENTS.md — a2n.Hangfire.Dashboard

Guidance for AI coding agents (Claude Code, Codex, Cursor, Kiro, Copilot, …) working in this repository.
Human contributors: see `CONTRIBUTING.md`. Release history: `CHANGELOG.md`. Roadmap: `docs/ROADMAP.md`.

## What this project is

A modern, open-source alternative to the built-in Hangfire dashboard that also replaces the popular
plugins **Hangfire.Console**, **Hangfire.Tags** and **Hangfire.RecurringJobAdmin** in one package.

Core principles (from `docs/ROADMAP.md`):
1. **Drop-in replacement** — swap the NuGet package, zero code changes for the host app.
2. **Data backward compatible** — reads historical data written by the original plugins, no migration.
3. **No assembly conflict** — the Console/Tags replacements keep the original namespaces
   (`Hangfire.Console`, `Hangfire.Tags`) and replace, not coexist with, the original packages.
4. **Modern stack** — Blazor Server, Bootstrap 5, Chart.js, SignalR, .NET 8/9/10.

- **Version:** single source of truth is `<Version>` in `Directory.Build.props` (currently 2.6.1).
- **License:** LGPL-3.0-or-later. Third-party notices in `NOTICES`.
- **Targets:** `net8.0;net9.0;net10.0` for every library and test project. Code must compile on all three
  (watch for per-TFM `PackageReference` conditions and APIs that only exist on newer frameworks).

## Tech stack

| Layer | Technology |
|---|---|
| UI | Blazor Server (Interactive), packaged as a Razor Class Library (NuGet drop-in) |
| CSS | Bootstrap 5.3 + Bootstrap Icons (vendored in `Content/lib/`) |
| Charts | Chart.js + chartjs-plugin-streaming + moment.js |
| Realtime | SignalR (`DashboardHub`, `MetricsBroadcastService`, `AnalyticsBroadcastService`) |
| Theme | Bootstrap `data-bs-theme` dark/light/auto + localStorage |
| Storage adapters | Dapper (SQL Server T-SQL, PostgreSQL/Npgsql); rollup-based metrics for non-SQL storages |
| Tests | xUnit 2, bUnit, Moq, FsCheck, AngleSharp, Hangfire.InMemory, ASP.NET Core TestHost |

## Repository layout

Solution file: `src/Hangfire Dashboard.slnx` (note the space — quote the path).

| Path | Purpose |
|---|---|
| `src/a2n.Hangfire.Dashboard/` | Main dashboard (Blazor RCL): pages, SignalR hub, middleware, services, interfaces |
| `src/a2n.Hangfire.Dashboard.SqlServer/` | SQL Server adapter (Dapper + T-SQL): search, paging, analytics |
| `src/a2n.Hangfire.Dashboard.PostgreSql/` | PostgreSQL adapter (Dapper + Npgsql) |
| `src/a2n.Hangfire.Dashboard.Rollup/` | Rollup metrics so analytics/heatmap history work on non-SQL storages |
| `src/a2n.Hangfire.Dashboard.Redis/` | Redis / non-SQL entry point that registers the rollup metrics |
| `src/a2n.Hangfire.Console/` | Drop-in replacement for Hangfire.Console (namespace `Hangfire.Console`) |
| `src/a2n.Hangfire.Tags/` | Drop-in replacement for Hangfire.Tags (namespace `Hangfire.Tags`) |
| `tests/*.Tests/` | Unit + integration tests, one project per library |
| `tests/load/` | Python harness: e2e (Playwright), load/stress, DB seeding, `release-check` |
| `tests/reverse-proxy/` | Docker compose stack (nginx, TLS, path prefix, cookie auth) for hosting tests |
| `samples/` | Host apps: SampleApp, SampleAppAuth, SampleAppBlazor, SampleAppMvc, SampleAppRazor, SampleAppSpa, SampleAppOrig; shared jobs in `SampleApp.SharedJobs` |
| `docs/` | ROADMAP, proposals, load-test reports, mockups, screenshots |

`src/a2n.Hangfire.Dashboard.OpenTelemetry/`, `src/a2n.Hangfire.Dashboard.RestApi/` and their test folders are
untracked local leftovers, not part of the solution — don't add them to the solution or commit them unless asked.

### Inside `src/a2n.Hangfire.Dashboard/`

- `Components/Pages/` — routable pages (Home, Jobs/*, Recurring, RecurringEditor, Queues, Servers, Retries,
  Tags, Throttling, Analytics/*, Heatmap/*, Audit, EnqueueJob, SearchResults).
- `Components/Shared/` — reusable components (Pager, RelativeTime, ServerLabel, ConsoleViewer, TagsViewer,
  ConfirmModal, …). Reuse these before writing new ones.
- `Components/Layout/` — layout and navigation.
- `Services/` — HangfireMonitorService, SearchService, AnalyticsService, HeatmapService, Console/Tags data
  readers, `DashboardPolling` / `DashboardStorageGate` (bounded storage concurrency), caches, audit log, health.
- `Interfaces/` — `IStorageQueryProvider`, `IStorageMetricsProvider`, `IFailedJobPageSource` (implemented by adapters).
- `Middleware/`, `Security/`, `Hubs/`, `Extensions/` — pipeline, authorization, SignalR, DI/registration
  (`AddHangfireDashboardUI` / `UseHangfireDashboardUI`).
- `DashboardUIOptions.cs` — all public options (AppPath, LoginPath, DashboardTitle, IsReadOnly,
  EnableJobManagement, Authorization / AsyncAuthorization, StatsPollingInterval, DefaultRecordsPerPage,
  DefaultTheme, Heatmap, Polling, QueueOperations, AuditLog, health-check settings, …).
- `Content/css/app.css` — the **only** custom stylesheet; keep it to minimal overrides.
- `Content/js/` — theme.js, charts.js, analyticsCharts.js, heatmap.js, nav.js, prefs.js, search-presets.js.
- `Content/lib/` — vendored static libraries (bootstrap, bootstrap-icons). Don't edit vendored files.
- `build/` — NuGet `.props/.targets` shipped to consumers (explicitly un-ignored in `.gitignore`).

## Build, test, run

```bash
dotnet build "src/Hangfire Dashboard.slnx"
dotnet test  "src/Hangfire Dashboard.slnx"

# single project / single test
dotnet test tests/a2n.Hangfire.Dashboard.Tests
dotnet test tests/a2n.Hangfire.Dashboard.Tests --filter "FullyQualifiedName~HeatmapService"
```

- No database is needed for a green run: the SQL Server and PostgreSQL suites use `SkippableFact` and
  **skip** when the server is unreachable. Once connected, a schema-setup failure fails the tests.
- Each adapter test run creates a unique schema (`test_<guid>`) and drops it afterwards.
- Point adapter suites at a DB via env vars (preferred over editing the committed `appsettings.json`):
  `ConnectionStrings__SqlServer=...`, `ConnectionStrings__PostgreSql=...`.
- CI (`.github/workflows/ci.yml`) builds Release and runs all tests on net8/9/10 with PostgreSQL 16 and
  SQL Server 2022 service containers. Keep the build at **0 warnings / 0 errors**.

### Sample apps

```bash
dotnet run --project samples/SampleApp/SampleApp.csproj
```

| App | HTTP | HTTPS | Landing |
|---|---|---|---|
| SampleApp | http://localhost:5100 | https://localhost:7100 | `/hangfire` |
| SampleAppAuth | http://localhost:5101 | https://localhost:7101 | `/login` (cookie auth first) |
| SampleAppBlazor | http://localhost:5103 | — | `/` |
| SampleAppSpa | http://localhost:5104 | — | `/` (SPA), dashboard at `/hangfire` |

- SampleApp storage defaults to **PostgreSQL** (`hangfire_test` on localhost:5432). Switch with
  `"StorageProvider": "InMemory" | "SqlServer" | "PostgreSql"` in `samples/SampleApp/appsettings.json`
  or `--StorageProvider=InMemory` on the command line. Use InMemory when no DB is available.
- SPA hosting: `app.UseHangfireDashboardUI("/hangfire")` must be registered **before**
  `MapFallbackToFile("index.html")`, otherwise the SPA catch-all swallows `/hangfire`.
- The dashboard's own SignalR endpoints live under the dashboard path (`/hangfire/_blazor`,
  `/hangfire/hubs/dashboard`) and must not collide with host hubs.

### Python tooling (`tests/load/`)

```bash
cd tests/load
python setup.py                    # once: deps + Playwright
python cli.py check-all            # quick endpoint + DB check
python cli.py e2e smoke            # Playwright smoke test
python cli.py jobs seed --count 50 # seed test jobs
python cli.py release-check --profile quick --provider pg|sqlserver|inmemory
```

Config: copy `config.example.toml` → `config.toml` (git-ignored, may hold secrets). Default base_url
`http://localhost:5100`. Stress/release checks are **manual only** — never add them to CI or the solution.

## Conventions

- C#: PascalCase public members, `_camelCase` private fields; follow surrounding code.
- Blazor: `.razor` files with `@code` blocks; prefer existing shared components.
- UI: Bootstrap utility classes first; minimal custom CSS in `Content/css/app.css`; icons are Bootstrap
  Icons (`bi bi-*`) only. Support both light and dark theme.
- Static assets use cache busting (`?v={version}`); keep it when adding assets.
- Storage access from pages/services goes through the existing polling/gate services (`DashboardPolling`,
  `DashboardStorageGate`) — don't add unbounded synchronous storage calls on circuit threads (see issue #64).
- Security defaults are fail-closed: with no authorization filter configured, only local requests are
  allowed; read-only (`IsReadOnly` / `IsReadOnlyFunc`) is evaluated per page load and enforced in services too.
  SQL identifiers (schema/table names) are validated — never interpolate unvalidated names into SQL.
- Parity with the original Hangfire dashboard matters: list pages handle expired jobs, "state changed"
  rows, conditional checkboxes and the aborted-server warning the same way the original does.
- Public API changes, breaking changes and new options go into `CHANGELOG.md` (and README when user-facing).

## Git workflow

- Conventional Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `ci:`, `perf:`; scopes are welcome
  (`fix(recurring): …`); mark breaking changes with `!`.
- Commits are **GPG-signed** (`git commit --gpg-sign`).
- **Never commit or push without the user's confirmation.** Show the commit title + description first.
- `main` is a **protected branch**: never push to it directly. All changes land through a pull request from a
  feature branch (e.g. `feature/…`, `fix/…`, `docs/…`).
- Work on the current branch. If the current branch is `main`, ask the user before creating a new branch.
- Keep PRs focused (one feature/fix), reference issues (`Closes #42`).

## Workspace scope (multi-root)

Only this repository (`D:\GitHub\HangfireDashboard`) is writable. Sibling folders are **read-only references**
— read and search them to understand APIs, but never create, edit or delete files there unless the user
explicitly asks:

| Folder | Use |
|---|---|
| `../Hangfire` | Hangfire core: storage, `IMonitoringApi`, original dashboard (`src/Hangfire.Core/Dashboard/`) |
| `../Hangfire.PostgreSql` | PostgreSQL storage |
| `../Hangfire.Console` | Original Console plugin |
| `../Hangfire.Tags` | Original Tags plugin |
| `../Hangfire.RecurringJobAdmin` | Original RecurringJobAdmin plugin |
| `../Hangfire.io` | Hangfire website/docs |
| `Hangfire.Community.Dashboard.Heatmap` | Community heatmap reference |

If a change is needed in one of them, explain what should change and ask first.

## Further reference (local only)

Detailed architecture notes for the original dashboard and plugins, plus feature specs, live in `.kiro/`
(git-ignored, so only on the maintainer's machine). Read them on demand when the task touches that area:

- `.kiro/steering/hangfire-original-reference.md` — original dashboard pipeline, pages/columns, edge cases, extension points.
- `.kiro/steering/hangfire-console-plugin.md`, `hangfire-tags-plugin.md`, `hangfire-recurringjobadmin-plugin.md` — plugin internals.
- `.kiro/steering/heatmap-backlog.md` — recurring schedule heatmap: closed items and deliberately deferred ones.
- `.kiro/steering/release-stress-test-plan.md` — stress-test tiers, thresholds and decisions (used for the 2.6.0 release).
- `.kiro/specs/<feature>/` — requirements / design / tasks for past features (job-builder, global-search, heatmap, storage-analytics, …).
