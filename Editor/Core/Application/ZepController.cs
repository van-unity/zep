using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using static Zep.Core.Application.ZepPathHelpers;
using static Zep.Core.Application.ZepSelectionResolver;

namespace Zep.Core.Application
{
    // Resolution for a same-named item already in the move destination.
    public enum ProjectMoveConflictResolution
    {
        KeepBoth,
        Replace,
        Skip
    }

    public sealed class ZepController : IDisposable
    {
        private readonly ZepState _viewModel;
        private readonly IProjectAssetGateway _assetGateway;
        private readonly ProjectFileCommandService _fileCommands;
        private readonly InMemoryAssetIndex _index;
        private readonly AssetSearchService _searchService;
        private readonly IAssetRecordSource _source;
        private readonly IDirectFolderAssetSource _directFolderSource;
        private readonly IAssetIndexSynchronizer _synchronizer;
        private readonly TagDefinitionStore _tagDefinitions;
        private readonly IDeferredCallScheduler _deferredScheduler;
        private readonly IShellRevealService _shellReveal;
        private readonly IAddressablesQuery _addressablesQuery;
        private readonly AssetPresentationCache _presentationCache;
        private readonly TagProjectionService _tagProjection;
        private readonly BrowseProjectionService _browseProjection;
        private readonly AssetChangeSyncer _assetChangeSyncer;
        private readonly IndexRefreshScheduler _refreshScheduler;
        private readonly CrossCacheCoordinator _crossCache;
        private readonly FolderEntriesBuilder _folderEntries;
        private readonly TagMutationOrchestrator _tagMutations;
        private readonly Action _deferredStartupCallback;
        private readonly Action _deferredWorkerPollCallback;
        private bool _deferredStartupWorkQueued;
        private bool _deferredStartupWorkUnfinished;
        // Set by an Addressables-only request: the queued run re-reads the
        // entry set but leaves the search postings alone (nothing in them
        // changed). Any other request resets it, so a full run queued on top
        // of an addressables-only one keeps its search build.
        private bool _pendingDeferredStartupSkipsSearchBuild;
        // An Addressables change that arrived while the worker was already
        // expanding the OLD capture; consumed when that worker's result lands.
        private bool _addressablesChangedWhileWorkerRan;
        private IReadOnlyList<string> _capturedAddressableFolderPaths = Array.Empty<string>();
        private Task<DeferredWorkerResult> _deferredWorkerTask;
        private const int MaxDeferredWorkerMismatchRetries = 2;
        private bool _deferredWorkerFaultRetryUsed;
        private int _deferredWorkerMismatchRetries;
        private bool _searchWasUsedInThisProject;
        private readonly Action _onSearchFirstUsedInThisProject;
        private readonly List<string> _pendingImportedPaths = new List<string>();
        private readonly List<string> _pendingDeletedPaths = new List<string>();
        private readonly List<string> _pendingMovedPaths = new List<string>();
        private readonly List<string> _pendingMovedFromPaths = new List<string>();
        private readonly List<ProjectAssetOperationUpdate> _pendingBulkOperationUpdates =
            new List<ProjectAssetOperationUpdate>();
        private readonly List<ProjectAssetChanges> _pendingBulkCallbackChanges =
            new List<ProjectAssetChanges>();
        private bool _pendingBulkStatusPublication;
        private ZepPaneState _expectedPanelSelectionPane;
        private IReadOnlyList<string> _expectedPanelSelectionPaths;
        private ZepPaneState _capturedUndoRedoPane;
        private bool _hasCapturedUndoRedoPane;
        private bool _isApplyingState;
        private bool _isHandlingOperation;
        private int _publishedFolderEntriesVersion = -1;
        // Positive while the shell view (or any external caller) is running a bulk
        // multi-item operation. The cross-cache coordinator short-circuits its
        // projection rebuild while this is non-zero, and RunBulk() performs one final
        // rebuild at depth 0.
        private int _bulkDepth;
        // Nested bulks share one gateway batch so importer pauses stay paired.
        private bool _holdsAssetBatch;
        // The one result a bulk would reveal, if it turns out to be the only
        // one. Inside a bulk it is not yet known whether a second revealable
        // result is coming, and revealing each in turn navigated the pane to
        // every destination and left the last item standing in for the whole
        // batch — so the candidate waits for CompleteBulkOperation to count.
        private ZepPaneState _pendingBulkRevealPane;
        private ProjectFileOperationResult _pendingBulkRevealResult;
        private int _pendingBulkRevealCandidates;
        private bool _panelUseStarted;
        private bool _globalReconciliationComplete;
        private bool _backgroundReconciliationScheduled;
        private bool _indexSynchronizationInProgress;

        // The catalog version the running synchronization started from, and
        // whether a projection cache exists at all. Together they decide whether a
        // completion has anything to rebuild.
        private int _catalogVersionAtSynchronizationStart;

        private bool _hasBuiltProjectionCache;
        private bool _synchronizationPresentationBlocked;
        private bool _refreshStatusPending;
        private bool _sourceSynchronizationFailed;
        private string _indexSynchronizationError;
        public string IndexingError => _indexSynchronizationError;
        private bool _isDisposed;
        private string _statusMessage = "Ready";
        private string _tagStateRecoveryError;
        private bool _tagPresentationReconciliationPending;

        // WHY: Twelve constructor parameters because the controller honestly
        // coordinates twelve real boundaries — six Core services it owns, plus
        // six Unity ports it can't see directly. Hiding any of them behind a
        // bundle struct would push the coupling sideways without removing it
        // (cf. the CrossCacheCoordinator comment for the same rationale).
        // Bridge.ZepControllerHost is the sole production composer;
        // tests build the deps explicitly with stubs at the port seams.
        public ZepController(
            ZepState viewModel,
            IProjectAssetGateway assetGateway,
            ProjectFileCommandService fileCommands,
            IAssetRecordSource source,
            InMemoryAssetIndex index,
            AssetSearchService searchService,
            TagDefinitionStore tagDefinitions,
            IAssetIndexSynchronizer synchronizer,
            IDeferredCallScheduler deferredScheduler,
            IShellRevealService shellReveal,
            IAddressablesQuery addressablesQuery,
            IDirectFolderAssetSource directFolderSource = null,
            bool searchWasUsedInThisProject = false,
            Action onSearchFirstUsedInThisProject = null)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _assetGateway = assetGateway ?? throw new ArgumentNullException(nameof(assetGateway));
            _fileCommands = fileCommands ?? throw new ArgumentNullException(nameof(fileCommands));
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
            _tagDefinitions = tagDefinitions ?? throw new ArgumentNullException(nameof(tagDefinitions));
            _deferredScheduler = deferredScheduler ?? throw new ArgumentNullException(nameof(deferredScheduler));
            _shellReveal = shellReveal ?? throw new ArgumentNullException(nameof(shellReveal));
            _addressablesQuery = addressablesQuery ?? throw new ArgumentNullException(nameof(addressablesQuery));
            _directFolderSource = directFolderSource;
            // 4.32(b): whether this project has ever been searched. The value and
            // its writer come from the composer because the answer has to outlive
            // the editor session that observed it; a caller that supplies neither
            // simply never warms, and the first Search() builds the index lazily.
            _searchWasUsedInThisProject = searchWasUsedInThisProject;
            _onSearchFirstUsedInThisProject = onSearchFirstUsedInThisProject;

            _tagProjection = new TagProjectionService(_assetGateway, _tagDefinitions);
            _presentationCache = new AssetPresentationCache(_assetGateway);
            _browseProjection = new BrowseProjectionService(_index, _searchService, _tagProjection);
            _folderEntries = new FolderEntriesBuilder(_index);
            // WHY: Three coordinators with disjoint responsibilities. The first two
            // (sync, scheduler) stay under five parameters because they own narrow,
            // local state. CrossCacheCoordinator deliberately takes more — see its
            // class-level comment for the full rationale.
            _refreshScheduler = new IndexRefreshScheduler(RefreshIndex, _deferredScheduler);
            _assetChangeSyncer = new AssetChangeSyncer(
                _deferredScheduler,
                // The lambda reads _crossCache at call time (runtime, after this
                // ctor completes and the field below is assigned), so the syncer
                // can delegate to a coordinator built after it — no reorder needed.
                tryApplyIncremental: (ProjectAssetChanges changes, out IReadOnlyList<string> unresolvedPaths) =>
                    ApplyExternalChanges(changes, out unresolvedPaths),
                onApplyProjectionOnly: ApplyProjectionOnly,
                onFullRefreshNeeded: RequestBackgroundReconciliation);
            _crossCache = new CrossCacheCoordinator(
                _index,
                _searchService,
                _source,
                _assetGateway,
                _tagDefinitions,
                _presentationCache,
                _tagProjection,
                _browseProjection,
                _folderEntries,
                HandleCatalogChanged);
            _tagMutations = new TagMutationOrchestrator(
                _assetGateway,
                _fileCommands,
                _crossCache,
                _tagDefinitions,
                SetStatus,
                ApplyProjection,
                () => { SyncTagAssignmentsIfNeeded(); },
                ReportTagPresentationUnavailable);
            _deferredStartupCallback = RunDeferredStartupWork;
            _deferredWorkerPollCallback = PollDeferredWorker;

            _viewModel.PaneChanged += HandlePaneChanged;
            _addressablesQuery.Changed += HandleAddressablesChanged;
        }

        internal void InitializeIndex()
        {
            BeginPanelUse();
        }

        // The reload-resident host owns history and callbacks, but project-wide
        // reconciliation is a panel-use concern. A direct observation is applied
        // first so the initial folder can paint from bounded filesystem facts;
        // cache validation and the global scan begin on the following editor tick.
        public void BeginPanelUse()
        {
            if (_isDisposed)
            {
                return;
            }

            if (_panelUseStarted)
            {
                if (_sourceSynchronizationFailed && !_indexSynchronizationInProgress)
                {
                    _deferredScheduler.Schedule(RunDeferredHydration);
                }

                return;
            }

            _panelUseStarted = true;
            // Defer hydration so Host.Create and the shell's first paint are not
            // blocked by signature validation, cache deserialization, or a cold
            // filesystem scan. The synchronizer owns the cache decision and raises
            // the scan-start callback only on a real cache miss; a domain reload
            // that successfully restores the saved index must not show the cold-scan
            // overlay.
            // Every `__sw_*` in this file is null unless perf logging is on: the
            // log line was already guarded, but the Stopwatch itself was allocated
            // on measured paths whether or not anything would ever read it. A null
            // watch is the guard AND the "was it running" answer, so the reader
            // below needs one condition instead of two.
            var __sw_init = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            _presentationCache.Clear();
            if (_directFolderSource != null)
            {
                try
                {
                    ObserveDirectFolder("Packages");
                    var restoredFolderPath =
                        _viewModel.PrimaryPane.CurrentFolderPath.Value;
                    ObservePrimaryFolderOrAssetsFallback(restoredFolderPath);
                    ApplyProjection();
                }
                catch (ProjectTagStateUnavailableException exception)
                {
                    _hasBuiltProjectionCache = false;
                    _synchronizationPresentationBlocked = true;
                    ReportTagPresentationUnavailable(exception);
                }
            }
            _deferredScheduler.Schedule(RunDeferredHydration);
            if (__sw_init != null)
            {
                __sw_init.Stop();
                ZepPerfLog.Log($"[Zep/perf] InitializeIndex (deferred hydration scheduled) ms={__sw_init.Elapsed.TotalMilliseconds:F2}");
            }
        }

        private void RunDeferredHydration()
        {
            if (_isDisposed
                || _globalReconciliationComplete
                || _indexSynchronizationInProgress)
            {
                return;
            }

            var __sw_sync = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            var completionEntered = false;
            _indexSynchronizationInProgress = true;
            BeginIndexSynchronization();
            try
            {
                SynchronizeAsyncWithOutcome(
                    onFullScanStarted: () =>
                    {
                        // The one producer of the cold-scan overlay. Counted so a
                        // headless reload scenario can prove the overlay never
                        // appeared, which the view model alone cannot: the flag is
                        // set and cleared inside the same hydration the test waits on.
                        ZepPerfLog.Count(ZepPerfCounter.ColdScanOverlays);
                        if (!_isDisposed)
                        {
                            _viewModel.SetIsIndexScanInProgress(true);
                        }
                    },
                    onComplete: result =>
                    {
                        completionEntered = true;
                        if (__sw_sync != null)
                        {
                            __sw_sync.Stop();
                            ZepPerfLog.Log($"[Zep/perf] _synchronizer.SynchronizeAsync (until callback fires) ms={__sw_sync.Elapsed.TotalMilliseconds:F2} indexCount={_index.Count}");
                        }

                        CompleteIndexSynchronization(result);
                    });
            }
            catch (Exception exception)
            {
                if (!completionEntered)
                {
                    HandleIndexSynchronizationFailure(
                        exception.GetType().Name + ": " + exception.Message);
                }

                throw;
            }
        }

        private bool ObserveDirectFolder(string folderPath)
        {
            if (_directFolderSource == null)
            {
                return false;
            }

            folderPath = AssetPathFormat.Normalize(folderPath);
            if (string.IsNullOrEmpty(folderPath))
            {
                return false;
            }

            var versionAtStart = _index.Version;
            var observation = _directFolderSource.Observe(AssetPath.From(folderPath));
            // The index records the folder itself (I2); the controller keeps no
            // parallel set. The bool is only "did the source find the folder",
            // which is what the Assets fallback below turns on.
            _crossCache.ApplyDirectFolderObservation(observation, versionAtStart);
            return observation != null && observation.IsComplete;
        }

