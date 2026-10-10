using System.Net;
using System.Security.Claims;
using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using ProxyEnvApp;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var hangfireConn = config.GetConnectionString("Hangfire")
    ?? throw new InvalidOperationException("ConnectionStrings:Hangfire is required.");
// The dashboard adapter gets its own connection string (own small pool), like the issue #61 setup.
var dashboardConn = config.GetConnectionString("Dashboard") ?? hangfireConn;
var pathPrefix = config["Dashboard:PathPrefix"] ?? "/jobs";
var requiredRole = config["Dashboard:RequiredRole"] ?? "HangfireAdmin";
// Empty by default, like issue #61: unauthorized dashboard requests (including hub negotiate) get 401.
// Set e.g. /Account/Login to get a redirect to the login page instead.
var loginPath = config["Dashboard:LoginPath"];
if (string.IsNullOrWhiteSpace(loginPath)) loginPath = null;
var statsPollingInterval = config.GetValue("Dashboard:StatsPollingInterval", 2000);
// Applied through reflection so this host still builds against dashboard versions that predate
// DashboardUIOptions.EnablePrerendering (useful for before/after comparisons).
var enablePrerendering = config.GetValue<bool?>("Dashboard:EnablePrerendering");
var demandRollupEnabled = config.GetValue<bool?>("Dashboard:DemandRollupEnabled");
// Off by default: reproduces a proxy setup where the app sees http:// and the proxy's Host header.
var useForwardedHeaders = config.GetValue("App:UseForwardedHeaders", false);
var workerCount = config.GetValue("App:WorkerCount", 4);

// Persist keys so auth cookies survive container rebuilds (otherwise every rebuild logs everyone out).
var keysPath = config["App:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("hangfire-proxy-env");
}

builder.Services.AddAntiforgery();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });
builder.Services.AddAuthorization();

if (useForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Test stack only: trust the nginx container on the compose network.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(hangfireConn)));

builder.Services.AddHangfireServer(o =>
{
    o.WorkerCount = workerCount;
    o.Queues = ["default", "reports"];
});

builder.Services.AddHangfireDashboardUI(o => o.UsePostgreSqlStorage(dashboardConn));

builder.Services.AddHostedService<SeedService>();

var app = builder.Build();

if (useForwardedHeaders)
    app.UseForwardedHeaders();

app.UseAuthentication();
app.UseAuthorization();

// Diagnostics: log every request that reaches the dashboard's SignalR hub, so self-connections
// made by the server process are visible in `docker compose logs app`, not only in the nginx log.
var hubLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HubRequests");
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments(pathPrefix + "/hubs", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    await next();
    hubLogger.LogInformation(
        "{Method} {Scheme}://{Host}{Path} -> {Status} | user={User} remote={Remote} ua={UserAgent}",
        ctx.Request.Method, ctx.Request.Scheme, ctx.Request.Host, ctx.Request.Path,
        ctx.Response.StatusCode, ctx.User.Identity?.Name ?? "(anonymous)",
        ctx.Connection.RemoteIpAddress, ctx.Request.Headers.UserAgent.ToString());
});

app.MapGet("/", (HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true
    ? Results.Redirect(pathPrefix)
    : Results.Redirect($"/Account/Login?returnUrl={Uri.EscapeDataString(pathPrefix)}"));
app.MapGet("/health", () => Results.Ok("healthy"));

app.MapGet("/Account/Login", (HttpContext ctx, IAntiforgery antiforgery, string returnUrl, bool? invalid) =>
{
    var tokens = antiforgery.GetAndStoreTokens(ctx);
    return Results.Content(
        Pages.Login(LocalUrl(returnUrl, pathPrefix), invalid == true, tokens.RequestToken),
        "text/html");
});

app.MapPost("/Account/Login", async Task<IResult> (HttpContext ctx, IAntiforgery antiforgery) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var returnUrl = LocalUrl(form["returnUrl"], pathPrefix);

    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect($"/Account/Login?invalid=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    var user = DemoUsers.Find(form["username"], form["password"]);
    if (user is null)
        return Results.Redirect($"/Account/Login?invalid=true&returnUrl={Uri.EscapeDataString(returnUrl)}");

    var claims = new List<Claim> { new(ClaimTypes.Name, user.Name) };
    claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Redirect(returnUrl);
});

app.MapPost("/Account/Logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(ctx);
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Account/Login");
});

