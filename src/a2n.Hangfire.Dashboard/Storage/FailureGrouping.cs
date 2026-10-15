using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Hangfire.Common;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// The storage-independent part of <see cref="IStorageQueryProvider.GetFailureGroupsAsync"/>: reading
/// a failed job's stored strings, and merging stored and computed fingerprints into groups. The
/// providers only supply the queries, so every storage groups, counts and labels the same way.
/// </summary>
/// <remarks>
/// Intended for <see cref="IStorageQueryProvider"/> implementations (the bundled storage adapters, or a
/// custom one); it is public only because those live in other assemblies.
/// </remarks>
public static class FailureGrouping
{
    /// <summary>
    /// The job's type as stored in its invocation data (<c>MyApp.Jobs.OrderJob, MyApp</c>), or null
    /// when the payload can't be read.
    /// </summary>
    /// <remarks>
    /// Read from the stored payload rather than <c>IStorageConnection.GetJobData</c>, which returns the
    /// invocation data on some storages only (Hangfire.PostgreSql 1.20 leaves it unset), so a computed
    /// fingerprint would otherwise depend on which adapter is installed.
    /// </remarks>
    public static string ReadJobTypeName(string invocationDataPayload)
        => ReadInvocationData(invocationDataPayload, arguments: null)?.Type;

