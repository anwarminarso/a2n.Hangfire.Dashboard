using System;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the preset trace-link builders
/// (<see cref="TraceLinkBuilders"/>).
///
/// Feature: integrations-v2-6, Property 5: Trace-link templates embed the trace-id
///
/// **Property 5: Trace-link templates embed the trace-id** — for any valid trace-id, each preset
/// builder (Tempo, Jaeger, Honeycomb, and the generic Template builder) produces a non-null target
/// URL that contains that trace-id.
///
/// Trace-ids are 32 lowercase hex characters. Because hex characters are unreserved in URLs,
/// <see cref="Uri.EscapeDataString"/> is the identity on them, so the raw trace-id appears verbatim
/// in URLs even for builders that URL-encode (Tempo, Jaeger, Honeycomb).
///
/// **Validates: Requirements 3.4**
/// </summary>
public class TraceLinkBuilderProperties
{
    private const int TraceIdHexLength = 32;

    private static readonly char[] LowercaseHexDigits =
        "0123456789abcdef".ToCharArray();

    /// <summary>A single lowercase hex character (0-9, a-f).</summary>
    private static Gen<char> LowercaseHexCharGen =>
        Gen.Elements(LowercaseHexDigits);

    /// <summary>
    /// A valid 32-char lowercase hex trace-id that is NOT all zeros (an all-zero trace-id is
    /// invalid per the W3C grammar).
    /// </summary>
    private static Gen<string> ValidTraceIdGen =>
        Gen.ArrayOf(TraceIdHexLength, LowercaseHexCharGen)
            .Select(chars => new string(chars))
            .Where(s => s.Any(c => c != '0'));

    private static Arbitrary<string> ValidTraceIdArb =>
        Arb.From(ValidTraceIdGen);

    /// <summary>
    /// Builds a <see cref="TraceLinkContext"/> around the given trace-id. The traceparent and
    /// parent-span-id are not exercised by the preset builders, so representative values suffice.
    /// </summary>
    private static TraceLinkContext ContextFor(string traceId) =>
        new(
            Traceparent: $"00-{traceId}-0000000000000001-01",
            TraceId: traceId,
            ParentSpanId: "0000000000000001");

    [Property(MaxTest = 100)]
    public Property Tempo_UrlContainsTraceId()
    {
        var build = TraceLinkBuilders.Tempo("https://grafana.example.com");
        return Prop.ForAll(ValidTraceIdArb, traceId =>
        {
            var url = build(ContextFor(traceId));
            return (url is not null && url.Contains(traceId, StringComparison.Ordinal))
                .Label($"traceId='{traceId}' url='{url}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property Jaeger_UrlContainsTraceId()
    {
        var build = TraceLinkBuilders.Jaeger("https://jaeger.example.com/");
        return Prop.ForAll(ValidTraceIdArb, traceId =>
        {
            var url = build(ContextFor(traceId));
            return (url is not null && url.Contains(traceId, StringComparison.Ordinal))
                .Label($"traceId='{traceId}' url='{url}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property Honeycomb_UrlContainsTraceId()
    {
        var build = TraceLinkBuilders.Honeycomb("my-dataset", "my-team");
        return Prop.ForAll(ValidTraceIdArb, traceId =>
        {
            var url = build(ContextFor(traceId));
            return (url is not null && url.Contains(traceId, StringComparison.Ordinal))
                .Label($"traceId='{traceId}' url='{url}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property Template_UrlContainsTraceId()
    {
        var build = TraceLinkBuilders.Template("https://traces.example.com/view?id={traceId}");
        return Prop.ForAll(ValidTraceIdArb, traceId =>
        {
            var url = build(ContextFor(traceId));
            return (url is not null && url.Contains(traceId, StringComparison.Ordinal))
                .Label($"traceId='{traceId}' url='{url}'");
        });
    }
}
