using System.Linq;
using Zep.Core.Indexing;
using Zep.Core.Domain;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Indexing
{
    public sealed class InMemoryAssetIndexTests
    {
        [Test]
        public void Upsert_AddsNewRecordsInOrder()
        {
            var index = new InMemoryAssetIndex();

            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));
            index.Upsert(new AssetRecord("b", "Assets/B.asset", "B", "Texture2D"));

            Assert.That(index.Count, Is.EqualTo(2));
            Assert.That(index.Version, Is.EqualTo(2));
            Assert.That(index.Enumerate().Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Upsert_ReplacesExistingRecordWithoutChangingOrder()
        {
            var index = new InMemoryAssetIndex();

            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));
            index.Upsert(new AssetRecord("a", "Assets/Renamed.asset", "Renamed", "ScriptableObject"));

            Assert.That(index.Count, Is.EqualTo(1));
            Assert.That(index.Version, Is.EqualTo(2));
            Assert.That(index.Enumerate().Single().AssetPath, Is.EqualTo("Assets/Renamed.asset"));
        }

        [Test]
        public void Upsert_NewGuidAtExistingPathReplacesDisplacedRecord()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("old-guid", "Assets/A.asset", "Old", "ScriptableObject"));

            index.Upsert(new AssetRecord("new-guid", "Assets/A.asset", "New", "ScriptableObject"));

            Assert.That(index.Count, Is.EqualTo(1));
            Assert.That(index.Version, Is.EqualTo(2), "one logical replacement advances the index once");
            Assert.That(index.Contains("old-guid"), Is.False);
            Assert.That(index.TryGetByPath("Assets/A.asset", out var record), Is.True);
            Assert.That(record.Guid, Is.EqualTo("new-guid"));
            Assert.That(index.Enumerate().Select(item => item.Guid), Is.EqualTo(new[] { "new-guid" }));
        }

        [Test]
        public void Upsert_DoesNotAdvanceVersionWhenRecordIsUnchanged()
        {
            var index = new InMemoryAssetIndex();
            var record = new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject");

            index.Upsert(record);
            var versionAfterFirstInsert = index.Version;
            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));

            Assert.That(index.Version, Is.EqualTo(versionAfterFirstInsert));
        }

        [Test]
        public void Remove_DeletesExistingRecord()
        {
            var index = new InMemoryAssetIndex();

            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));

            Assert.That(index.Remove("a"), Is.True);
            Assert.That(index.Count, Is.EqualTo(0));
            Assert.That(index.Version, Is.EqualTo(2));
            Assert.That(index.Contains("a"), Is.False);
        }

        [Test]
        public void TryGetByPath_ReturnsRecordForKnownPath()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));

            Assert.That(index.TryGetByPath("Assets/A.asset", out var record), Is.True);
            Assert.That(record.Guid, Is.EqualTo("a"));
        }

        [Test]
        public void TryGetByPath_ReturnsFalseForUnknownPath()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));

            Assert.That(index.TryGetByPath("Assets/Missing.asset", out var record), Is.False);
            Assert.That(record, Is.Null);
        }

        [Test]
        public void TryGetByPath_FollowsRenamesThroughUpsert()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));
            // Simulate a rename: same guid, new path.
            index.Upsert(new AssetRecord("a", "Assets/Renamed.asset", "Renamed", "ScriptableObject"));

            Assert.That(index.TryGetByPath("Assets/A.asset", out _), Is.False, "Old path should no longer resolve.");
            Assert.That(index.TryGetByPath("Assets/Renamed.asset", out var record), Is.True);
            Assert.That(record.Guid, Is.EqualTo("a"));
        }

        [Test]
        public void TryGetByPath_ReturnsFalseAfterRemove()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));
            index.Remove("a");

            Assert.That(index.TryGetByPath("Assets/A.asset", out _), Is.False);
        }

        [Test]
        public void Clear_EmptiesIndex()
        {
            var index = new InMemoryAssetIndex();

            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));
            index.Upsert(new AssetRecord("b", "Assets/B.asset", "B", "Texture2D"));
            index.Clear();

            Assert.That(index.Count, Is.EqualTo(0));
            Assert.That(index.Version, Is.EqualTo(3));
            Assert.That(index.Enumerate(), Is.Empty);
        }

        [Test]
        public void Apply_LargeBatch_AdvancesOneAtomicVersionAndMaintainsTypeCounts()
        {
            var index = new InMemoryAssetIndex();
            var records = new AssetRecord[1000];
            for (var i = 0; i < records.Length; i++)
            {
                records[i] = new AssetRecord(
                    "bulk-" + i,
                    "Assets/Batch/Item" + i + ".asset",
                    "Item" + i,
                    i % 2 == 0 ? "Texture2D" : "ScriptableObject");
            }

            var delta = index.Apply(
                System.Array.Empty<string>(),
                System.Array.Empty<AssetCatalogMove>(),
                records,
                new[]
                {
                    new AssetFolderRecord(AssetPath.From("Assets")),
                    new AssetFolderRecord(AssetPath.From("Assets/Batch")),
                });

            Assert.That(index.Count, Is.EqualTo(1000));
            Assert.That(index.Version, Is.EqualTo(1));
            Assert.That(index.RecordVersion, Is.EqualTo(1));
            Assert.That(delta.UpsertedRecords.Count, Is.EqualTo(1000));
            Assert.That(delta.AffectedParentFolders, Is.EquivalentTo(new[] { "Assets", "Assets/Batch" }));
            Assert.That(index.TypeCounts["Texture2D"], Is.EqualTo(500));
            Assert.That(index.TypeCounts["ScriptableObject"], Is.EqualTo(500));
            Assert.That(index.NonEmptyFolderPaths, Does.Contain("Assets/Batch"));
        }

        [Test]
        public void Apply_FolderMove_RewritesOnlyTheOwnedSubtree()
        {
            var index = new InMemoryAssetIndex();
            index.ReplaceFolders(new[]
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(AssetPath.From("Assets/Art"), "art-guid"),
                new AssetFolderRecord(AssetPath.From("Assets/Art/Sub"), "sub-guid"),
                new AssetFolderRecord(AssetPath.From("Assets/Audio"), "audio-guid"),
            });
            index.Upsert(new AssetRecord("hero", "Assets/Art/Sub/Hero.png", "Hero", "Texture2D"));
            index.Upsert(new AssetRecord("theme", "Assets/Audio/Theme.wav", "Theme", "AudioClip"));

            var delta = index.Apply(
                System.Array.Empty<string>(),
                new[] { new AssetCatalogMove("Assets/Art", "Assets/Visuals") },
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(index.TryGetByPath("Assets/Art/Sub/Hero.png", out _), Is.False);
            Assert.That(index.TryGetByPath("Assets/Visuals/Sub/Hero.png", out var moved), Is.True);
            Assert.That(moved.Guid, Is.EqualTo("hero"));
            Assert.That(index.TryGetFolder("Assets/Visuals/Sub", out var movedFolder), Is.True);
            Assert.That(movedFolder.Guid, Is.EqualTo("sub-guid"));
            Assert.That(index.TryGetByPath("Assets/Audio/Theme.wav", out _), Is.True);
            Assert.That(delta.RemovedRecords.Select(record => record.Guid), Is.EqualTo(new[] { "hero" }));
            Assert.That(delta.UpsertedRecords.Select(record => record.Guid), Is.EqualTo(new[] { "hero" }));
        }

        [Test]
        public void Apply_FolderDelete_RemovesDescendantsAndUpdatesNonEmptyParents()
        {
            var index = new InMemoryAssetIndex();
            index.ReplaceFolders(new[]
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(AssetPath.From("Assets/Art")),
                new AssetFolderRecord(AssetPath.From("Assets/Art/Sub")),
            });
            index.Upsert(new AssetRecord("hero", "Assets/Art/Sub/Hero.png", "Hero", "Texture2D"));

            var delta = index.Apply(
                new[] { "Assets/Art" },
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(index.Count, Is.Zero);
            Assert.That(index.TryGetFolder("Assets/Art", out _), Is.False);
            Assert.That(index.TryGetFolder("Assets/Art/Sub", out _), Is.False);
            Assert.That(index.NonEmptyFolderPaths, Does.Not.Contain("Assets"));
            Assert.That(index.TypeCounts, Is.Empty);
            Assert.That(delta.RemovedFolders.Select(folder => folder.FolderPath.Value),
                Is.EquivalentTo(new[] { "Assets/Art", "Assets/Art/Sub" }));
        }

        [Test]
        public void PathLookup_IsCaseInsensitiveButPreservesAuthoritativeSpelling()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D"));

            Assert.That(index.TryGetByPath("assets/art/hero.png", out var record), Is.True);
            Assert.That(record.AssetPath, Is.EqualTo("Assets/Art/Hero.png"));
        }

        [Test]
        public void TypeRepresentative_FollowsIncrementalRemovalWithoutARecordScan()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "g-first",
                "Assets/First.png",
                "First",
                "Texture2D"));
            index.Upsert(new AssetRecord(
                "g-second",
                "Assets/Second.png",
                "Second",
                "Texture2D"));

            index.Remove("g-first");

            Assert.That(
                index.GetAnyAssetPathForType("texture2d"),
                Is.EqualTo("Assets/Second.png"));
        }

        [Test]
        public void DirectObservation_StoresPathPendingWithoutInventingIdentity()
        {
            var index = new InMemoryAssetIndex();

            var delta = index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Fresh.txt"),
                            isFolder: false,
                            "Fresh",
                            "TextAsset"),
                    },
                    isComplete: true),
                index.Version);

            Assert.That(index.Count, Is.Zero, "pending filesystem facts are not imported records");
            Assert.That(index.PendingCount, Is.EqualTo(1));
            Assert.That(index.TryGetByPath("Assets/Fresh.txt", out _), Is.False);
            Assert.That(delta.UpsertedRecords, Is.Empty);
            Assert.That(delta.UpsertedPendingEntries.Single().Path, Is.EqualTo("Assets/Fresh.txt"));
        }

        [Test]
        public void AuthoritativeImport_PromotesPendingPathExactlyOnce()
        {
            var index = new InMemoryAssetIndex();
            index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Fresh.txt"),
                            isFolder: false,
                            "Fresh",
                            "TextAsset"),
                    },
                    isComplete: true),
                index.Version);
            var authoritative = new AssetRecord(
                "fresh-guid",
                "Assets/Fresh.txt",
                "Fresh",
                "TextAsset");

            var promotion = index.ApplyFileChanges(
                new[] { authoritative },
                System.Array.Empty<string>());
            var acknowledgement = index.ApplyFileChanges(
                new[] { authoritative },
                System.Array.Empty<string>());

            Assert.That(index.PendingCount, Is.Zero);
            Assert.That(index.Count, Is.EqualTo(1));
            Assert.That(promotion.RemovedPendingEntries, Has.Count.EqualTo(1));
            Assert.That(promotion.UpsertedRecords, Has.Count.EqualTo(1));
            Assert.That(acknowledgement.IsEmpty, Is.True, "postprocessor acknowledgement is idempotent");
        }

        [Test]
        public void DirectObservation_StaleGenerationCannotOverwriteLiveMutation()
        {
            var index = new InMemoryAssetIndex();
            var observationVersion = index.Version;
            index.Upsert(new AssetRecord(
                "live-guid",
                "Assets/Live.asset",
                "Live",
                "ScriptableObject"));

            var stale = index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Stale.asset"),
                            isFolder: false,
                            "Stale",
                            "ScriptableObject"),
                    },
                    isComplete: true),
                observationVersion);

            Assert.That(stale.IsEmpty, Is.True);
            Assert.That(index.PendingCount, Is.Zero);
            Assert.That(index.TryGetByPath("Assets/Live.asset", out _), Is.True);
        }

        [Test]
        public void CompleteDirectObservation_RemovesOnlyOlderPendingSiblings()
        {
            var index = new InMemoryAssetIndex();
            index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets/Observed"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Observed/Vanished.txt"),
                            false,
                            "Vanished",
                            "TextAsset"),
                    },
                    true),
                index.Version);
            index.Upsert(new AssetRecord(
                "kept-guid",
                "Assets/Observed/Kept.txt",
                "Kept",
                "TextAsset"));

            index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets/Observed"),
                    System.Array.Empty<DirectFolderEntry>(),
                    true),
                index.Version);

            Assert.That(index.PendingCount, Is.Zero);
            Assert.That(index.TryGetByPath("Assets/Observed/Kept.txt", out _), Is.True,
                "filesystem absence cannot delete authoritative Unity state");
        }

        [Test]
        public void FolderDelete_RemovesOnlyItsPendingSubtreeWithoutScanningOtherFolders()
        {
            var index = new InMemoryAssetIndex();
            index.ReplaceFolders(new[]
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(AssetPath.From("Assets/Left")),
                new AssetFolderRecord(AssetPath.From("Assets/Left/Deep")),
                new AssetFolderRecord(AssetPath.From("Assets/Right")),
            });
            ObservePendingFiles(index, "Assets/Left", 1500);
            ObservePendingFiles(index, "Assets/Left/Deep", 1000);
            ObservePendingFiles(index, "Assets/Right", 2500);
            Assert.That(index.PendingCount, Is.EqualTo(5000));
            var probesBefore = index.PendingRemovalKeyProbes;

            var delta = index.Apply(
                new[] { "Assets/Left" },
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(
                delta.RemovedPendingEntries.Select(entry => entry.Path),
                Is.EquivalentTo(
                    ScrambledPendingPaths("Assets/Left", 1500)
                        .Concat(ScrambledPendingPaths("Assets/Left/Deep", 1000))),
                "the nested folder's pending entries must go with their parent");
            Assert.That(index.PendingCount, Is.EqualTo(2500));
            Assert.That(
                RemainingPendingPaths(index, "Assets/Right"),
                Is.EquivalentTo(ScrambledPendingPaths("Assets/Right", 2500)),
                "the untouched folder keeps every one of its pending entries");
            Assert.That(
                index.PendingRemovalKeyProbes - probesBefore,
                Is.EqualTo(2501),
                "one probe for the deleted root plus its own 2,500 pending descendants - "
                + "the other folder's 2,500 keys are never looked at");
        }

        [Test]
        public void FolderDelete_FollowsPendingFolderChildrenAsWellAsRealOnes()
        {
            var index = new InMemoryAssetIndex();
            index.ReplaceFolders(new[]
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(AssetPath.From("Assets/Left")),
            });
            // A folder Unity has not imported yet is itself pending, and the panel
            // may already have browsed inside it.
            ObservePending(
                index,
                "Assets/Left",
                new DirectFolderEntry(AssetPath.From("Assets/Left/Ghost"), true, "Ghost", "Folder"));
            ObservePending(
                index,
                "Assets/Left/Ghost",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Left/Ghost/Inner.txt"),
                    false,
                    "Inner",
                    "TextAsset"));
            Assert.That(index.PendingCount, Is.EqualTo(2));

            var delta = index.Apply(
                new[] { "Assets/Left" },
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(
                delta.RemovedPendingEntries.Select(entry => entry.Path),
                Is.EquivalentTo(new[] { "Assets/Left/Ghost/Inner.txt", "Assets/Left/Ghost" }));
            Assert.That(index.PendingCount, Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AncestorMutation_RemovesDisconnectedPendingDescendants(
            bool move,
            bool replaceAfterObservation)
        {
            var index = new InMemoryAssetIndex();
            ObservePending(
                index,
                "Assets/Foo/Deep",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Deep/Note.txt"), false, "Note", "TextAsset"),
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Deep/Nested"), true, "Nested", "Folder"));
            ObservePending(
                index,
                "Assets/Foo/Deep/Nested",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Deep/Nested/Inner.txt"), false, "Inner", "TextAsset"));
            ObservePending(
                index,
                "Assets/FooOther/Deep",
                new DirectFolderEntry(
                    AssetPath.From("Assets/FooOther/Deep/Keep.txt"), false, "Keep", "TextAsset"));
            if (replaceAfterObservation)
            {
                index.Replace(
                    System.Array.Empty<AssetRecord>(),
                    System.Array.Empty<AssetFolderRecord>());
            }

            Assert.That(index.TryGetFolder("Assets/Foo", out _), Is.False,
                "the pending descendants have no canonical ancestor chain to traverse");
            var previousVersion = index.Version;
            var probesBefore = index.PendingRemovalKeyProbes;

            var delta = index.Apply(
                move ? System.Array.Empty<string>() : new[] { "Assets/Foo" },
                move
                    ? new[] { new AssetCatalogMove("Assets/Foo", "Assets/Bar") }
                    : System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(
                delta.RemovedPendingEntries.Select(entry => entry.Path),
                Is.EquivalentTo(new[]
                {
                    "Assets/Foo/Deep/Note.txt",
                    "Assets/Foo/Deep/Nested",
                    "Assets/Foo/Deep/Nested/Inner.txt",
                }));
            Assert.That(index.PendingCount, Is.EqualTo(1));
            Assert.That(
                RemainingPendingPaths(index, "Assets/FooOther/Deep"),
                Is.EqualTo(new[] { "Assets/FooOther/Deep/Keep.txt" }),
                "a folder sharing the prefix is outside the mutated subtree");
            Assert.That(index.IsFolderNonEmpty("Assets/Foo/Deep"), Is.False);
            Assert.That(index.IsFolderNonEmpty("Assets/Foo/Deep/Nested"), Is.False);
            Assert.That(index.Version, Is.EqualTo(previousVersion + 1));
            Assert.That(index.PendingRemovalKeyProbes - probesBefore, Is.EqualTo(4),
                "the overlapping parent seeds must not inspect pending entries twice");
        }

        private static void ObservePendingFiles(
            InMemoryAssetIndex index,
            string folder,
            int count)
        {
            var entries = new DirectFolderEntry[count];
            for (var i = 0; i < count; i++)
            {
                var name = "Pending" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entries[i] = new DirectFolderEntry(
                    AssetPath.From(folder + "/" + name + ".txt"),
                    false,
                    name,
                    "TextAsset");
            }

            index.ApplyDirectObservation(
                new DirectFolderObservation(AssetPath.From(folder), entries, true),
                index.Version);
        }

        private static void ObservePending(
            InMemoryAssetIndex index,
            string folder,
            params DirectFolderEntry[] entries)
        {
            index.ApplyDirectObservation(
                new DirectFolderObservation(AssetPath.From(folder), entries, true),
                index.Version);
        }

        // Deliberately not in insertion order: the surviving set is what matters,
        // and expected data that is already sorted cannot catch an implementation
        // that merely returns a prefix or a sorted slice of the pending keys.
        private static System.Collections.Generic.List<string> ScrambledPendingPaths(
            string folder,
            int count)
        {
            var paths = new System.Collections.Generic.List<string>(count);
            for (var i = count - 1; i >= 0; i -= 2)
            {
                paths.Add(folder + "/Pending"
                    + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            }

            for (var i = 0; i < count; i += 2)
            {
                paths.Add(folder + "/Pending"
                    + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            }

            return paths;
        }

        [Test]
        public void Apply_ThatChangesNothing_CreatesNoDeltaCollections()
        {
            var index = new InMemoryAssetIndex();
            var record = new AssetRecord("hero", "Assets/Hero.prefab", "Hero", "GameObject");
            index.Upsert(record);

            var barren = index.Apply(
                System.Array.Empty<string>(),
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());
            var unchanged = index.Apply(
                System.Array.Empty<string>(),
                System.Array.Empty<AssetCatalogMove>(),
                new[] { record },
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(barren.IsEmpty, Is.True);
            Assert.That(unchanged.IsEmpty, Is.True, "re-applying the identical record changes nothing");
            Assert.That(
                barren.HasAllocatedCollections,
                Is.False,
                "an apply with no input must not build the six lists and the parent set");
            Assert.That(
                unchanged.HasAllocatedCollections,
                Is.False,
                "an apply whose records are already current must not build them either");
        }

        [Test]
        public void Apply_ThatChangesSomething_CreatesOnlyTheCollectionsItWrote()
        {
            var index = new InMemoryAssetIndex();

            var delta = index.Apply(
                System.Array.Empty<string>(),
                System.Array.Empty<AssetCatalogMove>(),
                new[] { new AssetRecord("hero", "Assets/Hero.prefab", "Hero", "GameObject") },
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(delta.IsEmpty, Is.False);
            Assert.That(
                delta.UpsertedRecords.Select(record => record.Guid),
                Is.EqualTo(new[] { "hero" }));
            Assert.That(delta.RemovedRecords, Is.Empty, "an unwritten collection still reads as empty");
            Assert.That(delta.UpsertedFolders, Is.Empty);
            Assert.That(delta.RemovedPendingEntries, Is.Empty);
            Assert.That(delta.AffectedParentFolders, Is.EqualTo(new[] { "Assets" }));
        }

        [Test]
        public void ReplaceFolders_SynthesisesTheAncestorsOfEveryFileEvenWhenTheSourceOmitsThem()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("hero", "Assets/Art/Sub/Hero.png", "Hero", "Texture2D"));
            index.Upsert(new AssetRecord("theme", "Assets/Audio/Theme.wav", "Theme", "AudioClip"));

            // The source list knows only about one branch; the other two folders exist purely
            // because records live in them.
            index.ReplaceFolders(new[]
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(AssetPath.From("Assets/Audio"), "audio-guid"),
            });

            Assert.That(index.TryGetFolder("Assets/Art", out var art), Is.True);
            Assert.That(art.Guid, Is.Empty, "a synthesised ancestor carries no GUID");
            Assert.That(index.TryGetFolder("Assets/Art/Sub", out _), Is.True);
            Assert.That(index.TryGetFolder("Assets/Audio", out var audio), Is.True);
            Assert.That(audio.Guid, Is.EqualTo("audio-guid"), "the source record wins over synthesis");
            Assert.That(index.TryGetByPath("Assets/Art/Sub/Hero.png", out _), Is.True);
            Assert.That(index.TryGetByPath("Assets/Audio/Theme.wav", out _), Is.True);
        }

        [Test]
        public void ReplaceFolders_DropsAFolderOnceItsLastFileIsGone()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D"));
            index.ReplaceFolders(new[] { new AssetFolderRecord(AssetPath.From("Assets")) });
            Assert.That(index.TryGetFolder("Assets/Art", out _), Is.True, "held alive by its file");

            index.Apply(
                new[] { "Assets/Art/Hero.png" },
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());
            index.ReplaceFolders(new[] { new AssetFolderRecord(AssetPath.From("Assets")) });

            Assert.That(index.TryGetFolder("Assets/Art", out _), Is.False);
            Assert.That(index.TryGetFolder("Assets", out _), Is.True);
        }

        // -------------------------------------------------------------------
        // T4.18 (D-6): Replace publishes a whole snapshot, and hydration after a
        // domain reload republishes the snapshot the index already holds. The
        // versions gate every derived cache's rebuild, so they must describe the
        // CONTENT of the snapshot, not the act of publishing it.
        // -------------------------------------------------------------------

        private static InMemoryAssetIndex BuildReplacedIndex()
        {
            var index = new InMemoryAssetIndex();
            index.Replace(SnapshotRecords(), SnapshotFolders());
            return index;
        }

        private static AssetRecord[] SnapshotRecords()
        {
            return new[]
            {
                new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                new AssetRecord("theme", "Assets/Audio/Theme.wav", "Theme", "AudioClip")
            };
        }

        private static AssetFolderRecord[] SnapshotFolders()
        {
            return new[]
            {
                new AssetFolderRecord(AssetPath.From("Assets")),
                new AssetFolderRecord(AssetPath.From("Assets/Art")),
                new AssetFolderRecord(AssetPath.From("Assets/Audio"))
            };
        }

        [Test]
        public void Replace_WithIdenticalContent_LeavesBothVersionsUnchanged()
        {
            var index = BuildReplacedIndex();
            var version = index.Version;
            var recordVersion = index.RecordVersion;

            // Equal by value, not the same instances — this is what a cache
            // hydration or a confirming scan hands back.
            index.Replace(SnapshotRecords(), SnapshotFolders());

            Assert.That(index.Version, Is.EqualTo(version));
            Assert.That(index.RecordVersion, Is.EqualTo(recordVersion));
            Assert.That(index.Count, Is.EqualTo(2));
            Assert.That(index.TryGetByPath("Assets/Art/Hero.png", out _), Is.True);
        }

        [Test]
        public void Replace_WithOneChangedRecord_BumpsBothVersions()
        {
            var index = BuildReplacedIndex();
            var version = index.Version;
            var recordVersion = index.RecordVersion;

            index.Replace(
                new[]
                {
                    new AssetRecord("hero", "Assets/Art/Hero.png", "Champion", "Texture2D"),
                    new AssetRecord("theme", "Assets/Audio/Theme.wav", "Theme", "AudioClip")
                },
                SnapshotFolders());

            Assert.That(index.Version, Is.EqualTo(version + 1));
            Assert.That(index.RecordVersion, Is.EqualTo(recordVersion + 1));
        }

        [Test]
        public void Replace_WithARemovedRecord_BumpsBothVersions()
        {
            var index = BuildReplacedIndex();
            var version = index.Version;
            var recordVersion = index.RecordVersion;

            index.Replace(
                new[]
                {
                    new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D")
                },
                SnapshotFolders());

            Assert.That(index.Version, Is.EqualTo(version + 1));
            Assert.That(index.RecordVersion, Is.EqualTo(recordVersion + 1));
        }

        [Test]
        public void Replace_WithADuplicatedRecord_BumpsBothVersions()
        {
            // Same COUNT as the live catalog and every entry matches one that is
            // already there, so only the rebuilt size can tell that an entry was
            // dropped. Without that leg this snapshot would read as unchanged.
            var index = BuildReplacedIndex();
            var version = index.Version;
            var recordVersion = index.RecordVersion;

            index.Replace(
                new[]
                {
                    new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                    new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D")
                },
                SnapshotFolders());

            Assert.That(index.Version, Is.EqualTo(version + 1));
            Assert.That(index.RecordVersion, Is.EqualTo(recordVersion + 1));
            Assert.That(index.Count, Is.EqualTo(1));
        }

        [Test]
        public void Replace_WithOnlyAFolderChanged_BumpsVersionButNotRecordVersion()
        {
            var index = BuildReplacedIndex();
            var version = index.Version;
            var recordVersion = index.RecordVersion;

            index.Replace(
                SnapshotRecords(),
                new[]
                {
                    new AssetFolderRecord(AssetPath.From("Assets")),
                    new AssetFolderRecord(AssetPath.From("Assets/Art")),
                    new AssetFolderRecord(
                        AssetPath.From("Assets/Audio"),
                        unityLabels: new[] { "Music" })
                });

            Assert.That(index.Version, Is.EqualTo(version + 1));
            Assert.That(index.RecordVersion, Is.EqualTo(recordVersion));
        }

        [Test]
        public void Replace_WithAFolderRecasedOnly_BumpsVersion()
        {
            var index = BuildReplacedIndex();
            var version = index.Version;

            index.Replace(
                SnapshotRecords(),
                new[]
                {
                    new AssetFolderRecord(AssetPath.From("Assets")),
                    new AssetFolderRecord(AssetPath.From("Assets/art")),
                    new AssetFolderRecord(AssetPath.From("Assets/Audio"))
                });

            Assert.That(
                index.Version,
                Is.EqualTo(version + 1),
                "the folder map keys are case-insensitive; what is rendered is not");
        }

        // Clear restarts the direct-observation generation, which is only safe
        // because Clear also drops every pending entry that carries a stamp from
        // before it. This pins that pairing: were an entry ever to survive Clear
        // with a higher stamp, the completeness prune below (`stamp <= generation`)
        // would refuse to remove it and it would haunt the folder forever. The
        // second observation is COMPLETE and lists only Kept, so Vanished has to be
        // gone and Kept has to be there — an "empty" or "unchanged" result both fail.
        [Test]
        public void Clear_ThenReobservingTheSameFolder_PrunesWhatTheNewObservationDoesNotList()
        {
            var index = new InMemoryAssetIndex();
            index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets/Observed"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Observed/Vanished.txt"),
                            false,
                            "Vanished",
                            "TextAsset"),
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Observed/Kept.txt"),
                            false,
                            "Kept",
                            "TextAsset"),
                    },
                    true),
                index.Version);
            Assert.That(index.PendingCount, Is.EqualTo(2));

            index.Clear();
            Assert.That(index.PendingCount, Is.Zero, "Clear drops the stamped entries");

            index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets/Observed"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Observed/Kept.txt"),
                            false,
                            "Kept",
                            "TextAsset"),
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Observed/Arrived.txt"),
                            false,
                            "Arrived",
                            "TextAsset"),
                    },
                    true),
                index.Version);

            Assert.That(
                RemainingPendingPaths(index, "Assets/Observed"),
                Is.EquivalentTo(new[]
                {
                    "Assets/Observed/Kept.txt",
                    "Assets/Observed/Arrived.txt"
                }));
        }

        // -------------------------------------------------------------------
        // Observed folders (I2). A COMPLETE direct observation the index
        // ACCEPTED is the newest read of the filesystem for that path, so the
        // index - not a second set parked beside it - is what answers "does
        // this folder exist". The rule is freshness, not precedence: the entry
        // is retired the moment newer evidence arrives, and newer evidence is
        // exactly five things. Each test below pins one of them, and each one
        // first asserts that the observation registered, because a retirement
        // assertion on a folder that was never registered passes for the wrong
        // reason.
        // -------------------------------------------------------------------

        [Test]
        public void CompleteDirectObservation_MakesTheFolderVisibleWithoutAFolderRecord()
        {
            var index = new InMemoryAssetIndex();

            ObservePending(
                index,
                "Assets/Foo",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));

            Assert.That(index.TryGetFolder("Assets/Foo", out _), Is.False,
                "no authoritative folder record exists for it yet");
            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.True,
                "the observation IS the index's evidence that the folder exists");
            Assert.That(index.IsObservedFolder("Assets/Foo"), Is.True);
            Assert.That(index.ObservedFolders, Is.EquivalentTo(new[] { "Assets/Foo" }),
                "only the observed folder itself is registered, never its children");
        }

        [Test]
        public void IncompleteDirectObservation_RetiresAFolderAnEarlierObservationRegistered()
        {
            var index = new InMemoryAssetIndex();
            ObservePending(
                index,
                "Assets/Foo",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));
            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.True);

            index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets/Foo"),
                    System.Array.Empty<DirectFolderEntry>(),
                    isComplete: false),
                index.Version);

            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.False,
                "an incomplete re-observation is the index learning the folder is gone");
        }

        [Test]
        public void VersionRejectedDirectObservation_RegistersNothing()
        {
            var index = new InMemoryAssetIndex();
            var versionBeforeTheMutation = index.Version;
            index.Upsert(new AssetRecord(
                "live-guid",
                "Assets/Live.asset",
                "Live",
                "ScriptableObject"));

            var rejected = index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets/Foo"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Foo/Note.txt"),
                            false,
                            "Note",
                            "TextAsset"),
                    },
                    isComplete: true),
                versionBeforeTheMutation);

            Assert.That(rejected.IsEmpty, Is.True, "the index applied none of it");
            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.False,
                "an observation the index refused cannot claim its folder exists");
            Assert.That(index.IsObservedFolder("Assets/Foo"), Is.False,
                "so the caller simply observes the folder again next time");
        }

        [Test]
        public void Replace_RetiresObservedFoldersButKeepsTheirPendingChildren()
        {
            var index = new InMemoryAssetIndex();
            ObservePending(
                index,
                "Assets/Foo",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));
            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.True);

            index.Replace(SnapshotRecords(), SnapshotFolders());

            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.False,
                "every observation is older than the snapshot that just replaced the catalog");
            Assert.That(index.ObservedFolders, Is.Empty);
            Assert.That(index.ContainsVisibleFolder("Assets/Art"), Is.True,
                "a folder the snapshot itself supplies stays visible");
            Assert.That(
                RemainingPendingPaths(index, "Assets/Foo"),
                Is.EqualTo(new[] { "Assets/Foo/Note.txt" }),
                "pending entries deliberately survive a Replace; observed-folder entries "
                + "deliberately do not");
        }

        [Test]
        public void Clear_RetiresAnObservedFolderThatLeftNoPendingEntryBehind()
        {
            var index = new InMemoryAssetIndex();
            ObservePending(index, "Assets/Empty");
            Assert.That(index.ContainsVisibleFolder("Assets/Empty"), Is.True);
            Assert.That(index.PendingCount, Is.Zero,
                "an empty folder observes to nothing, which is exactly the state Clear's "
                + "\"already empty\" early return cannot see");

            index.Clear();

            Assert.That(index.ContainsVisibleFolder("Assets/Empty"), Is.False);
        }

        [Test]
        public void Delete_RetiresTheObservedFolder()
        {
            var index = new InMemoryAssetIndex();
            ObservePending(
                index,
                "Assets/Foo",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));
            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.True);

            index.Apply(
                new[] { "Assets/Foo" },
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.False,
                "an observation must not outlive the folder it observed");
        }

        [Test]
        public void DeleteOfAnAncestor_RetiresTheObservedFolderBeneathIt()
        {
            var index = new InMemoryAssetIndex();
            ObservePending(
                index,
                "Assets/Foo/Deep",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Deep/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));
            Assert.That(index.ContainsVisibleFolder("Assets/Foo/Deep"), Is.True);

            index.Apply(
                new[] { "Assets/Foo" },
                System.Array.Empty<AssetCatalogMove>(),
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(index.ContainsVisibleFolder("Assets/Foo/Deep"), Is.False,
                "a delete retires every observation beneath it, not only its own path");
        }

        [Test]
        public void Move_RetiresTheObservedSourcePathAndRegistersNoDestination()
        {
            var index = new InMemoryAssetIndex();
            ObservePending(
                index,
                "Assets/Foo",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));
            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.True);

            index.Apply(
                System.Array.Empty<string>(),
                new[] { new AssetCatalogMove("Assets/Foo", "Assets/Bar") },
                System.Array.Empty<AssetRecord>(),
                System.Array.Empty<AssetFolderRecord>());

            Assert.That(index.ContainsVisibleFolder("Assets/Foo"), Is.False,
                "the observed path is gone the moment the catalog learns it moved");
            Assert.That(index.ContainsVisibleFolder("Assets/Bar"), Is.False,
                "the destination is not carried over - it comes back through a fresh observation");
        }

        // -------------------------------------------------------------------
        // Known emptiness (I3). "Empty" is a fact the index either holds or does
        // not: a folder is known empty only where the index knows the folder AND
        // knows its direct-child count is zero. Everything else - including a
        // root it can name but has never read - is simply unknown, and unknown is
        // not empty.
        // -------------------------------------------------------------------

        [Test]
        public void UnknownFolder_IsNotKnownEmpty()
        {
            var index = new InMemoryAssetIndex();

            Assert.That(index.IsFolderKnownEmpty("Assets/Unread"), Is.False,
                "a folder the index has never heard of is unknown, not empty");
            Assert.That(index.ContainsVisibleFolder("Assets"), Is.True,
                "the root is nameable before anything is read - which is the state "
                + "the assertion below exists to separate from emptiness");
            Assert.That(index.IsFolderKnownEmpty("Assets"), Is.False,
                "knowing a folder EXISTS is not knowing it is empty: right after a "
                + "reload the roots are visible and their contents are unread");
        }

        [Test]
        public void CompletelyObservedEmptyFolder_IsKnownEmpty()
        {
            var index = new InMemoryAssetIndex();

            ObservePending(index, "Assets/Empty");

            Assert.That(index.TryGetFolder("Assets/Empty", out _), Is.False,
                "no authoritative folder record exists for it yet");
            Assert.That(index.IsFolderKnownEmpty("Assets/Empty"), Is.True,
                "a complete observation that listed nothing IS the evidence that "
                + "the folder has no children");
        }

        [Test]
        public void ObservedFolderWithAPendingChild_IsNotKnownEmpty()
        {
            var index = new InMemoryAssetIndex();

            ObservePending(
                index,
                "Assets/Foo",
                new DirectFolderEntry(
                    AssetPath.From("Assets/Foo/Note.txt"),
                    false,
                    "Note",
                    "TextAsset"));

            Assert.That(index.IsObservedFolder("Assets/Foo"), Is.True,
                "the observation registered, so the answer below is about its "
                + "contents and not about the folder being unknown");
            Assert.That(index.IsFolderKnownEmpty("Assets/Foo"), Is.False,
                "a child the index holds only as a pending entry is still a child");
        }

        [Test]
        public void HydratedFolderRecords_AreKnownEmptyOnlyWhereTheSnapshotGaveThemNoChildren()
        {
            var index = new InMemoryAssetIndex();

            index.Replace(
                SnapshotRecords(),
                new[]
                {
                    new AssetFolderRecord(AssetPath.From("Assets")),
                    new AssetFolderRecord(AssetPath.From("Assets/Art")),
                    new AssetFolderRecord(AssetPath.From("Assets/Audio")),
                    new AssetFolderRecord(AssetPath.From("Assets/Empty")),
                });

            Assert.That(index.IsFolderKnownEmpty("Assets/Empty"), Is.True,
                "a hydrated folder record the snapshot gave no children is the "
                + "one case that paints the empty glyph");
            Assert.That(index.IsFolderKnownEmpty("Assets/Art"), Is.False,
                "Assets/Art holds Hero.png");
            Assert.That(index.IsFolderKnownEmpty("Assets"), Is.False,
                "its three subfolders are direct children");
        }

        private static System.Collections.Generic.List<string> RemainingPendingPaths(
            InMemoryAssetIndex index,
            string folder)
        {
            return index.EnumerateDirectPending(folder)
                .Select(entry => entry.Path)
                .ToList();
        }
    }
}
