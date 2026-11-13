using System;
using System.Collections;
using System.Linq;
using Zep.Core.Domain;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.AssetsArea.Substrate;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // Rubber-band selection end to end: which rows a rectangle collects, what
    // the scroll offset does to that answer, what a cancelled drag hands back,
    // and where edge auto-scroll stops. All of it is geometry, so every test
    // mounts the substrate in a real window and reads real row bounds.
    public sealed class MarqueeInteractionTests
    {
        [UnityTest]
        public IEnumerator Drag_OverASetOfRows_SelectsExactlyTheSweptRowsInAssetOrder()
        {
            // Short list: the rows end well above the viewport bottom, so the
            // press lands on genuine empty space below them, as in Finder.
            using var rig = MarqueeRig.Build(assetCount: 6);
            yield return rig.Realize();

            var lastRowBottom = rig.RowBounds(5).yMax;
            rig.PressAt(new Vector2(rig.CenterX, lastRowBottom + 20f));
            rig.DragTo(rig.RowCenter(2));
            rig.ReleaseAt(rig.RowCenter(2));

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(2), rig.PathAt(3), rig.PathAt(4), rig.PathAt(5) }),
                "the rectangle must collect every row it covers, in the list's own order");
        }

        [UnityTest]
        public IEnumerator Drag_OnAScrolledList_CollectsTheRowsUnderTheRectangle()
        {
            using var rig = MarqueeRig.Build(assetCount: 40);
            yield return rig.Realize();

            // Deliberately not a whole multiple of the row height: an exact
            // row boundary would let a half-pixel of float error decide the
            // first index for us.
            yield return rig.ScrollTo(10 * rig.RowHeight + 5f);

            rig.PressAt(rig.RowCenter(10));
            rig.DragTo(rig.RowCenter(12));
            rig.ReleaseAt(rig.RowCenter(12));

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(10), rig.PathAt(11), rig.PathAt(12) }),
                "the collector's row math must follow the scroll offset, not the top of the list");
        }

        [UnityTest]
        public IEnumerator DragStartedAboveTheViewport_ClampsTheAnchorToTheFirstVisibleRow()
        {
            using var rig = MarqueeRig.Build(assetCount: 40);
            yield return rig.Realize();
            yield return rig.ScrollTo(10 * rig.RowHeight + 5f);

            // The press lands on the surface chrome above the scroll viewport
            // (the list's column header). Unclamped, that negative offset maps
            // onto the row scrolled out of view above the first visible one.
            rig.PressAt(new Vector2(rig.CenterX, rig.Viewport.yMin - 8f));
            rig.DragTo(rig.RowCenter(12));
            rig.ReleaseAt(rig.RowCenter(12));

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(10), rig.PathAt(11), rig.PathAt(12) }),
                "a press on chrome must start at the first visible row, never at one above it");
        }

        [UnityTest]
        public IEnumerator CancelMidDrag_HandsBackThePreDragSelection()
        {
            // Escape (and mouse-capture loss) both route here through the
            // feature's CancelInteractions.
            using var rig = MarqueeRig.Build(assetCount: 6);
            yield return rig.Realize();

            rig.Collection.CommitClickSelection(new[] { rig.PathAt(0) });

            var lastRowBottom = rig.RowBounds(5).yMax;
            rig.PressAt(new Vector2(rig.CenterX, lastRowBottom + 20f));
            rig.DragTo(rig.RowCenter(3));

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(3), rig.PathAt(4), rig.PathAt(5) }),
                "the drag must have replaced the pre-drag selection before it is cancelled");

            rig.Marquee.Cancel();

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(0) }),
                "a cancelled marquee must not be destructive — the pre-drag selection comes back");
        }

        [UnityTest]
        public IEnumerator AutoScroll_HeldBelowTheViewport_StopsAtTheBottomBoundWithTheAnchorIntact()
        {
            using var rig = MarqueeRig.Build(assetCount: 20);
            yield return rig.Realize();

            rig.PressAt(rig.RowCenter(1));
            var belowViewport = new Vector2(rig.CenterX, rig.Viewport.yMax + 30f);
            rig.DragTo(belowViewport);

            Assert.That(rig.MaxScrollOffset, Is.GreaterThan(0f),
                "the list has to be scrollable for this to mean anything");

            // The ticker is wall-clock driven (one tick per 16 ms) and a
            // batchmode frame is far shorter than that, so this waits for the
            // offset to stop moving rather than counting frames. The stable
            // stretch at the end is several ticks long: they run against the
            // bound and must neither push the offset past it nor drift the
            // anchor the rectangle is measured from.
            var lastOffset = rig.ScrollOffset;
            var stableFrames = 0;
            for (var frame = 0; frame < 6000 && stableFrames < 200; frame++)
            {
                yield return null;
                if (Mathf.Approximately(rig.ScrollOffset, lastOffset))
                {
                    stableFrames++;
                    continue;
                }

                lastOffset = rig.ScrollOffset;
                stableFrames = 0;
            }

            Assert.That(rig.ScrollOffset, Is.GreaterThan(0f), "the cursor was held past the edge — it had to scroll");
            // The scrollable range grows as the virtualized list realizes its
            // tail, so the bound is read here, once the offset has settled.
            Assert.That(rig.ScrollOffset, Is.EqualTo(rig.MaxScrollOffset).Within(0.01f),
                "auto-scroll must settle exactly on the scrollable bound");

            rig.ReleaseAt(belowViewport);

            // The start anchor follows the scrolled content, so the sweep keeps
            // collecting rows all the way to the end of the list and never runs
            // back above the row the press started on. One row of slack at the
            // top: a virtualized ListView changes its own content height (and
            // with it the scroll offset) while it realizes the tail, and those
            // adjustments do not go through the tick that shifts the anchor.
            var selected = rig.SelectedPaths;
            Assert.That(selected, Does.Not.Contain(rig.PathAt(0)));
            Assert.That(selected.Length, Is.InRange(18, 19));
            Assert.That(
                selected,
                Is.EqualTo(Enumerable
                    .Range(20 - selected.Length, selected.Length)
                    .Select(rig.PathAt)
                    .ToArray()),
                "the sweep must end on a contiguous run of rows down to the last one");
        }

        // A long sweep costs what it selects, not what it selects times the
        // number of pointer moves it took to get there. Before the delta
        // rework this drag re-collected, re-hashed and re-pushed the whole
        // swept set on each of the 100 moves — about two million insertions
        // for forty thousand selected rows.
        [UnityTest]
        public IEnumerator LongDrag_CostsOneInsertionPerSweptRow_AndCommitsThemAll()
        {
            const int assetCount = 40000;
            const int moveCount = 100;
            using var rig = MarqueeRig.Build(assetCount);
            yield return rig.Realize();

            var rowHeight = rig.RowHeight;
            Assume.That(rowHeight, Is.GreaterThan(0f));

            // Press on row 0 and walk the bottom edge down 400 rows per move.
            // The rectangle is deliberately unclipped, so the cursor may sit
            // far below the window: the collector is index math.
            var top = rig.RowBounds(0).center;
            rig.PressAt(top);
            for (var move = 1; move <= moveCount; move++)
            {
                var sweptRows = move * (assetCount / moveCount);
                rig.DragTo(new Vector2(rig.CenterX, top.y + sweptRows * rowHeight - rowHeight * 0.5f));
            }

            TestContext.WriteLine(
                $"previewSetInsertions={rig.Marquee.PreviewSetInsertions} for {assetCount} swept rows");
            Assert.That(
                rig.Marquee.PreviewSetInsertions,
                Is.LessThanOrEqualTo(assetCount),
                "a monotonic sweep must insert each row into the working set at most once");
            Assert.That(
                rig.Marquee.PreviewSetInsertions,
                Is.GreaterThan(assetCount / 2),
                "the sweep really did have to reach most of the list");

            var lastCursor = new Vector2(
                rig.CenterX,
                top.y + assetCount * rowHeight - rowHeight * 0.5f);
            rig.ReleaseAt(lastCursor);

            var selected = rig.SelectedPaths;
            Assert.That(selected.Length, Is.EqualTo(assetCount));
            Assert.That(selected[0], Is.EqualTo(rig.PathAt(0)));
            Assert.That(selected[assetCount - 1], Is.EqualTo(rig.PathAt(assetCount - 1)));
        }

        // A drag that grows and then shrinks has to give the rows back. The
        // delta path removes them one span at a time, so this is the test that
        // fails if a removed span is computed against the wrong side.
        [UnityTest]
        public IEnumerator DragThatShrinksBack_DropsTheRowsItLeft()
        {
            using var rig = MarqueeRig.Build(assetCount: 12);
            yield return rig.Realize();

            rig.PressAt(rig.RowCenter(0));
            rig.DragTo(rig.RowCenter(6));
            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(Enumerable.Range(0, 7).Select(rig.PathAt).ToArray()),
                "the grown rectangle must preview every row it covers");

            rig.DragTo(rig.RowCenter(2));
            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(Enumerable.Range(0, 3).Select(rig.PathAt).ToArray()),
                "the rows the rectangle left must leave the preview too");

            rig.ReleaseAt(rig.RowCenter(2));
            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(Enumerable.Range(0, 3).Select(rig.PathAt).ToArray()));
        }

        // The grid's ListView carries 4px of USS padding, so its own top edge is
        // NOT where row 0 starts. Row math measured from it put every boundary
        // band one row low — a drag whose top edge grazes the row above lost
        // that row's tiles. This drags from 2px inside row 0's band down into
        // row 1, which only collects both rows if the padding is accounted for.
        [UnityTest]
        public IEnumerator GridDrag_StartingInsideTheListPadding_CollectsTheRowItGrazes()
        {
            using var rig = MarqueeRig.Build(assetCount: 24, AssetViewMode.Grid);
            yield return rig.Realize();

            var columns = rig.Grid.ColumnsPerRow;
            Assume.That(columns, Is.GreaterThan(1));
            Assume.That(columns * 2, Is.LessThanOrEqualTo(24));

            var secondRowTop = rig.GridRowBounds(1).yMin;
            var columnX = rig.FirstTileBounds().center.x;
            rig.PressAt(new Vector2(columnX, secondRowTop - 2f));
            var inSecondRow = new Vector2(columnX, secondRowTop + rig.GridRowHeight * 0.5f);
            rig.DragTo(inSecondRow);
            rig.ReleaseAt(inSecondRow);

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(0), rig.PathAt(columns) }),
                "a band that starts inside the row above must collect that row's tile too");
        }

        // §6.10(b): both asset surfaces route their mouse gestures through one
        // SurfaceGestureRouter, and the invariant it owns is that the marquee
        // gets first refusal on a press. When it takes one it consumes the
        // event, so drag-prepare's own capture-phase handler on the collection
        // — which sits BELOW the surface in the propagation path — never sees
        // the press and cannot turn the gesture into a click-or-drag. These
        // drive the real feature, not a hand-wired marquee, so the assertion is
        // on the routing the feature actually registers.
        [UnityTest]
        public IEnumerator EmptySpacePress_OnTheBrowserSurface_IsTakenByTheMarqueeBeforeDragPrepare()
        {
            using var rig = FeatureSurfaceRig.MountBrowser(assetCount: 6);
            yield return rig.Realize();

            AssertTheMarqueeOwnsTheEmptySpaceGesture(rig);
        }

        [UnityTest]
        public IEnumerator EmptySpacePress_OnTheSmartFolderSurface_IsTakenByTheMarqueeBeforeDragPrepare()
        {
            using var rig = FeatureSurfaceRig.MountSmartFolder(assetCount: 6);
            yield return rig.Realize();

            AssertTheMarqueeOwnsTheEmptySpaceGesture(rig);
        }

        // The context gesture cancels drag-prepare only. Drag-prepare owns a
        // folder press's capture of this same surface, never the rubber band's.
        [UnityTest]
        public IEnumerator RightClickDuringARubberBand_KeepsTheMarqueeGoing()
        {
            using var rig = FeatureSurfaceRig.MountBrowser(assetCount: 6);
            yield return rig.Realize();

            rig.PressAt(new Vector2(rig.CenterX, rig.RowBounds(5).yMax + 20f));
            rig.DragTo(rig.RowCenter(3));
            Assert.That(rig.Surface.HasMouseCapture(), Is.True,
                "fixture: the rubber band holds the surface capture");

            rig.RightPressAt(rig.RowCenter(3));
            Assert.That(rig.Surface.HasMouseCapture(), Is.True,
                "a right-click must not release the rubber band's capture");

            rig.DragTo(rig.RowCenter(2));
            rig.ReleaseAt(rig.RowCenter(2));
            rig.ReleasePointerButton(rig.RowCenter(2), button: 1);
            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(2), rig.PathAt(3), rig.PathAt(4), rig.PathAt(5) }));
        }

        private static void AssertTheMarqueeOwnsTheEmptySpaceGesture(FeatureSurfaceRig rig)
        {
            // Registered on the collection in the same phase drag-prepare uses,
            // so it runs exactly when drag-prepare's own mouse-down would.
            var collectionSawThePress = false;
            rig.Collection.RegisterCallback<MouseDownEvent>(
                _ => collectionSawThePress = true,
                TrickleDown.TrickleDown);

            // Empty space BELOW the rows but still inside the collection, which
            // is the press both FSMs can see.
            rig.PressAt(new Vector2(rig.CenterX, rig.RowBounds(5).yMax + 20f));

            Assert.That(
                collectionSawThePress,
                Is.False,
                "the marquee must consume an empty-space press before it reaches drag-prepare");

            rig.DragTo(rig.RowCenter(2));
            rig.ReleaseAt(rig.RowCenter(2));

            Assert.That(
                rig.SelectedPaths,
                Is.EqualTo(new[] { rig.PathAt(2), rig.PathAt(3), rig.PathAt(4), rig.PathAt(5) }),
                "the gesture must resolve as a rubber band over the swept rows");
        }

        // Mounts the substrate the way AssetsBrowserFeature does — a surface
        // element that hosts both the collection view and the marquee overlay —
        // and drives the marquee with the events the feature forwards to it.
        private sealed class MarqueeRig : IDisposable
        {
            private readonly int _assetCount;
            private EditorWindow _window;

            private MarqueeRig(int assetCount)
            {
                _assetCount = assetCount;
            }

            internal VisualElement Surface { get; private set; }
            internal AssetCollectionView Collection { get; private set; }
            internal MarqueeInteraction Marquee { get; private set; }

            internal static MarqueeRig Build(
                int assetCount,
                AssetViewMode viewMode = AssetViewMode.List)
            {
                var rig = new MarqueeRig(assetCount);
                rig.Collection = new AssetCollectionView(
                    new UnityZepIconRepository(),
                    new AssetsAreaInlineEditController(),
                    viewMode,
                    AssetsAreaConstants.DefaultAssetGridItemSize,
                    AssetsAreaConstants.MinAssetGridItemSize,
                    AssetsAreaConstants.MaxAssetGridItemSize,
                    presentationLookup: null);
                rig.Collection.style.flexGrow = 1f;

                var assets = new ZepAssetEntry[assetCount];
                for (var i = 0; i < assetCount; i++)
                {
                    var path = rig.PathAt(i);
                    assets[i] = new ZepAssetEntry(
                        System.IO.Path.GetFileNameWithoutExtension(path),
                        path,
                        "ScriptableObject");
                }

                rig.Collection.SetAssets(assets);

                rig.Surface = new VisualElement { name = "zep-marquee-test-surface" };
                rig.Surface.style.flexGrow = 1f;
                rig.Surface.Add(rig.Collection);

                rig._window = ScriptableObject.CreateInstance<MarqueeTestWindow>();
                rig._window.position = new Rect(80f, 80f, 420f, 320f);
                rig._window.Show();
                // With the real sheets: the grid list's own 4px padding is part
                // of the geometry these tests measure, and a bare window would
                // hide it.
                var root = rig._window.rootVisualElement;
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

                root.Add(rig.Surface);
                rig.Marquee = new MarqueeInteraction(rig.Surface, rig.Collection);
                return rig;
            }

            internal IEnumerator Realize()
            {
                // Two passes: the ListView needs one to size its viewport and a
                // second to realize the rows that fill it.
                yield return null;
                yield return null;
            }

            // Names run backwards against the row order so an assertion on the
            // collected paths cannot be satisfied by a sorted result.
            internal string PathAt(int index) => $"Assets/Item{_assetCount - 1 - index:D2}.asset";

            internal ListView List => Collection.Q<ListView>("zep-asset-list");

            internal GridAssetWidget Grid => Collection.Q<GridAssetWidget>();

            internal ListView GridList => Collection.Q<ListView>("zep-asset-grid-list");

            internal float GridRowHeight => GridList.fixedItemHeight;

            internal Rect GridRowBounds(int rowIndex)
            {
                var row = GridList.GetRootElementForIndex(rowIndex);
                Assert.That(row, Is.Not.Null, $"grid row {rowIndex} must be realized to be measured");
                return row.worldBound;
            }

            internal Rect FirstTileBounds()
            {
                var tile = GridList.GetRootElementForIndex(0)
                    ?.Q<VisualElement>(className: ZepUiClasses.AssetGridItem);
                Assert.That(tile, Is.Not.Null, "the first row must have realized its tiles");
                return tile.worldBound;
            }

            internal ScrollView Scroll => Collection.ResolveActiveScrollView();

            internal Rect Viewport => Scroll.worldBound;

            internal float RowHeight => List.fixedItemHeight;

            internal float ScrollOffset => Scroll.scrollOffset.y;

            internal float MaxScrollOffset => Mathf.Max(0f, Scroll.verticalScroller.highValue);

            internal float CenterX => Viewport.center.x;

            internal string[] SelectedPaths =>
                Collection.SelectedPaths.Select(path => path.Value).ToArray();

            internal Rect RowBounds(int index)
            {
                var row = List.GetRootElementForIndex(index);
                Assert.That(row, Is.Not.Null, $"row {index} must be realized to be measured");
                return row.worldBound;
            }

            internal Vector2 RowCenter(int index) => RowBounds(index).center;

            internal IEnumerator ScrollTo(float offsetY)
            {
                Scroll.scrollOffset = new Vector2(Scroll.scrollOffset.x, offsetY);
                yield return null;
            }

            // The editor presses the mouse button in its global pointer state on
            // PointerDown, and that is where MouseMoveEvent reads pressedButtons
            // from — which is the marquee's own "button still held" guard. So a
            // gesture starts with a real dispatched PointerDownEvent, in the same
            // order the editor delivers one (pointer first, mouse compatibility
            // events after).
            internal void PressAt(Vector2 position)
            {
                using (var pointerDown = PointerDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown,
                    button = 0,
                    clickCount = 1,
                    mousePosition = position
                }))
                {
                    pointerDown.target = Surface;
                    Surface.SendEvent(pointerDown);
                }

                using var mouseDown = MouseDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown,
                    button = 0,
                    clickCount = 1,
                    mousePosition = position
                });
                mouseDown.target = Surface;
                Assert.That(Marquee.TryBegin(mouseDown), Is.True, "the press must start a marquee");
            }

            internal void DragTo(Vector2 position)
            {
                using var move = MouseMoveEvent.GetPooled(new Event
                {
                    type = EventType.MouseDrag,
                    button = 0,
                    mousePosition = position
                });
                move.target = Surface;
                Assert.That(move.pressedButtons & 1, Is.Not.Zero, "the drag must run with the button held");
                Marquee.Update(move);
            }

            internal void ReleaseAt(Vector2 position)
            {
                using (var mouseUp = MouseUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp,
                    button = 0,
                    mousePosition = position
                }))
                {
                    mouseUp.target = Surface;
                    Marquee.End(mouseUp);
                }

                ReleasePointerButton(position);
            }

            // The pressed-button state is global to the editor, so every rig
            // releases it whether or not the gesture reached a mouse-up.
            private void ReleasePointerButton(Vector2 position)
            {
                using var pointerUp = PointerUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp,
                    button = 0,
                    clickCount = 1,
                    mousePosition = position
                });
                pointerUp.target = Surface;
                Surface.SendEvent(pointerUp);
            }

            public void Dispose()
            {
                Marquee?.Cancel();
                ReleasePointerButton(Vector2.zero);
                if (_window != null)
                {
                    _window.Close();
                }
            }
        }

        // Mounts a REAL feature (browser or smart folder) and drives its own
        // registered surface handlers, so what is under test is the routing the
        // feature wires — not a marquee called directly.
        private sealed class FeatureSurfaceRig : IDisposable
        {
            private int _assetCount;
            private EditorWindow _window;

            internal VisualElement Surface { get; private set; }
            internal AssetCollectionView Collection { get; private set; }

            internal static FeatureSurfaceRig MountBrowser(int assetCount)
            {
                var rig = new FeatureSurfaceRig();
                var feature = new AssetsBrowserFeature(
                    new UnityZepIconRepository(),
                    AssetViewMode.List,
                    AssetsAreaConstants.DefaultAssetGridItemSize);
                rig._assetCount = assetCount;
                feature.SetAssets(BuildAssets(assetCount));
                rig.Mount(feature);
                return rig;
            }

            internal static FeatureSurfaceRig MountSmartFolder(int assetCount)
            {
                var rig = new FeatureSurfaceRig();
                var feature = new SmartFolderFeature(
                    new UnityZepIconRepository(),
                    AssetViewMode.List,
                    AssetsAreaConstants.DefaultAssetGridItemSize,
                    new SmartFolderFeature.Options
                    {
                        ElementName = "zep-routing-test-feature",
                        EmptyMessage = "nothing here",
                    });
                rig._assetCount = assetCount;
                feature.SetAssets(BuildAssets(assetCount));
                rig.Mount(feature);
                return rig;
            }

            private void Mount(VisualElement feature)
            {
                feature.style.flexGrow = 1f;

                _window = ScriptableObject.CreateInstance<MarqueeTestWindow>();
                _window.position = new Rect(80f, 80f, 480f, 360f);
                _window.Show();

                var root = _window.rootVisualElement;
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

                root.Add(feature);

                Surface = feature.Q<VisualElement>(className: ZepUiClasses.AssetSurface);
                Assert.That(Surface, Is.Not.Null, "the feature must expose an asset surface");
                Collection = feature.Q<AssetCollectionView>();
                Assert.That(Collection, Is.Not.Null, "the feature must mount the substrate");
            }

            private static ZepAssetEntry[] BuildAssets(int assetCount)
            {
                var assets = new ZepAssetEntry[assetCount];
                for (var i = 0; i < assetCount; i++)
                {
                    var path = PathFor(assetCount, i);
                    assets[i] = new ZepAssetEntry(
                        System.IO.Path.GetFileNameWithoutExtension(path),
                        path,
                        "ScriptableObject");
                }

                return assets;
            }

            internal IEnumerator Realize()
            {
                yield return null;
                yield return null;
            }

            // Same backwards naming as MarqueeRig: a sorted result cannot pass.
            private static string PathFor(int assetCount, int index)
                => $"Assets/Item{assetCount - 1 - index:D2}.asset";

            internal string PathAt(int index) => PathFor(_assetCount, index);

            private ListView List => Collection.Q<ListView>("zep-asset-list");

            private ScrollView Scroll => Collection.ResolveActiveScrollView();

            internal float CenterX => Scroll.worldBound.center.x;

            internal string[] SelectedPaths =>
                Collection.SelectedPaths.Select(path => path.Value).ToArray();

            internal Rect RowBounds(int index)
            {
                var row = List.GetRootElementForIndex(index);
                Assert.That(row, Is.Not.Null, $"row {index} must be realized to be measured");
                return row.worldBound;
            }

            internal Vector2 RowCenter(int index) => RowBounds(index).center;

            // The press lands on the scroll content — inside the collection, so
            // drag-prepare's handler IS on the propagation path, and empty
            // space, so the marquee is entitled to take it.
            //
            // Only the PointerDownEvent is dispatched: it presses the button in
            // the editor's global pointer state (which MouseMoveEvent reads for
            // "still held") AND the panel derives the compatibility
            // MouseDownEvent from it — that derived event is the one the
            // surface router sees. A second hand-built MouseDownEvent here
            // would arrive as a SECOND press on an already-tracking marquee.
            internal void PressAt(Vector2 position)
            {
                using var pointerDown = PointerDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown,
                    button = 0,
                    clickCount = 1,
                    mousePosition = position
                });
                pointerDown.target = Scroll.contentContainer;
                Surface.SendEvent(pointerDown);
            }

            internal void DragTo(Vector2 position)
            {
                using var move = MouseMoveEvent.GetPooled(new Event
                {
                    type = EventType.MouseDrag,
                    button = 0,
                    mousePosition = position
                });
                move.target = Surface;
                Assert.That(move.pressedButtons & 1, Is.Not.Zero, "the drag must run with the button held");
                Surface.SendEvent(move);
            }

            internal void ReleaseAt(Vector2 position)
            {
                using (var mouseUp = MouseUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp,
                    button = 0,
                    mousePosition = position
                }))
                {
                    mouseUp.target = Surface;
                    Surface.SendEvent(mouseUp);
                }

                ReleasePointerButton(position);
            }

            // Press only: releasing a button ends a rubber band, so callers
            // release the right button themselves once the gesture is over.
            internal void RightPressAt(Vector2 position)
            {
                using var pointerDown = PointerDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown,
                    button = 1,
                    clickCount = 1,
                    mousePosition = position
                });
                pointerDown.target = Scroll.contentContainer;
                Surface.SendEvent(pointerDown);
            }

            internal void ReleasePointerButton(Vector2 position, int button = 0)
            {
                using var pointerUp = PointerUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp,
                    button = button,
                    clickCount = 1,
                    mousePosition = position
                });
                pointerUp.target = Surface;
                Surface.SendEvent(pointerUp);
            }

            public void Dispose()
            {
                ReleasePointerButton(Vector2.zero);
                if (_window != null)
                {
                    _window.Close();
                }
            }
        }

        private sealed class MarqueeTestWindow : EditorWindow
        {
        }
    }
}
