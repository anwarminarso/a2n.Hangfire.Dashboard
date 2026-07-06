using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>
/// Code-behind for the Queue × Hour view (task 11.2). Builds the renderer model from the aggregated
/// <see cref="HeatmapMatrix"/> — using the pure <see cref="MatrixViews"/> day-slice / weekly-sum
/// helpers for the per-day and whole-week value modes (Req 3.2, 3.3) — and drives the shared
/// <c>window.heatmapCharts.renderQueueHour</c> renderer through <see cref="IJSRuntime"/>, mirroring
/// the analytics chart components' interop and disposal pattern.
/// </summary>
public partial class QueueHourView : IAsyncDisposable
{
    private static readonly string[] DefaultDayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private readonly string _containerId = $"hf-hm-qh-{Guid.NewGuid():N}";
    private bool _rendered;
    private bool _dirty;

    /// <summary>The queues actually rendered, in display order (the supplied selection or the matrix's queues).</summary>
    private IReadOnlyList<string> VisibleQueues =>
        Queues ?? Matrix?.Queues ?? Array.Empty<string>();

    /// <summary>The human-readable label for the active load metric, used in tooltips and the header.</summary>
    private string MetricLabel =>
        Matrix?.Metric == LoadMetric.WorkerMinutes ? "worker-min" : "fires";

    /// <summary>The header label describing the active per-day / whole-week mode (Req 3.2, 3.3).</summary>
    private string DayModeLabel
    {
        get
        {
            if (SelectedDay < 0)
            {
                return "whole week";
            }

            var labels = (DayLabels is { Length: > 0 }) ? DayLabels : DefaultDayLabels;
            return SelectedDay < labels.Length ? labels[SelectedDay] : $"day {SelectedDay + 1}";
        }
    }

    protected override void OnParametersSet()
    {
        // Any parameter change (matrix, day mode, log scale, queue selection) requires a re-render.
        _dirty = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_dirty || Matrix is null || VisibleQueues.Count == 0)
        {
            return;
        }

        _dirty = false;

        try
        {
            await JS.InvokeVoidAsync("heatmapCharts.renderQueueHour", _containerId, BuildModel());
            _rendered = true;
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected — nothing to render against.
        }
        catch (ObjectDisposedException)
        {
            // Component disposed mid-render.
        }
    }

    /// <summary>
    /// Projects the matrix into the renderer model: one row per visible queue with its non-zero
    /// hour cells, plus the shared ramp domain and labels. Per-day mode slices the matrix to the
    /// selected day (Req 3.2); whole-week mode sums each <c>(queue, hour)</c> across the seven days
    /// (Req 3.3). Both are computed by the pure <see cref="MatrixViews"/> helpers.
    /// </summary>
    private object BuildModel()
    {
        IReadOnlyDictionary<QueueHourKey, double> values = SelectedDay < 0
            ? MatrixViews.SumWeek(Matrix)
            : MatrixViews.SliceDay(Matrix, SelectedDay);

        var rows = new List<object>(VisibleQueues.Count);
        double max = 0d;

        foreach (var queue in VisibleQueues)
        {
            var cells = new List<object>();

            for (var hour = 0; hour < 24; hour++)
            {
                if (!values.TryGetValue(new QueueHourKey(queue, hour), out var value) || value <= 0)
                {
                    continue;
                }

                if (value > max)
                {
                    max = value;
                }

                cells.Add(new { hour, value });
            }

            rows.Add(new
            {
                queue,
                isAdHoc = (bool?)null, // Phase 1 is cron-only; no ad-hoc/cron tag.
                cells
            });
        }

        return new
        {
            rows,
            hours = Enumerable.Range(0, 24).ToArray(),
            min = 0d,
            max,
            logScale = LogScale,
            colorMode = "ramp",
            metricLabel = MetricLabel,
            dayLabel = DayModeLabel
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
            await JS.InvokeVoidAsync("heatmapCharts.destroy", _containerId);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone.
        }
        catch (ObjectDisposedException)
        {
            // JS runtime disposed.
        }
    }
}
