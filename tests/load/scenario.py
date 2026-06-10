"""
scenario.py — Persona-based concurrent browser scenario for HangfireDashboard.

Five "users" drive the live dashboard at the same time, each with a distinct
behaviour, using the async Playwright API so they run as true concurrent
coroutines in one event loop. Server-side metrics (CPU / working set / GC heap /
connections) are sampled via dotnet-counters for the whole run.

Personas (default):
    1  watcher-home       — open Home, stay, monitor its SignalR/WebSocket health
    2  watcher-analytics  — open Analytics ▸ Performance, stay, monitor SignalR
    3  trigger-runner     — on Recurring: select-all ▸ click Trigger, repeated
    4  queue-operator     — on Queues: pause/resume queues at random intervals
    5  wanderer           — navigate to random pages, often before they finish
                            loading (cancel mid-load, jump to the next page)

Each persona's own browser context = its own Blazor circuit. WebSocket activity
is captured via Playwright's websocket events, which is the correct way to verify
the SignalR/Blazor circuit stays alive (frames keep flowing, no unexpected close).

Usage:
    python cli.py stress scenario -d 120
    python cli.py stress scenario -d 300 --headed          # watch it happen
    python cli.py stress scenario -d 120 --trigger-every 8 --queue-every 5
"""
from __future__ import annotations

import asyncio
import random
import time
from dataclasses import dataclass, field

import click
from rich import box
from rich.console import Console
from rich.panel import Panel
from rich.table import Table

import config as cfg_mod
from srvmetrics import ServerMetricsSampler
from stress import render_server_metrics, _verdict

console = Console()


def _pct(values: list[float], p: float) -> float | None:
    """Percentile via nearest-rank on a sorted copy."""
    if not values:
        return None
    s = sorted(values)
    if len(s) == 1:
        return s[0]
    k = max(0, min(len(s) - 1, int(round((p / 100.0) * (len(s) - 1)))))
    return s[k]


def _render_cadence(cad: "CadenceStats"):
    """Render the per-channel SignalR push cadence — the realtime downgrade check."""
    if not cad.connected:
        console.print(f"[yellow]SignalR cadence probe did not connect[/yellow]"
                      + (f": {cad.error}" if cad.error else ""))
        return

    import statistics as _st
    t = Table(title="SignalR push cadence (inter-arrival gaps)", box=box.ROUNDED)
    t.add_column("Channel", style="cyan")
    t.add_column("Expected", justify="right")
    t.add_column("Pushes", justify="right")
    t.add_column("mean", justify="right")
    t.add_column("p95", justify="right")
    t.add_column("max", justify="right")

    def row(label: str, expected_ms: int, gaps: list[float], count: int):
        if not gaps:
            t.add_row(label, f"~{expected_ms}ms", str(count), "—", "—", "—")
            return
        mean = _st.mean(gaps)
        p95 = _pct(gaps, 95)
        mx = max(gaps)
        # flag p95 that drifts >2x expected
        p95_str = f"{p95:.0f}ms"
        if p95 > expected_ms * 2:
            p95_str = f"[red]{p95_str}[/red]"
        mx_str = f"{mx:.0f}ms"
        if mx > expected_ms * 3:
            mx_str = f"[yellow]{mx_str}[/yellow]"
        t.add_row(label, f"~{expected_ms}ms", str(count), f"{mean:.0f}ms", p95_str, mx_str)

    row("MetricsUpdated", 2000, cad.metrics_gaps, cad.metrics_count)
    row("AnalyticsUpdate", 5000, cad.analytics_gaps, cad.analytics_count)
    console.print(t)


# ─── per-persona telemetry ────────────────────────────────────────────────────

@dataclass
class PersonaStats:
    name: str
    actions: int = 0
    errors: int = 0
    # WebSocket health
    ws_opened: int = 0
    ws_closed: int = 0
    frames_received: int = 0
    frames_sent: int = 0
    notes: list[str] = field(default_factory=list)

    def note(self, msg: str):
        if len(self.notes) < 8:
            self.notes.append(msg)


