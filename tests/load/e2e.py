"""
e2e.py — Playwright E2E browser tests for HangfireDashboard.

Uses Playwright (Chromium) to drive a real browser and validate the UI.

Usage:
    python e2e.py open                       # Just open the dashboard in a browser
    python e2e.py smoke                      # Quick smoke test: load all pages
    python e2e.py test-home                  # Home page metrics cards
    python e2e.py test-jobs                  # Job list pages (enqueued, failed, succeeded)
    python e2e.py test-recurring             # Recurring jobs page
    python e2e.py test-search                # Search functionality
    python e2e.py test-theme                 # Light/dark theme toggle
    python e2e.py test-signalr               # Verify SignalR realtime updates are received
    python e2e.py screenshot --page /queues  # Take a screenshot of a page
    python e2e.py all                        # Run all tests

Options:
    --headed                                 # Show browser window (default: headless)
    --slow-mo N                              # Add N ms delay between actions (for visual debugging)
    --timeout N                              # Page load timeout in ms (default: 15000)
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

import click
from rich.console import Console
from rich.panel import Panel
from rich.table import Table
from rich import box

import config as cfg_mod

console = Console()

SCREENSHOTS_DIR = Path(__file__).parent / "screenshots"

# ─── Test result tracking ─────────────────────────────────────────────────────

class TestResults:
    def __init__(self):
        self.results: list[tuple[str, bool, str]] = []

    def add(self, name: str, passed: bool, message: str = ""):
        self.results.append((name, passed, message))
        icon = "[green]✓[/green]" if passed else "[red]✗[/red]"
        msg = f"  {message}" if message else ""
        console.print(f"  {icon} {name}{msg}")

    def summary(self):
        passed = sum(1 for _, p, _ in self.results if p)
        total = len(self.results)
        color = "green" if passed == total else "red"
        console.print(f"\n[{color}]{passed}/{total} tests passed.[/{color}]")
        if passed < total:
            for name, ok, msg in self.results:
                if not ok:
                    console.print(f"  [red]FAIL:[/red] {name} — {msg}")
        return passed == total


# ─── Playwright helpers ───────────────────────────────────────────────────────

def _make_browser(headed: bool, slow_mo: int):
    """Return (playwright, browser) — caller must close both."""
    from playwright.sync_api import sync_playwright
    p = sync_playwright().start()
    browser = p.chromium.launch(headless=not headed, slow_mo=slow_mo)
    return p, browser


def _new_page(browser, timeout: int):
    ctx = browser.new_context()
    page = ctx.new_page()
    page.set_default_timeout(timeout)
    return page


# ─── Individual test functions ────────────────────────────────────────────────

def test_home(page, dashboard_url: str, results: TestResults):
    page.goto(dashboard_url + "/")
    page.wait_for_load_state("networkidle")

    # Stat cards should be present
    results.add(
        "Home: stat cards rendered",
        page.locator(".stat-card, .card, [data-testid='stat']").count() > 0,
    )
    results.add(
        "Home: page title present",
        "hangfire" in page.title().lower() or "dashboard" in page.title().lower(),
    )
    # Check for the servers section
    results.add(
        "Home: page content loaded",
        page.locator("body").inner_text() != "",
    )


def test_jobs_pages(page, dashboard_url: str, results: TestResults):
    for route, label in [
        ("/jobs/enqueued", "Enqueued"),
        ("/jobs/failed", "Failed"),
        ("/jobs/succeeded", "Succeeded"),
        ("/jobs/processing", "Processing"),
        ("/jobs/scheduled", "Scheduled"),
        ("/jobs/deleted", "Deleted"),
    ]:
        try:
            resp = page.goto(dashboard_url + route, wait_until="domcontentloaded")
            ok = resp.status == 200
            results.add(f"Jobs/{label}: HTTP 200", ok)
        except Exception as e:
            results.add(f"Jobs/{label}: HTTP 200", False, str(e)[:60])


def test_recurring(page, dashboard_url: str, results: TestResults):
    page.goto(dashboard_url + "/recurring")
    page.wait_for_load_state("networkidle")

    results.add(
        "Recurring: page loaded",
        page.locator("body").inner_text() != "",
    )

    # Look for a table or job list
    has_table = page.locator("table, .job-list, [role='grid']").count() > 0
    results.add("Recurring: job list/table present", has_table)

    # Check for recurring job admin buttons if not read-only
    has_actions = page.locator(
        "button:has-text('Trigger'), button:has-text('Delete'), [data-action]"
    ).count() > 0
    results.add("Recurring: action buttons present", has_actions)


def test_search(page, dashboard_url: str, results: TestResults):
    page.goto(dashboard_url + "/search")
    page.wait_for_load_state("networkidle")

    results.add("Search: page loaded", page.locator("body").inner_text() != "")

    # Look for search input
    input_sel = "input[type='search'], input[type='text'], input[placeholder*='earch']"
    has_input = page.locator(input_sel).count() > 0
    results.add("Search: search input present", has_input)

    if has_input:
        page.locator(input_sel).first.fill("failing")
        page.keyboard.press("Enter")
        page.wait_for_timeout(2000)
        results.add(
            "Search: search executed without error",
            "error" not in page.locator("body").inner_text().lower()[:500],
        )


def test_theme_toggle(page, dashboard_url: str, results: TestResults):
    page.goto(dashboard_url + "/")
    page.wait_for_load_state("networkidle")

    # Look for theme toggle button
    toggle = page.locator(
        "button[title*='theme'], button[aria-label*='theme'], "
        "[data-bs-theme], .theme-toggle, #themeToggle"
    )
    has_toggle = toggle.count() > 0
    results.add("Theme: toggle button present", has_toggle)

    if has_toggle:
        # Get initial theme
        initial_theme = page.evaluate("document.documentElement.getAttribute('data-bs-theme')")
        # Click the toggle
        toggle.first.click()
        page.wait_for_timeout(500)
        new_theme = page.evaluate("document.documentElement.getAttribute('data-bs-theme')")
        results.add(
            "Theme: toggle changes theme",
            initial_theme != new_theme,
            f"{initial_theme} → {new_theme}",
        )


def test_signalr_connection(page, dashboard_url: str, results: TestResults):
    """Verify that Blazor loads and SignalR circuit is established."""
    page.goto(dashboard_url + "/")

    # Capture console messages to detect SignalR
    signalr_connected = []
    page.on("console", lambda msg: signalr_connected.append(msg.text) if "blazor" in msg.text.lower() or "signalr" in msg.text.lower() else None)

    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(3000)  # Wait for SignalR circuit to establish

    # Check that _blazor is loaded
    blazor_loaded = page.evaluate("typeof Blazor !== 'undefined'")
    results.add("SignalR: Blazor runtime loaded", blazor_loaded)

    # Check that websocket connections are made
    ws_count = page.evaluate("""() => {
        const perf = window.performance.getEntriesByType('resource');
        return perf.filter(e => e.name.includes('_blazor') || e.name.includes('hubs')).length;
    }""")
    results.add("SignalR: WebSocket resources requested", ws_count > 0, f"({ws_count} resource(s))")


def test_analytics(page, dashboard_url: str, results: TestResults):
    for route, label in [
        ("/analytics", "Overview"),
        ("/analytics/performance", "Performance"),
        ("/analytics/failures", "Failures"),
        ("/analytics/queues", "Queues"),
        ("/analytics/recurring", "Recurring"),
    ]:
        try:
            resp = page.goto(dashboard_url + route, wait_until="domcontentloaded")
            ok = resp.status == 200
            results.add(f"Analytics/{label}: HTTP 200", ok)
        except Exception as e:
            results.add(f"Analytics/{label}: HTTP 200", False, str(e)[:60])


def test_health_pages(page, dashboard_url: str, results: TestResults):
    for path, label in [
        ("/healthz", "liveness"),
        ("/healthz/ready", "readiness"),
        ("/healthz/full", "full"),
    ]:
        try:
            resp = page.goto(dashboard_url + path, wait_until="domcontentloaded")
            results.add(f"Health/{label}: responds", resp.status in (200, 503), f"HTTP {resp.status}")
        except Exception as e:
            results.add(f"Health/{label}: responds", False, str(e)[:60])


def smoke_test(page, dashboard_url: str, results: TestResults):
    """Quick: hit every main route and check HTTP 200."""
    routes = [
        "/", "/queues", "/jobs/enqueued", "/jobs/processing",
        "/jobs/scheduled", "/jobs/succeeded", "/jobs/failed",
        "/recurring", "/tags", "/search", "/servers", "/audit",
    ]
    for route in routes:
        try:
            resp = page.goto(dashboard_url + route, wait_until="domcontentloaded")
            results.add(f"Smoke {route}", resp.status == 200, f"HTTP {resp.status}")
        except Exception as e:
            results.add(f"Smoke {route}", False, str(e)[:60])


# ─── CLI ──────────────────────────────────────────────────────────────────────

COMMON_OPTIONS = [
    click.option("--headed", is_flag=True, help="Show browser window."),
    click.option("--slow-mo", default=0, show_default=True, help="Delay between actions (ms)."),
    click.option("--timeout", default=15000, show_default=True, help="Page load timeout (ms)."),
]


def add_common_options(func):
    for option in reversed(COMMON_OPTIONS):
        func = option(func)
    return func


@click.group()
def cli():
    """Playwright E2E browser tests for HangfireDashboard."""
    pass


@cli.command()
@add_common_options
def open(headed: bool, slow_mo: int, timeout: int):
    """Open the dashboard in a browser window (interactive)."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed=True, slow_mo=slow_mo)
    page = _new_page(browser, timeout)
    page.goto(dashboard)
    console.print(f"[green]Opened:[/green] {dashboard}")
    console.print("[dim]Press Enter to close…[/dim]")
    input()
    browser.close()
    p.stop()


