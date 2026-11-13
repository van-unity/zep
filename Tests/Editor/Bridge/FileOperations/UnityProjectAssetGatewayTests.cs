using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.Settings;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.TestInfrastructure;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    public sealed class UnityProjectAssetGatewayTests
    {
        private IProjectTagDefinitionGateway _gateway;
        private ProjectTagStateSnapshot _original;

        [SetUp]
        public void SetUp()
        {
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            _gateway = new UnityProjectAssetGateway();
            _original = _gateway.CaptureTagState();
        }

        [TearDown]
        public void TearDown()
        {
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            var current = _gateway.CaptureTagState();
            var restore = _gateway.CompareAndSetTagState(current, _original);
            Assert.That(restore.Succeeded, Is.True, restore.ErrorMessage);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        [Test]
        public void TagStateCompareAndSet_ReloadsExternalAssignmentsEvenWhenMtimeIsPreserved()
        {
            var expected = _gateway.CaptureTagState();
            var assignmentPath = Path.Combine(
                ProjectPaths.GetProjectRootAbsolutePath(),
                "ProjectSettings",
                "Zep.TagAssignments.json");
            var originalWriteTime = File.GetLastWriteTimeUtc(assignmentPath);
            var externalGuid = Guid.NewGuid().ToString("N");
            var externalJson = "{\n  \"assignments\": [\n    {\n"
                + "      \"guid\": \"" + externalGuid + "\",\n"
                + "      \"tagIds\": [\"" + expected.Definitions[0].Id + "\"]\n"
                + "    }\n  ]\n}";
            File.WriteAllText(assignmentPath, externalJson);
            File.SetLastWriteTimeUtc(assignmentPath, originalWriteTime);

            var desiredDefinitions = CopyDefinitionsWithFirstFavoriteToggled(expected.Definitions);
            var result = _gateway.CompareAndSetTagState(
                expected,
                new ProjectTagStateSnapshot(desiredDefinitions, expected.AssignmentsByGuid));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(externalJson));
            var after = _gateway.CaptureTagState();
            Assert.That(after.Definitions, Is.EqualTo(expected.Definitions));
            Assert.That(after.AssignmentsByGuid.Keys, Is.EqualTo(new[] { externalGuid }));
        }

        [TestCase("assignments-applied")]
        [TestCase("definitions-applied")]
        public void InterruptedTagStateCommit_RecoversExactOriginalFromJournal(string failurePhase)
        {
            var expected = _gateway.CaptureTagState();
            var desiredAssignments = CopyAssignments(expected.AssignmentsByGuid);
            desiredAssignments[Guid.NewGuid().ToString("N")] = new[] { expected.Definitions[0].Id };
            var desired = new ProjectTagStateSnapshot(
                CopyDefinitionsWithFirstFavoriteToggled(expected.Definitions),
                desiredAssignments);
            var recoveryBlocked = false;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, failurePhase, StringComparison.Ordinal))
                {
                    recoveryBlocked = true;
                    throw new IOException("Simulated interrupted tag transaction.");
                }

                if (recoveryBlocked && string.Equals(phase, "before-recovery", StringComparison.Ordinal))
                {
                    throw new IOException("Simulated unavailable recovery boundary.");
                }
            };

            var result = _gateway.CompareAndSetTagState(expected, desired);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            var recovered = new UnityProjectAssetGateway();
            var recoveredState = ((IProjectTagDefinitionGateway)recovered).CaptureTagState();

            Assert.That(ProjectTagStateSnapshot.ContentEquals(recoveredState, expected), Is.True);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        // A definitions-only edit used to journal the project's whole assignment
        // table twice - ~20 MB of pretty-printed JSON to rename a tag at 50k
        // assignments, on the way in and again on undo.
        [Test]
        public void TagStateCompareAndSet_WithUnchangedAssignments_WritesNoAssignmentTableToTheJournal()
        {
            var seeded = SeedAssignments(5000);
            var journalBytesAtPrepare = -1L;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "prepared", StringComparison.Ordinal))
                {
                    journalBytesAtPrepare =
                        new FileInfo(UnityProjectAssetGateway.TagTransactionPathForTests).Length;
                }
            };

            var result = _gateway.CompareAndSetTagState(
                seeded,
                new ProjectTagStateSnapshot(
                    CopyDefinitionsWithFirstFavoriteToggled(seeded.Definitions),
                    seeded.AssignmentsByGuid));

            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(journalBytesAtPrepare, Is.GreaterThan(0L), "the journal must still be written");
            Assert.That(
                journalBytesAtPrepare, Is.LessThan(4096L),
                "an edit that moves no assignment must not journal the assignment table twice");
            Assert.That(
                _gateway.CaptureTagState().AssignmentsByGuid,
                Has.Count.EqualTo(seeded.AssignmentsByGuid.Count),
                "the assignments themselves must be untouched");
        }

        // The omission and the recovery have to agree: a journal that recorded
        // no assignment change must leave the live table alone, not replace it
        // with the empty one it deliberately carries.
        [Test]
        public void InterruptedDefinitionsOnlyCommit_RecoversWithoutErasingTheAssignments()
        {
            var seeded = SeedAssignments(64);
            var recoveryBlocked = false;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "definitions-applied", StringComparison.Ordinal))
                {
                    recoveryBlocked = true;
                    throw new IOException("Simulated interrupted tag transaction.");
                }

                if (recoveryBlocked && string.Equals(phase, "before-recovery", StringComparison.Ordinal))
                {
                    throw new IOException("Simulated unavailable recovery boundary.");
                }
            };

            var result = _gateway.CompareAndSetTagState(
                seeded,
                new ProjectTagStateSnapshot(
                    CopyDefinitionsWithFirstFavoriteToggled(seeded.Definitions),
                    seeded.AssignmentsByGuid));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            var recovered = new UnityProjectAssetGateway();
            var recoveredState = ((IProjectTagDefinitionGateway)recovered).CaptureTagState();

            Assert.That(ProjectTagStateSnapshot.ContentEquals(recoveredState, seeded), Is.True,
                "recovery must restore the original definitions AND keep every assignment");
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        // RecordCreatedAsset reads Exists to decide whether an entry that
        // landed on disk is an asset it can undo. A hidden or '~'-suffixed
        // file is on disk but Unity refuses to import it, so the honest answer
        // is false - which is why Exists must never become File.Exists.
        [Test]
        public void Exists_ForATildeSuffixedFileOnDisk_IsFalse()
        {
            using var scope = new TempAssetScope();
            var assetPath = scope.FolderPath + "/HiddenFromUnity.txt~";
            File.WriteAllText(assetPath, "on disk, not an asset");
            AssetDatabase.Refresh();

            Assert.That(File.Exists(assetPath), Is.True, "the fixture must really be on disk");
            Assert.That(new UnityProjectAssetGateway().Exists(AssetPath.From(assetPath)), Is.False);
        }

        [Test]
        public void Exists_ForAnImportedFile_IsTrue()
        {
            using var scope = new TempAssetScope();
            var assetPath = scope.CreateTextAsset("Imported.txt", "imported");

            Assert.That(new UnityProjectAssetGateway().Exists(AssetPath.From(assetPath)), Is.True);
        }

        [Test]
        public void Exists_ForAFolder_IsTrue()
        {
            using var scope = new TempAssetScope();

            Assert.That(new UnityProjectAssetGateway().Exists(AssetPath.From(scope.FolderPath)), Is.True);
        }

        [Test]
        public void Exists_ForAPathThatWasNeverCreated_IsFalse()
        {
            using var scope = new TempAssetScope();
            var assetPath = scope.FolderPath + "/NeverCreated.txt";

            Assert.That(File.Exists(assetPath), Is.False, "the fixture must really be absent");
            Assert.That(new UnityProjectAssetGateway().Exists(AssetPath.From(assetPath)), Is.False);
        }

        // The point of the fix: presence is answered from the database, not by
        // deserialising the asset. IsMainAssetAtPathLoaded is the instrument,
        // and the last two assertions prove it can actually see a load - so a
        // regression to LoadMainAssetAtPath fails the middle assertion instead
        // of passing vacuously.
        [Test]
        public void Exists_DoesNotLoadTheAssetIntoMemory()
        {
            using var scope = new TempAssetScope();
            var assetPath = scope.CreateTextAsset("NotLoadedByExists.txt", new string('a', 64 * 1024));
            EditorUtility.UnloadUnusedAssetsImmediate();
            Assert.That(
                AssetDatabase.IsMainAssetAtPathLoaded(assetPath), Is.False,
                "precondition: the asset must start unloaded for this probe to mean anything");

            var exists = new UnityProjectAssetGateway().Exists(AssetPath.From(assetPath));

            Assert.That(exists, Is.True);
            Assert.That(
                AssetDatabase.IsMainAssetAtPathLoaded(assetPath), Is.False,
                "Exists must answer from the database, not by deserialising the asset");
            Assert.That(AssetDatabase.LoadMainAssetAtPath(assetPath), Is.Not.Null);
            Assert.That(
                AssetDatabase.IsMainAssetAtPathLoaded(assetPath), Is.True,
                "the probe must be able to observe a load, or the assertion above proves nothing");
        }

        private ProjectTagStateSnapshot SeedAssignments(int count)
        {
            var current = _gateway.CaptureTagState();
            var assignments = CopyAssignments(current.AssignmentsByGuid);
            for (var i = 0; i < count; i++)
            {
                assignments[Guid.NewGuid().ToString("N")] = new[] { current.Definitions[0].Id };
            }

            var seed = _gateway.CompareAndSetTagState(
                current,
                new ProjectTagStateSnapshot(current.Definitions, assignments));
            Assert.That(seed.Succeeded, Is.True, seed.ErrorMessage);
            return _gateway.CaptureTagState();
        }

        private static ZepTag[] CopyDefinitionsWithFirstFavoriteToggled(
            IReadOnlyList<ZepTag> definitions)
        {
            var copy = new ZepTag[definitions.Count];
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                copy[i] = i == 0
                    ? new ZepTag(
                        definition.Id,
                        definition.Name,
                        definition.Color,
                        !definition.IsFavorite)
                    : definition;
            }

            return copy;
        }

        private static Dictionary<string, IReadOnlyList<string>> CopyAssignments(
            IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in assignments)
            {
                var tagIds = new string[assignment.Value.Count];
                for (var i = 0; i < assignment.Value.Count; i++)
                {
                    tagIds[i] = assignment.Value[i];
                }

                copy[assignment.Key] = tagIds;
            }

            return copy;
        }
        [Test]
        public void AssignmentCommand_FreshUndoSnapshot()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var unrelatedPath = scope.CreateTextAsset("Unrelated.txt", "unrelated");
            var targetGuid = GetImportedGuid(targetPath);
            var unrelatedGuid = GetImportedGuid(unrelatedPath);
            var tagIds = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetGuid] = new[] { tagIds[0] },
                [unrelatedGuid] = new[] { tagIds[0] },
            });

            var assignmentPath = GetAssignmentPath();
            var writeTime = File.GetLastWriteTimeUtc(assignmentPath);
            WriteAssignments(assignmentPath, new Dictionary<string, IReadOnlyList<string>>
            {
                [targetGuid] = new[] { tagIds[1] },
                [unrelatedGuid] = new[] { tagIds[1] },
            });
            File.SetLastWriteTimeUtc(assignmentPath, writeTime);

            var commands = new ProjectFileCommandService(new UnityProjectAssetGateway());
            var change = commands.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetPath] = new[] { tagIds[0] },
            });

            Assert.That(change.Succeeded, Is.True, change.ErrorMessage);
            Assert.That(commands.CanUndo, Is.True);
            Assert.That(ReadAssignments(assignmentPath, targetGuid), Is.EqualTo(new[] { tagIds[0] }));
            Assert.That(ReadAssignments(assignmentPath, unrelatedGuid), Is.EqualTo(new[] { tagIds[1] }));

            var undo = commands.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(ReadAssignments(assignmentPath, targetGuid), Is.EqualTo(new[] { tagIds[1] }));
            Assert.That(ReadAssignments(assignmentPath, unrelatedGuid), Is.EqualTo(new[] { tagIds[1] }));

            var redo = commands.Redo();

            Assert.That(redo.Succeeded, Is.True, redo.ErrorMessage);
            Assert.That(ReadAssignments(assignmentPath, targetGuid), Is.EqualTo(new[] { tagIds[0] }));
            Assert.That(ReadAssignments(assignmentPath, unrelatedGuid), Is.EqualTo(new[] { tagIds[1] }));
        }

        [Test]
        public void AssignmentCommand_StaleNoOpReevaluated()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetGuid] = new[] { tagIds[0] },
            });

            var assignmentPath = GetAssignmentPath();
            var writeTime = File.GetLastWriteTimeUtc(assignmentPath);
            WriteAssignments(assignmentPath, new Dictionary<string, IReadOnlyList<string>>
            {
                [targetGuid] = new[] { tagIds[1] },
            });
            File.SetLastWriteTimeUtc(assignmentPath, writeTime);

            var commands = new ProjectFileCommandService(new UnityProjectAssetGateway());
            var change = commands.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetPath] = new[] { tagIds[0] },
            });

            Assert.That(change.Succeeded, Is.True, change.ErrorMessage);
            Assert.That(commands.CanUndo, Is.True);
            Assert.That(ReadAssignments(assignmentPath, targetGuid), Is.EqualTo(new[] { tagIds[0] }));

            var undo = commands.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(ReadAssignments(assignmentPath, targetGuid), Is.EqualTo(new[] { tagIds[1] }));
        }

        [Test]
        public void AssignmentCommand_ConflictRefusedWithoutHistory()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetGuid] = new[] { tagIds[0] },
            });

            var assignmentPath = GetAssignmentPath();
            var readableBytes = File.ReadAllText(assignmentPath);
            var writeTime = File.GetLastWriteTimeUtc(assignmentPath);
            const string conflictBytes = "<<<<<<< HEAD\n{\"assignments\":[]}\n=======\n{\"assignments\":[]}\n>>>>>>> theirs\n";
            try
            {
                File.WriteAllText(assignmentPath, conflictBytes);
                File.SetLastWriteTimeUtc(assignmentPath, writeTime);
                var commands = new ProjectFileCommandService(new UnityProjectAssetGateway());

                var apparentNoOp = commands.SetTags(new Dictionary<string, IReadOnlyList<string>>
                {
                    [targetPath] = new[] { tagIds[0] },
                });
                var changed = commands.SetTags(new Dictionary<string, IReadOnlyList<string>>
                {
                    [targetPath] = new[] { tagIds[1] },
                });

                Assert.That(apparentNoOp.Succeeded, Is.False);
                Assert.That(changed.Succeeded, Is.False);
                Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(conflictBytes));
                Assert.That(new UnityProjectAssetGateway().GetTagIds(AssetPath.From(targetPath)), Is.EqualTo(new[] { tagIds[0] }));
                Assert.That(commands.CanUndo, Is.False);
            }
            finally
            {
                File.WriteAllText(assignmentPath, readableBytes);
                File.SetLastWriteTimeUtc(assignmentPath, writeTime);
            }
        }

        [Test]
        public void AssignmentCommand_PendingRecoveryFailureRefusesMutation()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            var expected = _gateway.CaptureTagState();
            var desiredAssignments = CopyAssignments(expected.AssignmentsByGuid);
            desiredAssignments[targetGuid] = new[] { tagIds[0] };
            var recoveryBlocked = false;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "assignments-applied", StringComparison.Ordinal))
                {
                    recoveryBlocked = true;
                    throw new IOException("Simulated interrupted assignment transaction.");
                }

                if (recoveryBlocked && string.Equals(phase, "before-recovery", StringComparison.Ordinal))
                {
                    throw new IOException("Simulated unavailable recovery boundary.");
                }
            };

            try
            {
                var interrupted = _gateway.CompareAndSetTagState(
                    expected,
                    new ProjectTagStateSnapshot(expected.Definitions, desiredAssignments));
                Assert.That(interrupted.Succeeded, Is.False);
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);

                var assignmentPath = GetAssignmentPath();
                var assignmentBytes = File.ReadAllText(assignmentPath);
                var journalBytes = File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests);
                var commands = new ProjectFileCommandService(new UnityProjectAssetGateway());
                var change = commands.SetTags(new Dictionary<string, IReadOnlyList<string>>
                {
                    [targetPath] = new[] { tagIds[1] },
                });

                Assert.That(change.Succeeded, Is.False);
                Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(assignmentBytes));
                Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));
                Assert.That(commands.CanUndo, Is.False);
            }
            finally
            {
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                ((IProjectTagDefinitionGateway)new UnityProjectAssetGateway()).CaptureTagState();
            }
        }

        [Test]
        public void AssignmentCommand_PreparationIsReadOnlyWhenUnchanged()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetGuid] = new[] { tagIds[0] },
            });

            var gateway = new UnityProjectAssetGateway();
            var preparation = (IProjectTagAssignmentPreparationGateway)gateway;
            var assignmentPath = GetAssignmentPath();
            var bytes = File.ReadAllText(assignmentPath);
            var writeTime = File.GetLastWriteTimeUtc(assignmentPath);
            var version = gateway.TagVersion;

            var first = preparation.PrepareTagAssignmentMutation();
            var second = preparation.PrepareTagAssignmentMutation();

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first.Succeeded, Is.True, first.ErrorMessage);
            Assert.That(second.Succeeded, Is.True, second.ErrorMessage);
            Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(assignmentPath), Is.EqualTo(writeTime));
            Assert.That(gateway.TagVersion, Is.EqualTo(version));

            var commands = new ProjectFileCommandService(gateway);
            var noOp = commands.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                [targetPath] = new[] { tagIds[0] },
            });

            Assert.That(noOp.Succeeded, Is.True, noOp.ErrorMessage);
            Assert.That(commands.CanUndo, Is.False);
        }

        [Test]
        public void RecoveryWrite_RollbackBeforeDirectAssignment()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            RetainInterruptedAssignmentTransaction(targetGuid, tagIds[0]);

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            var write = DirectSetTags(targetPath, tagIds[1]);

            Assert.That(write.Succeeded, Is.True, write.ErrorMessage);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            Assert.That(ReadAssignments(GetAssignmentPath(), targetGuid), Is.EqualTo(new[] { tagIds[1] }));
            Assert.That(_gateway.CaptureTagState().AssignmentsByGuid[targetGuid], Is.EqualTo(new[] { tagIds[1] }));
        }

        [Test]
        public void RecoveryWrite_CommittedTargetBeforeDirectAssignment()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            var expected = _gateway.CaptureTagState();
            var desiredDefinitions = CopyDefinitionsWithFirstFavoriteToggled(expected.Definitions);
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "before-journal-delete", StringComparison.Ordinal))
                {
                    throw new IOException("Keep committed journal.");
                }
            };

            var committed = _gateway.CompareAndSetTagState(
                expected,
                new ProjectTagStateSnapshot(desiredDefinitions, expected.AssignmentsByGuid));
            Assert.That(committed.Succeeded, Is.True, committed.ErrorMessage);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            var write = DirectSetTags(targetPath, tagIds[1]);

            Assert.That(write.Succeeded, Is.True, write.ErrorMessage);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            Assert.That(_gateway.CaptureTagState().Definitions, Is.EqualTo(desiredDefinitions));
            Assert.That(ReadAssignments(GetAssignmentPath(), targetGuid), Is.EqualTo(new[] { tagIds[1] }));
        }

        [Test]
        public void RecoveryWrite_BlockedRecoveryRefusesMutation()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            RetainInterruptedAssignmentTransaction(targetGuid, tagIds[0]);
            var assignmentBytes = File.ReadAllText(GetAssignmentPath());
            var journalBytes = File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests);

            var changed = DirectSetTags(targetPath, tagIds[1]);
            var apparentNoOp = DirectSetTags(targetPath, tagIds[0]);

            Assert.That(changed.Succeeded, Is.False);
            Assert.That(apparentNoOp.Succeeded, Is.False);
            Assert.That(File.ReadAllText(GetAssignmentPath()), Is.EqualTo(assignmentBytes));
            Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            Assert.That(DirectSetTags(targetPath, tagIds[1]).Succeeded, Is.True);
        }

        [Test]
        public void RecoveryWrite_JournalRetirementRequired()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagIds = GetDistinctValidTagIds();
            var expected = _gateway.CaptureTagState();
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "before-journal-delete", StringComparison.Ordinal))
                {
                    throw new IOException("Keep journal.");
                }
            };
            var committed = _gateway.CompareAndSetTagState(
                expected,
                new ProjectTagStateSnapshot(
                    CopyDefinitionsWithFirstFavoriteToggled(expected.Definitions),
                    expected.AssignmentsByGuid));
            Assert.That(committed.Succeeded, Is.True, committed.ErrorMessage);

            var capture = Assert.Throws<InvalidOperationException>(() => _gateway.CaptureTagState());
            var preparation = ((IProjectTagAssignmentPreparationGateway)new UnityProjectAssetGateway())
                .PrepareTagAssignmentMutation();
            var write = DirectSetTags(targetPath, tagIds[1]);

            Assert.That(capture, Is.Not.Null);
            Assert.That(preparation.Succeeded, Is.False);
            Assert.That(write.Succeeded, Is.False);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);
            Assert.That(ReadAssignments(GetAssignmentPath(), targetGuid), Is.Empty);

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            Assert.That(DirectSetTags(targetPath, tagIds[1]).Succeeded, Is.True);
        }

        [Test]
        public void RecoveryWrite_InvalidJournalRefusesMutation()
        {
            using var scope = new TempAssetScope();
            var targetPath = scope.CreateTextAsset("Target.txt", "target");
            var targetGuid = GetImportedGuid(targetPath);
            var tagId = GetDistinctValidTagIds()[0];
            var assignmentPath = GetAssignmentPath();
            var assignmentBytes = File.ReadAllText(assignmentPath);
            var journalPath = UnityProjectAssetGateway.TagTransactionPathForTests;

            try
            {
                foreach (var invalidJournal in new[] { "not json", string.Empty })
                {
                    File.WriteAllText(journalPath, invalidJournal);
                    Assert.That(DirectSetTags(targetPath, tagId).Succeeded, Is.False);
                    Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(assignmentBytes));
                    Assert.That(File.ReadAllText(journalPath), Is.EqualTo(invalidJournal));
                }

                File.Delete(journalPath);
                Directory.CreateDirectory(journalPath);
                Assert.That(DirectSetTags(targetPath, tagId).Succeeded, Is.False);
                Assert.That(Directory.Exists(journalPath), Is.True);
                Directory.Delete(journalPath);

                using (var handle = new FileStream(journalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.That(DirectSetTags(targetPath, tagId).Succeeded, Is.False);
                    Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(assignmentBytes));
                }
            }
            finally
            {
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                if (Directory.Exists(journalPath))
                {
                    Directory.Delete(journalPath);
                }
                else if (File.Exists(journalPath))
                {
                    File.Delete(journalPath);
                }
            }
        }

        [Test]
        public void RecoverySource_IncrementalReadsRestoreOriginal()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("RecoverySource.txt", "source");
            var guid = GetImportedGuid(path);
            var tags = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>> { [guid] = new[] { tags[0] } });
            var expectedSearchText = ExpectedTagSearchText(tags[0]);

            foreach (var source in CreateRecordSources())
            {
                RetainInterruptedAssignmentTransaction(guid, tags[1]);
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;

                var record = source.BuildRecord(AssetPath.From(path));

                Assert.That(record, Is.Not.Null);
                Assert.That(record.TagSearchText, Is.EqualTo(expectedSearchText));
                Assert.That(_gateway.CaptureTagState().AssignmentsByGuid[guid], Is.EqualTo(new[] { tags[0] }));
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            }
        }

        [Test]
        public void RecoverySource_EnumerationRecoversBeforeMetadata()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("RecoveryEnumeration.txt", "source");
            var guid = GetImportedGuid(path);
            var tags = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>> { [guid] = new[] { tags[0] } });
            var expectedSearchText = ExpectedTagSearchText(tags[0]);

            foreach (var source in CreateRecordSources())
            {
                RetainInterruptedAssignmentTransaction(guid, tags[1]);
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                source.PrepareForEnumeration();

                AssetRecord target = null;
                foreach (var record in source.GetRecords())
                {
                    if (string.Equals(record.Guid, guid, StringComparison.OrdinalIgnoreCase))
                    {
                        target = record;
                        break;
                    }
                }

                Assert.That(target, Is.Not.Null);
                Assert.That(target.TagSearchText, Is.EqualTo(expectedSearchText));
                Assert.That(_gateway.CaptureTagState().AssignmentsByGuid[guid], Is.EqualTo(new[] { tags[0] }));
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            }
        }

        [Test]
        public void RecoverySource_TypedRefusalAtEveryAcquisition()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("RecoveryTyped.txt", "source");
            var guid = GetImportedGuid(path);
            var tags = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>> { [guid] = new[] { tags[0] } });

            foreach (var source in CreateRecordSources())
            {
                RetainInterruptedAssignmentTransaction(guid, tags[1]);
                var journalBytes = File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests);
                var preparation = Assert.Throws<ProjectTagStateUnavailableException>(
                    () => source.PrepareForEnumeration());
                Assert.That(preparation.Message, Does.Contain("Block recovery."));
                Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                source.PrepareForEnumeration();

                RetainInterruptedAssignmentTransaction(guid, tags[1]);
                journalBytes = File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests);
                var incremental = Assert.Throws<ProjectTagStateUnavailableException>(
                    () => source.BuildRecord(AssetPath.From(path)));
                Assert.That(incremental.Message, Does.Contain("Block recovery."));
                Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                Assert.That(source.BuildRecord(AssetPath.From(path)).TagSearchText,
                    Is.EqualTo(ExpectedTagSearchText(tags[0])));
            }
        }

        [Test]
        public void RecoverySource_PreparedWorkerKeepsCapturedMetadata()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("RecoveryPreparedWorker.txt", "source");
            var guid = GetImportedGuid(path);
            var tags = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>> { [guid] = new[] { tags[0] } });
            var expectedSearchText = ExpectedTagSearchText(tags[0]);
            var source = new NativeFileSystemAssetRecordSource();
            source.PrepareForEnumeration();

            RetainInterruptedAssignmentTransaction(guid, tags[1]);
            var journalBytes = File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests);
            var recoveryAttempted = false;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "before-recovery", StringComparison.Ordinal))
                {
                    recoveryAttempted = true;
                    throw new IOException("Block recovery.");
                }
            };

            var worker = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var record in source.GetRecords())
                {
                    if (string.Equals(record.Guid, guid, StringComparison.OrdinalIgnoreCase))
                    {
                        return record;
                    }
                }

                return null;
            });
            var captured = worker.GetAwaiter().GetResult();

            Assert.That(captured, Is.Not.Null);
            Assert.That(captured.TagSearchText, Is.EqualTo(expectedSearchText));
            Assert.That(recoveryAttempted, Is.False);
            Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

            var refusal = Assert.Throws<ProjectTagStateUnavailableException>(
                () => source.PrepareForEnumeration());
            Assert.That(refusal.Message, Does.Contain("Block recovery."));
            Assert.That(recoveryAttempted, Is.True);
            Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            source.PrepareForEnumeration();
            var recovered = source.BuildRecord(AssetPath.From(path));
            Assert.That(recovered.TagSearchText, Is.EqualTo(expectedSearchText));
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        [Test]
        public void RecoverySource_BlockedPreparationCannotReuseOldMetadata()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("RecoveryBlocked.txt", "source");
            var guid = GetImportedGuid(path);
            var tags = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>> { [guid] = new[] { tags[0] } });
            var expectedSearchText = ExpectedTagSearchText(tags[0]);

            foreach (var source in CreateRecordSources())
            {
                source.PrepareForEnumeration();
                RetainInterruptedAssignmentTransaction(guid, tags[1]);
                var journalBytes = File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests);

                var preparation = Assert.Throws<ProjectTagStateUnavailableException>(() => source.PrepareForEnumeration());
                Assert.That(preparation.Message, Does.Contain("Block recovery."));
                Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

                var incremental = Assert.Throws<ProjectTagStateUnavailableException>(() => source.BuildRecord(AssetPath.From(path)));
                Assert.That(incremental.Message, Does.Contain("Block recovery."));
                Assert.That(File.ReadAllText(UnityProjectAssetGateway.TagTransactionPathForTests), Is.EqualTo(journalBytes));

                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                source.PrepareForEnumeration();
                var record = source.BuildRecord(AssetPath.From(path));
                Assert.That(record.TagSearchText, Is.EqualTo(expectedSearchText));
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            }
        }

        [Test]
        public void RecoverySource_SynchronizerPreservesCatalogAndRetries()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("RecoveryRetry.txt", "source");
            var guid = GetImportedGuid(path);
            var tags = GetDistinctValidTagIds();
            SeedAndLoadAssignments(new Dictionary<string, IReadOnlyList<string>> { [guid] = new[] { tags[0] } });
            var expectedSearchText = ExpectedTagSearchText(tags[0]);

            foreach (var source in CreateRecordSources())
            {
                RetainInterruptedAssignmentTransaction(guid, tags[1]);
                var refusal = Assert.Throws<ProjectTagStateUnavailableException>(() => source.BuildRecord(AssetPath.From(path)));
                Assert.That(refusal.Message, Does.Contain("Block recovery."));
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;

                var record = source.BuildRecord(AssetPath.From(path));
                Assert.That(record, Is.Not.Null);
                Assert.That(record.TagSearchText, Is.EqualTo(expectedSearchText));
                Assert.That(_gateway.CaptureTagState().AssignmentsByGuid[guid], Is.EqualTo(new[] { tags[0] }));
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            }
        }

        [Test]
        public void RecoveryHost_RestoresBeforeConstruction()
        {
            ZepControllerHost host = null;
            try
            {
                foreach (var failurePhase in new[] { "assignments-applied", "definitions-applied" })
                {
                    var original = _gateway.CaptureTagState();
                    var assignments = CopyAssignments(original.AssignmentsByGuid);
                    assignments[Guid.NewGuid().ToString("N")] = new[] { original.Definitions[0].Id };
                    var desired = new ProjectTagStateSnapshot(
                        CopyDefinitionsWithFirstFavoriteToggled(original.Definitions),
                        assignments);
                    var recoveryBlocked = false;
                    UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
                    {
                        if (string.Equals(phase, failurePhase, StringComparison.Ordinal))
                        {
                            recoveryBlocked = true;
                            throw new IOException("Retain interrupted journal.");
                        }

                        if (recoveryBlocked
                            && string.Equals(phase, "before-recovery", StringComparison.Ordinal))
                        {
                            throw new IOException("Block recovery.");
                        }
                    };

                    var interrupted = _gateway.CompareAndSetTagState(original, desired);
                    Assert.That(interrupted.Succeeded, Is.False);
                    Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);
                    var partial = new ProjectTagStateSnapshot(
                        ZepTagSettings.instance.Store.GetOrderedTags(),
                        ZepTagAssignmentStore.instance.GetAllTagIdsByGuid());
                    Assert.That(ProjectTagStateSnapshot.ContentEquals(partial, original), Is.False);

                    UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                    host = ZepControllerHost.Create(new ZepState());
                    Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
                    Assert.That(
                        ProjectTagStateSnapshot.ContentEquals(
                            new ProjectTagStateSnapshot(
                                ZepTagSettings.instance.Store.GetOrderedTags(),
                                ZepTagAssignmentStore.instance.GetAllTagIdsByGuid()),
                            original),
                        Is.True);
                    host.Dispose();
                    host = null;
                }

                var committedOriginal = _gateway.CaptureTagState();
                var committedDesired = new ProjectTagStateSnapshot(
                    CopyDefinitionsWithFirstFavoriteToggled(committedOriginal.Definitions),
                    committedOriginal.AssignmentsByGuid);
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
                {
                    if (string.Equals(phase, "before-journal-delete", StringComparison.Ordinal))
                    {
                        throw new IOException("Retain committed journal.");
                    }
                };
                var committed = _gateway.CompareAndSetTagState(committedOriginal, committedDesired);
                Assert.That(committed.Succeeded, Is.True, committed.ErrorMessage);
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);

                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                host = ZepControllerHost.Create(new ZepState());
                Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
                Assert.That(
                    ProjectTagStateSnapshot.ContentEquals(
                        new ProjectTagStateSnapshot(
                            ZepTagSettings.instance.Store.GetOrderedTags(),
                            ZepTagAssignmentStore.instance.GetAllTagIdsByGuid()),
                        committedDesired),
                    Is.True);
            }
            finally
            {
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                host?.Dispose();
            }
        }

        [Test]
        public void RecoveryHost_FailureLeavesCreationRetryable()
        {
            ZepControllerHost host = null;
            try
            {
                RetainInterruptedAssignmentTransaction(Guid.NewGuid().ToString("N"), GetDistinctValidTagIds()[0]);
                var assignmentBytes = File.ReadAllText(GetAssignmentPath());
                var journalPath = UnityProjectAssetGateway.TagTransactionPathForTests;
                var journalBytes = File.ReadAllText(journalPath);
                var viewModel = new ZepState();
                var primaryPane = viewModel.PrimaryPane;

                var failure = Assert.Throws<InvalidOperationException>(
                    () => ZepControllerHost.Create(viewModel));
                Assert.That(failure.Message, Does.Contain("Block recovery."));
                Assert.That(viewModel.PrimaryPane, Is.SameAs(primaryPane));
                Assert.That(File.ReadAllText(GetAssignmentPath()), Is.EqualTo(assignmentBytes));
                Assert.That(File.ReadAllText(journalPath), Is.EqualTo(journalBytes));

                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                host = ZepControllerHost.Create(viewModel);
                host.Dispose();
                host = null;

                RetainInterruptedAssignmentTransaction(Guid.NewGuid().ToString("N"), GetDistinctValidTagIds()[0]);
                var validJournalBytes = File.ReadAllText(journalPath);
                File.WriteAllText(journalPath, "{}");
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                try
                {
                    var corrupt = Assert.Throws<InvalidOperationException>(
                        () => ZepControllerHost.Create(new ZepState()));
                    Assert.That(corrupt.Message, Does.Contain("corrupt"));
                    Assert.That(File.ReadAllText(journalPath), Is.EqualTo("{}"));
                }
                finally
                {
                    File.WriteAllText(journalPath, validJournalBytes);
                }

                host = ZepControllerHost.Create(new ZepState());
                Assert.That(File.Exists(journalPath), Is.False);
            }
            finally
            {
                UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
                host?.Dispose();
            }
        }

        [Test]
        public void RecoveryHost_NoJournalDoesNotReloadAssignments()
        {
            var assignmentPath = GetAssignmentPath();
            var gateway = new UnityProjectAssetGateway();
            _ = ((IProjectTagDefinitionGateway)gateway).CaptureTagState();
            var version = gateway.TagVersion;
            var bytes = File.ReadAllText(assignmentPath);
            var writeTime = File.GetLastWriteTimeUtc(assignmentPath);

            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
            using (var host = ZepControllerHost.Create(new ZepState()))
            {
            }

            Assert.That(gateway.TagVersion, Is.EqualTo(version));
            Assert.That(File.ReadAllText(assignmentPath), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(assignmentPath), Is.EqualTo(writeTime));
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        [Test]
        public void RecoveryRead_OrdinaryReadsRestoreOriginal()
        {
            var gateway = new UnityProjectAssetGateway();
            RetainInterruptedAssignmentTransaction(Guid.NewGuid().ToString("N"), GetDistinctValidTagIds()[0]);

            Assert.Throws<ProjectTagStateUnavailableException>(() => { var ignored = gateway.TagVersion; });
            Assert.Throws<ProjectTagStateUnavailableException>(() =>
                gateway.GetTagIds(AssetPath.From("Assets/Unknown")));
            Assert.Throws<ProjectTagStateUnavailableException>(() =>
                gateway.GetTagIds(AssetPath.From("Assets/Unknown"), string.Empty));
            Assert.Throws<ProjectTagStateUnavailableException>(() =>
                gateway.GetTagIds(AssetPath.From("Assets/Unknown"), Guid.NewGuid().ToString("N")));

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            _ = gateway.TagVersion;
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        [Test]
        public void RecoveryRead_CommittedJournalRestoresDesired()
        {
            var original = _gateway.CaptureTagState();
            var guid = Guid.NewGuid().ToString("N");
            var desiredAssignments = CopyAssignments(original.AssignmentsByGuid);
            desiredAssignments[guid] = new[] { original.Definitions[0].Id };
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "before-journal-delete", StringComparison.Ordinal))
                {
                    throw new IOException("Retain committed journal.");
                }
            };

            var result = _gateway.CompareAndSetTagState(
                original,
                new ProjectTagStateSnapshot(original.Definitions, desiredAssignments));
            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            var gateway = new UnityProjectAssetGateway();
            _ = gateway.TagVersion;
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Unknown"), guid),
                Is.EqualTo(new[] { original.Definitions[0].Id }));
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        [Test]
        public void RecoveryRead_FailureRefusesAndRetries()
        {
            RetainInterruptedAssignmentTransaction(Guid.NewGuid().ToString("N"), GetDistinctValidTagIds()[0]);
            var journalPath = UnityProjectAssetGateway.TagTransactionPathForTests;
            var validJournal = File.ReadAllText(journalPath);
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            File.WriteAllText(journalPath, "{}");

            var gateway = new UnityProjectAssetGateway();
            Assert.Throws<ProjectTagStateUnavailableException>(() => { var ignored = gateway.TagVersion; });
            Assert.Throws<ProjectTagStateUnavailableException>(() =>
                gateway.GetTagIds(AssetPath.From("Assets/Unknown"), string.Empty));
            Assert.That(File.ReadAllText(journalPath), Is.EqualTo("{}"));

            File.WriteAllText(journalPath, validJournal);
            _ = gateway.TagVersion;
            Assert.That(File.Exists(journalPath), Is.False);
        }

        [Test]
        public void RecoveryRead_WarmGatewayDetectsNewTransaction()
        {
            var first = new UnityProjectAssetGateway();
            var second = new UnityProjectAssetGateway();
            _ = first.TagVersion;
            _ = second.TagVersion;

            RetainInterruptedAssignmentTransaction(Guid.NewGuid().ToString("N"), GetDistinctValidTagIds()[0]);
            Assert.Throws<ProjectTagStateUnavailableException>(() => { var ignored = first.TagVersion; });
            Assert.Throws<ProjectTagStateUnavailableException>(() =>
                second.GetTagIds(AssetPath.From("Assets/Unknown"), Guid.NewGuid().ToString("N")));

            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            _ = second.TagVersion;
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.False);
        }

        [Test]
        public void RecoveryRead_ReentrantReadCannotRecoverActiveCommit()
        {
            var original = _gateway.CaptureTagState();
            var assignments = CopyAssignments(original.AssignmentsByGuid);
            assignments[Guid.NewGuid().ToString("N")] = new[] { original.Definitions[0].Id };
            var reader = new UnityProjectAssetGateway();
            var attempted = false;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (!string.Equals(phase, "prepared", StringComparison.Ordinal))
                {
                    return;
                }

                attempted = true;
                Assert.Throws<ProjectTagStateUnavailableException>(() => { var ignored = reader.TagVersion; });
                Assert.Throws<ProjectTagStateUnavailableException>(() =>
                    reader.GetTagIds(AssetPath.From("Assets/Unknown"), string.Empty));
            };

            var result = _gateway.CompareAndSetTagState(
                original,
                new ProjectTagStateSnapshot(original.Definitions, assignments));
            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(attempted, Is.True);
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = null;
            _ = reader.TagVersion;
        }

        private static IAssetRecordSource[] CreateRecordSources()
        {
            return new IAssetRecordSource[]
            {
                new AssetDatabaseAssetRecordSource(),
                new HybridAssetRecordSource(),
                new NativeFileSystemAssetRecordSource(),
            };
        }

        private ProjectFileOperationResult DirectSetTags(string assetPath, string tagId)
        {
            return new UnityProjectAssetGateway().SetTagIds(
                new Dictionary<AssetPath, IReadOnlyList<string>>
                {
                    [AssetPath.From(assetPath)] = new[] { tagId },
                });
        }

        private void RetainInterruptedAssignmentTransaction(string targetGuid, string tagId)
        {
            var expected = _gateway.CaptureTagState();
            var assignments = CopyAssignments(expected.AssignmentsByGuid);
            assignments[targetGuid] = new[] { tagId };
            var interrupted = false;
            UnityProjectAssetGateway.TagTransactionJournal.CheckpointForTests = phase =>
            {
                if (string.Equals(phase, "assignments-applied", StringComparison.Ordinal))
                {
                    interrupted = true;
                    throw new IOException("Interrupt assignment transaction.");
                }

                if (interrupted && string.Equals(phase, "before-recovery", StringComparison.Ordinal))
                {
                    throw new IOException("Block recovery.");
                }
            };

            var result = _gateway.CompareAndSetTagState(
                expected,
                new ProjectTagStateSnapshot(expected.Definitions, assignments));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.Exists(UnityProjectAssetGateway.TagTransactionPathForTests), Is.True);
        }

        private string[] GetDistinctValidTagIds()
        {
            var current = _gateway.CaptureTagState();
            Assert.That(current.Definitions.Count, Is.GreaterThan(0));
            if (current.Definitions.Count == 1)
            {
                var first = current.Definitions[0];
                var definitions = new ZepTag[]
                {
                    first,
                    new ZepTag(
                        Guid.NewGuid().ToString("N"),
                        first.Name + " copy",
                        first.Color,
                        false),
                };
                var expanded = _gateway.CompareAndSetTagState(
                    current,
                    new ProjectTagStateSnapshot(definitions, current.AssignmentsByGuid));
                Assert.That(expanded.Succeeded, Is.True, expanded.ErrorMessage);
                current = _gateway.CaptureTagState();
            }

            var firstId = current.Definitions[0].Id;
            var secondId = current.Definitions[1].Id;
            Assert.That(firstId, Is.Not.Empty);
            Assert.That(secondId, Is.Not.Empty);
            Assert.That(secondId, Is.Not.EqualTo(firstId));
            return new[] { firstId, secondId };
        }

        private string ExpectedTagSearchText(string tagId)
        {
            var definitions = _gateway.CaptureTagState().Definitions;
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (!string.Equals(definition.Id, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(definition.Name))
                {
                    parts.Add(definition.Name);
                }

                var colorName = definition.Color.ToString();
                if (!string.IsNullOrWhiteSpace(colorName)
                    && !string.Equals(colorName, ZepTagColor.None.ToString(), StringComparison.Ordinal))
                {
                    parts.Add(colorName);
                }

                return string.Join("\n", parts);
            }

            Assert.Fail("The expected tag definition was not found.");
            return string.Empty;
        }

        private void SeedAndLoadAssignments(IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
        {
            var current = _gateway.CaptureTagState();
            var seeded = _gateway.CompareAndSetTagState(
                current,
                new ProjectTagStateSnapshot(current.Definitions, assignments));
            Assert.That(seeded.Succeeded, Is.True, seeded.ErrorMessage);
            _gateway.CaptureTagState();
        }

        private static string GetImportedGuid(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            Assert.That(guid, Is.Not.Empty, "the temporary asset must have an imported GUID");
            return guid;
        }

        private static string GetAssignmentPath()
        {
            return Path.Combine(
                ProjectPaths.GetProjectRootAbsolutePath(),
                "ProjectSettings",
                "Zep.TagAssignments.json");
        }

        private static IReadOnlyList<string> ReadAssignments(string assignmentPath, string guid)
        {
            return new TagAssignmentStore(assignmentPath).GetTagIds(guid);
        }

        private static void WriteAssignments(
            string assignmentPath,
            IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
        {
            var lines = new List<string>();
            foreach (var assignment in assignments)
            {
                var tagIds = assignment.Value ?? Array.Empty<string>();
                lines.Add("    {\n      \"guid\": \"" + assignment.Key + "\",\n      \"tagIds\": [\""
                    + string.Join("\", \"", tagIds) + "\"]\n    }");
            }

            File.WriteAllText(assignmentPath, "{\n  \"assignments\": [\n"
                + string.Join(",\n", lines) + "\n  ]\n}");
        }
    }
}
