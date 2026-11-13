using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEngine;

namespace Zep.Editor.UI.Tests
{
    // Tests for ContextMenuTagSubmenuBuilder.Build.
    // Build is internal and the test assembly has InternalsVisibleTo — no reflection needed.
    public sealed class ContextMenuTagSubmenuBuilderTests
    {
        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static ContextMenuActions NullActions()
        {
            return new ContextMenuActions(null, null, null, null, null, null);
        }

        private static ZepTagFilterOption FavOption(string id, string name = null)
        {
            var tag = new ZepTag(id, name ?? id, ZepTagColor.Blue, isFavorite: true);
            return new ZepTagFilterOption(tag, 1);
        }

        private static ZepTagFilterOption NonFavOption(string id, string name = null)
        {
            var tag = new ZepTag(id, name ?? id, ZepTagColor.Green, isFavorite: false);
            return new ZepTagFilterOption(tag, 1);
        }

        // Build a request where every contextPath is present in the map and each
        // maps to the supplied assigned-tag collection.
        private static ZepContextMenuRequest MakeRequest(
            IReadOnlyList<ZepTagFilterOption> availableTags,
            IReadOnlyList<string> contextPaths,
            IReadOnlyDictionary<string, ICollection<string>> assignedTagsByPath)
        {
            var state = new ZepState();
            return new ZepContextMenuRequest(
                pane: state.PrimaryPane,
                commandHandler: null,
                contextPaths: contextPaths,
                createTargetFolderPath: "Assets",
                availableTags: availableTags,
                assignedTagsByPath: assignedTagsByPath,
                selectedAssets: null,
                selectedFolder: null,
                isOpenFolderEnabled: false,
                isDeleteEnabled: false,
                isCreationAllowed: false,
                canModifyAllContextPaths: false);
        }

        // Simplest valid request: single path with no assigned tags.
        private static ZepContextMenuRequest MakeSimpleRequest(
            IReadOnlyList<ZepTagFilterOption> availableTags,
            string path = "Assets/Foo")
        {
            var map = new Dictionary<string, ICollection<string>>
            {
                { path, new List<string>() }
            };
            return MakeRequest(availableTags, new[] { path }, map);
        }

        // Returns only the non-separator, non-action-item children before any
        // separator (i.e. the favourite toggle items).
        private static IReadOnlyList<ZepContextMenuItem> TagsItem(ZepContextMenuItem root)
        {
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Label, Is.EqualTo("Tags"));
            return root.Children;
        }

        // ---------------------------------------------------------------------------
        // A1: Favourite cap is 7
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_With10FavouriteTags_TagsItemHasExactly7FavouriteToggleChildrenBeforeSeparator()
        {
            var available = Enumerable.Range(1, 10)
                .Select(i => FavOption("fav" + i))
                .ToList();
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            var children = TagsItem(result);
            // Structure is: 7 toggle items, separator, 3 overflow items (the remaining 3
            // favourites that didn't fit into the favourite slice fall into overflow
            // because seenTagIds excludes them), separator, New Tag…, Remove All Tags, Manage Tags…
            var firstSepIndex = children.TakeWhile(c => !c.IsSeparator).Count();
            Assert.That(firstSepIndex, Is.EqualTo(7),
                "Expected exactly 7 favourite toggle items before the first separator");
        }

        // ---------------------------------------------------------------------------
        // A2: Overflow separator only between favourites and overflow
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_With3FavsAnd4NonFavs_ChildrenAre3FavSepOverflowSepActionItems()
        {
            var available = new List<ZepTagFilterOption>
            {
                FavOption("f1"), FavOption("f2"), FavOption("f3"),
                NonFavOption("o1"), NonFavOption("o2"), NonFavOption("o3"), NonFavOption("o4")
            };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            var children = TagsItem(result);
            // Indices: 0,1,2 = fav toggles; 3 = separator; 4,5,6,7 = overflow toggles;
            // 8 = separator; 9 = New Tag…; 10 = Remove All Tags; 11 = Manage Tags…
            Assert.That(children.Count, Is.EqualTo(12));

            // Three favourites
            Assert.That(children[0].IsSeparator, Is.False);
            Assert.That(children[1].IsSeparator, Is.False);
            Assert.That(children[2].IsSeparator, Is.False);

            // Separator between favs and overflow
            Assert.That(children[3].IsSeparator, Is.True);

            // Four overflow items
            Assert.That(children[4].IsSeparator, Is.False);
            Assert.That(children[5].IsSeparator, Is.False);
            Assert.That(children[6].IsSeparator, Is.False);
            Assert.That(children[7].IsSeparator, Is.False);

            // Trailing separator before action items
            Assert.That(children[8].IsSeparator, Is.True);

            // Three action items
            Assert.That(children[9].Label, Is.EqualTo("New Tag…"));
            Assert.That(children[10].Label, Is.EqualTo("Remove All Tags"));
            Assert.That(children[11].Label, Is.EqualTo("Manage Tags…"));
        }

