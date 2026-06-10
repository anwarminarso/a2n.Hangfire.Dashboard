"""
stress.py — Hypothesis-driven stress tests for HangfireDashboard.

Each tier isolates ONE pressure source so you can attribute degradation, then
`combined` runs them together as a soak test. Server-side metrics (CPU, working
set, GC heap, connections) are sampled via dotnet-counters throughout, because a
client-only stress test is blind to the two things most likely to break a Blazor
Server dashboard: circuit memory growth and SignalR fan-out saturation.

Tiers:
    A  circuits   — N Blazor circuits (real browser contexts), held open.
                    Hypothesis: server RAM / circuit count scales and recovers.
    B  fanout     — N DashboardHub WebSocket subscribers.
                    Hypothesis: broadcast fan-out latency stays flat as N grows.
    C  pause      — concurrent pause/resume hammering on the queue:paused set.
                    Hypothesis: no torn state, no errors under write contention.
    D  dbload     — heavy job seeding while polling monitor stats.
                    Hypothesis: read latency (p95) stays bounded under write load.
    E  combined   — B + C + D + a few circuits, sustained.
                    Hypothesis: working set is stable (no leak) over the soak.

Usage:
    python stress.py fanout   -c 50 -d 60
    python stress.py pause    -c 16 -d 30
    python stress.py dbload   -d 45 --seed-rate 200
    python stress.py circuits -c 20 -d 60
    python stress.py combined -d 300 --circuits 10 --fanout 50
    python cli.py stress fanout -c 50 -d 60     # also wired into the unified CLI
"""
from __future__ import annotations

import asyncio
import statistics
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone

import click
from rich import box
from rich.console import Console
from rich.panel import Panel
from rich.table import Table

import config as cfg_mod
from srvmetrics import ServerMetricsSampler, CounterStats

console = Console()


# ─── shared helpers ───────────────────────────────────────────────────────────

def _hub_ws_url(cfg: dict) -> str:
    dashboard = cfg_mod.get_dashboard_url(cfg)
    ws = dashboard.replace("https://", "wss://").replace("http://", "ws://")
    return ws.rstrip("/") + "/hubs/dashboard"


def _pct(values: list[float], p: float) -> float | None:
    """Percentile via nearest-rank on a sorted copy."""
    if not values:
        return None
    s = sorted(values)
    if len(s) == 1:
        return s[0]
    k = max(0, min(len(s) - 1, int(round((p / 100.0) * (len(s) - 1)))))
    return s[k]


def render_server_metrics(stats: dict[str, CounterStats]) -> Panel | None:
    if not stats:
        return None
    t = Table(box=box.SIMPLE, show_header=True, expand=False)
    t.add_column("Counter", style="cyan")
    t.add_column("first", justify="right")
    t.add_column("mean", justify="right")
    t.add_column("max", justify="right")
    t.add_column("last", justify="right")
    t.add_column("delta", justify="right")

    order = [
        ("working_set_mb", "Working Set (MB)", "{:.1f}"),
        ("gc_heap_mb", "GC Heap (MB)", "{:.1f}"),
        ("cpu_pct", "CPU (%)", "{:.1f}"),
        ("connections", "Connections", "{:.0f}"),
        ("threadpool_queue", "TP Queue", "{:.0f}"),
        ("threadpool_threads", "TP Threads", "{:.0f}"),
        ("exceptions", "Exceptions/s", "{:.1f}"),
    ]
    for key, label, fmt in order:
        cs = stats.get(key)
        if cs is None or cs.count == 0:
            continue
        delta = cs.delta
        delta_str = (fmt.format(delta)) if delta is not None else "—"
        # Highlight a growing footprint in red — the leak signal.
        if key in ("working_set_mb", "gc_heap_mb") and delta is not None and delta > 0:
            delta_str = f"[red]+{delta_str.lstrip('+')}[/red]"
        elif delta is not None and key in ("working_set_mb", "gc_heap_mb"):
            delta_str = f"[green]{delta_str}[/green]"
        t.add_row(
            label,
            fmt.format(cs.first) if cs.first is not None else "—",
            fmt.format(cs.mean) if cs.mean is not None else "—",
            fmt.format(cs.max) if cs.max is not None else "—",
            fmt.format(cs.last) if cs.last is not None else "—",
            delta_str,
        )
    return Panel(t, title="[bold]Server-side metrics (dotnet-counters)[/bold]", border_style="magenta")


def _print_metrics_or_warn(session):
    if session is None:
        console.print(
            "[yellow]Server metrics unavailable[/yellow] — dotnet-counters not found "
            "or SampleApp process not detected. Client metrics only."
        )
        return
    stats = session.summarize()
    panel = render_server_metrics(stats)
    if panel:
        console.print(panel)
    session.cleanup()


