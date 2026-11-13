using Zep.Core.Indexing;
using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;
using Zep.Editor.Bridge.Settings;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Zep.Editor.Bridge.FileOperations
{
    public sealed class UnityProjectAssetGateway : IProjectAssetGateway, IProjectTagDefinitionGateway, IProjectTagAssignmentPreparationGateway
    {
        // One journal for every gateway instance: the transaction file is
        // project-wide, and so is the knowledge of whether it still needs recovery.
        internal static readonly TagStateJournal TagTransactionJournal = new TagStateJournal(
            GetTagTransactionPath,
            static () => ZepTagSettings.instance.Store,
            static () => ZepTagAssignmentStore.instance,
            static message => Debug.LogWarning(message));

        internal static string TagTransactionPathForTests => GetTagTransactionPath();

        private bool _tagStateReady;

        // UndoPayloadStore and ExternalAssetImporter live in Core; these hosts are
        // everything they ask of Unity. The static constructor covers a gateway
        // built before Unity runs load callbacks, the load callback covers code
        // that reaches either class first.
        static UnityProjectAssetGateway()
        {
            BindCoreFileOperations();
        }

        [InitializeOnLoadMethod]
        private static void BindCoreFileOperations()
        {
            UndoPayloadStore.BindUnity(UndoPayloadHost.Instance);
            ExternalAssetImporter.BindUnity(ExternalImportHost.Instance);
        }

        private sealed class ExternalImportHost : IExternalImportEditorHost
        {
            internal static readonly ExternalImportHost Instance = new ExternalImportHost();

            public string ProjectRootAbsolutePath => ProjectPaths.GetProjectRootAbsolutePath();

            public string ToAbsolutePath(string assetPath) => ProjectPaths.ToAbsolutePath(assetPath);

            public bool CanWrite(string assetPath) => CanWriteAssetPath(assetPath);

            public bool IsValidFolder(string assetPath) => AssetDatabase.IsValidFolder(assetPath);

            public void ImportPackage(string packageFullPath) =>
                AssetDatabase.ImportPackage(packageFullPath, interactive: true);

            public void StartAssetEditing() => AssetDatabase.StartAssetEditing();

            public void StopAssetEditing() => AssetDatabase.StopAssetEditing();

            public void ImportAsset(string assetPath, bool recursive) => AssetDatabase.ImportAsset(
                assetPath,
                recursive ? ImportAssetOptions.ImportRecursive : ImportAssetOptions.Default);

            public void LogWarning(string message) => Debug.LogWarning(message);
        }

        private sealed class UndoPayloadHost : IUndoPayloadEditorHost
        {
            internal static readonly UndoPayloadHost Instance = new UndoPayloadHost();

            public string ProjectRootAbsolutePath => ProjectPaths.GetProjectRootAbsolutePath();

            public string ToAbsolutePath(string assetPath) => ProjectPaths.ToAbsolutePath(assetPath);

            public bool CanWrite(AssetPath assetPath) => CanWriteAssetPath(assetPath);

            public string GetSessionString(string key) => SessionState.GetString(key, string.Empty);

            public void SetSessionString(string key, string value) => SessionState.SetString(key, value);

            public void LogWarning(string message) => Debug.LogWarning(message);

            public void ImportAsset(AssetPath assetPath) => AssetDatabase.ImportAsset(
                assetPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);

            public void RefreshAssetDatabase() => AssetDatabase.Refresh();
        }

        private readonly UndoPayloadStore _undoPayloads = new UndoPayloadStore();

        // Open BeginBatch calls; see Exists.
        private int _batchDepth;

        internal UndoPayloadStore UndoPayloadsForTests => _undoPayloads;

        public int TagVersion
        {
            get
            {
                EnsureTagStateReady(checkDisk: true);
                var assignmentStore = ZepTagAssignmentStore.instance;
                assignmentStore.ReloadIfFileChanged();
                return assignmentStore.Version;
            }
        }

        // Presence only. This used to answer "is there an asset here" by
        // deserialising the asset (LoadMainAssetAtPath != null), and it is
        // asked per item: RecordCreatedAsset runs it over every path of an
        // external drop and CanTag over every path of a tag edit, so a
        // multi-thousand-file drop pulled that many textures and prefabs into
        // the heap to answer a yes/no question.
        //
        // The AssetDatabase, not the filesystem, is the right source: a hidden
        // or '~'-suffixed file Unity refuses to import exists on disk but is
        // not an asset, and RecordCreatedAsset relies on this returning false
        // for exactly that case. File.Exists would answer true and record an
        // undo entry for a file that has no asset to undo.
        public bool Exists(AssetPath assetPath)
        {
            // The database is frozen while this gateway holds a batch: it keeps
            // reporting an item the batch already moved to undo storage and does
            // not yet see one the batch moved in, while AssetDatabase.MoveAsset
            // itself answers from the disk. Asked through the stale database,
            // Replace was refused over the occupant it had just displaced and
            // Keep Both handed out a name another item of the same drop had
            // taken. So inside a batch the disk decides what has gone, and a
            // sibling .meta - which Unity never writes for a file it refuses to
            // import - decides what has arrived. Everything else, the hidden and
            // '~' files above included, is still the database's answer.
            if (_batchDepth > 0)
            {
                var absolutePath = ResolveAbsolutePath(assetPath);
                if (!string.IsNullOrEmpty(absolutePath))
                {
                    if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
                    {
                        return false;
                    }

                    if (File.Exists(absolutePath + ".meta"))
                    {
                        return true;
                    }
                }
            }

            return ExistsInDatabase(assetPath);
        }

        private static bool ExistsInDatabase(AssetPath assetPath)
        {
#if UNITY_6000_0_OR_NEWER
            return AssetDatabase.IsValidFolder(assetPath)
                || AssetDatabase.AssetPathExists(assetPath);
#else
            // AssetPathExists arrived in 2023.1. OnlyExistingAssets asks the
            // same question on older editors: it returns an empty GUID rather
            // than minting one for a path the database does not hold.
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return true;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            return !string.IsNullOrEmpty(
                AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets));
#endif
        }

        public bool IsFolder(AssetPath assetPath)
        {
            return AssetDatabase.IsValidFolder(assetPath);
        }

        public bool CanWrite(AssetPath assetPath)
        {
            return CanWriteAssetPath(assetPath);
        }

        // Single owner of the package write policy: Assets (and anything else
        // AssetPathClassification already calls writable) plus the two package
        // sources whose contents are the user's own editable files. Registry,
        // Git, built-in and unknown packages are read-only — writing into them
        // either fails or edits a shared cache. UndoPayloadStore calls this
        // too: its boundary is reached by persisted undo commands and
        // payload restores, not only by UI paths that already asked.
        internal static bool CanWriteAssetPath(string assetPath)
        {
            if (AssetPathClassification.IsWritablePath(assetPath))
            {
                return true;
            }

            if (!AssetPathClassification.IsPackagesPath(assetPath))
            {
                return false;
            }

            // The registered-package snapshot already carries each package's
            // source, so this is a prefix lookup. FindForAssetPath was a Package
            // Manager call per asked path, and drop filtering, context-menu
            // building and the create flow ask per item.
            var snapshot = RegisteredPackageRoots.Current;
            if (!snapshot.IsComplete)
            {
                snapshot = RegisteredPackageRoots.Refresh();
            }

            if (snapshot.IsComplete)
            {
                return snapshot.TryFindRoot(assetPath, out var root) && root.IsWritable;
            }

            // Package Manager has not answered yet this domain: ask it directly.
            try
            {
                var package = PackageInfo.FindForAssetPath(assetPath);
                return package != null
                    && (package.source == PackageSource.Embedded
                        || package.source == PackageSource.Local);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Zep] Could not resolve package write access for '"
                    + assetPath + "': " + ex.Message);
                return false;
            }
        }

        public bool CanTag(AssetPath assetPath)
        {
            return !AssetPathClassification.IsProjectRootPath(assetPath)
                && Exists(assetPath)
                && !string.IsNullOrWhiteSpace(ResolveAssetGuid(assetPath));
        }

        public bool EnsureFolder(AssetPath folderPath)
        {
            if (folderPath.IsEmpty)
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return true;
            }

            var parentFolderPath = AssetPath.From(AssetPathFormat.GetParentFolder(folderPath));
            if (parentFolderPath.IsEmpty || !EnsureFolder(parentFolderPath))
            {
                return false;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateFolderCalls);
            AssetDatabase.CreateFolder(parentFolderPath, AssetPathFormat.GetFileName(folderPath));
            return AssetDatabase.IsValidFolder(folderPath);
        }

        public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName)
        {
            if (!AssetDatabase.IsValidFolder(parentFolderPath))
            {
                return ProjectFileOperationResult.Failure("Parent folder does not exist.", parentFolderPath);
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateFolderCalls);
            var createdGuid = AssetDatabase.CreateFolder(parentFolderPath, folderName);
            if (string.IsNullOrEmpty(createdGuid))
            {
                return ProjectFileOperationResult.Failure("Failed to create the folder.", parentFolderPath);
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            return ProjectFileOperationResult.Success(
                AssetDatabase.GUIDToAssetPath(createdGuid),
                parentFolderPath);
        }

        public ProjectFileOperationResult Move(
            AssetPath sourcePath,
            AssetPath destinationPath,
            bool allowOverwriteWithUniquePath)
        {
            // AssetPath equality is OrdinalIgnoreCase (a domain decision), but a
            // case-only rename is real work AssetDatabase.MoveAsset performs. Only
            // an exactly equal raw path is a no-op; `foo.png` -> `Foo.png` must
            // reach Unity, or the rename silently records a no-op undo step.
            if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            {
                return ProjectFileOperationResult.Success(sourcePath, sourcePath);
            }

            var destinationFolderPath = AssetPath.From(AssetPathFormat.GetParentFolder(destinationPath));
            if (!destinationFolderPath.IsEmpty && !EnsureFolder(destinationFolderPath))
            {
                return ProjectFileOperationResult.Failure(
                    "Destination folder could not be created.",
                    destinationFolderPath,
                    sourcePath);
            }

            SettleVacatedPath(destinationPath);

            string destination = destinationPath;
            if (allowOverwriteWithUniquePath)
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
                destination = AssetDatabase.GenerateUniqueAssetPath(destination);
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseMoveAssetCalls);
            var moveError = AssetDatabase.MoveAsset(sourcePath, destination);
            if (!string.IsNullOrEmpty(moveError))
            {
                return ProjectFileOperationResult.Failure(moveError, sourcePath);
            }

            return ProjectFileOperationResult.Success(destination, sourcePath);
        }

        // A path is about to change owner inside a batch: the batch already took
        // away the asset the frozen database still has there (a Replace
        // displaced it, or its Undo moved the replacement back out), and
        // something else is about to land on it. Left for the batch's closing
        // rescan, Unity gets the swap wrong - probed on 6000.3: undoing a
        // Replace gave the returning replacement a NEW GUID ("GUID conflicts
        // with... Assigning a new guid"), dropping its tags and every reference
        // to it, and redoing one left the database naming the old owner's GUID
        // for the path. Bringing the database up to date first makes the
        // arrival an ordinary move. One refresh per swapped path, which only a
        // Replace and its undo/redo ever pay.
        private void SettleVacatedPath(AssetPath path)
        {
            if (_batchDepth == 0 || !ExistsInDatabase(path) || Exists(path))
            {
                return;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseStopAssetEditingCalls);
            AssetDatabase.StopAssetEditing();
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGlobalRefreshCalls);
            AssetDatabase.Refresh();
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseStartAssetEditingCalls);
            AssetDatabase.StartAssetEditing();
        }

        public ProjectFileOperationResult Copy(
            AssetPath sourcePath,
            AssetPath destinationPath,
            bool allowOverwriteWithUniquePath)
        {
            var destinationFolderPath = AssetPath.From(AssetPathFormat.GetParentFolder(destinationPath));
            if (!destinationFolderPath.IsEmpty && !EnsureFolder(destinationFolderPath))
            {
                return ProjectFileOperationResult.Failure(
                    "Destination folder could not be created.",
                    destinationFolderPath,
                    sourcePath);
            }

            string destination = destinationPath;
            if (allowOverwriteWithUniquePath)
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
                destination = AssetDatabase.GenerateUniqueAssetPath(destination);
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCopyAssetCalls);
            if (!AssetDatabase.CopyAsset(sourcePath, destination))
            {
                return ProjectFileOperationResult.Failure("Failed to duplicate the item.", sourcePath);
            }

            return ProjectFileOperationResult.Success(destination, sourcePath);
        }

        public ProjectFileOperationResult Delete(AssetPath assetPath)
        {
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseDeleteAssetCalls);
            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                return ProjectFileOperationResult.Failure("Failed to delete the item.", assetPath);
            }

            return ProjectFileOperationResult.Success(string.Empty, assetPath);
        }

        public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default)
        {
            return _undoPayloads.Store(assetPath, payloadPath);
        }

        public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath)
        {
            SettleVacatedPath(destinationPath);
            return _undoPayloads.Restore(payloadPath, destinationPath);
        }

        public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath)
        {
            var result = _undoPayloads.Delete(payloadPath);
            if (!result.Succeeded)
                Debug.LogWarning("[Zep] Could not release an undo payload: " + result.ErrorMessage);
            return result;
        }

        public IReadOnlyList<string> GetTagIds(AssetPath assetPath)
        {
            EnsureTagStateReady(checkDisk: false);
            var assetGuid = ResolveAssetGuid(assetPath);
            if (string.IsNullOrWhiteSpace(assetGuid))
            {
                return Array.Empty<string>();
            }

            return ZepTagAssignmentStore.instance.GetTagIds(assetGuid);
        }

        public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid)
        {
            EnsureTagStateReady(checkDisk: false);
            return string.IsNullOrWhiteSpace(knownGuid)
                ? GetTagIds(assetPath)
                : ZepTagAssignmentStore.instance.GetTagIds(knownGuid);
        }

        public ProjectFileOperationResult SetTagIds(
            IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath)
        {
            if (tagIdsByAssetPath == null || tagIdsByAssetPath.Count == 0)
            {
                return ProjectFileOperationResult.Failure("Choose at least one item to tag.");
            }

            var tagIdsByGuid = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            // Reported back as the operation's path so a failure names something
            // the user can see; the first entry stands in for the whole batch.
            var firstAssetPath = default(AssetPath);
            foreach (var entry in tagIdsByAssetPath)
            {
                var assetPath = entry.Key;
                if (firstAssetPath.IsEmpty)
                {
                    firstAssetPath = assetPath;
                }

                if (!CanTag(assetPath))
                {
                    return ProjectFileOperationResult.Failure("This item cannot be tagged.", assetPath);
                }

                var assetGuid = ResolveAssetGuid(assetPath);
                if (string.IsNullOrWhiteSpace(assetGuid))
                {
                    return ProjectFileOperationResult.Failure(
                        "The item could not be identified for tagging.",
                        assetPath);
                }

                tagIdsByGuid[assetGuid] = entry.Value ?? Array.Empty<string>();
            }

            try
            {
                if (!TryRecoverTagStateTransaction(out var recoveryError))
                {
                    return ProjectFileOperationResult.Failure(recoveryError, firstAssetPath);
                }

                ZepTagAssignmentStore.instance.SetTagIds(tagIdsByGuid);
                return ProjectFileOperationResult.Success(firstAssetPath, firstAssetPath);
            }
            catch (Exception ex)
            {
                return ProjectFileOperationResult.Failure(ex.Message, firstAssetPath);
            }
        }

        ProjectFileOperationResult IProjectTagAssignmentPreparationGateway.PrepareTagAssignmentMutation()
        {
            try
            {
                if (!TryRecoverTagStateTransaction(out var recoveryError))
                {
                    return ProjectFileOperationResult.Failure(recoveryError);
                }

                ZepTagAssignmentStore.instance.PrepareForMutation();
                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                return ProjectFileOperationResult.Failure(ex.Message);
            }
        }

        ProjectTagStateSnapshot IProjectTagDefinitionGateway.CaptureTagState()
        {
            return TagTransactionJournal.Capture();
        }

        ProjectFileOperationResult IProjectTagDefinitionGateway.CompareAndSetTagState(
            ProjectTagStateSnapshot expected,
            ProjectTagStateSnapshot desired)
        {
            return TagTransactionJournal.CompareAndSet(expected, desired);
        }

        private void EnsureTagStateReady(bool checkDisk)
        {
            if (!checkDisk
                && _tagStateReady
                && TagTransactionJournal.IsSettled)
            {
                return;
            }

            _tagStateReady = false;
            TagTransactionJournal.RequireRecovery();
            try
            {
                if (!TryRecoverTagStateTransaction(out var error))
                {
                    throw new ProjectTagStateUnavailableException(error);
                }
            }
            catch (IOException ex)
            {
                throw new ProjectTagStateUnavailableException(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ProjectTagStateUnavailableException(ex.Message);
            }

            _tagStateReady = true;
        }

        internal static bool TryRecoverTagStateTransaction(out string error)
        {
            return TagTransactionJournal.TryRecover(out error);
        }

        private static string GetTagTransactionPath()
        {
            return Path.Combine(
                ProjectPaths.GetProjectRootAbsolutePath(),
                "Library",
                "Zep",
                "TagStateTransaction.json");
        }

        public bool Open(AssetPath assetPath)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return mainAsset != null && AssetDatabase.OpenAsset(mainAsset);
        }

        public void Reveal(AssetPath assetPath)
        {
            var absolutePath = ProjectPaths.ToAbsolutePath(assetPath);
            if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
            {
                EditorUtility.RevealInFinder(absolutePath);
            }
        }

        // One seam for both halves of a batch: Unity's importer is paused, and the
        // undo payload store holds its global refresh until the batch closes.
        // Splitting them would let a caller pause imports while still paying one
        // project-wide Refresh per deleted item.
        public void BeginBatch()
        {
            _undoPayloads.BeginBatch();
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseStartAssetEditingCalls);
            AssetDatabase.StartAssetEditing();
            _batchDepth++;
        }

        public void EndBatch()
        {
            // Before StopAssetEditing: that call brings the database up to date,
            // so the callbacks it fires get the database's answer again.
            _batchDepth--;
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseStopAssetEditingCalls);
            AssetDatabase.StopAssetEditing();

            // After StopAssetEditing, so the batch's single refresh is issued
            // against a database that is no longer deferring it.
            _undoPayloads.EndBatch();
        }

        public string GetPathForGuid(string assetGuid)
        {
            if (string.IsNullOrWhiteSpace(assetGuid))
            {
                return string.Empty;
            }

            try
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                return AssetDatabase.GUIDToAssetPath(assetGuid) ?? string.Empty;
            }
            catch (Exception)
            {
                // A malformed GUID is not a corruption the caller must act on:
                // the command falls back to its recorded path and the existing
                // identity check decides whether the target is still valid.
                return string.Empty;
            }
        }

        public string GetGuid(AssetPath assetPath)
        {
            string path = assetPath;
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                var absolutePath = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : ResolveAbsolutePath(assetPath);
                if (string.IsNullOrEmpty(absolutePath))
                {
                    return string.Empty;
                }

                if (Path.IsPathRooted(path))
                {
                    var payloadRoot = Path.GetFullPath(UndoPayloadStore.RootAbsolutePath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    var candidate = Path.GetFullPath(absolutePath);
                    if (!candidate.StartsWith(payloadRoot, StringComparison.Ordinal))
                    {
                        return string.Empty;
                    }
                }

                var diskGuid = UnityMetaFileReader.ReadGuidFromMetaFile(absolutePath + ".meta");
                if (!string.IsNullOrWhiteSpace(diskGuid) || Path.IsPathRooted(path))
                {
                    return diskGuid;
                }

                if (File.Exists(absolutePath) || Directory.Exists(absolutePath) || File.Exists(absolutePath + ".meta"))
                {
                    // A physical entry without a readable GUID is unknown, never the
                    // stale identity Unity may still cache for a replaced path.
                    return string.Empty;
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
            catch (ArgumentException)
            {
            }

            if (AssetPathClassification.IsWritablePath(assetPath)
                || (AssetPathClassification.IsPackagesPath(assetPath) && CanWrite(assetPath)))
            {
                return string.Empty;
            }

            // Some virtual/package assets have no readable physical .meta. Use
            // Unity's cache only as that fallback; for normal files the disk GUID
            // above is authoritative even when AssetDatabase is temporarily stale.
            if (!Path.IsPathRooted(path))
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            }

            return Path.IsPathRooted(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }

        public string ResolveAbsolutePath(AssetPath assetPath)
        {
            return ProjectPaths.ToAbsolutePath(assetPath);
        }

        private string ResolveAssetGuid(string assetPath)
        {
            var normalizedPath = AssetPathFormat.Normalize(assetPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            return GetGuid(AssetPath.From(normalizedPath));
        }
    }
}
