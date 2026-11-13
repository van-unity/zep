using System;
using System.Collections.Generic;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Domain;
using static Zep.Core.Application.ZepPathHelpers;

namespace Zep.Core.Application
{
    internal sealed class FolderEntriesBuilder
    {
        private readonly InMemoryAssetIndex _index;
        private readonly Dictionary<string, ZepFolderEntry> _standardByPath =
            new Dictionary<string, ZepFolderEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, ZepFolderEntry>>
            _standardChildrenByParent =
                new Dictionary<string, Dictionary<string, ZepFolderEntry>>(
                    StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<ZepFolderEntry>>
            _sortedStandardChildrenByParent =
                new Dictionary<string, IReadOnlyList<ZepFolderEntry>>(
                    StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<ZepFolderEntry> _cached = Array.Empty<ZepFolderEntry>();
        private int _version;
        // Folder mutations invalidate the sorted snapshot; ordinary asset changes do not.
        private int _standardVersion;
        private IReadOnlyList<ZepFolderEntry> _sortedStandardEntries;
        private int _sortedStandardEntriesVersion = -1;

        internal FolderEntriesBuilder(
            InMemoryAssetIndex index)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
        }

        internal IReadOnlyList<ZepFolderEntry> Cached => _cached;

        internal int Version => _version;

        internal IReadOnlyList<ZepFolderEntry> RebuildFromIndex(
            IEnumerable<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> sourceFolderRecords)
        {
            _index.ReplaceFolders(sourceFolderRecords);
            var __sw_build = ZepPerfLog.IsEnabled
                ? System.Diagnostics.Stopwatch.StartNew()
                : null;
            var standardFolders = BuildStandardFolderEntries(
                records,
                new List<AssetFolderRecord>(_index.EnumerateFolders()));
            _standardByPath.Clear();
            _standardChildrenByParent.Clear();
            _sortedStandardChildrenByParent.Clear();
            _standardVersion++;
            for (var i = 0; i < standardFolders.Count; i++)
            {
                AddOrUpdateStandardEntry(standardFolders[i]);
            }

            foreach (var parentPath in _standardChildrenByParent.Keys)
            {
                _ = GetSortedStandardChildren(parentPath);
            }
            if (__sw_build != null)
            {
                __sw_build.Stop();
                ZepPerfLog.Log($"[Zep/perf]     FolderEntriesBuilder.BuildStandardFolderEntries count={standardFolders.Count} ms={__sw_build.Elapsed.TotalMilliseconds:F2}");
            }

            // BuildStandardFolderEntries already produced exactly what
            // GetStandardEntries would sort, in the same order, so seed the cache
            // with it instead of sorting the same set a second time.
            _sortedStandardEntries = standardFolders;
            _sortedStandardEntriesVersion = _standardVersion;

            _cached = standardFolders;
            _version++;
            return standardFolders;
        }

        internal void ApplyCatalogDelta(AssetCatalogDelta delta)
        {
            if (delta == null
                || (delta.RemovedFolders.Count == 0 && delta.UpsertedFolders.Count == 0))
            {
                return;
            }

            for (var i = 0; i < delta.RemovedFolders.Count; i++)
            {
                RemoveStandardEntry(delta.RemovedFolders[i].FolderPath.Value);
            }

            for (var i = 0; i < delta.UpsertedFolders.Count; i++)
            {
                var folder = delta.UpsertedFolders[i];
                AddOrUpdateStandardEntry(CreateStandardFolderEntry(
                    folder.FolderPath.Value,
                    folder));
            }

            // Sidebar consumers use child queries and the delta; full snapshots stay lazy.
        }

        internal IReadOnlyList<ZepFolderEntry> GetStandardEntries()
        {
            if (_sortedStandardEntriesVersion == _standardVersion
                && _sortedStandardEntries != null)
            {
                return _sortedStandardEntries;
            }

            ZepPerfLog.Count(ZepPerfCounter.FolderCatalogRemerges);
            var result = new List<ZepFolderEntry>(_standardByPath.Values);
            SortByDisplayOrder(result);
            _sortedStandardEntries = result;
            _sortedStandardEntriesVersion = _standardVersion;
            return result;
        }

        internal bool TryGetVisibleFolderEntry(
            string folderPath,
            out ZepFolderEntry entry)
        {
            folderPath = AssetPathFormat.Normalize(folderPath);
            if (string.IsNullOrEmpty(folderPath))
            {
                entry = null;
                return false;
            }

            // This is a per-row sidebar seam, and the cache already holds the
            // entry CreateStandardFolderEntry would rebuild: both are made from
            // the same folder record, and every catalog delta rewrites this map
            // through AddOrUpdateStandardEntry/RemoveStandardEntry. Minting a
            // fresh entry per call re-normalised three or four strings for a
            // value that was already sitting here.
            if (_standardByPath.TryGetValue(folderPath, out entry))
            {
                return true;
            }

            if (_index.TryGetFolder(folderPath, out var folder))
            {
                entry = CreateStandardFolderEntry(folderPath, folder);
                return true;
            }

            if (_index.TryGetPending(folderPath, out var pending)
                && pending.IsFolder)
            {
                entry = CreateStandardFolderEntry(folderPath, null);
                return true;
            }

            if (string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase)
                || string.Equals(folderPath, "Packages", StringComparison.OrdinalIgnoreCase))
            {
                entry = CreateStandardFolderEntry(folderPath, null);
                return true;
            }

            entry = null;
            return false;
        }

