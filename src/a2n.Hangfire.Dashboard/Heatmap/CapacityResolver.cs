using System.Globalization;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure helper that resolves the active <c>Worker_Capacity</c> and validates operator-supplied
/// manual override values. The detected capacity is the sum of the worker counts reported by the
/// running servers; a valid manual override always wins over the detected value.
/// </summary>
/// <remarks>
/// Validates Requirements 5.1, 5.2, 5.4, and 5.5.
/// </remarks>
public static class CapacityResolver
{
    /// <summary>The smallest accepted manual override (inclusive).</summary>
    public const int MinManual = 1;

    /// <summary>The largest accepted manual override (inclusive).</summary>
    public const int MaxManual = 100_000;

    /// <summary>
    /// Resolves the active worker capacity from the detected worker sum and an optional manual
    /// override.
    /// </summary>
    /// <param name="detectedWorkerSum">
    /// The sum of the worker counts reported by the running servers. A value of zero or less
    /// (no servers) resolves to a detected capacity of 1 (Requirements 5.1, 5.4).
    /// </param>
    /// <param name="manualOverride">
    /// An optional manual override. When present, it is used and the source is reported as
    /// <see cref="CapacitySource.ManualOverride"/> (Requirement 5.2). Callers are expected to have
    /// validated the override via <see cref="TryValidateManual"/> before passing it here; this
    /// method still clamps it into the accepted range defensively.
    /// </param>
    /// <returns>The resolved capacity together with its source.</returns>
    public static CapacityResult Resolve(int detectedWorkerSum, int? manualOverride)
    {
        if (manualOverride.HasValue)
        {
            var clamped = manualOverride.Value;
            if (clamped < MinManual) clamped = MinManual;
            else if (clamped > MaxManual) clamped = MaxManual;

            return new CapacityResult(clamped, CapacitySource.ManualOverride);
        }

        var detected = detectedWorkerSum <= 0 ? 1 : detectedWorkerSum;
        return new CapacityResult(detected, CapacitySource.Detected);
    }

    /// <summary>
    /// Validates a raw manual capacity input. The input must parse strictly as an integer within
    /// the inclusive range <c>[1, 100000]</c>. On rejection the caller should retain the previously
    /// active capacity (Requirement 5.5).
    /// </summary>
    /// <param name="raw">The raw operator input (e.g. from a text field).</param>
    /// <param name="value">The parsed value when the input is valid; otherwise 0.</param>
    /// <returns><c>true</c> when the input is a valid manual capacity; otherwise <c>false</c>.</returns>
    public static bool TryValidateManual(string raw, out int value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();

        if (!int.TryParse(
                trimmed,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        if (parsed < MinManual || parsed > MaxManual)
            return false;

        value = parsed;
        return true;
    }
}
