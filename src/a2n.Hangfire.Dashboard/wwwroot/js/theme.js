window.themeManager = {
    get: function () {
        return localStorage.getItem('theme') || 'auto';
    },
    set: function (theme) {
        localStorage.setItem('theme', theme);
        this.apply(theme);
    },
    apply: function (theme) {
        const root = document.documentElement;
        root.removeAttribute('data-theme');

        if (theme === 'light') {
            root.setAttribute('data-theme', 'light');
        } else if (theme === 'dark') {
            root.setAttribute('data-theme', 'dark');
        }
        // 'auto' = no attribute, uses @media prefers-color-scheme
    },
    init: function () {
        const theme = this.get();
        this.apply(theme);
    }
};

// Apply theme immediately on load (before Blazor hydrates)
window.themeManager.init();
