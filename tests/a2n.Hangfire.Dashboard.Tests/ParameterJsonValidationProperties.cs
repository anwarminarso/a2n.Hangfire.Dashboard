using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using FsCheck;
using FsCheck.Xunit;
using Hangfire.Server;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Property 6: Parameter JSON array validation.
//
// For any candidate Parameter_JSON string and selected method, the validator returns:
//   * Malformed        when the string is not well-formed JSON;
//   * NotArray         when it is well-formed but its top-level value is not an array;
//   * CountMismatch    (carrying expected and actual counts) when the array length differs from
//                      the method's Job_Parameter count;
//   * ElementTypeError (naming the parameter and expected type) when an element is not convertible;
//   * Valid            otherwise — including an empty array for a zero-Job_Parameter method.
//
// **Validates: Requirements 2.2, 2.3, 2.4, 2.5, 2.7**

/// <summary>
/// Fixtures with known <c>Job_Parameter</c> shapes that the validator is exercised against.
/// Uniquely named (prefixed) to avoid collision with fixtures in other test files in the same wave.
/// Methods are never invoked — only reflected over by <see cref="JobArgumentConverter"/>.
/// Some overloads carry Hangfire <c>Injected_Parameter</c>s (<see cref="PerformContext"/>,
/// <see cref="CancellationToken"/>) to confirm those are excluded from the Job_Parameter count.
/// </summary>
public sealed class ParameterJsonValidationProps_Fixtures
{
    // 0 Job_Parameters.
    public void Param0() { }

    // 0 Job_Parameters (both declared parameters are injected; excluded from the count) — for 2.7.
    public void Param0Injected(PerformContext ctx, CancellationToken token) { }

    // 1 Job_Parameter, of an integer type (drives ElementTypeError scenarios).
    public void Param1Int(int value) { }

    // 2 Job_Parameters: an integer then a string.
    public void Param2IntString(int count, string label) { }

    // 2 Job_Parameters (count, label) with an injected parameter interleaved — confirms the count is
    // measured over Job_Parameters only.
    public void Param2Injected(int count, PerformContext ctx, string label) { }
}

/// <summary>
/// A single generated validation scenario: the fixture method name to validate against, the
/// candidate Parameter_JSON string, and the expected outcome.
/// </summary>
public sealed class ParameterJsonScenario
{
    public string MethodName { get; init; }
    public string Json { get; init; }
    public ParameterJsonStatus ExpectedStatus { get; init; }
    public string Description { get; init; }

    /// <summary>For <see cref="ParameterJsonStatus.CountMismatch"/>: the expected/actual counts the
    /// validator must report.</summary>
    public int? ExpectedCount { get; init; }
    public int? ActualCount { get; init; }

