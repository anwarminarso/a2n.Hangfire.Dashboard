(function () {
    'use strict';

    // Generic, namespaced UI-preference store backed by localStorage.
    // Mirrors the pattern used by themeManager so dashboard components can persist small,
    // non-sensitive view preferences (toggles, expanded/collapsed panels, last-selected tabs).
    // Keys are prefixed to avoid colliding with host-app localStorage entries.

    var PREFIX = 'hf:';

    function safeGet(key) {
        try {
            return localStorage.getItem(PREFIX + key);
        } catch (e) {
            // localStorage can throw in private mode / when disabled — degrade gracefully.
            return null;
        }
    }

    function safeSet(key, value) {
        try {
            localStorage.setItem(PREFIX + key, value);
        } catch (e) {
            // Ignore quota / access errors; the preference simply won't persist.
        }
    }

    window.dashboardPrefs = {
        // Returns the stored string, or the provided fallback when absent.
        get: function (key, fallback) {
            var v = safeGet(key);
            return v === null ? (fallback === undefined ? null : fallback) : v;
        },
        set: function (key, value) {
            safeSet(key, value);
        },
        // Boolean convenience helpers. Stored as "1" / "0".
        getBool: function (key, fallback) {
            var v = safeGet(key);
            if (v === null) return !!fallback;
            return v === '1' || v === 'true';
        },
        setBool: function (key, value) {
            safeSet(key, value ? '1' : '0');
        }
    };
})();