        private void ObservePrimaryFolderOrAssetsFallback(string folderPath)
        {
            if (ObserveDirectFolder(folderPath))
            {
                return;
            }

            // A serialized folder can disappear while Unity reloads the domain.
            // Populate the fallback itself before projection so the first useful
            // paint is Assets, not "Assets — 0 files" until reconciliation ends.
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] direct-observation fallback folder={folderPath} -> Assets");
            ObserveDirectFolder("Assets");
        }

        internal bool CanUndo => _fileCommands.CanUndo;

        internal bool CanRedo => _fileCommands.CanRedo;

        // True while ApplyProjection is mutating the view model. The shell uses
        // this to skip the recursive RefreshView fired by ApplyViewModelState's
        // deferral-end: without it every state change paints the shell twice.
        // Why: state.PaneChanged dispatches to subscribers in order; the controller
        // is subscribed first, runs ApplyProjection, which re-fires PaneChanged
        // synchronously before the outer dispatch reaches the shell. The shell
        // would then run once during the inner fire and once during the outer.
        // Checking this flag drops the inner fire — the outer one still runs
        // and sees the fully-applied state.
        internal bool IsApplyingState => _isApplyingState;

        // True only while HandlePaneChanged is running — i.e. we are reacting
        // to an outer pane-change dispatch from the view model, and the
        // outer dispatch will continue to its remaining subscribers (the shell)
        // after this returns. The shell uses this to drop the recursive inner
        // PaneChanged re-fire from ApplyViewModelState's deferral-end.
        //
        // IsApplyingState alone is too aggressive: when the controller invokes
        // ApplyProjection directly (e.g. after AddTag / RemoveTag / Refresh)
        // there is no outer PaneChanged dispatch coming, so the shell must run
        // its single RefreshView from the deferral-end PaneChanged. Gating on
        // *being in HandlePaneChanged* — not just on *running ApplyProjection*
        // — is what distinguishes the two cases.
        public bool IsReactingToViewModelChange => _isReactingToViewModelChange;

        private bool _isReactingToViewModelChange;

        // Fired after RebuildProjectionCache (full or incremental). UI-side
        // caches that key off the asset index — most importantly the icon
        // repository's folder-empty probe results — subscribe here to drop
        // their stale entries. This is the only signal that crosses the
        // Core→UI seam for index changes; the projection itself flows through
        // the view model's structured change events.
        public event Action<AssetCatalogDelta> AssetCatalogChanged;

        // A completed rename or move, as previous → new path. Path-keyed state that
        // the controller does not own has to follow it, and the sidebar's favorites
        // are exactly that: their store is an editor ScriptableSingleton reached
        // through the shared Bridge host. Owned results and completed external
        // folder moves publish here; create/delete and callback echoes do not.
        public event Action<string, string> AssetPathRewritten;

        // Restoration must not treat the intentionally empty pre-hydration
        // folder/tag projections as authoritative deletion evidence.
        public bool IsPaneSourceReconciliationReady { get; private set; }

        public event Action PaneSourceReconciliationReady;

        // Raised by RevealAndFrameAsset once the browser has navigated to and
        // selected an externally-pinged asset. The shell listens and scrolls the
        // path into view — the selection is applied synchronously through the
        // view model's PaneChanged dispatch before this fires, so the active list is
        // already repopulated when the view frames the path. Carries the asset
        // path (not the folder) as a plain string; Core stays Unity-free.
        public event Action<ZepPaneState, string> AssetFrameRequested;

        // Emptiness is a fact, not a default: false here means "not known to be
        // empty", which covers both a folder with children and a folder the
        // index has not read yet. The UI asks per row instead of holding a copy
        // of a set, so a folder stops reading as empty the moment the index
        // learns what is in it - no push, no refresh window.
        public bool IsFolderKnownEmpty(string folderPath) =>
            _index.IsFolderKnownEmpty(folderPath);

        public IReadOnlyList<ZepFolderEntry> GetVisibleFolderChildren(
            string parentFolder)
        {
            if (_synchronizationPresentationBlocked)
            {
                throw new ProjectTagStateUnavailableException(_tagStateRecoveryError);
            }

            parentFolder = AssetPathFormat.Normalize(parentFolder);
            try
            {
                ObserveDirectFolderIfNeeded(parentFolder);
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                _hasBuiltProjectionCache = false;
                _synchronizationPresentationBlocked = true;
                ReportTagPresentationUnavailable(exception);
                throw;
            }

            return _folderEntries.GetVisibleFolderChildren(parentFolder);
        }

        public ZepFolderEntry GetVisibleFolderEntry(string folderPath)
        {
            if (_synchronizationPresentationBlocked)
            {
                throw new ProjectTagStateUnavailableException(_tagStateRecoveryError);
            }

            folderPath = AssetPathFormat.Normalize(folderPath);
            try
            {
                ObserveDirectFolderIfNeeded(
                    ZepPathHelpers.GetParentFolder(folderPath));
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                _hasBuiltProjectionCache = false;
                _synchronizationPresentationBlocked = true;
                ReportTagPresentationUnavailable(exception);
                throw;
            }

            return _folderEntries.TryGetVisibleFolderEntry(folderPath, out var entry)
                ? entry
                : null;
        }

        private void ObserveDirectFolderIfNeeded(string folderPath)
        {
            if (_panelUseStarted
                && !_globalReconciliationComplete
                && !string.IsNullOrEmpty(folderPath)
                && !_index.IsObservedFolder(folderPath))
            {
                ObserveDirectFolder(folderPath);
            }
        }

        // The projections feed the AssetsArea features after Step 7. The shell
        // wires each feature with the projection it pulls from on every refresh
        // tick; the controller no longer routes asset lists through the view
        // model. Browse handles both the Browser and Tags features (Tags is a
        // tag-filtered Browse view).
        public BrowseProjectionService BrowseProjection => _browseProjection;

        // The dialog queries the same projections without publishing them to a
        // pane, which would reconcile its browsing selection on every keystroke.
        public IReadOnlyList<ZepAssetEntry> GetSearchResults(
            ZepPaneState pane,
            string query,
            ZepSearchScope scope,
            string typeFilter = null,
            string tagFilter = null)
        {
            if (pane == null) return Array.Empty<ZepAssetEntry>();
            var currentFolderPath = AssetPathFormat.Normalize(pane.CurrentFolderPath);
            var selectedTypeFilter = typeFilter ?? ResolveSelectedTypeFilter(pane.SelectedTypeFilter, currentFolderPath);
            var selectedTagFilter = tagFilter ?? _tagProjection.ResolveSelectedTagFilterId(pane.SelectedTagFilterId);
            if (string.IsNullOrWhiteSpace(query))
            {
                if (string.IsNullOrEmpty(selectedTypeFilter) && string.IsNullOrEmpty(selectedTagFilter))
                    return Array.Empty<ZepAssetEntry>();
                // A filter-only finder query is still a recursive search in its
                // explicit scope; an empty browse query would only list one folder.
                query = "a:all";
            }
            MarkSearchUsedInThisProject();
            var results = _browseProjection.BuildAssetEntries(
                currentFolderPath, query, scope, selectedTypeFilter, selectedTagFilter,
                pane.BreadcrumbRootPath.IsEmpty ? null : pane.BreadcrumbRootPath.Value);
            var parsed = _browseProjection.ParseSearchText(query);
            if (results is LazyAssetEntryList lazy)
            {
                lazy.RankByName(parsed);
                return lazy;
            }
            var ranked = new List<(ZepAssetEntry entry, int rank)>(results.Count);
            foreach (var entry in results) ranked.Add((entry, SearchQuery.NameMatchRank(entry.Name, parsed)));
            ranked.Sort((a, b) =>
            {
                var rank = a.rank.CompareTo(b.rank);
                if (rank != 0) return rank;
                var name = string.Compare(a.entry.Name, b.entry.Name, StringComparison.OrdinalIgnoreCase);
                return name != 0 ? name : string.Compare(a.entry.Path, b.entry.Path, StringComparison.OrdinalIgnoreCase);
            });
            var ordered = new ZepAssetEntry[ranked.Count];
            for (var i = 0; i < ranked.Count; i++) ordered[i] = ranked[i].entry;
            return ordered;
        }

        public IReadOnlyList<AssetSearchSuggestion> GetSearchSuggestions(
            ZepPaneState pane,
            string query,
            int limit = 8,
            ZepSearchScope? scope = null,
            string typeFilter = null,
            string tagFilter = null)
        {
            if (pane == null
                || limit <= 0
                || string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<AssetSearchSuggestion>();
            }

            var currentFolderPath = AssetPathFormat.Normalize(pane.CurrentFolderPath);

            var selectedSearchScope = scope ?? pane.SelectedSearchScope;
            var searchScopePath = ResolveSearchScopePath(
                selectedSearchScope,
                currentFolderPath);
            if (!pane.BreadcrumbRootPath.IsEmpty)
            {
                var rootFolderPath = pane.BreadcrumbRootPath.Value;
                searchScopePath = selectedSearchScope == ZepSearchScope.CurrentFolder
                    && IsWithinFolderScope(currentFolderPath, rootFolderPath)
                        ? currentFolderPath
                        : rootFolderPath;
            }

            var selectedTypeFilter = typeFilter ?? ResolveSelectedTypeFilter(
                pane.SelectedTypeFilter,
                currentFolderPath);
            var selectedTagFilterId = tagFilter ?? _tagProjection.ResolveSelectedTagFilterId(
                pane.SelectedTagFilterId);
            var candidateLimit = string.IsNullOrEmpty(selectedTagFilterId)
                ? limit
                : limit * 4;
            // The browse projection parsed this same text a moment ago for the
            // keystroke's own search; borrow those tokens instead of parsing
            // the string a second time on the typing path.
            var suggestions = _searchService.Suggest(
                _browseProjection.ParseSearchText(query),
                searchScopePath,
                selectedTypeFilter,
                candidateLimit);
            if (string.IsNullOrEmpty(selectedTagFilterId)
                || suggestions.Count == 0)
            {
                return suggestions;
            }

            var filtered = new List<AssetSearchSuggestion>(Math.Min(limit, suggestions.Count));
            for (var i = 0; i < suggestions.Count && filtered.Count < limit; i++)
            {
                if (_tagProjection.MatchesTagFilter(
                    suggestions[i].Asset.AssetPath,
                    selectedTagFilterId))
                {
                    filtered.Add(suggestions[i]);
                }
            }

            return filtered.Count == 0
                ? Array.Empty<AssetSearchSuggestion>()
                : filtered;
        }

        public bool CanWrite(AssetPath assetPath)
        {
            return _assetGateway.CanWrite(assetPath);
        }

        internal bool CanTag(AssetPath assetPath)
        {
            return _assetGateway.CanTag(assetPath);
        }

        public bool IsLivePane(ZepPaneState pane) =>
            _viewModel.IsLivePane(pane);

        public void Refresh()
        {
            _refreshScheduler.Cancel();
            RefreshIndex(true);
        }

        public bool OpenAsset(AssetPath assetPath)
        {
            var succeeded = _assetGateway.Open(assetPath);
            SetStatus(succeeded ? "Opened " + AssetPathFormat.GetFileName(assetPath) : "Unable to open " + assetPath);
            ApplyProjection();
            return succeeded;
        }

        public void Reveal(AssetPath assetPath)
        {
            _assetGateway.Reveal(assetPath);
            SetStatus("Revealed " + AssetPathFormat.GetFileName(assetPath));
            ApplyProjection();
        }

        // Record recipe-created assets so Undo removes them and Redo restores their contents.
        public bool RecordCreatedAsset(AssetPath createdPath)
        {
            if (createdPath.IsEmpty)
            {
                return false;
            }

            var result = _fileCommands.RecordCreatedAsset(createdPath);
            if (!result.Succeeded)
            {
                SetStatus(result.ErrorMessage);
                ApplyProjection();
                return false;
            }

            return true;
        }

        public bool AcknowledgeAssetPath(AssetPath assetPath)
        {
            if (assetPath.IsEmpty)
            {
                return false;
            }

            if (_bulkDepth > 0)
            {
                _pendingBulkOperationUpdates.Add(
                    new ProjectAssetOperationUpdate(assetPath.Value, string.Empty));
                AccumulatePendingAssetChanges(
                    new ProjectAssetChanges(
                        new[] { assetPath.Value },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                return true;
            }

            var sourceChanges = new ProjectAssetChanges(
                new[] { assetPath.Value },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());
            if (_synchronizationPresentationBlocked)
            {
                AccumulatePendingAssetChanges(sourceChanges);
                return false;
            }

            try
            {
                var result = ApplyExternalChanges(
                    sourceChanges,
                    out var unresolvedPaths);
                if (unresolvedPaths.Count > 0)
                {
                    return false;
                }

                if (result == ExternalChangeApplicationResult.Changed)
                {
                    AccumulatePendingAssetChanges(sourceChanges);
                    var tagVersion = _assetGateway.TagVersion;
                    EnsureSharedProjectionCaches(tagVersion);
                    var pendingChanges = TakePendingAssetChanges();
                    ApplyProjection(pendingChanges, sharedCachesAreCurrent: true);
                    return true;
                }
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                AccumulatePendingAssetChanges(sourceChanges);
                _synchronizationPresentationBlocked = true;
                _hasBuiltProjectionCache = false;
                _globalReconciliationComplete = false;
                ReportTagPresentationUnavailable(exception);
                return false;
            }

            ZepPerfLog.Count(ZepPerfCounter.DeduplicatedAcknowledgements);
            return true;
        }

        public bool CreateFolder(
            ZepPaneState pane,
            AssetPath parentFolderPath,
            string folderName)
        {
            return ExecuteOperation(
                pane,
                () => _fileCommands.CreateFolder(parentFolderPath, folderName),
                true,
                "Created folder");
        }

        public void RunBulk(Action work)
            => RunBulkCore(work, pauseAssetEditing: true);

        public void RunCatalogBatch(Action work)
            => RunBulkCore(work, pauseAssetEditing: false);

        private void RunBulkCore(Action work, bool pauseAssetEditing)
        {
            if (work == null)
            {
                return;
            }

            var isOutermost = _bulkDepth == 0;
            if (isOutermost)
            {
                _fileCommands.BeginExecutionGroup();
            }
            _bulkDepth++;
            try
            {
                // The batch already defers the PROJECTION rebuild, but each
                // item's own view-model mutation (an expansion-key rewrite, the
                // per-item reveal) still opened and closed its own deferral at
                // depth 0 and published — a 5,000-item move fired ~10,000
                // PaneChanged dispatches, each one a shell refresh that re-binds
                // the whole folder, before the single batch projection. One
                // deferral around the batch coalesces them; the batch's own
                // publication is the ApplyProjection in CompleteBulkOperation,
                // which runs after this scope closes.
                using (_viewModel.DeferNotifications())
                {
                    if (pauseAssetEditing && !_holdsAssetBatch)
                    {
                        _holdsAssetBatch = true;
                        _assetGateway.BeginBatch();
                        try
                        {
                            work();
                        }
                        finally
                        {
                            _holdsAssetBatch = false;
                            _assetGateway.EndBatch();
                        }
                    }
                    else
                    {
                        work();
                    }
                }
            }
            finally
            {
                _bulkDepth--;
                if (isOutermost)
                {
                    _fileCommands.EndExecutionGroup();
                    CompleteBulkOperation();
                }
            }
        }

        private void CompleteBulkOperation()
        {
            // Exactly one revealable result means this "batch" was a single
            // operation (a one-item drop, Cmd+D on one asset) and its reveal is
            // the same one it would have got outside a bulk. Two or more means a
            // real batch, whose selection belongs to the caller.
            var revealPane = _pendingBulkRevealPane;
            var revealResult = _pendingBulkRevealCandidates == 1
                ? _pendingBulkRevealResult
                : null;
            _pendingBulkRevealPane = null;
            _pendingBulkRevealResult = null;
            _pendingBulkRevealCandidates = 0;

            var callbackChanges = _pendingBulkCallbackChanges.ToArray();
            _pendingBulkCallbackChanges.Clear();

            if (_isDisposed)
            {
                _pendingBulkOperationUpdates.Clear();
                _pendingBulkStatusPublication = false;
                TakePendingAssetChanges();
                return;
            }

            var hasPendingSourceChanges = _pendingImportedPaths.Count > 0
                || _pendingDeletedPaths.Count > 0
                || _pendingMovedPaths.Count > 0;
            if (_pendingBulkOperationUpdates.Count == 0
                && callbackChanges.Length == 0
                && !hasPendingSourceChanges
                && !_pendingBulkStatusPublication)
            {
                return;
            }

            if (_synchronizationPresentationBlocked)
            {
                _pendingBulkOperationUpdates.Clear();
                _pendingBulkStatusPublication = false;
                return;
            }

            ProjectAssetChanges? sourceChanges;
            var requiresReconciliation = false;
            try
            {
                var tagVersion = _assetGateway.TagVersion;
                if (_pendingBulkOperationUpdates.Count > 0)
                {
                    using (ZepPerfLog.Measure(
                        "bulk-operation",
                        ZepPerfStage.CatalogMutation))
                    {
                        _crossCache.ApplyIncrementalOperationUpdates(
                            _pendingBulkOperationUpdates);
                    }
                }

                for (var i = 0; i < callbackChanges.Length; i++)
                {
                    var result = ApplyExternalChanges(
                        callbackChanges[i],
                        out var unresolvedPaths);

                    if (unresolvedPaths.Count == 0
                        && (result == ExternalChangeApplicationResult.Changed
                            || result == ExternalChangeApplicationResult.NoChanges))
                    {
                        _assetChangeSyncer.AcknowledgeOwnedAssetBatch();
                    }
                    else
                    {
                        requiresReconciliation = true;
                    }
                }

                EnsureSharedProjectionCaches(tagVersion);
                sourceChanges = TakePendingAssetChanges();
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                _synchronizationPresentationBlocked = true;
                _hasBuiltProjectionCache = false;
                _globalReconciliationComplete = false;
                ReportTagPresentationUnavailable(exception);
                return;
            }
            finally
            {
                _pendingBulkOperationUpdates.Clear();
                _pendingBulkStatusPublication = false;
            }

            if (requiresReconciliation)
            {
                RequestBackgroundReconciliation();
            }

            // The reveal and the projection are one publication: separately they
            // dispatched twice for what the user sees as one action.
            using (_viewModel.DeferNotifications())
            {
                if (revealResult != null)
                {
                    RevealOperationResult(revealPane, revealResult);
                }

                ApplyProjection(sourceChanges, sharedCachesAreCurrent: true);
            }
        }

        public bool Rename(
            ZepPaneState pane,
            AssetPath assetPath,
            string newName)
        {
            return ExecuteOperation(
                pane,
                () => _fileCommands.Rename(assetPath, newName),
                true,
                "Renamed item");
        }

        public bool Duplicate(ZepPaneState pane, AssetPath assetPath)
        {
            return ExecuteOperation(
                pane,
                () => _fileCommands.Duplicate(assetPath),
                true,
                "Duplicated item");
        }

        public bool Compress(IReadOnlyList<AssetPath> assetPaths, string archiveAbsolutePath, bool includeMeta)
        {
            if (assetPaths == null || assetPaths.Count == 0)
            {
                SetStatus("Choose at least one item to compress.");
                ApplyProjection();
                return false;
            }

            if (string.IsNullOrWhiteSpace(archiveAbsolutePath))
            {
                return false;
            }

            // Reserve original roots so generated suffixes cannot take another selected name.
            var reservedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < assetPaths.Count; i++)
            {
                var originalName = AssetPathFormat.GetFileName(assetPaths[i]);
                reservedEntryNames.Add(originalName);
                if (includeMeta) reservedEntryNames.Add(originalName + ".meta");
            }

            var allocatedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entryNamesByAssetPath = new Dictionary<AssetPath, string>();
            var sources = new List<ZipSourceItem>(assetPaths.Count * 2);
            for (var i = 0; i < assetPaths.Count; i++)
            {
                var assetPath = assetPaths[i];
                var sourceAbsolute = _assetGateway.ResolveAbsolutePath(assetPath);
                if (string.IsNullOrEmpty(sourceAbsolute) || (!File.Exists(sourceAbsolute) && !Directory.Exists(sourceAbsolute)))
                {
                    SetStatus("Item no longer exists: " + assetPath);
                    ApplyProjection();
                    return false;
                }

                if (!entryNamesByAssetPath.TryGetValue(assetPath, out var entryName))
                {
                    entryName = AssetPathFormat.GetFileName(assetPath);
                    if (allocatedEntryNames.Contains(entryName)
                        || (includeMeta && allocatedEntryNames.Contains(entryName + ".meta")))
                    {
                        var isFolder = _assetGateway.IsFolder(assetPath);
                        var stem = isFolder ? entryName : Path.GetFileNameWithoutExtension(entryName);
                        var extension = isFolder ? string.Empty : Path.GetExtension(entryName);
                        var suffix = 1;
                        do
                        {
                            entryName = stem + " " + suffix++ + extension;
                        }
                        while (reservedEntryNames.Contains(entryName)
                            || allocatedEntryNames.Contains(entryName)
                            || (includeMeta && (reservedEntryNames.Contains(entryName + ".meta")
                                || allocatedEntryNames.Contains(entryName + ".meta"))));
                    }

                    entryNamesByAssetPath.Add(assetPath, entryName);
                    allocatedEntryNames.Add(entryName);
                    if (includeMeta) allocatedEntryNames.Add(entryName + ".meta");
                }
                sources.Add(new ZipSourceItem(sourceAbsolute, entryName));

                // Include the sibling .meta so the archive round-trips as a
                // Unity asset bundle. When the caller opted to skip .meta we
                // neither add the sibling here nor let the writer pick up nested
                // .meta during the folder walk (see the includeMeta flag passed
                // to Write below).
                if (includeMeta)
                {
                    var metaAbsolute = sourceAbsolute + ".meta";
                    if (File.Exists(metaAbsolute))
                    {
                        sources.Add(new ZipSourceItem(metaAbsolute, entryName + ".meta"));
                    }
                }
            }

            try
            {
                ZipArchiveWriter.Write(archiveAbsolutePath, sources, includeMeta);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to compress: " + ex.Message);
                ApplyProjection();
                return false;
            }

            _shellReveal.Reveal(archiveAbsolutePath);

            var archiveName = Path.GetFileName(archiveAbsolutePath);
            SetStatus(assetPaths.Count == 1
                ? "Compressed to " + archiveName
                : "Compressed " + assetPaths.Count + " items to " + archiveName);
            ApplyProjection();
            return true;
        }

        internal bool Move(
            ZepPaneState pane,
            AssetPath assetPath,
            AssetPath destinationFolderPath)
        {
            return ExecuteMove(pane, assetPath, destinationFolderPath, null);
        }

        // The sources of `assetPaths` that already have a same-named item in
        // destinationFolderPath, in the order they were given. A pure query —
        // the drop asks it BEFORE moving anything so it can offer one choice
        // for the whole drop instead of letting each item fail on its own.
        // An item already sitting in the destination folder is not a collision
        // with itself: moving it there is a no-op, not something to resolve.
        internal IReadOnlyList<AssetPath> FindMoveCollisions(
            IReadOnlyList<AssetPath> assetPaths,
            AssetPath destinationFolderPath)
        {
            if (assetPaths == null || assetPaths.Count == 0 || destinationFolderPath.IsEmpty)
            {
                return Array.Empty<AssetPath>();
            }

            List<AssetPath> collisions = null;
            for (var i = 0; i < assetPaths.Count; i++)
            {
                var assetPath = assetPaths[i];
                if (assetPath.IsEmpty
                    || !IsOccupiedMoveDestination(assetPath, destinationFolderPath, out _))
                {
                    continue;
                }

                collisions = collisions ?? new List<AssetPath>();
                collisions.Add(assetPath);
            }

            return (IReadOnlyList<AssetPath>)collisions ?? Array.Empty<AssetPath>();
        }

        // Move one item into a folder that already holds a same-named item, the
        // way the user chose. The resolution only applies to an actual
        // collision: an item with nothing in its way just moves, so a caller
        // can drive a whole drop through this one entry point.
        //
        // Skip reports success with nothing done — it is the user declining
        // this item, not a failure. A caller that summarises a batch counts
        // skips from the collision list it already asked for, since it applies
        // one resolution to all of them.
        internal bool MoveWithConflictResolution(
            ZepPaneState pane,
            AssetPath assetPath,
            AssetPath destinationFolderPath,
            ProjectMoveConflictResolution resolution)
        {
            if (!IsOccupiedMoveDestination(assetPath, destinationFolderPath, out var occupantPath))
            {
                return ExecuteMove(pane, assetPath, destinationFolderPath, null);
            }

            switch (resolution)
            {
                case ProjectMoveConflictResolution.Skip:
                    return true;
                case ProjectMoveConflictResolution.KeepBoth:
                    var freeName = FindFreeMoveName(assetPath, destinationFolderPath);
                    if (string.IsNullOrEmpty(freeName))
                    {
                        SetStatus(
                            "No free name is available for '"
                            + AssetPathFormat.GetFileName(assetPath)
                            + "' in " + destinationFolderPath + ".");
                        ApplyProjection();
                        return false;
                    }

                    return ExecuteMove(pane, assetPath, destinationFolderPath, freeName);
                case ProjectMoveConflictResolution.Replace:
                    return ReplaceAtMoveDestination(
                        pane, assetPath, destinationFolderPath, occupantPath);
                default:
                    SetStatus("Unknown move-conflict resolution.");
                    ApplyProjection();
                    return false;
            }
        }

        private bool ExecuteMove(
            ZepPaneState pane,
            AssetPath assetPath,
            AssetPath destinationFolderPath,
            string destinationFileName)
        {
            return ExecuteOperation(
                pane,
                () => _fileCommands.Move(assetPath, destinationFolderPath, destinationFileName),
                true,
                "Moved item");
        }

        // Bound occupancy probes in a destination already full of copies.
        private const int MaxMoveCopySuffix = 1000;

        // Displacement and move share a history group, so one Undo restores both items.
        private bool ReplaceAtMoveDestination(
            ZepPaneState pane,
            AssetPath assetPath,
            AssetPath destinationFolderPath,
            AssetPath occupantPath)
        {
            var replaced = false;
            RunBulk(() =>
            {
                ProjectFileOperationResult displacement = null;
                if (!ExecuteOperation(
                    pane,
                    () => displacement = _fileCommands.Delete(occupantPath),
                    false,
                    "Deleted item"))
                {
                    return;
                }

                replaced = Move(pane, assetPath, destinationFolderPath);
                if (replaced)
                {
                    return;
                }

                var failureReason = _statusMessage;
                if (Undo(pane))
                {
                    SetStatus(failureReason);
                    return;
                }

                var rollbackReason = _statusMessage;
                SetStatus(
                    "'" + occupantPath + "' could not be replaced. " + failureReason
                    + " Restoring the displaced item failed: " + rollbackReason
                    + " Copy the saved item at '" + displacement.AssetPath
                    + "' and its sibling .meta file to a safe location for manual recovery.");
            });

            return replaced;
        }

        private string FindFreeMoveName(AssetPath assetPath, AssetPath destinationFolderPath)
        {
            var fileName = AssetPathFormat.GetFileName(assetPath);
            var isFolder = _assetGateway.IsFolder(assetPath);
            var stem = isFolder ? fileName : AssetPathFormat.GetNameWithoutExtension(assetPath);
            var extension = isFolder ? string.Empty : AssetPathFormat.GetExtension(assetPath);
            for (var suffix = 1; suffix <= MaxMoveCopySuffix; suffix++)
            {
                var candidateName = stem + " " + suffix + extension;
                var candidate = AssetPath.From(
                    AssetPathFormat.Combine(destinationFolderPath, candidateName));
                if (!_assetGateway.Exists(candidate))
                {
                    return candidateName;
                }
            }

            return string.Empty;
        }

        private bool IsOccupiedMoveDestination(
            AssetPath assetPath,
            AssetPath destinationFolderPath,
            out AssetPath occupantPath)
        {
            occupantPath = AssetPath.From(AssetPathFormat.Combine(
                destinationFolderPath,
                AssetPathFormat.GetFileName(assetPath)));
            return ProjectFileCommandService.IsOccupiedMoveDestination(
                _assetGateway, assetPath, occupantPath);
        }

        public bool Delete(ZepPaneState pane, AssetPath assetPath)
        {
            return ExecuteOperation(
                pane,
                () => _fileCommands.Delete(assetPath),
                false,
                "Deleted item");
        }

        // Delete every path in one bulk operation, then move the selection to the
        // nearest surviving neighbor in the current display order — the item that
        // slides up into the first deleted slot, else the item just before it, else
        // nothing when the container is now empty (Finder/Explorer/GNOME behavior).
        // The neighbor is captured BEFORE the delete: ApplyProjection only prunes
        // the vanished paths, so without this the selection would just go empty.
        public void DeleteAssetsSelectingNeighbor(
            ZepPaneState pane,
            IReadOnlyList<AssetPath> paths)
        {
            if (!_viewModel.IsLivePane(pane) || paths == null || paths.Count == 0)
            {
                return;
            }

            // The neighbor is resolved from the FULL selection: every selected
            // path leaves the view, whether it is deleted directly or taken by
            // the folder above it, so none of them may be picked as the survivor.
            var neighborPath = ResolvePostDeleteNeighborPath(pane, paths);

            var targetPaths = CollapseNestedPaths(
                ToStringPaths(paths),
                candidate => _folderEntries.TryGetVisibleFolderEntry(candidate, out _));

            var deleted = 0;
            var failed = 0;
            var firstFailureReason = string.Empty;
            RunBulk(() =>
            {
                for (var i = 0; i < targetPaths.Count; i++)
                {
                    // Continue, don't break: one item refusing to delete (a
                    // read-only file, a locked payload) must not silently abandon
                    // the rest of the selection. Each delete is independent, so
                    // delete everything that can be deleted.
                    if (Delete(pane, AssetPath.From(targetPaths[i])))
                    {
                        deleted++;
                        continue;
                    }

                    failed++;
                    if (failed == 1)
                    {
                        firstFailureReason = _statusMessage;
                    }
                }
            });

            // Each item's own status overwrote the previous one, so a partly
            // failed batch showed whichever message happened to come last —
            // often a success line hiding several refusals. Report it once.
            var hasAggregateStatus = failed > 0 && targetPaths.Count > 1;
            if (hasAggregateStatus)
            {
                SetStatus(
                    $"Deleted {deleted} of {targetPaths.Count} — {failed} failed; first: {firstFailureReason}");
            }

            if (string.IsNullOrEmpty(neighborPath))
            {
                if (hasAggregateStatus)
                {
                    // The neighbor block below is what normally publishes the
                    // status; with nothing to select it has to be published here
                    // or the aggregate never reaches the user.
                    ApplyProjection(pane);
                }

                return;
            }

            ApplyPaneState(pane, originatingPane =>
            {
                originatingPane.SetSelectedAssetPaths(new[] { AssetPath.From(neighborPath) });
            });
            ApplyProjection(pane);
        }

        // The path that should hold the selection after `deletedPaths` are removed
        // from the current display order: the first survivor at or after the
        // smallest deleted index, else the nearest survivor before it. Empty when
        // nothing survives or the display order is unavailable.
        private static string ResolvePostDeleteNeighborPath(
            ZepPaneState pane,
            IReadOnlyList<AssetPath> deletedPaths)
        {
            var ordered = pane.VisibleAssetsProvider?.Invoke();
            if (ordered == null || ordered.Count == 0)
            {
                return string.Empty;
            }

            var deletedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < deletedPaths.Count; i++)
            {
                var p = deletedPaths[i].Value;
                if (!string.IsNullOrEmpty(p)) deletedSet.Add(p);
            }
            if (deletedSet.Count == 0)
            {
                return string.Empty;
            }

            var anchorIndex = -1;
            for (var i = 0; i < ordered.Count; i++)
            {
                var path = ZepPathHelpers.GetAssetPathAt(ordered, i);
                if (!string.IsNullOrEmpty(path) && deletedSet.Contains(path))
                {
                    anchorIndex = i;
                    break;
                }
            }
            if (anchorIndex < 0)
            {
                return string.Empty;
            }

            for (var i = anchorIndex; i < ordered.Count; i++)
            {
                var path = ZepPathHelpers.GetAssetPathAt(ordered, i);
                if (!string.IsNullOrEmpty(path) && !deletedSet.Contains(path)) return path;
            }
            for (var i = anchorIndex - 1; i >= 0; i--)
            {
                var path = ZepPathHelpers.GetAssetPathAt(ordered, i);
                if (!string.IsNullOrEmpty(path) && !deletedSet.Contains(path)) return path;
            }
            return string.Empty;
        }

        public bool AddTag(IReadOnlyList<AssetPath> assetPaths, string tagId)
        {
            return _tagMutations.ApplyMutation(ToStringPaths(assetPaths), tagId, true);
        }

        public bool RemoveTag(IReadOnlyList<AssetPath> assetPaths, string tagId)
        {
            return _tagMutations.ApplyMutation(ToStringPaths(assetPaths), tagId, false);
        }

        public bool ClearTags(IReadOnlyList<AssetPath> assetPaths)
        {
            return _tagMutations.ClearAll(ToStringPaths(assetPaths));
        }

        private static IReadOnlyList<string> ToStringPaths(IReadOnlyList<AssetPath> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var converted = new string[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                converted[i] = paths[i];
            }

            return converted;
        }

        public bool CreateTag(string tagName, ZepTagColor color)
        {
            if (!TryBeginTagDefinitionMutation(out var originalDefinitions))
            {
                return false;
            }

            var preview = CreateTagDefinitionPreview(originalDefinitions);
            if (!preview.TryCreateTag(tagName, color, out var tag, out var errorMessage))
            {
                SetStatus(errorMessage);
                ApplyProjection();
                return false;
            }

            return ApplyTagDefinitionChange(
                originalDefinitions,
                preview.GetOrderedTags(),
                removeUndefinedAssignments: false,
                successMessage: "Created tag " + tag.Name);
        }

        public IReadOnlyList<ZepTag> GetSharedTags()
        {
            return _tagDefinitions.GetOrderedTags();
        }

        public bool UpdateTagDefinition(ZepTag tag)
        {
            if (!TryBeginTagDefinitionMutation(out var originalDefinitions))
            {
                return false;
            }

            var preview = CreateTagDefinitionPreview(originalDefinitions);
            if (!preview.TryUpdateTag(
                    tag.Id,
                    tag.Name,
                    tag.Color,
                    tag.IsFavorite,
                    out var updatedTag,
                    out var errorMessage))
            {
                SetStatus(errorMessage);
                ApplyProjection();
                return false;
            }

            return ApplyTagDefinitionChange(
                originalDefinitions,
                preview.GetOrderedTags(),
                removeUndefinedAssignments: false,
                successMessage: "Updated tag " + updatedTag.Name);
        }

        public bool DeleteTagDefinition(string tagId)
        {
            if (!TryBeginTagDefinitionMutation(out var originalDefinitions))
            {
                return false;
            }

            var preview = CreateTagDefinitionPreview(originalDefinitions);
            if (!preview.TryDeleteTag(tagId, out var errorMessage))
            {
                SetStatus(errorMessage);
                ApplyProjection();
                return false;
            }

            return ApplyTagDefinitionChange(
                originalDefinitions,
                preview.GetOrderedTags(),
                removeUndefinedAssignments: false,
                successMessage: "Removed tag",
                assignmentTagIdToRemove: tagId);
        }

        public bool ResetTagDefinitions()
        {
            if (!TryBeginTagDefinitionMutation(out var originalDefinitions))
            {
                return false;
            }

            var preview = CreateTagDefinitionPreview(originalDefinitions);
            preview.ResetToDefaults();
            return ApplyTagDefinitionChange(
                originalDefinitions,
                preview.GetOrderedTags(),
                removeUndefinedAssignments: true,
                successMessage: "Reset tags to defaults");
        }

        private bool TryBeginTagDefinitionMutation(out ZepTag[] originalDefinitions)
        {
            var preparation = _fileCommands.PrepareRecordedMutation();
            if (!preparation.Succeeded)
            {
                originalDefinitions = Array.Empty<ZepTag>();
                SetStatus(preparation.ErrorMessage);
                ApplyProjection();
                return false;
            }

            preparation = _fileCommands.PrepareTagAssignmentsForMutation();
            if (!preparation.Succeeded)
            {
                originalDefinitions = Array.Empty<ZepTag>();
                SetStatus(preparation.ErrorMessage);
                ApplyProjection();
                return false;
            }

            try
            {
                SyncTagAssignmentsIfNeeded();
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                originalDefinitions = Array.Empty<ZepTag>();
                ReportTagPresentationUnavailable(exception);
                return false;
            }

            originalDefinitions = CopyTagDefinitions(_tagDefinitions.GetOrderedTags());
            return true;
        }

        private bool ApplyTagDefinitionChange(
            IReadOnlyList<ZepTag> originalDefinitions,
            IReadOnlyList<ZepTag> desiredDefinitions,
            bool removeUndefinedAssignments,
            string successMessage,
            string assignmentTagIdToRemove = null)
        {
            var definitionIdsBefore = CaptureTagDefinitionIds();
            var result = _fileCommands.ChangeTagDefinitionsAfterPreparation(
                originalDefinitions,
                desiredDefinitions,
                removeUndefinedAssignments,
                assignmentTagIdToRemove);
            if (!result.Succeeded)
            {
                SetStatus(result.ErrorMessage);
                ApplyProjection();
                return false;
            }

            try
            {
                // Reset can remove orphan assignments whose IDs never belonged to a definition.
                if (removeUndefinedAssignments)
                {
                    RefreshTagPresentationCache(_assetGateway.TagVersion);
                }
                else
                {
                    RefreshTagPresentationForTaggedPaths(
                        FindUnassignedTagIds(definitionIdsBefore, out _),
                        _assetGateway.TagVersion);
                }
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                ReportTagPresentationUnavailable(exception);
                return true;
            }

            SetStatus(successMessage);
            ApplyProjection();
            return true;
        }

        // A replay is the one definition change that can ADD an assignment: undoing
        // a tag deletion restores the whole tag snapshot, and a restored assignment
        // can put a path back into the tagged set — which the tagged-path refresh,
        // by construction, cannot see. Reset can also restore unknown IDs without
        // changing definitions, so the command's actual assignment change matters.
        private void RefreshTagPresentationAfterDefinitionReplay(
            HashSet<string> definitionIdsBeforeReplay,
            bool assignmentSnapshotChanged)
        {
            var observedTagVersion = _assetGateway.TagVersion;
            var unassignedTagIds = FindUnassignedTagIds(
                definitionIdsBeforeReplay,
                out var addedAnyDefinitionId);
            if (assignmentSnapshotChanged || definitionIdsBeforeReplay == null || addedAnyDefinitionId)
            {
                RefreshTagPresentationCache(observedTagVersion);
                return;
            }

            RefreshTagPresentationForTaggedPaths(unassignedTagIds, observedTagVersion);
        }

        private static TagDefinitionStore CreateTagDefinitionPreview(
            IReadOnlyList<ZepTag> definitions)
        {
            var records = new List<TagDefinitionRecord>(definitions?.Count ?? 0);
            if (definitions != null)
            {
                for (var i = 0; i < definitions.Count; i++)
                {
                    var tag = definitions[i];
                    records.Add(new TagDefinitionRecord
                    {
                        Id = tag.Id,
                        Name = tag.Name,
                        Color = tag.Color,
                        IsFavorite = tag.IsFavorite
                    });
                }
            }

            return new TagDefinitionStore(records, () => { });
        }

        private static ZepTag[] CopyTagDefinitions(IReadOnlyList<ZepTag> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return Array.Empty<ZepTag>();
            }

            var copy = new ZepTag[tags.Count];
            for (var i = 0; i < tags.Count; i++)
            {
                copy[i] = tags[i];
            }

            return copy;
        }

        public bool Undo()
        {
            return Undo(TakeUndoRedoPane());
        }

        public bool Undo(ZepPaneState pane)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return false;
            }

            _isHandlingOperation = true;
            try
            {
                var replayedKind = _fileCommands.PeekUndoKind();
                if (replayedKind == ProjectCommandKind.SetTagDefinitions)
                {
                    var preparation = _fileCommands.PrepareTagAssignmentsForMutation();
                    if (!preparation.Succeeded)
                    {
                        return HandleUndoRedoResult(
                            pane,
                            preparation,
                            "Undo completed",
                            replayedKind);
                    }

                    SyncTagAssignmentsIfNeeded();
                }

                var definitionIdsBefore = CaptureTagDefinitionIdsForReplay(replayedKind);
                var result = _fileCommands.Undo();
                return HandleUndoRedoResult(
                    pane,
                    result,
                    "Undo completed",
                    replayedKind,
                    definitionIdsBefore);
            }
            finally
            {
                _isHandlingOperation = false;
            }
        }

        public bool Redo()
        {
            return Redo(TakeUndoRedoPane());
        }

        public bool Redo(ZepPaneState pane)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return false;
            }

            _isHandlingOperation = true;
            try
            {
                var replayedKind = _fileCommands.PeekRedoKind();
                if (replayedKind == ProjectCommandKind.SetTagDefinitions)
                {
                    var preparation = _fileCommands.PrepareTagAssignmentsForMutation();
                    if (!preparation.Succeeded)
                    {
                        return HandleUndoRedoResult(
                            pane,
                            preparation,
                            "Redo completed",
                            replayedKind);
                    }

                    SyncTagAssignmentsIfNeeded();
                }

                var definitionIdsBefore = CaptureTagDefinitionIdsForReplay(replayedKind);
                var result = _fileCommands.Redo();
                return HandleUndoRedoResult(
                    pane,
                    result,
                    "Redo completed",
                    replayedKind,
                    definitionIdsBefore);
            }
            finally
            {
                _isHandlingOperation = false;
            }
        }

        // Unity reconciles its serialized undo cursor on a delayed editor tick.
        // Panel shortcuts therefore capture the pane at gesture time instead of
        // consulting whichever pane happens to be active when that tick runs.
        public void CaptureUndoRedoPane(ZepPaneState pane)
        {
            _capturedUndoRedoPane = pane;
            _hasCapturedUndoRedoPane = true;
        }

        public void ReleaseUndoRedoPane(ZepPaneState pane)
        {
            if (_hasCapturedUndoRedoPane && ReferenceEquals(_capturedUndoRedoPane, pane))
            {
                _capturedUndoRedoPane = null;
                _hasCapturedUndoRedoPane = false;
            }
        }

        private ZepPaneState TakeUndoRedoPane()
        {
            if (!_hasCapturedUndoRedoPane)
            {
                return _viewModel.ActivePane;
            }

            var pane = _capturedUndoRedoPane;
            _capturedUndoRedoPane = null;
            _hasCapturedUndoRedoPane = false;
            return pane;
        }

        public void ClearRedoHistory()
        {
            if (_fileCommands.ClearRedo())
            {
                ApplyProjection();
            }
        }

        /// <summary>
        /// Looks up the presentation data (modified-date, size, IsAddressable) for
        /// an asset. The presentation cache is the state owner; this is the
        /// public seam UI row binders use to fetch the values lazily at bind
        /// time instead of having them baked into every projection entry. That
        /// shift is what makes RebuildProjectionCache O(records) hashtable ops
        /// instead of O(records) disk syscalls — visible rows pay the per-row
        /// cost the moment they bind, every other row pays nothing.
        /// </summary>
        public AssetPresentationData GetPresentation(string assetPath, string typeName)
        {
            return _presentationCache.Get(assetPath ?? string.Empty, typeName ?? string.Empty);
        }

        /// <summary>
        /// Whether the Addressables package is present, i.e. whether any asset
        /// can ever be addressable. Constant for the life of the domain.
        /// </summary>
        public bool IsAddressablesInstalled => _addressablesQuery.IsInstalled;

        /// <summary>
        /// Returns tags for a path, or refuses while tag presentation recovery is
        /// pending so callers never render unavailable state as an empty result.
        /// </summary>
        public IReadOnlyList<ZepTag> GetTagsForPath(string assetPath)
        {
            if (_synchronizationPresentationBlocked)
            {
                throw new ProjectTagStateUnavailableException(_tagStateRecoveryError);
            }

            assetPath = assetPath ?? string.Empty;
            if (!_globalReconciliationComplete
                && !string.IsNullOrEmpty(assetPath)
                && !_index.TryGetByPath(assetPath, out _)
                && !_index.TryGetFolder(assetPath, out _))
            {
                try
                {
                    _tagProjection.ResolveUnindexedPresentation(
                        assetPath,
                        isFolder: false,
                        out var tags,
                        out _);
                    return tags;
                }
                catch (ProjectTagStateUnavailableException exception)
                {
                    _hasBuiltProjectionCache = false;
                    _synchronizationPresentationBlocked = true;
                    ReportTagPresentationUnavailable(exception);
                    throw;
                }
            }

            return _tagProjection.GetTagsForPath(assetPath);
        }

        // The same most-recent-tag tint the browse projection stamps on folder
        // entries, for consumers that render a folder outside a projection
        // (the sidebar's Favorites rows).
        /// <summary>
        /// Returns a folder tint, or refuses while tag presentation recovery is
        /// pending so callers never render unavailable state as no tint.
        /// </summary>
        public ZepTagColor GetFolderTintColorForPath(string folderPath)
        {
            if (_synchronizationPresentationBlocked)
            {
                throw new ProjectTagStateUnavailableException(_tagStateRecoveryError);
            }

            folderPath = folderPath ?? string.Empty;
            if (!_globalReconciliationComplete
                && !string.IsNullOrEmpty(folderPath)
                && !_index.TryGetByPath(folderPath, out _)
                && !_index.TryGetFolder(folderPath, out _))
            {
                try
                {
                    _tagProjection.ResolveUnindexedPresentation(
                        folderPath,
                        isFolder: true,
                        out _,
                        out var folderTintColor);
                    return folderTintColor;
                }
                catch (ProjectTagStateUnavailableException exception)
                {
                    _hasBuiltProjectionCache = false;
                    _synchronizationPresentationBlocked = true;
                    ReportTagPresentationUnavailable(exception);
                    throw;
                }
            }

            return _tagProjection.GetFolderTintColorForPath(folderPath, "Folder");
        }

        /// <summary>
        /// Returns the first known asset path of the given type from the in-memory
        /// index, or null. Used by the icon repository so type-filter icons match
        /// what the files panel shows for that type. Index contains non-folder
        /// assets only — "Folder" and other pseudo-types will return null and
        /// let the repository fall back to built-in icons.
        /// </summary>
        public string GetFirstAssetPathForType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            return _index.GetAnyAssetPathForType(typeName);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _synchronizationPresentationBlocked = false;
            _sourceSynchronizationFailed = false;
            _indexSynchronizationError = null;
            _viewModel.PaneChanged -= HandlePaneChanged;
            _addressablesQuery.Changed -= HandleAddressablesChanged;
            _synchronizer.Dispose();
            _assetChangeSyncer.Dispose();
            _refreshScheduler.Dispose();
            _deferredStartupWorkQueued = false;
            _deferredStartupWorkUnfinished = false;
            _deferredScheduler.Cancel(_deferredStartupCallback);
            _deferredScheduler.Cancel(_deferredWorkerPollCallback);
        }

        // Called by Bridge in response to ZepAssetPostprocessor.Changed.
        public void HandleAssetChanges(ProjectAssetChanges changes)
        {
            changes = changes.Normalize();
            List<string> currentMoved = null;
            List<string> currentMovedFrom = null;
            for (var i = 0; i < changes.Moved.Length; i++)
            {
                // An owned rename can be reversed before its first callback.
                // The source being a live folder again AND the destination
                // being absent proves that old pair is superseded. Keep merely
                // unresolved destinations as concrete recovery evidence.
                var superseded = _assetGateway.IsFolder(AssetPath.From(changes.MovedFromPaths[i]))
                    && !_assetGateway.Exists(AssetPath.From(changes.Moved[i]));
                if (superseded && currentMoved == null)
                {
                    currentMoved = new List<string>(changes.Moved.Length);
                    currentMovedFrom = new List<string>(changes.Moved.Length);
                    for (var previous = 0; previous < i; previous++)
                    {
                        currentMoved.Add(changes.Moved[previous]);
                        currentMovedFrom.Add(changes.MovedFromPaths[previous]);
                    }
                }

                if (!superseded && currentMoved != null)
                {
                    currentMoved.Add(changes.Moved[i]);
                    currentMovedFrom.Add(changes.MovedFromPaths[i]);
                }
            }

            if (currentMoved != null)
            {
                changes = new ProjectAssetChanges(
                    changes.Imported, changes.Deleted,
                    currentMoved.ToArray(), currentMovedFrom.ToArray(),
                    changes.DidDomainReload).Normalize();
            }

            if (!changes.IsEmpty)
            {
                _synchronizer.InvalidateInProgressScan();
                if (_panelUseStarted
                    && !_globalReconciliationComplete
                    && !changes.DidDomainReload
                    && _synchronizer is ICatalogDirtyMarker dirtyMarker)
                {
                    dirtyMarker.MarkPersistentStateDirty();
                }
            }

            AccumulatePendingAssetChanges(changes);
            if (_bulkDepth > 0)
            {
                if (!changes.IsEmpty)
                {
                    _pendingBulkCallbackChanges.Add(changes);
                    RecordConcreteScanChanges(changes);
                }

                return;
            }

            if (_synchronizationPresentationBlocked)
            {
                RecordConcreteScanChanges(changes);
                return;
            }

            try
            {
                if (!_panelUseStarted)
                {
                    ApplyExternalChanges(changes, out var unresolvedPaths);
                    if (unresolvedPaths.Count == 0)
                    {
                        _assetChangeSyncer.AcknowledgeOwnedAssetBatch();
                    }
                    else
                    {
                        RequestBackgroundReconciliation();
                    }
                }
                else
                {
                    _assetChangeSyncer.HandleAssetChanges(changes);
                }
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                _synchronizationPresentationBlocked = true;
                _hasBuiltProjectionCache = false;
                _globalReconciliationComplete = false;
                ReportTagPresentationUnavailable(exception);
            }

            RecordConcreteScanChanges(changes);
        }

        private ExternalChangeApplicationResult ApplyExternalChanges(
            ProjectAssetChanges changes,
            out IReadOnlyList<string> unresolvedPaths)
        {
            changes = changes.Normalize();
            List<AssetCatalogMove> folderMoves = null;
            for (var i = 0; i < changes.Moved.Length && i < changes.MovedFromPaths.Length; i++)
            {
                var oldPath = AssetPathFormat.Normalize(changes.MovedFromPaths[i]);
                var newPath = AssetPathFormat.Normalize(changes.Moved[i]);
                if (!AssetPathClassification.IsProjectAssetPath(oldPath)
                    || !AssetPathClassification.IsProjectAssetPath(newPath)
                    || IsWithinFolderScope(newPath, oldPath)
                    // A completed owned operation already published its rewrite.
                    // An unrelated import in the same callback must not make its
                    // already-applied move look new again.
                    || (!_index.ContainsVisibleFolder(oldPath)
                        && _index.ContainsVisibleFolder(newPath))
                    // A callback may arrive after a second move (including a
                    // rename back). Only the live completed move can carry a
                    // bookmark; a stale destination must never capture it.
                    || !_assetGateway.IsFolder(AssetPath.From(newPath))
                    || _assetGateway.IsFolder(AssetPath.From(oldPath)))
                {
                    continue;
                }

                folderMoves ??= new List<AssetCatalogMove>();
                folderMoves.Add(new AssetCatalogMove(oldPath, newPath));
            }

            var result = _crossCache.ApplyExternalChangesDetailed(changes, out unresolvedPaths);
            if (unresolvedPaths.Count > 0 && _synchronizer is IAssetScanChangeRecorder recorder)
            {
                recorder.RecordUnresolvedChanges(unresolvedPaths);
            }

            if (result == ExternalChangeApplicationResult.Changed && folderMoves != null)
            {
                foreach (var move in folderMoves)
                {
                    if (!_index.ContainsVisibleFolder(move.OldPath)
                        && _index.ContainsVisibleFolder(move.NewPath))
                    {
                        AssetPathRewritten?.Invoke(move.OldPath, move.NewPath);
                    }
                }
            }

            return result;
        }

        // Called by Bridge in response to EditorApplication.projectChanged.
        public void HandleProjectChanged()
        {
            _synchronizer.InvalidateInProgressScan();
            // projectChanged carries no paths and Unity emits it during script/domain
            // reload. Concrete non-reload AssetPostprocessor batches own durable cache
            // invalidation; this coarse signal only requests reconciliation.
            _assetChangeSyncer.HandleProjectChanged();
        }

        private void RequestBackgroundReconciliation()
        {
            if (_isDisposed || !_panelUseStarted || _backgroundReconciliationScheduled)
            {
                return;
            }

            // The reload's own coarse projectChanged arrives while the synchronizer is
            // still patching the types that same reload changed, or verifying the
            // snapshot it loaded. Starting a new synchronization now would cancel that
            // job and cost the next reload the same work again, so the request is
            // dropped - exactly as it is dropped one step later, while the operation
            // is still open. The drop has to happen here
            // rather than in RunDeferredHydration: the line below clears
            // _globalReconciliationComplete, and a drop after that would leave the flag
            // false with nothing left to re-set it.
            if (_synchronizer.HasBackgroundJob)
            {
                return;
            }

            _globalReconciliationComplete = false;
            _backgroundReconciliationScheduled = true;
            _deferredScheduler.Schedule(() =>
            {
                _backgroundReconciliationScheduled = false;
                RunDeferredHydration();
            });
        }

        private void RecordConcreteScanChanges(ProjectAssetChanges changes)
        {
            if (_synchronizer is IAssetScanChangeRecorder recorder)
            {
                recorder.RecordConcreteChanges(changes);
            }
        }

        public void ExpectPanelSelectionChange(
            ZepPaneState pane,
            IReadOnlyList<string> selectedAssetPaths)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return;
            }

            _expectedPanelSelectionPane = pane;
            _expectedPanelSelectionPaths = selectedAssetPaths == null
                ? Array.Empty<string>()
                : new List<string>(selectedAssetPaths);
        }

        // Called by Bridge in response to Selection.selectionChanged after it has
        // already read Unity's Selection into the raw path list. Ordinary global
        // selection may mirror into the primary pane, but only an explicit reveal
        // is allowed to navigate the panel to a different folder.
        public void HandleSelectionPaths(IReadOnlyList<string> selectedAssetPaths)
        {
            if (_isDisposed || _isHandlingOperation)
            {
                return;
            }

            if (_expectedPanelSelectionPane != null)
            {
                var expectedPaths = _expectedPanelSelectionPaths;
                _expectedPanelSelectionPane = null;
                _expectedPanelSelectionPaths = null;
                if (AreSelectedAssetPathsEqual(expectedPaths, selectedAssetPaths))
                {
                    return;
                }
            }

            var primaryPane = _viewModel.PrimaryPane;
            if (!ReferenceEquals(_viewModel.ActivePane, primaryPane))
            {
                _viewModel.ActivatePane(primaryPane);
            }

            if (AreSelectedAssetPathsEqual(
                ToStringPaths(primaryPane.SelectedAssetPaths),
                selectedAssetPaths))
            {
                return;
            }

            if (selectedAssetPaths == null || selectedAssetPaths.Count == 0)
            {
                ApplyPaneState(primaryPane, pane =>
                {
                    pane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
                });

                ApplyProjection(primaryPane);
                return;
            }

            if (selectedAssetPaths.Count == 1 && _assetGateway.IsFolder(AssetPath.From(selectedAssetPaths[0])))
            {
                ApplyExternalSelection(Array.Empty<string>());
                return;
            }

            ApplyExternalSelection(selectedAssetPaths);
        }

        public bool RevealAndFrameAsset(string assetPath)
        {
            return RevealAndFrameAsset(_viewModel.PrimaryPane, assetPath);
        }

        public bool RevealAndFrameAsset(
            ZepPaneState pane,
            string assetPath)
        {
            if (_isDisposed || _isHandlingOperation)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            if (!_viewModel.IsLivePane(pane))
            {
                return false;
            }

            if (pane.Source.Kind == ZepPaneSourceKind.Tag)
            {
                return false;
            }

            var typedAsset = AssetPath.From(assetPath);
            if ((!_index.TryGetByPath(typedAsset.Value, out _)
                    && !_index.ContainsVisibleFolder(typedAsset.Value)
                    && !_index.TryGetPending(typedAsset.Value, out _))
                || !pane.IsPathWithinSource(typedAsset))
            {
                return false;
            }

            if (!ReferenceEquals(_viewModel.ActivePane, pane))
            {
                _viewModel.ActivatePane(pane);
            }

            string folderPath;
            IReadOnlyList<string> assetPaths;
            var isFolder = _assetGateway.IsFolder(typedAsset);
            if (isFolder)
            {
                // Select the folder inside its parent rather than entering it.
                // A root folder (Assets/Packages) has no parent to browse into —
                // GetParentFolder returns the path itself — so navigate to it
                // with no selection, the only case where "reveal" enters.
                var parentFolder = GetParentFolder(assetPath);
                if (!string.IsNullOrEmpty(parentFolder)
                    && !string.Equals(parentFolder, assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    folderPath = parentFolder;
                    assetPaths = new[] { assetPath };
                }
                else
                {
                    folderPath = assetPath;
                    assetPaths = Array.Empty<string>();
                }
            }
            else
            {
                folderPath = ResolveSelectionFolderPath(new[] { assetPath }, _assetGateway);
                assetPaths = new[] { assetPath };
            }

            folderPath = AssetPathFormat.Normalize(folderPath);
            if (string.IsNullOrEmpty(folderPath))
            {
                folderPath = "Assets";
            }

            var typedFolder = AssetPath.From(folderPath);
            var typedAssetPaths = ToAssetPaths(assetPaths);
            ApplyPaneState(pane, originatingPane =>
            {
                // A reveal is a navigation, and every navigation ends the
                // search (TrySetFolderSelection does the same). Left set, the
                // pane keeps projecting the SEARCH's results, reconciliation
                // prunes the selection this batch makes, and the miss below
                // sends the ping receiver into a Refresh-retry that can never
                // converge — e.g. an asset created while a search is live.
                // Cleared before the selection: a selection made while search
                // text is live is recorded as the picked search result.
                originatingPane.SetSearchText(string.Empty);
                originatingPane.SetSelectedTagFilterId(string.Empty);
                originatingPane.SetSelectedTypeFilter(string.Empty);
                originatingPane.TrySetCurrentFolderPath(typedFolder);
                originatingPane.TrySetSelectedFolderPath(typedFolder);
                originatingPane.SetSelectedAssetPaths(typedAssetPaths);
            });

            ApplyProjection(pane);

            AssetFrameRequested?.Invoke(pane, assetPath);

            // A parent-less root folder was navigated to with no selection to
            // reconcile — the reveal is complete.
            if (assetPaths.Count == 0)
            {
                return true;
            }

            // ApplyProjection drops selected paths that aren't in the current
            // projected list. If the target survived, it's really in view.
            var reconciledSelection = pane.SelectedAssetPaths;
            for (var i = 0; i < reconciledSelection.Count; i++)
            {
                if (reconciledSelection[i].Equals(typedAsset))
                {
                    return true;
                }
            }

            return false;
        }

        private void HandlePaneChanged(ZepPaneChange change)
        {
            // Nothing may project before panel use starts. The window writes the
            // serialized folder into the pane while building its UI, before it
            // calls BeginPanelUse, and the pane raises Navigation synchronously;
            // projecting that against a still-empty index falls back to "Assets"
            // and destroys the restore it was reacting to. Dropping the change
            // costs nothing: BeginPanelUse sets the flag before its own
            // ApplyProjection, which reconciles every pane, so the restored state
            // is published once, from there. The only work this method does
            // before projecting is the direct observation below, which is gated
            // on the same flag already.
            if (_isDisposed || _isApplyingState || !_panelUseStarted)
            {
                return;
            }

            ZepPaneState pane;
            // Only an Updated change carries a meaningful reason; every other kind
            // is a structural event whose projection has to be rebuilt outright.
            var update = change.Kind == ZepPaneChangeKind.Updated
                ? change.Update
                : ZepPaneUpdate.None;
            switch (change.Kind)
            {
                case ZepPaneChangeKind.Updated:
                    if ((change.Update
                        & (ZepPaneUpdate.Navigation
                            | ZepPaneUpdate.Search
                            | ZepPaneUpdate.Filter
                            | ZepPaneUpdate.Selection
                            | ZepPaneUpdate.Expansion))
                        == ZepPaneUpdate.None)
                    {
                        return;
                    }

                    pane = change.NewPane;
                    if (!_synchronizationPresentationBlocked
                        && _panelUseStarted
                        && (change.Update & ZepPaneUpdate.Navigation) != 0
                        && (!_globalReconciliationComplete
                            || !_index.ContainsVisibleFolder(
                                pane.CurrentFolderPath.Value)))
                    {
                        try
                        {
                            var folderPath = pane.CurrentFolderPath.Value;
                            if (pane.Source.IsPrimary)
                            {
                                ObservePrimaryFolderOrAssetsFallback(folderPath);
                            }
                            else
                            {
                                ObserveDirectFolder(folderPath);
                            }

                            // A sidebar multi-selection lists these folders
                            // with the current one, so their contents have to
                            // be read before reconciliation gets to them too.
                            var additionalFolders = pane.AdditionalFolderPaths;
                            for (var i = 0; i < additionalFolders.Count; i++)
                            {
                                ObserveDirectFolderIfNeeded(additionalFolders[i].Value);
                            }
                        }
                        catch (ProjectTagStateUnavailableException exception)
                        {
                            _hasBuiltProjectionCache = false;
                            _synchronizationPresentationBlocked = true;
                            ReportTagPresentationUnavailable(exception);
                            return;
                        }
                    }
                    break;
                case ZepPaneChangeKind.Added:
                case ZepPaneChangeKind.Replaced:
                    pane = change.NewPane;
                    break;
                case ZepPaneChangeKind.Restored:
                    _isReactingToViewModelChange = true;
                    try
                    {
                        ApplyProjection();
                    }
                    finally
                    {
                        _isReactingToViewModelChange = false;
                    }

                    return;
                default:
                    return;
            }

            _isReactingToViewModelChange = true;
            try
            {
                ApplyProjection(pane, update);
            }
            finally
            {
                _isReactingToViewModelChange = false;
            }
        }

        private int __pp_applySeq;
        private void ApplyProjection()
        {
            ApplyProjection(sourceChanges: null);
        }

        private void ApplyProjection(
            ZepPaneState pane,
            ZepPaneUpdate update = ZepPaneUpdate.None)
        {
            if (!_viewModel.IsLivePane(pane) || _synchronizationPresentationBlocked)
            {
                return;
            }
            if (!_panelUseStarted && ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] pre-panel-use pane projection indexCount={_index.Count}\n{Environment.StackTrace}");

            ZepPerfLog.Count(ZepPerfCounter.ProjectionPublications);
            using var measurement = ZepPerfLog.Measure(
                "pane-projection",
                ZepPerfStage.ViewModelPublication);

            if (!TryPrepareTagProjection(out var tagVersion))
            {
                return;
            }

            var recovering = _tagStateRecoveryError != null;
            var pendingChanges = TakePendingAssetChanges();
            var sharedCachesChanged = EnsureSharedProjectionCaches(tagVersion);
            if (recovering || pendingChanges.HasValue || sharedCachesChanged)
            {
                ApplyProjection(pendingChanges, sharedCachesAreCurrent: true);
                return;
            }

            var folderEntries = _folderEntries.Cached;
            ApplyViewModelState(() =>
            {
                if (TryReconcileLivePane(pane, null))
                {
                    ApplyPaneProjectionState(pane, folderEntries, update);
                }

                ApplyGlobalProjectionState(folderEntries);
            });
            CompleteTagStateRecovery();
        }

        private void ApplyProjection(
            ProjectAssetChanges? sourceChanges,
            bool sharedCachesAreCurrent = false)
        {
            if (!_panelUseStarted && ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] pre-panel-use projection indexCount={_index.Count}\n{Environment.StackTrace}");
            ZepPerfLog.Count(ZepPerfCounter.ProjectionPublications);
            using var measurement = ZepPerfLog.Measure(
                "projection",
                ZepPerfStage.ViewModelPublication);
            var stopwatch = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            var sequence = ++__pp_applySeq;
            if (!sharedCachesAreCurrent)
            {
                if (sourceChanges.HasValue)
                {
                    AccumulatePendingAssetChanges(sourceChanges.Value);
                }

                if (_synchronizationPresentationBlocked)
                {
                    return;
                }

                if (!TryPrepareTagProjection(out var tagVersion))
                {
                    return;
                }

                sourceChanges = TakePendingAssetChanges();
                EnsureSharedProjectionCaches(tagVersion);
            }

            var folderEntries = _folderEntries.Cached;
            var paneCount = 0;
            ApplyViewModelState(() =>
            {
                ReconcileAllPanes(sourceChanges);
                // Walked in place instead of through a copy of `Panes` (which is a
                // lazy iterator, so the copy was a List + an enumerator on every
                // publication). ApplyPaneProjectionState only writes to the pane it
                // is given, and every pane setter it calls raises inside the
                // notification deferral this runs under, so the collection cannot
                // change under the loop; the Count re-read is the guard if that ever
                // stops being true.
                var auxiliaries = _viewModel.AuxiliaryPanes;
                var windowPanes = _viewModel.WindowPanes;
                paneCount = 1 + auxiliaries.Count + windowPanes.Count;
                if (_viewModel.IsLivePane(_viewModel.PrimaryPane))
                {
                    ApplyPaneProjectionState(
                        _viewModel.PrimaryPane,
                        folderEntries,
                        sourceChanges: sourceChanges);
                }

                for (var i = 0; i < auxiliaries.Count; i++)
                {
                    if (_viewModel.IsLivePane(auxiliaries[i]))
                    {
                        ApplyPaneProjectionState(auxiliaries[i], folderEntries);
                    }
                }

                for (var i = 0; i < windowPanes.Count; i++)
                {
                    if (_viewModel.IsLivePane(windowPanes[i]))
                    {
                        ApplyPaneProjectionState(windowPanes[i], folderEntries);
                    }
                }

                ApplyGlobalProjectionState(folderEntries);
            });
            CompleteTagStateRecovery();

            if (stopwatch != null)
            {
                stopwatch.Stop();
                ZepPerfLog.Log(
                    $"[Zep/perf] ApplyProjection seq={sequence} panes={paneCount} ms={stopwatch.Elapsed.TotalMilliseconds:F2}");
            }
        }

        private bool EnsureSharedProjectionCaches(int tagVersion, bool tagsAreCurrent = false)
        {
            var changed = !tagsAreCurrent && SyncTagAssignmentsIfNeeded(tagVersion);

            ServiceSearchIndexCompactionRequest();
            return changed;
        }

        // The search index asks for compaction once a quarter of its ordinals are
        // retired. It used to answer itself, synchronously, inside ApplyCatalogDelta
        // — i.e. inside Unity's import callback, where a full gram-index rebuild
        // costs seconds on a large project. The deferred worker already exists to
        // build a search index off the editor thread and adopt it under a record-
        // version check, so the request rides that path instead. Nothing about the
        // results waits on it: every read path skips tombstoned ordinals. Only one
        // build runs at a time, and a request whose build loses the catalog race
        // stays unanswered only until the next delta raises it again.
        private void ServiceSearchIndexCompactionRequest()
        {
            if (_isDisposed
                || !_searchService.IsCompactionRequested
                || _deferredWorkerTask != null
                || _deferredStartupWorkQueued)
            {
                return;
            }

            _searchService.ClearCompactionRequest();
            ScheduleDeferredStartupWork();
        }

        // Everything a browse/search projection is a function of. Recorded at each
        // build so the next Selection-only publication can tell "the same list" from
        // "a list I have to ask for again". Weak keys: panes are closed and replaced
        // through the session and the controller has no other reason to remember one.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            ZepPaneState,
            PaneProjectionInputs> _lastProjectionInputs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<
                ZepPaneState,
                PaneProjectionInputs>();

        private sealed class PaneProjectionInputs
        {
            internal PaneProjectionInputs(
                string currentFolderPath,
                string searchText,
                ZepSearchScope searchScope,
                string typeFilter,
                string tagFilterId,
                string rootFolderPath,
                int catalogVersion,
                int tagVersion)
            {
                _currentFolderPath = currentFolderPath ?? string.Empty;
                _searchText = searchText ?? string.Empty;
                _searchScope = searchScope;
                _typeFilter = typeFilter ?? string.Empty;
                _tagFilterId = tagFilterId ?? string.Empty;
                _rootFolderPath = rootFolderPath ?? string.Empty;
                _catalogVersion = catalogVersion;
                _tagVersion = tagVersion;
            }

            private readonly string _currentFolderPath;
            private readonly string _searchText;
            private readonly ZepSearchScope _searchScope;
            private readonly string _typeFilter;
            private readonly string _tagFilterId;
            private readonly string _rootFolderPath;
            private readonly int _catalogVersion;
            private readonly int _tagVersion;

            internal bool Equals(PaneProjectionInputs other)
            {
                return other != null
                    && _catalogVersion == other._catalogVersion
                    && _tagVersion == other._tagVersion
                    && _searchScope == other._searchScope
                    && string.Equals(
                        _searchText,
                        other._searchText,
                        StringComparison.Ordinal)
                    && string.Equals(
                        _currentFolderPath,
                        other._currentFolderPath,
                        StringComparison.Ordinal)
                    && string.Equals(
                        _typeFilter,
                        other._typeFilter,
                        StringComparison.Ordinal)
                    && string.Equals(
                        _tagFilterId,
                        other._tagFilterId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        _rootFolderPath,
                        other._rootFolderPath,
                        StringComparison.Ordinal);
            }
        }

        private void ApplyPaneProjectionState(
            ZepPaneState pane,
            IReadOnlyList<ZepFolderEntry> folderEntries,
            ZepPaneUpdate update = ZepPaneUpdate.None,
            ProjectAssetChanges? sourceChanges = null)
        {
            var currentFolderPath = AssetPathFormat.Normalize(RewriteMovedPath(
                pane.CurrentFolderPath.Value,
                pane.Source.IsPrimary ? sourceChanges : null));
            if (string.IsNullOrEmpty(currentFolderPath))
            {
                currentFolderPath = "Assets";
            }

            if (pane.Source.IsPrimary
                && !ProjectionFolderExists(folderEntries, currentFolderPath))
            {
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] projection-prune folder={currentFolderPath} panelUse={_panelUseStarted} reconciled={_globalReconciliationComplete} indexCount={_index.Count}\n{Environment.StackTrace}");
                currentFolderPath = "Assets";
            }

            // The folders a sidebar multi-selection lists with the current one
            // are relative to it: a current folder that had to move (pruned,
            // renamed) drops them, and so does each one that no longer exists.
            var additionalFolderPaths = pane.AdditionalFolderPaths;
            if (additionalFolderPaths.Count > 0)
            {
                var surviving = new List<AssetPath>(additionalFolderPaths.Count);
                if (string.Equals(
                    currentFolderPath,
                    pane.CurrentFolderPath.Value,
                    StringComparison.OrdinalIgnoreCase))
                {
                    for (var i = 0; i < additionalFolderPaths.Count; i++)
                    {
                        if (ProjectionFolderExists(
                            folderEntries,
                            additionalFolderPaths[i].Value))
                        {
                            surviving.Add(additionalFolderPaths[i]);
                        }
                    }
                }

                additionalFolderPaths = surviving;
            }

            var searchScopeOptions = BuildSearchScopeOptions(pane, currentFolderPath);

            var selectedSearchScope = ResolveSelectedSearchScope(
                pane.SelectedSearchScope,
                searchScopeOptions);
            var selectedTypeFilter = ResolveSelectedTypeFilter(
                pane.SelectedTypeFilter,
                currentFolderPath);
            var selectedTagFilterId = _tagProjection.ResolveSelectedTagFilterId(
                pane.SelectedTagFilterId);
            var existingSelectedPaths = pane.SelectedAssetPaths;
            if (pane.Source.IsPrimary && sourceChanges.HasValue)
            {
                List<AssetPath> rewrittenSelectedPaths = null;
                for (var i = 0; i < existingSelectedPaths.Count; i++)
                {
                    var rewrittenPath = AssetPath.From(RewriteMovedPath(
                        existingSelectedPaths[i].Value,
                        sourceChanges));
                    if (rewrittenPath == existingSelectedPaths[i])
                    {
                        continue;
                    }

                    if (rewrittenSelectedPaths == null)
                    {
                        rewrittenSelectedPaths = new List<AssetPath>(existingSelectedPaths);
                    }

                    rewrittenSelectedPaths[i] = rewrittenPath;
                }

                if (rewrittenSelectedPaths != null)
                {
                    existingSelectedPaths = rewrittenSelectedPaths;
                }
            }
            IReadOnlyList<ZepAssetEntry> assets;
            // The ceiling a rooted pane or an opened-folder tag pane
            // browses under; search and type filters stay within it.
            var rootFolderPath = pane.BreadcrumbRootPath.IsEmpty
                ? null
                : pane.BreadcrumbRootPath.Value;
            if (!string.IsNullOrEmpty(pane.SearchText))
            {
                MarkSearchUsedInThisProject();
            }

            // 4.3: moving the selection cannot change WHAT is projected, but a
            // pane with a live search re-ran the whole query on every arrow key
            // and handed the shell a fresh list each time, so SetProjectedAssets'
            // reference check missed and the ListView rebound. This is a guard on
            // the call, not a memo of the result: the inputs that decide the list
            // are recorded at the build and compared here, and anything that has
            // not been recorded — a first paint, any non-Selection reason, a moved
            // catalog or tag version — falls straight through to the build.
            var inputs = new PaneProjectionInputs(
                currentFolderPath,
                pane.SearchText,
                selectedSearchScope,
                selectedTypeFilter,
                selectedTagFilterId,
                rootFolderPath,
                _index.Version,
                _tagProjection.ObservedTagVersion);
            if (update == ZepPaneUpdate.Selection
                && pane.ProjectedAssets != null
                && _lastProjectionInputs.TryGetValue(pane, out var lastInputs)
                && lastInputs.Equals(inputs))
            {
                assets = pane.ProjectedAssets;
            }
            else
            {
                assets = _browseProjection.BuildAssetEntries(
                    currentFolderPath,
                    pane.SearchText,
                    selectedSearchScope,
                    selectedTypeFilter,
                    selectedTagFilterId,
                    rootFolderPath,
                    additionalFolderPaths);
                _lastProjectionInputs.Remove(pane);
                _lastProjectionInputs.Add(pane, inputs);
            }

            string selectedFolderPath;
            if (!string.IsNullOrWhiteSpace(selectedTagFilterId))
            {
                selectedFolderPath = string.Empty;
            }
            else
            {
                var candidateSelectedFolderPath = pane.Source.IsPrimary
                    ? RewriteMovedPath(pane.SelectedFolderPath.Value, sourceChanges)
                    : pane.SelectedFolderPath.Value;
                selectedFolderPath = ProjectionFolderExists(
                    folderEntries,
                    candidateSelectedFolderPath)
                    ? candidateSelectedFolderPath
                    : currentFolderPath;

            }

            var selectedAssetPaths = ReconcileSelectedAssetPaths(
                existingSelectedPaths,
                assets,
                BuildExpansionVisibilityPredicate(pane, currentFolderPath));
            var typeFilterOptions = _browseProjection.CachedTypeFilterOptions;

            pane.TrySetCurrentFolderPath(AssetPath.From(currentFolderPath));
            pane.TrySetSelectedFolderPath(AssetPath.From(selectedFolderPath));
            pane.SetAdditionalFolderPaths(additionalFolderPaths);
            pane.SetProjectedAssets(assets);
            pane.SetSelectedAssetPaths(selectedAssetPaths);
            pane.SetSearchScopeOptions(searchScopeOptions);
            pane.ReconcileSelectedSearchScope(selectedSearchScope);
            pane.SetTypeFilterOptions(typeFilterOptions);
            pane.SetSelectedTypeFilter(selectedTypeFilter);
            pane.SetSelectedTagFilterId(selectedTagFilterId);
        }

        private bool ProjectionFolderExists(
            IReadOnlyList<ZepFolderEntry> folderEntries,
            string folderPath)
        {
            // The index is the only source of folder existence (I2). It carries
            // the gate the second clause here used to provide: an observation
            // made before hydration is cleared by the authoritative Replace, and
            // one made after it is the newer read of the filesystem, bounded by
            // the index retiring it on a delete or a move.
            return _index.ContainsVisibleFolder(folderPath);
        }

        private void ApplyGlobalProjectionState(
            IReadOnlyList<ZepFolderEntry> folderEntries)
        {
            if (_publishedFolderEntriesVersion != _folderEntries.Version)
            {
                _viewModel.SetFolders(folderEntries);
                _publishedFolderEntriesVersion = _folderEntries.Version;
            }
            _viewModel.SetTagFilterOptions(_tagProjection.CachedFilterOptions);
            _viewModel.SetCanUndoRedo(_fileCommands.CanUndo, _fileCommands.CanRedo);
            _viewModel.SetStatusMessage(_statusMessage);
        }

        // Task 5 drag validation and Task 7 restoration use this same Core path.
        // The controller owns authoritative folder/tag knowledge; the immutable
        // source value is returned unchanged unless a proven move mapping is
        // supplied by the live reconciliation path below.
        public bool TryReconcilePaneSource(
            ZepPaneSource source,
            out ZepPaneSource reconciledSource)
        {
            return TryReconcilePaneSource(
                source,
                default,
                out reconciledSource,
                out _);
        }

        public bool TryReconcilePaneSource(
            ZepPaneSource source,
            AssetPath currentFolderPath,
            out ZepPaneSource reconciledSource,
            out AssetPath reconciledCurrentFolderPath)
        {
            return TryReconcilePaneSource(
                source,
                default,
                currentFolderPath,
                out reconciledSource,
                out _,
                out reconciledCurrentFolderPath);
        }

        // tagBrowseRootPath is the folder a tag pane had opened from its list
        // (empty for every other case); it survives restoration only while the
        // folder is live, and falls back to the list — never to a closed pane.
        public bool TryReconcilePaneSource(
            ZepPaneSource source,
            AssetPath tagBrowseRootPath,
            AssetPath currentFolderPath,
            out ZepPaneSource reconciledSource,
            out AssetPath reconciledTagBrowseRootPath,
            out AssetPath reconciledCurrentFolderPath)
        {
            if (!TryResolvePaneSource(
                    source,
                    null,
                    out reconciledSource))
            {
                reconciledTagBrowseRootPath = default;
                reconciledCurrentFolderPath = default;
                return false;
            }

            var candidate = new ZepPaneState(reconciledSource);
            if (reconciledSource.Kind == ZepPaneSourceKind.Folder
                && !currentFolderPath.IsEmpty)
            {
                candidate.TrySetCurrentFolderPath(currentFolderPath);
            }
            else if (reconciledSource.Kind == ZepPaneSourceKind.Tag
                && !tagBrowseRootPath.IsEmpty)
            {
                candidate.TryEnterTagFolder(
                    tagBrowseRootPath,
                    currentFolderPath.IsEmpty ? tagBrowseRootPath : currentFolderPath);
            }

            if (!candidate.TryReconcileSource(reconciledSource, IsLiveFolder))
            {
                reconciledTagBrowseRootPath = default;
                reconciledCurrentFolderPath = default;
                return false;
            }

            candidate.ReconcileTagBrowseRoot(candidate.TagBrowseRootPath, IsLiveFolder);
            reconciledTagBrowseRootPath = candidate.TagBrowseRootPath;
            reconciledCurrentFolderPath = candidate.CurrentFolderPath;
            return true;
        }

        private void ReconcileAllPanes(ProjectAssetChanges? sourceChanges)
        {
            ReconcilePanes(_viewModel.AuxiliaryPanes, sourceChanges);
            ReconcilePanes(_viewModel.WindowPanes, sourceChanges);
        }

        private void ReconcilePanes(
            IReadOnlyList<ZepPaneState> panes,
            ProjectAssetChanges? sourceChanges)
        {
            // This loop DOES mutate the collection it walks — source removal removes
            // the pane at the current index — which is what the defensive copy paid
            // for on every publication. Walking the live list in original order is
            // exact instead: after a close the next candidate has slid into the
            // current index, so the index advances only when the pane sitting there
            // is still the one just examined. Termination: every iteration either
            // advances the index or removes at least one pane, so neither a close
            // that the owner rejects nor a listener that closes some other pane
            // from inside the raise can spin here.
            var i = 0;
            while (i < panes.Count)
            {
                var pane = panes[i];
                TryReconcileLivePane(pane, sourceChanges);
                if (i < panes.Count && ReferenceEquals(panes[i], pane))
                {
                    i++;
                }
            }
        }

        private bool TryReconcileLivePane(
            ZepPaneState pane,
            ProjectAssetChanges? sourceChanges)
        {
            if (!_viewModel.IsLivePane(pane) || pane.Source.IsPrimary)
            {
                return _viewModel.IsLivePane(pane);
            }

            if (TryResolvePaneSource(
                    pane.Source,
                    sourceChanges,
                    out var reconciledSource)
                && pane.TryReconcileSource(reconciledSource, IsLiveFolder))
            {
                ReconcileTagBrowseRoot(pane, sourceChanges);
                return true;
            }

            if (!_viewModel.CloseWindowPane(pane))
            {
                _viewModel.CloseAuxiliary(pane);
            }
            return false;
        }

        // The folder a tag pane opened from its list is path-keyed like a
        // rooted pane's root, so it follows the same move mapping; the pane
        // itself decides between rewriting and falling back to its list.
        private void ReconcileTagBrowseRoot(
            ZepPaneState pane,
            ProjectAssetChanges? sourceChanges)
        {
            var root = pane.TagBrowseRootPath;
            if (root.IsEmpty)
            {
                return;
            }

            pane.ReconcileTagBrowseRoot(
                AssetPath.From(RewriteMovedPath(root.Value, sourceChanges)),
                IsLiveFolder);
        }

        private bool TryResolvePaneSource(
            ZepPaneSource source,
            ProjectAssetChanges? sourceChanges,
            out ZepPaneSource reconciledSource)
        {
            reconciledSource = source;
            switch (source.Kind)
            {
                case ZepPaneSourceKind.Primary:
                    return true;
                case ZepPaneSourceKind.Tag:
                    var tags = _tagDefinitions.GetOrderedTags();
                    for (var i = 0; i < tags.Count; i++)
                    {
                        if (string.Equals(
                            tags[i].Id,
                            source.TagId,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                case ZepPaneSourceKind.Folder:
                    var resolvedRoot = AssetPath.From(RewriteMovedPath(
                        source.RootFolderPath.Value,
                        sourceChanges));
                    if (resolvedRoot.IsEmpty || !IsLiveFolder(resolvedRoot))
                    {
                        return false;
                    }

                    reconciledSource = ZepPaneSource.RootedFolder(resolvedRoot);
                    return true;
                default:
                    return false;
            }
        }

        public bool TryGetIndexedPathKind(string assetPath, out bool isFolder)
        {
            if (_index.TryGetFolder(assetPath, out _))
            {
                isFolder = true;
                return true;
            }

            isFolder = false;
            return _index.TryGetByPath(assetPath, out _);
        }

        public bool TryGetIndexedAsset(string assetPath, out string guid, out string typeName)
        {
            if (_index.TryGetByPath(assetPath, out var record))
            {
                guid = record.Guid;
                typeName = record.TypeName;
                return true;
            }

            guid = null;
            typeName = null;
            return false;
        }

        private bool IsLiveFolder(AssetPath folderPath)
        {
            return !folderPath.IsEmpty
                && _index.TryGetFolder(folderPath.Value, out _);
        }

        private static string RewriteMovedPath(
            string path,
            ProjectAssetChanges? sourceChanges)
        {
            if (string.IsNullOrWhiteSpace(path) || !sourceChanges.HasValue)
            {
                return path;
            }

            var moved = sourceChanges.Value.Moved ?? Array.Empty<string>();
            var movedFrom = sourceChanges.Value.MovedFromPaths ?? Array.Empty<string>();
            var bestIndex = -1;
            var bestLength = -1;
            for (var i = 0; i < moved.Length && i < movedFrom.Length; i++)
            {
                var oldPath = AssetPathFormat.Normalize(movedFrom[i]);
                var newPath = AssetPathFormat.Normalize(moved[i]);
                if (string.IsNullOrEmpty(oldPath)
                    || !AssetPathClassification.IsProjectAssetPath(newPath)
                    || !IsWithinFolderScope(path, oldPath)
                    || oldPath.Length <= bestLength)
                {
                    continue;
                }

                bestIndex = i;
                bestLength = oldPath.Length;
            }

            if (bestIndex < 0)
            {
                return path;
            }

            var bestOldPath = AssetPathFormat.Normalize(movedFrom[bestIndex]);
            var bestNewPath = AssetPathFormat.Normalize(moved[bestIndex]);
            return bestNewPath + path.Substring(bestOldPath.Length);
        }

        private static bool IsDeletedPath(
            string path,
            ProjectAssetChanges? sourceChanges)
        {
            if (!sourceChanges.HasValue)
            {
                return false;
            }

            var deleted = sourceChanges.Value.Deleted ?? Array.Empty<string>();
            for (var i = 0; i < deleted.Length; i++)
            {
                var deletedPath = AssetPathFormat.Normalize(deleted[i]);
                if (!string.IsNullOrEmpty(deletedPath)
                    && IsWithinFolderScope(path, deletedPath))
                {
                    return true;
                }
            }

            return false;
        }

        private void AccumulatePendingAssetChanges(ProjectAssetChanges changes)
        {
            if (changes.IsEmpty)
            {
                return;
            }

            _pendingImportedPaths.AddRange(changes.Imported);
            _pendingDeletedPaths.AddRange(changes.Deleted);
            for (var i = 0; i < changes.Moved.Length; i++)
            {
                _pendingMovedPaths.Add(changes.Moved[i]);
                _pendingMovedFromPaths.Add(
                    i < changes.MovedFromPaths.Length
                        ? changes.MovedFromPaths[i]
                        : string.Empty);
            }
        }

        private ProjectAssetChanges? TakePendingAssetChanges()
        {
            if (_pendingImportedPaths.Count == 0
                && _pendingDeletedPaths.Count == 0
                && _pendingMovedPaths.Count == 0)
            {
                return null;
            }

            var changes = new ProjectAssetChanges(
                _pendingImportedPaths.ToArray(),
                _pendingDeletedPaths.ToArray(),
                _pendingMovedPaths.ToArray(),
                _pendingMovedFromPaths.ToArray());
            _pendingImportedPaths.Clear();
            _pendingDeletedPaths.Clear();
            _pendingMovedPaths.Clear();
            _pendingMovedFromPaths.Clear();
            return changes.Normalize();
        }

        // Above this many selected paths the string scan below stops paying for
        // itself and the hash set wins. Real selections are one row or a handful.
        private const int SmallSelectionReconcileLimit = 8;

        private static List<AssetPath> ReconcileSelectedAssetPaths(
            IReadOnlyList<AssetPath> existingSelectedPaths,
            IReadOnlyList<ZepAssetEntry> assets,
            Func<AssetPath, bool> isVisibleThroughExpansion = null)
        {
            var selectedAssetPaths = new List<AssetPath>(existingSelectedPaths.Count);
            if (existingSelectedPaths.Count == 0)
            {
                return selectedAssetPaths;
            }

            // The early exit below only fires when EVERY selected path is present,
            // which is exactly what typing a query makes false — so the normal
            // keystroke walks all of `assets`, up to the whole result list. A
            // HashSet<AssetPath> probe hashes the full path OrdinalIgnoreCase for
            // every entry it walks; comparing the raw strings against a tiny
            // array settles a mismatch on the first differing character instead.
            if (existingSelectedPaths.Count <= SmallSelectionReconcileLimit)
            {
                var wantedPaths = new string[existingSelectedPaths.Count];
                for (var i = 0; i < wantedPaths.Length; i++)
                {
                    wantedPaths[i] = existingSelectedPaths[i].Value ?? string.Empty;
                }

                // Flagged per selected ENTRY, not per distinct path, so a
                // selection listing the same path twice reconciles the way the
                // set did: both entries survive together.
                var foundFlags = new bool[wantedPaths.Length];
                var remaining = wantedPaths.Length;
                for (var i = 0; i < assets.Count && remaining > 0; i++)
                {
                    var path = AssetPath.From(GetAssetPathAt(assets, i)).Value ?? string.Empty;
                    for (var k = 0; k < wantedPaths.Length; k++)
                    {
                        if (!foundFlags[k]
                            && string.Equals(
                                path,
                                wantedPaths[k],
                                StringComparison.OrdinalIgnoreCase))
                        {
                            foundFlags[k] = true;
                            remaining--;
                        }
                    }
                }

                for (var i = 0; i < existingSelectedPaths.Count; i++)
                {
                    if (foundFlags[i]
                        || (isVisibleThroughExpansion != null
                            && isVisibleThroughExpansion(existingSelectedPaths[i])))
                    {
                        selectedAssetPaths.Add(existingSelectedPaths[i]);
                    }
                }

                return selectedAssetPaths;
            }

            var wanted = new HashSet<AssetPath>();
            for (var i = 0; i < existingSelectedPaths.Count; i++)
            {
                wanted.Add(existingSelectedPaths[i]);
            }

            var found = new HashSet<AssetPath>();
            for (var i = 0; i < assets.Count && found.Count < wanted.Count; i++)
            {
                var path = AssetPath.From(GetAssetPathAt(assets, i));
                if (wanted.Contains(path))
                {
                    found.Add(path);
                }
            }

            for (var i = 0; i < existingSelectedPaths.Count; i++)
            {
                if (found.Contains(existingSelectedPaths[i])
                    || (isVisibleThroughExpansion != null
                        && isVisibleThroughExpansion(existingSelectedPaths[i])))
                {
                    selectedAssetPaths.Add(existingSelectedPaths[i]);
                }
            }

            return selectedAssetPaths;
        }

        private Func<AssetPath, bool> BuildExpansionVisibilityPredicate(
            ZepPaneState pane,
            string currentFolderPath)
        {
            if (pane.ExpandedAssetFolderPaths.Count == 0
                || pane.ActiveMode != AssetsPaneMode.Browser
                || pane.HasActiveAssetFilter())
            {
                return null;
            }

            // One reconcile runs this predicate once per selected path, and every
            // sibling of a flat expansion resolves through the same parent, so
            // the parent's contents are looked up once per build rather than
            // once per selected row.
            var contentsByParent = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>(
                StringComparer.OrdinalIgnoreCase);
            return path => IsVisibleThroughExpansion(
                pane,
                currentFolderPath,
                path.Value,
                contentsByParent);
        }

        // True when every ancestor between the path and the current folder is
        // a live, expanded folder — i.e. the row is really on screen inside an
        // expansion. Membership is re-checked against the browse cache at each
        // level so deleted or moved assets still fall out of the selection.
        private bool IsVisibleThroughExpansion(
            ZepPaneState pane,
            string currentFolderPath,
            string path,
            Dictionary<string, IReadOnlyList<ZepAssetEntry>> contentsByParent)
        {
            var child = path;
            // Bounded by real path depth; the guard only protects against a
            // GetParentFolder fixpoint that never reaches the current folder.
            for (var guard = 0; guard < 64; guard++)
            {
                var parent = GetParentFolder(child);
                if (string.IsNullOrEmpty(parent)
                    || string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!contentsByParent.TryGetValue(parent, out var siblings))
                {
                    siblings = _browseProjection.GetFolderContentsOrNull(parent);
                    contentsByParent[parent] = siblings;
                }

                // Browse buckets are sorted and expose IAssetPathLookup, so this
                // is the bucket's own OrdinalIgnoreCase binary search rather than
                // a scan of every sibling — a flat 5,000-child expansion used to
                // cost 5,000 scans of 5,000 entries per reconcile.
                if (siblings == null || FindAssetPathIndex(siblings, child) < 0)
                {
                    return false;
                }

                // A sidebar multi-selection lists several folders at the top
                // level; each of them roots an expansion like the current one.
                if (string.Equals(parent, currentFolderPath, StringComparison.OrdinalIgnoreCase)
                    || Contains(pane.AdditionalFolderPaths, AssetPath.From(parent)))
                {
                    return true;
                }

                if (!pane.IsAssetFolderExpanded(AssetPath.From(parent)))
                {
                    return false;
                }

                child = parent;
            }

            return false;
        }

        private static bool Contains(IReadOnlyList<AssetPath> paths, AssetPath path)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (paths[i] == path)
                {
                    return true;
                }
            }

            return false;
        }

        private IReadOnlyList<AssetRecord> RebuildProjectionCache()
        {
            ZepPerfLog.Count(ZepPerfCounter.RebuildProjectionCacheCalls);
            ZepPerfLog.Count(ZepPerfCounter.FullRecordEnumerations);
            using var measurement = ZepPerfLog.Measure(
                "rebuild-projection-cache",
                ZepPerfStage.ProjectionBuild);
            var __sw_enum = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            var records = new List<AssetRecord>(_index.Enumerate());
            ReconcileTagSearchText(records);
            records.Sort(static (a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
            if (__sw_enum != null)
            {
                __sw_enum.Stop();
                ZepPerfLog.Log($"[Zep/perf]   RPC.enumerate+sort recordCount={records.Count} ms={__sw_enum.Elapsed.TotalMilliseconds:F2}");
            }

            var __sw_getfolders = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            // Pull folder records via the synchronizer rather than the source directly.
            // On the cache-hit startup path the synchronizer returns the folder records
            // that were persisted alongside the record cache; other rebuilds fall through
            // to a fresh `IAssetRecordSource.GetFolderRecords` walk. See the synchronizer
            // method's comment for the why.
            var sourceFolders = _synchronizer.GetFolderRecordsForProjection();
            if (__sw_getfolders != null)
            {
                __sw_getfolders.Stop();
                ZepPerfLog.Log($"[Zep/perf]   RPC.synchronizer.GetFolderRecordsForProjection folderCount={sourceFolders.Count} ms={__sw_getfolders.Elapsed.TotalMilliseconds:F2}");
            }

            var __sw_folderbuild = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            var standardFolderEntries = _folderEntries.RebuildFromIndex(records, sourceFolders);
            if (__sw_folderbuild != null)
            {
                __sw_folderbuild.Stop();
                ZepPerfLog.Log($"[Zep/perf]   RPC._folderEntries.RebuildFromIndex standardCount={standardFolderEntries.Count} ms={__sw_folderbuild.Elapsed.TotalMilliseconds:F2}");
            }

            var __sw_tagrebuild = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            _tagProjection.RebuildCache(records, standardFolderEntries);
            if (__sw_tagrebuild != null)
            {
                __sw_tagrebuild.Stop();
                ZepPerfLog.Log($"[Zep/perf]   RPC._tagProjection.RebuildCache ms={__sw_tagrebuild.Elapsed.TotalMilliseconds:F2}");
            }

            var __sw_browserebuild = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            _browseProjection.RebuildCache(records, standardFolderEntries);
            if (__sw_browserebuild != null)
            {
                __sw_browserebuild.Stop();
                ZepPerfLog.Log($"[Zep/perf]   RPC._browseProjection.RebuildCache ms={__sw_browserebuild.Elapsed.TotalMilliseconds:F2}");
            }

            var __sw_filteropts = ZepPerfLog.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            _tagProjection.RebuildFilterOptions();
            if (__sw_filteropts != null)
            {
                __sw_filteropts.Stop();
                ZepPerfLog.Log($"[Zep/perf]   RPC._tagProjection.RebuildFilterOptions ms={__sw_filteropts.Elapsed.TotalMilliseconds:F2}");
            }

            AssetCatalogChanged?.Invoke(null);
            _hasBuiltProjectionCache = true;
            return records;
        }

        private void HandleCatalogChanged(AssetCatalogDelta delta)
        {
            AssetCatalogChanged?.Invoke(delta);
            if (CatalogTouchesCapturedAddressableFolders(delta))
            {
                HandleAddressablesChanged();
            }
        }

        private bool CatalogTouchesCapturedAddressableFolders(AssetCatalogDelta delta)
        {
            if (delta == null || _capturedAddressableFolderPaths.Count == 0)
            {
                return false;
            }

            foreach (var scope in _capturedAddressableFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(scope)) continue;
                foreach (var record in delta.RemovedRecords)
                {
                    if (IsWithinFolderScope(record.AssetPath, scope)) return true;
                }
                foreach (var record in delta.UpsertedRecords)
                {
                    if (IsWithinFolderScope(record.AssetPath, scope)) return true;
                }
                foreach (var folder in delta.RemovedFolders)
                {
                    if (IsWithinFolderScope(folder.FolderPath.Value, scope)
                        || IsWithinFolderScope(scope, folder.FolderPath.Value)) return true;
                }
                foreach (var folder in delta.UpsertedFolders)
                {
                    if (IsWithinFolderScope(folder.FolderPath.Value, scope)
                        || IsWithinFolderScope(scope, folder.FolderPath.Value)) return true;
                }
            }

            return false;
        }

        // A change the index already carries, applied somewhere else. The Bridge's
        // post-compile type-reconciliation job rebuilds one slice of type-ambiguous
        // records per editor tick straight into the index; this is where each slice's
        // delta reaches the derived caches and the panes, so a retype shows up while
        // the job runs instead of waiting for the whole operation.
        //
        // No empty-delta guard here on purpose: the job owns that decision, because
        // it is the only producer that can yield an empty delta and the projection
        // republication below is the half PublishIndexPatch cannot guard for it.
        public void ApplyIndexPatch(AssetCatalogDelta delta)
        {
            _crossCache.PublishIndexPatch(delta);
            ApplyProjectionOnly();
        }

        // Callback invoked by AssetChangeSyncer when an external batch was patched
        // incrementally (catalog + search postings + browse cache already current). The
        // projection caches don't need rebuilding — only the view model catches up.
        // This is the payoff of the incremental path: it skips RebuildProjectionCache
        // (the fresh folder-walk + folder-entry + tag + browse rebuild) entirely.
        private void ApplyProjectionOnly()
        {
            if (_isDisposed)
            {
                return;
            }

            ApplyProjection();
        }

        private static ZepSearchScope ResolveSelectedSearchScope(
            ZepSearchScope selectedSearchScope,
            IReadOnlyList<ZepSearchScopeOption> options)
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i].Scope == selectedSearchScope)
                {
                    return selectedSearchScope;
                }
            }

            return options.Count > 0
                ? options[0].Scope
                : ZepSearchScope.Assets;
        }

        private string ResolveSelectedTypeFilter(
            string selectedTypeFilter,
            string currentFolderPath)
        {
            if (string.IsNullOrWhiteSpace(selectedTypeFilter))
            {
                return string.Empty;
            }

            var options = _browseProjection.CachedTypeFilterOptions;

            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].TypeName, selectedTypeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return options[i].TypeName;
                }
            }

            return string.Empty;
        }

        private static IReadOnlyList<ZepSearchScopeOption> BuildSearchScopeOptions(
            ZepPaneState pane,
            string currentFolderPath)
        {
            if (!pane.BreadcrumbRootPath.IsEmpty)
            {
                var rootPath = pane.BreadcrumbRootPath.Value;
                var rootScope = AssetPathClassification.IsPackagesPath(rootPath)
                    ? ZepSearchScope.Packages
                    : ZepSearchScope.Assets;
                var rootedOptions = new List<ZepSearchScopeOption>
                {
                    new ZepSearchScopeOption(
                        rootScope,
                        $"'{GetFolderName(rootPath)}'")
                };
                if (!string.Equals(
                    currentFolderPath,
                    rootPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    rootedOptions.Add(new ZepSearchScopeOption(
                        ZepSearchScope.CurrentFolder,
                        $"'{GetFolderName(currentFolderPath)}'"));
                }

                return rootedOptions;
            }

            var options = new List<ZepSearchScopeOption>
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets")
            };

            if (!string.IsNullOrWhiteSpace(currentFolderPath)
                && !string.Equals(currentFolderPath, "Assets", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentFolderPath, "Packages", StringComparison.OrdinalIgnoreCase))
            {
                options.Add(new ZepSearchScopeOption(
                    ZepSearchScope.CurrentFolder,
                    $"'{GetFolderName(currentFolderPath)}'"));
            }

            return options;
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ZepController));
            }
        }

        // Only a tag-definition replay can change the definition set, so the
        // capture is skipped for every other command kind.
        private HashSet<string> CaptureTagDefinitionIdsForReplay(
            ProjectCommandKind? replayedKind)
        {
            return replayedKind == ProjectCommandKind.SetTagDefinitions
                ? CaptureTagDefinitionIds()
                : null;
        }

        private bool HandleUndoRedoResult(
            ZepPaneState pane,
            ProjectFileOperationResult result,
            string successMessage,
            ProjectCommandKind? replayedKind,
            HashSet<string> tagDefinitionIdsBeforeReplay = null)
        {
            if (result.Succeeded && result.TagDefinitionsChanged)
            {
                try
                {
                    RefreshTagPresentationAfterDefinitionReplay(
                        tagDefinitionIdsBeforeReplay,
                        result.TagAssignmentSnapshotChanged);
                }
                catch (ProjectTagStateUnavailableException exception)
                {
                    ReportTagPresentationUnavailable(exception);
                    return true;
                }
            }

            if (result.HasTagAssignments)
            {
                return _tagMutations.ApplyUndoRedoResult(result, successMessage);
            }

            return HandleOperationResult(pane, result, true, successMessage);
        }

        private bool ExecuteOperation(
            ZepPaneState pane,
            Func<ProjectFileOperationResult> operation,
            bool selectResultPath,
            string successMessage)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return false;
            }

            _isHandlingOperation = true;
            try
            {
                ProjectFileOperationResult result;
                using (ZepPerfLog.Measure(
                    successMessage,
                    ZepPerfStage.UnityBoundary))
                {
                    result = operation();
                }

                return HandleOperationResult(
                    pane,
                    result,
                    selectResultPath,
                    successMessage);
            }
            finally
            {
                _isHandlingOperation = false;
            }
        }

        // One operation, one publication. The body below mutates the view model
        // through up to four separate ApplyViewModelState scopes (reconcile, the
        // expansion-key rewrite, the reveal, the closing ApplyProjection), and
        // each of those disposed its deferral at depth 0 and dispatched — so a
        // single rename asked the shell to re-bind its folder two to four times.
        // Nesting them all inside one deferral makes the inner scopes no-ops at
        // dispose time and leaves exactly one dispatch, raised here, outside
        // ApplyViewModelState (i.e. with _isApplyingState already false) so the
        // shell still sees it. Spec §4.2: one publication per editor turn.
        private bool HandleOperationResult(
            ZepPaneState pane,
            ProjectFileOperationResult result,
            bool selectResultPath,
            string successMessage)
        {
            using (_viewModel.DeferNotifications())
            {
                return HandleOperationResultCore(
                    pane,
                    result,
                    selectResultPath,
                    successMessage);
            }
        }

        private bool HandleOperationResultCore(
            ZepPaneState pane,
            ProjectFileOperationResult result,
            bool selectResultPath,
            string successMessage)
        {
            if (!result.Succeeded)
            {
                SetStatus(result.ErrorMessage);
                if (_bulkDepth > 0)
                {
                    _pendingBulkStatusPublication = true;
                }
                _refreshScheduler.Request(false);
                return false;
            }

            var sourceChanges = BuildOperationSourceChanges(result);
            if (_bulkDepth > 0)
            {
                _pendingBulkOperationUpdates.Add(
                    new ProjectAssetOperationUpdate(
                        result.AssetPath,
                        result.PreviousPath));
                if (sourceChanges.HasValue)
                {
                    AccumulatePendingAssetChanges(sourceChanges.Value);
                }
            }
            else
            {
                if (sourceChanges.HasValue)
                {
                    AccumulatePendingAssetChanges(sourceChanges.Value);
                }

                if (!_synchronizationPresentationBlocked)
                {
                    try
                    {
                        var tagVersion = _assetGateway.TagVersion;
                        using (ZepPerfLog.Measure(
                            successMessage,
                            ZepPerfStage.CatalogMutation))
                        {
                            _crossCache.ApplyIncrementalOperationUpdate(
                                result.AssetPath,
                                result.PreviousPath);
                        }

                        EnsureSharedProjectionCaches(tagVersion);
                        var pendingChanges = TakePendingAssetChanges();
                        ApplyViewModelState(() => ReconcileAllPanes(pendingChanges));
                    }
                    catch (ProjectTagStateUnavailableException exception)
                    {
                        _synchronizationPresentationBlocked = true;
                        _hasBuiltProjectionCache = false;
                        _globalReconciliationComplete = false;
                        ReportTagPresentationUnavailable(exception);
                    }
                }
            }

            // Inline expansion is keyed by path and nothing else rewrites it, so
            // a renamed/moved folder would reopen collapsed and its old key
            // would pre-expand whatever is created at that path next.
            if (IsRenameOrMoveResult(result, out var previousPath))
            {
                ApplyPaneState(pane, originatingPane =>
                    originatingPane.RewriteExpandedAssetFolders(
                        AssetPath.From(previousPath),
                        AssetPath.From(result.AssetPath)));
                // 5.4: the sidebar's favorites are keyed by path too, and dropped
                // the row instead of following the folder. Reported rather than
                // applied here: the store lives on the UI side of the seam.
                AssetPathRewritten?.Invoke(previousPath, result.AssetPath);
            }

            if (_bulkDepth == 0 && _synchronizationPresentationBlocked)
            {
                _viewModel.SetCanUndoRedo(_fileCommands.CanUndo, _fileCommands.CanRedo);
                _viewModel.SetStatusMessage(_statusMessage);
                return true;
            }

            // A reveal is a statement about ONE result. Run per item inside a
            // bulk it navigated the pane to each destination in turn and left
            // the LAST item as the whole batch's selection — a 50-item move
            // ended selecting one file the user never singled out. But a batch
            // of one is not a batch (a single-item drop, Cmd+D on one asset),
            // and there its reveal is exactly right, so the candidate is
            // remembered and CompleteBulkOperation applies it only if it stayed
            // alone. Batch callers own a real batch's selection instead
            // (AssetDropOperations.MoveSelectedPaths, DeleteAssetsSelectingNeighbor).
            if (_bulkDepth > 0)
            {
                if (selectResultPath && IsRevealableResultPath(result.AssetPath))
                {
                    _pendingBulkRevealCandidates++;
                    _pendingBulkRevealPane = pane;
                    _pendingBulkRevealResult = result;
                }
            }
            else if (selectResultPath)
            {
                RevealOperationResult(pane, result);
            }

            SetStatus(successMessage);
            if (_bulkDepth == 0)
            {
                ApplyProjection(sourceChanges: null, sharedCachesAreCurrent: true);
            }
            return true;
        }

        private static bool IsRevealableResultPath(string resultAssetPath)
        {
            return !string.IsNullOrEmpty(resultAssetPath)
                && !Path.IsPathRooted(resultAssetPath);
        }

        private static bool IsRenameOrMoveResult(
            ProjectFileOperationResult result,
            out string previousPath)
        {
            previousPath = AssetPathFormat.Normalize(result.PreviousPath);
            return !string.IsNullOrEmpty(previousPath)
                && IsRevealableResultPath(result.AssetPath)
                && !string.Equals(
                    previousPath,
                    GetParentFolder(result.AssetPath),
                    StringComparison.OrdinalIgnoreCase);
        }

        private void RevealOperationResult(
            ZepPaneState pane,
            ProjectFileOperationResult result)
        {
            if (!_viewModel.IsLivePane(pane) || !IsRevealableResultPath(result.AssetPath))
            {
                return;
            }

            var isRenameOrMove = IsRenameOrMoveResult(result, out var previousPath);
            {
                var currentFolder = AssetPathFormat.Normalize(pane.CurrentFolderPath);

                if (isRenameOrMove
                    && !string.IsNullOrEmpty(currentFolder)
                    && ZepPathHelpers.IsWithinFolderScope(currentFolder, previousPath))
                {
                    // Renaming/moving the folder the user is currently INSIDE (or
                    // an ancestor of it) must keep them inside it — Finder/Explorer/
                    // Unity stay put and rewrite the path. Prefix-substitute the old
                    // path for the new one so an ancestor rename keeps the user at
                    // their depth instead of ejecting them to the parent.
                    var rewritten = AssetPath.From(result.AssetPath + currentFolder.Substring(previousPath.Length));
                    ApplyPaneState(pane, originatingPane =>
                    {
                        originatingPane.TrySetCurrentFolderPath(rewritten);
                        originatingPane.TrySetSelectedFolderPath(rewritten);
                        originatingPane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
                    });
                }
                else
                {
                    // Reveal, don't enter: show the result inside its parent
                    // container and select it — folders included. Treating a
                    // folder result as the new current folder made every folder
                    // rename/duplicate jump the panel INSIDE that folder
                    // (Finder/Explorer stay in the container and select it).
                    var resultPath = AssetPath.From(result.AssetPath);
                    var containerPath = AssetPath.From(GetParentFolder(result.AssetPath));
                    if (!containerPath.IsEmpty)
                    {
                        ApplyPaneState(pane, originatingPane =>
                        {
                            // This reveal is a navigation, so a live search
                            // ends with it — left set, the pane keeps
                            // projecting the SEARCH's results, which need not
                            // contain the result path (a created folder's
                            // name rarely matches the query), and
                            // reconciliation prunes the selection made here:
                            // the just-created item was nowhere on screen.
                            // Cleared before the selection so it isn't
                            // recorded as the picked search result.
                            originatingPane.SetSearchText(string.Empty);
                            originatingPane.TrySetCurrentFolderPath(containerPath);
                            originatingPane.TrySetSelectedFolderPath(containerPath);
                            originatingPane.SetSelectedAssetPaths(new[] { resultPath });
                        });
                    }
                }
            }
        }

        private static ProjectAssetChanges? BuildOperationSourceChanges(
            ProjectFileOperationResult result)
        {
            var previousPath = AssetPathFormat.Normalize(result.PreviousPath);
            if (!AssetPathClassification.IsProjectAssetPath(previousPath))
            {
                return null;
            }

            var resultPath = AssetPathFormat.Normalize(result.AssetPath);
            if (!AssetPathClassification.IsProjectAssetPath(resultPath))
            {
                return new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { previousPath },
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            if (string.Equals(
                previousPath,
                GetParentFolder(resultPath),
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { resultPath },
                new[] { previousPath });
        }

        private void RefreshIndex(bool emitStatusToast)
        {
            ThrowIfDisposed();
            _panelUseStarted = true;

            if (emitStatusToast)
            {
                _refreshStatusPending = true;
                SetStatus("Refreshing...");
                // Pending filesystem mappings describe the cache state we are
                // about to build, not the still-visible old cache. Consume them
                // only after Synchronize/RebuildProjectionCache below.
                ApplyViewModelState(() =>
                    ApplyGlobalProjectionState(_folderEntries.Cached));
            }

            _presentationCache.Clear();
            using (ZepPerfLog.Measure(
                "full-refresh",
                ZepPerfStage.CatalogMutation))
            {
                if (_indexSynchronizationInProgress)
                {
                    // The synchronizer adopts its active authoritative operation.
                    // Its one controller callback below owns projection publication;
                    // repeated refresh clicks only request the same result.
                    _synchronizer.Synchronize();
                    return;
                }

                var completionEntered = false;
                _indexSynchronizationInProgress = true;
                BeginIndexSynchronization();
                try
                {
                    SynchronizeWithOutcome(result =>
                    {
                        completionEntered = true;
                        CompleteIndexSynchronization(result);
                    });
                }
                catch (Exception exception)
                {
                    if (!completionEntered)
                    {
                        HandleIndexSynchronizationFailure(
                            exception.GetType().Name + ": " + exception.Message);
                    }

                    throw;
                }
            }
        }

        // InMemoryAssetIndex.Version moves for records AND folders, so one capture
        // covers both halves of the catalog. Taken before the synchronizer starts
        // so the completion below can tell "the project really changed" from "the
        // authoritative pass confirmed what we already had".
        private void BeginIndexSynchronization()
        {
            _catalogVersionAtSynchronizationStart = _index.Version;
        }

        private void SynchronizeAsyncWithOutcome(
            Action onFullScanStarted,
            Action<AssetIndexSynchronizationResult> onComplete)
        {
            if (_synchronizer is IAssetIndexSynchronizationResults results)
            {
                results.SynchronizeAsyncWithOutcome(onFullScanStarted, onComplete);
                return;
            }

            _synchronizer.SynchronizeAsync(
                onFullScanStarted,
                () => onComplete?.Invoke(AssetIndexSynchronizationResult.Success));
        }

        private void SynchronizeWithOutcome(Action<AssetIndexSynchronizationResult> onComplete)
        {
            if (_synchronizer is IAssetIndexSynchronizationResults results)
            {
                results.SynchronizeWithOutcome(onComplete);
                return;
            }

            _synchronizer.Synchronize(
                () => onComplete?.Invoke(AssetIndexSynchronizationResult.Success));
        }

        private void CompleteIndexSynchronization(AssetIndexSynchronizationResult result)
        {
            if (_isDisposed)
            {
                return;
            }

            if (result == null || !result.Succeeded)
            {
                HandleIndexSynchronizationFailure(
                    result?.ErrorMessage ?? "The index could not be synchronized.");
                return;
            }

            var indexingError = _indexSynchronizationError;
            _sourceSynchronizationFailed = false;
            CompleteIndexSynchronization();
            if (!_synchronizationPresentationBlocked)
            {
                _indexSynchronizationError = null;
                if (indexingError != null
                    && string.Equals(_statusMessage, indexingError, StringComparison.Ordinal))
                {
                    SetStatus("Ready");
                    _viewModel.SetStatusMessage(_statusMessage);
                }
            }
        }

        private void HandleIndexSynchronizationFailure(string errorMessage)
        {
            if (_isDisposed)
            {
                return;
            }

            _indexSynchronizationInProgress = false;
            _refreshStatusPending = false;
            _sourceSynchronizationFailed = true;
            _globalReconciliationComplete = false;
            _hasBuiltProjectionCache = false;
            _synchronizationPresentationBlocked = true;
            _viewModel.SetIsIndexScanInProgress(false);
            _indexSynchronizationError = "Indexing failed: " + errorMessage + " Retry with Refresh.";
            SetStatus(_indexSynchronizationError);
            _viewModel.SetStatusMessage(_statusMessage);
        }

        private void CompleteIndexSynchronization()
        {
            if (_isDisposed)
            {
                return;
            }

            _indexSynchronizationInProgress = false;
            var publishReadyStatus = _refreshStatusPending;
            var deferredWorkWasIdleAtEntry =
                !_deferredStartupWorkQueued && _deferredWorkerTask == null;
            _refreshStatusPending = false;
            var previouslyAcknowledgedTagVersion = _tagProjection.ObservedTagVersion;
            IReadOnlyList<AssetRecord> records = null;
            try
            {
                if (!TryPrepareTagProjection(out var tagVersion))
                {
                    _hasBuiltProjectionCache = false;
                    _synchronizationPresentationBlocked = true;
                    return;
                }

                var catalogChanged = _synchronizationPresentationBlocked
                    || !_hasBuiltProjectionCache
                    || _index.Version != _catalogVersionAtSynchronizationStart;
                if (catalogChanged)
                {
                    records = RebuildProjectionCache();
                }

                EnsureSharedProjectionCaches(tagVersion, tagsAreCurrent: catalogChanged);
                _tagProjection.ObservedTagVersion = tagVersion;
                _synchronizationPresentationBlocked = false;
                _globalReconciliationComplete = true;
                ApplyProjection(TakePendingAssetChanges(), sharedCachesAreCurrent: true);
                CompleteTagStateRecovery();

                MarkPaneSourceReconciliationReady();
                if (records != null)
                {
                    ScheduleDeferredStartupWork();
                }
                else if (publishReadyStatus
                    && _deferredStartupWorkUnfinished
                    && deferredWorkWasIdleAtEntry
                    && !_deferredStartupWorkQueued
                    && _deferredWorkerTask == null)
                {
                    _deferredWorkerFaultRetryUsed = false;
                    _deferredWorkerMismatchRetries = 0;
                    ScheduleDeferredStartupWork();
                }
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                _tagProjection.ObservedTagVersion = previouslyAcknowledgedTagVersion;
                _hasBuiltProjectionCache = false;
                _synchronizationPresentationBlocked = true;
                ReportTagPresentationUnavailable(exception);
            }
            catch
            {
                _tagProjection.ObservedTagVersion = previouslyAcknowledgedTagVersion;
                _hasBuiltProjectionCache = false;
                _synchronizationPresentationBlocked = true;
                throw;
            }
            finally
            {
                _viewModel.SetIsIndexScanInProgress(false);
                if (publishReadyStatus)
                {
                    if (string.Equals(_statusMessage, "Refreshing...", StringComparison.Ordinal)
                        && !string.Equals(
                            _statusMessage,
                            _tagStateRecoveryError,
                            StringComparison.Ordinal))
                    {
                        SetStatus("Ready");
                    }

                    _viewModel.SetStatusMessage(_statusMessage);
                }
            }
        }

        private void MarkPaneSourceReconciliationReady()
        {
            if (IsPaneSourceReconciliationReady)
            {
                return;
            }

            IsPaneSourceReconciliationReady = true;
            PaneSourceReconciliationReady?.Invoke();
        }

        // The first non-empty search this project has ever run. Persisting it is the
        // composer's job (an editor preference keyed by project path, so it survives
        // the editor restart the observation has to outlive); the flag is latched
        // here so a whole session of typing writes it once.
        private void MarkSearchUsedInThisProject()
        {
            if (_searchWasUsedInThisProject)
            {
                return;
            }

            _searchWasUsedInThisProject = true;
            _onSearchFirstUsedInThisProject?.Invoke();
        }

        private void ScheduleDeferredStartupWork()
        {
            _deferredStartupWorkUnfinished = true;
            _deferredStartupWorkQueued = true;
            _pendingDeferredStartupSkipsSearchBuild = false;
            _deferredScheduler.Schedule(_deferredStartupCallback);
        }

        // The Addressables settings changed under a live panel (an entry
        // added, removed or moved), so the entry set captured at startup is
        // stale. Coalesced against the deferred worker: a run that is queued
        // but not started captures the new settings when it runs; a run in
        // flight captured the old ones, so one more run is requested once its
        // result lands; an idle controller queues one now.
        private void HandleAddressablesChanged()
        {
            if (_isDisposed || _deferredStartupWorkQueued)
            {
                return;
            }

            if (_deferredWorkerTask != null)
            {
                _addressablesChangedWhileWorkerRan = true;
                return;
            }

            ScheduleAddressablesOnlyDeferredWork();
        }

        private void ScheduleAddressablesOnlyDeferredWork()
        {
            ScheduleDeferredStartupWork();
            _pendingDeferredStartupSkipsSearchBuild = true;
        }

        private void RunDeferredStartupWork()
        {
            var wasQueued = _deferredStartupWorkQueued;
            var skipSearchBuild = _pendingDeferredStartupSkipsSearchBuild;
            _deferredStartupWorkQueued = false;
            _pendingDeferredStartupSkipsSearchBuild = false;
            if (_isDisposed || !wasQueued)
            {
                return;
            }

            var records = new List<AssetRecord>(_index.Enumerate());
            var expectedRecordVersion = _index.RecordVersion;
            // Reflection and AssetDatabase folder classification remain on
            // the editor thread; project-wide record expansion does not.
            var addressablesCapture = _addressablesQuery.Capture();
            _capturedAddressableFolderPaths = addressablesCapture?.IsComplete == true
                ? addressablesCapture.FolderPaths
                : Array.Empty<string>();
            var hasAddressableFolderScopes = _capturedAddressableFolderPaths.Count > 0;
            // 4.32(b): the addressables badge is on every row, so that half is
            // unconditional. The search index is ~1.35 GB of postings nothing reads
            // until someone types, so it waits for evidence that this project's user
            // does. HasBuiltIndex keeps the compaction request (which arrives only on
            // an index that already exists) on the worker path it was moved to.
            var buildSearchIndex =
                !skipSearchBuild
                && (_searchWasUsedInThisProject || _searchService.HasBuiltIndex);

            _deferredWorkerTask = Task.Run(() =>
            {
                var addressableGuids = _addressablesQuery.Expand(
                    addressablesCapture,
                    records);
                var searchBuild = buildSearchIndex
                    ? AssetSearchService.BuildOnWorker(
                        records,
                        expectedRecordVersion)
                    : null;
                return new DeferredWorkerResult(
                    addressableGuids, searchBuild, expectedRecordVersion, hasAddressableFolderScopes);
            });
            _deferredScheduler.Schedule(_deferredWorkerPollCallback);
        }

        private void PollDeferredWorker()
        {
            var task = _deferredWorkerTask;
            if (_isDisposed || task == null)
            {
                return;
            }

            if (!task.IsCompleted)
            {
                _deferredScheduler.Schedule(_deferredWorkerPollCallback);
                return;
            }

            _deferredWorkerTask = null;

            // Cancellation is a deliberate teardown, never a failure — stay quiet.
            if (task.IsCanceled)
            {
                return;
            }

            if (task.IsFaulted)
            {
                // Reading Exception observes the fault, so it can no longer
                // resurface later as an unobserved-task exception. Then take the
                // version-mismatch retry below — ONCE. Without the latch a
                // deterministic fault (a broken addressables provider, a type
                // load) would re-arm itself on every tick for the whole session.
                var fault = task.Exception;
                var retryScheduled = !_deferredWorkerFaultRetryUsed;
                if (retryScheduled)
                {
                    _deferredWorkerFaultRetryUsed = true;
                    // The retry captures the settings afresh.
                    _addressablesChangedWhileWorkerRan = false;
                    ScheduleDeferredStartupWork();
                }

                // Core has no log channel of its own (the assembly carries no
                // engine reference). The deferred scheduler is this callback's
                // boundary and already reports a throwing panel callback with the
                // panel's attribution and the full exception, so the fault leaves
                // through it rather than being swallowed here.
                throw new InvalidOperationException(
                    "Zep's deferred index worker failed"
                    + (retryScheduled
                        ? "; one retry was scheduled."
                        : " again; search postings and the addressables badge stay stale until the next refresh."),
                    fault);
            }

            var result = task.Result;
            var addressableFoldersAreStale = result.HasAddressableFolderScopes
                && result.RecordVersion != _index.RecordVersion;
            if (addressableFoldersAreStale)
            {
                _addressablesChangedWhileWorkerRan = true;
            }

            // Search retains its bounded mismatch policy. Folder membership also
            // depends on the catalog, even when no search build was requested.
            if (result.SearchBuild != null
                && !_searchService.TryAdoptWorkerBuild(result.SearchBuild))
            {
                // Catalog generation changed while the worker was building. Its
                // immutable snapshot is discarded; retry against the live owner
                // — but only twice. A project that keeps mutating through
                // startup (an import queue, a package resolve) loses the race
                // every time, and an unbounded retry re-enumerated every record
                // and rebuilt the whole search index on every poll for the rest
                // of the session. Giving up is safe: EnsureSearchIndexIsCurrent
                // builds the index lazily on the first Search().
                if (_deferredWorkerMismatchRetries >= MaxDeferredWorkerMismatchRetries)
                {
                    _deferredWorkerMismatchRetries = 0;
                    // Core carries no engine reference, so the perf log is its
                    // only sink; it reaches the console through Bridge's Sink
                    // when panel diagnostics are on.
                    ZepPerfLog.Log(
                        "[Zep] The deferred search-index build lost the catalog race "
                        + MaxDeferredWorkerMismatchRetries
                        + " times; the search index will be built on first use.");
                    // The discarded result also carried the addressables set,
                    // so a change that arrived meanwhile still needs its run.
                    RunAddressablesChangeIfWorkerMissedIt();
                    return;
                }

                _deferredWorkerMismatchRetries++;
                // The retry captures the settings afresh.
                _addressablesChangedWhileWorkerRan = false;
                ScheduleDeferredStartupWork();
                return;
            }

            // A completed build clears the fault latch, so an unrelated failure
            // later in the session still gets its own single retry, and the
            // mismatch budget starts over for the next build.
            _deferredWorkerFaultRetryUsed = false;
            _deferredWorkerMismatchRetries = 0;
            if (!_deferredStartupWorkQueued)
            {
                _deferredStartupWorkUnfinished = false;
            }

            if (!addressableFoldersAreStale)
            {
                _presentationCache.SetAddressableAssetGuids(
                    result.AddressableGuids
                        ?? new System.Collections.Generic.HashSet<string>(
                            StringComparer.OrdinalIgnoreCase));
                ApplyProjection();
            }
            RunAddressablesChangeIfWorkerMissedIt();
        }

        private void RunAddressablesChangeIfWorkerMissedIt()
        {
            if (!_addressablesChangedWhileWorkerRan)
            {
                return;
            }

            _addressablesChangedWhileWorkerRan = false;
            // A queued full run already captures the latest settings and catalog;
            // do not downgrade it to Addressables-only work.
            if (!_deferredStartupWorkQueued)
            {
                ScheduleAddressablesOnlyDeferredWork();
            }
        }

        private sealed class DeferredWorkerResult
        {
            internal DeferredWorkerResult(
                System.Collections.Generic.HashSet<string> addressableGuids,
                AssetSearchService.WorkerBuild searchBuild,
                int recordVersion,
                bool hasAddressableFolderScopes)
            {
                AddressableGuids = addressableGuids;
                SearchBuild = searchBuild;
                RecordVersion = recordVersion;
                HasAddressableFolderScopes = hasAddressableFolderScopes;
            }

            internal System.Collections.Generic.HashSet<string> AddressableGuids { get; }

            internal AssetSearchService.WorkerBuild SearchBuild { get; }

            internal int RecordVersion { get; }

            internal bool HasAddressableFolderScopes { get; }
        }

        private bool SyncTagAssignmentsIfNeeded()
        {
            return SyncTagAssignmentsIfNeeded(_assetGateway.TagVersion);
        }

        private bool SyncTagAssignmentsIfNeeded(int currentTagVersion)
        {
            if (!_tagPresentationReconciliationPending
                && currentTagVersion == _tagProjection.ObservedTagVersion)
            {
                return false;
            }

            RefreshTagPresentationCache(currentTagVersion);
            _tagPresentationReconciliationPending = false;
            return true;
        }

        private bool TryPrepareTagProjection(out int tagVersion)
        {
            try
            {
                tagVersion = _assetGateway.TagVersion;
                return true;
            }
            catch (ProjectTagStateUnavailableException exception)
            {
                tagVersion = default;
                ReportTagPresentationUnavailable(exception);
                return false;
            }
        }

        private void ReportTagPresentationUnavailable(ProjectTagStateUnavailableException exception)
        {
            SetStatus(exception.Message);
            _tagStateRecoveryError = _statusMessage;
            _tagPresentationReconciliationPending = true;
            _viewModel.SetStatusMessage(_statusMessage);
        }

        private void CompleteTagStateRecovery()
        {
            if (_tagStateRecoveryError == null)
            {
                return;
            }

            if (string.Equals(_statusMessage, _tagStateRecoveryError, StringComparison.Ordinal))
            {
                SetStatus("Ready");
                _viewModel.SetStatusMessage(_statusMessage);
            }

            _tagStateRecoveryError = null;
        }

        // The assignment file itself changed under us (a git pull, an external
        // editor). Nothing local knows WHICH paths moved, so this is the one tag
        // path that still has to read every record. A definition edit made through
        // the panel takes RefreshTagPresentationForTaggedPaths instead.
        private void ReconcileTagSearchText(List<AssetRecord> records)
        {
            List<AssetRecord> recordUpserts = null;
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var tagIds = _assetGateway.GetTagIds(
                    AssetPath.From(record.AssetPath),
                    record.Guid);
                // WHY: Most assets have no tags. Skipping BuildTagSearchText for the
                // tagless-and-still-tagless case avoids per-asset string allocations
                // on every tag mutation in a large project.
                if ((tagIds == null || tagIds.Count == 0) && string.IsNullOrEmpty(record.TagSearchText))
                {
                    continue;
                }

                var tagSearchText = _tagDefinitions.BuildTagSearchText(tagIds);
                if (string.Equals(record.TagSearchText, tagSearchText, StringComparison.Ordinal))
                {
                    continue;
                }

                var updated = AssetRecord.WithTagSearchText(record, tagSearchText);
                // One catalog transaction and one search delta for the whole
                // sweep: applying per record made the search mirror re-index the
                // same corpus once per changed asset.
                (recordUpserts ??= new List<AssetRecord>()).Add(updated);
                records[i] = updated;
            }

            if (recordUpserts != null)
            {
                var delta = _index.ApplyFileChanges(
                    recordUpserts,
                    Array.Empty<string>());
                _searchService.ApplyCatalogDelta(delta);
            }
        }

        private void RefreshTagPresentationCache(int observedTagVersion)
        {
            ZepPerfLog.Count(ZepPerfCounter.TagPresentationRebuilds);
            var records = new List<AssetRecord>(_index.Enumerate());
            ReconcileTagSearchText(records);
            records.Sort(static (a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
            var standardFolders = _folderEntries.GetStandardEntries();
            _tagProjection.RebuildCache(records, standardFolders);
            _browseProjection.RebuildCache(records, standardFolders);
            // A first-use tag-version reconciliation can run after bounded direct
            // observations but before authoritative hydration. RebuildCache owns
            // authoritative records, so restore only those already-observed folder
            // buckets from the pending filesystem facts it deliberately omits.
            _browseProjection.RefreshParentFolders(_index.ObservedFolders);
            _tagProjection.RebuildFilterOptions();
            _tagProjection.ObservedTagVersion = observedTagVersion;
        }

        // §3.1 fix 2. A tag DEFINITION edit renames/recolours chips and can delete
        // a definition; it never assigns a tag. The affected set is therefore the
        // paths the projection already lists as tagged — tens of them, not the
        // whole catalog — so neither the record enumeration nor the browse/tag
        // cache rebuild that RefreshTagPresentationCache pays for is needed.
        // unassignedTagIds are the definition ids the edit also removed from the
        // assignment store (delete / reset), i.e. the only way a path can LOSE a
        // chip here.
        private void RefreshTagPresentationForTaggedPaths(
            IReadOnlyCollection<string> unassignedTagIds,
            int observedTagVersion)
        {
            var affectedPaths =
                _tagProjection.RefreshResolvedTagsForTaggedPaths(unassignedTagIds);
            List<AssetRecord> recordUpserts = null;
            var parentFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < affectedPaths.Count; i++)
            {
                var path = affectedPaths[i];
                parentFolders.Add(ZepPathHelpers.GetParentFolder(path));
                if (!_index.TryGetByPath(path, out var record))
                {
                    continue;
                }

                var tagSearchText = _tagDefinitions.BuildTagSearchText(
                    ToTagIds(_tagProjection.GetTagsForPath(path)));
                if (string.Equals(record.TagSearchText, tagSearchText, StringComparison.Ordinal))
                {
                    continue;
                }

                (recordUpserts ??= new List<AssetRecord>()).Add(
                    AssetRecord.WithTagSearchText(record, tagSearchText));
            }

            if (recordUpserts != null)
            {
                var delta = _index.ApplyFileChanges(
                    recordUpserts,
                    Array.Empty<string>());
                _searchService.ApplyCatalogDelta(delta);
            }

            if (!_globalReconciliationComplete)
            {
                parentFolders.UnionWith(_index.ObservedFolders);
            }

            _tagProjection.RebuildFilterOptions();
            _browseProjection.RefreshParentFolders(parentFolders);
            _tagProjection.ObservedTagVersion = observedTagVersion;
        }

        private static IReadOnlyList<string> ToTagIds(
            IReadOnlyList<ZepTag> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return Array.Empty<string>();
            }

            var ids = new string[tags.Count];
            for (var i = 0; i < tags.Count; i++)
            {
                ids[i] = tags[i].Id;
            }

            return ids;
        }

        // The definition ids the panel currently knows, captured so a replay can
        // be classified before its projection is refreshed.
        private HashSet<string> CaptureTagDefinitionIds()
        {
            var definitions = _tagDefinitions.GetOrderedTags();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < definitions.Count; i++)
            {
                ids.Add(definitions[i].Id);
            }

            return ids;
        }

        // The definition ids that left the set — the only ids a definition change
        // can also strip from the assignment store, so the only way a tagged path
        // can lose a chip without the store being re-read.
        private IReadOnlyCollection<string> FindUnassignedTagIds(
            HashSet<string> definitionIdsBefore,
            out bool addedAnyDefinitionId)
        {
            addedAnyDefinitionId = false;
            if (definitionIdsBefore == null)
            {
                return Array.Empty<string>();
            }

            var definitionIdsAfter = CaptureTagDefinitionIds();
            foreach (var id in definitionIdsAfter)
            {
                if (!definitionIdsBefore.Contains(id))
                {
                    addedAnyDefinitionId = true;
                    break;
                }
            }

            var removed = new List<string>();
            foreach (var id in definitionIdsBefore)
            {
                if (!definitionIdsAfter.Contains(id))
                {
                    removed.Add(id);
                }
            }

            return removed;
        }

        private void ApplyExternalSelection(IReadOnlyList<string> assetPaths)
        {
            var typedAssetPaths = ToAssetPaths(assetPaths);
            var primaryPane = _viewModel.PrimaryPane;
            if (!ReferenceEquals(_viewModel.ActivePane, primaryPane))
            {
                _viewModel.ActivatePane(primaryPane);
            }

            ApplyPaneState(primaryPane, pane =>
            {
                pane.SetSelectedAssetPaths(typedAssetPaths);
            });

            ApplyProjection(primaryPane);
        }

        private static IReadOnlyList<AssetPath> ToAssetPaths(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return Array.Empty<AssetPath>();
            }

            var converted = new AssetPath[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                converted[i] = AssetPath.From(paths[i]);
            }

            return converted;
        }

        private void ApplyViewModelState(Action applyState)
        {
            if (applyState == null)
            {
                return;
            }

            _isApplyingState = true;
            try
            {
                using (_viewModel.DeferNotifications())
                {
                    applyState();
                }
            }
            finally
            {
                _isApplyingState = false;
            }
        }

        private void ApplyPaneState(
            ZepPaneState pane,
            Action<ZepPaneState> applyState)
        {
            if (applyState == null || !_viewModel.IsLivePane(pane))
            {
                return;
            }

            ApplyViewModelState(() =>
            {
                if (_viewModel.IsLivePane(pane))
                {
                    applyState(pane);
                }
            });
        }

        private void SetStatus(string message)
        {
            _statusMessage = string.IsNullOrWhiteSpace(message) ? "Ready" : message;
        }

        // The status the last operation produced. A failed operation stores its
        // reason here WITHOUT flushing a projection, so ZepState's copy
        // is still the previous value at that point; batch callers read this to
        // name the first failure in an aggregate status.
        public string StatusMessage => _statusMessage;

        // Public surface for UI to surface a transient status without going
        // through an asset operation. Used by the compile-busy guard on
        // Cmd+Z/Cmd+Shift+Z so the panel can tell the user *why* the
        // shortcut declined instead of silently no-op'ing.
        public void ShowStatusMessage(string message)
        {
            SetStatus(message);
            ApplyProjection();
        }
    }
}
