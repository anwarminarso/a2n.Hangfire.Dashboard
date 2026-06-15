using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 22: Enqueue parity with recurring argument and queue handling.
//
// For any method and argument set, an enqueue request and a recurring request built from the same
// inputs produce the same Args array (same Injected_Parameter exclusion, type conversion, and
// overload resolution) and resolve the same Effective_Queue under the queue-attribute rules. This
// holds because EnqueueJob and CreateOrUpdateRecurringJob share the same JobMethodResolver +
// JobArgumentConverter pipeline.
//
// This property exercises that parity end-to-end against real Hangfire.InMemory storage:
//   * Argument parity (12.4): the stored Job.Args of the enqueued job (read via the monitoring
//     API's JobDetails) equals the stored Job.Args of the equivalent recurring job (read via
//     GetRecurringJobs) when both are built from identical TypeName/MethodName/Parameter_JSON.
//   * Queue handling parity (13.7): for a fixture WITHOUT a QueueAttribute, the Effective_Queue for
//     enqueue follows the same precedence rule as recurring — the Configured_Queue persists (or the
//     Default_Queue "default" when blank) — so the enqueued job lands on that queue.
//
// **Validates: Requirements 12.3, 12.4, 13.7**

/// <summary>
/// Uniquely-named public fixture whose methods are valid Job_Parameter targets resolvable by the
/// <see cref="JobMethodResolver"/> against the loaded test assembly. The fixture carries NO
/// <see cref="QueueAttribute"/> on the methods or the declaring class, so the Effective_Queue is
/// determined solely by the Configured_Queue supplied on each request (nothing overrides it). The
/// methods are never invoked — the resolver only reflects over them and the converter shapes Args.
/// </summary>
public sealed class EnqueueParityFixtureJob
{
    /// <summary>Two typed Job_Parameters, no injected parameters.</summary>
    public void RunTyped(string label, int count) { }

    /// <summary>
    /// Same two operator-supplied Job_Parameters plus a trailing Injected_Parameter
    /// (<see cref="PerformContext"/>) that must be excluded from Argument_Values and assigned a null
    /// Args slot identically for enqueue and recurring.
    /// </summary>
    public void RunTypedInjected(string label, int count, PerformContext context) { }
}

