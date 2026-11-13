using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.Application;
using Zep.Core.Indexing;

namespace Zep.Editor.UI.Sidebar
{
    internal sealed class SidebarWidget : VisualElement
    {
        internal const float DefaultPaneWidth = 270f;
        internal const float MinPaneWidth = 180f;
        internal const float MinPaneWidthNarrow = 140f;

        // Recorded state, pushed to the child widgets by Flush(). The tag list
        // is stored by reference: TagsFilterWidget diffs its contents itself,
        // so the in-place-mutated viewmodel collection is safe to hand through.
        // Folders are NOT recorded here — the trees pull them through the
        // queries below.
        private IReadOnlyList<ZepTagFilterOption> _tagOptions;
        private IReadOnlyList<string> _favoritePaths;
        private string _currentFolderPath = string.Empty;
        private string _selectedFolderPath = string.Empty;
        private string _selectedTagFilterId = string.Empty;
        // The folder the user reached THROUGH Favorites, while it is still the
        // selected folder. The sidebar highlight follows the origin of the
        // navigation (Finder / Explorer Quick Access): while a favorite is the
        // anchor only its row is lit and the folder trees neither select nor
        // expand; a tree click, or navigating anywhere else, hands the
        // highlight back to the trees. Sidebar-local presentation state — it
        // is not the pane's selection, which stays the folder either way.
        private string _favoriteAnchorPath = string.Empty;
        // The selection at the moment of the favorite click. The click's
        // navigation is not necessarily the next refresh: activating the
        // primary pane first (after a split drop focused another pane) raises
        // one with the OLD selection still in place. The anchor stays pending
        // while the selection is still that old one, and is dropped only once
        // the selection moves somewhere other than the anchor.
        private string _favoriteAnchorOriginFolder = string.Empty;
        private string _favoriteAnchorOriginTag = string.Empty;
        private bool _canCreateTag;

        private readonly FavoritesWidget _favorites;
        private readonly TagsFilterWidget _tagsFilter;
        private readonly FolderTreeWidget _assetsTree;
        private readonly FolderTreeWidget _packagesTree;
        private readonly ScrollView _scrollView;
        private readonly DragEdgeAutoScroll _dragAutoScroll;
        private IVisualElementScheduledItem _scrollerReveal;

        private float _paneWidth;
        private bool _hasCustomWidth;

        internal event Action<string> FolderSelected;
        internal event Action<string> TagFilterSelected;
        internal event Action CreateTagRequested;
        internal event Action<Rect, ZepFolderEntry> ContextMenuRequested;
        internal event Action<string, IReadOnlyList<string>> FolderDropReceived;
        internal event Action<string, IReadOnlyList<string>> FolderExternalImportReceived;
        internal event Action<string, IReadOnlyList<string>> TagDropReceived;
        internal event Action<IReadOnlyList<string>> FavoriteDropReceived;
        internal event Action<string> FavoriteRemoveRequested;
        internal event Action<string, string> FavoriteMoveRequested;

