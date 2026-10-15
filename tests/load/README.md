# HangfireDashboard — Load & Scenario Testing

A Python toolkit for **manual, developer-run** load and behaviour testing of the
HangfireDashboard against a live SampleApp. It drives the dashboard over its real
public surfaces (HTTP, SignalR/WebSocket, a real browser) and samples **server-side**
metrics (CPU, working set, GC heap, connections) so you can see what the load does
to the host, not just the client.

> **This is not part of the build or CI.** It is an optional, on-demand harness for
> developers validating storage adapters, SignalR realtime behaviour, and Blazor
> circuit health. It requires a running SampleApp and external tooling (Python,
> Playwright, dotnet-counters). Nothing here runs during `dotnet build`/`dotnet test`.

## Why it exists

This harness found a real, user-facing regression: on SQL Server the analytics
SignalR channel silently never broadcast (a `GROUP BY` subquery rejected by SQL
Server threw on every tick). A client-only check wouldn't have caught it — you need
to measure the **cadence** of server pushes and watch the server process. That's the
class of problem this toolkit exists to surface.

## Prerequisites

- **Python 3.11+** (uses the stdlib `tomllib`).
- **A running SampleApp** (`samples/SampleApp`) on the URL in your config.
- **Playwright + Chromium** — only for the browser tiers (`e2e`, `stress circuits`,
  `stress scenario`):
  ```bash
  pip install -r requirements.txt
  playwright install chromium
  ```
- **dotnet-counters** (optional but recommended) for the server-side metrics panel:
  ```bash
  dotnet tool install --global dotnet-counters
  ```
  If it isn't found, tests still run — they just report client-side numbers only.
- **dotnet-gcdump** for `release-check` tier R2, which counts live circuits and dashboard pages
  after a full GC (dumps saved to `reports/heap-*.gcdump`):
  ```bash
  dotnet tool install --global dotnet-gcdump
  ```
- **dotnet-stack** for `release-check` tier S1, which counts the threads blocked in storage from
  the app's managed stacks (saved to `reports/stacks-*.txt`):
  ```bash
  dotnet tool install --global dotnet-stack
  ```
  Without it S1 falls back to the thread-pool thread count, which overstates the blocking: the
  pool injects threads while workers are blocked and they sit idle until they retire.

## Configuration

Two ways, in priority order:

1. **Environment variables** (`HFLOAD_*`) — best for CI/shared machines, no file edits.
2. **`config.toml`** — your local settings (git-ignored, never committed).
3. Falls back to **`config.example.toml`** if neither is present.

```bash
copy config.example.toml config.toml     # then edit it
```

The provider in your config **must match** the SampleApp's `StorageProvider` so the
tools read the same store. Key `HFLOAD_*` overrides:

| Variable | Purpose |
|----------|---------|
| `HFLOAD_BASE_URL` | e.g. `http://localhost:5100` |
| `HFLOAD_DASHBOARD_PATH` | e.g. `/hangfire` |
| `HFLOAD_DB_PROVIDER` | `postgresql` \| `sqlserver` \| `inmemory` |
| `HFLOAD_PG_HOST/PORT/DATABASE/USERNAME/PASSWORD` | PostgreSQL connection |
| `HFLOAD_SQLSERVER_CONNECTION_STRING` | full ODBC string for pyodbc |

(See `config.py` for the complete list.)

## Tools

| Tool | Description |
|------|-------------|
| `cli.py` | Unified entry point for everything below |
| `db.py` | Direct database inspector (PostgreSQL & SQL Server) |
| `api.py` | HTTP API & health endpoint checks |
| `jobs.py` | Job seeder (writes Hangfire rows directly; multi-provider) |
| `signalr.py` | DashboardHub tester (metrics & analytics subscriptions) |
| `monitor.py` | Live terminal dashboard (DB polling + health) |
| `e2e.py` | Playwright E2E browser checks |
| `stress.py` | Hypothesis-driven stress tiers + server-side metrics |
| `scenario.py` | 5-persona concurrent browser scenario + SignalR cadence probe |
| `srvmetrics.py` | dotnet-counters wrapper (CPU / working set / GC heap / connections) |
| `bulkseed.py` | Set-based seeding of `StressJobs` rows for the volume and grouping tiers |
| `release.py` | `release-check`: the pre-release stress check, with a markdown report |
| `guard.py` | Refuses to write to a database that doesn't look like a local test database |

## Writing to storage: the guard

Every command that writes (`jobs seed/purge/trigger-recurring`, `db cleanup`, `bulk *`,
`stress pause/dbload/combined`, `release-check`) checks the target first and refuses
when the database name doesn't contain `test` or the host isn't local. Then it asks
before writing. `--yes` (or `HFLOAD_ASSUME_YES=1`) skips the question, not the checks.
To allow another database or a remote host on purpose:

