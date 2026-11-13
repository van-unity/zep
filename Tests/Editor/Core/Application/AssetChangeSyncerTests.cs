using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    // Covers AssetChangeSyncer's recovery boundary. A resolved catalog delta is
    // published once; an incomplete path observation schedules authoritative
    // recovery without invoking a second direct-index mutation path.
    public sealed class AssetChangeSyncerTests
    {
        private const string RegistryPackageFolder = "Packages/com.example.registry/Runtime";

        private FolderAwareGateway _gateway;
        private ImmediateScheduler _scheduler;
        private int _projectionOnlyCount;
        private int _refreshCount;

        [SetUp]
        public void SetUp()
        {
            _gateway = new FolderAwareGateway();
            _scheduler = new ImmediateScheduler();
            _projectionOnlyCount = 0;
            _refreshCount = 0;
        }

        // A registry/git package folder has no physical directory at its virtual
        // path — its files live under Library/PackageCache. Testing the virtual
        // path with Directory.Exists answers "not a folder", the batch looks like
        // it changed nothing, and the panel keeps showing the old tree.
        [Test]
        public void HandleAssetChanges_PackageFolderImport_SchedulesTheRebuild()
        {
            _gateway.FolderPaths.Add(RegistryPackageFolder);
            var syncer = CreateSyncer();

            syncer.HandleAssetChanges(Changes(imported: new[] { RegistryPackageFolder }));
            _scheduler.RunScheduled();

            Assert.That(_refreshCount, Is.EqualTo(1),
                "an unresolved folder-only batch requests authoritative recovery");
            Assert.That(_projectionOnlyCount, Is.Zero);
        }

        [Test]
        public void HandleAssetChanges_PackageFolderMove_SchedulesTheRebuild()
        {
            _gateway.FolderPaths.Add(RegistryPackageFolder);
            var syncer = CreateSyncer();

            syncer.HandleAssetChanges(Changes(
                moved: new[] { RegistryPackageFolder },
                movedFrom: new[] { "Packages/com.example.registry/Old" }));
            _scheduler.RunScheduled();

            Assert.That(_refreshCount, Is.EqualTo(1));
        }

        // The other half of the predicate: a path the asset boundary does not
        // recognize at all changes nothing, so nothing is scheduled.
        [Test]
        public void HandleAssetChanges_UnknownNonFolderImport_RequestsRecovery()
        {
            var syncer = CreateSyncer();

            syncer.HandleAssetChanges(Changes(imported: new[] { "Packages/com.example.registry/Ghost.cs" }));
            _scheduler.RunScheduled();

            Assert.That(_refreshCount, Is.EqualTo(1));
            Assert.That(_projectionOnlyCount, Is.Zero);
        }

        [Test]
        public void HandleAssetChanges_IdenticalAcknowledgements_AreDeduplicated()
        {
            var syncer = CreateSyncer(_ => ExternalChangeApplicationResult.NoChanges);
            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                syncer.HandleAssetChanges(Changes(imported: new[]
                {
                    "Assets/Same.asset",
                    "Assets/Same.asset",
                }));
                _scheduler.RunScheduled();

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(_projectionOnlyCount, Is.Zero);
                Assert.That(
                    snapshot.GetCount(ZepPerfCounter.DeduplicatedAcknowledgements),
                    Is.EqualTo(1));
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void ProjectAssetChanges_Normalize_CoalescesPathsAndMoveTargets()
        {
            var changes = new ProjectAssetChanges(
                new[] { "Assets/A.asset", "Assets/A.asset", "Assets/B.asset" },
                new[] { "Assets/Old.asset", "Assets/Old.asset" },
                new[] { "Assets/B.asset", "Assets/B.asset" },
                new[] { "Assets/Before.asset", "Assets/Before.asset" },
                didDomainReload: true)
                .Normalize();

            Assert.That(changes.Imported, Is.EqualTo(new[] { "Assets/A.asset" }));
            Assert.That(changes.Deleted, Is.EqualTo(new[] { "Assets/Old.asset" }));
            Assert.That(changes.Moved, Is.EqualTo(new[] { "Assets/B.asset" }));
            Assert.That(changes.MovedFromPaths, Is.EqualTo(new[] { "Assets/Before.asset" }));
            Assert.That(changes.DidDomainReload, Is.True);
        }

        [Test]
        public void StructuredPerfInstrumentation_DisabledPathRecordsNothing()
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = false;
                ZepPerfLog.Reset();
                ZepPerfLog.Count(
                    ZepPerfCounter.AssetDatabaseGlobalRefreshCalls);
                using (ZepPerfLog.Measure(
                    "disabled-probe",
                    ZepPerfStage.UnityBoundary))
                {
                }

                var snapshot = ZepPerfLog.CaptureSnapshot();
                Assert.That(snapshot.GetCount(
                    ZepPerfCounter.AssetDatabaseGlobalRefreshCalls), Is.Zero);
                Assert.That(snapshot.Measurements, Is.Empty);
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }
        }

        // Two acknowledged batches used to collapse into one suppression, so the
        // second projectChanged Unity raises for them requested a full project
        // reconciliation the panel had already performed incrementally.
        [Test]
        public void ProjectChanged_AfterTwoAcknowledgedBatches_RequestsNoFullRefresh()
        {
            var syncer = CreateSyncer(_ => ExternalChangeApplicationResult.Changed);

            syncer.HandleAssetChanges(Changes(imported: new[] { "Assets/A.asset" }));
            syncer.HandleAssetChanges(Changes(imported: new[] { "Assets/B.asset" }));
            syncer.HandleProjectChanged();
            syncer.HandleProjectChanged();

            Assert.That(_refreshCount, Is.Zero,
                "each acknowledged batch answers for its own projectChanged");
        }

        // The counter must not swallow a coarse signal nobody accounted for.
        [Test]
        public void ProjectChanged_BeyondTheAcknowledgedBatches_StillRequestsAFullRefresh()
        {
            var syncer = CreateSyncer(_ => ExternalChangeApplicationResult.Changed);

            syncer.HandleAssetChanges(Changes(imported: new[] { "Assets/A.asset" }));
            syncer.HandleProjectChanged();
            syncer.HandleProjectChanged();

            Assert.That(_refreshCount, Is.EqualTo(1));
        }

        // 4.5: the resolvable half is applied and published; only the remainder
        // asks for authoritative recovery.
        [Test]
        public void HandleAssetChanges_PartiallyAppliedBatch_PublishesItAndStillRequestsRecovery()
        {
            var syncer = CreateSyncer(
                _ => ExternalChangeApplicationResult.Changed,
                new[] { "Packages/com.example.registry/Runtime" });

            syncer.HandleAssetChanges(Changes(imported: new[] { "Assets/A.asset" }));
            _scheduler.RunScheduled();

            Assert.That(_projectionOnlyCount, Is.EqualTo(1),
                "what was applied must reach the view model without waiting for recovery");
            Assert.That(_refreshCount, Is.EqualTo(1));
        }

        private AssetChangeSyncer CreateSyncer()
        {
            return CreateSyncer(_ => ExternalChangeApplicationResult.RequiresFullRebuild);
        }

        private AssetChangeSyncer CreateSyncer(
            Func<ProjectAssetChanges, ExternalChangeApplicationResult> apply,
            IReadOnlyList<string> unresolvedPaths = null)
        {
            return new AssetChangeSyncer(
                _scheduler,
                tryApplyIncremental: (ProjectAssetChanges changes, out IReadOnlyList<string> unresolved) =>
                {
                    unresolved = unresolvedPaths ?? Array.Empty<string>();
                    return apply(changes);
                },
                onApplyProjectionOnly: () => _projectionOnlyCount++,
                onFullRefreshNeeded: () => _refreshCount++);
        }

        private static ProjectAssetChanges Changes(
            string[] imported = null,
            string[] deleted = null,
            string[] moved = null,
            string[] movedFrom = null)
            => new ProjectAssetChanges(imported, deleted, moved, movedFrom);

        // Answers only what the batch asks about: an unlisted path is neither a
        // folder nor an error, exactly like a path Unity no longer knows.
        private sealed class FolderAwareGateway : IProjectAssetGateway
        {
            internal HashSet<string> FolderPaths { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default) => throw new NotSupportedException();
            public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) => throw new NotSupportedException();
            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) => throw new NotSupportedException();
            public int TagVersion => 0;

            public bool IsFolder(AssetPath assetPath) => FolderPaths.Contains(assetPath.Value);

            public bool Exists(AssetPath assetPath) => throw new NotSupportedException();

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

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath) => throw new NotSupportedException();

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) => GetTagIds(assetPath);

            public ProjectFileOperationResult SetTagIds(IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath) =>
                throw new NotSupportedException();

            public bool Open(AssetPath assetPath) => throw new NotSupportedException();

            public void Reveal(AssetPath assetPath) => throw new NotSupportedException();

            public void BeginBatch() => throw new NotSupportedException();

            public void EndBatch() => throw new NotSupportedException();

            public string GetGuid(AssetPath assetPath) => throw new NotSupportedException();
            public string GetPathForGuid(string assetGuid) => throw new NotSupportedException();

            public string ResolveAbsolutePath(AssetPath assetPath) => throw new NotSupportedException();
        }

        private sealed class EmptyRecordSource : IAssetRecordSource
        {
            public bool IsThreadSafe => true;

            public void PrepareForEnumeration(
                IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
                IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
            {
            }

            public IEnumerable<AssetRecord> GetRecords() => Array.Empty<AssetRecord>();

            public IReadOnlyList<AssetFolderRecord> GetFolderRecords() => Array.Empty<AssetFolderRecord>();

            // Folders are not assets: the record source declines them, which is
            // what pushes the batch onto the folder predicate.
            public AssetRecord BuildRecord(AssetPath assetPath) => null;

            public AssetFolderRecord BuildFolderRecord(AssetPath folderPath) => null;
        }

        private sealed class ImmediateScheduler : IDeferredCallScheduler
        {
            private readonly List<Action> _scheduled = new List<Action>();

            public void Schedule(Action action) => _scheduled.Add(action);

            public void Cancel(Action action) => _scheduled.Remove(action);

            public void RunScheduled()
            {
                var pending = _scheduled.ToArray();
                _scheduled.Clear();
                for (var i = 0; i < pending.Length; i++)
                {
                    pending[i]();
                }
            }
        }
    }
}