        internal SidebarWidget(
            UnityZepIconRepository iconRepository,
            float initialWidth,
            bool hasCustomWidth,
            ZepController controller = null)
        {
            if (iconRepository == null)
            {
                throw new ArgumentNullException(nameof(iconRepository));
            }

            // The one place that binds the sidebar's read-only folder queries
            // to the controller. The child widgets keep taking delegates so
            // their tests can drive them with counting fakes. Without a
            // controller there is nothing to browse, so the trees hydrate from
            // an empty catalog rather than from a second, pushed folder list.
            var folderChildrenQuery = controller == null
                ? new Func<string, IReadOnlyList<ZepFolderEntry>>(
                    _ => Array.Empty<ZepFolderEntry>())
                : new Func<string, IReadOnlyList<ZepFolderEntry>>(
                    controller.GetVisibleFolderChildren);
            var folderEntryQuery = controller == null
                ? null
                : new Func<string, ZepFolderEntry>(
                    controller.GetVisibleFolderEntry);
            var folderTintQuery = controller == null
                ? null
                : new Func<string, ZepTagColor>(
                    controller.GetFolderTintColorForPath);

            _paneWidth = initialWidth;
            _hasCustomWidth = hasCustomWidth;

            name = "zep-folder-pane";
            AddToClassList(ZepUiClasses.SidebarPane);

            // Clicking a favorite navigates through the same shell path as a
            // folder-tree click, but anchors the highlight on the favorite;
            // drops and removals stay favorite-specific.
            _favorites = new FavoritesWidget(iconRepository, folderEntryQuery, folderTintQuery);
            _favorites.FavoriteClicked += OnFavoriteClicked;
            _favorites.FavoriteRevealRequested += OnFavoriteRevealRequested;
            _favorites.FavoriteDropReceived += OnChildFavoriteDropReceived;
            _favorites.FavoriteRemoveRequested += OnChildFavoriteRemoveRequested;
            _favorites.FavoriteMoveRequested += OnChildFavoriteMoveRequested;

            _tagsFilter = new TagsFilterWidget();
            _tagsFilter.TagClicked += OnTagClicked;
            _tagsFilter.CreateTagRequested += OnCreateTagRequested;
            _tagsFilter.TagDropReceived += OnChildTagDropReceived;

            _assetsTree = new FolderTreeWidget(
                "Assets",
                iconRepository,
                folderChildrenQuery,
                folderEntryQuery,
                folderTintQuery);
            _assetsTree.FolderClicked += OnFolderClicked;
            _assetsTree.ContextMenuRequested += OnChildContextMenuRequested;
            _assetsTree.FolderDropReceived += OnChildFolderDropReceived;
            _assetsTree.FolderExternalImportReceived += OnChildFolderExternalImportReceived;

            _packagesTree = new FolderTreeWidget(
                "Packages",
                iconRepository,
                folderChildrenQuery,
                folderEntryQuery,
                folderTintQuery);
            _packagesTree.FolderClicked += OnFolderClicked;
            _packagesTree.ContextMenuRequested += OnChildContextMenuRequested;
            _packagesTree.FolderDropReceived += OnChildFolderDropReceived;
            _packagesTree.FolderExternalImportReceived += OnChildFolderExternalImportReceived;

            // The ONE scrollable element in the sidebar — contract, see the
            // package PROJECT_GUIDANCE.md. Child widgets must never scroll
            // themselves or show a scroller: each sizes to its full content,
            // and a child's nested ScrollView (the trees' ListViews) is kept
            // non-scrolling (hidden scroller + ForwardScrolling, see the
            // FolderTreeWidget ctor). Everything that scrolls the sidebar
            // targets this element.
            var scrollView = new ScrollView(ScrollViewMode.Vertical) { name = "zep-sidebar-scroll" };
            scrollView.AddToClassList(ZepUiClasses.SidebarScroll);

            // Favorites, Tags, then the two folder trees as top-level siblings
            // — Assets and Packages are separate roots exactly as in Unity's
            // Project window, never grouped under a shared header. Each is a
            // section of its own for the gutter and spacing, except that
            // Packages is ATTACHED to Assets: two consecutive roots of one
            // location list read as one block, so the section gap between them
            // goes and they continue at the plain row pitch.
            _assetsTree.AddToClassList(ZepUiClasses.SidebarSection);
            _packagesTree.AddToClassList(ZepUiClasses.SidebarSection);
            _packagesTree.AddToClassList(ZepUiClasses.SidebarSectionAttached);
            scrollView.Add(_favorites);
            scrollView.Add(_tagsFilter);
            scrollView.Add(_assetsTree);
            scrollView.Add(_packagesTree);
            Add(scrollView);
            _scrollView = scrollView;

            // The scroller is an overlay that shows while the pointer is over
            // the sidebar (USS :hover) or while the content is moving: each
            // scroll offset change re-arms one short reveal.
            scrollView.verticalScroller.valueChanged += _ => RevealScrollerBriefly();

            // Selection paint follows focus: accent while keyboard focus is
            // anywhere in the sidebar, neutral otherwise; the focus ring only
            // while the last input here was keyboard navigation. Both are
            // classes on the pane so USS keys the three states on them.
            RegisterCallback<FocusInEvent>(_ => EnableInClassList(
                ZepUiClasses.SidebarPaneFocused, true));
            RegisterCallback<FocusOutEvent>(OnFocusOut);
            RegisterCallback<KeyDownEvent>(OnKeyboardNavigation, TrickleDown.TrickleDown);

            // Drag-hover edge auto-scroll for the whole sidebar. TrickleDown:
            // the children's own drag handlers StopPropagation for accepted
            // payloads, and these hooks must observe every hover regardless.
            // Recording-only — they never stop or reroute the event.
            _dragAutoScroll = new DragEdgeAutoScroll(
                _scrollView,
                () => _scrollView,
                RefreshTreeDropTargetsAfterAutoScroll);
            _scrollView.RegisterCallback<DragUpdatedEvent>(
                OnSidebarDragUpdated, TrickleDown.TrickleDown);
            _scrollView.RegisterCallback<DragPerformEvent>(
                _ => _dragAutoScroll.Stop(), TrickleDown.TrickleDown);
            _scrollView.RegisterCallback<DragLeaveEvent>(_ => _dragAutoScroll.Stop());
            _scrollView.RegisterCallback<DragExitedEvent>(_ => _dragAutoScroll.Stop());

            RegisterCallback<PointerDownEvent>(
                OnFreshPointerDown,
                TrickleDown.TrickleDown);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            Flush();
        }

