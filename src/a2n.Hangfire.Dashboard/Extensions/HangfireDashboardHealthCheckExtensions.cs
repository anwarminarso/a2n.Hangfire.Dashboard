using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using DashboardHealthCheckService = a2n.Hangfire.Dashboard.Services.HealthCheckService;
using DashboardHealthReport = a2n.Hangfire.Dashboard.HealthReport;
using DashboardHealthStatus = a2n.Hangfire.Dashboard.HealthStatus;
using AspNetHealthCheckResult = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult;
using AspNetHealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for plugging the dashboard's <see cref="HealthCheckService"/> into the
/// ASP.NET Core <c>IHealthChecksBuilder</c> pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard already exposes a built-in HTTP endpoint at <c>/{dashboard}/healthz</c>. This
/// adapter is for hosts that prefer a unified <c>/health</c> endpoint aggregating multiple
/// dependencies (database, message broker, Hangfire, ...).
/// </para>
/// <para>
/// Map the unified endpoint as usual:
/// <code>
/// builder.Services.AddHealthChecks()
///     .AddHangfireDashboard();
///
/// app.MapHealthChecks("/health");
/// </code>
/// </para>
/// </remarks>
public static class HangfireDashboardHealthCheckExtensions
{
    /// <summary>
    /// Registers the Hangfire dashboard health checks (storage + servers + queues + stuck
    /// processing + failure rate + recurring) as a single ASP.NET Core <see cref="IHealthCheck"/>.
    /// </summary>
    /// <param name="builder">The ASP.NET Core health checks builder.</param>
    /// <param name="name">The check name (default <c>"hangfire_dashboard"</c>).</param>
    /// <param name="failureStatus">
    /// The status reported when the underlying <see cref="DashboardHealthCheckService.CheckFull"/>
    /// returns <see cref="DashboardHealthStatus.Unhealthy"/>. Defaults to
    /// <see cref="DashboardHealthStatus.Unhealthy"/>.
    /// </param>
    /// <param name="tags">Optional tags applied to this check (use with <c>MapHealthChecks(predicate: …)</c>).</param>
    /// <returns>The same builder for chaining.</returns>
    public static IHealthChecksBuilder AddHangfireDashboard(
        this IHealthChecksBuilder builder,
        string name = "hangfire_dashboard",
        AspNetHealthStatus? failureStatus = null,
        IEnumerable<string> tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<HangfireDashboardHealthCheck>(
            name: name,
            failureStatus: failureStatus,
            tags: tags ?? []);
    }
}

/// <summary>
/// Adapts the dashboard's <see cref="DashboardHealthCheckService"/> to ASP.NET Core <see cref="IHealthCheck"/>.
/// </summary>
/// <remarks>
/// Reuses <see cref="DashboardHealthCheckService.CheckFull"/> so the result matches what the dashboard's
/// hero card and <c>/{dashboard}/healthz/full</c> endpoint show. Each individual check's
/// <c>Description</c> and <c>Data</c> are surfaced via <see cref="AspNetHealthCheckResult.Data"/> so
/// monitoring frontends (HealthCheckUI, custom dashboards) can drill into per-check status.
/// </remarks>
internal sealed class HangfireDashboardHealthCheck : IHealthCheck
{
    private readonly HealthReportCache _cache;

    public HangfireDashboardHealthCheck(HealthReportCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<AspNetHealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        DashboardHealthReport report;
        try
        {
            // Shared cache single-flights and offloads the synchronous storage probes onto the
            // thread pool, and reuses the same report the dashboard hero card / /healthz endpoint show.
            report = await _cache.GetAsync(HealthReportCache.Mode.Full, ct: cancellationToken);
        }
        catch (Exception ex)
        {
            return new AspNetHealthCheckResult(
                status: context.Registration.FailureStatus,
                description: $"Hangfire health probe threw: {ex.GetType().Name}: {ex.Message}",
                exception: ex);
        }

        // Map our status enum to ASP.NET Core's. We collapse Degraded to Degraded and
        // Unhealthy to the registration's configured failure status (defaults to Unhealthy).
        var aspNetStatus = report.Status switch
        {
            DashboardHealthStatus.Healthy => AspNetHealthStatus.Healthy,
            DashboardHealthStatus.Degraded => AspNetHealthStatus.Degraded,
            DashboardHealthStatus.Unhealthy => context.Registration.FailureStatus,
            _ => AspNetHealthStatus.Unhealthy,
        };

        // Flatten checks into the "data" dictionary so HealthCheckUI / consumers can see them.
        var data = new Dictionary<string, object>
        {
            ["version"] = report.Version,
            ["durationMs"] = report.DurationMs,
            ["overallStatus"] = report.Status.ToString(),
        };
        foreach (var (key, check) in report.Checks)
        {
            data[$"check.{key}.status"] = check.Status.ToString();
            if (!string.IsNullOrEmpty(check.Description))
                data[$"check.{key}.description"] = check.Description;
        }

        // Description string: short summary appropriate for log lines / Status field in UIs.
        var description = report.Status switch
        {
            DashboardHealthStatus.Healthy => "All Hangfire dashboard checks passed.",
            DashboardHealthStatus.Degraded => SummarizeIssues(report, "Degraded"),
            DashboardHealthStatus.Unhealthy => SummarizeIssues(report, "Unhealthy"),
            _ => "Hangfire dashboard health unknown."
        };

        return new AspNetHealthCheckResult(
            status: aspNetStatus,
            description: description,
            data: data);
    }

    private static string SummarizeIssues(DashboardHealthReport report, string label)
    {
        var issues = report.Checks
            .Where(kv => kv.Value.Status != DashboardHealthStatus.Healthy)
            .Select(kv => $"{kv.Key}: {kv.Value.Description}")
            .ToList();

        if (issues.Count == 0) return $"{label}.";
        if (issues.Count == 1) return $"{label} — {issues[0]}";
        return $"{label} — {issues.Count} issues: {string.Join(" | ", issues)}";
    }
}