    /// <summary>For <see cref="ParameterJsonStatus.ElementTypeError"/>: the offending parameter
    /// name the validator must name.</summary>
    public string ExpectedParameterName { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Property test for Parameter JSON array validation (Property 6).
///
/// **Validates: Requirements 2.2, 2.3, 2.4, 2.5, 2.7**
/// </summary>
public class ParameterJsonValidationProperties
{
    private static MethodInfo Method(string name) =>
        typeof(ParameterJsonValidationProps_Fixtures).GetMethod(
            name, BindingFlags.Public | BindingFlags.Instance);

    private static string JsonNumber(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string JsonString(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? string.Empty);

    // The Job_Parameter count of each fixture (injected parameters excluded).
    private static readonly Dictionary<string, int> JobParamCount = new()
    {
        ["Param0"] = 0,
        ["Param0Injected"] = 0,
        ["Param1Int"] = 1,
        ["Param2IntString"] = 2,
        ["Param2Injected"] = 2,
    };

    private static readonly string[] AllMethods = JobParamCount.Keys.ToArray();

    // --- Scenario generators -------------------------------------------------------------------

    // (2.2) Broken / not well-formed JSON -> Malformed, independent of the selected method.
    private static Gen<ParameterJsonScenario> MalformedGen =>
        from method in Gen.Elements(AllMethods)
        from raw in Gen.Elements(
            "",            // empty input
            "   ",         // whitespace only
            "{",           // unterminated object
            "[",           // unterminated array
            "[1, 2",       // unterminated array with elements
            "not json",    // bare word
            "{\"a\":}",    // missing value
            "[1,,2]",      // empty element
            "'single'",    // single quotes are not valid JSON
            "[1 2]")       // missing comma
        select new ParameterJsonScenario
        {
            MethodName = method,
            Json = raw,
            ExpectedStatus = ParameterJsonStatus.Malformed,
            Description = $"Malformed JSON \"{raw}\" against {method}",
        };

    // (2.3) Well-formed JSON whose top-level value is not an array -> NotArray.
    private static Gen<ParameterJsonScenario> NotArrayGen =>
        from method in Gen.Elements(AllMethods)
        from raw in Gen.Elements(
            "{}",
            "{\"x\":1}",
            "5",
            "3.14",
            "\"hello\"",
            "true",
            "false",
            "null")
        select new ParameterJsonScenario
        {
            MethodName = method,
            Json = raw,
            ExpectedStatus = ParameterJsonStatus.NotArray,
            Description = $"Non-array top-level {raw} against {method}",
        };

    // (2.4, 2.5) A well-formed array whose length differs from the method's Job_Parameter count ->
    // CountMismatch carrying both the expected and actual counts.
    private static Gen<ParameterJsonScenario> CountMismatchGen =>
        from method in Gen.Elements(AllMethods)
        let expected = JobParamCount[method]
        // Pick an actual length in [0, 5] that is NOT the expected count.
        from actual in Gen.Choose(0, 5).Where(a => a != expected)
        let elements = Enumerable.Range(0, actual).Select(JsonNumber)
        select new ParameterJsonScenario
        {
            MethodName = method,
            Json = "[" + string.Join(",", elements) + "]",
            ExpectedStatus = ParameterJsonStatus.CountMismatch,
            ExpectedCount = expected,
            ActualCount = actual,
            Description = $"Count mismatch {actual} vs {expected} against {method}",
        };

    // (2.6) A correct-length array with an element that cannot be converted to its integer parameter
    // -> ElementTypeError naming that parameter. (Validated here as part of the same validator; the
    // requirement is annotated on Property 3, but the validator must classify it as ElementTypeError.)
    private static Gen<ParameterJsonScenario> ElementTypeErrorGen
    {
        get
        {
            // Param1Int: a single incompatible element at the int position (name "value").
            var oneInt =
                from bad in Gen.Elements("\"abc\"", "true", "[1,2]", "{\"k\":1}", "1.5")
                select new ParameterJsonScenario
                {
                    MethodName = "Param1Int",
                    Json = $"[{bad}]",
                    ExpectedStatus = ParameterJsonStatus.ElementTypeError,
                    ExpectedParameterName = "value",
                    Description = $"Element type error {bad} for Param1Int.value",
                };

            // Param2IntString / Param2Injected: incompatible element at the int "count" position,
            // a valid string at the label position.
            var twoInt =
                from method in Gen.Elements("Param2IntString", "Param2Injected")
                from bad in Gen.Elements("\"abc\"", "true", "[1,2]", "{\"k\":1}", "1.5")
                from label in Arb.Default.String().Generator
                select new ParameterJsonScenario
                {
                    MethodName = method,
                    Json = $"[{bad},{JsonString(label)}]",
                    ExpectedStatus = ParameterJsonStatus.ElementTypeError,
                    ExpectedParameterName = "count",
                    Description = $"Element type error {bad} for {method}.count",
                };

            return Gen.OneOf(oneInt, twoInt);
        }
    }

    // (2.7 + the Valid branch) Correct-length, fully-convertible arrays -> Valid, including the empty
    // array for a zero-Job_Parameter method (with and without interleaved injected parameters).
    private static Gen<ParameterJsonScenario> ValidGen
    {
        get
        {
            var zero =
                from method in Gen.Elements("Param0", "Param0Injected")
                select new ParameterJsonScenario
                {
                    MethodName = method,
                    Json = "[]",
                    ExpectedStatus = ParameterJsonStatus.Valid,
                    Description = $"Empty array valid for zero-param {method}",
                };

            var oneInt =
                from n in Gen.Choose(-100000, 100000)
                select new ParameterJsonScenario
                {
                    MethodName = "Param1Int",
                    Json = $"[{JsonNumber(n)}]",
                    ExpectedStatus = ParameterJsonStatus.Valid,
                    Description = $"Valid [{n}] for Param1Int",
                };

            // A null element is the empty-value case and is always valid (int -> default(int)).
            var oneIntNull =
                Gen.Constant(new ParameterJsonScenario
                {
                    MethodName = "Param1Int",
                    Json = "[null]",
                    ExpectedStatus = ParameterJsonStatus.Valid,
                    Description = "Valid [null] (empty value) for Param1Int",
                });

            var two =
                from method in Gen.Elements("Param2IntString", "Param2Injected")
                from n in Gen.Choose(-100000, 100000)
                from label in Arb.Default.String().Generator
                select new ParameterJsonScenario
                {
                    MethodName = method,
                    Json = $"[{JsonNumber(n)},{JsonString(label)}]",
                    ExpectedStatus = ParameterJsonStatus.Valid,
                    Description = $"Valid [{n}, <string>] for {method}",
                };

            return Gen.OneOf(zero, oneInt, oneIntNull, two);
        }
    }

    private static Arbitrary<ParameterJsonScenario> ScenarioArb =>
        Arb.From(Gen.OneOf(
            MalformedGen,
            NotArrayGen,
            CountMismatchGen,
            ElementTypeErrorGen,
            ValidGen));

    [Property(MaxTest = 100)]
    public Property ValidateParameterJson_ClassifiesEachBranch()
    {
        return Prop.ForAll(ScenarioArb, sc =>
        {
            var method = Method(sc.MethodName);
            var result = JobArgumentConverter.ValidateParameterJson(sc.Json, method);

            if (result.Status != sc.ExpectedStatus)
            {
                return false.Label(
                    $"[{sc.Description}] expected status {sc.ExpectedStatus} but got {result.Status}");
            }

            switch (sc.ExpectedStatus)
            {
                case ParameterJsonStatus.CountMismatch:
                    if (result.ExpectedCount != sc.ExpectedCount)
                        return false.Label(
                            $"[{sc.Description}] expected ExpectedCount {sc.ExpectedCount} " +
                            $"but got {result.ExpectedCount}");
                    if (result.ActualCount != sc.ActualCount)
                        return false.Label(
                            $"[{sc.Description}] expected ActualCount {sc.ActualCount} " +
                            $"but got {result.ActualCount}");
                    break;

                case ParameterJsonStatus.ElementTypeError:
                    if (result.ParameterName != sc.ExpectedParameterName)
                        return false.Label(
                            $"[{sc.Description}] expected parameter '{sc.ExpectedParameterName}' " +
                            $"but got '{result.ParameterName}'");
                    if (string.IsNullOrEmpty(result.ExpectedType))
                        return false.Label(
                            $"[{sc.Description}] ElementTypeError must name the expected type");
                    break;
            }

            return true.ToProperty();
        });
    }
}
