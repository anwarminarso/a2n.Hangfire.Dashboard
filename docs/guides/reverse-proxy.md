# Behind a reverse proxy

Back to the [README](../../README.md). The [`tests/reverse-proxy`](../../tests/reverse-proxy) folder has a Docker Compose stack (nginx, TLS, path prefix, cookie auth) for trying these setups.

## On the same host

A proxy on the same machine forwards every request from `127.0.0.1`, so the default local-only filter would let **every** client in. Make ASP.NET Core use the forwarded client address before mapping the dashboard:

```csharp
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Loopback proxies are trusted by default; add others with options.KnownProxies / KnownNetworks.
});

app.UseForwardedHeaders();               // before UseHangfireDashboardUI
app.UseHangfireDashboardUI("/hangfire");
```

The proxy must set `X-Forwarded-For`. Better still, use a filter that checks the user rather than the address.
