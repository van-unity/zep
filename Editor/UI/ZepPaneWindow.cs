using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Settings;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.MaterialThumbnails;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // Unity owns this window's layout and lifetime; the shared panel state owns
    // its distinct browsing context. Closing another panel cannot retire it.
    public sealed class ZepPaneWindow : EditorWindow
    {
        [SerializeField] private ZepSplitPaneSnapshot _snapshot;
        [SerializeField] private string _displayName;
        [SerializeField] private string _pendingCreatedPath;

        private static readonly List<ZepPaneWindow> OpenWindows =
            new List<ZepPaneWindow>();

        private ZepState _state;
        private ZepController _controller;
        private ZepPaneState _pane;
        private ZepPaneView _view;
        private UnityZepIconRepository _icons;
        private ZepThemeRegistry _registry;
        private ZepThemeSwitcher _themeSwitcher;
        private ZepPromptOverlayWidget _prompt;
        private ZepToast _toast;
        private ZepStatusBarWidget _status;
        private ContextMenuActions _actions;
        private ContextMenuWidget _contextMenu;
        private readonly IDragSourcePathResolver _dragSourceResolver = new MacOSDragSourcePathResolver();
        private bool _loggedUnusableExternalDrag;
        private bool _restoring;
        private bool _closeQueued;
        private string _typeAhead = string.Empty;
        private double _lastTypeAhead = double.NegativeInfinity;

        internal ZepPaneState Pane => _pane;
        internal ZepState ViewModel => _state;
        internal ZepController Controller => _controller;
        internal UnityZepIconRepository IconRepository => _icons;
        internal SubAssetDescriptor ActiveSubAssetSelection => _view?.CommittedSubAsset;
        internal ZepThemeSwitcher ThemeSwitcher => _themeSwitcher;
        internal ZepThemeRegistry ThemeRegistry => _registry;
        internal event Action<string> ThemeApplied;

        internal static ZepPaneWindow Create(
            ZepPaneSource source,
            string displayName)
        {
            if (!source.IsAuxiliary)
                throw new ArgumentException("A docked pane requires a folder or tag.", nameof(source));

            var window = CreateInstance<ZepPaneWindow>();
            var viewMode = Enum.TryParse(
                EditorPrefs.GetString(ZepThemePreferences.AssetViewModePreferenceKey),
                true,
                out AssetViewMode stored) ? stored : AssetViewMode.List;
            window._snapshot = ZepWindow.CapturePaneSnapshot(
                new ZepPaneState(source), viewMode,
                ZepThemePreferences.GetAssetGridItemSize(), AssetDatabase.AssetPathToGUID);
            window._displayName = displayName;
            window.titleContent = new GUIContent(displayName);
            window.minSize = new Vector2(220f, 180f);
            return window;
        }

        internal static ZepPaneWindow FindByPanel(IPanel panel)
        {
            return panel == null ? null : OpenWindows.FirstOrDefault(
                window => window != null && window.rootVisualElement.panel == panel);
        }

        public void CreateGUI()
        {
            SaveSnapshot();
            TeardownView();
            _closeQueued = false;
            EditorApplication.delayCall -= CloseInvalidPane;
            _controller = ZepControllerHost.Shared.Core;
            _state = ZepControllerHost.SharedViewModel;
            minSize = new Vector2(220f, 180f);
            wantsMouseMove = true;
            wantsMouseEnterLeaveWindow = true;
            titleContent = new GUIContent(_displayName ?? ZepBootstrap.WindowTitle);
            if (!OpenWindows.Contains(this)) OpenWindows.Add(this);

            _controller.PaneSourceReconciliationReady -= RestorePane;
            _controller.PaneSourceReconciliationReady += RestorePane;
            _state.PaneChanged -= OnPaneChanged;
            _state.PaneChanged += OnPaneChanged;
            _controller.BeginPanelUse();
            RestorePane();
        }

        private void OnExternalDrag(EventBase evt)
        {
            // Unity's collection controls consume empty-path Finder drags as
            // rejected before bubble callbacks run. Recover only that payload
            // at the root; path-bearing drops keep the shared pane's handlers.
            var nativeEvent = evt.imguiEvent;
            if (nativeEvent?.type != EventType.DragExited
                && DragAndDrop.paths != null && DragAndDrop.paths.Length > 0) return;
            ZepWindow.HandleExternalDrag(nativeEvent, _dragSourceResolver, _controller,
                ResolveExternalDropDestinationFolder, ref _loggedUnusableExternalDrag);
            if (nativeEvent?.type == EventType.Used) evt.StopPropagation();
        }

        private string ResolveExternalDropDestinationFolder(Vector2 mousePosition)
        {
            if (_state == null || !_state.IsLivePane(_pane)) return string.Empty;

            var picked = rootVisualElement.panel?.Pick(mousePosition);
            var entry = AssetEntryRenderer.FindAssetEntryInAncestors(picked, _view);
            if (entry != null && IndexedPathQueries.IsFolder(_controller, entry.Path))
                return entry.Path;

            // A tag result set has no background folder. Only an opened folder
            // can receive an import; never redirect a tag-root drop into Assets.
            var folder = _pane.CurrentFolderPath.Value;
            return _pane.ActiveMode == AssetsPaneMode.Browser && AssetDatabase.IsValidFolder(folder)
                ? folder : string.Empty;
        }

        private void RestorePane()
        {
            if (_restoring || _view != null || _controller == null || !_controller.IsPaneSourceReconciliationReady)
                return;

            _controller.PaneSourceReconciliationReady -= RestorePane;
            _restoring = true;
            try
            {
                if (!ZepWindow.TryResolveSnapshotPane(
                        _snapshot, AssetDatabase.GUIDToAssetPath,
                        out var source, out var tagRoot, out var currentFolder,
                        out var viewMode, out var gridSize)
                    || !_controller.TryReconcilePaneSource(
                        source, tagRoot, currentFolder,
                        out source, out tagRoot, out currentFolder))
                {
                    QueueClose();
                    return;
                }

                if (!_state.IsLivePane(_pane))
                {
                    _pane = _state.OpenWindowPane(source);
                    if (source.Kind == ZepPaneSourceKind.Folder)
                        _pane.TrySetFolderSelection(currentFolder.Value);
                    else if (!tagRoot.IsEmpty)
                        _pane.TryEnterTagFolder(tagRoot, currentFolder);
                    ZepWindow.RestoreHistory(
                        _pane, _snapshot.History, _snapshot.LastKnownFolderPath);
                }

                BuildView(viewMode, gridSize);
                RevealCreatedAsset();
            }
            finally
            {
                _restoring = false;
            }
            SaveSnapshot();
        }

        private void BuildView(AssetViewMode viewMode, int gridSize)
        {
            var root = rootVisualElement;
            root.Clear();
            root.AddToClassList(ZepUiClasses.Root);
            root.EnableInClassList(ZepUiClasses.RootNoAddressables,
                !_controller.IsAddressablesInstalled);
            root.focusable = true;
            // Keep Unity's editor sheet: it supplies fonts and built-in control
            // geometry. Add our three sheets without replacing that foundation.
            foreach (var path in new[] { ZepUiClasses.PanelStyleSheetPath,
                         ZepUiClasses.DefaultsStyleSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            }
            _icons = new UnityZepIconRepository(
                _controller.GetFirstAssetPathForType, _controller.IsFolderKnownEmpty);
            _registry = ZepThemeProviderRegistration.CreateForInitialPanelPaint();
            _themeSwitcher = new ZepThemeSwitcher(root, _registry, _icons,
                theme =>
                {
                    _view?.RefreshFromPane();
                    ThemeApplied?.Invoke(theme);
                });
            _themeSwitcher.SetTheme(_themeSwitcher.CurrentTheme, false);
            _registry.ThemesChanged += ApplyTheme;
            ZepThemePreferences.PersistedThemeChanged += QueueThemeRefresh;
            EditorApplication.delayCall += CompleteThemeDiscovery;

            _prompt = new ZepPromptOverlayWidget();
            _toast = new ZepToast();
            _status = new ZepStatusBarWidget();
            var drops = new AssetDropOperations(_controller, _state, HandleSelection,
                (name, submit) => _prompt.Show("Create Prefab Variant", "Variant name",
                    name, "Create Variant", submit), showError: ShowError);
            _view = new ZepPaneView(_state, _pane, _icons, _controller,
                HandleSelection, inheritedViewMode: viewMode,
                inheritedGridItemSize: gridSize, dropOps: drops);
            var creator = new ZepCreateOrchestrator(_state, _controller,
                pane => ReferenceEquals(pane, _pane) ? _view?.BrowserFeature : null,
                OnCreatedAssetLanded, Focus, ShowError);
            _actions = new ContextMenuActions(_controller, creator, _prompt, _state,
                null, null, (pane, path) => pane.TrySetFolderSelection(path), ShowError,
                path => ZepFavoritesSettings.instance.Store.Contains(path), ToggleFavorite);
            _contextMenu = new ContextMenuWidget(new UnityAssetsContextMenuProvider(), _actions, root);
            _view.OpenRequested += _actions.OpenSelected;
            _view.ContextMenuRequested += ShowContextMenu;
            _view.PresentationChanged += OnPresentationChanged;
            root.Add(_view);
            root.Add(_status);
            root.Add(_prompt);
            root.Add(_toast);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<ValidateCommandEvent>(OnValidateCommand, TrickleDown.TrickleDown);
            root.RegisterCallback<ExecuteCommandEvent>(OnExecuteCommand, TrickleDown.TrickleDown);
            root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            root.RegisterCallback<DragUpdatedEvent>(OnExternalDrag, TrickleDown.TrickleDown);
            root.RegisterCallback<DragPerformEvent>(OnExternalDrag, TrickleDown.TrickleDown);
            root.RegisterCallback<DragExitedEvent>(OnExternalDrag, TrickleDown.TrickleDown);
            _state.GlobalChanged += RefreshChrome;
            _icons.PreviewsReady += OnPreviewsReady;
            MaterialThumbnailProvider.ThumbnailsReady += OnMaterialThumbnailsReady;
            _controller.AssetCatalogChanged += OnAssetCatalogChanged;
            _controller.AssetFrameRequested += OnAssetFrameRequested;
            RefreshChrome();
            _view.ApplyResponsiveWidth(position.width);
            // A docked or floated window gets native focus but no focused
            // element, and key events then never reach the root handlers.
            root.schedule.Execute(() =>
            {
                if (root.panel?.focusController?.focusedElement == null) root.Focus();
            }).ExecuteLater(0);
        }

        private void OnPaneChanged(ZepPaneChange change)
        {
            if (_restoring || _pane == null) return;
            if (ReferenceEquals(change.OldPane, _pane) && !_state.IsLivePane(_pane))
            {
                QueueClose();
                return;
            }
            if (ReferenceEquals(change.NewPane, _pane))
            {
                RefreshChrome();
                SaveSnapshot();
            }
        }

        private void RefreshChrome()
        {
            if (_pane == null) return;
            // Definitions, not the sidebar's filter options: those omit a
            // non-favorite tag with no assets, whose id is a stale slug.
            _displayName = _pane.Source.Kind == ZepPaneSourceKind.Folder
                ? AssetDropOperations.GetPathName(_pane.Source.RootFolderPath.Value)
                : _controller.GetSharedTags().FirstOrDefault(tag =>
                    string.Equals(tag.Id, _pane.Source.TagId, StringComparison.OrdinalIgnoreCase))
                    .Name ?? _pane.Source.TagId;
            titleContent = new GUIContent(_displayName);
            _view?.RefreshSplitNavigation();
            _status?.UpdateStatus(_state.StatusMessage);
        }

        private void OnPresentationChanged(ZepPaneState pane) => SaveSnapshot();

        private void SaveSnapshot()
        {
            if (_restoring || _view == null || _state == null || !_state.IsLivePane(_pane)) return;
            _snapshot = ZepWindow.CapturePaneSnapshot(
                _pane, _view.ViewMode, _view.GridItemSize, AssetDatabase.AssetPathToGUID);
            EditorUtility.SetDirty(this);
        }

        private void HandleSelection(ZepPaneState pane, IReadOnlyList<ZepAssetEntry> assets)
        {
            ZepWindow.HandleAssetSelected(_controller, pane, assets);
        }

        private void ShowContextMenu(ZepPaneState pane, Rect anchor, ZepAssetEntry entry)
        {
            if (!_state.IsLivePane(pane)) return;
            IReadOnlyList<string> paths;
            ZepFolderEntry folder = null;
            IReadOnlyList<ZepAssetEntry> assets = Array.Empty<ZepAssetEntry>();
            if (entry != null)
            {
                paths = pane.BuildContextMenuSelection(entry.Path);
                pane.SetSelectedAssetPaths(paths.Select(AssetPath.From));
                assets = pane.GetSelectedAssetEntries();
                HandleSelection(pane, assets);
            }
            else
            {
                var path = pane.ResolveBackgroundContextFolderPath(
                    candidate => IndexedPathQueries.IsFolder(_controller, candidate));
                paths = string.IsNullOrEmpty(path) ? Array.Empty<string>() : new[] { path };
                folder = _controller.GetVisibleFolderEntry(path);
            }
            try
            {
                var canWrite = ZepSelectionPredicates.CanModifyAllSelectedPaths(_controller, paths);
                var tags = new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                    tags[path] = _controller.GetTagsForPath(path).Select(tag => tag.Id).ToArray();
                _contextMenu.Show(new ZepContextMenuRequest(
                    pane, _controller, paths,
                    pane.ResolveCreateTargetFolderPath(_state.IsFolderAssetEntry),
                    _state.TagFilterOptions, tags, assets, folder,
                    isOpenFolderEnabled: paths.Count == 1,
                    isDeleteEnabled: canWrite && paths.Count > 0,
                    isCreationAllowed: pane.ActiveMode == AssetsPaneMode.Browser,
                    canModifyAllContextPaths: canWrite,
                    targetKind: entry == null ? ZepContextMenuTargetKind.Background
                        : ZepContextMenuTargetKind.Selection,
                    anchor: anchor));
            }
            catch (ProjectTagStateUnavailableException)
            {
                ShowError(_controller.StatusMessage);
            }
        }

        private void OnCreatedAssetLanded(ZepPaneState pane, string path)
        {
            if (!ReferenceEquals(pane, _pane)) return;
            _pendingCreatedPath = path;
            SaveSnapshot();
            var extension = AssetPathFormat.GetExtension(path);
            if (extension != ".cs" && extension != ".asmdef" && extension != ".asmref")
                EditorApplication.delayCall += RevealCreatedAsset;
        }

        private void RevealCreatedAsset()
        {
            if (this == null || _view == null || string.IsNullOrEmpty(_pendingCreatedPath)
                || !_state.IsLivePane(_pane)) return;
            var asset = AssetDatabase.LoadMainAssetAtPath(_pendingCreatedPath);
            if (asset == null) return;
            if (_controller.RevealAndFrameAsset(_pane, _pendingCreatedPath))
            {
                HandleSelection(_pane, _pane.GetSelectedAssetEntries());
                _pendingCreatedPath = null;
            }
        }

        private bool CanHandleCommand(IEventHandler target) =>
            _view != null && _state.IsLivePane(_pane) && !_prompt.IsVisible
            && !ZepShortcutController.IsTextInputTarget(target);

        private static bool IsPaneCommand(string command) => command == "SelectAll"
            || command == "Duplicate" || command == "Delete" || command == "SoftDelete";

        private void OnValidateCommand(ValidateCommandEvent evt)
        {
            if (CanHandleCommand(evt.target) && IsPaneCommand(evt.commandName)) evt.StopPropagation();
        }

        private void OnExecuteCommand(ExecuteCommandEvent evt)
        {
            if (!CanHandleCommand(evt.target) || !IsPaneCommand(evt.commandName)) return;
            ActivateOwnPane();
            if (evt.commandName == "SelectAll") _view.SelectAllAssets();
            else if (evt.commandName == "Duplicate") _actions.DuplicateSelected(_pane);
            else _actions.DeleteSelected(_pane);
            evt.StopPropagation();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_prompt != null && _prompt.IsVisible && evt.keyCode == KeyCode.Escape)
            {
                _prompt.Hide();
                rootVisualElement.Focus();
                evt.StopPropagation();
                return;
            }
            if (!CanHandleCommand(evt.target)) return;
            ActivateOwnPane();
            var platform = Application.platform;
            var key = evt.keyCode;
            var handled = true;
            if (evt.actionKey && key == KeyCode.A) _view.SelectAllAssets();
            else if (evt.actionKey && key == KeyCode.D) _actions.DuplicateSelected(_pane);
            else if (evt.actionKey && key == KeyCode.N) _actions.PromptCreateFolderAt(_pane, null);
            else if (ZepShortcutController.IsRedoChord(platform, key, evt.actionKey, evt.shiftKey))
                handled = _actions.RedoRequested();
            else if (evt.actionKey && key == KeyCode.Z) handled = _actions.UndoRequested();
            else if (ZepShortcutController.IsHistoryBackChord(platform, key, evt.actionKey, evt.altKey))
                _pane.NavigateBack();
            else if (ZepShortcutController.IsHistoryForwardChord(platform, key, evt.actionKey, evt.altKey))
                _pane.NavigateForward();
            else if (ZepShortcutController.IsNavigateToParentChord(platform, key, evt.actionKey, evt.altKey))
                _pane.TryNavigateToParentFolder();
            else if (ZepShortcutController.IsOpenSelectionChord(platform, key, evt.actionKey))
                OpenSelection();
            else if (ZepShortcutController.IsDeleteShortcut(platform, key, evt.actionKey))
                _actions.DeleteSelected(_pane);
            else if (key == KeyCode.F2 || ((key == KeyCode.Return || key == KeyCode.KeypadEnter)
                         && ZepShortcutController.ReturnBeginsRename(platform)))
                _actions.PromptRenameSelected(_pane);
            else if (key == KeyCode.Return || key == KeyCode.KeypadEnter) OpenSelection();
            else if (key == KeyCode.Space && !evt.actionKey && !evt.ctrlKey && !evt.altKey
                     && !evt.shiftKey && (_typeAhead.Length == 0
                         || EditorApplication.timeSinceStartup - _lastTypeAhead > 1))
                ZepQuickLookWindow.Toggle(this);
            else if (key == KeyCode.Escape && ZepQuickLookWindow.IsOpen)
                ZepQuickLookWindow.Toggle(this);
            else if (key == KeyCode.Escape) _view.CancelInteractions();
            else if (ZepContextMenuInput.IsKeyboardTrigger(evt))
                ShowContextMenu(_pane, ZepContextMenuInput.KeyboardAnchor(
                    rootVisualElement.panel?.focusController?.focusedElement as VisualElement),
                    _pane.GetSelectedAssetEntries().FirstOrDefault());
            else if (!evt.actionKey && !evt.ctrlKey && !evt.altKey)
                handled = HandleSelectionKey(evt);
            else handled = false;
            if (handled) evt.StopPropagation();
        }

        private void OpenSelection()
        {
            if (_view.CommittedSubAsset != null)
            {
                var asset = SubAssetCatalog.Resolve(_view.CommittedSubAsset);
                if (asset != null) AssetDatabase.OpenAsset(asset);
            }
            else _actions.OpenSelected(_pane);
        }

        private bool HandleSelectionKey(KeyDownEvent evt)
        {
            AssetSelectionMove move;
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow: move = AssetSelectionMove.Up; break;
                case KeyCode.DownArrow: move = AssetSelectionMove.Down; break;
                case KeyCode.LeftArrow: move = AssetSelectionMove.Left; break;
                case KeyCode.RightArrow: move = AssetSelectionMove.Right; break;
                case KeyCode.Home: move = AssetSelectionMove.First; break;
                case KeyCode.End: move = AssetSelectionMove.Last; break;
                case KeyCode.PageUp: move = AssetSelectionMove.PageUp; break;
                case KeyCode.PageDown: move = AssetSelectionMove.PageDown; break;
                default:
                    if (evt.character == '\0' || char.IsControl(evt.character)) return false;
                    var now = EditorApplication.timeSinceStartup;
                    if (now - _lastTypeAhead > 1) _typeAhead = string.Empty;
                    if (evt.character == ' ' && _typeAhead.Length == 0) return false;
                    _typeAhead += evt.character;
                    _lastTypeAhead = now;
                    _view.TypeToSelectAsset(_typeAhead);
                    return true;
            }
            return _view.MoveSelection(move, evt.shiftKey);
        }

        private void OnFocus()
        {
            if (_state != null && _state.IsLivePane(_pane)) ActivateOwnPane();
        }

        // Focus and every inspected key re-assert this pane; announcing an
        // unchanged activation would republish every pane listener, including
        // a full refresh of the main window, once per keystroke.
        private void ActivateOwnPane()
        {
            if (!ReferenceEquals(_state.ActivePane, _pane)) _state.ActivatePane(_pane);
        }

        private void OnLostFocus()
        {
            _typeAhead = string.Empty;
        }

        private void OnGeometryChanged(GeometryChangedEvent evt) => _view?.ApplyResponsiveWidth(evt.newRect.width);
        private void ShowError(string message) => _toast?.Show(message, ZepToastKind.Error);
        private void OnPreviewsReady(IReadOnlyList<string> paths) => _view?.NotifyIconPreviewsReady(paths);
        private void OnMaterialThumbnailsReady() => _view?.NotifyIconPreviewsReady(null);
        private void OnAssetFrameRequested(ZepPaneState pane, string path)
        {
            if (ReferenceEquals(pane, _pane)) _view?.PingPath(path);
        }

        private void OnAssetCatalogChanged(AssetCatalogDelta delta)
        {
            if (delta == null) _icons.InvalidateAssetIconCache();
            else
            {
                foreach (var record in delta.RemovedRecords) _icons.InvalidateAssetIcon(record.AssetPath);
                foreach (var record in delta.UpsertedRecords) _icons.InvalidateAssetIcon(record.AssetPath);
            }
        }

        private void ToggleFavorite(string folderPath)
        {
            var store = ZepFavoritesSettings.instance.Store;
            if (!store.Remove(folderPath) && !store.Add(folderPath)) return;
            foreach (var window in ZepWindow.OpenWindows)
                window.rootVisualElement.Q<ZepShellView>()?.RefreshView();
        }

        private void CompleteThemeDiscovery()
        {
            if (this != null && _view != null)
                ZepThemeProviderRegistration.CompleteProjectAssetDiscovery(_registry);
        }

        private void ApplyTheme()
        {
            if (_themeSwitcher == null) return;
            var theme = ZepThemePreferences.GetTheme();
            if (!_themeSwitcher.IsAppliedThemeUpToDate(theme)) _themeSwitcher.SetTheme(theme, false);
        }

        // A save raises both the definition and the preference change; one
        // deferred refresh also keeps this out of the main window's apply chain.
        private void QueueThemeRefresh()
        {
            EditorApplication.delayCall -= RefreshThemes;
            EditorApplication.delayCall += RefreshThemes;
        }

        private void RefreshThemes()
        {
            if (this == null || _view == null) return;
            _registry.RefreshRegistry();
            ApplyTheme();
        }

        private void QueueClose()
        {
            if (_closeQueued) return;
            _closeQueued = true;
            EditorApplication.delayCall += CloseInvalidPane;
        }

        private void CloseInvalidPane()
        {
            if (this != null) Close();
        }

        private void TeardownView()
        {
            EditorApplication.delayCall -= CompleteThemeDiscovery;
            EditorApplication.delayCall -= RevealCreatedAsset;
            EditorApplication.delayCall -= RefreshThemes;
            ZepThemePreferences.PersistedThemeChanged -= QueueThemeRefresh;
            rootVisualElement.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<ValidateCommandEvent>(OnValidateCommand, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<ExecuteCommandEvent>(OnExecuteCommand, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            rootVisualElement.UnregisterCallback<DragUpdatedEvent>(OnExternalDrag, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<DragPerformEvent>(OnExternalDrag, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<DragExitedEvent>(OnExternalDrag, TrickleDown.TrickleDown);
            if (_state != null) _state.GlobalChanged -= RefreshChrome;
            if (_controller != null)
            {
                _controller.AssetCatalogChanged -= OnAssetCatalogChanged;
                _controller.AssetFrameRequested -= OnAssetFrameRequested;
            }
            MaterialThumbnailProvider.ThumbnailsReady -= OnMaterialThumbnailsReady;
            if (_registry != null) _registry.ThemesChanged -= ApplyTheme;
            if (_icons != null) _icons.PreviewsReady -= OnPreviewsReady;
            _view?.Teardown();
            _view = null;
            _themeSwitcher?.Teardown();
            _themeSwitcher = null;
            _icons?.Teardown();
        }

        private void OnDisable()
        {
            SaveSnapshot();
            if (_state != null) _state.PaneChanged -= OnPaneChanged;
            if (_controller != null) _controller.PaneSourceReconciliationReady -= RestorePane;
            TeardownView();
            _state?.CloseWindowPane(_pane);
            _pane = null;
            _state = null;
            _controller = null;
            OpenWindows.Remove(this);
        }

        private void OnDestroy()
        {
            EditorApplication.delayCall -= CloseInvalidPane;
            OpenWindows.Remove(this);
        }
    }
}
