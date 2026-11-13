using System;
using Zep.Core.Indexing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zep.Core.Domain;
using Zep.Editor.Bridge.Indexing;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.Indexing
{
    // These two walks are the only producers that NAME paths for the index, and after
    // the consolidation there is exactly one pair of them: HybridAssetRecordSource calls
    // these, for its own roots and for every package root alike. So the rule proven here
    // on a chosen root is the rule that holds for a root Unity chooses.
    //
    // What it protects: Unity imports a plugin bundle as ONE asset. A walk that descends
    // into it puts records in the index for files that have no GUID, and every later
    // reload tries to rebuild them and gives up into a full rescan.
    public sealed class NativeFileSystemAssetRecordSourceTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "NativeFileSystemAssetRecordSourceTests_" + Guid.NewGuid().ToString("N"));
            // The real shape from a shipped package: the bundle's own .meta sits BESIDE
            // the directory (that is where its GUID lives), while its internals carry
            // metas Unity ignores — which is exactly what a descending walk would hand to
            // BuildRecord. "Plain" is the control: an ordinary directory the rule must
            // not swallow.
            Directory.CreateDirectory(Path.Combine(_root, "X.bundle", "Contents"));
            Directory.CreateDirectory(Path.Combine(_root, "Plain"));
            File.WriteAllText(Path.Combine(_root, "X.bundle.meta"), "guid: 11111111111111111111111111111111\n");
            File.WriteAllText(
                Path.Combine(_root, "X.bundle", "Contents", "Info.plist"),
                "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>");
            File.WriteAllText(
                Path.Combine(_root, "X.bundle", "Contents", "Info.plist.meta"),
                "guid: 22222222222222222222222222222222\n");
            File.WriteAllText(Path.Combine(_root, "Plain", "Keep.txt"), "keep");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public void CollectCandidatesAndFolders_SingleAssetDirectory_IsALeafCandidateAndNotAFolder()
        {
            var candidates = new List<string>();
            var folders = new List<string>();

            NativeFileSystemAssetRecordSource.CollectCandidatesAndFolders(_root, candidates, folders);

            Assert.That(
                candidates,
                Does.Contain(At("X.bundle")),
                "Unity imports the bundle as one asset, so the walk must offer the directory itself as a candidate");
            Assert.That(
                folders,
                Does.Not.Contain(At("X.bundle")),
                "recording it as a folder too would let the panel navigate into an asset");
            Assert.That(
                Beneath(candidates, "X.bundle"),
                Is.Empty,
                "nothing inside a single-asset directory has a GUID, so nothing inside it may be named");
            Assert.That(
                Beneath(folders, "X.bundle"),
                Is.Empty,
                "the walk must not descend into a single-asset directory");
            Assert.That(
                folders,
                Does.Contain(At("Plain")),
                "the rule must not swallow an ordinary directory");
            Assert.That(
                candidates,
                Does.Contain(At("Plain/Keep.txt")),
                "an ordinary directory is still traversed for its files");
        }

        [Test]
        public void CollectFoldersOnly_SingleAssetDirectory_IsNeitherAFolderNorTraversed()
        {
            var folders = new List<string>();

            NativeFileSystemAssetRecordSource.CollectFoldersOnly(_root, folders);

            Assert.That(
                folders,
                Does.Not.Contain(At("X.bundle")),
                "the folders-only walk feeds folder existence; a bundle is not a folder");
            Assert.That(
                Beneath(folders, "X.bundle"),
                Is.Empty,
                "and it must not descend into one either");
            Assert.That(
                folders,
                Does.Contain(At("Plain")),
                "an ordinary directory is still a folder");
        }

        // The native source is the synchronizer's asyncSource and is reachable from
        // CrossCacheCoordinator.AddPathObservation, so a bundle it cannot build lands in
        // unresolved-path recovery on every incremental import instead of in the index.
        // The hybrid half is a pin: it already resolves bundles (no File.Exists, and
        // AssetDatabase.IsValidFolder is false for one), and this task must not edit it.
        [Test]
        public void BuildRecord_ImportedSingleAssetDirectory_ResolvesInBothSources()
        {
            var bundlePath = NewBundlePath();
            try
            {
                ImportBundle(bundlePath);
                var guid = AssetDatabase.AssetPathToGUID(bundlePath);
                Assert.That(
                    guid,
                    Is.Not.Empty,
                    "fixture guard: Unity must import the .bundle directory as one asset");

                var native = new NativeFileSystemAssetRecordSource()
                    .BuildRecord(AssetPath.From(bundlePath));
                var hybrid = new HybridAssetRecordSource()
                    .BuildRecord(AssetPath.From(bundlePath));

                Assert.That(
                    native,
                    Is.Not.Null,
                    "a directory has no file at its own path, but its GUID is in the .meta beside it");
                Assert.That(native.Guid, Is.EqualTo(guid));
                Assert.That(hybrid, Is.Not.Null);
                Assert.That(hybrid.Guid, Is.EqualTo(guid));
            }
            finally
            {
                DeleteBundle(bundlePath);
            }
        }

        // Pins, not changed behaviour: both sources already answer null for a path INSIDE
        // a bundle, and neither gains an ancestor check to guarantee it. Unity names no
        // such path (no GUID, no .meta — asserted here as the fixture guard) and, after
        // the walk rule, no walk can name one either.
        [Test]
        public void BuildRecord_PathInsideASingleAssetDirectory_IsNotAnAsset()
        {
            var bundlePath = NewBundlePath();
            try
            {
                ImportBundle(bundlePath);
                var innerPath = bundlePath + "/Contents/Info.plist";
                Assert.That(
                    AssetDatabase.AssetPathToGUID(innerPath),
                    Is.Empty,
                    "fixture guard: Unity does not name paths inside an asset it imported as one");
                Assert.That(
                    File.Exists(innerPath + ".meta"),
                    Is.False,
                    "fixture guard: and writes no .meta inside one");

                Assert.That(
                    new NativeFileSystemAssetRecordSource().BuildRecord(AssetPath.From(innerPath)),
                    Is.Null);
                Assert.That(
                    new HybridAssetRecordSource().BuildRecord(AssetPath.From(innerPath)),
                    Is.Null);
            }
            finally
            {
                DeleteBundle(bundlePath);
            }
        }

        private static string NewBundlePath()
        {
            return "Assets/__ZepBundle_" + Guid.NewGuid().ToString("N") + ".bundle";
        }

        private static void ImportBundle(string bundlePath)
        {
            Directory.CreateDirectory(Path.Combine(bundlePath, "Contents"));
            File.WriteAllText(
                Path.Combine(bundlePath, "Contents", "Info.plist"),
                "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static void DeleteBundle(string bundlePath)
        {
            AssetDatabase.DeleteAsset(bundlePath);
            if (Directory.Exists(bundlePath))
            {
                Directory.Delete(bundlePath, recursive: true);
            }

            if (File.Exists(bundlePath + ".meta"))
            {
                File.Delete(bundlePath + ".meta");
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private string At(string relativePath)
        {
            return (_root + "/" + relativePath).Replace('\\', '/');
        }

        private IReadOnlyList<string> Beneath(IEnumerable<string> paths, string relativePath)
        {
            var prefix = At(relativePath) + "/";
            return paths
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
