using a2n.Hangfire.Dashboard.Heatmap;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Heatmap;

public partial class Heatmap
{
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_enabled)
        {
            return;
        }

        // Push the collision-free queue→color map to heatmap.js whenever it changes so every renderer
        // (dots, legends, bubbles) agrees with the server-rendered chips/badges. Runs on every render
        // (not just the first) because the map changes when the available queues change (source/class).
        // A change forces one extra render so the child views re-render with the new colors.
        if (await PushQueueColorsAsync())
        {
            StateHasChanged();
        }

        if (!firstRender)
        {
            return;
        }

        // Register a DotNetObjectReference so heatmap.js cell clicks can invoke OpenCellDrawerAsync
        // (the working drill-down open mechanism). The call is best-effort and swallowed if the JS
        // is unavailable so it never disrupts the page.
        _selfRef ??= DotNetObjectReference.Create(this);
        try
        {
            await JS.InvokeVoidAsync("heatmapCharts.registerDrillDown", _selfRef);
        }
        catch (JSException)
        {
            // heatmapCharts not loaded yet — ignore.
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone — nothing to register against.
        }
        catch (InvalidOperationException)
        {
            // Prerendering (no JS interop available yet) — the next render will retry on the client.
        }

        // Seed the viewer time zone from the operator's last choice (localStorage), else the
        // browser's time zone, else the server's — then persist the seeded default so subsequent
        // loads follow it. Returns true when a reload was triggered (the tz changed).
        var reloaded = await SeedViewerTimeZoneAsync();

        // Re-render so the just-registered drill-down ref reaches first-paint cells (when the tz did
        // not change there was no reload to do it for us).
        if (!reloaded)
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// Pushes <see cref="_queueColorMap"/> to the JS renderer when it differs from the last push.
    /// Returns <c>true</c> when a push happened (so the caller can re-render the views with the new
    /// colors).
    /// </summary>
    private async Task<bool> PushQueueColorsAsync()
    {
        var sig = _queueColorMap is null
            ? string.Empty
            : string.Join(";", _queueColorMap.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

        if (string.Equals(sig, _pushedQueueColorsSig, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await JS.InvokeVoidAsync("heatmapCharts.setQueueColors", _queueColorMap);
            _pushedQueueColorsSig = sig;
            return true;
        }
        catch (JSException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the initial "View TZ" selection: a previously persisted choice wins; otherwise the
    /// browser's IANA time zone is used, falling back to the server's local zone, and that seeded
    /// default is persisted so later visits follow it. Returns <c>true</c> when the selection changed
    /// and a reload was issued.
    /// </summary>
    private async Task<bool> SeedViewerTimeZoneAsync()
    {
        string desired;
        try
        {
            // A stored value of null means "never chosen"; "" means the operator explicitly picked UTC.
            var stored = await JS.InvokeAsync<string>("dashboardPrefs.get", "heatmap:tz");
            if (stored is not null)
            {
                desired = NormalizeTimeZoneId(stored);
            }
            else
            {
                var browser = await JS.InvokeAsync<string>("dashboardPrefs.getBrowserTimeZone");
                desired = NormalizeTimeZoneId(browser);
                if (string.IsNullOrEmpty(desired))
                {
                    desired = NormalizeTimeZoneId(TimeZoneInfo.Local.Id);
                }

                await JS.InvokeVoidAsync("dashboardPrefs.set", "heatmap:tz", desired);
            }
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (JSException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        EnsureTimeZoneOption(desired);
        if (!string.Equals(desired, _viewerTzId, StringComparison.Ordinal))
        {
            _viewerTzId = desired;
            await LoadAsync();
            return true;
        }

        return false;
    }

    /// <summary>Persists the operator's explicit "View TZ" choice, then reloads.</summary>
    private async Task OnViewerTzChangedAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("dashboardPrefs.set", "heatmap:tz", _viewerTzId ?? string.Empty);
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone — the choice simply won't persist.
        }
        catch (JSException)
        {
            // localStorage unavailable — ignore.
        }

        await ReloadAsync();
    }

    /// <summary>
    /// Normalizes an IANA/Windows time-zone id to the canonical id used by the dropdown, or an empty
    /// string for UTC / unrecognized ids. Cross-platform resolution is handled by <see cref="HeatmapTime"/>.
    /// </summary>
    private static string NormalizeTimeZoneId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        if (!HeatmapTime.TryResolveTimeZone(id, out var tz) || tz.Equals(TimeZoneInfo.Utc))
        {
            return string.Empty;
        }

        return tz.Id;
    }

    /// <summary>
    /// Guarantees the "View TZ" dropdown contains an option for <paramref name="id"/> so a
    /// browser/server-detected zone (whose id format may differ from the system-zone list) is
    /// selectable. UTC ("" id) is always present as the first option.
    /// </summary>
    private void EnsureTimeZoneOption(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        if (_timeZones.Any(t => string.Equals(t.Id, id, StringComparison.Ordinal)))
        {
            return;
        }

        if (HeatmapTime.TryResolveTimeZone(id, out var tz) && !tz.Equals(TimeZoneInfo.Utc))
        {
            _timeZones.Insert(0, tz);
        }
    }

    public void Dispose() => _selfRef?.Dispose();

    private string JobClassTitle(string cls) => cls switch
    {
        "Cron" => "Recurring jobs projected from their cron expressions.",
        "Ad-hoc" => _historicalAvailable
            ? "On-demand background load from historical executions."
            : "Requires a storage metrics provider (SQL Server / PostgreSQL).",
        "Combined" => _historicalAvailable
            ? "Overlay of projected cron jobs on the ad-hoc demand profile."
            : "Requires a storage metrics provider (SQL Server / PostgreSQL).",
        _ => cls
    };

    private static List<TimeZoneInfo> LoadTimeZones()
    {
        try
        {
            return TimeZoneInfo.GetSystemTimeZones()
                .Where(tz => !string.Equals(tz.Id, TimeZoneInfo.Utc.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(tz => tz.BaseUtcOffset)
                .ThenBy(tz => tz.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<TimeZoneInfo>();
        }
    }
}
