using System;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="CapacityResolver.Resolve"/> capacity resolution and override
/// selection.
///
/// **Property 15: Capacity resolution and override selection**
/// **Validates: Requirements 5.1, 5.2, 5.4**
///
/// For any detected worker sum and any optional manual override, the detected capacity equals the
/// sum of the server worker counts (with no servers — a sum of zero or less — resolving to 1), a
/// valid manual override always wins over the detected value, and the reported source reflects
/// whether the active value was detected or manually overridden.
/// </summary>
public class CapacityResolutionProperties
{
    /// <summary>The smallest accepted manual override (inclusive) per Requirement 5.2.</summary>
    private const int MinManual = 1;

    /// <summary>The largest accepted manual override (inclusive) per Requirement 5.2.</summary>
    private const int MaxManual = 100_000;

    /// <summary>
    /// Detected worker sums spanning negative values, zero (no servers), and positive sums up to a
    /// large fleet, so the no-servers fallback (Req 5.4) and the positive-sum path (Req 5.1) are
    /// both exercised.
    /// </summary>
    private static Gen<int> DetectedWorkerSumGen => Gen.Choose(-50, 100_000);

    /// <summary>
    /// Models an optional manual override. When <c>HasOverride</c> is <c>false</c> no manual
    /// capacity is active; otherwise <c>Value</c> holds a value valid under Requirement 5.2 (an
    /// integer within <c>[1, 100000]</c>).
    /// </summary>
    private static Gen<(bool HasOverride, int Value)> ManualOverrideGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant((false, 0))),
            Tuple.Create(2, Gen.Choose(MinManual, MaxManual).Select(v => (true, v))));

    /// <summary>
    /// **Property 15: Capacity resolution and override selection**
    /// **Validates: Requirements 5.1, 5.2, 5.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Resolve_SelectsCapacityAndSource()
    {
        var arb = Arb.From(
            from sum in DetectedWorkerSumGen
            from manual in ManualOverrideGen
            select (sum, manual));

        return Prop.ForAll(arb, input =>
        {
            var (sum, manualOption) = input;
            int? manual = manualOption.HasOverride ? manualOption.Value : (int?)null;

            var result = CapacityResolver.Resolve(sum, manual);

            if (manual.HasValue)
            {
                // A valid manual override always wins over the detected value (Req 5.2).
                if (result.Source != CapacitySource.ManualOverride)
                {
                    return false.Label(
                        $"override present but source={result.Source} (expected ManualOverride)");
                }

                if (result.Capacity != manual.Value)
                {
                    return false.Label(
                        $"override capacity: expected {manual.Value} but got {result.Capacity}");
                }

                return true.ToProperty();
            }

            // No override: the source is always Detected (Req 5.1, 5.4).
            if (result.Source != CapacitySource.Detected)
            {
                return false.Label(
                    $"no override but source={result.Source} (expected Detected)");
            }

            if (sum <= 0)
            {
                // No servers → detected capacity of 1 (Req 5.4).
                return (result.Capacity == 1).Label(
                    $"no-servers fallback: expected 1 but got {result.Capacity} (sum={sum})");
            }

            // Positive sum → capacity equals the sum of server worker counts (Req 5.1).
            return (result.Capacity == sum).Label(
                $"detected capacity: expected {sum} but got {result.Capacity}");
        });
    }
}
