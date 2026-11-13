using System.Collections.Generic;
using System.IO;

namespace Zep.Editor.Bridge.Theming
{
    // One built-in theme file: what its .uss name means.
    internal readonly struct BuiltinThemeFile
    {
        internal BuiltinThemeFile(
            string id,
            string displayName,
            bool isLight,
            string absolutePath,
            string projectRelativePath)
        {
            Id = id;
            DisplayName = displayName;
            IsLight = isLight;
            AbsolutePath = absolutePath;
            ProjectRelativePath = projectRelativePath;
        }

        internal string Id { get; }

        internal string DisplayName { get; }

        internal bool IsLight { get; }

        internal string AbsolutePath { get; }

        internal string ProjectRelativePath { get; }
    }

    // The single owner of "which .uss files are built-in themes and what their
    // id / display name / lightness are". Three call sites derived this
    // independently — ZepThemeRegistry.RefreshRegistry, its
    // CreateInitial fast path, and BuiltinThemeProvider — so a change to the
    // naming rule (or to the Defaults skip) had to be made in three places.
    internal static class BuiltinThemeFiles
    {
        internal const string StylesPath = "Packages/com.van-unity.zep/Editor/UI/Styles";
        internal const string ThemePrefix = "ZepTheme_";

        // The shared token sheet, not a selectable theme.
        private const string DefaultsFileName = ThemePrefix + "Defaults";

        internal static IEnumerable<BuiltinThemeFile> Enumerate()
        {
            var absoluteStylesPath = Path.GetFullPath(StylesPath);
            if (!Directory.Exists(absoluteStylesPath))
            {
                yield break;
            }

            foreach (var absolutePath in Directory.GetFiles(absoluteStylesPath, ThemePrefix + "*.uss"))
            {
                var fileName = Path.GetFileNameWithoutExtension(absolutePath);
                if (fileName == DefaultsFileName)
                {
                    continue;
                }

                var id = fileName.Substring(ThemePrefix.Length);
                yield return new BuiltinThemeFile(
                    id,
                    DeriveDisplayName(id),
                    IsLightThemeId(id),
                    absolutePath,
                    ProjectRelativePathForId(id));
            }
        }

        internal static string ProjectRelativePathForId(string themeId) =>
            $"{StylesPath}/{ThemePrefix}{themeId}.uss";

        internal static string DeriveDisplayName(string themeId) =>
            themeId.Replace("Dark", " (Dark)").Replace("Light", " (Light)");

        internal static bool IsLightThemeId(string themeId) => themeId.Contains("Light");
    }
}
