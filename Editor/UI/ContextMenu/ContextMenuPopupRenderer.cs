using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.ContextMenu
{
    // Shows a ZepContextMenuItem tree as the panel's own themed popup
    // menu: one frameless window per open level, placed in desktop space so the
    // menu can hang outside the panel window. Owns dismissal (focus loss,
    // Escape, host going away), submenu hover timing, keyboard navigation and
    // action dispatch. Falls back to the native GenericMenu when there is no
    // live panel window to borrow a theme and a desktop origin from.
    internal static class ContextMenuPopupRenderer
    {
        private const double SubmenuOpenDelaySeconds = 0.12;
        private const double SubmenuCloseDelaySeconds = 0.25;
        private const float SubmenuOverlap = 4f;

        private static Session _current;
        private static MethodInfo _fitRectToMouseScreen;
        private static bool _fitLookedUp;

        internal static bool IsOpen => _current != null;

        internal static void Show(
            VisualElement owner,
            IReadOnlyList<ZepContextMenuItem> items,
            Action<ZepContextMenuItem> dispatchUnbound,
            Rect? anchor)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var shell = owner as ZepShellView ?? owner?.GetFirstAncestorOfType<ZepShellView>();
            var host = ResolveHostWindow(owner);
            var paneWindow = host as ZepPaneWindow;
            if ((shell == null && paneWindow == null) || host == null || !anchor.HasValue || Application.isBatchMode)
            {
                ContextMenuGenericMenuRenderer.Show(items, dispatchUnbound, anchor);
                return;
            }

            CloseCurrent(refocusHost: false);
            // EditorWindow.position is the desktop rect of the view area, which
            // is exactly the UI Toolkit origin of the window's root element.
            var anchorScreen = new Rect(host.position.position + anchor.Value.position, anchor.Value.size);
            _current = new Session(shell, host, dispatchUnbound, paneWindow);
            _current.OpenRoot(items, anchorScreen);
        }

        internal static void CloseCurrent(bool refocusHost)
        {
            _current?.CloseAll(refocusHost);
        }

        private static EditorWindow ResolveHostWindow(VisualElement owner)
        {
            var ownerPanel = owner?.panel;
            if (ownerPanel == null)
            {
                return null;
            }

            // The owner sits under a ZepShellView (Show checks that),
            // and the panel window hosting a shell registers itself — so this
            // is one list lookup. The sweep below only remains for a shell
            // mounted in some other window (an in-editor test host), where a
            // wrong answer would mean a GenericMenu instead of the themed one.
            EditorWindow registered = ZepWindow.FindByPanel(ownerPanel);
            if (registered == null) registered = ZepPaneWindow.FindByPanel(ownerPanel);
            if (registered != null)
            {
                return registered;
            }

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            for (var i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                if (window != null
                    && window.rootVisualElement != null
                    && window.rootVisualElement.panel == ownerPanel)
                {
                    return window;
                }
            }

            return null;
        }

        // ContainerWindow.FitRectToMouseScreen(Rect, bool forceCompletelyVisible,
        // ContainerWindow) — internal but present on 6000.0/3/5. It shifts the
        // rect onto the monitor under the cursor; without it the menu simply
        // opens where asked.
        private static Rect FitToScreen(Rect rect)
        {
            if (!_fitLookedUp)
            {
                _fitLookedUp = true;
                var containerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                _fitRectToMouseScreen = containerWindowType?.GetMethod(
                    "FitRectToMouseScreen",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (_fitRectToMouseScreen != null && _fitRectToMouseScreen.GetParameters().Length != 3)
                {
                    _fitRectToMouseScreen = null;
                }
            }

            if (_fitRectToMouseScreen == null)
            {
                return rect;
            }

            try
            {
                return (Rect)_fitRectToMouseScreen.Invoke(null, new object[] { rect, true, null });
            }
            catch (Exception)
            {
                _fitRectToMouseScreen = null;
                return rect;
            }
        }

        internal sealed class Session
        {
            private readonly ZepShellView _shell;
            private readonly ZepPaneWindow _paneWindow;
            private readonly EditorWindow _host;
            private readonly Action<ZepContextMenuItem> _dispatchUnbound;
            private readonly List<ContextMenuPopupWindow> _windows = new List<ContextMenuPopupWindow>();
            private bool _closing;
            private bool _updateHooked;

            private ContextMenuPopupWindow _pendingOpenWindow;
            private int _pendingOpenIndex = -1;
            private double _pendingOpenAt;
            private int _pendingCloseDeeperThan = -1;
            private double _pendingCloseAt;

            internal Session(
                ZepShellView shell,
                EditorWindow host,
                Action<ZepContextMenuItem> dispatchUnbound,
                ZepPaneWindow paneWindow = null)
            {
                _shell = shell;
                _paneWindow = paneWindow;
                _host = host;
                _dispatchUnbound = dispatchUnbound;
            }

            private ContextMenuPopupWindow Deepest => _windows.Count > 0 ? _windows[_windows.Count - 1] : null;

            internal void OpenRoot(IReadOnlyList<ZepContextMenuItem> items, Rect anchorScreen)
            {
                var window = ContextMenuPopupWindow.Create(this, _shell, items, 0, _paneWindow);
                var size = window.EstimateSize();
                ShowWindow(window, ContextMenuPopupPlacement.PlaceRoot(anchorScreen, size, FitToScreen));
                window.Focus();
                window.FocusRoot();

                EditorApplication.update += OnEditorUpdate;
                _updateHooked = true;
                // Switching to another application never moves focus between
                // editor windows, so OnLostFocus stays silent; without this the
                // popups outlive the app switch and, being the key windows,
                // come back alone when Unity is re-activated from the Dock.
                EditorApplication.focusChanged += OnApplicationFocusChanged;
            }

            private void OnApplicationFocusChanged(bool focused)
            {
                if (!focused)
                {
                    CloseAll(refocusHost: false);
                }
            }

            private void OpenSubmenu(ContextMenuPopupWindow parent, int index)
            {
                if (parent == null || !parent.IsSelectable(index) || !parent.Items[index].HasChildren)
                {
                    return;
                }

                if (parent.OpenChildIndex == index && _windows.Count > parent.Level + 1)
                {
                    return;
                }

                CloseDeeperThan(parent.Level);
                parent.SetActive(index);

                var window = ContextMenuPopupWindow.Create(this, _shell, parent.Items[index].Children, parent.Level + 1, _paneWindow);
                var size = window.EstimateSize();
                var rect = ContextMenuPopupPlacement.PlaceSubmenu(
                    parent.ScreenRect,
                    parent.RowScreenTop(index),
                    size,
                    SubmenuOverlap,
                    ContextMenuPopupWindow.TopPadding,
                    FitToScreen);
                ShowWindow(window, rect);
                parent.OpenChildIndex = index;
            }

            private void ShowWindow(ContextMenuPopupWindow window, Rect rect)
            {
                window.minSize = rect.size;
                window.maxSize = rect.size;
                window.position = rect;
                window.ScreenRect = rect;
                _windows.Add(window);
                window.ShowPopup();
            }

            private void CloseDeeperThan(int level)
            {
                var closedAny = false;
                for (var i = _windows.Count - 1; i >= 0; i--)
                {
                    var window = _windows[i];
                    if (window == null)
                    {
                        _windows.RemoveAt(i);
                        continue;
                    }

                    if (window.Level > level)
                    {
                        _windows.RemoveAt(i);
                        window.Close();
                        closedAny = true;
                    }
                    else
                    {
                        window.OpenChildIndex = -1;
                    }
                }

                // macOS sometimes hands key focus to a freshly shown popup. If
                // the closed submenu held it, focus would land outside the
                // session and the lost-focus check would dismiss everything.
                var remaining = Deepest;
                if (closedAny && remaining != null)
                {
                    remaining.Focus();
                    remaining.FocusRoot();
                }
            }

            internal void CloseAll(bool refocusHost)
            {
                if (_closing)
                {
                    return;
                }

                _closing = true;
                if (_updateHooked)
                {
                    EditorApplication.update -= OnEditorUpdate;
                    _updateHooked = false;
                }

                EditorApplication.focusChanged -= OnApplicationFocusChanged;

                for (var i = _windows.Count - 1; i >= 0; i--)
                {
                    var window = _windows[i];
                    if (window != null)
                    {
                        window.Close();
                    }
                }

                _windows.Clear();
                if (_current == this)
                {
                    _current = null;
                }

                if (refocusHost && _host != null)
                {
                    _host.Focus();
                    _shell?.Focus();
                }
            }

            // ---- window callbacks ----------------------------------------

            internal void OnWindowDestroyed(ContextMenuPopupWindow window)
            {
                if (_closing)
                {
                    return;
                }

                _windows.Remove(window);
                if (_windows.Count == 0)
                {
                    CloseAll(refocusHost: false);
                }
            }

            internal void OnWindowLostFocus()
            {
                // Focus moves between our own windows when the user clicks a
                // submenu; only a focus that lands outside the session dismisses.
                EditorApplication.delayCall += () =>
                {
                    if (_closing)
                    {
                        return;
                    }

                    var focused = EditorWindow.focusedWindow;
                    if (focused == null || !IsSessionWindow(focused))
                    {
                        CloseAll(refocusHost: false);
                    }
                };
            }

            private bool IsSessionWindow(EditorWindow window)
            {
                for (var i = 0; i < _windows.Count; i++)
                {
                    if (_windows[i] == window)
                    {
                        return true;
                    }
                }

                return false;
            }

            internal void OnWindowLaidOut(ContextMenuPopupWindow window, float contentHeight)
            {
                if (_closing || window == null || contentHeight <= 0f)
                {
                    return;
                }

                var current = window.ScreenRect;
                if (Mathf.Abs(current.height - contentHeight) < 0.5f)
                {
                    return;
                }

                // The size estimate mirrors the USS constants; when a font or
                // rule drifts, snap to the laid-out height and re-fit so the
                // bottom never leaves the screen. The fit also caps a level
                // taller than the screen, whose rows then scroll; that capped
                // height never matches the content, so skip the no-op re-set.
                var size = new Vector2(current.width, Mathf.Ceil(contentHeight));
                var rect = FitToScreen(new Rect(current.position, size));
                if (Mathf.Abs(rect.height - current.height) < 0.5f
                    && Vector2.Distance(rect.position, current.position) < 0.5f)
                {
                    return;
                }

                window.minSize = rect.size;
                window.maxSize = rect.size;
                window.position = rect;
                window.ScreenRect = rect;
            }

            internal void OnRowHover(ContextMenuPopupWindow window, int index)
            {
                if (_closing || window == null)
                {
                    return;
                }

                CancelPendingClose();
                if (!window.IsSelectable(index))
                {
                    window.SetActive(-1);
                    CancelPendingOpen();
                    ScheduleCloseDeeperThan(window.Level);
                    return;
                }

                window.SetActive(index);
                if (window.Items[index].HasChildren)
                {
                    if (window.OpenChildIndex != index)
                    {
                        _pendingOpenWindow = window;
                        _pendingOpenIndex = index;
                        _pendingOpenAt = EditorApplication.timeSinceStartup + SubmenuOpenDelaySeconds;
                    }

                    return;
                }

                CancelPendingOpen();
                if (window.OpenChildIndex >= 0)
                {
                    ScheduleCloseDeeperThan(window.Level);
                }
            }

            internal void OnPointerEnteredWindow(ContextMenuPopupWindow window)
            {
                // Moving into an open submenu keeps it and its parent chain.
                if (window != null && window.Level > 0)
                {
                    CancelPendingClose();
                    CancelPendingOpen();
                }
            }

            internal void OnPointerLeftWindow(ContextMenuPopupWindow window)
            {
                if (_closing || window == null)
                {
                    return;
                }

                CancelPendingOpen();
                if (window.OpenChildIndex < 0)
                {
                    window.SetActive(-1);
                }
            }

            internal void OnRowActivate(ContextMenuPopupWindow window, int index)
            {
                if (_closing || window == null || !window.IsSelectable(index))
                {
                    return;
                }

                var item = window.Items[index];
                if (item.HasChildren)
                {
                    CancelPendingOpen();
                    OpenSubmenu(window, index);
                    return;
                }

                Activate(item);
            }

            internal void OnKey(KeyDownEvent evt)
            {
                if (_closing || evt == null)
                {
                    return;
                }

                var window = Deepest;
                if (window == null)
                {
                    return;
                }

                var handled = true;
                switch (evt.keyCode)
                {
                    case KeyCode.DownArrow:
                        window.SetActive(NextSelectable(window, window.ActiveIndex, +1));
                        window.RevealActive();
                        break;
                    case KeyCode.UpArrow:
                        window.SetActive(NextSelectable(window, window.ActiveIndex, -1));
                        window.RevealActive();
                        break;
                    case KeyCode.RightArrow:
                        OpenSubmenuFromKeyboard(window);
                        break;
                    case KeyCode.LeftArrow:
                        if (window.Level > 0)
                        {
                            CloseDeeperThan(window.Level - 1);
                        }

                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    case KeyCode.Space:
                        if (window.IsSelectable(window.ActiveIndex))
                        {
                            if (window.Items[window.ActiveIndex].HasChildren)
                            {
                                OpenSubmenuFromKeyboard(window);
                            }
                            else
                            {
                                Activate(window.Items[window.ActiveIndex]);
                            }
                        }

                        break;
                    case KeyCode.Escape:
                        if (window.Level > 0)
                        {
                            CloseDeeperThan(window.Level - 1);
                        }
                        else
                        {
                            CloseAll(refocusHost: true);
                        }

                        break;
                    default:
                        handled = false;
                        break;
                }

                if (handled)
                {
                    evt.StopPropagation();
                }
            }

            private void OpenSubmenuFromKeyboard(ContextMenuPopupWindow window)
            {
                var index = window.ActiveIndex;
                if (!window.IsSelectable(index) || !window.Items[index].HasChildren)
                {
                    return;
                }

                CancelPendingOpen();
                OpenSubmenu(window, index);
                var child = Deepest;
                if (child != null && child.Level == window.Level + 1)
                {
                    child.SetActive(NextSelectable(child, -1, +1));
                }
            }

            private static int NextSelectable(ContextMenuPopupWindow window, int from, int direction)
            {
                var count = window.Items.Count;
                if (count == 0)
                {
                    return -1;
                }

                var index = from < 0 && direction < 0 ? 0 : from;
                for (var step = 0; step < count; step++)
                {
                    index = ((index + direction) % count + count) % count;
                    if (window.IsSelectable(index))
                    {
                        return index;
                    }
                }

                return from;
            }

            private void Activate(ZepContextMenuItem item)
            {
                CloseAll(refocusHost: true);
                // Like GenericMenu: the action runs after the menu is gone and
                // focus is back on the panel, so inline editors and dialogs the
                // action opens land in the right window.
                EditorApplication.delayCall += () =>
                {
                    if (item.OnClick != null)
                    {
                        item.OnClick.Invoke();
                    }
                    else
                    {
                        _dispatchUnbound?.Invoke(item);
                    }
                };
            }

            // ---- timers -----------------------------------------------------

            private void ScheduleCloseDeeperThan(int level)
            {
                if (_windows.Count <= level + 1)
                {
                    return;
                }

                _pendingCloseDeeperThan = level;
                _pendingCloseAt = EditorApplication.timeSinceStartup + SubmenuCloseDelaySeconds;
            }

            private void CancelPendingClose()
            {
                _pendingCloseDeeperThan = -1;
            }

            private void CancelPendingOpen()
            {
                _pendingOpenWindow = null;
                _pendingOpenIndex = -1;
            }

            private void OnEditorUpdate()
            {
                if (_closing)
                {
                    return;
                }

                if (_host == null || (_shell?.panel == null && _paneWindow?.rootVisualElement.panel == null))
                {
                    CloseAll(refocusHost: false);
                    return;
                }

                var now = EditorApplication.timeSinceStartup;
                if (_pendingOpenWindow != null && now >= _pendingOpenAt)
                {
                    var window = _pendingOpenWindow;
                    var index = _pendingOpenIndex;
                    CancelPendingOpen();
                    if (window != null && window.ActiveIndex == index)
                    {
                        OpenSubmenu(window, index);
                    }
                }

                if (_pendingCloseDeeperThan >= 0 && now >= _pendingCloseAt)
                {
                    var level = _pendingCloseDeeperThan;
                    CancelPendingClose();
                    CloseDeeperThan(level);
                }
            }
        }
    }
}
