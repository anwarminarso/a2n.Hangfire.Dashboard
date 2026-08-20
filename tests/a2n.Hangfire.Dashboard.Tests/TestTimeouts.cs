namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Shared timeouts for bUnit render waits.
/// </summary>
/// <remarks>
/// A render-wait timeout is not an assertion about how fast a component renders — it only exists so
/// a genuinely stuck render fails instead of hanging forever. Making it generous therefore costs
/// nothing on the passing path (the wait returns as soon as the predicate holds) and only delays the
/// report of a real failure.
///
/// <para>
/// The previous per-call value of 5 seconds was tight enough to fail on a loaded machine: the suite
/// runs three target frameworks concurrently, and <c>StatCards_AlwaysRenderExpectedLabels</c> would
/// intermittently report <c>WaitForFailedException</c> under that contention while passing in well
/// under a second when run on its own. Intermittent red teaches everyone to ignore red, which is
/// worse than a slow test.
/// </para>
/// </remarks>
internal static class TestTimeouts
{
    /// <summary>
    /// How long to wait for a component to reach an expected render state before failing.
    /// </summary>
    public static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(30);
}
