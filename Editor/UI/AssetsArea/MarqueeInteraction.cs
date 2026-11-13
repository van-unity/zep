using System;
using Zep.Core.State;
using System.Collections.Generic;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Self-contained rubber-band (marquee) selection controller. Owns:
    //   - tracking/visible state machine,
    //   - the overlay VisualElement (rooted at an externally supplied host),
    //   - mouse capture on the host element (single, predictable capture target),
    //   - a reusable path accumulator with HashSet-backed dedup for the
    //     additive merge.
    //
    // Drives the substrate (AssetCollectionView) directly: the substrate owns
    // canonical selection and dispatches hit-testing to whichever view mode is
    // active, so no per-view wiring is needed here.
    //
    // Allocation-free on the hot path: all collections are pre-allocated and
    // reused across marquee lifetimes.
    internal sealed class MarqueeInteraction
    {
        // Pre-drag pixel threshold before the overlay becomes visible and the
        // "discard existing selection" side-effect fires (matches pre-refactor UX).
        private const float DragThresholdPx = 4f;

        // --- Auto-scroll tuning ----------------------------------------------------
        // Distance from a viewport edge (in panel px) where auto-scroll engages and
        // ramps from 0 → max as the cursor approaches the edge. Beyond the edge the
        // ramp clamps to max. Matches Finder feel — close enough that you don't have
        // to leave the window, far enough that it doesn't fire on incidental moves.
        private const float AutoScrollEdgeZonePx       = 40f;
        // Per-tick scroll delta in panel px at maximum speed. 16 px * 60 ticks/s
        // = ~960 px/s, comparable to Finder's marquee auto-scroll.
        private const float AutoScrollMaxSpeedPxPerTick = 16f;
        private const long  AutoScrollTickIntervalMs    = 16;

        private readonly VisualElement _overlayHost;
        private readonly VisualElement _overlay;
        private readonly AssetCollectionView _collection;

        // Recurring scheduled item that drives edge-zone auto-scroll while the
        // marquee is visible. Created once and Pause/Resumed across drags.
        private readonly IVisualElementScheduledItem _autoScrollTicker;

        // --- Hot-path state (reused across the lifetime) ---------------------------

        private bool _isTracking;
        private bool _isVisible;
        private bool _isAdditive;
        // Two start anchors, both shifted by AutoScrollTick's content anchoring:
        //   _startPanelPos    — the raw press point; anchors the OVERLAY so the
        //                       rectangle visibly grows from where the user
        //                       actually clicked, even on chrome (list header).
        //   _hitStartPanelPos — the press point clamped into the scroll viewport
        //                       at Begin; anchors the HIT rect so the collectors'
        //                       world→content row math never maps a chrome-area
        //                       edge onto rows scrolled out of view. They differ
        //                       only when the press landed outside the viewport.
        private Vector2 _startPanelPos;
        private Vector2 _hitStartPanelPos;
        // Latest cursor position in panel coords. Updated on MouseMove and read
        // by the auto-scroll ticker when the cursor isn't moving (the common case
        // when a user holds at the edge waiting for scroll).
        private Vector2 _lastCursorPanelPos;

        // Pre-drag selection captured on Begin; O(N) ref copy once, then
        // read-only. Additive drags merge it into the working set; a mid-drag
        // Cancel hands it back to the substrate regardless of additivity.
        private readonly List<string> _baseSelection = new List<string>();
        // Membership side of the same snapshot: an additive drag must not drop
        // a pre-selected row when the rectangle moves off it.
        private readonly HashSet<string> _baseSelectionSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The working set, maintained INCREMENTALLY across moves. The ordered
        // list is built once, at commit: order (base first, then hits in
        // visible order) is what the substrate's pivot/lead anchors read, and
        // rebuilding it per move is what made a large sweep quadratic.
        private readonly List<string> _currentPaths = new List<string>();
        private readonly HashSet<string> _currentPathsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The rectangle's hit cells as of the previous move; the difference
        // against the current one is the only thing that needs resolving.
        private MarqueeHitRegion _hitRegion = MarqueeHitRegion.Empty;
        private readonly List<int> _scratchCells = new List<int>();
        private readonly List<string> _addedPaths = new List<string>();
        private readonly List<string> _removedPaths = new List<string>();

        // Buffer the commit pass appends to — kept separate so we can merge
        // with the additive base deterministically and dedup in one pass.
        private readonly List<string> _scratchHitPaths = new List<string>();

        // Test hook: insertions into the working set over one drag. A drag that
        // sweeps N rows must cost about N, not N per move.
        private int _previewSetInsertions;

        internal MarqueeInteraction(VisualElement overlayHost, AssetCollectionView collection)
        {
            _overlayHost = overlayHost ?? throw new ArgumentNullException(nameof(overlayHost));
            _collection  = collection  ?? throw new ArgumentNullException(nameof(collection));

            _overlay = new VisualElement
            {
                name = "zep-marquee-overlay",
                pickingMode = PickingMode.Ignore,
            };
            _overlay.AddToClassList(ZepUiClasses.MarqueeOverlay);
            _overlay.style.display = DisplayStyle.None;
            _overlayHost.Add(_overlay);

            _autoScrollTicker = _overlayHost.schedule
                .Execute(AutoScrollTick)
                .Every(AutoScrollTickIntervalMs);
            _autoScrollTicker.Pause();
        }

        internal bool IsTracking => _isTracking;

        internal int PreviewSetInsertions => _previewSetInsertions;

        // Attempt to start tracking.  Returns true iff the event should be consumed
        // by the caller (StopImmediatePropagation to prevent drag-prepare).
        internal bool TryBegin(MouseDownEvent evt)
        {
            if (_isTracking) return false;
            if (ZepContextMenuInput.IsMouseTrigger(evt)) return false;
            if (evt == null || evt.button != 0 || evt.clickCount != 1) return false;
            if (!_collection.IsEmptySpaceTarget(evt.target as VisualElement)) return false;

            _isTracking         = true;
            _isVisible          = false;
            _isAdditive         = evt.actionKey;
            _startPanelPos      = evt.mousePosition;
            _hitStartPanelPos   = evt.mousePosition;
            _lastCursorPanelPos = evt.mousePosition;

            // The begin point can land on surface chrome outside the scroll
            // viewport (the list-mode column header, or surface padding). Clamp
            // only the HIT anchor into the viewport — the overlay keeps the raw
            // press point. After this, out-of-viewport travel of the hit anchor
            // comes exclusively from AutoScrollTick's content anchoring —
            // exactly the case that must keep hitting swept rows.
            var scrollView = _collection.ResolveActiveScrollView();
            if (scrollView != null)
            {
                var viewport = scrollView.worldBound;
                if (viewport.height > 0f)
                {
                    _hitStartPanelPos.y = Mathf.Clamp(_hitStartPanelPos.y, viewport.yMin, viewport.yMax);
                }
            }

            // Captured unconditionally (not just for additive drags): Cancel
            // must be able to hand the pre-drag selection back after the
            // threshold-crossing clear has already discarded it.
            _baseSelection.Clear();
            _baseSelectionSet.Clear();
            var currentSelection = _collection.SelectedPaths;
            for (var i = 0; i < currentSelection.Count; i++)
            {
                if (!currentSelection[i].IsEmpty)
                {
                    _baseSelection.Add(currentSelection[i].Value);
                    _baseSelectionSet.Add(currentSelection[i].Value);
                }
            }

            _currentPaths.Clear();
            _currentPathsSet.Clear();
            _hitRegion = MarqueeHitRegion.Empty;
            _scratchCells.Clear();
            _addedPaths.Clear();
            _removedPaths.Clear();
            _scratchHitPaths.Clear();
            _previewSetInsertions = 0;

            _overlayHost.CaptureMouse();
            return true;
        }

        internal bool Update(MouseMoveEvent evt)
        {
            if (!_isTracking || evt == null) return false;

            if ((evt.pressedButtons & 1) == 0)
            {
                Cancel();
                return true;
            }

            _lastCursorPanelPos = evt.mousePosition;

            if (!_isVisible)
            {
                var delta = evt.mousePosition - _startPanelPos;
                if (delta.sqrMagnitude < DragThresholdPx * DragThresholdPx)
                {
                    return true;
                }

                _isVisible = true;
                _overlay.style.display = DisplayStyle.Flex;
                _autoScrollTicker.Resume();
                if (_isAdditive)
                {
                    // Seed the working set once. The substrate and the widget
                    // still hold this selection (an additive drag never
                    // clears), so nothing has to be pushed for it.
                    for (var i = 0; i < _baseSelection.Count; i++)
                    {
                        if (_currentPathsSet.Add(_baseSelection[i]))
                        {
                            _previewSetInsertions++;
                        }
                    }
                }
                else
                {
                    _collection.ClearSelection();
                }
            }

            // Document-order alone isn't sufficient on Unity 6: ListAssetWidget's
            // GeometryChangedEvent → _listView.MarkDirtyRepaint() handler fires
            // whenever the marquee resizes (the overlay's style changes bubble as
            // geometry events to siblings). Unity batches that repaint after the
            // current frame, so the ListView's row content paints *over* the marquee
            // even though the marquee is its parent's last child. Re-asserting the
            // front on every move keeps the overlay above the list's repaint batch.
            // BringToFront on an already-last element is a fast no-op for the grid
            // path that doesn't trigger the same repaint cascade.
            _overlay.BringToFront();

            UpdateOverlayRect(evt.mousePosition);
            RefreshHitPathsAndPreview(evt.mousePosition);

            return true;
        }

        internal bool End(MouseUpEvent evt)
        {
            if (!_isTracking) return false;
            if (evt != null && evt.button != 0) return false;

            var wasVisible  = _isVisible;
            var wasAdditive = _isAdditive;

            if (wasVisible)
            {
                BuildCommittedPaths();
                _collection.CommitMarqueeSelection(_currentPaths);
            }
            else if (!wasAdditive)
            {
                _collection.ClearSelection();
            }

            Reset();
            return wasVisible;
        }

        internal void Cancel()
        {
            if (!_isTracking) return;

            // Flip before raising anything: CommitMarqueeSelection fires
            // SelectionChanged, whose handlers can re-enter here via
            // capture-out. Reset() re-clears the flag; that's harmless.
            _isTracking = false;

            if (_isVisible)
            {
                // The preview already replaced the substrate's selection (and a
                // non-additive drag cleared it, with a SelectionChanged
                // broadcast, when the overlay appeared) — hand the pre-drag
                // selection back so a mid-drag capture loss isn't destructive.
                _collection.CommitMarqueeSelection(_baseSelection);
            }

            Reset();
        }

        private void Reset()
        {
            _isTracking = false;
            _isVisible  = false;
            _overlay.style.display = DisplayStyle.None;
            _autoScrollTicker.Pause();

            _baseSelection.Clear();
            _baseSelectionSet.Clear();
            _currentPaths.Clear();
            _currentPathsSet.Clear();
            _hitRegion = MarqueeHitRegion.Empty;
            _scratchCells.Clear();
            _addedPaths.Clear();
            _removedPaths.Clear();
            _scratchHitPaths.Clear();

            if (_overlayHost.HasMouseCapture())
            {
                _overlayHost.ReleaseMouse();
            }
        }

        // Edge-zone auto-scroll. Runs while the marquee is visible. Reads the
        // last known cursor panel position (updated on MouseMove + on Begin),
        // measures distance to the active ScrollView's viewport edges, and
        // scrolls proportionally. After applying the actual scroll delta we
        // shift _startPanelPos by the *same* delta so the marquee start anchors
        // to the scroll content — items above/below the original click point
        // stay inside the rectangle as the view scrolls under them (Finder UX).
        // Re-collects hit paths immediately so the preview tracks the new
        // content under the unchanged cursor position.
        private void AutoScrollTick()
        {
            if (!_isTracking || !_isVisible) return;

            var scrollView = _collection.ResolveActiveScrollView();
            if (scrollView == null) return;

            var viewport = scrollView.worldBound;
            if (viewport.height <= 0f) return;

            var cursorY = _lastCursorPanelPos.y;
            float deltaY;
            if (cursorY < viewport.yMin)
            {
                deltaY = -AutoScrollMaxSpeedPxPerTick;
            }
            else if (cursorY < viewport.yMin + AutoScrollEdgeZonePx)
            {
                var t = 1f - (cursorY - viewport.yMin) / AutoScrollEdgeZonePx;
                deltaY = -AutoScrollMaxSpeedPxPerTick * t;
            }
            else if (cursorY > viewport.yMax)
            {
                deltaY = AutoScrollMaxSpeedPxPerTick;
            }
            else if (cursorY > viewport.yMax - AutoScrollEdgeZonePx)
            {
                var t = 1f - (viewport.yMax - cursorY) / AutoScrollEdgeZonePx;
                deltaY = AutoScrollMaxSpeedPxPerTick * t;
            }
            else
            {
                return;
            }

            // Short lists can have a NEGATIVE scrollable height (inverted
            // scroller bounds; see the list-striping rework). An unclamped
            // scrollOffset write there bounces to a garbage offset, and the
            // bounce would shift the start anchors below — the marquee origin
            // visibly drifts away from the press point. Clamp to the real
            // scrollable range and skip the write when nothing can scroll.
            var oldOffsetY    = scrollView.scrollOffset.y;
            var maxOffsetY    = Mathf.Max(0f, scrollView.verticalScroller.highValue);
            var targetOffsetY = Mathf.Clamp(oldOffsetY + deltaY, 0f, maxOffsetY);
            if (Mathf.Abs(targetOffsetY - oldOffsetY) < 0.01f) return;

            scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, targetOffsetY);
            var actualDeltaY = scrollView.scrollOffset.y - oldOffsetY;
            if (Mathf.Abs(actualDeltaY) < 0.01f) return;

            _startPanelPos.y    -= actualDeltaY;
            _hitStartPanelPos.y -= actualDeltaY;

            UpdateOverlayRect(_lastCursorPanelPos);
            RefreshHitPathsAndPreview(_lastCursorPanelPos);
        }

        // Shared per-move / per-tick tail: work out which cells the rectangle
        // gained and lost, resolve only those to paths, and push the delta.
        // An unmoved rectangle covers the same cells and stops here.
        //
        // The paths of cells that stayed in the rectangle are NOT re-resolved,
        // so an asset list that changes under a live drag leaves the preview
        // highlight stale until the next move that touches those cells. The
        // committed selection cannot go stale: BuildCommittedPaths re-resolves
        // every cell at release.
        private void RefreshHitPathsAndPreview(Vector2 currentPanelPos)
        {
            var worldRect = BuildWorldRect(_hitStartPanelPos, currentPanelPos);
            var region = _collection.ComputeMarqueeHitRegion(worldRect);
            if (region.Matches(_hitRegion))
            {
                return;
            }

            _addedPaths.Clear();
            _scratchCells.Clear();
            region.AppendCellsMissingFrom(_hitRegion, _scratchCells);
            for (var i = 0; i < _scratchCells.Count; i++)
            {
                if (_collection.TryGetMarqueePath(_scratchCells[i], out var path)
                    && _currentPathsSet.Add(path))
                {
                    _previewSetInsertions++;
                    _addedPaths.Add(path);
                }
            }

            _removedPaths.Clear();
            _scratchCells.Clear();
            _hitRegion.AppendCellsMissingFrom(region, _scratchCells);
            for (var i = 0; i < _scratchCells.Count; i++)
            {
                if (!_collection.TryGetMarqueePath(_scratchCells[i], out var path))
                {
                    continue;
                }

                // An additive drag keeps what was selected before it started,
                // whether or not the rectangle still covers it.
                if (_isAdditive && _baseSelectionSet.Contains(path))
                {
                    continue;
                }

                if (_currentPathsSet.Remove(path))
                {
                    _removedPaths.Add(path);
                }
            }

            _hitRegion = region;
            if (_addedPaths.Count == 0 && _removedPaths.Count == 0)
            {
                return;
            }

            _collection.ApplyMarqueePreview(_addedPaths, _removedPaths);
        }

        // The one full pass of the gesture. The drag maintained membership
        // incrementally; the commit needs the ORDER too — base selection
        // first, then the swept cells in visible order — because the
        // substrate anchors its pivot and lead on the ends of this list.
        private void BuildCommittedPaths()
        {
            _scratchHitPaths.Clear();
            _scratchCells.Clear();
            _hitRegion.AppendCellsMissingFrom(MarqueeHitRegion.Empty, _scratchCells);
            for (var i = 0; i < _scratchCells.Count; i++)
            {
                if (_collection.TryGetMarqueePath(_scratchCells[i], out var path))
                {
                    _scratchHitPaths.Add(path);
                }
            }

            _currentPaths.Clear();
            _currentPathsSet.Clear();

            if (_isAdditive)
            {
                for (var i = 0; i < _baseSelection.Count; i++)
                {
                    var p = _baseSelection[i];
                    if (_currentPathsSet.Add(p))
                    {
                        _currentPaths.Add(p);
                    }
                }
            }

            for (var i = 0; i < _scratchHitPaths.Count; i++)
            {
                var p = _scratchHitPaths[i];
                if (_currentPathsSet.Add(p))
                {
                    _currentPaths.Add(p);
                }
            }
        }

        private void UpdateOverlayRect(Vector2 currentPanelPos)
        {
            var startLocal   = _overlayHost.WorldToLocal(_startPanelPos);
            var currentLocal = _overlayHost.WorldToLocal(currentPanelPos);

            var left   = Mathf.Min(startLocal.x, currentLocal.x);
            var top    = Mathf.Min(startLocal.y, currentLocal.y);
            var width  = Mathf.Abs(currentLocal.x - startLocal.x);
            var height = Mathf.Abs(currentLocal.y - startLocal.y);

            _overlay.style.left   = left;
            _overlay.style.top    = top;
            _overlay.style.width  = width;
            _overlay.style.height = height;
        }

        private static Rect BuildWorldRect(Vector2 a, Vector2 b)
        {
            return Rect.MinMaxRect(
                Mathf.Min(a.x, b.x),
                Mathf.Min(a.y, b.y),
                Mathf.Max(a.x, b.x),
                Mathf.Max(a.y, b.y));
        }
    }
}
