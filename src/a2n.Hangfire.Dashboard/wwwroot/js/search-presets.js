(function () {
    'use strict';

    function getStorageKey(pathPrefix) {
        var prefix = (pathPrefix || '').replace(/^\/|\/$/g, '');
        return 'hangfire-dashboard:' + prefix + ':search-presets';
    }

    function isLocalStorageAvailable() {
        try {
            var testKey = '__ls_test__';
            localStorage.setItem(testKey, '1');
            localStorage.removeItem(testKey);
            return true;
        } catch (e) {
            return false;
        }
    }

    function readPresets(pathPrefix) {
        if (!isLocalStorageAvailable()) {
            return [];
        }
        try {
            var raw = localStorage.getItem(getStorageKey(pathPrefix));
            if (!raw) {
                return [];
            }
            var parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
        } catch (e) {
            return [];
        }
    }

    function writePresets(pathPrefix, presets) {
        if (!isLocalStorageAvailable()) {
            return false;
        }
        try {
            localStorage.setItem(getStorageKey(pathPrefix), JSON.stringify(presets));
            return true;
        } catch (e) {
            // Handles QuotaExceededError and other write failures
            return false;
        }
    }

    /**
     * Saves a preset to localStorage.
     * If a preset with the same name exists, it is overwritten.
     * @param {string} pathPrefix - Dashboard path prefix for isolation
     * @param {object} preset - The FilterPreset object to save
     * @returns {boolean} true if saved successfully, false otherwise
     */
    function savePreset(pathPrefix, preset) {
        if (!preset || !preset.name || !preset.name.trim()) {
            return false;
        }

        var presets = readPresets(pathPrefix);
        var trimmedName = preset.name.trim();
        var existingIndex = -1;

        for (var i = 0; i < presets.length; i++) {
            if (presets[i].name === trimmedName) {
                existingIndex = i;
                break;
            }
        }

        var presetToSave = Object.assign({}, preset, { name: trimmedName });

        if (existingIndex >= 0) {
            presets[existingIndex] = presetToSave;
        } else {
            presets.push(presetToSave);
        }

        return writePresets(pathPrefix, presets);
    }

    /**
     * Loads all presets for the given path prefix.
     * @param {string} pathPrefix - Dashboard path prefix for isolation
     * @returns {Array} Array of FilterPreset objects, or empty array if unavailable
     */
    function loadPresets(pathPrefix) {
        return readPresets(pathPrefix);
    }

    /**
     * Deletes a preset by name.
     * @param {string} pathPrefix - Dashboard path prefix for isolation
     * @param {string} presetName - Name of the preset to delete
     * @returns {boolean} true if deleted successfully, false otherwise
     */
    function deletePreset(pathPrefix, presetName) {
        if (!presetName || !presetName.trim()) {
            return false;
        }

        if (!isLocalStorageAvailable()) {
            return false;
        }

        var presets = readPresets(pathPrefix);
        var trimmedName = presetName.trim();
        var filtered = [];

        for (var i = 0; i < presets.length; i++) {
            if (presets[i].name !== trimmedName) {
                filtered.push(presets[i]);
            }
        }

        if (filtered.length === presets.length) {
            // Preset not found — still return true (idempotent delete)
            return true;
        }

        return writePresets(pathPrefix, filtered);
    }

    /**
     * Checks if a preset with the given name exists.
     * @param {string} pathPrefix - Dashboard path prefix for isolation
     * @param {string} presetName - Name to check
     * @returns {boolean} true if preset exists, false otherwise
     */
    function presetExists(pathPrefix, presetName) {
        if (!presetName || !presetName.trim()) {
            return false;
        }

        var presets = readPresets(pathPrefix);
        var trimmedName = presetName.trim();

        for (var i = 0; i < presets.length; i++) {
            if (presets[i].name === trimmedName) {
                return true;
            }
        }

        return false;
    }

    // Expose functions for Blazor JS interop via IJSRuntime
    window.searchPresets = {
        savePreset: savePreset,
        loadPresets: loadPresets,
        deletePreset: deletePreset,
        presetExists: presetExists
    };
})();