@cli.command()
@add_common_options
def smoke(headed: bool, slow_mo: int, timeout: int):
    """Quick smoke test: visit all main routes and check HTTP 200."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Smoke Test[/cyan] — {dashboard}"))
        smoke_test(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-home")
@add_common_options
def test_home_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test the Home page: stat cards, title, content."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Home Page Tests[/cyan] — {dashboard}"))
        test_home(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-jobs")
@add_common_options
def test_jobs_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test all job list pages."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Job Pages Tests[/cyan] — {dashboard}"))
        test_jobs_pages(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-recurring")
@add_common_options
def test_recurring_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test the Recurring Jobs page."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Recurring Jobs Tests[/cyan] — {dashboard}"))
        test_recurring(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-search")
@add_common_options
def test_search_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test the Search page."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Search Tests[/cyan] — {dashboard}"))
        test_search(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-theme")
@add_common_options
def test_theme_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test light/dark theme toggle."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Theme Toggle Tests[/cyan] — {dashboard}"))
        test_theme_toggle(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-signalr")
@add_common_options
def test_signalr_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test SignalR / Blazor circuit establishment."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]SignalR Tests[/cyan] — {dashboard}"))
        test_signalr_connection(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command("test-analytics")
@add_common_options
def test_analytics_cmd(headed: bool, slow_mo: int, timeout: int):
    """Test analytics pages."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)
    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Analytics Tests[/cyan] — {dashboard}"))
        test_analytics(page, dashboard, results)
        ok = results.summary()
    finally:
        browser.close()
        p.stop()
    sys.exit(0 if ok else 1)


