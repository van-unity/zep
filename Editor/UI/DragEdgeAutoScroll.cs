using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // Edge-zone auto-scroll for native DragAndDrop hovers: while a drag is
    // over a surface, holding the cursor near (or past) the top/bottom edge
    // of that surface's ScrollView scrolls it in that direction so drop
    // targets outside the viewport become reachable. One instance per drag
    // surface (the assets pane and the sidebar).
    //
    // Time-driven on purpose: platforms don't reliably deliver DragUpdated
    // while the cursor holds still — exactly the pose a user takes while
    // waiting at an edge for the view to scroll — so a recurring scheduled
    // tick reads the last hover position instead of relying on event cadence.
    // The owner forwards every DragUpdated position via OnDragUpdated and
    // ends the hover via Stop() from the same teardown set the drop-target
    // highlight already trusts (DragLeave / DragExited / DragPerform);
    // detaching the host stops the ticker as well.
    //
    // Scroll writes are clamped to the real scrollable range: short content
    // can report a NEGATIVE scrollable height (inverted scroller bounds, see
    // the list-striping rework), where an unclamped scrollOffset write
    // bounces to a garbage offset.
    //
    // Tuning matches MarqueeInteraction's proven auto-scroll feel.
    internal sealed class DragEdgeAutoScroll
    {
        // Distance from a viewport edge (in panel px) where auto-scroll
        // engages and ramps from 0 → max as the cursor approaches the edge;
        // beyond the edge the ramp clamps to max.
        internal const float EdgeZonePx = 40f;
        // Per-tick scroll delta in panel px at maximum speed. 16 px * 60
        // ticks/s = ~960 px/s.
        internal const float MaxSpeedPxPerTick = 16f;
        private const long TickIntervalMs = 16;

        private readonly Func<ScrollView> _resolveScrollView;
        // Invoked after a tick actually moved content, with the last hover
        // position: the owner re-derives its drop-target highlight from
        // whatever now sits under the (unmoved) cursor, keeping the highlight
        // in agreement with what a drop at that position would hit.
        private readonly Action<Vector2> _scrolled;
        private readonly IVisualElementScheduledItem _ticker;

        private Vector2 _lastDragPanelPos;
        private bool _isActive;

        internal DragEdgeAutoScroll(
            VisualElement host,
            Func<ScrollView> resolveScrollView,
            Action<Vector2> scrolled)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            _resolveScrollView = resolveScrollView ?? throw new ArgumentNullException(nameof(resolveScrollView));
            _scrolled = scrolled;

            _ticker = host.schedule.Execute(Tick).Every(TickIntervalMs);
            _ticker.Pause();
            host.RegisterCallback<DetachFromPanelEvent>(_ => Stop());
        }

        internal void OnDragUpdated(Vector2 dragPanelPosition)
        {
            _lastDragPanelPos = dragPanelPosition;
            if (_isActive) return;

            _isActive = true;
            _ticker.Resume();
        }

        internal void Stop()
        {
            if (!_isActive) return;

            _isActive = false;
            _ticker.Pause();
        }

        // The scroll policy, kept pure for tests: signed per-tick delta for a
        // cursor at cursorY against a viewport spanning [viewportYMin,
        // viewportYMax]. Zero everywhere except the two edge zones.
        internal static float ComputeScrollDeltaPerTick(
            float cursorY,
            float viewportYMin,
            float viewportYMax)
        {
            if (cursorY < viewportYMin)
            {
                return -MaxSpeedPxPerTick;
            }

            if (cursorY < viewportYMin + EdgeZonePx)
            {
                var t = 1f - (cursorY - viewportYMin) / EdgeZonePx;
                return -MaxSpeedPxPerTick * t;
            }

            if (cursorY > viewportYMax)
            {
                return MaxSpeedPxPerTick;
            }

            if (cursorY > viewportYMax - EdgeZonePx)
            {
                var t = 1f - (viewportYMax - cursorY) / EdgeZonePx;
                return MaxSpeedPxPerTick * t;
            }

            return 0f;
        }

        private void Tick()
        {
            if (!_isActive) return;

            var scrollView = _resolveScrollView();
            if (scrollView == null) return;

            var viewport = scrollView.worldBound;
            if (viewport.height <= 0f) return;

            var deltaY = ComputeScrollDeltaPerTick(_lastDragPanelPos.y, viewport.yMin, viewport.yMax);
            if (deltaY == 0f) return;

            var oldOffsetY = scrollView.scrollOffset.y;
            var maxOffsetY = Mathf.Max(0f, scrollView.verticalScroller.highValue);
            var targetOffsetY = Mathf.Clamp(oldOffsetY + deltaY, 0f, maxOffsetY);
            if (Mathf.Abs(targetOffsetY - oldOffsetY) < 0.01f) return;

            scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, targetOffsetY);
            if (Mathf.Abs(scrollView.scrollOffset.y - oldOffsetY) < 0.01f) return;

            _scrolled?.Invoke(_lastDragPanelPos);
        }
    }
}
