using System;
using Zep.Core.Application;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.Bridge.Settings;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using UnityEditor;

namespace Zep.Editor.Bridge
{
    // Bridge composition root for the Core controller. Owns the Unity-side
    // wiring: subscribes to AssetPostprocessor / Selection / projectChanged
    // events and forwards them into the Core controller via the public
    // Handle* methods. Holds no parallel state — the controller is the sole
    // source of truth.
    //
    // Lifecycle: the host is a session-scoped singleton, not per-window. A
    // single controller + index lives for the editor session and survives
    // panel-close/reopen cycles, so the second-and-later panel opens see an
    // already-hydrated index and paint with real data immediately. The
    // singleton is reset on every domain reload (static-field clear), at
    // which point the [InitializeOnLoadMethod] prewarm below kicks off the
    // next lightweight callback owner. Global hydration begins only when a
    // panel calls BeginPanelUse.
    [UnityEditor.InitializeOnLoad]
    public sealed class ZepControllerHost : IDisposable
    {
        public ZepController Core { get; }

        private readonly Action _projectChangedHandler;
        private readonly Action _selectionChangedHandler;
        private readonly Action _packageRootsChangedHandler;
        private readonly ProjectCommandHistory _history;
        private readonly UnityProjectUndoBridge _unityUndoBridge;

        private static ZepControllerHost _shared;
        private static ZepState _sharedViewModel;
        private static bool _packageRemovalStarted;

        internal static void StopForPackageRemoval()
        {
            _packageRemovalStarted = true;
            _shared?.Dispose();
            _shared = null;
            _sharedViewModel = null;
            EditorPrefs.DeleteKey(SearchWasUsedPreferenceKey);
        }

        private static string SearchWasUsedPreferenceKey =>
            "Zep.SearchWasUsed."
            + UnityEngine.Application.dataPath;

        /// <summary>
        /// Session-scoped singleton host. First access constructs the host and
        /// subscribes it to the editor's callbacks; index hydration is NOT part of
        /// construction — it is scheduled by <c>Core.BeginPanelUse()</c>, which the
        /// window calls when a panel actually opens. Subsequent accesses return the
        /// already-built instance. Reset to null on every domain reload along with
        /// every other static field.
        /// </summary>
        public static ZepControllerHost Shared
        {
            get
            {
                if (_packageRemovalStarted)
                {
                    throw new ObjectDisposedException(nameof(ZepControllerHost));
                }
                if (_shared == null)
                {
                    // Publish the view model only once Create has succeeded: a
                    // failed prewarm must leave BOTH statics null so the next
                    // access retries, instead of handing out a view model whose
                    // host never existed.
                    var viewModel = new ZepState();
                    _shared = Create(viewModel);
                    _sharedViewModel = viewModel;
                }
                return _shared;
            }
        }

        /// <summary>
        /// Session-scoped singleton view model that pairs with <see cref="Shared"/>.
        /// Carries the current folder, search text, selection, and other UI state
        /// across panel-close/reopen cycles so reopens land back on the same view.
        /// </summary>
        public static ZepState SharedViewModel
        {
            get
            {
                if (_sharedViewModel == null)
                {
                    _ = Shared; // initializes both
                }
                return _sharedViewModel;
            }
        }

        // [InitializeOnLoad]-driven entry point. The static ctor fires on every
        // assembly load (initial Unity startup + every domain reload). We touch
        // the singleton on a delayCall so undo/history and change callbacks are
        // available even with no open panel. This must stay lightweight: it does
        // not validate an index cache or enumerate project/package contents.
        //
        // The override-check guards against test pollution: a test that has
        // replaced the cache's label or artifact-revision snapshot doesn't want
        // a concurrent prewarm hydrating through the same overrides — that
        // produces duplicate warnings and race-prone cache-file writes. The
        // overrides are a clean signal we're in test context.
        //
        // The try/catch is unconditional: a prewarm failure must never block
        // normal editor use, and the panel's open path retries hydration from
        // scratch anyway.
        static ZepControllerHost()
        {
            // Core's index cache and synchronizer need their Unity bindings before
            // this host builds them, and a window can reach the host before Unity
            // runs load callbacks.
            Indexing.AssetDatabaseAssetRecordSource.BindCoreIndexing();
            EditorApplication.delayCall += () =>
            {
                if (_packageRemovalStarted || PersistentAssetIndexCache.HasTestOverrides)
                {
                    return;
                }

                try
                {
                    _ = Shared;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[Zep] Session prewarm failed ({ex.GetType().Name}: {ex.Message}); panel open will retry hydration.");
                }
            };
        }

