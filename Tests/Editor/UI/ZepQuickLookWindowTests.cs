using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.UI;
using Zep.Editor.UI.TopBar;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.Tests;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TestTools;
using UnityEngine.Rendering;

namespace Zep.Tests.Editor.UI
{
    // Pure classification/formatting rules behind the Quick Look window's
    // folder breakdown bar and duration rows. The window itself (popup
    // lifecycle, live-follow, IMGUI preview hosting) is exercised in-editor.
    public sealed class ZepQuickLookWindowTests
    {
        [TestCase(".png", "Textures")]
        [TestCase(".PSD", "Textures")]
        [TestCase(".cs", "Code")]
        [TestCase(".Shader", "Code")]
        [TestCase(".prefab", "Prefabs")]
        [TestCase(".mat", "Materials")]
        [TestCase(".wav", "Audio")]
        [TestCase(".fbx", "Models")]
        [TestCase(".unity", "Scenes")]
        [TestCase(".anim", "Animation")]
        [TestCase(".controller", "Animation")]
        [TestCase(".bytes", "Other")]
        [TestCase("", "Other")]
        [TestCase(null, "Other")]
        public void BucketForExtension_ClassifiesKnownAndUnknownExtensions(string extension, string expected)
        {
            Assert.That(ZepQuickLookWindow.BucketForExtension(extension), Is.EqualTo(expected));
        }

        [Test]
        public void FormatDuration_SubMinute_UsesMinutesSecondsMillis()
        {
            Assert.That(ZepQuickLookWindow.FormatDuration(3.5f), Is.EqualTo("0:03.500"));
        }

        [Test]
        public void FormatDuration_OverAMinute_CarriesMinutes()
        {
            Assert.That(ZepQuickLookWindow.FormatDuration(75f), Is.EqualTo("1:15.000"));
        }

