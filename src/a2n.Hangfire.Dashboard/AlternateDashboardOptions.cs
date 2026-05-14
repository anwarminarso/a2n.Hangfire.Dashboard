using Hangfire;
using Hangfire.Dashboard;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Configuration options for the alternate Hangfire Dashboard.
/// </summary>
public class AlternateDashboardOptions
{
    /// <summary>
    /// The path for the Back To Site link. Set to null to hide it.
    /// </summary>
    public string? AppPath { get; set; } = "/";

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
    /// Authorization filters for the alternate dashboard.
    /// </summary>
    public IEnumerable<IAlternateDashboardAuthorizationFilter> Authorization { get; set; } = [];

    /// <summary>
    /// Creates AlternateDashboardOptions from an existing Hangfire DashboardOptions instance.
    /// Maps relevant properties for backward compatibility.
    /// </summary>
    /// <param name="hangfireOptions">The existing DashboardOptions from Hangfire</param>
    /// <returns>A new AlternateDashboardOptions with mapped values</returns>
    public static AlternateDashboardOptions FromDashboardOptions(DashboardOptions hangfireOptions)
    {
        ArgumentNullException.ThrowIfNull(hangfireOptions);

        return new AlternateDashboardOptions
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

/// <summary>
/// Authorization filter for the alternate dashboard.
/// </summary>
public interface IAlternateDashboardAuthorizationFilter
{
    /// <summary>
    /// Determines whether the current request is authorized to access the dashboard.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>True if authorized, false otherwise</returns>
    bool Authorize(HttpContext context);
}