```toml
[safety]
allow_databases = ["my_scratch_db"]
allow_remote = true
```

## Quick start

```bash
python cli.py check-all              # HTTP + DB sanity against a running SampleApp
python cli.py info                   # show resolved configuration
python cli.py db stats               # job counts, queues, servers
python cli.py jobs seed -n 20        # seed mixed test jobs
python cli.py signalr connect        # watch live SignalR pushes
```

## Release check

`release-check` is the stress test to run by hand before a release. It drives a running
SampleApp, prints a verdict per tier and writes a markdown report to `reports/`
(git-ignored; copy the ones worth keeping to `docs/load-test-*.md`). It isn't part of CI.

```bash
python cli.py release-check --provider pg --profile quick --yes
python cli.py release-check --provider pg --profile full --yes          # 1 h soak included
python cli.py release-check --provider pg --only V1,V2 --reuse-data --yes
python cli.py release-check --provider pg --only R1,R2 --scale 0.05 --yes   # try the harness out
```

| Tier | Checks | Pass when |
|------|--------|-----------|
| P0 preflight | dashboard, `/healthz`, a server heartbeating in the DB, dotnet-counters attached | all answer |
| R1 realtime | Home's `data-snapshot-at` in several tabs and Analytics ▸ Last 1h's `data-received-at`, with 50 extra hub clients | Home gap p95 ≤ 1.25 × `StatsPollingInterval`, Analytics ≤ 6.5 s, all clients connected |
| R2 circuits | two rounds of N circuits opened, held, closed | after a full GC (`dotnet-gcdump`) no `CircuitHost` and no dashboard page component is alive, connections back to baseline; working-set growth is reported (and judged, ≤ +10 % round 1 → round 2, only without `dotnet-gcdump`) |
| M2 pause | concurrent pause/resume on `queue:paused` (SQL only) | no errors, no duplicate rows |
| V1 volume | list pages and Search over a seeded job table | list p95 ≤ `list_p95_ms` (2 s), Search inside its timeout |
| V2 grouping | Failed page groups with all, half and no stored fingerprints; Search `?exception=` | groups load inside the search timeout; the fingerprint search finds what the DB holds |
| M1 group actions | on the seeded `StressJobs.Fail(0)` group (picked by its message): Requeue all (cancelled after 2 s), then Delete all, with workers running | cancelled cleanly (on the generic provider the snapshot is the 1,000 newest failures, so Requeue all may finish first); deleted + skipped = snapshot |
| S1 slow storage | the job table locked 10 s / free 5 s (SQL Server: a schema lock, since its reads use NOLOCK), 5 then 20 circuits, `/healthz*` polled (SQL only) | pool workers blocked in the storage driver ≤ `MaxConcurrentStorageCalls` + 3 at 5 and at 20 circuits (from `dotnet-stack`, 6 s into a lock), thread-pool threads under a ceiling, probes inside their timeout, Home refreshes after |
| S2 storage down | the test database refuses connections for a minute (SQL only) | no Blazor error UI, `/healthz` answers, everything recovers |
| E1 soak | stress tier E for an hour (full profile, SQL only) | working set plateaus |
| P1 package | `dotnet pack`, then a fresh app per target framework installs it | the page and static assets serve on net8, net9 and net10 |

Thresholds that depend on dashboard options read them from an optional `[release]` section
(`stats_interval_ms`, `analytics_interval_ms`, `search_timeout_s`, `max_storage_calls`,
`max_backoff_s`, `storage_probe_timeout_ms`, `list_p95_ms`, `process_name`). The defaults are
the dashboard's own.

Seeded jobs all invoke `SampleApp.SharedJobs.StressJobs`, so `bulk purge` (or `--cleanup`)
removes exactly them. Failures are real: the harness enqueues `StressJobs.Fail(kind)` and the
SampleApp worker fails them, so the stored fingerprints come from the dashboard's own filter.

### Starting SampleApp per provider

Start it from the repo root, with its log in a file so `--app-log` can count back-off messages.
`--environment Development` matters: an unpublished app only serves `_framework/blazor.web.js`
in Development (P1 covers the published, Production case).

