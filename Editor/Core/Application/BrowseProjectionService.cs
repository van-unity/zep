using System;
using System.Collections.Generic;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.Domain;
using static Zep.Core.Application.ZepPathHelpers;

namespace Zep.Core.Application
{
    public sealed class BrowseProjectionService
    {
        private static readonly IReadOnlyList<ZepAssetEntry> EmptyAssetEntries = Array.Empty<ZepAssetEntry>();

        // Browse buckets are already sorted folders-first, then by name/path.
        // Preserve that fact behind the list contract so reveal/ping can locate
        // an indexed asset in logarithmic time without a second path dictionary.
        private sealed class FolderContentsList
            : IReadOnlyList<ZepAssetEntry>, IAssetPathLookup
        {
            private readonly IReadOnlyList<ZepAssetEntry> _entries;
            private readonly InMemoryAssetIndex _index;
            private readonly int _folderCount;

            internal FolderContentsList(
                IReadOnlyList<ZepAssetEntry> entries,
                InMemoryAssetIndex index,
                int folderCount)
            {
                _entries = entries ?? throw new ArgumentNullException(nameof(entries));
                _index = index ?? throw new ArgumentNullException(nameof(index));
                _folderCount = folderCount;
            }

            public int Count => _entries.Count;
            public ZepAssetEntry this[int index] => _entries[index];

            public IEnumerator<ZepAssetEntry> GetEnumerator() =>
                _entries.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();

            public int IndexOfPath(string assetPath)
            {
                if (string.IsNullOrEmpty(assetPath))
                {
                    return -1;
                }

                if (_index.TryGetByPath(assetPath, out var record))
                {
                    return FindInSortedSegment(
                        assetPath,
                        record.Name,
                        _folderCount,
                        Count);
                }

                if (_index.TryGetFolder(assetPath, out _))
                {
                    var found = FindInSortedSegment(
                        assetPath,
                        GetFolderDisplayName(assetPath),
                        0,
                        _folderCount);
                    if (found >= 0)
                    {
                        return found;
                    }

                    // Defense, not the primary path: the binary search only finds
                    // the folder if this bucket was built with the same display
                    // name. Every producer now goes through GetFolderDisplayName,
                    // but a sort-key disagreement would otherwise silently break
                    // reveal/ping, so scan the folder segment before giving up.
                    // Folders always occupy [0, _folderCount) — see ShouldInsertBefore.
                    for (var i = 0; i < _folderCount; i++)
                    {
                        if (string.Equals(
                            _entries[i].Path,
                            assetPath,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return i;
                        }
                    }

                    return -1;
                }

                // Pending/test entries may not have reached the index yet.
                // Keep lookup correct without penalizing the indexed hot path.
                for (var i = 0; i < Count; i++)
                {
                    if (string.Equals(
                        _entries[i].Path,
                        assetPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                return -1;
            }

            private int FindInSortedSegment(
                string assetPath,
                string assetName,
                int start,
                int end)
            {
                var low = start;
                var high = end;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (string.Compare(
                        _entries[middle].Name,
                        assetName,
                        StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                for (var i = low;
                    i < end && string.Equals(
                        _entries[i].Name,
                        assetName,
                        StringComparison.OrdinalIgnoreCase);
                    i++)
                {
                    if (string.Equals(
                        _entries[i].Path,
                        assetPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        private readonly Dictionary<string, IReadOnlyList<ZepAssetEntry>> _cachedEntriesByFolder =
            new Dictionary<string, IReadOnlyList<ZepAssetEntry>>(StringComparer.OrdinalIgnoreCase);

        private readonly InMemoryAssetIndex _index;
        private readonly AssetSearchService _searchService;
        private readonly TagProjectionService _tagProjection;
        private readonly FolderSearchIndex _folderSearch = new FolderSearchIndex();

        private IReadOnlyList<ZepTypeFilterOption> _cachedTypeFilterOptions = Array.Empty<ZepTypeFilterOption>();
        private int _folderContentsVersion;
        // The last text this service parsed, and its tokens. One entry is all a
        // keystroke needs: the projection build and the suggestion pass that
        // follows it ask for the same string, and the parse is a pure function
        // of that string, so a hit is always a correct answer. A second pane
        // searching for something else just evicts and re-parses, which is what
        // both callers did unconditionally before.
        private string _memoizedSearchText;
        private ParsedSearchQuery _memoizedSearchQuery;

        internal BrowseProjectionService(
            InMemoryAssetIndex index,
            AssetSearchService searchService,
            TagProjectionService tagProjection)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _tagProjection = tagProjection ?? throw new ArgumentNullException(nameof(tagProjection));
        }

        internal IReadOnlyList<ZepTypeFilterOption> CachedTypeFilterOptions => _cachedTypeFilterOptions;

        internal int LastFolderSearchCandidateCount =>
            _folderSearch.LastCandidateCount;

        internal int FolderSearchTokenCandidateSets =>
            _folderSearch.TokenCandidateSetsAllocated;

        // Direct children of one folder, in browse order (folders first, then
        // files, name-sorted) — the same list plain browsing shows for that
        // folder. Null when the folder isn't in the projection (deleted,
        // virtual, or never indexed). The list view's inline folder expansion
        // and its selection reconciliation both read through this, so an
        // expanded folder renders exactly what navigating into it would.
        // Bumped whenever any folder bucket is written. The list view's
        // expansion flatten holds children of folders OTHER than the one being
        // browsed, so it cannot tell from its own inputs whether a bucket moved
        // under it; this is the cheap signal that lets it skip the per-read
        // probe over every expanded folder.
        public int FolderContentsVersion => _folderContentsVersion;

        public IReadOnlyList<ZepAssetEntry> GetFolderContentsOrNull(string folderPath)
        {
            return !string.IsNullOrEmpty(folderPath)
                && _cachedEntriesByFolder.TryGetValue(folderPath, out var entries)
                    ? entries
                    : null;
        }

        internal void RebuildCache(IReadOnlyList<AssetRecord> records, IReadOnlyList<ZepFolderEntry> standardFolders)
        {
            var __sw = ZepPerfLog.IsEnabled
                ? System.Diagnostics.Stopwatch.StartNew()
                : null;
            _folderContentsVersion++;
            _cachedEntriesByFolder.Clear();
            _folderSearch.Replace(standardFolders);

            var folderChildrenByParent = new Dictionary<string, List<ZepAssetEntry>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < standardFolders.Count; i++)
            {
                var folder = standardFolders[i];
                var parentPath = GetParentFolder(folder.Path);
                if (string.Equals(parentPath, folder.Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!folderChildrenByParent.TryGetValue(parentPath, out var children))
                {
                    children = new List<ZepAssetEntry>();
                    folderChildrenByParent[parentPath] = children;
                }

                children.Add(CreateAssetEntry(
                    folder.Name,
                    folder.Path,
                    "Folder"));
            }

            var filesByParent = new Dictionary<string, List<ZepAssetEntry>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var parentPath = GetParentFolder(record.AssetPath);
                if (!filesByParent.TryGetValue(parentPath, out var files))
                {
                    files = new List<ZepAssetEntry>();
                    filesByParent[parentPath] = files;
                }

                files.Add(CreateAssetEntry(
                    record.Name,
                    record.AssetPath,
                    record.TypeName));
            }

            foreach (var entry in folderChildrenByParent)
            {
                entry.Value.Sort(static (left, right) =>
                {
                    var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                    return byName != 0
                        ? byName
                        : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
                });
            }

            foreach (var entry in filesByParent)
            {
                entry.Value.Sort(static (left, right) =>
                {
                    var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                    return byName != 0
                        ? byName
                        : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
                });
            }

            var projectionFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Assets",
                "Packages"
            };

            foreach (var folder in standardFolders)
            {
                projectionFolders.Add(folder.Path);
            }

            foreach (var parentFolderPath in folderChildrenByParent.Keys)
            {
                projectionFolders.Add(parentFolderPath);
            }

            foreach (var parentFolderPath in filesByParent.Keys)
            {
                projectionFolders.Add(parentFolderPath);
            }

            foreach (var folderPath in projectionFolders)
            {
                folderChildrenByParent.TryGetValue(folderPath, out var folderAssets);
                filesByParent.TryGetValue(folderPath, out var fileAssets);
                var folderCount = folderAssets == null ? 0 : folderAssets.Count;
                var fileCount = fileAssets == null ? 0 : fileAssets.Count;

                if (folderCount == 0 && fileCount == 0)
                {
                    _cachedEntriesByFolder[folderPath] = EmptyAssetEntries;
                    continue;
                }

                var merged = new ZepAssetEntry[folderCount + fileCount];
                if (folderCount > 0)
                {
                    folderAssets.CopyTo(merged, 0);
                }

                if (fileCount > 0)
                {
                    fileAssets.CopyTo(merged, folderCount);
                }

                _cachedEntriesByFolder[folderPath] = new FolderContentsList(
                    merged,
                    _index,
                    folderCount);
            }

            _cachedTypeFilterOptions = BuildTypeFilterOptions(records, standardFolders);
            if (__sw != null)
            {
                __sw.Stop();
                ZepPerfLog.Log($"[Zep/perf]     BrowseProjectionService.RebuildCache records={records.Count} folders={standardFolders.Count} folderBuckets={_cachedEntriesByFolder.Count} typeOptions={_cachedTypeFilterOptions.Count} ms={__sw.Elapsed.TotalMilliseconds:F2}");
            }
        }

        // Compatibility entry point for callers that already hold the snapshot.
        // Counts come from the canonical catalog; this never re-walks records.
        internal void RebuildTypeFilterOptions(IReadOnlyList<AssetRecord> records, IReadOnlyList<ZepFolderEntry> standardFolders)
        {
            _cachedTypeFilterOptions = BuildTypeFilterOptions(
                _index.TypeCounts,
                CountFilterableFolders(standardFolders));
        }

        // Rebuild only the direct-child buckets named by one atomic catalog
        // delta. A 1,000-file import into one folder therefore sorts and publishes
        // that folder once instead of copying its list once per imported path.
        internal void ApplyCatalogDelta(AssetCatalogDelta delta)
        {
            if (delta == null || delta.IsEmpty)
            {
                return;
            }

            _folderContentsVersion++;
            for (var i = 0; i < delta.RemovedFolders.Count; i++)
            {
                _folderSearch.Remove(
                    delta.RemovedFolders[i].FolderPath.Value);
                _cachedEntriesByFolder.Remove(
                    delta.RemovedFolders[i].FolderPath.Value);
            }

            for (var i = 0; i < delta.RemovedPendingEntries.Count; i++)
            {
                if (delta.RemovedPendingEntries[i].IsFolder)
                {
                    _folderSearch.Remove(
                        delta.RemovedPendingEntries[i].Path);
                    _cachedEntriesByFolder.Remove(
                        delta.RemovedPendingEntries[i].Path);
                }
            }

            foreach (var parentFolder in delta.AffectedParentFolders)
            {
                RebuildFolderBucket(parentFolder);
            }

            for (var i = 0; i < delta.UpsertedFolders.Count; i++)
            {
                var folder = delta.UpsertedFolders[i];
                var folderPath = folder.FolderPath.Value;
                _folderSearch.Upsert(CreateSearchFolderEntry(
                    folderPath,
                    folder.Guid,
                    folder.UnityLabels));
                if (!_cachedEntriesByFolder.ContainsKey(folderPath))
                {
                    RebuildFolderBucket(folderPath);
                }
            }

            for (var i = 0; i < delta.UpsertedPendingEntries.Count; i++)
            {
                var pending = delta.UpsertedPendingEntries[i];
                if (!pending.IsFolder)
                {
                    continue;
                }

                _folderSearch.Upsert(CreateSearchFolderEntry(
                    pending.Path,
                    string.Empty,
                    Array.Empty<string>()));
                if (!_cachedEntriesByFolder.ContainsKey(pending.Path))
                {
                    RebuildFolderBucket(pending.Path);
                }
            }

            _cachedTypeFilterOptions = BuildTypeFilterOptions(
                _index.TypeCounts,
                _index.FilterableFolderCount);
        }

        internal void RefreshParentFolders(IReadOnlyCollection<string> parentFolders)
        {
            if (parentFolders == null || parentFolders.Count == 0)
            {
                return;
            }

            foreach (var parentFolder in parentFolders)
            {
                RebuildFolderBucket(parentFolder);
            }
        }

        internal string ResolveSelectedTypeFilter(string selectedTypeFilter)
        {
            if (string.IsNullOrWhiteSpace(selectedTypeFilter))
            {
                return string.Empty;
            }

            for (var i = 0; i < _cachedTypeFilterOptions.Count; i++)
            {
                if (string.Equals(_cachedTypeFilterOptions[i].TypeName, selectedTypeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return _cachedTypeFilterOptions[i].TypeName;
                }
            }

            return string.Empty;
        }

        internal IReadOnlyList<ZepAssetEntry> BuildAssetEntries(
            string currentFolderPath,
            string searchText,
            ZepSearchScope selectedSearchScope,
            string selectedTypeFilter,
            string selectedTagFilterId,
            string rootFolderPath = null)
        {
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                ZepPerfLog.Count(
                    ZepPerfCounter.SearchProjectionRequests);
            }

            return BuildAssetEntriesUncached(
                currentFolderPath,
                searchText,
                selectedSearchScope,
                selectedTypeFilter,
                selectedTagFilterId,
                rootFolderPath);
        }

        // The one place this service turns search text into tokens, and the one
        // the controller borrows for the suggestion pass on the same keystroke.
        // The returned query is read-only after Parse (nothing outside Parse
        // adds a term), so handing the same instance to Search and to Suggest
        // is safe.
        internal ParsedSearchQuery ParseSearchText(string searchText)
        {
            if (_memoizedSearchQuery != null
                && string.Equals(_memoizedSearchText, searchText, StringComparison.Ordinal))
            {
                return _memoizedSearchQuery;
            }

            var parsed = SearchQuery.Parse(searchText);
            _memoizedSearchText = searchText;
            _memoizedSearchQuery = parsed;
            return parsed;
        }

        private IReadOnlyList<ZepAssetEntry> BuildAssetEntriesUncached(
            string currentFolderPath,
            string searchText,
            ZepSearchScope selectedSearchScope,
            string selectedTypeFilter,
            string selectedTagFilterId,
            string rootFolderPath)
        {
            var hasSearchText = !string.IsNullOrWhiteSpace(searchText);
            var hasTypeFilter = !string.IsNullOrWhiteSpace(selectedTypeFilter);
            var hasTagFilter = !string.IsNullOrWhiteSpace(selectedTagFilterId);
            var searchTokens = hasSearchText
                ? ParseSearchText(searchText)
                : null;
            var hasScopedFilter = hasSearchText || hasTagFilter;
            var searchScopePath = hasScopedFilter
                ? ResolveSearchScopePath(selectedSearchScope, currentFolderPath)
                : currentFolderPath;
            if (hasScopedFilter && !string.IsNullOrWhiteSpace(rootFolderPath))
            {
                searchScopePath = selectedSearchScope == ZepSearchScope.CurrentFolder
                    && IsWithinFolderScope(currentFolderPath, rootFolderPath)
                        ? currentFolderPath
                        : rootFolderPath;
            }
            if (!hasSearchText && !hasTypeFilter && !hasTagFilter)
            {
                return _cachedEntriesByFolder.TryGetValue(currentFolderPath, out var entries)
                    ? entries
                    : EmptyAssetEntries;
            }

            if (!hasSearchText)
            {
                if (hasTagFilter)
                {
                    return BuildTagOnlyEntries(
                        searchScopePath,
                        selectedTypeFilter,
                        selectedTagFilterId);
                }

                if (!_cachedEntriesByFolder.TryGetValue(currentFolderPath, out var currentEntries))
                {
                    return EmptyAssetEntries;
                }

                var filtered = new List<ZepAssetEntry>(currentEntries.Count);
                for (var i = 0; i < currentEntries.Count; i++)
                {
                    if (MatchesTypeFilter(currentEntries[i].TypeName, selectedTypeFilter))
                    {
                        filtered.Add(currentEntries[i]);
                    }
                }

                return filtered;
            }

            return BuildSearchEntries(
                currentFolderPath,
                searchText,
                searchTokens,
                searchScopePath,
                selectedTypeFilter,
                selectedTagFilterId);
        }

        private IReadOnlyList<ZepAssetEntry> BuildTagOnlyEntries(
            string tagScopePath,
            string selectedTypeFilter,
            string selectedTagFilterId)
        {
            // Finder-style tag views iterate the tag owner's membership set,
            // not every catalog record/folder. Cost follows matching paths.
            var tagFolderResults = new List<ZepAssetEntry>();
            var tagFileResults = new List<ZepAssetEntry>();
            foreach (var path in _tagProjection.EnumeratePathsForTag(selectedTagFilterId))
            {
                if (!IsWithinFolderScope(path, tagScopePath))
                {
                    continue;
                }

                if (_index.TryGetByPath(path, out var record))
                {
                    if (MatchesTypeFilter(record.TypeName, selectedTypeFilter))
                    {
                        tagFileResults.Add(CreateAssetEntry(
                            record.Name,
                            record.AssetPath,
                            record.TypeName));
                    }

                    continue;
                }

                if (!string.Equals(path, tagScopePath, StringComparison.OrdinalIgnoreCase)
                    && _index.TryGetFolder(path, out _)
                    && MatchesTypeFilter("Folder", selectedTypeFilter))
                {
                    tagFolderResults.Add(CreateAssetEntry(
                        GetFolderDisplayName(path),
                        path,
                        "Folder"));
                }
            }
            tagFolderResults.Sort(static (a, b) =>
            {
                var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

            tagFileResults.Sort(static (a, b) =>
            {
                var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

            if (tagFolderResults.Count == 0)
            {
                return tagFileResults;
            }
            if (tagFileResults.Count == 0)
            {
                return tagFolderResults;
            }
            tagFolderResults.AddRange(tagFileResults);
            return tagFolderResults;
        }

        private IReadOnlyList<ZepAssetEntry> BuildSearchEntries(
            string currentFolderPath,
            string searchText,
            ParsedSearchQuery searchQuery,
            string searchScopePath,
            string selectedTypeFilter,
            string selectedTagFilterId)
        {
            ZepPerfLog.Count(
                ZepPerfCounter.SearchProjectionBuilds);
            var folderEntries = new List<ZepAssetEntry>();
            if (MatchesTypeFilter("Folder", selectedTypeFilter))
            {
                foreach (var folder in _folderSearch.GetCandidates(
                    searchQuery,
                    _tagProjection))
                {
                    if (string.Equals(
                            folder.Path,
                            searchScopePath,
                            StringComparison.OrdinalIgnoreCase)
                        || !IsWithinFolderScope(folder.Path, searchScopePath)
                        || !_tagProjection.FolderMatchesSearch(folder, searchQuery))
                    {
                        continue;
                    }

                    var entry = CreateAssetEntry(
                        folder.Name,
                        folder.Path,
                        "Folder");
                    if (_tagProjection.MatchesTagFilter(
                        entry.Path,
                        selectedTagFilterId))
                    {
                        folderEntries.Add(entry);
                    }
                }
            }

            folderEntries.Sort(static (a, b) =>
            {
                var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

            System.Diagnostics.Stopwatch searchStopwatch = null;
            if (ZepPerfLog.IsEnabled)
            {
                searchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            }

            // The tokens were parsed once at the top of BuildAssetEntriesUncached
            // (the folder loop above matches against them too); the token overload
            // skips the engine's re-parse of the raw query string.
            ZepPerfLog.Count(ZepPerfCounter.SearchQueries);
            var searchResults = _searchService.Search(searchQuery, searchScopePath, selectedTypeFilter);
            if (searchStopwatch != null)
            {
                searchStopwatch.Stop();
                ZepPerfLog.Log($"[Zep/perf]   BuildSearchEntries.Search query=\"{searchText}\" scope=\"{searchScopePath}\" type=\"{selectedTypeFilter}\" results={searchResults.Count} ms={searchStopwatch.Elapsed.TotalMilliseconds:F2}");
            }

            // When a tag filter is active we have to pre-filter records (the search
            // engine doesn't know about tags). The narrow records list still flows
            // into the lazy wrapper — entries are only constructed at bind time.
            // When no tag filter is active (the dominant typing case) we pass the
            // search results straight through and pay zero per-record cost.
            IReadOnlyList<AssetRecord> fileRecords;
            if (string.IsNullOrEmpty(selectedTagFilterId))
            {
                fileRecords = searchResults;
            }
            else
            {
                var filtered = new List<AssetRecord>(searchResults.Count);
                for (var i = 0; i < searchResults.Count; i++)
                {
                    if (_tagProjection.MatchesTagFilter(searchResults[i].AssetPath, selectedTagFilterId))
                    {
                        filtered.Add(searchResults[i]);
                    }
                }
                fileRecords = filtered;
            }

            var folderArray = folderEntries.Count == 0
                ? Array.Empty<ZepAssetEntry>()
                : folderEntries.ToArray();
            var wrapper = new LazyAssetEntryList(folderArray, fileRecords, _tagProjection);
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]   BuildSearchEntries.LazyWrapper folders={folderArray.Length} fileRecords={fileRecords.Count}");
            return wrapper;
        }

        internal void RemoveEntry(string assetPath)
        {
            var parentPath = GetParentFolder(assetPath);
            if (!_cachedEntriesByFolder.TryGetValue(parentPath, out var entries))
            {
                return;
            }

            var updated = new List<ZepAssetEntry>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                if (!string.Equals(entries[i].Path, assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    updated.Add(entries[i]);
                }
            }

            _folderContentsVersion++;
            _cachedEntriesByFolder[parentPath] = updated.Count > 0
                ? CreateFolderContentsList(updated)
                : EmptyAssetEntries;
        }

        internal void InsertEntry(string assetPath)
        {
            // WHY: Look up by path, not by AssetDatabase.AssetPathToGUID + TryGet(guid).
            // AssetPathToGUID returns empty for any path that isn't actually in the
            // editor's asset database (every test path, plus mid-flight imports);
            // the caller has already mutated _index with the new record by the time
            // we get here, so the path index is the authoritative source.
            if (!_index.TryGetByPath(assetPath, out var record))
            {
                return;
            }

            var parentPath = GetParentFolder(assetPath);
            var newEntry = CreateAssetEntry(
                record.Name,
                record.AssetPath,
                record.TypeName);

            InsertSorted(parentPath, newEntry);
        }

        internal void InsertFolderEntry(string folderPath)
        {
            UpsertFolderEntryInParent(folderPath);
            // A restore updates this cache synchronously, then Unity reports the
            // same folder through AssetPostprocessor after ImportAsset. The second
            // notification must not erase children already cached for an existing
            // folder; only a genuinely new folder needs an empty child bucket.
            if (!_cachedEntriesByFolder.ContainsKey(folderPath))
            {
                _folderContentsVersion++;
                _cachedEntriesByFolder[folderPath] = EmptyAssetEntries;
            }
        }

        internal void UpsertFolderEntryInParent(string folderPath)
        {
            _index.TryGetFolder(folderPath, out var folderRecord);
            _folderSearch.Upsert(CreateSearchFolderEntry(
                folderPath,
                folderRecord?.Guid ?? string.Empty,
                folderRecord?.UnityLabels ?? Array.Empty<string>()));
            var parentPath = GetParentFolder(folderPath);
            var newEntry = CreateAssetEntry(
                GetFolderDisplayName(folderPath),
                folderPath,
                "Folder");

            InsertSorted(parentPath, newEntry);
        }

        private void InsertSorted(string parentPath, ZepAssetEntry newEntry)
        {
            _folderContentsVersion++;
            if (!_cachedEntriesByFolder.TryGetValue(parentPath, out var entries) || entries.Count == 0)
            {
                _cachedEntriesByFolder[parentPath] = new FolderContentsList(
                    new[] { newEntry },
                    _index,
                    IsFolderTypeName(newEntry.TypeName) ? 1 : 0);
                return;
            }

            var updated = new List<ZepAssetEntry>(entries.Count + 1);
            var inserted = false;
            for (var i = 0; i < entries.Count; i++)
            {
                // Insertions are fed by both the synchronous command result and
                // Unity's later import callback. Keep the cache's one-row-per-path
                // invariant here at its owner so every caller is idempotent.
                if (string.Equals(entries[i].Path, newEntry.Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!inserted && ShouldInsertBefore(newEntry, entries[i]))
                {
                    updated.Add(newEntry);
                    inserted = true;
                }

                updated.Add(entries[i]);
            }

            if (!inserted)
            {
                updated.Add(newEntry);
            }

            _cachedEntriesByFolder[parentPath] = CreateFolderContentsList(updated);
        }

        private void RebuildFolderBucket(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                return;
            }

            _folderContentsVersion++;

            var entries = new List<ZepAssetEntry>();
            foreach (var folder in _index.EnumerateDirectFolders(folderPath))
            {
                entries.Add(CreateAssetEntry(
                    GetFolderDisplayName(folder.FolderPath.Value),
                    folder.FolderPath.Value,
                    "Folder"));
            }

            foreach (var record in _index.EnumerateDirectFiles(folderPath))
            {
                entries.Add(CreateAssetEntry(
                    record.Name,
                    record.AssetPath,
                    record.TypeName));
            }

            foreach (var pending in _index.EnumerateDirectPending(folderPath))
            {
                _tagProjection.ResolveUnindexedPresentation(
                    pending.Path,
                    IsFolderTypeName(pending.InferredTypeName),
                    out var tags,
                    out var folderTintColor);
                entries.Add(new ZepAssetEntry(
                    pending.Name,
                    pending.Path,
                    pending.InferredTypeName,
                    tags: tags,
                    folderTintColor: folderTintColor));
            }

            if (entries.Count == 0)
            {
                _cachedEntriesByFolder[folderPath] = EmptyAssetEntries;
                return;
            }

            // Direct three-way. Sort wants an ordering, and asking the boolean
            // predicate in both directions evaluated the same folder/name/path
            // comparison twice per comparison on the hottest folder-open path
            // for no extra information. Same order as ShouldInsertBefore —
            // folders first, then name, then path, all OrdinalIgnoreCase — which
            // the single-entry insert path above still needs in boolean form.
            entries.Sort(static (left, right) =>
            {
                var folderOrder = IsFolderTypeName(left.TypeName)
                    .CompareTo(IsFolderTypeName(right.TypeName));
                if (folderOrder != 0)
                {
                    return -folderOrder;
                }

                var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0
                    ? byName
                    : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            });
            _cachedEntriesByFolder[folderPath] = CreateFolderContentsList(entries);
        }

        private FolderContentsList CreateFolderContentsList(
            IReadOnlyList<ZepAssetEntry> entries)
        {
            var folderCount = 0;
            while (folderCount < entries.Count
                && IsFolderTypeName(entries[folderCount].TypeName))
            {
                folderCount++;
            }

            return new FolderContentsList(entries, _index, folderCount);
        }

        internal ZepAssetEntry CreateAssetEntry(
            string name,
            string assetPath,
            string typeName)
        {
            // Presentation (modified-date, size, IsAddressable) lives in the
            // presentation cache and is fetched lazily by row binders via
            // ZepController.GetPresentation. Touching it here used to
            // mean O(records) disk syscalls on every projection rebuild and
            // every search match — see the entry type's class comment.
            return new ZepAssetEntry(
                name,
                assetPath,
                typeName,
                tags: _tagProjection.GetTagsForPath(assetPath),
                folderTintColor: _tagProjection.GetFolderTintColorForPath(assetPath, typeName));
        }

        private static ZepFolderEntry CreateSearchFolderEntry(
            string folderPath,
            string guid,
            IReadOnlyList<string> unityLabels)
        {
            return new ZepFolderEntry(
                GetFolderDisplayName(folderPath),
                folderPath,
                GetFolderDepth(folderPath),
                !AssetPathClassification.IsWritablePath(folderPath),
                guid: guid,
                unityLabels: unityLabels);
        }

        private static bool ShouldInsertBefore(ZepAssetEntry newEntry, ZepAssetEntry existing)
        {
            var newIsFolder = IsFolderTypeName(newEntry.TypeName);
            var existingIsFolder = IsFolderTypeName(existing.TypeName);
            if (newIsFolder != existingIsFolder)
            {
                return newIsFolder;
            }

            var byName = string.Compare(newEntry.Name, existing.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName < 0
                : string.Compare(newEntry.Path, existing.Path, StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static IReadOnlyList<ZepTypeFilterOption> BuildTypeFilterOptions(
            IReadOnlyList<AssetRecord> records,
            IReadOnlyList<ZepFolderEntry> folders)
        {
            var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < records.Count; i++)
            {
                var typeName = records[i].TypeName;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                typeCounts.TryGetValue(typeName, out var existingCount);
                typeCounts[typeName] = existingCount + 1;
            }

            return BuildTypeFilterOptions(typeCounts, CountFilterableFolders(folders));
        }

        private static int CountFilterableFolders(
            IReadOnlyList<ZepFolderEntry> folders)
        {
            var count = 0;
            for (var i = 0; i < folders.Count; i++)
            {
                if (folders[i].Depth > 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static IReadOnlyList<ZepTypeFilterOption> BuildTypeFilterOptions(
            IReadOnlyDictionary<string, int> typeCounts,
            int folderCount)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in typeCounts)
            {
                counts[entry.Key] = entry.Value;
            }
            if (folderCount > 0)
            {
                counts["Folder"] = folderCount;
            }

            var options = new List<ZepTypeFilterOption>(counts.Count + 1);
            var totalCount = 0;
            foreach (var entry in counts)
            {
                options.Add(new ZepTypeFilterOption(entry.Key, entry.Value));
                totalCount += entry.Value;
            }

            options.Sort(static (a, b) =>
            {
                var cmp = b.Count.CompareTo(a.Count);
                return cmp != 0 ? cmp : string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
            });

            options.Insert(0, new ZepTypeFilterOption(string.Empty, totalCount));
            return options;
        }
    }

    // Incremental candidate index for folder documents. AssetSearchService owns
    // file postings; this smaller sibling lives with the folder browse projection
    // because folders deliberately never enter the file index. It returns only a
    // safe superset. The shared SearchQuery matcher remains the single grammar and
    // performs the authoritative check before a folder reaches the result view.
    internal sealed class FolderSearchIndex
    {
        // The only gram length the postings hold. Shorter tokens have no posting list
        // at all and fall back to the full folder set (audit §3.12).
        private const int MinimumIndexedGramLength = 3;

        private static readonly IReadOnlyCollection<string> EmptyPaths =
            Array.Empty<string>();

        private readonly Dictionary<string, ZepFolderEntry> _foldersByPath =
            new Dictionary<string, ZepFolderEntry>(
                StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, HashSet<string>> _anyPostings =
            new Dictionary<ulong, HashSet<string>>();
        private readonly Dictionary<ulong, HashSet<string>> _namePostings =
            new Dictionary<ulong, HashSet<string>>();
        private readonly Dictionary<ulong, HashSet<string>> _labelPostings =
            new Dictionary<ulong, HashSet<string>>();
        private readonly Dictionary<ulong, HashSet<string>> _guidPostings =
            new Dictionary<ulong, HashSet<string>>();

        internal int LastCandidateCount { get; private set; }

        internal void Replace(
            IReadOnlyList<ZepFolderEntry> folders)
        {
            _foldersByPath.Clear();
            _anyPostings.Clear();
            _namePostings.Clear();
            _labelPostings.Clear();
            _guidPostings.Clear();

            if (folders == null)
            {
                return;
            }

            for (var i = 0; i < folders.Count; i++)
            {
                Upsert(folders[i]);
            }
        }

        internal void Upsert(ZepFolderEntry folder)
        {
            if (folder == null
                || string.IsNullOrEmpty(folder.Path))
            {
                return;
            }

            Remove(folder.Path);
            _foldersByPath[folder.Path] = folder;
            UpdatePostings(folder, add: true);
        }

        internal void Remove(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)
                || !_foldersByPath.TryGetValue(folderPath, out var folder))
            {
                return;
            }

            UpdatePostings(folder, add: false);
            _foldersByPath.Remove(folderPath);
        }

        internal IEnumerable<ZepFolderEntry> GetCandidates(
            ParsedSearchQuery query,
            TagProjectionService tags)
        {
            var candidatePaths = FindCandidatePaths(query, tags);
            LastCandidateCount = candidatePaths.Count;
            foreach (var path in candidatePaths)
            {
                if (_foldersByPath.TryGetValue(path, out var folder))
                {
                    yield return folder;
                }
            }
        }

        private IReadOnlyCollection<string> FindCandidatePaths(
            ParsedSearchQuery query,
            TagProjectionService tags)
        {
            if (query == null || query.IsEmpty)
            {
                return _foldersByPath.Keys;
            }

            if (query.MatchesNothing)
            {
                return EmptyPaths;
            }

            IReadOnlyCollection<string> best = null;
            for (var i = 0; i < query.RequiredTerms.Count; i++)
            {
                if (!TryGetTokenCandidates(
                    query.RequiredTerms[i],
                    tags,
                    out var candidates))
                {
                    continue;
                }

                if (candidates.Count == 0)
                {
                    return EmptyPaths;
                }

                if (best == null || candidates.Count < best.Count)
                {
                    best = candidates;
                }
            }

            if (query.LabelTerms.Count > 0)
            {
                var labelCandidates = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < query.LabelTerms.Count; i++)
                {
                    var candidates = GetMetadataCandidates(
                        query.LabelTerms[i],
                        _labelPostings);
                    labelCandidates.UnionWith(candidates);
                }

                if (labelCandidates.Count == 0)
                {
                    return EmptyPaths;
                }

                if (best == null || labelCandidates.Count < best.Count)
                {
                    best = labelCandidates;
                }
            }

            return best ?? _foldersByPath.Keys;
        }

        // Test hook: how many candidate sets the token resolver allocated. An
        // ordinary search word must cost none.
        internal int TokenCandidateSetsAllocated;

        private bool TryGetTokenCandidates(
            SearchToken token,
            TagProjectionService tags,
            out IReadOnlyCollection<string> candidates)
        {
            if (token.MatchMode == SearchMatchMode.Glob)
            {
                candidates = null;
                return false;
            }

            switch (token.Field)
            {
                case SearchField.Any:
                    if (token.MatchMode == SearchMatchMode.Exact)
                    {
                        candidates = GetMetadataCandidates(
                            token,
                            _namePostings);
                        return true;
                    }

                    var metadata = GetMetadataCandidates(
                        token,
                        _anyPostings);
                    if (tags == null || !tags.HasPathsMatchingSearchToken(token))
                    {
                        // The ordinary word: nobody's tag name or colour, so
                        // the postings already are the answer and no set is
                        // allocated for it at all.
                        candidates = metadata;
                        return true;
                    }

                    // A tag hit adds a handful of folders to a candidate set
                    // that can be the whole catalog, so the big side is copied
                    // once and the small one added to it — never the reverse.
                    TokenCandidateSetsAllocated++;
                    var tagged = new HashSet<string>(
                        metadata,
                        StringComparer.OrdinalIgnoreCase);
                    tags.AppendPathsMatchingSearchToken(token, tagged);
                    candidates = tagged;
                    return true;
                case SearchField.Name:
                    candidates = GetMetadataCandidates(
                        token,
                        _namePostings);
                    return true;
                case SearchField.Path:
                case SearchField.Directory:
                    // No path postings: a full path posted ~100 trigrams into
                    // HashSet<string> postings — two thirds of the folder index's
                    // memory (audit §3.12) — to serve three qualifiers nobody
                    // types on a hot path. These are candidates, not answers, so
                    // the whole folder set is a correct (if wider) input and
                    // FolderMatchesSearch decides exactly as it did before.
                    candidates = _foldersByPath.Keys;
                    return true;
                case SearchField.Label:
                    candidates = GetMetadataCandidates(
                        token,
                        _labelPostings);
                    return true;
                case SearchField.Guid:
                    candidates = GetMetadataCandidates(
                        token,
                        _guidPostings);
                    return true;
                case SearchField.Tag:
                    TokenCandidateSetsAllocated++;
                    var tagCandidates = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    tags?.AppendPathsMatchingSearchToken(
                        token,
                        tagCandidates);
                    candidates = tagCandidates;
                    return true;
                case SearchField.Area:
                    if (string.Equals(
                        token.Value,
                        "all",
                        StringComparison.Ordinal))
                    {
                        candidates = null;
                        return false;
                    }

                    // Same as path:/dir: above — the area is read off the path.
                    candidates = _foldersByPath.Keys;
                    return true;
                case SearchField.Extension:
                    candidates = EmptyPaths;
                    return true;
                default:
                    candidates = null;
                    return false;
            }
        }

        private IReadOnlyCollection<string> GetMetadataCandidates(
            SearchToken token,
            Dictionary<ulong, HashSet<string>> postings)
        {
            if (string.IsNullOrEmpty(token.Value))
            {
                return EmptyPaths;
            }

            if (token.Value.Length < MinimumIndexedGramLength)
            {
                // Nothing shorter than a trigram is posted, so there is no candidate
                // set to narrow with — hand every folder to the authoritative matcher.
                // This is the same "no useful candidate" fallback FindCandidatePaths
                // already takes when no term resolves to a posting list.
                return _foldersByPath.Keys;
            }

            var gramLength = MinimumIndexedGramLength;
            var gramCount = token.Value.Length - gramLength + 1;
            if (gramCount == 1)
            {
                return postings.TryGetValue(PackGram(token.Value, 0, gramLength), out var onlyMatches)
                    ? (IReadOnlyCollection<string>)onlyMatches
                    : EmptyPaths;
            }

            // Resolve every gram's posting list ONCE. The intersection loop below
            // used to re-pack the gram and re-probe the dictionary for each of
            // its grams for EVERY candidate path — an O(|smallest| x grams) run
            // of full-string hashes per keystroke against an answer that cannot
            // change inside the loop.
            var gramMatches = new HashSet<string>[gramCount];
            HashSet<string> smallest = null;
            var smallestIndex = 0;
            for (var i = 0; i < gramCount; i++)
            {
                var gram = PackGram(token.Value, i, gramLength);
                if (!postings.TryGetValue(gram, out var matches))
                {
                    return EmptyPaths;
                }

                gramMatches[i] = matches;
                if (smallest == null || matches.Count < smallest.Count)
                {
                    smallest = matches;
                    smallestIndex = i;
                }
            }

            // Long terms usually have a tiny intersection even when one gram is
            // common (for example the repeated digits in generated folder names).
            // Intersect only the smallest posting and keep the result as a safe
            // superset; the shared grammar matcher still performs final authority.
            var intersection = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in smallest)
            {
                var matchesAllGrams = true;
                for (var i = 0; i < gramCount; i++)
                {
                    // Every path here came out of gramMatches[smallestIndex].
                    if (i == smallestIndex)
                    {
                        continue;
                    }

                    if (!gramMatches[i].Contains(path))
                    {
                        matchesAllGrams = false;
                        break;
                    }
                }

                if (matchesAllGrams)
                {
                    intersection.Add(path);
                }
            }

            return intersection;
        }

        // The path itself is deliberately NOT posted; see the path:/dir:/area:
        // legs in TryGetTokenCandidates.
        private void UpdatePostings(
            ZepFolderEntry folder,
            bool add)
        {
            var path = folder.Path;
            UpdateField(_namePostings, folder.NormalizedName, path, add);
            UpdateField(_labelPostings, folder.NormalizedUnityLabelSearchText, path, add);
            UpdateField(_guidPostings, folder.NormalizedGuid, path, add);

            UpdateField(_anyPostings, folder.NormalizedName, path, add);
            UpdateField(_anyPostings, "folder", path, add);
            UpdateField(_anyPostings, "object", path, add);
            UpdateField(_anyPostings, folder.NormalizedGuid, path, add);
            UpdateField(
                _anyPostings,
                folder.NormalizedUnityLabelSearchText,
                path,
                add);
        }

        private static void UpdateField(
            Dictionary<ulong, HashSet<string>> postings,
            string value,
            string path,
            bool add)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            // Trigrams only. The 1- and 2-gram levels were two thirds of the postings
            // (~15 KB per folder, 146 MB at 10k folders) and bought almost nothing:
            // at 10k folders "p" selected 2,022 candidates and "pl" 674, against the
            // 674 the trigram level alone gives for "pla". A token too short to have
            // a trigram now falls back to every folder in GetMetadataCandidates, and
            // the authoritative folder matcher decides as it always did (audit §3.12).
            var end = value.Length - MinimumIndexedGramLength;
            for (var i = 0; i <= end; i++)
            {
                var gram = PackGram(value, i, MinimumIndexedGramLength);
                if (add)
                {
                    if (!postings.TryGetValue(gram, out var matches))
                    {
                        matches = new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);
                        postings[gram] = matches;
                    }

                    matches.Add(path);
                }
                else if (postings.TryGetValue(gram, out var matches))
                {
                    matches.Remove(path);
                    if (matches.Count == 0)
                    {
                        postings.Remove(gram);
                    }
                }
            }
        }

        private static ulong PackGram(
            string value,
            int start,
            int length)
        {
            var packed = (ulong)length << 60;
            for (var i = 0; i < length; i++)
            {
                packed |= (ulong)value[start + i] << (32 - (i * 16));
            }

            return packed;
        }
    }
}
