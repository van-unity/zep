using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.ContextMenu;
using Zep.Editor.UI.Sidebar;
using Zep.Editor.UI.TopBar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepPaneViewTests
    {
        [Test]
        public void TagView_EmptyTag_ShowsThePlainTagEmptyMessage()
        {
            // The tag IS the container a tag view browses, so an untouched
            // empty tag must read "No assets with this tag." — counting the
            // tag itself as a filter made that message unreachable and every
            // empty tag claimed a filter the user never applied.
            var state = new ZepState();
            Assert.That(state.PrimaryPane.ApplyTagView("red"), Is.True);
            var view = CreateView(state, state.PrimaryPane);

            try
            {
                view.RefreshFromPane();
                var label = view.Q<Label>("zep-tags-feature-empty-state");
                Assert.That(label, Is.Not.Null);
                Assert.That(label.text, Is.EqualTo("No assets with this tag."));

                state.PrimaryPane.SetSearchText("anything");
                view.RefreshFromPane();
                Assert.That(
                    label.text,
                    Is.EqualTo("No assets match this tag and filter."),
                    "a real search on top of the tag is still a filter");
            }
            finally
            {
                view.Teardown();
            }
        }

        [Test]
        public void Primary_MountsExactlyItsTwoValidFeatures()
        {
            var state = new ZepState();
            var view = CreateView(state, state.PrimaryPane);

            Assert.That(view.Q("zep-browser-feature"), Is.Not.Null);
            Assert.That(view.Q("zep-tags-feature"), Is.Not.Null);
            Assert.That(view.AssetsArea.Children().OfType<IAssetsPaneFeature>().Count(), Is.EqualTo(2));
            Assert.That(
                view.Q<VisualElement>(
                    className: ZepUiClasses.SplitPaneNavigationBar),
                Is.Null);
            Assert.That(view.AssetsArea.Q<BreadcrumbWidget>(), Is.Not.Null);
            Assert.That(
                view.ClassListContains(ZepUiClasses.AssetsArea),
                Is.True,
                "The reusable wrapper must retain the primary asset area's flex sizing.");

            view.Teardown();
        }

        [Test]
        public void RootedFolder_MountsOnlyBrowser()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var view = CreateView(state, pane);

            Assert.That(view.Q("zep-browser-feature"), Is.Not.Null);
            Assert.That(view.Q("zep-tags-feature"), Is.Null);
            Assert.That(
                view.Q<VisualElement>(
                    className: ZepUiClasses.SplitPaneNavigationBar),
                Is.Not.Null);
            Assert.That(
                view.Q<NavigationButtonsWidget>(
                    "zep-split-pane-nav"),
                Is.Not.Null);
            Assert.That(
                view.Query<BreadcrumbWidget>().ToList(),
                Has.Count.EqualTo(1));
            Assert.That(view.AssetsArea.Q<BreadcrumbWidget>(), Is.Null);

            view.Teardown();
        }

        [Test]
        public void RootedFolderNavigationChrome_TargetsBoundPaneAndClampsBreadcrumb()
        {
            var state = new ZepState();
            state.PrimaryPane.TrySetFolderSelection("Assets/Primary");
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var view = CreateView(state, pane);
            view.RefreshFromPane();

            pane.TrySetFolderSelection("Assets/Art/UI");
            pane.TrySetFolderSelection("Assets/Art/UI/Icons");
            state.ActivatePane(state.PrimaryPane);

            var navigation = view.Q<NavigationButtonsWidget>(
                "zep-split-pane-nav");
            var back = view.Q<Button>("zep-split-pane-back");
            var forward = view.Q<Button>("zep-split-pane-forward");
            var breadcrumb = view.Q<BreadcrumbWidget>(
                "zep-split-pane-breadcrumb");
            var segments = breadcrumb.Query<Button>(
                    className: ZepUiClasses.BreadcrumbSegment)
                .ToList();

            Assert.That(segments.Select(segment => segment.text), Is.EqualTo(new[]
            {
                "Art",
                "UI",
                "Icons"
            }));
            Assert.That(segments.Select(segment => segment.tooltip), Is.EqualTo(new[]
            {
                "Assets/Art",
                "Assets/Art/UI",
                "Assets/Art/UI/Icons"
            }));
            Assert.That(back.enabledSelf, Is.True);
            Assert.That(forward.enabledSelf, Is.False);

            RaiseNavigationEvent(navigation, "BackClicked");

            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Primary")));
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            Assert.That(back.enabledSelf, Is.True);
            Assert.That(forward.enabledSelf, Is.True);

            RaiseNavigationEvent(navigation, "ForwardClicked");

            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI/Icons")));
            Assert.That(back.enabledSelf, Is.True);
            Assert.That(forward.enabledSelf, Is.False);

            RaiseBreadcrumbSegment(breadcrumb, "Assets/Art");

            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(
                state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Primary")));
            Assert.That(
                breadcrumb.Query<Button>(
                        className: ZepUiClasses.BreadcrumbSegment)
                    .ToList()
                    .Select(segment => segment.text),
                Is.EqualTo(new[] { "Art" }));

            view.Teardown();
        }

        [Test]
        public void FixedTag_MountsTagsAndBrowserWithNavigationChrome()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue")).NewPane;
            var view = CreateView(state, pane);
            view.RefreshFromPane();

            Assert.That(view.Q("zep-browser-feature"), Is.Not.Null);
            Assert.That(view.Q("zep-tags-feature"), Is.Not.Null);
            Assert.That(
                view.Q<VisualElement>(
                    className: ZepUiClasses.SplitPaneNavigationBar),
                Is.Not.Null);
            var breadcrumb = view.Q<BreadcrumbWidget>("zep-split-pane-breadcrumb");
            Assert.That(breadcrumb, Is.Not.Null);
            Assert.That(
                breadcrumb.Query<Button>(className: ZepUiClasses.BreadcrumbSegment)
                    .ToList()
                    .Select(segment => segment.text),
                Is.EqualTo(new[] { "blue" }),
                "on its list the pane shows only the tag crumb");

            view.Teardown();
        }

        // With no title row naming the pane's kind, the dot ahead of the tag
        // crumb is what tells a tag pane from a folder pane of the same name.
        [Test]
        public void TagPaneCrumb_CarriesTheTagColourDot_AndAFolderPaneCrumbHasNone()
        {
            // One auxiliary per state: a second OpenAuxiliary would replace
            // the first, so each pane gets its own state.
            var tagState = new ZepState();
            tagState.SetTagFilterOptions(new[]
            {
                new ZepTagFilterOption(
                    new ZepTag("blue", "Blue", ZepTagColor.Green),
                    0)
            });
            var tagPane = tagState.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue")).NewPane;
            var folderState = new ZepState();
            var folderPane = folderState.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Blue"))).NewPane;
            var tagView = CreateView(tagState, tagPane);
            var folderView = CreateView(folderState, folderPane);

            try
            {
                tagView.RefreshFromPane();
                folderView.RefreshFromPane();

                var tagCrumb = tagView.Q<BreadcrumbWidget>("zep-split-pane-breadcrumb");
                var dot = tagCrumb.Q<VisualElement>(className: ZepUiClasses.TagColorDot);
                Assert.That(dot, Is.Not.Null);
                Assert.That(
                    dot.style.backgroundColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Green)));
                Assert.That(
                    tagCrumb.IndexOf(dot),
                    Is.EqualTo(tagCrumb.IndexOf(
                        tagCrumb.Q<Button>("zep-breadcrumb-leading-segment")) - 1),
                    "the dot sits immediately ahead of the tag crumb");
                Assert.That(
                    tagCrumb.Q<Button>("zep-breadcrumb-leading-segment").text,
                    Is.EqualTo("Blue"));

                var folderCrumb = folderView.Q<BreadcrumbWidget>("zep-split-pane-breadcrumb");
                Assert.That(
                    folderCrumb.Q<VisualElement>(className: ZepUiClasses.TagColorDot),
                    Is.Null);
                Assert.That(
                    folderCrumb.Query<Button>(className: ZepUiClasses.BreadcrumbSegment)
                        .ToList()
                        .Select(segment => segment.text),
                    Is.EqualTo(new[] { "Blue" }));
            }
            finally
            {
                tagView.Teardown();
                folderView.Teardown();
            }
        }

        [Test]
        public void TagPaneNavigationChrome_OpensAFolderUnderTheTagCrumbAndReturnsThroughIt()
        {
            var state = new ZepState();
            state.PrimaryPane.TrySetFolderSelection("Assets/Primary");
            var pane = state.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue")).NewPane;
            var view = CreateView(state, pane);

            Assert.That(pane.TrySetFolderSelection("Assets/Art"), Is.True);
            Assert.That(pane.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            var breadcrumb = view.Q<BreadcrumbWidget>("zep-split-pane-breadcrumb");
            var back = view.Q<Button>("zep-split-pane-back");
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
            Assert.That(
                breadcrumb.Query<Button>(className: ZepUiClasses.BreadcrumbSegment)
                    .ToList()
                    .Select(segment => segment.text),
                Is.EqualTo(new[] { "blue", "Art", "UI" }),
                "the opened folder is the ceiling: nothing above it is offered");
            Assert.That(back.enabledSelf, Is.True);

            RaiseBreadcrumbSegment(breadcrumb, "Assets/Art");
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));

            RaiseBreadcrumbLeadingSegment(breadcrumb);
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(pane.TagBrowseRootPath.IsEmpty, Is.True);
            Assert.That(
                breadcrumb.Query<Button>(className: ZepUiClasses.BreadcrumbSegment)
                    .ToList()
                    .Select(segment => segment.text),
                Is.EqualTo(new[] { "blue" }));
            Assert.That(
                state.PrimaryPane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Primary")),
                "none of this touches the primary pane");

            view.Teardown();
        }

        [Test]
        public void VisibleAssetsProvider_ResolvesOnlyTheBoundPaneSelection()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var view = CreateView(state, pane);
            var asset = new ZepAssetEntry(
                "Hero.png",
                "Assets/Art/Hero.png",
                "Texture2D");
            view.BrowserFeature.SetAssets(new[] { asset });

            pane.SetSelectedAssetPath(AssetPath.From(asset.Path));
            state.PrimaryPane.SetSelectedAssetPath(AssetPath.From(asset.Path));

            Assert.That(pane.GetSelectedAssetEntries(), Is.EqualTo(new[] { asset }));
            Assert.That(state.PrimaryPane.GetSelectedAssetEntries(), Is.Empty);

            view.Teardown();
        }

        [Test]
        public void ActivationCallback_ActivatesBoundPaneBeforeItsActions()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var view = CreateView(state, pane);
            state.ActivatePane(state.PrimaryPane);
            ZepPaneState actionPane = null;
            view.OpenRequested += capturedPane => actionPane = capturedPane;

            Invoke(view, "ActivateBoundPane");
            RaiseAssetsAreaEvent(view.AssetsArea, "OpenRequested");

            Assert.That(state.ActivePane, Is.SameAs(pane));
            Assert.That(actionPane, Is.SameAs(pane));

            view.Teardown();
        }

        [Test]
        public void DetachClearsProviderAndSubscription_ReattachRestoresExactlyOnce()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var view = CreateView(state, pane);
            var provider = pane.VisibleAssetsProvider;

            Assert.That(provider, Is.Not.Null);
            Assert.That(CountPaneChangeSubscriptions(state, view), Is.EqualTo(1));

            Invoke(view, "OnDetachFromPanel", null);

            Assert.That(pane.VisibleAssetsProvider, Is.Null);
            Assert.That(CountPaneChangeSubscriptions(state, view), Is.Zero);

            Invoke(view, "OnAttachToPanel", null);
            Invoke(view, "OnAttachToPanel", null);

            Assert.That(pane.VisibleAssetsProvider, Is.SameAs(provider));
            Assert.That(CountPaneChangeSubscriptions(state, view), Is.EqualTo(1));

            view.Teardown();
        }

        [Test]
        public void TeardownDisablesChildInteractionRoutes()
        {
            var state = new ZepState();
            var view = CreateView(state, state.PrimaryPane);
            var openCount = 0;
            view.OpenRequested += pane => openCount++;

            view.Teardown();
            RaiseAssetsAreaEvent(view.AssetsArea, "OpenRequested");

            Assert.That(openCount, Is.Zero);
            Assert.That(state.PrimaryPane.VisibleAssetsProvider, Is.Null);
            Assert.That(CountPaneChangeSubscriptions(state, view), Is.Zero);
        }

        [Test]
        public void ClosedPaneCannotRestoreItsProviderOrSubscription()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var view = CreateView(state, pane);

            state.CloseAuxiliary(pane);
            Invoke(view, "OnAttachToPanel", null);

            Assert.That(pane.VisibleAssetsProvider, Is.Null);
            Assert.That(CountPaneChangeSubscriptions(state, view), Is.Zero);
        }

        [Test]
        public void AuxiliaryPresentation_InheritsWithoutOverwritingPrimaryPreferences()
        {
            var viewModeKey = ZepThemePreferences.AssetViewModePreferenceKey;
            var priorMode = EditorPrefs.GetString(viewModeKey, string.Empty);
            var priorSize = ZepThemePreferences.GetAssetGridItemSize();
            try
            {
                EditorPrefs.SetString(viewModeKey, AssetViewMode.List.ToString());
                ZepThemePreferences.SetAssetGridItemSize(82);
                var expectedPrimarySize =
                    ZepThemePreferences.GetAssetGridItemSize();
                var state = new ZepState();
                var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
                var view = new ZepPaneView(
                    state,
                    pane,
                    new UnityZepIconRepository(),
                    inheritedViewMode: AssetViewMode.Grid,
                    inheritedGridItemSize: 128);

                Assert.That(view.ViewMode, Is.EqualTo(AssetViewMode.Grid));
                Assert.That(view.GridItemSize, Is.EqualTo(128));
                Assert.That(
                    EditorPrefs.GetString(viewModeKey, string.Empty),
                    Is.EqualTo(AssetViewMode.List.ToString()));
                Assert.That(
                    ZepThemePreferences.GetAssetGridItemSize(),
                    Is.EqualTo(expectedPrimarySize));

                view.Teardown();
            }
            finally
            {
                if (string.IsNullOrEmpty(priorMode))
                {
                    EditorPrefs.DeleteKey(viewModeKey);
                }
                else
                {
                    EditorPrefs.SetString(viewModeKey, priorMode);
                }

                ZepThemePreferences.SetAssetGridItemSize(priorSize);
            }
        }

        [Test]
        public void EmptyAuxiliarySelection_NeverFallsBackToPrimarySidebarFolder()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            // The sidebar's trees hydrate through the controller's folder
            // queries, so the fixture needs a real one to have any folder to
            // report as selected.
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Assets/Art", "Packages" });
            var sidebar = new SidebarWidget(
                new UnityZepIconRepository(),
                SidebarWidget.DefaultPaneWidth,
                false,
                rig.Controller);
            sidebar.SetSelectedFolderPath("Assets/Art");
            sidebar.Flush();
            var primaryFolder = sidebar.SelectedFolderEntry;
            Assert.That(
                primaryFolder?.Path,
                Is.EqualTo("Assets/Art"),
                "the sidebar must report the selected folder for the primary-pane case to be meaningful");
            var actions = new ContextMenuActions(
                null,
                null,
                null,
                state,
                sidebar,
                null);
            var method = typeof(ContextMenuActions).GetMethod(
                "SelectedFolder",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.Invoke(actions, new object[] { pane }),
                Is.Null);
            Assert.That(
                method.Invoke(actions, new object[] { state.PrimaryPane }),
                Is.SameAs(primaryFolder));
        }

        [Test]
        public void CurrentFolderDrop_RoutesTheBoundPaneAndCapturedDestinationToTheDropOwner()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            pane.TrySetFolderSelection("Assets/Art/UI");
            var view = CreateView(
                state,
                pane,
                new AssetDropOperations(null, state, null, null));
            // The pane the drop belongs to is NOT the active one: the
            // destination must come from the pane this view is bound to.
            state.ActivatePane(state.PrimaryPane);
            ResetDropOwnerSeam();

            RaiseCurrentFolderDrop(view.BrowserFeature, "Assets/Other.asset");

            Assert.That(AssetDropOperations.FolderDropsReceivedForTests, Is.EqualTo(1));
            Assert.That(
                AssetDropOperations.LastFolderDropPaneForTests,
                Is.SameAs(pane));
            Assert.That(
                AssetDropOperations.LastFolderDropDestinationForTests,
                Is.EqualTo("Assets/Art/UI"));

            view.Teardown();
            RaiseCurrentFolderDrop(view.BrowserFeature, "Assets/Other.asset");

            Assert.That(AssetDropOperations.FolderDropsReceivedForTests, Is.EqualTo(1));
        }

        [Test]
        public void CreateGhost_IsHostedByTheOriginatingWritablePane()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            var view = CreateView(state, pane);
            var orchestrator = new ZepCreateOrchestrator(
                state,
                null,
                candidate => ReferenceEquals(candidate, pane)
                    ? view.BrowserFeature
                    : null,
                null,
                null);

            orchestrator.BeginCreateFolder(pane, "Assets/Art");

            Assert.That(view.BrowserFeature.HasActiveInlineEdit, Is.True);
            Assert.That(state.PrimaryPane.SelectedAssetPaths, Is.Empty);

            view.CancelInlineEdit();
            view.Teardown();
        }

        // A pane view is built detached and mounted a moment later. Only the
        // mount may pull from the pane state: a constructor pull is thrown
        // away by the attach handler's pull and cost a second full projection
        // push per pane (audit 5.13).
        [Test]
        public void MountingAPaneView_PullsFromItsPaneExactlyOnce()
        {
            var window = ScriptableObject.CreateInstance<PaneViewMountTestWindow>();
            try
            {
                window.position = new Rect(100f, 100f, 640f, 480f);
                window.Show();

                var state = new ZepState();
                ZepPaneView.RefreshFromPaneCountForTests = 0;
                var view = CreateView(state, state.PrimaryPane);
                var afterConstruction =
                    ZepPaneView.RefreshFromPaneCountForTests;
                window.rootVisualElement.Add(view);

                Assert.That(
                    afterConstruction,
                    Is.Zero,
                    "construction must not pull — the attach handler owns the first refresh");
                Assert.That(
                    ZepPaneView.RefreshFromPaneCountForTests,
                    Is.EqualTo(1),
                    "the mount pulls once, not once per wiring step");

                view.Teardown();
            }
            finally
            {
                window.Close();
            }
        }

        private sealed class PaneViewMountTestWindow : EditorWindow
        {
        }

        private static ZepPaneView CreateView(
            ZepState state,
            ZepPaneState pane,
            AssetDropOperations dropOps = null)
        {
            return new ZepPaneView(
                state,
                pane,
                new UnityZepIconRepository(),
                dropOps: dropOps);
        }

        private static void ResetDropOwnerSeam()
        {
            AssetDropOperations.FolderDropsReceivedForTests = 0;
            AssetDropOperations.LastFolderDropPaneForTests = null;
            AssetDropOperations.LastFolderDropDestinationForTests = null;
        }

        private static void RaiseCurrentFolderDrop(
            AssetsBrowserFeature browser,
            string path)
        {
            var field = typeof(AssetsBrowserFeature).GetField(
                "CurrentFolderDropReceived",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action<System.Collections.Generic.IReadOnlyList<string>>)
                field.GetValue(browser))?.Invoke(new[] { path });
        }

        private static void Invoke(object target, string methodName, object argument = null)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var parameterCount = argument == null && methodName == "ActivateBoundPane" ? 0 : 1;
            var method = target.GetType()
                .GetMethods(flags)
                .Single(candidate =>
                    candidate.Name == methodName
                    && candidate.GetParameters().Length == parameterCount);
            method.Invoke(target, parameterCount == 0 ? null : new[] { argument });
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

        private static void RaiseNavigationEvent(
            NavigationButtonsWidget navigation,
            string eventName)
        {
            var field = typeof(NavigationButtonsWidget).GetField(
                eventName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action)field.GetValue(navigation))?.Invoke();
        }

        private static void RaiseBreadcrumbSegment(
            BreadcrumbWidget breadcrumb,
            string path)
        {
            var field = typeof(BreadcrumbWidget).GetField(
                "SegmentClicked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action<string>)field.GetValue(breadcrumb))?.Invoke(path);
        }

        private static void RaiseBreadcrumbLeadingSegment(BreadcrumbWidget breadcrumb)
        {
            var field = typeof(BreadcrumbWidget).GetField(
                "LeadingSegmentClicked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            ((Action)field.GetValue(breadcrumb))?.Invoke();
        }

        private static int CountPaneChangeSubscriptions(
            ZepState state,
            ZepPaneView view)
        {
            var field = typeof(ZepState).GetField(
                "PaneChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var handler = field.GetValue(state) as Delegate;
            return handler?.GetInvocationList().Count(entry => ReferenceEquals(entry.Target, view)) ?? 0;
        }

        // -----------------------------------------------------------------------
        // F-005 — a failed inline create must report itself through the toast
        // (showError), the same channel CommitRename already uses for a refused
        // rename, instead of the ghost row just vanishing.
        // -----------------------------------------------------------------------

        [Test]
        public void ExecuteMenuFallback_UnresolvableMenuPath_ReportsShowErrorOnce()
        {
            var rootPath = CreateShowErrorTestFolder("UnresolvableMenu");
            try
            {
                var state = new ZepState();
                var errorCount = 0;
                string lastError = null;
                var orchestrator = new ZepCreateOrchestrator(
                    state,
                    null,
                    pane => null,
                    (pane, path) => { },
                    null,
                    message => { errorCount++; lastError = message; });

                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                    "no menu named"));
                InvokeCreateMenuFallback(
                    orchestrator,
                    state.PrimaryPane,
                    "Assets/Create/Zep F-005 Does Not Exist",
                    rootPath,
                    "ProbeUnresolvableMenu");

                Assert.That(errorCount, Is.EqualTo(1));
                Assert.That(lastError, Does.Contain("ProbeUnresolvableMenu"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(rootPath);
            }
        }

        // Deliberately produces no asset on either the immediate or the delayCall
        // attempt — proves the "final attempt cannot resolve a created asset" leg
        // (F-005) without depending on any real Unity create machinery misbehaving.
        [MenuItem("Assets/Create/Zep F-005 Unresolved Probe", false, 10001)]
        private static void CreateF005UnresolvedProbeAsset()
        {
        }

        [Test]
        public void ExecuteMenuFallback_FinalAttemptNeverResolves_ReportsShowErrorExactlyOnce()
        {
            var rootPath = CreateShowErrorTestFolder("FinalAttempt");
            try
            {
                var state = new ZepState();
                var errorCount = 0;
                string lastError = null;
                var orchestrator = new ZepCreateOrchestrator(
                    state,
                    null,
                    pane => null,
                    (pane, path) => { },
                    null,
                    message => { errorCount++; lastError = message; });

                InvokeCreateMenuFallback(
                    orchestrator,
                    state.PrimaryPane,
                    "Assets/Create/Zep F-005 Unresolved Probe",
                    rootPath,
                    "ProbeUnresolved");
                Assert.That(
                    errorCount,
                    Is.Zero,
                    "the immediate attempt failing must not report before the delayCall retry has had its turn");

                var runDelayCalls = typeof(EditorApplication).GetMethod(
                    "Internal_CallDelayFunctions",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(runDelayCalls, Is.Not.Null);
                runDelayCalls.Invoke(null, null);

                Assert.That(errorCount, Is.EqualTo(1));
                Assert.That(lastError, Does.Contain("ProbeUnresolved"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(rootPath);
            }
        }

        [Test]
        public void CommitCreateAsset_RecipeFailure_ReportsShowErrorOnceWithTheRecipesOwnMessage()
        {
            var rootPath = CreateShowErrorTestFolder("RecipeFailure");
            try
            {
                var state = new ZepState();
                var errorCount = 0;
                string lastError = null;
                var orchestrator = new ZepCreateOrchestrator(
                    state,
                    null,
                    pane => null,
                    (pane, path) => { },
                    null,
                    message => { errorCount++; lastError = message; });

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "No supported named-create recipe"));
                InvokeCommitCreateAsset(
                    orchestrator,
                    state.PrimaryPane,
                    "Assets/Create/Zep F-005 Missing Recipe",
                    rootPath,
                    "New Asset",
                    "ProbeRecipeFailure",
                    hasRecipe: true);

                Assert.That(errorCount, Is.EqualTo(1));
                Assert.That(lastError, Does.Contain("No supported named-create recipe"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(rootPath);
            }
        }

        [Test]
        public void CommitCreateAsset_Success_ReportsNoShowError()
        {
            var rootPath = CreateShowErrorTestFolder("Success");
            try
            {
                var state = new ZepState();
                var errorCount = 0;
                var orchestrator = new ZepCreateOrchestrator(
                    state,
                    null,
                    pane => null,
                    (pane, path) => { },
                    null,
                    message => errorCount++);

                InvokeCommitCreateAsset(
                    orchestrator,
                    state.PrimaryPane,
                    "Assets/Create/Physics Material",
                    rootPath,
                    "New Physics Material",
                    "ProbeCreateSuccess",
                    hasRecipe: true);

                Assert.That(errorCount, Is.Zero);
                Assert.That(
                    AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(
                        rootPath + "/ProbeCreateSuccess.physicMaterial"),
                    Is.Not.Null);
            }
            finally
            {
                AssetDatabase.DeleteAsset(rootPath);
            }
        }

        [Test]
        public void CommitCreateAsset_WhitespaceTypedName_IsACancelAndReportsNoShowError()
        {
            var rootPath = CreateShowErrorTestFolder("Cancel");
            try
            {
                var state = new ZepState();
                var errorCount = 0;
                var orchestrator = new ZepCreateOrchestrator(
                    state,
                    null,
                    pane => null,
                    (pane, path) => { },
                    null,
                    message => errorCount++);

                InvokeCommitCreateAsset(
                    orchestrator,
                    state.PrimaryPane,
                    "Assets/Create/Physics Material",
                    rootPath,
                    string.Empty,
                    "   ",
                    hasRecipe: true);

                Assert.That(errorCount, Is.Zero);
            }
            finally
            {
                AssetDatabase.DeleteAsset(rootPath);
            }
        }

        private static string CreateShowErrorTestFolder(string label)
        {
            var folderName = "F005ShowErrorTests_" + label + "_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName);
            return "Assets/" + folderName;
        }

        private static void InvokeCreateMenuFallback(
            ZepCreateOrchestrator orchestrator,
            ZepPaneState pane,
            string menuPath,
            string targetFolder,
            string basename)
        {
            var method = typeof(ZepCreateOrchestrator).GetMethod(
                "ExecuteUnityMenuItemFallback",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                orchestrator,
                new object[] { pane, menuPath, targetFolder, basename, null });
        }

        private static void InvokeCommitCreateAsset(
            ZepCreateOrchestrator orchestrator,
            ZepPaneState pane,
            string menuPath,
            string targetFolder,
            string defaultBasename,
            string typedBasename,
            bool hasRecipe)
        {
            var method = typeof(ZepCreateOrchestrator).GetMethod(
                "CommitCreateAsset",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                orchestrator,
                new object[]
                {
                    pane, menuPath, targetFolder, defaultBasename, typedBasename, hasRecipe, null
                });
        }
    }

    public sealed class ZepCreateFallbackCaptureTests
    {
        private string _rootPath;
        private string _rootAbsolutePath;

        [SetUp]
        public void SetUp()
        {
            _rootPath = "Assets/CreateFallbackCaptureTests_" + Guid.NewGuid().ToString("N");
            _rootAbsolutePath = ProjectPaths.ToAbsolutePath(_rootPath);
            Directory.CreateDirectory(_rootAbsolutePath);
            AssetDatabase.ImportAsset(_rootPath, ImportAssetOptions.ForceSynchronousImport);
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            ZepPerfLog.IsEnabled = false;
            AssetDatabase.DeleteAsset(_rootPath);
        }

        [Test]
        public void ScopedCapture_SynchronousCallbackAndDirectEntry_ResolveWithoutFindAssets()
        {
            using var capture = new ZepCreateFallbackCapture(
                new[] { _rootPath },
                new[] { "Assets" });
            var createdPath = _rootPath + "/Created.asset";
            File.WriteAllText(ProjectPaths.ToAbsolutePath(createdPath), "created");
            capture.RecordChanges(new ProjectAssetChanges(
                new[] { createdPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));

            Assert.That(capture.TryResolve(string.Empty, true, out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(createdPath));
            var snapshot = ZepPerfLog.CaptureSnapshot();
            Assert.That(
                snapshot.GetCount(ZepPerfCounter.CreateFallbackScopedCaptures),
                Is.EqualTo(1));
            Assert.That(
                snapshot.GetCount(ZepPerfCounter.AssetDatabaseFindAssetsCalls),
                Is.Zero);
        }

        [Test]
        public void ScopedCapture_DelayedCallbackIsOptionalWhenOneDirectFileAppeared()
        {
            using var capture = new ZepCreateFallbackCapture(
                new[] { _rootPath },
                new[] { "Assets" });
            var createdPath = _rootPath + "/Delayed.asset";
            File.WriteAllText(ProjectPaths.ToAbsolutePath(createdPath), "created");

            Assert.That(capture.TryResolve(string.Empty, false, out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(createdPath));
        }

        [Test]
        public void ScopedCapture_ReimportOfExistingPathIsNotAttributedAsCreation()
        {
            var existingPath = _rootPath + "/Existing.asset";
            File.WriteAllText(ProjectPaths.ToAbsolutePath(existingPath), "existing");
            using var capture = new ZepCreateFallbackCapture(
                new[] { _rootPath },
                new[] { "Assets" });
            capture.RecordChanges(new ProjectAssetChanges(
                new[] { existingPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));

            Assert.That(capture.TryResolve(existingPath, true, out _), Is.False);
        }

        [Test]
        public void ScopedCapture_MultipleNewAssetsUsesOnlyASelectedCandidate()
        {
            using var capture = new ZepCreateFallbackCapture(
                new[] { _rootPath },
                new[] { "Assets" });
            var firstPath = _rootPath + "/First.asset";
            var secondPath = _rootPath + "/Second.asset";
            File.WriteAllText(ProjectPaths.ToAbsolutePath(firstPath), "first");
            File.WriteAllText(ProjectPaths.ToAbsolutePath(secondPath), "second");
            capture.RecordChanges(new ProjectAssetChanges(
                new[] { firstPath, secondPath },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()));

            Assert.That(capture.TryResolve(secondPath, true, out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(secondPath));
        }

        [Test]
        public void ScopedCapture_AmbiguousConcurrentAdditionsFailClosed()
        {
            using var capture = new ZepCreateFallbackCapture(
                new[] { _rootPath },
                new[] { "Assets" });
            File.WriteAllText(ProjectPaths.ToAbsolutePath(_rootPath + "/First.asset"), "first");
            File.WriteAllText(ProjectPaths.ToAbsolutePath(_rootPath + "/Second.asset"), "second");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "produced multiple candidate assets"));
            Assert.That(capture.TryResolve(string.Empty, true, out _), Is.False);
            Assert.That(
                ZepPerfLog.CaptureSnapshot().GetCount(
                    ZepPerfCounter.CreateFallbackAmbiguousResults),
                Is.EqualTo(1));
        }

        [Test]
        public void MissingScopedFolder_UsesCountedWarnedProjectGuidFallback()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "project-wide GUID compatibility fallback"));
            using var capture = new ZepCreateFallbackCapture(
                new[] { _rootPath + "/Missing" },
                new[] { "Assets" });
            var createdPath = _rootPath + "/Fallback.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), createdPath);

            Assert.That(capture.TryResolve(createdPath, true, out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(createdPath));
            var snapshot = ZepPerfLog.CaptureSnapshot();
            Assert.That(
                snapshot.GetCount(ZepPerfCounter.CreateFallbackProjectWideGuidDiffs),
                Is.EqualTo(1));
            Assert.That(
                snapshot.GetCount(ZepPerfCounter.AssetDatabaseFindAssetsCalls),
                Is.EqualTo(2));
        }

        [Test]
        public void ExecuteMenuFallback_SynchronousCreateLandsWithZeroProjectGuidScans()
        {
            var state = new ZepState();
            string landedPath = null;
            var orchestrator = new ZepCreateOrchestrator(
                state,
                null,
                pane => null,
                (pane, path) => landedPath = path,
                null);

            InvokeMenuFallback(
                orchestrator,
                state.PrimaryPane,
                "Assets/Create/Zep Phase 6 Single Probe",
                _rootPath,
                "FinalSingle");

            Assert.That(landedPath, Is.EqualTo(_rootPath + "/FinalSingle.anim"));
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(landedPath), Is.Not.Null);
            var snapshot = ZepPerfLog.CaptureSnapshot();
            Assert.That(
                snapshot.GetCount(ZepPerfCounter.AssetDatabaseFindAssetsCalls),
                Is.Zero);
            Assert.That(
                snapshot.GetCount(ZepPerfCounter.CreateFallbackScopedCaptures),
                Is.EqualTo(1));
        }

        [Test]
        public void ExecuteMenuFallback_MultipleCreatesMovesOnlyTheSelectedCandidate()
        {
            var state = new ZepState();
            string landedPath = null;
            var orchestrator = new ZepCreateOrchestrator(
                state,
                null,
                pane => null,
                (pane, path) => landedPath = path,
                null);

            InvokeMenuFallback(
                orchestrator,
                state.PrimaryPane,
                "Assets/Create/Zep Phase 6 Multiple Probe",
                _rootPath,
                "FinalSelected");

            Assert.That(landedPath, Is.EqualTo(_rootPath + "/FinalSelected.anim"));
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(landedPath), Is.Not.Null);
            Assert.That(
                AssetDatabase.LoadAssetAtPath<AnimationClip>(_rootPath + "/Companion.anim"),
                Is.Not.Null,
                "the unselected companion output must stay at Unity's chosen path");
        }

        [Test]
        public void ExecuteMenuFallback_NextTickCreateIsCapturedAndLanded()
        {
            var state = new ZepState();
            string landedPath = null;
            var orchestrator = new ZepCreateOrchestrator(
                state,
                null,
                pane => null,
                (pane, path) => landedPath = path,
                null);

            InvokeMenuFallback(
                orchestrator,
                state.PrimaryPane,
                "Assets/Create/Zep Phase 6 Delayed Probe",
                _rootPath,
                "FinalDelayed");
            Assert.That(landedPath, Is.Null);

            var runDelayCalls = typeof(EditorApplication).GetMethod(
                "Internal_CallDelayFunctions",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(runDelayCalls, Is.Not.Null);
            runDelayCalls.Invoke(null, null);

            Assert.That(landedPath, Is.EqualTo(_rootPath + "/FinalDelayed.anim"));
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(landedPath), Is.Not.Null);
            Assert.That(
                ZepPerfLog.CaptureSnapshot().GetCount(
                    ZepPerfCounter.AssetDatabaseFindAssetsCalls),
                Is.Zero);
        }

        [MenuItem("Assets/Create/Zep Phase 6 Single Probe", false, 9998)]
        private static void CreateSingleProbeAsset()
        {
            var folder = AssetDatabase.GetAssetPath(Selection.activeObject);
            var path = folder.TrimEnd('/') + "/UnitySingle.anim";
            var asset = new AnimationClip();
            AssetDatabase.CreateAsset(asset, path);
            Selection.activeObject = asset;
        }

        [MenuItem("Assets/Create/Zep Phase 6 Multiple Probe", false, 9999)]
        private static void CreateMultipleProbeAssets()
        {
            var folder = AssetDatabase.GetAssetPath(Selection.activeObject);
            AssetDatabase.CreateAsset(
                new AnimationClip(),
                folder.TrimEnd('/') + "/Companion.anim");
            var selected = new AnimationClip();
            AssetDatabase.CreateAsset(
                selected,
                folder.TrimEnd('/') + "/UnitySelected.anim");
            Selection.activeObject = selected;
        }

        [MenuItem("Assets/Create/Zep Phase 6 Delayed Probe", false, 10000)]
        private static void CreateDelayedProbeAsset()
        {
            var folder = AssetDatabase.GetAssetPath(Selection.activeObject);
            EditorApplication.delayCall += () =>
            {
                var asset = new AnimationClip();
                AssetDatabase.CreateAsset(
                    asset,
                    folder.TrimEnd('/') + "/UnityDelayed.anim");
                Selection.activeObject = asset;
            };
        }

        private static void InvokeMenuFallback(
            ZepCreateOrchestrator orchestrator,
            ZepPaneState pane,
            string menuPath,
            string targetFolder,
            string basename)
        {
            var method = typeof(ZepCreateOrchestrator).GetMethod(
                "ExecuteUnityMenuItemFallback",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                orchestrator,
                new object[] { pane, menuPath, targetFolder, basename, null });
        }
    }
}
