using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Zep.Core.Indexing;
using Zep.Editor.Bridge.Indexing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Editor.Tests.Bridge
{
    // The load contract: a snapshot on disk is loaded whenever it can be read.
    // What happened since it was written — a journaled batch, a marker, a tag
    // edit, a compile — is reported to the caller, never a reason to refuse.
    // And the save contract that pairs with it: a save covers the journal
    // generation current when it began, retires exactly that, and yields to a
    // snapshot that already covers more.
    public sealed class PersistentAssetIndexCacheLoadTests
    {
        private bool _cacheExisted;
        private byte[] _cacheBackup;
        private bool _dirtyMarkerExisted;
        private byte[] _dirtyMarkerBackup;
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

            _pendingChangesBackup = PersistentAssetIndexCache.CapturePendingChanges();
            PersistentAssetIndexCache.ClearPendingChanges();
            PersistentAssetIndexCache.Invalidate();
        }

        [TearDown]
        public void TearDown()
        {
            PersistentAssetIndexCache.ClearPendingChanges();
            for (var i = 0; i < _pendingChangesBackup.Count; i++)
            {
                PersistentAssetIndexCache.RecordPendingChanges(_pendingChangesBackup[i]);
            }

            Directory.CreateDirectory(PersistentAssetIndexCache.CacheDirectory);
            if (_cacheExisted)
            {
                File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, _cacheBackup);
            }
            else if (File.Exists(PersistentAssetIndexCache.CacheFilePath))
            {
                File.Delete(PersistentAssetIndexCache.CacheFilePath);
            }

            if (_dirtyMarkerExisted)
            {
                File.WriteAllBytes(PersistentAssetIndexCache.DirtyMarkerPath, _dirtyMarkerBackup);
            }
            else if (File.Exists(PersistentAssetIndexCache.DirtyMarkerPath))
            {
                File.Delete(PersistentAssetIndexCache.DirtyMarkerPath);
            }
        }

        [Test]
        public void Load_IsUnconditional_ForAReadableSnapshot()
        {
            Assert.That(
                PersistentAssetIndexCache.Save(new[] { Record("a", "Assets/A.asset") }, Array.Empty<AssetFolderRecord>()),
                Is.True,
                "premise: the snapshot was written");

            // Everything that used to refuse a load, all at once.
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/B.asset"));
            Assert.That(PersistentAssetIndexCache.MarkDirty(), Is.True);
            Directory.CreateDirectory(PersistentAssetIndexCache.CacheDirectory);
            File.WriteAllText(PersistentAssetIndexCache.PendingChangesAuthoritativeFallbackPath, "poison");

            var loaded = PersistentAssetIndexCache.TryLoad(
                PersistentAssetIndexCache.CaptureArtifactDependencyVersion() + 1,
                PersistentAssetIndexCache.CaptureRecoveryState(),
                out var records,
                out var folders,
                out var artifactRevisionChanged,
                out _,
                out var snapshotId);

            Assert.That(loaded, Is.True, "a readable snapshot is loaded whatever the journal and the markers say");
            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(records[0].AssetPath, Is.EqualTo("Assets/A.asset"));
            Assert.That(folders, Is.Empty);
            Assert.That(artifactRevisionChanged, Is.True, "and what moved since is reported, not judged");
            Assert.That(PersistentAssetIndexCache.IsCacheSnapshotCurrent(snapshotId), Is.True);
            PersistentAssetIndexCache.CapturePendingChangesWithRecovery(out var state);
            Assert.That(state.RequiresVerification, Is.True, "the marker still asks for a verification — after the load");
        }

        [Test]
        public void Load_ReportsATagStateThatMovedSinceTheSnapshot()
        {
            var written = new PersistentAssetIndexCache.RecoveryState(0, null, null, 100, 200);
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { Record("a", "Assets/A.asset") },
                    Array.Empty<AssetFolderRecord>(),
                    artifactDependencyVersion: 7,
                    written),
                Is.True);

            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                    7,
                    new PersistentAssetIndexCache.RecoveryState(0, null, null, 100, 200),
                    out _,
                    out _,
                    out var artifactChangedSame,
                    out var tagChangedSame,
                    out _),
                Is.True);
            Assert.That(artifactChangedSame, Is.False);
            Assert.That(tagChangedSame, Is.False);

            Assert.That(
                PersistentAssetIndexCache.TryLoad(
                    7,
                    new PersistentAssetIndexCache.RecoveryState(0, null, null, 100, 201),
                    out var records,
                    out _,
                    out _,
                    out var tagChanged,
                    out _),
                Is.True,
                "a tag edit is a reason to verify the tag text, not to drop the snapshot");
            Assert.That(tagChanged, Is.True);
            Assert.That(records, Has.Count.EqualTo(1));
        }

        [Test]
        public void Load_RejectsOnlyAnotherVersionBadMagicAndTruncation()
        {
            Assert.That(
                PersistentAssetIndexCache.Save(new[] { Record("a", "Assets/A.asset") }, Array.Empty<AssetFolderRecord>()),
                Is.True);
            var good = File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath);

            // Previous format version: expected, quiet, left on disk for the next save.
            var aged = (byte[])good.Clone();
            aged[4]--;
            File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, aged);
            var logged = new List<string>();
            void CaptureLog(string condition, string stackTrace, LogType type) =>
                logged.Add(type + ": " + condition);
            Application.logMessageReceived += CaptureLog;
            bool agedLoaded;
            try
            {
                agedLoaded = PersistentAssetIndexCache.TryLoad(out _, out _);
            }
            finally
            {
                Application.logMessageReceived -= CaptureLog;
            }

            Assert.That(agedLoaded, Is.False, "a snapshot written by another format version must not be adopted");
            Assert.That(logged, Is.Empty, "an older version is expected, not corrupt");
            Assert.That(File.ReadAllBytes(PersistentAssetIndexCache.CacheFilePath), Is.EqualTo(aged), "and is left exactly as found");

            // Bad magic: corrupt, said so once.
            var wrongMagic = (byte[])good.Clone();
            wrongMagic[0] = (byte)'X';
            File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, wrongMagic);
            LogAssert.Expect(LogType.Warning, new Regex("Discarding corrupt index cache"));
            Assert.That(PersistentAssetIndexCache.TryLoad(out _, out _), Is.False);

            // Truncated mid-record: unreadable, quiet.
            var truncated = new byte[good.Length - 3];
            Array.Copy(good, truncated, truncated.Length);
            File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, truncated);
            Assert.That(PersistentAssetIndexCache.TryLoad(out _, out _), Is.False);

            // And the intact bytes load again: nothing above was about state.
            File.WriteAllBytes(PersistentAssetIndexCache.CacheFilePath, good);
            Assert.That(PersistentAssetIndexCache.TryLoad(out var records, out _), Is.True);
            Assert.That(records, Has.Count.EqualTo(1));
        }

        [Test]
        public void Save_RetiresOnlyTheJournalGenerationsItCovers()
        {
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/A.asset"));
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/B.asset"));
            var covered = PersistentAssetIndexCache.CaptureRecoveryState();
            Assert.That(covered.JournalGeneration, Is.EqualTo(2), "premise: two batches journaled");

            // A batch that lands after the save captured its state — Unity's second
            // import pass, a callback during serialization — is above what the
            // snapshot covers and must survive the commit.
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/C.asset"));

            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { Record("a", "Assets/A.asset"), Record("b", "Assets/B.asset") },
                    Array.Empty<AssetFolderRecord>(),
                    artifactDependencyVersion: 0,
                    covered),
                Is.True);

            // The append folded A and B into C's file under generation 3, so the
            // save found nothing at or below 2 to retire: C is still journaled,
            // and A and B replay once more on a snapshot that already holds them,
            // which is a no-op. Nothing above the covered generation is ever lost.
            var remaining = PersistentAssetIndexCache.CapturePendingChanges(out var generation);
            var stillJournaled = new List<string>();
            for (var i = 0; i < remaining.Count; i++)
            {
                stillJournaled.AddRange(remaining[i].Imported);
            }

            Assert.That(stillJournaled, Does.Contain("Assets/C.asset"), "the batch above the covered generation is still journaled");
            Assert.That(generation, Is.EqualTo(3), "generations stay monotonic across a retirement");

            // A later save with nothing new journaled still covers generation 3 —
            // the snapshot's own floor — so the next batch is 4, never 1 again.
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { Record("a", "Assets/A.asset"), Record("b", "Assets/B.asset"), Record("c", "Assets/C.asset") },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);
            Assert.That(PersistentAssetIndexCache.CapturePendingChanges(out generation), Is.Empty);
            Assert.That(generation, Is.EqualTo(3));
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/D.asset"));
            Assert.That(PersistentAssetIndexCache.CaptureRecoveryState().JournalGeneration, Is.EqualTo(4));
        }

        [Test]
        public void Commit_YieldsToASnapshotThatAlreadyCoversMoreOfTheJournal()
        {
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/A.asset"));
            var stale = PersistentAssetIndexCache.CaptureRecoveryState();
            Assert.That(
                PersistentAssetIndexCache.TryBeginSnapshotSave(
                    new[] { Record("a", "Assets/A.asset") },
                    Array.Empty<AssetFolderRecord>(),
                    0,
                    stale,
                    null,
                    out var attempt),
                Is.True);
            PersistentAssetIndexCache.WriteStagedSnapshot(attempt);

            // Meanwhile another save covered one batch more and committed.
            PersistentAssetIndexCache.RecordPendingChanges(Import("Assets/B.asset"));
            Assert.That(
                PersistentAssetIndexCache.Save(
                    new[] { Record("a", "Assets/A.asset"), Record("b", "Assets/B.asset") },
                    Array.Empty<AssetFolderRecord>()),
                Is.True);

            Assert.That(
                PersistentAssetIndexCache.TryCommitSnapshotSave(attempt),
                Is.False,
                "replacing the newer snapshot would lose the batch only it holds");
            Assert.That(PersistentAssetIndexCache.TryLoad(out var records, out _), Is.True);
            Assert.That(records, Has.Count.EqualTo(2), "the newer snapshot is the one on disk");
            Assert.That(File.Exists(attempt.TempPath), Is.False, "and the staged file is gone");
        }

        private static AssetRecord Record(string guid, string path)
        {
            return new AssetRecord(guid, path, Path.GetFileNameWithoutExtension(path), "TestAsset");
        }

        private static ProjectAssetChanges Import(string path)
        {
            return new ProjectAssetChanges(
                new[] { path },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                false);
        }
    }
}
