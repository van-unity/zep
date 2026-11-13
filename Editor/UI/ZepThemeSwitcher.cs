using System;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Theming;
using UnityEditor;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI
{
    internal sealed class ZepThemeSwitcher
    {
        private readonly VisualElement _target;
        private readonly ZepThemeRegistry _registry;
        private readonly UnityZepIconRepository _iconRepository;
        private readonly Action<string> _onCurrentThemeChanged;

        private string _currentTheme;
        private StyleSheet _activeThemeStyleSheet;
        private bool _activeThemeSheetIsRuntime;

        public string CurrentTheme => _currentTheme;

        public ZepThemeSwitcher(
            VisualElement target,
            ZepThemeRegistry registry,
            UnityZepIconRepository iconRepository,
            Action<string> onCurrentThemeChanged)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _iconRepository = iconRepository;
            _onCurrentThemeChanged = onCurrentThemeChanged;
            _currentTheme = ZepThemePreferences.GetTheme();
            if (!CurrentThemeStillExists())
                _currentTheme = ZepThemeRegistry.DefaultThemeId;
        }

        public bool CurrentThemeStillExists()
        {
            if (string.IsNullOrEmpty(_currentTheme)) return false;
            foreach (var t in _registry.AvailableThemes)
            {
                if (string.Equals(t.Id, _currentTheme, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // Answers "would re-applying this theme change anything?" for callers that
        // re-run the current theme on an unrelated trigger (the registry republishes
        // ThemesChanged on every theme asset import). Identity of the freshly loaded
        // sheet — not the id — is the test: a reimport hands us a NEW StyleSheet
        // object while the attached one is stale, and answering "up to date" there
        // would leave the panel wearing a dead sheet. Definition (runtime) themes are
        // rebuilt from their variables on every apply and are never up to date here.
        internal bool IsAppliedThemeUpToDate(string themeId)
        {
            if (_activeThemeStyleSheet == null || _activeThemeSheetIsRuntime)
                return false;
            if (!string.Equals(themeId, _currentTheme, StringComparison.Ordinal))
                return false;
            var theme = _registry.GetTheme(themeId);
            if (theme == null || string.IsNullOrEmpty(theme.StyleSheetPath))
                return false;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(theme.StyleSheetPath);
            return ReferenceEquals(sheet, _activeThemeStyleSheet);
        }

        public void SetTheme(string themeId, bool persistPreference)
        {
            if (string.IsNullOrWhiteSpace(themeId))
                themeId = ZepThemeRegistry.DefaultThemeId;
            var theme = _registry.GetTheme(themeId);
            if (theme != null)
                themeId = theme.Id;
            if (theme != null && !string.IsNullOrEmpty(theme.StyleSheetPath))
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(theme.StyleSheetPath);
                ApplyThemeSheet(themeId, sheet, persistPreference, isRuntime: false);
                return;
            }
            foreach (var def in _registry.AvailableDefinitions)
            {
                if (string.Equals(def.Id, themeId, StringComparison.Ordinal))
                {
                    SetTheme(def, persistPreference);
                    return;
                }
            }
            ApplyThemeSheet(themeId, null, persistPreference, isRuntime: false);
        }

        public void SetTheme(ZepThemeDefinition definition, bool persistPreference)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            var sheet = RuntimeThemeStyleSheetBuilder.Build(definition.Id, definition.Variables.ToUnityColorDict());
            ApplyThemeSheet(definition.Id, sheet, persistPreference, isRuntime: sheet != null);
        }

        // Runtime theme sheets are native Unity objects, so every owning window
        // must explicitly release its final sheet when the window is destroyed.
        public void Teardown()
        {
            if (_activeThemeStyleSheet == null)
                return;
            _target.styleSheets.Remove(_activeThemeStyleSheet);
            if (_activeThemeSheetIsRuntime)
                Object.DestroyImmediate(_activeThemeStyleSheet);
            _activeThemeStyleSheet = null;
        }

        // Test-only counter: how many times a theme sheet was actually swapped in.
        internal static int ApplyThemeSheetCountForTests;

        private void ApplyThemeSheet(string themeId, StyleSheet sheet, bool persistPreference, bool isRuntime)
        {
            ApplyThemeSheetCountForTests++;
            var previousTheme = _currentTheme;
            var previousSheet = _activeThemeStyleSheet;
            var previousWasRuntime = _activeThemeSheetIsRuntime;
            _currentTheme = themeId;
            if (previousSheet != null)
                _target.styleSheets.Remove(previousSheet);
            if (!string.IsNullOrEmpty(previousTheme))
                _target.RemoveFromClassList(ZepThemeRegistry.GetThemeClassName(previousTheme));
            _target.AddToClassList(ZepThemeRegistry.GetThemeClassName(themeId));
            _activeThemeStyleSheet = sheet;
            _activeThemeSheetIsRuntime = isRuntime;
            if (_activeThemeStyleSheet != null)
                _target.styleSheets.Add(_activeThemeStyleSheet);
            if (previousWasRuntime && previousSheet != null && previousSheet != _activeThemeStyleSheet)
                Object.DestroyImmediate(previousSheet);
            // WHY: Unity's built-in icons (EditorGUIUtility.IconContent) ship dark/light
            // variants that flip with the editor skin, not with our theme. Drop the
            // cached lookups so the next render re-fetches the current variant.
            _iconRepository?.InvalidateThemeCache();
            if (persistPreference)
                ZepThemePreferences.SetTheme(themeId, _registry);
            _onCurrentThemeChanged?.Invoke(_currentTheme);
        }
    }
}
