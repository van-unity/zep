using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepSplitDragAndDropTests
    {
        [TearDown]
        public void TearDown()
        {
            ZepSplitDragAndDrop.Clear();
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.visualMode = DragAndDropVisualMode.None;
        }

        [Test]
        public void TypedPayload_RoundTripsFolderAndTagWithoutAssetData()
        {
            var payloads = new[]
            {
                new ZepSplitDragPayload(
                    ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Art")),
                    "Art"),
                new ZepSplitDragPayload(
                    ZepPaneSource.FixedTag("blue"),
                    "Blue"),
                new ZepSplitDragPayload(
                    ZepPaneSource.FixedTag("green"),
                    "Green")
            };

            for (var i = 0; i < payloads.Length; i++)
            {
                ZepSplitDragAndDrop.Prepare(payloads[i]);

                Assert.That(
                    ZepSplitDragAndDrop.TryRead(out var read),
                    Is.True);
                Assert.That(read, Is.SameAs(payloads[i]));
                Assert.That(read.Source, Is.EqualTo(payloads[i].Source));
                Assert.That(read.DisplayName, Is.EqualTo(payloads[i].DisplayName));
                Assert.That(DragAndDrop.paths, Is.Empty);
                Assert.That(DragAndDrop.objectReferences, Is.Empty);
            }
        }

        [Test]
        public void Prepare_ReplacesAnAlreadyStagedAssetDragWithTheTypedPayloadAlone()
        {
            // Two halves of one guarantee: a split drag never looks like an
            // asset drag, and the payload survives the DragAndDrop reset that
            // preparing one begins with instead of being wiped by it.
            var stagedObject = ScriptableObject.CreateInstance<ScriptableObject>();
            try
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.paths = new[] { "Assets/Art/Hero.png" };
                DragAndDrop.objectReferences = new UnityEngine.Object[] { stagedObject };

                var payload = new ZepSplitDragPayload(
                    ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")),
                    "Art");
                ZepSplitDragAndDrop.Prepare(payload);

                Assert.That(DragAndDrop.paths, Is.Empty);
                Assert.That(DragAndDrop.objectReferences, Is.Empty);
                Assert.That(ZepSplitDragAndDrop.TryRead(out var read), Is.True);
                Assert.That(read, Is.SameAs(payload));

                ZepSplitDragAndDrop.Clear();
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False,
                    "a cleared session must leave nothing for the next drag to read");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stagedObject);
            }
        }

        [Test]
        public void FolderRows_SnapshotAssetsAndPackagesSourcesBeforeRebind()
        {
            var assetsTree = CreateFolderTree(
                "Assets",
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Art", "Assets/Art", 1, false));
            var assetsRow = BindRow(assetsTree, 1);
            var assetsPayload = assetsTree.SnapshotSplitDragPayload(
                assetsRow.Q<Label>());

            Assert.That(assetsPayload.Source.Kind, Is.EqualTo(ZepPaneSourceKind.Folder));
            Assert.That(
                assetsPayload.Source.RootFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(assetsPayload.DisplayName, Is.EqualTo("Art"));

            assetsTree.Q<ListView>().bindItem(assetsRow, 0);
            Assert.That(
                assetsPayload.Source.RootFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")),
                "The payload must not read a virtualized row after pointer-down.");

            var packagesTree = CreateFolderTree(
                "Packages",
                new ZepFolderEntry("Packages", "Packages", 0, true),
                new ZepFolderEntry(
                    "Tools",
                    "Packages/com.example.tools",
                    1,
                    true));
            var packagesRow = BindRow(packagesTree, 1);
            var packagesPayload = packagesTree.SnapshotSplitDragPayload(
                packagesRow.Q<Label>());

            Assert.That(
                packagesPayload.Source.RootFolderPath,
                Is.EqualTo(AssetPath.From("Packages/com.example.tools")));
        }

        [Test]
        public void FolderChevronAndTagsAddButton_AreNotSplitDragSources()
        {
            var tree = CreateFolderTree(
                "Assets",
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Art", "Assets/Art", 1, false));
            var rootRow = BindRow(tree, 0);
            var disclosure = rootRow.Q<Button>();

            Assert.That(disclosure, Is.Not.Null);
            Assert.That(tree.SnapshotSplitDragPayload(disclosure), Is.Null);

            var tags = new TagsFilterWidget();
            tags.SetTags(new[]
            {
                new ZepTagFilterOption(
                    new ZepTag(
                        "blue",
                        "Blue",
                        ZepTagColor.Blue,
                        true),
                    1)
            });
            var addButton = tags.Q<Button>("zep-tag-create-button");
            var tagLabel = tags.Q<VisualElement>(
                "zep-sidebar-tags-row-blue").Q<Label>();

            Assert.That(tags.SnapshotSplitDragPayload(addButton), Is.Null);
            Assert.That(tags.SnapshotSplitDragPayload(tagLabel).Source,
                Is.EqualTo(ZepPaneSource.FixedTag("blue")));
        }

        [Test]
        public void PressInteraction_CommitsSubThresholdClickAndCancelsItOnDrag()
        {
            var surface = new VisualElement();
            var row = new VisualElement();
            surface.Add(row);
            var payload = new ZepSplitDragPayload(
                ZepPaneSource.FixedTag("blue"),
                "Blue");
            var clickCount = 0;
            var dragCount = 0;
            var interaction = new SidebarSplitDragPrepareInteraction(
                surface,
                target => ReferenceEquals(target, row) ? payload : null,
                clicked => clickCount++,
                dragged => dragCount++);

            SendPointerDown(interaction, row, 1, Vector2.zero);
            SendPointerUp(interaction, surface, 1, Vector2.zero);
            Assert.That(clickCount, Is.Zero, "Right-click must remain unrelated.");
            Assert.That(dragCount, Is.Zero);

            SendPointerDown(interaction, row, 0, Vector2.zero);
            Assert.That(interaction.IsPending, Is.True);
            SendPointerMove(interaction, surface, new Vector2(5f, 0f));
            SendPointerUp(interaction, surface, 0, new Vector2(5f, 0f));

            Assert.That(clickCount, Is.EqualTo(1));
            Assert.That(dragCount, Is.Zero);
            Assert.That(interaction.IsPending, Is.False);

            SendPointerDown(interaction, row, 0, Vector2.zero);
            SendPointerMove(interaction, surface, new Vector2(7f, 0f));

            Assert.That(clickCount, Is.EqualTo(1));
            Assert.That(dragCount, Is.EqualTo(1));
            Assert.That(interaction.IsPending, Is.False);
        }

        [Test]
        public void FolderClickCommit_UsesSnappedPathAndKeyboardSelectionRemainsImmediate()
        {
            var tree = CreateFolderTree(
                "Assets",
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Art", "Assets/Art", 1, false));
            var artRow = BindRow(tree, 1);
            var payload = tree.SnapshotSplitDragPayload(artRow.Q<Label>());
            string clickedPath = null;
            tree.FolderClicked += path => clickedPath = path;

            Invoke(tree, "CommitSplitSourceClick", payload);

            Assert.That(clickedPath, Is.EqualTo("Assets/Art"));
            Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets/Art"));

            using var key = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.LeftArrow
            });
            tree.HandleListKeyDown(key);

            Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets"));
            Assert.That(clickedPath, Is.EqualTo("Assets"));
        }

        [UnityTest]
        public IEnumerator AttachedFolderPointerClick_DefersNavigationUntilPointerUp()
        {
            var tree = CreateFolderTree(
                "Assets",
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Art", "Assets/Art", 1, false));
            string clickedPath = null;
            tree.FolderClicked += path => clickedPath = path;
            var window = ScriptableObject.CreateInstance<SplitDragTestWindow>();
            window.position = new Rect(40f, 40f, 360f, 260f);
            window.Show();
            window.rootVisualElement.Add(tree);

            try
            {
                yield return null;
                var list = tree.Q<ListView>();
                var artRow = list.GetRootElementForIndex(1);
                Assert.That(artRow, Is.Not.Null);
                var target = artRow.Q<Label>();
                var position = target.worldBound.center;

                SendPointerDownThroughHierarchy(target, 0, position);

                Assert.That(clickedPath, Is.Null);
                Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets"));

                SendPointerUpThroughHierarchy(target, 0, position);

                Assert.That(clickedPath, Is.EqualTo("Assets/Art"));
                Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets/Art"));
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void AddPreviewMountsOnePureGhostAndRepeatedUpdatesStayIdempotent()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var weights = workspace.PaneWeights.ToArray();

            PrepareTagDrag();
            SendDragUpdated(workspace.PrimaryPaneView);

            var ghost = WithClass(
                workspace,
                ZepUiClasses.SplitDropGhost).Single();
            Assert.That(ghost.parent, Is.SameAs(workspace));
            Assert.That(ghost.pickingMode, Is.EqualTo(PickingMode.Ignore));
            Assert.That(workspace.PaneCount, Is.EqualTo(1));
            Assert.That(state.AuxiliaryPanes, Is.Empty);
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            Assert.That(workspace.PaneWeights, Is.EqualTo(weights));
            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropPreviewVisible),
                Is.Empty,
                "A valid add uses the structural ghost, not the status overlay.");

            SendDragUpdated(workspace.PrimaryPaneView);

            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropGhost).Single(),
                Is.SameAs(ghost));
            Assert.That(workspace.PaneCount, Is.EqualTo(1));
            Assert.That(state.AuxiliaryPanes, Is.Empty);
            Assert.That(workspace.PaneWeights, Is.EqualTo(weights));
        }

        [Test]
        public void WorkspaceInterceptsRecognizedDragBeforeChildAndPerformsAddFocusReplace()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var child = workspace.PrimaryPaneView;
            var childUpdated = false;
            var childPerformed = false;
            child.RegisterCallback<DragUpdatedEvent>(evt => childUpdated = true);
            child.RegisterCallback<DragPerformEvent>(evt => childPerformed = true);

            PrepareTagDrag();
            SendDragUpdated(child);

            Assert.That(childUpdated, Is.False);
            Assert.That(workspace.IsSplitDropPreviewVisible, Is.True);
            Assert.That(workspace.IsSplitDropPreviewRejected, Is.False);
            Assert.That(workspace.SplitDropPreviewText,
                Is.EqualTo("Release to open \u201cBlue\u201d in Split View"));
            Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Link));

            SendDragPerform(child);

            Assert.That(childPerformed, Is.False);
            Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(1));
            Assert.That(state.AuxiliaryPanes[0].Source,
                Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(workspace.PaneCount, Is.EqualTo(2));
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
            Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);

            var tagDragPane = state.AuxiliaryPanes[0];
            state.ActivatePane(state.PrimaryPane);
            PrepareTagDrag();
            SendDragUpdated(workspace.GetPaneView(tagDragPane));
            Assert.That(workspace.SplitDropPreviewText,
                Is.EqualTo("Focus \u201cBlue\u201d Split"));
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropPreviewVisible).Count,
                Is.EqualTo(1));
            SendDragPerform(workspace.GetPaneView(tagDragPane));

            Assert.That(state.ActivePane, Is.SameAs(tagDragPane));
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitPaneDropPulse).Count,
                Is.EqualTo(1));

            workspace.CancelSplitDrag();
            var folderPayload = new ZepSplitDragPayload(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art")),
                "Art");
            ZepSplitDragAndDrop.Prepare(folderPayload);
            SendDragUpdated(workspace.GetPaneView(tagDragPane));
            Assert.That(workspace.SplitDropPreviewText,
                Is.EqualTo("Replace Split with \u201cArt\u201d"));
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropPreviewVisible).Count,
                Is.EqualTo(1));
            SendDragPerform(workspace.GetPaneView(tagDragPane));

            Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(1));
            Assert.That(state.AuxiliaryPanes[0].Source,
                Is.EqualTo(folderPayload.Source));
        }

        [Test]
        public void WorkspaceRejectsTooNarrowAddAndInvalidSourceWithExactFeedback()
        {
            var state = new ZepState();
            string feedback = null;
            var workspace = CreateWorkspace(state, message => feedback = message);
            workspace.ApplyResponsiveWidth(
                (AssetsAreaConstants.SplitPaneMinimumWidth * 2f)
                + ZepPaneSplitterWidget.Width
                - 1f);

            PrepareTagDrag();
            SendDragUpdated(workspace.PrimaryPaneView);

            Assert.That(workspace.IsSplitDropPreviewRejected, Is.True);
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropPreviewVisible).Count,
                Is.EqualTo(1));
            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropPreviewPresented),
                Is.Empty,
                "Rejected previews must appear immediately without open motion.");
            Assert.That(workspace.SplitDropPreviewText,
                Is.EqualTo(ZepSplitWorkspaceView.WidthFeedback));
            Assert.That(DragAndDrop.visualMode,
                Is.EqualTo(DragAndDropVisualMode.Rejected));

            SendDragPerform(workspace.PrimaryPaneView);

            Assert.That(state.AuxiliaryPanes, Is.Empty);
            Assert.That(feedback,
                Is.EqualTo(ZepSplitWorkspaceView.WidthFeedback));

            var payload = new ZepSplitDragPayload(
                ZepPaneSource.FixedTag("deleted"),
                "Deleted");
            var method = typeof(ZepSplitWorkspaceView).GetMethod(
                "ResolveSplitDropTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var invalid = method.Invoke(workspace, new object[]
            {
                payload,
                payload.Source,
                workspace.PrimaryPaneView,
                false
            });
            var invalidType = invalid.GetType();

            Assert.That(
                (bool)invalidType.GetProperty("IsValid",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(invalid),
                Is.False);
            Assert.That(
                (string)invalidType.GetProperty("Message",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(invalid),
                Is.EqualTo("\u201cDeleted\u201d is no longer available."));
        }

        [Test]
        public void CancelDetachAndExternalFallbackClearOrIgnoreTypedSplitSession()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            PrepareTagDrag();
            SendDragUpdated(workspace.PrimaryPaneView);

            Assert.That(ZepWindow.ShouldIgnoreExternalDragFallback(), Is.True);
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost).Count,
                Is.EqualTo(1));

            workspace.CancelSplitDrag();

            Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
            Assert.That(ZepWindow.ShouldIgnoreExternalDragFallback(), Is.False);

            PrepareTagDrag();
            SendDragUpdated(workspace.PrimaryPaneView);
            workspace.Teardown();

            Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
        }

        [Test]
        public void ConsumingTheGhostMidOpenLeavesNoPendingPreviewWork()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            PrepareTagDrag();
            SendDragUpdated(workspace.PrimaryPaneView);

            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost).Count,
                Is.EqualTo(1));
            Assert.That(
                workspace.HasPendingMotion,
                Is.True,
                "The ghost presents on the next panel tick, so showing it must "
                + "leave that step pending.");

            // The pane opens before the ghost's presenting tick ever runs: the
            // real slot takes its place, so every step still scheduled for it
            // must be retired with it.
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            workspace.ApplyResponsiveWidth(800f);

            Assert.That(workspace.PaneCount, Is.EqualTo(2));
            Assert.That(
                WithClass(workspace, ZepUiClasses.SplitDropGhost),
                Is.Empty);
            Assert.That(
                WithClass(
                    workspace,
                    ZepUiClasses.SplitDropPreviewVisible),
                Is.Empty);
            Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
            Assert.That(workspace.HasPendingMotion, Is.False);
        }

        [UnityTest]
        public IEnumerator AttachedAddPreviewShrinksPrimaryAndCancelRestoresIt()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required for preview geometry.");
            var state = new ZepState();
            var window = ScriptableObject.CreateInstance<SplitDragTestWindow>();
            window.position = new Rect(40f, 40f, 1100f, 600f);
            window.Show();
            var shell = MountStyledWorkspace(window, state, out var workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(workspace.resolvedStyle.width);
                yield return null;
                var primaryWrapper = GetPaneWrapper(workspace.PrimaryPaneView);
                var originalPrimaryWidth = primaryWrapper.worldBound.width;
                var weights = workspace.PaneWeights.ToArray();
                Assert.That(
                    originalPrimaryWidth,
                    Is.GreaterThan(
                        (AssetsAreaConstants.SplitPaneMinimumWidth * 2f)
                        + ZepPaneSplitterWidget.Width));

                PrepareTagDrag();
                SendDragUpdatedThroughHierarchy(
                    workspace.PrimaryPaneView,
                    workspace.PrimaryPaneView.worldBound.center);

                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost).Count,
                    Is.EqualTo(1));
                Assert.That(workspace.PaneCount, Is.EqualTo(1));
                Assert.That(state.AuxiliaryPanes, Is.Empty);
                Assert.That(workspace.PaneWeights, Is.EqualTo(weights));

                yield return WaitForEditorSeconds(
                    (ZepSplitWorkspaceView.DropGhostMotionMs + 150d)
                    / 1000d);

                var ghost = WithClass(
                    workspace,
                    ZepUiClasses.SplitDropGhost).Single();
                Assert.That(
                    ghost.ClassListContains(
                        ZepUiClasses.SplitDropGhostPresented),
                    Is.True);
                Assert.That(
                    primaryWrapper.worldBound.width,
                    Is.LessThan(
                        originalPrimaryWidth
                        - (AssetsAreaConstants.SplitPaneMinimumWidth * 0.5f)));
                Assert.That(
                    ghost.worldBound.width,
                    Is.GreaterThan(AssetsAreaConstants.SplitPaneMinimumWidth));

                workspace.CancelSplitDrag();

                Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost).Count,
                    Is.EqualTo(1),
                    "Cancel should reverse the mounted ghost before retiring it.");
                Assert.That(
                    WithClass(
                        workspace,
                        ZepUiClasses.SplitDropGhostPresented),
                    Is.Empty);

                yield return WaitForEditorSeconds(
                    (ZepSplitWorkspaceView.DropGhostMotionMs + 150d)
                    / 1000d);

                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost),
                    Is.Empty);
                Assert.That(
                    primaryWrapper.worldBound.width,
                    Is.EqualTo(originalPrimaryWidth).Within(
                        Mathf.Max(8f, originalPrimaryWidth * 0.05f)));
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
                Assert.That(state.AuxiliaryPanes, Is.Empty);
                Assert.That(workspace.PaneWeights, Is.EqualTo(weights));
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator AttachedRapidReentryAndPerformPreservePreviewGeometry()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required for preview geometry.");
            var state = new ZepState();
            var window = ScriptableObject.CreateInstance<SplitDragTestWindow>();
            window.position = new Rect(40f, 40f, 1100f, 600f);
            window.Show();
            var shell = MountStyledWorkspace(window, state, out var workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(workspace.resolvedStyle.width);
                yield return null;
                var primaryWrapper = GetPaneWrapper(workspace.PrimaryPaneView);
                PrepareTagDrag();
                SendDragUpdatedThroughHierarchy(
                    workspace.PrimaryPaneView,
                    workspace.PrimaryPaneView.worldBound.center);
                yield return WaitForEditorSeconds(
                    (ZepSplitWorkspaceView.DropGhostMotionMs + 150d)
                    / 1000d);

                var ghost = WithClass(
                    workspace,
                    ZepUiClasses.SplitDropGhost).Single();
                SendDragLeaveThroughHierarchy(
                    workspace.PrimaryPaneView,
                    new Vector2(
                        workspace.worldBound.xMax + 10f,
                        workspace.worldBound.yMax + 10f));
                Assert.That(
                    ghost.ClassListContains(
                        ZepUiClasses.SplitDropGhostPresented),
                    Is.False);

                SendDragUpdatedThroughHierarchy(
                    workspace.PrimaryPaneView,
                    workspace.PrimaryPaneView.worldBound.center);

                Assert.That(
                    WithClass(
                        workspace,
                        ZepUiClasses.SplitDropGhost).Single(),
                    Is.SameAs(ghost));
                Assert.That(
                    ghost.ClassListContains(
                        ZepUiClasses.SplitDropGhostPresented),
                    Is.True);

                yield return WaitForEditorSeconds(
                    (ZepSplitWorkspaceView.DropGhostMotionMs + 150d)
                    / 1000d);

                Assert.That(
                    WithClass(
                        workspace,
                        ZepUiClasses.SplitDropGhost).Single(),
                    Is.SameAs(ghost),
                    "Stale leave cleanup must not retire a re-entered preview.");
                Assert.That(workspace.IsSplitDropPreviewVisible, Is.True);
                Assert.That(workspace.PaneCount, Is.EqualTo(1));
                Assert.That(state.AuxiliaryPanes, Is.Empty);

                var previewPrimaryWidth = primaryWrapper.worldBound.width;
                var previewAuxiliaryWidth = ghost.worldBound.width
                    - ZepPaneSplitterWidget.Width;
                SendDragPerform(workspace.PrimaryPaneView);

                Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(1));
                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(workspace.DividerCount, Is.EqualTo(1));
                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost),
                    Is.Empty);
                Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
                Assert.That(workspace.SplitDropPreviewText, Is.Empty);

                yield return WaitForEditorSeconds(0.04d);

                var auxiliary = state.AuxiliaryPanes.Single();
                var auxiliaryWrapper = GetPaneWrapper(
                    workspace.GetPaneView(auxiliary));
                var continuityTolerance = Mathf.Max(
                    24f,
                    workspace.worldBound.width * 0.12f);
                Assert.That(
                    primaryWrapper.worldBound.width,
                    Is.EqualTo(previewPrimaryWidth).Within(continuityTolerance),
                    "The primary pane must keep the width established by the ghost.");
                Assert.That(
                    auxiliaryWrapper.worldBound.width,
                    Is.EqualTo(previewAuxiliaryWidth).Within(continuityTolerance),
                    "The committed pane must start at the ghost pane width.");
                Assert.That(
                    auxiliaryWrapper.worldBound.width,
                    Is.GreaterThan(AssetsAreaConstants.SplitPaneMinimumWidth));

                yield return WaitForEditorSeconds(
                    (ZepSplitWorkspaceView.OpenMotionMs + 150d)
                    / 1000d);

                Assert.That(
                    workspace.GetPaneLifecycle(auxiliary),
                    Is.EqualTo(ZepSplitPaneLifecycle.Open));
                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost),
                    Is.Empty);
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator DescendantDragLeaveDoesNotFlickerAndTerminalExitCleansSession()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required for world-bound drag leave.");
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitDragTestWindow>();
            window.position = new Rect(40f, 40f, 700f, 400f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(workspace.resolvedStyle.width);
                var childUpdated = false;
                workspace.PrimaryPaneView.RegisterCallback<DragUpdatedEvent>(
                    evt => childUpdated = true);
                PrepareTagDrag();
                SendDragUpdatedThroughHierarchy(
                    workspace.PrimaryPaneView,
                    workspace.worldBound.center);
                Assert.That(childUpdated, Is.False);
                Assert.That(workspace.IsSplitDropPreviewVisible, Is.True);
                yield return WaitForEditorSeconds(0.03d);
                Assert.That(
                    WithClass(
                        workspace,
                        ZepUiClasses.SplitDropGhostPresented).Count,
                    Is.EqualTo(1));

                SendDragLeaveThroughHierarchy(
                    workspace.PrimaryPaneView,
                    workspace.worldBound.center);
                Assert.That(
                    workspace.IsSplitDropPreviewVisible,
                    Is.True,
                    "Moving between workspace descendants must not clear preview.");

                SendDragLeaveThroughHierarchy(
                    workspace.PrimaryPaneView,
                    new Vector2(
                        workspace.worldBound.xMax + 10f,
                        workspace.worldBound.yMax + 10f));
                Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.True);
                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost).Count,
                    Is.EqualTo(1),
                    "A true leave should reverse the ghost before unmounting.");
                Assert.That(
                    WithClass(
                        workspace,
                        ZepUiClasses.SplitDropGhostPresented),
                    Is.Empty);

                yield return WaitForEditorSeconds(
                    (ZepSplitWorkspaceView.DropGhostMotionMs + 150d)
                    / 1000d);

                Assert.That(
                    WithClass(workspace, ZepUiClasses.SplitDropGhost),
                    Is.Empty);
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.True);

                SendDragExited(window.rootVisualElement);
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator NewSplitDragClassesHaveLiveConsumersAndStructuralRules()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitDragTestWindow>();
            window.position = new Rect(40f, 40f, 700f, 400f);
            window.Show();
            window.rootVisualElement.Add(workspace);
            var uss = File.ReadAllText(Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(800f);
                PrepareTagDrag();
                SendDragUpdated(workspace.PrimaryPaneView);
                yield return WaitForEditorSeconds(0.03d);

                var ghostClasses = new[]
                {
                    ZepUiClasses.SplitDropGhost,
                    ZepUiClasses.SplitDropGhostPresented
                };
                for (var i = 0; i < ghostClasses.Length; i++)
                {
                    StringAssert.Contains(
                        "." + ghostClasses[i],
                        uss,
                        ghostClasses[i]);
                    Assert.That(
                        WithClass(workspace, ghostClasses[i]).Count,
                        Is.GreaterThan(0),
                        ghostClasses[i]);
                }

                workspace.ApplyResponsiveWidth(300f);
                SendDragUpdated(workspace.PrimaryPaneView);
                var overlayClasses = new[]
                {
                    ZepUiClasses.SplitDropPreview,
                    ZepUiClasses.SplitDropPreviewVisible,
                    ZepUiClasses.SplitDropPreviewRejected,
                    ZepUiClasses.SplitDropPreviewCard,
                    ZepUiClasses.SplitDropPreviewLabel
                };

                for (var i = 0; i < overlayClasses.Length; i++)
                {
                    StringAssert.Contains(
                        "." + overlayClasses[i],
                        uss,
                        overlayClasses[i]);
                    Assert.That(
                        WithClass(workspace, overlayClasses[i]).Count,
                        Is.GreaterThan(0),
                        overlayClasses[i]);
                }

                workspace.ApplyResponsiveWidth(800f);
                SendDragUpdated(workspace.PrimaryPaneView);
                SendDragPerform(workspace.PrimaryPaneView);
                state.ActivatePane(state.PrimaryPane);
                PrepareTagDrag();
                SendDragPerform(workspace.GetPaneView(state.AuxiliaryPanes[0]));

                StringAssert.Contains(
                    "." + ZepUiClasses.SplitPaneDropPulse,
                    uss);
                Assert.That(
                    WithClass(
                        workspace,
                        ZepUiClasses.SplitPaneDropPulse).Count,
                    Is.EqualTo(1));
            }
            finally
            {
                window.Close();
            }
        }

        private static FolderTreeWidget CreateFolderTree(
            string root,
            params ZepFolderEntry[] folders)
        {
            SessionState.EraseString(
                "Zep.FolderTreeExpanded." + root);
            var tree = new FolderTreeWidget(
                root,
                new UnityZepIconRepository(),
                ChildrenQuery(folders));
            tree.Apply(root, root);
            // The tree does not own the selection: it publishes the folder it
            // wants and the shell republishes it. Without that round trip a
            // committed click would leave SelectedFolderEntry on the old
            // folder, exactly as it would in the panel.
            tree.FolderClicked += path => tree.Apply(path, path);
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

        private static IEnumerator WaitForEditorSeconds(double seconds)
        {
            var end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
            {
                yield return null;
            }
        }

        private static VisualElement BindRow(FolderTreeWidget tree, int index)
        {
            var list = tree.Q<ListView>();
            var row = list.makeItem();
            list.bindItem(row, index);
            return row;
        }

        private static ZepSplitWorkspaceView CreateWorkspace(
            ZepState state,
            Action<string> feedback = null)
        {
            return new ZepSplitWorkspaceView(
                state,
                new UnityZepIconRepository(),
                showFeedback: feedback);
        }

        private static ZepShellView MountStyledWorkspace(
            SplitDragTestWindow window,
            ZepState state,
            out ZepSplitWorkspaceView workspace)
        {
            var shell = new ZepShellView(state);
            window.rootVisualElement.Add(shell);
            workspace = shell.Q<ZepSplitWorkspaceView>();
            Assert.That(workspace, Is.Not.Null);
            return shell;
        }

        private static VisualElement GetPaneWrapper(ZepPaneView paneView)
        {
            Assert.That(paneView, Is.Not.Null);
            var wrapper = paneView.parent?.parent;
            Assert.That(wrapper, Is.Not.Null);
            Assert.That(
                wrapper.ClassListContains(ZepUiClasses.SplitPane),
                Is.True);
            return wrapper;
        }

        private static void PrepareTagDrag()
        {
            ZepSplitDragAndDrop.Prepare(
                new ZepSplitDragPayload(
                    ZepPaneSource.FixedTag("blue"),
                    "Blue"));
        }

        private static void SendPointerDown(
            SidebarSplitDragPrepareInteraction interaction,
            VisualElement target,
            int button,
            Vector2 position)
        {
            using var evt = PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                button = button,
                clickCount = 1,
                mousePosition = position
            });
            evt.target = target;
            Invoke(interaction, "OnPointerDown", evt);
        }

        private static void SendPointerMove(
            SidebarSplitDragPrepareInteraction interaction,
            VisualElement target,
            Vector2 position)
        {
            using var evt = PointerMoveEvent.GetPooled(new Event
            {
                type = EventType.MouseDrag,
                button = 0,
                mousePosition = position
            });
            evt.target = target;
            Invoke(interaction, "OnPointerMove", evt);
        }

        private static void SendPointerDownThroughHierarchy(
            VisualElement target,
            int button,
            Vector2 position)
        {
            using var evt = PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                button = button,
                clickCount = 1,
                mousePosition = position
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static void SendPointerUp(
            SidebarSplitDragPrepareInteraction interaction,
            VisualElement target,
            int button,
            Vector2 position)
        {
            using var evt = PointerUpEvent.GetPooled(new Event
            {
                type = EventType.MouseUp,
                button = button,
                clickCount = 1,
                mousePosition = position
            });
            evt.target = target;
            Invoke(interaction, "OnPointerUp", evt);
        }

        private static void SendPointerUpThroughHierarchy(
            VisualElement target,
            int button,
            Vector2 position)
        {
            using var evt = PointerUpEvent.GetPooled(new Event
            {
                type = EventType.MouseUp,
                button = button,
                clickCount = 1,
                mousePosition = position
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static void SendDragUpdated(
            VisualElement target,
            Vector2? position = null)
        {
            using var evt = DragUpdatedEvent.GetPooled(new Event
            {
                type = EventType.DragUpdated,
                mousePosition = position ?? Vector2.zero
            });
            evt.target = target;
            var workspace = target as ZepSplitWorkspaceView
                ?? target.GetFirstAncestorOfType<ZepSplitWorkspaceView>();
            Assert.That(workspace, Is.Not.Null);
            Invoke(workspace, "OnSplitDragUpdated", evt);
            Assert.That(evt.isPropagationStopped, Is.True);
        }

        private static void SendDragPerform(VisualElement target)
        {
            using var evt = DragPerformEvent.GetPooled(new Event
            {
                type = EventType.DragPerform,
                mousePosition = Vector2.zero
            });
            evt.target = target;
            var workspace = target as ZepSplitWorkspaceView
                ?? target.GetFirstAncestorOfType<ZepSplitWorkspaceView>();
            Assert.That(workspace, Is.Not.Null);
            Invoke(workspace, "OnSplitDragPerform", evt);
            Assert.That(evt.isPropagationStopped, Is.True);
        }

        private static void SendDragUpdatedThroughHierarchy(
            VisualElement target,
            Vector2 position)
        {
            using var evt = DragUpdatedEvent.GetPooled(new Event
            {
                type = EventType.DragUpdated,
                mousePosition = position
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static void SendDragLeaveThroughHierarchy(
            VisualElement target,
            Vector2 position)
        {
            using var evt = DragLeaveEvent.GetPooled(new Event
            {
                type = EventType.DragExited,
                mousePosition = position
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static void SendDragExited(VisualElement target)
        {
            using var evt = DragExitedEvent.GetPooled(new Event
            {
                type = EventType.DragExited,
                mousePosition = Vector2.zero
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static System.Collections.Generic.List<VisualElement> WithClass(
            VisualElement root,
            string className)
        {
            var result = root.Query<VisualElement>()
                .ToList()
                .Where(element => element.ClassListContains(className))
                .ToList();
            if (root.ClassListContains(className))
            {
                result.Insert(0, root);
            }

            return result;
        }

        private static void Invoke(
            object target,
            string methodName,
            params object[] arguments)
        {
            var method = target.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate =>
                    candidate.Name == methodName
                    && candidate.GetParameters().Length == arguments.Length);
            method.Invoke(target, arguments);
        }

        private sealed class SplitDragTestWindow : EditorWindow
        {
        }
    }
}
