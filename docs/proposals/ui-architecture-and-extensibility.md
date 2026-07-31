# Proposal: UI Architecture & Extensibility

> **Mixed status.** This document covers four related topics that surfaced from a single
> question ("could we build a React SPA version of this dashboard?"). They are recorded
> together because the answer to each depends on the others, but they carry **different
> statuses** and should not be read as one decision:
>
> | Part | Topic | Status |
> |------|-------|--------|
> | 1 | Extension surface (third-party pages & nav items) | **Recommended for the active roadmap** |
> | 2 | `a2n.Hangfire.Dashboard.Core` package split | Design notes — not decided |
> | 3 | REST API scope adjustments (v2.6) | Recommended roadmap amendment |
> | 4 | React SPA UI | **Deferred** — revisit conditions below |
> | 5 | Blazor WebAssembly as a middle option | Parked — worth a spike, not a commitment |
>
> Parts 2, 4, and 5 are analysis, not validated user demand. Part 1 is the exception: its
> evidence is verifiable in the repository today (see the verification appendix).

---

## Summary

A React SPA rewrite is **technically feasible** — the service layer is already UI-agnostic —
but it should not be pursued as a replacement for the Blazor UI. The deciding factor is not
the cost of rewriting ~65 Razor components. It is that **users of this dashboard extend it
rather than restyle it**, and an embedded JavaScript bundle is structurally hostile to
extension by .NET developers, while a Razor Class Library is the natural home for it.

That reframing exposed a larger gap: **this dashboard currently has no extension surface at
all**, and third-party extensions written against Hangfire's built-in dashboard fail
*silently* when the package is swapped in. That is a higher-value problem than the SPA
question and it is not on the roadmap.

The package split the SPA question originally motivated (`.Core` + UI packages) remains
worth doing — its justification simply moves from "enable a second UI" to "give extension
authors something to reference that isn't the whole UI".

---

## Context

The original question was whether the dashboard could be re-implemented as a
`.Core` / `.BlazorUI` / `.SPAUI` triad, with the SPA built in React. Investigation of the
codebase produced arguments in both directions; the deciding input arrived mid-discussion:

> Users of this dashboard do not customize its UI. They add features — the same pattern seen
> around Hangfire's built-in dashboard, where developers are either plain consumers or they
> add capabilities Hangfire itself lacks.

That statement inverts the weighting of every argument below, which is why Part 1 is ranked
above Part 4 despite Part 4 being the original question.

---

## Part 1 — Extension surface (recommended)

### The gap

Hangfire's built-in dashboard is extensible through two public statics in `Hangfire.Core`:

```csharp
Hangfire.Dashboard.DashboardRoutes.Routes.AddRazorPage(route, factory);
Hangfire.Dashboard.NavigationMenu.Items.Add(page => new MenuItem(title, url));
```

This is how the plugin ecosystem around it exists. Both extension points are used by
packages kept as reference folders in this workspace:

- `Hangfire.RecurringJobAdmin/src/Hangfire.RecurringJobAdmin/ConfigurationExtensions.cs`
- `Hangfire.Community.Dashboard.Heatmap/src/GlobalConfigurationExtension.cs`

`a2n.Hangfire.Dashboard` provides **no equivalent**. A repository-wide search for
`AdditionalAssemblies`, `NavigationMenu`, `CustomPage`, `MenuItems`, and `NavItems` returns
zero hits inside `src/a2n.*`. Concretely:

- `Components/DashboardRoutes.razor` binds the router to a single assembly:
  `<Router AppAssembly="typeof(DashboardApp).Assembly">`
- `Components/DashboardApp.razor` and `Components/DashboardRoutes.razor` are both marked
  `[EditorBrowsable(EditorBrowsableState.Never)]` — deliberately hidden from consumers
- `Components/Layout/NavMenu.razor` hardcodes its items

The gap is plausibly invisible from the inside: `a2n.Hangfire.Console` and
`a2n.Hangfire.Tags` are consumed as `ProjectReference`, so the project's own extension needs
have always been met by compiling in.

