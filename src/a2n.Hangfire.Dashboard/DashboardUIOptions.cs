using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Configuration options for the Hangfire Dashboard UI.
/// </summary>
public class DashboardUIOptions
{
    /// <summary>
    /// The path for the Back To Site link. Set to null to hide it.
    /// </summary>
    public string AppPath { get; set; } = "/";

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
    /// Authorization filters for the dashboard.
    /// </summary>
    public IEnumerable<IDashboardAuthorizationFilter> Authorization { get; set; } = [];

    /// <summary>
    /// Creates DashboardUIOptions from an existing Hangfire DashboardOptions instance.
    /// Maps relevant properties for backward compatibility.
    /// </summary>
    /// <param name="hangfireOptions">The existing DashboardOptions from Hangfire</param>
    /// <returns>A new DashboardUIOptions with mapped values</returns>
    public static DashboardUIOptions FromDashboardOptions(DashboardOptions hangfireOptions)
    {
        ArgumentNullException.ThrowIfNull(hangfireOptions);

        return new DashboardUIOptions
        {
            AppPath = hangfireOptions.AppPath,
            DashboardTitle = hangfireOptions.DashboardTitle,
            StatsPollingInterval = hangfireOptions.StatsPollingInterval,
            IsReadOnly = hangfireOptions.IsReadOnlyFunc?.Invoke(null!) ?? false,
            DefaultRecordsPerPage = hangfireOptions.DefaultRecordsPerPage,
            DefaultTheme = hangfireOptions.DarkModeEnabled ? "auto" : "light",
        };
    }
}
