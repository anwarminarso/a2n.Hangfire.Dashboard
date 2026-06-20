using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Heatmap;

namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Deterministic queue → color mapping shared by every dashboard surface that renders a queue badge
/// (the heatmap recurring-job table and queue chips, the servers page, search results, …). A queue
/// name always maps to the same color so the same queue reads identically everywhere.
/// </summary>
/// <remarks>
/// <para>
/// The 20-entry palette and the FNV-style hashing below are mirrored verbatim by the
/// <c>queueColor</c> helper in <c>Content/js/heatmap.js</c> so that server-rendered badges and the
/// JS-rendered heatmap dots/legends agree on the color for any given queue. Keep the two in sync.
/// </para>
/// <para>
/// The label color is picked for WCAG-AA contrast against the resolved background via
/// <see cref="Intensity.PickLabelColor"/>, so a badge is always legible regardless of which palette
/// entry it lands on.
/// </para>
/// </remarks>
public static class QueueColors
{
    /// <summary>
    /// The fixed palette of 20 distinct, theme-neutral hues. Index 0 is the fallback used for an
    /// empty/unknown queue name. Mirrored by <c>QUEUE_PALETTE</c> in <c>heatmap.js</c>.
    /// </summary>
    private static readonly string[] Palette =
    {
        "#4dabf7", "#f783ac", "#ffa94d", "#38d9a9", "#b197fc",
        "#ffe066", "#ff8787", "#9775fa", "#74c0fc", "#63e6be",
        "#ff922b", "#a9e34b", "#e599f7", "#66d9e8", "#ffc078",
        "#8ce99a", "#da77f2", "#f06595", "#5c7cfa", "#3bc9db",
    };

    /// <summary>
    /// Returns the stable <c>#rrggbb</c> background color for a queue name. A null/empty name maps to
    /// the first palette entry. The mapping is deterministic and matches the JS renderer.
    /// </summary>
    public static string ColorFor(string queue)
    {
        if (string.IsNullOrEmpty(queue))
        {
            return Palette[0];
        }

        return Palette[HashOf(queue) % (uint)Palette.Length];
    }

    /// <summary>
    /// Builds a collision-minimizing queue → color map for a set of queues shown together (e.g. the
    /// heatmap's available queues). Each queue prefers its hashed palette slot; when that slot is
    /// already taken the assignment probes forward to the next free slot, so up to
    /// <see cref="PaletteSize"/> queues each receive a distinct color. Queues are processed in ordinal
    /// order so the result is deterministic for a given set. Beyond <see cref="PaletteSize"/> queues
    /// colors necessarily repeat, falling back to the plain hashed color.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildColorMap(IEnumerable<string> queues)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queues is null)
        {
            return map;
        }

        var ordered = queues
            .Where(q => !string.IsNullOrEmpty(q))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(q => q, StringComparer.Ordinal)
            .ToList();

        var used = new bool[Palette.Length];
        foreach (var queue in ordered)
        {
            var start = (int)(HashOf(queue) % (uint)Palette.Length);
            var idx = start;
            var probes = 0;
            while (used[idx] && probes < Palette.Length)
            {
                idx = (idx + 1) % Palette.Length;
                probes++;
            }

            // All slots taken (more queues than palette entries) — fall back to the hashed color.
            map[queue] = probes < Palette.Length ? Palette[idx] : Palette[start];
            if (probes < Palette.Length)
            {
                used[idx] = true;
            }
        }

        return map;
    }

    /// <summary>The number of entries in the palette.</summary>
    public static int PaletteSize => Palette.Length;

    /// <summary>
    /// FNV-style rolling hash with a seed of 7 and a multiplier of 31, evaluated as an unsigned
    /// 32-bit integer so it wraps identically to the JS <c>(seed * 31 + c) &gt;&gt;&gt; 0</c>.
    /// </summary>
    private static uint HashOf(string value)
    {
        uint seed = 7;
        foreach (var c in value)
        {
            seed = unchecked((seed * 31) + c);
        }

        return seed;
    }

    /// <summary>
    /// Returns the label color (<c>#000000</c> or <c>#ffffff</c>) that meets WCAG-AA contrast against
    /// the supplied <c>#rrggbb</c> background.
    /// </summary>
    public static string TextColorFor(string backgroundHex)
    {
        return RampColor.TryFromHex(backgroundHex, out var bg)
            ? Intensity.PickLabelColor(bg).ToHex()
            : "#000000";
    }
}