### The silent-failure problem

This is the sharper half of the gap, and it is arguably a defect rather than a missing
feature.

`a2n.Hangfire.Dashboard.csproj` references `Hangfire.Core 1.8.*` and
`Hangfire.AspNetCore 1.8.*`. Therefore `NavigationMenu.Items` and `DashboardRoutes.Routes`
still exist and still accept registrations at runtime — but nothing in this dashboard ever
reads them.

The consequence for the exact audience described above: a developer who swapped the built-in
dashboard for this one, and who has custom dashboard pages or a third-party plugin, will find
that their code **compiles, runs, registers, and then does nothing**. No exception, no log
entry, no missing-page error. A silent no-op is worse than a build break, because a build
break is discovered before deployment.

This directly undercuts roadmap principle #1 ("Drop-in replacement — swap the NuGet package,
zero code changes").

### Proposed work

Minimum viable extension surface:

- [ ] **`AddAdditionalAssemblies(params Assembly[])`** on the options/builder, forwarded to
      both `<Router AppAssembly=... AdditionalAssemblies=...>` in `DashboardRoutes.razor` and
      `MapRazorComponents<DashboardApp>().AddAdditionalAssemblies(...)` in
      `HangfireDashboardUIExtensions.UseHangfireDashboardUI`. Without this, third-party pages
      cannot be routed at all.
- [ ] **Nav item registry** on `DashboardUIOptions` — label, icon (Bootstrap Icons class),
      route, parent group, and a visibility predicate. Functional equivalent of
      `NavigationMenu.Items`, consumed by `NavMenu.razor` / `NavMenuGroup.razor`.
- [ ] **Publish the consumer contract** — remove `[EditorBrowsable(Never)]` where extension
      authors need access, and document `MainLayout`, `HangfireMonitorService`,
      `IStorageQueryProvider`, and `IStorageMetricsProvider` as the stable extension API.
      Decide explicitly what is *not* stable.
- [ ] **`samples/SampleAppExtension`** — a separate RCL contributing one page plus one nav
      item, referenced by a host app. This is what proves the seam works, and doubles as
      living documentation.
- [ ] **Detect legacy registrations** — at startup, compare `NavigationMenu.Items` and
      `DashboardRoutes.Routes` against Hangfire's defaults; if entries were added, emit a
      warning (log, and optionally a dashboard banner) explaining that legacy dashboard
      extensions are not rendered and pointing at the new mechanism. Cheap, and it converts a
      silent failure into a diagnosable one.

Stretch, requires a feasibility spike:

- [ ] **Render legacy Hangfire extension pages.** Rendering legacy *nav items* is cheap.
      Rendering legacy *pages* is harder: `IDashboardDispatcher` implementations emit
      Hangfire's own HTML and CSS class names, which will not inherit this dashboard's
      Bootstrap 5 theme. Options range from an isolated iframe-style host to a compatibility
      stylesheet. Not yet validated — if it works, the drop-in story becomes materially
      stronger than it is today.

### Why this ranks first

It is comparatively cheap, it can ship as a minor release with no breaking change, it
directly serves the observed demand pattern, and it closes a silent-failure path in the
project's headline promise. Extension surfaces also compound: each extension package that
exists makes the ecosystem more valuable and, notably, raises the cost of ever switching UI
technology — which is itself an argument for settling Part 1 before Part 4.

---

## Part 2 — `a2n.Hangfire.Dashboard.Core` split (design notes)

### Where the seam already is

The service layer is genuinely UI-agnostic: `Services/` (22 files), `Heatmap/` (13),
`Internal/`, `Helpers/`, `Interfaces/`, `Models/`, `Security/`, `Storage/` contain no Razor
dependency. `Hubs/DashboardHub` is plain SignalR — its only surface is
`SubscribeToMetrics` / `SubscribeToAnalytics` plus group broadcast, with no Blazor coupling.

`UseHangfireDashboardUI` mixes shareable and Blazor-specific concerns in one method:

