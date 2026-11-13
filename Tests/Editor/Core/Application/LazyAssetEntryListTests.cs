using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    // The wrapper exists to keep a 30k-match search result from allocating 30k
    // entries. Two ways of defeating that used to be silent: IndexOfPath rescanned
    // every record on every reveal/ping/marquee lookup, and the IList members
    // materialized the whole list for anyone who reached them through plumbing
    // that does not look like materialization (List.AddRange, Enumerable.ToList,
    // Enumerable.Contains all take the ICollection<T> route).
    public sealed class LazyAssetEntryListTests
    {
        private static LazyAssetEntryList Build()
        {
            var folders = new[]
            {
                new ZepAssetEntry("HeroFolder", "Assets/HeroFolder", "Folder"),
                new ZepAssetEntry("Props", "Assets/Props", "Folder")
            };
            var records = new[]
            {
                new AssetRecord("g-hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                new AssetRecord("g-villain", "Assets/Art/Villain.png", "Villain", "Texture2D"),
                new AssetRecord("g-ctrl", "Assets/Scripts/HeroController.cs", "HeroController", "MonoScript")
            };

            return new LazyAssetEntryList(
                folders,
                records,
                new TagProjectionService(
                    new ThrowingGateway(),
                    new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { })));
        }

        [Test]
        public void IndexOfPath_ResolvesFoldersAndFilesWithoutMaterializingAnything()
        {
            var lazy = Build();

            Assert.That(lazy.IndexOfPath("Assets/HeroFolder"), Is.EqualTo(0));
            Assert.That(lazy.IndexOfPath("Assets/Props"), Is.EqualTo(1));
            Assert.That(lazy.IndexOfPath("Assets/Art/Hero.png"), Is.EqualTo(2));
            Assert.That(lazy.IndexOfPath("Assets/Scripts/HeroController.cs"), Is.EqualTo(4));
            Assert.That(lazy.IndexOfPath("Assets/Art/Missing.png"), Is.EqualTo(-1));
            Assert.That(lazy.IndexOfPath(null), Is.EqualTo(-1));
            Assert.That(lazy.IndexOfPath(string.Empty), Is.EqualTo(-1));

            Assert.That(
                lazy.IndexerAccessCount,
                Is.Zero,
                "the lookup map is built from the records' own paths — no entry may be constructed");
            Assert.That(lazy.MaterializedCount, Is.Zero);
        }

        // OrdinalIgnoreCase, like every other path identity in the panel: a ping
        // whose path differs only in case must still frame its row.
        [Test]
        public void IndexOfPath_MatchesACasedifferingPath()
        {
            var lazy = Build();

            Assert.That(lazy.IndexOfPath("assets/art/HERO.PNG"), Is.EqualTo(2));
            Assert.That(lazy.IndexOfPath("ASSETS/HeroFolder"), Is.EqualTo(0));
            Assert.That(lazy.IndexerAccessCount, Is.Zero);
        }

        [Test]
        public void RepeatedIndexOfPath_ReusesTheMapAndKeepsAnsweringCorrectly()
        {
            var lazy = Build();

            for (var i = 0; i < 50; i++)
            {
                Assert.That(lazy.IndexOfPath("Assets/Art/Villain.png"), Is.EqualTo(3));
                Assert.That(lazy.ContainsPath("Assets/Nope.png"), Is.False);
            }

            Assert.That(lazy.IndexerAccessCount, Is.Zero);
        }

        // These four are the ones that used to materialize EVERY entry in silence.
        [Test]
        public void TheMaterializingIListMembers_Throw()
        {
            var lazy = Build();
            var entry = new ZepAssetEntry("Hero", "Assets/Art/Hero.png", "Texture2D");

            Assert.Throws<NotSupportedException>(() => lazy.Contains(entry));
            Assert.Throws<NotSupportedException>(() => lazy.IndexOf(entry));
            Assert.Throws<NotSupportedException>(
                () => lazy.CopyTo(new ZepAssetEntry[lazy.Count], 0));

            var asList = (System.Collections.IList)lazy;
            Assert.Throws<NotSupportedException>(() => asList.Contains(entry));
            Assert.Throws<NotSupportedException>(() => asList.IndexOf(entry));
            Assert.Throws<NotSupportedException>(
                () => ((System.Collections.ICollection)lazy).CopyTo(new ZepAssetEntry[lazy.Count], 0));

            Assert.That(lazy.MaterializedCount, Is.Zero, "a throw must not leave entries behind");
        }

        // The reads the UI actually performs must keep working.
        [Test]
        public void IndexingAndPathAt_StillWork()
        {
            var lazy = Build();

            Assert.That(lazy.Count, Is.EqualTo(5));
            Assert.That(lazy.PathAt(0), Is.EqualTo("Assets/HeroFolder"));
            Assert.That(lazy.PathAt(4), Is.EqualTo("Assets/Scripts/HeroController.cs"));
            Assert.That(lazy.MaterializedCount, Is.Zero);

            Assert.That(lazy[2].Name, Is.EqualTo("Hero"));
            Assert.That(lazy[2].TypeName, Is.EqualTo("Texture2D"));
            Assert.That(lazy.MaterializedCount, Is.EqualTo(1));
        }

        private sealed class ThrowingGateway : IProjectAssetGateway
        {
            public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default) => throw new NotSupportedException();
            public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) => throw new NotSupportedException();
            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) => throw new NotSupportedException();
            public int TagVersion => 0;

            public bool Exists(AssetPath assetPath) => throw new NotSupportedException();

            public bool IsFolder(AssetPath assetPath) => throw new NotSupportedException();

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
    }
}
