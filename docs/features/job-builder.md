# Job Builder

Back to the [README](../../README.md).

The Job Builder lets operators construct, schedule, and enqueue Hangfire jobs **with their arguments** from the dashboard — no code change or redeploy. It powers two places:

- **Recurring** — the Create/Edit recurring job form (`/recurring`), gated behind `EnableJobManagement`.
- **Enqueue** — a new one-off job page at `/jobs/enqueue` for fire-and-forget jobs, also gated behind `EnableJobManagement`.

Both share the same method picker, parameter form, and argument conversion, so behavior is identical across the two.

## Method discovery & selection

The picker offers two sources:

- **Registered methods** — discovered by scanning loaded assemblies for `public` instance/static methods whose method or declaring class carries a recognized attribute (`JobDisplayName`, `Tag`, or `Queue`). Discovery is cached for the dashboard's lifetime and resilient to assemblies that fail to load. Interface and abstract-class contracts are surfaced alongside their concrete implementations, each badged as **Contract** or **Implementation**, and the list is searchable by display label, type name, or method name.
- **Custom methods** — a full type name + method name typed by hand, validated on demand. This is **opt-in** and disabled by default:

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    AllowArbitraryMethodInvocation = true,   // default false — keeps arbitrary invocation opt-in
});
```

Resolution is **overload-safe**: when a method name has multiple overloads, the single overload whose parameter count and types match the supplied arguments is selected. Ambiguous, missing, or non-matching methods are rejected with an identifying error and never touch storage.

## Parameter form

A type-aware form is generated from the method signature, one control per parameter:

| Parameter type | Control |
|----------------|---------|
| `string` | text input |
| integers / floats | number input (whole vs. fractional, range-checked) |
| `DateOnly` / `TimeOnly` / `DateTime` / `DateTimeOffset` | date / time / datetime-local picker |
| `Guid` | text input |
| `bool` / `bool?` | checkbox / tri-state (unset = null) |
| enum / `[Flags]` enum | single-select / multi-select |
| scalar arrays (`T[]`) | add/remove list |
| nested object (class, depth ≤ 5) | collapsed placeholder + explicit "Add" to instantiate |
| anything else / depth > 5 | JSON textarea |

Key behaviors:

- **Form ⇄ JSON toggle** — switch to a raw `["value", 42]` JSON textarea at any time; the form and JSON stay in sync (a read-only JSON mirror is shown alongside the form).
- **Typed conversion** — values are converted to each parameter's declared CLR type before the job is stored, matching how Hangfire serializes arguments.
- **Injected parameters skipped** — `PerformContext`, `IJobCancellationToken`, and `CancellationToken` are excluded from the form and filled by Hangfire at runtime.
- **Empty fields are total** — a blank field resolves to `null` for nullable types or `default(T)` for non-nullable types, never an error.
- **Edit pre-fill** — when editing an existing recurring job, current argument values are loaded back into the form.

## Schedule builder (recurring only)

For recurring jobs, the Schedule Builder offers a field-by-field cron editor (minute, hour, day-of-month, month, day-of-week) with an **Every / Specific / Range / Step** mode per field, plus a manual cron input. It shows a human-readable description and the **next occurrence** in the selected time zone (UTC when none is chosen). Unparseable expressions are flagged and block submission. (Cron parsing uses Cronos, already bundled with Hangfire — no new dependency.)

## Queue handling

The queue control is editable with suggestions from current queues (defaulting to `default`). When the target method or its declaring class carries a `[Queue(...)]` attribute, the control becomes read-only and shows a precedence notice, because Hangfire's `QueueAttribute` overrides the stored queue at state election.

## Gating

- **Read-only mode** (`IsReadOnly = true`) — a persistent banner is shown and the submit controls are disabled. Read-only mode applies to the whole dashboard, not only the builder: see `IsReadOnly` in [Full Setup](../../README.md#full-setup).
- **Job management** (`EnableJobManagement = false`) — the recurring create/edit builder is hidden, the navigation no longer links to the Enqueue page, and the `/jobs/enqueue` route returns Not Found (read-only gating still applies). Defaults to `true`.
- **Arbitrary methods** (`AllowArbitraryMethodInvocation = false`, default) — only discovered registered methods may be selected.