@dataclass
class _LatencyStats:
    name: str
    samples: list[float] = field(default_factory=list)

    def add(self, v: float):
        self.samples.append(v)

    def render(self) -> str:
        if not self.samples:
            return f"{self.name}: [dim]no samples[/dim]"
        return (
            f"{self.name}: n={len(self.samples)} "
            f"min={min(self.samples):.0f} "
            f"mean={statistics.mean(self.samples):.0f} "
            f"p95={_pct(self.samples, 95):.0f} "
            f"max={max(self.samples):.0f} ms"
        )


def autodetect_sampler() -> ServerMetricsSampler:
    return ServerMetricsSampler.autodetect(process_name="SampleApp", refresh_interval=1)


# ══════════════════════════════════════════════════════════════════════════════
# Tier B — DashboardHub fan-out
# ══════════════════════════════════════════════════════════════════════════════

_REC_SEP = "\x1e"
_HANDSHAKE = '{"protocol":"json","version":1}' + _REC_SEP


@dataclass
class _ConnResult:
    conn_id: int
    connected: bool = False
    messages: int = 0
    error: str | None = None
    # inter-arrival gaps (ms) between server pushes — a proxy for fan-out health
    gaps_ms: list[float] = field(default_factory=list)


async def _fanout_conn(ws_url: str, groups: list[str], duration: float, res: _ConnResult, ready_evt: asyncio.Event):
    import websockets

    try:
        async with websockets.connect(
            ws_url,
            additional_headers={"User-Agent": "HF-Stress/1.0"},
            open_timeout=15,
            close_timeout=5,
            max_queue=64,
        ) as ws:
            await ws.send(_HANDSHAKE)
            raw = await asyncio.wait_for(ws.recv(), timeout=15)
            hs = raw.rstrip(_REC_SEP)
            if hs and hs != "{}":
                # {} is the success handshake; anything with "error" is a failure
                import json
                parsed = json.loads(hs)
                if "error" in parsed:
                    res.error = f"handshake: {parsed['error']}"
                    ready_evt.set()
                    return
            res.connected = True

            invoke_id = 1
            for g in groups:
                method = "SubscribeToMetrics" if g == "metrics" else "SubscribeToAnalytics"
                msg = (
                    '{"type":1,"invocationId":"%d","target":"%s","arguments":[]}' % (invoke_id, method)
                ) + _REC_SEP
                await ws.send(msg)
                invoke_id += 1

            ready_evt.set()

            start = time.monotonic()
            last_msg = start
            while (time.monotonic() - start) < duration:
                remaining = duration - (time.monotonic() - start)
                try:
                    raw_msg = await asyncio.wait_for(ws.recv(), timeout=min(remaining, 10) + 1)
                except asyncio.TimeoutError:
                    break
                now = time.monotonic()
                for frame in raw_msg.split(_REC_SEP):
                    frame = frame.strip()
                    if not frame:
                        continue
                    # type 6 = ping; reply to keep the connection alive
                    if frame == '{"type":6}':
                        await ws.send('{"type":6}' + _REC_SEP)
                        continue
                    if '"type":1' in frame:
                        res.messages += 1
                        res.gaps_ms.append((now - last_msg) * 1000)
                        last_msg = now
    except ConnectionRefusedError:
        res.error = "connection refused"
        ready_evt.set()
    except Exception as e:
        res.error = f"{type(e).__name__}: {str(e)[:60]}"
        ready_evt.set()


async def _run_fanout(ws_url: str, n: int, groups: list[str], duration: float, ramp: float) -> list[_ConnResult]:
    results = [_ConnResult(i) for i in range(n)]
    tasks = []
    per_conn_delay = (ramp / n) if (ramp > 0 and n > 0) else 0.0
    for i in range(n):
        evt = asyncio.Event()
        tasks.append(asyncio.create_task(_fanout_conn(ws_url, groups, duration, results[i], evt)))
        if per_conn_delay:
            await asyncio.sleep(per_conn_delay)
    await asyncio.gather(*tasks, return_exceptions=True)
    return results