| Shareable → `.Core` | Blazor-specific → UI package |
|---|---|
| `branch.UseWebSockets()` | `FrameworkScriptMiddleware` (serves `_framework/blazor.web.js`) |
| `DashboardMiddleware` (auth, antiforgery, `_content/*`) | `MapRazorComponents<DashboardApp>().AddInteractiveServerRenderMode()` |
| `MapHub<DashboardHub>("/hubs/dashboard")` | `AddRazorComponents().AddInteractiveServerComponents()` |
| All service registrations, storage adapters, hosted broadcast services | `HtmlShellRenderer` / `DashboardApp.razor` (hardcodes Blazor + Bootstrap + Chart.js tags) |

### Two places that need new abstractions

These are not simple file moves.

**Shell rendering.** `Middleware/HtmlShellRenderer.cs` and `Components/DashboardApp.razor`
both hand-write a complete HTML document including
`<script src=".../_framework/blazor.web.js">`. Each UI package must supply its own shell.

**Asset serving.** `Middleware/EmbeddedResourceDispatcher.cs` binds assets to a single
assembly:

```csharp
private static readonly Assembly ResourceAssembly = typeof(EmbeddedResourceDispatcher).Assembly;
```

`ResourceRegistry` is likewise static. Both must become assembly-aware, since
`<EmbeddedResource Include="Content\**\*" />` would move into each UI package. Note this is
*also* a prerequisite for Part 1 if extension packages are to ship their own CSS/JS.

Suggested seam, registered by whichever UI package is installed:

```csharp
public interface IDashboardUiProvider
{
    Task RenderShellAsync(HttpContext context, string pathPrefix, DashboardUIOptions options);
    Assembly AssetAssembly { get; }                        // source of EmbeddedResource Content/**
    void ConfigureBranch(IApplicationBuilder branch);      // Blazor: FrameworkScriptMiddleware
    void MapEndpoints(IEndpointRouteBuilder endpoints);    // Blazor: MapRazorComponents
}
```

### Packaging constraints

**Do not repurpose the `a2n.Hangfire.Dashboard` package ID as the core.** It is already
published and consumed as:

```csharp
services.AddHangfireDashboardUI();
app.UseHangfireDashboardUI("/hangfire");
```

Both methods are Blazor-specific — `AddHangfireDashboardUI` calls
`AddRazorComponents().AddInteractiveServerComponents()`, and `UseHangfireDashboardUI` calls
`MapRazorComponents<DashboardApp>()`. Making that package core-only turns a version bump into
a compile error for every existing consumer, violating roadmap principle #1.

Two safe options:

1. **Metapackage** — `a2n.Hangfire.Dashboard` becomes an empty package depending on `.Core`
   plus the Blazor UI package. Existing consumers change nothing. Structurally honest;
   slightly more moving parts.
2. **Leave it as the Blazor package** — which is what it already contains — and introduce
   `.Core` as the new artifact. The breaking change is aimed at a new name; the published
   identity is untouched. Cheapest, zero risk.

**Distinguish the extension method names.** If two UI packages both declare
`UseHangfireDashboardUI` in namespace `Microsoft.AspNetCore.Builder`, a host that references
both gets an ambiguous-call error it cannot resolve without an alias. Use
`UseHangfireDashboardBlazorUI` / `UseHangfireDashboardSpaUI` and keep the original name only
in the Blazor package as a back-compat alias.

**Do not split `DashboardUIOptions`.** Separating UI properties (`DefaultTheme`,
`FaviconPath`, `DashboardTitle`) from policy properties (`Authorization`, `LoginPath`,
`IsReadOnly`, `EnableJobManagement`, `SourceLink`, `HealthCheckThresholds`) is tempting but
forces consumers to configure two objects. Keep one class in `.Core`; a UI that cannot honor
a property simply ignores it. Policy properties are already enforced in the service layer —
`HangfireMonitorService` receives the options in its constructor.

**Naming.** Prefer `a2n.Hangfire.Dashboard.Blazor` over `.BlazorUI`, and `.Spa` over
`.SPAUI`. Package IDs are permanent, and `.Blazor` survives the arrival of a WebAssembly
variant.

