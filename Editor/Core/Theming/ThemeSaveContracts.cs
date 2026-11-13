namespace Zep.Core.Theming
{
    // Where a user-created theme is persisted.
    public enum ThemeSaveMode
    {
        ProjectAsset,
        UserPrefs
    }

    // Outcome of a theme save attempt. Ok carries the saved theme id; Fail
    // carries a user-facing error message.
    public sealed class ThemeSaveResult
    {
        public bool Success { get; }
        public string Error { get; }
        internal string ThemeId { get; }

        // Recovered seam: callers (ThemeCreatorScreenModel) read the saved id as
        // SavedThemeId; it is the same value the constructor stores in ThemeId.
        public string SavedThemeId => ThemeId;

        private ThemeSaveResult(bool success, string themeId, string error)
        {
            Success = success;
            ThemeId = themeId ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public static ThemeSaveResult Ok(string themeId) => new ThemeSaveResult(true, themeId, null);
        public static ThemeSaveResult Fail(string error) => new ThemeSaveResult(false, null, error);
    }

    // Default accent for a light/dark mode, plus the id of the theme it was
    // derived from (null when it's the built-in fallback accent).
    public readonly struct ThemeDefaultAccent
    {
        public ZepColor Accent { get; }
        internal string SourceThemeId { get; }

        public ThemeDefaultAccent(ZepColor accent, string sourceThemeId)
        {
            Accent = accent;
            SourceThemeId = sourceThemeId;
        }
    }
}
