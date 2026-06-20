using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>
/// Concurrency view (task 11.5): renders the duration-aware concurrency of a single day, stacked by
/// layer, with a dashed worker-capacity reference line and a distinct flag on the buckets whose
/// concurrency strictly exceeds capacity (Requirements 4.8, 4.9).
/// </summary>
/// <remarks>
/// <para>
/// The concurrency itself is computed server-side by the pure <c>ConcurrencyAnalyzer</c> (surfaced
/// through <c>HeatmapService.GetConcurrencyAsync</c>) and handed to this component as a
/// <see cref="ConcurrencyResult"/> together with the resolved worker <see cref="Capacity"/>. This
/// view never calls the service itself — it mirrors the other heatmap views by taking its computed
/// inputs as parameters and only maps them into the <c>window.heatmapCharts.renderConcurrency</c>
/// model, managing the Chart.js lifecycle through <see cref="IJSRuntime"/> exactly like the existing
/// analytics chart components.
/// </para>
/// <para>
/// The renderer's model carries an <c>adhoc</c> baseline layer and a <c>cron</c> layer
/// (<c>{ labels, adhoc, cron, capacity, peak, peakMinute, worstDayLabel }</c>). The <c>cron</c> layer
/// is populated by summing the per-queue concurrency series into a single layer.
/// </para>
/// <para>
/// <b>Combined demand-aware concurrency (task 16.4, Req 19.1/19.2).</b> When <see cref="JobClass"/>
/// is <c>Combined</c>, the per-slot ad-hoc baseline from <see cref="AdHocBaselinePerSlot"/> (derived
/// by the caller from the <c>Demand_Profile</c>) is emitted as a distinct <c>adhoc</c> layer beneath
/// the <c>cron</c> layer. The renderer stacks <c>adhoc + cron</c> and compares that combined total to
/// <see cref="Capacity"/> when flagging over-capacity buckets and drawing the capacity line, so the
/// over-capacity indicator reflects the total real pressure (Req 19.1) while keeping the two
/// contributions visually distinct (Req 19.2). In <c>Cron</c> or <c>Ad-hoc</c> classes the ad-hoc
/// baseline layer is suppressed and only the cron contribution is plotted.
/// </para>
/// </remarks>
public partial class ConcurrencyView : ComponentBase, IAsyncDisposable
{
    private const int MinutesPerDay = 1440;
    private const int HoursPerDay = 24;
    private const int MinutesPerHour = 60;

    private readonly string _canvasId = $"hf-heatmap-concurrency-{Guid.NewGuid():N}";

    private object _model;
    private bool _pendingRender;
    private bool _rendered;

    [Inject] private IJSRuntime JS { get; set; }

    /// <summary>
    /// The duration-aware concurrency analysis for the analyzed day, produced by
    /// <c>HeatmapService.GetConcurrencyAsync</c> from the pure <c>ConcurrencyAnalyzer</c>.
    /// </summary>
    [Parameter, EditorRequired] public ConcurrencyResult Result { get; set; }

    /// <summary>
    /// The active worker capacity (detected or manually overridden) used to draw the reference line
    /// and flag over-capacity buckets (Req 4.9). Resolved by the caller via <c>CapacityResolver</c>.
    /// </summary>
    [Parameter, EditorRequired] public int Capacity { get; set; }

    /// <summary>The human-readable label of the day being analyzed (e.g. <c>Mon</c> or a date).</summary>
    [Parameter] public string DayLabel { get; set; }

    /// <summary>
    /// The queues to include, in display order. When <c>null</c>, every queue in
    /// <see cref="ConcurrencyResult.PerQueueSeries"/> contributes. Filtering (e.g. Top-N) is applied
    /// by the caller, mirroring the other heatmap views.
    /// </summary>
    [Parameter] public IReadOnlyList<string> VisibleQueues { get; set; }

    /// <summary>
    /// An optional per-minute ad-hoc demand baseline (1,440 slots) added beneath the cron layer
    /// before the capacity comparison. Derived by the caller from the <c>Demand_Profile</c>; only
    /// consumed when <see cref="JobClass"/> is <c>Combined</c> (Req 19.1).
    /// </summary>
    [Parameter] public IReadOnlyList<int> AdHocBaselinePerSlot { get; set; }

    /// <summary>
    /// The active job class — <c>Cron</c>, <c>Ad-hoc</c>, or <c>Combined</c>. Only in <c>Combined</c>
    /// is the <see cref="AdHocBaselinePerSlot"/> emitted as a distinct ad-hoc layer and summed with
    /// the cron contribution before the capacity comparison (Req 19.1/19.2). Defaults to
    /// <c>Combined</c> to mirror the other demand-aware views.
    /// </summary>
    [Parameter] public string JobClass { get; set; } = "Combined";

