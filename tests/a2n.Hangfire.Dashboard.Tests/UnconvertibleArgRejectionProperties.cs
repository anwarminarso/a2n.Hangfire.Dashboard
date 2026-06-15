using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Hangfire.InMemory;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 3: Unconvertible argument rejection preserves state.
//
// For any argument value that cannot be converted to its Job_Parameter's declared type — whether
// supplied through the service layer (CreateOrUpdateRecurringJob) or through Parameter_JSON — the
// operation is rejected (Success == false), any EXISTING stored recurring job is left unchanged,
// and the returned error identifies the offending parameter name and its expected type
// (FailedField/ParameterName + ExpectedType).
//
// **Validates: Requirements 1.4, 2.6**

/// <summary>
/// Fixture job target with a single overload whose only Job_Parameter (<c>count</c>) is a typed
/// <see cref="int"/>. A single overload guarantees the resolver matches by count and hands a
/// non-convertible value to <see cref="JobArgumentConverter.BuildArgs"/>, exercising the
/// conversion-failure path (Req 1.4) rather than overload rejection. Uniquely named to avoid
/// collision with fixtures in other test files. The method is never invoked — only reflected over.
/// </summary>
public sealed class UnconvertibleArgRejectionProps_Fixture
{
    public void ScheduleWithCount(int count) { }
}

/// <summary>
/// A single generated rejection scenario: a unique job-id seed, a valid baseline argument and cron
/// used to first store the job, and an unconvertible Parameter_JSON element used to attempt an
/// update of the same id.
/// </summary>
public sealed class UnconvertibleArgScenario
{
    public int IdSeed { get; init; }
    public int BaselineCount { get; init; }
    public string BaselineCron { get; init; }

