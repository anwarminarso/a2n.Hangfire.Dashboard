using System.Diagnostics;
using a2n.Hangfire.Dashboard.Services;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Task 7.8 — smoke test for custom-method validation latency.
//
// Requirement 7.1 states that when the Operator requests validation of a Custom_Method, the
// Method_Resolver SHALL evaluate the ordered checks "within 2 seconds". This is a single
// representative xUnit [Fact] (NOT a property test): it primes discovery, then measures one
// ValidateCustomMethod call against a uniquely-named local fixture type exposing a public method
// and asserts the call completes well within the 2-second bound.
//
// _Requirements: 7.1_

/// <summary>
/// Uniquely-named fixture exposing a single public method to be resolved by the latency smoke test.
/// Named to avoid collision with fixtures in other test files. The method is never invoked — it is
/// only reflected over by <see cref="JobMethodResolver.ValidateCustomMethod"/>.
/// </summary>
public sealed class CustomMethodValidationLatencyFixture_Target
{
    public void RunLatencyProbe(int count, string label) { }
}

public class CustomMethodValidationLatencyTests
{
    /// <summary>The Requirement 7.1 bound: validation must complete within 2 seconds.</summary>
    private const long TwoSecondBoundMs = 2000;

    [Fact]
    public void ValidateCustomMethod_CompletesWithinTwoSecondBound()
    {
        var resolver = new JobMethodResolver();

        // Prime discovery so the measured call reflects steady-state validation cost rather than
        // any first-call assembly-scan warmup.
        _ = resolver.GetRegisteredMethods();

        var typeName = typeof(CustomMethodValidationLatencyFixture_Target).FullName!;
        const string methodName = nameof(CustomMethodValidationLatencyFixture_Target.RunLatencyProbe);

        var stopwatch = Stopwatch.StartNew();
        var result = resolver.ValidateCustomMethod(typeName, methodName);
        stopwatch.Stop();

        // The representative method is a valid public match, confirming we measured a real
        // resolution rather than an early-exit failure path.
        Assert.True(result.IsValid, $"Expected the representative method to validate; got: {result.Message}");

        Assert.True(
            stopwatch.ElapsedMilliseconds < TwoSecondBoundMs,
            $"Custom-method validation took {stopwatch.ElapsedMilliseconds} ms, exceeding the {TwoSecondBoundMs} ms (Req 7.1) bound.");
    }
}
