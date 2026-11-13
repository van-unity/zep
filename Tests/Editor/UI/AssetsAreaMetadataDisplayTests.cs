using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // Two pieces of derived text the assets area re-computes on every refresh
    // tick, and the rules that decide whether the computation happens at all.
    //
    //  1. The list row's secondary metadata line. Zep.uss hides it by
    //     default and only the row binder's inline write shows it, above a 30px
    //     row pitch — which most of the thumbnail-size range, including the
    //     default, is BELOW. The binder used to build the string (a List, a
    //     joined result, and up to three formatted parts) for every changed bind
    //     regardless, so these pin both halves: populated when the row is tall
    //     enough, and empty AND hidden when it isn't.
    //
    //  2. The bottom bar's file count. It is now memoized against the last
    //     shown count so an unchanged refresh formats nothing; a memo is only
    //     safe if it cannot go stale, so this drives the count up, down, and
    //     back to zero.
    //
    // Plus the cost of the bind pass that computes them: how many times one
    // publish binds the realized window, and that the single itemsSource
    // facade still re-sizes the content when the row extent changes.
    public sealed class AssetsAreaMetadataDisplayTests
    {
        // The type name is the only metadata part available without a
        // controller (presentation lookups return default), so it is what a
        // shown secondary line must read.
        private const string EntryTypeName = "ScriptableObject";

        [Test]
        public void AddressablesFolderCapture_KeepsExplicitFolderAndFileGuidsAndExpandsOnlyDescendants()
        {
            var folderName = "ZepAddressables_" + Guid.NewGuid().ToString("N");
            var folderPath = "Assets/" + folderName;
            var folderGuid = AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                Assert.That(folderGuid, Is.Not.Empty, "fixture guard: a real Unity folder was created");
                var settings = new AddressableSettingsShape { groups = new object[]
                {
                    new AddressableGroupShape { entries = new object[]
                    {
                        new AddressableEntryShape { guid = folderGuid, AssetPath = folderPath },
                        new AddressableEntryShape { guid = "explicit-file-guid", AssetPath = "Assets/ExplicitFile.asset" },
                    } },
                } };
                var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var folders = new List<string>();
                var complete = (bool)typeof(ZepAddressableUtility)
                    .GetMethod("TryCollectAddressableEntries", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { settings, guids, folders });

                Assert.That(complete, Is.True);
                Assert.That(guids, Is.EquivalentTo(new[] { folderGuid, "explicit-file-guid" }));
                Assert.That(folders, Is.EqualTo(new[] { folderPath }));
                var expanded = AddressablesCapture.Expand(
                    new AddressablesCapture(guids.ToArray(), folders, complete),
                    new[]
                    {
                        new AssetRecord("child-guid", folderPath + "/Child.asset", "Child", EntryTypeName),
                        new AssetRecord("outside-guid", folderPath + "Sibling/Outside.asset", "Outside", EntryTypeName),
                    });

                Assert.That(expanded, Is.EquivalentTo(new[] { folderGuid, "explicit-file-guid", "child-guid" }),
                    "the explicit folder keeps its own identity while only its descendants inherit membership");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        [Test]
        public void AddressablesFolderCapture_IncompleteReflectionStillFailsClosed()
        {
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "partial-guid" };
            var folders = new List<string> { "Assets" };
            var complete = (bool)typeof(ZepAddressableUtility)
                .GetMethod("TryCollectAddressableEntries", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { new object(), guids, folders });

            Assert.That(complete, Is.False);
            Assert.That(AddressablesCapture.Expand(
                    new AddressablesCapture(guids.ToArray(), folders, complete),
                    new[] { new AssetRecord("child-guid", "Assets/Child.asset", "Child", EntryTypeName) }),
                Is.Empty, "incomplete capture must discard even previously collected positive entries");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressablesFolderCapture_MixedGroupShapesFailClosed(bool unreadableFirst)
        {
            var readable = new AddressableGroupShape { entries = new object[]
            {
                new AddressableEntryShape { guid = "partial-guid", AssetPath = "Assets/Partial.asset" },
            } };
            var unreadable = new object();
            var settings = new AddressableSettingsShape
            {
                groups = unreadableFirst
                    ? new object[] { unreadable, readable }
                    : new object[] { readable, unreadable },
            };
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new List<string>();
            var complete = (bool)typeof(ZepAddressableUtility)
                .GetMethod("TryCollectAddressableEntries", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { settings, guids, folders });
            var expanded = AddressablesCapture.Expand(
                new AddressablesCapture(guids.ToArray(), folders, complete),
                Array.Empty<AssetRecord>());

            Assert.That(expanded, Is.Empty, "a readable group must not publish a partial authoritative badge set");
            Assert.That(complete, Is.False, "every non-null group must expose its entries before capture is complete");
        }

        private sealed class AddressableSettingsShape { public object[] groups; }
        private sealed class AddressableGroupShape { public object[] entries; }
        private sealed class AddressableEntryShape { public string guid; public string AssetPath; }

        [UnityTest]
        public IEnumerator ListRow_AtTheDefaultRowPitch_LeavesTheSecondaryLineHiddenAndEmpty()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(1));
                yield return null;
                yield return null;

                var secondary = SecondaryLabelOfFirstRow(view);
                Assert.That(secondary, Is.Not.Null, "the first row must be realized");
                Assert.That(secondary.resolvedStyle.display, Is.EqualTo(DisplayStyle.None),
                    "the default thumbnail size gives a row too short for a metadata line");
                Assert.That(secondary.text, Is.Empty,
                    "a hidden secondary line must not hold a built string — building it is the "
                    + "per-bind allocation the row binder now skips");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ListRow_AtTheLargestRowPitch_ShowsThePopulatedSecondaryLine()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(1));
                // Rows are sized from the thumbnail slider: the maximum maps to
                // the tallest row, which is the only tier that shows the line.
                view.AssetsArea.SetGridItemSize(AssetsAreaConstants.MaxAssetGridItemSize);
                yield return null;
                yield return null;

                var secondary = SecondaryLabelOfFirstRow(view);
                Assert.That(secondary, Is.Not.Null, "the first row must be realized");
                Assert.That(secondary.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "a tall row must show the metadata line under the name");
                Assert.That(secondary.text, Is.EqualTo(EntryTypeName),
                    "the shown line must carry the row's metadata, not an empty string");
            }
            finally
            {
                window.Close();
            }
        }

        // ---- List column layout, measured on real geometry ----
        //
        // The column gap is the column class's margin-left, shared by the
        // header label and the row cell. The row-cell rules come later in the
        // sheet and used to write the margin SHORTHAND, which zeroed every gap
        // in the rows while the header kept it: dates ran into the tag chips,
        // sizes into the type, and no cell sat under its header. The header's
        // horizontal padding and its scroller-mirroring spacer are the other
        // two alignment inputs; all three are pinned here.

        [UnityTest]
        public IEnumerator ListRow_CellsStartUnderTheirHeaders_AndKeepTheColumnGap()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.style.width = WidePaneWidth;
                view.BrowserFeature.SetAssets(BuildAssets(1));
                yield return null;
                yield return null;
                yield return null;

                var row = FirstRow(view);
                Assert.That(row, Is.Not.Null, "the first row must be realized");

                foreach (var column in new[] { "modified", "size", "type", "addressable" })
                {
                    var header = view.Q<Label>("zep-asset-list-header-" + column);
                    var cell = row.Q<Label>("zep-asset-row-" + column);
                    Assert.That(header, Is.Not.Null, column + " header");
                    Assert.That(cell, Is.Not.Null, column + " cell");
                    Assert.That(header.worldBound.width, Is.GreaterThan(0f),
                        column + ": the header must be laid out for the measurement to mean anything");
                    Assert.That(cell.worldBound.xMin, Is.EqualTo(header.worldBound.xMin).Within(0.5f),
                        column + ": the cell must start under its header");
                }

                var size = row.Q<Label>("zep-asset-row-size");
                var type = row.Q<Label>("zep-asset-row-type");
                Assert.That(type.worldBound.xMin - size.worldBound.xMax, Is.EqualTo(16f).Within(0.5f),
                    "the size and type cells must keep the column gap between them");
            }
            finally
            {
                window.Close();
            }
        }

        // The tag chips sit in a slot of their own between the name and the
        // date: parked at the far end of the name column (the name grows, the
        // chips do not), and standing 28px clear of the Date Modified cell —
        // the column's 16px gap plus the slot's own 12px trailing margin. They
        // used to touch the date, then (briefly) hug the name text.
        [UnityTest]
        public IEnumerator ListRow_TagChipsSitInTheirOwnSlot_BetweenTheNameAndTheDate()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.style.width = WidePaneWidth;
                view.BrowserFeature.SetAssets(new[]
                {
                    new ZepAssetEntry(
                        "Editor",
                        "Assets/Editor",
                        "Folder",
                        tags: new[] { new ZepTag("red", "Red", ZepTagColor.Red) }),
                });
                yield return null;
                yield return null;
                yield return null;

                var row = FirstRow(view);
                var name = row.Q<Label>("zep-asset-row-name");
                var tags = row.Q<VisualElement>("zep-asset-row-tags");
                var modified = row.Q<Label>("zep-asset-row-modified");
                Assert.That(tags.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "the tag chip must show");
                Assert.That(tags.Q<Label>(className: ZepUiClasses.TagChipLabel), Is.Null,
                    "a list row shows a bare colour dot, no tag name");
                Assert.That(tags[0].resolvedStyle.backgroundColor.a, Is.EqualTo(0f),
                    "and no pill behind the dot");
                Assert.That(tags.tooltip, Is.EqualTo("Red"), "the name rides on the slot's tooltip instead");

                Assert.That(modified.worldBound.xMin - tags.worldBound.xMax, Is.EqualTo(28f).Within(0.5f),
                    "the chips must stand clear of the date column by the column gap plus the slot margin");
                var textWidth = name.MeasureTextSize(
                    name.text, 0f, VisualElement.MeasureMode.Undefined, 0f, VisualElement.MeasureMode.Undefined).x;
                Assert.That(tags.worldBound.xMin, Is.GreaterThan(name.worldBound.xMin + textWidth + 40f),
                    "the chips must be parked at the end of the column, not hugging the name text");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void SubAssetTooltip_RenameWithStableIdentityRefreshesAndThenReusesItsString()
        {
            const string path = "Assets/TooltipRename.asset";
            var list = new ListAssetWidget(new UnityZepIconRepository(), new AssetsAreaInlineEditController(), null);
            var row = (VisualElement)typeof(ListAssetWidget)
                .GetMethod("MakeAssetRow", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, null);
            var bind = typeof(ListAssetWidget).GetMethod("BindSubAssetRow", BindingFlags.Instance | BindingFlags.NonPublic);
            var parts = (ListAssetWidget.AssetRowParts)row.userData;
            var original = new SubAssetDescriptor(path, 1, default, "Original", EntryTypeName, null);
            bind.Invoke(list, new object[] { row, parts, original, 0 });
            Assert.That(parts.Name.tooltip, Is.EqualTo(path + " › Original"));
            var builds = ListAssetWidget.SubAssetTooltipBuildsForTests;

            var renamed = new SubAssetDescriptor(path, 1, default, "Renamed", EntryTypeName, null);
            bind.Invoke(list, new object[] { row, parts, renamed, 0 });
            Assert.That(parts.Name.text, Is.EqualTo("Renamed"));
            Assert.That(parts.Name.tooltip, Is.EqualTo(path + " › Renamed"));
            Assert.That(ListAssetWidget.SubAssetTooltipBuildsForTests - builds, Is.EqualTo(1));

            for (var i = 0; i < 3; i++)
            {
                var unchanged = new SubAssetDescriptor(path, 1, default, "Renamed", EntryTypeName, null);
                bind.Invoke(list, new object[] { row, parts, unchanged, 0 });
            }

            Assert.That(ListAssetWidget.SubAssetTooltipBuildsForTests - builds, Is.EqualTo(1),
                "equivalent descriptors must reuse the string after the one rename rebuild");
        }

        [TestCase(0)]
        [TestCase(4)]
        public void ListTagTooltip_RenameRefreshesNamesIncludingTagsBeyondTheDotLimit(int renamedIndex)
        {
            var list = new ListAssetWidget(new UnityZepIconRepository(), new AssetsAreaInlineEditController(), null);
            var row = (VisualElement)typeof(ListAssetWidget)
                .GetMethod("MakeAssetRow", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, null);
            var bind = typeof(ListAssetWidget).GetMethod("BindAssetRow", BindingFlags.Instance | BindingFlags.NonPublic);
            var tags = Enumerable.Range(0, 5)
                .Select(i => new ZepTag("tag" + i, "Tag " + i, ZepTagColor.Blue)).ToArray();
            var entries = new[] { new ZepAssetEntry("Assets", "Assets", "Folder", tags: tags) };
            ((IAssetWidget)list).SetAssets(entries);
            bind.Invoke(list, new object[] { row, 0 });
            var parts = (ListAssetWidget.AssetRowParts)row.userData;
            var firstTooltip = parts.Tags.tooltip;
            Assert.That(firstTooltip, Is.EqualTo("Tag 0, Tag 1, Tag 2, Tag 3, Tag 4"));

            bind.Invoke(list, new object[] { row, 0 });
            Assert.That(parts.Tags.tooltip, Is.SameAs(firstTooltip), "unchanged rebinding retains the tooltip value");

            tags[renamedIndex] = new ZepTag(tags[renamedIndex].Id, "Renamed tag", tags[renamedIndex].Color);
            ((IAssetWidget)list).SetAssets(new[] { new ZepAssetEntry("Assets", "Assets", "Folder", tags: tags) });
            bind.Invoke(list, new object[] { row, 0 });

            Assert.That(parts.Tags.tooltip, Is.EqualTo(string.Join(", ", tags.Select(tag => tag.Name))),
                "same-ID/color renames must refresh every name in the tooltip, including overflow tags");
            Assert.That(parts.Tags.Query<VisualElement>(className: ZepUiClasses.TagColorDot).ToList(), Has.Count.EqualTo(3));
            Assert.That(parts.Tags.Q<Label>().text, Is.EqualTo("+2"));
            Assert.That(parts.Tags.Q<Label>(className: ZepUiClasses.TagChipLabel), Is.Null,
                "including names in the signature must keep the row's dot-only presentation");
        }

        // Without the Addressables package nothing can be addressable; the
        // shell stamps the root state and the sheet drops the column under it.
        [UnityTest]
        public IEnumerator ListView_DropsTheAddressableColumn_UnderTheNoAddressablesRootState()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.style.width = WidePaneWidth;
                view.BrowserFeature.SetAssets(BuildAssets(1));
                yield return null;
                yield return null;

                var header = view.Q<Label>("zep-asset-list-header-addressable");
                var cell = FirstRow(view).Q<Label>("zep-asset-row-addressable");
                Assert.That(header.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "package present: header shown");
                Assert.That(cell.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "package present: cell shown");

                window.rootVisualElement.AddToClassList(ZepUiClasses.RootNoAddressables);
                yield return null;
                yield return null;

                Assert.That(header.resolvedStyle.display, Is.EqualTo(DisplayStyle.None), "package absent: header hidden");
                Assert.That(cell.resolvedStyle.display, Is.EqualTo(DisplayStyle.None), "package absent: cell hidden");
            }
            finally
            {
                window.Close();
            }
        }

        // Finder's "--": a folder has no byte size, and an empty cell next to
        // a filled Type cell read as missing data.
        [UnityTest]
        public IEnumerator ListRow_ForAFolder_ShowsADashInTheSizeCell()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(new[]
                {
                    new ZepAssetEntry("Editor", "Assets/Editor", "Folder"),
                });
                yield return null;
                yield return null;

                var cell = FirstRow(view).Q<Label>("zep-asset-row-size");
                Assert.That(cell.text, Is.EqualTo(ListAssetWidget.NoSizeText));
            }
            finally
            {
                window.Close();
            }
        }

        private static VisualElement FirstRow(ZepPaneView view)
        {
            var list = view.Q<ListView>("zep-asset-list");
            return list?.GetRootElementForIndex(0);
        }

        [UnityTest]
        public IEnumerator ListSubAssetRow_ShowsItsIdentityChip()
        {
            const string fixturePath = "Assets/ZepListSubAssetChipFixture.asset";
            var main = ScriptableObject.CreateInstance<MetadataSubAssetFixture>();
            AssetDatabase.CreateAsset(main, fixturePath);
            var sub = ScriptableObject.CreateInstance<MetadataSubAssetFixture>();
            sub.name = "Embedded";
            AssetDatabase.AddObjectToAsset(sub, main);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(fixturePath, ImportAssetOptions.ForceUpdate);

            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(new[]
                {
                    new ZepAssetEntry(
                        "ZepListSubAssetChipFixture",
                        fixturePath,
                        "ScriptableObject")
                });
                yield return null;

                SubAssetCatalog.GetOrRequest(fixturePath);
                for (var frame = 0;
                     frame < 60 && SubAssetCatalog.GetResolved(fixturePath) == null;
                     frame++)
                {
                    yield return null;
                }

                Assert.That(SubAssetCatalog.GetResolved(fixturePath), Has.Length.EqualTo(1));
                var listWidget = view.Q<ListAssetWidget>();
                Assert.That(listWidget, Is.Not.Null);
                listWidget.ExpandSubAssets(fixturePath);
                yield return null;
                yield return null;

                var chip = view.Q<Label>("zep-asset-list-status-chip");
                Assert.That(chip, Is.Not.Null);
                Assert.That(chip.text, Is.EqualTo("Sub-asset"));
                Assert.That(chip.ClassListContains(ZepUiClasses.RowChip), Is.True);
                Assert.That(chip.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "the chip has to be visible — it shares the badge container with the tag "
                    + "chips, and the sub-asset row's own tag rebuild hides that container");
                Assert.That(chip.parent.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "the container's display is the OR of the chip's and the tag badges' needs");
            }
            finally
            {
                window.Close();
                AssetDatabase.DeleteAsset(fixturePath);
            }
        }

        // Row pooling drove the leak: the chip was rebuilt on every sub-asset
        // bind and never removed, so scrolling piled hidden Labels into the
        // badge container. One cached chip per row, shown and hidden per bind.
        [UnityTest]
        public IEnumerator ListSubAssetChip_AcrossRebinds_KeepsOneChipAndLeavesTagBadgesAlone()
        {
            const string fixturePath = "Assets/ZepListSubAssetRebindFixture.asset";
            const string firstTaggedPath = "Assets/ZepListTaggedNeighbourA.asset";
            const string secondTaggedPath = "Assets/ZepListTaggedNeighbourB.asset";
            var main = ScriptableObject.CreateInstance<MetadataSubAssetFixture>();
            AssetDatabase.CreateAsset(main, fixturePath);
            var sub = ScriptableObject.CreateInstance<MetadataSubAssetFixture>();
            sub.name = "Embedded";
            AssetDatabase.AddObjectToAsset(sub, main);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(fixturePath, ImportAssetOptions.ForceUpdate);

            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                var tags = new[] { new ZepTag("blue", "Blue", ZepTagColor.Blue) };
                var tagged = new[]
                {
                    new ZepAssetEntry(
                        "ZepListTaggedNeighbourA",
                        firstTaggedPath,
                        "ScriptableObject",
                        tags: tags),
                    new ZepAssetEntry(
                        "ZepListTaggedNeighbourB",
                        secondTaggedPath,
                        "ScriptableObject",
                        tags: tags),
                };
                var assets = new[]
                {
                    new ZepAssetEntry(
                        "ZepListSubAssetRebindFixture",
                        fixturePath,
                        "ScriptableObject"),
                    tagged[0],
                    tagged[1],
                };
                view.BrowserFeature.SetAssets(assets);
                yield return null;

                SubAssetCatalog.GetOrRequest(fixturePath);
                for (var frame = 0;
                     frame < 60 && SubAssetCatalog.GetResolved(fixturePath) == null;
                     frame++)
                {
                    yield return null;
                }

                var listWidget = view.Q<ListAssetWidget>();
                Assert.That(listWidget, Is.Not.Null);
                listWidget.ExpandSubAssets(fixturePath);
                yield return null;

                var badges = BadgeContainerOfRow(view, 1);
                Assert.That(badges, Is.Not.Null, "the sub-asset row must be realized");
                var childCountAfterFirstBind = badges.childCount;

                for (var rebind = 0; rebind < 5; rebind++)
                {
                    view.BrowserFeature.SetAssets(assets);
                    yield return null;
                }

                Assert.That(BadgeContainerOfRow(view, 1), Is.SameAs(badges),
                    "the rebinds must land on the same pooled row for this to mean anything");
                Assert.That(badges.childCount, Is.EqualTo(childCountAfterFirstBind),
                    "rebinding a pooled sub-asset row must reuse its chip, not add one per bind");
                Assert.That(
                    badges.Q<Label>("zep-asset-list-status-chip").resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.Flex));

                // Drop the parent: visible row 1 is now a TAGGED MAIN asset on
                // the same pooled element that just hosted the sub-asset row.
                view.BrowserFeature.SetAssets(tagged);
                yield return null;

                Assert.That(BadgeContainerOfRow(view, 1), Is.SameAs(badges),
                    "the recycle under test needs the same pooled row element");
                Assert.That(
                    badges.Q<Label>("zep-asset-list-status-chip").resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.None),
                    "a main asset row must never claim to be a sub-asset");
                Assert.That(badges.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "the tag badges of the recycled row are still shown");
                Assert.That(
                    badges.Children().Count(child =>
                        child.ClassListContains(ZepUiClasses.TagChip)
                        && child.resolvedStyle.display == DisplayStyle.Flex),
                    Is.EqualTo(1),
                    "exactly the row's own tag badge, unaffected by the chip");
            }
            finally
            {
                window.Close();
                AssetDatabase.DeleteAsset(fixturePath);
            }
        }

        // The conflict has to be visible on the row itself: a greyed-out Put
        // Back inside a context menu the user never opened explains nothing.

        private static VisualElement BadgeContainerOfRow(ZepPaneView view, int visibleIndex)
        {
            var list = view.Q<ListView>("zep-asset-list");
            var row = list?.GetRootElementForIndex(visibleIndex);
            return row?.Q<VisualElement>(className: ZepUiClasses.RowBadges);
        }

        [UnityTest]
        public IEnumerator BottomBarCount_TracksEveryChangeInBothDirections()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                yield return null;

                var count = view.Q<Label>("zep-floating-count");
                Assert.That(count, Is.Not.Null);
                Assert.That(count.text, Is.EqualTo("0 files"),
                    "an empty folder starts at zero — the memo's seed must agree with the "
                    + "label's construction text or the first real update is skipped");

                SetAssetCount(view, 3);
                Assert.That(count.text, Is.EqualTo("3 files"));

                SetAssetCount(view, 1);
                Assert.That(count.text, Is.EqualTo("1 file"),
                    "one file reads in the singular");

                SetAssetCount(view, 0);
                Assert.That(count.text, Is.EqualTo("0 files"),
                    "returning to empty must repaint the count — a memo that only ever moves "
                    + "upward would leave the bar claiming files that are gone");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------

        private static void SetAssetCount(ZepPaneView view, int count)
        {
            view.BrowserFeature.SetAssets(BuildAssets(count));
            // The bar's count is refreshed by the pane's asset pull, which is
            // what a real refresh tick runs.
            view.AssetsArea.RefreshActiveAssets();
        }

        private static ZepAssetEntry[] BuildAssets(int count, string prefix = "Asset")
        {
            var assets = new ZepAssetEntry[count];
            for (var i = 0; i < count; i++)
            {
                var name = prefix + i + ".asset";
                assets[i] = new ZepAssetEntry(name, "Assets/" + name, EntryTypeName);
            }

            return assets;
        }

        // The date/size format caches used to be emptied wholesale at the cap,
        // which threw away exactly the rows the user is looking at and made the
        // whole visible list re-format on the next bind. Dropping the oldest
        // quarter keeps them. The middle key is what tells the two apart: a
        // wholesale clear would leave only the ~1,000 entries added after it,
        // so key 2,000 would be gone and the count would be 1,000, not 4,750.
        [Test]
        public void FormatCaches_PastTheCap_EvictTheOldestQuarterAndKeepTheRest()
        {
            AssetEntryRenderer.ClearFormatCaches();
            try
            {
                var capacity = AssetEntryRenderer.FormatCacheCapacity;
                var overflow = capacity + capacity / 5;
                var baseDate = new DateTime(2026, 1, 1, 0, 0, 0);
                for (var i = 0; i < overflow; i++)
                {
                    AssetEntryRenderer.FormatModifiedDate(baseDate.AddMinutes(i));
                }

                Assert.That(
                    AssetEntryRenderer.FormattedDateCache.Count,
                    Is.EqualTo(overflow - capacity / 4),
                    "one eviction of a quarter is all an overflow of a fifth can trigger");
                Assert.That(
                    AssetEntryRenderer.FormattedDateCache.ContainsKey(baseDate),
                    Is.False,
                    "the oldest entries are the ones that go");
                Assert.That(
                    AssetEntryRenderer.FormattedDateCache.ContainsKey(
                        baseDate.AddMinutes(capacity / 4)),
                    Is.True,
                    "everything past the evicted quarter must survive — a wholesale "
                    + "clear would have dropped this one too");
                Assert.That(
                    AssetEntryRenderer.FormattedDateCache.ContainsKey(
                        baseDate.AddMinutes(overflow - 1)),
                    Is.True,
                    "the newest entry is always cached");
                Assert.That(
                    AssetEntryRenderer.FormatModifiedDate(baseDate.AddMinutes(overflow - 1)),
                    Is.EqualTo(baseDate.AddMinutes(overflow - 1).ToString("MMM d, yyyy HH:mm")),
                    "eviction must not change what a format call returns");
            }
            finally
            {
                AssetEntryRenderer.ClearFormatCaches();
            }
        }

        // The list's itemsSource is a virtual facade over the visible rows and
        // the widget addresses it by index only (the ListView is at
        // SelectionType.None, so nothing asks it to find an item). The O(n)
        // boxed-Equals scan it used to answer with was a trap waiting for the
        // day selection moved onto the control; the throw says the seam does
        // not exist instead of paying for a lookup nobody should make.
        [UnityTest]
        public IEnumerator ListItemsSource_AskedToFindAnItem_ThrowsInsteadOfScanning()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(3));
                yield return null;
                yield return null;

                var list = view.Q<ListView>("zep-asset-list");
                Assert.That(list, Is.Not.Null);
                var source = list.itemsSource;
                Assert.That(source, Is.Not.Null);
                Assert.That(source.Count, Is.GreaterThan(0), "the facade still answers by index");
                Assert.That(source[0], Is.Not.Null);

                var probe = source[0];
                Assert.Throws<NotSupportedException>(() => source.IndexOf(probe));
                Assert.Throws<NotSupportedException>(() => source.Contains(probe));
            }
            finally
            {
                window.Close();
            }
        }

        // A publish binds the visible window exactly once. It used to bind it
        // twice: assigning a freshly allocated itemsSource facade made the
        // collection-view controller's setter fire its own RefreshItems() on
        // the reference change, and the widget then called RefreshItems()
        // again. Every search keystroke, chevron toggle and catalog drain paid
        // two full BindAssetRow passes over every realized row.
        [UnityTest]
        public IEnumerator ListPublish_OfAChangedList_BindsTheVisibleWindowOnce()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                var list = view.Q<ListView>("zep-asset-list");
                Assert.That(list, Is.Not.Null);
                var realized = RealizedRowCount(list);
                Assert.That(realized, Is.GreaterThan(4),
                    "the viewport must realize a window of rows for a bind count to mean anything");

                ListAssetWidget.BindCountForTests = 0;
                view.BrowserFeature.SetAssets(BuildAssets(100, "Second"));
                yield return null;
                yield return null;

                var binds = ListAssetWidget.BindCountForTests;
                Assert.That(binds, Is.GreaterThanOrEqualTo(realized),
                    "every realized row must be rebound against the republished list");
                Assert.That(binds, Is.LessThan(realized * 2),
                    "one publish must bind the realized window once, not twice");
                Assert.That(NameLabelOfFirstRow(view)?.text, Is.EqualTo("Second0.asset"),
                    "the single bind pass must show the republished entries");
            }
            finally
            {
                window.Close();
            }
        }

        // The facade's Count is dynamic (it reads the widget's visible row
        // count), so a publish that changes the row extent needs no new source
        // object: RefreshItems() re-sizes the ListView's content itself.
        [UnityTest]
        public IEnumerator ListPublish_OfAShorterList_ResizesTheContentWithoutANewSource()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                var list = view.Q<ListView>("zep-asset-list");
                Assert.That(list, Is.Not.Null);
                var source = list.itemsSource;
                Assert.That(RealizedRowCount(list), Is.GreaterThan(4));

                ListAssetWidget.BindCountForTests = 0;
                view.BrowserFeature.SetAssets(BuildAssets(3, "Short"));
                yield return null;
                yield return null;

                Assert.That(list.itemsSource, Is.SameAs(source),
                    "the widget keeps one facade for its lifetime");
                Assert.That(list.itemsSource.Count, Is.EqualTo(5),
                    "three rows plus the two marquee tail dummies");
                Assert.That(RealizedRowCount(list), Is.EqualTo(5),
                    "the refresh must shrink the realized window to the new row extent");
                Assert.That(ListAssetWidget.BindCountForTests, Is.GreaterThan(0),
                    "the surviving rows must be rebound against the shorter list");
                Assert.That(NameLabelOfFirstRow(view)?.text, Is.EqualTo("Short0.asset"));
            }
            finally
            {
                window.Close();
            }
        }

        // A row's icon boxes are derived from the row pitch and nothing else,
        // and the only thing that changes the pitch Rebuild()s every row. Four
        // style writes per row per bind therefore buy nothing a freshly made
        // row does not already have — they are pure cost on every publish,
        // search keystroke and catalog drain.
        [UnityTest]
        public IEnumerator RebindingRealizedRows_RewritesNoIconSizingStyle()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                var list = view.Q<ListView>("zep-asset-list");
                Assert.That(list, Is.Not.Null);
                var rowsBefore = RealizedRowElements(list);
                Assume.That(rowsBefore.Count, Is.GreaterThan(4),
                    "rows must be realized for a per-bind write count to mean anything");

                var binds = ListAssetWidget.BindCountForTests;
                var styleWrites = ListAssetWidget.StyleWritesForTests;
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                Assert.That(ListAssetWidget.BindCountForTests - binds, Is.GreaterThan(0),
                    "precondition: the republish must actually rebind the realized rows");
                Assume.That(RealizedRowElements(list), Is.EqualTo(rowsBefore),
                    "precondition: no row was created, so every icon-sizing write below "
                    + "would be a bind-path write");
                Assert.That(ListAssetWidget.StyleWritesForTests - styleWrites, Is.Zero,
                    "an already-made row is already sized — rebinding it must not rewrite "
                    + "the icon box and the icon image");

                var icon = list.GetRootElementForIndex(0)
                    ?.Q<VisualElement>("zep-asset-row-icon-image");
                Assert.That(icon, Is.Not.Null);
                Assert.That(
                    icon.resolvedStyle.width,
                    Is.EqualTo(Mathf.RoundToInt(list.fixedItemHeight * 0.7f)),
                    "the row must keep the icon geometry make-time gave it");
            }
            finally
            {
                window.Close();
            }
        }

        // The sub-asset row's tooltip is the one string its bind pass built
        // from scratch every time. A pooled row rebound to the same sub-asset
        // — the dominant case while scrolling or republishing an expansion —
        // must reuse the string it already has. The assertion is on the
        // allocation, not on the tooltip property: Unity's setter compares by
        // value and TextElement rewrites the property itself around eliding,
        // so the property can never show which string the binder built.
        [UnityTest]
        public IEnumerator ListSubAssetRow_ReboundWithTheSameKey_ReusesItsTooltipString()
        {
            const string fixturePath = "Assets/ZepListSubAssetTooltipFixture.asset";
            var main = ScriptableObject.CreateInstance<MetadataSubAssetFixture>();
            AssetDatabase.CreateAsset(main, fixturePath);
            var sub = ScriptableObject.CreateInstance<MetadataSubAssetFixture>();
            sub.name = "Embedded";
            AssetDatabase.AddObjectToAsset(sub, main);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(fixturePath, ImportAssetOptions.ForceUpdate);

            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                var assets = new[]
                {
                    new ZepAssetEntry(
                        "ZepListSubAssetTooltipFixture",
                        fixturePath,
                        "ScriptableObject")
                };
                view.BrowserFeature.SetAssets(assets);
                yield return null;

                SubAssetCatalog.GetOrRequest(fixturePath);
                for (var frame = 0;
                     frame < 60 && SubAssetCatalog.GetResolved(fixturePath) == null;
                     frame++)
                {
                    yield return null;
                }

                Assert.That(SubAssetCatalog.GetResolved(fixturePath), Has.Length.EqualTo(1));
                var listWidget = view.Q<ListAssetWidget>();
                Assert.That(listWidget, Is.Not.Null);
                listWidget.ExpandSubAssets(fixturePath);
                yield return null;
                yield return null;

                var subLabel = NameLabelOfRow(view, 1);
                Assert.That(subLabel, Is.Not.Null, "the sub-asset row must be realized");
                Assert.That(subLabel.text, Is.EqualTo("Embedded"),
                    "precondition: visible row 1 must be the expanded sub-asset row");

                var binds = ListAssetWidget.BindCountForTests;
                var tooltipBuilds = ListAssetWidget.SubAssetTooltipBuildsForTests;
                view.BrowserFeature.SetAssets(assets);
                yield return null;

                Assert.That(ListAssetWidget.BindCountForTests - binds, Is.GreaterThan(0),
                    "precondition: the republish must actually rebind the rows");
                Assert.That(NameLabelOfRow(view, 1), Is.SameAs(subLabel),
                    "the rebind must land on the same pooled row for this to mean anything");
                Assert.That(subLabel.text, Is.EqualTo("Embedded"),
                    "the rebind must still render the same sub-asset");
                Assert.That(
                    ListAssetWidget.SubAssetTooltipBuildsForTests - tooltipBuilds,
                    Is.Zero,
                    "an unchanged sub-asset key must reuse the tooltip string, not rebuild it");
            }
            finally
            {
                window.Close();
                AssetDatabase.DeleteAsset(fixturePath);
            }
        }

        // A catalog drain used to rebind the whole realized window: the
        // Changed signal said only "something resolved", so every row paid a
        // full bind pass for the two rows whose disclosure arrow could
        // actually have changed. The signal now carries the resolved paths.
        [UnityTest]
        public IEnumerator CatalogDrain_RebindsOnlyTheRowsTheBatchNames()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                var list = view.Q<ListView>("zep-asset-list");
                Assert.That(list, Is.Not.Null);
                var realized = RealizedRowCount(list);
                Assume.That(realized, Is.GreaterThan(4),
                    "the viewport must realize a window of rows for a bind count to mean anything");

                SubAssetCatalog.ResetForTests();
                SubAssetCatalog.ResolveOverrideForTests = _ => Array.Empty<SubAssetDescriptor>();
                try
                {
                    SubAssetCatalog.GetOrRequest("Assets/Asset0.asset");
                    SubAssetCatalog.GetOrRequest("Assets/Asset1.asset");
                    ListAssetWidget.BindCountForTests = 0;
                    SubAssetCatalog.TickForTests();

                    Assert.That(ListAssetWidget.BindCountForTests, Is.EqualTo(2),
                        "a drain that resolved two realized rows must rebind those two rows "
                        + "and nothing else");
                }
                finally
                {
                    SubAssetCatalog.ResolveOverrideForTests = null;
                    SubAssetCatalog.ResetForTests();
                }
            }
            finally
            {
                window.Close();
            }
        }

        // The inactive view mode is never detached — AssetCollectionView only
        // sets display:none on it — so it keeps hearing every catalog drain and
        // used to re-project and re-bind for one. Regression guard, not a
        // discriminating proof: measured in this batchmode rig, a ListView
        // hidden for a frame binds nothing on a refresh anyway (its rows are no
        // longer realized), so the count is zero with or without the guard.
        // What the guard buys here is the skipped projection pass.
        [UnityTest]
        public IEnumerator CatalogDrain_WhileTheListIsHidden_BindsNothing()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                var listWidget = view.Q<ListAssetWidget>();
                Assert.That(listWidget, Is.Not.Null);
                // Exactly what a view-mode switch does to the widget it leaves.
                listWidget.style.display = DisplayStyle.None;
                yield return null;

                Assume.That(listWidget.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));

                SubAssetCatalog.ResetForTests();
                SubAssetCatalog.ResolveOverrideForTests = _ => Array.Empty<SubAssetDescriptor>();
                try
                {
                    SubAssetCatalog.GetOrRequest("Assets/Asset0.asset");
                    ListAssetWidget.BindCountForTests = 0;
                    SubAssetCatalog.TickForTests();

                    Assert.That(ListAssetWidget.BindCountForTests, Is.Zero,
                        "a hidden widget must not bind a single row for a catalog drain");
                }
                finally
                {
                    SubAssetCatalog.ResolveOverrideForTests = null;
                    SubAssetCatalog.ResetForTests();
                }
            }
            finally
            {
                window.Close();
            }
        }

        // Same signal shape from the icon repository: the applied paths are in
        // hand when the poll raises, so one upgraded preview must rebind one
        // row rather than the whole window.
        [UnityTest]
        public IEnumerator IconPreviewsReady_RebindsOnlyTheRowsTheBatchNames()
        {
            var window = CreateWindow();
            try
            {
                var view = MountBrowser(window);
                view.BrowserFeature.SetAssets(BuildAssets(100));
                yield return null;
                yield return null;

                var list = view.Q<ListView>("zep-asset-list");
                Assert.That(list, Is.Not.Null);
                Assume.That(RealizedRowCount(list), Is.GreaterThan(4),
                    "the viewport must realize a window of rows for a bind count to mean anything");

                ListAssetWidget.BindCountForTests = 0;
                view.NotifyIconPreviewsReady(new[] { "Assets/Asset3.asset" });

                Assert.That(ListAssetWidget.BindCountForTests, Is.EqualTo(1),
                    "one applied preview must rebind the one row that shows it");
            }
            finally
            {
                window.Close();
            }
        }

        private static System.Collections.Generic.List<VisualElement> RealizedRowElements(
            ListView list)
        {
            var rows = new System.Collections.Generic.List<VisualElement>();
            for (var i = 0; i < list.itemsSource.Count; i++)
            {
                var row = list.GetRootElementForIndex(i);
                if (row != null)
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        private static Label NameLabelOfRow(ZepPaneView view, int visibleIndex)
        {
            var list = view.Q<ListView>("zep-asset-list");
            var row = list?.GetRootElementForIndex(visibleIndex);
            return row?.Q<Label>("zep-asset-row-name");
        }

        private static int RealizedRowCount(ListView list)
        {
            var realized = 0;
            for (var i = 0; i < list.itemsSource.Count; i++)
            {
                if (list.GetRootElementForIndex(i) != null)
                {
                    realized++;
                }
            }

            return realized;
        }

        private static Label NameLabelOfFirstRow(ZepPaneView view)
        {
            var list = view.Q<ListView>("zep-asset-list");
            var row = list?.GetRootElementForIndex(0);
            return row?.Q<Label>("zep-asset-row-name");
        }

        private static Label SecondaryLabelOfFirstRow(ZepPaneView view)
        {
            var list = view.Q<ListView>("zep-asset-list");
            var row = list?.GetRootElementForIndex(0);
            return row?.Q<Label>("zep-asset-row-secondary");
        }

        // Wide enough that the name column is well past its 140px minimum and
        // the pane sits above every responsive tier, so the layout tests below
        // measure a roomy row. Written on the pane view, not the window: a
        // batchmode window ignores its position rect and stays 640px wide.
        private const float WidePaneWidth = 1200f;

        private static MetadataDisplayTestWindow CreateWindow()
        {
            var window = ScriptableObject.CreateInstance<MetadataDisplayTestWindow>();
            window.position = new Rect(100f, 100f, 640f, 480f);
            window.Show();
            return window;
        }

        // A primary pane with no controller: its projections are null, so the
        // feature's pull source is null and explicitly pushed assets stay put
        // instead of being replaced by the real project's contents.
        private static ZepPaneView MountBrowser(EditorWindow window)
        {
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            AttachSheet(root, ZepUiClasses.PanelStyleSheetPath);
            AttachSheet(root, ZepUiClasses.DefaultsStyleSheetPath);

            var state = new ZepState();
            var view = new ZepPaneView(
                state,
                state.PrimaryPane,
                new UnityZepIconRepository());
            root.Add(view);

            view.AssetsArea.SetViewMode(AssetViewMode.List);
            view.BrowserFeature.SetAssets(Array.Empty<ZepAssetEntry>());
            return view;
        }

        private static void AttachSheet(VisualElement root, string path)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            Assert.That(sheet, Is.Not.Null, $"stylesheet missing: {path}");
            root.styleSheets.Add(sheet);
        }

        // §4.4: the widget used to hold its open sub-asset rows as a set of
        // paths and find them again by walking every path in the projection.
        // One opened row therefore made every later publication O(assets) — for
        // a filtered folder, on every keystroke — for the rest of the session.
        // The expansions are spans now, re-pointed through the order's own
        // index, so a publication costs one lookup per OPEN row.
        [Test]
        public void PublishingWithOpenSubAssets_CostsOneLookupPerExpansion_NotOneReadPerAsset()
        {
            const int assetCount = 5000;
            const string firstOpen = "Assets/Asset10.asset";
            const string secondOpen = "Assets/Asset4200.asset";

            var order = new CountingAssetOrder(BuildAssets(assetCount));
            var widget = new ListAssetWidget(
                new UnityZepIconRepository(),
                new AssetsAreaInlineEditController(),
                null);
            var sink = (IAssetWidget)widget;

            SubAssetCatalog.ResetForTests();
            SubAssetCatalog.ResolveOverrideForTests = path =>
                path == firstOpen || path == secondOpen
                    ? new[] { Descriptor(path, 1), Descriptor(path, 2) }
                    : Array.Empty<SubAssetDescriptor>();
            try
            {
                sink.SetAssets(order);
                widget.ExpandSubAssets(firstOpen);
                widget.ExpandSubAssets(secondOpen);
                SubAssetCatalog.TickForTests();
                Assume.That(
                    SubAssetCatalog.GetResolved(secondOpen),
                    Is.Not.Null.And.Length.EqualTo(2),
                    "both opened paths must have resolved for the projection to have rows");

                order.ResetCounts();
                ZepPathHelpers.LinearAssetPathScans = 0;
                sink.SetAssets(order);

                Assert.That(
                    order.IndexOfPathCalls,
                    Is.EqualTo(2),
                    "a publication resolves one index per open row, and nothing else");
                Assert.That(
                    order.EntryReads,
                    Is.Zero,
                    "and reads no entry at all — the rebuild it replaces read every one");
                Assert.That(
                    ZepPathHelpers.LinearAssetPathScans,
                    Is.Zero,
                    "the published order answers by index, never by scan");
                Assert.That(
                    widget.VisibleItemCount,
                    Is.EqualTo(assetCount + 4),
                    "and both expansions still project their two sub rows each");
            }
            finally
            {
                SubAssetCatalog.ResolveOverrideForTests = null;
                SubAssetCatalog.ResetForTests();
            }
        }

        // An open row whose path leaves the projection has no row left to hang
        // its sub rows under, so it closes rather than being carried (and
        // re-checked) by every later publication.
        [Test]
        public void PublishingWithoutAnOpenRowsPath_ClosesThatExpansion()
        {
            const string openPath = "Assets/Asset1.asset";
            var widget = new ListAssetWidget(
                new UnityZepIconRepository(),
                new AssetsAreaInlineEditController(),
                null);
            var sink = (IAssetWidget)widget;

            SubAssetCatalog.ResetForTests();
            SubAssetCatalog.ResolveOverrideForTests =
                path => new[] { Descriptor(path, 1) };
            try
            {
                sink.SetAssets(new CountingAssetOrder(BuildAssets(4)));
                widget.ExpandSubAssets(openPath);
                SubAssetCatalog.TickForTests();
                sink.SetAssets(new CountingAssetOrder(BuildAssets(4)));
                Assume.That(widget.IsSubAssetExpanded(openPath), Is.True);
                Assume.That(widget.VisibleItemCount, Is.EqualTo(5));

                sink.SetAssets(new CountingAssetOrder(BuildAssets(4, "Other")));

                Assert.That(widget.IsSubAssetExpanded(openPath), Is.False);
                Assert.That(widget.VisibleItemCount, Is.EqualTo(4));
            }
            finally
            {
                SubAssetCatalog.ResolveOverrideForTests = null;
                SubAssetCatalog.ResetForTests();
            }
        }

        private static SubAssetDescriptor Descriptor(string parentPath, long localId)
        {
            return new SubAssetDescriptor(
                parentPath,
                localId,
                runtimeId: default,
                name: "Sub" + localId,
                typeName: EntryTypeName,
                icon: null);
        }

        // A published order shaped like the real projections (they all answer
        // IAssetPathLookup), counting what the widget asks of it: entries read
        // and paths resolved.
        private sealed class CountingAssetOrder
            : IReadOnlyList<ZepAssetEntry>, IAssetPathLookup
        {
            private readonly ZepAssetEntry[] _entries;
            private readonly Dictionary<string, int> _indexByPath;

            internal CountingAssetOrder(ZepAssetEntry[] entries)
            {
                _entries = entries;
                _indexByPath = new Dictionary<string, int>(
                    entries.Length,
                    StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < entries.Length; i++)
                {
                    _indexByPath[entries[i].Path] = i;
                }
            }

            internal int EntryReads { get; private set; }
            internal int IndexOfPathCalls { get; private set; }

            internal void ResetCounts()
            {
                EntryReads = 0;
                IndexOfPathCalls = 0;
            }

            public int Count => _entries.Length;

            public ZepAssetEntry this[int index]
            {
                get
                {
                    EntryReads++;
                    return _entries[index];
                }
            }

            public int IndexOfPath(string assetPath)
            {
                IndexOfPathCalls++;
                return !string.IsNullOrEmpty(assetPath)
                    && _indexByPath.TryGetValue(assetPath, out var index)
                    ? index
                    : -1;
            }

            public IEnumerator<ZepAssetEntry> GetEnumerator()
            {
                for (var i = 0; i < _entries.Length; i++)
                {
                    yield return this[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class MetadataDisplayTestWindow : EditorWindow
        {
        }
    }

    internal sealed class MetadataSubAssetFixture : ScriptableObject
    {
    }
}
