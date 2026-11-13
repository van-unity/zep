using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.State;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Domain;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Settings;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using Zep.Editor.UI.Sidebar;
using Zep.Editor.UI.Theming;
using Zep.Editor.UI.TopBar;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Zep.Core.State.ZepSelectionPredicates;

namespace Zep.Editor.UI
{
    public sealed class ZepShellView : VisualElement, IZepShortcutHost
    {
        private const string ThemeStyleSheetPathFormat = "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_{0}.uss";
        private const float MinAssetPaneWidth = 300f;
        private const float MinAssetPaneWidthNarrow = 180f;

        private readonly ZepState _viewModel;
        private bool _isStateSubscribed;
        private ZepPaneState _lastLocalActivePane;
        private readonly ZepController _commandHandler;
        private readonly Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>> _assetSelectionAction;
        private readonly UnityAssetsContextMenuProvider _contextMenuProvider;
        private TopBarWidget _topBar;
        private VisualElement _contentArea;
        private VisualElement _initialScanOverlay;
        private Label _initialScanMessage;
        private VisualElement _initialScanFolder;
        private readonly VisualElement[] _initialScanFiles = new VisualElement[3];
        private readonly VisualElement[] _initialScanDots = new VisualElement[3];
        private IVisualElementScheduledItem _initialScanAnimation;
        private double _initialScanAnimationStartedAt;
        private SidebarWidget _sidebar;
        private VisualElement _assetPane;
        private ZepSplitWorkspaceView _splitWorkspace;
        private ZepPaneSplitterWidget _paneSplitter;
        private float _folderPaneWidthAtDragStart;
        private ZepOverlayPageController _overlayController;
        private VisualElement _settingsBackdrop;
        private ZepStatusBarWidget _statusBar;
        private ContextMenuWidget _contextMenu;
        private ContextMenuActions _actions;
        private ZepPromptOverlayWidget _promptOverlay;
        private ZepCreateOrchestrator _createOrchestrator;
        private AssetDropOperations _dropOps;
        private bool _isRefreshing;
        private bool _sidebarVisible;
        private bool _bypassAssetPaneWidthCache;
        private bool _registryThemesSubscribed;
        private ZepThemeSwitcher _themeSwitcher;
        private ZepShortcutController _shortcutController;
        private SearchBarWidget _searchOverlay;
        private ZepSearchWindow _searchWindow;
        private ZepSearchScope _searchOverlayScope;
        private string _searchContextKey;
        private ZepToast _toast;

        internal ZepThemeSwitcher ThemeSwitcher => _themeSwitcher;
        internal ZepShortcutController ShortcutController => _shortcutController;

        // Companion-window seams (ZepQuickLookWindow): the Quick Look
        // window lives outside this element tree but mirrors the panel's
        // selection, thumbnails, and theme, so it reads them through the shell.
        internal ZepState ViewModel => _viewModel;
        internal ZepController Controller => _commandHandler;
        internal UnityZepIconRepository IconRepository => _iconRepository;
        internal ZepFolderEntry SelectedSidebarFolder => GetSelectedFolderEntry();
        // The active pane's committed sub-asset, standing in for its cleared
        // path selection (sub-assets never enter the path-keyed selection).
        internal SubAssetDescriptor ActiveSubAssetSelection =>
            GetMountedPaneView(LocalActivePane)?.CommittedSubAsset;
        internal ZepPaneState LocalActivePane =>
            IsLocalPane(_lastLocalActivePane) ? _lastLocalActivePane
                : IsLocalPane(_viewModel.ActivePane) ? _viewModel.ActivePane
                : _viewModel.PrimaryPane;
        internal event Action<string> ThemeApplied;
        internal event Action SplitArrangementChanged;

        internal const string PendingCreateSelectionSessionKey =
            "Zep.PendingCreateSelection";

        private readonly UnityZepIconRepository _iconRepository;
        private readonly ZepThemeRegistry _registry;
        // Null when the host (Bootstrap) didn't supply one — theme creator stays disabled.
        // Tests that don't exercise theme creation pass null implicitly via the public ctor.
        private readonly IThemeSaveOrchestrator _themeSaveOrchestrator;

        internal ZepThemeRegistry Registry => _registry;

        public ZepShellView(
            ZepState viewModel,
            ZepController commandHandler = null,
            Action<IReadOnlyList<ZepAssetEntry>> assetSelectionAction = null,
            UnityZepIconRepository iconRepository = null)
            : this(
                viewModel,
                commandHandler,
                assetSelectionAction == null
                    ? (Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>>)null
                    : (pane, assets) => assetSelectionAction(assets),
                null,
                iconRepository,
                null)
        {
        }

        internal ZepShellView(
            ZepState viewModel,
            ZepController commandHandler,
            Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>> assetSelectionAction,
            UnityAssetsContextMenuProvider contextMenuProvider = null,
            UnityZepIconRepository iconRepository = null,
            IThemeSaveOrchestrator themeSaveOrchestrator = null,
            ZepThemeRegistry registry = null,
            bool deferSecondaryUi = false)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _lastLocalActivePane = IsLocalPane(viewModel.ActivePane)
                ? viewModel.ActivePane : viewModel.PrimaryPane;
            _commandHandler = commandHandler;
            _assetSelectionAction = assetSelectionAction;
            _contextMenuProvider = contextMenuProvider ?? new UnityAssetsContextMenuProvider();
            _iconRepository = iconRepository ?? new UnityZepIconRepository();
            _registry = registry ?? new ZepThemeRegistry();
            _themeSaveOrchestrator = themeSaveOrchestrator;

            AddToClassList(ZepUiClasses.Root);
            // Package presence cannot change without a domain reload, which
            // rebuilds this shell, so it is read once. No controller (preview
            // seed, bare-shell tests) means no addressables lookups either.
            EnableInClassList(
                ZepUiClasses.RootNoAddressables,
                _commandHandler == null || !_commandHandler.IsAddressablesInstalled);
            focusable = true;
            _themeSwitcher = new ZepThemeSwitcher(
                this,
                _registry,
                _iconRepository,
                onCurrentThemeChanged: themeId =>
                {
                    _overlayController?.SetCurrentThemeId(themeId);
                    ThemeApplied?.Invoke(themeId);
                });
            SubscribeToRegistryThemes();

            _overlayController = new ZepOverlayPageController(
                _commandHandler,
                _themeSaveOrchestrator,
                _registry,
                _themeSwitcher,
                showToast: (message, kind) => _toast?.Show(message, kind),
                onCreateTagRequested: () => _actions?.PromptCreateTag(),
                onActiveChanged: ApplyOverlayPageState,
                themeCreatorHost: this);

            _shortcutController = new ZepShortcutController(this);
            _sidebarVisible = ZepThemePreferences.GetSidebarVisible();

            var __sw_uss = System.Diagnostics.Stopwatch.StartNew();
            LoadStyleSheet();
            __sw_uss.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ShellView.LoadStyleSheet ms={__sw_uss.Elapsed.TotalMilliseconds:F2}");

            var __sw_layout = System.Diagnostics.Stopwatch.StartNew();
            BuildLayout(deferSecondaryUi);
            __sw_layout.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ShellView.BuildLayout ms={__sw_layout.Elapsed.TotalMilliseconds:F2}");

            if (_sidebar != null)
            {
                _sidebar.style.display = _sidebarVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_paneSplitter != null)
            {
                _paneSplitter.style.display = _sidebarVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }
            _topBar?.SetSidebarActive(_sidebarVisible);

            _sidebar.ApplyResponsiveWidth(-1f, MinAssetPaneWidth, MinAssetPaneWidthNarrow);

            var __sw_theme = System.Diagnostics.Stopwatch.StartNew();
            _themeSwitcher.SetTheme(_themeSwitcher.CurrentTheme, false);
            __sw_theme.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ShellView._themeSwitcher.SetTheme ms={__sw_theme.Elapsed.TotalMilliseconds:F2}");

            var __sw_bind = System.Diagnostics.Stopwatch.StartNew();
            Bind();
            __sw_bind.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ShellView.Bind (incl. first RefreshView) ms={__sw_bind.Elapsed.TotalMilliseconds:F2}");
            PlayMaterialEntranceAnimation();

            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<KeyUpEvent>(_shortcutController.HandleKeyUp, TrickleDown.TrickleDown);
            RegisterCallback<PointerDownEvent>(_ => _shortcutController.ResetDoubleShift(), TrickleDown.TrickleDown);
            // On macOS the Edit-menu accelerators (⌘A, ⌘D, ⌘F, ⌘⌫) reach the
            // focused window as command events instead of key events; the
            // shortcut controller handles both routes.
            RegisterCallback<ValidateCommandEvent>(OnValidateCommand, TrickleDown.TrickleDown);
            RegisterCallback<ExecuteCommandEvent>(OnExecuteCommand, TrickleDown.TrickleDown);
            RegisterCallback<FocusOutEvent>(OnPanelFocusOut);
            RegisterCallback<FocusInEvent>(_ => RestoreLocalPaneFocus());
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            schedule.Execute(() => ApplyResponsiveLayout(resolvedStyle.width)).ExecuteLater(0);
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            // Bind() is idempotent: subsequent calls are a no-op because the first
            // call (from the constructor) already subscribed and ran the initial
            // RefreshView. We don't run another RefreshView here — the view model is
            // fully hydrated by the time the shell is constructed (Controller's
            // InitializeIndex runs synchronously on cache hit before Bootstrap.BuildRoot
            // calls `new ZepShellView`), so the ctor-time RefreshView already
            // sees the final state. A second refresh here was pure repaint cost (~60 ms
            // on a 36k-record project) with no visible change.
            Bind();
            _iconRepository.PreviewsReady += OnIconPreviewsReady;
            // Material thumbnails share the icon repository's delivery path:
            // both events mean "an assets-view icon texture changed, rebind
            // visible rows". The provider is a domain-shared static, so the
            // subscription is per-attach like PreviewsReady, not per-window.
            AssetsArea.MaterialThumbnails.MaterialThumbnailProvider.ThumbnailsReady += OnMaterialThumbnailsReady;
            SubscribeToRegistryThemes();
            if (_commandHandler != null)
            {
                _commandHandler.AssetCatalogChanged += OnAssetCatalogChanged;
                _commandHandler.AssetFrameRequested += OnAssetFrameRequested;
            }
            // Re-apply the full-scan overlay display once we're actually attached.
            // BuildLayout set `style.display = Flex` while the shell was still detached;
            // on the cache-miss path the overlay needs to actually paint
            // now that we're in a real panel. Cheap (one bool read + one style write),
            // never adds RefreshView's per-keystroke cost.
            ApplyInitialScanOverlayState();
            schedule.Execute(() =>
            {
                var focusedElement = panel?.focusController?.focusedElement as VisualElement;
                if (focusedElement == null)
                {
                    Focus();
                }
            }).ExecuteLater(0);
        }