def _attach_ws_monitor(page, stats: PersonaStats):
    """Wire Playwright websocket events into the persona's telemetry.

    Blazor Server uses a _blazor WebSocket for its circuit; the dashboard also has
    a DashboardHub at /hubs/dashboard. We count frames on both so a 'watcher' can
    prove its realtime channel is alive (frames keep arriving, socket not closed).
    """
    def on_ws(ws):
        stats.ws_opened += 1

        def on_recv(_payload):
            stats.frames_received += 1

        def on_sent(_payload):
            stats.frames_sent += 1

        def on_close():
            stats.ws_closed += 1
            stats.note(f"ws closed: {ws.url.split('/')[-1][:40]}")

        ws.on("framereceived", on_recv)
        ws.on("framesent", on_sent)
        ws.on("close", on_close)

    page.on("websocket", on_ws)


# ─── persona behaviours ───────────────────────────────────────────────────────

async def persona_watcher(context, dashboard: str, route: str, label: str,
                          duration: float, stats: PersonaStats):
    """Open one route, stay put, and monitor the realtime channel."""
    page = await context.new_page()
    _attach_ws_monitor(page, stats)
    try:
        await page.goto(dashboard + route, wait_until="domcontentloaded", timeout=20000)
        stats.actions += 1
        stats.note(f"opened {route}")
    except Exception as e:
        stats.errors += 1
        stats.note(f"open failed: {str(e)[:50]}")
        return

    # Standby: just live. Periodically sample frame count to detect a stalled feed.
    start = time.monotonic()
    last_frames = 0
    last_check = start
    while time.monotonic() - start < duration:
        await asyncio.sleep(5)
        now = time.monotonic()
        delta_frames = stats.frames_received - last_frames
        if now - last_check >= 15 and delta_frames == 0:
            stats.note(f"no frames for ~{int(now - last_check)}s")
        if delta_frames > 0:
            last_check = now
        last_frames = stats.frames_received
    try:
        await page.close()
    except Exception:
        pass


async def persona_trigger(context, dashboard: str, duration: float,
                          every: float, stats: PersonaStats):
    """On the Recurring page: select-all, then click Trigger, at random intervals."""
    page = await context.new_page()
    _attach_ws_monitor(page, stats)
    try:
        await page.goto(dashboard + "/recurring", wait_until="domcontentloaded", timeout=20000)
        stats.note("opened /recurring")
    except Exception as e:
        stats.errors += 1
        stats.note(f"open failed: {str(e)[:50]}")
        return

    start = time.monotonic()
    while time.monotonic() - start < duration:
        try:
            # The select-all checkbox lives in the table header (first checkbox).
            select_all = page.locator("thead input[type='checkbox']").first
            await select_all.wait_for(state="visible", timeout=8000)
            # Ensure it ends up checked (toggle to checked if not already).
            if not await select_all.is_checked():
                await select_all.click()
            # Trigger button enables once something is selected.
            trigger_btn = page.locator("button:has-text('Trigger')").first
            await trigger_btn.wait_for(state="visible", timeout=8000)
            if await trigger_btn.is_enabled():
                await trigger_btn.click()
                stats.actions += 1
            else:
                stats.note("Trigger still disabled (no jobs?)")
        except Exception as e:
            stats.errors += 1
            stats.note(f"trigger err: {str(e)[:50]}")
        # random gap between triggers (same style as the queue operator)
        await asyncio.sleep(max(0.5, every + random.uniform(-2.0, 3.0)))
    try:
        await page.close()
    except Exception:
        pass


