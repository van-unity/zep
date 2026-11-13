using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.State
{
    // The assignment file lives in ProjectSettings and is VCS-tracked, so a merge
    // conflict lands unparseable JSON under a running editor. These tests pin the
    // failure policy for that: keep what is already loaded, never write over a
    // file we could not read, and resume normally once the file parses again.
    // Each store instance is pointed at its own scratch file — the developer's
    // real assignments are never touched.
    public sealed class TagAssignmentStoreTests
    {
        private const string ConflictedFileContents =
            "<<<<<<< HEAD\n{\"assignments\":[]}\n=======\n{\"assignments\":[]}\n>>>>>>> theirs\n";

        private string _scratchRoot;
        private string _filePath;

        [SetUp]
        public void SetUp()
        {
            _scratchRoot = Path.Combine(
                Path.GetTempPath(),
                "TagAssignmentStoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratchRoot);
            _filePath = Path.Combine(_scratchRoot, "Zep.TagAssignments.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, true);
            }
        }

        [Test]
        public void CleanFile_LoadsSavesAndRoundTripsThroughDisk()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);

            Assert.That(store.GetTagIds("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), Is.EqualTo(new[] { "red" }));

            var saved = store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
            });

            Assert.That(saved, Is.True);

            var reopened = new TagAssignmentStore(_filePath);
            Assert.That(reopened.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));
            Assert.That(reopened.GetTagIds("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), Is.EqualTo(new[] { "blue" }));
        }

        // The rollback is a journal of the touched entries now, not a copy of the
        // whole table, so it has to put back exactly what a failed batch moved -
        // including a removal and a brand-new GUID - and leave the rest alone.
        [Test]
        public void FailedSave_RestoresOnlyTheEntriesTheBatchTouched()
        {
            WriteAssignmentFile(
                ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new[] { "red" }),
                ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new[] { "blue" }),
                ("cccccccccccccccccccccccccccccccc", new[] { "green", "red" }));
            var store = new TagAssignmentStore(_filePath);
            // The store writes through a sibling temp file; a DIRECTORY in its
            // place fails the write deterministically on every platform, after
            // the in-memory map has already been mutated.
            Directory.CreateDirectory(_filePath + ".tmp");

            Assert.Throws<UnauthorizedAccessException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = new[] { "purple" },
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = Array.Empty<string>(),
                    ["dddddddddddddddddddddddddddddddd"] = new[] { "orange" },
                }));

            Assert.That(
                store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }),
                "a changed entry must go back to its previous tag set");
            Assert.That(
                store.GetTagIds("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), Is.EqualTo(new[] { "blue" }),
                "an entry the batch removed must come back");
            Assert.That(
                store.GetTagIds("dddddddddddddddddddddddddddddddd"), Is.Empty,
                "an entry the batch added must be gone again");
            Assert.That(
                store.GetTagIds("cccccccccccccccccccccccccccccccc"),
                Is.EqualTo(new[] { "green", "red" }),
                "an untouched entry must survive the rollback unchanged");
        }

        [Test]
        public void SavedFile_IsCompactAndStillRoundTrips()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);

            Assert.That(store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue", "green" },
            }), Is.True);

            var json = File.ReadAllText(_filePath);
            Assert.That(
                json, Does.Not.Contain("\n"),
                "the machine-owned assignment file must not be pretty-printed");
            var reopened = new TagAssignmentStore(_filePath);
            Assert.That(reopened.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));
            Assert.That(
                reopened.GetTagIds("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                Is.EqualTo(new[] { "blue", "green" }),
                "compact JSON must still round-trip through the parser");
        }

        // ReloadIfFileChanged stats the file ONCE now (the File.Exists probe was
        // asking a question GetLastWriteTimeUtc already answers). These two pin
        // the answers that stat has to keep producing: an untouched file is
        // "unchanged", and a file that vanished under the store is "changed" and
        // reloads — which recreates it, exactly as the File.Exists branch did.
        [Test]
        public void UntouchedFile_ReportsUnchanged()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            Assert.That(store.ReloadIfFileChanged(), Is.False);
            Assert.That(store.ReloadIfFileChanged(), Is.False, "a repeated probe must stay quiet");
            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));
        }

        [Test]
        public void FileDeletedUnderTheStore_ReportsChangedAndReloads()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            File.Delete(_filePath);

            Assert.That(store.ReloadIfFileChanged(), Is.True, "a missing file must not read as unchanged");
            Assert.That(
                store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Is.Empty,
                "the reload is authoritative: the deleted file has no assignments");
            Assert.That(File.Exists(_filePath), Is.True, "the reload rewrites the file it could not find");
        }

        [Test]
        public void CorruptFileMidSession_KeepsLoadedAssignmentsRefusesSaveAndLeavesTheFileAlone()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));

            OverwriteFile(ConflictedFileContents);

            Assert.DoesNotThrow(() => store.ReloadIfFileChanged());
            Assert.That(
                store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Is.EqualTo(new[] { "red" }),
                "a file we cannot parse must not empty the in-memory map");

            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                }));

            Assert.That(File.ReadAllText(_filePath), Is.EqualTo(ConflictedFileContents));
            Assert.That(
                store.GetTagIds("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                Is.Empty,
                "a refused save must roll the in-memory mutation back");
        }

        [Test]
        public void CorruptFileRepairedOnDisk_ResumesSavingAndRoundTrips()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            OverwriteFile(ConflictedFileContents);
            store.ReloadIfFileChanged();
            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["cccccccccccccccccccccccccccccccc"] = new[] { "green" },
                }));

            OverwriteFile("{\"assignments\":[{\"guid\":\"dddddddddddddddddddddddddddddddd\","
                + "\"tagIds\":[\"yellow\"]}]}");

            Assert.That(store.ReloadIfFileChanged(), Is.True);
            Assert.That(store.GetTagIds("dddddddddddddddddddddddddddddddd"), Is.EqualTo(new[] { "yellow" }));
            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.Empty);

            Assert.That(
                store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
                {
                    ["cccccccccccccccccccccccccccccccc"] = new[] { "green" },
                }),
                Is.True,
                "a clean reload must lift the save block");

            var reopened = new TagAssignmentStore(_filePath);
            Assert.That(reopened.GetTagIds("cccccccccccccccccccccccccccccccc"), Is.EqualTo(new[] { "green" }));
            Assert.That(reopened.GetTagIds("dddddddddddddddddddddddddddddddd"), Is.EqualTo(new[] { "yellow" }));
        }

        [Test]
        public void CorruptFileAtFirstLoad_ReadsEmptyWithoutThrowingAndStillRefusesToSave()
        {
            File.WriteAllText(_filePath, ConflictedFileContents);
            var store = new TagAssignmentStore(_filePath);

            Assert.DoesNotThrow(() => store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.Empty);
            Assert.That(store.Version, Is.GreaterThanOrEqualTo(0), "the version probe must stay alive");

            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                }));

            Assert.That(File.ReadAllText(_filePath), Is.EqualTo(ConflictedFileContents));
        }

        // The file is VCS-tracked and hand-editable, and the JsonUtility era
        // accepted pretty-printed and reordered documents with unknown members.
        // The hand-written codec has to keep reading all of those.
        [Test]
        public void PrettyPrintedFileWithUnknownMembersAndEscapes_StillLoads()
        {
            File.WriteAllText(_filePath,
                "{\n  \"version\": 2,\n  \"assignments\": [\n    {\n      \"tagIds\": [\"Red\", \"re\\u0064\", \"quo\\\"te\"],\n"
                + "      \"guid\": \"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\n      \"note\": null\n    },\n"
                + "    null,\n    { \"guid\": \"\", \"tagIds\": [\"lost\"] }\n  ]\n}\n");
            var store = new TagAssignmentStore(_filePath);

            Assert.That(
                store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Is.EqualTo(new[] { "red", "quo\"te" }),
                "tag ids normalise and dedupe case-insensitively, escapes decode");
            Assert.That(
                store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
                {
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                }),
                Is.True,
                "a pretty-printed file is a readable file, so saves stay enabled");
        }

        // A file that is JSON but not an assignment document is "unknown
        // assignments", not "no assignments": saving over it would drop what
        // the user meant to keep.
        [Test]
        public void JsonWithoutAnAssignmentList_KeepsLoadedTagsAndRefusesToSave()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            OverwriteFile("{\"assignments\": \"not a list\"}");
            store.ReloadIfFileChanged();

            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));
            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                }));
        }

        [Test]
        public void AssignmentMutation_ExternalChangesPreservedWithoutPolling()
        {
            WriteAssignmentFile(
                ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new[] { "red" }),
                ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new[] { "blue" }));
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var originalWriteTime = File.GetLastWriteTimeUtc(_filePath);

            WriteAssignmentFile(
                ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new[] { "purple" }),
                ("cccccccccccccccccccccccccccccccc", new[] { "green" }));
            File.SetLastWriteTimeUtc(_filePath, originalWriteTime);

            Assert.That(store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["dddddddddddddddddddddddddddddddd"] = new[] { "orange" },
            }), Is.True);

            var reopened = new TagAssignmentStore(_filePath);
            Assert.That(reopened.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.Empty);
            Assert.That(reopened.GetTagIds("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), Is.EqualTo(new[] { "purple" }));
            Assert.That(reopened.GetTagIds("cccccccccccccccccccccccccccccccc"), Is.EqualTo(new[] { "green" }));
            Assert.That(reopened.GetTagIds("dddddddddddddddddddddddddddddddd"), Is.EqualTo(new[] { "orange" }));
        }

        [Test]
        public void AssignmentMutation_ConflictRefusedBeforeNoOp()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var originalWriteTime = File.GetLastWriteTimeUtc(_filePath);
            File.WriteAllText(_filePath, ConflictedFileContents);
            File.SetLastWriteTimeUtc(_filePath, originalWriteTime);

            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                }));
            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = new[] { "red" },
                }));

            Assert.That(File.ReadAllText(_filePath), Is.EqualTo(ConflictedFileContents));
            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));
        }

        [Test]
        public void AssignmentMutation_UnreadableFileRefusesAndRecovers()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var problems = new List<string>();
            var store = new TagAssignmentStore(_filePath, reportProblem: problems.Add);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            using (File.Open(_filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                    }));
            }

            Assert.That(problems, Is.Not.Empty);
            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.EqualTo(new[] { "red" }));
            Assert.That(store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
            }), Is.True);
        }

        [Test]
        public void AssignmentMutation_FailedSaveRetainsExternalBaseline()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var originalWriteTime = File.GetLastWriteTimeUtc(_filePath);

            WriteAssignmentFile(
                ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new[] { "purple" }),
                ("cccccccccccccccccccccccccccccccc", new[] { "green" }));
            File.SetLastWriteTimeUtc(_filePath, originalWriteTime);
            var externalContents = File.ReadAllText(_filePath);
            Directory.CreateDirectory(_filePath + ".tmp");

            Assert.Throws<UnauthorizedAccessException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = new[] { "orange" },
                }));

            Assert.That(store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Is.Empty);
            Assert.That(store.GetTagIds("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), Is.EqualTo(new[] { "purple" }));
            Assert.That(store.GetTagIds("cccccccccccccccccccccccccccccccc"), Is.EqualTo(new[] { "green" }));
            Assert.That(File.ReadAllText(_filePath), Is.EqualTo(externalContents));
        }

        [Test]
        public void AssignmentMutation_NoOpAndRepairUseFreshDisk()
        {
            WriteAssignmentFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "red");
            var store = new TagAssignmentStore(_filePath);
            store.GetTagIds("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var unchangedVersion = store.Version;
            var unchangedContents = File.ReadAllText(_filePath);

            Assert.That(store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = new[] { "red" },
            }), Is.False);
            Assert.That(store.Version, Is.EqualTo(unchangedVersion));
            Assert.That(File.ReadAllText(_filePath), Is.EqualTo(unchangedContents));

            var originalWriteTime = File.GetLastWriteTimeUtc(_filePath);
            File.WriteAllText(_filePath, ConflictedFileContents);
            File.SetLastWriteTimeUtc(_filePath, originalWriteTime);
            Assert.Throws<InvalidOperationException>(() => store.SetTagIds(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = new[] { "blue" },
                }));

            WriteAssignmentFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "blue");
            File.SetLastWriteTimeUtc(_filePath, originalWriteTime);
            Assert.That(store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["cccccccccccccccccccccccccccccccc"] = new[] { "green" },
            }), Is.True);

            var versionBeforeExternalNoOp = store.Version;
            var savedWriteTime = File.GetLastWriteTimeUtc(_filePath);
            WriteAssignmentFile(
                ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new[] { "blue" }),
                ("cccccccccccccccccccccccccccccccc", new[] { "green" }),
                ("dddddddddddddddddddddddddddddddd", new[] { "orange" }));
            File.SetLastWriteTimeUtc(_filePath, savedWriteTime);

            Assert.That(store.SetTagIds(new Dictionary<string, IReadOnlyList<string>>
            {
                ["cccccccccccccccccccccccccccccccc"] = new[] { "green" },
            }), Is.False);
            Assert.That(store.Version, Is.EqualTo(versionBeforeExternalNoOp + 1));
            Assert.That(store.GetTagIds("dddddddddddddddddddddddddddddddd"), Is.EqualTo(new[] { "orange" }));
        }

        private void WriteAssignmentFile(string guid, string tagId)
        {
            WriteAssignmentFile((guid, new[] { tagId }));
        }

        private void WriteAssignmentFile(params (string Guid, string[] TagIds)[] assignments)
        {
            var records = new List<string>(assignments.Length);
            foreach (var assignment in assignments)
            {
                records.Add(
                    "{\"guid\":\"" + assignment.Guid + "\",\"tagIds\":[\""
                    + string.Join("\",\"", assignment.TagIds) + "\"]}");
            }

            File.WriteAllText(_filePath, "{\"assignments\":[" + string.Join(",", records) + "]}");
        }

        // The store's change probe is the file's mtime, and some filesystems
        // report a coarse enough timestamp that two writes in the same test would
        // look identical. Stamp the write forward so the probe always sees it.
        private void OverwriteFile(string contents)
        {
            File.WriteAllText(_filePath, contents);
            File.SetLastWriteTimeUtc(_filePath, DateTime.UtcNow.AddSeconds(5));
        }
    }
}
