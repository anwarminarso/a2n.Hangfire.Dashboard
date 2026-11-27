# Search on Large Instances

Back to the [README](../../README.md).

Name search and argument search (`args:`) are substring matches (`LIKE '%value%'` on SQL Server, `ILIKE '%value%'` on PostgreSQL). An ordinary index can't serve a pattern with a leading wildcard, so on a large `Job` table the cost of a search depends on how common the value is:

- **Common values are cheap.** Results come newest first by the job's primary key, so the database can stop as soon as a page is full. The total is counted up to 1,000 matches; beyond that the page shows **1,000+**, and only the first 1,000 can be paged through.
- **Rare values read the whole table.** Finding a handful of matches means checking every row. Adding a **state** or **date** filter narrows the rows that have to be checked.
- **Time limit.** A search is cancelled after `DashboardUIOptions.SearchTimeoutSeconds` (default 30; `0` leaves it to the storage's `Command Timeout`), and a running search can be cancelled from the page.

## Optional: trigram indexes on PostgreSQL

On PostgreSQL, [`pg_trgm`](https://www.postgresql.org/docs/current/pgtrgm.html) GIN indexes let `ILIKE '%value%'` use an index, which makes rare-value searches fast too. The dashboard doesn't create them. They are an opt-in for your DBA, since they take disk space and add a little write cost to every job insert. With the default `hangfire` schema:

```sql
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Argument search (args:)
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_hangfire_job_arguments_trgm
    ON hangfire.job USING gin ((arguments::text) gin_trgm_ops);

-- Name search (type and method name)
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_hangfire_job_invocationdata_trgm
    ON hangfire.job USING gin ((invocationdata::text) gin_trgm_ops);
```

`CONCURRENTLY` builds the index without blocking job processing, but it can't run inside a transaction. Trigram indexes only help once the searched value is at least 3 characters long. SQL Server has no equivalent for `LIKE '%value%'` short of Full-Text Search, which the dashboard doesn't use.

## Filter by exception

The **Exception** filter in **Advanced Filters** on the Search page finds every job that failed with one kind of failure, in any state: still failed, requeued and succeeded since, or deleted. It matches the `FailureFingerprint` parameter that `UseDashboardFailureFingerprintFilter()` stores (see [Failure fingerprints](../features/operations.md#failure-fingerprints)), with an exact comparison on the `JobParameter` table, so it is an index lookup per job rather than a substring match. It combines with every other filter, and with the 1,000-match count and newest-first paging above.

The list offers the exceptions the Failed page shows, labeled the same way on one line (short type, message, top frame and number of failed jobs), but only those with at least one stored fingerprint, since only those can match. It is loaded when the panel opens, under `SearchTimeoutSeconds`. When no failed job has a stored fingerprint, the list is disabled with a hint to register the filter. The filter can be linked, `search?exception=v1:…`, as the **Search all jobs with this exception** link on a Failed page group does; a fingerprint in the address that isn't in the list (every failed job of it was requeued, say) still shows as selected. A value that isn't a current fingerprint is ignored.

Failures recorded before the filter was registered, or on a server without it, carry no fingerprint, so Search can't find them; only the Failed page's grouping, which computes their fingerprint from the stored exception, knows them. On storages without the SQL Server or PostgreSQL adapter (Redis and others), the scan stops at 1,000 matches; for a rare exception it reads the parameter of every scanned job, one storage call each, until `SearchTimeoutSeconds`, and a search stopped that way shows as timed out. Every labeled exception is offered there, since stored and computed fingerprints can't be told apart.

On the Failed page, a group whose failures were all recorded without the filter still links to Search, since its requeued or deleted jobs may carry a fingerprint, but says that Search may not find them.
