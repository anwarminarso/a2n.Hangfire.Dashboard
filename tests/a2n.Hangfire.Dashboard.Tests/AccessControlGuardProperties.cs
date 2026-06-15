using System;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 8: Access-control mutation guards.
//
// For any create, update, or enqueue request:
//   * While Read_Only_Mode is active the request is rejected with a read-only error and no stored
//     job state changes (Req 4.6).
//   * While Recurring_Admin_Enabled is inactive any recurring create/update request is rejected
//     with a recurring-admin-disabled error and no state changes (Req 4.7). Enqueue is NOT subject
//     to the recurring-admin gate.
//   * While Custom_Method_Enabled is inactive any request specifying a Custom_Method is rejected
//     with a custom-method-disabled error and no state changes (Req 4.8); this applies to both the
//     recurring and the enqueue paths.
//
// The property generates over every combination of the three option flags, the operation
// (recurring vs enqueue), and whether the request specifies a custom method. It predicts the first
// applicable gate (the service evaluates them in order: read-only → recurring-admin → custom) and
// asserts the rejection happens EXACTLY under the gating conditions: when no gate applies the
// otherwise-valid request against a uniquely-named resolvable fixture method succeeds, and when a
// gate applies the request is rejected with the matching error and no recurring/enqueued state
// change.
//
// **Validates: Requirements 4.6, 4.7, 4.8**

/// <summary>
/// Uniquely-named fixture exposing a single resolvable parameterless job method. It is the
/// "would otherwise succeed" baseline target: when no access gate applies, the service must be
/// able to resolve and persist a job built from this method. The method is never invoked — only
/// reflected over by <see cref="JobMethodResolver"/>.
/// </summary>
public sealed class AccessControlGuard_FixtureJob
{
    public void RunGuardedFixtureJob() { }
}

/// <summary>The mutation under test.</summary>
public enum AccessControlOperation { Recurring, Enqueue }

/// <summary>
/// A single generated access-control scenario: the three option flags, the operation, and whether
/// the request specifies a Custom_Method.
/// </summary>
public sealed class AccessControlScenario
{
    public bool IsReadOnly { get; init; }
    public bool EnableJobManagement { get; init; }
    public bool AllowArbitraryMethodInvocation { get; init; }
    public AccessControlOperation Operation { get; init; }
    public bool IsCustomMethod { get; init; }

    public override string ToString() =>
        $"{Operation} custom={IsCustomMethod} | readOnly={IsReadOnly} " +
        $"jobManagement={EnableJobManagement} arbitraryEnabled={AllowArbitraryMethodInvocation}";
}

/// <summary>
/// Property test for the access-control mutation guards (Property 8).
///
/// **Validates: Requirements 4.6, 4.7, 4.8**
/// </summary>
public class AccessControlGuardProperties
{
    private static readonly string FixtureTypeName = typeof(AccessControlGuard_FixtureJob).FullName;
    private const string FixtureMethodName = nameof(AccessControlGuard_FixtureJob.RunGuardedFixtureJob);

    // Error-message fragments emitted by HangfireMonitorService for each gate.
    private const string ReadOnlyFragment = "read-only";
    private const string RecurringAdminFragment = "Job management is disabled";
    private const string CustomDisabledFragment = "Arbitrary method invocation is disabled";

    private static Arbitrary<AccessControlScenario> ScenarioArb =>
        Arb.From(
            from readOnly in Arb.Default.Bool().Generator
            from recurringAdmin in Arb.Default.Bool().Generator
            from customEnabled in Arb.Default.Bool().Generator
            from op in Gen.Elements(AccessControlOperation.Recurring, AccessControlOperation.Enqueue)
            from isCustom in Arb.Default.Bool().Generator
            select new AccessControlScenario
            {
                IsReadOnly = readOnly,
                EnableJobManagement = recurringAdmin,
                AllowArbitraryMethodInvocation = customEnabled,
                Operation = op,
                IsCustomMethod = isCustom,
            });

