# Integrations (v2.6.0)

a2n.Hangfire.Dashboard v2.6 adds four independently shippable, opt-in integrations. Each one
reuses the dashboard's existing query/metrics providers, honors the configured dashboard path
prefix, and enforces an authorization check before returning any Hangfire data. This document
describes each network endpoint's **default authorization mode** and **how to change it**
(Requirement 17.3).

## Summary of endpoint authorization

| Integration | Endpoint | Default authorization | How to change |
| --- | --- | --- | --- |
| Prometheus metrics | `{prefix}/metrics` | `LocalOnly` (local requests only) | `Prometheus.AuthorizationMode` / `Prometheus.ScraperAuthorization` |
| CSV / JSON export | `{prefix}/export` | Dashboard authorization (`Dashboard_Authorization`) | `DashboardUIOptions.Authorization` / `AsyncAuthorization` |
| REST API | `{prefix}/api/v1/*` | JWT bearer required (no anonymous access) | host JWT bearer scheme + authorization policy |
| OpenTelemetry | (no network endpoint) | n/a | n/a |

`{prefix}` is the dashboard mount path (default `/hangfire`). Every endpoint is served relative to
that prefix, so a non-default prefix is honored automatically.

## Prometheus `/metrics`

- **Default auth:** `PrometheusAuthorization.LocalOnly`. Requests originating from a non-local
  address receive `HTTP 401` and no metric values are emitted.
- **How to change:** set `AuthorizationMode` on the Prometheus options, or supply a custom
  `ScraperAuthorization` filter so a scraper can reach `/metrics` without weakening the
  authorization used for dashboard pages.

```csharp
services.AddHangfireDashboardUI(options =>
{
    options.Prometheus.Enabled = true;
    options.Prometheus.Path = "/metrics";           // relative to the dashboard prefix
    options.Prometheus.AuthorizationMode = PrometheusAuthorization.LocalOnly; // default

    // To allow a remote scraper without weakening dashboard-page auth, provide a
    // dedicated scraper authorization filter (explicit action, never the default):
    // options.Prometheus.ScraperAuthorization = new MyScraperAuthorizationFilter();
});
```

Weakening the metrics authorization always requires an explicit configuration action; the default
never exposes metrics anonymously.

## CSV / JSON export `/export`

- **Default auth:** the same `Dashboard_Authorization` pipeline used by the dashboard pages
  (`DashboardUIOptions.Authorization`, defaulting to `LocalRequestsOnlyAuthorizationFilter`). A
  request that fails authorization receives `HTTP 401` and no records are streamed.
- **How to change:** adjust `DashboardUIOptions.Authorization` / `AsyncAuthorization`. Export is a
  read operation, so it remains available while the dashboard is in read-only mode.

```csharp
services.AddHangfireDashboardUI(options =>
{
    options.Export.Enabled = true;
    options.Export.Path = "/export";                 // relative to the dashboard prefix
    // Export reuses DashboardUIOptions.Authorization / AsyncAuthorization.
});
```

## REST API `/api/v1`

- **Default auth:** JWT bearer authentication is required. A request without a valid JWT bearer
  token receives `HTTP 401` and no job data; an authenticated-but-unauthorized request receives
  `HTTP 403`. The API never exposes job data anonymously by default.
- **How to change:** the REST API binds to the host application's configured JWT bearer scheme and
  authorization policy. Configure `AddAuthentication().AddJwtBearer(...)` and the desired policy in
  the host to control who may call the endpoints.

```csharp
// Host application (separate opt-in package a2n.Hangfire.Dashboard.RestApi):
builder.Services.AddAuthentication().AddJwtBearer(/* issuer, audience, keys */);
builder.Services.AddHangfireDashboardRestApi();

app.MapHangfireDashboardRestApi("/hangfire"); // group mounted at {prefix}/api/v1
```

The auto-generated OpenAPI document declares the JWT bearer security scheme required by every
endpoint.

## Enabling each integration

- **OpenTelemetry** (separate package `a2n.Hangfire.Dashboard.OpenTelemetry`):
  `GlobalConfiguration.Configuration.UseHangfireDashboardOpenTelemetry(...)`. Registers the trace
  capture client filter and span restorer via a named `ActivitySource`; adds no network endpoint.
- **Prometheus** (core): `options.Prometheus.Enabled = true`.
- **Export** (core): `options.Export.Enabled = true`.
- **REST API** (separate package `a2n.Hangfire.Dashboard.RestApi`):
  `AddHangfireDashboardRestApi()` + `MapHangfireDashboardRestApi(prefix)`.

Each integration is enabled independently; when an integration is not registered the dashboard
exposes no endpoint, page element, or job filter belonging to it. The OpenTelemetry and REST API
integrations ship as separate NuGet packages, so a team can adopt one without referencing the
other, and the core package references neither.
