using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

// Feature: job-builder, Property 5: Missing type or method is rejected with an identifying error.
//
// For any requested type name absent from the loaded assemblies, resolution fails with a
// type-not-found error containing that type name verbatim; for any method name absent from a
// resolved type, resolution fails with a method-not-found error containing that method name
// verbatim. In both cases stored state is unchanged (no storage is touched on these paths). This
// holds for BOTH the service create/update path (JobMethodResolver.ResolveMethod) and the
// custom-method validation path (JobMethodResolver.ValidateCustomMethod).
//
// **Validates: Requirements 1.7, 1.8, 7.2, 7.3**

namespace MissingTypeOrMethodFixtures
{
    /// <summary>
    /// Uniquely-named "present type" base for Property 5. Its single, unambiguous public method
    /// <see cref="KnownMethod"/> serves as the resolvable anchor: the type is guaranteed to exist
    /// in the loaded assemblies, so a missing-method scenario can be exercised against a real type.
    /// The prefix (<c>Mtm5_</c>) avoids collision with fixtures in sibling test files. The method is
    /// never invoked — only reflected over by the resolver.
    /// </summary>
    public sealed class Mtm5_PresentType
    {
        public void KnownMethod()
        {
        }
    }
}

namespace a2n.Hangfire.Dashboard.Tests
{
    /// <summary>
    /// Property test for missing type or method rejection (Property 5).
    ///
    /// Covers both resolution surfaces:
    /// <list type="bullet">
    /// <item>Service create/update path — <see cref="JobMethodResolver.ResolveMethod"/>.</item>
    /// <item>Custom-method validation path — <see cref="JobMethodResolver.ValidateCustomMethod"/>.</item>
    /// </list>
    ///
    /// **Validates: Requirements 1.7, 1.8, 7.2, 7.3**
    /// </summary>
    public class MissingTypeOrMethodProperties
    {
        private static readonly string PresentTypeName =
            typeof(MissingTypeOrMethodFixtures.Mtm5_PresentType).FullName;

        private static readonly string KnownMethodName = "KnownMethod";

        /// <summary>
        /// A type name guaranteed not to exist in the loaded assemblies. Built from a random GUID so
        /// no real type — production or test fixture — can collide with it. Kept syntactically valid
        /// as a type name (letters/digits) so the failure is "not found", never a parse quirk.
        /// </summary>
        private static Arbitrary<string> AbsentTypeNameArb =>
            Arb.From(
                from guid in Gen.Constant(0).Select(_ => Guid.NewGuid())
                select $"Mtm5.Absent.Type_{guid:N}");

        /// <summary>
        /// A method name guaranteed not to exist on <see cref="Mtm5_PresentType"/>. GUID-based so it
        /// never matches the single real method on the fixture.
        /// </summary>
        private static Arbitrary<string> AbsentMethodNameArb =>
            Arb.From(
                from guid in Gen.Constant(0).Select(_ => Guid.NewGuid())
                select $"AbsentMethod_{guid:N}");

        private static IReadOnlyList<JsonElement> NoArgs => Array.Empty<JsonElement>();

        // --- Service create/update path: JobMethodResolver.ResolveMethod -----------------------

        /// <summary>
        /// Req 1.7: an absent type name yields a <see cref="MethodResolutionError.TypeNotFound"/>
        /// failure whose error message contains the requested type name verbatim, with a null
        /// <c>Method</c> (no stored state is touched on the resolution path).
        /// </summary>
        [Property(MaxTest = 100)]
        public Property ResolveMethod_AbsentType_FailsTypeNotFound_WithTypeNameVerbatim()
        {
            var resolver = new JobMethodResolver();

            return Prop.ForAll(AbsentTypeNameArb, absentType =>
            {
                var result = resolver.ResolveMethod(absentType, "AnyMethod", 0, NoArgs);

                if (result.Success)
                    return false.Label($"[ResolveMethod] expected failure for absent type '{absentType}' but succeeded");
                if (result.Method is not null)
                    return false.Label($"[ResolveMethod] failed result for '{absentType}' must have null Method");
                if (result.ErrorKind != MethodResolutionError.TypeNotFound)
                    return false.Label(
                        $"[ResolveMethod] expected TypeNotFound for '{absentType}' but got {result.ErrorKind?.ToString() ?? "<none>"}");

                return (result.Error is not null && result.Error.Contains(absentType, StringComparison.Ordinal))
                    .Label($"[ResolveMethod] error '{result.Error}' did not contain type name '{absentType}' verbatim (Req 1.7)");
            });
        }