        // ---------------------------------------------------------------------------
        // A3: No overflow separator when there are no favourites
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_WithNoFavsAnd5NonFavs_NoLeadingSeparatorBeforeOverflowItems()
        {
            var available = Enumerable.Range(1, 5)
                .Select(i => NonFavOption("o" + i))
                .ToList();
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            var children = TagsItem(result);
            // First child must not be a separator (no favourites, so no intra-toggle separator).
            Assert.That(children[0].IsSeparator, Is.False,
                "First child should be an overflow toggle item, not a separator");

            // Count toggle items before the first separator: should be all 5.
            var countBeforeFirstSep = children.TakeWhile(c => !c.IsSeparator).Count();
            Assert.That(countBeforeFirstSep, Is.EqualTo(5));
        }

        // ---------------------------------------------------------------------------
        // A4: All-whitespace tagIds are skipped; action items still appear with no leading sep
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_WhenAllTagIdsAreWhitespace_ReturnsTagsItemWithOnlyThreeActionItemsAndNoLeadingSeparator()
        {
            // Tags with empty/whitespace ids are filtered by AppendTagChildren.
            var available = new List<ZepTagFilterOption>
            {
                new ZepTagFilterOption(new ZepTag("", "Empty", ZepTagColor.Blue, isFavorite: true), 1),
                new ZepTagFilterOption(new ZepTag("   ", "Whitespace", ZepTagColor.Blue, isFavorite: false), 1)
            };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            // Build should still return a non-null Tags item (availableTags.Count > 0).
            Assert.That(result, Is.Not.Null);
            var children = TagsItem(result);

            // No toggle items were appended, so tagItems.Count was 0 before the trailing
            // separator guard. The trailing separator guard only adds a separator when
            // tagItems.Count > 0, so there is NO leading separator. The children should
            // be exactly: New Tag…, Remove All Tags, Manage Tags…
            Assert.That(children.Count, Is.EqualTo(3),
                "Expected exactly the three action items and no separator when all tagIds are empty");
            Assert.That(children[0].IsSeparator, Is.False);
            Assert.That(children[0].Label, Is.EqualTo("New Tag…"));
            Assert.That(children[1].Label, Is.EqualTo("Remove All Tags"));
            Assert.That(children[2].Label, Is.EqualTo("Manage Tags…"));
        }

        // ---------------------------------------------------------------------------
        // A5: Common-tag intersection across multiple paths, canTag=true
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_TwoPathsWithPartialTagOverlap_ToggleItemsReflectIntersection()
        {
            var available = new List<ZepTagFilterOption>
            {
                FavOption("t1"), FavOption("t2"), FavOption("t3"), FavOption("t4")
            };
            // pathA has t1,t2,t3; pathB has t2,t3,t4 → intersection is {t2,t3}
            var map = new Dictionary<string, ICollection<string>>
            {
                { "Assets/A", new List<string> { "t1", "t2", "t3" } },
                { "Assets/B", new List<string> { "t2", "t3", "t4" } }
            };
            var request = MakeRequest(available, new[] { "Assets/A", "Assets/B" }, map);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            var children = TagsItem(result);
            var toggles = children.Where(c => !c.IsSeparator && !IsActionItem(c)).ToList();

            Assert.That(toggles.Count, Is.EqualTo(4));

            var byPath = toggles.ToDictionary(t => t.NormalizedCommandPath.Replace("Zep/Tags/Toggle/", ""));
            Assert.That(byPath["t1"].IsChecked, Is.False, "t1: only in pathA, not common");
            Assert.That(byPath["t2"].IsChecked, Is.True, "t2: in both paths");
            Assert.That(byPath["t3"].IsChecked, Is.True, "t3: in both paths");
            Assert.That(byPath["t4"].IsChecked, Is.False, "t4: only in pathB, not common");
        }

