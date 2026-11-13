using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // Behavior of the drag-over drop-target rules: a folder highlights during
    // a drag only when dropping there would actually move something. Mirrors
    // the no-op set FilterMovablePaths rejects on the real drop.
    public sealed class AssetDropOperationsTests
    {
        private static bool IsMeaningful(string folderPath, params string[] draggedPaths)
        {
            return FolderMoveDrop.IsMeaningfulMoveTarget(folderPath, draggedPaths);
        }

        [Test]
        public void IsMeaningfulMoveTarget_UnrelatedFolder_IsMeaningful()
        {
            Assert.That(IsMeaningful("Assets/B", "Assets/A/Rock.mat"), Is.True);
        }

        [Test]
        public void IsMeaningfulMoveTarget_ItemsOwnParent_IsNoOp()
        {
            Assert.That(IsMeaningful("Assets/A", "Assets/A/Rock.mat"), Is.False);
        }

        [Test]
        public void IsMeaningfulMoveTarget_FolderOntoItself_IsNoOp()
        {
            Assert.That(IsMeaningful("Assets/A", "Assets/A"), Is.False);
        }

        [TestCase("Assets/A/B")]
        [TestCase("Assets/A/B/C")]
        public void IsMeaningfulMoveTarget_FolderIntoOwnSubtree_IsNoOp(string target)
        {
            Assert.That(IsMeaningful(target, "Assets/A"), Is.False);
        }

        [Test]
        public void IsMeaningfulMoveTarget_SiblingWithSharedNamePrefix_IsMeaningful()
        {
            // "Assets/AB" merely starts with "Assets/A" — it is NOT inside it;
            // the subtree rule must respect the path-segment boundary.
            Assert.That(IsMeaningful("Assets/AB", "Assets/A"), Is.True);
        }

        [Test]
        public void IsMeaningfulMoveTarget_MixedSetWithOneMovablePath_IsMeaningful()
        {
            Assert.That(
                IsMeaningful("Assets/A", "Assets/A/Rock.mat", "Assets/B/Tree.prefab"),
                Is.True);
        }

        [Test]
        public void IsMeaningfulMoveTarget_NoOpRules_IgnoreCase()
        {
            Assert.That(IsMeaningful("Assets/A", "ASSETS/a/Rock.mat"), Is.False);
            Assert.That(IsMeaningful("assets/a", "Assets/A"), Is.False);
        }

        [Test]
        public void IsMeaningfulMoveTarget_EmptyOrNullInputs_AreNoOps()
        {
            Assert.That(IsMeaningful(null, "Assets/A/Rock.mat"), Is.False);
            Assert.That(IsMeaningful(string.Empty, "Assets/A/Rock.mat"), Is.False);
            Assert.That(FolderMoveDrop.IsMeaningfulMoveTarget("Assets/B", null), Is.False);
            Assert.That(IsMeaningful("Assets/B"), Is.False);
            Assert.That(IsMeaningful("Assets/B", string.Empty, null), Is.False);
        }
    }

    // What a drop does when the destination folder already holds an item under
    // the dropped item's name. It used to look like a no-op: nothing probed the
    // destination, AssetDatabase.MoveAsset failed, and the raw error showed for
    // 1.9s in a status bar that then swallowed every identical repeat.
    public sealed class MoveConflictDropTests
    {
        private const string Destination = "Assets";
        private const string Occupant = "Assets/Foo.mat";
        private const string Source = "Assets/Art/Foo.mat";
        private const string Companion = "Assets/Art/Bar.mat";
        private const string Unindexed = "Assets/Art/Ghost.mat";

        private readonly List<string> _promptTitles = new List<string>();
        private readonly List<string> _promptMessages = new List<string>();
        private readonly List<string> _errors = new List<string>();
        private ZepUiControllerRig _rig;
        private ProjectMoveConflictResolution _answer;

        [SetUp]
        public void SetUp()
        {
            _promptTitles.Clear();
            _promptMessages.Clear();
            _errors.Clear();
            _answer = ProjectMoveConflictResolution.Skip;
            _rig = ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("occupant-guid", Occupant, "Foo", "Material"),
                    new AssetRecord("source-guid", Source, "Foo", "Material"),
                    new AssetRecord("companion-guid", Companion, "Bar", "Material")
                },
                new[] { "Assets", "Assets/Art" });
        }

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
        }

        [Test]
        public void ACollidingDrop_AsksOnce_NamingTheItemAndTheFolder()
        {
            Drop(Source);

            Assert.That(_promptTitles, Is.EqualTo(new[] { "Move Conflict" }));
            Assert.That(
                _promptMessages[0],
                Does.StartWith("'Foo.mat' already exists in Assets."),
                "the dialog has to name what is in the way and where");
            Assert.That(
                _promptMessages[0],
                Does.Contain("Replace moves the existing item aside; you can undo it."),
                "the destructive-looking choice has to say it is recoverable");
        }

        [Test]
        public void Cancel_MovesNothingAndStillSaysWhatHappened()
        {
            _answer = ProjectMoveConflictResolution.Skip;

            Drop(Source);

            Assert.That(Exists(Source), Is.True, "the declined item stays where it was");
            Assert.That(Exists(Occupant), Is.True);
            Assert.That(_rig.Gateway.GetGuid(AssetPath.From(Occupant)), Is.EqualTo("occupant-guid"));
            Assert.That(
                _rig.State.StatusMessage,
                Is.EqualTo("Skipped 'Foo.mat' — already exists in Assets"),
                "a lone Cancel must not be the silence this whole change is about");
        }

        [Test]
        public void KeepBoth_LandsUnderAFreeNameAndLeavesTheOccupantAlone()
        {
            _answer = ProjectMoveConflictResolution.KeepBoth;

            Drop(Source);

            Assert.That(Exists("Assets/Foo 1.mat"), Is.True);
            Assert.That(Exists(Source), Is.False);
            Assert.That(
                _rig.Gateway.GetGuid(AssetPath.From(Occupant)),
                Is.EqualTo("occupant-guid"),
                "the item that was already there must be untouched");
            Assert.That(_rig.Gateway.WasStoredForUndo(Occupant), Is.False);
        }

        [Test]
        public void Replace_TakesTheDestinationAndStoresTheOccupantForUndo()
        {
            _answer = ProjectMoveConflictResolution.Replace;

            Drop(Source);

            Assert.That(
                _rig.Gateway.WasStoredForUndo(Occupant),
                Is.True,
                "replacing must displace, never destroy");
            Assert.That(Exists(Source), Is.False);
            Assert.That(
                _rig.Gateway.GetGuid(AssetPath.From(Occupant)),
                Is.EqualTo("source-guid"),
                "the dropped item is the one at the destination now");
        }

        [Test]
        public void AMixedDrop_AsksOnce_AndTheItemsWithNothingInTheirWayStillMove()
        {
            _answer = ProjectMoveConflictResolution.Skip;

            Drop(Source, Companion);

            Assert.That(_promptTitles.Count, Is.EqualTo(1), "one drop is one decision");
            Assert.That(Exists("Assets/Bar.mat"), Is.True);
            Assert.That(Exists(Source), Is.True);
            Assert.That(_rig.State.StatusMessage, Is.EqualTo("Moved 1 of 2 — 1 skipped"));
        }

        [Test]
        public void AFailedMove_AlsoRaisesTheToast()
        {
            // Not in the index, so its identity cannot be validated and the move
            // fails the way a real broken move does. The status bar drops a
            // message identical to the one it is showing, so on the second
            // attempt the toast is the only thing left that reacts.
            Drop(Unindexed);

            Assert.That(_promptTitles, Is.Empty, "nothing is in this item's way");
            Assert.That(
                _errors,
                Is.EqualTo(new[] { "The original item can no longer be identified safely." }));
        }

        [Test]
        public void ADestinationHoldingTheSameName_StillHighlightsAsADropTarget()
        {
            Assume.That(
                _rig.Controller.FindMoveCollisions(
                    new[] { AssetPath.From(Source) }, AssetPath.From(Destination)).Count,
                Is.EqualTo(1),
                "the fixture's destination must really hold the dropped name");

            // The drop is meaningful again precisely because it now offers a
            // choice; refusing the hover would hide that choice behind a
            // rejected cursor.
            Assert.That(
                FolderMoveDrop.IsMeaningfulMoveTarget(Destination, new[] { Source }),
                Is.True);
        }

        [Test]
        public void ManyCollisions_ListTheFirstFiveNamesAndCountTheRest()
        {
            var collisions = new List<AssetPath>();
            for (var i = 1; i <= 7; i++)
            {
                collisions.Add(AssetPath.From("Assets/Art/Item" + i + ".mat"));
            }

            var message = FolderMoveDrop.BuildMoveConflictMessage(collisions, "Assets/B");

            Assert.That(message, Does.StartWith("7 items already exist in Assets/B:"));
            Assert.That(message, Does.Contain("Item5.mat"));
            Assert.That(message, Does.Not.Contain("Item6.mat"), "the list is bounded");
            Assert.That(message, Does.Contain("…and 2 more"));
            Assert.That(
                message,
                Does.EndWith("Replace moves the existing items aside; you can undo it."));
        }

        private void Drop(params string[] paths)
        {
            var dropOps = new AssetDropOperations(
                _rig.Controller,
                _rig.State,
                null,
                null,
                showError: message => _errors.Add(message),
                askMoveConflict: (title, message) =>
                {
                    _promptTitles.Add(title);
                    _promptMessages.Add(message);
                    return _answer;
                });

            dropOps.HandleSidebarFolderDrop(_rig.State.PrimaryPane, Destination, paths);
        }

        private bool Exists(string assetPath)
        {
            return _rig.Gateway.Exists(AssetPath.From(assetPath));
        }
    }

    // What the sidebar tree tells the cursor while a project-asset drag hovers a
    // folder row. The highlight is the single decision source: a row that would
    // move nothing must not promise a move either.
    public sealed class FolderTreeDropTargetHoverTests
    {
        [TearDown]
        public void TearDown()
        {
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.visualMode = DragAndDropVisualMode.None;
        }

        [Test]
        public void NoOpRow_RefusesTheDragAndShowsNoHighlight()
        {
            var tree = CreateFolderTree(
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("A", "Assets/A", 1, false),
                new ZepFolderEntry("B", "Assets/B", 1, false));
            var ownParentRow = AttachRow(tree, 1);
            var unrelatedRow = AttachRow(tree, 2);
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat" };
            Assume.That(
                DragAndDrop.paths,
                Is.EqualTo(new[] { "Assets/A/Rock.mat" }),
                "the drag payload must be readable for the hover rules to run");

            Assert.That(
                tree.RefreshDropTargetFromHover(unrelatedRow),
                Is.EqualTo(DragAndDropVisualMode.Move));
            Assert.That(
                unrelatedRow.ClassListContains(ZepUiClasses.DropTarget),
                Is.True);

            // Dropping an item back on the folder it already lives in moves
            // nothing; the cursor used to promise the move anyway.
            Assert.That(
                tree.RefreshDropTargetFromHover(ownParentRow),
                Is.EqualTo(DragAndDropVisualMode.Rejected));
            Assert.That(
                ownParentRow.ClassListContains(ZepUiClasses.DropTarget),
                Is.False);
            Assert.That(
                unrelatedRow.ClassListContains(ZepUiClasses.DropTarget),
                Is.False,
                "moving to a refused row must clear the previous row's highlight");
        }

        [Test]
        public void HoveringTheSameRowTwice_EvaluatesTheMoveVerdictOnce()
        {
            var tree = CreateFolderTree(
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("A", "Assets/A", 1, false),
                new ZepFolderEntry("B", "Assets/B", 1, false));
            var ownParentRow = AttachRow(tree, 1);
            var unrelatedRow = AttachRow(tree, 2);
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat" };
            Assume.That(
                DragAndDrop.paths,
                Is.EqualTo(new[] { "Assets/A/Rock.mat" }),
                "the drag payload must be readable for the hover rules to run");

            // A drag hovering one row sends a DragUpdated per mouse frame, plus
            // one re-pick per auto-scroll tick, all with the same payload.
            var evaluations = FolderTreeWidget.MoveVerdictEvaluationsForTests;
            Assert.That(
                tree.RefreshDropTargetFromHover(unrelatedRow),
                Is.EqualTo(DragAndDropVisualMode.Move));
            Assert.That(
                tree.RefreshDropTargetFromHover(unrelatedRow),
                Is.EqualTo(DragAndDropVisualMode.Move));
            Assert.That(
                tree.RefreshDropTargetFromHover(unrelatedRow),
                Is.EqualTo(DragAndDropVisualMode.Move));
            Assert.That(
                FolderTreeWidget.MoveVerdictEvaluationsForTests - evaluations,
                Is.EqualTo(1),
                "a stationary hover must not re-walk the payload");

            // Moving to another row is a different question, and the memo holds
            // one answer, so coming back re-evaluates: A -> B -> A is 3.
            evaluations = FolderTreeWidget.MoveVerdictEvaluationsForTests;
            Assert.That(
                tree.RefreshDropTargetFromHover(ownParentRow),
                Is.EqualTo(DragAndDropVisualMode.Rejected));
            Assert.That(
                tree.RefreshDropTargetFromHover(unrelatedRow),
                Is.EqualTo(DragAndDropVisualMode.Move));
            Assert.That(
                FolderTreeWidget.MoveVerdictEvaluationsForTests - evaluations,
                Is.EqualTo(2),
                "a row change must re-evaluate, and must not serve the other row's verdict");
        }

        [Test]
        public void PayloadChangingMidDrag_DropsTheCachedVerdict()
        {
            var tree = CreateFolderTree(
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("A", "Assets/A", 1, false));
            var row = AttachRow(tree, 1);
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/B/Rock.mat" };
            Assert.That(
                tree.RefreshDropTargetFromHover(row),
                Is.EqualTo(DragAndDropVisualMode.Move));

            // Same row, different payload: the answer must follow the payload.
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat" };
            Assert.That(
                tree.RefreshDropTargetFromHover(row),
                Is.EqualTo(DragAndDropVisualMode.Rejected),
                "a row that was a valid target for the previous payload must be re-judged");
        }

        [Test]
        public void RowOutsideThisTree_IsStillNotADroppableHover()
        {
            var tree = CreateFolderTree(
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("A", "Assets/A", 1, false));
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat" };
            Assume.That(
                DragAndDrop.paths,
                Is.EqualTo(new[] { "Assets/A/Rock.mat" }),
                "the drag payload must be readable for the hover rules to run");

            Assert.That(
                tree.RefreshDropTargetFromHover(new VisualElement()),
                Is.EqualTo(DragAndDropVisualMode.None));
        }

        private static FolderTreeWidget CreateFolderTree(params ZepFolderEntry[] folders)
        {
            SessionState.EraseString("Zep.FolderTreeExpanded.Assets");
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(folders));
            tree.Apply("Assets", "Assets");
            return tree;
        }

        // The lazy children query the sidebar runs in production, served from a
        // flat fixture array: the children of a path are the entries whose
        // parent is that path, and the empty key holds the root itself.
        private static Func<string, IReadOnlyList<ZepFolderEntry>> ChildrenQuery(
            IReadOnlyList<ZepFolderEntry> folders)
        {
            var byParent = new Dictionary<string, List<ZepFolderEntry>>(
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                var parent = AssetPathFormat.GetParentFolderPath(folder.Path);
                if (!byParent.TryGetValue(parent, out var children))
                {
                    children = new List<ZepFolderEntry>();
                    byParent[parent] = children;
                }

                children.Add(folder);
            }

            return parent => byParent.TryGetValue(parent ?? string.Empty, out var children)
                ? children
                : (IReadOnlyList<ZepFolderEntry>)Array.Empty<ZepFolderEntry>();
        }

        // A realized row, parented into the tree so the hover resolution's
        // "belongs to THIS tree" gate sees it. Cheaper than a windowed layout
        // pass and binds the same element makeItem/bindItem produce live.
        private static VisualElement AttachRow(FolderTreeWidget tree, int visibleIndex)
        {
            var list = tree.Q<ListView>();
            var row = list.makeItem();
            list.bindItem(row, visibleIndex);
            tree.hierarchy.Add(row);
            return row;
        }
    }

    // The asset pane asks the same question the sidebar does, over its own rows.
    // Its answer also costs an AssetDatabase.IsValidFolder syscall, so it is
    // memoized on the same key.
    public sealed class AssetsBrowserDropTargetHoverTests
    {
        [TearDown]
        public void TearDown()
        {
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.visualMode = DragAndDropVisualMode.None;
        }

        private static AssetsBrowserFeature CreateFeature()
        {
            return new AssetsBrowserFeature(
                new UnityZepIconRepository(),
                AssetViewMode.List,
                AssetsAreaConstants.DefaultAssetGridItemSize);
        }

        [Test]
        public void HoveringTheSameFolderTwice_ResolvesTheDropTargetOnce()
        {
            var feature = CreateFeature();
            var payload = new[] { "Assets/SomeFolder/Rock.mat" };

            var evaluations = AssetsBrowserFeature.DropTargetEvaluationsForTests;
            Assert.That(
                feature.ResolveFolderDropTarget("Assets", payload),
                Is.EqualTo("Assets"));
            Assert.That(
                feature.ResolveFolderDropTarget("Assets", payload),
                Is.EqualTo("Assets"));
            Assert.That(
                feature.ResolveFolderDropTarget("Assets", payload),
                Is.EqualTo("Assets"));
            Assert.That(
                AssetsBrowserFeature.DropTargetEvaluationsForTests - evaluations,
                Is.EqualTo(1),
                "a stationary hover must not re-run IsValidFolder or the move verdict");
        }

        [Test]
        public void DifferentPayloadOverTheSameFolder_IsJudgedAgain()
        {
            var feature = CreateFeature();

            Assert.That(
                feature.ResolveFolderDropTarget("Assets", new[] { "Assets/A/Rock.mat" }),
                Is.EqualTo("Assets"));
            Assert.That(
                feature.ResolveFolderDropTarget("Assets", new[] { "Assets/Rock.mat" }),
                Is.Null,
                "dropping an item back into the folder it already lives in is a no-op");
        }

        [Test]
        public void NonFolderAndEmptyHovers_TargetTheBrowsedFolder()
        {
            var feature = CreateFeature();

            Assert.That(
                feature.ResolveFolderDropTarget(null, new[] { "Assets/A/Rock.mat" }),
                Is.Null);
            Assert.That(
                feature.ResolveFolderDropTarget(
                    "Assets/DoesNotExist_Zep", new[] { "Assets/A/Rock.mat" }),
                Is.Null);
        }
    }

    // The payload read is shared, not copied: the hover handlers run it on every
    // drag frame and only read it, while the drop handlers keep what they get.
    public sealed class DragPayloadReadTests
    {
        [TearDown]
        public void TearDown()
        {
            DragAndDrop.paths = Array.Empty<string>();
        }

        [Test]
        public void RepeatedReadsOfOneDrag_ShareTheSameList()
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat", "Assets/B" };

            var first = EditorDragAndDropPaths.ReadProjectAssetPaths();
            Assume.That(first.Count, Is.EqualTo(2), "the payload must classify as project assets");

            Assert.That(
                EditorDragAndDropPaths.ReadProjectAssetPaths(),
                Is.SameAs(first),
                "hovering must not allocate a copy per frame");
        }

        [Test]
        public void TheDropSitesCopy_SoTheyCanKeepThePayload()
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat" };
            var shared = EditorDragAndDropPaths.ReadProjectAssetPaths();

            var copy = EditorDragAndDropPaths.CopyProjectAssetPaths();

            Assert.That(copy, Is.Not.SameAs(shared));
            Assert.That(copy, Is.EqualTo(new[] { "Assets/A/Rock.mat" }));
        }

        [Test]
        public void ChangingTheDraggedPaths_ChangesThePayloadGeneration()
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { "Assets/A/Rock.mat" };
            EditorDragAndDropPaths.ReadProjectAssetPaths();
            var generation = EditorDragAndDropPaths.PayloadGeneration;

            EditorDragAndDropPaths.ReadProjectAssetPaths();
            Assert.That(
                EditorDragAndDropPaths.PayloadGeneration,
                Is.EqualTo(generation),
                "an unchanged payload must keep its generation, or every memo keyed on it is dead");

            DragAndDrop.paths = new[] { "Assets/B/Tree.prefab" };
            EditorDragAndDropPaths.ReadProjectAssetPaths();
            Assert.That(EditorDragAndDropPaths.PayloadGeneration, Is.Not.EqualTo(generation));
        }
    }

    // Where the OnGUI external-drag fallback writes a dropped file. It is the
    // only handler macOS Finder drags reach (DragAndDrop.paths stays empty until
    // perform), so its destination rule decides what a Finder drop does.
    public sealed class ExternalDropDestinationTests
    {

        [Test]
        public void FolderUnderTheCursor_WinsOverTheCurrentFolder()
        {
            Assert.That(
                ZepWindow.ResolveExternalDropDestination("Assets", "Assets"),
                Is.EqualTo("Assets"));
        }

        [Test]
        public void NothingUnderTheCursor_FallsBackToTheCurrentFolder()
        {
            Assert.That(
                ZepWindow.ResolveExternalDropDestination(null, "Assets"),
                Is.EqualTo("Assets"));
            Assert.That(
                ZepWindow.ResolveExternalDropDestination(string.Empty, string.Empty),
                Is.EqualTo("Assets"));
        }

        [Test]
        public void MissingFolderUnderTheCursor_DoesNotDeclineTheDrop()
        {
            Assert.That(
                ZepWindow.ResolveExternalDropDestination("Assets/DoesNotExist_Zep", "Assets"),
                Is.EqualTo("Assets"));
        }

    }
    // A drop the panel accepted and then refused because something is read-only.
    // The hover verdicts advertise Move/Copy without ever asking the gateway, so
    // each of these gestures used to end in silence — no toast, no status line,
    // no log — reading as a broken panel rather than as a refusal.
    public sealed class ReadOnlyDropRefusalTests
    {
        private const string WritableDestination = "Assets";
        private const string ReadOnlyDestination = "Packages/com.example.locked/Runtime";
        private const string WritableSource = "Assets/Art/Bar.mat";
        private const string RootSource = "Assets/Foo.mat";
        private const string ReadOnlySource = "Packages/com.example.locked/Runtime/Locked.mat";

        private readonly List<string> _errors = new List<string>();
        private ZepUiControllerRig _rig;

        [SetUp]
        public void SetUp()
        {
            _errors.Clear();
            _rig = ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("root-guid", RootSource, "Foo", "Material"),
                    new AssetRecord("writable-guid", WritableSource, "Bar", "Material"),
                    new AssetRecord("locked-guid", ReadOnlySource, "Locked", "Material")
                },
                new[] { "Assets", "Assets/Art" });
        }

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
        }

        [Test]
        public void ReadOnlyDestination_IsNamedInsteadOfSwallowed()
        {
            Drop(ReadOnlyDestination, WritableSource);

            Assert.That(_errors.Count, Is.EqualTo(1), "an accepted drop that writes nothing has to say why");
            Assert.That(_errors[0], Does.Contain(ReadOnlyDestination), "the refusal names where it was refused");
            Assert.That(_errors[0], Does.Contain("read-only"));
            Assert.That(Exists(WritableSource), Is.True, "a refusal writes nothing");
        }

        [Test]
        public void ReadOnlyDestination_RepeatsTheMessageOnEveryAttempt()
        {
            Drop(ReadOnlyDestination, WritableSource);
            Drop(ReadOnlyDestination, WritableSource);

            Assert.That(
                _errors.Count,
                Is.EqualTo(2),
                "the toast is the channel that still reacts to an identical repeat");
        }

        [Test]
        public void EveryReadOnlySource_IsNamedInsteadOfSwallowed()
        {
            Drop(WritableDestination, ReadOnlySource);

            Assert.That(_errors.Count, Is.EqualTo(1));
            Assert.That(_errors[0], Does.Contain("Locked.mat"), "the refusal names the item so the user can act");
            Assert.That(_errors[0], Does.Contain("read-only"));
            Assert.That(Exists(ReadOnlySource), Is.True, "a refusal writes nothing");
        }

        [Test]
        public void MixedSet_MovesWhatItCanAndDoesNotAlsoRefuse()
        {
            Drop(WritableDestination, WritableSource, ReadOnlySource);

            Assert.That(Exists("Assets/Bar.mat"), Is.True, "the writable half still moves");
            Assert.That(
                _errors,
                Is.Empty,
                "the drop moved something, so its outcome is ReportMoveOutcome's to tell — not a second refusal");
        }

        [Test]
        public void NoOpDrop_StaysSilent()
        {
            Drop(WritableDestination, RootSource);

            Assert.That(
                _errors,
                Is.Empty,
                "an item dropped on its own parent was already refused by the hover; repeating it here would nag");
        }

        [Test]
        public void ExternalImportIntoAReadOnlyFolder_IsNamedInsteadOfSwallowed()
        {
            NewOps().HandleSidebarExternalImport(
                _rig.State.PrimaryPane,
                ReadOnlyDestination,
                new[] { "/tmp/zep-t07-drop/dropped.txt" });

            Assert.That(_errors.Count, Is.EqualTo(1));
            Assert.That(_errors[0], Does.Contain(ReadOnlyDestination));
            Assert.That(_errors[0], Does.Contain("read-only"));
        }

        [Test]
        public void DroppedUnityPackage_StillRoutesToTheInteractiveImportBeforeTheWriteCheck()
        {
            // Package import ignores the drop target, so it runs ahead of the
            // destination write check: a package dropped on a read-only folder
            // still imports and there is nothing to refuse. The interactive
            // importer is a Unity boundary and complains about the throwaway path
            // it is handed here; the ordering is what this pins.
            LogAssert.ignoreFailingMessages = true;

            NewOps().HandleSidebarExternalImport(
                _rig.State.PrimaryPane,
                ReadOnlyDestination,
                new[] { "/tmp/zep-t07-drop/dropped.unitypackage" });

            Assert.That(_errors, Is.Empty, "the package never asked to write into the drop target");
        }

        [Test]
        public void SceneObjectDropOnAReadOnlyFolder_IsNamedInsteadOfSwallowed()
        {
            // Pin the capability instead of the path: the destination has to be a
            // real folder to be honored at all, and every real folder in the test
            // project is writable by path shape.
            _rig.Gateway.CanWriteOverride = false;

            NewOps().HandleSceneObjectDrop(
                _rig.State.PrimaryPane,
                new UnityEngine.GameObject[0],
                WritableDestination);

            Assert.That(_errors.Count, Is.EqualTo(1));
            Assert.That(_errors[0], Does.Contain(WritableDestination));
            Assert.That(_errors[0], Does.Contain("read-only"));
        }

        private AssetDropOperations NewOps()
        {
            return new AssetDropOperations(
                _rig.Controller,
                _rig.State,
                null,
                null,
                showError: message => _errors.Add(message),
                askMoveConflict: (title, message) =>
                {
                    Assert.Fail("a refused drop must not reach the conflict dialog");
                    return ProjectMoveConflictResolution.Skip;
                });
        }

        private void Drop(string destination, params string[] paths)
        {
            NewOps().HandleSidebarFolderDrop(_rig.State.PrimaryPane, destination, paths);
        }

        private bool Exists(string assetPath)
        {
            return _rig.Gateway.Exists(AssetPath.From(assetPath));
        }
    }
}
