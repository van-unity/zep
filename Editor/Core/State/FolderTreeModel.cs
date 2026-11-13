using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    /// <summary>
    /// One sidebar folder tree without its rows: the folders materialized so
    /// far under a root, which of them are expanded, and the flat top-to-bottom
    /// order that results. Folders are pulled lazily through the two queries;
    /// nothing here knows about list views, scrolling or painting.
    /// </summary>
    public sealed class FolderTreeModel
    {
        private readonly string _rootPath;
        // Pre-allocated "<rootPath>/" prefix so BelongsToRoot doesn't allocate
        // a new string per entry when filtering ~4,400 folders per change.
        private readonly string _rootPathSlashPrefix;
        private readonly Func<string, IReadOnlyList<ZepFolderEntry>> _folderChildrenQuery;
        private readonly Func<string, ZepFolderEntry> _folderEntryQuery;

        // The materialized hierarchy IS the tree's model: _materializedByPath
        // owns identity, _folderChildren owns structure, and the visible
        // projection is built from them. A flat mirror of the whole folder
        // list would only be a second thing to keep in step.
        private readonly Dictionary<string, ZepFolderEntry> _materializedByPath =
            new Dictionary<string, ZepFolderEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ZepFolderEntry> _visibleFolders = new List<ZepFolderEntry>();
        // Path -> index into _visibleFolders, written by the only two places that
        // write _visibleFolders. Keyboard navigation resolves a row up to 13 times
        // per keypress and the reveal-scroll retry once per panel tick, each of
        // which was a full walk of a few thousand visible rows.
        private readonly Dictionary<string, int> _visibleFolderIndexByPath =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ZepFolderEntry>> _folderChildren = new Dictionary<string, List<ZepFolderEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loadedParentPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The single, sticky expansion state — Unity Project window semantics:
        // only the chevron toggles membership, selecting a folder never
        // expands or collapses anything, and navigating to a folder reveals
        // it by persistently expanding its ancestors. Stale paths of deleted
        // folders linger harmlessly (no chevron renders for them) and
        // re-attach if the path returns.
        private readonly HashSet<string> _expandedFolderPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isInvalidatingCatalogQueries;

        /// <param name="folderChildrenQuery">The direct child folders of a path; the empty path asks for the root itself.</param>
        /// <param name="folderEntryQuery">
        /// Optional refinement, not a second mode: with it a lineage materializes
        /// one entry at a time, without it each ancestor's whole child bucket is
        /// loaded through the children query, which yields the same hierarchy.
        /// </param>
        /// <param name="savedExpansionState">What <see cref="SerializeExpansionState"/> returned earlier this editor session, or empty.</param>
        public FolderTreeModel(
            string rootPath,
            Func<string, IReadOnlyList<ZepFolderEntry>> folderChildrenQuery,
            Func<string, ZepFolderEntry> folderEntryQuery,
            string savedExpansionState)
        {
            _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
            _rootPathSlashPrefix = _rootPath + "/";
            _folderChildrenQuery = folderChildrenQuery
                ?? throw new ArgumentNullException(nameof(folderChildrenQuery));
            _folderEntryQuery = folderEntryQuery;

            RestoredFromSavedState = !string.IsNullOrEmpty(savedExpansionState);
            if (!RestoredFromSavedState)
            {
                // First open this editor session: start with the root
                // expanded, like Unity's Assets/Packages foldouts.
                _expandedFolderPaths.Add(_rootPath);
                return;
            }

            foreach (var path in savedExpansionState.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _expandedFolderPaths.Add(path);
            }
        }

        public string RootPath => _rootPath;

        public bool HasFolderEntryQuery => _folderEntryQuery != null;

        public bool RestoredFromSavedState { get; }

        /// <summary>The rows of the tree, top to bottom, as of the last <see cref="RebuildVisibleFolders"/>.</summary>
        public IReadOnlyList<ZepFolderEntry> VisibleFolders => _visibleFolders;

        public bool TryGetMaterialized(string folderPath, out ZepFolderEntry entry)
        {
            return _materializedByPath.TryGetValue(folderPath, out entry);
        }

        public bool IsParentLoaded(string parentPath)
        {
            return _loadedParentPaths.Contains(parentPath);
        }

        /// <summary>The first child in display order. Only valid when <see cref="FolderHasChildren"/> is true.</summary>
        public string GetFirstChildPath(string folderPath)
        {
            return _folderChildren[folderPath][0].Path;
        }

        /// <summary>Adds or removes one folder from the expanded set; false when it already was that way.</summary>
        public bool SetExpandedFlag(string folderPath, bool expanded)
        {
            return expanded
                ? _expandedFolderPaths.Add(folderPath)
                : _expandedFolderPaths.Remove(folderPath);
        }

        // Leading '\n' keeps a deliberately-emptied set distinguishable from an
        // unset value even if the store treats an empty string as absent; the
        // constructor's RemoveEmptyEntries drops it on load.
        public string SerializeExpansionState()
        {
            return "\n" + string.Join("\n", _expandedFolderPaths);
        }

        /// <summary>
        /// Drops everything materialized and loads it again for the two given
        /// lineages and every expanded branch. When a query fails the previous
        /// hierarchy is put back; false means tag state was unavailable and the
        /// caller should keep showing what it has, any other failure is rethrown.
        /// </summary>
        public bool TryReloadCatalog(string currentFolderPath, string selectedFolderPath)
        {
            var materialized = new Dictionary<string, ZepFolderEntry>(
                _materializedByPath,
                StringComparer.OrdinalIgnoreCase);
            var children = new Dictionary<string, List<ZepFolderEntry>>(
                _folderChildren,
                StringComparer.OrdinalIgnoreCase);
            var loadedParents = new HashSet<string>(
                _loadedParentPaths,
                StringComparer.OrdinalIgnoreCase);
            _materializedByPath.Clear();
            _folderChildren.Clear();
            _loadedParentPaths.Clear();
            _isInvalidatingCatalogQueries = true;
            try
            {
                EnsureChildrenLoaded(string.Empty);
                EnsureLineageMaterialized(currentFolderPath);
                EnsureLineageMaterialized(selectedFolderPath);
                LoadExpandedDescendants(_rootPath);
            }
            catch (Exception exception)
            {
                _materializedByPath.Clear();
                _folderChildren.Clear();
                _loadedParentPaths.Clear();
                foreach (var pair in materialized)
                {
                    _materializedByPath.Add(pair.Key, pair.Value);
                }

                foreach (var pair in children)
                {
                    _folderChildren.Add(pair.Key, pair.Value);
                }

                _loadedParentPaths.UnionWith(loadedParents);
                if (exception is ProjectTagStateUnavailableException)
                {
                    return false;
                }

                throw;
            }
            finally
            {
                _isInvalidatingCatalogQueries = false;
            }

            return true;
        }

        public string ScopeToRoot(string path)
        {
            var normalized = AssetPathFormat.Normalize(path);
            return BelongsToRoot(normalized) ? normalized : string.Empty;
        }

        public bool BelongsToRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return string.Equals(path, _rootPath, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(_rootPathSlashPrefix, StringComparison.OrdinalIgnoreCase);
        }

        public bool EnsureLineageMaterialized(string folderPath)
        {
            if (!BelongsToRoot(folderPath))
            {
                return false;
            }

            var lineage = new List<string>();
            var cursor = folderPath;
            while (BelongsToRoot(cursor))
            {
                lineage.Add(cursor);
                cursor = AssetPathFormat.GetParentFolderPath(cursor);
            }

            var changed = false;
            for (var i = lineage.Count - 1; i >= 0; i--)
            {
                if (_folderEntryQuery != null)
                {
                    changed |= EnsureFolderMaterialized(lineage[i]);
                }
                else
                {
                    var parent = AssetPathFormat.GetParentFolderPath(lineage[i]);
                    changed |= EnsureChildrenLoaded(parent);
                }
            }

            return changed;
        }

        private bool EnsureFolderMaterialized(string folderPath)
        {
            if (_materializedByPath.ContainsKey(folderPath))
            {
                return false;
            }

            ZepFolderEntry entry;
            try
            {
                entry = _folderEntryQuery(folderPath);
            }
            catch (ProjectTagStateUnavailableException)
            {
                if (_isInvalidatingCatalogQueries)
                {
                    throw;
                }

                return false;
            }

            if (entry == null || !BelongsToRoot(entry.Path))
            {
                return false;
            }

            _materializedByPath[entry.Path] = entry;
            var parentPath = AssetPathFormat.GetParentFolderPath(entry.Path);
            if (!_folderChildren.TryGetValue(parentPath, out var siblings))
            {
                siblings = new List<ZepFolderEntry>();
                _folderChildren[parentPath] = siblings;
            }

            siblings.Add(entry);
            siblings.Sort(CompareFolderEntries);
            return true;
        }

        public bool LoadExpandedDescendants(string folderPath)
        {
            if (!IsFolderExpanded(folderPath))
            {
                return false;
            }

            var changed = EnsureChildrenLoaded(folderPath);
            if (!_folderChildren.TryGetValue(folderPath, out var children))
            {
                return changed;
            }

            for (var i = 0; i < children.Count; i++)
            {
                changed |= LoadExpandedDescendants(children[i].Path);
            }

            return changed;
        }

        public bool EnsureChildrenLoaded(string parentPath)
        {
            parentPath ??= string.Empty;
            if (_loadedParentPaths.Contains(parentPath))
            {
                return false;
            }

            if (!TryReloadChildren(parentPath, out _))
            {
                return false;
            }

            _loadedParentPaths.Add(parentPath);
            return true;
        }

        public bool ReloadChildren(string parentPath)
        {
            return TryReloadChildren(parentPath, out var changed) && changed;
        }

        private bool TryReloadChildren(string parentPath, out bool changed)
        {
            parentPath ??= string.Empty;
            IReadOnlyList<ZepFolderEntry> queried;
            try
            {
                queried = _folderChildrenQuery(parentPath)
                    ?? Array.Empty<ZepFolderEntry>();
            }
            catch (ProjectTagStateUnavailableException)
            {
                if (_isInvalidatingCatalogQueries)
                {
                    throw;
                }

                changed = false;
                return false;
            }

            var byPath = new Dictionary<string, ZepFolderEntry>(
                StringComparer.OrdinalIgnoreCase);
            var nextChildren = new List<ZepFolderEntry>(queried.Count);
            for (var i = 0; i < queried.Count; i++)
            {
                var entry = queried[i];
                if (entry != null
                    && BelongsToRoot(entry.Path)
                    && !byPath.ContainsKey(entry.Path))
                {
                    byPath[entry.Path] = entry;
                    nextChildren.Add(entry);
                }
            }

            changed = false;
            if (_folderChildren.TryGetValue(parentPath, out var previousChildren))
            {
                for (var i = previousChildren.Count - 1; i >= 0; i--)
                {
                    var entry = previousChildren[i];
                    if (byPath.ContainsKey(entry.Path))
                    {
                        continue;
                    }

                    RemoveMaterializedSubtree(entry.Path);
                    changed = true;
                }
            }

            for (var i = 0; i < nextChildren.Count; i++)
            {
                var entry = nextChildren[i];
                if (!_materializedByPath.TryGetValue(entry.Path, out var existing)
                    || !FolderEntriesEqual(existing, entry))
                {
                    _materializedByPath[entry.Path] = entry;
                    changed = true;
                }
            }

            _folderChildren[parentPath] = nextChildren;
            _loadedParentPaths.Add(parentPath);
            return true;
        }

        // Walks down the child buckets rather than scanning every materialized
        // folder for the path prefix: a delete that drops several siblings used
        // to re-scan the whole tree once per dropped sibling. The caller owns
        // the link from the removed root to ITS parent bucket (ReloadChildren
        // overwrites that bucket wholesale).
        private void RemoveMaterializedSubtree(string rootPath)
        {
            if (_folderChildren.TryGetValue(rootPath, out var children))
            {
                for (var i = 0; i < children.Count; i++)
                {
                    RemoveMaterializedSubtree(children[i].Path);
                }

                _folderChildren.Remove(rootPath);
            }

            _loadedParentPaths.Remove(rootPath);
            _materializedByPath.Remove(rootPath);
        }

        private static bool FolderEntriesEqual(
            ZepFolderEntry left,
            ZepFolderEntry right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(left.Path, right.Path, StringComparison.Ordinal)
                || left.Depth != right.Depth
                || left.IsReadOnly != right.IsReadOnly
                || !string.Equals(left.Guid, right.Guid, StringComparison.Ordinal)
                || left.UnityLabels.Count != right.UnityLabels.Count)
            {
                return false;
            }

            for (var i = 0; i < left.UnityLabels.Count; i++)
            {
                if (!string.Equals(
                    left.UnityLabels[i],
                    right.UnityLabels[i],
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static int CompareFolderEntries(
            ZepFolderEntry left,
            ZepFolderEntry right)
        {
            var byName = string.Compare(
                left.Name,
                right.Name,
                StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(
                    left.Path,
                    right.Path,
                    StringComparison.OrdinalIgnoreCase);
        }

        public bool IsFolderExpanded(string folderPath)
        {
            return _expandedFolderPaths.Contains(folderPath);
        }

        public bool ExpandAncestors(string folderPath)
        {
            var expandedAny = false;
            var parent = AssetPathFormat.GetParentFolderPath(folderPath);
            while (BelongsToRoot(parent))
            {
                expandedAny |= _expandedFolderPaths.Add(parent);
                parent = AssetPathFormat.GetParentFolderPath(parent);
            }

            return expandedAny;
        }

        public bool FolderHasChildren(string folderPath)
        {
            return _folderChildren.TryGetValue(folderPath, out var children) && children.Count > 0;
        }

        public void RebuildVisibleFolders()
        {
            _visibleFolders.Clear();
            _visibleFolderIndexByPath.Clear();

            // Every materialized entry sits under _rootPath, so the only
            // parentless entry is the root itself.
            if (_folderChildren.TryGetValue(string.Empty, out var roots))
            {
                for (var i = 0; i < roots.Count; i++)
                {
                    AddVisibleFolderRecursive(roots[i]);
                }
            }
        }

        private void AddVisibleFolderRecursive(ZepFolderEntry folder)
        {
            _visibleFolders.Add(folder);
            // First occurrence wins, exactly like the scan this map replaces.
            if (!string.IsNullOrEmpty(folder.Path)
                && !_visibleFolderIndexByPath.ContainsKey(folder.Path))
            {
                _visibleFolderIndexByPath[folder.Path] = _visibleFolders.Count - 1;
            }

            if (!IsFolderExpanded(folder.Path))
            {
                return;
            }

            if (!_folderChildren.TryGetValue(folder.Path, out var children))
            {
                return;
            }

            for (var i = 0; i < children.Count; i++)
            {
                AddVisibleFolderRecursive(children[i]);
            }
        }

        // The roots bucket has the empty key and is the projection's own
        // source, so a change there is always on screen.
        public bool IsParentVisible(string parentPath)
        {
            return string.IsNullOrEmpty(parentPath)
                || _visibleFolderIndexByPath.ContainsKey(parentPath);
        }

        // Reads the map RebuildVisibleFolders writes: this runs per RefreshList
        // (each sidebar Apply), up to 13 times per keyboard navigation, and on
        // every tick of the reveal-scroll retry, over as many as a few thousand
        // folders — the scan it replaced was the whole of that cost.
        public int FindVisibleFolderIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                return -1;
            }

            return _visibleFolderIndexByPath.TryGetValue(folderPath, out var index) ? index : -1;
        }
    }
}
