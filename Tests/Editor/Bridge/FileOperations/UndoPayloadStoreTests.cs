using System;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    public sealed class UndoPayloadStoreTests
    {
        private UndoPayloadStore _store;
        private string _folder;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/UndoPayloadTests_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(ProjectPaths.ToAbsolutePath(_folder));
            _root = Path.Combine(ProjectPaths.GetProjectRootAbsolutePath(), "Temp", "UndoPayloadTests_" + Guid.NewGuid().ToString("N"));
            UndoPayloadStore.TestRootOverride = _root;
            _store = new UndoPayloadStore();
        }

        [TearDown]
        public void TearDown()
        {
            UndoPayloadStore.TestRootOverride = null;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            AssetDatabase.DeleteAsset(_folder);
            if (Directory.Exists(ProjectPaths.ToAbsolutePath(_folder)))
                Directory.Delete(ProjectPaths.ToAbsolutePath(_folder), true);
            File.Delete(ProjectPaths.ToAbsolutePath(_folder) + ".meta");
            AssetDatabase.Refresh();
        }

        [Test]
        public void StoreRestoreAndReuse_PreserveCurrentBytesAndExactMetadata()
        {
            var path = WriteFile("Example.txt", "first bytes");
            var meta = File.ReadAllBytes(ProjectPaths.ToAbsolutePath(path) + ".meta");
            var saved = _store.Store(AssetPath.From(path));
            Assert.That(saved.Succeeded, Is.True, saved.ErrorMessage);
            Assert.That(File.Exists(ProjectPaths.ToAbsolutePath(path)), Is.False);
            Assert.That(File.ReadAllText(saved.AssetPath), Is.EqualTo("first bytes"));
            Assert.That(File.ReadAllBytes(saved.AssetPath + ".meta"), Is.EqualTo(meta));
            Assert.That(_store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path)).Succeeded, Is.True);

            File.WriteAllText(ProjectPaths.ToAbsolutePath(path), "edited outside history");
            var savedAgain = _store.Store(AssetPath.From(path), AssetPath.From(saved.AssetPath));
            Assert.That(savedAgain.Succeeded, Is.True, savedAgain.ErrorMessage);
            Assert.That(savedAgain.AssetPath, Is.EqualTo(saved.AssetPath));
            Assert.That(_store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path)).Succeeded, Is.True);
            Assert.That(File.ReadAllText(ProjectPaths.ToAbsolutePath(path)), Is.EqualTo("edited outside history"));
            Assert.That(File.ReadAllBytes(ProjectPaths.ToAbsolutePath(path) + ".meta"), Is.EqualTo(meta));
        }

        [Test]
        public void FolderPayload_PreservesEveryFileIncludingUnityIgnoredEntries()
        {
            var path = _folder + "/Folder";
            var absolute = ProjectPaths.ToAbsolutePath(path);
            Directory.CreateDirectory(Path.Combine(absolute, "Nested"));
            File.WriteAllText(absolute + ".meta", "guid: " + Guid.NewGuid().ToString("N") + "\n");
            File.WriteAllText(Path.Combine(absolute, ".hidden"), "hidden bytes");
            File.WriteAllText(Path.Combine(absolute, "Nested", "child.txt"), "child bytes");
            var childMeta = "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n";
            File.WriteAllText(Path.Combine(absolute, "Nested", "child.txt.meta"), childMeta);
            var saved = _store.Store(AssetPath.From(path));
            Assert.That(saved.Succeeded, Is.True, saved.ErrorMessage);
            Assert.That(_store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path)).Succeeded, Is.True);
            Assert.That(File.ReadAllText(Path.Combine(absolute, ".hidden")), Is.EqualTo("hidden bytes"));
            Assert.That(File.ReadAllText(Path.Combine(absolute, "Nested", "child.txt.meta")), Is.EqualTo(childMeta));
        }

        [Test]
        public void StoreMetadataMoveFailure_RollsBackWithoutLosingIdentity()
        {
            var path = WriteFile("Example.txt", "original");
            _store.MoveFileSystemEntryOverrideForTests = (source, destination) =>
                destination.EndsWith(".meta", StringComparison.Ordinal) ? false : (bool?)null;
            var result = _store.Store(AssetPath.From(path));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.ReadAllText(ProjectPaths.ToAbsolutePath(path)), Is.EqualTo("original"));
            Assert.That(File.Exists(ProjectPaths.ToAbsolutePath(path) + ".meta"), Is.True);
            Assert.That(File.Exists(result.PreviousPath), Is.False);
        }

        [Test]
        public void StoreRollbackFailure_CompletesCoherentForwardMove()
        {
            var path = WriteFile("Example.txt", "original");
            var metaFailureInjected = false;
            var absolute = ProjectPaths.ToAbsolutePath(path);
            _store.MoveFileSystemEntryOverrideForTests = (source, destination) =>
            {
                if (!metaFailureInjected && source == absolute + ".meta")
                {
                    metaFailureInjected = true;
                    return false;
                }
                return destination == absolute ? false : (bool?)null;
            };
            var result = _store.Store(AssetPath.From(path));
            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(File.ReadAllText(result.AssetPath), Is.EqualTo("original"));
            Assert.That(File.Exists(result.AssetPath + ".meta"), Is.True);
            Assert.That(File.Exists(absolute), Is.False);
            Assert.That(File.Exists(absolute + ".meta"), Is.False);
        }

        [Test]
        public void RestoreMetadataMoveFailure_LeavesCompletePayloadInItsSlot()
        {
            var path = WriteFile("Example.txt", "original");
            var saved = _store.Store(AssetPath.From(path));
            Assert.That(saved.Succeeded, Is.True);
            _store.MoveFileSystemEntryOverrideForTests = (source, destination) =>
                destination == ProjectPaths.ToAbsolutePath(path) + ".meta" ? false : (bool?)null;
            var result = _store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.ReadAllText(saved.AssetPath), Is.EqualTo("original"));
            Assert.That(File.Exists(saved.AssetPath + ".meta"), Is.True);
            Assert.That(File.Exists(ProjectPaths.ToAbsolutePath(path)), Is.False);
        }

        [Test]
        public void OccupiedDestinationWithSameGuidAndDifferentBytes_IsNeverOverwritten()
        {
            var path = WriteFile("Example.txt", "original");
            var saved = _store.Store(AssetPath.From(path));
            var absolute = ProjectPaths.ToAbsolutePath(path);
            File.WriteAllText(absolute, "replacement");
            File.Copy(saved.AssetPath + ".meta", absolute + ".meta");
            var result = _store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("conflict"));
            Assert.That(File.ReadAllText(absolute), Is.EqualTo("replacement"));
            Assert.That(File.ReadAllText(saved.AssetPath), Is.EqualTo("original"));
            Assert.That(File.Exists(Path.Combine(_root, "recovery-required")), Is.True);
        }

        [Test]
        public void IdenticalExternallyRestoredDestination_DropsOnlyItsRedundantPayload()
        {
            var path = WriteFile("Example.txt", "original");
            var saved = _store.Store(AssetPath.From(path));
            var other = _store.Store(AssetPath.From(WriteFile("Other.txt", "keep")));
            File.Copy(saved.AssetPath, ProjectPaths.ToAbsolutePath(path));
            File.Copy(saved.AssetPath + ".meta", ProjectPaths.ToAbsolutePath(path) + ".meta");
            var result = _store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path));
            Assert.That(result.Succeeded, Is.True, result.ErrorMessage);
            Assert.That(Directory.Exists(Path.GetDirectoryName(saved.AssetPath)), Is.False);
            Assert.That(File.ReadAllText(ProjectPaths.ToAbsolutePath(path)), Is.EqualTo("original"));
            Assert.That(File.ReadAllText(other.AssetPath), Is.EqualTo("keep"));
        }

        [Test]
        public void OrphanDestinationMetadata_RefusesRestoreAndKeepsThePayload()
        {
            var path = WriteFile("Example.txt", "original");
            var saved = _store.Store(AssetPath.From(path));
            File.WriteAllText(ProjectPaths.ToAbsolutePath(path) + ".meta", "replacement identity");
            var result = _store.Restore(AssetPath.From(saved.AssetPath), AssetPath.From(path));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(File.Exists(saved.AssetPath), Is.True);
            Assert.That(File.ReadAllText(ProjectPaths.ToAbsolutePath(path) + ".meta"), Is.EqualTo("replacement identity"));
        }

        [Test]
        public void UnownedStoragePathAndEscapingProjectPath_AreRejected()
        {
            var path = WriteFile("Example.txt", "original");
            var outside = Path.Combine(Path.GetDirectoryName(_root), "outside.txt");
            Assert.That(_store.Store(AssetPath.From(path), AssetPath.From(outside)).Succeeded, Is.False);
            Assert.That(_store.Delete(AssetPath.From(outside)).Succeeded, Is.False);
            Assert.That(_store.Store(AssetPath.From(_folder + "/../../ProjectSettings/ProjectSettings.asset")).Succeeded, Is.False);
            Assert.That(File.ReadAllText(ProjectPaths.ToAbsolutePath(path)), Is.EqualTo("original"));
        }

        [Test]
        public void CommandHistoryReload_RestoresExactBytesAndGuid()
        {
            var path = WriteFile("Example.txt", "original");
            AssetDatabase.Refresh();
            var stateKey = "Zep.UndoPayloadReloadTest." + Guid.NewGuid().ToString("N");
            var storage = new SessionUndoHistoryStorage(stateKey);
            try
            {
                var gateway = new UnityProjectAssetGateway();
                var guid = gateway.GetGuid(AssetPath.From(path));
                var history = new ProjectCommandHistory(storage.UndoStack, storage.RedoStack, storage.Save);
                var commands = new ProjectFileCommandService(gateway, history);
                Assert.That(commands.Delete(AssetPath.From(path)).Succeeded, Is.True);
                storage.SetAppliedRevision(storage.UndoStack.Count);
                storage.Save();
                var reloaded = new SessionUndoHistoryStorage(stateKey);
                var replay = new ProjectFileCommandService(gateway,
                    new ProjectCommandHistory(reloaded.UndoStack, reloaded.RedoStack, reloaded.Save));
                Assert.That(replay.Undo().Succeeded, Is.True);
                Assert.That(File.ReadAllText(ProjectPaths.ToAbsolutePath(path)), Is.EqualTo("original"));
                Assert.That(gateway.GetGuid(AssetPath.From(path)), Is.EqualTo(guid));
            }
            finally { storage.EraseForTests(); }
        }

        [Test]
        public void NewEditorSession_CleansExpiredPayloadsAndKeepsTheActiveSession()
        {
            var active = Guid.NewGuid().ToString("N");
            var expired = Guid.NewGuid().ToString("N");
            var recovery = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(_root, active));
            Directory.CreateDirectory(Path.Combine(_root, expired));
            Directory.CreateDirectory(Path.Combine(_root, recovery));
            File.WriteAllText(Path.Combine(_root, recovery, "recovery-required"), "preserve");
            File.WriteAllText(Path.Combine(_root, recovery, "payload"), "recovery bytes");
            File.WriteAllText(Path.Combine(_root, active, "current"), "active payload");
            var expiredSlot = Path.Combine(_root, expired, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(expiredSlot);
            File.WriteAllText(Path.Combine(expiredSlot, "old.txt"), "expired payload");
            File.WriteAllText(Path.Combine(expiredSlot, "old.txt.meta"), "expired metadata");
            var suspicious = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(suspicious);
            File.WriteAllText(Path.Combine(suspicious, "unexpected"), "preserve evidence");
            File.WriteAllText(Path.Combine(_root, "unowned.txt"), "leave alone");

            UndoPayloadStore.CleanupExpiredSessions(_root, active);

            Assert.That(File.ReadAllText(Path.Combine(_root, active, "current")), Is.EqualTo("active payload"));
            Assert.That(Directory.Exists(Path.Combine(_root, expired)), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_root, recovery, "payload")), Is.EqualTo("recovery bytes"));
            Assert.That(File.ReadAllText(Path.Combine(suspicious, "unexpected")), Is.EqualTo("preserve evidence"));
            Assert.That(File.ReadAllText(Path.Combine(_root, "unowned.txt")), Is.EqualTo("leave alone"));
        }

        [Test]
        public void CrossVolumeWritablePackage_IsRejectedBeforeMovingAnything()
        {
            var previous = RegisteredPackageRoots.Current;
            var packageRoot = Path.Combine(_root, "Package");
            Directory.CreateDirectory(packageRoot);
            var absolute = Path.Combine(packageRoot, "Example.txt");
            File.WriteAllText(absolute, "original");
            File.WriteAllText(absolute + ".meta", "guid: " + Guid.NewGuid().ToString("N") + "\n");
            RegisteredPackageRoots.SetForTests(new RegisteredPackageRootSnapshot(new[]
            {
                new RegisteredPackageRoot("Packages/com.example.undo-test", packageRoot, "Temporary", true)
            }, true));
            try
            {
                _store.SameFileSystemVolumeOverrideForTests = (source, destination) => false;
                var result = _store.Store(AssetPath.From("Packages/com.example.undo-test/Example.txt"));
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.ErrorMessage, Does.Contain("different filesystem volume"));
                Assert.That(File.ReadAllText(absolute), Is.EqualTo("original"));
                Assert.That(File.Exists(absolute + ".meta"), Is.True);
            }
            finally { RegisteredPackageRoots.SetForTests(previous); }
        }

        [Test]
        public void LinkedChild_RefusesMovingTheWholeFolder()
        {
            if (Path.DirectorySeparatorChar == '\\') Assert.Ignore("Unix link fixture");
            var path = _folder + "/Folder";
            var absolute = ProjectPaths.ToAbsolutePath(path);
            Directory.CreateDirectory(absolute);
            Directory.CreateDirectory(_root);
            var target = Path.Combine(_root, "outside.txt");
            var link = Path.Combine(absolute, "linked.txt");
            File.WriteAllText(target, "external bytes");
            File.WriteAllText(absolute + ".meta", "guid: " + Guid.NewGuid().ToString("N") + "\n");
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/ln",
                Arguments = "-s \"" + target.Replace("\"", "\\\"") + "\" \"" + link.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false
            }))
            {
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero);
            }
            try
            {
                var result = _store.Store(AssetPath.From(path));
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.ErrorMessage, Does.Contain("Linked"));
                Assert.That(Directory.Exists(absolute), Is.True);
                Assert.That(File.ReadAllText(target), Is.EqualTo("external bytes"));
            }
            finally { File.Delete(link); }
        }

        private string WriteFile(string name, string contents)
        {
            var path = _folder + "/" + name;
            var absolute = ProjectPaths.ToAbsolutePath(path);
            File.WriteAllText(absolute, contents);
            File.WriteAllText(absolute + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
            return path;
        }
    }
}
