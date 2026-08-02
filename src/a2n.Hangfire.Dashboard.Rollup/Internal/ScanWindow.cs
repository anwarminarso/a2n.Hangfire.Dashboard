namespace a2n.Hangfire.Dashboard.Rollup.Internal;

/// <summary>
/// The persisted position of one state-list scan (succeeded or failed).
/// </summary>
/// <remarks>
/// <para>A poll pages the monitoring API newest-first and is bounded, so a single pass cannot always
/// reach back to the watermark. Keeping only a high-water mark loses everything a capped pass did not
/// reach — it advances past those executions and they are never aggregated (issue #29). The checkpoint
/// therefore describes a covered <em>range</em> rather than a single boundary:</para>
/// <list type="bullet">
/// <item><description>ticks ≤ <see cref="WatermarkTicks"/> — aggregated, or deliberately skipped at
///   seeding time;</description></item>
/// <item><description>ticks in <c>(WatermarkTicks, CoveredFloorTicks)</c> — the pending gap: seen by
///   no pass yet and still to be drained;</description></item>
/// <item><description>ticks in <c>[CoveredFloorTicks, CoveredCeilingTicks]</c> — aggregated by an
///   earlier capped pass, so a resuming pass must skip them to keep the additive counters
///   correct;</description></item>
/// <item><description>ticks &gt; <see cref="CoveredCeilingTicks"/> — not scanned yet (arrivals since
///   the last pass).</description></item>
/// </list>
/// <para>With no gap open both boundaries are zero and the checkpoint degenerates to the plain
/// watermark, which is also how state written by earlier versions reads back.</para>
/// </remarks>
internal readonly record struct ScanCheckpoint
{
    public ScanCheckpoint(long watermarkTicks, long coveredFloorTicks, long coveredCeilingTicks)
    {
        WatermarkTicks = watermarkTicks;

        // A floor at or below the watermark carries no information: that range is covered already.
        var floor = coveredFloorTicks > watermarkTicks ? coveredFloorTicks : 0L;
        CoveredFloorTicks = floor;
        CoveredCeilingTicks = floor > 0 ? Math.Max(coveredCeilingTicks, floor) : 0L;
    }

    /// <summary>Everything at or below this tick has been aggregated.</summary>
    public long WatermarkTicks { get; }

    /// <summary>Exclusive lower bound of the range an earlier capped pass covered; 0 when no gap is open.</summary>
    public long CoveredFloorTicks { get; }

    /// <summary>Upper bound of the range an earlier capped pass covered; 0 when no gap is open.</summary>
    public long CoveredCeilingTicks { get; }

    /// <summary>True while executions between the watermark and the covered floor are still pending.</summary>
    public bool HasGap => CoveredFloorTicks > WatermarkTicks;

    /// <summary>Length of the pending gap, or <see cref="TimeSpan.Zero"/> when there is none.</summary>
    public TimeSpan PendingSpan
        => HasGap ? TimeSpan.FromTicks(CoveredFloorTicks - WatermarkTicks) : TimeSpan.Zero;

    /// <summary>A checkpoint with no pending gap: the classic single watermark.</summary>
    public static ScanCheckpoint Collapsed(long watermarkTicks) => new(watermarkTicks, 0, 0);
}

/// <summary>What a scan should do with the entry it is looking at.</summary>
internal enum ScanAction
{
    /// <summary>Aggregate the entry.</summary>
    Record,

    /// <summary>An earlier pass already aggregated it; step over it without spending budget.</summary>
    Skip,

    /// <summary>The entry is at or below the watermark: everything above it is now covered.</summary>
    StopDrained,

    /// <summary>The per-poll budget is spent; the rest is left for the next poll.</summary>
    StopExhausted
}

/// <summary>Where a finished pass leaves the checkpoint.</summary>
/// <param name="Checkpoint">State to persist.</param>
/// <param name="Recorded">How many executions the pass aggregated.</param>
/// <param name="DataDropped">
/// True when the pass could not be joined to the range an earlier pass covered, so executions between
/// the two ranges were written off. This is the only case in which rollup data is genuinely lost.
/// </param>
internal readonly record struct ScanResult(ScanCheckpoint Checkpoint, int Recorded, bool DataDropped);

/// <summary>
/// Decides, entry by entry, whether a newest-first pass over a state list should record, skip or stop,
/// and computes the checkpoint the pass leaves behind. Pure state, so the resume and cap paths are
/// testable without any storage.
/// </summary>
internal sealed class ScanWindow
{
    private readonly ScanCheckpoint _start;
    private readonly int _recordCap;

    private long _newestRecorded;
    private long _oldestRecorded = long.MaxValue;
    private int _recorded;

    public ScanWindow(ScanCheckpoint start, int recordCap)
    {
        _start = start;
        _recordCap = recordCap > 0 ? recordCap : 1;
    }

    /// <summary>How many entries this pass has recorded so far.</summary>
    public int Recorded => _recorded;

    public ScanAction Classify(long ticks)
    {
        if (ticks <= _start.WatermarkTicks)
            return ScanAction.StopDrained;

        // The budget is soft at the tail: once it is spent the pass keeps taking entries that share the
        // oldest tick it already recorded, so the covered floor always lands on a tick boundary.
        // Stopping mid-tick would either drop that tick's remaining siblings (the floor claims them as
        // covered) or replay them on the next poll, and the rollup counters are additive.
        if (_recorded >= _recordCap)
            return ticks == _oldestRecorded ? ScanAction.Record : ScanAction.StopExhausted;

        if (_start.HasGap && ticks >= _start.CoveredFloorTicks && ticks <= _start.CoveredCeilingTicks)
            return ScanAction.Skip;

        return ScanAction.Record;
    }

    public void OnRecorded(long ticks)
    {
        _recorded++;
        if (ticks > _newestRecorded) _newestRecorded = ticks;
        if (ticks < _oldestRecorded) _oldestRecorded = ticks;
    }

    /// <param name="drained">
    /// True when the pass reached the watermark or the end of the list, so everything above the
    /// watermark is now covered.
    /// </param>
    public ScanResult Complete(bool drained)
    {
        if (drained)
        {
            var watermark = Math.Max(
                Math.Max(_start.WatermarkTicks, _start.CoveredCeilingTicks),
                _newestRecorded);
            return new ScanResult(ScanCheckpoint.Collapsed(watermark), _recorded, false);
        }

        // Nothing was recorded, so nothing new is covered: an empty read or a failure before the first
        // usable entry leaves the checkpoint untouched and the poll simply retries.
        if (_recorded == 0)
            return new ScanResult(_start, 0, false);

        var ceiling = Math.Max(_newestRecorded, _start.CoveredCeilingTicks);

        if (_start.HasGap && _oldestRecorded > _start.CoveredCeilingTicks)
        {
            // The budget ran out before the pass reached the range the previous pass had covered, so
            // the executions in between were never scanned. Two disjoint covered ranges cannot be
            // represented, and re-scanning them later would double-count the additive counters, so the
            // ranges are merged and the unscanned band is written off.
            return new ScanResult(
                new ScanCheckpoint(_start.WatermarkTicks, _start.CoveredFloorTicks, ceiling), _recorded, true);
        }

        // Contiguous from the top: the pass covered [oldestRecorded, ceiling], either straight down
        // from the newest entry or by stepping over the previously covered range on the way.
        return new ScanResult(
            new ScanCheckpoint(_start.WatermarkTicks, _oldestRecorded, ceiling), _recorded, false);
    }
}
