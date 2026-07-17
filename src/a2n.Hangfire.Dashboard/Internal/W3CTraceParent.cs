#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;

namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Pure, dependency-free codec for the W3C Trace Context <c>traceparent</c> header value.
/// </summary>
/// <remarks>
/// <para>
/// The <c>traceparent</c> grammar (per the W3C Trace Context specification) is:
/// <code>traceparent = version "-" trace-id "-" parent-id "-" trace-flags</code>
/// where:
/// </para>
/// <list type="bullet">
///   <item><description><c>version</c> — 1 byte, 2 lowercase hex digits. The reserved value <c>ff</c> is invalid.</description></item>
///   <item><description><c>trace-id</c> — 16 bytes, 32 lowercase hex digits; MUST NOT be all zeros.</description></item>
///   <item><description><c>parent-id</c> (a.k.a. parent span-id) — 8 bytes, 16 lowercase hex digits; MUST NOT be all zeros.</description></item>
///   <item><description><c>trace-flags</c> — 1 byte, 2 lowercase hex digits.</description></item>
/// </list>
/// <para>
/// This helper keeps <see cref="TraceId"/> and <see cref="ParentId"/> as their canonical lowercase
/// hex strings so the trace-id and parent span-id remain recoverable (Req 1.3). It is shared by the
/// Job Details page (to extract the trace-id for the trace-link builder) and mirrors the wire format
/// used by the OpenTelemetry integration package so both sides agree on encoding.
/// </para>
/// </remarks>
public readonly record struct W3CTraceParent(byte Version, string TraceId, string ParentId, byte TraceFlags)
{
    private const int VersionHexLength = 2;
    private const int TraceIdHexLength = 32;
    private const int ParentIdHexLength = 16;
    private const int TraceFlagsHexLength = 2;

    // version "-" trace-id "-" parent-id "-" trace-flags => 2 + 1 + 32 + 1 + 16 + 1 + 2 = 55.
    private const int ExpectedLength =
        VersionHexLength + 1 + TraceIdHexLength + 1 + ParentIdHexLength + 1 + TraceFlagsHexLength;

    private const string ZeroTraceId = "00000000000000000000000000000000";
    private const string ZeroParentId = "0000000000000000";

    /// <summary>
    /// Attempts to parse a W3C <c>traceparent</c> string into a <see cref="W3CTraceParent"/>.
    /// </summary>
    /// <param name="value">The raw <c>traceparent</c> header value. May be <see langword="null"/>.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, contains the parsed value with canonical
    /// lowercase hex <see cref="TraceId"/> and <see cref="ParentId"/>. Otherwise contains the default.
    /// </param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a valid <c>traceparent</c>; otherwise <see langword="false"/>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out W3CTraceParent result)
    {
        result = default;

        if (value is null || value.Length != ExpectedLength)
        {
            return false;
        }

        // Field separators must be at the fixed positions dictated by the grammar.
        if (value[VersionHexLength] != '-' ||
            value[VersionHexLength + 1 + TraceIdHexLength] != '-' ||
            value[VersionHexLength + 1 + TraceIdHexLength + 1 + ParentIdHexLength] != '-')
        {
            return false;
        }

        var versionSpan = value.AsSpan(0, VersionHexLength);
        var traceIdSpan = value.AsSpan(VersionHexLength + 1, TraceIdHexLength);
        var parentIdSpan = value.AsSpan(VersionHexLength + 1 + TraceIdHexLength + 1, ParentIdHexLength);
        var flagsSpan = value.AsSpan(VersionHexLength + 1 + TraceIdHexLength + 1 + ParentIdHexLength + 1, TraceFlagsHexLength);

        if (!IsLowercaseHex(versionSpan) ||
            !IsLowercaseHex(traceIdSpan) ||
            !IsLowercaseHex(parentIdSpan) ||
            !IsLowercaseHex(flagsSpan))
        {
            return false;
        }

        var version = (byte)((HexValue(versionSpan[0]) << 4) | HexValue(versionSpan[1]));

        // Version 0xff is reserved/invalid per the specification.
        if (version == 0xff)
        {
            return false;
        }

        var traceId = traceIdSpan.ToString();
        var parentId = parentIdSpan.ToString();

        // All-zero trace-id or parent-id is invalid.
        if (traceId == ZeroTraceId || parentId == ZeroParentId)
        {
            return false;
        }

        var traceFlags = (byte)((HexValue(flagsSpan[0]) << 4) | HexValue(flagsSpan[1]));

        result = new W3CTraceParent(version, traceId, parentId, traceFlags);
        return true;
    }

    /// <summary>
    /// Formats this value as a canonical W3C <c>traceparent</c> string
    /// (<c>version "-" trace-id "-" parent-id "-" trace-flags</c>) using lowercase hex.
    /// </summary>
    public override string ToString() =>
        string.Create(ExpectedLength, this, static (span, tp) =>
        {
            WriteHexByte(span, 0, tp.Version);
            span[2] = '-';
            tp.TraceId.AsSpan().CopyTo(span[3..]);
            span[35] = '-';
            tp.ParentId.AsSpan().CopyTo(span[36..]);
            span[52] = '-';
            WriteHexByte(span, 53, tp.TraceFlags);
        });

    private static void WriteHexByte(Span<char> span, int offset, byte value)
    {
        span[offset] = ToLowerHexDigit(value >> 4);
        span[offset + 1] = ToLowerHexDigit(value & 0xf);
    }

    private static char ToLowerHexDigit(int nibble) =>
        (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));

    private static bool IsLowercaseHex(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            var isDigit = c is >= '0' and <= '9';
            var isLowerHexLetter = c is >= 'a' and <= 'f';
            if (!isDigit && !isLowerHexLetter)
            {
                return false;
            }
        }

        return true;
    }

    private static int HexValue(char c) =>
        c <= '9' ? c - '0' : c - 'a' + 10;
}
