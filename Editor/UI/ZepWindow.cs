using System;
using System.Collections.Generic;
using System.Text;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Theming;
using Zep.Core.FileOperations;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.Theming;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    [Serializable]
    internal sealed class ZepSplitSnapshot
    {
        internal const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public List<ZepSplitPaneSnapshot> AuxiliaryPanes =
            new List<ZepSplitPaneSnapshot>();
        public int ActiveAuxiliaryIndex = -1;
        public List<float> PaneWeights = new List<float>();
    }

    [Serializable]
    internal sealed class ZepSplitPaneSnapshot
    {
        public int SourceKind;
        public string FolderGuid = string.Empty;
        public string LastKnownFolderPath = string.Empty;
        public string CurrentFolderPath = string.Empty;
        public string TagId = string.Empty;
        public int ViewMode;
        public int GridItemSize = AssetsAreaConstants.DefaultAssetGridItemSize;
        // The pane's own Back/Forward history, so a domain reload — which
        // rebuilds every split pane from this snapshot — keeps Back working
        // inside each of them, as it does in the main view.
        public ZepHistorySnapshot History = new ZepHistorySnapshot();
    }

    // The primary pane's back/forward history, serialized with the window so a
    // domain reload — which rebuilds the pane from scratch — keeps Back and
    // Forward working on the folders that were browsed before it.
    [Serializable]
    internal sealed class ZepHistorySnapshot
    {
        public List<ZepHistoryEntrySnapshot> Entries =
            new List<ZepHistoryEntrySnapshot>();
        public int Index = -1;
    }

    [Serializable]
    internal sealed class ZepHistoryEntrySnapshot
    {
        public string FolderPath = string.Empty;
        public string TagId = string.Empty;
        public string BrowseRootPath = string.Empty;
    }

    public static class ZepBootstrap
    {
        public const string MenuPath = "Window/Zep";
        public const string WindowTitle = "Assets";

        [MenuItem(MenuPath)]
        public static void OpenWindow()
        {
            var window = ZepWindow.Open();
            window.Focus();
        }

        public static void BuildRoot(
            VisualElement root,
            ZepState viewModel = null,
            ZepController commandHandler = null,
            System.Action<ZepPaneState, System.Collections.Generic.IReadOnlyList<ZepAssetEntry>> assetSelectionAction = null,
            UnityZepIconRepository iconRepository = null,
            ZepThemeRegistry registry = null)
        {
            root.Clear();
            root.style.flexGrow = 1f;

            var ownsRegistry = registry == null;
            var __sw_theme = System.Diagnostics.Stopwatch.StartNew();
            var resolvedRegistry = registry
                ?? ZepThemeProviderRegistration.CreateForInitialPanelPaint();
            __sw_theme.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ZepThemeProviderRegistration.CreateForInitialPanelPaint ms={__sw_theme.Elapsed.TotalMilliseconds:F2}");

            // Internal ctor so BuildRoot can wire the theme-save orchestrator without
            // surfacing it on ShellView's public ctor (the interface is internal).
            var __sw_shell = System.Diagnostics.Stopwatch.StartNew();
            var shell = new ZepShellView(
                viewModel ?? ZepPreviewSeed.Build(),
                commandHandler,
                assetSelectionAction,
                contextMenuProvider: null,
                iconRepository: iconRepository,
                themeSaveOrchestrator: new ThemeSaveOrchestrator(resolvedRegistry),
                registry: resolvedRegistry,
                deferSecondaryUi: ownsRegistry);
            __sw_shell.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] new ZepShellView ms={__sw_shell.Elapsed.TotalMilliseconds:F2}");

            var __sw_attach = System.Diagnostics.Stopwatch.StartNew();
            root.Add(shell);
            __sw_attach.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] root.Add(shell) ms={__sw_attach.Elapsed.TotalMilliseconds:F2}");

            if (ownsRegistry)
            {
                EditorApplication.delayCall += () =>
                {
                    // A window closed before its first editor turn must not
                    // trigger project-wide theme discovery while no panel is
                    // observing the result.
                    if (shell.panel == null)
                    {
                        return;
                    }

                    ZepThemeProviderRegistration
                        .CompleteProjectAssetDiscovery(resolvedRegistry);
                };
            }
        }
    }

#if UNITY_6000_3_OR_NEWER
    // The base implements Unity's internal ping interface, so on 6.3+ the panel
    // window itself receives pings (see PingFramableEditorWindow).
    public sealed class ZepWindow
        : Zep.Editor.InternalBridge.PingFramableEditorWindow
#else
    public sealed class ZepWindow : EditorWindow
