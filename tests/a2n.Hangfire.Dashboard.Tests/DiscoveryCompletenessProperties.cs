using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Tags.Attributes;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

// Feature: job-builder, Property 9: Registered-method discovery completeness and de-duplication.
//
// WHEN the Method_Resolver performs a scan, THE Method_Resolver SHALL include every public
// instance or static method — excluding property accessors and constructors — that is
// decorated with a Recognized_Attribute or whose declaring class is decorated with a
// class-targeting Recognized_Attribute, listing each such method exactly once even when both the
// method and its declaring class are decorated (Req 5.1). Eligible abstract methods on interfaces
// and abstract classes are included as canonical Contract_Methods (Req 5.11). The recognized set is
// JobDisplayNameAttribute (method), TagAttribute (class/method) and QueueAttribute (class/method)
// (Req 5.2–5.4). Each Registered_Method's Display_Label is computed via the display-name extraction
// logic, falling back to the method name when no display name is available (Req 5.5, 5.10).

// --- Fixtures -------------------------------------------------------------------------------
//
// IMPORTANT — fixture isolation. JobMethodResolver.GetRegisteredMethods() scans EVERY loaded
// assembly, so any attribute-decorated type in the test assembly pollutes the discovered set. We
// therefore never assert over the TOTAL count; instead we assert properties over our own
// uniquely-prefixed fixtures (the "Dcp9_" prefix), which cannot collide with sibling test fixtures
// (tasks 7.3/7.4) or production types. These fixtures are compiled into the test assembly so they
// are always discoverable, even though the resolver caches on first call.

namespace DiscoveryCompletenessFixtures
{
    /// <summary>Class is undecorated; only the method-level JobDisplayName makes one method eligible.</summary>
    public class Dcp9_DisplayNameOnly
    {
        [JobDisplayName("Dcp9 custom label")]
        public void DecoratedByDisplayName()
        {
        }

        // No recognized attribute on this method and the class is undecorated → NOT discovered.
        public void NotDecorated()
        {
        }
    }

    /// <summary>Class decorated with a class-targeting TagAttribute → all its public methods are eligible.</summary>
    [Tag("dcp9")]
    public class Dcp9_TagOnClass
    {
        public void MethodA()
        {
        }

        public void MethodB()
        {
        }
    }

    /// <summary>Class decorated with a class-targeting QueueAttribute → its public methods are eligible.</summary>
    [Queue("dcp9-queue")]
    public class Dcp9_QueueOnClass
    {
        public void QueuedClassMethod()
        {
        }
    }

    /// <summary>Undecorated class with method-level Tag / Queue attributes; a third method is undecorated.</summary>
    public class Dcp9_MethodLevel
    {
        [Tag("dcp9-m")]
        public void TaggedMethod()
        {
        }

        [Queue("dcp9-mq")]
        public void QueuedMethod()
        {
        }

        // Neither the method nor the class is decorated → NOT discovered.
        public void Plain()
        {
        }
    }

    /// <summary>
    /// Both the class (Tag) and the method (JobDisplayName) are decorated — the de-duplication case:
    /// the method must be listed exactly once (Req 5.1).
    /// </summary>
    [Tag("dcp9-both")]
    public class Dcp9_BothDecorated
    {
        [JobDisplayName("Dcp9 both")]
        public void DoubleDecorated()
        {
        }
    }

    /// <summary>
    /// Class-decorated abstract type used to verify the member rules: property accessors and
    /// constructors are never discovered (Req 5.1), a concrete public method is discovered, and the
    /// abstract method is surfaced as a canonical Contract_Method (Req 5.11).
    /// </summary>
    [Tag("dcp9-abstract")]
    public abstract class Dcp9_WithExcludables
    {
        protected Dcp9_WithExcludables()
        {
        }

        // Abstract method on an abstract class → a Contract_Method (Req 5.11), discovered.
        public abstract void AbstractMethod();

        // Property accessors get_Prop / set_Prop are special-name → excluded.
        public string Prop { get; set; }

        // Concrete, public, non-special → discovered.
        public void ConcreteMethod()
        {
        }
    }
}

// --- Property test --------------------------------------------------------------------------

namespace a2n.Hangfire.Dashboard.Tests
{
    /// <summary>
    /// Property test for registered-method discovery completeness and de-duplication (Property 9).
    ///
    /// **Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5, 5.10**
    /// </summary>
    public class DiscoveryCompletenessProperties
{
    /// <summary>
    /// One expected entry in the discovered set: a fixture method that must be present, paired with
    /// the exact Display_Label it should carry when a display name applies, or <c>null</c> to assert
    /// the method-name fallback (Req 5.5, 5.10).
    /// </summary>
    private sealed record Expected(Type DeclaringType, string MethodName, string ExpectedLabel);

    /// <summary>A fixture member that must NOT appear in the discovered set (Req 5.1 exclusions).</summary>
    private sealed record Excluded(Type DeclaringType, string MemberName);

