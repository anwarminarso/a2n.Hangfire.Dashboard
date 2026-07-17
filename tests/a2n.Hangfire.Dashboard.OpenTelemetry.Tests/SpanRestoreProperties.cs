#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.OpenTelemetry;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.OpenTelemetry.Tests;

/// <summary>
/// Property test for <see cref="SpanRestorerServerFilter"/>.
///
/// **Property 3: Restore links to parent and records terminal outcome**
/// **Validates: Requirements 2.1, 2.2**
///
/// For any job carrying a stored <c>traceparent</c> and any terminal outcome (success or failure),
/// the <see cref="SpanRestorerServerFilter"/> starts an execution span and, on completion, records
/// the terminal outcome as the span status:
/// <list type="bullet">
///   <item><description>valid traceparent — the started span's trace-id and parent span-id equal the
///     stored trace context (Req 2.1);</description></item>
///   <item><description>absent traceparent — a parentless span is started, without error (Req 2.3);</description></item>
///   <item><description>malformed traceparent — a parentless span is started, without error (Req 2.4);</description></item>
///   <item><description>on completion — status is <see cref="ActivityStatusCode.Ok"/> when no exception
///     occurred and <see cref="ActivityStatusCode.Error"/> when the job faulted (Req 2.2).</description></item>
/// </list>
///
/// The started span is captured through an <see cref="ActivityListener"/> registered against the
/// integration's <see cref="ActivitySource"/> by name, which also forces the source to be sampled so
/// <c>StartActivity</c> returns a recording <see cref="Activity"/>.
/// </summary>
public class SpanRestoreProperties
{
    private const string ParameterName = OpenTelemetryIntegrationOptions.DefaultTraceParentParameterName;
    private const string ZeroSpanId = "0000000000000000";

    private enum TraceParentKind
    {
        Valid,
        Absent,
        Malformed,
    }

    private sealed record Input(TraceParentKind Kind, string ValidTraceParent, string Malformed, bool Faulted);

    /// <summary>A no-op target so a real <see cref="Job"/> can be constructed for the perform context.</summary>
    public static void NoopJob()
    {
    }

    // ── Generators ────────────────────────────────────────────────────────

    /// <summary>Generates a lowercase-hex word (4 hex digits from a 16-bit value).</summary>
    private static Gen<string> Word4 =>
        Gen.Choose(0, 0xFFFF).Select(i => i.ToString("x4"));

    private static string EnsureNonZero(string hex) =>
        hex.All(c => c == '0') ? "1" + hex.Substring(1) : hex;

    /// <summary>Generates a valid 32-hex-digit, non-all-zero trace-id (eight 4-hex words).</summary>
    private static Gen<string> TraceIdGen =>
        from a in Word4
        from b in Word4
        from c in Word4
        from d in Word4
        from e in Word4
        from f in Word4
        from g in Word4
        from h in Word4
        select EnsureNonZero(a + b + c + d + e + f + g + h);

    /// <summary>Generates a valid 16-hex-digit, non-all-zero parent span-id (four 4-hex words).</summary>
    private static Gen<string> ParentIdGen =>
        from a in Word4
        from b in Word4
        from c in Word4
        from d in Word4
        select EnsureNonZero(a + b + c + d);

    /// <summary>Generates a canonical, valid W3C <c>traceparent</c> string.</summary>
    private static Gen<string> ValidTraceParentGen =>
        from traceId in TraceIdGen
        from parentId in ParentIdGen
        from flags in Gen.Choose(0, 255)
        select new W3CTraceParent(0, traceId, parentId, (byte)flags).ToString();

    /// <summary>
    /// Generates non-null strings that are NOT valid <c>traceparent</c> values, so the filter must
    /// fall back to a parentless span (Req 2.4).
    /// </summary>
    private static Gen<string> MalformedGen =>
        Gen.Elements(
            "",
            "not-a-traceparent",
            "00",
            "00-",
            "abcdef",
            // Correct shape but reserved version 0xff (invalid).
            "ff-00000000000000000000000000000001-0000000000000001-00",
            // Correct shape but all-zero trace-id (invalid).
            "00-00000000000000000000000000000000-0000000000000001-00",
            // Correct shape but all-zero parent-id (invalid).
            "00-00000000000000000000000000000001-0000000000000000-00",
            // Uppercase hex (grammar requires lowercase).
            "00-0000000000000000000000000000000A-000000000000000B-01");

