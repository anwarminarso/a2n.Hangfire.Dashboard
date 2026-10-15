"""
release.py — the pre-release stress check. Manual and on demand only: never part of CI.

    python cli.py release-check --profile quick --provider pg --yes
    python cli.py release-check --profile full  --provider pg --yes --only S1,S2

It drives a running SampleApp (see README "Release check" for how to start it per provider), runs
the tiers below, prints each verdict as it goes and writes a markdown report to
tests/load/reports/ (git-ignored). Tiers that write to storage go through guard.ensure_writable.

    P0  preflight     dashboard and /healthz answer; for SQL storages, a server heartbeats in the DB
    R1  realtime      the Home page's data-snapshot-at changes every StatsPollingInterval in every tab,
                      the Analytics "Last 1h" view every AnalyticsBroadcastInterval, while N external
                      DashboardHub clients are subscribed
    R2  circuits      N Blazor circuits open, held, closed: working set and connections come back
    M2  pause         stress tier C (concurrent pause/resume on the queue:paused set)
    V1  volume        list pages and Search over a large seeded job table
    V2  grouping      the Failed page's exception groups with all, half and no stored fingerprints
    M1  group actions Requeue all (cancelled midway) and Delete all on a large group, workers running
    S1  slow storage  repeated table locks with 5 and then 20 circuits open and /healthz polled:
                      thread-pool threads stay bounded, nothing piles up, refresh recovers (#64)
    S2  storage down  the database refuses connections for a minute: no crash, circuits survive,
                      everything recovers
    E1  soak          stress tier E for the profile's duration (full profile)
    P1  package       dotnet pack, then a fresh app per target framework installs the package
"""
from __future__ import annotations

import asyncio
import json
import os
import platform
import re
import shutil
import statistics
import subprocess
import tempfile
import threading
import time
from contextlib import contextmanager
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

import click
import httpx
from rich.console import Console
from rich.panel import Panel
from rich.table import Table
from rich import box

import bulkseed
import config as cfg_mod
import guard
import stress
from srvmetrics import ServerMetricsSampler

console = Console()

TOOLS_DIR = Path(__file__).parent
REPO_ROOT = TOOLS_DIR.parent.parent
REPORTS_DIR = TOOLS_DIR / "reports"

PROVIDERS = {"pg": "postgresql", "sqlserver": "sqlserver", "inmemory": "inmemory"}

# Per-profile knobs. Durations in seconds.
PROFILES = {
    "quick": dict(r1_duration=60, r1_tabs=3, fanout=50, r2_circuits=20, r2_hold=30,
                  m2_workers=16, m2_duration=30, volume=100_000, failures=10_000, m1_jobs=10_000,
                  s1_phase=60, s2_outage=60, e1_duration=0, list_repeats=5),
    "full": dict(r1_duration=300, r1_tabs=5, fanout=50, r2_circuits=50, r2_hold=60,
                 m2_workers=16, m2_duration=60, volume=1_000_000, failures=50_000, m1_jobs=50_000,
                 s1_phase=180, s2_outage=60, e1_duration=3600, list_repeats=10),
}

ALL_TIERS = ["P0", "R1", "R2", "M2", "V1", "V2", "M1", "S1", "S2", "E1", "P1"]


# ─── results and report ──────────────────────────────────────────────────────

@dataclass
class TierResult:
    id: str
    title: str
    ok: bool | None            # None: skipped / not applicable
    summary: str
    rows: list[tuple[str, str]] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    seconds: float = 0.0
    console_text: str = ""

    @property
    def verdict(self) -> str:
        return "SKIP" if self.ok is None else ("PASS" if self.ok else "FAIL")


@dataclass
class Settings:
    """Dashboard options the thresholds depend on. Defaults are the dashboard's own."""
    stats_interval_ms: int = 2000
    analytics_interval_ms: int = 5000
    search_timeout_s: int = 30
    max_storage_calls: int = 8
    max_backoff_s: int = 60
    storage_probe_timeout_ms: int = 10000
    list_p95_ms: int = 2000

    @classmethod
    def from_cfg(cls, cfg: dict) -> "Settings":
        section = cfg.get("release", {})
        known = {k: v for k, v in section.items() if k in cls.__dataclass_fields__}
        return cls(**known)


class Context:
    def __init__(self, cfg: dict, provider_key: str, profile: str, assume_yes: bool,
                 reuse_data: bool, app_log: str | None):
        self.cfg = cfg
        self.provider_key = provider_key
        self.provider = PROVIDERS[provider_key]
        self.cfg["database"]["provider"] = self.provider
        # The legacy tiers and db.get_connection() reload the config themselves; make them agree.
        os.environ["HFLOAD_DB_PROVIDER"] = self.provider
        self.profile_name = profile
        self.p = dict(PROFILES[profile])
        self.scale = 1.0
        self.assume_yes = assume_yes
        self.reuse_data = reuse_data
        self.app_log = app_log
        self.settings = Settings.from_cfg(cfg)
        self.dashboard = cfg_mod.get_dashboard_url(cfg)
        self.base = cfg_mod.get_base_url(cfg)
        self.sampler = ServerMetricsSampler.autodetect(process_name=cfg.get("release", {}).get("process_name", "SampleApp"))
        self._writable_checked = False

    @property
    def sql(self) -> bool:
        return self.provider in ("postgresql", "sqlserver")

    @property
    def has_analytics(self) -> bool:
        return self.sql

    def ensure_writable(self, action: str):
        if self.sql:
            guard.ensure_writable(self.cfg, action, self.assume_yes or self._writable_checked)
            self._writable_checked = True
        elif not (self.assume_yes or self._writable_checked):
            click.confirm(f"{action[:1].upper()}{action[1:]} writes to the running app's InMemory storage. Continue?", abort=True)
            self._writable_checked = True

    def connect(self):
        from db import get_connection
        return get_connection(self.provider)


def _pct(values: list[float], p: float) -> float | None:
    return stress._pct(values, p)


def _int(text: str) -> int:
    """An integer the dashboard formatted in its culture ('1,000', '1.000', '1 000')."""
    return int(re.sub(r"\D", "", text) or 0)


def _fmt_ms(v: float | None) -> str:
    return "—" if v is None else f"{v:,.0f} ms"


@contextmanager
def _captured(*modules):
    """Record what legacy tiers print, so the report can include it."""
    from rich.console import Console as RichConsole
    saved = [(m, m.console) for m in modules]
    recorder = RichConsole(record=True, width=110)
    for m in modules:
        m.console = recorder
    try:
        yield recorder
    finally:
        for m, original in saved:
            m.console = original


def _metrics_window(session) -> dict:
    return session.summarize() if session is not None else {}


def _samples(stats: dict, key: str) -> list[float]:
    cs = stats.get(key)
    return list(cs.samples) if cs else []


def _window(samples: list[float], start_s: float, end_s: float) -> list[float]:
    """Samples between two offsets (seconds since the session's first sample, 1 sample/s)."""
    a = max(0, int(start_s))
    b = max(a + 1, int(end_s))
    return samples[a:b]


# ─── browser helpers ─────────────────────────────────────────────────────────

class Browser:
    """One Playwright Chromium for the run; each page() is its own context = its own circuit."""

    def __init__(self, headed: bool = False):
        from playwright.sync_api import sync_playwright
        self._pw = sync_playwright().start()
        self._browser = self._pw.chromium.launch(headless=not headed)
        self._contexts = []

    def page(self, url: str | None = None, timeout_ms: int = 30000):
        ctx = self._browser.new_context()
        self._contexts.append(ctx)
        page = ctx.new_page()
        page.set_default_timeout(timeout_ms)
        if url:
            page.goto(url, wait_until="domcontentloaded")
        return page

    def page_nowait(self, url: str, timeout_ms: int = 90000):
        """A page that starts loading `url` without waiting for it (it may be stuck behind a lock)."""
        page = self.page(timeout_ms=timeout_ms)
        page.evaluate("u => { window.location.href = u; }", url)
        return page

    def close_pages(self):
        # Navigate away first, as closing a real tab does: the page's unload sends Blazor's
        # disconnect beacon and the circuit is disposed at once. Closing the context outright skips
        # unload, so the server keeps each circuit as "disconnected" for
        # DisconnectedCircuitRetentionPeriod (3 min) in case the browser comes back.
        for ctx in self._contexts:
            for page in list(ctx.pages):
                try:
                    page.goto("about:blank", timeout=5000)
                except Exception:
                    pass
        for ctx in self._contexts:
            try:
                ctx.close()
            except Exception:
                pass
        self._contexts.clear()

    def close(self):
        self.close_pages()
        try:
            self._browser.close()
        finally:
            self._pw.stop()


def _wait_interactive(page, timeout_ms: int = 30000):
    """Blazor has started and the circuit is connected (the page answers events)."""
    page.wait_for_function("() => window.Blazor !== undefined", timeout=timeout_ms)
    page.wait_for_timeout(500)


def _marker(page, selector: str, attr: str) -> str | None:
    try:
        return page.locator(selector).first.get_attribute(attr, timeout=2000)
    except Exception:
        return None


