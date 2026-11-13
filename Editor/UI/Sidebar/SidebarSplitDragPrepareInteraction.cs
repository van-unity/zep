using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Sidebar
{
    // Owns the press/click/drag threshold for one sidebar split source surface.
    // The source callback runs on pointer-down so virtualized row data is never
    // consulted again after the gesture begins.
    internal sealed class SidebarSplitDragPrepareInteraction
    {
        private const float DragStartDistance = 6f;

        private readonly VisualElement _surface;
        private readonly Func<VisualElement, ZepSplitDragPayload>
            _snapshotPayload;
        private readonly Action<ZepSplitDragPayload> _commitClick;
        private readonly Action<ZepSplitDragPayload> _startDrag;

        private ZepSplitDragPayload _pendingPayload;
        private Vector2 _startPosition;
        private int _pointerId = -1;

        internal SidebarSplitDragPrepareInteraction(
            VisualElement surface,
            Func<VisualElement, ZepSplitDragPayload> snapshotPayload,
            Action<ZepSplitDragPayload> commitClick,
            Action<ZepSplitDragPayload> startDrag = null)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _snapshotPayload = snapshotPayload
                ?? throw new ArgumentNullException(nameof(snapshotPayload));
            _commitClick = commitClick
                ?? throw new ArgumentNullException(nameof(commitClick));
            _startDrag = startDrag ?? (payload => ZepSplitDragAndDrop.Start(payload, _surface));

            _surface.RegisterCallback<PointerDownEvent>(
                OnPointerDown,
                TrickleDown.TrickleDown);
            _surface.RegisterCallback<PointerMoveEvent>(
                OnPointerMove,
                TrickleDown.TrickleDown);
            _surface.RegisterCallback<PointerUpEvent>(
                OnPointerUp,
                TrickleDown.TrickleDown);
            _surface.RegisterCallback<PointerCancelEvent>(
                OnPointerCancel,
                TrickleDown.TrickleDown);
            _surface.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
            _surface.RegisterCallback<MouseDownEvent>(
                SuppressCompatibilityMouseDown,
                TrickleDown.TrickleDown);
            _surface.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _surface.RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        internal bool IsPending => _pendingPayload != null;

        internal void Cancel()
        {
            ClearPending(releaseCapture: true);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            ClearPending(releaseCapture: true);
            if (evt.button != 0 || evt.clickCount != 1)
            {
                return;
            }

            var payload = _snapshotPayload(evt.target as VisualElement);
            if (payload == null)
            {
                return;
            }

            // A fresh source press supersedes any stale drag bus state left by
            // a terminal route in another editor window.
            ZepSplitDragAndDrop.Clear();
            _pendingPayload = payload;
            _startPosition = new Vector2(evt.position.x, evt.position.y);
            _pointerId = evt.pointerId;
            _surface.CapturePointer(_pointerId);

            // ListView normally selects on pointer-down. Suppress that default
            // action and commit the snapped folder/tag click ourselves
            // on pointer-up, after the gesture is known not to be a drag.
            SuppressEvent(evt);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_pendingPayload == null || evt.pointerId != _pointerId)
            {
                return;
            }

            var delta = new Vector2(evt.position.x, evt.position.y)
                - _startPosition;
            if (delta.sqrMagnitude < DragStartDistance * DragStartDistance)
            {
                return;
            }

            var payload = _pendingPayload;
            ClearPending(releaseCapture: true);
            _startDrag(payload);
            SuppressEvent(evt);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_pendingPayload == null
                || evt.pointerId != _pointerId
                || evt.button != 0)
            {
                return;
            }

            var payload = _pendingPayload;
            ClearPending(releaseCapture: true);
            _commitClick(payload);
            SuppressEvent(evt);
        }

        private void SuppressCompatibilityMouseDown(MouseDownEvent evt)
        {
            if (_pendingPayload == null || evt.button != 0)
            {
                return;
            }

            SuppressEvent(evt);
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            if (_pendingPayload != null && evt.pointerId == _pointerId)
            {
                Cancel();
            }
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (_pendingPayload != null && evt.pointerId == _pointerId)
            {
                ClearPending(releaseCapture: false);
            }
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_pendingPayload == null || evt.keyCode != KeyCode.Escape)
            {
                return;
            }

            Cancel();
            evt.StopPropagation();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            Cancel();
        }

        private void ClearPending(bool releaseCapture)
        {
            var capturedPointerId = _pointerId;
            _pendingPayload = null;
            _pointerId = -1;

            if (releaseCapture
                && capturedPointerId >= 0
                && _surface.HasPointerCapture(capturedPointerId))
            {
                _surface.ReleasePointer(capturedPointerId);
            }
        }

        private static void SuppressEvent(EventBase evt)
        {
            evt.StopImmediatePropagation();
        }
    }
}
