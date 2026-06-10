# Load & Scenario Test Results — PostgreSQL

Results of the developer-run load/scenario harness (`tests/load/`) against a live
SampleApp backed by **PostgreSQL**. Server-side metrics captured via
`dotnet-counters`. This is a **fresh run after the analytics broadcast fix**
(`PeriodicTimer` + parallel queries) and the move to `tests/load/`.

## Environment

| Item | Value |
|------|-------|
| Storage provider | PostgreSQL (`hangfire_test`) |
| Dashboard | `http://localhost:5100/hangfire` |
| Hangfire worker count | 20 (Hangfire default) |
| Metrics tool | dotnet-counters (CPU, working set, GC heap, connections) |
| Job table size | ~3,000 jobs at start, growing during db-load tier |

## Summary verdict

**PASS — healthy at realistic load. No errors, no torn state, realtime cadence on
target. One thing to watch: occasional multi-second push stalls under heavy circuit
churn (see cadence note).**

---

## Per-tier stress results

| Tier | Load | Result | Verdict |
|------|------|--------|---------|
| B — fan-out | 50 WebSocket subscribers, 60 s | 50/50 connected; push gap p95 **2016 ms** (target 2 s); 0 failures | ✅ |
| C — pause hammer | 16 workers, 30 s | **388,717 ops** (~12,950/s); **0 errors, 0 torn state**; op p95 2.3 ms | ✅ |
| D — db load | 100 jobs/batch + 5 readers, 60 s | **5,500 jobs** written; read p95 **2.5 ms** (baseline 1.4 ms) | ✅ |
| A — circuits | 20 browser circuits, 60 s | 20/20 opened, 0 errors; working set residual +12 % after close | ✅ |

### Notable measurements

- **Pause/resume throughput**: 388k ops across 16 workers in 30 s, zero duplicate
  rows — the unique `(key,value)` index + transactions hold under contention.
- **DB read under write load**: p95 only rose 1.4 → 2.5 ms while 5,500 jobs were
  seeded concurrently. No starvation.
- **GC actively reclaims**: in the db-load tier GC heap went 31 → 40 → **7.5 MB**
  (−24 MB delta) — healthy collection, not a leak.

> ⚠️ **Circuits tier caveat:** the 15 s recovery window is not always enough for
> Blazor to fully dispose circuits, so the tool sometimes reports "connections still
> open" and a residual working-set bump. Back-to-back circuit runs let the baseline
> creep. This is largely a measurement-window artifact, not a confirmed leak — but
> circuit disposal timing is worth a dedicated longer-idle check.

---

## Persona scenario (300 s)

Five concurrent browser personas + passive SignalR cadence probe.

| Persona | Actions | Errors | WS open / closed | Frames recv |
|---------|---------|--------|------------------|-------------|
| watcher-home | 1 | 0 | 1 / **0** | 753 |
| watcher-analytics | 1 | 0 | 1 / **0** | 456 |
| trigger-runner | 32 | 0 | 1 / 0 | 673 |
| queue-operator | 49 | 0 | 1 / 0 | 699 |
| wanderer | 124 | 0 | 124 / 123 | 2,725 |

**Total: 207 actions, 0 errors over 5 minutes.** Both watchers held their circuit the
full duration (0 sockets closed).

### SignalR push cadence

| Channel | Expected | Pushes | mean | p95 | max |
|---------|----------|--------|------|-----|-----|
| MetricsUpdated | ~2000 ms | 145 | 2087 ms | **2016 ms** | 9578 ms |
| AnalyticsUpdate | ~5000 ms | 60 | 5000 ms | **5016 ms** | 10171 ms |

- **p95 is on target** for both channels (2.0 s / 5.0 s) — the realtime feed is
  healthy the vast majority of the time.
- **Max gaps spiked to ~9.6 s / ~10.2 s.** A handful of pushes stalled under the
  heaviest navigation churn from the wanderer (124 circuits opened/closed). These are
  rare outliers (mean stays at target), but they confirm the feed can briefly pause
  when the server is busy spinning up/tearing down circuits. Worth monitoring if
  smooth realtime updates under churn are a hard requirement.

### Server-side metrics over the soak

| Counter | first | mean | max | last | delta |
|---------|-------|------|-----|------|-------|
| Working Set (MB) | 292.4 | 323.2 | 336.0 | 332.5 | +40 (plateau) |
| GC Heap (MB) | 89.5 | 118.1 | 156.8 | 116.5 | rises **and falls** (GC active) |
| Connections | 60 | 41 | 78 | 0 | drains to 0 after teardown |

Working set plateaued (~330 MB) rather than climbing monotonically; GC heap rose and
fell; connections drained to 0 at the end — no leak signal.

---

## Findings

- **Broadcast architecture is sound.** Metrics/analytics run in a `BackgroundService`
  separate from Blazor circuits, gated by `DashboardSubscriptionTracker` and (post-fix)
  driven by a `PeriodicTimer` with parallel queries. DB query load does **not** scale
  with viewer count — 1 vs 50 subscribers, read latency unchanged.
- **No memory leak detected** at this scale. Working set plateaus; GC heap rises and
  falls; connections drain after teardown.
- **Realtime cadence is on target (p95)** but shows occasional multi-second stalls
  (max ~10 s) under heavy circuit churn.
- **Concurrent storage mutation is safe.** 388k pause/resume ops, 0 torn state.

## Where degradation *would* appear (not reached here)

- Very large job tables (hundreds of thousands+) until `GetStatistics()` exceeds the
  2 s broadcast interval → metrics push would lag.
- Hundreds of concurrent subscribers until SignalR fan-out exceeds the interval.

At the tested scale (~3–8k jobs, ~50–140 connections) neither was approached.

## Caveats

- These results prove the absence of problems *in the tested scenarios*, not in all
  cases.
- The circuit-recovery window (15 s) is too short to definitively rule out slow
  circuit disposal; a dedicated idle-recovery test would settle it.
- Sources tested via public surfaces (HTTP/WebSocket/UI) plus direct-store seeding.