def _sample_markers(pages, selectors: list[tuple[str, str]], duration_s: float,
                    interval_s: float = 0.2) -> list[list[float]]:
    """Gaps (ms) between changes of each page's (selector, attribute). The first value only sets the start."""
    last = [None] * len(pages)
    last_t = [None] * len(pages)
    gaps: list[list[float]] = [[] for _ in pages]
    end = time.monotonic() + duration_s
    while time.monotonic() < end:
        for i, page in enumerate(pages):
            selector, attr = selectors[i]
            value = _marker(page, selector, attr)
            now = time.monotonic()
            if value is None or value == "0":
                continue
            if value != last[i]:
                if last[i] is not None:
                    gaps[i].append((now - last_t[i]) * 1000)
                last[i], last_t[i] = value, now
        time.sleep(interval_s)
    return gaps


def _wait_marker_change(page, selector: str, attr: str, timeout_s: float) -> float | None:
    """Seconds until the attribute changes from its current value, or None."""
    start_value = _marker(page, selector, attr)
    started = time.monotonic()
    while time.monotonic() - started < timeout_s:
        value = _marker(page, selector, attr)
        if value is not None and value != start_value and value != "0":
            return time.monotonic() - started
        time.sleep(0.25)
    return None


# ─── P0 preflight ─────────────────────────────────────────────────────────────

def _wait_for_dashboard(url: str, timeout_s: float = 120) -> float | None:
    """Seconds until the dashboard answers (an app started just before the run may still be
    starting), or None if it never does."""
    started = time.monotonic()
    while time.monotonic() - started < timeout_s:
        try:
            if httpx.get(url, timeout=10, follow_redirects=True).status_code < 500:
                return time.monotonic() - started
        except Exception:
            pass
        time.sleep(2)
    return None


def tier_p0(ctx: Context) -> TierResult:
    rows, notes, ok = [], [], True
    waited = _wait_for_dashboard(ctx.dashboard)
    if waited is not None and waited > 5:
        notes.append(f"The dashboard answered after {waited:.0f} s (the app was still starting).")
    # Sampling attaches to the SampleApp process found at startup; find it again now it's up.
    if not ctx.sampler.available:
        ctx.sampler = ServerMetricsSampler.autodetect(process_name=ctx.cfg.get("release", {}).get("process_name", "SampleApp"))
    with httpx.Client(timeout=30) as client:
        for label, url in (("dashboard", ctx.dashboard), ("/healthz", ctx.dashboard + "/healthz"),
                           ("/healthz/full", ctx.dashboard + "/healthz/full"),
                           ("blazor.web.js", ctx.dashboard + "/_framework/blazor.web.js")):
            try:
                r = client.get(url, follow_redirects=True)
                rows.append((label, f"HTTP {r.status_code}"))
                ok &= r.status_code in (200, 503) if "healthz" in url else r.status_code == 200
                if label == "blazor.web.js" and r.status_code != 200:
                    notes.append("blazor.web.js isn't served, so no page goes interactive: start the "
                                 "unpublished SampleApp with --environment Development.")
            except Exception as e:
                rows.append((label, f"error: {e}"))
                ok = False
    if not ctx.sql:
        # No database to look a heartbeat up in: the seed endpoints prove the app is the
        # InMemory SampleApp started for this run, not another one left on the port.
        try:
            r = httpx.get(f"{ctx.base}/hfload/pending", timeout=10)
            rows.append(("/hfload seed endpoints", f"HTTP {r.status_code}"))
            if r.status_code != 200:
                ok = False
                notes.append("The app on this port has no /hfload endpoints: it isn't the InMemory SampleApp "
                             "started with --HfLoad:SeedEndpoints=true (is another app still on the port?).")
        except Exception as e:
            ok = False
            rows.append(("/hfload seed endpoints", f"error: {e}"))
    if ctx.sql:
        try:
            from db import fetch_stats
            conn, provider = ctx.connect()
            stats = fetch_stats(conn, provider)
            conn.close()
            rows.append(("servers heartbeating in the DB", str(stats["active_servers"])))
            rows.append(("jobs in the DB", f"{stats['total_jobs']:,}"))
            if stats["active_servers"] == 0:
                ok = False
                notes.append("No Hangfire server heartbeats in the configured database: the app is "
                             "probably running on another storage than --provider.")
        except SystemExit:
            ok = False
            notes.append("Could not connect to the configured database.")
    rows.append(("dotnet-counters", "attached to PID %s" % ctx.sampler.pid if ctx.sampler.available
                 else "unavailable (server-side metrics missing; S1 can't be judged)"))
    return TierResult("P0", "Preflight", ok, "dashboard reachable" if ok else "preflight failed", rows, notes)


# ─── R1 realtime ──────────────────────────────────────────────────────────────

def tier_r1(ctx: Context, browser: Browser) -> TierResult:
    p, s = ctx.p, ctx.settings
    duration = p["r1_duration"]
    rows, notes = [], []

    homes = [browser.page(ctx.dashboard + "/") for _ in range(p["r1_tabs"])]
    for page in homes:
        _wait_interactive(page)
    analytics = None
    if ctx.has_analytics:
        analytics = browser.page(ctx.dashboard + "/analytics")
        _wait_interactive(analytics)
        analytics.locator("[aria-label='Time range presets'] button", has_text="1h").first.click()

    fanout_results: list = []
    hub = stress._hub_ws_url(ctx.cfg)

    def run_fanout():
        try:
            fanout_results.extend(asyncio.run(stress._run_fanout(hub, p["fanout"], ["metrics", "analytics"],
                                                                 float(duration), ramp=5.0)))
        except Exception as e:
            notes.append(f"fan-out thread: {e}")

    fan = threading.Thread(target=run_fanout, daemon=True)
    fan.start()

    # Playwright's sync API is bound to this thread, so every page is sampled here, interleaved.
    pages = homes + ([analytics] if analytics is not None else [])
    selectors = [("[data-testid=metrics-snapshot]", "data-snapshot-at")] * len(homes)
    if analytics is not None:
        selectors.append(("[data-testid=analytics-update]", "data-received-at"))
    gaps = _sample_markers(pages, selectors, duration)
    fan.join(timeout=duration + 30)
    browser.close_pages()

    home_gaps = [g for i in range(len(homes)) for g in gaps[i]]
    analytics_gaps = gaps[len(homes)] if analytics is not None else []
    connected = sum(1 for r in fanout_results if r.connected)

    home_limit = 1.25 * s.stats_interval_ms
    home_p95 = _pct(home_gaps, 95)
    ok = bool(home_gaps) and home_p95 is not None and home_p95 <= home_limit
    tabs_without_updates = sum(1 for i in range(len(homes)) if not gaps[i])
    ok &= tabs_without_updates == 0
    rows += [
        ("Home tabs", str(len(homes))),
        ("Home updates seen", f"{len(home_gaps):,}"),
        ("Home gap mean / p95 / max", f"{_fmt_ms(statistics.mean(home_gaps) if home_gaps else None)} / "
                                      f"{_fmt_ms(home_p95)} / {_fmt_ms(max(home_gaps) if home_gaps else None)}"),
        ("Home p95 limit (1.25 × StatsPollingInterval)", _fmt_ms(home_limit)),
    ]
    if tabs_without_updates:
        notes.append(f"{tabs_without_updates} Home tab(s) never received an update.")
    if analytics is not None:
        a_p95 = _pct(analytics_gaps, 95)
        a_limit = 6500 if s.analytics_interval_ms == 5000 else 1.3 * s.analytics_interval_ms
        a_ok = bool(analytics_gaps) and a_p95 is not None and a_p95 <= a_limit
        ok &= a_ok
        rows += [("Analytics (Last 1h) updates seen", f"{len(analytics_gaps):,}"),
                 ("Analytics gap p95 / limit", f"{_fmt_ms(a_p95)} / {_fmt_ms(a_limit)}")]
    rows.append(("External hub clients connected", f"{connected}/{p['fanout']}"))
    ok &= connected == p["fanout"]
    summary = (f"Home p95 {home_p95:,.0f} ms over {len(home_gaps)} updates; {connected}/{p['fanout']} hub clients"
               if home_p95 is not None else "no realtime updates reached the Home page")
    return TierResult("R1", "Realtime cadence (DOM)", ok, summary, rows, notes)


# ─── R2 circuits ──────────────────────────────────────────────────────────────

def _find_tool(name: str) -> str | None:
    exe = shutil.which(name)
    if exe:
        return exe
    candidate = Path.home() / ".dotnet" / "tools" / (f"{name}.exe" if os.name == "nt" else name)
    return str(candidate) if candidate.exists() else None


@dataclass
class _HeapCensus:
    heap_bytes: int = 0
    circuits: int = 0          # live CircuitHost objects
    pages: int = 0             # live dashboard page components (a2n.Hangfire.Dashboard.Components.Pages.*)
    path: Path | None = None


