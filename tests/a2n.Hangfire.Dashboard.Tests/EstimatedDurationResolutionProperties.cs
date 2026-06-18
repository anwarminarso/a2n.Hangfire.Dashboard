using System;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="EstimatedDurationResolver.Resolve(double?, TimeSpan)"/> estimated
/// duration resolution.
///
/// **Property 29: Estimated-duration resolution prefers historical p95**
/// **Validates: Requirements 21.2, 21.3, 21.4**
///
/// When a historical p95 is available (present, positive, and finite), the resolved duration equals
/// that p95 (clamped to ≥ 1 minute) and the result is not flagged as default-derived (Req 21.2);
/// otherwise — when the p95 is absent, non-positive, NaN, or infinite — the resolved duration is the
/// configured default (clamped to ≥ 1 minute) and the result is flagged as default-derived
/// (Req 21.3, 21.4). In every case the resolved duration is at least one minute.
/// </summary>
public class EstimatedDurationResolutionProperties
{
    /// <summary>The floor every resolved duration must respect (Req 21.3, design invariant).</summary>
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(1);

    /// <summary>One minute expressed in milliseconds — the clamp boundary for the p95 path.</summary>
    private const double OneMinuteMs = 60_000d;

    /// <summary>
    /// Models the historical p95 in milliseconds. It is either absent (null) or one of several
    /// "no historical duration" sentinels (≤ 0, NaN, +/-Infinity) or a genuine positive duration.
    /// Genuine positive durations are constrained to ≥ 1 minute (<see cref="OneMinuteMs"/>) so the
    /// ≥ 1-minute clamp never alters the equality the property asserts for the p95 path.
    /// </summary>
    private static Gen<double?> HistoricalP95Gen =>
        Gen.Frequency(
            // Absent — falls back to the default (Req 21.3, 21.4).
            Tuple.Create(2, Gen.Constant((double?)null)),
            // Non-positive — treated as "no historical duration" (Req 21.3, 21.4).
            Tuple.Create(1, Gen.Choose(-100_000, 0).Select(v => (double?)v)),
            // Non-finite — treated as "no historical duration" (Req 21.3, 21.4).
            Tuple.Create(1, Gen.Elements(double.NaN, double.PositiveInfinity, double.NegativeInfinity)
                .Select(v => (double?)v)),
            // Genuine positive p95 ≥ 1 minute — the historical path wins (Req 21.2).
            Tuple.Create(4, Gen.Choose(60_000, 12 * 60 * 60_000).Select(v => (double?)v)));

    /// <summary>
    /// Models the configured default duration, spanning sub-minute values (which must be clamped up
    /// to one minute, Req 21.3) through multi-hour values.
    /// </summary>
    private static Gen<TimeSpan> ConfiguredDefaultGen =>
        Gen.Choose(0, 6 * 60 * 60_000).Select(ms => TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// **Property 29: Estimated-duration resolution prefers historical p95**
    /// **Validates: Requirements 21.2, 21.3, 21.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Resolve_PrefersHistoricalP95_ElseFlaggedDefault()
    {
        var arb = Arb.From(
            from p95 in HistoricalP95Gen
            from def in ConfiguredDefaultGen
            select (p95, def));

        return Prop.ForAll(arb, input =>
        {
            var (p95, configuredDefault) = input;

            var (duration, isDefault) = EstimatedDurationResolver.Resolve(p95, configuredDefault);

            var expectedDefault = configuredDefault < MinimumDuration ? MinimumDuration : configuredDefault;

            var hasHistorical = p95 is double v && v > 0 && !double.IsNaN(v) && !double.IsInfinity(v);

            if (hasHistorical)
            {
                // Historical path: p95 (≥ 1 min in the generator) wins and is not default-derived (Req 21.2).
                var expected = TimeSpan.FromMilliseconds(p95!.Value);

                if (isDefault)
                {
                    return false.Label($"p95={p95} present but IsDefault=true (expected false)");
                }

                if (duration != expected)
                {
                    return false.Label(
                        $"p95 path: expected {expected} but got {duration} (p95={p95})");
                }
            }
            else
            {
                // Fallback path: the flagged, clamped configured default (Req 21.3, 21.4).
                if (!isDefault)
                {
                    return false.Label($"p95={p95} absent/invalid but IsDefault=false (expected true)");
                }

                if (duration != expectedDefault)
                {
                    return false.Label(
                        $"default path: expected {expectedDefault} but got {duration} (configuredDefault={configuredDefault})");
                }
            }

            // In every case the resolved duration is at least one minute (Req 21.3 invariant).
            return (duration >= MinimumDuration).Label(
                $"resolved duration {duration} is below the 1-minute floor");
        });
    }
}
