using System;
using System.Collections.Generic;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Theming;
using UnityEngine;

namespace Zep.Editor.UI.Theming
{
    internal enum ThemeCreatorMode
    {
        New,
        Edit,
    }

    // WHY: View-model seam between Core theming (ZepColor) and the Unity-bound
    // creator view (UnityEngine.Color for sliders, preview, USS rendering). All
    // Core calls happen with ZepColor and the results are converted back to Color
    // before the view sees them.
    internal sealed class ThemeCreatorScreenModel
    {
        private readonly IThemeSaveOrchestrator _orchestrator;

        private ThemeCreatorMode _mode;
        private string _themeName = string.Empty;
        private string _editingThemeId;
        private bool _isLight;
        private Color _accent;
        private Dictionary<string, Color> _variables;
        private bool _isDirty;
        private ThemeSaveMode _saveDestination = ThemeSaveMode.UserPrefs;

        public event Action Changed;

        public ThemeCreatorScreenModel(IThemeSaveOrchestrator orchestrator)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            LoadForNew(isLight: false);
        }

        public ThemeCreatorMode Mode => _mode;
        public string ThemeName => _themeName;
        public bool IsLight => _isLight;
        public Color Accent => _accent;
        public IReadOnlyDictionary<string, Color> Variables => _variables;
        public bool IsDirty => _isDirty;
        public ThemeSaveMode SaveDestination => _saveDestination;
        public bool ShowSaveDestinationToggle => _mode == ThemeCreatorMode.New;
        public bool CanSave => !string.IsNullOrWhiteSpace(_themeName);

        public void LoadForNew(bool isLight)
        {
            _mode = ThemeCreatorMode.New;
            _editingThemeId = null;
            _themeName = string.Empty;
            _isLight = isLight;
            var defaults = _orchestrator.GetDefaultAccentForMode(isLight);
            _accent = defaults.Accent.ToUnityColor();
            _saveDestination = ThemeSaveMode.UserPrefs;
            _variables = ThemeDerivation.Compute(defaults.Accent, _isLight).ToUnityColorDict();
            _isDirty = false;
            Raise();
        }

        public void LoadForEdit(ZepThemeDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            _mode = ThemeCreatorMode.Edit;
            _editingThemeId = definition.Id;
            _themeName = definition.DisplayName ?? definition.Id;
            _isLight = definition.IsLight;
            var accent = ThemeDerivation.ExtractAccent(definition.Variables);
            _accent = accent.ToUnityColor();
            _saveDestination = definition.Source == ZepThemeSource.ProjectAsset
                ? ThemeSaveMode.ProjectAsset
                : ThemeSaveMode.UserPrefs;
            _variables = ThemeDerivation.Compute(accent, _isLight).ToUnityColorDict();
            _isDirty = false;
            Raise();
        }

        public void SetThemeName(string name)
        {
            var normalized = name ?? string.Empty;
            if (string.Equals(_themeName, normalized, StringComparison.Ordinal)) return;
            _themeName = normalized;
            _isDirty = true;
            Raise();
        }

        public void SetIsLight(bool isLight)
        {
            if (_isLight == isLight) return;
            _isLight = isLight;
            var defaults = _orchestrator.GetDefaultAccentForMode(isLight);
            _accent = defaults.Accent.ToUnityColor();
            _variables = ThemeDerivation.Compute(defaults.Accent, _isLight).ToUnityColorDict();
            _isDirty = true;
            Raise();
        }

        public void SetAccent(Color accent)
        {
            if (ColorsEqual(_accent, accent)) return;
            _accent = accent;
            _variables = ThemeDerivation.Compute(accent.ToZepColor(), _isLight).ToUnityColorDict();
            _isDirty = true;
            Raise();
        }

        public void SetSaveDestination(ThemeSaveMode destination)
        {
            if (_saveDestination == destination) return;
            _saveDestination = destination;
            Raise();
        }

        public ThemeSaveResult Save()
        {
            if (!CanSave)
                return ThemeSaveResult.Fail("Name is required.");

            string targetId;
            if (_mode == ThemeCreatorMode.Edit && !string.IsNullOrEmpty(_editingThemeId))
            {
                targetId = _editingThemeId;
            }
            else
            {
                targetId = ThemeIdSanitizer.SanitizeToId(_themeName);
                if (string.IsNullOrEmpty(targetId))
                    return ThemeSaveResult.Fail("Name must contain at least one alphanumeric character.");
                if (_orchestrator.ThemeIdExists(targetId))
                    return ThemeSaveResult.Fail($"A theme named “{_themeName}” already exists. Pick a different name.");
            }

            var result = _orchestrator.Save(targetId, _themeName, _isLight, _variables.ToZepColorDict(), _saveDestination);
            if (!result.Success)
                return result;

            _isDirty = false;
            _editingThemeId = result.SavedThemeId;
            _mode = ThemeCreatorMode.Edit;
            Raise();
            return result;
        }

        private void Raise() => Changed?.Invoke();

        private static bool ColorsEqual(Color a, Color b)
        {
            const float eps = 0.0005f;
            return Mathf.Abs(a.r - b.r) < eps
                && Mathf.Abs(a.g - b.g) < eps
                && Mathf.Abs(a.b - b.b) < eps
                && Mathf.Abs(a.a - b.a) < eps;
        }
    }
}
