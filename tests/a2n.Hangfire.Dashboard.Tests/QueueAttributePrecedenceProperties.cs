using System;
using System.Linq;
using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Services;

// Feature: job-builder, Property 20: Queue-attribute reporting and effective-queue precedence.
//
// For any method, the resolver reports whether a Queue_Attribute is present on the method or its
// declaring class and the queue name it specifies (Req 13.1); and the Effective_Queue equals the
// Queue_Attribute's queue name when a Queue_Attribute applies, otherwise the Configured_Queue
// (or the Default_Queue "default" when the Configured_Queue is blank) (Req 13.6).
//
// **Validates: Requirements 13.1, 13.6**

// --- Fixtures -------------------------------------------------------------------------------
//
// Uniquely "Qap20_"-prefixed fixture types so the resolver's assembly-wide scan and reflection
// cannot collide with sibling test fixtures or production types. Methods are only reflected over,
// never invoked.
namespace QueueAttributePrecedenceFixtures
{
    /// <summary>
    /// No class-level QueueAttribute; the method carries <c>[Queue("method-queue-a")]</c>.
    /// Expected: IsPresent=true, QueueName="method-queue-a", IsFormatTemplate=false (Req 13.1).
    /// </summary>
    public class Qap20_MethodQueueTarget
    {
        [Queue("method-queue-a")]
        public void MethodWithQueue()
        {
        }
    }

    /// <summary>
    /// Class carries <c>[Queue("class-queue-b")]</c>; the method has no QueueAttribute, so the
    /// declaring-class attribute is reported. Expected: IsPresent=true, QueueName="class-queue-b",
    /// IsFormatTemplate=false (Req 13.1).
    /// </summary>
    [Queue("class-queue-b")]
    public class Qap20_ClassQueueTarget
    {
        public void MethodWithoutQueue()
        {
        }
    }

    /// <summary>
    /// The method's QueueAttribute value is a format template (contains '{'). Expected:
    /// IsPresent=true, QueueName="{0}", IsFormatTemplate=true (Req 13.1, 13.4).
    /// </summary>
    public class Qap20_MethodTemplateQueueTarget
    {
        [Queue("{0}")]
        public void MethodWithTemplateQueue()
        {
        }
    }

    /// <summary>
    /// The class's QueueAttribute value is a format template; the method has no own attribute.
    /// Expected: IsPresent=true, QueueName="queue-{0}", IsFormatTemplate=true (Req 13.1, 13.4).
    /// </summary>
    [Queue("queue-{0}")]
    public class Qap20_ClassTemplateQueueTarget
    {
        public void MethodWithoutQueue()
        {
        }
    }

    /// <summary>
    /// Neither the method nor its declaring class carries a QueueAttribute. Expected:
    /// IsPresent=false (Req 13.1).
    /// </summary>
    public class Qap20_NoQueueTarget
    {
        public void MethodWithoutQueue()
        {
        }
    }

    /// <summary>
    /// The class carries <c>[Queue("class-q")]</c> AND the method carries <c>[Queue("method-q")]</c>.
    /// The method's own attribute takes precedence over the declaring class's. Expected:
    /// IsPresent=true, QueueName="method-q" (Req 13.1).
    /// </summary>
    [Queue("class-q")]
    public class Qap20_PrecedenceTarget
    {
        [Queue("method-q")]
        public void MethodOverridesClass()
        {
        }
    }
}

namespace a2n.Hangfire.Dashboard.Tests
{
    /// <summary>
    /// Property test for queue-attribute reporting and effective-queue precedence (Property 20).
    ///
    /// **Validates: Requirements 13.1, 13.6**
    /// </summary>
    public class QueueAttributePrecedenceProperties
    {
        // --- Part 1: Queue-attribute reporting (Req 13.1) --------------------------------------

        /// <summary>
        /// One generated reporting scenario: the fixture method to report on and the
        /// <see cref="QueueAttributeInfo"/> the resolver is expected to produce.
        /// </summary>
        private sealed class ReportingScenario
        {
            public MethodInfo Method { get; init; }
            public bool ExpectedPresent { get; init; }
            public string ExpectedQueueName { get; init; }
            public bool ExpectedTemplate { get; init; }
            public string Description { get; init; }

            public override string ToString() => Description;
        }