    /// <summary>A raw JSON element that cannot be converted to <see cref="int"/>.</summary>
    public string UnconvertibleJson { get; init; }
    public string Description { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for unconvertible argument rejection preserving state (Property 3).
///
/// **Validates: Requirements 1.4, 2.6**
/// </summary>
public class UnconvertibleArgRejectionProperties
{
    private static readonly string FixtureTypeName =
        typeof(UnconvertibleArgRejectionProps_Fixture).FullName;

    private const string MethodName = "ScheduleWithCount";
    private const string ParamName = "count";
    private const string ExpectedType = "Int32";

    private static MethodInfo FixtureMethod =>
        typeof(UnconvertibleArgRejectionProps_Fixture).GetMethod(
            MethodName, BindingFlags.Public | BindingFlags.Instance);

    // Valid Hangfire 5-field cron expressions used for the baseline definition.
    private static readonly string[] ValidCrons =
    [
        "* * * * *",
        "0 * * * *",
        "0 0 * * *",
        "*/5 * * * *",
        "0 12 * * 1",
    ];

    // Raw JSON elements that cannot be converted to int: non-numeric strings, fractional numbers,
    // booleans, and structured values. Each drives a conversion failure in BuildArgs (Req 1.4).
    private static readonly string[] UnconvertibleInts =
    [
        "\"abc\"",
        "\"hello world\"",
        "\"3.14\"",
        "1.5",
        "true",
        "false",
        "[1,2,3]",
        "{\"x\":1}",
    ];

    private static Arbitrary<UnconvertibleArgScenario> ScenarioArb =>
        Arb.From(
            from idSeed in Gen.Choose(1, 1_000_000)
            from count in Gen.Choose(-100_000, 100_000)
            from cron in Gen.Elements(ValidCrons)
            from bad in Gen.Elements(UnconvertibleInts)
            select new UnconvertibleArgScenario
            {
                IdSeed = idSeed,
                BaselineCount = count,
                BaselineCron = cron,
                UnconvertibleJson = bad,
                Description = $"baseline [{count}] cron '{cron}', unconvertible {bad}",
            });

    [Property(MaxTest = 100)]
    public Property UnconvertibleArgument_IsRejected_AndLeavesExistingJobUnchanged()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            // Fresh storage + service per case so each scenario is fully isolated.
            var storage = new InMemoryStorage();
            var options = new DashboardUIOptions
            {
                IsReadOnly = false,
                EnableJobManagement = true,
            };
            var service = new HangfireMonitorService(
                storage, audit: null, options: options, resolver: new JobMethodResolver());

            var jobId = $"unconv-arg-{sc.IdSeed}";

            // 1) Establish baseline: create a VALID recurring job with this id.
            var createResult = service.CreateOrUpdateRecurringJob(new RecurringJobRequest(
                JobId: jobId,
                TypeName: FixtureTypeName,
                MethodName: MethodName,
                ParameterJson: $"[{sc.BaselineCount.ToString(CultureInfo.InvariantCulture)}]",
                Cron: sc.BaselineCron,
                Queue: "default",
                TimeZoneId: null,
                IsCustomMethod: false));

            if (!createResult.Success)
                return false.Label($"[{sc.Description}] baseline create failed: {createResult.Error}");

            var baseline = Snapshot(service, jobId);
            if (baseline is null)
                return false.Label($"[{sc.Description}] baseline job was not stored");

            // 2) Attempt to UPDATE the same id with an unconvertible argument. The cron and queue
            //    are deliberately changed so any partial write would be detectable.
            var updateResult = service.CreateOrUpdateRecurringJob(new RecurringJobRequest(
                JobId: jobId,
                TypeName: FixtureTypeName,
                MethodName: MethodName,
                ParameterJson: $"[{sc.UnconvertibleJson}]",
                Cron: "0 0 1 * *",
                Queue: "critical",
                TimeZoneId: null,
                IsCustomMethod: false));

            // a) The operation is rejected (Req 1.4).
            if (updateResult.Success)
                return false.Label($"[{sc.Description}] unconvertible update unexpectedly succeeded");

            // b) The error identifies the parameter name and the expected type (Req 1.4).
            if (updateResult.FailedField != ParamName)
                return false.Label(
                    $"[{sc.Description}] FailedField '{updateResult.FailedField}' != '{ParamName}'");
            if (string.IsNullOrEmpty(updateResult.Error)
                || !updateResult.Error.Contains(ParamName, StringComparison.Ordinal)
                || !updateResult.Error.Contains(ExpectedType, StringComparison.Ordinal))
                return false.Label(
                    $"[{sc.Description}] error '{updateResult.Error}' must name parameter '{ParamName}' " +
                    $"and expected type '{ExpectedType}'");

            // c) The existing stored job is left unchanged (cron + args identical to baseline).
            var after = Snapshot(service, jobId);
            if (after is null)
                return false.Label($"[{sc.Description}] job disappeared after rejected update");
            if (after.Value.Cron != baseline.Value.Cron)
                return false.Label(
                    $"[{sc.Description}] cron changed from '{baseline.Value.Cron}' to '{after.Value.Cron}'");
            if (after.Value.ArgsJson != baseline.Value.ArgsJson)
                return false.Label(
                    $"[{sc.Description}] args changed from '{baseline.Value.ArgsJson}' to '{after.Value.ArgsJson}'");

            // d) The Parameter_JSON path through the converter classifies the same failure as an
            //    ElementTypeError naming the parameter and expected type (Req 2.6).
            var jsonValidation = JobArgumentConverter.ValidateParameterJson(
                $"[{sc.UnconvertibleJson}]", FixtureMethod);
            if (jsonValidation.Status != ParameterJsonStatus.ElementTypeError)
                return false.Label(
                    $"[{sc.Description}] expected ElementTypeError but got {jsonValidation.Status}");
            if (jsonValidation.ParameterName != ParamName)
                return false.Label(
                    $"[{sc.Description}] converter named '{jsonValidation.ParameterName}' != '{ParamName}'");
            if (jsonValidation.ExpectedType != ExpectedType)
                return false.Label(
                    $"[{sc.Description}] converter expected type '{jsonValidation.ExpectedType}' != '{ExpectedType}'");

            return true.ToProperty();
        });
    }

    /// <summary>
    /// Captures the stored recurring job's definition that must be preserved across a rejected
    /// update: its cron expression and the serialized positional <c>Args</c>.
    /// </summary>
    private static (string Cron, string ArgsJson)? Snapshot(HangfireMonitorService service, string jobId)
    {
        var dto = service.GetRecurringJobs().FirstOrDefault(j => j.Id == jobId);
        if (dto is null)
            return null;

        var argsJson = dto.Job is null
            ? "<null-job>"
            : JsonSerializer.Serialize(dto.Job.Args);

        return (dto.Cron, argsJson);
    }
}
