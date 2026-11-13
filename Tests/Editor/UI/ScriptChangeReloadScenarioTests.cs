using Zep.Core.Indexing;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Zep.Core.Application;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Tests.Editor.UI
{
    // The user's literal scenario, which no fixture had performed before: a
    // script under Assets/ is created, saved the way Rider saves (temp file,
    // then rename over the original), or deleted; Unity recompiles and reloads
    // the domain; the panel must come back on the folder it was browsing with
    // no cold-scan overlay, no filesystem walk, and the index already knowing
    // the change. Every step is a REAL domain reload in batchmode. The
    // assertions read the perf counters the new domain accumulated while it
    // hydrated, so they do not depend on the test resuming before or after
    // that hydration ran.
    //
    // Only [SerializeField] fields survive the reloads; coroutine locals do
    // not, and SetUp re-runs before the coroutine resumes, so it is idempotent.
    // Cleanup is a UnityTearDown so a failed assertion cannot leave a script
    // behind for the next scenario (a second create with identical bytes gives
    // Unity nothing to recompile).
    public sealed class ScriptChangeReloadScenarioTests
    {
        private const string FixtureFolderName = "PPScriptReloadFixture";
        private const string DeepFolderName = "Deep";
        private const string ScriptFileName = "PPReloadProbe.cs";
        private const string PerfLoggingPrefKey =
            "Zep.PerfLoggingToConsole";
        private const int WaitSeconds = 90;

        private const BindingFlags Instance =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [SerializeField] private string _fixtureFolderPath;
        [SerializeField] private string _deepFolderPath;
        [SerializeField] private string _scriptPath;
        [SerializeField] private bool _perfLoggingWasOn;

        [SetUp]
        public void SetUp()
        {
            _fixtureFolderPath = "Assets/" + FixtureFolderName;
            _deepFolderPath = _fixtureFolderPath + "/" + DeepFolderName;
            _scriptPath = _deepFolderPath + "/" + ScriptFileName;

            if (!AssetDatabase.IsValidFolder(_fixtureFolderPath))
            {
                AssetDatabase.CreateFolder("Assets", FixtureFolderName);
            }

            if (!AssetDatabase.IsValidFolder(_deepFolderPath))
            {
                AssetDatabase.CreateFolder(_fixtureFolderPath, DeepFolderName);
            }

            var marker = _deepFolderPath + "/Marker.txt";
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, "keeps the browsed folder non-empty");
                AssetDatabase.ImportAsset(
                    marker,
                    ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            EditorPrefs.SetBool(PerfLoggingPrefKey, _perfLoggingWasOn);
            ZepPerfLog.IsEnabled = _perfLoggingWasOn;

            var hadScript = File.Exists(_scriptPath);
            if (hadScript)
            {
                File.Delete(_scriptPath);
                File.Delete(_scriptPath + ".meta");
            }

            // Removing the last script recompiles too; that reload has to land
            // inside this test, not under the next one's feet.
            yield return new RecompileScripts(expectScriptCompilation: hadScript);

            CloseEveryPanelWindow();
            yield return null;

            AssetDatabase.DeleteAsset(_fixtureFolderPath);
            yield return null;
        }

        // Scenario A: a new script appears in the browsed folder.
        [UnityTest]
        public IEnumerator CreateScript_ThroughARealReload_KeepsFolderAndNeedsNoColdScan()
        {
            yield return RemoveLeftoverScript();
            yield return OpenPanelOnDeepFolderWithASavedSnapshot();

            ArmPerfLogging();
            WriteScript();
            yield return new RecompileScripts();

            yield return WaitForHydrationToSettle();
            AssertPanelCameBackClean(expectScriptIndexed: true, scenario: "create");
        }

        // Scenario B: an existing script is saved the way Rider saves it — a
        // temp file written next to it, then renamed over the original — which
        // moves the parent folder's mtime exactly like a create does.
        [UnityTest]
        public IEnumerator SafeWriteEditScript_ThroughARealReload_KeepsFolderAndNeedsNoColdScan()
        {
            yield return RemoveLeftoverScript();
            WriteScript();
            yield return new RecompileScripts();
            yield return OpenPanelOnDeepFolderWithASavedSnapshot();

            ArmPerfLogging();
            var temp = _scriptPath + ".tmp";
            File.WriteAllText(temp, ScriptContent());
            File.Delete(_scriptPath);
            File.Move(temp, _scriptPath);
            yield return new RecompileScripts();

            yield return WaitForHydrationToSettle();
            AssertPanelCameBackClean(expectScriptIndexed: true, scenario: "safe-write edit");
        }

        // Scenario C: the script is deleted on disk (Unity's own delete goes
        // through the same postprocessor batch and the same recompile).
        [UnityTest]
        public IEnumerator DeleteScript_ThroughARealReload_KeepsFolderAndNeedsNoColdScan()
        {
            yield return RemoveLeftoverScript();
            WriteScript();
            yield return new RecompileScripts();
            yield return OpenPanelOnDeepFolderWithASavedSnapshot();

            ArmPerfLogging();
            File.Delete(_scriptPath);
            File.Delete(_scriptPath + ".meta");
            yield return new RecompileScripts();

            yield return WaitForHydrationToSettle();
            AssertPanelCameBackClean(expectScriptIndexed: false, scenario: "delete");
        }

        // Scenario D: a folder is created (a live change the panel applies
        // without any reload) and THEN a script is created. The reload has to
        // replay both from the journal; a folder in that journal is what the
        // replay used to refuse.
        [UnityTest]
        public IEnumerator CreateFolderThenScript_ThroughARealReload_KeepsFolderAndNeedsNoColdScan()
        {
            yield return RemoveLeftoverScript();
            yield return OpenPanelOnDeepFolderWithASavedSnapshot();

            AssetDatabase.CreateFolder(_deepFolderPath, "Sub");
            yield return null;
            yield return null;

            ArmPerfLogging();
            WriteScript();
            yield return new RecompileScripts();

            yield return WaitForHydrationToSettle();
            AssertPanelCameBackClean(expectScriptIndexed: true, scenario: "folder then script");
            Assert.That(
                ZepControllerHost.Shared.Core.TryGetIndexedPathKind(
                    _deepFolderPath + "/Sub",
                    out var isFolder) && isFolder,
                Is.True,
                "the folder created before the reload must be in the index after it");
        }

        // Scenario E: the script is created the way the panel's Unity-menu
        // fallback does it and the way the user's editor was measured doing it:
        // Unity's create lands the file under its default name, the panel moves it
        // to the typed name and re-imports it, then the commit stages the
        // post-reload reveal, records the create for Undo and acknowledges the
        // path. The reload replays a journal whose first batch imports a path
        // that no longer exists; the perf log named exactly that refusal
        // (`journal-replay refused import=.../NewMonoBehaviourScript.cs`).
        [UnityTest]
        public IEnumerator CreateScriptThroughThePanel_ThroughARealReload_KeepsFolderAndNeedsNoColdScan()
        {
            yield return RemoveLeftoverScript();
            yield return OpenPanelOnDeepFolderWithASavedSnapshot();

            ArmPerfLogging();
            var defaultPath = _deepFolderPath + "/NewMonoBehaviourScript.cs";
            File.WriteAllText(defaultPath, ScriptContent());
            AssetDatabase.ImportAsset(defaultPath, ImportAssetOptions.ForceSynchronousImport);
            var moveError = AssetDatabase.MoveAsset(defaultPath, _scriptPath);
            Assert.That(moveError, Is.Empty, "the fallback's move to the typed name must succeed");
            AssetDatabase.ImportAsset(
                _scriptPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            SessionState.SetString(ZepShellView.PendingCreateSelectionSessionKey, _scriptPath);
            var core = ZepControllerHost.Shared.Core;
            core.RecordCreatedAsset(Zep.Core.Domain.AssetPath.From(_scriptPath));
            core.AcknowledgeAssetPath(Zep.Core.Domain.AssetPath.From(_scriptPath));
            yield return new RecompileScripts();

            yield return WaitForHydrationToSettle();
            AssertPanelCameBackClean(expectScriptIndexed: true, scenario: "create through the panel (default name, then rename)");
            Assert.That(
                ZepControllerHost.Shared.Core.TryGetIndexedPathKind(defaultPath, out _),
                Is.False,
                "the default-name path the file was renamed from must not be indexed");
        }

        // A script left by an earlier failure would make this test's own create
        // a no-op (same bytes, nothing to recompile); it is removed with its own
        // reload first.
        private IEnumerator RemoveLeftoverScript()
        {
            if (!File.Exists(_scriptPath))
            {
                yield break;
            }

            File.Delete(_scriptPath);
            File.Delete(_scriptPath + ".meta");
            yield return new RecompileScripts();
        }

        // Opens the real window, browses to the deep folder, and lets the
        // first hydration finish AND commit its snapshot, so the reload under
        // test starts from a saved cache — the state a user is always in.
        private IEnumerator OpenPanelOnDeepFolderWithASavedSnapshot()
        {
            // Exactly one window, ours. In a full run other fixtures can leave a
            // ZepWindow instance behind (a hidden one bound to a private
            // view model, or a registered one), and GetWindow would hand that back
            // — a window that never observes the shared pane and so never records
            // the navigation this scenario restores.
            CloseEveryPanelWindow();
            yield return null;
            // The panel's first hydration in this test verifies the snapshot on
            // disk against a walk, as the first hydration of an editor process
            // does: a fixture earlier in the run may have left a snapshot that is
            // not this project's, and only the reload under test must be measured
            // on the journal alone. The verification sets the flag back.
            AssetIndexSynchronizer.VerifiedThisProcess = false;
            var window = EditorWindow.GetWindow<ZepWindow>();
            // CreateGUI runs on the window's first paint, not inside GetWindow, and
            // in a long batchmode run that paint can take more than one frame. The
            // window records navigations only once CreateGUI has subscribed it, so
            // navigating earlier would leave the serialized folder at its default
            // and the reload would restore "Assets" for a reason that is not the
            // product's. RegisterOpenWindow is CreateGUI's last step.
            var clock = Stopwatch.StartNew();
            while (!ZepWindow.HasOpenWindow)
            {
                Assert.That(
                    clock.Elapsed.TotalSeconds,
                    Is.LessThan(WaitSeconds),
                    "the window never ran CreateGUI");
                yield return null;
            }

            yield return WaitForHydrationToSettle();

            // The shared pane outlives windows within a domain, so a previous
            // scenario can leave it on the deep folder already; a navigation to
            // where the pane already is raises no event and the fresh window
            // would rightly record nothing. Leave first, then browse in.
            ZepControllerHost.SharedViewModel.PrimaryPane
                .TrySetFolderSelection("Assets");
            yield return null;
            ZepControllerHost.SharedViewModel.PrimaryPane
                .TrySetFolderSelection(_deepFolderPath);
            yield return null;

            var synchronizer = Synchronizer();
            synchronizer.FlushPendingSnapshotSave();
            Assert.That(
                File.Exists(PersistentAssetIndexCache.CacheFilePath),
                Is.True,
                "precondition: the snapshot must be on disk before the reload under test");
            Assert.That(
                ZepControllerHost.SharedViewModel.PrimaryPane.CurrentFolderPath.Value,
                Is.EqualTo(_deepFolderPath),
                "precondition: the panel must be browsing the deep folder before the reload");
            var savedFolderField = typeof(ZepWindow).GetField("_savedFolderPath", Instance);
            Assert.That(savedFolderField, Is.Not.Null, "_savedFolderPath must exist");
            Assert.That(
                (string)savedFolderField.GetValue(window),
                Is.EqualTo(_deepFolderPath),
                "precondition: the window must have recorded the navigation it will restore");
        }

        private void ArmPerfLogging()
        {
            _perfLoggingWasOn = EditorPrefs.GetBool(PerfLoggingPrefKey, false);
            // The Bridge seeds IsEnabled from this pref at InitializeOnLoad, i.e.
            // before the new domain's hydration runs; the counters below are
            // no-ops while logging is off.
            EditorPrefs.SetBool(PerfLoggingPrefKey, true);
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();
        }

        // Unique bytes every time: identical content gives Unity nothing to
        // recompile and RecompileScripts throws.
        private static string ScriptContent()
        {
            return "public sealed class PPReloadProbe { public const string Stamp = \""
                + Guid.NewGuid().ToString("N")
                + "\"; }";
        }

        private void WriteScript()
        {
            File.WriteAllText(_scriptPath, ScriptContent());
        }

        // The hydration the new domain runs completes (pane sources ready), and
        // the compile's type-reconciliation job that follows it has ended. Both
        // are waited on by condition with a wall-clock cap: batchmode frames are
        // sub-millisecond, so a frame count means nothing.
        private IEnumerator WaitForHydrationToSettle()
        {
            var clock = Stopwatch.StartNew();
            while (!ZepControllerHost.Shared.Core.IsPaneSourceReconciliationReady)
            {
                Assert.That(
                    clock.Elapsed.TotalSeconds,
                    Is.LessThan(WaitSeconds),
                    "hydration never completed after the reload");
                yield return null;
            }

            var synchronizer = Synchronizer();
            while (synchronizer.HasBackgroundJob)
            {
                Assert.That(
                    clock.Elapsed.TotalSeconds,
                    Is.LessThan(WaitSeconds),
                    "the type reconciliation job never settled after the reload");
                yield return null;
            }

            yield return null;
            yield return null;
        }

        private void AssertPanelCameBackClean(bool expectScriptIndexed, string scenario)
        {
            ZepPerfLog.Flush();
            var counters = ZepPerfLog.CaptureSnapshot();
            var overlays = counters.GetCount(ZepPerfCounter.ColdScanOverlays);
            var walks = counters.GetCount(ZepPerfCounter.FolderTreeWalks);
            var reasons = ColdScanReasonsFromLog();
            var pane = ZepControllerHost.SharedViewModel.PrimaryPane;
            var scriptIndexed = ZepControllerHost.Shared.Core.TryGetIndexedPathKind(
                _scriptPath,
                out _);

            // Unity's bundled NUnit has no Assert.Multiple; every assertion
            // carries the whole picture so the first failure still reports it.
            var facts = "\n  scenario=" + scenario
                + " pane=" + pane.CurrentFolderPath.Value
                + " coldScanOverlays=" + overlays
                + " folderTreeWalks=" + walks
                + " scriptIndexed=" + scriptIndexed
                + reasons;

            Assert.That(
                pane.CurrentFolderPath.Value,
                Is.EqualTo(_deepFolderPath),
                "the panel must come back on the folder it was browsing" + facts);
            Assert.That(
                overlays,
                Is.Zero,
                "the cold-scan overlay must not appear after a script reload" + facts);
            Assert.That(
                walks,
                Is.Zero,
                "a script reload must not walk the filesystem" + facts);
            Assert.That(
                scriptIndexed,
                Is.EqualTo(expectScriptIndexed),
                "the index must reflect the script change after the reload" + facts);
        }

        private static string ColdScanReasonsFromLog()
        {
            try
            {
                if (!File.Exists(ZepPerfLog.FilePath))
                {
                    return " (no perf log written)";
                }

                var reasons = string.Empty;
                using (var stream = new FileStream(
                    ZepPerfLog.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Contains("cold-scan reason="))
                        {
                            reasons += "\n  " + line;
                        }
                    }
                }

                return reasons.Length == 0 ? " (no cold-scan reason logged)" : reasons;
            }
            catch (IOException ex)
            {
                return " (perf log unreadable: " + ex.Message + ")";
            }
        }

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

        private static AssetIndexSynchronizer Synchronizer()
        {
            var field = typeof(ZepController).GetField("_synchronizer", Instance);
            Assert.That(field, Is.Not.Null, "_synchronizer must exist");
            var synchronizer = field.GetValue(ZepControllerHost.Shared.Core)
                as AssetIndexSynchronizer;
            Assert.That(
                synchronizer,
                Is.Not.Null,
                "the shared host must drive the real AssetIndexSynchronizer");
            return synchronizer;
        }
    }
}
