using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.TestInfrastructure;
using NUnit.Framework;

namespace Zep.Editor.Tests.Core.State
{
    public sealed class TagDefinitionStoreTests
    {
        [Test]
        public void TryDeleteTag_RejectsDeletingTheFinalDefinition()
        {
            var records = new List<TagDefinitionRecord>
            {
                Record("red", "Red", ZepTagColor.Red, true)
            };
            var store = new TagDefinitionStore(records, () => { });

            var deleted = store.TryDeleteTag("red", out var errorMessage);

            Assert.That(deleted, Is.False);
            Assert.That(errorMessage, Is.EqualTo("At least one tag must remain."));
            Assert.That(store.GetOrderedTags().Count, Is.EqualTo(1));
        }

        [Test]
        public void ReplaceAll_SaveFailureRollsBackExactDefinitionsAndCanRetry()
        {
            var records = new List<TagDefinitionRecord>
            {
                Record("red", "Red", ZepTagColor.Red, true),
                Record("blue", "Blue", ZepTagColor.Blue, false)
            };
            var saveCount = 0;
            var store = new TagDefinitionStore(records, () =>
            {
                saveCount++;
                if (saveCount == 1)
                {
                    throw new IOException("Injected settings save failure.");
                }
            });
            var original = store.GetOrderedTags();
            var desired = new[]
            {
                new ZepTag("blue", "Sky", ZepTagColor.Green, true),
                new ZepTag("red", "Scarlet", ZepTagColor.Purple, false)
            };

            Assert.Throws<IOException>(() => store.ReplaceAll(desired));
            Assert.That(store.GetOrderedTags(), Is.EqualTo(original));

            store.ReplaceAll(desired);
            Assert.That(store.GetOrderedTags(), Is.EqualTo(desired));
        }

        // The seed is the one load-time branch that still writes: an empty
        // store has no user artifact to preserve.
        [Test]
        public void EnsureDefaults_SeedSaveFailureRestoresTheEmptyStoreAndRetries()
        {
            var records = new List<TagDefinitionRecord>();
            var saveCount = 0;
            var store = new TagDefinitionStore(records, () =>
            {
                saveCount++;
                if (saveCount == 1)
                {
                    throw new IOException("Injected settings save failure.");
                }
            });

            Assert.Throws<IOException>(() => store.GetOrderedTags());
            Assert.That(records, Is.Empty);

            Assert.That(store.GetOrderedTags().Count, Is.EqualTo(7));
            Assert.That(saveCount, Is.EqualTo(2));
        }

        [Test]
        public void EnsureDefaults_DuplicateIds_KeepsTheFirstAndLeavesTheFileAlone()
        {
            var records = new List<TagDefinitionRecord>
            {
                Record("red", "Red", ZepTagColor.Red, true),
                Record("red", "Crimson", ZepTagColor.Purple, false),
                Record("blue", "Blue", ZepTagColor.Blue, true)
            };
            var saveCount = 0;
            var warnings = new List<string>();
            var store = new TagDefinitionStore(records, () => saveCount++, warnings.Add);

            var tags = store.GetOrderedTags();

            Assert.That(
                tags.Select(tag => tag.Id + ":" + tag.Name),
                Is.EqualTo(new[] { "red:Red", "blue:Blue" }),
                "the duplicate must be dropped, never renamed to red-2 — every existing "
                + "'red' assignment would silently re-point at the first record");
            Assert.That(saveCount, Is.Zero, "a corrupt file must not be rewritten on load");
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("'red'").And.Contain("#2"));
        }

        [Test]
        public void EnsureDefaults_MalformedRecords_AreDroppedWithoutInventingIds()
        {
            var records = new List<TagDefinitionRecord>
            {
                Record(string.Empty, "No Id", ZepTagColor.Red, true),
                Record(" Mixed ID ", "Mixed", ZepTagColor.Green, true),
                Record("nameless", "   ", ZepTagColor.Blue, true),
                null,
                Record("green", "Green", ZepTagColor.Green, true)
            };
            var saveCount = 0;
            var warnings = new List<string>();
            var store = new TagDefinitionStore(records, () => saveCount++, warnings.Add);

            var tags = store.GetOrderedTags();

            Assert.That(tags.Select(tag => tag.Id), Is.EqualTo(new[] { "green" }));
            Assert.That(saveCount, Is.Zero);
            Assert.That(warnings.Count, Is.EqualTo(4));
        }

