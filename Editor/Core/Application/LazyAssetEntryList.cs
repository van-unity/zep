using System;
using System.Collections;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.Search;

namespace Zep.Core.Application
{
    // Projection lists that can locate a path without materializing their
    // entries expose that operation here. Callers use the shared helper rather
    // than knowing whether a projection uses sorted lookup or a record scan.
    public interface IAssetPathLookup
    {
        int IndexOfPath(string assetPath);
    }

    // Lazy projection of asset-search results.
    //
    // The trigram search engine returns IReadOnlyList<AssetRecord> for tens of
    // thousands of matches on a short query; eagerly materializing one
    // ZepAssetEntry per record is the dominant per-keystroke cost on a
    // large project (30k allocations + 60k tag-dictionary lookups for a
    // 1-letter query that the user can't even read).
    //
    // This wrapper holds the records plus the tag projection and constructs
    // entries on demand when the indexer is hit. The UI ListView virtualizer
    // only touches ~20 indices per render, so allocation drops from
    // O(matches) to O(visible_rows) per search.
    //
    // PathAt and ContainsPath give callers (selection reconciliation,
    // selection-index resolution, marquee row math) an allocation-free path
    // lookup so they don't have to materialize entries just to compare paths.
    //
    // Implements IList because Unity ListView.itemsSource is typed IList and
    // we want it to consume the wrapper directly without an intermediate copy.
    internal sealed class LazyAssetEntryList
        : IList<ZepAssetEntry>, IReadOnlyList<ZepAssetEntry>, IList,
          IAssetPathLookup
    {
        private readonly ZepAssetEntry[] _folderEntries;
        private readonly IReadOnlyList<AssetRecord> _fileRecords;
        private readonly TagProjectionService _tags;
        // Sparse cache: most search results are scrolled-past or never bound.
        // A Dictionary keyed by index keeps memory at O(touched indices), not
        // O(matches). 20 visible rows -> ~20 cached entries (~1 KB).
        private readonly Dictionary<int, ZepAssetEntry> _fileCache;
        private int[] _rankedOrder;

        internal void RankByName(ParsedSearchQuery query)
        {
            _rankedOrder = null;
            _indexByPath = null;
            var hasName = false;
            foreach (var term in query.RequiredTerms)
                hasName |= term.Field == SearchField.Any || term.Field == SearchField.Name;
            if (!hasName || Count < 2) return;
            var order = new int[Count];
            var scores = new int[Count];
            string NameAt(int i) => i < _folderEntries.Length
                ? _folderEntries[i].Name : _fileRecords[i - _folderEntries.Length].Name;
            for (var i = 0; i < Count; i++)
            {
                order[i] = i;
                scores[i] = SearchQuery.NameMatchRank(NameAt(i), query);
            }
            Array.Sort(order, (a, b) =>
            {
                var rank = scores[a].CompareTo(scores[b]);
                if (rank != 0) return rank;
                var name = string.Compare(NameAt(a), NameAt(b), StringComparison.OrdinalIgnoreCase);
                return name != 0 ? name : string.Compare(PathAt(a), PathAt(b), StringComparison.OrdinalIgnoreCase);
            });
            _rankedOrder = order;
            _indexByPath = null;
        }

        internal LazyAssetEntryList(
            ZepAssetEntry[] folderEntries,
            IReadOnlyList<AssetRecord> fileRecords,
            TagProjectionService tags)
        {
            _folderEntries = folderEntries ?? Array.Empty<ZepAssetEntry>();
            _fileRecords = fileRecords ?? (IReadOnlyList<AssetRecord>)Array.Empty<AssetRecord>();
            _tags = tags ?? throw new ArgumentNullException(nameof(tags));
            _fileCache = new Dictionary<int, ZepAssetEntry>();
        }

        public int Count => _folderEntries.Length + _fileRecords.Count;

        // Diagnostics: counts how many indexer accesses happened since the
        // wrapper was built. Logged when GetEnumerator is called (so we can
        // see if anyone is foreach-ing the whole thing) and exposed to
        // external callers via MaterializedCount.
        private int _indexerAccessCount;
        internal int MaterializedCount => _fileCache.Count;
        internal int IndexerAccessCount => _indexerAccessCount;

        public ZepAssetEntry this[int index]
        {
            get
            {
                _indexerAccessCount++;
                if (_rankedOrder != null) index = _rankedOrder[index];
                if (index < _folderEntries.Length)
                {
                    return _folderEntries[index];
                }

                var fileIndex = index - _folderEntries.Length;
                if (_fileCache.TryGetValue(fileIndex, out var cached))
                {
                    return cached;
                }

                var record = _fileRecords[fileIndex];
                var entry = new ZepAssetEntry(
                    record.Name,
                    record.AssetPath,
                    record.TypeName,
                    tags: _tags.GetTagsForPath(record.AssetPath),
                    folderTintColor: _tags.GetFolderTintColorForPath(record.AssetPath, record.TypeName));
                _fileCache[fileIndex] = entry;
                ZepPerfLog.Count(
                    ZepPerfCounter.SearchEntriesMaterialized);
                return entry;
            }
            set => throw new NotSupportedException();
        }

        // Allocation-free path lookup at an index. Used by selection-reconciliation,
        // marquee row math, and any code path that needs the path without the
        // (heap-allocated) entry around it.
        internal string PathAt(int index)
        {
            if (_rankedOrder != null) index = _rankedOrder[index];
            if (index < _folderEntries.Length)
            {
                return _folderEntries[index].Path;
            }

            return _fileRecords[index - _folderEntries.Length].AssetPath;
        }

        internal bool ContainsPath(string assetPath)
        {
            return IndexOfPath(assetPath) >= 0;
        }

        // Built on the first IndexOfPath, not in the constructor: the vast
        // majority of these wrappers are built for a keystroke and thrown away
        // without anyone ever resolving a path in them (only reveal, ping,
        // selection reconciliation and marquee row math ask). Reading a path out
        // of a record allocates nothing, so this costs one dictionary of the
        // strings the projection already holds, once, for the callers that do
        // ask — instead of a full scan of tens of thousands of records per call.
        // OrdinalIgnoreCase, like every other path identity in the panel.
        private Dictionary<string, int> _indexByPath;

        public int IndexOfPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return -1;
            }