    /// <summary>
    /// Whether the active <see cref="JobClass"/> is <c>Combined</c>, in which case the ad-hoc demand
    /// baseline is overlaid beneath the cron contribution (Req 19.1/19.2).
    /// </summary>
    private bool IsCombined => string.Equals(JobClass, "Combined", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether there is any concurrency to plot (the day has at least one fire).</summary>
    private bool HasData => Result is { PeakConcurrency: > 0 };

    /// <summary>The peak concurrency time formatted as <c>HH:mm</c>, or <c>null</c> when there is no peak.</summary>
    private string PeakTimeText =>
        Result?.PeakMinuteOfDay is int minute
            ? $"{minute / MinutesPerHour:00}:{minute % MinutesPerHour:00}"
            : null;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _model = HasData ? BuildModel() : null;
        // Any input change (result, capacity, queue filter, ad-hoc baseline) requires a re-render.
        _pendingRender = true;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_pendingRender)
        {
            return;
        }

        _pendingRender = false;

        try
        {
            if (_model is not null)
            {
                await JS.InvokeVoidAsync("heatmapCharts.renderConcurrency", _canvasId, _model);
                _rendered = true;
            }
            else if (_rendered)
            {
                // Data disappeared (e.g. day with no fires after a filter change) — tear down.
                await JS.InvokeVoidAsync("heatmapCharts.destroy", _canvasId);
                _rendered = false;
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected — nothing to render against.
        }
        catch (ObjectDisposedException)
        {
            // Component / JS runtime disposed during render.
        }
    }

    /// <summary>
    /// Maps the concurrency result into the <c>renderConcurrency</c> model
    /// (<c>{ labels, adhoc, cron, capacity, peak, peakMinute, worstDayLabel }</c>).
    /// </summary>
    /// <remarks>
    /// The per-minute per-queue series are summed into a single day-wide total (filtered to the
    /// visible queues), then reduced to 24 hourly buckets by taking the peak concurrency observed
    /// within each hour — the value the renderer compares against capacity to flag over-capacity
    /// buckets (Req 4.9). In <c>Combined</c> mode the ad-hoc baseline is reduced the same way and
    /// emitted as a distinct layer; the renderer stacks <c>adhoc + cron</c> and compares the combined
    /// total to capacity (Req 19.1/19.2). In <c>Cron</c>/<c>Ad-hoc</c> mode the ad-hoc layer is
    /// suppressed and only the cron layer is emitted.
    /// </remarks>
    private object BuildModel()
    {
        var labels = new string[HoursPerDay];
        for (var hour = 0; hour < HoursPerDay; hour++)
        {
            labels[hour] = $"{hour:00}:00";
        }

        // Ad-hoc baseline (Combined only) reduced to hourly peaks.
        var adhocBaseline = IsCombined ? AdHocBaselinePerSlot : null;
        var adhoc = new double[HoursPerDay];
        if (adhocBaseline is not null)
        {
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                double peak = 0;
                var start = hour * MinutesPerHour;
                var end = Math.Min(start + MinutesPerHour, adhocBaseline.Count);
                for (var minute = start; minute < end; minute++)
                {
                    if (adhocBaseline[minute] > peak)
                    {
                        peak = adhocBaseline[minute];
                    }
                }

                adhoc[hour] = peak;
            }
        }

        var hasAdHoc = adhoc.Any(v => v > 0);

        // One stacked layer per visible queue: its hourly peak concurrency. The authoritative combined
        // peak is reported separately (Result.PeakConcurrency); these per-queue bars are the visual
        // breakdown of where the pressure comes from (Req: split the cron layer per queue).
        var queues = new List<object>();
        var series = Result?.PerQueueSeries;
        if (series is not null)
        {
            foreach (var queueSeries in series.OrderBy(s => s.Queue, StringComparer.Ordinal))
            {
                if (VisibleQueues is not null && !VisibleQueues.Contains(queueSeries.Queue, StringComparer.Ordinal))
                {
                    continue;
                }

                var perSlot = queueSeries.ConcurrencyPerSlot;
                if (perSlot is null)
                {
                    continue;
                }

                var data = new double[HoursPerDay];
                var any = false;
                for (var hour = 0; hour < HoursPerDay; hour++)
                {
                    double peak = 0;
                    var start = hour * MinutesPerHour;
                    var end = Math.Min(start + MinutesPerHour, perSlot.Count);
                    for (var minute = start; minute < end; minute++)
                    {
                        if (perSlot[minute] > peak)
                        {
                            peak = perSlot[minute];
                        }
                    }

                    data[hour] = peak;
                    if (peak > 0)
                    {
                        any = true;
                    }
                }

                if (any)
                {
                    queues.Add(new { queue = queueSeries.Queue, data });
                }
            }
        }

        return new
        {
            labels,
            // Emitted only in Combined mode (renderer omits the ad-hoc dataset when the array is empty).
            adhoc = hasAdHoc ? adhoc : Array.Empty<double>(),
            queues,
            capacity = Capacity,
            peak = Result.PeakConcurrency,
            peakMinute = Result.PeakMinuteOfDay,
            worstDayLabel = DayLabel
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_rendered)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("heatmapCharts.destroy", _canvasId);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (ObjectDisposedException)
        {
            // JS runtime already disposed.
        }
    }
}
