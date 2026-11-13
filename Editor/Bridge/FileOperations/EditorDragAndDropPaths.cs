using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.FileOperations;
using UnityEditor;

namespace Zep.Editor.Bridge.FileOperations
{
    public static class EditorDragAndDropPaths
    {
        // Within a single drag DragAndDrop.paths is fixed, so the drop-target handlers
        // (every DragUpdated, then DragPerform — and the IMGUI importer's OnGUI, which
        // repaints continuously) would otherwise re-run the full classification on
        // every hover frame, including a Path.GetFullPath + File.Exists/Directory.Exists
        // per external path. Classification is a pure function of the path set, so
        // memoize per set: a CONTENT key (not the array reference — DragAndDrop.paths
        // may hand back a fresh array each get) is rebuilt when the drag's paths change,
        // and an empty path set (drag ended) drops the cache.
        //
        // ReadProjectAssetPaths hands back the memo itself, read-only: the hover
        // handlers call it once per drag frame and only read it, and every drop event
        // it feeds is typed IReadOnlyList<string>. A caller that keeps the payload
        // beyond the event (the DragPerform sites) takes CopyProjectAssetPaths.
        private static string[] _cachedKeyPaths;
        private static List<string> _cachedProjectPaths;
        private static List<string> _cachedExternalPaths;

        // Bumped whenever the memo is invalidated (a new path set, or the drag
        // ending), so hover handlers can memoize their own per-payload verdicts
        // against it. Starts at 1 so a zero-initialised consumer field never
        // looks like a valid key.
        private static int _payloadGeneration = 1;

        internal static int PayloadGeneration => _payloadGeneration;

        internal static IReadOnlyList<string> ReadProjectAssetPaths()
        {
            var draggedPaths = DragAndDrop.paths;
            if (draggedPaths == null || draggedPaths.Length == 0)
            {
                ResetClassificationCache();
                return Array.Empty<string>();
            }

            EnsureCacheKey(draggedPaths);
            _cachedProjectPaths ??= ClassifyProjectAssetPaths(draggedPaths);
            return _cachedProjectPaths;
        }

        // For the drop sites: the panel keeps the dropped payload past the event
        // (undo records, deferred file operations), so it gets a private list
        // instead of the memo the hover path shares.
        internal static List<string> CopyProjectAssetPaths()
        {
            return new List<string>(ReadProjectAssetPaths());
        }

        internal static List<string> ReadExternalPaths()
        {
            var draggedPaths = DragAndDrop.paths;
            if (draggedPaths == null || draggedPaths.Length == 0)
            {
                ResetClassificationCache();
                return new List<string>();
            }

            EnsureCacheKey(draggedPaths);
            _cachedExternalPaths ??= ClassifyExternalPaths(draggedPaths);
            return new List<string>(_cachedExternalPaths);
        }

        private static void EnsureCacheKey(string[] draggedPaths)
        {
            if (CachedKeyMatches(draggedPaths))
            {
                return;
            }

            _cachedKeyPaths = (string[])draggedPaths.Clone();
            _cachedProjectPaths = null;
            _cachedExternalPaths = null;
            _payloadGeneration++;
        }

        private static bool CachedKeyMatches(string[] draggedPaths)
        {
            var cachedKey = _cachedKeyPaths;
            if (cachedKey == null || cachedKey.Length != draggedPaths.Length)
            {
                return false;
            }

            for (var i = 0; i < draggedPaths.Length; i++)
            {
                if (!string.Equals(cachedKey[i], draggedPaths[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static void ResetClassificationCache()
        {
            if (_cachedKeyPaths == null)
            {
                // Already empty: an empty payload is read on every frame of a
                // drag that carries no paths (a Finder drag before perform, a
                // Hierarchy object), and bumping the generation there would
                // defeat the consumers' per-payload memos.
                return;
            }

            _cachedKeyPaths = null;
            _cachedProjectPaths = null;
            _cachedExternalPaths = null;
            _payloadGeneration++;
        }

        private static List<string> ClassifyProjectAssetPaths(string[] draggedPaths)
        {
            var projectPaths = new List<string>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < draggedPaths.Length; i++)
            {
                var assetPath = AssetPathFormat.Normalize(draggedPaths[i]);
                if (!string.IsNullOrEmpty(assetPath)
                    && AssetPathClassification.IsProjectAssetPath(assetPath)
                    && seenPaths.Add(assetPath))
                {
                    projectPaths.Add(assetPath);
                }
            }

            return projectPaths;
        }

        private static List<string> ClassifyExternalPaths(string[] draggedPaths)
        {
            var externalPaths = new List<string>();
            // The project root is Application.dataPath's parent, not the process
            // working directory: the rest of the package derives it that way, and
            // the two disagree whenever anything has changed the CWD.
            var projectRootFullPath = NormalizeFullPath(
                ProjectPaths.GetProjectRootAbsolutePath());
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < draggedPaths.Length; i++)
            {
                var draggedPath = draggedPaths[i];
                if (string.IsNullOrEmpty(draggedPath))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = NormalizeFullPath(Path.GetFullPath(draggedPath));
                }
                catch (ArgumentException)
                {
                    // Not a path this platform can resolve — it cannot be an
                    // external file, so drop it rather than failing the drag.
                    continue;
                }
                catch (NotSupportedException)
                {
                    continue;
                }
                catch (PathTooLongException)
                {
                    continue;
                }

                if ((File.Exists(fullPath) || Directory.Exists(fullPath))
                    && !IsPathInsideProject(fullPath, projectRootFullPath)
                    && seenPaths.Add(fullPath))
                {
                    externalPaths.Add(fullPath);
                }
            }

            return externalPaths;
        }

        internal static string NormalizeFullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var normalizedPath = path.Replace('\\', '/');
            return normalizedPath.Length > 1
                && normalizedPath.EndsWith("/", StringComparison.Ordinal)
                    ? normalizedPath.Substring(0, normalizedPath.Length - 1)
                    : normalizedPath;
        }

        private static bool IsPathInsideProject(string fullPath, string projectRootFullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(projectRootFullPath))
            {
                return false;
            }

            if (!fullPath.StartsWith(projectRootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A prefix match is not containment: "<root>Backup" starts with the
            // root but sits beside it, not inside it.
            return fullPath.Length == projectRootFullPath.Length
                || fullPath[projectRootFullPath.Length] == '/';
        }
    }
}