    [Property(MaxTest = 100)]
    public Property MutationGuards_RejectExactlyUnderGatingConditions()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            // Fresh storage per scenario so "no state change" is asserted against a clean baseline
            // and successful baselines never collide across generated cases.
            JobStorage storage = new InMemoryStorage();
            var options = new DashboardUIOptions
            {
                IsReadOnly = sc.IsReadOnly,
                EnableJobManagement = sc.EnableJobManagement,
                AllowArbitraryMethodInvocation = sc.AllowArbitraryMethodInvocation,
            };
            var service = new HangfireMonitorService(storage, audit: null, options: options, resolver: new JobMethodResolver());

            // Predict the first applicable gate. The service evaluates gates in this order:
            //   read-only → recurring-admin (recurring only) → custom-method (both paths).
            var expectedFragment = ExpectedRejectionFragment(sc);

            var recurringBefore = RecurringCount(storage);
            var enqueuedBefore = EnqueuedCount(storage, "default");

            JobOperationResult result = sc.Operation == AccessControlOperation.Recurring
                ? service.CreateOrUpdateRecurringJob(BuildRecurringRequest(sc))
                : service.EnqueueJob(BuildEnqueueRequest(sc));

            var recurringAfter = RecurringCount(storage);
            var enqueuedAfter = EnqueuedCount(storage, "default");

            if (expectedFragment is not null)
            {
                // Gated: must be rejected with the matching error and zero state change (Req 4.6–4.8).
                if (result.Success)
                    return false.Label($"[{sc}] expected rejection but the operation succeeded");

                if (string.IsNullOrEmpty(result.Error) ||
                    result.Error.IndexOf(expectedFragment, StringComparison.OrdinalIgnoreCase) < 0)
                    return false.Label($"[{sc}] expected error containing '{expectedFragment}' but got '{result.Error}'");

                if (recurringAfter != recurringBefore)
                    return false.Label($"[{sc}] rejected mutation changed recurring count {recurringBefore}->{recurringAfter}");

                if (enqueuedAfter != enqueuedBefore)
                    return false.Label($"[{sc}] rejected mutation changed enqueued count {enqueuedBefore}->{enqueuedAfter}");

                return true.ToProperty();
            }

            // No gate applies: the otherwise-valid request must succeed (no over-rejection).
            if (!result.Success)
                return false.Label($"[{sc}] expected success but was rejected: '{result.Error}'");

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Replicates the service's ordered gate evaluation to predict which rejection (if any) the
    /// request must produce. Returns the expected error fragment, or <c>null</c> when no gate
    /// applies and the request must succeed.
    /// </summary>
    private static string ExpectedRejectionFragment(AccessControlScenario sc)
    {
        if (sc.IsReadOnly)
            return ReadOnlyFragment;

        if (sc.Operation == AccessControlOperation.Recurring && !sc.EnableJobManagement)
            return RecurringAdminFragment;

        if (sc.IsCustomMethod && !sc.AllowArbitraryMethodInvocation)
            return CustomDisabledFragment;

        return null;
    }

    private static RecurringJobRequest BuildRecurringRequest(AccessControlScenario sc) =>
        new RecurringJobRequest(
            JobId: $"acg-{Guid.NewGuid():N}",
            TypeName: FixtureTypeName,
            MethodName: FixtureMethodName,
            ParameterJson: "[]",
            Cron: "* * * * *",
            Queue: "default",
            TimeZoneId: null,
            IsCustomMethod: sc.IsCustomMethod);

    private static EnqueueJobRequest BuildEnqueueRequest(AccessControlScenario sc) =>
        new EnqueueJobRequest(
            TypeName: FixtureTypeName,
            MethodName: FixtureMethodName,
            ParameterJson: "[]",
            Queue: "default",
            IsCustomMethod: sc.IsCustomMethod);

    private static int RecurringCount(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        return connection.GetRecurringJobs().Count;
    }

    private static long EnqueuedCount(JobStorage storage, string queue) =>
        storage.GetMonitoringApi().EnqueuedCount(queue);
}
