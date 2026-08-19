using System.Globalization;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Records and queries audit-log entries for admin actions performed through the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Storage uses Hangfire's own key-value primitives so no schema changes are required:
/// </para>
/// <list type="bullet">
///   <item><description>Sorted set <c>audit:log</c> — entry ids keyed by score (UTC ticks).</description></item>
///   <item><description>Hash <c>audit:entry:{id}</c> — entry payload (action, user, target, reason, metadata JSON).</description></item>
/// </list>
/// <para>
/// Trimming runs on every <see cref="Log(string, string, string, IDictionary{string, string}, string)"/>
/// call: anything older than <see cref="AuditLogOptions.Retention"/> or beyond the
/// <see cref="AuditLogOptions.MaxEntries"/> ceiling is removed in the same transaction.
/// </para>
/// </remarks>
public class AuditLogService
{
    private const string SetKey = "audit:log";
    private const string EntryKeyPrefix = "audit:entry:";

    private readonly JobStorage _storage;
    private readonly DashboardUIOptions _options;
    private readonly IHttpContextAccessor _httpContext;
    private readonly AuditActorAccessor _actor;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        JobStorage storage,
        DashboardUIOptions options,
        IHttpContextAccessor httpContext,
        AuditActorAccessor actor = null,
        ILogger<AuditLogService> logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpContext = httpContext;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>Records a new audit entry. No-op if <see cref="AuditLogOptions.Enabled"/> is false.</summary>
    /// <param name="action">One of <see cref="AuditAction"/> constants (or a custom key).</param>
    /// <param name="target">Target identifier (job id, queue name, recurring id) or null for global actions.</param>
    /// <param name="reason">Optional human-readable reason.</param>
    /// <param name="metadata">Optional small metadata bag.</param>
    /// <param name="actor">
    /// Explicit actor override. When supplied (non-null), it takes precedence over the ambient
    /// circuit/HTTP identity. Use when the caller already knows the acting user.
    /// </param>
    public void Log(string action, string target = null, string reason = null,
        IDictionary<string, string> metadata = null, string actor = null)
    {
        if (string.IsNullOrEmpty(action)) throw new ArgumentNullException(nameof(action));
        if (!_options.AuditLog.Enabled) return;

        try
        {
            var ts = DateTime.UtcNow;
            var id = ts.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            var (user, ip) = ResolveActor(actor);

            var hash = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timestamp"] = ts.ToString("O", CultureInfo.InvariantCulture),
                ["action"] = action,
                ["user"] = Truncate(user, 256),
                ["clientIp"] = Truncate(ip, 64),
            };

            if (!string.IsNullOrEmpty(target)) hash["target"] = Truncate(target, 512);
            if (!string.IsNullOrEmpty(reason)) hash["reason"] = Truncate(reason, 1024);
            if (metadata is { Count: > 0 })
            {
                var json = JsonSerializer.Serialize(metadata);
                hash["metadata"] = Truncate(json, 4096);
            }

            using var connection = _storage.GetConnection();
            using var tx = connection.CreateWriteTransaction();
            tx.AddToSet(SetKey, id, ts.Ticks);
            tx.SetRangeInHash(EntryKeyPrefix + id, hash);
            tx.Commit();

            // Best-effort trimming. Don't run on every call — only every ~50 writes (cheap heuristic).
            if (Random.Shared.Next(50) == 0)
            {
                TrimAsync();
            }
        }
        catch (Exception ex)
        {
            // Never let audit failure break the user action.
            _logger?.LogWarning(ex, "AuditLogService.Log failed for action {Action}", action);
        }
    }

    /// <summary>Returns a page of audit entries newest-first, applying optional filters.</summary>
    public IReadOnlyList<AuditLogEntry> Query(AuditLogFilter filter, int from, int count)
    {
        if (count <= 0) return Array.Empty<AuditLogEntry>();
        if (!_options.AuditLog.Enabled) return Array.Empty<AuditLogEntry>();

        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            if (connection is not JobStorageConnection storageConnection) return [];

            // Fetch the entry ids and scan the newest-first slice. Pulling ids (cheap strings) is
            // cheap; the expensive part — GetAllEntriesFromHash per entry — is bounded below by the
            // `results.Count >= count` break and the `maxScan` cap, so a large set never triggers
            // thousands of hash round-trips on a single page request.
            var maxScan = Math.Max((from + count) * 4, 200);
            // NOTE: endingAt must stay below int.MaxValue. SQL Server / PostgreSQL providers compute
            // "@endingAt + 1" internally, so passing int.MaxValue overflows to a negative bound and
            // returns ZERO rows (the audit page would always look empty on those storages, even
            // though InMemory tolerates it). Use a large-but-safe upper bound instead.
            const int maxIndex = int.MaxValue - 1;
            var ids = storageConnection.GetRangeFromSet(SetKey, 0, maxIndex);
            // Hangfire doesn't expose ZREVRANGE; we sort by id descending (id begins with ticks).
            var sortedIds = ids
                .OrderByDescending(s => s, StringComparer.Ordinal)
                .Take(maxScan)
                .ToList();

            var results = new List<AuditLogEntry>();
            var skipped = 0;
            foreach (var id in sortedIds)
            {
                if (results.Count >= count) break;

                var hash = storageConnection.GetAllEntriesFromHash(EntryKeyPrefix + id);
                if (hash is null || hash.Count == 0) continue;
                if (hash.ContainsKey("_deleted")) continue;

                var entry = HashToEntry(id, hash);
                if (!Matches(entry, filter)) continue;

                if (skipped < from) { skipped++; continue; }
                results.Add(entry);
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AuditLogService.Query failed");
            return Array.Empty<AuditLogEntry>();
        }
    }

    /// <summary>Returns the total number of stored audit entries (no filter applied).</summary>
    public long GetTotalCount()
    {
        if (!_options.AuditLog.Enabled) return 0;
        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            if (connection is JobStorageConnection storageConnection)
                return SetCounting.Count(storageConnection, SetKey);
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns a page of audit entries (newest-first) together with the total number of entries that
    /// match <paramref name="filter"/>, so the UI can render a numbered pager. Unlike
    /// <see cref="Query(AuditLogFilter, int, int)"/> this evaluates the full filtered set to produce
    /// an exact total; the work is bounded by <see cref="AuditLogOptions.MaxEntries"/> and the page
    /// is admin-only, so a single pass over the (capped) set is acceptable.
    /// </summary>
    public AuditLogPage QueryPage(AuditLogFilter filter, int from, int count)
    {
        if (count <= 0 || !_options.AuditLog.Enabled)
            return new AuditLogPage(Array.Empty<AuditLogEntry>(), 0);

        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            if (connection is not JobStorageConnection storageConnection)
                return new AuditLogPage(Array.Empty<AuditLogEntry>(), 0);

            // See Query(): int.MaxValue overflows the SQL providers' "@endingAt + 1".
            const int maxIndex = int.MaxValue - 1;
            var ids = storageConnection.GetRangeFromSet(SetKey, 0, maxIndex);

            // Newest-first (ids begin with zero-padded ticks, so an ordinal sort is chronological).
            var sortedIds = ids.OrderByDescending(s => s, StringComparer.Ordinal);

            var matched = new List<AuditLogEntry>();
            foreach (var id in sortedIds)
            {
                var hash = storageConnection.GetAllEntriesFromHash(EntryKeyPrefix + id);
                if (hash is null || hash.Count == 0) continue;
                if (hash.ContainsKey("_deleted")) continue;

                var entry = HashToEntry(id, hash);
                if (!Matches(entry, filter)) continue;
                matched.Add(entry);
            }

            var page = from >= matched.Count
                ? (IReadOnlyList<AuditLogEntry>)Array.Empty<AuditLogEntry>()
                : matched.GetRange(from, Math.Min(count, matched.Count - from));

            return new AuditLogPage(page, matched.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AuditLogService.QueryPage failed");
            return new AuditLogPage(Array.Empty<AuditLogEntry>(), 0);
        }
    }

    /// <summary>Manually trims old entries beyond retention or max-entries cap.</summary>
    public void TrimAsync()
    {
        try
        {
            using var connection = _storage.GetConnection();
            if (connection is not JobStorageConnection storageConnection) return;

            var total = SetCounting.Count(storageConnection, SetKey);
            var cutoffTicks = (DateTime.UtcNow - _options.AuditLog.Retention).Ticks;
            // See note in Query(): int.MaxValue overflows the SQL providers' "@endingAt + 1".
            var ids = storageConnection.GetRangeFromSet(SetKey, 0, int.MaxValue - 1);

            // Find ids to remove: too old OR over the count cap (keep newest `MaxEntries`).
            var sorted = ids.OrderBy(s => s, StringComparer.Ordinal).ToList(); // ascending = oldest first
            var toRemove = new HashSet<string>(StringComparer.Ordinal);

            // Age-based removal
            foreach (var id in sorted)
            {
                if (TryParseTicks(id, out var ticks) && ticks < cutoffTicks)
                    toRemove.Add(id);
                else
                    break; // sorted ascending; once one passes, the rest are newer
            }

            // Count-based removal: drop the oldest entries beyond the cap. Membership tests use a
            // HashSet so this stays O(n) rather than O(n²).
            if (total > _options.AuditLog.MaxEntries)
            {
                var excess = (int)(total - _options.AuditLog.MaxEntries);
                foreach (var id in sorted)
                {
                    if (toRemove.Count >= excess) break;
                    toRemove.Add(id);
                }
            }

            if (toRemove.Count == 0) return;

            using var tx = connection.CreateWriteTransaction();
            foreach (var id in toRemove)
            {
                tx.RemoveFromSet(SetKey, id);
                // Tombstone the hash with a single field. Hangfire's IWriteOnlyTransaction does
                // not expose a generic DeleteHash; the storage GC reclaims the keys eventually
                // and the entry no longer surfaces in queries because the set entry is gone.
                tx.SetRangeInHash(EntryKeyPrefix + id, [new KeyValuePair<string, string>("_deleted", "1")]);
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "AuditLogService trim failed (best-effort)");
        }
    }

    // ---- Helpers ----

    private (string user, string ip) ResolveActor(string explicitActor = null)
    {
        // 1. Explicit override wins.
        if (!string.IsNullOrWhiteSpace(explicitActor))
            return (explicitActor, _actor?.ClientIp ?? string.Empty);

        // 2. Per-circuit accessor (the normal path for Blazor interactive actions, where
        //    HttpContext is null). Populated by the dashboard layout from AuthenticationStateProvider.
        if (_actor is { HasActor: true })
            return (_actor.User, _actor.ClientIp ?? string.Empty);

        // 3. HttpContext fallback (classic request path, e.g. health endpoint or non-Blazor hosts).
        var ctx = _httpContext?.HttpContext;
        if (ctx is null)
        {
            // No identity available anywhere — attribute to the system actor with the circuit IP if known.
            return ("(system)", _actor?.ClientIp ?? string.Empty);
        }

        string user = null;
        if (ctx.User?.Identity?.IsAuthenticated == true)
        {
            user = ctx.User.Identity.Name;
            if (string.IsNullOrEmpty(user))
            {
                user = ctx.User.FindFirst("preferred_username")?.Value
                    ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                    ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            }
        }
        if (string.IsNullOrEmpty(user))
        {
            // Fallback for unauthenticated dashboards (e.g., LocalRequestsOnly default).
            user = "(anonymous)";
        }

        var ip = ctx.Connection?.RemoteIpAddress?.ToString() ?? string.Empty;
        return (user, ip);
    }

    private static AuditLogEntry HashToEntry(string id, Dictionary<string, string> hash)
    {
        Dictionary<string, string> metadata = null;
        if (hash.TryGetValue("metadata", out var metaJson) && !string.IsNullOrEmpty(metaJson))
        {
            try { metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson); }
            catch { metadata = null; }
        }

        DateTime ts = default;
        if (hash.TryGetValue("timestamp", out var tsRaw))
        {
            // Timestamps are persisted with the "O" (round-trip) format, which already carries the
            // UTC offset. RoundtripKind must NOT be combined with AssumeUniversal/AdjustToUniversal
            // (that throws ArgumentException), so parse with RoundtripKind alone and normalize.
            if (DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                ts = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            }
        }

        return new AuditLogEntry
        {
            Id = id,
            TimestampUtc = ts,
            Action = hash.GetValueOrDefault("action") ?? string.Empty,
            User = hash.GetValueOrDefault("user") ?? string.Empty,
            ClientIp = hash.GetValueOrDefault("clientIp") ?? string.Empty,
            Target = hash.GetValueOrDefault("target"),
            Reason = hash.GetValueOrDefault("reason"),
            Metadata = metadata,
        };
    }

    private static bool Matches(AuditLogEntry e, AuditLogFilter filter)
    {
        if (filter is null) return true;
        if (filter.FromUtc.HasValue && e.TimestampUtc < filter.FromUtc.Value) return false;
        if (filter.ToUtc.HasValue && e.TimestampUtc > filter.ToUtc.Value) return false;
        if (!string.IsNullOrEmpty(filter.ActionPrefix) &&
            !e.Action.StartsWith(filter.ActionPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(filter.User) &&
            (e.User?.IndexOf(filter.User, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
            return false;
        if (!string.IsNullOrEmpty(filter.Target) &&
            (e.Target?.IndexOf(filter.Target, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
            return false;
        return true;
    }

    private static bool TryParseTicks(string id, out long ticks)
    {
        // ids look like "{19-digit ticks}-{8 hex chars}". Parse the prefix.
        ticks = 0;
        if (string.IsNullOrEmpty(id) || id.Length < 19) return false;
        return long.TryParse(id.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
