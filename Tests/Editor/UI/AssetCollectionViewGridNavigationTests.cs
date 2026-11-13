using System.Collections;
using System.Linq;
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
    // Grid navigation and virtualization against Unity-style inline subassets,
    // plus the sub-asset keyboard cursor both view modes share (the multi-sub
    // fixture the cursor needs lives here).
    // Tests that depend on columns or the realized viewport use a real panel.
    public sealed class AssetCollectionViewGridNavigationTests
    {
        private const string FixturePath = "Assets/ZepGridNavFixture.asset";
        private const int FixtureIndex = 4;
        private const int SubAssetCount = 24;
        private const int AssetCount = 30;

        [SetUp]
        public void SetUp()
        {
            var main = ScriptableObject.CreateInstance<GridNavFixtureAsset>();
            AssetDatabase.CreateAsset(main, FixturePath);
            for (var i = 0; i < SubAssetCount; i++)
            {
                var sub = ScriptableObject.CreateInstance<GridNavFixtureAsset>();
                sub.name = "Sub" + i;
                AssetDatabase.AddObjectToAsset(sub, main);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(FixturePath, ImportAssetOptions.ForceUpdate);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(FixturePath);
        }

        [UnityTest]
        public IEnumerator MoveSelectionDown_OverAnOpenExpansion_LandsOnTheTileBelowOnScreen()
        {
            var view = CreateGridView();
            var window = ScriptableObject.CreateInstance<GridNavTestWindow>();
            window.position = new Rect(80f, 80f, 900f, 600f);
            window.Show();
            window.rootVisualElement.Add(view);
            try
            {
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid, Is.Not.Null);
                var columns = grid.ColumnsPerRow;
                Assume.That(FixtureIndex + columns, Is.LessThan(AssetCount));

                yield return ResolveFixtureSubAssets();
                Assume.That(
                    SubAssetCatalog.GetResolved(FixturePath).Length,
                    Is.EqualTo(SubAssetCount),
                    "the fixture must expose exactly the sub-assets this test's arithmetic assumes");

                grid.ExpandSubAssets(FixturePath);
                yield return null;

                Assert.That(grid.ExpandedAssetCount, Is.EqualTo(1));
                Assert.That(grid.VisibleItemCount, Is.EqualTo(AssetCount + SubAssetCount));
                Assert.That(
                    grid.DisplayRowCount,
                    Is.EqualTo((AssetCount + SubAssetCount + columns - 1) / columns),
                    "expanded children must join the same rightward, wrapping tile stream");
                Assert.That(
                    grid.AssetIndexToVisibleIndex(FixtureIndex + 1),
                    Is.EqualTo(FixtureIndex + SubAssetCount + 1),
                    "the next main asset must follow every inline child");

                Assume.That(columns, Is.LessThanOrEqualTo(SubAssetCount));

                view.CommitClickSelection(new[] { FixturePath });
                SubAssetDescriptor committed = null;
                view.SubAssetSelected += sub => committed = sub;
                var handled = view.MoveSelection(AssetSelectionMove.Down, extend: false);

                // One row down from the parent is a child cell, and that is
                // what the cursor lands on — children are on screen, so the
                // keyboard walks them instead of jumping past the expansion.
                Assert.That(handled, Is.True);
                Assert.That(
                    committed?.Key,
                    Is.EqualTo(SubAssetCatalog.GetResolved(FixturePath)[columns - 1].Key),
                    "vertical navigation must step onto the child cell one row below");
                Assert.That(
                    view.SelectedPaths,
                    Is.Empty,
                    "a child cell replaces the path selection — it has no path of its own");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator MoveSelectionUp_BackOverAnOpenExpansion_LandsOnTheChildTileAbove()
        {
            var view = CreateGridView();
            var window = ScriptableObject.CreateInstance<GridNavTestWindow>();
            window.position = new Rect(80f, 80f, 900f, 600f);
            window.Show();
            window.rootVisualElement.Add(view);
            try
            {
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                var columns = grid.ColumnsPerRow;
                Assume.That(FixtureIndex + columns, Is.LessThan(AssetCount));

                yield return ResolveFixtureSubAssets();
                grid.ExpandSubAssets(FixturePath);
                yield return null;

                Assume.That(columns, Is.LessThanOrEqualTo(SubAssetCount));

                var below = AssetPathAt(FixtureIndex + 1);
                view.CommitClickSelection(new[] { below });
                SubAssetDescriptor committed = null;
                view.SubAssetSelected += sub => committed = sub;
                var handled = view.MoveSelection(AssetSelectionMove.Up, extend: false);

                // The asset after the expansion sits one row below the tail of
                // the child stream, so stepping up re-enters that stream.
                Assert.That(handled, Is.True);
                Assert.That(
                    committed?.Key,
                    Is.EqualTo(SubAssetCatalog.GetResolved(FixturePath)[SubAssetCount - columns].Key));
                Assert.That(view.SelectedPaths, Is.Empty);
            }
            finally
            {
                window.Close();
            }
        }

        // The reported bug: with a child selected, the path-keyed selection is
        // empty, so navigation used to read "nothing selected" and jump to the
        // first/last MAIN asset instead of walking the siblings on screen.
        [UnityTest]
        public IEnumerator ArrowsFromASelectedChildTile_WalkItsSiblingsThenLeaveAtTheParent()
        {
            var view = CreateGridView();
            yield return ResolveFixtureSubAssets();

            var grid = view.Q<GridAssetWidget>();
            grid.ExpandSubAssets(FixturePath);
            var subAssets = SubAssetCatalog.GetResolved(FixturePath);

            SubAssetDescriptor committed = null;
            view.SubAssetSelected += sub => committed = sub;
            view.CommitSubAssetSelection(subAssets[0]);

            Assert.That(view.MoveSelection(AssetSelectionMove.Right, extend: false), Is.True);
            Assert.That(committed.Key, Is.EqualTo(subAssets[1].Key));
            Assert.That(view.SelectedPaths, Is.Empty);

            Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
            Assert.That(committed.Key, Is.EqualTo(subAssets[0].Key));

            // One more step left leaves the expansion at its parent tile, which
            // is a real asset again — so the path selection comes back.
            Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo(FixturePath));
        }

        // Same cursor, list mode: there the children are stacked rows, so it is
        // Up/Down that must walk them.
        [UnityTest]
        public IEnumerator ListMode_ArrowsFromASelectedSubAssetRow_WalkItsSiblings()
        {
            var view = CreateGridView();
            view.SetViewMode(AssetViewMode.List);
            yield return ResolveFixtureSubAssets();

            var list = view.Q<ListAssetWidget>();
            list.ExpandSubAssets(FixturePath);
            var subAssets = SubAssetCatalog.GetResolved(FixturePath);

            SubAssetDescriptor committed = null;
            view.SubAssetSelected += sub => committed = sub;
            view.CommitSubAssetSelection(subAssets[0]);

            Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
            Assert.That(committed.Key, Is.EqualTo(subAssets[1].Key));
            Assert.That(view.SelectedPaths, Is.Empty);

            Assert.That(view.MoveSelection(AssetSelectionMove.Up, extend: false), Is.True);
            Assert.That(committed.Key, Is.EqualTo(subAssets[0].Key));

            Assert.That(view.MoveSelection(AssetSelectionMove.Up, extend: false), Is.True);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo(FixturePath));
        }

        // List mode's horizontal arrows are the disclosure keys. The substrate
        // owns the sub-asset half of them: ← on a sub row steps out to the row
        // that owns it, and ← again on that row closes the disclosure. Folder
        // rows are still declined, so the pane view keeps its half.
        [UnityTest]
        public IEnumerator ListMode_LeftFromASubAssetRow_StepsOutToTheParentRow()
        {
            var view = CreateGridView();
            view.SetViewMode(AssetViewMode.List);
            yield return ResolveFixtureSubAssets();

            // Through the substrate: it owns the open set the disclosure keys
            // read and write (audit §6.1a).
            view.SetSubAssetExpanded(FixturePath, true);
            var subAssets = SubAssetCatalog.GetResolved(FixturePath);
            view.CommitSubAssetSelection(subAssets[SubAssetCount - 1]);

            // → has nothing to open on a sub row: it stays unconsumed so the
            // pane view's expansion handling still gets its turn.
            Assert.That(view.MoveSelection(AssetSelectionMove.Right, extend: false), Is.False);
            Assert.That(view.SelectedPaths, Is.Empty);

            // ← lands on the parent no matter how deep into the children the
            // cursor sits — it is a step OUT, not a step back.
            Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo(FixturePath));

            // Back on the owning row, ← closes the disclosure it just stepped
            // out of — the folder chevron's contract applied to the sub-asset
            // chevron beside it.
            var list = view.Q<ListAssetWidget>();
            Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
            Assert.That(list.IsSubAssetExpanded(FixturePath), Is.False);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo(FixturePath));

            // Closed, the substrate declines again, leaving ← to the pane
            // view's walk-to-parent-folder handling.
            Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.False);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo(FixturePath));
        }

        // The other half of the "keep the highlight across a republish" rule:
        // when the row it points at really is gone, it must still be dropped,
        // or the cursor would resolve against a cell that no longer exists.
        [UnityTest]
        public IEnumerator ListMode_PushingAListWithoutTheParent_DropsTheSubAssetCursor()
        {
            var view = CreateGridView();
            view.SetViewMode(AssetViewMode.List);
            yield return ResolveFixtureSubAssets();

            view.Q<ListAssetWidget>().ExpandSubAssets(FixturePath);
            view.CommitSubAssetSelection(SubAssetCatalog.GetResolved(FixturePath)[0]);
            Assume.That(view.SelectedPaths, Is.Empty);

            // Navigation: a list the highlighted sub-asset's parent is not in.
            view.SetAssets(new[]
            {
                new ZepAssetEntry("Elsewhere", "Assets/Elsewhere.asset", "ScriptableObject")
            });

            // With the cursor dropped, ↓ takes the "nothing selected" entry
            // branch and lands on the first row of the new list.
            Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo("Assets/Elsewhere.asset"));
        }

        // A shift-range is path-keyed, so it cannot stop on a child cell: it
        // takes the whole expansion's parent and continues from there.
        [UnityTest]
        public IEnumerator ListMode_ShiftDownOverAnOpenExpansion_RangesOverMainAssetsOnly()
        {
            var view = CreateGridView();
            view.SetViewMode(AssetViewMode.List);
            yield return ResolveFixtureSubAssets();

            view.Q<ListAssetWidget>().ExpandSubAssets(FixturePath);
            view.CommitClickSelection(new[] { FixturePath });

            Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: true), Is.True);
            Assert.That(
                view.SelectedPaths.Select(p => p.Value),
                Is.EqualTo(new[] { FixturePath, AssetPathAt(FixtureIndex + 1) }));
        }

        [Test]
        public void MoveSelectionDown_WithNoExpansionOpen_StillStepsOneRowOfAssets()
        {
            var view = CreateGridView();
            view.CommitClickSelection(new[] { AssetPathAt(0) });

            var handled = view.MoveSelection(AssetSelectionMove.Down, extend: false);

            // Unmounted, the grid keeps its pre-layout single column, so one
            // row is one asset. Pins that the visible-order step is identical
            // to the old base-order step whenever nothing is expanded.
            Assert.That(handled, Is.True);
            Assert.That(view.SelectedPaths.Single().Value, Is.EqualTo(AssetPathAt(1)));
        }

        [UnityTest]
        public IEnumerator OpenInlineExpansion_UsesUnityGroupingAndOnlyTheViewportPool()
        {
            var view = CreateGridView();
            var window = ScriptableObject.CreateInstance<GridNavTestWindow>();
            window.position = new Rect(80f, 80f, 900f, 340f);
            window.Show();
            // The only test here that measures painted geometry, so it is the
            // only one that mounts the real sheets: the group container's
            // segments are laid out by USS, and a bare window resolves them
            // to nothing.
            AttachPanelStyleSheets(window.rootVisualElement);
            window.rootVisualElement.Add(view);
            try
            {
                yield return null;
                yield return ResolveFixtureSubAssets();

                var grid = view.Q<GridAssetWidget>();
                grid.ExpandSubAssets(FixturePath);
                yield return null;
                yield return null;

                var realizedSubAssets = grid.Query<VisualElement>(
                    className: ZepUiClasses.AssetGridItemSubAsset).ToList();
                Assert.That(realizedSubAssets.Count, Is.GreaterThan(0));

                var parent = grid.Query<VisualElement>(
                        className: ZepUiClasses.AssetGridItem)
                    .ToList()
                    .Single(item => item.userData is GridAssetWidget.AssetGridItemParts itemParts
                        && string.Equals(itemParts.Entry?.Path, FixturePath));
                var parentParts = (GridAssetWidget.AssetGridItemParts)parent.userData;
                Assert.That(
                    parentParts.Disclosure.worldBound.xMax,
                    Is.EqualTo(parentParts.Card.worldBound.xMax - 3f).Within(0.5f),
                    "the chevron sits at the trailing edge toward the children");
                Assert.That(
                    parentParts.Disclosure.worldBound.center.y,
                    Is.EqualTo(parentParts.Icon.worldBound.center.y).Within(0.5f),
                    "the chevron is centered on the preview regardless of filename height");

                Assume.That(
                    FixtureIndex % grid.ColumnsPerRow,
                    Is.LessThan(grid.ColumnsPerRow - 1),
                    "the spacing assertion needs the first child beside its parent");
                var firstSubAssetKey = SubAssetCatalog.GetResolved(FixturePath)[0].Key;
                var firstChild = realizedSubAssets.Single(item =>
                    item.userData is GridAssetWidget.AssetGridItemParts itemParts
                    && string.Equals(itemParts.SubAsset?.Key, firstSubAssetKey));
                var firstChildParts =
                    (GridAssetWidget.AssetGridItemParts)firstChild.userData;
                // One container for the parent and its children: the parent
                // opens it under its own card, and its segment reaches the
                // first child's through the cell gap — no seam, and nothing
                // pulled back over the parent's selection.
                Assert.That(
                    parentParts.Root.ClassListContains(ZepUiClasses.AssetGridItemGrouped),
                    Is.True,
                    "the parent tile opens the group container");
                Assert.That(
                    parentParts.SubAssetGroupBackground.resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    parentParts.SubAssetGroupBackground.worldBound.xMax,
                    Is.GreaterThanOrEqualTo(
                        firstChildParts.SubAssetGroupBackground.worldBound.xMin - 0.5f),
                    "the parent's segment must reach the first child's segment");
                Assert.That(
                    parentParts.SubAssetGroupBackground.worldBound.xMin,
                    Is.LessThanOrEqualTo(parentParts.Card.worldBound.xMin + 0.5f),
                    "the container starts at or before the parent's card, never inside it");
                Assert.That(
                    firstChildParts.Icon.resolvedStyle.width,
                    Is.EqualTo(parentParts.Icon.resolvedStyle.width).Within(0.5f),
                    "Unity renders child thumbnails at the same scale as their parent");
                Assert.That(
                    firstChildParts.SubAssetGroupBackground.resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.Flex));
                Assert.That(
                    realizedSubAssets.Any(item => item.ClassListContains(
                        ZepUiClasses.AssetGridSubAssetContinuation)),
                    Is.True,
                    "a child strip that wraps must expose a continuation notch on the next row");
                Assert.That(
                    grid.RealizedTileCount,
                    Is.LessThan(grid.VisibleItemCount),
                    "inline expansion must reuse the outer virtualized tile pool");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator InspectorPing_ExpandsScrollsHighlightsAndPulsesInlineSubAsset()
        {
            var view = CreateGridView();
            var window = ScriptableObject.CreateInstance<GridNavTestWindow>();
            window.position = new Rect(80f, 80f, 900f, 600f);
            window.Show();
            window.rootVisualElement.Add(view);
            try
            {
                yield return null;
                yield return ResolveFixtureSubAssets();

                var descriptors = SubAssetCatalog.GetResolved(FixturePath);
                var target = descriptors[descriptors.Length - 1];
                view.PingSubAsset(FixturePath, target.LocalId);
                yield return null;
                yield return null;

                var grid = view.Q<GridAssetWidget>();
                Assert.That(grid.ExpandedAssetCount, Is.EqualTo(1));

                var realizedItems = grid.Query<VisualElement>(
                    className: ZepUiClasses.AssetGridItemSubAsset).ToList();
                var selected = realizedItems.SingleOrDefault(item =>
                    item.ClassListContains(ZepUiClasses.AssetGridItemSelected));
                Assert.That(selected, Is.Not.Null, "the pinged inline subasset must be scrolled into view");
                Assert.That(
                    selected.Q<Label>(
                        className: ZepUiClasses.AssetGridName).text,
                    Is.EqualTo(target.Name));
                Assert.That(
                    selected.Q<VisualElement>(
                        className: ZepUiClasses.AssetPingPulse),
                    Is.Not.Null,
                    "the centered subasset must receive the inspector ping bloom");
            }
            finally
            {
                window.Close();
            }
        }

        // The reported §6.1 defect: the open disclosures used to be widget
        // state, so flipping the view mode silently closed every one of them
        // while the sub-asset highlight survived. The set is the substrate's
        // now, and each widget flattens the SAME set into its own order.
        [UnityTest]
        public IEnumerator ExpandingInListMode_ThenSwitchingToGrid_KeepsTheExpansion()
        {
            var view = CreateGridView();
            view.SetViewMode(AssetViewMode.List);
            yield return ResolveFixtureSubAssets();

            view.SetSubAssetExpanded(FixturePath, true);
            var list = view.Q<ListAssetWidget>();
            Assume.That(
                list.VisibleItemCount,
                Is.EqualTo(AssetCount + SubAssetCount),
                "the list must be showing the expansion before the switch");

            view.SetViewMode(AssetViewMode.Grid);

            var grid = view.Q<GridAssetWidget>();
            Assert.That(
                grid.ExpandedAssetCount,
                Is.EqualTo(1),
                "the disclosure opened in list mode must still be open in grid mode");
            Assert.That(
                grid.VisibleItemCount,
                Is.EqualTo(AssetCount + SubAssetCount),
                "and its children must join the grid's own tile stream");

            var parentVisible = grid.AssetIndexToVisibleIndex(FixtureIndex);
            var parentIndex = grid.ResolveVisibleIndex(parentVisible + 1, out var subAsset);
            Assert.That(subAsset, Is.Not.Null, "the cell after the parent is its first child");
            Assert.That(
                subAsset.Key,
                Is.EqualTo(SubAssetCatalog.GetResolved(FixturePath)[0].Key));
            Assert.That(parentIndex, Is.EqualTo(FixtureIndex));
        }

        // One expansion, applied once: the substrate owns the set, so a ping
        // no longer has to reach into each widget separately — and the view
        // that is not on screen resolves the sub cell too.
        [UnityTest]
        public IEnumerator PingSubAsset_OnACollapsedParent_ExpandsOnceForBothViews()
        {
            var view = CreateGridView();
            view.SetViewMode(AssetViewMode.List);
            yield return ResolveFixtureSubAssets();

            var descriptors = SubAssetCatalog.GetResolved(FixturePath);
            Assume.That(view.IsSubAssetExpanded(FixturePath), Is.False);

            view.PingSubAsset(FixturePath, descriptors[0].LocalId);

            Assert.That(
                view.IsSubAssetExpanded(FixturePath),
                Is.True,
                "the ping opens the disclosure in the substrate's set");

            var list = view.Q<ListAssetWidget>();
            Assert.That(list.ResolveVisibleIndex(FixtureIndex + 1, out var listSub),
                Is.EqualTo(FixtureIndex));
            Assert.That(listSub?.Key, Is.EqualTo(descriptors[0].Key));

            var grid = view.Q<GridAssetWidget>();
            Assert.That(grid.ResolveVisibleIndex(FixtureIndex + 1, out var gridSub),
                Is.EqualTo(FixtureIndex));
            Assert.That(
                gridSub?.Key,
                Is.EqualTo(descriptors[0].Key),
                "the hidden view resolves the same cell — it flattens the same set");
        }

        [Test]
        public void MillionAssets_UsesCountOnlyRowsWithoutEagerEntryAccess()
        {
            var view = CreateGridView();
            var assets = new CountOnlyAssetList(1_000_000);

            view.SetAssets(assets);

            var grid = view.Q<GridAssetWidget>();
            Assert.That(grid.DisplayRowCount, Is.EqualTo(1_000_000));
            Assert.That(
                assets.IndexerAccessCount,
                Is.LessThan(100),
                "publishing the projection must not enumerate or materialize the asset set");
        }

        private static IEnumerator ResolveFixtureSubAssets()
        {
            SubAssetCatalog.GetOrRequest(FixturePath);
            for (var frame = 0; frame < 60 && SubAssetCatalog.GetResolved(FixturePath) == null; frame++)
            {
                yield return null;
            }

            Assert.That(
                SubAssetCatalog.GetResolved(FixturePath),
                Is.Not.Null,
                "the sub-asset catalog never resolved the fixture");
        }

        private static AssetCollectionView CreateGridView()
        {
            var view = new AssetCollectionView(
                new UnityZepIconRepository(),
                new AssetsAreaInlineEditController(),
                AssetViewMode.Grid,
                initialGridItemSize: 120,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize,
                presentationLookup: null);
            view.style.flexGrow = 1f;

            var assets = new ZepAssetEntry[AssetCount];
            for (var i = 0; i < AssetCount; i++)
            {
                var path = AssetPathAt(i);
                assets[i] = new ZepAssetEntry(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    path,
                    "ScriptableObject");
            }

            view.SetAssets(assets);
            return view;
        }

        private static void AttachPanelStyleSheets(VisualElement root)
        {
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
        }

        private static string AssetPathAt(int index) =>
            index == FixtureIndex ? FixturePath : $"Assets/GridNavItem{index:D2}.asset";

        private sealed class GridNavTestWindow : EditorWindow
        {
        }

        private sealed class CountOnlyAssetList : System.Collections.Generic.IReadOnlyList<ZepAssetEntry>
        {
            private readonly int _count;

            internal CountOnlyAssetList(int count)
            {
                _count = count;
            }

            internal int IndexerAccessCount { get; private set; }
            public int Count => _count;

            public ZepAssetEntry this[int index]
            {
                get
                {
                    IndexerAccessCount++;
                    return new ZepAssetEntry(
                        "Item" + index,
                        "Assets/Item" + index + ".asset",
                        "ScriptableObject");
                }
            }

            public System.Collections.Generic.IEnumerator<ZepAssetEntry> GetEnumerator()
            {
                for (var i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();
        }
    }

    internal sealed class GridNavFixtureAsset : ScriptableObject
    {
    }
}
