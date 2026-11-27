# Job Dependency Graph

Back to the [README](../../README.md).

The Job Details page includes a continuation visualization for jobs created via `BackgroundJob.ContinueJobWith(...)`. The graph walks up to the root parent (via the `Awaiting` state's `ParentId`), then expands all descendants by parsing each job's `Continuations` parameter. Edge labels show the continuation condition (`on succeeded`, `on deleted`, `on any`).

The card only appears when a job is part of a continuation chain — standalone jobs render nothing. Each node is clickable and navigates to that job's details. Expired or deleted jobs render as dashed placeholders so the graph stays consistent.

Traversal is bounded by two options to keep page loads fast:

| Option | Default | Description |
|--------|---------|-------------|
| `JobGraphMaxDepth` | `5` | Maximum hops in either direction (ancestors or descendants) |
| `JobGraphMaxNodes` | `30` | Maximum total nodes materialized |

When either limit is hit, the card shows a `truncated` badge and a **Load more** button that doubles the node budget and adds depth (`+3`) on each click, up to a hard ceiling of 200 nodes / depth 12. Each click triggers exactly one storage round-trip per newly visited node, so expansion is incremental.
