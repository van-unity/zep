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
    // List-mode IAssetWidget implementation.  Owns the ListView, list header,
    // and list-specific mouse / marquee handling. Finder-style row striping is
    // Unity's own AlternatingRowBackground.All: the stripe is the item wrapper's
    // background (alignment by construction) and the ListView fills the empty
    // tail below the last row with alternating filler rows, recomputed on every
    // Resize/RefreshItems. The stripe colour lives in Zep.uss
    // (#zep-asset-list … --alternative-background).
    internal sealed class ListAssetWidget : VisualElement, IAssetWidget
    {
        // Made internal so AssetEntryRenderer.ResolveAssetEntryUserData can pattern-match it.
        internal sealed class AssetRowParts
        {
            public ZepAssetEntry Entry;
            public VisualElement Row;
            public AssetPingPulse PingPulse;
            public VisualElement IconContainer;
            public VisualElement IconImage;
            public Label Name;
            public Label Secondary;
            public VisualElement Tags;
            public Label Modified;
            public Label Size;
            public Label Type;
            public Label Addressable;
            public VisualElement NameEditContainer;
            public TextField NameField;
            public Label NameFieldExtension;
            // Children the edit-mode USS mute rules hide; the inline-edit
            // controller clears their inline display while this row hosts the
            // active edit (see AssetsAreaInlineEditController.BindRow).
            public VisualElement[] EditMuted;
            // Cached tag-set hash so RebuildTagBadges can short-circuit when
            // the tags didn't change between binds (the dominant case during
            // row recycling within the same folder).
            public int TagSignature;

            // Non-null while this pooled row renders a sub-asset instead of a
            // main asset entry (Entry is null then). Click/drag/selection
            // resolution branches on it.
            public SubAssetDescriptor SubAsset;
            // Hover-reveal-free disclosure chevron. Present on every row as a
            // fixed-width slot (visibility-hidden when the asset has no
            // sub-assets) so name columns stay aligned across rows.
            public Button Disclosure;

            // The visible index this pooled row is currently bound to, so a
            // path-scoped refresh can name the row without walking the
            // ListView. -1 while the row has never been bound.
            public int BoundVisibleIndex = -1;

            // Cached fields for dirty checking
            public string CachedPath;
            // Reused while CachedPath and the previously bound descriptor's
            // name match the next sub-asset.
            public string CachedSubTooltip;
            public DateTime? CachedModifiedDate;
            public long? CachedSize;
            public string CachedTypeName;
            public bool CachedIsAddressable;
            // The icon is dirty-checked separately from the fields above: the
            // async preview upgrade swaps the repository's cached texture for a
            // path without any other field changing, so an icon compare inside
            // the main needsUpdate block would never apply the upgrade.
            public Texture2D CachedIcon;
            // Folder icon tint tracks the asset's tag color. It changes on
            // tag mutation without any other cached field changing, so it must
            // participate in the dirty check or a re-tagged folder keeps its
            // stale icon color until a full list rebuild.
            public ZepTagColor CachedFolderTintColor;
            // Whether the last bind rendered this row as the inline-edit row.
            // Lets the bind path skip the inline-edit restore entirely in the
            // steady state and still run it exactly once on each transition.
            public bool CachedIsEditRow;

            // Lazily allocated sub-asset chip; pooled main rows hide it again.
            public Label StatusChip;
        }

        // Read-only IList over the visible rows (main assets interleaved with
        // the expanded sub-asset rows) plus two synthesized tail rows. The +2
        // count makes the ListView realize two extra rows so the marquee has a
        // rows-shaped landing area below the last real row (see the dummy
        // branch in BindAssetRow). A facade instead of a copy keeps the
        // BrowseProjection's lazy wrapper un-materialized: a copy loop would
        // force every entry into existence per refresh. Items are either
        // ZepAssetEntry (mains + dummies) or SubAssetDescriptor.
        private sealed class VisibleRowsSource : IList
        {
            internal const int DummyRowCount = 2;

            // BindAssetRow never reads the dummy indices, but ListView selection
            // internals may; a shared empty entry keeps them path-empty, which
            // every selection consumer already filters out.
            private static readonly ZepAssetEntry DummyEntry =
                new ZepAssetEntry(string.Empty, string.Empty, string.Empty);

            private readonly ListAssetWidget _owner;

            internal VisibleRowsSource(ListAssetWidget owner)
            {
                _owner = owner;
            }

            public int Count => _owner.VisibleItemCount + DummyRowCount;
            public bool IsReadOnly => true;
            public bool IsFixedSize => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;

            public object this[int index]
            {
                get => index < _owner.VisibleItemCount ? _owner.RowAtVisibleIndex(index) : DummyEntry;
                set => throw new NotSupportedException();
            }

            // ListView reaches for these only through the selection APIs that
            // take items rather than indices, and the widget drives selection
            // exclusively by index while the control stays at
            // SelectionType.None. A linear scan with a boxed Equals over every
            // visible row is the wrong answer to give a caller that got here:
            // throwing says the seam does not exist instead of silently paying
            // O(rows) per lookup.
            public int IndexOf(object value) => throw new NotSupportedException(
                "VisibleRowsSource is index-addressed only; ListView selection is driven by index.");

            public bool Contains(object value) => throw new NotSupportedException(
                "VisibleRowsSource is index-addressed only; ListView selection is driven by index.");

            public IEnumerator GetEnumerator()
            {
                for (var i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }

            public void CopyTo(Array array, int index)
            {
                for (var i = 0; i < Count; i++)
                {
                    array.SetValue(this[i], index + i);
                }
            }

            public int Add(object value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public void Insert(int index, object value) => throw new NotSupportedException();
            public void Remove(object value) => throw new NotSupportedException();
            public void RemoveAt(int index) => throw new NotSupportedException();
        }

        private readonly UnityZepIconRepository _iconRepository;
        private readonly AssetsAreaInlineEditController _inlineEditController;
        // Called per-row at bind time to fetch modified-date / size / IsAddressable.
        // Nullable for the no-controller substrate cases (preview seed, tests).
        private readonly Func<string, string, AssetPresentationData> _presentationLookup;

        private readonly VisualElement _listHeader;
        // Trails the header's columns and mirrors the width of the list's
        // vertical scroller: the ScrollView gives the scroller its own column
        // beside the viewport, so rows are narrower than the header by that
        // width whenever it shows and the fixed-width columns would otherwise
        // sit left of their headers. Measured geometry, hence an inline write.
        private readonly VisualElement _headerScrollerInset;
        private readonly ListView _listView;
        // One facade for the widget's lifetime. Its Count is dynamic
        // (VisibleItemCount), so a publish only has to RefreshItems() — the
        // refresh re-sizes the content for the new Count. Re-assigning a fresh
        // instance instead made the itemsSource setter fire its own
        // RefreshItems() on the reference change, so every publish bound the
        // visible window twice.
        private readonly VisibleRowsSource _rowSource;
        private ScrollView _listScrollView;

        // The visible cell order — the published assets with every open
        // sub-asset disclosure's children spliced in — and the published order
        // itself. Shared with the grid widget and owned by the substrate
        // (audit §6.1b), so an expansion opened in one mode is already in place
        // in the other. A widget built without one (widget-level tests) makes
        // its own, which is what keeps a substrate-less chevron working.
        // Held as IReadOnlyList so the BrowseProjection's lazy wrapper flows
        // straight to the ListView's itemsSource — no eager 30k-entry copy on
        // every keystroke.
        private readonly VisibleOrderProjection<SubAssetDescriptor> _order;
        private IReadOnlyList<ZepAssetEntry> Assets => _order.Assets;
        // O(1) membership lookups on the visible-row sync path, which runs on
        // every selection push and every marquee move. Selection ORDER lives in
        // the substrate; this widget only needs membership.
        //
        // Normally this IS the substrate's canonical set, held by reference so
        // a push costs nothing (audit §5.1). That reference is read-only here:
        // the marquee preview mutates the mirror, so it takes its own copy
        // first — see EnsureOwnedSelectionSet.
        private HashSet<AssetPath> _selectedPathsSet = new HashSet<AssetPath>();
        private bool _ownsSelectionSet = true;
        // How many times this widget has materialized a selection set of its
        // own. Read by tests only; a selection push must not build one.
        internal static int SelectionSetBuildsForTests;

        // Every row ever produced by MakeAssetRow. ListView pools rows rather
        // than destroying them, so this is the realized window plus the pool
        // tail (~visible+2). Selection/marquee sweeps iterate this instead of
        // Query<VisualElement>() walking every descendant label and icon.
        //
        // Detached rows are SKIPPED, never removed: ListView may shelve a
        // pooled row (detach) and re-attach it later without calling makeItem
        // again, so removal would lose track of a live row. The registry is
        // cleared only where the pool actually dies — the Rebuild() call in
        // ApplyResponsiveItemHeights — after which makeItem repopulates it.
        private readonly List<AssetRowParts> _rowPool = new List<AssetRowParts>();
        private string _pendingPingPath = string.Empty;
        // A ping that targets one SUB-asset row (set with its parent's path so
        // the deferred scroll can re-find the row once the catalog resolves).
        // Mutually exclusive with _pendingPingPath — a new ping of either kind
        // displaces the other.
        private string _pendingPingSubAssetParentPath = string.Empty;
        private string _pendingPingSubAssetKey = string.Empty;

        // The one locally-highlighted sub-asset row (sub-assets never join the
        // substrate's path-based multi-selection). Cleared by any non-empty
        // main-selection push.
        private string _highlightedSubAssetKey = string.Empty;
        // The folder row currently highlighted as a drag-over drop target.
        // Transient: pushed by the composing feature during a Unity
        // DragAndDrop hover, empty outside drags.
        private string _dropTargetFolderPath = string.Empty;

        public event Action OpenRequested;
        public event Action<DragUpdatedEvent> DragUpdated;
        public event Action<DragPerformEvent> DragPerformed;
        // A folder row's chevron was clicked. Folder expansion state lives in
        // pane state because expanded children join the real path-based
        // selection, so the raise travels up and the new flattened list comes
        // back via SetAssets.
        public event Action<string> FolderExpansionToggled;
        // A sub-asset disclosure should open (true) or close (false) — the row
        // chevron, and the prune of an expansion whose parent resolved to no
        // sub-assets at all. The substrate owns the open set, so the same round
        // trip applies one level lower: the raise goes up, the shared
        // projection is reconciled against the new set, and RefreshVisibleOrder
        // comes back down.
        public event Action<string, bool> SubAssetExpansionRequested;

        // One indent level for expanded-folder children, applied as computed
        // margin on the row's fixed disclosure slot so the date/size/type
        // columns stay aligned. Finder-calibre step (~16px), deliberately
        // smaller than the sub-asset row's 26px USS padding-left — one level
        // must read as a nudge, not a jump, in deep trees.
        private const float IndentPerDepth = 16f;

        // Tag dots shown per row before an overflow "+N" takes over.
        private const int MaxVisibleTagDots = 3;

        private static string JoinTagNames(IReadOnlyList<ZepTag> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return string.Empty;
            }

            var names = new string[tags.Count];
            for (var i = 0; i < tags.Count; i++)
            {
                names[i] = tags[i].Name;
            }

            return string.Join(", ", names);
        }

        // Finder's "--" for a main row that has no byte size (a folder): an
        // empty Size cell between a filled date and a filled type read as
        // missing data. Sub-asset rows keep both cells empty — they carry no
        // date either, so a lone dash there would be the odd one out.
        internal const string NoSizeText = "\u2014";

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        internal ListAssetWidget(
            UnityZepIconRepository iconRepository,
            AssetsAreaInlineEditController inlineEditController,
            Func<string, string, AssetPresentationData> presentationLookup,
            VisibleOrderProjection<SubAssetDescriptor> visibleOrder = null)
        {
            _iconRepository = iconRepository ?? throw new ArgumentNullException(nameof(iconRepository));
            _inlineEditController = inlineEditController ?? throw new ArgumentNullException(nameof(inlineEditController));
            _presentationLookup = presentationLookup;
            _order = visibleOrder ?? SubAssetCatalog.NewVisibleOrder();

            name = "zep-list-asset-view";
            AddToClassList(ZepUiClasses.ListAssetView);

            _listHeader = BuildListHeader();
            _headerScrollerInset = new VisualElement { name = "zep-asset-list-header-scroller-inset" };
            _listHeader.Add(_headerScrollerInset);
            Add(_listHeader);

            // SelectionType.None: selection is owned by the substrate and
            // committed by AssetDragPrepareInteraction / MarqueeInteraction —
            // the same path grid mode uses. Running ListView's native selection
            // in parallel meant a second bookkeeping system kept in sync via an
            // O(assets) path→index scan on every selection push.
            // AlternatingRowBackground.All is the Finder behaviour: odd rows get
            // the --alternative-background USS class, and the viewport tail past
            // the last row is filled with alternating filler rows that continue
            // the parity (the TailPaddedAssets dummies count as rows, so the
            // pattern stays seamless across them).
            _rowSource = new VisibleRowsSource(this);
            _listView = new ListView
            {
                name = "zep-asset-list",
                fixedItemHeight = AssetsAreaConstants.DefaultAssetRowHeight,
                selectionType = SelectionType.None,
                showAlternatingRowBackgrounds = AlternatingRowBackground.All,
                itemsSource = _rowSource
            };
            _listView.AddToClassList(ZepUiClasses.ListSurface);
            _listView.makeItem = MakeAssetRow;
            _listView.bindItem = BindAssetRow;
            _listView.unbindItem = UnbindAssetRow;
            _listView.RegisterCallback<MouseDownEvent>(OnListMouseDown);
            _listView.RegisterCallback<DragUpdatedEvent>(OnDragUpdated, TrickleDown.TrickleDown);
            _listView.RegisterCallback<DragPerformEvent>(OnDragPerform, TrickleDown.TrickleDown);
            Add(_listView);

            ScrollView.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => SyncHeaderScrollerInset());

            // The catalog resolves sub-asset lists asynchronously; a drained
            // batch means the rows it names may need their disclosure arrow
            // (or an expanded row its sub rows), so rebind those rows.
            RegisterCallback<AttachToPanelEvent>(_ => SubAssetCatalog.Changed += OnSubAssetCatalogChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => SubAssetCatalog.Changed -= OnSubAssetCatalogChanged);
        }

        // -----------------------------------------------------------------------
        // IAssetWidget
        // -----------------------------------------------------------------------

        void IAssetWidget.SetAssets(IReadOnlyList<ZepAssetEntry> assets)
        {
            using var measurement = ZepPerfLog.Measure(
                "asset-list",
                ZepPerfStage.AssetListRefresh);
            var __sw = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            _order.SetAssets(assets);
            // The pin the projection just dropped (its row left the order) is
            // this widget's pending ping: it can never complete now.
            if (_pendingPingSubAssetKey.Length > 0 && _order.PinnedParentPath.Length == 0)
            {
                ClearPendingSubAssetPing();
            }

            // A re-published projection is not automatically a different list:
            // a filtered folder builds fresh entries on every publish, and a
            // sub-asset commit provokes a publish of its own by clearing the
            // pane selection. Dropping the highlight unconditionally left the
            // view with nothing painted at all — a committed sub-asset holds
            // no path, so the row selection is empty by design. Keep it exactly
            // as long as the row it points at survived the rebuild; -1 from the
            // sub-only cursor means it did not (navigation, a collapsed parent,
            // an invalidated catalog).
            if (ResolveCursorVisibleIndex(-1) < 0)
            {
                _highlightedSubAssetKey = string.Empty;
            }

            // An empty folder keeps the SAME source shape as a one-file folder
            // (the one VisibleRowsSource always adds its two tail dummies) and
            // the ListView stays displayed. Swapping to an empty source and
            // hiding the view is what made empty folders render as a blank
            // slab: with no ListView there is nothing for
            // AlternatingRowBackground.All to stripe, so the Finder bands
            // stopped at the folder boundary.
            //
            // RefreshItems rebinds the visible rows against the (unchanged)
            // facade and re-sizes the content for its new Count, without
            // destroying and recreating row VisualElements. Only the visible
            // window's bindItem fires — for a 30k search result that means ~20
            // entry materializations from the lazy wrapper rather than 30k.
            _listView.RefreshItems();
            if (__sw != null)
            {
                __sw.Stop();
                ZepPerfLog.Log($"[Zep/perf]   ListAssetWidget.SetAssets type={Assets.GetType().Name} count={Assets.Count} refreshMs={__sw.Elapsed.TotalMilliseconds:F2}");
            }
        }

        // -----------------------------------------------------------------------
        // Sub-asset expansion projection
        // -----------------------------------------------------------------------

        // Visible rows = main assets + the sub rows of every expanded asset.
        // The order and the spans live in the shared VisibleOrderProjection
        // (audit §6.1b); everything below forwards to it, and the row/pixel
        // layer is what stays here.
        public int VisibleItemCount => _order.VisibleItemCount;

        // Either the main ZepAssetEntry or the SubAssetDescriptor at
        // a visible index. Sub rows sit directly below their parent row.
        private object RowAtVisibleIndex(int visibleIndex)
        {
            var assetIndex = _order.ResolveVisibleIndex(visibleIndex, out var sub);
            return sub ?? (object)Assets[assetIndex];
        }

        // Maps a visible index back to its main-asset index; subAsset is
        // non-null (and the return value the parent's index) for sub-asset rows.
        public int ResolveVisibleIndex(int visibleIndex, out SubAssetDescriptor subAsset)
            => _order.ResolveVisibleIndex(visibleIndex, out subAsset);

        // A main asset's position in the visible order.
        private int AssetIndexToVisibleIndex(int assetIndex)
            => _order.AssetIndexToVisibleIndex(assetIndex);

        // Where the keyboard cursor sits in the visible row order: on the
        // highlighted sub row when a sub-asset is the current selection (it has
        // no path of its own, so the substrate's lead cannot point at it),
        // otherwise on the lead asset's own row. -1 when neither resolves.
        public int ResolveCursorVisibleIndex(int leadAssetIndex)
            => _order.ResolveCursorVisibleIndex(leadAssetIndex, _highlightedSubAssetKey);

        // The shared order changed under this widget (the substrate reconciled
        // it against a new open set): re-bind, because the rows below a changed
        // expansion are renumbered and the bound visible index every realized
        // row carries is stale until they do.
        public void RefreshVisibleOrder() => RefreshVisibleRows();

        // Programmatic disclosure (the ping-a-sub-asset reveal): idempotent,
        // and it kicks the catalog itself — bind-time GetOrRequest only runs
        // for rows inside the visible window, and the parent row may not be
        // realized yet when the reveal lands.
        public void ExpandSubAssets(string assetPath)
        {
            if (_order.Expand(assetPath))
            {
                RefreshVisibleRows();
            }
        }

        // The other direction, and the read behind it: the keyboard disclosure
        // keys are directional (→ opens, ← closes), so they ask before acting
        // rather than toggling blind the way the chevron does.
        internal bool IsSubAssetExpanded(string assetPath) => _order.IsExpanded(assetPath);

        internal void CollapseSubAssets(string assetPath)
        {
            if (_order.Collapse(assetPath))
            {
                RefreshVisibleRows();
            }
        }

        // Rebinds after anything that changes the visible row shape (expansion
        // toggle, catalog resolve/invalidate). The one itemsSource facade reads
        // its Count from the projection, so the refresh alone picks up the new
        // row extent.
        private void RefreshVisibleRows()
        {
            _listView.RefreshItems();
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

            // A resolve can change the row SHAPE (an expanded parent gains or
            // loses its sub rows, or resolves to none at all and closes), and
            // every index below it then moves — only a full refresh is correct.
            // Otherwise rebind just the rows the batch names.
            var pruned = _order.RefreshResolvedSubs(out var shapeChanged);
            if (shapeChanged || resolvedPaths == null || resolvedPaths.Count == 0)
            {
                _listView.RefreshItems();
            }
            else
            {
                RefreshRowsForPaths(resolvedPaths);
            }

            TryFinishPendingSubAssetPing();

            // The prune decided here, but the MEMBERSHIP it drops belongs to
            // the substrate — raised after the refresh, or the next push would
            // splice the empty expansion straight back in. Raised by index: the
            // projection's list is only cleared by the next RefreshResolvedSubs.
            for (var i = 0; i < pruned.Count; i++)
            {
                SubAssetExpansionRequested?.Invoke(pruned[i], false);
            }
        }

        // Rebinds the realized rows whose asset path — or, for a sub row, whose
        // parent's path — the batch names. The batch is short (the catalog
        // drains a handful of paths per raise; an import batch is the outlier),
        // and the realized window is ~20 rows, so a linear membership test
        // beats building a set per raise.
        private void RefreshRowsForPaths(IReadOnlyList<string> paths)
        {
            for (var i = 0; i < _rowPool.Count; i++)
            {
                var parts = _rowPool[i];
                if (parts.Row.panel == null || parts.BoundVisibleIndex < 0)
                {
                    continue;
                }

                var path = parts.SubAsset != null
                    ? parts.SubAsset.ParentPath
                    : parts.Entry?.Path;
                if (string.IsNullOrEmpty(path) || !ContainsPath(paths, path))
                {
                    continue;
                }

                _listView.RefreshItem(parts.BoundVisibleIndex);
            }
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

        // A resolve batch can materialize the pinged sub row for the first
        // time — and possibly below the viewport, since the expansion grew the
        // list under the parent. Re-run the precise scroll; the bind that
        // realizes the row plays the bloom.
        private void TryFinishPendingSubAssetPing()
        {
            if (string.IsNullOrEmpty(_pendingPingSubAssetKey))
            {
                return;
            }

            ScrollToPendingSubAssetRow();
            PlayPendingPingIfRealized();
        }

        // The widget-local sub-asset selection highlight. Passed through the
        // collection view so the drag-prepare FSM can preview/commit it.
        public void SetSubAssetHighlight(string subAssetKey)
        {
            var next = subAssetKey ?? string.Empty;
            if (string.Equals(_highlightedSubAssetKey, next, StringComparison.Ordinal))
            {
                return;
            }

            _highlightedSubAssetKey = next;
            SyncVisibleRowSelection();
        }

        // The drag-over drop-target highlight for folder rows. Same sweep
        // shape as SyncVisibleRowSelection: realized rows toggle immediately,
        // recycled rows reconcile at bind time.
        public void SetDropTargetFolderPath(string folderPath)
        {
            var next = folderPath ?? string.Empty;
            if (string.Equals(_dropTargetFolderPath, next, StringComparison.Ordinal))
            {
                return;
            }

            _dropTargetFolderPath = next;
            for (var i = 0; i < _rowPool.Count; i++)
            {
                var parts = _rowPool[i];
                if (parts.Row.panel == null) continue;
                parts.Row.EnableInClassList(
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

        // Resolve a pointer target to the sub-asset row it landed on, if any.
        public bool TryGetSubAssetFromTarget(VisualElement target, out SubAssetDescriptor subAsset)
        {
            subAsset = null;
            var current = target;
            while (current != null && current != _listView && current != this)
            {
                if (current.userData is AssetRowParts parts && parts.SubAsset != null)
                {
                    subAsset = parts.SubAsset;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        // True when the target sits on a disclosure chevron: the chevron owns
        // the click (expansion toggle), so selection and drag must not react.
        public bool IsDisclosureTarget(VisualElement target)
        {
            var current = target;
            while (current != null && current != _listView && current != this)
            {
                if (current.ClassListContains(ZepUiClasses.AssetRowDisclosureButton))
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
                    foreach (var p in paths)
                    {
                        if (!p.IsEmpty)
                        {
                            owned.Add(p);
                        }
                    }
                }

                _selectedPathsSet = owned;
                _ownsSelectionSet = true;
            }

            // A real main selection displaces the sub-asset highlight. An
            // empty push does NOT: committing a sub-asset selection clears the
            // substrate selection first, and that clear must not wipe the sub
            // highlight it accompanies.
            if (_selectedPathsSet.Count > 0)
            {
                _highlightedSubAssetKey = string.Empty;
            }

            SyncVisibleRowSelection();
        }

        void IAssetWidget.NotifyIconPreviewsReady(IReadOnlyList<string> paths)
        {
            if (_listView.itemsSource == null || IsHidden)
            {
                return;
            }

            // An icon upgrade never moves a row, so the named rows are the
            // only ones whose bind can produce a different pixel.
            if (paths == null || paths.Count == 0)
            {
                _listView.RefreshItems();
            }
            else
            {
                RefreshRowsForPaths(paths);
            }
        }

        void IAssetWidget.ApplyResponsiveItemHeights(int gridItemSize, int minSize, int maxSize)
        {
            var t = (gridItemSize - minSize) / (float)(maxSize - minSize);
            var rowHeight = Mathf.Round(Mathf.Lerp(18f, 32f, t));
            if (!Mathf.Approximately(_listView.fixedItemHeight, rowHeight))
            {
                _listView.fixedItemHeight = rowHeight;
                // Rebuild discards the row pool; drop the registry so it
                // tracks only the rows makeItem is about to recreate.
                _rowPool.Clear();
                _listView.Rebuild();
            }
        }

        bool IAssetWidget.TryGetAssetEntryFromTarget(VisualElement target, out ZepAssetEntry assetEntry)
        {
            assetEntry = AssetEntryRenderer.FindAssetEntryInAncestors(target, _listView);
            return assetEntry != null;
        }

        public void ScrollPathIntoView(string path)
        {
            var index = FindPathIndex(path);
            if (index >= 0)
            {
                _listView.ScrollToItem(AssetIndexToVisibleIndex(index));
            }
        }

        // Scroll one visible row — main or sub — into view. The keyboard
        // cursor's counterpart to ScrollPathIntoView: a sub row has no path
        // to look up, so it is addressed by its visible index.
        public void ScrollVisibleIndexIntoView(int visibleIndex)
        {
            if (visibleIndex >= 0 && visibleIndex < VisibleItemCount)
            {
                _listView.ScrollToItem(visibleIndex);
            }
        }

        public void PingPath(string path)
        {
            var index = FindPathIndex(path);
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/ping] list PingPath path={path} index={index} realizedRows={_rowPool.Count}");
            if (index < 0)
            {
                return;
            }

            ClearPendingSubAssetPing();
            _pendingPingPath = path;
            _listView.ScrollToItem(AssetIndexToVisibleIndex(index));
            PlayPendingPingIfRealized();
        }

        // Scroll to and bloom one sub-asset row (a ping that resolved to a
        // sub-asset). The parent is expanded by the caller (see
        // AssetCollectionView.PingSubAsset); while the catalog hasn't resolved
        // the parent yet there is no sub row to target, so the parent row is
        // framed and the pending key finishes the precise scroll + bloom when
        // the resolved rows arrive (OnSubAssetCatalogChanged).
        public void PingSubAsset(string parentPath, string subAssetKey)
        {
            if (FindPathIndex(parentPath) < 0 || string.IsNullOrEmpty(subAssetKey))
            {
                return;
            }

            _pendingPingPath = string.Empty;
            _pendingPingSubAssetParentPath = parentPath;
            _pendingPingSubAssetKey = subAssetKey;
            // Hold the parent's disclosure open until the target arrives or the
            // catalog answers without it (the projection's pin, §6.1b).
            _order.PinnedParentPath = parentPath;
            ScrollToPendingSubAssetRow();
            PlayPendingPingIfRealized();
        }

        // Scroll for the pending sub ping: to the exact sub row when the
        // parent's expansion span is resolved, else to the parent row.
        private void ScrollToPendingSubAssetRow()
        {
            var visibleIndex = _order.ResolvePingVisibleIndex(
                _pendingPingSubAssetParentPath,
                _pendingPingSubAssetKey);
            if (visibleIndex >= 0)
            {
                _listView.ScrollToItem(visibleIndex);
            }
        }

        private void ClearPendingSubAssetPing()
        {
            _pendingPingSubAssetParentPath = string.Empty;
            _pendingPingSubAssetKey = string.Empty;
            _order.PinnedParentPath = string.Empty;
        }

        // Path-only scan: avoids forcing the lazy BrowseProjection wrapper to
        // materialize every ZepAssetEntry skipped during a reveal.
        private int FindPathIndex(string path) => _order.FindPathIndex(path);

        private void PlayPendingPingIfRealized()
        {
            for (var i = 0;
                i < _rowPool.Count
                    && (!string.IsNullOrEmpty(_pendingPingPath)
                        || !string.IsNullOrEmpty(_pendingPingSubAssetKey));
                i++)
            {
                TryPlayPendingPing(_rowPool[i]);
            }
        }

        private void TryPlayPendingPing(AssetRowParts parts)
        {
            if (parts.Row.panel == null)
            {
                return;
            }

            var isPendingMainRow = !string.IsNullOrEmpty(_pendingPingPath)
                && parts.Entry != null
                && string.Equals(parts.Entry.Path, _pendingPingPath, StringComparison.Ordinal);
            var isPendingSubRow = !string.IsNullOrEmpty(_pendingPingSubAssetKey)
                && parts.SubAsset != null
                && string.Equals(parts.SubAsset.Key, _pendingPingSubAssetKey, StringComparison.Ordinal);
            if (!isPendingMainRow && !isPendingSubRow)
            {
                return;
            }

            _pendingPingPath = string.Empty;
            ClearPendingSubAssetPing();
            if (parts.PingPulse == null)
            {
                parts.PingPulse = new AssetPingPulse();
                parts.Row.Add(parts.PingPulse);
            }

            parts.PingPulse.Play();
        }

        // -----------------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------------

        public float FixedItemHeight => _listView.fixedItemHeight;

        // Below this pitch a row has no room for the metadata line under the
        // name, so Zep.uss's `display: none` default stands.
        private const float SecondaryLineMinRowHeight = 30f;

        private bool ShowsSecondaryLine => _listView.fixedItemHeight >= SecondaryLineMinRowHeight;

        // Inner ScrollView surfaced for marquee auto-scroll and scroll-offset
        // reads. ListView creates it lazily on first geometry, so resolve and
        // cache on demand.
        public ScrollView ScrollView
        {
            get
            {
                if (_listScrollView == null && _listView != null)
                {
                    _listScrollView = _listView.Q<ScrollView>();
                }
                return _listScrollView;
            }
        }

        // Row cells span the viewport; the header spans the whole list. The
        // difference at the right edge is the scroller (zero while hidden).
        private void SyncHeaderScrollerInset()
        {
            var inset = _listView.worldBound.xMax - ScrollView.contentViewport.worldBound.xMax;
            if (float.IsNaN(inset) || inset < 0f)
            {
                inset = 0f;
            }

            _headerScrollerInset.style.minWidth = inset;
            _headerScrollerInset.style.width = inset;
        }

        internal void ShowListHeader(bool show)
        {
            _listHeader.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // -----------------------------------------------------------------------
        // Marquee support (called via AssetCollectionView's mode dispatch)
        // -----------------------------------------------------------------------

        // Append the paths of all rows whose world-space bounds intersect worldRect
        // into appendTo.  Uses row-index math keyed off the internal ScrollView's
        // vertical offset — no per-row WorldBound reads, no allocations.
        //
        // Deliberately NOT clipped to the viewport: during marquee auto-scroll
        // the rect's anchored edge extends past the viewport (MarqueeInteraction
        // shifts the start point by the scroll delta), and rows swept there must
        // stay hit after they leave the realized window. The index clamps below
        // already bound the math to real rows.
        public MarqueeHitRegion ComputeMarqueeHitRegion(Rect worldRect)
        {
            if (Assets.Count == 0) return MarqueeHitRegion.Empty;

            var rowHeight = _listView.fixedItemHeight;
            if (rowHeight <= 0f) return MarqueeHitRegion.Empty;

            var scrollView    = ScrollView;
            var scrollOffset  = scrollView != null ? scrollView.scrollOffset.y : 0f;
            var listTop       = _listView.worldBound.yMin;
            var contentTop    = worldRect.yMin - listTop + scrollOffset;
            var contentBottom = worldRect.yMax - listTop + scrollOffset;

            // Index math runs over the VISIBLE row order (mains + expanded sub
            // rows). Sub rows occupy geometry but never join the path-based
            // selection, so they are skipped rather than appended.
            var startIndex = Mathf.Max(0, Mathf.FloorToInt(contentTop / rowHeight));
            var endIndex   = Mathf.Min(VisibleItemCount - 1, Mathf.FloorToInt(contentBottom / rowHeight));

            return new MarqueeHitRegion(startIndex, endIndex, 0, 0, 1);
        }

        // Rows are the only cells list mode has, so the visible index IS the
        // row. Sub rows are skipped rather than reported.
        public bool TryGetMarqueePath(int visibleIndex, out string path)
        {
            path = null;
            if (visibleIndex < 0 || visibleIndex >= VisibleItemCount)
            {
                return false;
            }

            var assetIndex = ResolveVisibleIndex(visibleIndex, out var sub);
            if (sub != null || assetIndex < 0 || assetIndex >= Assets.Count)
            {
                return false;
            }

            path = ZepPathHelpers.GetAssetPathAt(Assets, assetIndex);
            return true;
        }

        // A pushed set belongs to the substrate, so the preview takes its own
        // copy before the first local mutation; without that the preview would
        // rewrite the canonical selection the substrate resolves clicks
        // against. One copy per drag: the following moves already own it.
        private void EnsureOwnedSelectionSet()
        {
            if (_ownsSelectionSet) return;
            SelectionSetBuildsForTests++;
            _selectedPathsSet = new HashSet<AssetPath>(
                _selectedPathsSet,
                _selectedPathsSet.Comparer);
            _ownsSelectionSet = true;
        }

        // Lightweight selection preview during a marquee drag: applies the rows
        // that entered and left the rectangle to the local selection mirror and
        // repaints the realized rows. Delta rather than replace — a drag over
        // 40k rows re-hashed all of them on every pointer move.
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
                    var ap = AssetPath.From(added[i]);
                    if (!ap.IsEmpty)
                    {
                        _selectedPathsSet.Add(ap);
                    }
                }
            }

            if (_selectedPathsSet.Count > 0)
            {
                _highlightedSubAssetKey = string.Empty;
            }

            SyncVisibleRowSelection();
        }

        // -----------------------------------------------------------------------
        // Selection push (no event raise)
        // -----------------------------------------------------------------------

        private void SyncVisibleRowSelection()
        {
            for (var i = 0; i < _rowPool.Count; i++)
            {
                var parts = _rowPool[i];
                if (parts.Row.panel == null) continue;

                if (parts.SubAsset != null)
                {
                    parts.Row.EnableInClassList(
                        ZepUiClasses.RowSelected,
                        string.Equals(parts.SubAsset.Key, _highlightedSubAssetKey, StringComparison.Ordinal));
                    continue;
                }

                if (parts.Entry == null) continue;
                parts.Row.EnableInClassList(
                    ZepUiClasses.RowSelected,
                    _selectedPathsSet.Contains(AssetPath.From(parts.Entry.Path)));
            }
        }

        // -----------------------------------------------------------------------
        // DOM construction
        // -----------------------------------------------------------------------

        private static VisualElement BuildListHeader()
        {
            var header = new VisualElement { name = "zep-asset-list-header" };
            header.AddToClassList(ZepUiClasses.AssetListHeader);
            header.Add(CreateHeaderLabel("Name",        "zep-asset-list-header-name",        ZepUiClasses.AssetColumnName));
            header.Add(CreateHeaderLabel("Date Modified","zep-asset-list-header-modified",   ZepUiClasses.AssetColumnModified));
            header.Add(CreateHeaderLabel("Size",        "zep-asset-list-header-size",        ZepUiClasses.AssetColumnSize));
            header.Add(CreateHeaderLabel("Type",        "zep-asset-list-header-type",        ZepUiClasses.AssetColumnType));
            header.Add(CreateHeaderLabel("Addressable", "zep-asset-list-header-addressable", ZepUiClasses.AssetColumnAddressable));
            return header;
        }

        private static Label CreateHeaderLabel(string text, string name, string columnClass)
        {
            var label = new Label(text) { name = name };
            label.AddToClassList(ZepUiClasses.AssetListHeaderLabel);
            label.AddToClassList(columnClass);
            return label;
        }

        private VisualElement MakeAssetRow()
        {
            var parts = new AssetRowParts();
            var row = new VisualElement { name = "zep-asset-row" };
            row.AddToClassList(ZepUiClasses.AssetRow);
            row.userData = parts;
            _rowPool.Add(parts);

            var nameColumn = new VisualElement { name = "zep-asset-row-name-column" };
            nameColumn.AddToClassList(ZepUiClasses.AssetColumnName);
            nameColumn.AddToClassList(ZepUiClasses.AssetNameCell);

            // Fixed-width disclosure slot on every row: visibility-hidden when
            // the asset has no sub-assets so name columns stay aligned. Clicks
            // toggle expansion only — StopPropagation keeps the row's
            // select/drag FSM out of it (see IsDisclosureTarget for the
            // capture-phase guard).
            var disclosure = new Button { name = "zep-asset-row-disclosure", focusable = false, text = string.Empty };
            disclosure.AddToClassList(ZepUiClasses.AssetRowDisclosureButton);
            disclosure.AddToClassList(ZepUiClasses.FlatButton);
            disclosure.AddToClassList(ZepUiClasses.ThemedIcon);
            disclosure.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            disclosure.clicked += () =>
            {
                if (!(disclosure.userData is string assetPath) || string.IsNullOrEmpty(assetPath))
                {
                    return;
                }

                if (parts.Entry != null && parts.Entry.IsFolder)
                {
                    FolderExpansionToggled?.Invoke(assetPath);
                }
                else
                {
                    // The open set is the substrate's, so the chevron asks
                    // rather than toggles: the answer arrives back as a push
                    // and reaches the grid widget with it.
                    SubAssetExpansionRequested?.Invoke(
                        assetPath,
                        !_order.IsExpanded(assetPath));
                }
            };
            nameColumn.Add(disclosure);

            var icon = new VisualElement { name = "zep-asset-row-icon" };
            icon.AddToClassList(ZepUiClasses.RowIcon);
            var iconImage = new VisualElement { name = "zep-asset-row-icon-image" }
                .WithClass(ZepUiClasses.RowIconImage);
            icon.Add(iconImage);
            nameColumn.Add(icon);

            var nameContent = new VisualElement { name = "zep-asset-row-content" };
            nameContent.AddToClassList(ZepUiClasses.RowContent);
            nameContent.AddToClassList(ZepUiClasses.AssetNameContent);
            var nameLabel = new Label { name = "zep-asset-row-name" }
                .WithClass(ZepUiClasses.RowPrimaryText);
            // The TextField itself (a heavyweight composite) is created on
            // demand by EnsureRowEditField the first time this row actually
            // hosts an inline edit — at most one row app-wide ever does.
            var nameEditContainer = new VisualElement { name = "zep-asset-row-name-edit" };
            nameEditContainer.style.display = DisplayStyle.None;
            var tags = new VisualElement { name = "zep-asset-row-tags" }
                .WithClass(ZepUiClasses.RowBadges)
                .WithClass(ZepUiClasses.AssetRowTags);
            var secondary = new Label { name = "zep-asset-row-secondary" }
                .WithClass(ZepUiClasses.RowSecondaryText)
                .WithClass(ZepUiClasses.AssetRowSecondary);
            nameContent.Add(nameLabel);
            nameContent.Add(nameEditContainer);
            nameContent.Add(tags);
            nameContent.Add(secondary);
            nameColumn.Add(nameContent);

            var modified = new Label { name = "zep-asset-row-modified" };
            modified.AddToClassList(ZepUiClasses.AssetColumnModified);
            modified.AddToClassList(ZepUiClasses.AssetCellText);

            var size = new Label { name = "zep-asset-row-size" };
            size.AddToClassList(ZepUiClasses.AssetColumnSize);
            size.AddToClassList(ZepUiClasses.AssetCellText);

            var type = new Label { name = "zep-asset-row-type" };
            type.AddToClassList(ZepUiClasses.AssetColumnType);
            type.AddToClassList(ZepUiClasses.AssetCellText);

            var addressable = new Label { name = "zep-asset-row-addressable" };
            addressable.AddToClassList(ZepUiClasses.AssetColumnAddressable);
            addressable.AddToClassList(ZepUiClasses.AssetAddressableCell);

            parts.Row                = row;
            parts.Disclosure         = disclosure;
            parts.IconContainer      = icon;
            parts.IconImage          = iconImage;
            parts.Name               = nameLabel;
            parts.NameEditContainer  = nameEditContainer;
            parts.Secondary          = secondary;
            parts.Tags               = tags;
            parts.Modified           = modified;
            parts.Size               = size;
            parts.Type               = type;
            parts.Addressable        = addressable;
            parts.EditMuted          = new VisualElement[] { secondary };

            // Runtime geometry derived from the row pitch alone, so it belongs
            // to the row's creation, not to its binding: the pitch can only
            // change through ApplyResponsiveItemHeights, which sets it and then
            // Rebuild()s — dropping this pool and re-running makeItem at the new
            // value. Writing it per bind bought nothing and cost four style
            // writes per row on every publish, keystroke and catalog drain.
            StyleWritesForTests += 4;
            var iconSize = Mathf.RoundToInt(_listView.fixedItemHeight * 0.7f);
            parts.IconImage.style.width      = iconSize;
            parts.IconImage.style.height     = iconSize;
            parts.IconContainer.style.width  = iconSize;
            parts.IconContainer.style.height = iconSize;

            row.Add(nameColumn);
            row.Add(modified);
            row.Add(size);
            row.Add(type);
            row.Add(addressable);
            return row;
        }

        private void EnsureRowEditField(AssetRowParts parts)
        {
            if (parts.NameField != null) return;

            var nameField = new TextField { name = "zep-asset-row-name-field" };
            var nameFieldExtension = new Label { name = "zep-asset-row-name-extension" };
            parts.NameEditContainer.Add(nameField);
            parts.NameEditContainer.Add(nameFieldExtension);
            parts.NameField = nameField;
            parts.NameFieldExtension = nameFieldExtension;

            // Escape at TrickleDown so it beats the inner text element's own
            // handling (restore + blur → FocusOut would commit the edit).
            nameField.RegisterCallback<KeyDownEvent>(evt =>
                _inlineEditController.HandleRowEscapeKeyDown(evt, parts.Entry?.Path),
                TrickleDown.TrickleDown);
            nameField.RegisterCallback<KeyDownEvent>(evt =>
                _inlineEditController.HandleRowKeyDown(evt, parts.Entry?.Path, parts.NameField));
            nameField.RegisterValueChangedCallback(evt =>
                _inlineEditController.HandleRowValueChanged(evt.newValue, parts.Entry?.Path));
            nameField.RegisterCallback<FocusOutEvent>(evt =>
                _inlineEditController.HandleRowFocusOut(parts.Entry?.Path, parts.NameField));
        }

        // A released row gets no follow-up BindRow (unbindItem has no
        // replacement entry), so a shrink/swap that releases the edit row
        // would leave the session alive on a hidden field — the same trap
        // BindRow's stranded-edit guard closes for rebinds. CachedIsEditRow
        // stays set: the next bind of this pooled row must still run the
        // controller funnel to strip the row's edit chrome.
        private void UnbindAssetRow(VisualElement element, int index)
        {
            if (!(element.userData is AssetRowParts parts))
            {
                return;
            }

            parts.BoundVisibleIndex = -1;
            if (parts.CachedIsEditRow)
            {
                _inlineEditController.HandleRowReleased(parts.NameField);
            }
        }

        // How many times the ListView has bound a row, and how many inline
        // style writes the row's icon geometry has performed. Read by tests
        // only; nothing branches on either. A publish must move the bind count
        // by one realized window, not two, and the style-write count not at all.
        internal static int BindCountForTests;
        internal static int StyleWritesForTests;
        // How many times a sub-asset row has built its tooltip string. Unity
        // owns the tooltip PROPERTY (TextElement rewrites it around eliding and
        // the setter compares by value), so the allocation is what a test can
        // see — and a rebind of an unchanged sub-asset must not add one.
        internal static int SubAssetTooltipBuildsForTests;

        private void BindAssetRow(VisualElement element, int index)
        {
            BindCountForTests++;
            var parts = (AssetRowParts)element.userData;
            parts.BoundVisibleIndex = index;

            if (index >= VisibleItemCount)
            {
                parts.Entry = null;
                parts.SubAsset = null;
                parts.Disclosure.style.visibility = Visibility.Hidden;
                ApplyRowIndent(parts, 0);
                element.EnableInClassList(ZepUiClasses.AssetRowSubAsset, false);
                parts.PingPulse?.Reset();

                parts.Name.text = string.Empty;
                parts.Name.tooltip = string.Empty;
                parts.Secondary.text = string.Empty;
                parts.Secondary.tooltip = string.Empty;
                parts.Secondary.style.display = DisplayStyle.None;
                parts.Modified.text = string.Empty;
                parts.Modified.tooltip = string.Empty;
                parts.Size.text = string.Empty;
                parts.Size.tooltip = string.Empty;
                parts.Type.text = string.Empty;
                parts.Type.tooltip = string.Empty;
                parts.Addressable.text = string.Empty;
                parts.Addressable.tooltip = string.Empty;
                parts.IconImage.style.backgroundImage = new StyleBackground();
                parts.IconImage.style.unityBackgroundImageTintColor = StyleKeyword.Null;
                parts.CachedIcon = null;
                parts.TagSignature = 0;
                AssetEntryRenderer.RebuildTagBadges(parts.Tags, null, false, MaxVisibleTagDots);
                parts.Tags.tooltip = string.Empty;

                parts.CachedPath = string.Empty;
                parts.CachedModifiedDate = null;
                parts.CachedSize = null;
                parts.CachedTypeName = string.Empty;
                parts.CachedIsAddressable = false;
                parts.CachedFolderTintColor = ZepTagColor.None;

                if (parts.CachedIsEditRow)
                {
                    _inlineEditController.BindRow(
                        entryPath: string.Empty,
                        nameLabel: parts.Name,
                        editContainer: parts.NameEditContainer,
                        nameField: parts.NameField,
                        extensionLabel: parts.NameFieldExtension,
                        rowRoot: parts.Row,
                        editingUssClass: ZepUiClasses.RowEditing);
                    parts.CachedIsEditRow = false;
                }

                element.EnableInClassList(ZepUiClasses.RowSelected, false);
                element.EnableInClassList(ZepUiClasses.DropTarget, false);
                return;
            }

            var assetIndex = ResolveVisibleIndex(index, out var subAssetAtIndex);
            if (subAssetAtIndex != null)
            {
                // Sub rows inherit their parent's indent; their own one-level
                // shift is the USS sub-asset padding on top of it.
                BindSubAssetRow(element, parts, subAssetAtIndex, RowDepthAt(assetIndex));
                return;
            }

            var row = Assets[assetIndex];
            parts.Entry = row;
            parts.SubAsset = null;
            element.EnableInClassList(ZepUiClasses.AssetRowSubAsset, false);

            var presentation = _presentationLookup != null
                ? _presentationLookup(row.Path, row.TypeName)
                : default;

            bool isSamePath = string.Equals(parts.CachedPath, row.Path, StringComparison.Ordinal);
            if (!isSamePath)
            {
                parts.PingPulse?.Reset();
            }
            bool isSameModifiedDate = parts.CachedModifiedDate == presentation.ModifiedDate;
            bool isSameSize = parts.CachedSize == presentation.Size;
            bool isSameType = string.Equals(parts.CachedTypeName, row.TypeName, StringComparison.Ordinal);
            bool isSameAddressable = parts.CachedIsAddressable == presentation.IsAddressable;
            bool isSameFolderTint = parts.CachedFolderTintColor == row.FolderTintColor;

            bool needsUpdate = !isSamePath || !isSameModifiedDate || !isSameSize || !isSameType || !isSameAddressable || !isSameFolderTint;

            if (needsUpdate)
            {
                parts.Name.text    = row.Name;
                parts.Name.tooltip = row.Path;

                // The secondary line is hidden by USS below a 30px row pitch —
                // most of the thumbnail-size range, including the default. Only
                // build the metadata (a List + a joined string + up to three
                // formatted parts) when the row will actually show it; the
                // one-shot clear keeps a hidden label from holding a stale
                // string, and the row height can only cross 30px through
                // ApplyResponsiveItemHeights, which Rebuild()s every row anyway.
                var showsSecondary = ShowsSecondaryLine;
                parts.Secondary.style.display = showsSecondary ? DisplayStyle.Flex : DisplayStyle.None;
                if (showsSecondary)
                {
                    parts.Secondary.text = BuildCompactMetadata(row, presentation);
                    parts.Secondary.tooltip = row.Path;
                }
                else
                {
                    parts.Secondary.text = string.Empty;
                    parts.Secondary.tooltip = string.Empty;
                }

                var modifiedDateLabel = AssetEntryRenderer.FormatModifiedDate(presentation.ModifiedDate);
                var sizeLabel         = AssetEntryRenderer.FormatSize(presentation.Size);
                parts.Modified.text    = modifiedDateLabel;
                parts.Modified.tooltip = modifiedDateLabel;
                parts.Size.text        = presentation.Size.HasValue ? sizeLabel : NoSizeText;
                parts.Size.tooltip     = sizeLabel;

                var typeLabel = AssetEntryRenderer.ResolveTypeDisplayName(row.TypeName);
                parts.Type.text    = typeLabel;
                parts.Type.tooltip = typeLabel;

                parts.Addressable.text    = presentation.IsAddressable ? "✓" : string.Empty;
                parts.Addressable.tooltip = presentation.IsAddressable ? "Addressable asset" : "Not addressable";

                AssetEntryRenderer.ApplyFolderIconTint(parts.IconImage, row);

                parts.CachedPath = row.Path;
                parts.CachedModifiedDate = presentation.ModifiedDate;
                parts.CachedSize = presentation.Size;
                parts.CachedTypeName = row.TypeName;
                parts.CachedIsAddressable = presentation.IsAddressable;
                parts.CachedFolderTintColor = row.FolderTintColor;
            }
            else
            {
                parts.Secondary.style.display = ShowsSecondaryLine
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }

            // Inline-edit bind runs outside the needsUpdate block: a rename
            // activates on a row whose path/date/size are unchanged, so the
            // main dirty-check can't see the transition. The CachedIsEditRow
            // compare keeps the steady state free — the controller runs only
            // when this row enters or leaves edit mode.
            var isEditRow = _inlineEditController.IsActiveForPath(row.Path);
            if (isEditRow || parts.CachedIsEditRow)
            {
                if (isEditRow)
                {
                    EnsureRowEditField(parts);
                }

                _inlineEditController.BindRow(
                    entryPath: row.Path,
                    nameLabel: parts.Name,
                    editContainer: parts.NameEditContainer,
                    nameField: parts.NameField,
                    extensionLabel: parts.NameFieldExtension,
                    rowRoot: parts.Row,
                    editingUssClass: ZepUiClasses.RowEditing,
                    mutedElements: parts.EditMuted);
                parts.CachedIsEditRow = isEditRow;
            }

            // Icon dirty-check runs outside the needsUpdate block: the async
            // preview upgrade changes the repository's texture for an otherwise
            // unchanged row, and this compare is what makes the PreviewsReady
            // rebind actually show it. On a repository cache hit this is one
            // dictionary lookup plus a reference compare.
            var assetIcon = AssetEntryRenderer.ResolveAssetIcon(_iconRepository, row);
            if (!ReferenceEquals(parts.CachedIcon, assetIcon))
            {
                parts.IconImage.style.backgroundImage = assetIcon != null ? new StyleBackground(assetIcon) : new StyleBackground();
                parts.CachedIcon = assetIcon;
            }

            // Bare colour dots, Finder-style (no label in the row); the names
            // ride on the slot's tooltip and must participate in its signature.
            var tagSignature = AssetEntryRenderer.ComputeTagSignature(row.Tags, true, MaxVisibleTagDots);
            if (parts.TagSignature != tagSignature)
            {
                AssetEntryRenderer.RebuildTagBadges(parts.Tags, row.Tags, false, MaxVisibleTagDots);
                parts.Tags.tooltip = JoinTagNames(row.Tags);
                parts.TagSignature = tagSignature;
            }

            // Unconditional, not inside the signature check: a row recycled
            // from a sub-asset carries signature 0 into an untagged main row,
            // which skips the rebuild — and the chip would stay up on an asset
            // that is not a sub-asset.
            if (parts.StatusChip != null)
            {
                ApplyStatusChip(
                    parts,
                    null,
                    hasTagBadges: (row.Tags?.Count ?? 0) > 0);
            }

            ApplyRowIndent(parts, RowDepthAt(assetIndex));
            BindDisclosure(parts, row, assetIndex);

            element.EnableInClassList(
                ZepUiClasses.RowSelected,
                _selectedPathsSet.Contains(AssetPath.From(row.Path)));
            element.EnableInClassList(
                ZepUiClasses.DropTarget,
                IsDropTargetEntry(row));
            TryPlayPendingPing(parts);
        }

        // Depth of a main row in the flattened expansion order; zero whenever
        // folder expansion isn't active (plain lists, search results).
        private int RowDepthAt(int assetIndex)
        {
            return Assets is ExpandedAssetList expanded ? expanded.DepthAt(assetIndex) : 0;
        }

        // Inline because it is runtime-computed geometry (depth × indent step);
        // StyleKeyword.Null at depth 0 hands the value back to the stylesheet.
        private static void ApplyRowIndent(AssetRowParts parts, int depth)
        {
            parts.Disclosure.style.marginLeft = depth > 0
                ? depth * IndentPerDepth
                : (StyleLength)StyleKeyword.Null;
        }

        // Outside the needsUpdate dirty check on purpose: the catalog resolves
        // asynchronously, so the chevron appears on a row whose other fields
        // are unchanged. On a catalog cache hit this is one dictionary lookup.
        private void BindDisclosure(AssetRowParts parts, ZepAssetEntry row, int assetIndex)
        {
            parts.Disclosure.userData = row.Path;

            // Folder rows: the chevron is inline folder expansion, present only
            // while the flattened expansion order is active (plain browse) and
            // the folder has children to show.
            if (row.IsFolder)
            {
                var showChevron = Assets is ExpandedAssetList expanded
                    && expanded.ShowsChevronAt(assetIndex);
                parts.Disclosure.style.visibility = showChevron ? Visibility.Visible : Visibility.Hidden;
                if (showChevron)
                {
                    parts.Disclosure.style.backgroundImage =
                        AssetEntryRenderer.GetDisclosureChevron(((ExpandedAssetList)Assets).IsExpandedFolderAt(assetIndex));
                }

                return;
            }

            var subs = string.IsNullOrEmpty(row.Path)
                ? null
                : SubAssetCatalog.GetOrRequest(row.Path);
            var hasSubs = subs != null && subs.Length > 0;
            parts.Disclosure.style.visibility = hasSubs ? Visibility.Visible : Visibility.Hidden;
            if (hasSubs)
            {
                parts.Disclosure.style.backgroundImage =
                    AssetEntryRenderer.GetDisclosureChevron(_order.IsExpanded(row.Path));
            }
        }

        // Rendering of one sub-asset row. Always a full rebind: sub rows are
        // few, and skipping the main dirty-check machinery keeps the
        // main↔sub row-recycling transitions trivially correct (CachedPath is
        // set to the sub key, which can never equal a real asset path).
        private void BindSubAssetRow(VisualElement element, AssetRowParts parts, SubAssetDescriptor subAsset, int parentDepth)
        {
            // Reimports can rename a child without changing its local ID.
            // Compare the previous descriptor before replacing it below.
            var isSameSubAsset = string.Equals(parts.CachedPath, subAsset.Key, StringComparison.Ordinal);
            var isSameSubAssetName = string.Equals(parts.SubAsset?.Name, subAsset.Name, StringComparison.Ordinal);

            parts.Entry = null;
            parts.SubAsset = subAsset;
            parts.PingPulse?.Reset();
            ApplyRowIndent(parts, parentDepth);

            parts.Name.text = subAsset.Name;
            if (!isSameSubAsset || !isSameSubAssetName || parts.CachedSubTooltip == null)
            {
                parts.CachedSubTooltip = subAsset.ParentPath + " › " + subAsset.Name;
                SubAssetTooltipBuildsForTests++;
            }

            parts.Name.tooltip = parts.CachedSubTooltip;

            parts.Secondary.text = string.Empty;
            parts.Secondary.tooltip = string.Empty;
            parts.Secondary.style.display = DisplayStyle.None;

            parts.Modified.text = string.Empty;
            parts.Modified.tooltip = string.Empty;
            parts.Size.text = string.Empty;
            parts.Size.tooltip = string.Empty;

            var typeLabel = AssetEntryRenderer.ResolveTypeDisplayName(subAsset.TypeName);
            parts.Type.text = typeLabel;
            parts.Type.tooltip = typeLabel;
            parts.Addressable.text = string.Empty;
            parts.Addressable.tooltip = string.Empty;

            // Null entry: clears both the folder-tint class and the inline
            // tint a recycled folder row would otherwise leave on the icon.
            AssetEntryRenderer.ApplyFolderIconTint(parts.IconImage, null);
            parts.IconImage.style.backgroundImage = subAsset.Icon != null
                ? new StyleBackground(subAsset.Icon)
                : new StyleBackground();
            parts.CachedIcon = subAsset.Icon;

            AssetEntryRenderer.RebuildTagBadges(parts.Tags, null, false, MaxVisibleTagDots);
                parts.Tags.tooltip = string.Empty;
            parts.TagSignature = 0;
            ApplyStatusChip(parts, "Sub-asset", hasTagBadges: false);

            if (parts.CachedIsEditRow)
            {
                _inlineEditController.BindRow(
                    entryPath: string.Empty,
                    nameLabel: parts.Name,
                    editContainer: parts.NameEditContainer,
                    nameField: parts.NameField,
                    extensionLabel: parts.NameFieldExtension,
                    rowRoot: parts.Row,
                    editingUssClass: ZepUiClasses.RowEditing);
                parts.CachedIsEditRow = false;
            }

            parts.Disclosure.userData = null;
            parts.Disclosure.style.visibility = Visibility.Hidden;

            // CachedPath holds the sub key so a later main-row bind on this
            // recycled row always fails the same-path check and fully rebinds.
            parts.CachedPath = subAsset.Key;
            parts.CachedModifiedDate = null;
            parts.CachedSize = null;
            parts.CachedTypeName = subAsset.TypeName;
            parts.CachedIsAddressable = false;
            parts.CachedFolderTintColor = ZepTagColor.None;

            element.EnableInClassList(ZepUiClasses.AssetRowSubAsset, true);
            element.EnableInClassList(
                ZepUiClasses.RowSelected,
                string.Equals(subAsset.Key, _highlightedSubAssetKey, StringComparison.Ordinal));
            element.EnableInClassList(ZepUiClasses.DropTarget, false);
            TryPlayPendingPing(parts);
        }

        // The chip shares the badge container with the tag chips, so the
        // container's display is the OR of both needs — RebuildTagBadges hides
        // it whenever the row has no tags, which is every sub-asset row.
        //
        // Re-attaching rather than assuming: RebuildTagBadges pools that
        // container BY INDEX and replaces any Label it finds inside the range
        // it rebuilds, so a tagged main row can take the chip out of the
        // hierarchy. Coming back to a sub-asset row then puts it back at the
        // end, where the rebuild only ever hides it.
        private static void ApplyStatusChip(
            AssetRowParts parts,
            string chipText,
            bool hasTagBadges)
        {
            var showChip = !string.IsNullOrEmpty(chipText);
            if (showChip && parts.StatusChip == null)
            {
                parts.StatusChip = new Label { name = "zep-asset-list-status-chip" };
                parts.StatusChip.AddToClassList(ZepUiClasses.RowChip);
                parts.StatusChip.AddToClassList(ZepUiClasses.RowChipStatus);
            }

            if (parts.StatusChip != null)
            {
                if (showChip)
                {
                    parts.StatusChip.text = chipText;
                    if (parts.StatusChip.parent != parts.Tags)
                    {
                        parts.Tags.Add(parts.StatusChip);
                    }
                }

                parts.StatusChip.style.display =
                    showChip ? DisplayStyle.Flex : DisplayStyle.None;
            }

            parts.Tags.style.display = showChip || hasTagBadges
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        private static string BuildCompactMetadata(ZepAssetEntry row, AssetPresentationData presentation)
        {
            var parts = new List<string>(4);
            if (presentation.ModifiedDate.HasValue)       parts.Add(AssetEntryRenderer.FormatModifiedDate(presentation.ModifiedDate));
            if (!string.IsNullOrWhiteSpace(row.TypeName)) parts.Add(AssetEntryRenderer.ResolveTypeDisplayName(row.TypeName));
            if (presentation.Size.HasValue)               parts.Add(AssetEntryRenderer.FormatSize(presentation.Size));
            if (presentation.IsAddressable)               parts.Add("Addressable");
            return string.Join(" • ", parts);
        }

        // -----------------------------------------------------------------------
        // Event handlers
        // -----------------------------------------------------------------------

        private void OnListMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 0)
            {
                return;
            }

            // Empty-space clicks are intercepted by MarqueeInteraction upstream
            // and never reach this handler — we only see clicks that landed on
            // a list item.
            if (AssetEntryRenderer.FindAssetEntryInAncestors(evt.target as VisualElement, _listView) == null)
            {
                return;
            }

            if (evt.clickCount == 2)
            {
                OpenRequested?.Invoke();
            }
        }

        private void OnDragUpdated(DragUpdatedEvent evt)   => DragUpdated?.Invoke(evt);
        private void OnDragPerform(DragPerformEvent evt)   => DragPerformed?.Invoke(evt);

    }
}
