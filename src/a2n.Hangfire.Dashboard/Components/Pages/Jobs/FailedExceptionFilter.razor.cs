using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace a2n.Hangfire.Dashboard.Components.Pages.Jobs;

/// <summary>Where the Failed page's exception groups are in loading.</summary>
public enum FailureGroupLoadStatus { NotLoaded, Loading, Loaded, TimedOut, Failed }

public partial class FailedExceptionFilter
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>
    /// The options, read when needed: null for "All exceptions", then the labeled groups. A function
    /// rather than a value so a key press that loads the groups first sees them straight away.
    /// </summary>
    [Parameter, EditorRequired] public Func<IReadOnlyList<FailureGroupDto?>> GetOptions { get; set; } = default!;

    [Parameter] public FailureGroupLoadStatus Status { get; set; }

    /// <summary>Groups that are counted but not listed, because only the largest ones are labeled.</summary>
    [Parameter] public int UnlabeledCount { get; set; }

    [Parameter] public string? TimedOutMessage { get; set; }

    /// <summary>Whether the counts are lower bounds (only the latest failures were grouped).</summary>
    [Parameter] public bool IsPartial { get; set; }

    [Parameter] public string? SelectedFingerprint { get; set; }

    [Parameter] public string SelectedLabel { get; set; } = "All exceptions";

    /// <summary>Raised when the filter gets focus or opens, so the page can load the groups.</summary>
    [Parameter] public EventCallback OnOpen { get; set; }

    /// <summary>Raised with the chosen fingerprint, or null for "All exceptions".</summary>
    [Parameter] public EventCallback<string?> OnSelect { get; set; }

    /// <summary>Raised when the list opens or closes; the page refreshes the groups only while it is open.</summary>
    [Parameter] public EventCallback<bool> OnMenuOpenChanged { get; set; }

    private bool _menuOpen;

    // The option the arrow keys have moved to, by fingerprint (null is "All exceptions"), so it stays on
    // the same group if a refresh reorders the list. Its index is the fallback when that group is gone.
    private string? _activeFingerprint;
    private int _activeIndex;
    private bool _scrollActiveOption;

    private static string OptionId(int index) => $"exception-option-{index}";

    private int ActiveOptionIn(IReadOnlyList<FailureGroupDto?> options)
    {
        var index = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i]?.Fingerprint == _activeFingerprint)
            {
                index = i;
                break;
            }
        }

        // The group has gone in a refresh: stay at the same position, inside the list.
        return index >= 0 ? index : Math.Clamp(_activeIndex, 0, options.Count - 1);
    }

    private bool HasMenuStatus => Status is FailureGroupLoadStatus.Loading or FailureGroupLoadStatus.TimedOut or FailureGroupLoadStatus.Failed
        || (Status == FailureGroupLoadStatus.Loaded && UnlabeledCount > 0);

    private string? MenuStatusClass => !HasMenuStatus ? null : "px-3 py-2 border-top " + Status switch
    {
        FailureGroupLoadStatus.TimedOut => "text-warning-emphasis",
        FailureGroupLoadStatus.Failed => "text-danger",
        _ => "text-muted",
    };

    private static string ShortTypeName(FailureGroupDto group) => FailureGroupLabels.ShortTypeName(group);

    private static string FullTypeName(FailureGroupDto group) => FailureGroupLabels.FullTypeName(group);

    private string CountText(FailureGroupDto group) => FailureGroupLabels.CountText(group, IsPartial);

    private static string Truncate(string text, int length) => FailureGroupLabels.Truncate(text, length);

    private async Task SetMenuOpen(bool open)
    {
        if (_menuOpen == open)
            return;
        _menuOpen = open;
        await OnMenuOpenChanged.InvokeAsync(open);
    }

    private async Task ToggleMenu()
    {
        if (_menuOpen)
            await CloseMenu();
        else
            await OpenMenu();
    }

    private async Task OpenMenu()
    {
        _activeFingerprint = SelectedFingerprint;
        _activeIndex = 0;
        _scrollActiveOption = true;
        await SetMenuOpen(true);
        await OnOpen.InvokeAsync();
    }

    private Task CloseMenu() => SetMenuOpen(false);

    private async Task SelectGroup(string? fingerprint)
    {
        await SetMenuOpen(false);
        await OnSelect.InvokeAsync(fingerprint);
    }

    // The select-only combobox keys (WAI-ARIA APG). nav.js cancels the browser's own handling of the
    // keys used here, apart from Escape and Tab: Tab leaves the combobox, and focusout closes the list.
    private async Task HandleFilterKeyDown(KeyboardEventArgs e)
    {
        if (e.AltKey || e.CtrlKey || e.MetaKey)
            return;

        if (!_menuOpen)
        {
            if (e.Key is "ArrowDown" or "ArrowUp" or "Enter" or " " or "Home" or "End")
            {
                await OpenMenu();
                // After OpenMenu, which loads the groups the first time, so End can reach the last one.
                if (e.Key is "Home" or "End")
                {
                    var loaded = GetOptions();
                    MoveActiveOption(loaded, e.Key == "Home" ? 0 : loaded.Count - 1);
                }
            }
            return;
        }

        var options = GetOptions();
        var active = ActiveOptionIn(options);
        var last = options.Count - 1;
        switch (e.Key)
        {
            case "ArrowDown":
                MoveActiveOption(options, Math.Min(active + 1, last));
                break;
            case "ArrowUp":
                MoveActiveOption(options, Math.Max(active - 1, 0));
                break;
            case "Home":
                MoveActiveOption(options, 0);
                break;
            case "End":
                MoveActiveOption(options, last);
                break;
            case "Enter":
            case " ":
                await SelectGroup(options[active]?.Fingerprint);
                break;
            case "Escape":
                await CloseMenu();
                break;
        }
    }

    private void MoveActiveOption(IReadOnlyList<FailureGroupDto?> options, int index)
    {
        _activeFingerprint = options[index]?.Fingerprint;
        _activeIndex = index;
        _scrollActiveOption = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_scrollActiveOption || !_menuOpen)
            return;
        _scrollActiveOption = false;
        try
        {
            await JS.InvokeVoidAsync("hfListbox.scrollIntoView", OptionId(ActiveOptionIn(GetOptions())));
        }
        catch (Exception)
        {
            // Only keeps the option in view; the list still works without it.
        }
    }
}
