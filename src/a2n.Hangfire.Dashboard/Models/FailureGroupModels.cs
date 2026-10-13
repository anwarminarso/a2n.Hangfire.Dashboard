using System;
using System.Collections.Generic;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Interfaces;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Failed jobs that share a <see cref="FailureFingerprint"/>: the same kind of failure.
/// </summary>
public class FailureGroupDto
{
    /// <summary>The fingerprint the jobs share; the group's key.</summary>
    public string Fingerprint { get; set; }

    /// <summary>Number of failed jobs in the group: <see cref="StoredCount"/> plus <see cref="ComputedCount"/>.</summary>
    public long Count { get; set; }

    /// <summary>
    /// Jobs counted by the fingerprint stored on them by <c>FailureFingerprintFilter</c>. These are
    /// exact: every failed job with a stored fingerprint is counted.
    /// </summary>
    public long StoredCount { get; set; }

    /// <summary>
    /// Jobs without a stored fingerprint whose fingerprint was computed from their state data. The
    /// fallback reads a bounded number of them (<see cref="FailureGroupsResult.IsPartial"/>), so this
    /// is a lower bound when the result is partial.
    /// </summary>
    public long ComputedCount { get; set; }

    /// <summary>
    /// Ids of the jobs counted in <see cref="ComputedCount"/>, newest first. Carried so the group's
    /// jobs can be listed without reading the fallback jobs again (see
    /// <see cref="IStorageQueryProvider.GetFailureGroupJobsAsync"/>); there are at most
    /// <see cref="JobFilterCriteria.CountLimit"/> across all groups.
    /// </summary>
    public IReadOnlyList<string> ComputedJobIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Id of the group's newest job: the most recently created one on storages with numeric ids, the
    /// most recently failed one on the generic provider.
    /// </summary>
    public string NewestJobId { get; set; }

    /// <summary>
    /// True when the label below was loaded. Only the largest groups are labeled; see
    /// <see cref="IStorageQueryProvider.GetFailureGroupsAsync"/>.
    /// </summary>
    public bool IsLabeled { get; set; }

    /// <summary>Full name of the exception type, without generic arguments. Null when not labeled.</summary>
    public string ExceptionType { get; set; }

    /// <summary>
    /// Full name of the innermost exception's type when the exception wraps others, otherwise empty.
    /// Null when not labeled.
    /// </summary>
    public string InnerExceptionType { get; set; }

    /// <summary>
    /// First line of the message with run-specific values replaced by placeholders, as the
    /// fingerprint uses it. Null when not labeled.
    /// </summary>
    public string NormalizedMessage { get; set; }

    /// <summary>The stack frame the fingerprint uses, or empty when there is none. Null when not labeled.</summary>
    public string TopFrame { get; set; }

    /// <summary>
    /// When the newest job failed: its <c>FailedAt</c>, or the time its Failed state was recorded
    /// when that is missing. Loaded with the label, so null when not labeled.
    /// </summary>
    public DateTime? LastFailedAt { get; set; }
}

/// <summary>
/// Failed jobs grouped by <see cref="FailureFingerprint"/>, as returned by
/// <see cref="IStorageQueryProvider.GetFailureGroupsAsync"/>.
/// </summary>
public class FailureGroupsResult
{
    /// <summary>How many of the largest groups the Failed page asks to have labeled.</summary>
    public const int DefaultLabelCount = 50;

    /// <summary>
    /// False when the storage provider doesn't support grouping. The other properties are then empty.
    /// </summary>
    public bool IsSupported { get; set; } = true;

    /// <summary>
    /// True when fingerprints stored by <c>FailureFingerprintFilter</c> are counted, so registering the
    /// filter makes the counts exact. The SQL Server and PostgreSQL adapters read them; the generic
    /// provider (Redis and others) can't query job parameters and fingerprints every job it reads, with
    /// or without the filter, so the Failed page doesn't suggest the filter there.
    /// </summary>
    public bool UsesStoredFingerprints { get; set; }

    /// <summary>The groups, most jobs first.</summary>
    public IReadOnlyList<FailureGroupDto> Groups { get; set; } = Array.Empty<FailureGroupDto>();

    /// <summary>
    /// Failed jobs without a stored fingerprint of the current version: failed before
    /// <c>FailureFingerprintFilter</c> was registered, or on a server that doesn't register it.
    /// </summary>
    public long UnfingerprintedCount { get; set; }

    /// <summary>
    /// How many of the <see cref="UnfingerprintedCount"/> jobs were read and fingerprinted on the fly:
    /// the newest ones, up to <see cref="JobFilterCriteria.CountLimit"/>.
    /// </summary>
    public long FallbackCount { get; set; }

    /// <summary>
    /// True when some jobs without a stored fingerprint were not read, so the groups' counts are lower
    /// bounds and groups made only of older failures may be missing.
    /// </summary>
    public bool IsPartial => FallbackCount < UnfingerprintedCount;

    /// <summary>Result of a provider that doesn't support grouping.</summary>
    public static FailureGroupsResult Unsupported() => new() { IsSupported = false };
}
