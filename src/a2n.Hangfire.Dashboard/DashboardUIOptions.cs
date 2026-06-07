using a2n.Hangfire.Dashboard.Security;
using Hangfire;
using Hangfire.Dashboard;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Configuration options for the Hangfire Dashboard UI.
/// </summary>
public class DashboardUIOptions
{
    private static readonly IDashboardAuthorizationFilter[] DefaultAuthorization =
        [new Security.LocalRequestsOnlyAuthorizationFilter()];

    /// <summary>
    /// The path for the Back To Site link. Set to null to hide it.
    /// </summary>
    public string AppPath { get; set; } = "/";

    /// <summary>
    /// Optional login path. When set, unauthenticated users are redirected here instead of receiving HTTP 401.
    /// </summary>
    public string LoginPath { get; set; }

    /// <summary>
    /// The title displayed on the dashboard.
    /// </summary>
    public string DashboardTitle { get; set; } = "Hangfire Dashboard";

    /// <summary>
    /// The interval (in milliseconds) for realtime metric updates via SignalR.
    /// Default: 2000ms.
    /// </summary>
    public int StatsPollingInterval { get; set; } = 2000;

    /// <summary>
    /// Whether the dashboard is in read-only mode (hides action buttons).
    /// </summary>
    public bool IsReadOnly { get; set; } = false;

    /// <summary>
    /// Whether recurring job administration (create, edit, delete, stop, start) is enabled.
    /// When false, only the recurring jobs list and trigger action are available.
    /// Default: true.
    /// </summary>
    public bool EnableRecurringJobAdmin { get; set; } = true;

    /// <summary>
    /// Default number of records per page.
    /// </summary>
    public int DefaultRecordsPerPage { get; set; } = 20;

    /// <summary>
    /// Default theme: "auto", "light", or "dark".
    /// </summary>
    public string DefaultTheme { get; set; } = "auto";

    /// <summary>
    /// Custom favicon path. When set, the dashboard uses this URL instead of the built-in favicon.
    /// Use an absolute path (e.g., "/favicon.ico") to reference the host app's favicon,
    /// or a full URL (e.g., "https://example.com/icon.png").
    /// When null, the dashboard's built-in favicon is used.
    /// </summary>
    public string FaviconPath { get; set; }

    /// <summary>
    /// Maximum traversal depth (in either direction — ancestors or descendants) when building the
    /// job dependency graph on the Job Details page. Each hop costs one storage lookup, so this
    /// bounds N+1 calls. Default: 5.
    /// </summary>
    public int JobGraphMaxDepth { get; set; } = 5;

    /// <summary>
    /// Maximum total nodes materialized in the job dependency graph. The graph is truncated and
    /// shows a "truncated" indicator once this limit is reached. Default: 30.
    /// </summary>
    public int JobGraphMaxNodes { get; set; } = 30;

    /// <summary>
    /// Optional source-control link configuration. When set, file paths in stack traces become
    /// clickable links pointing to the configured provider (GitHub, GitLab, Azure DevOps, Bitbucket,
    /// a self-hosted Git server, or a local IDE handler such as <c>vscode://</c>). When null,
    /// stack traces render as plain text. See <see cref="SourceLinkOptions"/> presets.
    /// </summary>
    public SourceLinkOptions SourceLink { get; set; }

    /// <summary>
    /// Authorization filters for the dashboard. Defaults to <see cref="LocalRequestsOnlyAuthorizationFilter"/>
    /// (same as Hangfire's built-in dashboard). Set to an empty array to allow all requests.
    /// </summary>
    public IEnumerable<IDashboardAuthorizationFilter> Authorization { get; set; } = DefaultAuthorization;

    /// <summary>
    /// Async authorization filters for the dashboard.
    /// </summary>
    public IEnumerable<IDashboardAsyncAuthorizationFilter> AsyncAuthorization { get; set; } = [];

    /// <summary>
    /// Authorization mode for the health check endpoint (<c>/healthz</c>, <c>/healthz/ready</c>,
    /// <c>/healthz/full</c>). Defaults to <see cref="HealthCheckAuthorization.AllowAnonymous"/> so
    /// Kubernetes liveness/readiness probes work without configuring authentication on the prober.
    /// </summary>
    public HealthCheckAuthorization HealthCheckAuthorizationMode { get; set; } = HealthCheckAuthorization.AllowAnonymous;

    /// <summary>
    /// Configurable thresholds for the dashboard's built-in health checks.
    /// Modify individual properties to tune what counts as Degraded vs Unhealthy.
    /// </summary>
    public HealthThresholds HealthCheckThresholds { get; set; } = new HealthThresholds();

    /// <summary>
    /// Creates DashboardUIOptions from an existing Hangfire DashboardOptions instance.
    /// Maps relevant properties for backward compatibility.
    /// </summary>
    /// <param name="hangfireOptions">The existing DashboardOptions from Hangfire</param>
    /// <returns>A new DashboardUIOptions with mapped values</returns>
    public static DashboardUIOptions FromDashboardOptions(DashboardOptions hangfireOptions)
    {
        ArgumentNullException.ThrowIfNull(hangfireOptions);

        var authorization = hangfireOptions.Authorization?
            .Select(f => (IDashboardAuthorizationFilter)new Security.HangfireDashboardAuthorizationFilterAdapter(f))
            .ToArray() ?? DefaultAuthorization;

        var asyncAuthorization = hangfireOptions.AsyncAuthorization?
            .Select(f => (IDashboardAsyncAuthorizationFilter)new Security.HangfireDashboardAsyncAuthorizationFilterAdapter(f))
            .ToArray() ?? [];

        return new DashboardUIOptions
        {
            AppPath = hangfireOptions.AppPath,
            DashboardTitle = hangfireOptions.DashboardTitle,
            StatsPollingInterval = hangfireOptions.StatsPollingInterval,
            // IsReadOnlyFunc requires a DashboardContext; default to false when mapping from Hangfire options.
            IsReadOnly = false,
            DefaultRecordsPerPage = hangfireOptions.DefaultRecordsPerPage,
            DefaultTheme = hangfireOptions.DarkModeEnabled ? "auto" : "light",
            Authorization = authorization,
            AsyncAuthorization = asyncAuthorization,
        };
    }
}
