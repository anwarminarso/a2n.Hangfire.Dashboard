using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 21: Configured-queue persistence.
//
// For any recurring job create or update request, the queue stored on the resulting recurring job
// equals the Configured_Queue supplied in the request — and when the supplied queue is blank, the
// stored queue is the Default_Queue ("default").
//
// **Validates: Requirements 13.5**

/// <summary>
/// Uniquely-named fixture job for the configured-queue persistence property. It carries NO
/// <see cref="QueueAttribute"/> on the method or its declaring class, so the queue that persists on
/// the recurring job is exactly the Configured_Queue supplied by the request (nothing overrides it).
/// The method is parameterless so the Parameter_JSON is the empty array and the property isolates
/// queue persistence from argument handling. The method is never invoked — only reflected over.
/// </summary>
public sealed class ConfiguredQueuePersistenceFixtureJob
{
    public void RunConfiguredQueuePersistenceFixture()
    {
    }
}

/// <summary>
/// A single generated queue scenario: the raw queue value supplied on the request and the queue the
/// stored recurring job is expected to carry as a result (the value itself, or "default" when blank).
/// </summary>
public sealed class ConfiguredQueueScenario
{
    public string SuppliedQueue { get; init; }
    public string ExpectedStoredQueue { get; init; }
    public string Description { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for configured-queue persistence (Property 21). Uses Hangfire.InMemory storage and
/// the real <see cref="HangfireMonitorService"/> with recurring-admin enabled, creates a recurring
/// job through <see cref="HangfireMonitorService.CreateOrUpdateRecurringJob"/>, reads it back via the
/// storage connection's <c>GetRecurringJobs</c>, and asserts the stored queue equals the supplied
/// Configured_Queue (or "default" when blank).
///
/// **Validates: Requirements 13.5**
/// </summary>
public class ConfiguredQueuePersistenceProperties
{
    private static readonly string FixtureTypeName = typeof(ConfiguredQueuePersistenceFixtureJob).FullName;
    private const string FixtureMethodName = nameof(ConfiguredQueuePersistenceFixtureJob.RunConfiguredQueuePersistenceFixture);

    // --- Queue name generators -----------------------------------------------------------------

    // Valid Hangfire queue names: lowercase letters/digits/hyphen/underscore, starting with a
    // lowercase letter so the generated name is always a realistic, non-empty queue identifier.
    private static Gen<string> ValidQueueNameGen =>
        from first in Gen.Elements("abcdefghijklmnopqrstuvwxyz".ToCharArray())
        from rest in Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray())
            .ListOf().Select(cs => cs.Take(15).ToArray())
        select first + new string(rest);

    // A configured (non-blank) queue scenario: the stored queue must equal the supplied name.
    private static Gen<ConfiguredQueueScenario> ConfiguredQueueGen =>
        from name in ValidQueueNameGen
        select new ConfiguredQueueScenario
        {
            SuppliedQueue = name,
            ExpectedStoredQueue = name,
            Description = $"configured queue '{name}' persists verbatim",
        };

    // A blank queue scenario (null / empty / whitespace): the stored queue must be "default".
    private static Gen<ConfiguredQueueScenario> BlankQueueGen =>
        from blank in Gen.Elements<string>(null, "", "   ")
        select new ConfiguredQueueScenario
        {
            SuppliedQueue = blank,
            ExpectedStoredQueue = "default",
            Description = $"blank queue ({Describe(blank)}) persists as 'default'",
        };

    private static Arbitrary<ConfiguredQueueScenario> ScenarioArb =>
        Arb.From(Gen.OneOf(new[] { ConfiguredQueueGen, BlankQueueGen }));

    [Property(MaxTest = 100)]
    public Property CreateOrUpdateRecurringJob_StoresConfiguredQueue_OrDefaultWhenBlank()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            // Fresh storage per case so each recurring job is read back in isolation.
            var storage = new InMemoryStorage();
            var options = new DashboardUIOptions
            {
                IsReadOnly = false,
                EnableRecurringJobAdmin = true,
            };
            var service = new HangfireMonitorService(storage, audit: null, options: options, resolver: new JobMethodResolver());

            const string jobId = "configured-queue-persistence-fixture";
            var request = new RecurringJobRequest(
                JobId: jobId,
                TypeName: FixtureTypeName,
                MethodName: FixtureMethodName,
                ParameterJson: "[]",
                Cron: "* * * * *",
                Queue: sc.SuppliedQueue,
                TimeZoneId: null,
                IsCustomMethod: false);

            var result = service.CreateOrUpdateRecurringJob(request);
            if (!result.Success)
                return false.Label($"[{sc.Description}] create failed: {result.Error}");

            var stored = ReadStoredQueue(storage, jobId);
            if (stored is null)
                return false.Label($"[{sc.Description}] recurring job '{jobId}' was not found in storage");

            // Hangfire may omit the hash entry for the default queue; absence is equivalent to
            // "default" by Hangfire's own convention, so normalize before comparing.
            var effective = string.IsNullOrEmpty(stored.Queue) ? "default" : stored.Queue;

            return (effective == sc.ExpectedStoredQueue)
                .Label($"[{sc.Description}] stored queue '{effective}' != expected '{sc.ExpectedStoredQueue}'");
        });
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static RecurringJobDto ReadStoredQueue(JobStorage storage, string jobId)
    {
        using var connection = storage.GetReadOnlyConnection();
        if (connection is JobStorageConnection storageConnection)
        {
            return storageConnection.GetRecurringJobs().FirstOrDefault(j => j.Id == jobId);
        }
        return null;
    }

    private static string Describe(string s) =>
        s is null ? "null" : s.Length == 0 ? "empty" : "whitespace";
}
