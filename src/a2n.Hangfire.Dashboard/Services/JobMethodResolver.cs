using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Tags.Attributes;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Resolves Hangfire job methods from a type name + method name in an overload-safe way and
/// produces <see cref="JobMethodDescriptor"/>s for the Job Builder. See
/// <c>.kiro/specs/job-builder/design.md</c> ("JobMethodResolver").
/// </summary>
/// <remarks>
/// This is the Phase 2 surface of the resolver: it implements <see cref="GetRegisteredMethods"/>
/// (attribute-based discovery + caching, Req 5), <see cref="ResolveMethod"/> (Req 1.5–1.8),
/// <see cref="Describe"/> (Req 6.4), <see cref="ValidateCustomMethod"/> (ordered custom-method
/// checks, Req 7.1–7.6) and <see cref="GetQueueAttribute"/> (queue reporting, Req 13.1). The
/// discovery cache is held in a <c>volatile</c> field populated once under a lock.
/// </remarks>
public sealed class JobMethodResolver
{
    /// <summary>
    /// The Recognized_Attribute table (Req 5.2–5.4): maps each attribute type to the
    /// <see cref="AttributeTargets"/> for which it marks a method eligible. A method is discovered
    /// when it carries one of these attributes valid for <see cref="AttributeTargets.Method"/>, or
    /// when its declaring class carries one valid for <see cref="AttributeTargets.Class"/>.
    /// </summary>
    private static readonly (Type AttributeType, AttributeTargets ValidTargets)[] RecognizedAttributes =
    [
        (typeof(JobDisplayNameAttribute), AttributeTargets.Method),
        (typeof(TagAttribute), AttributeTargets.Class | AttributeTargets.Method),
        (typeof(QueueAttribute), AttributeTargets.Class | AttributeTargets.Method),
    ];

    /// <summary>Guards population of <see cref="_registeredMethodsCache"/>.</summary>
    private readonly object _scanLock = new();

    /// <summary>
    /// The cached discovery result (Req 5.6, 5.8). <c>null</c> until the first scan completes;
    /// populated exactly once under <see cref="_scanLock"/>. <c>volatile</c> so other circuits see
    /// the fully-built list without tearing.
    /// </summary>
    private volatile IReadOnlyList<JobMethodDescriptor> _registeredMethodsCache;

    /// <summary>
    /// Scans the loaded assemblies for Registered_Methods on the first call and caches the result;
    /// subsequent calls return the cache without rescanning (Req 5.1–5.10).
    /// </summary>
    /// <remarks>
    /// A method is included when it is public, non-abstract, not a special-name member (property
    /// accessors/operators), not a constructor, and the method <em>or</em> its declaring class
    /// carries a Recognized_Attribute valid for that target (Req 5.1). Each method appears exactly
    /// once even when both it and its class are decorated. Display_Labels are computed via
    /// <see cref="JobNameHelper"/>, falling back to the method name (Req 5.5, 5.10). Assemblies that
    /// cannot be inspected are skipped and the scan continues (Req 5.7); an empty result is valid
    /// (Req 5.9).
    /// </remarks>
    public IReadOnlyList<JobMethodDescriptor> GetRegisteredMethods()
    {
        var cache = _registeredMethodsCache;
        if (cache is not null)
            return cache;

        lock (_scanLock)
        {
            // Re-check inside the lock: another thread may have populated the cache while we waited.
            cache = _registeredMethodsCache;
            if (cache is not null)
                return cache;

            cache = ScanRegisteredMethods();
            _registeredMethodsCache = cache;
            return cache;
        }
    }