def tier_fanout(cfg: dict, connections: int, duration: int, group: str, ramp: float):
    ws_url = _hub_ws_url(cfg)
    groups = ["metrics", "analytics"] if group == "both" else [group]

    console.print(Panel(
        f"[cyan]Tier B — DashboardHub fan-out[/cyan]\n"
        f"Hub: {ws_url}\n"
        f"Connections: {connections}   Duration: {duration}s   Groups: {', '.join(groups)}   Ramp: {ramp}s",
        border_style="cyan",
    ))

    sampler = autodetect_sampler()
    with sampler.session(ramp + duration) as session:
        t0 = time.monotonic()
        results = asyncio.run(_run_fanout(ws_url, connections, groups, float(duration), ramp))
        elapsed = time.monotonic() - t0

    connected = sum(1 for r in results if r.connected)
    failed = [r for r in results if r.error]
    total_msgs = sum(r.messages for r in results)
    all_gaps = [g for r in results for g in r.gaps_ms]

    t = Table(title="Fan-out results", box=box.ROUNDED)
    t.add_column("Metric", style="cyan")
    t.add_column("Value", justify="right")
    t.add_row("Connections requested", str(connections))
    t.add_row("Connected OK", f"{connected}")
    t.add_row("Failed", f"[red]{len(failed)}[/red]" if failed else "0")
    t.add_row("Total messages", f"{total_msgs:,}")
    t.add_row("Msgs/connection (avg)", f"{(total_msgs / connected):.1f}" if connected else "—")
    t.add_row("Wall time", f"{elapsed:.1f}s")
    if all_gaps:
        t.add_row("Push gap mean", f"{statistics.mean(all_gaps):.0f} ms")
        t.add_row("Push gap p95", f"{_pct(all_gaps, 95):.0f} ms")
        t.add_row("Push gap max", f"{max(all_gaps):.0f} ms")
    console.print(t)

    if failed:
        sample = {}
        for r in failed:
            sample[r.error] = sample.get(r.error, 0) + 1
        console.print("[red]Failure reasons:[/red]")
        for err, cnt in sample.items():
            console.print(f"  {cnt}× {err}")

    _print_metrics_or_warn(session)

    # Pass/fail verdict: metrics broadcast every 2s, analytics every 5s. With healthy
    # fan-out the p95 push gap should stay near the broadcast interval (≤ ~6s).
    verdict_ok = connected == connections and (not all_gaps or _pct(all_gaps, 95) <= 8000)
    _verdict(verdict_ok,
             "fan-out healthy: all connected, push cadence steady"
             if verdict_ok else
             "degraded: connection failures or push cadence stalled")
    return verdict_ok


def _verdict(ok: bool, msg: str):
    color = "green" if ok else "red"
    icon = "✓" if ok else "✗"
    console.print(f"\n[{color}]{icon} {msg}[/{color}]")


# ══════════════════════════════════════════════════════════════════════════════
# Tier C — queue pause/resume hammer
# ══════════════════════════════════════════════════════════════════════════════
#
# The dashboard's QueueOperationsService writes paused queue names into the
# Hangfire set "queue:paused" (AddToSet / RemoveFromSet), and QueuePauseServerFilter
# reads it on every state election. We mimic those exact storage writes from many
# concurrent workers to probe for torn state / errors under write contention.
#
# Storage layout (verified): hangfire.set has UNIQUE(key, value); pause = upsert a
# row (key='queue:paused', value=<queue>), resume = delete that row.

_PAUSED_SET_KEY = "queue:paused"


def _pause(cur, provider: str, queue: str):
    """Mirror AddToSet: idempotent upsert of (key='queue:paused', value=<queue>)."""
    if provider == "sqlserver":
        # No ON CONFLICT; Set has unique (Key, Value) → guard with NOT EXISTS.
        cur.execute(
            """
            INSERT INTO [HangFire].[Set] ([Key], Score, [Value])
            SELECT ?, 0.0, ?
            WHERE NOT EXISTS (
                SELECT 1 FROM [HangFire].[Set] WHERE [Key] = ? AND [Value] = ?
            )
            """,
            (_PAUSED_SET_KEY, queue, _PAUSED_SET_KEY, queue),
        )
    else:
        cur.execute(
            """
            INSERT INTO hangfire.set (key, score, value)
            VALUES (%s, 0.0, %s)
            ON CONFLICT (key, value) DO UPDATE SET score = EXCLUDED.score
            """,
            (_PAUSED_SET_KEY, queue),
        )


def _resume(cur, provider: str, queue: str):
    """Mirror RemoveFromSet."""
    if provider == "sqlserver":
        cur.execute(
            "DELETE FROM [HangFire].[Set] WHERE [Key] = ? AND [Value] = ?",
            (_PAUSED_SET_KEY, queue),
        )
    else:
        cur.execute(
            "DELETE FROM hangfire.set WHERE key = %s AND value = %s",
            (_PAUSED_SET_KEY, queue),
        )


def _dup_paused_count(cur, provider: str) -> int:
    """Count duplicate (value) rows in the paused set — should always be 0."""
    if provider == "sqlserver":
        cur.execute(
            "SELECT [Value], COUNT(*) FROM [HangFire].[Set] WHERE [Key]=? "
            "GROUP BY [Value] HAVING COUNT(*) > 1",
            (_PAUSED_SET_KEY,),
        )
    else:
        cur.execute(
            "SELECT value, COUNT(*) FROM hangfire.set WHERE key=%s "
            "GROUP BY value HAVING COUNT(*) > 1",
            (_PAUSED_SET_KEY,),
        )
    return len(cur.fetchall())


