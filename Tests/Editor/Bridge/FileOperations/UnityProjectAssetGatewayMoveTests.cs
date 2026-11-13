using System;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    public sealed class UnityProjectAssetGatewayMoveTests
    {
        private const string LowerCaseName = "caseonly.txt";
        private const string UpperCaseName = "CaseOnly.txt";

        private string _folderPath;
        private string _folderAbsolutePath;
        private UnityProjectAssetGateway _gateway;
        private ProjectFileCommandService _commands;

        [SetUp]
        public void SetUp()
        {
            _folderPath = "Assets/GatewayMoveTests_" + Guid.NewGuid().ToString("N");
            var folderGuid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folderPath));
            Assert.That(folderGuid, Is.Not.Empty);
            _folderAbsolutePath = ProjectPaths.ToAbsolutePath(_folderPath);

            File.WriteAllText(Path.Combine(_folderAbsolutePath, LowerCaseName), "case-only rename fixture");
            AssetDatabase.ImportAsset(_folderPath + "/" + LowerCaseName, ImportAssetOptions.ForceSynchronousImport);

            _gateway = new UnityProjectAssetGateway();
            _commands = new ProjectFileCommandService(_gateway);
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(_folderPath))
            {
                AssetDatabase.DeleteAsset(_folderPath);
            }

            AssetDatabase.Refresh();
        }

        [Test]
        public void CaseOnlyRename_RenamesOnDisk_AndOneUndoRestoresTheOriginalCasing()
        {
            var rename = _commands.Rename(
                AssetPath.From(_folderPath + "/" + LowerCaseName),
                Path.GetFileNameWithoutExtension(UpperCaseName));

            Assert.That(rename.Succeeded, Is.True, rename.ErrorMessage);
            // APFS is case-insensitive, so File.Exists would pass even when the
            // rename never happened. Only the enumerated on-disk name proves it.
            Assert.That(GetSingleTextFileName(), Is.EqualTo(UpperCaseName));
            Assert.That(_commands.CanUndo, Is.True);

            var undo = _commands.Undo();

            Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
            Assert.That(GetSingleTextFileName(), Is.EqualTo(LowerCaseName));
            Assert.That(_commands.CanUndo, Is.False, "a case-only rename must record exactly one undo step");
        }

        [Test]
        public void Move_ToTheSameRawPath_SucceedsWithoutMovingTheAsset()
        {
            var samePath = AssetPath.From(_folderPath + "/" + LowerCaseName);

            // AssetDatabase.MoveAsset reports an error for a move onto its own
            // path, so a success here proves the no-op guard still short-circuits.
            var move = _gateway.Move(samePath, samePath, false);

            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(move.AssetPath, Is.EqualTo((string)samePath));
            Assert.That(GetSingleTextFileName(), Is.EqualTo(LowerCaseName));
        }

        [Test]
        public void Move_CaseOnlyFileRename_RenamesOnDiskAndKeepsTheGuid()
        {
            var sourcePath = _folderPath + "/" + LowerCaseName;
            var destinationPath = _folderPath + "/" + UpperCaseName;
            var originalGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            Assert.That(originalGuid, Is.Not.Empty, "the fixture must be imported before the rename");

            var move = _gateway.Move(AssetPath.From(sourcePath), AssetPath.From(destinationPath), false);

            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(move.AssetPath, Is.EqualTo(destinationPath));
            // The AssetDatabase resolves paths case-insensitively on this volume,
            // so AssetPathToGUID alone cannot tell a real rename from a no-op.
            // The enumerated on-disk spelling is the load-bearing assertion.
            Assert.That(GetSingleTextFileName(), Is.EqualTo(UpperCaseName));
            Assert.That(AssetDatabase.AssetPathToGUID(destinationPath), Is.EqualTo(originalGuid));
        }

        [Test]
        public void Move_CaseOnlyFolderRename_RenamesOnDiskAndKeepsTheGuid()
        {
            const string lowerFolderName = "casedir";
            const string upperFolderName = "CaseDir";

            var sourcePath = _folderPath + "/" + lowerFolderName;
            var destinationPath = _folderPath + "/" + upperFolderName;
            var originalGuid = AssetDatabase.CreateFolder(_folderPath, lowerFolderName);
            Assert.That(originalGuid, Is.Not.Empty);

            var move = _gateway.Move(AssetPath.From(sourcePath), AssetPath.From(destinationPath), false);

            Assert.That(move.Succeeded, Is.True, move.ErrorMessage);
            Assert.That(move.AssetPath, Is.EqualTo(destinationPath));
            Assert.That(GetSingleSubfolderName(), Is.EqualTo(upperFolderName));
            Assert.That(AssetDatabase.AssetPathToGUID(destinationPath), Is.EqualTo(originalGuid));
            Assert.That(AssetDatabase.IsValidFolder(destinationPath), Is.True);
        }

        private string GetSingleTextFileName()
        {
            var files = Directory.GetFiles(_folderAbsolutePath, "*.txt", SearchOption.TopDirectoryOnly);
            Assert.That(files.Length, Is.EqualTo(1));
            return Path.GetFileName(files[0]);
        }

        private string GetSingleSubfolderName()
        {
            var folders = Directory.GetDirectories(_folderAbsolutePath, "*", SearchOption.TopDirectoryOnly);
            Assert.That(folders.Length, Is.EqualTo(1));
            return Path.GetFileName(folders[0]);
        }
    }
}
