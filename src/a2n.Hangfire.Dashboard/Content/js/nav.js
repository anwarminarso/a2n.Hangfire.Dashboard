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
