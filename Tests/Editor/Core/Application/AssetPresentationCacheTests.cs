using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    public sealed class AssetPresentationCacheTests
    {
        // Only GetGuid / ResolveAbsolutePath are on the cache's read path; the
        // rest of the gateway is mutation surface the cache never touches.
        private sealed class FakeGateway : IProjectAssetGateway
        {
            public int GetGuidCalls;
            public Func<string, string> GuidForPath = _ => string.Empty;
            public string AbsolutePath = string.Empty;

            public string GetGuid(AssetPath assetPath)
            {
                GetGuidCalls++;
                return GuidForPath(assetPath.Value);
            }

            // Unused by this fixture: an unknown GUID makes commands fall back
            // to their recorded path, which is the pre-existing behaviour.
            public string GetPathForGuid(string assetGuid) => string.Empty;

            public string ResolveAbsolutePath(AssetPath assetPath) => AbsolutePath;

            public bool Exists(AssetPath assetPath) => throw new NotSupportedException();
            public bool IsFolder(AssetPath assetPath) => throw new NotSupportedException();
            public bool CanWrite(AssetPath assetPath) => throw new NotSupportedException();
            public bool CanTag(AssetPath assetPath) => throw new NotSupportedException();
            public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default) => throw new NotSupportedException();
            public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) => throw new NotSupportedException();
            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) => throw new NotSupportedException();
            public int TagVersion => 0;
            public bool EnsureFolder(AssetPath folderPath) => throw new NotSupportedException();
            public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName) => throw new NotSupportedException();
            public ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) => throw new NotSupportedException();
            public ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) => throw new NotSupportedException();
            public ProjectFileOperationResult Delete(AssetPath assetPath) => throw new NotSupportedException();

            // No identity to re-mint: these fakes hold no .meta files.

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath) => throw new NotSupportedException();
            public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) => GetTagIds(assetPath);
            public ProjectFileOperationResult SetTagIds(IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath) => throw new NotSupportedException();
            public bool Open(AssetPath assetPath) => throw new NotSupportedException();
            public void Reveal(AssetPath assetPath) => throw new NotSupportedException();
            public void BeginBatch() { }
            public void EndBatch() { }
        }

        private static AssetPresentationCache CreateCache(FakeGateway gateway, out Counter statReads)
        {
            var reads = new Counter();
            statReads = reads;
            return new AssetPresentationCache(
                gateway,
                readFileLastWriteTime: _ => { reads.Value++; return new DateTime(2026, 7, 8, 12, 0, 0); },
                readDirectoryLastWriteTime: _ => new DateTime(2026, 7, 8, 12, 0, 0),
                readFileLength: _ => 1024L);
        }

        private sealed class Counter { public int Value; }

        [Test]
        public void Get_WithNoAddressablesInProject_NeverResolvesGuids()
        {
            var gateway = new FakeGateway();
            var cache = CreateCache(gateway, out _);

            var data = cache.Get("Assets/Marble.mat", "Material");

            Assert.That(data.IsAddressable, Is.False);
            Assert.That(gateway.GetGuidCalls, Is.Zero,
                "With an empty addressable set nothing can match — the native GUID lookup must be skipped.");
        }

        [Test]
        public void Get_ResolvesTheGuidOncePerPathAcrossRepeatedBinds()
        {
            var gateway = new FakeGateway { GuidForPath = _ => "guid-marble" };
            var cache = CreateCache(gateway, out _);
            cache.SetAddressableAssetGuids(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "guid-marble" });

            var first = cache.Get("Assets/Marble.mat", "Material");
            var second = cache.Get("Assets/Marble.mat", "Material");
            var third = cache.Get("Assets/Marble.mat", "Material");

            Assert.That(first.IsAddressable, Is.True);
            Assert.That(second.IsAddressable, Is.True);
            Assert.That(third.IsAddressable, Is.True);
            Assert.That(gateway.GetGuidCalls, Is.EqualTo(1),
                "Get runs once per row bind on every scroll/refresh — the GUID must come from the per-path cache after the first call.");
        }

        // An Addressables folder entry makes the folder itself addressable —
        // the expansion puts the folder's own GUID in the set — and the row
        // used to hide that behind a "folders are never addressable" gate.
        [Test]
        public void Get_ForAFolderInTheAddressableSet_ReportsItAddressable()
        {
            var gateway = new FakeGateway { GuidForPath = _ => "folder-guid" };
            var cache = CreateCache(gateway, out _);
            cache.SetAddressableAssetGuids(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "folder-guid" });

            Assert.That(cache.Get("Assets/Bundled", "Folder").IsAddressable, Is.True,
                "a folder whose GUID is in the expanded addressable set wears the same mark as its contents");
        }

        [Test]
        public void Get_WithExpandedAddressableFolderCapture_MarksTheFolderAndItsChild()
        {
            var gateway = new FakeGateway { GuidForPath = path => path == "Assets/Bundled" ? "folder-guid" : "child-guid" };
            var cache = CreateCache(gateway, out _);
            var expanded = AddressablesCapture.Expand(
                new AddressablesCapture(new[] { "folder-guid" }, new[] { "Assets/Bundled" }, true),
                new[] { new AssetRecord("child-guid", "Assets/Bundled/Child.asset", "Child", "ScriptableObject") });
            cache.SetAddressableAssetGuids(expanded);

            Assert.That(cache.Get("Assets/Bundled", "Folder").IsAddressable, Is.True);
            Assert.That(cache.Get("Assets/Bundled/Child.asset", "ScriptableObject").IsAddressable, Is.True);
        }

        [Test]
        public void Get_AppliesAnAddressableSetSwapWithoutRefetchingGuids()
        {
            var gateway = new FakeGateway { GuidForPath = _ => "guid-marble" };
            var cache = CreateCache(gateway, out _);
            cache.SetAddressableAssetGuids(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "guid-marble" });

            Assert.That(cache.Get("Assets/Marble.mat", "Material").IsAddressable, Is.True);

            // The asset was removed from the addressable group; its GUID is
            // unchanged, so the cached GUID must be reused for the new verdict.
            cache.SetAddressableAssetGuids(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other" });

            Assert.That(cache.Get("Assets/Marble.mat", "Material").IsAddressable, Is.False);
            Assert.That(gateway.GetGuidCalls, Is.EqualTo(1));
        }

        // Both maps used to grow for the whole editor session — Clear() only fires
        // on an index rebuild and Remove() only on a per-asset change — so
        // browsing a large project added a key per bind and never gave one back.
        // The cap is 8,192 with a wholesale clear on overflow, so the count can
        // never exceed the cap and the entries still on screen re-stat once.
        [Test]
        public void TenThousandDistinctBinds_LeaveAtMostTheCapCached()
        {
            var gateway = new FakeGateway();
            var cache = CreateCache(gateway, out _);

            for (var i = 0; i < 10000; i++)
            {
                cache.Get("Assets/Bulk/Item" + i + ".mat", "Material");
            }

            var cached = 0;
            for (var i = 0; i < 10000; i++)
            {
                if (cache.HasCachedEntry("Assets/Bulk/Item" + i + ".mat"))
                {
                    cached++;
                }
            }

            Assert.That(cached, Is.LessThanOrEqualTo(8192), "the stats cache must be bounded");
            Assert.That(cached, Is.GreaterThan(0), "clearing on overflow must not leave the cache permanently empty");
            Assert.That(
                cache.HasCachedEntry("Assets/Bulk/Item0.mat"),
                Is.False,
                "the oldest binds are the ones that go");
            Assert.That(
                cache.HasCachedEntry("Assets/Bulk/Item9999.mat"),
                Is.True,
                "the most recent bind is always cached");
        }

        [Test]
        public void TenThousandDistinctAddressableProbes_LeaveTheGuidCacheBoundedAndCorrect()
        {
            var gateway = new FakeGateway { GuidForPath = path => "guid-" + path };
            var cache = CreateCache(gateway, out _);
            cache.SetAddressableAssetGuids(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "guid-Assets/Bulk/Item9999.mat" });

            for (var i = 0; i < 10000; i++)
            {
                cache.Get("Assets/Bulk/Item" + i + ".mat", "Material");
            }

            Assert.That(
                gateway.GetGuidCalls,
                Is.EqualTo(10000),
                "one native GUID resolve per distinct path is the pre-existing behaviour");
            Assert.That(
                cache.Get("Assets/Bulk/Item9999.mat", "Material").IsAddressable,
                Is.True,
                "clearing the GUID cache must not change any verdict");
            Assert.That(
                gateway.GetGuidCalls,
                Is.EqualTo(10000),
                "the last path is still cached, so re-reading it must not resolve again");
        }

        [Test]
        public void Remove_InvalidatesTheGuidForThatPath()
        {
            var gateway = new FakeGateway { GuidForPath = _ => "guid-marble" };
            var cache = CreateCache(gateway, out _);
            cache.SetAddressableAssetGuids(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "guid-marble" });

            cache.Get("Assets/Marble.mat", "Material");
            cache.Remove("Assets/Marble.mat");
            cache.Get("Assets/Marble.mat", "Material");

            Assert.That(gateway.GetGuidCalls, Is.EqualTo(2),
                "Remove fires when the asset at the path changed, so the GUID must be re-resolved.");
        }

        // The absolute-path resolve settles "file or directory" in the same
        // probe it already had to make, and both stat readers take that answer.
        // The plausible wrong result is asking File.Exists a second time — or,
        // worse, dropping the guard and handing a directory to the file-length
        // reader, which throws on a real project.
        [Test]
        public void Get_ForANonFolderTypeWhosePathIsADirectory_ReadsTheDirectoryStampAndNoFileLength()
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "AssetPresentationCacheTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var fileLengthReads = 0;
                var fileStampReads = 0;
                var cache = new AssetPresentationCache(
                    new FakeGateway { AbsolutePath = tempDirectory },
                    readFileLastWriteTime: _ =>
                    {
                        fileStampReads++;
                        return new DateTime(1999, 1, 1);
                    },
                    readDirectoryLastWriteTime: _ => new DateTime(2026, 7, 8, 12, 0, 0),
                    readFileLength: _ =>
                    {
                        fileLengthReads++;
                        return 1024L;
                    });

                var data = cache.Get("Assets/Marble.mat", "Material");

                Assert.That(
                    data.ModifiedDate,
                    Is.EqualTo(new DateTime(2026, 7, 8, 12, 0, 0)),
                    "what is on disk decides which stamp reader runs, not the type name");
                Assert.That(fileStampReads, Is.Zero);
                Assert.That(data.Size, Is.Null, "a directory has no file size to report");
                Assert.That(
                    fileLengthReads,
                    Is.Zero,
                    "the single existence probe must keep the length reader off a directory");
            }
            finally
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        [Test]
        public void Get_CachesFileStatsPerPath()
        {
            // A real temp file so the absolute-path existence probe passes;
            // the stat readers themselves are injected counters.
            var tempFile = Path.GetTempFileName();
            try
            {
                var gateway = new FakeGateway { AbsolutePath = tempFile };
                var cache = CreateCache(gateway, out var statReads);

                var first = cache.Get("Assets/Marble.mat", "Material");
                var second = cache.Get("Assets/Marble.mat", "Material");

                Assert.That(first.ModifiedDate, Is.EqualTo(new DateTime(2026, 7, 8, 12, 0, 0)));
                Assert.That(second.Size, Is.EqualTo(1024L));
                Assert.That(statReads.Value, Is.EqualTo(1), "Stats are cached after the first read.");
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
