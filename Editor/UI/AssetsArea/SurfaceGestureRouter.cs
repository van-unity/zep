using System;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Owns the "marquee wins over drag-prepare" invariant for an asset surface:
    // the two gesture FSMs (MarqueeInteraction, AssetDragPrepareInteraction) and
    // the four surface handlers that decide which of them sees a mouse gesture.
    //
    // The order is the whole point and it is not obvious from either FSM alone:
    //   - mouse-down: the marquee gets first refusal, and when it takes the
    //     press it StopImmediatePropagation()s so drag-prepare's own
    //     capture-phase handler on the collection (a descendant) never runs;
    //   - move/up: a tracking marquee consumes the event and drag-prepare is
    //     not called at all;
    //   - capture-out: the marquee is cancelled.
    //
    // Both present asset surfaces route exactly this way — AssetsBrowserFeature
    // (folder browsing) and SmartFolderFeature (Tags) — and they had a
    // copy each. They differ only in whether items may be dragged out.
    internal sealed class SurfaceGestureRouter
    {
        private readonly MarqueeInteraction _marquee;
        private readonly AssetDragPrepareInteraction _dragPrepare;

        // Construct AFTER the collection is parented into the surface: the
        // marquee mounts its overlay on the surface and it must sit above the
        // collection in the child order.
        internal SurfaceGestureRouter(
            VisualElement assetSurface,
            AssetCollectionView collection,
            bool allowDragOut = true,
            Func<string, bool> isFolderPath = null)
        {
            if (assetSurface == null) throw new ArgumentNullException(nameof(assetSurface));

            _marquee = new MarqueeInteraction(assetSurface, collection);
            _dragPrepare = new AssetDragPrepareInteraction(collection, assetSurface, allowDragStart: allowDragOut, isFolderPath: isFolderPath);

            // Marquee: intercept mouse-down in TrickleDown on the asset
            // surface. We see the event before any view-specific handler can
            // eat it, and can StopImmediatePropagation to prevent drag-prepare
            // when we begin tracking. Move/up/capture-out follow on the same
            // element.
            assetSurface.RegisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
            assetSurface.RegisterCallback<MouseMoveEvent>(OnMouseMove);
            assetSurface.RegisterCallback<MouseUpEvent>(OnMouseUp);
            assetSurface.RegisterCallback<MouseCaptureOutEvent>(OnCaptureOut);
        }

        // Both FSMs drop whatever they were tracking (pane teardown, mode
        // switch, escape).
        internal void Cancel()
        {
            _marquee.Cancel();
            _dragPrepare.Cancel();
        }

        // Right-click: the context gesture is not a drag, but the marquee is
        // untouched (a right-click during a rubber band must not cancel it —
        // MarqueeInteraction.TryBegin rejects the press on its own).
        internal void CancelDragPrepare() => _dragPrepare.Cancel();

        private void OnMouseDown(MouseDownEvent evt)
        {
            if (ZepContextMenuInput.IsMouseTrigger(evt))
            {
                // The preceding PointerDown already raised the menu. Swallow
                // this compatibility event so no mouse-based control mutates
                // selection or opens a duplicate menu.
                ZepContextMenuInput.Consume(evt);
                return;
            }

            if (_marquee.TryBegin(evt))
            {
                // Prevent drag-prepare on items below: StopImmediatePropagation
                // halts further TrickleDown handlers on descendant elements.
                evt.StopImmediatePropagation();
            }
        }

        private void OnMouseMove(MouseMoveEvent evt)
        {
            if (_marquee.Update(evt))
            {
                evt.StopPropagation();
                return;
            }

            if (_dragPrepare.OnSurfaceMouseMove(evt))
            {
                evt.StopPropagation();
            }
        }

        private void OnMouseUp(MouseUpEvent evt)
        {
            if (_marquee.IsTracking)
            {
                _marquee.End(evt);
                evt.StopPropagation();
                return;
            }

            _dragPrepare.OnSurfaceMouseUp(evt);
        }

        private void OnCaptureOut(MouseCaptureOutEvent evt) => _marquee.Cancel();
    }
}