def tier_pause(cfg: dict, workers: int, duration: int, queues: list[str]):
    import threading
    import psycopg  # noqa: F401  (only used when provider is postgresql)
    from db import get_connection

    provider = cfg_mod.get_db_provider(cfg)
    if provider not in ("postgresql", "sqlserver"):
        console.print(f"[red]Tier C supports postgresql or sqlserver, not '{provider}'.[/red]")
        return False

    console.print(Panel(
        f"[cyan]Tier C — queue pause/resume hammer[/cyan]\n"
        f"Provider: {provider}   Workers: {workers}   Duration: {duration}s   "
        f"Queues: {', '.join(queues)}",
        border_style="cyan",
    ))

    stop_evt = threading.Event()
    counters = {"ops": 0, "errors": 0, "conflicts": 0}
    lock = threading.Lock()
    errors_sample: list[str] = []
    op_latencies: list[float] = []

    def worker(wid: int):
        local_ops = 0
        local_err = 0
        local_lat: list[float] = []
        try:
            conn, prov = get_connection()
            conn.autocommit = True
            cur = conn.cursor()
        except Exception as e:
            with lock:
                counters["errors"] += 1
                errors_sample.append(f"connect: {str(e)[:60]}")
            return

        import random
        while not stop_evt.is_set():
            q = random.choice(queues)
            toggle = random.random() < 0.5
            t0 = time.perf_counter()
            try:
                if toggle:
                    _pause(cur, prov, q)
                else:
                    _resume(cur, prov, q)
                local_lat.append((time.perf_counter() - t0) * 1000)
                local_ops += 1
            except Exception as e:
                local_err += 1
                if len(errors_sample) < 10:
                    with lock:
                        errors_sample.append(f"{type(e).__name__}: {str(e)[:50]}")
                try:
                    conn.rollback()
                except Exception:
                    pass

        try:
            cur.close()
            conn.close()
        except Exception:
            pass
        with lock:
            counters["ops"] += local_ops
            counters["errors"] += local_err
            op_latencies.extend(local_lat)

    sampler = autodetect_sampler()
    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(workers)]

    with sampler.session(duration) as session:
        t0 = time.monotonic()
        for th in threads:
            th.start()
        time.sleep(duration)
        stop_evt.set()
        for th in threads:
            th.join(timeout=10)
        elapsed = time.monotonic() - t0

    # Consistency check: no duplicate (key,value) rows should exist (the UNIQUE
    # index guarantees this, but we verify no torn state leaked under contention).
    final_state_ok = True
    dup_count = 0
    try:
        conn, prov = get_connection()
        conn.autocommit = True
        cur = conn.cursor()
        dup_count = _dup_paused_count(cur, prov)
        final_state_ok = dup_count == 0
        # Clean up: resume everything we may have left paused.
        for q in queues:
            _resume(cur, prov, q)
        cur.close()
        conn.close()
    except Exception as e:
        console.print(f"[yellow]Post-check warning: {e}[/yellow]")

    ops_per_sec = counters["ops"] / elapsed if elapsed > 0 else 0
    t = Table(title="Pause/resume results", box=box.ROUNDED)
    t.add_column("Metric", style="cyan")
    t.add_column("Value", justify="right")
    t.add_row("Workers", str(workers))
    t.add_row("Total ops", f"{counters['ops']:,}")
    t.add_row("Ops/sec", f"{ops_per_sec:,.0f}")
    t.add_row("Errors", f"[red]{counters['errors']}[/red]" if counters["errors"] else "0")
    t.add_row("Duplicate rows after run", f"[red]{dup_count}[/red]" if dup_count else "0")
    if op_latencies:
        t.add_row("Op latency mean", f"{statistics.mean(op_latencies):.1f} ms")
        t.add_row("Op latency p95", f"{_pct(op_latencies, 95):.1f} ms")
        t.add_row("Op latency max", f"{max(op_latencies):.1f} ms")
    console.print(t)

    if errors_sample:
        console.print("[red]Error sample:[/red]")
        for e in errors_sample[:10]:
            console.print(f"  {e}")

    _print_metrics_or_warn(session)

    verdict_ok = counters["errors"] == 0 and final_state_ok
    _verdict(verdict_ok,
             "no errors, no torn state under write contention"
             if verdict_ok else
             "contention surfaced errors or inconsistent paused-set state")
    return verdict_ok


# ══════════════════════════════════════════════════════════════════════════════
# Tier D — DB read latency under write load
# ══════════════════════════════════════════════════════════════════════════════
#
# Seed jobs at a target rate (write pressure) while concurrently polling the same
# monitor queries the dashboard runs (GetStatistics-style aggregates). We measure
# the READ latency: if writes starve reads, p95 read latency climbs.

def _monitor_read(cur, provider: str):
    """Run the aggregate the metrics broadcast depends on (counts by state)."""
    if provider == "sqlserver":
        cur.execute(
            """
            SELECT StateName, COUNT(*)
            FROM [HangFire].Job
            WHERE StateName IS NOT NULL
            GROUP BY StateName
            """
        )
    else:
        cur.execute(
            """
            SELECT statename, COUNT(*)
            FROM hangfire.job
            WHERE statename IS NOT NULL
            GROUP BY statename
            """
        )
    cur.fetchall()