        // The folder context menus' Add/Remove from Favorites. Favorites
        // mutate their own store, not the asset catalog, so no controller
        // round-trip arrives to repaint the sidebar — refresh explicitly, as
        // the sidebar's own drop/remove/reorder handlers do.
        private void ToggleFavorite(string folderPath)
        {
            var store = ZepFavoritesSettings.instance.Store;
            if (store.Remove(folderPath) || store.Add(folderPath))
            {
                RefreshView();
            }
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            if (_searchWindow != null) _searchWindow.Close();
            _shortcutController.ResetDoubleShift();
            _initialScanAnimation?.Pause();
            _paneSplitter?.CancelDrag();
            _splitWorkspace?.CancelDividerDrags();
            Unbind();
            _iconRepository.PreviewsReady -= OnIconPreviewsReady;
            AssetsArea.MaterialThumbnails.MaterialThumbnailProvider.ThumbnailsReady -= OnMaterialThumbnailsReady;
            if (_commandHandler != null)
            {
                _commandHandler.AssetCatalogChanged -= OnAssetCatalogChanged;
                _commandHandler.AssetFrameRequested -= OnAssetFrameRequested;
            }
            UnsubscribeFromRegistryThemes();
        }

        private void SubscribeToRegistryThemes()
        {
            if (_registryThemesSubscribed)
            {
                return;
            }

            _registry.ThemesChanged += OnRegistryThemesChanged;
            _registryThemesSubscribed = true;
        }

        private void UnsubscribeFromRegistryThemes()
        {
            if (!_registryThemesSubscribed)
            {
                return;
            }

            _registry.ThemesChanged -= OnRegistryThemesChanged;
            _registryThemesSubscribed = false;
        }

        internal void Teardown()
        {
            OnDetachFromPanel(null);
            _initialScanAnimation?.Pause();
            _initialScanAnimation = null;
            _splitWorkspace?.Teardown();
            // Release the workspace this shell built: the window keeps its
            // reference to the shell after tearing it down (OnDisable), and a
            // deferred callback armed before the teardown still holds this
            // instance. Dropping the reference here means nothing scheduled
            // earlier can drive a torn-down pane. Every read of these two is
            // null-safe, and the window always rebuilds a fresh shell rather
            // than reviving this one.
            _splitWorkspace = null;
            _assetPane = null;
            UnsubscribeFromRegistryThemes();
            _themeSwitcher.Teardown();
            _iconRepository.Teardown();
        }

        private void OnAssetCatalogChanged(AssetCatalogDelta delta)
        {
            if (_commandHandler == null)
            {
                return;
            }

            if (delta == null)
            {
                _sidebar?.InvalidateFolderCatalog();
                _iconRepository.InvalidateAssetIconCache();
                return;
            }

            _sidebar?.ApplyCatalogDelta(delta);

            for (var i = 0; i < delta.RemovedRecords.Count; i++)
            {
                _iconRepository.InvalidateAssetIcon(
                    delta.RemovedRecords[i].AssetPath);
            }

            for (var i = 0; i < delta.UpsertedRecords.Count; i++)
            {
                _iconRepository.InvalidateAssetIcon(
                    delta.UpsertedRecords[i].AssetPath);
            }
        }