        /// <summary>
        /// Req 1.8: for a present type, an absent method name yields a
        /// <see cref="MethodResolutionError.MethodNotFound"/> failure whose error message contains
        /// the requested method name verbatim, with a null <c>Method</c>.
        /// </summary>
        [Property(MaxTest = 100)]
        public Property ResolveMethod_PresentType_AbsentMethod_FailsMethodNotFound_WithMethodNameVerbatim()
        {
            var resolver = new JobMethodResolver();

            return Prop.ForAll(AbsentMethodNameArb, absentMethod =>
            {
                var result = resolver.ResolveMethod(PresentTypeName, absentMethod, 0, NoArgs);

                if (result.Success)
                    return false.Label($"[ResolveMethod] expected failure for absent method '{absentMethod}' but succeeded");
                if (result.Method is not null)
                    return false.Label($"[ResolveMethod] failed result for '{absentMethod}' must have null Method");
                if (result.ErrorKind != MethodResolutionError.MethodNotFound)
                    return false.Label(
                        $"[ResolveMethod] expected MethodNotFound for '{absentMethod}' but got {result.ErrorKind?.ToString() ?? "<none>"}");

                return (result.Error is not null && result.Error.Contains(absentMethod, StringComparison.Ordinal))
                    .Label($"[ResolveMethod] error '{result.Error}' did not contain method name '{absentMethod}' verbatim (Req 1.8)");
            });
        }

        // --- Custom-method validation path: JobMethodResolver.ValidateCustomMethod --------------

        /// <summary>
        /// Req 7.2: an absent type name yields a <see cref="CustomMethodCheck.TypeNotFound"/> result
        /// whose message contains the requested type name verbatim, with no descriptor produced.
        /// </summary>
        [Property(MaxTest = 100)]
        public Property ValidateCustomMethod_AbsentType_FailsTypeNotFound_WithTypeNameVerbatim()
        {
            var resolver = new JobMethodResolver();

            return Prop.ForAll(AbsentTypeNameArb, absentType =>
            {
                var result = resolver.ValidateCustomMethod(absentType, "AnyMethod");

                if (result.IsValid)
                    return false.Label($"[ValidateCustomMethod] expected invalid for absent type '{absentType}' but was valid");
                if (result.Descriptor is not null)
                    return false.Label($"[ValidateCustomMethod] invalid result for '{absentType}' must have null Descriptor");
                if (result.FailedCheck != CustomMethodCheck.TypeNotFound)
                    return false.Label(
                        $"[ValidateCustomMethod] expected TypeNotFound for '{absentType}' but got {result.FailedCheck}");

                return (result.Message is not null && result.Message.Contains(absentType, StringComparison.Ordinal))
                    .Label($"[ValidateCustomMethod] message '{result.Message}' did not contain type name '{absentType}' verbatim (Req 7.2)");
            });
        }

        /// <summary>
        /// Req 7.3: for a present type, an absent method name yields a
        /// <see cref="CustomMethodCheck.MethodNotFound"/> result whose message contains the
        /// requested method name verbatim, with no descriptor produced.
        /// </summary>
        [Property(MaxTest = 100)]
        public Property ValidateCustomMethod_PresentType_AbsentMethod_FailsMethodNotFound_WithMethodNameVerbatim()
        {
            var resolver = new JobMethodResolver();

            return Prop.ForAll(AbsentMethodNameArb, absentMethod =>
            {
                var result = resolver.ValidateCustomMethod(PresentTypeName, absentMethod);

                if (result.IsValid)
                    return false.Label($"[ValidateCustomMethod] expected invalid for absent method '{absentMethod}' but was valid");
                if (result.Descriptor is not null)
                    return false.Label($"[ValidateCustomMethod] invalid result for '{absentMethod}' must have null Descriptor");
                if (result.FailedCheck != CustomMethodCheck.MethodNotFound)
                    return false.Label(
                        $"[ValidateCustomMethod] expected MethodNotFound for '{absentMethod}' but got {result.FailedCheck}");

                return (result.Message is not null && result.Message.Contains(absentMethod, StringComparison.Ordinal))
                    .Label($"[ValidateCustomMethod] message '{result.Message}' did not contain method name '{absentMethod}' verbatim (Req 7.3)");
            });
        }

        // --- Sanity anchor: the present fixture really does resolve -----------------------------

        /// <summary>
        /// Confirms the fixture anchor is genuinely present and resolvable, so the missing-method
        /// scenarios above exercise a real type rather than an accidentally-absent one.
        /// </summary>
        [Property(MaxTest = 1)]
        public Property PresentFixture_KnownMethod_Resolves()
        {
            var resolver = new JobMethodResolver();
            var result = resolver.ResolveMethod(PresentTypeName, KnownMethodName, 0, NoArgs);

            return (result.Success && result.Method is not null)
                .Label($"Expected the fixture anchor '{PresentTypeName}.{KnownMethodName}' to resolve but got: {result.Error}");
        }
    }
}
