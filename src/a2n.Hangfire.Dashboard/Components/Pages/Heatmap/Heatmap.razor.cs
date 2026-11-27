using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

public partial class Heatmap
{
    private static readonly string[] JobClasses = { "Cron", "Ad-hoc", "Combined" };
    private static readonly string[] DayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private static readonly string[] DowAbbrev = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    private static readonly (string Key, string Label)[] Views =
    {
        ("planner", "Planner"),
        ("punch", "Punchcard"),
        ("qh", "Queue × Hour"),
        ("multi", "Per-queue"),
        ("cal", "Calendar"),
        ("conc", "Concurrency"),
        ("rec", "Recommendations"),
    };

    // Gate captured once so the decision is stable for the page lifetime.
    private bool _enabled;
    private bool _historicalAvailable;

    // Toolbar state — seeded from Heatmap_Options on init (Req 22.4, 22.5).
    private HeatmapSource _source = HeatmapSource.Projected;
    private ProjectionWindowKind _windowKind = ProjectionWindowKind.IdealizedWeek;
    private string _viewerTzId = string.Empty; // empty => UTC (Req 8.5)
    private string _jobClass = "Cron";
    private LoadMetric _loadMetric = LoadMetric.FireCount;
    private bool _hideSubHourly;
    private bool _logScale;
    private string _aggregationStatistic = "Average";
    private int _lookbackWeeks = 4;

    // Capacity input + validation (Req 5.2, 5.3, 5.5).
    private string _capacityInput = string.Empty;
    private int? _manualCapacity;
    private bool _capacityInvalid;
    private CapacityResult _capacity = new(1, CapacitySource.Detected);
    private int _detectedCapacity = 1;

    // View selector state.
    private string _view = "planner";
    private int _selectedDay = -1;
    private string _colorBy = "volume";

    // Loaded data + freshness (Req 24.3).
    private HeatmapResult? _result;
    private bool _loading = true;
    private DateTime? _loadedAtUtc;
    private bool _historicalErrorDismissed;

    // Demand-aware availability (a metrics provider + demand profile provider are registered).
    private bool _demandAvailable;

    // Auxiliary data feeding the views (task 16.x, 18.6, 13.5, 11.6). Loaded alongside the matrix
    // (specs / demand / historical cells) or lazily per active view (concurrency / recommendations).
    private IReadOnlyList<RecurringJobSpec> _specs = Array.Empty<RecurringJobSpec>();
    private DemandProfile? _demand;
    private IReadOnlyList<HeatmapHistoricalCell> _historicalCells = Array.Empty<HeatmapHistoricalCell>();
    private IReadOnlyList<Recommendation> _recommendations = Array.Empty<Recommendation>();
    private ConcurrencyResult? _concurrency;
    private int _concurrencyDay;
    private int _concDay = -1; // -1 = auto (worst day); 0..6 = a specific window day.
    private IReadOnlyList<int>? _concurrencyBaseline;

    // Queue filter chips (mirrors the v4 mockup): the queues available (cron ∪ ad-hoc) and the
    // operator's current selection. The views, analyses, and insights are scoped to the selection.
    private IReadOnlyList<string> _availableQueues = Array.Empty<string>();
    private readonly HashSet<string> _selectedQueues = new(StringComparer.Ordinal);

    // The queues that were available on the previous reconcile, so newly-appearing queues can be
    // auto-selected (and a transient source/class change never silently hides queues).
    private HashSet<string> _previousAvailableQueues = new(StringComparer.Ordinal);
    private HeatmapMatrix? _displayMatrix;
    private DemandProfile? _displayDemand;

    // Collision-free queue → color map over the available queues, shared by the chips, the recurring
    // table badges, the recommendation dots, and (pushed to heatmap.js) every renderer's dots/legends.
    private IReadOnlyDictionary<string, string> _queueColorMap = new Dictionary<string, string>();
    private string? _pushedQueueColorsSig;

    // Insight summaries shown in the top cards (mirrors the v4 mockup's insights bar).
    private string? _insightBestWindow;
    private string? _insightAdhocPeak;
    private string? _insightAdhocWhen;
    private string? _insightPeakWhen;
    private bool _insightPeakOver;

    // CSV export state (task 19.4). _exporting guards re-entrancy while the file is generated and
    // handed to the browser; _exportError surfaces a non-blocking notice when the download fails.
    private bool _exporting;
    private string? _exportError;

