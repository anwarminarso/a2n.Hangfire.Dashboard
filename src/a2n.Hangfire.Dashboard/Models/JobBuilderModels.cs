using System.Reflection;
using System.Text.Json;

namespace a2n.Hangfire.Dashboard;

// Shared contract for the Job Builder feature. These records and enums are the common
// vocabulary used by the argument converter, the method resolver, the Blazor components,
// and the recurring/enqueue service paths. See .kiro/specs/job-builder/design.md ("Data Models").

/// <summary>
/// A discovered or resolved job method (Req 5, 6, 7).
/// </summary>
/// <param name="TypeFullName">Full type name (namespace + type) declaring the method.</param>
/// <param name="MethodName">The method's name.</param>
/// <param name="DisplayLabel">Human-readable label produced by the display-name extraction logic.</param>
/// <param name="JobParameters">Operator-supplied parameters; excludes Injected_Parameters.</param>
/// <param name="Queue">QueueAttribute reporting for the method or its declaring class.</param>
public sealed record JobMethodDescriptor(
    string TypeFullName,
    string MethodName,
    string DisplayLabel,
    IReadOnlyList<JobParameterDescriptor> JobParameters,
    QueueAttributeInfo Queue);

/// <summary>
/// One operator-supplied parameter (Req 6.4, 8).
/// </summary>
/// <param name="Name">Declared parameter name.</param>
/// <param name="DeclaredType">The parameter's declared CLR type.</param>
/// <param name="InputKind">The input control kind the form should render for this parameter.</param>
/// <param name="IsRequired">Whether the parameter requires a value.</param>
/// <param name="IsNullable">
/// True when the declared type is a reference type or <see cref="System.Nullable{T}"/>; drives
/// whether an empty value resolves to <c>null</c> versus <c>default(T)</c>.
/// </param>
/// <param name="Position">Position among ALL declared parameters (including injected ones).</param>
public sealed record JobParameterDescriptor(
    string Name,
    Type DeclaredType,
    ParameterInputKind InputKind,
    bool IsRequired,
    bool IsNullable,
    int Position);

/// <summary>
/// QueueAttribute reporting (Req 13.1, 13.4).
/// </summary>
/// <param name="IsPresent">Whether a QueueAttribute applies to the method or its declaring class.</param>
/// <param name="QueueName">The queue value; may be a format template (e.g. "{0}").</param>
/// <param name="IsFormatTemplate">Whether <paramref name="QueueName"/> is a format template.</param>
public sealed record QueueAttributeInfo(
    bool IsPresent,
    string QueueName,
    bool IsFormatTemplate);

/// <summary>
/// The input control kind to render for a Job_Parameter, driving both rendering and validation
/// (Req 8.1–8.12).
/// </summary>
public enum ParameterInputKind
{
    Text,
    Integer,
    Float,
    Date,
    Time,
    DateTime,
    Guid,
    Bool,
    NullableBool,
    EnumSingle,
    EnumFlags,
    ScalarArray,
    NestedObject,
    Json,
}

/// <summary>
/// Method resolution outcome (Req 1.5–1.8).
/// </summary>
/// <param name="Success">Whether a single matching overload was resolved.</param>
/// <param name="Method">The resolved method when <paramref name="Success"/> is true; otherwise null.</param>
/// <param name="Error">An error message identifying the failure when resolution fails.</param>
/// <param name="ErrorKind">The category of resolution failure, or null on success.</param>
public sealed record MethodResolutionResult(
    bool Success,
    MethodInfo Method,
    string Error,
    MethodResolutionError? ErrorKind);

/// <summary>
/// Categories of method-resolution failure (Req 1.5–1.8).
/// </summary>
public enum MethodResolutionError
{
    TypeNotFound,
    MethodNotFound,
    NoMatchingOverload,
    AmbiguousOverload,
}

/// <summary>
/// Custom-method validation outcome (Req 7).
/// </summary>
/// <param name="IsValid">Whether all ordered checks passed.</param>
/// <param name="FailedCheck">The first failed check, or <see cref="CustomMethodCheck.None"/> when valid.</param>
/// <param name="Message">An operator-facing message describing the outcome.</param>
/// <param name="Descriptor">The resolved descriptor when valid; otherwise null.</param>
public sealed record CustomMethodValidationResult(
    bool IsValid,
    CustomMethodCheck FailedCheck,
    string Message,
    JobMethodDescriptor Descriptor);

/// <summary>
/// The ordered custom-method validation checks (Req 7).
/// </summary>
public enum CustomMethodCheck
{
    None,
    TypeNotFound,
    MethodNotFound,
    NotPublic,
    Ambiguous,
}

