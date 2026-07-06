using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

/// <summary>
/// Per-queue small-multiples view: one <c>day × hour</c> heatmap per visible queue, all shaded with
/// a single shared color-ramp scale so that intensities are directly comparable across queues
/// (Requirement 3.7).
/// </summary>
/// <remarks>
/// <para>
/// The shared ramp domain is the matrix's <see cref="HeatmapMatrix.Min"/>/<see cref="HeatmapMatrix.Max"/>
/// — the global minimum and maximum cell value across the matrix — which the per-queue renderer uses
/// as the <c>[globalMin, globalMax]</c> domain for every small-multiple. This guarantees equal cell
/// values render at equal intensity in every small-multiple (validated by Property 11).
/// </para>
/// <para>
/// All shading, WCAG-contrast labels, tooltips, and keyboard navigation are performed by the
/// <c>window.heatmapCharts.renderPerQueue</c> renderer in <c>Content/js/heatmap.js</c>; this
/// component only maps the computed <see cref="HeatmapMatrix"/> into that renderer's model and
/// manages the JS lifecycle (mirroring the existing analytics chart interop pattern).
/// </para>
/// </remarks>
public partial class PerQueueView : ComponentBase, IAsyncDisposable
{
    private static readonly string[] WeekdayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private const int DayCount = 7;

    private readonly string _containerId = $"hf-heatmap-perqueue-{Guid.NewGuid():N}";

    private object _model;
    private bool _hasVisibleData;
    private bool _pendingRender;
    private bool _rendered;

    [Inject] private IJSRuntime JS { get; set; }

    /// <summary>The aggregated <c>queue × day × hour</c> matrix to render small-multiples for.</summary>
    [Parameter, EditorRequired] public HeatmapMatrix Matrix { get; set; }

    /// <summary>
    /// The queues to render, in display order. When <c>null</c>, every queue in
    /// <see cref="HeatmapMatrix.Queues"/> is shown. Filtering (e.g. Top-N) is applied by the caller.
    /// </summary>
    [Parameter] public IReadOnlyList<string> VisibleQueues { get; set; }

    /// <summary>Whether to apply logarithmic intensity scaling across the shared ramp domain.</summary>
    [Parameter] public bool LogScale { get; set; }

    private bool HasVisibleData => _hasVisibleData;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _model = BuildModel();
        // Re-render whenever the inputs change (matrix, visible queues, or log scale).
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
            if (_hasVisibleData && _model is not null)
            {
                await JS.InvokeVoidAsync("heatmapCharts.renderPerQueue", _containerId, _model);
                _rendered = true;
            }
            else if (_rendered)
            {
                // Visible data disappeared (e.g. queue filter cleared it) — tear down the prior grid.
                await JS.InvokeVoidAsync("heatmapCharts.destroy", _containerId);
                _rendered = false;
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected — nothing to render against.
        }
        catch (ObjectDisposedException)
        {
            // Component/JS runtime disposed during render.
        }
    }

    /// <summary>
    /// Maps the matrix into the <c>renderPerQueue</c> model:
    /// <c>{ queues:[{ queue, isAdHoc, queueColor, days, dayIndices, cells:[{day,hour,value}], max }],
    /// globalMin, globalMax, logScale, hours, metricLabel }</c>.
    /// </summary>
    private object BuildModel()
    {
        if (Matrix is null)
        {
            _hasVisibleData = false;
            return null;
        }

        var visibleQueues = (VisibleQueues ?? Matrix.Queues) ?? Array.Empty<string>();

        // Group populated cells by queue once for an order-independent, deterministic build.
        var byQueue = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var maxByQueue = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var cell in Matrix.Cells.Values)
        {
            if (cell.Value <= 0)
            {
                continue; // empty cells are simply absent (the renderer treats missing as zero).
            }

            var queue = cell.Key.Queue;
            if (!byQueue.TryGetValue(queue, out var list))
            {
                list = new List<object>();
                byQueue[queue] = list;
                maxByQueue[queue] = 0;
            }

            list.Add(new { day = cell.Key.DayIndex, hour = cell.Key.Hour, value = cell.Value });
            if (cell.Value > maxByQueue[queue])
            {
                maxByQueue[queue] = cell.Value;
            }
        }

        var (dayLabels, dayIndices) = BuildDays();
        var hours = Enumerable.Range(0, 24).ToArray();

        var queues = new List<object>(visibleQueues.Count);
        foreach (var queue in visibleQueues)
        {
            byQueue.TryGetValue(queue, out var cells);
            queues.Add(new
            {
                queue,
                isAdHoc = (bool?)null,        // Phase 1 projected/cron — no ad-hoc tag.
                queueColor = (string)null,    // null => renderer derives a stable color from the name.
                days = dayLabels,
                dayIndices,
                cells = (IReadOnlyList<object>)(cells ?? new List<object>()),
                max = cells is null ? 0d : maxByQueue[queue]
            });
        }

        _hasVisibleData = queues.Count > 0;

        return new
        {
            queues,
            // Shared global ramp domain across ALL visible queues (Req 3.7 / Property 11).
            globalMin = Matrix.Min,
            globalMax = Matrix.Max,
            logScale = LogScale,
            hours,
            metricLabel = MetricLabel(Matrix.Metric)
        };
    }

    /// <summary>
    /// Builds the seven day labels + indices for the window. The idealized week uses generic weekday
    /// names (day 0 = Monday); the next-seven-days window uses concrete dates derived from the
    /// window start so each column reads as a real calendar day.
    /// </summary>
    private (string[] Labels, int[] Indices) BuildDays()
    {
        var labels = new string[DayCount];
        var indices = new int[DayCount];
        var isCalendar = Matrix.Window?.Kind == ProjectionWindowKind.Next7Days;
        var start = Matrix.Window?.StartInclusive ?? default;

        for (var d = 0; d < DayCount; d++)
        {
            indices[d] = d;
            labels[d] = isCalendar
                ? start.AddDays(d).ToString("ddd d")
                : WeekdayLabels[d];
        }

        return (labels, indices);
    }

    private static string MetricLabel(LoadMetric metric) =>
        metric == LoadMetric.WorkerMinutes ? "Worker-minutes" : "Fire count";

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
            // JS runtime already disposed.
        }
    }
}
