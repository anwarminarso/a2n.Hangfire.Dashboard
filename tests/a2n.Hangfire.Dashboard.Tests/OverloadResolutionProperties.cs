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
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 4: Overload resolution selects the unique matching overload.
//
// For any type exposing several overloads of the same method name, resolution selects exactly the
// overload whose Job_Parameter count and types accept the supplied argument values; when zero
// overloads match (by count or type) or more than one matches, the operation is rejected with a
// "no single matching overload" error (NoMatchingOverload or AmbiguousOverload) and Method is null.
//
// **Validates: Requirements 1.5, 1.6**

/// <summary>
/// Fixture target exposing several overloads of the same method name that differ by Job_Parameter
/// count and types. Some overloads carry Hangfire Injected_Parameters (<see cref="PerformContext"/>,
/// <see cref="CancellationToken"/>) to confirm those are excluded from the Job_Parameter count.
/// Uniquely named (prefixed) to avoid collision with fixtures in other test files in the same wave.
/// Methods are never invoked — only reflected over by the resolver.
/// </summary>
public sealed class OverloadResolutionProps_Target
{
    // --- "Run": a family used for unique-match and no-match scenarios. ---
    // Job_Parameter counts (injected params excluded): 0, 1, 1, 1, 2, 4.
    public void Run() { }
    public void Run(int n) { }
    public void Run(string s) { }
    public void Run(bool b) { }
    public void Run(int n, string s) { }

    // 6 declared parameters, but PerformContext + CancellationToken are injected, so this is the
    // unique 4-Job_Parameter overload (int, string, bool, double). Confirms injected exclusion.
    public void Run(PerformContext ctx, CancellationToken token, int a, string b, bool c, double d) { }

    // --- "Ambig": two overloads that BOTH accept a JSON number at arity 1 -> AmbiguousOverload. ---
    public void Ambig(int n) { }
    public void Ambig(long n) { }
}

/// <summary>
/// A single generated resolution scenario: the method name to resolve, the supplied argument
/// values, and the expected outcome (a unique match with a known Job_Parameter signature, or a
/// rejection with an allowed failure kind).
/// </summary>
public sealed class OverloadResolutionScenario
{
    public string MethodName { get; init; }
    public JsonElement[] Args { get; init; }
    public string Description { get; init; }

    /// <summary>When true, resolution must succeed and select the overload whose Job_Parameter
    /// types equal <see cref="ExpectedJobParamTypes"/>.</summary>
    public bool ExpectUnique { get; init; }
    public Type[] ExpectedJobParamTypes { get; init; }

    /// <summary>When <see cref="ExpectUnique"/> is false, resolution must fail with one of these
    /// kinds and a null <c>Method</c>.</summary>
    public MethodResolutionError[] AllowedFailureKinds { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for overload resolution selecting the unique matching overload (Property 4).
///
/// **Validates: Requirements 1.5, 1.6**
/// </summary>
public class OverloadResolutionProperties
{
    private static readonly string TargetTypeName = typeof(OverloadResolutionProps_Target).FullName;

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string JsonNumber(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static JsonElement[] EmptyArgs => Array.Empty<JsonElement>();

    // --- Scenario generators -------------------------------------------------------------------

    // Arity 0: the unique parameterless Run overload.
    private static Gen<OverloadResolutionScenario> UniqueZeroGen =>
        Gen.Constant(new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = EmptyArgs,
            ExpectUnique = true,
            ExpectedJobParamTypes = Type.EmptyTypes,
            Description = "Run() arity-0 unique match",
        });

    // Arity 1, JSON number -> uniquely the Run(int) overload (string/bool overloads reject a number).
    private static Gen<OverloadResolutionScenario> UniqueNumberGen =>
        from n in Gen.Choose(-100000, 100000)
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = new[] { Json(JsonNumber(n)) },
            ExpectUnique = true,
            ExpectedJobParamTypes = new[] { typeof(int) },
            Description = $"Run(int) selected for number {n}",
        };

    // Arity 1, JSON string -> uniquely the Run(string) overload.
    private static Gen<OverloadResolutionScenario> UniqueStringGen =>
        from s in Arb.Default.String().Generator
        let raw = JsonSerializer.Serialize(s ?? "x")
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = new[] { Json(raw) },
            ExpectUnique = true,
            ExpectedJobParamTypes = new[] { typeof(string) },
            Description = "Run(string) selected for a JSON string",
        };

