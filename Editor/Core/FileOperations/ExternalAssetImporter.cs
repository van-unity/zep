using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;

namespace Zep.Core.FileOperations
{
    /// <summary>
    /// What <see cref="ExternalAssetImporter"/> needs from the Unity editor.
    /// Implemented in the Bridge.
    /// </summary>
    public interface IExternalImportEditorHost
    {
        string ProjectRootAbsolutePath { get; }

        string ToAbsolutePath(string assetPath);

        bool CanWrite(string assetPath);

        bool IsValidFolder(string assetPath);

        /// <summary>Opens Unity's interactive Import Package dialog for a .unitypackage.</summary>
        void ImportPackage(string packageFullPath);

        /// <summary>Pauses Unity's importer until <see cref="StopAssetEditing"/>.</summary>
        void StartAssetEditing();

        void StopAssetEditing();

        /// <summary>Imports one written path; <paramref name="recursive"/> for a folder.</summary>
        void ImportAsset(string assetPath, bool recursive);

        void LogWarning(string message);
    }

    // WHY: External-drag imports run a small pipeline — validate raw drag
    // payload paths into in-project full paths, pick a non-colliding
    // destination name, copy the file or directory tree, then ask the
    // AssetDatabase to import the freshly-written paths. Splitting this
    // out of the editor window keeps the window as composition glue and
    // gives the pipeline a typed result so callers can react to
    // partial failures instead of fishing log lines.
    public static class ExternalAssetImporter
    {
        private static IExternalImportEditorHost _editorHost;

        public static void BindUnity(IExternalImportEditorHost editorHost)
        {
            _editorHost = editorHost ?? throw new ArgumentNullException(nameof(editorHost));
        }

        private static IExternalImportEditorHost EditorHost =>
            _editorHost ?? throw new InvalidOperationException(
                "ExternalAssetImporter.BindUnity must run before an import.");

        private const int MaxDedupSuffix = 10000;

        internal static Action<string> CheckpointForTests { get; set; }

        // Counts one filesystem existence probe per collision candidate. The
        // probe count is the behaviour under test for a large same-named drop,
        // and it cannot be observed any other way.
        internal static Action ExistenceProbeForTests { get; set; }

        private sealed class ImportPlan
        {
            internal string SourceFullPath;
            internal string TargetFullPath;
            internal string TargetAssetPath;
            internal bool IsDirectory;
        }

        // WHY: Drag payloads can include malformed paths, project-internal
        // paths, and paths that don't exist on disk. Filtering happens
        // here rather than in the window so the policy lives with the
        // rest of the import pipeline.
        public static List<string> CollectExternalPaths(IReadOnlyList<string> rawPaths, string projectRootFullPath)
        {
            var result = new List<string>();
            AppendExternalPaths(rawPaths, projectRootFullPath, result);
            return result;
        }

        public static void AppendExternalPaths(IReadOnlyList<string> rawPaths, string projectRootFullPath, List<string> destination)
        {
            if (rawPaths == null)
            {
                return;
            }

            for (var i = 0; i < rawPaths.Count; i++)
            {
                AppendIfExternal(rawPaths[i], projectRootFullPath, destination);
            }
        }

        private static void AppendIfExternal(string raw, string projectRootFullPath, List<string> destination)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(raw).Replace('\\', '/').TrimEnd('/');
            }
            catch (ArgumentException) // tolerated: drag payload contained an invalid/malformed path string; skip and keep processing other dragged items.
            {
                return;
            }
            catch (NotSupportedException) // tolerated: path contained an unsupported character (e.g. embedded colon on Windows); skip this item.
            {
                return;
            }
            catch (PathTooLongException) // tolerated: dragged path exceeds the OS limit; skip rather than abort the whole drop.
            {
                return;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return;
            }

            var isInsideProject = fullPath.StartsWith(projectRootFullPath, StringComparison.OrdinalIgnoreCase)
                && (fullPath.Length == projectRootFullPath.Length || fullPath[projectRootFullPath.Length] == '/');
            if (isInsideProject)
            {
                return;
            }