def _gcdump(ctx: Context, label: str) -> _HeapCensus | None:
    """With dotnet-gcdump installed, a heap census after a full GC (the collection induces one)."""
    exe = _find_tool("dotnet-gcdump")
    if not exe or not ctx.sampler.pid:
        return None
    REPORTS_DIR.mkdir(exist_ok=True)
    path = REPORTS_DIR / f"heap-{ctx.provider_key}-{label}-{datetime.now():%Y%m%d-%H%M%S}.gcdump"
    try:
        subprocess.run([exe, "collect", "-p", str(ctx.sampler.pid), "-o", str(path)],
                       capture_output=True, text=True, timeout=180)
        report = subprocess.run([exe, "report", str(path)], capture_output=True, text=True, timeout=180).stdout
    except Exception:
        return None
    census = _HeapCensus(path=path)
    # Lines: "<bytes>  <count>  <type>  [<assembly>]", the first one "<bytes>  GC Heap bytes".
    for line in report.splitlines():
        m = re.match(r"\s*([\d,]+)\s+([\d,]+)\s+(\S.*?)(?:\s+\[[^\]]+\])?\s*$", line)
        if not m:
            m_total = re.match(r"\s*([\d,]+)\s+GC Heap bytes", line)
            if m_total:
                census.heap_bytes = _int(m_total.group(1))
            continue
        count, type_name = _int(m.group(2)), m.group(3).split(" (Bytes")[0].strip()
        if type_name == "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost":
            census.circuits += count
        elif type_name.startswith("a2n.Hangfire.Dashboard.Components.Pages.") and "<" not in type_name:
            census.pages += count
    path.with_suffix(".txt").write_text(report, encoding="utf-8")
    return census


def tier_r2(ctx: Context, browser: Browser) -> TierResult:
    """Two identical rounds of N circuits opened, held and closed. What the first round leaves
    behind is warm-up (JIT, caches, pools, and on InMemory the storage's own growth); a per-circuit
    leak leaves the same again after the second. So the verdict is the growth from the end of
    round 1 to the end of round 2; the first round's residual is reported as information."""
    p = ctx.p
    n, hold = p["r2_circuits"], p["r2_hold"]
    settle = 30
    window = 15 + 2 * (n * 0.3 + hold + settle) + 15
    rows, notes = [], []
    opened = 0
    marks: list[float] = []   # end of each settle, seconds since t0
    with ctx.sampler.session(window) as session:
        t0 = time.monotonic()
        time.sleep(10)
        marks.append(time.monotonic() - t0)
        for _ in range(2):
            for _ in range(n):
                try:
                    browser.page(ctx.dashboard + "/")
                    opened += 1
                except Exception as e:
                    notes.append(f"open failed: {str(e)[:80]}")
                time.sleep(0.3)
            time.sleep(hold)
            browser.close_pages()
            time.sleep(settle)
            marks.append(time.monotonic() - t0)
    # The direct leak check: after a full GC, no circuit and no dashboard page may outlive the
    # closed tabs. The working set alone can't tell a leak from storage growth (InMemory keeps
    # every job in the heap, and SampleApp's own jobs keep adding to it).
    census = _gcdump(ctx, "r2-after")
    stats = _metrics_window(session)
    ws, conns = _samples(stats, "working_set_mb"), _samples(stats, "connections")
    gc = _samples(stats, "gc_heap_mb")
    ok = opened == 2 * n
    rows.append(("Circuits opened (2 rounds)", f"{opened}/{2 * n}"))

    def at(samples: list[float], mark: float) -> float | None:
        w = _window(samples, mark - 5, mark)
        return statistics.mean(w) if w else None

    if ws and len(marks) == 3:
        start, after1, after2 = (at(ws, m) for m in marks)
        if None in (start, after1, after2):
            ok = False
            notes.append(f"dotnet-counters stopped early ({len(ws)} samples): the rounds weren't covered.")
        else:
            growth = (after2 - after1) / after1 * 100 if after1 else 0
            judged_by_heap = census is not None
            rows += [("Working set start / after round 1 / after round 2 / peak",
                      f"{start:,.0f} / {after1:,.0f} / {after2:,.0f} / {max(ws):,.0f} MB"),
                     ("Round 1 residual (warm-up, information)", f"{(after1 - start) / start * 100:+.1f} %"),
                     ("Growth from round 1 to round 2" + (" (information)" if judged_by_heap else " (limit +10 %)"),
                      f"{growth:+.1f} %")]
            if not judged_by_heap:
                ok &= growth <= 10
            if gc:
                g1, g2 = at(gc, marks[1]), at(gc, marks[2])
                if g1 is not None and g2 is not None:
                    rows.append(("GC heap after round 1 / round 2 (information)", f"{g1:,.0f} / {g2:,.0f} MB"))
    else:
        # Without the working set this tier has no verdict to give.
        ok = False
        why = session.failure if session is not None else "dotnet-counters isn't attached (see P0)"
        notes.append(f"No server metrics, so the working-set check wasn't made: {why}.")
    if conns:
        base_c = min(_window(conns, marks[0] - 5, marks[0]) or conns[:3])
        rows.append(("Connections baseline / peak / after close",
                     f"{base_c:,.0f} / {max(conns):,.0f} / {conns[-1]:,.0f}"))
        ok &= conns[-1] <= base_c + 2
    if census is not None:
        rows.append((f"After a full GC: live circuits / dashboard pages (limit 0 / 0) — {census.path.name}",
                     f"{census.circuits} / {census.pages}; heap {census.heap_bytes / 1048576:,.0f} MB"))
        ok &= census.circuits == 0 and census.pages == 0
    else:
        notes.append("dotnet-gcdump isn't installed, so the working set stands in for the leak check "
                     "and storage growth can fail it: `dotnet tool install --global dotnet-gcdump`.")
    if session:
        session.cleanup()
    return TierResult("R2", "Circuits open and close", ok,
                      "working set and connections came back" if ok else "circuits didn't recover", rows, notes)


# ─── M2 pause (legacy tier C) ─────────────────────────────────────────────────

def tier_m2(ctx: Context) -> TierResult:
    if not ctx.sql:
        return TierResult("M2", "Pause / resume contention", None, "SQL storages only")
    ctx.ensure_writable("pause and resume queues")
    with _captured(stress) as rec:
        ok = stress.tier_pause(ctx.cfg, ctx.p["m2_workers"], ctx.p["m2_duration"],
                               ["default", "critical", "emails", "reports"])
    return TierResult("M2", "Pause / resume contention", ok,
                      "no errors, no torn state" if ok else "errors or torn queue:paused state",
                      console_text=rec.export_text())


# ─── seeding (shared by V1, V2, M1) ───────────────────────────────────────────

def _app_post(ctx: Context, path: str, params: dict, timeout: float = 600) -> dict:
    r = httpx.post(f"{ctx.base}/hfload/{path}", params=params, timeout=timeout)
    if r.status_code == 404:
        raise click.ClickException("The app has no /hfload endpoints: start SampleApp with --HfLoad:SeedEndpoints=true.")
    r.raise_for_status()
    return r.json()


def _app_pending(ctx: Context) -> int:
    return int(httpx.get(f"{ctx.base}/hfload/pending", timeout=60).json()["pending"])


def _seed_volume(ctx: Context, total: int) -> str:
    ctx.ensure_writable(f"seed {total:,} jobs")
    started = time.monotonic()
    if ctx.sql:
        conn, provider = ctx.connect()
        try:
            written = bulkseed.seed_volume(conn, provider, total)
        finally:
            conn.close()
    else:
        written = {}
        for _ in range(0, total, 20_000):
            for state, count in _app_post(ctx, "volume", {"count": min(20_000, total)}).items():
                written[state] = written.get(state, 0) + count
    return f"{sum(written.values()):,} jobs in {time.monotonic() - started:.0f}s (" + \
        ", ".join(f"{k} {v:,}" for k, v in written.items()) + ")"


def _seed_failures(ctx: Context, count: int, fixed_kind: int | None = None) -> tuple[bool, float]:
    ctx.ensure_writable(f"enqueue {count:,} failing jobs")
    if ctx.sql:
        conn, provider = ctx.connect()
        try:
            bulkseed.enqueue_failures(conn, provider, count, fixed_kind=fixed_kind)
            return bulkseed.wait_until_processed(conn, provider, timeout_s=3600)
        finally:
            conn.close()
    _app_post(ctx, "failures", {"count": count, **({"kind": fixed_kind} if fixed_kind is not None else {})})
    started = time.monotonic()
    while time.monotonic() - started < 3600:
        if _app_pending(ctx) == 0:
            return True, time.monotonic() - started
        time.sleep(5)
    return False, time.monotonic() - started


# ─── V1 volume ────────────────────────────────────────────────────────────────

_LIST_ROUTES = ["/jobs/succeeded", "/jobs/failed", "/jobs/deleted", "/jobs/scheduled",
                "/jobs/enqueued/hfload-idle", "/retries", "/recurring", "/"]


def _time_to_rows(page, url: str, timeout_ms: int = 60000) -> float:
    started = time.perf_counter()
    page.goto(url, wait_until="commit")
    page.locator("tbody tr, .card.text-center, [data-testid=metrics-snapshot]").first.wait_for(
        state="attached", timeout=timeout_ms)
    return (time.perf_counter() - started) * 1000


def _search(page, ctx: Context, query: str) -> tuple[float, str]:
    """Run a search from the Search page; returns (ms, outcome text)."""
    page.goto(ctx.dashboard + "/search", wait_until="domcontentloaded")
    _wait_interactive(page)
    box_ = page.locator("[aria-label='Search jobs']")
    box_.fill(query)
    started = time.perf_counter()
    page.locator("button", has_text="Search Jobs").first.click()
    return _search_outcome(page, ctx, started)


