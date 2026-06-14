using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Server;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

// Feature: job-builder, Property 12: Custom-method validation ordering and resolution.
//
// For any type-name/method-name input, custom-method validation evaluates the checks
// type-exists -> method-exists -> public-and-unambiguous IN ORDER, stops at the first failed
// check, and returns a SINGLE result that is either "valid" (FailedCheck = None) or the identity
// of the first failed check (TypeNotFound, MethodNotFound, NotPublic for a non-public method,
// Ambiguous when more than one public method shares the name). When valid, it provides a
// Job_Parameter list containing exactly one entry per declared parameter EXCLUDING
// Injected_Parameters, in declared order (Req 7.6, 6.8) — the same list a changed selection
// hands to the Parameter_Builder.
//
// **Validates: Requirements 7.1, 7.4, 7.5, 7.6, 6.8**

// --- Fixtures -------------------------------------------------------------------------------
//
// Uniquely "Cmv12_"-prefixed fixture types so the resolver's assembly-wide scan cannot collide
// with sibling test fixtures or production types. Methods are only reflected over, never invoked.
namespace CustomMethodValidationFixtures
{
    /// <summary>
    /// A valid, public, unambiguous method whose declared parameters interleave two
    /// Injected_Parameters (<see cref="PerformContext"/>, <see cref="CancellationToken"/>) with
    /// three Job_Parameters. The descriptor's Job_Parameter list must therefore be exactly
    /// [int a, string b, bool c] in declared order, excluding the injected ones (Req 7.6, 6.8).
    /// </summary>
    public class Cmv12_ValidTarget
    {
        public void ValidMethod(int a, PerformContext ctx, string b, CancellationToken token, bool c)
        {
        }
    }

    /// <summary>
    /// A type whose only methods of a given name are non-public (private / internal / protected).
    /// The named method is FOUND (so the check advances past method-exists) but is not public, so
    /// the validation result must be <see cref="CustomMethodCheck.NotPublic"/> (Req 7.4).
    /// </summary>
    public class Cmv12_NonPublicTarget
    {
#pragma warning disable IDE0051, CA1822 // members exist solely to be reflected over
        private void PrivateMethod(int x)
        {
        }

        internal void InternalMethod(int x)
        {
        }

        protected void ProtectedMethod(int x)
        {
        }
#pragma warning restore IDE0051, CA1822
    }

    /// <summary>
    /// Two public overloads of the same name → the validation result must be
    /// <see cref="CustomMethodCheck.Ambiguous"/> (Req 7.5).
    /// </summary>
    public class Cmv12_AmbiguousTarget
    {
        public void Overloaded(int x)
        {
        }

        public void Overloaded(string s)
        {
        }
    }

    /// <summary>
    /// An existing type exposing exactly one real public method, used to assert that requesting a
    /// non-existent method on a real type short-circuits to
    /// <see cref="CustomMethodCheck.MethodNotFound"/> (ordering: type-exists passed, method-exists
    /// failed).
    /// </summary>
    public class Cmv12_ExistingTarget
    {
        public void RealMethod()
        {
        }
    }
}

// --- Property test --------------------------------------------------------------------------

namespace a2n.Hangfire.Dashboard.Tests
{
    /// <summary>
    /// Property test for custom-method validation ordering and resolution (Property 12).
    ///
    /// **Validates: Requirements 7.1, 7.4, 7.5, 7.6, 6.8**
    /// </summary>
    public class CustomMethodValidationProperties
    {
        private static readonly string ValidTypeName =
            typeof(CustomMethodValidationFixtures.Cmv12_ValidTarget).FullName;

        private static readonly string NonPublicTypeName =
            typeof(CustomMethodValidationFixtures.Cmv12_NonPublicTarget).FullName;

        private static readonly string AmbiguousTypeName =
            typeof(CustomMethodValidationFixtures.Cmv12_AmbiguousTarget).FullName;

        private static readonly string ExistingTypeName =
            typeof(CustomMethodValidationFixtures.Cmv12_ExistingTarget).FullName;

        /// <summary>
        /// One generated validation scenario: the type/method input, the expected validity, the
        /// first failed check the ordered evaluation should stop at, and (for the valid case) the
        /// exact ordered Job_Parameter names the descriptor must carry.
        /// </summary>
        private sealed class Scenario
        {
            public string TypeName { get; init; }
            public string MethodName { get; init; }
            public bool ExpectValid { get; init; }
            public CustomMethodCheck ExpectedCheck { get; init; }
            public string[] ExpectedJobParamNames { get; init; }
            public string Description { get; init; }

            public override string ToString() => Description;
        }

        // Valid: one public unambiguous method; descriptor excludes the two injected parameters and
        // lists [a, b, c] in declared order (Req 7.6, 6.8).
        private static Gen<Scenario> ValidGen =>
            Gen.Constant(new Scenario
            {
                TypeName = ValidTypeName,
                MethodName = "ValidMethod",
                ExpectValid = true,
                ExpectedCheck = CustomMethodCheck.None,
                ExpectedJobParamNames = new[] { "a", "b", "c" },
                Description = "valid: Cmv12_ValidTarget.ValidMethod (injected excluded, declared order)",
            });