            destination.Add(fullPath);
        }

        // WHY: A dropped .unitypackage is not a raw file to copy into a folder —
        // it launches Unity's interactive Import Package dialog, matching what
        // the native Project window does. Every .unitypackage in the drop is
        // handed to the importer; the remaining paths (to be copied in as raw
        // assets) are returned so the caller continues its normal copy pass.
        // Package import ignores the drop's destination folder, so callers route
        // packages before any destination write-permission check.
        public static List<string> ImportDroppedUnityPackages(IReadOnlyList<string> externalFullPaths)
        {
            var remaining = new List<string>();
            if (externalFullPaths == null)
            {
                return remaining;
            }

            for (var i = 0; i < externalFullPaths.Count; i++)
            {
                var path = externalFullPaths[i];
                if (IsUnityPackage(path))
                {
                    EditorHost.ImportPackage(path);
                }
                else if (!string.IsNullOrEmpty(path))
                {
                    remaining.Add(path);
                }
            }

            return remaining;
        }

        // Classifies a dropped path as a .unitypackage by its full extension, so
        // "foo.unitypackage.bak" is correctly excluded. Case-insensitive.
        internal static bool IsUnityPackage(string fullPath)
        {
            return !string.IsNullOrEmpty(fullPath)
                && fullPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);
        }

        public static ExternalAssetImportResult Import(
            IReadOnlyList<string> sourceFullPaths,
            string destinationFolderAssetPath)
        {
            if (sourceFullPaths == null || sourceFullPaths.Count == 0)
            {
                return ExternalAssetImportResult.Empty;
            }

            var importedAssetPaths = new List<string>(sourceFullPaths.Count);
            // The plans that actually reached the project. The targeted import
            // below needs each one's kind, which the commit loop already had -
            // recovering it by scanning the plan list cost k^2 comparisons.
            var importedPlans = new List<ImportPlan>(sourceFullPaths.Count);
            var failures = new List<(string sourcePath, string reason)>();
            var plans = Preflight(
                sourceFullPaths,
                destinationFolderAssetPath,
                failures);

            // All accepted top-level entries have been traversed and validated
            // before this first write. Each is then copied under one Unity-
            // ignored sibling name and committed with a same-volume rename.
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                var stageFullPath = Path.Combine(
                    Path.GetDirectoryName(plan.TargetFullPath) ?? string.Empty,
                    ".zep-import-" + Guid.NewGuid().ToString("N") + "~");
                try
                {
                    using (ZepPerfLog.Measure(
                        "external-import-copy",
                        ZepPerfStage.NativeCopy))
                    {
                        CheckpointForTests?.Invoke("before-copy:" + plan.SourceFullPath);
                        if (plan.IsDirectory)
                        {
                            FilesystemEntryOperations.CopyDirectoryRecursive(
                                plan.SourceFullPath,
                                stageFullPath);
                        }
                        else
                        {
                            File.Copy(plan.SourceFullPath, stageFullPath, false);
                        }
                        CheckpointForTests?.Invoke("after-copy:" + plan.SourceFullPath);
                    }

                    CheckpointForTests?.Invoke("before-commit:" + plan.SourceFullPath);
                    if (plan.IsDirectory)
                    {
                        Directory.Move(stageFullPath, plan.TargetFullPath);
                    }
                    else
                    {
                        File.Move(stageFullPath, plan.TargetFullPath);
                    }
                    importedAssetPaths.Add(plan.TargetAssetPath);
                    importedPlans.Add(plan);
                }
                catch (Exception exception) when (
                    exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is NotSupportedException)
                {
                    failures.Add((plan.SourceFullPath, exception.Message));
                    CleanupStage(stageFullPath);
                }
            }

            if (importedAssetPaths.Count == 0)
            {
                return new ExternalAssetImportResult(importedAssetPaths, failures);
            }

            using (ZepPerfLog.Measure(
                "external-import-targeted-imports",
                ZepPerfStage.TargetedImportGroup))
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseStartAssetEditingCalls);
                EditorHost.StartAssetEditing();
                try
                {
                    for (var i = 0; i < importedPlans.Count; i++)
                    {
                        var plan = importedPlans[i];
                        try
                        {
                            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseImportAssetCalls);
                            EditorHost.ImportAsset(plan.TargetAssetPath, plan.IsDirectory);
                        }
                        catch (Exception exception) when (
                            exception is ArgumentException
                            || exception is InvalidOperationException)
                        {
                            failures.Add((
                                plan.SourceFullPath,
                                "targeted import failed: " + exception.Message));
                        }
                    }
                }
                finally
                {
                    ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseStopAssetEditingCalls);
                    EditorHost.StopAssetEditing();
                }
            }

            return new ExternalAssetImportResult(importedAssetPaths, failures);
        }

        private static List<ImportPlan> Preflight(
            IReadOnlyList<string> sourceFullPaths,
            string destinationFolderAssetPath,
            List<(string sourcePath, string reason)> failures)
        {
            var plans = new List<ImportPlan>(sourceFullPaths.Count);
            var destinationAssetPath = AssetPathFormat.Normalize(destinationFolderAssetPath);
            if (!EditorHost.CanWrite(destinationAssetPath)
                || !EditorHost.IsValidFolder(destinationAssetPath))
            {
                for (var i = 0; i < sourceFullPaths.Count; i++)
                {
                    failures.Add((sourceFullPaths[i], "destination is not a writable asset folder"));
                }
                return plans;
            }

            string destinationFullPath;
            try
            {
                destinationFullPath = NormalizeFullPath(
                    EditorHost.ToAbsolutePath(destinationAssetPath));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException)
            {
                for (var i = 0; i < sourceFullPaths.Count; i++)
                {
                    failures.Add((sourceFullPaths[i], exception.Message));
                }
                return plans;
            }

            if (!Directory.Exists(destinationFullPath))
            {
                for (var i = 0; i < sourceFullPaths.Count; i++)
                {
                    failures.Add((sourceFullPaths[i], "destination directory does not exist"));
                }
                return plans;
            }

            var reservedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Where the next probe for a given dropped name starts. Without it,
            // dropping k files that share a name re-walks every suffix already
            // taken - two syscalls per step, k^2/2 steps for the whole drop.
            var nextSuffixByEntryName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var projectRootFullPath = NormalizeFullPath(
                EditorHost.ProjectRootAbsolutePath);
            for (var i = 0; i < sourceFullPaths.Count; i++)
            {
                var source = sourceFullPaths[i];
                try
                {
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        failures.Add((source, "source path is empty"));
                        continue;
                    }

                    var sourceFullPath = NormalizeFullPath(source);
                    var isDirectory = Directory.Exists(sourceFullPath);
                    if (!isDirectory && !File.Exists(sourceFullPath))
                    {
                        failures.Add((source, "source path does not exist"));
                        continue;
                    }
                    if (IsSameOrChild(sourceFullPath, destinationFullPath))
                    {
                        failures.Add((source, "source cannot be inside the destination"));
                        continue;
                    }
                    if (IsSameOrChild(sourceFullPath, projectRootFullPath))
                    {
                        failures.Add((source, "source is already inside the Unity project"));
                        continue;
                    }

                    FilesystemEntryOperations.ValidateImportTree(sourceFullPath, isDirectory);
                    var entryName = isDirectory
                        ? new DirectoryInfo(sourceFullPath).Name
                        : Path.GetFileName(sourceFullPath);
                    var targetFullPath = ResolveNonCollidingDestination(
                        destinationFullPath,
                        entryName,
                        isDirectory,
                        reservedTargets,
                        nextSuffixByEntryName);
                    if (string.IsNullOrEmpty(targetFullPath))
                    {
                        failures.Add((source, "could not allocate a collision-free destination"));
                        continue;
                    }

                    reservedTargets.Add(targetFullPath);
                    plans.Add(new ImportPlan
                    {
                        SourceFullPath = sourceFullPath,
                        TargetFullPath = targetFullPath,
                        TargetAssetPath = destinationAssetPath.TrimEnd('/')
                            + "/" + Path.GetFileName(targetFullPath),
                        IsDirectory = isDirectory,
                    });
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    || exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    failures.Add((source, exception.Message));
                }
            }

            return plans;
        }

        private static string ResolveNonCollidingDestination(
            string destinationFullPath,
            string entryName,
            bool isDirectory,
            HashSet<string> reservedTargets,
            Dictionary<string, int> nextSuffixByEntryName)
        {
            if (string.IsNullOrEmpty(entryName))
            {
                return string.Empty;
            }

            var baseName = isDirectory ? entryName : Path.GetFileNameWithoutExtension(entryName);
            var extension = isDirectory ? string.Empty : Path.GetExtension(entryName);
            // Every suffix below the high-water mark is taken - by this drop or
            // by what was already on disk when it first walked past them.
            var firstSuffix = nextSuffixByEntryName.TryGetValue(entryName, out var known)
                ? known
                : 0;
            for (var suffix = firstSuffix; suffix < MaxDedupSuffix; suffix++)
            {
                var candidateName = suffix == 0
                    ? entryName
                    : baseName + " " + suffix + extension;
                var candidate = Path.Combine(destinationFullPath, candidateName);
                ExistenceProbeForTests?.Invoke();
                if (!reservedTargets.Contains(candidate)
                    && !File.Exists(candidate)
                    && !Directory.Exists(candidate))
                {
                    nextSuffixByEntryName[entryName] = suffix + 1;
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static string NormalizeFullPath(string path)
            => Path.GetFullPath(path ?? string.Empty)
                .Replace('\\', '/')
                .TrimEnd('/');

        private static bool IsSameOrChild(string path, string parent)
            => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
                || (path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    && path.Length > parent.Length
                    && path[parent.Length] == '/');

        private static void CleanupStage(string stageFullPath)
        {
            try
            {
                if (Directory.Exists(stageFullPath))
                {
                    Directory.Delete(stageFullPath, recursive: true);
                }
                else if (File.Exists(stageFullPath))
                {
                    File.Delete(stageFullPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                EditorHost.LogWarning(
                    "[Zep] Failed to clean external-import staging path '"
                    + stageFullPath + "': " + exception.Message);
            }
        }
    }

    public readonly struct ExternalAssetImportResult
    {
        public static readonly ExternalAssetImportResult Empty = new ExternalAssetImportResult(
            Array.Empty<string>(),
            Array.Empty<(string, string)>());

        internal ExternalAssetImportResult(
            IReadOnlyList<string> importedAssetPaths,
            IReadOnlyList<(string sourcePath, string reason)> failures)
        {
            ImportedAssetPaths = importedAssetPaths ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<(string, string)>();
        }

        public IReadOnlyList<string> ImportedAssetPaths { get; }
        public IReadOnlyList<(string sourcePath, string reason)> Failures { get; }
    }
}
