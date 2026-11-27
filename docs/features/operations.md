# Operations

Back to the [README](../../README.md).

The dashboard ships day-to-day operational controls so ops teams don't need to redeploy or write SQL to handle common scenarios.

## Queue pause / resume

The `/queues` page lists every queue with its current status and a per-row pause toggle. Paused queues stop workers from executing jobs — the dashboard's server filter intercepts each fetched job and reschedules it back into the future (default +30s) instead of running it.

```csharp
// Required: register the server filter on the Hangfire pipeline so running servers
// honour the pause toggles set from the dashboard.
builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connStr)
    .UseDashboardQueuePauseFilter());          // <-- here
```

Without this call, the dashboard still records the pause and shows the badge — but jobs keep executing on running workers until the host restarts with the filter enabled.

Customize behaviour via `DashboardUIOptions.QueueOperations`:

```csharp
QueueOperations = new QueueOperationsOptions
{
    Enabled         = true,
    Behavior        = PausedJobBehavior.Reschedule,   // or Requeue (busy-loop, use sparingly)
    RescheduleDelay = TimeSpan.FromSeconds(30),
    PauseStateCacheTtl = TimeSpan.FromSeconds(2),     // server-side cache window
}
```

## Maintenance mode

A single global toggle (top of the `/queues` page) pauses every queue at once. While maintenance is active, a persistent yellow banner is rendered on every dashboard page with the operator's reason and a `Manage →` link. Disable maintenance to resume — individual queue pauses set before maintenance was enabled remain in effect.

## Failure fingerprints

An optional server filter records which kind of failure each failed job had, so failed jobs can be grouped by exception. Register it on every host that runs a Hangfire server:

```csharp
builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connStr)
    .UseDashboardFailureFingerprintFilter());   // <-- here
```

When a job enters the Failed state, the filter stores a `FailureFingerprint` job parameter such as `v1:5a633710a9709394`, which is a hash of three things:

- **The exception type.** When the exception wraps others, the innermost exception's type is included and its message is used. EF Core, for example, wraps every database error in a `DbUpdateException` with the same message, so without this `Column 'Email' cannot be null` and `Column 'Phone' cannot be null` would look identical. Generic arguments are dropped from type names because they carry assembly versions.
- **The first line of the message, with run-specific values replaced.** GUIDs, dates and times, hex values and hashes, e-mail addresses, IPv6 and IPv4 addresses, URL query strings and numbers become placeholders, so `Transaction (Process ID 57) was deadlocked…` and `Transaction (Process ID 112) was deadlocked…` are the same failure. Quoted names are kept, so `Column 'Email' cannot be null` and `Column 'Phone' cannot be null` stay apart.
- **The top stack frame from your own code.** This is the first frame in the root namespace of the job's type, e.g. `MyApp` for `MyApp.Jobs.OrderJob`, so the same database error from two jobs doesn't collapse into one group at a shared driver frame. If no frame is in that namespace, the first frame outside the framework and common libraries is used (`System`, `Microsoft`, `Hangfire`, `Npgsql`, `Dapper`, `Newtonsoft`, `Polly`, `StackExchange`, `Azure`, `Amazon` and others; the full list is `FailureFingerprint.LibraryNamespaces`). Parameters, file paths and line numbers are dropped, and async, lambda and local-function names map to the method that contains them, so a rebuild or a moved line doesn't change the fingerprint.

Hangfire keeps only the first 100 lines of a failure's exception details. In a very deep stack, such as a long async chain through framework code, your own frames can fall past that limit. The fingerprint then uses the first remaining frame outside those libraries, or the first frame.

Only failures that end in the Failed state are fingerprinted, not the attempts that `AutomaticRetry` retries. The parameter stays on the job after it is requeued or deleted, and a later failure overwrites it. It is stored as a plain string and is visible under **Parameters** on the Job Details page.

### Grouping on the Failed page

The **Exception** filter next to the page-size selector on **Jobs → Failed** groups the failed jobs by fingerprint, most failed jobs first. Each entry shows the short exception type, as `Outer → Inner` when one exception wraps another (full names in the tooltip), the normalized message, the top frame and the number of failed jobs. Selecting one shows that exception's total and the jobs that failed most recently; the page-size selector sets how many. A job that was scheduled, retried or requeued before it failed is placed by when it failed, not when it was created, on every storage. A group can be linked: the address becomes `jobs/failed?exception=v1:…`. The filter works from the keyboard: the arrow keys, Home and End move through the list, Enter or Space selects, and Escape closes it.

