using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property test for <see cref="DemandProfileProvider.ShiftToViewerLocal"/>, the pure rotation that
/// re-keys the ad-hoc <see cref="DemandProfile"/> from its native UTC <c>day-of-week × hour</c>
/// coordinates into the viewer's local coordinates so the demand shading aligns with the cron matrix
/// (heatmap backlog item #4 — viewer-time-zone alignment of the demand profile).
///
/// <para>// Feature: recurring-schedule-heatmap, Property 30 (supplementary): Demand-profile viewer-tz shift is a value-preserving rotation</para>
/// <para>// Validates: Requirements 8.2 (parity for ad-hoc demand)</para>
///
/// For any demand profile and any whole-hour viewer offset, the shift (a) preserves the total demand
/// mass and the Min/Max/queue metadata, (b) maps every slot to the deterministic
/// <c>(dow, hour) + offsetHours</c> coordinate with day-of-week wrap, and (c) round-trips: shifting by
/// <c>+offset</c> then <c>-offset</c> recovers the original slot dictionary.
/// </summary>
public class DemandProfileShiftProperties
{
    private const double Tolerance = 1e-9;

    private static readonly string[] Queues = { "alpha", "billing", "default", "reports" };

    private static Gen<(string Queue, int Dow, int Hour, double Value)> SlotGen =>
        from queue in Gen.Elements(Queues)
        from dow in Gen.Choose(0, 6)
        from hour in Gen.Choose(0, 23)
        from value in Gen.Choose(1, 50)
        select (queue, dow, hour, (double)value);

    private static DemandProfile BuildProfile(IEnumerable<(string Queue, int Dow, int Hour, double Value)> descs)
    {
        var slots = new Dictionary<DemandSlotKey, double>();
        foreach (var d in descs)
        {
            var key = new DemandSlotKey(d.Queue, d.Dow, d.Hour);
            slots.TryGetValue(key, out var running);
            slots[key] = running + d.Value;
        }

        var min = slots.Count == 0 ? 0d : slots.Values.Min();
        var max = slots.Count == 0 ? 0d : slots.Values.Max();

        return new DemandProfile(
            slots,
            slots.Keys.Select(k => k.Queue).Distinct().OrderBy(q => q, StringComparer.Ordinal).ToList(),
            LoadMetric.FireCount,
            AggregationStatistic.Average,
            RequestedLookbackWeeks: 4,
            AvailableSpanWeeks: 4,
            IsSpanReduced: false,
            Min: min,
            Max: max);
    }

    /// <summary>
    /// **Property 30 (supplementary): Demand-profile viewer-tz shift is a value-preserving rotation**
    /// **Validates: Requirements 8.2 (parity for ad-hoc demand)**
    /// </summary>
    [Property(MaxTest = 300)]
    public Property Shift_IsValuePreservingRotation_AndRoundTrips()
    {
        var arb = Arb.From(
            from count in Gen.Choose(0, 80)
            from descs in Gen.ArrayOf(count, SlotGen)
            // Offsets spanning the real-world UTC range (−12 .. +14), in whole hours.
            from offsetHours in Gen.Choose(-12, 14)
            select (descs, offsetHours));

        return Prop.ForAll(arb, input =>
        {
            var (descs, offsetHours) = input;
            var profile = BuildProfile(descs);
            var offset = TimeSpan.FromHours(offsetHours);

            var shifted = DemandProfileProvider.ShiftToViewerLocal(profile, offset);

            // (a) Total demand mass is preserved (rotation only re-keys slots).
            var originalSum = profile.Slots.Values.Sum();
            var shiftedSum = shifted.Slots.Values.Sum();
            if (Math.Abs(originalSum - shiftedSum) > Tolerance)
            {
                return false.Label($"mass not preserved: original={originalSum} shifted={shiftedSum}");
            }

            // Min/Max/queue metadata are unchanged.
            if (Math.Abs(shifted.Min - profile.Min) > Tolerance || Math.Abs(shifted.Max - profile.Max) > Tolerance)
            {
                return false.Label("Min/Max changed by the shift");
            }

            if (!shifted.Queues.SequenceEqual(profile.Queues))
            {
                return false.Label("queue set changed by the shift");
            }

            // The number of populated slots is invariant (bijective rotation, no collisions).
            if (shifted.Slots.Count != profile.Slots.Count)
            {
                return false.Label($"slot count changed: original={profile.Slots.Count} shifted={shifted.Slots.Count}");
            }

            // (b) Each original slot maps to the deterministic (dow,hour)+offset coordinate.
            foreach (var kv in profile.Slots)
            {
                var total = kv.Key.Hour + offsetHours;
                var localHour = ((total % 24) + 24) % 24;
                var dayShift = (int)Math.Floor(total / 24d);
                var localDow = ((kv.Key.DayOfWeek + dayShift) % 7 + 7) % 7;
                var expectedKey = new DemandSlotKey(kv.Key.Queue, localDow, localHour);

                if (!shifted.Slots.TryGetValue(expectedKey, out var v) || Math.Abs(v - kv.Value) > Tolerance)
                {
                    return false.Label(
                        $"slot {kv.Key} (val {kv.Value}) did not map to {expectedKey} under offset {offsetHours}");
                }
            }

            // (c) Round-trip: +offset then −offset recovers the original slots exactly.
            var roundTrip = DemandProfileProvider.ShiftToViewerLocal(shifted, TimeSpan.FromHours(-offsetHours));
            if (roundTrip.Slots.Count != profile.Slots.Count)
            {
                return false.Label("round-trip slot count mismatch");
            }

            foreach (var kv in profile.Slots)
            {
                if (!roundTrip.Slots.TryGetValue(kv.Key, out var v) || Math.Abs(v - kv.Value) > Tolerance)
                {
                    return false.Label($"round-trip did not recover slot {kv.Key}");
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>A zero (UTC) offset returns the profile reference unchanged.</summary>
    [Fact]
    public void ZeroOffset_ReturnsProfileUnchanged()
    {
        var profile = BuildProfile(new[] { ("default", 3, 14, 5d), ("billing", 0, 2, 9d) });
        var shifted = DemandProfileProvider.ShiftToViewerLocal(profile, TimeSpan.Zero);
        Assert.Same(profile, shifted);
    }

    /// <summary>A +7h shift moves a UTC midnight (Sun 00:00) slot to Sun 07:00 with no day wrap.</summary>
    [Fact]
    public void PositiveOffset_ShiftsHour_NoDayWrap()
    {
        var profile = BuildProfile(new[] { ("default", 0, 0, 5d) }); // Sunday 00:00 UTC
        var shifted = DemandProfileProvider.ShiftToViewerLocal(profile, TimeSpan.FromHours(7));
        Assert.True(shifted.Slots.TryGetValue(new DemandSlotKey("default", 0, 7), out var v));
        Assert.Equal(5d, v, 9);
    }

    /// <summary>A +7h shift on a late-UTC slot (Sat 20:00) wraps forward to Sun 03:00.</summary>
    [Fact]
    public void PositiveOffset_WrapsDayForward()
    {
        var profile = BuildProfile(new[] { ("default", 6, 20, 5d) }); // Saturday 20:00 UTC
        var shifted = DemandProfileProvider.ShiftToViewerLocal(profile, TimeSpan.FromHours(7));
        Assert.True(shifted.Slots.TryGetValue(new DemandSlotKey("default", 0, 3), out var v)); // Sunday 03:00
        Assert.Equal(5d, v, 9);
    }
}