    /// <summary>
    /// The job's invocation data with its arguments, which Hangfire.SqlServer and Hangfire.PostgreSql
    /// keep in a column of their own, or null when the payload can't be read.
    /// </summary>
    public static InvocationData ReadInvocationData(string invocationDataPayload, string arguments)
    {
        if (string.IsNullOrWhiteSpace(invocationDataPayload))
            return null;

        try
        {
            var invocationData = InvocationData.DeserializePayload(invocationDataPayload);
            if (invocationData != null && arguments != null)
                invocationData.Arguments = arguments;
            return invocationData;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The state data stored as JSON, with keys compared case-insensitively, or null when it is
    /// missing or can't be read. A missing exception field then counts as empty, as it does in
    /// <see cref="FailureFingerprint.Compute(IDictionary{string, string}, string)"/>.
    /// </summary>
    public static Dictionary<string, string> ReadStateData(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var parsed = SerializationHelper.Deserialize<Dictionary<string, string>>(json);
            return parsed == null ? null : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// When the job failed: the state's <c>FailedAt</c>, or the time the state was recorded when that
    /// is missing or unreadable (as on the Failed Jobs page, see <c>IFailedJobPageSource</c>).
    /// </summary>
    public static DateTime? ReadFailedAt(IDictionary<string, string> stateData, DateTime? stateCreatedAt)
    {
        if (stateData != null && stateData.TryGetValue("FailedAt", out var text) && !string.IsNullOrEmpty(text))
        {
            try
            {
                var failedAt = JobHelper.DeserializeNullableDateTime(text);
                if (failedAt.HasValue)
                    return failedAt;
            }
            catch (Exception)
            {
                // Fall back to the state's own time below.
            }
        }

        return AsUtc(stateCreatedAt);
    }

    /// <summary>
    /// Fingerprints a failed job read by a storage adapter. Its <see cref="ComputedFailure.Sequence"/>
    /// is the row's <see cref="FailedJobRow.StateId"/>, or its id when the adapter doesn't read that.
    /// </summary>
    public static ComputedFailure Compute(FailedJobRow row)
    {
        var stateData = ReadStateData(row.StateData);
        return new ComputedFailure(
            row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.StateId ?? row.Id,
            FailureFingerprint.Compute(stateData, ReadJobTypeName(row.InvocationData)),
            ReadFailedAt(stateData, row.StateCreatedAt));
    }

    /// <summary>A failed job read by a storage adapter, as a row of the Failed page's group view.</summary>
    public static JobSummaryDto ToJobSummary(FailedJobRow row)
    {
        var stateData = ReadStateData(row.StateData);
        var invocationData = ReadInvocationData(row.InvocationData, row.Arguments);
        return new JobSummaryDto
        {
            JobId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JobName = JobNameHelper.GetDisplayName(null, invocationData),
            State = FailedStateName,
            CreatedAt = AsUtc(row.CreatedAt),
            LastStateChange = ReadFailedAt(stateData, row.StateCreatedAt),
            ExceptionType = Get(stateData, "ExceptionType"),
            ExceptionMessage = Get(stateData, "ExceptionMessage"),
            Reason = row.Reason,
            InvocationData = invocationData,
        };
    }

    /// <summary>
    /// Merges the stored fingerprints and the ones computed by the fallback into groups, most jobs
    /// first, and labels the <paramref name="labelCount"/> largest from their newest job: the one with
    /// the highest <see cref="ComputedFailure.Sequence"/> or <see cref="StoredFailureGroup.NewestSequence"/>,
    /// which is the job that failed last.
    /// </summary>
    /// <param name="stored">Per stored fingerprint: how many failed jobs carry it and the newest of them.</param>
    /// <param name="unfingerprintedCount">Failed jobs without a current-version stored fingerprint.</param>
    /// <param name="computed">The ones of those the fallback read and fingerprinted.</param>
    /// <param name="labelCount">How many of the largest groups to label.</param>
    /// <param name="loadNewestJobs">
    /// Reads and fingerprints the jobs with the given ids in one round trip; used for groups whose
    /// newest job has a stored fingerprint. May be null when <paramref name="stored"/> is empty.
    /// </param>
    /// <param name="ct">Cancellation token</param>
    public static async Task<FailureGroupsResult> BuildAsync(
        IEnumerable<StoredFailureGroup> stored,
        long unfingerprintedCount,
        IReadOnlyCollection<ComputedFailure> computed,
        int labelCount,
        Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<ComputedFailure>>> loadNewestJobs,
        CancellationToken ct)
    {
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        GroupBuilder For(string fingerprint)
        {
            if (!groups.TryGetValue(fingerprint, out var group))
                groups[fingerprint] = group = new GroupBuilder(fingerprint);
            return group;
        }

        foreach (var s in stored ?? Enumerable.Empty<StoredFailureGroup>())
        {
            var group = For(s.Fingerprint);
            group.StoredCount += s.Count;
            group.Offer(s.NewestSequence, s.NewestJobId.ToString(System.Globalization.CultureInfo.InvariantCulture), s.NewestJobId, newest: null);
        }

        foreach (var c in computed ?? Array.Empty<ComputedFailure>())
        {
            var group = For(c.Result.Fingerprint);
            group.Computed.Add(c);
            group.Offer(c.Sequence, c.JobId, storedJobId: null, c);
        }

        var ordered = groups.Values
            .OrderByDescending(g => g.StoredCount + g.Computed.Count)
            .ThenByDescending(g => g.NewestSequence)
            .ThenBy(g => g.Fingerprint, StringComparer.Ordinal)
            .ToList();

        var toLabel = ordered.Take(Math.Max(0, labelCount)).ToList();
        // Matched by job id: a sample's sequence is whatever the adapter read it with.
        var samples = new Dictionary<string, ComputedFailure>(StringComparer.Ordinal);
        var missing = toLabel.Where(g => g.Newest is null && g.NewestStoredJobId.HasValue)
            .Select(g => g.NewestStoredJobId.Value)
            .ToList();
        if (missing.Count > 0 && loadNewestJobs != null)
        {
            foreach (var sample in await loadNewestJobs(missing, ct).ConfigureAwait(false))
                samples[sample.JobId] = sample;
        }

        var labeled = new HashSet<GroupBuilder>(toLabel);
        var result = new List<FailureGroupDto>(ordered.Count);
        foreach (var group in ordered)
        {
            var dto = new FailureGroupDto
            {
                Fingerprint = group.Fingerprint,
                StoredCount = group.StoredCount,
                ComputedCount = group.Computed.Count,
                Count = group.StoredCount + group.Computed.Count,
                ComputedJobIds = group.Computed.OrderByDescending(c => c.Sequence).Select(c => c.JobId).ToList(),
                NewestJobId = group.NewestJobId,
            };

            var newest = group.Newest
                ?? (samples.TryGetValue(group.NewestJobId, out var sample) ? sample : (ComputedFailure?)null);
            if (labeled.Contains(group) && newest is { } label)
            {
                dto.IsLabeled = true;
                dto.ExceptionType = label.Result.ExceptionType;
                dto.InnerExceptionType = label.Result.InnerExceptionType;
                dto.NormalizedMessage = label.Result.NormalizedMessage;
                dto.TopFrame = label.Result.TopFrame;
                dto.LastFailedAt = label.FailedAt;
            }

            result.Add(dto);
        }

        return new FailureGroupsResult
        {
            Groups = result,
            UnfingerprintedCount = unfingerprintedCount,
            FallbackCount = computed?.Count ?? 0,
        };
    }

    private const string FailedStateName = "Failed";

    private static string Get(IDictionary<string, string> data, string key)
        => data != null && data.TryGetValue(key, out var value) ? value : null;

    private static DateTime? AsUtc(DateTime? value)
        => value is { Kind: not DateTimeKind.Utc } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : value;

    private sealed class GroupBuilder
    {
        public GroupBuilder(string fingerprint) => Fingerprint = fingerprint;

        public string Fingerprint { get; }
        public long StoredCount { get; set; }
        public List<ComputedFailure> Computed { get; } = new();
        public long NewestSequence { get; private set; } = long.MinValue;
        public string NewestJobId { get; private set; }

        /// <summary>The newest job's id when it carries a stored fingerprint, for loading its label.</summary>
        public long? NewestStoredJobId { get; private set; }

        /// <summary>The newest job when the fallback read it, so it doesn't need loading for the label.</summary>
        public ComputedFailure? Newest { get; private set; }

        public void Offer(long sequence, string jobId, long? storedJobId, ComputedFailure? newest)
        {
            if (sequence <= NewestSequence)
                return;
            NewestSequence = sequence;
            NewestJobId = jobId;
            NewestStoredJobId = storedJobId;
            Newest = newest;
        }
    }
}

/// <summary>
/// A fingerprint stored on failed jobs: how many failed jobs carry it, and the id of the newest, the
/// one that failed last. Intended for storage adapters, as input to <see cref="FailureGrouping.BuildAsync"/>.
/// </summary>
public readonly record struct StoredFailureGroup(string Fingerprint, long Count, long NewestJobId)
{
    /// <summary>
    /// Orders the newest job against the fallback's jobs, on the scale of their
    /// <see cref="ComputedFailure.Sequence"/>: its <see cref="FailedJobRow.StateId"/> on the SQL Server and
    /// PostgreSQL adapters. Defaults to <see cref="NewestJobId"/>.
    /// </summary>
    public long NewestSequence { get; init; } = NewestJobId;
}

/// <summary>
/// A failed job fingerprinted from its stored strings. Intended for storage adapters, as input to
/// <see cref="FailureGrouping.BuildAsync"/>.
/// </summary>
/// <param name="JobId">The job id.</param>
/// <param name="Sequence">
/// Orders jobs by when they failed, higher is more recent. On the SQL Server and PostgreSQL adapters
/// it is the id of the job's current state (<see cref="FailedJobRow.StateId"/>), which grows with
/// every state change, so a job that was created long ago and failed just now counts as newer than
/// one created after it that failed earlier.
/// </param>
/// <param name="Result">The computed fingerprint and its parts.</param>
/// <param name="FailedAt">When the job failed; see <see cref="FailureGrouping.ReadFailedAt"/>.</param>
public readonly record struct ComputedFailure(string JobId, long Sequence, FailureFingerprintResult Result, DateTime? FailedAt);

/// <summary>
/// A failed job as the SQL Server and PostgreSQL adapters read it for grouping: the job row joined
/// with its current state. Column names match the properties, so Dapper maps it directly. Intended
/// for storage adapters only.
/// </summary>
public sealed class FailedJobRow
{
    /// <summary>The job id.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The id of the job's current state, its Failed state. State ids grow with every state change, so
    /// they order failed jobs by when they failed, which job ids (creation order) don't for a job that
    /// was scheduled, retried or requeued first. Null when the adapter doesn't read it; the job id is
    /// used then.
    /// </summary>
    public long? StateId { get; set; }

    /// <summary>The stored invocation data payload, as text.</summary>
    public string InvocationData { get; set; }

    /// <summary>The stored arguments, as text, when the storage keeps them in their own column.</summary>
    public string Arguments { get; set; }

    /// <summary>When the job was created.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>The current state's reason.</summary>
    public string Reason { get; set; }

    /// <summary>When the current state was recorded.</summary>
    public DateTime? StateCreatedAt { get; set; }

    /// <summary>The current state's data, as JSON text.</summary>
    public string StateData { get; set; }
}
