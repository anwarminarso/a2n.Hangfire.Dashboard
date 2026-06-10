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

## Quick start

```bash
python cli.py check-all              # HTTP + DB sanity against a running SampleApp
python cli.py info                   # show resolved configuration
python cli.py db stats               # job counts, queues, servers
python cli.py jobs seed -n 20        # seed mixed test jobs
python cli.py signalr connect        # watch live SignalR pushes
```

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
- `config.toml`, `screenshots/`, and `__pycache__/` are git-ignored — only the
  scripts, `config.example.toml`, and this README are tracked.