async def persona_queue_operator(context, dashboard: str, duration: float,
                                 every: float, stats: PersonaStats):
    """On the Queues page: pause/resume whatever queues are present, random gaps."""
    page = await context.new_page()
    _attach_ws_monitor(page, stats)
    try:
        await page.goto(dashboard + "/queues", wait_until="domcontentloaded", timeout=20000)
        stats.note("opened /queues")
    except Exception as e:
        stats.errors += 1
        stats.note(f"open failed: {str(e)[:50]}")
        return

    start = time.monotonic()
    while time.monotonic() - start < duration:
        try:
            # Prefer a Pause if available, else Resume — toggle whatever exists.
            pause_btns = page.locator("button:has-text('Pause')")
            resume_btns = page.locator("button:has-text('Resume')")
            n_pause = await pause_btns.count()
            n_resume = await resume_btns.count()

            target = None
            if n_pause > 0 and (n_resume == 0 or random.random() < 0.5):
                target = pause_btns.nth(random.randrange(n_pause))
                kind = "pause"
            elif n_resume > 0:
                target = resume_btns.nth(random.randrange(n_resume))
                kind = "resume"

            if target is not None:
                await target.click(timeout=6000)
                stats.actions += 1
            else:
                stats.note("no pause/resume buttons present")
        except Exception as e:
            stats.errors += 1
            stats.note(f"queue err: {str(e)[:50]}")
        # random interval
        await asyncio.sleep(max(0.5, every + random.uniform(-2.0, 3.0)))
    try:
        await page.close()
    except Exception:
        pass


async def persona_wanderer(context, dashboard: str, duration: float,
                           every: float, stats: PersonaStats):
    """Navigate to random pages, frequently interrupting the load mid-flight."""
    routes = [
        "/", "/queues", "/jobs/enqueued", "/jobs/processing", "/jobs/scheduled",
        "/jobs/succeeded", "/jobs/failed", "/retries", "/recurring", "/tags",
        "/search", "/servers", "/audit", "/analytics", "/analytics/performance",
        "/analytics/failures", "/analytics/queues", "/analytics/recurring",
    ]
    page = await context.new_page()
    _attach_ws_monitor(page, stats)

    start = time.monotonic()
    while time.monotonic() - start < duration:
        route = random.choice(routes)
        try:
            # "commit" returns as soon as the server responds — we deliberately do
            # NOT wait for the page to finish loading, then jump away quickly to
            # simulate an impatient user cancelling navigations mid-load.
            interrupt = random.random() < 0.5
            wait_until = "commit" if interrupt else "domcontentloaded"
            await page.goto(dashboard + route, wait_until=wait_until, timeout=15000)
            stats.actions += 1
            if interrupt:
                # jump away almost immediately, before the circuit settles
                await asyncio.sleep(random.uniform(0.1, 0.8))
            else:
                await asyncio.sleep(max(0.3, every + random.uniform(-1.0, 2.0)))
        except Exception as e:
            # An aborted navigation is expected sometimes; only count hard errors.
            msg = str(e)
            if "interrupted" in msg.lower() or "aborted" in msg.lower() or "navigating" in msg.lower():
                stats.note(f"nav interrupted ({route})")
            else:
                stats.errors += 1
                stats.note(f"nav err {route}: {msg[:40]}")
    try:
        await page.close()
    except Exception:
        pass


# ─── SignalR cadence probe ────────────────────────────────────────────────────
#
# The persona watchers only count frames; they cannot prove the *cadence* of the
# server pushes. This passive probe subscribes to the DashboardHub directly and
# records the inter-arrival gap for each channel separately:
#     MetricsUpdated  — expected every ~2s  (MetricsBroadcastService)
#     AnalyticsUpdate — expected every ~5s  (AnalyticsBroadcastService)
# If GetStatistics()/analytics queries slow under load, these gaps grow — that's
# the SignalR "downgrade" we want to catch with numbers, not vibes.

_REC_SEP = "\x1e"
_HANDSHAKE = '{"protocol":"json","version":1}' + _REC_SEP


@dataclass
class CadenceStats:
    metrics_gaps: list[float] = field(default_factory=list)
    analytics_gaps: list[float] = field(default_factory=list)
    metrics_count: int = 0
    analytics_count: int = 0
    connected: bool = False
    error: str | None = None