def _search_outcome(page, ctx: Context, started: float) -> tuple[float, str]:
    timeout = (ctx.settings.search_timeout_s + 30) * 1000
    outcome = page.locator("text=/result(s)? found|Search Timed Out|Search Error|No results found/").first
    # A search opened from a URL is prerendered and then run again once the circuit is
    # interactive, so the first outcome can disappear while the second search runs. Read it
    # only once the page is interactive and the outcome is there again.
    outcome.wait_for(timeout=timeout)
    try:
        _wait_interactive(page, timeout_ms=30000)
    except Exception:
        pass
    outcome.wait_for(timeout=timeout)
    ms = (time.perf_counter() - started) * 1000
    if page.locator("text=Search Timed Out").count():
        return ms, "timed out"
    if page.locator("text=Search Error").count():
        return ms, "error"
    if page.locator("text=No results found.").count():
        return ms, "0 found"
    # The count is formatted in the server's culture (1,000 or 1.000); normalize to 1,000.
    strong = page.locator("strong").filter(has_text=re.compile(r"^[\d.,\u00a0 ]+\+?$")).first
    try:
        strong.wait_for(timeout=10000)
    except Exception:
        return ms, "? found"
    raw = strong.inner_text().strip()
    digits = re.sub(r"\D", "", raw)
    return ms, (f"{int(digits):,}" if digits else "?") + ("+" if raw.endswith("+") else "") + " found"


def tier_v1(ctx: Context, browser: Browser) -> TierResult:
    rows, notes = [], []
    total = ctx.p["volume"]
    if not ctx.reuse_data:
        rows.append(("Seeded", _seed_volume(ctx, total)))
    page = browser.page()
    timings: dict[str, list[float]] = {}
    for route in _LIST_ROUTES:
        for _ in range(ctx.p["list_repeats"]):
            try:
                timings.setdefault(route, []).append(_time_to_rows(page, ctx.dashboard + route))
            except Exception as e:
                notes.append(f"{route}: {str(e)[:80]}")
    all_ms = [v for vs in timings.values() for v in vs]
    p95_all = _pct(all_ms, 95)
    ok = p95_all is not None and p95_all <= ctx.settings.list_p95_ms and not notes
    for route, vs in timings.items():
        rows.append((f"list {route} p50 / p95", f"{_fmt_ms(_pct(vs, 50))} / {_fmt_ms(_pct(vs, 95))}"))
    rows.append((f"all list pages p95 (limit {ctx.settings.list_p95_ms:,} ms)", _fmt_ms(p95_all)))

    limit_ms = ctx.settings.search_timeout_s * 1000 + 5000
    for query in ("StressJobs", "args:4242", "zz-no-such-job-zz"):
        try:
            ms, outcome = _search(page, ctx, query)
            rows.append((f"search '{query}'", f"{_fmt_ms(ms)} → {outcome}"))
            ok &= ms <= limit_ms and outcome != "error"
            if query == "StressJobs" and outcome != "timed out":
                count = outcome.split(" ")[0]
                n_found = int(re.sub(r"\D", "", count) or 0)
                if not count.endswith("+") and n_found < min(total, 1000):
                    notes.append(f"'StressJobs' should match at least {min(total, 1000):,} jobs, got {outcome}.")
                    ok = False
        except Exception as e:
            ok = False
            notes.append(f"search '{query}': {str(e)[:100]}")
    browser.close_pages()
    return TierResult("V1", "Volume: list pages and Search", ok,
                      f"list p95 {_fmt_ms(p95_all)}", rows, notes)


# ─── V2 grouping ──────────────────────────────────────────────────────────────

def _open_groups(page, ctx: Context) -> tuple[float, int, str]:
    """Open the Failed page's exception filter; returns (ms, labeled groups, status text)."""
    page.goto(ctx.dashboard + "/jobs/failed", wait_until="domcontentloaded")
    _wait_interactive(page)
    started = time.perf_counter()
    page.locator("#exception-filter").click()
    timeout = (ctx.settings.search_timeout_s + 30) * 1000
    page.locator(".hf-exception-option, .hf-exception-menu .text-warning-emphasis, .hf-exception-menu .text-danger"
                 ).first.wait_for(timeout=timeout)
    ms = (time.perf_counter() - started) * 1000
    groups = page.locator(".hf-exception-option").count()
    status = page.locator(".hf-exception-menu .border-top").all_inner_texts()
    return ms, groups, " ".join(t.strip() for t in status)[:160]


def tier_v2(ctx: Context, browser: Browser) -> TierResult:
    rows, notes = [], []
    if not ctx.reuse_data:
        done, seconds = _seed_failures(ctx, ctx.p["failures"])
        rows.append(("Real failures seeded", f"{ctx.p['failures']:,} in {seconds:.0f}s" + ("" if done else " (NOT all processed)")))
        if not done:
            notes.append("The server didn't process every seeded failure in time.")
    page = browser.page()
    limit_ms = ctx.settings.search_timeout_s * 1000
    ok = not notes
    # Search's Exception filter (?exception=<fingerprint>, as linked from a group), while every
    # seeded failure still has its stored fingerprint. SQL storages only: the generic provider
    # has no stored fingerprints to read here.
    if ctx.sql:
        conn, provider = ctx.connect()
        try:
            top = bulkseed.top_stored_fingerprint(conn, provider)
        finally:
            conn.close()
        if top is None:
            ok = False
            notes.append("No stored fingerprint on any stress failure: FailureFingerprintFilter didn't run.")
        else:
            fp, expected = top
            try:
                started = time.perf_counter()
                page.goto(f"{ctx.dashboard}/search?exception={fp}", wait_until="domcontentloaded")
                ms, outcome = _search_outcome(page, ctx, started)
                found = outcome.split(" ")[0]
                rows.append(("Search ?exception=<largest group>", f"{_fmt_ms(ms)} → {outcome} (expected {expected:,})"))
                matches = found.endswith("+") or found.replace(",", "") == str(expected)
                ok &= ms <= limit_ms + 5000 and outcome not in ("error", "timed out") and matches
                if not matches:
                    notes.append(f"Search by fingerprint found {found}, the database has {expected:,} jobs with it.")
            except Exception as e:
                ok = False
                notes.append(f"search by fingerprint: {str(e)[:120]}")

    variants = [("all stored", None), ("half stored", 0.5), ("none stored", 1.0)] if ctx.sql else [("generic provider", None)]
    for label, unstore in variants:
        if unstore is not None:
            ctx.ensure_writable("delete stored fingerprints")
            conn, provider = ctx.connect()
            try:
                bulkseed.unstore_fingerprints(conn, provider, unstore)
                stored = bulkseed.stress_failure_count(conn, provider, True)
            finally:
                conn.close()
            label += f" ({stored:,} left)"
        try:
            ms, groups, status = _open_groups(page, ctx)
            rows.append((f"groups, {label}", f"{_fmt_ms(ms)} → {groups} labeled" + (f"; {status}" if status else "")))
            ok &= ms <= limit_ms and groups > 0 and groups <= 50
        except Exception as e:
            ok = False
            notes.append(f"{label}: {str(e)[:120]}")
    browser.close_pages()
    return TierResult("V2", "Failed page grouping", ok,
                      "groups load within the search timeout" if ok else "grouping too slow or empty", rows, notes)


# ─── M1 group actions ─────────────────────────────────────────────────────────

# StressJobs.Fail(0)'s message: M1 seeds its big group with kind 0. Picking the group by it, not
# by size, keeps M1 off other large groups (V1's synthetic failures are one 12,000-job group).
_M1_GROUP_MESSAGE = "The invoice was rejected by the gateway"


def _select_m1_group(page, ctx: Context) -> str:
    _open_groups(page, ctx)
    page.locator(".hf-exception-option", has_text=_M1_GROUP_MESSAGE).first.click()
    total = page.locator("[data-testid=group-total]")
    total.wait_for(timeout=(ctx.settings.search_timeout_s + 30) * 1000)
    return total.inner_text().strip()


def _run_group_action(page, ctx: Context, test_id: str, confirm: str, cancel_after_s: float | None,
                      timeout_s: float = 1800) -> tuple[float, str]:
    page.locator(f"[data-testid={test_id}]").click()
    page.locator(".modal-footer button", has_text=confirm).first.click(timeout=(ctx.settings.search_timeout_s + 30) * 1000)
    started = time.monotonic()
    summary = page.locator("[data-testid=group-action-summary]")
    if cancel_after_s is not None:
        # Cancel once the action has run for cancel_after_s, unless it finished first (small group).
        progress = page.locator("[data-testid=group-action-progress]")
        page.locator("[data-testid=group-action-progress], [data-testid=group-action-summary]").first.wait_for(timeout=60000)
        while time.monotonic() - started < cancel_after_s and not summary.count():
            time.sleep(0.1)
        if progress.count():
            try:
                progress.locator("button", has_text="Cancel").click(timeout=5000)
            except Exception:
                pass  # finished between the check and the click
    summary.wait_for(timeout=timeout_s * 1000)
    return time.monotonic() - started, re.sub(r"\s+", " ", summary.inner_text()).strip()