        [Test]
        public void FormatDuration_OverAnHour_DropsMillisAndCarriesHours()
        {
            Assert.That(ZepQuickLookWindow.FormatDuration(3723f), Is.EqualTo("1:02:03"));
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void FormatDuration_InvalidInputs_FallBackToZero(float seconds)
        {
            Assert.That(ZepQuickLookWindow.FormatDuration(seconds), Is.EqualTo("0:00"));
        }

        [Test]
        public void Initialize_WithNothingPreviewable_ShowsAnEmptySelectionMessage()
        {
            var state = new ZepState();
            state.PrimaryPane.SetSelectedTagFilterId("missing");
            var shell = new ZepShellView(state);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                var initialize = typeof(ZepQuickLookWindow).GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(initialize, Is.Not.Null);

                var initialized = (bool)initialize.Invoke(window, new object[] { shell, null });

                Assert.That(initialized, Is.True);
                Assert.That(
                    window.rootVisualElement.Query<Label>().ToList()
                        .Any(label => label.text == "Select something in the panel to preview it"),
                    Is.True);
            }
            finally
            {
                Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        // Opening over an empty selection is a display state. The window used
        // to skip its live-follow subscriptions on that path and stayed blank
        // for as long as it was open — selecting an asset did nothing until the
        // user closed and reopened it.
        [Test]
        public void EmptyOpen_FollowsTheSelectionThatArrivesAfterwards()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var shell = new ZepShellView(state);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                var initialize = typeof(ZepQuickLookWindow).GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(initialize, Is.Not.Null);
                Assert.That(
                    (bool)initialize.Invoke(window, new object[] { shell, null }),
                    Is.True);
                Assume.That(
                    window.rootVisualElement.Query<Label>().ToList()
                        .Any(label => label.text == "Select something in the panel to preview it"),
                    Is.True,
                    "precondition: the window opened with nothing to preview");

                var entry = new ZepAssetEntry("Hero.png", "Assets/Art/Hero.png", "Texture2D");
                var paneView = shell.Query<ZepPaneView>()
                    .ToList()
                    .Single(view => ReferenceEquals(view.Pane, pane));
                var tags = paneView.Q<SmartFolderFeature>("zep-tags-feature");
                Assert.That(tags, Is.Not.Null);
                tags.SetAssets(new[] { entry });
                pane.SetSelectedAssetPath(AssetPath.From(entry.Path));

                var labels = window.rootVisualElement.Query<Label>().ToList();
                Assert.That(
                    labels.Any(label => label.text == entry.Name),
                    Is.True,
                    "a selection made while Quick Look is open must reach it — that is the "
                    + "subscription the empty-open path used to skip");
                Assert.That(
                    labels.Any(label => label.text == "Select something in the panel to preview it"),
                    Is.False,
                    "the empty-state message must go when there is something to preview");
            }
            finally
            {
                Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        [Test]
        public void SearchSource_FollowsResultsWithoutChangingBrowsing_AndClosesWhenResultsDisappear()
        {
            var state = new ZepState();
            var shell = new ZepShellView(state);
            var search = new SearchBarWidget();
            var first = new ZepAssetEntry("First search result", "Assets/First.png", "Texture2D");
            var second = new ZepAssetEntry("Second search result", "Assets/Second.cs", "MonoScript");
            search.SetSearchText("search");
            search.SetResults(new[] { first, second });
            var folder = state.PrimaryPane.CurrentFolderPath;
            var unitySelection = Selection.activeObject;
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                typeof(ZepQuickLookWindow).GetField("_search", flags).SetValue(window, search);
                InitializeWindow(window, shell);
                window.ShowPopup();
                var title = (Label)typeof(ZepQuickLookWindow).GetField("_title", flags).GetValue(window);
                Assert.That(title.text, Is.EqualTo(first.Name));
                Assert.That(ZepQuickLookWindow.IsOpenForSearch(search), Is.True);
                Assert.That(ZepQuickLookWindow.IsOpenForSearch(new SearchBarWidget()), Is.False);
                typeof(SearchBarWidget).GetMethod("SelectSuggestion", flags).Invoke(search, new object[] { 1 });
                Assert.That(title.text, Is.EqualTo(second.Name));
                Assert.That(state.PrimaryPane.CurrentFolderPath, Is.EqualTo(folder));
                Assert.That(state.PrimaryPane.SelectedAssetPaths, Is.Empty);
                Assert.That(Selection.activeObject, Is.SameAs(unitySelection));
                state.PrimaryPane.SetSelectedAssetPath(AssetPath.From("Assets/Unrelated.asset"));
                Assert.That(title.text, Is.EqualTo(second.Name), "Browser changes cannot replace the search preview");
                search.SetResults(System.Array.Empty<ZepAssetEntry>());
                Assert.That(window == null, Is.True, "An empty search selection dismisses its Quick Look");
                Assert.That(ZepQuickLookWindow.IsOpenForSearch(search), Is.False);
            }
            finally
            {
                if (window != null) Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        [Test]
        public void SelectionSource_UsesTheActivePaneProvider()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var activeAsset = new ZepAssetEntry(
                "Hero.png",
                "Assets/Art/Hero.png",
                "Texture2D");
            var shell = new ZepShellView(state);
            var paneView = shell.Query<ZepPaneView>()
                .ToList()
                .Single(view => ReferenceEquals(view.Pane, pane));
            var tags = paneView.Q<SmartFolderFeature>(
                "zep-tags-feature");
            Assert.That(tags, Is.Not.Null);
            tags.SetAssets(new[] { activeAsset });
            pane.SetSelectedAssetPath(AssetPath.From(activeAsset.Path));

            var items = BuildItems(shell);

            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(ItemField(items[0], "Path"), Is.EqualTo(activeAsset.Path));

            shell.Teardown();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MainPanePreview_IgnoresNativePaneActivationAndSelection(bool subAsset)
        {
            var harness = SubAssetHarness.Create();
            var preview = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                if (subAsset) harness.Collection.CommitSubAssetSelection(harness.Sub);
                else harness.Pane.SetSelectedAssetPath(AssetPath.From(harness.Parent.Path));
                InitializeWindow(preview, harness.Shell);
                var expectedName = subAsset ? harness.Sub.Name : harness.Parent.Name;
                Assert.That(Labels(preview), Does.Contain(expectedName));

                var nativePane = harness.Shell.ViewModel.OpenWindowPane(
                    ZepPaneSource.RootedFolder(AssetPath.From("Assets/Other")));
                var other = new ZepAssetEntry("Other", "Assets/Other/Other.png", "Texture2D");
                nativePane.VisibleAssetsProvider = () => new[] { other };
                nativePane.SetSelectedAssetPath(AssetPath.From(other.Path));
                Assert.That(preview != null, Is.True);
                Assert.That(Labels(preview), Does.Contain(expectedName));
                Assert.That(Labels(preview), Does.Not.Contain(other.Name));

                nativePane.SetSelectedAssetPaths(System.Array.Empty<AssetPath>());

                Assert.That(preview != null, Is.True, "clearing a sibling must not close the main preview");
                Assert.That(Labels(preview), Does.Contain(expectedName));
                Assert.That(harness.Shell.ViewModel.ActivePane, Is.SameAs(nativePane),
                    "following the local preview must not reactivate its host pane");
                Assert.That(ItemField(BuildItems(harness.Shell)[0], "Path"), Is.EqualTo(harness.Parent.Path));
            }
            finally
            {
                if (preview != null) Object.DestroyImmediate(preview);
                harness.Shell.Teardown();
            }
        }

        [Test]
        public void NativePanePreview_FollowsItsOwnSelectionWhenThePrimaryPaneChanges()
        {
            using var rig = ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("hero", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                    new AssetRecord("icon", "Assets/Art/Icon.png", "Icon", "Texture2D"),
                    new AssetRecord("other", "Assets/Other/Other.png", "Other", "Texture2D")
                },
                new[] { "Assets", "Assets/Art", "Assets/Other" });
            var pane = rig.State.OpenWindowPane(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art")));
            var host = BuildPaneWindow(rig, pane);
            var preview = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Hero.png"));
                InitializePanePreview(preview, host);
                Assert.That(Labels(preview), Does.Contain("Hero"));

                rig.State.ActivatePane(rig.State.PrimaryPane);
                rig.State.PrimaryPane.TrySetFolderSelection("Assets/Other");
                rig.State.PrimaryPane.SetSelectedAssetPath(AssetPath.From("Assets/Other/Other.png"));

                Assert.That(Labels(preview), Does.Contain("Hero"));
                Assert.That(Labels(preview), Does.Not.Contain("Other"));
                pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/Icon.png"));
                Assert.That(Labels(preview), Does.Contain("Icon"));
                Assert.That(Labels(preview), Does.Not.Contain("Hero"));

                // Closing the preview must not retire the owning native pane.
                SetField(preview, "_hostWindow", null);
                Object.DestroyImmediate(preview);
                Assert.That(host != null, Is.True);
                Assert.That(rig.State.IsLivePane(pane), Is.True);
            }
            finally
            {
                if (preview != null)
                {
                    SetField(preview, "_hostWindow", null);
                    Object.DestroyImmediate(preview);
                }
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void NativePanePreview_PreviewsCommittedSubAssetsAndOpensFoldersInsideItsRoot()
        {
            using var rig = ZepUiControllerRig.Build(
                new[] { new AssetRecord("hero", "Assets/Art/Hero.fbx", "Hero", "GameObject") },
                new[] { "Assets", "Assets/Art", "Assets/Art/UI" });
            var pane = rig.State.OpenWindowPane(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art")));
            var host = BuildPaneWindow(rig, pane);
            var preview = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                var collection = host.rootVisualElement.Q<AssetCollectionView>();
                Assert.That(collection, Is.Not.Null);
                var sub = new SubAssetDescriptor("Assets/Art/Hero.fbx", 4711,
                    default, "Hero Mesh", "Mesh", null);
                collection.CommitSubAssetSelection(sub);
                InitializePanePreview(preview, host);
                Assert.That(Labels(preview), Does.Contain("Hero Mesh"));

                pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI"));
                Assert.That(Labels(preview), Does.Contain("UI"));
                // Folder navigation clears selection and closes Quick Look;
                // Unity's Close requires a shown window with a native parent.
                preview.ShowPopup();
                SetField(preview, "_hostWindow", null);
                Invoke(preview, "OpenCurrent");

                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/Art/UI"));
                Assert.That(pane.Source.RootFolderPath.Value, Is.EqualTo("Assets/Art"));
                Assert.That(rig.State.PrimaryPane.CurrentFolderPath.Value, Is.EqualTo("Assets"));
            }
            finally
            {
                if (preview != null)
                {
                    SetField(preview, "_hostWindow", null);
                    Object.DestroyImmediate(preview);
                }
                Object.DestroyImmediate(host);
            }
        }

        // A committed sub-asset never enters the pane's path-keyed selection
        // (its commit CLEARS it); Quick Look must read the pane view's
        // descriptor instead of falling back to a folder.
        [Test]
        public void SelectionSource_CommittedSubAsset_BuildsTheSubAssetItem()
        {
            var harness = SubAssetHarness.Create();

            harness.Collection.CommitSubAssetSelection(harness.Sub);
            var items = BuildItems(harness.Shell);

            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(ItemField(items[0], "SubAsset"), Is.SameAs(harness.Sub));
            Assert.That(ItemField(items[0], "Path"), Is.EqualTo(harness.Parent.Path));

            harness.Shell.Teardown();
        }

        // A user main-selection commit displaces the committed sub-asset —
        // the pane-level mirror of the widgets' highlight rule.
        [Test]
        public void SelectionSource_MainCommitDisplacesTheCommittedSubAsset()
        {
            var harness = SubAssetHarness.Create();
            harness.Collection.CommitSubAssetSelection(harness.Sub);

            harness.Collection.CommitClickSelection(new[] { harness.Parent.Path });
            var items = BuildItems(harness.Shell);

            Assert.That(harness.Shell.ActiveSubAssetSelection, Is.Null);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(ItemField(items[0], "SubAsset"), Is.Null);
            Assert.That(ItemField(items[0], "Path"), Is.EqualTo(harness.Parent.Path));

            harness.Shell.Teardown();
        }

        // A state-side (echoed) selection displaces it too: reveal flows and
        // external Selection sync write paths straight into the pane state.
        [Test]
        public void SelectionSource_StateSelectionEchoDisplacesTheCommittedSubAsset()
        {
            var harness = SubAssetHarness.Create();
            harness.Collection.CommitSubAssetSelection(harness.Sub);

            harness.Pane.SetSelectedAssetPath(AssetPath.From(harness.Parent.Path));

            Assert.That(harness.Shell.ActiveSubAssetSelection, Is.Null);
            var items = BuildItems(harness.Shell);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(ItemField(items[0], "SubAsset"), Is.Null);

            harness.Shell.Teardown();
        }

        // Live-follow's same-set compare must key on item identity, not path: a
        // sub-asset item carries its PARENT's path, so a path compare would call
        // "sub of X" and "X selected" the same set and leave the stale sub-asset
        // preview on screen. The compare now runs before any Item is built, so
        // the identity rule is proven through the window.
        [Test]
        public void LiveFollow_ParentCommitAfterASubAsset_RebuildsInsteadOfMatching()
        {
            var harness = SubAssetHarness.Create();
            harness.Collection.CommitSubAssetSelection(harness.Sub);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                InitializeWindow(window, harness.Shell);
                Assume.That(
                    Labels(window).Any(text => text == harness.Sub.Name),
                    Is.True,
                    "precondition: the window opened on the committed sub-asset");
                ZepQuickLookWindow.BuildItemsFromSelectionCallsForTests = 0;

                harness.Collection.CommitClickSelection(new[] { harness.Parent.Path });

                Assert.That(
                    ZepQuickLookWindow.BuildItemsFromSelectionCallsForTests,
                    Is.GreaterThan(0),
                    "selecting the sub-asset's parent is a different set - the key "
                    + "compare must not short-circuit it");
                Assert.That(
                    Labels(window).Any(text => text == harness.Parent.Name),
                    Is.True,
                    "the window must follow the new selection");
            }
            finally
            {
                Object.DestroyImmediate(window);
                harness.Shell.Teardown();
            }
        }

        // §5.21: GlobalChanged fires per status-bar update and per search
        // keystroke. Answering "did the selection move?" must not materialize an
        // Item per selected asset first.
        [Test]
        public void NoOpGlobalChanges_DoNotMaterializeTheSelection()
        {
            var state = new ZepState();
            var shell = SelectAllOfANewPane(state, 8, out var entries);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                InitializeWindow(window, shell);
                ZepQuickLookWindow.BuildItemsFromSelectionCallsForTests = 0;

                for (var i = 0; i < 10; i++)
                {
                    state.SetStatusMessage("status " + i);
                }

                Assert.That(
                    ZepQuickLookWindow.BuildItemsFromSelectionCallsForTests,
                    Is.Zero,
                    "an unchanged selection must be recognised from its keys alone");
                Assert.That(
                    Labels(window).Any(text => text == entries[0].Name),
                    Is.True,
                    "and the window must still be showing that selection");
            }
            finally
            {
                Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        // §4.12: every filmstrip tile holds an icon the repository may upgrade
        // off-frame, and Unity's preview cache holds 512 entries - a tile per
        // selected asset turned a big selection into a permanent preview poll.
        [Test]
        public void Filmstrip_HugeSelection_RendersACappedWindowAndCountsTheRest()
        {
            var state = new ZepState();
            var shell = SelectAllOfANewPane(state, 2000, out _);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                InitializeWindow(window, shell);
                var strip = Filmstrip(window);

                Assert.That(
                    strip.contentContainer.childCount,
                    Is.LessThanOrEqualTo(66),
                    "at most FilmstripWindow (64) tiles plus the two clipped-end counts");
                Assert.That(
                    Tiles(strip).Count,
                    Is.EqualTo(64),
                    "the window itself must be full");
                Assert.That(
                    strip.contentContainer.Children().OfType<Label>()
                        .Select(label => label.text),
                    Does.Contain("+1936"),
                    "the clipped tail is counted, not rendered");
            }
            finally
            {
                Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        // Arrows walk the whole selection, not just the rendered window.
        [Test]
        public void Filmstrip_CurrentItemOutsideTheRenderedWindow_ReCentresOnIt()
        {
            var state = new ZepState();
            var shell = SelectAllOfANewPane(state, 200, out _);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            try
            {
                InitializeWindow(window, shell);
                var strip = Filmstrip(window);
                Assume.That(Tiles(strip).Count, Is.EqualTo(64), "precondition: capped");

                SetField(window, "_index", 150);
                Invoke(window, "UpdateFilmstripHighlight");

                Assert.That(
                    Tiles(strip).Count(tile => tile.ClassListContains(
                        ZepUiClasses.QuickLookFilmstripTileSelected)),
                    Is.EqualTo(1),
                    "the strip must re-centre so the current item has a tile to highlight");
                Assert.That(
                    strip.contentContainer.childCount,
                    Is.LessThanOrEqualTo(66),
                    "and stay capped after re-centring");
            }
            finally
            {
                Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        [UnityTest]
        public IEnumerator DeferredPreviewFailure_DoesNotReplaceANewerSelection() =>
            VerifyDeferredPreviewFailure(disableBeforeFallback: false, 1);

        [UnityTest]
        public IEnumerator DeferredPreviewFailure_DoesNotReplaceANewLifetimeOfTheSameAsset() =>
            VerifyDeferredPreviewFailure(disableBeforeFallback: false, 1, 0);

        [UnityTest]
        public IEnumerator DeferredPreviewFailure_IsIgnoredAfterDisable() =>
            VerifyDeferredPreviewFailure(disableBeforeFallback: true);

        [UnityTest]
        public IEnumerator CurrentPreviewFailure_StillShowsTheExistingFallback() =>
            VerifyDeferredPreviewFailure(disableBeforeFallback: false);

        private static IEnumerator VerifyDeferredPreviewFailure(bool disableBeforeFallback, params int[] replacements)
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required to execute a live IMGUI preview.");
            var state = new ZepState();
            var shell = SelectAllOfANewPane(state, 2, out var entries);
            var pane = shell.LocalActivePane;
            var host = ScriptableObject.CreateInstance<EditorWindow>();
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            var failingEditor = ScriptableObject.CreateInstance<QuickLookFailureTestEditor>();
            QuickLookFailureTestEditor replacementEditor = null;
            var schedulerDrained = false;
            var invoked = false;
            try
            {
                host.rootVisualElement.Add(shell);
                host.Show();
                InitializeWindow(window, shell);
                SetField(window, "_hostWindow", host);
                window.ShowPopup();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var root = (VisualElement)typeof(ZepQuickLookWindow).GetField("_root", flags).GetValue(window);
                var zone = (VisualElement)typeof(ZepQuickLookWindow).GetField("_previewZone", flags).GetValue(window);
                failingEditor.FailDraw = true;
                failingEditor.BeforeDraw = () => root.schedule.Execute(() =>
                {
                    foreach (var index in replacements)
                        pane.SetSelectedAssetPath(AssetPath.From(entries[index].Path));
                    if (disableBeforeFallback) Invoke(window, "OnDisable");
                    if (disableBeforeFallback || replacements.Length > 0)
                    {
                        zone.Clear();
                        zone.Add(new Label("Replacement preview survives"));
                        if (!disableBeforeFallback)
                        {
                            replacementEditor = ScriptableObject.CreateInstance<QuickLookFailureTestEditor>();
                            SetField(window, "_editor", replacementEditor);
                        }
                    }
                });
                var imgui = new IMGUIContainer(() =>
                {
                    if (invoked) return;
                    invoked = true;
                    Invoke(window, "DrawPreviewImgui");
                    // This follows both the replacement and failure callbacks on
                    // the same scheduler, so assertions cannot pass before them.
                    root.schedule.Execute(() => schedulerDrained = true);
                });
                zone.Clear();
                zone.Add(imgui);
                SetField(window, "_editor", failingEditor);
                SetField(window, "_previewImgui", imgui);

                var deadline = EditorApplication.timeSinceStartup + 5d;
                while (!schedulerDrained && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                Assert.That(invoked && schedulerDrained, Is.True,
                    "both the real preview exception and queued UI work must have executed");
                if (disableBeforeFallback || replacements.Length > 0)
                {
                    Assert.That(Labels(window), Does.Contain("Replacement preview survives"));
                    Assert.That(Labels(window), Does.Not.Contain("This asset's preview failed to render"));
                    if (!disableBeforeFallback)
                    {
                        Assert.That(replacementEditor != null, Is.True);
                        Assert.That(typeof(ZepQuickLookWindow).GetField("_editor", flags).GetValue(window),
                            Is.SameAs(replacementEditor));
                    }
                }
                else
                {
                    Assert.That(Labels(window), Does.Contain("This asset's preview failed to render"));
                    Assert.That(failingEditor == null, Is.True);
                }
            }
            finally
            {
                if (window != null) Object.DestroyImmediate(window);
                if (failingEditor != null) Object.DestroyImmediate(failingEditor);
                if (replacementEditor != null) Object.DestroyImmediate(replacementEditor);
                shell.Teardown();
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ReentrantInspectorFailure_DoesNotDestroyTheReplacementPreview()
        {
            var state = new ZepState();
            var shell = SelectAllOfANewPane(state, 2, out var entries);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            var asset = ScriptableObject.CreateInstance<QuickLookFailureTestAsset>();
            QuickLookFailureTestEditor replacement = null;
            try
            {
                InitializeWindow(window, shell);
                QuickLookFailureTestEditor.BeforeHasPreview = () =>
                {
                    shell.LocalActivePane.SetSelectedAssetPath(AssetPath.From(entries[1].Path));
                    replacement = ScriptableObject.CreateInstance<QuickLookFailureTestEditor>();
                    SetField(window, "_editor", replacement);
                };
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var items = (IList)typeof(ZepQuickLookWindow).GetField("_items", flags).GetValue(window);
                typeof(ZepQuickLookWindow).GetMethod("BuildAssetPreview", flags)
                    .Invoke(window, new[] { items[0], asset });

                Assert.That(replacement != null, Is.True, "A's catch must not destroy B's editor");
                Assert.That(typeof(ZepQuickLookWindow).GetField("_editor", flags).GetValue(window),
                    Is.SameAs(replacement));
                Assert.That(Labels(window), Does.Contain(entries[1].Name));
                Assert.That(Labels(window), Does.Not.Contain("This asset's inspector failed to load"));
            }
            finally
            {
                QuickLookFailureTestEditor.BeforeHasPreview = null;
                Object.DestroyImmediate(window);
                if (replacement != null) Object.DestroyImmediate(replacement);
                Object.DestroyImmediate(asset);
                shell.Teardown();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DependencyIcons_FollowStockPreviewUpgrades_AndReleaseRebuiltRows(bool previewAvailable)
        {
            const string dependencyPath =
                "Packages/com.van-unity.zep/Editor/UI/Icons/folder-icon.png";
            var fixtureName = "__QuickLookDependency_" + System.Guid.NewGuid().ToString("N");
            var fixtureRoot = "Assets/" + fixtureName;
            var state = new ZepState();
            var shell = SelectAllOfANewPane(state, 1, out _);
            var window = ScriptableObject.CreateInstance<ZepQuickLookWindow>();
            var firstPreview = new Texture2D(1, 1);
            var nextPreview = new Texture2D(1, 1);
            Material material = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var dependency = AssetDatabase.LoadAssetAtPath<Texture2D>(dependencyPath);
                Assert.That(dependency, Is.Not.Null);
                var shader = Shader.Find("UI/Default");
                Assert.That(shader, Is.Not.Null);
                AssetDatabase.CreateFolder("Assets", fixtureName);
                material = new Material(shader) { mainTexture = dependency };
                var materialPath = fixtureRoot + "/Preview.mat";
                AssetDatabase.CreateAsset(material, materialPath);
                Assert.That(AssetDatabase.GetDependencies(materialPath, false), Does.Contain(dependencyPath));

                InitializeWindow(window, shell);
                // Build the real dependency rows independently of an interactive
                // material inspector, which needs a graphics-backed editor.
                var items = (IList)typeof(ZepQuickLookWindow).GetField("_items", flags).GetValue(window);
                items[0].GetType().GetField("Path").SetValue(items[0], materialPath);
                items[0].GetType().GetField("TypeName").SetValue(items[0], "Material");
                SetField(window, "_currentAsset", material);
                Invoke(window, "RebuildInfoPane");

                var row = window.rootVisualElement.Query<VisualElement>(className: ZepUiClasses.QuickLookDepRow)
                    .ToList().Single(element => element.tooltip == dependencyPath);
                var icon = row.Q<Image>();
                var mini = icon.image;
                Assert.That(mini, Is.Not.Null);
                Assert.That(mini, Is.Not.SameAs(firstPreview));
                var store = typeof(UnityZepIconRepository).GetMethod("StoreAssetIcon", flags);
                shell.IconRepository.UpgradeOverrideForTests = path =>
                {
                    if (path != dependencyPath || !previewAvailable)
                        return UnityZepIconRepository.PreviewUpgrade.Dropped;
                    store.Invoke(shell.IconRepository, new object[] { path, firstPreview });
                    return UnityZepIconRepository.PreviewUpgrade.Applied;
                };
                shell.IconRepository.PollPendingPreviewsForTests();
                Assert.That(icon.image, Is.SameAs(previewAvailable ? firstPreview : mini),
                    "dependency icons must consume the stock upgrade or retain their mini when no preview exists");

                Invoke(window, "RebuildInfoPane");
                var replacementRow = window.rootVisualElement
                    .Query<VisualElement>(className: ZepUiClasses.QuickLookDepRow)
                    .ToList().Single(element => element.tooltip == dependencyPath);
                var replacementIcon = replacementRow.Q<Image>();
                Assert.That(replacementIcon, Is.Not.SameAs(icon));
                var detachedImage = icon.image;
                shell.IconRepository.UpgradeOverrideForTests = path =>
                {
                    if (path != dependencyPath) return UnityZepIconRepository.PreviewUpgrade.Dropped;
                    store.Invoke(shell.IconRepository, new object[] { path, nextPreview });
                    return UnityZepIconRepository.PreviewUpgrade.Applied;
                };
                shell.IconRepository.InvalidateAssetIcon(dependencyPath);
                shell.IconRepository.GetAssetIcon(dependencyPath, "Texture2D");
                shell.IconRepository.PollPendingPreviewsForTests();
                Assert.That(replacementIcon.image, Is.SameAs(nextPreview));
                Assert.That(icon.image, Is.SameAs(detachedImage), "detached dependency rows must stop receiving updates");

                Invoke(window, "OnDisable");
                var tracked = (IList)typeof(ZepQuickLookWindow)
                    .GetField("_dependencyThumbs", flags).GetValue(window);
                Assert.That(tracked, Is.Empty, "closing Quick Look must release its dependency Image references");
            }
            finally
            {
                Object.DestroyImmediate(window);
                shell.IconRepository.UpgradeOverrideForTests = null;
                shell.Teardown();
                Object.DestroyImmediate(firstPreview);
                Object.DestroyImmediate(nextPreview);
                AssetDatabase.DeleteAsset(fixtureRoot);
                if (material != null) Object.DestroyImmediate(material);
            }
        }

        private static ZepShellView SelectAllOfANewPane(
            ZepState state,
            int count,
            out List<ZepAssetEntry> entries)
        {
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            entries = new List<ZepAssetEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var name = "Item" + i.ToString("D4") + ".png";
                entries.Add(new ZepAssetEntry(name, "Assets/Art/" + name, "Texture2D"));
            }

            var shell = new ZepShellView(state);
            var paneView = shell.Query<ZepPaneView>()
                .ToList()
                .Single(view => ReferenceEquals(view.Pane, pane));
            var tags = paneView.Q<SmartFolderFeature>("zep-tags-feature");
            Assert.That(tags, Is.Not.Null);
            tags.SetAssets(entries);
            pane.SetSelectedAssetPaths(entries.Select(entry => AssetPath.From(entry.Path)));
            return shell;
        }

        private static void InitializeWindow(
            ZepQuickLookWindow window,
            ZepShellView shell)
        {
            var initialize = typeof(ZepQuickLookWindow).GetMethod(
                "Initialize",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(initialize, Is.Not.Null);
            Assert.That(
                (bool)initialize.Invoke(window, new object[] { shell, null }),
                Is.True);
        }

        private static ZepPaneWindow BuildPaneWindow(
            ZepUiControllerRig rig,
            ZepPaneState pane)
        {
            var host = ScriptableObject.CreateInstance<ZepPaneWindow>();
            SetField(host, "_state", rig.State);
            SetField(host, "_controller", rig.Controller);
            SetField(host, "_pane", pane);
            var buildView = typeof(ZepPaneWindow).GetMethod(
                "BuildView", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(buildView, Is.Not.Null);
            buildView.Invoke(host, new object[]
            {
                AssetViewMode.List, AssetsAreaConstants.DefaultAssetGridItemSize
            });
            host.rootVisualElement.Q<ZepPaneView>().RefreshFromPane();
            return host;
        }

        private static void InitializePanePreview(
            ZepQuickLookWindow preview,
            ZepPaneWindow host)
        {
            var initialize = typeof(ZepQuickLookWindow).GetMethod(
                "InitializePaneWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(initialize, Is.Not.Null);
            Assert.That((bool)initialize.Invoke(preview, new object[] { host }), Is.True);
        }

        private static ScrollView Filmstrip(ZepQuickLookWindow window)
        {
            var field = typeof(ZepQuickLookWindow).GetField(
                "_filmstrip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (ScrollView)field.GetValue(window);
        }

        private static List<VisualElement> Tiles(ScrollView filmstrip)
        {
            return filmstrip.contentContainer.Children()
                .Where(child => child.ClassListContains(
                    ZepUiClasses.QuickLookFilmstripTile))
                .ToList();
        }

        private static List<string> Labels(ZepQuickLookWindow window)
        {
            return window.rootVisualElement.Query<Label>().ToList()
                .Select(label => label.text)
                .ToList();
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string methodName)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, methodName);
            method.Invoke(target, null);
        }

        private sealed class SubAssetHarness
        {
            public ZepShellView Shell;
            public ZepPaneState Pane;
            public AssetCollectionView Collection;
            public ZepAssetEntry Parent;
            public SubAssetDescriptor Sub;

            public static SubAssetHarness Create()
            {
                var state = new ZepState();
                var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
                var parent = new ZepAssetEntry(
                    "Hero.fbx",
                    "Assets/Art/Hero.fbx",
                    "GameObject");
                var shell = new ZepShellView(state);
                var paneView = shell.Query<ZepPaneView>()
                    .ToList()
                    .Single(view => ReferenceEquals(view.Pane, pane));
                var tags = paneView.Q<SmartFolderFeature>(
                    "zep-tags-feature");
                Assert.That(tags, Is.Not.Null);
                tags.SetAssets(new[] { parent });
                var collection = tags.Q<AssetCollectionView>();
                Assert.That(collection, Is.Not.Null);

                return new SubAssetHarness
                {
                    Shell = shell,
                    Pane = pane,
                    Collection = collection,
                    Parent = parent,
                    // A default runtime id never resolves; the selection-source
                    // rules under test are independent of the live object.
                    Sub = new SubAssetDescriptor(
                        parent.Path,
                        localId: 4711,
                        runtimeId: default,
                        name: "Hero Mesh",
                        typeName: "Mesh",
                        icon: null),
                };
            }
        }

        // §4.13: the folder walk classifies each entry from the enumeration
        // result instead of re-probing it. These fixtures pin the rules the walk
        // has always had - dot-prefixed and tilde-suffixed entries skipped,
        // .meta files ignored, folders descended, file sizes summed.
        [Test]
        public void CollectFolderStats_CountsFilesFoldersAndBytes_SkippingHiddenAndMetaEntries()
        {
            var root = CreateFixtureRoot();
            try
            {
                WriteBytes(Path.Combine(root, "a.txt"), 3);
                WriteBytes(Path.Combine(root, "b.png"), 5);
                WriteBytes(Path.Combine(root, ".hidden.txt"), 7);
                WriteBytes(Path.Combine(root, "a.txt.meta"), 11);
                Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
                WriteBytes(Path.Combine(root, "sub", "c.mat"), 2);
                WriteBytes(Path.Combine(root, "sub", "deep", "d.cs"), 4);
                Directory.CreateDirectory(Path.Combine(root, "ignored~"));
                WriteBytes(Path.Combine(root, "ignored~", "e.png"), 100);

                var stats = ZepQuickLookWindow.CollectFolderStats(root);

                Assert.That(stats.FileCount, Is.EqualTo(4), "a.txt, b.png, c.mat, d.cs");
                Assert.That(stats.FolderCount, Is.EqualTo(2), "sub and sub/deep");
                Assert.That(stats.Bytes, Is.EqualTo(3 + 5 + 2 + 4));
                Assert.That(stats.Truncated, Is.False);
                Assert.That(
                    stats.Buckets,
                    Is.EquivalentTo(new Dictionary<string, int>
                    {
                        { "Other", 1 },
                        { "Textures", 1 },
                        { "Materials", 1 },
                        { "Code", 1 },
                    }));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void CollectFolderStats_DanglingSymbolicLink_CountsAsAFileAndKeepsWalkingSiblings()
        {
            var root = CreateFixtureRoot();
            try
            {
                WriteBytes(Path.Combine(root, "a.txt"), 3);
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                WriteBytes(Path.Combine(root, "sub", "b.png"), 5);
                var dangling = Path.Combine(root, "dangling.txt");
                CreateSymbolicLink(dangling, Path.Combine(root, "missing-target"));

                // Whatever the runtime reports for a link with no target, the walk
                // adds exactly that and swallows the failure - it must never
                // classify the link as a folder or abandon its siblings.
                long danglingBytes;
                try
                {
                    danglingBytes = new FileInfo(dangling).Length;
                }
                catch (IOException)
                {
                    danglingBytes = 0;
                }
                catch (System.UnauthorizedAccessException)
                {
                    danglingBytes = 0;
                }

                var stats = ZepQuickLookWindow.CollectFolderStats(root);

                Assert.That(stats.FileCount, Is.EqualTo(3), "a.txt, dangling.txt, sub/b.png");
                Assert.That(stats.FolderCount, Is.EqualTo(1), "sub");
                Assert.That(stats.Bytes, Is.EqualTo(3 + 5 + danglingBytes));
                Assert.That(stats.Buckets["Other"], Is.EqualTo(2), "a.txt and dangling.txt");
                Assert.That(stats.Buckets["Textures"], Is.EqualTo(1), "sub/b.png");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateFixtureRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ZepQuickLookWalk_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void WriteBytes(string path, int length)
        {
            File.WriteAllBytes(path, new byte[length]);
        }

        // Unity's editor scripting profile predates File.CreateSymbolicLink, so
        // the fixture shells out; the dangling-entry rule is platform-independent.
        private static void CreateSymbolicLink(string linkPath, string targetPath)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.Ignore("Symbolic-link fixtures are POSIX-only.");
            }

            var startInfo = new ProcessStartInfo(
                "/bin/ln",
                "-s \"" + targetPath + "\" \"" + linkPath + "\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(startInfo))
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero, "ln -s failed: " + error);
            }
        }

        private static IList BuildItems(ZepShellView shell)
        {
            var method = typeof(ZepQuickLookWindow).GetMethod(
                "BuildItemsFromSelection",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (IList)method.Invoke(null, new object[] { shell });
        }

        private static object ItemField(object item, string fieldName)
        {
            var field = item.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, fieldName);
            return field.GetValue(item);
        }
    }

    internal sealed class QuickLookFailureTestAsset : ScriptableObject { }

    [CustomEditor(typeof(QuickLookFailureTestAsset))]
    internal sealed class QuickLookFailureTestEditor : UnityEditor.Editor
    {
        internal static System.Action BeforeHasPreview;
        internal System.Action BeforeDraw;
        internal bool FailDraw;

        public override bool HasPreviewGUI()
        {
            if (BeforeHasPreview != null)
            {
                BeforeHasPreview();
                throw new System.InvalidOperationException("Test inspector failure after replacement");
            }
            return true;
        }

        public override void OnPreviewSettings() { }

        public override void OnInteractivePreviewGUI(Rect rect, GUIStyle background)
        {
            BeforeDraw?.Invoke();
            if (FailDraw) throw new System.InvalidOperationException("Test live preview failure");
        }
    }
}
