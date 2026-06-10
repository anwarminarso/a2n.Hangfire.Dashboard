"""
api.py — HTTP API & health endpoint tester for HangfireDashboard.

Usage:
    python api.py health
    python api.py health --mode ready
    python api.py health --mode full
    python api.py ping
    python api.py pages          # Check all dashboard pages respond 200
    python api.py static         # Check embedded static assets
"""
from __future__ import annotations

import sys
import time
from dataclasses import dataclass
from typing import Literal

import click
import httpx
from rich import box
from rich.console import Console
from rich.panel import Panel
from rich.table import Table

import config as cfg_mod

console = Console()


# ─── Dashboard pages to check ─────────────────────────────────────────────────

DASHBOARD_ROUTES = [
    "/",
    "/queues",
    "/jobs/enqueued",
    "/jobs/processing",
    "/jobs/scheduled",
    "/jobs/succeeded",
    "/jobs/failed",
    "/jobs/awaiting",
    "/jobs/deleted",
    "/retries",
    "/recurring",
    "/tags",
    "/search",
    "/servers",
    "/audit",
    "/analytics",
    "/analytics/performance",
    "/analytics/failures",
    "/analytics/queues",
    "/analytics/recurring",
]

STATIC_ASSETS = [
    "/_content/a2n.Hangfire.Dashboard/css/app.css",
    "/_content/a2n.Hangfire.Dashboard/js/theme.js",
    "/_content/a2n.Hangfire.Dashboard/js/charts.js",
    "/_content/a2n.Hangfire.Dashboard/lib/bootstrap/bootstrap.bundle.min.js",
    "/_framework/blazor.web.js",
]


# ─── HTTP helpers ─────────────────────────────────────────────────────────────

@dataclass
class CheckResult:
    url: str
    status: int | None
    elapsed_ms: float
    ok: bool
    error: str | None = None
    body_preview: str | None = None


def check_url(
    client: httpx.Client,
    url: str,
    expected_statuses: tuple[int, ...] = (200,),
    show_body: bool = False,
) -> CheckResult:
    try:
        t0 = time.perf_counter()
        resp = client.get(url, follow_redirects=True)
        elapsed_ms = (time.perf_counter() - t0) * 1000
        ok = resp.status_code in expected_statuses
        body_preview = resp.text[:200] if show_body else None
        return CheckResult(url, resp.status_code, elapsed_ms, ok, body_preview=body_preview)
    except httpx.ConnectError as e:
        return CheckResult(url, None, 0, False, error=f"Connection refused: {e}")
    except httpx.TimeoutException as e:
        return CheckResult(url, None, 0, False, error=f"Timeout: {e}")
    except Exception as e:
        return CheckResult(url, None, 0, False, error=str(e))


def make_client(cfg: dict, timeout: float = 30.0) -> httpx.Client:
    base = cfg_mod.get_base_url(cfg)
    return httpx.Client(base_url=base, timeout=timeout)


# ─── CLI ──────────────────────────────────────────────────────────────────────

@click.group()
def cli():
    """HTTP API & endpoint tester for HangfireDashboard."""
    pass


@cli.command()
@click.option(
    "--mode",
    type=click.Choice(["liveness", "ready", "full"]),
    default="liveness",
    show_default=True,
    help="Health check mode.",
)
def health(mode: str):
    """Check the Hangfire health endpoints (/healthz, /healthz/ready, /healthz/full)."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    path_map = {
        "liveness": "/healthz",
        "ready": "/healthz/ready",
        "full": "/healthz/full",
    }
    url = dashboard + path_map[mode]

    console.print(f"\n[cyan]Checking:[/cyan] {url}")
    with make_client(cfg) as client:
        result = check_url(client, url, expected_statuses=(200, 503), show_body=True)

    if result.error:
        console.print(f"[red]ERROR:[/red] {result.error}")
        sys.exit(1)

    color = "green" if result.ok and result.status == 200 else "yellow" if result.status == 503 else "red"
    console.print(f"Status: [{color}]{result.status}[/{color}]  ({result.elapsed_ms:.0f} ms)")

    if result.body_preview:
        try:
            import json
            data = json.loads(result.body_preview)
            _print_health_json(data)
        except Exception:
            console.print(Panel(result.body_preview, title="Response"))


def _print_health_json(data: dict):
    """Pretty-print the health check JSON response."""
    overall = data.get("status", "unknown")
    color = {"Healthy": "green", "Degraded": "yellow", "Unhealthy": "red"}.get(overall, "white")
    console.print(f"\nOverall status: [{color}]{overall}[/{color}]")

    entries = data.get("entries", data.get("checks", {}))
    if isinstance(entries, dict):
        t = Table(box=box.ROUNDED)
        t.add_column("Check", style="cyan")
        t.add_column("Status")
        t.add_column("Description", max_width=60)
        for name, entry in entries.items():
            s = entry.get("status", "?")
            c = {"Healthy": "green", "Degraded": "yellow", "Unhealthy": "red"}.get(s, "white")
            t.add_row(name, f"[{c}]{s}[/{c}]", entry.get("description") or "—")
        console.print(t)


@cli.command()
def ping():
    """Quick connectivity check — GET /hangfire and report status."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    with make_client(cfg) as client:
        result = check_url(client, dashboard, expected_statuses=(200, 302, 303))

    if result.error:
        console.print(f"[red]Cannot reach {dashboard}[/red]\n{result.error}")
        sys.exit(1)

    color = "green" if result.ok else "red"
    console.print(f"[{color}]{'✓' if result.ok else '✗'}[/{color}] {dashboard}  "
                  f"[dim]HTTP {result.status}  {result.elapsed_ms:.0f} ms[/dim]")