app.MapGet("/Account/AccessDenied", () => Results.Content(Pages.AccessDenied(requiredRole), "text/html"));

var dashboardOptions = new DashboardUIOptions
{
    DashboardTitle = "Hangfire (proxy env)",
    AppPath = "/",
    LoginPath = loginPath,
    StatsPollingInterval = statsPollingInterval,
    // Same shape as the issue #61 filter: context.User.IsInRole(...).
    Authorization = [new RoleDashboardAuthorizationFilter(requiredRole)],
    AsyncAuthorization = []
};
if (enablePrerendering is bool prerender)
{
    var property = typeof(DashboardUIOptions).GetProperty("EnablePrerendering");
    if (property is null)
        app.Logger.LogWarning("Dashboard:EnablePrerendering ignored: this dashboard version has no such option");
    else
        property.SetValue(dashboardOptions, prerender);
}
if (demandRollupEnabled is bool rollup)
{
    var property = typeof(HeatmapOptions).GetProperty("DemandRollupEnabled");
    if (property is null)
        app.Logger.LogWarning("Dashboard:DemandRollupEnabled ignored: this dashboard version has no such option");
    else
        property.SetValue(dashboardOptions.Heatmap, rollup);
}
app.UseHangfireDashboardUI(pathPrefix, dashboardOptions);

app.Logger.LogInformation(
    "Dashboard at {Prefix} | role={Role} | loginPath={LoginPath} | forwardedHeaders={Fwd} | users: admin/admin (role), viewer/viewer (no role)",
    pathPrefix, requiredRole, loginPath ?? "(none, 401)", useForwardedHeaders);

app.Run();

static string LocalUrl(string returnUrl, string fallback)
{
    if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') ||
        returnUrl.StartsWith("//", StringComparison.Ordinal) || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        return fallback;
    return returnUrl;
}

static class Pages
{
    private const string Style =
        "<style>body{font-family:system-ui,sans-serif;background:#f4f6f8;margin:0;padding:2rem}"
        + ".card{max-width:420px;margin:4rem auto;background:#fff;border-radius:8px;padding:2rem;box-shadow:0 2px 12px rgba(0,0,0,.08)}"
        + "h1{font-size:1.25rem;margin:0 0 1rem}label{display:block;font-weight:600;margin-bottom:.25rem}"
        + "input{width:100%;padding:.5rem;margin-bottom:1rem;box-sizing:border-box}"
        + "button{background:#0d6efd;color:#fff;border:0;padding:.6rem 1.2rem;border-radius:4px;cursor:pointer}"
        + ".hint{background:#eef6ff;border:1px solid #c5ddfc;border-radius:6px;padding:.75rem 1rem;margin-bottom:1rem;font-size:.9rem}</style>";

    public static string Login(string returnUrl, bool invalid, string token)
    {
        var error = invalid ? "<p style=\"color:#b00020\">Invalid username or password.</p>" : "";
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Login</title>" + Style + "</head><body>"
            + "<main class=\"card\"><h1>Sign in</h1>"
            + "<div class=\"hint\">Test users: <code>admin</code>/<code>admin</code> (has role), "
            + "<code>viewer</code>/<code>viewer</code> (no role)</div>" + error
            + "<form method=\"post\" action=\"/Account/Login\">"
            + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + WebUtility.HtmlEncode(returnUrl) + "\">"
            + "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"" + WebUtility.HtmlEncode(token) + "\">"
            + "<label for=\"username\">Username</label><input id=\"username\" name=\"username\" autocomplete=\"username\">"
            + "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\">"
            + "<button type=\"submit\">Sign in</button></form></main></body></html>";
    }

    public static string AccessDenied(string role) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Access denied</title>" + Style + "</head><body>"
        + "<main class=\"card\"><h1>Access denied</h1><p>Role <code>" + WebUtility.HtmlEncode(role) + "</code> is required.</p>"
        + "<p><a href=\"/Account/Login\">Sign in as another user</a></p></main></body></html>";
}