    // Drill-down drawer state (task 20.2). The drawer is driven entirely from here.
    private DotNetObjectReference<Heatmap>? _selfRef;
    private DrillDownResult? _drillDown;
    private bool _drawerVisible;
    private bool _drillDownLoading;
    private string? _drawerCellLabel;
    private string? _drawerSaveError;

    private List<TimeZoneInfo> _timeZones = new();

    private bool ShowDaySelector => _view is "qh";
    private bool ShowColorBy => _view == "cal" && _source == HeatmapSource.Historical;

    /// <summary>Whether the active view renders cells that open the drill-down on click.</summary>
    private bool CellClickAvailable => _view is "planner" or "punch" or "multi";
    private string CurrentViewLabel => Views.FirstOrDefault(v => v.Key == _view).Label ?? "View";

    protected override async Task OnInitializedAsync()
    {
        _enabled = Options.Heatmap?.Enabled ?? false;
        if (!_enabled)
        {
            // Behave as if the route does not exist: bounce to the dashboard home (Req 22.3).
            Navigation.NavigateTo("", replace: true);
            return;
        }

        _historicalAvailable = HeatmapSvc.IsHistoricalAvailable;
        _demandAvailable = HeatmapSvc.IsDemandAvailable;
        _timeZones = LoadTimeZones();
        ApplyDefaults();

        await LoadAsync();
    }