### Independent justification

Even if no second UI is ever built, the split has value: **third-party extension packages
should reference `.Core`, not a UI package.** Otherwise every extension drags in the entire
UI and its transitive dependencies. This is a stronger justification than the one that
originally motivated the split.

---

## Part 3 — REST API scope (roadmap amendment)

Already planned for v2.6 as "REST API (read-only first, optional package)". Two adjustments
are recommended.

**Raise the scope beyond read-only.** Include commands — requeue, delete, batch operations,
queue pause/resume, maintenance toggle, recurring CRUD, enqueue — plus a parameter-schema
endpoint for the Job Builder. Read-only is not sufficient for any serious consumer, and the
command surface is where the contract design decisions actually live.

**Put the REST API in `.Core`, not in a UI package.** If the API ships inside an SPA package,
there are two data paths to test and they will drift. A concrete example already in the
codebase: `AuditActorAccessor` is registered scoped specifically because Blazor circuit
actions have no `HttpContext`, while a REST path always has one. Audit attribution would
behave differently on each path unless the logic has a single home.

Consequences worth recording: a well-scoped REST API also unblocks several backlog items —
`hangfire-cli`, CSV/JSON export (already v2.6), Grafana and automation integrations, and
multi-instance federation. One piece of work, several backlog items moving.

**Housekeeping.** `src/a2n.Hangfire.Dashboard.RestApi/` and
`src/a2n.Hangfire.Dashboard.OpenTelemetry/` currently contain only `bin/` and `obj/` — no
`.csproj`, and neither appears in `src/Hangfire Dashboard.slnx`. Leftover scaffolding from an
abandoned start; either remove or resume.

---

## Part 4 — React SPA (deferred)

### Rationale for deferring

**The demand is extension, and SPA is hostile to it.** Compare how a .NET developer adds a
page:

- *Blazor RCL* — ships their own RCL with `@page` components, references it, registers the
  assembly. They get C#, DI, the same services, the same Bootstrap theme. Idiomatic .NET;
  nothing new to learn.
- *React SPA* — their code must end up inside a bundle that lives inside a NuGet DLL. Either
  they fork and rebuild with npm (in a .NET shop, effectively "no"), or the project builds a
  runtime plugin loader (module federation or a remote-JS registry) — a large, fragile
  investment. Either way the developer writes TypeScript.

The asymmetry is permanent and it compounds. Choosing SPA means choosing an architecture that
works against the primary demand.

**The cost is a full presentation-layer rewrite.** Approximately 35 page/view components,
27 shared components, and 3 layout components. If both UIs were then maintained in parallel,
that is a permanent 2× UI tax on a project with an active roadmap through v3.0.

**Type sharing stops being free.** `Models/` DTOs are consumed directly by components today.
An SPA requires TypeScript types mirroring the C# DTOs, or OpenAPI-driven generation — extra
machinery and an extra failure mode.

**Reflection-driven features get expensive.** `JobMethodResolver`, `ParameterInputMapper`, and
`JobArgumentConverter` generate forms from CLR types (nested objects, enum flags, tri-state
nullable bool). Direct in Blazor; in an SPA this becomes a JSON schema contract that must be
designed and versioned.

**Part of the test investment is lost.** The 25 FsCheck properties over pure logic survive —
they belong to `.Core`. The bUnit component tests (MethodPicker, ParameterBuilder,
ScheduleBuilder, JobBuilder, RecurringEditor) would be rewritten in Vitest/Playwright.

**Attack surface grows.** Blazor Server never exposes a public data API. A REST API means auth
on every endpoint and rate-limiting considerations — immediately after the v2.2.1 auth
hardening work.

**New toolchain.** npm, a bundler, lockfile churn, and Node in CI, for a project whose
audience and contributor base is .NET.

### The case *for* an SPA (preserved — it is not weak)

Recorded honestly, because these are real and remain unaddressed by deferring.

