using System;
using System.Collections.Generic;
using Zep.Editor.Bridge.FileOperations;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Core.Indexing;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.ContextMenu;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Sidebar
{
    internal sealed class FolderTreeWidget : VisualElement
    {
        // One indent level = the chevron slot (12px glyph + 4px gap), so a
        // child's chevron sits under its parent's folder icon.
        private const float FolderIndentStep = 16f;
        // The 4px visual inset every sidebar row shares is a 1px transparent
        // border plus this padding (.zep-sidebar-row); the border is
        // always there so the keyboard focus ring never shifts the layout.
        private const float FolderRowBasePadding = 3f;
        // The item pitch is the row's whole border box — border 1 + padding 3
        // + 16px content + padding 3 + border 1 — with no margins anywhere
        // (Zep.uss), so fixedItemHeight, the flow layout, and the
        // spacer/window sizing in RefreshList all agree exactly. Any drift
        // between them leaves the ListView convinced it needs its own scroll —
        // ScrollView then force-shows its scroller with an inline display
        // write that no USS rule can override.
        private const float FolderRowPitch = 24f;
        private const int FolderRowOverscan = 2;
        private const int UnresolvedViewportRowLimit = 16;
        // Reveal-scroll retry budget in panel updates: covers the couple of
        // layout passes a grown projection needs before the target row exists
        // and the sidebar's scrollable range is current, while keeping the
        // give-up (row hidden again, user scrolled away) unnoticeable.
        private const int MaxRowScrollAttempts = 10;

        private readonly UnityZepIconRepository _iconRepository;
        // The hierarchy, the expansion set and the visible order live in Core;
        // this widget renders them and feeds user input back.
        private readonly FolderTreeModel _model;
        private readonly ListView _folderList;
        private readonly VisualElement _leadingSpacer;
        private readonly VisualElement _trailingSpacer;
        private readonly SidebarSplitDragPrepareInteraction _splitDrag;
        private readonly Func<string, ZepTagColor> _folderTintQuery;

        // Only the outer-sidebar viewport slice is handed to the content-sized
        // ListView. The spacers preserve the full tree height in the one owning
        // ScrollView, while this bounded source keeps Unity's row pool and every
        // RefreshItems bind proportional to viewport height.
        private readonly List<ZepFolderEntry> _boundFolders =
            new List<ZepFolderEntry>();
        // The expansion set is backed by SessionState so the tree keeps its
        // shape across domain reloads; it resets with the editor session.
        private readonly string _expansionStateKey;
        private bool _hasAppliedPaths;

        // Both paths are root-scoped at set time (empty when outside this
        // tree's root), so plain equality is a complete change signal.
        private string _selectedFolderPath = string.Empty;
        private string _currentFolderPath = string.Empty;
        // The row currently highlighted as a drag-over drop target. Transient
        // per-hover state; the element ref gives O(1) clear on target change,
        // BindFolderRow reconciles when a mid-drag rebuild recycles rows.
        private string _dropTargetFolderPath = string.Empty;
        private VisualElement _dropTargetRow;
        // Memo of the last hovered row's move verdict. Keyed on the HOVERED
        // path, not _dropTargetFolderPath: that field stores empty for a
        // rejected row, which cannot be told apart from "no row hovered".
        private string _hoverVerdictFolderPath;
        private int _hoverVerdictPayloadGeneration;
        private IReadOnlyList<string> _hoverVerdictPaths;
        private bool _hoverVerdictIsMeaningful;
        // Live reveal-scroll retry; a newer reveal pauses and replaces it.
        private IVisualElementScheduledItem _pendingRowScroll;
        private string _pendingRowScrollPath;
        private IVisualElementScheduledItem _pendingHierarchyMaterialization;
        private IVisualElementScheduledItem _pendingViewportRefresh;
        // Coalesces the SessionState write of the expansion set; see SaveExpandedState.
        private IVisualElementScheduledItem _pendingExpansionSave;
        private ScrollView _sidebarScroll;
        private int _boundFolderStartIndex;
        private int _rowBindCount;
        private bool _isDiscoveringBoundFolder;

        internal event Action<string> FolderClicked;
        internal event Action<Rect, ZepFolderEntry> ContextMenuRequested;
        internal event Action<string, IReadOnlyList<string>> FolderDropReceived;
        internal event Action<string, IReadOnlyList<string>> FolderExternalImportReceived;

        internal FolderTreeWidget(
            string rootPath,
            UnityZepIconRepository iconRepository,
            Func<string, IReadOnlyList<ZepFolderEntry>> folderChildrenQuery,
            Func<string, ZepFolderEntry> folderEntryQuery = null,
            Func<string, ZepTagColor> folderTintQuery = null)
        {
            if (rootPath == null)
            {
                throw new ArgumentNullException(nameof(rootPath));
            }

            _iconRepository = iconRepository ?? throw new ArgumentNullException(nameof(iconRepository));
            // A tagged folder shows its tag color on the row icon, as it does
            // in Favorites and the assets area. Null (tests, standalone use)
            // leaves every icon on the theme tint.
            _folderTintQuery = folderTintQuery;
            _expansionStateKey = "Zep.FolderTreeExpanded." + rootPath;
            // The tree hydrates itself through folderChildrenQuery; there is no
            // other way to give it folders.
            _model = new FolderTreeModel(
                rootPath,
                folderChildrenQuery,
                folderEntryQuery,
                SessionState.GetString(_expansionStateKey, string.Empty));

            name = "zep-folder-tree-" + rootPath.ToLowerInvariant();
            AddToClassList(ZepUiClasses.FolderTree);

            _folderList = new ListView
            {
                name = "zep-folder-list-" + rootPath.ToLowerInvariant(),
                fixedItemHeight = FolderRowPitch,
                // The tree owns selection by stable project path, so the list
                // holds none: Unity's native selection bookkeeping traverses a
                // content-sized ListView on every change, turning a two-row
                // repaint into a project-size operation.
                selectionType = SelectionType.None,
                showAlternatingRowBackgrounds = AlternatingRowBackground.None
            };
            _folderList.AddToClassList(ZepUiClasses.ListSurface);
            _folderList.AddToClassList(ZepUiClasses.FolderTreeList);
            _folderList.makeItem = MakeFolderRow;
            _folderList.bindItem = BindFolderRow;
            // TrickleDown so the ListView's own navigation can't swallow the
            // horizontal arrows before the tree fold handler sees them.
            _folderList.RegisterCallback<KeyDownEvent>(HandleListKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<PointerDownEvent>(OnFolderContextMenuPointerDown, TrickleDown.TrickleDown);
            RegisterCallback<MouseDownEvent>(OnFolderContextMenuMouseDown, TrickleDown.TrickleDown);
            _folderList.RegisterCallback<DragUpdatedEvent>(OnFolderListDragUpdated, TrickleDown.TrickleDown);
            _folderList.RegisterCallback<DragPerformEvent>(OnFolderListDragPerform, TrickleDown.TrickleDown);
            _folderList.RegisterCallback<DragLeaveEvent>(_ => EndDropHover());
            _folderList.RegisterCallback<DragExitedEvent>(_ => EndDropHover());
            _splitDrag = new SidebarSplitDragPrepareInteraction(
                this,
                SnapshotSplitDragPayload,
                CommitSplitSourceClick);
            _folderList.itemsSource = _boundFolders;
            // The sidebar ScrollView is the only vertical scroll owner:
            // RefreshList gives this list only the outer-viewport row slice and
            // represents the rest with sibling spacers. Its internal ScrollView
            // must never scroll or show a scroller. Hidden must be
            // set here in C# — ScrollView toggles scroller display with inline
            // writes, so a USS display rule cannot suppress it — and
            // ForwardScrolling hands wheel events to the sidebar instead of
            // letting the nested viewport eat them.
            var listScrollView = _folderList.Q<ScrollView>();
            listScrollView.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            listScrollView.nestedInteractionKind =
                ScrollView.NestedInteractionKind.ForwardScrolling;
            _leadingSpacer = new VisualElement
            {
                name = "zep-folder-tree-leading-spacer"
            };
            _trailingSpacer = new VisualElement
            {
                name = "zep-folder-tree-trailing-spacer"
            };
            Add(_leadingSpacer);
            Add(_folderList);
            Add(_trailingSpacer);
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            // The bound window is a slice of the OUTER viewport, so it goes
            // stale whenever this tree moves relative to that viewport — and
            // the sidebar scroll/viewport hooks below only see scrolling and
            // resizing, not the tree above this one folding or growing. A
            // tree pulled up from below the viewport that way kept its
            // "nothing visible" window and rendered blank until the next
            // scroll (the Packages tree, after collapsing Assets). Own
            // geometry changes cover that; the height this tree writes for
            // itself re-enters here and returns as an unchanged window.
            RegisterCallback<GeometryChangedEvent>(_ => ScheduleViewportRefresh());
            // The list delegates focus to an inner element, so the tree cannot
            // key a USS :focus rule on itself; this class says "keyboard focus
            // is somewhere in this tree", which makes the selected row the
            // keyboard cursor the sidebar's focus ring is drawn on.
            RegisterCallback<FocusInEvent>(_ => EnableInClassList(
                ZepUiClasses.FolderTreeFocused, true));
            RegisterCallback<FocusOutEvent>(evt => EnableInClassList(
                ZepUiClasses.FolderTreeFocused,
                evt.relatedTarget is VisualElement next && Contains(next)));
        }

        internal ZepFolderEntry SelectedFolderEntry
        {
            get
            {
                return _model.TryGetMaterialized(
                    _selectedFolderPath,
                    out var selected)
                    ? selected
                    : null;
            }
        }

        internal int VisibleFolderCount => _model.VisibleFolders.Count;

        internal int BoundFolderCount => _boundFolders.Count;

        internal int RowBindCount => _rowBindCount;

        // Test-only: how often the hover path actually evaluated the move
        // verdict instead of reusing the memo above.
        internal static int MoveVerdictEvaluationsForTests;

        // Test-only: how often a reveal actually allocated a retry loop instead
        // of leaving the pending one to finish framing the same folder.
        internal static int RevealScheduleCountForTests;

        // Test-only: how often a catalog delta actually rebuilt the visible
        // projection instead of stopping at buckets no visible row reads.
        internal static int VisibleProjectionRebuildsForTests;

        // Called by SidebarWidget after drag auto-scroll writes the owning
        // ScrollView offset, and by the reveal-scroll retry after it writes it.
        // The write does raise valueChanged, but that leg only schedules a
        // refresh for the next tick. What this synchronous call buys is
        // same-tick realization: the caller re-picks the hovered row, or
        // measures the revealed one, against rows that already exist.
        internal void RefreshViewportWindow()
        {
            RefreshBoundWindow(forceRebind: false);
            ScheduleViewportRefresh();
        }

        // Single entry point per refresh tick. Compares both paths and
        // rebuilds the visible projection at most once; a no-change call costs
        // two string compares.
        internal void Apply(string currentFolderPath, string selectedFolderPath)
        {
            var scopedCurrent = _model.ScopeToRoot(currentFolderPath);
            var scopedSelected = _model.ScopeToRoot(selectedFolderPath);
            var previousSelectedFolderPath = _selectedFolderPath;
            var currentChanged = !string.Equals(
                _currentFolderPath,
                scopedCurrent,
                StringComparison.OrdinalIgnoreCase);
            var selectedChanged = !string.Equals(
                _selectedFolderPath,
                scopedSelected,
                StringComparison.OrdinalIgnoreCase);
            var isFirstApply = !_hasAppliedPaths;
            if (!isFirstApply && !currentChanged && !selectedChanged)
            {
                return;
            }

            _currentFolderPath = scopedCurrent;
            _selectedFolderPath = scopedSelected;
            _hasAppliedPaths = true;

            var hierarchyChanged = _model.EnsureChildrenLoaded(string.Empty);
            hierarchyChanged |= _model.EnsureLineageMaterialized(scopedCurrent);
            hierarchyChanged |= _model.EnsureLineageMaterialized(scopedSelected);

            var isNavigation = !isFirstApply || !_model.RestoredFromSavedState;
            var revealed = false;
            if (isNavigation)
            {
                if (currentChanged)
                {
                    revealed |= _model.ExpandAncestors(scopedCurrent);
                }

                if (selectedChanged)
                {
                    revealed |= _model.ExpandAncestors(scopedSelected);
                }

                if (revealed)
                {
                    SaveExpandedState();
                }
            }

            // A selection-only navigation does not change expansion state or
            // the materialized hierarchy. Re-walking every expanded branch on
            // that hot path makes a sibling switch scale with total folder
            // count even though only the highlight changed.
            var deferExpandedMaterialization = _model.HasFolderEntryQuery
                && !isFirstApply
                && isNavigation
                && (currentChanged || selectedChanged);
            if (!deferExpandedMaterialization
                && (hierarchyChanged || revealed || isFirstApply))
            {
                hierarchyChanged |= _model.LoadExpandedDescendants(_model.RootPath);
            }
            if (!hierarchyChanged && !revealed)
            {
                RefreshSelectionOnly(previousSelectedFolderPath);
                if (selectedChanged && isNavigation)
                {
                    ScheduleScrollRowIntoView(scopedSelected);
                }

                return;
            }

            _model.RebuildVisibleFolders();
            RefreshList();
            if (deferExpandedMaterialization)
            {
                ScheduleExpandedHierarchyMaterialization();
            }
            if (selectedChanged && isNavigation)
            {
                ScheduleScrollRowIntoView(scopedSelected);
            }
        }

        private void ScheduleExpandedHierarchyMaterialization()
        {
            if (_pendingHierarchyMaterialization != null)
            {
                return;
            }

            var scheduled = _folderList.schedule.Execute(() =>
            {
                _pendingHierarchyMaterialization = null;
                if (!_model.LoadExpandedDescendants(_model.RootPath))
                {
                    return;
                }

                _model.RebuildVisibleFolders();
                RefreshList();
            });
            _pendingHierarchyMaterialization = scheduled;
            scheduled.ExecuteLater(0);
        }

        internal void ApplyCatalogDelta(AssetCatalogDelta delta)
        {
            if (delta == null)
            {
                return;
            }

            // A first folder query can synchronously publish a catalog delta.
            // Refreshing here would re-enter Unity's virtualization controller
            // while BindFolderRow is still setting up one of its active rows.
            if (_isDiscoveringBoundFolder)
            {
                _folderList.schedule.Execute(() => ApplyCatalogDelta(delta));
                return;
            }

            var changed = false;
            var changedVisibleParent = false;
            foreach (var parentPath in delta.AffectedParentFolders)
            {
                if (!_model.IsParentLoaded(parentPath)
                    || !_model.ReloadChildren(parentPath))
                {
                    continue;
                }

                changed = true;
                // Read against the projection this delta is about to replace,
                // which is the one the rows on screen were built from.
                changedVisibleParent |= _model.IsParentVisible(parentPath);
            }

            if (!changed)
            {
                return;
            }

            // Unchanged: the current and selected lineages stay materialized
            // whatever the delta touched. Materializing one adds a row to its
            // parent's bucket, so its verdict joins the gate below.
            var lineageChanged = _model.EnsureLineageMaterialized(_currentFolderPath);
            lineageChanged |= _model.EnsureLineageMaterialized(_selectedFolderPath);

            // A folder is visible only when every ancestor is visible AND
            // expanded, so a bucket under a hidden parent holds nothing on
            // screen — re-expanding that branch walks the buckets this delta
            // just refreshed. A visible parent is rebuilt even when it is
            // COLLAPSED: its own row paints the chevron from FolderHasChildren,
            // so gaining or losing a first child changes it (§5.25).
            if (!changedVisibleParent && !lineageChanged)
            {
                return;
            }

            VisibleProjectionRebuildsForTests++;
            _model.RebuildVisibleFolders();
            RefreshList();
        }

        internal void InvalidateCatalog()
        {
            if (!_model.TryReloadCatalog(_currentFolderPath, _selectedFolderPath))
            {
                return;
            }

            _model.RebuildVisibleFolders();
            RefreshList();
        }

        internal bool TryGetFolderEntryFromTarget(VisualElement target, out ZepFolderEntry folderEntry)
        {
            return TryGetFolderRowFromTarget(target, out folderEntry, out _);
        }

        // Ancestor walk shared by entry resolution and the drag-over
        // highlight, which also needs the row ELEMENT the entry lives on.
        private bool TryGetFolderRowFromTarget(
            VisualElement target,
            out ZepFolderEntry folderEntry,
            out VisualElement rowElement)
        {
            folderEntry = null;
            rowElement = null;
            var current = target;
            while (current != null && current != _folderList && current != this)
            {
                if (current.userData is FolderRowParts parts && parts.Entry != null)
                {
                    folderEntry = parts.Entry;
                    rowElement = current;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        internal ZepSplitDragPayload SnapshotSplitDragPayload(
            VisualElement target)
        {
            var current = target;
            while (current != null && current != _folderList && current != this)
            {
                if (current.ClassListContains(
                        ZepUiClasses.FolderDisclosureButton))
                {
                    return null;
                }

                if (current.userData is FolderRowParts parts
                    && parts.Entry != null)
                {
                    var entry = parts.Entry;
                    return new ZepSplitDragPayload(
                        ZepPaneSource.RootedFolder(
                            AssetPath.From(entry.Path)),
                        entry.Name);
                }

                current = current.parent;
            }

            return null;
        }

        internal void CancelSplitDragPreparation()
        {
            _splitDrag.Cancel();
        }

        // Rebinds the outer-sidebar viewport slice; BindFolderRow paints the
        // path-owned selection. RefreshItems (not Rebuild) reuses the bounded
        // row pool.
        private void RefreshList()
        {
            RefreshBoundWindow(forceRebind: true);
        }

        private void RefreshBoundWindow(bool forceRebind)
        {
            var start = 0;
            var end = _model.VisibleFolders.Count;
            if (_sidebarScroll != null
                && _sidebarScroll.panel != null
                && panel != null)
            {
                var viewport = _sidebarScroll.contentViewport.worldBound;
                var viewportHeight = viewport.height;
                if (viewportHeight > 0f
                    && !float.IsNaN(viewport.yMin)
                    && !float.IsNaN(worldBound.yMin))
                {
                    var viewportTopInTree = viewport.yMin - worldBound.yMin;
                    var viewportBottomInTree = viewport.yMax - worldBound.yMin;
                    start = Mathf.Clamp(
                        Mathf.FloorToInt(viewportTopInTree / FolderRowPitch)
                            - FolderRowOverscan,
                        0,
                        _model.VisibleFolders.Count);
                    end = Mathf.Clamp(
                        Mathf.CeilToInt(viewportBottomInTree / FolderRowPitch)
                            + FolderRowOverscan,
                        start,
                        _model.VisibleFolders.Count);
                }
                else
                {
                    end = Mathf.Min(
                        _model.VisibleFolders.Count,
                        UnresolvedViewportRowLimit);
                }
            }

            var count = end - start;
            var windowChanged = start != _boundFolderStartIndex
                || count != _boundFolders.Count;
            if (!windowChanged && !forceRebind)
            {
                return;
            }

            _boundFolderStartIndex = start;
            _boundFolders.Clear();
            for (var i = start; i < end; i++)
            {
                _boundFolders.Add(_model.VisibleFolders[i]);
            }

            // Runtime-computed geometry only: together these three heights are
            // exactly the full visible projection. The outer sidebar therefore
            // retains the sole scroll range while the ListView itself remains a
            // small, non-scrolling realized window. Pin the total first so an
            // offset write cannot clamp while the three child heights change in
            // sequence before the next layout pass.
            style.height = _model.VisibleFolders.Count * FolderRowPitch;
            _leadingSpacer.style.height = start * FolderRowPitch;
            _folderList.style.height = count * FolderRowPitch;
            _trailingSpacer.style.height =
                (_model.VisibleFolders.Count - end) * FolderRowPitch;

            _folderList.RefreshItems();
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            BindSidebarScroll();
            RefreshBoundWindow(forceRebind: false);
            ScheduleViewportRefresh();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            UnbindSidebarScroll();
            _pendingViewportRefresh?.Pause();
            _pendingViewportRefresh = null;
            // A detach can be the last tick this tree ever gets (window closed,
            // domain reload); the coalesced expansion write must not die with it.
            FlushPendingExpandedState();
        }

        private void BindSidebarScroll()
        {
            var sidebarScroll = GetFirstAncestorOfType<ScrollView>();
            if (ReferenceEquals(_sidebarScroll, sidebarScroll))
            {
                return;
            }

            UnbindSidebarScroll();
            _sidebarScroll = sidebarScroll;
            if (_sidebarScroll == null)
            {
                return;
            }

            _sidebarScroll.verticalScroller.valueChanged += OnSidebarScrollChanged;
            _sidebarScroll.contentViewport.RegisterCallback<GeometryChangedEvent>(
                OnSidebarViewportGeometryChanged);
        }

        private void UnbindSidebarScroll()
        {
            if (_sidebarScroll == null)
            {
                return;
            }

            _sidebarScroll.verticalScroller.valueChanged -= OnSidebarScrollChanged;
            _sidebarScroll.contentViewport.UnregisterCallback<GeometryChangedEvent>(
                OnSidebarViewportGeometryChanged);
            _sidebarScroll = null;
        }

        private void OnSidebarScrollChanged(float value)
        {
            ScheduleViewportRefresh();
        }

        private void OnSidebarViewportGeometryChanged(GeometryChangedEvent evt)
        {
            ScheduleViewportRefresh();
        }

        private void ScheduleViewportRefresh()
        {
            if (_pendingViewportRefresh != null)
            {
                return;
            }

            var scheduled = schedule.Execute(() =>
            {
                _pendingViewportRefresh = null;
                RefreshBoundWindow(forceRebind: false);
            });
            _pendingViewportRefresh = scheduled;
            scheduled.ExecuteLater(0);
        }

        // Navigation inside an already-materialized hierarchy changes only the
        // selected row. Avoid rebinding and re-sorting every visible sibling.
        private void RefreshSelectionOnly(string previousSelectedFolderPath)
        {
            RefreshRealizedSelectionClass(previousSelectedFolderPath);
            RefreshRealizedSelectionClass(_selectedFolderPath);
        }

        private void RefreshRealizedSelectionClass(string folderPath)
        {
            var index = _model.FindVisibleFolderIndex(folderPath);
            if (index < 0)
            {
                return;
            }

            var rowElement = GetRealizedRowForVisibleIndex(index);
            rowElement?.EnableInClassList(
                ZepUiClasses.RowSelected,
                string.Equals(
                    folderPath,
                    _selectedFolderPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        // One navigation can reveal both the current and the selected folder, a
        // fold-all touches every branch, and each of those ends here. The write
        // serialises the WHOLE expansion set into SessionState (native memory,
        // hundreds of KB on a large project), so it is coalesced to one per burst
        // and flushed when the tree leaves its panel — the set itself is already
        // up to date, only its persistence is deferred.
        private void SaveExpandedState()
        {
            if (_pendingExpansionSave != null)
            {
                return;
            }

            var scheduled = _folderList.schedule.Execute(() =>
            {
                _pendingExpansionSave = null;
                WriteExpandedState();
            });
            _pendingExpansionSave = scheduled;
            scheduled.ExecuteLater(0);
        }

        internal void FlushPendingExpandedState()
        {
            if (_pendingExpansionSave == null)
            {
                return;
            }

            _pendingExpansionSave.Pause();
            _pendingExpansionSave = null;
            WriteExpandedState();
        }

        // Counts the actual SessionState writes so a test can prove a burst
        // collapses into one.
        internal int ExpansionStateWriteCount { get; private set; }

        private void WriteExpandedState()
        {
            ExpansionStateWriteCount++;
            SessionState.SetString(_expansionStateKey, _model.SerializeExpansionState());
        }

        private void ToggleFolderExpanded(string folderPath)
        {
            SetFolderExpanded(folderPath, !_model.IsFolderExpanded(folderPath));
        }

        private void SetFolderExpanded(string folderPath, bool expanded)
        {
            if (expanded)
            {
                _model.EnsureChildrenLoaded(folderPath);
            }

            if (string.IsNullOrEmpty(folderPath) || !_model.FolderHasChildren(folderPath))
            {
                return;
            }

            if (!_model.SetExpandedFlag(folderPath, expanded))
            {
                return;
            }

            SaveExpandedState();
            if (expanded)
            {
                _model.LoadExpandedDescendants(folderPath);
            }

            _model.RebuildVisibleFolders();
            RefreshList();
        }

        // Unity Project-window tree keys: Right expands a collapsed folder and
        // steps into the first child of an expanded one; Left collapses an
        // expanded folder and otherwise jumps to the parent. Selection moves
        // publish FolderClicked so they navigate exactly like a click.
        // Unhandled shapes (leaf + Right, root + Left) leave the event
        // unconsumed.
        internal void HandleListKeyDown(KeyDownEvent evt)
        {
            var isFoldKey = evt.keyCode == KeyCode.LeftArrow
                || evt.keyCode == KeyCode.RightArrow;
            var isLinearKey = evt.keyCode == KeyCode.UpArrow
                || evt.keyCode == KeyCode.DownArrow
                || evt.keyCode == KeyCode.Home
                || evt.keyCode == KeyCode.End;
            if (!isFoldKey && !isLinearKey)
            {
                return;
            }

            if (evt.actionKey || evt.altKey || evt.ctrlKey || evt.shiftKey)
            {
                return;
            }

            var selected = SelectedFolderEntry;
            if (selected == null)
            {
                return;
            }

            if (isLinearKey)
            {
                var selectedIndex = _model.FindVisibleFolderIndex(selected.Path);
                var targetIndex = evt.keyCode switch
                {
                    KeyCode.UpArrow => selectedIndex - 1,
                    KeyCode.DownArrow => selectedIndex + 1,
                    KeyCode.Home => 0,
                    KeyCode.End => _model.VisibleFolders.Count - 1,
                    _ => -1,
                };
                if (targetIndex >= 0
                    && targetIndex < _model.VisibleFolders.Count
                    && TrySelectRowByPath(_model.VisibleFolders[targetIndex].Path))
                {
                    evt.StopPropagation();
                }

                return;
            }

            _model.EnsureChildrenLoaded(selected.Path);

            var hasChildren = _model.FolderHasChildren(selected.Path);
            if (evt.keyCode == KeyCode.RightArrow)
            {
                if (!hasChildren)
                {
                    return;
                }

                if (!_model.IsFolderExpanded(selected.Path))
                {
                    SetFolderExpanded(selected.Path, true);
                    evt.StopPropagation();
                    return;
                }

                if (TrySelectRowByPath(_model.GetFirstChildPath(selected.Path)))
                {
                    evt.StopPropagation();
                }

                return;
            }

            if (hasChildren && _model.IsFolderExpanded(selected.Path))
            {
                SetFolderExpanded(selected.Path, false);
                evt.StopPropagation();
                return;
            }

            var parent = AssetPathFormat.GetParentFolderPath(selected.Path);
            if (_model.BelongsToRoot(parent) && TrySelectRowByPath(parent))
            {
                evt.StopPropagation();
            }
        }

        private bool TrySelectRowByPath(string folderPath)
        {
            var index = _model.FindVisibleFolderIndex(folderPath);
            if (index < 0)
            {
                return false;
            }

            FolderClicked?.Invoke(folderPath);
            ScheduleScrollRowIntoView(folderPath);
            return true;
        }

        private VisualElement GetRealizedRowForVisibleIndex(int visibleIndex)
        {
            var boundIndex = visibleIndex - _boundFolderStartIndex;
            return boundIndex >= 0 && boundIndex < _boundFolders.Count
                ? _folderList.GetRootElementForIndex(boundIndex)
                : null;
        }

        // Deferred and self-verifying: after an Apply that grew the visible
        // projection (expanded ancestors) the target row is not realized —
        // and the ancestor ScrollView's scrollable range not updated — until
        // the ListView's new height goes through layout, which can take more
        // than one panel update. A single-shot scroll then clamps against the
        // stale range and silently stays put, so this retries until the row
        // verifiably sits inside the sidebar viewport (bounded, so a row the
        // user immediately scrolls away from is not fought over).
        private void ScheduleScrollRowIntoView(string folderPath)
        {
            var index = _model.FindVisibleFolderIndex(folderPath);
            if (index < 0)
            {
                return;
            }

            // One navigation reaches this twice: TrySelectRowByPath tells the
            // shell, whose refresh comes back through Apply and schedules for
            // the same folder, and then schedules again on the way out. The
            // pending loop already re-resolves the row every tick, so a second
            // one for the same target only throws away its attempt budget.
            if (_pendingRowScroll != null
                && _pendingRowScroll.isActive
                && string.Equals(
                    _pendingRowScrollPath,
                    folderPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RevealScheduleCountForTests++;
            _pendingRowScroll?.Pause();
            _pendingRowScrollPath = folderPath;
            var attempts = 0;
            _pendingRowScroll = _folderList.schedule.Execute(() =>
            {
                attempts++;
                // The path, not the index, is what is being revealed. An Apply
                // inside this window (an external folder add/remove above the
                // target) reorders the visible projection without rescheduling
                // — a captured index would then frame whatever row slid into
                // its slot. Re-resolve each tick, and stop if the folder is
                // gone: there is nothing left to frame.
                var rowIndex = _model.FindVisibleFolderIndex(folderPath);
                var done = rowIndex < 0;
                if (!done)
                {
                    var row = GetRealizedRowForVisibleIndex(rowIndex);
                    var sidebarScroll = _sidebarScroll
                        ?? GetFirstAncestorOfType<ScrollView>();
                    if (row == null && sidebarScroll != null)
                    {
                        // Never realize through ListView.ScrollToItem: that moves
                        // the nested list viewport even though its scroller is
                        // hidden, violating the sidebar's one-scroll-owner
                        // contract. Frame the estimated content-sized row through
                        // the outer owner; a later layout pass realizes it there.
                        var estimatedTop = worldBound.yMin
                            + (rowIndex * FolderRowPitch);
                        ScrollOuterSidebarToward(
                            sidebarScroll,
                            estimatedTop,
                            estimatedTop + FolderRowPitch);
                    }
                    else if (row != null && sidebarScroll != null)
                    {
                        sidebarScroll.ScrollTo(row);
                        var viewport = sidebarScroll.contentViewport.worldBound;
                        var rowBound = row.worldBound;
                        ScrollOuterSidebarToward(
                            sidebarScroll,
                            rowBound.yMin,
                            rowBound.yMax);

                        done = rowBound.yMin >= viewport.yMin - 1f
                            && rowBound.yMax <= viewport.yMax + 1f;
                    }
                }

                if (done || attempts >= MaxRowScrollAttempts)
                {
                    _pendingRowScroll?.Pause();
                    _pendingRowScroll = null;
                    _pendingRowScrollPath = null;
                }
            }).Every(0);
        }

        private void ScrollOuterSidebarToward(
            ScrollView sidebarScroll,
            float rowTop,
            float rowBottom)
        {
            var viewport = sidebarScroll.contentViewport.worldBound;
            var offset = sidebarScroll.scrollOffset;
            var targetY = offset.y;
            if (rowTop < viewport.yMin)
            {
                targetY -= viewport.yMin - rowTop;
            }
            else if (rowBottom > viewport.yMax)
            {
                targetY += rowBottom - viewport.yMax;
            }

            targetY = Mathf.Max(0f, targetY);
            if (Mathf.Approximately(targetY, offset.y))
            {
                return;
            }

            // ScrollView.ScrollTo can clamp against its previous layout range
            // in the same tick the content-sized tree grows. Apply the measured
            // delta to the one owning sidebar scroller; the next retry verifies
            // post-layout row bounds before stopping.
            sidebarScroll.scrollOffset = new Vector2(offset.x, targetY);
            RefreshViewportWindow();
        }

        private void CommitSplitSourceClick(ZepSplitDragPayload payload)
        {
            var folderPath = payload.Source.RootFolderPath.Value;
            var index = _model.FindVisibleFolderIndex(folderPath);
            if (index < 0)
            {
                return;
            }

            FolderClicked?.Invoke(folderPath);
        }

        private void OnFolderContextMenuPointerDown(PointerDownEvent evt)
        {
            if (!ZepContextMenuInput.IsPointerTrigger(evt))
            {
                return;
            }

            TryGetFolderEntryFromTarget(evt.target as VisualElement, out var folderEntry);
            ContextMenuRequested?.Invoke(
                ZepContextMenuInput.PointerAnchor(evt),
                folderEntry);
            ZepContextMenuInput.Consume(evt);
        }

        private static void OnFolderContextMenuMouseDown(MouseDownEvent evt)
        {
            if (ZepContextMenuInput.IsMouseTrigger(evt))
            {
                ZepContextMenuInput.Consume(evt);
            }
        }

        private void OnFolderListDragUpdated(DragUpdatedEvent evt)
        {
            var visualMode = RefreshDropTargetFromHover(evt.target as VisualElement);
            if (visualMode == DragAndDropVisualMode.None) return;

            DragAndDrop.visualMode = visualMode;
            evt.StopPropagation();
        }

        // Shared by the DragUpdated handler and the sidebar's drag auto-scroll
        // tick (which re-picks under a stationary cursor as rows move under
        // it): resolves the hovered row, refreshes the drop-target highlight,
        // and returns the visual mode the hovered payload wants (None = not a
        // droppable hover; the event handler then leaves the event alone).
        //
        // The Contains gate scopes tick picks to THIS tree: both sidebar trees
        // share the row type, so without it the ancestor walk would happily
        // resolve the sibling tree's rows too.
        internal DragAndDropVisualMode RefreshDropTargetFromHover(VisualElement target)
        {
            if (target == null
                || !Contains(target)
                || !TryGetFolderRowFromTarget(target, out var folderEntry, out var rowElement))
            {
                SetDropTarget(null, null);
                return DragAndDropVisualMode.None;
            }

            var projectPaths = EditorDragAndDropPaths.ReadProjectAssetPaths();
            if (projectPaths.Count > 0)
            {
                var isMeaningful = IsMeaningfulMoveTargetForHover(folderEntry.Path, projectPaths);
                SetDropTarget(isMeaningful ? folderEntry.Path : null, rowElement);
                return isMeaningful
                    ? DragAndDropVisualMode.Move
                    : DragAndDropVisualMode.Rejected;
            }

            var externalPaths = EditorDragAndDropPaths.ReadExternalPaths();
            if (externalPaths.Count > 0)
            {
                SetDropTarget(folderEntry.Path, rowElement);
                return DragAndDropVisualMode.Copy;
            }

            // Some other payload (a sidebar split drag) is hovering the row;
            // no folder drop will happen, so no row may advertise one.
            SetDropTarget(null, null);
            return DragAndDropVisualMode.None;
        }

        // The verdict is pure in (folder path, dragged payload), and the payload
        // is fixed for the life of a drag, so a hover that has not left the row —
        // every DragUpdated frame plus every drag auto-scroll tick — reuses the
        // previous answer instead of re-walking every dragged path. The payload
        // is keyed by the read's generation and by the memo instance it hands
        // back, both of which change the moment the drag's paths do.
        // FolderMoveDrop.FilterMovablePaths must NOT be memoized this way:
        // it asks the gateway whether each path is writable, which is not a
        // function of the payload alone.
        private bool IsMeaningfulMoveTargetForHover(
            string folderPath,
            IReadOnlyList<string> projectPaths)
        {
            var payloadGeneration = EditorDragAndDropPaths.PayloadGeneration;
            if (_hoverVerdictPayloadGeneration == payloadGeneration
                && ReferenceEquals(_hoverVerdictPaths, projectPaths)
                && string.Equals(_hoverVerdictFolderPath, folderPath, StringComparison.Ordinal))
            {
                return _hoverVerdictIsMeaningful;
            }

            MoveVerdictEvaluationsForTests++;
            _hoverVerdictIsMeaningful =
                FolderMoveDrop.IsMeaningfulMoveTarget(folderPath, projectPaths);
            _hoverVerdictFolderPath = folderPath;
            _hoverVerdictPayloadGeneration = payloadGeneration;
            _hoverVerdictPaths = projectPaths;
            return _hoverVerdictIsMeaningful;
        }

        // Drop-hover teardown: the highlight must not outlive the hover, and the
        // verdict memo must not outlive the drag.
        private void EndDropHover()
        {
            SetDropTarget(null, null);
            _hoverVerdictFolderPath = null;
            _hoverVerdictPaths = null;
            _hoverVerdictPayloadGeneration = 0;
        }

        private void OnFolderListDragPerform(DragPerformEvent evt)
        {
            EndDropHover();
            if (!TryGetFolderEntryFromTarget(evt.target as VisualElement, out var folderEntry))
            {
                return;
            }

            // Copied, not the shared hover memo: the drop handler keeps the
            // payload past this event.
            var projectPaths = EditorDragAndDropPaths.CopyProjectAssetPaths();
            if (projectPaths.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                FolderDropReceived?.Invoke(folderEntry.Path, projectPaths);
                evt.StopPropagation();
                return;
            }

            var externalPaths = EditorDragAndDropPaths.ReadExternalPaths();
            if (externalPaths.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                FolderExternalImportReceived?.Invoke(folderEntry.Path, externalPaths);
                evt.StopPropagation();
            }
        }

        // A null/empty path clears. The row element is toggled directly (no
        // realized-row registry exists here); BindFolderRow re-derives the
        // class from _dropTargetFolderPath whenever rows rebind mid-drag.
        private void SetDropTarget(string folderPath, VisualElement rowElement)
        {
            var next = folderPath ?? string.Empty;
            var nextRow = next.Length > 0 ? rowElement : null;
            if (string.Equals(_dropTargetFolderPath, next, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(_dropTargetRow, nextRow))
            {
                return;
            }

            _dropTargetRow?.EnableInClassList(ZepUiClasses.DropTarget, false);
            _dropTargetFolderPath = next;
            _dropTargetRow = nextRow;
            _dropTargetRow?.EnableInClassList(ZepUiClasses.DropTarget, true);
        }

        private sealed class FolderRowParts
        {
            public ZepFolderEntry Entry;
            public VisualElement IconImage;
            public Label Name;
            public Button Disclosure;
        }

        private VisualElement MakeFolderRow()
        {
            var parts = new FolderRowParts();
            var row = new VisualElement { name = "zep-folder-row" };
            row.AddToClassList(ZepUiClasses.FolderRow);
            // The shared sidebar row geometry (pitch, inset, radius, hover); the
            // tree's own deltas are keyed on the list class in Zep.uss.
            row.AddToClassList(ZepUiClasses.SidebarRow);
            row.userData = parts;

            // The list holds no native selection, so this is the ONLY
            // notification path for a folder click — including a re-click on
            // the already-selected folder ("navigate here again", which the
            // shell uses to exit an active search). A repeat for the same
            // folder is absorbed by ZepState.SetFolderSelection's
            // same-value early-returns.
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                var folder = ((FolderRowParts)row.userData).Entry;
                if (folder != null)
                {
                    FolderClicked?.Invoke(folder.Path);
                }
            });

            // Clickable themed-icon chevron, leading the row. text is unused
            // (image-driven); zep-themed-icon picks up the theme tint variable,
            // and the expanded state is a class that USS rotates. StopPropagation
            // keeps a chevron click from re-firing the row's navigation.
            var disclosure = new Button { name = "zep-folder-row-disclosure", focusable = false, text = string.Empty };
            disclosure.AddToClassList(ZepUiClasses.FolderDisclosureButton);
            disclosure.AddToClassList(ZepUiClasses.FlatButton);
            disclosure.AddToClassList(ZepUiClasses.ThemedIcon);
            disclosure.style.backgroundImage = ZepChevronIcons.GetLeadingChevronBackground();
            disclosure.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            disclosure.clicked += () =>
            {
                if (disclosure.userData is string folderPath)
                {
                    ToggleFolderExpanded(folderPath);
                }
            };

            var icon = new VisualElement { name = "zep-folder-row-icon" };
            icon.AddToClassList(ZepUiClasses.RowIcon);

            var iconImage = new VisualElement { name = "zep-folder-row-icon-image" };
            iconImage.AddToClassList(ZepUiClasses.RowIconImage);
            iconImage.AddToClassList(ZepUiClasses.ThemedFolderIcon);
            icon.Add(iconImage);

            var content = new VisualElement { name = "zep-folder-row-content" };
            content.AddToClassList(ZepUiClasses.RowContent);
            var nameLabel = new Label { name = "zep-folder-row-label" };
            nameLabel.AddToClassList(ZepUiClasses.RowPrimaryText);
            content.Add(nameLabel);

            parts.IconImage = iconImage;
            parts.Name = nameLabel;
            parts.Disclosure = disclosure;

            // Order: disclosure (always occupies its slot; invisible on a leaf
            // so names align per depth), icon, content (flex-grow:1). Depth
            // indentation is applied to row.paddingLeft in BindFolderRow.
            row.Add(disclosure);
            row.Add(icon);
            row.Add(content);
            return row;
        }

        private void BindFolderRow(VisualElement element, int index)
        {
            _rowBindCount++;
            var row = _boundFolders[index];
            var parts = (FolderRowParts)element.userData;
            var previousEntry = parts.Entry;
            var preservesTintOnRefusal = previousEntry != null
                && string.Equals(previousEntry.Path, row.Path, StringComparison.OrdinalIgnoreCase);
            parts.Entry = row;

            parts.Name.text = row.Name;
            // The label elides in the fixed-pitch row, so hover has to be able
            // to give the whole name back — the path, as FavoritesWidget does.
            element.tooltip = string.IsNullOrWhiteSpace(row.Path) ? string.Empty : row.Path;
            element.EnableInClassList(
                ZepUiClasses.RowSelected,
                string.Equals(
                    row.Path,
                    _selectedFolderPath,
                    StringComparison.OrdinalIgnoreCase));

            // A collapsed folder's child bucket is loaded lazily, and a root
            // restored collapsed from session state (some deeper folder still
            // expanded) never had it loaded — so it drew no chevron and could
            // not be expanded by mouse at all. Realized rows are a viewport's
            // worth, and each bucket loads once, so ask before deciding.
            _isDiscoveringBoundFolder = true;
            try
            {
                _model.EnsureChildrenLoaded(row.Path);
            }
            finally
            {
                _isDiscoveringBoundFolder = false;
            }
            var hasChildren = _model.FolderHasChildren(row.Path);
            parts.Disclosure.userData = row.Path;
            // Visibility, not display: a leaf keeps the chevron's slot so its
            // icon and name stay on the same columns as its siblings'. The
            // hidden button also stops picking, so a click in that slot
            // reaches the row instead of a chevron with nothing to toggle.
            parts.Disclosure.style.visibility = hasChildren ? Visibility.Visible : Visibility.Hidden;
            parts.Disclosure.pickingMode = hasChildren ? PickingMode.Position : PickingMode.Ignore;
            parts.Disclosure.EnableInClassList(
                ZepUiClasses.FolderDisclosureExpanded,
                hasChildren && _model.IsFolderExpanded(row.Path));

            element.style.paddingLeft = FolderRowBasePadding + row.Depth * FolderIndentStep;

            // Rebinds during a drag (external change rebuilt the tree) recycle
            // rows under the highlight; re-derive it from the path so it stays
            // on the target folder, wherever that folder's row now is.
            var isDropTarget = _dropTargetFolderPath.Length > 0
                && string.Equals(row.Path, _dropTargetFolderPath, StringComparison.OrdinalIgnoreCase);
            element.EnableInClassList(ZepUiClasses.DropTarget, isDropTarget);
            if (isDropTarget)
            {
                _dropTargetRow = element;
            }

            var folderIcon = _iconRepository.GetFolderIcon(row.Path);
            parts.IconImage.style.backgroundImage = folderIcon != null ? new StyleBackground(folderIcon) : new StyleBackground();
            ApplyFolderTint(parts, preservesTintOnRefusal);
        }

        private bool TryGetFolderTint(string folderPath, out ZepTagColor tintColor)
        {
            tintColor = ZepTagColor.None;
            if (_folderTintQuery == null)
            {
                return true;
            }

            try
            {
                tintColor = _folderTintQuery(folderPath);
                return true;
            }
            catch (ProjectTagStateUnavailableException)
            {
                return false;
            }
        }

        private void ApplyFolderTint(FolderRowParts parts, bool preserveOnRefusal)
        {
            if (!TryGetFolderTint(parts.Entry.Path, out var tintColor) && preserveOnRefusal)
            {
                return;
            }

            AssetEntryRenderer.ApplyFolderIconTint(parts.IconImage, isFolder: true, tintColor);
        }

        // Tagging a folder changes nothing the tree's Apply diffs on (paths
        // and selection), so the sidebar re-tints realized rows on every flush.
        // Queries are prepared before rendering so a typed refusal cannot
        // publish a partially refreshed set of row colors.
        internal void RefreshRealizedFolderTints()
        {
            if (_folderTintQuery == null)
            {
                return;
            }

            var prepared = new List<(FolderRowParts Parts, ZepTagColor Tint)>();
            for (var i = 0; i < _boundFolders.Count; i++)
            {
                if (_folderList.GetRootElementForIndex(i)?.userData is FolderRowParts parts
                    && parts.Entry != null)
                {
                    if (!TryGetFolderTint(parts.Entry.Path, out var tintColor))
                    {
                        return;
                    }

                    prepared.Add((parts, tintColor));
                }
            }

            for (var i = 0; i < prepared.Count; i++)
            {
                AssetEntryRenderer.ApplyFolderIconTint(
                    prepared[i].Parts.IconImage,
                    isFolder: true,
                    prepared[i].Tint);
            }
        }
    }
}
