using System;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the W3C Trace Context <c>traceparent</c> codec
/// (<see cref="W3CTraceParent"/>).
///
/// Feature: integrations-v2-6, Property 1: W3C traceparent round-trip
///
/// **Property 1: W3C traceparent round-trip** — for any valid <see cref="W3CTraceParent"/> value,
/// formatting it to its string form and then parsing that string yields an equal value (same
/// <see cref="W3CTraceParent.Version"/>, <see cref="W3CTraceParent.TraceId"/>,
/// <see cref="W3CTraceParent.ParentId"/>, and <see cref="W3CTraceParent.TraceFlags"/>).
///
/// **Validates: Requirements 1.3**
/// </summary>
public class W3CTraceParentRoundTripProperties
{
    private const int TraceIdHexLength = 32;
    private const int ParentIdHexLength = 16;

    private static readonly char[] LowercaseHexDigits =
        "0123456789abcdef".ToCharArray();

    /// <summary>A single lowercase hex character (0-9, a-f).</summary>
    private static Gen<char> LowercaseHexCharGen =>
        Gen.Elements(LowercaseHexDigits);

    /// <summary>
    /// A lowercase hex string of the given length that is NOT all zeros (per the W3C grammar,
    /// an all-zero trace-id or parent-id is invalid).
    /// </summary>
    private static Gen<string> NonZeroHexStringGen(int length) =>
        Gen.ArrayOf(length, LowercaseHexCharGen)
            .Select(chars => new string(chars))
            .Where(s => s.Any(c => c != '0'));

    /// <summary>
    /// Version byte in the valid range 0x00–0xfe. The reserved value 0xff is excluded because it is
    /// invalid per the specification and would not survive a round-trip.
    /// </summary>
    private static Gen<byte> VersionGen =>
        Gen.Choose(0x00, 0xfe).Select(v => (byte)v);

    /// <summary>Trace-flags byte across the full 0x00–0xff range (all values are valid).</summary>
    private static Gen<byte> TraceFlagsGen =>
        Gen.Choose(0x00, 0xff).Select(v => (byte)v);

    /// <summary>Generates a valid <see cref="W3CTraceParent"/> value.</summary>
    private static Gen<W3CTraceParent> ValidTraceParentGen =>
        from version in VersionGen
        from traceId in NonZeroHexStringGen(TraceIdHexLength)
        from parentId in NonZeroHexStringGen(ParentIdHexLength)
        from flags in TraceFlagsGen
        select new W3CTraceParent(version, traceId, parentId, flags);

    private static Arbitrary<W3CTraceParent> ValidTraceParentArb =>
        Arb.From(ValidTraceParentGen);

    [Property(MaxTest = 100)]
    public Property FormatThenParse_YieldsEqualValue()
    {
        return Prop.ForAll(ValidTraceParentArb, tp =>
        {
            var formatted = tp.ToString();

            var parsed = W3CTraceParent.TryParse(formatted, out var roundTripped);

            return (parsed && roundTripped == tp)
                .Label($"traceparent='{formatted}' parsed={parsed} " +
                       $"expected={tp} actual={roundTripped}");
        });
    }
}