        [Test]
        public void EnsureDefaults_WellFormedRecords_AreKeptUntouchedAndUnsaved()
        {
            var records = new List<TagDefinitionRecord>
            {
                Record("red", "Red", ZepTagColor.Red, true),
                Record("blue", "Blue", ZepTagColor.Blue, false)
            };
            var saveCount = 0;
            var warnings = new List<string>();
            var store = new TagDefinitionStore(records, () => saveCount++, warnings.Add);

            var tags = store.GetOrderedTags();

            Assert.That(tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red", "blue" }));
            Assert.That(tags.Select(tag => tag.IsFavorite), Is.EqualTo(new[] { true, false }));
            Assert.That(saveCount, Is.Zero);
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void EnsureDefaults_MissingColor_IsDerivedInMemoryWithoutTouchingTheId()
        {
            var records = new List<TagDefinitionRecord>
            {
                Record("blue", "Blue", ZepTagColor.None, true)
            };
            var saveCount = 0;
            var store = new TagDefinitionStore(records, () => saveCount++);

            var tags = store.GetOrderedTags();

            Assert.That(tags[0].Id, Is.EqualTo("blue"));
            Assert.That(tags[0].Color, Is.EqualTo(ZepTagColor.Blue));
            Assert.That(saveCount, Is.Zero, "a derived colour is presentation, not a reason to rewrite the file");
        }

        // ResolveAssignedTags no longer builds a HashSet or runs OrderBy for the
        // dominant case (every id is a real tag). These pin the two orderings
        // that must not move with it: defined tags come back in definition
        // order, and unknown ids come back sorted OrdinalIgnoreCase AFTER them.
        // The inputs are deliberately unsorted and mixed so a comparator that
        // simply preserved input order would fail.
        [Test]
        public void ResolveAssignedTags_ReturnsDefinedTagsInDefinitionOrder()
        {
            var store = new TagDefinitionStore(
                new List<TagDefinitionRecord>
                {
                    Record("red", "Red", ZepTagColor.Red, false),
                    Record("blue", "Blue", ZepTagColor.Blue, false),
                    Record("green", "Green", ZepTagColor.Green, false)
                },
                () => { });

            var resolved = store.ResolveAssignedTags(new[] { " green ", " RED ", "GREEN" });

            Assert.That(
                resolved.Select(tag => tag.Id).ToArray(),
                Is.EqualTo(new[] { "red", "green" }),
                "definition order wins over request order, and a duplicate id resolves once");
            Assert.That(
                resolved.Select(tag => tag.Name).ToArray(),
                Is.EqualTo(new[] { "Red", "Green" }),
                "a resolved id carries its DEFINITION, not a fallback");
        }

        [Test]
        public void ResolveAssignedTags_AppendsUnknownIdsSortedAfterTheDefinedOnes()
        {
            var store = new TagDefinitionStore(
                new List<TagDefinitionRecord>
                {
                    Record("red", "Red", ZepTagColor.Red, false),
                    Record("blue", "Blue", ZepTagColor.Blue, false)
                },
                () => { });

            var resolved = store.ResolveAssignedTags(
                new[] { "zeta", " BLUE ", "alpha", "Mid", "red", " quo\"te ", "QuO\"TE" });

            Assert.That(
                resolved.Select(tag => tag.Id).ToArray(),
                Is.EqualTo(new[] { "red", "blue", "alpha", "mid", "quo\"te", "zeta" }),
                "ids normalize whitespace and case without changing identity; unknowns sort after defined tags");
            Assert.That(
                resolved.Take(2).Select(tag => tag.Name).ToArray(),
                Is.EqualTo(new[] { "Red", "Blue" }),
                "the defined tags keep their definitions");
            Assert.That(
                resolved.Skip(2).Select(tag => tag.Name).ToArray(),
                Is.EqualTo(new[] { "Alpha", "Mid", "Quo\"te", "Zeta" }),
                "the unknown ids get fallback names derived from the id");
        }

        [Test]
        public void StoredUnknownAssignment_PreservesIdentityWithoutUsingAnotherDefinitionsTint()
        {
            var scratchRoot = Path.Combine(Path.GetTempPath(), "TagDefinitionIdentity_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratchRoot);
            try
            {
                var filePath = Path.Combine(scratchRoot, "assignments.json");
                const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                const string contents = "{\"assignments\":[{\"guid\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"tagIds\":[\" QuO\\\"TE \"]}]}";
                File.WriteAllText(filePath, contents);
                var assignments = new TagAssignmentStore(filePath);
                var assignedIds = assignments.GetTagIds(guid);
                var store = new TagDefinitionStore(
                    new List<TagDefinitionRecord>
                    {
                        Record("quote", "Unrelated", ZepTagColor.Purple, true),
                        Record("blue", "Blue", ZepTagColor.Blue, false)
                    },
                    () => { });

                Assert.That(assignedIds, Is.EqualTo(new[] { "quo\"te" }));
                Assert.That(store.TryGetTag(assignedIds[0], out _), Is.False);
                var resolved = store.ResolveAssignedTags(assignedIds);
                Assert.That(resolved.Select(tag => tag.Id), Is.EqualTo(new[] { "quo\"te" }));
                Assert.That(resolved[0].Name, Is.EqualTo("Quo\"te"));
                Assert.That(resolved[0].IsFavorite, Is.False);

                var projection = new TagProjectionService(new UnusedGateway(), store);
                const string folderPath = "Assets/TaggedFolder";
                projection.ApplyAssignment(folderPath, assignedIds, isFolder: true);
                Assert.That(projection.MatchesTagFilter(folderPath, "quote"), Is.False);
                Assert.That(projection.GetFolderTintColorForPath(folderPath, "Folder"), Is.EqualTo(ZepTagColor.None));

                projection.ApplyAssignment(folderPath, new[] { " BLUE ", assignedIds[0] }, isFolder: true);
                Assert.That(projection.GetFolderTintColorForPath(folderPath, "Folder"), Is.EqualTo(ZepTagColor.Blue),
                    "the latest known tag determines tint; an unknown ID cannot supply the unrelated quote tint");
                Assert.That(store.TryGetTag(" QuOtE ", out var knownTag), Is.True);
                Assert.That(knownTag.Id, Is.EqualTo("quote"));
                Assert.That(File.ReadAllText(filePath), Is.EqualTo(contents), "presentation must preserve the assignment evidence");
            }
            finally
            {
                Directory.Delete(scratchRoot, true);
            }
        }

        [Test]
        public void UpdateAndDelete_UnknownIdentityCannotTargetAnotherDefinition()
        {
            var saveCount = 0;
            var store = new TagDefinitionStore(
                new List<TagDefinitionRecord>
                {
                    Record("quote", "Quote", ZepTagColor.Purple, true),
                    Record("blue", "Blue", ZepTagColor.Blue, false)
                },
                () => saveCount++);
            var original = store.GetOrderedTags();

            Assert.That(store.TryUpdateTag("quo\"te", "Changed", ZepTagColor.Green, false, out _, out var updateError), Is.False);
            Assert.That(updateError, Is.EqualTo("Tag could not be found."));
            Assert.That(store.TryDeleteTag("quo\"te", out var deleteError), Is.False);
            Assert.That(deleteError, Is.EqualTo("Tag could not be found."));
            Assert.That(store.GetOrderedTags(), Is.EqualTo(original));
            Assert.That(saveCount, Is.Zero);

            Assert.That(store.TryUpdateTag(" QuOtE ", "Quoted", ZepTagColor.Green, false, out var updated, out _), Is.True);
            Assert.That(updated.Id, Is.EqualTo("quote"));
            Assert.That(store.TryDeleteTag(" BLUE ", out _), Is.True);
            Assert.That(store.GetOrderedTags().Select(tag => tag.Id), Is.EqualTo(new[] { "quote" }));
        }

        [Test]
        public void ResolveAssignedTags_EmptyAndBlankRequestsResolveToNothing()
        {
            var store = new TagDefinitionStore(
                new List<TagDefinitionRecord> { Record("red", "Red", ZepTagColor.Red, false) },
                () => { });

            Assert.That(store.ResolveAssignedTags(null), Is.Empty);
            Assert.That(store.ResolveAssignedTags(new string[0]), Is.Empty);
            Assert.That(store.ResolveAssignedTags(new[] { "", "   ", null }), Is.Empty);
        }

        private static TagDefinitionRecord Record(
            string id,
            string name,
            ZepTagColor color,
            bool isFavorite)
        {
            return new TagDefinitionRecord
            {
                Id = id,
                Name = name,
                Color = color,
                IsFavorite = isFavorite
            };
        }
    }
}