        internal static ZepControllerHost Create(ZepState viewModel)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

            if (!UnityProjectAssetGateway.TryRecoverTagStateTransaction(out var recoveryError))
            {
                throw new InvalidOperationException(recoveryError);
            }

            var __sw_total = System.Diagnostics.Stopwatch.StartNew();

            var __sw_deps = System.Diagnostics.Stopwatch.StartNew();
            var settings = ZepSettings.instance;
            var enablePersistentCache = settings == null || settings.EnablePersistentIndexCache;

            var gateway = new UnityProjectAssetGateway();
            // Persist the undo/redo stacks through SessionState so they survive
            // the domain reload that reimport triggers after asset operations.
            // Without this, moving a folder (which usually causes script
            // recompilation) wipes the in-memory history and Cmd+Z shows
            // "Nothing to undo" right after a real operation.
            var undoStorage = new SessionUndoHistoryStorage();
            var history = new ProjectCommandHistory(
                undoStorage.UndoStack,
                undoStorage.RedoStack,
                undoStorage.Save);
            // The history caps its undo stack; the storage owns the applied
            // revision that has to keep matching the stack's depth. The two
            // share a lifetime (the storage owns the very lists the history
            // mutates), so this subscription needs no matching teardown.
            history.Trimmed += undoStorage.NoteTrimmedRecords;
            var commands = new ProjectFileCommandService(gateway, history);
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            var configuredSources = CreateConfiguredRecordSources(
                settings != null && settings.UseNativeFileSystemAssetSource);
            var source = configuredSources.SyncSource;
            // Declared here, assigned below: the controller's constructor takes the
            // synchronizer and rejects null, so it cannot be built first — yet the
            // synchronizer's follow-up type-reconciliation job has to publish each
            // rebuilt slice into it. The lambda captures the VARIABLE, so it reads
            // whatever core holds when the job first runs, which can only be after
            // SynchronizeAsync, long after this method returns. Same technique and
            // same reason as the AssetChangeSyncer lambda in ZepController's
            // own constructor.
            ZepController core = null;
            var directFolderSource = new NativeDirectFolderAssetSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                configuredSources.AsyncSource,
                index,
                enablePersistentCache,
                delta => core.ApplyIndexPatch(delta),
                directFolderSource);

            var deferredScheduler = new UnityDeferredCallScheduler();
            var shellReveal = new UnityShellReveal();
            var addressablesQuery = new UnityAddressablesQuery();