**A whole class of bugs would disappear.** Three of the four most recent patch releases fixed
Blazor runtime problems rather than dashboard features:

- **#23 (v2.5.1)** — reading `localStorage` returned JavaScript `null`; deserializing it into
  `bool?` threw `InvalidCastException`, the uncaught `JSException` tore down the circuit, and
  every sub-page became unreachable on a fresh session.
- **#20 (v2.4.3)** — persisted theme lost across Blazor enhanced navigation; required a
  `MutationObserver` guard to restore `data-bs-theme`.
- **#19 (v2.4.3)** — characters dropped while typing quickly in the recurring filter; fixed by
  making the input uncontrolled. Not a bug so much as a symptom of the round-trip model.

None of these have an analogue in a client-rendered app.

**Per-viewer server cost.** Blazor Server holds a component tree and state in server memory
for every open tab. A dashboard is precisely the page teams pin to a wall monitor all day.
`MetricsBroadcastService` and `AnalyticsBroadcastService` push every ~5s; with Blazor each
subscriber's circuit then re-renders server-side and ships a DOM diff. A client-rendered UI
serializes one payload and fans it out. (Note: WebSocket *count* would not drop — an SPA
would still use SignalR. The saving is server work per connection.)

**Interaction latency.** Every click is a round trip. Fine on a LAN; noticeably slow over VPN
to a distant datacenter — and list filtering, sorting, and paging are the most frequent
dashboard operations.

**Proxy resilience.** A dropped circuit means the reconnect overlay and, on failure, lost UI
state and a full reload. Evidence: **v2.1.1** was a release dedicated to fixing WebSockets for
`Startup`-pattern hosts, and `UseHangfireDashboardUI` must force `branch.UseWebSockets()`
because "the host app may not have `UseWebSockets()`". A client-rendered app degrades to HTTP
polling and stays usable.

**Embedding friction.** The machinery currently required to serve Blazor from inside a DLL
mounted at an arbitrary path: a hand-written `HtmlShellRenderer`; a `FrameworkScriptMiddleware`
that exists *solely* because `MapRazorComponents` via `MapStaticAssets` does not work in a
branched pipeline; forced `UseWebSockets()` inside the branch; antiforgery skip rules for
`/_blazor`; and a long ordering-pitfalls comment block in `samples/SampleAppSpa/Program.cs`.

**The visual layer is already JavaScript.** `charts.js`, `analyticsCharts.js`, `heatmap.js`,
`chartjs-plugin-streaming`, moment.js, plus a `MutationObserver` for theme changes. For the
7 Heatmap views, `JobGraphViewer`, and long virtualized tables, Blazor currently acts as a
wrapper marshalling data across interop to reach JS anyway.

**Contributor pool for frontend work** is larger for React than for Razor + Blazor render
modes — though this cuts the other way for the feature-adding audience, who are .NET
developers.

### Revisit conditions

Reconsider if any of the following becomes true:

1. **Circuit problems move from annoying to blocking** — a deployment genuinely cannot use the
   dashboard because of a proxy or load balancer, with no available workaround.
2. **Repeated, concrete demand to embed the dashboard *inside* an SPA host** as a component,
   rather than as a separate sub-application (which `samples/SampleAppSpa` already
   demonstrates and which works today).
3. **Scale becomes a real constraint** — hundreds of concurrent viewers rather than dozens.

If revived, migrate **Analytics and Heatmap first**: both are almost entirely read-only and
already render through Chart.js/JS interop, so Blazor contributes the least there.

If revived, also **declare a parity policy up front**: either Blazor is feature-frozen and the
SPA becomes the default, or the SPA ships a documented feature subset (for example, read plus
basic operations, without the Job Builder initially). Do not promise indefinite full parity
across two UIs.

---

## Part 5 — Blazor WebAssembly (parked)

Worth noting because it is the only option that captures most of the SPA benefits **without**
sacrificing the extension model — which is the single reason Part 4 was deferred.

**Gains:** no circuit, so #23/#20/#19-class bugs disappear; local interaction latency; proxy
resilience; free type sharing; C# retained; extension packages remain plain RCLs.