#endif
    {
        [SerializeField] private string _savedFolderPath = "Assets";
        [SerializeField] private ZepSplitSnapshot _splitSnapshot =
            new ZepSplitSnapshot();
        [SerializeField] private ZepHistorySnapshot _historySnapshot =
            new ZepHistorySnapshot();

        private ZepControllerHost _controllerHost;
        private ZepState _viewModel;
        private ZepShellView _shell;
        private bool _didAttemptSerializedRestore;
        private bool _isApplyingSplitRestore;
        private PendingSplitRestore _pendingSplitRestore;

        // Panels live in this editor session. The ping receiver reads it instead
        // of sweeping every loaded object per ping, and on 6.0–6.2 it is the
        // seam that arms the legacy receiver on the first open and stops its
        // poll after the last close. OnDisable removes the window and clears its
        // registration latch together, before either close or domain reload.
        // A list, not a count: the ping receiver, the shell's refocus, and the
        // context-menu host lookup used to Resources.FindObjectsOfTypeAll their
        // way to the open window (every loaded EditorWindow, per menu open);
        // the windows that registered here ARE that set.
        private static readonly List<ZepWindow> _openWindows =
            new List<ZepWindow>();

        // CreateGUI is this window's only "I am live" hook (EditorWindow gives it
        // no OnEnable) and it runs again after every domain reload, while
        // OnDestroy runs only on a real close — so the count is guarded per
        // instance. Unity restores this window's plain private fields across a
        // domain reload (measured on 6000.3.10f1), so the guard does NOT reset
        // with the static list it protects: OnDisable unregisters the window
        // so the next CreateGUI can register it again.
        private bool _countedAsOpen;

        internal static int OpenWindowCount => _openWindows.Count;

        internal static bool HasOpenWindow => _openWindows.Count > 0;

        // Snapshot order = registration order. Read-only view; the list itself
        // changes only through Register/Unregister.
        internal static IReadOnlyList<ZepWindow> OpenWindows => _openWindows;

#if UNITY_6000_3_OR_NEWER
        // Unity pings every framable window, so with two panels open each one is
        // called. The first registered panel answers for all of them: the reveal
        // runs on the shared controller, and running it twice would reveal twice.
        // An unregistered window (CreateGUI not run yet) has nowhere to reveal.
        internal bool AnswersPings => _openWindows.Count > 0 && _openWindows[0] == this;

        protected override void OnFrameObject(EntityId instanceId, bool ping)
        {
            if (AnswersPings)
            {
                ZepPingReceiver.HandleFrame(instanceId, ping);
            }
        }
#endif

        // The registered window whose root lives on `panel`, or null. Docking
        // and undocking re-parent a window's root, so this is answered live
        // rather than cached at registration.
        internal static ZepWindow FindByPanel(IPanel panel)
        {
            if (panel == null)
            {
                return null;
            }

            for (var i = 0; i < _openWindows.Count; i++)
            {
                var window = _openWindows[i];
                if (window != null
                    && window.rootVisualElement != null
                    && window.rootVisualElement.panel == panel)
                {
                    return window;
                }
            }

            return null;
        }

        private ZepController _controller => _controllerHost?.Core;

        // TODO: Add Windows/Linux drag source resolvers when those platforms
        // need pasteboard fallback. Today only macOS Finder leaves
        // DragAndDrop.paths empty for cross-process drags, so a single
        // resolver suffices.
        private readonly IDragSourcePathResolver _dragSourceResolver = new MacOSDragSourcePathResolver();
        // Once-per-drag latch for the unusable-payload diagnostic; cleared when
        // the drag ends or the payload turns out to be usable after all.
        private bool _loggedUnusableExternalDrag;

        public static ZepWindow Open()
        {
            var window = GetWindow<ZepWindow>();
            window.titleContent = new GUIContent(ZepBootstrap.WindowTitle);
            window.minSize = new Vector2(520f, 320f);
            return window;
        }

        internal static void OpenSettings(bool tags)
        {
            var window = Open();
            if (window._shell == null)
            {
                window.CreateGUI();
            }

            window._shell.OpenSettings(tags);
            window.Focus();
        }

        public void CreateGUI()
        {
            // PERF-DIAG: total panel-open cost from CreateGUI entry to UI root attach.
            var __sw_creategui = System.Diagnostics.Stopwatch.StartNew();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log("[Zep/perf] === panel open BEGIN ===");
            titleContent = new GUIContent(ZepBootstrap.WindowTitle);
            minSize = new Vector2(520f, 320f);
            wantsMouseEnterLeaveWindow = true;
            wantsMouseMove = true;

            // Pull the session-scoped singletons. First access in the editor session
            // builds the host + viewmodel and triggers the deferred hydration; every
            // subsequent panel open returns the already-hydrated instance and the
            // shell paints with real data on first frame. See
            // ZepControllerHost.Shared for the lifecycle contract.
            var __sw_ctorhost = System.Diagnostics.Stopwatch.StartNew();
            _controllerHost = ZepControllerHost.Shared;
            _viewModel = ZepControllerHost.SharedViewModel;
            __sw_ctorhost.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ZepControllerHost.Shared (cached={(_viewModel.Folders.Count > 0)}) ms={__sw_ctorhost.Elapsed.TotalMilliseconds:F2}");

            PrepareSerializedRestore();
            var primaryPane = _viewModel.PrimaryPane;

            // An asset created in this panel just before a domain reload (e.g. a
            // script that triggered a recompile) stashed its path here. Restore the
            // browser to its folder and select it BEFORE BuildRoot so the list paints
            // there on the first frame; we frame it into view after BuildRoot.
            var pendingPath = SessionState.GetString(
                ZepShellView.PendingCreateSelectionSessionKey,
                string.Empty);
            var hasPendingReveal = false;
            if (!string.IsNullOrEmpty(pendingPath))
            {
                SessionState.EraseString(
                    ZepShellView.PendingCreateSelectionSessionKey);

                var pendingAssetObject = AssetDatabase.LoadMainAssetAtPath(pendingPath);
                if (pendingAssetObject != null)
                {
                    var parentFolder = AssetPathFormat.GetParentFolder(pendingPath);
                    if (string.IsNullOrEmpty(parentFolder)) parentFolder = "Assets";
                    var folderPath = AssetPath.From(parentFolder);
                    primaryPane.TrySetCurrentFolderPath(folderPath);
                    primaryPane.TrySetSelectedFolderPath(folderPath);
                    primaryPane.SetSelectedAssetPaths(new[] { AssetPath.From(pendingPath) });
                    Selection.activeObject = pendingAssetObject;
                    hasPendingReveal = true;
                }
            }

            _viewModel.PaneChanged -= OnViewModelChanged;
            _viewModel.PaneChanged += OnViewModelChanged;

            // First real panel use: publish the bounded direct-folder view now,
            // then let the controller schedule global reconciliation after paint.
            _controller.BeginPanelUse();
            // Reopening keeps the session pane without another navigation event,
            // so this new window must also adopt its current restore snapshot.
            OnViewModelChanged(ZepPaneChange.Updated(
                primaryPane,
                ZepPaneUpdate.Navigation | ZepPaneUpdate.History));

            var iconRepository = new UnityZepIconRepository(
                _controller.GetFirstAssetPathForType,
                _controller.IsFolderKnownEmpty);

            var __sw_buildroot = System.Diagnostics.Stopwatch.StartNew();
            if (_shell != null)
            {
                _shell.SplitArrangementChanged -= OnSplitArrangementChanged;
            }
            _shell?.Teardown();
            ZepBootstrap.BuildRoot(
                rootVisualElement,
                _viewModel,
                _controller,
                HandleAssetSelected,
                iconRepository);
            _shell = rootVisualElement.Q<ZepShellView>();
            if (_shell != null)
            {
                _shell.SplitArrangementChanged += OnSplitArrangementChanged;
            }
            __sw_buildroot.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ZepBootstrap.BuildRoot ms={__sw_buildroot.Elapsed.TotalMilliseconds:F2}");

            // Now that the browser list exists, scroll the restored create-selection
            // into view so a post-reload asset is visibly pinged, not selected off
            // screen. The folder + selection were already applied above.
            if (hasPendingReveal)
            {
                _controller.RevealAndFrameAsset(pendingPath);
            }
            else
            {
                MirrorEditorSelectionIntoRestoredFolder();
            }

            SubscribeToPaneSourceReadiness();
            TryRestorePendingSplit();

            // Last, so a ping is answered only by a fully built panel.
            RegisterOpenWindow();

            __sw_creategui.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ZepWindow.CreateGUI TOTAL ms={__sw_creategui.Elapsed.TotalMilliseconds:F2}");
        }

        // On 6.0–6.2 the legacy ping receiver exists to serve open panels: its
        // poll restarts on the first open, and dropping to zero open panels is
        // what stops it. On 6.3+ the registered windows answer pings themselves.
        private void RegisterOpenWindow()
        {
            if (_countedAsOpen)
            {
                return;
            }

            _countedAsOpen = true;
            _openWindows.Add(this);
#if !UNITY_6000_3_OR_NEWER
            if (_openWindows.Count == 1)
            {
                ZepPingReceiver.OnFirstPanelWindowOpened();
            }
#endif
        }

        private void UnregisterOpenWindow()
        {
            if (!_countedAsOpen)
            {
                return;
            }

            _countedAsOpen = false;
            _openWindows.Remove(this);
            if (_openWindows.Count == 0)
            {
                ZepPingReceiver.OnLastPanelWindowClosed();
            }
        }

        private void OnGUI()
        {
#if !UNITY_6000_3_OR_NEWER
            ZepPingReceiver.EnsureReceiver();
#endif

            HandleExternalDrag(Event.current, _dragSourceResolver, _controller,
                ResolveExternalDropDestinationFolder, ref _loggedUnusableExternalDrag);
        }

        internal static void HandleExternalDrag(
            Event evt,
            IDragSourcePathResolver dragSourceResolver,
            ZepController controller,
            Func<Vector2, string> resolveDestination,
            ref bool loggedUnusableExternalDrag)
        {
            if (evt == null)
            {
                return;
            }

            // The drag ended (dropped, cancelled, or left the window): arm the
            // unusable-payload diagnostic again for the next one.
            if (evt.type == EventType.DragExited)
            {
                loggedUnusableExternalDrag = false;
                return;
            }

            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
            {
                return;
            }

            // Sidebar split drags intentionally publish no paths or object
            // references. Recognize the typed payload before the macOS empty-
            // object fallback so it cannot log, import, accept, or replace the
            // split host's Link/Rejected visual mode.
            if (ShouldIgnoreExternalDragFallback())
            {
                return;
            }

            var objectRefs = DragAndDrop.objectReferences;
            var isExternalDrag = objectRefs == null || objectRefs.Length == 0;
            if (!isExternalDrag)
            {
                return;
            }

            // Application.dataPath's parent, not the process working directory:
            // the UI Toolkit drop handler classifies against the same root, and
            // the two disagree whenever anything has changed the CWD.
            var projectRoot = ProjectPaths.GetProjectRootAbsolutePath()
                .Replace('\\', '/')
                .TrimEnd('/');
            var externalPaths = Zep.Core.FileOperations.ExternalAssetImporter.CollectExternalPaths(DragAndDrop.paths, projectRoot);

            if (externalPaths.Count == 0)
            {
                var originals = dragSourceResolver.TryResolveOriginalPaths();
                Zep.Core.FileOperations.ExternalAssetImporter.AppendExternalPaths(originals, projectRoot, externalPaths);
            }

            if (externalPaths.Count == 0)
            {
                // DragUpdated fires per mouse-move, so an unusable payload used to
                // fill the console with one identical line per frame of hover.
                // Once per drag session says the same thing.
                if (!loggedUnusableExternalDrag)
                {
                    loggedUnusableExternalDrag = true;
                    Debug.Log($"[Zep] External drag detected but no usable paths. DragAndDrop.paths=[{(DragAndDrop.paths != null ? string.Join(",", DragAndDrop.paths) : "null")}]");
                }

                return;
            }

            loggedUnusableExternalDrag = false;

            // macOS Finder drags leave DragAndDrop.paths empty, so the
            // folder-target-aware UITK DragPerform handlers never accept the drop
            // and it lands here. Hit-test the folder under the cursor so a drop
            // onto a subfolder imports THERE, not always the current folder.
            // Resolved on DragUpdated too, so a destination-less drop is refused
            // by the cursor instead of being accepted and then redirected.
            var destinationFolderPath = resolveDestination(evt.mousePosition);
            if (string.IsNullOrEmpty(destinationFolderPath))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                evt.Use();
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();

                // A dropped .unitypackage starts Unity's interactive Import Package
                // flow; only the non-package remainder is copied into the folder.
                var toCopy = Zep.Core.FileOperations.ExternalAssetImporter.ImportDroppedUnityPackages(externalPaths);
                if (toCopy.Count > 0)
                {
                    var result = Zep.Core.FileOperations.ExternalAssetImportResult.Empty;
                    if (controller != null)
                    {
                        controller.RunCatalogBatch(() =>
                        {
                            result = Zep.Core.FileOperations.ExternalAssetImporter.Import(toCopy, destinationFolderPath);
                            for (var i = 0; i < result.ImportedAssetPaths.Count; i++)
                            {
                                controller.AcknowledgeAssetPath(
                                    AssetPath.From(result.ImportedAssetPaths[i]));
                                if (!controller.RecordCreatedAsset(AssetPath.From(result.ImportedAssetPaths[i])))
                                {
                                    Debug.LogWarning("Zep imported '"
                                        + result.ImportedAssetPaths[i]
                                        + "', but Unity did not provide a stable identity for Undo.");
                                    break;
                                }
                            }
                        });
                    }
                    else
                    {
                        result = Zep.Core.FileOperations.ExternalAssetImporter.Import(toCopy, destinationFolderPath);
                    }
                    if (result.Failures.Count > 0)
                    {
                        LogImportFailures(result.Failures);
                    }
                }
            }

            evt.Use();
        }

        internal static bool ShouldIgnoreExternalDragFallback()
        {
            return ZepSplitDragAndDrop.TryRead(out _);
        }

        private string ResolveExternalDropDestinationFolder(Vector2 mousePosition)
        {
            var shell = rootVisualElement?.Q<ZepShellView>();
            var primaryFolder = _viewModel?.PrimaryPane.CurrentFolderPath ?? default;
            return ResolveExternalDropDestination(
                shell?.ResolveExternalDropFolderTarget(mousePosition),
                primaryFolder.Value);
        }

        // Destination folder for an OnGUI external drop: the folder under the cursor
        // (the macOS Finder fallback path), else the current folder, else Assets.
        internal static string ResolveExternalDropDestination(
            string folderTargetUnderCursor,
            string paneCurrentFolderPath)
        {

            if (!string.IsNullOrEmpty(folderTargetUnderCursor)
                && AssetDatabase.IsValidFolder(folderTargetUnderCursor))
            {
                return folderTargetUnderCursor;
            }

            var current = string.IsNullOrEmpty(paneCurrentFolderPath) ? "Assets" : paneCurrentFolderPath;
            return AssetDatabase.IsValidFolder(current) ? current : "Assets";
        }

        private static void LogImportFailures(IReadOnlyList<(string sourcePath, string reason)> failures)
        {
            var builder = new StringBuilder();
            builder.Append("[Zep] External import had ").Append(failures.Count).Append(" failure(s):");
            for (var i = 0; i < failures.Count; i++)
            {
                builder.Append("\n  - ").Append(failures[i].sourcePath).Append(": ").Append(failures[i].reason);
            }

            Debug.LogWarning(builder.ToString());
        }

        private void PrepareSerializedRestore()
        {
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] serialized-restore saved={_savedFolderPath} latch={_didAttemptSerializedRestore} pristine={IsPristineForSerializedRestore(_viewModel)} pane={_viewModel.PrimaryPane.CurrentFolderPath.Value} entryEmpty={_viewModel.PrimaryPane.CurrentEntry.IsEmpty} selected={_viewModel.PrimaryPane.SelectedAssetPaths.Count} search={_viewModel.PrimaryPane.SearchText?.Length ?? 0} aux={_viewModel.AuxiliaryPanes.Count}");
            if (_didAttemptSerializedRestore)
            {
                return;
            }

            _didAttemptSerializedRestore = true;
            if (!IsPristineForSerializedRestore(_viewModel))
            {
                return;
            }

            var primaryPane = _viewModel.PrimaryPane;
            if (!string.IsNullOrWhiteSpace(_savedFolderPath))
            {
                var savedFolder = AssetPath.From(_savedFolderPath);
                primaryPane.TrySetCurrentFolderPath(savedFolder);
                primaryPane.TrySetSelectedFolderPath(savedFolder);
            }

            RestoreHistory(primaryPane, _historySnapshot);

            if (!IsSupportedSplitSnapshot(_splitSnapshot))
            {
                _splitSnapshot = new ZepSplitSnapshot();
                return;
            }

            var pendingPanes = new List<PendingSplitPane>();
            var serializedPanes = _splitSnapshot.AuxiliaryPanes;
            if (serializedPanes != null)
            {
                for (var i = 0; i < serializedPanes.Count; i++)
                {
                    if (TryResolveSnapshotPane(
                        serializedPanes[i],
                        AssetDatabase.GUIDToAssetPath,
                        out var source,
                        out var tagBrowseRootPath,
                        out var currentFolderPath,
                        out var viewMode,
                        out var gridItemSize))
                    {
                        pendingPanes.Add(new PendingSplitPane(
                            i,
                            source,
                            tagBrowseRootPath,
                            currentFolderPath,
                            viewMode,
                            gridItemSize,
                            serializedPanes[i].History,
                            serializedPanes[i].LastKnownFolderPath));
                    }
                }
            }

            if (pendingPanes.Count == 0)
            {
                return;
            }

            _pendingSplitRestore = new PendingSplitRestore(
                pendingPanes,
                _splitSnapshot.ActiveAuxiliaryIndex,
                _splitSnapshot.PaneWeights);
        }

        internal static bool IsSupportedSplitSnapshot(
            ZepSplitSnapshot snapshot)
        {
            return snapshot != null
                && snapshot.Version == ZepSplitSnapshot.CurrentVersion;
        }

        internal static bool IsPristineForSerializedRestore(ZepState state)
        {
            if (state == null
                || state.AuxiliaryPanes.Count != 0)
            {
                return false;
            }

            var primary = state.PrimaryPane;
            var currentFolder = primary.CurrentFolderPath.Value;
            return (string.IsNullOrEmpty(currentFolder)
                    || string.Equals(currentFolder, "Assets", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrEmpty(primary.SearchText)
                && string.IsNullOrEmpty(primary.SelectedTypeFilter)
                && string.IsNullOrEmpty(primary.SelectedTagFilterId)
                && primary.SelectedAssetPaths.Count == 0
                && primary.CurrentEntry.IsEmpty;
        }

        private void SubscribeToPaneSourceReadiness()
        {
            if (_controller == null || _pendingSplitRestore == null)
            {
                return;
            }

            _controller.PaneSourceReconciliationReady -= OnPaneSourceReconciliationReady;
            _controller.PaneSourceReconciliationReady += OnPaneSourceReconciliationReady;
        }

        private void OnPaneSourceReconciliationReady()
        {
            TryRestorePendingSplit();
        }

        private void TryRestorePendingSplit()
        {
            if (_pendingSplitRestore == null
                || _controller == null
                || !_controller.IsPaneSourceReconciliationReady
                || _viewModel == null
                || _shell == null)
            {
                return;
            }

            _controller.PaneSourceReconciliationReady -=
                OnPaneSourceReconciliationReady;

            var pending = _pendingSplitRestore;
            var candidates = new List<ZepPaneState>();
            var restoredBySource =
                new Dictionary<ZepPaneSource, ZepPaneState>();
            var viewModes = new Dictionary<ZepPaneState, AssetViewMode>();
            var gridItemSizes = new Dictionary<ZepPaneState, int>();
            var restoredWeights = new List<float>();
            var hasCompleteWeights = pending.PaneWeights.Count > 0;
            if (hasCompleteWeights)
            {
                restoredWeights.Add(pending.PaneWeights[0]);
            }
            ZepPaneState activeAuxiliary = null;

            for (var i = 0; i < pending.Panes.Count; i++)
            {
                var serializedPane = pending.Panes[i];
                if (!_controller.TryReconcilePaneSource(
                        serializedPane.Source,
                        serializedPane.TagBrowseRootPath,
                        serializedPane.CurrentFolderPath,
                        out var reconciledSource,
                        out var reconciledTagBrowseRootPath,
                        out var reconciledCurrentFolderPath))
                {
                    continue;
                }

                if (restoredBySource.TryGetValue(
                    reconciledSource,
                    out var equivalentPane))
                {
                    if (serializedPane.SerializedIndex
                        == pending.ActiveAuxiliaryIndex)
                    {
                        activeAuxiliary = equivalentPane;
                    }

                    continue;
                }

                if (candidates.Count >= ZepState.MaxSplitViews)
                {
                    continue;
                }

                var pane = new ZepPaneState(reconciledSource);
                if (reconciledSource.Kind == ZepPaneSourceKind.Folder)
                {
                    pane.TrySetCurrentFolderPath(reconciledCurrentFolderPath);
                    pane.TrySetSelectedFolderPath(reconciledCurrentFolderPath);
                }
                else if (!reconciledTagBrowseRootPath.IsEmpty)
                {
                    pane.TryEnterTagFolder(
                        reconciledTagBrowseRootPath,
                        reconciledCurrentFolderPath);
                }

                RestoreHistory(
                    pane,
                    serializedPane.History,
                    serializedPane.LastKnownFolderPath);

                candidates.Add(pane);
                restoredBySource.Add(reconciledSource, pane);
                viewModes.Add(pane, serializedPane.ViewMode);
                gridItemSizes.Add(pane, serializedPane.GridItemSize);
                var serializedWeightIndex = serializedPane.SerializedIndex + 1;
                if (hasCompleteWeights
                    && serializedWeightIndex < pending.PaneWeights.Count)
                {
                    restoredWeights.Add(
                        pending.PaneWeights[serializedWeightIndex]);
                }
                else
                {
                    hasCompleteWeights = false;
                }
                if (serializedPane.SerializedIndex == pending.ActiveAuxiliaryIndex)
                {
                    activeAuxiliary = pane;
                }
            }

            var restored = false;
            _isApplyingSplitRestore = true;
            try
            {
                restored = _viewModel.RestoreAuxiliaries(
                    candidates,
                    activeAuxiliary);
                if (restored)
                {
                    _shell.ApplyRestoredSplitLayout(
                        viewModes,
                        gridItemSizes,
                        hasCompleteWeights
                            ? restoredWeights
                            : Array.Empty<float>());
                }
            }
            finally
            {
                _pendingSplitRestore = null;
                _isApplyingSplitRestore = false;
            }

            if (restored)
            {
                SaveSplitSnapshot();
            }
        }

        private void OnSplitArrangementChanged()
        {
            SaveSplitSnapshot();
        }

        private void SaveSplitSnapshot()
        {
            if (_isApplyingSplitRestore
                || _pendingSplitRestore != null
                || _viewModel == null
                || _shell == null)
            {
                return;
            }

            _splitSnapshot = CaptureSplitSnapshot(
                _viewModel,
                _shell,
                AssetDatabase.AssetPathToGUID);
            EditorUtility.SetDirty(this);
        }

        internal static ZepSplitSnapshot CaptureSplitSnapshot(
            ZepState state,
            ZepShellView shell,
            Func<string, string> pathToGuid)
        {
            var snapshot = new ZepSplitSnapshot();
            if (state == null || shell == null)
            {
                return snapshot;
            }

            for (var i = 0; i < state.AuxiliaryPanes.Count; i++)
            {
                var pane = state.AuxiliaryPanes[i];
                var hasPresentation = shell.TryGetPanePresentation(
                    pane,
                    out var viewMode,
                    out var gridItemSize);
                var serializedPane = CapturePaneSnapshot(
                    pane,
                    hasPresentation ? viewMode : AssetViewMode.List,
                    hasPresentation ? gridItemSize : AssetsAreaConstants.DefaultAssetGridItemSize,
                    pathToGuid);

                if (ReferenceEquals(shell.LocalActivePane, pane))
                {
                    snapshot.ActiveAuxiliaryIndex = i;
                }

                snapshot.AuxiliaryPanes.Add(serializedPane);
            }

            var weights = shell.SplitPaneWeights;
            for (var i = 0; i < weights.Count; i++)
            {
                snapshot.PaneWeights.Add(weights[i]);
            }

            return snapshot;
        }

        internal static ZepSplitPaneSnapshot CapturePaneSnapshot(
            ZepPaneState pane,
            AssetViewMode viewMode,
            int gridItemSize,
            Func<string, string> pathToGuid)
        {
            // Both window and split panes preserve the root by GUID, including
            // the folder opened under a tag, so a move survives domain reload.
            var source = pane.Source;
            var rootFolderPath = pane.BreadcrumbRootPath;
            return new ZepSplitPaneSnapshot
            {
                SourceKind = (int)source.Kind,
                TagId = source.Kind == ZepPaneSourceKind.Tag
                    ? source.TagId
                    : string.Empty,
                CurrentFolderPath = rootFolderPath.IsEmpty
                    ? string.Empty
                    : pane.CurrentFolderPath.Value,
                LastKnownFolderPath = rootFolderPath.Value ?? string.Empty,
                FolderGuid = !rootFolderPath.IsEmpty && pathToGuid != null
                    ? pathToGuid(rootFolderPath.Value) ?? string.Empty
                    : string.Empty,
                ViewMode = (int)viewMode,
                GridItemSize = gridItemSize,
                History = CaptureHistorySnapshot(pane)
            };
        }

        internal static bool TryResolveSnapshotPane(
            ZepSplitPaneSnapshot serializedPane,
            Func<string, string> guidToPath,
            out ZepPaneSource source,
            out AssetPath tagBrowseRootPath,
            out AssetPath currentFolderPath,
            out AssetViewMode viewMode,
            out int gridItemSize)
        {
            source = default;
            tagBrowseRootPath = default;
            currentFolderPath = default;
            viewMode = serializedPane != null
                && serializedPane.ViewMode == (int)AssetViewMode.Grid
                    ? AssetViewMode.Grid
                    : AssetViewMode.List;
            gridItemSize = Mathf.Clamp(
                serializedPane?.GridItemSize
                    ?? AssetsAreaConstants.DefaultAssetGridItemSize,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize);

            if (serializedPane == null)
            {
                return false;
            }

            switch ((ZepPaneSourceKind)serializedPane.SourceKind)
            {
                case ZepPaneSourceKind.Folder:
                    var folderPath = ResolveFolderSnapshotPath(
                        serializedPane,
                        guidToPath);
                    if (!AssetPathClassification.IsProjectAssetPath(folderPath))
                    {
                        return false;
                    }

                    source = ZepPaneSource.RootedFolder(
                        AssetPath.From(folderPath));
                    currentFolderPath = AssetPath.From(RemapSnapshotDescendant(
                        serializedPane.CurrentFolderPath,
                        serializedPane.LastKnownFolderPath,
                        folderPath));
                    return true;
                case ZepPaneSourceKind.Tag:
                    if (string.IsNullOrWhiteSpace(serializedPane.TagId))
                    {
                        return false;
                    }

                    source = ZepPaneSource.FixedTag(serializedPane.TagId);
                    // An opened folder is optional: unresolvable means the
                    // pane comes back on its list, never that it is dropped.
                    if (!string.IsNullOrWhiteSpace(serializedPane.LastKnownFolderPath)
                        || !string.IsNullOrWhiteSpace(serializedPane.FolderGuid))
                    {
                        var openedFolder = ResolveFolderSnapshotPath(
                            serializedPane,
                            guidToPath);
                        if (AssetPathClassification.IsProjectAssetPath(openedFolder))
                        {
                            tagBrowseRootPath = AssetPath.From(openedFolder);
                            currentFolderPath = AssetPath.From(RemapSnapshotDescendant(
                                serializedPane.CurrentFolderPath,
                                serializedPane.LastKnownFolderPath,
                                openedFolder));
                        }
                    }

                    return true;
                default:
                    return false;
            }
        }

        internal static string ResolveFolderSnapshotPath(
            ZepSplitPaneSnapshot serializedPane,
            Func<string, string> guidToPath)
        {
            if (serializedPane == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(serializedPane.FolderGuid)
                && guidToPath != null)
            {
                var guidPath = guidToPath(serializedPane.FolderGuid);
                if (AssetPathClassification.IsProjectAssetPath(guidPath))
                {
                    return guidPath;
                }
            }

            return serializedPane.LastKnownFolderPath ?? string.Empty;
        }

        private static string RemapSnapshotDescendant(
            string currentFolderPath,
            string lastKnownRoot,
            string resolvedRoot)
        {
            var current = AssetPath.From(currentFolderPath).Value;
            var oldRoot = AssetPath.From(lastKnownRoot).Value;
            var newRoot = AssetPath.From(resolvedRoot).Value;
            if (string.IsNullOrEmpty(current)
                || string.IsNullOrEmpty(oldRoot)
                || string.IsNullOrEmpty(newRoot)
                || (!string.Equals(current, oldRoot, StringComparison.OrdinalIgnoreCase)
                    && !(current.Length > oldRoot.Length
                        && current.StartsWith(
                            oldRoot,
                            StringComparison.OrdinalIgnoreCase)
                        && current[oldRoot.Length] == '/')))
            {
                return currentFolderPath;
            }

            return newRoot + current.Substring(oldRoot.Length);
        }

        private void OnViewModelChanged(ZepPaneChange change)
        {
            if (change.Kind != ZepPaneChangeKind.Updated
                || !ReferenceEquals(change.NewPane, _viewModel?.PrimaryPane)
                || (change.Update
                    & (ZepPaneUpdate.Navigation
                        | ZepPaneUpdate.History)) == 0)
            {
                return;
            }

            var pane = _viewModel.PrimaryPane;
            if ((change.Update & ZepPaneUpdate.Navigation) != 0)
            {
                var current = pane.CurrentFolderPath;
                if (!string.IsNullOrEmpty(current))
                {
                    _savedFolderPath = current;
                }
            }

            _historySnapshot = CaptureHistorySnapshot(pane);
        }

        // Unity keeps its selection across a domain reload; the pane does not,
        // and the controller ignores selection events that arrive before panel
        // use so a restored selection cannot close the serialized-restore gate.
        // Mirror it once here, after the restore and BeginPanelUse, which is what
        // keeps the script being edited highlighted after its compile — as
        // Unity's own Project window does. Only assets inside the restored folder:
        // a selection elsewhere must not pull the panel away from the folder it
        // has just restored.
        private void MirrorEditorSelectionIntoRestoredFolder()
        {
            var pane = _viewModel.PrimaryPane;
            if (pane.SelectedAssetPaths.Count != 0)
            {
                return;
            }

            var selected = UnitySelectionResolver.GetSelectedAssetPathsFromEditorSelection();
            if (selected.Count == 0)
            {
                return;
            }

            var folder = pane.CurrentFolderPath.Value;
            var inFolder = new List<string>(selected.Count);
            for (var i = 0; i < selected.Count; i++)
            {
                if (string.Equals(
                    AssetPathFormat.GetParentFolder(selected[i]),
                    folder,
                    StringComparison.OrdinalIgnoreCase))
                {
                    inFolder.Add(selected[i]);
                }
            }

            if (inFolder.Count == 0)
            {
                return;
            }

            _controller.HandleSelectionPaths(inFolder);
        }

        internal static ZepHistorySnapshot CaptureHistorySnapshot(
            ZepPaneState pane)
        {
            var snapshot = new ZepHistorySnapshot
            {
                Index = pane.NavigationHistoryIndex,
            };
            var history = pane.NavigationHistory;
            for (var i = 0; i < history.Count; i++)
            {
                var entry = history[i];
                snapshot.Entries.Add(new ZepHistoryEntrySnapshot
                {
                    FolderPath = entry.FolderPath ?? string.Empty,
                    TagId = entry.TagId ?? string.Empty,
                    BrowseRootPath = entry.BrowseRootPath ?? string.Empty,
                });
            }

            return snapshot;
        }

        internal static void RestoreHistory(
            ZepPaneState pane,
            ZepHistorySnapshot snapshot,
            string lastKnownRoot = null)
        {
            if (snapshot == null || snapshot.Entries == null || snapshot.Entries.Count == 0)
            {
                return;
            }

            // The GUID-resolved root is already live. Move its saved folder
            // entries and tag browse ceilings before Core constrains history.
            var entries = new List<NavigationEntry>(snapshot.Entries.Count);
            for (var i = 0; i < snapshot.Entries.Count; i++)
            {
                var serialized = snapshot.Entries[i];
                if (serialized == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(serialized.FolderPath))
                {
                    entries.Add(NavigationEntry.Folder(
                        RemapSnapshotDescendant(
                            serialized.FolderPath,
                            lastKnownRoot,
                            pane.BreadcrumbRootPath.Value),
                        RemapSnapshotDescendant(
                            serialized.BrowseRootPath,
                            lastKnownRoot,
                            pane.BreadcrumbRootPath.Value)));
                }
                else if (!string.IsNullOrWhiteSpace(serialized.TagId))
                {
                    entries.Add(NavigationEntry.Tag(serialized.TagId));
                }
            }

            pane.RestoreNavigationHistory(entries, snapshot.Index);
        }

        private void ModifierKeysChanged()
        {
            if (focusedWindow != this || _shell == null) return;
            // macOS reports standalone Shift through this native window callback,
            // without UI Toolkit key events. CommandEvent snapshots native modifier
            // state even outside OnGUI, where Event.current is unavailable.
            var modifiers = EditorGUIUtility.CommandEvent("ModifierKeysChanged").modifiers;
            _shell.ShortcutController.HandleModifierKeysChanged(modifiers);
        }

        private void OnFocus()
        {
            _shell?.RestoreLocalPaneFocus();
        }

        private void OnLostFocus()
        {
            _shell?.ShortcutController.ResetDoubleShift();
        }

        private void OnDisable()
        {
            ZepWindowDocking.Clear();
            ZepSplitDragAndDrop.Clear();
            SaveSplitSnapshot();
            // A domain reload restores this window's managed fields — plain
            // private ones as well as [SerializeField] ones — so the one-shot
            // latch comes back already set and the next CreateGUI returns on
            // PrepareSerializedRestore's FIRST line, before the pristine gate
            // or the folder write are reached: the serialized folder is never
            // applied, BeginPanelUse projects an empty pane to "Assets", and
            // OnViewModelChanged then overwrites _savedFolderPath with it.
            // OnDisable is the reload door (a reload disables the window and
            // then rebuilds it through CreateGUI), so clearing the latch here
            // is what makes that CreateGUI the first attempt of the new domain.
            // Re-attempting after a re-dock is safe: the pane is no longer
            // pristine, and where it still is, the serialized folder is the
            // folder the pane already shows.
            _didAttemptSerializedRestore = false;
            // Disable precedes both close and reload. Remove the registration
            // while clearing its latch so close cannot leave a stale first
            // window blocking pings, and the next CreateGUI can register again.
            UnregisterOpenWindow();
            if (_controller != null)
            {
                _controller.PaneSourceReconciliationReady -=
                    OnPaneSourceReconciliationReady;
            }
            if (_viewModel != null)
            {
                _viewModel.PaneChanged -= OnViewModelChanged;
            }

            if (_shell != null)
            {
                _shell.SplitArrangementChanged -= OnSplitArrangementChanged;
                // The shell owns a runtime-built StyleSheet for user themes — a
                // native object that outlives a domain reload while the shell's
                // managed tree does not, so every reload used to orphan one sheet.
                // OnDisable is the reload/close door, and it is always followed by
                // a full rebuild (CreateGUI clears the root and builds a new shell,
                // which re-applies the theme) or by OnDestroy. Teardown is
                // idempotent, so the rebuild path's own Teardown call is a no-op.
                _shell.Teardown();
            }

            // Do NOT dispose _controllerHost: it's the session-scoped singleton
            // (ZepControllerHost.Shared) and we want it to outlive this
            // window instance so the next panel open is instant. The host's
            // EditorApplication / AssetPostprocessor subscriptions also stay
            // active in the background so the index keeps up with project
            // changes while the panel is closed. The singleton is reset by the
            // next domain reload, at which point InitializeOnLoad re-prewarms.
            _controllerHost = null;
            _viewModel = null;
        }

        private void OnDestroy()
        {
            // Usually already removed by OnDisable; keep destruction cleanup
            // idempotent for a window destroyed before it was disabled.
            UnregisterOpenWindow();

            if (_shell != null)
            {
                _shell.SplitArrangementChanged -= OnSplitArrangementChanged;
            }
            _shell?.Teardown();
            _shell = null;
        }

        private void HandleAssetSelected(
            ZepPaneState pane,
            System.Collections.Generic.IReadOnlyList<ZepAssetEntry> assets)
        {
            HandleAssetSelected(_controller, pane, assets);
        }

        internal static void HandleAssetSelected(
            ZepController controller,
            ZepPaneState pane,
            System.Collections.Generic.IReadOnlyList<ZepAssetEntry> assets)
        {
            if (assets == null || assets.Count == 0)
            {
                if (Selection.objects.Length > 0)
                {
                    controller?.ExpectPanelSelectionChange(
                        pane,
                        System.Array.Empty<string>());
                    Selection.objects = System.Array.Empty<UnityEngine.Object>();
                }

                return;
            }

            var selectedObjects = new System.Collections.Generic.List<UnityEngine.Object>(assets.Count);
            for (var i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null || string.IsNullOrEmpty(asset.Path))
                {
                    continue;
                }

                var mainAsset = AssetDatabase.LoadMainAssetAtPath(asset.Path);
                if (mainAsset != null)
                {
                    selectedObjects.Add(mainAsset);
                }
            }

            if (selectedObjects.Count == 0)
            {
                if (Selection.objects.Length > 0)
                {
                    controller?.ExpectPanelSelectionChange(
                        pane,
                        System.Array.Empty<string>());
                    Selection.objects = System.Array.Empty<UnityEngine.Object>();
                }

                return;
            }

            if (HasSameSelection(Selection.objects, selectedObjects))
            {
                return;
            }

            var selectedPaths = new string[selectedObjects.Count];
            for (var i = 0; i < selectedObjects.Count; i++)
            {
                selectedPaths[i] = AssetDatabase.GetAssetPath(selectedObjects[i]);
            }

            controller?.ExpectPanelSelectionChange(pane, selectedPaths);
            Selection.objects = selectedObjects.ToArray();
        }

        private static bool HasSameSelection(
            System.Collections.Generic.IReadOnlyList<UnityEngine.Object> currentSelection,
            System.Collections.Generic.IReadOnlyList<UnityEngine.Object> nextSelection)
        {
            var currentCount = currentSelection?.Count ?? 0;
            var nextCount = nextSelection?.Count ?? 0;
            if (currentCount != nextCount)
            {
                return false;
            }

            for (var i = 0; i < currentCount; i++)
            {
                if (currentSelection[i] != nextSelection[i])
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class PendingSplitRestore
        {
            internal PendingSplitRestore(
                IReadOnlyList<PendingSplitPane> panes,
                int activeAuxiliaryIndex,
                IReadOnlyList<float> paneWeights)
            {
                Panes = panes ?? Array.Empty<PendingSplitPane>();
                ActiveAuxiliaryIndex = activeAuxiliaryIndex;
                PaneWeights = paneWeights != null
                    ? new List<float>(paneWeights)
                    : new List<float>();
            }

            internal IReadOnlyList<PendingSplitPane> Panes { get; }

            internal int ActiveAuxiliaryIndex { get; }

            internal IReadOnlyList<float> PaneWeights { get; }
        }

        private sealed class PendingSplitPane
        {
            internal PendingSplitPane(
                int serializedIndex,
                ZepPaneSource source,
                AssetPath tagBrowseRootPath,
                AssetPath currentFolderPath,
                AssetViewMode viewMode,
                int gridItemSize,
                ZepHistorySnapshot history,
                string lastKnownFolderPath)
            {
                SerializedIndex = serializedIndex;
                Source = source;
                TagBrowseRootPath = tagBrowseRootPath;
                CurrentFolderPath = currentFolderPath;
                ViewMode = viewMode;
                GridItemSize = gridItemSize;
                History = history;
                LastKnownFolderPath = lastKnownFolderPath;
            }

            internal int SerializedIndex { get; }

            internal ZepPaneSource Source { get; }

            internal AssetPath TagBrowseRootPath { get; }

            internal AssetPath CurrentFolderPath { get; }

            internal AssetViewMode ViewMode { get; }

            internal int GridItemSize { get; }

            internal ZepHistorySnapshot History { get; }

            internal string LastKnownFolderPath { get; }
        }
    }
}