async def cadence_probe(ws_url: str, duration: float, cad: CadenceStats):
    import json
    import websockets

    try:
        async with websockets.connect(
            ws_url,
            additional_headers={"User-Agent": "HF-CadenceProbe/1.0"},
            open_timeout=15,
            close_timeout=5,
        ) as ws:
            await ws.send(_HANDSHAKE)
            await asyncio.wait_for(ws.recv(), timeout=15)  # handshake ack
            cad.connected = True

            # subscribe to BOTH channels
            await ws.send('{"type":1,"target":"SubscribeToMetrics","arguments":[]}' + _REC_SEP)
            await ws.send('{"type":1,"target":"SubscribeToAnalytics","arguments":[]}' + _REC_SEP)

            start = time.monotonic()
            last_metrics = None
            last_analytics = None

            while (time.monotonic() - start) < duration:
                remaining = duration - (time.monotonic() - start)
                try:
                    raw = await asyncio.wait_for(ws.recv(), timeout=min(remaining, 15) + 1)
                except asyncio.TimeoutError:
                    break
                now = time.monotonic()
                for frame in raw.split(_REC_SEP):
                    frame = frame.strip()
                    if not frame:
                        continue
                    if frame == '{"type":6}':  # ping
                        await ws.send('{"type":6}' + _REC_SEP)
                        continue
                    if '"type":1' not in frame:
                        continue
                    # cheap target sniff to avoid full parse cost on the hot path
                    if "MetricsUpdated" in frame:
                        if last_metrics is not None:
                            cad.metrics_gaps.append((now - last_metrics) * 1000)
                        last_metrics = now
                        cad.metrics_count += 1
                    elif "AnalyticsUpdate" in frame:
                        if last_analytics is not None:
                            cad.analytics_gaps.append((now - last_analytics) * 1000)
                        last_analytics = now
                        cad.analytics_count += 1
    except Exception as e:
        cad.error = f"{type(e).__name__}: {str(e)[:60]}"


def _hub_ws_url(dashboard: str) -> str:
    ws = dashboard.replace("https://", "wss://").replace("http://", "ws://")
    return ws.rstrip("/") + "/hubs/dashboard"


# ─── orchestration ────────────────────────────────────────────────────────────

async def _run_scenario(dashboard: str, duration: float, headed: bool,
                        trigger_every: float, queue_every: float,
                        wander_every: float, cad: "CadenceStats") -> list[PersonaStats]:
    from playwright.async_api import async_playwright

    stats = {
        "watcher-home": PersonaStats("watcher-home"),
        "watcher-analytics": PersonaStats("watcher-analytics"),
        "trigger-runner": PersonaStats("trigger-runner"),
        "queue-operator": PersonaStats("queue-operator"),
        "wanderer": PersonaStats("wanderer"),
    }

    ws_url = _hub_ws_url(dashboard)

    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=not headed)
        # Each persona gets its own context = its own Blazor circuit.
        contexts = [await browser.new_context() for _ in range(5)]

        tasks = [
            persona_watcher(contexts[0], dashboard, "/", "home", duration, stats["watcher-home"]),
            persona_watcher(contexts[1], dashboard, "/analytics/performance", "analytics",
                            duration, stats["watcher-analytics"]),
            persona_trigger(contexts[2], dashboard, duration, trigger_every, stats["trigger-runner"]),
            persona_queue_operator(contexts[3], dashboard, duration, queue_every, stats["queue-operator"]),
            persona_wanderer(contexts[4], dashboard, duration, wander_every, stats["wanderer"]),
            # passive cadence probe runs alongside the personas
            cadence_probe(ws_url, duration, cad),
        ]
        await asyncio.gather(*tasks, return_exceptions=True)

        for ctx in contexts:
            try:
                await ctx.close()
            except Exception:
                pass
        await browser.close()

    return list(stats.values())


