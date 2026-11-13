using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.TestInfrastructure;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.MaterialThumbnails;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using Zep.Editor.UI.Sidebar;
using Zep.Editor.UI.TopBar;
using Zep.Core.Domain;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepShellViewTests
    {
        [Test]
        public void ClosingSiblingWindow_PreservesTheMainShellsLastPane()
        {
            var state = new ZepState();
            var shell = new ZepShellView(state);
            try
            {
                var split = state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
                var sibling = state.OpenWindowPane(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Audio")));

                state.CloseWindowPane(sibling);
                state.PrimaryPane.TrySetFolderSelection("Assets/Textures");

                Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
                Assert.That(shell.LocalActivePane, Is.SameAs(split));
                shell.RestoreLocalPaneFocus();
                Assert.That(state.ActivePane, Is.SameAs(split));

                state.ActivatePane(state.PrimaryPane);
                shell.RestoreLocalPaneFocus();
                Assert.That(shell.LocalActivePane, Is.SameAs(state.PrimaryPane));
                Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void ReturningFromSiblingWindow_RestoresTheMainShellsLastPane()
        {
            var state = new ZepState();
            var shell = new ZepShellView(state);
            try
            {
                var split = state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
                shell.RestoreLocalPaneFocus();
                var sibling = state.OpenWindowPane(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Audio")));
                sibling.TrySetFolderSelection("Assets/Audio/Music");

                shell.RestoreLocalPaneFocus();

                Assert.That(state.ActivePane, Is.SameAs(split));
                Assert.That(sibling.CurrentFolderPath.Value, Is.EqualTo("Assets/Audio/Music"));

                state.CloseAuxiliary(split);
                state.ActivatePane(sibling);
                shell.RestoreLocalPaneFocus();
                Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void MainChrome_IgnoresAWindowPaneThatIsGloballyActive()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages", "Packages/local.pkg" },
                new[] { new TagDefinitionRecord { Id = "blue", Name = "Blue" } });
            rig.Gateway.CanWriteOverrideByPath["Packages/local.pkg"] = false;
            var shell = rig.BuildShell();
            var plusButton = FindPlusButton(shell);
            Assert.That(plusButton.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(plusButton.enabledSelf, Is.True);

            // A docked tag window takes global activation; this window still
            // browses its own folder, so its + stays.
            var tagWindow = rig.State.OpenWindowPane(ZepPaneSource.FixedTag("blue"));
            Assert.That(rig.State.ActivePane, Is.SameAs(tagWindow));
            Assert.That(tagWindow.ActiveMode, Is.Not.EqualTo(AssetsPaneMode.Browser),
                "fixture: a tag window opens on its tag list");
            Assert.That(plusButton.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(plusButton.enabledSelf, Is.True);

            var readOnlyWindow = rig.State.OpenWindowPane(ZepPaneSource.RootedFolder(
                AssetPath.From("Packages/local.pkg")));
            Assert.That(rig.State.ActivePane, Is.SameAs(readOnlyWindow));
            Assert.That(plusButton.enabledSelf, Is.True,
                "another window's read-only folder is not this window's create target");

            var refreshesBefore = GetShellCounter(shell, "__pp_refreshSeq");
            readOnlyWindow.SetSearchText("x");
            Assert.That(GetShellCounter(shell, "__pp_refreshSeq") - refreshesBefore, Is.Zero,
                "browsing in a docked window must not republish this window's chrome");
        }

        private const string ThemePreferenceKey = "Zep.Editor.UI.ThemeIsLight";
        private const string AssetViewModePreferenceKey = "Zep.Editor.UI.AssetViewMode";
        private const string FolderPaneWidthPreferenceKey = "Zep.Editor.UI.FolderPaneWidth";
        // FolderTreeWidget persists its sticky expansion state per root in
        // SessionState; snapshot/restore so shell construction and reveal in
        // tests can't contaminate the user's live sidebar (and vice versa).
        private const string AssetsTreeExpansionKey = "Zep.FolderTreeExpanded.Assets";
        private const string PackagesTreeExpansionKey = "Zep.FolderTreeExpanded.Packages";
        private string _previousAssetViewModePreference;
        private bool _hadFolderPaneWidthPreference;
        private float _previousFolderPaneWidthPreference;
        private string _previousAssetsTreeExpansion;
        private string _previousPackagesTreeExpansion;
        private string _previousPendingCreateSelection;
        private readonly List<EditorWindow> _searchHosts = new List<EditorWindow>();
        private int? _previousSearchShortcut;

        [SetUp]
        public void SetUp()
        {
            _previousSearchShortcut = EditorPrefs.HasKey(ZepSearchShortcut.PreferenceKey)
                ? EditorPrefs.GetInt(ZepSearchShortcut.PreferenceKey) : (int?)null;
            EditorPrefs.DeleteKey(ZepSearchShortcut.PreferenceKey);
            _previousAssetViewModePreference = EditorPrefs.GetString(AssetViewModePreferenceKey, string.Empty);
            _hadFolderPaneWidthPreference = EditorPrefs.HasKey(FolderPaneWidthPreferenceKey);
            _previousFolderPaneWidthPreference = EditorPrefs.GetFloat(FolderPaneWidthPreferenceKey, 270f);
            _previousAssetsTreeExpansion = SessionState.GetString(AssetsTreeExpansionKey, null);
            _previousPackagesTreeExpansion = SessionState.GetString(PackagesTreeExpansionKey, null);
            _previousPendingCreateSelection = SessionState.GetString(
                ZepShellView.PendingCreateSelectionSessionKey,
                null);
            EditorPrefs.SetString(AssetViewModePreferenceKey, "List");
            EditorPrefs.DeleteKey(FolderPaneWidthPreferenceKey);
            SessionState.EraseString(AssetsTreeExpansionKey);
            SessionState.EraseString(PackagesTreeExpansionKey);
            SessionState.EraseString(ZepShellView.PendingCreateSelectionSessionKey);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in _searchHosts)
                if (host != null) host.Close();
            _searchHosts.Clear();
            if (_previousSearchShortcut.HasValue)
                EditorPrefs.SetInt(ZepSearchShortcut.PreferenceKey, _previousSearchShortcut.Value);
            else EditorPrefs.DeleteKey(ZepSearchShortcut.PreferenceKey);
            if (string.IsNullOrEmpty(_previousAssetViewModePreference))
            {
                EditorPrefs.DeleteKey(AssetViewModePreferenceKey);
            }
            else
            {
                EditorPrefs.SetString(AssetViewModePreferenceKey, _previousAssetViewModePreference);
            }

            if (_hadFolderPaneWidthPreference)
            {
                EditorPrefs.SetFloat(FolderPaneWidthPreferenceKey, _previousFolderPaneWidthPreference);
            }
            else
            {
                EditorPrefs.DeleteKey(FolderPaneWidthPreferenceKey);
            }

            if (_previousAssetsTreeExpansion != null)
            {
                SessionState.SetString(AssetsTreeExpansionKey, _previousAssetsTreeExpansion);
            }
            else
            {
                SessionState.EraseString(AssetsTreeExpansionKey);
            }

            if (_previousPackagesTreeExpansion != null)
            {
                SessionState.SetString(PackagesTreeExpansionKey, _previousPackagesTreeExpansion);
            }
            else
            {
                SessionState.EraseString(PackagesTreeExpansionKey);
            }

            if (_previousPendingCreateSelection != null)
            {
                SessionState.SetString(
                    ZepShellView.PendingCreateSelectionSessionKey,
                    _previousPendingCreateSelection);
            }
            else
            {
                SessionState.EraseString(ZepShellView.PendingCreateSelectionSessionKey);
            }
        }

        [UnityTest]
        public IEnumerator CreatedScript_KeepsItsSelectionHandoffForTheDomainReload()
        {
            const string scriptPath =
                "Packages/com.van-unity.zep/Editor/UI/ZepShellView.cs";
            Assert.That(AssetDatabase.LoadMainAssetAtPath(scriptPath), Is.Not.Null);

            var state = new ZepState();
            var shell = new ZepShellView(state);

            try
            {
                InvokeShellMethod(
                    shell,
                    "OnCreatedAssetLanded",
                    state.PrimaryPane,
                    scriptPath);

                // The pre-fix path scheduled the ordinary reveal callback for scripts;
                // that callback ran here and erased the reload handoff before the
                // compilation reload could consume it.
                yield return null;

                Assert.That(
                    SessionState.GetString(
                        ZepShellView.PendingCreateSelectionSessionKey,
                        string.Empty),
                    Is.EqualTo(scriptPath));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void Constructor_UsesStoredFolderPaneWidthPreference()
        {
            EditorPrefs.SetFloat(FolderPaneWidthPreferenceKey, 348f);

            var shell = new ZepShellView(ZepPreviewSeed.Build());
            var folderPane = shell.Q<VisualElement>("zep-folder-pane");

            Assert.That(folderPane, Is.Not.Null);
            Assert.That(folderPane.style.width.value.value, Is.EqualTo(348f).Within(0.5f));
        }

        [UnityTest]
        public IEnumerator InitialScanOverlay_AnimatesOnlyWhileTheIndexScanIsRunning()
        {
            var state = ZepPreviewSeed.Build();
            var shell = new ZepShellView(state);
            var window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            window.position = new Rect(40f, 40f, 640f, 480f);
            window.Show();

            try
            {
                window.rootVisualElement.Add(shell);
                var overlay = shell.Q<VisualElement>("zep-initial-scan-overlay");
                var file = shell.Q<VisualElement>("zep-initial-scan-file-0");
                var folder = shell.Q<VisualElement>("zep-initial-scan-folder");
                Assert.That(overlay, Is.Not.Null);
                Assert.That(file, Is.Not.Null);
                Assert.That(folder, Is.Not.Null);
                Assert.That(overlay.style.display.value, Is.EqualTo(DisplayStyle.None),
                    "opening with an existing index must not show the initial-scan overlay");
                yield return AssertInitialScanMotionStopped(window, file, folder, "before a scan");

                state.SetIsIndexScanInProgress(true);
                Assert.That(overlay.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                yield return WaitForInitialScanMotion(window, file);

                state.SetIsIndexScanInProgress(false);
                Assert.That(overlay.style.display.value, Is.EqualTo(DisplayStyle.None),
                    "scan completion must release the window immediately");
                yield return AssertInitialScanMotionStopped(window, file, folder, "after scan completion");
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator InitialScanOverlay_DetachPausesMotion_ReattachRespectsState_AndTeardownStopsIt()
        {
            var state = ZepPreviewSeed.Build();
            state.SetIsIndexScanInProgress(true);
            var shell = new ZepShellView(state);
            var window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            window.position = new Rect(40f, 40f, 640f, 480f);
            window.Show();

            try
            {
                window.rootVisualElement.Add(shell);
                var overlay = shell.Q<VisualElement>("zep-initial-scan-overlay");
                var file = shell.Q<VisualElement>("zep-initial-scan-file-0");
                var folder = shell.Q<VisualElement>("zep-initial-scan-folder");
                Assert.That(overlay, Is.Not.Null);
                Assert.That(file, Is.Not.Null);
                Assert.That(folder, Is.Not.Null);
                yield return WaitForInitialScanMotion(window, file);

                shell.RemoveFromHierarchy();
                Assert.That(shell.panel, Is.Null);
                yield return AssertInitialScanMotionStopped(window, file, folder, "while detached");

                window.rootVisualElement.Add(shell);
                Assert.That(overlay.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                yield return WaitForInitialScanMotion(window, file);

                shell.RemoveFromHierarchy();
                state.SetIsIndexScanInProgress(false);
                window.rootVisualElement.Add(shell);
                Assert.That(overlay.style.display.value, Is.EqualTo(DisplayStyle.None),
                    "reattaching after completion must not revive the scan animation");
                yield return AssertInitialScanMotionStopped(window, file, folder, "after reattaching a completed scan");

                state.SetIsIndexScanInProgress(true);
                yield return WaitForInitialScanMotion(window, file);
                shell.Teardown();
                yield return AssertInitialScanMotionStopped(window, file, folder, "after teardown while attached");

                state.SetIsIndexScanInProgress(false);
                state.SetIsIndexScanInProgress(true);
                yield return AssertInitialScanMotionStopped(window, file, folder, "after a later state refresh");
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        private static IEnumerator WaitForInitialScanMotion(EditorWindow window, VisualElement file)
        {
            var initialTranslation = file.style.translate;
            var deadline = EditorApplication.timeSinceStartup + 2.0;
            do
            {
                // Batchmode panels need the repaints that the visible editor
                // normally supplies before UI Toolkit schedules can advance.
                window.Repaint();
                yield return null;
            }
            while (file.style.translate.Equals(initialTranslation)
                && EditorApplication.timeSinceStartup < deadline);

            Assert.That(file.style.translate, Is.Not.EqualTo(initialTranslation),
                "the file must travel while a mounted shell is indexing");
        }

        private static IEnumerator AssertInitialScanMotionStopped(
            EditorWindow window,
            VisualElement file,
            VisualElement folder,
            string context)
        {
            var fileTranslation = file.style.translate;
            var fileOpacity = file.style.opacity;
            var folderTranslation = folder.style.translate;
            var deadline = EditorApplication.timeSinceStartup + 0.2;
            do
            {
                window.Repaint();
                yield return null;
                Assert.That(file.style.translate, Is.EqualTo(fileTranslation), context);
                Assert.That(file.style.opacity, Is.EqualTo(fileOpacity), context);
                Assert.That(folder.style.translate, Is.EqualTo(folderTranslation), context);
            }
            while (EditorApplication.timeSinceStartup < deadline);
        }

        // The entrance animation is an inline opacity/translate write, and an
        // inline write out-ranks every USS rule for good (styling-standards.md
        // §4). It may therefore only touch an element whose own class declares
        // the transition it rides and whose paint no state class re-drives. The
        // top bar qualifies; the status bar does NOT — it is built display:none
        // (so its entrance was invisible anyway) and fades its toast through
        // .zep-status-visible, which a stamped inline opacity kills
        // for the window's lifetime. The content area declares no transition at
        // all, so its leg only blanked the pane for a frame.
        [Test]
        public void Entrance_AnimatesOnlyTheTopBar_AndLeavesTheStatusToastToItsClass()
        {
            var shell = new ZepShellView(ZepPreviewSeed.Build());

            try
            {
                var topBar = shell.Q<VisualElement>("zep-top-bar");
                Assert.That(topBar, Is.Not.Null);
                Assert.That(
                    topBar.style.opacity.keyword,
                    Is.Not.EqualTo(StyleKeyword.Null),
                    "the top bar is the one element whose class declares the entrance transition");

                var contentArea = shell.Q<VisualElement>("zep-workspace-row");
                Assert.That(contentArea, Is.Not.Null);
                Assert.That(
                    contentArea.style.opacity.keyword,
                    Is.EqualTo(StyleKeyword.Null),
                    "the content area has no opacity transition; writing one only blanked it for a frame");
                Assert.That(contentArea.style.translate.keyword, Is.EqualTo(StyleKeyword.Null));

                var statusBar = shell.Q<ZepStatusBarWidget>();
                Assert.That(statusBar, Is.Not.Null);
                Assert.That(
                    statusBar.style.opacity.keyword,
                    Is.EqualTo(StyleKeyword.Null),
                    "an inline opacity on the status bar out-ranks .zep-status-visible forever");
                Assert.That(statusBar.style.translate.keyword, Is.EqualTo(StyleKeyword.Null));

                // A real toast: the fade must still be a pure class toggle.
                statusBar.UpdateStatus("Indexing 12 assets…");
                statusBar.UpdateStatus("Ready.");
                Assert.That(
                    statusBar.ClassListContains(ZepUiClasses.StatusVisible),
                    Is.True,
                    "the toast shows itself by adding the visible class");
                Assert.That(
                    statusBar.style.opacity.keyword,
                    Is.EqualTo(StyleKeyword.Null),
                    "showing a toast must not stamp an inline opacity either");
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void FolderNavigation_HistoryFollowUpDoesNotRefreshShellAndSidebarTwice()
        {
            var state = ZepPreviewSeed.Build();
            var shell = new ZepShellView(state);
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();

            try
            {
                Assert.That(
                    state.PrimaryPane.TrySetFolderSelection("Assets/Art"),
                    Is.True);

                var measurements = ZepPerfLog.CaptureSnapshot().Measurements;
                Assert.That(
                    measurements.Count(measurement =>
                        measurement.Stage == ZepPerfStage.ShellRefresh),
                    Is.EqualTo(1),
                    "the history-only notification must update buttons without a second shell paint");
                Assert.That(
                    measurements.Count(measurement =>
                        measurement.Stage == ZepPerfStage.SidebarRefresh),
                    Is.EqualTo(1),
                    "one folder switch must flush the sidebar exactly once");
            }
            finally
            {
                ZepPerfLog.IsEnabled = false;
                ZepPerfLog.Reset();
                shell.Teardown();
            }
        }

        [Test]
        public void Constructor_UsesStoredGridViewPreference()
        {
            var previousPreference = EditorPrefs.GetString(AssetViewModePreferenceKey, string.Empty);
            try
            {
                EditorPrefs.SetString(AssetViewModePreferenceKey, "Grid");
                var shell = new ZepShellView(ZepPreviewSeed.Build());
                // The AssetCollectionView substrate creates the per-mode widget
                // lazily and toggles visibility via inline display: with the
                // stored Grid preference the grid widget must be mounted and
                // shown, and the list widget either never created or hidden.
                var collection = shell.Q<VisualElement>("zep-asset-collection");
                Assert.That(collection, Is.Not.Null);

                var gridView = collection.Q<VisualElement>("zep-grid-asset-view");
                Assert.That(gridView, Is.Not.Null);
                // keyword check first: an unset inline display reads back as
                // value == Flex (enum default), which would pass vacuously.
                Assert.That(gridView.style.display.keyword, Is.Not.EqualTo(StyleKeyword.Null),
                    "Grid widget's display must be explicitly stamped by RefreshViewPresentation.");
                Assert.That(gridView.style.display.value, Is.EqualTo(DisplayStyle.Flex));

                var listView = collection.Q<VisualElement>("zep-list-asset-view");
                Assert.That(
                    listView == null || listView.style.display.value == DisplayStyle.None,
                    Is.True,
                    "List widget must not be visible when the stored view mode is Grid.");
            }
            finally
            {
                if (string.IsNullOrEmpty(previousPreference))
                {
                    EditorPrefs.DeleteKey(AssetViewModePreferenceKey);
                }
                else
                {
                    EditorPrefs.SetString(AssetViewModePreferenceKey, previousPreference);
                }
            }
        }

        [Test]
        public void SearchField_KeepsQueryInTheDialog()
        {
            var viewModel = new ZepState();
            var shell = new ZepShellView(viewModel);

            // ToolbarSearchField.value setter dispatches ChangeEvent through the panel,
            // which is null in batch tests. Drive the wired handler directly instead.
            InvokeShellMethod(shell, "OnSearchTextChanged", "icons");

            Assert.That(viewModel.PrimaryPane.SearchText, Is.Empty);
            Assert.That(((SearchBarWidget)GetShellField(shell, "_searchOverlay")).SearchText, Is.EqualTo("icons"));
        }

        // Dialog typing leaves both panes alone; sidebar navigation targets the main view.
        [Test]
        public void SharedSearchAndSidebarNavigation_BothRouteToPrimary()
        {
            var viewModel = new ZepState();
            var auxiliary = viewModel.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var shell = new ZepShellView(viewModel);
            Assert.That(viewModel.ActivePane, Is.SameAs(auxiliary));

            InvokeShellMethod(shell, "OnSearchTextChanged", "hero");

            Assert.That(viewModel.PrimaryPane.SearchText, Is.Empty);
            Assert.That(((SearchBarWidget)GetShellField(shell, "_searchOverlay")).SearchText, Is.EqualTo("hero"));
            Assert.That(auxiliary.SearchText, Is.Empty);

            InvokeShellMethod(shell, "OnFolderPaneFolderSelected", "Assets/Audio");

            Assert.That(viewModel.ActivePane, Is.SameAs(viewModel.PrimaryPane));
            Assert.That(
                viewModel.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(
                auxiliary.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
        }

        // Opening a folder from a tag split used to bounce the user to the
        // primary pane; it now opens in the pane that was clicked, and the
        // primary pane is left exactly where it was.
        [Test]
        public void TagPaneFolderOpen_BrowsesInsideTheTagPaneAndLeavesPrimaryAlone()
        {
            var viewModel = new ZepState();
            viewModel.PrimaryPane.TrySetFolderSelection("Assets/Audio");
            var tagPane = viewModel.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue")).NewPane;
            var shell = new ZepShellView(viewModel);

            shell.OpenFolderFromPane(tagPane, "Assets/Art");

            Assert.That(viewModel.ActivePane, Is.SameAs(tagPane));
            Assert.That(
                viewModel.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(tagPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
            Assert.That(tagPane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(tagPane.TagBrowseRootPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(tagPane.Source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
        }

        [Test]
        public void ProductionShell_MountsOrderedPrimaryAndAuxiliaryPaneViews()
        {
            var viewModel = new ZepState();
            var auxiliary = viewModel.OpenAuxiliary(ZepPaneSource.FixedTag("green")).NewPane;

            var shell = new ZepShellView(viewModel);
            var paneViews = shell.Query<ZepPaneView>().ToList();

            Assert.That(paneViews.Count, Is.EqualTo(2));
            Assert.That(paneViews[0].Pane, Is.SameAs(viewModel.PrimaryPane));
            Assert.That(paneViews[1].Pane, Is.SameAs(auxiliary));
        }

        [Test]
        public void TypeFilterSelection_WritesBackToViewModel()
        {
            var viewModel = new ZepState();
            viewModel.PrimaryPane.SetTypeFilterOptions(new[]
            {
                new ZepTypeFilterOption(string.Empty, 3),
                new ZepTypeFilterOption("Texture2D", 2)
            });

            var shell = new ZepShellView(viewModel);

            InvokeShellMethod(shell, "SelectTypeFilter", "Texture2D");

            Assert.That(viewModel.PrimaryPane.SelectedTypeFilter, Is.EqualTo("Texture2D"));
        }

        [TestCase("blue")]
        [TestCase("BLUE")]
        public void TagFilterSelection_RepeatedClickPreservesTagAndSelection(string clickedTagId)
        {
            var viewModel = new ZepState();
            viewModel.SetTagFilterOptions(new[]
            {
                new ZepTagFilterOption(new ZepTag("blue", "Blue", ZepTagColor.Blue, true), 2),
                new ZepTagFilterOption(new ZepTag("green", "Green", ZepTagColor.Green, true), 1)
            });

            var selectionNotifications = 0;
            var shell = new ZepShellView(viewModel,
                assetSelectionAction: _ => selectionNotifications++);
            try
            {
                var pane = viewModel.PrimaryPane;
                shell.SelectTagFilter("blue");
                pane.SetSelectedAssetPath(AssetPath.From("Assets/Selected.asset"));
                pane.SetSearchText("Selected");
                pane.SetSelectedSearchScope(ZepSearchScope.Packages);
                selectionNotifications = 0;

                shell.SelectTagFilter(clickedTagId);

                Assert.That(pane.SelectedTagFilterId, Is.EqualTo("blue"));
                Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
                Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From("Assets/Selected.asset") }));
                Assert.That(pane.SearchText, Is.EqualTo("Selected"));
                Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Packages));
                Assert.That(selectionNotifications, Is.Zero, "a repeated tag click must not clear Unity's selection");
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void TagFilterSelection_SwitchesTagsAndAllowsFolderNavigation()
        {
            var viewModel = new ZepState();
            viewModel.SetTagFilterOptions(new[]
            {
                new ZepTagFilterOption(new ZepTag("blue", "Blue", ZepTagColor.Blue, true), 2),
                new ZepTagFilterOption(new ZepTag("green", "Green", ZepTagColor.Green, true), 1)
            });
            IReadOnlyList<ZepAssetEntry> notifiedSelection = null;
            var shell = new ZepShellView(viewModel,
                assetSelectionAction: assets => notifiedSelection = assets);
            try
            {
                var pane = viewModel.PrimaryPane;
                shell.SelectTagFilter("blue");
                pane.SetSelectedAssetPath(AssetPath.From("Assets/Selected.asset"));
                notifiedSelection = null;

                shell.SelectTagFilter("green");

                Assert.That(pane.SelectedTagFilterId, Is.EqualTo("green"));
                Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
                Assert.That(pane.SelectedAssetPaths, Is.Empty);
                Assert.That(notifiedSelection, Is.Not.Null.And.Empty);

                InvokeShellMethod(shell, "OnFolderPaneFolderSelected", "Assets");

                Assert.That(pane.SelectedTagFilterId, Is.Empty);
                Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets"));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void TagFilterSelection_RepeatedClickActivatesPrimaryFromSplit()
        {
            var viewModel = new ZepState();
            viewModel.SetTagFilterOptions(new[]
            {
                new ZepTagFilterOption(new ZepTag("blue", "Blue", ZepTagColor.Blue, true), 2)
            });
            var shell = new ZepShellView(viewModel);
            try
            {
                var primary = viewModel.PrimaryPane;
                shell.SelectTagFilter("blue");
                primary.SetSelectedAssetPath(AssetPath.From("Assets/Selected.asset"));
                var split = viewModel.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
                Assert.That(viewModel.ActivePane, Is.SameAs(split));

                shell.SelectTagFilter("blue");

                Assert.That(viewModel.ActivePane, Is.SameAs(primary));
                Assert.That(primary.SelectedTagFilterId, Is.EqualTo("blue"));
                Assert.That(primary.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
                Assert.That(primary.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From("Assets/Selected.asset") }));
                Assert.That(split.CurrentFolderPath.Value, Is.EqualTo("Assets/Art"));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void SearchScopeSelection_WritesBackToViewModel()
        {
            var viewModel = new ZepState();
            viewModel.PrimaryPane.SetSearchText("icon");
            viewModel.PrimaryPane.SetSearchScopeOptions(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets"),
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder, "'Art'")
            });

            var shell = new ZepShellView(viewModel);

            Assert.That(shell.Q<VisualElement>("zep-search-scope-row").style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(shell.Q<Button>("zep-search-scope-button-assets"), Is.Not.Null);

            // The shell wires SearchScopeSelected as an inline lambda on _topBar; fire
            // the event directly via reflection (Button.clickable.SimulateClick needs a
            // panel to dispatch).
            var topBarField = typeof(ZepShellView).GetField("_topBar", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(topBarField, Is.Not.Null);
            var topBar = topBarField.GetValue(shell);
            var eventField = topBar.GetType().GetField(nameof(SearchScopeSelected), BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(eventField, Is.Not.Null, "TopBarWidget should expose the SearchScopeSelected event field.");
            var handler = (Action<ZepSearchScope>)eventField.GetValue(topBar);
            Assert.That(handler, Is.Not.Null, "Shell should subscribe to SearchScopeSelected on construction.");

            handler.Invoke(ZepSearchScope.Packages);

            Assert.That(
                viewModel.PrimaryPane.SelectedSearchScope,
                Is.EqualTo(ZepSearchScope.Packages));
        }

        [Test]
        public void SearchScopeRow_RemainsVisibleForTagOnlyProjection()
        {
            var viewModel = new ZepState();
            viewModel.PrimaryPane.SetSelectedTagFilterId("blue");
            viewModel.PrimaryPane.SetSearchScopeOptions(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets")
            });

            var shell = new ZepShellView(viewModel);

            Assert.That(viewModel.PrimaryPane.SearchText, Is.Empty);
            Assert.That(
                shell.Q<VisualElement>("zep-search-scope-row").style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
            Assert.That(shell.Q<Button>("zep-search-scope-button-assets"), Is.Not.Null);
        }

        // The top bar belongs to the MAIN assets view. Dropping a tag into a
        // split activates the split, and the top bar used to follow the active
        // pane: the scope row lit up with the split's tag scopes, and search,
        // scope, type filter and the arrows all targeted the split. All of it
        // stays on the primary pane now; only the plus button follows focus.
        [Test]
        public void TopBar_IgnoresTheActiveSplitAndStaysBoundToThePrimaryPane()
        {
            var viewModel = new ZepState();
            var primary = viewModel.PrimaryPane;
            primary.TrySetFolderSelection("Assets");
            primary.SyncNavigationHistory();
            primary.TrySetFolderSelection("Assets/Art");
            primary.SyncNavigationHistory();
            primary.SetTypeFilterOptions(new[]
            {
                new ZepTypeFilterOption(string.Empty, 3),
                new ZepTypeFilterOption("Texture2D", 2)
            });
            // Scope options come from the controller's projection in production;
            // without one the row has nothing to show, so seed them here.
            primary.SetSearchScopeOptions(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets")
            });
            var shell = new ZepShellView(viewModel);

            var tagPane = viewModel.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            Assert.That(viewModel.ActivePane, Is.SameAs(tagPane));

            Assert.That(
                shell.Q<VisualElement>("zep-search-scope-row").style.display.value,
                Is.EqualTo(DisplayStyle.None),
                "the split's tag is not a filter on the main view");

            InvokeShellMethod(shell, "OnSearchTextChanged", "hero");
            Assert.That(primary.SearchText, Is.Empty);
            Assert.That(tagPane.SearchText, Is.Empty);
            OpenSearch(shell);
            Assert.That(
                ((SearchBarWidget)GetShellField(shell, "_searchOverlay")).Q<VisualElement>("zep-search-scope-row").style.display.value,
                Is.EqualTo(DisplayStyle.Flex));

            RaiseTopBarEvent<Action<ZepSearchScope>>(shell, SearchScopeSelected)
                .Invoke(ZepSearchScope.Packages);
            Assert.That(primary.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
            Assert.That(tagPane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.All));
            ((SearchBarWidget)GetShellField(shell, "_searchOverlay")).Hide();

            InvokeShellMethod(shell, "SelectTypeFilter", "Texture2D");
            Assert.That(primary.SelectedTypeFilter, Is.EqualTo("Texture2D"));
            Assert.That(tagPane.SelectedTypeFilter, Is.Empty);

            // BackClicked is forwarded by the top bar to its navigation widget,
            // so the subscribed delegate lives on that widget's field.
            var topBarNavigation = shell.Q<NavigationButtonsWidget>("zep-folder-nav");
            Assert.That(topBarNavigation, Is.Not.Null);
            var backField = typeof(NavigationButtonsWidget).GetField(
                "BackClicked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(backField, Is.Not.Null);
            ((Action)backField.GetValue(topBarNavigation)).Invoke();
            Assert.That(primary.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets")));
            Assert.That(tagPane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(viewModel.ActivePane, Is.SameAs(tagPane), "focus is not stolen either");
        }

        private static TDelegate RaiseTopBarEvent<TDelegate>(ZepShellView shell, string eventName)
            where TDelegate : Delegate
        {
            var topBarField = typeof(ZepShellView).GetField("_topBar", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(topBarField, Is.Not.Null);
            var topBar = topBarField.GetValue(shell);
            var eventField = topBar.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(eventField, Is.Not.Null, $"TopBarWidget should expose the {eventName} event field.");
            var handler = eventField.GetValue(topBar) as TDelegate;
            Assert.That(handler, Is.Not.Null, $"Shell should subscribe to {eventName} on construction.");
            return handler;
        }

        // Marker for nameof() above — TopBarWidget's event lives in a different namespace
        // and isn't statically referenced here, so we use a const string handle.
        private const string SearchScopeSelected = "SearchScopeSelected";

        [Test]
        public void ThemeDropdown_PersistsSelectedThemePreference()
        {
            const string themeKey = ZepThemePreferences.ThemeStringPreferenceKey;
            var hadPreference = EditorPrefs.HasKey(themeKey);
            var previousPreference = EditorPrefs.GetString(themeKey, string.Empty);
            try
            {
                EditorPrefs.SetString(themeKey, ZepThemeRegistry.DefaultThemeId);
                var shell = new ZepShellView(new ZepState());
                var dropdown = shell.Q<PopupField<string>>("zep-theme-dropdown");

                Assert.That(dropdown, Is.Not.Null);
                Assert.That(dropdown.ClassListContains(ZepUiClasses.ThemeToggle), Is.True);

                var nonDefaultTheme = shell.Registry.AvailableThemes
                    .Select(t => t.Id)
                    .First(id => !string.Equals(id, ZepThemeRegistry.DefaultThemeId, StringComparison.Ordinal));

                // PopupField.value setter dispatches ChangeEvent through the panel; drive
                // the theme switcher directly so the test runs without a panel attached.
                shell.ThemeSwitcher.SetTheme(nonDefaultTheme, true);

                Assert.That(EditorPrefs.GetString(themeKey, string.Empty), Is.EqualTo(nonDefaultTheme));
                Assert.That(ZepThemePreferences.GetTheme(), Is.EqualTo(nonDefaultTheme));
            }
            finally
            {
                if (hadPreference)
                {
                    EditorPrefs.SetString(themeKey, previousPreference);
                }
                else
                {
                    EditorPrefs.DeleteKey(themeKey);
                }
            }
        }

        [Test]
        public void Constructor_BuildsPromptOverlayHiddenByDefault()
        {
            var shell = new ZepShellView(new ZepState());
            var modalScrim = shell.Q<VisualElement>("zep-modal-scrim");

            Assert.That(modalScrim, Is.Not.Null);
            Assert.That(modalScrim.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        private void OpenSearch(ZepShellView shell)
        {
            MountSearchHost(shell);
            ((IZepShortcutHost)shell).FocusSearch();
        }

        private void MountSearchHost(ZepShellView shell)
        {
            if (shell.panel == null)
            {
                var host = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
                _searchHosts.Add(host);
                host.Show();
                host.position = new Rect(100, 100, 340, 280);
                host.rootVisualElement.Add(shell);
            }
        }

        [UnityTest]
        public IEnumerator SearchWindow_UsesASeparatePanelLargerThanItsSmallHost_AndRestoresQueryOnReopen()
        {
            if (Application.isBatchMode)
                Assert.Ignore("Native dropdown focus requires an interactive Editor; batch mode dismisses it on the next tick.");
            var shell = new ZepShellView(new ZepState());
            var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
            MountSearchHost(shell);
            yield return null;
            yield return null;
            OpenSearch(shell);
            var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
            Assert.That(popup, Is.Not.Null);
            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(popup != null, Is.True, "The popup must retain focus after opening.");
            Assert.That(search.panel, Is.Not.SameAs(shell.panel));
            Assert.That(popup.position.width, Is.GreaterThan(_searchHosts[0].position.width));
            Assert.That(popup.position.height, Is.GreaterThan(_searchHosts[0].position.height));
            var popupRoot = popup.rootVisualElement.Q(className: ZepUiClasses.SearchWindow);
            Assert.That(popupRoot.styleSheets.count, Is.EqualTo(3));
            Assert.That(search.Q("zep-search-card").worldBound.width,
                Is.EqualTo(popup.rootVisualElement.worldBound.width).Within(2f));
            Assert.That(search.Q("zep-search-scope-row"), Is.Not.Null);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(popup));
            var field = search.Q<ToolbarSearchField>("zep-search");
            var focused = search.focusController.focusedElement as VisualElement;
            Assert.That(focused == field || field.Contains(focused), Is.True);
            var otherTheme = shell.Registry.AvailableThemes.First(t => t.Id != shell.ThemeSwitcher.CurrentTheme).Id;
            shell.ThemeSwitcher.SetTheme(otherTheme, persistPreference: false);
            Assert.That(popupRoot.ClassListContains("zep-theme-" + otherTheme), Is.True);
            Assert.That(popupRoot.styleSheets.count, Is.EqualTo(3));
            field.value = "hero";
            using (var escape = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }))
            {
                escape.target = field;
                field.SendEvent(escape);
            }
            Assert.That(popup == null, Is.True, "Dismissal must destroy the native popup.");
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(_searchHosts[0]));
            Assert.That(search.parent, Is.SameAs(shell));
            Assert.That(shell.Q("zep-top-bar").Q("zep-search-scope-row"), Is.Not.Null);
            Assert.That(search.IsVisible, Is.False);

            OpenSearch(shell);
            Assert.That(search.SearchText, Is.EqualTo("hero"));
            var reopened = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
            Assert.That(reopened, Is.Not.Null);
            shell.RemoveFromHierarchy();
            Assert.That(reopened == null, Is.True, "Detaching the owner must close its popup.");
            Assert.That(search.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator SearchWindow_TopAndBottomDragAreas_MoveOnlyTheWindow_AndWorkAfterReopening()
        {
            if (Application.isBatchMode)
                Assert.Ignore("Native dropdown focus requires an interactive Editor; batch mode dismisses it on the next tick.");
            var state = new ZepState();
            var shell = new ZepShellView(state);
            MountSearchHost(shell);
            // Leave room for movement in both directions without screen-edge fitting.
            _searchHosts[0].position = new Rect(400f, 300f, 340f, 280f);
            yield return null;
            yield return null;

            for (var open = 0; open < 2; open++)
            {
                OpenSearch(shell);
                yield return new WaitForSecondsRealtime(0.2f);
                var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
                Assert.That(popup != null, Is.True);
                var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
                var field = search.Q<ToolbarSearchField>("zep-search");
                field.SetValueWithoutNotify("hero");
                var focused = search.focusController.focusedElement;
                var folder = state.PrimaryPane.CurrentFolderPath;
                var surface = popup.rootVisualElement.Q(className: ZepUiClasses.SearchWindow);
                var title = search.Q<Label>(className: ZepUiClasses.SearchTitle);
                var footer = search.Q(className: ZepUiClasses.SearchFooter);
                var bounds = surface.worldBound;
                var points = new[]
                {
                    ("title", title.worldBound.center),
                    // Inside the 6px edge band and 14px corner squares of the resize frame.
                    ("top left padding", new Vector2(bounds.xMin + 16, bounds.yMin + 16)),
                    ("top right padding", new Vector2(bounds.xMax - 16, bounds.yMin + 16)),
                    ("left of title", new Vector2(bounds.xMin + 8, title.worldBound.center.y)),
                    ("right of title", new Vector2(bounds.xMax - 8, title.worldBound.center.y)),
                    ("above input", new Vector2(bounds.center.x, field.worldBound.yMin - 3)),
                    ("footer text", footer.worldBound.center),
                    ("above footer", new Vector2(bounds.center.x, footer.worldBound.yMin - 3)),
                    ("left of footer", new Vector2(bounds.xMin + 8, footer.worldBound.center.y)),
                    ("right of footer", new Vector2(bounds.xMax - 8, footer.worldBound.center.y)),
                    ("bottom left padding", new Vector2(bounds.xMin + 16, bounds.yMax - 16))
                };
                for (var i = 0; i < points.Length; i++)
                {
                    var (area, grab) = points[i];
                    var before = SearchWindowScreenRect(popup);
                    var picked = search.panel.Pick(grab);
                    Assert.That(picked, Is.Not.Null, area);
                    SendSearchWindowPointer(picked, EventType.MouseDown, grab);
                    try
                    {
                        Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.True, area);
                        var delta = new Vector2(20f, 20f) * (i % 2 == 0 ? 1 : -1);
                        SendSearchWindowPointer(surface, EventType.MouseDrag, grab + delta);
                        // Native movement is asynchronous; position briefly reports (0, 0).
                        var deadline = EditorApplication.timeSinceStartup + 1;
                        while ((Mathf.Abs(SearchWindowScreenRect(popup).x - before.x - delta.x) > 1f
                                || Mathf.Abs(SearchWindowScreenRect(popup).y - before.y - delta.y) > 1f)
                               && EditorApplication.timeSinceStartup < deadline) yield return null;
                        Assert.That(SearchWindowScreenRect(popup).x, Is.EqualTo(before.x + delta.x).Within(1f), area);
                        Assert.That(SearchWindowScreenRect(popup).y, Is.EqualTo(before.y + delta.y).Within(1f), area);
                        Assert.That(SearchWindowScreenRect(popup).size, Is.EqualTo(before.size), area);
                    }
                    finally
                    {
                        SendSearchWindowPointer(surface, EventType.MouseUp, grab);
                    }
                    Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.False, area);
                    var after = SearchWindowScreenRect(popup);
                    SendSearchWindowPointer(surface, EventType.MouseMove, grab + new Vector2(100f, 100f));
                    Assert.That(SearchWindowScreenRect(popup), Is.EqualTo(after), "Movement must stop on release: " + area);
                    Assert.That(search.focusController.focusedElement, Is.SameAs(focused), area);
                }

                foreach (var point in new[] { field.worldBound.center,
                             search.Q(className: ZepUiClasses.SearchEmpty).worldBound.center })
                {
                    var before = SearchWindowScreenRect(popup);
                    var picked = search.panel.Pick(point);
                    SendSearchWindowPointer(picked, EventType.MouseDown, point);
                    try
                    {
                        Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.False,
                            "The input and results area must not start a window drag.");
                        SendSearchWindowPointer(picked, EventType.MouseDrag, point + new Vector2(5f, 5f));
                        Assert.That(SearchWindowScreenRect(popup), Is.EqualTo(before));
                    }
                    finally
                    {
                        SendSearchWindowPointer(picked, EventType.MouseUp, point);
                    }
                }
                SendSearchWindowPointer(title, EventType.MouseDown, title.worldBound.center, button: 1);
                Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.False);
                SendSearchWindowPointer(title, EventType.MouseUp, title.worldBound.center, button: 1);
                Assert.That(search.SearchText, Is.EqualTo("hero"));
                Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(folder));
                Assert.That(state.PrimaryPane.SearchText, Is.Empty);

                Assert.That(search.Q("zep-search-close"), Is.Null);
                using (var escape = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }))
                    search.Q<ToolbarSearchField>("zep-search").SendEvent(escape);
                Assert.That(popup == null, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator SearchWindow_ResizeGrip_ResizesWithoutMovingOrLosingSelection_AndClampsMinimum()
        {
            if (Application.isBatchMode)
                Assert.Ignore("Native dropdown focus requires an interactive Editor; batch mode dismisses it on the next tick.");
            var state = new ZepState();
            var shell = new ZepShellView(state);
            MountSearchHost(shell);
            _searchHosts[0].position = new Rect(350f, 300f, 340f, 280f);
            yield return null;
            yield return null;
            for (var open = 0; open < 2; open++)
            {
                OpenSearch(shell);
                yield return new WaitForSecondsRealtime(0.2f);
                var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
                Assert.That(popup != null, Is.True);
                var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
                var surface = popup.rootVisualElement.Q(className: ZepUiClasses.SearchWindow);
                var grip = surface.Q(className: ZepUiClasses.SearchResizeGrip);
                var query = "hero " + open;
                search.SetSearchText(query);
                var assets = Enumerable.Range(0, 60).Select(i => new ZepAssetEntry(
                    "Hero " + i, "Assets/Hero" + i + ".prefab", "GameObject")).ToArray();
                search.SetResults(assets);
                var field = search.Q<ToolbarSearchField>("zep-search");
                using (var up = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.UpArrow }))
                    field.SendEvent(up);
                for (var frame = 0; frame < 10; frame++) yield return null;
                var focused = search.focusController.focusedElement;
                var folder = state.PrimaryPane.CurrentFolderPath;
                var before = SearchWindowScreenRect(popup);
                foreach (var delta in new[] { new Vector2(160, 80), new Vector2(-2000, -2000), new Vector2(250, 180) })
                {
                    var start = SearchWindowScreenRect(popup);
                    var grab = grip.worldBound.center;
                    Assert.That(search.panel.Pick(grab), Is.SameAs(grip));
                    SendSearchWindowPointer(grip, EventType.MouseDown, grab);
                    Assert.That(grip.HasPointerCapture(PointerId.mousePointerId), Is.True);
                    Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.False);
                    SendSearchWindowPointer(grip, EventType.MouseDrag, grab + delta);
                    SendSearchWindowPointer(grip, EventType.MouseUp, grab + delta);
                    for (var frame = 0; frame < 10; frame++) yield return null;
                    var after = SearchWindowScreenRect(popup);
                    Assert.That(after.x, Is.EqualTo(before.x).Within(1f));
                    Assert.That(after.y, Is.EqualTo(before.y).Within(1f));
                    Assert.That(after.width, Is.EqualTo(Mathf.Max(popup.minSize.x, start.width + delta.x)).Within(1f));
                    Assert.That(after.height, Is.EqualTo(Mathf.Max(popup.minSize.y, start.height + delta.y)).Within(1f));
                    Assert.That(grip.HasPointerCapture(PointerId.mousePointerId), Is.False);
                    SendSearchWindowPointer(grip, EventType.MouseMove, grab + delta + Vector2.one * 50);
                    Assert.That(SearchWindowScreenRect(popup), Is.EqualTo(after), "Resizing stops after release");
                    Assert.That(search.focusController.focusedElement, Is.SameAs(focused));
                    Assert.That(search.SelectedResult, Is.SameAs(assets[59]));
                    Assert.That(search.SearchText, Is.EqualTo(query));
                    var viewport = ((ScrollView)search.SuggestionSurface).contentViewport;
                    var selectedRow = search.Q("zep-search-suggestion-59");
                    Assert.That(selectedRow, Is.Not.Null);
                    Assert.That(selectedRow.worldBound.yMin, Is.GreaterThanOrEqualTo(viewport.worldBound.yMin - 1f));
                    Assert.That(selectedRow.worldBound.yMax, Is.LessThanOrEqualTo(viewport.worldBound.yMax + 1f),
                        "Native popup reparenting must preserve resize-driven selection scrolling");
                    Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(folder));
                }
                SendSearchWindowPointer(grip, EventType.MouseDown, grip.worldBound.center, button: 1);
                Assert.That(grip.HasPointerCapture(PointerId.mousePointerId), Is.False);
                SendSearchWindowPointer(grip, EventType.MouseUp, grip.worldBound.center, button: 1);
                var finalGrab = grip.worldBound.center;
                SendSearchWindowPointer(grip, EventType.MouseDown, finalGrab);
                SendSearchWindowPointer(grip, EventType.MouseDrag, finalGrab + Vector2.one * 10);
                SendSearchWindowPointer(grip, EventType.MouseUp, finalGrab + Vector2.one * 10);
                search.Hide();
                yield return null; // A queued resize repaint must tolerate immediate closure.
                Assert.That(popup == null, Is.True);
            }
        }

        [Test]
        public void SearchWindow_RememberedSize_DefaultsWhenUnset_AndNeverDropsBelowTheMinimum()
        {
            var keys = new[] { ZepSearchWindow.WidthPreferenceKey, ZepSearchWindow.HeightPreferenceKey };
            var saved = keys.Select(key => EditorPrefs.HasKey(key) ? EditorPrefs.GetFloat(key) : (float?)null).ToArray();
            try
            {
                foreach (var key in keys) EditorPrefs.DeleteKey(key);
                Assert.That(ZepSearchWindow.RememberedSize(), Is.EqualTo(ZepSearchWindow.DefaultSize));
                EditorPrefs.SetFloat(keys[0], 880f);
                EditorPrefs.SetFloat(keys[1], 720f);
                Assert.That(ZepSearchWindow.RememberedSize(), Is.EqualTo(new Vector2(880f, 720f)));
                EditorPrefs.SetFloat(keys[0], 10f);
                EditorPrefs.SetFloat(keys[1], 10f);
                Assert.That(ZepSearchWindow.RememberedSize(), Is.EqualTo(ZepSearchWindow.MinimumSize));
            }
            finally
            {
                RestoreSearchSizePreferences(keys, saved);
            }
        }

        private static void RestoreSearchSizePreferences(string[] keys, float?[] saved)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                if (saved[i].HasValue) EditorPrefs.SetFloat(keys[i], saved[i].Value);
                else EditorPrefs.DeleteKey(keys[i]);
            }
        }

        [UnityTest]
        public IEnumerator SearchWindow_EdgeHandles_ResizeFromEverySide_KeepTheOppositeEdge_AndRememberTheSize()
        {
            if (Application.isBatchMode)
                Assert.Ignore("Native dropdown focus requires an interactive Editor; batch mode dismisses it on the next tick.");
            var keys = new[] { ZepSearchWindow.WidthPreferenceKey, ZepSearchWindow.HeightPreferenceKey };
            var saved = keys.Select(key => EditorPrefs.HasKey(key) ? EditorPrefs.GetFloat(key) : (float?)null).ToArray();
            foreach (var key in keys) EditorPrefs.DeleteKey(key);
            var shell = new ZepShellView(new ZepState());
            MountSearchHost(shell);
            _searchHosts[0].position = new Rect(400f, 300f, 340f, 280f);
            yield return null;
            yield return null;
            OpenSearch(shell);
            yield return new WaitForSecondsRealtime(0.2f);
            var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
            Assert.That(popup != null, Is.True);
            var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
            var surface = popup.rootVisualElement.Q(className: ZepUiClasses.SearchWindow);
            var min = popup.minSize;
            var cases = new (string handle, Vector2 delta, Func<Rect, Rect> expected)[]
            {
                (ZepUiClasses.SearchResizeEdgeLeft, new Vector2(-80f, 0f),
                    r => Rect.MinMaxRect(r.xMin - 80f, r.yMin, r.xMax, r.yMax)),
                (ZepUiClasses.SearchResizeEdgeTop, new Vector2(0f, -60f),
                    r => Rect.MinMaxRect(r.xMin, r.yMin - 60f, r.xMax, r.yMax)),
                (ZepUiClasses.SearchResizeCornerTopLeft, new Vector2(40f, 30f),
                    r => Rect.MinMaxRect(r.xMin + 40f, r.yMin + 30f, r.xMax, r.yMax)),
                (ZepUiClasses.SearchResizeEdgeRight, new Vector2(90f, 0f),
                    r => Rect.MinMaxRect(r.xMin, r.yMin, r.xMax + 90f, r.yMax)),
                (ZepUiClasses.SearchResizeEdgeBottom, new Vector2(0f, 50f),
                    r => Rect.MinMaxRect(r.xMin, r.yMin, r.xMax, r.yMax + 50f)),
                (ZepUiClasses.SearchResizeCornerTopRight, new Vector2(30f, -20f),
                    r => Rect.MinMaxRect(r.xMin, r.yMin - 20f, r.xMax + 30f, r.yMax)),
                (ZepUiClasses.SearchResizeCornerBottomLeft, new Vector2(-25f, 35f),
                    r => Rect.MinMaxRect(r.xMin - 25f, r.yMin, r.xMax, r.yMax + 35f)),
                // Pushing an edge past the opposite one clamps at the minimum
                // while that opposite edge stays where it was.
                (ZepUiClasses.SearchResizeEdgeLeft, new Vector2(5000f, 0f),
                    r => Rect.MinMaxRect(r.xMax - min.x, r.yMin, r.xMax, r.yMax)),
                (ZepUiClasses.SearchResizeEdgeTop, new Vector2(0f, 5000f),
                    r => Rect.MinMaxRect(r.xMin, r.yMax - min.y, r.xMax, r.yMax))
            };
            foreach (var (className, delta, expected) in cases)
            {
                var handle = surface.Q(className: className);
                Assert.That(handle, Is.Not.Null, className);
                var start = SearchWindowScreenRect(popup);
                var grab = handle.worldBound.center;
                Assert.That(search.panel.Pick(grab), Is.SameAs(handle), className);
                SendSearchWindowPointer(handle, EventType.MouseDown, grab);
                Assert.That(handle.HasPointerCapture(PointerId.mousePointerId), Is.True, className);
                Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.False,
                    "A resize handle must not start a window drag: " + className);
                SendSearchWindowPointer(handle, EventType.MouseDrag, grab + delta);
                SendSearchWindowPointer(handle, EventType.MouseUp, grab + delta);
                for (var frame = 0; frame < 10; frame++) yield return null;
                var after = SearchWindowScreenRect(popup);
                var want = expected(start);
                Assert.That(after.xMin, Is.EqualTo(want.xMin).Within(1f), className + " left edge");
                Assert.That(after.yMin, Is.EqualTo(want.yMin).Within(1f), className + " top edge");
                Assert.That(after.xMax, Is.EqualTo(want.xMax).Within(1f), className + " right edge");
                Assert.That(after.yMax, Is.EqualTo(want.yMax).Within(1f), className + " bottom edge");
                Assert.That(handle.HasPointerCapture(PointerId.mousePointerId), Is.False, className);
                Assert.That(ZepSearchWindow.RememberedSize().x, Is.EqualTo(after.width).Within(1f), className + " remembers width");
                Assert.That(ZepSearchWindow.RememberedSize().y, Is.EqualTo(after.height).Within(1f), className + " remembers height");
            }

            var closedSize = SearchWindowScreenRect(popup).size;
            search.Hide();
            yield return null;
            Assert.That(popup == null, Is.True);
            OpenSearch(shell);
            yield return new WaitForSecondsRealtime(0.2f);
            var reopened = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
            Assert.That(reopened != null, Is.True);
            var reopenedRect = SearchWindowScreenRect(reopened);
            Assert.That(reopenedRect.width, Is.EqualTo(closedSize.x).Within(1f), "Reopening restores the last width");
            Assert.That(reopenedRect.height, Is.EqualTo(closedSize.y).Within(1f), "Reopening restores the last height");
            Assert.That(reopenedRect.center.x, Is.EqualTo(_searchHosts[0].position.center.x).Within(1f), "Reopening centres on the host");
            Assert.That(reopenedRect.center.y, Is.EqualTo(_searchHosts[0].position.center.y).Within(1f), "Reopening centres on the host");
            search.Hide();
            yield return null;
            RestoreSearchSizePreferences(keys, saved);
        }

        private static Rect SearchWindowScreenRect(ZepSearchWindow window)
        {
            // EditorWindow.position may become (0, 0) after a popup move. Read
            // the native container to prove the window actually moved on screen.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var parent = typeof(EditorWindow).GetField("m_Parent", flags).GetValue(window);
            var container = parent.GetType().GetProperty("window", flags).GetValue(parent);
            return (Rect)container.GetType().GetProperty("position", flags).GetValue(container);
        }

        private static void SendSearchWindowPointer(VisualElement target, EventType type, Vector2 point, int button = 0)
        {
            var source = new Event { type = type, button = button, clickCount = 1, mousePosition = point };
            using EventBase evt = type == EventType.MouseDown ? PointerDownEvent.GetPooled(source)
                : type == EventType.MouseUp ? PointerUpEvent.GetPooled(source)
                : PointerMoveEvent.GetPooled(source);
            evt.target = target;
            target.SendEvent(evt);
        }

        [UnityTest]
        public IEnumerator SearchWindow_QuickLook_FollowsSearch_KeepsBothWindowsInteractive_AndClosesOutside()
        {
            if (Application.isBatchMode) Assert.Ignore("Native companion focus requires an interactive Editor.");
            using var assets = new Zep.Editor.TestInfrastructure.TempAssetScope();
            var firstPath = assets.CreateTextAsset("First.txt", "First preview content");
            var secondPath = assets.CreateTextAsset("Second.txt", "Second preview content");
            var state = new ZepState();
            var shell = new ZepShellView(state);
            MountSearchHost(shell);
            yield return null;
            yield return null;
            OpenSearch(shell);
            yield return new WaitForSecondsRealtime(0.2f);
            var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
            var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
            var folder = state.PrimaryPane.CurrentFolderPath;
            var unitySelection = Selection.activeObject;
            search.SetSearchText("Preview");
            search.SetResults(new[]
            {
                new ZepAssetEntry("First", firstPath, "TextAsset"),
                new ZepAssetEntry("Second", secondPath, "TextAsset")
            });
            search.FocusResults();
            using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                search.Q<ListView>("zep-search-results").SendEvent(space);
            for (var frame = 0; frame < 10; frame++) yield return null;
            Assert.That(popup != null, Is.True, "Opening Quick Look must preserve Search");
            Assert.That(ZepQuickLookWindow.IsOpenForSearch(search), Is.True);
            var quickLook = Resources.FindObjectsOfTypeAll<ZepQuickLookWindow>().Single();
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(quickLook), "Quick Look must appear in front of the Search popup");
            Assert.That(quickLook.rootVisualElement.Query<Label>().ToList().Any(label => label.text.Contains("First preview content")), Is.True);
            var quickLookRoot = quickLook.rootVisualElement.Q(className: ZepUiClasses.QuickLookRoot);
            using (var down = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow }))
                quickLookRoot.SendEvent(down);
            for (var frame = 0; frame < 3; frame++) yield return null;
            Assert.That(quickLook.rootVisualElement.Query<Label>().ToList().Any(label => label.text.Contains("Second preview content")), Is.True);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(quickLook));
            using (var up = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.UpArrow }))
                quickLookRoot.SendEvent(up);
            for (var frame = 0; frame < 3; frame++) yield return null;
            Assert.That(quickLook.rootVisualElement.Query<Label>().ToList().Any(label => label.text.Contains("First preview content")), Is.True);
            popup.Focus();
            search.FocusResults();
            using (var down = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow }))
                search.Q<ListView>("zep-search-results").SendEvent(down);
            for (var frame = 0; frame < 3; frame++) yield return null;
            Assert.That(quickLook.rootVisualElement.Query<Label>().ToList().Any(label => label.text.Contains("Second preview content")), Is.True);
            Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(folder));
            Assert.That(state.PrimaryPane.SelectedAssetPaths, Is.Empty);
            Assert.That(Selection.activeObject, Is.SameAs(unitySelection));
            quickLook.Focus();
            for (var frame = 0; frame < 5; frame++) yield return null;
            Assert.That(popup != null, Is.True, "Interacting with Quick Look must preserve Search");
            using (var escape = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }))
                quickLook.rootVisualElement.Q(className: ZepUiClasses.QuickLookRoot).SendEvent(escape);
            for (var frame = 0; frame < 5; frame++) yield return null;
            Assert.That(quickLook == null, Is.True);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(popup));
            Assert.That(search.SearchText, Is.EqualTo("Preview"));
            Assert.That(search.SelectedResult.Path, Is.EqualTo(secondPath));
            Assert.That(search.focusController.focusedElement, Is.SameAs(search.Q<ListView>("zep-search-results")));
            search.FocusResults();
            using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                search.Q<ListView>("zep-search-results").SendEvent(space);
            for (var frame = 0; frame < 5; frame++) yield return null;
            quickLook = Resources.FindObjectsOfTypeAll<ZepQuickLookWindow>().Single();
            popup.Focus();
            var field = search.Q<ToolbarSearchField>("zep-search");
            field.Focus();
            for (var frame = 0; frame < 3; frame++) yield return null;
            var queryFocus = search.focusController.focusedElement;
            field.value = "Updated query";
            for (var frame = 0; frame < 3; frame++) yield return null;
            Assert.That(quickLook == null, Is.True, "Editing the query dismisses the stale preview");
            Assert.That(search.focusController.focusedElement, Is.SameAs(queryFocus), "Dismissing a stale preview must not interrupt typing");
            var queryDeadline = EditorApplication.timeSinceStartup + 1;
            while (search.HasPendingQuery && EditorApplication.timeSinceStartup < queryDeadline) yield return null;
            Assert.That(search.HasPendingQuery, Is.False);
            search.SetResults(new[] { new ZepAssetEntry("First", firstPath, "TextAsset") });
            for (var frame = 0; frame < 3; frame++) yield return null;
            search.FocusResults();
            using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                search.Q<ListView>("zep-search-results").SendEvent(space);
            for (var frame = 0; frame < 5; frame++) yield return null;
            quickLook = Resources.FindObjectsOfTypeAll<ZepQuickLookWindow>().Single();
            quickLook.Focus();
            for (var frame = 0; frame < 3; frame++) yield return null;
            var other = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            _searchHosts.Add(other);
            other.Show();
            other.Focus();
            var deadline = EditorApplication.timeSinceStartup + 1;
            while (popup != null && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(popup == null, Is.True);
            Assert.That(quickLook == null, Is.True);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(other));
        }

        [UnityTest]
        public IEnumerator SearchWindow_OpeningShortcut_TogglesFromQueryTypeDrawerAndQuickLook()
        {
            if (Application.isBatchMode) Assert.Ignore("Native popup focus requires an interactive Editor.");
            using var assets = new Zep.Editor.TestInfrastructure.TempAssetScope();
            var path = assets.CreateTextAsset("Hero.txt", "Preview content");
            var state = new ZepState();
            var shell = new ZepShellView(state);
            MountSearchHost(shell);
            yield return null;
            yield return null;
            var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
            var folder = state.PrimaryPane.CurrentFolderPath;
            var selection = Selection.activeObject;
            var custom = ZepSearchShortcut.GetAvailableBindings().First(b => b.keyCode == KeyCode.K);
            foreach (var binding in new[] { default(KeyCombination), custom })
            {
                Assert.That(ZepSearchShortcut.TrySet(binding, out _), Is.True);
                foreach (var location in new[] { "query", "types", "quick-look" })
                {
                    SendSearchShortcut(shell, binding);
                    yield return new WaitForSecondsRealtime(0.2f);
                    var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
                    Assert.That(popup != null, Is.True, "The shortcut opens Search");
                    // Native focus settles after opening. The physical Shift
                    // release is delivered to the newly focused popup.
                    using (var release = KeyUpEvent.GetPooled(new Event { type = EventType.KeyUp, keyCode = KeyCode.LeftShift }))
                        search.SendEvent(release);
                    Assert.That(search.Q("zep-search-close"), Is.Null);
                    search.SetSearchText("Hero");
                    search.SetResults(new[] { new ZepAssetEntry("Hero", path, "TextAsset") });
                    VisualElement target = search.Q<ToolbarSearchField>("zep-search").Q<TextField>();
                    ZepQuickLookWindow quickLook = null;
                    if (location == "types")
                    {
                        using (var submit = NavigationSubmitEvent.GetPooled())
                            search.Q<Button>("zep-search-more-types").SendEvent(submit);
                        for (var frame = 0; frame < 5; frame++) yield return null;
                        target = search.Q<TextField>("zep-search-types-query");
                    }
                    else if (location == "quick-look")
                    {
                        search.FocusResults();
                        using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                            search.Q<ListView>("zep-search-results").SendEvent(space);
                        for (var frame = 0; frame < 5; frame++) yield return null;
                        quickLook = Resources.FindObjectsOfTypeAll<ZepQuickLookWindow>().Single();
                        target = quickLook.rootVisualElement.Q(className: ZepUiClasses.QuickLookRoot);
                    }
                    target.Focus();
                    Assert.That(popup != null, Is.True);
                    SendSearchShortcut(target, binding);
                    Assert.That(popup == null, Is.True, "The same shortcut closes Search from " + location);
                    Assert.That(quickLook == null, Is.True);
                    Assert.That(search.IsVisible, Is.False);
                    Assert.That(search.SearchText, Is.EqualTo("Hero"));
                    Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(folder));
                    Assert.That(state.PrimaryPane.SelectedAssetPaths, Is.Empty);
                    Assert.That(Selection.activeObject, Is.SameAs(selection));
                    Assert.That(EditorWindow.focusedWindow, Is.SameAs(_searchHosts[0]));
                    yield return null;
                }
            }
        }

        private static void SendSearchShortcut(VisualElement target, KeyCombination binding)
        {
            if (binding.Equals(default(KeyCombination)))
            {
                for (var tap = 0; tap < 2; tap++)
                {
                    using (var down = KeyDownEvent.GetPooled(new Event
                        { type = EventType.KeyDown, keyCode = KeyCode.LeftShift, modifiers = EventModifiers.Shift }))
                        target.SendEvent(down);
                    using (var up = KeyUpEvent.GetPooled(new Event { type = EventType.KeyUp, keyCode = KeyCode.LeftShift }))
                    {
                        // Opening transfers native focus before the final release.
                        var releaseTarget = EditorWindow.focusedWindow is ZepSearchWindow popup
                            ? popup.rootVisualElement.Q(className: ZepUiClasses.SearchWindow) : target;
                        releaseTarget.SendEvent(up);
                    }
                }
                return;
            }
            var modifiers = EventModifiers.None;
            if ((binding.modifiers & ShortcutModifiers.Action) != 0)
                modifiers |= Application.platform == RuntimePlatform.OSXEditor ? EventModifiers.Command : EventModifiers.Control;
            if ((binding.modifiers & ShortcutModifiers.Control) != 0) modifiers |= EventModifiers.Control;
            if ((binding.modifiers & ShortcutModifiers.Shift) != 0) modifiers |= EventModifiers.Shift;
            if ((binding.modifiers & ShortcutModifiers.Alt) != 0) modifiers |= EventModifiers.Alt;
            using var key = KeyDownEvent.GetPooled(new Event
                { type = EventType.KeyDown, keyCode = binding.keyCode, modifiers = modifiers });
            target.SendEvent(key);
        }

        [UnityTest]
        public IEnumerator SearchWindow_LosingApplicationFocus_ClosesSearchAndQuickLook_AndPreservesQuery()
        {
            if (Application.isBatchMode) Assert.Ignore("Native popup focus requires an interactive Editor.");
            using var assets = new Zep.Editor.TestInfrastructure.TempAssetScope();
            var path = assets.CreateTextAsset("Hero.txt", "Preview content");
            var state = new ZepState();
            var shell = new ZepShellView(state);
            MountSearchHost(shell);
            yield return null;
            yield return null;
            var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
            var originalFolder = state.PrimaryPane.CurrentFolderPath;
            var unitySelection = Selection.activeObject;
            var focusEvent = typeof(EditorApplication).GetField("focusChanged",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(focusEvent, Is.Not.Null);

            foreach (var withQuickLook in new[] { false, true })
            {
                OpenSearch(shell);
                yield return new WaitForSecondsRealtime(0.2f);
                var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
                Assert.That(popup != null, Is.True);
                if (withQuickLook) Assert.That(search.SearchText, Is.EqualTo("Hero"));
                search.SetSearchText("Hero");
                search.SetResults(new[] { new ZepAssetEntry("Hero", path, "TextAsset") });
                ZepQuickLookWindow quickLook = null;
                if (withQuickLook)
                {
                    search.FocusResults();
                    using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                        search.Q<ListView>("zep-search-results").SendEvent(space);
                    for (var frame = 0; frame < 5; frame++) yield return null;
                    quickLook = Resources.FindObjectsOfTypeAll<ZepQuickLookWindow>().Single();
                    Assert.That(EditorWindow.focusedWindow, Is.SameAs(quickLook));
                }
                else Assert.That(EditorWindow.focusedWindow, Is.SameAs(popup));

                // Invoke the actual subscription at the application boundary,
                // without sending synthetic focus changes to unrelated editor tools.
                var handlers = ((Action<bool>)focusEvent.GetValue(null)).GetInvocationList();
                var onFocusChanged = (Action<bool>)handlers.Single(handler => ReferenceEquals(handler.Target, popup));
                onFocusChanged(true);
                Assert.That(popup != null, Is.True, "Gaining application focus must not dismiss Search");
                onFocusChanged(false);
                Assert.That(popup == null, Is.True, "App deactivation must close even when a companion still owns editor focus");
                Assert.That(quickLook == null, Is.True);
                Assert.That(ZepQuickLookWindow.IsOpenForSearch(search), Is.False);
                Assert.That(search.IsVisible, Is.False);
                Assert.That(search.SearchText, Is.EqualTo("Hero"));
                Assert.That(search.parent, Is.SameAs(shell));
                Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(originalFolder));
                Assert.That(state.PrimaryPane.SelectedAssetPaths, Is.Empty);
                Assert.That(state.PrimaryPane.SearchText, Is.Empty);
                Assert.That(Selection.activeObject, Is.SameAs(unitySelection));
                var remaining = ((Action<bool>)focusEvent.GetValue(null))?.GetInvocationList() ?? Array.Empty<Delegate>();
                Assert.That(remaining.Any(handler => ReferenceEquals(handler.Target, popup)), Is.False,
                    "Closing must release the application-focus subscription");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator SearchWindow_LosingNativeFocus_DismissesWithoutChangingBrowsing()
        {
            if (Application.isBatchMode)
                Assert.Ignore("Native dropdown focus requires an interactive Editor; batch mode dismisses it on the next tick.");
            var state = new ZepState();
            var shell = new ZepShellView(state);
            MountSearchHost(shell);
            yield return null;
            yield return null;
            OpenSearch(shell);
            yield return new WaitForSecondsRealtime(0.2f);
            var popup = (ZepSearchWindow)GetShellField(shell, "_searchWindow");
            Assert.That(popup != null, Is.True);
            var search = (SearchBarWidget)GetShellField(shell, "_searchOverlay");
            search.Q<ToolbarSearchField>("zep-search").value = "hero";
            var originalFolder = state.PrimaryPane.CurrentFolderPath;
            var otherWindow = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            _searchHosts.Add(otherWindow);
            otherWindow.Show();
            otherWindow.Focus();
            var deadline = EditorApplication.timeSinceStartup + 1;
            while (popup != null && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(popup == null, Is.True);
            Assert.That(search.IsVisible, Is.False);
            Assert.That(search.SearchText, Is.EqualTo("hero"));
            Assert.That(state.PrimaryPane.SearchText, Is.Empty);
            Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(originalFolder));
            Assert.That(shell.Q("zep-top-bar").Q("zep-search-scope-row"), Is.Not.Null);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(otherWindow));
        }

        [Test]
        public void SearchOverlay_UsesFullQueryResults_AndKeepsScopeControlsReachable()
        {
            using var rig = ZepUiControllerRig.Build(new[]
            {
                new AssetRecord("hero", "Assets/Hero.prefab", "Hero", "GameObject"),
                new AssetRecord("script", "Assets/Hero.cs", "Hero", "MonoScript")
            }, new[] { "Assets" });
            var shell = rig.BuildShell();
            OpenSearch(shell);
            InvokeShellMethod(shell, "OnSearchTextChanged", "t:MonoScript");
            var overlay = ((SearchBarWidget)GetShellField(shell, "_searchOverlay"));
            Assert.That(overlay.ResultCount, Is.EqualTo(1));
            Assert.That(overlay.SelectedResult.Path,
                Is.EqualTo("Assets/Hero.cs"));
            Assert.That(overlay.Q("zep-search-scope-row"), Is.Not.Null);
            overlay.Hide();
            Assert.That(shell.Q("zep-top-bar").Q("zep-search-scope-row"), Is.Not.Null);
            Assert.That(rig.State.PrimaryPane.SearchText, Is.Empty);
            Assert.That(overlay.SearchText, Is.EqualTo("t:MonoScript"));
        }

        [Test]
        public void SearchOverlay_TypingClearingAndClosing_PreserveBrowsingAndSelection()
        {
            using var rig = ZepUiControllerRig.Build(new[]
            {
                new AssetRecord("hero", "Assets/Art/Hero.prefab", "Hero", "GameObject"),
                new AssetRecord("music", "Assets/Audio/Music.wav", "Music", "AudioClip")
            }, new[] { "Assets", "Assets/Art", "Assets/Audio" });
            var pane = rig.State.PrimaryPane;
            pane.TrySetFolderSelection("Assets/Audio");
            pane.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Audio/Music.wav") });
            var shell = rig.BuildShell();
            MountSearchHost(shell);
            var originalAssets = pane.ProjectedAssets;
            var paneChanges = 0;
            rig.State.PaneChanged += _ => paneChanges++;
            OpenSearch(shell);
            var overlay = ((SearchBarWidget)GetShellField(shell, "_searchOverlay"));

            foreach (var query in new[] { "Hero", "no-such-match", string.Empty, "Hero" })
            {
                InvokeShellMethod(shell, "OnSearchTextChanged", query);
                rig.Scheduler.RunAll();
                Assert.That(pane.ProjectedAssets, Is.SameAs(originalAssets));
                Assert.That(pane.SearchText, Is.Empty);
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/Audio"));
                Assert.That(pane.SelectedFolderPath.Value, Is.EqualTo("Assets/Audio"));
                Assert.That(pane.SelectedAssetPaths,
                    Is.EqualTo(new[] { AssetPath.From("Assets/Audio/Music.wav") }));
            }

            overlay.Hide();
            Assert.That(paneChanges, Is.Zero, "Searching and dismissal must not publish pane changes.");
            OpenSearch(shell);
            Assert.That(overlay.SearchText, Is.EqualTo("Hero"));
            // A later pane refresh must not overwrite the dialog's independent query.
            pane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
            Assert.That(overlay.SearchText, Is.EqualTo("Hero"));
        }

        [TestCase("Player")]
        [TestCase("plaeyr")]
        public void SearchOverlay_ScopeFiltersResultsWithoutChangingThePane(string query)
        {
            using var rig = ZepUiControllerRig.Build(new[]
            {
                new AssetRecord("hero", "Assets/Player.prefab", "Player", "GameObject"),
                new AssetRecord("package-hero", "Packages/demo/Player.prefab", "Player", "GameObject")
            }, new[] { "Assets", "Packages", "Packages/demo" });
            var shell = rig.BuildShell();
            var pane = rig.State.PrimaryPane;
            var originalAssets = pane.ProjectedAssets;
            OpenSearch(shell);
            InvokeShellMethod(shell, "OnSearchTextChanged", query);
            RaiseTopBarEvent<Action<ZepSearchScope>>(shell, SearchScopeSelected)
                .Invoke(ZepSearchScope.Packages);

            var overlay = ((SearchBarWidget)GetShellField(shell, "_searchOverlay"));
            Assert.That(overlay.ResultCount, Is.EqualTo(1));
            Assert.That(overlay.SelectedResult.Path,
                Does.Contain("Packages/demo/Player.prefab"));
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
            Assert.That(pane.ProjectedAssets, Is.SameAs(originalAssets));
            overlay.Hide();
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
        }

        [Test]
        public void SearchOverlay_SelectingFolder_ClosesAndHighlightsItInItsParent()
        {
            using var rig = ZepUiControllerRig.Build(Array.Empty<AssetRecord>(),
                new[] { "Assets", "Assets/Art", "Assets/Audio" });
            var shell = rig.BuildShell();
            var auxiliary = rig.State.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Audio"))).NewPane;
            OpenSearch(shell);
            InvokeShellMethod(shell, "OnSearchTextChanged", "Art");
            var overlay = ((SearchBarWidget)GetShellField(shell, "_searchOverlay"));
            typeof(SearchBarWidget).GetMethod("OpenSuggestion", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(overlay, new object[] { 0 });

            Assert.That(overlay.IsVisible, Is.False);
            Assert.That(rig.State.PrimaryPane.CurrentFolderPath.Value, Is.EqualTo("Assets"));
            Assert.That(rig.State.PrimaryPane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From("Assets/Art") }));
            Assert.That(rig.State.ActivePane, Is.SameAs(rig.State.PrimaryPane));
            Assert.That(auxiliary.CurrentFolderPath.Value, Is.EqualTo("Assets/Audio"));
        }

        // The shortcut controller hands the overlay every key while it reports
        // visible, so "visible" has to mean the logical state, not the one the
        // 140 ms dismiss fade is still rendering.
        [Test]
        public void PromptOverlay_StopsReportingVisibleTheMomentItIsDismissed()
        {
            var overlay = new ZepPromptOverlayWidget();
            overlay.Show("Rename", "Name", "Foo", "Rename", _ => { });
            Assert.That(overlay.IsVisible, Is.True);

            overlay.Hide();

            Assert.That(overlay.IsVisible, Is.False);
            Assert.That(
                overlay.style.display.value,
                Is.EqualTo(DisplayStyle.Flex),
                "precondition: display stays Flex until the fade completes — reading it is what swallowed keys");
        }

        [Test]
        public void PromptOverlay_ConfirmVariantAlsoTracksLogicalVisibility()
        {
            var overlay = new ZepPromptOverlayWidget();
            Assert.That(overlay.IsVisible, Is.False);

            overlay.ShowConfirm("Delete", "Are you sure?", "Delete", () => { });
            Assert.That(overlay.IsVisible, Is.True);

            overlay.Hide();
            Assert.That(overlay.IsVisible, Is.False);
        }

        [Test]
        public void AssetList_UsesUnityAlternatingRowBackgroundsAll()
        {
            // Widgets are created lazily per view mode, so pin the stored view
            // mode to List — otherwise this test would depend on whatever mode
            // the developer last used.
            const string viewModeKey = ZepThemePreferences.AssetViewModePreferenceKey;
            var hadPreference = EditorPrefs.HasKey(viewModeKey);
            var previousPreference = EditorPrefs.GetString(viewModeKey, string.Empty);
            try
            {
                EditorPrefs.SetString(viewModeKey, AssetViewMode.List.ToString());
                var shell = new ZepShellView(ZepPreviewSeed.Build());
                var assetList = shell.Q<ListView>("zep-asset-list");

                // Finder-style striping is Unity's built-in
                // AlternatingRowBackground.All: the stripe is the item wrapper's
                // background (alignment by construction) and ListView fills the
                // empty viewport tail with alternating filler rows. `ContentOnly`
                // would stop striping at the last row; `None` would drop the
                // stripes entirely (the old hand-painted bands are gone).
                Assert.That(assetList, Is.Not.Null);
                Assert.That(assetList.showAlternatingRowBackgrounds, Is.EqualTo(AlternatingRowBackground.All));
            }
            finally
            {
                if (hadPreference)
                {
                    EditorPrefs.SetString(viewModeKey, previousPreference);
                }
                else
                {
                    EditorPrefs.DeleteKey(viewModeKey);
                }
            }
        }

        [Test]
        public void AssetPaneResponsiveClasses_CollapseColumnsByPriority()
        {
            // Breakpoints live in AssetsAreaConstants (compact=820, narrow=700,
            // tight=560). AssetsAreaController forwards ApplyResponsiveWidth to
            // the mounted features, which toggle the layout classes on
            // themselves — assert on the browser feature element.
            var shell = new ZepShellView(ZepPreviewSeed.Build());
            var assetsArea = shell.Q<AssetsAreaController>("zep-assets-area");
            Assert.That(assetsArea, Is.Not.Null);
            var browserFeature = shell.Q<VisualElement>("zep-browser-feature");
            Assert.That(browserFeature, Is.Not.Null);

            assetsArea.ApplyResponsiveWidth(780f);

            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetCompact), Is.True);
            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetNarrow),  Is.False);
            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetTight),   Is.False);

            assetsArea.ApplyResponsiveWidth(680f);

            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetCompact), Is.True);
            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetNarrow),  Is.True);
            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetTight),   Is.False);

            assetsArea.ApplyResponsiveWidth(520f);

            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetCompact), Is.True);
            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetNarrow),  Is.True);
            Assert.That(browserFeature.ClassListContains(ZepUiClasses.LayoutAssetTight),   Is.True);
        }

        [Test]
        public void TryBuildAssetDragPayload_IncludesDragPathAndObjectReference()
        {
            using var scope = new Zep.Editor.TestInfrastructure.TempAssetScope();
            var assetPath = scope.CreateTextAsset("DragPayload.txt", "drag-payload-fixture");
            Assert.That(AssetDatabase.LoadMainAssetAtPath(assetPath), Is.Not.Null);

            var started = AssetDragPrepareInteraction.TryBuildAssetDragPayload(
                new[] { assetPath }, out var objectReferences, out var dragPaths);

            Assert.That(started, Is.True);
            Assert.That(dragPaths, Is.EqualTo(new[] { assetPath }));
            Assert.That(objectReferences, Is.Not.Null);
            Assert.That(objectReferences.Length, Is.GreaterThan(0));
        }

        [Test]
        public void TryBuildAssetDragPayload_RejectsNonProjectPaths()
        {
            var started = AssetDragPrepareInteraction.TryBuildAssetDragPayload(
                new[] { "/tmp/outside.txt" }, out var objectReferences, out var dragPaths);

            Assert.That(started, Is.False);
            Assert.That(dragPaths, Is.Empty);
            Assert.That(objectReferences, Is.Empty);
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.Backspace, false, false)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.Backspace, true, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.Delete, false, false)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.Delete, true, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.Delete, false, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.Delete, true, false)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.Backspace, false, false)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.Delete, false, true)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.Backspace, false, false)]
        public void DeleteShortcut_MatchesPlatformConventions(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool expected)
        {
            var result = ZepShortcutController.IsDeleteShortcut(platform, keyCode, actionKey);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(RuntimePlatform.OSXEditor, true)]
        [TestCase(RuntimePlatform.OSXPlayer, true)]
        [TestCase(RuntimePlatform.WindowsEditor, false)]
        [TestCase(RuntimePlatform.LinuxEditor, false)]
        public void ReturnBeginsRename_OnlyOnMac(RuntimePlatform platform, bool expected)
        {
            Assert.That(ZepShortcutController.ReturnBeginsRename(platform), Is.EqualTo(expected));
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.UpArrow, true, false, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.UpArrow, false, true, false)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.UpArrow, false, true, true)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.UpArrow, false, true, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.UpArrow, true, false, false)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.DownArrow, true, false, false)]
        public void NavigateToParentChord_MatchesPlatformConventions(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool altKey, bool expected)
        {
            var result = ZepShortcutController.IsNavigateToParentChord(platform, keyCode, actionKey, altKey);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.DownArrow, true, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.DownArrow, false, false)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.DownArrow, true, false)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.DownArrow, true, false)]
        public void OpenSelectionChord_IsCommandDownOnMacOnly(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool expected)
        {
            var result = ZepShortcutController.IsOpenSelectionChord(platform, keyCode, actionKey);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.Z, true, true, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.Z, true, true, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.Y, true, false, true)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.Y, true, false, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.Y, true, false, false)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.Y, false, false, false)]
        public void RedoChord_AcceptsCtrlYOffMac(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool shiftKey, bool expected)
        {
            var result = ZepShortcutController.IsRedoChord(platform, keyCode, actionKey, shiftKey);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.LeftArrow, false, true, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.LeftArrow, false, true, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.LeftBracket, true, false, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.LeftBracket, true, false, false)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.LeftArrow, false, false, false)]
        public void HistoryBackChord_MatchesPlatformConventions(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool altKey, bool expected)
        {
            var result = ZepShortcutController.IsHistoryBackChord(platform, keyCode, actionKey, altKey);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.RightArrow, false, true, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.RightBracket, true, false, true)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.RightBracket, true, false, false)]
        public void HistoryForwardChord_MatchesPlatformConventions(RuntimePlatform platform, KeyCode keyCode, bool actionKey, bool altKey, bool expected)
        {
            var result = ZepShortcutController.IsHistoryForwardChord(platform, keyCode, actionKey, altKey);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("SelectAll", true)]
        [TestCase("Duplicate", true)]
        [TestCase("SoftDelete", true)]
        [TestCase("Delete", true)]
        [TestCase("Copy", false)]
        [TestCase("Paste", false)]
        [TestCase("Find", false)]
        public void HandledCommand_CoversExactlyThePanelCommands(string commandName, bool expected)
        {
            Assert.That(ZepShortcutController.IsHandledCommand(commandName), Is.EqualTo(expected));
        }

        // The panel declines Undo/Redo while the editor is compiling or updating.
        // Consuming the key anyway stopped Unity's own Edit > Undo from running,
        // so the keystroke did nothing at all rather than merely bypassing the
        // panel. A declined chord must fall through.
        [TestCase(KeyCode.Z, false)]
        [TestCase(KeyCode.Z, true)]
        public void UndoRedoChord_IsLeftUnconsumedWhenThePanelDeclinesIt(KeyCode keyCode, bool shift)
        {
            var actionModifier = ZepShortcutController.IsMacPlatform(Application.platform)
                ? EventModifiers.Command
                : EventModifiers.Control;
            var modifiers = shift ? actionModifier | EventModifiers.Shift : actionModifier;

            var accepting = new TrackingShortcutHost { UndoRedoResult = true };
            var acceptingController = new ZepShortcutController(accepting);
            using (var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = keyCode,
                modifiers = modifiers
            }))
            {
                acceptingController.HandleKeyDown(evt);

                Assert.That(accepting.UndoRequestedCount + accepting.RedoRequestedCount, Is.EqualTo(1));
                Assert.That(evt.isPropagationStopped, Is.True,
                    "a handled chord stays consumed so Unity does not act twice");
            }

            var declining = new TrackingShortcutHost { UndoRedoResult = false };
            var decliningController = new ZepShortcutController(declining);
            using (var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = keyCode,
                modifiers = modifiers
            }))
            {
                decliningController.HandleKeyDown(evt);

                Assert.That(declining.UndoRequestedCount + declining.RedoRequestedCount, Is.EqualTo(1));
                Assert.That(evt.isPropagationStopped, Is.False,
                    "a declined chord must reach Unity's own Undo/Redo");
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void FindShortcut_DoesNotOpenSearchOrConsumeKey(bool customBinding, bool textInput)
        {
            if (customBinding)
            {
                var binding = ZepSearchShortcut.GetAvailableBindings().First(b => b.keyCode == KeyCode.K);
                Assert.That(ZepSearchShortcut.TrySet(binding, out _), Is.True);
            }
            var host = new TrackingShortcutHost();
            var controller = new ZepShortcutController(host);
            var target = textInput ? new TextField().Q("unity-text-input") : new VisualElement();
            var actionModifier = ZepShortcutController.IsMacPlatform(Application.platform)
                ? EventModifiers.Command
                : EventModifiers.Control;

            using (var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.F,
                modifiers = actionModifier
            }))
            {
                evt.target = target;
                controller.HandleKeyDown(evt);

                Assert.That(host.FocusSearchCount, Is.Zero);
                Assert.That(evt.isPropagationStopped, Is.False);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void FindCommand_DoesNotOpenSearchOrConsumeCommand(bool customBinding, bool textInput)
        {
            if (customBinding)
            {
                var binding = ZepSearchShortcut.GetAvailableBindings().First(b => b.keyCode == KeyCode.K);
                Assert.That(ZepSearchShortcut.TrySet(binding, out _), Is.True);
            }
            var host = new TrackingShortcutHost();
            var controller = new ZepShortcutController(host);
            var target = textInput ? new TextField().Q("unity-text-input") : new VisualElement();

            using (var evt = ValidateCommandEvent.GetPooled(new Event
            {
                type = EventType.ValidateCommand,
                commandName = "Find"
            }))
            {
                evt.target = target;
                controller.HandleValidateCommand(evt);
                Assert.That(evt.isPropagationStopped, Is.False);
            }
            using (var evt = ExecuteCommandEvent.GetPooled(new Event
            {
                type = EventType.ExecuteCommand,
                commandName = "Find"
            }))
            {
                evt.target = target;
                controller.HandleExecuteCommand(evt);

                Assert.That(host.FocusSearchCount, Is.Zero);
                Assert.That(evt.isPropagationStopped, Is.False);
            }
        }

        [Test]
        public void Teardown_WithPreviewUpgradesStillQueued_ReleasesTheEditorUpdateHook()
        {
            var iconRepository = new UnityZepIconRepository();
            var shell = new ZepShellView(
                new ZepState(),
                iconRepository: iconRepository);
            // A path whose preview never arrives is exactly the case the poll
            // cannot resolve on its own: it unsubscribes only once the queue
            // drains, so without a teardown the hook outlives the panel.
            var queuedPath = "Assets/ZepPollTeardown_"
                + Guid.NewGuid().ToString("N") + ".mat";
            iconRepository.GetAssetIcon(queuedPath, "Material");
            Assert.That(
                IsPollingEditorUpdate(iconRepository),
                Is.True,
                "a queued preview upgrade must arm the poll");

            shell.Teardown();

            Assert.That(
                IsPollingEditorUpdate(iconRepository),
                Is.False,
                "panel teardown must release the editor update hook");

            // The pending set has to be cleared with the queue: a path still in
            // it would never re-queue, so its upgrade would be lost to a poll
            // that is no longer running.
            iconRepository.GetAssetIcon(queuedPath, "Material");
            Assert.That(
                IsPollingEditorUpdate(iconRepository),
                Is.True,
                "a later icon request must re-arm the poll");

            iconRepository.Teardown();
        }

        private static bool IsPollingEditorUpdate(UnityZepIconRepository iconRepository)
        {
            var subscribers = EditorApplication.update;
            if (subscribers == null)
            {
                return false;
            }

            var invocationList = subscribers.GetInvocationList();
            for (var i = 0; i < invocationList.Length; i++)
            {
                if (ReferenceEquals(invocationList[i].Target, iconRepository))
                {
                    return true;
                }
            }

            return false;
        }

        // The type-filter button is a focusable VisualElement whose only
        // keyboard activation is its own KeyDown handler; the shell's global
        // trickle-down handler runs first and must yield these keys to it.
        [TestCase(KeyCode.Space)]
        [TestCase(KeyCode.Return)]
        [TestCase(KeyCode.KeypadEnter)]
        public void OnKeyDown_YieldsActivationKeysToTheFocusedTypeFilterButton(KeyCode keyCode)
        {
            var shell = new ZepShellView(new ZepState());
            var typeFilterButton = shell.Q<VisualElement>("zep-type-filter-button");
            Assert.That(typeFilterButton, Is.Not.Null);

            using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = keyCode });
            evt.target = typeFilterButton;

            shell.OnKeyDown(evt);

            Assert.That(evt.isPropagationStopped, Is.False,
                "The global handler must leave activation keys for the button's own KeyDown handler.");
        }

        [Test]
        public void OnKeyDown_StillConsumesReturnWhenTheTypeFilterButtonIsNotTheTarget()
        {
            var shell = new ZepShellView(new ZepState());

            using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return });
            evt.target = shell;

            shell.OnKeyDown(evt);

            Assert.That(evt.isPropagationStopped, Is.True,
                "Return anywhere else in the panel is the open/rename shortcut and must be consumed.");
        }

        [Test]
        public void OnKeyDown_LeavesTypedCharactersToTheFocusedTextInput()
        {
            var shell = new ZepShellView(new ZepState());
            // Unity 6 TextFields delegate focus to their inner input element,
            // so key events target that element — the guard must hold for the
            // real key target, or type-ahead eats every character typed into
            // an inline rename.
            var field = new TextField();
            var innerInput = field.Q("unity-text-input");
            Assert.That(innerInput, Is.Not.Null);

            using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, character = 'a' });
            evt.target = innerInput;

            shell.OnKeyDown(evt);

            Assert.That(evt.isPropagationStopped, Is.False,
                "Printable keys inside a text input must reach the field, not the type-ahead buffer.");
        }

        [Test]
        public void IsTextInputTarget_CoversFieldAndInnerInputButNotOtherElements()
        {
            var field = new TextField();

            Assert.That(ZepShortcutController.IsTextInputTarget(field), Is.True);
            Assert.That(ZepShortcutController.IsTextInputTarget(field.Q("unity-text-input")), Is.True);
            Assert.That(ZepShortcutController.IsTextInputTarget(new VisualElement()), Is.False);
            Assert.That(ZepShortcutController.IsTextInputTarget(null), Is.False);
        }

        // After an inline edit ends its row is hidden, not removed — and the
        // panel can keep dispatching keys to the hidden field (stale focus
        // leaf). A hidden field must read as stale, never as a text input.
        [Test]
        public void IsTextInputTarget_IgnoresHiddenTextInputs()
        {
            var container = new VisualElement();
            var field = new TextField();
            container.Add(field);
            container.style.display = DisplayStyle.None;

            Assert.That(ZepShortcutController.IsTextInputTarget(field), Is.False);
            Assert.That(ZepShortcutController.IsStaleTextInputTarget(field), Is.True);
            Assert.That(ZepShortcutController.IsStaleTextInputTarget(field.Q("unity-text-input")), Is.True);

            // A live (shown) field is a text input, not a stale one.
            var shownField = new TextField();
            Assert.That(ZepShortcutController.IsStaleTextInputTarget(shownField), Is.False);
        }

        // Fresh folder tree with deterministic expansion state (SessionState
        // persists across test runs within an editor session, so clear it):
        // Assets (expanded) > Art > Props, plus Assets/Scripts.
        private static FolderTreeWidget CreateFolderTree(string selectedFolderPath)
        {
            SessionState.EraseString("Zep.FolderTreeExpanded.Assets");
            var widget = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(new[]
                {
                    new ZepFolderEntry("Assets", "Assets", 0, false),
                    new ZepFolderEntry("Art", "Assets/Art", 1, false),
                    new ZepFolderEntry("Props", "Assets/Art/Props", 2, false),
                    new ZepFolderEntry("Scripts", "Assets/Scripts", 1, false),
                }));
            widget.Apply(
                currentFolderPath: "Assets",
                selectedFolderPath: selectedFolderPath);
            // The tree does not own the selection: it publishes the folder it
            // wants and the shell republishes it. Without that round trip a
            // keyboard move would leave SelectedFolderEntry on the old folder,
            // exactly as it would in the panel.
            widget.FolderClicked += path => widget.Apply(
                currentFolderPath: path,
                selectedFolderPath: path);
            return widget;
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

        private sealed class SidebarViewportTestWindow : EditorWindow
        {
        }

        private static void SendTreeKey(FolderTreeWidget tree, KeyCode keyCode)
        {
            using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = keyCode });
            tree.HandleListKeyDown(evt);
        }

        [Test]
        public void FolderTree_LazyQueryMaterializesOnlyExpandedAndRevealedChildren()
        {
            SessionState.EraseString("Zep.FolderTreeExpanded.Assets");
            var queryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var assetsChildren = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Art", "Assets/Art", 1, false),
                new ZepFolderEntry("Scripts", "Assets/Scripts", 1, false),
            };
            IReadOnlyList<ZepFolderEntry> Query(string parent)
            {
                parent ??= string.Empty;
                queryCounts[parent] = queryCounts.TryGetValue(parent, out var count)
                    ? count + 1
                    : 1;
                if (string.IsNullOrEmpty(parent))
                {
                    return new[]
                    {
                        new ZepFolderEntry("Assets", "Assets", 0, false),
                    };
                }

                if (string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return assetsChildren;
                }

                if (string.Equals(parent, "Assets/Art", StringComparison.OrdinalIgnoreCase))
                {
                    return new[]
                    {
                        new ZepFolderEntry("Props", "Assets/Art/Props", 2, false),
                    };
                }

                return Array.Empty<ZepFolderEntry>();
            }

            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                Query);
            tree.Apply("Assets", "Assets/Art");

            Assert.That(queryCounts.Keys, Is.EquivalentTo(new[] { string.Empty, "Assets" }));
            Assert.That(queryCounts.ContainsKey("Assets/Art"), Is.False,
                "a collapsed child must not enumerate descendants");

            SendTreeKey(tree, KeyCode.RightArrow);

            Assert.That(queryCounts["Assets/Art"], Is.EqualTo(1));
            Assert.That(tree.Q<ListView>().itemsSource.Count, Is.EqualTo(4));

            assetsChildren.Add(new ZepFolderEntry(
                "Tests",
                "Assets/Tests",
                1,
                false));
            var delta = new AssetCatalogDelta(previousRecordVersion: 0);
            delta.AffectedParentFolders.Add("Assets");
            tree.ApplyCatalogDelta(delta);

            Assert.That(queryCounts["Assets"], Is.EqualTo(2));
            Assert.That(queryCounts["Assets/Art"], Is.EqualTo(1),
                "an Assets sibling delta must not requery an unrelated expanded child");
        }

        [Test]
        public void FolderTree_LazyRevealPublishesLineageBeforeCompleteSiblingQuery()
        {
            SessionState.EraseString("Zep.FolderTreeExpanded.Assets");
            var queryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<string, ZepFolderEntry>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Assets"] = new ZepFolderEntry("Assets", "Assets", 0, false),
                ["Assets/Phase01Fixture"] = new ZepFolderEntry(
                    "Phase01Fixture",
                    "Assets/Phase01Fixture",
                    1,
                    false),
                ["Assets/Phase01Fixture/F00001"] = new ZepFolderEntry(
                    "F00001",
                    "Assets/Phase01Fixture/F00001",
                    2,
                    false),
            };

            IReadOnlyList<ZepFolderEntry> Query(string parent)
            {
                parent ??= string.Empty;
                queryCounts[parent] = queryCounts.TryGetValue(parent, out var count)
                    ? count + 1
                    : 1;
                if (string.IsNullOrEmpty(parent))
                {
                    return new[] { entries["Assets"] };
                }

                if (string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return new[] { entries["Assets/Phase01Fixture"] };
                }

                if (string.Equals(
                    parent,
                    "Assets/Phase01Fixture",
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new AssertionException(
                        "the complete sibling query must be deferred past navigation publication");
                }

                return Array.Empty<ZepFolderEntry>();
            }

            ZepFolderEntry QueryEntry(string path) =>
                entries.TryGetValue(path, out var entry) ? entry : null;

            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                Query,
                QueryEntry);
            tree.Apply("Assets", "Assets");
            tree.Apply(
                "Assets/Phase01Fixture/F00001",
                "Assets/Phase01Fixture/F00001");

            Assert.That(
                queryCounts.Keys,
                Is.EquivalentTo(new[] { string.Empty, "Assets" }));
            Assert.That(
                tree.SelectedFolderEntry?.Path,
                Is.EqualTo("Assets/Phase01Fixture/F00001"));
            Assert.That(tree.Q<ListView>().itemsSource.Count, Is.EqualTo(3));
        }

        // Sidebar hydration walks every expanded branch, and a catalog delta
        // re-walks one parent. Both used to reconcile against the flat
        // materialized list, which made restoring a deep expansion quadratic.
        // The equivalence that matters is the projection: incremental
        // hydration must publish exactly the tree a widget that hydrated the
        // same source from scratch publishes.
        [Test]
        public void FolderTree_LazyHydrationOfADeepExpansion_MatchesAFromScratchWidget()
        {
            const int groupCount = 500;
            const int leavesPerGroup = 3;
            var groupChildren = new Dictionary<string, List<ZepFolderEntry>>(
                StringComparer.OrdinalIgnoreCase);
            var assetsChildren = new List<ZepFolderEntry>(groupCount);
            var expandedPaths = new List<string> { "Assets" };
            for (var group = 0; group < groupCount; group++)
            {
                var groupName = "G" + group.ToString("D4");
                var groupPath = "Assets/" + groupName;
                assetsChildren.Add(new ZepFolderEntry(groupName, groupPath, 1, false));
                expandedPaths.Add(groupPath);
                var leaves = new List<ZepFolderEntry>(leavesPerGroup);
                for (var leaf = 0; leaf < leavesPerGroup; leaf++)
                {
                    var leafName = "L" + leaf;
                    leaves.Add(new ZepFolderEntry(
                        leafName,
                        groupPath + "/" + leafName,
                        2,
                        false));
                }

                groupChildren[groupPath] = leaves;
            }

            var savedExpansion = string.Join("\n", expandedPaths);

            IReadOnlyList<ZepFolderEntry> Query(string parent)
            {
                parent ??= string.Empty;
                if (parent.Length == 0)
                {
                    return new[] { new ZepFolderEntry("Assets", "Assets", 0, false) };
                }

                if (string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return assetsChildren;
                }

                return groupChildren.TryGetValue(parent, out var leaves)
                    ? (IReadOnlyList<ZepFolderEntry>)leaves
                    : Array.Empty<ZepFolderEntry>();
            }

            FolderTreeWidget BuildHydratedTree()
            {
                SessionState.SetString(AssetsTreeExpansionKey, savedExpansion);
                var built = new FolderTreeWidget(
                    "Assets",
                    new UnityZepIconRepository(),
                    Query);
                built.Apply("Assets", "Assets");
                return built;
            }

            var hydrationWatch = System.Diagnostics.Stopwatch.StartNew();
            var incremental = BuildHydratedTree();
            hydrationWatch.Stop();

            var expectedVisibleCount = 1 + groupCount + (groupCount * leavesPerGroup);
            Assert.That(incremental.VisibleFolderCount, Is.EqualTo(expectedVisibleCount));

            // One catalog delta on a single expanded parent: a new leaf
            // appears and an existing one disappears, so the reconcile has to
            // both add and remove inside that bucket.
            const string mutatedGroupPath = "Assets/G0250";
            var mutatedLeaves = groupChildren[mutatedGroupPath];
            mutatedLeaves.RemoveAt(0);
            mutatedLeaves.Add(new ZepFolderEntry(
                "L9",
                mutatedGroupPath + "/L9",
                2,
                false));
            var delta = new AssetCatalogDelta(previousRecordVersion: 0);
            delta.AffectedParentFolders.Add(mutatedGroupPath);
            var deltaWatch = System.Diagnostics.Stopwatch.StartNew();
            incremental.ApplyCatalogDelta(delta);
            deltaWatch.Stop();

            var fromScratch = BuildHydratedTree();
            TestContext.WriteLine(
                $"hydration({expectedVisibleCount} folders, {groupCount + 1} expanded)="
                + $"{hydrationWatch.Elapsed.TotalMilliseconds:F1}ms "
                + $"delta={deltaWatch.Elapsed.TotalMilliseconds:F3}ms");

            Assert.That(
                VisibleFolderPaths(incremental),
                Is.EqualTo(VisibleFolderPaths(fromScratch)),
                "incremental hydration must publish the same tree a from-scratch hydration does");
            Assert.That(
                VisibleFolderPaths(incremental),
                Does.Contain(mutatedGroupPath + "/L9").And.Not.Contains(mutatedGroupPath + "/L0"));
        }

        private static List<string> VisibleFolderPaths(FolderTreeWidget tree)
        {
            var source = tree.Q<ListView>().itemsSource;
            var paths = new List<string>(source.Count);
            for (var i = 0; i < source.Count; i++)
            {
                paths.Add(((ZepFolderEntry)source[i]).Path);
            }

            return paths;
        }

        [UnityTest]
        public IEnumerator FolderTree_LargeExpandedTree_KeepsTheBoundWindowViewportSized()
        {
            const int childCount = 5000;
            const float viewportHeight = 200f;
            SessionState.EraseString(AssetsTreeExpansionKey);

            var folders = new List<ZepFolderEntry>(childCount + 1)
            {
                new ZepFolderEntry("Assets", "Assets", 0, false)
            };
            for (var i = 0; i < childCount; i++)
            {
                var name = "Folder" + i.ToString("D5");
                folders.Add(new ZepFolderEntry(
                    name,
                    "Assets/" + name,
                    1,
                    false));
            }

            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(folders));
            tree.Apply("Assets", "Assets");
            var sidebarScroll = new ScrollView(ScrollViewMode.Vertical);
            sidebarScroll.style.height = viewportHeight;
            sidebarScroll.Add(tree);
            var window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            window.position = new Rect(40f, 40f, 320f, 320f);
            window.Show();
            window.rootVisualElement.Add(sidebarScroll);
            try
            {
                yield return null;
                yield return null;

                var list = tree.Q<ListView>("zep-folder-list-assets");
                var nestedScroll = list.Q<ScrollView>();
                Assert.That(
                    nestedScroll.verticalScrollerVisibility,
                    Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(
                    nestedScroll.nestedInteractionKind,
                    Is.EqualTo(ScrollView.NestedInteractionKind.ForwardScrolling));
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(childCount + 1));
                Assert.That(
                    list.itemsSource.Count,
                    Is.LessThanOrEqualTo(16),
                    "a 200px viewport needs about eight 25px rows plus bounded overscan, not the whole tree");
                Assert.That(tree.BoundFolderCount, Is.EqualTo(list.itemsSource.Count));
                Assert.That(
                    tree.RowBindCount,
                    Is.LessThanOrEqualTo(256),
                    "initial row binding may repeat across layout passes, but must remain viewport-bound");
                TestContext.WriteLine(
                    $"beforeScroll treeHeight={tree.layout.height} "
                    + $"listHeight={list.layout.height} "
                    + $"tailHeight={tree.Q<VisualElement>("zep-folder-tree-trailing-spacer").layout.height} "
                    + $"scrollHigh={sidebarScroll.verticalScroller.highValue}");

                var bindsBeforeScroll = tree.RowBindCount;
                const string targetPath = "Assets/Folder02500";
                tree.Apply(targetPath, targetPath);
                // The reveal is a scheduled retry, and scheduled items tick on
                // repaint, which batchmode does not do on its own. A sibling
                // switch on the lazy path is selection-only (no RefreshList),
                // so nothing else dirties the window: drive the repaints the
                // editor would, the way the split and inline-edit tests do.
                for (var attempt = 0;
                    attempt < 30 && sidebarScroll.scrollOffset.y <= 0f;
                    attempt++)
                {
                    window.Repaint();
                    yield return null;
                }
                yield return null;

                Assert.That(sidebarScroll.scrollOffset.y, Is.GreaterThan(0f));
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(childCount + 1));
                Assert.That(tree.BoundFolderCount, Is.LessThanOrEqualTo(16));
                Assert.That(
                    list.itemsSource.Cast<ZepFolderEntry>().Any(
                        entry => string.Equals(entry.Path, targetPath, StringComparison.Ordinal)),
                    Is.True,
                    "the bounded source must track the rows framed by the outer scroll owner");
                Assert.That(
                    tree.RowBindCount - bindsBeforeScroll,
                    Is.GreaterThan(0),
                    "moving the outer owner must rebind the recycled row window");
                Assert.That(
                    tree.RowBindCount - bindsBeforeScroll,
                    Is.LessThanOrEqualTo(256),
                    "scrolling to the middle must recycle a viewport window, not bind all 5,001 rows");
                TestContext.WriteLine(
                    $"visible={tree.VisibleFolderCount} "
                    + $"bound={tree.BoundFolderCount} "
                    + $"initialBinds={bindsBeforeScroll} "
                    + $"scrollBinds={tree.RowBindCount - bindsBeforeScroll}");
            }
            finally
            {
                window.Close();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void FolderTree_RightArrowExpandsTheSelectedFolder()
        {
            var tree = CreateFolderTree("Assets/Art");
            var list = tree.Q<ListView>();
            Assert.That(list.itemsSource.Count, Is.EqualTo(3), "Art starts collapsed: Assets, Art, Scripts.");

            SendTreeKey(tree, KeyCode.RightArrow);

            Assert.That(list.itemsSource.Count, Is.EqualTo(4), "Expanding Art must reveal Props.");
            Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets/Art"), "Expanding must not move the selection.");
        }

        [Test]
        public void FolderTree_RightArrowOnExpandedFolderStepsIntoFirstChild()
        {
            var tree = CreateFolderTree("Assets/Art");

            SendTreeKey(tree, KeyCode.RightArrow);
            SendTreeKey(tree, KeyCode.RightArrow);

            Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets/Art/Props"));
        }

        [Test]
        public void FolderTree_LeftArrowCollapsesTheExpandedFolder()
        {
            var tree = CreateFolderTree("Assets/Art");
            var list = tree.Q<ListView>();
            SendTreeKey(tree, KeyCode.RightArrow);
            Assert.That(list.itemsSource.Count, Is.EqualTo(4));

            SendTreeKey(tree, KeyCode.LeftArrow);

            Assert.That(list.itemsSource.Count, Is.EqualTo(3), "Collapsing Art must hide Props.");
            Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets/Art"));
        }

        [Test]
        public void FolderTree_LeftArrowOnCollapsedFolderJumpsToParent()
        {
            var tree = CreateFolderTree("Assets/Art");

            SendTreeKey(tree, KeyCode.LeftArrow);

            Assert.That(tree.SelectedFolderEntry?.Path, Is.EqualTo("Assets"));
        }

        [Test]
        public void FolderTree_HeightTracksItsVisibleRows()
        {
            var tree = CreateFolderTree("Assets/Art");
            var list = tree.Q<ListView>();
            Assert.That(list.style.height.value.value, Is.EqualTo(3 * list.fixedItemHeight));

            SendTreeKey(tree, KeyCode.RightArrow);

            Assert.That(list.style.height.value.value, Is.EqualTo(4 * list.fixedItemHeight));
        }

        [Test]
        public void OnKeyDown_TreatsKeysAimedAtHiddenTextInputAsPanelKeys()
        {
            var shell = new ZepShellView(new ZepState());
            var container = new VisualElement();
            var field = new TextField();
            container.Add(field);
            container.style.display = DisplayStyle.None;
            shell.Add(container);

            using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return });
            evt.target = field.Q("unity-text-input");

            shell.OnKeyDown(evt);

            Assert.That(evt.isPropagationStopped, Is.True,
                "Keys aimed at a hidden (stale-leaf) text input must be handled as panel shortcuts.");
        }

        // ── Rebuilt from the pre-incident shell-test quarantine ──
        // The originals were written against the removed IZepViewModel /
        // IZepCommandHandler seams; the behaviours they covered are all
        // still live, so they run against the concrete ZepState and a
        // real ZepController (ZepUiControllerRig).

        [Test]
        public void ControlDigitShortcut_TogglesFavoriteTagsThroughTheController()
        {
            if (!ZepShortcutController.IsMacPlatform(Application.platform))
            {
                Assert.Ignore("Finder tag shortcuts are macOS-only.");
            }

            const string iconPath = "Assets/Icon.png";
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("icon-guid", iconPath, "Icon", "Texture2D") },
                new[] { "Assets", "Packages" });
            rig.Gateway.SeedTagIds(iconPath, "red");

            var red = new ZepTag("red", "Red", ZepTagColor.Red, true);
            var blue = new ZepTag("blue", "Blue", ZepTagColor.Blue, true);
            var host = new TrackingShortcutHost
            {
                CommandHandler = rig.Controller,
                SelectedAssetEntries = new[]
                {
                    new ZepAssetEntry(
                        "Icon.png",
                        iconPath,
                        "Texture2D",
                        tags: new[] { red })
                },
                TagFilterOptions = new[]
                {
                    new ZepTagFilterOption(red, 1),
                    new ZepTagFilterOption(blue, 0)
                }
            };
            var shortcuts = new ZepShortcutController(host);

            using (var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.Alpha1,
                modifiers = EventModifiers.Control
            }))
            {
                Assert.That(shortcuts.TryHandleFinderTagShortcut(evt), Is.True);
            }

            Assert.That(
                rig.Gateway.GetTagIds(AssetPath.From(iconPath)),
                Is.Empty,
                "Ctrl+1 removes the favorite tag the whole selection already carries.");

            using (var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.Alpha2,
                modifiers = EventModifiers.Control
            }))
            {
                Assert.That(shortcuts.TryHandleFinderTagShortcut(evt), Is.True);
            }

            Assert.That(
                rig.Gateway.GetTagIds(AssetPath.From(iconPath)),
                Is.EqualTo(new[] { "blue" }),
                "Ctrl+2 adds the second favorite tag the selection lacks.");
        }

        [Test]
        public void TagAssignmentMenu_UnpinnedUnusedDefinitionsRemainAssignable()
        {
            using var scope = new TempAssetScope();
            var path = scope.CreateTextAsset("Tagged.txt", "tag assignment menu");
            var guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Is.Not.Empty);
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord(guid, path, "Tagged", "TextAsset") },
                new[] { "Assets", scope.FolderPath, "Packages" },
                new[]
                {
                    new TagDefinitionRecord { Id = "blue", Name = "Blue", Color = ZepTagColor.Blue, IsFavorite = true },
                    new TagDefinitionRecord { Id = "red", Name = "Red", Color = ZepTagColor.Red, IsFavorite = false }
                });
            var shell = rig.BuildShell();
            try
            {
                Assert.That(rig.Controller.UpdateTagDefinition(
                    new ZepTag("blue", "Blue", ZepTagColor.Blue, false)), Is.True);
                Assert.That(rig.State.TagFilterOptions, Is.Empty, "unused unpinned definitions stay out of sidebar shortcuts");
                var pane = rig.State.PrimaryPane;
                pane.TrySetFolderSelection(scope.FolderPath);
                var selected = pane.ProjectedAssets.Single(entry => entry.Path == path);
                pane.SetSelectedAssetPaths(new[] { AssetPath.From(path) });
                var request = InvokeShellMethod<ZepContextMenuRequest>(shell, "BuildContextMenuRequest",
                    pane, new[] { path }, ZepContextMenuTargetKind.Selection, new[] { selected }, null, null);
                var menu = ContextMenuTagSubmenuBuilder.Build(request, true,
                    (ContextMenuActions)GetShellField(shell, "_actions"), RuntimePlatform.OSXEditor);
                var toggles = menu.Children.Where(item => item.NormalizedCommandPath.StartsWith("Zep/Tags/Toggle/", StringComparison.Ordinal)).ToArray();

                Assert.That(request.AvailableTags.Select(option => option.TagId), Is.EqualTo(new[] { "blue", "red" }));
                Assert.That(toggles.Select(item => item.NormalizedCommandPath), Is.EqualTo(new[]
                {
                    "Zep/Tags/Toggle/blue", "Zep/Tags/Toggle/red"
                }));
                Assert.That(toggles.All(item => item.IsEnabled), Is.True);
                Assert.That(toggles.All(item => item.Hotkey == string.Empty), Is.True);
                Assert.That(request.AreContextTargetsCurrent(), Is.True, "the action must use the real temporary asset identity");

                toggles[1].OnClick();

                Assert.That(rig.Gateway.GetTagIds(AssetPath.From(path)), Is.EqualTo(new[] { "red" }));
                Assert.That(pane.ProjectedAssets.Single(entry => entry.Path == path).Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "red" }));
                Assert.That(rig.State.TagFilterOptions.Select(option => option.TagId), Is.EqualTo(new[] { "red" }),
                    "an unpinned definition enters the sidebar only after it is in use");
                Assert.That(rig.State.TagFilterOptions[0].Tag.IsFavorite, Is.False);
                Assert.That(rig.State.TagFilterOptions[0].Count, Is.EqualTo(1));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void TagAssignmentMenu_PreservesDefinitionCountsAndFavoriteShortcutOrder()
        {
            const string path = "Assets/Tagged.txt";
            var ids = new[] { "zeta", "unused", "alpha", "tango", "bravo", "yankee", "echo", "delta", "charlie", "used" };
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("tagged-guid", path, "Tagged", "TextAsset") },
                new[] { "Assets", "Packages" },
                ids.Select(id => new TagDefinitionRecord
                {
                    Id = id, Name = id, Color = ZepTagColor.Blue,
                    IsFavorite = id != "unused" && id != "used"
                }));
            rig.Gateway.SeedTagIds(path, "used");
            rig.Controller.Refresh();
            var shell = rig.BuildShell();
            try
            {
                var pane = rig.State.PrimaryPane;
                var selected = pane.ProjectedAssets.Single(entry => entry.Path == path);
                var request = InvokeShellMethod<ZepContextMenuRequest>(shell, "BuildContextMenuRequest",
                    pane, new[] { path }, ZepContextMenuTargetKind.Selection, new[] { selected }, null, null);
                var menu = ContextMenuTagSubmenuBuilder.Build(request, true,
                    (ContextMenuActions)GetShellField(shell, "_actions"), RuntimePlatform.OSXEditor);
                var toggles = menu.Children.Where(item => item.NormalizedCommandPath.StartsWith("Zep/Tags/Toggle/", StringComparison.Ordinal)).ToArray();

                Assert.That(request.AvailableTags.Select(option => option.TagId), Is.EqualTo(ids));
                Assert.That(request.AvailableTags.Select(option => option.Count), Is.EqualTo(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }));
                Assert.That(rig.State.TagFilterOptions.Select(option => option.TagId), Is.EqualTo(ids.Where(id => id != "unused")));
                var firstSeven = rig.State.TagFilterOptions.Where(option => option.Tag.IsFavorite).Take(7).Select(option => option.TagId).ToArray();
                Assert.That(toggles.Take(7).Select(item => item.NormalizedCommandPath), Is.EqualTo(firstSeven.Select(id => "Zep/Tags/Toggle/" + id)));
                Assert.That(toggles.Take(7).Select(item => item.Hotkey), Is.EqualTo(Enumerable.Range(1, 7).Select(index => "^" + index)));
                Assert.That(toggles.Skip(7).Select(item => item.NormalizedCommandPath), Is.EqualTo(new[]
                {
                    "Zep/Tags/Toggle/unused", "Zep/Tags/Toggle/charlie", "Zep/Tags/Toggle/used"
                }));
                Assert.That(toggles.Skip(7).All(item => item.Hotkey == string.Empty), Is.True);
                Assert.That(toggles.Single(item => item.NormalizedCommandPath.EndsWith("/used", StringComparison.Ordinal)).IsChecked, Is.True);
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void TagCreateButton_IsShownOnlyWhenACommandHandlerIsPresent()
        {
            var withoutHandler = new ZepShellView(new ZepState());
            var readOnlyButton = withoutHandler.Q<Button>("zep-tag-create-button");
            Assert.That(readOnlyButton, Is.Not.Null);
            Assert.That(readOnlyButton.style.display.value, Is.EqualTo(DisplayStyle.None));

            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            var button = shell.Q<Button>("zep-tag-create-button");
            Assert.That(button, Is.Not.Null);
            Assert.That(button.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(button.enabledSelf, Is.True);
            Assert.That(
                shell.Q<VisualElement>("zep-tag-filter-row"),
                Is.Not.Null);
        }

        // The list view's Addressable column only means something with the
        // Addressables package present. The shell stamps that once on its root
        // from the controller's port; Zep.uss hides the column under
        // the class (AssetsAreaMetadataDisplayTests pins the resolved display).
        [Test]
        public void Shell_MarksTheRoot_WhenTheAddressablesPackageIsAbsent()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });

            rig.Addressables.IsInstalled = false;
            var withoutPackage = rig.BuildShell();
            Assert.That(
                withoutPackage.ClassListContains(ZepUiClasses.RootNoAddressables),
                Is.True,
                "no package: the root must carry the state that hides the column");

            rig.Addressables.IsInstalled = true;
            var withPackage = rig.BuildShell();
            Assert.That(
                withPackage.ClassListContains(ZepUiClasses.RootNoAddressables),
                Is.False,
                "package present: the column must render");
        }

        [Test]
        public void OpenSettingsPage_OpensTheDrawerWithTheEmbeddedTagEditor()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            var page = shell.Q<VisualElement>("zep-settings-page");
            Assert.That(page, Is.Not.Null);
            Assert.That(page.ClassListContains(ZepUiClasses.SettingsPageOpen), Is.False);

            InvokeActionsMethod(shell, "OpenSettingsPage");

            Assert.That(page.ClassListContains(ZepUiClasses.SettingsPageOpen), Is.True);
            Assert.That(page.Q<VisualElement>("zep-settings-tags-editor"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator SettingsDrawer_OverlaysAndDimsTheWorkspaceWithoutResizingAssets()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();
            var window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            try
            {
                window.Show();
                window.position = new Rect(40, 40, 1000, 600);
                window.rootVisualElement.Add(shell);
                for (var frame = 0; frame < 5; frame++) yield return null;

                var assets = shell.Q<ZepSplitWorkspaceView>();
                var page = shell.Q<VisualElement>("zep-settings-page");
                var backdrop = shell.Q<VisualElement>("zep-settings-backdrop");
                var originalBounds = assets.worldBound;
                Assert.That(originalBounds.width, Is.GreaterThan(320f));
                Assert.That(backdrop.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));

                InvokeActionsMethod(shell, "OpenSettingsPage");
                var deadline = EditorApplication.timeSinceStartup + 0.3;
                do
                {
                    yield return null;
                    Assert.That(assets.worldBound.x, Is.EqualTo(originalBounds.x).Within(0.1f));
                    Assert.That(assets.worldBound.width, Is.EqualTo(originalBounds.width).Within(0.1f),
                        "Opening settings must not squeeze the asset view, even during animation.");
                } while (EditorApplication.timeSinceStartup < deadline);

                Assert.That(page.resolvedStyle.width, Is.EqualTo(320f).Within(0.1f));
                Assert.That(page.worldBound.xMax, Is.EqualTo(originalBounds.xMax).Within(0.1f));
                Assert.That(backdrop.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(backdrop.resolvedStyle.backgroundColor.a, Is.InRange(0.01f, 0.99f));
                var exposedPoint = new Vector2(originalBounds.x + 10f, originalBounds.center.y);
                Assert.That(shell.panel.Pick(exposedPoint), Is.SameAs(backdrop),
                    "The dimmed workspace must not receive pointer input through the backdrop.");
                var settingsPoint = page.worldBound.center;
                var pickedSettings = shell.panel.Pick(settingsPoint);
                Assert.That(pickedSettings == page || page.Contains(pickedSettings), Is.True,
                    "The backdrop must paint behind settings and leave its controls reachable.");

                ((IZepShortcutHost)shell).CloseOverlay();
                deadline = EditorApplication.timeSinceStartup + 0.3;
                do
                {
                    yield return null;
                    Assert.That(assets.worldBound.width, Is.EqualTo(originalBounds.width).Within(0.1f));
                } while (EditorApplication.timeSinceStartup < deadline);

                Assert.That(page.resolvedStyle.width, Is.EqualTo(0f).Within(0.1f));
                Assert.That(backdrop.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(shell.panel.Pick(exposedPoint), Is.Not.SameAs(backdrop));

                InvokeActionsMethod(shell, "OpenSettingsPage");
                page.Q<Button>("zep-settings-close-button").Focus();
                using (var inside = MouseDownEvent.GetPooled(new Event { button = 0 }))
                {
                    inside.target = page;
                    page.SendEvent(inside);
                }
                Assert.That(((IZepShortcutHost)shell).IsOverlayActive, Is.True);
                using (var secondary = MouseDownEvent.GetPooled(new Event { button = 1 }))
                {
                    secondary.target = backdrop;
                    backdrop.SendEvent(secondary);
                }
                Assert.That(((IZepShortcutHost)shell).IsOverlayActive, Is.True);
                using (var outside = MouseDownEvent.GetPooled(new Event { button = 0 }))
                {
                    outside.target = backdrop;
                    backdrop.SendEvent(outside);
                    Assert.That(outside.isPropagationStopped, Is.True);
                }
                Assert.That(((IZepShortcutHost)shell).IsOverlayActive, Is.False);
                Assert.That(backdrop.style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(shell.panel.focusController.focusedElement, Is.SameAs(shell));
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [Test]
        public void CreateTagFromPrompt_CreatesTheTagDefinitionWithItsColor()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            var created = InvokeActionsMethod<bool>(
                shell,
                "CreateTagFromPrompt",
                "Urgent",
                ZepTagColor.Orange);

            Assert.That(created, Is.True);
            var tag = rig.Controller.GetSharedTags()
                .FirstOrDefault(candidate => candidate.Name == "Urgent");
            Assert.That(tag, Is.Not.Null, "The prompt must create the tag definition.");
            Assert.That(tag.Color, Is.EqualTo(ZepTagColor.Orange));
        }

        [Test]
        public void CanTagAllSelectedPaths_AllowsTaggableReadOnlyItems()
        {
            const string packageAssetPath = "Packages/com.demo/Icon.png";
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("demo-guid", packageAssetPath, "Icon", "Texture2D") },
                new[] { "Assets", "Packages", "Packages/com.demo" });
            rig.Gateway.CanWriteOverride = false;
            rig.Gateway.CanTagOverride = true;

            var selection = new[] { packageAssetPath };

            Assert.That(
                ZepSelectionPredicates.CanTagAllSelectedPaths(rig.Controller, selection),
                Is.True,
                "A read-only-but-taggable item must keep its tag affordances.");
            Assert.That(
                ZepSelectionPredicates.CanWriteAllSelectedPaths(rig.Controller, selection),
                Is.False);
        }

        // F-012: the + button's enabled state must track the SAME create target
        // ShowCreateMenu and Cmd+N resolve (pane.ResolveCreateTargetFolderPath),
        // not the browsed folder. These four tests pin the button against that
        // resolver directly instead of driving the popup menu, since the two
        // now share one expression by construction.
        private static Button FindPlusButton(ZepShellView shell)
        {
            // The type-filter, +, and toolbar-overflow buttons all carry
            // ZepUiClasses.CreateButton (a shared "top-bar action
            // button" look) and the type-filter one isn't even a Button, so
            // disambiguate the class query with the + button's own tooltip.
            var plusButton = shell.Query<Button>(className: ZepUiClasses.CreateButton)
                .ToList()
                .FirstOrDefault(button => button.tooltip == "Create New...");
            Assert.That(plusButton, Is.Not.Null, "Expected the top-bar + button.");
            return plusButton;
        }

        [Test]
        public void PlusButtonEnabled_EnablesForWritableSelectedFolderUnderUnwritableBrowsedFolder()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages", "Packages/local.pkg" });
            rig.Gateway.CanWriteOverrideByPath["Packages"] = false;
            rig.Gateway.CanWriteOverrideByPath["Packages/local.pkg"] = true;

            var shell = rig.BuildShell();
            var pane = rig.State.PrimaryPane;
            // Browse into the folder the way the UI actually does (sets BOTH
            // current and selected folder to it, see e.g. line 629-630) rather
            // than only TrySetCurrentFolderPath, whose stale selected-folder
            // leftover would otherwise pass this fixture for the wrong reason.
            Assert.That(pane.TrySetFolderSelection("Packages"), Is.True);

            var plusButton = FindPlusButton(shell);
            Assert.That(
                plusButton.enabledSelf,
                Is.False,
                "Browsing the unwritable Packages folder with nothing selected must disable +.");

            // Select (but do not open) the writable child folder, exactly as a
            // sidebar click would — this is a Selection-only pane update.
            Assert.That(pane.TrySetSelectedFolderPath(AssetPath.From("Packages/local.pkg")), Is.True);

            var resolvedTarget = pane.ResolveCreateTargetFolderPath(rig.State.IsFolderAssetEntry);
            Assert.That(resolvedTarget, Is.EqualTo("Packages/local.pkg"),
                "The resolver must prefer the selected folder over the browsed one.");
            Assert.That(rig.Controller.CanWrite(AssetPath.From(resolvedTarget)), Is.True,
                "Fixture sanity: the selected folder must actually be writable.");
            Assert.That(
                plusButton.enabledSelf,
                Is.True,
                "Selecting a writable child folder while browsing an unwritable folder must enable +, " +
                "matching what ShowCreateMenu/Cmd+N would target.");
        }

        [Test]
        public void PlusButtonEnabled_DisablesForUnwritableSelectedFolderUnderWritableBrowsedFolder()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Assets/ReadOnlyChild" });
            rig.Gateway.CanWriteOverrideByPath["Assets"] = true;
            rig.Gateway.CanWriteOverrideByPath["Assets/ReadOnlyChild"] = false;

            var shell = rig.BuildShell();
            var pane = rig.State.PrimaryPane;
            Assert.That(pane.TrySetFolderSelection("Assets"), Is.True);

            var plusButton = FindPlusButton(shell);
            Assert.That(
                plusButton.enabledSelf,
                Is.True,
                "Browsing the writable Assets folder with nothing selected must enable +.");

            Assert.That(pane.TrySetSelectedFolderPath(AssetPath.From("Assets/ReadOnlyChild")), Is.True);

            var resolvedTarget = pane.ResolveCreateTargetFolderPath(rig.State.IsFolderAssetEntry);
            Assert.That(resolvedTarget, Is.EqualTo("Assets/ReadOnlyChild"));
            Assert.That(rig.Controller.CanWrite(AssetPath.From(resolvedTarget)), Is.False,
                "Fixture sanity: the selected folder must actually be unwritable.");
            Assert.That(
                plusButton.enabledSelf,
                Is.False,
                "Selecting an unwritable child folder while browsing a writable folder must disable +, " +
                "matching what ShowCreateMenu/Cmd+N would target (both are all-disabled entries there).");
        }

        [Test]
        public void PlusButtonEnabled_StaysHiddenAndDisabledOutsideBrowserMode()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets" });
            rig.Gateway.CanWriteOverride = true;

            var shell = rig.BuildShell();
            var pane = rig.State.PrimaryPane;
            var plusButton = FindPlusButton(shell);
            Assert.That(plusButton.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(plusButton.enabledSelf, Is.True);

            // For the primary pane, ActiveMode derives Tags from a non-empty
            // selected tag filter (ZepPaneState.ActiveMode getter) —
            // EnterMode(Tags) itself is a no-op case for the primary pane, so
            // picking a tag filter is how the UI actually enters this mode.
            Assert.That(pane.SetSelectedTagFilterId("blue"), Is.True);
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(plusButton.style.display.value, Is.EqualTo(DisplayStyle.None),
                "+ must be hidden in Tags mode.");
            Assert.That(plusButton.enabledSelf, Is.False,
                "+ must also be disabled in Tags mode, not just hidden.");
        }

        // The mac physical route for ⌘⌫ is the ExecuteCommand path (see the
        // comment above HandleValidateCommand), not HandleKeyDown, so the
        // message fix must reach that path too, independent of host platform.

        [Test]
        public void DeleteChord_OnAssetSelection_DeletesWithNoBlockedMessage()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var host = new TrackingShortcutHost
            {
                CommandHandler = rig.Controller,
                SelectedAssetEntries = new[]
                {
                    new ZepAssetEntry("Icon.png", "Assets/Icon.png", "Texture2D")
                }
            };
            var shortcuts = new ZepShortcutController(host);
            var isMac = ZepShortcutController.IsMacPlatform(Application.platform);

            using var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.Delete,
                modifiers = isMac ? EventModifiers.Command : EventModifiers.None
            });
            evt.target = new VisualElement();
            shortcuts.HandleKeyDown(evt);

            Assert.That(host.DeleteSelectedCount, Is.EqualTo(1));
            Assert.That(
                rig.Controller.StatusMessage,
                Is.EqualTo("Ready"),
                "Deleting an asset should leave the ready status unchanged in this host fixture.");
        }

        [Test]
        public void DeleteChord_ForwardsTheChordTargetToTheHost()
        {
            var host = new TrackingShortcutHost();
            var shortcuts = new ZepShortcutController(host);
            var target = new VisualElement();

            using var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.Delete,
                modifiers = ZepShortcutController.IsMacPlatform(Application.platform)
                    ? EventModifiers.Command
                    : EventModifiers.None
            });
            evt.target = target;
            shortcuts.HandleKeyDown(evt);

            Assert.That(host.DeleteSelectedCount, Is.EqualTo(1));
            Assert.That(
                host.LastDeleteTarget,
                Is.SameAs(target),
                "The host needs the chord's target to route a sidebar-focused delete.");
        }

        [Test]
        public void DeleteCommandEvent_ForwardsTheCommandTargetToTheHost()
        {
            var host = new TrackingShortcutHost();
            var shortcuts = new ZepShortcutController(host);
            var target = new VisualElement();

            // macOS delivers ⌘⌫ as the Edit-menu SoftDelete command, not as a
            // KeyDownEvent — this is the real mac delete route.
            using var evt = ExecuteCommandEvent.GetPooled("SoftDelete");
            evt.target = target;
            shortcuts.HandleExecuteCommand(evt);

            Assert.That(host.DeleteSelectedCount, Is.EqualTo(1));
            Assert.That(host.LastDeleteTarget, Is.SameAs(target));
        }

        [Test]
        public void DeleteAimedAtTheSidebarFolderTree_TargetsTheSidebarSelectedFolder()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            Assert.That(rig.State.PrimaryPane.TrySetFolderSelection("Assets"), Is.True);
            rig.Scheduler.RunAll();
            Assert.That(
                shell.SelectedSidebarFolder?.Path,
                Is.EqualTo("Assets"),
                "Precondition: the sidebar tree must hold the Assets root selection.");

            var tree = shell.Q<ListView>("zep-folder-list-assets");
            Assert.That(tree, Is.Not.Null);

            ((IZepShortcutHost)shell).DeleteSelected(tree);

            // The Assets root is refused before any dialog with this exact
            // status — reaching it proves the chord was routed to the sidebar
            // folder instead of the (empty) asset selection.
            Assert.That(
                rig.Controller.StatusMessage,
                Is.EqualTo("Project root and package folders cannot be deleted."));
        }

        // Where the parent is now belongs to ZepPaneState; the shell
        // keeps only the overlay guard and the delegation, so this pins both.
        [Test]
        public void NavigateToParentFolderShortcut_ClimbsTheActivePaneUnlessAnOverlayIsOpen()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages", "Assets/Art", "Assets/Art/Props" });
            var shell = rig.BuildShell();

            Assert.That(
                rig.State.PrimaryPane.TrySetFolderSelection("Assets/Art/Props"),
                Is.True);
            rig.Scheduler.RunAll();

            ((IZepShortcutHost)shell).NavigateToParentFolder();

            Assert.That(
                rig.State.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")),
                "the shortcut must move the active pane one folder up");

            InvokeActionsMethod(shell, "OpenSettingsPage");

            ((IZepShortcutHost)shell).NavigateToParentFolder();

            Assert.That(
                rig.State.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")),
                "an open overlay owns the keyboard: nothing may navigate behind it");
        }

        [Test]
        public void DeleteAimedOutsideTheSidebar_KeepsTheNoFolderFallbackNoOp()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            Assert.That(rig.State.PrimaryPane.TrySetFolderSelection("Assets"), Is.True);
            rig.Scheduler.RunAll();

            // Focus in the asset area with no asset selection: the sidebar's
            // selected folder must NOT become a delete fallback target, so the
            // delete is a complete no-op and the status stays untouched.
            var statusBefore = rig.Controller.StatusMessage;
            ((IZepShortcutHost)shell).DeleteSelected(shell);

            Assert.That(rig.Controller.StatusMessage, Is.EqualTo(statusBefore));
        }

        [Test]
        public void DetachAttachCycle_KeepsSingleStateSubscription()
        {
            var state = new ZepState();
            var shell = new ZepShellView(state);

            Assert.That(CountShellStateSubscriptions(state, shell), Is.EqualTo(2));

            InvokeShellLifecycle(shell, "OnDetachFromPanel");
            Assert.That(CountShellStateSubscriptions(state, shell), Is.Zero);

            InvokeShellLifecycle(shell, "OnAttachToPanel");
            Assert.That(CountShellStateSubscriptions(state, shell), Is.EqualTo(2));

            InvokeShellLifecycle(shell, "OnAttachToPanel");
            Assert.That(
                CountShellStateSubscriptions(state, shell),
                Is.EqualTo(2),
                "Attach must not duplicate subscriptions.");

            InvokeShellLifecycle(shell, "OnDetachFromPanel");
            InvokeShellLifecycle(shell, "OnDetachFromPanel");
            Assert.That(
                CountShellStateSubscriptions(state, shell),
                Is.Zero,
                "Detach must not over-remove subscriptions.");

            InvokeShellLifecycle(shell, "OnAttachToPanel");
            Assert.That(CountShellStateSubscriptions(state, shell), Is.EqualTo(2));
        }

        [Test]
        public void PublicationTouchingGlobalAndPaneState_RunsOneFullShellRefresh()
        {
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("marble-guid", "Assets/Marble.mat", "Marble", "Material") },
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            var refreshesBefore = GetShellCounter(shell, "__pp_refreshSeq");

            // The shape of every ZepController.ApplyProjection outside
            // its reacting window: one notification deferral that touches a
            // global slot and a pane slot, ending with PaneChanged(Updated)
            // followed by GlobalChanged.
            using (rig.State.DeferNotifications())
            {
                rig.State.SetStatusMessage("Imported 3 assets");
                rig.State.PrimaryPane.SetSearchText("marble");
            }

            Assert.That(
                GetShellCounter(shell, "__pp_refreshSeq") - refreshesBefore,
                Is.EqualTo(1),
                "One publication must run one full shell refresh, not one per raised event.");
        }

        [Test]
        public void SelectionOnlyPaneUpdate_DoesNotRefreshTheShell()
        {
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("marble-guid", "Assets/Marble.mat", "Marble", "Material") },
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            var refreshesBefore = GetShellCounter(shell, "__pp_refreshSeq");

            rig.State.PrimaryPane.SetSelectedAssetPaths(
                new[] { AssetPath.From("Assets/Marble.mat") });

            Assert.That(
                GetShellCounter(shell, "__pp_refreshSeq") - refreshesBefore,
                Is.Zero,
                "A row click changes nothing the shell pushes.");

            // A selection coalesced with another update must still refresh: the
            // update is a [Flags] value, so the fast path is exact-equality.
            using (rig.State.DeferNotifications())
            {
                rig.State.PrimaryPane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
                rig.State.PrimaryPane.SetSearchText("marble");
            }

            Assert.That(
                GetShellCounter(shell, "__pp_refreshSeq") - refreshesBefore,
                Is.EqualTo(1),
                "Selection|Search must not be mistaken for a selection-only update.");
        }

        [Test]
        public void GlobalOnlyChange_RefreshesChromeWithoutTheFullPaneRefresh()
        {
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("marble-guid", "Assets/Marble.mat", "Marble", "Material") },
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            var statusLabel = shell.Q<Label>("zep-status-label");
            Assert.That(statusLabel, Is.Not.Null);
            var searchField = shell.Q<ToolbarSearchField>("zep-search");
            Assert.That(searchField, Is.Not.Null);

            // Dialog text must survive both global and pane refreshes.
            searchField.SetValueWithoutNotify("sentinel");
            var refreshesBefore = GetShellCounter(shell, "__pp_refreshSeq");
            var chromeRefreshesBefore = GetShellCounter(shell, "__pp_globalChromeSeq");

            rig.State.SetStatusMessage("Scanning project");

            Assert.That(statusLabel.text, Is.EqualTo("Scanning project"),
                "The global path still pushes the status bar.");
            Assert.That(
                GetShellCounter(shell, "__pp_globalChromeSeq") - chromeRefreshesBefore,
                Is.EqualTo(1));
            Assert.That(
                GetShellCounter(shell, "__pp_refreshSeq") - refreshesBefore,
                Is.Zero,
                "A global-only raise must not run the pane half of the refresh.");
            Assert.That(searchField.value, Is.EqualTo("sentinel"),
                "The global path must not push pane chrome such as the search text.");

            rig.State.PrimaryPane.SetSearchText("marble");

            Assert.That(searchField.value, Is.EqualTo("sentinel"),
                "A pane refresh must not overwrite the dialog query.");
        }

        [Test]
        public void Teardown_ReleasesTheSplitWorkspaceTheShellOwns()
        {
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("marble-guid", "Assets/Marble.mat", "Marble", "Material") },
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();

            Assert.That(GetShellField(shell, "_splitWorkspace"), Is.Not.Null);
            Assert.That(GetShellField(shell, "_assetPane"), Is.Not.Null);

            shell.Teardown();

            Assert.That(
                GetShellField(shell, "_splitWorkspace"),
                Is.Null,
                "A torn-down shell must not hand a dead workspace to a late caller.");
            Assert.That(
                GetShellField(shell, "_assetPane"),
                Is.Null,
                "The asset-pane alias points at the same dead workspace.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Teardown_ReleasesSubscriptionsBeforeNativeDetach(bool attached)
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();
            var icons = GetShellField(shell, "_iconRepository");
            try
            {
                if (attached) InvokeShellLifecycle(shell, "OnAttachToPanel");
                Assert.That(CountShellStateSubscriptions(rig.State, shell), Is.EqualTo(2));
                Assert.That(CountSubscriptions(rig.Controller, "AssetCatalogChanged", shell), Is.EqualTo(attached ? 1 : 0));
                Assert.That(CountSubscriptions(rig.Controller, "AssetFrameRequested", shell), Is.EqualTo(attached ? 1 : 0));
                Assert.That(CountSubscriptions(icons, "PreviewsReady", shell), Is.EqualTo(attached ? 1 : 0));
                Assert.That(CountSubscriptions(typeof(MaterialThumbnailProvider), "ThumbnailsReady", shell), Is.EqualTo(attached ? 1 : 0));

                shell.Teardown();
                shell.Teardown();

                Assert.That(CountShellStateSubscriptions(rig.State, shell), Is.Zero);
                Assert.That(CountSubscriptions(rig.Controller, "AssetCatalogChanged", shell), Is.Zero);
                Assert.That(CountSubscriptions(rig.Controller, "AssetFrameRequested", shell), Is.Zero);
                Assert.That(CountSubscriptions(icons, "PreviewsReady", shell), Is.Zero);
                Assert.That(CountSubscriptions(typeof(MaterialThumbnailProvider), "ThumbnailsReady", shell), Is.Zero);

                var status = shell.Q<Label>("zep-status-label");
                var retiredStatus = status.text;
                rig.State.SetStatusMessage("A later session update");
                Assert.That(status.text, Is.EqualTo(retiredStatus), "Retired chrome must not receive session updates.");

                InvokeShellLifecycle(shell, "OnDetachFromPanel");
                Assert.That(CountShellStateSubscriptions(rig.State, shell), Is.Zero,
                    "Native detach after teardown must remain harmless.");
            }
            finally
            {
                InvokeShellLifecycle(shell, "OnDetachFromPanel");
                shell.Teardown();
            }
        }

        // The icon refresh is deferred to the next editor turn, which can land
        // after the window closed (ZepWindow tears the shell down and
        // drops it from the panel). §5.16.
        [Test]
        public void DeferredIconRefresh_LandingAfterTeardown_IsDropped()
        {
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("marble-guid", "Assets/Marble.mat", "Marble", "Material") },
                new[] { "Assets", "Packages" });
            var shell = rig.BuildShell();
            var window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            window.position = new Rect(40f, 40f, 320f, 320f);
            window.Show();
            try
            {
                window.rootVisualElement.Add(shell);
                Assert.That(shell.panel, Is.Not.Null, "The shell must be live when the refresh is armed.");

                var armed = CaptureDeferredCall(() => InvokeShellMethod(
                    shell,
                    "OnIconPreviewsReady",
                    new object[] { new[] { "Assets/Marble.mat" } }));
                Assert.That(armed, Is.Not.Null, "The icon refresh must be deferred, not run inline.");

                shell.Teardown();
                shell.RemoveFromHierarchy();
                Assert.That(shell.panel, Is.Null);

                Assert.DoesNotThrow(() => armed.Invoke());

                Assert.That(
                    GetShellFlag(shell, "_iconRefreshQueued"),
                    Is.False,
                    "The queued latch must clear so a re-attached shell can arm again.");
                Assert.That(
                    (ICollection)GetShellField(shell, "_pendingIconRefreshPaths"),
                    Is.Not.Empty,
                    "A batch nobody rebound must not be recorded as delivered.");
            }
            finally
            {
                window.Close();
            }
        }

        // The create reveal is deferred for the same reason, and its pane can
        // still be live in the view model while this shell's views are gone.
        [Test]
        public void DeferredCreateReveal_LandingAfterTeardown_DoesNotRevealThroughTheDeadShell()
        {
            var assetPath = "Assets/ZepShellCreated_"
                + Guid.NewGuid().ToString("N")
                + ".asset";
            AssetDatabase.CreateAsset(
                ScriptableObject.CreateInstance<CreatedAssetMarker>(),
                assetPath);
            AssetDatabase.SaveAssets();
            var window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
            try
            {
                using var rig = ZepUiControllerRig.Build(
                    new[] { new AssetRecord("created-guid", assetPath, "Created", "ScriptableObject") },
                    new[] { "Assets", "Packages" });
                var shell = rig.BuildShell();
                window.position = new Rect(40f, 40f, 320f, 320f);
                window.Show();
                window.rootVisualElement.Add(shell);
                Assert.That(shell.panel, Is.Not.Null);

                var armed = CaptureDeferredCall(() => InvokeShellMethod(
                    shell,
                    "OnCreatedAssetLanded",
                    rig.State.PrimaryPane,
                    assetPath));
                Assert.That(armed, Is.Not.Null, "The reveal must be deferred, not run inline.");
                Assert.That(
                    SessionState.GetString(ZepShellView.PendingCreateSelectionSessionKey, null),
                    Is.EqualTo(assetPath),
                    "The reload stash is written before the deferred reveal.");

                shell.Teardown();
                shell.RemoveFromHierarchy();

                Assert.DoesNotThrow(() => armed.Invoke());

                Assert.That(
                    rig.State.PrimaryPane.SelectedAssetPaths,
                    Is.Empty,
                    "A reveal must not run through a torn-down shell.");
                Assert.That(
                    SessionState.GetString(ZepShellView.PendingCreateSelectionSessionKey, null),
                    Is.EqualTo(assetPath),
                    "A reveal that never ran must not erase the reload stash.");
            }
            finally
            {
                window.Close();
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        // EditorApplication.delayCall is a plain static delegate field: empty it,
        // run the arming call, and whatever it holds afterwards is the callback
        // under test. The unrelated callbacks that were already pending are put
        // back; the captured one is invoked by hand instead of by the editor.
        private static EditorApplication.CallbackFunction CaptureDeferredCall(Action arm)
        {
            var pending = EditorApplication.delayCall;
            EditorApplication.delayCall = null;
            try
            {
                arm();
                return EditorApplication.delayCall;
            }
            finally
            {
                EditorApplication.delayCall = pending;
            }
        }

        [UnityTest]
        public IEnumerator RecoveryMenu_PointerRefusalShowsNoMenu()
        {
            ZepUiControllerRig rig = null;
            ZepShellView shell = null;
            EditorWindow window = null;
            var existingPopups = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>();
            try
            {
                rig = BuildRecoveryMenuRig();
                shell = rig.BuildShell();
                window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
                window.position = new Rect(40, 40, 800, 600);
                window.Show();
                window.rootVisualElement.Add(shell);
                yield return null;
                yield return null;
                var pane = rig.State.PrimaryPane;
                var folder = rig.Controller.GetVisibleFolderEntry("Assets/MenuFolder");
                Assert.That(folder, Is.Not.Null);
                Assert.That(rig.Controller.GetTagsForPath(folder.Path).Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
                Assert.That(pane.ResolveBackgroundContextFolderPath(AssetDatabase.IsValidFolder), Is.EqualTo("Assets"));
                Assert.That(pane.SelectedAssetPaths, Is.Empty);
                var published = CaptureRecoveryMenuMetadata(rig);
                var tree = shell.Q<FolderTreeWidget>();
                Assert.That(tree, Is.Not.Null);
                tree.Apply("Assets", folder.Path);
                yield return null;
                var row = tree.Query<VisualElement>().ToList().FirstOrDefault(element =>
                    tree.TryGetFolderEntryFromTarget(element, out var entry) && entry.Path == folder.Path);
                Assert.That(row, Is.Not.Null, "a real sidebar row must receive the pointer request");
                var paneView = ((ZepSplitWorkspaceView)GetShellField(shell, "_splitWorkspace")).PrimaryPaneView;
                var background = paneView.Q<VisualElement>("zep-browser-surface");
                Assert.That(background, Is.Not.Null);
                var sidebarRequests = 0;
                var backgroundRequests = 0;
                tree.ContextMenuRequested += (_, entry) =>
                {
                    Assert.That(entry.Path, Is.EqualTo(folder.Path));
                    sidebarRequests++;
                };
                paneView.ContextMenuRequested += (requestedPane, _, entry) =>
                {
                    Assert.That(requestedPane, Is.SameAs(pane));
                    Assert.That(entry, Is.Null);
                    backgroundRequests++;
                };
                rig.Gateway.TagVersionException = new ProjectTagStateUnavailableException("menu metadata unavailable");
                rig.Controller.Refresh();
                Assert.Throws<ProjectTagStateUnavailableException>(() => rig.Controller.GetTagsForPath(folder.Path));
                Assert.Throws<ProjectTagStateUnavailableException>(() => rig.Controller.GetVisibleFolderEntry("Assets"));
                Assert.That(pane.ResolveBackgroundContextFolderPath(AssetDatabase.IsValidFolder), Is.EqualTo("Assets"));

                Assert.DoesNotThrow(() => SendRecoveryMenuPointer(row));
                Assert.That(sidebarRequests, Is.EqualTo(1));
                Assert.That(rig.State.ActivePane, Is.SameAs(pane));
                Assert.That(CaptureRecoveryMenuMetadata(rig), Is.EqualTo(published));
                Assert.That(NewRecoveryMenuPopups(existingPopups), Is.Empty);
                Assert.DoesNotThrow(() => SendRecoveryMenuPointer(background));
                Assert.That(backgroundRequests, Is.EqualTo(1));
                Assert.That(pane.SelectedAssetPaths, Is.Empty);
                Assert.That(CaptureRecoveryMenuMetadata(rig), Is.EqualTo(published));
                Assert.That(rig.State.StatusMessage, Is.EqualTo("menu metadata unavailable"));
                Assert.That(NewRecoveryMenuPopups(existingPopups), Is.Empty);
            }
            finally
            {
                CleanupRecoveryMenu(rig, shell, window, existingPopups);
            }
        }

        [UnityTest]
        public IEnumerator RecoveryMenu_KeyboardRefusalIsHandled()
        {
            ZepUiControllerRig rig = null;
            ZepShellView shell = null;
            EditorWindow window = null;
            var existingPopups = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>();
            try
            {
                rig = BuildRecoveryMenuRig();
                shell = rig.BuildShell();
                window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
                window.position = new Rect(40, 40, 800, 600);
                window.Show();
                window.rootVisualElement.Add(shell);
                yield return null;
                yield return null;
                var pane = rig.State.PrimaryPane;
                var folder = rig.Controller.GetVisibleFolderEntry(pane.CurrentFolderPath);
                Assert.That(folder, Is.Not.Null);
                var tree = shell.Q<FolderTreeWidget>();
                Assert.That(folder.Path, Is.EqualTo("Assets"));
                yield return null;
                var list = tree.Q<ListView>("zep-folder-list-assets");
                Assert.That(list, Is.Not.Null);
                Assert.That(((SidebarWidget)GetShellField(shell, "_sidebar")).SelectedFolderEntry.Path, Is.EqualTo(folder.Path));
                Assert.That(pane.ResolveBackgroundContextFolderPath(AssetDatabase.IsValidFolder), Is.EqualTo("Assets"));
                var published = CaptureRecoveryMenuMetadata(rig);
                rig.Gateway.TagVersionException = new ProjectTagStateUnavailableException("menu metadata unavailable");
                rig.Controller.Refresh();
                Assert.Throws<ProjectTagStateUnavailableException>(() => rig.Controller.GetTagsForPath(folder.Path));
                Assert.Throws<ProjectTagStateUnavailableException>(() => rig.Controller.GetVisibleFolderEntry("Assets"));
                Assert.That(pane.ResolveBackgroundContextFolderPath(AssetDatabase.IsValidFolder), Is.EqualTo("Assets"));
                foreach (var key in new[] { KeyCode.Menu, KeyCode.F10 })
                {
                    var modifiers = key == KeyCode.F10 ? EventModifiers.Shift : EventModifiers.None;
                    AssertRecoveryMenuKey(shell, list, key, modifiers, true);
                    Assert.That(rig.State.ActivePane, Is.SameAs(pane));
                    Assert.That(((SidebarWidget)GetShellField(shell, "_sidebar")).SelectedFolderEntry.Path, Is.EqualTo(folder.Path));
                    AssertRecoveryMenuKey(shell, shell, key, modifiers, true);
                    Assert.That(pane.SelectedAssetPaths, Is.Empty);
                    Assert.That(NewRecoveryMenuPopups(existingPopups), Is.Empty);
                    Assert.That(CaptureRecoveryMenuMetadata(rig), Is.EqualTo(published));
                }
                Assert.That(rig.State.StatusMessage, Is.EqualTo("menu metadata unavailable"));
                AssertRecoveryMenuKey(shell, shell, KeyCode.A, EventModifiers.None, false);
                var input = new TextField();
                window.rootVisualElement.Add(input);
                AssertRecoveryMenuKey(shell, input.Q<VisualElement>("unity-text-input"), KeyCode.Menu, EventModifiers.None, false);
                var unsupported = new Button();
                window.rootVisualElement.Add(unsupported);
                AssertRecoveryMenuKey(shell, unsupported, KeyCode.Menu, EventModifiers.None, false);
                var prompt = (ZepPromptOverlayWidget)GetShellField(shell, "_promptOverlay");
                prompt.Show("Rename", "Name", "Before", "Rename", _ => { });
                AssertRecoveryMenuKey(shell, shell, KeyCode.Menu, EventModifiers.None, false);
                prompt.Hide();
                InvokeActionsMethod(shell, "OpenSettingsPage");
                Assert.That(((IZepShortcutHost)shell).IsOverlayActive, Is.True);
                AssertRecoveryMenuKey(shell, shell, KeyCode.Menu, EventModifiers.None, false);
                Assert.That(NewRecoveryMenuPopups(existingPopups), Is.Empty);
            }
            finally
            {
                CleanupRecoveryMenu(rig, shell, window, existingPopups);
            }
        }

        [UnityTest]
        public IEnumerator RecoveryMenu_RecoveryReopensWithCurrentTags()
        {
            ZepUiControllerRig rig = null;
            ZepShellView shell = null;
            EditorWindow window = null;
            var existingPopups = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>();
            try
            {
                rig = BuildRecoveryMenuRig();
                shell = rig.BuildShell();
                window = ScriptableObject.CreateInstance<SidebarViewportTestWindow>();
                window.position = new Rect(40, 40, 800, 600);
                window.Show();
                window.rootVisualElement.Add(shell);
                yield return null;
                yield return null;
                var pane = rig.State.PrimaryPane;
                var folder = rig.Controller.GetVisibleFolderEntry("Assets/MenuFolder");
                var anchor = new Rect(120, 140, 0, 0);
                rig.Gateway.TagVersionException = new ProjectTagStateUnavailableException("menu metadata unavailable");
                rig.Controller.Refresh();
                Assert.DoesNotThrow(() => InvokeShellMethod(shell, "OnFolderPaneContextMenuRequested", anchor, folder));
                Assert.That(NewRecoveryMenuPopups(existingPopups), Is.Empty);
                Assert.That(rig.State.StatusMessage, Is.EqualTo("menu metadata unavailable"));

                foreach (var tags in new[] { new[] { "red" }, Array.Empty<string>() })
                {
                    rig.Gateway.TagVersionException = null;
                    rig.Gateway.SeedTagIds(folder.Path, tags);
                    rig.Gateway.SeedTagIds("Assets", tags);
                    rig.Controller.Refresh();
                    rig.Scheduler.RunAll();
                    Assert.That(rig.State.StatusMessage, Is.EqualTo("Ready"));
                    Assert.That(rig.Controller.GetTagsForPath(folder.Path).Select(tag => tag.Id), Is.EqualTo(tags));
                    folder = rig.Controller.GetVisibleFolderEntry(folder.Path);
                    var request = InvokeShellMethod<ZepContextMenuRequest>(shell, "BuildContextMenuRequest",
                        pane, new[] { folder.Path }, ZepContextMenuTargetKind.SidebarFolder,
                        null, folder, (Rect?)anchor);
                    Assert.That(request.ContextPaths, Is.EqualTo(new[] { folder.Path }));
                    Assert.That(request.AssignedTagsByPath.Keys, Is.EqualTo(new[] { folder.Path }));
                    Assert.That(request.AssignedTagsByPath[folder.Path], Is.EqualTo(tags));
                    Assert.That(request.SelectedFolder, Is.SameAs(folder));
                    Assert.That(request.SelectedAssets, Is.Empty);
                    Assert.That(request.TargetKind, Is.EqualTo(ZepContextMenuTargetKind.SidebarFolder));
                    Assert.That(request.Anchor, Is.EqualTo((Rect?)anchor));
                    Assert.DoesNotThrow(() => InvokeShellMethod(shell, "OnFolderPaneContextMenuRequested", anchor, folder));
                    // The recorded runner uses -batchmode: production deliberately
                    // uses GenericMenu there. A real popup is required only on the
                    // interactive route; neither route may substitute stale tags.
                    if (!Application.isBatchMode)
                    {
                        Assert.That(NewRecoveryMenuPopups(existingPopups), Has.Length.EqualTo(1));
                        ContextMenuPopupRenderer.CloseCurrent(refocusHost: false);
                    }
                    Assert.That(pane.SelectedAssetPaths, Is.Empty);
                    var background = rig.Controller.GetVisibleFolderEntry("Assets");
                    var backgroundRequest = InvokeShellMethod<ZepContextMenuRequest>(shell, "BuildContextMenuRequest",
                        pane, new[] { "Assets" }, ZepContextMenuTargetKind.Background,
                        null, background, (Rect?)anchor);
                    Assert.That(backgroundRequest.ContextPaths, Is.EqualTo(new[] { "Assets" }));
                    Assert.That(backgroundRequest.AssignedTagsByPath["Assets"], Is.EqualTo(tags));
                    Assert.That(backgroundRequest.TargetKind, Is.EqualTo(ZepContextMenuTargetKind.Background));
                    Assert.That(backgroundRequest.Anchor, Is.EqualTo((Rect?)anchor));
                    Assert.DoesNotThrow(() => InvokeShellMethod(shell, "OnAssetPaneContextMenuRequested", pane, anchor, null));
                    if (!Application.isBatchMode)
                    {
                        Assert.That(NewRecoveryMenuPopups(existingPopups), Has.Length.EqualTo(1));
                        ContextMenuPopupRenderer.CloseCurrent(refocusHost: false);
                    }
                }
            }
            finally
            {
                CleanupRecoveryMenu(rig, shell, window, existingPopups);
            }
        }

        [Test]
        public void RecoveryMenu_FailureBoundaryIsNarrow()
        {
            using var rig = BuildRecoveryMenuRig();
            var shell = rig.BuildShell();
            try
            {
                Assert.DoesNotThrow(() => InvokeShellMethod(shell, "ShowContextMenu",
                    (Func<ZepContextMenuRequest>)(() => throw new ProjectTagStateUnavailableException("Unavailable."))));
                var failure = new InvalidOperationException("Unexpected.");
                var exception = Assert.Throws<TargetInvocationException>(() => InvokeShellMethod(shell, "ShowContextMenu",
                    (Func<ZepContextMenuRequest>)(() => throw failure)));
                Assert.That(exception.InnerException, Is.SameAs(failure));
                var pane = rig.State.PrimaryPane;
                var selected = pane.ProjectedAssets.Single(entry => entry.Path == "Assets/Item.txt");
                pane.SetSelectedAssetPaths(new[] { AssetPath.From(selected.Path) });
                var anchor = new Rect(40, 60, 0, 0);
                var request = InvokeShellMethod<ZepContextMenuRequest>(shell, "BuildContextMenuRequest",
                    pane, new[] { selected.Path }, ZepContextMenuTargetKind.Selection,
                    new[] { selected }, null, (Rect?)anchor);
                Assert.That(request.ContextPaths, Is.EqualTo(new[] { selected.Path }));
                Assert.That(request.SelectedAssets, Is.EqualTo(new[] { selected }));
                Assert.That(request.AssignedTagsByPath[selected.Path], Is.EqualTo(new[] { "blue" }));
                Assert.That(request.TargetKind, Is.EqualTo(ZepContextMenuTargetKind.Selection));
                Assert.That(request.Anchor, Is.EqualTo((Rect?)anchor));
                var empty = InvokeShellMethod<ZepContextMenuRequest>(shell, "BuildContextMenuRequest",
                    pane, Array.Empty<string>(), ZepContextMenuTargetKind.Background, null, null, (Rect?)anchor);
                Assert.That(empty.ContextPaths, Is.Empty);
                Assert.That(empty.SelectedAssets, Is.Empty);
                Assert.That(empty.AssignedTagsByPath, Is.Empty);
                Assert.That(empty.TargetKind, Is.EqualTo(ZepContextMenuTargetKind.Background));
                Assert.That(empty.Anchor, Is.EqualTo((Rect?)anchor));
                Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From(selected.Path) }));
            }
            finally
            {
                shell.Teardown();
            }
        }

        private static ZepUiControllerRig BuildRecoveryMenuRig()
        {
            var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("menu-item-guid", "Assets/Item.txt", "Item", "TextAsset") },
                new[] { "Assets", "Assets/MenuFolder", "Packages" },
                new[]
                {
                    new TagDefinitionRecord { Id = "blue", Name = "Blue", Color = ZepTagColor.Blue },
                    new TagDefinitionRecord { Id = "red", Name = "Red", Color = ZepTagColor.Red }
                });
            try
            {
                rig.Gateway.SeedTagIds("Assets/MenuFolder", "blue");
                rig.Gateway.SeedTagIds("Assets", "blue");
                rig.Gateway.SeedTagIds("Assets/Item.txt", "blue");
                rig.Controller.Refresh();
                rig.Scheduler.RunAll();
                rig.State.PrimaryPane.TrySetFolderSelection("Assets");
                rig.State.PrimaryPane.SetSelectedAssetPaths(Array.Empty<AssetPath>());
                Assert.That(rig.State.PrimaryPane.ProjectedAssets.Single(entry => entry.Path == "Assets/Item.txt")
                    .Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        private static string[] CaptureRecoveryMenuMetadata(ZepUiControllerRig rig)
            => rig.State.PrimaryPane.ProjectedAssets.Select(entry => entry.Path + "|" + entry.FolderTintColor + "|"
                + string.Join(",", entry.Tags.Select(tag => tag.Id + ":" + tag.Name + ":" + tag.Color)))
                .Concat(rig.State.TagFilterOptions.Select(option => option.TagId + ":" + option.Count)).ToArray();

        private static void SendRecoveryMenuPointer(VisualElement target)
        {
            Assert.That(target.panel, Is.Not.Null);
            Assert.That(target.worldBound.width, Is.GreaterThan(0));
            using var evt = PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown, button = 1, clickCount = 1, mousePosition = target.worldBound.center
            });
            evt.target = target;
            target.SendEvent(evt);
            Assert.That(evt.isPropagationStopped, Is.True);
        }

        private static void AssertRecoveryMenuKey(ZepShellView shell, VisualElement target,
            KeyCode key, EventModifiers modifiers, bool expected)
        {
            Assert.That(target, Is.Not.Null);
            target.Focus();
            using (var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers }))
            {
                evt.target = target;
                Assert.That(InvokeShellMethod<bool>(shell, "TryShowKeyboardContextMenu", evt), Is.EqualTo(expected));
                Assert.That(evt.isPropagationStopped, Is.EqualTo(expected));
            }
            if (expected)
            {
                using var dispatched = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
                dispatched.target = target;
                target.SendEvent(dispatched);
                Assert.That(dispatched.isPropagationStopped, Is.True, "the mounted shell must consume the actual key event");
            }
        }

        private static ContextMenuPopupWindow[] NewRecoveryMenuPopups(ContextMenuPopupWindow[] baseline)
            => Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>().Where(popup => !baseline.Contains(popup)).ToArray();

        private static void CleanupRecoveryMenu(ZepUiControllerRig rig, ZepShellView shell,
            EditorWindow window, ContextMenuPopupWindow[] baseline)
        {
            try
            {
                foreach (var popup in NewRecoveryMenuPopups(baseline)) popup.Close();
            }
            finally
            {
                try
                {
                    if (rig != null) rig.Gateway.TagVersionException = null;
                    shell?.Teardown();
                }
                finally
                {
                    try { rig?.Dispose(); }
                    finally { window?.Close(); }
                }
            }
        }

        private static object GetShellField(ZepShellView shell, string fieldName)
        {
            var field = typeof(ZepShellView).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Expected shell field '{fieldName}'.");
            return field.GetValue(shell);
        }

        private static bool GetShellFlag(ZepShellView shell, string fieldName)
            => (bool)GetShellField(shell, fieldName);

        private sealed class CreatedAssetMarker : ScriptableObject
        {
        }

        private static int GetShellCounter(ZepShellView shell, string fieldName)
        {
            var field = typeof(ZepShellView).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Expected shell counter field '{fieldName}'.");
            return (int)field.GetValue(shell);
        }

        private static int CountShellStateSubscriptions(
            ZepState state,
            ZepShellView shell)
        {
            return CountSubscriptions(state, "GlobalChanged", shell)
                + CountSubscriptions(state, "PaneChanged", shell);
        }

        private static int CountSubscriptions(
            object publisher,
            string eventFieldName,
            object subscriber)
        {
            var publisherType = publisher as Type ?? publisher.GetType();
            var field = publisherType.GetField(
                eventFieldName,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Expected event field '{eventFieldName}'.");
            var handler = field.GetValue(publisher is Type ? null : publisher) as Delegate;
            return handler?.GetInvocationList()
                .Count(entry => ReferenceEquals(entry.Target, subscriber)) ?? 0;
        }

        private static void InvokeShellLifecycle(ZepShellView shell, string methodName)
        {
            var method = typeof(ZepShellView).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Expected lifecycle method '{methodName}' to exist.");
            method.Invoke(shell, new object[] { null });
        }

        private static void InvokeShellMethod(ZepShellView shell, string methodName, params object[] arguments)
        {
            var method = typeof(ZepShellView).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Expected method '{methodName}' to exist.");
            method.Invoke(shell, arguments);
        }

        private static TResult InvokeShellMethod<TResult>(ZepShellView shell, string methodName, params object[] arguments)
        {
            var method = typeof(ZepShellView).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Expected method '{methodName}' to exist.");
            return (TResult)method.Invoke(shell, arguments);
        }

        private static object GetActions(ZepShellView shell)
        {
            var field = typeof(ZepShellView).GetField("_actions", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Expected ZepShellView._actions field to exist.");
            var actions = field.GetValue(shell);
            Assert.That(actions, Is.Not.Null, "Expected ContextMenuActions to be initialised after BuildLayout.");
            return actions;
        }

        private static void InvokeActionsMethod(ZepShellView shell, string methodName, params object[] arguments)
        {
            var actions = GetActions(shell);
            var method = actions.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, $"Expected ContextMenuActions method '{methodName}' to exist.");
            method.Invoke(actions, arguments);
        }

        private static TResult InvokeActionsMethod<TResult>(ZepShellView shell, string methodName, params object[] arguments)
        {
            var actions = GetActions(shell);
            var method = actions.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, $"Expected ContextMenuActions method '{methodName}' to exist.");
            return (TResult)method.Invoke(actions, arguments);
        }

        // Type-to-select. The buffer's lifetime is a clock rule, so these run
        // the controller against a hand-driven clock instead of the editor's.
        [Test]
        public void TypeAhead_AccumulatesCharactersWhileTheBufferIsAlive()
        {
            var host = new TrackingShortcutHost { TypeToSelectResult = true };
            var clock = 100.0;
            var controller = new ZepShortcutController(host, () => clock);

            TypeCharacter(controller, 'm');
            clock += 0.4;
            TypeCharacter(controller, 'a');

            Assert.That(host.TypeToSelectPrefixes, Is.EqualTo(new[] { "m", "ma" }));
        }

        [Test]
        public void TypeAhead_AfterASecondOfSilence_StartsANewPrefix()
        {
            var host = new TrackingShortcutHost { TypeToSelectResult = true };
            var clock = 100.0;
            var controller = new ZepShortcutController(host, () => clock);

            TypeCharacter(controller, 'm');
            // Well past the buffer's one-second reset window.
            clock += 1.5;
            TypeCharacter(controller, 'a');

            Assert.That(host.TypeToSelectPrefixes, Is.EqualTo(new[] { "m", "a" }),
                "a stale buffer must not glue the next character onto it");
        }

        [Test]
        public void TypeAhead_WhenNothingMatches_LeavesTheBufferWhereItWas()
        {
            var host = new TrackingShortcutHost { TypeToSelectResult = true };
            var clock = 100.0;
            var controller = new ZepShortcutController(host, () => clock);

            TypeCharacter(controller, 'm');
            host.TypeToSelectResult = false;
            var unmatched = TypeCharacter(controller, 'z');
            host.TypeToSelectResult = true;
            var matched = TypeCharacter(controller, 'a');

            Assert.That(unmatched, Is.False, "an unmatched character must stay unconsumed");
            Assert.That(matched, Is.True);
            Assert.That(host.TypeToSelectPrefixes, Is.EqualTo(new[] { "m", "mz", "ma" }),
                "the rejected character must not stick to the buffer");
        }

        [Test]
        public void TypeAhead_IsNotOfferedWhileATextInputHasTheKeys()
        {
            var host = new TrackingShortcutHost { TypeToSelectResult = true };
            var controller = new ZepShortcutController(host, () => 100.0);
            var innerInput = new TextField().Q("unity-text-input");

            using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, character = 'm' });
            evt.target = innerInput;
            controller.HandleKeyDown(evt);

            Assert.That(host.TypeToSelectPrefixes, Is.Empty);
            Assert.That(evt.isPropagationStopped, Is.False);
        }

        private static bool TypeCharacter(ZepShortcutController controller, char character)
        {
            using var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                character = character
            });
            evt.target = new VisualElement();
            controller.HandleKeyDown(evt);
            return evt.isPropagationStopped;
        }

        // Escape during a marquee did nothing in the main window: only a
        // detached pane window cancelled the gesture, so here the rubber band
        // kept tracking and committed on release.
        [Test]
        public void Escape_CancelsTheActivePanesInteractions_ButNotFromATextInput()
        {
            var host = new TrackingShortcutHost();
            var shortcuts = new ZepShortcutController(host);

            using (var escape = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }))
            {
                escape.target = new VisualElement();
                shortcuts.HandleKeyDown(escape);
            }

            Assert.That(host.CancelInteractionsCount, Is.EqualTo(1));

            using (var escape = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape }))
            {
                escape.target = new TextField().Q("unity-text-input");
                shortcuts.HandleKeyDown(escape);
            }

            Assert.That(host.CancelInteractionsCount, Is.EqualTo(1),
                "a text field owns its own Escape (inline rename cancels itself)");
        }

        [Test]
        public void SearchShortcut_OpenSearchKeepsOtherCommandsWithItsOwnControls()
        {
            var host = new TrackingShortcutHost { IsSearchOpen = true, IsOverlayActive = true };
            var shortcuts = new ZepShortcutController(host);
            foreach (var key in new[] { KeyCode.Delete, KeyCode.Escape, KeyCode.Space, KeyCode.Return })
            {
                using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = key });
                shortcuts.HandleKeyDown(evt);
                Assert.That(evt.isPropagationStopped, Is.False, "Search controls own " + key);
            }
            Assert.That(host.DeleteSelectedCount, Is.Zero);
            Assert.That(host.FocusSearchCount, Is.Zero);
        }

        [Test]
        public void SearchShortcut_ChoicesExcludeUnityPlayTypingAndPanelBindings()
        {
            var manager = ShortcutManager.instance;
            var previousProfile = manager.activeProfileId;
            manager.activeProfileId = ShortcutManager.defaultProfileId;
            try
            {
                var available = ZepSearchShortcut.GetAvailableBindings();
                Assert.That(available, Is.Not.Empty);
                var play = new KeyCombination(KeyCode.P, ShortcutModifiers.Action);
                Assert.That(available, Has.No.Member(play));
                Assert.That(ZepSearchShortcut.GetUnavailableReason(play), Does.Contain("Unity"));
                var find = new KeyCombination(KeyCode.F, ShortcutModifiers.Action);
                Assert.That(available, Has.No.Member(find));
                Assert.That(ZepSearchShortcut.GetUnavailableReason(find), Does.Contain("Unity"));
                Assert.That(available, Has.No.Member(new KeyCombination(KeyCode.B, ShortcutModifiers.Action)));
                Assert.That(available, Has.No.Member(new KeyCombination(KeyCode.F10, ShortcutModifiers.Shift)));
                Assert.That(available.Any(b => b.keyCode == KeyCode.F10
                    && (b.modifiers & ShortcutModifiers.Shift) != 0), Is.False);
                Assert.That(available, Has.No.Member(new KeyCombination(KeyCode.K, ShortcutModifiers.None)));
                Assert.That(available, Has.No.Member(new KeyCombination(KeyCode.K, ShortcutModifiers.Shift)));
                Assert.That(ZepSearchShortcut.TrySet(play, out _), Is.False);
                Assert.That(ZepSearchShortcut.UsesDoubleShift, Is.True);
                Assert.That(EditorPrefs.HasKey(ZepSearchShortcut.PreferenceKey), Is.False);
            }
            finally { manager.activeProfileId = previousProfile; }
        }

        [TestCase(RuntimePlatform.OSXEditor, ShortcutModifiers.Control, ShortcutModifiers.Control)]
        [TestCase(RuntimePlatform.WindowsEditor, ShortcutModifiers.Control, ShortcutModifiers.Action)]
        [TestCase(RuntimePlatform.LinuxEditor, ShortcutModifiers.Control, ShortcutModifiers.Action)]
        public void SearchShortcut_NormalizesControlForThePlatform(RuntimePlatform platform,
            ShortcutModifiers input, ShortcutModifiers expected)
        {
            Assert.That(ZepSearchShortcut.Normalize(new KeyCombination(KeyCode.K, input), platform),
                Is.EqualTo(new KeyCombination(KeyCode.K, expected)));
        }

        [Test]
        public void SearchShortcut_CustomBindingReplacesDoubleShift_AndResetRestoresIt()
        {
            var binding = ZepSearchShortcut.GetAvailableBindings().First(b =>
                b.modifiers == (ShortcutModifiers.Action | ShortcutModifiers.Shift | ShortcutModifiers.Alt));
            Assert.That(ZepSearchShortcut.TrySet(binding, out _), Is.True);
            Assert.That(ZepSearchShortcut.Current, Is.EqualTo(binding));
            Assert.That(EditorPrefs.HasKey(ZepSearchShortcut.PreferenceKey), Is.True);
            var host = new TrackingShortcutHost();
            var clock = 10.0;
            var shortcuts = new ZepShortcutController(host, () => clock);
            ShiftDown(shortcuts, nativeModifiers: true);
            ShiftUp(shortcuts, true);
            clock += 0.1;
            ShiftDown(shortcuts, nativeModifiers: true);
            Assert.That(host.FocusSearchCount, Is.Zero);

            var modifiers = EventModifiers.Shift | EventModifiers.Alt
                | (Application.platform == RuntimePlatform.OSXEditor ? EventModifiers.Command : EventModifiers.Control);
            using (var wrong = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown,
                keyCode = binding.keyCode, modifiers = modifiers & ~EventModifiers.Shift }))
                shortcuts.HandleKeyDown(wrong);
            Assert.That(host.FocusSearchCount, Is.Zero);
            using (var selected = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown,
                keyCode = binding.keyCode, modifiers = modifiers }))
            {
                selected.target = new TextField().Q("unity-text-input");
                shortcuts.HandleKeyDown(selected);
                Assert.That(selected.isPropagationStopped, Is.True);
            }
            Assert.That(host.FocusSearchCount, Is.EqualTo(1));
            host.IsOverlayActive = true;
            using (var blocked = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown,
                keyCode = binding.keyCode, modifiers = modifiers }))
                shortcuts.HandleKeyDown(blocked);
            Assert.That(host.FocusSearchCount, Is.EqualTo(1));
            host.IsOverlayActive = false;
            Assert.That(ZepSearchShortcut.TrySet(default, out _), Is.True);
            Assert.That(EditorPrefs.HasKey(ZepSearchShortcut.PreferenceKey), Is.False);
            shortcuts.ResetDoubleShift();
            ShiftDown(shortcuts);
            ShiftUp(shortcuts);
            clock += 0.1;
            ShiftDown(shortcuts);
            Assert.That(host.FocusSearchCount, Is.EqualTo(2));
        }

        [Test]
        public void SearchShortcut_UnityRebindingRemovesChoiceAndDisablesSavedConflict_WithoutOverwritingIt()
        {
            var manager = ShortcutManager.instance;
            var previousProfile = manager.activeProfileId;
            var profile = "ZepShortcutTest-" + Guid.NewGuid().ToString("N");
            var shell = new ZepShellView(new ZepState());
            MountSearchHost(shell);
            var modifiers = shell.Q<PopupField<ShortcutModifiers>>("zep-search-shortcut-modifiers");
            var keys = shell.Q<PopupField<KeyCode>>("zep-search-shortcut-key");
            KeyCombination binding;
            try
            {
                manager.activeProfileId = ShortcutManager.defaultProfileId;
                manager.CreateProfile(profile);
                manager.activeProfileId = profile;
                binding = ZepSearchShortcut.GetAvailableBindings().First(b => b.keyCode == KeyCode.K);
                Assert.That(ZepSearchShortcut.TrySet(binding, out _), Is.True);
                var playId = manager.GetAvailableShortcutIds().First(id => manager.GetShortcutBinding(id)
                    .keyCombinationSequence.FirstOrDefault().Equals(new KeyCombination(KeyCode.P, ShortcutModifiers.Action)));
                manager.RebindShortcut(playId, new ShortcutBinding(binding));
                Assert.That(ZepSearchShortcut.GetAvailableBindings(), Has.No.Member(binding));
                Assert.That(ZepSearchShortcut.Current, Is.EqualTo(binding));
                Assert.That(ZepSearchShortcut.GetUnavailableReason(binding), Does.Contain(playId));
                var modifiersForKey = EventModifiers.None;
                if ((binding.modifiers & ShortcutModifiers.Action) != 0) modifiersForKey |=
                    Application.platform == RuntimePlatform.OSXEditor ? EventModifiers.Command : EventModifiers.Control;
                if ((binding.modifiers & ShortcutModifiers.Control) != 0) modifiersForKey |= EventModifiers.Control;
                if ((binding.modifiers & ShortcutModifiers.Alt) != 0) modifiersForKey |= EventModifiers.Alt;
                if ((binding.modifiers & ShortcutModifiers.Shift) != 0) modifiersForKey |= EventModifiers.Shift;
                Assert.That(ZepSearchShortcut.Matches(binding.keyCode, modifiersForKey, Application.platform),
                    Is.False, "A later Unity rebind must disable the saved Search chord immediately.");
                Assert.That(shell.Q<Label>("zep-search-shortcut-status").text, Does.Contain("Unavailable"));
                Assert.That(shell.Q<Button>("zep-search-button").tooltip, Is.EqualTo("Search assets"));
                modifiers.value = binding.modifiers;
                Assert.That(keys.choices, Has.No.Member(binding.keyCode));
                Assert.That(ZepSearchShortcut.TrySet(binding, out _), Is.False);
                manager.activeProfileId = ShortcutManager.defaultProfileId;
                Assert.That(ZepSearchShortcut.GetAvailableBindings(), Has.Member(binding));
                Assert.That(shell.Q<Label>("zep-search-shortcut-status").text, Does.Not.Contain("Unavailable"));
            }
            finally
            {
                manager.activeProfileId = previousProfile;
                manager.DeleteProfile(profile);
            }
        }

        [Test]
        public void SearchShortcut_SettingsSelectionPersistsAndUpdatesLauncher_ThenResetRestoresDefault()
        {
            var shell = new ZepShellView(new ZepState());
            MountSearchHost(shell);
            var screen = shell.Q<ZepSettingsScreen>();
            var binding = ZepSearchShortcut.GetAvailableBindings().First(b => b.keyCode == KeyCode.K);
            screen.Q<PopupField<ShortcutModifiers>>("zep-search-shortcut-modifiers").value = binding.modifiers;
            screen.Q<PopupField<KeyCode>>("zep-search-shortcut-key").value = binding.keyCode;
            var apply = screen.Q<Button>("zep-search-shortcut-apply");
            Assert.That(apply.enabledSelf, Is.True);
            using (var click = new NavigationSubmitEvent { target = apply }) apply.SendEvent(click);
            Assert.That(ZepSearchShortcut.Current, Is.EqualTo(binding));
            Assert.That(shell.Q<Button>("zep-search-button").tooltip,
                Is.EqualTo("Search assets (" + ZepSearchShortcut.Format(binding) + ")"));
            var another = new ZepShellView(new ZepState());
            Assert.That(another.Q<Label>("zep-search-shortcut-status").text,
                Does.Contain(ZepSearchShortcut.Format(binding)), "A new panel reads the persisted binding.");
            var reset = screen.Q<Button>("zep-search-shortcut-reset");
            using (var click = new NavigationSubmitEvent { target = reset }) reset.SendEvent(click);
            Assert.That(ZepSearchShortcut.UsesDoubleShift, Is.True);
            Assert.That(shell.Q<Button>("zep-search-button").tooltip, Is.EqualTo("Search assets (Double Shift)"));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void DoubleShift_RequiresRelease_AndDoesNotRepeatWhileHeld(bool nativeModifiers, bool searchOpen)
        {
            var host = new TrackingShortcutHost { IsSearchOpen = searchOpen, IsOverlayActive = searchOpen };
            var clock = 10.0;
            var shortcuts = new ZepShortcutController(host, () => clock);
            ShiftDown(shortcuts, nativeModifiers: nativeModifiers);
            clock += 0.05;
            ShiftDown(shortcuts, nativeModifiers: nativeModifiers);
            Assert.That(host.FocusSearchCount, Is.Zero);
            ShiftUp(shortcuts, nativeModifiers);
            clock += 0.1;
            ShiftDown(shortcuts, nativeModifiers: nativeModifiers);
            Assert.That(host.FocusSearchCount, Is.EqualTo(1));
            ShiftDown(shortcuts, nativeModifiers: nativeModifiers);
            ShiftUp(shortcuts, nativeModifiers);
            ShiftDown(shortcuts, nativeModifiers: nativeModifiers);
            Assert.That(host.FocusSearchCount, Is.EqualTo(1));
        }

        [Test, Combinatorial]
        public void DoubleShift_InterruptedGestureDoesNotOpenSearch(
            [Values("timeout", "other-key", "focus-loss", "modifier", "overlay", "prompt", "held")] string interruption,
            [Values(false, true)] bool nativeModifiers)
        {
            var host = new TrackingShortcutHost();
            var clock = 10.0;
            var shortcuts = new ZepShortcutController(host, () => clock);
            ShiftDown(shortcuts, nativeModifiers: nativeModifiers);
            clock += interruption == "held" ? 0.5 : 0.05;
            ShiftUp(shortcuts, nativeModifiers);
            if (interruption == "timeout") clock += 0.5;
            if (interruption == "focus-loss") shortcuts.ResetDoubleShift();
            if (interruption == "other-key")
            {
                using var other = KeyDownEvent.GetPooled(new Event
                    { type = EventType.KeyDown, keyCode = KeyCode.A });
                shortcuts.HandleKeyDown(other);
            }
            host.IsOverlayActive = interruption == "overlay";
            host.IsPromptOverlayVisible = interruption == "prompt";
            clock += 0.1;
            ShiftDown(shortcuts, interruption == "modifier" ? EventModifiers.Control : EventModifiers.None,
                nativeModifiers);
            Assert.That(host.FocusSearchCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DoubleShift_KeyAndModifierEvents_CountEachTapOnce(bool nativeFirst)
        {
            var host = new TrackingShortcutHost();
            var clock = 10.0;
            var shortcuts = new ZepShortcutController(host, () => clock);
            ShiftDown(shortcuts, nativeModifiers: nativeFirst);
            ShiftDown(shortcuts, nativeModifiers: !nativeFirst);
            Assert.That(host.FocusSearchCount, Is.Zero);
            clock += 0.05;
            ShiftUp(shortcuts, nativeFirst);
            ShiftUp(shortcuts, !nativeFirst);
            clock += 0.05;
            ShiftDown(shortcuts, nativeModifiers: nativeFirst);
            ShiftDown(shortcuts, nativeModifiers: !nativeFirst);
            Assert.That(host.FocusSearchCount, Is.EqualTo(1));
            ShiftUp(shortcuts, nativeFirst);
            ShiftUp(shortcuts, !nativeFirst);
            Assert.That(host.FocusSearchCount, Is.EqualTo(1));
        }

        [TestCase(EventModifiers.Control)]
        [TestCase(EventModifiers.Command)]
        [TestCase(EventModifiers.Alt)]
        public void DoubleShift_ReleasingAnotherModifierWhileShiftIsHeld_DoesNotStartATap(EventModifiers other)
        {
            var host = new TrackingShortcutHost();
            var clock = 10.0;
            var shortcuts = new ZepShortcutController(host, () => clock);
            shortcuts.HandleModifierKeysChanged(EventModifiers.Shift | other);
            clock += 0.05;
            shortcuts.HandleModifierKeysChanged(EventModifiers.Shift);
            shortcuts.HandleModifierKeysChanged(EventModifiers.None);
            clock += 0.05;
            shortcuts.HandleModifierKeysChanged(EventModifiers.Shift);
            Assert.That(host.FocusSearchCount, Is.Zero);
            shortcuts.HandleModifierKeysChanged(EventModifiers.None);
            clock += 0.05;
            shortcuts.HandleModifierKeysChanged(EventModifiers.Shift);
            Assert.That(host.FocusSearchCount, Is.EqualTo(1), "A fresh double tap must still work after a chord.");
        }

        private static void ShiftDown(ZepShortcutController controller,
            EventModifiers modifiers = EventModifiers.None, bool nativeModifiers = false)
        {
            if (nativeModifiers)
            {
                controller.HandleModifierKeysChanged(modifiers | EventModifiers.Shift);
                return;
            }
            using var evt = KeyDownEvent.GetPooled(new Event
                { type = EventType.KeyDown, keyCode = KeyCode.LeftShift, modifiers = modifiers | EventModifiers.Shift });
            controller.HandleKeyDown(evt);
        }

        private static void ShiftUp(ZepShortcutController controller, bool nativeModifiers = false)
        {
            if (nativeModifiers)
            {
                controller.HandleModifierKeysChanged(EventModifiers.None);
                return;
            }
            using var evt = KeyUpEvent.GetPooled(new Event
                { type = EventType.KeyUp, keyCode = KeyCode.LeftShift });
            controller.HandleKeyUp(evt);
        }

        private sealed class TrackingShortcutHost : IZepShortcutHost
        {
            public bool IsPromptOverlayVisible { get; set; }
            public bool IsOverlayActive { get; set; }
            public bool IsSearchOpen { get; set; }
            public bool IsQuickLookOpen => false;
            public IReadOnlyList<ZepAssetEntry> SelectedAssetEntries { get; set; } =
                Array.Empty<ZepAssetEntry>();
            public ZepFolderEntry SelectedFolderEntry { get; set; }
            public ZepController CommandHandler { get; set; }
            public IReadOnlyList<ZepTagFilterOption> TagFilterOptions { get; set; } =
                Array.Empty<ZepTagFilterOption>();
            public int FocusSearchCount { get; private set; }
            public bool TypeToSelectResult { get; set; }
            public List<string> TypeToSelectPrefixes { get; } = new List<string>();

            public void HidePromptOverlay() { }
            public void CloseOverlay() { }
            public void AnimateTagHighlight(string tagId) { }
            public void NavigateBack() { }
            public void NavigateForward() { }
            public void PromptCreateFolder() { }
            public void DuplicateSelected() { }
            public bool UndoRedoResult { get; set; } = true;
            public int UndoRequestedCount { get; private set; }
            public int RedoRequestedCount { get; private set; }

            public bool RedoRequested()
            {
                RedoRequestedCount++;
                return UndoRedoResult;
            }

            public bool UndoRequested()
            {
                UndoRequestedCount++;
                return UndoRedoResult;
            }
            public void PromptRenameSelected() { }
            public void OpenSelected() { }
            public int DeleteSelectedCount { get; private set; }
            public IEventHandler LastDeleteTarget { get; private set; }

            public void DeleteSelected(IEventHandler target)
            {
                DeleteSelectedCount++;
                LastDeleteTarget = target;
            }
            public void ToggleSidebar() { }
            public void FocusSearch() => FocusSearchCount++;
            public void QuickLookToggle() { }
            public void SelectAllAssets() { }
            public int CancelInteractionsCount { get; private set; }
            public void CancelInteractions() => CancelInteractionsCount++;
            public void NavigateToParentFolder() { }
            public bool MoveAssetSelection(AssetSelectionMove move, bool extend) => false;

            public bool TypeToSelectAsset(string prefix)
            {
                TypeToSelectPrefixes.Add(prefix);
                return TypeToSelectResult;
            }
        }

    }
}
