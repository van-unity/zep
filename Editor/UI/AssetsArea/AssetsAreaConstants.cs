namespace Zep.Editor.UI.AssetsArea
{
    // Grid-item sizing, list row height, drag-start distance, and pane-width
    // breakpoints shared by every AssetsArea feature widget and the inner
    // list/grid views.
    internal static class AssetsAreaConstants
    {
        internal const int MinAssetGridItemSize = 104;
        internal const int MaxAssetGridItemSize = 188;
        internal const int DefaultAssetGridItemSize = 136;
        internal const float DefaultAssetRowHeight = 38f;
        internal const float AssetDragStartDistance = 6f;
        internal const float AssetPaneCompactBreakpoint = 820f;
        internal const float AssetPaneNarrowBreakpoint = 700f;
        internal const float AssetPaneTightBreakpoint = 560f;
        internal const float SplitPaneMinimumWidth = 180f;
        internal const float SplitPaneChromeCompactBreakpoint = 300f;
        internal const float SplitPaneChromeTightBreakpoint = 220f;
        internal const float SplitDividerKeyboardStep = 16f;
    }
}