/// <summary>
/// A single generated parity scenario: the target method, the operator-supplied argument values
/// shaped into Parameter_JSON, and the queue selected on both requests with the queue the job is
/// expected to land on (the value itself, or "default" when blank).
/// </summary>
public sealed class EnqueueParityScenario
{
    public string MethodName { get; init; }
    public string ParameterJson { get; init; }
    public string SuppliedQueue { get; init; }
    public string ExpectedQueue { get; init; }
    public string Description { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for enqueue/recurring parity (Property 22). Uses Hangfire.InMemory storage and the
/// real <see cref="HangfireMonitorService"/> wired with a <see cref="JobMethodResolver"/> and
/// <see cref="DashboardUIOptions"/> (IsReadOnly = false, EnableJobManagement = true). For each
/// generated scenario it enqueues a one-off job and creates an equivalent recurring job from the
/// same inputs, then asserts the two share an identical Args array and that the enqueued job lands
/// on the same Effective_Queue the recurring job would persist.
///
/// **Validates: Requirements 12.3, 12.4, 13.7**
/// </summary>
public class EnqueueParityProperties
{
    private static readonly string FixtureTypeName = typeof(EnqueueParityFixtureJob).FullName;
    private const string TypedMethodName = nameof(EnqueueParityFixtureJob.RunTyped);
    private const string TypedInjectedMethodName = nameof(EnqueueParityFixtureJob.RunTypedInjected);

    // --- Generators ----------------------------------------------------------------------------

    // Operator-supplied label values: arbitrary strings (including null) so the converter's string
    // handling is exercised; System.Text.Json safely encodes any value into the Parameter_JSON.
    private static Gen<string> LabelGen =>
        Gen.OneOf(
            Gen.Constant<string>(null),
            Arb.Default.String().Generator.Where(s => s is not null));

    // Valid Hangfire queue names: lowercase letters/digits/hyphen/underscore, starting with a
    // lowercase letter so the generated name is always a realistic, non-empty queue identifier.
    private static Gen<string> ValidQueueNameGen =>
        from first in Gen.Elements("abcdefghijklmnopqrstuvwxyz".ToCharArray())
        from rest in Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray())
            .ListOf().Select(cs => cs.Take(15).ToArray())
        select first + new string(rest);

    // Either a configured (non-blank) queue that must persist verbatim, or a blank queue that must
    // resolve to the Default_Queue "default".
    private static Gen<(string supplied, string expected)> QueueGen =>
        Gen.OneOf(
            ValidQueueNameGen.Select(q => (q, q)),
            Gen.Elements<string>(null, "", "   ").Select(b => (b, "default")));

    private static Arbitrary<EnqueueParityScenario> ScenarioArb =>
        Arb.From(
            from methodName in Gen.Elements(TypedMethodName, TypedInjectedMethodName)
            from label in LabelGen
            from count in Arb.Default.Int32().Generator
            from queue in QueueGen
            let json = JsonSerializer.Serialize(new object[] { label, count })
            select new EnqueueParityScenario
            {
                MethodName = methodName,
                ParameterJson = json,
                SuppliedQueue = queue.supplied,
                ExpectedQueue = queue.expected,
                Description =
                    $"method '{methodName}', json {json}, queue ({DescribeQueue(queue.supplied)}) -> '{queue.expected}'",
            });

    // --- Property ------------------------------------------------------------------------------

    [Property(MaxTest = 100)]
    public Property Enqueue_And_Recurring_Share_Args_And_EffectiveQueue()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            // Fresh storage per case so each enqueued/recurring pair is observed in isolation.
            var storage = new InMemoryStorage();
            var options = new DashboardUIOptions
            {
                IsReadOnly = false,
                EnableJobManagement = true,
            };
            var service = new HangfireMonitorService(
                storage, audit: null, options: options, resolver: new JobMethodResolver());

            // --- Enqueue a one-off job from the generated inputs -------------------------------
            var enqueueRequest = new EnqueueJobRequest(
                TypeName: FixtureTypeName,
                MethodName: sc.MethodName,
                ParameterJson: sc.ParameterJson,
                Queue: sc.SuppliedQueue,
                IsCustomMethod: false);

            var enqueueResult = service.EnqueueJob(enqueueRequest);
            if (!enqueueResult.Success)
                return false.Label($"[{sc.Description}] enqueue failed: {enqueueResult.Error}");

            // --- Create the equivalent recurring job from the SAME inputs ----------------------
            const string recurringId = "enqueue-parity-fixture";
            var recurringRequest = new RecurringJobRequest(
                JobId: recurringId,
                TypeName: FixtureTypeName,
                MethodName: sc.MethodName,
                ParameterJson: sc.ParameterJson,
                Cron: "* * * * *",
                Queue: sc.SuppliedQueue,
                TimeZoneId: null,
                IsCustomMethod: false);

            var recurringResult = service.CreateOrUpdateRecurringJob(recurringRequest);
            if (!recurringResult.Success)
                return false.Label($"[{sc.Description}] recurring create failed: {recurringResult.Error}");

            // --- Argument parity (12.4): enqueued Args == recurring Args -----------------------
            var enqueuedArgs = ReadEnqueuedArgs(storage, enqueueResult.JobId);
            if (enqueuedArgs is null)
                return false.Label($"[{sc.Description}] enqueued job details/Args were not found");

            var recurringArgs = ReadRecurringArgs(service, recurringId);
            if (recurringArgs is null)
                return false.Label($"[{sc.Description}] recurring job '{recurringId}' or its Args were not found");

            var argsParity = enqueuedArgs.SequenceEqual(recurringArgs)
                .Label(
                    $"[{sc.Description}] Args mismatch: enqueued [{Render(enqueuedArgs)}] != recurring [{Render(recurringArgs)}]");

            // --- Queue parity (13.7): enqueued job lands on the same Effective_Queue -----------
            var landedOnExpectedQueue = EnqueuedJobsContains(storage, sc.ExpectedQueue, enqueueResult.JobId)
                .Label(
                    $"[{sc.Description}] enqueued job '{enqueueResult.JobId}' did not land on Effective_Queue '{sc.ExpectedQueue}'");

            return argsParity.And(landedOnExpectedQueue);
        });
    }

    // --- Helpers -------------------------------------------------------------------------------

    /// <summary>Reads the stored Args of the enqueued job through the monitoring API.</summary>
    private static IReadOnlyList<object> ReadEnqueuedArgs(JobStorage storage, string jobId)
    {
        var details = storage.GetMonitoringApi().JobDetails(jobId);
        return details?.Job?.Args?.ToList();
    }

    /// <summary>Reads the stored Args of the recurring job via the service's GetRecurringJobs.</summary>
    private static IReadOnlyList<object> ReadRecurringArgs(HangfireMonitorService service, string jobId)
    {
        var recurring = service.GetRecurringJobs().FirstOrDefault(j => j.Id == jobId);
        return recurring?.Job?.Args?.ToList();
    }

    /// <summary>True when the enqueued job list for <paramref name="queue"/> contains the id.</summary>
    private static bool EnqueuedJobsContains(JobStorage storage, string queue, string jobId)
    {
        var jobs = storage.GetMonitoringApi().EnqueuedJobs(queue, 0, 100);
        return jobs.Any(j => j.Key == jobId);
    }

    private static string Render(IEnumerable<object> args) =>
        string.Join(", ", args.Select(a => a is null ? "null" : $"{a} ({a.GetType().Name})"));

    private static string DescribeQueue(string s) =>
        s is null ? "null" : s.Length == 0 ? "empty" : string.IsNullOrWhiteSpace(s) ? "whitespace" : s;
}
