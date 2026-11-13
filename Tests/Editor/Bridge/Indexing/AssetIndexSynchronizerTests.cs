using System;
using Zep.Core.FileOperations;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.Application.Ports;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Indexing;
using RecoveryCache = Zep.Core.Indexing.PersistentAssetIndexCache;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Editor.Tests.Bridge.Indexing
{
    public sealed class AssetIndexSynchronizerTests
    {
        private static readonly byte[] OnePixelPng =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0xF8, 0xCF, 0xC0, 0xF0,
            0x1F, 0x00, 0x05, 0x00, 0x01, 0xFF, 0x89, 0x99, 0x3D, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
            0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
        };

        private bool _cacheExisted;
        private byte[] _cacheBackup;
        private bool _dirtyMarkerExisted;
        private byte[] _dirtyMarkerBackup;
        private Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> _unityLabelSnapshotOverride;
        private Func<IReadOnlyDictionary<string, AssetTypeMetadata>> _authoritativeTypeSnapshotOverride;
        private Func<uint> _artifactDependencyVersionOverride;
        private Action<string, string> _pendingChangesCommitOverride;
        private Action<string> _pendingChangesRetireOverride;
        private Action _cacheDeserializedOverride;
        private Action<string, string> _authoritativeFallbackCommitOverride;
        private Action<string, string> _dirtyMarkerCommitOverride;
        private Action _pendingChangeBatchMergeObserver;
        private Action _snapshotSerializationObserver;
        private IReadOnlyList<ProjectAssetChanges> _pendingChangesBackup;

        [SetUp]
        public void SetUp()
        {
            _cacheExisted = File.Exists(PersistentAssetIndexCache.CacheFilePath);
            _cacheBackup = _cacheExisted
                ? File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath)
                : null;
            _dirtyMarkerExisted = File.Exists(PersistentAssetIndexCache.DirtyMarkerPath);
            _dirtyMarkerBackup = _dirtyMarkerExisted
                ? File.ReadAllBytes(PersistentAssetIndexCache.DirtyMarkerPath)
                : null;
            if (_dirtyMarkerExisted)
            {
                File.Delete(PersistentAssetIndexCache.DirtyMarkerPath);
            }
            _unityLabelSnapshotOverride = PersistentAssetIndexCache.UnityLabelSnapshotOverride;
            _authoritativeTypeSnapshotOverride = PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride;
            _artifactDependencyVersionOverride = PersistentAssetIndexCache.ArtifactDependencyVersionOverride;
            _pendingChangesCommitOverride = PersistentAssetIndexCache.PendingChangesCommitOverride;
            _pendingChangesRetireOverride = PersistentAssetIndexCache.PendingChangesRetireOverride;
            _cacheDeserializedOverride =
                PersistentAssetIndexCache.CacheDeserializedOverride;
            _authoritativeFallbackCommitOverride =
                PersistentAssetIndexCache.AuthoritativeFallbackCommitOverride;
            _dirtyMarkerCommitOverride =
                PersistentAssetIndexCache.DirtyMarkerCommitOverride;
            _pendingChangeBatchMergeObserver =
                PersistentAssetIndexCache.PendingChangeBatchMergeObserverForTests;
            _snapshotSerializationObserver =
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            _pendingChangesBackup = PersistentAssetIndexCache.CapturePendingChanges();
            PersistentAssetIndexCache.ClearPendingChanges();
            // The first hydration of an editor process verifies regardless of the
            // journal; every test here that hydrates would otherwise start a walk.
            // The tests about that rule set this back to false themselves.
            AssetIndexSynchronizer.VerifiedThisProcess = true;
        }

        private static string JournalDirectory =>
            PersistentAssetIndexCache.PendingChangesDirectory;

        private static string[] BatchFiles()
        {
            return Directory.Exists(JournalDirectory)
                ? Directory.GetFiles(JournalDirectory, "*.json")
                : Array.Empty<string>();
        }

        private static bool JournalHasBatches()
        {
            return BatchFiles().Length > 0;
        }

        // A verification or retype job runs after the operation completed; its
        // save is what retires the journal and clears the markers.
        private static IEnumerator WaitForBackgroundJob(AssetIndexSynchronizer synchronizer)
        {
            for (var frame = 0; frame < 1000 && synchronizer.HasBackgroundJob; frame++)
            {
                yield return null;
            }

            Assert.That(synchronizer.HasBackgroundJob, Is.False, "the background job never settled");
        }

        private static bool HasStagedSnapshotFiles()
        {
            return Directory.Exists(PersistentAssetIndexCache.CacheDirectory)
                && Directory.GetFiles(
                    PersistentAssetIndexCache.CacheDirectory,
                    Path.GetFileName(PersistentAssetIndexCache.CacheFilePath) + ".*.tmp").Length > 0;
        }

        private static long JournalByteCount()
        {
            var total = 0L;
            var files = BatchFiles();
            for (var i = 0; i < files.Length; i++)
            {
                total += new FileInfo(files[i]).Length;
            }

            return total;
        }

        private static string FirstBatchFilePath()
        {
            var files = BatchFiles();
            Array.Sort(files, StringComparer.Ordinal);
            return files.Length > 0
                ? files[0]
                : Path.Combine(JournalDirectory, "0000000000000000001.json");
        }

        // A batch file the parser rejects, under the generation the next append
        // would take, so the journal reads as malformed exactly where a torn
        // write of a real batch would leave it.
        private static void WriteMalformedBatchFile(string content)
        {
            Directory.CreateDirectory(JournalDirectory);
            File.WriteAllText(
                Path.Combine(JournalDirectory, "0000000000000000001.json"),
                content);
        }

        [TearDown]
        public void TearDown()
        {
            PersistentAssetIndexCache.UnityLabelSnapshotOverride = _unityLabelSnapshotOverride;
            PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = _authoritativeTypeSnapshotOverride;
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = _artifactDependencyVersionOverride;
            PersistentAssetIndexCache.PendingChangesCommitOverride = _pendingChangesCommitOverride;
            PersistentAssetIndexCache.PendingChangesRetireOverride = _pendingChangesRetireOverride;
            PersistentAssetIndexCache.CacheDeserializedOverride =
                _cacheDeserializedOverride;
            PersistentAssetIndexCache.AuthoritativeFallbackCommitOverride =
                _authoritativeFallbackCommitOverride;
            PersistentAssetIndexCache.DirtyMarkerCommitOverride =
                _dirtyMarkerCommitOverride;
            PersistentAssetIndexCache.PendingChangeBatchMergeObserverForTests =
                _pendingChangeBatchMergeObserver;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests =
                _snapshotSerializationObserver;
            PersistentAssetIndexCache.ClearPendingChanges();
            for (var i = 0; i < _pendingChangesBackup.Count; i++)
            {
                PersistentAssetIndexCache.RecordPendingChanges(
                    _pendingChangesBackup[i]);
            }

            if (_cacheExisted)
            {
                Directory.CreateDirectory(PersistentAssetIndexCache.CacheDirectory);
                File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, _cacheBackup);
            }
            else if (File.Exists(PersistentAssetIndexCache.CacheFilePath))
            {
                File.Delete(PersistentAssetIndexCache.CacheFilePath);
            }

            if (_dirtyMarkerExisted)
            {
                Directory.CreateDirectory(PersistentAssetIndexCache.CacheDirectory);
                File.WriteAllBytes(
                    PersistentAssetIndexCache.DirtyMarkerPath,
                    _dirtyMarkerBackup);
            }
            else if (File.Exists(PersistentAssetIndexCache.DirtyMarkerPath))
            {
                File.Delete(PersistentAssetIndexCache.DirtyMarkerPath);
            }
        }

        [Test]
        public void SynchronizationOutcome_SyncFailureAndRecovery()
        {
            var record = new AssetRecord("outcome-recovered", "Assets/OutcomeRecovered.asset", "OutcomeRecovered", "TestAsset");
            var source = new FaultOnceSynchronousSource(record);
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: false);
            AssetIndexSynchronizationResult failure = null;

            LogAssert.Expect(LogType.Warning, "[Zep] Synchronous synchronization failed (IOException: transient synchronous enumeration); the current synchronization was retired and can be retried.");
            synchronizer.SynchronizeWithOutcome(result => failure = result);

            Assert.That(failure.Succeeded, Is.False);
            Assert.That(failure.ErrorMessage, Does.Contain("Synchronous synchronization"));
            Assert.That(failure.ErrorMessage, Does.Contain("IOException"));
            Assert.That(index.Count, Is.Zero);

            AssetIndexSynchronizationResult recovery = null;
            synchronizer.SynchronizeWithOutcome(result => recovery = result);
            Assert.That(recovery.Succeeded, Is.True);
            Assert.That(recovery.ErrorMessage, Is.Empty);
            Assert.That(index.TryGetByPath(record.AssetPath, out _), Is.True);
        }

        [Test]
        public void SynchronizationOutcome_AsyncFallbackDistinguishesFailure()
        {
            var record = new AssetRecord("outcome-fallback", "Assets/OutcomeFallback.asset", "OutcomeFallback", "TestAsset");
            var source = new MutableSnapshotSource(new[] { record });
            using var synchronizer = new AssetIndexSynchronizer(source, source, new InMemoryAssetIndex(), enablePersistentCache: false);
            AssetIndexSynchronizationResult result = null;

            synchronizer.SynchronizeAsyncWithOutcome(null, value => result = value);

            Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.SyncFallback));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.ErrorMessage, Is.Empty);
        }

        [Test]
        public void SynchronizationOutcome_MixedWaitersAndReentrancy()
        {
            var record = new AssetRecord("outcome-waiter", "Assets/OutcomeWaiter.asset", "OutcomeWaiter", "TestAsset");
            var source = new MutableSnapshotSource(new[] { record });
            using var synchronizer = new AssetIndexSynchronizer(source, source, new InMemoryAssetIndex(), enablePersistentCache: false);
            var callbacks = new List<string>();

            synchronizer.Synchronize(() => callbacks.Add("legacy"));
            synchronizer.SynchronizeWithOutcome(result => callbacks.Add(result.Succeeded ? "outcome" : "failure"));

            Assert.That(callbacks, Is.EqualTo(new[] { "legacy", "outcome" }));
        }

        [Test]
        public void SynchronizationOutcome_DisposalAndSuccessContracts()
        {
            var success = AssetIndexSynchronizationResult.Success;
            Assert.That(success.Succeeded, Is.True);
            Assert.That(success.ErrorMessage, Is.Empty);
            Assert.Throws<ArgumentException>(() => AssetIndexSynchronizationResult.Failure(null));
            Assert.Throws<ArgumentException>(() => AssetIndexSynchronizationResult.Failure(string.Empty));
            Assert.Throws<ArgumentException>(() => AssetIndexSynchronizationResult.Failure("   "));

            var source = new MutableSnapshotSource(Array.Empty<AssetRecord>());
            var synchronizer = new AssetIndexSynchronizer(source, source, new InMemoryAssetIndex(), enablePersistentCache: false);
            synchronizer.Dispose();
            Assert.Throws<ObjectDisposedException>(() => synchronizer.SynchronizeWithOutcome(null));
        }

        [Test]
        public void AssetPostprocessor_UsesDomainReloadAwareCallback()
        {
            var callback = typeof(ZepAssetPostprocessor).GetMethod(
                "OnPostprocessAllAssets",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[]
                {
                    typeof(string[]),
                    typeof(string[]),
                    typeof(string[]),
                    typeof(string[]),
                    typeof(bool)
                },
                modifiers: null);

            Assert.That(callback, Is.Not.Null,
                "Unity must be able to pass didDomainReload into the package callback");
        }

        // A dirty marker no longer refuses the snapshot: it is loaded, and the
        // marker asks for a verification until a save that captured it clears it.
        [Test]
        public void DirtyMarker_LoadsTheSnapshotAndAsksForAVerificationUntilASaveClearsIt()
        {
            UseStableCaptureOverrides();
            var record = new AssetRecord(
                "dirty-marker-guid",
                "Assets/DirtyMarker.asset",
                "DirtyMarker",
                "TestAsset");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { record },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);

            PersistentAssetIndexCache.MarkDirty();
            Assert.That(File.Exists(PersistentAssetIndexCache.DirtyMarkerPath), Is.True);
            Assert.That(
                PersistentAssetIndexCache.TryLoad(out var loaded, out _),
                Is.True,
                "a durable pre-hydration mutation marker is a reason to verify, not to refuse");
            Assert.That(loaded.Single().Guid, Is.EqualTo(record.Guid));
            PersistentAssetIndexCache.CapturePendingChangesWithRecovery(out var dirty);
            Assert.That(dirty.RequiresVerification, Is.True);

            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { record },
                    Array.Empty<AssetFolderRecord>(),
                    PersistentAssetIndexCache.CaptureArtifactDependencyVersion(),
                    dirty),
                Is.True);
            Assert.That(File.Exists(PersistentAssetIndexCache.DirtyMarkerPath), Is.False);
            PersistentAssetIndexCache.CapturePendingChangesWithRecovery(out var clean);
            Assert.That(clean.RequiresVerification, Is.False);
            Assert.That(PersistentAssetIndexCache.TryLoad(out var records, out _), Is.True);
            Assert.That(records.Single().Guid, Is.EqualTo(record.Guid));
        }

        [Test]
        public void PendingChangeJournal_SurvivesSessionLossAndPatchesSnapshotAfterRestart()
        {
            UseStableCaptureOverrides();
            var deleted = new AssetRecord(
                "restart-deletion-guid",
                "Assets/DeletedBeforeRestart.png",
                "DeletedBeforeRestart",
                "Texture2D");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { deleted },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { deleted.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            Assert.That(JournalHasBatches(), Is.True);

            // SessionState is empty in a newly launched editor process. The Library
            // journal must remain sufficient without any process-local state.
            var index = new InMemoryAssetIndex();
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => Assert.Fail("A durable known deletion must not cold scan."),
                onComplete: () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(deleted.AssetPath, out _), Is.False);
            Assert.That(JournalHasBatches(), Is.False,
                "the journal is cleared only after the patched cache is atomically saved");
        }

        // An append that cannot be committed leaves the earlier batch files as they
        // were and marks the journal as incomplete: the next load still hydrates
        // the snapshot, and verifies it against a walk.
        [Test]
        public void PendingChangeJournal_FailedBatchCommit_MarksTheJournalForVerification()
        {
            var first = new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { "Assets/First.asset" },
                Array.Empty<string>(),
                Array.Empty<string>());
            PersistentAssetIndexCache.RecordPendingChanges(first);
            var firstFiles = BatchFiles();
            Assert.That(firstFiles, Has.Length.EqualTo(1), "premise: one batch file");
            PersistentAssetIndexCache.PendingChangesCommitOverride = (_, __) =>
                throw new IOException("simulated commit interruption");
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Could not persist reload asset changes"));

            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { "Assets/Second.asset" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));

            Assert.That(
                BatchFiles(),
                Is.EqualTo(firstFiles),
                "an interrupted append must leave the earlier batches intact");
            var recovered = PersistentAssetIndexCache.CapturePendingChangesWithRecovery(
                out var state);
            Assert.That(recovered, Is.Empty,
                "a journal missing the interrupted callback is not a complete account");
            Assert.That(state.RequiresVerification, Is.True,
                "losing the newest batch means the next load verifies");
            Assert.That(
                File.Exists(PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                Is.True);
        }

        // A batch file the parser rejects makes the journal untrusted — every
        // observation says so with the same dirty token — and the snapshot still
        // loads, from any thread: the verification that follows is what settles it.
        [Test]
        public void MalformedJournal_RepeatedObservationIsStable_AndTheSnapshotStillLoads()
        {
            UseStableCaptureOverrides();
            MalformedReset();
            Assert.That(RecoveryCache.Save(new[] { MalformedRecord("stable") }, Array.Empty<AssetFolderRecord>()), Is.True);
            Assert.That(RecoveryCache.TryLoad(out _, out _), Is.True);
            WriteMalformedBatchFile("[");
            RecoveryCache.CapturePendingChangesWithRecovery(out var first);
            Assert.That(first.RequiresVerification, Is.True);
            Assert.That(first.HasMalformedJournalEvidence, Is.True);
            var token = File.ReadAllText(RecoveryCache.DirtyMarkerPath);
            for (var pass = 0; pass < 3; pass++)
            {
                RecoveryCache.CapturePendingChangesWithRecovery(out var again);
                Assert.That(again.RequiresVerification, Is.True);
                Assert.That(RecoveryCache.TryLoad(out _, out _), Is.True, "the snapshot is loaded; the malformed journal is verified after");
                Assert.That(File.ReadAllText(RecoveryCache.DirtyMarkerPath), Is.EqualTo(token), "repeated observation of the same damage is stable");
            }

            var artifactRevision = RecoveryCache.CaptureArtifactDependencyVersion();
            var worker = Task.Run(() =>
            {
                Assert.That(RecoveryCache.TryLoad(
                    artifactRevision,
                    RecoveryCache.CaptureRecoveryState(),
                    out _,
                    out _,
                    out _,
                    out _,
                    out _), Is.True, "the load needs nothing from the main thread");
            });
            Assert.That(worker.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(File.ReadAllText(RecoveryCache.DirtyMarkerPath), Is.EqualTo(token));

            // An authoritative save — a walk's — discards the damaged journal and
            // clears the token it recorded.
            RecoveryCache.CapturePendingChangesWithRecovery(out var untrusted);
            Assert.That(RecoveryCache.Save(new[] { MalformedRecord("stable") }, Array.Empty<AssetFolderRecord>(), artifactRevision, untrusted), Is.True);
            Assert.That(JournalHasBatches(), Is.False);
            Assert.That(File.Exists(RecoveryCache.DirtyMarkerPath), Is.False);
            RecoveryCache.CapturePendingChangesWithRecovery(out var clean);
            Assert.That(clean.RequiresVerification, Is.False);
        }

        [UnityTest]
        public IEnumerator VerificationWalk_CallbackDuringTheWalk_IsCoveredByTheSaveThatFollows()
        {
            UseStableCaptureOverrides();
            var current = new AssetRecord(
                "scan-epoch-current-guid",
                "Assets/ScanEpochCurrent.asset",
                "ScanEpochCurrent",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()), Is.True);
            Directory.CreateDirectory(PersistentAssetIndexCache.CacheDirectory);
            File.WriteAllText(
                PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath,
                "before-scan");
            PersistentAssetIndexCache.MarkDirty();
            var source = new BlockingSnapshotSource(new[] { current });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(null, () => completed = true);
                for (var frame = 0; frame < 100 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True);
                PersistentAssetIndexCache.RecordPendingChanges(
                    new ProjectAssetChanges(
                        new[] { current.AssetPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                Assert.That(File.ReadAllText(
                    PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                    Is.Not.EqualTo("before-scan"));
                source.ReleaseEnumeration();
                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True, "the operation completed at hydration; the walk is the verification's");
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
                // The save captures the journal and the markers when it begins —
                // after the walk — so the marker the callback refreshed is the one
                // it clears: what the walk published is the truth as of the save.
                synchronizer.FlushPendingSnapshotSave();
                Assert.That(File.Exists(
                    PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                    Is.False,
                    "an authoritative save covers the marker refreshed during its walk");
                Assert.That(File.Exists(
                    PersistentAssetIndexCache.DirtyMarkerPath), Is.False);
                Assert.That(PersistentAssetIndexCache.TryLoad(out _, out _), Is.True);
                PersistentAssetIndexCache.CapturePendingChangesWithRecovery(out var after);
                Assert.That(after.RequiresVerification, Is.False);
            }
            finally
            {
                source.ReleaseEnumeration();
            }
        }

        // A batch journaled while the snapshot is being read is the next thing to
        // replay, not a reason to refuse the snapshot: the journal is still a
        // complete account of everything after it, one batch longer. Before this,
        // Unity's post-compile import pass of a NEW script — landing while a real
        // project's snapshot was still deserializing on the worker — cold-scanned
        // every script creation. Markers, poison and an untrusted dirty token keep
        // rejecting (the cases below): those mean the journal is NOT complete.
        [Test]
        public void CacheLoad_JournalAppendDuringDeserialization_StillLoads()
        {
            UseStableCaptureOverrides();
            var record = new AssetRecord(
                "load-append-guid",
                "Assets/LoadAppend.asset",
                "LoadAppend",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { record },
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.CacheDeserializedOverride = () =>
                PersistentAssetIndexCache.RecordPendingChanges(
                    new ProjectAssetChanges(
                        new[] { "Assets/DuringLoad.asset" },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));
            Assert.That(PersistentAssetIndexCache.TryLoad(out var loaded, out _), Is.True,
                "a journal that only grew is still a complete account; the load stands");
            Assert.That(loaded.Select(item => item.AssetPath),
                Is.EqualTo(new[] { record.AssetPath }),
                "the snapshot is the committed one; the appended batch is the caller's to replay");
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Not.Empty,
                "and that batch is still in the journal for it");
        }

        [Test]
        public void CacheLoad_AtomicReplacementDuringDeserializationRejectsOpenedSnapshot()
        {
            UseStableCaptureOverrides();
            var oldRecord = new AssetRecord(
                "replaced-cache-old-guid",
                "Assets/ReplacedCacheOld.asset",
                "ReplacedCacheOld",
                "TestAsset");
            var newRecord = new AssetRecord(
                "replaced-cache-new-guid",
                "Assets/ReplacedCacheNew.asset",
                "ReplacedCacheNew",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { oldRecord },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var oldBytes = File.ReadAllBytes(
                PersistentAssetIndexCache.CacheFilePath);
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { newRecord },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var replacementBytes = File.ReadAllBytes(
                PersistentAssetIndexCache.CacheFilePath);
            File.WriteAllBytes(
                PersistentAssetIndexCache.CacheFilePath,
                oldBytes);
            PersistentAssetIndexCache.CacheDeserializedOverride = () =>
            {
                var replacementPath =
                    PersistentAssetIndexCache.CacheFilePath + ".replacement";
                File.WriteAllBytes(replacementPath, replacementBytes);
                File.Replace(
                    replacementPath,
                    PersistentAssetIndexCache.CacheFilePath,
                    null);
            };

            Assert.That(PersistentAssetIndexCache.TryLoad(out _, out _), Is.False,
                "hydration must remain bound to the snapshot identity opened at the start");
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_OverlappingRunCannotRetireLaterUnresolvedImport()
        {
            var stale = new AssetRecord(
                "overlap-stale-guid",
                "Assets/OverlapStale.asset",
                "OverlapStale",
                "TestAsset");
            var current = new AssetRecord(
                "overlap-current-guid",
                "Assets/OverlapCurrent.asset",
                "OverlapCurrent",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { stale },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var firstEntered = new ManualResetEventSlim(false);
            var secondEntered = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var releaseSecond = new ManualResetEventSlim(false);
            var loadCount = 0;
            PersistentAssetIndexCache.CacheDeserializedOverride = () =>
            {
                var invocation = Interlocked.Increment(ref loadCount);
                if (invocation == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait();
                }
                else if (invocation == 2)
                {
                    secondEntered.Set();
                    releaseSecond.Wait();
                }
            };
            var source = new MutableSnapshotSource(new[] { current });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var firstCompleted = false;
            var secondCompleted = false;
            var retireCount = 0;
            PersistentAssetIndexCache.PendingChangesRetireOverride = path =>
            {
                retireCount++;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            };
            Assert.That(PersistentAssetIndexCache.HasTestOverrides, Is.False,
                "the validation overlap requires the production worker path");

            try
            {
                synchronizer.SynchronizeAsync(null, () => firstCompleted = true);
                for (var frame = 0; frame < 1000 && !firstEntered.IsSet; frame++)
                {
                    yield return null;
                }
                Assert.That(firstEntered.IsSet, Is.True);

                PersistentAssetIndexCache.RecordPendingChanges(
                    new ProjectAssetChanges(
                        new[] { current.AssetPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                synchronizer.SynchronizeAsync(null, () => secondCompleted = true);
                releaseFirst.Set();
                for (var frame = 0; frame < 1000 && !secondEntered.IsSet; frame++)
                {
                    yield return null;
                }
                Assert.That(secondEntered.IsSet, Is.True);

                for (var frame = 0; frame < 5 && !firstCompleted; frame++)
                {
                    yield return null;
                }
                Assert.That(firstCompleted, Is.False,
                    "a superseded caller must wait for the latest run to hydrate");
                Assert.That(
                    PersistentAssetIndexCache.CapturePendingChanges(),
                    Has.Count.EqualTo(1),
                    "the superseded operation must not retire the later generation");

                releaseSecond.Set();
                for (var frame = 0; frame < 100 && !secondCompleted; frame++)
                {
                    yield return null;
                }
                Assert.That(secondCompleted, Is.True);
                Assert.That(firstCompleted, Is.True,
                    "the latest hydration must release every coalesced caller");
                // The import the source cannot build is verified by a walk after
                // the hydration completed; its save retires the journal.
                yield return WaitForBackgroundJob(synchronizer);
                synchronizer.FlushPendingSnapshotSave();
                Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(record => record.AssetPath),
                    Is.EqualTo(new[] { current.AssetPath }));
                Assert.That(retireCount, Is.EqualTo(1),
                    "only the latest validation run may retire the journal");
                Assert.That(
                    synchronizer.SynchronizationOperationGenerationForTests,
                    Is.EqualTo(2),
                    "a superseded validation must not create a replacement run");
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_OverlapDuringTypeCaptureOnlyLatestRunReachesEnumeration()
        {
            PersistentAssetIndexCache.Invalidate();
            PersistentAssetIndexCache.UnityLabelSnapshotOverride = () =>
                new Dictionary<string, IReadOnlyList<string>>();
            PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = null;
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 42;
            var current = new AssetRecord(
                "type-overlap-current-guid",
                "Assets/TypeOverlapCurrent.asset",
                "TypeOverlapCurrent",
                "TestAsset");
            var source = new CountingThreadSafeSource(new[] { current });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var firstCompleted = false;
            var secondCompleted = false;

            synchronizer.SynchronizeAsync(null, () => firstCompleted = true);
            synchronizer.SynchronizeAsync(null, () => secondCompleted = true);

            for (var frame = 0; frame < 300 && !secondCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(secondCompleted, Is.True);
            Assert.That(firstCompleted, Is.True);
            Assert.That(source.PrepareCount, Is.EqualTo(1),
                "the superseded type-capture run must stop before enumeration");
            Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
            Assert.That(
                synchronizer.SynchronizationOperationGenerationForTests,
                Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_OverlapDuringWorkerScanPublishesOnlyLatestSnapshot()
        {
            var stale = new AssetRecord(
                "worker-overlap-stale-guid",
                "Assets/WorkerOverlapStale.asset",
                "WorkerOverlapStale",
                "TestAsset");
            var current = new AssetRecord(
                "worker-overlap-current-guid",
                "Assets/WorkerOverlapCurrent.asset",
                "WorkerOverlapCurrent",
                "TestAsset");
            var staleFolder = new AssetFolderRecord(
                AssetPath.From("Assets/WorkerOverlapStale"),
                "worker-overlap-stale-folder-guid");
            var currentFolder = new AssetFolderRecord(
                AssetPath.From("Assets/WorkerOverlapCurrent"),
                "worker-overlap-current-folder-guid");
            var source = new SequencedBlockingSnapshotSource(
                new[] { stale },
                new[] { current },
                new[] { staleFolder },
                new[] { currentFolder });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            var firstCompleted = false;
            var secondCompleted = false;

            try
            {
                synchronizer.SynchronizeAsync(null, () => firstCompleted = true);
                for (var frame = 0; frame < 100 && !source.FirstStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.FirstStarted.IsSet, Is.True);
                synchronizer.SynchronizeAsync(null, () => secondCompleted = true);
                for (var frame = 0; frame < 5; frame++)
                {
                    yield return null;
                }

                Assert.That(source.SecondStarted.IsSet, Is.False,
                    "the replacement must wait until the mutable source has supplied both snapshots");
                Assert.That(source.ConcurrentAccessDetected, Is.False);
                source.ReleaseFirst();
                for (var frame = 0; frame < 100 && !source.SecondStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.SecondStarted.IsSet, Is.True);
                Assert.That(firstCompleted, Is.False);
                Assert.That(secondCompleted, Is.False);
                Assert.That(index.Count, Is.Zero,
                    "the superseded worker snapshot must never publish");

                source.ReleaseSecond();
                for (var frame = 0; frame < 100 && !secondCompleted; frame++)
                {
                    yield return null;
                }

                Assert.That(secondCompleted, Is.True);
                Assert.That(firstCompleted, Is.True);
                Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(index.TryGetFolder(currentFolder.FolderPath.Value, out _), Is.True);
                Assert.That(index.TryGetFolder(staleFolder.FolderPath.Value, out _), Is.False);
                Assert.That(source.ConcurrentAccessDetected, Is.False);
                Assert.That(
                    synchronizer.SynchronizationOperationGenerationForTests,
                    Is.EqualTo(2));
            }
            finally
            {
                source.ReleaseFirst();
                source.ReleaseSecond();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_OverlapDuringSyncFallbackCannotPublishStaleOuterRun()
        {
            var stale = new AssetRecord(
                "fallback-overlap-stale-guid",
                "Assets/FallbackOverlapStale.asset",
                "FallbackOverlapStale",
                "TestAsset");
            var current = new AssetRecord(
                "fallback-overlap-current-guid",
                "Assets/FallbackOverlapCurrent.asset",
                "FallbackOverlapCurrent",
                "TestAsset");
            var source = new ReentrantFallbackSource(stale, current);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            var firstCompleted = false;
            var secondCompleted = false;
            source.StartReplacement = () => synchronizer.SynchronizeAsync(
                null,
                () => secondCompleted = true);

            synchronizer.SynchronizeAsync(null, () => firstCompleted = true);

            for (var frame = 0; frame < 100 && !secondCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(secondCompleted, Is.True);
            Assert.That(firstCompleted, Is.True);
            Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
            Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False,
                "the outer fallback must re-check ownership before publication");
            Assert.That(
                synchronizer.SynchronizationOperationGenerationForTests,
                Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Synchronize_DuringActiveWorkerReturnsPromptlyAndAdoptsItsSnapshot()
        {
            var authoritative = new AssetRecord(
                "refresh-adopted-guid",
                "Assets/RefreshAdopted.asset",
                "RefreshAdopted",
                "TestAsset");
            var obsoleteSecond = new AssetRecord(
                "refresh-second-guid",
                "Assets/RefreshSecond.asset",
                "RefreshSecond",
                "TestAsset");
            var source = new SequencedBlockingSnapshotSource(
                new[] { authoritative },
                new[] { obsoleteSecond });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            var initialCompletions = 0;
            var refreshCompletions = 0;

            try
            {
                synchronizer.SynchronizeAsync(
                    null,
                    () => initialCompletions++);
                for (var frame = 0; frame < 100 && !source.FirstStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.FirstStarted.IsSet, Is.True);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                synchronizer.Synchronize(() => refreshCompletions++);
                elapsed.Stop();

                Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(100),
                    "manual refresh must never wait behind the worker on Unity's main thread");
                Assert.That(refreshCompletions, Is.Zero);
                Assert.That(
                    synchronizer.SynchronizationOperationGenerationForTests,
                    Is.EqualTo(1),
                    "refresh must adopt the authoritative operation instead of superseding it");

                source.ReleaseFirst();
                for (var frame = 0; frame < 100 && refreshCompletions == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(initialCompletions, Is.EqualTo(1));
                Assert.That(refreshCompletions, Is.EqualTo(1));
                Assert.That(index.TryGetByPath(authoritative.AssetPath, out _), Is.True);
                Assert.That(index.TryGetByPath(obsoleteSecond.AssetPath, out _), Is.False);
                Assert.That(source.EnumerationCount, Is.EqualTo(1),
                    "adopting the worker result must not pay for an obsolete scan and a replacement scan");
                Assert.That(source.SecondStarted.IsSet, Is.False);
            }
            finally
            {
                source.ReleaseFirst();
                source.ReleaseSecond();
                synchronizer.Dispose();
            }
        }

        [Test]
        public void Synchronize_UncontendedSourceFaultRetiresOperationAndLaterRefreshRecovers()
        {
            var recovered = new AssetRecord(
                "uncontended-recovery-guid",
                "Assets/UncontendedRecovery.asset",
                "UncontendedRecovery",
                "TestAsset");
            var source = new FaultOnceSynchronousSource(recovered);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            var completions = 0;
            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Synchronous synchronization failed (IOException: transient synchronous enumeration); the current synchronization was retired and can be retried.");

            Assert.DoesNotThrow(() => synchronizer.Synchronize(() => completions++));

            Assert.That(completions, Is.EqualTo(1));
            Assert.That(index.Count, Is.Zero);

            Assert.DoesNotThrow(() => synchronizer.Synchronize(() => completions++));

            Assert.That(completions, Is.EqualTo(2));
            Assert.That(index.TryGetByPath(recovered.AssetPath, out _), Is.True);
            Assert.That(source.EnumerationCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator Synchronize_QueuedCrossInstanceFaultReleasesGateAndLaterRefreshRecovers()
        {
            var gateOwnerSource = new BlockingSnapshotSource(Array.Empty<AssetRecord>());
            var gateOwner = new AssetIndexSynchronizer(
                gateOwnerSource,
                gateOwnerSource,
                new InMemoryAssetIndex(),
                enablePersistentCache: false);
            var recovered = new AssetRecord(
                "queued-recovery-guid",
                "Assets/QueuedRecovery.asset",
                "QueuedRecovery",
                "TestAsset");
            var queuedSource = new FaultOnceSynchronousSource(recovered);
            var queuedIndex = new InMemoryAssetIndex();
            var queued = new AssetIndexSynchronizer(
                queuedSource,
                queuedSource,
                queuedIndex,
                enablePersistentCache: false);
            var ownerCompleted = false;
            var queuedCompletions = 0;

            try
            {
                gateOwner.SynchronizeAsync(null, () => ownerCompleted = true);
                for (var frame = 0;
                    frame < 100 && !gateOwnerSource.EnumerationStarted.IsSet;
                    frame++)
                {
                    yield return null;
                }

                Assert.That(gateOwnerSource.EnumerationStarted.IsSet, Is.True);
                queued.Synchronize(() => queuedCompletions++);
                Assert.That(queuedCompletions, Is.Zero);
                LogAssert.Expect(
                    LogType.Warning,
                    "[Zep] Synchronous synchronization failed (IOException: transient synchronous enumeration); the current synchronization was retired and can be retried.");

                gateOwnerSource.ReleaseEnumeration();
                for (var frame = 0;
                    frame < 100 && (queuedCompletions == 0 || !ownerCompleted);
                    frame++)
                {
                    yield return null;
                }

                Assert.That(ownerCompleted, Is.True);
                Assert.That(queuedCompletions, Is.EqualTo(1));
                Assert.That(queuedIndex.Count, Is.Zero);

                queued.Synchronize(() => queuedCompletions++);

                Assert.That(queuedCompletions, Is.EqualTo(2));
                Assert.That(queuedIndex.TryGetByPath(recovered.AssetPath, out _), Is.True);
                Assert.That(queuedSource.EnumerationCount, Is.EqualTo(2));
            }
            finally
            {
                gateOwnerSource.ReleaseEnumeration();
                gateOwner.Dispose();
                queued.Dispose();
            }
        }

        [Test]
        public void SynchronizeAsync_ThrowingFullScanNotificationDoesNotStopScanOrWaiters()
        {
            var events = new List<string>();
            var source = new RecordingSource(events);
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                new InMemoryAssetIndex(),
                enablePersistentCache: false);
            var completions = 0;
            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Full-scan notification failed (InvalidOperationException: notification failed); synchronization will continue.");

            Assert.DoesNotThrow(() => synchronizer.SynchronizeAsync(
                onFullScanStarted: () =>
                    throw new InvalidOperationException("notification failed"),
                onComplete: () => completions++));

            Assert.That(completions, Is.EqualTo(1));
            Assert.That(events, Does.Contain("enumerate"));
        }

        [UnityTest]
        public IEnumerator Dispose_ActiveWorkerCannotPublishOrContendWithReplacementSynchronizer()
        {
            var stale = new AssetRecord(
                "disposed-stale-guid",
                "Assets/DisposedStale.asset",
                "DisposedStale",
                "TestAsset");
            var current = new AssetRecord(
                "replacement-current-guid",
                "Assets/ReplacementCurrent.asset",
                "ReplacementCurrent",
                "TestAsset");
            var source = new SequencedBlockingSnapshotSource(
                new[] { stale },
                new[] { current });
            var disposedIndex = new InMemoryAssetIndex();
            var replacementIndex = new InMemoryAssetIndex();
            var disposedSynchronizer = new AssetIndexSynchronizer(
                source,
                source,
                disposedIndex,
                enablePersistentCache: false);
            var replacementSynchronizer = new AssetIndexSynchronizer(
                source,
                source,
                replacementIndex,
                enablePersistentCache: false);
            var disposedCompleted = false;
            var replacementCompleted = false;

            try
            {
                disposedSynchronizer.SynchronizeAsync(
                    null,
                    () => disposedCompleted = true);
                for (var frame = 0; frame < 100 && !source.FirstStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.FirstStarted.IsSet, Is.True);
                var adoptedCompletion = false;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                disposedSynchronizer.Synchronize(
                    () => adoptedCompletion = true);
                elapsed.Stop();
                Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(100));
                Assert.That(
                    disposedSynchronizer.SynchronizationOperationGenerationForTests,
                    Is.EqualTo(1));
                disposedSynchronizer.Dispose();
                replacementSynchronizer.SynchronizeAsync(
                    null,
                    () => replacementCompleted = true);
                for (var frame = 0; frame < 5; frame++)
                {
                    yield return null;
                }

                Assert.That(source.SecondStarted.IsSet, Is.False,
                    "a new controller must wait for the uncancellable old source walk");
                source.ReleaseFirst();
                for (var frame = 0; frame < 100 && !source.SecondStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.SecondStarted.IsSet, Is.True);
                source.ReleaseSecond();
                for (var frame = 0; frame < 100 && !replacementCompleted; frame++)
                {
                    yield return null;
                }

                Assert.That(replacementCompleted, Is.True);
                Assert.That(disposedCompleted, Is.False);
                Assert.That(adoptedCompletion, Is.False,
                    "disposing the operation owner must retire adopted refresh callbacks");
                Assert.That(disposedIndex.Count, Is.Zero);
                Assert.That(replacementIndex.TryGetByPath(current.AssetPath, out _), Is.True);
                Assert.That(replacementIndex.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(source.ConcurrentAccessDetected, Is.False);
            }
            finally
            {
                source.ReleaseFirst();
                source.ReleaseSecond();
                disposedSynchronizer.Dispose();
                replacementSynchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ThrowingCompletionDoesNotSuppressLaterWaiters()
        {
            var first = new AssetRecord(
                "waiter-first-guid",
                "Assets/WaiterFirst.asset",
                "WaiterFirst",
                "TestAsset");
            var latest = new AssetRecord(
                "waiter-latest-guid",
                "Assets/WaiterLatest.asset",
                "WaiterLatest",
                "TestAsset");
            var source = new SequencedBlockingSnapshotSource(
                new[] { first },
                new[] { latest });
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                new InMemoryAssetIndex(),
                enablePersistentCache: false);
            var callbackCounts = new int[3];

            try
            {
                synchronizer.SynchronizeAsync(null, () =>
                {
                    callbackCounts[0]++;
                    throw new InvalidOperationException("waiter failed");
                });
                for (var frame = 0; frame < 100 && !source.FirstStarted.IsSet; frame++)
                {
                    yield return null;
                }

                synchronizer.SynchronizeAsync(null, () => callbackCounts[1]++);
                synchronizer.SynchronizeAsync(null, () => callbackCounts[2]++);
                source.ReleaseFirst();
                for (var frame = 0; frame < 100 && !source.SecondStarted.IsSet; frame++)
                {
                    yield return null;
                }

                LogAssert.Expect(
                    LogType.Exception,
                    new Regex("InvalidOperationException: waiter failed"));
                source.ReleaseSecond();
                for (var frame = 0; frame < 100 && callbackCounts[2] == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(callbackCounts, Is.EqualTo(new[] { 1, 1, 1 }));
                for (var frame = 0; frame < 3; frame++)
                {
                    yield return null;
                }

                Assert.That(callbackCounts, Is.EqualTo(new[] { 1, 1, 1 }),
                    "completion waiters must be retired before any callback runs");
            }
            finally
            {
                source.ReleaseFirst();
                source.ReleaseSecond();
                synchronizer.Dispose();
            }
        }

        [Test]
        public void PendingChangeJournal_CreateThenDelete_DoesNotResolveVanishedPathOrColdScan()
        {
            UseStableCaptureOverrides();
            Assert.That(
                PersistentAssetIndexCache.Save(
                    Array.Empty<AssetRecord>(),
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            const string transientPath = "Assets/Transient.asset";
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { transientPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { transientPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            var compacted = PersistentAssetIndexCache.CapturePendingChanges();
            Assert.That(compacted.Count, Is.EqualTo(1));
            Assert.That(compacted[0].Imported, Is.Empty);
            Assert.That(compacted[0].Deleted, Is.EqualTo(new[] { transientPath }));
            var index = new InMemoryAssetIndex();
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("a compacted transient change must not cold scan"),
                () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(JournalHasBatches(), Is.False);
        }

        [Test]
        public void PendingChangeJournal_ChainedMovesResolveOnlyFinalTarget()
        {
            UseStableCaptureOverrides();
            var original = new AssetRecord(
                "move-guid",
                "Assets/A.asset",
                "A",
                "TestAsset");
            var current = new AssetRecord(
                original.Guid,
                "Assets/C.asset",
                "C",
                "TestAsset");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { original },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "Assets/B.asset" },
                new[] { original.AssetPath }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { current.AssetPath },
                new[] { "Assets/B.asset" }));
            var compacted = PersistentAssetIndexCache.CapturePendingChanges();
            Assert.That(compacted.Count, Is.EqualTo(1));
            Assert.That(compacted[0].MovedFromPaths, Is.EqualTo(new[] { original.AssetPath }));
            Assert.That(compacted[0].Moved, Is.EqualTo(new[] { current.AssetPath }));
            var events = new List<string>();
            var source = new KnownChangeSource(current, events);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("a compacted move chain must not cold scan"),
                () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(events.Count(entry => entry == "build-record"), Is.EqualTo(1));
            Assert.That(index.TryGetByPath("Assets/B.asset", out _), Is.False);
            Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
        }

        [Test]
        public void PendingChangeJournal_RepeatedMoveHistoryStaysBoundedByFinalPaths()
        {
            const int moveCount = 200;
            for (var i = 0; i < moveCount; i++)
            {
                PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new[] { $"Assets/Step{i + 1}.asset" },
                    new[] { $"Assets/Step{i}.asset" }));
            }

            var compacted = PersistentAssetIndexCache.CapturePendingChanges();
            Assert.That(compacted.Count, Is.EqualTo(1));
            Assert.That(compacted[0].MovedFromPaths, Is.EqualTo(new[] { "Assets/Step0.asset" }));
            Assert.That(compacted[0].Moved, Is.EqualTo(new[] { $"Assets/Step{moveCount}.asset" }));
            Assert.That(
                JournalByteCount(),
                Is.LessThan(1024),
                "journal storage must depend on final paths, not callback history");
        }

        [Test]
        public void PendingChangeJournal_NonMergeableHistoryFallsBackWithinFixedBounds()
        {
            UseStableCaptureOverrides();
            var pairCount = PersistentAssetIndexCache.MaxPendingChangeBatchCount * 4;
            var staleRecords = new List<AssetRecord>(pairCount);
            var currentRecords = new List<AssetRecord>(pairCount);
            for (var i = 0; i < pairCount; i++)
            {
                staleRecords.Add(new AssetRecord(
                    $"bounded-stale-{i}",
                    $"Assets/Stale{i}.asset",
                    $"Stale{i}",
                    "TestAsset"));
                currentRecords.Add(new AssetRecord(
                    $"bounded-current-{i}",
                    $"Assets/Current{i}.asset",
                    $"Current{i}",
                    "TestAsset"));
            }

            Assert.That(PersistentAssetIndexCache.Save(
                staleRecords,
                Array.Empty<AssetFolderRecord>()), Is.True);
            for (var i = 0; i < pairCount; i++)
            {
                // Independent import/delete phases cannot be collapsed without
                // changing chronological replay semantics.
                PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                    new[] { currentRecords[i].AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { staleRecords[i].AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            }

            Assert.That(
                File.Exists(PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                Is.True);
            Assert.That(
                JournalHasBatches(),
                Is.False,
                "exact history must stop being rewritten once its fixed bound is reached");
            Assert.That(
                new FileInfo(
                    PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath).Length,
                Is.LessThan(128),
                "post-bound storage is one fixed-size authoritative-fallback token");
            Assert.That(
                PersistentAssetIndexCache.CapturePendingChanges(out var generation),
                Is.Empty);
            Assert.That(generation,
                Is.EqualTo(PersistentAssetIndexCache.UntrustedJournalGeneration));

            var source = new AuthoritativeSnapshotSource(
                currentRecords,
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => fullScanStarted = true,
                () => completed = true);
            synchronizer.FlushPendingSnapshotSave();

            Assert.That(fullScanStarted, Is.False,
                "the stale snapshot is loaded and verified against a walk, silently");
            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.GreaterThan(0));
            Assert.That(index.Enumerate().Select(record => record.AssetPath),
                Is.EquivalentTo(currentRecords.Select(record => record.AssetPath)));
            Assert.That(
                File.Exists(PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                Is.False,
                "only the verification's authoritative save retires fallback state");
            Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
            Assert.That(saved.Select(record => record.AssetPath),
                Is.EquivalentTo(currentRecords.Select(record => record.AssetPath)));
        }

        // The journal's entry bound decides whether exact replay is kept at all.
        // Deciding it only after compaction means a 2,000-move callback pays the
        // pairwise merge (one batch per moved pair, each merge attempt scanning the
        // accumulated moves) before the result is thrown away anyway.
        [Test]
        public void PendingChangeJournal_BatchPastTheEntryBound_FallsBackWithoutCompacting()
        {
            UseStableCaptureOverrides();
            const int pairCount = 2_000;
            var moved = new string[pairCount];
            var movedFrom = new string[pairCount];
            for (var i = 0; i < pairCount; i++)
            {
                moved[i] = $"Assets/Moved/Item{i}.asset";
                movedFrom[i] = $"Assets/Source/Item{i}.asset";
            }

            var mergeAttempts = 0;
            PersistentAssetIndexCache.PendingChangeBatchMergeObserverForTests =
                () => mergeAttempts++;

            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                moved,
                movedFrom));

            Assert.That(
                mergeAttempts,
                Is.Zero,
                "a batch already past the entry bound must not be compacted first");
            Assert.That(
                File.Exists(PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                Is.True,
                "the fail-closed outcome is unchanged: an authoritative scan must be marked");
            Assert.That(
                JournalHasBatches(),
                Is.False);
            Assert.That(
                PersistentAssetIndexCache.CapturePendingChanges(out var generation),
                Is.Empty);
            Assert.That(
                generation,
                Is.EqualTo(PersistentAssetIndexCache.UntrustedJournalGeneration));
        }

        // The discriminating half of the case above: the hoisted bound must not be a
        // blanket "stop compacting moves", so a batch that fits still compacts and
        // still produces an exactly replayable journal.
        [Test]
        public void PendingChangeJournal_BatchInsideTheEntryBound_StillCompacts()
        {
            UseStableCaptureOverrides();
            const int pairCount = 400;
            var moved = new string[pairCount];
            var movedFrom = new string[pairCount];
            for (var i = 0; i < pairCount; i++)
            {
                moved[i] = $"Assets/Moved/Item{i}.asset";
                movedFrom[i] = $"Assets/Source/Item{i}.asset";
            }

            var mergeAttempts = 0;
            PersistentAssetIndexCache.PendingChangeBatchMergeObserverForTests =
                () => mergeAttempts++;

            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                moved,
                movedFrom));

            Assert.That(
                mergeAttempts,
                Is.GreaterThan(0),
                "an in-bound batch must still go through compaction");
            Assert.That(
                File.Exists(PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath),
                Is.False,
                "an in-bound batch must keep exact replay");
            var compacted = PersistentAssetIndexCache.CapturePendingChanges();
            Assert.That(compacted.Count, Is.EqualTo(1));
            Assert.That(compacted[0].Moved.Length, Is.EqualTo(pairCount));
            Assert.That(compacted[0].MovedFromPaths.Length, Is.EqualTo(pairCount));
        }

        [Test]
        public void PendingChangeJournal_FolderMovesWithSourceReuseReplayChronologically()
        {
            UseStableCaptureOverrides();
            const string firstSource = "Assets/Z";
            const string firstTarget = "Assets/A";
            const string secondSource = "Assets/B";
            const string secondTarget = firstSource;
            var firstRecord = new AssetRecord(
                "folder-reuse-first-guid",
                firstSource + "/z.asset",
                "z",
                "TestAsset");
            var secondRecord = new AssetRecord(
                "folder-reuse-second-guid",
                secondSource + "/b.asset",
                "b",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { firstRecord, secondRecord },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(firstSource)),
                    new AssetFolderRecord(AssetPath.From(secondSource)),
                }), Is.True);

            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { firstTarget },
                new[] { firstSource }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { secondTarget },
                new[] { secondSource }));

            var compacted = PersistentAssetIndexCache.CapturePendingChanges();
            Assert.That(compacted.Count, Is.EqualTo(2));
            Assert.That(compacted[0].MovedFromPaths, Is.EqualTo(new[] { firstSource }));
            Assert.That(compacted[1].MovedFromPaths, Is.EqualTo(new[] { secondSource }));
            var source = new AuthoritativeSnapshotSource(
                new[]
                {
                    new AssetRecord(
                        firstRecord.Guid,
                        firstTarget + "/z.asset",
                        firstRecord.Name,
                        firstRecord.TypeName),
                    new AssetRecord(
                        secondRecord.Guid,
                        secondTarget + "/b.asset",
                        secondRecord.Name,
                        secondRecord.TypeName),
                },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(firstTarget)),
                    new AssetFolderRecord(AssetPath.From(secondTarget)),
                });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("known folder moves must not show the cold overlay"),
                () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(index.TryGetByPath(firstTarget + "/z.asset", out var movedFirst), Is.True);
            Assert.That(movedFirst.Guid, Is.EqualTo(firstRecord.Guid));
            Assert.That(index.TryGetByPath(secondTarget + "/b.asset", out var movedSecond), Is.True);
            Assert.That(movedSecond.Guid, Is.EqualTo(secondRecord.Guid));
            Assert.That(index.TryGetByPath(firstTarget + "/b.asset", out _), Is.False);
        }

        [Test]
        public void PendingChangeJournal_FileMovesWithTargetSourceReuseKeepBothIdentities()
        {
            UseStableCaptureOverrides();
            const string vacatedPath = "Assets/Z.asset";
            const string firstTarget = "Assets/A.asset";
            const string secondSource = "Assets/B.asset";
            var first = new AssetRecord("file-reuse-first-guid", vacatedPath, "Z", "TestAsset");
            var second = new AssetRecord("file-reuse-second-guid", secondSource, "B", "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { first, second },
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { firstTarget },
                new[] { vacatedPath }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { vacatedPath },
                new[] { secondSource }));

            var source = new AuthoritativeSnapshotSource(
                new[]
                {
                    new AssetRecord(first.Guid, firstTarget, first.Name, first.TypeName),
                    new AssetRecord(second.Guid, vacatedPath, second.Name, second.TypeName),
                },
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("known file moves must not show the cold overlay"),
                () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(index.TryGetByPath(firstTarget, out var movedFirst), Is.True);
            Assert.That(movedFirst.Guid, Is.EqualTo(first.Guid));
            Assert.That(index.TryGetByPath(vacatedPath, out var movedSecond), Is.True);
            Assert.That(movedSecond.Guid, Is.EqualTo(second.Guid));
        }

        [Test]
        public void PendingChangeJournal_DeleteThenFolderMoveIntoDeletedPathKeepsMovedSubtree()
        {
            UseStableCaptureOverrides();
            const string replacedPath = "Assets/Z";
            const string movedSource = "Assets/B";
            var stale = new AssetRecord(
                "folder-reuse-stale-guid",
                replacedPath + "/stale.asset",
                "stale",
                "TestAsset");
            var moved = new AssetRecord(
                "folder-reuse-current-guid",
                movedSource + "/current.asset",
                "current",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { stale, moved },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(replacedPath)),
                    new AssetFolderRecord(AssetPath.From(movedSource)),
                }), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { replacedPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { replacedPath },
                new[] { movedSource }));

            var source = new AuthoritativeSnapshotSource(
                new[]
                {
                    new AssetRecord(
                        moved.Guid,
                        replacedPath + "/current.asset",
                        moved.Name,
                        moved.TypeName),
                },
                new[] { new AssetFolderRecord(AssetPath.From(replacedPath)) });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("known path reuse must not show the cold overlay"),
                () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
            Assert.That(index.TryGetByPath(replacedPath + "/current.asset", out var current), Is.True);
            Assert.That(current.Guid, Is.EqualTo(moved.Guid));
        }

        [Test]
        public void CacheOverlay_DeleteThenFolderMovePreservesCachedSourceSubtreeForReplay()
        {
            UseStableCaptureOverrides();
            const string recreatedAncestor = "Assets/Recreated";
            const string moveTarget = recreatedAncestor + "/Moved";
            const string moveSource = "Assets/Source";
            const string childSource = moveSource + "/Nested/Current.asset";
            const string childTarget = moveTarget + "/Nested/Current.asset";
            var stale = new AssetRecord(
                "overlay-folder-stale-guid",
                recreatedAncestor + "/Stale.asset",
                "Stale",
                "TestAsset");
            var moved = new AssetRecord(
                "overlay-folder-current-guid",
                childSource,
                "Current",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { stale, moved },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(recreatedAncestor)),
                    new AssetFolderRecord(AssetPath.From(moveSource)),
                    new AssetFolderRecord(AssetPath.From(moveSource + "/Nested")),
                }), Is.True);
            var deletion = new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { recreatedAncestor },
                Array.Empty<string>(),
                Array.Empty<string>());
            var move = new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { moveTarget },
                new[] { moveSource });
            PersistentAssetIndexCache.RecordPendingChanges(deletion);
            PersistentAssetIndexCache.RecordPendingChanges(move);

            var source = new AuthoritativeSnapshotSource(
                new[]
                {
                    new AssetRecord(
                        moved.Guid,
                        childTarget,
                        moved.Name,
                        moved.TypeName),
                },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(recreatedAncestor)),
                    new AssetFolderRecord(AssetPath.From(moveTarget)),
                    new AssetFolderRecord(AssetPath.From(moveTarget + "/Nested")),
                });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            synchronizer.RecordConcreteChanges(deletion);
            synchronizer.RecordConcreteChanges(move);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("an exact folder move must replay over the hydrated cache"),
                () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
            Assert.That(index.TryGetByPath(childSource, out _), Is.False);
            Assert.That(index.TryGetByPath(childTarget, out var current), Is.True);
            Assert.That(current.Guid, Is.EqualTo(moved.Guid));
            Assert.That(index.TryGetFolder(moveTarget + "/Nested", out _), Is.True);
            Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
            Assert.That(saved.Select(record => record.AssetPath),
                Is.EqualTo(new[] { childTarget }));
        }

        [Test]
        public void PendingChangeJournal_SwapThroughTemporaryPath_IsVerifiedWithoutAScan()
        {
            UseStableCaptureOverrides();
            const string firstPath = "Assets/A.asset";
            const string secondPath = "Assets/B.asset";
            const string temporaryPath = "Assets/Swap.tmp";
            var first = new AssetRecord("swap-first-guid", firstPath, "A", "TestAsset");
            var second = new AssetRecord("swap-second-guid", secondPath, "B", "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { first, second },
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { temporaryPath },
                new[] { firstPath }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { firstPath },
                new[] { secondPath }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { secondPath },
                new[] { temporaryPath }));

            Assert.That(PersistentAssetIndexCache.CapturePendingChanges().Count, Is.EqualTo(3));
            var source = new AuthoritativeSnapshotSource(
                new[]
                {
                    new AssetRecord(second.Guid, firstPath, second.Name, second.TypeName),
                    new AssetRecord(first.Guid, secondPath, first.Name, first.TypeName),
                },
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("a hydrated fallback must not show the cold overlay"),
                () => completed = true);

            Assert.That(completed, Is.True);
            synchronizer.FlushPendingSnapshotSave();
            Assert.That(source.EnumerationCount, Is.GreaterThan(0), "the unresolvable move is settled by the verification walk");
            Assert.That(index.TryGetByPath(firstPath, out var atFirst), Is.True);
            Assert.That(atFirst.Guid, Is.EqualTo(second.Guid));
            Assert.That(index.TryGetByPath(secondPath, out var atSecond), Is.True);
            Assert.That(atSecond.Guid, Is.EqualTo(first.Guid));
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
        }

        [Test]
        public void SynchronizeAsync_ThrowingImportedPathResolution_IsVerifiedWithoutAScan()
        {
            UseStableCaptureOverrides();
            var imported = new AssetRecord(
                "throwing-import-guid",
                "Assets/Imported.asset",
                "Imported",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { imported.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var source = new AuthoritativeSnapshotSource(
                new[] { imported },
                Array.Empty<AssetFolderRecord>(),
                throwRecordPath: imported.AssetPath);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Pending asset change resolution failed .* will be verified"));

            Assert.DoesNotThrow(() => synchronizer.SynchronizeAsync(
                () => Assert.Fail("a hydrated fallback must not show the cold overlay"),
                () => completed = true));

            Assert.That(completed, Is.True);
            synchronizer.FlushPendingSnapshotSave();
            Assert.That(source.EnumerationCount, Is.GreaterThan(0));
            Assert.That(index.TryGetByPath(imported.AssetPath, out _), Is.True);
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                "the journal retires when the verification saves");
        }

        [Test]
        public void SynchronizeAsync_ThrowingMovedFolderResolution_IsVerifiedWithoutAScan()
        {
            UseStableCaptureOverrides();
            const string oldPath = "Assets/OldFolder";
            const string newPath = "Assets/NewFolder";
            var oldFolder = new AssetFolderRecord(AssetPath.From(oldPath));
            var newFolder = new AssetFolderRecord(AssetPath.From(newPath));
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                new[] { oldFolder }), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { newPath },
                new[] { oldPath }));
            var source = new AuthoritativeSnapshotSource(
                Array.Empty<AssetRecord>(),
                new[] { newFolder },
                throwFolderPath: newPath);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Pending asset change resolution failed .* will be verified"));

            Assert.DoesNotThrow(() => synchronizer.SynchronizeAsync(
                () => Assert.Fail("a hydrated fallback must not show the cold overlay"),
                () => completed = true));

            Assert.That(completed, Is.True);
            synchronizer.FlushPendingSnapshotSave();
            Assert.That(source.EnumerationCount, Is.GreaterThan(0));
            Assert.That(index.TryGetFolder(oldPath, out _), Is.False);
            Assert.That(index.TryGetFolder(newPath, out _), Is.True);
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
        }

        [Test]
        public void PendingChangeJournal_AncestorDeleteThenChildImport_PersistsReachableRestartSnapshot()
        {
            UseStableCaptureOverrides();
            const string deletedRoot = "Assets/Recreated";
            const string parentPath = deletedRoot + "/Nested";
            var stale = new AssetRecord(
                "stale-guid",
                deletedRoot + "/Stale.asset",
                "Stale",
                "TestAsset");
            var child = new AssetRecord(
                "restart-child-guid",
                parentPath + "/Child.asset",
                "Child",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { stale },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(deletedRoot)),
                    new AssetFolderRecord(AssetPath.From(parentPath)),
                }), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { deletedRoot },
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { child.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var compacted = PersistentAssetIndexCache.CapturePendingChanges().Single();
            Assert.That(compacted.Deleted, Is.EqualTo(new[] { deletedRoot }));
            Assert.That(compacted.Imported, Is.EqualTo(new[] { child.AssetPath }));
            var source = new AuthoritativeSnapshotSource(
                new[] { child },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(deletedRoot)),
                    new AssetFolderRecord(AssetPath.From(parentPath)),
                });

            AssertRestartSnapshotIsReachable(
                source,
                child.AssetPath,
                new[] { deletedRoot, parentPath });
            Assert.That(source.EnumerationCount, Is.Zero,
                "a resolvable child replay should not enumerate the project");
        }

        [Test]
        public void PendingChangeJournal_AncestorDeleteThenFolderImport_PersistsAuthoritativeSubtree()
        {
            UseStableCaptureOverrides();
            const string deletedRoot = "Assets/Recreated";
            const string importedFolder = deletedRoot + "/Subtree";
            const string nestedFolder = importedFolder + "/Nested";
            var child = new AssetRecord(
                "subtree-child-guid",
                nestedFolder + "/Child.asset",
                "Child",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                new[] { new AssetFolderRecord(AssetPath.From(deletedRoot)) }), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { deletedRoot },
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { importedFolder },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var compacted = PersistentAssetIndexCache.CapturePendingChanges().Single();
            Assert.That(compacted.Deleted, Is.EqualTo(new[] { deletedRoot }));
            Assert.That(compacted.Imported, Is.EqualTo(new[] { importedFolder }));
            var source = new AuthoritativeSnapshotSource(
                new[] { child },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(deletedRoot)),
                    new AssetFolderRecord(AssetPath.From(importedFolder)),
                    new AssetFolderRecord(AssetPath.From(nestedFolder)),
                });

            AssertRestartSnapshotIsReachable(
                source,
                child.AssetPath,
                new[] { deletedRoot, importedFolder, nestedFolder });
            Assert.That(source.EnumerationCount, Is.GreaterThan(0),
                "without a direct-folder observer a folder import scans: one path cannot describe its subtree");
        }

        // The folder-then-script reload the user hits: a folder created live sits in
        // the journal when a script reload replays it. With the browser's own
        // direct-folder observer wired in, the replay reads the folder's subtree
        // from disk and patches the snapshot; nothing walks the project and nothing
        // announces a cold scan. Red before TryReplayFolderSubtree existed: the
        // import was refused outright and the overlay appeared.
        [Test]
        public void JournalFolderImport_WithADirectFolderObserver_ReplaysItsSubtreeWithoutAScan()
        {
            UseStableCaptureOverrides();
            const string root = "Assets/Root";
            const string imported = root + "/New";
            const string nested = imported + "/Nested";
            var child = new AssetRecord(
                "replayed-child-guid",
                imported + "/Child.asset",
                "Child",
                "TestAsset");
            var deep = new AssetRecord(
                "replayed-deep-guid",
                nested + "/Deep.asset",
                "Deep",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                new[] { new AssetFolderRecord(AssetPath.From(root)) }), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { imported },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var source = new AuthoritativeSnapshotSource(
                new[] { child, deep },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(root)),
                    new AssetFolderRecord(AssetPath.From(imported)),
                    new AssetFolderRecord(AssetPath.From(nested)),
                });
            var observer = new RecordedDirectFolderSource();
            observer.Add(imported, (child.AssetPath, false), (nested, true));
            observer.Add(nested, (deep.AssetPath, false));
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true,
                directFolderSource: observer);
            var completed = false;
            var counts = MeasurePerfCounters(() =>
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => Assert.Fail(
                        "a folder import the observer can read must not announce a cold scan"),
                    onComplete: () => completed = true);
                synchronizer.FlushPendingSnapshotSave();
            });
            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero,
                "the subtree came from the observer, not from a project enumeration");
            Assert.That(
                counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                Is.Zero);
            Assert.That(index.TryGetByPath(child.AssetPath, out _), Is.True);
            Assert.That(index.TryGetByPath(deep.AssetPath, out _), Is.True);
            Assert.That(index.TryGetFolder(imported, out _), Is.True);
            Assert.That(index.TryGetFolder(nested, out _), Is.True,
                "the observer's nested directory is a folder whether or not the source built it");
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                "the patched snapshot was saved and retired the journal");
        }

        // The replay is bounded like the journal it serves: a pasted-in tree past
        // the entry cap is not read on the main thread, it is scanned — and says so.
        [Test]
        public void JournalFolderImport_OverTheReplayBound_IsVerifiedWithoutAScan()
        {
            UseStableCaptureOverrides();
            const string imported = "Assets/Huge";
            var records = new List<AssetRecord>();
            var entries = new List<(string path, bool isFolder)>();
            for (var i = 0; i <= PersistentAssetIndexCache.MaxPendingChangeEntryCount; i++)
            {
                var path = imported + "/File" + i + ".asset";
                records.Add(new AssetRecord("huge-guid-" + i, path, "File" + i, "TestAsset"));
                entries.Add((path, false));
            }
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { imported },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var source = new AuthoritativeSnapshotSource(
                records,
                new[] { new AssetFolderRecord(AssetPath.From(imported)) });
            var observer = new RecordedDirectFolderSource();
            observer.Add(imported, entries.ToArray());
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true,
                directFolderSource: observer);
            var fullScanStarted = false;
            var completed = false;
            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);
            synchronizer.FlushPendingSnapshotSave();
            Assert.That(fullScanStarted, Is.False,
                "over the bound the import is unresolved, and the walk that settles it is silent");
            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.GreaterThan(0));
            Assert.That(index.Count, Is.EqualTo(records.Count),
                "the verification walk, not the replay, is what populated the catalog");
        }

        [Test]
        public void PendingChangeJournal_AncestorDeleteThenMovedTarget_PersistsReachableRestartSnapshot()
        {
            UseStableCaptureOverrides();
            const string oldPath = "Assets/Old/Child.asset";
            const string deletedRoot = "Assets/Recreated";
            const string parentPath = deletedRoot + "/Nested";
            var oldRecord = new AssetRecord(
                "moved-restart-guid",
                oldPath,
                "Child",
                "TestAsset");
            var moved = new AssetRecord(
                oldRecord.Guid,
                parentPath + "/Child.asset",
                "Child",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { oldRecord },
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { deletedRoot },
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { moved.AssetPath },
                new[] { oldPath }));
            var compacted = PersistentAssetIndexCache.CapturePendingChanges();
            Assert.That(
                compacted.SelectMany(batch => batch.Deleted),
                Is.EqualTo(new[] { deletedRoot }));
            Assert.That(
                compacted.SelectMany(batch => batch.MovedFromPaths),
                Is.EqualTo(new[] { oldPath }));
            Assert.That(
                compacted.SelectMany(batch => batch.Moved),
                Is.EqualTo(new[] { moved.AssetPath }));
            var source = new AuthoritativeSnapshotSource(
                new[] { moved },
                new[]
                {
                    new AssetFolderRecord(AssetPath.From(deletedRoot)),
                    new AssetFolderRecord(AssetPath.From(parentPath)),
                });

            AssertRestartSnapshotIsReachable(
                source,
                moved.AssetPath,
                new[] { deletedRoot, parentPath });
            Assert.That(source.EnumerationCount, Is.Zero);
        }

        [Test]
        public void TypeCandidateFiltering_IsPureAndKeepsOnlyAmbiguousProjectAssets()
        {
            AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();
            var candidates = Task.Run(() => AssetDatabaseAssetRecordSource.FilterAuthoritativeTypeCandidatePaths(
                new[]
                {
                    "Assets/Known.png",
                    "Assets/Custom.asset",
                    "Assets/Imported.zeptypefixture",
                    "Assets/Unknown.extension",
                    "Assets/Ignored.meta",
                    "Library/Outside.asset",
                },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".zeptypefixture",
                })).GetAwaiter().GetResult();

            Assert.That(candidates, Is.EqualTo(new[]
            {
                "Assets/Custom.asset",
                "Assets/Imported.zeptypefixture",
                "Assets/Unknown.extension",
            }));
        }

        [Test]
        public void InferredTypes_AuthoritativeMetadataWins()
        {
            const string path = "Assets/Fixture.png";
            const string guid = "fixture-guid";
            var authoritative = new AssetTypeMetadata("AuthoritativeType", new[] { "AuthoritativeBase" });
            var types = new Dictionary<string, AssetTypeMetadata> { { guid, authoritative } };
            var result = AssetDatabaseAssetRecordSource.ResolveTypeMetadata(path, guid, types);
            Assert.That(result.TypeName, Is.EqualTo("AuthoritativeType"));
            Assert.That(result.BaseTypeNames, Is.EqualTo(new[] { "AuthoritativeBase" }));

            var field = typeof(AssetDatabaseAssetRecordSource).GetField(
                "_inferredTypesByName", BindingFlags.Static | BindingFlags.NonPublic);
            var original = field.GetValue(null);
            try
            {
                field.SetValue(null, null);
                Assert.That(AssetDatabaseAssetRecordSource.RequiresAuthoritativeType(path, new HashSet<string>()), Is.True);
                field.SetValue(null, new Dictionary<string, AssetTypeMetadata>());
                Assert.That(AssetDatabaseAssetRecordSource.RequiresAuthoritativeType(path, new HashSet<string>()), Is.True);
            }
            finally
            {
                field.SetValue(null, original);
            }

            AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();
            Assert.That(AssetDatabaseAssetRecordSource.RequiresAuthoritativeType("Assets/Fixture.asset", new HashSet<string>()), Is.True);
            Assert.That(AssetDatabaseAssetRecordSource.RequiresAuthoritativeType("Assets/Fixture.unknown", new HashSet<string>()), Is.True);
            Assert.That(AssetDatabaseAssetRecordSource.RequiresAuthoritativeType(path, new HashSet<string> { ".png" }), Is.True);
        }

        [Test]
        public void InferredTypes_IncrementalSearchMatchesAssetDatabase()
        {
            var prefix = "Assets/__ZepInferred_" + Guid.NewGuid().ToString("N");
            var guiPath = prefix + ".guiskin";
            var pngPath = prefix + ".png";
            GUISkin skin = null;
            var wasEnabled = ZepPerfLog.IsEnabled;
            try
            {
                skin = ScriptableObject.CreateInstance<GUISkin>();
                AssetDatabase.CreateAsset(skin, guiPath);
                File.WriteAllBytes(pngPath, OnePixelPng);
                AssetDatabase.ImportAsset(guiPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
                var guiGuid = AssetDatabase.AssetPathToGUID(guiPath);
                var pngGuid = AssetDatabase.AssetPathToGUID(pngPath);
                Assert.That(guiGuid, Is.Not.Empty, "fixture guard: the GUISkin must have imported");
                Assert.That(pngGuid, Is.Not.Empty, "fixture guard: the PNG must have imported");
                Assert.That(AssetDatabase.GetMainAssetTypeAtPath(guiPath), Is.EqualTo(typeof(GUISkin)));
                Assert.That(AssetDatabase.GetMainAssetTypeAtPath(pngPath), Is.EqualTo(typeof(Texture2D)));
                var database = new AssetDatabaseAssetRecordSource();
                var expectedGui = database.BuildRecord(AssetPath.From(guiPath));
                var expectedPng = database.BuildRecord(AssetPath.From(pngPath));
                AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                foreach (var source in new IAssetRecordSource[]
                {
                    new NativeFileSystemAssetRecordSource(),
                    new HybridAssetRecordSource(),
                })
                {
                    var records = new[]
                    {
                        source.BuildRecord(AssetPath.From(guiPath)),
                        source.BuildRecord(AssetPath.From(pngPath)),
                    };
                    Assert.That(records[0], Is.Not.Null);
                    Assert.That(records[0].Guid, Is.EqualTo(guiGuid));
                    Assert.That(records[0].AssetPath, Is.EqualTo(guiPath));
                    Assert.That(records[0].TypeName, Is.EqualTo(expectedGui.TypeName));
                    Assert.That(records[0].BaseTypeNames, Is.EqualTo(expectedGui.BaseTypeNames));
                    Assert.That(records[1], Is.Not.Null);
                    Assert.That(records[1].Guid, Is.EqualTo(pngGuid));
                    Assert.That(records[1].AssetPath, Is.EqualTo(pngPath));
                    Assert.That(records[1].TypeName, Is.EqualTo(expectedPng.TypeName));
                    Assert.That(records[1].BaseTypeNames, Is.EqualTo(expectedPng.BaseTypeNames));

                    var index = new InMemoryAssetIndex();
                    index.Replace(records, Array.Empty<AssetFolderRecord>());
                    var search = new AssetSearchService(index);
                    Assert.That(
                        search.Search("t:ScriptableObject").Select(record => record.Guid),
                        Is.EqualTo(new[] { guiGuid }));
                    Assert.That(
                        search.Search("t:Texture").Select(record => record.Guid),
                        Is.EqualTo(new[] { pngGuid }));
                }

                Assert.That(
                    ZepPerfLog.CaptureSnapshot()
                        .GetCount(ZepPerfCounter.AssetDatabaseTypeQueries),
                    Is.Zero);
            }
            finally
            {
                try
                {
                    ZepPerfLog.IsEnabled = wasEnabled;
                }
                finally
                {
                    try
                    {
                        AssetDatabase.DeleteAsset(guiPath);
                    }
                    finally
                    {
                        try
                        {
                            AssetDatabase.DeleteAsset(pngPath);
                        }
                        finally
                        {
                            if (skin != null)
                            {
                                UnityEngine.Object.DestroyImmediate(skin);
                            }
                        }
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator InferredTypes_ColdSourcesPreserveSearch()
        {
            var prefix = "Assets/__ZepColdInferred_" + Guid.NewGuid().ToString("N");
            var guiPath = prefix + ".guiskin";
            var pngPath = prefix + ".png";
            GUISkin skin = null;
            Task<List<AssetRecord>> preparedWorker = null;
            try
            {
                skin = ScriptableObject.CreateInstance<GUISkin>();
                AssetDatabase.CreateAsset(skin, guiPath);
                File.WriteAllBytes(pngPath, OnePixelPng);
                AssetDatabase.ImportAsset(guiPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);

                var guiGuid = AssetDatabase.AssetPathToGUID(guiPath);
                var pngGuid = AssetDatabase.AssetPathToGUID(pngPath);
                Assert.That(guiGuid, Is.Not.Empty, "fixture guard: the GUISkin must have imported");
                Assert.That(pngGuid, Is.Not.Empty, "fixture guard: the PNG must have imported");
                Assert.That(AssetDatabase.GetMainAssetTypeAtPath(guiPath), Is.EqualTo(typeof(GUISkin)));
                Assert.That(AssetDatabase.GetMainAssetTypeAtPath(pngPath), Is.EqualTo(typeof(Texture2D)));

                var databaseRecords = new AssetDatabaseAssetRecordSource().GetRecords().ToList();
                var expectedGui = databaseRecords.Single(record => record.AssetPath == guiPath);
                var expectedPng = databaseRecords.Single(record => record.AssetPath == pngPath);
                Assert.That(expectedGui.Guid, Is.EqualTo(guiGuid));
                Assert.That(expectedPng.Guid, Is.EqualTo(pngGuid));

                Action<IReadOnlyList<AssetRecord>> assertFixtureRecords = records =>
                {
                    var actualGui = records.Single(record => record.AssetPath == guiPath);
                    var actualPng = records.Single(record => record.AssetPath == pngPath);
                    Assert.That(actualGui.Guid, Is.EqualTo(expectedGui.Guid));
                    Assert.That(actualGui.AssetPath, Is.EqualTo(expectedGui.AssetPath));
                    Assert.That(actualGui.TypeName, Is.EqualTo(expectedGui.TypeName));
                    Assert.That(actualGui.BaseTypeNames, Is.EqualTo(expectedGui.BaseTypeNames));
                    Assert.That(actualPng.Guid, Is.EqualTo(expectedPng.Guid));
                    Assert.That(actualPng.AssetPath, Is.EqualTo(expectedPng.AssetPath));
                    Assert.That(actualPng.TypeName, Is.EqualTo(expectedPng.TypeName));
                    Assert.That(actualPng.BaseTypeNames, Is.EqualTo(expectedPng.BaseTypeNames));

                    var index = new InMemoryAssetIndex();
                    index.Replace(new[] { actualGui, actualPng }, Array.Empty<AssetFolderRecord>());
                    var search = new AssetSearchService(index);
                    Assert.That(
                        search.Search("t:ScriptableObject").Select(record => record.Guid),
                        Is.EqualTo(new[] { guiGuid }));
                    Assert.That(
                        search.Search("t:Texture").Select(record => record.Guid),
                        Is.EqualTo(new[] { pngGuid }));
                };

                var hybridRecords = new HybridAssetRecordSource().GetRecords().ToList();
                assertFixtureRecords(hybridRecords);

                AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();
                var preparedTypes = new Dictionary<string, AssetTypeMetadata>(
                    AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot());
                preparedTypes.Remove(guiGuid);
                Assert.That(preparedTypes.ContainsKey(guiGuid), Is.False);
                preparedTypes.Remove(pngGuid);
                Assert.That(preparedTypes.ContainsKey(pngGuid), Is.False);
                var preparedNative = new NativeFileSystemAssetRecordSource();
                preparedNative.PrepareForEnumeration(typesByGuid: preparedTypes);
                preparedWorker = Task.Run(() => preparedNative.GetRecords().ToList());
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (!preparedWorker.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    yield return null;
                }

                var timedOut = !preparedWorker.IsCompleted;
                while (!preparedWorker.IsCompleted)
                {
                    yield return null;
                }

                List<AssetRecord> preparedRecords = null;
                Exception workerFailure = null;
                try
                {
                    preparedRecords = preparedWorker.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    workerFailure = ex;
                }

                if (timedOut)
                {
                    Assert.Fail(
                        $"prepared native enumeration timed out after {stopwatch.Elapsed}; "
                        + $"task status: {preparedWorker.Status}; worker fault: {workerFailure}");
                }

                if (workerFailure != null)
                {
                    throw workerFailure;
                }

                assertFixtureRecords(preparedRecords);

                var unpreparedNativeRecords = new NativeFileSystemAssetRecordSource().GetRecords().ToList();
                assertFixtureRecords(unpreparedNativeRecords);
                var unpreparedHybridRecords = new HybridAssetRecordSource().GetRecords().ToList();
                assertFixtureRecords(unpreparedHybridRecords);
            }
            finally
            {
                try
                {
                    AssetDatabase.DeleteAsset(guiPath);
                }
                finally
                {
                    try
                    {
                        AssetDatabase.DeleteAsset(pngPath);
                    }
                    finally
                    {
                        if (skin != null)
                        {
                            UnityEngine.Object.DestroyImmediate(skin);
                        }
                    }
                }
            }
        }

        [Test]
        public void InferredTypes_LegacyCacheRejected()
        {
            UseStableCaptureOverrides();
            var record = new AssetRecord(
                "legacy-inferred-guid",
                "Assets/LegacyInferred.guiskin",
                "LegacyInferred",
                nameof(GUISkin));
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { record },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            var artifactRevision = PersistentAssetIndexCache.CaptureArtifactDependencyVersion();
            Assert.That(PersistentAssetIndexCache.TryLoad(
                artifactRevision,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out _,
                out _,
                out _), Is.True);
            Assert.That(PersistentAssetIndexCache.TryLoad(
                artifactRevision,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out _,
                out _,
                out _), Is.True);

            var legacyBytes = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            legacyBytes[4] = 8;
            File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, legacyBytes);

            Assert.That(PersistentAssetIndexCache.TryLoad(
                artifactRevision,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out _,
                out _,
                out _), Is.False);
            Assert.That(PersistentAssetIndexCache.TryLoad(
                artifactRevision,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out _,
                out _,
                out _), Is.False);
            Assert.That(
                File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath),
                Is.EqualTo(legacyBytes),
                "an ordinary version mismatch must not discard the reconstructible cache");
        }

        [Test]
        public void InferredTypes_CacheRebuildAndReloadPreserveSearch()
        {
            UseStableCaptureOverrides();
            var assetPath = "Assets/__ZepLegacyGuiSkin_"
                + Guid.NewGuid().ToString("N")
                + ".guiskin";
            GUISkin skin = null;
            AssetIndexSynchronizer synchronizer = null;
            var authoritativeTypeSnapshotOverride =
                PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride;
            try
            {
                skin = ScriptableObject.CreateInstance<GUISkin>();
                AssetDatabase.CreateAsset(skin, assetPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var expectedMetadata = AssetDatabaseAssetRecordSource.BuildTypeMetadata(typeof(GUISkin));
                Assert.That(guid, Is.Not.Empty);

                Assert.That(PersistentAssetIndexCache.Save(
                    new[]
                    {
                        new AssetRecord(
                            guid,
                            assetPath,
                            Path.GetFileNameWithoutExtension(assetPath),
                            nameof(GUISkin)),
                    },
                    Array.Empty<AssetFolderRecord>()), Is.True);
                var artifactRevision = PersistentAssetIndexCache.CaptureArtifactDependencyVersion();
                Assert.That(PersistentAssetIndexCache.TryLoad(
                artifactRevision,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out _,
                out _,
                out _), Is.True);
                var legacyBytes = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
                legacyBytes[4] = 8;
                File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, legacyBytes);

                AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();
                var authoritativeTypes = AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot();
                Assert.That(authoritativeTypes.ContainsKey(guid), Is.False);
                PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = () => authoritativeTypes;

                var source = new HybridAssetRecordSource();
                var index = new InMemoryAssetIndex();
                synchronizer = new AssetIndexSynchronizer(
                    source,
                    source,
                    index,
                    enablePersistentCache: true);
                var fullScanStarted = false;
                var completionCount = 0;

                synchronizer.SynchronizeAsync(
                    () => fullScanStarted = true,
                    () => completionCount++);

                Assert.That(fullScanStarted, Is.True);
                Assert.That(completionCount, Is.EqualTo(1));
                synchronizer.FlushPendingSnapshotSave();
                Assert.That(index.TryGetByPath(assetPath, out var rebuilt), Is.True);
                Assert.That(rebuilt.Guid, Is.EqualTo(guid));
                Assert.That(rebuilt.AssetPath, Is.EqualTo(assetPath));
                Assert.That(rebuilt.TypeName, Is.EqualTo(expectedMetadata.TypeName));
                Assert.That(rebuilt.BaseTypeNames, Is.EqualTo(expectedMetadata.BaseTypeNames));
                Assert.That(rebuilt.BaseTypeNames, Does.Contain(nameof(ScriptableObject)));
                Assert.That(
                    new AssetSearchService(index).Search("t:ScriptableObject")
                        .Select(record => record.Guid),
                    Does.Contain(guid));

                var savedBytes = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
                Assert.That(savedBytes[4], Is.EqualTo(12));
                Assert.That(PersistentAssetIndexCache.TryLoad(
                    out var savedRecords,
                    out var savedFolders), Is.True);
                var reloadedIndex = new InMemoryAssetIndex();
                reloadedIndex.Replace(savedRecords, savedFolders);
                Assert.That(reloadedIndex.TryGetByPath(assetPath, out var reloaded), Is.True);
                Assert.That(reloaded.Guid, Is.EqualTo(guid));
                Assert.That(reloaded.AssetPath, Is.EqualTo(assetPath));
                Assert.That(reloaded.TypeName, Is.EqualTo(expectedMetadata.TypeName));
                Assert.That(reloaded.BaseTypeNames, Is.EqualTo(expectedMetadata.BaseTypeNames));
                Assert.That(
                    new AssetSearchService(reloadedIndex).Search("t:ScriptableObject")
                        .Select(record => record.Guid),
                    Does.Contain(guid));
            }
            finally
            {
                try
                {
                    PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride =
                        authoritativeTypeSnapshotOverride;
                }
                finally
                {
                    try
                    {
                        synchronizer?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            AssetDatabase.DeleteAsset(assetPath);
                        }
                        finally
                        {
                            if (skin != null)
                            {
                                UnityEngine.Object.DestroyImmediate(skin);
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void SynchronizeAsync_CacheHit_CompletesWithoutStartingFullScan()
        {
            UseStableCaptureOverrides();
            var cachedRecord = new AssetRecord(
                "cached-guid",
                "Assets/Cached.asset",
                "Cached",
                "TestAsset",
                "Panel Tag",
                new[] { "Gameplay", "Needs Review" },
                new[] { "ScriptableObject" });
            var saved = PersistentAssetIndexCache.Save(
                new[] { cachedRecord },
                new[]
                {
                    new AssetFolderRecord(
                        AssetPath.From("Assets/CachedFolder"),
                        "cached-folder-guid",
                        new[] { "Folder Label" }),
                });
            Assert.That(saved, Is.True, "test cache should be written with a stable signature");
            var labelCaptureCount = 0;
            PersistentAssetIndexCache.UnityLabelSnapshotOverride = () =>
            {
                labelCaptureCount++;
                return new Dictionary<string, IReadOnlyList<string>>();
            };
            PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = () =>
                throw new AssertionException("a cache hit must not resolve per-asset concrete types");

            var index = new InMemoryAssetIndex();
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            Assert.That(fullScanStarted, Is.False,
                "restoring a valid cache must not activate the cold-scan UI state");
            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(cachedRecord.AssetPath, out var restored), Is.True);
            Assert.That(restored.Guid, Is.EqualTo(cachedRecord.Guid));
            Assert.That(restored.UnityLabels, Is.EqualTo(cachedRecord.UnityLabels));
            Assert.That(restored.BaseTypeNames, Is.EqualTo(cachedRecord.BaseTypeNames));
            var restoredFolders = synchronizer.GetFolderRecordsForProjection();
            Assert.That(restoredFolders, Has.Count.EqualTo(1));
            Assert.That(restoredFolders[0].FolderPath.Value, Is.EqualTo("Assets/CachedFolder"));
            Assert.That(restoredFolders[0].Guid, Is.EqualTo("cached-folder-guid"));
            Assert.That(restoredFolders[0].UnityLabels, Is.EqualTo(new[] { "Folder Label" }));
            Assert.That(labelCaptureCount, Is.EqualTo(1),
                "cache validation and hydration must share one label snapshot");
        }

        // The compile-only reload end to end. This fixture is the rewrite of the
        // pre-rewrite test of the same name, which asserted the OPPOSITE outcome: that an
        // artifact-only change escalated to a silent full enumeration ("enumerate" in the
        // recorded events). T5.3 deleted that escalation, so the assertion that pinned it
        // is rewritten into the behaviour that replaced it rather than dropped - the
        // operation completes against the hydrated snapshot, and the types follow as a
        // budgeted patch job that publishes each slice it actually changed.
        [Test]
        public void SynchronizeAsync_ArtifactOnlyChange_CompletesAtHydrationThenPatchesTypes()
        {
            UseStableCaptureOverrides();
            var stale = new AssetRecord(
                "artifact-only-guid",
                "Assets/ArtifactOnly.asset",
                "ArtifactOnly",
                "StaleType");
            var current = new AssetRecord(
                "artifact-only-guid",
                "Assets/ArtifactOnly.asset",
                "ArtifactOnly",
                "CurrentType");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { stale },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new TargetedRecordSource(new[] { current });
            var index = new InMemoryAssetIndex();
            var patches = new List<AssetCatalogDelta>();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true,
                onCatalogPatched: delta => patches.Add(delta));
            var fullScanStarted = false;
            var completed = false;
            // Read INSIDE the completion callback, never asserted there: a waiter that
            // throws is swallowed and logged by CompleteOperation. This is the whole
            // ordering proof - under the old ordering the job had already run by the
            // time waiters fired, so this would read "CurrentType".
            string typeWhenWaitersFired = null;

            var counts = MeasurePerfCounters(() =>
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: () =>
                    {
                        completed = true;
                        typeWhenWaitersFired =
                            index.TryGetByPath(stale.AssetPath, out var atCompletion)
                                ? atCompletion.TypeName
                                : null;
                    });
                synchronizer.FlushPendingSnapshotSave();
            });

            Assert.That(fullScanStarted, Is.False,
                "a script-only artifact revision must reuse the saved snapshot without "
                + "presenting a cold-build overlay");
            Assert.That(completed, Is.True);
            Assert.That(typeWhenWaitersFired, Is.EqualTo("StaleType"),
                "the operation completes at hydration - before a single candidate is rebuilt");
            Assert.That(index.TryGetByPath(stale.AssetPath, out var patched), Is.True);
            Assert.That(patched.TypeName, Is.EqualTo("CurrentType"),
                "and the follow-up job patches the type afterwards");
            Assert.That(patches, Has.Count.EqualTo(1),
                "one slice changed the catalog, so exactly one delta is published");
            Assert.That(
                patches[0].UpsertedRecords.Select(record => record.AssetPath),
                Is.EqualTo(new[] { stale.AssetPath }),
                "and the delta carries the record the slice rebuilt");
            Assert.That(source.EnumerationCount, Is.Zero,
                "a retype is not a scan");
            Assert.That(
                counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                Is.Zero,
                "AC3: a compile-only reload walks no folder tree");
            Assert.That(
                counts.GetCount(ZepPerfCounter.FullRecordEnumerations),
                Is.Zero,
                "AC3: and enumerates no records in the synchronizer");
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out var saved,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.False,
                "the job's save promotes the new artifact revision");
            Assert.That(
                saved.Single(record => string.Equals(
                    record.AssetPath,
                    stale.AssetPath,
                    StringComparison.Ordinal)).TypeName,
                Is.EqualTo("CurrentType"),
                "and commits the patched types");
        }

        [Test]
        public void SynchronizeAsync_KnownDeletionWithChangedStructure_PatchesCacheWithoutColdScan()
        {
            UseStableCaptureOverrides();
            var cachedRecord = new AssetRecord(
                "deleted-guid",
                "Assets/Deleted.cs",
                "Deleted",
                "MonoScript");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { cachedRecord },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { cachedRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>()));

            var index = new InMemoryAssetIndex();
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            Assert.That(fullScanStarted, Is.False,
                "a journaled deletion must patch the saved catalog instead of presenting a cold rebuild");

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(cachedRecord.AssetPath, out _), Is.False);
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                "the journal is cleared only after the patched snapshot is saved");
        }

        [Test]
        public void SynchronizeAsync_KnownScriptImportWithChangedStructure_ReconcilesWithoutColdScan()
        {
            UseStableCaptureOverrides();
            var oldRecord = new AssetRecord(
                "script-guid",
                "Assets/Changed.cs",
                "Changed",
                "OldScriptType");
            var currentRecord = new AssetRecord(
                "script-guid",
                "Assets/Changed.cs",
                "Changed",
                "MonoScript");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { oldRecord },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { currentRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var events = new List<string>();
            var source = new KnownChangeSource(currentRecord, events);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            Assert.That(fullScanStarted, Is.False,
                "a journaled script import must seed artifact reconciliation without a cold rebuild");

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(events, Does.Contain("build-record"));
            Assert.That(events, Does.Not.Contain("enumerate"),
                "a concrete script import must not enumerate the project");
            Assert.That(index.TryGetByPath(currentRecord.AssetPath, out var restored), Is.True);
            Assert.That(restored.TypeName, Is.EqualTo(currentRecord.TypeName));
        }

        [Test]
        public void SynchronizeAsync_KnownScriptDeletion_DoesNotEnumerateProject()
        {
            UseStableCaptureOverrides();
            var deletedScript = new AssetRecord(
                "deleted-script-guid",
                "Assets/Deleted.cs",
                "Deleted",
                "MonoScript");
            var texture = new AssetRecord(
                "texture-guid",
                "Assets/Texture.png",
                "Texture",
                "Texture2D");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { deletedScript, texture },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { deletedScript.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new ThrowIfEnumeratedSource();
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => Assert.Fail("A journaled script deletion is not a cold scan."),
                onComplete: () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(deletedScript.AssetPath, out _), Is.False);
            Assert.That(index.TryGetByPath(texture.AssetPath, out _), Is.True);
        }

        [Test]
        public void SynchronizeAsync_ScriptChange_RefreshesCustomTypeMetadataWithoutProjectEnumeration()
        {
            UseStableCaptureOverrides();
            var oldScript = new AssetRecord(
                "script-guid",
                "Assets/Changed.cs",
                "Changed",
                "MonoScript");
            var currentScript = new AssetRecord(
                "script-guid",
                "Assets/Changed.cs",
                "Changed",
                "MonoScript");
            var oldCustomAsset = new AssetRecord(
                "custom-guid",
                "Assets/Custom.png",
                "Custom",
                "CustomSettings",
                baseTypeNames: new[] { "ScriptableObject" });
            var currentCustomAsset = new AssetRecord(
                "custom-guid",
                "Assets/Custom.png",
                "Custom",
                "Texture2D");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { oldScript, oldCustomAsset },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { currentScript.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new TargetedRecordSource(
                new[] { currentScript, currentCustomAsset });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => Assert.Fail("Targeted metadata refresh is not a cold scan."),
                onComplete: () => completed = true);

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(index.TryGetByPath(currentCustomAsset.AssetPath, out var refreshed), Is.True);
            Assert.That(refreshed.TypeName, Is.EqualTo(currentCustomAsset.TypeName));
            Assert.That(refreshed.BaseTypeNames, Is.EqualTo(currentCustomAsset.BaseTypeNames));
        }

        // Recorded as plan §9 item 15: this fixture pinned the contract T5.4 deleted -
        // a throwing rebuild slice escalated to a full authoritative enumeration - so it
        // is rewritten rather than removed, and it keeps the half of its old contract
        // that survived: the throw stays inside the synchronizer, the operation still
        // succeeds, and the record the source could not rebuild keeps the type the
        // snapshot recorded instead of vanishing or being guessed. The job-internal half
        // (no scan, no save, old revision on disk, journal kept, flag cleared) belongs to
        // TypeReconciliation_SliceThrows_EndsTheJobWithoutAScanOrSave and is not
        // duplicated here.
        [Test]
        public void SynchronizeAsync_ThrowingTargetedMetadataRefresh_StillCompletesAndKeepsTheRecordedType()
        {
            UseStableCaptureOverrides();
            var changedScript = new AssetRecord(
                "script-guid",
                "Assets/Changed.cs",
                "Changed",
                "MonoScript");
            var staleCustomAsset = new AssetRecord(
                "custom-guid",
                "Assets/Custom.asset",
                "Custom",
                "OldCustomType",
                baseTypeNames: new[] { "ScriptableObject" });
            var currentCustomAsset = new AssetRecord(
                "custom-guid",
                "Assets/Custom.asset",
                "Custom",
                "CurrentCustomType",
                baseTypeNames: new[] { "ScriptableObject" });
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { changedScript, staleCustomAsset },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { changedScript.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new ThrowingTargetedRecordSource(
                new[] { changedScript, currentCustomAsset });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;
            // A flag, never Assert.Fail: BeginFullScan invokes this callback inside a
            // try/catch, so an assertion thrown here would be swallowed and logged.
            var fullScanStarted = false;
            LogAssert.Expect(
                LogType.Warning,
                new Regex(
                    "Script type reconciliation failed .* keeps its recorded types"));

            Assert.DoesNotThrow(() => synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true));

            Assert.That(completed, Is.True,
                "the operation completed at hydration, so a faulting follow-up job "
                + "cannot un-complete it");
            Assert.That(fullScanStarted, Is.False,
                "a hydrated reload must not show the cold overlay");
            Assert.That(index.TryGetByPath(currentCustomAsset.AssetPath, out var kept), Is.True,
                "the record the source could not rebuild is still in the catalog");
            Assert.That(kept.TypeName, Is.EqualTo(staleCustomAsset.TypeName),
                "and keeps the type the snapshot recorded - the job patches types, it "
                + "never discards knowledge and never guesses");
            Assert.That(index.TryGetByPath(changedScript.AssetPath, out _), Is.True,
                "and the rest of the hydrated catalog is untouched");
        }

        // T3.7 / audit §3.3(b) + 4.13. ApplyScanOverlay used to decide every record's
        // survival by scanning the whole removed-root list and the whole
        // expected-current-root list; it now probes a set and walks the candidate
        // path's own ancestor chain. That is a pure shape change, so the proof is
        // equivalence at a size the old shape could not survive: 1,000 roots over
        // 20,000 records is 20 million IsAtOrBelow calls, which is the finding. The
        // reference below is the ORIGINAL semantics written out by hand — a path is
        // removed when some root is at or above it, unless some eligible
        // expected-current root is at or below it, or (folders, authoritative
        // snapshots only) some eligible expected-current FOLDER is at or above it.
        [TestCase(true)]
        [TestCase(false)]
        public void ApplyScanOverlay_AtScale_KeepsExactlyTheRecordsTheLinearRuleKept(
            bool authoritativeSnapshot)
        {
            var removedRoots = new List<string>();
            for (var i = 0; i < 480; i++)
            {
                removedRoots.Add("Assets/Area" + (i % 40) + "/Group" + (i % 15) + "/Item" + i);
            }

            // Nested and case-differing roots, plus a prefix that is NOT an ancestor
            // ("Area1Extra" under "Area1"), so the walk cannot pass by string prefix.
            for (var i = 0; i < 260; i++)
            {
                removedRoots.Add("Assets/Area" + (i % 40) + "/Group" + (i % 15));
                removedRoots.Add("ASSETS/Area" + (i % 40) + "/GROUP" + (i % 15) + "/Item" + i);
            }

            removedRoots.Add("Assets/Area1Extra");
            // One shallow root, but deliberately not "Assets": a root that covers the
            // whole fixture would leave nothing to compare in the non-authoritative case.
            removedRoots.Add("Assets/Area7");

            var records = new List<AssetRecord>();
            var folders = new List<AssetFolderRecord>();
            for (var i = 0; i < 20000; i++)
            {
                var area = i % 60;
                var group = i % 25;
                var path = "Assets/Area" + area + "/Group" + group + "/File" + i + ".png";
                records.Add(new AssetRecord(
                    i.ToString("x32"),
                    path,
                    "File" + i,
                    "Texture2D"));
                if (i < 1500)
                {
                    folders.Add(new AssetFolderRecord(
                        AssetPath.From("Assets/Area" + area + "/Group" + i)));
                }
            }

            // Expected-current roots: some resolve to a record, some to a folder, some
            // to neither (those must stay ineligible and protect nothing).
            var expectedCurrentRoots = new List<string>();
            for (var i = 0; i < 150; i++)
            {
                expectedCurrentRoots.Add(records[i * 37].AssetPath);
                expectedCurrentRoots.Add(folders[i * 7].FolderPath.Value);
                expectedCurrentRoots.Add("Assets/Nowhere" + i + "/Ghost" + i);
            }

            // Record upsert paths are disjoint from every input record path, so the
            // comparison below can look at surviving INPUTS without modelling the
            // upsert merge. The folder upserts deliberately name folders that ARE in
            // the input, which is the real shape (a folder re-observed during a scan)
            // and which the reference models the same way.
            var recordUpserts = new List<AssetRecord>();
            for (var i = 0; i < 20; i++)
            {
                var path = "Assets/Upserted/Record" + i + ".asset";
                recordUpserts.Add(new AssetRecord(
                    (900000 + i).ToString("x32"),
                    path,
                    "Record" + i,
                    "TextAsset"));
                expectedCurrentRoots.Add(path);
            }

            var folderUpserts = new List<AssetFolderRecord>();
            for (var i = 0; i < 10; i++)
            {
                var path = "Assets/Area" + i + "/Group" + i;
                folderUpserts.Add(new AssetFolderRecord(AssetPath.From(path)));
                expectedCurrentRoots.Add(path);
            }

            var inputRecordPaths = records
                .Select(record => record.AssetPath)
                .ToArray();
            var inputFolderPaths = folders
                .Select(folder => folder.FolderPath.Value)
                .ToArray();

            var expectedSurvivingRecords = BruteForceSurvivors(
                inputRecordPaths,
                inputRecordPaths,
                inputFolderPaths,
                removedRoots,
                expectedCurrentRoots,
                recordUpserts.Select(record => record.AssetPath).ToArray(),
                folderUpserts.Select(folder => folder.FolderPath.Value).ToArray(),
                authoritativeSnapshot);
            var expectedSurvivingFolders = BruteForceSurvivors(
                inputFolderPaths,
                inputRecordPaths,
                inputFolderPaths,
                removedRoots,
                expectedCurrentRoots,
                recordUpserts.Select(record => record.AssetPath).ToArray(),
                folderUpserts.Select(folder => folder.FolderPath.Value).ToArray(),
                authoritativeSnapshot);

            AssetIndexSynchronizer.ApplyScanOverlayForTests(
                records,
                folders,
                removedRoots,
                expectedCurrentRoots,
                recordUpserts,
                folderUpserts,
                authoritativeSnapshot);

            var inputRecordPathSet = new HashSet<string>(
                inputRecordPaths,
                StringComparer.OrdinalIgnoreCase);
            var inputFolderPathSet = new HashSet<string>(
                inputFolderPaths,
                StringComparer.OrdinalIgnoreCase);
            var actualSurvivingRecords = records
                .Select(record => record.AssetPath)
                .Where(path => inputRecordPathSet.Contains(path))
                .ToArray();
            var actualSurvivingFolders = folders
                .Select(folder => folder.FolderPath.Value)
                .Where(path => inputFolderPathSet.Contains(path))
                .ToArray();

            // A fixture that removed everything, or nothing, would make the comparison
            // vacuous. Pin that it did neither.
            Assert.That(expectedSurvivingRecords.Length, Is.GreaterThan(0));
            Assert.That(expectedSurvivingRecords.Length, Is.LessThan(inputRecordPaths.Length));
            Assert.That(expectedSurvivingFolders.Length, Is.GreaterThan(0));
            Assert.That(expectedSurvivingFolders.Length, Is.LessThan(inputFolderPaths.Length));

            Assert.That(actualSurvivingRecords, Is.EqualTo(expectedSurvivingRecords));
            Assert.That(actualSurvivingFolders, Is.EqualTo(expectedSurvivingFolders));
        }

        // The pre-2026-09-02 rule, written out linearly. Deliberately not a call into
        // the production helpers: a reference that shares the code under test proves
        // nothing.
        private static string[] BruteForceSurvivors(
            IReadOnlyList<string> candidatePaths,
            IReadOnlyList<string> snapshotRecordPaths,
            IReadOnlyList<string> snapshotFolderPaths,
            IReadOnlyList<string> removedRoots,
            IReadOnlyList<string> expectedCurrentRoots,
            IReadOnlyList<string> recordUpsertPaths,
            IReadOnlyList<string> folderUpsertPaths,
            bool authoritativeSnapshot)
        {
            var expectedCurrentRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (authoritativeSnapshot)
            {
                foreach (var path in snapshotRecordPaths)
                {
                    expectedCurrentRecords.Add(path);
                }
            }

            foreach (var path in recordUpsertPaths)
            {
                expectedCurrentRecords.Add(path);
            }

            var expectedCurrentFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (authoritativeSnapshot)
            {
                foreach (var path in snapshotFolderPaths)
                {
                    expectedCurrentFolders.Add(path);
                }
            }

            foreach (var path in folderUpsertPaths)
            {
                expectedCurrentFolders.Add(path);
            }

            bool IsAtOrBelow(string path, string root)
            {
                return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    || (path.Length > root.Length
                        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && path[root.Length] == '/');
            }

            var survivors = new List<string>();
            foreach (var path in candidatePaths)
            {
                var removed = false;
                for (var i = 0; i < removedRoots.Count && !removed; i++)
                {
                    removed = IsAtOrBelow(path, removedRoots[i]);
                }

                if (!removed)
                {
                    survivors.Add(path);
                    continue;
                }

                var protectedByExpectedCurrent = false;
                for (var i = 0; i < expectedCurrentRoots.Count && !protectedByExpectedCurrent; i++)
                {
                    var expected = expectedCurrentRoots[i];
                    var isRecord = expectedCurrentRecords.Contains(expected);
                    var isFolder = !isRecord && expectedCurrentFolders.Contains(expected);
                    protectedByExpectedCurrent = (isRecord || isFolder)
                        && (IsAtOrBelow(expected, path)
                            || (isFolder
                                && authoritativeSnapshot
                                && IsAtOrBelow(path, expected)));
                }

                if (protectedByExpectedCurrent)
                {
                    survivors.Add(path);
                }
            }

            return survivors.ToArray();
        }

        [Test]
        public void ScanOverlay_RepeatedChangesToOneAsset_CoalesceInsteadOfGrowing()
        {
            UseStableCaptureOverrides();
            var churned = new AssetRecord(
                "churn-guid",
                "Assets/Churn.asset",
                "Churn",
                "TestAsset");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { churned },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            var index = new InMemoryAssetIndex();
            var source = new TargetedRecordSource(Array.Empty<AssetRecord>());
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var recorder = (IAssetScanChangeRecorder)synchronizer;

            for (var i = 0; i < 500; i++)
            {
                recorder.RecordConcreteChanges(new ProjectAssetChanges(
                    new[] { churned.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                recorder.RecordConcreteChanges(new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { churned.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            }

            // A delete under an already-recorded delete adds nothing either: the
            // removal closure of the folder root already covers every member.
            recorder.RecordConcreteChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { "Assets/Churn.asset/Nested.asset" },
                Array.Empty<string>(),
                Array.Empty<string>()));

            Assert.That(
                synchronizer.ScanOverlayEntryCountForTests,
                Is.EqualTo(1),
                "1001 callbacks about one path must coalesce to its final state");

            var completed = false;
            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => Assert.Fail(
                    "a valid cache must not present a cold scan"),
                onComplete: () => completed = true);

            Assert.That(completed, Is.True);
            Assert.That(
                index.TryGetByPath(churned.AssetPath, out _),
                Is.False,
                "the coalesced overlay must still express the final deletion");
        }

        [Test]
        public void ScanOverlay_PastItsBound_IsDroppedSoTheNextScanIsAuthoritative()
        {
            UseStableCaptureOverrides();
            var kept = new AssetRecord(
                "bounded-overlay-guid",
                "Assets/Kept.asset",
                "Kept",
                "TestAsset");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { kept },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);

            var boundedIndex = new InMemoryAssetIndex();
            var boundedSource = new TargetedRecordSource(Array.Empty<AssetRecord>());
            var bounded = new AssetIndexSynchronizer(
                boundedSource,
                boundedSource,
                boundedIndex,
                enablePersistentCache: true);
            var boundedRecorder = (IAssetScanChangeRecorder)bounded;
            boundedRecorder.RecordConcreteChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { kept.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            var generationBeforeDrop = bounded.AssetChangeGenerationForTests;
            for (var i = 0;
                i < AssetIndexSynchronizer.MaxScanOverlayEntryCount;
                i++)
            {
                boundedRecorder.RecordConcreteChanges(new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { $"Assets/Bound{i}.asset" },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            }

            Assert.That(
                bounded.ScanOverlayEntryCountForTests,
                Is.Zero,
                "past the bound the overlay is dropped whole, never truncated");
            Assert.That(
                bounded.AssetChangeGenerationForTests,
                Is.GreaterThan(generationBeforeDrop),
                "dropping the overlay must retire any scan captured against it");

            var boundedCompleted = false;
            bounded.SynchronizeAsync(
                onFullScanStarted: null,
                onComplete: () => boundedCompleted = true);

            Assert.That(boundedCompleted, Is.True);
            Assert.That(
                boundedIndex.TryGetByPath(kept.AssetPath, out _),
                Is.True,
                "a dropped overlay must stop subtracting: only a fresh authoritative "
                + "walk is trusted, and the durable journal still owns the replay");

            // Control: the very same deletion, below the bound, is still applied.
            var withinIndex = new InMemoryAssetIndex();
            var withinSource = new TargetedRecordSource(Array.Empty<AssetRecord>());
            var within = new AssetIndexSynchronizer(
                withinSource,
                withinSource,
                withinIndex,
                enablePersistentCache: true);
            ((IAssetScanChangeRecorder)within).RecordConcreteChanges(
                new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { kept.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var withinCompleted = false;
            within.SynchronizeAsync(
                onFullScanStarted: null,
                onComplete: () => withinCompleted = true);

            Assert.That(withinCompleted, Is.True);
            Assert.That(withinIndex.TryGetByPath(kept.AssetPath, out _), Is.False);
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_DuringVerification_RetainJournalUntilCurrentWalk()
        {
            UseStableCaptureOverrides();
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>()), Is.True);
            var before = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var imported = new AssetRecord("unresolved-verification-guid", "Assets/UnresolvedVerification.png", "UnresolvedVerification", "Texture2D");
            var source = new VerificationOverflowSource(Array.Empty<AssetRecord>(), new[] { imported });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForVerificationEnumeration(source, 0);
                RecordUnresolvedImport(synchronizer, imported.AssetPath);
                source.Release(0);
                yield return WaitForVerificationEnumeration(source, 1);
                Assert.That(index.TryGetByPath(imported.AssetPath, out _), Is.False);
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Not.Empty);
                source.Release(1);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(source.EnumerationCount, Is.EqualTo(2));
                Assert.That(index.TryGetByPath(imported.AssetPath, out _), Is.True);
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(record => record.AssetPath), Is.EqualTo(new[] { imported.AssetPath }));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_ExistingPath_DoesNotOverlayStaleMetadata()
        {
            yield return AssertUnresolvedMetadataRecovery(laterResolvedBatch: false);
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_LaterConcreteBatch_RestartsUntrustedRecoveryProof()
        {
            yield return AssertUnresolvedMetadataRecovery(laterResolvedBatch: true);
        }

        private static IEnumerator AssertUnresolvedMetadataRecovery(bool laterResolvedBatch)
        {
            UseStableCaptureOverrides();
            var old = new AssetRecord("unresolved-metadata-guid", "Assets/UnresolvedMetadata.png", "OldMetadata", "Texture2D");
            var current = new AssetRecord(old.Guid, old.AssetPath, "CurrentMetadata", "Texture2D", unityLabels: new[] { "Current Label" });
            var latest = new AssetRecord(old.Guid, old.AssetPath, "LatestMetadata", "Texture2D", unityLabels: new[] { "Latest Label" });
            Assert.That(PersistentAssetIndexCache.Save(new[] { old }, Array.Empty<AssetFolderRecord>()), Is.True);
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var source = new VerificationOverflowSource(new[] { old }, new[] { current }, new[] { latest });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForVerificationEnumeration(source, 0);
                RecordUnresolvedImport(synchronizer, old.AssetPath);
                source.Release(0);
                yield return WaitForVerificationEnumeration(source, 1);
                if (laterResolvedBatch)
                {
                    index.Upsert(latest);
                    var changes = new ProjectAssetChanges(new[] { latest.AssetPath }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                    PersistentAssetIndexCache.RecordPendingChanges(changes);
                    synchronizer.InvalidateInProgressScan();
                    synchronizer.RecordConcreteChanges(changes);
                    source.Release(1);
                    yield return WaitForVerificationEnumeration(source, 2);
                    Assert.That(index.TryGetByPath(latest.AssetPath, out var live), Is.True);
                    Assert.That(live, Is.EqualTo(latest), "an older recovery walk cannot overwrite the later resolved batch");
                }
                source.Release(laterResolvedBatch ? 2 : 1);
                yield return WaitForBackgroundJob(synchronizer);
                var expected = laterResolvedBatch ? latest : current;
                Assert.That(index.TryGetByPath(expected.AssetPath, out var record), Is.True);
                Assert.That(record, Is.EqualTo(expected));
                Assert.That(source.EnumerationCount, Is.EqualTo(laterResolvedBatch ? 3 : 2));
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Single(), Is.EqualTo(expected));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_DuringTypeTail_VerifyBeforePromotingRevision()
        {
            UseStableCaptureOverrides();
            var old = new AssetRecord("unresolved-type-guid", "Assets/UnresolvedType.asset", "UnresolvedType", "OldConfig", baseTypeNames: new[] { "ScriptableObject" });
            var current = new AssetRecord(old.Guid, old.AssetPath, old.Name, "NewConfig", baseTypeNames: new[] { "ScriptableObject" });
            var imported = new AssetRecord("unresolved-type-import-guid", "Assets/TypeTailImport.png", "TypeTailImport", "Texture2D");
            Assert.That(PersistentAssetIndexCache.Save(new[] { old }, Array.Empty<AssetFolderRecord>()), Is.True);
            var before = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new VerificationOverflowSource(new[] { old, imported }) { RetypedRecord = current };
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
            synchronizer.ScriptTypeReconciliationSliceObserverForTests = _ =>
            {
                synchronizer.ScriptTypeReconciliationSliceObserverForTests = null;
                RecordUnresolvedImport(synchronizer, imported.AssetPath);
            };
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForVerificationEnumeration(source, 0);
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Not.Empty);
                source.Release(0);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(index.TryGetByPath(imported.AssetPath, out _), Is.True);
                Assert.That(PersistentAssetIndexCache.TryLoad(43, PersistentAssetIndexCache.CaptureRecoveryState(),
                    out var saved, out _, out var revisionChanged, out _, out _), Is.True);
                Assert.That(saved.Select(record => record.AssetPath), Is.EquivalentTo(new[] { current.AssetPath, imported.AssetPath }));
                Assert.That(saved.Single(record => record.Guid == current.Guid).TypeName, Is.EqualTo("NewConfig"));
                Assert.That(revisionChanged, Is.False);
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_DuringVerificationSave_RejectStaleCommit()
        {
            yield return AssertUnresolvedDuringSave(typeSave: false, supersede: false, serializationFault: false);
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_DuringTypeSave_PreserveRetypeOnReplacement()
        {
            yield return AssertUnresolvedDuringSave(typeSave: true, supersede: false, serializationFault: false);
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_DuringSaveSupersession_RetainEvidenceForCurrentJob()
        {
            yield return AssertUnresolvedDuringSave(typeSave: false, supersede: true, serializationFault: false);
        }

        [UnityTest]
        public IEnumerator UnresolvedChanges_DuringFaultedSave_RetainEvidenceUntilRetry()
        {
            yield return AssertUnresolvedDuringSave(typeSave: false, supersede: false, serializationFault: true);
        }

        private static IEnumerator AssertUnresolvedDuringSave(bool typeSave, bool supersede, bool serializationFault)
        {
            UseStableCaptureOverrides();
            var old = new AssetRecord("save-proof-type-guid", "Assets/SaveProofType.asset", "SaveProofType", "OldConfig", baseTypeNames: new[] { "ScriptableObject" });
            var current = new AssetRecord(old.Guid, old.AssetPath, old.Name, "NewConfig", baseTypeNames: new[] { "ScriptableObject" });
            var imported = new AssetRecord("save-proof-import-guid", "Assets/SaveProofImport.png", "SaveProofImport", "Texture2D");
            Assert.That(PersistentAssetIndexCache.Save(typeSave ? new[] { old } : Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()), Is.True);
            var before = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            AssetIndexSynchronizer.VerifiedThisProcess = typeSave;
            if (typeSave) PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var finalRecords = typeSave ? new[] { current, imported } : new[] { imported };
            var source = typeSave
                ? new VerificationOverflowSource(new[] { old, imported })
                : new VerificationOverflowSource(Array.Empty<AssetRecord>(), finalRecords);
            source.RetypedRecord = current;
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var priorObserver = PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("save was not released");
                if (serializationFault) throw new IOException("unresolved save failure");
            };
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                if (!typeSave)
                {
                    yield return WaitForVerificationEnumeration(source, 0);
                    source.Release(0);
                }
                for (var frame = 0; frame < 1000 && !entered.IsSet; frame++) yield return null;
                Assert.That(entered.IsSet, Is.True, "the snapshot must be staged before the unresolved batch");
                RecordUnresolvedImport(synchronizer, imported.AssetPath);
                if (supersede) synchronizer.SynchronizeAsync(null, null);
                if (serializationFault)
                    LogAssert.Expect(LogType.Warning, new Regex("Index snapshot serialization failed"));
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
                release.Set();
                synchronizer.FlushPendingSnapshotSave();
                if (serializationFault)
                {
                    yield return WaitForBackgroundJob(synchronizer);
                    Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                    Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Not.Empty);
                    synchronizer.SynchronizeAsync(null, null);
                }
                var replacement = typeSave ? 0 : 1;
                yield return WaitForVerificationEnumeration(source, replacement);
                for (var frame = 0; frame < 1000 && HasStagedSnapshotFiles(); frame++) yield return null;
                Assert.That(HasStagedSnapshotFiles(), Is.False, "the invalidated save must release its slot");
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Not.Empty);
                source.Release(replacement);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(index.TryGetByPath(imported.AssetPath, out _), Is.True);
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(record => record.AssetPath), Is.EquivalentTo(finalRecords.Select(record => record.AssetPath)));
                if (typeSave) Assert.That(saved.Single(record => record.Guid == current.Guid).TypeName, Is.EqualTo("NewConfig"));
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty);
            }
            finally
            {
                release.Set();
                source.ReleaseAll();
                synchronizer.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }
        }

        private static void RecordUnresolvedImport(AssetIndexSynchronizer synchronizer, string path)
        {
            var changes = new ProjectAssetChanges(new[] { path }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            PersistentAssetIndexCache.RecordPendingChanges(changes);
            synchronizer.InvalidateInProgressScan();
            synchronizer.RecordUnresolvedChanges(new[] { path });
            synchronizer.RecordConcreteChanges(changes);
        }

        [UnityTest]
        public IEnumerator Verification_OverlayOverflow_RestartsBeforePublishingAndPreservesRetype()
        {
            yield return AssertVerificationOverlayRecovery(repeatOverflow: false);
        }

        [UnityTest]
        public IEnumerator Verification_ReplacementOverlayOverflow_RestartsAgain()
        {
            yield return AssertVerificationOverlayRecovery(repeatOverflow: true);
        }

        private static IEnumerator AssertVerificationOverlayRecovery(bool repeatOverflow)
        {
            UseStableCaptureOverrides();
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>()), Is.True);
            var before = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var stale = new AssetRecord(
                "overflow-deleted-guid", "Assets/OverflowDeleted.asset", "OverflowDeleted", "TestAsset");
            var current = new AssetRecord(
                "overflow-kept-guid", "Assets/OverflowKept.asset", "OverflowKept", "OldConfig",
                baseTypeNames: new[] { "ScriptableObject" });
            var retyped = new AssetRecord(
                current.Guid, current.AssetPath, current.Name, "NewConfig",
                baseTypeNames: new[] { "ScriptableObject" });
            var source = new VerificationOverflowSource(
                new[] { stale },
                repeatOverflow ? new[] { stale } : new[] { current },
                new[] { current })
            {
                RetypedRecord = retyped,
            };
            var index = new InMemoryAssetIndex();
            var published = new List<AssetCatalogDelta>();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true, published.Add);
            var coldStarts = 0;

            try
            {
                synchronizer.SynchronizeAsync(() => coldStarts++, null);
                yield return WaitForVerificationEnumeration(source, 0);
                RecordVerificationDeletions(synchronizer, stale.AssetPath, overflow: true);
                Assert.That(synchronizer.ScanOverlayEntryCountForTests, Is.Zero);
                source.Release(0);
                yield return WaitForVerificationEnumeration(source, 1);

                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(published, Is.Empty, "the retired walk must publish no stale delta");
                Assert.That(AssetIndexSynchronizer.VerifiedThisProcess, Is.False);
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                Assert.That(PersistentAssetIndexCache.CaptureRecoveryState().RequiresVerification, Is.True);

                if (repeatOverflow)
                {
                    RecordVerificationDeletions(synchronizer, stale.AssetPath, overflow: true);
                    source.Release(1);
                    yield return WaitForVerificationEnumeration(source, 2);
                    Assert.That(published, Is.Empty);
                    Assert.That(AssetIndexSynchronizer.VerifiedThisProcess, Is.False);
                    Assert.That(PersistentAssetIndexCache.CaptureRecoveryState().RequiresVerification, Is.True);
                }

                source.Release(repeatOverflow ? 2 : 1);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(source.EnumerationCount, Is.EqualTo(repeatOverflow ? 3 : 2));
                Assert.That(coldStarts, Is.Zero, "replacement verification remains silent");
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(index.TryGetByPath(current.AssetPath, out var kept), Is.True);
                Assert.That(kept.TypeName, Is.EqualTo("NewConfig"));
                Assert.That(published.SelectMany(delta => delta.UpsertedRecords)
                    .Select(record => record.AssetPath), Does.Not.Contain(stale.AssetPath));
                Assert.That(AssetIndexSynchronizer.VerifiedThisProcess, Is.True);
                var recovery = PersistentAssetIndexCache.CaptureRecoveryState();
                Assert.That(recovery.RequiresVerification, Is.False);
                Assert.That(PersistentAssetIndexCache.TryLoad(
                    43, recovery, out var saved, out _, out var revisionChanged, out _, out _), Is.True);
                Assert.That(saved.Select(record => record.TypeName), Is.EqualTo(new[] { "NewConfig" }));
                Assert.That(revisionChanged, Is.False,
                    "the restarted verification must retain its retype-after requirement");
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Verification_SynchronousOverlayLoss_RestartsBeforePublishing()
        {
            UseStableCaptureOverrides();
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>()), Is.True);
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var stale = new AssetRecord(
                "sync-deleted-guid", "Assets/SyncDeleted.asset", "SyncDeleted", "TestAsset");
            var current = new AssetRecord(
                "sync-current-guid", "Assets/SyncCurrent.asset", "SyncCurrent", "TestAsset");
            var source = new ReentrantFallbackSource(stale, current);
            var index = new InMemoryAssetIndex();
            var published = new List<AssetCatalogDelta>();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true, published.Add);
            source.StartReplacement = () =>
                RecordVerificationDeletions(synchronizer, stale.AssetPath, overflow: true);
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
                Assert.That(published.SelectMany(delta => delta.UpsertedRecords)
                    .Select(record => record.AssetPath), Is.EqualTo(new[] { current.AssetPath }));
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(record => record.AssetPath), Is.EqualTo(new[] { current.AssetPath }));
                Assert.That(PersistentAssetIndexCache.CaptureRecoveryState().RequiresVerification, Is.False);
            }
            finally
            {
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Verification_BoundedOverlayAndCoarseSignal_KeepOneWalk()
        {
            UseStableCaptureOverrides();
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>()), Is.True);
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var stale = new AssetRecord(
                "bounded-deleted-guid", "Assets/BoundedDeleted.asset", "BoundedDeleted", "TestAsset");
            var source = new VerificationOverflowSource(new[] { stale });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForVerificationEnumeration(source, 0);
                RecordVerificationDeletions(synchronizer, stale.AssetPath, overflow: false);
                synchronizer.InvalidateInProgressScan(); // A coarse signal adds no lost concrete history.
                source.Release(0);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(source.EnumerationCount, Is.EqualTo(1));
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved, Is.Empty);
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Verification_OverflowReplacementFault_RetainsRecoveryEvidence()
        {
            UseStableCaptureOverrides();
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>()), Is.True);
            var before = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var stale = new AssetRecord(
                "fault-deleted-guid", "Assets/FaultDeleted.asset", "FaultDeleted", "TestAsset");
            var source = new VerificationOverflowSource(new[] { stale }, Array.Empty<AssetRecord>())
            {
                FailingEnumeration = 1,
            };
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForVerificationEnumeration(source, 0);
                RecordVerificationDeletions(synchronizer, stale.AssetPath, overflow: true);
                var recovery = PersistentAssetIndexCache.CaptureRecoveryState();
                source.Release(0);
                yield return WaitForVerificationEnumeration(source, 1);
                LogAssert.Expect(LogType.Warning, new Regex("Verification walk failed"));
                source.Release(1);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(AssetIndexSynchronizer.VerifiedThisProcess, Is.False);
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                var after = PersistentAssetIndexCache.CaptureRecoveryState();
                Assert.That(after.RequiresVerification, Is.True);
                Assert.That(after.AuthoritativeFallbackToken, Is.EqualTo(recovery.AuthoritativeFallbackToken));
                Assert.That(after.DirtyToken, Is.EqualTo(recovery.DirtyToken));
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Verification_OverflowReplacementSuperseded_CannotSaveItsResult()
        {
            UseStableCaptureOverrides();
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>()), Is.True);
            var before = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var stale = new AssetRecord(
                "superseded-deleted-guid", "Assets/SupersededDeleted.asset", "SupersededDeleted", "TestAsset");
            var source = new VerificationOverflowSource(
                new[] { stale }, new[] { stale }, Array.Empty<AssetRecord>());
            var index = new InMemoryAssetIndex();
            var published = new List<AssetCatalogDelta>();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true, published.Add);
            try
            {
                synchronizer.SynchronizeAsync(null, null);
                yield return WaitForVerificationEnumeration(source, 0);
                RecordVerificationDeletions(synchronizer, stale.AssetPath, overflow: true);
                source.Release(0);
                yield return WaitForVerificationEnumeration(source, 1);
                synchronizer.SynchronizeAsync(null, null);
                source.Release(1);
                yield return WaitForVerificationEnumeration(source, 2);
                Assert.That(published, Is.Empty);
                Assert.That(AssetIndexSynchronizer.VerifiedThisProcess, Is.False);
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(before));
                Assert.That(PersistentAssetIndexCache.CaptureRecoveryState().RequiresVerification, Is.True);
                source.Release(2);
                yield return WaitForBackgroundJob(synchronizer);
                Assert.That(source.EnumerationCount, Is.EqualTo(3));
                Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False);
                Assert.That(published, Is.Empty);
                Assert.That(PersistentAssetIndexCache.CaptureRecoveryState().RequiresVerification, Is.False);
            }
            finally
            {
                source.ReleaseAll();
                synchronizer.Dispose();
            }
        }

        private static void RecordVerificationDeletions(
            AssetIndexSynchronizer synchronizer,
            string deletedPath,
            bool overflow)
        {
            var deleted = new string[overflow ? AssetIndexSynchronizer.MaxScanOverlayEntryCount + 1 : 1];
            deleted[0] = deletedPath;
            for (var i = 1; i < deleted.Length; i++)
            {
                deleted[i] = "Assets/VerificationDeleted" + i + ".asset";
            }

            var changes = new ProjectAssetChanges(
                Array.Empty<string>(), deleted, Array.Empty<string>(), Array.Empty<string>());
            PersistentAssetIndexCache.RecordPendingChanges(changes);
            synchronizer.InvalidateInProgressScan();
            synchronizer.RecordConcreteChanges(changes);
        }

        private static IEnumerator WaitForVerificationEnumeration(
            VerificationOverflowSource source,
            int invocation)
        {
            for (var frame = 0; frame < 1000 && !source.Started(invocation); frame++)
            {
                yield return null;
            }

            Assert.That(source.Started(invocation), Is.True, "verification enumeration " + invocation + " never started");
        }

        private sealed class VerificationOverflowSource : IAssetRecordSource
        {
            private readonly IReadOnlyList<AssetRecord>[] _snapshots;
            private readonly ManualResetEventSlim[] _started;
            private readonly ManualResetEventSlim[] _release;
            private int _enumerationCount;

            internal VerificationOverflowSource(params IReadOnlyList<AssetRecord>[] snapshots)
            {
                _snapshots = snapshots;
                _started = snapshots.Select(_ => new ManualResetEventSlim(false)).ToArray();
                _release = snapshots.Select(_ => new ManualResetEventSlim(false)).ToArray();
            }

            public bool IsThreadSafe => true;
            internal int FailingEnumeration { get; set; } = -1;
            internal AssetRecord RetypedRecord { get; set; }
            internal int EnumerationCount => Volatile.Read(ref _enumerationCount);
            internal bool Started(int invocation) => _started[invocation].IsSet;
            internal void Release(int invocation) => _release[invocation].Set();
            internal void ReleaseAll()
            {
                foreach (var release in _release) release.Set();
            }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null) { }

            public IEnumerable<AssetRecord> GetRecords()
            {
                var invocation = Interlocked.Increment(ref _enumerationCount) - 1;
                if (invocation >= _snapshots.Length)
                {
                    throw new InvalidOperationException("Unexpected extra verification walk.");
                }

                _started[invocation].Set();
                if (!_release[invocation].Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release verification enumeration " + invocation);
                }

                if (invocation == FailingEnumeration)
                {
                    throw new IOException("verification source failure");
                }

                return _snapshots[invocation];
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => Array.Empty<AssetFolderRecord>();
            public AssetRecord BuildRecord(AssetPath assetPath) =>
                assetPath.Value == RetypedRecord?.AssetPath ? RetypedRecord : null;
            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;
        }

        [UnityTest]
        public IEnumerator ScriptTypeReconciliation_IsBudgetedAcrossTicksAndMatchesTheSinglePass()
        {
            UseStableCaptureOverrides();
            const int ambiguousCount = 7;
            var stale = new List<AssetRecord>
            {
                new AssetRecord(
                    "budget-script-guid",
                    "Assets/Changed.cs",
                    "Changed",
                    "MonoScript"),
            };
            var current = new List<AssetRecord>
            {
                new AssetRecord(
                    "budget-script-guid",
                    "Assets/Changed.cs",
                    "Changed",
                    "MonoScript"),
            };
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"budget-ambiguous-{i}",
                    $"Assets/Ambiguous{i}.asset",
                    $"Ambiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"budget-ambiguous-{i}",
                    $"Assets/Ambiguous{i}.asset",
                    $"Ambiguous{i}",
                    "CurrentType"));
            }

            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { "Assets/Changed.cs" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            // Each rebuild costs more than one budget slice, so a budgeted run must
            // hand the frame back after every single record.
            var source = new SlowTargetedRecordSource(current, buildDelayMilliseconds: 3);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var slices = new List<int>();
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            var completed = false;
            // A flag, never Assert.Fail: BeginFullScan invokes this inside a try/catch.
            var fullScanStarted = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            // Adjusted to the follow-up-job timing (T5.6). Completion no longer waits for
            // the retype: it fires at hydration. What must still span editor ticks is the
            // JOB, so the budget assertion moved onto the job's own outstanding-ness and
            // its slice bookkeeping, where it is now measured rather than implied.
            Assert.That(
                completed,
                Is.True,
                "the operation completes at hydration - the retype is a follow-up job");
            Assert.That(
                slices.Sum(),
                Is.LessThan(ambiguousCount),
                "a batch that outruns the budget must continue on later ticks");
            Assert.That(synchronizer.HasBackgroundJob, Is.True);

            for (var frame = 0;
                frame < 1000 && slices.Sum() < ambiguousCount;
                frame++)
            {
                yield return null;
            }

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "and the job ends when its save resolves");
            Assert.That(fullScanStarted, Is.False,
                "a targeted metadata refresh is not a cold scan");
            Assert.That(source.EnumerationCount, Is.Zero);
            for (var i = 0; i < current.Count; i++)
            {
                Assert.That(
                    index.TryGetByPath(current[i].AssetPath, out var refreshed),
                    Is.True);
                Assert.That(
                    refreshed.TypeName,
                    Is.EqualTo(current[i].TypeName),
                    "the final state must equal the old single-pass result");
            }

            Assert.That(
                slices.Sum(),
                Is.EqualTo(ambiguousCount),
                "every candidate is visited exactly once across the budgeted slices; "
                + "the extension-typed script record is not a candidate at all");
            Assert.That(
                slices.Count,
                Is.GreaterThanOrEqualTo(ambiguousCount),
                "a single uninterrupted pass would report one slice; each expensive "
                + "record must end its own");
            Assert.That(
                slices,
                Is.All.LessThanOrEqualTo(2),
                "no tick may run past the main-thread budget (a slice carries one "
                + "record that costs more than the budget, plus any free skips)");
        }

        [UnityTest]
        public IEnumerator ScriptTypeReconciliation_VisitsOnlyTheCandidatesAndMatchesTheFullWalk()
        {
            UseStableCaptureOverrides();
            const int catalogSize = 10000;
            const int ambiguousCount = 100;
            // Extensions the map types concretely and no ScriptedImporter can claim,
            // so the 9,900 fillers are provably outside the candidate set.
            var fillerExtensions = new[] { ".cs", ".mat", ".prefab", ".unity" };
            var fillerTypeNames = new[] { "MonoScript", "Material", "GameObject", "SceneAsset" };
            var stale = new List<AssetRecord>(catalogSize);
            var current = new List<AssetRecord>(catalogSize);
            var changedScript = new AssetRecord(
                "wide-script-guid",
                "Assets/Changed.cs",
                "Changed",
                "MonoScript");
            stale.Add(changedScript);
            current.Add(changedScript);
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"wide-ambiguous-{i}",
                    $"Assets/Ambiguous{i}.asset",
                    $"Ambiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"wide-ambiguous-{i}",
                    $"Assets/Ambiguous{i}.asset",
                    $"Ambiguous{i}",
                    "CurrentType"));
            }

            for (var i = 0; i < catalogSize - ambiguousCount - 1; i++)
            {
                var slot = i % fillerExtensions.Length;
                var filler = new AssetRecord(
                    $"wide-filler-{i}",
                    $"Assets/Filler{i}{fillerExtensions[slot]}",
                    $"Filler{i}",
                    fillerTypeNames[slot]);
                stale.Add(filler);
                current.Add(filler);
            }

            Assert.That(
                PersistentAssetIndexCache.Save(stale, Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { "Assets/Changed.cs" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 71;

            // The reference set: the predicate the old full walk applied to EVERY
            // record in the catalog. The reconciliation must rebuild exactly these.
            var scriptedImporterExtensions =
                AssetDatabaseAssetRecordSource.CaptureScriptedImporterExtensions();
            var expectedRefreshedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < stale.Count; i++)
            {
                if (stale[i].BaseTypeNames.Count > 0
                    || AssetDatabaseAssetRecordSource.RequiresAuthoritativeType(
                        stale[i].AssetPath,
                        scriptedImporterExtensions))
                {
                    expectedRefreshedPaths.Add(stale[i].AssetPath);
                }
            }

            Assert.That(
                expectedRefreshedPaths.Count,
                Is.EqualTo(ambiguousCount),
                "the fixture's fillers must be unambiguous for this rig's importers");

            var source = new TargetedRecordSource(current);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var slices = new List<int>();
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            var completed = false;
            // A flag, never Assert.Fail: BeginFullScan invokes this inside a try/catch.
            var fullScanStarted = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            // Adjusted to the follow-up-job timing (T5.6): completion now fires at
            // hydration, so waiting on it would no longer wait for the job. The job's own
            // progress is what this test measures, so the wait is on the slice count.
            for (var frame = 0;
                frame < 1000 && slices.Sum() < expectedRefreshedPaths.Count;
                frame++)
            {
                yield return null;
            }

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(fullScanStarted, Is.False,
                "a targeted metadata refresh is not a cold scan");
            Assert.That(
                source.EnumerationCount,
                Is.Zero,
                "a targeted metadata refresh must not fall back to a catalog enumeration");
            Assert.That(
                slices.Sum(),
                Is.LessThanOrEqualTo(ambiguousCount),
                $"the reconciliation must look at the candidates, not the catalog "
                    + $"(catalog {stale.Count}, slices {slices.Count})");
            Assert.That(
                slices.Sum(),
                Is.EqualTo(expectedRefreshedPaths.Count),
                $"and it must look at every candidate the full walk would have rebuilt "
                    + $"(slices {slices.Count}, indexed {index.Count})");

            var refreshedPaths = new List<string>();
            for (var i = 0; i < stale.Count; i++)
            {
                Assert.That(
                    index.TryGetByPath(stale[i].AssetPath, out var indexed),
                    Is.True,
                    stale[i].AssetPath);
                if (!string.Equals(indexed.TypeName, stale[i].TypeName, StringComparison.Ordinal))
                {
                    refreshedPaths.Add(stale[i].AssetPath);
                    Assert.That(
                        indexed.TypeName,
                        Is.EqualTo("CurrentType"),
                        $"'{stale[i].AssetPath}' was rebuilt but is not an ambiguous record");
                }
            }

            Assert.That(
                refreshedPaths,
                Is.EquivalentTo(expectedRefreshedPaths),
                "the refreshed set must equal the old full walk's result exactly");
        }

        // The producer-side empty-delta guard, with its positive half in the same fixture
        // and on the same instance, in the order a real project produces them: the first
        // compile after a type changes publishes one delta, and every compile after that
        // rebuilds the very same candidate into a record the index already holds and
        // publishes nothing. Without the guard the second pass would republish every live
        // pane per tick of the job, in exactly the post-compile window this plan exists
        // to make quiet.
        //
        // The pairing is what makes each half discriminating: the non-empty pass proves
        // the callback is wired on this synchronizer at all (a zero that came from a
        // missing callback would be worthless), and the slice-observer sum proves the
        // empty pass really visited and rebuilt its candidate rather than skipping it.
        [Test]
        public void TypeReconciliation_UnchangedTypeSlice_PublishesNothing()
        {
            UseStableCaptureOverrides();
            var scriptRecord = new AssetRecord(
                "unchanged-slice-script-guid",
                "Assets/UnchangedSlice.cs",
                "UnchangedSlice",
                "MonoScript");
            var stale = new AssetRecord(
                "unchanged-slice-guid",
                "Assets/UnchangedSlice.asset",
                "UnchangedSlice",
                "StaleType");
            var current = new AssetRecord(
                "unchanged-slice-guid",
                "Assets/UnchangedSlice.asset",
                "UnchangedSlice",
                "CurrentType");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { scriptRecord, stale },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            var source = new TargetedRecordSource(new[] { scriptRecord, current });
            var index = new InMemoryAssetIndex();
            var patches = new List<AssetCatalogDelta>();
            var slices = new List<int>();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true,
                onCatalogPatched: delta => patches.Add(delta));
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);

            // Pass 1 - the compile that really retypes the record.
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            synchronizer.SynchronizeAsync(onFullScanStarted: null, onComplete: null);
            synchronizer.FlushPendingSnapshotSave();

            Assert.That(slices.Sum(), Is.EqualTo(1),
                "one ambiguous record is one candidate; the extension-typed script is none");
            Assert.That(patches, Has.Count.EqualTo(1),
                "a slice that changed the catalog publishes exactly one delta - and this "
                + "is what proves the callback is wired for the assertion below");
            Assert.That(index.TryGetByPath(stale.AssetPath, out var patched), Is.True);
            Assert.That(patched.TypeName, Is.EqualTo("CurrentType"));

            // Pass 2 - the ordinary compile after it: the same candidate is rebuilt into
            // a record the index already holds, so the apply short-circuits and the delta
            // is empty. It is also the job's FINAL slice, which is where a guard spelt as
            // a `return` inside Poll would skip the settle check and never end the job.
            var patchesAfterFirstPass = patches.Count;
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 44;
            synchronizer.SynchronizeAsync(onFullScanStarted: null, onComplete: null);

            Assert.That(slices.Sum(), Is.EqualTo(2),
                "the second job visited and rebuilt the candidate - 'published nothing' "
                + "must not mean 'looked at nothing'");
            Assert.That(patches, Has.Count.EqualTo(patchesAfterFirstPass),
                "a rebuilt record identical to the indexed one is not a catalog change, "
                + "so nothing is published");

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "the job settled and its save resolved: an empty final slice must not "
                + "latch the job open");
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                44u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.False,
                "and the save that promotes the new revision still happened");

            // Pass 3 - nothing moved, so there is nothing to reconcile and no job.
            var fullScanStarted = false;
            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: null);

            Assert.That(fullScanStarted, Is.False);
            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "an unmoved artifact revision starts no job at all - a started job would "
                + "still be outstanding here, waiting on its own save");
            Assert.That(slices.Sum(), Is.EqualTo(2),
                "and rebuilds nothing");
            synchronizer.FlushPendingSnapshotSave();
        }

        // The compile-only reload with an empty journal: nothing to patch, but promoting
        // the artifact revision IS the job's save's job. Red on the pre-rewrite tree,
        // where an empty journal plus a moved revision took BeginFullScan(false) - a
        // silent full rescan on every compile, which is the defect this plan exists for.
        [Test]
        public void CompileOnlyReload_EmptyJournal_PromotesTheArtifactRevisionWithoutAScan()
        {
            UseStableCaptureOverrides();
            var cached = new AssetRecord(
                "compile-only-guid",
                "Assets/CompileOnly.cs",
                "CompileOnly",
                "MonoScript");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { cached },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            // Every leg of a scan throws on this source, so "no scan" is proven by the
            // test finishing at all, not only by the counters below.
            var source = new ThrowIfEnumeratedSource();
            var index = new InMemoryAssetIndex();
            var slices = new List<int>();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            var fullScanStarted = false;
            var completed = false;

            var counts = MeasurePerfCounters(() =>
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: () => completed = true);
                synchronizer.FlushPendingSnapshotSave();
            });

            Assert.That(fullScanStarted, Is.False,
                "an empty journal with a moved revision is a compile, not a structural miss");
            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(cached.AssetPath, out _), Is.True);
            Assert.That(slices.Sum(), Is.Zero,
                "an extension-typed catalog offers the job no candidates");
            Assert.That(
                counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                Is.Zero,
                "AC3: zero filesystem walks");
            Assert.That(
                counts.GetCount(ZepPerfCounter.FullRecordEnumerations),
                Is.Zero,
                "AC3: and zero record enumerations in the synchronizer");
            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "the zero-candidate job ends when its save resolves");
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.False,
                "the zero-candidate job saves, so the revision is promoted");

            var secondFullScanStarted = false;
            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => secondFullScanStarted = true,
                onComplete: null);

            Assert.That(secondFullScanStarted, Is.False);
            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "the next reload sees an unchanged revision and starts no job - without "
                + "the save above it would re-run a zero-candidate job for ever");
            synchronizer.FlushPendingSnapshotSave();
        }

        // A record the source cannot rebuild is no longer an exit. Red on the pre-rewrite
        // tree, where the first null BuildRecord called Settle(false) and escalated to a
        // full authoritative scan - the exact escalation §2 measured on the user's project.
        [Test]
        public void ScriptTypeReconciliation_UnresolvableCandidate_KeepsTheRecordAndFinishes()
        {
            UseStableCaptureOverrides();
            var unresolvable = new AssetRecord(
                "unresolvable-guid",
                "Assets/Unresolvable.asset",
                "Unresolvable",
                "StaleType");
            var staleNeighbour = new AssetRecord(
                "neighbour-guid",
                "Assets/Neighbour.asset",
                "Neighbour",
                "StaleType");
            var currentNeighbour = new AssetRecord(
                "neighbour-guid",
                "Assets/Neighbour.asset",
                "Neighbour",
                "CurrentType");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { unresolvable, staleNeighbour },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            // The unresolvable path is simply absent from the source, which is what an
            // importer that cannot answer looks like from here.
            var source = new TargetedRecordSource(new[] { currentNeighbour });
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            LogAssert.Expect(
                LogType.Log,
                new Regex(
                    @"Script type reconciliation kept 1 record\(s\) the source could not rebuild"));

            var counts = MeasurePerfCounters(() =>
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: null);
                synchronizer.FlushPendingSnapshotSave();
            });

            Assert.That(fullScanStarted, Is.False,
                "one unreadable importer is not a reason to rescan the project");
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(
                counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                Is.Zero);
            Assert.That(index.TryGetByPath(unresolvable.AssetPath, out var kept), Is.True);
            Assert.That(kept.TypeName, Is.EqualTo("StaleType"),
                "the record keeps the type the snapshot recorded");
            Assert.That(index.TryGetByPath(staleNeighbour.AssetPath, out var rebuilt), Is.True);
            Assert.That(rebuilt.TypeName, Is.EqualTo("CurrentType"),
                "and the loop moved on to the next candidate instead of ending the job");
            Assert.That(synchronizer.HasBackgroundJob, Is.False);
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.False,
                "every candidate was visited, so this is the Completed exit and it saves");
        }

        // The Faulted (rebuild slice) exit. Red on the pre-rewrite tree, where the same
        // catch called Settle(false) and escalated to a full scan. It is also the test
        // that fails a parameterless Settle: IsCurrent(operation) is still true here, so
        // that implementation would commit a NEW-revision snapshot from a half-reconciled
        // index and fail the revision assertion below.
        [Test]
        public void TypeReconciliation_SliceThrows_EndsTheJobWithoutAScanOrSave()
        {
            UseStableCaptureOverrides();
            var changedScript = new AssetRecord(
                "slice-throw-script-guid",
                "Assets/SliceThrow.cs",
                "SliceThrow",
                "MonoScript");
            var stale = new AssetRecord(
                "slice-throw-guid",
                "Assets/SliceThrow.asset",
                "SliceThrow",
                "StaleType");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { changedScript, stale },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { changedScript.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            // Resolves the journaled .cs so hydration lands, throws for everything else -
            // which is the ambiguous record the job is about to rebuild.
            var source = new ThrowingTargetedRecordSource(new[] { changedScript });
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            LogAssert.Expect(
                LogType.Warning,
                new Regex(
                    "Script type reconciliation failed .* keeps its recorded types"));

            var counts = MeasurePerfCounters(() =>
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: null));

            Assert.That(fullScanStarted, Is.False,
                "a faulted slice ends the job; it does not escalate to a scan");
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(
                counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                Is.Zero);
            Assert.That(
                counts.GetCount(ZepPerfCounter.FullRecordEnumerations),
                Is.Zero);
            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "the faulted exit clears the outstanding job then and there - there is "
                + "no save left to resolve it");
            Assert.That(index.TryGetByPath(stale.AssetPath, out var kept), Is.True);
            Assert.That(kept.TypeName, Is.EqualTo("StaleType"));
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.True,
                "no snapshot was committed, so the OLD artifact revision is still on disk "
                + "and the next reload runs the job again");
            Assert.That(
                PersistentAssetIndexCache.CapturePendingChanges(),
                Is.Not.Empty,
                "and the journal is retired only by a committed save");
        }

        // The one leg on which the job's save-path MarkDirty suppression is both
        // reachable and discriminating. The serialization task faults, so
        // ResolvePendingSnapshotSave abandons the attempt - and AbandonSnapshotSave marks
        // nothing dirty from inside the cache, which makes the synchronizer's own
        // MarkDirty the only possible source. Without T5.4's CompletesOperation clause
        // that call runs (IsCurrent(operation) is still true here) and the next reload
        // pays the cold scan this plan removes.
        //
        // The control for the other direction already exists and is not duplicated here:
        // ColdSave_FailureAndCompatibility drives the same throwing observer through an
        // OPERATION's own save and asserts the dirty marker IS written.
        //
        // This is NOT the plan's TypeReconciliation_BusySaveSlot_DoesNotMarkTheCacheDirty,
        // and it is not a substitute for it - see plan §9 items 14 and 17 for why that
        // fixture is unreachable and why no reachable busy-slot state can discriminate.
        [Test]
        public void TypeReconciliation_FaultedSaveSerialization_DoesNotMarkTheCacheDirty()
        {
            UseStableCaptureOverrides();
            var scriptRecord = new AssetRecord(
                "faulted-save-script-guid",
                "Assets/FaultedSave.cs",
                "FaultedSave",
                "MonoScript");
            var stale = new AssetRecord(
                "faulted-save-guid",
                "Assets/FaultedSave.asset",
                "FaultedSave",
                "StaleType");
            var current = new AssetRecord(
                "faulted-save-guid",
                "Assets/FaultedSave.asset",
                "FaultedSave",
                "CurrentType");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { scriptRecord, stale },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new TargetedRecordSource(new[] { scriptRecord, current });
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            // Restored by TearDown, like every other override this fixture sets.
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
                throw new IOException("staged snapshot write failure");
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Index snapshot serialization failed"));

            synchronizer.SynchronizeAsync(onFullScanStarted: null, onComplete: null);

            Assert.That(index.TryGetByPath(stale.AssetPath, out var patched), Is.True);
            Assert.That(patched.TypeName, Is.EqualTo("CurrentType"),
                "the job really ran, so the assertions below are about its SAVE");
            Assert.That(synchronizer.HasBackgroundJob, Is.True,
                "the save is staged and has not resolved");

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(
                File.Exists(PersistentAssetIndexCache.DirtyMarkerPath),
                Is.False,
                "a job whose save faulted must not force the next reload into a cold scan");
            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "and the job stops being outstanding on that outcome too");
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.True,
                "the abandoned save left the old revision on disk");
            Assert.That(
                PersistentAssetIndexCache.CapturePendingChanges(),
                Is.Not.Empty,
                "and the unretired journal is what makes the next reload re-run the job");
        }

        // T5.3's structural-miss branch, and the plan's one new user-visible overlay
        // (§10 item 7). Red on the pre-rewrite tree, where this call site passed
        // onFullScanStarted: false and rebuilt the whole project in silence.
        //
        // It is also the positive control for every FolderTreeWalks delta-0 assertion in
        // this class: the counter is proven to move here, on a real full scan, so a zero
        // elsewhere is a measurement rather than a constant.
        [Test]
        public void UnreplayableJournal_IsVerifiedSilently()
        {
            UseStableCaptureOverrides();
            var cached = new AssetRecord(
                "unreplayable-guid",
                "Assets/Unreplayable.cs",
                "Unreplayable",
                "MonoScript");
            var scanned = new AssetRecord(
                "scanned-guid",
                "Assets/Scanned.asset",
                "Scanned",
                "TestAsset");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { cached },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            // A journaled import of a path the source can resolve neither as a record nor
            // as a folder: the batch cannot be replayed onto the snapshot, so the loaded
            // catalog is not trustworthy and only a full scan can restore it.
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { "Assets/Vanished.asset" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var source = new TargetedRecordSource(new[] { scanned });
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            var counts = MeasurePerfCounters(() =>
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: () => completed = true);
                synchronizer.FlushPendingSnapshotSave();
            });

            Assert.That(fullScanStarted, Is.False,
                "a journal entry that cannot be replayed is verified by a silent walk, "
                + "never by the overlay");
            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.GreaterThan(0),
                "the verification is a real walk of the source");
            Assert.That(
                counts.GetCount(ZepPerfCounter.VerificationJobs),
                Is.EqualTo(1));
            Assert.That(
                counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                Is.Zero,
                "and it never replaces the catalog wholesale, so the walk counter that "
                + "measures a cold rebuild stays at zero");
            Assert.That(index.TryGetByPath(scanned.AssetPath, out _), Is.True,
                "the walk's difference was applied over the snapshot that could not be patched");
        }

        // The poison behind "create a script, and the NEXT reload shows the
        // indexing overlay". Unity imports a script in two passes; the second
        // pass's batch is journaled while the retype job the first reload started
        // is still running. The job's save then finds the journal generation
        // moved and, before this fix, the cache answered that benign staleness
        // with MarkDirty — so the next reload refused a complete snapshot +
        // journal and cold-scanned. Red on the pre-fix tree at the dirty-marker
        // assertion, then at the next load's overlay.
        [UnityTest]
        public IEnumerator TypeReconciliationSave_JournalMovedDuringTheJob_LeavesTheSnapshotTrustedForTheNextLoad()
        {
            UseStableCaptureOverrides();
            const int ambiguousCount = 7;
            var scriptRecord = new AssetRecord(
                "moved-during-job-script-guid",
                "Assets/MovedDuringJob.cs",
                "MovedDuringJob",
                "MonoScript");
            var stale = new List<AssetRecord> { scriptRecord };
            var current = new List<AssetRecord> { scriptRecord };
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"moved-during-job-ambiguous-{i}",
                    $"Assets/MovedDuringJobAmbiguous{i}.asset",
                    $"MovedDuringJobAmbiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"moved-during-job-ambiguous-{i}",
                    $"Assets/MovedDuringJobAmbiguous{i}.asset",
                    $"MovedDuringJobAmbiguous{i}",
                    "CurrentType"));
            }

            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new SlowTargetedRecordSource(current, buildDelayMilliseconds: 3);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            try
            {
                var fullScanStarted = false;
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: null);
                Assert.That(fullScanStarted, Is.False, "the first reload hydrates");
                Assert.That(synchronizer.HasBackgroundJob, Is.True,
                    "the budget must leave the job mid-set, or nothing can land during it");

                // Unity's second import pass of the same script, journaled while
                // the job is still patching types.
                PersistentAssetIndexCache.RecordPendingChanges(
                    new ProjectAssetChanges(
                        new[] { scriptRecord.AssetPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));

                for (var frame = 0; frame < 2000 && synchronizer.HasBackgroundJob; frame++)
                {
                    yield return null;
                }

                Assert.That(synchronizer.HasBackgroundJob, Is.False,
                    "the job never settled within 2000 editor frames");
                Assert.That(File.Exists(PersistentAssetIndexCache.DirtyMarkerPath), Is.False,
                    "the job's save touched no marker; the cache is not dirty");
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                    "the job's save covers the batch journaled during it: the index already "
                    + "holds the script it re-imports, and the journal generation is captured "
                    + "when the save begins");

                // The next reload: it must hydrate, replay the journal, and never
                // announce a cold scan — that announcement is the user's overlay.
                var nextSource = new TargetedRecordSource(current);
                var nextIndex = new InMemoryAssetIndex();
                using var next = new AssetIndexSynchronizer(
                    nextSource,
                    nextSource,
                    nextIndex,
                    enablePersistentCache: true);
                var nextCompleted = false;
                var counts = MeasurePerfCounters(() =>
                {
                    next.SynchronizeAsync(
                        onFullScanStarted: () => Assert.Fail(
                            "the next reload must hydrate the snapshot; a cold scan here is the overlay"),
                        onComplete: () => nextCompleted = true);
                });
                Assert.That(nextCompleted, Is.True);
                Assert.That(nextSource.EnumerationCount, Is.Zero,
                    "nothing walked the project");
                Assert.That(
                    counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                    Is.Zero);
                Assert.That(nextIndex.TryGetByPath(scriptRecord.AssetPath, out _), Is.True);
            }
            finally
            {
                synchronizer.Dispose();
            }
        }

        // The user's "create a script" overlay, end to end on the background path a
        // real project takes: the snapshot is deserialized on a worker, and Unity's
        // post-compile import pass of the new script is journaled on the main thread
        // while that read is still running. The reload must hydrate the snapshot,
        // replay the longer journal (both batches), and never announce a cold scan.
        // Red before TryAdoptJournalGrowth: the loaded snapshot was "not current".
        // No signature overrides on purpose — they would route the synchronizer to
        // the synchronous path, and the worker-side read is the whole point.
        [UnityTest]
        public IEnumerator SynchronizeAsync_BatchJournaledDuringBackgroundValidation_HydratesAndReplaysIt()
        {
            var cached = new AssetRecord(
                "bg-growth-cached-guid",
                "Assets/BgGrowthCached.asset",
                "BgGrowthCached",
                "TestAsset");
            var appended = new AssetRecord(
                "bg-growth-appended-guid",
                "Assets/BgGrowthAppended.asset",
                "BgGrowthAppended",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { cached },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var source = new TargetedRecordSource(new[] { cached, appended });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var priorHook = PersistentAssetIndexCache.CacheDeserializedOverride;
            PersistentAssetIndexCache.CacheDeserializedOverride = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            var fullScanStarted = false;
            var completed = false;
            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => fullScanStarted = true,
                    onComplete: () => completed = true);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!entered.IsSet && clock.Elapsed.TotalSeconds < 30)
                {
                    yield return null;
                }
                Assert.That(entered.IsSet, Is.True,
                    "the worker did not reach the deserialized snapshot within 30 s");

                // Unity's second import pass, on the main thread, while the read waits.
                PersistentAssetIndexCache.RecordPendingChanges(
                    new ProjectAssetChanges(
                        new[] { appended.AssetPath },
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>()));
                release.Set();

                while (!completed && clock.Elapsed.TotalSeconds < 60)
                {
                    yield return null;
                }
                Assert.That(completed, Is.True, "hydration did not complete within 60 s");
                Assert.That(fullScanStarted, Is.False,
                    "a batch journaled during the read is replayed, not answered with a cold scan");
                Assert.That(source.EnumerationCount, Is.Zero, "nothing walked the project");
                Assert.That(index.TryGetByPath(cached.AssetPath, out _), Is.True);
                Assert.That(index.TryGetByPath(appended.AssetPath, out _), Is.True,
                    "the batch that landed mid-read is in the hydrated index");

                synchronizer.FlushPendingSnapshotSave();
                Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                    "the save retired the journal it replayed, the grown one included");
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(item => item.AssetPath),
                    Is.EquivalentTo(new[] { cached.AssetPath, appended.AssetPath }));
            }
            finally
            {
                release.Set();
                PersistentAssetIndexCache.CacheDeserializedOverride = priorHook;
                synchronizer.Dispose();
            }
        }

        // The user's "create a script from the panel" overlay, named by the perf log:
        // `journal-replay refused import=Assets/.../NewMonoBehaviourScript.cs`. The
        // Unity-menu create lands under a default name, the panel moves it to the
        // typed one, and the compile's reload then replays a journal whose first
        // batch imports a path that no longer exists. That is not a record the
        // source cannot describe; it is a file that was renamed after the batch —
        // and the move is the next batch. Red before ExistsOnDisk: the replay
        // refused the import and the reload cold-scanned.
        [Test]
        public void JournalImportOfAVanishedPath_IsSkippedAndTheRenameReplays()
        {
            UseStableCaptureOverrides();
            const string defaultPath = "Assets/NewMonoBehaviourScript.cs";
            var final = new AssetRecord(
                "renamed-create-guid",
                "Assets/Typed.cs",
                "Typed",
                "MonoScript");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { defaultPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { final.AssetPath },
                new[] { defaultPath }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { final.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges().Count, Is.GreaterThan(1),
                "precondition: the default-name import and the rename stay separate batches");
            // The source describes only what is on disk now: the typed name. The
            // folder observer is what proves the default name is gone from Assets.
            var source = new TargetedRecordSource(new[] { final });
            var observer = new RecordedDirectFolderSource();
            observer.Add("Assets", (final.AssetPath, false));
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true,
                directFolderSource: observer);
            var completed = false;
            var counts = MeasurePerfCounters(() =>
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => Assert.Fail(
                        "a renamed create must replay, not cold-scan"),
                    onComplete: () => completed = true);
                synchronizer.FlushPendingSnapshotSave();
            });
            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero, "nothing walked the project");
            Assert.That(counts.GetCount(ZepPerfCounter.FolderTreeWalks), Is.Zero);
            Assert.That(index.TryGetByPath(final.AssetPath, out _), Is.True,
                "the typed name is indexed");
            Assert.That(index.TryGetByPath(defaultPath, out _), Is.False,
                "the default name it was renamed from is not");
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                "the patched snapshot was saved and retired the journal");
        }

        // The user's "ghost asset": a script created under a default name, moved
        // to the typed one, then deleted — all before the compile's reload. The
        // journal compacts the move and the delete into one batch. Replaying it
        // built a record for the moved destination (a source can still describe
        // a path Unity has only just forgotten) and upserted it AFTER the batch's
        // removal, so the deleted asset came back as a row nothing on disk backed.
        // Red before the move-then-deleted rule.
        [Test]
        public void JournalMoveThenDeleteOfTheDestination_LeavesNoGhostRecord()
        {
            UseStableCaptureOverrides();
            const string defaultPath = "Assets/NewMonoBehaviourScript.cs";
            var typed = new AssetRecord(
                "ghost-create-guid",
                "Assets/New MonoBehaviour Script.cs",
                "New MonoBehaviour Script",
                "MonoScript");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()), Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                new[] { defaultPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { typed.AssetPath },
                new[] { defaultPath }));
            PersistentAssetIndexCache.RecordPendingChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { typed.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            // The source can still build the deleted destination — exactly the
            // stale-but-answerable state a just-deleted path is in.
            var source = new TargetedRecordSource(new[] { typed });
            var observer = new RecordedDirectFolderSource();
            observer.Add("Assets");
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true,
                directFolderSource: observer);
            var completed = false;
            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => Assert.Fail(
                    "a create, rename and delete before the reload must replay, not cold-scan"),
                onComplete: () => completed = true);
            synchronizer.FlushPendingSnapshotSave();
            Assert.That(completed, Is.True);
            Assert.That(source.EnumerationCount, Is.Zero);
            Assert.That(index.TryGetByPath(typed.AssetPath, out _), Is.False,
                "the deleted destination must not come back as a ghost row");
            Assert.That(index.TryGetByPath(defaultPath, out _), Is.False,
                "nor the default name it was created under");
            Assert.That(index.Count, Is.Zero);
        }

        // F2/F3: an interrupted job must cost nothing but a repeat. The job is only a
        // real mid-flight state under the slow source - with a fast one the whole
        // candidate set is consumed inside the first synchronous Poll and every
        // assertion below would hold for the wrong reason.
        [UnityTest]
        public IEnumerator InterruptedTypeReconciliation_LeavesASnapshotThatReRunsTheJobOnTheNextLoad()
        {
            UseStableCaptureOverrides();
            const int ambiguousCount = 7;
            var scriptRecord = new AssetRecord(
                "interrupted-script-guid",
                "Assets/InterruptedChanged.cs",
                "InterruptedChanged",
                "MonoScript");
            var stale = new List<AssetRecord> { scriptRecord };
            var current = new List<AssetRecord> { scriptRecord };
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"interrupted-ambiguous-{i}",
                    $"Assets/InterruptedAmbiguous{i}.asset",
                    $"InterruptedAmbiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"interrupted-ambiguous-{i}",
                    $"Assets/InterruptedAmbiguous{i}.asset",
                    $"InterruptedAmbiguous{i}",
                    "CurrentType"));
            }

            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new SlowTargetedRecordSource(current, buildDelayMilliseconds: 3);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var slices = new List<int>();
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            try
            {
                synchronizer.SynchronizeAsync(onFullScanStarted: null, onComplete: null);

                Assert.That(slices.Sum(), Is.LessThan(ambiguousCount),
                    "the budget must stop the job mid-set, or 'interrupted' is not a state");
                Assert.That(synchronizer.HasBackgroundJob, Is.True);
                var slicesAtInterrupt = slices.Sum();

                // The interruption: a newer operation. It hydrates the same snapshot -
                // the old revision is still on disk and the journal is still there - so
                // it is also the "next load" half of this test.
                var secondFullScanStarted = false;
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: () => secondFullScanStarted = true,
                    onComplete: null);

                Assert.That(
                    PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out var saved,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                    Is.True,
                    "(a) the interrupted job committed no snapshot, so the one it loaded "
                    + "is still the one on disk");
                Assert.That(artifactRevisionChanged, Is.True,
                    "(b) carrying the OLD artifact revision");
                Assert.That(
                    saved.Single(record => string.Equals(
                        record.AssetPath,
                        "Assets/InterruptedAmbiguous0.asset",
                        StringComparison.Ordinal)).TypeName,
                    Is.EqualTo("StaleType"),
                    "and the pre-compile types it was saved with");
                Assert.That(
                    PersistentAssetIndexCache.CapturePendingChanges(),
                    Is.Not.Empty,
                    "(c) the pending-change journal is retired only by a committed save");
                Assert.That(secondFullScanStarted, Is.False,
                    "(d) the next load hydrates - it does not pay a cold scan for the "
                    + "job that was killed");
                Assert.That(source.EnumerationCount, Is.Zero);
                Assert.That(synchronizer.HasBackgroundJob, Is.True,
                    "and it starts the job again");

                for (var frame = 0;
                    frame < 1000 && slices.Sum() <= slicesAtInterrupt;
                    frame++)
                {
                    yield return null;
                }

                Assert.That(slices.Sum(), Is.GreaterThan(slicesAtInterrupt),
                    "measured, not inferred: the replacement job really rebuilds records");
            }
            finally
            {
                synchronizer.Dispose();
            }
        }

        // I7: an explicit Refresh mid-job cancels the job and takes the ordinary full
        // path. The synchronizer can do that only because T5.3 nulled _activeOperation at
        // hydration - an adopted operation would have returned the retype tail's result
        // instead of scanning.
        [UnityTest]
        public IEnumerator RefreshDuringTypeReconciliation_CancelsTheJobAndTakesTheFullPath()
        {
            UseStableCaptureOverrides();
            const int ambiguousCount = 7;
            var scriptRecord = new AssetRecord(
                "refresh-script-guid",
                "Assets/RefreshChanged.cs",
                "RefreshChanged",
                "MonoScript");
            var stale = new List<AssetRecord> { scriptRecord };
            var current = new List<AssetRecord> { scriptRecord };
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"refresh-ambiguous-{i}",
                    $"Assets/RefreshAmbiguous{i}.asset",
                    $"RefreshAmbiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"refresh-ambiguous-{i}",
                    $"Assets/RefreshAmbiguous{i}.asset",
                    $"RefreshAmbiguous{i}",
                    "CurrentType"));
            }

            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new SlowTargetedRecordSource(current, buildDelayMilliseconds: 3);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var slices = new List<int>();
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            try
            {
                synchronizer.SynchronizeAsync(onFullScanStarted: null, onComplete: null);

                Assert.That(slices.Sum(), Is.LessThan(ambiguousCount),
                    "the budget must stop the job mid-set, or there is no job to cancel");
                Assert.That(synchronizer.HasBackgroundJob, Is.True);
                var slicesAtRefresh = slices.Sum();
                var refreshed = false;

                var counts = MeasurePerfCounters(
                    () => synchronizer.Synchronize(() => refreshed = true));

                Assert.That(refreshed, Is.True);
                Assert.That(synchronizer.HasBackgroundJob, Is.False,
                    "the new operation cancels the outstanding job synchronously");
                Assert.That(source.EnumerationCount, Is.GreaterThan(0),
                    "and an explicit Refresh is a real full scan, not a second hydration");
                Assert.That(
                    counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                    Is.GreaterThan(0),
                    "the walk counter moves on the path that is supposed to walk");
                Assert.That(
                    index.TryGetByPath("Assets/RefreshAmbiguous0.asset", out var scanned),
                    Is.True);
                Assert.That(scanned.TypeName, Is.EqualTo("CurrentType"),
                    "the scan published the source's catalog");

                for (var frame = 0; frame < 10; frame++)
                {
                    yield return null;
                    Assert.That(slices.Sum(), Is.EqualTo(slicesAtRefresh),
                        "the cancelled job rebuilds nothing further");
                    Assert.That(synchronizer.HasBackgroundJob, Is.False,
                        "and its later tick must not resurrect the flag");
                }
            }
            finally
            {
                synchronizer.Dispose();
            }
        }

        // The follow-up retype job's outstanding-ness, transition by transition. The
        // window HasBackgroundJob covers deliberately outlasts the last rebuilt slice:
        // the job is not over until the snapshot that promotes the new artifact revision
        // has resolved, because a synchronization started inside that window cancels the
        // save and costs the next reload a cold scan.
        [Test]
        public void TypeReconciliation_SaveWindow_StillReportsReconcilingUntilTheSaveResolves()
        {
            UseStableCaptureOverrides();
            var scriptRecord = new AssetRecord(
                "save-window-script-guid",
                "Assets/SaveWindowChanged.cs",
                "SaveWindowChanged",
                "MonoScript");
            var stale = new[]
            {
                scriptRecord,
                new AssetRecord(
                    "save-window-guid",
                    "Assets/SaveWindow.asset",
                    "SaveWindow",
                    "StaleType"),
            };
            var current = new[]
            {
                scriptRecord,
                new AssetRecord(
                    "save-window-guid",
                    "Assets/SaveWindow.asset",
                    "SaveWindow",
                    "CurrentType"),
            };
            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new TargetedRecordSource(current);
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            Assert.That(fullScanStarted, Is.False,
                "a compile-only reload must not present a cold-build overlay");
            Assert.That(completed, Is.True,
                "the operation completes at hydration, before the retype job runs");
            Assert.That(
                index.TryGetByPath("Assets/SaveWindow.asset", out var patched),
                Is.True);
            Assert.That(patched.TypeName, Is.EqualTo("CurrentType"),
                "the job patched the record in the caller's own frame");
            Assert.That(synchronizer.HasBackgroundJob, Is.True,
                "the job has settled and staged its save; the save has not resolved");

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(synchronizer.HasBackgroundJob, Is.False,
                "the job is over only once its snapshot save has resolved");
            Assert.That(
                File.Exists(PersistentAssetIndexCache.DirtyMarkerPath),
                Is.False,
                "a committed save marks nothing dirty");
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out var saved,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.False,
                "the job's save is what promotes the new artifact revision");
            Assert.That(
                saved.Single(record => string.Equals(
                    record.AssetPath,
                    "Assets/SaveWindow.asset",
                    StringComparison.Ordinal)).TypeName,
                Is.EqualTo("CurrentType"),
                "and it commits the patched types, not the ones it loaded");
        }

        // The compile-only reload: an empty journal and a moved artifact revision. The
        // job has nothing to rebuild, but promoting the revision IS its save's job, so it
        // must not look finished before that save resolves - a zero-candidate job that
        // skipped its save would re-run on every reload for ever.
        [Test]
        public void TypeReconciliation_ZeroCandidates_ReportsReconcilingUntilTheSaveResolves()
        {
            UseStableCaptureOverrides();
            var cached = new AssetRecord(
                "zero-candidate-guid",
                "Assets/ZeroCandidate.cs",
                "ZeroCandidate",
                "MonoScript");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { cached },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new ThrowIfEnumeratedSource();
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            Assert.That(fullScanStarted, Is.False,
                "an empty journal with a moved revision is a compile, not a structural miss");
            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(cached.AssetPath, out _), Is.True,
                "the hydrated snapshot is the live index");
            Assert.That(synchronizer.HasBackgroundJob, Is.True,
                "a zero-candidate job still owns the save that promotes the revision");

            synchronizer.FlushPendingSnapshotSave();

            Assert.That(synchronizer.HasBackgroundJob, Is.False);
            Assert.That(
                File.Exists(PersistentAssetIndexCache.DirtyMarkerPath),
                Is.False);
            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                43u,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out _,
                out _,
                out var artifactRevisionChanged,
                out _,
                out _),
                Is.True);
            Assert.That(artifactRevisionChanged, Is.False,
                "the zero-candidate job saves, so the next reload sees no revision change");
        }

        // A newer operation cancels an outstanding job and clears the flag synchronously,
        // in BeginOperation. It must be a real mid-job state: with a fast source the whole
        // candidate set is consumed inside the first synchronous Poll, and the assertion
        // would pass because the job had already ended.
        [UnityTest]
        public IEnumerator NewOperationDuringTypeReconciliation_StopsReportingReconciling()
        {
            UseStableCaptureOverrides();
            const int ambiguousCount = 7;
            var scriptRecord = new AssetRecord(
                "cancel-script-guid",
                "Assets/CancelChanged.cs",
                "CancelChanged",
                "MonoScript");
            var stale = new List<AssetRecord> { scriptRecord };
            var current = new List<AssetRecord> { scriptRecord };
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"cancel-ambiguous-{i}",
                    $"Assets/CancelAmbiguous{i}.asset",
                    $"CancelAmbiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"cancel-ambiguous-{i}",
                    $"Assets/CancelAmbiguous{i}.asset",
                    $"CancelAmbiguous{i}",
                    "CurrentType"));
            }

            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new SlowTargetedRecordSource(current, buildDelayMilliseconds: 3);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var slices = new List<int>();
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: null);

                Assert.That(slices.Sum(), Is.LessThan(ambiguousCount),
                    "the budget must stop the job mid-set, or 'mid-job' is not a state");
                Assert.That(synchronizer.HasBackgroundJob, Is.True);

                // A second synchronization against the snapshot's OWN revision: it
                // hydrates, finds nothing to retype and starts no job, so what the
                // assertion below sees is BeginOperation's clear rather than a
                // replacement job re-setting the field.
                PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 42;
                var slicesAtCancel = slices.Sum();
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: null);

                Assert.That(synchronizer.HasBackgroundJob, Is.False,
                    "a newer operation cancels the outstanding job synchronously");

                for (var frame = 0; frame < 10; frame++)
                {
                    yield return null;
                    Assert.That(synchronizer.HasBackgroundJob, Is.False,
                        "the cancelled job's later tick must not resurrect the flag");
                }

                Assert.That(slices.Sum(), Is.EqualTo(slicesAtCancel),
                    "and it rebuilds nothing further");
            }
            finally
            {
                synchronizer.Dispose();
            }
        }

        // A synchronization started inside the job's save window makes that save lose
        // its canCommit race. That used to mark the cache dirty from inside
        // TryCommitSnapshotSave, and was one of the ways a script reload poisoned the
        // NEXT reload into a cold scan. The race is benign: the refused commit touched
        // nothing, the committed snapshot plus the unretired journal are still a
        // complete account of the index, and the next load hydrates them. The guard in
        // ZepController.RequestBackgroundReconciliation still exists to avoid
        // the wasted job, not to avoid a poisoned cache.
        [UnityTest]
        public IEnumerator NewOperationDuringTheJobsSaveWindow_LeavesTheCacheTrusted()
        {
            UseStableCaptureOverrides();
            const int ambiguousCount = 7;
            var scriptRecord = new AssetRecord(
                "save-race-script-guid",
                "Assets/SaveRaceChanged.cs",
                "SaveRaceChanged",
                "MonoScript");
            var stale = new List<AssetRecord> { scriptRecord };
            var current = new List<AssetRecord> { scriptRecord };
            for (var i = 0; i < ambiguousCount; i++)
            {
                stale.Add(new AssetRecord(
                    $"save-race-ambiguous-{i}",
                    $"Assets/SaveRaceAmbiguous{i}.asset",
                    $"SaveRaceAmbiguous{i}",
                    "StaleType"));
                current.Add(new AssetRecord(
                    $"save-race-ambiguous-{i}",
                    $"Assets/SaveRaceAmbiguous{i}.asset",
                    $"SaveRaceAmbiguous{i}",
                    "CurrentType"));
            }

            Assert.That(
                PersistentAssetIndexCache.Save(
                    stale,
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { scriptRecord.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 43;
            var source = new SlowTargetedRecordSource(current, buildDelayMilliseconds: 3);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var slices = new List<int>();
            synchronizer.ScriptTypeReconciliationSliceObserverForTests =
                slice => slices.Add(slice);
            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: null);

                // Run out the budgeted slices and stop on the frame the last one lands:
                // the job settles and stages its save in that frame, and the save's own
                // poll has not ticked yet.
                for (var frame = 0;
                    frame < 1000 && slices.Sum() < ambiguousCount;
                    frame++)
                {
                    yield return null;
                }

                Assert.That(slices.Sum(), Is.EqualTo(ambiguousCount));
                Assert.That(synchronizer.HasBackgroundJob, Is.True,
                    "the job has settled and its save is in flight");

                PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 42;
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: null);

                // The newer operation's own snapshot save is also refused while the job
                // holds the journal write marker; it, too, must leave no marker. Deleting
                // any marker here keeps the assertion below about the job's save alone.
                if (File.Exists(PersistentAssetIndexCache.DirtyMarkerPath))
                {
                    File.Delete(PersistentAssetIndexCache.DirtyMarkerPath);
                }

                synchronizer.FlushPendingSnapshotSave();

                Assert.That(
                    File.Exists(PersistentAssetIndexCache.DirtyMarkerPath),
                    Is.False,
                    "a save that loses its canCommit race touched nothing on disk and "
                    + "marks nothing; distrusting the cache here is what cost the next "
                    + "reload a cold scan");
                Assert.That(synchronizer.HasBackgroundJob, Is.False,
                    "and the job stops being outstanding on that outcome too");
            }
            finally
            {
                synchronizer.Dispose();
            }
        }

        [Test]
        public void MarkDirty_FromManyThreads_StagesThroughDistinctTempFiles()
        {
            const int writerCount = 32;
            Assert.That(PersistentAssetIndexCache.MarkDirty(), Is.True,
                "pre-create the marker so every racing write takes the replace leg");
            var stagedPaths = new List<string>();
            var gate = new object();
            PersistentAssetIndexCache.DirtyMarkerCommitOverride = (temp, target) =>
            {
                lock (gate)
                {
                    stagedPaths.Add(temp);
                }

                File.Replace(temp, target, null);
            };
            try
            {
                var writers = new Task[writerCount];
                for (var i = 0; i < writerCount; i++)
                {
                    writers[i] = Task.Run(() => PersistentAssetIndexCache.MarkDirty());
                }

                Task.WaitAll(writers, 30_000);
            }
            finally
            {
                PersistentAssetIndexCache.DirtyMarkerCommitOverride = null;
            }

            Assert.That(stagedPaths, Has.Count.EqualTo(writerCount));
            Assert.That(
                stagedPaths,
                Is.Unique,
                "two concurrent MarkDirty calls must not stage through one temp file: "
                + "sharing it lets one writer commit the other's token");
            Assert.That(File.Exists(PersistentAssetIndexCache.DirtyMarkerPath), Is.True);
            Assert.That(
                Directory.GetFiles(
                    PersistentAssetIndexCache.CacheDirectory,
                    Path.GetFileName(PersistentAssetIndexCache.DirtyMarkerPath)
                        + "*.tmp"),
                Is.Empty,
                "every staged file is either committed or removed by its own writer");
        }

        [Test]
        public void SynchronizeAsync_PrewarmDelta_ReplaysOverCacheWithoutColdScanOverlay()
        {
            UseStableCaptureOverrides();
            var cachedRecord = new AssetRecord(
                "cached-guid",
                "Assets/Cached.png",
                "Cached",
                "Texture2D");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { cachedRecord },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            var importedRecord = new AssetRecord(
                "imported-guid",
                "Assets/Imported.png",
                "Imported",
                "Texture2D");
            var index = new InMemoryAssetIndex();
            index.Upsert(importedRecord);
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            synchronizer.InvalidateInProgressScan();
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                new[] { importedRecord.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var fullScanStarted = false;
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => fullScanStarted = true,
                onComplete: () => completed = true);

            Assert.That(fullScanStarted, Is.False);
            Assert.That(completed, Is.True);
            Assert.That(index.TryGetByPath(cachedRecord.AssetPath, out _), Is.True,
                "the saved catalog must replace the partial prewarm index");
            Assert.That(index.TryGetByPath(importedRecord.AssetPath, out _), Is.True,
                "the prewarm delta must be replayed over the saved catalog");
        }

        [Test]
        public void SynchronizeAsync_CacheMiss_NotifiesBeforeEnumeration()
        {
            var events = new List<string>();
            var source = new RecordingSource(events);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => events.Add("scan-started"),
                onComplete: () => events.Add("complete"));

            Assert.That(events[0], Is.EqualTo("scan-started"),
                "the UI must be notified before any cold-scan work begins");
            Assert.That(events, Does.Contain("enumerate"));
            Assert.That(events[events.Count - 1], Is.EqualTo("complete"));
            Assert.That(index.TryGetByPath("Assets/Scanned.asset", out _), Is.True);
        }

        [Test]
        public void SynchronizeAsync_MissingAuthoritativeTypesFallsBackWithoutPublishingGuesses()
        {
            UseStableCaptureOverrides();
            PersistentAssetIndexCache.Invalidate();
            PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = () => null;
            var syncSource = new RecordingSource(new List<string>());
            var asyncSource = new TypeMetadataRequiredSource();
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                syncSource,
                asyncSource,
                index,
                enablePersistentCache: true);
            var completed = false;
            LogAssert.Expect(
                LogType.Warning,
                "[Zep] PrepareForEnumeration threw (InvalidOperationException: missing type metadata); falling back to sync.");

            synchronizer.SynchronizeAsync(
                onFullScanStarted: null,
                onComplete: () => completed = true);

            Assert.That(completed, Is.True);
            Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.SyncFallback));
            Assert.That(index.TryGetByPath("Assets/Scanned.asset", out _), Is.True,
                "the authoritative main-thread source must replace the unsafe background guess");
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ProjectionUsesFoldersFromTheEnumeratedAsyncSource()
        {
            var syncSource = new FolderTrackingSource(
                isThreadSafe: false,
                "sync-guid",
                "Assets/Sync.asset",
                new AssetFolderRecord(
                    AssetPath.From("Assets/SyncOnly"),
                    "sync-folder-guid"));
            var asyncSource = new FolderTrackingSource(
                isThreadSafe: true,
                "async-guid",
                "Packages/com.example.async/Editor/Async.cs",
                new AssetFolderRecord(
                    AssetPath.From("Packages/com.example.async"),
                    "async-package-guid"));
            var synchronizer = new AssetIndexSynchronizer(
                syncSource,
                asyncSource,
                new InMemoryAssetIndex(),
                enablePersistentCache: false);
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: null,
                onComplete: () => completed = true);

            for (var frame = 0; frame < 100 && !completed; frame++)
            {
                yield return null;
            }

            Assert.That(completed, Is.True, "the worker scan did not complete within 100 editor frames");
            Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async));
            var folders = synchronizer.GetFolderRecordsForProjection();
            Assert.That(folders.Count, Is.EqualTo(1));
            Assert.That(
                folders[0].FolderPath.Value,
                Is.EqualTo("Packages/com.example.async"),
                "virtual package roots must come from the source that enumerated their assets");
            Assert.That(folders[0].Guid, Is.EqualTo("async-package-guid"));
            Assert.That(asyncSource.GetFolderRecordsCallCount, Is.EqualTo(1));
            Assert.That(syncSource.GetFolderRecordsCallCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_WorkerFaultDiscardsPartialSnapshotAndFallsBackToSync()
        {
            var authoritative = new AssetRecord(
                "authoritative-guid",
                "Assets/Authoritative.asset",
                "Authoritative",
                "TestAsset");
            var partial = new AssetRecord(
                "partial-guid",
                "Assets/Partial.asset",
                "Partial",
                "TestAsset");
            var stale = new AssetRecord(
                "stale-guid",
                "Assets/Stale.asset",
                "Stale",
                "TestAsset");
            var index = new InMemoryAssetIndex();
            index.Upsert(stale);
            using var synchronizer = new AssetIndexSynchronizer(
                new MutableSnapshotSource(new[] { authoritative }),
                new PartiallyEnumeratingFaultSource(partial),
                index,
                enablePersistentCache: false);
            var completed = false;
            var adoptedRefreshCompletions = 0;
            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Async synchronize failed (IOException: transient worker read); falling back to sync.");

            synchronizer.SynchronizeAsync(
                onFullScanStarted: null,
                onComplete: () => completed = true);
            synchronizer.Synchronize(() => adoptedRefreshCompletions++);

            var completionWait = System.Diagnostics.Stopwatch.StartNew();
            while (!completed && completionWait.Elapsed < TimeSpan.FromSeconds(30))
            {
                yield return null;
            }

            Assert.That(completed, Is.True,
                $"the worker-fault fallback did not complete within the deadline; elapsed={completionWait.Elapsed}");
            Assert.That(adoptedRefreshCompletions, Is.EqualTo(1),
                "a refresh adopted before the worker fault must complete with the authoritative fallback");
            Assert.That(
                synchronizer.SynchronizationOperationGenerationForTests,
                Is.EqualTo(1),
                "worker recovery must remain inside the adopted operation");
            Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.SyncFallback));
            Assert.That(index.Contains(authoritative.Guid), Is.True);
            Assert.That(index.Contains(partial.Guid), Is.False,
                "records yielded before the worker fault must never be published");
            Assert.That(index.Contains(stale.Guid), Is.False,
                "the complete sync fallback remains authoritative after the worker fault");
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ImportDuringWorkerScanCannotBeRemovedByStalePublication()
        {
            var original = new AssetRecord(
                "original-guid",
                "Assets/Original.asset",
                "Original",
                "TestAsset");
            var imported = new AssetRecord(
                "imported-guid",
                "Assets/Imported.asset",
                "Imported",
                "TestAsset");
            var syncSource = new MutableSnapshotSource(new[] { original });
            var asyncSource = new BlockingSnapshotSource(new[] { original });
            var index = new InMemoryAssetIndex();
            index.Upsert(original);
            var synchronizer = new AssetIndexSynchronizer(
                syncSource,
                asyncSource,
                index,
                enablePersistentCache: false);
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: () => completed = true);

                for (var frame = 0; frame < 100 && !asyncSource.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(asyncSource.EnumerationStarted.IsSet, Is.True,
                    "the worker did not enter the blocking source within 100 editor frames");

                // Simulate the AssetPostprocessor fast path while the worker still
                // owns its older point-in-time snapshot.
                syncSource.SetRecords(new[] { original, imported });
                index.Upsert(imported);
                synchronizer.InvalidateInProgressScan();
                ((IAssetScanChangeRecorder)synchronizer).RecordConcreteChanges(
                    new ProjectAssetChanges(
                        new[] { imported.AssetPath },
                        System.Array.Empty<string>(),
                        System.Array.Empty<string>(),
                        System.Array.Empty<string>()));
                asyncSource.ReleaseEnumeration();

                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True, "the invalidated scan did not complete within 100 editor frames");
                Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async));
                Assert.That(index.TryGetByPath(imported.AssetPath, out var current), Is.True,
                    "stale worker publication must not delete the newer imported asset");
                Assert.That(current.Guid, Is.EqualTo(imported.Guid));
                Assert.That(
                    synchronizer.SynchronizationOperationGenerationForTests,
                    Is.EqualTo(1),
                    "a stale-worker retry must stay inside its original operation");
            }
            finally
            {
                asyncSource.ReleaseEnumeration();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_UnresolvedImportDoesNotRemoveLaterAuthoritativeScanRecord()
        {
            UseStableCaptureOverrides();
            PersistentAssetIndexCache.Invalidate();
            var imported = new AssetRecord(
                "resolved-after-callback-guid",
                "Assets/ResolvedAfterCallback.asset",
                "ResolvedAfterCallback",
                "TestAsset");
            var source = new BlockingSnapshotSource(new[] { imported });
            var index = new InMemoryAssetIndex();
            using var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            synchronizer.RecordConcreteChanges(
                new ProjectAssetChanges(
                    new[] { imported.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: () => completed = true);

                var entryWait = System.Diagnostics.Stopwatch.StartNew();
                while (!source.EnumerationStarted.IsSet && entryWait.Elapsed < TimeSpan.FromSeconds(10))
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True,
                    $"the worker did not enter the source within the deadline; elapsed={entryWait.Elapsed}");
                source.ReleaseEnumeration();
                var completionWait = System.Diagnostics.Stopwatch.StartNew();
                while (!completed && completionWait.Elapsed < TimeSpan.FromSeconds(30))
                {
                    yield return null;
                }

                Assert.That(completed, Is.True,
                    $"the authoritative scan did not complete within the deadline; elapsed={completionWait.Elapsed}");
                Assert.That(index.TryGetByPath(imported.AssetPath, out var resolved), Is.True,
                    "an unresolved import callback must not become a deletion overlay");
                Assert.That(resolved.Guid, Is.EqualTo(imported.Guid));
                Assert.That(PersistentAssetIndexCache.TryLoad(out var saved, out _), Is.True);
                Assert.That(saved.Select(record => record.Guid), Does.Contain(imported.Guid),
                    "the authoritative record must not be persisted absent");
            }
            finally
            {
                source.ReleaseEnumeration();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_LaterParentDeletionSupersedesEarlierImportOverlay()
        {
            var imported = new AssetRecord(
                "deleted-child-guid",
                "Assets/DeletedFolder/Child.asset",
                "Child",
                "TestAsset");
            var source = new BlockingSnapshotSource(new[] { imported });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            synchronizer.RecordConcreteChanges(
                new ProjectAssetChanges(
                    new[] { imported.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            synchronizer.RecordConcreteChanges(
                new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { "Assets/DeletedFolder" },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: () => completed = true);
                for (var frame = 0; frame < 100 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                source.ReleaseEnumeration();
                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True);
                Assert.That(index.TryGetByPath(imported.AssetPath, out _), Is.False,
                    "a later parent deletion must clear earlier child import state");
            }
            finally
            {
                source.ReleaseEnumeration();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ParentDeletionThenUnresolvedChildImport_PreservesScanTopology()
        {
            const string parentPath = "Assets/RecreatedFolder";
            var child = new AssetRecord(
                "recreated-child-guid",
                parentPath + "/Child.asset",
                "Child",
                "TestAsset");
            var parent = new AssetFolderRecord(AssetPath.From(parentPath));
            var source = new BlockingSnapshotSource(new[] { child }, new[] { parent });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { parentPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                new[] { child.AssetPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(null, () => completed = true);
                for (var frame = 0; frame < 100 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                source.ReleaseEnumeration();
                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True);
                Assert.That(index.TryGetFolder(parentPath, out _), Is.True,
                    "the expected child must keep its authoritative parent reachable");
                Assert.That(index.TryGetByPath(child.AssetPath, out _), Is.True);
            }
            finally
            {
                source.ReleaseEnumeration();
            }
        }

        [Test]
        public void CacheHydration_DeleteThenRecreateEmptyFolder_DropsStaleDescendants()
        {
            UseStableCaptureOverrides();
            const string folderPath = "Assets/RecreatedEmpty";
            var stale = new AssetRecord(
                "recreated-empty-stale-guid",
                folderPath + "/Stale.asset",
                "Stale",
                "TestAsset");
            var folder = new AssetFolderRecord(AssetPath.From(folderPath));
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { stale },
                new[] { folder }), Is.True);
            var index = new InMemoryAssetIndex();
            index.Replace(
                Array.Empty<AssetRecord>(),
                new[] { folder });
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                new[] { folderPath },
                new[] { folderPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("the valid cache should hydrate without enumeration"),
                () => completed = true);

            Assert.That(completed, Is.True);
            Assert.That(index.TryGetFolder(folderPath, out _), Is.True,
                "the concrete recreation restores the folder itself");
            Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False,
                "a stale cache row is not evidence that the recreated folder still contains it");
        }

        [Test]
        public void CacheHydration_RecreatedFolder_PreservesOnlyConcreteCurrentDescendant()
        {
            UseStableCaptureOverrides();
            const string folderPath = "Assets/RecreatedWithCurrent";
            var stale = new AssetRecord(
                "recreated-sibling-stale-guid",
                folderPath + "/Stale.asset",
                "Stale",
                "TestAsset");
            var current = new AssetRecord(
                "recreated-current-guid",
                folderPath + "/Current.asset",
                "Current",
                "TestAsset");
            var folder = new AssetFolderRecord(AssetPath.From(folderPath));
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { stale },
                new[] { folder }), Is.True);
            var index = new InMemoryAssetIndex();
            index.Replace(
                new[] { current },
                new[] { folder });
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                new[] { current.AssetPath },
                new[] { folderPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            var completed = false;

            synchronizer.SynchronizeAsync(
                () => Assert.Fail("the valid cache should hydrate without enumeration"),
                () => completed = true);

            Assert.That(completed, Is.True);
            Assert.That(index.TryGetFolder(folderPath, out _), Is.True);
            Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True,
                "a concrete current child restores itself and its parent chain");
            Assert.That(index.TryGetByPath(stale.AssetPath, out _), Is.False,
                "the current child must not protect stale siblings from the old cache");
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ParentDeletionThenUnresolvedMovedChild_PreservesScanTopology()
        {
            const string parentPath = "Assets/MovedFolder";
            var child = new AssetRecord(
                "moved-child-guid",
                parentPath + "/Child.asset",
                "Child",
                "TestAsset");
            var parent = new AssetFolderRecord(AssetPath.From(parentPath));
            var source = new BlockingSnapshotSource(new[] { child }, new[] { parent });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { parentPath },
                Array.Empty<string>(),
                Array.Empty<string>()));
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { child.AssetPath },
                new[] { "Assets/OldFolder/Child.asset" }));
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(null, () => completed = true);
                for (var frame = 0; frame < 100 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                source.ReleaseEnumeration();
                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True);
                Assert.That(index.TryGetFolder(parentPath, out _), Is.True,
                    "the unresolved move target must keep its authoritative parent reachable");
                Assert.That(index.TryGetByPath(child.AssetPath, out _), Is.True);
            }
            finally
            {
                source.ReleaseEnumeration();
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ParentDeletionThenResolvedChildImport_PreservesOnlyRequiredTopology()
        {
            return AssertResolvedOverlayTopology(
                currentIsFolder: false,
                recordAsMove: false);
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ParentDeletionThenResolvedFolderImport_PreservesAuthoritativeSubtree()
        {
            return AssertResolvedOverlayTopology(
                currentIsFolder: true,
                recordAsMove: false);
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_ParentDeletionThenResolvedMoveTarget_PreservesOnlyRequiredTopology()
        {
            return AssertResolvedOverlayTopology(
                currentIsFolder: false,
                recordAsMove: true);
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_DeleteDuringWorkerScanCannotBeReintroducedByStalePublication()
        {
            var survivor = new AssetRecord(
                "survivor-guid",
                "Assets/Survivor.asset",
                "Survivor",
                "TestAsset");
            var deleted = new AssetRecord(
                "deleted-guid",
                "Assets/Deleted.asset",
                "Deleted",
                "TestAsset");
            var syncSource = new MutableSnapshotSource(new[] { survivor, deleted });
            var asyncSource = new BlockingSnapshotSource(new[] { survivor, deleted });
            var index = new InMemoryAssetIndex();
            index.Upsert(survivor);
            index.Upsert(deleted);
            var synchronizer = new AssetIndexSynchronizer(
                syncSource,
                asyncSource,
                index,
                enablePersistentCache: false);
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(
                    onFullScanStarted: null,
                    onComplete: () => completed = true);

                for (var frame = 0; frame < 100 && !asyncSource.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(asyncSource.EnumerationStarted.IsSet, Is.True,
                    "the worker did not enter the blocking source within 100 editor frames");

                syncSource.SetRecords(new[] { survivor });
                index.Remove(deleted.Guid);
                synchronizer.InvalidateInProgressScan();
                ((IAssetScanChangeRecorder)synchronizer).RecordConcreteChanges(
                    new ProjectAssetChanges(
                        System.Array.Empty<string>(),
                        new[] { deleted.AssetPath },
                        System.Array.Empty<string>(),
                        System.Array.Empty<string>()));
                asyncSource.ReleaseEnumeration();

                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True, "the invalidated scan did not complete within 100 editor frames");
                Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async));
                Assert.That(index.Contains(deleted.Guid), Is.False,
                    "stale worker publication must not resurrect an asset deleted during the scan");
                Assert.That(index.TryGetByPath(survivor.AssetPath, out _), Is.True);
            }
            finally
            {
                asyncSource.ReleaseEnumeration();
            }
        }

        [Test]
        public void ConfiguredRecordSources_DisablingNativeSourceAlsoDisablesTheColdAsyncSource()
        {
            var disabled = ZepControllerHost.CreateConfiguredRecordSources(
                useNativeFileSystemAssetSource: false);
            var enabled = ZepControllerHost.CreateConfiguredRecordSources(
                useNativeFileSystemAssetSource: true);

            Assert.That(disabled.SyncSource, Is.TypeOf<AssetDatabaseAssetRecordSource>());
            Assert.That(disabled.AsyncSource, Is.SameAs(disabled.SyncSource),
                "the native-source kill switch must cover first-run cache misses");
            Assert.That(enabled.SyncSource, Is.TypeOf<HybridAssetRecordSource>());
            Assert.That(enabled.AsyncSource, Is.TypeOf<NativeFileSystemAssetRecordSource>());
        }

        [Test]
        public void Synchronize_CacheMissCapturesEachMetadataSnapshotOnceAndReusesItForTheSource()
        {
            UseStableCaptureOverrides();
            PersistentAssetIndexCache.Invalidate();
            IReadOnlyDictionary<string, IReadOnlyList<string>> labels =
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["label-guid"] = new[] { "Gameplay" },
                };
            IReadOnlyDictionary<string, AssetTypeMetadata> types =
                new Dictionary<string, AssetTypeMetadata>
            {
                ["type-guid"] = new AssetTypeMetadata("EnemyConfig", new[] { "ScriptableObject" }),
            };
            PersistentAssetIndexCache.UnityLabelSnapshotOverride = () => labels;
            PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = () => types;
            var source = new RecordingSource(new List<string>());
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                new InMemoryAssetIndex(),
                enablePersistentCache: true);

            synchronizer.Synchronize();

            Assert.That(source.PreparedUnityLabelsByGuid, Is.SameAs(labels));
            Assert.That(source.PreparedTypesByGuid, Is.SameAs(types));
        }

        [Test]
        public void ReadGuidAndLabelsFromMetaFile_ParsesHeaderAndTrailingLabelBlocks()
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "Zep.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var headerMetaPath = Path.Combine(tempDirectory, "Header.asset.meta");
                File.WriteAllText(
                    headerMetaPath,
                    "\uFEFFfileFormatVersion: 2\n"
                    + "guid: 0123456789abcdef0123456789abcdef\n"
                    + "labels:\n"
                    + "- Gameplay\n"
                    + "- 'Needs Review'\n"
                    + "NativeFormatImporter:\n"
                    + "  userData:\n");

                var headerGuid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    headerMetaPath,
                    out var headerLabels);

                Assert.That(headerGuid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
                Assert.That(headerLabels, Is.EqualTo(new[] { "Gameplay", "Needs Review" }));
                Assert.That(
                    UnityMetaFileReader.ReadGuidFromMetaFile(headerMetaPath),
                    Is.EqualTo(headerGuid),
                    "the existing guid-only gateway must remain compatible");

                var trailingMetaPath = Path.Combine(tempDirectory, "Trailing.dll.meta");
                File.WriteAllText(
                    trailingMetaPath,
                    "fileFormatVersion: 2\n"
                    + "guid: fedcba9876543210fedcba9876543210\n"
                    + "PluginImporter:\n"
                    + "  platformData:\n"
                    + "  - first:\n"
                    + "      Any:\n"
                    + "    second:\n"
                    + "      enabled: 0\n"
                    + "labels:\n"
                    + "- RoslynAnalyzer\n"
                    + "- \"Escaped \\\"Label\\\"\"\n");

                var trailingGuid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    trailingMetaPath,
                    out var trailingLabels);

                Assert.That(trailingGuid, Is.EqualTo("fedcba9876543210fedcba9876543210"));
                Assert.That(trailingLabels, Is.EqualTo(new[] { "RoslynAnalyzer", "Escaped \"Label\"" }));
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Test]
        public void MetaReaders_MissingAndMalformedFilesRemainNormalAbsence()
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "Zep.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var missingMetaPath = Path.Combine(tempDirectory, "Missing.asset.meta");
                Assert.That(
                    UnityMetaFileReader.ReadGuidFromMetaFile(missingMetaPath),
                    Is.Empty);
                Assert.That(
                    UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                        missingMetaPath,
                        out var missingLabels),
                    Is.Empty);
                Assert.That(missingLabels, Is.Empty);

                var malformedMetaPath = Path.Combine(tempDirectory, "Malformed.asset.meta");
                File.WriteAllText(
                    malformedMetaPath,
                    "fileFormatVersion: 2\nNativeFormatImporter:\n  externalObjects: {}\n");
                Assert.That(
                    UnityMetaFileReader.ReadGuidFromMetaFile(malformedMetaPath),
                    Is.Empty);
                Assert.That(
                    UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                        malformedMetaPath,
                        out var malformedLabels),
                    Is.Empty);
                Assert.That(malformedLabels, Is.Empty);
            }
            finally
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }

        [Test]
        public void MetaCompatibilityAdapter_UnknownSerializationFailsClosed()
        {
            var metaPath = Path.Combine(
                Path.GetTempPath(),
                "__ZepUnknownMeta_" + Guid.NewGuid().ToString("N") + ".meta");
            try
            {
                File.WriteAllText(
                    metaPath,
                    "fileFormatVersion: 3\nguid: 0123456789abcdef0123456789abcdef\n");

                Assert.That(
                    UnityMetaFileReader.MetaCompatibilityAdapterVersion,
                    Is.EqualTo(1));
                Assert.That(
                    UnityMetaFileReader.ReadGuidFromMetaFile(metaPath),
                    Is.Empty);
                Assert.That(
                    UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                        metaPath,
                        out var labels),
                    Is.Empty);
                Assert.That(labels, Is.Empty);
            }
            finally
            {
                File.Delete(metaPath);
            }
        }

        [Test]
        public void NativeSource_ExistingMetaReadFailureAbortsParallelScan()
        {
            var assetPath = "Assets/__ZepLockedMeta_"
                + Guid.NewGuid().ToString("N")
                + ".txt";
            try
            {
                File.WriteAllText(assetPath, "Zep locked metadata fixture");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var expectedGuid = AssetDatabase.AssetPathToGUID(assetPath);
                var metaPath = assetPath + ".meta";
                Assert.That(File.Exists(metaPath), Is.True);

                var source = new NativeFileSystemAssetRecordSource();
                source.PrepareForEnumeration();
                using (var exclusiveLock = new FileStream(
                    metaPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None))
                {
                    var labelsReadFailure = Assert.Catch<Exception>(() =>
                        UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                            metaPath,
                            out _));
                    Assert.That(IsTransientReadFailure(labelsReadFailure), Is.True);

                    var guidReadFailure = Assert.Catch<Exception>(() =>
                        UnityMetaFileReader.ReadGuidFromMetaFile(metaPath));
                    Assert.That(IsTransientReadFailure(guidReadFailure), Is.True);

                    var nativeIncremental = source.BuildRecord(AssetPath.From(assetPath));
                    var hybridIncremental = new HybridAssetRecordSource().BuildRecord(
                        AssetPath.From(assetPath));
                    Assert.That(nativeIncremental, Is.Not.Null);
                    Assert.That(nativeIncremental.Guid, Is.EqualTo(expectedGuid));
                    Assert.That(hybridIncremental, Is.Not.Null);
                    Assert.That(hybridIncremental.Guid, Is.EqualTo(expectedGuid));

                    var scanFailure = Assert.Throws<AggregateException>(() =>
                        source.GetRecords().ToList());
                    Assert.That(
                        scanFailure.Flatten().InnerExceptions.Any(IsTransientReadFailure),
                        Is.True,
                        "the worker failure must escape instead of publishing completed workers as a full snapshot");
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void DirectFolderSource_EnumeratesOneLevelWithoutMetaOrNestedLeakage()
        {
            var folderPath = "Assets/__ZepDirect_" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(folderPath);
                Directory.CreateDirectory(folderPath + "/Sub");
                File.WriteAllText(folderPath + "/Visible.txt", "visible");
                File.WriteAllText(folderPath + "/Visible.txt.meta", "not an asset row");
                File.WriteAllText(folderPath + "/Sub/Nested.txt", "nested");

                var observation = new NativeDirectFolderAssetSource().Observe(
                    AssetPath.From(folderPath));

                Assert.That(observation.IsComplete, Is.True);
                Assert.That(
                    observation.Entries.Select(entry => entry.Path.Value),
                    Is.EquivalentTo(new[]
                    {
                        folderPath + "/Sub",
                        folderPath + "/Visible.txt",
                    }));
                Assert.That(
                    observation.Entries.Single(entry => !entry.IsFolder).InferredTypeName,
                    Is.EqualTo("TextAsset"));
            }
            finally
            {
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, recursive: true);
                }
            }
        }

        [Test]
        public void NativeSource_ExistingFolderMetaReadFailureAbortsSnapshot()
        {
            var folderName = "__ZepLockedFolderMeta_" + Guid.NewGuid().ToString("N");
            var folderPath = "Assets/" + folderName;
            var expectedGuid = AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var source = new NativeFileSystemAssetRecordSource();
                source.PrepareForEnumeration();
                using (var exclusiveLock = new FileStream(
                    folderPath + ".meta",
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None))
                {
                    var incremental = source.BuildFolderRecord(AssetPath.From(folderPath));
                    Assert.That(incremental, Is.Not.Null);
                    Assert.That(incremental.Guid, Is.EqualTo(expectedGuid),
                        "the main-thread incremental path must fall back to AssetDatabase");

                    var scanFailure = Assert.Catch<Exception>(() =>
                        source.GetRecords().ToList());
                    Assert.That(IsTransientReadFailure(scanFailure), Is.True,
                        "folder metadata is part of the authoritative native snapshot and cannot be defaulted");
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        [Test]
        public void ResolvePhysicalAssetPath_MapsOnlyTheExactRegisteredPackagePrefix()
        {
            var packageRoots = new[]
            {
                (VirtualPrefix: "Packages/com.example.tools", PhysicalRoot: "/cache/com.example.tools@1.2.3"),
            };

            Assert.That(
                HybridAssetRecordSource.ResolvePhysicalAssetPath(
                    "Packages/com.example.tools/Editor/Tool.cs",
                    packageRoots),
                Is.EqualTo("/cache/com.example.tools@1.2.3/Editor/Tool.cs"));
            Assert.That(
                HybridAssetRecordSource.ResolvePhysicalAssetPath(
                    "Packages/com.example.tools-extra/Editor/Tool.cs",
                    packageRoots),
                Is.EqualTo("Packages/com.example.tools-extra/Editor/Tool.cs"));
            Assert.That(
                HybridAssetRecordSource.ResolvePhysicalAssetPath("Assets/Tool.cs", packageRoots),
                Is.EqualTo("Assets/Tool.cs"));
        }

        [Test]
        public void UnityArtifactDependencyRevision_ChangesWhenMetaGuidIsReplacedInPlace()
        {
            var assetPath = "Assets/__ZepGuidRevision_"
                + Guid.NewGuid().ToString("N")
                + ".txt";
            try
            {
                File.WriteAllText(assetPath, "Zep GUID revision fixture");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var oldGuid = AssetDatabase.AssetPathToGUID(assetPath);
                var before = AssetDatabase.GlobalArtifactDependencyVersion;
                var replacementGuid = Guid.NewGuid().ToString("N");
                var metaPath = assetPath + ".meta";
                var meta = File.ReadAllText(metaPath);
                Assert.That(meta, Does.Contain("guid: " + oldGuid));
                // The import above journaled its own batch; clear it so the assertion
                // below is about the GUID swap and nothing else.
                PersistentAssetIndexCache.ClearPendingChanges();

                File.WriteAllText(
                    metaPath,
                    meta.Replace("guid: " + oldGuid, "guid: " + replacementGuid));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                Assert.That(AssetDatabase.AssetPathToGUID(assetPath), Is.EqualTo(replacementGuid));
                Assert.That(AssetDatabase.GlobalArtifactDependencyVersion, Is.Not.EqualTo(before),
                    "the cache revision must reject an otherwise unchanged path whose GUID changed via VCS");
                // Which half of the reload covers it: the postprocessor sees the swap as
                // a concrete import and journals the path, so the next reload REPLAYS it
                // onto the snapshot. The retype job cannot: it rebuilds types for records
                // the snapshot already holds and would keep the stale GUID for ever.
                Assert.That(
                    PersistentAssetIndexCache.CapturePendingChanges()
                        .SelectMany(batch => batch.Imported),
                    Has.Member(assetPath),
                    "an in-place GUID swap is covered by the journaled import batch, "
                    + "not by the follow-up type-reconciliation job");
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        // Unity 2022.3 serialises any label carrying a non-ASCII character as a
        // YAML double-quoted scalar with an escape, while Unity 6 writes the
        // scalar raw. Measured on both editors: 2022.3 emits \xNN below U+0100,
        // \uNNNN for the rest of the BMP, and \UNNNNNNNN above it. The scanner
        // is the authoritative full-scan path, so a label it fails to decode is
        // indexed and displayed mangled and stops matching l: search.
        //
        // The fixture is bytes, not an imported asset, deliberately: the guard
        // then holds on every editor rather than only on whichever one happens
        // to write the escaped form, and a .meta from a mixed-version team is
        // exactly the input that reaches Unity 6 with escapes in it.
        [Test]
        public void MetaLabelScanner_DecodesTheYamlEscapesUnityWritesForNonAsciiLabels()
        {
            var metaPath = Path.Combine(
                Path.GetTempPath(),
                "__ZepEscapedLabels_" + Guid.NewGuid().ToString("N") + ".meta");
            try
            {
                File.WriteAllText(
                    metaPath,
                    string.Join(
                        "\n",
                        "fileFormatVersion: 2",
                        "guid: 0123456789abcdef0123456789abcdef",
                        "labels:",
                        "- \"Caf\\xE9 Boss\"",
                        "- \"\\u65E5\\u672C Boss\"",
                        "- \"Boss \\U0001F3AE\"",
                        "- Gameplay",
                        "TextScriptImporter:",
                        "  externalObjects: {}",
                        ""),
                    new UTF8Encoding(false));

                var guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    metaPath,
                    out var labels);

                Assert.That(guid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
                Assert.That(
                    labels,
                    Is.EqualTo(new[]
                    {
                        "Caf\u00E9 Boss",
                        "\u65E5\u672C Boss",
                        "Boss \U0001F3AE",
                        "Gameplay",
                    }),
                    "escaped labels must decode to the code points Unity round trips through AssetDatabase.GetLabels");
            }
            finally
            {
                File.Delete(metaPath);
            }
        }

        // The unescape switch has always re-emitted an escape it does not know
        // verbatim rather than dropping it. Adding the numeric forms must not
        // cost that: a truncated or non-hex sequence still reads back exactly
        // as written, so a hand-edited .meta degrades instead of losing text.
        [Test]
        public void MetaLabelScanner_LeavesMalformedOrUnknownEscapesLiteral()
        {
            var metaPath = Path.Combine(
                Path.GetTempPath(),
                "__ZepMalformedLabels_" + Guid.NewGuid().ToString("N") + ".meta");
            try
            {
                File.WriteAllText(
                    metaPath,
                    string.Join(
                        "\n",
                        "fileFormatVersion: 2",
                        "guid: fedcba9876543210fedcba9876543210",
                        "labels:",
                        "- \"Bad\\xZZ End\"",
                        "- \"Short\\u12\"",
                        "- \"Unknown\\q Escape\"",
                        "- \"Quote\\\" And\\\\ Tab\\tEnd\"",
                        "TextScriptImporter:",
                        "  externalObjects: {}",
                        ""),
                    new UTF8Encoding(false));

                UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    metaPath,
                    out var labels);

                Assert.That(
                    labels,
                    Is.EqualTo(new[]
                    {
                        "Bad\\xZZ End",
                        "Short\\u12",
                        "Unknown\\q Escape",
                        "Quote\" And\\ Tab\tEnd",
                    }));
            }
            finally
            {
                File.Delete(metaPath);
            }
        }

        [Test]
        public void RecordSources_BuildRecordIncludesUnityLabels()
        {
            var assetPath = "Assets/__ZepLabelSource_"
                + Guid.NewGuid().ToString("N")
                + ".txt";
            try
            {
                File.WriteAllText(assetPath, "Zep label source test");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                Assert.That(asset, Is.Not.Null);
                AssetDatabase.SetLabels(asset, new[] { "Gameplay", "Needs Review", "Café Boss" });
                AssetDatabase.SaveAssets();

                var databaseRecord = new AssetDatabaseAssetRecordSource().BuildRecord(AssetPath.From(assetPath));
                var nativeSource = new NativeFileSystemAssetRecordSource();
                var nativeRecord = nativeSource.BuildRecord(AssetPath.From(assetPath));
                var hybridRecord = new HybridAssetRecordSource().BuildRecord(AssetPath.From(assetPath));
                var labelSnapshot = AssetDatabaseAssetRecordSource.BuildUnityLabelSnapshot();
                var guid = AssetDatabase.AssetPathToGUID(assetPath);

                Assert.That(databaseRecord, Is.Not.Null);
                Assert.That(nativeRecord, Is.Not.Null);
                Assert.That(hybridRecord, Is.Not.Null);
                Assert.That(labelSnapshot, Is.Not.Null);
                Assert.That(labelSnapshot.ContainsKey(guid), Is.True);
                Assert.That(
                    databaseRecord.UnityLabels,
                    Is.EquivalentTo(new[] { "Gameplay", "Needs Review", "Café Boss" }));
                Assert.That(nativeRecord.UnityLabels, Is.EquivalentTo(databaseRecord.UnityLabels));
                Assert.That(hybridRecord.UnityLabels, Is.EquivalentTo(databaseRecord.UnityLabels));
                Assert.That(labelSnapshot[guid], Is.EquivalentTo(databaseRecord.UnityLabels));
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void RecordSources_PreserveConcreteScriptableObjectTypeAndBaseTypeAlias()
        {
            var assetPath = "Assets/__ZepConcreteType_"
                + Guid.NewGuid().ToString("N")
                + ".asset";
            var instance = ScriptableObject.CreateInstance<ZepConcreteTypeFixture>();
            try
            {
                AssetDatabase.CreateAsset(instance, assetPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var typeSnapshot = AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot();
                var databaseRecord = new AssetDatabaseAssetRecordSource().BuildRecord(AssetPath.From(assetPath));
                var nativeSource = new NativeFileSystemAssetRecordSource();
                var nativeRecord = nativeSource.BuildRecord(AssetPath.From(assetPath));
                var hybridRecord = new HybridAssetRecordSource().BuildRecord(AssetPath.From(assetPath));

                Assert.That(typeSnapshot, Is.Not.Null);
                Assert.That(typeSnapshot[guid].TypeName, Is.EqualTo(nameof(ZepConcreteTypeFixture)));
                Assert.That(typeSnapshot[guid].BaseTypeNames, Does.Contain(nameof(ScriptableObject)));
                Assert.That(
                    AssetDatabaseAssetRecordSource.ResolveTypeMetadata(assetPath, guid, typeSnapshot).TypeName,
                    Is.EqualTo(nameof(ZepConcreteTypeFixture)),
                    "the worker-safe cold-start path must project the concrete type snapshot");
                Assert.That(databaseRecord.TypeName, Is.EqualTo(nameof(ZepConcreteTypeFixture)));
                Assert.That(nativeRecord.TypeName, Is.EqualTo(nameof(ZepConcreteTypeFixture)));
                Assert.That(hybridRecord.TypeName, Is.EqualTo(nameof(ZepConcreteTypeFixture)));
                Assert.That(databaseRecord.BaseTypeNames, Does.Contain(nameof(ScriptableObject)));
                Assert.That(nativeRecord.BaseTypeNames, Does.Contain(nameof(ScriptableObject)));
                Assert.That(hybridRecord.BaseTypeNames, Does.Contain(nameof(ScriptableObject)));

                nativeSource.PrepareForEnumeration();
                var coldStartRecord = Task.Run(() => nativeSource.GetRecords()
                    .FirstOrDefault(record => string.Equals(
                        record.AssetPath,
                        assetPath,
                        StringComparison.OrdinalIgnoreCase)))
                    .GetAwaiter()
                    .GetResult();
                Assert.That(coldStartRecord, Is.Not.Null);
                Assert.That(coldStartRecord.TypeName, Is.EqualTo(nameof(ZepConcreteTypeFixture)),
                    "the default background cold-start source must retain the concrete type");

                var index = new InMemoryAssetIndex();
                index.Upsert(nativeRecord);
                var search = new AssetSearchService(index);
                Assert.That(search.Search("t:" + nameof(ZepConcreteTypeFixture)), Has.Count.EqualTo(1));
                Assert.That(search.Search("t:ScriptableObject"), Has.Count.EqualTo(1),
                    "concrete .asset types must retain Unity's broad ScriptableObject match");

                UseStableCaptureOverrides();
                Assert.That(
                    PersistentAssetIndexCache.Save(
                        new[] { nativeRecord },
                        Array.Empty<AssetFolderRecord>()),
                    Is.True);
                Assert.That(
                    PersistentAssetIndexCache.TryLoad(out var restoredRecords, out _),
                    Is.True);
                var restoredIndex = new InMemoryAssetIndex();
                restoredIndex.Upsert(restoredRecords[0]);
                var restoredSearch = new AssetSearchService(restoredIndex);
                Assert.That(restoredSearch.Search("t:" + nameof(ZepConcreteTypeFixture)), Has.Count.EqualTo(1));
                Assert.That(restoredSearch.Search("t:ScriptableObject"), Has.Count.EqualTo(1));
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        [Test]
        public void RecordSources_DoNotInferScriptableObjectFromAssetExtension()
        {
            var assetPath = "Assets/__ZepMeshType_"
                + Guid.NewGuid().ToString("N")
                + ".asset";
            var mesh = new Mesh { name = "Serialized Mesh" };
            try
            {
                AssetDatabase.CreateAsset(mesh, assetPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var typeSnapshot = AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot();
                var records = new[]
                {
                    new AssetDatabaseAssetRecordSource().BuildRecord(AssetPath.From(assetPath)),
                    new NativeFileSystemAssetRecordSource().BuildRecord(AssetPath.From(assetPath)),
                    new HybridAssetRecordSource().BuildRecord(AssetPath.From(assetPath)),
                };

                Assert.That(typeSnapshot, Is.Not.Null);
                Assert.That(typeSnapshot[guid].TypeName, Is.EqualTo(nameof(Mesh)));
                Assert.That(typeSnapshot[guid].BaseTypeNames, Does.Not.Contain(nameof(ScriptableObject)));
                for (var i = 0; i < records.Length; i++)
                {
                    Assert.That(records[i], Is.Not.Null);
                    Assert.That(records[i].TypeName, Is.EqualTo(nameof(Mesh)));
                    var index = new InMemoryAssetIndex();
                    index.Upsert(records[i]);
                    Assert.That(new AssetSearchService(index).Search("t:ScriptableObject"), Is.Empty);
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }

        [Test]
        public void BackgroundSource_PreservesScriptedImporterTypeAndItsBaseClasses()
        {
            var assetPath = "Assets/__ZepImportedType_"
                + Guid.NewGuid().ToString("N")
                + ".zeptypefixture";
            try
            {
                File.WriteAllText(assetPath, "Zep scripted importer type fixture");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                Assert.That(mainAsset, Is.TypeOf<ZepImportedConcreteFixture>());
                var typeSnapshot = AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot();
                Assert.That(typeSnapshot, Is.Not.Null);
                Assert.That(typeSnapshot.ContainsKey(guid), Is.True,
                    "registered ScriptedImporter extensions must be included in the authoritative snapshot");
                Assert.That(typeSnapshot[guid].TypeName, Is.EqualTo(nameof(ZepImportedConcreteFixture)));
                Assert.That(typeSnapshot[guid].BaseTypeNames,
                    Is.SupersetOf(new[]
                    {
                        nameof(ZepImportedBaseFixture),
                        nameof(ScriptableObject),
                    }));

                var nativeSource = new NativeFileSystemAssetRecordSource();
                nativeSource.PrepareForEnumeration(
                    AssetDatabaseAssetRecordSource.BuildUnityLabelSnapshot(),
                    typeSnapshot);
                var record = Task.Run(() => nativeSource.GetRecords()
                    .FirstOrDefault(candidate => string.Equals(
                        candidate.AssetPath,
                        assetPath,
                        StringComparison.OrdinalIgnoreCase)))
                    .GetAwaiter()
                    .GetResult();
                Assert.That(record, Is.Not.Null);
                var index = new InMemoryAssetIndex();
                index.Upsert(record);
                var search = new AssetSearchService(index);
                Assert.That(search.Search("t:" + nameof(ZepImportedConcreteFixture)), Has.Count.EqualTo(1));
                Assert.That(search.Search("t:" + nameof(ZepImportedBaseFixture)), Has.Count.EqualTo(1));
                Assert.That(search.Search("t:ScriptableObject"), Has.Count.EqualTo(1));
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        [Test]
        public void RecordSources_PreserveFolderGuidAndUnityLabels()
        {
            var folderName = "__ZepFolderLabels_" + Guid.NewGuid().ToString("N");
            var folderPath = "Assets/" + folderName;
            var guid = AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var folderAsset = AssetDatabase.LoadMainAssetAtPath(folderPath);
                Assert.That(folderAsset, Is.Not.Null);
                AssetDatabase.SetLabels(folderAsset, new[] { "Gameplay", "Needs Review" });
                AssetDatabase.SaveAssets();

                var labelSnapshot = AssetDatabaseAssetRecordSource.BuildUnityLabelSnapshot();
                var databaseFolder = new AssetDatabaseAssetRecordSource().BuildFolderRecord(AssetPath.From(folderPath));
                var nativeFolder = new NativeFileSystemAssetRecordSource().BuildFolderRecord(AssetPath.From(folderPath));
                var hybrid = new HybridAssetRecordSource();
                var hybridFolder = hybrid.BuildFolderRecord(AssetPath.From(folderPath));
                var bulkHybridFolder = FindFolder(hybrid.GetFolderRecords(), folderPath);

                Assert.That(labelSnapshot, Is.Not.Null);
                Assert.That(labelSnapshot.ContainsKey(guid), Is.True,
                    "Unity's label index must include labeled folders");
                AssertFolderMetadata(databaseFolder, folderPath, guid);
                AssertFolderMetadata(nativeFolder, folderPath, guid);
                AssertFolderMetadata(hybridFolder, folderPath, guid);
                AssertFolderMetadata(bulkHybridFolder, folderPath, guid);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        [Test]
        public void NativeSource_EnumerationFolderSnapshotIsAtomicAndKeepsAuthoritativePackageRootGuid()
        {
            var folderName = "__ZepEnumerationSnapshot_" + Guid.NewGuid().ToString("N");
            var folderPath = "Assets/" + folderName;
            var folderGuid = AssetDatabase.CreateFolder("Assets", folderName);
            var originalPackageRoots = HybridAssetRecordSource.ResolvedRoots;
            try
            {
                var packageRoots = HybridAssetRecordSource.GetRegisteredPackageRoots();
                HybridAssetRecordSource.ResolvedRoots = packageRoots;
                AssetFolderRecord expectedPackageRoot = null;
                for (var i = 0; i < packageRoots.Count; i++)
                {
                    var candidate = new AssetDatabaseAssetRecordSource().BuildFolderRecord(
                        AssetPath.From(packageRoots[i].VirtualPrefix));
                    if (candidate != null && !string.IsNullOrEmpty(candidate.Guid))
                    {
                        expectedPackageRoot = candidate;
                        break;
                    }
                }

                Assert.That(expectedPackageRoot, Is.Not.Null,
                    "the fixture must expose at least one AssetDatabase-backed package root");

                var source = new NativeFileSystemAssetRecordSource();
                source.PrepareForEnumeration();
                Task.Run(() => source.GetRecords().ToList()).GetAwaiter().GetResult();

                Assert.That(AssetDatabase.DeleteAsset(folderPath), Is.True);
                var enumerationSnapshot = source.GetFolderRecords();
                var deletedFolder = FindFolder(enumerationSnapshot, folderPath);
                var packageRoot = FindFolder(
                    enumerationSnapshot,
                    expectedPackageRoot.FolderPath.Value);

                Assert.That(deletedFolder, Is.Not.Null,
                    "folders and assets must come from the same point-in-time enumeration");
                Assert.That(deletedFolder.Guid, Is.EqualTo(folderGuid));
                Assert.That(
                    packageRoot,
                    Is.Not.Null,
                    "expected package root "
                    + expectedPackageRoot.FolderPath.Value
                    + "; enumerated package roots: "
                    + string.Join(
                        ", ",
                        enumerationSnapshot
                            .Where(folder => folder.FolderPath.Value.StartsWith(
                                "Packages/",
                                StringComparison.Ordinal))
                            .Select(folder => folder.FolderPath.Value)
                            .Take(20)));
                Assert.That(packageRoot.Guid, Is.EqualTo(expectedPackageRoot.Guid),
                    "native package-root projection must retain Unity's authoritative virtual GUID");
                Assert.That(packageRoot.UnityLabels, Is.EqualTo(expectedPackageRoot.UnityLabels));

                var refreshedFolders = source.GetFolderRecords();
                Assert.That(FindFolder(refreshedFolders, folderPath), Is.Null,
                    "only the enumeration snapshot is consumed; later projection rebuilds must read live folders");
            }
            finally
            {
                HybridAssetRecordSource.ResolvedRoots = originalPackageRoots;
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        // Unity writes labels as a block sequence but reads the inline flow form
        // too. A .meta carrying the flow form used to scan as unlabeled, so the
        // incremental record silently disagreed with the next full rebuild and
        // `l:` search missed the asset until then.
        [TestCase("labels:\n- Gameplay\n- Needs Review\n", new[] { "Gameplay", "Needs Review" }, TestName = "MetaScanner_BlockSequenceLabels")]
        [TestCase("labels: [Gameplay, Needs Review]\n", new[] { "Gameplay", "Needs Review" }, TestName = "MetaScanner_FlowSequenceLabels")]
        [TestCase("labels: ['Gameplay, Combat', \"Needs Review\"]\n", new[] { "Gameplay, Combat", "Needs Review" }, TestName = "MetaScanner_FlowSequenceQuotedLabels")]
        [TestCase("labels: [\"Say \\\"Hello, world\\\"\", Gameplay]\n", new[] { "Say \"Hello, world\"", "Gameplay" }, TestName = "MetaScanner_FlowEscapedQuoteComma")]
        [TestCase("labels: ['It''s ready, now', Gameplay]\n", new[] { "It's ready, now", "Gameplay" }, TestName = "MetaScanner_FlowDoubledSingleQuoteComma")]
        [TestCase("labels: []\n", new string[0], TestName = "MetaScanner_EmptyFlowSequence")]
        [TestCase("labels: nonsense\n", new string[0], TestName = "MetaScanner_GarbageAfterLabels")]
        [TestCase("labels: [\n", new string[0], TestName = "MetaScanner_UnterminatedFlowSequence")]
        public void ReadGuidAndLabelsFromMetaFile_ParsesLabelForms(string labelsYaml, string[] expected)
        {
            var metaPath = Path.Combine(
                Path.GetTempPath(),
                "__ZepMetaLabels_" + Guid.NewGuid().ToString("N") + ".asset.meta");
            try
            {
                File.WriteAllText(
                    metaPath,
                    "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n"
                    + labelsYaml
                    + "NativeFormatImporter:\n  externalObjects: {}\n");

                var guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    metaPath, out var labels);

                Assert.That(guid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
                Assert.That(labels, Is.EqualTo(expected));
            }
            finally
            {
                File.Delete(metaPath);
            }
        }

        [Test]
        public void MetaScanner_FlowBackslashParity()
        {
            var metaPath = Path.Combine(
                Path.GetTempPath(),
                "__ZepMetaLabels_" + Guid.NewGuid().ToString("N") + ".asset.meta");
            try
            {
                File.WriteAllText(
                    metaPath,
                    "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n"
                    + "labels: [\"Tail\\\\\", Gameplay]\n"
                    + "NativeFormatImporter:\n  externalObjects: {}\n");

                var guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    metaPath, out var labels);

                Assert.That(guid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
                Assert.That(labels, Is.EqualTo(new[] { "Tail\\", "Gameplay" }));

                File.WriteAllText(
                    metaPath,
                    "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n"
                    + "labels: [\"Path" + new string('\\', 3) + "\"part,rest\", Gameplay]\n"
                    + "NativeFormatImporter:\n  externalObjects: {}\n");

                guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    metaPath, out labels);

                Assert.That(guid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
                Assert.That(labels, Is.EqualTo(new[] { "Path\\\"part,rest", "Gameplay" }));
            }
            finally
            {
                File.Delete(metaPath);
            }
        }

        [Test]
        public void MetaScanner_EscapedFlowLabelsSearch()
        {
            var metaPath = Path.Combine(
                Path.GetTempPath(),
                "__ZepMetaLabels_" + Guid.NewGuid().ToString("N") + ".asset.meta");
            try
            {
                File.WriteAllText(
                    metaPath,
                    "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n"
                    + "labels: [\"Say \\\"Hello, world\\\"\", Gameplay]\n"
                    + "NativeFormatImporter:\n  externalObjects: {}\n");

                var guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    metaPath, out var labels);
                var index = new InMemoryAssetIndex();
                index.Upsert(new AssetRecord(
                    guid,
                    "Assets/FlowLabels.asset",
                    "FlowLabels",
                    "ScriptableObject",
                    unityLabels: labels));
                var search = new AssetSearchService(index);

                Assert.That(guid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
                Assert.That(labels, Is.EqualTo(new[] { "Say \"Hello, world\"", "Gameplay" }));
                var gameplayResults = search.Search("l:Gameplay");
                Assert.That(gameplayResults, Has.Count.EqualTo(1));
                Assert.That(gameplayResults[0].UnityLabels,
                    Is.EqualTo(new[] { "Say \"Hello, world\"", "Gameplay" }));
                Assert.That(gameplayResults[0].Guid, Is.EqualTo(guid));
                Assert.That(search.Search("l:MissingLabel"), Is.Empty);
            }
            finally
            {
                File.Delete(metaPath);
            }
        }

        // The snapshot runs on every hydration, cache hits included. Resolving labels
        // through the asset OBJECT made it import every labeled asset in the project;
        // the GUID-keyed read must produce the same labels without loading anything.
        [Test]
        public void BuildUnityLabelSnapshot_ResolvesLabelsWithoutLoadingTheAsset()
        {
            var assetPath = "Assets/__ZepUnloadedLabels_"
                + Guid.NewGuid().ToString("N")
                + ".txt";
            try
            {
                File.WriteAllText(assetPath, "Zep unloaded-label test");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                AssetDatabase.SetLabels(asset, new[] { "Needs Review", "Gameplay" });
                AssetDatabase.SaveAssets();
                var guid = AssetDatabase.AssetPathToGUID(assetPath);

                Resources.UnloadAsset(asset);
                if (AssetDatabase.IsMainAssetAtPathLoaded(assetPath))
                {
                    Assert.Ignore("the editor still holds the fixture asset; the load probe would be meaningless");
                }

                var snapshot = AssetDatabaseAssetRecordSource.BuildUnityLabelSnapshot();

                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot[guid], Is.EqualTo(new[] { "Gameplay", "Needs Review" }),
                    "labels are stored sorted so the cache signature is order-independent");
                Assert.That(AssetDatabase.IsMainAssetAtPathLoaded(assetPath), Is.False,
                    "building the label snapshot must not load asset objects");
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        // A cache row that AssetFolderRecord itself rejects must lose to TryLoad's
        // "false on any failure" contract, not throw out of the deferred hydration
        // callback (which leaves the panel empty until the next domain reload).
        [Test]
        public void TryLoad_FolderRowWithAWhitespacePath_IsDiscardedWithoutThrowing()
        {
            WriteCacheFile("   ");
            LogAssert.Expect(LogType.Warning, new Regex("Discarding corrupt index cache"));

            var loaded = PersistentAssetIndexCache.TryLoad( out var records, out var folders);

            Assert.That(loaded, Is.False, "a folder path that normalizes to empty is a corrupt row");
            Assert.That(records, Is.Null);
            Assert.That(folders, Is.Null);
        }

        [Test]
        public void TryLoad_ValidFolderRow_StillLoads()
        {
            WriteCacheFile("Assets/Valid");

            var loaded = PersistentAssetIndexCache.TryLoad( out var records, out var folders);

            Assert.That(loaded, Is.True);
            Assert.That(records, Is.Empty);
            Assert.That(folders.Select(folder => folder.FolderPath.Value),
                Is.EqualTo(new[] { "Assets/Valid" }));
        }

        // Hand-writes the current format (see PersistentAssetIndexCache's file-format
        // comment): zero records, then one folder row with the given raw path. Save()
        // cannot produce this fixture — it refuses folder records the domain type
        // already rejected.
        private static void WriteCacheFile(string rawFolderPath)
        {
            Directory.CreateDirectory(PersistentAssetIndexCache.CacheDirectory);
            using (var stream = new FileStream(
                PersistentAssetIndexCache.CacheFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(new[] { (byte)'B', (byte)'C', (byte)'P', (byte)'I' });
                writer.Write((byte)12);
                writer.Write(Guid.NewGuid().ToByteArray());
                writer.Write(-1L); // tag assignment timestamp (missing file)
                writer.Write(-1L); // tag definition timestamp (missing file)
                writer.Write((uint)0); // artifact dependency revision
                writer.Write(0L); // covered journal generation
                writer.Write(0); // record count
                writer.Write(1); // folder count
                writer.Write(rawFolderPath);
                writer.Write("folder-guid");
                writer.Write(0); // folder label count
            }
        }

        private static AssetFolderRecord FindFolder(
            IReadOnlyList<AssetFolderRecord> folders,
            string expectedPath)
        {
            for (var i = 0; i < folders.Count; i++)
            {
                if (string.Equals(
                    folders[i].FolderPath.Value,
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return folders[i];
                }
            }

            return null;
        }

        private static bool IsTransientReadFailure(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException;
        }

        private static IEnumerator AssertFolderHandoffDuringHydratedSave(
            IReadOnlyList<AssetFolderRecord> initialFolders,
            IReadOnlyList<AssetFolderRecord> changedFolders,
            Action<IReadOnlyList<AssetFolderRecord>, InMemoryAssetIndex> assertProjection)
        {
            UseStableCaptureOverrides();
            var trigger = new AssetRecord(
                "folder-handoff-trigger-guid",
                "Assets/FolderHandoffTrigger.asset",
                "FolderHandoffTrigger",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                initialFolders), Is.True);

            var priorObserver = PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            var serializationEntered = new ManualResetEventSlim(false);
            var releaseSerialization = new ManualResetEventSlim(false);
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                serializationEntered.Set();
                releaseSerialization.Wait();
            };
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { trigger.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));

            var source = new AuthoritativeSnapshotSource(
                new[] { trigger },
                initialFolders);
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true);
            var completed = false;
            try
            {
                synchronizer.SynchronizeAsync(
                    () => Assert.Fail("The cached catalog must hydrate."),
                    () => completed = true);

                for (var frame = 0; frame < 300 && !serializationEntered.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(serializationEntered.IsSet, Is.True,
                    "the patched snapshot save must enter serialization before the live folder delta");
                Assert.That(completed, Is.False,
                    "the gated worker must keep hydration completion pending");
                Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value),
                    Is.EquivalentTo(initialFolders.Select(folder => folder.FolderPath.Value)));

                index.ReplaceFolders(changedFolders);
                releaseSerialization.Set();
                for (var frame = 0; frame < 300 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True,
                    "completion must follow release of the blocked snapshot serialization");
                var handoff = synchronizer.GetFolderRecordsForProjection();
                var projectionIndex = new InMemoryAssetIndex();
                projectionIndex.ReplaceFolders(handoff);
                assertProjection(handoff, projectionIndex);
            }
            finally
            {
                releaseSerialization.Set();
                synchronizer.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }
        }

        private static IEnumerator AssertResolvedOverlayTopology(
            bool currentIsFolder,
            bool recordAsMove)
        {
            const string deletedRoot = "Assets/Recreated";
            const string currentFolder = deletedRoot + "/Current";
            const string nestedFolder = currentFolder + "/Nested";
            const string staleFolder = deletedRoot + "/Stale";
            var currentRecord = new AssetRecord(
                "resolved-overlay-guid",
                nestedFolder + "/Child.asset",
                "Child",
                "TestAsset");
            var staleRecord = new AssetRecord(
                "stale-overlay-guid",
                staleFolder + "/Stale.asset",
                "Stale",
                "TestAsset");
            var folders = new[]
            {
                new AssetFolderRecord(AssetPath.From(deletedRoot)),
                new AssetFolderRecord(AssetPath.From(currentFolder)),
                new AssetFolderRecord(AssetPath.From(nestedFolder)),
                new AssetFolderRecord(AssetPath.From(staleFolder)),
            };
            var source = new BlockingSnapshotSource(
                new[] { currentRecord, staleRecord },
                folders);
            var index = new InMemoryAssetIndex();
            if (currentIsFolder)
            {
                index.Replace(
                    Array.Empty<AssetRecord>(),
                    new[] { folders[1] });
            }
            else
            {
                index.Upsert(currentRecord);
            }

            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: false);
            synchronizer.RecordConcreteChanges(new ProjectAssetChanges(
                Array.Empty<string>(),
                new[] { deletedRoot },
                Array.Empty<string>(),
                Array.Empty<string>()));
            var currentPath = currentIsFolder
                ? currentFolder
                : currentRecord.AssetPath;
            synchronizer.RecordConcreteChanges(recordAsMove
                ? new ProjectAssetChanges(
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new[] { currentPath },
                    new[] { "Assets/Old/Child.asset" })
                : new ProjectAssetChanges(
                    new[] { currentPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var completed = false;

            try
            {
                synchronizer.SynchronizeAsync(null, () => completed = true);
                for (var frame = 0; frame < 100 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True);
                source.ReleaseEnumeration();
                for (var frame = 0; frame < 100 && !completed; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.True);
                Assert.That(index.TryGetFolder(deletedRoot, out _), Is.True);
                Assert.That(index.TryGetFolder(currentFolder, out _), Is.True);
                Assert.That(index.TryGetFolder(staleFolder, out _), Is.False,
                    "a current descendant must not preserve a truly deleted sibling");
                if (currentIsFolder)
                {
                    Assert.That(index.TryGetFolder(nestedFolder, out _), Is.True);
                    Assert.That(index.TryGetByPath(currentRecord.AssetPath, out _), Is.True,
                        "a current folder keeps its authoritative descendants");
                }
                else
                {
                    Assert.That(index.TryGetFolder(nestedFolder, out _), Is.True);
                    Assert.That(index.TryGetByPath(currentRecord.AssetPath, out _), Is.True);
                }

                Assert.That(index.TryGetByPath(staleRecord.AssetPath, out _), Is.False);
            }
            finally
            {
                source.ReleaseEnumeration();
            }
        }

        private static void AssertRestartSnapshotIsReachable(
            AuthoritativeSnapshotSource source,
            string expectedRecordPath,
            IReadOnlyList<string> expectedFolderPaths)
        {
            var firstIndex = new InMemoryAssetIndex();
            var firstSynchronizer = new AssetIndexSynchronizer(
                source,
                source,
                firstIndex,
                enablePersistentCache: true);
            var firstCompleted = false;
            firstSynchronizer.SynchronizeAsync(
                () => Assert.Fail("a hydrated topology recovery must not show the cold overlay"),
                () => firstCompleted = true);

            firstSynchronizer.FlushPendingSnapshotSave();

            Assert.That(firstCompleted, Is.True);
            Assert.That(firstIndex.TryGetByPath(expectedRecordPath, out _), Is.True);
            for (var i = 0; i < expectedFolderPaths.Count; i++)
            {
                Assert.That(firstIndex.TryGetFolder(expectedFolderPaths[i], out _), Is.True,
                    $"missing required parent '{expectedFolderPaths[i]}'");
            }

            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                "the authoritative snapshot must retire the compact journal");
            Assert.That(PersistentAssetIndexCache.TryLoad(
                out var savedRecords,
                out var savedFolders), Is.True);
            Assert.That(savedRecords.Select(record => record.AssetPath),
                Is.EqualTo(new[] { expectedRecordPath }));
            Assert.That(savedFolders.Select(folder => folder.FolderPath.Value),
                Is.SupersetOf(expectedFolderPaths));

            // Simulate a new process with no journal. The saved topology must be
            // sufficient without resolving or enumerating any project paths.
            var restartedIndex = new InMemoryAssetIndex();
            var restartSource = new ThrowIfEnumeratedSource();
            var restartedSynchronizer = new AssetIndexSynchronizer(
                restartSource,
                restartSource,
                restartedIndex,
                enablePersistentCache: true);
            var restartCompleted = false;
            restartedSynchronizer.SynchronizeAsync(
                () => Assert.Fail("the persisted topology must hydrate after restart"),
                () => restartCompleted = true);

            Assert.That(restartCompleted, Is.True);
            Assert.That(restartedIndex.TryGetByPath(expectedRecordPath, out _), Is.True);
            for (var i = 0; i < expectedFolderPaths.Count; i++)
            {
                Assert.That(restartedIndex.TryGetFolder(expectedFolderPaths[i], out _), Is.True);
            }
        }

        [UnityTest]
        public IEnumerator SynchronizeAsync_HydratedSnapshotSave_CommitsALoadableSnapshotOffTheMainThread()
        {
            UseStableCaptureOverrides();
            var cached = new AssetRecord(
                "bg-save-cached-guid",
                "Assets/BgSaveCached.asset",
                "BgSaveCached",
                "TestAsset");
            var imported = new AssetRecord(
                "bg-save-imported-guid",
                "Assets/BgSaveImported.asset",
                "BgSaveImported",
                "TestAsset");
            var serializationThreadIds = new List<int>();
            var threadIdGate = new object();
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                lock (threadIdGate)
                {
                    serializationThreadIds.Add(
                        Thread.CurrentThread.ManagedThreadId);
                }
            };
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { cached },
                Array.Empty<AssetFolderRecord>()), Is.True);
            Assert.That(serializationThreadIds, Is.EqualTo(new[] { mainThreadId }),
                "the synchronous Save overload still serializes on its caller's thread, "
                + "so a worker id below cannot be an artefact of the observer");
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { imported.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var source = new AuthoritativeSnapshotSource(
                new[] { cached, imported },
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(null, () => completed = true);

            Assert.That(completed, Is.False,
                "the hydrated save must hand the record serialization to a worker and "
                + "return, instead of writing the whole snapshot before it returns");
            for (var frame = 0; frame < 1000 && !completed; frame++)
            {
                yield return null;
            }

            Assert.That(completed, Is.True,
                "the commit poll must complete the operation it deferred");
            lock (threadIdGate)
            {
                Assert.That(serializationThreadIds, Has.Count.EqualTo(2));
                Assert.That(serializationThreadIds[1], Is.Not.EqualTo(mainThreadId),
                    "the record serialization loop is the whole cost of a save and must "
                    + "not run on the main thread");
            }

            Assert.That(HasStagedSnapshotFiles(), Is.False,
                "a committed snapshot leaves no staged file behind");
            Assert.That(File.Exists(
                PersistentAssetIndexCache.DirtyMarkerPath), Is.False,
                "a clean commit clears the dirty marker it started from");
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(), Is.Empty,
                "the commit retires the journal generation it covered");
            Assert.That(JournalHasBatches(), Is.False,
                "a retired generation leaves no journal behind");
            Assert.That(PersistentAssetIndexCache.TryLoad(
                out var saved,
                out _), Is.True);
            Assert.That(
                saved.Select(record => record.AssetPath),
                Is.EquivalentTo(new[] { cached.AssetPath, imported.AssetPath }));
            synchronizer.Dispose();
        }

        [Test]
        public void SynchronizeAsync_HydratedSnapshotSaveSupersededBeforeCommit_KeepsTheOldSnapshot()
        {
            UseStableCaptureOverrides();
            var cached = new AssetRecord(
                "bg-abort-cached-guid",
                "Assets/BgAbortCached.asset",
                "BgAbortCached",
                "TestAsset");
            var imported = new AssetRecord(
                "bg-abort-imported-guid",
                "Assets/BgAbortImported.asset",
                "BgAbortImported",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { cached },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var committedSnapshot = File.ReadAllBytes(
                PersistentAssetIndexCache.CacheFilePath);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    new[] { imported.AssetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var source = new AuthoritativeSnapshotSource(
                new[] { cached, imported },
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(null, () => completed = true);
            Assert.That(completed, Is.False,
                "the staged write must still be in flight for this to test a commit");

            // Retiring the synchronizer moves the operation generation, which is exactly
            // what canCommit measures: the staged snapshot must be discarded rather than
            // published on top of a cache nobody is tracking any more.
            synchronizer.Dispose();

            Assert.That(completed, Is.False,
                "a superseded operation must not report a hydration it never committed");
            Assert.That(
                File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath),
                Is.EqualTo(committedSnapshot),
                "an aborted commit must leave the previous snapshot byte-identical");
            Assert.That(HasStagedSnapshotFiles(), Is.False,
                "the staged file must not survive an aborted commit");
            Assert.That(File.Exists(
                PersistentAssetIndexCache.DirtyMarkerPath), Is.False,
                "the abort touched nothing: the committed snapshot plus the "
                + "unretired journal are still a complete account of the index, "
                + "and distrusting them would cost the next reload a cold scan");
            Assert.That(JournalHasBatches(), Is.True,
                "an aborted commit must not retire the generation it failed to persist");
        }

        [UnityTest]
        public IEnumerator ColdSave_WorkerCommitAndReload()
        {
            UseStableCaptureOverrides();
            if (File.Exists(PersistentAssetIndexCache.CacheFilePath))
            {
                File.Delete(PersistentAssetIndexCache.CacheFilePath);
            }

            var record = new AssetRecord(
                "cold-save-guid",
                "Assets/ColdSave/Record.asset",
                "Record",
                "TestAsset");
            var folder = new AssetFolderRecord(AssetPath.From("Assets/ColdSave"));
            var source = new BlockingSnapshotSource(
                new[] { record },
                new[] { folder });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var serializationThread = mainThread;
            var priorObserver = PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            var completed = 0;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                serializationThread = Thread.CurrentThread.ManagedThreadId;
                entered.Set();
                release.Wait();
            };

            try
            {
                synchronizer.SynchronizeAsync(null, () => completed++);
                for (var frame = 0; frame < 300 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True);
                source.ReleaseEnumeration();
                for (var frame = 0; frame < 300 && !entered.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(synchronizer.LastEnumerationStrategy,
                    Is.EqualTo(EnumerationStrategy.Async));
                Assert.That(entered.IsSet, Is.True);
                Assert.That(completed, Is.EqualTo(0));
                Assert.That(index.TryGetByPath(record.AssetPath, out _), Is.True);
                Assert.That(index.TryGetFolder(folder.FolderPath.Value, out _), Is.True);

                release.Set();
                for (var frame = 0; frame < 300 && completed == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.EqualTo(1));
                Assert.That(serializationThread, Is.Not.EqualTo(mainThread));
                Assert.That(PersistentAssetIndexCache.TryLoad(
                    out var savedRecords,
                    out var savedFolders), Is.True);
                Assert.That(savedRecords.Select(saved => saved.AssetPath),
                    Is.EquivalentTo(new[] { record.AssetPath }));
                Assert.That(savedFolders.Select(saved => saved.FolderPath.Value),
                    Is.SupersetOf(new[] { folder.FolderPath.Value }));
                Assert.That(HasStagedSnapshotFiles(), Is.False);
            }
            finally
            {
                source.ReleaseEnumeration();
                release.Set();
                synchronizer.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }
        }

        [UnityTest]
        public IEnumerator VerificationSave_LiveChangeDuringSerialization_StillCommits()
        {
            UseStableCaptureOverrides();
            var previous = new AssetRecord(
                "cold-save-previous-guid",
                "Assets/ColdSave/Previous.asset",
                "Previous",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { previous },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var previousSnapshot = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            PersistentAssetIndexCache.MarkDirty();

            var current = new AssetRecord(
                "cold-save-current-guid",
                "Assets/ColdSave/Current.asset",
                "Current",
                "TestAsset");
            var source = new BlockingSnapshotSource(
                new[] { current },
                new[] { new AssetFolderRecord(AssetPath.From("Assets/ColdSave")) });
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var priorObserver = PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                entered.Set();
                release.Wait();
            };

            try
            {
                synchronizer.SynchronizeAsync(null, null);
                for (var frame = 0; frame < 300 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True);
                source.ReleaseEnumeration();
                for (var frame = 0; frame < 300 && !entered.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(entered.IsSet, Is.True);
                Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
                // A change that lands while the verification's snapshot is being
                // serialized is journaled above the generation that snapshot
                // covers, so the save commits and the change replays on top of
                // it at the next load; nothing is refused and nothing is dirty.
                synchronizer.InvalidateInProgressScan();
                release.Set();
                yield return WaitForBackgroundJob(synchronizer);
                synchronizer.FlushPendingSnapshotSave();

                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath),
                    Is.Not.EqualTo(previousSnapshot),
                    "the verified snapshot replaced the one the dirty marker distrusted");
                Assert.That(index.TryGetByPath(current.AssetPath, out _), Is.True);
                Assert.That(index.TryGetByPath(previous.AssetPath, out _), Is.False,
                    "the walk did not see the previous record, so the verification removed it");
                Assert.That(File.Exists(PersistentAssetIndexCache.DirtyMarkerPath), Is.False);
                Assert.That(PersistentAssetIndexCache.TryLoad(out var verified, out _), Is.True);
                Assert.That(verified.Select(record => record.AssetPath),
                    Is.EqualTo(new[] { current.AssetPath }));
            }
            finally
            {
                source.ReleaseEnumeration();
                release.Set();
                synchronizer.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }
        }

        [UnityTest]
        public IEnumerator ColdSave_FailureAndCompatibility()
        {
            UseStableCaptureOverrides();
            if (File.Exists(PersistentAssetIndexCache.CacheFilePath))
            {
                File.Delete(PersistentAssetIndexCache.CacheFilePath);
            }

            var record = new AssetRecord(
                "cold-save-failure-guid",
                "Assets/ColdSave/Failure.asset",
                "Failure",
                "TestAsset");
            var source = new BlockingSnapshotSource(
                new[] { record },
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true);
            var priorObserver = PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            var completed = 0;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                throw new IOException("cold save failure");
            };

            try
            {
                synchronizer.SynchronizeAsyncWithOutcome(null, result =>
                {
                    Assert.That(result.Succeeded, Is.True);
                    completed++;
                });
                for (var frame = 0; frame < 300 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True);
                source.ReleaseEnumeration();
                for (var frame = 0; frame < 300 && completed == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(completed, Is.EqualTo(1));
                Assert.That(index.TryGetByPath(record.AssetPath, out _), Is.True);
                Assert.That(File.Exists(PersistentAssetIndexCache.DirtyMarkerPath), Is.True);
            }
            finally
            {
                source.ReleaseEnumeration();
                synchronizer.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }

            var disabledIndex = new InMemoryAssetIndex();
            var disabled = new AssetIndexSynchronizer(
                source, source, disabledIndex, enablePersistentCache: false);
            var observed = false;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () => observed = true;
            try
            {
                disabled.SynchronizeAsync(null, null);
                for (var frame = 0; frame < 300 && !disabledIndex.TryGetByPath(record.AssetPath, out _); frame++)
                {
                    yield return null;
                }

                Assert.That(disabledIndex.TryGetByPath(record.AssetPath, out _), Is.True);
                Assert.That(observed, Is.False);
            }
            finally
            {
                disabled.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }
        }

        [UnityTest]
        public IEnumerator ColdSave_DisposalAndSupersession()
        {
            UseStableCaptureOverrides();
            var existing = new AssetRecord(
                "cold-save-existing-guid",
                "Assets/ColdSave/Existing.asset",
                "Existing",
                "TestAsset");
            Assert.That(PersistentAssetIndexCache.Save(
                new[] { existing },
                Array.Empty<AssetFolderRecord>()), Is.True);
            var committedSnapshot = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);
            PersistentAssetIndexCache.MarkDirty();

            var replacement = new AssetRecord(
                "cold-save-replacement-guid",
                "Assets/ColdSave/Replacement.asset",
                "Replacement",
                "TestAsset");
            var source = new BlockingSnapshotSource(
                new[] { replacement },
                Array.Empty<AssetFolderRecord>());
            var index = new InMemoryAssetIndex();
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var priorObserver = PersistentAssetIndexCache.SnapshotSerializationObserverForTests;
            PersistentAssetIndexCache.SnapshotSerializationObserverForTests = () =>
            {
                entered.Set();
                release.Wait();
            };

            try
            {
                synchronizer.SynchronizeAsync(null, null);
                for (var frame = 0; frame < 300 && !source.EnumerationStarted.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(source.EnumerationStarted.IsSet, Is.True);
                source.ReleaseEnumeration();
                for (var frame = 0; frame < 300 && !entered.IsSet; frame++)
                {
                    yield return null;
                }

                Assert.That(entered.IsSet, Is.True);
                release.Set();
                synchronizer.Dispose();
                Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath),
                    Is.EqualTo(committedSnapshot));
            }
            finally
            {
                source.ReleaseEnumeration();
                release.Set();
                synchronizer.Dispose();
                PersistentAssetIndexCache.SnapshotSerializationObserverForTests = priorObserver;
            }
        }

        private static IEnumerable<string> EnumerateOneDirectoryThenFail(string root)
        {
            yield return root + "/Partial";
            throw new IOException("deferred enumeration failure");
        }

        private static void AssertFolderMetadata(
            AssetFolderRecord folder,
            string expectedPath,
            string expectedGuid)
        {
            Assert.That(folder, Is.Not.Null);
            Assert.That(folder.FolderPath.Value, Is.EqualTo(expectedPath));
            Assert.That(folder.Guid, Is.EqualTo(expectedGuid));
            Assert.That(folder.UnityLabels, Is.EquivalentTo(new[] { "Gameplay", "Needs Review" }));
        }

        // The journal replay runs AFTER the snapshot is hydrated, so the folder
        // list handed to the next projection cannot be the hydrated one: it still
        // holds the folder the journal just deleted. ApplyPendingChanges re-reads
        // the patched index for exactly this reason.
        [Test]
        public void PendingChangeJournal_FolderDeletion_LeavesItOutOfTheProjectionFolderList()
        {
            UseStableCaptureOverrides();
            var kept = new AssetFolderRecord(AssetPath.From("Assets/KeptByJournalReplay"));
            var removed = new AssetFolderRecord(AssetPath.From("Assets/RemovedByJournalReplay"));
            var record = new AssetRecord(
                "folder-journal-replay-guid",
                "Assets/KeptByJournalReplay/Sprite.png",
                "Sprite",
                "Texture2D");
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { record },
                    new[] { kept, removed }),
                Is.True);
            PersistentAssetIndexCache.RecordPendingChanges(
                new ProjectAssetChanges(
                    Array.Empty<string>(),
                    new[] { removed.FolderPath.Value },
                    Array.Empty<string>(),
                    Array.Empty<string>()));
            var index = new InMemoryAssetIndex();
            var source = new ThrowIfEnumeratedSource();
            var synchronizer = new AssetIndexSynchronizer(
                source,
                source,
                index,
                enablePersistentCache: true);
            var completed = false;

            synchronizer.SynchronizeAsync(
                onFullScanStarted: () => Assert.Fail("A durable known folder deletion must not cold scan."),
                onComplete: () => completed = true);
            synchronizer.FlushPendingSnapshotSave();

            Assert.That(completed, Is.True);
            Assert.That(index.TryGetFolder(removed.FolderPath.Value, out _), Is.False);
            // One call only: the projection folder list is handed over once and
            // the source behind it refuses to be enumerated.
            var projectionFolders = synchronizer.GetFolderRecordsForProjection()
                .Select(folder => folder.FolderPath.Value)
                .ToList();
            Assert.That(projectionFolders, Does.Contain(kept.FolderPath.Value));
            Assert.That(
                projectionFolders,
                Does.Not.Contain(removed.FolderPath.Value),
                "the projection must not be handed a folder the journal replay deleted");
        }

        [UnityTest]
        public IEnumerator FolderHandoff_LateImportPreservesIdentity()
        {
            var unaffected = new AssetFolderRecord(
                AssetPath.From("Assets/UnaffectedImportFolder"),
                "unaffected-import-guid",
                new[] { "Shared" });
            var imported = new AssetFolderRecord(
                AssetPath.From("Assets/LateImportedFolder"),
                "late-import-guid",
                new[] { "Gameplay", "Needs Review" });

            yield return AssertFolderHandoffDuringHydratedSave(
                new[] { unaffected },
                new[] { unaffected, imported },
                (handoff, projectionIndex) =>
                {
                    Assert.That(handoff.Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(new[]
                        {
                            unaffected.FolderPath.Value,
                            imported.FolderPath.Value,
                            "Assets",
                            "Packages",
                        }));
                    AssertFolderMetadata(
                        FindFolder(handoff, imported.FolderPath.Value),
                        imported.FolderPath.Value,
                        imported.Guid);
                    Assert.That(FindFolder(handoff, imported.FolderPath.Value).UnityLabels,
                        Is.EqualTo(imported.UnityLabels));
                    Assert.That(projectionIndex.TryGetFolder(imported.FolderPath.Value, out var projected),
                        Is.True);
                    AssertFolderMetadata(projected, imported.FolderPath.Value, imported.Guid);
                    Assert.That(projected.UnityLabels, Is.EqualTo(imported.UnityLabels));

                    var handoffUnaffected = FindFolder(handoff, unaffected.FolderPath.Value);
                    Assert.That(handoffUnaffected, Is.Not.Null);
                    Assert.That(handoffUnaffected.FolderPath.Value,
                        Is.EqualTo(unaffected.FolderPath.Value));
                    Assert.That(handoffUnaffected.Guid, Is.EqualTo(unaffected.Guid));
                    Assert.That(handoffUnaffected.UnityLabels, Is.EqualTo(unaffected.UnityLabels));
                    Assert.That(projectionIndex.TryGetFolder(unaffected.FolderPath.Value,
                        out var projectedUnaffected), Is.True);
                    Assert.That(projectedUnaffected.FolderPath.Value,
                        Is.EqualTo(unaffected.FolderPath.Value));
                    Assert.That(projectedUnaffected.Guid, Is.EqualTo(unaffected.Guid));
                    Assert.That(projectedUnaffected.UnityLabels,
                        Is.EqualTo(unaffected.UnityLabels));
                    Assert.That(projectionIndex.EnumerateFolders().Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(new[]
                        {
                            unaffected.FolderPath.Value,
                            imported.FolderPath.Value,
                            "Assets",
                            "Packages",
                        }));
                });
        }

        [UnityTest]
        public IEnumerator FolderHandoff_LateDeleteStaysDeleted()
        {
            var kept = new AssetFolderRecord(
                AssetPath.From("Assets/KeptLateDeleteFolder"),
                "kept-late-delete-guid",
                new[] { "Shared" });
            var removed = new AssetFolderRecord(
                AssetPath.From("Assets/LateDeletedFolder"),
                "removed-late-delete-guid",
                new[] { "Removed" });

            yield return AssertFolderHandoffDuringHydratedSave(
                new[] { kept, removed },
                new[] { kept },
                (handoff, projectionIndex) =>
                {
                    Assert.That(handoff.Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(new[] { "Assets", "Packages", kept.FolderPath.Value }));
                    Assert.That(projectionIndex.TryGetFolder(removed.FolderPath.Value, out _),
                        Is.False);
                    var handoffKept = FindFolder(handoff, kept.FolderPath.Value);
                    Assert.That(handoffKept, Is.Not.Null);
                    Assert.That(handoffKept.FolderPath.Value, Is.EqualTo(kept.FolderPath.Value));
                    Assert.That(handoffKept.Guid, Is.EqualTo(kept.Guid));
                    Assert.That(handoffKept.UnityLabels, Is.EqualTo(kept.UnityLabels));
                    Assert.That(projectionIndex.TryGetFolder(kept.FolderPath.Value, out var projected),
                        Is.True);
                    Assert.That(projected.FolderPath.Value, Is.EqualTo(kept.FolderPath.Value));
                    Assert.That(projected.Guid, Is.EqualTo(kept.Guid));
                    Assert.That(projected.UnityLabels, Is.EqualTo(kept.UnityLabels));
                    Assert.That(projectionIndex.EnumerateFolders().Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(new[] { "Assets", "Packages", kept.FolderPath.Value }));
                });
        }

        [UnityTest]
        public IEnumerator FolderHandoff_LateMovePreservesIdentity()
        {
            var oldPath = "Assets/LateMovedFolder";
            var unaffected = new AssetFolderRecord(
                AssetPath.From("Assets/UnaffectedMoveFolder"),
                "unaffected-move-guid",
                new[] { "Shared" });
            var original = new AssetFolderRecord(
                AssetPath.From(oldPath),
                "late-move-guid",
                new[] { "Gameplay", "Needs Review" });
            var moved = new AssetFolderRecord(
                AssetPath.From("Assets/LateMovedDestination"),
                original.Guid,
                original.UnityLabels);

            yield return AssertFolderHandoffDuringHydratedSave(
                new[] { unaffected, original },
                new[] { unaffected, moved },
                (handoff, projectionIndex) =>
                {
                    Assert.That(handoff.Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(new[]
                        {
                            unaffected.FolderPath.Value,
                            moved.FolderPath.Value,
                            "Assets",
                            "Packages",
                        }));
                    AssertFolderMetadata(
                        FindFolder(handoff, moved.FolderPath.Value),
                        moved.FolderPath.Value,
                        moved.Guid);
                    Assert.That(FindFolder(handoff, moved.FolderPath.Value).UnityLabels,
                        Is.EqualTo(moved.UnityLabels));
                    Assert.That(projectionIndex.TryGetFolder(oldPath, out _), Is.False);
                    Assert.That(projectionIndex.TryGetFolder(moved.FolderPath.Value, out var projected),
                        Is.True);
                    AssertFolderMetadata(projected, moved.FolderPath.Value, moved.Guid);
                    Assert.That(projected.UnityLabels, Is.EqualTo(moved.UnityLabels));

                    var handoffUnaffected = FindFolder(handoff, unaffected.FolderPath.Value);
                    Assert.That(handoffUnaffected, Is.Not.Null);
                    Assert.That(handoffUnaffected.FolderPath.Value,
                        Is.EqualTo(unaffected.FolderPath.Value));
                    Assert.That(handoffUnaffected.Guid, Is.EqualTo(unaffected.Guid));
                    Assert.That(handoffUnaffected.UnityLabels, Is.EqualTo(unaffected.UnityLabels));
                    Assert.That(projectionIndex.TryGetFolder(unaffected.FolderPath.Value,
                        out var projectedUnaffected), Is.True);
                    Assert.That(projectedUnaffected.FolderPath.Value,
                        Is.EqualTo(unaffected.FolderPath.Value));
                    Assert.That(projectedUnaffected.Guid, Is.EqualTo(unaffected.Guid));
                    Assert.That(projectedUnaffected.UnityLabels,
                        Is.EqualTo(unaffected.UnityLabels));
                    Assert.That(projectionIndex.EnumerateFolders().Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(new[]
                        {
                            unaffected.FolderPath.Value,
                            moved.FolderPath.Value,
                            "Assets",
                            "Packages",
                        }));
                });
        }

        [Test]
        public void FolderHandoff_OneShotFallbackAndDisposal()
        {
            UseStableCaptureOverrides();
            var hydrated = new AssetFolderRecord(AssetPath.From("Assets/HydratedFolder"));
            var current = new AssetFolderRecord(AssetPath.From("Assets/CurrentFolder"));
            var fallback = new AssetFolderRecord(AssetPath.From("Assets/FallbackFolder"));
            Assert.That(PersistentAssetIndexCache.Save(
                Array.Empty<AssetRecord>(),
                new[] { hydrated }), Is.True);

            var index = new InMemoryAssetIndex();
            var source = new AuthoritativeSnapshotSource(
                Array.Empty<AssetRecord>(),
                new[] { fallback });
            var synchronizer = new AssetIndexSynchronizer(
                source, source, index, enablePersistentCache: true);
            try
            {
                var completed = false;
                synchronizer.SynchronizeAsync(
                    () => Assert.Fail("The cached catalog must hydrate."),
                    () => completed = true);
                synchronizer.FlushPendingSnapshotSave();
                Assert.That(completed, Is.True);
                Assert.That(source.EnumerationCount, Is.EqualTo(0),
                    "hydration must not enumerate the fallback source");

                index.ReplaceFolders(new[] { current });
                var first = synchronizer.GetFolderRecordsForProjection();
                Assert.That(source.EnumerationCount, Is.EqualTo(0),
                    "the pending handoff must not enumerate the source");
                index.ReplaceFolders(Array.Empty<AssetFolderRecord>());

                Assert.That(first.Select(folder => folder.FolderPath.Value),
                    Is.EquivalentTo(new[] { "Assets", "Packages", current.FolderPath.Value }),
                    "the handed-off list is a stable snapshot of the current catalog");
                var second = synchronizer.GetFolderRecordsForProjection();
                Assert.That(source.EnumerationCount, Is.EqualTo(1),
                    "only the subsequent fallback may enumerate the source");
                Assert.That(second.Select(folder => folder.FolderPath.Value),
                    Is.EqualTo(new[] { fallback.FolderPath.Value }),
                    "only later calls fall back to the source walk");
            }
            finally
            {
                synchronizer.Dispose();
            }

            Assert.Throws<ObjectDisposedException>(
                () => synchronizer.GetFolderRecordsForProjection());
        }

        [Test]
        public void TraversalLinks_FullCollectorsExcludeLinkedEntries()
        {
            string root = null;
            string outside = null;
            string[] links = null;
            try
            {
                CreateTraversalLinksFixture(out root, out outside, out links);
                foreach (var sourceType in TraversalSourceTypes)
                {
                    InvokeTraversalCollector(sourceType, "CollectCandidatesAndFolders", root, true, out var candidates, out var folders);
                    Assert.That(candidates, Is.EquivalentTo(new[] { NormalizePath(root + "/Normal.txt") }));
                    Assert.That(folders, Is.EquivalentTo(new[]
                    {
                        NormalizePath(root + "/Empty"),
                        NormalizePath(root + "/Normal"),
                    }));
                }
            }
            finally
            {
                CleanupTraversalLinksFixture(root, outside, links);
            }
        }

        [Test]
        public void TraversalLinks_FolderCollectorsExcludeLinkedDirectories()
        {
            string root = null;
            string outside = null;
            string[] links = null;
            try
            {
                CreateTraversalLinksFixture(out root, out outside, out links);
                foreach (var sourceType in TraversalSourceTypes)
                {
                    InvokeTraversalCollector(sourceType, "CollectFoldersOnly", root, false, out _, out var folders);
                    Assert.That(folders, Is.EquivalentTo(new[]
                    {
                        NormalizePath(root + "/Empty"),
                        NormalizePath(root + "/Normal"),
                    }));
                }
            }
            finally
            {
                CleanupTraversalLinksFixture(root, outside, links);
            }
        }

        [Test]
        public void TraversalLinks_SelectedLinkedRootFails()
        {
            string root = null;
            string outside = null;
            string[] links = null;
            try
            {
                CreateTraversalLinksFixture(out root, out outside, out links);
                var linkedRoot = links[0];
                foreach (var sourceType in TraversalSourceTypes)
                {
                    foreach (var collector in new[] { "CollectCandidatesAndFolders", "CollectFoldersOnly" })
                    {
                        var exception = Assert.Throws<IOException>(() =>
                            InvokeTraversalCollector(sourceType, collector, linkedRoot, collector == "CollectCandidatesAndFolders", out _, out _));
                        Assert.That(exception.Message, Does.Contain(linkedRoot));
                        var existingParent = Path.Combine(root, "Empty");
                        Assert.That(Directory.Exists(existingParent), Is.True);
                        var missingLeaf = Path.Combine(existingParent, "MissingLeaf");
                        Assert.Throws<DirectoryNotFoundException>(() =>
                            InvokeTraversalCollector(sourceType, collector, missingLeaf,
                                collector == "CollectCandidatesAndFolders", out _, out _));
                    }

                    Assert.DoesNotThrow(() =>
                        InvokeTraversalCollector(sourceType, "CollectCandidatesAndFolders", root, true, out _, out _));
                }
            }
            finally
            {
                CleanupTraversalLinksFixture(root, outside, links);
            }
        }

        [UnityTest]
        public IEnumerator TraversalLinks_SynchronizerPublishesContainedCatalog()
        {
            string root = null;
            string outside = null;
            string[] links = null;
            AssetIndexSynchronizer nativeSynchronizer = null;
            AssetIndexSynchronizer hybridSynchronizer = null;
            var autoRefreshSuspended = false;
            try
            {
                root = "Assets/__ZepTraversalPublication_" + Guid.NewGuid().ToString("N");
                var assetPath = root + "/Normal.txt";
                var folderPaths = new[] { root, root + "/Normal", root + "/Empty" };
                Directory.CreateDirectory(folderPaths[1]);
                Directory.CreateDirectory(folderPaths[2]);
                File.WriteAllText(assetPath, "normal imported traversal asset");
                AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
                var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
                Assert.That(assetGuid, Is.Not.Empty);
                var folderGuids = folderPaths.ToDictionary(path => path, AssetDatabase.AssetPathToGUID);
                foreach (var guid in folderGuids.Values) Assert.That(guid, Is.Not.Empty);

                outside = Path.Combine(Path.GetTempPath(), "__ZepTraversalPublicationOutside_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "Outside.txt"), "outside target must never enter the catalog");
                File.WriteAllText(Path.Combine(outside, "Outside.txt.meta"),
                    "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                links = new[]
                {
                    Path.GetFullPath(root + "/Outside"),
                    Path.GetFullPath(root + "/Cycle"),
                    Path.GetFullPath(root + "/Linked.txt"),
                    Path.GetFullPath(root + "/Linked.txt.meta"),
                };
                // No editor refresh/import may observe the cycle links. All GUIDs
                // were captured above, and refresh is restored only after unlinking.
                AssetDatabase.DisallowAutoRefresh();
                autoRefreshSuspended = true;
                CreateSymbolicLink(links[0], outside);
                CreateSymbolicLink(links[1], Path.GetFullPath(root));
                CreateSymbolicLink(links[2], Path.GetFullPath(assetPath));
                CreateSymbolicLink(links[3], Path.GetFullPath(assetPath + ".meta"));

                var nativeIndex = new InMemoryAssetIndex();
                nativeSynchronizer = new AssetIndexSynchronizer(
                    new HybridAssetRecordSource(), new NativeFileSystemAssetRecordSource(),
                    nativeIndex, enablePersistentCache: false);
                var nativeCompletions = 0;
                nativeSynchronizer.SynchronizeAsync(null, () => nativeCompletions++);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (nativeCompletions == 0 && wait.Elapsed < TimeSpan.FromSeconds(30))
                {
                    yield return null;
                }
                Assert.That(nativeCompletions, Is.EqualTo(1),
                    $"Native traversal did not complete exactly once; elapsed={wait.Elapsed}, completions={nativeCompletions}, strategy={nativeSynchronizer.LastEnumerationStrategy}.");
                Assert.That(nativeSynchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async),
                    "native enumeration must succeed without the hybrid fallback hiding a traversal failure");
                AssertContainedPublication(nativeIndex, nativeSynchronizer);
                nativeSynchronizer.Dispose();
                nativeSynchronizer = null;

                var hybridIndex = new InMemoryAssetIndex();
                var hybridSource = new HybridAssetRecordSource();
                hybridSynchronizer = new AssetIndexSynchronizer(
                    hybridSource, hybridSource, hybridIndex, enablePersistentCache: false);
                var hybridCompletions = 0;
                hybridSynchronizer.Synchronize(() => hybridCompletions++);
                Assert.That(hybridCompletions, Is.EqualTo(1));
                AssertContainedPublication(hybridIndex, hybridSynchronizer);

                var observation = new NativeDirectFolderAssetSource().Observe(AssetPath.From(root));
                Assert.That(observation.IsComplete, Is.True);
                Assert.That(observation.Entries.Select(entry => entry.Path.Value), Is.EquivalentTo(new[]
                {
                    assetPath, root + "/Normal", root + "/Empty",
                }), "direct browsing and both authoritative publications share the link-exclusion policy");
                Assert.That(observation.Entries.Single(entry => entry.Path.Value == assetPath).IsFolder, Is.False);
                Assert.That(observation.Entries.Where(entry => entry.Path.Value != assetPath).All(entry => entry.IsFolder), Is.True);
                Assert.That(nativeCompletions, Is.EqualTo(1));

                void AssertContainedPublication(InMemoryAssetIndex index, AssetIndexSynchronizer synchronizer)
                {
                    Assert.That(index.Enumerate().Where(record => record.AssetPath.StartsWith(root + "/", StringComparison.Ordinal))
                        .Select(record => record.AssetPath), Is.EquivalentTo(new[] { assetPath }));
                    Assert.That(index.TryGetByPath(assetPath, out var asset), Is.True);
                    Assert.That(asset.Guid, Is.EqualTo(assetGuid));
                    Assert.That(index.EnumerateFolders().Where(folder => folder.FolderPath.Value == root
                        || folder.FolderPath.Value.StartsWith(root + "/", StringComparison.Ordinal))
                        .Select(folder => folder.FolderPath.Value), Is.EquivalentTo(folderPaths));
                    foreach (var path in folderPaths)
                    {
                        Assert.That(index.TryGetFolder(path, out var folder), Is.True);
                        Assert.That(folder.Guid, Is.EqualTo(folderGuids[path]));
                    }

                    index.ReplaceFolders(synchronizer.GetFolderRecordsForProjection());
                    var allPublishedPaths = index.Enumerate().Select(record => record.AssetPath)
                        .Concat(index.EnumerateFolders().Select(folder => folder.FolderPath.Value)).ToArray();
                    foreach (var linkedPath in new[] { root + "/Outside", root + "/Cycle", root + "/Linked.txt" })
                    {
                        Assert.That(allPublishedPaths.Any(path => path == linkedPath
                            || path.StartsWith(linkedPath + "/", StringComparison.Ordinal)), Is.False,
                            "linked paths and descendants must be absent from the entire published catalog");
                    }
                    Assert.That(index.EnumerateFolders().Where(folder => folder.FolderPath.Value == root
                        || folder.FolderPath.Value.StartsWith(root + "/", StringComparison.Ordinal))
                        .Select(folder => folder.FolderPath.Value), Is.EquivalentTo(folderPaths));
                    foreach (var path in folderPaths)
                    {
                        Assert.That(index.TryGetFolder(path, out var folder), Is.True);
                        Assert.That(folder.Guid, Is.EqualTo(folderGuids[path]));
                    }
                    Assert.That(index.TryGetFolder("Assets", out _), Is.True);
                    Assert.That(index.TryGetFolder("Packages", out _), Is.True);
                }
            }
            finally
            {
                try
                {
                    nativeSynchronizer?.Dispose();
                }
                finally
                {
                    try
                    {
                        hybridSynchronizer?.Dispose();
                    }
                    finally
                    {
                        var unlinkFailures = new List<Exception>();
                        if (links != null)
                        {
                            foreach (var link in links)
                            {
                                try
                                {
                                    FileAttributes attributes;
                                    try { attributes = File.GetAttributes(link); }
                                    catch (FileNotFoundException) { continue; }
                                    catch (DirectoryNotFoundException) { continue; }
                                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                                        throw new IOException("Expected a fixture link before unlinking: " + link);
                                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(link);
                                    else File.Delete(link);
                                }
                                catch (Exception exception)
                                {
                                    unlinkFailures.Add(exception);
                                }
                            }
                        }
                        // If unlinking failed, retain targets and keep refresh suspended;
                        // recursively deleting or importing a remaining cycle is unsafe.
                        if (unlinkFailures.Count > 0) throw new AggregateException(unlinkFailures);
                        try
                        {
                            if (!string.IsNullOrEmpty(outside) && Directory.Exists(outside))
                                Directory.Delete(outside, recursive: true);
                        }
                        finally
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(root)) AssetDatabase.DeleteAsset(root);
                            }
                            finally
                            {
                                if (autoRefreshSuspended) AssetDatabase.AllowAutoRefresh();
                            }
                        }
                    }
                }
            }
        }

        // One entry, not two: NativeFileSystemAssetRecordSource declares the package's
        // only pair of walks and HybridAssetRecordSource calls them, so there is one
        // implementation of these link and failure rules to hold to. That Hybrid really
        // routes through them is covered end to end by DirectoryFailure_*CollectorsReject*
        // and TraversalLinks_SynchronizerPublishesContainedCatalog, which drive its own
        // public entry points.
        private static readonly Type[] TraversalSourceTypes =
        {
            typeof(NativeFileSystemAssetRecordSource),
        };

        private static void CreateTraversalLinksFixture(out string root, out string outside, out string[] links)
        {
            root = Path.Combine(Path.GetTempPath(), "__ZepTraversal_" + Guid.NewGuid().ToString("N"));
            outside = Path.Combine(Path.GetTempPath(), "__ZepTraversalOutside_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "Normal"));
            Directory.CreateDirectory(Path.Combine(root, "Empty"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(root, "Normal.txt"), "normal");
            File.WriteAllText(Path.Combine(outside, "Outside.txt"), "outside");

            links = new[]
            {
                Path.Combine(root, "Outside"),
                Path.Combine(root, "Cycle"),
                Path.Combine(root, "Linked.txt"),
            };
            CreateSymbolicLink(links[0], outside);
            CreateSymbolicLink(links[1], root);
            CreateSymbolicLink(links[2], Path.Combine(root, "Normal.txt"));
        }

        private static void CleanupTraversalLinksFixture(string root, string outside, string[] links)
        {
            if (links != null)
            {
                for (var i = 0; i < links.Length; i++)
                {
                    if (Directory.Exists(links[i]))
                    {
                        Directory.Delete(links[i]);
                    }
                    else if (File.Exists(links[i]))
                    {
                        File.Delete(links[i]);
                    }
                }
            }

            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (!string.IsNullOrEmpty(outside) && Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }

        private static void CreateSymbolicLink(string linkPath, string targetPath)
        {
            var quote = new Func<string, string>(path =>
                "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/ln",
                Arguments = "-s " + quote(targetPath) + " " + quote(linkPath),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                Assert.That(process, Is.Not.Null);
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.EqualTo(0), "ln failed for test-created link.");
            }

            Assert.That((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0, Is.True);
        }

        private static void InvokeTraversalCollector(
            Type sourceType,
            string methodName,
            string root,
            bool candidatesAndFolders,
            out List<string> candidates,
            out List<string> folders)
        {
            candidates = new List<string>();
            folders = new List<string>();
            var method = sourceType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            try
            {
                method.Invoke(null, candidatesAndFolders
                    ? new object[] { root, candidates, folders }
                    : new object[] { root, folders });
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException;
            }
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

        [Test]
        public void DirectoryFailure_FullCollectorsRejectUnreadableSubtree()
        {
            string root = null;
            string denied = null;
            try
            {
                CreateDirectoryFailureFixture(out root, out denied, out _);
                SetDirectoryMode(denied, "000");
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetDirectories(denied));
                Assert.Throws<UnauthorizedAccessException>(() => new HybridAssetRecordSource().GetRecords().ToList());
                Assert.Throws<UnauthorizedAccessException>(() => new NativeFileSystemAssetRecordSource().GetRecords().ToList());
            }
            finally
            {
                CleanupDirectoryFailureFixture(root, denied);
            }
        }

        [Test]
        public void DirectoryFailure_FolderCollectorsRejectUnreadableSubtree()
        {
            string root = null;
            string denied = null;
            try
            {
                CreateDirectoryFailureFixture(out root, out denied, out _);
                SetDirectoryMode(denied, "000");
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetDirectories(denied));
                Assert.Throws<UnauthorizedAccessException>(() => new HybridAssetRecordSource().GetFolderRecords());
                Assert.Throws<UnauthorizedAccessException>(() => new NativeFileSystemAssetRecordSource().GetFolderRecords());
            }
            finally
            {
                CleanupDirectoryFailureFixture(root, denied);
            }
        }

        [Test]
        public void DirectoryFailure_MissingSelectedRootFails()
        {
            var missing = Path.Combine(Path.GetTempPath(), "__ZepMissing_" + Guid.NewGuid().ToString("N"));
            var empty = Path.Combine(Path.GetTempPath(), "__ZepEmpty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try
            {
                AssertCollectorFailure(typeof(NativeFileSystemAssetRecordSource), "CollectCandidatesAndFolders", missing, true);
                AssertCollectorFailure(typeof(NativeFileSystemAssetRecordSource), "CollectFoldersOnly", missing, false);

                AssertCollectorSucceedsEmpty(typeof(NativeFileSystemAssetRecordSource), "CollectCandidatesAndFolders", empty, true);
                AssertCollectorSucceedsEmpty(typeof(NativeFileSystemAssetRecordSource), "CollectFoldersOnly", empty, false);
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }

        [UnityTest]
        public IEnumerator DirectoryFailure_AsyncFallbackPreservesCatalogAndRecovers()
        {
            string root = null;
            string denied = null;
            string assetPath = null;
            AssetIndexSynchronizer synchronizer = null;
            try
            {
                CreateDirectoryFailureFixture(out root, out denied, out assetPath);
                var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
                var folderGuid = AssetDatabase.AssetPathToGUID(root);
                var seeded = new AssetRecord(
                    "directory-failure-seeded-guid",
                    "Assets/DirectoryFailureSeeded.asset",
                    "DirectoryFailureSeeded",
                    "TestAsset",
                    "seed tag text",
                    new[] { "seed-first", "seed-second" });
                var seededFolder = new AssetFolderRecord(
                    AssetPath.From("Assets/DirectoryFailureSeededFolder"),
                    "directory-failure-seeded-folder-guid",
                    new[] { "folder-first", "folder-second" });
                var index = new InMemoryAssetIndex();
                index.Replace(new[] { seeded }, new[] { seededFolder });
                var seededAssetPaths = index.Enumerate().Select(record => record.AssetPath).ToArray();
                var seededFolderPaths = index.EnumerateFolders().Select(folder => folder.FolderPath.Value).ToArray();
                synchronizer = new AssetIndexSynchronizer(
                    new HybridAssetRecordSource(),
                    new NativeFileSystemAssetRecordSource(),
                    index,
                    enablePersistentCache: false);
                var completions = 0;

                SetDirectoryMode(denied, "000");
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetDirectories(denied));
                LogAssert.Expect(LogType.Warning, new Regex("^\\[Zep\\] Async synchronize failed \\(UnauthorizedAccessException: .+\\); falling back to sync\\.$"));
                LogAssert.Expect(LogType.Warning, new Regex("^\\[Zep\\] Synchronous synchronization failed \\(UnauthorizedAccessException: .+\\); the current synchronization was retired and can be retried\\.$"));
                synchronizer.SynchronizeAsync(null, () => completions++);
                var failedOperationWait = System.Diagnostics.Stopwatch.StartNew();
                while (completions == 0 && failedOperationWait.Elapsed < TimeSpan.FromSeconds(30))
                {
                    yield return null;
                }

                failedOperationWait.Stop();
                Assert.That(completions, Is.EqualTo(1),
                    $"Timed out waiting for failed synchronization; elapsed={failedOperationWait.Elapsed}, completions={completions}, strategy={synchronizer.LastEnumerationStrategy}.");
                Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.SyncFallback));
                Assert.That(index.Enumerate().Select(record => record.AssetPath), Is.EquivalentTo(seededAssetPaths));
                Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value), Is.EquivalentTo(seededFolderPaths));
                Assert.That(index.TryGetByPath(seeded.AssetPath, out var preserved), Is.True);
                Assert.That(preserved.AssetPath, Is.EqualTo(seeded.AssetPath));
                Assert.That(preserved.Guid, Is.EqualTo(seeded.Guid));
                Assert.That(preserved.TagSearchText, Is.EqualTo("seed tag text"));
                Assert.That(preserved.UnityLabels, Is.EqualTo(new[] { "seed-first", "seed-second" }));
                Assert.That(index.TryGetFolder(seededFolder.FolderPath.Value, out var preservedFolder), Is.True);
                Assert.That(preservedFolder.FolderPath, Is.EqualTo(seededFolder.FolderPath));
                Assert.That(preservedFolder.Guid, Is.EqualTo(seededFolder.Guid));
                Assert.That(preservedFolder.UnityLabels, Is.EqualTo(new[] { "folder-first", "folder-second" }));

                SetDirectoryMode(denied, "700");
                Assert.DoesNotThrow(() => Directory.GetDirectories(denied));
                synchronizer.SynchronizeAsync(null, () => completions++);
                var recoveryWait = System.Diagnostics.Stopwatch.StartNew();
                while (completions < 2 && recoveryWait.Elapsed < TimeSpan.FromSeconds(30))
                {
                    yield return null;
                }

                recoveryWait.Stop();
                Assert.That(completions, Is.EqualTo(2),
                    $"Timed out waiting for recovery synchronization; elapsed={recoveryWait.Elapsed}, completions={completions}, strategy={synchronizer.LastEnumerationStrategy}.");
                Assert.That(index.TryGetByPath(seeded.AssetPath, out _), Is.False);
                Assert.That(index.TryGetByPath(assetPath, out var recovered), Is.True);
                Assert.That(recovered.Guid, Is.EqualTo(assetGuid));
                Assert.That(recovered.UnityLabels, Is.EqualTo(new[] { "asset-first", "asset-second" }));
                Assert.That(index.TryGetFolder(seededFolder.FolderPath.Value, out _), Is.False);
                var recoveredFolders = synchronizer.GetFolderRecordsForProjection();
                index.ReplaceFolders(recoveredFolders);
                Assert.That(index.TryGetFolder(seededFolder.FolderPath.Value, out _), Is.False);
                Assert.That(index.TryGetFolder(root, out var recoveredFolder), Is.True);
                Assert.That(recoveredFolder.Guid, Is.EqualTo(folderGuid));
                Assert.That(recoveredFolder.UnityLabels, Is.EqualTo(new[] { "folder-first", "folder-second" }));
                Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value), Does.Contain("Assets"));
                Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value), Does.Contain("Packages"));
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(denied))
                    {
                        SetDirectoryMode(denied, "700");
                    }
                }
                finally
                {
                    try
                    {
                        synchronizer?.Dispose();
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(root))
                        {
                            AssetDatabase.DeleteAsset(root);
                        }
                    }
                }
            }
        }

        private static void CreateDirectoryFailureFixture(out string root, out string denied, out string assetPath)
        {
            root = "Assets/__ZepDirectoryFailure_" + Guid.NewGuid().ToString("N");
            denied = root + "/Denied";
            assetPath = root + "/Readable.asset.txt";
            var nestedAssetPath = denied + "/Nested.asset.txt";
            Directory.CreateDirectory(denied);
            File.WriteAllText(assetPath, "directory failure fixture");
            File.WriteAllText(nestedAssetPath, "unreadable fixture");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(nestedAssetPath, ImportAssetOptions.ForceSynchronousImport);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            var nestedAsset = AssetDatabase.LoadMainAssetAtPath(nestedAssetPath);
            var folder = AssetDatabase.LoadMainAssetAtPath(root);
            Assert.That(asset, Is.Not.Null);
            Assert.That(nestedAsset, Is.Not.Null);
            Assert.That(folder, Is.Not.Null);
            AssetDatabase.SetLabels(asset, new[] { "asset-first", "asset-second" });
            AssetDatabase.SetLabels(nestedAsset, new[] { "nested-first", "nested-second" });
            AssetDatabase.SetLabels(folder, new[] { "folder-first", "folder-second" });
            AssetDatabase.SaveAssets();
        }

        private static void CleanupDirectoryFailureFixture(string root, string denied)
        {
            try
            {
                if (!string.IsNullOrEmpty(denied))
                {
                    SetDirectoryMode(denied, "700");
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(root))
                {
                    AssetDatabase.DeleteAsset(root);
                }
            }
        }

        private static void SetDirectoryMode(string path, string mode)
        {
            var escapedPath = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/chmod",
                Arguments = mode + " \"" + escapedPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                Assert.That(process, Is.Not.Null);
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.EqualTo(0), "chmod failed for test-created directory.");
            }
        }

        private static void AssertCollectorFailure(Type sourceType, string methodName, string root, bool candidatesAndFolders)
        {
            var exception = Assert.Throws<DirectoryNotFoundException>(
                () => InvokeCollector(sourceType, methodName, root, candidatesAndFolders));
            Assert.That(exception, Is.Not.Null);
        }

        private static void AssertCollectorSucceedsEmpty(Type sourceType, string methodName, string root, bool candidatesAndFolders)
        {
            Assert.DoesNotThrow(() => InvokeCollector(sourceType, methodName, root, candidatesAndFolders));
        }

        private static void InvokeCollector(Type sourceType, string methodName, string root, bool candidatesAndFolders)
        {
            var method = sourceType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var candidates = new List<string>();
            var folders = new List<string>();
            try
            {
                method.Invoke(null, candidatesAndFolders
                    ? new object[] { root, candidates, folders }
                    : new object[] { root, folders });
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException;
            }

            Assert.That(candidates, Is.Empty);
            Assert.That(folders, Is.Empty);
        }

        // The counters record only while the perf toggle is on, so every assertion on
        // them has to own the window it measures. Same shape as the helper the
        // controller fixture already uses (ZepControllerPaneProjectionTests
        // .MeasurePerfCounters), so the two sides of AC3 are measured identically.
        private static ZepPerfSnapshot MeasurePerfCounters(Action work)
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                work();
                return ZepPerfLog.CaptureSnapshot();
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
            }
        }

        private static void UseStableCaptureOverrides()
        {
            PersistentAssetIndexCache.UnityLabelSnapshotOverride = () =>
                new Dictionary<string, IReadOnlyList<string>>();
            PersistentAssetIndexCache.AuthoritativeTypeSnapshotOverride = () =>
                new Dictionary<string, AssetTypeMetadata>();
            PersistentAssetIndexCache.ArtifactDependencyVersionOverride = () => 42;
        }

        private sealed class ThrowIfEnumeratedSource : IAssetRecordSource
        {
            public bool IsThreadSafe => false;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null) =>
                throw new AssertionException("Cache hit unexpectedly prepared a full scan.");

            public IEnumerable<AssetRecord> GetRecords() => throw new AssertionException("Cache hit unexpectedly enumerated records.");

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => throw new AssertionException("Cache hit unexpectedly enumerated folders.");

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => throw new AssertionException("Cache hit unexpectedly built a folder record.");

            public AssetRecord BuildRecord(AssetPath assetPath) => throw new AssertionException("Cache hit unexpectedly built a record.");
        }

        private sealed class RecordingSource : IAssetRecordSource
        {
            private readonly List<string> _events;

            public RecordingSource(List<string> events)
            {
                _events = events;
            }

            public bool IsThreadSafe => false;

            public IReadOnlyDictionary<string, IReadOnlyList<string>> PreparedUnityLabelsByGuid { get; private set; }

            public IReadOnlyDictionary<string, AssetTypeMetadata> PreparedTypesByGuid { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                PreparedUnityLabelsByGuid = unityLabelsByGuid;
                PreparedTypesByGuid = typesByGuid;
                _events.Add("prepare");
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                _events.Add("enumerate");
                yield return new AssetRecord(
                    "scanned-guid",
                    "Assets/Scanned.asset",
                    "Scanned",
                    "TestAsset");
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class KnownChangeSource : IAssetRecordSource
        {
            private readonly AssetRecord _record;
            private readonly List<string> _events;

            internal KnownChangeSource(AssetRecord record, List<string> events)
            {
                _record = record;
                _events = events;
            }

            public bool IsThreadSafe => false;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                _events.Add("prepare");
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                _events.Add("enumerate");
                yield return _record;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() =>
                Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath)
            {
                _events.Add("build-record");
                return string.Equals(
                    assetPath.Value,
                    _record.AssetPath,
                    StringComparison.OrdinalIgnoreCase)
                    ? _record
                    : null;
            }
        }

        // BuildRecord costs more than one main-thread budget slice, so a budgeted
        // reconciliation is forced to hand the frame back between records.
        private sealed class SlowTargetedRecordSource : IAssetRecordSource
        {
            private readonly Dictionary<string, AssetRecord> _records;
            private readonly int _buildDelayMilliseconds;

            internal SlowTargetedRecordSource(
                IReadOnlyList<AssetRecord> records,
                int buildDelayMilliseconds)
            {
                _records = records.ToDictionary(
                    record => record.AssetPath,
                    StringComparer.OrdinalIgnoreCase);
                _buildDelayMilliseconds = buildDelayMilliseconds;
            }

            public bool IsThreadSafe => false;

            public int EnumerationCount { get; private set; }

            public int BuildRecordCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                EnumerationCount++;
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationCount++;
                return _records.Values;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
            {
                EnumerationCount++;
                return Array.Empty<AssetFolderRecord>();
            }

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath)
            {
                BuildRecordCount++;
                Thread.Sleep(_buildDelayMilliseconds);
                return _records.TryGetValue(assetPath.Value, out var record)
                    ? record
                    : null;
            }
        }

        private sealed class TargetedRecordSource : IAssetRecordSource
        {
            private readonly Dictionary<string, AssetRecord> _records;

            internal TargetedRecordSource(IReadOnlyList<AssetRecord> records)
            {
                _records = records.ToDictionary(
                    record => record.AssetPath,
                    StringComparer.OrdinalIgnoreCase);
            }

            public bool IsThreadSafe => false;

            public int EnumerationCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                EnumerationCount++;
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationCount++;
                return _records.Values;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
            {
                EnumerationCount++;
                return Array.Empty<AssetFolderRecord>();
            }

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) =>
                _records.TryGetValue(assetPath.Value, out var record)
                    ? record
                    : null;
        }

        // One folder's direct children, as the browser's observer would report
        // them: filesystem facts only, no GUIDs.
        private sealed class RecordedDirectFolderSource : IDirectFolderAssetSource
        {
            private readonly Dictionary<string, List<DirectFolderEntry>> _entries =
                new Dictionary<string, List<DirectFolderEntry>>(
                    StringComparer.OrdinalIgnoreCase);

            internal void Add(string folderPath, params (string path, bool isFolder)[] children)
            {
                var list = new List<DirectFolderEntry>(children.Length);
                for (var i = 0; i < children.Length; i++)
                {
                    list.Add(new DirectFolderEntry(
                        AssetPath.From(children[i].path),
                        children[i].isFolder,
                        System.IO.Path.GetFileName(children[i].path),
                        children[i].isFolder ? "Folder" : "TestAsset"));
                }

                _entries[folderPath] = list;
            }

            public DirectFolderObservation Observe(AssetPath folderPath)
            {
                return _entries.TryGetValue(folderPath.Value, out var list)
                    ? new DirectFolderObservation(folderPath, list, isComplete: true)
                    : new DirectFolderObservation(
                        folderPath,
                        Array.Empty<DirectFolderEntry>(),
                        isComplete: false);
            }
        }

        private sealed class AuthoritativeSnapshotSource : IAssetRecordSource
        {
            private readonly IReadOnlyList<AssetRecord> _records;
            private readonly IReadOnlyList<AssetFolderRecord> _folders;
            private readonly Dictionary<string, AssetRecord> _recordsByPath;
            private readonly Dictionary<string, AssetFolderRecord> _foldersByPath;
            private readonly string _throwRecordPath;
            private readonly string _throwFolderPath;

            internal AuthoritativeSnapshotSource(
                IReadOnlyList<AssetRecord> records,
                IReadOnlyList<AssetFolderRecord> folders,
                string throwRecordPath = null,
                string throwFolderPath = null)
            {
                _records = records;
                _folders = folders;
                _recordsByPath = records.ToDictionary(
                    record => record.AssetPath,
                    StringComparer.OrdinalIgnoreCase);
                _foldersByPath = folders.ToDictionary(
                    folder => folder.FolderPath.Value,
                    StringComparer.OrdinalIgnoreCase);
                _throwRecordPath = throwRecordPath;
                _throwFolderPath = throwFolderPath;
            }

            public bool IsThreadSafe => false;

            public int EnumerationCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                EnumerationCount++;
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationCount++;
                return _records;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
            {
                EnumerationCount++;
                return _folders;
            }

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath)
            {
                if (string.Equals(
                    folderPath.Value,
                    _throwFolderPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("simulated folder resolution failure");
                }

                return _foldersByPath.TryGetValue(folderPath.Value, out var folder)
                    ? folder
                    : null;
            }

            public AssetRecord BuildRecord(AssetPath assetPath)
            {
                if (string.Equals(
                    assetPath.Value,
                    _throwRecordPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("simulated record resolution failure");
                }

                return _recordsByPath.TryGetValue(assetPath.Value, out var record)
                    ? record
                    : null;
            }
        }

        private sealed class ThrowingTargetedRecordSource : IAssetRecordSource
        {
            private readonly IReadOnlyList<AssetRecord> _records;

            internal ThrowingTargetedRecordSource(IReadOnlyList<AssetRecord> records)
            {
                _records = records;
            }

            public bool IsThreadSafe => false;

            public int EnumerationCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                EnumerationCount++;
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationCount++;
                return _records;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() =>
                Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath)
            {
                for (var i = 0; i < _records.Count; i++)
                {
                    if (string.Equals(
                        _records[i].AssetPath,
                        assetPath.Value,
                        StringComparison.OrdinalIgnoreCase)
                        && assetPath.Value.EndsWith(
                            ".cs",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return _records[i];
                    }
                }

                throw new InvalidOperationException(
                    "simulated targeted metadata failure");
            }
        }

        private sealed class FolderTrackingSource : IAssetRecordSource
        {
            private readonly bool _isThreadSafe;
            private readonly AssetRecord _record;
            private readonly IReadOnlyList<AssetFolderRecord> _folders;

            public FolderTrackingSource(
                bool isThreadSafe,
                string guid,
                string assetPath,
                AssetFolderRecord folder)
            {
                _isThreadSafe = isThreadSafe;
                _record = new AssetRecord(guid, assetPath, "Fixture", "TestAsset");
                _folders = new[] { folder };
            }

            public bool IsThreadSafe => _isThreadSafe;

            public int GetFolderRecordsCallCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                yield return _record;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
            {
                GetFolderRecordsCallCount++;
                return _folders;
            }

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class TypeMetadataRequiredSource : IAssetRecordSource
        {
            public bool IsThreadSafe => true;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                if (typesByGuid == null)
                {
                    throw new InvalidOperationException("missing type metadata");
                }
            }

            public IEnumerable<AssetRecord> GetRecords() => Array.Empty<AssetRecord>();

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class MutableSnapshotSource : IAssetRecordSource
        {
            private IReadOnlyList<AssetRecord> _records;

            public MutableSnapshotSource(IReadOnlyList<AssetRecord> records)
            {
                SetRecords(records);
            }

            public bool IsThreadSafe => false;

            public void SetRecords(IReadOnlyList<AssetRecord> records)
            {
                _records = records == null
                    ? Array.Empty<AssetRecord>()
                    : records.ToArray();
            }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords() => _records;

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class FaultOnceSynchronousSource : IAssetRecordSource
        {
            private readonly AssetRecord _record;
            private bool _mustThrow = true;

            internal FaultOnceSynchronousSource(AssetRecord record)
            {
                _record = record;
            }

            public bool IsThreadSafe => false;

            internal int EnumerationCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationCount++;
                if (_mustThrow)
                {
                    _mustThrow = false;
                    throw new IOException("transient synchronous enumeration");
                }

                return new[] { _record };
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() =>
                Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class MalformedCompletionSource : IAssetRecordSource, IDisposable
        {
            private readonly IReadOnlyList<AssetRecord> _records;
            private readonly MalformedCompletionFolders _folders;
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

            public MalformedCompletionSource(IReadOnlyList<AssetRecord> records, IReadOnlyList<AssetFolderRecord> folders)
            {
                _records = records.ToArray();
                _folders = new MalformedCompletionFolders(folders.ToArray(), EnumerationFinished);
            }

            public bool IsThreadSafe => true;
            public ManualResetEventSlim EnumerationStarted { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim EnumerationFinished { get; } = new ManualResetEventSlim(false);
            public void ReleaseEnumeration() => _release.Set();
            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null) { }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationStarted.Set();
                try
                {
                    if (!_release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("The test did not release the blocking asset enumeration.");
                    return _records;
                }
                catch
                {
                    EnumerationFinished.Set();
                    throw;
                }
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => _folders;
            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;
            public AssetRecord BuildRecord(AssetPath assetPath) => null;
            public void Dispose()
            {
                _release.Dispose();
                EnumerationStarted.Dispose();
                EnumerationFinished.Dispose();
            }
        }

        private sealed class MalformedCompletionFolders : IReadOnlyList<AssetFolderRecord>
        {
            private readonly AssetFolderRecord[] _folders;
            private readonly ManualResetEventSlim _finished;
            public MalformedCompletionFolders(AssetFolderRecord[] folders, ManualResetEventSlim finished)
            {
                _folders = folders;
                _finished = finished;
            }
            public int Count => _folders.Length;
            public AssetFolderRecord this[int index] => _folders[index];
            public IEnumerator<AssetFolderRecord> GetEnumerator()
            {
                try
                {
                    foreach (var folder in _folders) yield return folder;
                }
                finally
                {
                    // Record enumeration precedes folder enumeration; after this boundary
                    // the worker uses only its captured immutable snapshots.
                    _finished.Set();
                }
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class BlockingSnapshotSource : IAssetRecordSource
        {
            private readonly IReadOnlyList<AssetRecord> _records;
            private readonly IReadOnlyList<AssetFolderRecord> _folders;
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

            public BlockingSnapshotSource(
                IReadOnlyList<AssetRecord> records,
                IReadOnlyList<AssetFolderRecord> folders = null)
            {
                _records = records == null
                    ? Array.Empty<AssetRecord>()
                    : records.ToArray();
                _folders = folders == null
                    ? Array.Empty<AssetFolderRecord>()
                    : folders.ToArray();
            }

            public bool IsThreadSafe => true;

            public ManualResetEventSlim EnumerationStarted { get; } = new ManualResetEventSlim(false);

            public void ReleaseEnumeration() => _release.Set();

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                EnumerationStarted.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the blocking asset enumeration.");
                }

                return _records;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => _folders;

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class CountingThreadSafeSource : IAssetRecordSource
        {
            private readonly IReadOnlyList<AssetRecord> _records;

            internal CountingThreadSafeSource(IReadOnlyList<AssetRecord> records)
            {
                _records = records;
            }

            public bool IsThreadSafe => true;

            public int PrepareCount { get; private set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                PrepareCount++;
            }

            public IEnumerable<AssetRecord> GetRecords() => _records;

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() =>
                Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class SequencedBlockingSnapshotSource : IAssetRecordSource
        {
            private readonly IReadOnlyList<AssetRecord> _first;
            private readonly IReadOnlyList<AssetRecord> _second;
            private readonly IReadOnlyList<AssetFolderRecord> _firstFolders;
            private readonly IReadOnlyList<AssetFolderRecord> _secondFolders;
            private readonly ManualResetEventSlim _releaseFirst =
                new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _releaseSecond =
                new ManualResetEventSlim(false);
            private int _enumerationCount;
            private int _activeEnumeration;

            internal SequencedBlockingSnapshotSource(
                IReadOnlyList<AssetRecord> first,
                IReadOnlyList<AssetRecord> second,
                IReadOnlyList<AssetFolderRecord> firstFolders = null,
                IReadOnlyList<AssetFolderRecord> secondFolders = null)
            {
                _first = first;
                _second = second;
                _firstFolders = firstFolders ?? Array.Empty<AssetFolderRecord>();
                _secondFolders = secondFolders ?? Array.Empty<AssetFolderRecord>();
            }

            public bool IsThreadSafe => true;

            public ManualResetEventSlim FirstStarted { get; } =
                new ManualResetEventSlim(false);

            public ManualResetEventSlim SecondStarted { get; } =
                new ManualResetEventSlim(false);

            public bool ConcurrentAccessDetected { get; private set; }

            public int EnumerationCount => Volatile.Read(ref _enumerationCount);

            public void ReleaseFirst() => _releaseFirst.Set();

            public void ReleaseSecond() => _releaseSecond.Set();

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
                if (Volatile.Read(ref _activeEnumeration) != 0)
                {
                    ConcurrentAccessDetected = true;
                }
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                if (Interlocked.Increment(ref _activeEnumeration) != 1)
                {
                    ConcurrentAccessDetected = true;
                }

                var invocation = Interlocked.Increment(ref _enumerationCount);
                var started = invocation == 1 ? FirstStarted : SecondStarted;
                var release = invocation == 1 ? _releaseFirst : _releaseSecond;
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        "The test did not release the sequenced enumeration.");
                }

                return invocation == 1 ? _first : _second;
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
            {
                var invocation = Volatile.Read(ref _enumerationCount);
                var folders = invocation == 1 ? _firstFolders : _secondFolders;
                Interlocked.Decrement(ref _activeEnumeration);
                return folders;
            }

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class ReentrantFallbackSource : IAssetRecordSource
        {
            private readonly AssetRecord _stale;
            private readonly AssetRecord _current;
            private int _enumerationCount;

            internal ReentrantFallbackSource(
                AssetRecord stale,
                AssetRecord current)
            {
                _stale = stale;
                _current = current;
            }

            public bool IsThreadSafe => false;

            internal Action StartReplacement { get; set; }

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                var invocation = ++_enumerationCount;
                if (invocation == 1)
                {
                    var startReplacement = StartReplacement;
                    StartReplacement = null;
                    startReplacement?.Invoke();
                    return new[] { _stale };
                }

                return new[] { _current };
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() =>
                Array.Empty<AssetFolderRecord>();

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        private sealed class PartiallyEnumeratingFaultSource : IAssetRecordSource
        {
            private readonly AssetRecord _partialRecord;

            public PartiallyEnumeratingFaultSource(AssetRecord partialRecord)
            {
                _partialRecord = partialRecord;
            }

            public bool IsThreadSafe => true;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords()
            {
                yield return _partialRecord;
                Parallel.For(
                    0,
                    1,
                    _ => throw new IOException("transient worker read"));
            }

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() =>
                throw new AssertionException("A faulted async snapshot must not supply folders.");

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;

            public AssetRecord BuildRecord(AssetPath assetPath) => null;
        }

        [Test]
        public void PackageRoots_SourcesRejectIncompleteEnumeration()
        {
            var original = RegisteredPackageRoots.Current;
            var originalQuery = RegisteredPackageRoots.PackageQueryOverrideForTests;
            string root = null;
            string denied = null;
            try
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                Assert.That(packages, Is.Not.Null.And.Not.Empty);
                CreateDirectoryFailureFixture(out root, out denied, out _);
                foreach (var native in new[] { false, true })
                {
                    var hybridSource = new HybridAssetRecordSource();
                    var nativeSource = new NativeFileSystemAssetRecordSource();
                    Action prepare = () =>
                    {
                        if (native) nativeSource.PrepareForEnumeration();
                        else hybridSource.PrepareForEnumeration();
                    };
                    Func<List<AssetRecord>> enumerate = () => native
                        ? nativeSource.GetRecords().ToList()
                        : hybridSource.GetRecords().ToList();
                    Func<IReadOnlyList<AssetFolderRecord>> folders = () => native
                        ? nativeSource.GetFolderRecords()
                        : hybridSource.GetFolderRecords();
                    var queries = 0;
                    void Unavailable()
                    {
                        RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);
                        RegisteredPackageRoots.PackageQueryOverrideForTests = () => { queries++; return null; };
                    }
                    void Complete()
                    {
                        RegisteredPackageRoots.PackageQueryOverrideForTests = () => packages;
                        RegisteredPackageRoots.MarkRegistrationChangedForTests();
                        Assert.That(RegisteredPackageRoots.Refresh().IsComplete, Is.True);
                    }
                    void Reject(TestDelegate action)
                    {
                        Assert.That(Assert.Throws<InvalidOperationException>(action).Message,
                            Is.EqualTo("Package metadata was unavailable; authoritative enumeration requires a complete package mapping."),
                            native ? "native" : "hybrid");
                    }

                    Unavailable();
                    Reject(() => prepare());
                    Reject(() => enumerate());
                    Reject(() => folders());
                    Assert.That(queries, Is.GreaterThanOrEqualTo(3), "each unprepared entry must acquire checked mapping");

                    Complete();
                    prepare();
                    Unavailable();
                    Reject(() => prepare());
                    Reject(() => enumerate());
                    Reject(() => folders());

                    Complete();
                    prepare();
                    Assert.That(enumerate(), Is.Not.Empty);
                    Unavailable();
                    Reject(() => enumerate());
                    Reject(() => folders());

                    Complete();
                    prepare();
                    Assert.That(enumerate(), Is.Not.Empty);
                    Unavailable();
                    Reject(() => prepare());
                    Reject(() => folders());

                    try
                    {
                        SetDirectoryMode(denied, "000");
                        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetDirectories(denied));
                        Unavailable();
                        Reject(() => folders());
                    }
                    finally
                    {
                        SetDirectoryMode(denied, "700");
                    }
                }
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(denied) && Directory.Exists(denied)) SetDirectoryMode(denied, "700");
                }
                finally
                {
                    RegisteredPackageRoots.PackageQueryOverrideForTests = originalQuery;
                    RegisteredPackageRoots.SetForTests(original);
                    if (!string.IsNullOrEmpty(root)) AssetDatabase.DeleteAsset(root);
                }
            }
        }

        [Test]
        public void PackageRoots_PreparedScanKeepsCapturedMapping()
        {
            var original = RegisteredPackageRoots.Current;
            var originalQuery = RegisteredPackageRoots.PackageQueryOverrideForTests;
            string root = null;
            try
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                Assert.That(packages, Is.Not.Null.And.Not.Empty);
                CreateDirectoryFailureFixture(out root, out _, out var assetPath);
                var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
                Assert.That(assetGuid, Is.Not.Empty);
                RegisteredPackageRoots.PackageQueryOverrideForTests = () => packages;
                RegisteredPackageRoots.MarkRegistrationChangedForTests();
                Assert.That(RegisteredPackageRoots.Refresh().IsComplete, Is.True);
                var packagePath = HybridAssetRecordSource.GetRegisteredPackageRoots()
                    .Select(package => package.VirtualPrefix)
                    .First(path => !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))
                        && AssetDatabase.LoadMainAssetAtPath(path) != null);
                var packageGuid = AssetDatabase.AssetPathToGUID(packagePath);
                var packageLabels = AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(packagePath)).ToArray();
                Assert.That(packageGuid, Is.Not.Empty);

                foreach (var native in new[] { false, true })
                {
                    RegisteredPackageRoots.PackageQueryOverrideForTests = () => packages;
                    RegisteredPackageRoots.MarkRegistrationChangedForTests();
                    Assert.That(RegisteredPackageRoots.Refresh().IsComplete, Is.True);
                    var hybridSource = new HybridAssetRecordSource();
                    var nativeSource = new NativeFileSystemAssetRecordSource();
                    if (native) nativeSource.PrepareForEnumeration();
                    else hybridSource.PrepareForEnumeration();
                    var queries = 0;
                    RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);
                    RegisteredPackageRoots.PackageQueryOverrideForTests = () => { queries++; return null; };
                    var queryCount = RegisteredPackageRoots.PackageManagerQueriesForTests;
                    var records = native
                        ? Task.Run(() => nativeSource.GetRecords().ToList()).GetAwaiter().GetResult()
                        : hybridSource.GetRecords().ToList();
                    var record = records.Single(item => item.AssetPath == assetPath);
                    Assert.That(record.Guid, Is.EqualTo(assetGuid));
                    Assert.That(record.UnityLabels, Is.EqualTo(new[] { "asset-first", "asset-second" }));
                    Assert.That(queries, Is.Zero, "prepared enumeration must not refresh Package Manager");
                    Assert.That(RegisteredPackageRoots.PackageManagerQueriesForTests, Is.EqualTo(queryCount));
                    var snapshot = native ? nativeSource.GetFolderRecords() : hybridSource.GetFolderRecords();
                    var packageFolder = snapshot.Single(folder => folder.FolderPath.Value == packagePath);
                    Assert.That(packageFolder.Guid, Is.EqualTo(packageGuid));
                    Assert.That(packageFolder.UnityLabels, Is.EqualTo(packageLabels));
                    Assert.That(queries, Is.Zero, "one-shot folders must retain the prepared mapping without refreshing it");
                    Assert.That(RegisteredPackageRoots.PackageManagerQueriesForTests, Is.EqualTo(queryCount));
                    const string message = "Package metadata was unavailable; authoritative enumeration requires a complete package mapping.";
                    Assert.That(Assert.Throws<InvalidOperationException>(() =>
                    {
                        if (native) nativeSource.GetFolderRecords();
                        else hybridSource.GetFolderRecords();
                    }).Message, Is.EqualTo(message));
                    Assert.That(Assert.Throws<InvalidOperationException>(() =>
                    {
                        if (native) nativeSource.GetRecords().ToList();
                        else hybridSource.GetRecords().ToList();
                    }).Message, Is.EqualTo(message));
                    Assert.That(queries, Is.EqualTo(2));
                }
            }
            finally
            {
                RegisteredPackageRoots.PackageQueryOverrideForTests = originalQuery;
                RegisteredPackageRoots.SetForTests(original);
                if (!string.IsNullOrEmpty(root)) AssetDatabase.DeleteAsset(root);
            }
        }

        [UnityTest]
        public IEnumerator PackageRoots_SynchronizerPreservesCatalogAndRecovers()
        {
            var original = RegisteredPackageRoots.Current;
            var originalQuery = RegisteredPackageRoots.PackageQueryOverrideForTests;
            string root = null;
            AssetIndexSynchronizer asynchronous = null;
            AssetIndexSynchronizer synchronous = null;
            try
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                Assert.That(packages, Is.Not.Null.And.Not.Empty);
                CreateDirectoryFailureFixture(out root, out _, out var baselinePath);
                var baselineGuid = AssetDatabase.AssetPathToGUID(baselinePath);
                var folderGuid = AssetDatabase.AssetPathToGUID(root);
                var folderLabels = AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(root)).ToArray();
                Assert.That(baselineGuid, Is.Not.Empty);
                Assert.That(folderGuid, Is.Not.Empty);
                Assert.That(folderLabels, Is.EqualTo(new[] { "folder-first", "folder-second" }));
                var unavailableQueries = 0;
                void CompleteMapping()
                {
                    RegisteredPackageRoots.PackageQueryOverrideForTests = () => packages;
                    RegisteredPackageRoots.MarkRegistrationChangedForTests();
                    Assert.That(RegisteredPackageRoots.Refresh().IsComplete, Is.True);
                }
                void UnavailableMapping()
                {
                    RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);
                    RegisteredPackageRoots.PackageQueryOverrideForTests = () => { unavailableQueries++; return null; };
                }
                string ImportRecoveryAsset(string name)
                {
                    var path = root + "/" + name + ".txt";
                    File.WriteAllText(path, "package mapping recovery");
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var imported = AssetDatabase.LoadMainAssetAtPath(path);
                    Assert.That(imported, Is.Not.Null);
                    AssetDatabase.SetLabels(imported, new[] { "recovery-first", "recovery-second" });
                    AssetDatabase.SaveAssets();
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    Assert.That(guid, Is.Not.Empty);
                    return guid;
                }
                AssetRecord[] CaptureAssets(InMemoryAssetIndex index) => index.Enumerate()
                    .Select(record => new AssetRecord(record.Guid, record.AssetPath, record.Name, record.TypeName,
                        record.TagSearchText, record.UnityLabels.ToArray(), record.BaseTypeNames.ToArray()))
                    .ToArray();
                AssetFolderRecord[] CaptureFolders(InMemoryAssetIndex index) => index.EnumerateFolders()
                    .Select(folder => new AssetFolderRecord(folder.FolderPath, folder.Guid, folder.UnityLabels.ToArray()))
                    .ToArray();
                void AssertCatalog(InMemoryAssetIndex index, AssetRecord[] assets, AssetFolderRecord[] folders)
                {
                    Assert.That(index.Enumerate().Select(record => record.AssetPath),
                        Is.EquivalentTo(assets.Select(record => record.AssetPath)), "complete asset membership");
                    Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value),
                        Is.EquivalentTo(folders.Select(folder => folder.FolderPath.Value)), "complete folder membership");
                    foreach (var expected in assets)
                    {
                        Assert.That(index.TryGetByPath(expected.AssetPath, out var actual), Is.True, expected.AssetPath);
                        Assert.That(actual.Guid, Is.EqualTo(expected.Guid), expected.AssetPath);
                        Assert.That(actual.Name, Is.EqualTo(expected.Name), expected.AssetPath);
                        Assert.That(actual.TypeName, Is.EqualTo(expected.TypeName), expected.AssetPath);
                        Assert.That(actual.TagSearchText, Is.EqualTo(expected.TagSearchText), expected.AssetPath);
                        Assert.That(actual.UnityLabels, Is.EqualTo(expected.UnityLabels), expected.AssetPath);
                        Assert.That(actual.BaseTypeNames, Is.EqualTo(expected.BaseTypeNames), expected.AssetPath);
                    }
                    foreach (var expected in folders)
                    {
                        Assert.That(index.TryGetFolder(expected.FolderPath.Value, out var actual), Is.True, expected.FolderPath.Value);
                        Assert.That(actual.Guid, Is.EqualTo(expected.Guid), expected.FolderPath.Value);
                        Assert.That(actual.UnityLabels, Is.EqualTo(expected.UnityLabels), expected.FolderPath.Value);
                    }
                }
                void AssertPublishedFolders(InMemoryAssetIndex index, AssetIndexSynchronizer synchronizer)
                {
                    index.ReplaceFolders(synchronizer.GetFolderRecordsForProjection());
                    Assert.That(index.TryGetFolder(root, out var folder), Is.True);
                    Assert.That(folder.Guid, Is.EqualTo(folderGuid));
                    Assert.That(folder.UnityLabels, Is.EqualTo(folderLabels));
                    Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value), Does.Contain("Assets"));
                    Assert.That(index.EnumerateFolders().Select(folder => folder.FolderPath.Value), Does.Contain("Packages"));
                }
                void AssertRecovery(InMemoryAssetIndex index, string path, string guid)
                {
                    Assert.That(index.TryGetByPath(path, out var recovered), Is.True, path);
                    Assert.That(recovered.Guid, Is.EqualTo(guid));
                    Assert.That(recovered.UnityLabels, Is.EqualTo(new[] { "recovery-first", "recovery-second" }));
                }

                CompleteMapping();
                var asyncIndex = new InMemoryAssetIndex();
                asynchronous = new AssetIndexSynchronizer(new HybridAssetRecordSource(),
                    new NativeFileSystemAssetRecordSource(), asyncIndex, enablePersistentCache: false);
                var asyncCompletions = 0;
                asynchronous.SynchronizeAsync(null, () => asyncCompletions++);
                yield return WaitForPackageScanCompletion(asynchronous, () => asyncCompletions, 1, "native baseline");
                Assert.That(asynchronous.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async));
                Assert.That(asyncIndex.TryGetByPath(baselinePath, out var baseline), Is.True);
                Assert.That(baseline.Guid, Is.EqualTo(baselineGuid));
                Assert.That(baseline.UnityLabels, Is.EqualTo(new[] { "asset-first", "asset-second" }));
                AssertPublishedFolders(asyncIndex, asynchronous);
                var asyncAssets = CaptureAssets(asyncIndex);
                var asyncFolders = CaptureFolders(asyncIndex);

                UnavailableMapping();
                LogAssert.Expect(LogType.Warning,
                    "[Zep] PrepareForEnumeration threw (InvalidOperationException: Package metadata was unavailable; authoritative enumeration requires a complete package mapping.); falling back to sync.");
                LogAssert.Expect(LogType.Warning,
                    "[Zep] Synchronous fallback failed (InvalidOperationException: Package metadata was unavailable; authoritative enumeration requires a complete package mapping.); the current synchronization was retired and can be retried.");
                asynchronous.SynchronizeAsync(null, () => asyncCompletions++);
                yield return WaitForPackageScanCompletion(asynchronous, () => asyncCompletions, 2, "native refusal");
                Assert.That(asynchronous.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.SyncFallback));
                Assert.That(unavailableQueries, Is.GreaterThanOrEqualTo(2), "native preparation and synchronous fallback both reject unavailable mapping");
                AssertCatalog(asyncIndex, asyncAssets, asyncFolders);

                CompleteMapping();
                var asyncRecoveryGuid = ImportRecoveryAsset("AsyncRecovery");
                Assert.That(asyncIndex.TryGetByPath(root + "/AsyncRecovery.txt", out _), Is.False);
                asynchronous.SynchronizeAsync(null, () => asyncCompletions++);
                yield return WaitForPackageScanCompletion(asynchronous, () => asyncCompletions, 3, "native recovery");
                Assert.That(asynchronous.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async));
                AssertRecovery(asyncIndex, root + "/AsyncRecovery.txt", asyncRecoveryGuid);
                AssertPublishedFolders(asyncIndex, asynchronous);
                asynchronous.Dispose();
                asynchronous = null;

                CompleteMapping();
                var syncIndex = new InMemoryAssetIndex();
                synchronous = new AssetIndexSynchronizer(new HybridAssetRecordSource(),
                    new NativeFileSystemAssetRecordSource(), syncIndex, enablePersistentCache: false);
                var syncCompletions = 0;
                synchronous.Synchronize(() => syncCompletions++);
                Assert.That(syncCompletions, Is.EqualTo(1));
                Assert.That(syncIndex.TryGetByPath(baselinePath, out var syncBaseline), Is.True);
                Assert.That(syncBaseline.Guid, Is.EqualTo(baselineGuid));
                AssertPublishedFolders(syncIndex, synchronous);
                var syncAssets = CaptureAssets(syncIndex);
                var syncFolders = CaptureFolders(syncIndex);

                UnavailableMapping();
                var queriesBeforeRefusal = unavailableQueries;
                LogAssert.Expect(LogType.Warning,
                    "[Zep] Synchronous synchronization failed (InvalidOperationException: Package metadata was unavailable; authoritative enumeration requires a complete package mapping.); the current synchronization was retired and can be retried.");
                synchronous.Synchronize(() => syncCompletions++);
                Assert.That(syncCompletions, Is.EqualTo(2));
                Assert.That(unavailableQueries, Is.GreaterThan(queriesBeforeRefusal));
                AssertCatalog(syncIndex, syncAssets, syncFolders);

                CompleteMapping();
                var syncRecoveryGuid = ImportRecoveryAsset("SyncRecovery");
                Assert.That(syncIndex.TryGetByPath(root + "/SyncRecovery.txt", out _), Is.False);
                synchronous.Synchronize(() => syncCompletions++);
                Assert.That(syncCompletions, Is.EqualTo(3));
                AssertRecovery(syncIndex, root + "/SyncRecovery.txt", syncRecoveryGuid);
                AssertPublishedFolders(syncIndex, synchronous);
            }
            finally
            {
                try
                {
                    asynchronous?.Dispose();
                }
                finally
                {
                    try
                    {
                        synchronous?.Dispose();
                    }
                    finally
                    {
                        RegisteredPackageRoots.PackageQueryOverrideForTests = originalQuery;
                        RegisteredPackageRoots.SetForTests(original);
                        if (!string.IsNullOrEmpty(root)) AssetDatabase.DeleteAsset(root);
                    }
                }
            }
        }

        private static IEnumerator WaitForPackageScanCompletion(
            AssetIndexSynchronizer synchronizer, Func<int> completions, int expected, string phase)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (completions() < expected && watch.Elapsed < TimeSpan.FromSeconds(30)) yield return null;
            var timedOut = completions() < expected;
            var deadlineDetail = $"{phase}: elapsed={watch.Elapsed}, completions={completions()}, strategy={synchronizer.LastEnumerationStrategy}";
            // No scan cancellation/join exists. Retain timeout failure, but let the
            // already-started operation finish before finally removes its fixtures.
            while (completions() < expected) yield return null;
            Assert.That(timedOut, Is.False, deadlineDetail);
            Assert.That(completions(), Is.EqualTo(expected), deadlineDetail);
        }

        // A tag file that moved since the snapshot — advanced, created or deleted —
        // is reported by the load, never a reason to refuse it: the records' tag
        // text is re-derived by the verification that follows.
        [Test]
        public void JournalTagState_LoadValidation()
        {
            UseStableCaptureOverrides();
            using var tags = new JournalTagFiles();
            foreach (var path in JournalTagFiles.Paths)
            foreach (var mutation in new[] { "advance", "create", "delete" })
            {
                MalformedReset(); tags.Reset();
                if (mutation == "create") File.Delete(path);
                Assert.That(RecoveryCache.Save(new[] { JournalTagRecord("azure") }, Array.Empty<AssetFolderRecord>()), Is.True);
                Assert.That(RecoveryCache.TryLoad(RecoveryCache.CaptureArtifactDependencyVersion(), RecoveryCache.CaptureRecoveryState(), out _, out _, out _, out var unchanged, out _), Is.True);
                Assert.That(unchanged, Is.False, path + " " + mutation + ": premise");
                if (mutation == "delete") File.Delete(path); else tags.Change(path);
                JournalTagImport();
                Assert.That(RecoveryCache.TryLoad(RecoveryCache.CaptureArtifactDependencyVersion(), RecoveryCache.CaptureRecoveryState(), out _, out _, out _, out var moved, out _), Is.True, path + " " + mutation + ": the snapshot loads");
                Assert.That(moved, Is.True, path + " " + mutation + ": and the moved tag state is reported");
            }
            MalformedReset(); tags.Reset();
            Assert.That(RecoveryCache.Save(new[] { JournalTagRecord("azure") }, Array.Empty<AssetFolderRecord>()), Is.True);
            JournalTagImport();
            Assert.That(RecoveryCache.TryLoad(
                RecoveryCache.CaptureArtifactDependencyVersion(),
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out var records,
                out _,
                out _,
                out var stillSame,
                out _), Is.True);
            Assert.That(stillSame, Is.False, "an unchanged tag pair with a journaled batch reports nothing about tags");
            Assert.That(records.Single().TagSearchText, Is.EqualTo("azure"));
        }

        // Disposal detaches validation/enumeration polls but does not join their
        // workers. Observe only their existing Task fields for fixture lifetime
        // cleanup; no private method is invoked and all behavior uses public APIs.
        private static Task[] JournalTagPolledWorkerTasks(AssetIndexSynchronizer synchronizer)
        {
            var field = typeof(AssetIndexSynchronizer).GetField(
                "_editorPolls", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "worker lifetime observation must not silently disappear");
            var polls = (IEnumerable<Action>)field.GetValue(synchronizer);
            return polls.Where(poll => poll.Target != null)
                .SelectMany(poll => poll.Target.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(member => typeof(Task).IsAssignableFrom(member.FieldType))
                    .Select(member => (Task)member.GetValue(poll.Target)))
                .Where(task => task != null).Distinct().ToArray();
        }

        private static Task JournalTagCaptureValidationTask(AssetIndexSynchronizer synchronizer)
        {
            var tasks = JournalTagPolledWorkerTasks(synchronizer);
            Assert.That(tasks.Length, Is.EqualTo(1),
                "background dispatch must expose one unambiguous validation worker before polling");
            return tasks[0];
        }

        private static void JournalTagJoinWorkersAndDispose(AssetIndexSynchronizer synchronizer, Task validation)
        {
            var tasks = new List<Task>();
            if (validation != null) tasks.Add(validation);
            try { tasks.AddRange(JournalTagPolledWorkerTasks(synchronizer)); }
            finally
            {
                try { synchronizer.Dispose(); }
                finally
                {
                    Assert.That(Task.WaitAll(tasks.Distinct().ToArray(), TimeSpan.FromSeconds(30)), Is.True,
                        "validation and enumeration workers must finish before restoring shared storage and hooks");
                }
            }
        }

        [UnityTest]
        public IEnumerator JournalTagState_RebuildAndReloadSearch()
        {
            using var tags = new JournalTagFiles();
            foreach (var background in new[] { false, true })
            for (var scenario = 0; scenario < 4; scenario++)
            {
                MalformedReset(); tags.Reset(); JournalTagValidationRoute(background);
                var oldText = scenario == 0 ? "" : "azure";
                var currentText = scenario == 0 ? "azure" : scenario == 2 ? "crimson" : "";
                var oldRecord = JournalTagRecord(oldText);
                var current = JournalTagRecord(currentText);
                Assert.That(RecoveryCache.Save(new[] { oldRecord }, Array.Empty<AssetFolderRecord>()), Is.True);
                tags.Change(JournalTagFiles.Paths[scenario < 2 ? 0 : 1]);
                JournalTagImport();
                var source = new JournalTagSource(current);
                var index = new InMemoryAssetIndex();
                var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
                Task validation = null;
                try
                {
                    AssetIndexSynchronizationResult outcome = null;
                    if (background)
                    {
                        synchronizer.SynchronizeAsyncWithOutcome(null, result => outcome = result);
                        validation = JournalTagCaptureValidationTask(synchronizer);
                    }
                    else synchronizer.SynchronizeWithOutcome(result => outcome = result);
                    yield return MalformedWait(() => outcome != null, "tag metadata rebuild");
                    Assert.That(outcome.Succeeded, Is.True);
                    // A moved tag state loads the snapshot and verifies it against a
                    // walk after the operation completed; the walk re-derives the text.
                    yield return MalformedWait(() => !synchronizer.HasBackgroundJob, "tag metadata verification");
                    synchronizer.FlushPendingSnapshotSave();
                    Assert.That(source.Enumerations, Is.GreaterThan(0), "stale tag metadata requires authoritative source enumeration");
                    if (background)
                    {
                        Assert.That(RecoveryCache.HasTestOverrides, Is.False);
                        Assert.That(synchronizer.LastEnumerationStrategy, Is.EqualTo(EnumerationStrategy.Async));
                        Assert.That(source.EnumerationThread, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId));
                    }
                    JournalTagAssertCatalog(index, currentText);
                }
                finally { JournalTagJoinWorkersAndDispose(synchronizer, validation); }
                Assert.That(RecoveryCache.TryLoad(out var persisted, out _), Is.True);
                Assert.That(persisted.Single().TagSearchText, Is.EqualTo(currentText));
                Assert.That(persisted.Single().UnityLabels, Is.EqualTo(current.UnityLabels));
                var hydrateSource = new JournalTagSource(JournalTagRecord("incorrect enumeration"));
                var hydrated = new InMemoryAssetIndex();
                var observedThread = 0;
                var observer = RecoveryCache.CacheDeserializedOverride;
                RecoveryCache.CacheDeserializedOverride = () =>
                {
                    observedThread = Thread.CurrentThread.ManagedThreadId;
                };
                var second = new AssetIndexSynchronizer(hydrateSource, hydrateSource, hydrated, enablePersistentCache: true);
                Task hydrationValidation = null;
                try
                {
                    AssetIndexSynchronizationResult outcome = null;
                    if (background)
                    {
                        second.SynchronizeAsyncWithOutcome(null, result => outcome = result);
                        hydrationValidation = JournalTagCaptureValidationTask(second);
                    }
                    else second.SynchronizeWithOutcome(result => outcome = result);
                    yield return MalformedWait(() => outcome != null, "tag metadata hydration");
                    Assert.That(outcome.Succeeded, Is.True);
                    Assert.That(hydrateSource.Enumerations, Is.Zero, "the second synchronizer must hydrate persisted metadata");
                    JournalTagAssertCatalog(hydrated, currentText);
                    Assert.That(observedThread, Is.Not.Zero);
                    Assert.That(observedThread == Thread.CurrentThread.ManagedThreadId, Is.EqualTo(!background));
                }
                finally
                {
                    try { JournalTagJoinWorkersAndDispose(second, hydrationValidation); }
                    finally { RecoveryCache.CacheDeserializedOverride = observer; }
                }
            }
        }

        [UnityTest]
        public IEnumerator JournalTagState_CompatibilityAndUnavailableState()
        {
            UseStableCaptureOverrides();
            using var tags = new JournalTagFiles();
            MalformedReset(); tags.Reset();
            Assert.That(RecoveryCache.Save(new[] { JournalTagRecord("azure") }, Array.Empty<AssetFolderRecord>()), Is.True);
            var current = File.ReadAllBytes(RecoveryCache.CacheFilePath);
            Assert.That(current[4], Is.EqualTo(12));
            foreach (var version in new byte[] { 8, 9, 10 })
            {
                var old = (byte[])current.Clone(); old[4] = version;
                File.WriteAllBytes(RecoveryCache.CacheFilePath, old);
                Assert.That(RecoveryCache.TryLoad(out _, out _), Is.False);
                Assert.That(File.Exists(RecoveryCache.DirtyMarkerPath), Is.False, "incompatible versions are ordinary cache misses");
            }
            File.WriteAllBytes(RecoveryCache.CacheFilePath, current);
            Assert.That(RecoveryCache.TryLoad(out _, out _), Is.True);
            foreach (var path in JournalTagFiles.Paths)
            {
                MalformedReset(); tags.Reset();
                File.Delete(path);
                Assert.That(RecoveryCache.Save(new[] { JournalTagRecord("azure") }, Array.Empty<AssetFolderRecord>()), Is.True,
                    "confirmed absence is a valid timestamp state");
                Assert.That(RecoveryCache.TryLoad(out _, out _), Is.True);
                Directory.CreateDirectory(path);
                RecoveryCache.CapturePendingChangesWithRecovery(out var unavailable);
                Assert.That(unavailable.RequiresVerification, Is.True, "a tag state that cannot be read asks for a verification");
                Assert.That(RecoveryCache.TryLoad(out _, out _), Is.True, "and still loads the snapshot");
                Assert.That(RecoveryCache.Save(new[] { JournalTagRecord("azure") }, Array.Empty<AssetFolderRecord>(),
                    RecoveryCache.CaptureArtifactDependencyVersion(), unavailable), Is.True,
                    "the save is allowed; the unreadable state keeps asking for a verification on its own");
                RecoveryCache.CapturePendingChangesWithRecovery(out var stillUnavailable);
                Assert.That(stillUnavailable.RequiresVerification, Is.True);
                Directory.Delete(path); tags.Change(path);
                var source = new JournalTagSource(JournalTagRecord("restored"));
                var index = new InMemoryAssetIndex();
                using var synchronizer = new AssetIndexSynchronizer(source, source, index, enablePersistentCache: true);
                AssetIndexSynchronizationResult outcome = null;
                synchronizer.SynchronizeWithOutcome(result => outcome = result);
                yield return MalformedWait(() => outcome != null, "unavailable tag state recovery");
                Assert.That(outcome.Succeeded, Is.True);
                // The moved tag state is verified by a walk after the operation
                // completed; its save is what persists the re-derived text.
                yield return MalformedWait(() => !synchronizer.HasBackgroundJob, "tag state verification");
                synchronizer.FlushPendingSnapshotSave();
                Assert.That(source.Enumerations, Is.GreaterThan(0));
                Assert.That(RecoveryCache.TryLoad(out var recovered, out _), Is.True);
                Assert.That(recovered.Single().TagSearchText, Is.EqualTo("restored"));
                RecoveryCache.CapturePendingChangesWithRecovery(out var currentRecovery);
                Assert.That(RecoveryCache.TryLoad(
                RecoveryCache.CaptureArtifactDependencyVersion(),
                currentRecovery,
                out var reloaded,
                out _,
                out _,
                out _,
                out _), Is.True);
                Assert.That(reloaded.Single().TagSearchText, Is.EqualTo("restored"));
            }
        }

        private static void MalformedAssertRecord(AssetRecord actual, AssetRecord expected)
        {
            Assert.That(actual.Guid, Is.EqualTo(expected.Guid));
            Assert.That(actual.AssetPath, Is.EqualTo(expected.AssetPath));
            Assert.That(actual.Name, Is.EqualTo(expected.Name));
            Assert.That(actual.TypeName, Is.EqualTo(expected.TypeName));
            Assert.That(actual.TagSearchText, Is.EqualTo(expected.TagSearchText));
            Assert.That(actual.UnityLabels, Is.EqualTo(expected.UnityLabels));
            Assert.That(actual.BaseTypeNames, Is.EqualTo(expected.BaseTypeNames));
        }

        private static AssetRecord MalformedRecord(string name) =>
            new AssetRecord("recovery-" + name, "Assets/Recovery/" + name + ".asset", name, "TestAsset", "recovery-tag", new[] { "RecoveryRecordLabel" }, new[] { "RecoveryBase" });
        private static byte[] MalformedBytes(string path) => File.Exists(path)
            ? File.ReadAllBytes(path)
            : null;

        private static void MalformedReset()
        {
            RecoveryCache.ClearPendingChanges();
            Directory.CreateDirectory(RecoveryCache.CacheDirectory);
            foreach (var path in new[]
                     {
                         RecoveryCache.CacheFilePath,
                         RecoveryCache.CacheFilePath + ".tmp",
                         RecoveryCache.DirtyMarkerPath
                     })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static IEnumerator MalformedWait(Func<bool> predicate, string step)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            Assert.That(predicate(), Is.True, "Timed out during " + step);
        }

        private static AssetRecord JournalTagRecord(string tags) => new AssetRecord(
            "journal-tag-target", "Assets/JournalTagTarget.asset", "JournalTagTarget", "TestAsset", tags, new[] { "retained-label" });

        private static void JournalTagImport() => RecoveryCache.RecordPendingChanges(new ProjectAssetChanges(
            new[] { "Assets/UnrelatedScript.cs" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));

        private static void JournalTagAssertCatalog(InMemoryAssetIndex index, string text)
        {
            Assert.That(index.TryGetByPath("Assets/JournalTagTarget.asset", out var record), Is.True);
            Assert.That(record.TagSearchText, Is.EqualTo(text));
            Assert.That(record.UnityLabels, Is.EqualTo(new[] { "retained-label" }));
            var found = new AssetSearchService(index).Search("tag:azure").Any(item => item.Guid == "journal-tag-target");
            Assert.That(found, Is.EqualTo(text == "azure"));
        }

        private static void JournalTagValidationRoute(bool background)
        {
            if (!background) { UseStableCaptureOverrides(); return; }
            RecoveryCache.UnityLabelSnapshotOverride = null;
            RecoveryCache.ArtifactDependencyVersionOverride = null;
            RecoveryCache.AuthoritativeTypeSnapshotOverride = () => new Dictionary<string, AssetTypeMetadata>();
            Assert.That(RecoveryCache.HasTestOverrides, Is.False);
        }

        private sealed class JournalTagSource : IAssetRecordSource
        {
            private readonly AssetRecord _record;
            internal int Enumerations;
            internal int EnumerationThread;
            internal JournalTagSource(AssetRecord record) { _record = record; }
            public bool IsThreadSafe => true;
            public void PrepareForEnumeration(IReadOnlyDictionary<string, IReadOnlyList<string>> labels = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> types = null) { }
            public IEnumerable<AssetRecord> GetRecords()
            {
                Interlocked.Increment(ref Enumerations); EnumerationThread = Thread.CurrentThread.ManagedThreadId;
                return new[] { _record };
            }
            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => Array.Empty<AssetFolderRecord>();
            public AssetFolderRecord BuildFolderRecord(AssetPath path) => null;
            public AssetRecord BuildRecord(AssetPath path) => path.Value == _record.AssetPath ? _record :
                path.Value == "Assets/UnrelatedScript.cs" ? new AssetRecord("unrelated-script", path.Value, "UnrelatedScript", "MonoScript") : null;
        }

        private sealed class JournalTagFiles : IDisposable
        {
            internal static readonly string[] Paths = {
                "ProjectSettings/Zep.TagAssignments.json",
                "ProjectSettings/ZepTagSettings.asset" };
            private readonly byte[][] _contents = new byte[2][];
            private readonly DateTime[] _times = new DateTime[2];
            private readonly FileAttributes[] _attributes = new FileAttributes[2];
            private int _sequence;
            internal JournalTagFiles()
            {
                for (var i = 0; i < Paths.Length; i++)
                {
                    Assert.That(Directory.Exists(Paths[i]), Is.False, "scratch setup expects file or absence");
                    if (!File.Exists(Paths[i])) continue;
                    _contents[i] = File.ReadAllBytes(Paths[i]);
                    _times[i] = File.GetLastWriteTimeUtc(Paths[i]);
                    _attributes[i] = File.GetAttributes(Paths[i]);
                    File.SetAttributes(Paths[i], FileAttributes.Normal);
                }
            }
            internal void Reset() { foreach (var path in Paths) { if (Directory.Exists(path)) Directory.Delete(path); Change(path); } }
            internal void Change(string path)
            {
                var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;
                File.WriteAllText(path, "{}");
                var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_sequence);
                File.SetLastWriteTimeUtc(path, stamp);
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
                Assert.That(File.GetLastWriteTimeUtc(path).Ticks, Is.Not.EqualTo(before));
            }
            public void Dispose()
            {
                for (var i = 0; i < Paths.Length; i++)
                {
                    if (Directory.Exists(Paths[i])) Directory.Delete(Paths[i]);
                    if (File.Exists(Paths[i])) { File.SetAttributes(Paths[i], FileAttributes.Normal); File.Delete(Paths[i]); }
                    if (_contents[i] == null) continue;
                    File.WriteAllBytes(Paths[i], _contents[i]);
                    File.SetLastWriteTimeUtc(Paths[i], _times[i]);
                    File.SetAttributes(Paths[i], _attributes[i]);
                }
            }
        }

        private sealed class TagStateFile : IDisposable
        {
            private readonly string _path;
            private readonly bool _existed;
            private readonly byte[] _contents;
            private readonly DateTime _timestamp;

            internal TagStateFile(string path)
            {
                _path = path;
                _existed = File.Exists(path);
                _contents = _existed ? File.ReadAllBytes(path) : null;
                _timestamp = _existed ? File.GetLastWriteTimeUtc(path) : default;
            }

            internal void Write(int sequence)
            {
                if (Directory.Exists(_path))
                {
                    Directory.Delete(_path);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, "tag-state-" + sequence);
                var timestamp = new DateTime(
                    new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
                    + sequence * TimeSpan.TicksPerSecond,
                    DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(_path, timestamp);
                Assert.That(File.GetLastWriteTimeUtc(_path).Ticks, Is.EqualTo(timestamp.Ticks));
            }

            internal void MakeDirectory()
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                Directory.CreateDirectory(_path);
            }

            public void Dispose()
            {
                if (Directory.Exists(_path))
                {
                    Directory.Delete(_path);
                }
                else if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                if (_existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllBytes(_path, _contents);
                    File.SetLastWriteTimeUtc(_path, _timestamp);
                }
            }
        }

        private sealed class ZepConcreteTypeFixture : ScriptableObject
        {
        }
    }

    [ScriptedImporter(1, "zeptypefixture")]
    internal sealed class ZepTypeFixtureImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var asset = ScriptableObject.CreateInstance<ZepImportedConcreteFixture>();
            asset.name = "Zep Imported Type Fixture";
            context.AddObjectToAsset("main", asset);
            context.SetMainObject(asset);
        }
    }
}