        internal IReadOnlyList<ZepFolderEntry> GetDirectVisibleFolders(
            string parentFolder)
        {
            var standard = GetSortedStandardChildren(parentFolder);
            List<ZepFolderEntry> result = null;

            foreach (var pending in _index.EnumerateDirectPending(parentFolder))
            {
                if (pending.IsFolder && !_standardByPath.ContainsKey(pending.Path))
                {
                    result ??= new List<ZepFolderEntry>(standard);
                    result.Add(CreateStandardFolderEntry(
                        pending.Path,
                        null));
                }
            }

            if (result == null)
            {
                return standard;
            }

            SortByDisplayOrder(result);
            return result;
        }

        private void AddOrUpdateStandardEntry(ZepFolderEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Path))
            {
                return;
            }

            RemoveStandardEntry(entry.Path);
            _standardByPath[entry.Path] = entry;
            var parentPath = AssetPathFormat.GetParentFolderPath(entry.Path);
            if (!_standardChildrenByParent.TryGetValue(parentPath, out var children))
            {
                children = new Dictionary<string, ZepFolderEntry>(
                    StringComparer.OrdinalIgnoreCase);
                _standardChildrenByParent[parentPath] = children;
            }

            children[entry.Path] = entry;
            _sortedStandardChildrenByParent.Remove(parentPath);
            _standardVersion++;
        }

        private void RemoveStandardEntry(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)
                || !_standardByPath.TryGetValue(folderPath, out var existing))
            {
                return;
            }

            _standardByPath.Remove(folderPath);
            var parentPath = AssetPathFormat.GetParentFolderPath(existing.Path);
            if (_standardChildrenByParent.TryGetValue(parentPath, out var children))
            {
                children.Remove(existing.Path);
                if (children.Count == 0)
                {
                    _standardChildrenByParent.Remove(parentPath);
                }
            }

            _sortedStandardChildrenByParent.Remove(parentPath);
            _standardVersion++;
        }

        private IReadOnlyList<ZepFolderEntry> GetSortedStandardChildren(
            string parentFolder)
        {
            parentFolder = AssetPathFormat.Normalize(parentFolder);
            if (_sortedStandardChildrenByParent.TryGetValue(
                    parentFolder,
                    out var sorted))
            {
                return sorted;
            }

            if (!_standardChildrenByParent.TryGetValue(parentFolder, out var children))
            {
                return Array.Empty<ZepFolderEntry>();
            }

            var result = new List<ZepFolderEntry>(children.Values);
            SortByDisplayOrder(result);
            _sortedStandardChildrenByParent[parentFolder] = result;
            return result;
        }

        internal IReadOnlyList<ZepFolderEntry> GetVisibleFolderChildren(
            string parentFolder)
        {
            parentFolder = AssetPathFormat.Normalize(parentFolder);
            if (!string.IsNullOrEmpty(parentFolder))
            {
                return GetDirectVisibleFolders(parentFolder);
            }

            var roots = new List<ZepFolderEntry>(2);
            if (TryGetVisibleFolderEntry("Assets", out var assets))
            {
                roots.Add(assets);
            }

            if (TryGetVisibleFolderEntry("Packages", out var packages))
            {
                roots.Add(packages);
            }

            return roots;
        }

        private static IReadOnlyList<ZepFolderEntry> BuildStandardFolderEntries(
            IEnumerable<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> sourceFolderRecords)
        {
            // Dedup only — order is irrelevant here because `entries` is fully
            // re-sorted by SortByDisplayOrder at the end. The old SortedSet kept
            // a red-black tree in sort order on every insert, so each of the ~10 k
            // per-record parent-walk Add()s paid O(log n) string comparisons to
            // maintain an ordering that was immediately discarded. A HashSet dedups
            // in O(1) with identical output.
            var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Assets",
                "Packages"
            };

            var metadataByPath = new Dictionary<string, AssetFolderRecord>(
                StringComparer.OrdinalIgnoreCase);
            if (sourceFolderRecords != null)
            {
                for (var i = 0; i < sourceFolderRecords.Count; i++)
                {
                    var folderRecord = sourceFolderRecords[i];
                    if (folderRecord == null || folderRecord.FolderPath.IsEmpty)
                    {
                        continue;
                    }

                    string folderPath = folderRecord.FolderPath;
                    folderPaths.Add(folderPath);
                    metadataByPath[folderPath] = folderRecord;
                }
            }

            foreach (var record in records)
            {
                var folderPath = GetParentFolder(record.AssetPath);
                while (!string.IsNullOrEmpty(folderPath)
                    && folderPaths.Add(folderPath)
                    && !string.Equals(folderPath, "Assets", StringComparison.Ordinal)
                    && !string.Equals(folderPath, "Packages", StringComparison.Ordinal))
                {
                    folderPath = GetParentFolder(folderPath);
                }
            }

            var entries = new List<ZepFolderEntry>(folderPaths.Count);
            foreach (var path in folderPaths)
            {
                metadataByPath.TryGetValue(path, out var folderRecord);
                entries.Add(CreateStandardFolderEntry(path, folderRecord));
            }

            SortByDisplayOrder(entries);
            return entries;
        }

        private static ZepFolderEntry CreateStandardFolderEntry(
            string folderPath,
            AssetFolderRecord folderRecord)
        {
            return new ZepFolderEntry(
                GetFolderDisplayName(folderPath),
                folderPath,
                GetFolderDepth(folderPath),
                !AssetPathClassification.IsWritablePath(folderPath),
                guid: folderRecord?.Guid ?? string.Empty,
                unityLabels: folderRecord?.UnityLabels);
        }
    }
}