def tier_m1(ctx: Context, browser: Browser) -> TierResult:
    rows, notes = [], []
    n = ctx.p["m1_jobs"]
    if not ctx.reuse_data:
        done, seconds = _seed_failures(ctx, n, fixed_kind=0)
        rows.append(("One big group seeded", f"{n:,} failures of kind 0 in {seconds:.0f}s"))
        if not done:
            notes.append("The server didn't process every seeded failure in time.")
    ctx.ensure_writable("requeue and delete a failure group")
    page = browser.page(timeout_ms=120000)
    ok = not notes
    try:
        before = _select_m1_group(page, ctx)
        rows.append(("Group selected", before))
        secs, text = _run_group_action(page, ctx, "requeue-all", "Requeue all", cancel_after_s=2)
        rows.append(("Requeue all, cancelled after ~2 s", f"{secs:.1f}s → {text}"))
        if text.startswith("Requeued"):
            # Finished inside the 2 s: nothing to cancel. Acceptable for a small group, and on the
            # generic provider, whose snapshot is at most the 1,000 newest failures it grouped
            # (README "Requeue or delete a whole group").
            m_snap = re.search(r"of (\d[\d.,\u00a0]*) jobs", text)
            snapshot = _int(m_snap.group(1)) if m_snap else 0
            generic_cap = not ctx.sql and snapshot <= 1000
            notes.append("Requeue all finished before the cancel"
                         + (": the generic provider acts on the 1,000 newest failures it grouped." if generic_cap
                            else "; the group is too small to test cancelling."))
            ok &= (n < 5000 or generic_cap) and "error" not in text.lower()
        else:
            ok &= text.startswith("Cancelled after")

        page.reload(wait_until="domcontentloaded")
        _wait_interactive(page)
        group = _select_m1_group(page, ctx)
        secs, text = _run_group_action(page, ctx, "delete-all", "Delete all", cancel_after_s=None)
        rows.append(("Delete all", f"{secs:.1f}s → {text}"))
        # Counts are formatted in the server's culture: 1,000 or 1.000.
        num = r"(\d[\d.,\u00a0]*)"
        m = re.search(rf"Deleted {num} of {num} jobs", text)
        if not m:
            ok = False
            notes.append("No 'Deleted N of M jobs' summary.")
        else:
            deleted, total = (_int(x) for x in m.groups())
            skipped = re.search(rf"{num} (?:was|were|job|jobs)[^.]*skipped", text)
            skipped_n = _int(skipped.group(1)) if skipped else 0
            rows.append(("Deleted + skipped = snapshot", f"{deleted:,} + {skipped_n:,} = {deleted + skipped_n:,} (snapshot {total:,})"))
            ok &= deleted + skipped_n == total and "error" not in text.lower()
            rows.append(("Expected audit batches (500 per batch)", f"{-(-total // 500):,}"))
    except Exception as e:
        ok = False
        notes.append(str(e)[:200])
    browser.close_pages()
    return TierResult("M1", "Requeue all / Delete all", ok,
                      "group actions completed and add up" if ok else "a group action failed or didn't add up", rows, notes)


# ─── S1 slow storage ──────────────────────────────────────────────────────────

class _LockCycler(threading.Thread):
    """Locks the job table for `hold_s`, releases it for `gap_s`, repeatedly: PostgreSQL ACCESS
    EXCLUSIVE; SQL Server a schema-modification lock (an uncommitted column add), since its
    monitoring reads use NOLOCK and pass an exclusive table lock."""

    def __init__(self, ctx: Context, hold_s: float = 10, gap_s: float = 5):
        super().__init__(daemon=True)
        self.ctx, self.hold_s, self.gap_s = ctx, hold_s, gap_s
        self.stop_evt = threading.Event()
        self.cycles = 0
        self.error = None
        self.held_since: float | None = None   # monotonic time the current hold started, None when free

    def wait_into_hold(self, seconds: float, timeout: float = 30) -> bool:
        """Block until the lock has been held for `seconds`, so callers have piled up behind it."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            since = self.held_since
            if since is not None and time.monotonic() - since >= seconds:
                return True
            time.sleep(0.2)
        return False

    def run(self):
        try:
            conn, provider = self.ctx.connect()
        except BaseException as e:  # get_connection exits on failure
            self.error = str(e)
            return
        cur = conn.cursor()
        try:
            while not self.stop_evt.is_set():
                if provider == "sqlserver":
                    # Hangfire.SqlServer's monitoring API reads WITH (NOLOCK), which an exclusive
                    # table lock doesn't block. A schema change does: NOLOCK still takes a
                    # schema-stability lock. The column add is rolled back with the transaction.
                    cur.execute("ALTER TABLE [HangFire].[Job] ADD [hfload_s1_lock] int NULL")
                else:
                    cur.execute("LOCK TABLE hangfire.job IN ACCESS EXCLUSIVE MODE")
                self.held_since = time.monotonic()
                self.stop_evt.wait(self.hold_s)
                conn.rollback()
                self.held_since = None
                self.cycles += 1
                self.stop_evt.wait(self.gap_s)
        except Exception as e:
            self.error = str(e)[:200]
        finally:
            try:
                conn.rollback()
                conn.close()
            except Exception:
                pass


class _HealthPinger(threading.Thread):
    def __init__(self, url: str, every_s: float = 1.0):
        super().__init__(daemon=True)
        self.url, self.every_s = url, every_s
        self.stop_evt = threading.Event()
        self.latencies: list[float] = []
        self.errors = 0

    def run(self):
        with httpx.Client(timeout=60) as client:
            while not self.stop_evt.is_set():
                started = time.perf_counter()
                try:
                    client.get(self.url)
                    self.latencies.append((time.perf_counter() - started) * 1000)
                except Exception:
                    self.errors += 1
                self.stop_evt.wait(self.every_s)


def _open_s_pages(browser: Browser, ctx: Context, count: int, wait: bool = True):
    routes = ["/jobs/processing", "/queues", "/", "/jobs/succeeded", "/jobs/failed"]
    pages = []
    for i in range(count):
        url = ctx.dashboard + routes[i % len(routes)]
        try:
            pages.append(browser.page(url, timeout_ms=90000) if wait else browser.page_nowait(url))
        except Exception:
            pass
    return pages


_STORAGE_FRAMES = ("Npgsql", "Microsoft.Data.SqlClient", "System.Data.SqlClient")
# Dashboard readers that single-flight their own Task.Run outside the storage gate: the metrics feed,
# QueueOperationsStateCache and ThrottlingDetectionCache. Each blocks at most one thread.
_SINGLE_FLIGHT_READERS = 3


def _find_stack_exe() -> str | None:
    exe = shutil.which("dotnet-stack")
    if exe:
        return exe
    candidate = Path.home() / ".dotnet" / "tools" / ("dotnet-stack.exe" if os.name == "nt" else "dotnet-stack")
    return str(candidate) if candidate.exists() else None


@dataclass
class _Stacks:
    summary: str
    path: Path | None = None
    workers: int = 0
    busy: int = 0
    in_storage: int = 0      # pool workers blocked in the storage driver
    via_dashboard: int = 0   # ... of which on a dashboard call

    @property
    def available(self) -> bool:
        return self.path is not None


def _capture_stacks(ctx: Context, label: str) -> _Stacks:
    """With dotnet-stack installed, save the app's managed stacks and count the pool workers in a storage driver."""
    exe = _find_stack_exe()
    if not exe or not ctx.sampler.pid:
        return _Stacks("dotnet-stack not installed")
    try:
        r = subprocess.run([exe, "report", "--process-id", str(ctx.sampler.pid)], capture_output=True,
                           text=True, timeout=120)
    except Exception as e:
        return _Stacks(f"dotnet-stack failed: {e}")
    REPORTS_DIR.mkdir(exist_ok=True)
    path = REPORTS_DIR / f"stacks-{ctx.provider_key}-{label}-{datetime.now():%Y%m%d-%H%M%S}.txt"
    path.write_text(r.stdout + r.stderr, encoding="utf-8")
    # dotnet-stack prints one block per thread, starting with "Thread (0x…):".
    blocks = re.split(r"(?m)^\s*Thread \(0x[0-9A-Fa-f]+\):", r.stdout)[1:]
    # Pool workers start in WorkerThreadStart; an idle one waits in LowLevelLifoSemaphore.
    workers = [b for b in blocks if "WorkerThread.WorkerThreadStart" in b]
    busy = [b for b in workers if "LowLevelLifoSemaphore" not in b]
    in_storage = [b for b in busy if any(f in b for f in _STORAGE_FRAMES)]
    via_dashboard = sum(1 for b in in_storage if "a2n.Hangfire" in b)
    # What the other busy workers are doing: their innermost managed frame, most common first.
    tops: dict[str, int] = {}
    for b in busy:
        if b in in_storage:
            continue
        frames = [line.strip() for line in b.splitlines() if "!" in line]
        if frames:
            name = frames[0].split("!", 1)[1].split("(", 1)[0]
            tops[name] = tops.get(name, 0) + 1
    other = ", ".join(f"{n} ×{c}" for n, c in sorted(tops.items(), key=lambda kv: -kv[1])[:3])
    summary = (f"{len(workers)} pool workers, {len(busy)} busy, {len(in_storage)} blocked in the storage driver "
               f"({via_dashboard} on dashboard calls)" + (f"; other busy ones in {other}" if other else ""))
    return _Stacks(summary, path, len(workers), len(busy), len(in_storage), via_dashboard)


