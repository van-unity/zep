using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.Core.Application
{
    public sealed class ZepControllerPaneProjectionTests
    {
        private ZepState _state;
        private ZepController _controller;
        private MutableRecordSource _source;
        private InMemoryAssetIndex _index;
        private AssetSearchService _searchService;
        private FakeGateway _gateway;
        private TagDefinitionStore _tagDefinitions;
        private QueuedScheduler _scheduler;
        private FakeSynchronizer _synchronizer;
        // Held rather than constructed inline so a test can read the undo stack
        // and count the execution-group notifications the Bridge maps onto
        // Unity's native undo timeline.
        private ProjectCommandHistory _commandHistory;
        private ProjectFileCommandService _fileCommands;
        private string _compressionWorkspace;

        [SetUp]
        public void SetUp()
        {
            _compressionWorkspace = null;
            _source = new MutableRecordSource(
                new[]
                {
                    new AssetRecord(
                        "hero-guid",
                        "Assets/Art/Hero.png",
                        "Hero",
                        "Texture2D"),
                    new AssetRecord(
                        "icon-guid",
                        "Assets/Art/UI/Icon.png",
                        "Icon",
                        "Texture2D"),
                    new AssetRecord(
                        "theme-guid",
                        "Assets/Audio/Theme.wav",
                        "Theme",
                        "AudioClip"),
                    new AssetRecord(
                        "leak-guid",
                        "Assets/Art2/Leak.png",
                        "Leak",
                        "Texture2D"),
                    new AssetRecord(
                        "config-guid",
                        "Assets/Art/ProjectConfiguration.asset",
                        "ProjectConfiguration",
                        "ProjectConfiguration",
                        baseTypeNames: new[] { "ScriptableObject", "Object" })
                },
                new[]
                {
                    "Assets",
                    "Assets/Art",
                    "Assets/Art/UI",
                    "Assets/Audio",
                    "Assets/Art2",
                    "Packages"
                });
            _gateway = new FakeGateway(_source);
            _gateway.SetTags("Assets/Art/Hero.png", "blue");
            _gateway.SetTags("Assets/Art/UI/Icon.png", "blue");
            _tagDefinitions = new TagDefinitionStore(
                new List<TagDefinitionRecord>
                {
                    new TagDefinitionRecord
                    {
                        Id = "blue",
                        Name = "Blue",
                        Color = ZepTagColor.Blue
                    },
                    new TagDefinitionRecord
                    {
                        Id = "red",
                        Name = "Red",
                        Color = ZepTagColor.Red
                    }
                },
                () => { });
            // The gateway is the tag-definition boundary in production too; it
            // reads and writes the same store the controller projects from.
            _gateway.TagDefinitions = _tagDefinitions;

            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _searchService = new AssetSearchService(_index);
            _scheduler = new QueuedScheduler();
            _synchronizer = new FakeSynchronizer(_source, _index);
            _commandHistory = new ProjectCommandHistory();
            _fileCommands = new ProjectFileCommandService(_gateway, _commandHistory);
            _controller = new ZepController(
                _state,
                _gateway,
                _fileCommands,
                _source,
                _index,
                _searchService,
                _tagDefinitions,
                _synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            _controller.Refresh();
        }

        [Test]
        public void FinderFilters_CanBrowseByTypeAlone_AndOverrideWithoutMutatingThePane()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetSelectedTypeFilter("Texture2D");
            var originalAssets = pane.ProjectedAssets;
            var results = _controller.GetSearchResults(pane, "t:Texture", ZepSearchScope.All, string.Empty, string.Empty);
            Assert.That(results.Select(a => a.Name), Is.EquivalentTo(new[] { "Hero", "Icon", "Leak" }));
            Assert.That(_controller.GetSearchResults(pane, "t:Audio", ZepSearchScope.All, string.Empty, string.Empty)
                .Select(a => a.Name), Is.EqualTo(new[] { "Theme" }));
            Assert.That(_controller.GetSearchResults(pane, string.Empty, ZepSearchScope.All, "Texture2D", "blue")
                .Select(a => a.Name), Is.EquivalentTo(new[] { "Hero", "Icon" }));
            Assert.That(pane.SelectedTypeFilter, Is.EqualTo("Texture2D"));
            Assert.That(pane.ProjectedAssets, Is.SameAs(originalAssets));
        }

        // Sidebar multi-selection end to end: ⌘-clicking a second folder lists
        // both folders' contents in the primary pane, and a selected folder
        // that is deleted simply leaves the selection.
        [Test]
        public void MultiFolderSelection_ListsEverySelectedFoldersContents_AndDropsADeletedFolder()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            Assert.That(
                pane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[]
                {
                    "Assets/Art/UI",
                    "Assets/Art/Hero.png",
                    "Assets/Art/ProjectConfiguration.asset",
                }),
                "fixture guard: one folder lists only its own contents");

            pane.TryToggleFolderInSelection("Assets/Audio");
            pane.TryToggleFolderInSelection("Assets/Art2");

            Assert.That(
                pane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[]
                {
                    "Assets/Art/UI",
                    "Assets/Art/Hero.png",
                    "Assets/Art2/Leak.png",
                    "Assets/Art/ProjectConfiguration.asset",
                    "Assets/Audio/Theme.wav",
                }));
            Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/Art"));

            _source.RemovePath("Assets/Audio");
            _controller.Refresh();

            Assert.That(
                pane.AdditionalFolderPaths,
                Is.EqualTo(new[] { AssetPath.From("Assets/Art2") }));
            Assert.That(
                pane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[]
                {
                    "Assets/Art/UI",
                    "Assets/Art/Hero.png",
                    "Assets/Art2/Leak.png",
                    "Assets/Art/ProjectConfiguration.asset",
                }));

            pane.TrySetFolderSelection("Assets/Art2");

            Assert.That(
                pane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Art2/Leak.png" }),
                "a plain selection goes back to one folder");
        }

        [Test]
        public void FinderBroadQuery_KeepsAllTwentyThousandMatchesWithoutEagerEntries()
        {
            for (var i = 0; i < 20000; i++)
            {
                var name = "Hero" + i.ToString("D5");
                _index.Upsert(new AssetRecord("finder-scale-" + i, "Assets/Generated/" + name + ".prefab", name, "GameObject"));
            }
            _controller.GetSearchResults(_state.PrimaryPane, "Hero", ZepSearchScope.All);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 3; i++)
            {
                var results = (LazyAssetEntryList)_controller.GetSearchResults(_state.PrimaryPane, "Hero", ZepSearchScope.All);
                Assert.That(results.Count, Is.EqualTo(20001));
                Assert.That(results.PathAt(0), Is.EqualTo("Assets/Art/Hero.png"));
                Assert.That(results.PathAt(results.Count - 1), Is.EqualTo("Assets/Generated/Hero19999.prefab"));
                Assert.That(results.MaterializedCount, Is.Zero);
            }
            timer.Stop();
            TestContext.WriteLine($"Quick Finder: 20,001 matches, three warm queries in {timer.Elapsed.TotalMilliseconds:F1} ms; zero eager entries.");
        }

        [Test]
        public void FinderRelevance_SortsNamesAheadOfMetadataMatches_WithoutMaterializingTheWholeList()
        {
            _index.Upsert(new AssetRecord("exact", "Assets/Art.prefab", "Art", "GameObject"));
            _index.Upsert(new AssetRecord("prefix", "Assets/ArtWork.prefab", "ArtWork", "GameObject"));
            _index.Upsert(new AssetRecord("substring", "Assets/Cart.prefab", "Cart", "GameObject"));
            _index.Upsert(new AssetRecord("metadata", "Assets/Other.asset", "Other", "ArtConfiguration"));
            var results = (LazyAssetEntryList)_controller.GetSearchResults(_state.PrimaryPane, "Art", ZepSearchScope.All);
            Assert.That(results.MaterializedCount, Is.Zero);
            Assert.That(results.PathAt(0), Is.EqualTo("Assets/Art"), "equal exact names have a stable path tie-break");
            Assert.That(results.PathAt(1), Is.EqualTo("Assets/Art.prefab"));
            Assert.That(results.IndexOfPath("Assets/Other.asset"), Is.GreaterThanOrEqualTo(0));
            Assert.That(results.IndexOfPath("Assets/Cart.prefab"), Is.LessThan(results.IndexOfPath("Assets/Other.asset")));
            Assert.That(results.MaterializedCount, Is.Zero, "ranking and restoring selection must not load all entries");
            results.RankByName(SearchQuery.Parse("art"));
            Assert.That(results.PathAt(results.IndexOfPath("Assets/Art.prefab")), Is.EqualTo("Assets/Art.prefab"));
        }

        // The UI's per-item "is this a folder / what is its guid and type"
        // questions for paths that came out of the panel's own entries go to
        // the index first (IndexedPathQueries) instead of AssetDatabase. These
        // pin what the controller answers, and that a path the catalog does not
        // hold is reported as unknown rather than guessed.
        [Test]
        public void IndexedPathLookups_AnswerFromTheCatalogAndReportUnknownPaths()
        {
            Assert.That(_index.TryGetFolder("Assets/Art", out _), Is.True, "fixture guard: Assets/Art is a catalog folder");
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var record), Is.True, "fixture guard");

            Assert.That(_controller.TryGetIndexedPathKind("Assets/Art", out var isFolder), Is.True);
            Assert.That(isFolder, Is.True);
            Assert.That(_controller.TryGetIndexedPathKind("Assets/Art/Hero.png", out isFolder), Is.True);
            Assert.That(isFolder, Is.False);
            Assert.That(
                _controller.TryGetIndexedPathKind("Assets/Art/Missing.png", out _),
                Is.False,
                "an unindexed path is unknown, so the caller may still ask Unity");

            Assert.That(_controller.TryGetIndexedAsset("Assets/Art/Hero.png", out var guid, out var typeName), Is.True);
            Assert.That(guid, Is.EqualTo(record.Guid));
            Assert.That(typeName, Is.EqualTo(record.TypeName));
            Assert.That(
                _controller.TryGetIndexedAsset("Assets/Art", out _, out _),
                Is.False,
                "a folder has no asset record to answer with");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                _controller?.Dispose();
            }
            finally
            {
                _gateway?.DeleteCreatedPayloadStorage();
                if (_compressionWorkspace != null && Directory.Exists(_compressionWorkspace))
                {
                    Directory.Delete(_compressionWorkspace, true);
                }
            }
        }

        [Test]
        public void ScanFailure_StartupRemainsUnreadyAndRetries()
        {
            _controller.Dispose();
            _controller = CreateScanFailureController(
                _source,
                new OutcomeSynchronizer(AssetIndexSynchronizationResult.Failure("scan unavailable")));

            _controller.InitializeIndex();
            _scheduler.RunUpTo(1);

            Assert.That(
                _controller.StatusMessage,
                Is.EqualTo("Indexing failed: scan unavailable Retry with Refresh."));
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void ScanFailure_RefreshPreservesPublishedMetadata()
        {
            var publishedPaths = _state.PrimaryPane.ProjectedAssets
                .Select(asset => asset.Path)
                .ToArray();
            _controller.Dispose();
            _controller = CreateScanFailureController(
                _source,
                new OutcomeSynchronizer(AssetIndexSynchronizationResult.Failure("scan unavailable")));

            _controller.Refresh();

            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(publishedPaths));
            Assert.That(
                _state.StatusMessage,
                Is.EqualTo("Indexing failed: scan unavailable Retry with Refresh."));
        }

        [Test]
        public void ScanFailure_OutcomeAndLegacyCompatibility()
        {
            _controller.Dispose();
            _controller = CreateScanFailureController(
                _source,
                new OutcomeSynchronizer(AssetIndexSynchronizationResult.Success));

            Assert.DoesNotThrow(() => _controller.Refresh());
            Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));
        }

        [Test]
        public void ScanFailure_CoalescingStatusAndDisposal()
        {
            _controller.Dispose();
            _controller = CreateScanFailureController(
                _source,
                new OutcomeSynchronizer(AssetIndexSynchronizationResult.Failure("scan unavailable")));

            _controller.Refresh();
            _controller.Refresh();
            _controller.Dispose();

            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void Dispose_DisposesIndexSynchronizer()
        {
            _controller.Dispose();

            Assert.That(_synchronizer.IsDisposed, Is.True);
        }

        [Test]
        public void PaneLocalSearchAndFilter_ReconcileOnlyOriginatingPane()
        {
            var artPane = OpenArtPane();
            _state.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Audio/Theme.wav"));
            artPane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));

            var changes = new List<ZepPaneChange>();
            _state.PaneChanged += changes.Add;
            artPane.SetSearchText("hero");
            artPane.SetSelectedSearchScope(ZepSearchScope.Assets);
            artPane.SetSelectedTypeFilter("Texture2D");

            Assert.That(
                changes.Where(change =>
                    change.Kind == ZepPaneChangeKind.Updated
                    && ReferenceEquals(change.NewPane, _state.PrimaryPane)),
                Is.Empty);
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
            Assert.That(artPane.SearchText, Is.EqualTo("hero"));
            Assert.That(artPane.SelectedTypeFilter, Is.EqualTo("Texture2D"));
            Assert.That(
                artPane.SearchScopeOptions.Select(option => option.Scope),
                Is.EqualTo(new[] { ZepSearchScope.Assets }));
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));
        }

        [Test]
        public void DomainReloadDuringDeferredHydration_DoesNotPoisonPersistentCache()
        {
            var index = new InMemoryAssetIndex();
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = new ZepController(
                new ZepState(),
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                new AssetSearchService(index),
                _tagDefinitions,
                synchronizer,
                new QueuedScheduler(),
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            try
            {
                controller.BeginPanelUse();
                controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { "Assets/ReloadedScript.cs" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    didDomainReload: true));
                controller.HandleProjectChanged();

                Assert.That(synchronizer.PersistentDirtyCount, Is.Zero,
                    "domain-reload callbacks must preserve the snapshot that deferred hydration is about to restore");
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void ConcreteAssetChangeDuringDeferredHydration_PoisonsPersistentCache()
        {
            var index = new InMemoryAssetIndex();
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = new ZepController(
                new ZepState(),
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                new AssetSearchService(index),
                _tagDefinitions,
                synchronizer,
                new QueuedScheduler(),
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            try
            {
                controller.BeginPanelUse();
                controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { "Assets/Imported.asset" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));

                Assert.That(synchronizer.PersistentDirtyCount, Is.EqualTo(1),
                    "a concrete non-reload change racing hydration must still invalidate the saved snapshot");
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void RootedPaneVisibleProjection_ClampsSearchResultsToItsRoot()
        {
            var artPane = OpenArtPane();
            artPane.SetSearchText("hero");
            artPane.SetSelectedSearchScope(ZepSearchScope.Assets);
            var view = new ZepPaneView(
                _state,
                artPane,
                new UnityZepIconRepository(),
                _controller);

            try
            {
                view.RefreshFromPane();

                Assert.That(
                    view.ActiveAssets.Select(asset => asset.Path),
                    Is.EqualTo(new[] { "Assets/Art/Hero.png" }),
                    "The rooted pane must still surface matches inside its root.");

                artPane.SetSearchText("theme");
                view.RefreshFromPane();

                Assert.That(
                    view.ActiveAssets.Select(asset => asset.Path),
                    Is.Empty,
                    "Assets/Audio/Theme.wav must not escape an Assets/Art-rooted pane.");

                artPane.SetSearchText("leak");
                view.RefreshFromPane();

                Assert.That(
                    view.ActiveAssets.Select(asset => asset.Path),
                    Is.Empty,
                    "Assets/Art2 must not pass the segment-aware Assets/Art clamp.");
            }
            finally
            {
                view.Teardown();
            }
        }

        [Test]
        public void SearchProjection_ControllerPublishesTheExactListConsumedByThePaneView()
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            ZepPaneView view = null;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                var pane = _state.PrimaryPane;
                pane.SetSearchText("hero");
                var published = pane.ProjectedAssets;
                view = new ZepPaneView(
                    _state,
                    pane,
                    new UnityZepIconRepository(),
                    _controller);
                view.RefreshFromPane();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(view.ActiveAssets, Is.SameAs(published));
                Assert.That(published.Select(asset => asset.Path),
                    Is.EqualTo(new[] { "Assets/Art/Hero.png" }));
                Assert.That(snapshot.GetCount(
                    ZepPerfCounter.SearchProjectionRequests),
                    Is.EqualTo(1));
                Assert.That(snapshot.GetCount(
                    ZepPerfCounter.SearchProjectionBuilds),
                    Is.EqualTo(1));
                Assert.That(snapshot.GetCount(
                    ZepPerfCounter.SearchQueries),
                    Is.EqualTo(1));
            }
            finally
            {
                view?.Teardown();
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void FullIndexRefresh_ReconcilesEachPaneSelectionAgainstItsOwnResults()
        {
            var artPane = OpenArtPane();
            _state.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Audio/Theme.wav"));
            artPane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));
            var selectionChanges = new Dictionary<ZepPaneState, int>();
            _state.PaneChanged += change =>
            {
                if (change.Kind != ZepPaneChangeKind.Updated
                    || (change.Update & ZepPaneUpdate.Selection)
                        == ZepPaneUpdate.None)
                {
                    return;
                }

                selectionChanges.TryGetValue(change.NewPane, out var count);
                selectionChanges[change.NewPane] = count + 1;
            };

            _source.RemovePath("Assets/Art/Hero.png");
            _source.RemovePath("Assets/Audio/Theme.wav");
            _controller.Refresh();

            Assert.That(_state.PrimaryPane.SelectedAssetPaths, Is.Empty);
            Assert.That(artPane.SelectedAssetPaths, Is.Empty);
            Assert.That(selectionChanges[_state.PrimaryPane], Is.EqualTo(1));
            Assert.That(selectionChanges[artPane], Is.EqualTo(1));
        }

        [Test]
        public void FullIndexRefresh_WhenSynchronizationIsInFlight_CompletesProjectionFromCallback()
        {
            _source.RemovePath("Assets/Audio/Theme.wav");
            _synchronizer.DeferSynchronizeCompletion = true;

            _controller.Refresh();

            Assert.That(
                _index.TryGetByPath("Assets/Audio/Theme.wav", out _),
                Is.True,
                "the controller must leave the visible index intact while synchronization is pending");
            Assert.That(_state.StatusMessage, Is.EqualTo("Refreshing..."));

            _synchronizer.CompleteDeferredSynchronization();

            Assert.That(
                _index.TryGetByPath("Assets/Audio/Theme.wav", out _),
                Is.False);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
        }

        [TestCase(1)]
        [TestCase(3)]
        public void StartupSynchronization_WithRefreshClicks_PublishesProjectionOnce(
            int refreshClicks)
        {
            var state = new ZepState();
            var index = new InMemoryAssetIndex();
            var scheduler = new QueuedScheduler();
            var synchronizer = new FakeSynchronizer(_source, index)
            {
                DeferSynchronizeCompletion = true
            };
            var controller = new ZepController(
                state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                new AssetSearchService(index),
                _tagDefinitions,
                synchronizer,
                scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                controller.BeginPanelUse();
                scheduler.RunAll();

                for (var i = 0; i < refreshClicks; i++)
                {
                    controller.Refresh();
                }

                Assert.That(state.StatusMessage, Is.EqualTo("Refreshing..."));
                Assert.That(synchronizer.EnumerationCount, Is.Zero);

                synchronizer.CompleteDeferredSynchronization();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(synchronizer.EnumerationCount, Is.EqualTo(1));
                Assert.That(
                    snapshot.GetCount(
                        ZepPerfCounter.RebuildProjectionCacheCalls),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.GetCount(
                        ZepPerfCounter.ProjectionPublications),
                    Is.EqualTo(1));
                Assert.That(
                    synchronizer.GetFolderRecordsForProjectionCount,
                    Is.EqualTo(1),
                    "the synchronized folder snapshot must be consumed once");
                Assert.That(state.IsIndexScanInProgress, Is.False);
                Assert.That(state.StatusMessage, Is.EqualTo("Ready"));
            }
            finally
            {
                controller.Dispose();
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void Refresh_ProjectionFaultExitsRefreshingAndLaterRefreshRecovers()
        {
            var folderProjectionCountAtStart =
                _synchronizer.GetFolderRecordsForProjectionCount;
            _synchronizer.DeferSynchronizeCompletion = true;
            _synchronizer.ThrowProjectionFolderRecordsOnce = true;

            _controller.Refresh();

            Assert.That(_state.StatusMessage, Is.EqualTo("Refreshing..."));
            Assert.Throws<InvalidOperationException>(
                _synchronizer.CompleteDeferredSynchronization);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));

            Assert.DoesNotThrow(_controller.Refresh);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(
                _synchronizer.GetFolderRecordsForProjectionCount,
                Is.EqualTo(folderProjectionCountAtStart + 2));
        }

        [Test]
        public void RecoveryObservation_StartupRefusalStillSchedulesHydration()
        {
            const string packageEntryPath = "Packages/com.example";
            _controller.Dispose();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete(
                "Packages",
                new DirectFolderEntry(
                    AssetPath.From(packageEntryPath),
                    isFolder: true,
                    "com.example",
                    "Folder"));
            directSource.SetComplete("Assets");
            _controller = CreateControllerWithDirectSource(directSource);
            var readPaths = new List<string>();
            _gateway.TagAssignmentReadCallback = (path, _) =>
            {
                readPaths.Add(path.Value);
                if (path.Value == packageEntryPath)
                {
                    throw new ProjectTagStateUnavailableException("Tag state unavailable");
                }
            };

            Assert.DoesNotThrow(_controller.BeginPanelUse);
            Assert.That(readPaths, Is.EqualTo(new[] { packageEntryPath }));
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
            Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
            Assert.That(_scheduler.PendingCount, Is.EqualTo(1));
            Assert.That(directSource.ObservedPaths, Is.EqualTo(new[] { "Packages" }));

            _controller.BeginPanelUse();

            Assert.That(_scheduler.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void RecoveryObservation_NavigationRefusalPreservesPublication()
        {
            const string firstPendingPath = "Assets/Audio/First.wav";
            const string refusedPendingPath = "Assets/Audio/Second.wav";
            _controller.Dispose();
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art"));
            _state.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art"));
            _source.Folders.Add(AssetPath.From("Assets/Art/PendingFolder"));
            _gateway.SetTags("Assets/Art/Hero.png", "blue");
            _gateway.SetTags("Assets/Art/PendingFolder", "red", "blue");
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Art",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art/Hero.png"),
                    isFolder: false,
                    "Hero",
                    "Texture2D"),
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art/PendingFolder"),
                    isFolder: true,
                    "PendingFolder",
                    "Folder"));
            directSource.SetComplete(
                "Assets/Audio",
                new DirectFolderEntry(
                    AssetPath.From(firstPendingPath),
                    isFolder: false,
                    "First",
                    "AudioClip"),
                new DirectFolderEntry(
                    AssetPath.From(refusedPendingPath),
                    isFolder: false,
                    "Second",
                    "AudioClip"));
            _controller = CreateControllerWithDirectSource(directSource);
            _controller.BeginPanelUse();
            var publishedPaths = _state.PrimaryPane.ProjectedAssets
                .Select(asset => asset.Path)
                .ToArray();
            Assert.That(publishedPaths, Is.Not.Empty);
            directSource.ObservedPaths.Clear();
            var readPaths = new List<string>();
            _gateway.TagAssignmentReadCallback = (path, _) =>
            {
                if (path.Value != firstPendingPath && path.Value != refusedPendingPath)
                {
                    return;
                }

                readPaths.Add(path.Value);
                if (path.Value == refusedPendingPath)
                {
                    throw new ProjectTagStateUnavailableException("Tag state unavailable");
                }
            };

            Assert.DoesNotThrow(() =>
                _state.PrimaryPane.TrySetFolderSelection("Assets/Audio"));

            Assert.That(readPaths, Is.EqualTo(new[] { firstPendingPath, refusedPendingPath }));
            Assert.That(_state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(publishedPaths));
            Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
            Assert.That(directSource.ObservedPaths, Is.EqualTo(new[] { "Assets/Audio" }));
            var observationCount = directSource.ObservedPaths.Count;

            Assert.DoesNotThrow(() =>
                _state.PrimaryPane.TrySetFolderSelection("Assets/Art"));
            Assert.That(directSource.ObservedPaths.Count, Is.EqualTo(observationCount));
            Assert.That(readPaths, Is.EqualTo(new[] { firstPendingPath, refusedPendingPath }));
        }

        [Test]
        public void RecoveryObservation_AuthoritativeRefreshRecovers()
        {
            _controller.Dispose();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete("Assets");
            _controller = CreateControllerWithDirectSource(directSource);
            _gateway.TagAssignmentReadCallback = (_, __) =>
                throw new ProjectTagStateUnavailableException("Tag state unavailable");
            _controller.BeginPanelUse();

            _gateway.TagAssignmentReadCallback = null;
            _scheduler.RunAll();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
        }

        [Test]
        public void RecoveryObservation_FailureContractsRemainNarrow()
        {
            _controller.Dispose();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetIncomplete("Packages");
            directSource.SetComplete("Assets");
            directSource.SetComplete("Assets/Audio");
            _controller = CreateControllerWithDirectSource(directSource);

            Assert.DoesNotThrow(_controller.BeginPanelUse);
            Assert.That(directSource.ObservedPaths, Has.Member("Assets"));

            _gateway.TagVersionException = new InvalidOperationException("unexpected tag failure");
            Assert.Throws<InvalidOperationException>(() =>
                _state.PrimaryPane.TrySetFolderSelection("Assets/Audio"));
        }

        [Test]
        public void RecoveryCompletion_InitialRefusalDoesNotDeclareReady()
        {
            var state = new ZepState();
            var index = new InMemoryAssetIndex();
            var scheduler = new QueuedScheduler();
            var synchronizer = new FakeSynchronizer(_source, index)
            {
                DeferSynchronizeCompletion = true,
                PublishesViaReplace = true
            };
            var controller = new ZepController(
                state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                new AssetSearchService(index),
                _tagDefinitions,
                synchronizer,
                scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            try
            {
                _gateway.ThrowTagVersionUnavailable = true;
                controller.Refresh();

                Assert.DoesNotThrow(synchronizer.CompleteDeferredSynchronization);
                Assert.That(state.IsIndexScanInProgress, Is.False);
                Assert.That(controller.IsPaneSourceReconciliationReady, Is.False);
                Assert.That(state.StatusMessage, Is.EqualTo("Tag state unavailable"));
            }
            finally
            {
                _gateway.ThrowTagVersionUnavailable = false;
                controller.Dispose();
            }
        }

        [Test]
        public void RecoveryCompletion_RefreshReadFailurePreservesPublication()
        {
            var publishedPaths = _state.PrimaryPane.ProjectedAssets
                .Select(asset => asset.Path)
                .ToArray();
            _synchronizer.DeferSynchronizeCompletion = true;
            _gateway.ThrowTagVersionUnavailable = true;

            _controller.Refresh();

            Assert.DoesNotThrow(_synchronizer.CompleteDeferredSynchronization);
            Assert.That(_state.IsIndexScanInProgress, Is.False);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(publishedPaths));
            Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
        }

        [Test]
        public void RecoveryCompletion_ExplicitRefreshRecovers()
        {
            _synchronizer.DeferSynchronizeCompletion = true;
            _gateway.ThrowTagVersionUnavailable = true;
            _controller.Refresh();
            _synchronizer.CompleteDeferredSynchronization();

            var folderReads = _synchronizer.GetFolderRecordsForProjectionCount;
            _gateway.ThrowTagVersionUnavailable = false;

            Assert.DoesNotThrow(_controller.Refresh);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_synchronizer.GetFolderRecordsForProjectionCount,
                Is.EqualTo(folderReads + 1));
        }

[Test]
        public void DeferredRefresh_UnchangedCatalogRecoversWorker()
        {
            var addressables = new FaultingAddressablesQuery(int.MaxValue);
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = CreateDeferredWorkerController(addressables, scheduler, index, synchronizer);
            try
            {
                controller.Refresh();
                scheduler.RunAll();
                Assert.That(addressables.ExpandAttempts, Is.EqualTo(2));

                synchronizer.LeavesCatalogUnchanged = true;
                addressables.FaultCount = 0;
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(addressables.ExpandAttempts, Is.EqualTo(3));
                Assert.That(controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable, Is.True);
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredRefresh_RetryBudgetIsBounded()
        {
            var addressables = new FaultingAddressablesQuery(int.MaxValue);
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = CreateDeferredWorkerController(addressables, scheduler, index, synchronizer);
            try
            {
                controller.Refresh();
                scheduler.RunAll();
                synchronizer.LeavesCatalogUnchanged = true;
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(addressables.ExpandAttempts, Is.EqualTo(4));
                controller.GetPresentation("Assets/Art/Hero.png", "Texture2D");
                Assert.That(scheduler.PendingCount, Is.Zero);
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredRefresh_CaptureAndMismatchRemainRecoverable()
        {
            var addressables = new FaultingAddressablesQuery(0) { ThrowOnCapture = true };
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = CreateDeferredWorkerController(addressables, scheduler, index, synchronizer);
            try
            {
                controller.Refresh();
                scheduler.RunAll();
                Assert.That(scheduler.Faults, Has.Count.EqualTo(1));

                synchronizer.LeavesCatalogUnchanged = true;
                addressables.ThrowOnCapture = false;
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(addressables.ExpandAttempts, Is.EqualTo(1));
                Assert.That(scheduler.Faults, Has.Count.EqualTo(1));
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredRefresh_CoalescesAndRespectsCompletion()
        {
            var addressables = new FaultingAddressablesQuery(int.MaxValue);
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = CreateDeferredWorkerController(addressables, scheduler, index, synchronizer);
            try
            {
                controller.Refresh();
                synchronizer.LeavesCatalogUnchanged = true;
                controller.Refresh();
                controller.Refresh();
                Assert.That(scheduler.PendingCount, Is.EqualTo(1));

                scheduler.RunAll();
                addressables.FaultCount = 0;
                controller.Refresh();
                scheduler.RunAll();
                var completedAttempts = addressables.ExpandAttempts;
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(addressables.ExpandAttempts, Is.EqualTo(completedAttempts));
                controller.Dispose();
                var exception = Assert.Throws<ObjectDisposedException>(() => controller.Refresh());
                Assert.That(exception.ObjectName, Is.EqualTo(nameof(ZepController)));
                Assert.That(scheduler.PendingCount, Is.Zero);
            }
            finally
            {
                controller.Dispose();
            }
        }

                [Test]
        public void RecoveryCompletion_FastPathAndFailureContracts()
        {
            var folderReads = _synchronizer.GetFolderRecordsForProjectionCount;
            _synchronizer.LeavesCatalogUnchanged = true;

            _controller.Refresh();

            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_synchronizer.GetFolderRecordsForProjectionCount,
                Is.EqualTo(folderReads));

            _gateway.TagVersionException = new InvalidOperationException("metadata failure");
            Assert.Throws<InvalidOperationException>(_controller.Refresh);
            Assert.That(_state.IsIndexScanInProgress, Is.False);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));

            _gateway.TagVersionException = null;
            Assert.DoesNotThrow(_controller.Refresh);
            Assert.That(_synchronizer.GetFolderRecordsForProjectionCount,
                Is.EqualTo(folderReads + 1));
        }
        // m-12: a faulted deferred worker used to be swallowed whole — no report,
        // no retry, search postings and the addressables badge stale all session.
        [Test]
        public void DeferredWorkerFault_IsReportedAndRetriedOnce()
        {
            var addressables = new FaultingAddressablesQuery(faultCount: 1);
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler);
            try
            {
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(scheduler.Faults.Count, Is.EqualTo(1),
                    "the fault must reach the scheduler boundary, which reports it");
                Assert.That(scheduler.Faults[0].Message,
                    Does.Contain("deferred index worker failed")
                        .And.Contain("one retry was scheduled"));
                Assert.That(scheduler.Faults[0].InnerException, Is.Not.Null,
                    "the worker's own exception must be observed and carried out");
                Assert.That(addressables.ExpandAttempts, Is.EqualTo(2),
                    "the faulted attempt must be retried exactly once");
                Assert.That(
                    controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable,
                    Is.True,
                    "the retry's result must be adopted — postings first, then the badge");
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredWorkerFault_ThatRepeats_StopsInsteadOfRetryingForever()
        {
            var addressables = new FaultingAddressablesQuery(faultCount: int.MaxValue);
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler);
            try
            {
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(addressables.ExpandAttempts, Is.EqualTo(2),
                    "one retry, then the latch stops it — a deterministic fault must "
                    + "not re-arm itself on every tick");
                Assert.That(scheduler.Faults.Count, Is.EqualTo(2));
                Assert.That(scheduler.Faults[1].Message, Does.Contain("stay stale"));
            }
            finally
            {
                controller.Dispose();
            }
        }

        // 4.32(a): the worker's build is discarded when the catalog moved on
        // while it ran, and the controller used to reschedule a fresh
        // project-wide build every time. A project that keeps mutating through
        // startup loses that race on every poll, so the rebuild never stopped.
        [Test]
        public void DeferredWorkerVersionMismatch_StopsAfterTwoRetries()
        {
            var addressables = new FaultingAddressablesQuery(faultCount: 0);
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var controller = CreateDeferredWorkerController(addressables, scheduler, index);
            try
            {
                controller.Refresh();

                // Bump the catalog's record version before every scheduled
                // callback, so no worker build can ever be adopted.
                for (var i = 0; i < 200 && scheduler.PendingCount > 0; i++)
                {
                    index.ApplyFileChanges(
                        new[]
                        {
                            new AssetRecord(
                                "race-guid-" + i,
                                "Assets/Race" + i + ".png",
                                "Race" + i,
                                "Texture2D"),
                        },
                        Array.Empty<string>());
                    scheduler.RunUpTo(1);
                    if (scheduler.PendingCount > 0)
                    {
                        System.Threading.Thread.Sleep(1);
                    }
                }

                Assert.That(
                    addressables.ExpandAttempts,
                    Is.EqualTo(3),
                    "the first build plus at most two retries — then the controller "
                    + "must stop and let the index build lazily on first search");
                Assert.That(
                    scheduler.PendingCount,
                    Is.Zero,
                    "nothing may still be queued once the retry budget is spent");
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredWorker_OnAStableCatalog_AdoptsOnTheFirstPoll()
        {
            var addressables = new FaultingAddressablesQuery(faultCount: 0);
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler);
            try
            {
                controller.Refresh();
                scheduler.RunAll();

                Assert.That(
                    addressables.ExpandAttempts,
                    Is.EqualTo(1),
                    "the retry budget must not cost an ordinary startup a second build");
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredStartup_CatalogMutationBeforeFirstCallbackUsesCurrentSnapshot()
        {
            const string existingPath = "Assets/ExistingMetadata.asset";
            const string oldToken = "quartzoldtoken";
            const string newToken = "saffronnewtoken";
            _source.Records.Add(new AssetRecord(
                "metadata-guid", existingPath, "ExistingMetadata", "ScriptableObject", oldToken));
            Assert.That(_tagDefinitions.TryCreateTag(oldToken, ZepTagColor.Blue,
                out var oldTag, out _), Is.True);
            Assert.That(_tagDefinitions.TryCreateTag(newToken, ZepTagColor.Red,
                out var newTag, out _), Is.True);
            _gateway.SetTags(existingPath, oldTag.Id);
            var addressables = new RecordingAddressablesQuery();
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            var controller = new ZepController(
                new ZepState(),
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                search,
                _tagDefinitions,
                new FakeSynchronizer(_source, index),
                scheduler,
                new NoOpShellReveal(),
                addressables,
                null,
                searchWasUsedInThisProject: true);
            try
            {
                controller.Refresh();
                Assert.That(index.TryGetByPath(existingPath, out var before), Is.True);
                Assert.That(before.TagSearchText, Does.Contain(oldToken));
                _gateway.SetTags(existingPath, newTag.Id);
                var metadataIndex = _source.Records.FindIndex(record => record.Guid == "metadata-guid");
                _source.Records[metadataIndex] = new AssetRecord(
                    "metadata-guid", existingPath, "ExistingMetadata", "ScriptableObject", newToken);

                _source.Records.Add(new AssetRecord(
                    "new-tag-guid",
                    "Assets/NewTag.asset",
                    "NewTag",
                    "ScriptableObject",
                    "newtag"));
                _gateway.SetTags("Assets/NewTag.asset", "red");
                _gateway.RemovePath("Assets/Audio/Theme.wav");
                _gateway.MovePath(
                    "Assets/Art/UI/Icon.png",
                    "Assets/Art/MovedIcon.png");
                controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { "Assets/NewTag.asset", existingPath },
                    new[] { "Assets/Audio/Theme.wav" },
                    new[] { "Assets/Art/MovedIcon.png" },
                    new[] { "Assets/Art/UI/Icon.png" }));

                Assert.That(index.TryGetByPath(existingPath, out var current), Is.True);
                Assert.That(current.TagSearchText, Does.Contain(newToken));
                Assert.That(index.TryGetByPath("Assets/NewTag.asset", out _), Is.True);
                Assert.That(index.TryGetByPath("Assets/Art/MovedIcon.png", out _), Is.True);
                Assert.That(index.TryGetByPath("Assets/Audio/Theme.wav", out _), Is.False);
                Assert.That(index.TryGetByPath("Assets/Art/UI/Icon.png", out _), Is.False);

                scheduler.RunAll();

                Assert.That(addressables.FirstExpandedRecords.Single(record =>
                    record.AssetPath == existingPath).TagSearchText, Does.Contain(newToken),
                    "the first worker input must include the changed existing tag text");
                Assert.That(search.Search(newToken).Select(record => record.AssetPath),
                    Is.EqualTo(new[] { existingPath }));
                Assert.That(search.Search(oldToken), Is.Empty);
                Assert.That(search.Search("MovedIcon").Select(record => record.AssetPath),
                    Is.EqualTo(new[] { "Assets/Art/MovedIcon.png" }));
                Assert.That(search.Search("Icon").Select(record => record.AssetPath),
                    Does.Not.Contain("Assets/Art/UI/Icon.png"));

                Assert.That(addressables.FirstExpandedPaths,
                    Does.Contain("Assets/NewTag.asset"));
                Assert.That(addressables.FirstExpandedPaths,
                    Does.Contain("Assets/Art/MovedIcon.png"));
                Assert.That(addressables.FirstExpandedPaths,
                    Does.Not.Contain("Assets/Audio/Theme.wav"));
                Assert.That(addressables.FirstExpandedPaths,
                    Does.Not.Contain("Assets/Art/UI/Icon.png"));
                Assert.That(
                    search.Search("newtag").Select(record => record.AssetPath),
                    Is.EqualTo(new[] { "Assets/NewTag.asset" }),
                    "the adopted worker search build must use the callback-time tag text");
                Assert.That(
                    search.Search("theme"),
                    Is.Empty,
                    "the deleted record must not survive in the adopted search build");
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void DeferredStartup_QueuedAndActiveWorkSuppressCompactionUntilCompletion()
        {
            using var addressables = new GatedStartupAddressablesQuery();
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            var synchronizer = new FakeSynchronizer(_source, index);
            var controller = new ZepController(
                new ZepState(), _gateway, new ProjectFileCommandService(_gateway),
                _source, index, search, _tagDefinitions, synchronizer, scheduler,
                new NoOpShellReveal(), addressables, null, searchWasUsedInThisProject: true);
            try
            {
                controller.Refresh();
                synchronizer.LeavesCatalogUnchanged = true;
                controller.Refresh();
                controller.Refresh();
                Assert.That(scheduler.PendingCount, Is.EqualTo(1));
                search.Search("hero");
                Assert.That(search.HasBuiltIndex, Is.True);
                RequestStartupTestCompaction(index, search, "_queued");
                controller.ShowStatusMessage("queued compaction request");
                Assert.That(search.IsCompactionRequested, Is.True,
                    "a queued startup already owns the next worker");
                Assert.That(scheduler.PendingCount, Is.EqualTo(1));
                Assert.That(addressables.CaptureCount, Is.Zero);

                scheduler.RunUpTo(1);
                Assert.That(addressables.Started.Wait(TimeSpan.FromSeconds(30)), Is.True);
                controller.ShowStatusMessage("active compaction request");
                Assert.That(search.IsCompactionRequested, Is.True,
                    "an active worker must suppress overlap after the queued flag clears");
                Assert.That(scheduler.PendingCount, Is.EqualTo(1),
                    "only the active worker poll may remain queued");
                Assert.That(addressables.CaptureCount, Is.EqualTo(1));
                Assert.That(addressables.ExpandCount, Is.EqualTo(1));

                addressables.Release.Set();
                DrainStartupTestScheduler(scheduler);
                Assert.That(search.TombstoneCount, Is.Zero);
                Assert.That(search.IsCompactionRequested, Is.False);
                Assert.That(scheduler.PendingCount, Is.Zero);
                Assert.That(scheduler.Faults, Is.Empty);

                RequestStartupTestCompaction(index, search, "_after");
                controller.ShowStatusMessage("new compaction after completed startup");
                Assert.That(scheduler.PendingCount, Is.EqualTo(1));
                DrainStartupTestScheduler(scheduler);
                Assert.That(addressables.CaptureCount, Is.EqualTo(2));
                Assert.That(addressables.ExpandCount, Is.EqualTo(2));
                Assert.That(search.TombstoneCount, Is.Zero);
                Assert.That(search.IsCompactionRequested, Is.False);
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally
            {
                try
                {
                    addressables.Release.Set();
                    DrainStartupTestScheduler(scheduler);
                }
                finally
                {
                    controller.Dispose();
                }
            }
        }

        private static void DrainStartupTestScheduler(QueuedScheduler scheduler)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (scheduler.PendingCount > 0 && DateTime.UtcNow < deadline)
            {
                scheduler.RunUpTo(1);
                System.Threading.Thread.Sleep(1);
            }
            Assert.That(scheduler.PendingCount, Is.Zero, "startup callbacks did not finish within the test deadline");
        }

        private static void RequestStartupTestCompaction(
            InMemoryAssetIndex index, AssetSearchService search, string suffix)
        {
            var changed = index.Enumerate().Select(record => new AssetRecord(
                record.Guid, record.AssetPath, record.Name + suffix,
                record.TypeName, record.TagSearchText)).ToArray();
            search.ApplyCatalogDelta(index.ApplyFileChanges(changed, Array.Empty<string>()));
            Assert.That(search.TombstoneCount, Is.GreaterThan(0));
            Assert.That(search.IsCompactionRequested, Is.True);
        }

        [Test]
        public void DeferredStartup_DisposeBeforeCallbackPreventsCaptureAndPublication()
        {
            var addressables = new RecordingAddressablesQuery();
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var state = new ZepState();
            var controller = new ZepController(
                state, _gateway, new ProjectFileCommandService(_gateway), _source,
                index, new AssetSearchService(index), _tagDefinitions,
                new FakeSynchronizer(_source, index), scheduler, new NoOpShellReveal(),
                addressables, null, searchWasUsedInThisProject: true);
            var publications = 0;
            state.PaneChanged += _ => publications++;
            state.GlobalChanged += () => publications++;
            try
            {
                controller.Refresh();
                Assert.That(scheduler.PendingCount, Is.EqualTo(1));
                var version = index.RecordVersion;
                publications = 0;
                controller.Dispose();
                DrainStartupTestScheduler(scheduler);
                Assert.That(addressables.CaptureCount, Is.Zero);
                Assert.That(addressables.FirstExpandedRecords, Is.Null);
                Assert.That(index.RecordVersion, Is.EqualTo(version));
                Assert.That(publications, Is.Zero, "disposal and drain must not publish presentation changes");
                Assert.That(scheduler.PendingCount, Is.Zero);
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally
            {
                controller.Dispose();
            }
        }

        // The Addressables settings change under a live panel (an asset marked
        // addressable in the Inspector); the port raises Changed and the
        // controller re-captures through the same deferred worker — asking it
        // for the addressables half only, since no posting moved.
        [Test]
        public void AddressablesChanged_WhileIdle_RecapturesTheEntrySetWithoutASearchBuild()
        {
            var addressables = new MutableAddressablesQuery();
            var scheduler = new QueuedScheduler();
            var index = new InMemoryAssetIndex();
            var controller = CreateDeferredWorkerController(addressables, scheduler, index);
            try
            {
                controller.Refresh();
                scheduler.RunAll();
                Assert.That(
                    controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable,
                    Is.False,
                    "fixture guard: nothing is addressable at startup");
                Assert.That(addressables.ExpandAttempts, Is.EqualTo(1), "fixture guard");

                addressables.Guids.Add("hero-guid");
                addressables.RaiseChanged();
                // The worker starts on the next tick. Move the catalog on under
                // it: a search build would lose the version race and cost a
                // retry, whereas an addressables-only run has nothing to lose.
                scheduler.RunUpTo(1);
                index.ApplyFileChanges(
                    new[] { new AssetRecord("race-guid", "Assets/Race.png", "Race", "Texture2D") },
                    Array.Empty<string>());
                scheduler.RunAll();

                Assert.That(
                    controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable,
                    Is.True,
                    "the re-captured entry set must reach the badge");
                Assert.That(addressables.ExpandAttempts, Is.EqualTo(2),
                    "exactly one re-capture: no search build, so no version-mismatch retry");
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void AddressablesChanged_WhileTheWorkerRuns_RunsOnceMoreAfterItLands()
        {
            var addressables = new MutableAddressablesQuery
            {
                HoldExpand = new System.Threading.ManualResetEventSlim(false),
            };
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler);
            try
            {
                controller.Refresh();
                // Capture runs synchronously inside the startup callback, right
                // before the worker is started — so once it has run, a worker is
                // in flight on the OLD (empty) capture and parked in Expand.
                for (var i = 0; i < 50 && addressables.CaptureCount == 0 && scheduler.PendingCount > 0; i++)
                {
                    scheduler.RunUpTo(1);
                }

                Assert.That(addressables.CaptureCount, Is.EqualTo(1), "fixture guard: the worker is in flight");

                addressables.Guids.Add("hero-guid");
                addressables.RaiseChanged();
                addressables.HoldExpand.Set();
                scheduler.RunAll();

                Assert.That(addressables.CaptureCount, Is.EqualTo(2),
                    "the change landed while the worker held the old capture, so one more run must follow it");
                Assert.That(addressables.ExpandAttempts, Is.EqualTo(2));
                Assert.That(
                    controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable,
                    Is.True,
                    "the follow-up run's result is the one that stays");
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally
            {
                addressables.HoldExpand.Set();
                controller.Dispose();
            }
        }

        [Test]
        public void AddressablesChanged_AfterDispose_SchedulesNothing()
        {
            var addressables = new MutableAddressablesQuery();
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler);
            controller.Refresh();
            scheduler.RunAll();

            controller.Dispose();
            addressables.RaiseChanged();

            Assert.That(addressables.ChangedSubscriberCount, Is.Zero,
                "Dispose must unsubscribe, or the package's static settings event keeps a dead controller alive");
            Assert.That(scheduler.PendingCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressableFolder_IncrementalImportUpdatesMembership(bool searchWasUsed)
        {
            var addressables = new MutableAddressablesQuery();
            addressables.FolderPaths.Add("Assets/Art");
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler, searchWasUsedInThisProject: searchWasUsed);
            try
            {
                controller.Refresh();
                DrainStartupTestScheduler(scheduler);
                Assert.That(controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable, Is.True, "fixture guard");
                const string added = "Assets/Art/Added.png";
                var counts = MeasurePerfCounters(() =>
                {
                    _source.Records.Add(new AssetRecord("added-guid", added, "Added", "Texture2D"));
                    controller.HandleAssetChanges(new ProjectAssetChanges(new[] { added }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
                    controller.HandleProjectChanged();
                    DrainStartupTestScheduler(scheduler);
                });

                Assert.That(controller.GetPresentation(added, "Texture2D").IsAddressable, Is.True);
                Assert.That(addressables.CaptureCount, Is.EqualTo(2));
                Assert.That(counts.GetCount(ZepPerfCounter.SearchFullBuilds), Is.Zero,
                    "the inherited badge refresh must not rebuild search postings");
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally { controller.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressableFolder_IncrementalMoveOutUpdatesMembership(bool searchWasUsed)
        {
            var addressables = new MutableAddressablesQuery();
            addressables.FolderPaths.Add("Assets/Art");
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler, searchWasUsedInThisProject: searchWasUsed);
            try
            {
                controller.Refresh();
                DrainStartupTestScheduler(scheduler);
                const string before = "Assets/Art/Hero.png";
                const string after = "Assets/Audio/Hero.png";
                Assert.That(controller.GetPresentation(before, "Texture2D").IsAddressable, Is.True, "fixture guard");
                _gateway.MovePath(before, after);
                controller.HandleAssetChanges(new ProjectAssetChanges(Array.Empty<string>(), Array.Empty<string>(), new[] { after }, new[] { before }));
                controller.HandleProjectChanged();
                DrainStartupTestScheduler(scheduler);

                Assert.That(controller.GetPresentation(after, "Texture2D").IsAddressable, Is.False);
                Assert.That(addressables.CaptureCount, Is.EqualTo(2));
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally { controller.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressableFolder_CatalogMutationDuringWorkerNeverAdoptsStaleBadge(bool searchWasUsed)
        {
            using var hold = new System.Threading.ManualResetEventSlim(false);
            var addressables = new MutableAddressablesQuery { HoldExpand = hold };
            addressables.FolderPaths.Add("Assets/Art");
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler, searchWasUsedInThisProject: searchWasUsed);
            try
            {
                controller.Refresh();
                scheduler.RunUpTo(1);
                Assert.That(addressables.CaptureCount, Is.EqualTo(1), "fixture guard: old catalog captured");
                const string moved = "Assets/Audio/Hero.png";
                const string added = "Assets/Art/WhileWorkerRuns.png";
                _gateway.MovePath("Assets/Art/Hero.png", moved);
                _source.Records.Add(new AssetRecord("worker-added-guid", added, "WhileWorkerRuns", "Texture2D"));
                controller.HandleAssetChanges(new ProjectAssetChanges(new[] { added }, Array.Empty<string>(), new[] { moved }, new[] { "Assets/Art/Hero.png" }));
                controller.HandleProjectChanged();
                hold.Set();

                var staleBadgeWasPublished = false;
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (scheduler.PendingCount > 0 && DateTime.UtcNow < deadline)
                {
                    scheduler.RunUpTo(1);
                    staleBadgeWasPublished |= controller.GetPresentation(moved, "Texture2D").IsAddressable;
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(scheduler.PendingCount, Is.Zero, "worker did not settle within the test deadline");
                Assert.That(staleBadgeWasPublished, Is.False, "a rejected generation must never introduce an inherited badge at the moved-out path");
                Assert.That(controller.GetPresentation(added, "Texture2D").IsAddressable, Is.True);
                Assert.That(controller.GetPresentation(moved, "Texture2D").IsAddressable, Is.False);
                Assert.That(addressables.CaptureCount, Is.EqualTo(2));
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally { hold.Set(); controller.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressableFolder_RelevantChangesCoalesceBeforeAndDuringExpansion(bool startWorkerBeforeChanges)
        {
            using var hold = new System.Threading.ManualResetEventSlim(false);
            var addressables = new MutableAddressablesQuery();
            addressables.FolderPaths.Add("Assets/Art");
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler, searchWasUsedInThisProject: false);
            try
            {
                controller.Refresh();
                DrainStartupTestScheduler(scheduler);
                if (startWorkerBeforeChanges)
                {
                    addressables.HoldExpand = hold;
                    addressables.RaiseChanged();
                    scheduler.RunUpTo(1);
                    Assert.That(addressables.CaptureCount, Is.EqualTo(2), "fixture guard");
                }

                for (var i = 0; i < 2; i++)
                {
                    var path = "Assets/Art/Coalesced" + i + ".png";
                    _source.Records.Add(new AssetRecord("coalesced-guid-" + i, path, "Coalesced" + i, "Texture2D"));
                    controller.HandleAssetChanges(new ProjectAssetChanges(new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
                    controller.HandleProjectChanged();
                }
                hold.Set();
                DrainStartupTestScheduler(scheduler);

                Assert.That(controller.GetPresentation("Assets/Art/Coalesced0.png", "Texture2D").IsAddressable, Is.True);
                Assert.That(controller.GetPresentation("Assets/Art/Coalesced1.png", "Texture2D").IsAddressable, Is.True);
                Assert.That(addressables.CaptureCount, Is.EqualTo(startWorkerBeforeChanges ? 3 : 2),
                    "the change burst needs one fresh snapshot, not one per delta");
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally { hold.Set(); controller.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddressableFolder_UnrelatedOrDirectOnlyChangesDoNotReexpand(bool directOnly)
        {
            var addressables = new MutableAddressablesQuery();
            addressables.Guids.Add("hero-guid");
            if (!directOnly) addressables.FolderPaths.Add("Assets/Art");
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler, searchWasUsedInThisProject: false);
            try
            {
                controller.Refresh();
                DrainStartupTestScheduler(scheduler);
                var path = directOnly ? "Assets/Art/DirectOnly.png" : "Assets/Art2/Outside.png";
                _source.Records.Add(new AssetRecord("outside-guid", path, "Outside", "Texture2D"));
                controller.HandleAssetChanges(new ProjectAssetChanges(new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
                controller.HandleProjectChanged();
                DrainStartupTestScheduler(scheduler);

                Assert.That(addressables.CaptureCount, Is.EqualTo(1));
                Assert.That(controller.GetPresentation(path, "Texture2D").IsAddressable, Is.False);
                Assert.That(controller.GetPresentation("Assets/Art/Hero.png", "Texture2D").IsAddressable, Is.True);
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally { controller.Dispose(); }
        }

        [Test]
        public void AddressableFolder_MovingAnEmptyScopeAncestorRecapturesItsNewPath()
        {
            const string before = "Assets/EmptyAddressable";
            const string after = "Assets/MovedAddressable";
            _source.Folders.Add(AssetPath.From(before));
            _source.Folders.Add(AssetPath.From(before + "/Scope"));
            var addressables = new MutableAddressablesQuery();
            addressables.FolderPaths.Add(before + "/Scope");
            var scheduler = new QueuedScheduler();
            var controller = CreateDeferredWorkerController(addressables, scheduler, searchWasUsedInThisProject: false);
            try
            {
                controller.Refresh();
                DrainStartupTestScheduler(scheduler);
                _gateway.MovePath(before, after);
                addressables.FolderPaths.Clear();
                addressables.FolderPaths.Add(after + "/Scope");
                controller.HandleAssetChanges(new ProjectAssetChanges(Array.Empty<string>(), Array.Empty<string>(), new[] { after }, new[] { before }));
                controller.HandleProjectChanged();
                DrainStartupTestScheduler(scheduler);
                Assert.That(addressables.CaptureCount, Is.EqualTo(2), "even an empty scope's ancestor move must refresh its captured path");

                var added = after + "/Scope/Child.asset";
                _source.Records.Add(new AssetRecord("scope-child-guid", added, "Child", "ScriptableObject"));
                controller.HandleAssetChanges(new ProjectAssetChanges(new[] { added }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
                controller.HandleProjectChanged();
                DrainStartupTestScheduler(scheduler);

                Assert.That(controller.GetPresentation(added, "ScriptableObject").IsAddressable, Is.True);
                Assert.That(scheduler.Faults, Is.Empty);
            }
            finally { controller.Dispose(); }
        }

        private ZepController CreateDeferredWorkerController(
            IAddressablesQuery addressables,
            QueuedScheduler scheduler,
            InMemoryAssetIndex index = null,
            FakeSynchronizer synchronizer = null,
            bool searchWasUsedInThisProject = true)
        {
            index ??= new InMemoryAssetIndex();
            return new ZepController(
                new ZepState(),
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                new AssetSearchService(index),
                _tagDefinitions,
                synchronizer ?? new FakeSynchronizer(_source, index),
                scheduler,
                new NoOpShellReveal(),
                addressables,
                null,
                // Search-build tests keep the historical warm default; folder
                // membership tests also exercise projects that never searched.
                searchWasUsedInThisProject: searchWasUsedInThisProject);
        }

        [Test]
        public void FullIndexRefresh_PreservesPrimaryInvalidFolderFallback()
        {
            var primaryPane = _state.PrimaryPane;
            primaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Missing"));
            primaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Missing"));

            _controller.Refresh();

            Assert.That(
                primaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets")));
            Assert.That(
                primaryPane.SelectedFolderPath,
                Is.EqualTo(AssetPath.From("Assets")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExternalFolderMove_RewritesOrderedFavoritesAndLeavesPrefixSiblings(bool beforePanelUse)
        {
            if (beforePanelUse)
            {
                _controller.Dispose();
                _index.Clear();
                _controller = new ZepController(
                    _state, _gateway, _fileCommands, _source, _index, _searchService,
                    _tagDefinitions, _synchronizer, _scheduler,
                    new NoOpShellReveal(), new EmptyAddressablesQuery());
            }

            var favorites = new FavoriteFolderStore(new List<string>
            {
                "Assets/Audio", "Assets/Art/UI", "Assets/Art2", "Assets/Art"
            }, () => { });
            var rewrites = new List<string>();
            _controller.AssetPathRewritten += (oldPath, newPath) =>
            {
                rewrites.Add(oldPath + " -> " + newPath);
                favorites.Rewrite(oldPath, newPath);
            };
            _gateway.MovePath("Assets/Art", "Assets/Illustration");

            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Assets/Illustration" }, new[] { "Assets/Art" }));

            Assert.That(rewrites, Is.EqualTo(new[] { "Assets/Art -> Assets/Illustration" }));
            Assert.That(favorites.Paths, Is.EqualTo(new[]
            {
                "Assets/Audio", "Assets/Illustration/UI", "Assets/Art2", "Assets/Illustration"
            }));
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void ExternalFolderMove_OwnedEchoAndRenameBackCannotRewriteFavoritesToAnObsoletePath()
        {
            var favorites = new FavoriteFolderStore(new List<string> { "Assets/Art/UI" }, () => { });
            var rewrites = new List<string>();
            _controller.AssetPathRewritten += (oldPath, newPath) =>
            {
                rewrites.Add(oldPath + " -> " + newPath);
                favorites.Rewrite(oldPath, newPath);
            };

            Assert.That(_controller.Rename(_state.PrimaryPane, AssetPath.From("Assets/Art"), "Illustration"), Is.True);
            _source.Records.Add(new AssetRecord("echo-import", "Assets/Audio/Other.wav", "Other", "AudioClip"));
            // The unrelated import makes the catalog batch change even though
            // this move was already applied by the owned operation.
            ApplyExternalChanges(new ProjectAssetChanges(
                new[] { "Assets/Audio/Other.wav" }, Array.Empty<string>(),
                new[] { "Assets/Illustration" }, new[] { "Assets/Art" }));
            Assert.That(rewrites, Has.Count.EqualTo(1));

            Assert.That(_controller.Rename(_state.PrimaryPane, AssetPath.From("Assets/Illustration"), "Art"), Is.True);
            _source.Records.Add(new AssetRecord("late-import", "Assets/Audio/Late.wav", "Late", "AudioClip"));
            ApplyExternalChanges(new ProjectAssetChanges(
                new[] { "Assets/Audio/Late.wav" }, Array.Empty<string>(),
                new[] { "Assets/Illustration" }, new[] { "Assets/Art" }));
            Assert.That(_index.TryGetFolder("Assets/Art", out _), Is.True,
                "the late callback must not move canonical state back to its obsolete destination");
            Assert.That(_index.TryGetFolder("Assets/Illustration", out _), Is.False);
            Assert.That(_index.TryGetByPath("Assets/Audio/Late.wav", out _), Is.True,
                "unrelated concrete changes in the late callback still apply");
            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Assets/Art" }, new[] { "Assets/Illustration" }));

            Assert.That(rewrites, Is.EqualTo(new[]
            {
                "Assets/Art -> Assets/Illustration", "Assets/Illustration -> Assets/Art"
            }));
            Assert.That(favorites.Paths, Is.EqualTo(new[] { "Assets/Art/UI" }));

            _gateway.MovePath("Assets/Art", "Assets/Drawings");
            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Assets/Drawings" }, new[] { "Assets/Art" }));
            Assert.That(rewrites, Has.Count.EqualTo(3), "a later real external move still publishes");
            Assert.That(favorites.Paths, Is.EqualTo(new[] { "Assets/Drawings/UI" }));
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void ExternalFolderMove_InvalidPairsDoNotRewriteFavorites()
        {
            var rewrites = new List<string>();
            _controller.AssetPathRewritten += (oldPath, newPath) => rewrites.Add(oldPath + " -> " + newPath);
            _source.Folders.Add(AssetPath.From("Assets/Art/New Folder"));

            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(), new[] { "Assets/Gone" },
                new[] { "Assets/Art/New Folder", "Assets/Audio/Theme.wav", "Assets/Missing", "Assets/Audio" },
                new[] { "Assets/Art", "Assets/Art/Hero.png", "Assets/Art/UI", "" }));

            Assert.That(rewrites, Is.Empty, "create-shaped, file, unresolved and unpaired moves are not completed folder rewrites");
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void ExternalFolderMove_CaseOnlyCallbackKeepsTheFavoriteResolvable()
        {
            var favorites = new FavoriteFolderStore(new List<string> { "Assets/Art/UI" }, () => { });
            _controller.AssetPathRewritten += (oldPath, newPath) => favorites.Rewrite(oldPath, newPath);
            _gateway.MovePath("Assets/Art", "Assets/art");

            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Assets/art" }, new[] { "Assets/Art" }));

            Assert.That(favorites.Paths, Is.EqualTo(new[] { "Assets/Art/UI" }),
                "bookmark path identity is case-insensitive");
            Assert.That(_controller.GetVisibleFolderEntry(favorites.Paths[0])?.Path,
                Is.EqualTo("Assets/art/UI"));
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void ExternalFolderMove_UnresolvedPairRemainsConcreteRecoveryEvidence()
        {
            _gateway.RemovePath("Assets/Art");
            _controller.HandleAssetChanges(new ProjectAssetChanges(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Assets/Importing" }, new[] { "Assets/Art" }));

            var recorded = _synchronizer.RecordedConcreteChanges.Last();
            Assert.That(recorded.Moved, Is.EqualTo(new[] { "Assets/Importing" }));
            Assert.That(recorded.MovedFromPaths, Is.EqualTo(new[] { "Assets/Art" }),
                "absence of both ends is unresolved, not proof of a superseded callback");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FolderMove_RewritesRootDescendantSelectionAndHistory(bool windowPane)
        {
            var artPane = windowPane
                ? _state.OpenWindowPane(ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")))
                : OpenArtPane();
            artPane.SyncNavigationHistory();
            artPane.TrySetFolderSelection("Assets/Art/UI");
            artPane.SyncNavigationHistory();
            artPane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            _gateway.MovePath("Assets/Art", "Assets/Illustration");
            _controller.HandleAssetChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "Assets/Illustration" },
                new[] { "Assets/Art" }));
            _controller.Refresh();

            Assert.That(_state.IsLivePane(artPane), Is.True);
            Assert.That(
                artPane.Source,
                Is.EqualTo(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Illustration"))));
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI")));
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI/Icon.png")));
            artPane.NavigateBack();
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration")));
        }

        [Test]
        public void PrimaryFolderMove_PublishesTheRemappedSidebarSelectionImmediately()
        {
            _scheduler.RunAll();
            var primary = _state.PrimaryPane;
            primary.TrySetFolderSelection("Assets/Art/UI");
            var movedPath = AssetPath.From("Assets/Illustration/UI");
            var observedPublication = false;
            AssetPath firstCurrent = default;
            AssetPath firstSelected = default;
            _state.PaneChanged += change =>
            {
                if (!observedPublication
                    && ReferenceEquals(change.NewPane, primary)
                    && primary.CurrentFolderPath == movedPath)
                {
                    observedPublication = true;
                    firstCurrent = primary.CurrentFolderPath;
                    firstSelected = primary.SelectedFolderPath;
                }
            };
            _gateway.MovePath("Assets/Art", "Assets/Illustration");

            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(), Array.Empty<string>(),
                new[] { "Assets/Illustration" }, new[] { "Assets/Art" }));

            Assert.That(observedPublication, Is.True);
            Assert.That(firstCurrent, Is.EqualTo(movedPath));
            Assert.That(firstSelected, Is.EqualTo(movedPath),
                "the first publication must already carry the sidebar's remapped selection");
            Assert.That(primary.CurrentFolderPath, Is.EqualTo(movedPath));
            Assert.That(primary.SelectedFolderPath, Is.EqualTo(movedPath));
            Assert.That(_controller.GetVisibleFolderEntry(primary.SelectedFolderPath)?.Path,
                Is.EqualTo(movedPath.Value));
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FolderDeletion_ClosesOnlyMatchingRootedPane(bool windowPane)
        {
            var artPane = windowPane
                ? _state.OpenWindowPane(ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")))
                : OpenArtPane();
            var duplicateWindow = _state.OpenWindowPane(artPane.Source);
            var survivingWindow = _state.OpenWindowPane(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Audio")));
            _state.ActivatePane(artPane);
            _gateway.RemovePath("Assets/Art");
            _controller.HandleAssetChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { "Assets/Art" },
                Array.Empty<string>(),
                Array.Empty<string>()));
            _controller.Refresh();

            Assert.That(_state.IsLivePane(artPane), Is.False);
            Assert.That(_state.IsLivePane(duplicateWindow), Is.False);
            Assert.That(_state.AuxiliaryPanes, Is.Empty);
            Assert.That(_state.WindowPanes, Is.EqualTo(new[] { survivingWindow }));
            Assert.That(_state.ActivePane, Is.SameAs(_state.PrimaryPane));
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From(windowPane ? "Assets" : "Assets/Audio")));
        }

        [Test]
        public void WindowPanes_ProjectAndRefreshIndependentlyAlongsideTheSplit()
        {
            var split = OpenArtPane();
            var folder = _state.OpenWindowPane(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art/UI")));
            var tag = _state.OpenWindowPane(ZepPaneSource.FixedTag("blue"));
            folder.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));
            tag.SetSearchText("Hero");

            Assert.That(folder.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Art/UI/Icon.png" }));
            Assert.That(tag.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Art/Hero.png" }));
            Assert.That(split.SearchText, Is.Empty);
            Assert.That(_state.PrimaryPane.CurrentFolderPath.Value, Is.EqualTo("Assets/Audio"));

            _source.RemovePath("Assets/Art/UI/Icon.png");
            _source.Records.Add(new AssetRecord(
                "late-ui-guid", "Assets/Art/UI/Late.png", "Late", "Texture2D"));
            _controller.Refresh();

            Assert.That(folder.SelectedAssetPaths, Is.Empty);
            Assert.That(folder.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Art/UI/Late.png" }));
            Assert.That(tag.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Art/Hero.png" }));
            Assert.That(_state.AuxiliaryPanes, Is.EqualTo(new[] { split }));
        }

        // ApplyProjection walks the primary and the auxiliaries in place instead of
        // through a copy of the lazy `Panes` iterator. The two panes show different
        // folders and each gains a different new asset, so a projection that skipped
        // either pane - or that handed both the same list - fails here, which a
        // "both panes are non-empty" assertion could not tell apart.
        [Test]
        public void Projection_WithAnAuxiliaryPaneOpen_ProjectsEveryLivePaneAgainstItsOwnFolder()
        {
            var artPane = OpenArtPane();
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));

            _source.Records.Add(new AssetRecord(
                "late-audio-guid",
                "Assets/Audio/Late.wav",
                "Late",
                "AudioClip"));
            _source.Records.Add(new AssetRecord(
                "late-art-guid",
                "Assets/Art/Late.png",
                "Late",
                "Texture2D"));
            _controller.Refresh();

            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EquivalentTo(new[]
                {
                    "Assets/Audio/Late.wav",
                    "Assets/Audio/Theme.wav"
                }));
            Assert.That(
                artPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EquivalentTo(new[]
                {
                    "Assets/Art/Hero.png",
                    "Assets/Art/UI",
                    "Assets/Art/Late.png",
                    "Assets/Art/ProjectConfiguration.asset"
                }));
        }

        [Test]
        public void DescendantDeletion_FallsBackInsideRootWithoutClosingPane()
        {
            var artPane = OpenArtPane();
            artPane.TrySetFolderSelection("Assets/Art/UI");
            _gateway.RemovePath("Assets/Art/UI");
            _controller.HandleAssetChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { "Assets/Art/UI" },
                Array.Empty<string>(),
                Array.Empty<string>()));
            _controller.Refresh();

            Assert.That(_state.IsLivePane(artPane), Is.True);
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                artPane.CurrentFolderPath,
                Is.Not.EqualTo(AssetPath.From("Assets")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TagDeletion_ClosesOnlyMatchingTagPane(bool windowPane)
        {
            var tagPane = windowPane
                ? _state.OpenWindowPane(ZepPaneSource.FixedTag("blue"))
                : _state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var survivingWindow = _state.OpenWindowPane(ZepPaneSource.FixedTag("red"));
            _state.PrimaryPane.ApplyTagView("red");

            Assert.That(_tagDefinitions.TryDeleteTag("blue", out var error), Is.True, error);
            _controller.Refresh();

            Assert.That(_state.IsLivePane(tagPane), Is.False);
            Assert.That(_state.WindowPanes, Is.EqualTo(new[] { survivingWindow }));
            Assert.That(_state.PrimaryPane.SelectedTagFilterId, Is.EqualTo("red"));
            Assert.That(_state.PrimaryPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
        }

        [Test]
        public void ExternalTagVersionRefresh_PreservesTypeAncestryInSearchIndex()
        {
            var assetPath = AssetPath.From("Assets/Art/ProjectConfiguration.asset");
            var result = _gateway.SetTagIds(
                new Dictionary<AssetPath, IReadOnlyList<string>>
                {
                    [assetPath] = new[] { "red" }
                });

            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);

            // A pane change applies the projection without a full source sync. The
            // changed gateway TagVersion must therefore patch the existing record.
            _state.PrimaryPane.SetSearchText("ProjectConfiguration");

            Assert.That(_index.TryGetByPath(assetPath.Value, out var updated), Is.True);
            Assert.That(
                updated.BaseTypeNames,
                Is.EqualTo(new[] { "ScriptableObject", "Object" }));
            Assert.That(updated.TagSearchText, Does.Contain("Red"));
            Assert.That(
                _searchService
                    .Search("t:ScriptableObject tag:red")
                    .Select(record => record.Guid),
                Is.EqualTo(new[] { "config-guid" }));
        }

        [Test]
        public void DragSourceValidation_RejectsFolderOrTagThatDisappeared()
        {
            var folderSource = ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art"));
            var tagSource = ZepPaneSource.FixedTag("blue");

            Assert.That(
                _controller.TryReconcilePaneSource(folderSource, out var liveFolder),
                Is.True);
            Assert.That(liveFolder, Is.EqualTo(folderSource));
            Assert.That(
                _controller.TryReconcilePaneSource(tagSource, out var liveTag),
                Is.True);
            Assert.That(liveTag, Is.EqualTo(tagSource));

            _gateway.RemovePath("Assets/Art");
            _controller.Refresh();
            Assert.That(
                _tagDefinitions.TryDeleteTag("blue", out var error),
                Is.True,
                error);

            Assert.That(
                _controller.TryReconcilePaneSource(folderSource, out _),
                Is.False);
            Assert.That(
                _controller.TryReconcilePaneSource(tagSource, out _),
                Is.False);
        }

        [Test]
        public void RestoreValidation_FallsBackCurrentFolderInsideLiveRoot()
        {
            var source = ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art"));

            Assert.That(
                _controller.TryReconcilePaneSource(
                    source,
                    AssetPath.From("Assets/Art/Missing/Child"),
                    out var reconciledSource,
                    out var missingDescendant),
                Is.True);
            Assert.That(reconciledSource, Is.EqualTo(source));
            Assert.That(missingDescendant, Is.EqualTo(AssetPath.From("Assets/Art")));

            Assert.That(
                _controller.TryReconcilePaneSource(
                    source,
                    AssetPath.From("Assets/Audio"),
                    out _,
                    out var outsideRoot),
                Is.True);
            Assert.That(outsideRoot, Is.EqualTo(AssetPath.From("Assets/Art")));
        }

        [Test]
        public void FirstPaintMetadata_ReloadPreservesTagsAndFolderTints()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art"));
            _state.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art"));
            _source.Folders.Add(AssetPath.From("Assets/Art/PendingFolder"));
            _gateway.SetTags("Assets/Art", "red", "blue");
            _gateway.SetTags("Assets/Art/Hero.png", "blue");
            _gateway.SetTags("Assets/Art/PendingFolder", "red", "blue");
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Art",
                new DirectFolderEntry(AssetPath.From("Assets/Art/Hero.png"), false, "Hero", "Texture2D"),
                new DirectFolderEntry(AssetPath.From("Assets/Art/PendingFolder"), true, "PendingFolder", "Folder"));
            _controller = CreateControllerWithDirectSource(directSource);

            _controller.BeginPanelUse();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/Hero.png").Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/PendingFolder").Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue", "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/PendingFolder").FolderTintColor, Is.EqualTo(ZepTagColor.Blue));
            Assert.That(_controller.GetTagsForPath("Assets/Art").Select(tag => tag.Id), Is.EqualTo(new[] { "blue", "red" }));
            Assert.That(_controller.GetFolderTintColorForPath("Assets/Art"), Is.EqualTo(ZepTagColor.Blue));
            Assert.That(_controller.GetFolderTintColorForPath("Assets/Art/PendingFolder"), Is.EqualTo(ZepTagColor.Blue));

            _scheduler.RunAll();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/Hero.png").Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/PendingFolder").Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue", "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/PendingFolder").FolderTintColor, Is.EqualTo(ZepTagColor.Blue));
            Assert.That(_controller.GetTagsForPath("Assets/Art").Select(tag => tag.Id), Is.EqualTo(new[] { "blue", "red" }));
            Assert.That(_controller.GetFolderTintColorForPath("Assets/Art"), Is.EqualTo(ZepTagColor.Blue));
        }

        [Test]
        public void FirstPaintMetadata_UnknownIdentityHasNoMetadata()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art"));
            _state.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art"));
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Art",
                new DirectFolderEntry(AssetPath.From("Assets/Art/Unknown.asset"), false, "Unknown", "ScriptableObject"),
                new DirectFolderEntry(AssetPath.From("Assets/Art/UnknownFolder"), true, "UnknownFolder", "Folder"));
            _controller = CreateControllerWithDirectSource(directSource);

            _controller.BeginPanelUse();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/Unknown.asset").Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/UnknownFolder").Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/UnknownFolder").FolderTintColor, Is.EqualTo(ZepTagColor.None));

            _gateway.SetTags("Assets/Art/Unknown.asset", "blue");
            _gateway.SetTags("Assets/Art/UnknownFolder", "blue");
            Assert.That(_controller.UpdateTagDefinition(new ZepTag("blue", "Azure", ZepTagColor.Blue)), Is.True);

            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/Unknown.asset").Tags.Select(tag => tag.Name), Is.EqualTo(new[] { "Azure" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/UnknownFolder").Tags.Select(tag => tag.Name), Is.EqualTo(new[] { "Azure" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/UnknownFolder").FolderTintColor, Is.EqualTo(ZepTagColor.Blue));

            _gateway.SetTags("Assets/Art/Unknown.asset");
            _gateway.SetTags("Assets/Art/UnknownFolder");
            Assert.That(_controller.UpdateTagDefinition(new ZepTag("blue", "Blue", ZepTagColor.Blue)), Is.True);

            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/Unknown.asset").Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/UnknownFolder").Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Art/UnknownFolder").FolderTintColor, Is.EqualTo(ZepTagColor.None));
            Assert.That(_controller.GetTagsForPath("Assets/Art/Unknown.asset"), Is.Empty);
            Assert.That(_controller.GetFolderTintColorForPath("Assets/Art/UnknownFolder"), Is.EqualTo(ZepTagColor.None));
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
        }

        [Test]
        public void FirstPaintMetadata_TagChangesRefreshPendingRows()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art"));
            _state.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art"));
            _source.Folders.Add(AssetPath.From("Assets/Art/PendingFolder"));
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Art",
                new DirectFolderEntry(AssetPath.From("Assets/Art/Hero.png"), false, "Hero", "Texture2D"),
                new DirectFolderEntry(AssetPath.From("Assets/Art/PendingFolder"), true, "PendingFolder", "Folder"));
            _controller = CreateControllerWithDirectSource(directSource);

            var file = AssetPath.From("Assets/Art/Hero.png");
            var folder = AssetPath.From("Assets/Art/PendingFolder");
            var currentFolder = AssetPath.From("Assets/Art");
            _gateway.SetTags(file.Value);
            _gateway.SetTags(folder.Value);
            _gateway.SetTags(currentFolder.Value);
            Assert.That(_gateway.CanTag(file), Is.True);
            Assert.That(_gateway.CanTag(folder), Is.True);
            Assert.That(_gateway.CanTag(currentFolder), Is.True);
            _controller.BeginPanelUse();

            Assert.That(_controller.AddTag(new[] { file, folder, currentFolder }, "red"), Is.True);
            Assert.That(_gateway.GetTagIds(file), Is.EqualTo(new[] { "red" }));
            Assert.That(_gateway.GetTagIds(folder), Is.EqualTo(new[] { "red" }));
            Assert.That(_gateway.GetTagIds(currentFolder), Is.EqualTo(new[] { "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == file.Value).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.Red));
            Assert.That(_controller.GetTagsForPath(currentFolder.Value).Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_controller.GetFolderTintColorForPath(currentFolder.Value), Is.EqualTo(ZepTagColor.Red));

            Assert.That(_controller.AddTag(new[] { folder, currentFolder }, "blue"), Is.True);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue", "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.Blue));
            Assert.That(_controller.UpdateTagDefinition(new ZepTag("blue", "Azure", ZepTagColor.Green)), Is.True);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags.Single(tag => tag.Id == "blue").Name, Is.EqualTo("Azure"));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.Green));

            Assert.That(_controller.DeleteTagDefinition("blue"), Is.True);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.Red));
            Assert.That(_controller.RemoveTag(new[] { file, folder, currentFolder }, "red"), Is.True);
            Assert.That(_gateway.GetTagIds(file), Is.Empty);
            Assert.That(_gateway.GetTagIds(folder), Is.Empty);
            Assert.That(_gateway.GetTagIds(currentFolder), Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == file.Value).Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.None));
            Assert.That(_controller.GetTagsForPath(currentFolder.Value), Is.Empty);
            Assert.That(_controller.GetFolderTintColorForPath(currentFolder.Value), Is.EqualTo(ZepTagColor.None));

            Assert.That(_controller.AddTag(new[] { file, folder, currentFolder }, "red"), Is.True);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == file.Value).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.Red));
            Assert.That(_controller.GetTagsForPath(currentFolder.Value).Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(_controller.GetFolderTintColorForPath(currentFolder.Value), Is.EqualTo(ZepTagColor.Red));

            Assert.That(_controller.ClearTags(new[] { file, folder, currentFolder }), Is.True);
            Assert.That(_gateway.GetTagIds(file), Is.Empty);
            Assert.That(_gateway.GetTagIds(folder), Is.Empty);
            Assert.That(_gateway.GetTagIds(currentFolder), Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == file.Value).Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).Tags, Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == folder.Value).FolderTintColor, Is.EqualTo(ZepTagColor.None));
            Assert.That(_controller.GetTagsForPath(currentFolder.Value), Is.Empty);
            Assert.That(_controller.GetFolderTintColorForPath(currentFolder.Value), Is.EqualTo(ZepTagColor.None));
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
        }

        [Test]
        public void FirstPaintMetadata_HydrationRetiresPendingPresentation()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art"));
            _state.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art"));
            _gateway.SetTags("Assets/Art/Hero.png", "blue");
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Art",
                new DirectFolderEntry(AssetPath.From("Assets/Art/Hero.png"), false, "Hero", "Texture2D"));
            _controller = CreateControllerWithDirectSource(directSource);

            _controller.BeginPanelUse();
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single().Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
            Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.EqualTo(new[] { "blue" }));
            _source.Records.RemoveAll(record => record.AssetPath == "Assets/Art/Hero.png");
            _scheduler.RunAll();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.EqualTo(new[] { "blue" }));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(entry => entry.Path), Has.No.Member("Assets/Art/Hero.png"));
            Assert.That(_controller.GetTagsForPath("Assets/Art/Hero.png"), Is.Empty);
        }

        [Test]
        public void PaneSourceReadiness_WaitsForDeferredAuthoritativeHydration()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            var index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            _controller = new ZepController(
                _state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                index,
                new AssetSearchService(index),
                _tagDefinitions,
                new FakeSynchronizer(_source, index),
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            var readinessCount = 0;
            _controller.PaneSourceReconciliationReady += () => readinessCount++;

            _controller.InitializeIndex();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
            Assert.That(_state.Folders, Is.Empty);

            _scheduler.RunAll();

            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            Assert.That(readinessCount, Is.EqualTo(1));
            Assert.That(_state.Folders, Is.Not.Empty);

            _controller.Refresh();
            Assert.That(readinessCount, Is.EqualTo(1));
        }

        [Test]
        public void BeginPanelUse_RestoredFolderPaintsDirectContentsBeforeHydration()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(
                AssetPath.From("Assets/Scripts"));
            _state.PrimaryPane.TrySetSelectedFolderPath(
                AssetPath.From("Assets/Scripts"));
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Scripts",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Scripts/Reloaded.cs"),
                    isFolder: false,
                    "Reloaded",
                    "MonoScript"));
            _controller = CreateControllerWithDirectSource(directSource);

            _controller.BeginPanelUse();

            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Scripts")));
            Assert.That(
                _state.PrimaryPane.SelectedFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Scripts")));
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Scripts/Reloaded.cs" }));
            Assert.That(
                directSource.ObservedPaths,
                Is.EqualTo(new[] { "Packages", "Assets/Scripts" }),
                "a live restored folder must not pay for an unnecessary Assets-root observation");
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False,
                "the useful restored-folder paint must precede deferred hydration");
        }

        // The same restore in the window's real order, which the test above
        // deliberately does not use: ZepWindow builds the controller
        // first and only then writes the serialized folder into the pane, so a
        // live PaneChanged subscriber sees the restore before BeginPanelUse.
        // Projecting it there resolves the folder against the still-empty
        // post-reload index, falls back to "Assets", and writes that back into
        // the pane — the restore is gone before the first paint.
        [Test]
        public void BeginPanelUse_RestoreWrittenIntoALivePaneSurvivesToTheFirstPaint()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets/Scripts",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Scripts/Reloaded.cs"),
                    isFolder: false,
                    "Reloaded",
                    "MonoScript"));
            _controller = CreateControllerWithDirectSource(directSource);

            _state.PrimaryPane.TrySetCurrentFolderPath(
                AssetPath.From("Assets/Scripts"));
            _state.PrimaryPane.TrySetSelectedFolderPath(
                AssetPath.From("Assets/Scripts"));
            _controller.BeginPanelUse();

            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Scripts")),
                "a restore written into a live pane must not be projected away before panel use starts");
            Assert.That(
                _state.PrimaryPane.SelectedFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Scripts")));
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Scripts/Reloaded.cs" }));
            Assert.That(
                directSource.ObservedPaths,
                Is.EqualTo(new[] { "Packages", "Assets/Scripts" }),
                "BeginPanelUse observes whatever the pane says; an Assets observation means the pane was already rewritten");
        }

        [Test]
        public void BeginPanelUse_MissingRestoredFolderObservesAndPopulatesAssetsFallback()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _state.PrimaryPane.TrySetCurrentFolderPath(
                AssetPath.From("Assets/DeletedDuringReload"));
            _state.PrimaryPane.TrySetSelectedFolderPath(
                AssetPath.From("Assets/DeletedDuringReload"));
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetIncomplete("Assets/DeletedDuringReload");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Scenes"),
                    isFolder: true,
                    "Scenes",
                    "Folder"),
                new DirectFolderEntry(
                    AssetPath.From("Assets/README.txt"),
                    isFolder: false,
                    "README",
                    "TextAsset"));
            _controller = CreateControllerWithDirectSource(directSource);

            _controller.BeginPanelUse();

            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets")));
            Assert.That(
                _state.PrimaryPane.SelectedFolderPath,
                Is.EqualTo(AssetPath.From("Assets")));
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Scenes", "Assets/README.txt" }));
            Assert.That(
                directSource.ObservedPaths,
                Is.EqualTo(new[]
                {
                    "Packages",
                    "Assets/DeletedDuringReload",
                    "Assets"
                }));
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False,
                "the Assets fallback must be useful before deferred hydration");
        }

        [Test]
        public void FolderNavigation_StopsRepeatingDirectObservationAfterAuthoritativeHydration()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art"),
                    isFolder: true,
                    "Art",
                    "Folder"));
            directSource.SetComplete(
                "Assets/Art",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art/Hero.png"),
                    isFolder: false,
                    "Hero",
                    "Texture2D"));
            _controller = CreateControllerWithDirectSource(directSource);

            _controller.BeginPanelUse();
            directSource.ObservedPaths.Clear();
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");

            Assert.That(directSource.ObservedPaths, Is.EqualTo(new[] { "Assets/Art" }),
                "pre-hydration navigation still needs the bounded first-paint observation");
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Does.Contain("Assets/Art/Hero.png"));

            _scheduler.RunAll();
            directSource.ObservedPaths.Clear();
            _state.PrimaryPane.TrySetFolderSelection("Assets/Audio");

            Assert.That(directSource.ObservedPaths, Is.Empty,
                "the synchronized index is authoritative for ordinary folder switches");
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Audio/Theme.wav" }));
        }

        // The post-hydration half of the freshness rule (I2). A folder created
        // outside Unity - or created after the cache was last written - is absent
        // from the hydrated snapshot, but a COMPLETE direct observation of it is
        // a read of the filesystem taken LATER than that snapshot was written, so
        // the snapshot's silence is not evidence of deletion and the pane stands.
        [Test]
        public void FolderMissingFromTheHydratedIndexButPresentOnDisk_KeepsThePaneAfterObservation()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art"),
                    isFolder: true,
                    "Art",
                    "Folder"));
            directSource.SetComplete(
                "Assets/CreatedOutsideUnity",
                new DirectFolderEntry(
                    AssetPath.From("Assets/CreatedOutsideUnity/Note.txt"),
                    isFolder: false,
                    "Note",
                    "TextAsset"));
            _controller = CreateControllerWithDirectSource(directSource);
            _controller.BeginPanelUse();
            _scheduler.RunAll();
            Assert.That(
                _index.ContainsVisibleFolder("Assets/CreatedOutsideUnity"),
                Is.False,
                "the hydrated snapshot knows nothing about this folder");

            _state.PrimaryPane.TrySetFolderSelection("Assets/CreatedOutsideUnity");

            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/CreatedOutsideUnity")));
            Assert.That(
                _state.PrimaryPane.SelectedFolderPath,
                Is.EqualTo(AssetPath.From("Assets/CreatedOutsideUnity")));
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/CreatedOutsideUnity/Note.txt" }),
                "and it paints what the observation actually found");
        }

        // The bound on the half above, and the reason keeping the pane is safe:
        // the observation is retired the moment the index learns the folder is
        // gone, so it can never outlive the folder the way the deleted
        // reconciliation-flag gate used to prevent. The opening assertion is
        // deliberate - without it, a tree that never keeps the pane would pass
        // this test for the wrong reason.
        [Test]
        public void ObservedFolderDeletedAfterHydration_FallsBackToAssets()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art"),
                    isFolder: true,
                    "Art",
                    "Folder"));
            directSource.SetComplete(
                "Assets/CreatedOutsideUnity",
                new DirectFolderEntry(
                    AssetPath.From("Assets/CreatedOutsideUnity/Note.txt"),
                    isFolder: false,
                    "Note",
                    "TextAsset"));
            _controller = CreateControllerWithDirectSource(directSource);
            _controller.BeginPanelUse();
            _scheduler.RunAll();
            _state.PrimaryPane.TrySetFolderSelection("Assets/CreatedOutsideUnity");
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/CreatedOutsideUnity")),
                "the observation has to have kept the pane, or the fallback below proves nothing");

            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[]
                {
                    "Assets/CreatedOutsideUnity/Note.txt",
                    "Assets/CreatedOutsideUnity"
                },
                Array.Empty<string>(),
                Array.Empty<string>()));

            Assert.That(
                _index.ContainsVisibleFolder("Assets/CreatedOutsideUnity"),
                Is.False,
                "the delete retires the observation along with the folder");
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets")),
                "so the pane can no longer stand on it");
            Assert.That(
                _state.PrimaryPane.SelectedFolderPath,
                Is.EqualTo(AssetPath.From("Assets")));
        }

        [Test]
        public void FixedTagAndFolderSources_ProjectIndependentlyAndRemainValid()
        {
            var primaryPane = _state.PrimaryPane;
            primaryPane.TrySetFolderSelection("Assets/Audio");
            primaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Audio/Theme.wav"));

            var tagResult = _state.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue"));
            var tagPane = tagResult.NewPane;
            tagPane.SetSearchText("hero");
            tagPane.SetSelectedTypeFilter("Texture2D");
            tagPane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));

            Assert.That(tagPane.SelectedTagFilterId, Is.EqualTo("blue"));
            Assert.That(tagPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(
                tagPane.SearchScopeOptions.Select(option => option.Scope),
                Is.EqualTo(new[]
                {
                    ZepSearchScope.All,
                    ZepSearchScope.Packages,
                    ZepSearchScope.Assets
                }));
            Assert.That(
                tagPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));
            Assert.That(
                primaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));

            var folderResult = _state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")),
                tagPane);
            var folderPane = folderResult.NewPane;
            _controller.Refresh();

            Assert.That(_state.IsLivePane(folderPane), Is.True);
            Assert.That(folderPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
            Assert.That(
                folderPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                folderPane.SearchScopeOptions.Select(option => option.Scope),
                Is.EqualTo(new[] { ZepSearchScope.Assets }),
                "a rooted folder pane sitting at its own root confines search to that root");
            Assert.That(
                primaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
        }

        // A tag pane used to be pinned to its list. Opening a tagged folder now
        // browses it inside the pane: the projection lists the folder's
        // contents rather than the tag's matches, search is confined to that
        // folder, and the primary pane is not involved at all.
        [Test]
        public void TagPane_OpeningATaggedFolder_ProjectsItsContentsAndConfinesSearchToIt()
        {
            _gateway.SetTags("Assets/Art/UI", "blue");
            ImportRecord("plain-guid", "Assets/Art/UI/Plain.png", "Plain");
            _controller.Refresh();
            var tagPane = _state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            Assert.That(
                tagPane.ProjectedAssets.Select(asset => asset.Path),
                Does.Contain("Assets/Art/UI").And.Not.Contain("Assets/Art/UI/Plain.png"),
                "the list shows the tagged folder, not the untagged file inside it");

            Assert.That(tagPane.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            Assert.That(tagPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
            Assert.That(
                tagPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EquivalentTo(new[] { "Assets/Art/UI/Icon.png", "Assets/Art/UI/Plain.png" }));
            Assert.That(
                tagPane.SearchScopeOptions.Select(option => option.Scope),
                Is.EqualTo(new[] { ZepSearchScope.Assets }),
                "sitting at its ceiling the pane offers only the ceiling's scope");
            Assert.That(_state.PrimaryPane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets")));

            tagPane.SetSearchText("Hero");
            Assert.That(
                tagPane.ProjectedAssets,
                Is.Empty,
                "Hero.png lives above the opened folder and must not leak into its search");
        }

        [Test]
        public void TagPane_DeletingItsOpenedFolder_ReturnsItToTheListAndKeepsThePaneOpen()
        {
            var tagPane = _state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            Assert.That(tagPane.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            _source.RemovePath("Assets/Art/UI/Icon.png");
            _source.Folders.Remove(AssetPath.From("Assets/Art/UI"));
            ApplyExternalChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { "Assets/Art/UI/Icon.png", "Assets/Art/UI" },
                Array.Empty<string>(),
                Array.Empty<string>()));
            _controller.Refresh();

            Assert.That(_state.IsLivePane(tagPane), Is.True);
            Assert.That(tagPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(tagPane.TagBrowseRootPath.IsEmpty, Is.True);
            Assert.That(tagPane.SelectedTagFilterId, Is.EqualTo("blue"));
        }

        [Test]
        public void TagPane_MovingItsOpenedFolder_FollowsTheMove()
        {
            var tagPane = _state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            Assert.That(tagPane.TrySetFolderSelection("Assets/Art"), Is.True);
            Assert.That(tagPane.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            _gateway.MovePath("Assets/Art", "Assets/Illustration");
            _controller.HandleAssetChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "Assets/Illustration" },
                new[] { "Assets/Art" }));
            _controller.Refresh();

            Assert.That(_state.IsLivePane(tagPane), Is.True);
            Assert.That(tagPane.Source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(tagPane.TagBrowseRootPath, Is.EqualTo(AssetPath.From("Assets/Illustration")));
            Assert.That(tagPane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Illustration/UI")));
        }

        [Test]
        public void TagDeletion_ClosesTheTagAuxiliaryEvenWhileItBrowsesAFolder()
        {
            var tagPane = _state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            Assert.That(tagPane.TrySetFolderSelection("Assets/Art"), Is.True);

            Assert.That(_tagDefinitions.TryDeleteTag("blue", out var error), Is.True, error);
            _controller.Refresh();

            Assert.That(_state.IsLivePane(tagPane), Is.False);
        }

        [Test]
        public void RestoringATagPane_KeepsALiveOpenedFolderAndDropsADeadOne()
        {
            var tagSource = ZepPaneSource.FixedTag("blue");

            Assert.That(
                _controller.TryReconcilePaneSource(
                    tagSource,
                    AssetPath.From("Assets/Art"),
                    AssetPath.From("Assets/Art/UI"),
                    out var liveSource,
                    out var liveRoot,
                    out var liveCurrent),
                Is.True);
            Assert.That(liveSource, Is.EqualTo(tagSource));
            Assert.That(liveRoot, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(liveCurrent, Is.EqualTo(AssetPath.From("Assets/Art/UI")));

            Assert.That(
                _controller.TryReconcilePaneSource(
                    tagSource,
                    AssetPath.From("Assets/Gone"),
                    AssetPath.From("Assets/Gone/Deeper"),
                    out var listSource,
                    out var listRoot,
                    out _),
                Is.True,
                "a dead opened folder costs the folder, not the pane");
            Assert.That(listSource, Is.EqualTo(tagSource));
            Assert.That(listRoot.IsEmpty, Is.True);
        }

        [Test]
        public void ExternalAssetAndFolderSelection_DoNotNavigatePrimary()
        {
            var artPane = OpenArtPane();
            artPane.TrySetFolderSelection("Assets/Art/UI");
            _state.ActivatePane(artPane);

            _controller.HandleSelectionPaths(new[] { "Assets/Art/Hero.png" });

            Assert.That(_state.ActivePane, Is.SameAs(_state.PrimaryPane));
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")),
                "ordinary Unity selection must not force the panel into the selected asset's folder");
            Assert.That(_state.PrimaryPane.SelectedAssetPaths, Is.Empty,
                "an asset outside the current projection must not leave an invisible panel selection");
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));

            _state.ActivatePane(artPane);
            _controller.HandleSelectionPaths(new[] { "Assets/Art" });

            Assert.That(_state.ActivePane, Is.SameAs(_state.PrimaryPane));
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")),
                "selecting a Unity folder must not enter that folder in the panel");
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
        }

        [Test]
        public void ExplicitReveal_ActivatesAndTargetsPrimary()
        {
            var artPane = OpenArtPane();
            artPane.TrySetFolderSelection("Assets/Art/UI");
            _state.ActivatePane(artPane);

            Assert.That(
                _controller.RevealAndFrameAsset("Assets/Art/Hero.png"),
                Is.True);
            Assert.That(_state.ActivePane, Is.SameAs(_state.PrimaryPane));
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
        }

        [Test]
        public void PanelSelectionAndReveal_KeepTheirCapturedAuxiliaryPane()
        {
            var artPane = OpenArtPane();
            _state.ActivatePane(artPane);
            artPane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));

            _controller.ExpectPanelSelectionChange(
                artPane,
                new[] { "Assets/Art/Hero.png" });
            _controller.HandleSelectionPaths(new[] { "Assets/Art/Hero.png" });

            Assert.That(_state.ActivePane, Is.SameAs(artPane));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPaths,
                Is.Empty);

            Assert.That(
                _controller.RevealAndFrameAsset(
                    artPane,
                    "Assets/Art/Hero.png"),
                Is.True);
            Assert.That(_state.ActivePane, Is.SameAs(artPane));
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPaths,
                Is.Empty);
        }

        [Test]
        public void RevealUnderTagFilter_ClearsTheFilterAndSelectsTheTargetInItsParent()
        {
            _state.PrimaryPane.ApplyTagView("blue");
            Assert.That(
                _state.PrimaryPane.ActiveMode,
                Is.EqualTo(AssetsPaneMode.Tags));

            Assert.That(
                _controller.RevealAndFrameAsset("Assets/Audio/Theme.wav"),
                Is.True);

            Assert.That(_state.PrimaryPane.SelectedTagFilterId, Is.Empty);
            Assert.That(
                _state.PrimaryPane.ActiveMode,
                Is.EqualTo(AssetsPaneMode.Browser));
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
        }

        [Test]
        public void RevealDuringALiveSearch_EndsTheSearchSoTheTargetSurvivesReconciliation()
        {
            // The reveal contract is "the target is really in view". A live
            // search left set keeps projecting its own results, reconciliation
            // prunes the selection the reveal just made, and the returned miss
            // sends the ping receiver into a Refresh-retry that can never
            // converge — the search, not the index, is what hides the target.
            _state.PrimaryPane.SetSearchText("hero");

            Assert.That(
                _controller.RevealAndFrameAsset("Assets/Audio/Theme.wav"),
                Is.True);

            Assert.That(_state.PrimaryPane.SearchText, Is.Empty);
            Assert.That(
                _state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
        }

        [TestCase("Assets/Audio/Theme.wav", "Assets/Audio")]
        [TestCase("Assets/Art/UI", "Assets/Art")]
        public void RevealUnderANonmatchingTypeFilter_SelectsTheKnownTargetInItsParent(
            string targetPath,
            string parentPath)
        {
            var pane = _state.PrimaryPane;
            pane.SetSelectedTypeFilter("Texture2D");

            Assert.That(_controller.RevealAndFrameAsset(targetPath), Is.True);

            Assert.That(pane.SelectedTypeFilter, Is.Empty);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From(parentPath)));
            Assert.That(pane.SelectedAssetPath, Is.EqualTo(AssetPath.From(targetPath)));
            Assert.That(pane.ProjectedAssets.Select(asset => asset.Path), Has.Member(targetPath));
        }

        [Test]
        public void RevealOfAMissingTarget_LeavesFiltersSelectionAndActivePaneUnchanged()
        {
            var artPane = OpenArtPane();
            var primary = _state.PrimaryPane;
            primary.ApplyTagView("blue");
            primary.SetSelectedTypeFilter("Texture2D");
            primary.SetSearchText("hero");
            primary.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));
            _state.ActivatePane(artPane);
            var previousFolder = primary.CurrentFolderPath;
            var previousSelectedFolder = primary.SelectedFolderPath;
            var previousProjection = primary.ProjectedAssets;
            var frames = 0;
            _controller.AssetFrameRequested += (_, _) => frames++;

            Assert.That(_controller.RevealAndFrameAsset("Assets/Audio/Missing.wav"), Is.False);

            Assert.That(primary.SelectedTypeFilter, Is.EqualTo("Texture2D"));
            Assert.That(primary.SelectedTagFilterId, Is.EqualTo("blue"));
            Assert.That(primary.SearchText, Is.EqualTo("hero"));
            Assert.That(primary.SelectedAssetPath, Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));
            Assert.That(primary.CurrentFolderPath, Is.EqualTo(previousFolder));
            Assert.That(primary.SelectedFolderPath, Is.EqualTo(previousSelectedFolder));
            Assert.That(primary.ProjectedAssets, Is.SameAs(previousProjection));
            Assert.That(_state.ActivePane, Is.SameAs(artPane));
            Assert.That(frames, Is.Zero);
        }

        [TestCase("Assets/Art/Hero.png", true, "Opened Hero.png")]
        [TestCase("Assets/Missing.asset", false, "Unable to open Assets/Missing.asset")]
        public void OpenAsset_PublishesResultStatusBeforeReturning(
            string assetPath,
            bool expectedResult,
            string expectedStatus)
        {
            _scheduler.RunAll();
            _controller.ShowStatusMessage("before Open");
            var observedStatuses = new List<string>();
            _state.GlobalChanged += () => observedStatuses.Add(_state.StatusMessage);

            var result = _controller.OpenAsset(AssetPath.From(assetPath));

            Assert.That(result, Is.EqualTo(expectedResult));
            Assert.That(_state.StatusMessage, Is.EqualTo(expectedStatus));
            Assert.That(observedStatuses, Is.Not.Empty);
            Assert.That(observedStatuses, Has.All.EqualTo(expectedStatus),
                "the synchronous notification must carry this action's result");
        }

        [Test]
        public void Reveal_PublishesResultStatusBeforeReturning()
        {
            _scheduler.RunAll();
            _controller.ShowStatusMessage("before Reveal");
            var observedStatuses = new List<string>();
            _state.GlobalChanged += () => observedStatuses.Add(_state.StatusMessage);

            _controller.Reveal(AssetPath.From("Assets/Art/Hero.png"));

            Assert.That(_state.StatusMessage, Is.EqualTo("Revealed Hero.png"));
            Assert.That(observedStatuses, Is.Not.Empty);
            Assert.That(observedStatuses, Has.All.EqualTo("Revealed Hero.png"));
        }

        [Test]
        public void Compress_SameBasenameFiles_PreservesBothPayloadsAndTheirMetadata()
        {
            WriteCompressionFile("Assets/A/Foo.txt", "first payload");
            WriteCompressionFile("Assets/A/Foo.txt.meta", "guid: first");
            WriteCompressionFile("Assets/B/Foo.txt", "second payload");
            WriteCompressionFile("Assets/B/Foo.txt.meta", "guid: second");
            var output = Path.Combine(_compressionWorkspace, "out.zip");

            Assert.That(_controller.Compress(new[]
            {
                AssetPath.From("Assets/A/Foo.txt"),
                AssetPath.From("Assets/B/Foo.txt"),
            }, output, true), Is.True);

            using (var archive = ZipFile.OpenRead(output))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(4));
                Assert.That(ReadCompressionEntry(archive, "Foo.txt"), Is.EqualTo("first payload"));
                Assert.That(ReadCompressionEntry(archive, "Foo.txt.meta"), Is.EqualTo("guid: first"));
                Assert.That(ReadCompressionEntry(archive, "Foo 1.txt"), Is.EqualTo("second payload"));
                Assert.That(ReadCompressionEntry(archive, "Foo 1.txt.meta"), Is.EqualTo("guid: second"));
            }
        }

        [Test]
        public void Compress_SameBasenameFolders_PreservesBothTreesAndTheirMetadata()
        {
            WriteCompressionFile("Assets/A/Group.v1/child.txt", "first child");
            WriteCompressionFile("Assets/A/Group.v1/child.txt.meta", "guid: first child");
            WriteCompressionFile("Assets/A/Group.v1.meta", "guid: first folder");
            WriteCompressionFile("Assets/B/Group.v1/child.txt", "second child");
            WriteCompressionFile("Assets/B/Group.v1/child.txt.meta", "guid: second child");
            WriteCompressionFile("Assets/B/Group.v1.meta", "guid: second folder");
            _source.Folders.Add(AssetPath.From("Assets/A/Group.v1"));
            _source.Folders.Add(AssetPath.From("Assets/B/Group.v1"));
            var output = Path.Combine(_compressionWorkspace, "out.zip");

            Assert.That(_controller.Compress(new[]
            {
                AssetPath.From("Assets/A/Group.v1"),
                AssetPath.From("Assets/B/Group.v1"),
            }, output, true), Is.True);

            using (var archive = ZipFile.OpenRead(output))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(8));
                Assert.That(ReadCompressionEntry(archive, "Group.v1/child.txt"), Is.EqualTo("first child"));
                Assert.That(ReadCompressionEntry(archive, "Group.v1/child.txt.meta"), Is.EqualTo("guid: first child"));
                Assert.That(ReadCompressionEntry(archive, "Group.v1.meta"), Is.EqualTo("guid: first folder"));
                Assert.That(ReadCompressionEntry(archive, "Group.v1 1/child.txt"), Is.EqualTo("second child"));
                Assert.That(ReadCompressionEntry(archive, "Group.v1 1/child.txt.meta"), Is.EqualTo("guid: second child"));
                Assert.That(ReadCompressionEntry(archive, "Group.v1 1.meta"), Is.EqualTo("guid: second folder"));
            }
        }

        [Test]
        public void Compress_CollisionSuffix_DoesNotTakeAnotherSelectionsOriginalName()
        {
            WriteCompressionFile("Assets/A/Foo.txt", "first");
            WriteCompressionFile("Assets/B/Foo.txt", "second");
            WriteCompressionFile("Assets/C/Foo 1.txt", "original suffix");
            WriteCompressionFile("Assets/C/Other.txt", "unrelated");
            var output = Path.Combine(_compressionWorkspace, "out.zip");

            Assert.That(_controller.Compress(new[]
            {
                AssetPath.From("Assets/A/Foo.txt"),
                AssetPath.From("Assets/B/Foo.txt"),
                AssetPath.From("Assets/C/Foo 1.txt"),
                AssetPath.From("Assets/C/Other.txt"),
            }, output, false), Is.True);

            using (var archive = ZipFile.OpenRead(output))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(4));
                Assert.That(ReadCompressionEntry(archive, "Foo.txt"), Is.EqualTo("first"));
                Assert.That(ReadCompressionEntry(archive, "Foo 2.txt"), Is.EqualTo("second"));
                Assert.That(ReadCompressionEntry(archive, "Foo 1.txt"), Is.EqualTo("original suffix"));
                Assert.That(ReadCompressionEntry(archive, "Other.txt"), Is.EqualTo("unrelated"));
            }
        }

        [Test]
        public void Compress_RepeatedSource_KeepsWriterDeduplication()
        {
            WriteCompressionFile("Assets/A/Foo.txt", "one payload");
            var output = Path.Combine(_compressionWorkspace, "out.zip");
            var source = AssetPath.From("Assets/A/Foo.txt");

            Assert.That(_controller.Compress(new[] { source, source }, output, false), Is.True);

            using (var archive = ZipFile.OpenRead(output))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(1));
                Assert.That(ReadCompressionEntry(archive, "Foo.txt"), Is.EqualTo("one payload"));
            }
        }

        private void WriteCompressionFile(string assetPath, string content)
        {
            if (_compressionWorkspace == null)
            {
                _compressionWorkspace = Path.Combine(Path.GetTempPath(), "ZepCompressTests-" + Guid.NewGuid().ToString("N"));
                _gateway.PhysicalProjectRoot = _compressionWorkspace;
            }

            var physicalPath = Path.Combine(_compressionWorkspace, assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(physicalPath));
            File.WriteAllText(physicalPath, content);
        }

        private static string ReadCompressionEntry(ZipArchive archive, string entryName)
        {
            var entry = archive.GetEntry(entryName);
            Assert.That(entry, Is.Not.Null, "missing archive entry: " + entryName);
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        [Test]
        public void CreateFolder_DuringALiveSearch_EndsTheSearchSoTheResultIsRevealed()
        {
            // A create reveals its result in the creation parent. With the
            // search left set the pane kept projecting search results — which
            // do not contain the new folder unless its name happens to match
            // the query — so the reveal's selection was pruned and the just-
            // created item was nowhere on screen.
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetSearchText("hero");

            Assert.That(
                _controller.CreateFolder(pane, AssetPath.From("Assets/Art"), "New Folder"),
                Is.True);

            Assert.That(pane.SearchText, Is.Empty);
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                pane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/New Folder")));
        }

        [Test]
        public void CatalogBatch_OneThousandAdds_AppliesAndPublishesOnceWithoutRebuild()
        {
            const int additionCount = 1000;
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            _scheduler.RunAll();
            var paths = new string[additionCount];
            for (var i = 0; i < additionCount; i++)
            {
                paths[i] = "Assets/Art/Bulk" + i.ToString("D4") + ".asset";
                _source.Records.Add(new AssetRecord(
                    "bulk-guid-" + i,
                    paths[i],
                    "Bulk" + i.ToString("D4"),
                    "ScriptableObject"));
            }

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                _controller.RunCatalogBatch(() =>
                {
                    _controller.HandleAssetChanges(new ProjectAssetChanges(
                        paths,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                    for (var i = 0; i < paths.Length; i++)
                    {
                        Assert.That(
                            _controller.AcknowledgeAssetPath(AssetPath.From(paths[i])),
                        Is.True);
                    }
                });
                _controller.HandleProjectChanged();
                _scheduler.RunAll();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.BulkCatalogApplications),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.ProjectionPublications),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                    Is.Zero);
                Assert.That(
                    _index.Count,
                    Is.EqualTo(additionCount + 5));
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void BulkCallbacks_OnlyImportedDescendantsReachCatalogAndSearch()
        {
            DrainBulkCallbackWork();
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            const string path = "Assets/Art/CallbackOnly.asset";
            _source.Records.Add(new AssetRecord("callback-only-guid", path, "CallbackOnly", "ScriptableObject"));
            var publications = 0;
            _state.PaneChanged += _ => publications++;
            var enumerations = _synchronizer.EnumerationCount;

            _controller.RunCatalogBatch(() =>
            {
                _controller.HandleAssetChanges(BulkImport(path));
                Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
                Assert.That(_index.TryGetByPath(path, out _), Is.False);
                Assert.That(pane.ProjectedAssets.Select(asset => asset.Path), Does.Not.Contain(path));
                Assert.That(publications, Is.Zero);
            });

            Assert.That(_index.TryGetByPath(path, out var record), Is.True);
            Assert.That(record.Guid, Is.EqualTo("callback-only-guid"));
            Assert.That(_searchService.Search("callbackonly").Select(item => item.AssetPath), Is.EqualTo(new[] { path }));
            Assert.That(pane.ProjectedAssets.Select(asset => asset.Path), Does.Contain(path));
            Assert.That(publications, Is.EqualTo(1));
            DrainBulkCallbackWork();
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations), "the resolved callback owns one coarse-event acknowledgement");
        }

        [Test]
        public void BulkCallbacks_EndBatchIncludesCallbackOnlyDescendant()
        {
            DrainBulkCallbackWork();
            const string root = "Assets/Art/EndBatchFolder";
            const string child = root + "/EndBatchChild.asset";
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            var publications = 0;
            _state.PaneChanged += _ => publications++;
            _gateway.EndBatchCallback = () =>
            {
                _source.Records.Add(new AssetRecord("end-batch-child-guid", child, "EndBatchChild", "ScriptableObject"));
                _controller.HandleAssetChanges(BulkImport(root, child));
                Assert.That(_index.TryGetByPath(child, out _), Is.False, "EndBatch is still inside the bulk scope");
            };
            try
            {
                _controller.RunBulk(() => Assert.That(
                    _controller.CreateFolder(_state.PrimaryPane, AssetPath.From("Assets/Art"), "EndBatchFolder"), Is.True));
            }
            finally
            {
                _gateway.EndBatchCallback = null;
            }
            Assert.That(_index.TryGetFolder(root, out _), Is.True);
            Assert.That(_index.TryGetByPath(child, out var record), Is.True);
            Assert.That(record.Guid, Is.EqualTo("end-batch-child-guid"));
            Assert.That(_searchService.Search("endbatchchild").Select(item => item.AssetPath), Is.EqualTo(new[] { child }));
            Assert.That(publications, Is.EqualTo(1));
            Assert.That(_gateway.BeginBatchCalls, Is.EqualTo(1));
            Assert.That(_gateway.EndBatchCalls, Is.EqualTo(1));
            Assert.That(_gateway.OpenBatchDepth, Is.Zero);
        }

        [Test]
        public void BulkCallbacks_DeletesMovesAndDuplicatesRemainConsistent()
        {
            DrainBulkCallbackWork();
            const string oldPath = "Assets/Art/Hero.png";
            const string movedPath = "Assets/Art/MovedHero.png";
            var enumerations = _synchronizer.EnumerationCount;
            var move = new ProjectAssetChanges(Array.Empty<string>(), Array.Empty<string>(), new[] { movedPath }, new[] { oldPath });
            _controller.RunBulk(() =>
            {
                Assert.That(_controller.Rename(_state.PrimaryPane, AssetPath.From(oldPath), "MovedHero"), Is.True);
                var position = _source.Records.FindIndex(record => record.AssetPath == movedPath);
                _source.Records[position] = new AssetRecord("hero-guid", movedPath, "MovedHero", "Texture2D", "Blue", new[] { "callback-label" }, new[] { "Texture" });
                _gateway.SetTags(movedPath, "blue");
                _controller.HandleAssetChanges(move);
                _controller.HandleAssetChanges(move);
            });
            Assert.That(_index.TryGetByPath(oldPath, out _), Is.False);
            Assert.That(_index.TryGetByPath(movedPath, out var moved), Is.True);
            Assert.That(moved.Guid, Is.EqualTo("hero-guid"));
            Assert.That(moved.TypeName, Is.EqualTo("Texture2D"));
            Assert.That(moved.TagSearchText, Does.Contain("Blue"));
            Assert.That(moved.UnityLabels, Is.EqualTo(new[] { "callback-label" }));
            Assert.That(moved.BaseTypeNames, Is.EqualTo(new[] { "Texture" }));
            Assert.That(_searchService.Search("hero").Count(item => item.AssetPath == movedPath), Is.EqualTo(1));
            DrainBulkCallbackWork();
            _controller.HandleProjectChanged();
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations));

            _source.RemovePath(movedPath);
            _controller.RunCatalogBatch(() => _controller.HandleAssetChanges(
                new ProjectAssetChanges(Array.Empty<string>(), new[] { movedPath }, Array.Empty<string>(), Array.Empty<string>())));
            Assert.That(_index.TryGetByPath(movedPath, out _), Is.False);
            Assert.That(_searchService.Search("hero").Select(item => item.AssetPath), Does.Not.Contain(movedPath));
            Assert.That(_searchService.Search("hero").Select(item => item.AssetPath), Does.Not.Contain(oldPath));

            const string transient = "Assets/Art/ArrivalOrder.asset";
            _source.Records.Add(new AssetRecord("arrival-guid", transient, "ArrivalOrder", "ScriptableObject"));
            _controller.RunCatalogBatch(() =>
            {
                _controller.HandleAssetChanges(BulkImport(transient));
                _controller.HandleAssetChanges(new ProjectAssetChanges(Array.Empty<string>(), new[] { transient }, Array.Empty<string>(), Array.Empty<string>()));
            });
            Assert.That(_index.TryGetByPath(transient, out _), Is.False, "the later deletion wins over an earlier import, even while the source can still resolve it");
            Assert.That(_searchService.Search("arrivalorder"), Is.Empty);
            _source.RemovePath(transient);
        }

        [Test]
        public void BulkCallbacks_UnresolvedPathsRequestRecovery()
        {
            DrainBulkCallbackWork();
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            const string first = "Assets/Art/ResolvedCallback.asset";
            const string second = "Assets/Art/OtherResolved.asset";
            const string missing = "Assets/Art/MissingCallback.asset";
            const string moveTarget = "Assets/Art/UnresolvedMove.png";
            _source.Records.Add(new AssetRecord("resolved-callback-guid", first, "ResolvedCallback", "ScriptableObject"));
            _source.Records.Add(new AssetRecord("other-resolved-guid", second, "OtherResolved", "ScriptableObject"));
            var enumerations = _synchronizer.EnumerationCount;
            _controller.RunCatalogBatch(() =>
            {
                _controller.HandleAssetChanges(BulkImport(first));
                _controller.HandleAssetChanges(BulkImport(second, missing));
                _controller.HandleAssetChanges(new ProjectAssetChanges(Array.Empty<string>(), Array.Empty<string>(), new[] { moveTarget }, new[] { "Assets/Art/Hero.png" }));
            });
            Assert.That(_index.TryGetByPath(first, out var resolved), Is.True);
            Assert.That(resolved.Guid, Is.EqualTo("resolved-callback-guid"));
            Assert.That(_index.TryGetByPath(second, out _), Is.True);
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var hero), Is.True);
            Assert.That(hero.Guid, Is.EqualTo("hero-guid"));
            Assert.That(_index.TryGetByPath(moveTarget, out _), Is.False);
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations + 1), "multiple unresolved callbacks coalesce without projectChanged");
            Assert.That(_index.TryGetByPath(first, out _), Is.True);
            Assert.That(_index.TryGetByPath(second, out _), Is.True);
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations + 1), "only the fully resolved first callback was acknowledged");
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations + 2), "the unresolved callback must not consume its coarse event");
        }

        // T5.7. The retype job's patch seam seen from the controller: a delta the index
        // produced on the Bridge side reaches the derived caches and the panes, and costs
        // one projection publication - not a projection-cache rebuild, and not one per
        // live pane (R12's bound).
        //
        // Written in ZepControllerHost.Create's construction order on purpose:
        // the delegate is built BEFORE the controller exists, because the controller's
        // constructor takes the synchronizer and the synchronizer takes this callback.
        // The delta is delivered by INVOKING the delegate, never by calling
        // ApplyIndexPatch directly, so what is driven is the arrangement production uses.
        // What that proves is the observable behaviour - a delegate built before the
        // controller existed reaches the controller's projections. It does NOT prove
        // C# closure semantics: capture-by-value is not expressible at this site, so no
        // test could fail on it. The other half - that Create really passes this delegate
        // - is ledger evidence only (§10 item 13) and is not test-backed anywhere.
        //
        // Deliberately NOT asserted here: FullRecordEnumerations. RebuildProjectionCache
        // increments it once at every completed synchronization, the hydration in this
        // very fixture included, so a zero delta across a controller-level reload is red
        // against a correct implementation (§1.4). AC3 scopes that counter to the
        // synchronizer, and AssetIndexSynchronizerTests is where it is measured.
        [Test]
        public void IndexPatch_UpdatesProjectionsWithoutRebuildingTheCache()
        {
            const string configPath = "Assets/Art/ProjectConfiguration.asset";
            var stalePosition = _source.Records.FindIndex(
                record => record.AssetPath == configPath);
            Assert.That(stalePosition, Is.GreaterThanOrEqualTo(0), "fixture guard");
            // What a reload actually hydrates: the snapshot's pre-compile type, which a
            // compile resolves to the concrete one.
            _source.Records[stalePosition] = new AssetRecord(
                "config-guid",
                configPath,
                "ProjectConfiguration",
                "ScriptableObject");

            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            _synchronizer = new FakeSynchronizer(_source, _index);
            ZepController core = null;
            Action<AssetCatalogDelta> patch = delta => core.ApplyIndexPatch(delta);
            core = new ZepController(
                _state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                _index,
                new AssetSearchService(_index),
                _tagDefinitions,
                _synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            _controller = core;
            _controller.Refresh();
            _scheduler.RunAll();
            DrainBulkCallbackWork();
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            Assert.That(
                TypeNameInBrowseProjection(configPath),
                Is.EqualTo("ScriptableObject"),
                "premise: the hydrated catalog still carries the pre-compile type");

            // Exactly what a slice does: apply to the index, hand the returned delta to
            // the callback. The counters are snapshotted around the delivery ALONE -
            // hydration's own completion legitimately rebuilds the projection cache once.
            var delta = _index.ApplyFileChanges(
                new[]
                {
                    new AssetRecord(
                        "config-guid",
                        configPath,
                        "ProjectConfiguration",
                        "ProjectConfiguration",
                        baseTypeNames: new[] { "ScriptableObject", "Object" }),
                },
                Array.Empty<string>());
            Assert.That(delta.IsEmpty, Is.False,
                "an unchanged record would make the publication unobservable");

            var counts = MeasurePerfCounters(() => patch(delta));

            Assert.That(
                counts.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                Is.Zero,
                "a patch reaches the projections through the incremental path; rebuilding "
                + "the cache per slice is the post-compile stall this job removes");
            Assert.That(
                counts.GetCount(ZepPerfCounter.ProjectionPublications),
                Is.EqualTo(1),
                "R12's bound: one delivered non-empty delta is one publication");
            Assert.That(
                TypeNameInBrowseProjection(configPath),
                Is.EqualTo("ProjectConfiguration"),
                "and the row the panel paints carries the rebuilt type");
        }

        private string TypeNameInBrowseProjection(string assetPath)
        {
            return _controller.BrowseProjection
                .BuildAssetEntries(
                    "Assets/Art",
                    string.Empty,
                    ZepSearchScope.Assets,
                    string.Empty,
                    string.Empty)
                .Single(entry => entry.Path == assetPath)
                .TypeName;
        }

        // T5.9's guard, negative leg. Unity emits projectChanged during the very reload
        // whose types the synchronizer is still patching; starting a second
        // synchronization there cancels the job and costs the next reload a cold scan.
        // The instrument is the fake's own EnumerationCount: FullSynchronizerRuns is
        // counted only inside the real Bridge class, so under a fake it is structurally
        // zero and an assertion on it could not fail.
        [Test]
        public void ProjectChangedDuringTypeReconciliation_DoesNotStartASecondSynchronization()
        {
            _controller.Dispose();
            _controller = null;
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _scheduler = new QueuedScheduler();
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art"),
                    isFolder: true,
                    "Art",
                    "Folder"));
            _synchronizer = new FakeSynchronizer(_source, _index);
            _controller = new ZepController(
                _state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                _index,
                new AssetSearchService(_index),
                _tagDefinitions,
                _synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery(),
                directSource);
            _controller.BeginPanelUse();
            _scheduler.RunAll();
            directSource.ObservedPaths.Clear();
            _synchronizer.HasBackgroundJob = true;
            var enumerations = _synchronizer.EnumerationCount;

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                _controller.HandleProjectChanged();
                DrainBulkCallbackWork();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    _synchronizer.EnumerationCount,
                    Is.EqualTo(enumerations),
                    "the reload's own coarse projectChanged must not restart synchronization "
                    + "while the retype tail is still running");
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                    Is.Zero);
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }

            // The consequence of dropping the request BEFORE it clears
            // _globalReconciliationComplete: ordinary folder navigation still trusts the
            // hydrated index. A flag left false would take HandlePaneChanged's
            // !_globalReconciliationComplete leg and re-observe the folder directly.
            _state.PrimaryPane.TrySetFolderSelection("Assets/Audio");

            Assert.That(
                directSource.ObservedPaths,
                Is.Empty,
                "a dropped request must not leave the panel believing it is unhydrated");
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Is.EqualTo(new[] { "Assets/Audio/Theme.wav" }));
        }

        // T5.9's guard, positive leg - and the step that proves the guard GATES rather
        // than deafens. A guard that never releases passes the first assertion and fails
        // the third, so all three steps are load-bearing.
        [Test]
        public void ProjectChangedAfterTypeReconciliation_StillRequestsReconciliation()
        {
            DrainBulkCallbackWork();
            var enumerations = _synchronizer.EnumerationCount;

            _synchronizer.HasBackgroundJob = false;
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();

            Assert.That(
                _synchronizer.EnumerationCount,
                Is.EqualTo(enumerations + 1),
                "with no retype tail outstanding the coarse signal still reconciles");

            _synchronizer.HasBackgroundJob = true;
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();

            Assert.That(
                _synchronizer.EnumerationCount,
                Is.EqualTo(enumerations + 1),
                "and it is gated again while a new retype tail runs");

            _synchronizer.HasBackgroundJob = false;
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();

            Assert.That(
                _synchronizer.EnumerationCount,
                Is.EqualTo(enumerations + 2),
                "the guard releases every time the tail ends, not once");
        }

        // The controller avoids cancelling an existing job. The synchronizer owns
        // the unresolved obligation and must settle it before retiring the journal.
        [Test]
        public void ConcreteBatchDuringTypeReconciliation_AppliesIncrementallyButDefersEscalation()
        {
            DrainBulkCallbackWork();
            Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            _synchronizer.HasBackgroundJob = true;
            var enumerations = _synchronizer.EnumerationCount;

            const string applied = "Assets/Art/RetypeWindowApplied.asset";
            _source.Records.Add(new AssetRecord(
                "retype-window-applied-guid",
                applied,
                "RetypeWindowApplied",
                "ScriptableObject"));

            _controller.HandleAssetChanges(BulkImport(applied));
            DrainBulkCallbackWork();

            Assert.That(_index.TryGetByPath(applied, out _), Is.True);
            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Does.Contain(applied),
                "a batch the catalog can apply incrementally is applied and published");
            Assert.That(
                _synchronizer.EnumerationCount,
                Is.EqualTo(enumerations),
                "and it needs no synchronization to do it");
            Assert.That(_synchronizer.RecordedUnresolvedPaths, Is.Empty);

            const string missing = "Assets/Art/RetypeWindowMissing.asset";
            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                _controller.HandleAssetChanges(BulkImport(missing));
                DrainBulkCallbackWork();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    _synchronizer.EnumerationCount,
                    Is.EqualTo(enumerations),
                    "a path the catalog cannot resolve escalates, and the escalation is "
                    + "dropped while the retype tail runs - not queued");
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                    Is.Zero);
                Assert.That(_synchronizer.RecordedUnresolvedPaths, Is.EqualTo(new[] { missing }));
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void UnresolvedChanges_PrePanelCallback_ReachesTheScanRecorder()
        {
            _controller.Dispose();
            _controller = new ZepController(_state, _gateway, _fileCommands, _source,
                _index, _searchService, _tagDefinitions, _synchronizer, _scheduler,
                new NoOpShellReveal(), new EmptyAddressablesQuery());
            const string missing = "Assets/Art/PrePanelUnresolved.png";
            _controller.HandleAssetChanges(BulkImport(missing));
            Assert.That(_synchronizer.RecordedUnresolvedPaths, Is.EqualTo(new[] { missing }));
        }

        [Test]
        public void UnresolvedChanges_MixedBulkCallback_RecordsOnlyTheUnresolvedSubset()
        {
            _synchronizer.HasBackgroundJob = true;
            const string applied = "Assets/Art/BulkResolved.png";
            const string missing = "Assets/Art/BulkUnresolved.png";
            _source.Records.Add(new AssetRecord("bulk-resolved-guid", applied, "BulkResolved", "Texture2D"));
            _controller.RunCatalogBatch(() => _controller.HandleAssetChanges(BulkImport(applied, missing)));
            Assert.That(_index.TryGetByPath(applied, out _), Is.True);
            Assert.That(_synchronizer.RecordedUnresolvedPaths, Is.EqualTo(new[] { missing }));
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_RealVerification_PublishesAndSavesTheMixedBatch()
        {
            _controller.Dispose();
            var labelOverride = PersistentAssetIndexCache.UnityLabelSnapshotOverride;
            var typeOverride = PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride;
            var revisionOverride = PersistentAssetIndexCache.ArtifactDependencyVersionOverride;
            var verified = AssetIndexSynchronizer.VerifiedThisProcess;
            var fixedPaths = new[] { PersistentAssetIndexCache.CacheFilePath,
                PersistentAssetIndexCache.DirtyMarkerPath, PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath };
            var paths = fixedPaths.Concat(Directory.Exists(PersistentAssetIndexCache.PendingChangesDirectory)
                ? Directory.GetFiles(PersistentAssetIndexCache.PendingChangesDirectory) : Array.Empty<string>());
            var backup = paths.Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
            var gated = new GatedTagScanSource(_source);
            AssetIndexSynchronizer synchronizer = null;
            try
            {
                PersistentAssetIndexCache.ClearPendingChanges();
                foreach (var path in fixedPaths) if (File.Exists(path)) File.Delete(path);
                PersistentAssetIndexCache.UnityLabelSnapshotOverride = () => new Dictionary<string, IReadOnlyList<string>>();
                PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = () => new Dictionary<string, AssetTypeMetadata>();
                PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 42;
                Assert.That(PersistentAssetIndexCache.Save(_source.Records, _source.GetFolderRecords()), Is.True);
                AssetIndexSynchronizer.VerifiedThisProcess = false;
                _index.Clear();
                synchronizer = new AssetIndexSynchronizer(_source, gated, _index,
                    enablePersistentCache: true, delta => _controller.ApplyIndexPatch(delta));
                _controller = new ZepController(_state, _gateway, _fileCommands, _source,
                    _index, _searchService, _tagDefinitions, synchronizer, _scheduler,
                    new NoOpShellReveal(), new EmptyAddressablesQuery());
                _controller.BeginPanelUse();
                for (var frame = 0; frame < 1000 && !gated.HasEntered; frame++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }
                Assert.That(gated.HasEntered, Is.True);
                Assert.That(synchronizer.HasBackgroundJob, Is.True);
                _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
                _searchService.Search("UnresolvedController");
                var resolved = new AssetRecord("controller-resolved-guid", "Assets/Art/ResolvedController.png", "ResolvedController", "Texture2D");
                var unresolved = new AssetRecord("controller-unresolved-guid", "Assets/Art/UnresolvedController.png", "UnresolvedController", "Texture2D");
                _source.Records.Add(resolved);
                var changes = BulkImport(resolved.AssetPath, unresolved.AssetPath);
                PersistentAssetIndexCache.RecordPendingChanges(changes);
                _controller.HandleAssetChanges(changes);
                Assert.That(_index.TryGetByPath(resolved.AssetPath, out _), Is.True);
                Assert.That(_index.TryGetByPath(unresolved.AssetPath, out _), Is.False);
                _source.Records.Add(unresolved);
                gated.Release();
                for (var frame = 0; frame < 1000 && synchronizer.HasBackgroundJob; frame++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }
                Assert.That(synchronizer.HasBackgroundJob, Is.False);
                Assert.That(_index.TryGetByPath(unresolved.AssetPath, out _), Is.True);
                Assert.That(_searchService.Search("UnresolvedController").Select(record => record.AssetPath), Does.Contain(unresolved.AssetPath));
                Assert.That(_controller.BrowseProjection.BuildAssetEntries("Assets/Art", string.Empty,
                    ZepSearchScope.Assets, string.Empty, string.Empty).Select(entry => entry.Path),
                    Is.SupersetOf(new[] { resolved.AssetPath, unresolved.AssetPath }));
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(record => record.AssetPath), Is.SupersetOf(new[] { resolved.AssetPath, unresolved.AssetPath }));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
            }
            finally
            {
                gated.Release();
                _controller.Dispose();
                gated.Dispose();
                PersistentAssetIndexCache.ClearPendingChanges();
                foreach (var path in fixedPaths) if (File.Exists(path)) File.Delete(path);
                foreach (var file in backup)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Key));
                    File.WriteAllBytes(file.Key, file.Value);
                }
                PersistentAssetIndexCache.UnityLabelSnapshotOverride = labelOverride;
                PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = typeOverride;
                PersistentAssetIndexCache.ArtifactDependencyVersionOverride = revisionOverride;
                AssetIndexSynchronizer.VerifiedThisProcess = verified;
            }
        }

        [Test]
        public void RecoveryAcknowledgement_RefusalReturnsFalse()
        {
            const string path = "Assets/Art/AcknowledgementRefusal.asset";
            _source.Records.Add(new AssetRecord(
                "acknowledgement-refusal-guid", path, "AcknowledgementRefusal", "ScriptableObject"));
            _gateway.SetTags(path, "red");
            var publishedPaths = _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path).ToArray();

            _gateway.ThrowTagVersionUnavailable = true;
            Assert.DoesNotThrow(() =>
                Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(path)), Is.False));

            Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path), Is.EqualTo(publishedPaths));
            Assert.That(_index.TryGetByPath(path, out _), Is.True,
                "incremental catalog application may complete before presentation is refused");
        }

        [Test]
        public void RecoveryAcknowledgement_BlockedRetryAndRefresh()
        {
            const string firstPath = "Assets/Art/AcknowledgementFirst.asset";
            const string secondPath = "Assets/Art/AcknowledgementSecond.asset";
            _source.Records.Add(new AssetRecord(
                "acknowledgement-first-guid", firstPath, "AcknowledgementFirst", "ScriptableObject"));
            _source.Records.Add(new AssetRecord(
                "acknowledgement-second-guid", secondPath, "AcknowledgementSecond", "ScriptableObject"));

            _gateway.ThrowTagVersionUnavailable = true;
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(firstPath)), Is.False);

            _gateway.ThrowTagVersionUnavailable = false;
            _gateway.ResetTagAssignmentReadCalls();
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(firstPath)), Is.False);
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(secondPath)), Is.False);
            Assert.That(_gateway.TagAssignmentReadCalls, Is.Zero,
                "blocked acknowledgements must retain paths without another metadata acquisition");

            _controller.Refresh();
            _scheduler.RunAll();

            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_index.TryGetByPath(firstPath, out _), Is.True);
            Assert.That(_index.TryGetByPath(secondPath, out _), Is.True);
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(secondPath)), Is.True);
        }

        [Test]
        public void RecoveryAcknowledgement_CompatibilityContracts()
        {
            const string path = "Assets/Art/AcknowledgementHealthy.asset";
            const string missingPath = "Assets/Art/AcknowledgementMissing.asset";
            _source.Records.Add(new AssetRecord(
                "acknowledgement-healthy-guid", path, "AcknowledgementHealthy", "ScriptableObject"));

            Assert.That(_controller.AcknowledgeAssetPath(default), Is.False);
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(missingPath)), Is.False);
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(path)), Is.True);
            Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(path)), Is.True);

            _controller.RunCatalogBatch(() =>
                Assert.That(_controller.AcknowledgeAssetPath(AssetPath.From(path)), Is.True));

            const string exceptionalPath = "Assets/Art/AcknowledgementExceptional.asset";
            _source.Records.Add(new AssetRecord(
                "acknowledgement-exceptional-guid", exceptionalPath, "AcknowledgementExceptional", "ScriptableObject"));
            var exception = new InvalidOperationException("unexpected acknowledgement failure");
            _gateway.TagVersionException = exception;
            try
            {
                Assert.That(
                    Assert.Throws<InvalidOperationException>(() =>
                        _controller.AcknowledgeAssetPath(AssetPath.From(exceptionalPath))),
                    Is.SameAs(exception));
            }
            finally
            {
                _gateway.TagVersionException = null;
            }
        }

        [Test]
        public void RecoveryOperation_RefusalPreservesCommittedResult()
        {
            var selectedBefore = _state.PrimaryPane.SelectedAssetPath;
            _gateway.ThrowTagVersionUnavailable = true;

            Assert.That(
                _controller.Rename(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    "Champion"),
                Is.True);

            Assert.That(_gateway.Exists(AssetPath.From("Assets/Art/Champion.png")), Is.True);
            Assert.That(_fileCommands.CanUndo, Is.True);
            Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
            Assert.That(_state.PrimaryPane.SelectedAssetPath, Is.EqualTo(selectedBefore));
        }

        [Test]
        public void RecoveryOperation_ReplayAndBlockedWorkPreserveMappings()
        {
            _gateway.ThrowTagVersionUnavailable = true;
            var rewrites = 0;
            _controller.AssetPathRewritten += (_, _) => rewrites++;

            Assert.That(
                _controller.Rename(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    "Champion"),
                Is.True);
            Assert.That(_fileCommands.CanUndo, Is.True);

            _gateway.ThrowTagVersionUnavailable = false;
            _gateway.ResetTagAssignmentReadCalls();
            Assert.That(
                _controller.Rename(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/Art/Champion.png"),
                    "Legend"),
                Is.True);

            Assert.That(_gateway.Exists(AssetPath.From("Assets/Art/Legend.png")), Is.True);
            Assert.That(_gateway.TagAssignmentReadCalls, Is.Zero,
                "blocked presentation must not acquire metadata for later committed operations");
            Assert.That(rewrites, Is.EqualTo(2));
        }

        [Test]
        public void RecoveryOperation_RefreshRecoversCurrentState()
        {
            _gateway.ThrowTagVersionUnavailable = true;
            Assert.That(
                _controller.Rename(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    "Champion"),
                Is.True);

            _gateway.ThrowTagVersionUnavailable = false;
            _controller.Refresh();
            _scheduler.RunAll();

            Assert.That(_index.TryGetByPath("Assets/Art/Champion.png", out _), Is.True);
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
        }

        [Test]
        public void RecoveryOperation_SuccessAndFailureContracts()
        {
            Assert.That(
                _controller.Rename(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    "Champion"),
                Is.True);
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Champion.png")));

            _gateway.TagVersionException = new InvalidOperationException("unexpected tag failure");
            Assert.Throws<InvalidOperationException>(() => _controller.Undo(_state.PrimaryPane));
            _gateway.TagVersionException = null;

            Assert.That(_controller.Redo(_state.PrimaryPane), Is.True);
        }

        [Test]
        public void RecoveryBulk_RefusalRetainsCommittedWork()
        {
            const string path = "Assets/Art/BulkRefusal.asset";
            _source.Records.Add(new AssetRecord(
                "bulk-refusal-guid", path, "BulkRefusal", "ScriptableObject"));
            _gateway.ThrowTagVersionUnavailable = true;

            Assert.DoesNotThrow(() => _controller.RunCatalogBatch(() =>
                _controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()))));

            Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
            Assert.That(_index.TryGetByPath(path, out _), Is.False);
            Assert.That(_synchronizer.RecordedConcreteChanges, Has.Count.EqualTo(1));
        }

        [Test]
        public void RecoveryBulk_RetiresTransientState()
        {
            const string path = "Assets/Art/BulkTransient.asset";
            _source.Records.Add(new AssetRecord(
                "bulk-transient-guid", path, "BulkTransient", "ScriptableObject"));
            _gateway.ThrowTagVersionUnavailable = true;
            _controller.RunCatalogBatch(() => _controller.HandleAssetChanges(
                new ProjectAssetChanges(
                    new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));

            _gateway.ThrowTagVersionUnavailable = false;
            Assert.DoesNotThrow(() => _controller.RunCatalogBatch(() => { }));
            Assert.That(_index.TryGetByPath(path, out _), Is.False);
            Assert.That(_synchronizer.RecordedConcreteChanges, Has.Count.EqualTo(1));
        }

        [Test]
        public void RecoveryBulk_RefreshRecoversMappings()
        {
            const string path = "Assets/Art/BulkRecovery.asset";
            _source.Records.Add(new AssetRecord(
                "bulk-recovery-guid", path, "BulkRecovery", "ScriptableObject"));
            _gateway.ThrowTagVersionUnavailable = true;
            _controller.RunCatalogBatch(() => _controller.HandleAssetChanges(
                new ProjectAssetChanges(
                    new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));

            _gateway.ThrowTagVersionUnavailable = false;
            _controller.Refresh();
            _scheduler.RunAll();

            Assert.That(_index.TryGetByPath(path, out _), Is.True);
        }

        [Test]
        public void RecoveryBulk_VersionAndSuccessContracts()
        {
            const string path = "Assets/Art/BulkSuccess.asset";
            _source.Records.Add(new AssetRecord(
                "bulk-success-guid", path, "BulkSuccess", "ScriptableObject"));

            Assert.DoesNotThrow(() => _controller.RunCatalogBatch(() =>
                _controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()))));

            Assert.That(_index.TryGetByPath(path, out _), Is.True);
        }

        [Test]
        public void BulkCallbacks_LifecycleAndRecording()
        {
            const string first = "Assets/Art/FirstCallback.asset";
            const string second = "Assets/Art/SecondCallback.asset";
            _source.Records.Add(new AssetRecord("first-callback-guid", first, "FirstCallback", "ScriptableObject"));
            _source.Records.Add(new AssetRecord("second-callback-guid", second, "SecondCallback", "ScriptableObject"));
            var firstChanges = new ProjectAssetChanges(
                new[] { first }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            var secondChanges = new ProjectAssetChanges(
                new[] { second }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

            _controller.RunCatalogBatch(() =>
            {
                _controller.HandleAssetChanges(firstChanges);
                _controller.RunCatalogBatch(() =>
                {
                    _controller.HandleAssetChanges(new ProjectAssetChanges(
                        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
                    _controller.HandleAssetChanges(secondChanges);
                    Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(2),
                        "nonempty callbacks are recorded immediately while catalog application remains deferred");
                });
            });

            Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(2));

            var recordedFirst = _synchronizer.RecordedConcreteChanges[0];
            Assert.That(recordedFirst.Imported, Is.EqualTo(new[] { first }));
            Assert.That(recordedFirst.Deleted, Is.Empty);
            Assert.That(recordedFirst.Moved, Is.Empty);
            Assert.That(recordedFirst.MovedFromPaths, Is.Empty);
            Assert.That(recordedFirst.DidDomainReload, Is.False);

            var recordedSecond = _synchronizer.RecordedConcreteChanges[1];
            Assert.That(recordedSecond.Imported, Is.EqualTo(new[] { second }));
            Assert.That(recordedSecond.Deleted, Is.Empty);
            Assert.That(recordedSecond.Moved, Is.Empty);
            Assert.That(recordedSecond.MovedFromPaths, Is.Empty);
            Assert.That(recordedSecond.DidDomainReload, Is.False);
            Assert.That(_index.TryGetByPath(first, out _), Is.True);
            Assert.That(_index.TryGetByPath(second, out _), Is.True);
        }

        [Test]
        public void BulkCallbacks_NormalizedPayloadsKeepPairingAndReloadWithoutEarlyPublication()
        {
            DrainBulkCallbackWork();
            const string imported = "Assets/Art/Normalized.asset";
            const string moved = "Assets/Art/Zed.png";
            const string otherMoved = "Assets/Art/Alpha.wav";
            _source.Records.Add(new AssetRecord("normalized-guid", imported, "Normalized", "ScriptableObject"));
            _source.MovePath("Assets/Art/Hero.png", moved);
            _source.MovePath("Assets/Audio/Theme.wav", otherMoved);
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            var publications = 0;
            _state.PaneChanged += _ => publications++;
            var enumerations = _synchronizer.EnumerationCount;
            _controller.RunCatalogBatch(() =>
            {
                _controller.RunCatalogBatch(() =>
                {
                    _controller.HandleAssetChanges(new ProjectAssetChanges(
                        new[] { "Assets\\Art\\Normalized.asset", imported, moved, otherMoved },
                        new[] { "Assets\\Gone", "Assets/Gone/Child.asset", "Assets/Gone" },
                        new[] { "Assets\\Art\\Zed.png", "Assets\\Art\\Alpha.wav", moved },
                        new[] { "Assets\\Art\\Hero.png", "Assets\\Audio\\Theme.wav", "Assets/Art/Hero.png" }, true));
                    _controller.HandleAssetChanges(BulkImport("", "   "));
                    Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
                    var recorded = _synchronizer.RecordedConcreteChanges[0];
                    Assert.That(recorded.Imported, Is.EqualTo(new[] { imported }));
                    Assert.That(recorded.Deleted, Is.EqualTo(new[] { "Assets/Gone" }));
                    Assert.That(recorded.Moved, Is.EqualTo(new[] { otherMoved, moved }));
                    Assert.That(recorded.MovedFromPaths, Is.EqualTo(new[] { "Assets/Audio/Theme.wav", "Assets/Art/Hero.png" }));
                    Assert.That(recorded.DidDomainReload, Is.True);
                    Assert.That(_index.TryGetByPath(imported, out _), Is.False);
                    Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.True);
                    Assert.That(publications, Is.Zero);
                });
                Assert.That(_index.TryGetByPath(imported, out _), Is.False);
                Assert.That(publications, Is.Zero);
            });
            Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
            Assert.That(_index.TryGetByPath(imported, out _), Is.True);
            Assert.That(_index.TryGetByPath(moved, out var hero), Is.True);
            Assert.That(hero.Guid, Is.EqualTo("hero-guid"));
            Assert.That(_index.TryGetByPath(otherMoved, out var theme), Is.True);
            Assert.That(theme.Guid, Is.EqualTo("theme-guid"));
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.False);
            Assert.That(_index.TryGetByPath("Assets/Audio/Theme.wav", out _), Is.False);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path), Does.Contain(imported));
            Assert.That(publications, Is.EqualTo(1));
            _controller.RunCatalogBatch(() => { });
            Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
            Assert.That(publications, Is.EqualTo(1));
            DrainBulkCallbackWork();
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations));
            _controller.HandleProjectChanged();
            DrainBulkCallbackWork();
            Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations + 1), "normalized-empty callback did not add an acknowledgement");
        }

        [Test]
        public void BulkCallbacks_DisposalRetainsDeliveredRecordsButDiscardsApplication()
        {
            DrainBulkCallbackWork();
            const string path = "Assets/Art/DisposedBulk.asset";
            _source.Records.Add(new AssetRecord("disposed-bulk-guid", path, "DisposedBulk", "ScriptableObject"));
            var publications = 0;
            _state.PaneChanged += _ => publications++;
            _controller.RunBulk(() =>
            {
                _controller.HandleAssetChanges(BulkImport(path));
                Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
                _controller.Dispose();
            });
            Assert.That(_index.TryGetByPath(path, out _), Is.False);
            Assert.That(publications, Is.Zero);
            Assert.That(_synchronizer.IsDisposed, Is.True);
            Assert.That(_gateway.BeginBatchCalls, Is.EqualTo(_gateway.EndBatchCalls));
            Assert.That(_gateway.OpenBatchDepth, Is.Zero);
            _controller.RunCatalogBatch(() => { });
            DrainBulkCallbackWork();
            Assert.That(_index.TryGetByPath(path, out _), Is.False);
            Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void BulkCallbacks_UserWorkFailureStillCompletesThenPropagatesWithoutReplay()
        {
            DrainBulkCallbackWork();
            const string path = "Assets/Art/WorkFailure.asset";
            _source.Records.Add(new AssetRecord("work-failure-guid", path, "WorkFailure", "ScriptableObject"));
            var failure = new InvalidOperationException("bulk work failed");
            var reads = 0;
            _source.BuildRecordCallback = _ => reads++;
            try
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => _controller.RunBulk(() =>
                {
                    _controller.HandleAssetChanges(BulkImport(path));
                    throw failure;
                })), Is.SameAs(failure));
                Assert.That(_index.TryGetByPath(path, out _), Is.True);
                var completedReads = reads;
                _controller.RunBulk(() => { });
                Assert.That(reads, Is.EqualTo(completedReads));
                Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(1));
                Assert.That(_gateway.BeginBatchCalls, Is.EqualTo(_gateway.EndBatchCalls));
                Assert.That(_gateway.OpenBatchDepth, Is.Zero);
            }
            finally
            {
                _source.BuildRecordCallback = null;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BulkCallbacks_RecorderFailurePropagatesAtReceiptAndCannotReplay(bool typed)
        {
            DrainBulkCallbackWork();
            const string path = "Assets/Art/RecorderFailure.asset";
            _source.Records.Add(new AssetRecord("recorder-failure-guid", path, "RecorderFailure", "ScriptableObject"));
            Exception failure = typed
                ? (Exception)new ProjectTagStateUnavailableException("recorder unavailable")
                : new InvalidOperationException("recorder failed");
            var attempts = 0;
            var reads = 0;
            _source.BuildRecordCallback = _ => reads++;
            _synchronizer.RecordConcreteChangesCallback = _ => { attempts++; throw failure; };
            try
            {
                var observedAtReceipt = false;
                Assert.That(Assert.Catch(() => _controller.RunBulk(() =>
                {
                    try
                    {
                        _controller.HandleAssetChanges(BulkImport(path));
                    }
                    catch (Exception exception)
                    {
                        Assert.That(exception, Is.SameAs(failure));
                        Assert.That(_index.TryGetByPath(path, out _), Is.False);
                        observedAtReceipt = true;
                        throw;
                    }
                })), Is.SameAs(failure));
                Assert.That(observedAtReceipt, Is.True, "the caller observes recorder failure before bulk completion, and it also escapes RunBulk");
                Assert.That(attempts, Is.EqualTo(1));
                var completedReads = reads;
                _synchronizer.RecordConcreteChangesCallback = null;
                _controller.RunBulk(() => { });
                Assert.That(reads, Is.EqualTo(completedReads));
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(_synchronizer.RecordedConcreteChanges, Is.Empty);
                Assert.That(_state.StatusMessage, Does.Not.Contain("recorder unavailable"));
                Assert.That(_gateway.BeginBatchCalls, Is.EqualTo(_gateway.EndBatchCalls));
                Assert.That(_gateway.OpenBatchDepth, Is.Zero);
            }
            finally
            {
                _source.BuildRecordCallback = null;
                _synchronizer.RecordConcreteChangesCallback = null;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BulkCallbacks_ApplicationFailureKeepsEarlierAcknowledgementAndRetiresRemainder(bool typed)
        {
            DrainBulkCallbackWork();
            const string first = "Assets/Art/BeforeFailure.asset";
            const string failed = "Assets/Art/FailedApplication.asset";
            const string skipped = "Assets/Art/SkippedApplication.asset";
            foreach (var path in new[] { first, failed, skipped })
                _source.Records.Add(new AssetRecord(path, path, Path.GetFileNameWithoutExtension(path), "ScriptableObject"));
            Exception failure = typed
                ? (Exception)new ProjectTagStateUnavailableException("source acquisition unavailable")
                : new InvalidOperationException("source acquisition failed");
            var reads = new List<string>();
            _source.BuildRecordCallback = path =>
            {
                reads.Add(path.Value);
                if (path.Value == failed) throw failure;
            };
            try
            {
                Action work = () => _controller.RunBulk(() =>
                {
                    _controller.HandleAssetChanges(BulkImport(first));
                    _controller.HandleAssetChanges(BulkImport(failed));
                    _controller.HandleAssetChanges(BulkImport(skipped));
                });
                if (typed)
                    Assert.DoesNotThrow(() => work());
                else
                    Assert.That(Assert.Throws<InvalidOperationException>(() => work()), Is.SameAs(failure));
                Assert.That(_index.TryGetByPath(first, out _), Is.True);
                Assert.That(_index.TryGetByPath(failed, out _), Is.False);
                Assert.That(_index.TryGetByPath(skipped, out _), Is.False);
                Assert.That(reads, Does.Not.Contain(skipped));
                var completedReads = reads.Count;
                _source.BuildRecordCallback = path => reads.Add(path.Value);
                _controller.RunBulk(() => { });
                Assert.That(reads.Count, Is.EqualTo(completedReads), "detached callback work cannot replay in the next scope");
                Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(3));
                Assert.That(_gateway.OpenBatchDepth, Is.Zero);
                Assert.That(_gateway.BeginBatchCalls, Is.EqualTo(_gateway.EndBatchCalls));
                DrainBulkCallbackWork();
                var enumerations = _synchronizer.EnumerationCount;
                _controller.HandleProjectChanged();
                DrainBulkCallbackWork();
                Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations), "the earlier successful acknowledgement survives a later failure");
                _controller.HandleProjectChanged();
                DrainBulkCallbackWork();
                Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations + 1), "the failing callback did not acknowledge its coarse event");
                _controller.HandleProjectChanged();
                DrainBulkCallbackWork();
                Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations + 2), "the skipped callback did not acknowledge its coarse event");
            }
            finally
            {
                _source.BuildRecordCallback = null;
            }
        }

        [Test]
        public void BulkCallbacks_TypedPreparationRefusalPreservesCommittedMappingUntilRefresh()
        {
            DrainBulkCallbackWork();
            const string oldPath = "Assets/Art/UI";
            const string movedPath = "Assets/Art/CommittedUI";
            _state.PrimaryPane.TrySetFolderSelection(oldPath);
            var published = _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path).ToArray();
            var enumerations = _synchronizer.EnumerationCount;
            try
            {
                Assert.DoesNotThrow(() => _controller.RunBulk(() =>
                {
                    Assert.That(_controller.Rename(_state.PrimaryPane, AssetPath.From(oldPath), "CommittedUI"), Is.True);
                    _controller.HandleAssetChanges(new ProjectAssetChanges(Array.Empty<string>(), Array.Empty<string>(), new[] { movedPath }, new[] { oldPath }));
                    _gateway.ThrowTagVersionUnavailable = true;
                }));
                Assert.That(_gateway.Exists(AssetPath.From(movedPath)), Is.True);
                Assert.That(_gateway.Exists(AssetPath.From(oldPath)), Is.False);
                Assert.That(_commandHistory.CanUndo, Is.True);
                Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path), Is.EqualTo(published));
                Assert.That(_state.StatusMessage, Does.Contain("unavailable"));
                _gateway.ThrowTagVersionUnavailable = false;
                var reads = 0;
                _source.BuildRecordCallback = _ => reads++;
                _controller.RunCatalogBatch(() => _controller.HandleAssetChanges(BulkImport(movedPath)));
                Assert.That(reads, Is.Zero, "publication-blocked completion does not reacquire callback sources");
                DrainBulkCallbackWork();
                Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(enumerations), "refusal must not trigger an automatic scan or command retry");
                Assert.That(_synchronizer.RecordedConcreteChanges.Count, Is.EqualTo(2));
                _source.BuildRecordCallback = null;
                _controller.Refresh();
                DrainBulkCallbackWork();
                Assert.That(_index.TryGetFolder(movedPath, out _), Is.True);
                Assert.That(_index.TryGetFolder(oldPath, out _), Is.False);
                Assert.That(_state.PrimaryPane.CurrentFolderPath, Is.EqualTo(AssetPath.From(movedPath)), "Refresh consumes the retained navigation mapping");
                Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path), Does.Contain(movedPath + "/Icon.png"));
                Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
                Assert.That(_gateway.BeginBatchCalls, Is.EqualTo(_gateway.EndBatchCalls));
                Assert.That(_gateway.OpenBatchDepth, Is.Zero);
                var recoveredEnumerations = _synchronizer.EnumerationCount;
                _controller.HandleProjectChanged();
                DrainBulkCallbackWork();
                Assert.That(_synchronizer.EnumerationCount, Is.EqualTo(recoveredEnumerations + 1), "refused and blocked callbacks did not acknowledge a coarse event");
            }
            finally
            {
                _source.BuildRecordCallback = null;
                _gateway.ThrowTagVersionUnavailable = false;
            }
        }

        private static ProjectAssetChanges BulkImport(params string[] paths)
            => new ProjectAssetChanges(paths, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        private void DrainBulkCallbackWork()
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (_scheduler.PendingCount > 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                _scheduler.RunUpTo(1);
                System.Threading.Thread.Sleep(1);
            }
            Assert.That(_scheduler.PendingCount, Is.Zero, "bulk callback deferred work must settle within the bounded drain");
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void ExternalCallback_OneThousandAdds_AppliesWithoutAuthoritativeRebuild()
        {
            const int additionCount = 1000;
            _scheduler.RunAll();
            var paths = new string[additionCount];
            for (var i = 0; i < additionCount; i++)
            {
                paths[i] = "Assets/Art/External" + i.ToString("D4") + ".asset";
                _source.Records.Add(new AssetRecord(
                    "external-guid-" + i,
                    paths[i],
                    "External" + i.ToString("D4"),
                    "ScriptableObject"));
            }

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                _controller.HandleAssetChanges(new ProjectAssetChanges(
                    paths,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                _scheduler.RunAll();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                    Is.Zero);
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.FullRecordEnumerations),
                    Is.Zero);
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.FullSynchronizerRuns),
                    Is.Zero);
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.ProjectionPublications),
                    Is.EqualTo(1));
                Assert.That(_index.Count, Is.EqualTo(additionCount + 5));
                Assert.That(
                    _index.TryGetByPath(paths[additionCount - 1], out var lastRecord),
                    Is.True);
                Assert.That(lastRecord.Guid, Is.EqualTo("external-guid-999"));
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void RevealOfAnUnindexedPath_StillReportsFailureForTheReceiverRetry()
        {
            _state.PrimaryPane.ApplyTagView("blue");

            Assert.That(
                _controller.RevealAndFrameAsset("Assets/Art/JustCreated.png"),
                Is.False,
                "an asset the index has not seen yet must stay a retryable miss");
            Assert.That(
                _state.PrimaryPane.SelectedAssetPaths,
                Is.Empty);
        }

        [Test]
        public void RevealOnATagSourcedPane_IsDeclinedBecauseItHasNoBrowserProjection()
        {
            var tagPane = _state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;

            Assert.That(
                _controller.RevealAndFrameAsset(tagPane, "Assets/Audio/Theme.wav"),
                Is.False);
            Assert.That(tagPane.SelectedTagFilterId, Is.EqualTo("blue"));
            Assert.That(tagPane.SelectedAssetPaths, Is.Empty);
        }

        [Test]
        public void RenameAndDeleteNeighbor_MutateOnlyCapturedOriginatingPane()
        {
            var artPane = OpenArtPane();
            _state.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Audio/Theme.wav"));

            Assert.That(
                _controller.Rename(
                    artPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    "HeroRenamed"),
                Is.True);
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/HeroRenamed.png")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));

            artPane.VisibleAssetsProvider = () => new[]
            {
                new ZepAssetEntry(
                    "HeroRenamed",
                    "Assets/Art/HeroRenamed.png",
                    "Texture2D"),
                new ZepAssetEntry(
                    "UI",
                    "Assets/Art/UI",
                    "Folder")
            };
            _controller.DeleteAssetsSelectingNeighbor(
                artPane,
                new[] { AssetPath.From("Assets/Art/HeroRenamed.png") });

            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
        }

        [Test]
        public void Rename_OfAnInlineExpandedFolder_CarriesTheExpansionAndLeavesNoStaleKey()
        {
            var pane = _state.PrimaryPane;
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);

            Assert.That(
                _controller.Rename(pane, AssetPath.From("Assets/Art/UI"), "Icons"),
                Is.True);

            Assert.That(
                pane.IsAssetFolderExpanded(AssetPath.From("Assets/Art/Icons")),
                Is.True,
                "the renamed folder must stay open — inline expansion is keyed by path");
            Assert.That(
                pane.IsAssetFolderExpanded(AssetPath.From("Assets/Art/UI")),
                Is.False,
                "a folder recreated at the old path must not inherit the expansion");
        }

        [Test]
        public void CreateFolder_DoesNotDragTheCreationParentsExpansionOntoTheNewFolder()
        {
            // A create reports its PARENT as the previous path, so an unguarded
            // rewrite would move every expansion under Assets/Art onto the new
            // folder instead of leaving them alone.
            var pane = _state.PrimaryPane;
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);

            Assert.That(
                _controller.CreateFolder(pane, AssetPath.From("Assets/Art"), "New Folder"),
                Is.True);

            Assert.That(
                pane.ExpandedAssetFolderPaths,
                Is.EquivalentTo(new[] { AssetPath.From("Assets/Art/UI") }));
        }

        [Test]
        public void DeleteBatch_FolderSelectedWithItsOwnChild_StillDeletesTheRestOfTheSelection()
        {
            _controller.DeleteAssetsSelectingNeighbor(
                _state.PrimaryPane,
                new[]
                {
                    AssetPath.From("Assets/Art/UI"),
                    AssetPath.From("Assets/Art/UI/Icon.png"),
                    AssetPath.From("Assets/Audio/Theme.wav")
                });

            Assert.That(_gateway.Exists(AssetPath.From("Assets/Art/UI")), Is.False);
            Assert.That(_gateway.Exists(AssetPath.From("Assets/Art/UI/Icon.png")), Is.False);
            Assert.That(
                _gateway.Exists(AssetPath.From("Assets/Audio/Theme.wav")),
                Is.False,
                "an item selected after the folder+child pair must still be deleted");
            Assert.That(_state.StatusMessage, Is.EqualTo("Deleted item"));
        }

        // Was DeleteBatch_RealFailureOnAnUnnestedItem_StopsTheBatchWithThatItemsError.
        // T3.5 (audit 5.5) reverses that rule: one item failing must not abandon
        // the rest of the selection, and the reason must still reach the user.
        [Test]
        public void DeleteBatch_RealFailureOnAnUnnestedItem_DeletesTheRestAndKeepsTheReason()
        {
            // "Assets/Ghost.png" was deleted out of band, so it has no identity
            // left and genuinely fails. It sorts before the deeper
            // "Assets/Audio/Theme.wav", so Theme.wav surviving would prove the
            // loop broke on the first failure.
            _controller.DeleteAssetsSelectingNeighbor(
                _state.PrimaryPane,
                new[]
                {
                    AssetPath.From("Assets/Ghost.png"),
                    AssetPath.From("Assets/Audio/Theme.wav")
                });

            Assert.That(
                _gateway.Exists(AssetPath.From("Assets/Audio/Theme.wav")),
                Is.False,
                "the item after the failure must still be deleted");
            Assert.That(
                _state.StatusMessage,
                Is.EqualTo(
                    "Deleted 1 of 2 — 1 failed; first: "
                    + "The original item can no longer be identified safely."));
        }

        // Seeds two sibling folders holding the same file name, plus one file
        // with nothing in its way and one folder collision — the shape a drop
        // of a mixed selection onto a folder produces.
        private void SeedMoveCollisionFixture()
        {
            _source.Folders.Add(AssetPath.From("Assets/A"));
            _source.Folders.Add(AssetPath.From("Assets/B"));
            _source.Folders.Add(AssetPath.From("Assets/A/Shared"));
            _source.Folders.Add(AssetPath.From("Assets/B/Shared"));
            _source.Records.Add(new AssetRecord("a-foo-guid", "Assets/A/Foo.mat", "Foo", "Material"));
            _source.Records.Add(new AssetRecord("b-foo-guid", "Assets/B/Foo.mat", "Foo", "Material"));
            _source.Records.Add(new AssetRecord("a-bar-guid", "Assets/A/Bar.mat", "Bar", "Material"));
            _controller.Refresh();
        }

        // The reported defect: the drop reached AssetDatabase.MoveAsset, failed
        // there, and showed Unity's raw error for 1.9s — which the status bar's
        // identical-message dedupe then swallowed on every repeat, so the drop
        // looked like it did nothing at all.
        [Test]
        public void Move_OntoAFolderHoldingTheSameName_IsRefusedInThePanelsOwnWords()
        {
            SeedMoveCollisionFixture();

            Assert.That(
                _controller.Move(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/B")),
                Is.False);
            // The controller's own status, not the published one: a refused
            // operation defers its publication to the refresh scheduler, which
            // this fixture queues rather than runs.
            Assert.That(
                _controller.StatusMessage,
                Is.EqualTo("'Foo.mat' already exists in Assets/B."));
            Assert.That(
                _gateway.Exists(AssetPath.From("Assets/A/Foo.mat")),
                Is.True,
                "the source stays where it is");
            Assert.That(_fileCommands.CanUndo, Is.False, "and nothing is recorded");
        }

        [Test]
        public void FindMoveCollisions_ReturnsOnlyTheSourcesTheDestinationAlreadyHolds()
        {
            SeedMoveCollisionFixture();

            var collisions = _controller.FindMoveCollisions(
                new[]
                {
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/A/Bar.mat"),
                    AssetPath.From("Assets/A/Shared"),
                    AssetPath.From("Assets/B/Foo.mat")
                },
                AssetPath.From("Assets/B"));

            Assert.That(
                collisions.Select(path => path.Value).ToArray(),
                Is.EqualTo(new[] { "Assets/A/Foo.mat", "Assets/A/Shared" }),
                "a free name is no collision, a folder name is, and an item"
                + " already in the destination is not in its own way");
        }

        [Test]
        public void MoveWithConflictResolution_KeepBoth_LandsBesideTheOccupant_AndUndoesToTheOriginalPath()
        {
            SeedMoveCollisionFixture();

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.KeepBoth),
                Is.True,
                _state.StatusMessage);

            Assert.That(_gateway.Exists(AssetPath.From("Assets/B/Foo 1.mat")), Is.True);
            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo 1.mat")),
                Is.EqualTo("a-foo-guid"),
                "the moved item is the one that was dropped");
            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")),
                Is.EqualTo("b-foo-guid"),
                "the item that was there is untouched");
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.False);

            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True, _state.StatusMessage);

            Assert.That(
                _gateway.Exists(AssetPath.From("Assets/A/Foo.mat")),
                Is.True,
                "one undo puts it back where it came from, not at the name it was given");
            Assert.That(_gateway.Exists(AssetPath.From("Assets/B/Foo 1.mat")), Is.False);
            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")),
                Is.EqualTo("b-foo-guid"));
        }

        [Test]
        public void MoveWithConflictResolution_KeepBoth_NamesAFolderWithoutAnExtension()
        {
            SeedMoveCollisionFixture();

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Shared"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.KeepBoth),
                Is.True,
                _state.StatusMessage);

            Assert.That(_gateway.IsFolder(AssetPath.From("Assets/B/Shared 1")), Is.True);
            Assert.That(
                _gateway.IsFolder(AssetPath.From("Assets/B/Shared")),
                Is.True,
                "the folder that was there is untouched");
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Shared")), Is.False);
        }

        [Test]
        public void MoveWithConflictResolution_Replace_StoresTheOccupantForUndo_AsOneUndoStep()
        {
            SeedMoveCollisionFixture();
            var notifications = 0;
            var notifiedCommandCount = 0;
            Action<ProjectCommandRecord, int> onExecuted = (_, count) =>
            {
                notifications++;
                notifiedCommandCount = count;
            };
            _commandHistory.Executed += onExecuted;
            try
            {
                Assert.That(
                    _controller.MoveWithConflictResolution(
                        _state.PrimaryPane,
                        AssetPath.From("Assets/A/Foo.mat"),
                        AssetPath.From("Assets/B"),
                        ProjectMoveConflictResolution.Replace),
                    Is.True,
                    _state.StatusMessage);
            }
            finally
            {
                _commandHistory.Executed -= onExecuted;
            }

            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")),
                Is.EqualTo("a-foo-guid"),
                "the dropped item takes the destination");
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.False);
            Assert.That(
                _gateway.UndoPayloadOrigins.Values.ToArray(),
                Is.EqualTo(new[] { "Assets/B/Foo.mat" }),
                "the displaced item remains available for undo");
            Assert.That(notifications, Is.EqualTo(1), "one native undo step");
            Assert.That(notifiedCommandCount, Is.EqualTo(2), "standing for both commands");

            // What that one native step drives: the group replayed back to front.
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True, _state.StatusMessage);
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True, _state.StatusMessage);

            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/A/Foo.mat")),
                Is.EqualTo("a-foo-guid"),
                "undoing the swap returns the dropped item");
            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")),
                Is.EqualTo("b-foo-guid"),
                "and restores the displaced item");
            Assert.That(_gateway.UndoPayloadOrigins, Is.Empty);
        }

        [Test]
        public void MoveWithConflictResolution_Replace_WhenTheMoveFails_ReturnsTheOccupantAndKeepsTheSource()
        {
            SeedMoveCollisionFixture();
            _gateway.ReadOnlyPaths.Add("Assets/A/Foo.mat");
            var notifications = 0;
            Action<ProjectCommandRecord, int> onExecuted = (_, __) => notifications++;
            _commandHistory.Executed += onExecuted;
            try
            {
                Assert.That(
                    _controller.MoveWithConflictResolution(
                        _state.PrimaryPane,
                        AssetPath.From("Assets/A/Foo.mat"),
                        AssetPath.From("Assets/B"),
                        ProjectMoveConflictResolution.Replace),
                    Is.False);
            }
            finally
            {
                _commandHistory.Executed -= onExecuted;
            }

            // The displacement was executed and undone inside the same execution
            // group, so the group stands for nothing. Notifying the Bridge would
            // register a native undo step for a record that is no longer on the
            // stack, and the next Cmd+Z would replay whatever sits beneath it.
            Assert.That(notifications, Is.EqualTo(0), "no native undo step for a swap that did not happen");
            Assert.That(
                _fileCommands.CanRedo,
                Is.False,
                "and no redo entry that would delete the occupant again on its own");

            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")),
                Is.EqualTo("b-foo-guid"),
                "the displaced item is back where it was");
            Assert.That(_gateway.UndoPayloadOrigins, Is.Empty, "the rollback consumes the stored payload");
            Assert.That(
                _gateway.Exists(AssetPath.From("Assets/A/Foo.mat")),
                Is.True,
                "the source never moved");
            Assert.That(
                _state.StatusMessage,
                Is.EqualTo("Items can only be moved within Assets."),
                "the reason the move failed is what the user is shown, not"
                + " the rollback's own success message");
            Assert.That(
                _fileCommands.CanUndo,
                Is.False,
                "the displacement was taken off the undo stack by being undone");
        }

        [Test]
        public void MoveWithConflictResolution_Replace_WhenMoveAndRollbackFail_ReportsTheSavedPayloadAndBothReasons()
        {
            SeedMoveCollisionFixture();
            _gateway.ReadOnlyPaths.Add("Assets/A/Foo.mat");
            _gateway.RestoreUndoPayloadFailureMessage = "The saved item could not be restored.";

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.Replace),
                Is.False);

            var savedItem = _gateway.UndoPayloadOrigins.Single();
            Assert.That(savedItem.Value, Is.EqualTo("Assets/B/Foo.mat"));
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.True);
            Assert.That(_gateway.Exists(AssetPath.From("Assets/B/Foo.mat")), Is.False);
            Assert.That(_fileCommands.CanUndo, Is.False);
            Assert.That(_fileCommands.CanRedo, Is.False);
            Assert.That(_state.StatusMessage,
                Does.Contain("Assets/B/Foo.mat")
                    .And.Contain("Items can only be moved within Assets.")
                    .And.Contain("The saved item could not be restored.")
                    .And.Contain(savedItem.Key)
                    .And.Contain("sibling .meta")
                    .And.Contain("manual recovery"));
        }

        // The move-copy bound: once every
        // "<stem> N" up to the ceiling is taken, Keep Both must say so and
        // change nothing, not move the item under a name the user never saw.
        [Test]
        public void MoveWithConflictResolution_KeepBoth_WithEveryCopyNameTaken_FailsAndMovesNothing()
        {
            SeedMoveCollisionFixture();
            for (var suffix = 1; suffix <= 1000; suffix++)
            {
                _source.Records.Add(new AssetRecord(
                    "b-foo-" + suffix,
                    "Assets/B/Foo " + suffix + ".mat",
                    "Foo " + suffix,
                    "Material"));
            }

            _controller.Refresh();

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.KeepBoth),
                Is.False);

            Assert.That(
                _controller.StatusMessage,
                Is.EqualTo("No free name is available for 'Foo.mat' in Assets/B."));
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.True, "the source stays");
            Assert.That(_gateway.Exists(AssetPath.From("Assets/B/Foo 1001.mat")), Is.False);
            Assert.That(_fileCommands.CanUndo, Is.False, "and nothing is recorded");
        }

        [Test]
        public void MoveWithConflictResolution_Replace_WithAReadOnlyOccupant_RefusesBeforeMovingAnything()
        {
            SeedMoveCollisionFixture();
            _gateway.ReadOnlyPaths.Add("Assets/B/Foo.mat");

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.Replace),
                Is.False);

            Assert.That(_controller.StatusMessage, Is.EqualTo("This item is read-only."));
            Assert.That(_gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")), Is.EqualTo("b-foo-guid"));
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.True);
            Assert.That(_gateway.UndoPayloadOrigins, Is.Empty);
            Assert.That(_fileCommands.CanUndo, Is.False);
        }

        [Test]
        public void MoveWithConflictResolution_Skip_RecordsNothingAndChangesNothing()
        {
            SeedMoveCollisionFixture();
            Assert.That(_fileCommands.CanUndo, Is.False);

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Foo.mat"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.Skip),
                Is.True,
                "declining one item of a drop is not a failure");

            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.True);
            Assert.That(
                _gateway.GetGuid(AssetPath.From("Assets/B/Foo.mat")),
                Is.EqualTo("b-foo-guid"));
            Assert.That(_gateway.UndoPayloadOrigins, Is.Empty);
            Assert.That(_fileCommands.CanUndo, Is.False, "and records no command");
        }

        // The resolution only applies to an actual collision, so a caller can
        // drive a whole drop — colliding items and free ones alike — through
        // this one entry point.
        [Test]
        public void MoveWithConflictResolution_WithNothingInTheWay_JustMoves()
        {
            SeedMoveCollisionFixture();

            Assert.That(
                _controller.MoveWithConflictResolution(
                    _state.PrimaryPane,
                    AssetPath.From("Assets/A/Bar.mat"),
                    AssetPath.From("Assets/B"),
                    ProjectMoveConflictResolution.Skip),
                Is.True,
                _state.StatusMessage);

            Assert.That(_gateway.Exists(AssetPath.From("Assets/B/Bar.mat")), Is.True);
            Assert.That(_gateway.Exists(AssetPath.From("Assets/A/Bar.mat")), Is.False);
        }

        [Test]
        public void MoveUndoAndRedo_MutateOnlyCapturedOriginatingPane()
        {
            var artPane = OpenArtPane();
            _state.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Audio/Theme.wav"));

            Assert.That(
                _controller.Move(
                    artPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    AssetPath.From("Assets/Art/UI")),
                Is.True);
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI/Hero.png")));

            Assert.That(_controller.Undo(artPane), Is.True);
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));

            Assert.That(_controller.Redo(artPane), Is.True);
            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI/Hero.png")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
        }

        [Test]
        public void DelayedUndoAndRedo_UseThePaneCapturedAtCommandEntry()
        {
            var artPane = OpenArtPane();
            Assert.That(
                _controller.Move(
                    artPane,
                    AssetPath.From("Assets/Art/Hero.png"),
                    AssetPath.From("Assets/Art/UI")),
                Is.True);

            _controller.CaptureUndoRedoPane(artPane);
            _state.ActivatePane(_state.PrimaryPane);

            Assert.That(_controller.Undo(), Is.True);
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/Hero.png")));
            Assert.That(_state.ActivePane, Is.SameAs(_state.PrimaryPane));

            _controller.CaptureUndoRedoPane(artPane);
            Assert.That(_controller.Redo(), Is.True);
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI/Hero.png")));
            Assert.That(_state.ActivePane, Is.SameAs(_state.PrimaryPane));
        }

        [Test]
        public void CreateAndUndo_KeepPostOperationNavigationOnCapturedPane()
        {
            var artPane = OpenArtPane();
            _state.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Audio/Theme.wav"));

            Assert.That(
                _controller.CreateFolder(
                    artPane,
                    AssetPath.From("Assets/Art"),
                    "New Folder"),
                Is.True);
            Assert.That(
                artPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/New Folder")));
            Assert.That(_controller.Undo(artPane), Is.True);

            Assert.That(
                artPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                _state.PrimaryPane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Audio/Theme.wav")));
        }

        private void ImportRecord(string guid, string assetPath, string name)
        {
            _source.Records.Add(new AssetRecord(guid, assetPath, name, "Texture2D"));
            var parent = assetPath.Substring(0, assetPath.LastIndexOf('/'));
            if (!_source.ContainsFolder(parent))
            {
                _source.Folders.Add(AssetPath.From(parent));
            }

            ApplyExternalChanges(new ProjectAssetChanges(
                new[] { assetPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        // The incremental path coalesces its view-model publication onto the
        // scheduler, so nothing is projected until the queue drains.
        private void ApplyExternalChanges(ProjectAssetChanges changes)
        {
            _controller.HandleAssetChanges(changes);
            _scheduler.RunAll();
        }

        private void DeleteFromPrimaryPane(string assetPath)
        {
            Assert.That(
                _controller.Delete(_state.PrimaryPane, AssetPath.From(assetPath)),
                Is.True,
                _controller.StatusMessage);
        }

        // Capturing physical freshness must not rebuild the payload listing.

        // -------------------------------------------------------------------
        // List-view folder expansion vs selection reconciliation. A selected
        // child of an expanded folder is really on screen, so ApplyProjection
        // must keep it; everywhere else the strict membership rule stands.
        // -------------------------------------------------------------------

        [Test]
        public void ExpandedFolderChildSelection_SurvivesReconciliation()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);
            var child = AssetPath.From("Assets/Art/UI/Icon.png");

            // The Selection raise runs the controller's reconciliation
            // synchronously; without the expansion predicate this prunes.
            pane.SetSelectedAssetPath(child);
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { child }));

            _controller.Refresh();
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { child }));
        }

        // Expansion visibility now resolves the child through the browse
        // bucket's own sorted lookup instead of scanning every sibling. That
        // lookup has to stay case-insensitive: the scan it replaced was
        // OrdinalIgnoreCase, and the generic fallback inside FindAssetPathIndex
        // is Ordinal, so a bucket that stopped exposing IAssetPathLookup would
        // silently start pruning selections that differ only in case.
        [Test]
        public void ExpandedFolderChildSelection_WithCaseDifferingPath_SurvivesReconciliation()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);

            pane.SetSelectedAssetPath(AssetPath.From("assets/art/ui/icon.PNG"));

            Assert.That(pane.SelectedAssetPaths.Count, Is.EqualTo(1));
            _controller.Refresh();
            Assert.That(pane.SelectedAssetPaths.Count, Is.EqualTo(1));
        }

        [Test]
        public void FlatExpansion_WithThousandsOfSelectedChildren_KeepsThemAllThroughReconciliation()
        {
            const int childCount = 2000;
            var expectedPaths = new List<AssetPath>(childCount);
            for (var i = 0; i < childCount; i++)
            {
                var childPath = "Assets/Art/UI/Bulk" + i.ToString("D4") + ".png";
                _source.Records.Add(new AssetRecord(
                    "bulk-guid-" + i,
                    childPath,
                    "Bulk" + i.ToString("D4"),
                    "Texture2D"));
                expectedPaths.Add(AssetPath.From(childPath));
            }

            _controller.Refresh();
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);

            pane.SetSelectedAssetPaths(expectedPaths);
            _controller.Refresh();

            Assert.That(pane.SelectedAssetPaths, Is.EquivalentTo(expectedPaths));
        }

        [Test]
        public void ChildSelection_WithoutExpansion_IsPrunedByReconciliation()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");

            pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            Assert.That(pane.SelectedAssetPaths, Is.Empty,
                "The strict membership rule must still prune paths that are not on screen.");
        }

        [Test]
        public void ExpandedFolderChildSelection_IsPrunedOnceAFilterActivates()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);
            pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            // Filtered projections render flat (no expansions), so the child
            // row is no longer on screen. "theme" matches nothing under UI.
            pane.SetSearchText("theme");

            Assert.That(pane.SelectedAssetPaths, Is.Empty);
        }

        [Test]
        public void ExpandedFolderChildSelection_IsPrunedWhenTheAssetIsDeleted()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);
            pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            _source.RemovePath("Assets/Art/UI/Icon.png");
            _controller.Refresh();

            Assert.That(pane.SelectedAssetPaths, Is.Empty,
                "Expansion visibility must re-check membership so deleted assets fall out.");
        }

        [Test]
        public void CollapsingTheFolder_MovesChildSelectionToTheFolderThroughReconciliation()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Art");
            var folder = AssetPath.From("Assets/Art/UI");
            pane.SetAssetFolderExpanded(folder, true);
            pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            pane.SetAssetFolderExpanded(folder, false);

            // The pane state reselects the collapsed folder; reconciliation
            // must keep it (it is a direct child of the current folder).
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { folder }));
        }

        [Test]
        public void NestedExpansion_KeepsADeepChildOnlyWhileEveryAncestorIsExpanded()
        {
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets");
            var art = AssetPath.From("Assets/Art");
            var ui = AssetPath.From("Assets/Art/UI");
            var deepChild = AssetPath.From("Assets/Art/UI/Icon.png");
            pane.SetAssetFolderExpanded(art, true);
            pane.SetAssetFolderExpanded(ui, true);

            pane.SetSelectedAssetPath(deepChild);
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { deepChild }));

            // Collapsing the middle ancestor hides the chain below it. The
            // pane state reselects the collapsed folder itself.
            pane.SetAssetFolderExpanded(ui, false);
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { ui }));
        }

        [Test]
        public void MultiItemDrop_WithOneFailure_ReportsTheBatchInsteadOfTheLastItemsStatus()
        {
            // The move loop continues past a failure on purpose, but each item's
            // own status overwrote the previous one — a partly failed drop ended
            // up showing "Moved item" and the user never learned anything failed.
            // "Assets/Art/Ghost.png" is not in the index, so it fails identity
            // validation, and it sorts first among the three.
            var dropOps = new AssetDropOperations(_controller, _state, null, null);

            dropOps.HandleSidebarFolderDrop(
                _state.PrimaryPane,
                "Assets",
                new[]
                {
                    "Assets/Art/Hero.png",
                    "Assets/Art/Ghost.png",
                    "Assets/Audio/Theme.wav"
                });

            Assert.That(_gateway.Exists(AssetPath.From("Assets/Hero.png")), Is.True);
            Assert.That(_gateway.Exists(AssetPath.From("Assets/Theme.wav")), Is.True);
            Assert.That(
                _state.StatusMessage,
                Is.EqualTo(
                    "Moved 2 of 3 — 1 failed; first: The original item can no longer be identified safely."));
        }

        [Test]
        public void SingleItemDropFailure_KeepsItsOwnReasonUnaggregated()
        {
            var dropOps = new AssetDropOperations(_controller, _state, null, null);

            dropOps.HandleSidebarFolderDrop(
                _state.PrimaryPane,
                "Assets",
                new[] { "Assets/Art/Ghost.png" });

            Assert.That(
                _state.StatusMessage,
                Is.EqualTo("The original item can no longer be identified safely."));
        }

        [Test]
        public void SidebarExternalImport_UnderATagFilter_StillCopiesTheFileIn()
        {
            var dropOps = new AssetDropOperations(_controller, _state, null, null);
            _state.PrimaryPane.ApplyTagView("blue");
            Assert.That(
                _state.PrimaryPane.ActiveMode,
                Is.EqualTo(AssetsPaneMode.Tags));

            var sourceFullPath = Path.Combine(
                Path.GetTempPath(),
                "ZEP_SidebarImport_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(sourceFullPath, "dropped");
            var importedAssetPath = "Assets/" + Path.GetFileName(sourceFullPath);
            try
            {
                dropOps.HandleSidebarExternalImport(
                    _state.PrimaryPane,
                    "Assets",
                    new[] { sourceFullPath });

                Assert.That(
                    File.Exists(importedAssetPath),
                    Is.True,
                    "an external file dropped on a sidebar folder must import while a tag filter is active");
            }
            finally
            {
                AssetDatabase.DeleteAsset(importedAssetPath);
                File.Delete(sourceFullPath);
            }
        }

        // The drag-over highlight and the drop's own move filter are documented
        // mirrors of one no-op rule set, written twice. This runs one case
        // matrix through both, so a rule that changes in one place and not the
        // other stops being silent: a folder that lights up must be a folder
        // the drop would actually move something into.
        private static IEnumerable<TestCaseData> MoveNoOpMatrix()
        {
            yield return new TestCaseData(ParitySource, new[] { ParitySource }, false)
                .SetName("{m}_FolderOntoItself");
            yield return new TestCaseData(ParitySource, new[] { ParityLeaf }, false)
                .SetName("{m}_ItemOntoItsOwnParent");
            yield return new TestCaseData(ParityNested, new[] { ParitySource }, false)
                .SetName("{m}_FolderIntoItsOwnSubtree");
            yield return new TestCaseData(ParitySource, new[] { ParityLeaf, ParityForeignLeaf }, true)
                .SetName("{m}_MixedSetWhereOnlyOneItemMoves");
            yield return new TestCaseData(ParityDestination, new[] { ParitySource, ParityLeaf }, true)
                .SetName("{m}_FolderTogetherWithOneOfItsChildren");
            yield return new TestCaseData(ParityDestination, new[] { ParityLeaf, ParityLeaf }, true)
                .SetName("{m}_TheSamePathTwice");
            yield return new TestCaseData(ParityDestination, new[] { ParityLeaf }, true)
                .SetName("{m}_ForeignFolder");
            yield return new TestCaseData(ParitySibling, new[] { ParitySource }, true)
                .SetName("{m}_SiblingWhoseNameSharesAPrefix");
        }

        [TestCaseSource(nameof(MoveNoOpMatrix))]
        public void HighlightAndMoveFilterAgreeOnEveryNoOpRule(
            string destinationFolderPath,
            string[] draggedPaths,
            bool expectedToMoveSomething)
        {
            // The filter asks AssetDatabase whether a dragged path is a folder,
            // so the folders in the matrix have to be real ones.
            using var folders = new ParityFolders();
            var dropOps = new FolderMoveDrop(
                _controller,
                _state,
                AssetDatabase.IsValidFolder,
                showError: null,
                askMoveConflict: (_, __) => ProjectMoveConflictResolution.KeepBoth);

            var movable = dropOps.FilterMovablePaths(draggedPaths, destinationFolderPath);
            var highlights = FolderMoveDrop.IsMeaningfulMoveTarget(destinationFolderPath, draggedPaths);

            Assert.That(movable.Count > 0, Is.EqualTo(expectedToMoveSomething),
                "the drop's own filter disagrees with the case");
            Assert.That(highlights, Is.EqualTo(expectedToMoveSomething),
                "the drag-over highlight must promise exactly what the drop would do");
        }

        [Test]
        public void HighlightIgnoresTheReadOnlyGateTheMoveFilterApplies()
        {
            // The one deliberate difference between the two: the highlight has
            // no CanWrite gate, so a read-only source still lights the folder up
            // while the drop itself declines to move it. Pinned here so the
            // parity matrix above can be read as "and nothing else differs".
            using var folders = new ParityFolders();
            var dropOps = new FolderMoveDrop(
                _controller,
                _state,
                AssetDatabase.IsValidFolder,
                showError: null,
                askMoveConflict: (_, __) => ProjectMoveConflictResolution.KeepBoth);
            var readOnlySource = new[] { "Packages/com.example.readonly/Art/Hero.png" };

            Assert.That(dropOps.FilterMovablePaths(readOnlySource, ParityDestination), Is.Empty);
            Assert.That(
                FolderMoveDrop.IsMeaningfulMoveTarget(ParityDestination, readOnlySource),
                Is.True);
        }

        // -------------------------------------------------------------------
        // T3.8 (audit 4.7 / 4.8): one view-model publication per operation
        // result and per bulk batch.
        // -------------------------------------------------------------------

        [Test]
        public void SingleRename_PublishesOnePaneChangeDispatch()
        {
            _state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            _scheduler.RunAll();

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                Assert.That(
                    _controller.Rename(
                        _state.PrimaryPane,
                        AssetPath.From("Assets/Art/Hero.png"),
                        "Champion"),
                    Is.True);

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.PaneChangedDispatches),
                    Is.EqualTo(1),
                    "reconcile, the expansion-key rewrite, the reveal and the "
                    + "closing projection must coalesce into a single dispatch");
                Assert.That(
                    _state.PrimaryPane.SelectedAssetPath,
                    Is.EqualTo(AssetPath.From("Assets/Art/Champion.png")),
                    "a single operation still reveals and selects its result");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void BulkMoveOfFiftyItems_PublishesOnePaneChange_AndLeavesTheSelectionToTheCaller()
        {
            const int itemCount = 50;
            _source.Folders.Add(AssetPath.From("Assets/BulkSource"));
            _source.Folders.Add(AssetPath.From("Assets/BulkTarget"));
            var sourcePaths = new string[itemCount];
            var movedPaths = new AssetPath[itemCount];
            for (var i = 0; i < itemCount; i++)
            {
                var name = "Item" + i.ToString("D2");
                sourcePaths[i] = "Assets/BulkSource/" + name + ".asset";
                movedPaths[i] = AssetPath.From("Assets/BulkTarget/" + name + ".asset");
                _source.Records.Add(new AssetRecord(
                    "bulk-move-guid-" + i,
                    sourcePaths[i],
                    name,
                    "ScriptableObject"));
            }

            _controller.Refresh();
            _scheduler.RunAll();
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/BulkSource");
            pane.SetSelectedAssetPaths(
                sourcePaths.Select(AssetPath.From).ToArray());
            _scheduler.RunAll();
            Assert.That(pane.SelectedAssetPaths.Count, Is.EqualTo(itemCount));

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                _controller.RunBulk(() =>
                {
                    for (var i = 0; i < sourcePaths.Length; i++)
                    {
                        Assert.That(
                            _controller.Move(
                                pane,
                                AssetPath.From(sourcePaths[i]),
                                AssetPath.From("Assets/BulkTarget")),
                            Is.True);
                    }
                });

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.PaneChangedDispatches),
                    Is.EqualTo(1),
                    "a 50-item bulk must publish once, at CompleteBulkOperation, "
                    + "not once per item");
                Assert.That(
                    pane.CurrentFolderPath,
                    Is.EqualTo(AssetPath.From("Assets/BulkSource")),
                    "the per-item reveal must not navigate the pane to each "
                    + "item's destination in turn");
                Assert.That(
                    pane.SelectedAssetPaths,
                    Is.Empty,
                    "the moved paths are gone from this folder, so reconciliation "
                    + "empties the selection — no single item may be left standing "
                    + "in for the whole batch");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }

            // The batch caller owns the post-batch selection: this is what a
            // real bulk caller does once, after the batch.
            pane.TrySetFolderSelection("Assets/BulkTarget");
            pane.SetSelectedAssetPaths(movedPaths);
            _scheduler.RunAll();

            Assert.That(
                pane.SelectedAssetPaths.Select(path => path.Value),
                Is.EquivalentTo(movedPaths.Select(path => path.Value)),
                "all fifty moved items survive reconciliation at their new home");
        }

        // -------------------------------------------------------------------
        // T3.5 (audit §3.9(b) / 5.5): bulk loops continue and aggregate.
        // -------------------------------------------------------------------

        [Test]
        public void BulkDeleteWithOneReadOnlyItem_DeletesTheRestAndReportsTheAggregate()
        {
            const int itemCount = 5;
            _source.Folders.Add(AssetPath.From("Assets/BulkDelete"));
            var paths = new AssetPath[itemCount];
            for (var i = 0; i < itemCount; i++)
            {
                var name = "Doomed" + i.ToString("D2");
                paths[i] = AssetPath.From("Assets/BulkDelete/" + name + ".asset");
                _source.Records.Add(new AssetRecord(
                    "bulk-delete-guid-" + i,
                    paths[i].Value,
                    name,
                    "ScriptableObject"));
            }

            // The SECOND item refuses, so a break would leave three untouched
            // items behind and a count of one — the red state.
            _gateway.ReadOnlyPaths.Add(paths[1].Value);
            _controller.Refresh();
            _scheduler.RunAll();
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/BulkDelete");
            _scheduler.RunAll();

            _controller.DeleteAssetsSelectingNeighbor(pane, paths);
            _scheduler.RunAll();

            Assert.That(
                _gateway.UndoPayloadOrigins.Values,
                Is.EquivalentTo(new[]
                {
                    paths[0].Value,
                    paths[2].Value,
                    paths[3].Value,
                    paths[4].Value
                }),
                "every deletable item must be deleted, not just the ones before "
                + "the first refusal");
            Assert.That(
                _state.StatusMessage,
                Does.StartWith("Deleted 4 of 5"),
                "the batch reports itself once instead of showing whichever "
                + "per-item message happened to come last");
            Assert.That(
                _state.StatusMessage,
                Does.Contain("read-only"),
                "the aggregate names the first failure's reason");
        }

        [Test]
        public void BulkDelete_RunsInsideExactlyOneGatewayBatch()
        {
            var paths = SeedBatchDeleteFixture("Assets/BatchDelete", 3);
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/BatchDelete");
            _scheduler.RunAll();

            _controller.DeleteAssetsSelectingNeighbor(pane, paths);
            _scheduler.RunAll();

            Assert.That(
                _gateway.UndoPayloadOrigins.Count,
                Is.EqualTo(3),
                "the batch must still delete every item");
            Assert.That(
                _gateway.BeginBatchCalls,
                Is.EqualTo(1),
                "three deletes must open one batch, not one batch per item");
            Assert.That(
                _gateway.EndBatchCalls,
                Is.EqualTo(1),
                "the batch must be closed exactly once, or the payload refresh is "
                + "never issued and the importer stays paused");
            Assert.That(
                _gateway.OpenBatchDepth,
                Is.Zero,
                "begin and end must balance");
        }

        [Test]
        public void NestedRunBulk_StillOpensExactlyOneGatewayBatch()
        {
            var paths = SeedBatchDeleteFixture("Assets/NestedBatch", 3);
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/NestedBatch");
            _scheduler.RunAll();

            // DeleteAssetsSelectingNeighbor opens its own RunBulk, so a caller
            // that already wrapped the work (a drop handler, a context action)
            // used to stack a second StartAssetEditing pair inside the first.
            _controller.RunBulk(() =>
                _controller.DeleteAssetsSelectingNeighbor(pane, paths));
            _scheduler.RunAll();

            Assert.That(
                _gateway.UndoPayloadOrigins.Count,
                Is.EqualTo(3),
                "nesting must not change what the batch does");
            Assert.That(
                _gateway.BeginBatchCalls,
                Is.EqualTo(1),
                "the inner bulk must join the open batch instead of opening one");
            Assert.That(
                _gateway.MaxOpenBatchDepth,
                Is.EqualTo(1),
                "two batches must never be open at once: an inner EndBatch would "
                + "release the held payload refresh in the middle of the outer batch");
            Assert.That(
                _gateway.OpenBatchDepth,
                Is.Zero,
                "begin and end must balance");
        }

        private AssetPath[] SeedBatchDeleteFixture(string folderPath, int itemCount)
        {
            _source.Folders.Add(AssetPath.From(folderPath));
            var paths = new AssetPath[itemCount];
            for (var i = 0; i < itemCount; i++)
            {
                var name = "Batched" + i.ToString("D2");
                paths[i] = AssetPath.From(folderPath + "/" + name + ".asset");
                _source.Records.Add(new AssetRecord(
                    "batch-delete-guid-" + i,
                    paths[i].Value,
                    name,
                    "ScriptableObject"));
            }

            _controller.Refresh();
            _scheduler.RunAll();
            return paths;
        }

        // -------------------------------------------------------------------
        // T3.18 (audit 4.34): the small-selection string compare must reconcile
        // exactly as the hash-set path did.
        // -------------------------------------------------------------------

        [Test]
        public void SelectionReconciliation_MatchesAcrossTheSmallAndLargeSelectionPaths()
        {
            const int itemCount = 20;
            _source.Folders.Add(AssetPath.From("Assets/Reconcile"));
            var live = new List<AssetPath>();
            for (var i = 0; i < itemCount; i++)
            {
                var name = "Row" + i.ToString("D2");
                var path = "Assets/Reconcile/" + name + ".asset";
                live.Add(AssetPath.From(path));
                _source.Records.Add(new AssetRecord(
                    "reconcile-guid-" + i,
                    path,
                    name,
                    "ScriptableObject"));
            }

            _controller.Refresh();
            _scheduler.RunAll();
            var pane = _state.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Reconcile");
            _scheduler.RunAll();

            var ghost = AssetPath.From("Assets/Reconcile/Ghost.asset");

            // Small path (<= 8 wanted), deliberately UNSORTED and holding a path
            // that is not in the projection: the ghost is pruned, the rest keep
            // the caller's order — exactly what the HashSet form did.
            pane.SetSelectedAssetPaths(new[] { live[7], ghost, live[3], live[11] });
            _scheduler.RunAll();
            Assert.That(
                pane.SelectedAssetPaths.Select(path => path.Value),
                Is.EqualTo(new[] { live[7].Value, live[3].Value, live[11].Value }),
                "a small selection keeps the caller's order and drops only the "
                + "path that is not on screen");

            // Large path (> 8 wanted) still runs the set branch.
            var large = new List<AssetPath>(live.GetRange(0, 12)) { ghost };
            pane.SetSelectedAssetPaths(large);
            _scheduler.RunAll();
            Assert.That(
                pane.SelectedAssetPaths.Select(path => path.Value),
                Is.EqualTo(live.GetRange(0, 12).Select(path => path.Value)),
                "a large selection prunes exactly the same way");

            // Case-insensitivity is the property the string compare had to keep.
            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("assets/reconcile/row05.asset")
            });
            _scheduler.RunAll();
            Assert.That(
                pane.SelectedAssetPaths.Count,
                Is.EqualTo(1),
                "paths compare OrdinalIgnoreCase on both branches");
        }

        // -------------------------------------------------------------------
        // T3.21 (audit 4.37): the controller answers a compaction request with
        // a worker build instead of a synchronous rebuild.
        // -------------------------------------------------------------------

        [Test]
        public void SearchCompactionRequest_IsServicedByTheDeferredWorker()
        {
            _controller.Refresh();
            _scheduler.RunAll();
            // Build the search index so ApplyCatalogDelta takes the incremental
            // path at all.
            _state.PrimaryPane.SetSearchText("hero");
            _scheduler.RunAll();
            Assert.That(_searchService.HasBuiltIndex, Is.True);

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                // Retire well past the 25% compaction threshold by re-upserting
                // every record under a new name: each upsert tombstones the
                // ordinal the previous one held.
                for (var round = 0; round < 3; round++)
                {
                    var suffix = "_r" + round;
                    var refreshed = new List<AssetRecord>(_index.Enumerate())
                        .Select(record => new AssetRecord(
                            record.Guid,
                            record.AssetPath,
                            record.Name + suffix,
                            record.TypeName))
                        .ToArray();
                    _searchService.ApplyCatalogDelta(
                        _index.ApplyFileChanges(refreshed, Array.Empty<string>()));
                }

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.SearchFullBuilds),
                    Is.Zero,
                    "compaction must not rebuild the whole gram index inside the "
                    + "import callback");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }

            Assert.That(
                _searchService.TombstoneCount,
                Is.GreaterThan(0),
                "the fixture must actually produce tombstones");
            Assert.That(
                _searchService.IsCompactionRequested,
                Is.True,
                "crossing the threshold records a request instead of rebuilding");

            // Any user action publishes a projection, which is where the
            // controller notices the request and hands it to the worker.
            _controller.ShowStatusMessage("compaction probe");
            _scheduler.RunAll();

            Assert.That(
                _searchService.TombstoneCount,
                Is.Zero,
                "the adopted worker build carries only live ordinals");
            Assert.That(_searchService.IsCompactionRequested, Is.False);
        }

        // NOT a headless test: ContextMenuActions.DeleteTargets goes through
        // EditorUtility.DisplayDialog, and Unity batchmode CANCELS every dialog
        // ("Canceling DisplayDialog: Delete Items" in the run log), so the
        // sidebar branch cannot be driven from an EditMode test at all. The
        // sidebar half of T3.5 is verified in-editor; see the plan's §7 entry.
        // What IS provable headlessly — that a bulk delete loop continues past a
        // refusal and reports itself once — is covered by
        // BulkDeleteWithOneReadOnlyItem_DeletesTheRestAndReportsTheAggregate and
        // DeleteBatch_RealFailureOnAnUnnestedItem_DeletesTheRestAndKeepsTheReason.

        // ---- 4.5: hydration completion rebuilds only when the catalog moved ----

        [Test]
        public void BackgroundReconciliation_ThatFindsNothingNew_RebuildsNoProjectionCache()
        {
            _controller.InitializeIndex();
            _scheduler.RunAll();
            _synchronizer.LeavesCatalogUnchanged = true;

            var counts = MeasurePerfCounters(() =>
            {
                _controller.HandleProjectChanged();
                _scheduler.RunAll();
            });

            Assert.That(
                _synchronizer.EnumerationCount,
                Is.GreaterThan(0),
                "precondition: the reconciliation really ran");
            Assert.That(
                counts.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                Is.Zero,
                "a pass that confirmed the catalog has nothing to rebuild from");
            Assert.That(
                counts.GetCount(ZepPerfCounter.FullRecordEnumerations),
                Is.Zero);
            Assert.That(
                _controller.BrowseProjection.BuildAssetEntries(
                        "Assets/Art",
                        string.Empty,
                        ZepSearchScope.Assets,
                        string.Empty,
                        string.Empty)
                    .Select(entry => entry.Path),
                Has.Member("Assets/Art/Hero.png"),
                "the projection that was kept must still be the right one");
        }

        [Test]
        public void BackgroundReconciliation_ThatChangedTheCatalog_StillRebuilds()
        {
            _controller.InitializeIndex();
            _scheduler.RunAll();
            _source.Records.Add(new AssetRecord(
                "late-guid",
                "Assets/Art/Late.png",
                "Late",
                "Texture2D"));

            var counts = MeasurePerfCounters(() =>
            {
                _controller.HandleProjectChanged();
                _scheduler.RunAll();
            });

            Assert.That(
                counts.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                Is.EqualTo(1));
            Assert.That(
                _controller.BrowseProjection.BuildAssetEntries(
                        "Assets/Art",
                        string.Empty,
                        ZepSearchScope.Assets,
                        string.Empty,
                        string.Empty)
                    .Select(entry => entry.Path),
                Has.Member("Assets/Art/Late.png"));
        }

        [Test]
        public void AssignmentUi_AddUsesPreparedAssignments()
        {
            var file = AssetPath.From("Assets/Audio/Theme.wav");
            var folder = AssetPath.From("Assets/Art");
            AssertAssignmentUiMetadata(file.Value);
            AssertAssignmentUiMetadata(folder.Value);
            var version = _gateway.TagVersion;
            _gateway.StageExternalTags(file.Value, "red");
            _gateway.StageExternalTags(folder.Value, "red");
            Assert.That(_gateway.TagVersion, Is.EqualTo(version));
            Assert.That(_gateway.GetTagIds(file), Is.Empty);
            Assert.That(_controller.AddTag(new[] { file, folder }, "blue"), Is.True);
            AssertAssignmentUiMetadata(file.Value, "red", "blue");
            AssertAssignmentUiMetadata(folder.Value, "red", "blue");
            Assert.That(_controller.GetFolderTintColorForPath(folder.Value), Is.EqualTo(ZepTagColor.Blue));
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
            AssertAssignmentUiMetadata(file.Value, "red");
            AssertAssignmentUiMetadata(folder.Value, "red");
            Assert.That(_controller.Redo(_state.PrimaryPane), Is.True);
            AssertAssignmentUiMetadata(file.Value, "red", "blue");
            AssertAssignmentUiMetadata(folder.Value, "red", "blue");
        }

        [Test]
        public void AssignmentUi_RemoveAndClearUsePreparedAssignments()
        {
            var file = AssetPath.From("Assets/Audio/Theme.wav");
            var folder = AssetPath.From("Assets/Art");
            AssertAssignmentUiMetadata(file.Value);
            AssertAssignmentUiMetadata(folder.Value);
            _gateway.StageExternalTags(file.Value, "red", "blue");
            _gateway.StageExternalTags(folder.Value, "red", "blue");
            Assert.That(_controller.RemoveTag(new[] { file, folder }, "red"), Is.True);
            AssertAssignmentUiMetadata(file.Value, "blue");
            AssertAssignmentUiMetadata(folder.Value, "blue");
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
            AssertAssignmentUiMetadata(file.Value, "red", "blue");
            AssertAssignmentUiMetadata(folder.Value, "red", "blue");

            // A separately projected empty baseline makes clearing depend on preparation too.
            Assert.That(_controller.ClearTags(new[] { file, folder }), Is.True);
            AssertAssignmentUiMetadata(file.Value);
            AssertAssignmentUiMetadata(folder.Value);
            _gateway.StageExternalTags(file.Value, "red");
            _gateway.StageExternalTags(folder.Value, "blue", "red");
            Assert.That(_controller.ClearTags(new[] { file, folder }), Is.True);
            AssertAssignmentUiMetadata(file.Value);
            AssertAssignmentUiMetadata(folder.Value);
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
            AssertAssignmentUiMetadata(file.Value, "red");
            AssertAssignmentUiMetadata(folder.Value, "blue", "red");
        }

        [Test]
        public void AssignmentUi_PreparationFailureRefusesBeforeNoOp()
        {
            var file = AssetPath.From("Assets/Audio/Theme.wav");
            var initialVersion = _gateway.TagVersion;
            var recordVersion = _index.RecordVersion;
            var before = _index.Enumerate().Select(record => record.TagSearchText).ToArray();
            _gateway.StageExternalTags(file.Value, "red");
            _gateway.TagAssignmentPreparationFailureMessage = "Tag assignments are unavailable.";
            try
            {
                foreach (Func<bool> action in new Func<bool>[]
                {
                    () => _controller.AddTag(new[] { file }, "red"),
                    () => _controller.RemoveTag(new[] { file }, "red"),
                    () => _controller.ClearTags(new[] { file }),
                })
                {
                    Assert.That(action(), Is.False);
                    Assert.That(_state.StatusMessage, Is.EqualTo("Tag assignments are unavailable."));
                    Assert.That(_gateway.GetTagIds(file), Is.Empty);
                    Assert.That(_gateway.TagVersion, Is.EqualTo(initialVersion));
                    Assert.That(_index.RecordVersion, Is.EqualTo(recordVersion));
                    Assert.That(_index.Enumerate().Select(record => record.TagSearchText), Is.EqualTo(before));
                    Assert.That(_fileCommands.CanUndo, Is.False);
                    Assert.That(_fileCommands.CanRedo, Is.False);
                }
                _gateway.TagAssignmentPreparationFailureMessage = null;
                var preparations = _gateway.TagAssignmentPreparationCalls;
                _fileCommands.SetExecutionPreparation(() => false);
                Assert.That(_controller.AddTag(new[] { file }, "blue"), Is.False);
                Assert.That(_state.StatusMessage, Is.EqualTo("Wait for Unity to finish the pending Undo or Redo operation."));
                Assert.That(_gateway.TagAssignmentPreparationCalls, Is.EqualTo(preparations));
                Assert.That(_gateway.GetTagIds(file), Is.Empty);
                Assert.That(_gateway.TagVersion, Is.EqualTo(initialVersion));
                Assert.That(_index.RecordVersion, Is.EqualTo(recordVersion));
                Assert.That(_fileCommands.CanUndo, Is.False);
                Assert.That(_fileCommands.CanRedo, Is.False);
            }
            finally
            {
                _fileCommands.SetExecutionPreparation(null);
                _gateway.TagAssignmentPreparationFailureMessage = null;
            }
        }

        [Test]
        public void AssignmentUi_ExternalChangesRemainVisible()
        {
            const string file = "Assets/Audio/Theme.wav";
            const string folder = "Assets/Art";
            const string removed = "Assets/Art/UI/Icon.png";
            var selected = AssetPath.From("Assets/Art/Hero.png");
            _gateway.StageExternalTags(file, "red");
            _gateway.StageExternalTags(folder, "red");
            _gateway.StageExternalTags(removed);
            Assert.That(_controller.AddTag(new[] { selected }, "red"), Is.True);
            for (var projection = 0; projection < 2; projection++)
            {
                var version = _gateway.TagVersion;
                AssertAssignmentUiMetadata(file, "red");
                AssertAssignmentUiMetadata(folder, "red");
                AssertAssignmentUiMetadata(removed);
                AssertAssignmentUiMetadata(selected.Value, "blue", "red");
                _controller.ShowStatusMessage("reproject prepared external state");
                Assert.That(_gateway.TagVersion, Is.EqualTo(version));
            }
            _gateway.StageExternalTags(file, "blue");
            _gateway.StageExternalTags(folder, "blue");
            _gateway.StageExternalTags(removed, "red");
            Assert.That(_controller.AddTag(new[] { selected }, "red"), Is.False);
            Assert.That(_state.StatusMessage, Is.EqualTo("Selection already has that tag."));
            for (var projection = 0; projection < 2; projection++)
            {
                var version = _gateway.TagVersion;
                AssertAssignmentUiMetadata(file, "blue");
                AssertAssignmentUiMetadata(folder, "blue");
                AssertAssignmentUiMetadata(removed, "red");
                AssertAssignmentUiMetadata(selected.Value, "blue", "red");
                _controller.ShowStatusMessage("reproject local no-op");
                Assert.That(_gateway.TagVersion, Is.EqualTo(version));
            }
        }

        [Test]
        public void AssignmentUi_UnchangedPreparationKeepsFastPath()
        {
            var file = AssetPath.From("Assets/Audio/Theme.wav");
            var preparations = _gateway.TagAssignmentPreparationCalls;
            var counts = MeasurePerfCounters(() =>
                Assert.That(_controller.AddTag(new[] { file, file }, "red"), Is.True));
            Assert.That(counts.GetCount(ZepPerfCounter.TagPresentationRebuilds), Is.Zero);
            Assert.That(counts.GetCount(ZepPerfCounter.FullRecordEnumerations), Is.Zero);
            Assert.That(_gateway.TagAssignmentPreparationCalls, Is.EqualTo(preparations + 2),
                "one derivation preparation and the independent command preparation");
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
            Assert.That(_fileCommands.CanUndo, Is.False, "duplicate paths record one command");
            Assert.That(_fileCommands.CanRedo, Is.True);
            preparations = _gateway.TagAssignmentPreparationCalls;
            foreach (var paths in new[]
            {
                Array.Empty<AssetPath>(),
                new[] { AssetPath.From(" "), default(AssetPath) },
                new[] { AssetPath.From("Assets/DoesNotExist.asset"), AssetPath.From("Packages/read-only.asset") },
            })
            {
                Assert.That(_controller.AddTag(paths, "blue"), Is.False);
                Assert.That(_gateway.TagAssignmentPreparationCalls, Is.EqualTo(preparations));
                Assert.That(_fileCommands.CanUndo, Is.False);
                Assert.That(_fileCommands.CanRedo, Is.True);
            }
        }

        private void AssertAssignmentUiMetadata(string path, params string[] expectedIds)
        {
            Assert.That(_gateway.GetTagIds(AssetPath.From(path)), Is.EqualTo(expectedIds), path + " committed assignments");
            var parent = AssetPathFormat.GetParentFolder(path);
            var entry = _controller.BrowseProjection.BuildAssetEntries(parent, string.Empty,
                ZepSearchScope.CurrentFolder, string.Empty, string.Empty).Single(item => item.Path == path);
            Assert.That(entry.Tags.Select(tag => tag.Id), Is.EquivalentTo(expectedIds), path + " chips");
            foreach (var definition in _tagDefinitions.GetOrderedTags())
            {
                var expected = expectedIds.Contains(definition.Id);
                Assert.That(_controller.BrowseProjection.BuildAssetEntries(parent, string.Empty,
                    ZepSearchScope.CurrentFolder, string.Empty, definition.Id).Any(item => item.Path == path),
                    Is.EqualTo(expected), path + " filter " + definition.Id);
                Assert.That(_controller.BrowseProjection.BuildAssetEntries(parent, "tag:" + definition.Name,
                    ZepSearchScope.CurrentFolder, string.Empty, string.Empty).Any(item => item.Path == path),
                    Is.EqualTo(expected), path + " search " + definition.Name);
                if (!_gateway.IsFolder(AssetPath.From(path)))
                    Assert.That(_searchService.Search("tag:" + definition.Name).Any(record => record.AssetPath == path),
                        Is.EqualTo(expected), path + " index search " + definition.Name);
            }
            if (_gateway.IsFolder(AssetPath.From(path)))
            {
                var expectedColor = expectedIds.Length == 0 ? ZepTagColor.None
                    : _tagDefinitions.GetOrderedTags().Single(tag => tag.Id == expectedIds[expectedIds.Length - 1]).Color;
                Assert.That(_controller.GetFolderTintColorForPath(path), Is.EqualTo(expectedColor), path + " tint");
            }
        }

        [Test]
        public void DefinitionExternal_RenameAndRecolorReconcileAssignments()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.StageExternalTags(theme.Value, "red");

            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("red", "Crimson", ZepTagColor.Green)), Is.True);

            Assert.That(_gateway.GetTagIds(theme), Is.EqualTo(new[] { "red" }));
            Assert.That(_searchService.Search("tag:crimson").Select(match => match.AssetPath),
                Has.Member(theme.Value));
            Assert.That(_tagDefinitions.GetOrderedTags().Single(tag => tag.Id == "red").Color,
                Is.EqualTo(ZepTagColor.Green));
        }

        [Test]
        public void DefinitionExternal_CreateDeleteAndNoOpReconcileAssignments()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.StageExternalTags(theme.Value, "red");

            Assert.That(_controller.CreateTag("Amber", ZepTagColor.Orange), Is.True);
            var amber = _tagDefinitions.GetOrderedTags().Single(tag => tag.Name == "Amber");
            Assert.That(_controller.DeleteTagDefinition(amber.Id), Is.True);
            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("red", "Red", ZepTagColor.Red)), Is.True);

            Assert.That(_gateway.GetTagIds(theme), Is.EqualTo(new[] { "red" }));
            Assert.That(_searchService.Search("tag:red").Select(match => match.AssetPath),
                Has.Member(theme.Value));
        }

        [Test]
        public void DefinitionExternal_UndoRedoReconcileAssignments()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("red", "Crimson", ZepTagColor.Red)), Is.True);

            _gateway.StageExternalTags(theme.Value, "red");
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
            Assert.That(_searchService.Search("tag:red").Select(match => match.AssetPath),
                Has.Member(theme.Value));

            _gateway.StageExternalTags("Assets/Art/Hero.png", "red");
            Assert.That(_controller.Redo(_state.PrimaryPane), Is.True);
            Assert.That(_searchService.Search("tag:crimson").Select(match => match.AssetPath),
                Has.Member("Assets/Art/Hero.png"));
        }

        [Test]
        public void DefinitionExternal_PreparationFailureStopsMutation()
        {
            _gateway.TagAssignmentPreparationFailureMessage = "Tag assignments are unavailable.";

            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("red", "Crimson", ZepTagColor.Red)), Is.False);
            Assert.That(_state.StatusMessage, Is.EqualTo("Tag assignments are unavailable."));
            Assert.That(_tagDefinitions.GetOrderedTags().Single(tag => tag.Id == "red").Name,
                Is.EqualTo("Red"));
            Assert.That(_fileCommands.CanUndo, Is.False);

            _gateway.TagAssignmentPreparationFailureMessage = null;
            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("red", "Crimson", ZepTagColor.Red)), Is.True);
            _gateway.TagAssignmentPreparationFailureMessage = "Tag assignments are unavailable.";
            Assert.That(_controller.Undo(_state.PrimaryPane), Is.False);
            Assert.That(_fileCommands.CanUndo, Is.True);
        }

        [Test]
        public void DefinitionExternal_ReconciliationIsConditional()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.StageExternalTags(theme.Value, "red");
            var changed = MeasurePerfCounters(() => Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("red", "Crimson", ZepTagColor.Red)), Is.True));
            Assert.That(changed.GetCount(ZepPerfCounter.TagPresentationRebuilds), Is.EqualTo(1));

            var unchanged = MeasurePerfCounters(() => Assert.That(_controller.CreateTag(
                "Amber", ZepTagColor.Orange), Is.True));
            Assert.That(unchanged.GetCount(ZepPerfCounter.TagPresentationRebuilds), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResetTags_ReconcilesOrphanAssignmentsInFilesFoldersAndSearch(bool keepKnownTag)
        {
            const string filePath = "Assets/Audio/Theme.wav";
            const string folderPath = "Assets/Art";
            _tagDefinitions.ResetToDefaults();
            var assignedIds = keepKnownTag ? new[] { "blue", "orphan" } : new[] { "orphan" };
            _gateway.SetTags(filePath, assignedIds);
            _gateway.SetTags(folderPath, assignedIds);
            _controller.Refresh();
            Assert.That(_searchService.Search("tag:orphan").Any(record => record.AssetPath == filePath), Is.True);
            Assert.That(_controller.GetSearchResults(_state.PrimaryPane, "tag:orphan", ZepSearchScope.All)
                .Any(entry => entry.Path == folderPath), Is.True);

            Assert.That(_controller.ResetTagDefinitions(), Is.True);

            var expectedIds = keepKnownTag ? new[] { "blue" } : Array.Empty<string>();
            foreach (var path in new[] { filePath, folderPath })
            {
                Assert.That(_gateway.GetTagIds(AssetPath.From(path)), Is.EqualTo(expectedIds));
                Assert.That(_controller.GetTagsForPath(path).Select(tag => tag.Id), Is.EqualTo(expectedIds));
                Assert.That(_controller.GetSearchResults(_state.PrimaryPane, "tag:orphan", ZepSearchScope.All)
                    .Any(entry => entry.Path == path), Is.False);
                Assert.That(_controller.GetSearchResults(_state.PrimaryPane, "tag:blue", ZepSearchScope.All)
                    .Any(entry => entry.Path == path), Is.EqualTo(keepKnownTag));
            }
            Assert.That(_searchService.Search("tag:orphan").Any(record => record.AssetPath == filePath), Is.False);
            Assert.That(_searchService.Search("tag:blue").Any(record => record.AssetPath == filePath), Is.EqualTo(keepKnownTag));
            Assert.That(_controller.GetFolderTintColorForPath(folderPath),
                Is.EqualTo(keepKnownTag ? ZepTagColor.Blue : ZepTagColor.None));
        }

        [Test]
        public void ResetTags_ReplayReconcilesAssignmentsWhenDefinitionIdsDoNotChange()
        {
            const string filePath = "Assets/Audio/Theme.wav";
            const string folderPath = "Assets/Art";
            _tagDefinitions.ResetToDefaults();
            _gateway.SetTags(filePath, "orphan");
            _gateway.SetTags(folderPath, "orphan");
            _controller.Refresh();
            var originalDefinitionIds = _tagDefinitions.GetOrderedTags().Select(tag => tag.Id).ToArray();
            Assert.That(_controller.ResetTagDefinitions(), Is.True);
            _controller.Refresh(); // A correct post-reset state makes the inverse regression discriminating.
            Assert.That(_controller.GetTagsForPath(filePath), Is.Empty);
            Assert.That(_controller.GetTagsForPath(folderPath), Is.Empty);

            Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);

            Assert.That(_tagDefinitions.GetOrderedTags().Select(tag => tag.Id), Is.EqualTo(originalDefinitionIds));
            foreach (var path in new[] { filePath, folderPath })
            {
                Assert.That(_gateway.GetTagIds(AssetPath.From(path)), Is.EqualTo(new[] { "orphan" }));
                Assert.That(_controller.GetTagsForPath(path).Select(tag => tag.Id), Is.EqualTo(new[] { "orphan" }));
                Assert.That(_controller.GetSearchResults(_state.PrimaryPane, "tag:orphan", ZepSearchScope.All)
                    .Any(entry => entry.Path == path), Is.True);
            }
            Assert.That(_searchService.Search("tag:orphan").Any(record => record.AssetPath == filePath), Is.True);
            _controller.Refresh(); // Redo also starts from the correct preceding presentation.

            Assert.That(_controller.Redo(_state.PrimaryPane), Is.True);

            foreach (var path in new[] { filePath, folderPath })
            {
                Assert.That(_gateway.GetTagIds(AssetPath.From(path)), Is.Empty);
                Assert.That(_controller.GetTagsForPath(path), Is.Empty);
                Assert.That(_controller.GetSearchResults(_state.PrimaryPane, "tag:orphan", ZepSearchScope.All)
                    .Any(entry => entry.Path == path), Is.False);
            }
            Assert.That(_searchService.Search("tag:orphan").Any(record => record.AssetPath == filePath), Is.False);
        }

        [Test]
        public void DefinitionOnlyEdits_KeepExistingNoSweepPaths()
        {
            foreach (var operation in new Action[]
            {
                () => Assert.That(_controller.CreateTag("Custom", ZepTagColor.Purple), Is.True),
                () => Assert.That(_controller.UpdateTagDefinition(new ZepTag("blue", "Azure", ZepTagColor.Blue)), Is.True),
                () => Assert.That(_controller.UpdateTagDefinition(new ZepTag("red", "Red", ZepTagColor.Green)), Is.True),
                () => Assert.That(_controller.DeleteTagDefinition("custom"), Is.True)
            })
            {
                AssertNoCatalogSweep(MeasurePerfCounters(operation));
            }
        }

        // ---- §3.1 fix 2: a tag-DEFINITION edit re-resolves only tagged paths ----

        private const string BulkFolder = "Assets/Bulk";
        private const string TaggedFolder = "Assets/Bulk/Tagged";
        private const int BulkRecordCount = 10000;
        private const int TaggedRecordCount = 20;

        [Test]
        public void CreatingATagDefinition_TouchesNeitherTheCatalogNorTheProjectionCaches()
        {
            SeedLargeMostlyUntaggedProject();

            var counts = MeasurePerfCounters(
                () => Assert.That(
                    _controller.CreateTag("Azure", ZepTagColor.Orange),
                    Is.True));

            AssertNoCatalogSweep(counts);
            Assert.That(
                TaggedRowNames(),
                Is.EqualTo(Enumerable.Repeat("Blue", TaggedRecordCount).ToArray()),
                "creating an unrelated tag must leave the existing chips alone");
            Assert.That(
                _tagDefinitions.GetOrderedTags().Select(tag => tag.Name),
                Has.Member("Azure"));
        }

        [Test]
        public void RenamingATagDefinition_RepaintsTheTaggedRowsWithoutACatalogSweep()
        {
            SeedLargeMostlyUntaggedProject();

            var counts = MeasurePerfCounters(
                () => Assert.That(
                    _controller.UpdateTagDefinition(
                        new ZepTag("blue", "Azure", ZepTagColor.Blue)),
                    Is.True));

            AssertNoCatalogSweep(counts);
            Assert.That(
                TaggedRowNames(),
                Is.EqualTo(Enumerable.Repeat("Azure", TaggedRecordCount).ToArray()),
                "every tagged row must show the new tag name");
            Assert.That(
                _searchService.Search("tag:azure").Count,
                // The two fixture assets in Assets/Art carry "blue" as well.
                Is.EqualTo(TaggedRecordCount + 2),
                "the tagged records' search text must be rebuilt in the same pass");
        }

        [Test]
        public void RecolouringATagDefinition_RepaintsChipsAndTheFolderTintWithoutACatalogSweep()
        {
            SeedLargeMostlyUntaggedProject();
            _gateway.SetTags(TaggedFolder, "blue");
            _controller.Refresh();
            Assert.That(
                TaggedFolderTint(),
                Is.EqualTo(ZepTagColor.Blue),
                "precondition: the tagged folder is tinted by its tag");

            var counts = MeasurePerfCounters(
                () => Assert.That(
                    _controller.UpdateTagDefinition(
                        new ZepTag("blue", "Blue", ZepTagColor.Green)),
                    Is.True));

            AssertNoCatalogSweep(counts);
            Assert.That(TaggedFolderTint(), Is.EqualTo(ZepTagColor.Green));
            Assert.That(
                TaggedRowColors(),
                Is.EqualTo(
                    Enumerable.Repeat(ZepTagColor.Green, TaggedRecordCount)
                        .ToArray()));
        }

        [Test]
        public void DeletingATagDefinition_DropsTheChipsWithoutACatalogSweep()
        {
            SeedLargeMostlyUntaggedProject();

            var counts = MeasurePerfCounters(
                () => Assert.That(_controller.DeleteTagDefinition("blue"), Is.True));

            AssertNoCatalogSweep(counts);
            Assert.That(TaggedRowNames(), Is.Empty, "the deleted chips must be gone");
            Assert.That(
                _searchService.Search("tag:blue"),
                Is.Empty,
                "the deleted tag must leave the records' search text too");
        }

        // The other half of the rule: an undo that can RESTORE assignments (undo of
        // a tag deletion replays the whole tag snapshot) may put a path back into
        // the tagged set, which the tagged-path refresh cannot see. That one still
        // takes the full sweep — and the chips must actually come back.
        [Test]
        public void UndoingATagDeletion_TakesTheFullSweepAndRestoresTheChips()
        {
            SeedLargeMostlyUntaggedProject();
            Assert.That(_controller.DeleteTagDefinition("blue"), Is.True);
            Assert.That(TaggedRowNames(), Is.Empty);

            var counts = MeasurePerfCounters(
                () => Assert.That(_controller.Undo(_state.PrimaryPane), Is.True));

            Assert.That(
                counts.GetCount(ZepPerfCounter.TagPresentationRebuilds),
                Is.EqualTo(1),
                "a replay that can restore assignments must re-read every record");
            Assert.That(
                TaggedRowNames(),
                Is.EqualTo(Enumerable.Repeat("Blue", TaggedRecordCount).ToArray()));
        }

        // A rename undo cannot restore an assignment, so it stays on the cheap path.
        [Test]
        public void UndoingATagRename_RestoresTheOldNameWithoutACatalogSweep()
        {
            SeedLargeMostlyUntaggedProject();
            Assert.That(
                _controller.UpdateTagDefinition(
                    new ZepTag("blue", "Azure", ZepTagColor.Blue)),
                Is.True);

            var counts = MeasurePerfCounters(
                () => Assert.That(_controller.Undo(_state.PrimaryPane), Is.True));

            AssertNoCatalogSweep(counts);
            Assert.That(
                TaggedRowNames(),
                Is.EqualTo(Enumerable.Repeat("Blue", TaggedRecordCount).ToArray()));
        }

        // The assignment FILE changed under the panel (a git pull). Nothing local
        // knows which paths moved, so this one keeps the full sweep.
        [Test]
        public void ExternalTagAssignmentChange_StillSweepsEveryRecordExactlyOnce()
        {
            SeedLargeMostlyUntaggedProject();
            _gateway.SetTagIds(
                new Dictionary<AssetPath, IReadOnlyList<string>>
                {
                    [AssetPath.From("Assets/Art/Hero.png")] = new[] { "red" }
                });

            var counts = MeasurePerfCounters(
                () => _state.PrimaryPane.SetSearchText("Hero"));

            Assert.That(
                counts.GetCount(ZepPerfCounter.TagPresentationRebuilds),
                Is.EqualTo(1));
        }

        private void SeedLargeMostlyUntaggedProject()
        {
            _source.Folders.Add(AssetPath.From(BulkFolder));
            _source.Folders.Add(AssetPath.From(TaggedFolder));
            for (var i = 0; i < BulkRecordCount; i++)
            {
                _source.Records.Add(new AssetRecord(
                    "bulk-guid-" + i.ToString("D5"),
                    BulkFolder + "/Bulk" + i.ToString("D5") + ".asset",
                    "Bulk" + i.ToString("D5"),
                    "TextAsset"));
            }

            for (var i = 0; i < TaggedRecordCount; i++)
            {
                var path = TaggedFolder + "/Tagged" + i.ToString("D2") + ".asset";
                _source.Records.Add(new AssetRecord(
                    "tagged-guid-" + i.ToString("D2"),
                    path,
                    "Tagged" + i.ToString("D2"),
                    "TextAsset"));
                _gateway.SetTags(path, "blue");
            }

            _controller.Refresh();
        }

        // -------------------------------------------------------------------
        // T4.9 (audit 4.3): moving the selection cannot change WHAT is projected,
        // so a pane with a live search must not re-query the engine per keypress.
        // T4.13 (audit 4.32(b)): the startup search warm waits for evidence that
        // this project's user searches at all.
        // T4.18 (§6 D-6): a hydration that reproduces the catalog leaves the index
        // versions alone, so the projection-cache gate can finally fire.
        // -------------------------------------------------------------------

        [Test]
        public void SelectionOnlyUpdates_WithALiveSearch_DoNotRebuildTheSearchProjection()
        {
            var pane = _state.PrimaryPane;
            var hero = AssetPath.From("Assets/Art/Hero.png");
            var icon = AssetPath.From("Assets/Art/UI/Icon.png");

            var counts = MeasurePerfCounters(() =>
            {
                pane.SetSearchText("o");
                Assert.That(
                    pane.ProjectedAssets.Select(entry => entry.Path),
                    Has.Member(hero.Value).And.Member(icon.Value),
                    "precondition: the search really is live and has >1 result");

                for (var i = 0; i < 10; i++)
                {
                    pane.SetSelectedAssetPath((i % 2 == 0) ? icon : hero);
                }
            });

            Assert.That(
                pane.SelectedAssetPaths.Select(path => path.Value),
                Is.EqualTo(new[] { hero.Value }),
                "the selection still moved on every keypress");
            Assert.That(
                counts.GetCount(ZepPerfCounter.SearchProjectionBuilds),
                Is.EqualTo(1),
                "one build for the query itself, none for the ten selections");
            Assert.That(
                counts.GetCount(ZepPerfCounter.SearchQueries),
                Is.EqualTo(1),
                "and the engine was asked exactly once");
        }

        [Test]
        public void SelectionOnlyUpdate_AfterTheCatalogMoved_RebuildsTheSearchProjection()
        {
            var pane = _state.PrimaryPane;
            pane.SetSearchText("o");
            pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));

            // A catalog mutation that publishes no projection of its own, so the
            // NEXT selection change is the first thing to see the moved version.
            _index.ApplyFileChanges(
                new[]
                {
                    new AssetRecord(
                        "lone-guid",
                        "Assets/Art/Lone.png",
                        "Lone",
                        "Texture2D")
                },
                Array.Empty<string>());

            var afterDelta = MeasurePerfCounters(() =>
                pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png")));
            var afterThat = MeasurePerfCounters(() =>
                pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png")));

            Assert.That(
                afterDelta.GetCount(ZepPerfCounter.SearchProjectionBuilds),
                Is.EqualTo(1),
                "the recorded catalog version moved, so the list is asked for again");
            Assert.That(
                afterThat.GetCount(ZepPerfCounter.SearchProjectionBuilds),
                Is.Zero,
                "and the rebuild re-recorded the inputs, so the next one reuses again");
        }

        [Test]
        public void SearchTextChange_IsNeverTreatedAsASelectionOnlyUpdate()
        {
            var pane = _state.PrimaryPane;
            pane.SetSearchText("o");

            var counts = MeasurePerfCounters(() => pane.SetSearchText("hero"));

            Assert.That(
                counts.GetCount(ZepPerfCounter.SearchProjectionBuilds),
                Is.EqualTo(1));
            Assert.That(
                pane.ProjectedAssets.Select(entry => entry.Path),
                Is.EqualTo(new[] { "Assets/Art/Hero.png" }),
                "and the new query's own answer, not the previous one");
        }

        [Test]
        public void DeferredStartup_InAProjectThatHasNeverSearched_BuildsNoSearchIndex()
        {
            using var probe = BuildSearchWarmupProbe(searchWasUsedInThisProject: false);

            var counts = MeasurePerfCounters(() =>
            {
                probe.Controller.Refresh();
                probe.Scheduler.RunAll();
            });

            Assert.That(
                counts.GetCount(ZepPerfCounter.SearchFullBuilds),
                Is.Zero,
                "nothing has asked for postings, so nothing builds them");
            Assert.That(probe.Search.HasBuiltIndex, Is.False);
            Assert.That(probe.MarkedAsSearched, Is.False);
        }

        [Test]
        public void FirstSearch_MarksTheProjectSoTheNextStartupWarms()
        {
            using var neverSearched = BuildSearchWarmupProbe(
                searchWasUsedInThisProject: false);
            neverSearched.Controller.Refresh();
            neverSearched.Scheduler.RunAll();

            neverSearched.State.PrimaryPane.SetSearchText("hero");

            Assert.That(
                neverSearched.MarkedAsSearched,
                Is.True,
                "the first non-empty search is what earns the warm");
            Assert.That(
                neverSearched.Search.HasBuiltIndex,
                Is.True,
                "and that search itself still gets an index, lazily");

            using var hasSearched = BuildSearchWarmupProbe(
                searchWasUsedInThisProject: true);

            var counts = MeasurePerfCounters(() =>
            {
                hasSearched.Controller.Refresh();
                hasSearched.Scheduler.RunAll();
            });

            Assert.That(
                counts.GetCount(ZepPerfCounter.SearchFullBuilds),
                Is.GreaterThan(0),
                "a project that has searched before warms at startup as it did");
            Assert.That(hasSearched.Search.HasBuiltIndex, Is.True);
        }

        [Test]
        public void EmptySearchText_DoesNotMarkTheProjectAsSearched()
        {
            using var probe = BuildSearchWarmupProbe(searchWasUsedInThisProject: false);
            probe.Controller.Refresh();
            probe.Scheduler.RunAll();

            probe.State.PrimaryPane.SetSearchText(string.Empty);
            probe.State.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Art/Hero.png"));

            Assert.That(probe.MarkedAsSearched, Is.False);
        }

        [Test]
        public void HydrationThatReproducesTheCatalog_RebuildsNoProjectionCache()
        {
            var currentText = _tagDefinitions.BuildTagSearchText(new[] { "blue" });
            _source.Records[0] = AssetRecord.WithTagSearchText(_source.Records[0], currentText);
            _source.Records[1] = AssetRecord.WithTagSearchText(_source.Records[1], currentText);
            _synchronizer.PublishesViaReplace = true;
            _controller.Refresh();
            _scheduler.RunAll();
            var versionAfterHydration = _index.Version;

            var counts = MeasurePerfCounters(() =>
            {
                _controller.Refresh();
                _scheduler.RunAll();
            });

            Assert.That(
                _synchronizer.EnumerationCount,
                Is.GreaterThan(1),
                "precondition: the second pass really republished the snapshot");
            Assert.That(
                _index.Version,
                Is.EqualTo(versionAfterHydration),
                "an identical snapshot is not a catalog change");
            Assert.That(
                counts.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                Is.Zero,
                "T4.4's gate can only fire once Replace stops bumping unconditionally");
            Assert.That(
                _controller.BrowseProjection.BuildAssetEntries(
                        "Assets/Art",
                        string.Empty,
                        ZepSearchScope.Assets,
                        string.Empty,
                        string.Empty)
                    .Select(entry => entry.Path),
                Has.Member("Assets/Art/Hero.png"),
                "the projection that was kept must still be the right one");
        }

        [Test]
        public void HydrationThatChangedTheCatalog_StillRebuildsTheProjectionCache()
        {
            _synchronizer.PublishesViaReplace = true;
            _controller.Refresh();
            _scheduler.RunAll();
            _source.Records.Add(new AssetRecord(
                "late-guid",
                "Assets/Art/Late.png",
                "Late",
                "Texture2D"));

            var counts = MeasurePerfCounters(() =>
            {
                _controller.Refresh();
                _scheduler.RunAll();
            });

            Assert.That(
                counts.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                Is.EqualTo(1));
            Assert.That(
                _controller.BrowseProjection.BuildAssetEntries(
                        "Assets/Art",
                        string.Empty,
                        ZepSearchScope.Assets,
                        string.Empty,
                        string.Empty)
                    .Select(entry => entry.Path),
                Has.Member("Assets/Art/Late.png"));
        }

        [UnityTest]
        public IEnumerator TagScan_AddDuringWorkerPreserved()
        {
            _controller.Dispose();
            var gatedSource = new GatedTagScanSource(_source);
            try
            {
                _controller = new ZepController(
                    _state,
                    _gateway,
                    _fileCommands,
                    _source,
                    _index,
                    _searchService,
                    _tagDefinitions,
                    new AssetIndexSynchronizer(_source, gatedSource, _index, enablePersistentCache: false),
                    _scheduler,
                    new NoOpShellReveal(),
                    new EmptyAddressablesQuery());
                _controller.BeginPanelUse();
                _scheduler.RunUpTo(1);

                for (var attempt = 0; attempt < 120 && !gatedSource.HasEntered; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(gatedSource.HasEntered, Is.True, "the real worker must capture and block before mutation");
                Assert.That(gatedSource.CapturedRecords.Single(record => record.Guid == "hero-guid").TagSearchText, Is.Empty);
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False, "completion is pending while enumeration is blocked");
                Assert.That(_controller.AddTag(
                    new[] { AssetPath.From("Assets/Art/Hero.png") }, "red"), Is.True);

                gatedSource.Release();
                for (var attempt = 0; attempt < 120 && !_controller.IsPaneSourceReconciliationReady; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                yield return DrainTagScanDeferredWork();
                Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var record), Is.True);
                Assert.That(record.TagSearchText, Is.EqualTo(_tagDefinitions.BuildTagSearchText(new[] { "blue", "red" })));
                Assert.That(_searchService.Search("Red").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Red").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(
                    _controller.BrowseProjection.BuildAssetEntries("Assets/Art", string.Empty, ZepSearchScope.Assets, string.Empty, string.Empty)
                        .Single(entry => entry.Path == "Assets/Art/Hero.png").Tags.Select(tag => tag.Name),
                    Has.Member("Red"));
            }
            finally
            {
                gatedSource.Release();
                try { _controller.Dispose(); }
                finally { gatedSource.Dispose(); }
            }
        }

        [UnityTest]
        public IEnumerator TagScan_RemoveDuringWorkerPreserved()
        {
            _source.Records[0] = AssetRecord.WithTagSearchText(
                _source.Records[0],
                _tagDefinitions.BuildTagSearchText(new[] { "blue" }));
            _controller.Dispose();
            var gatedSource = new GatedTagScanSource(_source);
            try
            {
                _controller = new ZepController(
                    _state,
                    _gateway,
                    _fileCommands,
                    _source,
                    _index,
                    _searchService,
                    _tagDefinitions,
                    new AssetIndexSynchronizer(_source, gatedSource, _index, enablePersistentCache: false),
                    _scheduler,
                    new NoOpShellReveal(),
                    new EmptyAddressablesQuery());
                _controller.BeginPanelUse();
                _scheduler.RunUpTo(1);

                for (var attempt = 0; attempt < 120 && !gatedSource.HasEntered; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(gatedSource.HasEntered, Is.True, "the real worker must capture and block before mutation");
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False, "completion is pending while enumeration is blocked");
                Assert.That(_controller.RemoveTag(
                    new[] { AssetPath.From("Assets/Art/Hero.png") }, "blue"), Is.True);

                gatedSource.Release();
                for (var attempt = 0; attempt < 120 && !_controller.IsPaneSourceReconciliationReady; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                yield return DrainTagScanDeferredWork();
                Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var record), Is.True);
                Assert.That(record.TagSearchText, Is.Empty);
                Assert.That(_searchService.Search("Blue").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Blue").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(
                    _controller.BrowseProjection.BuildAssetEntries("Assets/Art", string.Empty, ZepSearchScope.Assets, string.Empty, string.Empty)
                        .Single(entry => entry.Path == "Assets/Art/Hero.png").Tags,
                    Is.Empty);
            }
            finally
            {
                gatedSource.Release();
                try { _controller.Dispose(); }
                finally { gatedSource.Dispose(); }
            }
        }

        [UnityTest]
        public IEnumerator TagScan_DefinitionChangeDuringWorkerPreserved()
        {
            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("blue", "Ocean", ZepTagColor.Blue, false)), Is.True);
            _source.Records[0] = AssetRecord.WithTagSearchText(
                _source.Records[0],
                _tagDefinitions.BuildTagSearchText(new[] { "blue" }));
            _controller.Dispose();

            var renameSource = new GatedTagScanSource(_source);
            try
            {
                _controller = new ZepController(
                    _state,
                    _gateway,
                    _fileCommands,
                    _source,
                    _index,
                    _searchService,
                    _tagDefinitions,
                    new AssetIndexSynchronizer(_source, renameSource, _index, enablePersistentCache: false),
                    _scheduler,
                    new NoOpShellReveal(),
                    new EmptyAddressablesQuery());
                _controller.BeginPanelUse();
                _scheduler.RunUpTo(1);

                for (var attempt = 0; attempt < 120 && !renameSource.HasEntered; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(renameSource.HasEntered, Is.True, "the rename worker must capture and block before mutation");
                var capturedHero = renameSource.CapturedRecords.Single(record => record.Guid == "hero-guid");
                Assert.That(capturedHero.TagSearchText, Does.Contain("Ocean"));
                Assert.That(capturedHero.TagSearchText, Does.Contain("Blue"));
                Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.EqualTo(new[] { "blue" }));
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
                var previousAdvance = _gateway.AdvanceTagVersionOnDefinitionOnlyCommit;
                try
                {
                    _gateway.AdvanceTagVersionOnDefinitionOnlyCommit = false;
                    var tagVersion = _gateway.TagVersion;
                    Assert.That(_controller.UpdateTagDefinition(
                        new ZepTag("blue", "Azure", ZepTagColor.Blue, false)), Is.True);
                    Assert.That(_gateway.TagVersion, Is.EqualTo(tagVersion));
                    Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.EqualTo(new[] { "blue" }));
                }
                finally
                {
                    _gateway.AdvanceTagVersionOnDefinitionOnlyCommit = previousAdvance;
                }

                renameSource.Release();
                for (var attempt = 0; attempt < 120 && !_controller.IsPaneSourceReconciliationReady; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                yield return DrainTagScanDeferredWork();
                Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var renamed), Is.True);
                Assert.That(renamed.TagSearchText, Does.Contain("Azure"));
                Assert.That(renamed.TagSearchText, Does.Contain("Blue"));
                Assert.That(renamed.TagSearchText, Does.Not.Contain("Ocean"));
                Assert.That(_searchService.Search("Azure").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Azure").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("Blue").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Blue").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("Ocean").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Ocean").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(
                    _controller.BrowseProjection.BuildAssetEntries("Assets/Art", string.Empty, ZepSearchScope.Assets, string.Empty, string.Empty)
                        .Single(entry => entry.Path == "Assets/Art/Hero.png").Tags.Select(tag => tag.Name),
                    Has.Member("Azure"));
                Assert.That(_controller.BrowseProjection.BuildAssetEntries(
                        "Assets/Art", string.Empty, ZepSearchScope.Assets, string.Empty, string.Empty)
                    .Single(entry => entry.Path == "Assets/Art/Hero.png").Tags.Single(tag => tag.Id == "blue").Color,
                    Is.EqualTo(ZepTagColor.Blue));
            }
            finally
            {
                renameSource.Release();
                try { _controller.Dispose(); }
                finally { renameSource.Dispose(); }
            }

            _source.Records[0] = AssetRecord.WithTagSearchText(
                _source.Records[0],
                _tagDefinitions.BuildTagSearchText(new[] { "blue" }));
            var deletionSource = new GatedTagScanSource(_source);
            try
            {
                _controller = new ZepController(
                    _state,
                    _gateway,
                    _fileCommands,
                    _source,
                    _index,
                    _searchService,
                    _tagDefinitions,
                    new AssetIndexSynchronizer(_source, deletionSource, _index, enablePersistentCache: false),
                    _scheduler,
                    new NoOpShellReveal(),
                    new EmptyAddressablesQuery());
                _controller.BeginPanelUse();
                _scheduler.RunUpTo(1);

                for (var attempt = 0; attempt < 120 && !deletionSource.HasEntered; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(deletionSource.HasEntered, Is.True, "the deletion worker must capture and block before mutation");
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
                Assert.That(_controller.DeleteTagDefinition("blue"), Is.True);
                Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.Empty);

                deletionSource.Release();
                for (var attempt = 0; attempt < 120 && !_controller.IsPaneSourceReconciliationReady; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }

                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                yield return DrainTagScanDeferredWork();
                Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var deleted), Is.True);
                Assert.That(deleted.TagSearchText, Is.Empty);
                Assert.That(_searchService.Search("Azure").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Azure").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("Blue").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Blue").Select(match => match.AssetPath), Does.Not.Contain("Assets/Art/Hero.png"));
                Assert.That(
                    _controller.BrowseProjection.BuildAssetEntries("Assets/Art", string.Empty, ZepSearchScope.Assets, string.Empty, string.Empty)
                        .Single(entry => entry.Path == "Assets/Art/Hero.png").Tags,
                    Is.Empty);
            }
            finally
            {
                deletionSource.Release();
                try { _controller.Dispose(); }
                finally { deletionSource.Dispose(); }
            }
        }

        [UnityTest]
        public IEnumerator TagScan_UnchangedMetadataDoesNotMutateRecords()
        {
            var currentText = _tagDefinitions.BuildTagSearchText(new[] { "blue" });
            _source.Records[0] = AssetRecord.WithTagSearchText(_source.Records[0], currentText);
            _source.Records[1] = AssetRecord.WithTagSearchText(_source.Records[1], currentText);
            foreach (var path in new[] { "Assets/Art/Hero.png", "Assets/Art/UI/Icon.png" })
            {
                Assert.That(_index.TryGetByPath(path, out var existing), Is.True);
                Assert.That(existing.TagSearchText, Is.EqualTo(currentText));
                Assert.That(_source.Records.Single(record => record.AssetPath == path).TagSearchText, Is.EqualTo(currentText));
            }
            var versionBefore = _index.Version;
            var recordVersionBefore = _index.RecordVersion;
            _controller.Dispose();
            var gatedSource = new GatedTagScanSource(_source);
            try
            {
                _controller = new ZepController(
                    _state, _gateway, _fileCommands, _source, _index, _searchService, _tagDefinitions,
                    new AssetIndexSynchronizer(_source, gatedSource, _index, enablePersistentCache: false),
                    _scheduler, new NoOpShellReveal(), new EmptyAddressablesQuery());
                _controller.BeginPanelUse();
                _scheduler.RunUpTo(1);
                for (var attempt = 0; attempt < 120 && !gatedSource.HasEntered; attempt++) { _scheduler.RunUpTo(1); yield return null; }
                Assert.That(gatedSource.HasEntered, Is.True);
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.False);
                Assert.That(_index.Version, Is.EqualTo(versionBefore));
                Assert.That(_index.RecordVersion, Is.EqualTo(recordVersionBefore));
                gatedSource.Release();
                for (var attempt = 0; attempt < 120 && !_controller.IsPaneSourceReconciliationReady; attempt++)
                {
                    _scheduler.RunUpTo(1);
                    yield return null;
                }
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                yield return DrainTagScanDeferredWork();
                _scheduler.RunUpTo(1);
                Assert.That(_index.Version, Is.EqualTo(versionBefore));
                Assert.That(_index.RecordVersion, Is.EqualTo(recordVersionBefore));
                foreach (var path in new[] { "Assets/Art/Hero.png", "Assets/Art/UI/Icon.png" })
                {
                    Assert.That(_index.TryGetByPath(path, out var record), Is.True);
                    Assert.That(record.TagSearchText, Is.EqualTo(currentText));
                }
                Assert.That(_searchService.Search("Blue").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_searchService.Search("tag:Blue").Select(match => match.AssetPath), Has.Member("Assets/Art/Hero.png"));
                Assert.That(_controller.BrowseProjection.BuildAssetEntries("Assets/Art", string.Empty,
                        ZepSearchScope.Assets, string.Empty, string.Empty)
                    .Single(entry => entry.Path == "Assets/Art/Hero.png").Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
                Assert.That(_scheduler.Faults, Is.Empty);
            }
            finally
            {
                gatedSource.Release();
                try { _controller.Dispose(); }
                finally { gatedSource.Dispose(); }
            }
        }

        [Test]
        public void TagScan_FixtureVersionAndAssignmentEquality()
        {
            var before = _gateway.TagVersion;
            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("blue", "Ocean", ZepTagColor.Blue, false)), Is.True);
            Assert.That(_gateway.TagVersion, Is.EqualTo(before + 1));
            var previousAdvance = _gateway.AdvanceTagVersionOnDefinitionOnlyCommit;
            try
            {
                _gateway.AdvanceTagVersionOnDefinitionOnlyCommit = false;
                before = _gateway.TagVersion;
                Assert.That(_controller.UpdateTagDefinition(
                    new ZepTag("blue", "Azure", ZepTagColor.Blue, false)), Is.True);
                Assert.That(_gateway.TagVersion, Is.EqualTo(before));
                Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.EqualTo(new[] { "blue" }));
                Assert.That(_controller.DeleteTagDefinition("blue"), Is.True);
                Assert.That(_gateway.TagVersion, Is.EqualTo(before + 1));
                Assert.That(_gateway.GetTagIds(AssetPath.From("Assets/Art/Hero.png")), Is.Empty);
            }
            finally
            {
                _gateway.AdvanceTagVersionOnDefinitionOnlyCommit = previousAdvance;
            }

            var left = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["hero-guid"] = null
            };
            var right = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["different-guid"] = null
            };
            Assert.That(FakeGateway.AssignmentContentsEqual(left, right), Is.False);
            right.Clear(); right["HERO-GUID"] = null;
            Assert.That(FakeGateway.AssignmentContentsEqual(left, right), Is.False, "GUID identity is ordinal even for case-insensitive dictionaries");
            right.Clear(); right["hero-guid"] = null;
            Assert.That(FakeGateway.AssignmentContentsEqual(left, right), Is.True);
            left["hero-guid"] = new[] { "blue", "red" };
            right["hero-guid"] = new[] { "red", "blue" };
            Assert.That(FakeGateway.AssignmentContentsEqual(left, right), Is.False);
            right["hero-guid"] = new[] { "Blue", "red" };
            Assert.That(FakeGateway.AssignmentContentsEqual(left, right), Is.False);
            right["hero-guid"] = new[] { "blue", "red" };
            Assert.That(FakeGateway.AssignmentContentsEqual(left, right), Is.True);
        }

        [Test]
        public void TagScan_CompletedSnapshotCorrectsMultipleRecordsBeforePanePublication()
        {
            var pane = OpenArtPane();
            var originals = new[]
            {
                new AssetRecord("hero-guid", "Assets/Art/Hero.png", "Hero", "Texture2D", "oldtagneedle",
                    new[] { "hero-label", "shared-label" }, new[] { "Texture", "Object" }),
                new AssetRecord("icon-guid", "Assets/Art/UI/Icon.png", "Icon", "Texture2D", "oldtagneedle",
                    new[] { "icon-label", "shared-label" }, new[] { "Texture", "Object" })
            };
            _source.Records[0] = originals[0];
            _source.Records[1] = originals[1];
            _synchronizer.PublishesViaReplace = true;
            _synchronizer.DeferSynchronizeCompletion = true;
            var publishedVersion = -1;
            var publications = 0;
            var searchBuildsAfterSnapshot = -1L;
            var previousPerfEnabled = ZepPerfLog.IsEnabled;
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();
            _synchronizer.SnapshotPublished = () =>
            {
                publishedVersion = _index.RecordVersion;
                Assert.That(_index.TryGetByPath(originals[0].AssetPath, out var stale), Is.True);
                Assert.That(stale.TagSearchText, Is.EqualTo("oldtagneedle"));
                Assert.That(_searchService.Search("oldtagneedle").Select(match => match.AssetPath),
                    Is.EquivalentTo(originals.Select(record => record.AssetPath)), "build the search mirror from the stale snapshot before its correction delta");
                searchBuildsAfterSnapshot = ZepPerfLog.CaptureSnapshot().GetCount(ZepPerfCounter.SearchFullBuilds);
            };
            Action<ZepPaneChange> observe = change =>
            {
                if (publishedVersion < 0 || change.NewPane != pane
                    || (change.Update & ZepPaneUpdate.Projection) == ZepPaneUpdate.None) return;
                publications++;
                foreach (var original in originals)
                {
                    Assert.That(_index.TryGetByPath(original.AssetPath, out var corrected), Is.True);
                    Assert.That(corrected.TagSearchText, Is.EqualTo(_tagDefinitions.BuildTagSearchText(new[] { "blue" })));
                    Assert.That(_searchService.Search("Blue").Select(match => match.AssetPath), Has.Member(original.AssetPath));
                    Assert.That(_searchService.Search("tag:Blue").Select(match => match.AssetPath), Has.Member(original.AssetPath));
                    Assert.That(_searchService.Search("oldtagneedle").Select(match => match.AssetPath), Does.Not.Contain(original.AssetPath));
                    var parent = Path.GetDirectoryName(original.AssetPath).Replace('\\', '/');
                    Assert.That(_controller.BrowseProjection.BuildAssetEntries(parent, string.Empty,
                            ZepSearchScope.Assets, string.Empty, string.Empty)
                        .Single(entry => entry.Path == original.AssetPath).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
                }
                Assert.That(searchBuildsAfterSnapshot, Is.GreaterThan(0));
                Assert.That(ZepPerfLog.CaptureSnapshot().GetCount(ZepPerfCounter.SearchFullBuilds),
                    Is.EqualTo(searchBuildsAfterSnapshot), "the correction must update the warm search mirror through its delta");
            };
            _state.PaneChanged += observe;
            try
            {
                _controller.Refresh();
                _synchronizer.CompleteDeferredSynchronization();
                _scheduler.RunAll();
                Assert.That(publishedVersion, Is.GreaterThanOrEqualTo(0));
                Assert.That(publications, Is.GreaterThan(0), "an actual dependent pane publication must be observed");
                Assert.That(_index.RecordVersion, Is.EqualTo(publishedVersion + 1), "all stale records use one correction transaction");

                foreach (var original in originals)
                {
                    Assert.That(_index.TryGetByPath(original.AssetPath, out var corrected), Is.True);
                    Assert.That(corrected, Is.EqualTo(AssetRecord.WithTagSearchText(original,
                        _tagDefinitions.BuildTagSearchText(new[] { "blue" }))), "only tag-derived fields change");
                    Assert.That(corrected.UnityLabels, Is.EqualTo(original.UnityLabels));
                    Assert.That(corrected.BaseTypeNames, Is.EqualTo(original.BaseTypeNames));
                }
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousPerfEnabled;
                ZepPerfLog.Reset();
                _state.PaneChanged -= observe;
                _synchronizer.SnapshotPublished = null;
                _synchronizer.DeferSynchronizeCompletion = false;
            }
        }

        private IEnumerator DrainTagScanDeferredWork()
        {
            for (var attempt = 0; attempt < 240 && _scheduler.PendingCount > 0; attempt++)
            {
                _scheduler.RunUpTo(1);
                yield return null;
            }
            Assert.That(_scheduler.PendingCount, Is.Zero, "deferred work must finish after readiness within the bounded editor wait");
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        private SearchWarmupProbe BuildSearchWarmupProbe(bool searchWasUsedInThisProject)
        {
            return new SearchWarmupProbe(_gateway, _tagDefinitions, searchWasUsedInThisProject);
        }

        // A second, independent controller: the warm decision is taken once per
        // controller construction, so it cannot be observed on the shared one.
        private sealed class SearchWarmupProbe : IDisposable
        {
            internal SearchWarmupProbe(
                FakeGateway gateway,
                TagDefinitionStore tagDefinitions,
                bool searchWasUsedInThisProject)
            {
                Source = new MutableRecordSource(
                    new[]
                    {
                        new AssetRecord(
                            "hero-guid",
                            "Assets/Art/Hero.png",
                            "Hero",
                            "Texture2D"),
                        new AssetRecord(
                            "theme-guid",
                            "Assets/Audio/Theme.wav",
                            "Theme",
                            "AudioClip")
                    },
                    new[] { "Assets", "Assets/Art", "Assets/Audio", "Packages" });
                State = new ZepState();
                Index = new InMemoryAssetIndex();
                Search = new AssetSearchService(Index);
                Scheduler = new QueuedScheduler();
                Controller = new ZepController(
                    State,
                    gateway,
                    new ProjectFileCommandService(gateway),
                    Source,
                    Index,
                    Search,
                    tagDefinitions,
                    new FakeSynchronizer(Source, Index),
                    Scheduler,
                    new NoOpShellReveal(),
                    new EmptyAddressablesQuery(),
                    null,
                    searchWasUsedInThisProject,
                    () => MarkedAsSearched = true);
            }

            internal MutableRecordSource Source { get; }

            internal ZepState State { get; }

            internal InMemoryAssetIndex Index { get; }

            internal AssetSearchService Search { get; }

            internal QueuedScheduler Scheduler { get; }

            internal ZepController Controller { get; }

            internal bool MarkedAsSearched { get; private set; }

            public void Dispose()
            {
                Controller.Dispose();
            }
        }

        private ZepPerfSnapshot MeasurePerfCounters(Action work)
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                work();
                return ZepPerfLog.CaptureSnapshot();
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        private static void AssertNoCatalogSweep(ZepPerfSnapshot counts)
        {
            Assert.That(
                counts.GetCount(ZepPerfCounter.TagPresentationRebuilds),
                Is.Zero,
                "a definition edit must not re-read every record's tags");
            Assert.That(
                counts.GetCount(ZepPerfCounter.RebuildProjectionCacheCalls),
                Is.Zero);
            Assert.That(
                counts.GetCount(ZepPerfCounter.FullRecordEnumerations),
                Is.Zero,
                "the record catalog must not be enumerated at all");
        }

        [Test]
        public void RecoveryTagCache_RecordReadFailurePreservesState()
        {
            var records = new[]
            {
                new AssetRecord("cache-hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                new AssetRecord("cache-theme", "Assets/Audio/Theme.wav", "Theme", "AudioClip")
            };
            var folders = new[]
            {
                new ZepFolderEntry("Art", "Assets/Art", 1, false, guid: "cache-art")
            };
            var projection = new TagProjectionService(_gateway, _tagDefinitions);
            _gateway.SetTags("Assets/Art/Hero.png", "blue");
            _gateway.SetTags("Assets/Audio/Theme.wav", "red");
            _gateway.SetTags("Assets/Art", "red", "blue");
            projection.RebuildCache(records, folders);
            projection.RebuildFilterOptions();
            projection.ObservedTagVersion = 41;

            var failure = new ProjectTagStateUnavailableException("assignment read refused");
            _gateway.ResetTagAssignmentReadCalls();
            _gateway.TagAssignmentReadCallback = (_, count) =>
            {
                if (count == 2)
                {
                    throw failure;
                }
            };
            _gateway.SetTags("Assets/Art/Hero.png", "red");

            var thrown = Assert.Throws<ProjectTagStateUnavailableException>(
                () => projection.RebuildCache(records, folders));

            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(projection.GetTagsForPath("Assets/Art/Hero.png").Select(tag => tag.Id),
                Is.EqualTo(new[] { "blue" }));
            Assert.That(projection.GetFolderTintColorForPath("Assets/Art", "Folder"),
                Is.EqualTo(ZepTagColor.Blue));
            Assert.That(projection.CachedFilterOptions.Count, Is.EqualTo(2));
            Assert.That(projection.ObservedTagVersion, Is.EqualTo(41));
        }

        [Test]
        public void RecoveryTagCache_FolderReadFailurePreservesState()
        {
            var records = new[]
            {
                new AssetRecord("folder-hero", "Assets/Art/Hero.png", "Hero", "Texture2D")
            };
            var folders = new[]
            {
                new ZepFolderEntry("Art", "Assets/Art", 1, false, guid: "folder-art"),
                new ZepFolderEntry("Audio", "Assets/Audio", 1, false, guid: "folder-audio")
            };
            var projection = new TagProjectionService(_gateway, _tagDefinitions);
            _gateway.SetTags("Assets/Art/Hero.png", "blue");
            _gateway.SetTags("Assets/Art", "red", "blue");
            _gateway.SetTags("Assets/Audio", "red");
            projection.RebuildCache(records, folders);

            var failure = new ProjectTagStateUnavailableException("folder assignment refused");
            _gateway.ResetTagAssignmentReadCalls();
            _gateway.TagAssignmentReadCallback = (_, count) =>
            {
                if (count == 3)
                {
                    throw failure;
                }
            };

            Assert.That(
                Assert.Throws<ProjectTagStateUnavailableException>(
                    () => projection.RebuildCache(records, folders)),
                Is.SameAs(failure));
            Assert.That(projection.GetFolderTintColorForPath("Assets/Art", "Folder"),
                Is.EqualTo(ZepTagColor.Blue));

            _tagDefinitions.ReplaceAll(new[]
            {
                new ZepTag("blue", "Blue", ZepTagColor.Green),
                new ZepTag("red", "Red", ZepTagColor.Red)
            });
            projection.RefreshResolvedTagsForTaggedPaths(Array.Empty<string>());
            Assert.That(projection.GetFolderTintColorForPath("Assets/Art", "Folder"),
                Is.EqualTo(ZepTagColor.Green));
        }

        [Test]
        public void RecoveryTagCache_RetryReplacesState()
        {
            var hero = new AssetRecord("retry-hero", "Assets/Art/Hero.png", "Hero", "Texture2D");
            var theme = new AssetRecord("retry-theme", "Assets/Audio/Theme.wav", "Theme", "AudioClip");
            var records = new[] { hero, theme };
            var folders = new[]
            {
                new ZepFolderEntry("Art", "Assets/Art", 1, false, guid: "retry-art")
            };
            var projection = new TagProjectionService(_gateway, _tagDefinitions);
            var mutableTags = new[] { "blue" };
            _gateway.SetTags(hero.AssetPath, mutableTags);
            _gateway.SetTags(theme.AssetPath, "red");
            _gateway.SetTags("Assets/Art", "red", "blue");
            projection.RebuildCache(records, folders);
            projection.RebuildFilterOptions();

            _gateway.ResetTagAssignmentReadCalls();
            _gateway.TagAssignmentReadCallback = (_, count) =>
            {
                if (count == 2)
                {
                    throw new ProjectTagStateUnavailableException("retry refusal");
                }
            };
            Assert.Throws<ProjectTagStateUnavailableException>(() => projection.RebuildCache(records, folders));

            _gateway.TagAssignmentReadCallback = null;
            _gateway.SetTags(hero.AssetPath, "red");
            _gateway.SetTags(theme.AssetPath, Array.Empty<string>());
            _gateway.SetTags("Assets/Art", "red");
            projection.RebuildCache(new[] { hero }, folders);
            projection.RebuildFilterOptions();
            mutableTags[0] = "blue";

            Assert.That(projection.GetTagsForPath(hero.AssetPath).Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
            Assert.That(projection.GetTagsForPath(theme.AssetPath), Is.Empty);
            Assert.That(projection.GetFolderTintColorForPath("Assets/Art", "Folder"),
                Is.EqualTo(ZepTagColor.Red));
            Assert.That(
                projection.CachedFilterOptions.Select(option => option.TagId + ":" + option.Count),
                Is.EqualTo(new[] { "blue:0", "red:2" }));

            var unrelated = new InvalidOperationException("unrelated read failure");
            _gateway.ResetTagAssignmentReadCalls();
            _gateway.TagAssignmentReadCallback = (_, count) =>
            {
                if (count == 1)
                {
                    throw unrelated;
                }
            };
            Assert.That(Assert.Throws<InvalidOperationException>(
                () => projection.RebuildCache(new[] { hero }, folders)), Is.SameAs(unrelated));
            Assert.That(projection.GetTagsForPath(hero.AssetPath).Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
        }

        [Test]
        public void RecoveryTagCache_DefinitionPreflightIntegration()
        {
            _gateway.StageExternalTags("Assets/Audio/Theme.wav", "red");
            _gateway.ResetTagAssignmentReadCalls();
            var refusal = new ProjectTagStateUnavailableException("rebuild refused");
            _gateway.TagAssignmentReadCallback = (_, count) =>
            {
                // The record-correction pass completes before this first rebuild read.
                if (count == 6)
                {
                    throw refusal;
                }
            };

            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("blue", "Azure", ZepTagColor.Blue)), Is.False);
            Assert.That(_gateway.TagAssignmentReadCalls, Is.EqualTo(6));
            Assert.That(_fileCommands.CanUndo, Is.False);
            Assert.That(_tagDefinitions.GetOrderedTags().Single(tag => tag.Id == "blue").Name, Is.EqualTo("Blue"));
            Assert.That(_state.StatusMessage, Is.EqualTo("rebuild refused"));

            _gateway.TagAssignmentReadCallback = null;
            Assert.That(_controller.UpdateTagDefinition(
                new ZepTag("blue", "Azure", ZepTagColor.Blue)), Is.True);
            Assert.That(_tagDefinitions.GetOrderedTags().Single(tag => tag.Id == "blue").Name, Is.EqualTo("Azure"));

            _gateway.StageExternalTags("Assets/Audio/Theme.wav", "blue");
            _gateway.ResetTagAssignmentReadCalls();
            var unrelated = new InvalidOperationException("unrelated preflight failure");
            _gateway.TagAssignmentReadCallback = (_, count) =>
            {
                if (count == 6)
                {
                    throw unrelated;
                }
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => _controller.UpdateTagDefinition(
                new ZepTag("blue", "Blue", ZepTagColor.Blue))), Is.SameAs(unrelated));
        }

        [Test]
        public void RecoveryAssetChanges_RefusalPreservesPublication()
        {
            const string importedPath = "Assets/Art/RecoveryImported.png";
            var publishedPaths = _state.PrimaryPane.ProjectedAssets
                .Select(asset => asset.Path)
                .ToArray();
            _source.Records.Add(new AssetRecord(
                "recovery-imported-guid",
                importedPath,
                "RecoveryImported",
                "Texture2D"));
            _gateway.SetTags(importedPath, "red");
            var reads = 0;
            _gateway.TagAssignmentReadCallback = (path, _) =>
            {
                if (path.Value == importedPath)
                {
                    reads++;
                    throw new ProjectTagStateUnavailableException("Tag state unavailable");
                }
            };

            try
            {
                Assert.DoesNotThrow(() => _controller.HandleAssetChanges(
                    new ProjectAssetChanges(
                        new[] { importedPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>())));
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(
                    _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                    Is.EqualTo(publishedPaths));
                Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));
                Assert.That(_synchronizer.RecordedConcreteChanges, Has.Count.EqualTo(1));
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryAssetChanges_PendingWorkStaysBlocked()
        {
            const string refusedPath = "Assets/Art/RecoveryRefused.png";
            const string blockedPath = "Assets/Art/RecoveryBlocked.png";
            _source.Records.Add(new AssetRecord(
                "recovery-refused-guid", refusedPath, "RecoveryRefused", "Texture2D"));
            _source.Records.Add(new AssetRecord(
                "recovery-blocked-guid", blockedPath, "RecoveryBlocked", "Texture2D"));
            var reads = 0;
            _gateway.TagAssignmentReadCallback = (_, __) =>
            {
                reads++;
                throw new ProjectTagStateUnavailableException("Tag state unavailable");
            };

            try
            {
                _controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { refusedPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                _controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { blockedPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));

                Assert.That(reads, Is.EqualTo(1));
                Assert.That(_synchronizer.RecordedConcreteChanges, Has.Count.EqualTo(2));
                Assert.That(_index.TryGetByPath(blockedPath, out _), Is.False);
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryAssetChanges_RefreshReconstructsCurrentState()
        {
            const string importedPath = "Assets/Art/RecoveryRefresh.png";
            _source.Records.Add(new AssetRecord(
                "recovery-refresh-guid", importedPath, "RecoveryRefresh", "Texture2D"));
            _gateway.SetTags(importedPath, "red");
            _gateway.TagAssignmentReadCallback = (path, _) =>
            {
                if (path.Value == importedPath)
                {
                    throw new ProjectTagStateUnavailableException("Tag state unavailable");
                }
            };

            try
            {
                _controller.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { importedPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                _gateway.TagAssignmentReadCallback = null;
                _controller.Refresh();

                Assert.That(_index.TryGetByPath(importedPath, out var record), Is.True);
                Assert.That(record.Guid, Is.EqualTo("recovery-refresh-guid"));
                Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryAssetChanges_PrePanelAndFailureContracts()
        {
            const string importedPath = "Assets/Art/RecoveryPrePanel.png";
            const string prePanelPath = "Assets/Art/RecoveryPrePanelFailure.png";
            const string activePath = "Assets/Art/RecoveryActiveFailure.png";
            _source.Records.Add(new AssetRecord(
                "recovery-pre-panel-guid", importedPath, "RecoveryPrePanel", "Texture2D"));
            _gateway.TagAssignmentReadCallback = (_, __) =>
                throw new ProjectTagStateUnavailableException("Tag state unavailable");

            try
            {
                Assert.DoesNotThrow(() => _controller.HandleAssetChanges(
                    new ProjectAssetChanges(
                        new[] { importedPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>())));
                Assert.That(_synchronizer.RecordedConcreteChanges, Has.Count.EqualTo(1));

                _controller.Dispose();
                SetUp();
                _source.Records.Add(new AssetRecord(
                    "recovery-pre-panel-failure-guid", prePanelPath, "RecoveryPrePanelFailure", "Texture2D"));
                var prePanelRead = false;
                var prePanelFailure = new InvalidOperationException("unrelated pre-panel failure");
                _gateway.TagAssignmentReadCallback = (path, _) =>
                {
                    if (path.Value == prePanelPath)
                    {
                        prePanelRead = true;
                        throw prePanelFailure;
                    }
                };
                Assert.That(Assert.Throws<InvalidOperationException>(() => _controller.HandleAssetChanges(
                    new ProjectAssetChanges(
                        new[] { prePanelPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()))), Is.SameAs(prePanelFailure));
                Assert.That(prePanelRead, Is.True);

                _controller.Dispose();
                SetUp();
                _controller.BeginPanelUse();
                _scheduler.RunAll();
                _source.Records.Add(new AssetRecord(
                    "recovery-active-failure-guid", activePath, "RecoveryActiveFailure", "Texture2D"));
                var activeRead = false;
                var activeFailure = new InvalidOperationException("unrelated active-panel failure");
                _gateway.TagAssignmentReadCallback = (path, _) =>
                {
                    if (path.Value == activePath)
                    {
                        activeRead = true;
                        throw activeFailure;
                    }
                };
                Assert.That(Assert.Throws<InvalidOperationException>(() => _controller.HandleAssetChanges(
                    new ProjectAssetChanges(
                        new[] { activePath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()))), Is.SameAs(activeFailure));
                Assert.That(activeRead, Is.True);
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        private IReadOnlyList<string> TaggedRowNames()
        {
            return TaggedRows()
                .SelectMany(entry => entry.Tags.Select(tag => tag.Name))
                .ToList();
        }

        private IReadOnlyList<ZepTagColor> TaggedRowColors()
        {
            return TaggedRows()
                .SelectMany(entry => entry.Tags.Select(tag => tag.Color))
                .ToList();
        }

        private IReadOnlyList<ZepAssetEntry> TaggedRows()
        {
            return _controller.BrowseProjection.BuildAssetEntries(
                    TaggedFolder,
                    string.Empty,
                    ZepSearchScope.Assets,
                    string.Empty,
                    string.Empty)
                .Where(entry => entry.TypeName != "Folder")
                .ToList();
        }

        private ZepTagColor TaggedFolderTint()
        {
            return _controller.BrowseProjection.BuildAssetEntries(
                    BulkFolder,
                    string.Empty,
                    ZepSearchScope.Assets,
                    string.Empty,
                    string.Empty)
                .Single(entry => entry.Path == TaggedFolder)
                .FolderTintColor;
        }

        private const string ParityRoot = "Assets/ZEP_MoveFilterParity";
        private const string ParitySource = ParityRoot + "/Src";
        private const string ParityNested = ParitySource + "/Nested";
        private const string ParityDestination = ParityRoot + "/Dest";
        private const string ParitySibling = ParityRoot + "/SrcExtra";
        private const string ParityLeaf = ParitySource + "/Leaf.txt";
        private const string ParityForeignLeaf = ParityDestination + "/Other.txt";

        // Only the folders need to exist — a file path that is not a folder is
        // exactly what the filter's IsValidFolder check has to see for the leaf
        // cases, so the leaves stay virtual.
        private sealed class ParityFolders : IDisposable
        {
            internal ParityFolders()
            {
                AssetDatabase.DeleteAsset(ParityRoot);
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(ParityRoot));
                AssetDatabase.CreateFolder(ParityRoot, Path.GetFileName(ParitySource));
                AssetDatabase.CreateFolder(ParitySource, Path.GetFileName(ParityNested));
                AssetDatabase.CreateFolder(ParityRoot, Path.GetFileName(ParityDestination));
                AssetDatabase.CreateFolder(ParityRoot, Path.GetFileName(ParitySibling));
            }

            public void Dispose()
            {
                AssetDatabase.DeleteAsset(ParityRoot);
            }
        }

        private ZepPaneState OpenArtPane()
        {
            _state.PrimaryPane.TrySetFolderSelection("Assets/Audio");
            var result = _state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Added));
            Assert.That(_state.IsLivePane(result.NewPane), Is.True);
            return result.NewPane;
        }

        [Test]
        public void RecoveryTree_ControllerQueryIntegration()
        {
            const string pendingPath = "Assets/Audio/Pending.wav";
            const string childPath = "Assets/Audio/Child";
            _controller.Dispose();
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _searchService = new AssetSearchService(_index);
            _scheduler = new QueuedScheduler();
            _source.Folders.Add(AssetPath.From(childPath));
            _gateway.SetTags(pendingPath, "blue");
            var synchronizer = new FakeSynchronizer(_source, _index)
            {
                DeferSynchronizeCompletion = true,
                PublishesViaReplace = true
            };
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Preview.png"),
                    isFolder: false,
                    "Preview",
                    "Texture2D"),
                new DirectFolderEntry(
                    AssetPath.From("Assets/Art"),
                    isFolder: true,
                    "Art",
                    "Folder"),
                new DirectFolderEntry(
                    AssetPath.From("Assets/Audio"),
                    isFolder: true,
                    "Audio",
                    "Folder"));
            directSource.SetComplete(
                "Assets/Audio",
                new DirectFolderEntry(
                    AssetPath.From(pendingPath),
                    isFolder: false,
                    "Pending",
                    "AudioClip"),
                new DirectFolderEntry(
                    AssetPath.From(childPath),
                    isFolder: true,
                    "Child",
                    "Folder"));
            _controller = new ZepController(
                _state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                _index,
                _searchService,
                _tagDefinitions,
                synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery(),
                directSource);
            _controller.BeginPanelUse();

            var readinessNotifications = 0;
            _controller.PaneSourceReconciliationReady += () => readinessNotifications++;
            var tree = new Zep.Editor.UI.Sidebar.FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                _controller.GetVisibleFolderChildren,
                _controller.GetVisibleFolderEntry);
            tree.Apply("Assets", "Assets");
            var baselineRows = tree.VisibleFolderCount;
            var publishedPaths = _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path).ToArray();
            Assert.That(baselineRows, Is.GreaterThan(0));
            Assert.That(publishedPaths, Is.Not.Empty);
            Assert.That(directSource.ObservedPaths, Does.Not.Contain("Assets/Audio"));
            directSource.ObservedPaths.Clear();
            var reads = 0;
            _gateway.TagAssignmentReadCallback = (path, _) =>
            {
                if (path.Value == pendingPath)
                {
                    reads++;
                    throw new ProjectTagStateUnavailableException("Tag state unavailable");
                }
            };

            try
            {
                Assert.DoesNotThrow(() => tree.Apply(childPath, childPath));
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(directSource.ObservedPaths, Is.EqualTo(new[] { "Assets/Audio" }));
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(baselineRows));
                Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                    Is.EqualTo(publishedPaths));
                Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));

                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    _controller.GetVisibleFolderChildren("Assets"));
                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    _controller.GetVisibleFolderEntry("Assets/Art"));
                Assert.That(reads, Is.EqualTo(1),
                    "blocked getters must not observe again or expose a partial cache");

                _gateway.TagAssignmentReadCallback = null;
                _controller.Refresh();
                synchronizer.CompleteDeferredSynchronization();
                tree.InvalidateCatalog();

                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                Assert.That(readinessNotifications, Is.EqualTo(1));
                Assert.That(_controller.GetVisibleFolderChildren("Assets/Audio")
                        .Select(entry => entry.Path),
                    Is.EqualTo(new[] { childPath }));
                tree.InvalidateCatalog();
                Assert.That(readinessNotifications, Is.EqualTo(1));
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryTreeTint_ControllerIntegration()
        {
            RecoveryTree_ControllerQueryIntegration();
        }

        [Test]
        public void RecoveryFavorites_ControllerQueryIntegration()
        {
            const string pendingPath = "Assets/Audio/Pending.wav";
            const string childPath = "Assets/Audio/Child";
            _controller.Dispose();
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _searchService = new AssetSearchService(_index);
            _scheduler = new QueuedScheduler();
            _source.Folders.Add(AssetPath.From(childPath));
            _gateway.SetTags(pendingPath, "blue");
            var synchronizer = new FakeSynchronizer(_source, _index)
            {
                DeferSynchronizeCompletion = true,
                PublishesViaReplace = true
            };
            var directSource = new FakeDirectFolderSource();
            directSource.SetComplete("Packages");
            directSource.SetComplete(
                "Assets",
                new DirectFolderEntry(AssetPath.From("Assets/Preview.png"), false, "Preview", "Texture2D"),
                new DirectFolderEntry(AssetPath.From("Assets/Art"), true, "Art", "Folder"),
                new DirectFolderEntry(AssetPath.From("Assets/Audio"), true, "Audio", "Folder"));
            directSource.SetComplete(
                "Assets/Audio",
                new DirectFolderEntry(AssetPath.From(pendingPath), false, "Pending", "AudioClip"),
                new DirectFolderEntry(AssetPath.From(childPath), true, "Child", "Folder"));
            _controller = new ZepController(
                _state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                _index,
                _searchService,
                _tagDefinitions,
                synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery(),
                directSource);
            _controller.BeginPanelUse();

            var favorites = new Zep.Editor.UI.Sidebar.FavoritesWidget(
                new UnityZepIconRepository(),
                _controller.GetVisibleFolderEntry);
            favorites.SetFavorites(new[] { "Assets/Art" });
            favorites.SetSelectedPath("Assets/Art");
            var rows = favorites.Q<VisualElement>("zep-sidebar-favorites-list");
            var baseline = rows[0];
            var baselineLabel = ((Label)baseline[1][0]).text;

            directSource.ObservedPaths.Clear();
            _gateway.ResetTagAssignmentReadCalls();
            _gateway.TagAssignmentReadCallback = (path, _) =>
            {
                if (path.Value == pendingPath)
                {
                    throw new ProjectTagStateUnavailableException("Tag state unavailable");
                }
            };

            try
            {
                Assert.DoesNotThrow(() => favorites.SetFavorites(new[] { childPath }));
                Assert.That(_gateway.TagAssignmentReadCalls, Is.EqualTo(1));
                Assert.That(directSource.ObservedPaths, Is.EqualTo(new[] { "Assets/Audio" }));
                Assert.That(rows[0], Is.SameAs(baseline));
                Assert.That(((Label)rows[0][1][0]).text, Is.EqualTo(baselineLabel));
                Assert.That(favorites.SelectedFolderEntry?.Path, Is.EqualTo("Assets/Art"));
                Assert.That(_state.StatusMessage, Is.EqualTo("Tag state unavailable"));

                Assert.DoesNotThrow(() => favorites.SetFavorites(new[] { childPath }));
                Assert.That(_gateway.TagAssignmentReadCalls, Is.EqualTo(1));
                Assert.That(directSource.ObservedPaths, Is.EqualTo(new[] { "Assets/Audio" }));
                Assert.That(_index.TryGetPending("Assets/Art", out var pendingArt), Is.True);
                Assert.That(pendingArt.IsFolder, Is.True);

                _gateway.TagAssignmentReadCallback = null;
                _source.RemovePath("Assets/Art");
                directSource.SetComplete(
                    "Assets",
                    new DirectFolderEntry(AssetPath.From("Assets/Preview.png"), false, "Preview", "Texture2D"),
                    new DirectFolderEntry(AssetPath.From("Assets/Audio"), true, "Audio", "Folder"));
                _index.ApplyFileChanges(Array.Empty<AssetRecord>(), new[] { "Assets/Art" });
                Assert.That(_index.TryGetPending("Assets/Art", out _), Is.False);
                Assert.That(_index.TryGetFolder("Assets/Art", out _), Is.False);

                _controller.Refresh();
                synchronizer.CompleteDeferredSynchronization();

                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
                Assert.That(_controller.GetVisibleFolderEntry("Assets/Art"), Is.Null);

                favorites.SetFavorites(new[] { "Assets/Art", childPath });

                Assert.That(rows.childCount, Is.EqualTo(1));
                Assert.That(rows[0].userData, Is.EqualTo(childPath));
                Assert.That(favorites.SelectedFolderEntry, Is.Null);
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryDirectMetadata_GettersRefuseAndReport()
        {
            PrepareDirectMetadataRecoveryController();
            var failure = new ProjectTagStateUnavailableException("tag read refused");
            _gateway.TagAssignmentReadCallback = (_, __) => throw failure;

            try
            {
                Assert.That(
                    Assert.Throws<ProjectTagStateUnavailableException>(() =>
                        _controller.GetTagsForPath("Assets/Pending.asset")),
                    Is.SameAs(failure));
                Assert.That(_state.StatusMessage, Is.EqualTo(failure.Message));

                PrepareDirectMetadataRecoveryController();
                _gateway.TagAssignmentReadCallback = (_, __) => throw failure;
                Assert.That(
                    Assert.Throws<ProjectTagStateUnavailableException>(() =>
                        _controller.GetFolderTintColorForPath("Assets/PendingFolder")),
                    Is.SameAs(failure));
                Assert.That(_state.StatusMessage, Is.EqualTo(failure.Message));
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryDirectMetadata_BlockPreventsFurtherReads()
        {
            PrepareDirectMetadataRecoveryController();
            var reads = 0;
            _gateway.TagAssignmentReadCallback = (_, __) =>
            {
                reads++;
                throw new ProjectTagStateUnavailableException("tag read refused");
            };

            try
            {
                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    _controller.GetTagsForPath("Assets/Pending.asset"));
                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    _controller.GetTagsForPath("Assets/Art/Hero.png"));
                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    _controller.GetFolderTintColorForPath("Assets/PendingFolder"));
                Assert.That(reads, Is.EqualTo(1));

                _gateway.TagAssignmentReadCallback = null;
                _controller.Refresh();
                _synchronizer.CompleteDeferredSynchronization();
                Assert.That(_controller.IsPaneSourceReconciliationReady, Is.True);
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryDirectMetadata_FavoritesIntegration()
        {
            PrepareDirectMetadataRecoveryController();
            var favorites = new Zep.Editor.UI.Sidebar.FavoritesWidget(
                new UnityZepIconRepository(),
                path => new ZepFolderEntry(
                    AssetPathFormat.GetFileName(path), path, 0, false),
                _controller.GetFolderTintColorForPath);
            var failure = new ProjectTagStateUnavailableException("folder tint refused");
            _gateway.TagAssignmentReadCallback = (_, __) => throw failure;

            try
            {
                Assert.DoesNotThrow(() => favorites.SetFavorites(new[]
                {
                    "Assets/FirstFavorite",
                    "Assets/SecondFavorite"
                }));
                Assert.That(_state.StatusMessage, Is.EqualTo(failure.Message));
                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    _controller.GetTagsForPath("Assets/Pending.asset"));
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        [Test]
        public void RecoveryDirectMetadata_SuccessAndFailureContracts()
        {
            PrepareDirectMetadataRecoveryController();
            Assert.That(_controller.GetTagsForPath("Assets/Pending.asset"), Is.Empty);
            Assert.That(
                _controller.GetFolderTintColorForPath("Assets/PendingFolder"),
                Is.EqualTo(ZepTagColor.None));
            Assert.That(_controller.GetTagsForPath("Outside/Virtual"), Is.Empty);

            var statusBeforeFailure = _state.StatusMessage;
            var unrelated = new InvalidOperationException("unrelated read failure");
            _gateway.TagAssignmentReadCallback = (_, __) => throw unrelated;
            try
            {
                Assert.That(
                    Assert.Throws<InvalidOperationException>(() =>
                        _controller.GetTagsForPath("Assets/OtherPending.asset")),
                    Is.SameAs(unrelated));
                Assert.That(_state.StatusMessage, Is.EqualTo(statusBeforeFailure));
            }
            finally
            {
                _gateway.TagAssignmentReadCallback = null;
            }
        }

        private void PrepareDirectMetadataRecoveryController()
        {
            _controller.Dispose();
            _state = new ZepState();
            _index = new InMemoryAssetIndex();
            _searchService = new AssetSearchService(_index);
            _scheduler = new QueuedScheduler();
            _synchronizer = new FakeSynchronizer(_source, _index)
            {
                DeferSynchronizeCompletion = true,
                PublishesViaReplace = true
            };
            _commandHistory = new ProjectCommandHistory();
            _fileCommands = new ProjectFileCommandService(_gateway, _commandHistory);
            _controller = new ZepController(
                _state,
                _gateway,
                _fileCommands,
                _source,
                _index,
                _searchService,
                _tagDefinitions,
                _synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery());
            _controller.BeginPanelUse();
        }

        private ZepController CreateControllerWithDirectSource(
            IDirectFolderAssetSource directSource)
        {
            return new ZepController(
                _state,
                _gateway,
                new ProjectFileCommandService(_gateway),
                _source,
                _index,
                new AssetSearchService(_index),
                _tagDefinitions,
                new FakeSynchronizer(_source, _index),
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery(),
                directSource);
        }

        [Test]
        public void RecoveryMutation_ReadFailureBeforeCommitRefuses()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.ThrowTagVersionUnavailable = true;

            Assert.That(_controller.AddTag(new[] { theme }, "red"), Is.False);
            Assert.That(_gateway.GetTagIds(theme), Is.Empty);
            Assert.That(_fileCommands.CanUndo, Is.False);
            Assert.That(_state.StatusMessage, Does.Contain("Tag state unavailable"));
        }

        [Test]
        public void RecoveryMutation_ReplayPreflightPreservesHistory()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.ThrowTagVersionUnavailable = true;

            Assert.That(_controller.AddTag(new[] { theme }, "red"), Is.False);
            Assert.That(_fileCommands.CanUndo, Is.False);
            _gateway.ThrowTagVersionUnavailable = false;
            Assert.That(_controller.AddTag(new[] { theme }, "red"), Is.True);
            Assert.That(_fileCommands.CanUndo, Is.True);
        }

        [Test]
        public void RecoveryMutation_CommittedAssignmentRemainsSuccessful()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.ThrowTagVersionUnavailableAfterNextSuccessfulWrite = true;

            Assert.That(_controller.AddTag(new[] { theme }, "red"), Is.True);
            Assert.That(_gateway.GetTagIds(theme), Is.EqualTo(new[] { "red" }));
            Assert.That(_fileCommands.CanUndo, Is.True);
            _state.PrimaryPane.SetSearchText("Theme");
            Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));
        }

        [TestCase("rename")]
        [TestCase("delete")]
        [TestCase("reset")]
        [TestCase("undo")]
        [TestCase("redo")]
        public void CommittedTagDefinition_UnavailablePresentationRetainsSuccessAndRecovers(string operation)
        {
            const string filePath = "Assets/Audio/Theme.wav";
            const string folderPath = "Assets/Art";
            var targetId = "red";
            if (operation == "reset")
            {
                Assert.That(_tagDefinitions.TryCreateTag("Custom", ZepTagColor.Purple, out var custom, out _), Is.True);
                targetId = custom.Id;
            }
            _gateway.SetTags(filePath, targetId);
            _gateway.SetTags(folderPath, targetId);
            _controller.Refresh();
            _state.PrimaryPane.TrySetFolderSelection("Assets/Audio");
            if (operation == "undo" || operation == "redo")
            {
                Assert.That(_controller.UpdateTagDefinition(new ZepTag("red", "Crimson", ZepTagColor.Green)), Is.True);
                if (operation == "redo")
                {
                    Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
                }
            }
            var folderPane = _state.OpenWindowPane(ZepPaneSource.RootedFolder(AssetPath.From("Assets")));
            _state.ActivatePane(_state.PrimaryPane);
            var publishedFolder = folderPane.ProjectedAssets.Single(entry => entry.Path == folderPath);
            var publishedFolderMetadata = publishedFolder.FolderTintColor + "|"
                + string.Join(",", publishedFolder.Tags.Select(tag => tag.Id + ":" + tag.Name + ":" + tag.Color));
            var published = _state.PrimaryPane.ProjectedAssets.Select(entry => entry.Path + "|"
                + string.Join(",", entry.Tags.Select(tag => tag.Id + ":" + tag.Name + ":" + tag.Color))).ToArray();
            _gateway.AdvanceTagVersionOnDefinitionOnlyCommit = false;
            _gateway.ThrowTagVersionUnavailableAfterNextSuccessfulWrite = true;

            var succeeded = operation == "rename"
                ? _controller.UpdateTagDefinition(new ZepTag("red", "Crimson", ZepTagColor.Green))
                : operation == "delete" ? _controller.DeleteTagDefinition("red")
                : operation == "reset" ? _controller.ResetTagDefinitions()
                : operation == "undo" ? _controller.Undo(_state.PrimaryPane)
                : _controller.Redo(_state.PrimaryPane);

            Assert.That(succeeded, Is.True, "a committed mutation remains successful while presentation is unavailable");
            Assert.That(_fileCommands.CanUndo, Is.EqualTo(operation != "undo"));
            Assert.That(_fileCommands.CanRedo, Is.EqualTo(operation == "undo"));
            Assert.That(operation == "undo" ? _commandHistory.PeekRedoKind() : _commandHistory.PeekUndoKind(),
                Is.EqualTo(ProjectCommandKind.SetTagDefinitions));
            Assert.That(_controller.StatusMessage, Does.Contain("Tag state unavailable"));
            Assert.That(_state.StatusMessage, Is.EqualTo(_controller.StatusMessage));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(entry => entry.Path + "|"
                + string.Join(",", entry.Tags.Select(tag => tag.Id + ":" + tag.Name + ":" + tag.Color))), Is.EqualTo(published));
            var currentFolder = folderPane.ProjectedAssets.Single(entry => entry.Path == folderPath);
            Assert.That(currentFolder.FolderTintColor + "|"
                + string.Join(",", currentFolder.Tags.Select(tag => tag.Id + ":" + tag.Name + ":" + tag.Color)),
                Is.EqualTo(publishedFolderMetadata), "published folder metadata stays available during recovery");
            var removed = operation == "delete" || operation == "reset";
            if (removed)
            {
                Assert.That(_tagDefinitions.GetOrderedTags().Any(tag => tag.Id == targetId), Is.False);
                Assert.That(_gateway.GetTagIds(AssetPath.From(filePath)), Is.Empty);
                Assert.That(_gateway.GetTagIds(AssetPath.From(folderPath)), Is.Empty);
            }
            else
            {
                Assert.That(_tagDefinitions.GetOrderedTags().Single(tag => tag.Id == targetId).Name,
                    Is.EqualTo(operation == "undo" ? "Red" : "Crimson"));
            }

            _state.PrimaryPane.SetSearchText("Theme");

            Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == filePath).Tags.Select(tag => tag.Id),
                Is.EqualTo(removed ? Array.Empty<string>() : new[] { targetId }));
            var expectedColor = removed ? ZepTagColor.None
                : operation == "undo" ? ZepTagColor.Red : ZepTagColor.Green;
            Assert.That(_controller.GetFolderTintColorForPath(folderPath), Is.EqualTo(expectedColor));
            var searchName = removed ? operation == "reset" ? "custom" : "red"
                : operation == "undo" ? "red" : "crimson";
            Assert.That(_searchService.Search("tag:" + searchName).Any(record => record.AssetPath == filePath), Is.EqualTo(!removed));
        }

        [TestCase("rename")]
        [TestCase("undo")]
        [TestCase("redo")]
        public void CommittedTagDefinition_UnexpectedPresentationFailureStillPropagates(string operation)
        {
            if (operation != "rename")
            {
                Assert.That(_controller.UpdateTagDefinition(new ZepTag("red", "Crimson", ZepTagColor.Green)), Is.True);
                if (operation == "redo")
                {
                    Assert.That(_controller.Undo(_state.PrimaryPane), Is.True);
                }
            }
            _gateway.TagVersionExceptionAfterNextDefinitionCommit = new InvalidOperationException("unexpected post-commit tag failure");

            var failure = Assert.Throws<InvalidOperationException>(() =>
            {
                if (operation == "rename")
                {
                    _controller.UpdateTagDefinition(new ZepTag("red", "Crimson", ZepTagColor.Green));
                }
                else if (operation == "undo")
                {
                    _controller.Undo(_state.PrimaryPane);
                }
                else
                {
                    _controller.Redo(_state.PrimaryPane);
                }
            });

            Assert.That(failure.Message, Is.EqualTo("unexpected post-commit tag failure"));
            Assert.That(_tagDefinitions.GetOrderedTags().Single(tag => tag.Id == "red").Name,
                Is.EqualTo(operation == "undo" ? "Red" : "Crimson"));
            Assert.That(_fileCommands.CanUndo, Is.EqualTo(operation != "undo"));
            Assert.That(_fileCommands.CanRedo, Is.EqualTo(operation == "undo"));
            _gateway.TagVersionException = null;
        }

        [TestCase("rename")]
        [TestCase("delete")]
        [TestCase("reset")]
        public void TagDefinition_PreflightUnavailabilityRefusesWithoutMutation(string operation)
        {
            var definitions = _tagDefinitions.GetOrderedTags().ToArray();
            var original = _gateway.CaptureTagState();
            _gateway.ThrowTagVersionUnavailable = true;

            var succeeded = operation == "rename"
                ? _controller.UpdateTagDefinition(new ZepTag("red", "Crimson", ZepTagColor.Green))
                : operation == "delete" ? _controller.DeleteTagDefinition("red")
                : _controller.ResetTagDefinitions();

            Assert.That(succeeded, Is.False);
            Assert.That(_tagDefinitions.GetOrderedTags(), Is.EqualTo(definitions));
            Assert.That(ProjectTagStateSnapshot.ContentEquals(_gateway.CaptureTagState(), original), Is.True);
            Assert.That(_fileCommands.CanUndo, Is.False);
            Assert.That(_controller.StatusMessage, Does.Contain("Tag state unavailable"));
        }

        [Test]
        public void RecoveryMutation_FailureHandlingIsNarrowAndConditional()
        {
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.TagVersionException = new InvalidOperationException("unexpected tag failure");

            Assert.Throws<InvalidOperationException>(() => _controller.AddTag(new[] { theme }, "red"));
            _gateway.TagVersionException = null;
            Assert.That(_controller.AddTag(new[] { theme }, "red"), Is.True);
        }

        [Test]
        public void RecoveryProjection_UnavailableKeepsPublishedMetadata()
        {
            var artPane = OpenArtPane();
            var primaryPaths = _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path).ToArray();
            var artPaths = artPane.ProjectedAssets.Select(asset => asset.Path).ToArray();

            _gateway.ThrowTagVersionUnavailable = true;
            Assert.DoesNotThrow(() => _state.PrimaryPane.SetSearchText("Hero"));
            Assert.DoesNotThrow(() => artPane.SetSearchText("Icon"));

            Assert.That(_controller.StatusMessage, Does.Contain("Tag state unavailable"));
            Assert.That(_state.StatusMessage, Does.Contain("Tag state unavailable"));
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path), Is.EqualTo(primaryPaths));
            Assert.That(artPane.ProjectedAssets.Select(asset => asset.Path), Is.EqualTo(artPaths));

            _gateway.ThrowTagVersionUnavailable = false;
            _state.PrimaryPane.SetSearchText("Theme");
            Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
        }

        [Test]
        public void RecoveryProjection_PendingMovesSurviveRefusal()
        {
            var primaryPane = _state.PrimaryPane;
            var oldFolderPath = AssetPath.From("Assets/Art");
            var movedFolderPath = AssetPath.From("Assets/RenamedArt");
            var selectedPath = AssetPath.From("Assets/Art/Hero.png");
            var movedSelectedPath = AssetPath.From("Assets/RenamedArt/Hero.png");

            Assert.That(primaryPane.TrySetFolderSelection(oldFolderPath.Value), Is.True);
            primaryPane.SetSelectedAssetPath(selectedPath);
            Assert.That(primaryPane.CurrentFolderPath, Is.EqualTo(oldFolderPath));
            Assert.That(primaryPane.SelectedAssetPath, Is.EqualTo(selectedPath));
            Assert.That(
                primaryPane.ProjectedAssets.Select(asset => asset.Path),
                Has.Member(selectedPath.Value),
                "fixture guard: the selected asset must be visible before refusal");

            _scheduler.RunAll();
            Assert.That(_scheduler.Faults, Is.Empty);
            Assert.That(_scheduler.PendingCount, Is.Zero);
            Assert.That(primaryPane.CurrentFolderPath, Is.EqualTo(oldFolderPath));
            Assert.That(primaryPane.SelectedAssetPath, Is.EqualTo(selectedPath));
            Assert.That(
                primaryPane.ProjectedAssets.Select(asset => asset.Path),
                Has.Member(selectedPath.Value),
                "fixture guard: the selected asset must remain visible before capturing publication");

            var publishedPaths = primaryPane.ProjectedAssets.Select(asset => asset.Path).ToArray();
            var publishedHeroTags = primaryPane.ProjectedAssets
                .Single(asset => asset.Path == selectedPath.Value)
                .Tags
                .Select(tag => tag.Id)
                .ToArray();

            _gateway.MovePath(oldFolderPath.Value, movedFolderPath.Value);
            _controller.HandleAssetChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { movedFolderPath.Value },
                new[] { oldFolderPath.Value }));
            Assert.That(_scheduler.PendingCount, Is.GreaterThan(0));

            _gateway.ThrowTagVersionUnavailable = true;
            _scheduler.RunAll();

            Assert.That(_scheduler.Faults, Is.Empty);
            Assert.That(primaryPane.CurrentFolderPath, Is.EqualTo(oldFolderPath));
            Assert.That(primaryPane.SelectedAssetPath, Is.EqualTo(selectedPath));
            Assert.That(primaryPane.ProjectedAssets.Select(asset => asset.Path), Is.EqualTo(publishedPaths));
            Assert.That(
                primaryPane.ProjectedAssets
                    .Single(asset => asset.Path == selectedPath.Value)
                    .Tags
                    .Select(tag => tag.Id),
                Is.EqualTo(publishedHeroTags));
            Assert.That(_controller.StatusMessage, Does.Contain("Tag state unavailable"));

            _gateway.ThrowTagVersionUnavailable = false;
            primaryPane.SetSearchText("Hero");

            Assert.That(primaryPane.CurrentFolderPath, Is.EqualTo(movedFolderPath));
            Assert.That(primaryPane.SelectedAssetPath, Is.EqualTo(movedSelectedPath));
            Assert.That(_index.TryGetByPath(movedSelectedPath.Value, out var movedRecord), Is.True);
            Assert.That(movedRecord.Guid, Is.EqualTo("hero-guid"));
            Assert.That(_index.TryGetByPath(selectedPath.Value, out _), Is.False);
            Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));

            primaryPane.SetSearchText(string.Empty);
            Assert.That(primaryPane.CurrentFolderPath, Is.EqualTo(movedFolderPath));
            Assert.That(primaryPane.SelectedAssetPath, Is.EqualTo(movedSelectedPath));
            Assert.That(_scheduler.Faults, Is.Empty);
        }

        [Test]
        public void RecoveryProjection_RecoveryRefreshesAllPanes()
        {
            var artPane = OpenArtPane();
            _gateway.ThrowTagVersionUnavailable = true;
            _state.PrimaryPane.SetSearchText("Theme");
            artPane.SetSearchText("Icon");

            _gateway.ThrowTagVersionUnavailable = false;
            _state.PrimaryPane.SetSearchText("Hero");

            Assert.That(
                _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path),
                Has.Member("Assets/Art/Hero.png"));
            Assert.That(
                artPane.ProjectedAssets.Select(asset => asset.Path),
                Has.Member("Assets/Art/UI/Icon.png"));
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));
        }

        [Test]
        public void RecoveryProjection_FailedMutationRepaintIsSafe()
        {
            var publishedPaths = _state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path).ToArray();
            var theme = AssetPath.From("Assets/Audio/Theme.wav");
            _gateway.TagAssignmentPreparationFailureMessage = "Tag assignments are unavailable.";
            _gateway.ThrowTagVersionUnavailable = true;

            Assert.DoesNotThrow(() =>
                Assert.That(_controller.AddTag(new[] { theme }, "red"), Is.False));

            Assert.That(_fileCommands.CanUndo, Is.False);
            Assert.That(_gateway.GetTagIds(theme), Is.Empty);
            Assert.That(_state.PrimaryPane.ProjectedAssets.Select(asset => asset.Path), Is.EqualTo(publishedPaths));
            Assert.That(_state.StatusMessage, Does.Contain("Tag state unavailable"));
        }

        [Test]
        public void RecoveryProjection_DoesNotSwallowOtherFailures()
        {
            _gateway.ThrowTagVersionUnavailable = true;
            _state.PrimaryPane.SetSearchText("Hero");
            _gateway.ThrowTagVersionUnavailable = false;
            _state.PrimaryPane.SetSearchText("Icon");

            Assert.That(_controller.StatusMessage, Is.EqualTo("Ready"));
            Assert.That(_state.StatusMessage, Is.EqualTo("Ready"));

            _gateway.TagVersionException = new InvalidOperationException("unexpected tag failure");
            Assert.Throws<InvalidOperationException>(() => _state.PrimaryPane.SetSearchText("Theme"));
        }

        private sealed class FakeDirectFolderSource : IDirectFolderAssetSource
        {
            private readonly Dictionary<string, DirectFolderObservation> _observations =
                new Dictionary<string, DirectFolderObservation>(
                    StringComparer.OrdinalIgnoreCase);

            internal List<string> ObservedPaths { get; } = new List<string>();

            internal void SetComplete(
                string folderPath,
                params DirectFolderEntry[] entries)
            {
                Set(folderPath, entries, isComplete: true);
            }

            internal void SetIncomplete(string folderPath)
            {
                Set(folderPath, Array.Empty<DirectFolderEntry>(), isComplete: false);
            }

            public DirectFolderObservation Observe(AssetPath folderPath)
            {
                ObservedPaths.Add(folderPath.Value);
                return _observations.TryGetValue(folderPath.Value, out var observation)
                    ? observation
                    : new DirectFolderObservation(
                        folderPath,
                        Array.Empty<DirectFolderEntry>(),
                        isComplete: false);
            }

            private void Set(
                string folderPath,
                IReadOnlyList<DirectFolderEntry> entries,
                bool isComplete)
            {
                var typedFolderPath = AssetPath.From(folderPath);
                _observations[typedFolderPath.Value] = new DirectFolderObservation(
                    typedFolderPath,
                    entries,
                    isComplete);
            }
        }

        private ZepController CreateScanFailureController(
            IAssetRecordSource source,
            IAssetIndexSynchronizer synchronizer,
            IDirectFolderAssetSource directFolderSource = null)
        {
            return new ZepController(
                _state,
                _gateway,
                _fileCommands,
                source,
                _index,
                _searchService,
                _tagDefinitions,
                synchronizer,
                _scheduler,
                new NoOpShellReveal(),
                new EmptyAddressablesQuery(),
                directFolderSource);
        }

        private sealed class OutcomeSynchronizer : IAssetIndexSynchronizationResults
        {
            private readonly AssetIndexSynchronizationResult _result;

            internal OutcomeSynchronizer(AssetIndexSynchronizationResult result)
            {
                _result = result;
            }

            public void Synchronize(Action onComplete = null)
            {
                onComplete?.Invoke();
            }

            public void SynchronizeWithOutcome(Action<AssetIndexSynchronizationResult> onComplete)
            {
                onComplete?.Invoke(_result);
            }

            public void SynchronizeAsync(Action onFullScanStarted, Action onComplete)
            {
                onFullScanStarted?.Invoke();
                onComplete?.Invoke();
            }

            public void SynchronizeAsyncWithOutcome(
                Action onFullScanStarted,
                Action<AssetIndexSynchronizationResult> onComplete)
            {
                onFullScanStarted?.Invoke();
                onComplete?.Invoke(_result);
            }

            public void InvalidateInProgressScan()
            {
            }

            public bool HasBackgroundJob => false;

            public IReadOnlyList<AssetFolderRecord> GetFolderRecordsForProjection()
            {
                return Array.Empty<AssetFolderRecord>();
            }

            public void Dispose()
            {
            }
        }

        private sealed class MutableRecordSource : IAssetRecordSource
        {
            internal MutableRecordSource(
                IEnumerable<AssetRecord> records,
                IEnumerable<string> folders)
            {
                Records = new List<AssetRecord>(records);
                Folders = folders.Select(AssetPath.From).ToList();
            }

            internal List<AssetRecord> Records { get; }

            internal List<AssetPath> Folders { get; }

            internal Action<AssetPath> BuildRecordCallback { get; set; }

            public bool IsThreadSafe => true;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                return Records;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
            {
                return Folders
                    .Select(folderPath => new AssetFolderRecord(folderPath))
                    .ToList();
            }

            public AssetRecord BuildRecord(AssetPath assetPath)
            {
                BuildRecordCallback?.Invoke(assetPath);
                return Records.FirstOrDefault(record =>
                    string.Equals(
                        record.AssetPath,
                        assetPath.Value,
                        StringComparison.OrdinalIgnoreCase));
            }

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath)
            {
                return ContainsFolder(folderPath.Value)
                    ? new AssetFolderRecord(folderPath)
                    : null;
            }

            internal void MovePath(string oldPath, string newPath)
            {
                for (var i = 0; i < Folders.Count; i++)
                {
                    if (IsSameOrDescendant(Folders[i].Value, oldPath))
                    {
                        Folders[i] = AssetPath.From(
                            newPath + Folders[i].Value.Substring(oldPath.Length));
                    }
                }

                for (var i = 0; i < Records.Count; i++)
                {
                    var record = Records[i];
                    if (!IsSameOrDescendant(record.AssetPath, oldPath))
                    {
                        continue;
                    }

                    var movedPath = newPath + record.AssetPath.Substring(oldPath.Length);
                    var movedName = string.Equals(
                        record.AssetPath,
                        oldPath,
                        StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(movedPath)
                            : record.Name;
                    Records[i] = new AssetRecord(
                        record.Guid,
                        movedPath,
                        movedName,
                        record.TypeName,
                        record.TagSearchText);
                }
            }

            internal void RemovePath(string path)
            {
                Records.RemoveAll(record => IsSameOrDescendant(record.AssetPath, path));
                Folders.RemoveAll(folder => IsSameOrDescendant(folder.Value, path));
            }

            internal bool ContainsFolder(string path)
            {
                return Folders.Any(folder =>
                    string.Equals(folder.Value, path, StringComparison.OrdinalIgnoreCase));
            }

            private static bool IsSameOrDescendant(string path, string root)
            {
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    || (path.Length > root.Length
                        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && path[root.Length] == '/');
            }
        }

        private sealed class GatedTagScanSource : IAssetRecordSource, IDisposable
        {
            private readonly MutableRecordSource _mainThreadSource;
            private readonly object _lifecycle = new object();
            private readonly System.Threading.ManualResetEventSlim _entered = new System.Threading.ManualResetEventSlim(false);
            private readonly System.Threading.ManualResetEventSlim _release = new System.Threading.ManualResetEventSlim(false);
            private readonly System.Threading.ManualResetEventSlim _exited = new System.Threading.ManualResetEventSlim(false);
            private AssetRecord[] _capturedRecords = Array.Empty<AssetRecord>();
            private AssetFolderRecord[] _capturedFolders = Array.Empty<AssetFolderRecord>();
            private bool _started;
            private bool _disposed;

            internal GatedTagScanSource(MutableRecordSource mainThreadSource)
            {
                _mainThreadSource = mainThreadSource;
            }

            public bool IsThreadSafe => true;
            internal bool HasEntered => _entered.IsSet;
            internal IReadOnlyList<AssetRecord> CapturedRecords => _capturedRecords;

            internal void Release() => _release.Set();

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                _capturedRecords = _mainThreadSource.Records.ToArray();
                _capturedFolders = _mainThreadSource.GetFolderRecords().ToArray();
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                lock (_lifecycle)
                {
                    // A worker dispatched before disposal may enter afterwards.
                    // It must then avoid all disposed synchronization primitives.
                    if (_disposed) return _capturedRecords;
                    _started = true;
                    _entered.Set();
                }
                try
                {
                    _release.Wait();
                    return _capturedRecords;
                }
                finally
                {
                    lock (_lifecycle) _exited.Set();
                }
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => _capturedFolders;
            public AssetRecord BuildRecord(AssetPath assetPath) => _mainThreadSource.BuildRecord(assetPath);
            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => _mainThreadSource.BuildFolderRecord(folderPath);

            public void Dispose()
            {
                bool started;
                lock (_lifecycle)
                {
                    if (_disposed) return;
                    _disposed = true;
                    started = _started;
                    _release.Set();
                }
                // The worker may still run after controller disposal detaches its poll.
                // Join its use of these resources even on a failed test assertion.
                if (started && !_exited.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Tag scan worker did not leave the gate.");
                lock (_lifecycle)
                {
                    _entered.Dispose();
                    _release.Dispose();
                    _exited.Dispose();
                }
            }
        }

        private sealed class FakeSynchronizer : IAssetIndexSynchronizer, ICatalogDirtyMarker, IAssetScanChangeRecorder
        {
            private readonly MutableRecordSource _source;
            private readonly InMemoryAssetIndex _index;
            private readonly List<Action> _deferredCompletions = new List<Action>();

            internal FakeSynchronizer(
                MutableRecordSource source,
                InMemoryAssetIndex index)
            {
                _source = source;
                _index = index;
            }

            internal bool DeferSynchronizeCompletion { get; set; }

            // A completed authoritative pass that publishes nothing because it
            // found nothing new. The operation still runs and still completes —
            // only the catalog is left exactly as the operation found it.
            internal bool LeavesCatalogUnchanged { get; set; }

            // Publish the way the real synchronizer does on its cache-hydration
            // and authoritative-scan legs: one InMemoryAssetIndex.Replace with the
            // whole snapshot, rather than Clear + per-record Upsert.
            internal bool PublishesViaReplace { get; set; }

            // The port member, as a settable knob: the guard under test asks the
            // synchronizer whether its follow-up retype job is still outstanding, and
            // this fake runs no job. public because it implements an interface member.
            public bool HasBackgroundJob { get; set; }

            internal Action SnapshotPublished { get; set; }

            internal bool ThrowProjectionFolderRecordsOnce { get; set; }

            internal int EnumerationCount { get; private set; }

            internal int GetFolderRecordsForProjectionCount { get; private set; }

            internal void CompleteDeferredSynchronization()
            {
                DeferSynchronizeCompletion = false;
                PublishSnapshot();
                var completions = _deferredCompletions.ToArray();
                _deferredCompletions.Clear();
                for (var i = 0; i < completions.Length; i++)
                {
                    completions[i]?.Invoke();
                }
            }

            internal List<ProjectAssetChanges> RecordedConcreteChanges { get; } =
                new List<ProjectAssetChanges>();

            internal Action<ProjectAssetChanges> RecordConcreteChangesCallback { get; set; }

            internal List<string> RecordedUnresolvedPaths { get; } = new List<string>();

            public void RecordUnresolvedChanges(IReadOnlyList<string> paths)
            {
                RecordedUnresolvedPaths.AddRange(paths);
            }

            public void RecordConcreteChanges(ProjectAssetChanges changes)
            {
                RecordConcreteChangesCallback?.Invoke(changes);
                RecordedConcreteChanges.Add(changes);
            }

            public void Synchronize(Action onComplete = null)
            {
                if (DeferSynchronizeCompletion)
                {
                    if (onComplete != null)
                    {
                        _deferredCompletions.Add(onComplete);
                    }

                    return;
                }

                PublishSnapshot();
                onComplete?.Invoke();
            }

            private void PublishSnapshot()
            {
                EnumerationCount++;
                if (LeavesCatalogUnchanged)
                {
                    return;
                }

                if (PublishesViaReplace)
                {
                    _index.Replace(_source.Records, _source.GetFolderRecords());
                    SnapshotPublished?.Invoke();
                    return;
                }

                _index.Clear();
                foreach (var record in _source.Records)
                {
                    _index.Upsert(record);
                }
            }

            public void SynchronizeAsync(Action onFullScanStarted, Action onComplete)
            {
                onFullScanStarted?.Invoke();
                Synchronize(onComplete);
            }

            public void InvalidateInProgressScan()
            {
            }

            public void Dispose()
            {
                IsDisposed = true;
            }

            public int PersistentDirtyCount { get; private set; }

            public bool IsDisposed { get; private set; }

            public void MarkPersistentStateDirty()
            {
                PersistentDirtyCount++;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecordsForProjection()
            {
                GetFolderRecordsForProjectionCount++;
                if (ThrowProjectionFolderRecordsOnce)
                {
                    ThrowProjectionFolderRecordsOnce = false;
                    throw new InvalidOperationException(
                        "transient folder projection failure");
                }

                return _source.GetFolderRecords();
            }
        }

        private sealed class FakeGateway : IProjectAssetGateway, IProjectTagDefinitionGateway, IProjectTagAssignmentPreparationGateway
        {
            private readonly MutableRecordSource _source;
            private readonly Dictionary<string, IReadOnlyList<string>> _tagsByPath =
                new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, IReadOnlyList<string>> _stagedTagsByPath;
            private readonly Dictionary<string, string> _guidsByFolder =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _payloadOrigins =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _payloadGuids =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, AssetRecord> _storedRecords =
                new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _createdPayloadFolders = new List<string>();
            private readonly List<string> _createdPayloadFiles = new List<string>();

            internal FakeGateway(MutableRecordSource source)
            {
                _source = source;
            }

            private int _tagVersion = 1;
            private bool _pendingTagVersionUnavailableRead;

            internal bool ThrowTagVersionUnavailable { get; set; }

            internal bool ThrowTagVersionUnavailableAfterNextSuccessfulWrite { get; set; }

            internal Exception TagVersionException { get; set; }

            internal Exception TagVersionExceptionAfterNextDefinitionCommit { get; set; }

            public int TagVersion
            {
                get
                {
                    if (TagVersionException != null)
                    {
                        throw TagVersionException;
                    }

                    if (_pendingTagVersionUnavailableRead)
                    {
                        _pendingTagVersionUnavailableRead = false;
                        throw new ProjectTagStateUnavailableException(
                            "Tag state unavailable");
                    }

                    if (ThrowTagVersionUnavailable)
                    {
                        throw new ProjectTagStateUnavailableException(
                            "Tag state unavailable");
                    }

                    return _tagVersion;
                }
                private set => _tagVersion = value;
            }

            internal IReadOnlyDictionary<string, string> UndoPayloadOrigins => _payloadOrigins;


            internal int BeginBatchCalls { get; private set; }

            internal int EndBatchCalls { get; private set; }

            internal int OpenBatchDepth { get; private set; }

            internal int MaxOpenBatchDepth { get; private set; }

            public bool Exists(AssetPath assetPath)
            {
                return IsFolder(assetPath) || _source.BuildRecord(assetPath) != null;
            }

            public bool IsFolder(AssetPath assetPath)
            {
                return _source.ContainsFolder(assetPath.Value);
            }

            // Read-only-ness is a filesystem fact this in-memory fake cannot
            // observe, so a fixture that needs one item to refuse declares it.
            internal HashSet<string> ReadOnlyPaths { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool CanWrite(AssetPath assetPath)
            {
                return !ReadOnlyPaths.Contains(assetPath.Value)
                    && AssetPathClassification.IsWritablePath(assetPath);
            }

            public bool CanTag(AssetPath assetPath)
            {
                return Exists(assetPath);
            }

            public bool EnsureFolder(AssetPath folderPath)
            {
                if (!_source.ContainsFolder(folderPath.Value))
                {
                    _source.Folders.Add(folderPath);
                }

                return true;
            }

            public ProjectFileOperationResult CreateFolder(
                AssetPath parentFolderPath,
                string folderName)
            {
                var path = parentFolderPath.Value + "/" + folderName;
                _source.Folders.Add(AssetPath.From(path));
                return ProjectFileOperationResult.Success(path, parentFolderPath.Value);
            }

            public ProjectFileOperationResult Move(
                AssetPath sourcePath,
                AssetPath destinationPath,
                bool allowOverwriteWithUniquePath)
            {
                // The real gateway refuses to move onto something that is
                // already there. This fake used to absorb it — MovePath simply
                // rewrote the record onto the occupied path and reported a
                // success — which is why a whole class of move and rename
                // collisions could not be caught by a test.
                if (!allowOverwriteWithUniquePath
                    && !string.Equals(
                        sourcePath.Value,
                        destinationPath.Value,
                        StringComparison.OrdinalIgnoreCase)
                    && Exists(destinationPath))
                {
                    return ProjectFileOperationResult.Failure(
                        "Move Asset failed: '" + destinationPath.Value + "' already exists.",
                        sourcePath.Value);
                }

                var guid = GetGuid(sourcePath);
                MovePath(sourcePath.Value, destinationPath.Value);
                if (!string.IsNullOrEmpty(guid) && IsFolder(destinationPath))
                {
                    _guidsByFolder[destinationPath.Value] = guid;
                }

                return ProjectFileOperationResult.Success(
                    destinationPath.Value,
                    sourcePath.Value);
            }

            public ProjectFileOperationResult Copy(
                AssetPath sourcePath,
                AssetPath destinationPath,
                bool allowOverwriteWithUniquePath)
            {
                var record = _source.BuildRecord(sourcePath);
                if (record == null)
                {
                    return ProjectFileOperationResult.Failure(
                        "'" + sourcePath.Value + "' could not be found.");
                }

                // The real gateway never overwrites: it makes the destination
                // unique, which is what a Duplicate relies on — it asks to copy
                // onto the source's own path.
                var destination = destinationPath.Value;
                if (allowOverwriteWithUniquePath)
                {
                    var extension = Path.GetExtension(destination);
                    var stem = destination.Substring(
                        0,
                        destination.Length - extension.Length);
                    for (var suffix = 1; Exists(AssetPath.From(destination)); suffix++)
                    {
                        destination = stem + " " + suffix + extension;
                    }
                }

                _source.Records.Add(new AssetRecord(
                    "copy-" + Guid.NewGuid().ToString("N"),
                    destination,
                    Path.GetFileNameWithoutExtension(destination),
                    record.TypeName));
                return ProjectFileOperationResult.Success(
                    destination,
                    sourcePath.Value);
            }

            public ProjectFileOperationResult Delete(AssetPath assetPath)
            {
                RemovePath(assetPath.Value);
                return ProjectFileOperationResult.Success(string.Empty, assetPath.Value);
            }

            internal string CreateOwnedPayloadStorage(bool isFolder)
            {
                var storagePath = Path.Combine(
                    Path.GetTempPath(),
                    "ZepPayload",
                    Guid.NewGuid().ToString("N"));
                // Only newly generated storage belongs to this fake. Explicit
                // history paths are handled by the overload without writing.
                if (isFolder)
                {
                    _createdPayloadFolders.Add(storagePath);
                    Directory.CreateDirectory(storagePath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(storagePath));
                    using (new FileStream(storagePath, FileMode.CreateNew, FileAccess.Write))
                    {
                        _createdPayloadFiles.Add(storagePath);
                    }
                }

                return storagePath;
            }

            internal void DeleteCreatedPayloadStorage()
            {
                foreach (var file in _createdPayloadFiles)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }

                _createdPayloadFiles.Clear();
                foreach (var folder in _createdPayloadFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }

                _createdPayloadFolders.Clear();
            }

            public ProjectFileOperationResult StoreUndoPayload(
                AssetPath assetPath,
                AssetPath payloadPath = default)
            {
                if (payloadPath.IsEmpty)
                {
                    payloadPath = AssetPath.From(CreateOwnedPayloadStorage(IsFolder(assetPath)));
                }

                _payloadOrigins[payloadPath.Value] = assetPath.Value;
                // The payload keeps its .meta, so the identity a recorded delete
                // validates against on undo travels with it.
                _payloadGuids[payloadPath.Value] = GetGuid(assetPath);
                _storedRecords[payloadPath.Value] = _source.BuildRecord(assetPath);
                var isFolder = IsFolder(assetPath);
                RemovePath(assetPath.Value);
                return ProjectFileOperationResult.Success(
                    payloadPath.Value,
                    assetPath.Value);
            }




            internal string RestoreUndoPayloadFailureMessage { get; set; }

            public ProjectFileOperationResult RestoreUndoPayload(
                AssetPath payloadPath,
                AssetPath destinationPath)
            {
                if (!string.IsNullOrEmpty(RestoreUndoPayloadFailureMessage))
                {
                    return ProjectFileOperationResult.Failure(
                        RestoreUndoPayloadFailureMessage, destinationPath, payloadPath);
                }

                if (_storedRecords.TryGetValue(payloadPath.Value, out var storedRecord)
                    && storedRecord != null)
                {
                    _source.Records.Add(new AssetRecord(
                        storedRecord.Guid,
                        destinationPath.Value,
                        Path.GetFileNameWithoutExtension(destinationPath.Value),
                        storedRecord.TypeName));
                    _storedRecords.Remove(payloadPath.Value);
                }
                else
                {
                    _source.Folders.Add(destinationPath);
                }

                _payloadOrigins.Remove(payloadPath.Value);

                return ProjectFileOperationResult.Success(
                    destinationPath.Value,
                    payloadPath.Value);
            }

            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath)
            {
                _payloadOrigins.Remove(payloadPath.Value);
                _storedRecords.Remove(payloadPath.Value);

                return ProjectFileOperationResult.Success(string.Empty, payloadPath.Value);
            }

            internal string TagAssignmentPreparationFailureMessage { get; set; }

            internal int TagAssignmentPreparationCalls { get; private set; }

            internal void StageExternalTags(string path, params string[] tagIds)
            {
                if (_stagedTagsByPath == null)
                {
                    _stagedTagsByPath = new Dictionary<string, IReadOnlyList<string>>(
                        _tagsByPath,
                        StringComparer.OrdinalIgnoreCase);
                }

                if (tagIds == null || tagIds.Length == 0)
                {
                    _stagedTagsByPath.Remove(path);
                    return;
                }

                _stagedTagsByPath[path] = tagIds.ToArray();
            }

            public ProjectFileOperationResult PrepareTagAssignmentMutation()
            {
                TagAssignmentPreparationCalls++;
                if (!string.IsNullOrEmpty(TagAssignmentPreparationFailureMessage))
                {
                    return ProjectFileOperationResult.Failure(TagAssignmentPreparationFailureMessage);
                }

                if (_stagedTagsByPath != null)
                {
                    if (!AssignmentContentsEqual(_tagsByPath, _stagedTagsByPath))
                    {
                        _tagsByPath.Clear();
                        foreach (var entry in _stagedTagsByPath)
                        {
                            _tagsByPath[entry.Key] = entry.Value.ToArray();
                        }

                        TagVersion++;
                    }

                    _stagedTagsByPath = null;
                }

                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }

            internal int TagAssignmentReadCalls { get; private set; }

            internal Action<AssetPath, int> TagAssignmentReadCallback { get; set; }

            internal void ResetTagAssignmentReadCalls()
            {
                TagAssignmentReadCalls = 0;
            }

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath)
            {
                TagAssignmentReadCalls++;
                TagAssignmentReadCallback?.Invoke(assetPath, TagAssignmentReadCalls);
                return _tagsByPath.TryGetValue(assetPath.Value, out var tagIds)
                    ? tagIds
                    : Array.Empty<string>();
            }

            // Path-keyed store: the GUID adds nothing, but a caller that passes
            // one must still get the same answer. Delegating here deliberately
            // invokes the instance-local read control once.
            public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) =>
                GetTagIds(assetPath);

            public ProjectFileOperationResult SetTagIds(
                IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath)
            {
                foreach (var entry in tagIdsByAssetPath)
                {
                    _tagsByPath[entry.Key.Value] = entry.Value;
                }

                TagVersion++;
                if (ThrowTagVersionUnavailableAfterNextSuccessfulWrite)
                {
                    ThrowTagVersionUnavailableAfterNextSuccessfulWrite = false;
                    _pendingTagVersionUnavailableRead = true;
                }

                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }

            public bool Open(AssetPath assetPath)
            {
                return Exists(assetPath);
            }

            public void Reveal(AssetPath assetPath)
            {
            }

            public void BeginBatch()
            {
                BeginBatchCalls++;
                OpenBatchDepth++;
                if (OpenBatchDepth > MaxOpenBatchDepth)
                {
                    MaxOpenBatchDepth = OpenBatchDepth;
                }
            }

            internal Action EndBatchCallback { get; set; }

            public void EndBatch()
            {
                EndBatchCalls++;
                OpenBatchDepth--;
                EndBatchCallback?.Invoke();
            }

            public string GetGuid(AssetPath assetPath)
            {
                var record = _source.BuildRecord(assetPath);
                if (record != null)
                {
                    return record.Guid;
                }

                if (_payloadGuids.TryGetValue(assetPath.Value, out var payloadGuid)
                    && !string.IsNullOrEmpty(payloadGuid))
                {
                    return payloadGuid;
                }

                if (!IsFolder(assetPath))
                {
                    return string.Empty;
                }

                if (!_guidsByFolder.TryGetValue(assetPath.Value, out var guid))
                {
                    guid = "folder-" + Guid.NewGuid().ToString("N");
                    _guidsByFolder[assetPath.Value] = guid;
                }

                return guid;
            }

            // Unused by this fixture: an unknown GUID makes commands fall back
            // to their recorded path, which is the pre-existing behaviour.
            public string GetPathForGuid(string assetGuid) => string.Empty;

            internal string PhysicalProjectRoot { get; set; }

            public string ResolveAbsolutePath(AssetPath assetPath)
            {
                return PhysicalProjectRoot == null
                    ? string.Empty
                    : Path.Combine(PhysicalProjectRoot, assetPath.Value);
            }

            internal void SetTags(string path, params string[] tagIds)
            {
                _tagsByPath[path] = tagIds;
            }

            // Production keeps tag DEFINITIONS in ZepTagSettings' store —
            // the very instance the controller projects from — and ASSIGNMENTS in a
            // GUID-keyed file that GetTagIds reads. The fake mirrors that split, so
            // a definition edit really does reach the projection's definition
            // source, and a definition delete really does drop the id from the
            // assignment store the panel then re-reads.
            internal TagDefinitionStore TagDefinitions { get; set; }

            internal bool AdvanceTagVersionOnDefinitionOnlyCommit { get; set; } = true;

            public ProjectTagStateSnapshot CaptureTagState()
            {
                return new ProjectTagStateSnapshot(
                    TagDefinitions.GetOrderedTags(),
                    CaptureAssignmentsByGuid());
            }

            public ProjectFileOperationResult CompareAndSetTagState(
                ProjectTagStateSnapshot expected,
                ProjectTagStateSnapshot desired)
            {
                if (!ProjectTagStateSnapshot.ContentEquals(CaptureTagState(), expected))
                {
                    return ProjectFileOperationResult.Failure(
                        "Tags changed outside this operation.");
                }

                var assignmentsChanged = !AssignmentContentsEqual(
                    CaptureAssignmentsByGuid(),
                    desired.AssignmentsByGuid);
                TagDefinitions.ReplaceAll(desired.Definitions);
                ApplyAssignmentsByGuid(desired.AssignmentsByGuid);
                if (assignmentsChanged || AdvanceTagVersionOnDefinitionOnlyCommit)
                {
                    TagVersion++;
                }

                if (ThrowTagVersionUnavailableAfterNextSuccessfulWrite)
                {
                    ThrowTagVersionUnavailableAfterNextSuccessfulWrite = false;
                    _pendingTagVersionUnavailableRead = true;
                }

                if (TagVersionExceptionAfterNextDefinitionCommit != null)
                {
                    TagVersionException = TagVersionExceptionAfterNextDefinitionCommit;
                    TagVersionExceptionAfterNextDefinitionCommit = null;
                }

                return ProjectFileOperationResult.Success(
                    string.Empty,
                    string.Empty,
                    tagDefinitionsChanged: true);
            }

            internal static bool AssignmentContentsEqual(
                IReadOnlyDictionary<string, IReadOnlyList<string>> left,
                IReadOnlyDictionary<string, IReadOnlyList<string>> right)
            {
                if (ReferenceEquals(left, right))
                {
                    return true;
                }

                if (left == null || right == null || left.Count != right.Count)
                {
                    return false;
                }

                foreach (var leftEntry in left)
                {
                    IReadOnlyList<string> rightIds = null;
                    var found = false;
                    foreach (var rightEntry in right)
                    {
                        if (!string.Equals(leftEntry.Key, rightEntry.Key, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        rightIds = rightEntry.Value;
                        found = true;
                        break;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    if (leftEntry.Value == null || rightIds == null)
                    {
                        if (leftEntry.Value != rightIds)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (leftEntry.Value.Count != rightIds.Count)
                    {
                        return false;
                    }

                    for (var i = 0; i < leftEntry.Value.Count; i++)
                    {
                        if (!string.Equals(leftEntry.Value[i], rightIds[i], StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            private IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureAssignmentsByGuid()
            {
                var assignments = new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _tagsByPath)
                {
                    var guid = GetGuid(AssetPath.From(entry.Key));
                    // A path with no identity yet still has to round-trip, so it
                    // stands in for its own GUID.
                    assignments[string.IsNullOrEmpty(guid) ? entry.Key : guid] = entry.Value;
                }

                return assignments;
            }

            private void ApplyAssignmentsByGuid(
                IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
            {
                // The reverse map must cover the whole project, not just the
                // currently tagged paths: an undo restores assignments for GUIDs
                // that are no longer tagged, and those are exactly the ones that
                // have to find their way home.
                var pathByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < _source.Records.Count; i++)
                {
                    pathByGuid[_source.Records[i].Guid] = _source.Records[i].AssetPath;
                }

                foreach (var folder in _guidsByFolder)
                {
                    pathByGuid[folder.Value] = folder.Key;
                }

                _tagsByPath.Clear();
                if (assignments == null)
                {
                    return;
                }

                foreach (var entry in assignments)
                {
                    if (entry.Value == null || entry.Value.Count == 0)
                    {
                        continue;
                    }

                    _tagsByPath[
                        pathByGuid.TryGetValue(entry.Key, out var path) ? path : entry.Key] =
                        entry.Value;
                }
            }

            internal void MovePath(string oldPath, string newPath)
            {
                var movedTags = _tagsByPath
                    .Where(entry => IsSameOrDescendant(entry.Key, oldPath))
                    .ToArray();
                foreach (var entry in movedTags)
                {
                    _tagsByPath.Remove(entry.Key);
                    _tagsByPath[newPath + entry.Key.Substring(oldPath.Length)] = entry.Value;
                }

                if (_guidsByFolder.TryGetValue(oldPath, out var guid))
                {
                    _guidsByFolder.Remove(oldPath);
                    _guidsByFolder[newPath] = guid;
                }

                _source.MovePath(oldPath, newPath);
            }

            internal void RemovePath(string path)
            {
                var tagPaths = _tagsByPath.Keys
                    .Where(candidate => IsSameOrDescendant(candidate, path))
                    .ToArray();
                for (var i = 0; i < tagPaths.Length; i++)
                {
                    _tagsByPath.Remove(tagPaths[i]);
                }

                _source.RemovePath(path);
            }

            private static bool IsSameOrDescendant(string path, string root)
            {
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    || (path.Length > root.Length
                        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && path[root.Length] == '/');
            }
        }

        // Mirrors UnityDeferredCallScheduler: a throwing callback is caught and
        // reported (there, to the console; here, into Faults) and the remaining
        // callbacks still run. Tests that assert on a fault read Faults.
        private sealed class QueuedScheduler : IDeferredCallScheduler
        {
            private readonly List<Action> _actions = new List<Action>();

            internal List<Exception> Faults { get; } = new List<Exception>();

            public void Schedule(Action action)
            {
                if (action != null && !_actions.Contains(action))
                {
                    _actions.Add(action);
                }
            }

            public void Cancel(Action action)
            {
                _actions.Remove(action);
            }

            internal int PendingCount => _actions.Count;

            // A bounded drain: a test for an UNBOUNDED reschedule cannot use
            // RunAll, which would never return in the red state.
            internal void RunUpTo(int maxActions)
            {
                for (var i = 0; i < maxActions && _actions.Count > 0; i++)
                {
                    var action = _actions[0];
                    _actions.RemoveAt(0);
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        Faults.Add(exception);
                    }
                }
            }

            internal void RunAll()
            {
                while (_actions.Count > 0)
                {
                    var action = _actions[0];
                    _actions.RemoveAt(0);
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        Faults.Add(exception);
                    }
                }
            }
        }

        private sealed class NoOpShellReveal : IShellRevealService
        {
            public void Reveal(string absolutePath)
            {
            }
        }

        // Faults the deferred worker task the first N times it is expanded, then
        // reports Hero as addressable — the observable proof that a later attempt
        // ran to completion and its whole result was consumed.
        private sealed class FaultingAddressablesQuery : IAddressablesQuery
        {
            public bool IsInstalled => true;

            public event Action Changed
            {
                add { }
                remove { }
            }

            private int _faultCount;

            internal FaultingAddressablesQuery(int faultCount)
            {
                _faultCount = faultCount;
            }

            internal int ExpandAttempts { get; private set; }

            internal int FaultCount
            {
                get => _faultCount;
                set => _faultCount = value;
            }

            internal bool ThrowOnCapture { get; set; }

            public AddressablesCapture Capture()
            {
                if (ThrowOnCapture)
                {
                    throw new InvalidOperationException("addressables capture failed");
                }

                return new AddressablesCapture(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    isComplete: true);
            }

            public HashSet<string> Expand(
                AddressablesCapture capture,
                IReadOnlyList<AssetRecord> records)
            {
                ExpandAttempts++;
                if (ExpandAttempts <= _faultCount)
                {
                    throw new InvalidOperationException("addressables expansion failed");
                }

                return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hero-guid" };
            }
        }

        private sealed class GatedStartupAddressablesQuery : IAddressablesQuery, IDisposable
        {
            public bool IsInstalled => true;

            public event Action Changed
            {
                add { }
                remove { }
            }

            internal readonly System.Threading.ManualResetEventSlim Started = new System.Threading.ManualResetEventSlim();
            internal readonly System.Threading.ManualResetEventSlim Release = new System.Threading.ManualResetEventSlim();
            private int _expandCount;
            internal int CaptureCount { get; private set; }
            internal int ExpandCount => System.Threading.Volatile.Read(ref _expandCount);

            public AddressablesCapture Capture()
            {
                CaptureCount++;
                return new AddressablesCapture(Array.Empty<string>(), Array.Empty<string>(), isComplete: true);
            }

            public HashSet<string> Expand(AddressablesCapture capture, IReadOnlyList<AssetRecord> records)
            {
                System.Threading.Interlocked.Increment(ref _expandCount);
                Started.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("test did not release the startup worker");
                }
                return new HashSet<string>(StringComparer.Ordinal);
            }

            public void Dispose()
            {
                Started.Dispose();
                Release.Dispose();
            }
        }

        private sealed class RecordingAddressablesQuery : IAddressablesQuery
        {
            public bool IsInstalled => false;

            public event Action Changed
            {
                add { }
                remove { }
            }

            internal IReadOnlyList<string> FirstExpandedPaths { get; private set; }
            internal IReadOnlyList<AssetRecord> FirstExpandedRecords { get; private set; }

            internal int CaptureCount { get; private set; }

            public AddressablesCapture Capture()
            {
                CaptureCount++;
                return new AddressablesCapture(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    isComplete: true);
            }

            public HashSet<string> Expand(
                AddressablesCapture capture,
                IReadOnlyList<AssetRecord> records)
            {
                if (FirstExpandedPaths == null)
                {
                    FirstExpandedRecords = records.ToArray();
                    FirstExpandedPaths = records
                        .Select(record => record.AssetPath)
                        .ToArray();
                }

                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        // Models the live port: Capture snapshots the current entry set on the
        // editor thread, Expand (on the worker) turns that snapshot into GUIDs
        // and can be held open to model a run in flight.
        private sealed class MutableAddressablesQuery : IAddressablesQuery
        {
            private int _expandAttempts;

            internal HashSet<string> Guids { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal List<string> FolderPaths { get; } = new List<string>();

            internal System.Threading.ManualResetEventSlim HoldExpand { get; set; }

            internal int CaptureCount { get; private set; }

            internal int ExpandAttempts => System.Threading.Volatile.Read(ref _expandAttempts);

            public bool IsInstalled => true;

            public event Action Changed;

            internal int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;

            internal void RaiseChanged() => Changed?.Invoke();

            public AddressablesCapture Capture()
            {
                CaptureCount++;
                return new AddressablesCapture(
                    Guids.ToArray(),
                    FolderPaths.ToArray(),
                    isComplete: true);
            }

            public HashSet<string> Expand(
                AddressablesCapture capture,
                IReadOnlyList<AssetRecord> records)
            {
                System.Threading.Interlocked.Increment(ref _expandAttempts);
                HoldExpand?.Wait();
                return AddressablesCapture.Expand(capture, records);
            }
        }

        private sealed class EmptyAddressablesQuery : IAddressablesQuery
        {
            public bool IsInstalled => false;

            public event Action Changed
            {
                add { }
                remove { }
            }

            public AddressablesCapture Capture()
            {
                return new AddressablesCapture(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    isComplete: true);
            }

            public HashSet<string> Expand(
                AddressablesCapture capture,
                IReadOnlyList<AssetRecord> records)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }
    }
}
