using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Server;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 1: Positional argument assignment and injected-parameter exclusion.
//
// For any method with an arbitrary interleaving of Job_Parameters and Injected_Parameters
// (PerformContext / CancellationToken / IJobCancellationToken) and a matching list of argument
// values, the built Args array has length equal to the declared parameter count, holds each
// supplied value at the position of its Job_Parameter, and holds null at every Injected_Parameter
// position.
//
// **Validates: Requirements 1.1, 1.2**

/// <summary>
/// Fixture target whose methods exercise representative interleavings of Job_Parameters and
/// Hangfire Injected_Parameters (<see cref="PerformContext"/>, <see cref="CancellationToken"/>,
/// <see cref="IJobCancellationToken"/>): injected leading, trailing, in the middle, multiple
/// interleaved, all-injected (zero Job_Parameters), and none-injected. Uniquely named (prefixed)
/// to avoid collision with fixtures in other test files. Methods are never invoked — only
/// reflected over by the converter.
/// </summary>
public sealed class BuildArgsPositional_Target
{
    // No injected parameters: Job_Parameters at positions 0,1.
    public void NoInjected(int a, string b) { }

    // Injected leading: PerformContext at 0; Job_Parameters at 1,2.
    public void LeadingInjected(PerformContext ctx, int a, string b) { }

    // Injected trailing: CancellationToken at the end; Job_Parameter at 0.
    public void TrailingInjected(int a, CancellationToken token) { }

    // Injected in the middle: PerformContext at 1; Job_Parameters at 0,2.
    public void MiddleInjected(int a, PerformContext ctx, string b) { }

    // All injected: zero Job_Parameters; both slots must be null.
    public void AllInjected(PerformContext ctx, CancellationToken token) { }

    // Multiple injected, fully interleaved with the three injected kinds.
    // Declared: [IJobCancellationToken, int, CancellationToken, string, PerformContext, bool, double]
    // Injected positions: 0, 2, 4. Job_Parameters: int@1, string@3, bool@5, double@6.
    public void MultiInterleaved(
        IJobCancellationToken t1, int a, CancellationToken t2, string b, PerformContext ctx, bool c, double d) { }

    // Only Job_Parameters, several scalar types in declaration order.
    public void OnlyJob(int a, double b, bool c, string d) { }

    // No parameters at all: empty Args.
    public void NoParams() { }
}

/// <summary>
/// One generated scenario: a target method, the JSON-encoded values supplied for its
/// Job_Parameters (in Job_Parameter order), and the expected CLR values those decode to (used to
/// assert each value lands at its Job_Parameter's declared position).
/// </summary>
public sealed class BuildArgsPositionalScenario
{
    public MethodInfo Method { get; init; }
    public JsonElement[] JobArgJson { get; init; }
    public object[] ExpectedValues { get; init; }

    public override string ToString() =>
        $"{Method.Name}({string.Join(", ", ExpectedValues.Select(v => v ?? "null"))})";
}