def _quiet_seed(conn, provider: str, count: int, queue: str) -> int:
    """Seed `count` mixed jobs without any Rich progress UI (thread-safe).

    Reuses jobs.py's verified low-level insert helpers so we stay consistent with
    the real Hangfire row layout, but avoids the Progress bar that corrupts output
    when called from a background thread.
    """
    import random
    from jobs import TYPE_CONFIGS, INVOCATIONS, _insert_job, _insert_tags

    prev_autocommit = getattr(conn, "autocommit", None)
    try:
        conn.autocommit = True
    except Exception:
        pass

    inserted = 0
    types = list(TYPE_CONFIGS.keys())
    for _ in range(count):
        cur = conn.cursor()
        t = random.choice(types)
        entry = TYPE_CONFIGS[t]
        inv = INVOCATIONS[entry["invocation_key"]]
        state = entry["state_factory"]()
        tags = entry["tags"]
        offset = random.uniform(0, 1)
        try:
            job_id = _insert_job(cur, provider, inv, state, queue=queue, created_offset_hours=offset)
            if tags:
                _insert_tags(cur, provider, job_id, tags)
            inserted += 1
        except Exception:
            pass
        finally:
            try:
                cur.close()
            except Exception:
                pass

    if prev_autocommit is not None:
        try:
            conn.autocommit = prev_autocommit
        except Exception:
            pass
    return inserted


def tier_dbload(cfg: dict, duration: int, seed_rate: int, readers: int):
    import threading
    from db import get_connection

    provider = cfg_mod.get_db_provider(cfg)
    if provider not in ("postgresql", "sqlserver"):
        console.print(f"[red]Tier D supports postgresql or sqlserver, not '{provider}'.[/red]")
        return False

    console.print(Panel(
        f"[cyan]Tier D — DB read latency under write load[/cyan]\n"
        f"Provider: {provider}   Duration: {duration}s   "
        f"Seed rate: ~{seed_rate} jobs/batch   Readers: {readers}",
        border_style="cyan",
    ))

    stop_evt = threading.Event()
    read_latencies: list[float] = []
    write_count = {"n": 0}
    lock = threading.Lock()

    def reader(rid: int):
        local: list[float] = []
        try:
            conn, prov = get_connection()
            conn.autocommit = True
            cur = conn.cursor()
        except Exception:
            return
        while not stop_evt.is_set():
            t0 = time.perf_counter()
            try:
                _monitor_read(cur, prov)
                local.append((time.perf_counter() - t0) * 1000)
            except Exception:
                pass
            time.sleep(0.2)  # ~5 reads/sec per reader, matching dashboard cadence
        try:
            cur.close(); conn.close()
        except Exception:
            pass
        with lock:
            read_latencies.extend(local)

    def writer():
        try:
            conn, prov = get_connection()
        except Exception:
            return
        batch = max(10, seed_rate)
        while not stop_evt.is_set():
            try:
                inserted = _quiet_seed(conn, prov, batch, "default")
                with lock:
                    write_count["n"] += inserted
            except Exception:
                try:
                    conn.rollback()
                except Exception:
                    pass
            time.sleep(1.0)  # one batch per second
        try:
            conn.close()
        except Exception:
            pass

    sampler = autodetect_sampler()
    reader_threads = [threading.Thread(target=reader, args=(i,), daemon=True) for i in range(readers)]
    writer_thread = threading.Thread(target=writer, daemon=True)

    # Baseline: measure read latency for 3s with NO write load first.
    base_lat: list[float] = []
    try:
        conn, prov = get_connection()
        conn.autocommit = True
        cur = conn.cursor()
        bt = time.monotonic()
        while time.monotonic() - bt < 3:
            t0 = time.perf_counter()
            _monitor_read(cur, prov)
            base_lat.append((time.perf_counter() - t0) * 1000)
            time.sleep(0.2)
        cur.close(); conn.close()
    except Exception:
        pass

    with sampler.session(duration) as session:
        for th in reader_threads:
            th.start()
        writer_thread.start()
        time.sleep(duration)
        stop_evt.set()
        for th in reader_threads:
            th.join(timeout=10)
        writer_thread.join(timeout=15)

    t = Table(title="DB load results", box=box.ROUNDED)
    t.add_column("Metric", style="cyan")
    t.add_column("Value", justify="right")
    t.add_row("Jobs written", f"{write_count['n']:,}")
    if base_lat:
        t.add_row("Read p95 (baseline, no writes)", f"{_pct(base_lat, 95):.1f} ms")
    if read_latencies:
        t.add_row("Read samples (under load)", f"{len(read_latencies):,}")
        t.add_row("Read mean (under load)", f"{statistics.mean(read_latencies):.1f} ms")
        t.add_row("Read p95 (under load)", f"{_pct(read_latencies, 95):.1f} ms")
        t.add_row("Read max (under load)", f"{max(read_latencies):.1f} ms")
    console.print(t)

    _print_metrics_or_warn(session)

    # Verdict: read p95 under write load shouldn't blow past 4x the baseline,
    # and should stay under a hard 1s ceiling for a counts aggregate.
    ok = True
    reason = "read latency stayed bounded under write load"
    if read_latencies:
        load_p95 = _pct(read_latencies, 95)
        if load_p95 > 1000:
            ok = False
            reason = f"read p95 under load = {load_p95:.0f}ms exceeds 1s ceiling"
        elif base_lat:
            base_p95 = _pct(base_lat, 95)
            if base_p95 and load_p95 > base_p95 * 4 and load_p95 > 100:
                ok = False
                reason = f"read p95 degraded {load_p95/base_p95:.1f}x under write load"
    _verdict(ok, reason)
    return ok


