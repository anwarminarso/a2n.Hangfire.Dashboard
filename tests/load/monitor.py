"""
monitor.py — Real-time live dashboard monitor.

Polls the database + health endpoint at a configurable interval and renders a
Rich live dashboard showing job counts, queue depths, server status, and trends.

Usage:
    python monitor.py live                # Start live terminal dashboard
    python monitor.py live --interval 3  # Poll every 3 seconds
    python monitor.py snapshot           # One-shot status snapshot
    python monitor.py watch-failed       # Alert when new failed jobs appear
"""
from __future__ import annotations

import sys
import time
from collections import deque
from datetime import datetime, timezone

import click
import httpx
from rich import box
from rich.columns import Columns
from rich.console import Console
from rich.layout import Layout
from rich.live import Live
from rich.panel import Panel
from rich.table import Table
from rich.text import Text

import config as cfg_mod
from db import get_connection, fetch_stats, _schema_prefix

console = Console()


# ─── State snapshot ───────────────────────────────────────────────────────────

class Snapshot:
    def __init__(self, stats: dict, health: dict | None, ts: datetime):
        self.stats = stats
        self.health = health
        self.ts = ts

    @property
    def total_jobs(self) -> int:
        return self.stats.get("total_jobs", 0)

    def jobs_by_state(self) -> dict[str, int]:
        return {row[0]: row[1] for row in self.stats.get("jobs_by_state", [])}

    def queue_depths(self) -> list[tuple[str, int]]:
        return self.stats.get("queue_depths", [])

    @property
    def active_servers(self) -> int:
        return self.stats.get("active_servers", 0)

    @property
    def recurring_count(self) -> int:
        return self.stats.get("recurring_count", 0)


# ─── Health check fetch ───────────────────────────────────────────────────────

def fetch_health(dashboard_url: str, timeout: float = 5.0) -> dict | None:
    try:
        resp = httpx.get(dashboard_url + "/healthz", timeout=timeout, follow_redirects=True)
        return {"status_code": resp.status_code, "text": resp.text[:200]}
    except Exception as e:
        return {"error": str(e)}


# ─── Rich rendering ───────────────────────────────────────────────────────────

STATE_COLORS = {
    "Succeeded": "green",
    "Failed": "red",
    "Processing": "yellow",
    "Enqueued": "blue",
    "Scheduled": "magenta",
    "Awaiting": "cyan",
    "Deleted": "dim",
}


def render_stats_table(snap: Snapshot) -> Table:
    t = Table(title="Job Counts", box=box.ROUNDED, show_header=True, expand=True)
    t.add_column("State")
    t.add_column("Count", justify="right")
    t.add_column("Bar")

    by_state = snap.jobs_by_state()
    total = max(snap.total_jobs, 1)

    for state, cnt in sorted(by_state.items(), key=lambda x: -x[1]):
        color = STATE_COLORS.get(state, "white")
        bar_len = max(int(cnt / total * 20), 1 if cnt > 0 else 0)
        bar = "█" * bar_len
        t.add_row(f"[{color}]{state}[/{color}]", f"[{color}]{cnt:,}[/{color}]", f"[{color}]{bar}[/{color}]")

    t.add_row("[bold]Total[/bold]", f"[bold]{total:,}[/bold]", "")
    return t


def render_queues_table(snap: Snapshot) -> Table:
    t = Table(title="Queues", box=box.ROUNDED, expand=True)
    t.add_column("Queue")
    t.add_column("Depth", justify="right")
    for queue, depth in snap.queue_depths():
        color = "red" if depth > 100 else "yellow" if depth > 20 else "green"
        t.add_row(queue, f"[{color}]{depth:,}[/{color}]")
    if not snap.queue_depths():
        t.add_row("[dim]—[/dim]", "[dim]0[/dim]")
    return t


def render_info_panel(snap: Snapshot, prev: Snapshot | None, health: dict | None) -> Panel:
    ts_str = snap.ts.strftime("%H:%M:%S")
    lines = [f"[dim]Updated:[/dim] {ts_str}"]
    lines.append(f"[dim]Servers:[/dim] [{'green' if snap.active_servers > 0 else 'red'}]{snap.active_servers}[/]")
    lines.append(f"[dim]Recurring:[/dim] {snap.recurring_count}")

    if prev is not None:
        by_state = snap.jobs_by_state()
        prev_by_state = prev.jobs_by_state()
        delta_failed = by_state.get("Failed", 0) - prev_by_state.get("Failed", 0)
        delta_succ = by_state.get("Succeeded", 0) - prev_by_state.get("Succeeded", 0)
        if delta_failed > 0:
            lines.append(f"[red]▲ {delta_failed} new failed[/red]")
        if delta_succ > 0:
            lines.append(f"[green]▲ {delta_succ} succeeded[/green]")

    if health:
        if "error" in health:
            lines.append(f"[red]Health: {health['error'][:40]}[/red]")
        else:
            code = health.get("status_code")
            color = "green" if code == 200 else "yellow"
            lines.append(f"[{color}]Health: HTTP {code}[/{color}]")

    return Panel("\n".join(lines), title="[bold]Status[/bold]", border_style="cyan")


def render_trend_panel(history: deque) -> Panel:
    """Show ASCII trend of Failed count over recent snapshots."""
    if len(history) < 2:
        return Panel("[dim]Collecting data…[/dim]", title="[bold]Trend (Failed)[/bold]")

    failed_vals = [s.jobs_by_state().get("Failed", 0) for s in history]
    max_val = max(failed_vals) or 1
    height = 6
    width = len(failed_vals)

    lines = []
    for row in range(height, 0, -1):
        threshold = (row / height) * max_val
        line = ""
        for val in failed_vals:
            line += "█" if val >= threshold else " "
        lines.append(f"[red]{line}[/red]")
    lines.append("[dim]" + "─" * width + "[/dim]")
    lines.append(f"[dim]min:{min(failed_vals)} max:{max(failed_vals)} now:{failed_vals[-1]}[/dim]")

    return Panel("\n".join(lines), title="[bold]Trend (Failed)[/bold]")


