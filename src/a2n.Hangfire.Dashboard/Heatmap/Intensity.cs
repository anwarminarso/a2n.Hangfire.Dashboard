using System;
using System.Collections.Generic;
using System.Globalization;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// The intensity scale applied when mapping a displayed value onto the color ramp or a bubble area.
/// </summary>
public enum IntensityScale
{
    /// <summary>Value is mapped linearly across the displayed domain.</summary>
    Linear,

    /// <summary>
    /// Value is mapped on a logarithmic curve across the displayed domain so that small values are
    /// more distinguishable; still endpoint-normalized and monotonic.
    /// </summary>
    Logarithmic
}

/// <summary>
/// The two theme variants the heatmap renders in. Mirrors the effective theme resolved by the
/// dashboard's existing <c>theme.js</c> mechanism (light or dark).
/// </summary>
public enum HeatmapTheme
{
    /// <summary>The light theme variant.</summary>
    Light,

    /// <summary>The dark theme variant.</summary>
    Dark
}

/// <summary>
/// An immutable sRGB color used by the contrast helpers. Channel components are stored as 8-bit
/// values (0&#8211;255) and parsed from / rendered to a <c>#rrggbb</c> hex string.
/// </summary>
public readonly struct RampColor : IEquatable<RampColor>
{
    /// <summary>The red channel (0&#8211;255).</summary>
    public byte R { get; }

    /// <summary>The green channel (0&#8211;255).</summary>
    public byte G { get; }

    /// <summary>The blue channel (0&#8211;255).</summary>
    public byte B { get; }

    /// <summary>Pure black (<c>#000000</c>).</summary>
    public static readonly RampColor Black = new RampColor(0, 0, 0);

    /// <summary>Pure white (<c>#ffffff</c>).</summary>
    public static readonly RampColor White = new RampColor(255, 255, 255);

    /// <summary>Initializes a new color from its red, green, and blue channels.</summary>
    public RampColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>
    /// Parses a <c>#rrggbb</c> (or <c>rrggbb</c>) hex string into a <see cref="RampColor"/>.
    /// </summary>
    /// <param name="hex">The six-digit hex color, with or without a leading <c>#</c>.</param>
    /// <returns>The parsed color.</returns>
    /// <exception cref="ArgumentException">The string is not a valid six-digit hex color.</exception>
    public static RampColor FromHex(string hex)
    {
        if (!TryFromHex(hex, out var color))
        {
            throw new ArgumentException($"'{hex}' is not a valid #rrggbb hex color.", nameof(hex));
        }

        return color;
    }

    /// <summary>
    /// Attempts to parse a <c>#rrggbb</c> (or <c>rrggbb</c>) hex string into a <see cref="RampColor"/>.
    /// </summary>
    public static bool TryFromHex(string hex, out RampColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var s = hex.Trim();
        if (s.Length > 0 && s[0] == '#')
        {
            s = s.Substring(1);
        }

        if (s.Length != 6)
        {
            return false;
        }

        if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = new RampColor(r, g, b);
        return true;
    }

    /// <summary>Renders the color as a lowercase <c>#rrggbb</c> hex string.</summary>
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    /// <inheritdoc />
    public override string ToString() => ToHex();

    /// <inheritdoc />
    public bool Equals(RampColor other) => R == other.R && G == other.G && B == other.B;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is RampColor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (R << 16) | (G << 8) | B;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RampColor left, RampColor right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RampColor left, RampColor right) => !left.Equals(right);
}

/// <summary>
/// Pure, dependency-light intensity and contrast helpers shared by the heatmap renderers.
/// Provides endpoint-normalized, monotonic value&#8594;intensity mappings (color-ramp index and
/// bubble area) under both linear and logarithmic scales, and WCAG 2.x relative-luminance / contrast
/// computation used to pick a numeric-label color that meets the 4.5:1 threshold against any ramp
/// shade in either theme variant.
/// </summary>
/// <remarks>
/// <para>
/// All mappings are <em>endpoint-normalized</em>: the lowest displayed value maps to the minimum
/// intensity (0) and the highest displayed value maps to the maximum intensity (1). A value of zero
/// (or any value at or below the domain minimum) maps to the empty/minimum intensity and produces no
/// bubble. Mappings are <em>monotonic non-decreasing</em>: for any <c>a &#8804; b</c> the intensity
/// of <c>a</c> never exceeds the intensity of <c>b</c>. The degenerate <c>min == max</c> domain is
/// resolved deterministically to the minimum intensity (0).
/// </para>
/// <para>
/// Validates portions of Requirements 3.4, 3.5, 3.7, 6.1, 6.2, 6.3, 6.4, 15.3, 15.5, 18.1, 20.4, 20.5.
/// </para>
/// </remarks>
public static class Intensity
{
    /// <summary>The number of shades in the sequential color ramp (index 0&#8230;<see cref="RampSize"/>-1).</summary>
    public const int RampSize = 6;

    /// <summary>The minimum acceptable WCAG contrast ratio for numeric cell labels (WCAG AA, normal text).</summary>
    public const double MinimumContrastRatio = 4.5;

