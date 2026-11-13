using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Core.Theming;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.AssetsArea;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepPaneWindowTests
    {
        private string _root;
        private string _externalRoot;
        private ZepState _state;
        private ZepControllerHost _host;

        [SetUp]
        public void SetUp()
        {
            _root = "Assets/PPDockedPaneFixture_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _root.Substring("Assets/".Length));
            AssetDatabase.CreateFolder(_root, "Child");
            _externalRoot = Path.Combine(Path.GetTempPath(), "PPDockedPaneExternal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_externalRoot);
            _state = new ZepState();
            _host = ZepControllerHost.Create(_state);
        }

        [TearDown]
        public void TearDown()
        {
            ZepSplitDragAndDrop.Clear();
            DragAndDrop.SetGenericData("NSFilenamesPboardType", null);
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.visualMode = DragAndDropVisualMode.None;
            _host.Dispose();
            AssetDatabase.DeleteAsset(_root);
            Directory.Delete(_externalRoot, recursive: true);
        }

        [UnityTest]
        public IEnumerator HostedPane_RendersNamesBreadcrumbAndSizeSlider_AfterRebuilding()
        {
            var sharedState = ZepControllerHost.SharedViewModel;
            var previousActive = sharedState.ActivePane;
            ZepControllerHost.Shared.Core.Refresh();
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            try
            {
                window.position = new Rect(100f, 100f, 640f, 400f);
                window.Show();
                for (var frame = 0; frame < 60 && window.Pane == null; frame++) yield return null;
                Assert.That(window.Pane, Is.Not.Null);

                // Exercise Unity's real host and a CreateGUI rebuild, not a
                // detached tree where the editor's default styles never exist.
                for (var rebuild = 0; rebuild < 2; rebuild++)
                {
                    if (rebuild > 0) window.CreateGUI();
                    window.position = new Rect(100f, 100f, rebuild == 0 ? 640f : 320f, 400f);
                    var root = window.rootVisualElement;
                    var view = root.Q<ZepPaneView>();
                    Assert.That(view, Is.Not.Null);
                    view.ApplyPresentation(AssetViewMode.Grid, 100);
                    for (var frame = 0; frame < 4; frame++) yield return null;

                    var name = root.Q<Label>("zep-asset-grid-name");
                    Assert.That(name, Is.Not.Null);
                    Assert.That(name.text, Is.EqualTo("Child"));
                    AssertRenderableText(name);
                    var breadcrumb = root.Q("zep-split-pane-breadcrumb")
                        .Q<Button>("zep-breadcrumb-segment");
                    Assert.That(breadcrumb.text, Is.EqualTo(_root.Substring("Assets/".Length)));
                    AssertRenderableText(breadcrumb);

                    var slider = root.Q<SliderInt>("zep-floating-grid-size-slider");
                    Assert.That(slider.worldBound.width, Is.GreaterThan(40f));
                    Assert.That(slider.Q("unity-tracker").worldBound.height, Is.GreaterThan(0f),
                        "The built-in slider needs Unity's default geometry, not just our color rules.");
                    Assert.That(slider.Q("unity-dragger").worldBound.width, Is.GreaterThan(0f));
                    slider.value = 172;
                    Assert.That(view.GridItemSize, Is.EqualTo(172));
                    view.ApplyPresentation(AssetViewMode.List, view.GridItemSize);
                    for (var frame = 0; frame < 2; frame++) yield return null;
                    AssertRenderableText(root.Q<Label>("zep-asset-row-name"));
                }
            }
            finally
            {
                window.Close();
                if (sharedState.IsLivePane(previousActive)) sharedState.ActivatePane(previousActive);
            }
        }

        private static void AssertRenderableText(TextElement text)
        {
            Assert.That(text, Is.Not.Null);
            Assert.That(text.text, Is.Not.Empty);
            var style = text.resolvedStyle;
            Assert.That(style.unityFont != null || style.unityFontDefinition.font != null
                || style.unityFontDefinition.fontAsset != null, Is.True,
                "Text must inherit the native editor font.");
            Assert.That(text.worldBound.width, Is.GreaterThan(0f));
            Assert.That(text.worldBound.height, Is.GreaterThan(0f));
            Assert.That(style.color.a, Is.GreaterThan(0f));
        }

        [Test]
        public void FinderDrop_OnPaneBackground_ImportsIntoItsCurrentFolder()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(), new[] { "Assets", _root, _root + "/Child" });
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            try
            {
                Bind(window, rig.State, rig.Controller);
                Invoke(window, "RestorePane");
                Assert.That(window.Pane.TrySetFolderSelection(_root + "/Child"), Is.True);
                var fileName = PrepareFinderDrag();

                Assert.That(DispatchExternalDrag(window, EventType.DragUpdated, Vector2.zero),
                    Is.EqualTo(EventType.Used));
                Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Copy));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/Child/" + fileName), Is.Null,
                    "Hover advertises the destination without importing.");
                Assert.That(DispatchExternalDrag(window, EventType.DragPerform, Vector2.zero),
                    Is.EqualTo(EventType.Used));

                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/Child/" + fileName)?.text,
                    Is.EqualTo("Finder import fixture"));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/" + fileName), Is.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/" + fileName), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [UnityTest]
        public IEnumerator FinderDrop_OnHoveredFolder_ImportsIntoThatFolder()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(), new[] { "Assets", _root, _root + "/Child" });
            var sharedState = ZepControllerHost.SharedViewModel;
            var previousActive = sharedState.ActivePane;
            ZepControllerHost.Shared.Core.Refresh();
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            try
            {
                window.position = new Rect(100f, 100f, 640f, 400f);
                window.Show();
                for (var frame = 0; frame < 60 && window.Pane == null; frame++) yield return null;
                Assert.That(window.Pane, Is.Not.Null);
                // Keep Unity's real native host and input dispatch, but isolate
                // controller history so importing a fixture cannot leave global
                // Undo records after the fixture is removed.
                Invoke(window, "OnDisable");
                Bind(window, rig.State, rig.Controller);
                Invoke(window, "RestorePane");
                var view = window.rootVisualElement.Q<ZepPaneView>();
                view.ApplyPresentation(AssetViewMode.Grid, 100);
                for (var frame = 0; frame < 4; frame++) yield return null;
                var child = view.Q<Label>("zep-asset-grid-name");
                Assert.That(child.text, Is.EqualTo("Child"));
                Assert.That(child.worldBound.width, Is.GreaterThan(0f));
                var point = child.worldBound.center;
                var fileName = PrepareFinderDrag();

                window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = point });
                Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Copy));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/Child/" + fileName), Is.Null);
                window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = point });
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/Child/" + fileName)?.text,
                    Is.EqualTo("Finder import fixture"));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/" + fileName), Is.Null,
                    "The hovered folder takes precedence over the window's current folder.");
            }
            finally
            {
                window.Close();
                if (sharedState.IsLivePane(previousActive)) sharedState.ActivatePane(previousActive);
            }
        }

        [Test]
        public void FinderFallback_IgnoresTypedSplitPayload()
        {
            Hydrate();
            var window = NewWindow();
            try
            {
                Invoke(window, "RestorePane");
                var payload = new ZepSplitDragPayload(
                    ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
                ZepSplitDragAndDrop.Prepare(payload);
                var fileName = PrepareFinderDrag();
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;

                Assert.That(DispatchExternalDrag(window, EventType.DragPerform, Vector2.zero),
                    Is.EqualTo(EventType.DragPerform));
                Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Link));
                Assert.That(ZepSplitDragAndDrop.TryRead(out var remaining), Is.True);
                Assert.That(remaining, Is.SameAs(payload));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/" + fileName), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void FinderDrop_OnTagBackground_RejectsWithoutRedirectingToAssets()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(), new[] { "Assets", _root, _root + "/Child" },
                new[] { new TagDefinitionRecord { Id = "window-tag", Name = "Window Tag" } });
            var window = ZepPaneWindow.Create(ZepPaneSource.FixedTag("window-tag"), "Window Tag");
            try
            {
                Bind(window, rig.State, rig.Controller);
                Invoke(window, "RestorePane");
                var fileName = PrepareFinderDrag();

                Assert.That(DispatchExternalDrag(window, EventType.DragPerform, Vector2.zero),
                    Is.EqualTo(EventType.Used));
                Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Rejected));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/" + fileName), Is.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/" + fileName), Is.Null,
                    "A tag result set must not redirect background imports to Assets.");
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        private string PrepareFinderDrag()
        {
            var fileName = "FinderFixture_" + Guid.NewGuid().ToString("N") + ".txt";
            var externalPath = Path.Combine(_externalRoot, fileName);
            File.WriteAllText(externalPath, "Finder import fixture");
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.SetGenericData("NSFilenamesPboardType", new[] { externalPath });
            return fileName;
        }

        private static EventType DispatchExternalDrag(
            ZepPaneWindow window, EventType type, Vector2 point)
        {
            // Event.current is null outside Unity's IMGUI dispatch even when
            // assigned. Exercise the shared native-drop boundary with its actual
            // event and this window's destination callback. The hosted case above
            // uses SendEvent to cover Unity's native event routing as well.
            var evt = new Event { type = type, mousePosition = point };
            var resolveDestination = (Func<Vector2, string>)Delegate.CreateDelegate(
                typeof(Func<Vector2, string>), window,
                typeof(ZepPaneWindow).GetMethod("ResolveExternalDropDestinationFolder",
                    BindingFlags.Instance | BindingFlags.NonPublic));
            var loggedUnusable = false;
            ZepWindow.HandleExternalDrag(evt, new MacOSDragSourcePathResolver(),
                window.Controller, resolveDestination, ref loggedUnusable);
            return evt.type;
        }

        [Test]
        public void Restore_WaitsForSourceReadinessWithoutDiscardingTheSavedRoot()
        {
            var window = NewWindow();
            try
            {
                Assert.That(_host.Core.IsPaneSourceReconciliationReady, Is.False);
                Invoke(window, "RestorePane");
                Assert.That(window.Pane, Is.Null);
                Assert.That(_state.WindowPanes, Is.Empty);

                Hydrate();
                Invoke(window, "RestorePane");

                Assert.That(window.Pane.Source.RootFolderPath.Value, Is.EqualTo(_root));
                Assert.That(_state.WindowPanes, Has.Count.EqualTo(1));
                Assert.That(window.rootVisualElement.Q<ZepShellView>(), Is.Null);
                Assert.That(window.rootVisualElement.Q<ZepPaneView>(), Is.Not.Null);
                Assert.That(window.rootVisualElement.Q("zep-sidebar"), Is.Null);
                Assert.That(window.rootVisualElement.Q("zep-split-pane-navigation-bar"), Is.Not.Null);
                var sheets = window.rootVisualElement.styleSheets;
                var packageSheets = 0;
                for (var i = 0; i < sheets.count; i++)
                {
                    if (AssetDatabase.GetAssetPath(sheets[i]).StartsWith(
                            "Packages/com.van-unity.zep/Editor/UI/Styles/", StringComparison.Ordinal))
                        packageSheets++;
                }
                Assert.That(packageSheets, Is.EqualTo(3));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void TwoWindowsOnTheSameRoot_BrowseIndependentlyAndCloseIndependently()
        {
            Hydrate();
            var first = NewWindow();
            var second = NewWindow();
            try
            {
                Invoke(first, "RestorePane");
                Invoke(second, "RestorePane");
                Assert.That(first.Pane, Is.Not.SameAs(second.Pane));
                Assert.That(_state.AuxiliaryPanes, Is.Empty);

                first.Pane.TrySetFolderSelection(_root + "/Child");
                Assert.That(first.Pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Child"));
                Assert.That(second.Pane.CurrentFolderPath.Value, Is.EqualTo(_root));
                Assert.That(first.Pane.TrySetFolderSelection("Assets"), Is.False);

                UnityEngine.Object.DestroyImmediate(first);
                first = null;
                Assert.That(_state.WindowPanes, Has.Count.EqualTo(1));
                Assert.That(_state.IsLivePane(second.Pane), Is.True);
                Assert.That(second.Pane.TrySetFolderSelection(_root + "/Child"), Is.True);
            }
            finally
            {
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void SerializedWindow_RestoresItsOwnNavigationHistoryAndPresentation()
        {
            Hydrate();
            var original = NewWindow();
            ZepPaneWindow restored = null;
            try
            {
                Invoke(original, "RestorePane");
                var pane = original.Pane;
                pane.SyncNavigationHistory();
                pane.TrySetFolderSelection(_root + "/Child");
                pane.SyncNavigationHistory();
                var view = original.rootVisualElement.Q<ZepPaneView>();
                view.ApplyPresentation(AssetViewMode.Grid, 172);
                Invoke(original, "SaveSnapshot");
                var json = EditorJsonUtility.ToJson(original);

                UnityEngine.Object.DestroyImmediate(original);
                original = null;
                restored = ScriptableObject.CreateInstance<ZepPaneWindow>();
                EditorJsonUtility.FromJsonOverwrite(json, restored);
                Bind(restored);
                Invoke(restored, "RestorePane");

                Assert.That(restored.Pane.Source.RootFolderPath.Value, Is.EqualTo(_root));
                Assert.That(restored.Pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Child"));
                Assert.That(restored.Pane.CanNavigateBack, Is.True);
                var restoredView = restored.rootVisualElement.Q<ZepPaneView>();
                Assert.That(restoredView.ViewMode, Is.EqualTo(AssetViewMode.Grid));
                Assert.That(restoredView.GridItemSize, Is.EqualTo(172));
                restored.Pane.NavigateBack();
                Assert.That(restored.Pane.CurrentFolderPath.Value, Is.EqualTo(_root));
                Assert.That(restored.Pane.CanNavigateForward, Is.True);
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
                if (restored != null) UnityEngine.Object.DestroyImmediate(restored);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GuidRemappedWindow_RestoresFolderAndTagHistoryBeforeLiveReconciliation(bool tagPane)
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(), new[] { "Assets", _root, _root + "/Child", _root + "/Icons" },
                new[] { new TagDefinitionRecord { Id = "window-tag", Name = "Window Tag" } });
            const string savedRoot = "Assets/PreviousWindowRoot";
            var entries = new List<ZepHistoryEntrySnapshot>();
            if (tagPane) entries.Add(new ZepHistoryEntrySnapshot { TagId = "window-tag" });
            foreach (var suffix in new[] { string.Empty, "/Child", "/Icons" })
            {
                entries.Add(new ZepHistoryEntrySnapshot
                {
                    FolderPath = savedRoot + suffix,
                    BrowseRootPath = tagPane ? savedRoot : string.Empty
                });
            }
            var snapshot = new ZepSplitPaneSnapshot
            {
                SourceKind = (int)(tagPane ? ZepPaneSourceKind.Tag : ZepPaneSourceKind.Folder),
                TagId = tagPane ? "window-tag" : string.Empty,
                FolderGuid = AssetDatabase.AssetPathToGUID(_root),
                LastKnownFolderPath = savedRoot,
                CurrentFolderPath = savedRoot + "/Icons",
                History = new ZepHistorySnapshot { Index = entries.Count - 1, Entries = entries }
            };
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            try
            {
                typeof(ZepPaneWindow).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(window, snapshot);
                Bind(window, rig.State, rig.Controller);

                Invoke(window, "RestorePane");
                rig.Controller.Refresh();

                var pane = window.Pane;
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Icons"));
                Assert.That(pane.NavigationHistoryIndex, Is.EqualTo(entries.Count - 1));
                pane.NavigateBack();
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Child"));
                pane.NavigateForward();
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Icons"));
                if (tagPane)
                {
                    pane.NavigateBack();
                    pane.NavigateBack();
                    pane.NavigateBack();
                    Assert.That(pane.CurrentEntry, Is.EqualTo(NavigationEntry.Tag("window-tag")));
                    pane.NavigateForward();
                    pane.NavigateForward();
                    pane.NavigateForward();
                    Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Icons"));
                    Assert.That(pane.TagBrowseRootPath.Value, Is.EqualTo(_root));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void SerializedTagWindow_RestoresTheTagAndItsOpenedFolder()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(), new[] { "Assets", _root, _root + "/Child" },
                new[] { new TagDefinitionRecord { Id = "window-tag", Name = "Window Tag" } });
            var original = ZepPaneWindow.Create(ZepPaneSource.FixedTag("window-tag"), "Window Tag");
            ZepPaneWindow restored = null;
            try
            {
                Bind(original, rig.State, rig.Controller);
                Invoke(original, "RestorePane");
                original.Pane.SyncNavigationHistory();
                Assert.That(original.Pane.TryEnterTagFolder(
                    AssetPath.From(_root), AssetPath.From(_root + "/Child")), Is.True);
                original.Pane.SyncNavigationHistory();
                Invoke(original, "SaveSnapshot");
                var json = EditorJsonUtility.ToJson(original);
                UnityEngine.Object.DestroyImmediate(original);
                original = null;

                restored = ScriptableObject.CreateInstance<ZepPaneWindow>();
                EditorJsonUtility.FromJsonOverwrite(json, restored);
                Bind(restored, rig.State, rig.Controller);
                Invoke(restored, "RestorePane");

                Assert.That(restored.Pane.Source.Kind, Is.EqualTo(ZepPaneSourceKind.Tag));
                Assert.That(restored.Pane.Source.TagId, Is.EqualTo("window-tag"));
                Assert.That(restored.titleContent.text, Is.EqualTo("Window Tag"));
                Assert.That(restored.Pane.TagBrowseRootPath.Value, Is.EqualTo(_root));
                Assert.That(restored.Pane.CurrentFolderPath.Value, Is.EqualTo(_root + "/Child"));
                restored.Pane.NavigateBack();
                Assert.That(restored.Pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
                Assert.That(restored.Pane.TagBrowseRootPath.IsEmpty, Is.True);
            }
            finally
            {
                if (original != null) UnityEngine.Object.DestroyImmediate(original);
                if (restored != null) UnityEngine.Object.DestroyImmediate(restored);
            }
        }

        [Test]
        public void SavedRootDeletedBeforeRestore_ClosesTheWindowWithoutOpeningAnotherFolder()
        {
            Hydrate();
            var source = ZepPaneSource.RootedFolder(AssetPath.From(_root));
            var snapshot = ZepWindow.CapturePaneSnapshot(
                new ZepPaneState(source), AssetViewMode.List,
                AssetsAreaConstants.DefaultAssetGridItemSize, AssetDatabase.AssetPathToGUID);
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets")), "Fixture");
            var sharedState = ZepControllerHost.SharedViewModel;
            var originalActivePane = sharedState.ActivePane;
            try
            {
                // Establish the native host so Close exercises a real Unity
                // window, then replay the disable/restore boundary against the
                // fixture's reconciled catalog, independently of shared scans.
                window.Show();
                Invoke(window, "OnDisable");
                if (sharedState.IsLivePane(originalActivePane)) sharedState.ActivatePane(originalActivePane);
                Bind(window);
                typeof(ZepPaneWindow).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(window, snapshot);

                Assert.That(AssetDatabase.DeleteAsset(_root), Is.True);
                // Batchmode can defer AssetPostprocessor delivery just as it
                // defers delayCall. Deliver the real deletion boundary before
                // asking the window to reconcile its serialized source.
                _host.Core.HandleAssetChanges(new ProjectAssetChanges(
                    Array.Empty<string>(), new[] { _root },
                    Array.Empty<string>(), Array.Empty<string>()));
                Assert.That(_host.Core.TryReconcilePaneSource(source, out _), Is.False);
                var complete = CaptureDeferredCall(() => Invoke(window, "RestorePane"));

                Assert.That(window.Pane, Is.Null);
                Assert.That(_state.WindowPanes, Is.Empty);
                Assert.That(complete, Is.Not.Null);
                complete();
                Assert.That(window == null, Is.True,
                    "an unavailable root closes its window instead of becoming a generic Assets browser");
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                if (sharedState.IsLivePane(originalActivePane)) sharedState.ActivatePane(originalActivePane);
            }
        }

        [Test]
        public void PaneWindowInput_ActivatesItsPaneOnlyWhenAnotherPaneIsActive()
        {
            using var rig = BuildFolderRig();
            var state = rig.State;
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            Bind(window, state, rig.Controller);
            var selection = Selection.objects;
            var activations = 0;
            Action<ZepPaneChange> count = change =>
            {
                if (change.Kind == ZepPaneChangeKind.Activated) activations++;
            };
            try
            {
                Invoke(window, "RestorePane");
                Assert.That(state.ActivePane, Is.SameAs(window.Pane));
                state.PaneChanged += count;

                // Unity re-sends focus and every key to the window that
                // already owns the active pane; none of it is a new activation.
                Invoke(window, "OnFocus");
                using (var key = KeyDownEvent.GetPooled('\0', KeyCode.F7, EventModifiers.None))
                    Invoke(window, "OnKeyDown", key);
                using (var command = ExecuteCommandEvent.GetPooled("SelectAll"))
                    Invoke(window, "OnExecuteCommand", command);
                Assert.That(activations, Is.Zero);

                state.ActivatePane(state.PrimaryPane);
                activations = 0;
                Invoke(window, "OnFocus");
                Assert.That(activations, Is.EqualTo(1));
                Assert.That(state.ActivePane, Is.SameAs(window.Pane));
            }
            finally
            {
                state.PaneChanged -= count;
                UnityEngine.Object.DestroyImmediate(window);
                Selection.objects = selection;
            }
        }

        [Test]
        public void PersistedThemeChange_RestylesThePaneWindowWithoutFocus()
        {
            var id = "zep-pane-follow-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var mainRegistry = ZepThemeProviderRegistration.CreateConfigured();
            var orchestrator = new ThemeSaveOrchestrator(mainRegistry);
            var prefs = SaveThemePreferences();
            using var rig = BuildFolderRig();
            ZepPaneWindow window = null;
            try
            {
                ZepThemePreferences.SetTheme(ZepThemeRegistry.DefaultThemeId, mainRegistry);
                window = NewThemedWindow(rig);
                Assert.That(window.ThemeSwitcher.CurrentTheme, Is.EqualTo(ZepThemeRegistry.DefaultThemeId));

                // The main window's settings create a theme and select it.
                var refresh = CaptureDeferredCall(() =>
                {
                    Assert.That(orchestrator.Save(id, "Pane Follow", false,
                        ThemeDerivation.Compute(new ZepColor(0.8f, 0.2f, 0.3f), isLight: false),
                        ThemeSaveMode.UserPrefs).Success, Is.True);
                    ZepThemePreferences.SetTheme(id, mainRegistry);
                });
                refresh?.Invoke();

                Assert.That(window.ThemeSwitcher.CurrentTheme, Is.EqualTo(id));
                Assert.That(window.rootVisualElement.ClassListContains(
                    ZepThemeRegistry.GetThemeClassName(id)), Is.True);
                var sheet = RuntimeThemeSheet(window);
                Assert.That(sheet, Is.Not.Null, "a user theme applies its own runtime sheet, not the default theme's");

                // Editing the selected theme restyles the window from the
                // edited definition, not from the one it first discovered.
                var edited = ThemeDerivation.Compute(new ZepColor(0.2f, 0.7f, 0.4f), isLight: false);
                refresh = CaptureDeferredCall(() => orchestrator.Save(id, "Pane Follow", false,
                    edited, ThemeSaveMode.UserPrefs));
                refresh?.Invoke();
                Assert.That(RuntimeThemeSheet(window), Is.Not.Null.And.Not.SameAs(sheet));
                var applied = window.ThemeRegistry.AvailableDefinitions.Single(definition => definition.Id == id);
                foreach (var variable in edited)
                {
                    var color = applied.Variables[variable.Key];
                    Assert.That(new[] { color.R, color.G, color.B, color.A },
                        Is.EqualTo(new[] { variable.Value.R, variable.Value.G, variable.Value.B, variable.Value.A })
                            .Within(0.01f), variable.Key);
                }
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                orchestrator.DeleteUserTheme(id, ZepThemeSource.UserPrefs);
                RestoreThemePreferences(prefs);
            }
        }

        [Test]
        public void Focus_DoesNotRebuildTheAppliedTheme()
        {
            var id = "zep-pane-focus-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var mainRegistry = ZepThemeProviderRegistration.CreateConfigured();
            var orchestrator = new ThemeSaveOrchestrator(mainRegistry);
            var prefs = SaveThemePreferences();
            using var rig = BuildFolderRig();
            ZepPaneWindow window = null;
            try
            {
                Assert.That(orchestrator.Save(id, "Pane Focus", false,
                    ThemeDerivation.Compute(new ZepColor(0.4f, 0.3f, 0.9f), isLight: false),
                    ThemeSaveMode.UserPrefs).Success, Is.True);
                ZepThemePreferences.SetTheme(id, mainRegistry);
                window = NewThemedWindow(rig);
                Assert.That(window.ThemeSwitcher.CurrentTheme, Is.EqualTo(id));

                ZepThemeSwitcher.ApplyThemeSheetCountForTests = 0;
                Invoke(window, "OnFocus");
                Assert.That(ZepThemeSwitcher.ApplyThemeSheetCountForTests, Is.Zero,
                    "a click into the window must not rebuild an unchanged user theme");
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                orchestrator.DeleteUserTheme(id, ZepThemeSource.UserPrefs);
                RestoreThemePreferences(prefs);
            }
        }

        [UnityTest]
        public IEnumerator ShownPaneWindow_TakesKeyboardFocusSoKeysReachIt()
        {
            var sharedState = ZepControllerHost.SharedViewModel;
            var previousActive = sharedState.ActivePane;
            var selection = Selection.objects;
            ZepControllerHost.Shared.Core.Refresh();
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            try
            {
                window.position = new Rect(100f, 100f, 640f, 400f);
                window.Show();
                window.Focus();
                for (var frame = 0; frame < 60 && window.Pane == null; frame++) yield return null;
                Assert.That(window.Pane, Is.Not.Null);
                var root = window.rootVisualElement;
                for (var frame = 0; frame < 60 && root.panel?.focusController?.focusedElement == null; frame++)
                    yield return null;
                var focused = root.panel?.focusController?.focusedElement as VisualElement;
                Assert.That(focused != null && (focused == root || root.Contains(focused)), Is.True,
                    "a docked or floated window must not wait for a click before it takes keys");

                window.SendEvent(Event.KeyboardEvent("down"));
                for (var frame = 0; frame < 30 && window.Pane.SelectedAssetPaths.Count == 0; frame++)
                    yield return null;
                Assert.That(window.Pane.SelectedAssetPaths.Select(path => path.Value),
                    Is.EqualTo(new[] { _root + "/Child" }));
            }
            finally
            {
                window.Close();
                if (sharedState.IsLivePane(previousActive)) sharedState.ActivatePane(previousActive);
                Selection.objects = selection;
            }
        }

        [Test]
        public void UnusedTagWindow_NamesTheTagFromItsDefinition()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(), new[] { "Assets", _root },
                new[] { new TagDefinitionRecord { Id = "window-tag", Name = "Window Tag", IsFavorite = false } });
            var window = ZepPaneWindow.Create(ZepPaneSource.FixedTag("window-tag"), "window-tag");
            try
            {
                Bind(window, rig.State, rig.Controller);
                Invoke(window, "RestorePane");
                Assert.That(rig.State.TagFilterOptions.Any(option => option.TagId == "window-tag"), Is.False,
                    "fixture: an unused, non-favorite tag is not a sidebar option");

                Assert.That(window.titleContent.text, Is.EqualTo("Window Tag"));
                Assert.That(LeadingCrumb(window), Is.EqualTo("Window Tag"));

                Assert.That(rig.Controller.UpdateTagDefinition(
                    new ZepTag("window-tag", "Renamed", ZepTagColor.Blue)), Is.True);
                Assert.That(window.titleContent.text, Is.EqualTo("Renamed"));
                Assert.That(LeadingCrumb(window), Is.EqualTo("Renamed"));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        private static string LeadingCrumb(ZepPaneWindow window) =>
            window.rootVisualElement.Q("zep-split-pane-breadcrumb")
                .Q<Button>("zep-breadcrumb-leading-segment")?.text;

        // A known catalog: a fresh fixture folder may not have reached the
        // shared index cache yet when the whole suite runs ahead of this.
        private ZepUiControllerRig BuildFolderRig() => ZepUiControllerRig.Build(
            Array.Empty<AssetRecord>(), new[] { "Assets", _root, _root + "/Child" });

        // Builds a pane and runs its deferred theme discovery, the way the
        // editor's next tick would.
        private ZepPaneWindow NewThemedWindow(ZepUiControllerRig rig)
        {
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            Bind(window, rig.State, rig.Controller);
            var discovery = CaptureDeferredCall(() => Invoke(window, "RestorePane"));
            Assert.That(window.Pane, Is.Not.Null);
            discovery?.Invoke();
            return window;
        }

        private static StyleSheet RuntimeThemeSheet(ZepPaneWindow window)
        {
            var sheets = window.rootVisualElement.styleSheets;
            for (var i = 0; i < sheets.count; i++)
            {
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sheets[i]))) return sheets[i];
            }
            return null;
        }

        private static readonly string[] ThemePreferenceKeys =
        {
            ZepThemePreferences.ThemeStringPreferenceKey,
            ZepThemePreferences.ThemeLegacyPreferenceKey,
            UserPrefsThemeProvider.EditorPrefsKey,
        };

        private static string[] SaveThemePreferences() => ThemePreferenceKeys
            .Select(key => EditorPrefs.HasKey(key)
                ? key == ZepThemePreferences.ThemeLegacyPreferenceKey
                    ? EditorPrefs.GetBool(key).ToString()
                    : EditorPrefs.GetString(key)
                : null)
            .ToArray();

        private static void RestoreThemePreferences(string[] values)
        {
            for (var i = 0; i < ThemePreferenceKeys.Length; i++)
            {
                var key = ThemePreferenceKeys[i];
                if (values[i] == null) EditorPrefs.DeleteKey(key);
                else if (key == ZepThemePreferences.ThemeLegacyPreferenceKey)
                    EditorPrefs.SetBool(key, bool.Parse(values[i]));
                else EditorPrefs.SetString(key, values[i]);
            }
        }

        // Batchmode does not pump delayCall during a test. Preserve unrelated
        // editor work and deliver only the callbacks armed by this window.
        private static EditorApplication.CallbackFunction CaptureDeferredCall(Action dispatch)
        {
            var pending = EditorApplication.delayCall;
            EditorApplication.delayCall = null;
            try
            {
                dispatch();
                return EditorApplication.delayCall;
            }
            finally
            {
                EditorApplication.delayCall = pending;
            }
        }

        private ZepPaneWindow NewWindow()
        {
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(_root)), "Fixture");
            Bind(window);
            return window;
        }

        private void Hydrate()
        {
            _host.Core.BeginPanelUse();
            _host.Core.Refresh();
            Assert.That(_host.Core.IsPaneSourceReconciliationReady, Is.True);
        }

        private void Bind(ZepPaneWindow window)
        {
            Bind(window, _state, _host.Core);
        }

        private static void Bind(ZepPaneWindow window, ZepState state,
            ZepController controller)
        {
            window.hideFlags = HideFlags.HideAndDontSave;
            typeof(ZepPaneWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(window, state);
            typeof(ZepPaneWindow).GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(window, controller);
        }

        private static void Invoke(ZepPaneWindow window, string method, params object[] args)
        {
            typeof(ZepPaneWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(window, args.Length == 0 ? null : args);
        }
    }
}