"All exceptions" is the default and shows the page as before. The groups are only read when the filter is opened or a group is linked. While a group is shown or the list is open, they are refreshed when the failed count changes; otherwise they are read again the next time the filter is opened. Grouping is stopped after `DashboardUIOptions.SearchTimeoutSeconds`, like a search.

Fingerprints stored by the filter are counted exactly, by a query over the `Job` and `JobParameter` tables. Failures without one (recorded before the filter was registered, or on a server without it) are fingerprinted from their stored exception data and job type when the page is read. Only the 1,000 that failed most recently are read. When there are more, the page says so, the counts are shown as lower bounds (`1,000+`), and older failures may be missing from the list. Registering the filter on every server gives exact counts. Nothing is written back, so grouping also works in read-only mode.

In a deployment where only some servers register the filter, a job keeps the fingerprint of the last failure recorded by a server with the filter. If it is requeued and then fails on a server without it, it is still counted under that earlier failure.

Only the 50 largest groups are labeled, each from the job that failed last. Storages without the SQL Server or PostgreSQL adapter (Redis and others) have no fast way to query job parameters. On those, the latest 1,000 failed jobs are fingerprinted in memory, with or without the filter, so the page doesn't suggest the filter when counts there are lower bounds. A custom `IStorageQueryProvider` doesn't offer the filter unless it implements `SupportsFailureGrouping`, `GetFailureGroupsAsync` and `GetFailureGroupJobsAsync`. It can also implement `GetFailureGroupJobIdsAsync` for the group actions below; by default that takes the ids from `GetFailureGroupJobsAsync`.

### Requeue or delete a whole group

With a group selected, **Requeue all** and **Delete all** act on every failed job with that exception, not only the ones on screen. When you click one, the page reads the ids of the group's jobs and asks for confirmation with their exact number. Both actions ask, because requeuing thousands of jobs has an impact too. The confirmation opens with focus on **Cancel**, and Escape cancels it. Only those jobs are changed, and each one only while it is still in the Failed state. A job that was retried, requeued or deleted in the meantime is skipped, and one that fails with the same exception after the click is left alone.

The jobs are changed 500 at a time. Each batch is one `jobs.requeued` or `jobs.deleted` entry in the audit log, with the requested and succeeded counts and the group's fingerprint. While it runs, the page shows how far it has got ("Requeued 1,500 of 12,345") and the buttons are disabled. **Cancel**, or leaving the page, stops it after the current batch. At the end the page says how many jobs were changed and how many were skipped because they had left Failed, then reloads the groups and the list. If a state change fails, for example because the storage is unreachable, the run stops there and the page says exactly how many jobs were changed; the batch's audit entry records them too.

The ids come from the same data as the counts: every failed job with the stored fingerprint, plus the jobs without one that the grouping read. When the counts are lower bounds, older failures that weren't read are not included, and the confirmation says so; registering the filter on every server avoids that. The group actions are hidden when `DashboardUIOptions.IsReadOnly` is set.

The rows of a group can also be selected and requeued or deleted with **Requeue** and **Delete**, as on the full list. On the Failed page those buttons also change only jobs that are still failed. A requeued or deleted job keeps its `FailureFingerprint` parameter.

### Search by exception

Because the parameter survives requeues and deletes, the [Exception filter on Search](../guides/search-on-large-instances.md#filter-by-exception) finds every job that failed with a given exception, whatever its state now. **Search all jobs with this exception** on a Failed page group opens that search.

## Audit log

Every admin action performed through the dashboard is recorded:

| Category | Actions |
|----------|---------|
| Job actions | requeue, delete, batch requeue, batch delete |
| Recurring | create, update, delete, trigger, stop, start |
| Queue ops | queue pause, queue resume, maintenance enabled, maintenance disabled |

Each entry captures: timestamp (UTC), user (or `(anonymous)` for unauthenticated local requests), client IP, action, target, optional reason, and a small metadata bag (e.g., batch counts).

The `/audit` page filters by action prefix (job/jobs/recurring/queue/maintenance), user (substring), and target, with an items-per-page selector and paged navigation. Storage uses Hangfire's KV primitives — no schema changes. Configurable retention:

```csharp
AuditLog = new AuditLogOptions
{
    Enabled    = true,
    Retention  = TimeSpan.FromDays(30),
    MaxEntries = 10_000,
}
```

Old entries beyond either bound are trimmed on writes (best-effort, ~every 50 entries).
