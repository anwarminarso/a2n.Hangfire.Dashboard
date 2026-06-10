"""
srvmetrics.py — Server-side metrics sampler for the SampleApp process.

Wraps `dotnet-counters collect` to capture CPU, working set, GC heap, and active
connections while a stress test runs. Without this, a stress test only sees the
client side and is effectively blind to memory leaks and server saturation.

Counters captured (all verified available on the SampleApp):
    System.Runtime:
        cpu-usage              (%)
        working-set            (MB)  — total process memory (the OOM signal)
        gc-heap-size           (MB)  — managed heap (the leak signal)
        threadpool-queue-length      — backlog = saturation
        threadpool-thread-count
        exception-count
    Microsoft.AspNetCore.Http.Connections:
        current-connections          — proxy for live SignalR circuits/connections

Usage (programmatic):
    sampler = ServerMetricsSampler.autodetect()
    with sampler.session() as s:
        ... run load ...
    report = s.summarize()   # dict of counter -> {min,max,mean,first,last}
"""
from __future__ import annotations

import csv
import os
import signal
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

# Counters requested from dotnet-counters. Keep names exactly as the tool emits
# them in the "Counter Name" CSV column so we can map back reliably.
_RUNTIME_COUNTERS = [
    "cpu-usage",
    "working-set",
    "gc-heap-size",
    "threadpool-queue-length",
    "threadpool-thread-count",
    "exception-count",
]
_CONN_COUNTERS = ["current-connections"]

# Map the human "Counter Name" column emitted in CSV -> short key we report on.
_DISPLAY_TO_KEY = {
    "CPU Usage (%)": "cpu_pct",
    "Working Set (MB)": "working_set_mb",
    "GC Heap Size (MB)": "gc_heap_mb",
    "ThreadPool Queue Length": "threadpool_queue",
    "ThreadPool Thread Count": "threadpool_threads",
    "Exception Count (Count / 1 sec)": "exceptions",
    "Exception Count": "exceptions",
    "Current Connections": "connections",
}


def _counters_arg() -> str:
    runtime = ",".join(_RUNTIME_COUNTERS)
    conn = ",".join(_CONN_COUNTERS)
    return f"System.Runtime[{runtime}],Microsoft.AspNetCore.Http.Connections[{conn}]"


def find_counters_exe() -> str | None:
    """Locate dotnet-counters: PATH first, then the default global-tools folder."""
    # Try PATH
    from shutil import which
    exe = which("dotnet-counters")
    if exe:
        return exe
    # Default global tools location
    candidate = Path(os.path.expanduser("~")) / ".dotnet" / "tools" / "dotnet-counters.exe"
    if candidate.exists():
        return str(candidate)
    candidate_nix = Path(os.path.expanduser("~")) / ".dotnet" / "tools" / "dotnet-counters"
    if candidate_nix.exists():
        return str(candidate_nix)
    return None


def find_pid(process_name: str = "SampleApp", counters_exe: str | None = None) -> int | None:
    """Find the target process PID via `dotnet-counters ps`."""
    exe = counters_exe or find_counters_exe()
    if not exe:
        return None
    try:
        out = subprocess.run(
            [exe, "ps"], capture_output=True, text=True, timeout=20
        ).stdout
    except Exception:
        return None

    # Lines look like: "  47944  SampleApp   ...path...\SampleApp.exe   ...cmdline..."
    # Prefer an exact-ish match on the *process name* column (2nd token).
    candidates: list[int] = []
    for line in out.splitlines():
        parts = line.split()
        if len(parts) < 2:
            continue
        if not parts[0].isdigit():
            continue
        pid = int(parts[0])
        name_col = parts[1]
        # Match the short name (avoid matching the "dotnet" launcher)
        if process_name.lower() in name_col.lower():
            candidates.append(pid)
    if candidates:
        return candidates[0]
    # Fallback: match anywhere on the line but exclude the dotnet host launcher
    for line in out.splitlines():
        parts = line.split()
        if len(parts) < 2 or not parts[0].isdigit():
            continue
        if process_name.lower() in line.lower() and "dotnet.exe" not in line.lower():
            return int(parts[0])
    return None


@dataclass
class CounterStats:
    key: str
    samples: list[float] = field(default_factory=list)

    @property
    def count(self) -> int:
        return len(self.samples)

    @property
    def min(self) -> float | None:
        return min(self.samples) if self.samples else None

    @property
    def max(self) -> float | None:
        return max(self.samples) if self.samples else None

    @property
    def mean(self) -> float | None:
        return sum(self.samples) / len(self.samples) if self.samples else None

    @property
    def first(self) -> float | None:
        return self.samples[0] if self.samples else None

    @property
    def last(self) -> float | None:
        return self.samples[-1] if self.samples else None

    @property
    def delta(self) -> float | None:
        """last - first; positive working-set/heap delta over a soak = leak signal."""
        if self.first is None or self.last is None:
            return None
        return self.last - self.first

    def value_after(self, seconds: float, refresh_interval: float = 1.0) -> float | None:
        """Sample value ~`seconds` into the run (one sample per refresh_interval).

        Used to skip the warm-up window when judging memory growth: the first
        sample is captured the moment dotnet-counters attaches, before load has
        ramped, so growth measured from it over-reports on short runs.
        """
        if not self.samples:
            return None
        idx = int(seconds / refresh_interval)
        if idx >= len(self.samples):
            idx = len(self.samples) - 1
        return self.samples[idx]

    def post_warmup_growth_pct(self, warmup_s: float = 20.0,
                               refresh_interval: float = 1.0) -> float | None:
        """Percent growth from a post-warm-up baseline to the last sample.

        This is the honest leak signal: a leak keeps climbing *after* warm-up,
        whereas a healthy service plateaus. Returns None if the run is too short
        to have a meaningful post-warm-up window.
        """
        base = self.value_after(warmup_s, refresh_interval)
        if base is None or self.last is None or base <= 0:
            return None
        # Need at least a few samples beyond the warm-up cutoff to be meaningful.
        if len(self.samples) < int(warmup_s / refresh_interval) + 5:
            return None
        return (self.last - base) / base * 100.0


