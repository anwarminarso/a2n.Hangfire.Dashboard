using System.Text.Json;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Reads concurrency-throttling primitives (semaphores, mutexes, and rate-limit windows) from
/// Hangfire storage. Compatible with data written by the Hangfire.Throttling package.
/// Key patterns (ids are lowercase-normalized by the writer):
///   "sync:set:sm"    - set of registered semaphore ids
///   "sync:sm:{id}"   - semaphore options hash ("max" = limit, "d" = description)
///   "sync:j:sm:{id}" - set of job ids currently holding a semaphore slot
///   "sync:set:mx"    - set of "{mutexId}/{jobId}" pairs for currently held mutexes
///   "sync:mx:{id}"   - set of job ids currently holding the mutex
///   "sync:set:fw|sw|dp" and "sync:fw|sw|dp:{id}" - fixed/sliding/dynamic windows
///     (hash: "obj" = serialized window state, "d" = description)
/// </summary>
public class ThrottlingDataReader
{
    private static readonly (string Type, string SetKey, string KeyPrefix)[] WindowKinds =
    [
        ("Fixed", "sync:set:fw", "sync:fw:"),
        ("Sliding", "sync:set:sw", "sync:sw:"),
        ("Dynamic", "sync:set:dp", "sync:dp:"),
    ];

    private readonly JobStorage _storage;

    public ThrottlingDataReader(JobStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Returns true when the storage contains any throttling data, i.e. the host
    /// application uses Hangfire.Throttling.
    /// </summary>
    public bool HasThrottlingData()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return false;

        if (storageConnection.GetSetCount("sync:set:sm") > 0 || storageConnection.GetSetCount("sync:set:mx") > 0)
            return true;

        foreach (var (_, setKey, _) in WindowKinds)
        {
            if (storageConnection.GetSetCount(setKey) > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all registered semaphores with their limits and current holders.
    /// </summary>
    public IReadOnlyList<SemaphoreDto> GetSemaphores()
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
    /// Resolves the current state of holder jobs, flagging holders that are recorded as
    /// Processing on a server without a recent heartbeat: those jobs died without releasing
    /// their slot and are the safe candidates for detaching.
    /// </summary>
    public IReadOnlyList<ThrottleHolderDto> GetHolderDetails(IEnumerable<string> jobIds)
    {
        using var connection = _storage.GetConnection();

        var activeServerIds = GetActiveServerIds();
        var result = new List<ThrottleHolderDto>();

        foreach (var jobId in jobIds)
        {
            var holder = new ThrottleHolderDto { JobId = jobId };

            var state = SafeGetStateData(connection, jobId);
            if (state != null)
            {
                holder.StateName = state.Name;

                if (ProcessingState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase)
                    && state.Data != null
                    && state.Data.TryGetValue("ServerId", out var serverId))
                {
                    holder.ServerId = serverId;
                    holder.IsOrphaned = !activeServerIds.Contains(serverId);
                }
            }

            result.Add(holder);
        }

        return result;
    }

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

        try
        {
            foreach (var server in _storage.GetMonitoringApi().Servers())
            {
                if (server.Heartbeat.HasValue && server.Heartbeat.Value > DateTime.UtcNow.AddMinutes(-1))
                {
                    active.Add(server.Name);
                }
            }
        }
        catch
        {
            // Monitoring unavailable — report holders without orphan detection.
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

    private static void PopulateWindowState(ThrottleWindowDto window, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            window.Limit = TryGetInt(root, "Limit");
            window.IntervalSeconds = TryGetInt(root, "IntervalInSeconds");
            window.Counter = TryGetInt(root, "Counter");
        }
        catch
        {
            // Unknown serialization — the window is still listed with id/description.
        }
    }

    private static int? TryGetInt(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }
}
