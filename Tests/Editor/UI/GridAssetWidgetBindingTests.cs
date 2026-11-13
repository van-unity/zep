using System.Collections;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // What the grid's bind pass owes the rest of the panel, beyond painting a
    // tile: releasing an inline edit whose tile falls out of range, painting a
    // selected tile's card rather than its whole row-cell, keeping an open
    // disclosure and its children inside one group container, pitching rows
    // to the card at every zoom, and dropping a pending sub-asset ping whose
    // target never arrives.
    public sealed class GridAssetWidgetBindingTests
    {
        private const string SubAssetFixturePath =
            "Assets/ZepGridBindingSubAssetFixture.asset";
        private const string PlainFixturePath =
            "Assets/ZepGridBindingPlainFixture.asset";
        private const string CatalogTriggerFixturePath =
            "Assets/ZepGridBindingTriggerFixture.asset";
        private const int SubAssetCount = 12;
        private const int GridItemSize = 120;

        [SetUp]
        public void SetUp()
        {
            var withSubs = ScriptableObject.CreateInstance<GridNavFixtureAsset>();
            AssetDatabase.CreateAsset(withSubs, SubAssetFixturePath);
            for (var i = 0; i < SubAssetCount; i++)
            {
                var sub = ScriptableObject.CreateInstance<GridNavFixtureAsset>();
                sub.name = "Sub" + i;
                AssetDatabase.AddObjectToAsset(sub, withSubs);
            }

            AssetDatabase.CreateAsset(
                ScriptableObject.CreateInstance<GridNavFixtureAsset>(),
                PlainFixturePath);
            AssetDatabase.CreateAsset(
                ScriptableObject.CreateInstance<GridNavFixtureAsset>(),
                CatalogTriggerFixturePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(SubAssetFixturePath, ImportAssetOptions.ForceUpdate);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(SubAssetFixturePath);
            AssetDatabase.DeleteAsset(PlainFixturePath);
            AssetDatabase.DeleteAsset(CatalogTriggerFixturePath);
        }

        [Test]
        public void ModifiedDateTooltip_SamePathRefreshesChangedAndAbsentDates()
        {
            System.DateTime? modified = new System.DateTime(2026, 1, 1, 12, 0, 0);
            var grid = new GridAssetWidget(new UnityZepIconRepository(), GridItemSize,
                new AssetsAreaInlineEditController(), (_, __) => new AssetPresentationData(modified, null, false));
            var tile = (VisualElement)typeof(GridAssetWidget)
                .GetMethod("MakeGridItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(grid, null);
            var bind = typeof(GridAssetWidget).GetMethod("BindGridItem", BindingFlags.Instance | BindingFlags.NonPublic);
            var entry = new ZepAssetEntry("Plain", PlainFixturePath, "ScriptableObject");
            var arguments = new object[] { tile, entry, 0, false, false };
            bind.Invoke(grid, arguments);
            var parts = (GridAssetWidget.AssetGridItemParts)tile.userData;
            var firstTooltip = parts.Name.tooltip;
            Assert.That(firstTooltip, Is.EqualTo(PlainFixturePath + "\n" + AssetEntryRenderer.FormatModifiedDate(modified)));

            bind.Invoke(grid, arguments);
            Assert.That(parts.Name.tooltip, Is.SameAs(firstTooltip), "unchanged rebinding retains the tooltip value");

            modified = modified.Value.AddDays(1);
            bind.Invoke(grid, arguments);
            Assert.That(parts.Name.tooltip, Is.EqualTo(PlainFixturePath + "\n" + AssetEntryRenderer.FormatModifiedDate(modified)),
                "a new timestamp must replace the previous date even when all other binding inputs match");

            modified = null;
            bind.Invoke(grid, arguments);
            Assert.That(parts.Name.tooltip, Is.EqualTo(PlainFixturePath), "an absent timestamp leaves only the path");
        }

        [Test]
        public void ModifiedDateTooltip_MainSubMainRecyclingReadsTheCurrentDate()
        {
            var modified = new System.DateTime(2026, 1, 1, 12, 0, 0);
            var grid = new GridAssetWidget(new UnityZepIconRepository(), GridItemSize,
                new AssetsAreaInlineEditController(), (_, __) => new AssetPresentationData(modified, null, false));
            var tile = (VisualElement)typeof(GridAssetWidget)
                .GetMethod("MakeGridItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(grid, null);
            var bind = typeof(GridAssetWidget).GetMethod("BindGridItem", BindingFlags.Instance | BindingFlags.NonPublic);
            var entry = new ZepAssetEntry("Plain", PlainFixturePath, "ScriptableObject");
            var arguments = new object[] { tile, entry, 0, false, false };
            bind.Invoke(grid, arguments);

            var sub = new SubAssetDescriptor(SubAssetFixturePath, 1, default, "Child", "ScriptableObject", null);
            typeof(GridAssetWidget).GetMethod("BindSubAssetGridItem", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(grid, new object[] { tile, sub, 0, false, false, false });
            var parts = (GridAssetWidget.AssetGridItemParts)tile.userData;
            Assert.That(parts.Name.tooltip, Is.EqualTo(SubAssetFixturePath + " › Child"));

            modified = modified.AddDays(2);
            bind.Invoke(grid, arguments);
            Assert.That(parts.Name.tooltip, Is.EqualTo(PlainFixturePath + "\n" + AssetEntryRenderer.FormatModifiedDate(modified)),
                "a tile recycled through a child must display the current main asset metadata");
        }

        [Test]
        public void SubAssetTooltip_RenameWithStableIdentityRefreshesAndThenReusesItsString()
        {
            var grid = new GridAssetWidget(new UnityZepIconRepository(), GridItemSize,
                new AssetsAreaInlineEditController(), null);
            var tile = (VisualElement)typeof(GridAssetWidget)
                .GetMethod("MakeGridItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(grid, null);
            var bind = typeof(GridAssetWidget).GetMethod("BindSubAssetGridItem", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = new SubAssetDescriptor(SubAssetFixturePath, 1, default, "Original", "ScriptableObject", null);
            bind.Invoke(grid, new object[] { tile, original, 0, false, false, false });
            var parts = (GridAssetWidget.AssetGridItemParts)tile.userData;
            Assert.That(parts.Name.tooltip, Is.EqualTo(SubAssetFixturePath + " › Original"));
            var builds = GridAssetWidget.SubAssetTooltipBuildsForTests;

            var renamed = new SubAssetDescriptor(SubAssetFixturePath, 1, default, "Renamed", "ScriptableObject", null);
            bind.Invoke(grid, new object[] { tile, renamed, 0, false, false, false });
            Assert.That(parts.Name.text, Is.EqualTo("Renamed"));
            Assert.That(parts.Name.tooltip, Is.EqualTo(SubAssetFixturePath + " › Renamed"));
            Assert.That(GridAssetWidget.SubAssetTooltipBuildsForTests - builds, Is.EqualTo(1));

            for (var i = 0; i < 3; i++)
            {
                var unchanged = new SubAssetDescriptor(SubAssetFixturePath, 1, default, "Renamed", "ScriptableObject", null);
                bind.Invoke(grid, new object[] { tile, unchanged, 0, false, false, false });
            }

            Assert.That(GridAssetWidget.SubAssetTooltipBuildsForTests - builds, Is.EqualTo(1),
                "equivalent descriptors must reuse the string after the one rename rebuild");
        }

        // A tail shrink (external delete) leaves the row that hosted the edit
        // BOUND — only the tile falls past the item count, so unbindItem never
        // runs and the cleared-tile leg is the only place the session can end.
        // Without the release the edit stays alive on a hidden, still-focused
        // TextField and the panel keyboard goes dead until that tile rebinds.
        [UnityTest]
        public IEnumerator ClearedTile_PastTheItemCount_ReleasesTheInlineEditExactlyOnce()
        {
            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            var commitCount = 0;
            controller.Aborted += () => abortCount++;
            controller.Confirmed += _ => commitCount++;

            var window = CreateWindow();
            try
            {
                var view = MountGridView(window, controller, assetCount: 1);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                var columns = grid.ColumnsPerRow;
                Assume.That(columns, Is.GreaterThan(1),
                    "the shrink under test must leave the last ROW bound and only the last TILE empty");

                // Two full rows, then one asset fewer: the second row keeps its
                // binding and its last tile takes the cleared-tile leg.
                var fullAssets = BuildAssets(columns * 2);
                view.SetAssets(fullAssets);
                yield return null;

                var editedPath = fullAssets[fullAssets.Length - 1].Path;
                controller.SetDescriptor(new AssetsAreaInlineEditDescriptor(
                    editedPath,
                    "GridBindingItem",
                    ".asset",
                    isGhost: false));
                view.SetAssets(fullAssets);
                yield return null;

                var editTile = FindTileParts(grid, editedPath);
                Assert.That(editTile, Is.Not.Null, "the edited asset's tile must be realized");
                Assert.That(editTile.CachedIsEditRow, Is.True,
                    "precondition: the bind pass must have made this tile the edit tile");
                Assert.That(controller.IsActiveForPath(editedPath), Is.True);

                view.SetAssets(BuildAssets(columns * 2 - 1));
                yield return null;
                yield return null;

                Assert.That(editTile.Entry, Is.Null,
                    "precondition: the shrink must have cleared the tile, not rebound it");
                Assert.That(controller.Descriptor.IsActive, Is.False,
                    "a tile cleared past the item count must end the session — hiding a tile does "
                    + "not blur its field, so the panel keyboard stays trapped otherwise");
                Assert.That(abortCount, Is.EqualTo(1));
                Assert.That(commitCount, Is.Zero,
                    "an asset that left the projection is a cancel, never a rename");
            }
            finally
            {
                window.Close();
            }
        }

        // A tile root is the fixed row-cell (the click target), taller than the
        // icon and one-line name it shows at most zooms. The selection fill
        // used to land on that root, so a selected tile painted a slab of
        // blank cell below its name. The card is what hugs the content.
        [UnityTest]
        public IEnumerator SelectedTile_PaintsItsCard_NotTheRowCell()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 4);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                const string path = "Assets/GridBindingItem01.asset";
                view.SetSelection(new[] { AssetPath.From(path) });
                yield return null;
                yield return null;

                var tile = FindTileParts(grid, path);
                Assert.That(tile, Is.Not.Null, "the selected asset's tile must be realized");
                Assert.That(tile.Card.resolvedStyle.backgroundColor.a, Is.GreaterThan(0f),
                    "a selected tile's card must carry the selection accent");
                Assert.That(tile.Root.resolvedStyle.backgroundColor.a,
                    Is.EqualTo(0f).Within(0.004f),
                    "the row-cell root must stay unpainted — a root fill is the blank slab");
                Assert.That(tile.Card.resolvedStyle.height,
                    Is.LessThan(tile.Root.resolvedStyle.height),
                    "the card must hug the icon and name, not stretch to the row-cell");
            }
            finally
            {
                window.Close();
            }
        }

        // An open disclosure and its children share ONE group container: the
        // parent's tile draws its first segment (under the parent's own card,
        // so the container never collides with the parent's selection), every
        // child continues it, and a child that wraps onto a new row re-opens
        // it with the notch. Selecting a child paints that child's card and
        // leaves the child group fill alone.
        [UnityTest]
        public IEnumerator ExpandedParent_AndItsChildren_ShareOneGroupContainer()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 1,
                    firstAssetPath: SubAssetFixturePath);
                yield return null;
                yield return ResolveSubAssets(SubAssetFixturePath);

                var grid = view.Q<GridAssetWidget>();
                grid.ExpandSubAssets(SubAssetFixturePath);
                yield return null;
                yield return null;

                var columns = grid.ColumnsPerRow;
                Assume.That(columns, Is.GreaterThan(1).And.LessThan(SubAssetCount + 1),
                    "the fixture must seat the parent and some children on one row and wrap the rest");

                var parent = FindTileParts(grid, SubAssetFixturePath);
                Assert.That(parent, Is.Not.Null);
                Assert.That(parent.Root.ClassListContains(ZepUiClasses.AssetGridItemGrouped),
                    Is.True, "the parent tile is the group's first segment");
                Assert.That(parent.Root.ClassListContains(ZepUiClasses.AssetGridSubAssetSegmentStart),
                    Is.True, "the group opens on the parent, not on its first child");
                Assert.That(parent.Root.ClassListContains(ZepUiClasses.AssetGridSubAssetSegmentEnd),
                    Is.False, "a parent with a child beside it does not close the container");
                Assert.That(parent.SubAssetGroupBackground.resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.Flex));
                var parentFill = parent.SubAssetGroupBackground.resolvedStyle.backgroundColor;
                Assert.That(parentFill.a, Is.GreaterThan(0f),
                    "the group container must resolve a visible fill");

                var subs = RealizedSubAssetParts(grid)
                    .OrderBy(parts => parts.BoundVisibleIndex)
                    .ToList();
                Assume.That(subs.Count, Is.GreaterThan(columns),
                    "children must wrap onto a second realized row");
                var groupFill = subs[0].SubAssetGroupBackground.resolvedStyle.backgroundColor;
                Assert.That(groupFill, Is.Not.EqualTo(parentFill),
                    "the children must have a quieter surface than the parent");
                Assert.That(parent.SubAssetGroupBackground.resolvedStyle.borderRightWidth,
                    Is.EqualTo(1f), "a divider separates the parent from its children");
                Assert.That(parent.SubAssetType.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(parent.Disclosure.worldBound.xMin, Is.GreaterThanOrEqualTo(parent.Card.worldBound.xMin));
                Assert.That(parent.Disclosure.worldBound.xMax, Is.LessThanOrEqualTo(parent.Card.worldBound.xMax));
                Assert.That(parent.Disclosure.worldBound.yMin, Is.GreaterThanOrEqualTo(parent.Card.worldBound.yMin));
                var lastChildVisibleIndex = grid.AssetIndexToVisibleIndex(0) + SubAssetCount;
                foreach (var sub in subs)
                {
                    var column = sub.BoundVisibleIndex % columns;
                    var wraps = column == 0;
                    Assert.That(sub.Root.ClassListContains(ZepUiClasses.AssetGridItemGrouped),
                        Is.True, "every child continues the container");
                    Assert.That(sub.Root.ClassListContains(ZepUiClasses.AssetGridSubAssetSegmentStart),
                        Is.EqualTo(wraps), "only a child that wraps onto a new row re-opens the container");
                    Assert.That(sub.Root.ClassListContains(ZepUiClasses.AssetGridSubAssetContinuation),
                        Is.EqualTo(wraps), "the notch marks exactly the wrapped rows");
                    Assert.That(sub.Root.ClassListContains(ZepUiClasses.AssetGridSubAssetSegmentEnd),
                        Is.EqualTo(column == columns - 1 || sub.BoundVisibleIndex == lastChildVisibleIndex),
                        "the container closes at the row's end and after the last child");
                    AssertSameColor(sub.SubAssetGroupBackground.resolvedStyle.backgroundColor, groupFill,
                        "every child segment paints the same group fill");
                    Assert.That(sub.SubAssetType.text, Is.EqualTo(sub.SubAsset.TypeName));
                    Assert.That(sub.SubAssetType.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                    Assert.That(sub.IconImage.worldBound.width,
                        Is.LessThan(parent.IconImage.worldBound.width * 0.9f),
                        "child previews are visually subordinate without shrinking the click target");
                }

                var firstChild = subs[0];
                grid.SetSubAssetHighlight(firstChild.SubAsset.Key);
                yield return null;
                yield return null;

                Assert.That(firstChild.Card.resolvedStyle.backgroundColor.a, Is.GreaterThan(0f),
                    "a selected child paints its own card, like any tile");
                Assert.That(firstChild.Root.resolvedStyle.backgroundColor.a,
                    Is.EqualTo(0f).Within(0.004f),
                    "the child's row-cell root stays unpainted");
                AssertSameColor(firstChild.SubAssetGroupBackground.resolvedStyle.backgroundColor, groupFill,
                    "selecting a child must not repaint the group container");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ExpandedChildren_AtEveryZoom_FitTheirCells_AndResetWhenReused()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(window, new AssetsAreaInlineEditController(),
                    assetCount: 8, firstAssetPath: SubAssetFixturePath);
                yield return ResolveSubAssets(SubAssetFixturePath);
                var grid = view.Q<GridAssetWidget>();
                grid.ExpandSubAssets(SubAssetFixturePath);
                foreach (var size in new[] { AssetsAreaConstants.MinAssetGridItemSize,
                             GridItemSize, AssetsAreaConstants.MaxAssetGridItemSize })
                {
                    view.SetGridItemSize(size, AssetsAreaConstants.MinAssetGridItemSize,
                        AssetsAreaConstants.MaxAssetGridItemSize);
                    yield return null;
                    yield return null;
                    yield return null;
                    var children = RealizedSubAssetParts(grid);
                    Assert.That(children, Is.Not.Empty);
                    var parent = FindTileParts(grid, SubAssetFixturePath);
                    Assert.That(parent.Disclosure.worldBound.xMax,
                        Is.EqualTo(parent.Card.worldBound.xMax - 3f).Within(0.5f),
                        "the disclosure stays beside the children at zoom " + size);
                    Assert.That(parent.Disclosure.worldBound.center.y,
                        Is.EqualTo(parent.Icon.worldBound.center.y).Within(0.5f),
                        "the disclosure tracks the preview's center at zoom " + size);
                    foreach (var child in children)
                    {
                        Assert.That(child.SubAssetType.worldBound.yMax,
                            Is.LessThanOrEqualTo(child.Card.worldBound.yMax));
                        Assert.That(child.Card.resolvedStyle.height,
                            Is.LessThanOrEqualTo(child.Root.resolvedStyle.height + 0.5f),
                            "type labels must not overflow the next row at zoom " + size);
                    }
                }

                view.SetAssets(BuildAssets(20));
                yield return null;
                yield return null;
                foreach (var item in grid.Query<VisualElement>(className: ZepUiClasses.AssetGridItem).ToList())
                {
                    var parts = item.userData as GridAssetWidget.AssetGridItemParts;
                    if (parts?.Entry == null) continue;
                    Assert.That(parts.SubAssetType.resolvedStyle.display, Is.EqualTo(DisplayStyle.None),
                        "recycled main tiles must not show the previous child's type");
                    Assert.That(parts.IconImage.worldBound.width,
                        Is.EqualTo(parts.IconImage.resolvedStyle.width).Within(0.5f),
                        "recycled main tiles must regain their full preview size");
                    Assert.That(parts.SubAssetGroupBackground.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                }
            }
            finally
            {
                window.Close();
            }
        }

        // A ping forces its parent open before the catalog can answer. When the
        // answer finally says the sub-asset is not there, the forced expansion
        // has nothing to show and must not stay pinned open forever.
        [UnityTest]
        public IEnumerator PendingSubAssetPing_WhoseTargetNeverArrives_LeavesNoPinnedExpansion()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 1,
                    firstAssetPath: PlainFixturePath);
                yield return null;
                yield return ResolveSubAssets(PlainFixturePath);

                Assert.That(SubAssetCatalog.GetResolved(PlainFixturePath), Is.Empty,
                    "the fixture for this test must have no sub-assets at all");

                var grid = view.Q<GridAssetWidget>();
                grid.PingSubAsset(
                    PlainFixturePath,
                    SubAssetDescriptor.MakeKey(PlainFixturePath, 4711));
                Assert.That(grid.ExpandedAssetCount, Is.EqualTo(1),
                    "precondition: the ping forces the parent open before the target is known");

                // Any later resolve batch re-evaluates every expansion.
                yield return ResolveSubAssets(CatalogTriggerFixturePath);
                yield return null;

                Assert.That(grid.ExpandedAssetCount, Is.Zero,
                    "a ping whose target is not in the resolved list must release its forced "
                    + "expansion instead of pinning an empty one open for the session");
            }
            finally
            {
                window.Close();
            }
        }

        // The tile roots take their new size from inline styles the moment the
        // zoom changes, but the outer ListView's realized row wrappers are
        // positioned by the virtualization controller. If a zoom leaves those
        // wrappers at the old pitch, every tile silently overflows its row and
        // the selection rectangle of any tile paints across the row below it.
        [UnityTest]
        public IEnumerator GridZoom_RaisingTheItemSize_RepitchesTheRealizedRows()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 24);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                Assume.That(RealizedRowWrappers(grid), Has.Count.GreaterThan(1),
                    "the fixture must realize at least two rows for a pitch to exist");

                view.SetGridItemSize(
                    AssetsAreaConstants.MaxAssetGridItemSize,
                    AssetsAreaConstants.MinAssetGridItemSize,
                    AssetsAreaConstants.MaxAssetGridItemSize);
                yield return null;
                yield return null;
                yield return null;

                var rowHeight = grid.FixedItemHeight;
                var wrappers = RealizedRowWrappers(grid);
                Assert.That(wrappers, Has.Count.GreaterThan(1));
                for (var i = 0; i < wrappers.Count; i++)
                {
                    Assert.That(wrappers[i].layout.height,
                        Is.EqualTo(rowHeight).Within(0.5f),
                        "a realized row wrapper must take the zoomed fixed height");
                    if (i > 0)
                    {
                        Assert.That(wrappers[i].layout.y - wrappers[i - 1].layout.y,
                            Is.EqualTo(rowHeight).Within(0.5f),
                            "consecutive rows must sit one zoomed row height apart");
                    }
                }

                var tile = grid
                    .Query<VisualElement>(className: ZepUiClasses.AssetGridItem)
                    .ToList()
                    .First(item => item.userData is GridAssetWidget.AssetGridItemParts parts
                        && parts.Entry != null);
                Assert.That(tile.resolvedStyle.height, Is.LessThanOrEqualTo(rowHeight),
                    "a tile taller than its row paints its selection over the row below");
            }
            finally
            {
                window.Close();
            }
        }

        // n columns need the row's side padding, n tiles and n - 1 gaps. The
        // column count used to charge a trailing gap for the last column as
        // well, so a viewport that fit another tile but not tile-plus-gap
        // wrapped it onto the next row and left a tile-wide hole at the end.
        [UnityTest]
        public IEnumerator ColumnCount_DoesNotChargeATrailingGapForTheLastColumn()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 3);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                var viewport = grid.ScrollView.contentViewport;
                var chrome = view.resolvedStyle.width - viewport.resolvedStyle.width;
                Assume.That(chrome, Is.GreaterThanOrEqualTo(0f).And.LessThan(64f),
                    "the grid viewport must sit inside the view by a small fixed chrome");

                // Room for three tiles and the two gaps between them, plus 2px
                // of slack — but not for a third trailing gap.
                const int columns = 3;
                var viewportWidth = 8f + columns * GridItemSize + (columns - 1) * 8f + 2f;
                view.style.flexGrow = 0f;
                view.style.width = viewportWidth + chrome;
                yield return null;
                yield return null;

                Assume.That(viewport.resolvedStyle.width, Is.EqualTo(viewportWidth).Within(0.5f),
                    "the viewport must take the width under test");
                Assert.That(grid.ColumnsPerRow, Is.EqualTo(columns),
                    "a row that fits n tiles and n - 1 gaps holds n columns");

                var lastTile = FindTileParts(grid, "Assets/GridBindingItem02.asset");
                Assert.That(lastTile, Is.Not.Null, "the third tile must sit on the first row");
                Assert.That(lastTile.BoundVisibleIndex, Is.EqualTo(2));
                Assert.That(lastTile.Root.worldBound.xMax,
                    Is.LessThanOrEqualTo(viewport.worldBound.xMax + 0.5f),
                    "the last column's tile must still fit inside the viewport");
            }
            finally
            {
                window.Close();
            }
        }

        // The row pitch derives from the card's chrome (TileChromeHeight), not
        // from a constant tuned at one zoom: base + 18 let cards overflow their
        // row at the minimum size and left a blank slab under them at the
        // maximum. A card taller than its cell paints over the row below.
        [UnityTest]
        public IEnumerator GridZoom_AtBothExtremes_KeepsEveryCardInsideItsRowCell()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 12);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                foreach (var size in new[]
                         {
                             AssetsAreaConstants.MinAssetGridItemSize,
                             AssetsAreaConstants.MaxAssetGridItemSize,
                         })
                {
                    view.SetGridItemSize(
                        size,
                        AssetsAreaConstants.MinAssetGridItemSize,
                        AssetsAreaConstants.MaxAssetGridItemSize);
                    yield return null;
                    yield return null;
                    yield return null;

                    var tiles = grid
                        .Query<VisualElement>(className: ZepUiClasses.AssetGridItem)
                        .ToList()
                        .Select(item => item.userData as GridAssetWidget.AssetGridItemParts)
                        .Where(parts => parts?.Entry != null)
                        .ToList();
                    Assume.That(tiles, Is.Not.Empty, "tiles must be realized at size " + size);
                    foreach (var tile in tiles)
                    {
                        Assert.That(tile.Root.resolvedStyle.height,
                            Is.EqualTo(grid.FixedItemHeight - 8f).Within(0.5f),
                            "the row-cell must fill its row minus the row gap at size " + size);
                        Assert.That(tile.Card.resolvedStyle.height,
                            Is.LessThanOrEqualTo(tile.Root.resolvedStyle.height + 0.5f),
                            "a card taller than its row-cell paints over the row below at size " + size);
                    }
                }
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------

        private static GridAssetWidget.AssetGridItemParts FirstRealizedSubAssetParts(
            GridAssetWidget grid)
        {
            return grid
                .Query<VisualElement>(className: ZepUiClasses.AssetGridItemSubAsset)
                .ToList()
                .Select(item => item.userData as GridAssetWidget.AssetGridItemParts)
                .FirstOrDefault(parts => parts?.SubAsset != null);
        }

        // The ListView's realized virtualization wrappers, top to bottom — the
        // elements whose height and stacking the fixed-height controller owns.
        private static System.Collections.Generic.List<VisualElement> RealizedRowWrappers(
            GridAssetWidget grid)
        {
            return grid
                .Query<VisualElement>(className: "unity-collection-view__item")
                .ToList()
                .Where(wrapper => wrapper.resolvedStyle.display != DisplayStyle.None)
                .OrderBy(wrapper => wrapper.layout.y)
                .ToList();
        }

        private static System.Collections.Generic.List<GridAssetWidget.AssetGridItemParts>
            RealizedSubAssetParts(GridAssetWidget grid)
        {
            return grid
                .Query<VisualElement>(className: ZepUiClasses.AssetGridItemSubAsset)
                .ToList()
                .Select(item => item.userData as GridAssetWidget.AssetGridItemParts)
                .Where(parts => parts?.SubAsset != null)
                .ToList();
        }

        private static void AssertSameColor(Color actual, Color expected, string because)
        {
            Assert.That(
                Mathf.Abs(actual.r - expected.r)
                    + Mathf.Abs(actual.g - expected.g)
                    + Mathf.Abs(actual.b - expected.b)
                    + Mathf.Abs(actual.a - expected.a),
                Is.LessThan(0.02f),
                because);
        }

        private static GridAssetWidget.AssetGridItemParts FindTileParts(
            GridAssetWidget grid,
            string assetPath)
        {
            return grid
                .Query<VisualElement>(className: ZepUiClasses.AssetGridItem)
                .ToList()
                .Select(item => item.userData as GridAssetWidget.AssetGridItemParts)
                .FirstOrDefault(parts =>
                    parts != null
                    && string.Equals(parts.Entry?.Path, assetPath, System.StringComparison.Ordinal));
        }

        private static IEnumerator ResolveSubAssets(string assetPath)
        {
            SubAssetCatalog.GetOrRequest(assetPath);
            for (var frame = 0;
                frame < 120 && SubAssetCatalog.GetResolved(assetPath) == null;
                frame++)
            {
                yield return null;
            }

            Assert.That(
                SubAssetCatalog.GetResolved(assetPath),
                Is.Not.Null,
                "the sub-asset catalog never resolved " + assetPath);
        }

        private static ZepAssetEntry[] BuildAssets(int count, string firstAssetPath = null)
        {
            var assets = new ZepAssetEntry[count];
            for (var i = 0; i < count; i++)
            {
                var path = i == 0 && firstAssetPath != null
                    ? firstAssetPath
                    : $"Assets/GridBindingItem{i:D2}.asset";
                assets[i] = new ZepAssetEntry(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    path,
                    "ScriptableObject");
            }

            return assets;
        }

        // Mounted only when the caller asks: the strip-lead test has to expand
        // BEFORE the first layout pass, which is what makes its tiles fresh.
        // Widening the window grows every realized row to the new column count.
        // Narrowing it used to only hide the surplus tiles, so a wide-then-narrow
        // resize left each row holding tiles — with their icons and their entry
        // in the tile registry every selection and drop-target sweep walks —
        // that nothing could ever bind again.
        [UnityTest]
        public IEnumerator NarrowingTheGrid_ReleasesTheTilesTheNewColumnCountCannotBind()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 24);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                var wideColumns = grid.ColumnsPerRow;
                Assume.That(wideColumns, Is.GreaterThan(2),
                    "the shrink under test needs room to lose columns");
                var wideTiles = grid.RealizedTileCount;
                Assume.That(wideTiles, Is.GreaterThan(0), "rows must be realized to hold tiles");

                view.style.width = GridItemSize * 2f;
                yield return null;
                yield return null;

                var narrowColumns = grid.ColumnsPerRow;
                Assume.That(narrowColumns, Is.LessThan(wideColumns), "the width change must cost columns");

                foreach (var tileRow in grid.Query<VisualElement>(
                             "zep-asset-grid-tile-row").ToList())
                {
                    Assert.That(
                        tileRow.childCount,
                        Is.EqualTo(narrowColumns),
                        "a realized row must hold exactly the tiles the current column "
                        + "count can bind — no hidden leftovers from the wider layout");
                }

                Assert.That(
                    grid.RealizedTileCount,
                    Is.LessThan(wideTiles),
                    "the released tiles must leave the registry the sweeps walk");
            }
            finally
            {
                window.Close();
            }
        }

        // A tile's width, height and icon boxes come from the grid item size and
        // nothing else, and the only thing that can change that size drops the
        // tile pool and rebuilds every row. Writing them again on every bind is
        // pure cost — one style write per box per tile per publish, per search
        // keystroke, per catalog drain — and it buys nothing a fresh tile does
        // not already have.
        [UnityTest]
        public IEnumerator RebindingRealizedTiles_RewritesNoStaticSizingStyle()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 24);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                var tilesBefore = grid.RealizedTileCount;
                Assume.That(tilesBefore, Is.GreaterThan(0), "rows must be realized to rebind");

                var binds = GridAssetWidget.BindCountForTests;
                var styleWrites = GridAssetWidget.StyleWritesForTests;
                view.SetAssets(BuildAssets(24));
                yield return null;

                Assert.That(GridAssetWidget.BindCountForTests - binds, Is.GreaterThan(0),
                    "precondition: the republish must actually rebind the realized tiles");
                Assume.That(grid.RealizedTileCount, Is.EqualTo(tilesBefore),
                    "precondition: no tile was created, so every sizing write below "
                    + "would be a bind-path write");
                Assert.That(GridAssetWidget.StyleWritesForTests - styleWrites, Is.Zero,
                    "an already-made tile is already sized — rebinding it must not "
                    + "rewrite width/height on the tile, its icon box and its strip");

                var tile = FindTileParts(grid, "Assets/GridBindingItem01.asset");
                Assert.That(tile, Is.Not.Null, "the rebound tile must still be realized");
                Assert.That(tile.Root.resolvedStyle.width, Is.EqualTo((float)GridItemSize),
                    "the tile must keep the size make-time gave it");
                Assert.That(tile.Root.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "a rebound tile must stay shown — out-of-range tiles are hidden by "
                    + "visibility, so no bind-path display write exists to restore it");
            }
            finally
            {
                window.Close();
            }
        }

        // A sub-asset tile has no per-bind geometry: its place in the group
        // container is carried by segment classes alone, so rebinding it must
        // write no inline sizing at all (the strip lead it once wrote per bind
        // is gone with the strip).
        [UnityTest]
        public IEnumerator RebindingSubAssetTiles_RewritesNoSizingStyle()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 8,
                    firstAssetPath: SubAssetFixturePath);
                yield return null;
                yield return ResolveSubAssets(SubAssetFixturePath);

                var grid = view.Q<GridAssetWidget>();
                grid.ExpandSubAssets(SubAssetFixturePath);
                yield return null;
                yield return null;

                var tilesBefore = grid.RealizedTileCount;
                Assume.That(RealizedSubAssetTileCount(grid), Is.GreaterThan(0),
                    "the expansion must have realized sub-asset tiles");

                var styleWrites = GridAssetWidget.StyleWritesForTests;
                view.SetAssets(BuildAssets(8, SubAssetFixturePath));
                yield return null;

                Assume.That(grid.RealizedTileCount, Is.EqualTo(tilesBefore),
                    "precondition: no tile was created during the republish");
                Assume.That(RealizedSubAssetTileCount(grid), Is.GreaterThan(0),
                    "the republish must keep the expansion");

                Assert.That(GridAssetWidget.StyleWritesForTests - styleWrites, Is.Zero,
                    "rebinding sub-asset tiles must not write inline sizing");
            }
            finally
            {
                window.Close();
            }
        }

        // The sub-asset tile's tooltip is the one string its bind pass built
        // from scratch every time. A pooled tile rebound to the same sub-asset
        // must reuse the string it already has. The assertion is on the
        // allocation, not on the tooltip property: Unity's setter compares by
        // value and TextElement rewrites the property itself around eliding,
        // so the property can never show which string the binder built.
        [UnityTest]
        public IEnumerator SubAssetTile_ReboundWithTheSameKey_ReusesItsTooltipString()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 8,
                    firstAssetPath: SubAssetFixturePath);
                yield return null;
                yield return ResolveSubAssets(SubAssetFixturePath);

                var grid = view.Q<GridAssetWidget>();
                grid.ExpandSubAssets(SubAssetFixturePath);
                yield return null;
                yield return null;

                var parts = FirstRealizedSubAssetParts(grid);
                Assert.That(parts, Is.Not.Null, "the expansion must have realized a sub-asset tile");
                var key = parts.SubAsset.Key;
                var binds = GridAssetWidget.BindCountForTests;
                var tooltipBuilds = GridAssetWidget.SubAssetTooltipBuildsForTests;
                view.SetAssets(BuildAssets(8, SubAssetFixturePath));
                yield return null;

                Assert.That(GridAssetWidget.BindCountForTests - binds, Is.GreaterThan(0),
                    "precondition: the republish must actually rebind the realized tiles");
                Assume.That(parts.SubAsset?.Key, Is.EqualTo(key),
                    "precondition: the pooled tile must still host the same sub-asset");
                Assert.That(
                    GridAssetWidget.SubAssetTooltipBuildsForTests - tooltipBuilds,
                    Is.Zero,
                    "an unchanged sub-asset key must reuse the tooltip string, not rebuild it");
            }
            finally
            {
                window.Close();
            }
        }

        // A catalog drain used to re-project and rebind the whole realized
        // grid: the Changed signal said only "something resolved". It now
        // names the resolved paths, so a drain that touches one tile rebinds
        // the one row that tile sits in.
        [UnityTest]
        public IEnumerator CatalogDrain_RebindsOnlyTheRowsHoldingTheNamedTiles()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 48);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                var realizedTiles = grid.RealizedTileCount;
                Assume.That(realizedTiles, Is.GreaterThan(8),
                    "several rows of tiles must be realized for a bind count to mean anything");

                SubAssetCatalog.ResetForTests();
                SubAssetCatalog.ResolveOverrideForTests =
                    _ => System.Array.Empty<SubAssetDescriptor>();
                try
                {
                    SubAssetCatalog.GetOrRequest("Assets/GridBindingItem01.asset");
                    GridAssetWidget.BindCountForTests = 0;
                    SubAssetCatalog.TickForTests();

                    var binds = GridAssetWidget.BindCountForTests;
                    Assert.That(binds, Is.GreaterThan(0),
                        "the row holding the resolved tile must be rebound");
                    Assert.That(binds, Is.LessThan(realizedTiles),
                        "a drain naming one tile must not rebind every realized tile");
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
        // discriminating proof: measured in this batchmode rig, a list hidden
        // for a frame binds nothing on a refresh anyway (its rows are no longer
        // realized), so the count is zero with or without the guard. What the
        // guard buys here is the skipped projection pass.
        [UnityTest]
        public IEnumerator CatalogDrain_WhileTheGridIsHidden_BindsNothing()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 48);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                Assume.That(grid.RealizedTileCount, Is.GreaterThan(8));
                // Exactly what a view-mode switch does to the widget it leaves.
                grid.style.display = DisplayStyle.None;
                yield return null;

                Assume.That(grid.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));

                SubAssetCatalog.ResetForTests();
                SubAssetCatalog.ResolveOverrideForTests =
                    _ => System.Array.Empty<SubAssetDescriptor>();
                try
                {
                    SubAssetCatalog.GetOrRequest("Assets/GridBindingItem01.asset");
                    GridAssetWidget.BindCountForTests = 0;
                    SubAssetCatalog.TickForTests();

                    Assert.That(GridAssetWidget.BindCountForTests, Is.Zero,
                        "a hidden widget must not bind a single tile for a catalog drain");
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

        private static int RealizedSubAssetTileCount(GridAssetWidget grid)
        {
            return grid
                .Query<VisualElement>(className: ZepUiClasses.AssetGridItem)
                .ToList()
                .Count(item => item.userData is GridAssetWidget.AssetGridItemParts parts
                    && parts.SubAsset != null);
        }

        // The grid mirrors the substrate's canonical selection set by
        // reference (audit §5.1): a push must not rebuild it, and the marquee
        // preview — the one local mutation — must copy it first.
        [Test]
        public void SelectionPush_IsNotRebuiltAndItsPreviewDoesNotLeakIntoTheSubstrate()
        {
            var window = CreateWindow();
            try
            {
                var view = MountGridView(
                    window,
                    new AssetsAreaInlineEditController(),
                    assetCount: 2,
                    mount: false);
                var assets = BuildAssets(2);
                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null, "the grid widget is the active view here");

                GridAssetWidget.SelectionSetBuildsForTests = 0;
                view.CommitClickSelection(new[] { assets[1].Path });
                view.SetSelection(new[] { AssetPath.From(assets[1].Path) });
                Assert.That(
                    GridAssetWidget.SelectionSetBuildsForTests,
                    Is.LessThanOrEqualTo(1),
                    "the commit and its pane echo pushed the same set twice");

                grid.ApplyMarqueePreview(new[] { assets[0].Path }, null);

                Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From(assets[1].Path) }));
                Assert.That(
                    view.ResolveClickSelection(assets[0].Path, toggle: true, range: false),
                    Is.EquivalentTo(new[] { assets[1].Path, assets[0].Path }),
                    "a leaked preview would make the substrate think the tile is already selected");
            }
            finally
            {
                window.Close();
            }
        }

        private static AssetCollectionView MountGridView(
            EditorWindow window,
            AssetsAreaInlineEditController controller,
            int assetCount,
            string firstAssetPath = null,
            bool mount = true)
        {
            var view = new AssetCollectionView(
                new UnityZepIconRepository(),
                controller,
                AssetViewMode.Grid,
                GridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize,
                presentationLookup: null);
            view.style.flexGrow = 1f;
            view.SetAssets(BuildAssets(assetCount, firstAssetPath));
            if (mount)
            {
                window.rootVisualElement.Add(view);
            }

            return view;
        }

        // The panel's own sheets, because these tests are about geometry the
        // stylesheet owns (tile padding and borders, the grid list's padding).
        // A bare window resolves every one of those to zero and would agree
        // with any answer at all.
        private static EditorWindow CreateWindow()
        {
            var window = ScriptableObject.CreateInstance<GridBindingTestWindow>();
            window.position = new Rect(90f, 90f, 900f, 520f);
            window.Show();

            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            foreach (var sheetPath in new[]
                     {
                         ZepUiClasses.PanelStyleSheetPath,
                         ZepUiClasses.DefaultsStyleSheetPath,
                     })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(sheetPath);
                Assert.That(sheet, Is.Not.Null, $"stylesheet missing: {sheetPath}");
                root.styleSheets.Add(sheet);
            }

            return window;
        }

        private sealed class GridBindingTestWindow : EditorWindow
        {
        }
    }
}
