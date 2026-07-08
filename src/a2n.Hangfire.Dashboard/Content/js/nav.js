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