def tier_s1(ctx: Context, browser: Browser) -> TierResult:
    if not ctx.sql:
        return TierResult("S1", "Slow storage (#64)", None, "SQL storages only (needs a lockable table)")
    ctx.ensure_writable("lock the job table repeatedly")
    phase, s = ctx.p["s1_phase"], ctx.settings
    rows, notes = [], []
    window = 15 + 2 * phase + 120 + 30
    pingers = [_HealthPinger(ctx.dashboard + path) for path in ("/healthz", "/healthz/ready", "/healthz/full")]
    home = None
    with ctx.sampler.session(window) as session:
        t0 = time.monotonic()
        time.sleep(12)
        idle_end = time.monotonic() - t0
        home = browser.page(ctx.dashboard + "/")
        _wait_interactive(home)
        _open_s_pages(browser, ctx, 4)
        locks = _LockCycler(ctx)
        locks.start()
        for p_ in pingers:
            p_.start()
        p1_start = time.monotonic() - t0
        time.sleep(phase)
        p1_end = time.monotonic() - t0
        # Stacks taken 6 s into a hold, when every caller that will block is blocked.
        locks.wait_into_hold(6)
        stacks5 = _capture_stacks(ctx, "s1-5-circuits")
        # Opened under the locks without waiting, so the phase lasts what it says.
        _open_s_pages(browser, ctx, 15, wait=False)
        time.sleep(phase)
        p2_end = time.monotonic() - t0
        locks.wait_into_hold(6)
        stacks20 = _capture_stacks(ctx, "s1-20-circuits")
        locks.stop_evt.set()
        locks.join(timeout=30)
        recovered_s = _wait_marker_change(home, "[data-testid=metrics-snapshot]", "data-snapshot-at",
                                          s.max_backoff_s + 60)
        for p_ in pingers:
            p_.stop_evt.set()
        healthy_s = None
        started = time.monotonic()
        while time.monotonic() - started < 120:
            try:
                r = httpx.get(ctx.dashboard + "/healthz/ready", timeout=30)
                if r.status_code == 200:
                    healthy_s = time.monotonic() - started
                    break
            except Exception:
                pass
            time.sleep(2)
    browser.close_pages()
    stats = _metrics_window(session)
    threads = _samples(stats, "threadpool_threads")
    ok = locks.cycles > 0 and locks.error is None
    rows.append(("Lock cycles (10 s held, 5 s free)", f"{locks.cycles}" + (f"; error {locks.error}" if locks.error else "")))
    # The bound #64 promises is on threads *blocked in storage*: the gate's slots plus one per
    # single-flight reader outside it (metrics feed, queue-operations state, throttling detection).
    # The pool's thread count isn't that: while workers are blocked the pool injects more, which then
    # sit idle until they retire, so it overstates the blocking. With dotnet-stack the check counts
    # the blocked workers; without it, it falls back to the thread count.
    blocked_limit = s.max_storage_calls + _SINGLE_FLIGHT_READERS
    for label, st in (("5 circuits", stacks5), ("20 circuits", stacks20)):
        rows.append((f"Managed stacks, {label}", st.summary + (f" — {st.path.name}" if st.path else "")))
    if stacks5.available and stacks20.available:
        rows.append((f"Workers blocked in storage, 5 / 20 circuits (limit MaxConcurrentStorageCalls + "
                     f"{_SINGLE_FLIGHT_READERS} = {blocked_limit})", f"{stacks5.in_storage} / {stacks20.in_storage}"))
        ok &= stacks20.in_storage <= blocked_limit and stacks5.in_storage <= blocked_limit
    if threads:
        idle = max(_window(threads, 0, idle_end) or threads[:5])
        w5, w20 = _window(threads, p1_start, p1_end), _window(threads, p1_end, p2_end)
        if not w5 or not w20:
            ok = False
            notes.append(f"dotnet-counters stopped early ({len(threads)} samples): the phases weren't covered.")
        t5 = max(w5 or [0])
        t20 = max(w20 or [0])
        after = statistics.mean(threads[-5:])
        rows.append(("Thread-pool threads idle / 5 circuits / 20 circuits / at the end",
                     f"{idle:.0f} / {t5:.0f} / {t20:.0f} / {after:.0f}"))
        if stacks5.available and stacks20.available:
            # Only a ceiling here: an unbounded pile-up grows with circuits × refresh loops, far past it.
            ceiling = idle + 2 * blocked_limit + 8
            rows.append((f"Thread-pool threads ceiling (idle + 2 × {blocked_limit} + 8)", f"{ceiling:.0f}"))
            ok &= t20 <= ceiling
        else:
            limit = idle + s.max_storage_calls + 8
            rows += [("Limit without dotnet-stack (idle + MaxConcurrentStorageCalls + 8)", f"{limit:.0f}"),
                     ("Growth from 5 to 20 circuits (limit 4)", f"{t20 - t5:+.0f}")]
            ok &= t20 <= limit and t20 - t5 <= 4
            notes.append("dotnet-stack isn't installed, so the thread-pool count stands in for the blocked "
                         "threads and overstates them: `dotnet tool install --global dotnet-stack`.")
    else:
        ok = False
        notes.append("No server metrics (dotnet-counters): the thread bound can't be judged.")
    all_lat = [v for p_ in pingers for v in p_.latencies]
    probe_limit = s.storage_probe_timeout_ms + 3000
    rows.append(("/healthz* during locks p95 / max (limit %s)" % _fmt_ms(probe_limit),
                 f"{_fmt_ms(_pct(all_lat, 95))} / {_fmt_ms(max(all_lat) if all_lat else None)}"))
    ok &= bool(all_lat) and max(all_lat) <= probe_limit
    rows.append(("Home refreshed after the locks stopped", "after %.0f s" % recovered_s if recovered_s is not None else "no"))
    ok &= recovered_s is not None and recovered_s <= s.max_backoff_s + 15
    rows.append(("/healthz/ready healthy again", "after %.0f s" % healthy_s if healthy_s is not None else "no"))
    ok &= healthy_s is not None
    if session:
        session.cleanup()
    if ctx.app_log:
        rows.append(("Back-off warnings in the app log", str(_count_log(ctx.app_log, "backing off"))))
    return TierResult("S1", "Slow storage (#64)", ok,
                      "threads bounded, nothing piled up, refresh recovered" if ok else "see rows", rows, notes)


def _count_log(path: str, needle: str) -> int:
    """Lines containing `needle`. PowerShell's `*>` writes UTF-16, so go by the BOM."""
    try:
        with open(path, "rb") as f:
            head = f.read(4)
        encoding = "utf-16" if head[:2] in (b"\xff\xfe", b"\xfe\xff") else "utf-8-sig"
        with open(path, encoding=encoding, errors="replace") as f:
            return sum(1 for line in f if needle in line)
    except OSError:
        return -1


# ─── S2 storage down ──────────────────────────────────────────────────────────

def _admin_connection(ctx: Context):
    """A connection to the server's maintenance database, from which the test database can be closed."""
    target = guard.describe_target(ctx.cfg)
    if ctx.provider == "postgresql":
        import psycopg
        pg = ctx.cfg["database"]["postgresql"]
        conn = psycopg.connect(host=pg["host"], port=pg["port"], dbname="postgres",
                               user=pg["username"], password=pg["password"], autocommit=True)
        return conn, target.database
    import pyodbc
    cs = cfg_mod.get_sqlserver_conn(ctx.cfg)
    cs = re.sub(r"(?i)(Database|Initial Catalog)\s*=\s*[^;]*", r"\1=master", cs)
    return pyodbc.connect(cs, autocommit=True), target.database


def _take_down(ctx: Context, conn, db: str):
    cur = conn.cursor()
    if ctx.provider == "postgresql":
        cur.execute(f'ALTER DATABASE "{db}" ALLOW_CONNECTIONS false')
        cur.execute("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = %s AND pid <> pg_backend_pid()", (db,))
    else:
        cur.execute(f"ALTER DATABASE [{db}] SET OFFLINE WITH ROLLBACK IMMEDIATE")


def _bring_up(ctx: Context, conn, db: str):
    cur = conn.cursor()
    if ctx.provider == "postgresql":
        cur.execute(f'ALTER DATABASE "{db}" ALLOW_CONNECTIONS true')
    else:
        cur.execute(f"ALTER DATABASE [{db}] SET ONLINE")