# ══════════════════════════════════════════════════════════════════════════════
# Tier A — Blazor circuit flood (real browser contexts)
# ══════════════════════════════════════════════════════════════════════════════
#
# Each browser context = one Blazor Server circuit (server-side component state +
# a SignalR _blazor connection). This is the layer most prone to memory growth.
# We open N contexts on the live dashboard, hold them, then close and check that
# the server's working set recovers (no leaked circuits).

def tier_circuits(cfg: dict, contexts: int, duration: int, page_path: str):
    from playwright.sync_api import sync_playwright

    dashboard = cfg_mod.get_dashboard_url(cfg)
    target = dashboard + page_path

    console.print(Panel(
        f"[cyan]Tier A — Blazor circuit flood[/cyan]\n"
        f"Target: {target}\n"
        f"Contexts: {contexts}   Hold: {duration}s",
        border_style="cyan",
    ))

    sampler = autodetect_sampler()
    opened = 0
    errors = 0

    # Sample window = idle baseline (3s) + ramp (contexts*0.25s) + hold + recovery wait (15s)
    metrics_window = 3 + contexts * 0.25 + duration + 15
    with sampler.session(metrics_window) as session:
        # capture an idle baseline window before opening anything
        time.sleep(3)
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            pages = []
            for i in range(contexts):
                try:
                    ctx = browser.new_context()
                    page = ctx.new_page()
                    page.set_default_timeout(20000)
                    page.goto(target, wait_until="domcontentloaded")
                    pages.append((ctx, page))
                    opened += 1
                except Exception:
                    errors += 1
                time.sleep(0.25)  # gentle ramp

            console.print(f"  Opened {opened}/{contexts} circuits, holding {duration}s …")
            # Hold the circuits open so server-side state + SignalR push accumulates.
            time.sleep(duration)

            # Close everything → circuits should dispose server-side.
            for ctx, _ in pages:
                try:
                    ctx.close()
                except Exception:
                    pass
            browser.close()

        # Give the server time to dispose circuits + GC before final samples.
        console.print("  Closed all circuits, waiting 15s for server to reclaim …")
        time.sleep(15)

    stats = session.summarize() if session else {}

    t = Table(title="Circuit flood results", box=box.ROUNDED)
    t.add_column("Metric", style="cyan")
    t.add_column("Value", justify="right")
    t.add_row("Circuits opened", f"{opened}/{contexts}")
    t.add_row("Open errors", f"[red]{errors}[/red]" if errors else "0")
    console.print(t)

    panel = render_server_metrics(stats)
    if panel:
        console.print(panel)
    elif session is None:
        console.print("[yellow]Server metrics unavailable — cannot assess circuit cleanup.[/yellow]")
    if session:
        session.cleanup()

    # Verdict: working set after close+GC should return close to where it started.
    # We accept up to +25% residual over the run's first sample as "recovered".
    ok = opened == contexts and errors == 0
    reason = f"all {contexts} circuits opened"
    ws = stats.get("working_set_mb")
    conns = stats.get("connections")
    if ws and ws.first and ws.last:
        residual = (ws.last - ws.first) / ws.first * 100
        if residual > 25:
            ok = False
            reason = f"working set did not recover: +{residual:.0f}% residual after close (possible circuit leak)"
        else:
            reason += f"; working set recovered (residual {residual:+.0f}%)"
    if conns and conns.last is not None and conns.last > 2:
        # connections should drop back near baseline after closing all contexts
        reason += f"; WARNING {conns.last:.0f} connections still open"
    _verdict(ok, reason)
    return ok


# ══════════════════════════════════════════════════════════════════════════════
# Tier E — combined soak
# ══════════════════════════════════════════════════════════════════════════════
#
# Run fan-out + pause hammer + db load (and optionally a few circuits) together
# for a sustained period. The headline question is the leak question: does the
# server's working set stabilise, or does it climb monotonically over the soak?

