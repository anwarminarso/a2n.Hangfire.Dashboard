namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Per-circuit holder for the identity of the user performing dashboard actions.
/// </summary>
/// <remarks>
/// <para>
/// Dashboard actions (pause, requeue, delete, recurring CRUD, maintenance) run inside a Blazor
/// Server interactive circuit — frequently off the request thread via <c>Task.Run</c> — where
/// <c>IHttpContextAccessor.HttpContext</c> is <see langword="null"/>. Relying on it would attribute
/// every audit entry to "(system)".
/// </para>
/// <para>
/// Instead, a root component (the dashboard layout) resolves the user from
/// <c>AuthenticationStateProvider</c> once per circuit and stores it here. <c>AuditLogService</c>
/// reads it synchronously when writing an entry. The accessor is registered <b>scoped</b>, so each
/// circuit (and each classic HTTP request) gets its own instance.
/// </para>
/// </remarks>
public sealed class AuditActorAccessor
{
    /// <summary>The resolved user name for the current circuit, or null if not yet set.</summary>
    public string User { get; private set; }

    /// <summary>The originating client IP for the current circuit, or null if unknown.</summary>
    public string ClientIp { get; private set; }

    /// <summary>True once <see cref="Set"/> has been called for this circuit.</summary>
    public bool HasActor => !string.IsNullOrEmpty(User);

    /// <summary>Records the actor for the current circuit. Safe to call repeatedly (e.g., on reconnect).</summary>
    public void Set(string user, string clientIp)
    {
        if (!string.IsNullOrWhiteSpace(user)) User = user;
        if (!string.IsNullOrWhiteSpace(clientIp)) ClientIp = clientIp;
    }
}