def build_layout(snap: Snapshot, prev: Snapshot | None, history: deque, health: dict | None) -> Layout:
    layout = Layout()
    layout.split_column(
        Layout(name="top", size=3),
        Layout(name="main"),
        Layout(name="bottom", size=5),
    )

    layout["top"].update(Panel(
        f"[bold cyan]HangfireDashboard Live Monitor[/bold cyan]  |  "
        f"[dim]Press Ctrl+C to exit[/dim]",
        border_style="bright_blue",
    ))

    layout["main"].split_row(
        Layout(render_stats_table(snap), name="stats"),
        Layout(render_queues_table(snap), name="queues"),
        Layout(render_info_panel(snap, prev, health), name="info"),
    )

    layout["bottom"].update(render_trend_panel(history))
    return layout


# ─── CLI ──────────────────────────────────────────────────────────────────────

@click.group()
def cli():
    """Real-time live dashboard monitor."""
    pass


@cli.command()
@click.option("--interval", "-i", default=None, type=float, help="Poll interval in seconds.")
@click.option("--history", default=None, type=int, help="Number of history points to keep.")
@click.option("--no-health", is_flag=True, help="Skip health endpoint checks.")
def live(interval: float | None, history: int | None, no_health: bool):
    """Start a live terminal dashboard showing real-time job metrics."""
    cfg = cfg_mod.load()
    monitor_cfg = cfg.get("monitor", {})
    poll_interval = interval or float(monitor_cfg.get("poll_interval", 2))
    history_points = history or int(monitor_cfg.get("history_points", 60))
    dashboard_url = cfg_mod.get_dashboard_url(cfg)

    snap_history: deque[Snapshot] = deque(maxlen=history_points)
    prev_snap: Snapshot | None = None

    console.print(f"[cyan]Starting live monitor[/cyan] — {dashboard_url}  [dim](interval: {poll_interval}s)[/dim]")

    def poll():
        nonlocal prev_snap
        conn, provider = get_connection()
        try:
            stats = fetch_stats(conn, provider)
        finally:
            conn.close()

        health = None if no_health else fetch_health(dashboard_url)
        ts = datetime.now(timezone.utc)
        snap = Snapshot(stats, health, ts)
        snap_history.append(snap)
        prev = prev_snap
        prev_snap = snap
        return snap, prev

    try:
        with Live(console=console, refresh_per_second=4, screen=True) as live_display:
            while True:
                snap, prev = poll()
                live_display.update(build_layout(snap, prev, snap_history, snap.health))
                time.sleep(poll_interval)
    except KeyboardInterrupt:
        console.print("\n[yellow]Monitor stopped.[/yellow]")


@cli.command()
def snapshot():
    """One-shot status snapshot — print current state and exit."""
    cfg = cfg_mod.load()
    dashboard_url = cfg_mod.get_dashboard_url(cfg)

    conn, provider = get_connection()
    try:
        stats = fetch_stats(conn, provider)
    finally:
        conn.close()

    snap = Snapshot(stats, None, datetime.now(timezone.utc))

    console.print(render_stats_table(snap))
    console.print(render_queues_table(snap))
    console.print(f"\n[dim]Active servers:[/dim] {snap.active_servers}  "
                  f"[dim]Recurring:[/dim] {snap.recurring_count}  "
                  f"[dim]Total jobs:[/dim] {snap.total_jobs}")

    health = fetch_health(dashboard_url)
    if health:
        if "error" in health:
            console.print(f"[red]Health endpoint error:[/red] {health['error']}")
        else:
            color = "green" if health["status_code"] == 200 else "yellow"
            console.print(f"[{color}]Health: HTTP {health['status_code']}[/{color}]")


@cli.command("watch-failed")
@click.option("--interval", "-i", default=5.0, show_default=True)
@click.option("--threshold", "-t", default=1, show_default=True, help="Alert when new_failed >= this value.")
def watch_failed(interval: float, threshold: int):
    """Poll and alert when new failed jobs appear."""
    cfg = cfg_mod.load()

    prev_failed = None
    console.print(f"[cyan]Watching for failed jobs[/cyan] (threshold: {threshold}, interval: {interval}s)")
    console.print("[dim]Press Ctrl+C to stop.[/dim]\n")

    try:
        while True:
            conn, provider = get_connection()
            try:
                stats = fetch_stats(conn, provider)
            finally:
                conn.close()

            by_state = {row[0]: row[1] for row in stats.get("jobs_by_state", [])}
            current_failed = by_state.get("Failed", 0)
            ts = datetime.now().strftime("%H:%M:%S")

            if prev_failed is None:
                console.print(f"[dim]{ts}[/dim] Baseline: {current_failed} failed job(s)")
            else:
                delta = current_failed - prev_failed
                if delta >= threshold:
                    console.print(
                        f"[dim]{ts}[/dim] [bold red]⚠ ALERT: {delta} new failed job(s)![/bold red] "
                        f"Total: {current_failed}"
                    )
                else:
                    console.print(f"[dim]{ts}[/dim] Failed: {current_failed} (Δ {delta:+d})")

            prev_failed = current_failed
            time.sleep(interval)

    except KeyboardInterrupt:
        console.print("\n[yellow]Watch stopped.[/yellow]")


if __name__ == "__main__":
    cli()