        private static MethodInfo Method<T>(string name) =>
            typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"fixture method {typeof(T).Name}.{name} not found");

        private static Arbitrary<ReportingScenario> ReportingScenarioArb =>
            Arb.From(Gen.Elements(
                new ReportingScenario
                {
                    Method = Method<QueueAttributePrecedenceFixtures.Qap20_MethodQueueTarget>(
                        nameof(QueueAttributePrecedenceFixtures.Qap20_MethodQueueTarget.MethodWithQueue)),
                    ExpectedPresent = true,
                    ExpectedQueueName = "method-queue-a",
                    ExpectedTemplate = false,
                    Description = "method [Queue(\"method-queue-a\")] -> present, name verbatim, not template",
                },
                new ReportingScenario
                {
                    Method = Method<QueueAttributePrecedenceFixtures.Qap20_ClassQueueTarget>(
                        nameof(QueueAttributePrecedenceFixtures.Qap20_ClassQueueTarget.MethodWithoutQueue)),
                    ExpectedPresent = true,
                    ExpectedQueueName = "class-queue-b",
                    ExpectedTemplate = false,
                    Description = "class [Queue(\"class-queue-b\")], method bare -> present from class",
                },
                new ReportingScenario
                {
                    Method = Method<QueueAttributePrecedenceFixtures.Qap20_MethodTemplateQueueTarget>(
                        nameof(QueueAttributePrecedenceFixtures.Qap20_MethodTemplateQueueTarget.MethodWithTemplateQueue)),
                    ExpectedPresent = true,
                    ExpectedQueueName = "{0}",
                    ExpectedTemplate = true,
                    Description = "method [Queue(\"{0}\")] -> present, format template",
                },
                new ReportingScenario
                {
                    Method = Method<QueueAttributePrecedenceFixtures.Qap20_ClassTemplateQueueTarget>(
                        nameof(QueueAttributePrecedenceFixtures.Qap20_ClassTemplateQueueTarget.MethodWithoutQueue)),
                    ExpectedPresent = true,
                    ExpectedQueueName = "queue-{0}",
                    ExpectedTemplate = true,
                    Description = "class [Queue(\"queue-{0}\")], method bare -> present from class, format template",
                },
                new ReportingScenario
                {
                    Method = Method<QueueAttributePrecedenceFixtures.Qap20_NoQueueTarget>(
                        nameof(QueueAttributePrecedenceFixtures.Qap20_NoQueueTarget.MethodWithoutQueue)),
                    ExpectedPresent = false,
                    ExpectedQueueName = null,
                    ExpectedTemplate = false,
                    Description = "no QueueAttribute on method or class -> not present",
                },
                new ReportingScenario
                {
                    Method = Method<QueueAttributePrecedenceFixtures.Qap20_PrecedenceTarget>(
                        nameof(QueueAttributePrecedenceFixtures.Qap20_PrecedenceTarget.MethodOverridesClass)),
                    ExpectedPresent = true,
                    ExpectedQueueName = "method-q",
                    ExpectedTemplate = false,
                    Description = "method [Queue(\"method-q\")] over class [Queue(\"class-q\")] -> method wins",
                }));

        [Property(MaxTest = 100)]
        public Property GetQueueAttribute_ReportsPresenceQueueNameAndTemplate()
        {
            var resolver = new JobMethodResolver();

            return Prop.ForAll(ReportingScenarioArb, sc =>
            {
                var info = resolver.GetQueueAttribute(sc.Method);

                if (info is null)
                    return false.Label($"[{sc.Description}] returned a null QueueAttributeInfo");

                if (info.IsPresent != sc.ExpectedPresent)
                    return false.Label(
                        $"[{sc.Description}] IsPresent={info.IsPresent} != expected {sc.ExpectedPresent} (Req 13.1)");

                if (!sc.ExpectedPresent)
                {
                    // When absent, no queue name and not a template.
                    if (info.IsFormatTemplate)
                        return false.Label($"[{sc.Description}] absent attribute must not be a format template");
                    return true.ToProperty();
                }

                if (info.QueueName != sc.ExpectedQueueName)
                    return false.Label(
                        $"[{sc.Description}] QueueName '{info.QueueName}' != expected '{sc.ExpectedQueueName}' (Req 13.1)");

                if (info.IsFormatTemplate != sc.ExpectedTemplate)
                    return false.Label(
                        $"[{sc.Description}] IsFormatTemplate={info.IsFormatTemplate} != expected {sc.ExpectedTemplate} (Req 13.1)");

                return true.ToProperty();
            });
        }

