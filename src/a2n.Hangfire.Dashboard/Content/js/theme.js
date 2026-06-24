(function () {
    'use strict';

    var STORAGE_KEY = 'theme';

    function getSystemTheme() {
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    function resolve(theme) {
        return theme === 'auto' ? getSystemTheme() : theme;
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-bs-theme', resolve(theme));
    }

    // Re-applies the persisted preference. Used after Blazor enhanced navigations, which patch the
    // <html> element to match the freshly-served document — and the server does NOT render
    // data-bs-theme — so the attribute would otherwise be stripped and the page would revert to
    // light (Issue #20).
    function reapplyPersisted() {
        applyTheme(window.themeManager.get());
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
            applyTheme(this.get());

            // Listen for system theme changes when in auto mode
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
                if (window.themeManager.get() === 'auto') {
                    applyTheme('auto');
                }
            });
        }
    };

    // Apply theme immediately on load (before Blazor hydrates) to prevent FOUC
    window.themeManager.init();

    // --- Persistence across Blazor enhanced navigation (Issue #20) ---------------------------------
    // blazor.web.js performs enhanced navigation by default. It merges the newly-fetched document
    // into the live DOM, which resets <html> attributes to those of the server-rendered page. Since
    // the theme lives in localStorage (the server can't know it), data-bs-theme gets removed and the
    // dashboard flips back to light. We restore it on Blazor's enhanced-load event, with a
    // MutationObserver as a defensive fallback.

    function registerEnhancedNavHook(attemptsLeft) {
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            try {
                window.Blazor.addEventListener('enhancedload', reapplyPersisted);
            } catch (e) {
                // 'enhancedload' unsupported on this runtime — the observer fallback below covers us.
            }
            return;
        }
        if (attemptsLeft > 0) {
            setTimeout(function () { registerEnhancedNavHook(attemptsLeft - 1); }, 100);
        }
    }
    // Blazor loads after this script; poll briefly (~10s max) for the global to appear.
    registerEnhancedNavHook(100);

    // Fallback: if anything strips or blanks data-bs-theme on <html>, restore the persisted value.
    // Guarded so it only acts when the attribute is missing or no longer matches the resolved
    // preference, which prevents a feedback loop with our own setAttribute call (and with the
    // chart re-render observers that also watch data-bs-theme).
    var themeGuard = new MutationObserver(function () {
        var current = document.documentElement.getAttribute('data-bs-theme');
        var expected = resolve(window.themeManager.get());
        if (current !== expected) {
            document.documentElement.setAttribute('data-bs-theme', expected);
        }
    });
    themeGuard.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });
})();
