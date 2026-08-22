using System.Globalization;
using System.Text.Json;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Reads concurrency-throttling primitives (semaphores, mutexes, and rate-limit windows) from
/// Hangfire storage, in the layout written by the Hangfire.Throttling package.
///
/// <para>
/// Key patterns (the writer lowercases ids):
/// <code>
/// sync:set:sm          set of registered semaphore ids
/// sync:sm:{id}         hash: "max" = limit, "d" = description
/// sync:j:sm:{id}       set of job ids currently holding a slot
/// sync:set:mx          set of "{mutexId}/{jobId}" pairs for currently held mutexes
/// sync:mx:{id}         set of job ids currently holding the mutex
/// sync:set:fw|sw|dp    sets of fixed / sliding / dynamic window ids
/// sync:fw|sw|dp:{id}   hash: "obj" = window state JSON, "d" = description
/// </code>
/// </para>
///
/// <para>
/// Occupancy is the cardinality of the holder set — no counter or hash field tracks used slots, so
/// removing a job id from <c>sync:j:sm:{id}</c> frees the slot completely.
/// </para>
///
/// <para>
/// The layout was established by runtime observation of Hangfire.Throttling 1.4.3 against SQL
/// Server, Redis (Hangfire.Pro.Redis.SEv2 3.3.2) and in-memory storage: registrations made through
/// the public <c>ThrottlingManager</c> API and jobs driven through each window attribute, then read
/// back. All three produce identical keys and identical state payloads despite very different
/// physical representations (relational rows, prefixed Redis sorted-sets and hashes, in-process
/// dictionaries), so the layout is a property of the package rather than of any storage provider.
/// </para>
/// </summary>
public class ThrottlingDataReader
{
    private static readonly (string Type, string SetKey, string KeyPrefix)[] WindowKinds =
    [
        (ThrottleWindowTypes.Fixed, "sync:set:fw", "sync:fw:"),
        (ThrottleWindowTypes.Sliding, "sync:set:sw", "sync:sw:"),
        (ThrottleWindowTypes.Dynamic, "sync:set:dp", "sync:dp:"),
    ];

    /// <summary>Matches <c>Processing.razor</c>, which waits this long before calling a server aborted.</summary>
    private static readonly TimeSpan ServerAbortedThreshold = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a job reaches a final state its slot may still legitimately appear held.
    /// The release and the state transition are separate storage writes, so a job that finished
    /// moments ago is not evidence of a leak.
    /// </summary>
    private static readonly TimeSpan FinalStateGrace = TimeSpan.FromMinutes(1);

    private readonly JobStorage _storage;
    private readonly TimeSpan _serverHeartbeatTolerance;

    public ThrottlingDataReader(JobStorage storage, DashboardUIOptions options = null)
    {
        _storage = storage;

        // Detaching is destructive, so the "server offline" flag that invites it takes its tolerance
        // from the health check rather than inventing a stricter one, and never drops below the
        // 5 minutes the Processing page waits before it calls a job's server aborted. Flagging a
        // live server would invite freeing a slot from a running job, letting the semaphore admit
        // past its limit — the one thing it exists to prevent. With the default 60s tolerance the
        // floor is what applies; a deployment that raises the tolerance raises this with it.
        var configured = options?.HealthCheckThresholds?.ServerHeartbeatTolerance ?? ServerAbortedThreshold;
        _serverHeartbeatTolerance = configured > ServerAbortedThreshold ? configured : ServerAbortedThreshold;
    }

    /// <summary>
    /// Returns true when the storage contains any throttling data, i.e. the host
    /// application uses Hangfire.Throttling.
    /// </summary>
    public virtual bool HasThrottlingData()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return false;

