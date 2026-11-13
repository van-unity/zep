namespace Zep.Editor.UI
{
    // Defines the two built-in asset view modes.  Lives in the root UI namespace
    // so ZepThemePreferences (and other non-AssetsArea code) can reference
    // it without an extra using directive.
    internal enum AssetViewMode
    {
        List = 0,
        Grid = 1
    }
}