def tier_combined(cfg: dict, duration: int, fanout_n: int, pause_workers: int,
                  seed_rate: int, circuits_n: int):
    import threading

    console.print(Panel(
        f"[cyan]Tier E — combined soak[/cyan]\n"
        f"Duration: {duration}s\n"
        f"Fan-out subscribers: {fanout_n}   Pause workers: {pause_workers}   "
        f"Seed rate: {seed_rate}/s   Circuits: {circuits_n}",
        border_style="cyan",
    ))

    ws_url = _hub_ws_url(cfg)
    sampler = autodetect_sampler()
    provider = cfg_mod.get_db_provider(cfg)

    # Run fan-out in its own thread (asyncio loop), pause + dbload in threads.
    fanout_results: list[_ConnResult] = []
    pause_stats = {"ops": 0, "errors": 0}
    write_count = {"n": 0}
    stop_evt = threading.Event()
    lock = threading.Lock()

    def fanout_thread():
        try:
            res = asyncio.run(_run_fanout(ws_url, fanout_n, ["metrics", "analytics"], float(duration), ramp=min(10.0, fanout_n * 0.1)))
            fanout_results.extend(res)
        except Exception as e:
            console.print(f"[yellow]fanout thread: {e}[/yellow]")

    def pause_thread():
        import random
        from db import get_connection
        try:
            conn, prov = get_connection()
            conn.autocommit = True
            cur = conn.cursor()
        except Exception:
            return
        queues = ["default", "critical", "emails", "reports"]
        ops = 0; err = 0
        while not stop_evt.is_set():
            q = random.choice(queues)
            try:
                if random.random() < 0.5:
                    _pause(cur, prov, q)
                else:
                    _resume(cur, prov, q)
                ops += 1
            except Exception:
                err += 1
            time.sleep(0.05)  # 20 ops/sec — sustained, not a flood
        for q in queues:
            try:
                _resume(cur, prov, q)
            except Exception:
                pass
        try:
            cur.close(); conn.close()
        except Exception:
            pass
        with lock:
            pause_stats["ops"] += ops
            pause_stats["errors"] += err

    def dbload_thread():
        from db import get_connection
        try:
            conn, prov = get_connection()
        except Exception:
            return
        while not stop_evt.is_set():
            try:
                n = _quiet_seed(conn, prov, max(10, seed_rate), "default")
                with lock:
                    write_count["n"] += n
            except Exception:
                try:
                    conn.rollback()
                except Exception:
                    pass
            time.sleep(1.0)
        try:
            conn.close()
        except Exception:
            pass

    # Optional circuits via a single Playwright instance held for the soak.
    circuit_holder = {"opened": 0}

    def circuits_thread():
        if circuits_n <= 0:
            return
        try:
            from playwright.sync_api import sync_playwright
        except Exception:
            return
        dashboard = cfg_mod.get_dashboard_url(cfg)
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            pages = []
            for _ in range(circuits_n):
                try:
                    ctx = browser.new_context()
                    page = ctx.new_page()
                    page.goto(dashboard + "/", wait_until="domcontentloaded", timeout=20000)
                    pages.append(ctx)
                    circuit_holder["opened"] += 1
                except Exception:
                    pass
                if stop_evt.is_set():
                    break
            while not stop_evt.is_set():
                time.sleep(0.5)
            for ctx in pages:
                try:
                    ctx.close()
                except Exception:
                    pass
            browser.close()

    threads = [
        threading.Thread(target=pause_thread, daemon=True),
        threading.Thread(target=dbload_thread, daemon=True),
        threading.Thread(target=circuits_thread, daemon=True),
    ]
    fanout_t = threading.Thread(target=fanout_thread, daemon=True)

    # Sample window = soak duration + circuit startup headroom
    metrics_window = duration + max(10, circuits_n * 0.5) + 10
    with sampler.session(metrics_window) as session:
        for th in threads:
            th.start()
        fanout_t.start()

        # progress ticks every 10s with a live working-set read isn't available
        # mid-collection, so just report elapsed.
        waited = 0
        tick = 10
        while waited < duration:
            time.sleep(min(tick, duration - waited))
            waited += tick
            console.print(f"  [dim]soak {min(waited, duration)}/{duration}s …[/dim]")

        stop_evt.set()
        fanout_t.join(timeout=20)
        for th in threads:
            th.join(timeout=20)

    connected = sum(1 for r in fanout_results if r.connected)
    total_msgs = sum(r.messages for r in fanout_results)

    t = Table(title="Combined soak results", box=box.ROUNDED)
    t.add_column("Metric", style="cyan")
    t.add_column("Value", justify="right")
    t.add_row("Fan-out connected", f"{connected}/{fanout_n}")
    t.add_row("Fan-out messages", f"{total_msgs:,}")
    t.add_row("Pause ops", f"{pause_stats['ops']:,}")
    t.add_row("Pause errors", f"[red]{pause_stats['errors']}[/red]" if pause_stats["errors"] else "0")
    t.add_row("Jobs written", f"{write_count['n']:,}")
    t.add_row("Circuits held", f"{circuit_holder['opened']}/{circuits_n}")
    console.print(t)

    stats = session.summarize() if session else {}
    panel = render_server_metrics(stats)
    if panel:
        console.print(panel)
    if session:
        session.cleanup()

    # Headline verdict: the leak question. We measure growth from a POST-WARM-UP
    # baseline (~20s in), because the first sample is taken before load ramps and
    # would over-report growth on short runs. A leak keeps climbing after warm-up;
    # a healthy service plateaus. For short runs where no post-warm-up window
    # exists, we fall back to the raw delta but only warn, never fail.
    ok = pause_stats["errors"] == 0
    reason = "soak stable"
    ws = stats.get("working_set_mb")
    if ws and ws.count:
        growth = ws.post_warmup_growth_pct(warmup_s=20.0)
        if growth is not None:
            if growth > 25:
                ok = False
                reason = f"working set climbed +{growth:.0f}% after warm-up — investigate for leak"
            else:
                reason = f"working set growth {growth:+.0f}% after warm-up (within tolerance)"
        else:
            # Run too short to judge a leak — report raw delta as info only.
            raw = ws.delta or 0.0
            reason = (f"run too short for leak verdict; working set "
                      f"{('+' if raw >= 0 else '')}{raw:.0f}MB end-to-end (warm-up included)")
    _verdict(ok, reason)
    return ok