        // GetSetCount is virtual on JobStorageConnection and throws NotSupportedException unless the
        // storage overrides it. This runs from NavMenu, so an unhandled throw would take down the
        // whole dashboard shell rather than just hiding one nav item.
        try
        {
            if (storageConnection.GetSetCount("sync:set:sm") > 0 || storageConnection.GetSetCount("sync:set:mx") > 0)
                return true;

            foreach (var (_, setKey, _) in WindowKinds)
            {
                if (storageConnection.GetSetCount(setKey) > 0)
                    return true;
            }
        }
        catch (NotSupportedException)
        {
            // Storage cannot count sets — fall back to listing, which every storage implements.
            try
            {
                if (storageConnection.GetAllItemsFromSet("sync:set:sm")?.Count > 0
                    || storageConnection.GetAllItemsFromSet("sync:set:mx")?.Count > 0)
                {
                    return true;
                }

                foreach (var (_, setKey, _) in WindowKinds)
                {
                    if (storageConnection.GetAllItemsFromSet(setKey)?.Count > 0)
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Gets all registered semaphores with their limits and current holders.
    /// </summary>
    public virtual IReadOnlyList<SemaphoreDto> GetSemaphores()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        var result = new List<SemaphoreDto>();

        foreach (var id in storageConnection.GetAllItemsFromSet("sync:set:sm").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(ReadSemaphore(storageConnection, id));
        }

        return result;
    }

    /// <summary>
    /// Gets a single registered semaphore, or null when it does not exist.
    /// </summary>
    public SemaphoreDto GetSemaphore(string semaphoreId)
    {
        if (string.IsNullOrWhiteSpace(semaphoreId))
            return null;

        // Hangfire.Throttling lowercases ids when it writes them, so a differently-cased id coming
        // from a route or a link would otherwise miss on case-sensitive storages.
        semaphoreId = semaphoreId.ToLowerInvariant();

        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return null;

        var options = storageConnection.GetAllEntriesFromHash($"sync:sm:{semaphoreId}");
        if (options == null || options.Count == 0)
            return null;

        return ReadSemaphore(storageConnection, semaphoreId);
    }

    /// <summary>
    /// Gets all currently tracked mutexes with their holders. The registry stores
    /// "{mutexId}/{jobId}" pairs; entries exist only while a mutex is held.
    /// </summary>
    public IReadOnlyList<MutexDto> GetMutexes()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        var holdersByMutex = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var entry in storageConnection.GetAllItemsFromSet("sync:set:mx"))
        {
            // Registry entries are "{mutexId}/{jobId}" pairs (split on the first separator,
            // mirroring the writer). Tolerate bare ids by falling back to the holder set.
            string mutexId;
            string holderJobId = null;

            var separator = entry.IndexOf('/');
            if (separator >= 0)
            {
                mutexId = entry[..separator];
                holderJobId = entry[(separator + 1)..];
            }
            else
            {
                mutexId = entry;
            }

            if (!holdersByMutex.TryGetValue(mutexId, out var holders))
            {
                holders = [];
                holdersByMutex[mutexId] = holders;
            }

            if (!string.IsNullOrEmpty(holderJobId))
            {
                holders.Add(holderJobId);
            }
            else
            {
                foreach (var jobId in storageConnection.GetAllItemsFromSet($"sync:mx:{mutexId}"))
                {
                    if (!holders.Contains(jobId))
                        holders.Add(jobId);
                }
            }
        }

        return holdersByMutex
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new MutexDto { Id = x.Key, HolderJobIds = x.Value })
            .ToArray();
    }

    /// <summary>
    /// Gets all registered rate-limit windows (fixed, sliding, and dynamic).
    /// </summary>
    public IReadOnlyList<ThrottleWindowDto> GetWindows()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        var result = new List<ThrottleWindowDto>();

        foreach (var (type, setKey, keyPrefix) in WindowKinds)
        {
            foreach (var id in storageConnection.GetAllItemsFromSet(setKey).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var entries = storageConnection.GetAllEntriesFromHash(keyPrefix + id);

                string description = null;
                entries?.TryGetValue("d", out description);

                var window = new ThrottleWindowDto
                {
                    Type = type,
                    Id = id,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                };

                if (entries != null && entries.TryGetValue("obj", out var raw) && !string.IsNullOrWhiteSpace(raw))
                {
                    PopulateWindowState(window, raw);
                }

                result.Add(window);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the current state of holder jobs and flags the ones that can no longer release
    /// their own slot, which are the safe candidates for detaching. Three cases qualify:
    /// the job no longer exists (its record expired out of storage), it reached a final state
    /// more than <see cref="FinalStateGrace"/> ago, or it is recorded as Processing on a server
    /// that has stopped sending heartbeats.
    /// </summary>
    public IReadOnlyList<ThrottleHolderDto> GetHolderDetails(IEnumerable<string> jobIds)
    {
        // Read-only: this method never writes, so it should not take a connection from the
        // read-write pool, and it can be served by a replica where storage supports one.
        using var connection = _storage.GetReadOnlyConnection();

        var activeServerIds = GetActiveServerIds();
        var result = new List<ThrottleHolderDto>();

        foreach (var jobId in jobIds)
        {
            var holder = new ThrottleHolderDto { JobId = jobId };
            var state = SafeGetStateData(connection, jobId);

            if (state == null)
            {
                // The holder entry outlived the job record itself. Throttling holder sets carry no
                // expiry, so nothing will ever remove this entry — the slot is permanently lost.
                holder.IsOrphaned = true;
                holder.OrphanReason = "The job no longer exists in storage, so its slot can never be released.";
                result.Add(holder);
                continue;
            }

            holder.StateName = state.Name;

            if (ProcessingState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (state.Data != null && state.Data.TryGetValue("ServerId", out var serverId))
                {
                    holder.ServerId = serverId;

                    if (activeServerIds != null && !activeServerIds.Contains(serverId))
                    {
                        holder.IsOrphaned = true;
                        holder.OrphanReason =
                            $"Server '{serverId}' has not sent a heartbeat for over {Describe(_serverHeartbeatTolerance)}, so the job aborted without releasing its slot.";
                    }
                }
            }
            else if (IsFinalState(state.Name))
            {
                // A job in a final state is not running and will not release anything. Allow a
                // short grace period first: the release and the state change are separate writes,
                // so a job that finished a moment ago may legitimately still appear as a holder.
                var reachedAt = TryGetStateTimestamp(state);

                if (reachedAt == null || DateTime.UtcNow - reachedAt.Value > FinalStateGrace)
                {
                    holder.IsOrphaned = true;
                    holder.OrphanReason =
                        $"The job finished in the {state.Name} state but its slot was never released.";
                }
            }

            result.Add(holder);
        }

        return result;
    }

    private static bool IsFinalState(string stateName) =>
        SucceededState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase)
        || FailedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase)
        || DeletedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the timestamp a final state was entered from its state data. Hangfire serializes these
    /// as milliseconds since the Unix epoch; older data may carry a round-trippable string instead.
    /// </summary>
    private static DateTime? TryGetStateTimestamp(StateData state)
    {
        if (state.Data == null)
            return null;

        foreach (var field in new[] { "SucceededAt", "FailedAt", "DeletedAt" })
        {
            if (!state.Data.TryGetValue(field, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;

            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string Describe(TimeSpan value) =>
        value.TotalMinutes >= 1
            ? $"{Math.Round(value.TotalMinutes)} minute(s)"
            : $"{Math.Round(value.TotalSeconds)} second(s)";

    private static SemaphoreDto ReadSemaphore(JobStorageConnection storageConnection, string id)
    {
        var options = storageConnection.GetAllEntriesFromHash($"sync:sm:{id}");

        var maxCount = 0;
        if (options != null && options.TryGetValue("max", out var rawMax))
        {
            int.TryParse(rawMax, out maxCount);
        }

        string description = null;
        options?.TryGetValue("d", out description);

        var holders = storageConnection.GetAllItemsFromSet($"sync:j:sm:{id}");

        return new SemaphoreDto
        {
            Id = id,
            MaxCount = maxCount,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            HolderJobIds = holders?.ToArray() ?? [],
        };
    }

    private HashSet<string> GetActiveServerIds()
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        var threshold = DateTime.UtcNow - _serverHeartbeatTolerance;

        try
        {
            foreach (var server in _storage.GetMonitoringApi().Servers())
            {
                // A server that has never reported a heartbeat counts as alive, matching
                // HealthCheckService: absence of evidence is not evidence of death, and here an
                // over-eager "offline" reading would invite detaching a live job's slot.
                if (!server.Heartbeat.HasValue || server.Heartbeat.Value >= threshold)
                {
                    active.Add(server.Name);
                }
            }
        }
        catch
        {
            // Monitoring unavailable. Returning the partial set would read as "no server is alive"
            // and flag every running job as orphaned, so report no opinion instead.
            return null;
        }

        return active;
    }

    private static StateData SafeGetStateData(IStorageConnection connection, string jobId)
    {
        try
        {
            return connection.GetStateData(jobId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the serialized window state stored under the "obj" hash field.
    ///
    /// <para>
    /// The state uses abbreviated field names, and the same name carries a different shape
    /// depending on the window kind, so parsing is per-type rather than generic:
    /// <code>
    /// Fixed    {"l":5,"i":3600,"w":1786359600,"c":3}
    ///          l limit · i interval seconds · w active window start (unix) · c count (number)
    /// Sliding  {"l":4,"i":600,"b":120,"t":1786362360,"c":{"0":3}}
    ///          b bucket size in seconds · c bucket index -> count (object, not a number)
    /// Dynamic  {"i":600,"b":120,"t":...,"maxc":1000,"maxs":3,"mins":3,"w":{"fmt":{"0":3}}}
    ///          no limit unless registered with a capacity (then l) · w window format -> buckets
    /// </code>
    /// Note that <c>c</c> is a number for fixed windows but an object for sliding ones, and
    /// <c>w</c> is a timestamp for fixed windows but an object for dynamic ones.
    /// </para>
    ///
    /// <para>
    /// Both the names and the shapes come from reading back what Hangfire.Throttling 1.4.3 wrote,
    /// on SQL Server, Redis and in-memory storage alike.
    /// </para>
    /// </summary>
    private static void PopulateWindowState(ThrottleWindowDto window, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return;

            // Present for fixed and sliding windows, and for dynamic windows registered with an
            // explicit capacity. A dynamic window without one has no fixed limit to show.
            window.Limit = TryGetInt(root, "l");
            window.IntervalSeconds = TryGetInt(root, "i");

            window.Counter = window.Type switch
            {
                ThrottleWindowTypes.Fixed => TryGetInt(root, "c"),
                ThrottleWindowTypes.Sliding => SumBuckets(root, "c"),
                ThrottleWindowTypes.Dynamic => SumNestedBuckets(root, "w"),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // Unknown serialization — the window is still listed with id/description.
        }
    }

    private static int? TryGetInt(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Sums a bucket map of index to count, e.g. the sliding window's <c>{"0":3,"1":1}</c>.
    /// Returns null when the property is absent, which reads as "no activity recorded yet"
    /// rather than as a count of zero.
    /// </summary>
    private static int? SumBuckets(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var buckets) || buckets.ValueKind != JsonValueKind.Object)
            return null;

        var total = 0;
        foreach (var bucket in buckets.EnumerateObject())
        {
            if (bucket.Value.ValueKind == JsonValueKind.Number && bucket.Value.TryGetInt32(out var count))
                total += count;
        }

        return total;
    }

    /// <summary>
    /// Sums the dynamic window's nested map of window format to bucket map, e.g.
    /// <c>{"my-window":{"0":3}}</c>. A dynamic window tracks each format it has seen separately,
    /// so the total across all of them is what corresponds to the single count column.
    /// </summary>
    private static int? SumNestedBuckets(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var windows) || windows.ValueKind != JsonValueKind.Object)
            return null;

        var total = 0;
        foreach (var format in windows.EnumerateObject())
        {
            total += SumBuckets(windows, format.Name) ?? 0;
        }

        return total;
    }
}
