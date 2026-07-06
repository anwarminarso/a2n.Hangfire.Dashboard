using System;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for the pure intensity mapping helpers in <see cref="Intensity"/>.
///
/// **Property 12: Intensity mapping is monotonic and endpoint-normalized under both scales**
/// **Validates: Requirements 3.4, 3.5, 6.1, 6.2, 6.3, 6.4, 18.1, 20.4, 20.5**
///
/// For any displayed value domain and any two values a &lt;= b, both the linear and logarithmic
/// intensity mappings (color-ramp index and bubble area) satisfy intensity(a) &lt;= intensity(b);
/// the lowest displayed value maps to the minimum intensity; the highest displayed value maps to
/// the maximum intensity; and a value of zero maps to empty / no bubble.
/// </summary>
public class IntensityMappingProperties
{
    /// <summary>The intensity scales under test (Req 6.1, 20.4, 20.5 — linear and logarithmic).</summary>
    private static Gen<IntensityScale> ScaleGen =>
        Gen.Elements(IntensityScale.Linear, IntensityScale.Logarithmic);

    /// <summary>
    /// A finite double in the inclusive range <c>[lo, hi]</c>, drawn at fine resolution so the
    /// generated domains and values exercise fractional positions across the ramp, not just
    /// integer boundaries.
    /// </summary>
    private static Gen<double> DoubleInRange(double lo, double hi) =>
        Gen.Choose(0, 1_000_000).Select(i => lo + ((hi - lo) * (i / 1_000_000.0)));

    /// <summary>
    /// A test case for the monotonicity / endpoint properties: a displayed domain <c>[min, max]</c>
    /// (with <paramref name="span"/> possibly zero, giving a degenerate domain), two ordered probe
    /// values <c>a &lt;= b</c> drawn from a range that overhangs the domain on both sides, a positive
    /// bubble-area budget, a ramp size of at least one, and an intensity scale.
    /// </summary>
    private static Gen<(double Min, double Max, double A, double B, double MaxArea, int RampSize, IntensityScale Scale)> CaseGen =>
        from min in DoubleInRange(-500.0, 500.0)
        from span in DoubleInRange(0.0, 1000.0)
        let max = min + span
        // Probe values can sit below the domain minimum, inside it, or above the maximum so the
        // clamping endpoints and the interior curve are all exercised.
        from v1 in DoubleInRange(min - 100.0, max + 100.0)
        from v2 in DoubleInRange(min - 100.0, max + 100.0)
        from maxArea in DoubleInRange(0.0001, 1000.0)
        from rampSize in Gen.Choose(1, 24)
        from scale in ScaleGen
        let a = Math.Min(v1, v2)
        let b = Math.Max(v1, v2)
        select (min, max, a, b, maxArea, rampSize, scale);

