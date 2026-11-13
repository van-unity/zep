using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Theming;
using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{
    public sealed class ZepThemeMetadata
    {
        public string Id { get; }
        public string DisplayName { get; }
        public bool IsLight { get; }
        public string StyleSheetPath { get; }

        public ZepThemeMetadata(string id, string displayName, bool isLight, string styleSheetPath)
        {
            Id = id;
            DisplayName = displayName;
            IsLight = isLight;
            StyleSheetPath = styleSheetPath;
        }
    }

    public sealed class ZepThemeRegistry
    {
        public const string DefaultThemeId = "ZepDark";

        public static string GetThemeClassName(string themeId) => $"zep-theme-{themeId}";

        private readonly List<ZepThemeMetadata> _themes = new List<ZepThemeMetadata>();
        private readonly List<IZepThemeProvider> _providers = new List<IZepThemeProvider>();
        // Cached snapshot of AvailableDefinitions. Materializing the property
        // walks every provider, and ProjectAssetThemeProvider calls
        // AssetDatabase.FindAssets — that's ~20-30 ms per access on a real
        // project. The settings overlay, the theme switcher, and the registry's
        // own RefreshRegistry all read this on panel open, so we'd pay the
        // walk three times without a cache. Invalidated on every state change
        // that could affect the result (RegisterProvider, RefreshRegistry,
        // NotifyThemesChanged).
        private IReadOnlyList<ZepThemeDefinition> _cachedDefinitions;

        public IReadOnlyList<ZepThemeMetadata> AvailableThemes => _themes;

        public event Action ThemesChanged;

        public ZepThemeRegistry()
            : this(refreshImmediately: true)
        {
        }

        private ZepThemeRegistry(bool refreshImmediately)
        {
            if (refreshImmediately)
            {
                RefreshRegistry();
            }
        }

        internal static ZepThemeRegistry CreateInitial(string preferredThemeId)
        {
            var registry = new ZepThemeRegistry(refreshImmediately: false);
            var id = IsSafeThemeId(preferredThemeId)
                ? preferredThemeId
                : DefaultThemeId;
            registry._themes.Add(new ZepThemeMetadata(
                id,
                BuiltinThemeFiles.DeriveDisplayName(id),
                BuiltinThemeFiles.IsLightThemeId(id),
                BuiltinThemeFiles.ProjectRelativePathForId(id)));
            return registry;
        }

        private static bool IsSafeThemeId(string themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId))
            {
                return false;
            }

            for (var i = 0; i < themeId.Length; i++)
            {
                var character = themeId[i];
                if (!char.IsLetterOrDigit(character)
                    && character != '-'
                    && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        public void NotifyThemesChanged()
        {
            _cachedDefinitions = null;
            ThemesChanged?.Invoke();
        }

        internal void PublishRefreshedThemes()
        {
            ThemesChanged?.Invoke();
        }

        public void RefreshRegistry()
        {
            _cachedDefinitions = null;
            _themes.Clear();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var themeFile in BuiltinThemeFiles.Enumerate())
            {
                if (seenIds.Add(themeFile.Id))
                {
                    _themes.Add(new ZepThemeMetadata(
                        themeFile.Id,
                        themeFile.DisplayName,
                        themeFile.IsLight,
                        themeFile.ProjectRelativePath));
                }
            }

            foreach (var def in AvailableDefinitions)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                if (def.Source == ZepThemeSource.Builtin) continue;
                if (!seenIds.Add(def.Id)) continue;
                _themes.Add(new ZepThemeMetadata(def.Id, def.DisplayName, def.IsLight, string.Empty));
            }

            if (_themes.Count == 0)
            {
                _themes.Add(new ZepThemeMetadata(
                    DefaultThemeId,
                    BuiltinThemeFiles.DeriveDisplayName(DefaultThemeId),
                    BuiltinThemeFiles.IsLightThemeId(DefaultThemeId),
                    BuiltinThemeFiles.ProjectRelativePathForId(DefaultThemeId)));
            }
        }

        public ZepThemeMetadata GetTheme(string id)
        {
            var theme = _themes.FirstOrDefault(t => t.Id == id);
            return theme ?? _themes.FirstOrDefault(t => t.Id == DefaultThemeId) ?? _themes.FirstOrDefault();
        }

        public void RegisterProvider(IZepThemeProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (!_providers.Contains(provider))
            {
                _providers.Add(provider);
                // A newly registered provider can contribute definitions, so the memoized
                // snapshot is stale — drop it (the comment on _cachedDefinitions promises this).
                _cachedDefinitions = null;
            }
        }

        public IReadOnlyList<ZepThemeDefinition> AvailableDefinitions
        {
            get
            {
                if (_cachedDefinitions != null)
                {
                    return _cachedDefinitions;
                }

                var result = new List<ZepThemeDefinition>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var provider in _providers)
                {
                    try
                    {
                        var defs = provider.GetThemes() ?? Array.Empty<ZepThemeDefinition>();
                        foreach (var def in defs)
                        {
                            if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                            if (!seen.Add(def.Id)) continue;
                            result.Add(def);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Zep] Theme provider {provider.GetType().Name} threw: {ex.Message}");
                    }
                }
                _cachedDefinitions = result;
                return result;
            }
        }
    }
}