class _Session:
    def __init__(self, exe: str, pid: int, refresh_interval: int, duration_s: float):
        self._exe = exe
        self._pid = pid
        self._refresh = refresh_interval
        # dotnet-counters only flushes its CSV on a *graceful* stop. The reliable
        # cross-platform way to get that is the --duration flag: the tool runs for
        # the requested window then writes the CSV and exits 0. We add headroom so
        # the sample window fully covers the load, and stop() simply waits for it.
        self._duration_s = max(3.0, duration_s)
        self._proc: subprocess.Popen | None = None
        self._csv_path: str | None = None
        self._started = False

    def start(self):
        # Pick a path but do NOT pre-create the file — dotnet-counters manages it
        # and an existing empty placeholder is left untouched if it crashes.
        self._csv_path = os.path.join(
            tempfile.gettempdir(), f"srvmetrics_{os.getpid()}_{int(time.time()*1000)}.csv"
        )
        if os.path.exists(self._csv_path):
            try:
                os.remove(self._csv_path)
            except OSError:
                pass

        # Format duration as HH:MM:SS for dotnet-counters. Add 3s headroom.
        total = int(self._duration_s + 3)
        hh, rem = divmod(total, 3600)
        mm, ss = divmod(rem, 60)
        dur_str = f"{hh:02d}:{mm:02d}:{ss:02d}"

        cmd = [
            self._exe, "collect",
            "--process-id", str(self._pid),
            "--counters", _counters_arg(),
            "--format", "csv",
            "--output", self._csv_path,
            "--refresh-interval", str(self._refresh),
            "--duration", dur_str,
        ]
        creationflags = 0
        if sys.platform == "win32":
            creationflags = subprocess.CREATE_NEW_PROCESS_GROUP
        self._proc = subprocess.Popen(
            cmd,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=creationflags,
        )
        self._started = True
        # Give the tool a moment to attach before the load starts.
        time.sleep(2.0)

    def stop(self):
        if self._proc is None:
            return
        # The --duration window does the graceful flush for us. Wait for it to
        # finish (it should already be done or nearly so by the time we're called,
        # since duration tracks the load window + headroom).
        try:
            self._proc.wait(timeout=self._duration_s + 15)
        except subprocess.TimeoutExpired:
            # Last resort — kill (CSV may be incomplete but partial data is fine).
            try:
                self._proc.terminate()
                self._proc.wait(timeout=6)
            except Exception:
                try:
                    self._proc.kill()
                except Exception:
                    pass
        time.sleep(0.5)

    def summarize(self) -> dict[str, CounterStats]:
        result: dict[str, CounterStats] = {}
        if not self._csv_path or not os.path.exists(self._csv_path):
            return result
        try:
            with open(self._csv_path, newline="", encoding="utf-8") as f:
                reader = csv.reader(f)
                header = next(reader, None)  # Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
                for row in reader:
                    if len(row) < 5:
                        continue
                    display = row[2].strip()
                    key = _DISPLAY_TO_KEY.get(display)
                    if key is None:
                        continue
                    try:
                        val = float(row[4])
                    except ValueError:
                        continue
                    result.setdefault(key, CounterStats(key)).samples.append(val)
        except Exception:
            pass
        return result

    @property
    def csv_path(self) -> str | None:
        return self._csv_path

    def cleanup(self):
        if self._csv_path and os.path.exists(self._csv_path):
            try:
                os.remove(self._csv_path)
            except OSError:
                pass


class ServerMetricsSampler:
    """Factory + context manager for server-side counter sampling."""

    def __init__(self, exe: str | None, pid: int | None, refresh_interval: int = 1):
        self.exe = exe
        self.pid = pid
        self.refresh_interval = refresh_interval
        self.available = bool(exe and pid)

    @classmethod
    def autodetect(cls, process_name: str = "SampleApp", refresh_interval: int = 1) -> "ServerMetricsSampler":
        exe = find_counters_exe()
        pid = find_pid(process_name, exe) if exe else None
        return cls(exe, pid, refresh_interval)

    def session(self, duration_s: float) -> "_SessionCtx":
        """Return a context manager that samples for `duration_s` seconds.

        Pass the expected load-window duration; the sampler runs dotnet-counters
        with that duration (+ headroom) so the CSV is flushed when the window ends.
        """
        return _SessionCtx(self, duration_s)


class _SessionCtx:
    def __init__(self, sampler: ServerMetricsSampler, duration_s: float):
        self._sampler = sampler
        self._duration_s = duration_s
        self._session: _Session | None = None

    def __enter__(self) -> _Session | None:
        if not self._sampler.available:
            return None
        self._session = _Session(
            self._sampler.exe, self._sampler.pid, self._sampler.refresh_interval, self._duration_s
        )
        self._session.start()
        return self._session

    def __exit__(self, exc_type, exc, tb):
        if self._session is not None:
            self._session.stop()
        return False