    /// <summary>
    /// **Property 12: Intensity mapping is monotonic and endpoint-normalized under both scales**
    /// **Validates: Requirements 3.4, 3.5, 6.1, 6.2, 6.3, 6.4, 18.1, 20.4, 20.5**
    ///
    /// For any domain, any ordered pair <c>a &lt;= b</c>, any ramp size, any bubble-area budget, and
    /// either scale: every mapping (normalized intensity, ramp index, bubble area, bubble radius) is
    /// monotonic non-decreasing in the value; for a non-degenerate domain the lowest displayed value
    /// maps to the minimum intensity (normalized 0, ramp index 0) and the highest maps to the maximum
    /// (normalized 1, ramp index <c>rampSize - 1</c>); and a value of zero produces no bubble.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property IntensityMapping_IsMonotonicAndEndpointNormalized()
    {
        return Prop.ForAll(Arb.From(CaseGen), input =>
        {
            var (min, max, a, b, maxArea, rampSize, scale) = input;

            // --- Monotonicity (a <= b implies intensity(a) <= intensity(b)) for every mapping. ---
            var normA = Intensity.Normalize(a, min, max, scale);
            var normB = Intensity.Normalize(b, min, max, scale);
            if (normA > normB)
            {
                return false.Label(
                    $"Normalize not monotonic: f({a})={normA} > f({b})={normB} " +
                    $"(min={min}, max={max}, scale={scale})");
            }

            var rampA = Intensity.RampIndex(a, min, max, scale, rampSize);
            var rampB = Intensity.RampIndex(b, min, max, scale, rampSize);
            if (rampA > rampB)
            {
                return false.Label(
                    $"RampIndex not monotonic: f({a})={rampA} > f({b})={rampB} " +
                    $"(min={min}, max={max}, scale={scale}, rampSize={rampSize})");
            }

            var areaA = Intensity.BubbleArea(a, min, max, maxArea, scale);
            var areaB = Intensity.BubbleArea(b, min, max, maxArea, scale);
            if (areaA > areaB)
            {
                return false.Label(
                    $"BubbleArea not monotonic: f({a})={areaA} > f({b})={areaB} " +
                    $"(min={min}, max={max}, maxArea={maxArea}, scale={scale})");
            }

            var radiusA = Intensity.BubbleRadius(a, min, max, maxArea, scale);
            var radiusB = Intensity.BubbleRadius(b, min, max, maxArea, scale);
            if (radiusA > radiusB)
            {
                return false.Label(
                    $"BubbleRadius not monotonic: f({a})={radiusA} > f({b})={radiusB} " +
                    $"(min={min}, max={max}, maxRadius={maxArea}, scale={scale})");
            }

            // --- Normalized intensity always lands in [0, 1] (Req 6.1, 6.2). ---
            if (normA < 0.0 || normA > 1.0 || normB < 0.0 || normB > 1.0)
            {
                return false.Label(
                    $"Normalize out of [0,1]: f({a})={normA}, f({b})={normB} (min={min}, max={max})");
            }

            // --- Ramp index always lands in [0, rampSize - 1]. ---
            if (rampA < 0 || rampA > rampSize - 1 || rampB < 0 || rampB > rampSize - 1)
            {
                return false.Label(
                    $"RampIndex out of range: f({a})={rampA}, f({b})={rampB} (rampSize={rampSize})");
            }

            // --- Zero maps to empty / no bubble regardless of domain or scale (Req 3.5, 20.x). ---
            if (Intensity.BubbleArea(0.0, min, max, maxArea, scale) != 0.0)
            {
                return false.Label($"zero produced a non-empty bubble area (min={min}, max={max}, scale={scale})");
            }

            if (Intensity.BubbleRadius(0.0, min, max, maxArea, scale) != 0.0)
            {
                return false.Label($"zero produced a non-zero bubble radius (min={min}, max={max}, scale={scale})");
            }

            // --- Endpoint normalization for a non-degenerate domain (max > min). ---
            if (max > min)
            {
                // The lowest displayed value maps to the minimum intensity (Req 6.1, 18.1).
                var normMin = Intensity.Normalize(min, min, max, scale);
                if (normMin != 0.0)
                {
                    return false.Label($"min did not map to 0 intensity: got {normMin} (min={min}, max={max}, scale={scale})");
                }

                if (Intensity.RampIndex(min, min, max, scale, rampSize) != 0)
                {
                    return false.Label($"min did not map to ramp index 0 (min={min}, max={max}, scale={scale}, rampSize={rampSize})");
                }

                // The highest displayed value maps to the maximum intensity (Req 6.1, 18.1).
                var normMax = Intensity.Normalize(max, min, max, scale);
                if (normMax != 1.0)
                {
                    return false.Label($"max did not map to 1 intensity: got {normMax} (min={min}, max={max}, scale={scale})");
                }

                if (Intensity.RampIndex(max, min, max, scale, rampSize) != rampSize - 1)
                {
                    return false.Label(
                        $"max did not map to ramp index {rampSize - 1} (min={min}, max={max}, scale={scale}, rampSize={rampSize})");
                }
            }

            return true.ToProperty();
        });
    }
}
