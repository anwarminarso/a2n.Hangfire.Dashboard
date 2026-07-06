using System;
using System.Globalization;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for invalid manual-capacity and Top-N input rejection.
///
/// **Property 16: Invalid capacity and Top-N inputs are rejected and the previous value is retained**
/// **Validates: Requirements 5.5, 13.7**
///
/// For any raw manual-capacity input, <see cref="CapacityResolver.TryValidateManual"/> accepts the
/// input exactly when it parses strictly as an integer within <c>[1, 100000]</c> and rejects it
/// otherwise (non-integer, empty/whitespace, decimal, or out-of-range); for any raw Top-N input,
/// <see cref="TopN.TryValidate"/> accepts the input exactly when it parses strictly as an integer
/// within <c>[1, 100]</c> and rejects it otherwise. On every rejection the validator returns
/// <c>false</c> (so the caller keeps the previously active value) and never produces a usable
/// replacement.
/// </summary>
public class InvalidCapacityTopNRejectionProperties
{
    private const int CapacityMin = CapacityResolver.MinManual; // 1
    private const int CapacityMax = CapacityResolver.MaxManual; // 100_000
    private const int TopNMin = TopN.MinValue;                  // 1
    private const int TopNMax = TopN.MaxValue;                  // 100

    // ---- Shared invalid-string categories (independent of the numeric range) ----

    /// <summary>Empty / whitespace-only inputs. Always invalid for both validators.</summary>
    private static Gen<string> EmptyOrWhitespaceGen =>
        Gen.Elements("", " ", "   ", "\t", "\t ", "\n");

    /// <summary>
    /// Non-numeric gibberish that can never parse as an integer (letters, symbols, mixed tokens).
    /// </summary>
    private static Gen<string> NonIntegerGen =>
        Gen.Elements(
            "abc", "1a", "a1", "ten", "NaN", "0x1F", "1,000", "1.", ".5",
            "+", "-", "  ?  ", "1e3", "Infinity", "twenty", "#5", "5%");

    /// <summary>
    /// Decimal / fractional strings. These are real numbers but not strict integers, so both
    /// validators (which parse integers only) must reject them.
    /// </summary>
    private static Gen<string> DecimalGen =>
        from whole in Gen.Choose(1, 100)
        from frac in Gen.Choose(1, 99)
        select string.Format(CultureInfo.InvariantCulture, "{0}.{1}", whole, frac);

    // ---- Capacity (Requirement 5.5) generators ----

    /// <summary>Valid manual-capacity integers in the inclusive range [1, 100000].</summary>
    private static Gen<string> ValidCapacityGen =>
        Gen.Choose(CapacityMin, CapacityMax)
            .Select(v => v.ToString(CultureInfo.InvariantCulture));

    /// <summary>Integers below the minimum (0, negatives) — rejected by Req 5.5.</summary>
    private static Gen<string> CapacityBelowMinGen =>
        Gen.Choose(-100_000, CapacityMin - 1)
            .Select(v => v.ToString(CultureInfo.InvariantCulture));

    /// <summary>Integers above the maximum — rejected by Req 5.5.</summary>
    private static Gen<string> CapacityAboveMaxGen =>
        Gen.Choose(CapacityMax + 1, CapacityMax + 1_000_000)
            .Select(v => v.ToString(CultureInfo.InvariantCulture));

    private static Gen<string> InvalidCapacityGen =>
        Gen.OneOf(
            EmptyOrWhitespaceGen,
            NonIntegerGen,
            DecimalGen,
            CapacityBelowMinGen,
            CapacityAboveMaxGen);

    // ---- Top-N (Requirement 13.7) generators ----

    /// <summary>Valid Top-N integers in the inclusive range [1, 100].</summary>
    private static Gen<string> ValidTopNGen =>
        Gen.Choose(TopNMin, TopNMax)
            .Select(v => v.ToString(CultureInfo.InvariantCulture));

    /// <summary>Integers below the minimum (0, negatives) — rejected by Req 13.7.</summary>
    private static Gen<string> TopNBelowMinGen =>
        Gen.Choose(-1_000, TopNMin - 1)
            .Select(v => v.ToString(CultureInfo.InvariantCulture));

