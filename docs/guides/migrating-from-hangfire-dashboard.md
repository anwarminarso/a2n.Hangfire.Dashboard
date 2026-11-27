# Using with existing Hangfire code

Back to the [README](../../README.md).

## Console & tags compatibility

Already using `Hangfire.Console` or `Hangfire.Tags`? You can keep those packages, or switch to the bundled ones — job code and storage formats are compatible:

```csharp
// Works exactly the same
context.WriteLine("Processing order...");
context.WriteProgressBar();

[Tag("orders")]
public void ProcessOrder() { }
```

> **Don't reference `a2n.Hangfire.Console` together with the original `Hangfire.Console`** (or `a2n.Hangfire.Tags` with `Hangfire.Tags`). The bundled packages use the same namespaces so job code compiles unchanged, which means both packages in one project give ambiguous type errors. Pick one per project.

## Existing DashboardOptions? Still works.

```csharp
app.UseHangfireDashboardUI("/hangfire", new DashboardOptions
{
    DashboardTitle = "My Jobs",
    Authorization = new[] { new MyAuthFilter() },
    // Evaluated once per page load with the request's DashboardContext (since 2.6.0).
    IsReadOnlyFunc = context => !context.GetHttpContext().User.IsInRole("HangfireAdmin"),
});
```

Your Hangfire authorization filters and `IsReadOnlyFunc` receive a `DashboardContext` carrying this exact `DashboardOptions` instance. `IsReadOnlyFunc` is evaluated once when a page loads; the result holds for that tab until it is reloaded. If the function throws, the page is read-only and a warning is logged.

## Data compatibility

Reads the same storage format as [Hangfire.Console](https://github.com/pieceofsummer/Hangfire.Console) and [Hangfire.Tags](https://github.com/face-it/Hangfire.Tags). Existing console logs and tags are visible immediately — no migration needed.
