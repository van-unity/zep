using Zep.Core.Indexing;
using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using NUnit.Framework;

namespace Zep.Editor.Tests.Core.FileOperations
{
    public sealed class ProjectFileCommandServiceTests
    {
        [Test]
        public void CreateFolder_RedoRestoresSavedFolderInsteadOfCreatingFreshOne()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway);

            var create = service.CreateFolder(AssetPath.From("Assets"), "New Folder");
            var undo = service.Undo();
            var redo = service.Redo();

            Assert.That(create.Succeeded, Is.True);
            Assert.That(create.AssetPath, Is.EqualTo("Assets/New Folder"));
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(redo.Succeeded, Is.True);
            Assert.That(redo.PreviousPath, Is.EqualTo(undo.AssetPath));
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Folder")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From(undo.AssetPath)), Is.False);
            Assert.That(gateway.CreateFolderCallCount, Is.EqualTo(1));
            Assert.That(gateway.RestoreUndoPayloadCallCount, Is.EqualTo(1));
        }

        [Test]
        public void CreateFolder_EmptyCreatedGuid_RollsBackWithoutRecordingHistory()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.ReturnEmptyGuid = true;
            var service = new ProjectFileCommandService(gateway);

            var result = service.CreateFolder(AssetPath.From("Assets"), "Unknown");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Unknown")), Is.False);
            Assert.That(service.CanUndo, Is.False);
        }

        // 5.6: a rename takes a NAME. "sub/child" used to reach
        // AssetPathFormat.ChangeName unvalidated, and the gateway's Move created
        // the folder on the way — a silent relocation recorded as a Rename whose
        // inverse left the new folder behind. "." and ".." only failed by
        // accident further down.
        [Test]
        public void Rename_ToAPathOrDotName_IsRefusedAndCreatesNothing()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var separator = service.Rename(AssetPath.From("Assets/Test.txt"), "sub/child");

            Assert.That(separator.Succeeded, Is.False);
            Assert.That(
                gateway.Exists(AssetPath.From("Assets/sub")),
                Is.False,
                "a refused rename must not have created a folder on the way");
            Assert.That(
                gateway.Exists(AssetPath.From("Assets/sub/child.txt")),
                Is.False);
            Assert.That(
                gateway.Exists(AssetPath.From("Assets/Test.txt")),
                Is.True,
                "the asset must still be where it was");
            Assert.That(service.CanUndo, Is.False, "a refusal records no history");

            Assert.That(
                service.Rename(AssetPath.From("Assets/Test.txt"), "sub\\child").Succeeded,
                Is.False,
                "the Windows separator is a separator too");
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), ".").Succeeded, Is.False);
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "..").Succeeded, Is.False);
        }

        [Test]
        public void Rename_ToAReservedDeviceNameOrATrailingDot_IsRefused()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "CON").Succeeded, Is.False);
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "con").Succeeded, Is.False,
                "the device name is case-insensitive");
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "nul.txt").Succeeded, Is.False,
                "Windows resolves the device by the stem, so an extension does not save it");
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "COM1").Succeeded, Is.False);
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "LPT9").Succeeded, Is.False);
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "name.").Succeeded, Is.False);

            // Names that merely resemble the reserved set stay legal, or the
            // guard would be refusing ordinary work.
            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "Console").Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Console.txt")), Is.True);
            Assert.That(service.Rename(AssetPath.From("Assets/Console.txt"), "COM10").Succeeded, Is.True,
                "only COM1-9 are devices");
            Assert.That(gateway.Exists(AssetPath.From("Assets/COM10.txt")), Is.True);
        }

        [Test]
        public void Rename_SupportsUndo()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/Test.txt"), "Renamed");
            var undo = service.Undo();

            Assert.That(rename.AssetPath, Is.EqualTo("Assets/Renamed.txt"));
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True);
        }

        // The reported defect: a drop onto a folder that already held a
        // same-named item reached AssetDatabase.MoveAsset, failed there, and
        // showed Unity's raw error for 1.9s — and the status bar's
        // identical-message dedupe made every repeat attempt silent, so the
        // drop looked like it did nothing at all.
        [Test]
        public void Move_DestinationOccupied_FailsWithPanelMessageAndLeavesSourceInPlace()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/A");
            gateway.AddFolder("Assets/B");
            gateway.AddFile("Assets/A/Foo.mat");
            gateway.AddFile("Assets/B/Foo.mat");
            var service = new ProjectFileCommandService(gateway);

            var move = service.Move(AssetPath.From("Assets/A/Foo.mat"), AssetPath.From("Assets/B"));

            Assert.That(move.Succeeded, Is.False);
            Assert.That(move.ErrorMessage, Is.EqualTo("'Foo.mat' already exists in Assets/B."),
                "the panel says what is in the way, in its own words");
            Assert.That(gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.True,
                "the source stays where it is");
            Assert.That(service.CanUndo, Is.False,
                "and a refused move records no command");
        }

        [Test]
        public void Move_DestinationOccupiedByAFolder_IsRefusedTheSameWay()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/A");
            gateway.AddFolder("Assets/B");
            gateway.AddFolder("Assets/A/Shared");
            gateway.AddFolder("Assets/B/Shared");
            gateway.AddFile("Assets/A/Shared/Inner.txt");
            var service = new ProjectFileCommandService(gateway);

            var move = service.Move(AssetPath.From("Assets/A/Shared"), AssetPath.From("Assets/B"));

            Assert.That(move.Succeeded, Is.False);
            Assert.That(move.ErrorMessage, Is.EqualTo("'Shared' already exists in Assets/B."));
            Assert.That(gateway.Exists(AssetPath.From("Assets/A/Shared/Inner.txt")), Is.True,
                "the source subtree stays where it is");
            Assert.That(service.CanUndo, Is.False);
        }

        // The Keep Both resolution moves to an explicit name beside the item
        // already there. The undo is what makes it safe to offer: it restores
        // the original path, not the name the item was given on the way in.
        [Test]
        public void Move_ToAnExplicitDestinationName_LandsThereAndUndoesToTheOriginalPath()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/A");
            gateway.AddFolder("Assets/B");
            gateway.AddFile("Assets/A/Foo.mat");
            gateway.AddFile("Assets/B/Foo.mat");
            var service = new ProjectFileCommandService(gateway);

            var move = service.Move(
                AssetPath.From("Assets/A/Foo.mat"),
                AssetPath.From("Assets/B"),
                "Foo 1.mat");

            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(move.AssetPath, Is.EqualTo("Assets/B/Foo 1.mat"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Foo.mat")), Is.True,
                "the item that was there is untouched");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(gateway.Exists(AssetPath.From("Assets/A/Foo.mat")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Foo 1.mat")), Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Foo.mat")), Is.True);
        }

        // A rename is a move to a sibling name and had the same silent hole:
        // the name that was already taken reached AssetDatabase.MoveAsset,
        // failed there, and left Unity's raw error in a status bar that then
        // deduped every retry away.
        [Test]
        public void Rename_OntoAnExistingSibling_IsRefusedInThePanelsOwnWords()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/B");
            gateway.AddFile("Assets/B/Foo.mat");
            gateway.AddFile("Assets/B/Bar.mat");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/B/Bar.mat"), "Foo");

            Assert.That(rename.Succeeded, Is.False);
            Assert.That(rename.ErrorMessage, Is.EqualTo("'Foo.mat' already exists in Assets/B."),
                "the panel says what is in the way, in the words the move conflict uses");
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Bar.mat")), Is.True,
                "the item keeps its name");
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Foo.mat")), Is.True,
                "and the item already there is untouched");
            Assert.That(service.CanUndo, Is.False,
                "a refused rename records no command");
        }

        [Test]
        public void Rename_OfAFolderOntoAnExistingSiblingFolder_IsRefusedTheSameWay()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/Art");
            gateway.AddFolder("Assets/Audio");
            gateway.AddFile("Assets/Audio/Clip.wav");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/Audio"), "Art");

            Assert.That(rename.Succeeded, Is.False);
            Assert.That(rename.ErrorMessage, Is.EqualTo("'Art' already exists in Assets."));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Audio/Clip.wav")), Is.True);
            Assert.That(service.CanUndo, Is.False);
        }

        // Changing only the case of an item's own name is not a collision: the
        // destination IS the source. On a case-insensitive volume the gateway
        // would answer "that exists" — the probe settles the identity first, so
        // it never asks.
        [Test]
        public void Rename_ChangingOnlyTheCaseOfItsOwnName_IsStillAllowed()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/foo.mat");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/foo.mat"), "Foo");

            Assert.That(rename.Succeeded, Is.True, rename.ErrorMessage);
            Assert.That(rename.AssetPath, Is.EqualTo("Assets/Foo.mat"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Foo.mat")), Is.True);
        }

        [Test]
        public void Rename_ToAFreeSiblingName_StillLandsAndUndoes()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/B");
            gateway.AddFile("Assets/B/Foo.mat");
            gateway.AddFile("Assets/B/Bar.mat");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/B/Bar.mat"), "Baz");

            Assert.That(rename.Succeeded, Is.True, rename.ErrorMessage);
            Assert.That(rename.AssetPath, Is.EqualTo("Assets/B/Baz.mat"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Bar.mat")), Is.False);

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(gateway.Exists(AssetPath.From("Assets/B/Bar.mat")), Is.True);
        }

        [Test]
        public void Rename_OfAPopulatedFolder_TakesItsContentsWithIt()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/Art");
            gateway.AddFolder("Assets/Art/UI");
            gateway.AddFile("Assets/Art/Hero.png");
            gateway.AddFile("Assets/Art/UI/Icon.png");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/Art"), "Illustration");

            Assert.That(rename.AssetPath, Is.EqualTo("Assets/Illustration"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Illustration/Hero.png")), Is.True,
                "a renamed folder takes its contents with it");
            Assert.That(gateway.Exists(AssetPath.From("Assets/Illustration/UI/Icon.png")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Art/Hero.png")), Is.False,
                "and leaves nothing behind at the old path");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Art/Hero.png")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Art/UI/Icon.png")), Is.True);
        }

        [Test]
        public void Rename_UndoRefusesOutOfBandReplacementAtTheSamePath()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.Rename(AssetPath.From("Assets/Test.txt"), "Renamed").Succeeded, Is.True);
            gateway.RemoveDirect("Assets/Renamed.txt");
            gateway.AddFile("Assets/Renamed.txt");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Renamed.txt")), Is.True,
                "Undo must leave the replacement untouched");
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.False);
        }

        // The reported case: a panel delete reverted with git, then Cmd+Z. The
        // asset is already back where the undo wanted it, byte for byte, so the
        // undo completes by dropping the redundant UndoStorage copy. Failing instead
        // is expensive: the bridge maps one native undo step to one command, so
        // a refused replay resets the whole panel timeline.
        [Test]
        public void Delete_UndoAfterTheFileWasRestoredExternally_SucceedsAndDropsTheUndoStorageCopy()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var assetGuid = gateway.GetGuid(AssetPath.From("Assets/Test.txt"));
            var service = new ProjectFileCommandService(gateway);

            var delete = service.Delete(AssetPath.From("Assets/Test.txt"));
            Assert.That(delete.Succeeded, Is.True);
            gateway.RestoreExternally("Assets/Test.txt", assetGuid);

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(undo.AssetPath, Is.EqualTo("Assets/Test.txt"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From(delete.AssetPath)), Is.False,
                "the redundant UndoStorage copy must be gone");
            Assert.That(gateway.RestoreUndoPayloadCallCount, Is.EqualTo(1),
                "the storage boundary verifies and drops the redundant payload");
        }

        // Redo must still work after that shortcut: the payload left its slot,
        // but the slot itself is reusable, so the delete replays normally.
        [Test]
        public void Delete_RedoAfterAnExternallyRestoredUndo_DeletesTheFileAgain()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var assetGuid = gateway.GetGuid(AssetPath.From("Assets/Test.txt"));
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.Delete(AssetPath.From("Assets/Test.txt")).Succeeded, Is.True);
            gateway.RestoreExternally("Assets/Test.txt", assetGuid);
            Assert.That(service.Undo().Succeeded, Is.True);

            var redo = service.Redo();

            Assert.That(redo.Succeeded, Is.True, redo.ErrorMessage);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.False);
        }

        // A different item at the original location cannot be chosen between
        // from inside a replay, so the undo fails — but it names the conflict
        // and leaves BOTH sides untouched without overwriting the project item.
        [Test]
        public void Delete_UndoWhenSomethingElseTookTheLocation_FailsWithoutTouchingEitherSide()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var delete = service.Delete(AssetPath.From("Assets/Test.txt"));
            Assert.That(delete.Succeeded, Is.True);
            gateway.AddFile("Assets/Test.txt");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.False);
            Assert.That(undo.ErrorMessage, Does.Contain("restore conflict"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From(delete.AssetPath)), Is.True,
                "the UndoStorage copy must survive so the conflict can still be resolved");
        }

        [Test]
        public void Duplicate_RedoRestoresSavedCopyInsteadOfCopyingFreshOne()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var duplicate = service.Duplicate(AssetPath.From("Assets/Test.txt"));

            Assert.That(duplicate.Succeeded, Is.True);
            Assert.That(duplicate.AssetPath, Is.EqualTo("Assets/Test 1.txt"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test 1.txt")), Is.True);

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test 1.txt")), Is.False);

            var redo = service.Redo();
            Assert.That(redo.Succeeded, Is.True);
            Assert.That(redo.PreviousPath, Is.EqualTo(undo.AssetPath));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test 1.txt")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From(undo.AssetPath)), Is.False);
            Assert.That(gateway.CopyCallCount, Is.EqualTo(1));
            Assert.That(gateway.RestoreUndoPayloadCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Duplicate_EmptyCreatedGuid_RollsBackWithoutRecordingHistory()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            gateway.ReturnEmptyGuid = true;
            var service = new ProjectFileCommandService(gateway);

            var result = service.Duplicate(AssetPath.From("Assets/Test.txt"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test 1.txt")), Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True);
            Assert.That(service.CanUndo, Is.False);
        }

        [Test]
        public void Duplicate_ReportsNoPreviousPath_SoOriginalIsNotEvicted()
        {
            // A duplicate is additive: the source is untouched. If the result
            // carried the source in its previous-path slot, the incremental
            // projection update would treat it as a vacated location and remove
            // the original from the panel (only the copy would remain visible).
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var duplicate = service.Duplicate(AssetPath.From("Assets/Test.txt"));

            Assert.That(duplicate.Succeeded, Is.True);
            Assert.That(duplicate.PreviousPath, Is.Empty);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True);
        }

        [Test]
        public void Delete_MovesItemToUndoStorageAndUndoRestoresIt()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var delete = service.Delete(AssetPath.From("Assets/Test.txt"));
            Assert.That(delete.Succeeded, Is.True);
            Assert.That(delete.AssetPath, Does.StartWith("Library/Zep/UndoPayloads/Assets"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.False);

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True);
        }

        [Test]
        public void Delete_RedoRefusesOutOfBandReplacementAtTheOriginalPath()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.Delete(AssetPath.From("Assets/Test.txt")).Succeeded, Is.True);
            Assert.That(service.Undo().Succeeded, Is.True);
            gateway.RemoveDirect("Assets/Test.txt");
            gateway.AddFile("Assets/Test.txt");

            var redo = service.Redo();

            Assert.That(redo.Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test.txt")), Is.True,
                "Redo must leave the replacement untouched");
        }

        [Test]
        public void ExecutionGroup_NotifiesOnceBeforePersisting()
        {
            var undoStack = new List<ProjectCommandRecord>();
            var redoStack = new List<ProjectCommandRecord>();
            var appliedRevision = 0;
            var savedRevision = -1;
            var saveCount = 0;
            var notificationCount = 0;
            var notificationCommandCount = 0;
            var history = new ProjectCommandHistory(
                undoStack,
                redoStack,
                () =>
                {
                    saveCount++;
                    savedRevision = appliedRevision;
                });
            history.Executed += (_, count) =>
            {
                notificationCount++;
                notificationCommandCount = count;
                appliedRevision += count;
            };

            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway, history);

            service.BeginExecutionGroup();
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "One").Succeeded, Is.True);
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "  ").Succeeded, Is.False);
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "Two").Succeeded, Is.True);
            Assert.That(notificationCount, Is.Zero);
            Assert.That(saveCount, Is.Zero);
            service.EndExecutionGroup();

            Assert.That(notificationCount, Is.EqualTo(1));
            Assert.That(notificationCommandCount, Is.EqualTo(2));
            Assert.That(undoStack, Has.Count.EqualTo(2));
            Assert.That(saveCount, Is.EqualTo(1));
            Assert.That(savedRevision, Is.EqualTo(2));
        }

        // A bulk that has to roll its own earlier half back (Replace's
        // displacement when the move that was to follow it fails) undoes a
        // command inside the group that ran it. The Bridge maps the group onto
        // ONE native undo step sized by the count it is notified with, so a
        // command that was executed and reverted within the group must leave
        // history entirely: counted, the native step would stand for a record
        // no longer on the stack, and the next Cmd+Z would replay whatever sits
        // beneath it; transferred to the redo stack, a lone Redo would re-apply
        // the half that was rolled back.
        [Test]
        public void ExecutionGroup_ACommandUndoneInsideItsOwnGroup_LeavesHistoryAndIsNotCounted()
        {
            var undoStack = new List<ProjectCommandRecord>();
            var redoStack = new List<ProjectCommandRecord>();
            var notificationCount = 0;
            var notificationCommandCount = 0;
            var history = new ProjectCommandHistory(undoStack, redoStack, null);
            history.Executed += (_, count) =>
            {
                notificationCount++;
                notificationCommandCount = count;
            };

            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway, history);

            service.BeginExecutionGroup();
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "One").Succeeded, Is.True);
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "Two").Succeeded, Is.True);
            Assert.That(service.Undo().Succeeded, Is.True, "the group rolls its second half back");
            service.EndExecutionGroup();

            Assert.That(gateway.Exists(AssetPath.From("Assets/Two")), Is.False);
            Assert.That(notificationCount, Is.EqualTo(1));
            Assert.That(notificationCommandCount, Is.EqualTo(1), "the step stands for the one command that held");
            Assert.That(undoStack, Has.Count.EqualTo(1));
            Assert.That(redoStack, Is.Empty, "the reverted half is not offered as a redo");

            service.BeginExecutionGroup();
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "Three").Succeeded, Is.True);
            Assert.That(service.Undo().Succeeded, Is.True, "a group that rolls everything back");
            service.EndExecutionGroup();

            Assert.That(notificationCount, Is.EqualTo(1), "stands for nothing and registers no step");
            Assert.That(undoStack, Has.Count.EqualTo(1));
            Assert.That(redoStack, Is.Empty);

            // A record undone OUTSIDE the group that ran it keeps the normal
            // transfer: that is the replay path the Bridge drives through RunBulk.
            service.BeginExecutionGroup();
            Assert.That(service.Undo().Succeeded, Is.True);
            service.EndExecutionGroup();

            Assert.That(undoStack, Is.Empty);
            Assert.That(redoStack, Has.Count.EqualTo(1));
            Assert.That(notificationCount, Is.EqualTo(1));
        }

        [Test]
        public void Undo_PersistsOneAtomicStackTransition()
        {
            var saveCount = 0;
            var history = new ProjectCommandHistory(
                new List<ProjectCommandRecord>(),
                new List<ProjectCommandRecord>(),
                () => saveCount++);
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway, history);

            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "New Folder").Succeeded, Is.True);
            saveCount = 0;

            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(saveCount, Is.EqualTo(1));
        }

        [Test]
        public void InvalidateHistory_ClearsBothSidesAndNotifiesBridge()
        {
            var history = new ProjectCommandHistory();
            var invalidationCount = 0;
            history.Invalidated += () => invalidationCount++;
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway, history);

            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "One").Succeeded, Is.True);
            Assert.That(service.CreateFolder(AssetPath.From("Assets"), "Two").Succeeded, Is.True);
            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(service.CanUndo, Is.True);
            Assert.That(service.CanRedo, Is.True);

            service.InvalidateHistory();

            Assert.That(service.CanUndo, Is.False);
            Assert.That(service.CanRedo, Is.False);
            Assert.That(invalidationCount, Is.EqualTo(1));
        }

        [Test]
        public void Delete_ProjectRoots_IsRejected()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Packages");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.Delete(AssetPath.From("Assets")).Succeeded, Is.False);
            Assert.That(service.Delete(AssetPath.From("Packages")).Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Packages")), Is.True);
            Assert.That(service.CanUndo, Is.False);
        }

        [Test]
        public void Delete_PackageRoot_IsRejected()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Packages");
            gateway.AddFolder("Packages/com.demo");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.Delete(AssetPath.From("Packages/com.demo")).Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Packages/com.demo")), Is.True);
            Assert.That(service.CanUndo, Is.False);
        }

        [Test]
        public void CreateFolderUndo_MovesFolderToUndoStorageInsteadOfDeleting()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway);

            service.CreateFolder(AssetPath.From("Assets"), "New Folder");
            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Folder")), Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Library/Zep/UndoPayloads/Assets/New Folder")), Is.True);
        }

        [Test]
        public void DuplicateUndo_MovesCopyToUndoStorageInsteadOfDeleting()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            service.Duplicate(AssetPath.From("Assets/Test.txt"));
            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Test 1.txt")), Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Library/Zep/UndoPayloads/Assets/Test 1.txt")), Is.True);
        }

        [Test]
        public void FailedUndo_IsDroppedFromHistoryInsteadOfWedgingTheStack()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var delete = service.Delete(AssetPath.From("Assets/Test.txt"));
            Assert.That(delete.Succeeded, Is.True);

            // Simulate a missing history payload purging the stored item out from under the
            // undo stack.
            gateway.RemoveDirect(delete.AssetPath);

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.False);
            // The dead command must not stay on top of the stack forever.
            Assert.That(service.CanUndo, Is.False);
        }

        [Test]
        public void PackagesPath_IsReadOnly()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Packages");
            gateway.AddFile("Packages/com.demo/File.txt");
            var service = new ProjectFileCommandService(gateway);

            var result = service.Delete(AssetPath.From("Packages/com.demo/File.txt"));

            Assert.That(result.Succeeded, Is.False);
        }

        [Test]
        public void SetTags_SupportsUndoAndRedo()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            var setTags = service.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                ["Assets/Test.txt"] = new[] { "red", "blue" }
            });

            Assert.That(setTags.Succeeded, Is.True);
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Test.txt")), Is.EqualTo(new[] { "red", "blue" }));

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(undo.HasTagAssignments, Is.True);
            Assert.That(undo.TagAssignments["Assets/Test.txt"], Is.Empty);
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Test.txt")), Is.Empty);

            var redo = service.Redo();
            Assert.That(redo.Succeeded, Is.True);
            Assert.That(redo.HasTagAssignments, Is.True);
            Assert.That(redo.TagAssignments["Assets/Test.txt"], Is.EqualTo(new[] { "red", "blue" }));
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Test.txt")), Is.EqualTo(new[] { "red", "blue" }));
        }

        [Test]
        public void NewCommandAfterUndo_ReleasesOnlyAbandonedPayload()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/First.txt");
            gateway.AddFile("Assets/Second.txt");
            var service = new ProjectFileCommandService(gateway);
            var first = service.Duplicate(AssetPath.From("Assets/First.txt"));
            Assert.That(service.Undo().Succeeded, Is.True);
            var abandoned = "Library/Zep/UndoPayloads/" + first.AssetPath;
            Assert.That(gateway.Exists(AssetPath.From(abandoned)), Is.True);

            var deletion = service.Delete(AssetPath.From("Assets/Second.txt"));

            Assert.That(deletion.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From(abandoned)), Is.False);
            Assert.That(gateway.Exists(AssetPath.From(deletion.AssetPath)), Is.True);
            Assert.That(service.CanRedo, Is.False);
            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Second.txt")), Is.True);
        }

        [Test]
        public void InvalidateHistory_ReleasesUndoAndRedoPayloads()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/First.txt");
            gateway.AddFile("Assets/Second.txt");
            var service = new ProjectFileCommandService(gateway);
            var deletion = service.Delete(AssetPath.From("Assets/First.txt"));
            var duplicate = service.Duplicate(AssetPath.From("Assets/Second.txt"));
            Assert.That(service.Undo().Succeeded, Is.True);

            service.InvalidateHistory();

            Assert.That(gateway.Exists(AssetPath.From(deletion.AssetPath)), Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Library/Zep/UndoPayloads/" + duplicate.AssetPath)), Is.False);
            Assert.That(service.CanUndo, Is.False);
            Assert.That(service.CanRedo, Is.False);
        }

        [Test]
        public void ClearRedo_ReleasesItsPayloadAndPreservesUndoPayload()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/First.txt");
            gateway.AddFile("Assets/Second.txt");
            var service = new ProjectFileCommandService(gateway);
            var deletion = service.Delete(AssetPath.From("Assets/First.txt"));
            var duplicate = service.Duplicate(AssetPath.From("Assets/Second.txt"));
            Assert.That(service.Undo().Succeeded, Is.True);

            Assert.That(service.ClearRedo(), Is.True);

            Assert.That(gateway.Exists(AssetPath.From(deletion.AssetPath)), Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Library/Zep/UndoPayloads/" + duplicate.AssetPath)), Is.False);
            Assert.That(service.Undo().Succeeded, Is.True);
        }

        [Test]
        public void HistoryLimit_ReleasesTheOldestPayloadOnly()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway);
            string oldest = null;
            string newest = null;
            for (var i = 0; i <= ProjectCommandHistory.MaxUndoDepth; i++)
            {
                var path = "Assets/Item" + i + ".txt";
                gateway.AddFile(path);
                var deleted = service.Delete(AssetPath.From(path));
                Assert.That(deleted.Succeeded, Is.True);
                if (i == 0) oldest = deleted.AssetPath;
                newest = deleted.AssetPath;
            }
            Assert.That(gateway.Exists(AssetPath.From(oldest)), Is.False);
            Assert.That(gateway.Exists(AssetPath.From(newest)), Is.True);
            Assert.That(service.Undo().Succeeded, Is.True);
        }

        // A record whose payload no longer forms a valid command rehydrates as
        // null. Dereferencing it threw an NRE that the bridge could only surface
        // as a whole-timeline reset naming neither the file nor the operation.
        [Test]
        public void Undo_DropsARecordThatCannotBeRehydrated()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var undoStack = new List<ProjectCommandRecord>
            {
                new ProjectCommandRecord { Kind = ProjectCommandKind.SetTagDefinitions },
            };
            var history = new ProjectCommandHistory(undoStack, new List<ProjectCommandRecord>(), () => { });
            var service = new ProjectFileCommandService(gateway, history);

            Assert.That(undoStack[0].ToCommand(), Is.Null,
                "guard the premise: an empty definition record cannot rehydrate");
            Assert.That(service.CanUndo, Is.True);

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.False);
            Assert.That(undo.ErrorMessage, Does.Contain("dropped from history"));
            Assert.That(service.CanUndo, Is.False, "the dead record must be discarded, not retried");
        }

        [Test]
        public void SetTags_UndoRefusesOutOfBandReplacementAtTheSamePath()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Test.txt");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                ["Assets/Test.txt"] = new[] { "red" }
            }).Succeeded, Is.True);
            gateway.RemoveDirect("Assets/Test.txt");
            gateway.AddFile("Assets/Test.txt");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.False);
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Test.txt")), Is.Empty,
                "Undo must not apply the old asset's tags to its replacement");
        }

        // A tag assignment is stored per GUID, so an asset relocated outside the
        // panel is still the same target. Validating the recorded PATH made an
        // ordinary folder rename in Unity's own Project window refuse the undo,
        // which the bridge could only report by resetting the whole timeline.
        [Test]
        public void SetTags_UndoFollowsAnAssetRelocatedOutsideThePanel()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/Art");
            gateway.AddFile("Assets/Art/Hero.png");
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                ["Assets/Art/Hero.png"] = new[] { "red" }
            }).Succeeded, Is.True);

            gateway.MoveDirectPreservingIdentity("Assets/Art/Hero.png", "Assets/Artwork/Hero.png");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True,
                "the GUID still identifies the asset, so undo must follow it to its new path");
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Artwork/Hero.png")), Is.Empty,
                "the tag must be cleared at the asset's CURRENT path");

            var redo = service.Redo();

            Assert.That(redo.Succeeded, Is.True);
            Assert.That(gateway.GetTagIds(AssetPath.From("Assets/Artwork/Hero.png")),
                Is.EqualTo(new[] { "red" }));
        }

        [Test]
        public void SetTags_AllowsPackagePaths()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Packages");
            gateway.AddFolder("Packages/com.demo");
            gateway.AddFile("Packages/com.demo/File.txt");
            var service = new ProjectFileCommandService(gateway);

            var setTags = service.SetTags(new Dictionary<string, IReadOnlyList<string>>
            {
                ["Packages/com.demo/File.txt"] = new[] { "blue" }
            });

            Assert.That(setTags.Succeeded, Is.True);
            Assert.That(gateway.GetTagIds(AssetPath.From("Packages/com.demo/File.txt")), Is.EqualTo(new[] { "blue" }));
        }

        [Test]
        public void RecordCreatedAsset_ThenUndo_MovesTheAssetToUndoStorage()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            // The UI recipe flow already wrote the asset to disk; RecordCreatedAsset only
            // records it in history.
            gateway.AddFile("Assets/New Material.mat");
            var service = new ProjectFileCommandService(gateway);

            var record = service.RecordCreatedAsset(AssetPath.From("Assets/New Material.mat"));
            Assert.That(record.Succeeded, Is.True);
            Assert.That(service.CanUndo, Is.True);

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Material.mat")), Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Library/Zep/UndoPayloads/Assets/New Material.mat")), Is.True);
        }

        [Test]
        public void RecordCreatedAsset_Undo_RemovesTheCreatedAsset_NotAnUnrelatedPriorOp()
        {
            // The F5 bug: recipe creation wasn't on the undo stack, so Cmd+Z reverted the
            // PREVIOUS panel op. With the create recorded, Undo must remove the created asset
            // and leave the earlier op intact.
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Existing.txt");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/Existing.txt"), "Renamed");
            Assert.That(rename.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Renamed.txt")), Is.True);

            gateway.AddFile("Assets/New Material.mat");
            service.RecordCreatedAsset(AssetPath.From("Assets/New Material.mat"));

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Material.mat")), Is.False, "created asset should be removed");
            Assert.That(gateway.Exists(AssetPath.From("Assets/Renamed.txt")), Is.True, "the unrelated rename must NOT be reverted");
            Assert.That(gateway.Exists(AssetPath.From("Assets/Existing.txt")), Is.False);
        }

        [Test]
        public void RecordCreatedAsset_Redo_RestoresTheAssetFromUndoStorage()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/New Material.mat");
            var service = new ProjectFileCommandService(gateway);

            service.RecordCreatedAsset(AssetPath.From("Assets/New Material.mat"));
            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Material.mat")), Is.False);

            var redo = service.Redo();
            Assert.That(redo.Succeeded, Is.True);
            Assert.That(redo.AssetPath, Is.EqualTo("Assets/New Material.mat"));
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Material.mat")), Is.True);
            Assert.That(service.CanUndo, Is.True);
            Assert.That(service.CanRedo, Is.False);
        }

        [Test]
        public void RecordCreatedAsset_RedoCollision_FailsWithoutReplacingTheNewAsset()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/New Material.mat");
            var service = new ProjectFileCommandService(gateway);

            service.RecordCreatedAsset(AssetPath.From("Assets/New Material.mat"));
            var undo = service.Undo();
            gateway.AddFile("Assets/New Material.mat");

            var redo = service.Redo();

            Assert.That(redo.Succeeded, Is.False);
            Assert.That(gateway.Exists(AssetPath.From("Assets/New Material.mat")), Is.True);
            Assert.That(gateway.Exists(AssetPath.From(undo.AssetPath)), Is.True,
                "the exact original payload must remain recoverable in UndoStorage");
            Assert.That(gateway.RestoreUndoPayloadCallCount, Is.Zero);
        }

        [Test]
        public void RecordCreatedAsset_EmptyPath_IsRejectedAndRecordsNothing()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            var service = new ProjectFileCommandService(gateway);

            var record = service.RecordCreatedAsset(AssetPath.From(string.Empty));
            Assert.That(record.Succeeded, Is.False);
            Assert.That(service.CanUndo, Is.False);
        }

        [Test]
        public void RecordCreatedAsset_MissingIdentityInvalidatesOlderHistory()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Existing.txt");
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.Rename(
                AssetPath.From("Assets/Existing.txt"),
                "Renamed").Succeeded, Is.True);

            gateway.AddFile("Assets/Unidentified.asset");
            gateway.ReturnEmptyGuid = true;
            var record = service.RecordCreatedAsset(AssetPath.From("Assets/Unidentified.asset"));

            Assert.That(record.Succeeded, Is.False);
            Assert.That(service.CanUndo, Is.False,
                "Cmd+Z must not fall through to the unrelated rename");
            Assert.That(service.CanRedo, Is.False);
        }

        // Creating a script starts a compile, and the new-command gate is shut
        // while Unity compiles. The asset is already on disk by then, so its
        // record asks the applied-command gate instead; asking the shut one
        // failed the record and wiped every earlier undo step with it.
        [Test]
        public void RecordCreatedAsset_AsksTheAppliedCommandGate_AndKeepsOlderHistory()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Existing.txt");
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.Rename(
                AssetPath.From("Assets/Existing.txt"),
                "Renamed").Succeeded, Is.True);

            gateway.AddFile("Assets/NewScript.cs");
            service.SetExecutionPreparation(() => false, () => true);
            var record = service.RecordCreatedAsset(AssetPath.From("Assets/NewScript.cs"));
            var refusedCommand = service.CreateFolder(AssetPath.From("Assets"), "Refused");

            Assert.That(record.Succeeded, Is.True, record.ErrorMessage);
            Assert.That(refusedCommand.Succeeded, Is.False,
                "a new command still waits for the new-command gate");
            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/NewScript.cs")), Is.False,
                "the first Undo removes the created script");
            Assert.That(service.CanUndo, Is.True, "the earlier rename is still undoable");
        }

        [Test]
        public void RecordCreatedAsset_WhenTheAppliedCommandGateRefuses_InvalidatesOlderHistory()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Existing.txt");
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.Rename(
                AssetPath.From("Assets/Existing.txt"),
                "Renamed").Succeeded, Is.True);

            gateway.AddFile("Assets/NewScript.cs");
            service.SetExecutionPreparation(() => true, () => false);
            var record = service.RecordCreatedAsset(AssetPath.From("Assets/NewScript.cs"));

            Assert.That(record.Succeeded, Is.False);
            Assert.That(service.CanUndo, Is.False,
                "Cmd+Z must not fall through to the unrelated rename");
        }

        [Test]
        public void RecordCreatedAsset_UnimportedEntryInABatch_IsSkippedAndTheRestStayUndoable()
        {
            // External drop of {visible, hidden, visible}. Unity never imports a
            // dotfile, so the gateway does not see it as an asset — routine, not
            // history corruption. The record loop must keep going: invalidating
            // here wiped both stacks and cost every later file its undo record.
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Existing.txt");
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.Rename(
                AssetPath.From("Assets/Existing.txt"),
                "Renamed").Succeeded, Is.True);

            gateway.AddFile("Assets/Dropped.png");
            gateway.AddFile("Assets/Dropped2.png");
            var droppedPaths = new[]
            {
                "Assets/Dropped.png",
                "Assets/.hidden",
                "Assets/Dropped2.png"
            };
            for (var i = 0; i < droppedPaths.Length; i++)
            {
                Assert.That(
                    service.RecordCreatedAsset(AssetPath.From(droppedPaths[i])).Succeeded,
                    Is.True,
                    droppedPaths[i] + " must not report a failure that stops the drop's record loop");
            }

            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(
                gateway.Exists(AssetPath.From("Assets/Dropped2.png")),
                Is.False,
                "the file dropped after the hidden one must have kept its own undo record");
            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(gateway.Exists(AssetPath.From("Assets/Dropped.png")), Is.False);
            Assert.That(
                service.CanUndo,
                Is.True,
                "nothing was inconsistent, so the unrelated earlier rename must survive");
            Assert.That(gateway.Exists(AssetPath.From("Assets/Renamed.txt")), Is.True);
        }

        [Test]
        public void ChangeTagDefinitions_DeleteFiltersEveryGuidAndUndoRestoresExactState()
        {
            var gateway = new FakeGateway();
            var originalDefinitions = new[]
            {
                Tag("custom", "Custom", ZepTagColor.Purple, false),
                Tag("red", "Red", ZepTagColor.Red, true)
            };
            var originalAssignments = new Dictionary<string, IReadOnlyList<string>>
            {
                ["guid-hidden"] = new[] { "custom", "red", "orphan" },
                ["guid-saved"] = new[] { "custom" }
            };
            gateway.SetTagStateDirect(originalDefinitions, originalAssignments);
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);

            var change = service.ChangeTagDefinitionsAfterPreparation(
                originalDefinitions,
                new[] { originalDefinitions[1] },
                removeUndefinedAssignments: false,
                assignmentTagIdToRemove: "  CUSTOM  ");

            Assert.That(change.Succeeded, Is.True, change.ErrorMessage);
            Assert.That(change.TagDefinitionsChanged, Is.True);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(new[] { originalDefinitions[1] }));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid.Keys, Is.EqualTo(new[] { "guid-hidden" }));
            Assert.That(
                gateway.CaptureTagState().AssignmentsByGuid["guid-hidden"],
                Is.EqualTo(new[] { "red", "orphan" }),
                "deleting one definition must preserve unrelated orphan assignments");

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(originalDefinitions));
            Assert.That(
                gateway.CaptureTagState().AssignmentsByGuid["guid-hidden"],
                Is.EqualTo(new[] { "custom", "red", "orphan" }));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-saved"], Is.EqualTo(new[] { "custom" }));

            var redo = service.Redo();
            Assert.That(redo.Succeeded, Is.True, redo.ErrorMessage);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(new[] { originalDefinitions[1] }));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid.ContainsKey("guid-saved"), Is.False);
        }

        [Test]
        public void ChangeTagDefinitions_UpdatePreservesOrderAndAllFieldsAcrossUndoRedo()
        {
            var gateway = new FakeGateway();
            var original = new[]
            {
                Tag("blue", "Blue", ZepTagColor.Blue, true),
                Tag("red", "Red", ZepTagColor.Red, false)
            };
            var desired = new[]
            {
                Tag("blue", "Sky", ZepTagColor.Green, false),
                original[1]
            };
            gateway.SetTagStateDirect(
                original,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["guid-a"] = new[] { "blue" }
                });
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);

            Assert.That(service.ChangeTagDefinitionsAfterPreparation(
                original,
                desired,
                removeUndefinedAssignments: false).Succeeded, Is.True);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(desired));
            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(original));
            Assert.That(service.Redo().Succeeded, Is.True);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(desired));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-a"], Is.EqualTo(new[] { "blue" }));
        }

        [Test]
        public void ChangeTagDefinitions_ResetRemovesEveryAssignmentAbsentFromDefaults()
        {
            var gateway = new FakeGateway();
            var original = new[]
            {
                Tag("red", "Red", ZepTagColor.Red, true),
                Tag("custom", "Custom", ZepTagColor.Purple, true)
            };
            gateway.SetTagStateDirect(
                original,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["guid-a"] = new[] { "custom", "orphan", "red" }
                });
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);

            var reset = service.ChangeTagDefinitionsAfterPreparation(
                original,
                new[] { original[0] },
                removeUndefinedAssignments: true);

            Assert.That(reset.Succeeded, Is.True, reset.ErrorMessage);
            Assert.That(reset.TagAssignmentSnapshotChanged, Is.True);
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-a"], Is.EqualTo(new[] { "red" }));
            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True);
            Assert.That(undo.TagAssignmentSnapshotChanged, Is.True);
            Assert.That(
                gateway.CaptureTagState().AssignmentsByGuid["guid-a"],
                Is.EqualTo(new[] { "custom", "orphan", "red" }));
        }

        [Test]
        public void ChangeTagDefinitions_ResetWithUnchangedDefinitionsFlagsAssignmentChangesAcrossReplay()
        {
            var gateway = new FakeGateway();
            var definitions = new[] { Tag("red", "Red", ZepTagColor.Red, true) };
            gateway.SetTagStateDirect(definitions, new Dictionary<string, IReadOnlyList<string>>
            {
                ["guid-mixed"] = new[] { "orphan", "red" },
                ["guid-orphan-only"] = new[] { "orphan" }
            });
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);

            var reset = service.ChangeTagDefinitionsAfterPreparation(definitions, definitions, removeUndefinedAssignments: true);

            Assert.That(reset.Succeeded, Is.True, reset.ErrorMessage);
            Assert.That(reset.TagAssignmentSnapshotChanged, Is.True);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(definitions));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid.Keys, Is.EqualTo(new[] { "guid-mixed" }));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-mixed"], Is.EqualTo(new[] { "red" }));

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(undo.TagAssignmentSnapshotChanged, Is.True);
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-orphan-only"], Is.EqualTo(new[] { "orphan" }));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-mixed"], Is.EqualTo(new[] { "orphan", "red" }));

            var redo = service.Redo();
            Assert.That(redo.Succeeded, Is.True, redo.ErrorMessage);
            Assert.That(redo.TagAssignmentSnapshotChanged, Is.True);
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid.Keys, Is.EqualTo(new[] { "guid-mixed" }));
            Assert.That(gateway.CaptureTagState().AssignmentsByGuid["guid-mixed"], Is.EqualTo(new[] { "red" }));
        }

        [TestCase("tags")]
        [TestCase("order")]
        [TestCase("guid")]
        public void SetTagDefinitionsCommand_FlagsChangedAssignmentContentWithUnchangedEntryCount(string change)
        {
            var gateway = new FakeGateway();
            var definitions = new[]
            {
                Tag("red", "Red", ZepTagColor.Red, true),
                Tag("blue", "Blue", ZepTagColor.Blue, true)
            };
            var original = new ProjectTagStateSnapshot(definitions, new Dictionary<string, IReadOnlyList<string>>
            {
                ["guid-a"] = new[] { "red", "blue" }
            });
            var desired = new ProjectTagStateSnapshot(definitions, new Dictionary<string, IReadOnlyList<string>>
            {
                [change == "guid" ? "guid-b" : "guid-a"] = change == "order"
                    ? new[] { "blue", "red" }
                    : change == "tags" ? new[] { "blue" } : new[] { "red", "blue" }
            });
            gateway.SetTagStateDirect(original.Definitions, original.AssignmentsByGuid);
            var command = new SetTagDefinitionsCommand(desired, original);

            var applied = command.Execute(gateway);

            Assert.That(applied.Succeeded, Is.True, applied.ErrorMessage);
            Assert.That(applied.TagAssignmentSnapshotChanged, Is.True);
            Assert.That(ProjectTagStateSnapshot.ContentEquals(gateway.CaptureTagState(), desired), Is.True);

            var copied = applied.WithTagAssignments(new Dictionary<string, IReadOnlyList<string>>
            {
                ["Assets/Tagged.txt"] = new[] { "blue" }
            });
            Assert.That(copied.TagAssignmentSnapshotChanged, Is.True, "existing result copies must preserve the reconciliation signal");
            Assert.That(copied.Succeeded, Is.EqualTo(applied.Succeeded));
            Assert.That(copied.TagDefinitionsChanged, Is.EqualTo(applied.TagDefinitionsChanged));
            Assert.That(copied.AssetPath, Is.EqualTo(applied.AssetPath));
            Assert.That(copied.PreviousPath, Is.EqualTo(applied.PreviousPath));
            Assert.That(copied.ErrorMessage, Is.EqualTo(applied.ErrorMessage));
            Assert.That(copied.TagAssignments["Assets/Tagged.txt"], Is.EqualTo(new[] { "blue" }));
        }

        [Test]
        public void SetTagDefinitionsCommand_AssignmentPreservingEditAndFailureDoNotFlagChanges()
        {
            var gateway = new FakeGateway();
            var definitions = new[] { Tag("red", "Red", ZepTagColor.Red, true) };
            var assignments = new Dictionary<string, IReadOnlyList<string>> { ["guid-a"] = new[] { "red" } };
            var original = new ProjectTagStateSnapshot(definitions, assignments);
            var renamed = new ProjectTagStateSnapshot(new[] { Tag("red", "Crimson", ZepTagColor.Red, true) }, assignments);
            gateway.SetTagStateDirect(original.Definitions, original.AssignmentsByGuid);
            var command = new SetTagDefinitionsCommand(renamed, original);

            var result = command.Execute(gateway);
            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(result.TagAssignmentSnapshotChanged, Is.False);
            Assert.That(result.WithTagAssignments(null).TagAssignmentSnapshotChanged, Is.False);

            var conflicting = new SetTagDefinitionsCommand(
                new ProjectTagStateSnapshot(renamed.Definitions,
                    new Dictionary<string, IReadOnlyList<string>> { ["guid-a"] = new[] { "orphan" } }),
                original);
            var rejected = conflicting.Execute(gateway);
            Assert.That(rejected.Succeeded, Is.False, "the old expected definition no longer matches");
            Assert.That(rejected.TagAssignmentSnapshotChanged, Is.False);
            Assert.That(ProjectTagStateSnapshot.ContentEquals(gateway.CaptureTagState(), renamed), Is.True);
        }

        [Test]
        public void ChangeTagDefinitions_NoOpCreatesNoMarkerAndNewBranchClearsRedo()
        {
            var gateway = new FakeGateway();
            var original = new[] { Tag("red", "Red", ZepTagColor.Red, true) };
            var firstChange = new[] { Tag("red", "Scarlet", ZepTagColor.Red, true) };
            var secondChange = new[] { Tag("red", "Ruby", ZepTagColor.Purple, false) };
            gateway.SetTagStateDirect(original, new Dictionary<string, IReadOnlyList<string>>());
            var service = new ProjectFileCommandService(gateway);

            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);
            Assert.That(service.ChangeTagDefinitionsAfterPreparation(
                original,
                original,
                removeUndefinedAssignments: false).Succeeded, Is.True);
            Assert.That(service.CanUndo, Is.False, "an identical definition snapshot is not an undo step");

            Assert.That(service.ChangeTagDefinitionsAfterPreparation(
                original,
                firstChange,
                removeUndefinedAssignments: false).Succeeded, Is.True);
            Assert.That(service.Undo().Succeeded, Is.True);
            Assert.That(service.CanRedo, Is.True);

            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);
            Assert.That(service.ChangeTagDefinitionsAfterPreparation(
                original,
                secondChange,
                removeUndefinedAssignments: false).Succeeded, Is.True);
            Assert.That(service.CanRedo, Is.False);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(secondChange));
        }

        [Test]
        public void ChangeTagDefinitions_UndoRefusesToOverwriteOutOfBandState()
        {
            var gateway = new FakeGateway();
            var original = new[] { Tag("red", "Red", ZepTagColor.Red, true) };
            var desired = new[] { Tag("red", "Scarlet", ZepTagColor.Red, true) };
            var external = new[] { Tag("red", "External", ZepTagColor.Blue, false) };
            gateway.SetTagStateDirect(original, new Dictionary<string, IReadOnlyList<string>>());
            var service = new ProjectFileCommandService(gateway);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);
            Assert.That(service.ChangeTagDefinitionsAfterPreparation(
                original,
                desired,
                removeUndefinedAssignments: false).Succeeded, Is.True);
            gateway.SetTagStateDirect(external, new Dictionary<string, IReadOnlyList<string>>());

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.False);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(external));
            Assert.That(service.CanUndo, Is.False);
        }

        private static ZepTag Tag(
            string id,
            string name,
            ZepTagColor color,
            bool isFavorite)
        {
            return new ZepTag(id, name, color, isFavorite);
        }

        // Identity resolution is a disk read on the real gateway (the sibling
        // .meta file), and rename/move/delete each paid for it twice: once in
        // the service, once again in the command's own forward Validate. The
        // second read compares a value against itself — nothing runs between
        // them — so it is skipped. Every path that can observe a changed
        // project (undo, redo, any rehydrated replay) must still validate;
        // that is the data-safety invariant, and each case below asserts it in
        // the same run as the saving.
        [Test]
        public void Rename_ResolvesTheIdentityOnceForward_AndStillValidatesOnUndoAndRedo()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Item.txt");
            var service = new ProjectFileCommandService(gateway);

            var before = gateway.GetGuidCallCount;
            var rename = service.Rename(AssetPath.From("Assets/Item.txt"), "Renamed.txt");
            Assert.That(rename.Succeeded, Is.True, rename.ErrorMessage);
            Assert.That(
                gateway.GetGuidCallCount - before,
                Is.EqualTo(1),
                "the forward pass must resolve the identity exactly once");

            before = gateway.GetGuidCallCount;
            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(
                gateway.GetGuidCallCount - before,
                Is.EqualTo(1),
                "undo replays against a project that may have changed — it must validate");

            before = gateway.GetGuidCallCount;
            var redo = service.Redo();
            Assert.That(redo.Succeeded, Is.True, redo.ErrorMessage);
            Assert.That(
                gateway.GetGuidCallCount - before,
                Is.EqualTo(1),
                "redo replays a rehydrated command — it must validate too");
        }

        [Test]
        public void MoveAndDelete_ResolveTheIdentityOnceForward()
        {
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFolder("Assets/Target");
            gateway.AddFile("Assets/Moved.txt");
            gateway.AddFile("Assets/Deleted.txt");
            var service = new ProjectFileCommandService(gateway);

            var before = gateway.GetGuidCallCount;
            var move = service.Move(AssetPath.From("Assets/Moved.txt"), AssetPath.From("Assets/Target"));
            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(gateway.GetGuidCallCount - before, Is.EqualTo(1));

            before = gateway.GetGuidCallCount;
            var delete = service.Delete(AssetPath.From("Assets/Deleted.txt"));
            Assert.That(delete.Succeeded, Is.True, delete.ErrorMessage);
            Assert.That(gateway.GetGuidCallCount - before, Is.EqualTo(1));
        }

        [Test]
        public void ACommandBuiltOutsideTheService_ValidatesOnItsFirstExecute()
        {
            // The red baseline, measured in this run rather than asserted from
            // memory: without the service's "already verified" hand-off, the
            // forward Execute resolves the identity itself — that read plus the
            // service's own is the pair this task removed one half of. It is
            // also the shape every rehydrated (FromRecord) command has.
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Standalone.txt");
            var path = AssetPath.From("Assets/Standalone.txt");
            var guid = gateway.GetGuid(path);

            var command = new RenameCommand(path, "Renamed.txt", false, guid);
            var before = gateway.GetGuidCallCount;
            var result = command.Execute(gateway);

            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(
                gateway.GetGuidCallCount - before,
                Is.EqualTo(1),
                "a command that was not handed a verified identity must read one");
        }

        [Test]
        public void AVerifiedCommandWhoseTargetWasReplaced_StillRefusesOnUndo()
        {
            // The hand-off must not survive past the one Execute it was granted
            // for: an out-of-band replacement between the operation and its undo
            // is exactly what Validate exists to catch.
            var gateway = new FakeGateway();
            gateway.AddFolder("Assets");
            gateway.AddFile("Assets/Victim.txt");
            var service = new ProjectFileCommandService(gateway);

            var rename = service.Rename(AssetPath.From("Assets/Victim.txt"), "Renamed.txt");
            Assert.That(rename.Succeeded, Is.True, rename.ErrorMessage);

            // Something else deletes the renamed file and puts a DIFFERENT item
            // at the same path.
            var renamedPath = AssetPath.From(rename.AssetPath);
            var displaced = gateway.Move(
                renamedPath,
                AssetPath.From("Assets/Elsewhere.txt"),
                false);
            Assert.That(displaced.Succeeded, Is.True, displaced.ErrorMessage);
            gateway.AddFile(rename.AssetPath);

            var undo = service.Undo();
            Assert.That(undo.Succeeded, Is.False);
            Assert.That(
                undo.ErrorMessage,
                Does.Contain("changed since the operation was recorded"));
        }

        // A rename moves no assignment at all, yet the record used to carry the
        // whole project's assignment table TWICE - a payload every later save
        // re-serialises and every domain reload re-parses.
        [Test]
        public void ChangeTagDefinitions_RenameRecordsNoAssignmentEntriesAndStillUndoes()
        {
            const int assignmentCount = 5000;
            var gateway = new FakeGateway();
            var original = new[] { Tag("red", "Red", ZepTagColor.Red, true) };
            var renamed = new[] { Tag("red", "Scarlet", ZepTagColor.Red, true) };
            var assignments = new Dictionary<string, IReadOnlyList<string>>();
            for (var i = 0; i < assignmentCount; i++)
            {
                assignments["guid-" + i] = new[] { "red" };
            }

            gateway.SetTagStateDirect(original, assignments);
            var undoStack = new List<ProjectCommandRecord>();
            var history = new ProjectCommandHistory(
                undoStack,
                new List<ProjectCommandRecord>(),
                () => { });
            var service = new ProjectFileCommandService(gateway, history);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);

            var change = service.ChangeTagDefinitionsAfterPreparation(
                original,
                renamed,
                removeUndefinedAssignments: false);

            Assert.That(change.Succeeded, Is.True, change.ErrorMessage);
            Assert.That(change.TagAssignmentSnapshotChanged, Is.False);
            Assert.That(undoStack, Has.Count.EqualTo(1));
            Assert.That(undoStack[0].AssignmentsAreDiff, Is.True);
            Assert.That(
                undoStack[0].DesiredGuidTagAssignments, Is.Empty,
                "a rename changes no assignment, so the record must carry none");
            Assert.That(undoStack[0].OriginalGuidTagAssignments, Is.Empty);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(renamed));

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(undo.TagAssignmentSnapshotChanged, Is.False);
            Assert.That(gateway.CaptureTagState().Definitions, Is.EqualTo(original));
            Assert.That(
                gateway.CaptureTagState().AssignmentsByGuid, Has.Count.EqualTo(assignmentCount),
                "reconstituting the state from the live store must not drop assignments");
            Assert.That(
                gateway.CaptureTagState().AssignmentsByGuid["guid-4999"],
                Is.EqualTo(new[] { "red" }));
        }

        // The cascade is the case where the record DOES have to carry entries:
        // exactly the GUIDs the deleted definition was on, and nothing else.
        [Test]
        public void ChangeTagDefinitions_DeleteRecordsOnlyTheCascadedAssignments()
        {
            var gateway = new FakeGateway();
            var original = new[]
            {
                Tag("custom", "Custom", ZepTagColor.Purple, false),
                Tag("red", "Red", ZepTagColor.Red, true)
            };
            gateway.SetTagStateDirect(
                original,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["guid-both"] = new[] { "custom", "red" },
                    ["guid-only-custom"] = new[] { "custom" },
                    ["guid-untouched"] = new[] { "red" }
                });
            var undoStack = new List<ProjectCommandRecord>();
            var history = new ProjectCommandHistory(
                undoStack,
                new List<ProjectCommandRecord>(),
                () => { });
            var service = new ProjectFileCommandService(gateway, history);
            Assert.That(service.PrepareRecordedMutation().Succeeded, Is.True);

            var change = service.ChangeTagDefinitionsAfterPreparation(
                original,
                new[] { original[1] },
                removeUndefinedAssignments: false,
                assignmentTagIdToRemove: "custom");

            Assert.That(change.Succeeded, Is.True, change.ErrorMessage);
            Assert.That(
                undoStack[0].OriginalGuidTagAssignments.Select(entry => entry.AssetGuid),
                Is.EquivalentTo(new[] { "guid-both", "guid-only-custom" }),
                "only the GUIDs the cascade touched belong in the record");
            Assert.That(
                undoStack[0].DesiredGuidTagAssignments.Select(entry => entry.AssetGuid),
                Is.EqualTo(new[] { "guid-both" }),
                "the GUID left with no tags is carried by the original side alone");

            var undo = service.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            var restored = gateway.CaptureTagState().AssignmentsByGuid;
            Assert.That(restored["guid-both"], Is.EqualTo(new[] { "custom", "red" }));
            Assert.That(
                restored["guid-only-custom"], Is.EqualTo(new[] { "custom" }),
                "an entry the cascade emptied must come back on undo");
            Assert.That(restored["guid-untouched"], Is.EqualTo(new[] { "red" }));
        }

        private sealed class FakeGateway : IProjectAssetGateway, IProjectTagDefinitionGateway
        {
            private readonly HashSet<string> _folders = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _files = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _guidByPath = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string[]> _tagIdsByPath = new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase);
            private ProjectTagStateSnapshot _tagState = new ProjectTagStateSnapshot(
                System.Array.Empty<ZepTag>(),
                new Dictionary<string, IReadOnlyList<string>>());
            private int _nextGuid;

            public int CreateFolderCallCount { get; private set; }
            public int CopyCallCount { get; private set; }
            public int RestoreUndoPayloadCallCount { get; private set; }
            public bool ReturnEmptyGuid { get; set; }

            public ProjectTagStateSnapshot CaptureTagState()
            {
                return new ProjectTagStateSnapshot(
                    _tagState.Definitions,
                    _tagState.AssignmentsByGuid);
            }

            public ProjectFileOperationResult CompareAndSetTagState(
                ProjectTagStateSnapshot expected,
                ProjectTagStateSnapshot desired)
            {
                if (!ProjectTagStateSnapshot.ContentEquals(_tagState, expected))
                {
                    return ProjectFileOperationResult.Failure("Tag state conflict.");
                }

                _tagState = new ProjectTagStateSnapshot(
                    desired.Definitions,
                    desired.AssignmentsByGuid);
                return ProjectFileOperationResult.Success(
                    string.Empty,
                    string.Empty,
                    tagDefinitionsChanged: true);
            }

            public void SetTagStateDirect(
                IReadOnlyList<ZepTag> definitions,
                IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
            {
                _tagState = new ProjectTagStateSnapshot(definitions, assignments);
            }

            public void AddFolder(string path)
            {
                var normalized = AssetPathFormat.Normalize(path);
                _folders.Add(normalized);
                AddIdentity(normalized);
            }

            // An outside tool (git revert) putting a deleted file back: same
            // path, same identity as the copy still sitting in UndoStorage. AddFile
            // would mint a fresh identity, which models a DIFFERENT item.
            public void RestoreExternally(string path, string assetGuid)
            {
                var normalized = AssetPathFormat.Normalize(path);
                _files.Add(normalized);
                _guidByPath[normalized] = assetGuid;
            }

            public void AddFile(string path)
            {
                _files.Add(AssetPathFormat.Normalize(path));
                AddIdentity(AssetPathFormat.Normalize(path));
                var parent = AssetPathFormat.GetParentFolder(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    _folders.Add(parent);
                    AddIdentity(parent);
                }
            }

            public bool Exists(AssetPath assetPath)
            {
                return _folders.Contains(assetPath) || _files.Contains(assetPath);
            }

            public bool IsFolder(AssetPath assetPath)
            {
                return _folders.Contains(assetPath);
            }

            public bool CanWrite(AssetPath assetPath)
            {
                return AssetPathClassification.IsWritablePath(assetPath);
            }

            public bool CanTag(AssetPath assetPath)
            {
                return Exists(assetPath);
            }

            public int TagVersion { get; private set; }

            public bool EnsureFolder(AssetPath folderPath)
            {
                if (folderPath.IsEmpty)
                {
                    return false;
                }

                _folders.Add(folderPath);
                AddIdentity(folderPath);
                return true;
            }

            public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName)
            {
                CreateFolderCallCount++;
                var path = AssetPathFormat.Combine(parentFolderPath, folderName);
                AddFolder(path);
                return ProjectFileOperationResult.Success(path, parentFolderPath);
            }

            public ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath)
            {
                string source = sourcePath;
                string destination = destinationPath;
                destination = allowOverwriteWithUniquePath ? GetUniquePath(destination) : destination;

                // The real gateway refuses to move onto something that is
                // already there. This fake used to absorb it — the sets are
                // keyed by path, so the occupant was simply overwritten and
                // every caller saw a success — which is why a whole class of
                // move and rename collisions could not be caught by a test.
                if (!string.Equals(source, destination, System.StringComparison.OrdinalIgnoreCase)
                    && (_files.Contains(destination) || _folders.Contains(destination)))
                {
                    return ProjectFileOperationResult.Failure(
                        "Move Asset failed: '" + destination + "' already exists.",
                        source);
                }

                if (_files.Remove(source))
                {
                    _files.Add(destination);
                    MoveIdentity(source, destination);
                    EnsureFolder(AssetPath.From(AssetPathFormat.GetParentFolder(destination)));
                    return ProjectFileOperationResult.Success(destination, source);
                }

                if (_folders.Remove(source))
                {
                    _folders.Add(destination);
                    MoveIdentity(source, destination);
                    // The real gateway relocates the whole subtree; a fake that
                    // renamed only the folder entry left its children sitting at
                    // paths that no longer exist and gave broken subtree handling
                    // a green light.
                    MoveDescendants(_files, source, destination);
                    MoveDescendants(_folders, source, destination);
                    return ProjectFileOperationResult.Success(destination, source);
                }

                return ProjectFileOperationResult.Failure("Missing source.", source);
            }

            public ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath)
            {
                CopyCallCount++;
                string source = sourcePath;
                string destination = destinationPath;
                destination = allowOverwriteWithUniquePath ? GetUniquePath(destination) : destination;

                if (_files.Contains(source))
                {
                    _files.Add(destination);
                    AddIdentity(destination);
                    return ProjectFileOperationResult.Success(destination, source);
                }

                if (_folders.Contains(source))
                {
                    _folders.Add(destination);
                    AddIdentity(destination);
                    return ProjectFileOperationResult.Success(destination, source);
                }

                return ProjectFileOperationResult.Failure("Missing source.", source);
            }

            public ProjectFileOperationResult Delete(AssetPath assetPath)
            {
                string normalized = assetPath;
                _files.Remove(normalized);
                _folders.Remove(normalized);
                _guidByPath.Remove(normalized);
                _tagIdsByPath.Remove(normalized);
                return ProjectFileOperationResult.Success(string.Empty, normalized);
            }

            public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default)
            {
                string normalized = assetPath;
                string destination = payloadPath.IsEmpty
                    ? "Library/Zep/UndoPayloads/" + normalized
                    : (string)payloadPath;

                if (_files.Remove(normalized))
                {
                    _files.Add(destination);
                    MoveIdentity(normalized, destination);
                    return ProjectFileOperationResult.Success(destination, normalized);
                }

                if (_folders.Remove(normalized))
                {
                    _folders.Add(destination);
                    MoveIdentity(normalized, destination);
                    return ProjectFileOperationResult.Success(destination, normalized);
                }

                return ProjectFileOperationResult.Failure("Missing source.", normalized);
            }

            public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath)
            {
                RestoreUndoPayloadCallCount++;
                string storagePath = payloadPath;
                string normalizedDestination = destinationPath;
                if (Exists(destinationPath))
                {
                    if (_guidByPath.TryGetValue(storagePath, out var storedGuid)
                        && _guidByPath.TryGetValue(normalizedDestination, out var destinationGuid)
                        && storedGuid == destinationGuid)
                    {
                        DeleteUndoPayload(payloadPath);
                        return ProjectFileOperationResult.Success(destinationPath, payloadPath);
                    }
                    return ProjectFileOperationResult.Failure("Undo restore conflict: destination is occupied.", destinationPath, payloadPath);
                }

                if (_files.Remove(storagePath))
                {
                    _files.Add(normalizedDestination);
                    MoveIdentity(storagePath, normalizedDestination);
                    EnsureFolder(AssetPath.From(AssetPathFormat.GetParentFolder(normalizedDestination)));
                    return ProjectFileOperationResult.Success(normalizedDestination, storagePath);
                }

                if (_folders.Remove(storagePath))
                {
                    _folders.Add(normalizedDestination);
                    MoveIdentity(storagePath, normalizedDestination);
                    return ProjectFileOperationResult.Success(normalizedDestination, storagePath);
                }

                return ProjectFileOperationResult.Failure("Missing saved item.", normalizedDestination, storagePath);
            }

            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath)
            {
                return Delete(payloadPath);
            }

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) =>
                GetTagIds(assetPath);

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath)
            {
                return _tagIdsByPath.TryGetValue(assetPath, out var tagIds)
                    ? tagIds
                    : System.Array.Empty<string>();
            }

            public ProjectFileOperationResult SetTagIds(IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath)
            {
                var firstPath = default(AssetPath);
                foreach (var entry in tagIdsByAssetPath)
                {
                    if (firstPath.IsEmpty)
                    {
                        firstPath = entry.Key;
                    }

                    _tagIdsByPath[entry.Key] = entry.Value == null
                        ? System.Array.Empty<string>()
                        : entry.Value.ToArray();
                }

                TagVersion++;
                return ProjectFileOperationResult.Success(firstPath, firstPath);
            }

            public int GetGuidCallCount { get; private set; }

            public string GetGuid(AssetPath assetPath)
            {
                GetGuidCallCount++;
                return !ReturnEmptyGuid && _guidByPath.TryGetValue(assetPath, out var guid)
                    ? guid
                    : string.Empty;
            }

            public string GetPathForGuid(string assetGuid)
            {
                if (ReturnEmptyGuid || string.IsNullOrWhiteSpace(assetGuid))
                {
                    return string.Empty;
                }

                foreach (var entry in _guidByPath)
                {
                    if (string.Equals(entry.Value, assetGuid, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Key;
                    }
                }

                return string.Empty;
            }

            public string ResolveAbsolutePath(AssetPath assetPath)
            {
                return assetPath;
            }

            public void RemoveDirect(string path)
            {
                _files.Remove(AssetPathFormat.Normalize(path));
                _folders.Remove(AssetPathFormat.Normalize(path));
                _guidByPath.Remove(AssetPathFormat.Normalize(path));
                _tagIdsByPath.Remove(AssetPathFormat.Normalize(path));
            }

            // Relocates an asset the way an edit made OUTSIDE the panel would:
            // the path changes, the GUID and the tag assignments follow it, and
            // no command is recorded.
            public void MoveDirectPreservingIdentity(string fromPath, string toPath)
            {
                var from = AssetPathFormat.Normalize(fromPath);
                var to = AssetPathFormat.Normalize(toPath);
                var wasFile = _files.Remove(from);
                if (wasFile)
                {
                    _files.Add(to);
                }
                else if (_folders.Remove(from))
                {
                    _folders.Add(to);
                }

                if (_guidByPath.TryGetValue(from, out var guid))
                {
                    _guidByPath.Remove(from);
                    _guidByPath[to] = guid;
                }

                if (_tagIdsByPath.TryGetValue(from, out var tagIds))
                {
                    _tagIdsByPath.Remove(from);
                    _tagIdsByPath[to] = tagIds;
                }

                var parent = AssetPathFormat.GetParentFolder(to);
                if (!string.IsNullOrEmpty(parent))
                {
                    _folders.Add(parent);
                    AddIdentity(parent);
                }
            }

            public bool Open(AssetPath assetPath)
            {
                return Exists(assetPath);
            }

            public void Reveal(AssetPath assetPath)
            {
            }

            public void BeginBatch()
            {
            }

            public void EndBatch()
            {
            }

            // A second copy of the real gateway's "Name 1", "Name 2" rule. It
            // is duplicated on purpose — the service under test only ever sees
            // the path it gets back — but it is a copy, so a change to the real
            // naming rule will not show up here.
            private string GetUniquePath(string path)
            {
                if (!Exists(AssetPath.From(path)))
                {
                    return path;
                }

                var parent = AssetPathFormat.GetParentFolder(path);
                var name = AssetPathFormat.GetNameWithoutExtension(path);
                var extension = AssetPathFormat.GetExtension(path);
                var index = 1;

                while (true)
                {
                    var candidate = AssetPathFormat.Combine(parent, name + " " + index + extension);
                    if (!Exists(AssetPath.From(candidate)))
                    {
                        return candidate;
                    }

                    index++;
                }
            }

            private void AddIdentity(string path)
            {
                if (!_guidByPath.ContainsKey(path))
                {
                    _guidByPath[path] = "guid-" + (++_nextGuid);
                }
            }

            private void MoveDescendants(HashSet<string> paths, string source, string destination)
            {
                var prefix = source + "/";
                var moved = new List<string>();
                foreach (var path in paths)
                {
                    if (path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                    {
                        moved.Add(path);
                    }
                }

                for (var i = 0; i < moved.Count; i++)
                {
                    var relocated = destination + moved[i].Substring(source.Length);
                    paths.Remove(moved[i]);
                    paths.Add(relocated);
                    MoveIdentity(moved[i], relocated);
                }
            }

            private void MoveIdentity(string sourcePath, string destinationPath)
            {
                if (_guidByPath.TryGetValue(sourcePath, out var guid))
                {
                    _guidByPath.Remove(sourcePath);
                    _guidByPath[destinationPath] = guid;
                }
                else
                {
                    AddIdentity(destinationPath);
                }
            }
        }
    }
}