    private static Arbitrary<Input> InputArb =>
        Arb.From(
            from kind in Gen.Elements(TraceParentKind.Valid, TraceParentKind.Absent, TraceParentKind.Malformed)
            from validTp in ValidTraceParentGen
            from malformed in MalformedGen
            from faulted in Gen.Elements(true, false)
            select new Input(kind, validTp, malformed, faulted));

    // ── Context construction ──────────────────────────────────────────────

    private static PerformingContext NewPerformingContext(IStorageConnection connection)
    {
        var job = Job.FromExpression(() => NoopJob());
        var backgroundJob = new BackgroundJob("job-" + Guid.NewGuid().ToString("N"), job, DateTime.UtcNow);
        var cancellationToken = new Mock<IJobCancellationToken>().Object;

        var performContext = new PerformContext((JobStorage?)null, connection, backgroundJob, cancellationToken);
        return new PerformingContext(performContext);
    }

    // ── Property ──────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property Restore_LinksParent_AndRecordsOutcome()
    {
        return Prop.ForAll(InputArb, input =>
        {
            var (kind, validTp, malformed, faulted) = input;

            // The raw traceparent value the job "carries", as it would be deserialized by
            // PerformContext.GetJobParameter<string>. Absent => the connection returns null.
            var raw = kind switch
            {
                TraceParentKind.Valid => validTp,
                TraceParentKind.Malformed => malformed,
                _ => (string?)null,
            };

            // GetJobParameter<string> deserializes the connection's stored value with the User option,
            // so the mock must return the serialized form (or null when absent).
            var storedSerialized = raw is null
                ? null
                : SerializationHelper.Serialize(raw, SerializationOption.User);

            var connectionMock = new Mock<IStorageConnection>();
            connectionMock
                .Setup(c => c.GetJobParameter(It.IsAny<string>(), ParameterName))
                .Returns(() => storedSerialized!);

            // Capture the span the filter starts, and force the source to be sampled so StartActivity
            // returns a recording Activity.
            Activity? started = null;
            using var listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == OpenTelemetryDashboardExtensions.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = a => started = a,
            };
            ActivitySource.AddActivityListener(listener);

            // Guarantee parentless spans are truly rootless (no ambient Activity.Current to adopt).
            var previous = Activity.Current;
            Activity.Current = null;

            try
            {
                var filter = new SpanRestorerServerFilter(ParameterName);
                var performing = NewPerformingContext(connectionMock.Object);

                filter.OnPerforming(performing);

                var exception = faulted ? new InvalidOperationException("boom") : (Exception?)null;
                var performed = new PerformedContext(performing, result: null, canceled: false, exception: exception);
                filter.OnPerformed(performed);

                if (started is null)
                {
                    return false.Label($"[{kind}] expected an execution span to be started, but none was");
                }

                // Terminal outcome recorded as span status (Req 2.2).
                var expectedStatus = faulted ? ActivityStatusCode.Error : ActivityStatusCode.Ok;
                var statusOk = started.Status == expectedStatus;
                var statusLabel = $"[{kind}] span status expected {expectedStatus} but was {started.Status}";

                if (kind == TraceParentKind.Valid)
                {
                    // Parent link equals the stored trace context (Req 2.1).
                    var parsedOk = W3CTraceParent.TryParse(validTp, out var parsed);
                    var traceIdMatches = string.Equals(started.TraceId.ToHexString(), parsed.TraceId, StringComparison.Ordinal);
                    var parentIdMatches = string.Equals(started.ParentSpanId.ToHexString(), parsed.ParentId, StringComparison.Ordinal);

                    return (parsedOk && traceIdMatches && parentIdMatches).Label(
                               $"[Valid] parent link must equal stored context: " +
                               $"traceId span='{started.TraceId.ToHexString()}' expected='{parsed.TraceId}'; " +
                               $"parentId span='{started.ParentSpanId.ToHexString()}' expected='{parsed.ParentId}'")
                           .And(statusOk.Label(statusLabel));
                }

                // Absent or malformed => parentless span (Req 2.3, 2.4).
                var parentless = started.ParentSpanId.ToHexString() == ZeroSpanId;
                return parentless.Label(
                           $"[{kind}] expected a parentless span but ParentSpanId was '{started.ParentSpanId.ToHexString()}'")
                       .And(statusOk.Label(statusLabel));
            }
            finally
            {
                Activity.Current = previous;
            }
        });
    }
}