    // Sequential color ramps from the agreed v4 mockup. Index 0 is the empty/minimum shade and the
    // last index is the maximum-intensity shade. Light and dark variants are theme-aware (Req 6.1).
    private static readonly RampColor[] LightRampColors =
    {
        RampColor.FromHex("#eef1f5"),
        RampColor.FromHex("#cfe8ef"),
        RampColor.FromHex("#8fd3c7"),
        RampColor.FromHex("#4cb3a9"),
        RampColor.FromHex("#2f8f9e"),
        RampColor.FromHex("#1f5f86"),
    };

    private static readonly RampColor[] DarkRampColors =
    {
        RampColor.FromHex("#1c2434"),
        RampColor.FromHex("#1f3b4d"),
        RampColor.FromHex("#1f5f6b"),
        RampColor.FromHex("#2f8f8a"),
        RampColor.FromHex("#4cc0a8"),
        RampColor.FromHex("#8fe3c7"),
    };

    /// <summary>Returns the sequential color ramp for the given theme (index 0 = empty/minimum shade).</summary>
    public static IReadOnlyList<RampColor> GetRamp(HeatmapTheme theme) =>
        theme == HeatmapTheme.Dark ? DarkRampColors : LightRampColors;

    /// <summary>
    /// Returns the ramp shade for a value, choosing the shade by <see cref="RampIndex(double, double, double, IntensityScale, int)"/>.
    /// </summary>
    public static RampColor GetShade(double value, double min, double max, HeatmapTheme theme, IntensityScale scale = IntensityScale.Linear)
    {
        var ramp = GetRamp(theme);
        var index = RampIndex(value, min, max, scale, ramp.Count);
        return ramp[index];
    }

    /// <summary>
    /// Normalizes <paramref name="value"/> onto <c>[0, 1]</c> across the displayed domain
    /// <c>[min, max]</c> under the requested <paramref name="scale"/>. Values at or below
    /// <paramref name="min"/> map to <c>0</c>; values at or above <paramref name="max"/> map to
    /// <c>1</c>. The degenerate <c>min == max</c> (or <c>max &lt; min</c>) domain returns <c>0</c>.
    /// </summary>
    /// <param name="value">The displayed value to normalize.</param>
    /// <param name="min">The lowest displayed value of the domain.</param>
    /// <param name="max">The highest displayed value of the domain.</param>
    /// <param name="scale">The intensity scale (linear or logarithmic).</param>
    /// <returns>A normalized intensity in <c>[0, 1]</c>, monotonic non-decreasing in <paramref name="value"/>.</returns>
    public static double Normalize(double value, double min, double max, IntensityScale scale = IntensityScale.Linear)
    {
        // Degenerate / invalid domain: no variation to express, resolve deterministically to 0.
        if (!IsFinite(min) || !IsFinite(max) || max <= min)
        {
            return 0.0;
        }

        if (!IsFinite(value))
        {
            // NaN/Infinity inputs clamp to the endpoints deterministically.
            return double.IsNaN(value) ? 0.0 : (value > max ? 1.0 : 0.0);
        }

        if (value <= min)
        {
            return 0.0;
        }

        if (value >= max)
        {
            return 1.0;
        }

        if (scale == IntensityScale.Logarithmic)
        {
            // Endpoint-normalized log curve over the offset-from-min domain: min -> 0, max -> 1.
            var numerator = Math.Log(1.0 + (value - min));
            var denominator = Math.Log(1.0 + (max - min));
            // denominator > 0 because max > min; guard anyway for determinism.
            return denominator > 0.0 ? Clamp01(numerator / denominator) : 0.0;
        }

        return Clamp01((value - min) / (max - min));
    }