        internal float ResolvedPaneWidth => _paneWidth;

        internal bool HasCustomWidth => _hasCustomWidth;

        internal ZepFolderEntry SelectedFolderEntry
        {
            get
            {

                if (IsFavoriteAnchored)
                {
                    return _favorites.SelectedFolderEntry;
                }

                return _assetsTree.SelectedFolderEntry ?? _packagesTree.SelectedFolderEntry;
            }
        }

        // Keyboard legs the shell routes by focused element, the way it
        // routes Delete and the menu key to the folder trees: Enter opens the
        // focused favorite, the menu key shows the favorite's menu, and
        // Delete is refused (IsFavoriteRowTarget). False when the focus is
        // not on a favorite row.
        internal bool TryOpenFocusedFavorite(VisualElement focused)
        {
            return _favorites.TryActivateRowAt(focused);
        }

        internal bool IsFavoriteRowTarget(VisualElement focused)
        {
            return _favorites.IsRowTarget(focused);
        }

        internal bool TryShowFocusedFavoriteMenu(VisualElement focused, Rect anchor)
        {
            return _favorites.TryShowRowMenuAt(focused, anchor);
        }

        internal bool IsFolderTreeTarget(object target)
        {
            var element = target as VisualElement;
            return element != null
                && (_assetsTree.Contains(element) || _packagesTree.Contains(element));
        }

        // Resolve the folder row under `target` in either tree. Used by the macOS
        // OnGUI external-drop fallback, which hit-tests the folder under the cursor
        // when Finder leaves DragAndDrop.paths empty.
        internal bool TryGetFolderPathAtTarget(VisualElement target, out string folderPath)
        {
            folderPath = null;
            if ((_assetsTree.TryGetFolderEntryFromTarget(target, out var entry)
                    || _packagesTree.TryGetFolderEntryFromTarget(target, out entry))
                && entry != null
                && !string.IsNullOrEmpty(entry.Path))
            {
                folderPath = entry.Path;
                return true;
            }

            return false;
        }

        // Record-only setters: the shell sets all state for the tick, then
        // calls Flush() once. A setter without a following Flush() leaves the
        // sidebar visuals stale on purpose.
        internal void SetTags(IReadOnlyList<ZepTagFilterOption> tagOptions) => _tagOptions = tagOptions;

        internal void SetFavoritePaths(IReadOnlyList<string> favoritePaths) => _favoritePaths = favoritePaths;

