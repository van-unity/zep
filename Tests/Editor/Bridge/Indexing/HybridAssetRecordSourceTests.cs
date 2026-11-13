using System;
using Zep.Core.Indexing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Editor.Bridge.Indexing;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.Indexing
{
    public sealed class HybridAssetRecordSourceTests
    {
        // Per-path physical resolution reads this list, so a 5,000-file import used
        // to build 5,000 identical lists. The snapshot is immutable and swapped as
        // one reference, which is what makes identity a sound cache key.
        [Test]
        public void ResolvedRoots_ForOneSnapshot_ReturnsTheSameList()
        {
            var original = HybridAssetRecordSource.ResolvedRoots;
            try
            {
                var first = HybridAssetRecordSource.ResolvedRoots;
                var second = HybridAssetRecordSource.ResolvedRoots;

                Assert.That(
                    second,
                    Is.SameAs(first),
                    "an unchanged package-root snapshot must not be re-projected");

                HybridAssetRecordSource.ResolvedRoots =
                    new List<(string VirtualPrefix, string PhysicalRoot)>
                    {
                        ("Packages/com.example.memo", "/tmp/com.example.memo"),
                    };
                var afterSwap = HybridAssetRecordSource.ResolvedRoots;

                Assert.That(
                    afterSwap,
                    Is.Not.SameAs(first),
                    "a new snapshot must invalidate the projection, not reuse it");
                Assert.That(afterSwap.Count, Is.EqualTo(1));
                Assert.That(
                    afterSwap[0].VirtualPrefix,
                    Is.EqualTo("Packages/com.example.memo"));
                Assert.That(
                    HybridAssetRecordSource.ResolvedRoots,
                    Is.SameAs(afterSwap),
                    "the replacement snapshot must be memoised in turn");
            }
            finally
            {
                HybridAssetRecordSource.ResolvedRoots = original;
            }
        }

        // The cold scan infers "Texture2D" from ".png" and never asks Unity for
        // it; the incremental BuildRecord used to call GetMainAssetTypeAtPath for
        // EVERY changed path, so a 5,000-file import paid 5,000 native type
        // resolutions for records the extension already settled. Both incremental
        // sources must now hand back the cold record's type for an unambiguous
        // extension — and pay no type query doing it.
        [Test]
        public void BuildRecord_UnambiguousExtension_MatchesTheColdScanTypeWithoutATypeQuery()
        {
            var assetPath = "Assets/__ZepHybridType_"
                + Guid.NewGuid().ToString("N") + ".png";
            var wasEnabled = ZepPerfLog.IsEnabled;
            try
            {
                File.WriteAllBytes(assetPath, OnePixelPng);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                Assert.That(guid, Is.Not.Empty, "fixture guard: the png must have imported");

                // Taken before the counter window opens: the cold snapshot pays
                // its own type queries for the project's ambiguous paths.
                var coldType = AssetDatabaseAssetRecordSource.ResolveTypeMetadata(
                    assetPath,
                    guid,
                    AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot());
                Assert.That(coldType.TypeName, Is.EqualTo("Texture2D"), "fixture guard");

                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                var hybrid = new HybridAssetRecordSource().BuildRecord(AssetPath.From(assetPath));
                var native = new NativeFileSystemAssetRecordSource().BuildRecord(AssetPath.From(assetPath));

                var typeQueries = ZepPerfLog.CaptureSnapshot()
                    .GetCount(ZepPerfCounter.AssetDatabaseTypeQueries);
                Assert.That(
                    typeQueries,
                    Is.Zero,
                    "an unambiguous extension must not ask Unity for its main type");
                foreach (var record in new[] { hybrid, native })
                {
                    Assert.That(record, Is.Not.Null);
                    Assert.That(record.TypeName, Is.EqualTo(coldType.TypeName));
                    Assert.That(
                        record.BaseTypeNames,
                        Is.EqualTo(coldType.BaseTypeNames),
                        "the incremental record must equal the cold-scan record, base types included");
                }
            }
            finally
            {
                ZepPerfLog.IsEnabled = wasEnabled;
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        // The end-to-end half of the single-asset-directory rule, on the configuration
        // that actually ships: HybridAssetRecordSource is the default sync source and
        // the synchronizer falls through to its folder walk, so this is where "the
        // bundle is ONE leaf record with the GUID from its .meta, nothing inside it is
        // indexed, and the first paint already says the same thing" is proven against a
        // real imported asset rather than against a walk called directly.
        [Test]
        public void ImportedSingleAssetDirectory_IsOneLeafRecordAcrossEverySourceView()
        {
            var bundlePath = "Assets/__ZepBundle_"
                + Guid.NewGuid().ToString("N") + ".bundle";
            try
            {
                Directory.CreateDirectory(Path.Combine(bundlePath, "Contents"));
                File.WriteAllText(
                    Path.Combine(bundlePath, "Contents", "Info.plist"),
                    "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var guid = AssetDatabase.AssetPathToGUID(bundlePath);
                Assert.That(
                    guid,
                    Is.Not.Empty,
                    "fixture guard: Unity must import the .bundle directory as one asset");

                var records = new HybridAssetRecordSource().GetRecords().ToList();
                var bundleRecords = records
                    .Where(record => string.Equals(
                        record.AssetPath,
                        bundlePath,
                        StringComparison.Ordinal))
                    .ToList();
                Assert.That(
                    bundleRecords.Count,
                    Is.EqualTo(1),
                    "the bundle is one asset, so the default source must produce exactly one record for it");
                Assert.That(
                    bundleRecords[0].Guid,
                    Is.EqualTo(guid),
                    "and that record carries the GUID Unity assigned the bundle");
                Assert.That(
                    bundleRecords[0].Name,
                    Is.EqualTo(Path.GetFileNameWithoutExtension(bundlePath)),
                    "the record drops the suffix, as it does for every other asset");
                Assert.That(
                    records.Where(record => record.AssetPath.StartsWith(
                        bundlePath + "/",
                        StringComparison.Ordinal)),
                    Is.Empty,
                    "nothing inside a single-asset directory is an asset of its own");

                var folderPaths = new HybridAssetRecordSource()
                    .GetFolderRecords()
                    .Select(folder => folder.FolderPath.Value)
                    .ToList();
                Assert.That(
                    folderPaths,
                    Does.Not.Contain(bundlePath),
                    "the bundle must not also be a folder record: ContainsVisibleFolder would let the panel enter an asset");
                Assert.That(
                    folderPaths.Where(path => path.StartsWith(
                        bundlePath + "/",
                        StringComparison.Ordinal)),
                    Is.Empty,
                    "and the folder walk must not descend into it");

                var observed = new NativeDirectFolderAssetSource()
                    .Observe(AssetPath.From("Assets"))
                    .Entries
                    .Where(entry => string.Equals(
                        entry.Path.Value,
                        bundlePath,
                        StringComparison.Ordinal))
                    .ToList();
                Assert.That(observed.Count, Is.EqualTo(1), "the first paint still lists the bundle");
                Assert.That(
                    observed[0].IsFolder,
                    Is.False,
                    "direct observation paints the same fact as the record: this is an asset");
                Assert.That(
                    observed[0].Name,
                    Is.EqualTo(bundleRecords[0].Name),
                    "the observed row and the record that replaces it at hydration must agree, or the row relabels mid-reload");
            }
            finally
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
        }

        [Test]
        public void InferredTypes_MetadataMatchesRuntimeBases()
        {
            AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();
            var paths = new[]
            {
                "Assets/Fixture.guiskin",
                "Assets/Fixture.png",
                "Assets/Fixture.rendertexture",
                "Assets/Fixture.controller",
            };
            var metadata = Task.Run(() => new[]
            {
                AssetDatabaseAssetRecordSource.ResolveTypeMetadata(paths[0], "", null),
                AssetDatabaseAssetRecordSource.ResolveTypeMetadata(paths[1], "", null),
                AssetDatabaseAssetRecordSource.ResolveTypeMetadata(paths[2], "", null),
                AssetDatabaseAssetRecordSource.ResolveTypeMetadata(paths[3], "", null),
            }).GetAwaiter().GetResult();

            var expected = new[]
            {
                AssetDatabaseAssetRecordSource.BuildTypeMetadata(typeof(UnityEngine.GUISkin)),
                AssetDatabaseAssetRecordSource.BuildTypeMetadata(typeof(UnityEngine.Texture2D)),
                AssetDatabaseAssetRecordSource.BuildTypeMetadata(typeof(UnityEngine.RenderTexture)),
                AssetDatabaseAssetRecordSource.BuildTypeMetadata(typeof(UnityEditor.Animations.AnimatorController)),
            };
            for (var i = 0; i < metadata.Length; i++)
            {
                Assert.That(metadata[i].TypeName, Is.EqualTo(expected[i].TypeName));
                Assert.That(metadata[i].BaseTypeNames, Is.EqualTo(expected[i].BaseTypeNames));
                Assert.That(metadata[i].BaseTypeNames, Does.Not.Contain(nameof(UnityEngine.Object)));
            }

            Assert.That(metadata[0].BaseTypeNames, Does.Contain(nameof(UnityEngine.ScriptableObject)));
            Assert.That(metadata[1].BaseTypeNames, Does.Contain(nameof(UnityEngine.Texture)));
            Assert.That(metadata[2].BaseTypeNames, Does.Contain(nameof(UnityEngine.Texture)));
            Assert.That(metadata[3].BaseTypeNames, Does.Contain("RuntimeAnimatorController"));
        }

        private static readonly byte[] OnePixelPng =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0xF8, 0xCF, 0xC0, 0xF0,
            0x1F, 0x00, 0x05, 0x00, 0x01, 0xFF, 0x89, 0x99, 0x3D, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
            0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
        };
    }
}