/// <summary>
/// Parameter JSON validation outcome (Req 2.2–2.7, 9.6).
/// </summary>
/// <param name="Status">The validation result category.</param>
/// <param name="ExpectedCount">Expected element count (used by <see cref="ParameterJsonStatus.CountMismatch"/>).</param>
/// <param name="ActualCount">Actual element count (used by <see cref="ParameterJsonStatus.CountMismatch"/>).</param>
/// <param name="ParameterName">Offending parameter name (used by <see cref="ParameterJsonStatus.ElementTypeError"/>).</param>
/// <param name="ExpectedType">Expected type (used by <see cref="ParameterJsonStatus.ElementTypeError"/>).</param>
public sealed record ParameterJsonValidation(
    ParameterJsonStatus Status,
    int ExpectedCount,
    int ActualCount,
    string ParameterName,
    string ExpectedType);

/// <summary>
/// Parameter JSON validation status (Req 2.2–2.7).
/// </summary>
public enum ParameterJsonStatus
{
    Valid,
    Malformed,
    NotArray,
    CountMismatch,
    ElementTypeError,
}

/// <summary>
/// Args build outcome (Req 1.1–1.4).
/// </summary>
/// <param name="Success">Whether the positional Args array was built successfully.</param>
/// <param name="Args">The positional Hangfire Args array on success; otherwise null.</param>
/// <param name="ParameterName">Offending parameter name on conversion failure.</param>
/// <param name="ExpectedType">Expected type on conversion failure.</param>
/// <param name="Error">An error message describing a conversion failure.</param>
public sealed record ArgsBuildResult(
    bool Success,
    object[] Args,
    string ParameterName,
    string ExpectedType,
    string Error);

/// <summary>
/// Request to create or update a recurring job (Req 11).
/// </summary>
public sealed record RecurringJobRequest(
    string JobId,
    string TypeName,
    string MethodName,
    string ParameterJson,
    string Cron,
    string Queue,
    string TimeZoneId,
    bool IsCustomMethod);

/// <summary>
/// Request to enqueue a one-off job (Req 12).
/// </summary>
public sealed record EnqueueJobRequest(
    string TypeName,
    string MethodName,
    string ParameterJson,
    string Queue,
    bool IsCustomMethod);

/// <summary>
/// Result of a recurring create/update or enqueue operation (Req 11, 12).
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="JobId">The job identifier (recurring id, or the enqueued job's id) on success.</param>
/// <param name="Error">An error message identifying the reason on failure.</param>
/// <param name="FailedField">The field that failed validation, when applicable.</param>
public sealed record JobOperationResult(
    bool Success,
    string JobId,
    string Error,
    string FailedField);

/// <summary>
/// Cron builder field model (Req 10.2). Each field captures how the corresponding cron field
/// was configured in the Cron Builder controls, including the concrete value(s) selected.
/// </summary>
public sealed record CronFields(
    CronFieldSpec Minute,
    CronFieldSpec Hour,
    CronFieldSpec DayOfMonth,
    CronFieldSpec Month,
    CronFieldSpec DayOfWeek);

/// <summary>
/// A single cron field as configured in the Cron Builder (Req 10.2): the <see cref="CronFieldMode"/>
/// plus the concrete value(s) that mode requires. Unused values for the active mode are ignored
/// (e.g. <see cref="Value"/> is meaningful only for <see cref="CronFieldMode.Specific"/>).
/// </summary>
/// <param name="Mode">How the field is expressed.</param>
/// <param name="Value">The selected value for <see cref="CronFieldMode.Specific"/>.</param>
/// <param name="RangeStart">The inclusive lower bound for <see cref="CronFieldMode.Range"/>.</param>
/// <param name="RangeEnd">The inclusive upper bound for <see cref="CronFieldMode.Range"/>.</param>
/// <param name="Step">The interval for <see cref="CronFieldMode.Step"/> (<c>*/n</c>); coerced to at least 1.</param>
public sealed record CronFieldSpec(
    CronFieldMode Mode,
    int Value = 0,
    int RangeStart = 0,
    int RangeEnd = 0,
    int Step = 1)
{
    /// <summary>A field that matches every value (<c>*</c>).</summary>
    public static CronFieldSpec Every { get; } = new(CronFieldMode.Every);
}

/// <summary>
/// How a single cron field is expressed by the Cron Builder (Req 10.2).
/// </summary>
public enum CronFieldMode
{
    /// <summary>Match every value for the field (<c>*</c>).</summary>
    Every,

    /// <summary>Match a single specific value.</summary>
    Specific,

    /// <summary>Match an inclusive range of values (<c>a-b</c>).</summary>
    Range,

    /// <summary>Match values at a fixed step/interval (<c>*/n</c>).</summary>
    Step,
}

/// <summary>
/// The operating mode of the <c>JobBuilder</c> composite component.
/// </summary>
public enum JobBuilderMode
{
    /// <summary>Construct a recurring job: shows the schedule, queue, and time-zone controls (Req 11).</summary>
    Recurring,

    /// <summary>Enqueue a one-off job: hides the schedule and requires no cron (Req 12).</summary>
    Enqueue,
}
