(function () {
    'use strict';

    var STORAGE_KEY = 'theme';

    function getSystemTheme() {
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    function applyTheme(theme) {
        var resolved = theme === 'auto' ? getSystemTheme() : theme;
        document.documentElement.setAttribute('data-bs-theme', resolved);
    }

    window.themeManager = {
        get: function () {
            return localStorage.getItem(STORAGE_KEY) || 'auto';
        },
        set: function (theme) {
            localStorage.setItem(STORAGE_KEY, theme);
            applyTheme(theme);
        },
        apply: function (theme) {
            applyTheme(theme);
        },
        init: function () {
            var theme = this.get();
            applyTheme(theme);

            // Listen for system theme changes when in auto mode
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
                var current = window.themeManager.get();
                if (current === 'auto') {
                    applyTheme('auto');
                }
            });
        }
    };

    // Apply theme immediately on load (before Blazor hydrates) to prevent FOUC
    window.themeManager.init();
})();
