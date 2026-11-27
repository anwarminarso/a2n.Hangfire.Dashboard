using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

public partial class Heatmap
{
    private bool IsCronClass => string.Equals(_jobClass, "Cron", StringComparison.OrdinalIgnoreCase);
    private bool IsCombinedClass => string.Equals(_jobClass, "Combined", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the Lookback control affects the current result: it drives the Historical aggregation
    /// and the ad-hoc demand profile (Ad-hoc / Combined classes). It is irrelevant — and disabled —
    /// for a pure Projected + Cron view, which is computed from cron expressions alone.
    /// </summary>
    private bool LookbackRelevant => _source == HeatmapSource.Historical || !IsCronClass;

    /// <summary>
    /// Whether the Demand statistic (Average / p95) affects the current result: only the ad-hoc demand
    /// profile (Ad-hoc / Combined classes) uses it. Disabled for the Cron class.
    /// </summary>
    private bool DemandRelevant => !IsCronClass;

    /// <summary>The operator's selected queues, in display order (intersection with available).</summary>
    private IReadOnlyList<string> SelectedQueuesList() =>
        _availableQueues.Where(_selectedQueues.Contains).ToList();

    /// <summary>
    /// The queue filter forwarded to the concurrency / recommendation analyses; <c>null</c> when every
    /// queue is selected so the service skips filtering entirely.
    /// </summary>
    private IReadOnlyList<string> QueueFilterList() =>
        _selectedQueues.Count > 0 && _selectedQueues.Count < _availableQueues.Count
            ? SelectedQueuesList()
            : null;

    /// <summary>
    /// Recomputes the available queues (cron matrix ∪ ad-hoc demand) and reconciles the selection:
    /// queues that are no longer available are dropped; queues that have <em>newly</em> become
    /// available (e.g. when switching back from a source/class that only exposed a subset) are
    /// auto-selected so a transient narrowing never silently hides queues; and an empty selection
    /// defaults to all (first load). Explicit deselections are preserved while the available set is
    /// unchanged.
    /// </summary>
    private void ReconcileQueueSelection()
    {
        var avail = new SortedSet<string>(StringComparer.Ordinal);
        if (_result?.Matrix.Queues is not null)
        {
            foreach (var q in _result.Matrix.Queues)
            {
                avail.Add(q);
            }
        }

        if (_demand?.Queues is not null)
        {
            foreach (var q in _demand.Queues)
            {
                avail.Add(q);
            }
        }

        _availableQueues = avail.ToList();

        // Auto-select any queue that wasn't available on the previous load (first load: every queue
        // is "new", so all start selected). This re-includes queues that reappear after a source/class
        // switch transiently removed them, rather than leaving them silently hidden.
        foreach (var q in avail)
        {
            if (!_previousAvailableQueues.Contains(q))
            {
                _selectedQueues.Add(q);
            }
        }

        // Drop selections that are no longer available.
        _selectedQueues.IntersectWith(avail);

        // Never leave the selection empty.
        if (_selectedQueues.Count == 0)
        {
            foreach (var q in avail)
            {
                _selectedQueues.Add(q);
            }
        }

        _previousAvailableQueues = new HashSet<string>(avail, StringComparer.Ordinal);
    }

    /// <summary>Returns the cron matrix restricted to the selected queues, with the ramp domain recomputed.</summary>
    private HeatmapMatrix BuildDisplayMatrix()
    {
        var m = _result?.Matrix;
        if (m is null)
        {
            return null;
        }

        var cells = new Dictionary<CellKey, HeatmapCell>();
        double min = 0, max = 0;
        var has = false;
        foreach (var kv in m.Cells)
        {
            if (!_selectedQueues.Contains(kv.Key.Queue))
            {
                continue;
            }

            cells[kv.Key] = kv.Value;
            if (!has)
            {
                min = max = kv.Value.Value;
                has = true;
            }
            else
            {
                if (kv.Value.Value < min) min = kv.Value.Value;
                if (kv.Value.Value > max) max = kv.Value.Value;
            }
        }

        var queues = m.Queues.Where(_selectedQueues.Contains).ToList();
        return m with { Cells = cells, Queues = queues, Min = min, Max = max };
    }

    /// <summary>Returns the demand profile restricted to the selected queues.</summary>
    private DemandProfile BuildDisplayDemand()
    {
        if (_demand is null)
        {
            return null;
        }

        var slots = new Dictionary<DemandSlotKey, double>();
        foreach (var kv in _demand.Slots)
        {
            if (_selectedQueues.Contains(kv.Key.Queue))
            {
                slots[kv.Key] = kv.Value;
            }
        }

        var queues = _demand.Queues.Where(_selectedQueues.Contains).ToList();
        return _demand with { Slots = slots, Queues = queues };
    }

    /// <summary>The class tag shown on a queue chip: cron, ad-hoc, or both.</summary>
    private string QueueClass(string queue)
    {
        var isCron = _result?.Matrix.Queues?.Contains(queue) == true;
        var isAdHoc = _demand?.Queues?.Contains(queue) == true;
        return isCron && isAdHoc ? "both" : isAdHoc ? "ad-hoc" : "cron";
    }

    /// <summary>The collision-free color for a queue, from the active map (fallback to its hashed color).</summary>
    private string QueueColorFor(string queue) =>
        _queueColorMap is not null && queue is not null && _queueColorMap.TryGetValue(queue, out var color)
            ? color
            : QueueColors.ColorFor(queue);

    /// <summary>Toggles a queue in/out of the selection (never empties it) and reloads.</summary>
    private async Task ToggleQueueAsync(string queue)
    {
        if (!_selectedQueues.Remove(queue))
        {
            _selectedQueues.Add(queue);
        }

        if (_selectedQueues.Count == 0)
        {
            _selectedQueues.Add(queue);
        }

        await LoadAsync();
    }

    /// <summary>Selects every available queue and reloads.</summary>
    private async Task SelectAllQueuesAsync()
    {
        foreach (var q in _availableQueues)
        {
            _selectedQueues.Add(q);
        }

        await LoadAsync();
    }

    /// <summary>
    /// Whether the Planner can render from the ad-hoc demand profile alone — i.e. the Ad-hoc/Combined
    /// class has demand data even though no recurring jobs populate the cron matrix. Lets the demand
    /// shading show instead of the "no recurring schedule" empty state in that case.
    /// </summary>
    private bool CanShowPlannerWithoutCron =>
        _view == "planner" && !IsCronClass && _demand is { Slots.Count: > 0 };

    private async Task OnCapacityChanged(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString() ?? string.Empty;
        _capacityInput = raw;

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Cleared — revert to the detected capacity (Req 5.1) and recompute the dependent
            // concurrency / recommendations / insights.
            _capacityInvalid = false;
            _manualCapacity = null;
            await LoadAsync();
            return;
        }

        if (CapacityResolver.TryValidateManual(raw, out var value))
        {
            _capacityInvalid = false;
            // Entering the detected value is not treated as an override (it stays "detected"),
            // so typing the same number the dashboard detected keeps the auto-detected semantics.
            _manualCapacity = value == _detectedCapacity ? (int?)null : value;
            await LoadAsync();
        }
        else
        {
            // Reject the value and retain the previously active capacity (Req 5.5).
            _capacityInvalid = true;
        }
    }
}
