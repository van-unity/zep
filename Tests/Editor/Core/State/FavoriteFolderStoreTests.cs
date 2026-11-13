using System.Collections.Generic;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Editor.Tests.Core.State
{
    public sealed class FavoriteFolderStoreTests
    {
        [Test]
        public void Add_AppendsNormalizedPathAndSaves()
        {
            var backing = new List<string>();
            var saves = 0;
            var store = new FavoriteFolderStore(backing, () => saves++);

            var added = store.Add("Assets\\Art\\Icons\\");

            Assert.That(added, Is.True);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/Art/Icons" }));
            Assert.That(saves, Is.EqualTo(1));
        }

        [Test]
        public void Add_KeepsUserOrder_NotSortedOrder()
        {
            var store = new FavoriteFolderStore(new List<string>(), () => { });

            store.Add("Assets/Zebra");
            store.Add("Assets/Apple");
            store.Add("Packages/com.example.pkg/Runtime");

            Assert.That(store.Paths, Is.EqualTo(new[]
            {
                "Assets/Zebra",
                "Assets/Apple",
                "Packages/com.example.pkg/Runtime"
            }));
        }

        [Test]
        public void Add_RejectsDuplicates_IgnoringCaseAndFormatting()
        {
            var saves = 0;
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art" },
                () => saves++);

            Assert.That(store.Add("assets/art"), Is.False);
            Assert.That(store.Add("Assets\\Art\\"), Is.False);
            Assert.That(store.Paths.Count, Is.EqualTo(1));
            Assert.That(saves, Is.EqualTo(0));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("/Users/someone/Desktop")]
        [TestCase("Library/Cache")]
        public void Add_RejectsPathsOutsideTheProject(string candidate)
        {
            var saves = 0;
            var store = new FavoriteFolderStore(new List<string>(), () => saves++);

            Assert.That(store.Add(candidate), Is.False);
            Assert.That(store.Paths, Is.Empty);
            Assert.That(saves, Is.EqualTo(0));
        }

        [Test]
        public void Remove_DropsTheEntry_MatchingCaseInsensitively()
        {
            var backing = new List<string> { "Assets/Art", "Assets/Audio" };
            var saves = 0;
            var store = new FavoriteFolderStore(backing, () => saves++);

            var removed = store.Remove("assets/ART");

            Assert.That(removed, Is.True);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/Audio" }));
            Assert.That(saves, Is.EqualTo(1));
        }

        [Test]
        public void Remove_UnknownPath_IsANoOpWithoutSave()
        {
            var saves = 0;
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art" },
                () => saves++);

            Assert.That(store.Remove("Assets/Missing"), Is.False);
            Assert.That(saves, Is.EqualTo(0));
        }

        [Test]
        public void MoveBefore_MovesAnEntryEarlier_AndSaves()
        {
            var backing = new List<string> { "Assets/A", "Assets/B", "Assets/C" };
            var saves = 0;
            var store = new FavoriteFolderStore(backing, () => saves++);

            var moved = store.MoveBefore("Assets/C", "Assets/A");

            Assert.That(moved, Is.True);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/C", "Assets/A", "Assets/B" }));
            Assert.That(saves, Is.EqualTo(1));
        }

        [Test]
        public void MoveBefore_MovesAnEntryLater()
        {
            var backing = new List<string> { "Assets/A", "Assets/B", "Assets/C" };
            var store = new FavoriteFolderStore(backing, () => { });

            var moved = store.MoveBefore("Assets/A", "Assets/C");

            Assert.That(moved, Is.True);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/B", "Assets/A", "Assets/C" }));
        }

        [Test]
        public void MoveBefore_EmptyAnchor_MovesToTheEnd()
        {
            var backing = new List<string> { "Assets/A", "Assets/B", "Assets/C" };
            var store = new FavoriteFolderStore(backing, () => { });

            var moved = store.MoveBefore("Assets/A", string.Empty);

            Assert.That(moved, Is.True);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/B", "Assets/C", "Assets/A" }));
        }

        [Test]
        public void MoveBefore_PositionUnchanged_IsANoOpWithoutSave()
        {
            var backing = new List<string> { "Assets/A", "Assets/B", "Assets/C" };
            var saves = 0;
            var store = new FavoriteFolderStore(backing, () => saves++);

            // Before itself, before its own successor, and last-to-end all
            // leave the order as it is.
            Assert.That(store.MoveBefore("Assets/B", "Assets/B"), Is.False);
            Assert.That(store.MoveBefore("Assets/B", "Assets/C"), Is.False);
            Assert.That(store.MoveBefore("Assets/C", string.Empty), Is.False);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/A", "Assets/B", "Assets/C" }));
            Assert.That(saves, Is.EqualTo(0));
        }

        [Test]
        public void MoveBefore_UnknownPathOrAnchor_IsANoOpWithoutSave()
        {
            var backing = new List<string> { "Assets/A", "Assets/B" };
            var saves = 0;
            var store = new FavoriteFolderStore(backing, () => saves++);

            Assert.That(store.MoveBefore("Assets/Missing", "Assets/A"), Is.False);
            Assert.That(store.MoveBefore("Assets/A", "Assets/Missing"), Is.False);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/A", "Assets/B" }));
            Assert.That(saves, Is.EqualTo(0));
        }

        [Test]
        public void MoveBefore_MatchesPathsCaseInsensitively()
        {
            var backing = new List<string> { "Assets/A", "Assets/B", "Assets/C" };
            var store = new FavoriteFolderStore(backing, () => { });

            var moved = store.MoveBefore("assets/c", "ASSETS/A");

            Assert.That(moved, Is.True);
            Assert.That(backing, Is.EqualTo(new[] { "Assets/C", "Assets/A", "Assets/B" }));
        }

        [Test]
        public void Contains_MatchesNormalizedForms()
        {
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art" },
                () => { });

            Assert.That(store.Contains("assets\\art\\"), Is.True);
            Assert.That(store.Contains("Assets/Audio"), Is.False);
        }

        [Test]
        public void FirstContact_DropsMalformedAndDuplicateEntries_WithoutSaving()
        {
            var backing = new List<string>
            {
                "Assets/Art",
                null,
                "",
                "assets/art",
                "/absolute/elsewhere",
                "Assets\\Audio\\"
            };
            var saves = 0;
            var store = new FavoriteFolderStore(backing, () => saves++);

            Assert.That(store.Paths, Is.EqualTo(new[] { "Assets/Art", "Assets/Audio" }));
            Assert.That(saves, Is.EqualTo(0));
        }

        [Test]
        public void FirstContact_NormalizesEntries_SoLaterAddsCannotDuplicateThem()
        {
            var store = new FavoriteFolderStore(
                new List<string> { "Assets\\Art\\" },
                () => { });

            Assert.That(store.Add("Assets/Art"), Is.False);
            Assert.That(store.Paths, Is.EqualTo(new[] { "Assets/Art" }));
        }

        // -------------------------------------------------------------------
        // T4.14 (audit 5.4): a favorite is a path, so it has to follow the
        // folder when the folder's path changes.
        // -------------------------------------------------------------------

        [Test]
        public void Rewrite_AfterARename_MovesTheFavoriteAndItsDescendants()
        {
            var saves = 0;
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Audio", "Assets/Art", "Assets/Art/Icons" },
                () => saves++);

            var rewritten = store.Rewrite("Assets/Art", "Assets/Artwork");

            Assert.That(rewritten, Is.True);
            Assert.That(
                store.Paths,
                Is.EqualTo(new[] { "Assets/Audio", "Assets/Artwork", "Assets/Artwork/Icons" }),
                "the user's order survives the rewrite");
            Assert.That(saves, Is.EqualTo(1), "one edit, one save");
        }

        [Test]
        public void Rewrite_WhenAnAncestorMoves_CarriesTheFavoriteWithIt()
        {
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art/Icons/Flags" },
                () => { });

            Assert.That(store.Rewrite("Assets/Art", "Assets/Shared/Art"), Is.True);
            Assert.That(
                store.Paths,
                Is.EqualTo(new[] { "Assets/Shared/Art/Icons/Flags" }));
        }

        [Test]
        public void Rewrite_WithACreatesParentAsThePreviousPath_ChangesNothing()
        {
            var saves = 0;
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art" },
                () => saves++);

            // A create reports its PARENT as the previous path. Rewriting on that
            // would drag the parent's favorite onto the folder just created in it.
            var rewritten = store.Rewrite("Assets/Art", "Assets/Art/New Folder");

            Assert.That(rewritten, Is.False);
            Assert.That(store.Paths, Is.EqualTo(new[] { "Assets/Art" }));
            Assert.That(saves, Is.Zero);
        }

        [Test]
        public void Rewrite_ToAPathOutsideTheProject_LeavesTheFavoriteAlone()
        {
            var saves = 0;
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art" },
                () => saves++);

            var rewritten = store.Rewrite(
                "Assets/Art",
                "/Users/someone/Project/UndoPayloads/item-1/Art");

            Assert.That(rewritten, Is.False);
            Assert.That(store.Paths, Is.EqualTo(new[] { "Assets/Art" }));
            Assert.That(saves, Is.Zero);
        }

        [Test]
        public void Rewrite_OfAnUnrelatedFolder_ChangesNothingAndDoesNotSave()
        {
            var saves = 0;
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art", "Assets/ArtWork" },
                () => saves++);

            // "Assets/ArtWork" starts with "Assets/Art" as a STRING but is not
            // inside it; a plain prefix rewrite would corrupt it.
            var rewritten = store.Rewrite("Assets/Audio", "Assets/Sound");

            Assert.That(rewritten, Is.False);
            Assert.That(store.Paths, Is.EqualTo(new[] { "Assets/Art", "Assets/ArtWork" }));
            Assert.That(saves, Is.Zero);
        }

        [Test]
        public void Rewrite_IsSameOrDescendant_NotAStringPrefix()
        {
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art", "Assets/ArtWork" },
                () => { });

            Assert.That(store.Rewrite("Assets/Art", "Assets/Drawings"), Is.True);
            Assert.That(
                store.Paths,
                Is.EqualTo(new[] { "Assets/Drawings", "Assets/ArtWork" }));
        }

        [Test]
        public void Rewrite_ThatCollidesWithAnExistingFavorite_KeepsTheListDuplicateFree()
        {
            var store = new FavoriteFolderStore(
                new List<string> { "Assets/Art/Icons", "Assets/Icons" },
                () => { });

            Assert.That(store.Rewrite("Assets/Art/Icons", "Assets/Icons"), Is.True);
            Assert.That(store.Paths, Is.EqualTo(new[] { "Assets/Icons" }));
        }
    }
}