        private static bool IsActionItem(ZepContextMenuItem item)
        {
            return item.Label == "New Tag…" || item.Label == "Remove All Tags" || item.Label == "Manage Tags…";
        }

        // ---------------------------------------------------------------------------
        // A6: canTag=false → all toggle items IsChecked=false, IsEnabled=false
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_CanTagFalse_AllToggleItemsAreUncheckedAndDisabled()
        {
            var available = new List<ZepTagFilterOption>
            {
                FavOption("t1"), FavOption("t2"), FavOption("t3"), FavOption("t4")
            };
            var map = new Dictionary<string, ICollection<string>>
            {
                { "Assets/A", new List<string> { "t1", "t2", "t3" } },
                { "Assets/B", new List<string> { "t2", "t3", "t4" } }
            };
            var request = MakeRequest(available, new[] { "Assets/A", "Assets/B" }, map);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: false, NullActions());

            var children = TagsItem(result);
            var toggles = children.Where(c => !c.IsSeparator && !IsActionItem(c)).ToList();

            Assert.That(toggles.Count, Is.EqualTo(4));
            foreach (var toggle in toggles)
            {
                Assert.That(toggle.IsChecked, Is.False,
                    $"Toggle '{toggle.Label}' should be unchecked when canTag=false");
                Assert.That(toggle.IsEnabled, Is.False,
                    $"Toggle '{toggle.Label}' should be disabled when canTag=false");
            }
        }

        // ---------------------------------------------------------------------------
        // A7: Tag management remains reachable when no tags exist yet
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_AvailableTagsEmpty_ReturnsActionOnlySubmenu()
        {
            var map = new Dictionary<string, ICollection<string>>
            {
                { "Assets/Foo", new List<string>() }
            };
            var request = MakeRequest(
                availableTags: Array.Empty<ZepTagFilterOption>(),
                contextPaths: new[] { "Assets/Foo" },
                assignedTagsByPath: map);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            var children = TagsItem(result);
            Assert.That(children.Select(item => item.Label), Is.EqualTo(new[]
            {
                "New Tag…",
                "Remove All Tags",
                "Manage Tags…"
            }));
        }

        // ---------------------------------------------------------------------------
        // A8: Returns null when assignedTagsByPath is null
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_AssignedTagsByPathNull_ReturnsNull()
        {
            var available = new List<ZepTagFilterOption> { FavOption("t1") };
            var request = MakeRequest(
                availableTags: available,
                contextPaths: new[] { "Assets/Foo" },
                assignedTagsByPath: null);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            Assert.That(result, Is.Null);
        }

        // ---------------------------------------------------------------------------
        // A9: Returns null when a contextPath is missing from assignedTagsByPath
        // ---------------------------------------------------------------------------

        [Test]
        public void Build_ContextPathMissingFromMap_ReturnsNull()
        {
            var available = new List<ZepTagFilterOption> { FavOption("t1") };
            // Map contains "Assets/Bar" but contextPaths wants "Assets/Foo"
            var map = new Dictionary<string, ICollection<string>>
            {
                { "Assets/Bar", new List<string>() }
            };
            var request = MakeRequest(
                availableTags: available,
                contextPaths: new[] { "Assets/Foo" },
                assignedTagsByPath: map);

            var result = ContextMenuTagSubmenuBuilder.Build(request, canTag: true, NullActions());

            Assert.That(result, Is.Null);
        }

        // ---------------------------------------------------------------------------
        // F-015: favourite/clear tag rows advertise the Finder-tag hotkeys the
        // shortcut controller actually dispatches (macOS only), positionally
        // matching TryHandleFinderTagShortcut's own favourites-Take(7) order.
        // ---------------------------------------------------------------------------

        private static IReadOnlyList<ZepContextMenuItem> Toggles(
            IReadOnlyList<ZepContextMenuItem> children)
        {
            return children.Where(c => !c.IsSeparator && !IsActionItem(c)).ToList();
        }

        [Test]
        public void Build_OnMacWithThreeFavourites_FirstThreeTogglesFormatToCtrl1Ctrl2Ctrl3()
        {
            var available = new List<ZepTagFilterOption>
            {
                FavOption("f1"), FavOption("f2"), FavOption("f3")
            };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(
                request, canTag: true, NullActions(), RuntimePlatform.OSXEditor);

            var toggles = Toggles(TagsItem(result));
            Assert.That(toggles.Count, Is.EqualTo(3));
            Assert.That(toggles.Select(t => t.Hotkey), Is.All.Not.Empty,
                "Every favourite toggle should carry a hotkey on macOS");
            Assert.That(
                toggles.Select(t => ContextMenuHotkeyText.Format(t.Hotkey, macStyle: true)),
                Is.EqualTo(new[] { "⌃1", "⌃2", "⌃3" }));
        }

        [Test]
        public void Build_OnMacWithEightFavouritesAndOneOverflow_EighthFavouriteAndOverflowCarryNoHotkey()
        {
            var favourites = Enumerable.Range(1, 8).Select(i => FavOption("fav" + i)).ToList();
            var available = new List<ZepTagFilterOption>(favourites) { NonFavOption("o1") };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(
                request, canTag: true, NullActions(), RuntimePlatform.OSXEditor);

            var toggles = Toggles(TagsItem(result));
            // 7 capped favourites + the 8th favourite (falls into overflow) + o1.
            Assert.That(toggles.Count, Is.EqualTo(9));

            var firstSeven = toggles.Take(7).ToList();
            Assert.That(firstSeven, Is.All.Matches<ZepContextMenuItem>(t => t.Hotkey.Length > 0),
                "The first 7 favourites should all carry a hotkey");

            var eighthFavourite = toggles.First(t => t.NormalizedCommandPath.EndsWith("fav8"));
            Assert.That(eighthFavourite.Hotkey, Is.Empty,
                "The 8th favourite (overflow, since the shortcut controller only binds 7) should carry no hotkey");

            var overflowNonFav = toggles.First(t => t.NormalizedCommandPath.EndsWith("o1"));
            Assert.That(overflowNonFav.Hotkey, Is.Empty,
                "A non-favourite overflow tag should carry no hotkey");
        }

        [Test]
        public void Build_OnMac_RemoveAllTagsCarriesCtrl0()
        {
            var available = new List<ZepTagFilterOption> { FavOption("f1") };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(
                request, canTag: true, NullActions(), RuntimePlatform.OSXEditor);

            var removeAllTags = TagsItem(result).First(c => c.Label == "Remove All Tags");
            Assert.That(ContextMenuHotkeyText.Format(removeAllTags.Hotkey, macStyle: true), Is.EqualTo("⌃0"));
        }

        [Test]
        public void Build_OffMac_TagRowsCarryNoHotkey()
        {
            var available = new List<ZepTagFilterOption>
            {
                FavOption("f1"), FavOption("f2"), NonFavOption("o1")
            };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(
                request, canTag: true, NullActions(), RuntimePlatform.WindowsEditor);

            var children = TagsItem(result);
            foreach (var child in children)
            {
                if (child.IsSeparator) continue;
                Assert.That(child.Hotkey, Is.Empty,
                    $"'{child.Label}' should carry no hotkey off macOS (Finder-tag chords don't exist there)");
            }
        }

        [Test]
        public void Build_OnMac_NewTagAndManageTagsCarryNoHotkey()
        {
            var available = new List<ZepTagFilterOption> { FavOption("f1") };
            var request = MakeSimpleRequest(available);

            var result = ContextMenuTagSubmenuBuilder.Build(
                request, canTag: true, NullActions(), RuntimePlatform.OSXEditor);

            var children = TagsItem(result);
            Assert.That(children.First(c => c.Label == "New Tag…").Hotkey, Is.Empty);
            Assert.That(children.First(c => c.Label == "Manage Tags…").Hotkey, Is.Empty);
        }
    }
}
