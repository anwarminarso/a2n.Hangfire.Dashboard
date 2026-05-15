# Roadmap — a2n.Hangfire.Dashboard

## Vision

A modern, open-source alternative Hangfire dashboard that replaces the built-in dashboard and popular plugins (Console, Tags, RecurringJobAdmin) in a single package — with realtime updates, advanced search, and a clean UI.

## Core Principles

1. **Drop-in replacement** — swap the NuGet package, zero code changes
2. **Data backward compatible** — reads historical data from original plugins without migration
3. **No assembly conflict** — identical namespaces, replaces (not coexists with) original packages
4. **Modern stack** — Blazor Server, Bootstrap 5, Chart.js, SignalR, .NET 8/9/10
5. **Open source** — free alternative to Hangfire Pro dashboard features

## Tech Stack

| Layer | Technology |
|-------|-----------|
| UI Framework | Blazor Server (Interactive) |
| CSS Framework | Bootstrap 5.3 + Bootstrap Icons |
| Charts | Chart.js + chartjs-plugin-streaming + moment.js |
| Realtime | ASP.NET Core SignalR |
| Theme | Bootstrap dark/light mode with localStorage persistence |
| Targets | .NET 8, .NET 9, .NET 10 |

---

## Phase 1 — Foundation & Parity ✅

**Goal**: Feature parity with the built-in dashboard plus integrated plugins.

- ✅ Blazor Server rewrite with full interactive SSR
- ✅ All job state pages (Enqueued, Processing, Scheduled, Succeeded, Failed, Deleted, Awaiting)
- ✅ Job Details with state history timeline
- ✅ Batch operations (requeue, delete) with checkbox selection
- ✅ Recurring job management (full CRUD + Start/Stop)
- ✅ Console integration (realtime logs, progress bars, colored text)
- ✅ Tags integration (tag cloud, search by tag, auto-tagging)
- ✅ Realtime charts (Chart.js + SignalR streaming)
- ✅ Dark/Light/Auto theme toggle
- ✅ Responsive layout (sidebar + offcanvas mobile)
- ✅ Pagination, relative timestamps, server/worker labels

---

## Phase 2 — Differentiation (In Progress)

**Goal**: Features that don't exist in the built-in dashboard.

### 2.1 Search & Filter ✅
- ✅ Global search bar in header (all pages)
- ✅ Dedicated search page with form + advanced filters
- ✅ Search by job ID (direct lookup), name (substring), queue, tag, exception text
- ✅ Advanced filters: date range, state, server, duration, tags (multi-select OR), queue, recurring job ID
- ✅ Filter presets (save/load/delete via localStorage)
- ✅ Form validation, active parameter badges, read-only result summary
- ✅ Pagination with page size selector (10, 20, 50, 100, 500)

### 2.2 Razor Class Library Conversion ✅
- ✅ Converted from web application to Razor Class Library (dll)
- ✅ Custom middleware pipeline (`app.Map` + `DashboardMiddleware` + `FrameworkScriptMiddleware`)
- ✅ Static assets served via embedded resources (`_content/*`)
- ✅ `_framework/blazor.web.js` served via custom middleware in branched pipeline
- ✅ NuGet `buildTransitive` props for automatic `RequiresAspNetWebAssets` propagation
- ✅ True NuGet drop-in: add package reference + `AddHangfireDashboardUI()` + `UseHangfireDashboardUI()`
- ✅ Relative URL links for path prefix compatibility

### 2.3 Performance Insights
- [ ] Top N slowest jobs
- [ ] Queue throughput (jobs/minute, jobs/hour)
- [ ] Server utilization (worker busy %)
- [ ] Job duration trend (per job type over time)

### 2.4 Enhanced Job Details
- [ ] Job dependency graph (continuations visualized)
- [ ] Retry history with diff
- [ ] Job execution duration chart (historical)

---

## Phase 3 — Extensibility & Integration

**Goal**: Features for extensibility and integration with external tools.

### 3.1 Notifications & Alerts
- [ ] Webhook notifications (Slack, Teams, Discord, generic HTTP)
- [ ] Browser push notifications
- [ ] Configurable alert rules
- [ ] Notification history log

### 3.2 API & Metrics
- [ ] REST API for job data (optional package)
- [ ] Prometheus /metrics endpoint
- [ ] Export to CSV/JSON

### 3.3 Customization
- [ ] Theming (custom colors, logo)
- [ ] Configurable homepage widgets
- [ ] Hide/show pages via options

---

## Stretch Goals

Items that may be implemented if there is demand, but are not prioritized.

- [ ] Job Execution Timeline — Gantt-like view for concurrent job execution + server workload distribution
- [ ] Visual cron builder component (interactive UI)
- [ ] Execution history per recurring job

---

## Milestone Targets

| Version | Scope | Status |
|---------|-------|--------|
| v0.1–v0.7 | Foundation (setup → grid standardization) | ✅ Done |
| v1.0 | Phase 1 complete — full parity + realtime | ✅ Done |
| v1.1 | Global search & advanced filters | ✅ Done |
| v1.2 | Razor Class Library conversion | ✅ Done |
| v1.3 | Performance insights | Planned |
| v2.0 | Phase 2 complete | Planned |
| v3.0 | Phase 3 — extensibility & integration | Planned |

---

## Contributing

See [CONTRIBUTING.md](../CONTRIBUTING.md) for guidelines on how to contribute to this project.
