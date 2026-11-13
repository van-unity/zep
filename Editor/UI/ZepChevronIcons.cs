using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // The one owner of the disclosure-chevron texture paths and of the textures
    // behind them: the up/down pair the asset rows' and tiles' sub-asset
    // disclosures swap between, and the single right-pointing glyph the folder
    // tree rotates. Consumers used to carry their own copies of the paths, two
    // of them reloading through the AssetDatabase on every toggle. Loaded once per domain reload; StyleBackground is a struct, so
    // wrapping the cached texture on demand costs nothing.
    //
    // It lives at the UI root rather than on FolderTreeWidget because the
    // assets area has no other dependency on the sidebar and must not gain one
    // for two constants and a memo. The chevrons tint through --zep-icon-tint in
    // Zep.uss (styling-standards.md §3); only the image comes from here.
    internal static class ZepChevronIcons
    {
        private const string ChevronDownIconPath =
            "Packages/com.van-unity.zep/Editor/UI/Icons/chevron-down.png";
        private const string ChevronUpIconPath =
            "Packages/com.van-unity.zep/Editor/UI/Icons/chevron-up.png";

        private const string ChevronRightIconPath =
            "Packages/com.van-unity.zep/Editor/UI/Icons/chevron-right.png";

        private static Texture2D _chevronDown;
        private static Texture2D _chevronUp;
        private static Texture2D _chevronRight;

        // Test-only: counts AssetDatabase loads at the single load site below.
        internal static int ChevronLoadsForTests;

        internal static StyleBackground GetChevronBackground(bool expanded)
        {
            return expanded
                ? Resolve(ref _chevronUp, ChevronUpIconPath)
                : Resolve(ref _chevronDown, ChevronDownIconPath);
        }

        // The folder tree's leading disclosure: one glyph pointing right, which
        // USS rotates a quarter turn for the expanded state so the toggle
        // animates instead of swapping images.
        internal static StyleBackground GetLeadingChevronBackground()
        {
            return Resolve(ref _chevronRight, ChevronRightIconPath);
        }

        // A null result is not cached as an answer: the field stays null and the
        // next caller retries, which is what keeps a chevron from staying blank
        // for the rest of the session when the AssetDatabase was not ready yet.
        private static StyleBackground Resolve(ref Texture2D cached, string path)
        {
            if (cached == null)
            {
                ChevronLoadsForTests++;
                cached = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            return cached != null ? new StyleBackground(cached) : new StyleBackground();
        }
    }
}
