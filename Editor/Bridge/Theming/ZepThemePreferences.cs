using System;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{
    internal static class ZepThemePreferences
    {
        internal const string ThemeStringPreferenceKey = "Zep.Editor.UI.ThemeId";
        internal const string ThemeLegacyPreferenceKey = "Zep.Editor.UI.ThemeIsLight";
        internal const string AssetViewModePreferenceKey = "Zep.Editor.UI.AssetViewMode";
        internal const string AssetGridItemSizePreferenceKey = "Zep.Editor.UI.AssetGridItemSize";
        internal const string FolderPaneWidthPreferenceKey = "Zep.Editor.UI.FolderPaneWidth";
        internal const string SidebarVisiblePreferenceKey = "Zep.Editor.UI.SidebarVisible";

        internal static bool GetSidebarVisible()
        {
            return EditorPrefs.GetBool(SidebarVisiblePreferenceKey, true);
        }

        internal static void SetSidebarVisible(bool visible)
        {
            EditorPrefs.SetBool(SidebarVisiblePreferenceKey, visible);
        }

        internal static string GetTheme()
        {
            if (EditorPrefs.HasKey(ThemeStringPreferenceKey))
            {
                return EditorPrefs.GetString(ThemeStringPreferenceKey, ZepThemeRegistry.DefaultThemeId);
            }
            
            bool isLight = EditorPrefs.GetBool(ThemeLegacyPreferenceKey, false);
            return isLight ? "ZepLight" : "ZepDark";
        }

        internal static void SetTheme(string themeId, ZepThemeRegistry registry)
        {
            EditorPrefs.SetString(ThemeStringPreferenceKey, themeId);
            var theme = registry?.GetTheme(themeId);
            if (theme != null)
            {
                EditorPrefs.SetBool(ThemeLegacyPreferenceKey, theme.IsLight);
            }

            NotifyPersistedThemeChanged();
        }

        // The persisted theme id or a saved theme definition changed. Docked
        // pane windows own their registries, which only the window that made
        // the change refreshes; this is how the others hear about it.
        internal static event Action PersistedThemeChanged;

        internal static void NotifyPersistedThemeChanged() => PersistedThemeChanged?.Invoke();

        internal static int GetAssetGridItemSize()
        {
            return Mathf.Clamp(
                EditorPrefs.GetInt(AssetGridItemSizePreferenceKey, 136),
                104,
                188);
        }

        internal static void SetAssetGridItemSize(int assetGridItemSize)
        {
            EditorPrefs.SetInt(
                AssetGridItemSizePreferenceKey,
                Mathf.Clamp(assetGridItemSize, 104, 188));
        }

        internal static bool HasFolderPaneWidth()
        {
            return EditorPrefs.HasKey(FolderPaneWidthPreferenceKey);
        }

        internal static float GetFolderPaneWidth()
        {
            return Mathf.Max(140f, EditorPrefs.GetFloat(FolderPaneWidthPreferenceKey, 270f));
        }

        internal static void SetFolderPaneWidth(float folderPaneWidth)
        {
            EditorPrefs.SetFloat(FolderPaneWidthPreferenceKey, Mathf.Max(140f, folderPaneWidth));
        }
    }
}