def tier_s2(ctx: Context, browser: Browser) -> TierResult:
    if not ctx.sql:
        return TierResult("S2", "Storage down", None, "SQL storages only")
    ctx.ensure_writable("take the test database offline for a minute")
    outage = ctx.p["s2_outage"]
    rows, notes = [], []
    pages = _open_s_pages(browser, ctx, 5)
    home = next((p for p in pages if p.url.rstrip("/").endswith(ctx.cfg["app"]["dashboard_path"].strip("/"))), None)
    for p in pages:
        _wait_interactive(p)
    conn, db = _admin_connection(ctx)
    down_at = time.monotonic()
    alive_during = 0
    ready_down = 0
    error_ui = False
    try:
        _take_down(ctx, conn, db)
        while time.monotonic() - down_at < outage:
            try:
                r = httpx.get(ctx.dashboard + "/healthz", timeout=30)
                alive_during += r.status_code in (200, 503)
                # Proves the outage took: readiness needs the storage.
                ready_down += httpx.get(ctx.dashboard + "/healthz/ready", timeout=30).status_code != 200
            except Exception:
                pass
            for p in pages:
                try:
                    if p.locator("#blazor-error-ui").is_visible():
                        error_ui = True
                except Exception:
                    pass
            time.sleep(5)
    finally:
        _bring_up(ctx, conn, db)
        conn.close()
    up_at = time.monotonic()
    recovered = _wait_marker_change(home, "[data-testid=metrics-snapshot]", "data-snapshot-at",
                                    ctx.settings.max_backoff_s + 90) if home else None
    healthy_s = None
    while time.monotonic() - up_at < 180:
        try:
            if httpx.get(ctx.dashboard + "/healthz/ready", timeout=30).status_code == 200:
                healthy_s = time.monotonic() - up_at
                break
        except Exception:
            pass
        time.sleep(2)
    heartbeat_ok = False
    try:
        from db import fetch_stats
        c2, prov = ctx.connect()
        heartbeat_ok = fetch_stats(c2, prov)["active_servers"] > 0
        c2.close()
    except BaseException:
        pass
    for p in pages:
        try:
            error_ui |= p.locator("#blazor-error-ui").is_visible()
        except Exception:
            pass
    browser.close_pages()
    ok = (alive_during > 0 and ready_down > 0 and not error_ui and recovered is not None
          and healthy_s is not None and heartbeat_ok)
    if ready_down == 0:
        notes.append("/healthz/ready never failed during the outage: the database wasn't really down.")
    rows += [("Outage", f"{outage}s ({'ALLOW_CONNECTIONS false' if ctx.provider == 'postgresql' else 'SET OFFLINE'})"),
             ("/healthz answered during the outage", f"{alive_during} times"),
             ("/healthz/ready unhealthy during the outage", f"{ready_down} times"),
             ("Blazor error UI shown", "yes" if error_ui else "no"),
             ("Home refreshed after recovery", "after %.0f s" % recovered if recovered is not None else "no"),
             ("/healthz/ready healthy again (storage + servers)", "after %.0f s" % healthy_s if healthy_s is not None else "no"),
             ("Hangfire server heartbeating again", "yes" if heartbeat_ok else "no")]
    if ctx.app_log:
        rows.append(("'failed; backing off' lines in the app log", str(_count_log(ctx.app_log, "failed; backing off"))))
    return TierResult("S2", "Storage down", ok, "survived and recovered" if ok else "see rows", rows, notes)


# ─── E1 soak (legacy tier E) ──────────────────────────────────────────────────

def tier_e1(ctx: Context) -> TierResult:
    duration = ctx.p["e1_duration"]
    if duration <= 0:
        return TierResult("E1", "Soak", None, "not in this profile")
    if not ctx.sql:
        return TierResult("E1", "Soak", None, "SQL storages only (its writers use the database)")
    ctx.ensure_writable("seed jobs and toggle queues for the soak")
    with _captured(stress) as rec:
        ok = stress.tier_combined(ctx.cfg, duration, 50, 4, 20, 10)
    return TierResult("E1", f"Soak ({duration // 60} min)", ok, "working set plateaued" if ok else "growth or errors",
                      console_text=rec.export_text())


# ─── P1 package ───────────────────────────────────────────────────────────────

_P1_PROGRAM = """using Hangfire;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHangfire(c => c.UseInMemoryStorage());
builder.Services.AddHangfireServer();
builder.Services.AddHangfireDashboardUI();
var app = builder.Build();
app.UseHangfireDashboardUI("/hangfire");
app.Run();
"""


