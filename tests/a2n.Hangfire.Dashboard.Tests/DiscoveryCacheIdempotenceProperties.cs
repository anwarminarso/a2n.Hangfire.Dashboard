using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 10: Discovery cache idempotence.
//
// WHEN the Method_Resolver receives the first discovery request after startup, THE Method_Resolver
// SHALL scan the loaded assemblies and store the discovered methods in a cache (Req 5.6). WHILE a
// populated cache exists, THE Method_Resolver SHALL serve discovery requests from the cache without
// performing a new scan (Req 5.8).

/// <summary>
/// Property test for discovery cache idempotence (Property 10).
///
/// Discovery scans the whole loaded-assembly set, so we do NOT assert exact contents. Instead we
/// assert the cache semantics implied by Req 5.6 and 5.8:
///   * The first call to <see cref="JobMethodResolver.GetRegisteredMethods"/> populates a non-null
///     cache (Req 5.6).
///   * Every subsequent call on the SAME resolver instance returns the very same cached list
///     instance (reference-equal) — proving the result is served from the cache without a rescan
///     (Req 5.8).
///   * Two independent resolver instances each return their own stable, non-null result.
///
/// **Validates: Requirements 5.6, 5.8**
/// </summary>
public class DiscoveryCacheIdempotenceProperties
{
    /// <summary>Generates the number of repeated discovery calls to make (2..20).</summary>
    private static Arbitrary<int> CallCountArb =>
        Arb.From(Gen.Choose(2, 20));

    [Property(MaxTest = 100)]
    public Property RepeatedCalls_OnSameInstance_ReturnSameCachedList()
    {
        return Prop.ForAll(CallCountArb, callCount =>
        {
            var resolver = new JobMethodResolver();

            // Req 5.6: the first call populates a non-null cache.
            var first = resolver.GetRegisteredMethods();
            if (first is null)
            {
                return false.Label("First call returned null; cache was not populated (Req 5.6).");
            }

            // Req 5.8: every subsequent call serves the SAME cached list instance (reference-equal),
            // proving no rescan occurs and the result is idempotent across N calls.
            for (var i = 1; i < callCount; i++)
            {
                var next = resolver.GetRegisteredMethods();

                if (next is null)
                {
                    return false.Label($"Call {i} returned null (Req 5.8).");
                }

                if (!ReferenceEquals(first, next))
                {
                    return false.Label(
                        $"Call {i} returned a different list instance than the first call; " +
                        "the cache is not being reused (Req 5.8).");
                }
            }

            return true.ToProperty();
        });
    }

    [Property(MaxTest = 100)]
    public Property SeparateInstances_EachReturnStableNonNullResult()
    {
        return Prop.ForAll(CallCountArb, callCount =>
        {
            var resolverA = new JobMethodResolver();
            var resolverB = new JobMethodResolver();

            var a1 = resolverA.GetRegisteredMethods();
            var b1 = resolverB.GetRegisteredMethods();

            if (a1 is null || b1 is null)
            {
                return false.Label("A resolver instance produced a null discovery result (Req 5.6).");
            }

            // Each instance is independently stable across repeated calls (Req 5.8).
            for (var i = 1; i < callCount; i++)
            {
                if (!ReferenceEquals(a1, resolverA.GetRegisteredMethods()))
                {
                    return false.Label($"Resolver A call {i} was not reference-equal to its first call (Req 5.8).");
                }

                if (!ReferenceEquals(b1, resolverB.GetRegisteredMethods()))
                {
                    return false.Label($"Resolver B call {i} was not reference-equal to its first call (Req 5.8).");
                }
            }

            return true.ToProperty();
        });
    }
}