    // The discovery result is cached on first call and scans all assemblies; computing it once here
    // mirrors how the resolver is actually used.
    private static readonly IReadOnlyList<JobMethodDescriptor> Discovered =
        new JobMethodResolver().GetRegisteredMethods();

    private static readonly Expected[] ExpectedPresent =
    [
        // JobDisplayNameAttribute on a method only (Req 5.2): present with its display label.
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_DisplayNameOnly), "DecoratedByDisplayName", "Dcp9 custom label"),

        // TagAttribute on the class (Req 5.3): every public method is eligible, label falls back.
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_TagOnClass), "MethodA", null),
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_TagOnClass), "MethodB", null),

        // QueueAttribute on the class (Req 5.4).
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_QueueOnClass), "QueuedClassMethod", null),

        // Method-level Tag / Queue on an undecorated class (Req 5.3, 5.4).
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_MethodLevel), "TaggedMethod", null),
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_MethodLevel), "QueuedMethod", null),

        // Both class and method decorated → exactly one entry (Req 5.1 de-dup), with the display label.
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_BothDecorated), "DoubleDecorated", "Dcp9 both"),

        // Concrete public method on a class-decorated abstract type (Req 5.1).
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_WithExcludables), "ConcreteMethod", null),

        // Abstract method on a class-decorated abstract type is a Contract_Method (Req 5.11):
        // surfaced as the canonical target with a method-name fallback label.
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_WithExcludables), "AbstractMethod", null),
    ];

    private static readonly Excluded[] ExpectedAbsent =
    [
        // Undecorated method on an undecorated class.
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_DisplayNameOnly), "NotDecorated"),
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_MethodLevel), "Plain"),

        // Property accessors and constructor on the class-decorated abstract type remain excluded.
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_WithExcludables), "get_Prop"),
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_WithExcludables), "set_Prop"),
        new(typeof(DiscoveryCompletenessFixtures.Dcp9_WithExcludables), ".ctor"),
    ];

    private static Arbitrary<Expected> ExpectedPresentArb =>
        Arb.From(Gen.Elements((IEnumerable<Expected>)ExpectedPresent));

    private static Arbitrary<Excluded> ExpectedAbsentArb =>
        Arb.From(Gen.Elements((IEnumerable<Excluded>)ExpectedAbsent));

    private static int CountMatches(Type declaringType, string methodName) =>
        Discovered.Count(d =>
            d.TypeFullName == declaringType.FullName &&
            string.Equals(d.MethodName, methodName, StringComparison.Ordinal));

    /// <summary>
    /// Every decorated, eligible fixture method is present EXACTLY once (completeness + de-dup,
    /// Req 5.1–5.4) and carries a non-empty Display_Label that is either the extracted display name
    /// or — when none applies — a fallback that includes the method name (Req 5.5, 5.10).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property EveryDecoratedMethod_IsPresentExactlyOnce_WithDisplayLabel()
    {
        return Prop.ForAll(ExpectedPresentArb, expected =>
        {
            var matches = Discovered
                .Where(d => d.TypeFullName == expected.DeclaringType.FullName &&
                            string.Equals(d.MethodName, expected.MethodName, StringComparison.Ordinal))
                .ToList();

            // Req 5.1–5.4: present, and exactly once even when class + method are both decorated.
            if (matches.Count != 1)
            {
                return false.Label(
                    $"Expected '{expected.DeclaringType.Name}.{expected.MethodName}' to appear exactly " +
                    $"once in discovery but found {matches.Count} (Req 5.1).");
            }

            var label = matches[0].DisplayLabel;

            // Req 5.5/5.10: the label is always non-empty.
            if (string.IsNullOrWhiteSpace(label))
            {
                return false.Label(
                    $"Display_Label for '{expected.DeclaringType.Name}.{expected.MethodName}' was empty (Req 5.5, 5.10).");
            }

            if (expected.ExpectedLabel is not null)
            {
                // Req 5.5: a display-name attribute drives the label exactly.
                return (label == expected.ExpectedLabel).Label(
                    $"Expected Display_Label '{expected.ExpectedLabel}' but got '{label}' " +
                    $"for '{expected.DeclaringType.Name}.{expected.MethodName}' (Req 5.5).");
            }

            // Req 5.10: with no display name the label falls back to one that includes the method name.
            return label.Contains(expected.MethodName, StringComparison.Ordinal).Label(
                $"Fallback Display_Label '{label}' did not include the method name " +
                $"'{expected.MethodName}' (Req 5.10).");
        });
    }

    /// <summary>
    /// Undecorated methods, property accessors and constructors are never discovered
    /// (Req 5.1 exclusions). Abstract Contract_Methods are covered by the present-set property.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property IneligibleMembers_AreNeverDiscovered()
    {
        return Prop.ForAll(ExpectedAbsentArb, absent =>
        {
            var count = CountMatches(absent.DeclaringType, absent.MemberName);

            return (count == 0).Label(
                $"Expected '{absent.DeclaringType.Name}.{absent.MemberName}' to be excluded from " +
                $"discovery but found {count} occurrence(s) (Req 5.1).");
        });
    }
}
}
