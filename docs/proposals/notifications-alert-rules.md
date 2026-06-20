# Proposal (Deferred): Notifications & Alert Rules

> **Status: Deferred to Stretch / Backlog.**
> Originally scoped as **v2.5**. Moved out of the active roadmap because it is **not
> mandatory**, has **no concrete user demand** yet, and is **largely superseded by the
> v2.6 Prometheus `/metrics` endpoint** (which feeds the alerting stacks most teams already
> run — Grafana Alertmanager, etc.).
>
> This document preserves the full original plan so the design work is not lost. It will be
> revisited under the same demand-driven rule applied to other backlog items: **reconsider
> when 5+ users explicitly request it.**

---

## Rationale for deferring

- **Not mandatory.** The dashboard is fully functional without it; alerting is a "nice to
  have", not a correctness or parity gap.
- **No demand signal.** No issues/requests asking for built-in notifications at the time of
  deferral.
- **Superseded by v2.6.** A Prometheus `/metrics` endpoint gives a stronger, standard
  alerting path (failure rate, queue depth, server count, job duration histograms) via tools
  teams already operate. Building a bespoke notification engine would duplicate that.
- **Highest maintenance surface on the roadmap.** 5 channels × 8 triggers + processor +
  template engine + 2 UI pages + history is a subsystem, not a feature. Each channel is a
  long-term liability (Teams deprecated legacy webhooks, Slack payload changes, SMTP/TLS, …).
- **Raises security/operational risk.** Credential storage, outbound HTTP carrying job data,
  and alert duplication/spam under multi-instance deployments.

If revived, the segment it serves best is **small/medium teams without an existing
observability stack** — the inverse of the segment for which the Gantt timeline was skipped.

---

## Original v2.5 plan (preserved verbatim, with notes)

**Goal**: Alert the right channel when something goes wrong, without polling the dashboard.
Granular plan replacing the original single-bullet "Webhook notifications".

- [ ] `NotificationRule` model + storage (Hangfire hash/set, no schema changes)
- [ ] `INotificationChannel` abstraction
- [ ] Built-in channels: Slack, Microsoft Teams, Discord, generic HTTP webhook, SMTP email
- [ ] Eight built-in trigger types:
  - [ ] Failure count (>N failed in last X minutes)
  - [ ] Failure rate (>N% failed in last X minutes)
  - [ ] Stuck processing (single job processing >X minutes)
  - [ ] Queue depth (queue Y has >N enqueued)
  - [ ] Server offline (no heartbeat for X seconds)
  - [ ] Recurring missed (recurring job not fired in expected window)
  - [ ] Specific exception (job throws exception matching regex)
  - [ ] Long-running job (single job duration >X minutes)
- [ ] `NotificationRuleProcessor` background service (poll, evaluate, dispatch with cooldown)
- [ ] Mustache-style message template engine (`{count}`, `{topException}`, `{dashboardUrl}`, ...)
- [ ] Per-rule cooldown (default 15 min) to prevent alert spam
- [ ] Dashboard pages: `/notifications` (rules CRUD list) and rule editor with live preview
- [ ] "Test webhook" button (dry-run send with sample payload)
- [ ] Notification history page (last N fires, success/failure)

---

## Design notes captured during discussion

These are the implementation decisions worth keeping if the feature is ever revived. They map
the plan onto patterns that already exist in the codebase.

### Where the processor runs (most important decision)
Unlike `AnalyticsBroadcastService` (which only pushes to currently-open browsers), alert
evaluation must run **even when nobody has the dashboard open**, and must **not** fire N times
in an N-instance deployment.

- **Recommended:** run evaluation as a **recurring Hangfire job** (e.g. every minute) rather
  than a plain `BackgroundService`. Hangfire guarantees a single execution per interval across
  all servers, which solves the multi-instance duplication problem natively.
- A plain `BackgroundService` would run in every host referencing the package → duplicate
  alerts.

### Cooldown / dedup
Store cooldown state in shared storage (e.g. hash `notif:rule:{id}`, field `lastFiredAt`), not
in-memory, so it is consistent across instances. Default cooldown 15 min.

### Storage layout (no schema changes)
Mirror the existing `AuditLogService` / `QueueOperationsService` patterns:
- Rules: set `notif:rules` (rule ids) + hash `notif:rule:{id}` (rule payload).
- History: sorted set `notif:history` keyed by UTC ticks + hash `notif:history:{id}`, with
  retention/trim like the audit log.

### Channels vs credentials (security)
- Define **channel connections** (webhook URLs, SMTP host/port/user/password) in
  code/`appsettings`/DI — **not** in Hangfire storage — to avoid plaintext secrets in the DB.
- Store only the **rule** (trigger + thresholds + channel reference + message template) in
  storage so it can be edited from the UI.
- Slack/Teams/Discord are effectively "a webhook with a specific payload shape" — a single
  generic webhook channel + templating can cover them initially without 5 separate classes.

### Trigger data sources
- Cheap (reuse `IStorageMetricsProvider` / Monitoring API): failure count, failure rate,
  queue depth, server offline, long-running, stuck processing.
- Expensive / needs care: "specific exception (regex)" (would scan failed-job details — bound
  to the most recent N) and "recurring missed".

### Outbound HTTP = security posture
- Default the whole feature **off**.
- `IsReadOnly` mode should disable sending and the "Test webhook" action.
- Decide payload redaction (what job data is allowed to leave the process).

---

## Suggested MVP if revived (ramped, not all-at-once)

1. Model + storage (`NotificationRule`, history) — Audit Log pattern.
2. `INotificationChannel` + **one** channel (generic webhook) for end-to-end validation.
3. Processor as a **recurring job** + shared cooldown lock.
4. Three cheap triggers first: failure count, queue depth, server offline.
5. UI: list/editor + "Test" + history.
6. Everything else (extra channels, expensive triggers) as follow-ups driven by real demand.
