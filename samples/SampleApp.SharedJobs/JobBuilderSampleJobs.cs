using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Tags.Attributes;

namespace SampleApp.SharedJobs;

/// <summary>
/// A catalog of jobs purpose-built to exercise the <b>Job Builder</b> (Create Recurring Job /
/// Enqueue Job) end-to-end. Every public method here is automatically <i>discovered</i> as a
/// Registered_Method because the class is decorated with <see cref="TagAttribute"/> (a recognized
/// discovery attribute), so they all appear in the Job Builder's "Registered method" dropdown
/// without any seeding.
/// </summary>
/// <remarks>
/// The set is designed so each method demonstrates a different facet of the builder:
/// <list type="bullet">
///   <item>Every <c>ParameterInputKind</c>: text, integer, float, date/time/datetime, GUID,
///   bool / nullable-bool, single-enum / flags-enum, scalar arrays, nested objects, and the JSON
///   fallback for unsupported types.</item>
///   <item>Hangfire <c>Injected_Parameter</c>s (<see cref="PerformContext"/>,
///   <see cref="CancellationToken"/>) — these never appear as inputs; the builder fills their
///   <c>Args</c> slots with <c>null</c> and Hangfire supplies them at run time.</item>
///   <item>Method overloads (<see cref="Notify(string)"/> / <see cref="Notify(string, int)"/>) to
///   show overload-safe resolution by argument count.</item>
///   <item><see cref="QueueAttribute"/> precedence — <see cref="CriticalCleanup"/> forces a queue,
///   so the builder renders the queue control read-only with a precedence notice.</item>
/// </list>
/// Method bodies are intentionally trivial (a short sleep, optional console output) and tolerate any
/// supplied values — including blanks resolved to <c>null</c>/<c>default(T)</c> — so a constructed
/// job always runs without throwing.
/// </remarks>
[Tag("job-builder")]
public class JobBuilderSampleJobs
{
    // === Text (string) + an injected PerformContext (excluded from the form) =================

    /// <summary>Three text inputs. <paramref name="context"/> is Hangfire-injected, so the builder
    /// shows only <c>to</c>, <c>subject</c>, and <c>body</c>.</summary>
    [JobDisplayName("Send Email")]
    public void SendEmail(string to, string subject, string body, PerformContext context)
    {
        context?.WriteLine($"Sending email to '{to}' — subject: '{subject}'");
        if (!string.IsNullOrEmpty(body)) context?.WriteLine($"Body: {body}");
        Thread.Sleep(300);
    }

    // === Integer + bool + nullable integer ===================================================

    /// <summary>Integer inputs (whole-number, range-checked), a checkbox, and a nullable integer
    /// (blank → <c>null</c>).</summary>
    [JobDisplayName("Generate Monthly Report")]
    public void GenerateReport(int month, int year, bool includeCharts, int? topN)
    {
        Thread.Sleep(300);
    }

    // === Floating-point types ================================================================

    /// <summary>Floating-point inputs (decimal / double / float).</summary>
    [JobDisplayName("Apply Discount")]
    public void ApplyDiscount(decimal amount, double percentage, float taxRate)
    {
        Thread.Sleep(200);
    }

    // === GUID + date-only / time-only / date-and-time ========================================

    /// <summary>A GUID text input, a date picker, a time picker, and a combined date-and-time
    /// picker.</summary>
    [JobDisplayName("Schedule Reminder")]
    public void ScheduleReminder(Guid reminderId, DateOnly date, TimeOnly time, DateTime fullTimestamp)
    {
        Thread.Sleep(200);
    }

    // === Bool + tri-state nullable bool ======================================================

    /// <summary>A checkbox plus a tri-state nullable bool (True / False / unset → <c>null</c>).</summary>
    [JobDisplayName("Toggle Feature Flag")]
    public void ToggleFeature(string feature, bool enabled, bool? forceOverride)
    {
        Thread.Sleep(150);
    }

    // === Single-select enum ==================================================================

    /// <summary>A single-select dropdown over the <see cref="PriorityLevel"/> members.</summary>
    [JobDisplayName("Set Task Priority")]
    public void SetPriority(string taskId, PriorityLevel priority)
    {
        Thread.Sleep(150);
    }

    // === [Flags] enum (multi-select) =========================================================

    /// <summary>A multi-select control over the <see cref="NotificationChannels"/> flags.</summary>
    [JobDisplayName("Send Notification")]
    public void SendNotification(string recipient, NotificationChannels channels)
    {
        Thread.Sleep(150);
    }

    // === Scalar arrays =======================================================================

    /// <summary>Add/remove list editors for an <see cref="int"/>[] and a <see cref="string"/>[].</summary>
    [JobDisplayName("Process Batch")]
    public void ProcessBatch(int[] orderIds, string[] tags, PerformContext context)
    {
        context?.WriteLine($"Processing {orderIds?.Length ?? 0} order(s), {tags?.Length ?? 0} tag(s).");
        Thread.Sleep(200);
    }

    // === Nested object (sub-form, built on demand) ===========================================

