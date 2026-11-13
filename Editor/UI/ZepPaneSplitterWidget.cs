using System;
using Zep.Editor.UI.AssetsArea;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    internal sealed class ZepPaneSplitterWidget : VisualElement
    {
        // Must equal the .zep-pane-splitter width in Zep.uss
        // (4px, zero margins) — ShellView/SidebarWidget pane-width math uses it.
        internal const float Width = 4f;

        private float _dragStartMouseX;
        private bool _isDragging;

        public event Action DragStarted;
        public event Action<float> DragMoved;
        public event Action DragEnded;
        public event Action<float> KeyboardResizeRequested;
        public event Action EqualizeRequested;

        public ZepPaneSplitterWidget()
        {
            name = "zep-pane-splitter";
            focusable = true;
            tooltip = "Resize panes";
            AddToClassList(ZepUiClasses.PaneSplitter);

            RegisterCallback<MouseDownEvent>(OnMouseDown);
            RegisterCallback<MouseMoveEvent>(OnMouseMove);
            RegisterCallback<MouseUpEvent>(OnMouseUp);
            RegisterCallback<MouseCaptureOutEvent>(OnMouseCaptureOut);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            var cursorOverlay = new IMGUIContainer(() =>
            {
                var rect = new Rect(
                    0f,
                    0f,
                    Mathf.Max(1f, resolvedStyle.width),
                    Mathf.Max(1f, resolvedStyle.height));
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.SplitResizeLeftRight);
            })
            {
                name = "zep-pane-splitter-cursor",
                pickingMode = PickingMode.Ignore
            };
            cursorOverlay.AddToClassList(ZepUiClasses.AbsoluteFill);
            Add(cursorOverlay);

            var grip = new VisualElement { name = "zep-pane-splitter-grip" };
            grip.AddToClassList(ZepUiClasses.PaneSplitterGrip);
            Add(grip);
        }

        public bool IsVisible => style.display.value != DisplayStyle.None;

        internal bool IsDragging => _isDragging;

        public void CancelDrag()
        {
            if (!_isDragging)
            {
                return;
            }

            _isDragging = false;
            RemoveFromClassList(ZepUiClasses.PaneSplitterDragging);
            if (this.HasMouseCapture())
            {
                this.ReleaseMouse();
            }

            DragEnded?.Invoke();
        }

        private void OnMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 0)
            {
                return;
            }

            if (evt.clickCount == 2)
            {
                EqualizeRequested?.Invoke();
                evt.StopPropagation();
                return;
            }

            _dragStartMouseX = evt.mousePosition.x;
            _isDragging = true;
            this.CaptureMouse();
            AddToClassList(ZepUiClasses.PaneSplitterDragging);
            DragStarted?.Invoke();
            evt.StopPropagation();
        }

        private void OnMouseMove(MouseMoveEvent evt)
        {
            if (!_isDragging)
            {
                return;
            }

            var deltaX = evt.mousePosition.x - _dragStartMouseX;
            DragMoved?.Invoke(deltaX);
            evt.StopPropagation();
        }

        private void OnMouseUp(MouseUpEvent evt)
        {
            if (!_isDragging || evt.button != 0)
            {
                return;
            }

            CancelDrag();
            evt.StopPropagation();
        }

        private void OnMouseCaptureOut(MouseCaptureOutEvent evt)
        {
            CancelDrag();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            float delta;
            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow:
                    delta = -AssetsAreaConstants.SplitDividerKeyboardStep;
                    break;
                case KeyCode.RightArrow:
                    delta = AssetsAreaConstants.SplitDividerKeyboardStep;
                    break;
                default:
                    return;
            }

            KeyboardResizeRequested?.Invoke(delta);
            evt.StopPropagation();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            CancelDrag();
        }
    }
}