@cli.command()
@click.option("--page", "page_path", default="/", show_default=True, help="Dashboard route to screenshot.")
@click.option("--output", "-o", default=None, help="Output file path (default: screenshots/<route>.png).")
@add_common_options
def screenshot(page_path: str, output: str | None, headed: bool, slow_mo: int, timeout: int):
    """Take a screenshot of a dashboard page."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    SCREENSHOTS_DIR.mkdir(exist_ok=True)
    if output is None:
        safe_name = page_path.strip("/").replace("/", "-") or "home"
        output = str(SCREENSHOTS_DIR / f"{safe_name}.png")

    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        page.goto(dashboard + page_path)
        page.wait_for_load_state("networkidle")
        page.wait_for_timeout(1500)
        page.screenshot(path=output, full_page=True)
        console.print(f"[green]Screenshot saved:[/green] {output}")
    finally:
        browser.close()
        p.stop()


@cli.command("all")
@add_common_options
def all_tests(headed: bool, slow_mo: int, timeout: int):
    """Run all E2E tests."""
    cfg = cfg_mod.load()
    dashboard = cfg_mod.get_dashboard_url(cfg)

    p, browser = _make_browser(headed, slow_mo)
    try:
        page = _new_page(browser, timeout)
        results = TestResults()
        console.print(Panel(f"[cyan]Full E2E Test Suite[/cyan] — {dashboard}"))

        console.print("\n[bold]── Smoke ──[/bold]")
        smoke_test(page, dashboard, results)

        console.print("\n[bold]── Home ──[/bold]")
        test_home(page, dashboard, results)

        console.print("\n[bold]── Job Pages ──[/bold]")
        test_jobs_pages(page, dashboard, results)

        console.print("\n[bold]── Recurring ──[/bold]")
        test_recurring(page, dashboard, results)

        console.print("\n[bold]── Search ──[/bold]")
        test_search(page, dashboard, results)

        console.print("\n[bold]── Theme Toggle ──[/bold]")
        test_theme_toggle(page, dashboard, results)

        console.print("\n[bold]── Analytics ──[/bold]")
        test_analytics(page, dashboard, results)

        console.print("\n[bold]── Health Endpoints ──[/bold]")
        test_health_pages(page, dashboard, results)

        console.print("\n[bold]── SignalR / Blazor ──[/bold]")
        test_signalr_connection(page, dashboard, results)

        ok = results.summary()
    finally:
        browser.close()
        p.stop()

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    cli()
