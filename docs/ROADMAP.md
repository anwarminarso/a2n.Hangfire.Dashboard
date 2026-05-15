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

### 2.2 Razor Class Library Conversion
- [ ] Convert from web application (exe) to Razor Class Library (dll)
- [ ] Static assets served via `_content/a2n.Hangfire.Dashboard/`
- [ ] True NuGet drop-in: add package reference + 2 extension method calls

### 2.3 Notifications & Alerts
- [ ] Browser push notifications
- [ ] Webhook notifications (Slack, Teams, Discord, generic HTTP)
- [ ] Configurable alert rules
- [ ] Notification history log

### 2.4 Performance Insights
- [ ] Top N slowest jobs
- [ ] Queue throughput (jobs/minute, jobs/hour)
- [ ] Server utilization (worker busy %)
- [ ] Job duration trend (per job type over time)

### 2.5 Job Execution Timeline
- [ ] Gantt-like view: concurrent job execution
- [ ] Server workload distribution

### 2.6 Enhanced Job Details
- [ ] Job dependency graph (continuations visualized)
- [ ] Retry history with diff
- [ ] Job execution duration chart (historical)

### 2.7 Standalone Deployment
- [ ] Deploy dashboard as separate application
- [ ] Connect to Hangfire storage directly (connection string config)

---

## Phase 3 — Enterprise Features

**Goal**: Features for teams and production environments.

### 3.1 Multi-Environment
- [ ] Single dashboard, multiple storage connections
- [ ] Environment switcher (Production, Staging, Dev)

### 3.2 Role-Based Access Control
- [ ] Roles: Viewer, Operator, Admin
- [ ] Integration: Azure AD, OAuth2, OpenID Connect
- [ ] Audit log

### 3.3 API & Integration
- [ ] REST API for all dashboard data
- [ ] Prometheus metrics endpoint
- [ ] Export to CSV/JSON
- [ ] Webhook on events

### 3.4 Customization
- [ ] Theming (custom colors, logo)
- [ ] Configurable homepage widgets
- [ ] Hide/show pages per role

---

## Milestone Targets

| Version | Scope | Status |
|---------|-------|--------|
| v0.1–v0.7 | Foundation (setup → grid standardization) | ✅ Done |
| v1.0 | Phase 1 complete — full parity + realtime | ✅ Done |
| v1.1 | Global search & advanced filters | ✅ Done |
| v1.2 | Razor Class Library conversion | Planned |
| v1.3 | Notifications & alerts | Planned |
| v2.0 | Phase 2 complete | Planned |
| v3.0 | Phase 3 — enterprise features | Planned |

---

## Contributing

See [CONTRIBUTING.md](../CONTRIBUTING.md) for guidelines on how to contribute to this project.
