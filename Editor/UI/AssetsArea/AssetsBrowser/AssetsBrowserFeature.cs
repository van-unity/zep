using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Editor.Bridge.FileOperations;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea.AssetsBrowser
{
    // Folder browser: owns project/external drop targets and inline editing,
    // and composes the shared asset rendering and gesture substrate.
    internal sealed class AssetsBrowserFeature : VisualElement, IAssetsPaneFeature
    {
        // Composition
        private readonly AssetCollectionView _collection;
        private readonly AssetsAreaInlineEditController _inlineEditController;
        private readonly SurfaceGestureRouter _gestures;
        private readonly DragEdgeAutoScroll _dragAutoScroll;

        // Chrome
        private readonly VisualElement _assetSurface;

        // Canonical assets reference (without the inline-edit ghost). The
        // substrate stores the effective list (canonical + ghost when
        // inline-create is active); this widget keeps a reference to the
        // canonical list so the count chip is computed off the real folder
        // contents, not the ghost-augmented view.
        private IReadOnlyList<ZepAssetEntry> _canonicalAssets =
            Array.Empty<ZepAssetEntry>();

        // Inline-edit (new-entry ghost + in-place rename). At most one is active.
        private AssetsAreaInlineEditRequest _activeInlineEdit;
        private ZepAssetEntry _ghostEntry;
        // Guards re-entrant confirm/abort triggered by programmatic focus change
        // when SetDescriptor(None) hides the TextField during teardown.
        private bool _inlineEditFinalizing;
        // The ghost-augmented view of the canonical list; created with the ghost and
        // rebound (never rebuilt) as the canonical list changes, so the substrate's
        // reference dirty-check behaves exactly as it did with the scratch copy.
        private GhostPrefixAssetList _ghostView;

        // Pull-based asset source. The shell wires this to the BrowseProjection
        // at construction; RefreshAssets() pulls on each refresh tick. Tests
        // leave it null and push assets via SetAssets directly.
        private readonly Func<IReadOnlyList<ZepAssetEntry>> _assetSource;
        // Index-first folder test for the paths this feature's own rows carry;
        // the AssetDatabase default only serves a feature built without a pane.
        private readonly Func<string, bool> _isFolderPath;

        // Memo of the last hovered element's drop-target verdict; see
        // ResolveFolderDropTarget. Transient per-hover state, dropped with the
        // highlight when the drag leaves or performs.
        private string _dropTargetVerdictHoveredPath;
        private string _dropTargetVerdict;
        private int _dropTargetVerdictPayloadGeneration;
        private IReadOnlyList<string> _dropTargetVerdictPaths;

        // Test-only: how often the hover path actually resolved a drop target
        // instead of reusing the memo above.
        internal static int DropTargetEvaluationsForTests;

        private enum AssetPaneLayoutTier { Tight = 0, Narrow = 1, Compact = 2, Standard = 3 }

        private AssetPaneLayoutTier _assetPaneLayoutTier = AssetPaneLayoutTier.Standard;

        // Events re-exposed to the controller / shell.
        public event Action<IReadOnlyList<ZepAssetEntry>> SelectionChanged
        {
            add => _collection.SelectionChanged += value;
            remove => _collection.SelectionChanged -= value;
        }
        public event Action OpenRequested
        {
            add => _collection.OpenRequested += value;
            remove => _collection.OpenRequested -= value;
        }

        public event Action<SubAssetDescriptor> SubAssetSelected
        {
            add => _collection.SubAssetSelected += value;
            remove => _collection.SubAssetSelected -= value;
        }

        public event Action<SubAssetDescriptor> SubAssetOpenRequested
        {
            add => _collection.SubAssetOpenRequested += value;
            remove => _collection.SubAssetOpenRequested -= value;
        }

        // List-mode folder chevron clicks; the pane view routes them into the
        // pane state's expansion set.
        internal event Action<string> FolderExpansionToggled
        {
            add => _collection.FolderExpansionToggled += value;
            remove => _collection.FolderExpansionToggled -= value;
        }
        public event Action<Rect, ZepAssetEntry> ContextMenuRequested;

        // Typed drop events raised to Shell.
        internal event Action<string, IReadOnlyList<string>> FolderAssetDropReceived;
        internal event Action<string, IReadOnlyList<string>> FolderAssetExternalImportReceived;
        internal event Action<IReadOnlyList<string>> CurrentFolderDropReceived;
        internal event Action<IReadOnlyList<string>> CurrentFolderExternalImportReceived;
        // string arg = explicit destination folder when the drop landed on a folder row,
        // else null (handler falls back to the current/selected folder).
        internal event Action<IReadOnlyList<GameObject>, string> SceneObjectDropReceived;

        internal AssetsBrowserFeature(
            UnityZepIconRepository iconRepository,
            AssetViewMode initialViewMode,
            int initialGridItemSize,
            Func<IReadOnlyList<ZepAssetEntry>> assetSource = null,
            Func<string, string, AssetPresentationData> presentationLookup = null,
            Func<string, IReadOnlyList<ZepAssetEntry>> folderContentsLookup = null,
            Func<int> folderContentsVersion = null,
            Func<string, bool> isFolderPath = null)
        {
            if (iconRepository == null) throw new ArgumentNullException(nameof(iconRepository));
            _assetSource = assetSource;
            _isFolderPath = isFolderPath ?? AssetDatabase.IsValidFolder;

            // NOTE: distinct element name from the legacy widget so Q-by-name
            // lookups in tests/UI Debug aren't ambiguous while both classes
            // coexist (Step 7 deletes the legacy widget).
            name = "zep-browser-feature";
            AddToClassList(ZepUiClasses.MainPane);

            // Pre-set visual mode as soon as an external OS file drag enters the
            // asset pane. Without this, some Unity versions don't dispatch
            // DragUpdated to child elements before the first frame can set
            // visualMode.
            RegisterCallback<DragEnterEvent>(OnDragEnter);
            // Neither the drop highlight nor edge scrolling may outlive hover.
            RegisterCallback<DragLeaveEvent>(_ => OnDragHoverEnded());
            RegisterCallback<DragExitedEvent>(_ => OnDragHoverEnded());

            // ---- Asset surface ----
            _assetSurface = new VisualElement { name = "zep-browser-surface" };
            _assetSurface.AddToClassList(ZepUiClasses.AssetSurface);
            // Bubble-phase drag fallbacks: fire when the drag target is empty
            // surface space (not inside the ListView/GridView bounds). The
            // inner-view TrickleDown handlers stop propagation for drops over
            // list/grid items, so these only fire for the uncovered area.
            _assetSurface.RegisterCallback<DragUpdatedEvent>(OnViewDragUpdated);
            _assetSurface.RegisterCallback<DragPerformEvent>(OnViewDragPerformed);

            // Context gestures are intercepted here in TrickleDown, before any
            // view-specific handler can eat them. The mouse-down/move/up/
            // capture-out quartet belongs to SurfaceGestureRouter, registered
            // on this same element once the collection exists.
            _assetSurface.RegisterCallback<PointerDownEvent>(OnAssetSurfacePointerDown, TrickleDown.TrickleDown);

            // ---- Inline-edit controller ----
            _inlineEditController = new AssetsAreaInlineEditController();
            _inlineEditController.Confirmed += OnInlineEditConfirmed;
            _inlineEditController.Aborted   += OnInlineEditAborted;

            // ---- Substrate ----
            _collection = new AssetCollectionView(
                iconRepository,
                _inlineEditController,
                initialViewMode,
                initialGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize,
                presentationLookup,
                folderContentsLookup,
                folderContentsVersion);
            _collection.DragUpdated          += OnViewDragUpdated;
            _collection.DragPerformed        += OnViewDragPerformed;
            _assetSurface.Add(_collection);

            Add(_assetSurface);

            // ---- Surface gestures: marquee + drag-prepare (the latter also
            // owns click-to-select) ----
            _gestures = new SurfaceGestureRouter(_assetSurface, _collection, isFolderPath: _isFolderPath);

            // ---- Drag-hover edge auto-scroll ----
            _dragAutoScroll = new DragEdgeAutoScroll(
                this,
                _collection.ResolveActiveScrollView,
                RefreshDropTargetAfterAutoScroll);
        }

        // -----------------------------------------------------------------------
        // Setters called by AssetsAreaController
        // -----------------------------------------------------------------------

        internal void SetAssets(IReadOnlyList<ZepAssetEntry> assets)
        {
            _canonicalAssets = assets ?? Array.Empty<ZepAssetEntry>();
            // With a ghost row active, BuildEffectiveAssetList returns the reused
            // ghost-view facade — a STABLE reference whose contents change every
            // call. AssetCollectionView's ReferenceEquals dirty-check can't detect that, so a
            // folder refresh arriving mid inline-edit would be silently dropped. Force the
            // rebuild (the inline begin/abort paths already do this for their own pushes).
            if (_ghostEntry != null)
            {
                _collection.MarkBothViewsDirty();
            }
            _collection.SetAssets(BuildEffectiveAssetList());
        }

        // Pull from the wired asset source and store. No-op when no source
        // is wired (tests push directly via SetAssets).
        public void RefreshAssets()
        {
            if (_assetSource == null) return;
            SetAssets(_assetSource());
        }

        // Read-only view of the canonical (non-ghost) asset list. The shell
        // queries this to drive the type-filter dropdown's count fallback and
        // ShellViewModel's GetSelectedAssetEntries.
        public IReadOnlyList<ZepAssetEntry> CurrentAssets => _canonicalAssets;

        public IReadOnlyList<ZepAssetEntry> VisibleAssets => _collection.VisibleAssets;

        public void SetSelection(IReadOnlyCollection<AssetPath> selectedAssetPaths)
        {
            _collection.SetSelection(selectedAssetPaths);
        }

        public void SelectAllAssets() => _collection.SelectAllAssets();

        public bool MoveSelection(Substrate.AssetSelectionMove move, bool extend)
            => _collection.MoveSelection(move, extend);

        public bool SelectFirstNameMatch(string prefix)
            => _collection.SelectFirstNameMatch(prefix);

        // Pane-state push: which folders render inline-expanded in list mode.
        // Enabled is false while a search/type/tag filter is active so those
        // projections stay flat.
        internal void SetFolderExpansion(bool enabled, IReadOnlyCollection<AssetPath> expandedFolders)
            => _collection.SetFolderExpansion(enabled, expandedFolders);

        // Select a path in the current visible order through the normal commit
        // pipeline (pivot, SelectionChanged, Unity Selection mirror).
        internal bool TrySelectAssetPath(string path)
            => _collection.TrySelectAssetPath(path);

        internal void ScrollPathIntoView(string assetPath)
        {
            _collection.ScrollPathIntoView(assetPath);
        }

        internal void PingPath(string assetPath)
        {
            _collection.PingPath(assetPath);
        }

        internal void PingSubAsset(string parentPath, long subAssetLocalId)
        {
            _collection.PingSubAsset(parentPath, subAssetLocalId);
        }

        // No-op here: the browser has no empty-state copy for the flag to
        // switch. The "This folder is empty." / "No files match the current
        // filter." label was removed on 2026-07-29 — the bottom bar's file
        // count already reports both states, and the label was the only thing
        // this flag ever drove. Tags still carry their own copy, so
        // the member stays on IAssetsPaneFeature.
        public void SetHasActiveFilter(bool hasActiveFilter)
        {
        }

        public void SetViewMode(AssetViewMode mode)
        {
            _collection.SetViewMode(mode);
        }

        internal AssetViewMode CurrentViewMode => _collection.CurrentViewMode;
        internal int CurrentGridItemSize => _collection.CurrentGridItemSize;

        public void SetGridItemSize(int size)
        {
            var clamped = Mathf.Clamp(size, AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize);
            if (_collection.CurrentGridItemSize == clamped) return;

            _collection.SetGridItemSize(clamped, AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize);
            ApplyResponsiveItemHeights();
        }

        public void ApplyResponsiveWidth(float assetPaneWidth)
        {
            if (assetPaneWidth <= 0f) return;

            _assetPaneLayoutTier =
                  assetPaneWidth <= AssetsAreaConstants.AssetPaneTightBreakpoint   ? AssetPaneLayoutTier.Tight
                : assetPaneWidth <= AssetsAreaConstants.AssetPaneNarrowBreakpoint  ? AssetPaneLayoutTier.Narrow
                : assetPaneWidth <= AssetsAreaConstants.AssetPaneCompactBreakpoint ? AssetPaneLayoutTier.Compact
                : AssetPaneLayoutTier.Standard;

            EnableInClassList(ZepUiClasses.LayoutAssetCompact, _assetPaneLayoutTier <= AssetPaneLayoutTier.Compact);
            EnableInClassList(ZepUiClasses.LayoutAssetNarrow,  _assetPaneLayoutTier <= AssetPaneLayoutTier.Narrow);
            EnableInClassList(ZepUiClasses.LayoutAssetTight,   _assetPaneLayoutTier == AssetPaneLayoutTier.Tight);
        }

        public void ApplyResponsiveItemHeights()
        {
            _collection.ApplyResponsiveItemHeights(
                _collection.CurrentGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);
        }

        public void NotifyIconPreviewsReady(IReadOnlyList<string> paths)
            => _collection.NotifyIconPreviewsReady(paths);

        internal bool TryGetAssetEntryFromTarget(VisualElement target, out ZepAssetEntry assetEntry)
            => _collection.TryGetAssetEntryFromTarget(target, out assetEntry);

        // -----------------------------------------------------------------------
        // Inline edit (new-entry ghost + in-place rename)
        // -----------------------------------------------------------------------

        internal bool HasActiveInlineEdit => _activeInlineEdit != null;

        internal void BeginInlineEdit(AssetsAreaInlineEditRequest request)
        {
            if (request == null) return;

            // If an edit is already in progress (user triggered another
            // Create/Rename without committing the first), treat the previous
            // edit as cancelled so its OnCancel callback still runs before we
            // start the new one.
            if (_activeInlineEdit != null)
            {
                var prior = _activeInlineEdit;
                _activeInlineEdit = null;
                _ghostEntry = null;
                prior.OnCancel?.Invoke();
            }

            _activeInlineEdit = request;
            _ghostEntry = request.IsGhost ? BuildGhostEntry(request) : null;

            var descriptor = new AssetsAreaInlineEditDescriptor(
                request.Path ?? string.Empty,
                request.InitialBasename ?? string.Empty,
                request.Extension ?? string.Empty,
                request.IsGhost);

            _inlineEditController.SetDescriptor(descriptor);

            // Re-push the effective list (now with ghost) and force the views
            // to rebuild even if the substrate's signature is unchanged.
            _collection.MarkBothViewsDirty();
            _collection.SetAssets(BuildEffectiveAssetList());
            _collection.ScrollPathIntoView(descriptor.Path);
        }

        internal void CancelInlineEdit()
        {
            var request = _activeInlineEdit;
            if (request == null) return;

            ClearInlineEditState();
            request.OnCancel?.Invoke();
        }

        public void CancelInteractions()
        {
            CancelInlineEdit();
            _gestures.Cancel();
        }

        private void OnInlineEditConfirmed(string text)
        {
            var request = _activeInlineEdit;
            if (request == null || _inlineEditFinalizing) return;

            _inlineEditFinalizing = true;
            try
            {
                ClearInlineEditState();
                request.OnCommit?.Invoke(text ?? string.Empty);
            }
            finally
            {
                _inlineEditFinalizing = false;
            }
        }

        private void OnInlineEditAborted()
        {
            var request = _activeInlineEdit;
            if (request == null || _inlineEditFinalizing) return;

            _inlineEditFinalizing = true;
            try
            {
                ClearInlineEditState();
                request.OnCancel?.Invoke();
            }
            finally
            {
                _inlineEditFinalizing = false;
            }
        }

        private void ClearInlineEditState()
        {
            _activeInlineEdit = null;
            _ghostEntry = null;

            _inlineEditController.SetDescriptor(AssetsAreaInlineEditDescriptor.None);

            _collection.MarkBothViewsDirty();
            _collection.SetAssets(BuildEffectiveAssetList());
        }

        private static ZepAssetEntry BuildGhostEntry(AssetsAreaInlineEditRequest request)
        {
            var basename = request.InitialBasename ?? string.Empty;
            var extension = request.Extension ?? string.Empty;
            var displayName = basename + extension;

            return new ZepAssetEntry(
                name: displayName,
                path: AssetsAreaInlineEditRequest.GhostPath,
                typeName: request.TypeName ?? string.Empty,
                folderTintColor: request.FolderTintColor);
        }

        // Returns the list pushed to the substrate — canonical plus a ghost
        // entry at the top when an inline-create edit is active. The ghost view
        // is a facade, not a copy: the canonical list can be a LazyAssetEntryList
        // (a search projection), and copying it read — and so materialised —
        // every entry on every publish while the edit field was open.
        private IReadOnlyList<ZepAssetEntry> BuildEffectiveAssetList()
        {
            if (_ghostEntry == null)
            {
                _ghostView = null;
                return _canonicalAssets;
            }

            if (_ghostView == null || !ReferenceEquals(_ghostView.Ghost, _ghostEntry))
            {
                _ghostView = new GhostPrefixAssetList(_ghostEntry, _canonicalAssets);
                return _ghostView;
            }

            _ghostView.Rebind(_canonicalAssets);
            return _ghostView;
        }

        // One ghost entry ahead of the canonical list, read through. Keeping the
        // instance stable across pushes preserves the substrate's ReferenceEquals
        // dirty-check semantics — the contents change behind a stable reference,
        // which is why SetAssets still forces a rebuild while a ghost is active.
        private sealed class GhostPrefixAssetList : IReadOnlyList<ZepAssetEntry>
        {
            private IReadOnlyList<ZepAssetEntry> _canonical;

            internal GhostPrefixAssetList(
                ZepAssetEntry ghost,
                IReadOnlyList<ZepAssetEntry> canonical)
            {
                Ghost = ghost;
                _canonical = canonical ?? Array.Empty<ZepAssetEntry>();
            }

            internal ZepAssetEntry Ghost { get; }

            internal void Rebind(IReadOnlyList<ZepAssetEntry> canonical)
            {
                _canonical = canonical ?? Array.Empty<ZepAssetEntry>();
            }

            public int Count => _canonical.Count + 1;

            public ZepAssetEntry this[int index] =>
                (index == 0) ? Ghost : _canonical[index - 1];

            public IEnumerator<ZepAssetEntry> GetEnumerator()
            {
                yield return Ghost;
                // Delegates to the canonical list's own enumerator, so a lazy
                // projection warns about being walked exactly as it would without
                // the ghost — the facade must not hide that.
                foreach (var entry in _canonical)
                {
                    yield return entry;
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => GetEnumerator();
        }

        // -----------------------------------------------------------------------
        // Substrate context-menu forwarding (with empty-surface fallback merged in)
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // Drag handling (project-asset, external-import, scene-object drops)
        // -----------------------------------------------------------------------

        private void OnDragEnter(DragEnterEvent evt)
        {
            if (EditorDragAndDropPaths.ReadExternalPaths().Count > 0)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            }
        }

        private void OnViewDragUpdated(DragUpdatedEvent evt)
        {
            // Split drags are position gestures aimed at the workspace's split
            // zones — scrolling the list under them would fight the aim. Every
            // other drag hover (including macOS Finder drags, whose paths stay
            // empty until perform) drives the edge auto-scroll.
            if (!ZepSplitDragAndDrop.TryRead(out _))
            {
                _dragAutoScroll.OnDragUpdated(evt.mousePosition);
            }

            var visualMode = RefreshDropTargetFromHover(evt.target as VisualElement);
            if (visualMode == DragAndDropVisualMode.None) return;

            DragAndDrop.visualMode = visualMode;
            evt.StopPropagation();
        }

        // Shared by the DragUpdated handler and the auto-scroll tick (which
        // re-picks under a stationary cursor as content moves): classifies the
        // hovered payload, refreshes the folder drop-target highlight, and
        // returns the visual mode the payload wants (None = not a payload
        // this pane accepts; the caller leaves the event alone).
        private DragAndDropVisualMode RefreshDropTargetFromHover(VisualElement target)
        {
            var projectPaths = EditorDragAndDropPaths.ReadProjectAssetPaths();
            if (projectPaths.Count > 0)
            {
                _collection.SetDropTargetFolderPath(
                    ResolveFolderDropTarget(ResolveHoveredAssetPath(target), projectPaths));
                return DragAndDropVisualMode.Move;
            }

            if (EditorDragAndDropPaths.ReadExternalPaths().Count > 0
                || TryGetSceneGameObjectsFromDrag(out _))
            {
                _collection.SetDropTargetFolderPath(
                    ResolveFolderDropTarget(ResolveHoveredAssetPath(target), null));
                return DragAndDropVisualMode.Copy;
            }

            return DragAndDropVisualMode.None;
        }

        private string ResolveHoveredAssetPath(VisualElement target)
        {
            return TryGetAssetEntryFromTarget(target, out var assetEntry)
                ? assetEntry.Path
                : null;
        }

        // The drop resolves its target by picking at the cursor position, so
        // after auto-scroll moves rows under an unmoved cursor the highlight
        // must re-derive from the same pick or highlight and drop disagree.
        private void RefreshDropTargetAfterAutoScroll(Vector2 dragPanelPosition)
        {
            RefreshDropTargetFromHover(panel?.Pick(dragPanelPosition));
        }

        private void OnDragHoverEnded()
        {
            _dragAutoScroll.Stop();
            _collection.SetDropTargetFolderPath(null);
            ClearDropTargetVerdict();
        }

        // The verdict memo must not outlive the drag that produced it.
        private void ClearDropTargetVerdict()
        {
            _dropTargetVerdictHoveredPath = null;
            _dropTargetVerdict = null;
            _dropTargetVerdictPaths = null;
            _dropTargetVerdictPayloadGeneration = 0;
        }

        // The folder row/tile the hovered drop would land in, or null when the
        // drop targets the browsed folder (empty space, non-folder rows) or is
        // a no-op for every dragged path (self, own parent, own subtree).
        // Resolution mirrors OnViewDragPerformed's target logic so the
        // highlight and the actual drop destination cannot disagree.
        //
        // Memoized per (hovered path, dragged payload): both the IsValidFolder
        // syscall and the move verdict are pure in those, and the payload is
        // fixed for the life of a drag, so a hover that has not left the row —
        // every DragUpdated frame plus every auto-scroll tick — reuses the
        // answer. The payload is keyed twice over: the generation covers the
        // external/scene call shape (which passes no list at all), the list
        // reference covers the move shape, since the read hands back the same
        // memo instance for as long as the payload is unchanged.
        internal string ResolveFolderDropTarget(
            string hoveredAssetPath,
            IReadOnlyList<string> draggedProjectPaths)
        {
            if (string.IsNullOrEmpty(hoveredAssetPath))
            {
                return null;
            }

            var payloadGeneration = EditorDragAndDropPaths.PayloadGeneration;
            if (_dropTargetVerdictPayloadGeneration == payloadGeneration
                && ReferenceEquals(_dropTargetVerdictPaths, draggedProjectPaths)
                && string.Equals(
                    _dropTargetVerdictHoveredPath, hoveredAssetPath, StringComparison.Ordinal))
            {
                return _dropTargetVerdict;
            }

            DropTargetEvaluationsForTests++;
            _dropTargetVerdict =
                _isFolderPath(hoveredAssetPath)
                && (draggedProjectPaths == null
                    || FolderMoveDrop.IsMeaningfulMoveTarget(
                        hoveredAssetPath, draggedProjectPaths))
                    ? hoveredAssetPath
                    : null;
            _dropTargetVerdictHoveredPath = hoveredAssetPath;
            _dropTargetVerdictPayloadGeneration = payloadGeneration;
            _dropTargetVerdictPaths = draggedProjectPaths;
            return _dropTargetVerdict;
        }

        private void OnViewDragPerformed(DragPerformEvent evt)
        {
            _dragAutoScroll.Stop();
            _collection.SetDropTargetFolderPath(null);
            ClearDropTargetVerdict();
            var target = evt.target as VisualElement;
            var isFolderTarget = TryGetAssetEntryFromTarget(target, out var assetEntry)
                && _isFolderPath(assetEntry.Path);

            // Copied, not the shared hover memo: the drop handlers keep the
            // payload past this event.
            var projectPaths = EditorDragAndDropPaths.CopyProjectAssetPaths();
            if (projectPaths.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                if (isFolderTarget)
                    FolderAssetDropReceived?.Invoke(assetEntry.Path, projectPaths);
                else
                    CurrentFolderDropReceived?.Invoke(projectPaths);
                evt.StopPropagation();
                return;
            }

            var externalPaths = EditorDragAndDropPaths.ReadExternalPaths();
            if (externalPaths.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                if (isFolderTarget)
                    FolderAssetExternalImportReceived?.Invoke(assetEntry.Path, externalPaths);
                else
                    CurrentFolderExternalImportReceived?.Invoke(externalPaths);
                evt.StopPropagation();
                return;
            }

            if (!TryGetSceneGameObjectsFromDrag(out var sceneObjects))
            {
                return;
            }

            // Forward the drop's folder target (F6): dropping a Hierarchy GameObject onto a
            // specific subfolder row must create the prefab there, not in the current folder.
            SceneObjectDropReceived?.Invoke(sceneObjects, isFolderTarget ? assetEntry.Path : null);
            DragAndDrop.AcceptDrag();
            evt.StopPropagation();
        }

        private static bool TryGetSceneGameObjectsFromDrag(out List<GameObject> sceneObjects)
        {
            sceneObjects = new List<GameObject>();
            var draggedObjects = DragAndDrop.objectReferences;
            if (draggedObjects == null || draggedObjects.Length == 0)
            {
                return false;
            }

            var seenObjects = new HashSet<GameObject>();
            for (var i = 0; i < draggedObjects.Length; i++)
            {
                GameObject gameObject = null;
                if (draggedObjects[i] is GameObject directGameObject)
                {
                    gameObject = directGameObject;
                }
                else if (draggedObjects[i] is Component component)
                {
                    gameObject = component.gameObject;
                }

                if (gameObject == null)
                {
                    continue;
                }

                if (EditorUtility.IsPersistent(gameObject) || !gameObject.scene.IsValid())
                {
                    continue;
                }

                if (seenObjects.Add(gameObject))
                {
                    sceneObjects.Add(gameObject);
                }
            }

            return sceneObjects.Count > 0;
        }

        // -----------------------------------------------------------------------
        // Asset-surface pointer routing — PointerDown always precedes the
        // compatibility MouseDown. Consume context gestures here in
        // trickle-down before ListView/drag/marquee can mutate selection.
        // -----------------------------------------------------------------------

        private void OnAssetSurfacePointerDown(PointerDownEvent evt)
        {
            if (!ZepContextMenuInput.IsPointerTrigger(evt))
            {
                return;
            }

            _gestures.CancelDragPrepare();
            // Sub-asset rows have no context menu (no rename/delete/tag ops
            // apply); consume the gesture so it doesn't read as empty space.
            if (_collection.TryGetSubAssetFromTarget(evt.target as VisualElement, out _))
            {
                ZepContextMenuInput.Consume(evt);
                return;
            }

            _collection.TryGetAssetEntryFromTarget(
                evt.target as VisualElement,
                out var contextEntry);
            ContextMenuRequested?.Invoke(
                ZepContextMenuInput.PointerAnchor(evt),
                contextEntry);
            ZepContextMenuInput.Consume(evt);
        }

    }
}
