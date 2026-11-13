using System;
using System.Collections;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    public sealed class UnityUndoFilesystemIntegrationTests
    {
        private string _folderPath;
        private string _payloadRoot;
        private string _stateKey;
        private SessionUndoHistoryStorage _storage;
        private ProjectCommandHistory _history;
        private ProjectFileCommandService _commands;
        private ZepUnityUndoCursor _cursor;
        private UnityProjectUndoBridge _bridge;
        private GameObject _camera;

        [SetUp]
        public void SetUp()
        {
            _folderPath = "Assets/NewPrefabsNew_" + Guid.NewGuid().ToString("N");
            _payloadRoot = Path.Combine(
                ProjectPaths.GetProjectRootAbsolutePath(),
                "Temp",
                "UnityUndoFilesystemIntegration_" + Guid.NewGuid().ToString("N"));
            UndoPayloadStore.TestRootOverride = _payloadRoot;
            _stateKey = "Zep.Tests.UndoFilesystem."
                + Guid.NewGuid().ToString("N");
            _storage = new SessionUndoHistoryStorage(_stateKey);
            _history = new ProjectCommandHistory(
                _storage.UndoStack,
                _storage.RedoStack,
                _storage.Save);
            _history.Trimmed += _storage.NoteTrimmedRecords;
            _commands = new ProjectFileCommandService(
                new UnityProjectAssetGateway(),
                _history);
            _cursor = ScriptableObject.CreateInstance<ZepUnityUndoCursor>();
            _cursor.hideFlags = HideFlags.HideAndDontSave;
            _cursor.Initialize(_storage.SessionId, 0);
            _bridge = new UnityProjectUndoBridge(
                _cursor,
                _storage,
                _history,
                () => _commands.Undo().Succeeded,
                () => _commands.Redo().Succeeded,
                work => work(),
                () => _history.ClearRedo());
            _commands.SetExecutionPreparation(_bridge.PrepareForNewCommand);
            _history.Executed += _bridge.RecordExecuted;
            _history.Invalidated += _bridge.InvalidateTimeline;

            var folderGuid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folderPath));
            Assert.That(folderGuid, Is.Not.Empty);
            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.True);

            _camera = new GameObject("MainCamera Undo Integration Test");
            _camera.hideFlags = HideFlags.HideAndDontSave;
            _camera.transform.localScale = Vector3.one;
        }

        [TearDown]
        public void TearDown()
        {
            if (_history != null && _bridge != null)
            {
                _history.Executed -= _bridge.RecordExecuted;
                _history.Invalidated -= _bridge.InvalidateTimeline;
            }

            _bridge?.Dispose();
            if (_cursor != null)
            {
                Undo.ClearUndo(_cursor);
                UnityEngine.Object.DestroyImmediate(_cursor);
            }

            if (_camera != null)
            {
                Undo.ClearUndo(_camera.transform);
                UnityEngine.Object.DestroyImmediate(_camera);
            }

            _storage?.EraseForTests();
            if (AssetDatabase.IsValidFolder(_folderPath))
            {
                AssetDatabase.DeleteAsset(_folderPath);
            }

            UndoPayloadStore.TestRootOverride = null;
            if (Directory.Exists(_payloadRoot))
            {
                Directory.Delete(_payloadRoot, true);
            }

            AssetDatabase.Refresh();
        }

        [Test]
        public void DeleteThenInspectorEdit_TwoUnityUndosRestoreExactlyOneFolder()
        {
            var delete = _commands.Delete(AssetPath.From(_folderPath));
            Assert.That(delete.Succeeded, Is.True, delete.ErrorMessage);
            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.False);

            Undo.IncrementCurrentGroup();
            var inspectorGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Change Main Camera Scale");
            Undo.RegisterCompleteObjectUndo(_camera.transform, "Change Main Camera Scale");
            _camera.transform.localScale = new Vector3(3f, 1f, 1f);
            EditorUtility.SetDirty(_camera.transform);
            Undo.CollapseUndoOperations(inspectorGroup);
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(_camera.transform.localScale.x, Is.EqualTo(1f));
            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.False,
                "the newer Inspector operation must undo before the panel delete");

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.True);
            Assert.That(
                Directory.GetDirectories(
                    ProjectPaths.ToAbsolutePath("Assets"),
                    Path.GetFileName(_folderPath),
                    SearchOption.TopDirectoryOnly).Length,
                Is.EqualTo(1),
                "the restored folder must have one physical identity");
        }

        [UnityTest]
        public IEnumerator Delete_UnityUndoAndDelayedReconciliation_PreservesRedo()
        {
            yield return null;

            var delete = _commands.Delete(AssetPath.From(_folderPath));
            Assert.That(delete.Succeeded, Is.True, delete.ErrorMessage);
            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.False);

            // Drain cursor registration and delete-side object changes before
            // exercising the real delayed reconciliation path used in the editor.
            yield return null;

            Undo.PerformUndo();
            yield return WaitForAppliedRevision(0);

            // Let Unity publish any AssetDatabase/object-change fallout from
            // the restored folder after the bridge has finished reconciliation.
            for (var i = 0; i < 3; i++)
            {
                yield return null;
                if (!AssetDatabase.IsValidFolder(_folderPath))
                {
                    Assert.Fail(
                        "restored folder disappeared after frame " + (i + 1)
                        + "; cursor=" + _cursor.Revision
                        + ", storage=" + _storage.AppliedRevision
                        + ", canUndo=" + _history.CanUndo
                        + ", canRedo=" + _history.CanRedo);
                }
            }

            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.True);
            Assert.That(_history.CanRedo, Is.True,
                "panel-owned object changes published after replay must not cut redo");

            Undo.PerformRedo();
            Assert.That(_cursor.Revision, Is.EqualTo(1),
                "filesystem replay must not erase Unity's native redo marker");
            yield return WaitForAppliedRevision(1);

            Assert.That(_history.CanUndo, Is.True);
            Assert.That(_history.CanRedo, Is.False);
            Assert.That(AssetDatabase.IsValidFolder(_folderPath), Is.False);
        }

        // Replace at a move destination is this sequence inside one asset
        // batch. The batch freezes the AssetDatabase, which went on reporting
        // the occupant the delete had already moved to undo storage, so the
        // move was refused with "already exists" and Replace never worked.
        [Test]
        public void InsideABatch_MovingOntoAPathTheBatchJustVacated_Succeeds()
        {
            var sourcePath = CreateTextAsset(_folderPath, "note.txt", "incoming");
            var destinationFolder = _folderPath + "/Dest";
            AssetDatabase.CreateFolder(_folderPath, "Dest");
            var occupantPath = CreateTextAsset(destinationFolder, "note.txt", "occupant");
            var gateway = new UnityProjectAssetGateway();
            var commands = new ProjectFileCommandService(gateway);

            ProjectFileOperationResult delete;
            ProjectFileOperationResult move;
            bool occupantExistsAfterDelete;
            gateway.BeginBatch();
            try
            {
                delete = commands.Delete(AssetPath.From(occupantPath));
                occupantExistsAfterDelete = gateway.Exists(AssetPath.From(occupantPath));
                move = commands.Move(AssetPath.From(sourcePath), AssetPath.From(destinationFolder));
            }
            finally
            {
                gateway.EndBatch();
            }

            Assert.That(delete.Succeeded, Is.True, delete.ErrorMessage);
            Assert.That(occupantExistsAfterDelete, Is.False,
                "an item the batch moved to undo storage is gone, whatever the frozen database says");
            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(File.ReadAllText(occupantPath), Is.EqualTo("incoming"));
            Assert.That(File.Exists(sourcePath), Is.False);
        }

        // A Replace, its Undo and its Redo each swap a path's owner inside one
        // batch. Left to the batch's closing rescan, an interactive editor gave
        // the replacement a new GUID on Undo and kept the displaced item's GUID
        // for the path on Redo. A batchmode run rescans differently and gets
        // the swap right either way, so there this only guards the outcome;
        // the failure itself was reproduced in an open 6000.3 editor.
        [Test]
        public void InsideABatch_ReplaceUndoRedo_KeepsBothIdentities()
        {
            var sourcePath = CreateTextAsset(_folderPath, "note.txt", "incoming");
            var destinationFolder = _folderPath + "/Dest";
            AssetDatabase.CreateFolder(_folderPath, "Dest");
            var occupantPath = CreateTextAsset(destinationFolder, "note.txt", "occupant");
            var incomingGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var occupantGuid = AssetDatabase.AssetPathToGUID(occupantPath);
            var gateway = new UnityProjectAssetGateway();
            var commands = new ProjectFileCommandService(gateway);

            gateway.BeginBatch();
            try
            {
                Assert.That(commands.Delete(AssetPath.From(occupantPath)).Succeeded, Is.True);
                Assert.That(commands.Move(
                    AssetPath.From(sourcePath), AssetPath.From(destinationFolder)).Succeeded, Is.True);
            }
            finally
            {
                gateway.EndBatch();
            }

            ProjectFileOperationResult undoMove;
            ProjectFileOperationResult undoDelete;
            gateway.BeginBatch();
            try
            {
                undoMove = commands.Undo();
                undoDelete = commands.Undo();
            }
            finally
            {
                gateway.EndBatch();
            }

            Assert.That(undoMove.Succeeded, Is.True, undoMove.ErrorMessage);
            Assert.That(undoDelete.Succeeded, Is.True, undoDelete.ErrorMessage);
            Assert.That(File.ReadAllText(sourcePath), Is.EqualTo("incoming"));
            Assert.That(File.ReadAllText(occupantPath), Is.EqualTo("occupant"));
            Assert.That(AssetDatabase.AssetPathToGUID(sourcePath), Is.EqualTo(incomingGuid),
                "the replacement must come back as the same asset");
            Assert.That(gateway.GetGuid(AssetPath.From(sourcePath)), Is.EqualTo(incomingGuid),
                "and its .meta must still say so");
            Assert.That(AssetDatabase.AssetPathToGUID(occupantPath), Is.EqualTo(occupantGuid));
            Assert.That(gateway.GetGuid(AssetPath.From(occupantPath)), Is.EqualTo(occupantGuid));

            ProjectFileOperationResult redoDelete;
            ProjectFileOperationResult redoMove;
            gateway.BeginBatch();
            try
            {
                redoDelete = commands.Redo();
                redoMove = commands.Redo();
            }
            finally
            {
                gateway.EndBatch();
            }

            Assert.That(redoDelete.Succeeded, Is.True, redoDelete.ErrorMessage);
            Assert.That(redoMove.Succeeded, Is.True, redoMove.ErrorMessage);
            Assert.That(File.ReadAllText(occupantPath), Is.EqualTo("incoming"));
            Assert.That(AssetDatabase.AssetPathToGUID(occupantPath), Is.EqualTo(incomingGuid),
                "after Redo the database must name the replacement at the path");
            Assert.That(gateway.GetGuid(AssetPath.From(occupantPath)), Is.EqualTo(incomingGuid));
        }

        // Keep Both asks for a free name while earlier items of the same drop
        // have already landed. The frozen database did not see them, so it
        // offered a name that was taken and the move died on Unity's raw error.
        [Test]
        public void InsideABatch_ExistsSeesAnItemTheBatchMovedIn()
        {
            var sourcePath = CreateTextAsset(_folderPath, "note.txt", "incoming");
            var landedPath = AssetPath.From(_folderPath + "/note 1.txt");
            var gateway = new UnityProjectAssetGateway();

            ProjectFileOperationResult move;
            bool landedExists;
            bool sourceExists;
            gateway.BeginBatch();
            try
            {
                move = gateway.Move(AssetPath.From(sourcePath), landedPath, false);
                landedExists = gateway.Exists(landedPath);
                sourceExists = gateway.Exists(AssetPath.From(sourcePath));
            }
            finally
            {
                gateway.EndBatch();
            }

            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(landedExists, Is.True);
            Assert.That(sourceExists, Is.False);
        }

        // The batch answer must not turn Exists into File.Exists: a file Unity
        // refuses to import is still not an asset (see
        // UnityProjectAssetGatewayTests.Exists_ForATildeSuffixedFileOnDisk_IsFalse).
        [Test]
        public void InsideABatch_ExistsIsStillFalseForAFileUnityDoesNotImport()
        {
            var hiddenPath = _folderPath + "/HiddenFromUnity.txt~";
            File.WriteAllText(hiddenPath, "on disk, not an asset");
            AssetDatabase.Refresh();
            var gateway = new UnityProjectAssetGateway();

            bool exists;
            gateway.BeginBatch();
            try
            {
                exists = gateway.Exists(AssetPath.From(hiddenPath));
            }
            finally
            {
                gateway.EndBatch();
            }

            Assert.That(File.Exists(hiddenPath), Is.True, "the fixture must really be on disk");
            Assert.That(exists, Is.False);
        }

        private static string CreateTextAsset(string folderPath, string fileName, string content)
        {
            var assetPath = folderPath + "/" + fileName;
            File.WriteAllText(assetPath, content);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        private IEnumerator WaitForAppliedRevision(int expectedRevision)
        {
            var deadline = EditorApplication.timeSinceStartup + 5d;
            while (_storage.AppliedRevision != expectedRevision
                && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(
                _storage.AppliedRevision,
                Is.EqualTo(expectedRevision),
                "the delayed bridge must consume Unity's native cursor");
        }
    }
}