            __sw_deps.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] Host.Create deps construction ms={__sw_deps.Elapsed.TotalMilliseconds:F2}");

            var __sw_tagsettings = System.Diagnostics.Stopwatch.StartNew();
            var tagSettingsStore = ZepTagSettings.instance.Store;
            __sw_tagsettings.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ZepTagSettings.instance.Store ms={__sw_tagsettings.Elapsed.TotalMilliseconds:F2}");

            // 4.32(b): "this project's user has searched at least once", which is
            // what earns the ~1.35 GB startup search-index warm. EditorPrefs, not
            // SessionState: the observation has to outlive the session that made it
            // — a SessionState flag is cleared on every editor restart, so the next
            // launch would never warm and the gate would only ever cost. EditorPrefs
            // is machine-wide, hence the project path in the key.
            var __sw_ctor = System.Diagnostics.Stopwatch.StartNew();
            core = new ZepController(
                viewModel,
                gateway,
                commands,
                source,
                index,
                search,
                tagSettingsStore,
                synchronizer,
                deferredScheduler,
                shellReveal,
                addressablesQuery,
                directFolderSource,
                EditorPrefs.GetBool(SearchWasUsedPreferenceKey, false),
                () => EditorPrefs.SetBool(SearchWasUsedPreferenceKey, true));
            __sw_ctor.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] new ZepController ms={__sw_ctor.Elapsed.TotalMilliseconds:F2}");

            var __sw_host = System.Diagnostics.Stopwatch.StartNew();
            var host = new ZepControllerHost(core, commands, history, undoStorage);
            __sw_host.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] new ZepControllerHost (subscribes; hydration waits for BeginPanelUse) ms={__sw_host.Elapsed.TotalMilliseconds:F2}");

            __sw_total.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] Host.Create TOTAL ms={__sw_total.Elapsed.TotalMilliseconds:F2}");
            return host;
        }

        private ZepControllerHost(
            ZepController core,
            ProjectFileCommandService commands,
            ProjectCommandHistory history,
            SessionUndoHistoryStorage undoStorage)
        {
            Core = core;
            _history = history;
            _projectChangedHandler = HandleProjectChanged;
            _selectionChangedHandler = HandleSelectionChanged;
            _packageRootsChangedHandler = Core.HandleProjectChanged;
            UnityProjectUndoBridge undoBridge = null;
            try
            {
                undoBridge = UnityProjectUndoBridge.Create(
                    undoStorage,
                    history,
                    Core.Undo,
                    Core.Redo,
                    Core.RunBulk,
                    Core.ClearRedoHistory);
                _unityUndoBridge = undoBridge;
                commands.SetExecutionPreparation(
                    _unityUndoBridge.PrepareForNewCommand,
                    _unityUndoBridge.PrepareForAppliedCommand);
                _history.Executed += _unityUndoBridge.RecordExecuted;
                _history.Invalidated += _unityUndoBridge.InvalidateTimeline;
                Core.AssetPathRewritten += HandleAssetPathRewritten;
                EditorApplication.projectChanged += _projectChangedHandler;
                Selection.selectionChanged += _selectionChangedHandler;
                ZepAssetPostprocessor.Changed += Core.HandleAssetChanges;
                RegisteredPackageRoots.Changed += _packageRootsChangedHandler;
            }
            catch
            {
                Core.AssetPathRewritten -= HandleAssetPathRewritten;
                EditorApplication.projectChanged -= _projectChangedHandler;
                Selection.selectionChanged -= _selectionChangedHandler;
                ZepAssetPostprocessor.Changed -= Core.HandleAssetChanges;
                RegisteredPackageRoots.Changed -= _packageRootsChangedHandler;
                commands.SetExecutionPreparation(null);
                if (undoBridge != null)
                {
                    _history.Executed -= undoBridge.RecordExecuted;
                    _history.Invalidated -= undoBridge.InvalidateTimeline;
                    undoBridge.Dispose();
                }
                try
                {
                    Core.Dispose();
                }
                catch (Exception disposeException)
                {
                    UnityEngine.Debug.LogError(
                        "[Zep] Failed to clean up an incomplete controller host: "
                        + disposeException.Message);
                }

                throw;
            }
        }

        public void Dispose()
        {
            Core.AssetPathRewritten -= HandleAssetPathRewritten;
            EditorApplication.projectChanged -= _projectChangedHandler;
            Selection.selectionChanged -= _selectionChangedHandler;
            ZepAssetPostprocessor.Changed -= Core.HandleAssetChanges;
            RegisteredPackageRoots.Changed -= _packageRootsChangedHandler;
            _history.Executed -= _unityUndoBridge.RecordExecuted;
            _history.Invalidated -= _unityUndoBridge.InvalidateTimeline;
            _unityUndoBridge.Dispose();
            Core.Dispose();
        }

        private void HandleAssetPathRewritten(string previousPath, string newPath)
        {
            ZepFavoritesSettings.instance.Store.Rewrite(previousPath, newPath);
        }

        private void HandleSelectionChanged()
        {
            Core.HandleSelectionPaths(UnitySelectionResolver.GetSelectedAssetPathsFromEditorSelection());
        }

        private void HandleProjectChanged()
        {
            // Package topology is Unity-owned main-thread state. Publish one
            // immutable replacement snapshot before any deferred reconciliation
            // observes the change; workers never call PackageInfo themselves.
            // Cheap unless Package Manager reported a registration change.
            RegisteredPackageRoots.Refresh();
            Core.HandleProjectChanged();
        }

        internal static (
            IAssetRecordSource SyncSource,
            IAssetRecordSource AsyncSource) CreateConfiguredRecordSources(
                bool useNativeFileSystemAssetSource)
        {
            // Default: the hybrid source - native filesystem walk for the path
            // list + Unity's in-memory guid hashtable (AssetPathToGUID) instead
            // of a per-asset .meta file read. Strictly faster on the main thread
            // than either pure alternative. The pure-native source is still
            // injected into the synchronizer for the async background-startup
            // path so the editor remains interactive on first-ever launch.
            //
            // The setting kill-switch retains the original AssetDatabase source
            // for both the synchronous and cold-startup paths. Falling through to
            // a native worker here would make the switch ineffective on cache misses.
            if (useNativeFileSystemAssetSource)
            {
                return (
                    new HybridAssetRecordSource(),
                    new NativeFileSystemAssetRecordSource());
            }

            var assetDatabaseSource = new AssetDatabaseAssetRecordSource();
            return (assetDatabaseSource, assetDatabaseSource);
        }
    }
}
