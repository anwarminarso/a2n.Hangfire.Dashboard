using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="Intensity.PickLabelColor"/> WCAG contrast guarantees.
///
/// **Property 13: Numeric cell labels meet the 4.5:1 contrast threshold in both themes**
/// **Validates: Requirements 15.3, 15.5**
///
/// For any ramp shade — both arbitrary sRGB backgrounds and every actual shade returned by
/// <see cref="Intensity.GetRamp"/> for the light and dark theme variants — the label color chosen
/// by <see cref="Intensity.PickLabelColor"/> achieves a WCAG 2.x contrast ratio of at least
/// <see cref="Intensity.MinimumContrastRatio"/> (4.5:1) against that shade. This holds because
/// picking the better of pure black and pure white guarantees ≥ ~4.58:1 against any background, so
/// numeric cell labels remain legible over every ramp shade in either theme (Req 15.3, 15.5).
/// </summary>
public class LabelContrastProperties
{
    /// <summary>
    /// Every actual ramp shade across both theme variants. The chosen label color must clear the
    /// threshold for each of these real backgrounds in addition to arbitrary colors.
    /// </summary>
    private static readonly RampColor[] AllThemeShades = BuildAllThemeShades();

    private static RampColor[] BuildAllThemeShades()
    {
        var shades = new List<RampColor>();
        foreach (var theme in new[] { HeatmapTheme.Light, HeatmapTheme.Dark })
        {
            shades.AddRange(Intensity.GetRamp(theme));

            // Also include the shades produced through the value→shade mapping over a representative
            // domain, exercising GetShade across the full ramp index range for each theme.
            for (var v = 0; v <= Intensity.RampSize; v++)
            {
                foreach (var scale in new[] { IntensityScale.Linear, IntensityScale.Logarithmic })
                {
                    shades.Add(Intensity.GetShade(v, 0, Intensity.RampSize, theme, scale));
                }
            }
        }

        return shades.Distinct().ToArray();
    }

    /// <summary>An arbitrary sRGB color with each channel drawn uniformly from the full 0–255 range.</summary>
    private static Gen<RampColor> RampColorGen =>
        from r in Gen.Choose(0, 255)
        from g in Gen.Choose(0, 255)
        from b in Gen.Choose(0, 255)
        select new RampColor((byte)r, (byte)g, (byte)b);

    /// <summary>
    /// **Property 13: Numeric cell labels meet the 4.5:1 contrast threshold in both themes**
    /// **Validates: Requirements 15.3, 15.5**
    ///
    /// For any arbitrary background color, and additionally for every real ramp shade in the light
    /// and dark theme variants, the contrast ratio between the picked label color and that
    /// background is at least 4.5:1.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property PickedLabelColor_MeetsContrastThreshold_ForAnyBackground_InBothThemes()
    {
        return Prop.ForAll(Arb.From(RampColorGen), background =>
        {
            // (1) Arbitrary background: the picked label must clear the WCAG AA threshold.
            var label = Intensity.PickLabelColor(background);
            var ratio = Intensity.ContrastRatio(label, background);
            if (ratio < Intensity.MinimumContrastRatio)
            {
                return false.Label(
                    $"arbitrary background {background.ToHex()} -> label {label.ToHex()} " +
                    $"ratio {ratio:F3} < {Intensity.MinimumContrastRatio}");
            }

            // (2) Every real ramp shade in both themes must also clear the threshold, independent of
            //     the generated input, so the property covers the actual rendered palette (Req 15.5).
            foreach (var shade in AllThemeShades)
            {
                var shadeLabel = Intensity.PickLabelColor(shade);
                var shadeRatio = Intensity.ContrastRatio(shadeLabel, shade);
                if (shadeRatio < Intensity.MinimumContrastRatio)
                {
                    return false.Label(
                        $"ramp shade {shade.ToHex()} -> label {shadeLabel.ToHex()} " +
                        $"ratio {shadeRatio:F3} < {Intensity.MinimumContrastRatio}");
                }
            }

            return true.ToProperty();
        });
    }
}
