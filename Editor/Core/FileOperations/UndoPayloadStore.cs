using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    /// <summary>
    /// What <see cref="UndoPayloadStore"/> needs from the Unity editor. Implemented
    /// in the Bridge.
    /// </summary>
    public interface IUndoPayloadEditorHost
    {
        string ProjectRootAbsolutePath { get; }

        string ToAbsolutePath(string assetPath);

        bool CanWrite(AssetPath assetPath);

        /// <summary>A value that survives domain reloads but not an editor restart; empty when unset.</summary>
        string GetSessionString(string key);

        void SetSessionString(string key, string value);

        void LogWarning(string message);

        /// <summary>Force-imports the asset, recursively for a folder.</summary>
        void ImportAsset(AssetPath assetPath);

        void RefreshAssetDatabase();
    }

    // Payload slots belong to command history, never to a project-facing collection.
    // Moving the entry and its sibling metadata preserves bytes and Unity identity.
    public sealed class UndoPayloadStore
    {
        // Every Unity dependency is a process-wide editor fact and most of this
        // class is static, so the Bridge binds the host once per domain.
        private static IUndoPayloadEditorHost _editorHost;

        public static void BindUnity(IUndoPayloadEditorHost editorHost)
        {
            _editorHost = editorHost ?? throw new ArgumentNullException(nameof(editorHost));
        }

        private static IUndoPayloadEditorHost EditorHost =>
            _editorHost ?? throw new InvalidOperationException(
                "UndoPayloadStore.BindUnity must run before the store is used.");

        internal static string TestRootOverride;
        internal Action<string> TransactionCheckpointForTests { get; set; }
        internal Func<string, string, bool?> MoveFileSystemEntryOverrideForTests { get; set; }
        internal Func<string, string, bool?> SameFileSystemVolumeOverrideForTests { get; set; }
        private int _batchDepth;
        private bool _refreshPending;

        private const string RecoveryMarker = "recovery-required";
        public const string SessionKey = "Zep.UndoPayloadSession";
        private static string _sessionId;
        private static string SessionId => _sessionId ?? (_sessionId = GetSessionId());
        private static bool _expiredSessionsChecked;

        public static string RootAbsolutePath => string.IsNullOrEmpty(TestRootOverride)
            ? Path.Combine(EditorHost.ProjectRootAbsolutePath, "Library", "Zep", "UndoPayloads", SessionId)
            : TestRootOverride;

        public UndoPayloadStore()
        {
            if (_expiredSessionsChecked || !string.IsNullOrEmpty(TestRootOverride)) return;
            _expiredSessionsChecked = true;
            CleanupExpiredSessions(Path.GetDirectoryName(RootAbsolutePath), SessionId);
        }

        private static string GetSessionId()
        {
            var id = EditorHost.GetSessionString(SessionKey);
            if (!Guid.TryParseExact(id, "N", out _))
            {
                id = Guid.NewGuid().ToString("N");
                EditorHost.SetSessionString(SessionKey, id);
            }
            return id;
        }

        internal static void CleanupExpiredSessions(string root, string activeSessionId)
        {
            try
            {
                ValidateAncestors(root, StorageBoundary);
                if (!Directory.Exists(root)) return;
                foreach (var directory in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    if (name == activeSessionId || !Guid.TryParseExact(name, "N", out _)
                        || File.Exists(Path.Combine(directory, RecoveryMarker))
                        || Directory.Exists(Path.Combine(directory, RecoveryMarker))) continue;
                    ValidateTree(directory);
                    if (IsCompleteSession(directory)) Directory.Delete(directory, true);
                }
            }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            {
                EditorHost.LogWarning("[Zep] Could not release expired undo payloads: " + ex.Message);
            }
        }

        private static bool IsCompleteSession(string directory)
        {
            // An unfamiliar or torn entry is evidence, not disposable cache.
            foreach (var container in Directory.GetFileSystemEntries(directory))
            {
                if (!Directory.Exists(container) || !Guid.TryParseExact(Path.GetFileName(container), "N", out _)) return false;
                var entries = Directory.GetFileSystemEntries(container);
                if (entries.Length == 0) continue;
                if (entries.Length != 2) return false;
                var payload = entries[0].EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? entries[1] : entries[0];
                if (!File.Exists(payload + ".meta") || payload.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public ProjectFileOperationResult Store(AssetPath assetPath, AssetPath payloadPath = default)
        {
            string storagePath = payloadPath;
            try
            {
                var source = ResolveProjectPath(assetPath);
                if (!EntryExists(source)) throw new IOException("The item no longer exists.");
                ValidateTree(source);
                ValidateAncestors(RootAbsolutePath, StorageBoundary);
                if (AssetPathClassification.IsPackagesPath(assetPath)
                    && !OnSameVolume(source, RootAbsolutePath))
                {
                    throw new IOException("Undo storage is unavailable because this package is on a different filesystem volume than the project Library. The item was not changed.");
                }

                if (payloadPath.IsEmpty)
                {
                    storagePath = Path.Combine(RootAbsolutePath, Guid.NewGuid().ToString("N"), Path.GetFileName(source));
                }
                storagePath = ResolveSlot(storagePath);
                if (EntryExists(storagePath) || EntryExists(storagePath + ".meta"))
                    throw new IOException("The original undo storage slot is occupied.");
                Directory.CreateDirectory(Path.GetDirectoryName(storagePath));
                var result = MovePair(source, storagePath, assetPath, AssetPath.From(storagePath));
                if (result.Succeeded) RequestRefresh();
                return result.Succeeded ? result : PreserveFailedPayload(result, AssetPath.From(storagePath));
            }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            {
                return ProjectFileOperationResult.Failure(ex.Message, assetPath, storagePath);
            }
        }

        public ProjectFileOperationResult Restore(AssetPath payloadPath, AssetPath destinationPath)
        {
            string storagePath = null;
            try
            {
                storagePath = ResolveSlot(payloadPath);
                var destination = ResolveProjectPath(destinationPath);
                if (!EntryExists(storagePath)) throw new IOException("The saved undo payload is no longer available.");
                ValidateTree(storagePath);
                if (AssetPathClassification.IsPackagesPath(destinationPath)
                    && !OnSameVolume(storagePath, destination))
                    throw new IOException("This package is on a different filesystem volume than the project Library. The saved item was not changed.");

                if (EntryExists(destination) || EntryExists(destination + ".meta"))
                {
                    // A git checkout or another tool may already have undone the deletion.
                    // Only a complete byte comparison, including all metadata, authorizes
                    // dropping the redundant payload; matching GUIDs alone are insufficient.
                    if (!PairsIdentical(storagePath, destination))
                        throw new IOException("Undo restore conflict: '" + destinationPath + "' already exists with different contents.");
                    var dropped = Delete(payloadPath);
                    return dropped.Succeeded
                        ? ProjectFileOperationResult.Success(destinationPath, payloadPath)
                        : dropped;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                var result = MovePair(storagePath, destination, payloadPath, destinationPath);
                if (result.Succeeded)
                {
                    RemoveEmptyContainer(storagePath);
                    ImportRestoredAsset(destinationPath);
                }
                return result.Succeeded ? result : PreserveFailedPayload(result, payloadPath);
            }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            {
                var failure = ProjectFileOperationResult.Failure(ex.Message, destinationPath, payloadPath);
                return storagePath == null ? failure : PreserveFailedPayload(failure, payloadPath);
            }
        }

        public ProjectFileOperationResult Delete(AssetPath payloadPath)
        {
            try
            {
                var path = ResolveSlot(payloadPath);
                var container = Path.GetDirectoryName(path);
                if (Directory.Exists(container))
                {
                    ValidateTree(container);
                    Directory.Delete(container, true);
                }
                return ProjectFileOperationResult.Success(string.Empty, payloadPath);
            }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            {
                return ProjectFileOperationResult.Failure(ex.Message, payloadPath);
            }
        }

        public void BeginBatch() => _batchDepth++;

        public void EndBatch()
        {
            if (_batchDepth <= 0) throw new InvalidOperationException("No undo payload batch is active.");
            _batchDepth--;
            if (_batchDepth == 0 && _refreshPending) Refresh();
        }

        private ProjectFileOperationResult MovePair(
            string source, string destination, AssetPath sourcePath, AssetPath destinationPath)
        {
            if (!File.Exists(source + ".meta"))
                return ProjectFileOperationResult.Failure("The item's Unity metadata is unavailable.", sourcePath);
            try
            {
                MoveEntry(source, destination);
                TransactionCheckpointForTests?.Invoke("payload-moved");
                MoveEntry(source + ".meta", destination + ".meta");
            }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            {
                // Recover only from the state actually on disk. A failed second move
                // normally rolls the payload back. If rollback itself fails, completing
                // the metadata move is the only coherent success we can report.
                if (EntryExists(destination) && !EntryExists(source))
                {
                    try
                    {
                        MoveEntry(destination, source);
                        if (File.Exists(destination + ".meta") && !File.Exists(source + ".meta"))
                            MoveEntry(destination + ".meta", source + ".meta");
                    }
                    catch (Exception rollbackError) when (IsBoundaryFailure(rollbackError))
                    {
                        try
                        {
                            if (EntryExists(source) && !EntryExists(destination)) MoveEntry(source, destination);
                            if (File.Exists(source + ".meta") && !File.Exists(destination + ".meta"))
                                MoveEntry(source + ".meta", destination + ".meta");
                            if (EntryExists(destination) && File.Exists(destination + ".meta")
                                && !EntryExists(source) && !File.Exists(source + ".meta"))
                                return ProjectFileOperationResult.Success(destinationPath, sourcePath);
                        }
                        catch (Exception recoveryError) when (IsBoundaryFailure(recoveryError)) { }
                        return ProjectFileOperationResult.Failure(
                            "The file operation could not recover its payload and metadata: " + ex.Message,
                            sourcePath, destinationPath);
                    }
                }
                return ProjectFileOperationResult.Failure(ex.Message, sourcePath, destinationPath);
            }
            return ProjectFileOperationResult.Success(destinationPath, sourcePath);
        }

        private void MoveEntry(string source, string destination)
        {
            var overridden = MoveFileSystemEntryOverrideForTests?.Invoke(source, destination);
            if (overridden == false) throw new IOException("The filesystem move was refused.");
            if (overridden == true) return;
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }

        private static string ResolveProjectPath(AssetPath assetPath)
        {
            if (!AssetPathClassification.IsProjectAssetPath(assetPath)
                || AssetPathClassification.IsProjectRootPath(assetPath)
                || AssetPathClassification.IsPackageRootPath(assetPath))
                throw new IOException("Only project items inside Assets or writable Packages can be changed.");
            if (!EditorHost.CanWrite(assetPath))
                throw new IOException("Items in read-only packages cannot be changed.");
            string path = assetPath;
            var packageEnd = AssetPathClassification.IsPackagesPath(assetPath)
                ? path.IndexOf('/', "Packages/".Length) : -1;
            var root = AssetPathClassification.IsPackagesPath(assetPath)
                ? EditorHost.ToAbsolutePath(packageEnd < 0 ? path : path.Substring(0, packageEnd))
                : Path.Combine(EditorHost.ProjectRootAbsolutePath, "Assets");
            var absolute = Path.GetFullPath(EditorHost.ToAbsolutePath(assetPath));
            if (!IsBelow(root, absolute)) throw new IOException("The item resolves outside its project root.");
            ValidateAncestors(absolute, root);
            ValidateAncestors(absolute + ".meta", root);
            return absolute;
        }

        private static string ResolveSlot(string payloadPath)
        {
            if (string.IsNullOrEmpty(payloadPath) || !Path.IsPathRooted(payloadPath))
                throw new IOException("The undo storage slot is invalid.");
            var path = Path.GetFullPath(payloadPath);
            var container = Path.GetDirectoryName(path);
            var root = Path.GetFullPath(RootAbsolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(container), root, StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(container), "N", out _)
                || Path.GetFileName(path).EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The undo storage slot is outside its owned location.");
            ValidateAncestors(path, StorageBoundary);
            ValidateAncestors(path + ".meta", StorageBoundary);
            if (Directory.Exists(container))
            {
                foreach (var entry in Directory.GetFileSystemEntries(container))
                    if (!string.Equals(entry, path, StringComparison.Ordinal)
                        && !string.Equals(entry, path + ".meta", StringComparison.Ordinal))
                        throw new IOException("The undo storage slot contains an unexpected entry.");
            }
            return path;
        }

        private static bool IsBelow(string root, string path) => path.StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        // The configured project/package root is trusted. Reject links inside that
        // boundary without rejecting a project opened through an OS alias such as /tmp.
        private static string StorageBoundary => string.IsNullOrEmpty(TestRootOverride)
            ? EditorHost.ProjectRootAbsolutePath : Path.GetDirectoryName(TestRootOverride);

        private static void ValidateAncestors(string path, string boundary)
        {
            path = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(path);
            if (!string.Equals(path, Path.GetFullPath(boundary), StringComparison.Ordinal)
                && !string.IsNullOrEmpty(parent) && parent != path) ValidateAncestors(parent, boundary);
            try { RefuseLink(path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private static void RefuseLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not accepted for undo payloads: " + path);
        }

        private static void ValidateTree(string path)
        {
            RefuseLink(path);
            if (!Directory.Exists(path)) return;
            foreach (var entry in Directory.GetFileSystemEntries(path)) ValidateTree(entry);
        }

        private static bool PairsIdentical(string left, string right)
        {
            ValidateTree(right);
            return EntriesIdentical(left, right) && EntriesIdentical(left + ".meta", right + ".meta");
        }

        private static bool EntriesIdentical(string left, string right)
        {
            var leftFolder = Directory.Exists(left);
            if (leftFolder != Directory.Exists(right)) return false;
            if (leftFolder)
            {
                var entries = Directory.GetFileSystemEntries(left);
                if (entries.Length != Directory.GetFileSystemEntries(right).Length) return false;
                foreach (var entry in entries)
                    if (!EntriesIdentical(entry, Path.Combine(right, Path.GetFileName(entry)))) return false;
                return true;
            }
            if (!File.Exists(left) || !File.Exists(right)) return false;
            if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
            using (var a = File.OpenRead(left))
            using (var b = File.OpenRead(right))
            {
                var aBuffer = new byte[8192];
                var bBuffer = new byte[8192];
                while (true)
                {
                    var aCount = ReadBlock(a, aBuffer);
                    var bCount = ReadBlock(b, bBuffer);
                    if (aCount != bCount) return false;
                    if (aCount == 0) return true;
                    for (var i = 0; i < aCount; i++) if (aBuffer[i] != bBuffer[i]) return false;
                }
            }
        }

        private static int ReadBlock(Stream stream, byte[] buffer)
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            return count;
        }

        private bool OnSameVolume(string source, string destination)
        {
            var overridden = SameFileSystemVolumeOverrideForTests?.Invoke(source, destination);
            if (overridden.HasValue) return overridden.Value;
            return string.Equals(FindDrive(source), FindDrive(destination), StringComparison.Ordinal);
        }

        private static string FindDrive(string path)
        {
            var best = string.Empty;
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName;
                if ((IsBelow(root, path) || string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.Ordinal))
                    && root.Length > best.Length) best = root;
            }
            if (best.Length == 0) throw new IOException("The filesystem volume could not be identified safely.");
            return best;
        }

        private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);
        private static bool IsBoundaryFailure(Exception ex) => ex is IOException || ex is UnauthorizedAccessException
            || ex is ArgumentException || ex is NotSupportedException;

        private static void RemoveEmptyContainer(string payloadPath)
        {
            try { Directory.Delete(Path.GetDirectoryName(payloadPath), false); }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            { EditorHost.LogWarning("[Zep] Could not clean an empty undo payload slot: " + ex.Message); }
        }

        private static ProjectFileOperationResult PreserveFailedPayload(
            ProjectFileOperationResult failure, AssetPath payloadPath)
        {
            string path;
            try
            {
                path = ResolveSlot(payloadPath);
                if (!EntryExists(path) && !File.Exists(path + ".meta")) return failure;
            }
            catch (Exception ex) when (IsBoundaryFailure(ex)) { return failure; }
            var message = failure.ErrorMessage + " Saved payload: '" + path + "'.";
            try
            {
                var marker = Path.Combine(RootAbsolutePath, RecoveryMarker);
                if (!EntryExists(marker))
                {
                    using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream))
                        writer.Write("A file operation could not restore its saved payload. Preserve this directory for manual recovery.");
                }
            }
            catch (Exception ex) when (IsBoundaryFailure(ex))
            {
                message += " Its recovery marker could not be written; copy the saved payload before closing the editor: " + ex.Message;
            }
            return ProjectFileOperationResult.Failure(message, failure.AssetPath, failure.PreviousPath);
        }

        private static void ImportRestoredAsset(AssetPath assetPath)
        {
            try
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseImportAssetCalls);
                EditorHost.ImportAsset(assetPath);
            }
            catch (Exception ex)
            {
                EditorHost.LogWarning("[Zep] Could not import a restored undo payload: " + ex.Message);
            }
        }

        private void RequestRefresh()
        {
            _refreshPending = true;
            if (_batchDepth == 0) Refresh();
        }

        private void Refresh()
        {
            try
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGlobalRefreshCalls);
                EditorHost.RefreshAssetDatabase();
                _refreshPending = false;
            }
            catch (Exception ex)
            {
                // Filesystem state has committed. Import synchronization cannot turn
                // that into a failure that would omit the matching history record.
                EditorHost.LogWarning("[Zep] Could not refresh after a file operation: " + ex.Message);
            }
        }
    }
}