    /// <summary>Integers above the maximum — rejected by Req 13.7.</summary>
    private static Gen<string> TopNAboveMaxGen =>
        Gen.Choose(TopNMax + 1, TopNMax + 1_000_000)
            .Select(v => v.ToString(CultureInfo.InvariantCulture));

    private static Gen<string> InvalidTopNGen =>
        Gen.OneOf(
            EmptyOrWhitespaceGen,
            NonIntegerGen,
            DecimalGen,
            TopNBelowMinGen,
            TopNAboveMaxGen);

    /// <summary>
    /// **Property 16: Invalid manual capacity inputs are rejected (previous value retained)**
    /// **Validates: Requirement 5.5**
    ///
    /// Every invalid raw input is rejected with <c>value == 0</c>, while every valid integer in
    /// <c>[1, 100000]</c> is accepted and round-trips to its parsed value. Asserting both directions
    /// keeps the rejection meaningful (an "always reject" implementation would fail the valid case).
    /// </summary>
    [Property(MaxTest = 200)]
    public Property InvalidManualCapacity_IsRejected_ValidIsAccepted()
    {
        var arb = Arb.From(
            from raw in Gen.Frequency(
                Tuple.Create(1, ValidCapacityGen.Select(s => (Raw: s, ExpectValid: true))),
                Tuple.Create(1, InvalidCapacityGen.Select(s => (Raw: s, ExpectValid: false))))
            select raw);

        return Prop.ForAll(arb, input =>
        {
            var (raw, expectValid) = input;
            var accepted = CapacityResolver.TryValidateManual(raw, out var value);

            if (expectValid)
            {
                // Valid input is accepted and the out value equals the strictly-parsed integer.
                var parsed = int.Parse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return (accepted && value == parsed)
                    .Label($"valid capacity '{raw}' expected accepted with value {parsed}, " +
                           $"got accepted={accepted}, value={value}");
            }

            // Invalid input is rejected and yields no usable replacement (value == 0), so the
            // caller retains the previously active capacity (Req 5.5).
            return (!accepted && value == 0)
                .Label($"invalid capacity '{raw}' expected rejected with value 0, " +
                       $"got accepted={accepted}, value={value}");
        });
    }

    /// <summary>
    /// **Property 16: Invalid Top-N inputs are rejected (previous value retained)**
    /// **Validates: Requirement 13.7**
    ///
    /// Every invalid raw input is rejected with <c>value == 0</c>, while every valid integer in
    /// <c>[1, 100]</c> is accepted and round-trips to its parsed value.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property InvalidTopN_IsRejected_ValidIsAccepted()
    {
        var arb = Arb.From(
            from raw in Gen.Frequency(
                Tuple.Create(1, ValidTopNGen.Select(s => (Raw: s, ExpectValid: true))),
                Tuple.Create(1, InvalidTopNGen.Select(s => (Raw: s, ExpectValid: false))))
            select raw);

        return Prop.ForAll(arb, input =>
        {
            var (raw, expectValid) = input;
            var accepted = TopN.TryValidate(raw, out var value);

            if (expectValid)
            {
                var parsed = int.Parse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return (accepted && value == parsed)
                    .Label($"valid Top-N '{raw}' expected accepted with value {parsed}, " +
                           $"got accepted={accepted}, value={value}");
            }

            // Invalid input is rejected and yields no usable replacement (value == 0), so the
            // caller retains the previously active Top-N value (Req 13.7).
            return (!accepted && value == 0)
                .Label($"invalid Top-N '{raw}' expected rejected with value 0, " +
                       $"got accepted={accepted}, value={value}");
        });
    }

    /// <summary>
    /// **Property 16 (anchors): Requirements 5.5, 13.7**
    ///
    /// Concrete, hand-checked invalid inputs across every category, independent of the randomized
    /// generators, asserted against both validators.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    public void KnownInvalidInputs_AreRejected_ByBothValidators(string raw)
    {
        Assert.False(CapacityResolver.TryValidateManual(raw, out var capacity));
        Assert.Equal(0, capacity);

        Assert.False(TopN.TryValidate(raw, out var topN));
        Assert.Equal(0, topN);
    }
}