        internal void SetCurrentFolderPath(string path) => _currentFolderPath = path ?? string.Empty;

        internal void SetSelectedFolderPath(string path) => _selectedFolderPath = path ?? string.Empty;

        internal void SetActiveTagId(string tagId) => _selectedTagFilterId = tagId ?? string.Empty;

        internal void SetCanCreateTag(bool canCreate) => _canCreateTag = canCreate;

        // Pushes the recorded state into every child exactly once. All
        // children are cheap when nothing changed: the buttons and tag filter
        // diff internally, each tree runs one compare scan and returns.
        internal void Flush()
        {
            using var measurement = ZepPerfLog.Measure(
                "sidebar",
                ZepPerfStage.SidebarRefresh);
            var stopwatch = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;

            _tagsFilter.SetCanCreateTag(_canCreateTag);
            _tagsFilter.SetTags(_tagOptions);
            _tagsFilter.SetActiveTagId(_selectedTagFilterId);

            // Tag selection clears the tree selection; each tree scopes
            // paths to its own root, so both receive the same values.
            var treeSelectedPath = !string.IsNullOrEmpty(_selectedTagFilterId)
                ? string.Empty
                : _selectedFolderPath;

            // One section owns the highlight. The favorite anchor survives
            // exactly as long as its folder is the selected one — or the
            // click's navigation has not landed yet (see the origin fields);
            // any other navigation clears it and the trees take over (and
            // expand to the folder, as they do for every tree-side navigation).
            var favoriteAnchored = _favoriteAnchorPath.Length > 0
                && string.Equals(_favoriteAnchorPath, treeSelectedPath, StringComparison.OrdinalIgnoreCase);
            if (!favoriteAnchored && !IsSelectionAtFavoriteAnchorOrigin)
            {
                ClearFavoriteAnchor();
            }

            _favorites.SetSelectedPath(favoriteAnchored ? treeSelectedPath : string.Empty);
            _favorites.SetFavorites(_favoritePaths);
            var treeCurrentPath = favoriteAnchored ? string.Empty : _currentFolderPath;
            var treeSelectedOrNone = favoriteAnchored ? string.Empty : treeSelectedPath;
            _assetsTree.Apply(treeCurrentPath, treeSelectedOrNone);
            _packagesTree.Apply(treeCurrentPath, treeSelectedOrNone);
            _assetsTree.RefreshRealizedFolderTints();
            _packagesTree.RefreshRealizedFolderTints();

            if (stopwatch != null)
            {
                ZepPerfLog.Log($"[Zep/perf]   Sidebar.Flush totalMs={stopwatch.Elapsed.TotalMilliseconds:F2}");
            }
        }

        internal void ApplyCatalogDelta(AssetCatalogDelta delta)
        {
            _assetsTree.ApplyCatalogDelta(delta);
            _packagesTree.ApplyCatalogDelta(delta);
        }

        internal void InvalidateFolderCatalog()
        {
            _assetsTree.InvalidateCatalog();
            _packagesTree.InvalidateCatalog();
        }

        internal float CaptureWidthForDrag()
        {
            return resolvedStyle.width > 0f
                ? resolvedStyle.width
                : (_hasCustomWidth ? _paneWidth : DefaultPaneWidth);
        }

        internal void SetWidth(
            float desiredWidth,
            float availableContentWidth,
            float minAssetPaneWidth,
            float minAssetPaneWidthNarrow)
        {
            _hasCustomWidth = true;
            _paneWidth = desiredWidth;
            ApplyInternalPaneWidth(availableContentWidth, minAssetPaneWidth, minAssetPaneWidthNarrow);
        }

        internal void ApplyResponsiveWidth(
            float availableContentWidth,
            float minAssetPaneWidth,
            float minAssetPaneWidthNarrow)
        {
            ApplyInternalPaneWidth(availableContentWidth, minAssetPaneWidth, minAssetPaneWidthNarrow);
        }

        internal void AnimateTagHighlight(string tagId)
        {
            _tagsFilter.AnimateTagHighlight(tagId);
        }