    /// <summary>
    /// Maps <paramref name="value"/> to a discrete color-ramp index in <c>[0, rampSize - 1]</c>.
    /// The lowest displayed value (and any value of zero or below the domain minimum) maps to index
    /// <c>0</c> (the empty/minimum shade); the highest displayed value maps to <c>rampSize - 1</c>.
    /// </summary>
    /// <param name="value">The displayed value.</param>
    /// <param name="min">The lowest displayed value of the domain.</param>
    /// <param name="max">The highest displayed value of the domain.</param>
    /// <param name="scale">The intensity scale (linear or logarithmic).</param>
    /// <param name="rampSize">The number of shades in the ramp; defaults to <see cref="RampSize"/>.</param>
    /// <returns>A ramp index, monotonic non-decreasing in <paramref name="value"/>.</returns>
    public static int RampIndex(double value, double min, double max, IntensityScale scale = IntensityScale.Linear, int rampSize = RampSize)
    {
        if (rampSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rampSize), rampSize, "Ramp size must be at least 1.");
        }

        if (rampSize == 1)
        {
            return 0;
        }

        var t = Normalize(value, min, max, scale);
        var index = (int)Math.Round(t * (rampSize - 1), MidpointRounding.AwayFromZero);
        if (index < 0)
        {
            index = 0;
        }
        else if (index > rampSize - 1)
        {
            index = rampSize - 1;
        }

        return index;
    }

    /// <summary>
    /// Returns the bubble <em>area</em> for a value as a fraction of <paramref name="maxArea"/>, so
    /// that area &#8212; not radius &#8212; scales with the value. A value of zero (or at/below the
    /// domain minimum) yields <c>0</c> (no bubble). Monotonic non-decreasing in <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The displayed value.</param>
    /// <param name="min">The lowest displayed value of the domain.</param>
    /// <param name="max">The highest displayed value of the domain.</param>
    /// <param name="maxArea">The bubble area assigned to the highest displayed value.</param>
    /// <param name="scale">The intensity scale (linear or logarithmic).</param>
    /// <returns>The bubble area in <c>[0, maxArea]</c>; <c>0</c> means render no bubble.</returns>
    public static double BubbleArea(double value, double min, double max, double maxArea, IntensityScale scale = IntensityScale.Linear)
    {
        if (maxArea <= 0.0 || value <= 0.0)
        {
            // Zero value (or no area budget) maps to no bubble (Req 3.5, 20.x).
            return 0.0;
        }

        return Normalize(value, min, max, scale) * maxArea;
    }

    /// <summary>
    /// Returns the bubble <em>radius</em> for a value, derived from <see cref="BubbleArea"/> so the
    /// radius scales with the square root of the value (area-proportional encoding). A value of zero
    /// yields a radius of <c>0</c> (no bubble). Monotonic non-decreasing in <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The displayed value.</param>
    /// <param name="min">The lowest displayed value of the domain.</param>
    /// <param name="max">The highest displayed value of the domain.</param>
    /// <param name="maxRadius">The bubble radius assigned to the highest displayed value.</param>
    /// <param name="scale">The intensity scale (linear or logarithmic).</param>
    /// <returns>The bubble radius in <c>[0, maxRadius]</c>; <c>0</c> means render no bubble.</returns>
    public static double BubbleRadius(double value, double min, double max, double maxRadius, IntensityScale scale = IntensityScale.Linear)
    {
        if (maxRadius <= 0.0 || value <= 0.0)
        {
            return 0.0;
        }

        // area = t * (maxRadius^2); radius = sqrt(area) = maxRadius * sqrt(t).
        var t = Normalize(value, min, max, scale);
        return maxRadius * Math.Sqrt(t);
    }

    /// <summary>
    /// Computes the WCAG 2.x relative luminance of a color in <c>[0, 1]</c>, per the sRGB
    /// linearization and the <c>0.2126 R + 0.7152 G + 0.0722 B</c> weighting.
    /// </summary>
    /// <param name="color">The sRGB color.</param>
    /// <returns>The relative luminance in <c>[0, 1]</c>.</returns>
    public static double RelativeLuminance(RampColor color)
    {
        var r = LinearizeChannel(color.R / 255.0);
        var g = LinearizeChannel(color.G / 255.0);
        var b = LinearizeChannel(color.B / 255.0);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>
    /// Computes the WCAG 2.x contrast ratio between two colors, ranging from <c>1.0</c> (identical
    /// luminance) to <c>21.0</c> (black vs white). Symmetric in its arguments.
    /// </summary>
    /// <param name="a">The first color.</param>
    /// <param name="b">The second color.</param>
    /// <returns>The contrast ratio <c>(Lhi + 0.05) / (Llo + 0.05)</c>.</returns>
    public static double ContrastRatio(RampColor a, RampColor b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Determines whether the contrast ratio between two colors meets the supplied threshold
    /// (defaulting to <see cref="MinimumContrastRatio"/>).
    /// </summary>
    public static bool MeetsContrast(RampColor foreground, RampColor background, double threshold = MinimumContrastRatio) =>
        ContrastRatio(foreground, background) >= threshold;

    /// <summary>
    /// Picks the numeric-label color (black or white) that yields the greater WCAG contrast against
    /// <paramref name="background"/>. Because choosing the better of pure black and pure white
    /// guarantees a contrast ratio of at least ~4.58:1 for any background, the returned color always
    /// meets the <see cref="MinimumContrastRatio"/> threshold &#8212; for every ramp shade in both
    /// the light and dark theme variants (Req 15.3, 15.5).
    /// </summary>
    /// <param name="background">The cell background shade the label is drawn over.</param>
    /// <returns><see cref="RampColor.Black"/> or <see cref="RampColor.White"/>, whichever contrasts more.</returns>
    public static RampColor PickLabelColor(RampColor background)
    {
        var contrastWithBlack = ContrastRatio(RampColor.Black, background);
        var contrastWithWhite = ContrastRatio(RampColor.White, background);

        // Prefer black on ties for a deterministic result (matches lighter backgrounds).
        return contrastWithBlack >= contrastWithWhite ? RampColor.Black : RampColor.White;
    }

    private static double LinearizeChannel(double channel)
    {
        return channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static double Clamp01(double value)
    {
        if (value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