        // NotPublic: the named method exists only at private/internal/protected access (Req 7.4).
        private static Gen<Scenario> NotPublicGen =>
            from name in Gen.Elements("PrivateMethod", "InternalMethod", "ProtectedMethod")
            select new Scenario
            {
                TypeName = NonPublicTypeName,
                MethodName = name,
                ExpectValid = false,
                ExpectedCheck = CustomMethodCheck.NotPublic,
                Description = $"not-public: Cmv12_NonPublicTarget.{name}",
            };

        // Ambiguous: two public overloads share the name (Req 7.5).
        private static Gen<Scenario> AmbiguousGen =>
            Gen.Constant(new Scenario
            {
                TypeName = AmbiguousTypeName,
                MethodName = "Overloaded",
                ExpectValid = false,
                ExpectedCheck = CustomMethodCheck.Ambiguous,
                Description = "ambiguous: Cmv12_AmbiguousTarget.Overloaded (two public overloads)",
            });

        // MethodNotFound (ordering): type exists, method does not -> stops at method-exists.
        private static Gen<Scenario> MethodNotFoundGen =>
            from suffix in Gen.Choose(0, 100000)
            select new Scenario
            {
                TypeName = ExistingTypeName,
                MethodName = $"NoSuchMethod_{suffix}",
                ExpectValid = false,
                ExpectedCheck = CustomMethodCheck.MethodNotFound,
                Description = $"method-not-found: Cmv12_ExistingTarget.NoSuchMethod_{suffix}",
            };

        // TypeNotFound (ordering short-circuit): the type does not exist, so evaluation stops at the
        // FIRST check and never reaches the method-exists check, regardless of the method name.
        private static Gen<Scenario> TypeNotFoundGen =>
            from suffix in Gen.Choose(0, 100000)
            // Use a method name that would also be missing, to prove short-circuit ordering:
            // the result must be TypeNotFound, not MethodNotFound.
            select new Scenario
            {
                TypeName = $"a2n.Hangfire.Dashboard.Tests.NoSuchType_{suffix}",
                MethodName = "AnyMethod",
                ExpectValid = false,
                ExpectedCheck = CustomMethodCheck.TypeNotFound,
                Description = $"type-not-found: NoSuchType_{suffix}.AnyMethod (short-circuit before method check)",
            };

        private static Arbitrary<Scenario> ScenarioArb =>
            Arb.From(Gen.OneOf(new[]
            {
                ValidGen,
                NotPublicGen,
                AmbiguousGen,
                MethodNotFoundGen,
                TypeNotFoundGen,
            }));

        [Property(MaxTest = 100)]
        public Property ValidateCustomMethod_StopsAtFirstFailedCheck_AndResolvesValidDescriptor()
        {
            var resolver = new JobMethodResolver();

            return Prop.ForAll(ScenarioArb, sc =>
            {
                var result = resolver.ValidateCustomMethod(sc.TypeName, sc.MethodName);

                // A single result is always returned. (Req 7.1)
                if (result is null)
                    return false.Label($"[{sc.Description}] returned a null result");

                if (sc.ExpectValid)
                {
                    if (!result.IsValid)
                        return false.Label(
                            $"[{sc.Description}] expected IsValid but failed with {result.FailedCheck}: {result.Message}");

                    if (result.FailedCheck != CustomMethodCheck.None)
                        return false.Label(
                            $"[{sc.Description}] valid result must carry FailedCheck=None but had {result.FailedCheck}");

                    if (result.Descriptor is null)
                        return false.Label($"[{sc.Description}] valid result must carry a Descriptor");

                    // Req 7.6 / 6.8: one entry per declared parameter EXCLUDING injected, declared order.
                    var actualNames = result.Descriptor.JobParameters.Select(p => p.Name).ToArray();
                    if (!actualNames.SequenceEqual(sc.ExpectedJobParamNames))
                        return false.Label(
                            $"[{sc.Description}] Job_Parameter names [{string.Join(",", actualNames)}] " +
                            $"!= expected [{string.Join(",", sc.ExpectedJobParamNames)}] (Req 7.6, 6.8)");

                    // Declared order is reflected by strictly ascending declared Position.
                    var positions = result.Descriptor.JobParameters.Select(p => p.Position).ToArray();
                    var ascending = positions
                        .Zip(positions.Skip(1), (a, b) => a < b)
                        .All(ok => ok);
                    if (!ascending)
                        return false.Label(
                            $"[{sc.Description}] Job_Parameters not in declared order; positions=[{string.Join(",", positions)}]");

                    // None of the included parameters may be an Injected_Parameter.
                    if (result.Descriptor.JobParameters.Any(p => IsInjected(p.DeclaredType)))
                        return false.Label($"[{sc.Description}] Job_Parameters must exclude injected parameters");

                    return true.ToProperty();
                }

                // Failure cases: stop at the expected first failed check, no descriptor (Req 7.1, 7.4, 7.5).
                if (result.IsValid)
                    return false.Label($"[{sc.Description}] expected failure {sc.ExpectedCheck} but validation passed");

                if (result.FailedCheck != sc.ExpectedCheck)
                    return false.Label(
                        $"[{sc.Description}] expected first failed check {sc.ExpectedCheck} but got {result.FailedCheck}");

                if (result.Descriptor is not null)
                    return false.Label($"[{sc.Description}] failed result must not carry a Descriptor");

                return true.ToProperty();
            });
        }

        private static bool IsInjected(Type t) =>
            t == typeof(PerformContext)
            || t == typeof(CancellationToken)
            || t == typeof(IJobCancellationToken);
    }
}