        private void ApplyInternalPaneWidth(
            float availableContentWidth,
            float minAssetPaneWidth,
            float minAssetPaneWidthNarrow)
        {
            var targetWidth = _hasCustomWidth ? _paneWidth : DefaultPaneWidth;
            _paneWidth = ClampPaneWidth(targetWidth, availableContentWidth, minAssetPaneWidth, minAssetPaneWidthNarrow);
            style.width = _paneWidth;
            style.flexBasis = _paneWidth;
        }

        private static float ClampPaneWidth(
            float width,
            float availableContentWidth,
            float minAssetPaneWidth,
            float minAssetPaneWidthNarrow)
        {
            if (availableContentWidth <= 0f)
            {
                return Mathf.Max(MinPaneWidthNarrow, width);
            }

            var splitterWidth = ZepPaneSplitterWidget.Width;
            var hasRoomForMinimums = availableContentWidth >= MinPaneWidth + splitterWidth + minAssetPaneWidth;
            var minimumFolderWidth = hasRoomForMinimums
                ? MinPaneWidth
                : Mathf.Min(MinPaneWidth, Mathf.Max(MinPaneWidthNarrow, availableContentWidth * 0.28f));
            var minimumAssetWidth = hasRoomForMinimums
                ? minAssetPaneWidth
                : Mathf.Max(minAssetPaneWidthNarrow, availableContentWidth * 0.45f);
            var maximumFolderWidth = Mathf.Max(minimumFolderWidth, availableContentWidth - splitterWidth - minimumAssetWidth);

            return Mathf.Clamp(width, minimumFolderWidth, maximumFolderWidth);
        }

        private bool IsFavoriteAnchored =>
            _favoriteAnchorPath.Length > 0
            && string.IsNullOrEmpty(_selectedTagFilterId)
            && string.Equals(_favoriteAnchorPath, _selectedFolderPath, StringComparison.OrdinalIgnoreCase);

        private bool IsSelectionAtFavoriteAnchorOrigin =>
            _favoriteAnchorPath.Length > 0
            && string.Equals(_favoriteAnchorOriginFolder, _selectedFolderPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_favoriteAnchorOriginTag, _selectedTagFilterId, StringComparison.Ordinal);

        private void ClearFavoriteAnchor()
        {
            _favoriteAnchorPath = string.Empty;
            _favoriteAnchorOriginFolder = string.Empty;
            _favoriteAnchorOriginTag = string.Empty;
        }

        private void OnTagClicked(string tagId)
        {
            TagFilterSelected?.Invoke(tagId);
        }

        private void OnCreateTagRequested()
        {
            CreateTagRequested?.Invoke();
        }

        private void OnFolderClicked(string folderPath)
        {
            ClearFavoriteAnchor();
            FolderSelected?.Invoke(folderPath);
            RepaintIfAlreadySelected(folderPath);
        }

        private void OnFavoriteClicked(string folderPath)
        {
            _favoriteAnchorPath = folderPath ?? string.Empty;
            _favoriteAnchorOriginFolder = _selectedFolderPath;
            _favoriteAnchorOriginTag = _selectedTagFilterId;
            FolderSelected?.Invoke(folderPath);
            RepaintIfAlreadySelected(folderPath);
        }

        private void OnFavoriteRevealRequested(string folderPath)
        {
            ClearFavoriteAnchor();
            FolderSelected?.Invoke(folderPath);
            RepaintIfAlreadySelected(folderPath);
        }

