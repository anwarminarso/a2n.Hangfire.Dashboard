using System.Security.Claims;
using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Console;
using Hangfire.PostgreSql;
using Hangfire.Tags;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SampleAppAuth.Auth;
using SampleApp.SharedJobs;

var builder = WebApplication.CreateBuilder(args);

var storageProvider = builder.Configuration["StorageProvider"] ?? "InMemory";
var sqlServerConn = builder.Configuration.GetConnectionString("SqlServer");
var postgreSqlConn = builder.Configuration.GetConnectionString("PostgreSql");

Console.WriteLine($"[SampleAppAuth] Storage provider: {storageProvider}");
Console.WriteLine($"[SampleAppAuth] Demo login — user: {DemoCredentials.Username}, password: {DemoCredentials.Password}");

builder.Services.AddAntiforgery();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
    });

builder.Services.AddAuthorization();

builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings();

    switch (storageProvider)
    {
        case "SqlServer":
            config.UseSqlServerStorage(sqlServerConn);
            break;
        case "PostgreSql":
            config.UsePostgreSqlStorage(x => x.UseNpgsqlConnection(postgreSqlConn));
            break;
        default:
            config.UseInMemoryStorage();
            break;
    }

    config.UseConsole();
    config.UseTags();
});

builder.Services.AddHangfireServer(options => options.WorkerCount = 2);

builder.Services.AddHangfireDashboardUI(options =>
{
    switch (storageProvider)
    {
        case "SqlServer":
            options.UseSqlServerStorage(sqlServerConn);
            break;
        case "PostgreSql":
            options.UsePostgreSqlStorage(postgreSqlConn);
            break;
    }
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/login"));

app.MapGet("/login", (HttpContext context, string returnUrl, bool? invalid) =>
{
    var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Content(
        LoginPage.Build(returnUrl, invalid == true, tokens.RequestToken),
        "text/html");
});

app.MapPost("/login", async Task<IResult> (
    HttpContext context,
    IAntiforgery antiforgery,
    string returnUrl) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/login?invalid=true");
    }

    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();

    if (username != DemoCredentials.Username || password != DemoCredentials.Password)
    {
        var failedReturn = Uri.EscapeDataString(ReturnUrlHelper.ResolveLocal(returnUrl));
        return Results.Redirect($"/login?invalid=true&returnUrl={failedReturn}");
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, username),
        new Claim(ClaimTypes.Role, "DashboardUser")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Redirect(ReturnUrlHelper.ResolveLocal(returnUrl));
});

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.UseHangfireDashboardUI("/hangfire", new DashboardUIOptions
{
    DashboardTitle = "Hangfire Dashboard (Auth Demo)",
    LoginPath = "/login",
    Authorization = [new DashboardCookieAuthorizationFilter()],
    AsyncAuthorization = []
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    SampleJobsSeeder.SeedBasic();
});

app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();

static class LoginPage
{
    public static string Build(string returnUrl, bool invalid, string antiforgeryToken)
    {
        var safeReturnUrl = ReturnUrlHelper.ResolveLocal(returnUrl);
        var error = invalid
            ? "<p style=\"color:#b00020;margin:0 0 1rem\">Invalid username or password.</p>"
            : string.Empty;

        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />"
            + "<title>Hangfire Dashboard — Login</title>"
            + "<style>body{font-family:system-ui,sans-serif;background:#f4f6f8;margin:0;padding:2rem}"
            + ".card{max-width:420px;margin:4rem auto;background:#fff;border-radius:8px;padding:2rem;box-shadow:0 2px 12px rgba(0,0,0,.08)}"
            + "h1{font-size:1.25rem;margin:0 0 .5rem}.hint{background:#eef6ff;border:1px solid #c5ddfc;border-radius:6px;padding:.75rem 1rem;margin-bottom:1.25rem;font-size:.9rem}"
            + "label{display:block;font-weight:600;margin-bottom:.25rem}input{width:100%;padding:.5rem;margin-bottom:1rem;box-sizing:border-box}"
            + "button{background:#0d6efd;color:#fff;border:0;padding:.6rem 1.2rem;border-radius:4px;cursor:pointer}</style></head><body>"
            + "<div class=\"card\"><h1>Hangfire Dashboard</h1><p>Sign in to open the dashboard.</p>"
            + "<div class=\"hint\"><strong>Demo credentials (development only)</strong><br />Username: <code>"
            + DemoCredentials.Username + "</code><br />Password: <code>" + DemoCredentials.Password + "</code></div>"
            + error
            + "<form method=\"post\" action=\"/login\">"
            + "<input type=\"hidden\" name=\"returnUrl\" value=\""
            + System.Net.WebUtility.HtmlEncode(safeReturnUrl) + "\" />"
            + "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\""
            + System.Net.WebUtility.HtmlEncode(antiforgeryToken) + "\" />"
            + "<label for=\"username\">Username</label><input id=\"username\" name=\"username\" autocomplete=\"username\" />"
            + "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" />"
            + "<button type=\"submit\">Sign in</button></form></div></body></html>";
    }
}

static class ReturnUrlHelper
{
    public static string ResolveLocal(string returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/hangfire";

        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
            return "/hangfire";

        return returnUrl;
    }
}