    // Arity 1, JSON boolean -> uniquely the Run(bool) overload.
    private static Gen<OverloadResolutionScenario> UniqueBoolGen =>
        from b in Arb.Default.Bool().Generator
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = new[] { Json(b ? "true" : "false") },
            ExpectUnique = true,
            ExpectedJobParamTypes = new[] { typeof(bool) },
            Description = $"Run(bool) selected for {b}",
        };

    // Arity 2 -> the unique 2-Job_Parameter overload Run(int, string) (matched by count).
    private static Gen<OverloadResolutionScenario> UniqueArity2Gen =>
        from n in Gen.Choose(-100000, 100000)
        from s in Arb.Default.String().Generator
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = new[] { Json(JsonNumber(n)), Json(JsonSerializer.Serialize(s ?? "x")) },
            ExpectUnique = true,
            ExpectedJobParamTypes = new[] { typeof(int), typeof(string) },
            Description = "Run(int, string) arity-2 unique match",
        };

    // Arity 4 -> the unique 4-Job_Parameter overload, whose 6 declared params include two injected
    // ones. Confirms injected parameters are excluded from the count.
    private static Gen<OverloadResolutionScenario> UniqueArity4InjectedGen =>
        from n in Gen.Choose(-1000, 1000)
        from s in Arb.Default.String().Generator
        from b in Arb.Default.Bool().Generator
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = new[]
            {
                Json(JsonNumber(n)),
                Json(JsonSerializer.Serialize(s ?? "x")),
                Json(b ? "true" : "false"),
                Json("1.5"),
            },
            ExpectUnique = true,
            ExpectedJobParamTypes = new[] { typeof(int), typeof(string), typeof(bool), typeof(double) },
            Description = "Run(ctx, token, int, string, bool, double) arity-4 (injected excluded)",
        };

    // No overload has this Job_Parameter count -> NoMatchingOverload.
    private static Gen<OverloadResolutionScenario> NoMatchByCountGen =>
        from arity in Gen.Elements(3, 5, 6, 7)
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = Enumerable.Range(0, arity).Select(i => Json(JsonNumber(i))).ToArray(),
            ExpectUnique = false,
            AllowedFailureKinds = new[] { MethodResolutionError.NoMatchingOverload },
            Description = $"Run arity-{arity} no overload by count",
        };

    // Arity 1 with a JSON array/object: none of the int/string/bool overloads accept it -> NoMatchingOverload.
    private static Gen<OverloadResolutionScenario> NoMatchByTypeGen =>
        from raw in Gen.Elements("[1,2,3]", "{\"x\":1}")
        select new OverloadResolutionScenario
        {
            MethodName = "Run",
            Args = new[] { Json(raw) },
            ExpectUnique = false,
            AllowedFailureKinds = new[] { MethodResolutionError.NoMatchingOverload },
            Description = $"Run arity-1 no overload by type for {raw}",
        };

    // Ambig(int) and Ambig(long) both accept a JSON number at arity 1 -> AmbiguousOverload.
    private static Gen<OverloadResolutionScenario> AmbiguousGen =>
        from n in Gen.Choose(-100000, 100000)
        select new OverloadResolutionScenario
        {
            MethodName = "Ambig",
            Args = new[] { Json(JsonNumber(n)) },
            ExpectUnique = false,
            AllowedFailureKinds = new[] { MethodResolutionError.AmbiguousOverload },
            Description = $"Ambig(int|long) ambiguous for number {n}",
        };

    private static Arbitrary<OverloadResolutionScenario> ScenarioArb =>
        Arb.From(Gen.OneOf(new[]
        {
            UniqueZeroGen,
            UniqueNumberGen,
            UniqueStringGen,
            UniqueBoolGen,
            UniqueArity2Gen,
            UniqueArity4InjectedGen,
            NoMatchByCountGen,
            NoMatchByTypeGen,
            AmbiguousGen,
        }));

    [Property(MaxTest = 100)]
    public Property ResolveMethod_SelectsUniqueOverload_OrRejectsAmbiguousAndUnmatched()
    {
        var resolver = new JobMethodResolver();

        return Prop.ForAll(ScenarioArb, sc =>
        {
            var result = resolver.ResolveMethod(TargetTypeName, sc.MethodName, sc.Args.Length, sc.Args);

            if (sc.ExpectUnique)
            {
                if (!result.Success)
                    return false.Label($"[{sc.Description}] expected Success but got failure: {result.Error}");
                if (result.Method is null)
                    return false.Label($"[{sc.Description}] Success but Method was null");

                var got = JobParamTypes(result.Method);
                if (!got.SequenceEqual(sc.ExpectedJobParamTypes))
                    return false.Label(
                        $"[{sc.Description}] selected overload Job_Parameter types [{Names(got)}] " +
                        $"!= expected [{Names(sc.ExpectedJobParamTypes)}]");

                return true.ToProperty();
            }

            // Rejection cases (Req 1.6): no single matching overload.
            if (result.Success)
                return false.Label($"[{sc.Description}] expected failure but resolution succeeded");
            if (result.Method is not null)
                return false.Label($"[{sc.Description}] failed result must have null Method");
            if (!result.ErrorKind.HasValue || !sc.AllowedFailureKinds.Contains(result.ErrorKind.Value))
                return false.Label(
                    $"[{sc.Description}] expected one of [{string.Join(",", sc.AllowedFailureKinds)}] " +
                    $"but got {result.ErrorKind?.ToString() ?? "<none>"}");

            return true.ToProperty();
        });
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static Type[] JobParamTypes(MethodInfo method) =>
        method.GetParameters()
            .Where(p => !IsInjected(p.ParameterType))
            .Select(p => p.ParameterType)
            .ToArray();

    private static bool IsInjected(Type t) =>
        t == typeof(PerformContext)
        || t == typeof(CancellationToken)
        || t == typeof(IJobCancellationToken);

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.Name));
}
