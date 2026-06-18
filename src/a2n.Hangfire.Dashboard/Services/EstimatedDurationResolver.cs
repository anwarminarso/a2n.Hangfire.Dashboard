using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Resolves a job's <c>Estimated_Duration</c> for the heatmap's Worker-minutes load metric and
/// duration-aware concurrency analysis (Req 21.1). It prefers a job type's historical 95th-percentile
/// execution duration supplied by the optional <see cref="IStorageMetricsProvider"/>
/// (<see cref="JobDurationStatsDto.P95Ms"/>, Req 21.2); when no historical duration is available it
/// falls back to the configured <see cref="HeatmapOptions.DefaultEstimatedDuration"/> treated as at
/// least one minute (Req 21.3) and flags the result as default-derived so callers can surface that the
/// duration is an estimate (Req 21.4).
/// </summary>
/// <remarks>
/// <para>The resolver follows the existing <see cref="AnalyticsService"/> / <see cref="HeatmapService"/>
/// conventions: it takes an <see cref="IServiceProvider"/>, resolves <see cref="IStorageMetricsProvider"/>
/// optionally (so it degrades gracefully when no metrics provider is registered — always returning the
/// flagged default), reads <see cref="DashboardUIOptions"/> for the configured default, and never throws
/// for metrics-provider failures (returning the flagged default instead).</para>
/// <para>The core mapping is exposed as the pure, side-effect-free <see cref="Resolve(double?, TimeSpan)"/>
/// helper so it can be exercised directly by property tests (Property 29).</para>
/// <para>Validates Requirements 21.1, 21.2, 21.3, and 21.4.</para>
/// </remarks>
public class EstimatedDurationResolver
{
    /// <summary>The floor for any resolved or default estimated duration (Req 21.3, design invariant).</summary>
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(1);

    private readonly IStorageMetricsProvider _metricsProvider;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<EstimatedDurationResolver> _logger;

    /// <summary>
    /// Indicates whether historical durations can be supplied (an <see cref="IStorageMetricsProvider"/>
    /// is registered). When false, every resolution returns the flagged default (Req 21.3, 21.4).
    /// </summary>
    public bool IsAvailable => _metricsProvider != null;

    public EstimatedDurationResolver(IServiceProvider serviceProvider)
    {
        // Resolve optionally — null when no metrics provider is registered (graceful degradation).
        _metricsProvider = serviceProvider.GetService<IStorageMetricsProvider>();
        _options = serviceProvider.GetService<DashboardUIOptions>() ?? new DashboardUIOptions();
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<EstimatedDurationResolver>()
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EstimatedDurationResolver>.Instance;
    }

    /// <summary>
    /// Pure resolution of a single estimate: prefers the supplied historical p95 (Req 21.2), otherwise
    /// the configured default treated as at least one minute (Req 21.3), flagging the default-derived
    /// case (Req 21.4). A non-positive, absent, NaN, or infinite p95 is treated as "no historical
    /// duration available" and falls back to the default.
    /// </summary>
    /// <param name="historicalP95Ms">The historical p95 execution duration in milliseconds, or null when absent.</param>
    /// <param name="configuredDefault">The configured default estimated duration (clamped to ≥ 1 minute).</param>
    /// <returns>The resolved duration (≥ 1 minute) and whether it was derived from the default.</returns>
    public static (TimeSpan Duration, bool IsDefault) Resolve(double? historicalP95Ms, TimeSpan configuredDefault)
    {
        var defaultDuration = ClampToMinimum(configuredDefault);

        if (historicalP95Ms is double p95 && p95 > 0 && !double.IsNaN(p95) && !double.IsInfinity(p95))
        {
            return (ClampToMinimum(TimeSpan.FromMilliseconds(p95)), false);
        }

        return (defaultDuration, true);
    }

    /// <summary>
    /// Resolves the estimated duration for a single job type over the given window. Uses the historical
    /// p95 when the metrics provider supplies one (Req 21.2), otherwise the flagged configured default
    /// (Req 21.3, 21.4). Never throws — metrics-provider failures degrade to the flagged default.
    /// </summary>
    /// <param name="jobTypeKey">The job type key matched against <see cref="JobDurationStatsDto.JobType"/>.</param>
    /// <param name="from">Start of the historical window.</param>
    /// <param name="to">End of the historical window.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<(TimeSpan Duration, bool IsDefault)> ResolveAsync(
        string jobTypeKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var defaultDuration = ResolveDefaultDuration();

        if (_metricsProvider == null || string.IsNullOrEmpty(jobTypeKey))
        {
            return (defaultDuration, true);
        }

        try
        {
            var stats = await _metricsProvider.GetJobDurationStatsAsync(from, to, ct).ConfigureAwait(false);
            var match = stats?.FirstOrDefault(s => string.Equals(s?.JobType, jobTypeKey, StringComparison.Ordinal));
            return Resolve(match?.P95Ms, defaultDuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve historical duration for job type '{JobType}'; using default estimate", jobTypeKey);
            return (defaultDuration, true);
        }
    }

    /// <summary>
    /// Resolves estimated durations for many job types from a single
    /// <see cref="IStorageMetricsProvider.GetJobDurationStatsAsync"/> call (the efficient path used by
    /// the heatmap projection). Each key resolves to its historical p95 when available (Req 21.2),
    /// otherwise the flagged configured default (Req 21.3, 21.4). Never throws — provider failures
    /// degrade every key to the flagged default.
    /// </summary>
    /// <param name="jobTypeKeys">The job type keys to resolve (matched against <see cref="JobDurationStatsDto.JobType"/>).</param>
    /// <param name="from">Start of the historical window.</param>
    /// <param name="to">End of the historical window.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A map from each distinct, non-empty job type key to its resolved duration and default flag.</returns>
    public async Task<IReadOnlyDictionary<string, (TimeSpan Duration, bool IsDefault)>> ResolveBatchAsync(
        IEnumerable<string> jobTypeKeys, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var defaultDuration = ResolveDefaultDuration();

        var keys = (jobTypeKeys ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var result = new Dictionary<string, (TimeSpan Duration, bool IsDefault)>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return result;
        }

        // Build the historical p95 lookup once. With no provider (or on failure) it stays empty, so
        // every key falls back to the flagged default (Req 21.3, 21.4).
        var p95ByType = new Dictionary<string, double>(StringComparer.Ordinal);
        if (_metricsProvider != null)
        {
            try
            {
                var stats = await _metricsProvider.GetJobDurationStatsAsync(from, to, ct).ConfigureAwait(false);
                if (stats != null)
                {
                    foreach (var s in stats)
                    {
                        if (s?.JobType != null && !p95ByType.ContainsKey(s.JobType))
                        {
                            p95ByType[s.JobType] = s.P95Ms;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve historical durations; using default estimates for all job types");
            }
        }

        foreach (var key in keys)
        {
            var p95 = p95ByType.TryGetValue(key, out var value) ? value : (double?)null;
            result[key] = Resolve(p95, defaultDuration);
        }

        return result;
    }

    /// <summary>
    /// Returns the configured default estimated duration, treated as at least one minute (Req 21.3).
    /// Mirrors <c>HeatmapService.ResolveDefaultDuration</c>.
    /// </summary>
    private TimeSpan ResolveDefaultDuration()
        => ClampToMinimum(_options.Heatmap?.DefaultEstimatedDuration ?? MinimumDuration);

    /// <summary>Clamps a duration to the <see cref="MinimumDuration"/> floor of one minute (Req 21.3).</summary>
    private static TimeSpan ClampToMinimum(TimeSpan duration)
        => duration < MinimumDuration ? MinimumDuration : duration;
}
