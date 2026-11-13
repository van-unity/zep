using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // The Favorites and Tags section headers used to reload their chevron
    // through the AssetDatabase on every toggle, while the folder tree and the
    // asset rows each cached their own copy of the same textures. One owner
    // now holds every chevron, and the headers fold with a Hide/Show label
    // instead of a chevron, so a toggle touches no asset at all.
    public sealed class SidebarChevronIconTests
    {
        [Test]
        public void FavoritesAndTagsHeaderToggles_DoNotReloadTheChevronTextures()
        {
            // Warm every glyph through the shared owner, the way the first
            // paint of any consumer does.
            var warmUp = ZepChevronIcons.GetChevronBackground(true);
            ZepChevronIcons.GetChevronBackground(false);
            ZepChevronIcons.GetLeadingChevronBackground();
            var warmTextureWasNull = warmUp.value.texture == null;

            var favorites = new FavoritesWidget(
                new UnityZepIconRepository(),
                _ => null);
            var tags = new TagsFilterWidget();

            ZepChevronIcons.ChevronLoadsForTests = 0;

            for (var i = 0; i < 20; i++)
            {
                ToggleHeader(favorites);
                ToggleHeader(tags);
                // The assets area draws the up/down pair for its sub-asset
                // rows; the folder tree draws the leading glyph per bind.
                AssetEntryRenderer.GetDisclosureChevron(i % 2 == 0);
                ZepChevronIcons.GetLeadingChevronBackground();
            }

            Assert.That(
                ZepChevronIcons.ChevronLoadsForTests,
                Is.Zero,
                "20 header toggles per section must not touch the AssetDatabase "
                + $"(warm texture was null: {warmTextureWasNull})");
        }

        private static void ToggleHeader(VisualElement widget)
        {
            var header = widget.Q<SidebarSectionHeader>();
            Assert.That(header, Is.Not.Null, "every sidebar section folds through its header");
            var before = header.IsExpanded;
            header.ToggleExpanded();
            Assert.That(header.IsExpanded, Is.Not.EqualTo(before));
        }
    }
}
