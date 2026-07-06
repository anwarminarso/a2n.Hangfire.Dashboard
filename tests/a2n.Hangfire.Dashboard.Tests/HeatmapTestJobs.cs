namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// A non-test helper holding the no-op job target used by the heatmap page/service tests when
/// registering recurring jobs in an <c>InMemoryStorage</c>. Kept out of the test classes so the
/// public method is not flagged by the xUnit analyzer (xUnit1013) as a missing <c>[Fact]</c>, and so
/// Hangfire's <c>Job.FromExpression</c> can resolve a public, invocable method.
/// </summary>
public static class HeatmapTestJobs
{
    /// <summary>A public, parameterless no-op so <c>Job.FromExpression(() =&gt; HeatmapTestJobs.NoOp())</c> resolves.</summary>
    public static void NoOp()
    {
    }
}
