using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // One captured pointer owns the gesture. Unity supplies the native window
    // preview and docking geometry; our existing content receivers own asset
    // moves, favorite ordering and split drops.
    internal static class ZepWindowDocking
    {
        private const string DragKey = "Zep.WindowDocking";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type DockArea = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        private static readonly Type View = typeof(EditorWindow).Assembly.GetType("UnityEditor.View");
        private static readonly Type DropArea = typeof(EditorWindow).Assembly.GetType("UnityEditor.IDropArea");
        private static readonly Type DropInfo = typeof(EditorWindow).Assembly.GetType("UnityEditor.DropInfo");
        private static readonly Type PaneDragTab = typeof(EditorWindow).Assembly.GetType("UnityEditor.PaneDragTab");
        private static readonly Type ContainerWindow = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
        private static readonly Type SplitView = typeof(EditorWindow).Assembly.GetType("UnityEditor.SplitView");
        private static readonly Type DispatchMode = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.DispatchMode");
        private static readonly FieldInfo Parent = typeof(EditorWindow).GetField("m_Parent", Instance);
        private static readonly PropertyInfo ViewWindow = View?.GetProperty("window", Instance);
        private static readonly PropertyInfo Windows = ContainerWindow?.GetProperty("windows", Static);
        private static readonly MethodInfo FitRectToMouseScreen = ContainerWindow?.GetMethod("FitRectToMouseScreen", Static);
        private static readonly PropertyInfo WindowPosition = ContainerWindow?.GetProperty("position", Instance);
        private static readonly PropertyInfo RootView = ContainerWindow?.GetProperty("rootView", Instance);
        private static readonly PropertyInfo RootSplitView = ContainerWindow?.GetProperty("rootSplitView", Instance);
        private static readonly PropertyInfo Children = View?.GetProperty("allChildren", Instance);
        private static readonly PropertyInfo ActualView = DockArea?.GetProperty("actualView", Instance);
        private static readonly PropertyInfo ScreenPosition = View?.GetProperty("screenPosition", Instance);
        private static readonly MethodInfo DragOverRoot = SplitView?.GetMethod("DragOverRootView", Instance);
        private static readonly MethodInfo DragOver = DropArea?.GetMethod("DragOver", Instance);
        private static readonly MethodInfo PerformDrop = DropArea?.GetMethod("PerformDrop", Instance);
        private static readonly ConstructorInfo FloatingDrop = DropInfo?.GetConstructor(new[] { DropArea });
        private static readonly FieldInfo DropTarget = DropInfo?.GetField("dropArea", Instance);
        private static readonly FieldInfo OriginalSource = DockArea?.GetField("s_OriginalDragSource", Static);
        private static readonly FieldInfo IgnoreView = DockArea?.GetField("s_IgnoreDockingForView", Static);
        private static readonly FieldInfo NativeDragging = DockArea?.GetField("s_IsDragging", Static);
        private static readonly FieldInfo NativeDropInfo = DockArea?.GetField("s_DropInfo", Static);
        private static readonly FieldInfo Placeholder = DockArea?.GetField("s_PlaceholderPos", Static);
        private static readonly PropertyInfo PreviewInstance = PaneDragTab?.GetProperty("get", Static);
        private static readonly MethodInfo PreviewShow = PaneDragTab?.GetMethod("Show", Instance);
        private static readonly MethodInfo PreviewSetDropInfo = PaneDragTab?.GetMethod("SetDropInfo", Instance);
        private static readonly MethodInfo PreviewClose = PaneDragTab?.GetMethod("Close", Instance);
        private static readonly MethodInfo SendEvent = DispatchMode == null ? null : typeof(VisualElement).GetMethod(
            "SendEvent", Instance, null, new[] { typeof(EventBase), DispatchMode }, null);
        private static readonly object Immediate = DispatchMode == null ? null : Enum.Parse(DispatchMode, "Immediate");

        private static object _marker;
        private static string[] _paths = Array.Empty<string>();
        private static UnityEngine.Object[] _objects = Array.Empty<UnityEngine.Object>();
        private static ZepSplitDragPayload _splitPayload;
        private static EditorWindow _sourceWindow;
        private static VisualElement _capture;
        private static ZepPaneWindow _candidate;
        private static UnityEngine.Object _preview;
        private static AssetDragPrepareInteraction.ThemedDragSession _contentPreview;
        private static Texture _contentIcon;
        private static EditorWindow _hoverWindow;
        private static List<VisualElement> _hoverPath = new List<VisualElement>();
        private static object _dropInfo;
        private static object _dropContainer;
        private static bool _clearing;
        private static bool _completing;
        private static bool _reportedFailure;

        internal static bool IsActive => _marker != null
            && ReferenceEquals(DragAndDrop.GetGenericData(DragKey), _marker)
            && (DragAndDrop.paths ?? Array.Empty<string>()).SequenceEqual(_paths)
            && (DragAndDrop.objectReferences ?? Array.Empty<UnityEngine.Object>()).SequenceEqual(_objects);

        internal static bool IsSupported => Parent != null && ViewWindow != null && Windows != null
            && FitRectToMouseScreen != null && WindowPosition != null && RootView != null && RootSplitView != null && Children != null
            && ActualView != null && ScreenPosition != null && DragOverRoot != null && DragOver != null
            && PerformDrop != null && FloatingDrop != null && DropTarget != null
            && OriginalSource != null && IgnoreView != null && NativeDragging != null
            && NativeDropInfo != null && Placeholder != null && PreviewInstance != null
            && SendEvent != null
            && PreviewShow != null && PreviewSetDropInfo != null && PreviewClose != null
            && (PreviewSetDropInfo.GetParameters().Length == 2
                || PreviewSetDropInfo.GetParameters().Length == 3);

        internal static bool Start(ZepPaneSource source, string displayName, VisualElement origin)
        {
            Clear();
            if (!source.IsAuxiliary)
                throw new ArgumentException("Docking requires a folder or tag root.", nameof(source));
            if (!IsSupported)
            {
                ReportFailure("This Unity version does not expose the required window docking API.");
                return false;
            }

            var window = Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault(candidate =>
                origin?.panel != null && candidate.rootVisualElement.panel == origin.panel
                && (candidate.rootVisualElement == origin || candidate.rootVisualElement.Contains(origin)));
            if (window == null || !DockArea.IsInstanceOfType(Parent.GetValue(window))
                || (bool)NativeDragging.GetValue(null) || Event.current == null) return false;

            _sourceWindow = window;
            _paths = (DragAndDrop.paths ?? Array.Empty<string>()).ToArray();
            _objects = (DragAndDrop.objectReferences ?? Array.Empty<UnityEngine.Object>()).ToArray();
            ZepSplitDragAndDrop.TryRead(out _splitPayload);
            _marker = new object();
            DragAndDrop.SetGenericData(DragKey, _marker);
            try
            {
                _candidate = ZepPaneWindow.Create(source, displayName);
                _candidate.position = new Rect(window.position.position, window.position.size);
                _contentIcon = EditorGUIUtility.IconContent(
                    source.Kind == ZepPaneSourceKind.Tag ? "FilterByLabel" : "Folder Icon").image;
                _capture = origin.panel.visualTree;
                _capture.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                _capture.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                _capture.RegisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
                _capture.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
                _capture.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                window.rootVisualElement.RegisterCallback<DetachFromPanelEvent>(OnSourceDetached);
                _capture.CapturePointer(PointerId.mousePointerId);
                EditorApplication.update += OnUpdate;
                AssemblyReloadEvents.beforeAssemblyReload += Clear;
                UpdateDestination(ToScreen(Event.current.mousePosition));
                return true;
            }
            catch (Exception exception) when (IsReflectionFailure(exception))
            {
                ReportFailure(exception.Message);
                // The caller may fall back to its normal asset drag. Keep its
                // payload even though our capture and native preview failed.
                _splitPayload = null;
                Clear();
                return false;
            }
        }

        internal static void Clear()
        {
            // AddTab detaches real workspace roots, whose teardown calls Clear.
            // Keep this atomic native completion alive until Unity hosts the pane.
            if (_clearing || _completing) return;
            _clearing = true;
            try
            {
                EditorApplication.update -= OnUpdate;
                AssemblyReloadEvents.beforeAssemblyReload -= Clear;
                var capture = _capture;
                _capture = null;
                if (capture != null)
                {
                    capture.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                    capture.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                    capture.UnregisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
                    capture.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
                    capture.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                    if (capture.HasPointerCapture(PointerId.mousePointerId))
                        capture.ReleasePointer(PointerId.mousePointerId);
                }
                if (_sourceWindow != null)
                    _sourceWindow.rootVisualElement.UnregisterCallback<DetachFromPanelEvent>(OnSourceDetached);
                ClearContentHover();
                if (_marker != null && ReferenceEquals(DragAndDrop.GetGenericData(DragKey), _marker))
                    DragAndDrop.SetGenericData(DragKey, null);
                if (_splitPayload != null && ZepSplitDragAndDrop.TryRead(out var payload)
                    && ReferenceEquals(payload, _splitPayload)) ZepSplitDragAndDrop.Clear();
                _marker = null;
                _splitPayload = null;
                _paths = Array.Empty<string>();
                _objects = Array.Empty<UnityEngine.Object>();
                _contentIcon = null;
                if (_preview != null) PreviewClose.Invoke(_preview, null);
                _preview = null;
                if (_dropInfo != null && ReferenceEquals(NativeDropInfo.GetValue(null), _dropInfo))
                {
                    NativeDropInfo.SetValue(null, null);
                    Placeholder.SetValue(null, -1);
                }
                RepaintDestination();
                _dropInfo = null;
                _dropContainer = null;
                // A successful dock/float owns the candidate from here on.
                if (_candidate != null && Parent.GetValue(_candidate) == null)
                    UnityEngine.Object.DestroyImmediate(_candidate);
                _candidate = null;
                _sourceWindow = null;
                _completing = false;
            }
            finally
            {
                _clearing = false;
            }
        }

        private static Vector2 ToScreen(Vector2 panelPoint) =>
            ((Rect)ScreenPosition.GetValue(Parent.GetValue(_sourceWindow))).position + panelPoint;

        private static void OnPointerMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != PointerId.mousePointerId || _capture == null) return;
            evt.StopImmediatePropagation();
            // Unity clears cached buttons on MouseLeaveWindow, even while the
            // native capture is still delivering held MouseDrag events.
            var held = (evt.pressedButtons & 1) != 0
                || (evt.imguiEvent?.rawType == EventType.MouseDrag && evt.imguiEvent.button == 0);
            if (!IsActive || !held) { Clear(); return; }
            try { UpdateDestination(ToScreen(evt.position)); }
            catch (Exception exception) when (IsReflectionFailure(exception))
            {
                ReportFailure(exception.Message);
                Clear();
            }
        }

        private static void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != PointerId.mousePointerId || evt.button != 0 || _capture == null) return;
            // Content handlers may synchronously Clear the gesture. Consume
            // this release before dispatch so it can never also float a pane.
            evt.StopImmediatePropagation();
            try
            {
                if (!IsActive) return;
                var point = ToScreen(evt.position);
                FindDestination(point, out var local, out var info, out var container);
                if (local != null)
                {
                    RelayContent(local, point - ((Rect)ScreenPosition.GetValue(Parent.GetValue(local))).position, true);
                    return;
                }

                ClearContentHover();
                // Native AddTab detaches the old active root before attaching
                // the candidate. This is completion, not source cancellation.
                _completing = true;
                if (DropTarget.GetValue(info) != null)
                {
                    if (!Dock(_candidate, info, point))
                    {
                        CloseRejectedPane(_candidate);
                        ReportFailure("Unity rejected the window docking destination.");
                    }
                }
                else
                {
                    var position = new Rect(point - _candidate.position.size * .5f, _candidate.position.size);
                    _candidate.position = position;
                    _candidate.Show();
                    _candidate.position = (Rect)FitRectToMouseScreen.Invoke(null, new object[]
                    {
                        position, true, ViewWindow.GetValue(Parent.GetValue(_candidate)),
                    });
                    _candidate.Focus();
                }
            }
            catch (Exception exception) when (IsReflectionFailure(exception))
            {
                CloseRejectedPane(_candidate);
                ReportFailure(exception.Message);
            }
            finally
            {
                _completing = false;
                Clear();
            }
        }

        private static void UpdateDestination(Vector2 point)
        {
            FindDestination(point, out var local, out var info, out var container);
            if (local != _hoverWindow) ClearContentHover();
            RepaintDestination();
            if (local != null)
            {
                // Content moves and split drops keep their own feedback. A
                // docking preview exists only while outside our content.
                NativeDropInfo.SetValue(null, null);
                Placeholder.SetValue(null, -1);
                _dropInfo = null;
                _dropContainer = null;
                if (_preview != null) PreviewClose.Invoke(_preview, null);
                _preview = null;
                var panelPoint = point - ((Rect)ScreenPosition.GetValue(Parent.GetValue(local))).position;
                var host = AssetDragPrepareInteraction.FindThemedHost(local.rootVisualElement);
                if (_contentPreview == null && host != null)
                {
                    _contentPreview = new AssetDragPrepareInteraction.ThemedDragSession(
                        host, _objects, _paths, panelPoint, () => _contentPreview = null,
                        _splitPayload?.DisplayName ?? _candidate.titleContent.text, _contentIcon);
                }
                _contentPreview?.UpdatePosition(panelPoint);
                RelayContent(local, panelPoint, false);
                return;
            }

            if (_preview == null)
            {
                _preview = (UnityEngine.Object)PreviewInstance.GetValue(null);
                PreviewShow.Invoke(_preview, new object[]
                {
                    _candidate.position, _candidate.titleContent, _candidate.position.size, point,
                });
            }

            _dropInfo = info;
            _dropContainer = container;
            NativeDropInfo.SetValue(null, info);
            if (DropTarget.GetValue(info) == null) Placeholder.SetValue(null, -1);
            var args = PreviewSetDropInfo.GetParameters().Length == 3
                ? new[] { info, (object)point, container }
                : new[] { info, (object)point };
            PreviewSetDropInfo.Invoke(_preview, args);
        }

        private static void FindDestination(Vector2 point, out EditorWindow local, out object info, out object container)
        {
            local = null;
            info = null;
            container = null;
            var ignored = IgnoreView.GetValue(null);
            try
            {
                IgnoreView.SetValue(null, null);
                var windows = (Array)Windows.GetValue(null);
                var previewWindow = _preview != null ? ViewWindow.GetValue(_preview) : null;
                // Our visible content keeps split/move priority even when a
                // nearby floating window's outer docking zone overlaps it.
                // Only the first containing native window is actually visible.
                foreach (var candidate in windows)
                {
                    if (ReferenceEquals(candidate, previewWindow)
                        || !((Rect)WindowPosition.GetValue(candidate)).Contains(point)) continue;
                    var root = RootView.GetValue(candidate);
                    if (root == null) break;
                    foreach (var view in (Array)Children.GetValue(root))
                    {
                        if (!DockArea.IsInstanceOfType(view)) continue;
                        var window = ActualView.GetValue(view) as EditorWindow;
                        if (!(window is ZepWindow) && !(window is ZepPaneWindow)) continue;
                        if (window.rootVisualElement.panel == null) continue;
                        var panelPoint = point - ((Rect)ScreenPosition.GetValue(view)).position;
                        if (!window.rootVisualElement.worldBound.Contains(panelPoint)) continue;
                        local = window;
                        container = candidate;
                        info = FloatingDrop.Invoke(new object[] { null });
                        return;
                    }
                    break;
                }

                // Native root zones extend beyond each window rectangle. Use
                // Unity's front-to-back search for every other destination.
                foreach (var candidate in windows)
                {
                    if (ReferenceEquals(candidate, previewWindow)) continue;
                    var root = RootView.GetValue(candidate);
                    if (root == null) continue;
                    var split = RootSplitView.GetValue(candidate);
                    if (split != null) info = DragOverRoot.Invoke(split, new object[] { point });
                    if (info == null)
                    {
                        foreach (var view in (Array)Children.GetValue(root))
                        {
                            if (!DropArea.IsInstanceOfType(view)) continue;
                            info = DragOver.Invoke(view, new object[] { _candidate, point });
                            if (info != null) break;
                        }
                    }
                    if (info != null) { container = candidate; break; }
                }
            }
            finally { IgnoreView.SetValue(null, ignored); }
            if (info == null) info = FloatingDrop.Invoke(new object[] { null });
        }

        private static void RelayContent(EditorWindow window, Vector2 panelPoint, bool perform)
        {
            if (_hoverWindow != null && _hoverWindow != window) ClearContentHover();
            var root = window.rootVisualElement;
            var target = root.panel?.Pick(panelPoint) ?? root;
            UpdateContentHover(target, panelPoint);
            _hoverWindow = window;
            var nativeEvent = new Event
            {
                type = perform ? EventType.DragPerform : EventType.DragUpdated,
                mousePosition = panelPoint,
            };
            // A relay runs inside the captured pointer event. Queued dispatch
            // would run after Clear has removed the payload the receiver needs.
            if (perform)
            {
                using var evt = DragPerformEvent.GetPooled(nativeEvent);
                evt.target = target;
                SendEvent.Invoke(target, new[] { (object)evt, Immediate });
            }
            else
            {
                // This is a content relay, not a new physical press. The
                // derived pool method would press Unity's global mouse button,
                // including when hover cleanup runs after the real MouseUp.
                using var evt = MouseEventBase<DragUpdatedEvent>.GetPooled(nativeEvent);
                evt.target = target;
                SendEvent.Invoke(target, new[] { (object)evt, Immediate });
            }
        }

        private static void ClearContentHover()
        {
            _contentPreview?.Dispose();
            _contentPreview = null;
            _hoverWindow = null;
            UpdateContentHover(null, new Vector2(-10000f, -10000f));
        }

        private static void UpdateContentHover(VisualElement target, Vector2 panelPoint)
        {
            // Captured pointer events update Unity's hover tracker before our
            // relayed drag events, so it cannot reliably synthesize DragLeave.
            // Retain the ancestry too: virtualization can detach the old row
            // before its scroll view receives the next hover or completion.
            var previous = _hoverPath;
            var next = new List<VisualElement>();
            for (var element = target; element != null; element = element.parent)
                next.Add(element);
            _hoverPath = next;

            var nativeEvent = new Event { type = EventType.DragUpdated, mousePosition = panelPoint };
            foreach (var element in previous)
            {
                if (next.Contains(element) || element.panel == null) continue;
                // DragLeave does not bubble; every exited ancestor owns its
                // own teardown. This does not release the captured pointer.
                using var evt = MouseEventBase<DragLeaveEvent>.GetPooled(nativeEvent);
                evt.target = element;
                SendEvent.Invoke(element, new[] { (object)evt, Immediate });
            }
        }

        private static bool Dock(EditorWindow created, object info, Vector2 point)
        {
            var previous = OriginalSource.GetValue(null);
            try
            {
                // Removing the unhosted candidate from the source DockArea is
                // a no-op; native PerformDrop then attaches it at the target.
                OriginalSource.SetValue(null, Parent.GetValue(_sourceWindow));
                if (!(bool)PerformDrop.Invoke(DropTarget.GetValue(info), new object[] { created, info, point }))
                    return false;
                created.Focus();
                return true;
            }
            finally { OriginalSource.SetValue(null, previous); }
        }

        private static void CloseRejectedPane(EditorWindow created)
        {
            if (created == null) return;
            if (Parent.GetValue(created) == null) UnityEngine.Object.DestroyImmediate(created);
            else created.Close();
        }

        private static void RepaintDestination()
        {
            if (_dropContainer == null) return;
            var root = RootView.GetValue(_dropContainer);
            if (root == null) return;
            foreach (var view in (Array)Children.GetValue(root))
                if (DockArea.IsInstanceOfType(view)) (ActualView.GetValue(view) as EditorWindow)?.Repaint();
        }

        private static void OnUpdate()
        {
            if (!IsActive || _sourceWindow == null || _capture?.panel == null
                || (!ReferenceEquals(_preview, null) && _preview == null)) Clear();
        }
        private static void OnPointerCancel(PointerCancelEvent evt)
        {
            if (evt.pointerId == PointerId.mousePointerId) Clear();
        }
        private static void OnCaptureOut(PointerCaptureOutEvent evt)
        {
            if (evt.pointerId == PointerId.mousePointerId && !_completing && _capture != null
                && !_capture.HasPointerCapture(PointerId.mousePointerId)) Clear();
        }
        private static void OnSourceDetached(DetachFromPanelEvent evt)
        {
            if (!_completing && evt.target == _sourceWindow?.rootVisualElement) Clear();
        }
        private static void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            evt.StopImmediatePropagation();
            Clear();
        }
        private static bool IsReflectionFailure(Exception exception) => exception is TargetInvocationException
            || exception is MemberAccessException || exception is ArgumentException;
        private static void ReportFailure(string message)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            Debug.LogWarning($"[Zep] Could not dock this folder or tag: {message}");
        }
    }
}