        // An asset was pinged from outside the panel (e.g. clicking a Sprite
        // reference on a SpriteRenderer in the Inspector). The controller has
        // already navigated the browser to the asset's folder and selected it
        // synchronously, so the active feature's list is repopulated and we can
        // frame the path and play its bloom — after the same synchronous reveal
        // ordering used by search exit.
        private void OnAssetFrameRequested(
            ZepPaneState pane,
            string assetPath)
        {
            // A sub-asset reveal frames its own sub row after the core reveal
            // returns; the parent-row ping requested here would fight it (two
            // blooms, wrong row).
            if (_isRoutingSubAssetFrame)
            {
                return;
            }

            var paneView = GetMountedPaneView(pane);
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/ping] frame request path={assetPath} paneViewMounted={paneView != null}");
            paneView?.PingPath(assetPath);
        }

        // True only while RevealAndFrameSubAsset runs the core reveal, whose
        // AssetFrameRequested fires synchronously inside the call.
        private bool _isRoutingSubAssetFrame;

        // Reveal-and-frame for a ping that resolved to a SUB-asset (see
        // ZepPingReceiver): Core reveals the parent path — sub-assets
        // are a view-layer identity Core never sees — and this shell then
        // frames the sub-asset's own row: parent disclosure expanded, sub row
        // highlighted and bloomed. The parent selection the reveal applied is
        // replaced by the sub highlight (a sub-asset selection clears the path
        // selection — the same rule as an in-panel sub-asset click), without
        // touching Unity's Selection: a ping frames, it never selects.
        // Returns the core reveal's result (false = parent not in the index
        // yet; the receiver refreshes and retries).
        internal bool RevealAndFrameSubAsset(string parentPath, long subAssetLocalId)
        {
            if (_commandHandler == null)
            {
                return false;
            }

            bool revealed;
            _isRoutingSubAssetFrame = true;
            try
            {
                revealed = _commandHandler.RevealAndFrameAsset(parentPath);
            }
            finally
            {
                _isRoutingSubAssetFrame = false;
            }

            if (!revealed)
            {
                return false;
            }

            var pane = _viewModel.PrimaryPane;
            pane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
            GetMountedPaneView(pane)?.PingSubAsset(parentPath, subAssetLocalId);
            return true;
        }

        private void OnRegistryThemesChanged()
        {
            var fellBack = !_themeSwitcher.CurrentThemeStillExists();
            var targetThemeId = fellBack
                ? ZepThemeRegistry.DefaultThemeId
                : _themeSwitcher.CurrentTheme;

            _overlayController?.RefreshThemeOptionsFromRegistry();
            _overlayController?.SetCurrentThemeId(targetThemeId);
            // The registry republishes on every theme asset import, and re-applying an
            // unchanged theme is not free: it removes and re-adds the root sheet, swaps
            // the root class, drops the icon cache and fans ThemeApplied out to every
            // mirror (settings, Quick Look, popups). Skip it only when the theme is
            // genuinely still applied — a reimported sheet is a new object and must swap,
            // and a fallback (fellBack) always targets a different id.
            if (!string.IsNullOrEmpty(targetThemeId)
                && !_themeSwitcher.IsAppliedThemeUpToDate(targetThemeId))
                _themeSwitcher.SetTheme(targetThemeId, persistPreference: fellBack);
        }

        // Fired by the icon repository when an async asset preview lands and
        // by the material-thumbnail provider when a custom thumb lands.
        // Rebind the already-constructed list / grid items in place so their
        // icons swap without the visual churn of a full list Rebuild().
        // Coalesced through EditorApplication.delayCall — NOT the panel
        // scheduler, which only ticks when the panel repaints: an idle
        // editor would then hold finished thumbnails hostage until some
        // unrelated interaction forced a repaint. delayCall fires on the
        // next editor update unconditionally, and one refresh pass rebinds
        // every row named by the raises that coalesced into it.
        private bool _iconRefreshQueued;
        // The paths whose icon changed since the queued refresh was armed.
        // Accumulated because several raises can coalesce into one delayCall.
        private readonly List<string> _pendingIconRefreshPaths = new List<string>();
        // The material-thumbnail provider does not report which materials
        // finished, so its raise degrades the batch to "rebind everything".
        private bool _iconRefreshAllPaths;

        private void OnMaterialThumbnailsReady()
        {
            _iconRefreshAllPaths = true;
            QueueIconRefresh();
        }

        private void OnIconPreviewsReady(IReadOnlyList<string> appliedPaths)
        {
            if (appliedPaths == null || appliedPaths.Count == 0)
            {
                _iconRefreshAllPaths = true;
            }
            else
            {
                for (var i = 0; i < appliedPaths.Count; i++)
                {
                    _pendingIconRefreshPaths.Add(appliedPaths[i]);
                }
            }

            QueueIconRefresh();
        }

        private void QueueIconRefresh()
        {
            if (_iconRefreshQueued)
            {
                return;
            }

            _iconRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _iconRefreshQueued = false;
                // The window can close (or the tab detach) between arming this
                // callback and the editor turn that runs it, leaving no live
                // views to rebind. Same door as ZepBootstrap's
                // deferred theme discovery. The pending batch is kept, not
                // consumed: a re-attached shell rebinds its rows anyway, and a
                // dropped batch must not look like a delivered one.
                if (panel == null)
                {
                    return;
                }

                _splitWorkspace?.NotifyIconPreviewsReady(
                    _iconRefreshAllPaths ? null : _pendingIconRefreshPaths);
                _pendingIconRefreshPaths.Clear();
                _iconRefreshAllPaths = false;
            };
        }

        private void BuildLayout(bool deferSecondaryUi)
        {
            _topBar = new TopBarWidget();
            _searchOverlay = new SearchBarWidget(icons: _iconRepository,
                historyKey: SearchBarWidget.HistoryPreferenceKey);
            _searchOverlay.FiltersChanged += RefreshSearchResults;
            _searchOverlay.BroadenRequested += () =>
            {
                if (!_viewModel.PrimaryPane.SearchScopeOptions.Any(option => option.Scope == ZepSearchScope.All)) return;
                _searchOverlayScope = ZepSearchScope.All;
                RefreshSearchScopeRow();
                RefreshSearchResults();
            };
            _searchOverlay.SearchTextChanged += OnSearchTextChanged;
            _searchOverlay.OpenRequested += OnSearchResultSelected;
            _contentArea = BuildContentArea();
            _statusBar = new ZepStatusBarWidget();
            WireTopBarEvents();

            Add(_contentArea);
            Add(_statusBar);
            Add(_searchOverlay);

            ApplyOverlayPageState();
            ApplyInitialScanOverlayState();

            if (deferSecondaryUi)
            {
                schedule.Execute(CompleteSecondaryUi).ExecuteLater(0);
            }
            else
            {
                CompleteSecondaryUi();
            }
        }

        private void CompleteSecondaryUi()
        {
            if (_actions != null)
            {
                return;
            }

            _promptOverlay = new ZepPromptOverlayWidget();
            _createOrchestrator = new ZepCreateOrchestrator(
                _viewModel,
                _commandHandler,
                pane => GetMountedPaneView(pane)?.BrowserFeature,
                OnCreatedAssetLanded,
                RestoreOwningWindowFocus,
                // A refused rename already writes the status bar, but the bar
                // drops a message identical to the one it is showing, so trying
                // the same taken name twice said nothing the second time. The
                // toast is the channel that reacts every time — the same one
                // the drop conflict is given.
                showError: message => _toast?.Show(message, ZepToastKind.Error));

            _toast = new ZepToast();

            _actions = new ContextMenuActions(
                _commandHandler,
                _createOrchestrator,
                _promptOverlay,
                _viewModel,
                _sidebar,
                _overlayController,
                OpenFolderFromPane,
                // The sidebar's folder rename has the same taken-name refusal
                // as the inline editor above, and the same reason to reach
                // the toast: the status bar drops a repeated message.
                showError: message => _toast?.Show(message, ZepToastKind.Error),
                isFavorite: path => ZepFavoritesSettings.instance.Store.Contains(path),
                toggleFavorite: ToggleFavorite);
            _contextMenu = new ContextMenuWidget(_contextMenuProvider, _actions, this);

            _initialScanOverlay = BuildInitialScanOverlay(out _initialScanMessage);

            Add(_promptOverlay);
            Add(_toast);
            Add(_initialScanOverlay);

            ApplyInitialScanOverlayState();
        }

        // Full-shell overlay shown only while a cache miss forces the off-thread
        // filesystem walk. Cache-hit domain reloads hydrate silently.
        private VisualElement BuildInitialScanOverlay(out Label message)
        {
            var overlay = new VisualElement { name = "zep-initial-scan-overlay" };
            overlay.AddToClassList(ZepUiClasses.AbsoluteFill);
            overlay.AddToClassList(ZepUiClasses.ScanOverlay);
            overlay.pickingMode = PickingMode.Position;
            overlay.style.display = DisplayStyle.None;

            var card = new VisualElement { name = "zep-initial-scan-card" };
            card.AddToClassList(ZepUiClasses.ScanCard);

            var scene = new VisualElement { pickingMode = PickingMode.Ignore };
            scene.AddToClassList(ZepUiClasses.ScanScene);
            var shadow = new VisualElement();
            shadow.AddToClassList(ZepUiClasses.ScanShadow);
            scene.Add(shadow);

            for (var i = 0; i < _initialScanFiles.Length; i++)
            {
                var file = new VisualElement
                {
                    name = "zep-initial-scan-file-" + i,
                    usageHints = UsageHints.DynamicTransform
                };
                file.AddToClassList(ZepUiClasses.ScanFile);
                for (var lineIndex = 0; lineIndex < 2; lineIndex++)
                {
                    var line = new VisualElement();
                    line.AddToClassList(ZepUiClasses.ScanFileLine);
                    file.Add(line);
                }
                _initialScanFiles[i] = file;
                scene.Add(file);
            }

            _initialScanFolder = new VisualElement
            {
                name = "zep-initial-scan-folder",
                usageHints = UsageHints.DynamicTransform
            };
            _initialScanFolder.AddToClassList(ZepUiClasses.ScanFolder);
            var tab = new VisualElement();
            tab.AddToClassList(ZepUiClasses.ScanFolderTab);
            _initialScanFolder.Add(tab);
            var face = new VisualElement();
            face.AddToClassList(ZepUiClasses.ScanFolderFace);
            for (var i = 0; i < 2; i++)
            {
                var eye = new VisualElement();
                eye.AddToClassList(ZepUiClasses.ScanFolderEye);
                face.Add(eye);
            }
            _initialScanFolder.Add(face);
            scene.Add(_initialScanFolder);
            card.Add(scene);

            var title = new Label("Building project index")
            {
                name = "zep-initial-scan-title"
            };
            title.AddToClassList(ZepUiClasses.ScanTitle);
            card.Add(title);

            message = new Label("This can take a moment. The saved index is reused while the project's files and folders are unchanged.")
            {
                name = "zep-initial-scan-message"
            };
            message.AddToClassList(ZepUiClasses.ScanMessage);
            card.Add(message);

            var dots = new VisualElement();
            dots.AddToClassList(ZepUiClasses.ScanDots);
            for (var i = 0; i < _initialScanDots.Length; i++)
            {
                var dot = new VisualElement { usageHints = UsageHints.DynamicTransform };
                dot.AddToClassList(ZepUiClasses.ScanDot);
                _initialScanDots[i] = dot;
                dots.Add(dot);
            }
            card.Add(dots);
            overlay.Add(card);
            _initialScanAnimation = overlay.schedule.Execute(AnimateInitialScan).Every(16);
            _initialScanAnimation.Pause();
            return overlay;
        }

        private void ApplyInitialScanOverlayState()
        {
            if (_initialScanOverlay == null)
            {
                return;
            }

            var scanning = _viewModel?.IsIndexScanInProgress ?? false;
            _initialScanOverlay.style.display = scanning ? DisplayStyle.Flex : DisplayStyle.None;
            if (!scanning)
            {
                _initialScanAnimation?.Pause();
            }
            else if (panel != null && _initialScanAnimation != null && !_initialScanAnimation.isActive)
            {
                _initialScanAnimationStartedAt = EditorApplication.timeSinceStartup;
                AnimateInitialScan();
                _initialScanAnimation.Resume();
            }
        }

        private void AnimateInitialScan()
        {
            // Elapsed time keeps the motion's pace when editor frames are uneven.
            // Only transforms/opacity change; the indexing job never pays for layout.
            var cycle = (float)((EditorApplication.timeSinceStartup - _initialScanAnimationStartedAt) % 2.4d / 2.4d);
            _initialScanFolder.style.translate = new Translate(0f, -2f * Mathf.Sin(cycle * Mathf.PI * 2f));
            for (var i = 0; i < _initialScanFiles.Length; i++)
            {
                var t = (cycle + i / 3f) % 1f;
                var remaining = 1f - t;
                // A cubic arc lifts each paper from the left and drops it behind
                // the folder's face. Fade at both ends hides the loop's reset.
                var x = -74f * remaining * remaining * remaining
                    - 192f * remaining * remaining * t + 180f * remaining * t * t;
                var y = -180f * remaining * t + 35f * t * t * t;
                var file = _initialScanFiles[i];
                file.style.translate = new Translate(x, y);
                file.style.opacity = Mathf.SmoothStep(0f, 1f, t / 0.16f)
                    * (1f - Mathf.SmoothStep(0f, 1f, (t - 0.78f) / 0.22f));
                var scale = 0.82f + 0.18f * Mathf.Sin(t * Mathf.PI);
                file.style.scale = new Scale(new Vector2(scale, scale));

                var pulse = 0.5f + 0.5f * Mathf.Sin(cycle * Mathf.PI * 4f - i * 0.8f);
                _initialScanDots[i].style.translate = new Translate(0f, -3f * pulse);
                _initialScanDots[i].style.opacity = 0.35f + 0.65f * pulse;
            }
        }

        // The top bar's navigation, search, scope and type filter belong to the
        // MAIN assets view and never follow the focused pane: split views are
        // browsing conveniences with their own arrows and breadcrumb, and a tag
        // dropped into a split must not make the top bar read as if the main
        // view had switched to that tag. Only the plus button and the
        // selection-driven actions target the focused pane.
        private void WireTopBarEvents()
        {
            _topBar.BackClicked += () => _viewModel.PrimaryPane.NavigateBack();
            _topBar.ForwardClicked += () => _viewModel.PrimaryPane.NavigateForward();
            _topBar.SearchRequested += OpenSearch;
            _topBar.TypeFilterButtonMouseDown += OnTypeFilterButtonMouseDown;
            _topBar.TypeFilterButtonKeyDown += OnTypeFilterButtonKeyDown;
            _topBar.SearchScopeSelected += scope =>
            {
                if (_searchOverlay.IsVisible)
                {
                    _searchOverlayScope = scope;
                    RefreshSearchScopeRow();
                    RefreshSearchResults();
                }
                else
                    _viewModel.PrimaryPane.SetSelectedSearchScope(scope);
            };
            _topBar.PlusClicked += ShowCreateMenu;
            _topBar.ToolbarOverflowClicked += ToggleSettingsDrawer;
            _topBar.SidebarToggleClicked += ToggleSidebar;
        }

        private VisualElement BuildContentArea()
        {
            var workspaceRow = new VisualElement { name = "zep-workspace-row" };
            workspaceRow.AddToClassList(ZepUiClasses.WorkspaceRow);
            workspaceRow.AddToClassList(ZepUiClasses.ContentArea);

            var hasCustomFolderPaneWidth = ZepThemePreferences.HasFolderPaneWidth();
            var initialFolderPaneWidth = hasCustomFolderPaneWidth
                ? ZepThemePreferences.GetFolderPaneWidth()
                : SidebarWidget.DefaultPaneWidth;
            _sidebar = new SidebarWidget(
                _iconRepository,
                initialFolderPaneWidth,
                hasCustomFolderPaneWidth,
                _commandHandler);
            _sidebar.FolderSelected += OnFolderPaneFolderSelected;
            _sidebar.TagFilterSelected += SelectTagFilter;
            _sidebar.CreateTagRequested += () => _actions?.PromptCreateTag();
            _sidebar.ContextMenuRequested += OnFolderPaneContextMenuRequested;
            _dropOps = new AssetDropOperations(
                _commandHandler,
                _viewModel,
                _assetSelectionAction,
                (defaultName, onSubmit) => _promptOverlay.Show(
                    "Create Prefab Variant",
                    "Variant name",
                    defaultName,
                    "Create Variant",
                    onSubmit),
                // A failed move already writes the status bar, but the bar drops
                // a message identical to the one it is showing, so retrying the
                // same drop said nothing. The toast is the channel that reacts
                // every time — the same one the split view is given for its own
                // errors.
                showError: message => _toast?.Show(message, ZepToastKind.Error));

            _sidebar.FolderDropReceived += (folder, paths) =>
                _dropOps.HandleSidebarFolderDrop(_viewModel.PrimaryPane, folder, paths);
            _sidebar.FolderExternalImportReceived += (folder, paths) =>
                _dropOps.HandleSidebarExternalImport(_viewModel.PrimaryPane, folder, paths);
            _sidebar.TagDropReceived += (tagId, paths) =>
            {
                if (_dropOps.HandleTagDrop(tagId, paths))
                {
                    _sidebar.AnimateTagHighlight(tagId);
                }
            };
            // Favorites mutate their own store, not the asset catalog, so no
            // controller round-trip arrives to repaint the sidebar — refresh
            // explicitly after a successful mutation.
            _sidebar.FavoriteDropReceived += paths =>
            {
                var store = ZepFavoritesSettings.instance.Store;
                var added = false;
                for (var i = 0; i < paths.Count; i++)
                {
                    added |= store.Add(paths[i]);
                }

                if (added)
                {
                    RefreshView();
                }
            };
            _sidebar.FavoriteRemoveRequested += path =>
            {
                if (ZepFavoritesSettings.instance.Store.Remove(path))
                {
                    RefreshView();
                }
            };
            _sidebar.FavoriteMoveRequested += (path, anchorPath) =>
            {
                if (ZepFavoritesSettings.instance.Store.MoveBefore(path, anchorPath))
                {
                    RefreshView();
                }
            };

            _paneSplitter = new ZepPaneSplitterWidget();
            _paneSplitter.DragStarted += OnPaneSplitterDragStarted;
            _paneSplitter.DragMoved += OnPaneSplitterDragMoved;
            _paneSplitter.DragEnded += OnPaneSplitterDragEnded;

            _splitWorkspace = new ZepSplitWorkspaceView(
                _viewModel,
                _iconRepository,
                _commandHandler,
                _assetSelectionAction,
                message => _toast?.Show(message, ZepToastKind.Error),
                _dropOps);
            _splitWorkspace.OpenRequested += pane => _actions?.OpenSelected(pane);
            _splitWorkspace.ArrangementChanged += () =>
                SplitArrangementChanged?.Invoke();
            _splitWorkspace.ContextMenuRequested += OnAssetPaneContextMenuRequested;
            _assetPane = _splitWorkspace;

            var rightColumn = new VisualElement { name = "zep-right-column" };
            rightColumn.AddToClassList(ZepUiClasses.RightColumn);
            rightColumn.Add(_topBar);
            rightColumn.Add(_assetPane);

            workspaceRow.Add(_sidebar);
            workspaceRow.Add(_paneSplitter);
            workspaceRow.Add(rightColumn);
            // Later siblings paint over the workspace without changing its layout.
            _settingsBackdrop = new VisualElement { name = "zep-settings-backdrop" };
            _settingsBackdrop.AddToClassList(ZepUiClasses.ModalScrim);
            _settingsBackdrop.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                CloseOverlayReanchoringFocus();
                evt.StopPropagation();
            });
            workspaceRow.Add(_settingsBackdrop);
            workspaceRow.Add(_overlayController.SettingsScreen);
            return workspaceRow;
        }

        private void OnFolderPaneFolderSelected(string folderPath)
        {
            if (_isRefreshing)
            {
                return;
            }

            ActivatePrimaryAndNavigate(folderPath);
        }

        private void OnFolderPaneContextMenuRequested(Rect anchor, ZepFolderEntry folderEntry)
        {
            var primaryPane = _viewModel.PrimaryPane;
            _viewModel.ActivatePane(primaryPane);
            if (folderEntry != null)
            {
                ShowContextMenu(() => BuildContextMenuRequest(
                    primaryPane,
                    new[] { folderEntry.Path },
                    ZepContextMenuTargetKind.SidebarFolder,
                    selectedFolder: folderEntry,
                    anchor: anchor));

                return;
            }

            ShowContextMenu(() => BuildContextMenuRequest(
                primaryPane,
                Array.Empty<string>(),
                ZepContextMenuTargetKind.Background,
                anchor: anchor));
        }

        private void Bind()
        {
            if (_isStateSubscribed)
            {
                return;
            }

            _viewModel.GlobalChanged += RefreshGlobalChrome;
            _viewModel.PaneChanged += OnPaneChanged;

            _isStateSubscribed = true;
            // Initial render — analog of the old ShellViewModel.Bind() firing Changed.
            RefreshView();
        }

        private void Unbind()
        {
            if (!_isStateSubscribed)
            {
                return;
            }

            _viewModel.GlobalChanged -= RefreshGlobalChrome;
            _viewModel.PaneChanged -= OnPaneChanged;

            _isStateSubscribed = false;
        }

        private int __pp_refreshSeq;
        internal void RefreshView()
        {
            if (_isRefreshing)
            {
                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log("[Zep/perf] ShellView.RefreshView SKIPPED (already refreshing)");
                }
                return;
            }

            if (_commandHandler != null && _commandHandler.IsReactingToViewModelChange)
            {
                return;
            }

            using var measurement = ZepPerfLog.Measure(
                "shell",
                ZepPerfStage.ShellRefresh);
            _isRefreshing = true;
            // Capture the flag once: every stopwatch below is null when perf
            // logging is off, and the final Log call is guarded by the same
            // captured value so a mid-refresh toggle cannot null-deref.
            var __perfLog = ZepPerfLog.IsEnabled;
            var __sw_refresh = __perfLog ? System.Diagnostics.Stopwatch.StartNew() : null;
            var __seq = ++__pp_refreshSeq;
            var __sw_chrome = __perfLog ? new System.Diagnostics.Stopwatch() : null;
            var __sw_sidebar = __perfLog ? new System.Diagnostics.Stopwatch() : null;
            var __sw_overlay = __perfLog ? new System.Diagnostics.Stopwatch() : null;
            var __sw_buttons = __perfLog ? new System.Diagnostics.Stopwatch() : null;

            try
            {
                var activePane = _viewModel.ActivePane;
                var primaryPane = _viewModel.PrimaryPane;
                __sw_chrome?.Start();
                PushGlobalState();
                _topBar.SetSearchText(primaryPane.SearchText ?? string.Empty);
                if (_searchOverlay.IsVisible) RefreshSearchResults();
                RefreshSearchScopeRow();
                RefreshTypeFilterDropdown();
                UpdateNavigationButtons(primaryPane.CanNavigateBack, primaryPane.CanNavigateForward);
                __sw_chrome?.Stop();

                __sw_sidebar?.Start();
                if (_sidebar != null)
                {
                    _sidebar.SetFavoritePaths(ZepFavoritesSettings.instance.Store.Paths);
                    _sidebar.SetCurrentFolderPath(primaryPane.CurrentFolderPath);
                    _sidebar.SetSelectedFolderPath(primaryPane.SelectedFolderPath);
                    _sidebar.SetActiveTagId(primaryPane.SelectedTagFilterId);
                    _sidebar.SetCanCreateTag(_commandHandler != null);
                    _sidebar.Flush();
                }
                __sw_sidebar?.Stop();

                __sw_overlay?.Start();
                _overlayController.PushTagsState();
                __sw_overlay?.Stop();

                __sw_buttons?.Start();
                UpdateButtonStates();
                __sw_buttons?.Stop();
            }
            finally
            {
                _isRefreshing = false;
                __sw_refresh?.Stop();
                if (__perfLog) ZepPerfLog.Log($"[Zep/perf] ShellView.RefreshView seq={__seq} chromeMs={__sw_chrome.Elapsed.TotalMilliseconds:F2} sidebarMs={__sw_sidebar.Elapsed.TotalMilliseconds:F2} overlayMs={__sw_overlay.Elapsed.TotalMilliseconds:F2} buttonsMs={__sw_buttons.Elapsed.TotalMilliseconds:F2} totalMs={__sw_refresh.Elapsed.TotalMilliseconds:F2}");
            }
        }

        private int __pp_globalChromeSeq;

        private void RefreshGlobalChrome()
        {
            if (_isRefreshing)
            {
                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log("[Zep/perf] ShellView.RefreshGlobalChrome SKIPPED (already refreshing)");
                }
                return;
            }

            if (_commandHandler != null && _commandHandler.IsReactingToViewModelChange)
            {
                return;
            }

            using var measurement = ZepPerfLog.Measure(
                "shell",
                ZepPerfStage.ShellRefresh);
            _isRefreshing = true;
            var __perfLog = ZepPerfLog.IsEnabled;
            var __sw_global = __perfLog ? System.Diagnostics.Stopwatch.StartNew() : null;
            var __seq = ++__pp_globalChromeSeq;

            try
            {
                PushGlobalState();
                // The sidebar setters above are record-only; Flush is what
                // reaches the child widgets.
                _sidebar?.Flush();
                _overlayController.PushTagsState();
            }
            finally
            {
                _isRefreshing = false;
                __sw_global?.Stop();
                if (__perfLog) ZepPerfLog.Log($"[Zep/perf] ShellView.RefreshGlobalChrome seq={__seq} totalMs={__sw_global.Elapsed.TotalMilliseconds:F2}");
            }
        }

        // The slots owned by the window rather than by a pane. Shared by the
        // full RefreshView and the GlobalChanged-only path so the two cannot
        // drift; the sidebar setters here are record-only, the caller flushes.
        private void PushGlobalState()
        {
            ApplyInitialScanOverlayState();
            // The only shell-side chrome refresh for a split slot whose title
            // follows an auxiliary pane, so it belongs on the global path.
            _splitWorkspace?.RefreshChrome();
            _statusBar.UpdateStatus(_viewModel.StatusMessage);

            if (_sidebar != null)
            {
                _sidebar.SetTags(_viewModel.TagFilterOptions);
            }
        }

        private void OnPaneChanged(ZepPaneChange change)
        {
            // Closing a native window resets global activation to the primary
            // pane; that fallback does not change this window's local focus.
            if (change.Kind != ZepPaneChangeKind.Updated
                && change.Kind != ZepPaneChangeKind.Closed
                && IsLocalPane(change.NewPane))
            {
                _lastLocalActivePane = change.NewPane;
            }
            else if (!IsLocalPane(_lastLocalActivePane))
            {
                _lastLocalActivePane = IsLocalPane(_viewModel.ActivePane)
                    ? _viewModel.ActivePane : _viewModel.PrimaryPane;
            }

            if (_commandHandler != null && _commandHandler.IsReactingToViewModelChange)
            {
                return;
            }

            if (change.Kind == ZepPaneChangeKind.Updated
                && change.Update == ZepPaneUpdate.History)
            {
                // History is synchronized by the pane view after its useful
                // folder paint. The top-level buttons are the only shell state
                // that depends on this follow-up notification; a full refresh
                // needlessly republished the sidebar and all global chrome.
                if (ReferenceEquals(change.NewPane, _viewModel.PrimaryPane))
                {
                    UpdateNavigationButtons(
                        change.NewPane.CanNavigateBack,
                        change.NewPane.CanNavigateForward);
                }

                return;
            }

            if (change.Kind == ZepPaneChangeKind.Updated
                && change.Update == ZepPaneUpdate.Selection)
            {
                // The + button's enabled state now tracks the same selection-aware
                // create target ShowCreateMenu/Cmd+N resolve (F-012), so a
                // selection-only update must still refresh it even though nothing
                // else the shell pushes reads the pane's selected paths — an
                // activation arrives as its own Kind, and a full RefreshView here
                // would needlessly republish the sidebar and all global chrome for
                // a row click. Exact equality on purpose — the update is a [Flags]
                // value coalesced with '|' while notifications are deferred, so a
                // selection bundled with a navigation or projection change must
                // still take the full refresh below.
                if (ReferenceEquals(change.NewPane, LocalActivePane))
                {
                    UpdateButtonStates();
                }

                return;
            }

            // A docked pane window's pane may be the globally active one; its
            // own updates do not touch this window's chrome.
            if (change.Kind != ZepPaneChangeKind.Updated
                || ReferenceEquals(change.NewPane, LocalActivePane)
                || ReferenceEquals(change.NewPane, _viewModel.PrimaryPane))
            {
                RefreshView();
            }
        }

        private void UpdateNavigationButtons(bool canGoBack, bool canGoForward)
        {
            _topBar?.SetCanGoBack(canGoBack);
            _topBar?.SetCanGoForward(canGoForward);
        }

        private void RefreshTypeFilterDropdown()
        {
            if (_topBar == null)
            {
                return;
            }

            var selectedTypeFilter = _viewModel.PrimaryPane.SelectedTypeFilter ?? string.Empty;
            var options = GetTypeFilterOptionsSnapshot();
            ZepTypeFilterOption selectedOption = default;
            var foundSelectedOption = false;

            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].TypeName, selectedTypeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    selectedOption = options[i];
                    foundSelectedOption = true;
                    break;
                }
            }

            if (!foundSelectedOption)
            {
                selectedOption = options.Count > 0
                    ? options[0]
                    : new ZepTypeFilterOption(
                        string.Empty,
                        GetMountedPaneView(_viewModel.PrimaryPane)?.ActiveAssets?.Count ?? 0);
            }

            _topBar.SetTypeFilterButtonTooltip(FormatTypeFilterOptionLabel(selectedOption));
            _topBar.SetSelectedType(AssetEntryRenderer.ResolveTypeDisplayName(selectedTypeFilter));
        }

        private void RefreshSearchScopeRow()
        {
            if (_topBar == null)
            {
                return;
            }

            var primaryPane = _viewModel.PrimaryPane;
            var hasScopedFilter = _searchOverlay.IsVisible
                || !string.IsNullOrWhiteSpace(primaryPane.SearchText)
                || !string.IsNullOrWhiteSpace(primaryPane.SelectedTagFilterId);
            _topBar.SetSearchScope(
                primaryPane.SearchScopeOptions,
                hasScopedFilter,
                _searchOverlay.IsVisible ? _searchOverlayScope : primaryPane.SelectedSearchScope);
        }

        internal void SelectTagFilter(string selectedTagFilterId)
        {
            var primaryPane = _viewModel.PrimaryPane;
            if (!ReferenceEquals(_viewModel.ActivePane, primaryPane))
            {
                _viewModel.ActivatePane(primaryPane);
            }

            // Sidebar tags are navigation destinations. Re-selecting the current
            // destination keeps both the tag view and its asset selection intact.
            if (string.Equals(
                primaryPane.SelectedTagFilterId,
                selectedTagFilterId,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            primaryPane.ApplyTagView(selectedTagFilterId);

            // Tag views are global - the prior selection (which lived in the previous
            // folder context) no longer makes sense. Clearing both our list selection
            // and Unity's editor Selection prevents the inspector from continuing to
            // show a now-out-of-context asset.
            ClearAssetSelectionAndUnitySelection(primaryPane);
        }

        private void ClearAssetSelectionAndUnitySelection(ZepPaneState pane)
        {
            pane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
            // Tell the host to flush Unity's editor Selection too. We pass an
            // empty array rather than null so the host's HasSameSelection/etc. paths
            // treat this as an explicit clear, not a "no change" signal.
            _assetSelectionAction?.Invoke(pane, Array.Empty<ZepAssetEntry>());
        }

        private void OnTypeFilterButtonMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 0)
            {
                return;
            }

            ShowTypeFilterMenu();
            evt.StopPropagation();
        }

        private void OnTypeFilterButtonKeyDown(KeyDownEvent evt)
        {
            if (!IsTypeFilterActivationKey(evt.keyCode))
            {
                return;
            }

            ShowTypeFilterMenu();
            evt.StopPropagation();
        }

        private void ShowTypeFilterMenu()
        {
            if (_topBar == null) return;

            var options = GetTypeFilterOptionsSnapshot();
            var buttonRect = _topBar.GetTypeFilterButtonWorldBound();
            var pane = _viewModel.PrimaryPane;
            var currentId = pane.SelectedTypeFilter;

            var items = new List<ZepContextMenuItem>(options.Count);
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var label = FormatTypeFilterOptionLabel(option);
                var isChecked = string.Equals(option.TypeName, currentId, StringComparison.Ordinal);
                var commandPath = "Zep/TypeFilter/" + option.TypeName;
                items.Add(new ZepContextMenuItem(
                    commandPath,
                    commandPath,
                    label,
                    string.Empty,
                    true,
                    isChecked,
                    false,
                    Array.Empty<ZepContextMenuItem>(),
                    onClick: () => SelectTypeFilterForPane(pane, option.TypeName)));
            }

            ContextMenuPopupRenderer.Show(this, items, null, buttonRect);
        }

        private IReadOnlyList<ZepTypeFilterOption> GetTypeFilterOptionsSnapshot()
        {
            var primaryPane = _viewModel.PrimaryPane;
            var visibleCount = primaryPane.VisibleAssetsProvider?.Invoke()?.Count ?? 0;
            return primaryPane.TypeFilterOptions.Count > 0
                ? primaryPane.TypeFilterOptions
                : new[] { new ZepTypeFilterOption(string.Empty, visibleCount) };
        }

        internal void SelectTypeFilter(string selectedTypeFilter)
        {
            SelectTypeFilterForPane(_viewModel.PrimaryPane, selectedTypeFilter);
        }

        private void SelectTypeFilterForPane(
            ZepPaneState pane,
            string selectedTypeFilter)
        {
            if (_viewModel.IsLivePane(pane))
            {
                pane.SetSelectedTypeFilter(selectedTypeFilter);
            }
        }

        private static string FormatTypeFilterOptionLabel(ZepTypeFilterOption option)
        {
            var title = string.IsNullOrWhiteSpace(option.TypeName)
                ? "All Types"
                : AssetEntryRenderer.ResolveTypeDisplayName(option.TypeName);
            return option.Count > 0
                ? $"{title} ({option.Count})"
                : title;
        }

        private void UpdateButtonStates()
        {
            var activePane = LocalActivePane;
            // Gate on the SAME target ShowCreateMenu and Cmd+N resolve, so the
            // button, the create menu, and the hotkey always agree on where a
            // create would land (F-012). A single selected-but-unopened folder
            // wins over the browsed folder; see ResolveCreateTargetFolderPath.
            var targetFolder = activePane.ResolveCreateTargetFolderPath(_viewModel.IsFolderAssetEntry);
            var canWriteTargetFolder = _commandHandler != null
                && IsBrowserMode()
                && _commandHandler.CanWrite(AssetPath.From(targetFolder));
            _topBar?.SetPlusVisible(IsBrowserMode());
            _topBar?.SetCreateEnabled(canWriteTargetFolder);
            _topBar?.SetToolbarOverflowEnabled(true);
        }

        private bool IsBrowserMode()
            => LocalActivePane.ActiveMode == AssetsPaneMode.Browser;

        private void OnCreatedAssetLanded(
            ZepPaneState pane,
            string assetPath)
        {
            if (!_viewModel.IsLivePane(pane) || string.IsNullOrWhiteSpace(assetPath)) return;

            // WHY: a domain reload (e.g. after creating a script) destroys
            // the in-memory viewmodel and selection before the reveal below can
            // run. Persist the path so CreateGUI restores folder+selection on the
            // next window build. For every no-reload create (materials, scenes,
            // most assets) the reveal runs this tick and erases the stash.
            if (ReferenceEquals(pane, _viewModel.PrimaryPane))
            {
                SessionState.SetString(PendingCreateSelectionSessionKey, assetPath);
            }

            // These imports rebuild the editor domain. Let CreateGUI consume the
            // SessionState handoff after that reload; the ordinary delay callback can
            // run first, erase the handoff, and then be destroyed by the reload.
            var extension = AssetPathFormat.GetExtension(assetPath);
            if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".asmref", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                // The window can close between the create and this editor turn:
                // the pane may still be live in the view model while this shell
                // and its views are gone, so revealing through it would drive a
                // dead tree (and refocus a destroyed window).
                if (panel == null || !_viewModel.IsLivePane(pane))
                {
                    return;
                }

                var createdAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (createdAsset == null)
                {
                    return;
                }

                // Navigate the browser to the asset's folder, select it in our
                // list, and scroll it into view — the same reveal Inspector pings
                // use — so the new asset is visibly pinged inside the panel, not
                // just set as Unity's global selection.
                _commandHandler?.RevealAndFrameAsset(pane, assetPath);

                // Mirror into Unity's global selection so the Inspector shows the
                // new asset (matching Unity's own create UX). The selection-change
                // round-trip sees the path already selected above and no-ops.
                if (Selection.activeObject != createdAsset)
                {
                    _commandHandler?.ExpectPanelSelectionChange(pane, new[] { assetPath });
                    Selection.activeObject = createdAsset;
                }

                // Some create paths (the ExecuteMenuItem fallback) briefly hand
                // focus to Unity's Project window; make sure our panel ends up in
                // front with the freshly pinged asset.
                RestoreOwningWindowFocus();

                // The reveal succeeded this session; drop the reload stash so a
                // later panel open doesn't re-navigate to it.
                SessionState.EraseString(PendingCreateSelectionSessionKey);
            };
        }

        // Brings this shell's own Zep window to the front. Used after a
        // create so Unity's default Project window — which its create machinery
        // focuses to start a rename — doesn't stay in front of ours. Matches by
        // panel identity so multi-window setups refocus the window that owns THIS
        // shell, not an arbitrary one.
        private void RestoreOwningWindowFocus()
        {
            var owner = ResolveOwningWindow();
            owner?.Focus();
        }

        private ZepWindow ResolveOwningWindow()
        {
            return ZepWindow.FindByPanel(panel);
        }

        // Resolve the folder row/tile under a panel-space position, for the macOS
        // OnGUI external-drop fallback: Finder cross-process drags leave
        // DragAndDrop.paths empty, so the folder-target-aware UITK DragPerform
        // handlers never fire and the drop falls through to the window's OnGUI. This
        // lets that path import into the subfolder actually under the cursor instead
        // of always the current folder. Returns null when the cursor isn't over a
        // folder (an asset-grid folder tile/row, or a sidebar folder row).
        internal string ResolveExternalDropFolderTarget(Vector2 panelPosition)
        {
            var picked = panel?.Pick(panelPosition);
            if (picked == null)
            {
                return null;
            }

            var entry = AssetEntryRenderer.FindAssetEntryInAncestors(picked, null);
            if (entry != null && IndexedPathQueries.IsFolder(_commandHandler, entry.Path))
            {
                return entry.Path;
            }

            if (_sidebar != null && _sidebar.TryGetFolderPathAtTarget(picked, out var sidebarFolder))
            {
                return sidebarFolder;
            }

            if (_splitWorkspace != null
                && _splitWorkspace.TryGetPaneAtTarget(picked, out var receivingPane))
            {
                return receivingPane.CurrentFolderPath.Value;
            }

            return null;
        }

        private void OnSearchTextChanged(string searchText)
        {
            if (_isRefreshing)
            {
                return;
            }

            _searchOverlay.SetSearchText(searchText);
            RefreshSearchResults();
        }

        private void OnSearchResultSelected(string path, bool isFolder)
        {
            _commandHandler?.RevealAndFrameAsset(path);
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (Mathf.Approximately(evt.newRect.width, evt.oldRect.width))
            {
                return;
            }

            ApplyResponsiveLayout(evt.newRect.width);
        }

        internal void ApplyResponsiveLayout(float width)
        {
            if (width <= 0f || float.IsNaN(width) || float.IsInfinity(width))
            {
                return;
            }

            UpdateToolbarOverflowState();
            UpdateButtonStates();
            _sidebar?.ApplyResponsiveWidth(
                ResolveContentAreaWidth(width),
                MinAssetPaneWidth,
                MinAssetPaneWidthNarrow);
            _splitWorkspace?.ApplyResponsiveWidth(ResolveAssetPaneWidth(width));
            ApplyResponsiveItemHeights();
        }

        private void ShowCreateMenu()
        {
            if (_topBar == null || _contextMenu == null)
            {
                return;
            }

            // Defensive: the + button is hidden outside Browser mode, but if
            // a hotkey or test path reaches this method we still bail rather
            // than offer Create entries that target a non-creation view.
            if (!IsBrowserMode())
            {
                return;
            }

            // Route through the same selection-aware resolver Cmd+N and the
            // right-click "New X" menu use, so all three create in the SAME place
            // for identical state: a single selected-but-unopened folder is the
            // target, otherwise the browsed folder. Passing CurrentFolderPath here
            // used to defeat the resolver and make + diverge from Cmd+N.
            var pane = LocalActivePane;
            var targetFolder = pane.ResolveCreateTargetFolderPath(_viewModel.IsFolderAssetEntry);
            _contextMenu.ShowForCreateButton(
                pane,
                targetFolder,
                _topBar.GetPlusButtonWorldBound(),
                _commandHandler);
        }

        private void UpdateToolbarOverflowState()
        {
            // The toolbar stays mounted beneath overlays; gear + plus track browser mode.
            _topBar?.SetPlusVisible(IsBrowserMode());
            _topBar?.SetToolbarOverflowVisible(true);
        }

        internal void ApplyResponsiveItemHeights()
        {
            _splitWorkspace?.ApplyResponsiveItemHeights();
        }

        private void OnPaneSplitterDragStarted()
        {
            if (_sidebar == null)
            {
                return;
            }

            _folderPaneWidthAtDragStart = _sidebar.CaptureWidthForDrag();
        }

        private void OnPaneSplitterDragMoved(float deltaX)
        {
            if (_sidebar == null)
            {
                return;
            }

            _sidebar.SetWidth(
                _folderPaneWidthAtDragStart + deltaX,
                ResolveContentAreaWidth(-1f),
                MinAssetPaneWidth,
                MinAssetPaneWidthNarrow);
            _splitWorkspace?.ApplyResponsiveWidth(ResolveAssetPaneWidth());
            ApplyResponsiveItemHeights();
        }

        private void OnPaneSplitterDragEnded()
        {
            if (_sidebar != null && _sidebar.HasCustomWidth)
            {
                ZepThemePreferences.SetFolderPaneWidth(_sidebar.ResolvedPaneWidth);
            }
        }

        private float ResolveContentAreaWidth(float fallbackWidth)
        {
            if (_contentArea != null && _contentArea.resolvedStyle.width > 0f)
            {
                return _contentArea.resolvedStyle.width;
            }

            return fallbackWidth;
        }

        private float ResolveAssetPaneWidth(float fallbackWidth = -1f)
        {
            if (!_bypassAssetPaneWidthCache && _assetPane != null && _assetPane.resolvedStyle.width > 0f)
            {
                return _assetPane.resolvedStyle.width;
            }

            var contentWidth = ResolveContentAreaWidth(fallbackWidth);
            if (contentWidth <= 0f)
            {
                return -1f;
            }

            var folderPaneWidth = (_sidebar != null && _sidebarVisible) ? _sidebar.ResolvedPaneWidth : 0f;
            var splitterWidth = (_paneSplitter != null && _sidebarVisible)
                ? ZepPaneSplitterWidget.Width
                : 0f;
            return Mathf.Max(0f, contentWidth - folderPaneWidth - splitterWidth);
        }

        private void OnAssetPaneContextMenuRequested(
            ZepPaneState pane,
            Rect anchor,
            ZepAssetEntry assetEntry)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return;
            }

            if (assetEntry != null)
            {
                var selectedPaths = pane.BuildContextMenuSelection(assetEntry.Path);
                pane.SetSelectedAssetPaths(selectedPaths.Select(AssetPath.From));
                _assetSelectionAction?.Invoke(pane, pane.GetSelectedAssetEntries());
                ShowContextMenu(() => BuildContextMenuRequest(
                    pane,
                    selectedPaths,
                    ZepContextMenuTargetKind.Selection,
                    selectedAssets: pane.GetSelectedAssetEntries(),
                    anchor: anchor));
                return;
            }

            ShowBackgroundMenu(pane, anchor);
        }

        // The browsed folder is the target of a background right-click, as in
        // Unity's two-column Project window: Unity's validators and the panel's
        // own Reveal/Open/Delete/Rename bindings see the folder, and its tree
        // entry (when the tree knows it) carries the folder's tags into the
        // Tags submenu. Without a resolvable folder the menu keeps its
        // no-target shape.
        private void ShowBackgroundMenu(ZepPaneState pane, Rect? anchor)
        {
            var folderPath = pane.ResolveBackgroundContextFolderPath(
                path => IndexedPathQueries.IsFolder(_commandHandler, path));
            if (string.IsNullOrEmpty(folderPath))
            {
                ShowContextMenu(() => BuildContextMenuRequest(
                    pane,
                    Array.Empty<string>(),
                    ZepContextMenuTargetKind.Background,
                    anchor: anchor));
                return;
            }

            ShowContextMenu(() => BuildContextMenuRequest(
                pane,
                new[] { folderPath },
                ZepContextMenuTargetKind.Background,
                selectedFolder: _commandHandler?.GetVisibleFolderEntry(folderPath),
                anchor: anchor));
        }

        private void ShowContextMenu(Func<ZepContextMenuRequest> buildRequest)
        {
            ZepContextMenuRequest request;
            try
            {
                request = buildRequest();
            }
            catch (ProjectTagStateUnavailableException)
            {
                return;
            }

            _contextMenu.Show(request);
        }

        private ZepContextMenuRequest BuildContextMenuRequest(
            ZepPaneState pane,
            IReadOnlyList<string> contextPaths,
            ZepContextMenuTargetKind targetKind,
            IReadOnlyList<ZepAssetEntry> selectedAssets = null,
            ZepFolderEntry selectedFolder = null,
            Rect? anchor = null)
        {
            selectedAssets = selectedAssets ?? Array.Empty<ZepAssetEntry>();
            contextPaths = contextPaths ?? Array.Empty<string>();
            var canWriteSelection = ZepSelectionPredicates.CanModifyAllSelectedPaths(
                _commandHandler,
                contextPaths);
            var canOpenSingleAsset = contextPaths.Count == 1
                && (selectedAssets.Count == 1 || selectedFolder != null);
            IReadOnlyList<ZepTagFilterOption> assignmentTags = _viewModel.TagFilterOptions;
            if (_commandHandler != null)
            {
                // Sidebar options omit unused unpinned tags; assignment still offers every definition.
                var definitions = _commandHandler.GetSharedTags();
                var allTags = new ZepTagFilterOption[definitions.Count];
                for (var i = 0; i < definitions.Count; i++)
                {
                    var tag = definitions[i];
                    var count = _viewModel.TagFilterOptions.FirstOrDefault(option =>
                        string.Equals(option.TagId, tag.Id, StringComparison.OrdinalIgnoreCase)).Count;
                    allTags[i] = new ZepTagFilterOption(tag, count);
                }

                assignmentTags = allTags;
            }

            return new ZepContextMenuRequest(
                pane,
                _commandHandler,
                contextPaths,
                pane.ResolveCreateTargetFolderPath(_viewModel.IsFolderAssetEntry),
                assignmentTags,
                BuildContextTagMap(pane, contextPaths, selectedAssets, selectedFolder),
                selectedAssets,
                selectedFolder,
                isOpenFolderEnabled: canOpenSingleAsset,
                isDeleteEnabled: (canWriteSelection && contextPaths.Count > 0),
                isCreationAllowed: pane.ActiveMode == AssetsPaneMode.Browser,
                canModifyAllContextPaths: canWriteSelection,
                targetKind: targetKind,
                anchor: anchor);
        }

        private IReadOnlyDictionary<string, ICollection<string>> BuildContextTagMap(
            ZepPaneState pane,
            IReadOnlyList<string> contextPaths,
            IReadOnlyList<ZepAssetEntry> selectedAssets,
            ZepFolderEntry selectedFolder)
        {
            var source = pane.BuildAssignedTagMap();
            var result = new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < contextPaths.Count; i++)
            {
                var path = contextPaths[i];
                if (selectedFolder != null
                    && string.Equals(selectedFolder.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    var folderTags = _commandHandler?.GetTagsForPath(path)
                        ?? Array.Empty<ZepTag>();
                    result[path] = folderTags
                        .Where(static tag => !string.IsNullOrWhiteSpace(tag.Id))
                        .Select(static tag => tag.Id)
                        .ToArray();
                    continue;
                }

                if (source.TryGetValue(path, out var assigned))
                {
                    result[path] = assigned;
                    continue;
                }

                for (var a = 0; a < selectedAssets.Count; a++)
                {
                    var asset = selectedAssets[a];
                    if (asset == null
                        || !string.Equals(asset.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result[path] = asset.Tags
                        .Where(static tag => !string.IsNullOrWhiteSpace(tag.Id))
                        .Select(static tag => tag.Id)
                        .ToArray();
                    break;
                }
            }

            return result;
        }

        internal void OnKeyDown(KeyDownEvent evt)
        {
            RestoreLocalPaneFocus();
            if (_searchOverlay.IsVisible)
            {
                _shortcutController.HandleKeyDown(evt);
                if (evt.target is not VisualElement target || !_searchOverlay.Contains(target))
                    evt.StopPropagation();
                return;
            }
            if (ZepPerfLog.IsEnabled)
            {
                var focused = panel?.focusController?.focusedElement as VisualElement;
                ZepPerfLog.Log(
                    $"[Zep/kbd] key={evt.keyCode} char={(int)evt.character}"
                    + $" target={evt.target?.GetType().Name ?? "null"}"
                    + $" focused={(focused == null ? "null" : focused.GetType().Name)}"
                    + $" focusedDisplayed={(focused == null ? "-" : (focused.resolvedStyle.display != DisplayStyle.None).ToString())}"
                    + $" yieldToText={ZepShortcutController.IsTextInputTarget(evt.target)}"
                    + $" editingTextField={EditorGUIUtility.editingTextField}");
            }

            // Log-verified wedge: after an inline edit ends, key events can
            // keep targeting the hidden field's TextElement (stale focus leaf)
            // while focusedElement reports the ListView — no focus-out ever
            // fires, so the reclaim below can't see it. Re-anchor focus at the
            // shell so follow-up keys target a live element; THIS event still
            // gets normal handling because a hidden field no longer counts as
            // a text input.
            if (ZepShortcutController.IsStaleTextInputTarget(evt.target))
            {
                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log("[Zep/kbd] stale-text-target -> re-anchoring focus at shell");
                }

                Focus();
            }

            if (TryShowKeyboardContextMenu(evt))
            {
                return;
            }

            // The type-filter button is a focusable VisualElement whose ONLY
            // keyboard activation is its own KeyDown callback — no native
            // Button submit behavior. This handler runs first (trickle-down
            // from the root), so it must yield the activation keys or the
            // button never sees them.
            if (IsTypeFilterActivationKey(evt.keyCode)
                && _topBar != null
                && _topBar.IsTypeFilterButtonTarget(evt.target))
            {
                return;
            }

            // The divider owns resize arrows; this trickle-down dispatcher
            // otherwise moves the active grid selection before they reach it.
            if (!evt.actionKey && !evt.ctrlKey && !evt.altKey
                && (evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.RightArrow)
                && evt.target is VisualElement dividerTarget
                && (dividerTarget is ZepPaneSplitterWidget
                    || dividerTarget.GetFirstAncestorOfType<ZepPaneSplitterWidget>() != null))
            {
                return;
            }

            _shortcutController.HandleKeyDown(evt);
        }

        private bool TryShowKeyboardContextMenu(KeyDownEvent evt)
        {
            if (!ZepContextMenuInput.IsKeyboardTrigger(evt)
                || _contextMenu == null
                || _promptOverlay.IsVisible
                || _overlayController.IsAnyOverlayActive
                || ZepShortcutController.IsTextInputTarget(evt.target))
            {
                return false;
            }

            var focused = panel?.focusController?.focusedElement as VisualElement
                ?? evt.target as VisualElement
                ?? this;
            var anchor = ZepContextMenuInput.KeyboardAnchor(focused);

            if (_sidebar != null && _sidebar.TryShowFocusedFavoriteMenu(focused, anchor))
            {
                ConsumeKeyboardContextMenuEvent(evt);
                return true;
            }

            if (_sidebar != null && _sidebar.IsFolderTreeTarget(focused))
            {
                var folder = _sidebar.SelectedFolderEntry;
                if (folder == null
                    || string.IsNullOrWhiteSpace(folder.Path))
                {
                    return false;
                }

                var primaryPane = _viewModel.PrimaryPane;
                _viewModel.ActivatePane(primaryPane);
                ShowContextMenu(() => BuildContextMenuRequest(
                    primaryPane,
                    new[] { folder.Path },
                    ZepContextMenuTargetKind.SidebarFolder,
                    selectedFolder: folder,
                    anchor: anchor));
                ConsumeKeyboardContextMenuEvent(evt);
                return true;
            }

            // A context-menu key belongs to the focused surface. Do not turn
            // Menu/Shift-F10 on top-bar controls or tag controls into an
            // unrelated asset menu merely because an asset pane is active.
            if (_sidebar != null
                && (ReferenceEquals(focused, _sidebar) || _sidebar.Contains(focused)))
            {
                return false;
            }

            ZepPaneState pane;
            if (_splitWorkspace != null
                && _splitWorkspace.TryGetPaneAtTarget(focused, out var focusedPane))
            {
                pane = focusedPane;
            }
            else if (ReferenceEquals(focused, this))
            {
                pane = _viewModel.ActivePane;
            }
            else
            {
                return false;
            }

            if (!_viewModel.IsLivePane(pane))
            {
                return false;
            }

            var selectedAssets = pane.GetSelectedAssetEntries();
            var contextPaths = selectedAssets
                .Where(static asset => asset != null && !string.IsNullOrWhiteSpace(asset.Path))
                .Select(static asset => asset.Path)
                .ToArray();
            if (contextPaths.Length == 0)
            {
                ShowBackgroundMenu(pane, anchor);
                ConsumeKeyboardContextMenuEvent(evt);
                return true;
            }

            var targetKind = ZepContextMenuTargetKind.Selection;
            ShowContextMenu(() => BuildContextMenuRequest(
                pane,
                contextPaths,
                targetKind,
                selectedAssets: selectedAssets,
                anchor: anchor));
            ConsumeKeyboardContextMenuEvent(evt);
            return true;
        }

        private static void ConsumeKeyboardContextMenuEvent(KeyDownEvent evt)
        {
            evt.StopPropagation();
        }

        // One key list shared by the OnKeyDown yield above and the button's
        // own handler below, so the two can't drift apart.
        private static bool IsTypeFilterActivationKey(KeyCode keyCode)
        {
            return keyCode == KeyCode.Space
                || keyCode == KeyCode.Return
                || keyCode == KeyCode.KeypadEnter;
        }

        private void OnValidateCommand(ValidateCommandEvent evt)
        {
            RestoreLocalPaneFocus();
            _shortcutController.HandleValidateCommand(evt);
        }

        private void OnExecuteCommand(ExecuteCommandEvent evt)
        {
            RestoreLocalPaneFocus();
            _shortcutController.HandleExecuteCommand(evt);
        }

        // Keyboard lifeline: when the focused element goes away with no
        // successor (inline-edit teardown, overlay dismissal), focus collapses
        // to null and key events stop reaching the shell subtree entirely —
        // every shortcut dies until the user happens to click a focusable
        // control. Reclaim focus at the shell whenever in-panel focus dies
        // heirless; the deferred re-check yields to anything that grabbed
        // focus in the meantime.
        private void OnPanelFocusOut(FocusOutEvent evt)
        {
            if (ZepPerfLog.IsEnabled)
            {
                ZepPerfLog.Log(
                    $"[Zep/kbd] focus-out target={evt.target?.GetType().Name ?? "null"}"
                    + $" related={(evt.relatedTarget == null ? "null" : evt.relatedTarget.GetType().Name)}");
            }

            if (evt.relatedTarget != null) return;

            schedule.Execute(() =>
            {
                var focusController = panel?.focusController;
                if (focusController == null) return;

                // Reclaim on null focus AND on a wedged focus — an element
                // that is hidden or detached can no longer produce events,
                // but some teardown orders leave it registered as focused.
                var focused = focusController.focusedElement as VisualElement;
                var focusedIsGone = focused == null
                    || focused.panel == null
                    || focused.resolvedStyle.display == DisplayStyle.None;

                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log(
                        $"[Zep/kbd] reclaim-check focused={(focused == null ? "null" : focused.GetType().Name)} gone={focusedIsGone}");
                }

                if (focusedIsGone)
                {
                    Focus();
                }
            }).ExecuteLater(0);
        }

        private ZepFolderEntry GetSelectedFolderEntry()
        {
            return _sidebar?.SelectedFolderEntry;
        }

        private void ToggleSettingsDrawer()
        {
            if (_overlayController.IsSettingsPageActive)
            {
                _overlayController.Close();
            }
            else
            {
                _actions?.OpenSettingsPage();
            }
        }

        // Keep browser geometry intact beneath the settings drawer and theme modal.
        // Called on every overlay toggle and once at build to reveal the gear.
        private void ApplyOverlayPageState()
        {
            _topBar?.SetNavigationVisible(true);
            if (_overlayController.IsAnyOverlayActive) _searchOverlay?.Hide();
            RefreshSearchScopeRow();
            _topBar?.SetPlusVisible(IsBrowserMode());
            _topBar?.SetToolbarOverflowVisible(true);

            _overlayController.ApplySettingsScreenVisibility();
            _settingsBackdrop.style.display = _overlayController.IsSettingsPageActive
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        // Only the top bar fades in. It used to be a three-element sequence, but
        // `.zep-top-bar` is the only one of the three whose class
        // declares the opacity/translate transition the fade rides: the content
        // area declares none, so writing opacity there just blanked it for a
        // frame and snapped it back, and the status bar is built display:none —
        // its entrance was invisible, while the inline opacity it was left with
        // out-ranked `.zep-status-visible` for the window's lifetime
        // (styling-standards.md §4) and killed the toast fade.
        private void PlayMaterialEntranceAnimation()
        {
            var topBar = _topBar;
            if (topBar == null)
            {
                return;
            }

            topBar.style.opacity = 0f;
            topBar.style.translate = new Translate(0f, 12f);
            topBar.schedule.Execute(() =>
            {
                topBar.style.opacity = 1f;
                topBar.style.translate = new Translate(0f, 0f);
            }).ExecuteLater(30);
        }

        private void LoadStyleSheet()
        {
            LoadOnce(ZepUiClasses.PanelStyleSheetPath);
            LoadOnce(ZepUiClasses.DefaultsStyleSheetPath);

            void LoadOnce(string path)
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null && !styleSheets.Contains(sheet))
                    styleSheets.Add(sheet);
            }
        }

        bool IZepShortcutHost.IsPromptOverlayVisible =>
            _promptOverlay?.IsVisible ?? false;
        void IZepShortcutHost.HidePromptOverlay() => _promptOverlay?.Hide();
        bool IZepShortcutHost.IsOverlayActive => _overlayController.IsAnyOverlayActive || _searchOverlay.IsVisible;
        bool IZepShortcutHost.IsSearchOpen => _searchOverlay.IsVisible;
        void IZepShortcutHost.CloseOverlay() => CloseOverlayReanchoringFocus();

        // Escape closes the overlay but — unlike a gear/✕ click — never moves
        // focus. A focused settings/theme field would stay registered as focused
        // inside the collapsed drawer (it hides via width→0, display stays Flex, so
        // it still counts as a text-input target) and silently eat every panel key.
        // Blur it and re-anchor at the shell so the keyboard stays alive.
        private void CloseOverlayReanchoringFocus()
        {
            if (_searchOverlay.IsVisible)
            {
                _searchOverlay.Hide();
                return;
            }
            var focused = panel?.focusController?.focusedElement as VisualElement;
            var reanchor = focused != null && _overlayController.OwnsElement(focused);

            _overlayController.Close();

            if (reanchor)
            {
                focused.Blur();
                Focus();
            }
        }
        bool IZepShortcutHost.IsQuickLookOpen => ZepQuickLookWindow.IsOpen;
        IReadOnlyList<ZepAssetEntry> IZepShortcutHost.SelectedAssetEntries =>
            _viewModel.ActivePane.GetSelectedAssetEntries();
        ZepFolderEntry IZepShortcutHost.SelectedFolderEntry =>
            ReferenceEquals(_viewModel.ActivePane, _viewModel.PrimaryPane)
                ? GetSelectedFolderEntry()
                : null;
        ZepController IZepShortcutHost.CommandHandler => _commandHandler;
        IReadOnlyList<ZepTagFilterOption> IZepShortcutHost.TagFilterOptions => _viewModel.TagFilterOptions;
        void IZepShortcutHost.AnimateTagHighlight(string tagId) => _sidebar.AnimateTagHighlight(tagId);
        void IZepShortcutHost.NavigateBack() => _viewModel.ActivePane.NavigateBack();
        void IZepShortcutHost.NavigateForward() => _viewModel.ActivePane.NavigateForward();
        void IZepShortcutHost.PromptCreateFolder() => _actions?.PromptCreateFolder();
        void IZepShortcutHost.DuplicateSelected() => _actions?.DuplicateSelected();
        bool IZepShortcutHost.RedoRequested() => _actions?.RedoRequested() ?? false;
        bool IZepShortcutHost.UndoRequested() => _actions?.UndoRequested() ?? false;
        void IZepShortcutHost.PromptRenameSelected() => _actions?.PromptRenameSelected();
        // Enter follows the keyboard focus the way Delete does below: on a
        // favorite row it opens that favorite, everywhere else it keeps the
        // asset-selection semantics.
        void IZepShortcutHost.OpenSelected()
        {
            var focused = panel?.focusController?.focusedElement as VisualElement;
            if (_sidebar != null && focused != null && _sidebar.TryOpenFocusedFavorite(focused))
            {
                return;
            }

            _actions?.OpenSelected();
        }

        void IZepShortcutHost.DeleteSelected(IEventHandler target)
        {
            // Finder: Delete on a sidebar favorite does nothing but beep — a
            // bookmark is removed only through its context menu, and the
            // folder behind it is never the target.
            var focused = panel?.focusController?.focusedElement as VisualElement
                ?? target as VisualElement;
            if (_sidebar != null && focused != null && _sidebar.IsFavoriteRowTarget(focused))
            {
                EditorApplication.Beep();
                return;
            }

            if (TryDeleteSidebarFolderTarget(target))
            {
                return;
            }

            _actions?.DeleteSelected();
        }

        // Delete/⌘⌫ follows the keyboard focus the way the context-menu key
        // does: aimed at the sidebar folder tree, the chord targets the tree's
        // selected folder (Finder convention) with the exact routing of the
        // sidebar row's context-menu Delete. Everywhere else it keeps the
        // asset-selection semantics — deliberately without a selected-folder
        // fallback, so an empty pane selection stays a no-op.
        private bool TryDeleteSidebarFolderTarget(IEventHandler target)
        {
            var focused = panel?.focusController?.focusedElement as VisualElement
                ?? target as VisualElement;
            if (_actions == null || _sidebar == null || !_sidebar.IsFolderTreeTarget(focused))
            {
                return false;
            }

            var folder = _sidebar.SelectedFolderEntry;
            if (folder == null
                || string.IsNullOrWhiteSpace(folder.Path))
            {
                return false;
            }

            var primaryPane = _viewModel.PrimaryPane;
            _viewModel.ActivatePane(primaryPane);
            _actions.DeleteSidebarFolder(primaryPane, folder);
            return true;
        }
        void IZepShortcutHost.ToggleSidebar() => ToggleSidebar();
        private void RefreshSearchResults()
        {
            if (_searchOverlay.HasPendingQuery) return;
            var pane = _viewModel.PrimaryPane;
            var query = _searchOverlay.SearchText;
            var label = pane.SearchScopeOptions.FirstOrDefault(option => option.Scope == _searchOverlayScope).Label;
            if (pane.BreadcrumbRootPath.IsEmpty)
                label = _searchOverlayScope == ZepSearchScope.All ? "Entire project (Assets + Packages)"
                    : _searchOverlayScope == ZepSearchScope.CurrentFolder ? pane.CurrentFolderPath.Value
                    : _searchOverlayScope == ZepSearchScope.Packages ? "Packages" : "Assets";
            _searchOverlay.SetScopeContext(label ?? "Current scope",
                _searchOverlayScope != ZepSearchScope.All && pane.SearchScopeOptions.Any(option => option.Scope == ZepSearchScope.All),
                _viewModel.IsIndexScanInProgress, pane.TypeFilterOptions);
            if (!string.IsNullOrEmpty(_commandHandler?.IndexingError))
            {
                _searchOverlay.SetFailure(_commandHandler.IndexingError);
                return;
            }
            var results = _commandHandler?.GetSearchResults(pane, query, _searchOverlayScope,
                _searchOverlay.InheritedType, _searchOverlay.InheritedTag);
            if (results != null && results.Count > 0)
                _searchOverlay.SetResults(results);
            else
                _searchOverlay.SetSuggestions(_commandHandler?.GetSearchSuggestions(
                    pane, query, scope: _searchOverlayScope,
                    typeFilter: _searchOverlay.InheritedType, tagFilter: _searchOverlay.InheritedTag));
        }

        private void OpenSearch()
        {
            if (_overlayController.IsAnyOverlayActive || (_promptOverlay?.IsVisible ?? false)) return;
            if (_searchWindow != null)
            {
                _searchWindow.FocusSearch();
                return;
            }
            var pane = _viewModel.PrimaryPane;
            var contextKey = pane.CurrentFolderPath.Value + "\n" + pane.BreadcrumbRootPath.Value + "\n"
                + pane.SelectedTypeFilter + "\n" + pane.SelectedTagFilterId;
            if (_searchContextKey != contextKey)
            {
                _searchContextKey = contextKey;
                _searchOverlayScope = pane.SelectedSearchScope;
                var tag = _viewModel.TagFilterOptions.FirstOrDefault(option => option.TagId == pane.SelectedTagFilterId);
                _searchOverlay.SetContext(pane.SelectedTypeFilter, pane.SelectedTagFilterId, tag.Tag.Name, pane.TypeFilterOptions);
            }
            RefreshSearchResults();
            _searchWindow = ZepSearchWindow.Show(this, _searchOverlay, OnSearchWindowClosed);
            if (_searchWindow == null) return;
            _searchOverlay.Q("zep-search-card").Insert(2, _topBar.SearchScopeSurface);
            RefreshSearchScopeRow();
        }

        private void OnSearchWindowClosed()
        {
            _searchWindow = null;
            Add(_searchOverlay);
            _topBar.Add(_topBar.SearchScopeSurface);
            _shortcutController.ResetDoubleShift();
            RefreshSearchScopeRow();
        }

        void IZepShortcutHost.FocusSearch()
        {
            if (_searchOverlay.IsVisible) _searchOverlay.Hide();
            else OpenSearch();
        }
        void IZepShortcutHost.QuickLookToggle()
        {
            // Settings/theme-creator overlays own the panel (same guard as
            // SelectAllAssets) — but Space must still CLOSE a Quick Look that
            // was opened before entering the overlay.
            if (_overlayController.IsAnyOverlayActive && !ZepQuickLookWindow.IsOpen)
            {
                return;
            }

            // The window resolves the selection itself (grid entries, else the
            // sidebar folder) so the toggle and live-follow share one code path.
            // Folders are previewable now — no IsFolder filter here anymore.
            ZepQuickLookWindow.Toggle(this, EditorWindow.focusedWindow);
        }

        void IZepShortcutHost.SelectAllAssets()
        {
            if (_overlayController.IsAnyOverlayActive) return;
            GetMountedPaneView(_viewModel.ActivePane)?.SelectAllAssets();
        }

        void IZepShortcutHost.CancelInteractions()
        {
            GetMountedPaneView(_viewModel.ActivePane)?.CancelInteractions();
        }

        void IZepShortcutHost.NavigateToParentFolder()
        {
            if (_overlayController.IsAnyOverlayActive) return;

            _viewModel.ActivePane.TryNavigateToParentFolder();
        }

        bool IZepShortcutHost.MoveAssetSelection(AssetSelectionMove move, bool extend)
        {
            if (_overlayController.IsAnyOverlayActive || IsSidebarFocused()) return false;
            return GetMountedPaneView(_viewModel.ActivePane)?.MoveSelection(move, extend) ?? false;
        }

        bool IZepShortcutHost.TypeToSelectAsset(string prefix)
        {
            if (_overlayController.IsAnyOverlayActive || IsSidebarFocused()) return false;
            GetMountedPaneView(_viewModel.ActivePane)?.TypeToSelectAsset(prefix);
            // A no-match keystroke is still a type-ahead keystroke — consumed
            // either way; false only means "not the assets area's key".
            return true;
        }

        // The sidebar trees keep their native TreeView keyboard navigation;
        // the shell's TrickleDown handler must not steal their keys.
        private bool IsSidebarFocused()
        {
            var focused = panel?.focusController?.focusedElement as VisualElement;
            return focused != null && _sidebar != null && _sidebar.Contains(focused);
        }

        internal void ToggleSidebar()
        {
            _sidebarVisible = !_sidebarVisible;
            ZepThemePreferences.SetSidebarVisible(_sidebarVisible);

            if (_sidebar != null)
            {
                _sidebar.style.display = _sidebarVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_paneSplitter != null)
            {
                _paneSplitter.style.display = _sidebarVisible ? DisplayStyle.Flex : DisplayStyle.None;
            }

            _topBar?.SetSidebarActive(_sidebarVisible);

            _bypassAssetPaneWidthCache = true;
            ApplyResponsiveLayout(resolvedStyle.width);
            _bypassAssetPaneWidthCache = false;
        }

        private void ActivatePrimaryAndNavigate(string folderPath)
        {
            var primaryPane = _viewModel.PrimaryPane;
            if (!ReferenceEquals(_viewModel.ActivePane, primaryPane))
            {
                _viewModel.ActivatePane(primaryPane);
            }

            primaryPane.TrySetFolderSelection(folderPath);
        }

        private void ActivatePrimaryAndEnterMode(AssetsPaneMode mode)
        {
            var primaryPane = _viewModel.PrimaryPane;
            if (!ReferenceEquals(_viewModel.ActivePane, primaryPane))
            {
                _viewModel.ActivatePane(primaryPane);
            }

            primaryPane.EnterMode(mode);
        }

        internal void OpenFolderFromPane(ZepPaneState pane, string folderPath)
        {
            if (!_viewModel.IsLivePane(pane) || string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            // Every pane opens the folder in place — a tag pane browses it
            // under the tag (see ZepPaneState.TryEnterTagFolder).
            pane.TrySetFolderSelection(folderPath);
        }

        internal IReadOnlyList<float> SplitPaneWeights =>
            _splitWorkspace?.PaneWeights ?? Array.Empty<float>();

        internal bool TryGetPanePresentation(
            ZepPaneState pane,
            out AssetViewMode viewMode,
            out int gridItemSize)
        {
            if (_splitWorkspace != null)
            {
                return _splitWorkspace.TryGetPanePresentation(
                    pane,
                    out viewMode,
                    out gridItemSize);
            }

            viewMode = default;
            gridItemSize = 0;
            return false;
        }

        internal void ApplyRestoredSplitLayout(
            IReadOnlyDictionary<ZepPaneState, AssetViewMode> viewModes,
            IReadOnlyDictionary<ZepPaneState, int> gridItemSizes,
            IReadOnlyList<float> weights)
        {
            _splitWorkspace?.ApplyRestoredLayout(
                viewModes,
                gridItemSizes,
                weights);
        }

        private ZepPaneView GetMountedPaneView(ZepPaneState pane)
        {
            return _splitWorkspace?.GetPaneView(pane);
        }

        internal void RestoreLocalPaneFocus()
        {
            var localPane = LocalActivePane;
            if (!ReferenceEquals(_viewModel.ActivePane, localPane))
            {
                _viewModel.ActivatePane(localPane);
            }
        }

        private bool IsLocalPane(ZepPaneState pane)
        {
            if (ReferenceEquals(pane, _viewModel.PrimaryPane)) return true;
            for (var i = 0; i < _viewModel.AuxiliaryPanes.Count; i++)
            {
                if (ReferenceEquals(pane, _viewModel.AuxiliaryPanes[i])) return true;
            }

            return false;
        }

        internal void OpenSettings(bool tags)
        {
            if (tags) _overlayController.OpenSettingsToTags();
            else _overlayController.OpenSettings();
        }
    }
}
