using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    // Sidebar queries and the published snapshot share ordering and catalog state.
    public sealed class FolderEntriesBuilderTests
    {
        private InMemoryAssetIndex _index;
        private FolderEntriesBuilder _builder;

        [SetUp]
        public void SetUp()
        {
            _index = new InMemoryAssetIndex();
            _builder = new FolderEntriesBuilder(_index);
        }

        [TearDown]
        public void TearDown()
        {
            // Process-wide state: every other test in the suite assumes it empty.
            PackageDisplayNames.Set(null);
            ZepPerfLog.Reset();
        }

        [Test]
        public void PackageRootOrder_IsTheSameInTheSidebarQueryAndThePublishedCatalog()
        {
            PackageDisplayNames.Set(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Packages/com.a", "Zebra Tools" },
                { "Packages/com.m", "Middle Tools" },
                { "Packages/com.z", "Alpha Tools" },
            });

            _builder.RebuildFromIndex(
                Array.Empty<AssetRecord>(),
                new List<AssetFolderRecord>
                {
                    new AssetFolderRecord(AssetPath.From("Packages/com.m")),
                    new AssetFolderRecord(AssetPath.From("Assets")),
                    new AssetFolderRecord(AssetPath.From("Packages/com.z")),
                    new AssetFolderRecord(AssetPath.From("Packages")),
                    new AssetFolderRecord(AssetPath.From("Packages/com.a")),
                });

            var expectedPackageOrder = new[]
            {
                "Packages/com.z", // Alpha Tools
                "Packages/com.m", // Middle Tools
                "Packages/com.a", // Zebra Tools
            };

            Assert.That(
                _builder.GetVisibleFolderChildren("Packages").Select(entry => entry.Path),
                Is.EqualTo(expectedPackageOrder),
                "the sidebar lists package roots by manifest display name");
            Assert.That(
                _builder.Cached
                    .Select(entry => entry.Path)
                    .Where(path => path.StartsWith("Packages/", StringComparison.Ordinal)),
                Is.EqualTo(expectedPackageOrder),
                "the published catalog must order package roots the same way");
            Assert.That(
                _builder.Cached
                    .Select(entry => entry.Path),
                Is.EqualTo(new[]
                {
                    "Assets",
                    "Packages",
                    "Packages/com.z",
                    "Packages/com.m",
                    "Packages/com.a",
                }),
                "roots keep their bucket order, and each package subtree stays under its root");
        }

        [Test]
        public void StandardEntries_WithNoCatalogChanges_ReusesTheSortedSnapshot()
        {
            SeedAssetsProject();
            var entries = _builder.GetStandardEntries();

            Assert.That(_builder.GetStandardEntries(), Is.SameAs(entries));
            Assert.That(entries, Is.SameAs(_builder.Cached));
            Assert.That(entries.Select(entry => entry.Path), Is.EqualTo(new[]
            {
                "Assets", "Assets/Art", "Assets/Scripts", "Packages"
            }));
        }

        [Test]
        public void FolderDelta_UpdatesChildQueriesAndTheStandardSnapshot()
        {
            SeedAssetsProject();
            var before = _builder.GetStandardEntries();
            var delta = _index.Apply(
                new[] { "Assets/Art" },
                Array.Empty<AssetCatalogMove>(),
                Array.Empty<AssetRecord>(),
                new[] { new AssetFolderRecord(AssetPath.From("Assets/Audio")) });

            _builder.ApplyCatalogDelta(delta);

            Assert.That(_builder.GetStandardEntries(), Is.Not.SameAs(before));
            Assert.That(_builder.GetDirectVisibleFolders("Assets").Select(entry => entry.Path),
                Is.EqualTo(new[] { "Assets/Audio", "Assets/Scripts" }));
            Assert.That(_builder.GetStandardEntries().Select(entry => entry.Path),
                Has.Member("Assets/Audio").And.No.Member("Assets/Art"));
        }

        private void SeedAssetsProject()
        {
            var records = new List<AssetRecord>
            {
                new AssetRecord("g-hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                new AssetRecord("g-ctrl", "Assets/Scripts/Ctrl.cs", "Ctrl", "MonoScript"),
            };
            foreach (var record in records)
            {
                _index.Upsert(record);
            }

            _builder.RebuildFromIndex(
                records,
                new List<AssetFolderRecord>
                {
                    new AssetFolderRecord(AssetPath.From("Assets")),
                    new AssetFolderRecord(AssetPath.From("Assets/Art")),
                    new AssetFolderRecord(AssetPath.From("Assets/Scripts")),
                });
        }

    }
}
