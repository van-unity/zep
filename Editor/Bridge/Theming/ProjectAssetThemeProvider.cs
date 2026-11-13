using System.Collections.Generic;
using Zep.Core.Theming;
using UnityEditor;
using Zep.Core.Application;

namespace Zep.Editor.Bridge.Theming
{
    internal sealed class ProjectAssetThemeProvider : IZepThemeProvider
    {
        public const string UserThemesFolder = "Assets/Editor/Zep/UserThemes";

        public IEnumerable<ZepThemeDefinition> GetThemes()
        {
            // PERF-DIAG: this provider is enumerated lazily; the AssetDatabase.FindAssets
            // call below is what costs — it runs on every RefreshRegistry call.
            var __sw_find = System.Diagnostics.Stopwatch.StartNew();
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseFindAssetsCalls);
            var guids = AssetDatabase.FindAssets("t:" + nameof(ZepUserTheme));
            __sw_find.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ProjectAssetThemeProvider.FindAssets(t:ZepUserTheme) hits={guids.Length} ms={__sw_find.Elapsed.TotalMilliseconds:F2}");

            foreach (var guid in guids)
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<ZepUserTheme>(path);
                if (asset == null) continue;
                if (string.IsNullOrEmpty(asset.ThemeId)) continue;
                yield return asset.ToDefinition();
            }
        }
    }
}