**Costs and unknowns:** it needs the REST API exactly as React would. Serving `.wasm`,
`dotnet.js`, and the assembly bundle from `EmbeddedResource` inside a branched pipeline is
plausibly *harder* than serving static JS, not easier — `FrameworkScriptMiddleware` already
exists for the sake of a single `blazor.web.js`. Initial download is substantially larger.
Neither the JS visualization ecosystem nor the broader frontend contributor pool is gained.

**Recommendation:** if the motivating pain is "the circuit" rather than "the language", this
deserves a small spike before React is considered further. Feasibility of the branched-pipeline
asset serving is the question to answer first, because it is the one that could kill the option
outright.

---

## Suggested sequencing

1. **Extension surface** (Part 1) — cheapest relative to impact, directly serves the observed
   demand, ships as a minor with no breaking change.
2. **Legacy registration detection** (Part 1) — small, and closes a silent-failure path.
3. **REST API inside the current package** (Part 3) — no split yet, so the API is exercised
   against real needs before it becomes a package boundary.
4. **Extract `.Core`** (Part 2) — mechanical, targeting zero user-visible change. Acceptance
   test: all sample apps (`SampleApp`, `SampleAppAuth`, `SampleAppRazor`, `SampleAppMvc`,
   `SampleAppBlazor`, `SampleAppSpa`, `SampleAppOrig`) run unmodified.
5. **Everything else** gated on the revisit conditions in Parts 4 and 5.

Splitting packages and writing a new frontend simultaneously would mean debugging two unknowns
at once; if something breaks, the cause is ambiguous between a bad refactor and a bad port.

---

## Verification appendix

Claims in this document that were confirmed by reading the code, as of the v2.5.1 line:

| Claim | Source |
|---|---|
| Router bound to a single assembly | `Components/DashboardRoutes.razor` |
| `DashboardApp` / `DashboardRoutes` hidden from consumers | `[EditorBrowsable(Never)]` on both |
| No extension surface anywhere in `src/a2n.*` | Repo search for `AdditionalAssemblies\|NavigationMenu\|CustomPage\|MenuItems\|NavItems` — 0 hits under `src/a2n.*` |
| Hangfire's extension points exist and are used | `Hangfire.Core/Dashboard/NavigationMenu.cs`; `Hangfire.RecurringJobAdmin/.../ConfigurationExtensions.cs`; `Hangfire.Community.Dashboard.Heatmap/src/GlobalConfigurationExtension.cs` |
| Legacy registrations still accepted but never rendered | `Hangfire.Core 1.8.*` referenced in `a2n.Hangfire.Dashboard.csproj`; no reference to `NavigationMenu` / `DashboardRoutes.Routes` in `src/a2n.*` |
| Asset serving bound to one assembly | `Middleware/EmbeddedResourceDispatcher.cs` |
| `Add`/`UseHangfireDashboardUI` are Blazor-specific | `Extensions/HangfireDashboardUIExtensions.cs` |
| `FrameworkScriptMiddleware` exists due to the branched pipeline | Comment in `Extensions/HangfireDashboardUIExtensions.cs` |
| `AuditActorAccessor` scoped because circuits lack `HttpContext` | Comment in `Extensions/HangfireDashboardUIExtensions.cs` |
| `DashboardHub` carries no Blazor coupling | `Hubs/DashboardHub.cs` |
| Blazor-runtime bugs in recent patch releases | `docs/ROADMAP.md` — v2.5.1 (#23), v2.4.3 (#20, #19), v2.1.1 |
| `RestApi` / `OpenTelemetry` are empty leftovers | Both contain only `bin/`+`obj/`; absent from `src/Hangfire Dashboard.slnx` |

Not verified, and flagged as such in the text: feasibility of rendering legacy Hangfire
extension pages; feasibility of serving Blazor WebAssembly assets from a branched embedded
pipeline; and any quantitative claim about circuit memory or CPU under load (the reasoning is
architectural, not measured).