    /// <summary>Seeds the toolbar selections from <see cref="HeatmapOptions"/> (Req 22.4, 22.5).</summary>
    private void ApplyDefaults()
    {
        var o = Options.Heatmap ?? new HeatmapOptions();

        _windowKind = o.DefaultProjectionWindow;
        _loadMetric = o.DefaultLoadMetric;
        _aggregationStatistic = string.IsNullOrWhiteSpace(o.DefaultAggregationStatistic) ? "Average" : o.DefaultAggregationStatistic;
        _lookbackWeeks = o.DefaultLookbackWeeks is 1 or 4 or 8 ? o.DefaultLookbackWeeks : 4;

        // Job_Class default (Req 16.2, 16.6, 16.7, 16.9):
        //   • The Ad-hoc and Combined classes are enabled together only when a metrics provider is
        //     registered; without one we disable them and default to Cron on the Projected source
        //     (Req 16.7, 16.9).
        //   • When a provider IS registered, Combined is the default class (Req 16.2). The storage-
        //     agnostic "Cron" option default (HeatmapOptions.DefaultJobClass) is therefore upgraded to
        //     Combined here, while an explicitly configured "Ad-hoc" is honored.
        //   • Ad-hoc demand only exists historically, so the Ad-hoc class forces the Historical source
        //     (Req 16.6); Combined overlays projected cron on the demand profile, so it starts on the
        //     Projected source.
        var configuredClass = string.IsNullOrWhiteSpace(o.DefaultJobClass) ? "Cron" : o.DefaultJobClass.Trim();
        var normalizedClass = JobClasses.FirstOrDefault(c => string.Equals(c, configuredClass, StringComparison.OrdinalIgnoreCase)) ?? "Cron";

        if (!_historicalAvailable)
        {
            // No provider: Ad-hoc / Combined are unavailable — default to Cron on Projected (Req 16.7, 16.9).
            _jobClass = "Cron";
            _source = HeatmapSource.Projected;
        }
        else
        {
            // Provider registered: Combined is the default (Req 16.2). Only an explicit "Ad-hoc"
            // configuration opts out of the Combined default.
            _jobClass = normalizedClass == "Ad-hoc" ? "Ad-hoc" : "Combined";
            _source = _jobClass == "Ad-hoc" ? HeatmapSource.Historical : HeatmapSource.Projected;
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        StateHasChanged();

        try
        {
            var query = BuildQuery();
            _result = await HeatmapSvc.GetMatrixAsync(query, CancellationToken.None);
            _capacity = HeatmapSvc.ResolveCapacity(_manualCapacity);
            _detectedCapacity = HeatmapSvc.ResolveCapacity(null).Capacity;

            // Reflect the effective worker count in the input box. While no manual override is active
            // the box shows the detected value (so the operator sees e.g. "20", matching the badge);
            // a manual override keeps whatever the operator typed.
            if (_manualCapacity is null)
            {
                _capacityInput = _capacity.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            _loadedAtUtc = DateTime.UtcNow;
            _historicalErrorDismissed = false;

            // Shared auxiliary data (used by several views):
            //  • the recurring-job specs back the recurring-job table (Req 9.7);
            //  • the ad-hoc demand profile shades the Planner (Req 18.1) for Ad-hoc/Combined classes;
            //  • per-cell historical stats drive the failure/duration coloring under the Historical
            //    source (Req 6.3/6.4/7.6).
            _specs = await HeatmapSvc.GetRecurringJobSpecsAsync(query, CancellationToken.None);

            _demand = (!IsCronClass && _demandAvailable)
                ? HeatmapSvc.GetDemandProfile(query)
                : null;

            _historicalCells = _source == HeatmapSource.Historical
                ? await HeatmapSvc.GetHistoricalCellsAsync(query, CancellationToken.None)
                : Array.Empty<HeatmapHistoricalCell>();

            // Reconcile the queue selection against the now-known available queues, then build the
            // queue-filtered matrix/demand the views and insights render from.
            ReconcileQueueSelection();
            _queueColorMap = QueueColors.BuildColorMap(_availableQueues);
            _displayMatrix = BuildDisplayMatrix();
            _displayDemand = BuildDisplayDemand();

            // Concurrency (worst day), recommendations, and the insight summaries are computed up
            // front because the always-visible insights bar surfaces them; the views then read the
            // already-computed values (no per-view recompute on switch).
            await ComputeViewDataAndInsightsAsync(query);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Computes the worst-day concurrency series, the stagger recommendations, and the four insight
    /// summaries (best window, ad-hoc demand peak, peak combined concurrency, overlap count) for the
    /// active query. Reuses the demand-aware baselines so the Concurrency view and the insights agree.
    /// </summary>
    private async Task ComputeViewDataAndInsightsAsync(HeatmapQuery query)
    {
        if (_result is null)
        {
            _concurrency = null;
            _recommendations = Array.Empty<Recommendation>();
            _insightBestWindow = _insightAdhocPeak = _insightAdhocWhen = _insightPeakWhen = null;
            _insightPeakOver = false;
            return;
        }

        var tz = HeatmapTime.ResolveTimeZone(_viewerTzId);
        var window = _result.Matrix.Window;
        var queueFilter = QueueFilterList();

        _concurrencyDay = _concDay >= 0
            ? Math.Clamp(_concDay, 0, HeatmapTime.WindowDays - 1)
            : await HeatmapSvc.GetWorstConcurrencyDayAsync(query, _capacity.Capacity, CancellationToken.None, queueFilter);
        _concurrencyBaseline = BuildConcurrencyBaseline(query, window, tz, _concurrencyDay);
        _concurrency = await HeatmapSvc.GetConcurrencyAsync(
            query, _concurrencyDay, _capacity.Capacity, _concurrencyBaseline, CancellationToken.None, queueFilter);

        var weekBaseline = BuildWeekAdHocBaseline(query, window, tz);
        _recommendations = await HeatmapSvc.GetRecommendationsAsync(
            query, _capacity.Capacity, weekBaseline, CancellationToken.None, queueFilter);

        ComputeInsights(window, tz);
    }

    /// <summary>Derives the four insight-card summaries from the loaded matrix, demand, and concurrency.</summary>
    private void ComputeInsights(ProjectionWindow window, TimeZoneInfo tz)
    {
        var matrix = _displayMatrix ?? _result!.Matrix;
        var demand = _displayDemand;

        // Best window to schedule = lowest combined (ad-hoc + cron) load slot (Req 18.6).
        var planner = PlannerHelpers.BuildPlanner(matrix, demand, window, tz, 0d);
        _insightBestWindow = planner.BestWindow is null
            ? null
            : DayHourLabel(planner.BestWindow.DayIndex, planner.BestWindow.Hour);

        // Ad-hoc demand peak across day-of-week × hour, summed over queues.
        _insightAdhocPeak = null;
        _insightAdhocWhen = null;
        if (demand?.Slots is { Count: > 0 })
        {
            var byDow = new Dictionary<(int Dow, int Hour), double>();
            foreach (var slot in demand.Slots)
            {
                var key = (slot.Key.DayOfWeek, slot.Key.Hour);
                byDow.TryGetValue(key, out var existing);
                byDow[key] = existing + slot.Value;
            }

            var peak = byDow.OrderByDescending(e => e.Value).FirstOrDefault();
            if (peak.Value > 0)
            {
                var unit = _loadMetric == LoadMetric.WorkerMinutes ? " wm/h" : " fires/h";
                _insightAdhocPeak = peak.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + unit;
                _insightAdhocWhen = $"{DowAbbrev[Math.Clamp(peak.Key.Dow, 0, 6)]} {peak.Key.Hour:00}:00";
            }
        }

        // Peak combined concurrency time + over-capacity flag.
        _insightPeakWhen = _concurrency?.PeakMinuteOfDay is int minute
            ? $"{DayLabelFor(_concurrencyDay)} {minute / 60:00}:{minute % 60:00}"
            : null;
        _insightPeakOver = _concurrency is not null && _concurrency.PeakConcurrency > _capacity.Capacity;
    }

    private HeatmapQuery BuildQuery() => new(
        Source: _source,
        WindowKind: _windowKind,
        ViewerTimeZoneId: _viewerTzId,
        JobClass: _jobClass,
        LoadMetric: _loadMetric,
        TopN: 100,
        HideSubHourly: _hideSubHourly,
        LogScale: _logScale,
        LookbackWeeks: _lookbackWeeks,
        AggregationStatistic: _aggregationStatistic,
        ManualCapacity: _manualCapacity);

    /// <summary>Manual refresh action — recomputes and re-renders the current data (Req 24.2).</summary>
    private Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Exports the currently displayed matrix as a downloadable CSV, scoped by the active source,
    /// projection window, viewer time zone, queue selection, and load metric (Req 12.1). The matrix is
    /// serialized by the pure <see cref="CsvExporter"/> via <see cref="HeatmapService.ExportCsv"/>, then
    /// the resulting text is saved client-side through the eval-free <c>dashboardFile.downloadText</c>
    /// helper (Content/js/prefs.js) — a small Blob + transient <c>&lt;a download&gt;</c> approach, since
    /// no existing dashboard download endpoint or helper was present and heatmap.js is locked. The file
    /// is named <c>heatmap-{source}-{window}.csv</c>.
    /// </summary>
    private async Task ExportCsvAsync()
    {
        _exportError = null;

        var matrix = _result?.Matrix;
        if (matrix is null)
        {
            return;
        }

        _exporting = true;
        StateHasChanged();

        try
        {
            // Scope the export to the active source / window / viewer tz / queue selection / load
            // metric (Req 12.1). The matrix already carries the window it was computed over, so the
            // exported day labels match the rendered grid exactly.
            var context = new CsvExportContext(
                Source: _source,
                Window: matrix.Window,
                ViewerTimeZoneId: _viewerTzId,
                Queues: matrix.Queues,
                Metric: matrix.Metric);

            var csv = HeatmapSvc.ExportCsv(matrix, context);
            var fileName = BuildExportFileName();

            var ok = await JS.InvokeAsync<bool>("dashboardFile.downloadText", fileName, csv, "text/csv");
            if (!ok)
            {
                _exportError = "The CSV could not be downloaded by the browser.";
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone — nothing more to do; the page state is unaffected.
        }
        catch (Exception ex)
        {
            _exportError = "The CSV export failed: " + ex.Message;
        }
        finally
        {
            _exporting = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Builds a sensible export file name of the form <c>heatmap-{source}-{window}.csv</c>, e.g.
    /// <c>heatmap-projected-idealized-week.csv</c>.
    /// </summary>
    private string BuildExportFileName()
    {
        var source = _source == HeatmapSource.Historical ? "historical" : "projected";
        var window = _windowKind == ProjectionWindowKind.Next7Days ? "next-7-days" : "idealized-week";
        return $"heatmap-{source}-{window}.csv";
    }

    /// <summary>Reloads after a control that changes the computed data is toggled.</summary>
    private Task ReloadAsync() => LoadAsync();

    private async Task SetJobClassAsync(string cls)
    {
        if (!_historicalAvailable && cls != "Cron")
        {
            return; // Ad-hoc / Combined require a metrics provider.
        }

        if (_jobClass == cls)
        {
            return;
        }

        _jobClass = cls;

        // Ad-hoc demand only exists historically; force the Historical source for Ad-hoc (Req 16.6).
        if (cls == "Ad-hoc" && _historicalAvailable)
        {
            _source = HeatmapSource.Historical;
        }

        await LoadAsync();
    }

    private async Task SetSourceAsync(HeatmapSource source)
    {
        if (_source == source)
        {
            return;
        }

        _source = source;
        await LoadAsync();
    }

    private void SetView(string view) => _view = view;
}
