using System;
using System.Reflection;
using Zep.Editor.UI.TopBar;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // Owns the native popup lifetime; the reusable widget keeps the query across opens.
    internal sealed class ZepSearchWindow : EditorWindow
    {
        internal const string WidthPreferenceKey = "Zep.Search.Width";
        internal const string HeightPreferenceKey = "Zep.Search.Height";
        internal static readonly Vector2 DefaultSize = new Vector2(700f, 620f);
        internal static readonly Vector2 MinimumSize = new Vector2(360f, 300f);
        private static readonly Vector2 MaximumSize = new Vector2(4000f, 4000f);
        private static MethodInfo _fitRectToScreen;
        private static bool _fitLookedUp;

        [Flags]
        private enum ResizeEdge
        {
            Left = 1,
            Right = 2,
            Top = 4,
            Bottom = 8
        }

        private ZepShellView _shell;
        private EditorWindow _hostWindow;
        private SearchBarWidget _search;
        private VisualElement _returnFocus;
        private ZepThemeSwitcher _themeSwitcher;
        private VisualElement _dragSurface;
        // Desktop rect of record. Like the context menu's ScreenRect: Unity can
        // report position as (0, 0) after moving the native popup, so every drag
        // and resize starts from this instead of reading it back.
        private Rect? _screenRect;
        private Vector2 _dragGrabOffset;
        private bool _dragScreenValid;
        private VisualElement _resizeHandle;
        private ResizeEdge _resizeEdges;
        private Vector2 _resizeStartCursor;
        private Rect _resizeStartRect;
        private bool _resizeScreenValid;
        private Action _closed;
        private bool _restoreFocus;

        internal static ZepSearchWindow Show(
            ZepShellView shell, SearchBarWidget search, Action closed)
        {
            if (shell.panel == null) return null;
            EditorWindow host = ZepWindow.FindByPanel(shell.panel);
            if (host == null)
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (candidate.rootVisualElement.panel != shell.panel) continue;
                    host = candidate;
                    break;
                }
            }
            if (host == null) return null;

            var window = CreateInstance<ZepSearchWindow>();
            window._shell = shell;
            window._hostWindow = host;
            window._search = search;
            window._closed = closed;
            window._returnFocus = shell.focusController?.focusedElement as VisualElement;
            window.titleContent = new GUIContent("Search assets");

            var root = new VisualElement { focusable = true };
            root.AddToClassList(ZepUiClasses.Root);
            root.AddToClassList(ZepUiClasses.SearchWindow);
            window.rootVisualElement.Add(root);
            foreach (var path in new[]
                     { ZepUiClasses.PanelStyleSheetPath, ZepUiClasses.DefaultsStyleSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) root.styleSheets.Add(sheet);
            }
            window._themeSwitcher = new ZepThemeSwitcher(root, shell.Registry, null, null);
            window.OnThemeApplied(shell.ThemeSwitcher.CurrentTheme);
            shell.ThemeApplied += window.OnThemeApplied;
            root.Add(search);
            search.Closed += window.OnSearchClosed;
            search.QuickLookRequested += window.OnQuickLookRequested;
            search.QuickLookDismissRequested += window.OnQuickLookDismissRequested;
            window._dragSurface = root;
            root.RegisterCallback<KeyDownEvent>(window.OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyUpEvent>(shell.ShortcutController.HandleKeyUp, TrickleDown.TrickleDown);
            search.Q(className: ZepUiClasses.SearchHeading).tooltip = "Drag to move search";
            search.Q(className: ZepUiClasses.SearchFooter).tooltip = "Drag to move search";
            root.RegisterCallback<PointerDownEvent>(window.OnDragPointerDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(window.OnDragPointerMove);
            root.RegisterCallback<PointerUpEvent>(window.OnDragPointerUp);

            // Eight hit zones make the frameless popup resize like a window: each
            // edge and corner carries its own cursor, and the bottom-right one is
            // also drawn so the affordance stays discoverable.
            foreach (var (edges, className) in new[]
                     {
                         (ResizeEdge.Left, ZepUiClasses.SearchResizeEdgeLeft),
                         (ResizeEdge.Right, ZepUiClasses.SearchResizeEdgeRight),
                         (ResizeEdge.Top, ZepUiClasses.SearchResizeEdgeTop),
                         (ResizeEdge.Bottom, ZepUiClasses.SearchResizeEdgeBottom),
                         (ResizeEdge.Top | ResizeEdge.Left, ZepUiClasses.SearchResizeCornerTopLeft),
                         (ResizeEdge.Top | ResizeEdge.Right, ZepUiClasses.SearchResizeCornerTopRight),
                         (ResizeEdge.Bottom | ResizeEdge.Left, ZepUiClasses.SearchResizeCornerBottomLeft),
                         (ResizeEdge.Bottom | ResizeEdge.Right, ZepUiClasses.SearchResizeCornerBottomRight)
                     })
            {
                var isGrip = edges == (ResizeEdge.Bottom | ResizeEdge.Right);
                VisualElement handle = isGrip
                    ? new Label("◢") { tooltip = "Drag to resize search" }
                    : new VisualElement();
                handle.userData = edges;
                handle.AddToClassList(ZepUiClasses.SearchResizeEdge);
                handle.AddToClassList(className);
                if (isGrip) handle.AddToClassList(ZepUiClasses.SearchResizeGrip);
                handle.RegisterCallback<PointerDownEvent>(window.OnResizePointerDown);
                handle.RegisterCallback<PointerMoveEvent>(window.OnResizePointerMove);
                handle.RegisterCallback<PointerUpEvent>(window.OnResizePointerUp);
                root.Add(handle);
            }

            // Own focus dismissal so the companion Quick Look remains interactive.
            // ShowAsDropDown would close Search as soon as Quick Look takes focus.
            window.minSize = MinimumSize;
            window.maxSize = MaximumSize;
            var size = RememberedSize();
            window.position = FitToScreen(new Rect(host.position.center - size * 0.5f, size), host.position.center);
            window.ShowPopup();
            window.FocusSearch();
            return window;
        }

        // The last size the user left, never below the layout's floor. The
        // position is deliberately not remembered: the popup opens centred on
        // its host, which is where the user is looking.
        internal static Vector2 RememberedSize() => Vector2.Min(MaximumSize, Vector2.Max(MinimumSize, new Vector2(
            EditorPrefs.GetFloat(WidthPreferenceKey, DefaultSize.x),
            EditorPrefs.GetFloat(HeightPreferenceKey, DefaultSize.y))));

        // ContainerWindow.FitRectToScreen(Rect, Vector2 uiPosition, bool
        // forceCompletelyVisible, ContainerWindow) — internal but present on
        // 6000.0/3/5. The point picks the monitor, so the popup lands on the
        // host's display rather than the mouse's; without it the rect is used
        // as asked, exactly like the context menu's fit.
        private static Rect FitToScreen(Rect rect, Vector2 anchor)
        {
            if (!_fitLookedUp)
            {
                _fitLookedUp = true;
                var containerWindow = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                _fitRectToScreen = containerWindow?.GetMethod("FitRectToScreen",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(Rect), typeof(Vector2), typeof(bool), containerWindow }, null);
            }
            if (_fitRectToScreen == null) return rect;
            try
            {
                return (Rect)_fitRectToScreen.Invoke(null, new object[] { rect, anchor, true, null });
            }
            catch (Exception)
            {
                _fitRectToScreen = null;
                return rect;
            }
        }

        internal void FocusSearch()
        {
            Focus();
            _search.FocusSearch();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            _shell.ShortcutController.HandleKeyDown(evt);
            // The frame can own focus after a drag; route its keys through the
            // same dialog handler used by fields, results and the type drawer.
            if (!evt.isPropagationStopped) _search.OnSearchFieldKeyDown(evt);
        }

        private void OnDragPointerDown(PointerDownEvent evt)
        {
            _shell.ShortcutController.ResetDoubleShift();
            if (evt.button != 0 || evt.target is VisualElement target
                && (target.ClassListContains(ZepUiClasses.SearchResizeEdge)
                    || target is Button || target.GetFirstAncestorOfType<Button>() != null)) return;

            var typeBackdrop = _search.Q(className: ZepUiClasses.SearchTypeBackdrop);
            if (evt.target is VisualElement drawerTarget && (drawerTarget == typeBackdrop || typeBackdrop.Contains(drawerTarget))) return;

            // Include the full-width padding around the title and footer, while
            // leaving the input, scope controls and results to their own gestures.
            var pointer = (Vector2)evt.position;
            var field = _search.Q("zep-search");
            var footer = _search.Q(className: ZepUiClasses.SearchFooter);
            var footerTop = footer.worldBound.yMin - footer.resolvedStyle.marginTop;
            if (!(pointer.y < field.worldBound.yMin || pointer.y >= footerTop)) return;

            // Match Quick Look: desktop coordinates avoid feeding delayed native
            // window movement back into the drag. UITK may lack a GUI context,
            // so validate the conversion while the window is still stationary.
            _dragGrabOffset = evt.position;
            _screenRect ??= position;
            var screen = GUIUtility.GUIToScreenPoint(_dragGrabOffset);
            _dragScreenValid = (screen - (_screenRect.Value.position + _dragGrabOffset)).sqrMagnitude < 64f;
            _dragSurface.focusController?.IgnoreEvent(evt);
            _dragSurface.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnDragPointerMove(PointerMoveEvent evt)
        {
            if (!_dragSurface.HasPointerCapture(evt.pointerId)) return;
            var pointer = (Vector2)evt.position;
            var cursor = _dragScreenValid
                ? GUIUtility.GUIToScreenPoint(pointer)
                : _screenRect.Value.position + pointer;
            var origin = cursor - _dragGrabOffset;
            if ((origin - _screenRect.Value.position).sqrMagnitude > 0f)
            {
                _screenRect = new Rect(origin, _screenRect.Value.size);
                position = _screenRect.Value;
            }
            evt.StopPropagation();
        }

        private void OnDragPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0 || !_dragSurface.HasPointerCapture(evt.pointerId)) return;
            _dragSurface.ReleasePointer(evt.pointerId);
            // A frameless popup has no title bar to recover from off-screen, so
            // a drag that leaves the monitor is pulled back onto it on release.
            var fitted = FitToScreen(_screenRect.Value, _screenRect.Value.center);
            if (fitted != _screenRect.Value)
            {
                _screenRect = fitted;
                position = fitted;
            }
            evt.StopPropagation();
        }

        private void OnResizePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || evt.currentTarget is not VisualElement handle) return;
            _screenRect ??= position;
            _resizeHandle = handle;
            _resizeEdges = (ResizeEdge)handle.userData;
            _resizeStartRect = _screenRect.Value;
            // Same desktop-space anchoring as the drag: a left or top edge moves
            // the window origin, which would otherwise shift the local pointer
            // under the cursor and feed back into the next delta.
            var pointer = (Vector2)evt.position;
            var screen = GUIUtility.GUIToScreenPoint(pointer);
            _resizeScreenValid = (screen - (_screenRect.Value.position + pointer)).sqrMagnitude < 64f;
            _resizeStartCursor = _resizeScreenValid ? screen : _screenRect.Value.position + pointer;
            handle.focusController?.IgnoreEvent(evt);
            handle.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnResizePointerMove(PointerMoveEvent evt)
        {
            if (_resizeHandle == null || !_resizeHandle.HasPointerCapture(evt.pointerId)) return;
            var pointer = (Vector2)evt.position;
            var cursor = _resizeScreenValid
                ? GUIUtility.GUIToScreenPoint(pointer)
                : _screenRect.Value.position + pointer;
            var delta = cursor - _resizeStartCursor;
            // The grabbed edge follows the cursor; the opposite edge stays put,
            // so a left/top drag moves the origin by exactly the width/height change.
            var rect = _resizeStartRect;
            if ((_resizeEdges & ResizeEdge.Right) != 0)
                rect.width = Mathf.Clamp(_resizeStartRect.width + delta.x, minSize.x, maxSize.x);
            if ((_resizeEdges & ResizeEdge.Bottom) != 0)
                rect.height = Mathf.Clamp(_resizeStartRect.height + delta.y, minSize.y, maxSize.y);
            if ((_resizeEdges & ResizeEdge.Left) != 0)
            {
                rect.width = Mathf.Clamp(_resizeStartRect.width - delta.x, minSize.x, maxSize.x);
                rect.x = _resizeStartRect.xMax - rect.width;
            }
            if ((_resizeEdges & ResizeEdge.Top) != 0)
            {
                rect.height = Mathf.Clamp(_resizeStartRect.height - delta.y, minSize.y, maxSize.y);
                rect.y = _resizeStartRect.yMax - rect.height;
            }
            if (rect != _screenRect.Value)
            {
                _screenRect = rect;
                position = rect;
                Repaint();
            }
            evt.StopPropagation();
        }

        private void OnResizePointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0 || _resizeHandle == null || !_resizeHandle.HasPointerCapture(evt.pointerId)) return;
            _resizeHandle.ReleasePointer(evt.pointerId);
            _resizeHandle = null;
            EditorPrefs.SetFloat(WidthPreferenceKey, _screenRect.Value.width);
            EditorPrefs.SetFloat(HeightPreferenceKey, _screenRect.Value.height);
            // Flush the native backing surface after the resize/layout event has
            // finished, otherwise macOS can retain a stretched frame until input.
            EditorApplication.delayCall += () => { if (this != null) Repaint(); };
            Repaint();
            evt.StopPropagation();
        }

        private void ModifierKeysChanged()
        {
            if (focusedWindow == this && _shell != null)
                _shell.ShortcutController.HandleModifierKeysChanged(
                    EditorGUIUtility.CommandEvent("ModifierKeysChanged").modifiers);
        }

        private void OnLostFocus()
        {
            _shell?.ShortcutController.ResetDoubleShift();
            EditorApplication.delayCall += CloseIfFocusLeft;
        }

        internal void CloseIfFocusLeft()
        {
            if (this == null || _shell == null || focusedWindow == this) return;
            if (focusedWindow is ZepQuickLookWindow && ZepQuickLookWindow.IsOpenForSearch(_search)) return;
            Close();
        }

        private void OnEnable()
        {
            EditorApplication.focusChanged += OnApplicationFocusChanged;
            // Native windows can be restored after reload without their shell wiring.
            EditorApplication.delayCall += CloseIfUnbound;
        }

        private void OnApplicationFocusChanged(bool focused)
        {
            // Like context menus, close on app switches: OnLostFocus may stay
            // silent while Unity still considers this popup its focused window.
            if (!focused) Close();
        }

        private void CloseIfUnbound()
        {
            if (this != null && _shell == null) Close();
        }

        private void OnThemeApplied(string themeId)
        {
            _themeSwitcher.SetTheme(themeId, persistPreference: false);
        }

        private void OnQuickLookRequested() => ZepQuickLookWindow.Toggle(_shell, this, _search);

        private bool OnQuickLookDismissRequested() => ZepQuickLookWindow.CloseForSearch(_search);

        private void OnSearchClosed()
        {
            _restoreFocus = true;
            Close();
        }

        private void OnDisable()
        {
            EditorApplication.focusChanged -= OnApplicationFocusChanged;
            EditorApplication.delayCall -= CloseIfUnbound;
            EditorApplication.delayCall -= CloseIfFocusLeft;
            if (_shell == null) return;
            _shell.ThemeApplied -= OnThemeApplied;
            _search.Closed -= OnSearchClosed;
            _search.QuickLookRequested -= OnQuickLookRequested;
            _search.QuickLookDismissRequested -= OnQuickLookDismissRequested;
            _dragSurface.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _dragSurface.UnregisterCallback<KeyUpEvent>(_shell.ShortcutController.HandleKeyUp, TrickleDown.TrickleDown);
            _dragSurface.UnregisterCallback<PointerDownEvent>(OnDragPointerDown, TrickleDown.TrickleDown);
            _dragSurface.UnregisterCallback<PointerMoveEvent>(OnDragPointerMove);
            _dragSurface.UnregisterCallback<PointerUpEvent>(OnDragPointerUp);
            _dragSurface.Query(className: ZepUiClasses.SearchResizeEdge).ForEach(handle =>
            {
                handle.UnregisterCallback<PointerDownEvent>(OnResizePointerDown);
                handle.UnregisterCallback<PointerMoveEvent>(OnResizePointerMove);
                handle.UnregisterCallback<PointerUpEvent>(OnResizePointerUp);
            });
            _search.Hide();
            ZepQuickLookWindow.CloseForSearch(_search);
            _search.RemoveFromHierarchy();
            _themeSwitcher.Teardown();
            _closed?.Invoke();
            if (_restoreFocus && _hostWindow != null && _shell.panel != null)
            {
                _hostWindow.Focus();
                if (_returnFocus?.panel == _shell.panel && _returnFocus.enabledInHierarchy)
                    _returnFocus.Focus();
                else
                    _shell.Focus();
            }
            _closed = null;
            _shell = null;
        }
    }
}