# ══════════════════════════════════════════════════════════════════════════════
# CLI
# ══════════════════════════════════════════════════════════════════════════════

@click.group()
def cli():
    """Hypothesis-driven stress tests for HangfireDashboard."""
    pass


import scenario as _scenario_mod
cli.add_command(_scenario_mod.cli, name="scenario")


@cli.command()
@click.option("--connections", "-c", default=50, show_default=True)
@click.option("--duration", "-d", default=60, show_default=True, help="Seconds to hold connections.")
@click.option("--group", "-g", type=click.Choice(["metrics", "analytics", "both"]), default="both", show_default=True)
@click.option("--ramp", default=5.0, show_default=True, help="Seconds to ramp up all connections.")
def fanout(connections, duration, group, ramp):
    """Tier B — flood the DashboardHub with N WebSocket subscribers."""
    cfg = cfg_mod.load()
    ok = tier_fanout(cfg, connections, duration, group, ramp)
    raise SystemExit(0 if ok else 1)


@cli.command()
@click.option("--workers", "-c", default=16, show_default=True, help="Concurrent pause/resume workers.")
@click.option("--duration", "-d", default=30, show_default=True)
@click.option("--queues", default="default,critical,emails,reports", show_default=True,
              help="Comma-separated queue names to toggle.")
def pause(workers, duration, queues):
    """Tier C — hammer queue pause/resume under concurrency."""
    cfg = cfg_mod.load()
    qlist = [q.strip() for q in queues.split(",") if q.strip()]
    ok = tier_pause(cfg, workers, duration, qlist)
    raise SystemExit(0 if ok else 1)


@cli.command()
@click.option("--duration", "-d", default=45, show_default=True)
@click.option("--seed-rate", default=100, show_default=True, help="Jobs inserted per 1s batch.")
@click.option("--readers", default=5, show_default=True, help="Concurrent stat readers.")
def dbload(duration, seed_rate, readers):
    """Tier D — measure read latency while seeding jobs hard."""
    cfg = cfg_mod.load()
    ok = tier_dbload(cfg, duration, seed_rate, readers)
    raise SystemExit(0 if ok else 1)


@cli.command()
@click.option("--contexts", "-c", default=20, show_default=True, help="Number of browser circuits.")
@click.option("--duration", "-d", default=60, show_default=True, help="Seconds to hold circuits open.")
@click.option("--page", "page_path", default="/", show_default=True, help="Dashboard route to load.")
def circuits(contexts, duration, page_path):
    """Tier A — open N Blazor circuits, hold, then verify server recovers."""
    cfg = cfg_mod.load()
    ok = tier_circuits(cfg, contexts, duration, page_path)
    raise SystemExit(0 if ok else 1)


@cli.command()
@click.option("--duration", "-d", default=300, show_default=True, help="Soak duration in seconds.")
@click.option("--fanout", "fanout_n", default=50, show_default=True)
@click.option("--pause-workers", default=8, show_default=True)
@click.option("--seed-rate", default=50, show_default=True)
@click.option("--circuits", "circuits_n", default=10, show_default=True)
def combined(duration, fanout_n, pause_workers, seed_rate, circuits_n):
    """Tier E — run all pressure sources together as a soak (leak detection)."""
    cfg = cfg_mod.load()
    ok = tier_combined(cfg, duration, fanout_n, pause_workers, seed_rate, circuits_n)
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    cli()
