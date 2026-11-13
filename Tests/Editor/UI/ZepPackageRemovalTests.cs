using Zep.Core.FileOperations;
using Zep.Core.Application;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Zep.Core.Indexing;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea.MaterialThumbnails;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zep.Editor.Bridge.Theming;

namespace Zep.Editor.Tests.UI
{
    public sealed class ZepPackageRemovalTests
    {
        private string _root;
        private string _preferenceKey;
        private int _prompts;
        private string _message;
        private string _accept;
        private string _decline;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zep-removal-" + Guid.NewGuid().ToString("N"));
            _preferenceKey = "Zep.RemovalTest." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_root);
            EditorPrefs.SetString(_preferenceKey, "keep until confirmed");
            _prompts = 0;
            SetAnswer(true);
        }

        [TearDown]
        public void TearDown()
        {
            ZepPackageRemoval.DialogForTests = null;
            typeof(ZepPackageRemoval).GetField("_approvedPlan", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, null);
            EditorPrefs.DeleteKey(_preferenceKey);
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void CurrentProjectInventory_IncludesOnlyThisProjectsRecentQueriesKey()
        {
            var capture = typeof(ZepPackageRemoval).GetMethod("CaptureCurrentProject", BindingFlags.NonPublic | BindingFlags.Static);
            var plan = (ZepPackageRemoval.CleanupPlan)capture.Invoke(null, null);
            Assert.That(plan.PreferenceKeys, Does.Contain(Zep.Editor.UI.TopBar.SearchBarWidget.HistoryPreferenceKey));
            Assert.That(plan.Description, Does.Contain("except the search-use flag and recent search queries"));
        }

        [Test]
        public void CurrentProjectInventory_IncludesSearchShortcutPreference()
        {
            var capture = typeof(ZepPackageRemoval).GetMethod("CaptureCurrentProject", BindingFlags.NonPublic | BindingFlags.Static);
            var plan = (ZepPackageRemoval.CleanupPlan)capture.Invoke(null, null);
            Assert.That(plan.PreferenceKeys, Does.Contain(ZepSearchShortcut.PreferenceKey));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SearchShortcutPreference_FollowsCleanupConsent(bool dedicated, bool accept)
        {
            var key = ZepSearchShortcut.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(key);
            var previousValue = EditorPrefs.GetInt(key);
            try
            {
                EditorPrefs.SetInt(key, 12345);
                SetAnswer(accept);
                var plan = ZepPackageRemoval.Capture(
                    _root, Array.Empty<string>(), new[] { key }, Path.Combine(_root, "perf.log"));
                var requested = false;
                var stopped = false;
                if (dedicated)
                {
                    ZepPackageRemoval.RemoveWithConfirmation(plan, () => requested = true);
                }
                if (!dedicated || requested)
                {
                    ZepPackageRemoval.HandleRemovedPackages(
                        new[] { ZepPackageRemoval.PackageName }, () => plan, () => stopped = true);
                }

                Assert.That(requested, Is.EqualTo(dedicated && accept));
                Assert.That(stopped, Is.EqualTo(accept));
                Assert.That(EditorPrefs.HasKey(key), Is.EqualTo(!accept));
                if (!accept)
                {
                    Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(12345));
                }
                Assert.That(_message, Does.Contain("shared across Unity projects"));
            }
            finally
            {
                if (hadPreference) EditorPrefs.SetInt(key, previousValue);
                else EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void SessionCleanup_ErasesCurrentUndoHistoryPayloadSessionAndCursor()
        {
            const string absent = "missing-session-value";
            var stringKeys = new[]
            {
                ZepShellView.PendingCreateSelectionSessionKey,
                "Zep.FolderTreeExpanded.Assets",
                "Zep.FolderTreeExpanded.Packages",
                SessionUndoHistoryStorage.DefaultStateKey,
                UndoPayloadStore.SessionKey
            };
            var strings = new Dictionary<string, string>();
            foreach (var key in stringKeys) strings[key] = SessionState.GetString(key, absent);
            var boolKeys = new[]
            {
                "Zep.Sidebar.TagsExpanded",
                "Zep.Sidebar.FavoritesExpanded",
                "Zep.VerifiedThisProcess"
            };
            var bools = new Dictionary<string, (bool Present, bool Value)>();
            foreach (var key in boolKeys)
                bools[key] = (SessionState.GetBool(key, false) == SessionState.GetBool(key, true),
                    SessionState.GetBool(key, false));
            var cursorKey = SessionUndoHistoryStorage.DefaultStateKey + ".CursorInstanceId";
            var cursorObject = ScriptableObject.CreateInstance<ZepUnityUndoCursor>();
#if UNITY_6000_5_OR_NEWER
            var cursor = SessionState.GetEntityId(cursorKey, EntityId.None);
#else
            var cursor = SessionState.GetInt(cursorKey, 0);
#endif
            try
            {
                SessionState.SetString(SessionUndoHistoryStorage.DefaultStateKey, "history-to-remove");
                SessionState.SetString(UndoPayloadStore.SessionKey, Guid.NewGuid().ToString("N"));
#if UNITY_6000_5_OR_NEWER
                SessionState.SetEntityId(cursorKey, cursorObject.GetEntityId());
                Assert.That(SessionState.GetEntityId(cursorKey, EntityId.None), Is.EqualTo(cursorObject.GetEntityId()));
#else
                SessionState.SetInt(cursorKey, cursorObject.GetInstanceID());
                Assert.That(SessionState.GetInt(cursorKey, 0), Is.EqualTo(cursorObject.GetInstanceID()));
#endif
                typeof(ZepPackageRemoval).GetMethod("EraseSessionState", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, null);

                Assert.That(SessionState.GetString(SessionUndoHistoryStorage.DefaultStateKey, absent), Is.EqualTo(absent));
                Assert.That(SessionState.GetString(UndoPayloadStore.SessionKey, absent), Is.EqualTo(absent));
#if UNITY_6000_5_OR_NEWER
                Assert.That(SessionState.GetEntityId(cursorKey, EntityId.None), Is.EqualTo(EntityId.None));
#else
                Assert.That(SessionState.GetInt(cursorKey, 0), Is.Zero);
#endif
            }
            finally
            {
                foreach (var entry in strings)
                {
                    if (entry.Value == absent) SessionState.EraseString(entry.Key);
                    else SessionState.SetString(entry.Key, entry.Value);
                }
                foreach (var entry in bools)
                {
                    if (entry.Value.Present) SessionState.SetBool(entry.Key, entry.Value.Value);
                    else SessionState.EraseBool(entry.Key);
                }
#if UNITY_6000_5_OR_NEWER
                if (cursor.Equals(EntityId.None)) SessionState.EraseEntityId(cursorKey);
                else SessionState.SetEntityId(cursorKey, cursor);
#else
                if (cursor == 0) SessionState.EraseInt(cursorKey);
                else SessionState.SetInt(cursorKey, cursor);
#endif
                UnityEngine.Object.DestroyImmediate(cursorObject);
            }
        }

        [Test]
        public void Preview_NamesExistingPathsCountsSharedPreferencesAndCachedPayloads()
        {
            Populate();
            var plan = Capture();
            ZepPackageRemoval.RemoveWithConfirmation(plan, () => { });

            Assert.That(_message, Does.Contain("ProjectSettings/ZepTagSettings.asset"));
            Assert.That(_message, Does.Contain("Zep.TagAssignments.json"));
            Assert.That(_message, Does.Contain("Assets/RelocatedTheme.asset.meta"));
            Assert.That(_message, Does.Contain("Library/Zep/ — all contents (3 files)"));
            Assert.That(_message, Does.Contain("shared across Unity projects"));
            Assert.That(_message, Does.Contain(_preferenceKey));
            Assert.That(_message, Does.Contain("Normal project assets remain"));
            Assert.That(_accept, Is.EqualTo("Remove and delete all data"));
            Assert.That(_decline, Is.EqualTo("Cancel"));
        }

        [Test]
        public void Preview_NamesAllPackageDirectoryScopesEvenWhenAbsent()
        {
            var plan = Capture();
            ZepPackageRemoval.RemoveWithConfirmation(plan, () => { });

            var scopes = new[]
            {
                "Library/com.van-unity.zep",
                "Library/Zep"
            };
            Assert.That(plan.Directories, Is.EquivalentTo(Array.ConvertAll(scopes, path => Path.Combine(_root, path))));
            foreach (var path in scopes)
            {
                Assert.That(_message, Does.Contain(path + "/ — all contents if created before cleanup (currently absent)"));
                Assert.That(Directory.Exists(Path.Combine(_root, path)), Is.False);
            }
        }

        [TestCase("Library/com.van-unity.zep", false)]
        [TestCase("Library/Zep", false)]
        [TestCase("Library/Zep/UndoPayloads", false)]
        [TestCase("Library/com.van-unity.zep", true)]
        [TestCase("Library/Zep", true)]
        [TestCase("Library/Zep/UndoPayloads", true)]
        public void PackageDirectoryCreatedWhileStoppingOwners_IsDeleted(string scope, bool dedicated)
        {
            Write("Library/OtherPackage/keep.bin");
            if (dedicated)
            {
                ZepPackageRemoval.RemoveWithConfirmation(Capture(), () => { });
            }
            var stopped = false;

            ZepPackageRemoval.HandleRemovedPackages(
                new[] { ZepPackageRemoval.PackageName }, Capture, () =>
                {
                    stopped = true;
                    Write(scope + "/late.bin");
                    Write("ProjectSettings/ZepSettings.asset");
                    Write("Assets/RelocatedTheme.asset");
                });

            Assert.That(stopped, Is.True);
            Assert.That(_prompts, Is.EqualTo(1));
            var ownedScope = scope == "Library/Zep/UndoPayloads"
                ? "Library/Zep"
                : scope;
            Assert.That(_message, Does.Contain(ownedScope + "/ — all contents if created before cleanup (currently absent)"));
            Assert.That(Directory.Exists(Path.Combine(_root, scope)), Is.False);
            foreach (var path in new[]
            {
                "Library/OtherPackage/keep.bin",
                "ProjectSettings/ZepSettings.asset",
                "Assets/RelocatedTheme.asset"
            })
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, path)), Is.EqualTo("fixture"));
            }
        }

        [Test]
        public void DedicatedCancel_NeverRequestsRemovalOrDeletesData()
        {
            Populate();
            SetAnswer(false);
            var requested = false;

            Assert.That(ZepPackageRemoval.RemoveWithConfirmation(Capture(), () => requested = true), Is.False);

            Assert.That(requested, Is.False);
            AssertKept();
        }

        [Test]
        public void DedicatedConfirmation_DeletesOnlyWhenUnityUnregistersWithoutASecondPrompt()
        {
            Populate();
            var requested = false;
            ZepPackageRemoval.RemoveWithConfirmation(Capture(), () => requested = true);
            Assert.That(requested, Is.True);
            AssertKept();

            HandleRemoval();

            Assert.That(_prompts, Is.EqualTo(1));
            AssertDeletedAndUnrelatedDataKept();
        }

        [Test]
        public void StandardKeepData_DoesNotStopOwnersOrDeleteAnything()
        {
            Populate();
            SetAnswer(false);
            var stopped = false;
            ZepPackageRemoval.HandleRemovedPackages(
                new[] { ZepPackageRemoval.PackageName }, Capture, () => stopped = true);

            Assert.That(stopped, Is.False);
            AssertKept();
            Assert.That(_decline, Is.EqualTo("Keep data"));
            Assert.That(_message, Does.Contain("Keeping data does not cancel package removal"));
        }

        [Test]
        public void StandardDelete_ClearsAllInventoriedDataAndPreservesUnrelatedFilesAndPreferences()
        {
            Populate();
            HandleRemoval();
            Assert.That(_prompts, Is.EqualTo(1));
            Assert.That(_accept, Is.EqualTo("Delete all package data"));
            AssertDeletedAndUnrelatedDataKept();
        }

        [Test]
        public void UpdatesAndOtherRemovals_NeverCapturePromptOrClean()
        {
            foreach (var removedNames in new[] { Array.Empty<string>(), new[] { "com.example.other-package" } })
            {
                ZepPackageRemoval.HandleRemovedPackages(removedNames,
                    () => throw new AssertionException("Unexpected inventory capture"),
                    () => throw new AssertionException("Unexpected cleanup"));
            }
            Assert.That(_prompts, Is.Zero);
        }

        [Test]
        public void FailedRemovalRequest_PreservesDataAndDoesNotAuthorizeALaterRemoval()
        {
            Populate();
            Assert.Throws<InvalidOperationException>(() =>
                ZepPackageRemoval.RemoveWithConfirmation(Capture(),
                    () => throw new InvalidOperationException("UPM unavailable")));
            AssertKept();

            SetAnswer(false);
            HandleRemoval();
            Assert.That(_prompts, Is.EqualTo(2));
            AssertKept();
        }

        [Test]
        public void NewPathsSinceDedicatedPreview_RequireFreshCleanupConsent()
        {
            Populate();
            ZepPackageRemoval.RemoveWithConfirmation(Capture(), () => { });
            Write("ProjectSettings/ZepSettings.asset");
            SetAnswer(false);

            HandleRemoval();

            Assert.That(_prompts, Is.EqualTo(2));
            Assert.That(_message, Does.Contain("ProjectSettings/ZepSettings.asset"));
            AssertKept();
        }

        [Test]
        public void DeleteData_IsRepeatableAndRemovesOnlyEmptyPackageThemeFolders()
        {
            Populate();
            Directory.CreateDirectory(Path.Combine(_root, "Assets/Editor/Zep/UserThemes"));
            Write("Assets/Editor/Zep/UserThemes.meta");
            Write("Assets/Editor/Zep.meta");
            var plan = Capture();

            Assert.That(ZepPackageRemoval.DeleteData(plan), Is.Empty);
            Assert.That(ZepPackageRemoval.DeleteData(plan), Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(_root, "Assets/Editor/Zep")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "Assets/Editor/Zep.meta")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_root, "Assets/Editor")), Is.True);
        }

        [Test]
        public void NonemptyThemeFolder_KeepsUnrelatedFilesAndItsMeta()
        {
            Populate();
            Write("Assets/Editor/Zep/Unrelated.txt");
            Write("Assets/Editor/Zep.meta");
            Assert.That(ZepPackageRemoval.DeleteData(Capture()), Is.Empty);
            Assert.That(File.Exists(Path.Combine(_root, "Assets/Editor/Zep/Unrelated.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "Assets/Editor/Zep.meta")), Is.True);
        }

        [Test]
        public void CurrentProjectInventory_FindsRelocatedThemesAndExcludesPackageContents()
        {
            var folder = "Assets/pp-removal-theme-test-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var path = folder + "/Relocated.asset";
            try
            {
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ZepUserTheme>(), path);
                var plan = (ZepPackageRemoval.CleanupPlan)typeof(ZepPackageRemoval)
                    .GetMethod("CaptureCurrentProject", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                var absolute = Path.GetFullPath(path);
                Assert.That(plan.Files, Does.Contain(absolute));
                Assert.That(plan.Files, Does.Contain(absolute + ".meta"));
                Assert.That(plan.Files, Has.None.Contains("/Packages/"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void DeleteFailure_ReportsThePathAndContinuesOtherCleanup()
        {
            Populate();
            var plan = Capture();
            var path = Path.Combine(_root, "ProjectSettings/ZepTagSettings.asset");
            File.Delete(path);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "unrelated.txt"), "preserve");

            var failures = ZepPackageRemoval.DeleteData(plan);

            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0], Does.Contain(path));
            Assert.That(File.Exists(Path.Combine(path, "unrelated.txt")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_root, "Library/Zep/UndoPayloads")), Is.False);
        }

        [Test]
        public void LinkedCache_IsRefusedAndTheOutsidePayloadSurvives()
        {
            if (Path.DirectorySeparatorChar == '\\') Assert.Ignore("POSIX symbolic-link fixture.");
            Populate();
            var plan = Capture();
            var cache = Path.Combine(_root, "Library/Zep");
            Directory.Delete(cache, recursive: true);
            var outside = Path.Combine(_root, "OutsideOwnedPaths");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "do not follow");
            var start = new System.Diagnostics.ProcessStartInfo("/bin/ln", "-s \"" + outside + "\" \"" + cache + "\"")
            {
                UseShellExecute = false
            };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero);
            }
            try
            {
                var failures = ZepPackageRemoval.DeleteData(plan);
                Assert.That(failures, Has.Count.EqualTo(1));
                Assert.That(failures[0], Does.Contain("symbolic link"));
                Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("do not follow"));
                Assert.Throws<IOException>(() => Capture());
            }
            finally
            {
                Directory.Delete(cache);
            }
        }

        [Test]
        public void LinkCreatedInInitiallyAbsentScope_IsRefusedAndOutsidePayloadSurvives()
        {
            if (Path.DirectorySeparatorChar == '\\') Assert.Ignore("POSIX symbolic-link fixture.");
            var plan = Capture();
            var cache = Path.Combine(_root, "Library/com.van-unity.zep");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            var outside = Path.Combine(_root, "OutsideOwnedPaths");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "do not follow");
            var start = new System.Diagnostics.ProcessStartInfo("/bin/ln", "-s \"" + outside + "\" \"" + cache + "\"")
            {
                UseShellExecute = false
            };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero);
            }
            try
            {
                var failures = ZepPackageRemoval.DeleteData(plan);
                Assert.That(failures, Has.Count.EqualTo(1));
                Assert.That(failures[0], Does.Contain(cache));
                Assert.That(failures[0], Does.Contain("symbolic link"));
                Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("do not follow"));
            }
            finally
            {
                Directory.Delete(cache);
            }
        }

        [Test]
        public void ProjectRootReplacedByLink_IsRefusedAndOutsidePackagePathsSurvive()
        {
            if (Path.DirectorySeparatorChar == '\\') Assert.Ignore("POSIX symbolic-link fixture.");
            Populate();
            var plan = Capture();
            var original = _root + "-original";
            var outside = _root + "-outside";
            var outsideSettings = Path.Combine(outside, "ProjectSettings/ZepTagSettings.asset");
            var outsidePayload = Path.Combine(outside, "Library/Zep/UndoPayloads/item/payload.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outsideSettings));
            Directory.CreateDirectory(Path.GetDirectoryName(outsidePayload));
            File.WriteAllText(outsideSettings, "outside settings");
            File.WriteAllText(outsidePayload, "outside payload");
            Directory.Move(_root, original);
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("/bin/ln", "-s \"" + outside + "\" \"" + _root + "\"")
                {
                    UseShellExecute = false
                };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    process.WaitForExit();
                    Assert.That(process.ExitCode, Is.Zero);
                }

                var failures = ZepPackageRemoval.DeleteData(plan);

                Assert.That(File.ReadAllText(outsideSettings), Is.EqualTo("outside settings"));
                Assert.That(File.ReadAllText(outsidePayload), Is.EqualTo("outside payload"));
                Assert.That(failures, Has.Some.Contains("symbolic link: " + _root));
                Assert.Throws<IOException>(() => Capture());
            }
            finally
            {
                // Unlink without recursion before restoring the real fixture root.
                if (Directory.Exists(_root)) Directory.Delete(_root);
                Directory.Move(original, _root);
                Directory.Delete(outside, recursive: true);
            }
        }

        [Test]
        public void StoppedThumbnailWriter_DoesNotRecreateDeletedDirectoriesFromAQueuedJob()
        {
            var stopped = typeof(MaterialThumbnailDiskStore).GetField("_writesStopped", BindingFlags.NonPublic | BindingFlags.Static);
            var before = stopped.GetValue(null);
            try
            {
                MaterialThumbnailDiskStore.StopWritesForPackageRemoval();
                var path = Path.Combine(_root, "removed/thumb.bct");
                var record = new MaterialThumbnailDiskStore.Record("hash", Array.Empty<string>(), Array.Empty<string>(), 1, 1, new byte[4]);
                Task.Run(() => MaterialThumbnailDiskStore.Write(path, record)).GetAwaiter().GetResult();
                Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.False);
            }
            finally
            {
                stopped.SetValue(null, before);
            }
        }

        [Test]
        public void StoppedIndexWriter_RefusesSnapshotsAndLateDirtyMarkers()
        {
            var stopped = typeof(PersistentAssetIndexCache).GetField("_writesStopped", BindingFlags.NonPublic | BindingFlags.Static);
            var before = stopped.GetValue(null);
            try
            {
                PersistentAssetIndexCache.StopWritesForPackageRemoval();
                Assert.That(PersistentAssetIndexCache.MarkDirty(), Is.False);
                Assert.That(PersistentAssetIndexCache.TryBeginSnapshotSave(
                    Array.Empty<AssetRecord>(), Array.Empty<AssetFolderRecord>(), 0, default, null, out var attempt), Is.False);
                Assert.That(attempt, Is.Null);
            }
            finally
            {
                stopped.SetValue(null, before);
            }
        }

        private void SetAnswer(bool answer)
        {
            ZepPackageRemoval.DialogForTests = (_, message, accept, decline) =>
            {
                _prompts++;
                _message = message;
                _accept = accept;
                _decline = decline;
                return answer;
            };
        }

        private ZepPackageRemoval.CleanupPlan Capture() => ZepPackageRemoval.Capture(
            _root, new[] { "Assets/RelocatedTheme.asset" }, new[] { _preferenceKey }, Path.Combine(_root, "perf.log"));

        private void HandleRemoval() => ZepPackageRemoval.HandleRemovedPackages(
            new[] { ZepPackageRemoval.PackageName }, Capture, () => { });

        private void Populate()
        {
            Write("ProjectSettings/ZepTagSettings.asset");
            Write("ProjectSettings/Zep.TagAssignments.json");
            Write("UserSettings/ZepFavoritesSettings.asset");
            Write("Assets/RelocatedTheme.asset");
            Write("Assets/RelocatedTheme.asset.meta");
            Write("Library/com.van-unity.zep/thumbs/material.bct");
            Write("Library/Zep/TagStateTransaction.json");
            Write("Library/Zep/UndoPayloads/item/payload.txt");
            Write("Library/Zep/UndoPayloads/item/payload.txt.meta");
            Write("perf.log");
            Write("Assets/UserCreatedScript.cs");
            Write("ProjectSettings/OtherSettings.asset");
            Write("Library/OtherPackage/keep.bin");
        }

        private void Write(string path)
        {
            path = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "fixture");
        }

        private void AssertKept()
        {
            Assert.That(File.Exists(Path.Combine(_root, "ProjectSettings/ZepTagSettings.asset")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "Library/Zep/UndoPayloads/item/payload.txt")), Is.True);
            Assert.That(EditorPrefs.GetString(_preferenceKey), Is.EqualTo("keep until confirmed"));
        }

        private void AssertDeletedAndUnrelatedDataKept()
        {
            Assert.That(Capture().Files, Is.Empty);
            foreach (var path in Capture().Directories)
            {
                Assert.That(Directory.Exists(path), Is.False);
            }
            Assert.That(EditorPrefs.HasKey(_preferenceKey), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "perf.log")), Is.False);
            foreach (var path in new[] { "Assets/UserCreatedScript.cs", "ProjectSettings/OtherSettings.asset", "Library/OtherPackage/keep.bin" })
            {
                Assert.That(File.ReadAllText(Path.Combine(_root, path)), Is.EqualTo("fixture"));
            }
        }
    }
}
