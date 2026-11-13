using System;
using Zep.Core.State;
using System.Collections;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea.Substrate;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Finder-style grid renderer with Unity-style inline sub-assets. Expanded
    // children join the parent's flat tile stream immediately to its right and
    // wrap naturally onto later rows. The outer ListView still virtualizes fixed
    // rows, so realized UI stays bounded by the viewport even for huge projects.
    internal sealed class GridAssetWidget : VisualElement, IAssetWidget
    {
        internal sealed class AssetGridItemParts
        {
            public ZepAssetEntry Entry;
            public SubAssetDescriptor SubAsset;
            public int BoundAssetIndex = -1;
            public int BoundVisibleIndex = -1;
            public VisualElement Root;
            // Hover/selection/edit/drop paint and the ping rings live here, not
            // on Root: the card hugs the icon, name and badges, while Root is
            // the fixed row-cell that hosts the group container under it.
            public VisualElement Card;
            public AssetPingPulse PingPulse;
            public VisualElement Icon;
            public VisualElement IconImage;
            public VisualElement SubAssetGroupBackground;
            public VisualElement Info;
            public Label Name;
            public Label SubAssetType;
            public VisualElement Tags;
            public Label Addressable;
            public TextField NameField;
            public VisualElement[] EditMuted;
            public Button Disclosure;
            // Created on demand and then retexted/shown/hidden per bind, like
            // the list widget's chip: a chip built per bind piles up invisibly
            // inside a badge container the tag rebuild has already hidden.
            public int TagSignature;
            public string CachedPath;
            // Reused while CachedPath and the previously bound descriptor's
            // name match the next sub-asset.
            public string CachedSubTooltip;
            public DateTime? CachedModifiedDate;
            public bool CachedIsAddressable;
            public Texture2D CachedIcon;
            public ZepTagColor CachedFolderTintColor;
            public bool CachedIsEditRow;
        }

        private sealed class GridRowParts
        {
            public VisualElement TileRow;
        }

        // Count-only IList for the outer ListView. A million assets therefore
        // cost one facade object, not one integer stored per grid row.
        private sealed class RowSource : IList
        {
            private readonly GridAssetWidget _owner;

            internal RowSource(GridAssetWidget owner)
            {
                _owner = owner;
            }

            public int Count => _owner._displayRowCount;
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public object this[int index]
            {
                get => index;
                set => throw new NotSupportedException();
            }

            public int IndexOf(object value) => value is int index
                && index >= 0
                && index < Count
                    ? index
                    : -1;
            public bool Contains(object value) => IndexOf(value) >= 0;

            public IEnumerator GetEnumerator()
            {
                for (var i = 0; i < Count; i++)
                {
                    yield return i;
                }
            }

            public void CopyTo(Array array, int index)
            {
                for (var i = 0; i < Count; i++)
                {
                    array.SetValue(i, index + i);
                }
            }

            public int Add(object value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public void Insert(int index, object value) => throw new NotSupportedException();
            public void Remove(object value) => throw new NotSupportedException();
            public void RemoveAt(int index) => throw new NotSupportedException();
        }

        private const int RowVerticalGap = 8;
        // The card's vertical chrome around the thumbnail box, so a row is
        // pitched to the tallest card it can hold at the current zoom instead
        // of to a constant tuned at one zoom (base + 18 fit only the default
        // size: tiles overflowed their row at the minimum zoom and floated in
        // blank space at the maximum). Mirrors .zep-asset-grid-card
        // and its children: padding 5 + border 1 above, a two-line name
        // (max-height 32), the badge row (margin-top 2 + a labelled chip, 18),
        // padding 3 + border 1 below. GridAssetWidgetBindingTests pins every
        // realized card inside its row at both zoom extremes.
        private const int TileChromeHeight = 62;

        private readonly UnityZepIconRepository _iconRepository;
        private readonly AssetsAreaInlineEditController _inlineEditController;
        private readonly Func<string, string, AssetPresentationData> _presentationLookup;
        private readonly ListView _rowListView;
        private readonly RowSource _rowSource;

        // The visible cell order — the published assets with every open
        // sub-asset disclosure's children spliced in — and the published order
        // itself. Shared with the list widget and owned by the substrate
        // (audit §6.1b), so an expansion opened in one mode is already in place
        // in the other. A widget built without one (widget-level tests) makes
        // its own, which is what keeps a substrate-less chevron working.
        private readonly VisibleOrderProjection<SubAssetDescriptor> _order;
        private IReadOnlyList<ZepAssetEntry> Assets => _order.Assets;
        // Normally the substrate's canonical selection set, held by reference
        // so a push costs nothing instead of rebuilding k entries (audit
        // §5.1). Read-only here: the marquee preview copies it before
        // mutating — see EnsureOwnedSelectionSet.
        private HashSet<AssetPath> _selectedPathsSet = new HashSet<AssetPath>();
        private bool _ownsSelectionSet = true;
        // How many times this widget has materialized a selection set of its
        // own. Read by tests only; a selection push must not build one.
        internal static int SelectionSetBuildsForTests;
        private readonly List<AssetGridItemParts> _tilePool = new List<AssetGridItemParts>();
        // Scratch for the path-scoped refresh: the realized rows a batch
        // touches, each rebound once. Reused so a raise allocates nothing.
        private readonly HashSet<int> _rowsToRefresh = new HashSet<int>();

        private int _gridItemSize;
        private int _columnsPerRow = 1;
        private int _displayRowCount;
        private string _highlightedSubAssetKey = string.Empty;
        private string _dropTargetFolderPath = string.Empty;
        private string _pendingPingPath = string.Empty;
        private string _pendingPingSubAssetParentPath = string.Empty;
        private string _pendingPingSubAssetKey = string.Empty;

        public event Action OpenRequested;
        public event Action<DragUpdatedEvent> DragUpdated;
        public event Action<DragPerformEvent> DragPerformed;
        // A sub-asset disclosure should open (true) or close (false): the tile
        // chevron, and the prune of an expansion whose parent resolved to no
        // sub-assets. The substrate owns the set, so the new set comes back
        // through RefreshVisibleOrder.
        public event Action<string, bool> SubAssetExpansionRequested;

        internal GridAssetWidget(
            UnityZepIconRepository iconRepository,
            int initialGridItemSize,
            AssetsAreaInlineEditController inlineEditController,
            Func<string, string, AssetPresentationData> presentationLookup,
            VisibleOrderProjection<SubAssetDescriptor> visibleOrder = null)
        {
            _iconRepository = iconRepository ?? throw new ArgumentNullException(nameof(iconRepository));
            _inlineEditController = inlineEditController ?? throw new ArgumentNullException(nameof(inlineEditController));
            _presentationLookup = presentationLookup;
            _order = visibleOrder ?? SubAssetCatalog.NewVisibleOrder();
            _gridItemSize = Mathf.Clamp(
                initialGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);

            name = "zep-grid-asset-view";
            _rowSource = new RowSource(this);
            _rowListView = new ListView
            {
                name = "zep-asset-grid-list",
                fixedItemHeight = ComputeRowHeight(),
                selectionType = SelectionType.None,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None,
                itemsSource = _rowSource,
            };
            _rowListView.makeItem = MakeRow;
            _rowListView.bindItem = BindRow;
            _rowListView.unbindItem = UnbindRow;
            _rowListView.RegisterCallback<MouseDownEvent>(OnGridMouseDown, TrickleDown.TrickleDown);
            _rowListView.RegisterCallback<DragUpdatedEvent>(OnDragUpdated, TrickleDown.TrickleDown);
            _rowListView.RegisterCallback<DragPerformEvent>(OnDragPerform, TrickleDown.TrickleDown);
            Add(_rowListView);

            // On the element the column count is measured FROM, not on the
            // widget: content growing past the viewport pushes the vertical
            // scroller in, which narrows the content viewport by the scroller's
            // width without ever changing the widget's own rect. Listening on
            // the widget left the column count one column too wide until the
            // next publish or resize re-measured it.
            ScrollView.contentViewport.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            // A drained catalog batch means the tiles it names may need their
            // disclosure arrow (or an expanded tile its children), so rebind
            // the rows those tiles sit in.
            RegisterCallback<AttachToPanelEvent>(_ => SubAssetCatalog.Changed += OnSubAssetCatalogChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => SubAssetCatalog.Changed -= OnSubAssetCatalogChanged);
        }

        void IAssetWidget.SetAssets(IReadOnlyList<ZepAssetEntry> assets)
        {
            using var measurement = ZepPerfLog.Measure(
                "asset-grid",
                ZepPerfStage.AssetListRefresh);
            var stopwatch = ZepPerfLog.IsEnabled
                ? System.Diagnostics.Stopwatch.StartNew()
                : null;

            _order.SetAssets(assets);
            // The pin the projection just dropped (its tile left the order) is
            // this widget's pending ping: it can never complete now.
            if (_pendingPingSubAssetKey.Length > 0 && _order.PinnedParentPath.Length == 0)
            {
                ClearPendingSubAssetPing();
            }

            RecomputeRows();
            // Same rule as the list widget's: a re-published projection must
            // not silently un-select a committed sub-asset, which holds no
            // path of its own to fall back on. See ListAssetWidget.SetAssets.
            if (ResolveCursorVisibleIndex(-1) < 0)
            {
                _highlightedSubAssetKey = string.Empty;
            }

            if (stopwatch != null)
            {
                stopwatch.Stop();
                ZepPerfLog.Log(
                    $"[Zep/perf]   GridAssetWidget.SetAssets count={Assets.Count} "
                    + $"columns={_columnsPerRow} visibleCells={_order.VisibleItemCount} "
                    + $"expansions={_order.ExpansionCount} displayRows={_displayRowCount} "
                    + $"ms={stopwatch.Elapsed.TotalMilliseconds:F2}");
            }
        }

        // ------------------------------------------------------------------
        // Sparse flat-stream projection
        // ------------------------------------------------------------------

        internal int DisplayRowCount => _displayRowCount;
        internal int ExpandedAssetCount => _order.ExpansionCount;
        internal int RealizedTileCount => _tilePool.Count;
        public int VisibleItemCount => _order.VisibleItemCount;

        // How many main tiles the grid has bound, and how many inline style
        // writes the tile sizing helpers have performed. Read by tests only;
        // nothing branches on either.
        internal static int BindCountForTests;
        internal static int StyleWritesForTests;
        // How many times a sub-asset tile has built its tooltip string. Unity
        // owns the tooltip PROPERTY (TextElement rewrites it around eliding and
        // the setter compares by value), so the allocation is what a test can
        // see — and a rebind of an unchanged sub-asset must not add one.
        internal static int SubAssetTooltipBuildsForTests;

        // Maps one flat tile position back to its main asset. A non-null subAsset
        // means the position belongs to an expanded child of the returned parent.
        // The order and the spans live in the shared VisibleOrderProjection
        // (audit §6.1b); what stays here is the row/column layer that turns a
        // visible cell index into a grid row and a strip segment.
        public int ResolveVisibleIndex(int visibleIndex, out SubAssetDescriptor subAsset)
            => _order.ResolveVisibleIndex(visibleIndex, out subAsset);

        internal int AssetIndexToVisibleIndex(int assetIndex)
            => _order.AssetIndexToVisibleIndex(assetIndex);

        // Where the keyboard cursor sits in the visible tile stream: on the
        // highlighted sub tile when a sub-asset is the current selection (it
        // has no path of its own, so the substrate's lead cannot point at it),
        // otherwise on the lead asset's own tile. -1 when neither resolves.
        public int ResolveCursorVisibleIndex(int leadAssetIndex)
            => _order.ResolveCursorVisibleIndex(leadAssetIndex, _highlightedSubAssetKey);

        // The shared order changed under this widget (the substrate reconciled
        // it against a new open set): re-project AND rebind, because every tile
        // after a changed expansion is renumbered and the bound visible index
        // each realized tile carries is stale until it rebinds.
        public void RefreshVisibleOrder() => RecomputeRows();

        public void ExpandSubAssets(string assetPath)
        {
            if (_order.Expand(assetPath))
            {
                RecomputeRows();
            }
        }

        // The widget behind the inactive view mode is never detached —
        // AssetCollectionView only sets display:none on it — so it keeps
        // hearing every catalog drain and every preview batch, and would
        // re-project and refresh for each one. Nothing can be visible behind
        // display:none, so it opts out here instead.
        private bool IsHidden => resolvedStyle.display == DisplayStyle.None;

        private void OnSubAssetCatalogChanged(IReadOnlyList<string> resolvedPaths)
        {
            if (panel == null || Assets.Count == 0 || IsHidden)
            {
                return;
            }

            // The pending ping's parent has answered without the target: the
            // ping can never complete, so the projection releases the pin it
            // was holding the disclosure open with and the ping goes with it.
            if (_order.DropPinIfResolvedWithout(_pendingPingSubAssetKey))
            {
                ClearPendingSubAssetPing();
            }

            // Only a changed tile stream needs the full re-projection; a batch
            // that just filled in disclosure arrows rebinds the rows it names.
            var pruned = _order.RefreshResolvedSubs(out var shapeChanged);
            if (shapeChanged || resolvedPaths == null || resolvedPaths.Count == 0)
            {
                RecomputeRows();
            }
            else
            {
                RefreshRowsForPaths(resolvedPaths);
            }

            TryFinishPendingSubAssetPing();

            // The prune decided in the projection, but the MEMBERSHIP it drops
            // belongs to the substrate — raised after the refresh, or the next
            // push would splice the empty expansion straight back in. Raised by
            // index: the projection's list is only cleared by the next
            // RefreshResolvedSubs.
            for (var i = 0; i < pruned.Count; i++)
            {
                SubAssetExpansionRequested?.Invoke(pruned[i], false);
            }
        }

        // Rebinds the realized grid ROWS holding a tile whose asset path — or,
        // for a child tile, whose parent's path — the batch names. The batch is
        // short (the catalog drains a handful of paths per raise; an import
        // batch is the outlier) and the tile registry is the realized window,
        // so a linear membership test beats building a set per raise.
        //
        // The rows are collected first and rebound after: a rebind can add or
        // release tiles, and the registry is in creation order, not row order.
        private void RefreshRowsForPaths(IReadOnlyList<string> paths)
        {
            if (_columnsPerRow <= 0)
            {
                return;
            }

            _rowsToRefresh.Clear();
            for (var i = 0; i < _tilePool.Count; i++)
            {
                var tileParts = _tilePool[i];
                if (tileParts.BoundVisibleIndex < 0 || tileParts.Root?.panel == null)
                {
                    continue;
                }

                var path = tileParts.SubAsset != null
                    ? tileParts.SubAsset.ParentPath
                    : tileParts.Entry?.Path;
                if (string.IsNullOrEmpty(path) || !ContainsPath(paths, path))
                {
                    continue;
                }

                _rowsToRefresh.Add(tileParts.BoundVisibleIndex / _columnsPerRow);
            }

            foreach (var rowIndex in _rowsToRefresh)
            {
                _rowListView.RefreshItem(rowIndex);
            }

            _rowsToRefresh.Clear();
        }

        private static bool ContainsPath(IReadOnlyList<string> paths, string path)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Selection, reveal, and interaction resolution
        // ------------------------------------------------------------------

        public void SetSubAssetHighlight(string subAssetKey)
        {
            var next = subAssetKey ?? string.Empty;
            if (string.Equals(_highlightedSubAssetKey, next, StringComparison.Ordinal))
            {
                return;
            }

            _highlightedSubAssetKey = next;
            SyncVisibleTileSelection();
        }

        public void SetDropTargetFolderPath(string folderPath)
        {
            var next = folderPath ?? string.Empty;
            if (string.Equals(_dropTargetFolderPath, next, StringComparison.Ordinal))
            {
                return;
            }

            _dropTargetFolderPath = next;
            for (var i = 0; i < _tilePool.Count; i++)
            {
                var parts = _tilePool[i];
                if (parts.Root.panel == null)
                {
                    continue;
                }

                parts.Card.EnableInClassList(
                    ZepUiClasses.DropTarget,
                    IsDropTargetEntry(parts.Entry));
            }
        }

        private bool IsDropTargetEntry(ZepAssetEntry entry)
        {
            return entry != null
                && _dropTargetFolderPath.Length > 0
                && string.Equals(entry.Path, _dropTargetFolderPath, StringComparison.Ordinal);
        }

        public bool TryGetSubAssetFromTarget(
            VisualElement target,
            out SubAssetDescriptor subAsset)
        {
            subAsset = null;
            var current = target;
            while (current != null && current != _rowListView && current != this)
            {
                if (current.userData is AssetGridItemParts parts
                    && parts.SubAsset != null)
                {
                    subAsset = parts.SubAsset;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        public bool IsDisclosureTarget(VisualElement target)
        {
            var current = target;
            while (current != null && current != _rowListView && current != this)
            {
                if (current.ClassListContains(ZepUiClasses.AssetGridDisclosureButton))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        void IAssetWidget.SetSelectedPaths(IReadOnlyCollection<AssetPath> paths)
        {
            if (paths is HashSet<AssetPath> canonical)
            {
                // The substrate's own set (which never admits an empty path):
                // read through it rather than copying it per push.
                _selectedPathsSet = canonical;
                _ownsSelectionSet = false;
            }
            else
            {
                // Any other collection shape the contract allows.
                SelectionSetBuildsForTests++;
                var owned = new HashSet<AssetPath>();
                if (paths != null)
                {
                    foreach (var path in paths)
                    {
                        if (!path.IsEmpty)
                        {
                            owned.Add(path);
                        }
                    }
                }

                _selectedPathsSet = owned;
                _ownsSelectionSet = true;
            }

            if (_selectedPathsSet.Count > 0)
            {
                _highlightedSubAssetKey = string.Empty;
            }

            SyncVisibleTileSelection();
        }

        void IAssetWidget.NotifyIconPreviewsReady(IReadOnlyList<string> paths)
        {
            if (IsHidden)
            {
                return;
            }

            // An icon upgrade never moves a tile, so the named rows are the
            // only ones whose bind can produce a different pixel.
            if (paths == null || paths.Count == 0)
            {
                _rowListView.RefreshItems();
            }
            else
            {
                RefreshRowsForPaths(paths);
            }
        }

        void IAssetWidget.ApplyResponsiveItemHeights(
            int gridItemSize,
            int minSize,
            int maxSize)
        {
            var clamped = Mathf.Clamp(gridItemSize, minSize, maxSize);
            if (_gridItemSize == clamped)
            {
                return;
            }

            _gridItemSize = clamped;
            ApplyGridSizing();
        }

        internal void SetGridItemSize(int size)
        {
            var clamped = Mathf.Clamp(
                size,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);
            if (_gridItemSize == clamped)
            {
                return;
            }

            _gridItemSize = clamped;
            ApplyGridSizing();
        }

        bool IAssetWidget.TryGetAssetEntryFromTarget(
            VisualElement target,
            out ZepAssetEntry assetEntry)
        {
            assetEntry = AssetEntryRenderer.FindAssetEntryInAncestors(target, _rowListView);
            return assetEntry != null;
        }

        public void ScrollPathIntoView(string path)
        {
            var index = FindPathIndex(path);
            if (index >= 0)
            {
                _rowListView.ScrollToItem(
                    AssetIndexToVisibleIndex(index) / Mathf.Max(1, _columnsPerRow));
            }
        }

        // Scroll the outer row that carries one visible tile — main or sub —
        // into view. The keyboard cursor's counterpart to ScrollPathIntoView:
        // a sub tile has no path to look up, so it is addressed by its
        // visible index.
        public void ScrollVisibleIndexIntoView(int visibleIndex)
        {
            if (visibleIndex >= 0 && visibleIndex < VisibleItemCount)
            {
                _rowListView.ScrollToItem(visibleIndex / Mathf.Max(1, _columnsPerRow));
            }
        }

        public void PingPath(string path)
        {
            var index = FindPathIndex(path);
            if (index < 0)
            {
                return;
            }

            ClearPendingSubAssetPing();
            _pendingPingPath = path;
            _rowListView.ScrollToItem(
                AssetIndexToVisibleIndex(index) / Mathf.Max(1, _columnsPerRow));
            PlayPendingPingIfRealized();
        }

        public void PingSubAsset(string parentPath, string subAssetKey)
        {
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(subAssetKey))
            {
                return;
            }

            _pendingPingPath = string.Empty;
            _pendingPingSubAssetParentPath = parentPath;
            _pendingPingSubAssetKey = subAssetKey;
            // Hold the parent's disclosure open until the target arrives or the
            // catalog answers without it (the projection's pin, §6.1b).
            _order.PinnedParentPath = parentPath;
            ExpandSubAssets(parentPath);
            ScrollToPendingSubAssetTile();
            PlayPendingPingIfRealized();
        }

        private void ClearPendingSubAssetPing()
        {
            _pendingPingSubAssetParentPath = string.Empty;
            _pendingPingSubAssetKey = string.Empty;
            _order.PinnedParentPath = string.Empty;
        }

        private void TryFinishPendingSubAssetPing()
        {
            if (string.IsNullOrEmpty(_pendingPingSubAssetKey))
            {
                return;
            }

            ScrollToPendingSubAssetTile();
            PlayPendingPingIfRealized();
        }

        private void ScrollToPendingSubAssetTile()
        {
            var targetVisibleIndex = _order.ResolvePingVisibleIndex(
                _pendingPingSubAssetParentPath,
                _pendingPingSubAssetKey);
            if (targetVisibleIndex >= 0)
            {
                _rowListView.ScrollToItem(
                    targetVisibleIndex / Mathf.Max(1, _columnsPerRow));
            }
        }

        private int FindPathIndex(string path) => _order.FindPathIndex(path);

        private void PlayPendingPingIfRealized()
        {
            for (var i = 0;
                i < _tilePool.Count
                    && (!string.IsNullOrEmpty(_pendingPingPath)
                        || !string.IsNullOrEmpty(_pendingPingSubAssetKey));
                i++)
            {
                TryPlayPendingMainPing(_tilePool[i]);
                TryPlayPendingSubAssetPing(_tilePool[i]);
            }
        }

        private void TryPlayPendingMainPing(AssetGridItemParts parts)
        {
            if (parts.Root.panel == null
                || parts.Entry == null
                || !string.Equals(parts.Entry.Path, _pendingPingPath, StringComparison.Ordinal))
            {
                return;
            }

            var path = _pendingPingPath;
            // ScrollToItem realizes the virtual row but can leave its tile
            // clipped. Align the tile's actual bounds after its layout updates.
            schedule.Execute(() =>
            {
                if (parts.Root.panel != panel
                    || !string.Equals(_pendingPingPath, path, StringComparison.Ordinal)
                    || !string.Equals(parts.Entry?.Path, path, StringComparison.Ordinal))
                    return;

                ScrollView.ScrollTo(parts.Root);
                _pendingPingPath = string.Empty;
                PlayPing(parts.Card, ref parts.PingPulse);
            });
        }

        private void TryPlayPendingSubAssetPing(AssetGridItemParts parts)
        {
            if (parts.Root.panel == null
                || parts.SubAsset == null
                || !string.Equals(
                    parts.SubAsset.Key,
                    _pendingPingSubAssetKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            ClearPendingSubAssetPing();
            PlayPing(parts.Card, ref parts.PingPulse);
        }

        private static void PlayPing(VisualElement root, ref AssetPingPulse pulse)
        {
            if (pulse == null)
            {
                pulse = new AssetPingPulse();
                root.Add(pulse);
            }

            pulse.Play();
        }

        // ------------------------------------------------------------------
        // Surface helpers and marquee geometry
        // ------------------------------------------------------------------

        internal void Show(bool show)
        {
            _rowListView.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        internal int ColumnsPerRow => _columnsPerRow;
        public float FixedItemHeight => _rowListView.fixedItemHeight;

        private ScrollView _cachedScrollView;
        public ScrollView ScrollView
        {
            get
            {
                if (_cachedScrollView == null)
                {
                    _cachedScrollView = _rowListView.Q<ScrollView>();
                }

                return _cachedScrollView;
            }
        }

        public MarqueeHitRegion ComputeMarqueeHitRegion(Rect worldRect)
        {
            if (Assets.Count == 0 || _columnsPerRow <= 0)
            {
                return MarqueeHitRegion.Empty;
            }

            var rowHeight = _rowListView.fixedItemHeight;
            var pitch = (float)ComputeTilePitch();
            if (rowHeight <= 0f || pitch <= 0f)
            {
                return MarqueeHitRegion.Empty;
            }

            var hasColumnOrigin = false;
            var columnZeroX = 0f;
            for (var i = 0; i < _tilePool.Count; i++)
            {
                var parts = _tilePool[i];
                if ((parts.Entry == null && parts.SubAsset == null)
                    || parts.Root.panel == null)
                {
                    continue;
                }

                var column = parts.BoundVisibleIndex % _columnsPerRow;
                columnZeroX = parts.Root.worldBound.xMin - column * pitch;
                hasColumnOrigin = true;
                break;
            }

            if (!hasColumnOrigin)
            {
                return MarqueeHitRegion.Empty;
            }

            var firstColumn = Mathf.Max(
                0,
                Mathf.FloorToInt((worldRect.xMin - columnZeroX) / pitch));
            var lastColumn = Mathf.Min(
                _columnsPerRow - 1,
                Mathf.FloorToInt((worldRect.xMax - columnZeroX) / pitch));
            if (firstColumn > lastColumn)
            {
                return MarqueeHitRegion.Empty;
            }

            // Measure from the ScrollView's contentViewport, never from the
            // ListView itself: #zep-asset-grid-list carries USS
            // padding, so the list's own top sits ABOVE row 0 by that padding
            // and every row index came out one band low at the boundaries.
            // Reading the viewport keeps the math tied to whatever the
            // stylesheet does instead of to a constant that can desync.
            var scrollView = ScrollView;
            var scrollOffset = scrollView != null ? scrollView.scrollOffset.y : 0f;
            var contentOriginY = scrollView != null
                ? scrollView.contentViewport.worldBound.yMin
                : _rowListView.worldBound.yMin;
            var contentTop = worldRect.yMin - contentOriginY + scrollOffset;
            var contentBottom = worldRect.yMax - contentOriginY + scrollOffset;
            var firstRow = Mathf.Max(0, Mathf.FloorToInt(contentTop / rowHeight));
            var lastRow = Mathf.Min(
                _displayRowCount - 1,
                Mathf.FloorToInt(contentBottom / rowHeight));

            return new MarqueeHitRegion(
                firstRow,
                lastRow,
                firstColumn,
                lastColumn,
                _columnsPerRow);
        }

        // One tile position: row * columns + column. Sub-asset tiles and
        // positions past the end of the grid resolve to nothing.
        public bool TryGetMarqueePath(int visibleIndex, out string path)
        {
            path = null;
            if (visibleIndex < 0 || visibleIndex >= VisibleItemCount)
            {
                return false;
            }

            var assetIndex = ResolveVisibleIndex(visibleIndex, out var subAsset);
            if (subAsset != null || assetIndex < 0 || assetIndex >= Assets.Count)
            {
                return false;
            }

            path = ZepPathHelpers.GetAssetPathAt(Assets, assetIndex);
            return true;
        }

        // See ListAssetWidget.EnsureOwnedSelectionSet.
        private void EnsureOwnedSelectionSet()
        {
            if (_ownsSelectionSet) return;
            SelectionSetBuildsForTests++;
            _selectedPathsSet = new HashSet<AssetPath>(
                _selectedPathsSet,
                _selectedPathsSet.Comparer);
            _ownsSelectionSet = true;
        }

        // Delta preview — see ListAssetWidget.ApplyMarqueePreview.
        public void ApplyMarqueePreview(
            IReadOnlyList<string> added,
            IReadOnlyList<string> removed)
        {
            EnsureOwnedSelectionSet();
            if (removed != null)
            {
                for (var i = 0; i < removed.Count; i++)
                {
                    _selectedPathsSet.Remove(AssetPath.From(removed[i]));
                }
            }

            if (added != null)
            {
                for (var i = 0; i < added.Count; i++)
                {
                    var path = AssetPath.From(added[i]);
                    if (!path.IsEmpty)
                    {
                        _selectedPathsSet.Add(path);
                    }
                }
            }

            if (_selectedPathsSet.Count > 0)
            {
                _highlightedSubAssetKey = string.Empty;
            }

            SyncVisibleTileSelection();
        }

        private void SyncVisibleTileSelection()
        {
            for (var i = 0; i < _tilePool.Count; i++)
            {
                var parts = _tilePool[i];
                if (parts.Root.panel == null)
                {
                    continue;
                }

                var isSelected = parts.SubAsset != null
                    ? string.Equals(
                        parts.SubAsset.Key,
                        _highlightedSubAssetKey,
                        StringComparison.Ordinal)
                    : parts.Entry != null
                        && _selectedPathsSet.Contains(AssetPath.From(parts.Entry.Path));
                parts.Root.EnableInClassList(
                    ZepUiClasses.AssetGridItemSelected,
                    isSelected);
            }
        }

        // ------------------------------------------------------------------
        // Outer fixed-height row virtualization
        // ------------------------------------------------------------------

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (Mathf.Approximately(evt.newRect.width, evt.oldRect.width))
            {
                return;
            }

            if (ComputeColumnsForViewport() != _columnsPerRow)
            {
                RecomputeRows();
            }
        }

        private void RecomputeRows(bool rebuild = false)
        {
            _columnsPerRow = ComputeColumnsForViewport();
            _displayRowCount = _columnsPerRow > 0
                ? (VisibleItemCount + _columnsPerRow - 1) / _columnsPerRow
                : 0;

            // No re-assignment of itemsSource here: the collection view's setter
            // compares by reference and early-returns on the same facade, so it
            // never notified anything. RefreshItems (and Rebuild) re-read the
            // facade's dynamic Count and resize the content on their own.
            if (rebuild)
            {
                _rowListView.Rebuild();
            }
            else
            {
                _rowListView.RefreshItems();
            }
        }

        private int ComputeColumnsForViewport()
        {
            var viewportWidth = ScrollView?.contentViewport.resolvedStyle.width
                ?? resolvedStyle.width;
            if (float.IsNaN(viewportWidth) || viewportWidth <= 0f)
            {
                return Mathf.Max(1, _columnsPerRow);
            }

            // The tile row pads 4px a side, and every column but the last is
            // followed by the cell gap: n columns need 8 + n * tile + (n - 1)
            // * gap. Charging a full pitch for the last column too left a
            // tile's worth of empty space at the row's end whenever the
            // viewport was within one gap of fitting another.
            var usable = Mathf.Max(0f, viewportWidth - 8f + RowVerticalGap);
            return Mathf.Max(1, Mathf.FloorToInt(usable / ComputeTilePitch()));
        }

        private int ComputeTilePitch()
        {
            return Mathf.Clamp(
                _gridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize) + RowVerticalGap;
        }

        private int ComputeRowHeight()
        {
            var baseSize = Mathf.Clamp(
                _gridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);
            return IconContainerSizeFor(baseSize) + TileChromeHeight + RowVerticalGap;
        }

        // The thumbnail box a tile of this size centres its icon in.
        private static int IconContainerSizeFor(int baseSize)
            => Mathf.RoundToInt(baseSize * 0.62f);

        private void ApplyGridSizing()
        {
            // Assigning fixedItemHeight does NOT re-pitch rows the fixed-height
            // virtualization controller has already realized: they keep the old
            // height and stacking, so every tile silently overflows its row and
            // a selected tile paints across the row below. Same lesson as the
            // list widget's ApplyResponsiveItemHeights — only Rebuild recreates
            // the rows at the new pitch, and it discards the realized row pool,
            // so drop the tile registry to track only the tiles the coming
            // makeItem calls recreate (each bind sizes its fresh tile itself).
            _rowListView.fixedItemHeight = ComputeRowHeight();
            _tilePool.Clear();
            RecomputeRows(rebuild: true);
        }

        private VisualElement MakeRow()
        {
            var parts = new GridRowParts();
            var root = new VisualElement { name = "zep-asset-grid-row" };
            root.AddToClassList(ZepUiClasses.AssetGridRow);
            root.userData = parts;

            var tileRow = new VisualElement { name = "zep-asset-grid-tile-row" };
            tileRow.AddToClassList(ZepUiClasses.AssetGridTileRow);
            root.Add(tileRow);

            parts.TileRow = tileRow;
            return root;
        }

        private void UnbindRow(VisualElement rowElement, int rowIndex)
        {
            if (!(rowElement.userData is GridRowParts parts))
            {
                return;
            }

            for (var i = 0; i < parts.TileRow.childCount; i++)
            {
                if (parts.TileRow[i].userData is AssetGridItemParts tileParts
                    && tileParts.CachedIsEditRow)
                {
                    _inlineEditController.HandleRowReleased(tileParts.NameField);
                }
            }
        }

        private void BindRow(VisualElement rowElement, int rowIndex)
        {
            var parts = (GridRowParts)rowElement.userData;
            while (parts.TileRow.childCount < _columnsPerRow)
            {
                parts.TileRow.Add(MakeGridItem());
            }

            // Widening the window grows every realized row to the new column
            // count; narrowing it used to only hide the surplus tiles, so a
            // wide-then-narrow resize left each row holding tiles (and their
            // icons and registry entries) that nothing can ever bind again.
            // Release them here, where the row is in hand and the tile has just
            // been unbound, rather than leaving them to the next Rebuild.
            for (var column = parts.TileRow.childCount - 1; column >= _columnsPerRow; column--)
            {
                var surplus = parts.TileRow[column];
                var surplusParts = (AssetGridItemParts)surplus.userData;
                ClearMainItemBinding(surplusParts);
                parts.TileRow.RemoveAt(column);
                _tilePool.Remove(surplusParts);
            }

            for (var column = 0; column < parts.TileRow.childCount; column++)
            {
                var tile = parts.TileRow[column];
                // A tile out of range is hidden by `visibility` below, never by
                // `display`, so nothing here has to switch display back on.
                var visibleIndex = rowIndex * _columnsPerRow + column;
                if (visibleIndex >= VisibleItemCount)
                {
                    tile.style.visibility = Visibility.Hidden;
                    ClearMainItemBinding((AssetGridItemParts)tile.userData);
                    continue;
                }

                tile.style.visibility = Visibility.Visible;
                var assetIndex = _order.ResolveVisibleIndex(
                    visibleIndex,
                    out var subAsset,
                    out var subAssetIndex,
                    out var subAssetCount);
                var tileParts = (AssetGridItemParts)tile.userData;
                tileParts.BoundVisibleIndex = visibleIndex;
                var endsRow = column == _columnsPerRow - 1;
                if (subAsset != null)
                {
                    // The parent leads the group, so a child only starts a
                    // segment where the group wraps onto a new row.
                    BindSubAssetGridItem(
                        tile,
                        subAsset,
                        assetIndex,
                        startsSegment: column == 0,
                        endsSegment: subAssetIndex == subAssetCount - 1 || endsRow,
                        continuesFromPreviousRow: column == 0);
                }
                else
                {
                    var entry = Assets[assetIndex];
                    BindGridItem(
                        tile,
                        entry,
                        assetIndex,
                        leadsGroup: _order.ResolvedChildCount(entry.Path) > 0,
                        endsRow);
                }
            }
        }

        // Both callers reach here with the tile still holding its last binding:
        // a column that no longer exists, or a position past the item count.
        // Neither gets a follow-up bind, so this is the same release point
        // UnbindRow is for the whole row — a tile that hosted the active edit
        // must hand its field back BEFORE the entry is nulled, or the session
        // lives on invisibly (hiding a tile does not blur its TextField, and
        // every field callback is keyed on the now-null entry's path).
        // HandleRowReleased is a no-op for any field that is not the active one.
        private void ClearMainItemBinding(AssetGridItemParts parts)
        {
            if (parts.CachedIsEditRow)
            {
                _inlineEditController.HandleRowReleased(parts.NameField);
            }

            parts.Entry = null;
            parts.SubAsset = null;
            parts.BoundAssetIndex = -1;
            parts.BoundVisibleIndex = -1;
            parts.Disclosure.style.visibility = Visibility.Hidden;
            parts.Root.EnableInClassList(ZepUiClasses.AssetGridItemSubAsset, false);
            ApplyGroupSegmentClasses(
                parts.Root,
                grouped: false,
                starts: false,
                ends: false,
                continues: false);
            parts.PingPulse?.Reset();
        }

        private VisualElement MakeGridItem()
        {
            var parts = new AssetGridItemParts();
            var item = new VisualElement { name = "zep-asset-grid-item" };
            item.AddToClassList(ZepUiClasses.AssetGridItem);
            item.userData = parts;
            _tilePool.Add(parts);

            var subAssetGroupBackground = new VisualElement
            {
                name = "zep-asset-grid-subasset-group-background",
            };
            subAssetGroupBackground.AddToClassList(
                ZepUiClasses.AssetGridSubAssetGroupBackground);
            var continuationMarker = new VisualElement
            {
                name = "zep-asset-grid-subasset-continuation-marker",
            };
            continuationMarker.AddToClassList(
                ZepUiClasses.AssetGridSubAssetContinuationMarker);
            subAssetGroupBackground.Add(continuationMarker);
            item.Add(subAssetGroupBackground);

            // The group container above is the tile's first child, so the card
            // that carries hover/selection paint has to be a later sibling: an
            // element's background draws under its children, so a root that
            // painted the selection itself could never sit on top of the group
            // fill its own segment draws. The card also hugs its content, which
            // is what keeps a selected tile from filling the whole row-cell.
            var card = new VisualElement { name = "zep-asset-grid-card" };
            card.AddToClassList(ZepUiClasses.AssetGridCard);
            item.Add(card);

            var icon = new VisualElement { name = "zep-asset-grid-icon" };
            icon.AddToClassList(ZepUiClasses.AssetGridIcon);
            var iconImage = new VisualElement { name = "zep-asset-grid-icon-image" };
            iconImage.AddToClassList(ZepUiClasses.AssetGridIconImage);
            icon.Add(iconImage);

            var addressable = new Label { name = "zep-asset-grid-addressable" };
            addressable.AddToClassList(ZepUiClasses.AssetGridAddressableBadge);
            icon.Add(addressable);

            var disclosure = new Button
            {
                name = "zep-asset-grid-disclosure",
                focusable = false,
                text = string.Empty,
            };
            disclosure.AddToClassList(ZepUiClasses.AssetGridDisclosureButton);
            disclosure.AddToClassList(ZepUiClasses.FlatButton);
            disclosure.AddToClassList(ZepUiClasses.ThemedIcon);
            disclosure.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            disclosure.clicked += () =>
            {
                if (parts.Entry != null && parts.BoundAssetIndex >= 0)
                {
                    // The open set is the substrate's, so the chevron asks
                    // rather than toggles: the answer arrives back as a push
                    // and reaches the list widget with it.
                    SubAssetExpansionRequested?.Invoke(
                        parts.Entry.Path,
                        !_order.IsExpanded(parts.Entry.Path));
                }
            };
            card.Add(icon);

            var info = new VisualElement { name = "zep-asset-grid-info" };
            info.AddToClassList(ZepUiClasses.AssetGridInfo);
            var name = new Label { name = "zep-asset-grid-name" };
            name.AddToClassList(ZepUiClasses.AssetGridName);
            info.Add(name);
            var subAssetType = new Label { name = "zep-asset-grid-subasset-type" };
            subAssetType.AddToClassList(ZepUiClasses.AssetGridSubAssetType);
            info.Add(subAssetType);
            card.Add(info);

            var tags = new VisualElement { name = "zep-asset-grid-tags" };
            tags.AddToClassList(ZepUiClasses.RowBadges);
            tags.AddToClassList(ZepUiClasses.AssetGridTags);
            card.Add(tags);
            card.Add(disclosure);

            parts.Root = item;
            parts.Card = card;
            parts.Icon = icon;
            parts.IconImage = iconImage;
            parts.SubAssetGroupBackground = subAssetGroupBackground;
            parts.Info = info;
            parts.Name = name;
            parts.SubAssetType = subAssetType;
            parts.Tags = tags;
            parts.Addressable = addressable;
            parts.Disclosure = disclosure;
            parts.EditMuted = new VisualElement[] { addressable, tags };
            ApplyTileSizing(parts);
            return item;
        }

        private void EnsureTileEditField(AssetGridItemParts parts)
        {
            if (parts.NameField != null)
            {
                return;
            }

            var nameField = new TextField { name = "zep-asset-grid-name-field" };
            nameField.style.display = DisplayStyle.None;
            parts.Info.Insert(parts.Info.IndexOf(parts.Name) + 1, nameField);
            parts.NameField = nameField;

            nameField.RegisterCallback<KeyDownEvent>(
                evt => _inlineEditController.HandleRowEscapeKeyDown(
                    evt,
                    parts.Entry?.Path),
                TrickleDown.TrickleDown);
            nameField.RegisterCallback<KeyDownEvent>(
                evt => _inlineEditController.HandleRowKeyDown(
                    evt,
                    parts.Entry?.Path,
                    parts.NameField));
            nameField.RegisterValueChangedCallback(
                evt => _inlineEditController.HandleRowValueChanged(
                    evt.newValue,
                    parts.Entry?.Path));
            nameField.RegisterCallback<FocusOutEvent>(
                evt => _inlineEditController.HandleRowFocusOut(
                    parts.Entry?.Path,
                    parts.NameField));
        }

        // leadsGroup: the tile is an open disclosure with children in the
        // order, so it draws the first segment of the group container its
        // children continue. endsRow closes that segment at the row's last
        // column; the first child on the next row re-opens it with a notch.
        private void BindGridItem(
            VisualElement item,
            ZepAssetEntry entry,
            int assetIndex,
            bool leadsGroup,
            bool endsRow)
        {
            BindCountForTests++;
            var parts = (AssetGridItemParts)item.userData;
            parts.Entry = entry;
            parts.SubAsset = null;
            parts.BoundAssetIndex = assetIndex;
            item.EnableInClassList(ZepUiClasses.AssetGridItemSubAsset, false);
            ApplyGroupSegmentClasses(
                item,
                grouped: leadsGroup,
                starts: leadsGroup,
                ends: leadsGroup && endsRow,
                continues: false);

            var presentation = _presentationLookup != null
                ? _presentationLookup(entry.Path, entry.TypeName)
                : default;
            var isSamePath = string.Equals(parts.CachedPath, entry.Path, StringComparison.Ordinal);
            if (!isSamePath)
            {
                parts.PingPulse?.Reset();
            }

            var isEditRow = _inlineEditController.IsActiveForPath(entry.Path);
            var needsUpdate = !isSamePath
                || parts.CachedModifiedDate != presentation.ModifiedDate
                || parts.CachedIsAddressable != presentation.IsAddressable
                || parts.CachedFolderTintColor != entry.FolderTintColor
                || parts.CachedIsEditRow != isEditRow;
            if (needsUpdate)
            {
                AssetEntryRenderer.ApplyFolderIconTint(parts.IconImage, entry);
                parts.Name.text = entry.Name;
                var modified = AssetEntryRenderer.FormatModifiedDate(presentation.ModifiedDate);
                parts.Name.tooltip = string.IsNullOrEmpty(modified)
                    ? entry.Path
                    : entry.Path + "\n" + modified;
                parts.Addressable.text = presentation.IsAddressable ? "✓" : string.Empty;
                parts.Addressable.style.display = presentation.IsAddressable
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;

                parts.CachedPath = entry.Path;
                parts.CachedModifiedDate = presentation.ModifiedDate;
                parts.CachedIsAddressable = presentation.IsAddressable;
                parts.CachedFolderTintColor = entry.FolderTintColor;
            }

            var assetIcon = AssetEntryRenderer.ResolveAssetIcon(_iconRepository, entry);
            if (!ReferenceEquals(parts.CachedIcon, assetIcon))
            {
                parts.IconImage.style.backgroundImage = assetIcon != null
                    ? new StyleBackground(assetIcon)
                    : new StyleBackground();
                parts.CachedIcon = assetIcon;
            }

            var tagSignature = AssetEntryRenderer.ComputeTagSignature(entry.Tags, false, 4);
            if (parts.TagSignature != tagSignature)
            {
                AssetEntryRenderer.RebuildTagBadges(parts.Tags, entry.Tags, false, 4);
                parts.TagSignature = tagSignature;
            }


            if (isEditRow || parts.CachedIsEditRow)
            {
                if (isEditRow)
                {
                    EnsureTileEditField(parts);
                }

                _inlineEditController.BindRow(
                    entryPath: entry.Path,
                    nameLabel: parts.Name,
                    editContainer: parts.NameField,
                    nameField: parts.NameField,
                    extensionLabel: null,
                    rowRoot: parts.Root,
                    editingUssClass: ZepUiClasses.AssetGridItemEditing,
                    mutedElements: parts.EditMuted);
                parts.CachedIsEditRow = isEditRow;
            }

            BindDisclosure(parts, entry);
            item.EnableInClassList(
                ZepUiClasses.AssetGridItemSelected,
                _selectedPathsSet.Contains(AssetPath.From(entry.Path)));
            parts.Card.EnableInClassList(
                ZepUiClasses.DropTarget,
                IsDropTargetEntry(entry));
            TryPlayPendingMainPing(parts);
        }

        private void BindDisclosure(AssetGridItemParts parts, ZepAssetEntry entry)
        {
            SubAssetDescriptor[] subAssets = null;
            if (!entry.IsFolder && !string.IsNullOrEmpty(entry.Path))
            {
                subAssets = SubAssetCatalog.GetOrRequest(entry.Path);
            }

            var isExpanded = _order.IsExpanded(entry.Path);
            var hasSubAssets = isExpanded || (subAssets != null && subAssets.Length > 0);
            parts.Disclosure.style.visibility = hasSubAssets
                ? Visibility.Visible
                : Visibility.Hidden;
            if (hasSubAssets)
            {
                parts.Disclosure.style.backgroundImage =
                    AssetEntryRenderer.GetDisclosureChevron(isExpanded);
                parts.Disclosure.tooltip = isExpanded
                    ? "Hide subassets"
                    : "Show subassets";
            }
        }

        // Every write here derives from _gridItemSize alone, so it belongs to the
        // tile's creation, not to its binding: a tile outlives any number of
        // binds, and the only thing that can change _gridItemSize goes through
        // ApplyGridSizing, which drops the tile pool and rebuilds every row —
        // so the makeItem calls that follow re-apply this at the new size.
        private void ApplyTileSizing(AssetGridItemParts parts)
        {
            var baseSize = Mathf.Clamp(
                _gridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);
            var iconContainerSize = IconContainerSizeFor(baseSize);
            var iconImageSize = Mathf.RoundToInt(baseSize * 0.56f);

            // Test hook: one per inline style write below. A non-zero delta
            // across a bind pass means static sizing crept back onto it.
            StyleWritesForTests += 7;
            parts.Root.style.width = baseSize;
            parts.Root.style.height = ComputeRowHeight() - RowVerticalGap;
            parts.Icon.style.width = iconContainerSize;
            parts.Icon.style.height = iconContainerSize;
            parts.IconImage.style.width = iconImageSize;
            parts.IconImage.style.height = iconImageSize;
            // Card top padding (5px) plus half the zoom-dependent preview box.
            // USS offsets the button by half its own height.
            parts.Disclosure.style.top = 5f + iconContainerSize * 0.5f;
        }

        // Shares the badge container with the tag chips, so that container's
        // display is the OR of both needs — RebuildTagBadges hides it whenever
        // the tile has no tags. Re-attaching rather than assuming: the tag
        // rebuild pools the container BY INDEX and replaces any Label it finds
        // in the range it rebuilds, so a tagged tile can take the chip out of
        // the hierarchy.

        private void BindSubAssetGridItem(
            VisualElement item,
            SubAssetDescriptor subAsset,
            int parentAssetIndex,
            bool startsSegment,
            bool endsSegment,
            bool continuesFromPreviousRow)
        {
            var parts = (AssetGridItemParts)item.userData;
            // Key identity owns the ping; the tooltip also depends on the
            // previous descriptor's name, which can change on reimport.
            var isSameSubAsset = string.Equals(parts.CachedPath, subAsset.Key, StringComparison.Ordinal);
            var isSameSubAssetName = string.Equals(parts.SubAsset?.Name, subAsset.Name, StringComparison.Ordinal);
            if (!isSameSubAsset)
            {
                parts.PingPulse?.Reset();
            }

            parts.Entry = null;
            parts.SubAsset = subAsset;
            parts.BoundAssetIndex = parentAssetIndex;

            parts.Name.text = subAsset.Name;
            parts.SubAssetType.text = subAsset.TypeName;
            parts.SubAssetType.tooltip = subAsset.TypeName;
            if (!isSameSubAsset || !isSameSubAssetName || parts.CachedSubTooltip == null)
            {
                parts.CachedSubTooltip = subAsset.ParentPath + " › " + subAsset.Name;
                SubAssetTooltipBuildsForTests++;
            }

            parts.Name.tooltip = parts.CachedSubTooltip;
            parts.Addressable.text = string.Empty;
            parts.Addressable.style.display = DisplayStyle.None;
            parts.Disclosure.style.visibility = Visibility.Hidden;

            AssetEntryRenderer.ApplyFolderIconTint(parts.IconImage, null);
            parts.IconImage.style.backgroundImage = subAsset.Icon != null
                ? new StyleBackground(subAsset.Icon)
                : new StyleBackground();
            parts.CachedIcon = subAsset.Icon;

            if (parts.TagSignature != 0)
            {
                AssetEntryRenderer.RebuildTagBadges(parts.Tags, null, false, 4);
                parts.TagSignature = 0;
            }


            if (parts.CachedIsEditRow)
            {
                _inlineEditController.BindRow(
                    entryPath: string.Empty,
                    nameLabel: parts.Name,
                    editContainer: parts.NameField,
                    nameField: parts.NameField,
                    extensionLabel: null,
                    rowRoot: parts.Root,
                    editingUssClass: ZepUiClasses.AssetGridItemEditing,
                    mutedElements: parts.EditMuted);
                parts.CachedIsEditRow = false;
            }

            parts.CachedPath = subAsset.Key;
            parts.CachedModifiedDate = null;
            parts.CachedIsAddressable = false;
            parts.CachedFolderTintColor = ZepTagColor.None;

            item.EnableInClassList(ZepUiClasses.AssetGridItemSubAsset, true);
            ApplyGroupSegmentClasses(
                item,
                grouped: true,
                starts: startsSegment,
                ends: endsSegment,
                continues: continuesFromPreviousRow);
            item.EnableInClassList(
                ZepUiClasses.AssetGridItemSelected,
                string.Equals(
                    subAsset.Key,
                    _highlightedSubAssetKey,
                    StringComparison.Ordinal));
            parts.Card.EnableInClassList(ZepUiClasses.DropTarget, false);
            TryPlayPendingSubAssetPing(parts);
        }

        // The group container is painted one segment per tile — the parent's,
        // then one per child — each reaching through the cell gap to the next.
        // starts/ends round the container's outer corners; continues adds the
        // notch a wrapped row re-opens with.
        private static void ApplyGroupSegmentClasses(
            VisualElement item,
            bool grouped,
            bool starts,
            bool ends,
            bool continues)
        {
            item.EnableInClassList(ZepUiClasses.AssetGridItemGrouped, grouped);
            item.EnableInClassList(ZepUiClasses.AssetGridSubAssetSegmentStart, starts);
            item.EnableInClassList(ZepUiClasses.AssetGridSubAssetSegmentEnd, ends);
            item.EnableInClassList(ZepUiClasses.AssetGridSubAssetContinuation, continues);
        }

        private void OnGridMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 0 || IsDisclosureTarget(evt.target as VisualElement))
            {
                return;
            }

            var assetEntry = AssetEntryRenderer.FindAssetEntryInAncestors(
                evt.target as VisualElement,
                _rowListView);
            if (assetEntry != null && evt.clickCount == 2)
            {
                OpenRequested?.Invoke();
            }
        }

        private void OnDragUpdated(DragUpdatedEvent evt) => DragUpdated?.Invoke(evt);
        private void OnDragPerform(DragPerformEvent evt) => DragPerformed?.Invoke(evt);
    }
}
