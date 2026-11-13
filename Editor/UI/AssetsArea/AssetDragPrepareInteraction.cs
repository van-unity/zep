using System;
using System.Collections.Generic;
using Zep.Editor.Bridge.FileOperations;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea.Substrate;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Self-contained "pending drag" controller. Owns the FSM that bridges a
    // single mouse-down on an asset row to a drag-with-themed-overlay session —
    // and, since the widgets run no native selection, it is also the only
    // click-to-select path for both view modes.
    //
    // The composing feature wires it as:
    //   - construction (registers its own capture-phase handlers on the collection)
    //   - OnSurfaceMouseMove / OnSurfaceMouseUp from its asset-surface handlers
    //   - Cancel() from the right-click bubble
    //
    // A single folder uses the native window drag, which retains its asset
    // payload for moves inside our panels. Other assets use Unity's OS drag
    // bus for Scene / Hierarchy / Inspector / Finder drops, with a themed
    // overlay while the cursor is inside our panel.
    internal sealed class AssetDragPrepareInteraction
    {
        // Pixel threshold from mouse-down before we promote a click into a drag.
        private const float DragStartDistance = 6f;

        private readonly AssetCollectionView _collection;
        private readonly VisualElement _assetSurface;
        private readonly bool _allowDragStart;
        private readonly Func<string, bool> _isFolderPath;

        private bool _isPending;
        private bool _capturedFolderPress;
        private bool _shouldDeferSelection;
        private Vector2 _startPosition;
        private string _anchorPath = string.Empty;
        // Non-null while the pending press started on a sub-asset row. The
        // gesture resolves exactly like a main row's — click commits the
        // sub selection on mouse-up, threshold-crossing starts a drag — but
        // the payload is the sub-OBJECT (no path: sub-assets have no file).
        private SubAssetDescriptor _pendingSubAsset;
        private readonly List<string> _selectionBeforePending = new List<string>();
        private readonly List<string> _selectionForClick = new List<string>();

        // Non-null while a themed-drag overlay is on screen. Owns its event
        // subscriptions and the overlay VisualElement; Dispose tears both down.
        private ThemedDragSession _activeSession;

        internal AssetDragPrepareInteraction(
            AssetCollectionView collection,
            VisualElement assetSurface,
            bool allowDragStart = true,
            Func<string, bool> isFolderPath = null)
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
            _assetSurface = assetSurface ?? throw new ArgumentNullException(nameof(assetSurface));
            _allowDragStart = allowDragStart;
            _isFolderPath = isFolderPath;

            // Everything registers on the collection root in the capture
            // phase, so events are seen before either inner view's own
            // handlers regardless of which view is realized:
            //   - Pointer-down opens the deferred-emission window before any
            //     selection change can be raised; pointer-up resolves a
            //     non-drag gesture by flushing the buffered selection.
            //   - Mouse-down drives the click-vs-drag FSM; the entry guard
            //     (TryGetAssetEntryFromTarget) rejects clicks that didn't land
            //     on an item, so header/empty-space clicks fall through.
            _collection.RegisterCallback<PointerDownEvent>(OnCollectionPointerDown, TrickleDown.TrickleDown);
            _collection.RegisterCallback<PointerUpEvent>(OnCollectionPointerUp, TrickleDown.TrickleDown);
            _collection.RegisterCallback<MouseDownEvent>(OnAssetMouseDown, TrickleDown.TrickleDown);
        }

        internal bool OnSurfaceMouseMove(MouseMoveEvent evt)
        {
            if (_activeSession != null) return false;
            if (!_isPending) return false;

            // Window leave clears Unity's cached buttons even during capture.
            // A native left MouseDrag still proves that the press is held.
            var nativeDrag = evt.imguiEvent?.rawType == EventType.MouseDrag && evt.imguiEvent.button == 0;
            if ((evt.pressedButtons & 1) == 0 && !nativeDrag)
            {
                if (_pendingSubAsset != null)
                {
                    _collection.CommitSubAssetSelection(_pendingSubAsset);
                }
                else if (_shouldDeferSelection)
                {
                    CommitPendingClickSelection();
                }
                _collection.FlushDeferredSelectionEmission();
                ClearPending();
                return false;
            }

            var dragDelta = evt.mousePosition - _startPosition;
            if (dragDelta.sqrMagnitude < DragStartDistance * DragStartDistance) return false;

            if (!_allowDragStart)
            {
                return false;
            }

            bool started;
            if (_pendingSubAsset != null)
            {
                started = TryStartSubAssetDrag(_pendingSubAsset, evt.mousePosition);
                // The press became a drag (or failed to resolve) — either way
                // it is not a sub-asset selection; roll back the mouse-down
                // preview highlight.
                _collection.PreviewSubAssetSelection(null);
            }
            else
            {
                started = TryStartThemedDrag(evt.mousePosition);
            }

            ClearPending();
            return started;
        }

        internal void OnSurfaceMouseUp(MouseUpEvent evt)
        {
            if (evt.button != 0) return;
            if (_activeSession != null) return;

            if (_isPending && _pendingSubAsset != null)
            {
                _collection.CommitSubAssetSelection(_pendingSubAsset);
            }
            else if (_isPending && _shouldDeferSelection)
            {
                CommitPendingClickSelection();
            }

            // Folder presses capture this surface, so PointerUp skips the
            // collection's usual flush callback. A click must still publish
            // its selection once; a promoted drag already discarded it.
            if (_isPending) _collection.FlushDeferredSelectionEmission();
            ClearPending();
        }

        internal void Cancel()
        {
            if (_activeSession != null)
            {
                _activeSession.Dispose();
                _activeSession = null;
            }
            if (_pendingSubAsset != null)
            {
                // The pending sub press never resolved into a commit; drop its
                // mouse-down preview highlight along with it.
                _collection.PreviewSubAssetSelection(null);
            }
            _collection.DiscardDeferredSelectionEmission();
            ClearPending();
        }

        // -----------------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------------

        // Opens the deferred-emission window so a selection change raised by this
        // press (Unity's ListView in list mode, our own commit in grid mode) is
        // buffered rather than mirrored into Unity's global Selection until the
        // gesture resolves. Runs in the capture phase before either of those.
        private void OnCollectionPointerDown(PointerDownEvent evt)
        {
            // A fresh press supersedes any window left open by a prior gesture
            // (dropped, never emitted late).
            _collection.DiscardDeferredSelectionEmission();

            if (evt.button != 0) return;

            var target = evt.target as VisualElement;
            // The expansion chevron sits inside a row, so the entry probe below
            // accepts it — but it is a Button, and Unity's Clickable captures the
            // pointer, so the matching PointerUp is routed straight to it and
            // never trickles back down through this collection. Opening the
            // window here would leave it open forever: every later
            // RaiseSelectionChanged (keyboard moves, type-ahead, select-all)
            // would be buffered and silently dropped. Same guard, same reason as
            // OnAssetMouseDown's.
            if (_collection.IsSubAssetDisclosureTarget(target)) return;
            if (!_collection.TryGetAssetEntryFromTarget(target, out _)) return;

            _collection.BeginDeferredSelectionEmission();
        }

        // Pointer-up without a drag means the press was a click: release the
        // buffered selection to the global mirror. If the press became a drag,
        // TryStartThemedDrag already discarded the buffer, so this no-ops.
        private void OnCollectionPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0) return;
            _collection.FlushDeferredSelectionEmission();
        }

        private void OnAssetMouseDown(MouseDownEvent evt)
        {
            // A new press can only arrive when no drag is in flight — Unity captures
            // the pointer for the whole drag. So a session still alive here never got
            // its teardown signal: the drag ended in another window (e.g. a sprite
            // dropped onto an Inspector ObjectField), so the DragExited/MouseUp/
            // DragPerform callbacks — all registered on our panel host — never fired.
            // Left set, _activeSession makes OnSurfaceMouseMove bail at its
            // `_activeSession != null` guard: the "can't drag from the panel anymore
            // after dropping on the Inspector" bug. Dispose the stale session so the
            // next drag can start.
            if (_activeSession != null)
            {
                _activeSession.Dispose();
                _activeSession = null;
            }

            ClearPending();
            if (evt.button != 0) return;

            var target = evt.target as VisualElement;
            // The expansion chevron owns its click (toggle only) — selecting
            // or dragging the row from a chevron press would fight it.
            if (_collection.IsSubAssetDisclosureTarget(target))
            {
                return;
            }

            if (!_collection.TryGetAssetEntryFromTarget(target, out var assetEntry))
            {
                if (_collection.TryGetSubAssetFromTarget(target, out var subAsset))
                {
                    if (evt.clickCount == 1)
                    {
                        _isPending = true;
                        _pendingSubAsset = subAsset;
                        _startPosition = evt.mousePosition;
                        // Immediate row feedback; Unity's Selection follows on
                        // commit so a drag never retargets the Inspector.
                        _collection.PreviewSubAssetSelection(subAsset);
                    }
                    else if (evt.clickCount == 2)
                    {
                        _collection.CommitSubAssetSelection(subAsset);
                        _collection.RequestSubAssetOpen(subAsset);
                    }
                }

                return;
            }

            if (evt.clickCount == 1)
            {
                _collection.CaptureCurrentSelectionPaths(_selectionBeforePending);
                _shouldDeferSelection =
                    ShouldPreserveExistingSelectionForPossibleDrag(assetEntry.Path, evt.actionKey, evt.shiftKey);

                _selectionForClick.Clear();
                _selectionForClick.AddRange(_collection.ResolveClickSelection(assetEntry.Path, evt.actionKey, evt.shiftKey));

                _isPending = true;
                _startPosition = evt.mousePosition;
                _anchorPath = assetEntry.Path;

                // Non-deferred presses select immediately (both view modes —
                // this FSM is the only click-selection path now that the
                // ListView's native selection is disabled). Deferred presses
                // resolve on mouse-up.
                if (!_shouldDeferSelection)
                {
                    CommitPendingClickSelection();
                }

                // Keep the pending folder press routed here even when the
                // first movement crosses straight into another native window.
                // The docking gesture takes over capture at the threshold.
                var candidates = BuildDragCandidatePaths();
                if (_allowDragStart && candidates.Count == 1
                    && (_isFolderPath ?? AssetDatabase.IsValidFolder)(candidates[0]))
                {
                    _assetSurface.CaptureMouse();
                    _capturedFolderPress = true;
                }
            }
            else if (evt.clickCount == 2)
            {
                _selectionForClick.Clear();
                _selectionForClick.AddRange(_collection.ResolveClickSelection(assetEntry.Path, evt.actionKey, evt.shiftKey));
                CommitPendingClickSelection();
            }
        }

        private bool TryStartThemedDrag(Vector2 mousePosition)
        {
            var dragPaths = BuildDragCandidatePaths();
            if (dragPaths.Count == 0) return false;

            if (!TryBuildAssetDragPayload(dragPaths, out var objectReferences, out var pathArray, _isFolderPath))
            {
                return false;
            }

            // The gesture is now a drag, not a click: drop the buffered click
            // selection so Unity's global Selection (and the Inspector) keeps
            // pointing at whatever was selected before this asset was grabbed.
            // This is precisely what lets a sprite be dragged onto a
            // SpriteRenderer field without the panel stealing the Inspector.
            _collection.DiscardDeferredSelectionEmission();

            return StartDragSession(
                objectReferences,
                pathArray,
                pathArray.Length == 1
                    ? AssetDropOperations.GetPathName(pathArray[0])
                    : $"{pathArray.Length} Assets",
                mousePosition);
        }

        // A folder takes the native window gesture; other assets start an OS
        // drag and mount the themed overlay. OS StartDrag MUST happen in the
        // same event that crossed the drag threshold — a delayed handler (e.g. on
        // MouseLeaveWindow) silently no-ops and breaks cross-window / Finder
        // drops, so this runs inline from the mouse-move handler.
        private bool StartDragSession(
            UnityEngine.Object[] objectReferences,
            string[] paths,
            string label,
            Vector2 mousePosition)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objectReferences;
            DragAndDrop.paths = paths;
            if (paths.Length == 1 && (_isFolderPath ?? AssetDatabase.IsValidFolder)(paths[0])
                && ZepWindowDocking.Start(
                    ZepPaneSource.RootedFolder(AssetPath.From(paths[0])), label, _collection))
            {
                return true;
            }
            DragAndDrop.StartDrag(label);

            var themedHost = FindThemedHost(_collection.panel?.visualTree);
            if (themedHost == null)
            {
                // No themed host attached (tests, or a torn-down view). The
                // native drag is already running; just skip the overlay.
                return true;
            }

            _activeSession = new ThemedDragSession(
                themedHost: themedHost,
                objectReferences: objectReferences,
                paths: paths,
                startMousePosition: mousePosition,
                onFinished: () =>
                {
                    _activeSession = null;
                });
            return true;
        }

        internal static VisualElement FindThemedHost(VisualElement panelRoot)
        {
            if (panelRoot == null) return null;
            if (panelRoot.ClassListContains(ZepUiClasses.Root)) return panelRoot;
            return panelRoot.Q(className: ZepUiClasses.Root);
        }

        private List<string> BuildDragCandidatePaths()
        {
            var result = new List<string>();
            if (AssetPathClassification.ContainsAssetPath(_selectionBeforePending, _anchorPath))
            {
                result.AddRange(_selectionBeforePending);
            }
            else
            {
                result.AddRange(_selectionForClick);
            }

            if (result.Count == 0 && !string.IsNullOrWhiteSpace(_anchorPath))
            {
                result.Add(_anchorPath);
            }

            return result;
        }

        // isFolderPath: the owning pane's index-first folder test for the
        // dragged rows' own paths; null asks AssetDatabase per path.
        internal static bool TryBuildAssetDragPayload(
            IReadOnlyList<string> dragCandidatePaths,
            out UnityEngine.Object[] objectReferences,
            out string[] dragPaths,
            Func<string, bool> isFolderPath = null)
        {
            isFolderPath ??= AssetDatabase.IsValidFolder;
            var resolvedPaths = new List<string>();
            var resolvedObjects = new List<UnityEngine.Object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < dragCandidatePaths.Count; i++)
            {
                var normalized = AssetPathFormat.Normalize(dragCandidatePaths[i]);
                if (string.IsNullOrEmpty(normalized) || !AssetPathClassification.IsProjectAssetPath(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                var isFolder = isFolderPath(normalized);
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(normalized);
                if (mainAsset == null && !isFolder) continue;

                resolvedPaths.Add(normalized);
                if (mainAsset != null) resolvedObjects.Add(mainAsset);
            }

            dragPaths = resolvedPaths.ToArray();
            objectReferences = resolvedObjects.ToArray();
            return dragPaths.Length > 0;
        }

        // Drag one sub-asset OBJECT. No paths: a sub-asset has no file of its
        // own, so DragAndDrop.paths stays empty — internal folder-drop targets
        // (which move files) naturally ignore the drag, while object-reference
        // receivers (Inspector fields, the Scene view) accept the object.
        private bool TryStartSubAssetDrag(SubAssetDescriptor subAsset, Vector2 mousePosition)
        {
            var subObject = SubAssetCatalog.Resolve(subAsset);
            if (subObject == null) return false;

            return StartDragSession(
                new[] { subObject },
                Array.Empty<string>(),
                subAsset.Name,
                mousePosition);
        }

        private void CommitPendingClickSelection()
        {
            _collection.CommitClickSelection(_selectionForClick);
        }

        private bool ShouldPreserveExistingSelectionForPossibleDrag(
            string clickedPath, bool toggle, bool range)
        {
            return !string.IsNullOrWhiteSpace(clickedPath)
                && !toggle
                && !range
                && _collection.SelectedPaths.Count > 1
                && _collection.ContainsSelectedPath(clickedPath);
        }

        private void ClearPending()
        {
            _isPending = false;
            // The marquee captures the same surface; a right-click cancel
            // during its rubber band must not release that capture.
            if (_capturedFolderPress)
            {
                _capturedFolderPress = false;
                if (_assetSurface.HasMouseCapture()) _assetSurface.ReleaseMouse();
            }
            _shouldDeferSelection = false;
            _anchorPath = string.Empty;
            _pendingSubAsset = null;
            _selectionBeforePending.Clear();
            _selectionForClick.Clear();
        }

        // -----------------------------------------------------------------------
        // Themed content preview shared by Unity asset drags and captured
        // folder/tag drags. Native drag events move and dismiss it; the captured
        // gesture also updates and disposes it explicitly. Drop receivers keep
        // ownership of the payload and the operation.
        // -----------------------------------------------------------------------
        internal sealed class ThemedDragSession
        {
            private const float OverlayOffsetX = 14f;
            private const float OverlayOffsetY = 10f;

            private readonly VisualElement _themedHost;
            private readonly UnityEngine.Object[] _objectReferences;
            private readonly string[] _paths;
            private readonly Action _onFinished;

            private readonly VisualElement _overlay;
            private readonly Image _overlayIcon;
            private readonly Label _overlayLabel;

            // Host-local point the overlay's left/top were pinned to at drag
            // start. Every later move writes only its delta to `translate`.
            private readonly Vector2 _overlayAnchor;

            // Once DragAndDrop.StartDrag has fired, Unity captures the pointer
            // and stops dispatching PointerMove / MouseMove to UIToolkit
            // elements. The drag's continuous "cursor moved" signal arrives
            // as DragUpdatedEvent instead — that's what drives the overlay
            // position. DragEnter/DragLeave/MouseLeaveWindow toggle visibility
            // so the overlay disappears when the cursor leaves our window
            // (Unity's native ghost takes over there) and reappears when
            // it returns with the mouse button still down.
            private readonly EventCallback<DragUpdatedEvent> _onDragUpdated;
            private readonly EventCallback<DragEnterEvent> _onDragEnter;
            private readonly EventCallback<DragLeaveEvent> _onDragLeave;
            private readonly EventCallback<MouseLeaveWindowEvent> _onMouseLeaveWindow;
            private readonly EventCallback<PointerUpEvent> _onPointerUp;
            private readonly EventCallback<MouseUpEvent> _onMouseUp;
            private readonly EventCallback<DragExitedEvent> _onDragExited;
            private readonly EventCallback<DragPerformEvent> _onDragPerform;
            private readonly EventCallback<DetachFromPanelEvent> _onPanelDetach;

            private bool _disposed;

            internal ThemedDragSession(
                VisualElement themedHost,
                UnityEngine.Object[] objectReferences,
                string[] paths,
                Vector2 startMousePosition,
                Action onFinished,
                string displayName = null,
                Texture icon = null)
            {
                _themedHost = themedHost;
                _objectReferences = objectReferences;
                _paths = paths;
                _onFinished = onFinished;

                _overlay = BuildOverlay(out _overlayIcon, out _overlayLabel);
                PopulateOverlayContent(displayName, icon);
                _themedHost.Add(_overlay);

                // Anchor the ghost's box once, here; PositionOverlay moves it
                // from this point with `translate` for the rest of the drag.
                _overlayAnchor = ToHostLocal(startMousePosition);
                _overlay.style.left = _overlayAnchor.x;
                _overlay.style.top = _overlayAnchor.y;

                _onDragUpdated = OnDragUpdated;
                _onDragEnter = OnDragEnter;
                _onDragLeave = OnDragLeave;
                _onMouseLeaveWindow = OnMouseLeaveWindow;
                _onPointerUp = OnPointerUp;
                _onMouseUp = OnMouseUp;
                _onDragExited = OnDragExited;
                _onDragPerform = OnDragPerform;
                _onPanelDetach = OnPanelDetach;

                _themedHost.RegisterCallback(_onDragUpdated, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onDragEnter, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onDragLeave, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onMouseLeaveWindow);
                _themedHost.RegisterCallback(_onPointerUp, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onMouseUp, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onDragExited, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onDragPerform, TrickleDown.TrickleDown);
                _themedHost.RegisterCallback(_onPanelDetach);
            }

            internal void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                _themedHost.UnregisterCallback(_onDragUpdated, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onDragEnter, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onDragLeave, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onMouseLeaveWindow);
                _themedHost.UnregisterCallback(_onPointerUp, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onMouseUp, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onDragExited, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onDragPerform, TrickleDown.TrickleDown);
                _themedHost.UnregisterCallback(_onPanelDetach);

                if (_overlay.parent != null)
                {
                    _overlay.RemoveFromHierarchy();
                }

                _onFinished?.Invoke();
            }

            private void OnDragUpdated(DragUpdatedEvent evt)
            {
                UpdatePosition(evt.mousePosition);
            }

            internal void UpdatePosition(Vector2 panelPosition)
            {
                if (_disposed) return;
                SetOverlayVisible(true);
                PositionOverlay(panelPosition);
            }

            private void OnDragEnter(DragEnterEvent evt)
            {
                if (_disposed) return;
                if (!_themedHost.worldBound.Contains(evt.mousePosition)) return;
                SetOverlayVisible(true);
                PositionOverlay(evt.mousePosition);
            }

            private void OnDragLeave(DragLeaveEvent evt)
            {
                if (_disposed) return;
                // DragLeave fires for every descendant transition too — only
                // hide when the cursor has actually crossed outside the panel.
                if (!_themedHost.worldBound.Contains(evt.mousePosition))
                {
                    SetOverlayVisible(false);
                }
            }

            private void OnMouseLeaveWindow(MouseLeaveWindowEvent evt)
            {
                if (_disposed) return;
                SetOverlayVisible(false);
            }

            private void SetOverlayVisible(bool visible)
            {
                _overlay.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (_disposed) return;
                if (evt.button != 0) return;
                Dispose();
            }

            private void OnMouseUp(MouseUpEvent evt)
            {
                if (_disposed) return;
                if (evt.button != 0) return;
                Dispose();
            }

            private void OnDragPerform(DragPerformEvent evt)
            {
                if (_disposed) return;
                // The drop landed inside our window — Unity dispatched
                // DragPerformEvent to the target. Tear down the visual now;
                // the target's handler does the actual drop work.
                Dispose();
            }

            private void OnDragExited(DragExitedEvent evt)
            {
                // Fires when the drag ends — including when the user released
                // outside our window. Our only job is to clean up the visual.
                if (_disposed) return;
                Dispose();
            }

            private void OnPanelDetach(DetachFromPanelEvent evt)
            {
                Dispose();
            }

            private void PositionOverlay(Vector2 panelLocalMousePos)
            {
                // Move the ghost with `translate`, which composes with the
                // left/top anchored at drag start and is what the overlay's
                // DynamicTransform usage hint accelerates. Writing left/top per
                // DragUpdated would instead dirty layout under the panel root
                // on every pointer move.
                var target = ToHostLocal(panelLocalMousePos);
                _overlay.style.translate = new Translate(
                    target.x - _overlayAnchor.x,
                    target.y - _overlayAnchor.y);
            }

            // The overlay is parented to the themed host. Position is expressed
            // in the host's local coordinate space — convert panel-local
            // pointer coords by subtracting the host's world origin. (This
            // measures the HOST, not the overlay, so the overlay's own
            // translate never enters it: worldBound is in panel-absolute
            // coords; with no transforms above the host, panel-local == world.)
            private Vector2 ToHostLocal(Vector2 panelLocalMousePos)
            {
                var hostOrigin = _themedHost.worldBound.position;
                return new Vector2(
                    panelLocalMousePos.x - hostOrigin.x + OverlayOffsetX,
                    panelLocalMousePos.y - hostOrigin.y + OverlayOffsetY);
            }

            private void PopulateOverlayContent(string displayName, Texture icon)
            {
                icon ??= ResolveIcon();
                _overlayIcon.image = icon;
                _overlayIcon.style.display =
                    icon != null ? DisplayStyle.Flex : DisplayStyle.None;

                // Path-less drags (a single sub-asset object) label from the
                // object itself.
                _overlayLabel.text = displayName ?? (_paths.Length == 1
                    ? AssetDropOperations.GetPathName(_paths[0])
                    : _paths.Length == 0 && _objectReferences.Length > 0 && _objectReferences[0] != null
                        ? _objectReferences[0].name
                        : $"{_paths.Length} Assets");
            }

            private Texture ResolveIcon()
            {
                if (_objectReferences != null && _objectReferences.Length > 0 && _objectReferences[0] != null)
                {
                    var preview = AssetPreview.GetMiniThumbnail(_objectReferences[0]);
                    if (preview != null) return preview;
                }

                if (_paths.Length > 0)
                {
                    return AssetDatabase.GetCachedIcon(_paths[0]);
                }

                return null;
            }

            // Styling lives in the .zep-drag-overlay* rules of the shell sheet
            // (the overlay is parented to the panel root, so the sheet reaches
            // it) — including the icon's 16px box, which the sheet pins so the
            // ghost never shows Unity's full-size default texture. The only
            // inline writes on the overlay are the ones USS cannot express:
            // the runtime-computed left/top anchored once at drag start, the
            // per-move `translate` in PositionOverlay, and the
            // interaction-driven display toggles.
            private static VisualElement BuildOverlay(out Image icon, out Label label)
            {
                var overlay = new VisualElement { name = "zep-drag-overlay" };
                overlay.AddToClassList(ZepUiClasses.DragOverlay);
                overlay.pickingMode = PickingMode.Ignore;
                overlay.usageHints = UsageHints.DynamicTransform;

                icon = new Image { name = "zep-drag-overlay-icon" };
                icon.AddToClassList(ZepUiClasses.DragOverlayIcon);
                icon.pickingMode = PickingMode.Ignore;
                icon.scaleMode = ScaleMode.ScaleToFit;
                overlay.Add(icon);

                label = new Label { name = "zep-drag-overlay-label" };
                label.AddToClassList(ZepUiClasses.DragOverlayLabel);
                label.pickingMode = PickingMode.Ignore;
                overlay.Add(label);

                return overlay;
            }
        }
    }
}
