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
                if (value === 'true') return true;
                if (value === 'false') return false;
            } catch (e) { }
            return null;
        },
        setGroupExpanded: function (groupId, expanded) {
            try {
                localStorage.setItem(storageKey(groupId), expanded ? 'true' : 'false');
            } catch (e) { }
        }
    };
})();
