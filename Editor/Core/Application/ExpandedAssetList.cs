using Zep.Core.Domain;
using System.Collections.Generic;
using System;

namespace Zep.Core.Application
{
    // The flattened row order the LIST widget renders while folder expansion
    // is active: every base entry, each expanded folder's children spliced in
    // directly below it (recursively), each row carrying its indent depth and
    // chevron state. Built by AssetCollectionView from the browse projection's
    // per-folder cache, consumed by ListAssetWidget at bind time — it lives in
    // this contract file because it is the one data shape both sides share.
    // Entries are the projection-cache instances, so expanded rows behave
    // exactly like the same assets browsed directly.
    public sealed class ExpandedAssetList
        : IReadOnlyList<ZepAssetEntry>, IAssetPathLookup
    {
        public readonly struct Row
        {
            public Row(ZepAssetEntry entry, int depth, bool isExpanded, bool showsChevron)
            {
                Entry = entry;
                Depth = depth;
                IsExpanded = isExpanded;
                ShowsChevron = showsChevron;
            }

            internal ZepAssetEntry Entry { get; }
            internal int Depth { get; }
            internal bool IsExpanded { get; }
            internal bool ShowsChevron { get; }
        }

        private readonly List<Row> _rows;
        // Path → row index, built with the flatten it describes. Every
        // keystroke resolves a handful of paths against this order (the
        // keyboard cursor, the grid's expansion remap, reveal/ping), and a
        // 50k-row list answered each of them with a full scan.
        private readonly Dictionary<string, int> _indexByPath;

        public ExpandedAssetList(List<Row> rows)
        {
            _rows = rows;
            _indexByPath = new Dictionary<string, int>(rows.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                var path = rows[i].Entry?.Path;
                // First occurrence wins, the way the scan it replaces did. A
                // repeat can only come from a projection that lists the same
                // asset twice; the row order still renders both.
                if (!string.IsNullOrEmpty(path) && !_indexByPath.ContainsKey(path))
                {
                    _indexByPath.Add(path, i);
                }
            }
        }

        // Test hook: how often this order was asked to resolve a path.
        internal int IndexOfPathCallCount { get; private set; }

        public int IndexOfPath(string assetPath)
        {
            IndexOfPathCallCount++;
            return !string.IsNullOrEmpty(assetPath)
                && _indexByPath.TryGetValue(assetPath, out var index)
                ? index
                : -1;
        }

        public int Count => _rows.Count;
        public ZepAssetEntry this[int index] => _rows[index].Entry;
        public int DepthAt(int index) => _rows[index].Depth;
        public bool IsExpandedFolderAt(int index) => _rows[index].IsExpanded;
        public bool ShowsChevronAt(int index) => _rows[index].ShowsChevron;

        public IEnumerator<ZepAssetEntry> GetEnumerator()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                yield return _rows[i].Entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}