    /// <summary>A nested-object editor (collapsed until "Create" is clicked) plus a checkbox.
    /// <see cref="OrderInfo"/> itself contains a further nested <see cref="AddressInfo"/>.</summary>
    [JobDisplayName("Create Order")]
    public void CreateOrder(OrderInfo order, bool sendConfirmation, PerformContext context)
    {
        context?.WriteLine(order is null
            ? "Creating order with no details (null)."
            : $"Creating order for '{order.CustomerName}', qty {order.Quantity}.");
        Thread.Sleep(250);
    }

    // === JSON fallback (unsupported type) ====================================================

    /// <summary><see cref="TimeSpan"/> is not one of the mapped scalar controls, so the builder
    /// falls back to a raw JSON input for <paramref name="delay"/> (e.g. <c>"00:05:00"</c>).</summary>
    [JobDisplayName("Delay And Run")]
    public void DelayAndRun(string label, TimeSpan delay)
    {
        Thread.Sleep(150);
    }

    // === Overloads (overload-safe resolution by argument count) ==============================

    /// <summary>Single-argument overload of <c>Notify</c>.</summary>
    [JobDisplayName("Notify (message only)")]
    public void Notify(string message)
    {
        Thread.Sleep(100);
    }

    /// <summary>Two-argument overload of <c>Notify</c> — the resolver selects this one when two
    /// argument values are supplied.</summary>
    [JobDisplayName("Notify (message + retries)")]
    public void Notify(string message, int retries)
    {
        Thread.Sleep(100);
    }

    // === QueueAttribute precedence ===========================================================

    /// <summary>Forces the <c>critical</c> queue via <see cref="QueueAttribute"/>. The builder shows
    /// the queue control read-only with a precedence notice, since the attribute wins at run time.</summary>
    [Queue("critical")]
    [JobDisplayName("Critical Cleanup")]
    public void CriticalCleanup(string scope, bool dryRun)
    {
        Thread.Sleep(200);
    }

    // === No parameters =======================================================================

    /// <summary>A parameter-less job — the parameter form shows "this method takes no parameters"
    /// and the Parameter_JSON is the empty array <c>[]</c>.</summary>
    [JobDisplayName("Heartbeat")]
    public void Heartbeat(PerformContext context)
    {
        context?.WriteLine("Heartbeat OK.");
        Thread.Sleep(100);
    }

    // === Dictionary<string, string> (collection type — Issue #39 repro) =====================

    /// <summary>
    /// Issue #39 repro: a <see cref="Dictionary{TKey, TValue}"/> argument (<paramref name="parameters"/>)
    /// alongside several scalar/optional parameters, mirroring the reported signature. Opening this
    /// job in the recurring editor and toggling JSON → Form → JSON must round-trip
    /// <paramref name="parameters"/> unchanged instead of losing it (the bug: the dictionary was
    /// mapped to a NestedObject sub-form, whose reflection-based property walk finds no dictionary
    /// entries, so switching to Form view silently dropped the data).
    /// </summary>
    [JobDisplayName("Send Report Email")]
    public Task SendReportEmailAsync(
        PerformContext context,
        int reportId,
        string database,
        string storedProcedure,
        string subject,
        string fileName,
        string format,
        string message,
        Dictionary<string, string> parameters = null,
        string reportName = null,
        bool skipWhenNoRows = true,
        int? commandTimeout = null,
        string csvCulture = null,
        string archiveFilePath = null,
        CancellationToken cancellationToken = default)
    {
        context?.WriteLine($"Report {reportId} ('{reportName ?? storedProcedure}') — {parameters?.Count ?? 0} parameter(s).");
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                context?.WriteLine($"  {key} = {value}");
            }
        }

        return Task.Delay(150, cancellationToken);
    }

    // === Cancellation token (injected, excluded from the form) ===============================

    /// <summary>One text input; the <see cref="CancellationToken"/> is Hangfire-injected and not
    /// shown in the form.</summary>
    [JobDisplayName("Cancellable Sync")]
    public void CancellableSync(string source, CancellationToken token)
    {
        for (var i = 0; i < 5 && !token.IsCancellationRequested; i++)
        {
            Thread.Sleep(100);
        }
    }
}

/// <summary>Priority levels used to demonstrate a single-select enum input in the Job Builder.</summary>
public enum PriorityLevel
{
    Low,
    Normal,
    High,
    Critical,
}

/// <summary>Notification channels — a <c>[Flags]</c> enum demonstrating a multi-select input.</summary>
[Flags]
public enum NotificationChannels
{
    None = 0,
    Email = 1,
    Sms = 2,
    Push = 4,
    Webhook = 8,
}

/// <summary>A nested object demonstrating the Job Builder's on-demand sub-form (depth 1), which in
/// turn nests <see cref="AddressInfo"/> (depth 2).</summary>
public class OrderInfo
{
    public string CustomerName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public bool Expedited { get; set; }
    public AddressInfo ShippingAddress { get; set; }
}

/// <summary>A second-level nested object used by <see cref="OrderInfo.ShippingAddress"/>.</summary>
public class AddressInfo
{
    public string Street { get; set; }
    public string City { get; set; }
    public string PostalCode { get; set; }
}