def run(cfg: dict, duration: int, headed: bool, trigger_every: float,
        queue_every: float, wander_every: float) -> bool:
    dashboard = cfg_mod.get_dashboard_url(cfg)

    console.print(Panel(
        f"[cyan]Persona scenario — 5 concurrent browsers[/cyan]\n"
        f"Dashboard: {dashboard}   Duration: {duration}s   Headed: {headed}\n\n"
        f"[dim]1 watcher-home       → Home, monitor SignalR\n"
        f"2 watcher-analytics  → Analytics/Performance, monitor SignalR\n"
        f"3 trigger-runner     → Recurring: select-all + Trigger every ~{trigger_every}s\n"
        f"4 queue-operator     → Queues: pause/resume every ~{queue_every}s (random)\n"
        f"5 wanderer           → random pages, interrupting loads every ~{wander_every}s[/dim]",
        border_style="cyan",
    ))

    # Browser startup overhead before personas begin acting.
    metrics_window = duration + 20
    sampler = ServerMetricsSampler.autodetect(process_name="SampleApp", refresh_interval=1)

    cad = CadenceStats()
    with sampler.session(metrics_window) as session:
        results = asyncio.run(_run_scenario(
            dashboard, float(duration), headed, trigger_every, queue_every, wander_every, cad
        ))

    # ── per-persona table ──
    t = Table(title="Persona results", box=box.ROUNDED)
    t.add_column("Persona", style="cyan")
    t.add_column("Actions", justify="right")
    t.add_column("Errors", justify="right")
    t.add_column("WS open", justify="right")
    t.add_column("WS closed", justify="right")
    t.add_column("Frames recv", justify="right")
    for s in results:
        err = f"[red]{s.errors}[/red]" if s.errors else "0"
        closed = f"[yellow]{s.ws_closed}[/yellow]" if s.ws_closed else "0"
        t.add_row(s.name, str(s.actions), err, str(s.ws_opened), closed, f"{s.frames_received:,}")
    console.print(t)

    # notes
    for s in results:
        if s.notes:
            console.print(f"[dim]{s.name}:[/dim] " + " | ".join(s.notes[:6]))

    # ── SignalR push cadence (the realtime "downgrade" check) ──
    _render_cadence(cad)

    # server metrics
    if session is not None:
        panel = render_server_metrics(session.summarize())
        if panel:
            console.print(panel)
        session.cleanup()
    else:
        console.print("[yellow]Server metrics unavailable (dotnet-counters not found).[/yellow]")

    # ── verdict ──
    total_errors = sum(s.errors for s in results)
    watchers = [s for s in results if s.name.startswith("watcher")]
    watchers_alive = all(w.frames_received > 0 and w.ws_closed == 0 for w in watchers)
    actors_worked = (results[2].actions > 0 or results[3].actions > 0)

    # Cadence verdict: metrics push every ~2s, analytics every ~5s. We allow
    # generous ceilings (p95) before calling it a downgrade, since GC pauses and
    # one slow tick are normal. Sustained drift past these = real degradation.
    cadence_ok = True
    cadence_reason = ""
    if cad.connected:
        m_p95 = _pct(cad.metrics_gaps, 95)
        a_p95 = _pct(cad.analytics_gaps, 95)
        if m_p95 is not None and m_p95 > 4000:   # 2s expected → 4s ceiling
            cadence_ok = False
            cadence_reason = f"metrics push p95={m_p95:.0f}ms (expected ~2000ms) — realtime downgrade"
        elif a_p95 is not None and a_p95 > 9000:  # 5s expected → 9s ceiling
            cadence_ok = False
            cadence_reason = f"analytics push p95={a_p95:.0f}ms (expected ~5000ms) — realtime downgrade"

    ok = total_errors == 0 and watchers_alive and actors_worked and cadence_ok
    if not watchers_alive:
        reason = "a watcher's realtime channel stalled or its socket closed unexpectedly"
    elif not actors_worked:
        reason = "trigger/queue personas performed no actions (UI controls not found)"
    elif not cadence_ok:
        reason = cadence_reason
    elif total_errors:
        reason = f"{total_errors} persona error(s) during the run"
    else:
        reason = "all personas concurrent; watchers live, actors acted, SignalR cadence steady, no errors"
    _verdict(ok, reason)
    return ok


@click.command()
@click.option("--duration", "-d", default=120, show_default=True, help="Scenario duration in seconds.")
@click.option("--headed", is_flag=True, help="Show the browser windows.")
@click.option("--trigger-every", default=8.0, show_default=True, help="Approx seconds between Trigger clicks.")
@click.option("--queue-every", default=6.0, show_default=True, help="Approx seconds between pause/resume toggles.")
@click.option("--wander-every", default=3.0, show_default=True, help="Approx seconds between random navigations.")
def cli(duration, headed, trigger_every, queue_every, wander_every):
    """Run the 5-persona concurrent browser scenario."""
    cfg = cfg_mod.load()
    ok = run(cfg, duration, headed, trigger_every, queue_every, wander_every)
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    cli()