_P1_NUGET_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-pack" value="{feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"""


def _run(cmd: list[str], cwd: Path, timeout: int = 900, env: dict | None = None) -> tuple[int, str]:
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout, env=env)
    return r.returncode, (r.stdout + r.stderr)[-2000:]


def _probe_p1_app(out_dir: Path, name: str, port: int, run_env: dict, app_log: Path):
    """Start a published P1 app, check the dashboard page, _content and blazor.web.js, stop it.
    Returns (page_ok, assets_ok, framework_ok, process, last log lines)."""
    log_handle = open(app_log, "w", encoding="utf-8", errors="replace")
    proc = subprocess.Popen(["dotnet", str(out_dir / f"{name}.dll"), "--urls", f"http://localhost:{port}"],
                            cwd=out_dir, env=run_env, stdout=log_handle, stderr=subprocess.STDOUT)
    page_ok = assets_ok = framework_ok = False
    root = f"http://localhost:{port}/hangfire"
    try:
        for _ in range(90):
            if proc.poll() is not None:
                break  # exited: the log says why
            try:
                r = httpx.get(root, timeout=10, follow_redirects=True)
                page_ok = r.status_code == 200 and "blazor.web.js" in r.text
                if page_ok:
                    assets = [f"{root}/_content/css/app.css", f"{root}/_content/lib/bootstrap/css/bootstrap.min.css",
                              f"{root}/_content/js/charts.js"]
                    assets_ok = all(httpx.get(a, timeout=10).status_code == 200 for a in assets)
                    framework_ok = httpx.get(f"{root}/_framework/blazor.web.js", timeout=10).status_code == 200
                    break
            except Exception:
                pass
            time.sleep(1)
    finally:
        exited_on_its_own = proc.poll() is not None
        if not exited_on_its_own:
            proc.terminate()
            try:
                proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)
        log_handle.close()
    tail = app_log.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-15:]
    if not exited_on_its_own:
        proc.returncode = None  # stopped by us, not a crash
    return page_ok, assets_ok, framework_ok, proc, tail


def tier_p1(ctx: Context) -> TierResult:
    rows, notes = [], []
    # Under reports/ (git-ignored) rather than %TEMP%: Windows Application Control can block a
    # freshly built, unsigned assembly loaded from the temp folder.
    REPORTS_DIR.mkdir(exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="p1-work-", dir=REPORTS_DIR))
    # Inside the repo, MSBuild would import the repo's Directory.Build.* files into the test apps
    # and could hide exactly what P1 checks. Empty ones here stop the search.
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        (work / name).write_text("<Project />\n", encoding="utf-8")
    feed = work / "feed"
    # The local feed plus nuget.org for the dependencies, and a package cache of its own, so a
    # same-version package cached from an earlier pack can't stand in for this one.
    (work / "nuget.config").write_text(_P1_NUGET_CONFIG.format(feed=feed), encoding="utf-8")
    env = {**os.environ, "NUGET_PACKAGES": str(work / "packages"), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
           "DOTNET_NOLOGO": "1"}
    # HFLOAD_P1_VERSION=2.5.7 tests that published version from nuget.org instead of packing the repo.
    published = os.environ.get("HFLOAD_P1_VERSION", "").strip()
    if published:
        feed.mkdir(parents=True, exist_ok=True)
        version = published
        rows.append(("Package", f"a2n.Hangfire.Dashboard {version} from nuget.org"))
    else:
        code, out = _run(["dotnet", "pack", str(REPO_ROOT / "src" / "a2n.Hangfire.Dashboard" / "a2n.Hangfire.Dashboard.csproj"),
                          "-c", "Release", "-o", str(feed), "-nologo"], REPO_ROOT)
        pkgs = sorted(feed.glob("a2n.Hangfire.Dashboard.*.nupkg"))
        if code != 0 or not pkgs:
            return TierResult("P1", "Package drop-in", False, "dotnet pack failed", notes=[out])
        version = pkgs[0].stem[len("a2n.Hangfire.Dashboard."):]
        rows.append(("Packed", pkgs[0].name))
    ok = True
    # HFLOAD_P1_TFMS=net10.0 limits the targets; HFLOAD_P1_KEEP=1 keeps the work folder for a look.
    tfms = [t.strip() for t in os.environ.get("HFLOAD_P1_TFMS", "net8.0,net9.0,net10.0").split(",") if t.strip()]
    keep = os.environ.get("HFLOAD_P1_KEEP") == "1"
    if keep:
        notes.append(f"Work folder kept: {work}")
    for i, tfm in enumerate(tfms):
        app = work / f"app-{tfm}"
        port = 5190 + i
        steps = [
            ["dotnet", "new", "web", "-o", str(app), "--framework", tfm, "--force"],
            ["dotnet", "add", str(app), "package", "Hangfire.AspNetCore"],
            ["dotnet", "add", str(app), "package", "Hangfire.InMemory"],
            ["dotnet", "add", str(app), "package", "a2n.Hangfire.Dashboard", "--version", version],
        ]
        failed = None
        for cmd in steps:
            code, out = _run(cmd, work, env=env)
            if code != 0:
                failed = f"{' '.join(cmd[:3])}: {out[-300:]}"
                break
        if failed:
            ok = False
            rows.append((tfm, "setup failed"))
            notes.append(failed)
            continue
        (app / "Program.cs").write_text(_P1_PROGRAM, encoding="utf-8")
        # Published and run in Production, as a user deploys it: no static-web-assets dev manifest.
        out_dir = app / "out"
        code, out = _run(["dotnet", "publish", str(app), "-c", "Release", "-o", str(out_dir), "-nologo", "-v", "q"],
                         work, env=env)
        if code != 0:
            ok = False
            rows.append((tfm, "publish failed"))
            notes.append(out[-400:])
            continue
        run_env = {**env, "ASPNETCORE_ENVIRONMENT": "Production"}
        app_log = app / "run.log"
        # Windows Application Control (Smart App Control) sometimes blocks a freshly built,
        # unsigned assembly on its first loads (0x800711C7), which says nothing about the package.
        # That one failure is retried; it is reported as the environment's if it persists.
        blocked = False
        for attempt in range(3):
            page_ok, assets_ok, framework_ok, proc, tail = _probe_p1_app(out_dir, app.name, port, run_env, app_log)
            blocked = proc.returncode is not None and any("Application Control policy has blocked" in line for line in tail)
            if not blocked:
                break
            time.sleep(10)
        if blocked:
            rows.append((tfm, "not run: Windows Application Control blocked the test app (environment, not the package)"))
            ok = False
            notes.append(f"{tfm}: the test app's own dll was blocked by Windows Application Control 3 times "
                         "(0x800711C7). Rerun P1, or allow the reports/ folder.")
            continue
        rows.append((tfm, f"page {'200' if page_ok else 'FAILED'}, _content {'200' if assets_ok else 'FAILED'}, "
                          f"blazor.web.js {'200' if framework_ok else 'FAILED'}"
                          + (f" (after {attempt} blocked start{'s' if attempt != 1 else ''})" if attempt else "")))
        ok &= page_ok and assets_ok and framework_ok
        if not (page_ok and assets_ok and framework_ok):
            if proc.returncode is not None:
                what = f"exited with code {proc.returncode}"
            elif page_ok:
                what = "served the page but not everything it needs"
            else:
                what = "didn't serve the dashboard page in 90 s"
            notes.append(f"{tfm} app on port {port} {what}. Last log lines:\n" + "\n".join(tail))
    if not keep:
        shutil.rmtree(work, ignore_errors=True)
    return TierResult("P1", "Package drop-in (net8/9/10)", ok, "installs and serves on every target" if ok else "see rows",
                      rows, notes)


# ─── orchestration ───────────────────────────────────────────────────────────

def _environment(ctx: Context) -> list[tuple[str, str]]:
    def git(*args):
        try:
            return subprocess.run(["git", "-C", str(REPO_ROOT), *args], capture_output=True, text=True, timeout=20).stdout.strip()
        except Exception:
            return "?"
    target = guard.describe_target(ctx.cfg)
    try:
        from playwright import __version__ as pw_version
    except Exception:
        pw_version = "?"
    return [
        ("Date (UTC)", datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M")),
        ("Commit", f"{git('rev-parse', '--short', 'HEAD')} ({git('rev-parse', '--abbrev-ref', 'HEAD')})"),
        ("Provider", f"{ctx.provider}" + (f" — {target.database} on {target.host}" if ctx.sql else "")),
        ("Profile", ctx.profile_name + (f" (data sizes ×{ctx.scale:g}: not a release run)" if ctx.scale != 1.0 else "")),
        ("Dashboard", ctx.dashboard),
        ("Machine", f"{platform.system()} {platform.release()}, {os.cpu_count()} logical CPUs"),
        ("Python / Playwright", f"{platform.python_version()} / {pw_version}"),
        ("Server metrics", "dotnet-counters" if ctx.sampler.available else "unavailable"),
    ]


def write_report(ctx: Context, results: list[TierResult]) -> Path:
    REPORTS_DIR.mkdir(exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M")
    path = REPORTS_DIR / f"release-check-{ctx.provider_key}-{ctx.profile_name}-{stamp}.md"
    failed = [r for r in results if r.ok is False]
    lines = [f"# Release check — {ctx.provider} ({ctx.profile_name})", "",
             f"**Verdict: {'PASS' if not failed else 'FAIL'}** — "
             f"{sum(r.ok is True for r in results)} passed, {len(failed)} failed, "
             f"{sum(r.ok is None for r in results)} skipped.", "",
             "## Environment", "", "| Item | Value |", "|---|---|"]
    lines += [f"| {k} | {v} |" for k, v in _environment(ctx)]
    lines += ["", "## Summary", "", "| Tier | Check | Verdict | Result | Time |", "|---|---|---|---|---|"]
    lines += [f"| {r.id} | {r.title} | {r.verdict} | {r.summary} | {r.seconds:.0f}s |" for r in results]
    for r in results:
        lines += ["", f"## {r.id} — {r.title}: {r.verdict}", ""]
        if r.rows:
            lines += ["| Metric | Value |", "|---|---|"] + [f"| {k} | {v} |" for k, v in r.rows]
        for note in r.notes:
            lines += ["", f"> {note}"]
        if r.console_text.strip():
            lines += ["", "```text", r.console_text.strip(), "```"]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def _print_result(r: TierResult):
    colour = {"PASS": "green", "FAIL": "red", "SKIP": "yellow"}[r.verdict]
    t = Table(box=box.SIMPLE, show_header=False)
    t.add_column("Metric", style="cyan")
    t.add_column("Value")
    for k, v in r.rows:
        t.add_row(k, v)
    console.print(Panel(t if r.rows else r.summary, title=f"[{colour}]{r.id} {r.title}: {r.verdict}[/{colour}]",
                        subtitle=f"{r.summary} · {r.seconds:.0f}s", border_style=colour))
    for note in r.notes:
        console.print(f"  [yellow]•[/yellow] {note}")


def run(ctx: Context, tiers: list[str], headed: bool) -> list[TierResult]:
    needs_browser = {"R1", "R2", "V1", "V2", "M1", "S1", "S2"} & set(tiers)
    browser = Browser(headed) if needs_browser else None
    results: list[TierResult] = []
    runners = {
        "P0": lambda: tier_p0(ctx),
        "R1": lambda: tier_r1(ctx, browser),
        "R2": lambda: tier_r2(ctx, browser),
        "M2": lambda: tier_m2(ctx),
        "V1": lambda: tier_v1(ctx, browser),
        "V2": lambda: tier_v2(ctx, browser),
        "M1": lambda: tier_m1(ctx, browser),
        "S1": lambda: tier_s1(ctx, browser),
        "S2": lambda: tier_s2(ctx, browser),
        "E1": lambda: tier_e1(ctx),
        "P1": lambda: tier_p1(ctx),
    }
    try:
        for tier in ALL_TIERS:
            if tier not in tiers:
                continue
            console.rule(f"[bold]{tier}")
            started = time.monotonic()
            try:
                result = runners[tier]()
            except click.Abort:
                raise
            except Exception as e:
                result = TierResult(tier, tier, False, f"crashed: {type(e).__name__}", notes=[str(e)[:400]])
                if browser:
                    browser.close_pages()
            result.seconds = time.monotonic() - started
            results.append(result)
            _print_result(result)
            if tier == "P0" and result.ok is False:
                console.print("[red]Preflight failed; stopping.[/red]")
                break
    finally:
        if browser:
            browser.close()
    return results


@click.command("release-check")
@click.option("--profile", type=click.Choice(sorted(PROFILES)), default="quick", show_default=True)
@click.option("--provider", type=click.Choice(sorted(PROVIDERS)), required=True,
              help="Storage the running SampleApp uses (pg, sqlserver or inmemory).")
@click.option("--only", default=None, help="Comma-separated tier ids to run (P0 always runs).")
@click.option("--skip", default=None, help="Comma-separated tier ids to leave out.")
@click.option("--reuse-data", is_flag=True, help="Don't seed: reuse what an earlier run left in storage.")
@click.option("--cleanup", is_flag=True, help="Delete the StressJobs rows when done (SQL storages).")
@click.option("--app-log", default=None, type=click.Path(), help="The app's log file, to count back-off messages.")
@click.option("--headed", is_flag=True, help="Show the browser windows.")
@click.option("--scale", default=1.0, show_default=True, type=click.FloatRange(0.001, 10),
              help="Multiply the profile's data sizes (volume, failures, group size); for trying the harness out.")
@guard.yes_option
def cli(profile, provider, only, skip, reuse_data, cleanup, app_log, headed, scale, assume_yes):
    """Run the pre-release stress check against a running SampleApp and write a report."""
    cfg = cfg_mod.load()
    ctx = Context(cfg, provider, profile, assume_yes, reuse_data, app_log)
    if scale != 1.0:
        for key in ("volume", "failures", "m1_jobs"):
            ctx.p[key] = max(100, int(ctx.p[key] * scale))
        ctx.scale = scale
    tiers = [t.strip().upper() for t in only.split(",")] if only else list(ALL_TIERS)
    # P1 builds its own apps, so it alone doesn't need the running SampleApp.
    if "P0" not in tiers and set(tiers) - {"P1"}:
        tiers.insert(0, "P0")
    if skip:
        tiers = [t for t in tiers if t not in {s.strip().upper() for s in skip.split(",")}]
    console.print(Panel(f"[bold cyan]Release check[/bold cyan] — {ctx.provider}, profile {profile}\n"
                        f"Tiers: {', '.join(tiers)}\nDashboard: {ctx.dashboard}", border_style="cyan"))
    results = run(ctx, tiers, headed)
    if cleanup and ctx.sql:
        ctx.ensure_writable("delete the StressJobs rows")
        conn, prov = ctx.connect()
        try:
            console.print(f"Cleanup: deleted {bulkseed.purge_stress_jobs(conn, prov):,} stress jobs.")
        finally:
            conn.close()
    path = write_report(ctx, results)
    failed = [r.id for r in results if r.ok is False]
    console.print(f"\nReport: {path}")
    console.print(f"[{'red' if failed else 'green'}]{'FAIL: ' + ', '.join(failed) if failed else 'PASS'}[/]")
    raise SystemExit(1 if failed else 0)
