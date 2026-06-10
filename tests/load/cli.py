"""
cli.py — Unified entry point for all HangfireDashboard testing tools.

Usage:
    python cli.py check-all              # Run all quick checks (API + DB)
    python cli.py db stats               # Database statistics
    python cli.py db jobs --state Failed
    python cli.py db queues
    python cli.py db recurring
    python cli.py db servers
    python cli.py db tags
    python cli.py db failures
    python cli.py db query "SELECT ..."
    python cli.py db cleanup --days 7
    python cli.py api health             # Health endpoints
    python cli.py api ping               # Quick connectivity check
    python cli.py api pages              # Check all dashboard pages
    python cli.py api static             # Check static assets
    python cli.py api bench --count 20   # Latency benchmark
    python cli.py jobs seed --count 20   # Seed test jobs
    python cli.py jobs stats             # Job distribution
    python cli.py jobs purge --state Failed
    python cli.py jobs trigger-recurring --all
    python cli.py signalr connect        # Connect to SignalR hub
    python cli.py signalr stress -c 5    # Stress test SignalR
    python cli.py monitor live           # Live terminal dashboard
    python cli.py monitor snapshot       # One-shot snapshot
    python cli.py monitor watch-failed   # Alert on new failures
    python cli.py e2e smoke              # Playwright smoke test
    python cli.py e2e all                # Full E2E suite
    python cli.py e2e screenshot --page /queues
"""
from __future__ import annotations

import sys

import click
from rich.console import Console
from rich.panel import Panel
from rich.table import Table
from rich import box

import config as cfg_mod

console = Console()

# Import sub-CLIs
import db as db_mod
import api as api_mod
import jobs as jobs_mod
import signalr as signalr_mod
import monitor as monitor_mod
import e2e as e2e_mod
import stress as stress_mod


@click.group()
@click.version_option("1.0.0", prog_name="HangfireDashboard Tools")
def main():
    """HangfireDashboard testing toolkit.

    Real-time testing tools for HangfireDashboard: database inspection,
    HTTP API checks, job seeding, SignalR testing, Playwright E2E, and live monitoring.
    """
    pass


# Register sub-command groups
main.add_command(db_mod.cli, name="db")
main.add_command(api_mod.cli, name="api")
main.add_command(jobs_mod.cli, name="jobs")
main.add_command(signalr_mod.cli, name="signalr")
main.add_command(monitor_mod.cli, name="monitor")
main.add_command(e2e_mod.cli, name="e2e")
main.add_command(stress_mod.cli, name="stress")


@main.command("check-all")
@click.option("--skip-db", is_flag=True, help="Skip database checks.")
@click.option("--skip-api", is_flag=True, help="Skip HTTP API checks.")
def check_all(skip_db: bool, skip_api: bool):
    """Run all quick checks: DB stats + API health + page availability."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    console.print(Panel(
        f"[bold cyan]HangfireDashboard — Full Check[/bold cyan]\n"
        f"Dashboard: {dashboard}\n"
        f"DB: {cfg_mod.get_db_provider(cfg)}",
        border_style="cyan",
    ))

    results: list[tuple[str, bool, str]] = []

    # ── API / Health ──
    if not skip_api:
        console.print("\n[bold]── HTTP Checks ──[/bold]")
        import httpx, time

        checks = [
            ("Ping dashboard",        dashboard,                       (200, 302, 303)),
            ("Health: liveness",      dashboard + "/healthz",          (200, 503)),
            ("Health: readiness",     dashboard + "/healthz/ready",    (200, 503)),
            ("Health: full",          dashboard + "/healthz/full",     (200, 503)),
            ("App health (/health)",  cfg_mod.get_base_url(cfg) + "/health", (200, 503)),
        ]

        with httpx.Client(timeout=10) as client:
            for label, url, expected in checks:
                try:
                    t0 = time.perf_counter()
                    resp = client.get(url, follow_redirects=True)
                    elapsed = (time.perf_counter() - t0) * 1000
                    ok = resp.status_code in expected
                    color = "green" if ok else "red"
                    icon = "✓" if ok else "✗"
                    console.print(f"  [{color}]{icon}[/{color}] {label:<30} HTTP {resp.status_code}  {elapsed:.0f}ms")
                    results.append((label, ok, f"HTTP {resp.status_code}"))
                except Exception as e:
                    console.print(f"  [red]✗[/red] {label:<30} ERROR: {e}")
                    results.append((label, False, str(e)[:60]))

    # ── DB ──
    if not skip_db:
        console.print("\n[bold]── Database Checks ──[/bold]")
        try:
            from db import get_connection, fetch_stats
            conn, provider = get_connection()
            stats = fetch_stats(conn, provider)
            conn.close()

            by_state = {row[0]: row[1] for row in stats["jobs_by_state"]}
            console.print(f"  [green]✓[/green] DB connected ({provider})")
            console.print(f"  [green]✓[/green] Total jobs: {stats['total_jobs']:,}")
            console.print(f"  [green]✓[/green] Active servers: {stats['active_servers']}")
            console.print(f"  [green]✓[/green] Recurring jobs: {stats['recurring_count']}")
            for state, cnt in sorted(by_state.items(), key=lambda x: -x[1]):
                color = {"Failed": "red", "Succeeded": "green"}.get(state, "dim")
                console.print(f"            [{color}]{state}[/{color}]: {cnt:,}")
            results.append(("DB connected", True, provider))
        except Exception as e:
            console.print(f"  [red]✗[/red] DB error: {e}")
            results.append(("DB connected", False, str(e)[:80]))

    # ── Summary ──
    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    color = "green" if passed == total else "red"
    console.print(f"\n[{color}]{'✓' if passed == total else '✗'} {passed}/{total} checks passed.[/{color}]")
    sys.exit(0 if passed == total else 1)


@main.command("info")
def info():
    """Show current configuration."""
    try:
        cfg = cfg_mod.load()
    except Exception as e:
        console.print(f"[red]Failed to load config: {e}[/red]")
        sys.exit(1)

    t = Table(title="Configuration", box=box.ROUNDED)
    t.add_column("Setting", style="cyan")
    t.add_column("Value")

    t.add_row("Base URL", cfg_mod.get_base_url(cfg))
    t.add_row("Dashboard URL", cfg_mod.get_dashboard_url(cfg))
    t.add_row("DB Provider", cfg_mod.get_db_provider(cfg))

    if cfg_mod.get_db_provider(cfg) == "postgresql":
        pg = cfg["database"]["postgresql"]
        t.add_row("PG Host", f"{pg['host']}:{pg['port']}")
        t.add_row("PG Database", pg["database"])
    elif cfg_mod.get_db_provider(cfg) == "sqlserver":
        t.add_row("SQL Server", cfg["database"]["sqlserver"]["connection_string"][:60] + "…")

    console.print(t)


if __name__ == "__main__":
    main()
