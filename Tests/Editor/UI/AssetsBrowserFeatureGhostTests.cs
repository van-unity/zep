using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using NUnit.Framework;

namespace Zep.Tests.Editor.UI
{
    // 6.13: while an inline-create ghost is open, EVERY publish used to copy the
    // whole canonical list to prepend one row. On a search projection the
    // canonical list is lazy, so the copy read — and materialised — every entry
    // through the indexer, which (unlike the enumerator) raises no alarm. The
    // ghost is a one-element prefix facade instead; the assertions below are on
    // reads of the canonical list, not on timing.
    public sealed class AssetsBrowserFeatureGhostTests
    {
        private const int CanonicalCount = 200_000;

        private static AssetsBrowserFeature CreateFeature()
        {
            return new AssetsBrowserFeature(
                new UnityZepIconRepository(),
                AssetViewMode.List,
                AssetsAreaConstants.DefaultAssetGridItemSize);
        }

        private static AssetsAreaInlineEditRequest GhostRequest()
        {
            return new AssetsAreaInlineEditRequest
            {
                Path = AssetsAreaInlineEditRequest.GhostPath,
                InitialBasename = "New Folder",
                Extension = string.Empty,
                TypeName = "DefaultAsset",
                IsGhost = true,
            };
        }

        [Test]
        public void OpeningAGhostRow_DoesNotReadTheCanonicalList()
        {
            var feature = CreateFeature();
            var canonical = new CountingAssetList(CanonicalCount);
            feature.SetAssets(canonical);
            var beforeGhost = canonical.IndexerAccessCount;

            feature.BeginInlineEdit(GhostRequest());

            Assert.That(
                canonical.IndexerAccessCount - beforeGhost,
                Is.LessThan(100),
                "prepending the ghost must not walk the canonical list");
            Assert.That(feature.VisibleAssets.Count, Is.EqualTo(CanonicalCount + 1));
            Assert.That(
                feature.VisibleAssets[0].Path,
                Is.EqualTo(AssetsAreaInlineEditRequest.GhostPath),
                "the ghost is the prefix");
            Assert.That(
                feature.VisibleAssets[1].Path,
                Is.EqualTo("Assets/Item0.asset"),
                "the canonical list reads through, shifted by one");
            Assert.That(
                feature.VisibleAssets[CanonicalCount].Path,
                Is.EqualTo("Assets/Item" + (CanonicalCount - 1) + ".asset"));
        }

        [Test]
        public void RefreshingWhileTheGhostIsOpen_ReadsThroughToTheNewCanonicalList()
        {
            var feature = CreateFeature();
            feature.SetAssets(new CountingAssetList(3));
            feature.BeginInlineEdit(GhostRequest());

            var replacement = new CountingAssetList(CanonicalCount);
            var beforePublish = replacement.IndexerAccessCount;
            feature.SetAssets(replacement);

            Assert.That(
                replacement.IndexerAccessCount - beforePublish,
                Is.LessThan(100),
                "a folder refresh arriving mid-edit must not copy either");
            Assert.That(feature.VisibleAssets.Count, Is.EqualTo(CanonicalCount + 1));
            Assert.That(feature.CurrentAssets, Is.SameAs(replacement));

            feature.CancelInlineEdit();

            Assert.That(
                feature.VisibleAssets.Count,
                Is.EqualTo(CanonicalCount),
                "clearing the edit must drop the prefix, not the list");
            Assert.That(feature.VisibleAssets, Is.SameAs(replacement));
        }

        // 5.3: the ghost row rebinds on every keystroke while the user types the
        // new asset's name, and each bind re-entered Unity's built-in icon
        // lookups. The answer depends only on the type name, the extension and
        // the skin, so it is resolved once per key.
        [Test]
        public void RebindingAGhostOfTheSameType_ResolvesTheFallbackIconOnce()
        {
            var iconRepository = new UnityZepIconRepository();
            var before = AssetEntryRenderer.GhostIconLookupsForTests;

            for (var i = 0; i < 10; i++)
            {
                AssetEntryRenderer.ResolveAssetIcon(iconRepository, GhostEntry("New Item.t32a"));
            }

            Assert.That(
                AssetEntryRenderer.GhostIconLookupsForTests - before,
                Is.EqualTo(1),
                "ten binds of the same ghost type must run the built-in lookup once");

            AssetEntryRenderer.ResolveAssetIcon(iconRepository, GhostEntry("New Item.t32b"));

            Assert.That(
                AssetEntryRenderer.GhostIconLookupsForTests - before,
                Is.EqualTo(2),
                "a different extension is a different icon question");
        }

        // No assembly defines this type and no built-in icon is named after it,
        // so the repository's type-icon lookup misses and the built-in fallback
        // the memo guards is the leg that answers.
        private const string UnresolvableGhostTypeName = "ZepT32GhostType";

        private static ZepAssetEntry GhostEntry(string name)
        {
            return new ZepAssetEntry(
                name,
                AssetsAreaInlineEditRequest.GhostPath,
                UnresolvableGhostTypeName);
        }

        // Counts indexer reads and refuses to be enumerated wholesale, the way a
        // search projection's LazyAssetEntryList behaves.
        private sealed class CountingAssetList : IReadOnlyList<ZepAssetEntry>
        {
            private readonly int _count;

            internal CountingAssetList(int count)
            {
                _count = count;
            }

            internal int IndexerAccessCount { get; private set; }

            public int Count => _count;

            public ZepAssetEntry this[int index]
            {
                get
                {
                    IndexerAccessCount++;
                    return new ZepAssetEntry(
                        "Item" + index,
                        "Assets/Item" + index + ".asset",
                        "ScriptableObject");
                }
            }

            public IEnumerator<ZepAssetEntry> GetEnumerator()
            {
                for (var i = 0; i < _count; i++)
                {
                    yield return this[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => GetEnumerator();
        }
    }
}