```powershell
# PostgreSQL (appsettings default), with a small pool so S1 can exhaust it
dotnet run --project samples\SampleApp --no-launch-profile -- --urls http://localhost:5100 --environment Development `
  "--ConnectionStrings:PostgreSql=Host=localhost;Port=5432;Database=hangfire_test;Username=postgres;Password=...;Maximum Pool Size=20" `
  *> sampleapp.log

# SQL Server (named instance)
dotnet run --project samples\SampleApp --no-launch-profile -- --urls http://localhost:5100 --environment Development `
  --StorageProvider=SqlServer `
  "--ConnectionStrings:SqlServer=Server=.\SQL2025;Database=HangfireTest;Integrated Security=true;TrustServerCertificate=true;Max Pool Size=20"

# InMemory: the harness can't write to it directly, so turn on the local-only seed endpoints
dotnet run --project samples\SampleApp --no-launch-profile -- --urls http://localhost:5100 --environment Development `
  --StorageProvider=InMemory --HfLoad:SeedEndpoints=true
```

Start the app before `release-check`: dotnet-counters attaches to the `SampleApp` process it
finds at startup, and P0 shows which PID.

`--provider` must match the app's storage, and `[database]` in `config.toml` must point at the
same database. P0 stops the run when no server heartbeats there.

## Stress tiers

Each tier isolates **one** pressure source so you can attribute any degradation,
then `combined` runs them together as a soak.

| Tier | Command | Hypothesis tested |
|------|---------|-------------------|
| A — circuits | `stress circuits -c 20 -d 60` | N Blazor circuits open, then server RAM recovers after close (no circuit leak) |
| B — fanout | `stress fanout -c 50 -d 60` | DashboardHub push cadence stays steady as subscriber count grows |
| C — pause | `stress pause -c 16 -d 30` | Concurrent pause/resume causes no errors or torn `queue:paused` state |
| D — dbload | `stress dbload -d 45 --seed-rate 100` | Monitor read p95 stays bounded while jobs are seeded hard |
| E — combined | `stress combined -d 300 --fanout 50 --circuits 10` | Working set is stable over a soak (leak detection) |

```bash
# Run component tiers individually first for clean attribution
python cli.py stress fanout   -c 50 -d 60
python cli.py stress pause    -c 16 -d 30
python cli.py stress dbload   -d 60 --seed-rate 100
python cli.py stress circuits -c 20 -d 60

# Then the combined soak (the leak question)
python cli.py stress combined -d 300 --fanout 50 --pause-workers 8 --circuits 10
```

## Persona scenario

Five concurrent "users", each a real browser context (= its own Blazor circuit),
plus a passive SignalR **cadence probe** that records the inter-arrival gap of each
push channel separately:

1. `watcher-home` — opens Home, stays, monitors its realtime channel
2. `watcher-analytics` — opens Analytics ▸ Performance, stays, monitors
3. `trigger-runner` — Recurring page: select-all + Trigger at random intervals
4. `queue-operator` — Queues page: pause/resume at random intervals
5. `wanderer` — navigates random pages, often interrupting the load mid-flight

```bash
python cli.py stress scenario -d 120
python cli.py stress scenario -d 300 --headed     # watch it happen
```

## Reading the results

Every tier prints a server-side metrics panel (first / mean / max / last / delta)
and a pass/fail verdict. How to read the signals:

- **SignalR cadence** — `MetricsUpdated` should arrive ~every 2s, `AnalyticsUpdate`
  ~every 5s. A p95 that drifts well past those intervals means the realtime feed is
  degrading (slow queries or fan-out saturation). **Zero `AnalyticsUpdate` pushes
  means the analytics broadcast is throwing server-side** — check the app log.
- **Working Set / GC Heap** — the leak signal. The combined soak judges growth from a
  **post-warm-up baseline (~20s in)**, because the first sample is taken before load
  ramps and would over-report on short runs. A leak keeps climbing after warm-up; a
  healthy service plateaus and GC heap rises *and falls*. Short runs report the raw
  delta as info only and won't fail on warm-up alone.
- **Connections** — proxies live SignalR connections; should rise under load and fall
  back near baseline after teardown. If it doesn't, circuits aren't being disposed.

## Notes & caveats

- Tiers **C (pause)** and **D (dbload)** and the job seeder write Hangfire rows
  **directly** to the store. They are multi-provider (PostgreSQL & SQL Server) but
  depend on Hangfire's internal schema — if Hangfire changes its schema, update the
  inserts in `jobs.py` / `stress.py`.
- `stress dbload` seeds `FailingJob`s on purpose, so the server's `Exceptions/s`
  counter will spike — that's the Hangfire worker failing those jobs by design.
- Tiers **A/B** and the **scenario** touch only public surfaces (HTTP/WebSocket/UI)
  and are the most robust to internal changes.
- `config.toml`, `reports/`, `screenshots/`, `.venv/` and `__pycache__/` are git-ignored — only the
  scripts, `config.example.toml`, and this README are tracked.
