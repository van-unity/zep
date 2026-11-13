using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    // Covers CrossCacheCoordinator.ApplyExternalChanges — the canonical
    // external-asset-change path. Every resolved batch must produce the same
    // per-folder browse projection as a full rebuild over the final record set;
    // only unresolved source-boundary data may request authoritative recovery.
    public sealed class CrossCacheCoordinatorExternalChangesTests
    {
        private InMemoryAssetIndex _index;
        private AssetSearchService _search;
        private TagProjectionService _tagProjection;
        private BrowseProjectionService _browse;
        private AssetPresentationCache _presentation;
        private FolderEntriesBuilder _folderEntries;
        private StubRecordSource _source;
        private ThrowingGateway _gateway;
        private CrossCacheCoordinator _coordinator;
        private List<AssetRecord> _initialRecords;
        private List<ZepFolderEntry> _standardFolders;
        private TagDefinitionStore _tagDefinitions;
        private List<AssetCatalogDelta> _catalogDeltas;

        [SetUp]
        public void SetUp()
        {
            _initialRecords = new List<AssetRecord>
            {
                new AssetRecord("g-hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                new AssetRecord("g-villain", "Assets/Art/Sub/Villain.png", "Villain", "Texture2D"),
                new AssetRecord("g-ctrl", "Assets/Scripts/HeroController.cs", "HeroController", "MonoScript"),
                new AssetRecord(
                    "g-config",
                    "Assets/Art/EnemyConfig.asset",
                    "EnemyConfig",
                    "EnemyConfig",
                    baseTypeNames: new[] { "GameConfig", "ScriptableObject" }),
            };

            _standardFolders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, isReadOnly: false),
                new ZepFolderEntry("Art", "Assets/Art", 1, isReadOnly: false),
                new ZepFolderEntry("Sub", "Assets/Art/Sub", 2, isReadOnly: false),
                new ZepFolderEntry("Scripts", "Assets/Scripts", 1, isReadOnly: false),
            };

            _index = new InMemoryAssetIndex();
            foreach (var record in _initialRecords)
            {
                _index.Upsert(record);
            }

            _gateway = new ThrowingGateway();
            _tagDefinitions = new TagDefinitionStore(
                new List<TagDefinitionRecord>
                {
                    new TagDefinitionRecord
                    {
                        Id = "red",
                        Name = "Red",
                        Color = ZepTagColor.Red,
                    },
                },
                () => { });
            _search = new AssetSearchService(_index);
            _tagProjection = new TagProjectionService(_gateway, _tagDefinitions);
            _presentation = new AssetPresentationCache(_gateway);
            _browse = new BrowseProjectionService(_index, _search, _tagProjection);
            _folderEntries = new FolderEntriesBuilder(_index);
            var folderRecords = new List<AssetFolderRecord>
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(
                    AssetPath.From("Assets/Art"),
                    "folder-art-guid",
                    new[] { "Old Folder Label" }),
                new AssetFolderRecord(AssetPath.From("Assets/Art/Sub")),
                new AssetFolderRecord(AssetPath.From("Assets/Scripts")),
            };
            // Seed the folder-entry cache so UpsertFolder (used by folder imports)
            // has a base to append to, matching the live post-hydration state.
            _folderEntries.RebuildFromIndex(_initialRecords, folderRecords);
            _standardFolders = _folderEntries.GetStandardEntries().ToList();
            _source = new StubRecordSource(_initialRecords);
            for (var i = 0; i < folderRecords.Count; i++)
            {
                _source.AddFolder(folderRecords[i]);
            }

            _browse.RebuildCache(_initialRecords, _standardFolders);

            _catalogDeltas = new List<AssetCatalogDelta>();

            _coordinator = new CrossCacheCoordinator(
                _index,
                _search,
                _source,
                _gateway,
                _tagDefinitions,
                _presentation,
                _tagProjection,
                _browse,
                _folderEntries,
                _catalogDeltas.Add);
        }

        [Test]
        public void ApplyExternalChanges_ImportFile_MatchesFullRebuildAndIsSearchable()
        {
            var imported = new AssetRecord("g-new", "Assets/Art/NewTex.png", "NewTex", "Texture2D");
            _source.Add(imported);
            _search.WarmIndex(); // exercise the warm mirror path

            var handled = _coordinator.ApplyExternalChanges(
                Changes(imported: new[] { "Assets/Art/NewTex.png" }));

            Assert.That(handled, Is.True, "a single file import must take the incremental path");
            AssertFolderMatchesFullRebuild("Assets/Art", WithAdded(imported));
            Assert.That(SearchPaths("newtex"), Has.Member("Assets/Art/NewTex.png"),
                "the warm search index must find the imported record without a full rebuild");
        }

        [Test]
        public void ApplyExternalChanges_ReimportExistingFile_DoesNotDuplicateRow()
        {
            // Unity reports a reimport (import-setting edit, refocus) as an Imported
            // path that already has a browse row — a bare insert would duplicate it.
            var handled = _coordinator.ApplyExternalChanges(
                Changes(imported: new[] { "Assets/Art/Hero.png" }));

            Assert.That(handled, Is.True);
            AssertFolderMatchesFullRebuild("Assets/Art", new List<AssetRecord>(_initialRecords));
        }

        [Test]
        public void ApplyExternalChangesDetailed_IdenticalAcknowledgement_IsANoOp()
        {
            var result = _coordinator.ApplyExternalChangesDetailed(
                Changes(imported: new[] { "Assets/Art/Hero.png" }),
                out var unresolvedPaths);

            Assert.That(result, Is.EqualTo(ExternalChangeApplicationResult.NoChanges));
            Assert.That(unresolvedPaths, Is.Empty);
            AssertFolderMatchesFullRebuild("Assets/Art", new List<AssetRecord>(_initialRecords));
        }

        [Test]
        public void ApplyExternalChanges_ImportAtExistingPathWithNewGuidReplacesWarmSearchRecord()
        {
            var replacement = new AssetRecord(
                "g-hero-replacement",
                "Assets/Art/Hero.png",
                "ReplacementIdentity",
                "Texture2D",
                unityLabels: new[] { "Replacement Label" });
            _source.Add(replacement);
            _search.WarmIndex();

            var handled = _coordinator.ApplyExternalChanges(
                Changes(imported: new[] { "Assets/Art/Hero.png" }));

            Assert.That(handled, Is.True);
            Assert.That(_index.Count, Is.EqualTo(_initialRecords.Count));
            Assert.That(_index.Contains("g-hero"), Is.False, "the displaced GUID must leave the canonical index");
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out var indexed), Is.True);
            Assert.That(indexed.Guid, Is.EqualTo("g-hero-replacement"));
            Assert.That(_search.Search("name=hero"), Is.Empty,
                "the warm mirror must retire the displaced record's name posting");
            Assert.That(_search.Search("name:replacementidentity").Select(record => record.Guid),
                Is.EqualTo(new[] { "g-hero-replacement" }));

            var final = Without("g-hero");
            final.Add(replacement);
            AssertFolderMatchesFullRebuild("Assets/Art", final);
        }

        [Test]
        public void ApplyExternalChanges_DeleteFile_MatchesFullRebuild()
        {
            var handled = _coordinator.ApplyExternalChanges(
                Changes(deleted: new[] { "Assets/Art/Hero.png" }));

            Assert.That(handled, Is.True);
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.False, "record removed from the index");
            AssertFolderMatchesFullRebuild("Assets/Art", Without("g-hero"));
        }

        [Test]
        public void ApplyExternalChanges_RenameFile_MovesEntryAndMatchesFullRebuild()
        {
            // Same guid, new path — a rename reported as a Move.
            var renamed = new AssetRecord("g-hero", "Assets/Art/Champion.png", "Champion", "Texture2D");
            _source.Add(renamed);
            _search.WarmIndex();

            var handled = _coordinator.ApplyExternalChanges(
                Changes(moved: new[] { "Assets/Art/Champion.png" }, movedFrom: new[] { "Assets/Art/Hero.png" }));

            Assert.That(handled, Is.True);
            var final = Without("g-hero");
            final.Add(renamed);
            AssertFolderMatchesFullRebuild("Assets/Art", final);
            Assert.That(SearchPaths("champion"), Has.Member("Assets/Art/Champion.png"));
            Assert.That(SearchPaths("hero"), Has.None.EqualTo("Assets/Art/Hero.png"),
                "the old path must not linger in the search index after a rename");
        }

        [Test]
        public void ApplyExternalChanges_MoveFileToAnotherFolder_MatchesFullRebuild()
        {
            var moved = new AssetRecord("g-hero", "Assets/Scripts/Hero.png", "Hero", "Texture2D");
            _source.Add(moved);

            var handled = _coordinator.ApplyExternalChanges(
                Changes(moved: new[] { "Assets/Scripts/Hero.png" }, movedFrom: new[] { "Assets/Art/Hero.png" }));

            Assert.That(handled, Is.True);
            var final = Without("g-hero");
            final.Add(moved);
            AssertFolderMatchesFullRebuild("Assets/Art", final);
            AssertFolderMatchesFullRebuild("Assets/Scripts", final);
        }

        [Test]
        public void ApplyExternalChanges_FolderDelete_RemovesOnlyTheKnownSubtree()
        {
            var handled = _coordinator.ApplyExternalChanges(
                Changes(deleted: new[] { "Assets/Art/Sub" }));

            Assert.That(handled, Is.True);
            Assert.That(_index.TryGetByPath("Assets/Art/Sub/Villain.png", out _), Is.False);
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.True,
                "an unrelated sibling must not be scanned or removed");
        }

        [Test]
        public void ApplyExternalChanges_FolderMove_RewritesOnlyTheKnownSubtree()
        {
            var handled = _coordinator.ApplyExternalChanges(
                Changes(moved: new[] { "Assets/Renamed" }, movedFrom: new[] { "Assets/Art" }));

            Assert.That(handled, Is.True);
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.False);
            Assert.That(_index.TryGetByPath("Assets/Renamed/Hero.png", out var moved), Is.True);
            Assert.That(moved.Guid, Is.EqualTo("g-hero"));
            Assert.That(_index.TryGetByPath("Assets/Scripts/HeroController.cs", out _), Is.True);
        }

        [Test]
        public void ApplyExternalChanges_OneThousandPaths_RemainsIncremental()
        {
            var big = new string[1000];
            for (var i = 0; i < big.Length; i++)
            {
                big[i] = $"Assets/Art/Bulk{i}.png";
                _source.Add(new AssetRecord(
                    "bulk-guid-" + i,
                    big[i],
                    "Bulk" + i,
                    "Texture2D"));
            }

            var handled = _coordinator.ApplyExternalChanges(Changes(imported: big));

            Assert.That(handled, Is.True);
            Assert.That(_index.Count, Is.EqualTo(_initialRecords.Count + big.Length));
            Assert.That(_catalogDeltas, Has.Count.EqualTo(1),
                "one resolved callback batch must emit one derived-cache delta");
        }

        [Test]
        public void ApplyExternalChanges_HasNoHiddenBulkOrSizeEligibilityGate()
        {
            var imported = new AssetRecord(
                "g-new",
                "Assets/Art/NewTex.png",
                "NewTex",
                "Texture2D");
            _source.Add(imported);

            var handled = _coordinator.ApplyExternalChanges(
                Changes(imported: new[] { "Assets/Art/NewTex.png" }));

            Assert.That(handled, Is.True);
        }

        // 4.5: one path the source boundary cannot build used to discard the whole
        // batch and cost a full project reconciliation. The applied subset is
        // complete for its own paths, so it commits; only the remainder is
        // reported for recovery.
        [Test]
        public void ApplyExternalChanges_MixedUnresolvedBatch_AppliesTheResolvedSubsetAndNamesTheRest()
        {
            var imported = new string[100];
            for (var i = 0; i < imported.Length - 1; i++)
            {
                var record = new AssetRecord(
                    "g-batch-" + i.ToString("D3"),
                    "Assets/Art/Batch" + i.ToString("D3") + ".png",
                    "Batch" + i.ToString("D3"),
                    "Texture2D");
                _source.Add(record);
                imported[i] = record.AssetPath;
            }

            imported[imported.Length - 1] = "Assets/Art/Unresolved.png";
            _gateway.FilePaths.Add("Assets/Art/Unresolved.png");

            var result = _coordinator.ApplyExternalChangesDetailed(
                Changes(imported: imported),
                out var unresolvedPaths);

            Assert.That(result, Is.EqualTo(ExternalChangeApplicationResult.Changed));
            Assert.That(
                unresolvedPaths,
                Is.EqualTo(new[] { "Assets/Art/Unresolved.png" }),
                "the caller can only scope recovery if it is told which paths failed");
            Assert.That(
                imported.Take(imported.Length - 1)
                    .Count(path => _index.TryGetByPath(path, out _)),
                Is.EqualTo(99),
                "every resolvable path in the batch must be committed");
            Assert.That(
                _index.TryGetByPath("Assets/Art/Unresolved.png", out _),
                Is.False);
            Assert.That(_catalogDeltas.Count, Is.EqualTo(1),
                "the resolved subset is ONE transaction, not one per path");
        }

        // The other end of a move is the dangerous half: dropping the source row
        // for a destination nothing could build would delete a row recovery has no
        // way to restore from the batch.
        [Test]
        public void ApplyExternalChanges_UnresolvedMove_KeepsTheSourceRowAndReportsBothEnds()
        {
            _gateway.FilePaths.Add("Assets/Art/Ghost.png");

            var result = _coordinator.ApplyExternalChangesDetailed(
                Changes(
                    moved: new[] { "Assets/Art/Ghost.png" },
                    movedFrom: new[] { "Assets/Art/Hero.png" }),
                out var unresolvedPaths);

            Assert.That(result, Is.EqualTo(ExternalChangeApplicationResult.NoChanges));
            Assert.That(
                unresolvedPaths,
                Is.EquivalentTo(new[] { "Assets/Art/Ghost.png", "Assets/Art/Hero.png" }));
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.True,
                "the stale row must survive for reconciliation to correct");
        }

        // A deletion needs no source resolution at all, so it commits alongside an
        // unresolvable import instead of waiting for a project-wide rebuild.
        [Test]
        public void ApplyExternalChanges_DeletionBesideAnUnresolvedImport_StillCommits()
        {
            _gateway.FilePaths.Add("Assets/Art/Unresolved.png");

            var result = _coordinator.ApplyExternalChangesDetailed(
                Changes(
                    imported: new[] { "Assets/Art/Unresolved.png" },
                    deleted: new[] { "Assets/Art/Hero.png" }),
                out var unresolvedPaths);

            Assert.That(result, Is.EqualTo(ExternalChangeApplicationResult.Changed));
            Assert.That(unresolvedPaths, Is.EqualTo(new[] { "Assets/Art/Unresolved.png" }));
            Assert.That(_index.TryGetByPath("Assets/Art/Hero.png", out _), Is.False);
        }

        [Test]
        public void CatalogUpsert_WhenSearchIndexIsCold_DoesNotForceBuild()
        {
            // Fresh service: the derived search index has never been built.
            Assert.That(_search.HasBuiltIndex, Is.False);

            var imported = new AssetRecord("g-new", "Assets/Art/NewTex.png", "NewTex", "Texture2D");
            _source.Add(imported);
            _coordinator.ApplyExternalChanges(Changes(imported: new[] { "Assets/Art/NewTex.png" }));

            Assert.That(_search.HasBuiltIndex, Is.False,
                "an import before the first search must NOT eagerly pay the full gram build");
            // The lazy build on first search still includes it.
            Assert.That(SearchPaths("newtex"), Has.Member("Assets/Art/NewTex.png"));
            Assert.That(_search.HasBuiltIndex, Is.True, "the first search builds the mirror lazily");
        }

        [Test]
        public void ApplyExternalChanges_ExistingFolderReplacesSearchMetadata()
        {
            _gateway.FolderPaths.Add("Assets/Art");
            Assert.That(
                SearchFolderPaths("l:\"old folder label\" t:folder"),
                Is.EqualTo(new[] { "Assets/Art" }));

            _source.AddFolder(new AssetFolderRecord(
                AssetPath.From("Assets/Art"),
                "folder-art-guid",
                new[] { "New Folder Label" }));

            Assert.That(
                _coordinator.ApplyExternalChanges(Changes(imported: new[] { "Assets/Art" })),
                Is.True,
                "a folder label reimport should stay on the incremental path");

            var updatedFolders = _folderEntries.GetStandardEntries();
            var updated = updatedFolders.Single(folder => folder.Path == "Assets/Art");
            Assert.That(updatedFolders.Count(folder => folder.Path == "Assets/Art"), Is.EqualTo(1));
            Assert.That(updated.Guid, Is.EqualTo("folder-art-guid"));
            Assert.That(updated.UnityLabels, Is.EqualTo(new[] { "New Folder Label" }));
            Assert.That(SearchFolderPaths("l:\"old folder label\" t:folder"), Is.Empty);
            Assert.That(
                SearchFolderPaths("l:\"new folder label\" guid:folder-art"),
                Is.EqualTo(new[] { "Assets/Art" }),
                "the next search must see replacement metadata without a full rebuild");
        }

        [Test]
        public void RecoveryTagDelta_RecordRefusalPreservesState()
        {
            SeedTagCache();
            var failure = new ProjectTagStateUnavailableException("assignment read refused");
            _gateway.TagReadFailure = (path, _) => path.Value == "Assets/Art/Sub/Villain.png"
                ? failure
                : null;
            var delta = _index.Apply(
                Array.Empty<string>(),
                Array.Empty<AssetCatalogMove>(),
                new[]
                {
                    new AssetRecord("g-hero", "Assets/Art/Hero.png", "Changed Hero", "Texture2D"),
                    new AssetRecord("g-villain", "Assets/Art/Sub/Villain.png", "Changed Villain", "Texture2D"),
                },
                Array.Empty<AssetFolderRecord>());

            var thrown = Assert.Throws<ProjectTagStateUnavailableException>(
                () => _tagProjection.ApplyCatalogDelta(delta));
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(_tagProjection.GetTagsForPath("Assets/Art/Hero.png").Single().Id, Is.EqualTo("red"));
            Assert.That(_tagProjection.GetTagsForPath("Assets/Art").Single().Id, Is.EqualTo("red"));
            Assert.That(_tagProjection.CachedFilterOptions.Single().Count, Is.EqualTo(2));
            Assert.That(_tagProjection.ObservedTagVersion, Is.EqualTo(17));
        }

        [Test]
        public void RecoveryTagDelta_FolderRefusalPreservesState()
        {
            SeedTagCache();
            var reads = new List<string>();
            var failure = new ProjectTagStateUnavailableException("folder assignment read refused");
            _gateway.TagIdsByPath["Assets/Art/Hero.png"] = new[] { "red" };
            _gateway.TagIdsByPath["Assets/Art"] = new[] { "red" };
            _gateway.TagReadFailure = (path, _) =>
            {
                reads.Add(path.Value);
                return path.Value == "Assets/Scripts" ? failure : null;
            };

            var delta = new AssetCatalogDelta(_index.RecordVersion);
            delta.RemovedRecords.Add(new AssetRecord(
                "g-hero", "Assets/Art/Hero.png", "Hero", "Texture2D"));
            delta.RemovedFolders.Add(new AssetFolderRecord(
                AssetPath.From("Assets/Art"), "folder-art-guid", new[] { "Old Folder Label" }));
            delta.UpsertedRecords.Add(new AssetRecord(
                "g-hero", "Assets/Art/Hero.png", "Changed Hero", "Texture2D"));
            delta.UpsertedFolders.Add(new AssetFolderRecord(
                AssetPath.From("Assets/Art"), "folder-art-guid", new[] { "Changed" }));
            delta.UpsertedFolders.Add(new AssetFolderRecord(AssetPath.From("Assets/Scripts")));

            Assert.That(delta.UpsertedRecords.Select(record => record.AssetPath),
                Is.EqualTo(new[] { "Assets/Art/Hero.png" }));
            Assert.That(delta.UpsertedFolders.Select(folder => folder.FolderPath.Value),
                Is.EqualTo(new[] { "Assets/Art", "Assets/Scripts" }));

            var thrown = Assert.Throws<ProjectTagStateUnavailableException>(
                () => _tagProjection.ApplyCatalogDelta(delta));
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(reads, Is.EqualTo(new[]
            {
                "Assets/Art/Hero.png",
                "Assets/Art",
                "Assets/Scripts",
            }));
            Assert.That(
                _tagProjection.GetFolderTintColorForPath("Assets/Art", "Folder"),
                Is.EqualTo(ZepTagColor.Red));
            Assert.That(_tagProjection.GetTagsForPath("Assets/Art/Hero.png").Single().Id,
                Is.EqualTo("red"));
            Assert.That(_tagProjection.CachedFilterOptions.Single().Count, Is.EqualTo(2));
            Assert.That(_tagProjection.ObservedTagVersion, Is.EqualTo(17));
        }

        [Test]
        public void RecoveryTagDelta_RetryAndEdgeCases()
        {
            SeedTagCache();
            var mutableIds = new List<string> { "red" };
            _gateway.TagIdsByPath["Assets/Art/Hero.png"] = mutableIds;
            _gateway.TagIdsByPath["Assets/Art/Sub/Villain.png"] = Array.Empty<string>();
            _gateway.TagReadFailure = (path, _) =>
            {
                if (path.Value == "Assets/Art/Sub/Villain.png")
                {
                    mutableIds.Clear();
                }

                return null;
            };
            var delta = _index.Apply(
                Array.Empty<string>(),
                Array.Empty<AssetCatalogMove>(),
                new[]
                {
                    new AssetRecord("g-hero", "Assets/Art/Hero.png", "Changed Hero", "Texture2D"),
                    new AssetRecord("g-villain", "Assets/Art/Sub/Villain.png", "Changed Villain", "Texture2D"),
                },
                Array.Empty<AssetFolderRecord>());

            _tagProjection.ApplyCatalogDelta(delta);

            Assert.That(_tagProjection.GetTagsForPath("Assets/Art/Hero.png").Single().Id, Is.EqualTo("red"),
                "assignment IDs must be copied before a later gateway read can mutate them");
            Assert.That(_tagProjection.GetTagsForPath("Assets/Art/Sub/Villain.png"), Is.Empty);
            Assert.That(_tagProjection.CachedFilterOptions.Single().Count, Is.EqualTo(2));

            _gateway.TagReadCount = 0;
            var deletionOnly = _index.Apply(
                new[] { "Assets/Art/Sub/Villain.png" },
                Array.Empty<AssetCatalogMove>(),
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>());
            _tagProjection.ApplyCatalogDelta(deletionOnly);
            Assert.That(_gateway.TagReadCount, Is.EqualTo(0));
        }

        [Test]
        public void RecoveryTagDelta_CrossCacheIntegration()
        {
            SeedTagCache();
            var failure = new ProjectTagStateUnavailableException("coordinator assignment read refused");
            _gateway.TagReadFailure = (_, __) => failure;
            _source.Add(new AssetRecord("g-hero", "Assets/Art/Hero.png", "Changed Hero", "Texture2D"));

            var thrown = Assert.Throws<ProjectTagStateUnavailableException>(
                () => _coordinator.ApplyExternalChangesDetailed(
                    Changes(imported: new[] { "Assets/Art/Hero.png" }),
                    out _));
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(_tagProjection.GetTagsForPath("Assets/Art/Hero.png").Single().Id, Is.EqualTo("red"));
            Assert.That(_tagProjection.CachedFilterOptions.Single().Count, Is.EqualTo(2));
            Assert.That(_catalogDeltas, Is.Empty,
                "a failed tag projection must not announce a completed catalog change");
        }

        private void SeedTagCache()
        {
            _gateway.TagIdsByPath["Assets/Art/Hero.png"] = new[] { "red" };
            _gateway.TagIdsByPath["Assets/Art"] = new[] { "red" };
            _tagProjection.RebuildCache(_initialRecords, _standardFolders);
            _tagProjection.RebuildFilterOptions();
            _tagProjection.ObservedTagVersion = 17;
            _gateway.TagReadCount = 0;
        }
        [Test]
        public void ApplyTagAssignmentsLocally_PreservesConcreteTypeAncestryInWarmSearch()
        {
            const string assetPath = "Assets/Art/EnemyConfig.asset";
            _gateway.FilePaths.Add(assetPath);
            _search.WarmIndex();

            _coordinator.ApplyTagAssignmentsLocally(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [assetPath] = new[] { "red" },
                });

            Assert.That(_index.TryGetByPath(assetPath, out var updated), Is.True);
            Assert.That(updated.TagSearchText, Does.Contain("Red"));
            Assert.That(updated.BaseTypeNames, Is.EqualTo(new[] { "GameConfig", "ScriptableObject" }),
                "a tag-only replacement must retain the imported CLR ancestry metadata");
            Assert.That(
                _search.Search("t:ScriptableObject tag:red").Select(record => record.Guid),
                Is.EqualTo(new[] { "g-config" }),
                "the warm mirror must keep base-type matching immediately after a tag edit");
        }

        // PublishIndexPatch is the publication half of ApplyCatalogChange, split
        // out so a delta the index produced SOMEWHERE ELSE can still reach the
        // derived caches and the view: the post-reload type-reconciliation job
        // rebuilds its slices straight into the index on the Bridge side and
        // hands the resulting delta here. This drives it exactly the way that
        // job will — index first, coordinator second.
        [Test]
        public void PublishIndexPatch_RetypedRecord_ReachesTheDerivedCachesAndAnnouncesOnce()
        {
            Assert.That(TypeFilterNames(), Has.Member("MonoScript").And.No.Member("HeroController"),
                "premise: the script is still the ambiguous pre-compile type");

            // What a compile does to a script record: the ambiguous MonoScript
            // resolves to its concrete type. Nothing else about it moves.
            var retyped = new AssetRecord(
                "g-ctrl",
                "Assets/Scripts/HeroController.cs",
                "HeroController",
                "HeroController");
            var delta = _index.Apply(
                Array.Empty<string>(),
                Array.Empty<AssetCatalogMove>(),
                new[] { retyped },
                Array.Empty<AssetFolderRecord>());
            Assert.That(delta.IsEmpty, Is.False,
                "an unchanged record would make the publication unobservable");

            _coordinator.PublishIndexPatch(delta);

            Assert.That(TypeFilterNames(), Has.Member("HeroController").And.No.Member("MonoScript"),
                "the browse projection's type filter must follow the retype without a full rebuild");
            Assert.That(
                _browse.BuildAssetEntries(
                        "Assets/Scripts",
                        string.Empty,
                        ZepSearchScope.Assets,
                        string.Empty,
                        string.Empty)
                    .Single(entry => entry.Path == "Assets/Scripts/HeroController.cs")
                    .TypeName,
                Is.EqualTo("HeroController"),
                "the row the panel paints must carry the rebuilt type");
            Assert.That(_catalogDeltas, Has.Count.EqualTo(1),
                "one patch is one announcement — no more, no less");
            Assert.That(_catalogDeltas[0], Is.SameAs(delta));
        }

        [Test]
        public void PublishIndexPatch_NothingChanged_PublishesNothing()
        {
            var unchanged = _index.Apply(
                Array.Empty<string>(),
                Array.Empty<AssetCatalogMove>(),
                new[] { _initialRecords[2] },
                Array.Empty<AssetFolderRecord>());
            Assert.That(unchanged.IsEmpty, Is.True,
                "re-applying an identical record is the premise of this test");

            _coordinator.PublishIndexPatch(unchanged);
            _coordinator.PublishIndexPatch(null);

            Assert.That(_catalogDeltas, Is.Empty,
                "a patch that changed nothing must not announce a catalog change");
            Assert.That(TypeFilterNames(), Has.Member("MonoScript"),
                "and must not disturb the caches it would otherwise republish");
        }

        // ---- helpers ----

        private IReadOnlyList<string> TypeFilterNames()
        {
            return _browse.CachedTypeFilterOptions
                .Select(option => option.TypeName)
                .ToList();
        }

        private ProjectAssetChanges Changes(
            string[] imported = null,
            string[] deleted = null,
            string[] moved = null,
            string[] movedFrom = null)
        {
            return new ProjectAssetChanges(imported, deleted, moved, movedFrom);
        }

        private List<AssetRecord> WithAdded(AssetRecord added)
        {
            var list = new List<AssetRecord>(_initialRecords) { added };
            return list;
        }

        private List<AssetRecord> Without(string guid)
        {
            return _initialRecords.Where(r => r.Guid != guid).ToList();
        }

        private void AssertFolderMatchesFullRebuild(string folderPath, List<AssetRecord> finalRecords)
        {
            var reference = BuildReference(finalRecords);
            var referencePaths = FolderPaths(reference, folderPath);
            var actualPaths = FolderPaths(_browse, folderPath);
            Assert.That(actualPaths, Is.EqualTo(referencePaths),
                $"incremental per-folder browse cache for '{folderPath}' must equal a full rebuild");
        }

        private IReadOnlyList<string> FolderPaths(BrowseProjectionService svc, string folderPath)
        {
            return svc
                .BuildAssetEntries(folderPath, string.Empty, ZepSearchScope.Assets, string.Empty, string.Empty)
                .Select(e => e.Path)
                .ToList();
        }

        private BrowseProjectionService BuildReference(List<AssetRecord> finalRecords)
        {
            var index = new InMemoryAssetIndex();
            foreach (var record in finalRecords)
            {
                index.Upsert(record);
            }

            var search = new AssetSearchService(index);
            var tagProjection = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }));
            var reference = new BrowseProjectionService(index, search, tagProjection);
            reference.RebuildCache(finalRecords, _standardFolders);
            return reference;
        }

        private IReadOnlyList<string> SearchPaths(string query)
        {
            return _search.Search(query).Select(r => r.AssetPath).ToList();
        }

        private IReadOnlyList<string> SearchFolderPaths(string query)
        {
            return _browse.BuildAssetEntries(
                    "Assets",
                    query,
                    ZepSearchScope.Assets,
                    string.Empty,
                    string.Empty)
                .Where(entry => entry.TypeName == "Folder")
                .Select(entry => entry.Path)
                .ToList();
        }

        private sealed class StubRecordSource : IAssetRecordSource
        {
            private readonly Dictionary<string, AssetRecord> _byPath = new Dictionary<string, AssetRecord>(StringComparer.Ordinal);
            private readonly Dictionary<string, AssetFolderRecord> _foldersByPath =
                new Dictionary<string, AssetFolderRecord>(StringComparer.OrdinalIgnoreCase);

            public StubRecordSource(IEnumerable<AssetRecord> records)
            {
                foreach (var record in records)
                {
                    Add(record);
                }
            }

            public void Add(AssetRecord record) => _byPath[record.AssetPath] = record;

            public void AddFolder(AssetFolderRecord folderRecord)
                => _foldersByPath[folderRecord.FolderPath.Value] = folderRecord;

            public bool IsThreadSafe => true;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords() => _byPath.Values;

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
                => _foldersByPath.Values.ToList();

            public AssetRecord BuildRecord(AssetPath assetPath)
                => _byPath.TryGetValue(assetPath.Value, out var record) ? record : null;

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath)
                => _foldersByPath.TryGetValue(folderPath.Value, out var record) ? record : null;
        }

        // Assignment reads are configured locally by the tag-cache tests; every
        // unrelated gateway member still throws on unexpected access.
        private sealed class ThrowingGateway : IProjectAssetGateway
        {
            public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default) => throw new NotSupportedException();
            public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) => throw new NotSupportedException();
            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) => throw new NotSupportedException();
            public int TagVersion => 0;

            internal HashSet<string> FolderPaths { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal HashSet<string> FilePaths { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal Dictionary<string, IReadOnlyList<string>> TagIdsByPath { get; } =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            internal Dictionary<string, string> GuidsByPath { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal Func<AssetPath, int, Exception> TagReadFailure { get; set; }

            internal int TagReadCount { get; set; }

            public bool Exists(AssetPath assetPath) => throw new NotSupportedException();

            public bool IsFolder(AssetPath assetPath)
            {
                if (FolderPaths.Contains(assetPath.Value))
                {
                    return true;
                }

                if (FilePaths.Contains(assetPath.Value))
                {
                    return false;
                }

                throw new NotSupportedException();
            }

            public bool CanWrite(AssetPath assetPath) => throw new NotSupportedException();

            public bool CanTag(AssetPath assetPath) => throw new NotSupportedException();

            public bool EnsureFolder(AssetPath folderPath) => throw new NotSupportedException();

            public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName) =>
                throw new NotSupportedException();

            public ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
                throw new NotSupportedException();

            public ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
                throw new NotSupportedException();

            public ProjectFileOperationResult Delete(AssetPath assetPath) => throw new NotSupportedException();

            // No identity to re-mint: these fakes hold no .meta files.

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath)
            {
                var failure = TagReadFailure?.Invoke(assetPath, ++TagReadCount);
                if (failure != null)
                {
                    throw failure;
                }

                return TagIdsByPath.TryGetValue(assetPath.Value, out var tagIds)
                    ? tagIds
                    : Array.Empty<string>();
            }

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) => GetTagIds(assetPath);

            public ProjectFileOperationResult SetTagIds(IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath) =>
                throw new NotSupportedException();

            public bool Open(AssetPath assetPath) => throw new NotSupportedException();

            public void Reveal(AssetPath assetPath) => throw new NotSupportedException();

            public void BeginBatch() => throw new NotSupportedException();

            public void EndBatch() => throw new NotSupportedException();

            public string GetGuid(AssetPath assetPath) =>
                GuidsByPath.TryGetValue(assetPath.Value, out var guid)
                    ? guid
                    : throw new NotSupportedException();
            public string GetPathForGuid(string assetGuid) => throw new NotSupportedException();

            public string ResolveAbsolutePath(AssetPath assetPath) => throw new NotSupportedException();
        }
    }
}
