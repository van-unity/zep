using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepSplitWorkspaceViewTests
    {
        private const string AssetViewModePreferenceKey =
            "Zep.Editor.UI.AssetViewMode";
        private string _previousViewMode;

        [SetUp]
        public void SetUp()
        {
            _previousViewMode = EditorPrefs.GetString(
                AssetViewModePreferenceKey,
                string.Empty);
            EditorPrefs.SetString(AssetViewModePreferenceKey, AssetViewMode.List.ToString());
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrEmpty(_previousViewMode))
            {
                EditorPrefs.DeleteKey(AssetViewModePreferenceKey);
            }
            else
            {
                EditorPrefs.SetString(AssetViewModePreferenceKey, _previousViewMode);
            }
        }

        [Test]
        public void PrimaryOnly_ComposesOneWrapperWithoutDividerOrCloseControl()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);

            Assert.That(workspace.PaneCount, Is.EqualTo(1));
            Assert.That(workspace.DividerCount, Is.Zero);
            Assert.That(WithClass(workspace, ZepUiClasses.SplitPane).Count, Is.EqualTo(1));
            Assert.That(workspace.Query<ZepPaneView>().ToList().Count, Is.EqualTo(1));
            Assert.That(WithClass(workspace, ZepUiClasses.SplitPaneCloseButton), Is.Empty);
            Assert.That(
                workspace.ClassListContains(ZepUiClasses.SplitWorkspaceMultiple),
                Is.False);

            var closePrimary = workspace.CloseAuxiliary(state.PrimaryPane);

            Assert.That(closePrimary.Kind, Is.EqualTo(ZepSplitOperationKind.Rejected));
            Assert.That(workspace.PaneCount, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WindowPane_WithTheSameSourceNeverMountsOrChangesTheSplitArrangement(
            bool windowExistsBeforeWorkspace)
        {
            var state = new ZepState();
            var source = ZepPaneSource.FixedTag("blue");
            var split = state.OpenAuxiliary(source).NewPane;
            var windowPane = windowExistsBeforeWorkspace ? state.OpenWindowPane(source) : null;
            var workspace = CreateWorkspace(state);
            var arrangementChanges = 0;
            workspace.ArrangementChanged += () => arrangementChanges++;
            try
            {
                windowPane ??= state.OpenWindowPane(source);
                state.ActivatePane(windowPane);
                windowPane.TrySetFolderSelection("Assets/Art");
                windowPane.SetSearchText("hero");

                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(workspace.GetPaneView(windowPane), Is.Null);
                Assert.That(workspace.GetPaneView(split), Is.Not.Null);
                Assert.That(state.ActivePane, Is.SameAs(windowPane));
                Assert.That(arrangementChanges, Is.Zero);

                state.CloseWindowPane(windowPane);

                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(workspace.GetPaneView(split), Is.Not.Null);
                Assert.That(state.AuxiliaryPanes, Is.EqualTo(new[] { split }));
                Assert.That(arrangementChanges, Is.Zero);
            }
            finally
            {
                workspace.Teardown();
            }
        }

        [Test]
        public void OrderedState_ComposesNWrappersAndNMinusOneDividers()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var workspace = CreateWorkspace(state);
            var views = workspace.Query<ZepPaneView>().ToList();

            Assert.That(workspace.PaneCount, Is.EqualTo(2));
            Assert.That(workspace.DividerCount, Is.EqualTo(workspace.PaneCount - 1));
            Assert.That(views.Select(view => view.Pane), Is.EqualTo(new[]
            {
                state.PrimaryPane,
                auxiliary
            }));
            Assert.That(
                workspace.ClassListContains(ZepUiClasses.SplitWorkspaceMultiple),
                Is.True);
            var close = WithClass(workspace, ZepUiClasses.SplitPaneCloseButton).Single();
            var navigationBar = workspace.GetPaneView(auxiliary).SplitNavigationBar;
            Assert.That(
                close.parent,
                Is.SameAs(navigationBar),
                "the close control lives in the auxiliary's own nav row");
            Assert.That(
                navigationBar.IndexOf(close),
                Is.EqualTo(navigationBar.childCount - 1),
                "after the breadcrumb, at the end of the row");
            Assert.That(close.tooltip, Is.EqualTo("Close Tag \u00b7 blue split"));
            Assert.That(
                workspace.PrimaryPaneView.SplitNavigationBar,
                Is.Null,
                "the primary has no row and no title of its own");
        }

        [Test]
        public void ConfiguredCapacity_ComposesAndOperatesEveryAuxiliaryByCollectionOrder()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(
                (ZepState.MaxSplitViews + 1) * 400f);
            var opened = new List<ZepPaneState>();
            for (var i = 0; i < ZepState.MaxSplitViews; i++)
            {
                var root = AssetPath.From("Assets/Root" + i);
                var result = workspace.OpenAuxiliary(
                    ZepPaneSource.RootedFolder(root));
                Assert.That(result.Kind,
                    Is.EqualTo(ZepSplitOperationKind.Added));
                result.NewPane.TrySetFolderSelection(root.Value + "/Child");
                result.NewPane.SetSearchText("pane-" + i);
                opened.Add(result.NewPane);
            }

            Assert.That(workspace.PaneCount,
                Is.EqualTo(ZepState.MaxSplitViews + 1));
            Assert.That(workspace.DividerCount,
                Is.EqualTo(ZepState.MaxSplitViews));
            Assert.That(
                workspace.Query<ZepPaneView>().ToList()
                    .Select(view => view.Pane),
                Is.EqualTo(new[] { state.PrimaryPane }.Concat(opened)));
            Assert.That(workspace.PaneWeights.Sum(),
                Is.EqualTo(1f).Within(0.0001f));

            for (var i = 0; i < opened.Count; i++)
            {
                Assert.That(opened[i].CurrentFolderPath,
                    Is.EqualTo(AssetPath.From("Assets/Root" + i + "/Child")));
                Assert.That(opened[i].SearchText, Is.EqualTo("pane-" + i));
            }

            var duplicate = workspace.OpenAuxiliary(opened[0].Source);
            Assert.That(duplicate.Kind,
                Is.EqualTo(ZepSplitOperationKind.Activated));
            Assert.That(state.AuxiliaryPanes, Is.EqualTo(opened));

            var dividers = workspace.Query<ZepPaneSplitterWidget>().ToList();
            if (dividers.Count > 1)
            {
                var before = workspace.PaneWeights.ToArray();
                Invoke(workspace, "OnDividerKeyboardResizeRequested", dividers[1], 20f);
                Assert.That(workspace.PaneWeights[0],
                    Is.EqualTo(before[0]).Within(0.0001f));
                Assert.That(workspace.PaneWeights[1], Is.Not.EqualTo(before[1]));
                Assert.That(workspace.PaneWeights[2], Is.Not.EqualTo(before[2]));
                Assert.That(workspace.PaneWeights.Sum(),
                    Is.EqualTo(1f).Within(0.0001f));
            }

            var explicitTarget = opened[opened.Count - 1];
            var replacement = workspace.OpenAuxiliary(
                ZepPaneSource.FixedTag("replacement"),
                explicitTarget);
            Assert.That(replacement.Kind,
                Is.EqualTo(ZepSplitOperationKind.Replaced));
            Assert.That(workspace.GetPaneView(explicitTarget), Is.Null);
            Assert.That(workspace.GetPaneView(replacement.NewPane), Is.Not.Null);
            Assert.That(state.AuxiliaryPanes.Last(), Is.SameAs(replacement.NewPane));

            state.ActivatePane(state.PrimaryPane);
            var inactiveClose = workspace.CloseAuxiliary(state.AuxiliaryPanes[0]);
            workspace.CompleteRemoval(inactiveClose.OldPane);
            Assert.That(inactiveClose.Kind,
                Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));

            var active = state.AuxiliaryPanes.LastOrDefault();
            if (active == null)
            {
                active = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            }

            state.ActivatePane(active);
            var activeClose = workspace.CloseAuxiliary(active);
            workspace.CompleteRemoval(activeClose.OldPane);
            Assert.That(activeClose.Kind,
                Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
            Assert.That(workspace.PaneCount, Is.EqualTo(state.AuxiliaryPanes.Count + 1));
            Assert.That(workspace.DividerCount, Is.EqualTo(state.AuxiliaryPanes.Count));
        }

        [Test]
        public void AddActivateReplaceAndClose_PreserveUnaffectedPrimaryInstance()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var primaryView = workspace.PrimaryPaneView;
            var folderSource = ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art"));

            var added = workspace.OpenAuxiliary(folderSource);
            var folderPane = added.NewPane;
            var folderView = workspace.GetPaneView(folderPane);

            state.ActivatePane(state.PrimaryPane);
            var activated = workspace.OpenAuxiliary(folderSource);

            Assert.That(activated.Kind, Is.EqualTo(ZepSplitOperationKind.Activated));
            Assert.That(workspace.GetPaneView(folderPane), Is.SameAs(folderView));

            state.ActivatePane(state.PrimaryPane);
            var replaced = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var tagPane = replaced.NewPane;

            Assert.That(replaced.Kind, Is.EqualTo(ZepSplitOperationKind.Replaced));
            Assert.That(workspace.GetPaneView(folderPane), Is.Null);
            Assert.That(workspace.GetPaneView(tagPane), Is.Not.Null);
            Assert.That(folderPane.VisibleAssetsProvider, Is.Null);
            Assert.That(workspace.PrimaryPaneView, Is.SameAs(primaryView));
            Assert.That(workspace.PaneCount, Is.EqualTo(2));

            var closed = workspace.CloseAuxiliary(tagPane);

            Assert.That(closed.Kind, Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            Assert.That(workspace.PaneCount, Is.EqualTo(1));
            Assert.That(workspace.DividerCount, Is.Zero);
            Assert.That(tagPane.VisibleAssetsProvider, Is.Null);
            Assert.That(workspace.PrimaryPaneView, Is.SameAs(primaryView));
        }

        [Test]
        public void ClosingInactiveAuxiliary_PreservesActivePrimaryAndItsView()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            state.ActivatePane(state.PrimaryPane);
            var workspace = CreateWorkspace(state);
            var primaryView = workspace.PrimaryPaneView;

            state.CloseAuxiliary(auxiliary);

            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            Assert.That(workspace.PrimaryPaneView, Is.SameAs(primaryView));
            Assert.That(workspace.PaneCount, Is.EqualTo(1));
        }

        [Test]
        public void TooNarrowAdd_IsRejectedWithoutMutatingStateAndReportsExactFeedback()
        {
            var state = new ZepState();
            string feedback = null;
            var workspace = CreateWorkspace(state, message => feedback = message);
            workspace.ApplyResponsiveWidth(
                (AssetsAreaConstants.SplitPaneMinimumWidth * 2f)
                + ZepPaneSplitterWidget.Width
                - 1f);

            var result = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Rejected));
            Assert.That(state.AuxiliaryPanes, Is.Empty);
            Assert.That(workspace.PaneCount, Is.EqualTo(1));
            Assert.That(feedback, Is.EqualTo(ZepSplitWorkspaceView.WidthFeedback));
        }

        [Test]
        public void SameSourceFocusAndAtCapacityReplacement_RemainValidWhileSqueezed()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var tagPane = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            workspace.ApplyResponsiveWidth(240f);
            state.ActivatePane(state.PrimaryPane);

            var focus = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            state.ActivatePane(state.PrimaryPane);
            var replacement = workspace.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Audio")));

            Assert.That(focus.Kind, Is.EqualTo(ZepSplitOperationKind.Activated));
            Assert.That(focus.NewPane, Is.SameAs(tagPane));
            Assert.That(replacement.Kind, Is.EqualTo(ZepSplitOperationKind.Replaced));
            Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(1));
            Assert.That(workspace.PaneCount, Is.EqualTo(2));
        }

        [Test]
        public void DividerKeyboardResizeClampsAdjacentWeightsAndDoubleClickEqualizes()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var divider = workspace.Q<ZepPaneSplitterWidget>();

            using (var keyEvent = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.RightArrow
            }))
            {
                Invoke(divider, "OnKeyDown", keyEvent);
            }

            Assert.That(workspace.PaneWeights.Sum(), Is.EqualTo(1f).Within(0.0001f));
            Assert.That(workspace.PaneWeights[0], Is.GreaterThan(0.5f));
            Assert.That(workspace.PaneWeights[1], Is.LessThan(0.5f));

            Invoke(workspace, "OnDividerKeyboardResizeRequested", divider, 10000f);
            var panePixelWidth = 800f - ZepPaneSplitterWidget.Width;
            Assert.That(
                workspace.PaneWeights[1] * panePixelWidth,
                Is.EqualTo(AssetsAreaConstants.SplitPaneMinimumWidth).Within(0.5f));

            using (var mouseEvent = MouseDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                button = 0,
                clickCount = 2
            }))
            {
                Invoke(divider, "OnMouseDown", mouseEvent);
            }

            Assert.That(workspace.PaneWeights[0], Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(workspace.PaneWeights[1], Is.EqualTo(0.5f).Within(0.0001f));
        }

        [UnityTest]
        public IEnumerator FocusedDivider_ResizesWithoutMovingGridSelection_AndGridArrowsStillNavigate()
        {
            using var rig = ZepUiControllerRig.Build(new[]
            {
                new AssetRecord("divider-a", "Assets/A.asset", "A", "ScriptableObject"),
                new AssetRecord("divider-b", "Assets/B.asset", "B", "ScriptableObject"),
                new AssetRecord("divider-c", "Assets/C.asset", "C", "ScriptableObject")
            }, new[] { "Assets", "Assets/Other" });
            rig.State.OpenAuxiliary(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Other")));
            rig.State.ActivatePane(rig.State.PrimaryPane);
            var shell = rig.BuildShell();
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.Show();
            window.position = new Rect(100f, 100f, 1200f, 600f);
            window.Focus();
            window.rootVisualElement.Add(shell);

            try
            {
                for (var frame = 0; frame < 4; frame++) yield return null;
                var workspace = shell.Q<ZepSplitWorkspaceView>();
                var primary = workspace.PrimaryPaneView;
                primary.AssetsArea.SetViewMode(AssetViewMode.Grid);
                for (var frame = 0; frame < 4; frame++) yield return null;
                Assert.That(primary.ActiveAssets.Select(asset => asset.Path),
                    Does.Contain("Assets/A.asset").And.Contain("Assets/B.asset"));
                rig.State.PrimaryPane.SetSelectedAssetPath(AssetPath.From("Assets/A.asset"));
                yield return null;

                var divider = workspace.Q<ZepPaneSplitterWidget>();
                divider.Focus();
                yield return null;
                var focused = window.rootVisualElement.panel.focusController.focusedElement;
                Assert.That(ReferenceEquals(focused, divider), Is.True,
                    $"divider focus required; actual type={focused?.GetType().Name ?? "none"}, "
                    + $"name={(focused as VisualElement)?.name ?? "none"}");
                var before = workspace.PaneWeights.ToArray();

                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow });
                yield return null;

                Assert.That(rig.State.PrimaryPane.SelectedAssetPaths.Select(path => path.Value),
                    Is.EqualTo(new[] { "Assets/A.asset" }), "the focused divider owns this arrow");
                Assert.That(workspace.PaneWeights[0], Is.GreaterThan(before[0]));
                Assert.That(workspace.PaneWeights[1], Is.LessThan(before[1]));

                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.LeftArrow });
                yield return null;

                Assert.That(workspace.PaneWeights, Is.EqualTo(before).Within(0.0001f));
                Assert.That(rig.State.PrimaryPane.SelectedAssetPaths.Select(path => path.Value),
                    Is.EqualTo(new[] { "Assets/A.asset" }));

                primary.Focus();
                yield return null;
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow });
                yield return null;

                Assert.That(rig.State.PrimaryPane.SelectedAssetPaths.Select(path => path.Value),
                    Is.EqualTo(new[] { "Assets/B.asset" }), "grid focus keeps ordinary arrow navigation");
                Assert.That(workspace.PaneWeights, Is.EqualTo(before).Within(0.0001f));
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [Test]
        public void ResizePreservesValidWeightsAndEqualizesOnlyWhenMinimumsCannotFit()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var divider = workspace.Q<ZepPaneSplitterWidget>();
            Invoke(workspace, "OnDividerKeyboardResizeRequested", divider, 100f);
            var leftBeforeResize = workspace.PaneWeights[0];

            workspace.ApplyResponsiveWidth(700f);

            Assert.That(
                workspace.PaneWeights[0],
                Is.EqualTo(leftBeforeResize).Within(0.0001f));

            workspace.ApplyResponsiveWidth(300f);

            Assert.That(workspace.PaneWeights[0], Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(workspace.PaneWeights[1], Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(workspace.PaneCount, Is.EqualTo(2));
        }

        [Test]
        public void SidebarAndSettingsTogglesDoNotResetValidWorkspaceWeights()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var shell = new ZepShellView(state);
            var workspace = shell.Q<ZepSplitWorkspaceView>();
            workspace.ApplyResponsiveWidth(800f);
            var divider = workspace.Q<ZepPaneSplitterWidget>();
            Invoke(workspace, "OnDividerKeyboardResizeRequested", divider, 100f);
            var expected = workspace.PaneWeights.ToArray();

            shell.ToggleSidebar();
            shell.ToggleSidebar();
            Invoke(shell, "ToggleSettingsDrawer");
            Invoke(shell, "ToggleSettingsDrawer");

            Assert.That(workspace.PaneWeights, Is.EqualTo(expected));
            Assert.That(workspace.PaneCount, Is.EqualTo(2));
            shell.Teardown();
        }

        [Test]
        public void EachPaneReceivesItsResolvedResponsiveWidthAndChromePriorityClasses()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(604f);
            var divider = workspace.Q<ZepPaneSplitterWidget>();
            Invoke(workspace, "OnDividerKeyboardResizeRequested", divider, 100f);

            var primaryView = workspace.PrimaryPaneView;
            var auxiliaryView = workspace.GetPaneView(auxiliary);

            Assert.That(primaryView.ResponsiveWidth, Is.EqualTo(400f).Within(1f));
            Assert.That(auxiliaryView.ResponsiveWidth, Is.EqualTo(200f).Within(1f));
            Assert.That(
                primaryView.AssetsArea.ClassListContains(
                    ZepUiClasses.AssetPaneChromeCompact),
                Is.False);
            Assert.That(
                auxiliaryView.AssetsArea.ClassListContains(
                    ZepUiClasses.AssetPaneChromeCompact),
                Is.True);
            Assert.That(
                auxiliaryView.AssetsArea.ClassListContains(
                    ZepUiClasses.AssetPaneChromeTight),
                Is.True);
        }

        [Test]
        public void NewAndReplacementPanesInheritPresentationCapturedFromActivePane()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            workspace.PrimaryPaneView.AssetsArea.SetViewMode(AssetViewMode.Grid);

            var auxiliary = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var auxiliaryView = workspace.GetPaneView(auxiliary);

            Assert.That(auxiliaryView.ViewMode, Is.EqualTo(AssetViewMode.Grid));
            auxiliaryView.AssetsArea.SetViewMode(AssetViewMode.List);
            Assert.That(workspace.PrimaryPaneView.ViewMode, Is.EqualTo(AssetViewMode.Grid));

            state.ActivatePane(state.PrimaryPane);
            var replacement = workspace.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art"))).NewPane;

            Assert.That(
                workspace.GetPaneView(replacement).ViewMode,
                Is.EqualTo(AssetViewMode.Grid));
        }

        [Test]
        public void VisibleAuxiliaryActivationAndCallbacksKeepOriginatingPane()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var workspace = CreateWorkspace(
                state,
                dropOps: new AssetDropOperations(null, state, null, null));
            var auxiliaryView = workspace.GetPaneView(auxiliary);
            state.ActivatePane(state.PrimaryPane);
            ZepPaneState openedFrom = null;
            workspace.OpenRequested += pane => openedFrom = pane;
            AssetDropOperations.FolderDropsReceivedForTests = 0;
            AssetDropOperations.LastFolderDropPaneForTests = null;
            AssetDropOperations.LastFolderDropDestinationForTests = null;

            // Drop first, while the PRIMARY is still the active pane: the
            // destination must come from the pane the view is bound to, not
            // from whatever the panel considers active.
            RaiseCurrentFolderDrop(auxiliaryView.BrowserFeature, "Assets/Other.asset");
            Invoke(auxiliaryView, "ActivateBoundPane");
            RaiseAssetsAreaEvent(auxiliaryView.AssetsArea, "OpenRequested");

            Assert.That(state.ActivePane, Is.SameAs(auxiliary));
            Assert.That(openedFrom, Is.SameAs(auxiliary));
            // The drop reaches the drop owner itself — the workspace no longer
            // relays it — carrying the auxiliary pane and its own folder.
            Assert.That(
                AssetDropOperations.FolderDropsReceivedForTests,
                Is.EqualTo(1));
            Assert.That(
                AssetDropOperations.LastFolderDropPaneForTests,
                Is.SameAs(auxiliary));
            Assert.That(
                AssetDropOperations.LastFolderDropDestinationForTests,
                Is.EqualTo("Assets/Art"));
        }

        [Test]
        public void SplitterDetachCancelsAnInFlightCaptureState()
        {
            var splitter = new ZepPaneSplitterWidget();
            var field = typeof(ZepPaneSplitterWidget).GetField(
                "_isDragging",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(splitter, true);

            Invoke(splitter, "OnDetachFromPanel", (object)null);

            Assert.That(splitter.IsDragging, Is.False);
            Assert.That(
                splitter.ClassListContains(ZepUiClasses.PaneSplitterDragging),
                Is.False);
        }

        [UnityTest]
        public IEnumerator ClosingActiveAuxiliaryTransfersRealFocusToNearestLeftWrapper()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required to prove real focus transfer.");
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                var primaryView = workspace.PrimaryPaneView;
                primaryView.Focus();
                yield return null;
                workspace.GetPaneView(auxiliary).Focus();
                yield return null;

                Assert.That(state.ActivePane, Is.SameAs(auxiliary));

                workspace.CloseAuxiliary(auxiliary);
                yield return null;

                var focused = workspace.panel?.focusController?.focusedElement
                    as VisualElement;
                Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
                Assert.That(focused, Is.SameAs(primaryView));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ClosingANonActiveAuxiliaryLeavesFocusOnTheActivePane()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required to prove real focus transfer.");
            var state = new ZepState();
            var left = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var right = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1200f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                var primaryView = workspace.PrimaryPaneView;
                primaryView.Focus();
                yield return null;
                var rightView = workspace.GetPaneView(right);
                rightView.Focus();
                yield return null;

                Assert.That(state.ActivePane, Is.SameAs(right));

                // Closing a pane that is NOT the active one must move neither
                // the activation nor the real focus.
                workspace.CloseAuxiliary(left);
                yield return null;

                Assert.That(state.ActivePane, Is.SameAs(right));
                Assert.That(
                    workspace.panel?.focusController?.focusedElement as VisualElement,
                    Is.SameAs(rightView));

                // ... while closing the one that IS active still hands focus to
                // the pane on its left. ZepState has already moved
                // ActivePane to that successor by the time the change is
                // dispatched, so only the pre-change mirror can tell the two
                // cases apart.
                workspace.CloseAuxiliary(right);
                yield return null;

                Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
                Assert.That(
                    workspace.panel?.focusController?.focusedElement as VisualElement,
                    Is.SameAs(primaryView));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator OpeningSettlesToStableHierarchyAndClearsTransientGeometry()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(800f);
                var auxiliary = workspace.OpenAuxiliary(
                    ZepPaneSource.FixedTag("blue")).NewPane;

                Assert.That(
                    workspace.GetPaneLifecycle(auxiliary),
                    Is.EqualTo(ZepSplitPaneLifecycle.Opening));
                Assert.That(workspace.HasPendingMotion, Is.True);

                yield return WaitForEditorSeconds(0.4d);

                Assert.That(
                    workspace.GetPaneLifecycle(auxiliary),
                    Is.EqualTo(ZepSplitPaneLifecycle.Open));
                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(WithClass(workspace, ZepUiClasses.SplitPane).Count,
                    Is.EqualTo(2));
                Assert.That(workspace.HasPendingMotion, Is.False);
                AssertTransientMotionCleared(workspace);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator CloseRetainsOnlyRetiringVisualUntilGenerationCheckedSettlement()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.Show();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Focus();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(800f);
                workspace.CloseAuxiliary(auxiliary);

                Assert.That(state.AuxiliaryPanes, Is.Empty);
                Assert.That(workspace.PaneCount, Is.EqualTo(1));
                Assert.That(workspace.RetiringPaneCount, Is.EqualTo(1));
                Assert.That(WithClass(workspace, ZepUiClasses.SplitPane).Count,
                    Is.EqualTo(2));
                Assert.That(
                    workspace.GetPaneLifecycle(auxiliary),
                    Is.EqualTo(ZepSplitPaneLifecycle.Closing));

                yield return WaitForEditorSeconds(0.35d, window);

                Assert.That(workspace.RetiringPaneCount, Is.Zero);
                Assert.That(WithClass(workspace, ZepUiClasses.SplitPane).Count,
                    Is.EqualTo(1));
                Assert.That(workspace.PrimaryPaneView, Is.Not.Null);
                Assert.That(workspace.HasPendingMotion, Is.False);
                AssertTransientMotionCleared(workspace);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator OpenCloseOpenCannotLoseNewPaneToStaleCloseCallback()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(800f);
                var first = workspace.OpenAuxiliary(
                    ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Art"))).NewPane;
                workspace.CloseAuxiliary(first);
                var second = workspace.OpenAuxiliary(
                    ZepPaneSource.FixedTag("blue")).NewPane;

                workspace.CompleteRemoval(first);
                yield return WaitForEditorSeconds(0.4d);

                Assert.That(state.AuxiliaryPanes.Single(), Is.SameAs(second));
                Assert.That(workspace.GetPaneView(second), Is.Not.Null);
                Assert.That(workspace.GetPaneView(first), Is.Null);
                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(workspace.RetiringPaneCount, Is.Zero);
                Assert.That(workspace.HasPendingMotion, Is.False);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ReplaceDuringEntranceAndCloseDuringReplaceSettleLatestState()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                workspace.ApplyResponsiveWidth(800f);
                workspace.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art")));
                var replacement = workspace.OpenAuxiliary(
                    ZepPaneSource.FixedTag("blue")).NewPane;

                Assert.That(
                    workspace.GetPaneLifecycle(replacement),
                    Is.EqualTo(ZepSplitPaneLifecycle.Replacing));
                Assert.That(workspace.RetiringPaneCount, Is.EqualTo(1));

                yield return WaitForEditorSeconds(0.55d);

                Assert.That(state.AuxiliaryPanes.Single(), Is.SameAs(replacement));
                Assert.That(
                    workspace.GetPaneLifecycle(replacement),
                    Is.EqualTo(ZepSplitPaneLifecycle.Open));
                Assert.That(workspace.Query<ZepPaneView>().ToList().Count,
                    Is.EqualTo(2));
                Assert.That(workspace.RetiringPaneCount, Is.Zero);

                var closingReplacement = workspace.OpenAuxiliary(
                    ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Audio"))).NewPane;
                workspace.CloseAuxiliary(closingReplacement);
                yield return WaitForEditorSeconds(0.35d);

                Assert.That(state.AuxiliaryPanes, Is.Empty);
                Assert.That(workspace.PaneCount, Is.EqualTo(1));
                Assert.That(workspace.RetiringPaneCount, Is.Zero);
                Assert.That(workspace.HasPendingMotion, Is.False);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator DetachSettlesOpeningReplacingAndClosingWithoutOrphans()
        {
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();

            try
            {
                var openingState = new ZepState();
                var openingWorkspace = CreateWorkspace(openingState);
                window.rootVisualElement.Add(openingWorkspace);
                yield return null;
                openingWorkspace.ApplyResponsiveWidth(800f);
                var openingPane = openingWorkspace.OpenAuxiliary(
                    ZepPaneSource.FixedTag("blue")).NewPane;
                openingWorkspace.RemoveFromHierarchy();

                Assert.That(openingWorkspace.HasPendingMotion, Is.False);
                Assert.That(openingWorkspace.RetiringPaneCount, Is.Zero);
                Assert.That(
                    openingWorkspace.GetPaneLifecycle(openingPane),
                    Is.EqualTo(ZepSplitPaneLifecycle.Open));
                Assert.That(openingPane.VisibleAssetsProvider, Is.Null);

                var replacingState = new ZepState();
                replacingState.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
                var replacingWorkspace = CreateWorkspace(replacingState);
                window.rootVisualElement.Add(replacingWorkspace);
                yield return null;
                replacingWorkspace.ApplyResponsiveWidth(800f);
                var replacement = replacingWorkspace.OpenAuxiliary(
                    ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Art"))).NewPane;
                replacingWorkspace.RemoveFromHierarchy();

                Assert.That(replacingWorkspace.HasPendingMotion, Is.False);
                Assert.That(replacingWorkspace.RetiringPaneCount, Is.Zero);
                Assert.That(replacingWorkspace.Query<ZepPaneView>().ToList().Count,
                    Is.EqualTo(2));
                Assert.That(replacement.VisibleAssetsProvider, Is.Null);

                var closingState = new ZepState();
                var closingPane = closingState.OpenAuxiliary(
                    ZepPaneSource.FixedTag("blue")).NewPane;
                var closingWorkspace = CreateWorkspace(closingState);
                window.rootVisualElement.Add(closingWorkspace);
                yield return null;
                closingWorkspace.ApplyResponsiveWidth(800f);
                closingWorkspace.CloseAuxiliary(closingPane);
                closingWorkspace.RemoveFromHierarchy();

                Assert.That(closingWorkspace.HasPendingMotion, Is.False);
                Assert.That(closingWorkspace.RetiringPaneCount, Is.Zero);
                Assert.That(WithClass(
                    closingWorkspace,
                    ZepUiClasses.SplitPane).Count, Is.EqualTo(1));
                Assert.That(closingPane.VisibleAssetsProvider, Is.Null);
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void DividerDragUsesHostNoTransitionStateAndControlsRemainAccessible()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(800f);
            var divider = workspace.Q<ZepPaneSplitterWidget>();
            var close = WithClass(
                workspace,
                ZepUiClasses.SplitPaneCloseButton).Single();

            Invoke(workspace, "OnDividerDragStarted", divider);
            Assert.That(
                workspace.ClassListContains(
                    ZepUiClasses.SplitWorkspaceResizing),
                Is.True);
            Invoke(workspace, "OnDividerDragEnded", divider);

            Assert.That(
                workspace.ClassListContains(
                    ZepUiClasses.SplitWorkspaceResizing),
                Is.False);
            Assert.That(divider.focusable, Is.True);
            Assert.That(divider.tooltip, Is.EqualTo("Resize panes"));
            Assert.That(close.focusable, Is.True);
            Assert.That(close.tooltip, Does.StartWith("Close "));
        }

        [UnityTest]
        public IEnumerator CompactChromeHidesTheBreadcrumbDividerAndModeHidesTheGroup()
        {
            var state = new ZepState();
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            Assert.That(styleSheet, Is.Not.Null, "the single structural sheet must be loadable");
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.styleSheets.Add(styleSheet);
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;

                var assetsArea = workspace.PrimaryPaneView.AssetsArea;
                var divider = WithClass(
                    assetsArea,
                    ZepUiClasses.FloatingBreadcrumbDivider).Single();
                assetsArea.ApplyResponsiveWidth(800f);
                yield return null;

                Assert.That(divider.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));

                // The regression: an inline display write in ApplyMountVisibility
                // out-ranks every rule, so the compact chrome could never hide it.
                assetsArea.ApplyResponsiveWidth(
                    AssetsAreaConstants.SplitPaneChromeCompactBreakpoint);
                yield return null;

                Assert.That(
                    divider.resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.None),
                    "compact chrome must still hide the breadcrumb divider in Browser mode");

                assetsArea.ApplyResponsiveWidth(800f);
                assetsArea.SetActiveMode(AssetsPaneMode.Tags);
                yield return null;

                Assert.That(
                    divider.resolvedStyle.display,
                    Is.EqualTo(DisplayStyle.None),
                    "Tags has no breadcrumb");

                assetsArea.SetActiveMode(AssetsPaneMode.Browser);
                yield return null;

                Assert.That(divider.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void NewSplitClassesHaveLiveConsumersAndClassDrivenUssRules()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art")));
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(404f);
            var ussPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            var hostPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/AssetsArea/ZepSplitWorkspaceView.cs");
            var dropPresentationPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/AssetsArea/SplitDropPresentation.cs");
            var uss = File.ReadAllText(ussPath);
            // The split workspace is written across two files: the composition
            // and the drop presentation it owns.
            var host = File.ReadAllText(hostPath)
                + File.ReadAllText(dropPresentationPath);

            var liveClasses = new[]
            {
                ZepUiClasses.SplitWorkspace,
                ZepUiClasses.SplitWorkspaceMultiple,
                ZepUiClasses.SplitPaneContent,
                ZepUiClasses.SplitPane,
                ZepUiClasses.SplitPaneActive,
                ZepUiClasses.SplitPaneNavigationBar,
                ZepUiClasses.SplitPaneCloseButton,
                ZepUiClasses.AssetPaneChromeCompact,
                ZepUiClasses.AssetPaneChromeTight
            };

            foreach (var className in liveClasses)
            {
                StringAssert.Contains("." + className, uss, className);
                var consumed = workspace.ClassListContains(className)
                    || workspace.Query<VisualElement>()
                        .ToList()
                        .Any(element => element.ClassListContains(className));
                Assert.That(consumed, Is.True, className);
            }

            StringAssert.DoesNotContain("style.minWidth", host);
            StringAssert.DoesNotContain("style.flexBasis", host);
            StringAssert.DoesNotContain("styleSheets.Add", host);
            StringAssert.Contains("style.flexGrow", host);
            StringAssert.Contains("TransitionEndEvent", host);
            StringAssert.Contains("AffectsProperty(WidthStyleProperty)", host);
            StringAssert.Contains("UsageHints.DynamicTransform", host);
        }

        [Test]
        public void MotionClassesAndTimingsAreClassDrivenAndUseNoColorTransition()
        {
            var uss = File.ReadAllText(Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));
            var host = File.ReadAllText(Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/AssetsArea/ZepSplitWorkspaceView.cs"))
                + File.ReadAllText(Path.GetFullPath(
                    "Packages/com.van-unity.zep/Editor/UI/AssetsArea/SplitDropPresentation.cs"));
            var motionClasses = new[]
            {
                (ZepUiClasses.SplitWorkspaceOpening,
                    nameof(ZepUiClasses.SplitWorkspaceOpening)),
                (ZepUiClasses.SplitWorkspaceClosing,
                    nameof(ZepUiClasses.SplitWorkspaceClosing)),
                (ZepUiClasses.SplitWorkspaceReplacing,
                    nameof(ZepUiClasses.SplitWorkspaceReplacing)),
                (ZepUiClasses.SplitWorkspaceResizing,
                    nameof(ZepUiClasses.SplitWorkspaceResizing)),
                (ZepUiClasses.SplitPaneWidthMotion,
                    nameof(ZepUiClasses.SplitPaneWidthMotion)),
                (ZepUiClasses.SplitPaneContentEntering,
                    nameof(ZepUiClasses.SplitPaneContentEntering)),
                (ZepUiClasses.SplitPaneContentPresented,
                    nameof(ZepUiClasses.SplitPaneContentPresented)),
                (ZepUiClasses.SplitPaneContentClosing,
                    nameof(ZepUiClasses.SplitPaneContentClosing)),
                (ZepUiClasses.SplitPaneContentRetiring,
                    nameof(ZepUiClasses.SplitPaneContentRetiring)),
                (ZepUiClasses.SplitPaneContentRetiringHidden,
                    nameof(ZepUiClasses.SplitPaneContentRetiringHidden)),
                (ZepUiClasses.SplitDropPreviewPresented,
                    nameof(ZepUiClasses.SplitDropPreviewPresented))
            };

            foreach (var motionClass in motionClasses)
            {
                StringAssert.Contains("." + motionClass.Item1, uss, motionClass.Item1);
                StringAssert.Contains(
                    "ZepUiClasses." + motionClass.Item2,
                    host,
                    motionClass.Item1);
            }

            StringAssert.Contains("transition-duration: 0.13s;", uss);
            StringAssert.Contains("transition-property: none;", uss);
        }

        // Every shipped call to InsertSlot appends, because MaxSplitViews is 1.
        // The middle-insert branch is therefore only reachable from here — and
        // it is the branch that decides which panes each divider resizes, so it
        // gets a test before the cap ever grows.
        [Test]
        public void InsertSlot_InTheMiddle_KeepsDividersPairedWithTheirNeighbours()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var workspace = CreateWorkspace(state);
            workspace.ApplyResponsiveWidth(1200f);
            // Stand in for a raised MaxSplitViews: the workspace already built
            // its two slots, and the pane view refuses a pane the state does not
            // own, so the third pane joins the collection the same way a larger
            // cap would let OpenAuxiliary add it.
            var inserted = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            AuxiliaryPaneList(state).Add(inserted);

            Invoke(workspace, "InsertSlot", 1, inserted, null, null);

            Assert.That(workspace.PaneCount, Is.EqualTo(3));
            Assert.That(workspace.DividerCount, Is.EqualTo(2));
            Assert.That(
                SplitRowKinds(workspace),
                Is.EqualTo(new[] { "pane", "divider", "pane", "divider", "pane" }),
                "a middle insert must leave one divider between each adjacent pane pair");

            var dividers = workspace.Query<ZepPaneSplitterWidget>().ToList();
            AssertDividerResizesOnly(workspace, dividers[0], leftPaneIndex: 0);
            AssertDividerResizesOnly(workspace, dividers[1], leftPaneIndex: 1);
        }

        // ZepWindow.CaptureSplitSnapshot serializes the auxiliaries,
        // the active auxiliary index and the pane weights — nothing about where
        // the primary is browsing — so primary navigation must not trigger a
        // snapshot save.
        [Test]
        public void PrimaryNavigation_DoesNotSnapshotTheArrangement()
        {
            var state = new ZepState();
            state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));
            var workspace = CreateWorkspace(state);
            var raises = 0;
            workspace.ArrangementChanged += () => raises++;

            var navigated = state.PrimaryPane.TrySetCurrentFolderPath(
                AssetPath.From("Assets/Art"));

            Assert.That(navigated, Is.True, "the primary must really have moved");
            Assert.That(
                state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(raises, Is.Zero);
        }

        [Test]
        public void AuxiliaryNavigation_SnapshotsTheArrangementOnce()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var workspace = CreateWorkspace(state);
            var raises = 0;
            workspace.ArrangementChanged += () => raises++;

            var navigated = auxiliary.TrySetCurrentFolderPath(
                AssetPath.From("Assets/Art/UI"));

            Assert.That(navigated, Is.True);
            Assert.That(raises, Is.EqualTo(1));
        }

        // A slot's chrome follows its pane SOURCE, never where that pane is
        // browsing, so navigating inside an auxiliary must repaint no chrome
        // at all — neither its own slot nor the primary's.
        [Test]
        public void AuxiliaryNavigation_LeavesTheCloseTooltipAloneAndWritesNoChrome()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var workspace = CreateWorkspace(state);
            var tooltipBefore = CloseControl(workspace).tooltip;
            ZepSplitWorkspaceView.SlotChromeWritesForTests = 0;

            var navigated = auxiliary.TrySetCurrentFolderPath(
                AssetPath.From("Assets/Art/UI"));

            Assert.That(navigated, Is.True, "the auxiliary must really have moved");
            Assert.That(tooltipBefore, Is.EqualTo("Close Folder \u00b7 Art split"));
            Assert.That(CloseControl(workspace).tooltip, Is.EqualTo(tooltipBefore));
            Assert.That(
                ZepSplitWorkspaceView.SlotChromeWritesForTests,
                Is.Zero);
        }

        // The shell pushes RefreshChrome on every global publication, and a tag
        // rename or recolour is exactly the one that must reach a tag slot:
        // only that slot repaints, and the crumb — the pane's only identity —
        // repaints with it, because the pane's own PaneChanged subscription
        // never sees a global change.
        [Test]
        public void TagSplit_AfterItsLastAssignmentIsRemoved_KeepsItsDefinitionIdentityAndFollowsRenames()
        {
            var path = AssetPath.From("Assets/Icon.png");
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("icon-guid", path.Value, "Icon", "Texture2D") },
                new[] { "Assets" },
                new[]
                {
                    new TagDefinitionRecord
                    {
                        Id = "blue", Name = "Blue", Color = ZepTagColor.Blue,
                        IsFavorite = false
                    }
                });
            Assert.That(rig.Controller.AddTag(new[] { path }, "blue"), Is.True);
            var shell = rig.BuildShell();
            try
            {
                var workspace = shell.Q<ZepSplitWorkspaceView>();
                workspace.ApplyResponsiveWidth(800f);
                var pane = workspace.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
                Assert.That(CloseControl(workspace).tooltip, Is.EqualTo("Close Tag \u00b7 Blue split"));

                Assert.That(rig.Controller.RemoveTag(new[] { path }, "blue"), Is.True);

                Assert.That(rig.State.TagFilterOptions.Any(option => option.TagId == "blue"), Is.False,
                    "An unused nonfavorite tag disappears from the sidebar, but its split stays live.");
                Assert.That(rig.State.IsLivePane(pane), Is.True);
                Assert.That(CloseControl(workspace).tooltip, Is.EqualTo("Close Tag \u00b7 Blue split"));

                Assert.That(rig.Controller.UpdateTagDefinition(
                    new ZepTag("blue", "Azure", ZepTagColor.Red)), Is.True);

                Assert.That(CloseControl(workspace).tooltip, Is.EqualTo("Close Tag \u00b7 Azure split"));
                var breadcrumb = workspace.GetPaneView(pane)
                    .Q<BreadcrumbWidget>("zep-split-pane-breadcrumb");
                Assert.That(breadcrumb.Q<Button>("zep-breadcrumb-leading-segment").text,
                    Is.EqualTo("Azure"));
                Assert.That(breadcrumb.Q<VisualElement>(className: ZepUiClasses.TagColorDot)
                    .style.backgroundColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Red)));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void TagRenameOrRecolourBehindAnAuxiliarySlot_WritesThatSlotChromeOnceAndRepaintsItsCrumb()
        {
            var state = new ZepState();
            state.SetTagFilterOptions(new[] { TagOption("blue", "Blue") });
            var auxiliary = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var workspace = CreateWorkspace(state);
            ZepSplitWorkspaceView.SlotChromeWritesForTests = 0;

            workspace.RefreshChrome();

            Assert.That(
                ZepSplitWorkspaceView.SlotChromeWritesForTests,
                Is.Zero,
                "a repeated push with nothing changed must write nothing");

            state.SetTagFilterOptions(new[] { TagOption("blue", "Azure") });
            workspace.RefreshChrome();

            Assert.That(
                ZepSplitWorkspaceView.SlotChromeWritesForTests,
                Is.EqualTo(1));
            Assert.That(
                CloseControl(workspace).tooltip,
                Is.EqualTo("Close Tag \u00b7 Azure split"));
            var breadcrumb = workspace.GetPaneView(auxiliary)
                .Q<BreadcrumbWidget>("zep-split-pane-breadcrumb");
            Assert.That(
                breadcrumb.Q<Button>("zep-breadcrumb-leading-segment").text,
                Is.EqualTo("Azure"));

            state.SetTagFilterOptions(new[]
            {
                new ZepTagFilterOption(
                    new ZepTag("blue", "Azure", ZepTagColor.Red),
                    0)
            });
            workspace.RefreshChrome();

            Assert.That(
                ZepSplitWorkspaceView.SlotChromeWritesForTests,
                Is.EqualTo(2));
            var dot = breadcrumb.Q<VisualElement>(className: ZepUiClasses.TagColorDot);
            Assert.That(
                dot.style.backgroundColor.value,
                Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Red)));
        }

        [UnityTest]
        public IEnumerator GridSizeTicksWithinOneFrame_SnapshotTheArrangementOnce()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                var raises = 0;
                workspace.ArrangementChanged += () => raises++;
                var assetsArea = workspace.GetPaneView(auxiliary).AssetsArea;

                for (var i = 0; i < 10; i++)
                {
                    RaiseGridItemSizeChanged(assetsArea, 96 + i);
                }

                Assert.That(
                    raises,
                    Is.Zero,
                    "a slider drag must not snapshot the arrangement per tick");

                // Scheduled items tick on repaint, which batchmode does not do
                // on its own, so drive repaints until the coalesced raise lands.
                var deadline = EditorApplication.timeSinceStartup + 5d;
                while (raises == 0 && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                yield return null;
                yield return null;

                Assert.That(
                    raises,
                    Is.EqualTo(1),
                    "the coalesced burst must snapshot the arrangement exactly once");
            }
            finally
            {
                window.Close();
            }
        }

        // Scheduled items do not tick while detached, so the pending raise is
        // flushed on the way out — the last size the user picked still reaches
        // the snapshot.
        [UnityTest]
        public IEnumerator PendingGridSizeTicks_SnapshotTheArrangementOnDetach()
        {
            var state = new ZepState();
            var auxiliary = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var workspace = CreateWorkspace(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 600f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                var raises = 0;
                workspace.ArrangementChanged += () => raises++;
                var assetsArea = workspace.GetPaneView(auxiliary).AssetsArea;

                RaiseGridItemSizeChanged(assetsArea, 128);
                Assert.That(raises, Is.Zero);

                window.rootVisualElement.Remove(workspace);

                Assert.That(
                    raises,
                    Is.EqualTo(1),
                    "a pending presentation snapshot must survive detachment");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator NativeRelay_PreservesSplitAndFolderMovePriorityInsidePointerDispatch()
        {
            var state = new ZepState();
            state.SetTagFilterOptions(new[] { TagOption("blue", "Blue") });
            var shell = new ZepShellView(state);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1200f, 600f);
            window.Show();
            window.position = new Rect(100f, 100f, 1200f, 600f);
            window.rootVisualElement.Add(shell);
            try
            {
                yield return null;
                yield return null;
                var workspace = shell.Q<ZepSplitWorkspaceView>();
                var browser = workspace.PrimaryPaneView.AssetsArea.BrowserFeature;
                var surface = browser.Q<VisualElement>("zep-browser-surface");
                Assert.That(surface.worldBound.width, Is.GreaterThan(500f));
                IReadOnlyList<string> moved = null;
                browser.CurrentFolderDropReceived += paths => moved = paths;

                var payload = new ZepSplitDragPayload(
                    ZepPaneSource.FixedTag("blue"), "Blue");
                ZepSplitDragAndDrop.Prepare(payload);
                RelayNativeContent(window, surface.worldBound.center, false);
                Assert.That(workspace.IsSplitDropPreviewVisible, Is.True);
                Assert.That(DragAndDrop.paths, Is.Empty);
                Assert.That(DragAndDrop.objectReferences, Is.Empty);

                // The real relay runs inside the captured pointer-up callback.
                // Clear immediately afterward must not erase a queued drop.
                var splitCompletedInsidePointerCallback = false;
                window.rootVisualElement.RegisterCallback<PointerUpEvent>(evt =>
                {
                    RelayNativeContent(window, surface.worldBound.center, true);
                    splitCompletedInsidePointerCallback = state.AuxiliaryPanes.Count == 1;
                    ZepWindowDocking.Clear();
                    ZepSplitDragAndDrop.Clear();
                    evt.StopImmediatePropagation();
                }, TrickleDown.TrickleDown);
                using (var evt = PointerUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp,
                    button = 0,
                    mousePosition = surface.worldBound.center
                }))
                {
                    evt.target = window.rootVisualElement;
                    window.rootVisualElement.SendEvent(evt);
                }
                yield return null;
                Assert.That(splitCompletedInsidePointerCallback, Is.True);
                Assert.That(state.AuxiliaryPanes.Single().Source, Is.EqualTo(payload.Source));
                Assert.That(moved, Is.Null, "a sidebar source must never reach asset movement");

                DragAndDrop.PrepareStartDrag();
                DragAndDrop.paths = new[] { "Assets/DraggedFolder" };
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
                RelayNativeContent(window, surface.worldBound.center, false);
                Assert.That(workspace.IsSplitDropPreviewVisible, Is.False);
                RelayNativeContent(window, surface.worldBound.center, true);
                Assert.That(moved, Is.EqualTo(new[] { "Assets/DraggedFolder" }));
                Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(1),
                    "a grid/list folder remains an asset move inside the workspace");
            }
            finally
            {
                ZepWindowDocking.Clear();
                ZepSplitDragAndDrop.Clear();
                DragAndDrop.paths = Array.Empty<string>();
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                shell.Teardown();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator NativeRelay_LeavingAssetsClearsHighlightAndStopsEdgeScrolling()
        {
            var state = new ZepState();
            var assets = new List<ZepAssetEntry>
            {
                new ZepAssetEntry("Assets", "Assets", "Folder")
            };
            for (var i = 0; i < 100; i++)
                assets.Add(new ZepAssetEntry($"Item {i:000}", $"Assets/Item{i:000}.asset", "ScriptableObject"));
            var shell = new ZepShellView(state);
            var browser = shell.Q<ZepSplitWorkspaceView>().PrimaryPaneView.AssetsArea.BrowserFeature;
            browser.SetAssets(assets);
            var window = ScriptableObject.CreateInstance<SplitWorkspaceTestWindow>();
            window.position = new Rect(100f, 100f, 1000f, 500f);
            window.Show();
            window.rootVisualElement.Add(shell);
            try
            {
                yield return null;
                yield return null;
                var folderLabel = browser.Query<Label>().ToList().First(label => label.text == "Assets");
                var scroll = browser.Q<ListView>().Q<ScrollView>();
                Assert.That(scroll.verticalScroller.highValue, Is.GreaterThan(100f));
                ZepSplitDragAndDrop.Clear();
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.paths = new[] { "Assets/Nested/DraggedFolder" };
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();

                RelayNativeContent(window, folderLabel.worldBound.center, false);
                Assert.That(WithClass(browser, ZepUiClasses.DropTarget), Is.Not.Empty);
                ClearNativeContentHover();
                yield return null;
                Assert.That(WithClass(browser, ZepUiClasses.DropTarget), Is.Empty,
                    "leaving must reach the owning browser, not only the picked row label");

                var edge = new Vector2(scroll.worldBound.center.x, scroll.worldBound.yMax - 2f);
                RelayNativeContent(window, edge, false);
                var deadline = EditorApplication.timeSinceStartup + 2d;
                while (scroll.scrollOffset.y <= 0f && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(scroll.scrollOffset.y, Is.GreaterThan(0f), "the hover must first start edge scrolling");
                ClearNativeContentHover();
                yield return null;
                var stoppedOffset = scroll.scrollOffset.y;
                deadline = EditorApplication.timeSinceStartup + 0.1d;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(scroll.scrollOffset.y, Is.EqualTo(stoppedOffset).Within(0.01f),
                    "the recurring scroll tick must stop once the native gesture leaves the panel");
            }
            finally
            {
                ZepWindowDocking.Clear();
                ZepSplitDragAndDrop.Clear();
                DragAndDrop.paths = Array.Empty<string>();
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                shell.Teardown();
                window.Close();
            }
        }

        private static void RelayNativeContent(EditorWindow window, Vector2 point, bool perform)
        {
            typeof(ZepWindowDocking).GetMethod("RelayContent", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { window, point, perform });
        }

        private static void ClearNativeContentHover()
        {
            typeof(ZepWindowDocking).GetMethod("ClearContentHover", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
        }

        // Drag the divider and check which weights moved: divider k must resize
        // panes k and k+1 and leave every other pane alone.
        private static void AssertDividerResizesOnly(
            ZepSplitWorkspaceView workspace,
            ZepPaneSplitterWidget divider,
            int leftPaneIndex)
        {
            var before = workspace.PaneWeights.ToArray();

            Invoke(workspace, "OnDividerKeyboardResizeRequested", divider, 60f);

            var after = workspace.PaneWeights.ToArray();
            for (var i = 0; i < before.Length; i++)
            {
                var isPaired = i == leftPaneIndex || i == leftPaneIndex + 1;
                Assert.That(
                    Mathf.Abs(after[i] - before[i]) > 0.0001f,
                    Is.EqualTo(isPaired),
                    $"divider {leftPaneIndex} moved pane {i} when it should"
                    + (isPaired ? " have" : " not have"));
            }
        }

        private static List<ZepPaneState> AuxiliaryPaneList(ZepState state)
        {
            var field = typeof(ZepState).GetField(
                "_auxiliaryPanes",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (List<ZepPaneState>)field.GetValue(state);
        }

        private static List<string> SplitRowKinds(ZepSplitWorkspaceView workspace)
        {
            var kinds = new List<string>();
            foreach (var child in workspace.Children())
            {
                if (child is ZepPaneSplitterWidget)
                {
                    kinds.Add("divider");
                }
                else if (child.ClassListContains(ZepUiClasses.SplitPane))
                {
                    kinds.Add("pane");
                }
            }

            return kinds;
        }

        private static ZepSplitWorkspaceView CreateWorkspace(
            ZepState state,
            Action<string> feedback = null,
            AssetDropOperations dropOps = null)
        {
            return new ZepSplitWorkspaceView(
                state,
                new UnityZepIconRepository(),
                showFeedback: feedback,
                dropOps: dropOps);
        }

        private static Button CloseControl(ZepSplitWorkspaceView workspace)
        {
            return (Button)WithClass(
                workspace,
                ZepUiClasses.SplitPaneCloseButton).Single();
        }

        private static ZepTagFilterOption TagOption(string id, string name)
        {
            return new ZepTagFilterOption(
                new ZepTag(id, name, ZepTagColor.Blue),
                0);
        }

        private static List<VisualElement> WithClass(
            VisualElement root,
            string className)
        {
            return root.Query<VisualElement>()
                .ToList()
                .Where(element => element.ClassListContains(className))
                .ToList();
        }

        private static IEnumerator WaitForEditorSeconds(
            double seconds,
            EditorWindow window = null)
        {
            var end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
            {
                window?.Repaint();
                yield return null;
            }
        }

        private static void AssertTransientMotionCleared(
            ZepSplitWorkspaceView workspace)
        {
            var wrappers = WithClass(workspace, ZepUiClasses.SplitPane);
            for (var i = 0; i < wrappers.Count; i++)
            {
                Assert.That(wrappers[i].style.width.keyword, Is.EqualTo(StyleKeyword.Null));
                Assert.That(
                    wrappers[i].ClassListContains(
                        ZepUiClasses.SplitPaneWidthMotion),
                    Is.False);
            }

            var contents = WithClass(
                workspace,
                ZepUiClasses.SplitPaneContent);
            for (var i = 0; i < contents.Count; i++)
            {
                Assert.That(contents[i].style.width.keyword, Is.EqualTo(StyleKeyword.Null));
                Assert.That(contents[i].style.opacity.keyword, Is.EqualTo(StyleKeyword.Null));
                Assert.That(contents[i].style.translate.keyword, Is.EqualTo(StyleKeyword.Null));
                Assert.That(contents[i].style.scale.keyword, Is.EqualTo(StyleKeyword.Null));
            }
        }

        private static void RaiseAssetsAreaEvent(
            AssetsAreaController assetsArea,
            string eventName)
        {
            var field = typeof(AssetsAreaController).GetField(
                eventName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action)field.GetValue(assetsArea))?.Invoke();
        }

        private static void RaiseGridItemSizeChanged(
            AssetsAreaController assetsArea,
            int size)
        {
            var field = typeof(AssetsAreaController).GetField(
                "GridItemSizeChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action<int>)field.GetValue(assetsArea))?.Invoke(size);
        }

        private static void RaiseCurrentFolderDrop(
            AssetsBrowserFeature browser,
            string path)
        {
            var field = typeof(AssetsBrowserFeature).GetField(
                "CurrentFolderDropReceived",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action<IReadOnlyList<string>>)field.GetValue(browser))?.Invoke(
                new[] { path });
        }

        private static void Invoke(object target, string methodName, params object[] arguments)
        {
            var argumentCount = arguments?.Length ?? 0;
            var method = target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate =>
                    candidate.Name == methodName
                    && candidate.GetParameters().Length == argumentCount);
            method.Invoke(target, arguments);
        }

        private sealed class SplitWorkspaceTestWindow : EditorWindow
        {
        }
    }
}
