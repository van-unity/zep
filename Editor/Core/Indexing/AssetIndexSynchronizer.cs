using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// Identifies which path <see cref="AssetIndexSynchronizer.SynchronizeAsync"/> took on
    /// its last call. <see cref="SyncFallback"/> means the async setup hit an error or the
    /// source was not thread-safe and the synchronizer ran on the calling thread instead.
    /// Exposed so callers and tests can detect the degraded path; otherwise an async
    /// failure looks identical to a normal sync hydration.
    /// </summary>
    internal enum EnumerationStrategy
    {
        None = 0,
        Async,
        SyncFallback
    }

    /// <summary>
    /// What <see cref="AssetIndexSynchronizer"/> needs from the Unity editor: a
    /// per-frame tick for its polls, the console, a flag that survives domain
    /// reloads but not an editor restart, and the asset-type queries only the
    /// AssetDatabase can answer. Implemented in the Bridge.
    /// </summary>
    public interface IAssetIndexEditorHost
    {
        /// <summary>Calls <paramref name="poll"/> on the main thread every editor frame until detached.</summary>
        void AttachUpdate(Action poll);

        /// <summary>
        /// Removes a poll attached earlier. The argument may be a different delegate
        /// instance for the same method and target.
        /// </summary>
        void DetachUpdate(Action poll);

        void Log(string message);

        void LogWarning(string message);

        void LogException(Exception exception);

        bool GetSessionFlag(string key);

        void SetSessionFlag(string key, bool value);

        /// <summary>Main thread only.</summary>
        string[] CaptureAllAssetPaths();

        /// <summary>Main thread only.</summary>
        HashSet<string> CaptureScriptedImporterExtensions();

        /// <summary>Safe to call from a worker thread.</summary>
        List<string> FilterAuthoritativeTypeCandidatePaths(
            IReadOnlyList<string> allAssetPaths,
            ISet<string> scriptedImporterExtensions);

        /// <summary>Main thread only.</summary>
        void AddAuthoritativeTypeMetadata(
            string path,
            IDictionary<string, AssetTypeMetadata> typesByGuid);

        bool RequiresAuthoritativeType(
            string assetPath,
            ISet<string> scriptedImporterExtensions);
    }

    public sealed class AssetIndexSynchronizer :
        IAssetIndexSynchronizer,
        IAssetIndexSynchronizationResults,
        ICatalogDirtyMarker,
        IAssetScanChangeRecorder
    {
        // Per-frame ceiling for main-thread work that must be chopped into slices:
        // the cold path's authoritative type capture and the post-reload script-type
        // reconciliation both resolve one asset at a time through Unity's importers.
        private const double MainThreadWorkBudgetMilliseconds = 2.0;

        // The overlay is the in-memory twin of the durable pending-change journal:
        // both describe changes an authoritative walk has not observed yet, and both
        // must stop growing rather than turn a long churn session into a multi-second
        // ApplyScanOverlay (it is O(records x roots)). The bound mirrors the journal's
        // MaxPendingChangeEntryCount for the same reason it was chosen there — past it,
        // exact replay is worth less than a fresh walk. Distinct entries only: repeats
        // of one path coalesce, so reaching this needs 1024 distinct unpublished paths.
        internal const int MaxScanOverlayEntryCount = 1_024;
        private static readonly SemaphoreSlim SourceScanGate =
            new SemaphoreSlim(1, 1);

        // The synchronizer's Unity dependencies. Each one is a process-wide editor
        // fact, so the Bridge binds them once per domain instead of every
        // construction site passing them in.
        private static IAssetIndexEditorHost _editorHost;

        public static void BindUnity(IAssetIndexEditorHost editorHost)
        {
            _editorHost = editorHost ?? throw new ArgumentNullException(nameof(editorHost));
        }

        private static IAssetIndexEditorHost EditorHost =>
            _editorHost ?? throw new InvalidOperationException(
                "AssetIndexSynchronizer.BindUnity must run before the synchronizer is used.");

        private readonly IAssetRecordSource _syncSource;
        private readonly IAssetRecordSource _asyncSource;
        private readonly InMemoryAssetIndex _index;
        private readonly bool _enablePersistentCache;
        // Where each slice the follow-up type-reconciliation job rebuilds is
        // published. The job applies its slice to the index itself, so without
        // this the delta would be discarded and the retype would stay invisible
        // until something else rebuilt the projection cache. Null when nobody
        // wired one — every construction that is not the Bridge composition
        // root, including all of the tests that do not assert on it.
        private readonly Action<AssetCatalogDelta> _onCatalogPatched;

        // The observer the browser uses for one folder's direct children. The
        // journal replay recurses through it when a batch imports a FOLDER: the
        // batch names one path, but the folder on disk may already hold a subtree
        // (a folder pasted in from Finder, an imported sample). Null when nobody
        // wired one — such an import then stays unresolved and the caller falls
        // back to the authoritative scan, exactly as before it existed.
        private readonly IDirectFolderAssetSource _directFolderSource;

        // AssetPostprocessor callbacks run on the main thread, while a cold source
        // walk runs on a worker. A generation captured before dispatch prevents the
        // worker's point-in-time snapshot from overwriting an import/delete that was
        // applied to the live index while that walk was in flight.
        private int _assetChangeGeneration;
        // Overflow loses concrete history, and an unresolved path has no confirmed
        // current record to overlay. Either invalidates an older authoritative proof.
        private int _authoritativeProofEpoch;
        // Nonzero until a current authoritative walk settles these failures.
        private int _unresolvedProofEpoch;
        private int _synchronizationOperationGeneration;
        private SynchronizationOperation _activeOperation;
        // The operation whose follow-up background job — the type reconciliation's
        // slices, a verification walk, or either one's staged save — is still
        // outstanding. Null means nothing is outstanding.
        private SynchronizationOperation _backgroundJobOperation;
        // The snapshot save whose serialization is on a worker right now. It holds the
        // cache's journal write marker until it is committed or abandoned, so it must
        // be resolved on exactly one path — the completion poll, or Dispose.
        private PendingSnapshotSave _pendingSnapshotSave;
        private readonly List<Action<AssetIndexSynchronizationResult>> _completionWaiters = new List<Action<AssetIndexSynchronizationResult>>();
        private readonly List<Action> _editorPolls = new List<Action>();
        private readonly List<string> _scanOverlayRemovedRoots = new List<string>();
        // Same contents as the list, as a set, so AddRemovedRoot can answer "is this
        // path already covered" with an ancestor walk instead of scanning every root.
        private readonly HashSet<string> _scanOverlayRemovedRootSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _deepestRemovedRootSegmentCount;
        private readonly HashSet<string> _scanOverlayExpectedCurrentRoots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssetRecord> _scanOverlayRecordUpserts =
            new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssetFolderRecord> _scanOverlayFolderUpserts =
            new Dictionary<string, AssetFolderRecord>(StringComparer.OrdinalIgnoreCase);

        // Folder metadata to hand to the next GetFolderRecordsForProjection call. Populated by
        // TryHydrateFromCache (cache hit) and ApplyRecords (cache miss). Cleared once
        // consumed so subsequent calls fall through to the live source walk — incremental
        // post-startup changes (folder add / delete via AssetPostprocessor) need a fresh
        // walk to pick up new entries.
        private IReadOnlyList<AssetFolderRecord> _pendingFolderRecords;
        private bool _isDisposed;

        /// <summary>
        /// The strategy used by the most recent <see cref="SynchronizeAsync"/> call. Reset
        /// to <see cref="EnumerationStrategy.None"/> at the start of each call. Callers
        /// that need to know whether the async path actually ran (e.g. tests asserting we
        /// did not silently fall back to sync) read this property after the completion
        /// callback fires.
        /// </summary>
        internal EnumerationStrategy LastEnumerationStrategy { get; private set; } = EnumerationStrategy.None;

        /// <summary>
        /// Receives the number of records each budgeted script-type reconciliation
        /// slice processed, so a test can pin that the per-tick batch honours
        /// <see cref="MainThreadWorkBudgetMilliseconds"/> instead of running the whole
        /// list in one frame.
        /// </summary>
        internal Action<int> ScriptTypeReconciliationSliceObserverForTests;

        internal int SynchronizationOperationGenerationForTests =>
            Volatile.Read(ref _synchronizationOperationGeneration);

        public bool HasBackgroundJob => _backgroundJobOperation != null;

        public AssetIndexSynchronizer(IAssetRecordSource source, InMemoryAssetIndex index)
            : this(source, source, index, enablePersistentCache: true)
        {
        }

        /// <summary>
        /// Two-source overload: <paramref name="syncSource"/> is used by
        /// <see cref="Synchronize"/> (typically a Unity-API-backed source that's
        /// strictly faster but main-thread-only) and <paramref name="asyncSource"/>
        /// is used by <see cref="SynchronizeAsync"/> when its <see cref="IAssetRecordSource.IsThreadSafe"/>
        /// is true (typically a pure native source that can run on a worker thread
        /// without freezing the editor). When the same source is appropriate for
        /// both paths, use the single-source constructor.
        /// </summary>
        public AssetIndexSynchronizer(
            IAssetRecordSource syncSource,
            IAssetRecordSource asyncSource,
            InMemoryAssetIndex index,
            bool enablePersistentCache)
        {
            _syncSource = syncSource ?? throw new ArgumentNullException(nameof(syncSource));
            _asyncSource = asyncSource ?? syncSource;
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _enablePersistentCache = enablePersistentCache;
        }

        /// <summary>
        /// The four-parameter overload plus the follow-up type-reconciliation job's
        /// publication seam: <paramref name="onCatalogPatched"/> receives the catalog
        /// delta of every slice the job rebuilds, so a post-compile retype reaches the
        /// panel per slice instead of waiting for a projection-cache rebuild. Only the
        /// Bridge composition root passes one; a four-argument construction still binds
        /// to the constructor above, which omits no optional argument.
        /// </summary>
        public AssetIndexSynchronizer(
            IAssetRecordSource syncSource,
            IAssetRecordSource asyncSource,
            InMemoryAssetIndex index,
            bool enablePersistentCache,
            Action<AssetCatalogDelta> onCatalogPatched = null,
            IDirectFolderAssetSource directFolderSource = null)
            : this(syncSource, asyncSource, index, enablePersistentCache)
        {
            _onCatalogPatched = onCatalogPatched;
            _directFolderSource = directFolderSource;
        }

        public void Synchronize(Action onComplete = null)
        {
            SynchronizeWithOutcome(
                onComplete == null ? null : _ => onComplete());
        }

        public void SynchronizeWithOutcome(
            Action<AssetIndexSynchronizationResult> onComplete)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(AssetIndexSynchronizer));
            }

            // A source walk is an authoritative operation already carrying all
            // recorded asset-change overlays. A manual refresh while it is active
            // must adopt that result: waiting here freezes Unity's main thread, and
            // superseding it forces the finished walk to be discarded and repeated.
            if (IsCurrent(_activeOperation))
            {
                if (onComplete != null)
                {
                    _completionWaiters.Add(onComplete);
                }

                return;
            }

            var operation = BeginOperation(null, onComplete);
            try
            {
                ZepPerfLog.Count(ZepPerfCounter.FullSynchronizerRuns);
                if (TryHydrateFromCache(
                    operation,
                    replaceNonEmptyIndex: false,
                    out var artifactRevisionChanged,
                    out var tagStateChanged,
                    out var unresolved))
                {
                    CompleteHydratedOperation(
                        operation,
                        operation.ArtifactDependencyVersion,
                        artifactRevisionChanged,
                        tagStateChanged,
                        unresolved);
                    return;
                }

                var typesByGuid = _enablePersistentCache
                    ? PersistentAssetIndexCache.CaptureAuthoritativeTypeSnapshot()
                    : null;
                if (SourceScanGate.Wait(0))
                {
                    ExecuteSynchronousRebuildOwningGate(
                        operation,
                        _syncSource,
                        operation.UnityLabelsByGuid,
                        typesByGuid,
                        operation.ArtifactDependencyVersion,
                        "Synchronous synchronization");
                    return;
                }

                BeginSynchronousRebuild(
                    operation,
                    _syncSource,
                    operation.UnityLabelsByGuid,
                    typesByGuid,
                    operation.ArtifactDependencyVersion);
            }
            catch (Exception ex)
            {
                FailOperation(operation, "Synchronous synchronization", ex);
            }
        }

        /// <summary>
        /// Non-blocking variant of <see cref="Synchronize"/>. Runs the record enumeration
        /// on a worker thread when the source declares <see cref="IAssetRecordSource.IsThreadSafe"/>;
        /// falls back to synchronous execution otherwise so callers get uniform semantics.
        /// <paramref name="onFullScanStarted"/> fires on the main thread only when the
        /// persistent cache misses and record enumeration is about to begin.
        /// <paramref name="onComplete"/> fires on the main thread when the index is fully
        /// hydrated and can be read by consumers.
        /// </summary>
        public void SynchronizeAsync(Action onFullScanStarted, Action onComplete)
        {
            SynchronizeAsyncWithOutcome(
                onFullScanStarted,
                onComplete == null ? null : _ => onComplete());
        }

        public void SynchronizeAsyncWithOutcome(
            Action onFullScanStarted,
            Action<AssetIndexSynchronizationResult> onComplete)
        {
            var operation = BeginOperation(onFullScanStarted, onComplete);
            try
            {
                ZepPerfLog.Count(ZepPerfCounter.FullSynchronizerRuns);
                LastEnumerationStrategy = EnumerationStrategy.None;

                if (_enablePersistentCache
                    && !PersistentAssetIndexCache.HasTestOverrides)
                {
                    BeginSnapshotLoad(operation);
                    return;
                }

                using var hydrationMeasurement = ZepPerfLog.Measure(
                    "synchronize-async",
                    ZepPerfStage.PersistentCacheValidation);
                var __sw_hydrate = System.Diagnostics.Stopwatch.StartNew();
                var hydrated = TryHydrateFromCache(
                    operation,
                    replaceNonEmptyIndex: true,
                    out var artifactRevisionChanged,
                    out var tagStateChanged,
                    out var unresolved);
                __sw_hydrate.Stop();
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] SynchronizeAsync.TryHydrateFromCache hit={hydrated} indexCount={_index.Count} ms={__sw_hydrate.Elapsed.TotalMilliseconds:F2}");
                if (hydrated)
                {
                    CompleteHydratedOperation(
                        operation,
                        operation.ArtifactDependencyVersion,
                        artifactRevisionChanged,
                        tagStateChanged,
                        unresolved);
                    return;
                }

                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] cold-scan reason=no-snapshot pendingBatches={operation.PendingChanges.Count}");
                BeginFullScan(operation, onFullScanStarted: true);
            }
            catch (Exception ex)
            {
                FailOperation(operation, "Asynchronous synchronization setup", ex);
            }
        }

        private SynchronizationOperation BeginOperation(
            Action onFullScanStarted,
            Action<AssetIndexSynchronizationResult> onComplete)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(AssetIndexSynchronizer));
            }

            var pendingChanges =
                PersistentAssetIndexCache.CapturePendingChangesWithRecovery(
                    out var recoveryState);
            var unityLabelsByGuid = _enablePersistentCache
                ? PersistentAssetIndexCache.CaptureUnityLabelSnapshot()
                : null;
            var artifactDependencyVersion = _enablePersistentCache
                ? PersistentAssetIndexCache.CaptureArtifactDependencyVersion()
                : 0;
            var generation = Interlocked.Increment(
                ref _synchronizationOperationGeneration);
            // The bump above already stops an outstanding type-reconciliation job at its
            // next IsCurrent check. Clearing here rather than waiting for that tick means
            // the flag can never keep the panel deaf on behalf of a job that is dead.
            _backgroundJobOperation = null;
            var operation = new SynchronizationOperation(
                generation,
                unityLabelsByGuid,
                artifactDependencyVersion,
                pendingChanges,
                recoveryState,
                onFullScanStarted);
            if (onComplete != null)
            {
                _completionWaiters.Add(onComplete);
            }

            _activeOperation = operation;
            return operation;
        }

        private bool IsCurrent(SynchronizationOperation operation) =>
            !_isDisposed
            && operation != null
            && operation.Generation == Volatile.Read(
                ref _synchronizationOperationGeneration);

        private void CompleteOperation(SynchronizationOperation operation)
        {
            CompleteOperation(operation, AssetIndexSynchronizationResult.Success);
        }

        private void CompleteOperation(
            SynchronizationOperation operation,
            AssetIndexSynchronizationResult result)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            _activeOperation = null;
            var waiters = _completionWaiters.ToArray();
            _completionWaiters.Clear();
            for (var i = 0; i < waiters.Length; i++)
            {
                if (_isDisposed)
                {
                    return;
                }

                try
                {
                    waiters[i]?.Invoke(result);
                }
                catch (Exception ex)
                {
                    EditorHost.LogException(ex);
                }
            }
        }

        /// <summary>
        /// Retires the outstanding background job, if the one being ended is
        /// still the one that is outstanding. The reference check is written once here
        /// so no call site has to reason about ordering: a cancelled job's late exit
        /// finds a newer operation in the field and leaves it alone.
        /// </summary>
        private void EndBackgroundJob(SynchronizationOperation operation)
        {
            if (ReferenceEquals(_backgroundJobOperation, operation))
            {
                _backgroundJobOperation = null;
            }
        }

        private void FailOperation(
            SynchronizationOperation operation,
            string stage,
            Exception exception)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            var error = $"{stage} failed ({exception.GetType().Name}: {exception.Message})";
            EditorHost.LogWarning(
                $"[Zep] {error}; the current synchronization was retired and can be retried.");
            CompleteOperation(
                operation,
                AssetIndexSynchronizationResult.Failure(error));
        }

        private void AttachPoll(Action poll)
        {
            if (_isDisposed)
            {
                return;
            }

            _editorPolls.Add(poll);
            EditorHost.AttachUpdate(poll);
        }

        private void DetachPoll(Action poll)
        {
            EditorHost.DetachUpdate(poll);
            _editorPolls.Remove(poll);
        }

        // The reload path. The worker reads the snapshot; the main thread publishes
        // it, replays the journal and decides what — if anything — must follow.
        // Only a snapshot that cannot be read at all ends in the cold scan.
        private void BeginSnapshotLoad(SynchronizationOperation operation)
        {
            var task = Task.Run(() =>
            {
                var loaded = PersistentAssetIndexCache.TryLoad(
                    operation.ArtifactDependencyVersion,
                    operation.RecoveryState,
                    out var records,
                    out var folders,
                    out var artifactRevisionChanged,
                    out var tagStateChanged,
                    out var cacheSnapshotId);
                return new SnapshotLoadResult(
                    loaded,
                    artifactRevisionChanged,
                    tagStateChanged,
                    cacheSnapshotId,
                    records,
                    folders);
            });

            void Poll()
            {
                if (!task.IsCompleted)
                {
                    return;
                }

                DetachPoll(Poll);
                if (!IsCurrent(operation))
                {
                    return;
                }

                try
                {
                    if (task.IsFaulted)
                    {
                        var exception = task.Exception?.GetBaseException();
                        EditorHost.LogWarning(
                            $"[Zep] Index snapshot load failed ({exception?.GetType().Name}: {exception?.Message}); rebuilding asynchronously.");
                        if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] cold-scan reason=load-faulted pendingBatches={operation.PendingChanges.Count}");
                        BeginFullScan(operation, onFullScanStarted: true);
                        return;
                    }

                    var result = task.Result;
                    if (!result.Loaded)
                    {
                        if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] cold-scan reason=no-snapshot pendingBatches={operation.PendingChanges.Count} cacheFile={System.IO.File.Exists(PersistentAssetIndexCache.CacheFilePath)}");
                        BeginFullScan(operation, onFullScanStarted: true);
                        return;
                    }

                    AdoptJournalGrowth(operation);
                    if (!IsCurrent(operation))
                    {
                        return;
                    }

                    if (!PersistentAssetIndexCache.IsCacheSnapshotCurrent(
                        result.CacheSnapshotId))
                    {
                        // A save replaced the file while the worker was reading it;
                        // the rows are a torn account. Practically only two
                        // synchronizer instances in one process can do this.
                        if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] cold-scan reason=snapshot-replaced-during-load pendingBatches={operation.PendingChanges.Count}");
                        BeginFullScan(operation, onFullScanStarted: true);
                        return;
                    }

                    if (!TryApplyLoadedCacheSnapshot(
                        operation,
                        result.Records,
                        result.Folders,
                        operation.PendingChanges,
                        out var unresolved))
                    {
                        return;
                    }

                    CompleteHydratedOperation(
                        operation,
                        operation.ArtifactDependencyVersion,
                        result.ArtifactRevisionChanged,
                        result.TagStateChanged,
                        unresolved);
                }
                catch (Exception ex)
                {
                    FailOperation(operation, "Snapshot load completion", ex);
                }
            }

            AttachPoll(Poll);
        }

        private void BeginFullScan(
            SynchronizationOperation operation,
            bool onFullScanStarted)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            if (onFullScanStarted)
            {
                try
                {
                    operation.OnFullScanStarted?.Invoke();
                }
                catch (Exception ex)
                {
                    EditorHost.LogWarning(
                        $"[Zep] Full-scan notification failed ({ex.GetType().Name}: {ex.Message}); synchronization will continue.");
                }

                if (!IsCurrent(operation))
                {
                    return;
                }
            }

            // Authoritative main-object types are needed for a real rebuild, and the
            // native walk refuses to run without them.
            if (!_asyncSource.IsThreadSafe)
            {
                LastEnumerationStrategy = EnumerationStrategy.SyncFallback;
                BeginSynchronousRebuild(
                    operation,
                    _syncSource,
                    operation.UnityLabelsByGuid,
                    null,
                    operation.ArtifactDependencyVersion);
                return;
            }

            if (_enablePersistentCache
                && !PersistentAssetIndexCache.HasAuthoritativeTypeSnapshotOverride)
            {
                BeginBudgetedAuthoritativeTypeCapture(
                    operation,
                    operation.ArtifactDependencyVersion);
                return;
            }

            var typesByGuid = _enablePersistentCache
                ? PersistentAssetIndexCache.CaptureAuthoritativeTypeSnapshot()
                : null;
            BeginPreparedFullScan(
                operation,
                operation.UnityLabelsByGuid,
                typesByGuid,
                operation.ArtifactDependencyVersion);
        }

        private void BeginBudgetedAuthoritativeTypeCapture(
            SynchronizationOperation operation,
            uint artifactDependencyVersion,
            VerificationJob verification = null)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            string[] allAssetPaths;
            HashSet<string> scriptedImporterExtensions;
            try
            {
                allAssetPaths =
                    EditorHost.CaptureAllAssetPaths();
                scriptedImporterExtensions =
                    EditorHost.CaptureScriptedImporterExtensions();
            }
            catch (Exception ex)
            {
                if (verification != null)
                {
                    AbandonVerification(operation, "Type-candidate capture", ex);
                    return;
                }

                EditorHost.LogWarning(
                    $"[Zep] Type-candidate capture failed ({ex.GetType().Name}: {ex.Message}); falling back to authoritative synchronization.");
                LastEnumerationStrategy = EnumerationStrategy.SyncFallback;
                BeginSynchronousRebuild(
                    operation,
                    _syncSource,
                    operation.UnityLabelsByGuid,
                    null,
                    artifactDependencyVersion);
                return;
            }

            var candidateTask = Task.Run(() =>
                EditorHost.FilterAuthoritativeTypeCandidatePaths(
                    allAssetPaths,
                    scriptedImporterExtensions));
            List<string> candidates = null;
            var typesByGuid = new Dictionary<string, AssetTypeMetadata>(
                StringComparer.OrdinalIgnoreCase);
            var nextCandidate = 0;

            void Poll()
            {
                if (!IsCurrent(operation))
                {
                    DetachPoll(Poll);
                    return;
                }

                if (candidates == null)
                {
                    if (!candidateTask.IsCompleted)
                    {
                        return;
                    }

                    if (candidateTask.IsFaulted)
                    {
                        DetachPoll(Poll);
                        var exception = candidateTask.Exception?.GetBaseException();
                        if (verification != null)
                        {
                            AbandonVerification(operation, "Type-candidate filtering", exception);
                            return;
                        }

                        EditorHost.LogWarning(
                            $"[Zep] Type-candidate filtering failed ({exception?.GetType().Name}: {exception?.Message}); falling back to authoritative synchronization.");
                        LastEnumerationStrategy = EnumerationStrategy.SyncFallback;
                        BeginSynchronousRebuild(
                            operation,
                            _syncSource,
                            operation.UnityLabelsByGuid,
                            null,
                            artifactDependencyVersion);
                        return;
                    }

                    candidates = candidateTask.Result;
                }

                var budget = System.Diagnostics.Stopwatch.StartNew();
                var firstCandidateThisFrame = nextCandidate;
                try
                {
                    while (nextCandidate < candidates.Count
                        && (nextCandidate == firstCandidateThisFrame
                            || budget.Elapsed.TotalMilliseconds
                                < MainThreadWorkBudgetMilliseconds))
                    {
                        EditorHost.AddAuthoritativeTypeMetadata(
                            candidates[nextCandidate],
                            typesByGuid);
                        nextCandidate++;
                    }
                }
                catch (Exception ex)
                {
                    DetachPoll(Poll);
                    if (verification != null)
                    {
                        AbandonVerification(operation, "Budgeted type capture", ex);
                        return;
                    }

                    EditorHost.LogWarning(
                        $"[Zep] Budgeted type capture failed ({ex.GetType().Name}: {ex.Message}); falling back to authoritative synchronization.");
                    LastEnumerationStrategy = EnumerationStrategy.SyncFallback;
                    BeginSynchronousRebuild(
                        operation,
                        _syncSource,
                        operation.UnityLabelsByGuid,
                        null,
                        artifactDependencyVersion);
                    return;
                }

                if (nextCandidate < candidates.Count)
                {
                    return;
                }

                DetachPoll(Poll);
                BeginPreparedFullScan(
                    operation,
                    operation.UnityLabelsByGuid,
                    typesByGuid,
                    artifactDependencyVersion,
                    verification);
            }

            AttachPoll(Poll);
        }

        private void BeginPreparedFullScan(
            SynchronizationOperation operation,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid,
            uint artifactDependencyVersion,
            VerificationJob verification = null)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            BeginWhenSourceScanAvailable(
                operation,
                () => StartPreparedFullScan(
                    operation,
                    unityLabelsByGuid,
                    typesByGuid,
                    artifactDependencyVersion,
                    verification));
        }

        // The worker walk, in two shapes. Authoritative (the cold path): the walk
        // replaces the index and is saved. Verification: the walk is diffed against
        // the index the loaded snapshot already populated, on the worker, and only
        // the difference is applied — the index is never cleared, no overlay is
        // raised, and a fault ends the job instead of falling back to the main
        // thread.
        private void StartPreparedFullScan(
            SynchronizationOperation operation,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid,
            uint artifactDependencyVersion,
            VerificationJob verification)
        {
            if (!IsCurrent(operation))
            {
                SourceScanGate.Release();
                return;
            }

            // PrepareForEnumeration MUST happen on the main thread - it touches
            // ScriptableSingletons. The enumeration itself can then be dispatched to a
            // worker when the source supports it.
            try
            {
                _asyncSource.PrepareForEnumeration(unityLabelsByGuid, typesByGuid);
            }
            catch (Exception ex)
            {
                if (verification != null)
                {
                    SourceScanGate.Release();
                    AbandonVerification(operation, "PrepareForEnumeration", ex);
                    return;
                }

                EditorHost.LogWarning($"[Zep] PrepareForEnumeration threw ({ex.GetType().Name}: {ex.Message}); falling back to sync.");
                LastEnumerationStrategy = EnumerationStrategy.SyncFallback;
                ExecuteSynchronousRebuildOwningGate(
                    operation,
                    _syncSource,
                    unityLabelsByGuid,
                    typesByGuid,
                    artifactDependencyVersion,
                    "Synchronous fallback");
                return;
            }

            var scanGeneration = Volatile.Read(ref _assetChangeGeneration);
            var scanProofEpoch = Volatile.Read(ref _authoritativeProofEpoch);
            var overlay = CaptureScanOverlay(scanGeneration);
            // The verification diff runs on the worker against a copy of the live
            // index taken now: the record graph is immutable, so the copy is a list
            // of references, and the diff's O(records) probes never touch the main
            // thread. Changes applied to the live index after this point are
            // reconciled with the diff when it is applied.
            var indexRecords = verification != null
                ? new List<AssetRecord>(_index.Enumerate())
                : null;
            var indexFolders = verification != null
                ? new List<AssetFolderRecord>(_index.EnumerateFolders())
                : null;
            var task = Task.Run(() =>
            {
                try
                {
                    ZepPerfLog.Count(ZepPerfCounter.FullRecordEnumerations);
                    var records = new List<AssetRecord>();
                    foreach (var record in _asyncSource.GetRecords())
                    {
                        if (record != null)
                        {
                            records.Add(record);
                        }
                    }

                    var folders = new List<AssetFolderRecord>(
                        _asyncSource.GetFolderRecords());
                    ApplyScanOverlay(
                        records,
                        folders,
                        overlay,
                        authoritativeSnapshot: true);
                    var diff = verification != null
                        ? ComputeVerificationDiff(indexRecords, indexFolders, records, folders)
                        : null;
                    return new WorkerEnumerationResult(records, folders, diff);
                }
                finally
                {
                    // A scan cannot be cancelled safely once the source walk starts.
                    // Releasing here, after both snapshots are immutable, lets a new
                    // controller proceed even when this instance has been disposed and
                    // its main-thread poll was detached.
                    SourceScanGate.Release();
                }
            });

            void Poll()
            {
                if (!task.IsCompleted)
                {
                    return;
                }

                DetachPoll(Poll);
                if (!IsCurrent(operation))
                {
                    return;
                }

                try
                {
                    if (task.IsFaulted)
                    {
                        var baseEx = task.Exception?.GetBaseException();
                        if (verification != null)
                        {
                            AbandonVerification(operation, "Verification walk", baseEx);
                            return;
                        }

                        EditorHost.LogWarning($"[Zep] Async synchronize failed ({baseEx?.GetType().Name}: {baseEx?.Message}); falling back to sync.");
                        LastEnumerationStrategy = EnumerationStrategy.SyncFallback;
                        BeginSynchronousRebuild(
                            operation,
                            _syncSource,
                            unityLabelsByGuid,
                            typesByGuid,
                            artifactDependencyVersion);
                        return;
                    }

                    if (verification != null)
                    {
                        LastEnumerationStrategy = EnumerationStrategy.Async;
                        ApplyVerificationResult(
                            operation,
                            task.Result.Diff,
                            scanGeneration,
                            scanProofEpoch,
                            artifactDependencyVersion,
                            verification);
                        return;
                    }

                    if (scanGeneration != Volatile.Read(ref _assetChangeGeneration))
                    {
                        // Never publish a stale worker snapshot and never recover by
                        // blocking the editor thread. Discard it and start another
                        // generation; the original completion fires only after a
                        // current background snapshot is adopted.
                        LastEnumerationStrategy = EnumerationStrategy.Async;
                        BeginPreparedFullScan(
                            operation,
                            unityLabelsByGuid,
                            typesByGuid,
                            artifactDependencyVersion);
                        return;
                    }

                    LastEnumerationStrategy = EnumerationStrategy.Async;
                    ApplyRecords(
                        operation,
                        task.Result.Records,
                        task.Result.Folders,
                        artifactDependencyVersion,
                        persistSnapshot: false);
                    if (!IsCurrent(operation))
                    {
                        return;
                    }

                    ClearAppliedScanOverlay(overlay.Generation);
                    if (!_enablePersistentCache)
                    {
                        CompleteOperation(operation);
                        return;
                    }

                    BeginStagedSnapshotSave(
                        operation,
                        task.Result.Records,
                        task.Result.Folders,
                        artifactDependencyVersion,
                        () => IsCurrent(operation)
                            && scanGeneration == Volatile.Read(
                                ref _assetChangeGeneration),
                        authoritative: true);
                }
                catch (Exception ex)
                {
                    FailOperation(operation, "Asynchronous synchronization completion", ex);
                }
            }

            AttachPoll(Poll);
        }

        private void BeginSynchronousRebuild(
            SynchronizationOperation operation,
            IAssetRecordSource source,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid,
            uint artifactDependencyVersion)
        {
            BeginWhenSourceScanAvailable(operation, () =>
                ExecuteSynchronousRebuildOwningGate(
                    operation,
                    source,
                    unityLabelsByGuid,
                    typesByGuid,
                    artifactDependencyVersion,
                    "Synchronous synchronization"));
        }

        private void ExecuteSynchronousRebuildOwningGate(
            SynchronizationOperation operation,
            IAssetRecordSource source,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid,
            uint artifactDependencyVersion,
            string stage)
        {
            Exception failure = null;
            try
            {
                RebuildSynchronously(
                    operation,
                    source,
                    unityLabelsByGuid,
                    typesByGuid,
                    artifactDependencyVersion);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SourceScanGate.Release();
            }

            if (failure != null)
            {
                FailOperation(operation, stage, failure);
                return;
            }

            CompleteOperation(operation);
        }

        private void BeginWhenSourceScanAvailable(
            SynchronizationOperation operation,
            Action start)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            if (SourceScanGate.Wait(0))
            {
                if (!IsCurrent(operation))
                {
                    SourceScanGate.Release();
                    return;
                }

                start();
                return;
            }

            void Poll()
            {
                if (!IsCurrent(operation))
                {
                    DetachPoll(Poll);
                    return;
                }

                if (!SourceScanGate.Wait(0))
                {
                    return;
                }

                DetachPoll(Poll);
                if (!IsCurrent(operation))
                {
                    SourceScanGate.Release();
                    return;
                }

                start();
            }

            AttachPoll(Poll);
        }

        public void InvalidateInProgressScan()
        {
            if (_isDisposed)
            {
                return;
            }

            Interlocked.Increment(ref _assetChangeGeneration);
        }

        public void MarkPersistentStateDirty()
        {
            if (!_isDisposed && _enablePersistentCache)
            {
                PersistentAssetIndexCache.MarkDirtyForPendingChanges();
            }
        }

        public void RecordConcreteChanges(ProjectAssetChanges changes)
        {
            if (_isDisposed)
            {
                return;
            }

            changes = changes.Normalize();
            if (!changes.IsEmpty && _unresolvedProofEpoch != 0)
            {
                // The live row may still be the one the source failed to rebuild.
                // Until a walk settles that uncertainty, no later concrete batch
                // can certify an overlay: repeat the proof after this batch instead.
                ClearScanOverlay();
                _unresolvedProofEpoch = Interlocked.Increment(ref _authoritativeProofEpoch);
                Interlocked.Increment(ref _assetChangeGeneration);
                return;
            }

            for (var i = 0; i < changes.Deleted.Length; i++)
            {
                RecordRemovedRoot(changes.Deleted[i]);
            }

            for (var i = 0; i < changes.MovedFromPaths.Length; i++)
            {
                RecordRemovedRoot(changes.MovedFromPaths[i]);
            }

            for (var i = 0; i < changes.Imported.Length; i++)
            {
                RecordCurrentPath(changes.Imported[i]);
            }

            for (var i = 0; i < changes.Moved.Length; i++)
            {
                RecordCurrentPath(changes.Moved[i]);
            }
        }

        public void RecordUnresolvedChanges(IReadOnlyList<string> paths)
        {
            if (_isDisposed || paths == null)
            {
                return;
            }

            for (var i = 0; i < paths.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(paths[i]))
                {
                    ClearScanOverlay();
                    _unresolvedProofEpoch = Interlocked.Increment(ref _authoritativeProofEpoch);
                    Interlocked.Increment(ref _assetChangeGeneration);
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _activeOperation = null;
            _backgroundJobOperation = null;
            Interlocked.Increment(ref _synchronizationOperationGeneration);
            var polls = _editorPolls.ToArray();
            _editorPolls.Clear();
            for (var i = 0; i < polls.Length; i++)
            {
                EditorHost.DetachUpdate(polls[i]);
            }

            _completionWaiters.Clear();

            // A staged snapshot write still on the worker owns the cache's journal write
            // marker, and its completion poll has just been detached. Dropping it here
            // would leave that marker held for the rest of the process and refuse every
            // later save: the generation moved above, so this commit can only take its
            // contended leg and can never publish a half-written snapshot.
            FlushPendingSnapshotSave();
        }

        /// <summary>
        /// Waits for a staged snapshot write and resolves it now instead of on the next
        /// editor update. Disposal needs it — the worker owns the cache's journal write
        /// marker while its completion poll is gone — and a test that must observe the
        /// committed snapshot inside one editor frame needs it for the same reason.
        /// </summary>
        internal void FlushPendingSnapshotSave()
        {
            var pending = _pendingSnapshotSave;
            if (pending == null)
            {
                return;
            }

            try
            {
                pending.Task.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                // Reported by ResolvePendingSnapshotSave, which reads Task.IsFaulted.
            }

            ResolvePendingSnapshotSave();
        }

        private void RecordRemovedRoot(string path)
        {
            path = AssetPathFormat.Normalize(path);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            AddRemovedRoot(path);
            if (_scanOverlayExpectedCurrentRoots.Count > 0)
            {
                _scanOverlayExpectedCurrentRoots.RemoveWhere(current =>
                    IsAtOrBelow(current, path));
            }
            RemoveUpsertsAtOrBelow(_scanOverlayRecordUpserts, path);
            RemoveUpsertsAtOrBelow(_scanOverlayFolderUpserts, path);
            DropScanOverlayIfUnbounded();
        }

        private void AddRemovedRoot(string path)
        {
            // Removed roots are only ever consulted through IsAtOrBelow, so a root
            // that another root already covers is pure redundancy: keeping only the
            // maximal roots preserves the removal closure exactly while collapsing a
            // delete/recreate churn loop (and a folder delete reported per member)
            // into a single entry.
            //
            // Both directions of that rule used to be one reverse scan of every root,
            // i.e. O(roots) per deleted path with roots bounded at 1,024. The "already
            // covered" direction is an ancestor walk against the set. The "this path
            // covers existing roots" direction cannot be a set probe, but it cannot
            // happen at all unless the new path is SHALLOWER than some existing root —
            // a proper descendant has strictly more '/'-delimited segments — so the
            // scan is skipped whenever the new path is at least as deep as the deepest
            // root. Sibling deletes, the batch shape that makes this loop hot, never
            // take it. The kept roots are still exactly the maximal ones: if the new
            // path is covered, no root can be below it either, because the roots are an
            // antichain and a root below the new path would also be below its coverer.
            if (IsAtOrBelowAnyRoot(path, _scanOverlayRemovedRootSet, null))
            {
                return;
            }

            var segmentCount = CountPathSegments(path);
            if (segmentCount < _deepestRemovedRootSegmentCount)
            {
                for (var i = _scanOverlayRemovedRoots.Count - 1; i >= 0; i--)
                {
                    var existing = _scanOverlayRemovedRoots[i];
                    if (IsAtOrBelow(existing, path))
                    {
                        _scanOverlayRemovedRoots.RemoveAt(i);
                        _scanOverlayRemovedRootSet.Remove(existing);
                    }
                }

                _deepestRemovedRootSegmentCount = 0;
                for (var i = 0; i < _scanOverlayRemovedRoots.Count; i++)
                {
                    var depth = CountPathSegments(_scanOverlayRemovedRoots[i]);
                    if (depth > _deepestRemovedRootSegmentCount)
                    {
                        _deepestRemovedRootSegmentCount = depth;
                    }
                }
            }

            _scanOverlayRemovedRoots.Add(path);
            _scanOverlayRemovedRootSet.Add(path);
            if (segmentCount > _deepestRemovedRootSegmentCount)
            {
                _deepestRemovedRootSegmentCount = segmentCount;
            }
        }

        private void RecordCurrentPath(string path)
        {
            path = AssetPathFormat.Normalize(path);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // A current path wins over an earlier ancestor deletion. Keep the
            // authoritative scan's required parent chain as well as the concrete
            // upsert below; otherwise a resolved child can survive publication while
            // its parent folder is removed by the same overlay.
            _scanOverlayExpectedCurrentRoots.Add(path);

            if (_index.TryGetByPath(path, out var record))
            {
                _scanOverlayRecordUpserts[path] = record;
            }
            else if (_index.TryGetFolder(path, out var folder))
            {
                _scanOverlayFolderUpserts[path] = folder;
            }

            DropScanOverlayIfUnbounded();
        }

        // Same rule the durable journal applies when its history outgrows its bound
        // (cc9c816): the overlay is dropped whole and the next walk is trusted on its
        // own, never truncated into a history that claims to be complete and is not.
        // Dropping it is fail-closed because the concrete paths are also in the
        // durable journal (ZepAssetPostprocessor records them before it
        // raises Changed), and bumping the change generation retires any scan already
        // captured against the dropped overlay so it cannot publish.
        private void DropScanOverlayIfUnbounded()
        {
            if (ScanOverlayEntryCount <= MaxScanOverlayEntryCount)
            {
                return;
            }

            ClearScanOverlay();
            Interlocked.Increment(ref _authoritativeProofEpoch);
            Interlocked.Increment(ref _assetChangeGeneration);
        }

        private int ScanOverlayEntryCount =>
            _scanOverlayRemovedRoots.Count
            + _scanOverlayExpectedCurrentRoots.Count
            + _scanOverlayRecordUpserts.Count
            + _scanOverlayFolderUpserts.Count;

        internal int ScanOverlayEntryCountForTests => ScanOverlayEntryCount;

        internal int AssetChangeGenerationForTests =>
            Volatile.Read(ref _assetChangeGeneration);

        private ScanOverlay CaptureScanOverlay(int generation)
        {
            var expectedCurrentRoots =
                new string[_scanOverlayExpectedCurrentRoots.Count];
            _scanOverlayExpectedCurrentRoots.CopyTo(expectedCurrentRoots);
            return new ScanOverlay(
                generation,
                _scanOverlayRemovedRoots.ToArray(),
                expectedCurrentRoots,
                new List<AssetRecord>(_scanOverlayRecordUpserts.Values),
                new List<AssetFolderRecord>(_scanOverlayFolderUpserts.Values));
        }

        private void ClearAppliedScanOverlay(int generation)
        {
            if (generation != Volatile.Read(ref _assetChangeGeneration))
            {
                return;
            }

            ClearScanOverlay();
        }

        private void ClearScanOverlay()
        {
            _scanOverlayRemovedRoots.Clear();
            _scanOverlayRemovedRootSet.Clear();
            _deepestRemovedRootSegmentCount = 0;
            _scanOverlayExpectedCurrentRoots.Clear();
            _scanOverlayRecordUpserts.Clear();
            _scanOverlayFolderUpserts.Clear();
        }

        private static void ApplyScanOverlay(
            List<AssetRecord> records,
            List<AssetFolderRecord> folders,
            ScanOverlay overlay,
            bool authoritativeSnapshot,
            IReadOnlyList<string> replayMoveSources = null)
        {
            if (overlay == null)
            {
                return;
            }

            var expectedCurrentRecords = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (authoritativeSnapshot)
            {
                for (var i = 0; i < records.Count; i++)
                {
                    expectedCurrentRecords.Add(records[i].AssetPath);
                }
            }

            for (var i = 0; i < overlay.RecordUpserts.Count; i++)
            {
                expectedCurrentRecords.Add(overlay.RecordUpserts[i].AssetPath);
            }

            var expectedCurrentFolders = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (authoritativeSnapshot)
            {
                for (var i = 0; i < folders.Count; i++)
                {
                    expectedCurrentFolders.Add(folders[i].FolderPath.Value);
                }
            }

            for (var i = 0; i < overlay.FolderUpserts.Count; i++)
            {
                expectedCurrentFolders.Add(
                    overlay.FolderUpserts[i].FolderPath.Value);
            }

            // Both survival questions below are asked once per record and once per
            // folder, against root lists the overlay bounds at MaxScanOverlayEntryCount.
            // Answering them by scanning those lists was O(records x roots) — 500k x
            // 1,024 IsAtOrBelow calls at the spec's target size. Reshape the roots once,
            // here, so each question becomes a set probe plus a walk of the candidate
            // path's own (short) ancestor chain.
            var removedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removedRootLengths = new HashSet<int>();
            for (var i = 0; i < overlay.RemovedRoots.Count; i++)
            {
                var removedRoot = overlay.RemovedRoots[i];
                if (string.IsNullOrEmpty(removedRoot))
                {
                    continue;
                }

                removedRoots.Add(removedRoot);
                removedRootLengths.Add(removedRoot.Length);
            }

            // An expected-current root protects (a) every ANCESTOR of itself, which is
            // the closure collected here, and (b) when it is a folder in an
            // authoritative snapshot, everything BELOW itself, which stays an ancestor
            // walk. Only a root the overlay actually resolved to a record or a folder
            // is eligible, exactly as the per-path form checked.
            var expectedCurrentAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedCurrentFolderRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedCurrentFolderRootLengths = new HashSet<int>();
            for (var i = 0; i < overlay.ExpectedCurrentRoots.Count; i++)
            {
                var expected = overlay.ExpectedCurrentRoots[i];
                if (string.IsNullOrEmpty(expected))
                {
                    continue;
                }

                var isRecord = expectedCurrentRecords.Contains(expected);
                var isFolder = !isRecord && expectedCurrentFolders.Contains(expected);
                if (!isRecord && !isFolder)
                {
                    continue;
                }

                AddSelfAndAncestors(expected, expectedCurrentAncestors);
                if (isFolder)
                {
                    expectedCurrentFolderRoots.Add(expected);
                    expectedCurrentFolderRootLengths.Add(expected.Length);
                }
            }

            records.RemoveAll(record => IsAtOrBelowAnyRoot(
                record.AssetPath,
                removedRoots,
                removedRootLengths)
                && !IsAtOrBelowAny(record.AssetPath, replayMoveSources)
                && !IsProtectedByExpectedCurrentPath(
                    record.AssetPath,
                    expectedCurrentAncestors,
                    expectedCurrentFolderRoots,
                    expectedCurrentFolderRootLengths,
                    authoritativeSnapshot));
            folders.RemoveAll(folder => IsAtOrBelowAnyRoot(
                folder.FolderPath.Value,
                removedRoots,
                removedRootLengths)
                && !IsAtOrBelowAny(
                    folder.FolderPath.Value,
                    replayMoveSources)
                && !IsProtectedByExpectedCurrentPath(
                    folder.FolderPath.Value,
                    expectedCurrentAncestors,
                    expectedCurrentFolderRoots,
                    expectedCurrentFolderRootLengths,
                    authoritativeSnapshot));

            var recordIndexByPath = new Dictionary<string, int>(
                records.Count,
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < records.Count; i++)
            {
                recordIndexByPath[records[i].AssetPath] = i;
            }

            for (var i = 0; i < overlay.RecordUpserts.Count; i++)
            {
                var record = overlay.RecordUpserts[i];
                if (recordIndexByPath.TryGetValue(record.AssetPath, out var index))
                {
                    records[index] = record;
                }
                else
                {
                    recordIndexByPath[record.AssetPath] = records.Count;
                    records.Add(record);
                }
            }

            var folderIndexByPath = new Dictionary<string, int>(
                folders.Count,
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < folders.Count; i++)
            {
                folderIndexByPath[folders[i].FolderPath.Value] = i;
            }

            for (var i = 0; i < overlay.FolderUpserts.Count; i++)
            {
                var folder = overlay.FolderUpserts[i];
                var path = folder.FolderPath.Value;
                if (folderIndexByPath.TryGetValue(path, out var index))
                {
                    folders[index] = folder;
                }
                else
                {
                    folderIndexByPath[path] = folders.Count;
                    folders.Add(folder);
                }
            }
        }

        // Test seam. ApplyScanOverlay's survival rule is the loop the ancestor walk
        // replaced, and it is otherwise reachable only by driving a full asynchronous
        // synchronization — which cannot express 1,000 removed roots over 20,000
        // records. Exposing the decision by value lets a test compare it against a
        // brute-force reference of the semantics it is meant to preserve.
        internal static void ApplyScanOverlayForTests(
            List<AssetRecord> records,
            List<AssetFolderRecord> folders,
            IReadOnlyList<string> removedRoots,
            IReadOnlyList<string> expectedCurrentRoots,
            IReadOnlyList<AssetRecord> recordUpserts,
            IReadOnlyList<AssetFolderRecord> folderUpserts,
            bool authoritativeSnapshot)
        {
            ApplyScanOverlay(
                records,
                folders,
                new ScanOverlay(
                    0,
                    removedRoots,
                    expectedCurrentRoots,
                    recordUpserts,
                    folderUpserts),
                authoritativeSnapshot);
        }

        private static bool IsRemovedByOverlay(
            string path,
            IReadOnlyList<string> removedRoots)
        {
            for (var i = 0; i < removedRoots.Count; i++)
            {
                if (IsAtOrBelow(path, removedRoots[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAncestorOfAny(
            string path,
            IReadOnlyList<string> descendants)
        {
            if (string.IsNullOrEmpty(path) || descendants == null)
            {
                return false;
            }

            for (var i = 0; i < descendants.Count; i++)
            {
                if (IsAtOrBelow(descendants[i], path))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAtOrBelowAny(
            string path,
            IReadOnlyList<string> roots)
        {
            if (roots == null)
            {
                return false;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                if (IsAtOrBelow(path, roots[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsProtectedByExpectedCurrentPath(
            string path,
            HashSet<string> expectedCurrentAncestors,
            HashSet<string> expectedCurrentFolderRoots,
            HashSet<int> expectedCurrentFolderRootLengths,
            bool authoritativeSnapshot)
        {
            // "Some eligible expected-current root is at or below this path" is the same
            // statement as "this path is that root or one of its ancestors", which the
            // precomputed closure answers in one probe.
            if (expectedCurrentAncestors.Contains(path))
            {
                return true;
            }

            return authoritativeSnapshot
                && IsAtOrBelowAnyRoot(
                    path,
                    expectedCurrentFolderRoots,
                    expectedCurrentFolderRootLengths);
        }

        // A path is at or below a root exactly when the root is the path itself or one
        // of the path's '/'-delimited prefixes, so walking that (short) prefix chain
        // answers the same question IsAtOrBelow answers against every root — O(depth)
        // instead of O(roots). rootLengths, when supplied, keeps the walk
        // allocation-free in the common case: a prefix can only equal a root when their
        // lengths match, so the Substring is paid only for a prefix that could be one.
        private static bool IsAtOrBelowAnyRoot(
            string path,
            HashSet<string> roots,
            HashSet<int> rootLengths)
        {
            if (roots.Count == 0 || string.IsNullOrEmpty(path))
            {
                return false;
            }

            if ((rootLengths == null || rootLengths.Contains(path.Length))
                && roots.Contains(path))
            {
                return true;
            }

            for (var slash = path.LastIndexOf('/');
                slash > 0;
                slash = path.LastIndexOf('/', slash - 1))
            {
                if ((rootLengths == null || rootLengths.Contains(slash))
                    && roots.Contains(path.Substring(0, slash)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddSelfAndAncestors(string path, HashSet<string> destination)
        {
            destination.Add(path);
            for (var slash = path.LastIndexOf('/');
                slash > 0;
                slash = path.LastIndexOf('/', slash - 1))
            {
                // Chains are always added whole, so meeting one that is already present
                // means every shorter prefix is present too.
                if (!destination.Add(path.Substring(0, slash)))
                {
                    return;
                }
            }
        }

        private static int CountPathSegments(string path)
        {
            var segments = 1;
            for (var i = 0; i < path.Length; i++)
            {
                if (path[i] == '/')
                {
                    segments++;
                }
            }

            return segments;
        }

        private static bool IsAtOrBelow(string path, string root)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || (path.Length > root.Length
                    && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && path[root.Length] == '/');
        }

        private static void RemoveUpsertsAtOrBelow<T>(
            Dictionary<string, T> upserts,
            string root)
        {
            // Called twice per removed path. A delete-only batch never fills these
            // maps, so without this the common case copies two empty key sets per
            // deleted file (4.11).
            if (upserts.Count == 0)
            {
                return;
            }

            var paths = new List<string>(upserts.Keys);
            for (var i = 0; i < paths.Count; i++)
            {
                if (IsAtOrBelow(paths[i], root))
                {
                    upserts.Remove(paths[i]);
                }
            }
        }

        private void RebuildSynchronously(
            SynchronizationOperation operation,
            IAssetRecordSource source,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid,
            uint artifactDependencyVersion)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            source.PrepareForEnumeration(unityLabelsByGuid, typesByGuid);
            ZepPerfLog.Count(ZepPerfCounter.FullRecordEnumerations);
            var records = new List<AssetRecord>();
            foreach (var record in source.GetRecords())
            {
                if (record != null)
                {
                    records.Add(record);
                }
            }

            if (!IsCurrent(operation))
            {
                return;
            }

            ApplyRecords(
                operation,
                records,
                artifactDependencyVersion,
                source);
        }

        private bool TryHydrateFromCache(
            SynchronizationOperation operation,
            bool replaceNonEmptyIndex,
            out bool artifactRevisionChanged,
            out bool tagStateChanged,
            out List<string> unresolved)
        {
            artifactRevisionChanged = false;
            tagStateChanged = false;
            unresolved = null;
            if (!_enablePersistentCache
                || !IsCurrent(operation)
                || (!replaceNonEmptyIndex && _index.Count != 0))
            {
                return false;
            }

            bool loaded;
            List<AssetRecord> cached;
            List<AssetFolderRecord> cachedFolders;
            string cacheSnapshotId;
            var __sw_load = System.Diagnostics.Stopwatch.StartNew();
            using (ZepPerfLog.Measure(
                "persistent-cache",
                ZepPerfStage.PersistentCacheHydration))
            {
                loaded = PersistentAssetIndexCache.TryLoad(
                    operation.ArtifactDependencyVersion,
                    operation.RecoveryState,
                    out cached,
                    out cachedFolders,
                    out artifactRevisionChanged,
                    out tagStateChanged,
                    out cacheSnapshotId);
            }
            __sw_load.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]   PersistentAssetIndexCache.TryLoad ok={loaded} count={(cached?.Count ?? 0)} folders={(cachedFolders?.Count ?? 0)} ms={__sw_load.Elapsed.TotalMilliseconds:F2}");
            if (!loaded || !IsCurrent(operation))
            {
                return false;
            }

            AdoptJournalGrowth(operation);
            if (!IsCurrent(operation)
                || !PersistentAssetIndexCache.IsCacheSnapshotCurrent(cacheSnapshotId))
            {
                return false;
            }

            return TryApplyLoadedCacheSnapshot(
                operation,
                cached,
                cachedFolders,
                operation.PendingChanges,
                out unresolved);
        }

        // Unity's post-compile import pass of a NEW script lands as a further
        // postprocessor batch a few frames into the reload — on a real project,
        // while the snapshot is still being read on the worker. The journal then
        // reads newer than the state captured at BeginOperation. That is no reason
        // to distrust the snapshot: the journal is still a complete account of
        // everything after it, one batch longer. Runs on the main thread, where no
        // callback can append concurrently, re-captures the list and the state and
        // hands both to the operation, so the replay and the later save agree on
        // what they cover. A journal that LOST batches meanwhile — another
        // instance's save retired them under this load — is the one shape the
        // longer-journal rule cannot cover, and is handed on as a verification.
        private void AdoptJournalGrowth(SynchronizationOperation operation)
        {
            if (!_enablePersistentCache)
            {
                return;
            }

            var captured = operation.RecoveryState;
            var fresh = PersistentAssetIndexCache.CapturePendingChangesWithRecovery(
                out var freshState);
            if (freshState.JournalGeneration == captured.JournalGeneration
                && freshState.RequiresVerification == captured.RequiresVerification
                && string.Equals(
                    freshState.DirtyToken,
                    captured.DirtyToken,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (freshState.JournalGeneration < captured.JournalGeneration)
            {
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal retired during load: {captured.JournalGeneration} -> {freshState.JournalGeneration}; the loaded snapshot will be verified");
                operation.AdoptJournal(
                    operation.PendingChanges,
                    new PersistentAssetIndexCache.RecoveryState(
                        freshState.JournalGeneration,
                        freshState.AuthoritativeFallbackToken,
                        freshState.DirtyToken,
                        freshState.AssignmentTimestamp,
                        freshState.DefinitionTimestamp,
                        requiresVerification: true,
                        freshState.HasMalformedJournalEvidence));
                return;
            }

            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal grew during load: {captured.JournalGeneration} -> {freshState.JournalGeneration}, batches={fresh.Count}, requiresVerification={freshState.RequiresVerification}; replaying the longer journal");
            operation.AdoptJournal(fresh, freshState);
        }

        /// <summary>
        /// Publishes a loaded snapshot: overlay, hydrate, replay. Shared by the
        /// synchronous path above and by the background load's completion, which
        /// differ only in how they obtain the snapshot — so the recovery tests that
        /// drive the synchronous path exercise the same publish sequence the
        /// production worker runs. <paramref name="unresolved"/> lists the journal
        /// entries the replay could not resolve; false means the operation was
        /// superseded before the snapshot was published.
        /// </summary>
        private bool TryApplyLoadedCacheSnapshot(
            SynchronizationOperation operation,
            List<AssetRecord> records,
            List<AssetFolderRecord> folderRecords,
            IReadOnlyList<ProjectAssetChanges> pendingChanges,
            out List<string> unresolved)
        {
            unresolved = null;
            ApplyRecordedChangesToSnapshot(
                operation,
                records,
                folderRecords,
                pendingChanges);
            ApplyHydratedCache(operation, records, folderRecords);
            if (!IsCurrent(operation))
            {
                return false;
            }

            unresolved = ApplyPendingChanges(operation, pendingChanges);
            return unresolved != null;
        }

        /// <summary>
        /// The tail every hydrated operation shares. The snapshot is published and
        /// the journal replayed by now; what follows depends on what the load could
        /// not vouch for. Uncertainty — a journal that is not a complete account, a
        /// tag state that moved since the snapshot, a replay entry the source could
        /// not build, or the first hydration of this editor process — completes the
        /// operation against the hydrated index and starts the silent verification
        /// walk. A moved artifact revision (a compile) with nothing uncertain
        /// completes here and hands the type reconciliation to its follow-up job,
        /// which owns the one snapshot save. Otherwise the patched snapshot is saved.
        ///
        /// The order matters and is not interchangeable: saving here after a
        /// compile would stamp the NEW artifact revision onto a snapshot still
        /// carrying pre-compile types, the next load would report an unchanged
        /// revision, and the retype would never run again.
        /// </summary>
        private void CompleteHydratedOperation(
            SynchronizationOperation operation,
            uint artifactDependencyVersion,
            bool artifactRevisionChanged,
            bool tagStateChanged,
            List<string> unresolved)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            var reason = DescribeVerificationReason(operation, tagStateChanged, unresolved);
            if (reason != null)
            {
                CompleteOperation(operation);
                BeginVerification(
                    operation,
                    artifactDependencyVersion,
                    artifactRevisionChanged,
                    reason);
                return;
            }

            if (!artifactRevisionChanged)
            {
                SaveAndCompleteHydratedOperation(
                    operation,
                    artifactDependencyVersion);
                return;
            }

            // Script compilation moves Unity's artifact revision even when the project
            // structure is unchanged, and it can only change the TYPE of records the
            // snapshot already holds. Waiters fire now, against the hydrated index;
            // the types follow, patched slice by slice.
            CompleteOperation(operation);
            BeginTypeReconciliation(
                operation,
                artifactDependencyVersion);
        }

        // Null when the loaded snapshot plus the replayed journal are a complete
        // account and nothing else asks for a walk.
        private string DescribeVerificationReason(
            SynchronizationOperation operation,
            bool tagStateChanged,
            List<string> unresolved)
        {
            if (!_enablePersistentCache)
            {
                return null;
            }

            var state = operation.RecoveryState;
            if (_unresolvedProofEpoch != 0)
            {
                return "unresolved-concrete-changes";
            }

            if (state.RequiresVerification)
            {
                return "journal-untrusted"
                    + " journalGen=" + state.JournalGeneration
                    + " dirty=" + (state.DirtyToken ?? "null")
                    + " fallback=" + (state.AuthoritativeFallbackToken ?? "null")
                    + " malformed=" + state.HasMalformedJournalEvidence;
            }

            if (tagStateChanged)
            {
                return "tag-state-moved";
            }

            if (unresolved != null && unresolved.Count > 0)
            {
                return "unresolved-replay count=" + unresolved.Count
                    + " first=" + unresolved[0];
            }

            if (!VerifiedThisProcess)
            {
                return "first-hydration-of-process";
            }

            return null;
        }

        private void SaveAndCompleteHydratedOperation(
            SynchronizationOperation operation,
            uint artifactDependencyVersion)
        {
            if (!NeedsPatchedSnapshotSave(operation))
            {
                CompleteOperation(operation);
                return;
            }

            BeginStagedSnapshotSave(
                operation,
                new List<AssetRecord>(_index.Enumerate()),
                new List<AssetFolderRecord>(_index.EnumerateFolders()),
                artifactDependencyVersion,
                () => IsCurrent(operation));
        }

        // Once per editor process the first hydration verifies regardless of what
        // the journal says: insurance for anything that changed while the editor
        // was closed and that Unity did not report as an asset change. SessionState
        // survives domain reloads and is cleared when the editor quits; an
        // authoritative walk of any kind counts.
        private const string VerifiedThisProcessKey =
            "Zep.VerifiedThisProcess";

        internal static bool VerifiedThisProcess
        {
            get => EditorHost.GetSessionFlag(VerifiedThisProcessKey);
            set => EditorHost.SetSessionFlag(VerifiedThisProcessKey, value);
        }

        private sealed class VerificationJob
        {
            internal VerificationJob(bool retypeAfter)
            {
                RetypeAfter = retypeAfter;
            }

            // The artifact revision moved as well: the type reconciliation runs
            // after the difference is applied, and its save is the job's save.
            internal bool RetypeAfter { get; }
        }

        private sealed class VerificationDiff
        {
            // The first record the walk replaced and its replacement, for the perf
            // line: a diff that replaces every record is a field the two sides
            // derive differently, and the line names it.
            internal AssetRecord FirstReplacedRecord;

            internal AssetRecord FirstReplacingRecord;

            internal List<string> RemovedPaths { get; } = new List<string>();

            internal List<AssetRecord> RecordUpserts { get; } = new List<AssetRecord>();

            internal List<AssetFolderRecord> FolderUpserts { get; } = new List<AssetFolderRecord>();

            internal bool IsEmpty =>
                RemovedPaths.Count == 0
                && RecordUpserts.Count == 0
                && FolderUpserts.Count == 0;
        }

        /// <summary>
        /// The silent walk that reconciles a published snapshot with the project
        /// when the journal could not vouch for it. It runs after the operation has
        /// completed, exactly like the type reconciliation, and shares its cost
        /// model: the walk is the cold path's own worker walk, the difference is
        /// computed there too, and one catalog apply publishes it. Nothing is
        /// cleared, no overlay is raised, and a fault ends the job with the on-disk
        /// state untouched, so the next reload simply verifies again.
        /// </summary>
        private void BeginVerification(
            SynchronizationOperation operation,
            uint artifactDependencyVersion,
            bool artifactRevisionChanged,
            string reason)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            _backgroundJobOperation = operation;
            ZepPerfLog.Count(ZepPerfCounter.VerificationJobs);
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] verification reason={reason} pendingBatches={operation.PendingChanges.Count} artifactChanged={artifactRevisionChanged} indexCount={_index.Count}");
            var verification = new VerificationJob(artifactRevisionChanged);
            if (!_asyncSource.IsThreadSafe)
            {
                BeginWhenSourceScanAvailable(
                    operation,
                    () => VerifySynchronouslyOwningGate(
                        operation,
                        artifactDependencyVersion,
                        verification));
                return;
            }

            if (_enablePersistentCache
                && !PersistentAssetIndexCache.HasAuthoritativeTypeSnapshotOverride)
            {
                BeginBudgetedAuthoritativeTypeCapture(
                    operation,
                    artifactDependencyVersion,
                    verification);
                return;
            }

            var typesByGuid = _enablePersistentCache
                ? PersistentAssetIndexCache.CaptureAuthoritativeTypeSnapshot()
                : null;
            BeginPreparedFullScan(
                operation,
                operation.UnityLabelsByGuid,
                typesByGuid,
                artifactDependencyVersion,
                verification);
        }

        // The main-thread shape of the walk, for a source that cannot enumerate on
        // a worker. Same diff, same apply; the walk itself blocks the caller the
        // way every synchronous rebuild does.
        private void VerifySynchronouslyOwningGate(
            SynchronizationOperation operation,
            uint artifactDependencyVersion,
            VerificationJob verification)
        {
            VerificationDiff diff;
            int scanGeneration;
            int scanProofEpoch;
            try
            {
                if (!IsCurrent(operation))
                {
                    return;
                }

                scanProofEpoch = Volatile.Read(ref _authoritativeProofEpoch);
                _syncSource.PrepareForEnumeration(operation.UnityLabelsByGuid, null);
                ZepPerfLog.Count(ZepPerfCounter.FullRecordEnumerations);
                var records = new List<AssetRecord>();
                foreach (var record in _syncSource.GetRecords())
                {
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }

                var folders = new List<AssetFolderRecord>(_syncSource.GetFolderRecords());
                scanGeneration = Volatile.Read(ref _assetChangeGeneration);
                ApplyScanOverlay(
                    records,
                    folders,
                    CaptureScanOverlay(scanGeneration),
                    authoritativeSnapshot: true);
                diff = ComputeVerificationDiff(
                    new List<AssetRecord>(_index.Enumerate()),
                    new List<AssetFolderRecord>(_index.EnumerateFolders()),
                    records,
                    folders);
            }
            catch (Exception ex)
            {
                AbandonVerification(operation, "Synchronous verification", ex);
                return;
            }
            finally
            {
                SourceScanGate.Release();
            }

            ApplyVerificationResult(
                operation,
                diff,
                scanGeneration,
                scanProofEpoch,
                artifactDependencyVersion,
                verification);
        }

        // What the walk found that the index does not hold, by identity: a record
        // is the same record while its GUID is, wherever it moved and whatever it
        // is now typed; a folder is the same folder while its path is. Everything
        // else is an upsert of the walk's version, and every index entry the walk
        // did not see is a removal by path. Worker-safe: reads immutable lists.
        private static VerificationDiff ComputeVerificationDiff(
            List<AssetRecord> indexRecords,
            List<AssetFolderRecord> indexFolders,
            List<AssetRecord> walkRecords,
            List<AssetFolderRecord> walkFolders)
        {
            var diff = new VerificationDiff();
            var indexByGuid = new Dictionary<string, AssetRecord>(
                indexRecords.Count,
                StringComparer.Ordinal);
            for (var i = 0; i < indexRecords.Count; i++)
            {
                indexByGuid[indexRecords[i].Guid] = indexRecords[i];
            }

            var walkGuids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < walkRecords.Count; i++)
            {
                var record = walkRecords[i];
                walkGuids.Add(record.Guid);
                if (!indexByGuid.TryGetValue(record.Guid, out var existing)
                    || !existing.Equals(record))
                {
                    if (existing != null && diff.FirstReplacedRecord == null)
                    {
                        diff.FirstReplacedRecord = existing;
                        diff.FirstReplacingRecord = record;
                    }

                    diff.RecordUpserts.Add(record);
                }
            }

            for (var i = 0; i < indexRecords.Count; i++)
            {
                if (!walkGuids.Contains(indexRecords[i].Guid))
                {
                    diff.RemovedPaths.Add(indexRecords[i].AssetPath);
                }
            }

            var indexFoldersByPath = new Dictionary<string, AssetFolderRecord>(
                indexFolders.Count,
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < indexFolders.Count; i++)
            {
                indexFoldersByPath[indexFolders[i].FolderPath.Value] = indexFolders[i];
            }

            // A folder the walk listed, and every folder above anything it listed
            // (the roots "Assets" and "Packages" above all, which no source lists
            // and the index adds itself): removing an ancestor would take the
            // whole subtree the walk just confirmed with it.
            var walkFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < walkFolders.Count; i++)
            {
                var folder = walkFolders[i];
                walkFolderPaths.Add(folder.FolderPath.Value);
                if (!indexFoldersByPath.TryGetValue(folder.FolderPath.Value, out var existing)
                    || !FolderRecordsEqual(existing, folder))
                {
                    diff.FolderUpserts.Add(folder);
                }
            }

            var confirmedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < walkFolders.Count; i++)
            {
                AddSelfAndAncestors(walkFolders[i].FolderPath.Value, confirmedFolders);
            }

            for (var i = 0; i < walkRecords.Count; i++)
            {
                var parent = ZepPathHelpers.GetParentFolder(walkRecords[i].AssetPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    AddSelfAndAncestors(parent, confirmedFolders);
                }
            }

            for (var i = 0; i < indexFolders.Count; i++)
            {
                var path = indexFolders[i].FolderPath.Value;
                if (!walkFolderPaths.Contains(path) && !confirmedFolders.Contains(path))
                {
                    diff.RemovedPaths.Add(path);
                }
            }

            return diff;
        }

        private static bool FolderRecordsEqual(AssetFolderRecord left, AssetFolderRecord right)
        {
            if (!string.Equals(left.FolderPath.Value, right.FolderPath.Value, StringComparison.Ordinal)
                || !string.Equals(left.Guid, right.Guid, StringComparison.Ordinal)
                || left.UnityLabels.Count != right.UnityLabels.Count)
            {
                return false;
            }

            for (var i = 0; i < left.UnityLabels.Count; i++)
            {
                if (!string.Equals(left.UnityLabels[i], right.UnityLabels[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        // Main thread. Live batches that landed while the worker walked are in the
        // live index already and were journaled; the difference is pruned by them
        // exactly as a cold snapshot is — nothing the overlay removed is upserted,
        // nothing it holds current is removed — and then applied as ONE catalog
        // change. The save that follows covers the journal as it stands now.
        private void ApplyVerificationResult(
            SynchronizationOperation operation,
            VerificationDiff diff,
            int scanGeneration,
            int scanProofEpoch,
            uint artifactDependencyVersion,
            VerificationJob verification)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            try
            {
                if (scanProofEpoch != Volatile.Read(ref _authoritativeProofEpoch))
                {
                    BeginVerification(
                        operation,
                        artifactDependencyVersion,
                        verification.RetypeAfter,
                        "authoritative-proof-expired");
                    return;
                }

                var generationNow = Volatile.Read(ref _assetChangeGeneration);
                if (generationNow != scanGeneration)
                {
                    var overlayNow = CaptureScanOverlay(generationNow);
                    ApplyScanOverlay(
                        diff.RecordUpserts,
                        diff.FolderUpserts,
                        overlayNow,
                        authoritativeSnapshot: false);
                    // A path the overlay holds current, anything below it, and
                    // every folder above it (a current child proves its parents)
                    // is not removed on the strength of a walk that predates it.
                    diff.RemovedPaths.RemoveAll(path =>
                        IsAtOrBelowAny(path, overlayNow.ExpectedCurrentRoots)
                        || IsAncestorOfAny(path, overlayNow.ExpectedCurrentRoots));
                }

                var delta = _index.Apply(
                    diff.RemovedPaths,
                    Array.Empty<AssetCatalogMove>(),
                    diff.RecordUpserts,
                    diff.FolderUpserts);
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] verification applied removed={diff.RemovedPaths.Count} upserted={diff.RecordUpserts.Count} folders={diff.FolderUpserts.Count} deltaEmpty={delta.IsEmpty} indexCount={_index.Count} removedSample=[{string.Join(", ", diff.RemovedPaths.Take(8))}] upsertSample=[{string.Join(", ", diff.RecordUpserts.Take(3).Select(record => record.AssetPath))}] folderSample=[{string.Join(", ", diff.FolderUpserts.Take(3).Select(folder => folder.FolderPath.Value))}] firstUpsertWas={(diff.FirstReplacedRecord == null ? "new" : diff.FirstReplacedRecord.ToString())} firstUpsertNow={(diff.FirstReplacingRecord == null ? "-" : diff.FirstReplacingRecord.ToString())}");
                if (!delta.IsEmpty)
                {
                    _pendingFolderRecords = new List<AssetFolderRecord>(
                        _index.EnumerateFolders());
                    _onCatalogPatched?.Invoke(delta);
                }

                // Publication listeners can apply another concrete batch. Its
                // unresolved paths still need their own current proof.
                if (scanProofEpoch != Volatile.Read(ref _authoritativeProofEpoch))
                {
                    BeginVerification(
                        operation,
                        artifactDependencyVersion,
                        verification.RetypeAfter,
                        "concrete-proof-expired-during-publication");
                    return;
                }

                if (generationNow == scanGeneration)
                {
                    ClearScanOverlay();
                }

                _unresolvedProofEpoch = 0;
                VerifiedThisProcess = true;
                if (!IsCurrent(operation))
                {
                    return;
                }

                if (verification.RetypeAfter)
                {
                    BeginTypeReconciliation(operation, artifactDependencyVersion);
                    return;
                }

                if (!_enablePersistentCache)
                {
                    EndBackgroundJob(operation);
                    return;
                }

                BeginStagedSnapshotSave(
                    operation,
                    new List<AssetRecord>(_index.Enumerate()),
                    new List<AssetFolderRecord>(_index.EnumerateFolders()),
                    artifactDependencyVersion,
                    () => IsCurrent(operation),
                    completesOperation: false,
                    authoritative: true);
            }
            catch (Exception ex)
            {
                AbandonVerification(operation, "Verification apply", ex);
            }
        }

        // One line, the job ends, the disk stays as it was: the unretired journal
        // and any marker survive, so the next reload verifies again.
        private void AbandonVerification(
            SynchronizationOperation operation,
            string stage,
            Exception exception)
        {
            EditorHost.LogWarning(
                $"[Zep] {stage} failed ({exception?.GetType().Name}: {exception?.Message}); the index keeps the loaded snapshot and the next reload will verify it.");
            EndBackgroundJob(operation);
        }

        // Every save covers the journal as it stands when the save begins: the
        // index has every batch applied to it by then (live batches are applied as
        // they arrive; a hydration replayed the rest), so the commit retires exactly
        // what the snapshot holds. A non-authoritative save whose journal cannot be
        // read is skipped — its records are not a walk's, so it may not discard
        // batches — and the next reload verifies instead.
        private void BeginStagedSnapshotSave(
            SynchronizationOperation operation,
            IReadOnlyCollection<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folderRecords,
            uint artifactDependencyVersion,
            Func<bool> canCommit,
            bool completesOperation = true,
            bool authoritative = false,
            bool retypeAfterVerification = false)
        {
            if (_unresolvedProofEpoch != 0 && IsCurrent(operation))
            {
                if (completesOperation)
                {
                    CompleteOperation(operation);
                }

                BeginVerification(operation, artifactDependencyVersion,
                    retypeAfterVerification, "unresolved-before-snapshot-save");
                return;
            }

            var proofEpoch = Volatile.Read(ref _authoritativeProofEpoch);
            var recoveryState = PersistentAssetIndexCache.CaptureRecoveryState();
            var refused = _pendingSnapshotSave != null
                || (!authoritative
                    && recoveryState.JournalGeneration
                        == PersistentAssetIndexCache.UntrustedJournalGeneration);
            PersistentAssetIndexCache.SnapshotSaveAttempt attempt = null;
            if (!refused
                && !PersistentAssetIndexCache.TryBeginSnapshotSave(
                    records,
                    folderRecords,
                    artifactDependencyVersion,
                    recoveryState,
                    () => canCommit()
                        && proofEpoch == Volatile.Read(ref _authoritativeProofEpoch),
                    out attempt))
            {
                refused = true;
            }

            if (refused)
            {
                if (!completesOperation)
                {
                    // A follow-up job's save, with the slot already held by an earlier
                    // operation's write or the begin refused. Skip it silently: its
                    // operation completed at hydration, and the old artifact revision
                    // and the unretired journal stay on disk, so the next reload
                    // simply runs the job again.
                    EndBackgroundJob(operation);
                    return;
                }

                // Only an authoritative save carries knowledge the journal cannot
                // reproduce (what the walk saw on disk); losing it must make the
                // next load verify again. A refused save of a hydrated-and-patched
                // index loses nothing: the committed snapshot plus the unretired
                // journal are the same account the index was built from.
                if (authoritative && IsCurrent(operation))
                {
                    PersistentAssetIndexCache.MarkDirty();
                }

                CompleteOperation(operation);
                return;
            }

            var task = Task.Run(
                () => PersistentAssetIndexCache.WriteStagedSnapshot(attempt));
            _pendingSnapshotSave = new PendingSnapshotSave(
                operation,
                attempt,
                task,
                completesOperation,
                authoritative,
                proofEpoch,
                retypeAfterVerification);

            void Poll()
            {
                if (!task.IsCompleted)
                {
                    return;
                }

                DetachPoll(Poll);
                ResolvePendingSnapshotSave();
            }

            AttachPoll(Poll);
        }

        /// <summary>
        /// A hydrated operation writes no snapshot when it replayed nothing into the
        /// loaded one, or can no longer authorize a write. Neither marks the cache
        /// dirty — there is nothing to distrust.
        /// </summary>
        private bool NeedsPatchedSnapshotSave(SynchronizationOperation operation)
        {
            return operation.PendingChanges.Count > 0 && IsCurrent(operation);
        }

        /// <summary>
        /// Main-thread completion of the staged snapshot write: commit it, or abandon it
        /// when the serialization itself failed, then complete the operation that started
        /// it. Called exactly once per attempt, from the poll or from Dispose. The
        /// follow-up background job's save completes no operation - its
        /// operation completed at hydration. A newer unresolved batch restarts
        /// verification; other outcomes end the job and retain existing failure evidence.
        /// </summary>
        private void ResolvePendingSnapshotSave()
        {
            var pending = _pendingSnapshotSave;
            if (pending == null)
            {
                return;
            }

            _pendingSnapshotSave = null;
            var restartForUnresolvedChanges = false;
            try
            {
                bool saved;
                if (pending.Task.IsFaulted)
                {
                    var exception = pending.Task.Exception?.GetBaseException();
                    EditorHost.LogWarning(
                        $"[Zep] Index snapshot serialization failed ({exception?.GetType().Name}: {exception?.Message}); the next reload will rebuild the index.");
                    PersistentAssetIndexCache.AbandonSnapshotSave(pending.Attempt);
                    saved = false;
                }
                else
                {
                    saved = PersistentAssetIndexCache.TryCommitSnapshotSave(
                        pending.Attempt);
                    restartForUnresolvedChanges = !saved
                        && IsCurrent(pending.Operation)
                        && _unresolvedProofEpoch != 0
                        && pending.ProofEpoch != Volatile.Read(ref _authoritativeProofEpoch);
                }

                if (!saved
                    && pending.Authoritative
                    && IsCurrent(pending.Operation))
                {
                    PersistentAssetIndexCache.MarkDirty();
                }
            }
            catch (Exception ex)
            {
                FailOperation(pending.Operation, "Index snapshot commit", ex);
                EndBackgroundJob(pending.Operation);
                return;
            }

            if (pending.CompletesOperation)
            {
                CompleteOperation(pending.Operation);
            }

            if (restartForUnresolvedChanges)
            {
                BeginVerification(
                    pending.Operation,
                    pending.Attempt.ArtifactDependencyVersion,
                    pending.RetypeAfterVerification,
                    "unresolved-during-snapshot-save");
            }
            else if (!pending.CompletesOperation)
            {
                EndBackgroundJob(pending.Operation);
            }
        }

        private sealed class PendingSnapshotSave
        {
            internal PendingSnapshotSave(
                SynchronizationOperation operation,
                PersistentAssetIndexCache.SnapshotSaveAttempt attempt,
                Task task,
                bool completesOperation,
                bool authoritative,
                int proofEpoch,
                bool retypeAfterVerification)
            {
                Operation = operation;
                Attempt = attempt;
                Task = task;
                CompletesOperation = completesOperation;
                Authoritative = authoritative;
                ProofEpoch = proofEpoch;
                RetypeAfterVerification = retypeAfterVerification;
            }

            internal SynchronizationOperation Operation { get; }

            internal PersistentAssetIndexCache.SnapshotSaveAttempt Attempt { get; }

            internal Task Task { get; }

            internal int ProofEpoch { get; }

            internal bool RetypeAfterVerification { get; }

            // False for the follow-up type-reconciliation job's save: the operation it
            // belongs to completed at hydration, so resolving this save ends the job
            // rather than the operation.
            internal bool CompletesOperation { get; }

            // True for the cold scan's save: its records came from the walk, not
            // from snapshot + journal, so a refused commit must mark the cache
            // dirty or that knowledge is lost until the next walk.
            internal bool Authoritative { get; }
        }

        // Total replay. Every batch is applied; an entry the source cannot build
        // is recorded and skipped, and the caller verifies the snapshot against a
        // walk instead of discarding it. Null when the operation was superseded.
        private List<string> ApplyPendingChanges(
            SynchronizationOperation operation,
            IReadOnlyList<ProjectAssetChanges> pendingChanges)
        {
            var unresolved = new List<string>();
            if (!IsCurrent(operation))
            {
                return null;
            }

            if (pendingChanges == null || pendingChanges.Count == 0)
            {
                return unresolved;
            }

            try
            {
                for (var batchIndex = 0; batchIndex < pendingChanges.Count; batchIndex++)
                {
                    if (!IsCurrent(operation))
                    {
                        return null;
                    }

                    var changes = pendingChanges[batchIndex].Normalize();
                    if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal-replay batch={batchIndex} imported=[{string.Join(", ", changes.Imported)}] deleted=[{string.Join(", ", changes.Deleted)}] moved=[{string.Join(", ", changes.Moved)}] movedFrom=[{string.Join(", ", changes.MovedFromPaths)}] reload={changes.DidDomainReload}");
                    var records = new Dictionary<string, AssetRecord>(
                        StringComparer.OrdinalIgnoreCase);
                    var folders = new Dictionary<string, AssetFolderRecord>(
                        StringComparer.OrdinalIgnoreCase);

                    var subtreeBudget =
                        PersistentAssetIndexCache.MaxPendingChangeEntryCount;
                    var vanished = new List<string>();
                    for (var i = 0; i < changes.Imported.Length; i++)
                    {
                        if (TryResolvePendingPath(
                            changes.Imported[i],
                            assumeFolder: false,
                            changes.Deleted,
                            records,
                            folders,
                            out var resolvedFolder))
                        {
                            // A folder import names one path; the journal cannot
                            // describe the subtree that folder may already hold, so
                            // the replay reads it from disk. Over the bound, or with
                            // no observer, the subtree is the walk's to find.
                            if (resolvedFolder
                                && !TryReplayFolderSubtree(
                                    changes.Imported[i],
                                    records,
                                    folders,
                                    ref subtreeBudget))
                            {
                                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal-replay unresolved batch={batchIndex} import={changes.Imported[i]} resolvedFolder=True");
                                unresolved.Add(changes.Imported[i]);
                            }

                            continue;
                        }

                        // An import of a path that is no longer in its folder is not
                        // an unresolvable record: the file was renamed or removed after
                        // the batch was journaled — a create that lands under a default
                        // name and is moved to the typed one, before the compile's
                        // reload — and the batch that did that follows in the journal.
                        // There is nothing to upsert here; whatever the index holds at
                        // the path is stale and goes with it.
                        if (IsAbsentFromItsFolder(changes.Imported[i]))
                        {
                            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal-replay skipped vanished import batch={batchIndex} path={changes.Imported[i]}");
                            vanished.Add(changes.Imported[i]);
                            continue;
                        }

                        // Present but not buildable (no .meta yet, an importer that
                        // cannot answer): the walk will describe it.
                        if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal-replay unresolved batch={batchIndex} import={changes.Imported[i]} resolvedFolder=False");
                        unresolved.Add(changes.Imported[i]);
                    }

                    var moves = new List<AssetCatalogMove>(changes.Moved.Length);
                    for (var i = 0; i < changes.Moved.Length; i++)
                    {
                        var oldPath = i < changes.MovedFromPaths.Length
                            ? changes.MovedFromPaths[i]
                            : string.Empty;
                        var movedFolder = _index.TryGetFolder(oldPath, out _);
                        moves.Add(new AssetCatalogMove(oldPath, changes.Moved[i]));
                        // Moved and then deleted, compacted into one batch: the move's
                        // only remaining meaning is that the source is gone, and the
                        // removal that follows takes the destination. Building the
                        // destination would upsert a record for a path the same batch
                        // removes — and the upsert runs after the removal.
                        if (IsRemovedByOverlay(changes.Moved[i], changes.Deleted))
                        {
                            continue;
                        }

                        if (!TryResolvePendingPath(
                            changes.Moved[i],
                            movedFolder,
                            changes.Deleted,
                            records,
                            folders,
                            out _))
                        {
                            // The move itself still applies — the index carries the
                            // record to its new path — and the walk settles what the
                            // source could not describe.
                            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] journal-replay unresolved batch={batchIndex} move={oldPath} -> {changes.Moved[i]} movedFolder={movedFolder}");
                            unresolved.Add(changes.Moved[i]);
                        }
                    }

                    if (!IsCurrent(operation))
                    {
                        return null;
                    }

                    IReadOnlyList<string> removedRoots = changes.Deleted;
                    if (vanished.Count > 0)
                    {
                        var combined = new List<string>(changes.Deleted.Length + vanished.Count);
                        combined.AddRange(changes.Deleted);
                        combined.AddRange(vanished);
                        removedRoots = combined;
                    }

                    _index.Apply(
                        removedRoots,
                        moves,
                        new List<AssetRecord>(records.Values),
                        new List<AssetFolderRecord>(folders.Values));
                }
            }
            catch (Exception ex)
            {
                EditorHost.LogWarning(
                    $"[Zep] Pending asset change resolution failed ({ex.GetType().Name}: {ex.Message}); the loaded snapshot will be verified.");
                unresolved.Add("<replay-faulted>");
            }

            _pendingFolderRecords = new List<AssetFolderRecord>(
                _index.EnumerateFolders());
            return unresolved;
        }

        private bool TryResolvePendingPath(
            string path,
            bool assumeFolder,
            IReadOnlyList<string> removedRoots,
            Dictionary<string, AssetRecord> records,
            Dictionary<string, AssetFolderRecord> folders,
            out bool resolvedFolder)
        {
            resolvedFolder = false;
            var typedPath = AssetPath.From(path);
            if (typedPath.IsEmpty)
            {
                return false;
            }

            var record = _syncSource.BuildRecord(typedPath);
            if (record != null)
            {
                records[record.AssetPath] = record;
                return TryResolvePendingParentFolders(
                    record.AssetPath,
                    removedRoots,
                    folders);
            }

            var folder = _syncSource.BuildFolderRecord(typedPath);
            if (folder == null && assumeFolder)
            {
                folder = new AssetFolderRecord(typedPath);
            }

            if (folder == null)
            {
                return false;
            }

            resolvedFolder = true;
            folders[folder.FolderPath.Value] = folder;
            return TryResolvePendingParentFolders(
                folder.FolderPath.Value,
                removedRoots,
                folders);
        }

        private bool TryResolvePendingParentFolders(
            string path,
            IReadOnlyList<string> removedRoots,
            Dictionary<string, AssetFolderRecord> folders)
        {
            var parent = ZepPathHelpers.GetParentFolder(path);
            while (!string.IsNullOrEmpty(parent)
                && !string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parent, "Packages", StringComparison.OrdinalIgnoreCase))
            {
                if (folders.ContainsKey(parent))
                {
                    parent = ZepPathHelpers.GetParentFolder(parent);
                    continue;
                }

                if (!IsRemovedByOverlay(parent, removedRoots)
                    && _index.TryGetFolder(parent, out _))
                {
                    return true;
                }

                var folder = _syncSource.BuildFolderRecord(AssetPath.From(parent));
                if (folder == null)
                {
                    return false;
                }

                folders[folder.FolderPath.Value] = folder;
                parent = ZepPathHelpers.GetParentFolder(parent);
            }

            return true;
        }

        // Whether the path is PROVEN absent from its parent folder by the browser's
        // own observer — the panel's filesystem boundary, so tests describe it with
        // the same fake the folder-subtree replay uses. Without an observer, or with
        // an incomplete observation, nothing is proven and the caller keeps refusing
        // as it always did.
        private bool IsAbsentFromItsFolder(string assetPath)
        {
            if (_directFolderSource == null)
            {
                return false;
            }

            var parent = ZepPathHelpers.GetParentFolder(assetPath);
            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }

            var observation = _directFolderSource.Observe(AssetPath.From(parent));
            if (observation == null || !observation.IsComplete)
            {
                return false;
            }

            var entries = observation.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(
                    entries[i].Path.Value,
                    assetPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        // Reads an imported folder's subtree into the replay: direct children from
        // the same observer the browser uses, recursively; every file built into a
        // record by the source that owns record construction; every directory the
        // observer saw is a folder whether or not Unity has stamped it yet. Bounded
        // by the journal's own entry cap so a pasted-in tree of tens of thousands
        // of files cannot turn a reload into a main-thread walk: over the bound, or
        // with no observer, or with a file the source cannot build (no .meta yet),
        // the import stays unresolved and the caller takes the authoritative scan.
        private bool TryReplayFolderSubtree(
            string folderPath,
            Dictionary<string, AssetRecord> records,
            Dictionary<string, AssetFolderRecord> folders,
            ref int budget)
        {
            if (_directFolderSource == null)
            {
                return false;
            }

            var observation = _directFolderSource.Observe(AssetPath.From(folderPath));
            if (observation == null || !observation.IsComplete)
            {
                return false;
            }

            var entries = observation.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                if (--budget < 0)
                {
                    return false;
                }

                var entry = entries[i];
                if (entry.IsFolder)
                {
                    var folder = _syncSource.BuildFolderRecord(entry.Path)
                        ?? new AssetFolderRecord(entry.Path);
                    folders[folder.FolderPath.Value] = folder;
                    if (!TryReplayFolderSubtree(
                        entry.Path.Value,
                        records,
                        folders,
                        ref budget))
                    {
                        return false;
                    }

                    continue;
                }

                var record = _syncSource.BuildRecord(entry.Path);
                if (record == null)
                {
                    return false;
                }

                records[record.AssetPath] = record;
            }

            return true;
        }

        /// <summary>
        /// The follow-up job that patches the types a compile changed, after its
        /// operation has already completed against the hydrated index. It rebuilds
        /// every ambiguously typed record, publishes each slice through
        /// <c>_onCatalogPatched</c>, and owns the one and only snapshot save that
        /// promotes the new artifact revision.
        ///
        /// The rebuild is budgeted exactly like the cold path's authoritative type
        /// capture: it resolves one record at a time through Unity's importers, so on
        /// a project with tens of thousands of ScriptableObjects an uninterrupted loop
        /// would append seconds of frozen editor to every compile. The first slice runs
        /// in the caller's frame, so the ordinary small batch settles synchronously;
        /// only a batch that exhausts the budget continues on later editor ticks.
        ///
        /// The job has exactly five exits, and exactly one of them saves:
        /// <list type="bullet">
        /// <item>NotStarted — the entry guard below. Nothing was set, nothing attached.</item>
        /// <item>Faulted (importer metadata) — the capture threw. Ends the job without
        /// <c>Settle</c> and without a detach; see the comment at that catch.</item>
        /// <item>Faulted (rebuild slice) — the in-loop catch. <c>Settle(save: false)</c>.</item>
        /// <item>Cancelled — a newer operation started. <c>Settle(save: false)</c>.</item>
        /// <item>Completed — every candidate visited with no exception, the
        /// zero-candidate job included. <c>Settle(save: true)</c>, the only save.</item>
        /// </list>
        /// Saving anywhere else would stamp the new artifact revision onto a partly
        /// reconciled index, and the next load would never retype again. Every other
        /// exit leaves the on-disk snapshot untouched, so the old revision and the
        /// unretired journal survive and the next reload re-runs the job.
        /// </summary>
        private void BeginTypeReconciliation(
            SynchronizationOperation operation,
            uint artifactDependencyVersion)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            if (_unresolvedProofEpoch != 0)
            {
                BeginVerification(operation, artifactDependencyVersion,
                    artifactRevisionChanged: true, "unresolved-before-type-reconciliation");
                return;
            }

            // Set above the first real work and below the guard, so a job that faults or
            // settles inside this very call still passes through a clear, and a job that
            // never starts never set the field.
            _backgroundJobOperation = operation;
            HashSet<string> scriptedImporterExtensions;
            try
            {
                scriptedImporterExtensions =
                    EditorHost.CaptureScriptedImporterExtensions();
            }
            catch (Exception ex)
            {
                // This exit cannot call Settle and needs no DetachPoll, and neither is an
                // oversight: Settle closes over locals declared below that are not
                // definitely assigned here (it would not compile), and AttachPoll runs
                // after the first Poll(), so nothing is subscribed yet. Log once, end the
                // job, and leave the snapshot alone - the next reload re-runs it.
                EditorHost.LogWarning(
                    $"[Zep] Script type reconciliation could not inspect importer metadata ({ex.GetType().Name}: {ex.Message}); the loaded snapshot keeps its recorded types.");
                EndBackgroundJob(operation);
                return;
            }

            // Only the records whose type a reload can actually change are looked at.
            // The catalog itself is never enumerated: the index addresses the
            // extension-ambiguous and importer-resolved records directly, and the
            // buckets below cover the one case it cannot see — a ScriptedImporter that
            // claims an extension the extension map already maps, whose records still
            // look extension-determined. The per-record predicate is unchanged, so the
            // set that is actually rebuilt is exactly the set the full walk rebuilt.
            var candidates = new List<AssetRecord>();
            _index.CollectTypeReconciliationCandidates(
                CollectMappedScriptedImporterTypeNames(scriptedImporterExtensions),
                candidates);
            var nextRecord = 0;
            var isSettled = false;
            var unresolvedCount = 0;
            // One slice's rebuilt records land in a single catalog apply. Each Upsert
            // was its own apply — its own delta, its own version bump — and the delta
            // was discarded every time, so nothing observed the per-record grain. A
            // slice is synchronous, so nothing can observe the batched grain either.
            var refreshedThisSlice = new List<AssetRecord>();

            void Poll()
            {
                var firstRecordThisFrame = nextRecord;
                if (!IsCurrent(operation))
                {
                    // Cancelled. No clear: IsCurrent goes false only through
                    // BeginOperation or Dispose, and both cleared the field already.
                    Settle(save: false);
                    return;
                }

                refreshedThisSlice.Clear();
                var budget = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (nextRecord < candidates.Count
                        && (nextRecord == firstRecordThisFrame
                            || budget.Elapsed.TotalMilliseconds
                                < MainThreadWorkBudgetMilliseconds))
                    {
                        var current = candidates[nextRecord];
                        nextRecord++;
                        if (current.BaseTypeNames.Count == 0
                            && !EditorHost.RequiresAuthoritativeType(
                                current.AssetPath,
                                scriptedImporterExtensions))
                        {
                            continue;
                        }

                        var refreshed = _syncSource.BuildRecord(
                            AssetPath.From(current.AssetPath));
                        if (refreshed == null)
                        {
                            // A record the source cannot rebuild keeps the type the
                            // snapshot recorded: this job patches types, it never
                            // discards knowledge, and one unreadable importer is not a
                            // reason to rescan the project. Counted and reported once at
                            // the end rather than per record.
                            unresolvedCount++;
                            continue;
                        }

                        if (!IsCurrent(operation))
                        {
                            Settle(save: false);
                            return;
                        }

                        refreshedThisSlice.Add(refreshed);
                    }

                    if (refreshedThisSlice.Count > 0)
                    {
                        var delta = _index.ApplyFileChanges(
                            refreshedThisSlice,
                            Array.Empty<string>());
                        // Publishing is a condition, never an exit: a compile retypes
                        // almost none of the candidates, so the index short-circuits most
                        // slices into an empty delta, and republishing every live pane for
                        // one of those is exactly the post-compile stall this job removes.
                        // Returning here instead would skip the settle check below and
                        // leave the poll attached for the life of the domain.
                        if (!delta.IsEmpty)
                        {
                            _onCatalogPatched?.Invoke(delta);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Faulted (rebuild slice). Settle detaches and does not save, so the
                    // snapshot keeps its old artifact revision and the journal stays
                    // unretired; the next reload runs the job again.
                    EditorHost.LogWarning(
                        $"[Zep] Script type reconciliation failed ({ex.GetType().Name}: {ex.Message}); the loaded snapshot keeps its recorded types.");
                    Settle(save: false);
                    EndBackgroundJob(operation);
                    return;
                }

                ScriptTypeReconciliationSliceObserverForTests?.Invoke(
                    nextRecord - firstRecordThisFrame);
                if (nextRecord < candidates.Count)
                {
                    return;
                }

                if (unresolvedCount > 0)
                {
                    // One line for the whole job, and only where "the job ended" is a
                    // complete statement about the candidate set: a cancelled or faulted
                    // job's partial count would report a number that means nothing.
                    EditorHost.Log(
                        $"[Zep] Script type reconciliation kept {unresolvedCount} record(s) the source could not rebuild; they keep the type the snapshot recorded.");
                }

                Settle(save: true);
            }

            // The job's single detach point. The bool says whether THIS exit saves, and
            // only the Completed exit passes true: the save stamps the new artifact
            // revision, which is only the truth once every candidate has been visited.
            void Settle(bool save)
            {
                DetachPoll(Poll);
                isSettled = true;
                if (save)
                {
                    if (_unresolvedProofEpoch != 0)
                    {
                        BeginVerification(operation, artifactDependencyVersion,
                            artifactRevisionChanged: true, "unresolved-during-type-reconciliation");
                        return;
                    }

                    BeginStagedSnapshotSave(
                        operation,
                        new List<AssetRecord>(_index.Enumerate()),
                        new List<AssetFolderRecord>(_index.EnumerateFolders()),
                        artifactDependencyVersion,
                        () => IsCurrent(operation),
                        completesOperation: false,
                        retypeAfterVerification: true);
                }
            }

            Poll();
            if (!isSettled)
            {
                AttachPoll(Poll);
            }
        }

        /// <summary>
        /// The type buckets the index must hand over in addition to its own ambiguous
        /// records: one per ScriptedImporter extension that the extension map ALSO
        /// maps to a concrete type (an override such as <c>.png</c>). A record with
        /// such an extension that the importer resolved to that same type is
        /// indistinguishable from an ordinary extension-typed record, so it has to be
        /// reached by its type name. An extension the map does not know already infers
        /// to a placeholder and is in the index's ambiguous set.
        /// </summary>
        private static HashSet<string> CollectMappedScriptedImporterTypeNames(
            HashSet<string> scriptedImporterExtensions)
        {
            var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in scriptedImporterExtensions)
            {
                var inferred = AssetExtensionTypeMap.GetTypeNameForPath("x" + extension);
                if (!string.Equals(
                        inferred,
                        AssetExtensionTypeMap.UnknownTypeName,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        inferred,
                        AssetExtensionTypeMap.ScriptableObjectTypeName,
                        StringComparison.Ordinal))
                {
                    typeNames.Add(inferred);
                }
            }

            return typeNames;
        }

        private void ApplyRecordedChangesToSnapshot(
            SynchronizationOperation operation,
            List<AssetRecord> records,
            List<AssetFolderRecord> folderRecords,
            IReadOnlyList<ProjectAssetChanges> pendingChanges)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            var generation = Volatile.Read(ref _assetChangeGeneration);
            var overlay = CaptureScanOverlay(generation);
            ApplyScanOverlay(
                records,
                folderRecords,
                overlay,
                authoritativeSnapshot: false,
                CaptureReplayMoveSources(pendingChanges));
            if (IsCurrent(operation))
            {
                ClearAppliedScanOverlay(generation);
            }
        }

        private static IReadOnlyList<string> CaptureReplayMoveSources(
            IReadOnlyList<ProjectAssetChanges> pendingChanges)
        {
            if (pendingChanges == null || pendingChanges.Count == 0)
            {
                return Array.Empty<string>();
            }

            var sources = new List<string>();
            for (var batchIndex = 0; batchIndex < pendingChanges.Count; batchIndex++)
            {
                var changes = pendingChanges[batchIndex].Normalize();
                for (var i = 0; i < changes.MovedFromPaths.Length; i++)
                {
                    if (!string.IsNullOrEmpty(changes.MovedFromPaths[i]))
                    {
                        sources.Add(changes.MovedFromPaths[i]);
                    }
                }
            }

            return sources;
        }

        private void ApplyHydratedCache(
            SynchronizationOperation operation,
            List<AssetRecord> records,
            List<AssetFolderRecord> folderRecords)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _index.Replace(records, folderRecords);
            stopwatch.Stop();
            if (ZepPerfLog.IsEnabled)
            {
                ZepPerfLog.Log(
                    $"[Zep/perf]   _index.Replace records={records.Count} folders={folderRecords.Count} ms={stopwatch.Elapsed.TotalMilliseconds:F2}");
            }

            // Hand the persisted folder list to the next projection rebuild so it
            // doesn't have to re-walk Assets/ + every package directory to produce
            // the answer the snapshot already holds.
            _pendingFolderRecords = folderRecords;
        }

        public IReadOnlyList<AssetFolderRecord> GetFolderRecordsForProjection()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(AssetIndexSynchronizer));
            }

            if (_pendingFolderRecords != null)
            {
                // The installed catalog can receive live folder deltas before this
                // one-shot handoff is consumed, so project the current snapshot.
                var records = new List<AssetFolderRecord>(_index.EnumerateFolders());
                _pendingFolderRecords = null;
                return records;
            }

            ZepPerfLog.Count(ZepPerfCounter.FolderTreeWalks);
            return _syncSource.GetFolderRecords();
        }

        private void ApplyRecords(
            SynchronizationOperation operation,
            List<AssetRecord> records,
            uint artifactDependencyVersion,
            IAssetRecordSource enumeratedSource)
        {
            // The record and folder snapshots must come from the same source. In
            // particular, an async Native scan owns the authoritative virtual package
            // roots it just enumerated; asking the sync source for folders can produce a
            // different package set and leave records unreachable in the projection.
            var folderRecords = enumeratedSource.GetFolderRecords();
            ApplyRecords(
                operation,
                records,
                new List<AssetFolderRecord>(folderRecords),
                artifactDependencyVersion);
        }

        private void ApplyRecords(
            SynchronizationOperation operation,
            List<AssetRecord> records,
            List<AssetFolderRecord> folderRecords,
            uint artifactDependencyVersion,
            bool persistSnapshot = true)
        {
            if (!IsCurrent(operation))
            {
                return;
            }

            ZepPerfLog.Count(ZepPerfCounter.FolderTreeWalks);
            _index.Replace(records, folderRecords);
            _pendingFolderRecords = folderRecords;
            // An authoritative walk is a verification and more.
            _unresolvedProofEpoch = 0;
            VerifiedThisProcess = true;

            if (!_enablePersistentCache || !persistSnapshot)
            {
                return;
            }

            var saved = PersistentAssetIndexCache.Save(
                records,
                folderRecords,
                artifactDependencyVersion,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                () => IsCurrent(operation));
            if (!saved && IsCurrent(operation))
            {
                PersistentAssetIndexCache.MarkDirty();
            }
        }

        private sealed class WorkerEnumerationResult
        {
            internal WorkerEnumerationResult(
                List<AssetRecord> records,
                List<AssetFolderRecord> folders,
                VerificationDiff diff)
            {
                Records = records;
                Folders = folders;
                Diff = diff;
            }

            internal List<AssetRecord> Records { get; }

            internal List<AssetFolderRecord> Folders { get; }

            // Null for an authoritative walk.
            internal VerificationDiff Diff { get; }
        }

        private sealed class SynchronizationOperation
        {
            internal SynchronizationOperation(
                int generation,
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
                uint artifactDependencyVersion,
                IReadOnlyList<ProjectAssetChanges> pendingChanges,
                PersistentAssetIndexCache.RecoveryState recoveryState,
                Action onFullScanStarted)
            {
                Generation = generation;
                UnityLabelsByGuid = unityLabelsByGuid;
                ArtifactDependencyVersion = artifactDependencyVersion;
                PendingChanges = pendingChanges == null
                    ? Array.Empty<ProjectAssetChanges>()
                    : new List<ProjectAssetChanges>(pendingChanges);
                RecoveryState = recoveryState;
                OnFullScanStarted = onFullScanStarted;
            }

            internal int Generation { get; }

            internal IReadOnlyDictionary<string, IReadOnlyList<string>> UnityLabelsByGuid { get; }

            internal uint ArtifactDependencyVersion { get; }

            internal IReadOnlyList<ProjectAssetChanges> PendingChanges { get; private set; }

            internal PersistentAssetIndexCache.RecoveryState RecoveryState { get; private set; }

            internal Action OnFullScanStarted { get; }

            // Batches journaled after this operation captured its list are replayed
            // by it too, and its save then retires what it actually replayed.
            internal void AdoptJournal(
                IReadOnlyList<ProjectAssetChanges> pendingChanges,
                PersistentAssetIndexCache.RecoveryState recoveryState)
            {
                PendingChanges = pendingChanges == null
                    ? Array.Empty<ProjectAssetChanges>()
                    : new List<ProjectAssetChanges>(pendingChanges);
                RecoveryState = recoveryState;
            }
        }

        private sealed class SnapshotLoadResult
        {
            internal SnapshotLoadResult(
                bool loaded,
                bool artifactRevisionChanged,
                bool tagStateChanged,
                string cacheSnapshotId,
                List<AssetRecord> records,
                List<AssetFolderRecord> folders)
            {
                Loaded = loaded;
                ArtifactRevisionChanged = artifactRevisionChanged;
                TagStateChanged = tagStateChanged;
                CacheSnapshotId = cacheSnapshotId;
                Records = records;
                Folders = folders;
            }

            internal bool Loaded { get; }

            internal bool ArtifactRevisionChanged { get; }

            internal bool TagStateChanged { get; }

            internal string CacheSnapshotId { get; }

            internal List<AssetRecord> Records { get; }

            internal List<AssetFolderRecord> Folders { get; }
        }

        private sealed class ScanOverlay
        {
            internal ScanOverlay(
                int generation,
                IReadOnlyList<string> removedRoots,
                IReadOnlyList<string> expectedCurrentRoots,
                IReadOnlyList<AssetRecord> recordUpserts,
                IReadOnlyList<AssetFolderRecord> folderUpserts)
            {
                Generation = generation;
                RemovedRoots = removedRoots;
                ExpectedCurrentRoots = expectedCurrentRoots;
                RecordUpserts = recordUpserts;
                FolderUpserts = folderUpserts;
            }

            internal int Generation { get; }

            internal IReadOnlyList<string> RemovedRoots { get; }

            internal IReadOnlyList<string> ExpectedCurrentRoots { get; }

            internal IReadOnlyList<AssetRecord> RecordUpserts { get; }

            internal IReadOnlyList<AssetFolderRecord> FolderUpserts { get; }
        }
    }
}