        // A click on the folder that is already selected changes no pane
        // state, so no refresh tick follows to move the highlight between
        // sections; paint the new owner now. Every other case is painted by
        // the refresh the navigation raises.
        private void RepaintIfAlreadySelected(string folderPath)
        {
            if (string.Equals(_selectedFolderPath, folderPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
            }
        }

        private void OnChildContextMenuRequested(Rect anchor, ZepFolderEntry entry)
        {
            ContextMenuRequested?.Invoke(anchor, entry);
        }

        private void OnChildFolderDropReceived(string targetFolderPath, IReadOnlyList<string> projectPaths)
        {
            FolderDropReceived?.Invoke(targetFolderPath, projectPaths);
        }

        private void OnChildFolderExternalImportReceived(string targetFolderPath, IReadOnlyList<string> externalPaths)
        {
            FolderExternalImportReceived?.Invoke(targetFolderPath, externalPaths);
        }

        private void OnChildTagDropReceived(string tagId, IReadOnlyList<string> projectPaths)
        {
            TagDropReceived?.Invoke(tagId, projectPaths);
        }

        private void OnChildFavoriteDropReceived(IReadOnlyList<string> folderPaths)
        {
            FavoriteDropReceived?.Invoke(folderPaths);
        }

        private void OnChildFavoriteRemoveRequested(string folderPath)
        {
            FavoriteRemoveRequested?.Invoke(folderPath);
        }

        private void OnChildFavoriteMoveRequested(string folderPath, string anchorFolderPath)
        {
            FavoriteMoveRequested?.Invoke(folderPath, anchorFolderPath);
        }

        private void OnSidebarDragUpdated(DragUpdatedEvent evt)
        {
            // Split drags originate from these very rows and aim at the
            // workspace's split zones; auto-scrolling the sidebar under them
            // would fight the gesture. Everything else — project assets,
            // external files (macOS Finder drags publish empty paths until
            // perform, so no payload gate here), scene objects — scrolls.
            if (ZepSplitDragAndDrop.TryRead(out _)) return;

            _dragAutoScroll.OnDragUpdated(evt.mousePosition);
        }

        // After an auto-scroll tick moved sidebar content under a stationary
        // drag cursor, re-derive each tree's drop-target highlight from what
        // now sits at that position. Each tree scopes the pick to itself, so
        // the tree the cursor is not over just clears.
        private void RefreshTreeDropTargetsAfterAutoScroll(Vector2 dragPanelPosition)
        {
            _assetsTree.RefreshViewportWindow();
            _packagesTree.RefreshViewportWindow();
            var picked = panel?.Pick(dragPanelPosition);
            _assetsTree.RefreshDropTargetFromHover(picked);
            _packagesTree.RefreshDropTargetFromHover(picked);
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            var staysInside = evt.relatedTarget is VisualElement next && Contains(next);
            EnableInClassList(ZepUiClasses.SidebarPaneFocused, staysInside);
            if (!staysInside)
            {
                EnableInClassList(ZepUiClasses.SidebarPaneKeyboard, false);
            }
        }

        // UI Toolkit has no :focus-visible, so "the last input was the
        // keyboard" is tracked here: any navigation key arms the ring, any
        // pointer press (OnFreshPointerDown) or focus leaving the pane clears it.
        private void OnKeyboardNavigation(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.Home:
                case KeyCode.End:
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                case KeyCode.Tab:
                    EnableInClassList(ZepUiClasses.SidebarPaneKeyboard, true);
                    break;
            }
        }

        private void RevealScrollerBriefly()
        {
            _scrollView.EnableInClassList(ZepUiClasses.SidebarScrollScrolling, true);
            if (_scrollerReveal == null)
            {
                _scrollerReveal = _scrollView.schedule.Execute(() => _scrollView.EnableInClassList(
                    ZepUiClasses.SidebarScrollScrolling, false));
            }

            _scrollerReveal.ExecuteLater(700);
        }

        private void OnFreshPointerDown(PointerDownEvent evt)
        {
            EnableInClassList(ZepUiClasses.SidebarPaneKeyboard, false);
            CancelSplitDragPreparation();
            ZepSplitDragAndDrop.Clear();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            CancelSplitDragPreparation();
            ZepSplitDragAndDrop.Clear();
        }

        private void CancelSplitDragPreparation()
        {
            _assetsTree.CancelSplitDragPreparation();
            _packagesTree.CancelSplitDragPreparation();
            _tagsFilter.CancelSplitDragPreparation();
            _favorites.CancelReorderDragPreparation();
        }
    }
}