/// <summary>
/// Property test for positional argument assignment and injected-parameter exclusion (Property 1).
///
/// **Validates: Requirements 1.1, 1.2**
/// </summary>
public class BuildArgsPositionalProperties
{
    private static readonly MethodInfo[] FixtureMethods = typeof(BuildArgsPositional_Target)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => !m.IsSpecialName)
        .OrderBy(m => m.Name, StringComparer.Ordinal)
        .ToArray();

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // Generates a (JSON, expected-CLR-value) pair appropriate for a supported scalar Job_Parameter
    // type. Values are chosen so they round-trip exactly through JSON and never hit the empty path.
    private static Gen<(JsonElement Json, object Expected)> ValueGen(Type type)
    {
        if (type == typeof(int))
        {
            return from n in Gen.Choose(-1_000_000, 1_000_000)
                   select (Json(n.ToString(CultureInfo.InvariantCulture)), (object)n);
        }

        if (type == typeof(double))
        {
            // n/100.0 yields a finite, exactly round-trippable double.
            return from n in Gen.Choose(-1_000_000, 1_000_000)
                   let d = n / 100.0
                   select (Json(JsonSerializer.Serialize(d)), (object)d);
        }

        if (type == typeof(bool))
        {
            return from b in Arb.Default.Bool().Generator
                   select (Json(b ? "true" : "false"), (object)b);
        }

        if (type == typeof(string))
        {
            // Coalesce null to a non-null string so the value stays on the conversion path
            // (a JSON null would be the empty case, which is a separate property).
            return from s in Arb.Default.String().Generator
                   let value = s ?? "x"
                   select (Json(JsonSerializer.Serialize(value)), (object)value);
        }

        throw new NotSupportedException($"No value generator for {type}.");
    }

    // Sequences value generators over a method's Job_Parameters (in declaration order), producing
    // the aligned JSON inputs and expected CLR values.
    private static Gen<(JsonElement[] Json, object[] Expected)> ArgsForMethod(MethodInfo method)
    {
        var jobParams = method.GetParameters()
            .Where(p => !IsInjected(p.ParameterType))
            .ToArray();

        Gen<List<(JsonElement Json, object Expected)>> acc =
            Gen.Constant(new List<(JsonElement, object)>());

        foreach (var parameter in jobParams)
        {
            var valueGen = ValueGen(parameter.ParameterType);
            var prev = acc;
            acc = prev.SelectMany(list => valueGen.Select(v =>
            {
                var next = new List<(JsonElement, object)>(list) { v };
                return next;
            }));
        }

        return acc.Select(list =>
            (list.Select(x => x.Json).ToArray(), list.Select(x => x.Expected).ToArray()));
    }

    private static Arbitrary<BuildArgsPositionalScenario> ScenarioArb =>
        Arb.From(
            from method in Gen.Elements(FixtureMethods)
            from args in ArgsForMethod(method)
            select new BuildArgsPositionalScenario
            {
                Method = method,
                JobArgJson = args.Json,
                ExpectedValues = args.Expected,
            });

    [Property(MaxTest = 100)]
    public Property BuildArgs_AssignsValuesPositionally_AndNullsInjectedSlots()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            var result = JobArgumentConverter.BuildArgs(sc.Method, sc.JobArgJson);

            if (!result.Success)
            {
                return false.Label($"[{sc}] expected success but got error: {result.Error}");
            }

            var declared = sc.Method.GetParameters();

            // Length equals the declared parameter count (Req 1.1).
            if (result.Args.Length != declared.Length)
            {
                return false
                    .Label($"[{sc}] Args length {result.Args.Length} != declared count {declared.Length}");
            }

            var jobParamIndex = 0;
            for (var i = 0; i < declared.Length; i++)
            {
                if (IsInjected(declared[i].ParameterType))
                {
                    // Injected_Parameter slot must be null (Req 1.2).
                    if (result.Args[i] is not null)
                    {
                        return false.Label(
                            $"[{sc}] injected slot {i} ({declared[i].ParameterType.Name}) " +
                            $"was '{result.Args[i]}', expected null");
                    }

                    continue;
                }

                // Job_Parameter slot must hold the k-th supplied value at this declared position (Req 1.1).
                var expected = sc.ExpectedValues[jobParamIndex];
                jobParamIndex++;

                if (!Equals(result.Args[i], expected))
                {
                    return false.Label(
                        $"[{sc}] Job_Parameter slot {i} was '{result.Args[i]}' " +
                        $"({result.Args[i]?.GetType().Name ?? "null"}), expected '{expected}'");
                }
            }

            return true.ToProperty();
        });
    }

    private static bool IsInjected(Type t) =>
        t == typeof(PerformContext)
        || t == typeof(CancellationToken)
        || t == typeof(IJobCancellationToken);
}
