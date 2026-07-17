#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.OpenTelemetry;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.OpenTelemetry.Tests;

/// <summary>
/// Property test for <see cref="TraceCaptureClientFilter"/>.
///
/// **Property 2: Capture stores a recoverable trace context**
/// **Validates: Requirements 1.1, 1.3**
///
/// For any job created while an ambient <see cref="Activity"/> is active, the
/// <see cref="TraceCaptureClientFilter"/> stores a <c>traceparent</c> job parameter whose parsed
/// trace-id and parent span-id equal those of the ambient activity context (Req 1.1, 1.3). The
/// generator also emits the "no ambient context" case (<see cref="Activity.Current"/> is
/// <see langword="null"/>), where the filter must store nothing and must not throw (Req 1.2 guard).
/// </summary>
public class TraceCaptureProperties
{
    private const string ParameterName = OpenTelemetryIntegrationOptions.DefaultTraceParentParameterName;

    /// <summary>A no-op target so a real <see cref="Job"/> can be constructed for the create context.</summary>
    public static void NoopJob()
    {
    }

    /// <summary>
    /// Builds a real Hangfire <see cref="CreatingContext"/> around a mocked storage/connection. Only
    /// <see cref="CreateContext.Parameters"/> is exercised by <c>SetJobParameter</c>, so the storage
    /// and connection can be inert mocks.
    /// </summary>
    private static CreatingContext NewCreatingContext()
    {
        var storage = new Mock<JobStorage>().Object;
        var connection = new Mock<IStorageConnection>().Object;
        var job = Job.FromExpression(() => NoopJob());
        var initialState = new Mock<IState>().Object;

        var createContext = new CreateContext(storage, connection, job, initialState);
        return new CreatingContext(createContext);
    }

    /// <summary>
    /// The generator emits a flag selecting the ambient/no-ambient case, plus the trace flags to use
    /// when an ambient activity is started (so both sampled and non-sampled flag bytes are exercised).
    /// </summary>
    private static Arbitrary<(bool HasAmbient, bool Recorded)> InputArb =>
        Arb.From(
            from hasAmbient in Gen.Elements(true, false)
            from recorded in Gen.Elements(true, false)
            select (hasAmbient, recorded));

    [Property(MaxTest = 100)]
    public Property Capture_StoresRecoverableTraceContext()
    {
        return Prop.ForAll(InputArb, input =>
        {
            var (hasAmbient, recorded) = input;

            // A dedicated listener per iteration so the ambient ActivitySource is actually sampled
            // and Activity.Current is populated with a real W3C context.
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    recorded ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData,
            };
            ActivitySource.AddActivityListener(listener);

            using var source = new ActivitySource("a2n.Hangfire.Dashboard.Tests." + Guid.NewGuid().ToString("N"));

            // Ensure a clean ambient state; each iteration controls Activity.Current explicitly.
            var previous = Activity.Current;
            Activity.Current = null;

            var filter = new TraceCaptureClientFilter(ParameterName);
            var context = NewCreatingContext();

            try
            {
                if (!hasAmbient)
                {
                    // No ambient Activity: filter must store nothing and must not throw (Req 1.2 guard).
                    filter.OnCreating(context);

                    var storedNothing = !context.Parameters.ContainsKey(ParameterName);
                    return storedNothing.Label("no ambient activity => no traceparent parameter stored");
                }

                using var activity = source.StartActivity("enqueue", ActivityKind.Producer);
                if (activity is null)
                {
                    // Sampling refused to create a recording Activity; skip this iteration as it does
                    // not represent an "ambient activity active" case.
                    return true.ToProperty();
                }

                var expectedTraceId = activity.TraceId.ToHexString();
                var expectedParentId = activity.SpanId.ToHexString();

                filter.OnCreating(context);

                if (!context.Parameters.TryGetValue(ParameterName, out var raw) || raw is not string stored)
                {
                    return false.Label("ambient activity active but no traceparent parameter was stored");
                }

                if (!W3CTraceParent.TryParse(stored, out var parsed))
                {
                    return false.Label($"stored traceparent did not parse: '{stored}'");
                }

                var traceIdMatches = string.Equals(parsed.TraceId, expectedTraceId, StringComparison.Ordinal);
                var parentIdMatches = string.Equals(parsed.ParentId, expectedParentId, StringComparison.Ordinal);

                return (traceIdMatches && parentIdMatches).Label(
                    $"parsed trace context must equal ambient context: " +
                    $"traceId parsed='{parsed.TraceId}' expected='{expectedTraceId}'; " +
                    $"parentId parsed='{parsed.ParentId}' expected='{expectedParentId}'");
            }
            finally
            {
                Activity.Current = previous;
            }
        });
    }
}
