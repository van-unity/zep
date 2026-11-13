using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.UI.AssetsArea;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepSplitPersistenceTests
    {
        // A domain reload rebuilds every split pane from the snapshot; before this
        // the folder came back and Back was empty. The pane's history rides along
        // and is re-applied through the same restore the main view uses.
        [Test]
        public void SnapshotRoundTrip_PreservesEachSplitPanesHistory()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            pane.SyncNavigationHistory();
            pane.TrySetFolderSelection("Assets/Art/UI");
            pane.SyncNavigationHistory();
            pane.TrySetFolderSelection("Assets/Art/Icons");
            pane.SyncNavigationHistory();
            Assert.That(pane.CanNavigateBack, Is.True, "precondition: two folders behind");
            var shell = new ZepShellView(state);
            try
            {
                var captured = ZepWindow.CaptureSplitSnapshot(
                    state,
                    shell,
                    path => string.Empty);
                var restoredJson = JsonUtility.FromJson<ZepSplitSnapshot>(
                    JsonUtility.ToJson(captured));
                var serializedPane = restoredJson.AuxiliaryPanes[0];
                Assert.That(serializedPane.History.Entries.Count, Is.EqualTo(3));
                Assert.That(serializedPane.History.Index, Is.EqualTo(2));
                Assert.That(serializedPane.History.Entries[1].FolderPath, Is.EqualTo("Assets/Art/UI"));

                var restored = new ZepPaneState(
                    ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
                restored.TrySetCurrentFolderPath(AssetPath.From("Assets/Art/Icons"));
                ZepWindow.RestoreHistory(restored, serializedPane.History);

                Assert.That(restored.CanNavigateBack, Is.True);
                Assert.That(restored.CanNavigateForward, Is.False);
                restored.NavigateBack();
                Assert.That(restored.CurrentFolderPath.Value, Is.EqualTo("Assets/Art/UI"));
                Assert.That(restored.CanNavigateForward, Is.True);
                restored.NavigateBack();
                Assert.That(restored.CurrentFolderPath.Value, Is.EqualTo("Assets/Art"));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PendingSplitRestore_PreservesHistoryUnderTheGuidResolvedRoot(bool rootMoved)
        {
            var root = "Assets/PPHistoryRestore_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", root.Substring("Assets/".Length));
            AssetDatabase.CreateFolder(root, "UI");
            AssetDatabase.CreateFolder(root, "Icons");
            var savedRoot = rootMoved ? "Assets/PreviousRoot" : root;
            var state = new ZepState();
            var host = ZepControllerHost.Create(state);
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var snapshot = new ZepSplitSnapshot
                {
                    ActiveAuxiliaryIndex = 0,
                    AuxiliaryPanes = new List<ZepSplitPaneSnapshot>
                    {
                        new ZepSplitPaneSnapshot
                        {
                            SourceKind = (int)ZepPaneSourceKind.Folder,
                            FolderGuid = AssetDatabase.AssetPathToGUID(root),
                            LastKnownFolderPath = savedRoot,
                            CurrentFolderPath = savedRoot + "/Icons",
                            History = new ZepHistorySnapshot
                            {
                                Index = 2,
                                Entries = new List<ZepHistoryEntrySnapshot>
                                {
                                    new ZepHistoryEntrySnapshot { FolderPath = savedRoot },
                                    new ZepHistoryEntrySnapshot { FolderPath = savedRoot + "/UI" },
                                    new ZepHistoryEntrySnapshot { FolderPath = savedRoot + "/Icons" }
                                }
                            }
                        }
                    }
                };
                typeof(ZepWindow).GetField("_controllerHost", instance).SetValue(window, host);
                typeof(ZepWindow).GetField("_viewModel", instance).SetValue(window, state);
                typeof(ZepWindow).GetField("_splitSnapshot", instance).SetValue(window, snapshot);
                typeof(ZepWindow).GetMethod("PrepareSerializedRestore", instance).Invoke(window, null);
                var shell = new ZepShellView(state, host.Core);
                typeof(ZepWindow).GetField("_shell", instance).SetValue(window, shell);
                host.Core.BeginPanelUse();
                host.Core.Refresh();
                Assert.That(host.Core.IsPaneSourceReconciliationReady, Is.True);

                typeof(ZepWindow).GetMethod("TryRestorePendingSplit", instance).Invoke(window, null);

                var pane = state.AuxiliaryPanes.Single();
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(root + "/Icons"));
                Assert.That(pane.NavigationHistoryIndex, Is.EqualTo(2));
                pane.NavigateBack();
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(root + "/UI"));
                pane.NavigateForward();
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(root + "/Icons"));
                Assert.That(pane.NavigationHistory.Select(entry => entry.FolderPath),
                    Is.EqualTo(new[] { root, root + "/UI", root + "/Icons" }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                host.Dispose();
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void RemappedTagHistory_KeepsItsCeilingThroughReconciliationAndBackForward()
        {
            using var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Assets/Illustration", "Assets/Illustration/UI", "Assets/Illustration/Icons" },
                new[] { new TagDefinitionRecord { Id = "blue", Name = "Blue" } });
            var original = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            original.SyncNavigationHistory();
            original.TrySetFolderSelection("Assets/Art");
            original.SyncNavigationHistory();
            original.TrySetFolderSelection("Assets/Art/UI");
            original.SyncNavigationHistory();
            original.TrySetFolderSelection("Assets/Art/Icons");
            original.SyncNavigationHistory();
            var snapshot = ZepWindow.CapturePaneSnapshot(
                original, AssetViewMode.List, 136, path => "art-guid");
            Assert.That(ZepWindow.TryResolveSnapshotPane(
                snapshot, guid => "Assets/Illustration", out var source,
                out var browseRoot, out var currentFolder, out _, out _), Is.True);
            Assert.That(rig.Controller.TryReconcilePaneSource(
                source, browseRoot, currentFolder, out source,
                out browseRoot, out currentFolder), Is.True);
            var pane = new ZepPaneState(source);
            pane.TryEnterTagFolder(browseRoot, currentFolder);
            ZepWindow.RestoreHistory(pane, snapshot.History, snapshot.LastKnownFolderPath);

            Assert.That(rig.State.RestoreAuxiliaries(new[] { pane }, pane), Is.True);

            Assert.That(pane.NavigationHistoryIndex, Is.EqualTo(3));
            pane.NavigateBack();
            Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/Illustration/UI"));
            pane.NavigateBack();
            Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/Illustration"));
            pane.NavigateBack();
            Assert.That(pane.CurrentEntry, Is.EqualTo(NavigationEntry.Tag("blue")));
            pane.NavigateForward();
            pane.NavigateForward();
            pane.NavigateForward();
            Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/Illustration/Icons"));
            Assert.That(pane.TagBrowseRootPath.Value, Is.EqualTo("Assets/Illustration"));
            Assert.That(pane.NavigationHistory.Skip(1).Select(entry => entry.BrowseRootPath),
                Is.EqualTo(Enumerable.Repeat("Assets/Illustration", 3)));
        }

        [Test]
        public void HistoryRemap_PreservesOutsideEntriesSegmentBoundariesOrderAndIndex()
        {
            var pane = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            pane.TryEnterTagFolder(AssetPath.From("Assets/Illustration"), AssetPath.From("Assets/Illustration/UI"));
            var snapshot = new ZepHistorySnapshot
            {
                Index = 2,
                Entries = new List<ZepHistoryEntrySnapshot>
                {
                    new ZepHistoryEntrySnapshot { TagId = "blue" },
                    new ZepHistoryEntrySnapshot { FolderPath = "Assets/Art/UI", BrowseRootPath = "Assets/Art" },
                    new ZepHistoryEntrySnapshot { FolderPath = "Assets/Art2/UI", BrowseRootPath = "Assets/Art2" },
                    new ZepHistoryEntrySnapshot { FolderPath = "Assets/Other", BrowseRootPath = "Assets/Other" }
                }
            };

            ZepWindow.RestoreHistory(pane, snapshot, "Assets/Art");

            Assert.That(pane.NavigationHistory, Is.EqualTo(new[]
            {
                NavigationEntry.Tag("blue"),
                NavigationEntry.Folder("Assets/Illustration/UI", "Assets/Illustration"),
                NavigationEntry.Folder("Assets/Art2/UI", "Assets/Art2"),
                NavigationEntry.Folder("Assets/Other", "Assets/Other")
            }));
            Assert.That(pane.NavigationHistoryIndex, Is.EqualTo(2));
            Assert.That(snapshot.Entries[1].FolderPath, Is.EqualTo("Assets/Art/UI"),
                "restoration must not rewrite the serialized evidence in place");
        }

        [Test]
        public void SnapshotRoundTrip_PreservesFolderIdentityPresentationActiveAndWeights()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))).NewPane;
            pane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art/UI"));
            pane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art/UI"));
            var shell = new ZepShellView(state);
            shell.ApplyRestoredSplitLayout(
                new Dictionary<ZepPaneState, AssetViewMode>
                {
                    [pane] = AssetViewMode.Grid
                },
                new Dictionary<ZepPaneState, int>
                {
                    [pane] = 172
                },
                new[] { 3f, 1f });

            try
            {
                var captured = ZepWindow.CaptureSplitSnapshot(
                    state,
                    shell,
                    path => path == "Assets/Art" ? "art-guid" : string.Empty);
                var restoredJson = JsonUtility.FromJson<ZepSplitSnapshot>(
                    JsonUtility.ToJson(captured));

                Assert.That(restoredJson.Version,
                    Is.EqualTo(ZepSplitSnapshot.CurrentVersion));
                Assert.That(restoredJson.AuxiliaryPanes.Count, Is.EqualTo(1));
                Assert.That(restoredJson.ActiveAuxiliaryIndex, Is.EqualTo(0));
                Assert.That(restoredJson.PaneWeights.Sum(), Is.EqualTo(1f).Within(0.0001f));
                Assert.That(restoredJson.PaneWeights[0], Is.EqualTo(0.75f).Within(0.0001f));
                var serializedPane = restoredJson.AuxiliaryPanes[0];
                Assert.That(serializedPane.FolderGuid, Is.EqualTo("art-guid"));
                Assert.That(serializedPane.LastKnownFolderPath, Is.EqualTo("Assets/Art"));
                Assert.That(serializedPane.CurrentFolderPath, Is.EqualTo("Assets/Art/UI"));
                Assert.That(serializedPane.ViewMode, Is.EqualTo((int)AssetViewMode.Grid));
                Assert.That(serializedPane.GridItemSize, Is.EqualTo(172));

                Assert.That(
                    ZepWindow.TryResolveSnapshotPane(
                        serializedPane,
                        guid => guid == "art-guid"
                            ? "Assets/Illustration"
                            : string.Empty,
                        out var source,
                        out _,
                        out var currentFolder,
                        out var viewMode,
                        out var gridItemSize),
                    Is.True);
                Assert.That(
                    source,
                    Is.EqualTo(ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Illustration"))));
                Assert.That(
                    currentFolder,
                    Is.EqualTo(AssetPath.From("Assets/Illustration/UI")));
                Assert.That(viewMode, Is.EqualTo(AssetViewMode.Grid));
                Assert.That(gridItemSize, Is.EqualTo(172));
            }
            finally
            {
                shell.Teardown();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SnapshotRoundTrip_WhileWindowPaneIsActive_PreservesMainWindowFocus(bool splitActive)
        {
            var state = new ZepState();
            var split = state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art"))).NewPane;
            var localPane = splitActive ? split : state.PrimaryPane;
            state.ActivatePane(localPane);
            var shell = new ZepShellView(state);
            try
            {
                var windowPane = state.OpenWindowPane(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Other")));
                Assert.That(shell.LocalActivePane, Is.SameAs(localPane));

                var captured = ZepWindow.CaptureSplitSnapshot(
                    state, shell, path => string.Empty);
                var restoredJson = JsonUtility.FromJson<ZepSplitSnapshot>(
                    JsonUtility.ToJson(captured));

                Assert.That(restoredJson.ActiveAuxiliaryIndex, Is.EqualTo(splitActive ? 0 : -1),
                    "the main layout must save its local focus while a dedicated window is active");
                Assert.That(state.ActivePane, Is.SameAs(windowPane),
                    "saving the main layout must not reactivate its pane");
            }
            finally
            {
                shell.Teardown();
            }
        }

        // The folder a tag pane opened from its list rides the same GUID-keyed
        // folder fields as a rooted pane's root, so it follows a move across a
        // domain reload; when it cannot be resolved the pane comes back on its
        // list rather than being dropped.
        [Test]
        public void TagPaneWithAnOpenedFolder_SnapshotRestoresTheFolderThroughItsGuid()
        {
            var state = new ZepState();
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            Assert.That(pane.TrySetFolderSelection("Assets/Art"), Is.True);
            Assert.That(pane.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            var shell = new ZepShellView(state);

            try
            {
                var captured = ZepWindow.CaptureSplitSnapshot(
                    state,
                    shell,
                    path => path == "Assets/Art" ? "art-guid" : string.Empty);
                var serializedPane = JsonUtility.FromJson<ZepSplitSnapshot>(
                    JsonUtility.ToJson(captured)).AuxiliaryPanes[0];

                Assert.That(serializedPane.SourceKind, Is.EqualTo((int)ZepPaneSourceKind.Tag));
                Assert.That(serializedPane.TagId, Is.EqualTo("blue"));
                Assert.That(serializedPane.FolderGuid, Is.EqualTo("art-guid"));
                Assert.That(serializedPane.LastKnownFolderPath, Is.EqualTo("Assets/Art"));
                Assert.That(serializedPane.CurrentFolderPath, Is.EqualTo("Assets/Art/UI"));

                Assert.That(
                    ZepWindow.TryResolveSnapshotPane(
                        serializedPane,
                        guid => guid == "art-guid" ? "Assets/Illustration" : string.Empty,
                        out var source,
                        out var openedFolder,
                        out var currentFolder,
                        out _,
                        out _),
                    Is.True);
                Assert.That(source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
                Assert.That(openedFolder, Is.EqualTo(AssetPath.From("Assets/Illustration")));
                Assert.That(currentFolder, Is.EqualTo(AssetPath.From("Assets/Illustration/UI")));

                Assert.That(
                    ZepWindow.TryResolveSnapshotPane(
                        serializedPane,
                        guid => string.Empty,
                        out source,
                        out openedFolder,
                        out currentFolder,
                        out _,
                        out _),
                    Is.True,
                    "the tag pane itself survives even when its opened folder cannot be found");
                Assert.That(source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
                Assert.That(openedFolder, Is.EqualTo(AssetPath.From("Assets/Art")),
                    "the last known path is the fallback before liveness is checked at restore");
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void ConfiguredCapacity_SnapshotAndStableRestorePreserveEveryAuxiliary()
        {
            var state = new ZepState();
            var panes = new List<ZepPaneState>();
            for (var i = 0; i < ZepState.MaxSplitViews; i++)
            {
                ZepPaneSource source;
                if (i == 1)
                {
                    source = ZepPaneSource.FixedTag("tag-" + i);
                }
                else if (i == 2)
                {
                    source = ZepPaneSource.FixedTag("second-" + i);
                }
                else
                {
                    source = ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Root" + i));
                }

                var pane = state.OpenAuxiliary(source).NewPane;
                if (source.Kind == ZepPaneSourceKind.Folder)
                {
                    pane.TrySetCurrentFolderPath(AssetPath.From(
                        source.RootFolderPath.Value + "/Child"));
                }

                panes.Add(pane);
            }

            var shell = new ZepShellView(state);
            try
            {
                var viewModes = panes.ToDictionary(
                    pane => pane,
                    pane => panes.IndexOf(pane) % 2 == 0
                        ? AssetViewMode.Grid
                        : AssetViewMode.List);
                var gridSizes = panes.ToDictionary(
                    pane => pane,
                    pane => 120 + panes.IndexOf(pane));
                var weights = Enumerable.Range(1, panes.Count + 1)
                    .Select(value => (float)value)
                    .ToArray();
                shell.ApplyRestoredSplitLayout(viewModes, gridSizes, weights);

                var snapshot = ZepWindow.CaptureSplitSnapshot(
                    state,
                    shell,
                    path => "guid-" + path);

                Assert.That(snapshot.AuxiliaryPanes.Count,
                    Is.EqualTo(ZepState.MaxSplitViews));
                Assert.That(snapshot.ActiveAuxiliaryIndex,
                    Is.EqualTo(ZepState.MaxSplitViews - 1));
                Assert.That(snapshot.PaneWeights.Count,
                    Is.EqualTo(ZepState.MaxSplitViews + 1));
                Assert.That(snapshot.PaneWeights.Sum(),
                    Is.EqualTo(1f).Within(0.0001f));

                var restoredState = new ZepState();
                var restoredPanes = new List<ZepPaneState>();
                var restoredViewModes = new Dictionary<
                    ZepPaneState,
                    AssetViewMode>();
                var restoredGridSizes = new Dictionary<ZepPaneState, int>();
                for (var i = 0; i < snapshot.AuxiliaryPanes.Count; i++)
                {
                    Assert.That(ZepWindow.TryResolveSnapshotPane(
                        snapshot.AuxiliaryPanes[i],
                        guid => guid.StartsWith("guid-")
                            ? guid.Substring("guid-".Length)
                            : string.Empty,
                        out var restoredSource,
                        out _,
                        out var restoredCurrentFolder,
                        out var restoredViewMode,
                        out var restoredGridSize), Is.True);
                    var restoredPane = new ZepPaneState(restoredSource);
                    if (!restoredCurrentFolder.IsEmpty)
                    {
                        restoredPane.TrySetCurrentFolderPath(restoredCurrentFolder);
                    }

                    restoredPanes.Add(restoredPane);
                    restoredViewModes.Add(restoredPane, restoredViewMode);
                    restoredGridSizes.Add(restoredPane, restoredGridSize);
                }

                Assert.That(restoredState.RestoreAuxiliaries(
                    restoredPanes,
                    restoredPanes.Last()), Is.True);
                var restoredWorkspace = new ZepSplitWorkspaceView(
                    restoredState,
                    new UnityZepIconRepository());
                try
                {
                    restoredWorkspace.ApplyRestoredLayout(
                        restoredViewModes,
                        restoredGridSizes,
                        snapshot.PaneWeights);

                    Assert.That(restoredState.AuxiliaryPanes.Select(pane => pane.Source),
                        Is.EqualTo(panes.Select(pane => pane.Source)));
                    Assert.That(restoredState.ActivePane,
                        Is.SameAs(restoredPanes.Last()));
                    Assert.That(restoredWorkspace.PaneCount,
                        Is.EqualTo(ZepState.MaxSplitViews + 1));
                    Assert.That(restoredWorkspace.DividerCount,
                        Is.EqualTo(ZepState.MaxSplitViews));
                    Assert.That(restoredWorkspace.HasPendingMotion, Is.False);
                    for (var i = 0; i < restoredPanes.Count; i++)
                    {
                        Assert.That(restoredWorkspace.GetPaneView(restoredPanes[i]).ViewMode,
                            Is.EqualTo(restoredViewModes[restoredPanes[i]]));
                        Assert.That(restoredWorkspace.GetPaneView(restoredPanes[i]).GridItemSize,
                            Is.EqualTo(restoredGridSizes[restoredPanes[i]]));
                    }
                }
                finally
                {
                    restoredWorkspace.Teardown();
                }
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void SnapshotSources_ResolveTagAndPackageFallbackAndRejectCorruption()
        {
            var tag = new ZepSplitPaneSnapshot
            {
                SourceKind = (int)ZepPaneSourceKind.Tag,
                TagId = "blue",
                ViewMode = (int)AssetViewMode.Grid,
                GridItemSize = 999
            };
            // Ordinal 3 was the retired Recents source. Snapshots written before
            // its removal still carry it, and must be dropped rather than
            // resolved — Primary/Folder/Tag keep ordinals 0-2, so no other
            // persisted source shifts meaning.
            var retiredRecents = new ZepSplitPaneSnapshot
            {
                SourceKind = 3
            };
            var packageFolder = new ZepSplitPaneSnapshot
            {
                SourceKind = (int)ZepPaneSourceKind.Folder,
                LastKnownFolderPath = "Packages/com.example/Editor",
                CurrentFolderPath = "Packages/com.example/Editor/UI"
            };
            var roundTripped = JsonUtility.FromJson<ZepSplitSnapshot>(
                JsonUtility.ToJson(new ZepSplitSnapshot
                {
                    AuxiliaryPanes = new List<ZepSplitPaneSnapshot>
                    {
                        tag,
                        retiredRecents,
                        packageFolder
                    }
                }));
            tag = roundTripped.AuxiliaryPanes[0];
            retiredRecents = roundTripped.AuxiliaryPanes[1];
            packageFolder = roundTripped.AuxiliaryPanes[2];

            Assert.That(ZepWindow.TryResolveSnapshotPane(
                tag,
                null,
                out var tagSource,
                out _,
                out _,
                out var tagViewMode,
                out var tagGridSize), Is.True);
            Assert.That(tagSource, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(tagViewMode, Is.EqualTo(AssetViewMode.Grid));
            Assert.That(tagGridSize, Is.EqualTo(AssetsAreaConstants.MaxAssetGridItemSize));

            Assert.That(ZepWindow.TryResolveSnapshotPane(
                retiredRecents,
                null,
                out _,
                out _,
                out _,
                out _,
                out _),
                Is.False,
                "A snapshot naming the retired Recents source must be dropped, not restored.");

            Assert.That(ZepWindow.TryResolveSnapshotPane(
                packageFolder,
                guid => string.Empty,
                out var packageSource,
                out _,
                out var packageCurrent,
                out _,
                out _), Is.True);
            Assert.That(packageSource.RootFolderPath,
                Is.EqualTo(AssetPath.From("Packages/com.example/Editor")));
            Assert.That(packageCurrent,
                Is.EqualTo(AssetPath.From("Packages/com.example/Editor/UI")));

            Assert.That(ZepWindow.TryResolveSnapshotPane(
                new ZepSplitPaneSnapshot
                {
                    SourceKind = 999,
                    LastKnownFolderPath = "not-a-project-path"
                },
                null,
                out _,
                out _,
                out _,
                out _,
                out _), Is.False);
            Assert.That(ZepWindow.IsSupportedSplitSnapshot(
                new ZepSplitSnapshot
                {
                    Version = ZepSplitSnapshot.CurrentVersion + 1
                }), Is.False);
            Assert.That(ZepWindow.IsSupportedSplitSnapshot(null), Is.False);
        }

        [Test]
        public void PristineGate_AcceptsFreshDefaultAndRejectsLiveWindowState()
        {
            var state = new ZepState();

            Assert.That(
                ZepWindow.IsPristineForSerializedRestore(state),
                Is.True);

            state.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets"));
            state.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets"));
            Assert.That(
                ZepWindow.IsPristineForSerializedRestore(state),
                Is.True,
                "Authoritative hydration may establish the default Assets path before the window opens.");

            state.PrimaryPane.SyncNavigationHistory();
            Assert.That(
                ZepWindow.IsPristineForSerializedRestore(state),
                Is.False,
                "An ordinary live close/reopen must not reapply serialized window state.");
        }

        [UnityTest]
        public IEnumerator BatchedRestore_MountsOpenAndDoesNotDuplicateOnDetachAttach()
        {
            var state = new ZepState();
            var workspace = new ZepSplitWorkspaceView(
                state,
                new UnityZepIconRepository());
            var window = ScriptableObject.CreateInstance<PersistenceTestWindow>();
            window.position = new Rect(100f, 100f, 900f, 500f);
            window.Show();
            window.rootVisualElement.Add(workspace);

            try
            {
                yield return null;
                var pane = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
                var publications = 0;
                state.PaneChanged += change =>
                {
                    if (change.Kind == ZepPaneChangeKind.Restored)
                    {
                        publications++;
                    }
                };

                Assert.That(state.RestoreAuxiliaries(new[] { pane }, pane), Is.True);
                workspace.ApplyRestoredLayout(
                    new Dictionary<ZepPaneState, AssetViewMode>
                    {
                        [pane] = AssetViewMode.Grid
                    },
                    new Dictionary<ZepPaneState, int>
                    {
                        [pane] = 160
                    },
                    new[] { float.NaN, -5f });

                Assert.That(publications, Is.EqualTo(1));
                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(workspace.DividerCount, Is.EqualTo(1));
                Assert.That(
                    workspace.GetPaneLifecycle(pane),
                    Is.EqualTo(ZepSplitPaneLifecycle.Open));
                Assert.That(workspace.HasPendingMotion, Is.False);
                Assert.That(workspace.PaneWeights,
                    Is.EqualTo(new[] { 0.5f, 0.5f }).Within(0.0001f));
                Assert.That(workspace.GetPaneView(pane).ViewMode,
                    Is.EqualTo(AssetViewMode.Grid));
                Assert.That(workspace.GetPaneView(pane).GridItemSize, Is.EqualTo(160));
                Assert.That(pane.VisibleAssetsProvider, Is.Not.Null);

                workspace.RemoveFromHierarchy();
                yield return null;
                Assert.That(pane.VisibleAssetsProvider, Is.Null);

                window.rootVisualElement.Add(workspace);
                yield return null;
                Assert.That(workspace.PaneCount, Is.EqualTo(2));
                Assert.That(workspace.Query<ZepPaneView>()
                    .ToList()
                    .Count(view => ReferenceEquals(view.Pane, pane)), Is.EqualTo(1));
                Assert.That(pane.VisibleAssetsProvider, Is.Not.Null);
                Assert.That(workspace.HasPendingMotion, Is.False);
            }
            finally
            {
                workspace.Teardown();
                window.Close();
            }
        }

        private sealed class PersistenceTestWindow : EditorWindow
        {
        }
    }
}
