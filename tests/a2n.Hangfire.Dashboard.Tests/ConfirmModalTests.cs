using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Xunit;
using a2n.Hangfire.Dashboard.Components.Shared;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// <see cref="ConfirmModal"/>'s keyboard and screen-reader behavior (#74): the dialog's ARIA, Escape,
/// and the hfModal calls that move focus into the dialog and give it back. The focus handling itself
/// runs in the browser (nav.js), so these tests check what the component asks of it.
/// </summary>
public class ConfirmModalTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private int _confirmed;
    private int _cancelled;

    public ConfirmModalTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<ConfirmModal> Render(bool visible)
        => _ctx.Render<ConfirmModal>(p => p
            .Add(c => c.Visible, visible)
            .Add(c => c.Title, "Confirm Requeue")
            .Add(c => c.Message, "Requeue 3 jobs?")
            .Add(c => c.OnConfirm, EventCallback.Factory.Create(this, () => _confirmed++))
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => _cancelled++)));

    private static string ReferenceIdOf(AngleSharp.Dom.IElement element)
        => element.GetAttribute("blazor:elementReference");

    [Fact]
    public void Dialog_IsModal_NamedByItsTitle_AndDescribedByItsMessage()
    {
        var cut = Render(visible: true);

        var dialog = cut.Find(".modal");
        Assert.Equal("dialog", dialog.GetAttribute("role"));
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));

        var title = cut.Find(".modal-title");
        Assert.False(string.IsNullOrEmpty(title.Id));
        Assert.Equal(title.Id, dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("Confirm Requeue", title.TextContent);

        var message = cut.Find(".modal-body p");
        Assert.False(string.IsNullOrEmpty(message.Id));
        Assert.Equal(message.Id, dialog.GetAttribute("aria-describedby"));
        Assert.Equal("Requeue 3 jobs?", message.TextContent);

        Assert.Equal("Close", cut.Find(".modal-header .btn-close").GetAttribute("aria-label"));
    }

    [Fact]
    public void TwoDialogs_OnOnePage_HaveTheirOwnIds()
    {
        var first = Render(visible: true);
        var second = Render(visible: true);

        Assert.NotEqual(first.Find(".modal-title").Id, second.Find(".modal-title").Id);
        Assert.NotEqual(first.Find(".modal-body p").Id, second.Find(".modal-body p").Id);
    }

    [Fact]
    public async Task Escape_Cancels_AndOtherKeysDont()
    {
        var cut = Render(visible: true);

        await cut.Find(".modal").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await cut.Find(".modal").KeyDownAsync(new KeyboardEventArgs { Key = "Tab" });
        Assert.Equal(0, _cancelled);

        await cut.Find(".modal").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, _cancelled);
        Assert.Equal(0, _confirmed);
    }

    [Fact]
    public void Opening_MovesFocusToCancel_AndClosing_GivesItBack()
    {
        var cut = Render(visible: false);
        _ctx.JSInterop.VerifyNotInvoke("hfModal.open");

        cut.Render(p => p.Add(c => c.Visible, true));

        var open = _ctx.JSInterop.VerifyInvoke("hfModal.open");
        var id = Assert.IsType<string>(open.Arguments[0]);
        Assert.Equal(ReferenceIdOf(cut.Find(".modal")), Assert.IsType<ElementReference>(open.Arguments[1]).Id);
        var cancel = cut.FindAll(".modal-footer button").Single(b => b.TextContent.Trim() == "Cancel");
        Assert.Equal(ReferenceIdOf(cancel), Assert.IsType<ElementReference>(open.Arguments[2]).Id);
        _ctx.JSInterop.VerifyNotInvoke("hfModal.close");

        // A re-render while open (the page's refresh) doesn't move focus again.
        cut.Render(p => p.Add(c => c.Message, "Requeue 4 jobs?"));
        _ctx.JSInterop.VerifyInvoke("hfModal.open", calledTimes: 1);

        cut.Render(p => p.Add(c => c.Visible, false));

        var close = _ctx.JSInterop.VerifyInvoke("hfModal.close");
        Assert.Equal(id, close.Arguments[0]);
    }

    [Fact]
    public async Task RemovingAnOpenDialog_GivesFocusBack()
    {
        var cut = Render(visible: true);
        var id = _ctx.JSInterop.VerifyInvoke("hfModal.open").Arguments[0];

        // As on the Failed page, where the group actions' dialog is removed rather than hidden.
        await _ctx.DisposeComponentsAsync();

        var close = _ctx.JSInterop.VerifyInvoke("hfModal.close");
        Assert.Equal(id, close.Arguments[0]);
    }

    [Fact]
    public async Task AHiddenDialog_NeverCallsTheFocusHelper()
    {
        var cut = Render(visible: false);
        cut.Render(p => p.Add(c => c.Message, "Something else"));
        await _ctx.DisposeComponentsAsync();

        Assert.Empty(_ctx.JSInterop.Invocations);
    }

    [Fact]
    public void AFailingFocusHelper_LeavesTheDialogWorking()
    {
        // What a browser still running a cached nav.js without hfModal reports.
        _ctx.JSInterop.SetupVoid("hfModal.open", _ => true).SetException(new JSException("Could not find 'hfModal.open'."));

        var cut = Render(visible: true);
        cut.FindAll(".modal-footer button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        Assert.Equal(1, _cancelled);
    }
}
