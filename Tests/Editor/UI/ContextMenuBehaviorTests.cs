using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Search;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Indexing;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI.Tests
{
    public sealed class ContextMenuBehaviorTests
    {
        private static ZepContextMenuItem Item(string path)
        {
            return new ZepContextMenuItem(
                path,
                path,
                path.Substring(path.LastIndexOf('/') + 1),
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>());
        }

        private static ZepContextMenuRequest Request(
            ZepState state,
            IReadOnlyList<string> contextPaths,
            ZepContextMenuTargetKind targetKind,
            IReadOnlyList<ZepAssetEntry> selectedAssets = null,
            ZepFolderEntry selectedFolder = null)
        {
            return new ZepContextMenuRequest(
                pane: state.PrimaryPane,
                commandHandler: null,
                contextPaths: contextPaths,
                createTargetFolderPath: "Assets",
                availableTags: Array.Empty<ZepTagFilterOption>(),
                assignedTagsByPath: new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase),
                selectedAssets: selectedAssets,
                selectedFolder: selectedFolder,
                isOpenFolderEnabled: true,
                isDeleteEnabled: true,
                isCreationAllowed: true,
                canModifyAllContextPaths: false,
                targetKind: targetKind,
                anchor: null);
        }

        [Test]
        public void Request_CopiesContextPathsAtConstruction()
        {
            var state = new ZepState();
            var mutablePaths = new List<string> { "Assets/Original.asset" };
            var request = Request(
                state,
                mutablePaths,
                ZepContextMenuTargetKind.Selection);

            mutablePaths[0] = "Assets/Changed.asset";
            mutablePaths.Add("Assets/Another.asset");

            Assert.That(request.ContextPaths, Is.EqualTo(new[] { "Assets/Original.asset" }));
        }

        [Test]
        public void Request_CopiesTagAndSelectionCollectionsAtConstruction()
        {
            var state = new ZepState();
            var path = "Assets/Original.asset";
            var assigned = new List<string> { "blue" };
            var tagMap = new Dictionary<string, ICollection<string>>
            {
                { path, assigned }
            };
            var assets = new List<ZepAssetEntry>
            {
                new ZepAssetEntry("Original", path, "Material")
            };
            var tags = new List<ZepTagFilterOption>
            {
                new ZepTagFilterOption(
                    new ZepTag("blue", "Blue", ZepTagColor.Blue, true),
                    1)
            };
            var request = new ZepContextMenuRequest(
                state.PrimaryPane,
                null,
                new[] { path },
                "Assets",
                tags,
                tagMap,
                assets,
                null,
                isOpenFolderEnabled: true,
                isDeleteEnabled: true,
                isCreationAllowed: true,
                canModifyAllContextPaths: false);

            assigned[0] = "red";
            tags.Clear();
            assets.Clear();

            Assert.That(request.AssignedTagsByPath[path], Is.EqualTo(new[] { "blue" }));
            Assert.That(request.AvailableTags.Count, Is.EqualTo(1));
            Assert.That(request.SelectedAssets.Count, Is.EqualTo(1));
        }

        [Test]
        public void Request_RejectsAllTargetsWhenOnePathIsReplacedUnderItsCapturedIdentity()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var firstPath = "Assets/ContextMenuIdentityA_" + suffix + ".asset";
            var secondPath = "Assets/ContextMenuIdentityB_" + suffix + ".asset";
            var replacementPath = "Assets/ContextMenuIdentityReplacement_" + suffix + ".asset";
            AssetDatabase.CreateAsset(
                ScriptableObject.CreateInstance<ContextMenuSelectionMarker>(),
                firstPath);
            AssetDatabase.CreateAsset(
                ScriptableObject.CreateInstance<ContextMenuSelectionMarker>(),
                secondPath);
            AssetDatabase.CreateAsset(
                ScriptableObject.CreateInstance<ContextMenuSelectionMarker>(),
                replacementPath);
            AssetDatabase.SaveAssets();
            try
            {
                var state = new ZepState();
                var request = Request(
                    state,
                    new[] { firstPath, secondPath },
                    ZepContextMenuTargetKind.Selection);

                Assert.That(request.AreContextTargetsCurrent(), Is.True);

                Assert.That(AssetDatabase.DeleteAsset(secondPath), Is.True);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Assert.That(
                    AssetDatabase.MoveAsset(replacementPath, secondPath),
                    Is.Empty);
                AssetDatabase.SaveAssets();

                Assert.That(
                    request.AreContextTargetsCurrent(),
                    Is.False,
                    "one replaced target must reject the entire captured operation");
            }
            finally
            {
                AssetDatabase.DeleteAsset(firstPath);
                AssetDatabase.DeleteAsset(secondPath);
                AssetDatabase.DeleteAsset(replacementPath);
            }
        }

        [Test]
        public void OpenCallback_UsesCapturedSidebarFolderAfterPaneSelectionChanges()
        {
            var folderPath = CreateTemporaryFolder();
            try
            {
                var state = new ZepState();
                var folder = new ZepFolderEntry("Target", folderPath, 0, false);
                var request = Request(
                    state,
                    new[] { folder.Path },
                    ZepContextMenuTargetKind.SidebarFolder,
                    selectedFolder: folder);
                string openedPath = null;
                var actions = new ContextMenuActions(
                    null,
                    null,
                    null,
                    state,
                    null,
                    null,
                    (_, path) => openedPath = path);
                var widget = new ContextMenuWidget(new UnityAssetsContextMenuProvider(), actions);
                var rebound = widget.WithKnownProviderBindings(
                    new[] { Item("Assets/Open") },
                    "Assets",
                    selectionIsWritable: true,
                    creationAllowed: true,
                    request);

                state.PrimaryPane.SetSelectedAssetPath(AssetPath.From("Assets/Changed.asset"));
                rebound[0].OnClick.Invoke();

                Assert.That(openedPath, Is.EqualTo(folder.Path));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        [Test]
        public void OpenCallback_ClosedPaneDoesNothing()
        {
            var folderPath = CreateTemporaryFolder();
            try
            {
                var state = new ZepState();
                var opened = state.OpenAuxiliary(
                    ZepPaneSource.RootedFolder(AssetPath.From("Assets")));
                var pane = opened.NewPane;
                var folder = new ZepFolderEntry("Target", folderPath, 0, false);
                var request = new ZepContextMenuRequest(
                    pane,
                    null,
                    new[] { folder.Path },
                    "Assets",
                    Array.Empty<ZepTagFilterOption>(),
                    new Dictionary<string, ICollection<string>>(),
                    null,
                    folder,
                    isOpenFolderEnabled: true,
                    isDeleteEnabled: true,
                    isCreationAllowed: true,
                    canModifyAllContextPaths: false,
                    targetKind: ZepContextMenuTargetKind.SidebarFolder,
                    anchor: null);
                var openCount = 0;
                var actions = new ContextMenuActions(
                    null,
                    null,
                    null,
                    state,
                    null,
                    null,
                    (_, __) => openCount++);
                var widget = new ContextMenuWidget(new UnityAssetsContextMenuProvider(), actions);
                var rebound = widget.WithKnownProviderBindings(
                    new[] { Item("Assets/Open") },
                    "Assets",
                    true,
                    true,
                    request);

                state.CloseAuxiliary(pane);
                rebound[0].OnClick.Invoke();

                Assert.That(openCount, Is.Zero);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        [Test]
        public void BackgroundRequest_DisablesSelectionOnlyKnownActions()
        {
            var state = new ZepState();
            state.PrimaryPane.SetSelectedAssetPath(AssetPath.From("Assets/Old.asset"));
            var request = Request(
                state,
                Array.Empty<string>(),
                ZepContextMenuTargetKind.Background);
            var widget = new ContextMenuWidget(
                new UnityAssetsContextMenuProvider(),
                new ContextMenuActions(null, null, null, state, null, null));

            var rebound = widget.WithKnownProviderBindings(
                new[]
                {
                    Item("Assets/Open"),
                    Item("Assets/Rename"),
                    Item("Assets/Duplicate"),
                    Item("Assets/Delete"),
                    Item("Assets/Reveal in Finder")
                },
                "Assets",
                selectionIsWritable: false,
                creationAllowed: true,
                request);

            Assert.That(rebound, Has.All.Property(nameof(ZepContextMenuItem.IsEnabled)).False);
        }

        // The shell hands a background right-click the browsed folder as its
        // context (Unity's two-column window selects that folder), so the
        // target-bound bindings must light up for it rather than stay greyed
        // as they do for a bare background request.
        [Test]
        public void BackgroundRequest_WithTheBrowsedFolderAsContext_EnablesTargetActions()
        {
            var state = new ZepState();
            var request = Request(
                state,
                new[] { "Assets/Parent" },
                ZepContextMenuTargetKind.Background);
            var widget = new ContextMenuWidget(
                new UnityAssetsContextMenuProvider(),
                new ContextMenuActions(null, null, null, state, null, null));

            var rebound = widget.WithKnownProviderBindings(
                new[]
                {
                    Item("Assets/Open"),
                    Item("Assets/Rename"),
                    Item("Assets/Delete"),
                    Item("Assets/Reveal in Finder")
                },
                "Assets/Parent",
                selectionIsWritable: true,
                creationAllowed: true,
                request);

            Assert.That(rebound, Has.All.Property(nameof(ZepContextMenuItem.IsEnabled)).True);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void DuplicateIsOfferedForASelectedFolder_ButNotForTheBrowsedFolderBehindABackgroundClick(
            bool isBackgroundClick,
            bool expectDuplicate)
        {
            var state = new ZepState();
            var request = Request(
                state,
                new[] { "Assets/Parent" },
                isBackgroundClick
                    ? ZepContextMenuTargetKind.Background
                    : ZepContextMenuTargetKind.Selection);
            var widget = new ContextMenuWidget(
                new UnityAssetsContextMenuProvider(),
                new ContextMenuActions(null, null, null, state, null, null));

            var items = widget.BuildStandardMenuItems(
                request,
                Array.Empty<ZepContextMenuItem>());

            Assert.That(
                items.Exists(item => item.NormalizedCommandPath == "Assets/Duplicate"),
                Is.EqualTo(expectDuplicate));
        }

        [Test]
        public void OwnedMenuItems_RemainAvailableWhenProviderReturnsNothing()
        {
            var state = new ZepState();
            var request = Request(
                state,
                Array.Empty<string>(),
                ZepContextMenuTargetKind.Background);
            var widget = new ContextMenuWidget(
                new UnityAssetsContextMenuProvider(),
                new ContextMenuActions(null, null, null, state, null, null));

            var items = widget.BuildStandardMenuItems(
                request,
                Array.Empty<ZepContextMenuItem>());

            Assert.That(items.Exists(item => item.Label == "Tags"), Is.True);
        }

        [Test]
        public void TryBuildContextObjects_PartialResolutionFailsClosed()
        {
            var widget = new ContextMenuWidget(
                new UnityAssetsContextMenuProvider(),
                new ContextMenuActions(null, null, null, null, null, null));

            var resolved = widget.TryBuildContextObjects(
                new[] { "Assets", "Packages" },
                out var contextObjects);

            Assert.That(resolved, Is.False);
            Assert.That(contextObjects, Is.Empty);
        }

        [Test]
        public void Provider_EmptyContextClearsPreviousUnitySelection()
        {
            var previousSelection = Selection.objects;
            var transient = ScriptableObject.CreateInstance<ContextMenuSelectionMarker>();
            try
            {
                Selection.objects = new Object[] { transient };

                new UnityAssetsContextMenuProvider().BuildAssetsMenu(Array.Empty<Object>());

                Assert.That(Selection.objects, Is.Empty);
            }
            finally
            {
                Selection.objects = previousSelection;
                Object.DestroyImmediate(transient);
            }
        }

        [Test]
        public void Widget_DisabledProviderClearsTheFallbackUnitySelection()
        {
            var state = new ZepState();
            var provider = new UnityAssetsContextMenuProvider();
            var disabledField = typeof(UnityAssetsContextMenuProvider).GetField(
                "_disabled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(disabledField, Is.Not.Null);
            disabledField.SetValue(provider, true);

            var previousSelection = Selection.objects;
            var transient = ScriptableObject.CreateInstance<ContextMenuSelectionMarker>();
            try
            {
                Selection.objects = new Object[] { transient };
                var request = Request(
                    state,
                    Array.Empty<string>(),
                    ZepContextMenuTargetKind.Background);
                var widget = new ContextMenuWidget(
                    provider,
                    new ContextMenuActions(null, null, null, state, null, null));

                var items = widget.BuildProviderItems(
                    request,
                    contextResolved: true,
                    contextObjects: new Object[] { transient });

                Assert.That(items, Is.Empty);
                Assert.That(Selection.objects, Is.Empty);
            }
            finally
            {
                Selection.objects = previousSelection;
                Object.DestroyImmediate(transient);
            }
        }

        [TestCase(1, EventModifiers.None, RuntimePlatform.OSXEditor, true)]
        [TestCase(0, EventModifiers.Control, RuntimePlatform.OSXEditor, true)]
        [TestCase(0, EventModifiers.Control, RuntimePlatform.WindowsEditor, false)]
        [TestCase(0, EventModifiers.None, RuntimePlatform.OSXEditor, false)]
        public void Input_MouseTriggerMatchesPlatformContextSemantics(
            int button,
            EventModifiers modifiers,
            RuntimePlatform platform,
            bool expected)
        {
            Assert.That(
                ZepContextMenuInput.IsMouseTrigger(button, modifiers, platform),
                Is.EqualTo(expected));
        }

        [UnityTest]
        public IEnumerator ControlClickContextPointer_PreemptsSelectionForSelectedAndUnselectedRows()
        {
            var feature = new AssetsBrowserFeature(
                new UnityZepIconRepository(),
                AssetViewMode.List,
                AssetsAreaConstants.DefaultAssetGridItemSize);
            var entries = new[]
            {
                new ZepAssetEntry("A", "Assets/A.asset", "Material"),
                new ZepAssetEntry("B", "Assets/B.asset", "Material"),
                new ZepAssetEntry("C", "Assets/C.asset", "Material")
            };
            var selectedPaths = new[]
            {
                AssetPath.From(entries[0].Path),
                AssetPath.From(entries[1].Path)
            };
            feature.SetAssets(entries);
            feature.SetSelection(selectedPaths);

            var contextPaths = new List<string>();
            var selectionChanges = 0;
            feature.ContextMenuRequested += (_, entry) => contextPaths.Add(entry?.Path);
            feature.SelectionChanged += _ => selectionChanges++;

            var window = ScriptableObject.CreateInstance<ContextMenuTestWindow>();
            try
            {
                window.position = new Rect(100f, 100f, 640f, 360f);
                window.rootVisualElement.Add(feature);
                window.Show();
                yield return null;

                var selectedRow = FindAssetElement(feature, entries[1].Path);
                SendControlClickPointer(selectedRow);
                SendControlClickMouse(selectedRow);
                Assert.That(contextPaths, Is.EqualTo(new[] { entries[1].Path }));
                Assert.That(selectionChanges, Is.Zero);

                feature.SetSelection(selectedPaths);
                var unselectedRow = FindAssetElement(feature, entries[2].Path);
                SendControlClickPointer(unselectedRow);
                SendControlClickMouse(unselectedRow);
                Assert.That(
                    contextPaths,
                    Is.EqualTo(new[] { entries[1].Path, entries[2].Path }));
                Assert.That(selectionChanges, Is.Zero);
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(window);
            }
        }

        [TestCase(KeyCode.Menu, EventModifiers.None, true)]
        [TestCase(KeyCode.F10, EventModifiers.Shift, true)]
        [TestCase(KeyCode.F10, EventModifiers.None, false)]
        public void Input_KeyboardTriggerSupportsMenuAndShiftF10(
            KeyCode keyCode,
            EventModifiers modifiers,
            bool expected)
        {
            Assert.That(
                ZepContextMenuInput.IsKeyboardTrigger(keyCode, modifiers),
                Is.EqualTo(expected));
        }

        [Test]
        public void Renderer_DisplayLabelIncludesHotkey()
        {
            var item = new ZepContextMenuItem(
                "Assets/Refresh",
                "Assets/Refresh",
                "Refresh",
                "%r",
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>());

            Assert.That(ContextMenuGenericMenuRenderer.BuildDisplayLabel(item), Is.EqualTo("Refresh %r"));
        }

        [TestCase("Gray", ZepTagColor.Gray, "⚪ Gray")]
        [TestCase("Green", ZepTagColor.Green, "🟢 Green")]
        [TestCase("Purple", ZepTagColor.Purple, "🟣 Purple")]
        [TestCase("Important", ZepTagColor.Blue, "🔵 Important")]
        [TestCase("Yellow", ZepTagColor.Yellow, "🟡 Yellow")]
        [TestCase("Red", ZepTagColor.Red, "🔴 Red")]
        [TestCase("Orange", ZepTagColor.Orange, "🟠 Orange")]
        [TestCase("Uncolored", ZepTagColor.None, "○ Uncolored")]
        public void Renderer_TagColorUsesColoredCirclePrefix(
            string label,
            ZepTagColor color,
            string expected)
        {
            var item = new ZepContextMenuItem(
                "Zep/Tags/Important",
                "Zep/Tags/Important",
                label,
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                iconTagColor: color);

            Assert.That(
                ContextMenuGenericMenuRenderer.BuildDisplayLabel(item),
                Is.EqualTo(expected));
        }

        private sealed class ContextMenuSelectionMarker : ScriptableObject
        {
        }

        private static VisualElement FindAssetElement(VisualElement root, string path)
        {
            var element = root.Query<VisualElement>().ToList().FirstOrDefault(candidate =>
            {
                var entry = AssetEntryRenderer.ResolveAssetEntryUserData(candidate.userData);
                return entry != null
                    && string.Equals(entry.Path, path, StringComparison.Ordinal);
            });
            Assert.That(element, Is.Not.Null, "expected a materialized row for " + path);
            return element;
        }

        [Test]
        public void OpeningAContextMenu_EvaluatesTheModifyGateOnce()
        {
            const string assetPath = "Assets/Item.mat";
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("item-guid", assetPath, "Item", "Material") },
                new[] { "Assets" });
            var shell = rig.BuildShell();
            var buildRequest = typeof(ZepShellView).GetMethod(
                "BuildContextMenuRequest",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(buildRequest, Is.Not.Null, "the shell still builds the menu request");
            var widget = new ContextMenuWidget(
                new UnityAssetsContextMenuProvider(),
                new ContextMenuActions(null, null, null, rig.State, null, null));

            ZepSelectionPredicates.ModifyEvaluationsForTests = 0;
            var request = (ZepContextMenuRequest)buildRequest.Invoke(
                shell,
                new object[]
                {
                    rig.State.PrimaryPane,
                    new[] { assetPath },
                    ZepContextMenuTargetKind.Selection,
                    null,
                    null,
                    null
                });
            widget.BuildStandardMenuItems(
                request,
                Array.Empty<ZepContextMenuItem>());

            Assert.That(
                ZepSelectionPredicates.ModifyEvaluationsForTests,
                Is.EqualTo(1),
                "the modify gate costs a gateway call per path: one open, one evaluation");
            Assert.That(
                request.CanModifyAllContextPaths,
                Is.True,
                "a writable asset must still report as modifiable");
        }

        private static string CreateTemporaryFolder()
        {
            var name = "ContextMenuTarget_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            return "Assets/" + name;
        }

        private static void SendControlClickPointer(VisualElement target)
        {
            using var evt = PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                button = 0,
                clickCount = 1,
                modifiers = EventModifiers.Control,
                mousePosition = target.worldBound.center
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static void SendControlClickMouse(VisualElement target)
        {
            using var evt = MouseDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                button = 0,
                clickCount = 1,
                modifiers = EventModifiers.Control,
                mousePosition = target.worldBound.center
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private sealed class ContextMenuTestWindow : EditorWindow
        {
        }
    }

    // What the sidebar's folder rename does when the name is already taken in
    // the parent. It runs through the prompt overlay rather than the inline
    // editor, and it used to drop the typed name with the prompt and leave only
    // the status line — which dedupes an identical repeat away, so a second
    // attempt at the same name said nothing at all.
    //
    // The folders are real (the request checks its captured targets against
    // the AssetDatabase GUIDs before it acts); the rename itself is the rig
    // gateway's, so nothing here moves them. Each folder is also a record of
    // the rig's index, as it is in the project's (a folder is an AssetRecord
    // typed "Folder"), because a rename identifies its item by GUID.
    public sealed class SidebarRenameConflictTests
    {
        private readonly List<string> _errors = new List<string>();
        private string _occupantName;
        private string _occupant;
        private string _renamedName;
        private string _renamed;
        private ZepUiControllerRig _rig;
        private ZepPromptOverlayWidget _prompt;
        private ContextMenuActions _actions;

        [SetUp]
        public void SetUp()
        {
            _errors.Clear();
            var suffix = Guid.NewGuid().ToString("N");
            _occupantName = "SidebarRenameOccupant_" + suffix;
            _occupant = "Assets/" + _occupantName;
            _renamedName = "SidebarRenameTarget_" + suffix;
            _renamed = "Assets/" + _renamedName;
            AssetDatabase.CreateFolder("Assets", _occupantName);
            AssetDatabase.CreateFolder("Assets", _renamedName);
            _rig = ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("occupant-folder-guid", _occupant, _occupantName, "Folder"),
                    new AssetRecord("renamed-folder-guid", _renamed, _renamedName, "Folder")
                },
                new[] { "Assets", _occupant, _renamed });
            _prompt = new ZepPromptOverlayWidget();
            _actions = new ContextMenuActions(
                _rig.Controller,
                null,
                _prompt,
                _rig.State,
                null,
                null,
                null,
                showError: message => _errors.Add(message));
        }

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
            AssetDatabase.DeleteAsset(_occupant);
            AssetDatabase.DeleteAsset(_renamed);
        }

        [Test]
        public void ASubmitOfATakenName_ReopensThePromptWithWhatWasTyped_AndSaysWhy()
        {
            BeginRename();

            Submit(_occupantName);

            var reason = "'" + _occupantName + "' already exists in Assets.";
            Assert.That(Exists(_renamed), Is.True, "the folder keeps its name");
            Assert.That(Exists(_occupant), Is.True, "and the folder already there is untouched");
            Assert.That(
                _rig.Controller.StatusMessage,
                Is.EqualTo(reason),
                "the status line carries the panel's own reason");
            Assert.That(_errors, Is.EqualTo(new[] { reason }),
                "and the toast carries it too — the status bar dedupes repeats away");
            Assert.That(_prompt.IsVisible, Is.True,
                "a refused name leaves the user in the prompt, the way the inline editor does");
            Assert.That(Field().value, Is.EqualTo(_occupantName),
                "the field re-opens with what was typed, not with the name on disk");
        }

        [Test]
        public void TwoSubmitsOfTheSameTakenName_SayItTwice()
        {
            BeginRename();

            Submit(_occupantName);
            Submit(_occupantName);

            Assert.That(_errors.Count, Is.EqualTo(2),
                "the second attempt at the same name must not be silent");
            Assert.That(_prompt.IsVisible, Is.True);
        }

        [Test]
        public void ASubmitOfAFreeName_RenamesAndClosesThePrompt()
        {
            BeginRename();

            Submit("SidebarRenameFree");

            Assert.That(Exists("Assets/SidebarRenameFree"), Is.True, _rig.Controller.StatusMessage);
            Assert.That(Exists(_renamed), Is.False);
            Assert.That(_prompt.IsVisible, Is.False);
            Assert.That(_errors, Is.Empty);
        }

        // The folder behind a background right-click has no row in its own
        // listing, so its Rename must take the prompt, not the inline editor.
        [Test]
        public void ABackgroundRequestOnTheBrowsedFolder_OpensThePrompt()
        {
            _actions.PromptRenameContextTarget(
                Request(ZepContextMenuTargetKind.Background));

            Assert.That(_prompt.IsVisible, Is.True);
            Assert.That(Field().value, Is.EqualTo(_renamedName));
        }

        private void BeginRename()
        {
            _actions.PromptRenameContextTarget(Request());
            Assume.That(_prompt.IsVisible, Is.True,
                "precondition: the sidebar rename opens the prompt on the folder");
            Assume.That(Field().value, Is.EqualTo(_renamedName), "precondition: it opens on the name on disk");
        }

        private ZepContextMenuRequest Request(
            ZepContextMenuTargetKind targetKind = ZepContextMenuTargetKind.SidebarFolder)
        {
            var folder = new ZepFolderEntry(_renamedName, _renamed, 0, false);
            return new ZepContextMenuRequest(
                _rig.State.PrimaryPane,
                _rig.Controller,
                new[] { _renamed },
                "Assets",
                Array.Empty<ZepTagFilterOption>(),
                new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase),
                null,
                folder,
                isOpenFolderEnabled: true,
                isDeleteEnabled: true,
                isCreationAllowed: true,
                canModifyAllContextPaths: true,
                targetKind: targetKind,
                anchor: null);
        }

        // The Enter/Rename leg of the prompt.
        private void Submit(string typed)
        {
            Field().SetValueWithoutNotify(typed);
            var submit = typeof(ZepPromptOverlayWidget).GetMethod(
                "Submit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(submit, Is.Not.Null, "the overlay's submit leg");
            submit.Invoke(_prompt, null);
        }

        private TextField Field()
        {
            return _prompt.Q<TextField>("zep-prompt-field");
        }

        private bool Exists(string assetPath)
        {
            return _rig.Gateway.Exists(AssetPath.From(assetPath));
        }
    }

    public sealed class ContextMenuDeleteBusyTests
    {
        private string _folderPath;
        private string _assetPath;
        private string _neighborPath;
        private string _sidebarPath;
        private ZepUiControllerRig _rig;
        private ContextMenuActions _actions;
        private bool _compiling;
        private bool _updating;
        private bool _accept;
        private int _dialogs;
        private Action _duringDialog;
        private readonly List<string> _errors = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _folderPath = "Assets/ContextDeleteBusy_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folderPath));
            _assetPath = _folderPath + "/Target.txt";
            _neighborPath = _folderPath + "/Neighbor.txt";
            _sidebarPath = _folderPath + "/Child";
            File.WriteAllText(_assetPath, "target bytes");
            File.WriteAllText(_neighborPath, "neighbor bytes");
            AssetDatabase.ImportAsset(_assetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(_neighborPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.CreateFolder(_folderPath, "Child");
            _rig = ZepUiControllerRig.Build(
                Records(), new[] { "Assets", _folderPath, _sidebarPath });
            _rig.Gateway.CanWriteOverride = true;
            // The mounted asset view normally provides the visible rows for selection.
            _rig.State.PrimaryPane.VisibleAssetsProvider = () => _rig.State.PrimaryPane.ProjectedAssets;
            _rig.Controller.BeginPanelUse();
            _rig.Scheduler.RunAll();
            var navigationAccepted = _rig.State.PrimaryPane.TrySetFolderSelection(_folderPath);
            _rig.State.PrimaryPane.SetSelectedAssetPath(AssetPath.From(_assetPath));
            _compiling = false;
            _updating = false;
            _accept = true;
            _dialogs = 0;
            _duringDialog = null;
            _errors.Clear();
            _actions = Actions(_rig.Controller, _rig.State);
            Assert.That(_rig.State.PrimaryPane.GetSelectedAssetEntries().Select(entry => entry.Path),
                Is.EqualTo(new[] { _assetPath }),
                $"navigationAccepted={navigationAccepted}; current={_rig.State.PrimaryPane.CurrentFolderPath}; "
                + $"selected=[{string.Join(", ", _rig.State.PrimaryPane.SelectedAssetPaths)}]; "
                + $"projected=[{string.Join(", ", _rig.State.PrimaryPane.ProjectedAssets.Select(entry => entry.Path))}]");
        }

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            if (AssetDatabase.IsValidFolder(_folderPath)) AssetDatabase.DeleteAsset(_folderPath);
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void BusyBeforeDelete_RefusesWithoutDialogAndRepeatsFeedback(bool compiling, bool sidebar)
        {
            SetBusy(compiling);

            Delete(sidebar);
            Delete(sidebar);

            Assert.That(_dialogs, Is.Zero);
            AssertUnchanged(sidebar, hasHistory: false);
            Assert.That(_rig.Controller.StatusMessage, Is.EqualTo(BusyMessage(compiling)));
            Assert.That(_errors, Is.EqualTo(new[] { BusyMessage(compiling), BusyMessage(compiling) }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BusyDelete_PreservesAlreadyRecordedHistory(bool compiling)
        {
            Assert.That(_rig.Controller.Delete(_rig.State.PrimaryPane, AssetPath.From(_neighborPath)), Is.True);
            Assert.That(_rig.Controller.CanUndo, Is.True);
            SetBusy(compiling);

            Delete(sidebar: false);

            AssertUnchanged(sidebar: false, hasHistory: true);
            Assert.That(_dialogs, Is.Zero);
            Assert.That(_errors, Is.EqualTo(new[] { BusyMessage(compiling) }));
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void BusyAfterConfirmation_RefusesWithoutMutationOrHistory(bool compiling, bool sidebar)
        {
            _duringDialog = () => SetBusy(compiling);

            Delete(sidebar);
            Delete(sidebar);

            Assert.That(_dialogs, Is.EqualTo(1), "the second request is refused before opening another dialog");
            AssertUnchanged(sidebar, hasHistory: false);
            Assert.That(_rig.Controller.StatusMessage, Is.EqualTo(BusyMessage(compiling)));
            Assert.That(_errors, Is.EqualTo(new[] { BusyMessage(compiling), BusyMessage(compiling) }));
        }

        [Test]
        public void CancelledDelete_DoesNotReportTheBusyStateThatBeganInTheDialog()
        {
            _accept = false;
            _duringDialog = () => _compiling = true;
            var status = _rig.Controller.StatusMessage;

            Delete(sidebar: false);

            Assert.That(_dialogs, Is.EqualTo(1));
            AssertUnchanged(sidebar: false, hasHistory: false);
            Assert.That(_errors, Is.Empty);
            Assert.That(_rig.Controller.StatusMessage, Is.EqualTo(status));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BusyMenu_DisablesOnlyDeleteAndReenablesItWhenRebuiltIdle(bool compiling)
        {
            var widget = new ContextMenuWidget(new UnityAssetsContextMenuProvider(), _actions);
            var request = Request();
            SetBusy(compiling);

            var busy = BindMenu(widget, request);

            Assert.That(busy.Single(item => item.NormalizedCommandPath == "Assets/Delete").IsEnabled, Is.False);
            Assert.That(busy.Where(item => item.NormalizedCommandPath != "Assets/Delete").All(item => item.IsEnabled), Is.True);
            busy.Single(item => item.NormalizedCommandPath == "Assets/Open").OnClick();
            Assert.That(_rig.Controller.StatusMessage, Is.EqualTo("Opened Target.txt"), "Open remains executable while the editor is busy");
            Assert.That(_dialogs, Is.Zero);
            Assert.That(_errors, Is.Empty);

            _compiling = false;
            _updating = false;
            var idle = BindMenu(widget, request);
            Assert.That(idle.Single(item => item.NormalizedCommandPath == "Assets/Delete").IsEnabled, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MenuBuiltWhileIdle_StillRefusesDeleteWhenInvokedBusy(bool compiling)
        {
            var widget = new ContextMenuWidget(new UnityAssetsContextMenuProvider(), _actions);
            var delete = BindMenu(widget, Request()).Single(item => item.NormalizedCommandPath == "Assets/Delete");
            Assert.That(delete.IsEnabled, Is.True);
            SetBusy(compiling);

            delete.OnClick();

            Assert.That(_dialogs, Is.Zero);
            AssertUnchanged(sidebar: false, hasHistory: false);
            Assert.That(_errors, Is.EqualTo(new[] { BusyMessage(compiling) }));
        }

        [Test]
        public void IdleConfirmedDelete_PreservesContentsAndIdentityThroughUndoRedo()
        {
            var previousPayloadRoot = UndoPayloadStore.TestRootOverride;
            var payloadRoot = Path.Combine(ProjectPaths.GetProjectRootAbsolutePath(), "Temp", "BusyDelete_" + Guid.NewGuid().ToString("N"));
            UndoPayloadStore.TestRootOverride = payloadRoot;
            ZepController controller = null;
            try
            {
                var state = new ZepState();
                var source = new AssetDatabaseAssetRecordSource();
                var seed = new RigRecordSource(Records(), new[] { "Assets", _folderPath, _sidebarPath });
                var index = new InMemoryAssetIndex();
                var gateway = new UnityProjectAssetGateway();
                var commands = new ProjectFileCommandService(gateway);
                var scheduler = new RigScheduler();
                controller = new ZepController(
                    state, gateway, commands, source, index, new AssetSearchService(index),
                    new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }),
                    new RigSynchronizer(seed, index), scheduler, new RigShellReveal(), new RigAddressablesQuery());
                state.PrimaryPane.VisibleAssetsProvider = () => state.PrimaryPane.ProjectedAssets;
                controller.Refresh();
                controller.BeginPanelUse();
                scheduler.RunAll();
                state.PrimaryPane.TrySetFolderSelection(_folderPath);
                state.PrimaryPane.SetSelectedAssetPath(AssetPath.From(_assetPath));
                var bytes = File.ReadAllBytes(_assetPath);
                var meta = File.ReadAllBytes(_assetPath + ".meta");
                var guid = AssetDatabase.AssetPathToGUID(_assetPath);
                var actions = Actions(controller, state);

                actions.DeleteSelected(state.PrimaryPane);

                Assert.That(_dialogs, Is.EqualTo(1));
                Assert.That(File.Exists(_assetPath), Is.False);
                Assert.That(File.Exists(_assetPath + ".meta"), Is.False);
                Assert.That(controller.CanUndo, Is.True);
                Assert.That(controller.CanRedo, Is.False);
                Assert.That(state.PrimaryPane.SelectedAssetPath.Value, Is.EqualTo(_neighborPath));
                Assert.That(controller.Undo(state.PrimaryPane), Is.True, controller.StatusMessage);
                Assert.That(File.ReadAllBytes(_assetPath), Is.EqualTo(bytes));
                Assert.That(File.ReadAllBytes(_assetPath + ".meta"), Is.EqualTo(meta));
                Assert.That(AssetDatabase.AssetPathToGUID(_assetPath), Is.EqualTo(guid));
                Assert.That(controller.CanRedo, Is.True);
                Assert.That(controller.Redo(state.PrimaryPane), Is.True, controller.StatusMessage);
                Assert.That(File.Exists(_assetPath), Is.False);
                Assert.That(File.Exists(_assetPath + ".meta"), Is.False);
                Assert.That(controller.CanUndo, Is.True);
                Assert.That(controller.CanRedo, Is.False);
                Assert.That(_errors, Is.Empty);
            }
            finally
            {
                controller?.Dispose();
                UndoPayloadStore.TestRootOverride = previousPayloadRoot;
                if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, true);
            }
        }

        private AssetRecord[] Records() => new[]
        {
            new AssetRecord(AssetDatabase.AssetPathToGUID(_assetPath), _assetPath, "Target", "TextAsset"),
            new AssetRecord(AssetDatabase.AssetPathToGUID(_neighborPath), _neighborPath, "Neighbor", "TextAsset"),
            new AssetRecord(AssetDatabase.AssetPathToGUID(_sidebarPath), _sidebarPath, "Child", "Folder")
        };

        private ContextMenuActions Actions(ZepController controller, ZepState state) =>
            new ContextMenuActions(
                controller, null, null, state, null, null,
                showError: message => _errors.Add(message),
                isCompiling: () => _compiling,
                isUpdating: () => _updating,
                displayDialog: (_, _, _, _) =>
                {
                    _dialogs++;
                    _duringDialog?.Invoke();
                    return _accept;
                });

        private void SetBusy(bool compiling)
        {
            _compiling = compiling;
            _updating = !compiling;
        }

        private static string BusyMessage(bool compiling) => "Nothing was deleted. "
            + (compiling ? "Wait for the compile to finish." : "Wait for the editor to finish updating.");

        private void Delete(bool sidebar)
        {
            if (sidebar)
                _actions.DeleteSidebarFolder(_rig.State.PrimaryPane,
                    new ZepFolderEntry("Child", _sidebarPath, 0, false));
            else
                _actions.DeleteSelected(_rig.State.PrimaryPane);
        }

        private void AssertUnchanged(bool sidebar, bool hasHistory)
        {
            Assert.That(_rig.Gateway.Exists(AssetPath.From(sidebar ? _sidebarPath : _assetPath)), Is.True);
            Assert.That(File.ReadAllText(_assetPath), Is.EqualTo("target bytes"));
            Assert.That(AssetDatabase.IsValidFolder(_sidebarPath), Is.True);
            Assert.That(_rig.State.PrimaryPane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From(_assetPath) }));
            Assert.That(_rig.Gateway.StoredPaths.Count, Is.EqualTo(hasHistory ? 1 : 0));
            Assert.That(_rig.Controller.CanUndo, Is.EqualTo(hasHistory));
            Assert.That(_rig.Controller.CanRedo, Is.False);
        }

        private ZepContextMenuRequest Request() => new ZepContextMenuRequest(
            _rig.State.PrimaryPane, _rig.Controller, new[] { _assetPath }, _folderPath,
            Array.Empty<ZepTagFilterOption>(), null,
            _rig.State.PrimaryPane.GetSelectedAssetEntries(), null,
            isOpenFolderEnabled: true, isDeleteEnabled: true, isCreationAllowed: true,
            canModifyAllContextPaths: true);

        private IReadOnlyList<ZepContextMenuItem> BindMenu(
            ContextMenuWidget widget, ZepContextMenuRequest request) =>
            widget.WithKnownProviderBindings(
                new[] { MenuItem("Assets/Delete"), MenuItem("Assets/Open"), MenuItem("Assets/Rename"), MenuItem("Assets/Duplicate") },
                _folderPath, selectionIsWritable: true, creationAllowed: true, request);

        private static ZepContextMenuItem MenuItem(string path) => new ZepContextMenuItem(
            path, path, path.Substring(path.LastIndexOf('/') + 1), string.Empty,
            true, false, false, Array.Empty<ZepContextMenuItem>());
    }

}
