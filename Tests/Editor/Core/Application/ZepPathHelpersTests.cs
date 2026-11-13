using Zep.Core.Application;
using Zep.Core.Domain;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    public sealed class ZepPathHelpersTests
    {
        // 5.8: the orders the UI resolves a path against are not all
        // IAssetPathLookup — a plain browse projection is a bare array and takes
        // the linear leg. That leg compared Ordinal while AssetPath, the
        // selection set and LazyAssetEntryList.IndexOfPath all compare
        // OrdinalIgnoreCase, so a ping or reveal carrying a case-differing path
        // resolved in a search projection and silently did nothing in a browse
        // one. The negative case is asserted too: a case-insensitive compare
        // must not degrade into "return the first row".
        [Test]
        public void FindAssetPathIndex_LinearFallback_MatchesPathsCaseInsensitively()
        {
            var entries = new[]
            {
                new ZepAssetEntry("Villain", "Assets/Art/Sub/Villain.png", "Texture2D"),
                new ZepAssetEntry("Hero", "Assets/Art/Hero.png", "Texture2D"),
                new ZepAssetEntry("Ctrl", "Assets/Scripts/Ctrl.cs", "MonoScript"),
            };

            Assert.That(
                entries,
                Is.Not.InstanceOf<IAssetPathLookup>(),
                "fixture guard: this must exercise the linear fallback, not a lookup");

            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(entries, "assets/art/HERO.png"),
                Is.EqualTo(1),
                "a case-differing path must resolve to its row");
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(entries, "Assets/Art/Hero.png"),
                Is.EqualTo(1),
                "the exact path must keep resolving to the same row");
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(entries, "Assets/Art/Missing.png"),
                Is.EqualTo(-1),
                "an absent path must still be absent");
        }

        // 6.6: the collapse now decorates each path with its depth once and, when the
        // caller can name the folders, probes containment only against kept folders.
        // Both must land on exactly the set (and order) the per-comparison version
        // produced, from deliberately unsorted mixed input.
        private static readonly string[] UnsortedMixedSelection =
        {
            "Assets/Art/Sub/Hero.png",
            "Assets/Zebra.txt",
            "Assets/Art",
            "Assets/Art/Sub",
            "Assets/Audio/Theme.wav",
            "",
            "Assets/Audio",
            "Assets/Banner.png",
        };

        private static readonly string[] ExpectedCollapse =
        {
            "Assets/Art",
            "Assets/Audio",
            "Assets/Banner.png",
            "Assets/Zebra.txt",
        };

        [Test]
        public void CollapseNestedPaths_WithoutAFolderPredicate_KeepsOnlyTheTopmostPaths()
        {
            Assert.That(
                ZepPathHelpers.CollapseNestedPaths(UnsortedMixedSelection),
                Is.EqualTo(ExpectedCollapse),
                "shallowest first, then ordinal-ignore-case, with every contained path dropped");
        }

        [Test]
        public void CollapseNestedPaths_WithAFolderPredicate_ProducesTheSameSet()
        {
            var folders = new System.Collections.Generic.HashSet<string>(
                new[] { "Assets/Art", "Assets/Art/Sub", "Assets/Audio" },
                System.StringComparer.OrdinalIgnoreCase);

            Assert.That(
                ZepPathHelpers.CollapseNestedPaths(UnsortedMixedSelection, folders.Contains),
                Is.EqualTo(ExpectedCollapse),
                "naming the folders must not change the answer, only the work");
        }

        [Test]
        public void CollapseNestedPaths_ContainmentIgnoresCaseAndFileNamePrefixes()
        {
            var folders = new System.Collections.Generic.HashSet<string>(
                new[] { "Assets/art" },
                System.StringComparer.OrdinalIgnoreCase);

            Assert.That(
                ZepPathHelpers.CollapseNestedPaths(
                    new[] { "Assets/ART/Hero.png", "Assets/art", "Assets/artwork.png" },
                    folders.Contains),
                Is.EqualTo(new[] { "Assets/art", "Assets/artwork.png" }),
                "a case-differing child is contained; a sibling that merely shares the prefix is not");
        }

        [Test]
        public void CollapseNestedPaths_KeepsARepeatedPathOnlyOnce()
        {
            var folders = new System.Collections.Generic.HashSet<string>(
                new[] { "Assets/Art" },
                System.StringComparer.OrdinalIgnoreCase);
            var repeated = new[] { "Assets/Hero.png", "Assets/Art", "Assets/Hero.png", "Assets/Art" };

            Assert.That(
                ZepPathHelpers.CollapseNestedPaths(repeated),
                Is.EqualTo(new[] { "Assets/Art", "Assets/Hero.png" }));
            Assert.That(
                ZepPathHelpers.CollapseNestedPaths(repeated, folders.Contains),
                Is.EqualTo(new[] { "Assets/Art", "Assets/Hero.png" }),
                "a repeated file must not survive twice just because it cannot contain anything");
        }
    }
}
