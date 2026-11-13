using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.State;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea.Substrate
{
    // One keyboard-navigation step over the substrate's flat asset order.
    // Left/Right only mean something in grid mode; list mode ignores them.
    internal enum AssetSelectionMove
    {
        Up,
        Down,
        Left,
        Right,
        First,
        Last,
        PageUp,
        PageDown,
    }

    // AssetCollectionView is the shared rendering substrate every AssetsArea
    // feature composes. It owns:
    //   - the swap between list-mode and grid-mode (ListAssetWidget / GridAssetWidget)
    //   - the canonical asset list and selection
    //   - the click-selection resolver (single-click / shift-range / cmd-toggle)
    //   - the keyboard-selection resolver (arrows / home / end / page / select-all / type-ahead)
    //   - dirty-bit caching so repeat SetAssets calls with identical content skip rebuild
    //
    // It does NOT own marquee, drag-prepare, inline-edit FSM, drops, or any
    // chrome (empty state, header buttons, breadcrumb). Features compose
    // those around this view; the view exposes the hooks they need.
    internal sealed class AssetCollectionView : VisualElement
    {
        private readonly UnityZepIconRepository _iconRepository;
        private readonly AssetsAreaInlineEditController _inlineEditController;
        private readonly Func<string, string, AssetPresentationData> _presentationLookup;
        private readonly Func<string, IReadOnlyList<ZepAssetEntry>> _folderContentsLookup;
        // Version of the projection's folder buckets, when the composer wired
        // one. Without it the staleness probe below has to run on every read.
        private readonly Func<int> _folderContentsVersion;
        private readonly int _minGridItemSize;
        private readonly int _maxGridItemSize;

        // Created lazily: only the widget for the active view mode exists.
        // Three substrates are mounted (Browser/Tags), so eager
        // construction meant 8 resident ListViews with pooled rows for the 1
        // the user is looking at. The inactive widget is built on the first
        // switch to its mode.
        private ListAssetWidget _listView;
        private GridAssetWidget _gridView;
        private IAssetWidget _currentView;

        // Canonical state pushed in via SetAssets / SetSelection. The asset
        // list stored here is "what the inner views render" — the composing
        // feature is free to prepend a ghost row (inline-create) before
        // calling SetAssets. Held as IReadOnlyList rather than List so the
        // BrowseProjection's lazy wrapper (LazyAssetEntryList) can be passed
        // straight through to the inner widgets without an eager 30k-entry
        // copy on every keystroke.
        private static readonly IReadOnlyList<ZepAssetEntry> EmptyAssets =
            Array.Empty<ZepAssetEntry>();
        private IReadOnlyList<ZepAssetEntry> _assets = EmptyAssets;
        // The selected paths, the shift-range pivot and lead, and every rule that
        // changes them live in Core; this view pushes the result to its widgets.
        private readonly AssetSelectionModel _selection = new AssetSelectionModel();

        // Drag-aware selection-emission gate. While a left-button press on an
        // asset row is in flight (the window opened by AssetDragPrepareInteraction),
        // the upward SelectionChanged is buffered instead of fired. See the
        // "Selection-emission gate" region below for the why.
        private bool _selectionEmissionDeferred;
        private bool _hasBufferedSelectionEmission;

        // Cache invalidation: reference to the last-pushed asset list. The
        // controller publishes one stable IReadOnlyList result for a projection,
        // so a ReferenceEquals check is sufficient and O(1) — cheaper than the
        // O(assets) signature hash that lived here before. Hashing 30k entries
        // per keystroke (after a broad search) was a measured chunk of the
        // input-latency budget.
        private bool _isAssetListDirty = true;
        private bool _isAssetGridDirty = true;
        private IReadOnlyList<ZepAssetEntry> _lastAssets;

        // ---- Folder expansion (list mode only) ----
        // Enabled + expanded set are pushed from pane state (the owner) on
        // every refresh; enabled is false whenever a search/type/tag filter is
        // active so filtered projections render flat, Finder-style. The grid
        // never sees the expanded list.
        private bool _folderExpansionEnabled;
        private readonly HashSet<string> _expandedFolderPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Reused scratch for the incoming push; de-duplicating it is what makes
        // the change detector in SetFolderExpansion honest (see there).
        private readonly HashSet<string> _incomingFolderPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _expansionVersion;
        // Memo for the flattened list: rebuilt when the base list instance,
        // the expansion set, or any flattened folder's cached children list
        // changes. The child-source refs catch content edits inside an
        // expanded subfolder, which patch that folder's browse-cache bucket
        // without replacing the current folder's own list instance.
        private IReadOnlyList<ZepAssetEntry> _effectiveListAssets = EmptyAssets;
        private IReadOnlyList<ZepAssetEntry> _effectiveBase;
        private int _effectiveVersion = -1;
        // One entry per FOLDER row the last flatten produced — expanded or not.
        // Both kinds bake something from the folder's bucket (expanded rows the
        // children themselves, collapsed rows only the chevron), so both are
        // dependencies the staleness probe has to watch.
        private readonly List<(string FolderPath, IReadOnlyList<ZepAssetEntry> Children, bool IsExpanded)> _effectiveChildSources =
            new List<(string, IReadOnlyList<ZepAssetEntry>, bool)>();
        // The inputs the last child-source probe answered "not stale" for.
        private IReadOnlyList<ZepAssetEntry> _probedBaseAssets;
        private int _probedExpansionVersion = -1;
        private int _probedContentsVersion = -1;
        // What the list widget last received; EnsureVisibleViewRealized pushes
        // whenever the effective instance moves, so expansion changes surface
        // even when the base list reference is unchanged.
        private IReadOnlyList<ZepAssetEntry> _lastPushedListAssets;

        // ---- Sub-asset expansion (both modes) ----
        // Which main-asset rows have their sub-asset disclosure open. Owned
        // here rather than per widget (audit §6.1a): THIS set is what the one
        // VisibleOrderProjection below is reconciled against, so an expansion
        // survives a list↔grid switch and neither widget can hold a different
        // answer from the other. Same comparer
        // as the folder set above — Unity's asset paths are case-insensitive.
        // In-memory and session-local, matching Unity's own sub-asset
        // disclosure; nothing here reaches pane state.
        private readonly HashSet<string> _expandedSubAssetPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The ONE flat visible order both widgets render (audit §6.1b): the
        // published order plus the spans that splice each open disclosure's
        // children in after their parent. Owned here and handed to both
        // widgets, so the index math exists once and neither widget can hold a
        // different answer from the other.
        private readonly VisibleOrderProjection<SubAssetDescriptor> _visibleOrder = SubAssetCatalog.NewVisibleOrder();
        // Retained here so a widget created on the first mode switch receives
        // the same sub-object highlight as the already-realized widget.
        private string _highlightedSubAssetKey = string.Empty;
        // Scratch for the publish-time prune; reused so a publish allocates
        // nothing while an expansion is open.
        private readonly List<string> _subAssetExpansionsToPrune = new List<string>();

        private AssetViewMode _assetViewMode;
        private int _assetGridItemSize;

        internal event Action<IReadOnlyList<ZepAssetEntry>> SelectionChanged;
        internal event Action OpenRequested;
        internal event Action<DragUpdatedEvent> DragUpdated;
        internal event Action<DragPerformEvent> DragPerformed;
        // Sub-asset rows live outside the path-based selection: a commit
        // clears the main selection and announces the sub-asset upward, where
        // the pane view mirrors the sub-OBJECT into Unity's Selection.
        internal event Action<SubAssetDescriptor> SubAssetSelected;
        internal event Action<SubAssetDescriptor> SubAssetOpenRequested;
        // A list-row folder chevron was clicked. The pane state owns the
        // expansion set, so the toggle rides up to it and the new set flows
        // back down through SetFolderExpansion.
        internal event Action<string> FolderExpansionToggled;

        internal AssetCollectionView(
            UnityZepIconRepository iconRepository,
            AssetsAreaInlineEditController inlineEditController,
            AssetViewMode initialViewMode,
            int initialGridItemSize,
            int minGridItemSize,
            int maxGridItemSize,
            Func<string, string, AssetPresentationData> presentationLookup,
            Func<string, IReadOnlyList<ZepAssetEntry>> folderContentsLookup = null,
            Func<int> folderContentsVersion = null)
        {
            _iconRepository = iconRepository ?? throw new ArgumentNullException(nameof(iconRepository));
            _inlineEditController = inlineEditController ?? throw new ArgumentNullException(nameof(inlineEditController));
            _presentationLookup = presentationLookup;
            _folderContentsLookup = folderContentsLookup;
            _folderContentsVersion = folderContentsVersion;
            _assetViewMode = initialViewMode;
            _minGridItemSize = minGridItemSize;
            _maxGridItemSize = maxGridItemSize;
            _assetGridItemSize = Mathf.Clamp(initialGridItemSize, minGridItemSize, maxGridItemSize);

            name = "zep-asset-collection";
            AddToClassList(ZepUiClasses.AssetCollection);

            // Invariant relied on throughout: the active-mode widget always
            // exists (_currentView is never null).
            _currentView = _assetViewMode == AssetViewMode.List
                ? (IAssetWidget)EnsureListView()
                : EnsureGridView();

            RefreshViewPresentation();
        }

        private ListAssetWidget EnsureListView()
        {
            if (_listView != null) return _listView;

            _listView = new ListAssetWidget(
                _iconRepository, _inlineEditController, _presentationLookup, _visibleOrder);
            _listView.OpenRequested        += OnViewOpenRequested;
            _listView.DragUpdated          += OnViewDragUpdated;
            _listView.DragPerformed        += OnViewDragPerformed;
            _listView.FolderExpansionToggled += OnListFolderExpansionToggled;
            _listView.SubAssetExpansionRequested += OnSubAssetExpansionRequested;
            ((IAssetWidget)_listView).ApplyResponsiveItemHeights(_assetGridItemSize, _minGridItemSize, _maxGridItemSize);
            Add(_listView);
            return _listView;
        }

        private GridAssetWidget EnsureGridView()
        {
            if (_gridView != null) return _gridView;

            _gridView = new GridAssetWidget(
                _iconRepository,
                _assetGridItemSize,
                _inlineEditController,
                _presentationLookup,
                _visibleOrder);
            _gridView.OpenRequested        += OnViewOpenRequested;
            _gridView.DragUpdated          += OnViewDragUpdated;
            _gridView.DragPerformed        += OnViewDragPerformed;
            _gridView.SubAssetExpansionRequested += OnSubAssetExpansionRequested;
            Add(_gridView);
            return _gridView;
        }

        // -----------------------------------------------------------------------
        // Public API used by the composing feature
        // -----------------------------------------------------------------------

        internal IReadOnlyList<ZepAssetEntry> Assets => _assets;
        internal IReadOnlyList<AssetPath> SelectedPaths => _selection.Paths;
        internal AssetViewMode CurrentViewMode => _assetViewMode;
        internal int CurrentGridItemSize => _assetGridItemSize;

        internal void SetAssets(IReadOnlyList<ZepAssetEntry> assets)
        {
            if (ReferenceEquals(_lastAssets, assets) && !_isAssetListDirty && !_isAssetGridDirty)
            {
                RefreshViewPresentation();
                EnsureVisibleViewRealized();
                return;
            }

            _assets = assets ?? EmptyAssets;
            _lastAssets = assets;
            _isAssetListDirty = true;
            _isAssetGridDirty = true;
            ZepPerfLog.Count(
                ZepPerfCounter.AssetCollectionPublications);

            // Before the widgets see the new order: a disclosure whose parent
            // left it has no row to hang sub rows under, and the widgets drop
            // that span on their own remap. Dropping it from the canonical set
            // here is what stops a later push from splicing it back into both.
            PruneSubAssetExpansionsOutsideProjection();
            RefreshViewPresentation();
            EnsureVisibleViewRealized();
        }

        // -----------------------------------------------------------------------
        // Sub-asset expansion (both modes)
        // -----------------------------------------------------------------------

        internal bool IsSubAssetExpanded(string assetPath)
            => !string.IsNullOrEmpty(assetPath)
                && _expandedSubAssetPaths.Contains(assetPath);

        internal void ToggleSubAssetExpansion(string assetPath)
            => SetSubAssetExpanded(assetPath, !IsSubAssetExpanded(assetPath));

        internal void SetSubAssetExpanded(string assetPath, bool expanded)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            var changed = expanded
                ? _expandedSubAssetPaths.Add(assetPath)
                : _expandedSubAssetPaths.Remove(assetPath);
            if (!changed)
            {
                return;
            }

            PushSubAssetExpansion();
        }

        // One reconcile, then both widgets — the hidden one included. They
        // read the SAME projection, so nothing is double-counted and the
        // expansion is already in place when a mode switch makes the other
        // widget current; each still has to rebind, because its realized
        // rows/tiles carry visible indices the reconcile just renumbered.
        private void PushSubAssetExpansion()
        {
            if (!_visibleOrder.SetExpandedPaths(_expandedSubAssetPaths))
            {
                return;
            }

            _listView?.RefreshVisibleOrder();
            _gridView?.RefreshVisibleOrder();
            ReconcileSubAssetHighlight();
        }

        // One path lookup per OPEN expansion (usually none), against the same
        // order the active widget is about to receive.
        private void PruneSubAssetExpansionsOutsideProjection()
        {
            if (_expandedSubAssetPaths.Count == 0)
            {
                return;
            }

            var order = SelectionAssets;
            _subAssetExpansionsToPrune.Clear();
            foreach (var path in _expandedSubAssetPaths)
            {
                if (ZepPathHelpers.FindAssetPathIndex(order, path) < 0)
                {
                    _subAssetExpansionsToPrune.Add(path);
                }
            }

            for (var i = 0; i < _subAssetExpansionsToPrune.Count; i++)
            {
                _expandedSubAssetPaths.Remove(_subAssetExpansionsToPrune[i]);
            }

            _subAssetExpansionsToPrune.Clear();
        }

        // A widget asking for a disclosure to open or close: the row/tile
        // chevron, and the grid's prune of an expansion that resolved to no
        // sub-assets at all. The decision stays where it was made; only the
        // membership it implies is applied here, so both widgets follow.
        private void OnSubAssetExpansionRequested(string assetPath, bool expanded)
            => SetSubAssetExpanded(assetPath, expanded);

        // Force the next SetAssets to rebuild even when the list reference matches.
        // Composing features call this when the effective list shape changes
        // outside this class — e.g. inline-edit ghost row prepended/removed.
        internal void MarkBothViewsDirty()
        {
            _isAssetListDirty = true;
            _isAssetGridDirty = true;
            // The ghost case mutates a STABLE list instance in place, which the
            // expansion flatten's ReferenceEquals memo cannot see — drop the
            // memoized base so the next read re-flattens.
            _effectiveBase = null;
        }

        // -----------------------------------------------------------------------
        // Folder expansion (list mode)
        // -----------------------------------------------------------------------

        // Pushed from pane state on every refresh. Disabled ≠ empty: disabled
        // means the projection is filtered (or non-browse) and folder rows
        // render flat without chevrons; an empty set with expansion enabled
        // still renders chevrons on expandable folders.
        internal void SetFolderExpansion(bool enabled, IReadOnlyCollection<AssetPath> expandedFolders)
        {
            var changed = _folderExpansionEnabled != enabled;
            _folderExpansionEnabled = enabled;

            // The incoming collection is de-duplicated FIRST: counting raw entries
            // let a push of {A, A} match an expanded {A, B} on size while every
            // entry was already expanded, so B silently stayed open.
            _incomingFolderPaths.Clear();
            if (expandedFolders != null)
            {
                foreach (var folder in expandedFolders)
                {
                    if (!folder.IsEmpty)
                    {
                        _incomingFolderPaths.Add(folder.Value);
                    }
                }
            }

            if (_incomingFolderPaths.Count != _expandedFolderPaths.Count)
            {
                changed = true;
            }
            else
            {
                foreach (var path in _incomingFolderPaths)
                {
                    if (!_expandedFolderPaths.Contains(path))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed)
            {
                return;
            }

            _expandedFolderPaths.Clear();
            foreach (var path in _incomingFolderPaths)
            {
                _expandedFolderPaths.Add(path);
            }

            _expansionVersion++;
            if (_assetViewMode == AssetViewMode.List)
            {
                EnsureVisibleViewRealized();
                PushSelectionToActiveView();
            }
        }

        private bool FolderExpansionActive =>
            _folderExpansionEnabled && _folderContentsLookup != null;

        // The list the LIST widget renders and every selection resolver
        // navigates in list mode. Identical to _assets unless folder expansion
        // is active, in which case it is the flattened base+children order.
        // Self-validating: rebuilt when the base instance, the expansion set,
        // or any expanded folder's cached children list changed since the last
        // build.
        private IReadOnlyList<ZepAssetEntry> EffectiveListAssets()
        {
            if (!FolderExpansionActive)
            {
                return _assets;
            }

            if (!ReferenceEquals(_effectiveBase, _assets)
                || _effectiveVersion != _expansionVersion
                || AreEffectiveChildSourcesStale())
            {
                _effectiveListAssets = BuildExpandedList(_assets);
                _effectiveBase = _assets;
                _effectiveVersion = _expansionVersion;
            }

            return _effectiveListAssets;
        }

        // Bounded by the folder rows of the current flatten; each check is one
        // dictionary probe into the browse cache. Every read of the selection
        // order used to pay it — several times per keystroke, click and
        // context menu — so the answer is memoised against the three things
        // that can change it: the expansion set, the base list, and the
        // projection's folder buckets. A composer that wired no bucket version
        // keeps the old always-probe behaviour.
        private bool AreEffectiveChildSourcesStale()
        {
            var contentsVersion = _folderContentsVersion?.Invoke() ?? 0;
            if (_folderContentsVersion != null
                && _probedContentsVersion == contentsVersion
                && _probedExpansionVersion == _expansionVersion
                && ReferenceEquals(_probedBaseAssets, _assets))
            {
                return false;
            }

            ChildSourceProbeCount++;
            if (ProbeChildSources())
            {
                // Leave the memo key alone: the caller is about to re-flatten,
                // and the probe after that rebuild is the one worth recording.
                return true;
            }

            _probedContentsVersion = contentsVersion;
            _probedExpansionVersion = _expansionVersion;
            _probedBaseAssets = _assets;
            return false;
        }

        // Test hook: how often the staleness probe actually walked the folder
        // rows (as opposed to answering from the memo).
        internal int ChildSourceProbeCount { get; private set; }

        private bool ProbeChildSources()
        {
            for (var i = 0; i < _effectiveChildSources.Count; i++)
            {
                var source = _effectiveChildSources[i];
                var current = _folderContentsLookup(source.FolderPath);
                if (source.IsExpanded)
                {
                    // The children ARE rows on screen: any bucket swap (added,
                    // removed, renamed child) changes what the list renders.
                    if (!ReferenceEquals(current, source.Children))
                    {
                        return true;
                    }
                }
                else if (HasEntries(current) != HasEntries(source.Children))
                {
                    // A collapsed row bakes only its chevron, so only the
                    // emptiness of the bucket matters — comparing references
                    // here would re-flatten on every unrelated bucket rebuild.
                    // This is also what un-sticks an EXPANDED folder that was
                    // empty at flatten time: it renders as a collapsed row
                    // until its first child exists.
                    return true;
                }
            }

            return false;
        }

        private static bool HasEntries(IReadOnlyList<ZepAssetEntry> entries)
            => entries != null && entries.Count > 0;

        private IReadOnlyList<ZepAssetEntry> BuildExpandedList(
            IReadOnlyList<ZepAssetEntry> baseAssets)
        {
            _effectiveChildSources.Clear();
            var rows = new List<ExpandedAssetList.Row>(baseAssets.Count + 8);
            for (var i = 0; i < baseAssets.Count; i++)
            {
                AppendExpandedRow(rows, baseAssets[i], 0);
            }

            return new ExpandedAssetList(rows);
        }

        // Depth recursion is bounded by real path depth — children live on
        // strictly longer paths, so the walk cannot cycle.
        private void AppendExpandedRow(
            List<ExpandedAssetList.Row> rows,
            ZepAssetEntry entry,
            int depth)
        {
            IReadOnlyList<ZepAssetEntry> children = null;
            var isFolderRow = entry.IsFolder;
            if (isFolderRow)
            {
                children = _folderContentsLookup(entry.Path);
            }

            var hasChildren = HasEntries(children);
            var isExpanded = hasChildren && _expandedFolderPaths.Contains(entry.Path);
            rows.Add(new ExpandedAssetList.Row(entry, depth, isExpanded, hasChildren));
            if (isFolderRow)
            {
                _effectiveChildSources.Add((entry.Path, children, isExpanded));
            }

            if (!isExpanded)
            {
                return;
            }

            for (var i = 0; i < children.Count; i++)
            {
                AppendExpandedRow(rows, children[i], depth + 1);
            }
        }

        private void OnListFolderExpansionToggled(string folderPath)
        {
            if (!string.IsNullOrEmpty(folderPath))
            {
                FolderExpansionToggled?.Invoke(folderPath);
            }
        }

        // Select one path that is present in the current selection order (base
        // or expanded). Runs the normal commit pipeline so pivot/lead, the
        // upward SelectionChanged, and the Unity Selection mirror all follow —
        // the keyboard step-into-child / step-to-parent moves ride this.
        internal bool TrySelectAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var order = SelectionAssets;
            var index = ZepPathHelpers.FindAssetPathIndex(order, path);
            if (index < 0)
            {
                return false;
            }

            _selection.SetPivotAndLead(path);
            CommitClickSelection(new List<string> { path });
            ScrollOrderIndexIntoView(index);
            return true;
        }

        // Scroll to a row this method already has the ORDER index for.
        // ScrollPathIntoView would look the path back up in the same order it
        // was just read out of — a full scan per keypress on a large folder.
        // Only correct straight after a commit, which clears any sub-asset
        // highlight, so the cursor conversion is the plain main-row one.
        private void ScrollOrderIndexIntoView(int orderIndex)
        {
            if (orderIndex < 0)
            {
                return;
            }

            EnsureVisibleViewRealized();
            _currentView.ScrollVisibleIndexIntoView(
                _currentView.ResolveCursorVisibleIndex(orderIndex));
        }

        // The flat order selection semantics run over: the expanded list in
        // list mode (expanded children are real, selectable rows), the plain
        // base list in grid mode.
        private IReadOnlyList<ZepAssetEntry> SelectionAssets =>
            _assetViewMode == AssetViewMode.List ? EffectiveListAssets() : _assets;

        // The same flat order for outside consumers — the pane's
        // visible-assets provider reads this so a path selection made on an
        // expanded child row resolves to its entry (Quick Look, the Unity
        // Selection mirror, create-target resolution). Identical instance to
        // the base list whenever expansion is inactive.
        internal IReadOnlyList<ZepAssetEntry> VisibleAssets => SelectionAssets;

        internal void SetSelection(IReadOnlyCollection<AssetPath> selectedAssetPaths)
        {
            _selection.ReplaceWith(selectedAssetPaths);
            PushSelectionToActiveView();
        }

        internal void SetViewMode(AssetViewMode mode)
        {
            HandleViewModeChange(mode);
        }

        internal void SetGridItemSize(int size, int minSize, int maxSize)
        {
            var clamped = Mathf.Clamp(size, minSize, maxSize);
            if (_assetGridItemSize == clamped) return;

            _assetGridItemSize = clamped;
            // A not-yet-created grid widget picks the size up at construction.
            _gridView?.SetGridItemSize(_assetGridItemSize);
        }

        internal void ApplyResponsiveItemHeights(int gridItemSize, int minSize, int maxSize)
        {
            _assetGridItemSize = Mathf.Clamp(gridItemSize, minSize, maxSize);
            // Not-yet-created widgets receive the current value on creation
            // (EnsureListView applies it; EnsureGridView takes it in the ctor).
            ((IAssetWidget)_listView)?.ApplyResponsiveItemHeights(_assetGridItemSize, minSize, maxSize);
            ((IAssetWidget)_gridView)?.ApplyResponsiveItemHeights(_assetGridItemSize, minSize, maxSize);
        }

        internal void NotifyIconPreviewsReady(IReadOnlyList<string> paths)
        {
            _currentView?.NotifyIconPreviewsReady(paths);
        }

        internal void ScrollPathIntoView(string path)
        {
            _currentView?.ScrollPathIntoView(path);
        }

        internal void PingPath(string path)
        {
            var view = _currentView;
            var assets = _assets;
            // A reveal replaces the folder contents before Unity has laid out
            // their scroll extent. Frame against the updated viewport.
            schedule.Execute(() =>
            {
                if (ReferenceEquals(_currentView, view) && ReferenceEquals(_assets, assets))
                    view?.PingPath(path);
            });
        }

        internal bool TryGetAssetEntryFromTarget(VisualElement target, out ZepAssetEntry assetEntry)
        {
            assetEntry = null;
            return _currentView != null
                && _currentView.TryGetAssetEntryFromTarget(target, out assetEntry);
        }

        // -----------------------------------------------------------------------
        // Composition hooks for marquee / drag-prepare consumers
        // -----------------------------------------------------------------------

        // Is the given event target empty space for marquee purposes — i.e. not
        // on a row/tile? View-mode independent: rows and tiles both resolve
        // through the same ancestor walk. Sub-asset rows/tiles carry no entry
        // but are NOT empty space — a drag starting on one is a sub-object
        // drag, never a marquee.
        internal bool IsEmptySpaceTarget(VisualElement target)
        {
            return target != null
                && AssetEntryRenderer.FindAssetEntryInAncestors(target, null) == null
                && !TryGetSubAssetFromTarget(target, out _);
        }

        // -----------------------------------------------------------------------
        // Sub-asset rows/tiles. Sub-assets share their parent's path, so they
        // never enter the path-keyed selection; the active widget owns their
        // single-item highlight and this view routes commit/open upward.
        // -----------------------------------------------------------------------

        internal bool TryGetSubAssetFromTarget(VisualElement target, out SubAssetDescriptor subAsset)
        {
            subAsset = null;
            if (target == null)
            {
                return false;
            }

            return _currentView.TryGetSubAssetFromTarget(target, out subAsset);
        }

        // True when the pointer target is an expansion chevron — the chevron
        // owns the click, so select/drag FSMs must ignore the press.
        internal bool IsSubAssetDisclosureTarget(VisualElement target)
        {
            if (target == null)
            {
                return false;
            }

            return _currentView.IsDisclosureTarget(target);
        }

        // Pressed-but-unresolved gesture feedback: highlight the sub item
        // immediately; Unity's Selection follows only on commit (mouse-up
        // without a drag), mirroring the main rows' deferred-emission rule.
        internal void PreviewSubAssetSelection(SubAssetDescriptor subAsset)
        {
            ApplySubAssetHighlight(subAsset?.Key);
        }

        internal void CommitSubAssetSelection(SubAssetDescriptor subAsset)
        {
            if (subAsset == null)
            {
                return;
            }

            // The sub item replaces the main selection. No SelectionChanged is
            // raised here — the pane view's SubAssetSelected handler owns both
            // the state clear and the Unity Selection mirror (with the
            // selection-echo suppression only it can arrange).
            _selection.Clear();
            PushSelectionToActiveView();
            ApplySubAssetHighlight(subAsset.Key);
            SubAssetSelected?.Invoke(subAsset);
        }

        // The highlight goes to BOTH widgets, not just the active one: it
        // mirrors which sub-OBJECT Unity's Selection points at, and that
        // doesn't change when the user flips view modes — the other widget
        // must show the same highlighted sub item when it becomes active.
        private void ApplySubAssetHighlight(string subAssetKey)
        {
            _highlightedSubAssetKey = subAssetKey ?? string.Empty;
            _listView?.SetSubAssetHighlight(_highlightedSubAssetKey);
            _gridView?.SetSubAssetHighlight(_highlightedSubAssetKey);
        }

        private void ReconcileSubAssetHighlight()
        {
            if (_highlightedSubAssetKey.Length == 0)
            {
                return;
            }

            if (_visibleOrder.ResolveCursorVisibleIndex(-1, _highlightedSubAssetKey) < 0)
            {
                ApplySubAssetHighlight(null);
                return;
            }

            _currentView.SetSubAssetHighlight(_highlightedSubAssetKey);
        }

        // The drag-over drop-target highlight (folder row/tile under the drag
        // cursor). Pushed by the composing feature on every DragUpdated and
        // cleared on leave/exit/perform; null clears.
        internal void SetDropTargetFolderPath(string folderPath)
        {
            _listView?.SetDropTargetFolderPath(folderPath);
            _gridView?.SetDropTargetFolderPath(folderPath);
        }

        internal void RequestSubAssetOpen(SubAssetDescriptor subAsset)
        {
            if (subAsset != null)
            {
                SubAssetOpenRequested?.Invoke(subAsset);
            }
        }

        // ← on a sub row (list mode): select the main row the sub belongs to.
        // A sub-asset has no children to collapse and no path of its own to
        // walk up from, so its owner is the only thing the key can mean.
        // Passing no lead asset asks the widget for the SUB cursor alone, so a
        // non-negative answer already means the cursor is on a sub row (and
        // ResolveVisibleIndex returns its parent's asset index); a main row
        // answers -1 and leaves the key to the pane view's folder-expansion
        // handling.
        private bool TrySelectSubAssetParentRow()
        {
            var cursor = _currentView.ResolveCursorVisibleIndex(-1);
            if (cursor < 0)
            {
                return false;
            }

            var parentIndex = _currentView.ResolveVisibleIndex(cursor, out _);
            return TrySelectAssetPath(
                ZepPathHelpers.GetAssetPathAt(SelectionAssets, parentIndex));
        }

        // The sub-asset half of list mode's horizontal arrows — the same
        // contract the pane view applies to the folder chevron, because a row
        // shows one chevron for both: → opens a closed disclosure and then
        // steps into its first child, ← closes an open one, and ← on a child
        // steps out to the row that owns it. Everything else (folder rows,
        // rows with no sub-assets, multi-selections) is declined so the pane
        // view's folder expansion and walk-to-parent-folder keep their turn.
        private bool TryHandleSubAssetDisclosureKey(bool isExpandKey)
        {
            // MoveSelection only routes here in list mode, so _currentView is
            // the list widget; the open set itself is this class's own state
            // (audit §6.1a), so nothing here names a concrete widget any more.
            //
            // Passing no lead asset asks for the SUB cursor alone, so a
            // non-negative answer already means the cursor sits on a sub row.
            // A sub-asset has no children of its own, so → means nothing there.
            if (_currentView.ResolveCursorVisibleIndex(-1) >= 0)
            {
                return !isExpandKey && TrySelectSubAssetParentRow();
            }

            if (_selection.Count != 1) return false;

            // The chevron's own predicate: cache-only, so a folder row (which
            // has no representations) and an unresolved path both answer "not
            // expandable" without queuing work behind a keystroke.
            var path = _selection.Paths[0].Value;
            var subAssets = SubAssetCatalog.GetResolved(path);
            if (subAssets == null || subAssets.Length == 0) return false;

            if (!isExpandKey)
            {
                if (!IsSubAssetExpanded(path)) return false;

                SetSubAssetExpanded(path, false);
                return true;
            }

            if (!IsSubAssetExpanded(path))
            {
                SetSubAssetExpanded(path, true);
                return true;
            }

            // Already open: step onto the first sub row, committing it exactly
            // as a click or a plain ↓ onto that row would.
            CommitSubAssetSelection(subAssets[0]);
            var cursor = _currentView.ResolveCursorVisibleIndex(-1);
            if (cursor >= 0)
            {
                _currentView.ScrollVisibleIndexIntoView(cursor);
            }

            return true;
        }

        // Frame a pinged SUB-asset: expand its parent's disclosure, mirror the
        // sub highlight into both widgets (the same rule ApplySubAssetHighlight
        // documents), and let the active widget scroll to and bloom the sub
        // row/tile. The catalog may not have resolved the parent yet —
        // expansion and highlight are declarative, and the widgets finish the
        // scroll + bloom when the resolved rows bind.
        internal void PingSubAsset(string parentPath, long subAssetLocalId)
        {
            if (string.IsNullOrEmpty(parentPath))
            {
                return;
            }

            var key = SubAssetDescriptor.MakeKey(parentPath, subAssetLocalId);
            // ONE expansion, not one per widget: the set is this class's, and
            // the push reaches both views (audit §6.1a).
            SetSubAssetExpanded(parentPath, true);
            ApplySubAssetHighlight(key);
            _currentView.PingSubAsset(parentPath, key);
        }

        // The cells whose world-space bounds intersect worldRect. Dispatches to
        // the active view: the row/tile geometry math is mode-specific.
        internal MarqueeHitRegion ComputeMarqueeHitRegion(Rect worldRect)
        {
            return _currentView.ComputeMarqueeHitRegion(worldRect);
        }

        // The selectable path at one visible cell, or false when the cell holds
        // a sub-asset (they occupy geometry but never join a path selection) or
        // nothing at all.
        internal bool TryGetMarqueePath(int visibleIndex, out string path)
        {
            return _currentView.TryGetMarqueePath(visibleIndex, out path);
        }

        internal ScrollView ResolveActiveScrollView()
        {
            return _currentView.ScrollView;
        }

        // Mid-drag preview, expressed as what changed since the previous move.
        // The substrate's selection stays live (a cancel has to be able to
        // restore over it) but is no longer rebuilt from scratch per move.
        internal void ApplyMarqueePreview(
            IReadOnlyList<string> added,
            IReadOnlyList<string> removed)
        {
            _selection.ApplyPreview(added, removed);
            _currentView.ApplyMarqueePreview(added, removed);
        }

        internal void CommitMarqueeSelection(IReadOnlyList<string> paths)
        {
            // Any user-intent selection commit displaces the sub-asset
            // highlight — including an EMPTY one (empty-space click, marquee
            // over nothing). The non-empty-only guard inside the widgets exists
            // solely for the state-echo pushes that follow a sub-asset commit.
            ApplySubAssetHighlight(null);
            _selection.ReplaceWithPaths(paths);
            // A marquee's paths arrive in asset order; anchor the pivot at the top
            // end so a following shift-click/arrow extends from a defined point.
            _selection.AnchorAtEnds(paths);
            PushSelectionToActiveView();
            SelectionChanged?.Invoke(GetSelectedEntries());
        }

        internal void ClearSelection()
        {
            // User-intent clear — see CommitMarqueeSelection for the why.
            ApplySubAssetHighlight(null);
            _selection.Clear();
            PushSelectionToActiveView();
            SelectionChanged?.Invoke(Array.Empty<ZepAssetEntry>());
        }

        internal List<ZepAssetEntry> GetSelectedEntries()
        {
            // The order is not asked for when nothing is selected: producing it
            // can mean flattening the expanded list.
            return _selection.Count == 0
                ? new List<ZepAssetEntry>()
                : _selection.GetSelectedEntries(SelectionAssets);
        }

        internal void CaptureCurrentSelectionPaths(List<string> target)
        {
            _selection.CapturePaths(target);
        }

        internal void CommitClickSelection(IReadOnlyList<string> paths)
        {
            // User-intent commit — see CommitMarqueeSelection for the why.
            ApplySubAssetHighlight(null);
            _selection.ReplaceWithPaths(paths);
            PushSelectionToActiveView();
            RaiseSelectionChanged(GetSelectedEntries());
        }

        // Click-selection resolver: cmd-toggle, shift-range, or replace over
        // the current order and selection. Ranges run over the visible order,
        // so shift-clicking across an open expansion takes the expanded
        // children in between.
        internal List<string> ResolveClickSelection(string clickedPath, bool toggle, bool range)
        {
            return _selection.ResolveClick(() => SelectionAssets, clickedPath, toggle, range);
        }

        internal bool ContainsSelectedPath(string path)
        {
            return _selection.Contains(path);
        }

        internal void SelectAllAssets()
        {
            // Over the visible order: with folders expanded, select-all takes
            // the expanded children too, exactly what Finder's ⌘A does.
            var order = SelectionAssets;
            if (order.Count == 0) return;

            CommitClickSelection(_selection.SelectAllPaths(order));
        }

        // Keyboard-selection resolver: the arrow/home/end/page counterpart of
        // ResolveClickSelection. The range extends from the explicit pivot to the
        // lead (both stored as their own fields so they survive the
        // SelectionChanged→SetSelection round-trip, which re-orders the selection
        // list). A plain move sets a new pivot; an extension moves only the lead,
        // so repeated shift-moves grow/shrink the same end from a fixed anchor.
        //
        // The cursor walks the VISIBLE order — main rows/tiles plus the sub
        // rows/tiles of every open expansion — so a plain move steps onto
        // exactly what sits next on screen. Sub-assets share their parent's
        // path and cannot enter the path-keyed selection, so landing on one
        // commits it as the single sub selection instead; a shift-range,
        // which is path-keyed by definition, stops at the main asset the sub
        // cell belongs to.
        // Returns false when the key doesn't apply (no assets, horizontal
        // arrow in list mode) so the caller can leave the event unconsumed.
        internal bool MoveSelection(AssetSelectionMove move, bool extend)
        {
            // One snapshot for the whole move: index math and path reads must
            // agree on the order (expanded rows included in list mode).
            var order = SelectionAssets;
            var count = order.Count;
            if (count == 0) return false;

            var isGrid = _assetViewMode == AssetViewMode.Grid;
            if (!isGrid && (move == AssetSelectionMove.Left || move == AssetSelectionMove.Right))
            {
                // List mode's horizontal arrows are the Finder disclosure keys.
                // A row carries one chevron for two different expansions, so
                // the key is answered in two places: the pane view owns the
                // FOLDER half (it needs the pane's expansion set and the browse
                // projection), and this owns the SUB-ASSET half, whose open set
                // is widget state. Declining leaves the key for the pane view.
                return TryHandleSubAssetDisclosureKey(move == AssetSelectionMove.Right);
            }

            // The active widget owns the open expansions, so it owns the
            // visible-order projection the cursor moves in; `order` stays the
            // path-keyed asset space the selection itself lives in.
            var visibleCount = _currentView.VisibleItemCount;
            if (visibleCount <= 0) return false;

            _selection.ResolveAnchorAndLeadIndices(order, out var anchorIndex, out var leadIndex);
            var cursor = _currentView.ResolveCursorVisibleIndex(leadIndex);

            int targetVisible;
            if (cursor < 0)
            {
                // Nothing selected (or the selection isn't in the current
                // list): enter at the end the key points toward, Finder-style.
                targetVisible = move == AssetSelectionMove.Up
                      || move == AssetSelectionMove.Left
                      || move == AssetSelectionMove.PageUp
                      || move == AssetSelectionMove.Last
                    ? visibleCount - 1
                    : 0;
            }
            else
            {
                // Column count is grid-only geometry (a list row is one
                // column), so it is read off the grid widget directly.
                var columns = isGrid ? Mathf.Max(1, EnsureGridView().ColumnsPerRow) : 1;
                var verticalStep = columns;
                var pageStep = Mathf.Max(1, VisibleRowCount()) * columns;
                switch (move)
                {
                    case AssetSelectionMove.Up: targetVisible = cursor - verticalStep; break;
                    case AssetSelectionMove.Down: targetVisible = cursor + verticalStep; break;
                    case AssetSelectionMove.Left: targetVisible = cursor - 1; break;
                    case AssetSelectionMove.Right: targetVisible = cursor + 1; break;
                    case AssetSelectionMove.First: targetVisible = 0; break;
                    case AssetSelectionMove.Last: targetVisible = visibleCount - 1; break;
                    case AssetSelectionMove.PageUp: targetVisible = cursor - pageStep; break;
                    case AssetSelectionMove.PageDown: targetVisible = cursor + pageStep; break;
                    default: return false;
                }

                // Grid Up from the top row stays put rather than wrapping to
                // the first item in a different column; every other move
                // clamps to the list ends (Down near the tail lands on the
                // last item even when the final row is partial).
                if (isGrid && move == AssetSelectionMove.Up && targetVisible < 0)
                {
                    targetVisible = cursor;
                }

                targetVisible = Mathf.Clamp(targetVisible, 0, visibleCount - 1);
            }

            var target = _currentView.ResolveVisibleIndex(targetVisible, out var targetSubAsset);

            // An extension without an anchor has no range to grow (committing a
            // sub-asset empties the path selection), so shift behaves as a plain
            // move there rather than silently jumping to a list end.
            var extendsRange = extend && anchorIndex >= 0;

            if (targetSubAsset != null && !extendsRange)
            {
                // Same commit a click on the sub row/tile makes: it replaces the
                // main selection, and the pane view mirrors the sub-OBJECT into
                // Unity's Selection.
                CommitSubAssetSelection(targetSubAsset);
                _currentView.ScrollVisibleIndexIntoView(targetVisible);
                return true;
            }

            if (targetSubAsset != null)
            {
                // The range stops at the main asset owning the sub cell: its
                // parent when the move ran backward, the asset after the whole
                // expansion when it ran forward (clamped at the list end).
                target = targetVisible < cursor ? target : target + 1;
            }

            target = Mathf.Clamp(target, 0, count - 1);
            var targetPath = ZepPathHelpers.GetAssetPathAt(order, target);
            if (string.IsNullOrEmpty(targetPath)) return false;

            List<string> next;
            if (extendsRange)
            {
                // Extension: keep the pivot, move only the lead to the new cursor.
                next = _selection.ExtendRangeTo(order, anchorIndex, target, targetPath);
            }
            else
            {
                next = new List<string> { targetPath };
                // Plain move: new fixed pivot at the cursor.
                _selection.SetPivotAndLead(targetPath);
            }

            CommitClickSelection(next);
            ScrollOrderIndexIntoView(target);
            return true;
        }

        // Type-ahead: select the first asset whose file name starts with
        // prefix (ordinal, case-insensitive).
        internal bool SelectFirstNameMatch(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;

            var index = AssetSelectionModel.FindFirstNameMatch(SelectionAssets, prefix, out var path);
            if (index < 0) return false;

            _selection.SetPivotAndLead(path);
            CommitClickSelection(new List<string> { path });
            ScrollOrderIndexIntoView(index);
            return true;
        }

        // -----------------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------------

        private void HandleViewModeChange(AssetViewMode assetViewMode)
        {
            if (_assetViewMode == assetViewMode)
            {
                RefreshViewPresentation();
                EnsureVisibleViewRealized();
                return;
            }

            _assetViewMode = assetViewMode;
            _currentView = _assetViewMode == AssetViewMode.List
                ? (IAssetWidget)EnsureListView()
                : EnsureGridView();

            if (_assetViewMode == AssetViewMode.List)
                _isAssetListDirty = true;
            else
                _isAssetGridDirty = true;

            RefreshViewPresentation();
            EnsureVisibleViewRealized();
            // AFTER the realize: a widget created by this switch has just
            // received the current order, and only then can it resolve a row
            // for each open disclosure. With nothing open there is nothing to
            // push — which also leaves a widget driven directly (widget-level
            // tests, no substrate set in play) with its own expansions.
            if (_expandedSubAssetPaths.Count > 0)
            {
                PushSubAssetExpansion();
            }

            // The view we just left keeps a stale selection mirror; it gets a
            // fresh push here the next time it becomes active.
            PushSelectionToActiveView();
        }

        private void RefreshViewPresentation()
        {
            var hasAssets = _assets.Count > 0;
            var isListMode = _assetViewMode == AssetViewMode.List;

            // A not-yet-created widget has nothing to show or hide.
            if (_listView != null)
            {
                _listView.ShowListHeader(isListMode);
                _listView.style.display = isListMode ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_gridView != null)
            {
                _gridView.Show(hasAssets && !isListMode);
                _gridView.style.display = !isListMode ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void EnsureVisibleViewRealized()
        {
            if (_assetViewMode == AssetViewMode.List)
            {
                // The instance compare (not just the dirty bit) is what makes
                // expansion changes land: a chevron toggle or an edit inside
                // an expanded subfolder produces a new effective list while
                // the base list reference stays the same.
                var listAssets = EffectiveListAssets();
                if (_isAssetListDirty || !ReferenceEquals(_lastPushedListAssets, listAssets))
                {
                    _currentView.SetAssets(listAssets);
                    _lastPushedListAssets = listAssets;
                    _isAssetListDirty = false;
                }
            }
            else if (_isAssetGridDirty)
            {
                _currentView.SetAssets(_assets);
                _isAssetGridDirty = false;
            }

            ReconcileSubAssetHighlight();
        }

        // Only the visible view gets live pushes; the hidden one receives the
        // current selection when a mode switch makes it active (see
        // HandleViewModeChange). Pushing to the hidden sibling on every click
        // was pure waste — and in grid mode a full visible-window rebind.
        private void PushSelectionToActiveView()
        {
            if (_selection.Count > 0)
            {
                ApplySubAssetHighlight(null);
            }

            // The SET, not the ordered list: the widgets only ask "is this
            // path selected", and pushing the canonical set lets them read it
            // by reference instead of rebuilding k entries per push — twice
            // per commit, since the pane echoes the commit back through
            // SetSelection (audit §5.1). The set is a READ view for them;
            // their marquee preview copies it before mutating.
            _currentView.SetSelectedPaths(_selection.PathSet);
        }

        // -----------------------------------------------------------------------
        // Inner-view event handlers
        // -----------------------------------------------------------------------

        private void OnViewOpenRequested() => OpenRequested?.Invoke();

        private void OnViewDragUpdated(DragUpdatedEvent evt) => DragUpdated?.Invoke(evt);
        private void OnViewDragPerformed(DragPerformEvent evt) => DragPerformed?.Invoke(evt);

        // -----------------------------------------------------------------------
        // Selection-emission gate (drag-aware)
        //
        // A left-button press on an asset row can become either a click or a
        // drag, and which one isn't known until the gesture resolves. The
        // in-panel highlight updates instantly either way (PushSelectionToActiveView
        // on commit), but the *upward* SelectionChanged
        // — which the shell mirrors into Unity's global Selection, and the
        // Inspector follows — must wait. Firing it on press would retarget the
        // Inspector to the pressed asset the instant it's grabbed, so dragging a
        // sprite onto a SpriteRenderer (or any object-reference field) would lose
        // the object being edited before the drop can land.
        //
        // While the window is open the emission is buffered; the gesture resolves
        // it — flushed on a click (pointer-up with no drag), discarded once it
        // becomes a drag. AssetDragPrepareInteraction owns the click-vs-drag
        // decision and drives these transitions; this view owns the buffer.
        // -----------------------------------------------------------------------

        private void RaiseSelectionChanged(IReadOnlyList<ZepAssetEntry> entries)
        {
            if (_selectionEmissionDeferred)
            {
                _hasBufferedSelectionEmission = true;
                return;
            }

            SelectionChanged?.Invoke(entries);
        }

        internal void BeginDeferredSelectionEmission()
        {
            _selectionEmissionDeferred = true;
            _hasBufferedSelectionEmission = false;
        }

        internal void FlushDeferredSelectionEmission()
        {
            if (!_selectionEmissionDeferred) return;

            _selectionEmissionDeferred = false;
            if (_hasBufferedSelectionEmission)
            {
                _hasBufferedSelectionEmission = false;
                SelectionChanged?.Invoke(GetSelectedEntries());
            }
        }

        internal void DiscardDeferredSelectionEmission()
        {
            _selectionEmissionDeferred = false;
            _hasBufferedSelectionEmission = false;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        // Rows visible in the active viewport — the PageUp/PageDown step.
        // Before the first layout pass (detached shell, tests) the viewport
        // has no height yet; fall back to a sane page of 10 rows.
        private int VisibleRowCount()
        {
            var viewport = ResolveActiveScrollView()?.contentViewport;
            var height = viewport?.layout.height ?? float.NaN;
            var pitch = _currentView.FixedItemHeight;
            if (float.IsNaN(height) || height <= 0f || pitch <= 0f) return 10;

            return Mathf.Max(1, (int)(height / pitch));
        }

    }
}