        // --- Part 2: Effective-queue precedence (Req 13.6) -------------------------------------

        /// <summary>
        /// One generated precedence scenario: the reported QueueAttributeInfo, the operator's
        /// Configured_Queue, and the Effective_Queue the helper is expected to resolve.
        /// </summary>
        private sealed class PrecedenceScenario
        {
            public QueueAttributeInfo Attribute { get; init; }
            public string ConfiguredQueue { get; init; }
            public string ExpectedEffective { get; init; }
            public string Description { get; init; }

            public override string ToString() => Description;
        }

        // Valid queue identifiers: lowercase letters/digits/hyphen/underscore, starting with a letter.
        private static Gen<string> ValidQueueNameGen =>
            from first in Gen.Elements("abcdefghijklmnopqrstuvwxyz".ToCharArray())
            from rest in Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray())
                .ListOf().Select(cs => cs.Take(15).ToArray())
            select first + new string(rest);

        private static Gen<string> BlankQueueGen => Gen.Elements<string>(null, "", "   ");

        private static Gen<string> AnyConfiguredQueueGen =>
            Gen.OneOf(ValidQueueNameGen, BlankQueueGen);

        // Attribute present: its queue name wins regardless of the configured queue (Req 13.6).
        private static Gen<PrecedenceScenario> AttributePresentGen =>
            from attrQueue in ValidQueueNameGen
            from configured in AnyConfiguredQueueGen
            select new PrecedenceScenario
            {
                Attribute = new QueueAttributeInfo(true, attrQueue, attrQueue.Contains('{')),
                ConfiguredQueue = configured,
                ExpectedEffective = attrQueue,
                Description = $"attribute present '{attrQueue}' wins over configured '{Describe(configured)}'",
            };

        // No attribute (null info): the configured queue is used, defaulting to "default" when blank.
        private static Gen<PrecedenceScenario> NoAttributeNullGen =>
            from configured in AnyConfiguredQueueGen
            select new PrecedenceScenario
            {
                Attribute = null,
                ConfiguredQueue = configured,
                ExpectedEffective = string.IsNullOrWhiteSpace(configured) ? "default" : configured,
                Description = $"no attribute (null), configured '{Describe(configured)}' -> effective",
            };

        // Attribute reported but absent (IsPresent=false): falls back to the configured queue.
        private static Gen<PrecedenceScenario> NoAttributeAbsentGen =>
            from configured in AnyConfiguredQueueGen
            select new PrecedenceScenario
            {
                Attribute = new QueueAttributeInfo(false, null, false),
                ConfiguredQueue = configured,
                ExpectedEffective = string.IsNullOrWhiteSpace(configured) ? "default" : configured,
                Description = $"attribute absent (IsPresent=false), configured '{Describe(configured)}' -> effective",
            };

        private static Arbitrary<PrecedenceScenario> PrecedenceScenarioArb =>
            Arb.From(Gen.OneOf(new[]
            {
                AttributePresentGen,
                NoAttributeNullGen,
                NoAttributeAbsentGen,
            }));

        [Property(MaxTest = 100)]
        public Property EffectiveQueue_Resolve_AppliesAttributeThenConfiguredThenDefault()
        {
            return Prop.ForAll(PrecedenceScenarioArb, sc =>
            {
                var effective = EffectiveQueue.Resolve(sc.Attribute, sc.ConfiguredQueue);

                if (effective != sc.ExpectedEffective)
                    return false.Label(
                        $"[{sc.Description}] effective '{effective}' != expected '{sc.ExpectedEffective}' (Req 13.6)");

                // The effective queue is never null or blank.
                if (string.IsNullOrWhiteSpace(effective))
                    return false.Label($"[{sc.Description}] effective queue must never be blank");

                return true.ToProperty();
            });
        }

        private static string Describe(string s) =>
            s is null ? "null" : s.Length == 0 ? "empty" : string.IsNullOrWhiteSpace(s) ? "whitespace" : s;
    }
}