@cli.command()
@click.option("--fail-fast", is_flag=True, help="Stop on first failure.")
def pages(fail_fast: bool):
    """Check all dashboard pages return HTTP 200."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    t = Table(title="Dashboard Pages", box=box.ROUNDED)
    t.add_column("Route", style="cyan")
    t.add_column("Status", justify="right")
    t.add_column("Latency", justify="right")
    t.add_column("Result")

    failures = 0
    with make_client(cfg, timeout=20) as client:
        for route in DASHBOARD_ROUTES:
            url = dashboard + route
            result = check_url(client, url, expected_statuses=(200,))
            status_str = str(result.status) if result.status else "ERR"
            color = "green" if result.ok else "red"
            result_str = f"[{color}]{'✓ OK' if result.ok else '✗ FAIL'}[/{color}]"
            if result.error:
                result_str = f"[red]✗ {result.error[:40]}[/red]"
                failures += 1
            elif not result.ok:
                failures += 1

            t.add_row(
                route,
                f"[{color}]{status_str}[/{color}]",
                f"{result.elapsed_ms:.0f} ms",
                result_str,
            )
            if fail_fast and failures > 0:
                break

    console.print(t)
    if failures:
        console.print(f"\n[red]{failures} page(s) failed.[/red]")
        sys.exit(1)
    else:
        console.print(f"\n[green]All {len(DASHBOARD_ROUTES)} pages OK.[/green]")


@cli.command()
def static():
    """Check embedded static assets are served correctly."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    t = Table(title="Static Assets", box=box.ROUNDED)
    t.add_column("Asset", style="cyan")
    t.add_column("Status", justify="right")
    t.add_column("Size", justify="right")
    t.add_column("Result")

    failures = 0
    with make_client(cfg, timeout=15) as client:
        for asset in STATIC_ASSETS:
            url = dashboard + asset
            try:
                t0 = time.perf_counter()
                resp = client.get(url, follow_redirects=True)
                elapsed = (time.perf_counter() - t0) * 1000
                ok = resp.status_code == 200
                size = len(resp.content)
                color = "green" if ok else "red"
                t.add_row(
                    asset,
                    f"[{color}]{resp.status_code}[/{color}]",
                    f"{size:,} B",
                    f"[{color}]{'✓' if ok else '✗'}[/{color}]",
                )
                if not ok:
                    failures += 1
            except Exception as e:
                t.add_row(asset, "ERR", "—", f"[red]{e}[/red]")
                failures += 1

    console.print(t)
    if failures:
        console.print(f"\n[red]{failures} asset(s) failed.[/red]")
        sys.exit(1)
    else:
        console.print(f"\n[green]All assets OK.[/green]")


@cli.command()
@click.option("--count", "-n", default=10, show_default=True)
@click.option("--interval", "-i", default=1.0, show_default=True, help="Seconds between requests.")
def bench(count: int, interval: float):
    """Simple latency benchmark — GET /hangfire N times."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    times = []
    console.print(f"Running {count} requests to [cyan]{dashboard}[/cyan] …\n")
    with make_client(cfg) as client:
        for i in range(count):
            t0 = time.perf_counter()
            try:
                resp = client.get(dashboard, follow_redirects=True)
                elapsed = (time.perf_counter() - t0) * 1000
                times.append(elapsed)
                color = "green" if resp.status_code == 200 else "yellow"
                console.print(f"  [{i+1:2d}] [{color}]{resp.status_code}[/{color}] {elapsed:6.1f} ms")
            except Exception as e:
                console.print(f"  [{i+1:2d}] [red]ERROR[/red] {e}")
            if interval > 0 and i < count - 1:
                time.sleep(interval)

    if times:
        import statistics
        console.print(f"\n[bold]Latency Summary[/bold]")
        console.print(f"  min:    {min(times):.1f} ms")
        console.print(f"  max:    {max(times):.1f} ms")
        console.print(f"  mean:   {statistics.mean(times):.1f} ms")
        console.print(f"  median: {statistics.median(times):.1f} ms")
        if len(times) >= 2:
            console.print(f"  stdev:  {statistics.stdev(times):.1f} ms")


if __name__ == "__main__":
    cli()
