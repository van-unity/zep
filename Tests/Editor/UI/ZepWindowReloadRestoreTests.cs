using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Tests.Editor.UI
{
    // The window's own post-reload order had no headless coverage: every other
    // restore test reconstructs the order inside a controller fixture and never
    // drives ZepWindow. The first five tests here drive the window's
    // real order against a real ZepControllerHost (real direct-folder
    // source, real index, real synchronizer) on a folder that exists on disk;
    // the last one performs a REAL domain reload in batchmode, which is the only
    // thing that reproduces the defect it pins.
    public sealed class ZepWindowReloadRestoreTests
    {
        private const BindingFlags Instance =
            BindingFlags.Instance | BindingFlags.NonPublic;

        // Serialized so the fixture's own state survives the real domain reload
        // the last test performs: the framework preserves only fields marked this
        // way, and a coroutine's locals not at all. SetUp re-runs before the
        // coroutine resumes and would re-derive both, but that is the framework's
        // behaviour to change, not this fixture's to depend on.
        [SerializeField] private string _folderPath;
        [SerializeField] private string _assetPath;
        [SerializeField] private string _subFolderPath;

        // Deterministic, not a fresh GUID per run: the domain reload the last
        // test performs makes the framework re-run SetUp before resuming the
        // coroutine, and a new name there would rename the folder the assertion
        // is about (and leak the old one).
        private const string FolderName = "PPReloadRestoreFixture";
        private const string SubFolderName = "Inner";

        [SetUp]
        public void SetUp()
        {
            _folderPath = "Assets/" + FolderName;
            if (!AssetDatabase.IsValidFolder(_folderPath))
            {
                AssetDatabase.CreateFolder("Assets", FolderName);
            }

            _subFolderPath = _folderPath + "/" + SubFolderName;
            if (!AssetDatabase.IsValidFolder(_subFolderPath))
            {
                AssetDatabase.CreateFolder(_folderPath, SubFolderName);
            }

            _assetPath = _folderPath + "/Selected.txt";
            if (!File.Exists(_assetPath))
            {
                File.WriteAllText(_assetPath, "zep reload restore fixture");
                AssetDatabase.ImportAsset(
                    _assetPath,
                    ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_folderPath))
            {
                AssetDatabase.DeleteAsset(_folderPath);
            }
        }

        // The gate every restore has to pass. A domain reload hands the window a
        // brand-new ZepState, so if this ever reads as "not pristine"
        // the serialized folder is dropped silently and the pane keeps Assets.
        [Test]
        public void AFreshSessionViewModel_IsPristineForSerializedRestore()
        {
            Assert.That(
                ZepWindow.IsPristineForSerializedRestore(
                    new ZepState()),
                Is.True,
                "a domain reload rebuilds the session view model from scratch; "
                + "the restore gate must accept exactly that state");
        }

        [Test]
        public void SiblingWindowRestoredFirst_DoesNotSuppressPrimaryRestore()
        {
            var state = new ZepState();
            state.OpenWindowPane(ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art")));

            Assert.That(ZepWindow.IsPristineForSerializedRestore(state), Is.True);
            state.PrimaryPane.TrySetFolderSelection("Assets/AlreadyBrowsed");
            Assert.That(ZepWindow.IsPristineForSerializedRestore(state), Is.False,
                "A sibling must not make an already-browsed primary eligible for overwriting.");
        }

        // CreateGUI's real order: host + fresh view model, PrepareSerializedRestore,
        // then BeginPanelUse. The pane must still be on the serialized folder.
        [Test]
        public void WindowRestoreOrder_KeepsTheSerializedFolderThroughBeginPanelUse()
        {
            var viewModel = new ZepState();
            var host = ZepControllerHost.Create(viewModel);
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                SetField(window, "_controllerHost", host);
                SetField(window, "_viewModel", viewModel);
                SetField(window, "_savedFolderPath", _folderPath);

                Invoke(window, "PrepareSerializedRestore");

                Assert.That(
                    viewModel.PrimaryPane.CurrentFolderPath.Value,
                    Is.EqualTo(_folderPath),
                    "PrepareSerializedRestore must write the serialized folder into the pane");

                host.Core.BeginPanelUse();

                Assert.That(
                    viewModel.PrimaryPane.CurrentFolderPath.Value,
                    Is.EqualTo(_folderPath),
                    "the restore must survive the first projection");
                Assert.That(
                    viewModel.PrimaryPane.SelectedFolderPath.Value,
                    Is.EqualTo(_folderPath));
            }
            finally
            {
                host.Dispose();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // The restored folder also has to survive the authoritative pass that
        // follows the first paint: hydration Replaces the whole catalog, which
        // retires every direct observation, so folder existence is answered from
        // the hydrated snapshot alone from that point on.
        [Test]
        public void WindowRestoreOrder_KeepsTheSerializedFolderThroughHydration()
        {
            var viewModel = new ZepState();
            var host = ZepControllerHost.Create(viewModel);
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                SetField(window, "_controllerHost", host);
                SetField(window, "_viewModel", viewModel);
                SetField(window, "_savedFolderPath", _folderPath);

                Invoke(window, "PrepareSerializedRestore");
                host.Core.BeginPanelUse();
                host.Core.Refresh();

                Assert.That(
                    viewModel.PrimaryPane.CurrentFolderPath.Value,
                    Is.EqualTo(_folderPath),
                    "the authoritative pass must not send the restored pane back to Assets");
            }
            finally
            {
                host.Dispose();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // The real post-reload order, which the two tests above do not model:
        // the [InitializeOnLoad] prewarm builds the host BEFORE the window's
        // CreateGUI, so Unity's own post-reload callbacks — the mandatory
        // AssetPostprocessor batch, projectChanged, and the restored Selection —
        // reach a live controller while the pane is still the fresh one the
        // restore has to be written into. Any of them leaving state behind fails
        // IsPristineForSerializedRestore and the serialized folder is dropped.
        [Test]
        public void PostReloadCallbacksBeforeCreateGui_LeaveTheRestoreGateOpen()
        {
            var viewModel = new ZepState();
            var host = ZepControllerHost.Create(viewModel);
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                host.Core.HandleAssetChanges(new ProjectAssetChanges(
                    new[] { _assetPath },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    didDomainReload: true));
                host.Core.HandleProjectChanged();
                host.Core.HandleSelectionPaths(new[] { _assetPath });

                Assert.That(
                    viewModel.PrimaryPane.SelectedAssetPaths.Count,
                    Is.Zero,
                    "a selection restored before the panel opens must not survive "
                    + "into the pane the serialized restore is written into");
                Assert.That(
                    ZepWindow.IsPristineForSerializedRestore(viewModel),
                    Is.True,
                    "Unity's own post-reload callbacks must not close the restore gate");

                SetField(window, "_controllerHost", host);
                SetField(window, "_viewModel", viewModel);
                SetField(window, "_savedFolderPath", _folderPath);

                Invoke(window, "PrepareSerializedRestore");
                host.Core.BeginPanelUse();

                Assert.That(
                    viewModel.PrimaryPane.CurrentFolderPath.Value,
                    Is.EqualTo(_folderPath));
            }
            finally
            {
                host.Dispose();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // _savedFolderPath is written by exactly one subscriber, and only for a
        // Navigation update on the primary pane. If an ordinary folder open does
        // not reach it, the next reload restores a stale folder faithfully.
        [Test]
        public void OpeningAFolder_IsRecordedIntoTheSerializedFolder()
        {
            var viewModel = new ZepState();
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            var handler = (Action<ZepPaneChange>)Delegate.CreateDelegate(
                typeof(Action<ZepPaneChange>),
                window,
                typeof(ZepWindow).GetMethod("OnViewModelChanged", Instance));
            try
            {
                SetField(window, "_viewModel", viewModel);
                SetField(window, "_savedFolderPath", "Assets");
                viewModel.PaneChanged += handler;

                viewModel.PrimaryPane.TrySetFolderSelection(_folderPath);

                Assert.That(
                    GetField<string>(window, "_savedFolderPath"),
                    Is.EqualTo(_folderPath),
                    "an ordinary folder open is what a reload has to restore");
            }
            finally
            {
                viewModel.PaneChanged -= handler;
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [UnityTest]
        public IEnumerator ReopenedWindow_CapturesTheExistingSessionFolderAndHistory()
        {
            Assume.That(Application.isBatchMode || !ZepWindow.HasOpenWindow, Is.True,
                "Interactive execution must preserve existing Zep windows.");
            CloseEveryPanelWindow();
            yield return null;
            var window = EditorWindow.GetWindow<ZepWindow>();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!ZepWindow.HasOpenWindow && clock.Elapsed.TotalSeconds < 60)
                {
                    yield return null;
                }

                Assert.That(ZepWindow.HasOpenWindow, Is.True, "the first window never ran CreateGUI");
                var state = ZepControllerHost.SharedViewModel;
                Assert.That(GetField<ZepState>(window, "_viewModel"), Is.SameAs(state));
                Assert.That(ZepControllerHost.Shared.Core.AcknowledgeAssetPath(AssetPath.From(_assetPath)), Is.True);
                var pane = state.PrimaryPane;
                pane.TrySetFolderSelection("Assets");
                pane.TrySetFolderSelection(_folderPath);
                Assert.That(GetField<string>(window, "_savedFolderPath"), Is.EqualTo(_folderPath),
                    "precondition: the first window observed real navigation");
                Assert.That(pane.CanNavigateBack, Is.True);
                var expectedHistory = ZepWindow.CaptureHistorySnapshot(pane);

                window.Close();
                window = EditorWindow.GetWindow<ZepWindow>();
                clock.Restart();
                while (!ZepWindow.HasOpenWindow && clock.Elapsed.TotalSeconds < 60)
                {
                    yield return null;
                }

                Assert.That(ZepWindow.HasOpenWindow, Is.True, "the reopened window never ran CreateGUI");
                Assert.That(GetField<ZepState>(window, "_viewModel"), Is.SameAs(state),
                    "closing the native window must preserve its session state");
                Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo(_folderPath),
                    "reopening must keep the session folder without another navigation request");
                Assert.That(GetField<string>(window, "_savedFolderPath"), Is.EqualTo(_folderPath),
                    "the new window must remember the folder already displayed by its session");
                var rememberedHistory = GetField<ZepHistorySnapshot>(window, "_historySnapshot");
                Assert.That(rememberedHistory.Index, Is.EqualTo(expectedHistory.Index));
                Assert.That(rememberedHistory.Entries.Count, Is.EqualTo(expectedHistory.Entries.Count));
                for (var i = 0; i < expectedHistory.Entries.Count; i++)
                {
                    Assert.That(rememberedHistory.Entries[i].FolderPath, Is.EqualTo(expectedHistory.Entries[i].FolderPath));
                    Assert.That(rememberedHistory.Entries[i].TagId, Is.EqualTo(expectedHistory.Entries[i].TagId));
                    Assert.That(rememberedHistory.Entries[i].BrowseRootPath, Is.EqualTo(expectedHistory.Entries[i].BrowseRootPath));
                }
            }
            finally
            {
                if (window != null) window.Close();
            }
        }

        // The defect the five tests above cannot see, because only a real domain
        // reload produces it: Unity restores this window's managed fields across
        // the reload — plain private ones as well as [SerializeField] ones — so
        // the window comes back with its one-shot restore latch already set and
        // PrepareSerializedRestore returns on its first line, before the pristine
        // gate or the folder write are ever reached. Measured on 6000.3.10f1.
        [UnityTest]
        public IEnumerator BrowsedFolder_SurvivesARealDomainReload()
        {
            // See ScriptChangeReloadScenarioTests: the panel's first hydration in
            // this test verifies the snapshot on disk, so a snapshot another
            // fixture left behind cannot decide what the reload restores.
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var window = EditorWindow.GetWindow<ZepWindow>();
            yield return null;

            ZepControllerHost.SharedViewModel.PrimaryPane
                .TrySetFolderSelection(_folderPath);
            yield return null;

            Assert.That(
                GetField<string>(window, "_savedFolderPath"),
                Is.EqualTo(_folderPath),
                "the window must have recorded the browsed folder before the reload");
            Assert.That(
                ZepControllerHost.SharedViewModel.PrimaryPane.CanNavigateBack,
                Is.True,
                "precondition: browsing into the folder must have left a Back entry");
            // Unity's selection survives the reload natively; the pane's does not.
            // The window mirrors it back after the restore, so the asset stays
            // selected the way it does in Unity's own Project window.
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(_assetPath);
            yield return null;
            Assert.That(
                ZepControllerHost.SharedViewModel.PrimaryPane.SelectedAssetPaths.Count,
                Is.EqualTo(1),
                "precondition: the editor selection was mirrored into the pane before the reload");
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
            yield return null;

            // GetWindow returns the surviving instance rather than creating one,
            // and is the same call the menu item makes — no instance-id lookup
            // (obsolete on this editor) and no ambiguity with the HideAndDontSave
            // windows other fixtures build.
            var restored = EditorWindow.GetWindow<ZepWindow>();
            try
            {
                Assert.That(
                    ZepControllerHost.SharedViewModel
                        .PrimaryPane.CurrentFolderPath.Value,
                    Is.EqualTo(_folderPath),
                    "the panel must come back on the folder it was browsing");
                Assert.That(
                    GetField<string>(restored, "_savedFolderPath"),
                    Is.EqualTo(_folderPath),
                    "a dropped restore also overwrites the remembered folder with "
                    + "the fallback, so the next reload cannot recover either");
                // The twin of the restore latch: the static open-window registry
                // is emptied by the reload while the instance's _countedAsOpen
                // comes back true, so without the OnDisable clear the window never
                // re-registers and everything gated on HasOpenWindow (ping reveal,
                // the menu host lookup, the shell refocus) is dead until reopen.
                Assert.That(
                    ZepWindow.HasOpenWindow,
                    Is.True,
                    "the surviving window must re-register itself after the reload");
                // Back/Forward live in the pane state, which the reload rebuilt from
                // scratch; the window serializes the primary pane's history with the
                // folder and re-applies both, so Back still works on what was browsed
                // before the reload.
                var pane = ZepControllerHost.SharedViewModel.PrimaryPane;
                Assert.That(
                    pane.SelectedAssetPaths.Count == 1
                        && string.Equals(pane.SelectedAssetPaths[0].Value, _assetPath, StringComparison.Ordinal),
                    Is.True,
                    "the asset selected before the reload must still be selected after it");
                Assert.That(
                    pane.CanNavigateBack,
                    Is.True,
                    "the folders browsed before the reload must still be behind Back");
                pane.NavigateBack();
                Assert.That(
                    pane.CurrentFolderPath.Value,
                    Is.EqualTo("Assets"),
                    "Back must land on the folder browsed before this one");
            }
            finally
            {
                restored.Close();
            }
        }

        // A split pane is rebuilt from the window's split snapshot after a reload
        // (source, folder, presentation, weights) — and, since the user asked for
        // split navigation to survive script operations too, its Back/Forward.
        // Real reload on purpose: the snapshot is captured in OnDisable, and only
        // a reload proves that capture runs before the window is serialized.
        [UnityTest]
        public IEnumerator SplitPaneHistory_SurvivesARealDomainReload()
        {
            CloseEveryPanelWindow();
            yield return null;
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var window = EditorWindow.GetWindow<ZepWindow>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!ZepWindow.HasOpenWindow && clock.Elapsed.TotalSeconds < 60)
            {
                yield return null;
            }

            Assert.That(ZepWindow.HasOpenWindow, Is.True, "the window never ran CreateGUI");
            var core = ZepControllerHost.Shared.Core;
            while (!core.IsPaneSourceReconciliationReady && clock.Elapsed.TotalSeconds < 90)
            {
                yield return null;
            }

            Assert.That(
                core.IsPaneSourceReconciliationReady,
                Is.True,
                "precondition: a split rooted at the fixture folder needs the index to know it");

            var viewModel = ZepControllerHost.SharedViewModel;
            var opened = viewModel.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From(_folderPath)));
            Assert.That(opened.Kind, Is.EqualTo(ZepSplitOperationKind.Added));
            yield return null;
            // The same route a click takes: the pane view syncs the history after
            // its paint, so nothing here pokes the history by hand.
            opened.NewPane.TrySetFolderSelection(_subFolderPath);
            yield return null;
            Assert.That(
                opened.NewPane.CurrentFolderPath.Value,
                Is.EqualTo(_subFolderPath),
                "precondition: the split pane browsed into the sub-folder");
            Assert.That(
                opened.NewPane.CanNavigateBack,
                Is.True,
                "precondition: browsing inside the split left a Back entry");
            var splitField = typeof(ZepWindow).GetField("_splitSnapshot", Instance);
            Assert.That(splitField, Is.Not.Null, "_splitSnapshot must exist");
            Assert.That(
                ((ZepSplitSnapshot)splitField.GetValue(window)).AuxiliaryPanes.Count,
                Is.EqualTo(1),
                "precondition: the window recorded the split it will restore");

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
            yield return null;

            var restored = EditorWindow.GetWindow<ZepWindow>();
            try
            {
                // The split comes back once the new domain's index can vouch for
                // the pane's root folder, which can take a few frames.
                clock = System.Diagnostics.Stopwatch.StartNew();
                while (ZepControllerHost.SharedViewModel.AuxiliaryPanes.Count == 0
                    && clock.Elapsed.TotalSeconds < 90)
                {
                    yield return null;
                }

                var panes = ZepControllerHost.SharedViewModel.AuxiliaryPanes;
                Assert.That(panes.Count, Is.EqualTo(1), "the split pane must come back");
                var pane = panes[0];
                Assert.That(
                    pane.Source.RootFolderPath.Value,
                    Is.EqualTo(_folderPath),
                    "on its own root");
                Assert.That(
                    pane.CurrentFolderPath.Value,
                    Is.EqualTo(_subFolderPath),
                    "on the folder it was browsing");
                Assert.That(
                    pane.CanNavigateBack,
                    Is.True,
                    "with the folder browsed before the reload still behind Back");
                pane.NavigateBack();
                Assert.That(
                    pane.CurrentFolderPath.Value,
                    Is.EqualTo(_folderPath),
                    "Back must land on the folder browsed before this one");
                Assert.That(
                    pane.CanNavigateForward,
                    Is.True,
                    "and Forward must lead back to where the reload happened");
            }
            finally
            {
                restored.Close();
            }
        }

        // In a full run other fixtures leave ZepWindow instances behind
        // (hidden ones bound to private view models, or registered ones) and
        // GetWindow hands one of those back — a window that never observes the
        // shared view model and so never records the split this test restores.
        private static void CloseEveryPanelWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<ZepWindow>();
            for (var i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                if (window == null)
                {
                    continue;
                }

                if ((window.hideFlags & HideFlags.HideAndDontSave) != 0)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
                else
                {
                    window.Close();
                }
            }
        }

        // A just-created asset's ping reveal: the receiver's chain used to end,
        // after three editor frames, in core.Refresh() — an authoritative rebuild
        // of the whole index (a main-thread walk, and when that walk's save lost
        // a race with a staged save, a distrusted cache and a cold scan on the
        // next reload) to frame one asset. With the panel open and its index
        // reconciled, an exhausted chain must end without any synchronizer run
        // and must release the path. Lives here, with the real-window tests,
        // because opening a window builds the static ping receiver for the rest
        // of the domain, which the receiver fixture's own domain-load test
        // asserts has not happened yet.
        [UnityTest]
        public IEnumerator AssetPing_ReentrantFrameCallback_KeepsTheNewerPendingReveal()
        {
            using var revealState = new PingReceiverStateSnapshot();
            const string newerPath = "Assets/ZEP_ReentrantPingNotImported.mat";
            var window = EditorWindow.GetWindow<ZepWindow>();
            var core = ZepControllerHost.Shared.Core;
            Action<ZepPaneState, string> frameHandler = null;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!ZepWindow.HasOpenWindow && clock.Elapsed.TotalSeconds < 60)
                {
                    yield return null;
                }

                Assert.That(ZepWindow.HasOpenWindow, Is.True);
                Assert.That(core.AcknowledgeAssetPath(AssetPath.From(_assetPath)), Is.True);
                var receiver = typeof(ZepPingReceiver);
                var acknowledge = receiver.GetMethod(
                    "AcknowledgeAndRevealCreatedAsset", BindingFlags.NonPublic | BindingFlags.Static);
                frameHandler = (_, path) =>
                {
                    if (path == _assetPath)
                    {
                        acknowledge.Invoke(null, new object[] { newerPath, false, 0L, 3 });
                    }
                };
                core.AssetFrameRequested += frameHandler;
                var pinged = AssetDatabase.LoadMainAssetAtPath(_assetPath);
                Assert.That(pinged, Is.Not.Null);
#if UNITY_6000_3_OR_NEWER
                var getEntityId = typeof(UnityEngine.Object).GetMethod("GetEntityId");
                Assert.That(getEntityId, Is.Not.Null);
                var instanceId = getEntityId.Invoke(pinged, null);
#else
                object instanceId = pinged.GetInstanceID();
#endif
                receiver.GetMethod("HandleFrame", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new[] { instanceId, (object)true });

                Assert.That(receiver.GetField("_pendingRevealPath", BindingFlags.NonPublic | BindingFlags.Static)
                        .GetValue(null), Is.EqualTo(newerPath),
                    "the older successful reveal must not cancel the request started by its frame callback");
            }
            finally
            {
                if (frameHandler != null)
                {
                    core.AssetFrameRequested -= frameHandler;
                }

                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator CreatedAssetPing_ExhaustedChain_DoesNotRebuildTheIndex()
        {
            using var revealState = new PingReceiverStateSnapshot();
            const string path = "Assets/ZEP_PingNeverImported.mat";
            var pendingField = typeof(ZepPingReceiver).GetField(
                "_pendingRevealPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(pendingField, Is.Not.Null, "_pendingRevealPath must exist");
            var attempt = typeof(ZepPingReceiver).GetMethod(
                "RunCreatedAssetRevealAttempt",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(attempt, Is.Not.Null, "RunCreatedAssetRevealAttempt must exist");
            var window = EditorWindow.GetWindow<ZepWindow>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!ZepWindow.HasOpenWindow && clock.Elapsed.TotalSeconds < 60)
            {
                yield return null;
            }

            Assert.That(ZepWindow.HasOpenWindow, Is.True, "the window never ran CreateGUI");
            var core = ZepControllerHost.Shared.Core;
            while (!core.IsPaneSourceReconciliationReady && clock.Elapsed.TotalSeconds < 90)
            {
                yield return null;
            }

            Assert.That(
                core.IsPaneSourceReconciliationReady,
                Is.True,
                "precondition: the index must be reconciled, or the chain is entitled to wait");
            var wasEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                pendingField.SetValue(null, path);
                var generation = typeof(ZepPingReceiver).GetField(
                    "_createdAssetRevealGeneration",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(generation, Is.Not.Null, "_createdAssetRevealGeneration must exist");
                attempt.Invoke(null, new object[] { path, false, 0L, 1, generation.GetValue(null) });
                var counts = ZepPerfLog.CaptureSnapshot();
                Assert.That(
                    counts.GetCount(ZepPerfCounter.FullSynchronizerRuns),
                    Is.Zero,
                    "an exhausted reveal chain must not rebuild the index");
                Assert.That(
                    counts.GetCount(ZepPerfCounter.FolderTreeWalks),
                    Is.Zero,
                    "and must not walk the project");
                Assert.That(
                    pendingField.GetValue(null),
                    Is.Null,
                    "the chain ended, so the path is released");
            }
            finally
            {
                ZepPerfLog.IsEnabled = wasEnabled;
                window.Close();
            }
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, Instance);
            Assert.That(field, Is.Not.Null, name + " must exist");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, Instance);
            Assert.That(field, Is.Not.Null, name + " must exist");
            return (T)field.GetValue(target);
        }

        private static void Invoke(object target, string name)
        {
            var method = target.GetType().GetMethod(name, Instance);
            Assert.That(method, Is.Not.Null, name + " must exist");
            method.Invoke(target, null);
        }
    }
}