    /// <summary>
    /// Performs the assembly scan that backs <see cref="GetRegisteredMethods"/>. De-duplicates by
    /// <see cref="MethodInfo"/> so a method that is decorated and whose class is also decorated is
    /// listed once (Req 5.1).
    /// </summary>
    private static IReadOnlyList<JobMethodDescriptor> ScanRegisteredMethods()
    {
        // Collect every inspectable type once so we can run a two-stage scan: eligible Contract_Methods
        // first (abstract methods on interfaces and abstract classes — the canonical, portable identity
        // for DI-dispatched jobs), then concrete methods. Both are surfaced (Option Y): a concrete
        // method that implements/overrides a contract is labelled Implementation rather than hidden, so
        // operators can target the concrete type when DI is not configured while still being steered to
        // the contract when it is.
        var allTypes = new List<Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Each assembly is inspected independently; an uninspectable one is skipped (Req 5.7).
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type is not null)
                    allTypes.Add(type);
            }
        }

        var seen = new HashSet<MethodInfo>(MethodKeyComparer.Instance);
        var descriptors = new List<JobMethodDescriptor>();

        // Stage 1 — eligible Contract_Methods (Req 5.1, 5.11). These are abstract methods declared on
        // an interface or an abstract class, so the abstract-method exclusion applied to concrete
        // types is intentionally NOT applied here: such a declaration IS a valid (DI-dispatched) target.
        foreach (var type in allTypes)
        {
            // An abstract class in IL is `abstract`; a static class is `abstract && sealed`. Only
            // interfaces and genuine (non-static) abstract classes can declare Contract_Methods.
            var isInterface = type.IsInterface;
            var isAbstractClass = type.IsAbstract && !type.IsSealed && !type.IsInterface;
            if (!isInterface && !isAbstractClass)
                continue;

            var typeDecorated = CarriesRecognizedAttribute(type, AttributeTargets.Class);

            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // On an abstract class only the abstract members are contracts; its concrete members
                // are ordinary methods handled by Stage 2. (Interface members are all abstract.)
                if (method.IsSpecialName || !method.IsAbstract)
                    continue;

                var eligible = typeDecorated
                               || CarriesRecognizedAttribute(method, AttributeTargets.Method);
                if (!eligible)
                    continue;

                if (seen.Add(method))
                    descriptors.Add(DescribeInternal(method, JobMethodKind.Contract));
            }
        }

        // Stage 2 — concrete (non-interface) methods. Surfaced WITH a Kind label; a method that
        // implements an interface Contract_Method or overrides an abstract-class Contract_Method is
        // marked Implementation (not hidden) so it remains selectable when DI is not used (Req 5.11).
        foreach (var type in allTypes)
        {
            if (type.IsInterface)
                continue;

            var classDecorated = CarriesRecognizedAttribute(type, AttributeTargets.Class);

            // DeclaredOnly so inherited members (e.g. object.ToString) are not pulled in via a
            // decorated subclass; only methods actually declared on this type are eligible.
            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (method.IsAbstract || method.IsSpecialName || method.IsConstructor)
                    continue;

                var eligible = classDecorated
                               || CarriesRecognizedAttribute(method, AttributeTargets.Method);
                if (!eligible)
                    continue;

                var kind = (ImplementsEligibleInterfaceContract(type, method)
                            || OverridesEligibleAbstractContract(method))
                    ? JobMethodKind.Implementation
                    : JobMethodKind.Standalone;

                if (seen.Add(method))
                    descriptors.Add(DescribeInternal(method, kind));
            }
        }

        return descriptors;
    }

    /// <summary>
    /// Classifies a method as a <see cref="JobMethodKind"/> independently of the scan, for the
    /// <see cref="Describe"/>/custom-method paths (Req 5.11).
    /// </summary>
    private static JobMethodKind DetermineKind(MethodInfo method)
    {
        var declaringType = method.DeclaringType;
        if (method.IsAbstract && declaringType is not null
            && (declaringType.IsInterface || (declaringType.IsAbstract && !declaringType.IsSealed)))
        {
            return JobMethodKind.Contract;
        }

        if (declaringType is not null
            && (ImplementsEligibleInterfaceContract(declaringType, method)
                || OverridesEligibleAbstractContract(method)))
        {
            return JobMethodKind.Implementation;
        }

        return JobMethodKind.Standalone;
    }

    /// <summary>
    /// True when <paramref name="method"/> on <paramref name="type"/> implements an interface method
    /// that is itself an eligible Contract_Method (the interface or the method carries a
    /// Recognized_Attribute) (Req 5.11).
    /// </summary>
    private static bool ImplementsEligibleInterfaceContract(Type type, MethodInfo method)
    {
        foreach (var iface in type.GetInterfaces())
        {
            InterfaceMapping map;
            try
            {
                map = type.GetInterfaceMap(iface);
            }
            catch
            {
                // Some type/interface combinations (e.g. open generics) don't support mapping; skip.
                continue;
            }

            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (!MethodKeyComparer.Instance.Equals(map.TargetMethods[i], method))
                    continue;

                var interfaceMethod = map.InterfaceMethods[i];
                if (CarriesRecognizedAttribute(iface, AttributeTargets.Class)
                    || CarriesRecognizedAttribute(interfaceMethod, AttributeTargets.Method))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="method"/> overrides an abstract method that is itself an eligible
    /// Contract_Method on an abstract class (the class or the method carries a Recognized_Attribute).
    /// <see cref="MethodInfo.GetBaseDefinition"/> walks back to the original declaration (Req 5.11).
    /// </summary>
    private static bool OverridesEligibleAbstractContract(MethodInfo method)
    {
        var baseDefinition = method.GetBaseDefinition();
        if (baseDefinition is null || MethodKeyComparer.Instance.Equals(baseDefinition, method))
            return false;

        var declaringType = baseDefinition.DeclaringType;
        if (declaringType is null || !declaringType.IsAbstract || declaringType.IsSealed
            || declaringType.IsInterface || !baseDefinition.IsAbstract)
        {
            return false;
        }

        return CarriesRecognizedAttribute(declaringType, AttributeTargets.Class)
               || CarriesRecognizedAttribute(baseDefinition, AttributeTargets.Method);
    }

    /// <summary>
    /// Compares methods by metadata token + module so the same logical method resolved through
    /// different reflection paths (e.g. <see cref="Type.GetMethods()"/> vs
    /// <see cref="Type.GetInterfaceMap"/>) compares equal without relying on reference identity.
    /// </summary>
    private sealed class MethodKeyComparer : IEqualityComparer<MethodInfo>
    {
        public static readonly MethodKeyComparer Instance = new();

        public bool Equals(MethodInfo x, MethodInfo y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.MetadataToken == y.MetadataToken && System.Collections.Generic.EqualityComparer<Module>.Default.Equals(x.Module, y.Module);
        }

        public int GetHashCode(MethodInfo obj)
            => HashCode.Combine(obj.MetadataToken, obj.Module);
    }

    /// <summary>
    /// True when <paramref name="member"/> carries a Recognized_Attribute whose valid targets
    /// include <paramref name="target"/> (Req 5.2–5.4).
    /// </summary>
    private static bool CarriesRecognizedAttribute(MemberInfo member, AttributeTargets target)
    {
        foreach (var (attributeType, validTargets) in RecognizedAttributes)
        {
            if ((validTargets & target) == 0)
                continue;

            if (member.IsDefined(attributeType, inherit: true))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Produces a <see cref="JobMethodDescriptor"/> for <paramref name="method"/> whose
    /// <see cref="JobMethodDescriptor.JobParameters"/> exclude Injected_Parameters (Req 6.4).
    /// </summary>
    public JobMethodDescriptor Describe(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return DescribeInternal(method, DetermineKind(method));
    }

    private static JobMethodDescriptor DescribeInternal(MethodInfo method, JobMethodKind kind)
    {
        var allParameters = method.GetParameters();
        var jobParameters = new List<JobParameterDescriptor>();

        foreach (var p in allParameters)
        {
            if (IsInjectedParameter(p))
                continue;

            var isNullable = IsNullableType(p.ParameterType);

            jobParameters.Add(new JobParameterDescriptor(
                Name: p.Name ?? string.Empty,
                DeclaredType: p.ParameterType,
                InputKind: ParameterInputMapper.Map(p.ParameterType),
                IsRequired: !p.IsOptional && !isNullable,
                IsNullable: isNullable,
                Position: p.Position));
        }

        return new JobMethodDescriptor(
            TypeFullName: method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? string.Empty,
            MethodName: method.Name,
            DisplayLabel: ComputeDisplayLabel(method),
            JobParameters: jobParameters,
            Queue: GetQueueAttributeStatic(method),
            Kind: kind);
    }

    /// <summary>
    /// Resolves the single method overload on <paramref name="typeName"/> named
    /// <paramref name="methodName"/> whose count of non-injected Job_Parameters equals
    /// <paramref name="jobArgCount"/> and whose Job_Parameter types accept the supplied
    /// <paramref name="args"/> (Req 1.5–1.8).
    /// </summary>
    /// <param name="typeName">Full or simple type name to resolve from the loaded assemblies.</param>
    /// <param name="methodName">The method name to resolve.</param>
    /// <param name="jobArgCount">The number of operator-supplied Argument_Values.</param>
    /// <param name="args">
    /// The operator-supplied argument values as <see cref="JsonElement"/>s, ordered over the
    /// Job_Parameters. May be empty; used to disambiguate overloads with equal Job_Parameter counts.
    /// </param>
    public MethodResolutionResult ResolveMethod(
        string typeName,
        string methodName,
        int jobArgCount,
        IReadOnlyList<JsonElement> args)
    {
        var type = ResolveType(typeName);
        if (type is null)
        {
            return new MethodResolutionResult(
                false, null,
                $"Type '{typeName}' not found in loaded assemblies.",
                MethodResolutionError.TypeNotFound);
        }

        // Public instance or static methods with the requested name, excluding property
        // accessors/operators (special-name). Abstract methods are intentionally INCLUDED:
        // interface and abstract-class contract methods are abstract yet valid job targets —
        // Hangfire activates them via the DI-based JobActivator, and Hangfire's own Job model
        // imposes no abstract/interface restriction (only that DeclaringType is assignable from the
        // selected type). Constructors are never returned by GetMethods.
        var named = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal)
                        && !m.IsSpecialName)
            .ToList();

        if (named.Count == 0)
        {
            return new MethodResolutionResult(
                false, null,
                $"Method '{methodName}' not found on type '{typeName}'.",
                MethodResolutionError.MethodNotFound);
        }

        // Keep only overloads whose Job_Parameter count matches the supplied argument count (Req 1.5).
        var countMatches = named
            .Where(m => JobParameters(m).Count == jobArgCount)
            .ToList();

        if (countMatches.Count == 0)
        {
            return new MethodResolutionResult(
                false, null,
                $"No overload of '{methodName}' on type '{typeName}' accepts {jobArgCount} argument(s); no single matching overload could be determined.",
                MethodResolutionError.NoMatchingOverload);
        }

        if (countMatches.Count == 1)
        {
            return new MethodResolutionResult(true, countMatches[0], null, null, type);
        }

        // Several overloads share the same Job_Parameter count: disambiguate by whether each
        // overload's Job_Parameter types accept the supplied argument values (Req 1.5).
        var typeMatches = countMatches
            .Where(m => OverloadAcceptsArgs(m, args))
            .ToList();

        if (typeMatches.Count == 1)
        {
            return new MethodResolutionResult(true, typeMatches[0], null, null, type);
        }

        if (typeMatches.Count == 0)
        {
            return new MethodResolutionResult(
                false, null,
                $"No overload of '{methodName}' on type '{typeName}' accepts the supplied argument values; no single matching overload could be determined.",
                MethodResolutionError.NoMatchingOverload);
        }

        return new MethodResolutionResult(
            false, null,
            $"More than one overload of '{methodName}' on type '{typeName}' matches the supplied arguments; no single matching overload could be determined.",
            MethodResolutionError.AmbiguousOverload);
    }

    /// <summary>
    /// Validates an operator-entered Custom_Method by evaluating, in order, the checks
    /// type-exists → method-exists → public-and-unambiguous, stopping at the first failure and
    /// returning a single result (Req 7.1–7.6).
    /// </summary>
    /// <param name="typeName">The operator-entered type name to resolve from the loaded assemblies.</param>
    /// <param name="methodName">The operator-entered method name to resolve on that type.</param>
    /// <returns>
    /// A <see cref="CustomMethodValidationResult"/>: on success <see cref="CustomMethodValidationResult.IsValid"/>
    /// is <c>true</c>, <see cref="CustomMethodValidationResult.FailedCheck"/> is
    /// <see cref="CustomMethodCheck.None"/>, and <see cref="CustomMethodValidationResult.Descriptor"/>
    /// is the <see cref="Describe(MethodInfo)"/> of the single public match (its
    /// <see cref="JobMethodDescriptor.JobParameters"/> excluding Injected_Parameters, Req 7.6, 6.8);
    /// otherwise it carries the first failed check and an operator-facing message that includes the
    /// offending type/method name verbatim (Req 7.2, 7.3).
    /// </returns>
    /// <remarks>
    /// Ordered checks (Req 7.1):
    /// <list type="number">
    /// <item>Type exists in the loaded assemblies; else <see cref="CustomMethodCheck.TypeNotFound"/> (Req 7.2).</item>
    /// <item>A method with that name exists on the type (any access level); else
    /// <see cref="CustomMethodCheck.MethodNotFound"/> (Req 7.3).</item>
    /// <item>The match is public and unambiguous: more than one public method with that name →
    /// <see cref="CustomMethodCheck.Ambiguous"/> (Req 7.5); the sole match is not public →
    /// <see cref="CustomMethodCheck.NotPublic"/> (Req 7.4).</item>
    /// </list>
    /// </remarks>
    public CustomMethodValidationResult ValidateCustomMethod(string typeName, string methodName)
    {
        // Check 1 — type exists (Req 7.2). The type name is echoed verbatim into the message.
        var type = ResolveType(typeName);
        if (type is null)
        {
            return new CustomMethodValidationResult(
                false,
                CustomMethodCheck.TypeNotFound,
                $"Type '{typeName}' not found in loaded assemblies.",
                null);
        }

        // Check 2 — a method with that name exists on the type (Req 7.3). Include non-public
        // members so a private/internal-only match still reaches the public-ness check below
        // (otherwise it would be misreported as "not found"). Exclude special-name members
        // (property accessors, operators) so they are not mistaken for invocable methods.
        var named = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal)
                        && !m.IsSpecialName)
            .ToList();

        if (named.Count == 0)
        {
            return new CustomMethodValidationResult(
                false,
                CustomMethodCheck.MethodNotFound,
                $"Method '{methodName}' not found on type '{typeName}'.",
                null);
        }

        // Check 3 — public and unambiguous (Req 7.4, 7.5).
        var publicMatches = named.Where(m => m.IsPublic).ToList();

        if (publicMatches.Count > 1)
        {
            return new CustomMethodValidationResult(
                false,
                CustomMethodCheck.Ambiguous,
                $"Method '{methodName}' on type '{typeName}' is ambiguous; more than one public method shares that name.",
                null);
        }

        if (publicMatches.Count == 0)
        {
            return new CustomMethodValidationResult(
                false,
                CustomMethodCheck.NotPublic,
                $"Method '{methodName}' on type '{typeName}' is not public; only public methods may be invoked.",
                null);
        }

        // Exactly one public match — valid. The descriptor's Job_Parameter list contains one entry
        // per declared parameter in declared order, excluding Injected_Parameters (Req 7.6, 6.8).
        var resolved = publicMatches[0];
        return new CustomMethodValidationResult(
            true,
            CustomMethodCheck.None,
            $"Method '{methodName}' on type '{typeName}' is valid.",
            DescribeInternal(resolved, DetermineKind(resolved)));
    }

    /// <summary>
    /// Reports the <see cref="QueueAttribute"/> applied to <paramref name="method"/> or its
    /// declaring class (Req 13.1).
    /// </summary>
    /// <remarks>
    /// The method's own attribute takes precedence over the declaring class's. The result reports
    /// <see cref="QueueAttributeInfo.IsPresent"/>, the <see cref="QueueAttributeInfo.QueueName"/>
    /// (which may be a format template such as <c>"{0}"</c>), and
    /// <see cref="QueueAttributeInfo.IsFormatTemplate"/> when the queue name contains a placeholder.
    /// </remarks>
    public QueueAttributeInfo GetQueueAttribute(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return GetQueueAttributeStatic(method);
    }

    private static QueueAttributeInfo GetQueueAttributeStatic(MethodInfo method)
    {
        var attr = method.GetCustomAttribute<QueueAttribute>(inherit: true)
                   ?? method.DeclaringType?.GetCustomAttribute<QueueAttribute>(inherit: true);

        if (attr is null)
            return new QueueAttributeInfo(false, null, false);

        var queueName = attr.Queue;
        var isTemplate = !string.IsNullOrEmpty(queueName) && queueName.Contains('{');
        return new QueueAttributeInfo(true, queueName, isTemplate);
    }

    // --- Internal helpers -------------------------------------------------------------------

    /// <summary>
    /// Resolves a type by full name (preferred) or simple name across the loaded assemblies,
    /// skipping assemblies that cannot be inspected (Req 5.7 resilience pattern).
    /// </summary>
    private static Type ResolveType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .ToList();

        return allTypes.FirstOrDefault(t => t.FullName == typeName)
               ?? allTypes.FirstOrDefault(t => t.Name == typeName);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partial results are still useful; drop the types that failed to load.
            return ex.Types.Where(t => t is not null)!;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// True when every Job_Parameter of <paramref name="method"/> accepts the corresponding
    /// supplied argument value. When no argument values are supplied, an overload is considered
    /// to accept only when it has zero Job_Parameters.
    /// </summary>
    private static bool OverloadAcceptsArgs(MethodInfo method, IReadOnlyList<JsonElement> args)
    {
        var jobParameters = JobParameters(method);
        var argCount = args?.Count ?? 0;

        if (jobParameters.Count != argCount)
            return false;

        for (var i = 0; i < jobParameters.Count; i++)
        {
            if (!ArgAccepts(args[i], jobParameters[i].ParameterType))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Best-effort check that a JSON argument value can be assigned to a declared parameter type.
    /// Used purely to disambiguate equal-arity overloads.
    /// </summary>
    private static bool ArgAccepts(JsonElement element, Type declaredType)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return IsNullableType(declaredType);

        try
        {
            // Round-trips through System.Text.Json: a value that deserializes to the declared
            // type is accepted. Default (strict) options reject e.g. "5" -> int or 5 -> string.
            _ = JsonSerializer.Deserialize(element.GetRawText(), declaredType);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The Job_Parameters of a method: declared parameters that are not Injected_Parameters.
    /// </summary>
    /// <remarks>
    /// Replicates <c>JobArgumentConverter.JobParameters</c> locally until task 2.1 lands; the
    /// converter will become the single source of truth.
    /// </remarks>
    private static IReadOnlyList<ParameterInfo> JobParameters(MethodInfo method)
        => method.GetParameters().Where(p => !IsInjectedParameter(p)).ToList();

    /// <summary>
    /// True for parameters Hangfire supplies at execution time: <see cref="PerformContext"/>,
    /// <see cref="System.Threading.CancellationToken"/>, or <see cref="IJobCancellationToken"/>.
    /// </summary>
    /// <remarks>Replicates <c>JobArgumentConverter.IsInjectedParameter</c> until task 2.1 lands.</remarks>
    private static bool IsInjectedParameter(ParameterInfo parameter)
    {
        var t = parameter.ParameterType;
        return t == typeof(PerformContext)
               || t == typeof(System.Threading.CancellationToken)
               || t == typeof(IJobCancellationToken);
    }

    /// <summary>
    /// True when the declared type can hold null without conversion: a reference type, or
    /// <see cref="Nullable{T}"/> for value types.
    /// </summary>
    /// <remarks>Replicates <c>JobArgumentConverter.IsNullableType</c> until task 2.1 lands.</remarks>
    private static bool IsNullableType(Type declaredType)
        => !declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) is not null;

    /// <summary>
    /// Computes the Display_Label for a method via <see cref="JobNameHelper"/>, falling back to
    /// the method name when no display name is available (Req 5.5, 5.10 spirit).
    /// </summary>
    private static string ComputeDisplayLabel(MethodInfo method)
    {
        try
        {
            // Job's constructor requires args length == parameter count; supply null slots so
            // any JobDisplayName format placeholders resolve without invoking the method.
            var args = new object[method.GetParameters().Length];
            var job = new Job(method.DeclaringType, method, args);
            var label = JobNameHelper.GetDisplayName(job, null);
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }
        catch
        {
            // Fall through to the method-name fallback below.
        }

        return method.Name;
    }
}
