// Sidebar nav group expand/collapse persistence
(function () {
    'use strict';

    function storageKey(groupId) {
        return 'hf-nav-group:' + groupId;
    }

    window.hfNav = {
        getGroupExpanded: function (groupId) {
            try {
                var value = localStorage.getItem(storageKey(groupId));
                if (value === 'true') return 'true';
                if (value === 'false') return 'false';
            } catch (e) { }
            // Return an empty string (not null) so the .NET side can deserialize
            // into a reference type and avoid the Nullable<bool> conversion path
            // that throws InvalidCastException on some Microsoft.JSInterop versions.
            return '';
        },
        setGroupExpanded: function (groupId, expanded) {
            try {
                localStorage.setItem(storageKey(groupId), expanded ? 'true' : 'false');
            } catch (e) { }
        }
    };
})();

// Keyboard support for select-only comboboxes (the Exception filter on the Failed page).
// Focus stays on the combobox and Blazor moves the active option, but Blazor can only cancel a key's
// default for every key at once. That would also cancel Tab and trap focus, so the keys the list
// handles itself are cancelled here instead: arrows and Space would scroll the page, Enter submit.
(function () {
    'use strict';

    var handledKeys = { ArrowDown: 1, ArrowUp: 1, Home: 1, End: 1, Enter: 1, ' ': 1, Spacebar: 1 };

    document.addEventListener('keydown', function (e) {
        var target = e.target;
        if (target && target.closest && target.closest('[data-hf-listbox-keys]')
            && handledKeys[e.key] && !e.altKey && !e.ctrlKey && !e.metaKey) {
            e.preventDefault();
        }
    });

    window.hfListbox = {
        // Keeps the active option visible when the arrow keys move it past the list's scroll area.
        scrollIntoView: function (id) {
            var el = document.getElementById(id);
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'nearest' });
        }
    };
})();

// Focus handling for ConfirmModal, which renders Bootstrap's modal markup without Bootstrap's
// JavaScript. Opening a dialog moves focus into it and remembers where it was; closing gives it
// back. While one is open, Tab and Shift+Tab cycle through its controls instead of moving on to the
// page behind the backdrop. Escape is handled by the component.
(function () {
    'use strict';

    var tabbableSelector = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), '
        + 'select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    // The open dialogs, the most recent last: { id, dialog, previous }.
    var open = [];

    // The element that last had focus. A button that opens a dialog after some work may disable
    // itself meanwhile (the Failed page's Requeue all reads the group first), and a browser moves
    // focus off a button that becomes disabled, so when the dialog opens focus is on the body.
    var lastFocused = null;
    document.addEventListener('focusin', function (e) { lastFocused = e.target; });

    function current() {
        // A dialog that left the page without being closed (the circuit dropped) no longer counts.
        open = open.filter(function (entry) { return entry.dialog.isConnected; });
        return open.length ? open[open.length - 1] : null;
    }

    function tabbables(dialog) {
        return Array.prototype.filter.call(dialog.querySelectorAll(tabbableSelector), function (el) {
            return el.tabIndex >= 0 && el.getClientRects().length > 0;
        });
    }

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Tab' || e.altKey || e.ctrlKey || e.metaKey) return;
        var entry = current();
        if (!entry) return;

        var dialog = entry.dialog;
        var items = tabbables(dialog);
        if (items.length === 0) {
            e.preventDefault();
            dialog.focus();
            return;
        }

        // Focus on the dialog itself (a click on the backdrop puts it there) counts as before the
        // first control, so Tab moves to the first one and Shift+Tab wraps to the last.
        var active = document.activeElement;
        var inside = dialog.contains(active);
        var first = items[0];
        var last = items[items.length - 1];
        if (e.shiftKey && (!inside || active === dialog || active === first)) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && (!inside || active === last)) {
            e.preventDefault();
            first.focus();
        }
    });

    window.hfModal = {
        open: function (id, dialog, initial) {
            if (!dialog) return;
            var active = document.activeElement;
            var previous = active && active !== document.body ? active : lastFocused;
            open = open.filter(function (entry) { return entry.id !== id; });
            open.push({ id: id, dialog: dialog, previous: previous });
            (initial || dialog).focus();
        },
        close: function (id) {
            for (var i = open.length - 1; i >= 0; i--) {
                if (open[i].id !== id) continue;
                var previous = open[i].previous;
                open.splice(i, 1);
                // The button that opened the dialog may be gone (a deleted job's page) or disabled;
                // focus then stays where the browser put it.
                if (previous && previous.isConnected && typeof previous.focus === 'function') previous.focus();
                return;
            }
        }
    };
})();
