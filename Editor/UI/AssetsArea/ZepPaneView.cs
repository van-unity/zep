using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.TopBar;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Owns the feature composition and UI lifecycle for one browsing context.
    // Source invariants decide which features exist: Primary mounts its two
    // switchable modes, while an auxiliary mounts only its fixed feature.
    internal sealed class ZepPaneView : VisualElement
    {
        private readonly ZepState _panelState;
        private readonly ZepController _controller;
        private readonly Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>> _selectionAction;
        private readonly AssetDropOperations _dropOps;
        private readonly Func<IReadOnlyList<ZepAssetEntry>> _visibleAssetsProvider;
        private readonly VisualElement _splitNavigationBar;
        private readonly NavigationButtonsWidget _splitNavigationButtons;
        private readonly BreadcrumbWidget _splitBreadcrumb;
        private bool _isSubscribed;
        private bool _isRefreshing;
        private SubAssetDescriptor _committedSubAsset;

        internal ZepPaneView(
            ZepState panelState,
            ZepPaneState pane,
            UnityZepIconRepository iconRepository,
            ZepController controller = null,
            Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>> selectionAction = null,
            AssetViewMode? inheritedViewMode = null,
            int? inheritedGridItemSize = null,
            AssetDropOperations dropOps = null)
        {
            _panelState = panelState ?? throw new ArgumentNullException(nameof(panelState));
            Pane = pane ?? throw new ArgumentNullException(nameof(pane));
            if (!_panelState.IsLivePane(Pane))
            {
                throw new ArgumentException("The pane view requires a live pane.", nameof(pane));
            }

            if (iconRepository == null)
            {
                throw new ArgumentNullException(nameof(iconRepository));
            }

            _controller = controller;
            _selectionAction = selectionAction;
            // The drop owner the shell built; the split view hands the same
            // instance to every pane it creates. Null only in fixtures that
            // never drop — a drop then routes nowhere, as it does on a pane
            // whose CanRouteInteraction() gate is shut.
            _dropOps = dropOps;

            name = "zep-pane-view";
            AddToClassList(ZepUiClasses.AssetsArea);
            focusable = true;

            var initialViewMode = Pane.Source.IsPrimary
                ? ReadPrimaryViewMode()
                : inheritedViewMode ?? ReadPrimaryViewMode();
            var initialGridItemSize = Mathf.Clamp(
                Pane.Source.IsPrimary
                    ? ZepThemePreferences.GetAssetGridItemSize()
                    : inheritedGridItemSize ?? ZepThemePreferences.GetAssetGridItemSize(),
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);

            // Both auxiliary kinds navigate: a folder pane within its root, a
            // tag pane into a tagged folder and back to its list.
            if (Pane.Source.IsAuxiliary)
            {
                var navigationBar = new VisualElement
                {
                    name = "zep-split-pane-navigation-bar"
                };
                // The drop pulse scales and fades this row.
                navigationBar.usageHints = UsageHints.DynamicTransform;
                navigationBar.AddToClassList(
                    ZepUiClasses.SplitPaneNavigationBar);
                _splitNavigationBar = navigationBar;

                _splitNavigationButtons = new NavigationButtonsWidget(
                    "zep-split-pane");
                _splitNavigationButtons.BackClicked += OnSplitBackClicked;
                _splitNavigationButtons.ForwardClicked += OnSplitForwardClicked;
                navigationBar.Add(_splitNavigationButtons);

                _splitBreadcrumb = new BreadcrumbWidget
                {
                    name = "zep-split-pane-breadcrumb"
                };
                _splitBreadcrumb.SegmentClicked += OnBreadcrumbSegmentClicked;
                _splitBreadcrumb.LeadingSegmentClicked += OnBreadcrumbLeadingSegmentClicked;
                navigationBar.Add(_splitBreadcrumb);
                Add(navigationBar);
            }

            BuildFeatures(
                iconRepository,
                initialViewMode,
                initialGridItemSize,
                out var browser,
                out var tags);

            AssetsArea = new AssetsAreaController(
                Pane.ActiveMode,
                initialViewMode,
                initialGridItemSize,
                browser,
                tags,
                showFloatingBreadcrumb: Pane.Source.IsPrimary);
            Add(AssetsArea);

            AssetsArea.ViewModeChanged += OnViewModeChanged;
            AssetsArea.GridItemSizeChanged += OnGridItemSizeChanged;
            AssetsArea.SelectionChanged += OnSelectionChanged;
            AssetsArea.OpenRequested += OnOpenRequested;
            AssetsArea.SubAssetSelected += OnSubAssetSelected;
            AssetsArea.SubAssetOpenRequested += OnSubAssetOpenRequested;
            AssetsArea.ContextMenuRequested += OnContextMenuRequested;
            AssetsArea.BreadcrumbSegmentClicked += OnBreadcrumbSegmentClicked;
            if (browser != null)
            {
                browser.FolderExpansionToggled += OnFolderExpansionToggled;
                // Drops are heard where they are raised: only the browser has
                // drop targets, and this view adds the pane and the captured
                // destination the drop owner needs.
                browser.FolderAssetDropReceived += OnFolderAssetDropReceived;
                browser.FolderAssetExternalImportReceived += OnFolderAssetExternalImportReceived;
                browser.CurrentFolderDropReceived += OnCurrentFolderDropReceived;
                browser.CurrentFolderExternalImportReceived += OnCurrentFolderExternalImportReceived;
                browser.SceneObjectDropReceived += OnSceneObjectDropReceived;
            }

            RegisterCallback<PointerDownEvent>(ActivateBoundPane, TrickleDown.TrickleDown);
            RegisterCallback<FocusInEvent>(ActivateBoundPane, TrickleDown.TrickleDown);
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            // Expansion-aware on purpose: the pane state resolves selected
            // paths against this, and expanded-folder child rows are real
            // selectable rows whose paths are NOT in the base folder listing.
            _visibleAssetsProvider = () => AssetsArea.ActiveVisibleAssets;
            // Provider first, subscription second: the pane raises a change for
            // the provider write, and subscribing before it made the view pull
            // in reaction to its own assignment. The provider itself must be in
            // place from construction — the pane state resolves selected
            // entries through it whether or not this view is mounted yet.
            AssignVisibleAssetsProvider();
            Bind();
            // No pull here: OnAttachToPanel owns the first one. A detached view
            // shows nothing, so a constructor pull was thrown away by the pull
            // the mount performs a moment later.
        }

        internal ZepPaneState Pane { get; }

        internal AssetsAreaController AssetsArea { get; }

        // The sub-asset whose commit currently stands in for the (cleared)
        // path selection — what Quick Look previews when the pane's own
        // selection is empty. Null whenever a main selection is current.
        internal SubAssetDescriptor CommittedSubAsset => _committedSubAsset;

        internal AssetsBrowserFeature BrowserFeature => AssetsArea.BrowserFeature;

        internal AssetViewMode ViewMode => AssetsArea.CurrentViewMode;

        internal int GridItemSize => AssetsArea.CurrentGridItemSize;

        internal float ResponsiveWidth => AssetsArea.ResponsiveWidth;

        internal IReadOnlyList<ZepAssetEntry> ActiveAssets => AssetsArea.ActiveAssets;

        // The auxiliary nav row: arrows, breadcrumb, and whatever the split
        // workspace appends (its close control). Null on the primary pane,
        // which navigates from the top bar and has no row of its own.
        internal VisualElement SplitNavigationBar => _splitNavigationBar;

        internal event Action<ZepPaneState> OpenRequested;
        internal event Action<ZepPaneState> PresentationChanged;
        internal event Action<ZepPaneState, Rect, ZepAssetEntry> ContextMenuRequested;

        // Test-only tally of the pulls this view actually performed. The
        // discriminating number is per mount: construction contributes none,
        // the attach handler contributes exactly one.
        internal static int RefreshFromPaneCountForTests;

        internal void RefreshFromPane()
        {
            if (_isRefreshing || !_panelState.IsLivePane(Pane))
            {
                return;
            }

            RefreshFromPaneCountForTests++;
            _isRefreshing = true;
            try
            {
                AssetsArea.SetActiveMode(Pane.ActiveMode);
                AssetsArea.SetBreadcrumbPath(
                    Pane.BreadcrumbRootPath.Value,
                    Pane.BreadcrumbCurrentPath.Value);
                // Before the asset pull: RefreshActiveAssets projects the list
                // through the expansion set, so the set must be current first.
                // Disabled under any active filter — filtered projections
                // render flat (and selection reconciliation applies the same
                // gate, see BuildExpansionVisibilityPredicate).
                AssetsArea.BrowserFeature?.SetFolderExpansion(
                    Pane.ActiveMode == AssetsPaneMode.Browser && !Pane.HasActiveAssetFilter(),
                    Pane.ExpandedAssetFolderPaths);
                AssetsArea.RefreshActiveAssets();
                // Beyond-container on purpose: the flag drives empty-state
                // copy ("…with this tag." vs "…match this tag and filter."),
                // and a tag view's own tag is its container, not a filter —
                // counting it made the plain message unreachable.
                AssetsArea.SetHasActiveFilter(Pane.HasAssetFilterBeyondContainer());
                // A real state-side selection displaces the committed
                // sub-asset — the pane-level mirror of the widgets' "non-empty
                // push clears the sub highlight" rule (an EMPTY push keeps it:
                // committing a sub clears the path selection first, and that
                // echo must not wipe the descriptor it accompanies).
                if (Pane.SelectedAssetPaths.Count > 0)
                {
                    _committedSubAsset = null;
                }

                AssetsArea.SetSelection(Pane.SelectedAssetPaths);
                Pane.SyncNavigationHistory();
                RefreshSplitNavigation();
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        internal void ApplyResponsiveWidth(float width) => AssetsArea.ApplyResponsiveWidth(width);

        internal void ApplyResponsiveItemHeights() => AssetsArea.ApplyResponsiveItemHeights();

        internal void NotifyIconPreviewsReady(IReadOnlyList<string> paths)
            => AssetsArea.NotifyIconPreviewsReady(paths);

        internal void ScrollPathIntoView(AssetPath path) => AssetsArea.ScrollActivePathIntoView(path.Value);

        internal void PingPath(string path) => AssetsArea.PingActivePath(path);

        internal void PingSubAsset(string parentPath, long subAssetLocalId)
            => AssetsArea.PingActiveSubAsset(parentPath, subAssetLocalId);

        internal void SetSelection(IReadOnlyCollection<AssetPath> paths) => AssetsArea.SetSelection(paths);

        internal void ApplyPresentation(AssetViewMode viewMode, int gridItemSize)
        {
            AssetsArea.SetViewMode(viewMode);
            AssetsArea.SetGridItemSize(gridItemSize);
        }

        internal void SelectAllAssets() => AssetsArea.SelectAllActiveAssets();

        internal bool MoveSelection(AssetSelectionMove move, bool extend)
        {
            if (AssetsArea.MoveActiveSelection(move, extend))
            {
                return true;
            }

            // The substrate ignores horizontal arrows in list mode; there they
            // are the Finder expansion keys (→ expand / step into, ← collapse
            // / step to parent). Plain moves only — shift stays a range key.
            if (extend
                || (move != AssetSelectionMove.Left && move != AssetSelectionMove.Right))
            {
                return false;
            }

            return TryHandleListExpansionKey(move == AssetSelectionMove.Right);
        }

        private bool TryHandleListExpansionKey(bool isExpandKey)
        {
            if (_controller == null
                || AssetsArea.CurrentViewMode != AssetViewMode.List
                || Pane.ActiveMode != AssetsPaneMode.Browser
                || Pane.HasActiveAssetFilter()
                || Pane.SelectedAssetPaths.Count != 1)
            {
                return false;
            }

            var selected = Pane.SelectedAssetPaths[0];
            var contents = _controller.BrowseProjection.GetFolderContentsOrNull(selected.Value);
            var isExpandableFolder = contents != null && contents.Count > 0;

            if (isExpandKey)
            {
                if (!isExpandableFolder)
                {
                    return false;
                }

                if (!Pane.IsAssetFolderExpanded(selected))
                {
                    Pane.SetAssetFolderExpanded(selected, true);
                    return true;
                }

                // Finder: → on an already-expanded folder steps into its
                // first child row.
                return AssetsArea.BrowserFeature?.TrySelectAssetPath(contents[0].Path) ?? false;
            }

            if (isExpandableFolder && Pane.IsAssetFolderExpanded(selected))
            {
                Pane.SetAssetFolderExpanded(selected, false);
                return true;
            }

            // Finder: ← on a collapsed or non-folder row walks up to the
            // parent row; rows directly under the current folder have no
            // parent row to walk to.
            var parent = ZepPathHelpers.GetParentFolder(selected.Value);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, Pane.CurrentFolderPath.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return AssetsArea.BrowserFeature?.TrySelectAssetPath(parent) ?? false;
        }

        internal bool TypeToSelectAsset(string prefix) => AssetsArea.TypeToSelectActiveAsset(prefix);

        internal void CancelInlineEdit() => AssetsArea.CancelInlineEdit();

        internal void CancelInteractions() => AssetsArea.CancelInteractions();

        internal void Teardown()
        {
            AssetsArea.CancelInteractions();
            if (_splitNavigationButtons != null)
            {
                _splitNavigationButtons.BackClicked -= OnSplitBackClicked;
                _splitNavigationButtons.ForwardClicked -= OnSplitForwardClicked;
            }

            if (_splitBreadcrumb != null)
            {
                _splitBreadcrumb.SegmentClicked -= OnBreadcrumbSegmentClicked;
                _splitBreadcrumb.LeadingSegmentClicked -= OnBreadcrumbLeadingSegmentClicked;
            }

            Unbind();
            ClearVisibleAssetsProvider();
        }

        private void BuildFeatures(
            UnityZepIconRepository iconRepository,
            AssetViewMode initialViewMode,
            int initialGridItemSize,
            out AssetsBrowserFeature browser,
            out SmartFolderFeature tags)
        {
            browser = null;
            tags = null;

            var browseProjection = _controller?.BrowseProjection;
            var presentationLookup = _controller != null
                ? (Func<string, string, AssetPresentationData>)_controller.GetPresentation
                : null;

            // Every pane browses folders: the primary and a rooted pane always,
            // a tag pane once it opens a tagged folder from its list.
            {
                browser = new AssetsBrowserFeature(
                    iconRepository,
                    initialViewMode,
                    initialGridItemSize,
                    _controller != null
                        ? (Func<IReadOnlyList<ZepAssetEntry>>)(
                            () => Pane.ProjectedAssets)
                        : null,
                    presentationLookup,
                    browseProjection != null
                        ? browseProjection.GetFolderContentsOrNull
                        : (Func<string, IReadOnlyList<ZepAssetEntry>>)null,
                    browseProjection != null
                        ? () => browseProjection.FolderContentsVersion
                        : (Func<int>)null,
                    _controller != null
                        ? path => IndexedPathQueries.IsFolder(_controller, path)
                        : (Func<string, bool>)null);
            }

            if (Pane.Source.Kind == ZepPaneSourceKind.Primary
                || Pane.Source.Kind == ZepPaneSourceKind.Tag)
            {
                tags = new SmartFolderFeature(
                    iconRepository,
                    initialViewMode,
                    initialGridItemSize,
                    new SmartFolderFeature.Options
                    {
                        ElementName = "zep-tags-feature",
                        EmptyMessage = "No assets with this tag.",
                        FilteredEmptyMessage = "No assets match this tag and filter.",
                        AssetSource = _controller != null
                            ? (Func<IReadOnlyList<ZepAssetEntry>>)(
                                () => Pane.ProjectedAssets)
                            : null,
                        PresentationLookup = presentationLookup,
                    });
            }
        }

        private void OnSelectionChanged(IReadOnlyList<ZepAssetEntry> selectedAssets)
        {
            if (_isRefreshing || !CanRouteInteraction())
            {
                return;
            }

            // Any user-intent main commit — including an empty one (empty-space
            // click, marquee over nothing) — displaces the committed sub-asset,
            // matching the widgets' highlight rule.
            _committedSubAsset = null;
            var paths = new AssetPath[selectedAssets.Count];
            for (var i = 0; i < selectedAssets.Count; i++)
            {
                paths[i] = AssetPath.From(selectedAssets[i].Path);
            }

            Pane.SetSelectedAssetPaths(paths);
            _selectionAction?.Invoke(Pane, selectedAssets);
        }

        // A sub-asset row was committed (click resolved without a drag). The
        // panel's path-based selection was already cleared by the collection
        // view; here the pane state follows, and Unity's Selection gets the
        // sub-OBJECT so the Inspector shows it. ExpectPanelSelectionChange
        // pre-arms the controller with the parent path — that is what the
        // Selection.selectionChanged echo resolves to (a sub-object's asset
        // path IS its parent's) — so the reverse sync doesn't bounce the
        // panel back onto the parent row.
        private void OnSubAssetSelected(SubAssetDescriptor subAsset)
        {
            if (_isRefreshing || !CanRouteInteraction())
            {
                return;
            }

            // Recorded BEFORE the pane clear: that clear raises PaneChanged
            // synchronously, and an open Quick Look rebuilds from it — the
            // descriptor must already be readable or it would fall back to
            // the sidebar folder for one rebuild.
            _committedSubAsset = subAsset;
            Pane.SetSelectedAssetPaths(Array.Empty<AssetPath>());

            var subObject = SubAssetCatalog.Resolve(subAsset);
            if (subObject == null)
            {
                return;
            }

            _controller?.ExpectPanelSelectionChange(Pane, new[] { subAsset.ParentPath });
            Selection.objects = new UnityEngine.Object[] { subObject };
        }

        private void OnSubAssetOpenRequested(SubAssetDescriptor subAsset)
        {
            if (_isRefreshing || !CanRouteInteraction())
            {
                return;
            }

            var subObject = SubAssetCatalog.Resolve(subAsset);
            if (subObject != null)
            {
                AssetDatabase.OpenAsset(subObject);
            }
        }

        // A list-row folder chevron was clicked. The pane state owns the
        // expansion set; its Expansion change flows back through the
        // controller (selection reconciliation) and RefreshFromPane, which
        // re-projects the list with the new set applied.
        private void OnFolderExpansionToggled(string folderPath)
        {
            if (_isRefreshing || !CanRouteInteraction())
            {
                return;
            }

            Pane.ToggleAssetFolderExpanded(AssetPath.From(folderPath));
        }

        private void OnViewModeChanged(AssetViewMode mode)
        {
            if (_isSubscribed && Pane.Source.IsPrimary)
            {
                EditorPrefs.SetString(
                    ZepThemePreferences.AssetViewModePreferenceKey,
                    mode.ToString());
            }

            if (_isSubscribed && Pane.Source.IsAuxiliary)
            {
                PresentationChanged?.Invoke(Pane);
            }
        }

        private void OnGridItemSizeChanged(int size)
        {
            if (_isSubscribed && Pane.Source.IsPrimary)
            {
                ZepThemePreferences.SetAssetGridItemSize(size);
            }

            if (_isSubscribed && Pane.Source.IsAuxiliary)
            {
                PresentationChanged?.Invoke(Pane);
            }
        }

        private void ActivateBoundPane(PointerDownEvent evt) => ActivateBoundPane();

        private void ActivateBoundPane(FocusInEvent evt) => ActivateBoundPane();

        private void ActivateBoundPane()
        {
            if (CanRouteInteraction()
                && !ReferenceEquals(_panelState.ActivePane, Pane))
            {
                _panelState.ActivatePane(Pane);
            }
        }

        private void OnOpenRequested()
        {
            if (CanRouteInteraction())
            {
                OpenRequested?.Invoke(Pane);
            }
        }

        private void OnContextMenuRequested(
            Rect anchor,
            ZepAssetEntry entry)
        {
            if (CanRouteInteraction())
            {
                ContextMenuRequested?.Invoke(Pane, anchor, entry);
            }
        }

        private void OnFolderAssetDropReceived(
            string folder,
            IReadOnlyList<string> paths)
        {
            if (CanRouteInteraction())
            {
                _dropOps?.HandleFolderDrop(Pane, folder, paths);
            }
        }

        private void OnFolderAssetExternalImportReceived(
            string folder,
            IReadOnlyList<string> paths)
        {
            if (CanRouteInteraction())
            {
                _dropOps?.HandleExternalImport(Pane, folder, paths);
            }
        }

        private void OnCurrentFolderDropReceived(IReadOnlyList<string> paths)
        {
            if (CanRouteInteraction())
            {
                _dropOps?.HandleFolderDrop(
                    Pane,
                    Pane.CurrentFolderPath.Value,
                    paths);
            }
        }

        private void OnCurrentFolderExternalImportReceived(
            IReadOnlyList<string> paths)
        {
            if (CanRouteInteraction())
            {
                _dropOps?.HandleExternalImport(
                    Pane,
                    Pane.CurrentFolderPath.Value,
                    paths);
            }
        }

        private void OnSceneObjectDropReceived(
            IReadOnlyList<GameObject> objects,
            string folder)
        {
            if (CanRouteInteraction())
            {
                _dropOps?.HandleSceneObjectDrop(
                    Pane,
                    objects,
                    string.IsNullOrEmpty(folder)
                        ? Pane.CurrentFolderPath.Value
                        : folder);
            }
        }

        private void OnBreadcrumbSegmentClicked(string path)
        {
            if (CanRouteInteraction())
            {
                Pane.TrySetFolderSelection(path);
            }
        }

        private void OnSplitBackClicked()
        {
            if (CanRouteInteraction())
            {
                Pane.NavigateBack();
            }
        }

        private void OnSplitForwardClicked()
        {
            if (CanRouteInteraction())
            {
                Pane.NavigateForward();
            }
        }

        // Internal because the tag crumb is the pane's identity, and a tag
        // rename or recolour is a global change that never reaches this
        // pane's own PaneChanged subscription: the split workspace pushes it
        // from its chrome refresh.
        internal void RefreshSplitNavigation()
        {
            if (_splitNavigationButtons == null)
            {
                return;
            }

            _splitNavigationButtons.SetCanGoBack(Pane.CanNavigateBack);
            _splitNavigationButtons.SetCanGoForward(Pane.CanNavigateForward);
            if (Pane.Source.Kind == ZepPaneSourceKind.Tag)
            {
                // "Tag › OpenedFolder › Sub" while a folder is open, just the
                // tag while the list shows; the tag crumb is the way back.
                ResolveTag(Pane.Source.TagId, out var tagName, out var tagColor);
                _splitBreadcrumb.SetLeadingSegment(tagName, tagColor);
                _splitBreadcrumb.SetPath(
                    Pane.BreadcrumbRootPath.Value,
                    Pane.TagBrowseRootPath.IsEmpty
                        ? string.Empty
                        : Pane.BreadcrumbCurrentPath.Value);
                return;
            }

            _splitBreadcrumb.SetPath(
                Pane.BreadcrumbRootPath.Value,
                Pane.BreadcrumbCurrentPath.Value);
        }

        private void OnBreadcrumbLeadingSegmentClicked()
        {
            if (CanRouteInteraction())
            {
                Pane.EnterMode(AssetsPaneMode.Tags);
            }
        }

        // The sidebar's options list only favorite or in-use tags, so an
        // unused tag resolves from its definition. A tag deleted behind an
        // open pane keeps its id as the crumb text and the palette's fallback
        // dot until the pane reconciles.
        private void ResolveTag(string tagId, out string name, out ZepTagColor color)
        {
            var options = _panelState.TagFilterOptions;
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].TagId, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    name = options[i].Tag.Name;
                    color = options[i].Tag.Color;
                    return;
                }
            }

            var definitions = _controller?.GetSharedTags();
            for (var i = 0; definitions != null && i < definitions.Count; i++)
            {
                if (string.Equals(definitions[i].Id, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    name = definitions[i].Name;
                    color = definitions[i].Color;
                    return;
                }
            }

            name = tagId;
            color = ZepTagColor.None;
        }

        private bool CanRouteInteraction() =>
            _isSubscribed && _panelState.IsLivePane(Pane);

        private void OnPaneChanged(ZepPaneChange change)
        {
            if (ReferenceEquals(change.OldPane, Pane) && !_panelState.IsLivePane(Pane))
            {
                Teardown();
                return;
            }

            if (change.Kind != ZepPaneChangeKind.Updated
                || !ReferenceEquals(change.NewPane, Pane)
                || (_controller != null && _controller.IsReactingToViewModelChange))
            {
                return;
            }

            if (change.Update == ZepPaneUpdate.History)
            {
                // SyncNavigationHistory publishes after the useful pane paint.
                // Only the split navigation buttons depend on that notification;
                // re-pulling and republishing the asset collection here made one
                // folder switch paint the pane twice.
                RefreshSplitNavigation();
                return;
            }

            RefreshFromPane();
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            // Same order as construction, for the same reason: the provider
            // write must not echo back into a pull this method is about to do.
            AssignVisibleAssetsProvider();
            Bind();
            RefreshFromPane();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            AssetsArea.CancelInlineEdit();
            Unbind();
            ClearVisibleAssetsProvider();
        }

        private void Bind()
        {
            if (_isSubscribed || !_panelState.IsLivePane(Pane))
            {
                return;
            }

            _panelState.PaneChanged += OnPaneChanged;
            _isSubscribed = true;
        }

        private void Unbind()
        {
            if (!_isSubscribed)
            {
                return;
            }

            _panelState.PaneChanged -= OnPaneChanged;
            _isSubscribed = false;
        }

        private void AssignVisibleAssetsProvider()
        {
            if (_panelState.IsLivePane(Pane))
            {
                Pane.VisibleAssetsProvider = _visibleAssetsProvider;
            }
        }

        private void ClearVisibleAssetsProvider()
        {
            if (Pane.VisibleAssetsProvider == _visibleAssetsProvider)
            {
                Pane.VisibleAssetsProvider = null;
            }
        }

        private static AssetViewMode ReadPrimaryViewMode()
        {
            var stored = EditorPrefs.GetString(
                ZepThemePreferences.AssetViewModePreferenceKey,
                AssetViewMode.List.ToString());
            return Enum.TryParse(stored, true, out AssetViewMode parsed)
                ? parsed
                : AssetViewMode.List;
        }
    }
}