            if (_indexByPath == null)
            {
                _indexByPath = new Dictionary<string, int>(
                    Count,
                    StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < Count; i++)
                {
                    var path = PathAt(i);
                    if (!string.IsNullOrEmpty(path) && !_indexByPath.ContainsKey(path)) _indexByPath[path] = i;
                }
            }

            return _indexByPath.TryGetValue(assetPath, out var index) ? index : -1;
        }

        public IEnumerator<ZepAssetEntry> GetEnumerator()
        {
            // Big alarm: someone is foreach-ing the lazy wrapper, which
            // forces every entry to materialize. The whole point of the
            // wrapper is to avoid that. Logged so we can find the caller.
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] LazyAssetEntryList.GetEnumerator called Count={Count} alreadyMaterialized={_fileCache.Count} — caller about to materialize EVERY entry");
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsReadOnly => true;

        // These three used to materialize EVERY entry — the one thing this class
        // exists to prevent — and did it silently, unlike GetEnumerator which at
        // least logs. They are reachable through IList/ICollection<T> plumbing
        // that nobody would recognise as a materialisation: List.AddRange,
        // Enumerable.ToList/ToArray/Contains all take the ICollection<T> route.
        // No production caller reaches them today (both asset widgets are
        // SelectionType.None and bind their own row source, not this list), so
        // the throw costs nothing and documents the contract the way the
        // mutators below already do. Callers that need a path use PathAt,
        // ContainsPath or IndexOfPath.
        public int IndexOf(ZepAssetEntry item) => throw new NotSupportedException(
            "LazyAssetEntryList.IndexOf would materialize every entry; use IndexOfPath.");

        public bool Contains(ZepAssetEntry item) => throw new NotSupportedException(
            "LazyAssetEntryList.Contains would materialize every entry; use ContainsPath.");

        public void CopyTo(ZepAssetEntry[] array, int arrayIndex) => throw new NotSupportedException(
            "LazyAssetEntryList.CopyTo would materialize every entry; index the visible rows instead.");

        public void Add(ZepAssetEntry item) => throw new NotSupportedException();
        public void Insert(int index, ZepAssetEntry item) => throw new NotSupportedException();
        public bool Remove(ZepAssetEntry item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();

        bool IList.IsFixedSize => true;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;
        object IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        int IList.Add(object value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object value) => throw new NotSupportedException(
            "LazyAssetEntryList.Contains would materialize every entry; use ContainsPath.");
        int IList.IndexOf(object value) => throw new NotSupportedException(
            "LazyAssetEntryList.IndexOf would materialize every entry; use IndexOfPath.");
        void IList.Insert(int index, object value) => throw new NotSupportedException();
        void IList.Remove(object value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();
        void ICollection.CopyTo(Array array, int index) => throw new NotSupportedException(
            "LazyAssetEntryList.CopyTo would materialize every entry; index the visible rows instead.");
    }
}
