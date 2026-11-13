using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    /// <summary>
    /// The path-keyed selection of one asset view and the rules that change it:
    /// plain, toggle and range clicks, keyboard range extension, select-all,
    /// type-ahead, and the marquee preview. It knows nothing about rows, tiles
    /// or scrolling; the view hands it the flat order selection runs over.
    /// </summary>
    public sealed class AssetSelectionModel
    {
        // Order (list) + O(1) membership (set, drives per-entry checks in
        // GetSelectedEntries and click resolution).
        private readonly List<AssetPath> _paths = new List<AssetPath>();
        private readonly HashSet<AssetPath> _pathSet = new HashSet<AssetPath>();

        // Explicit shift-selection endpoints. The pivot is the fixed item a range
        // extends FROM (last plain click / range start); the lead is the moving end
        // the cursor sits on. Held as their own fields — NOT inferred from the
        // list's first/last — because the view's SelectionChanged→SetSelection
        // round-trip re-pushes the selection in index order, which would otherwise
        // drift the anchor to the top-most item after the first extension. Reads
        // fall back to the selection's ends when these go stale (external replace).
        private AssetPath _pivot;
        private AssetPath _lead;

        /// <summary>The selected paths in the order they were added.</summary>
        public IReadOnlyList<AssetPath> Paths => _paths;

        /// <summary>
        /// The same paths as a set. The live instance, not a copy: the widgets
        /// read it by reference instead of rebuilding it on every push.
        /// </summary>
        public IReadOnlyCollection<AssetPath> PathSet => _pathSet;

        public int Count => _paths.Count;

        public bool Contains(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && _pathSet.Contains(AssetPath.From(path));
        }

        /// <summary>Empties the selection and forgets the pivot and lead.</summary>
        public void Clear()
        {
            _paths.Clear();
            _pathSet.Clear();
            _pivot = default;
            _lead = default;
        }

        /// <summary>Replaces the selected paths; the pivot and lead are left alone.</summary>
        public void ReplaceWith(IReadOnlyCollection<AssetPath> selectedAssetPaths)
        {
            _paths.Clear();
            _pathSet.Clear();
            if (selectedAssetPaths != null)
            {
                foreach (var p in selectedAssetPaths)
                {
                    if (!p.IsEmpty && _pathSet.Add(p))
                    {
                        _paths.Add(p);
                    }
                }
            }
        }

        /// <summary>Replaces the selected paths; the pivot and lead are left alone.</summary>
        public void ReplaceWithPaths(IReadOnlyList<string> paths)
        {
            _paths.Clear();
            _pathSet.Clear();
            if (paths == null) return;
            for (var i = 0; i < paths.Count; i++)
            {
                var ap = AssetPath.From(paths[i]);
                if (!ap.IsEmpty && _pathSet.Add(ap))
                {
                    _paths.Add(ap);
                }
            }
        }

        // Mid-drag marquee preview, expressed as what changed since the previous
        // move. The selection stays live (a cancel has to be able to restore
        // over it) but is no longer rebuilt from scratch per move.
        public void ApplyPreview(
            IReadOnlyList<string> added,
            IReadOnlyList<string> removed)
        {
            var removedAny = false;
            if (removed != null)
            {
                for (var i = 0; i < removed.Count; i++)
                {
                    var path = AssetPath.From(removed[i]);
                    removedAny |= !path.IsEmpty && _pathSet.Remove(path);
                }
            }

            if (removedAny)
            {
                // The ordered list is the one structure a removal cannot touch
                // in place, so a shrinking move compacts it once instead of
                // once per dropped row. A growing move never gets here.
                var write = 0;
                for (var read = 0; read < _paths.Count; read++)
                {
                    var path = _paths[read];
                    if (_pathSet.Contains(path))
                    {
                        _paths[write++] = path;
                    }
                }

                _paths.RemoveRange(write, _paths.Count - write);
            }

            if (added != null)
            {
                for (var i = 0; i < added.Count; i++)
                {
                    var path = AssetPath.From(added[i]);
                    if (!path.IsEmpty && _pathSet.Add(path))
                    {
                        _paths.Add(path);
                    }
                }
            }
        }

        /// <summary>
        /// Puts the pivot on the first and the lead on the last of
        /// <paramref name="paths"/>; nothing changes when it is empty.
        /// </summary>
        public void AnchorAtEnds(IReadOnlyList<string> paths)
        {
            if (paths != null && paths.Count > 0)
            {
                _pivot = AssetPath.From(paths[0]);
                _lead = AssetPath.From(paths[paths.Count - 1]);
            }
        }

        public void SetPivotAndLead(string path)
        {
            var typed = AssetPath.From(path);
            _pivot = typed;
            _lead = typed;
        }

        /// <summary>The selected entries in the order's own sequence.</summary>
        public List<ZepAssetEntry> GetSelectedEntries(IReadOnlyList<ZepAssetEntry> order)
        {
            var result = new List<ZepAssetEntry>(_paths.Count);
            if (_pathSet.Count == 0) return result;

            if (order is IAssetPathLookup)
            {
                // Selection-first: ask the order where each of the k selected
                // paths sits, then materialize in visible order. Walking the
                // order instead hashed one path per row — ~30k hashes for a
                // single click at the bottom of a large folder, on every
                // click, arrow step and marquee commit (audit 4.1).
                //
                // Duplicate paths inside one order resolve to their first
                // occurrence, exactly as the scan below does; the selection
                // itself cannot repeat a path (the replace methods dedupe
                // through the set, which is case-insensitive).
                var indices = new List<int>(_paths.Count);
                for (var i = 0; i < _paths.Count; i++)
                {
                    var index = ZepPathHelpers.FindAssetPathIndex(
                        order,
                        _paths[i].Value);
                    if (index >= 0) indices.Add(index);
                }

                indices.Sort();
                for (var i = 0; i < indices.Count; i++)
                {
                    result.Add(order[indices[i]]);
                }

                return result;
            }

            // Bare arrays (a plain browse projection) index nothing, so the
            // fallback is the path-first probe: lazy wrappers return the path
            // without constructing an entry, so we only allocate entries for
            // the (usually small) set that actually matches the selection.
            // Stops once every selected path has been matched.
            for (var i = 0; i < order.Count && result.Count < _pathSet.Count; i++)
            {
                var path = ZepPathHelpers.GetAssetPathAt(order, i);
                if (string.IsNullOrEmpty(path)) continue;
                if (_pathSet.Contains(AssetPath.From(path)))
                {
                    result.Add(order[i]);
                }
            }

            return result;
        }

        public void CapturePaths(List<string> target)
        {
            target.Clear();
            for (var i = 0; i < _paths.Count; i++)
            {
                target.Add(_paths[i].Value);
            }
        }

        // Click-selection resolver: applies cmd-toggle, shift-range, or
        // replace semantics over the current order and selection, and moves
        // the pivot and lead accordingly. Returns the paths the click selects;
        // the caller commits them. The order is asked for only by a range
        // click: producing it can be work, and a plain click never needs it.
        public List<string> ResolveClick(
            Func<IReadOnlyList<ZepAssetEntry>> order,
            string clickedPath,
            bool toggle,
            bool range)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(clickedPath)) return result;

            if (range)
            {
                var rangePaths = BuildRange(order(), clickedPath);
                if (rangePaths.Count > 0)
                {
                    // Range extension: the pivot stays put; the clicked item
                    // becomes the new moving end (lead).
                    _lead = AssetPath.From(clickedPath);
                    return rangePaths;
                }
            }

            if (toggle)
            {
                var clicked = AssetPath.From(clickedPath);
                var remove = _pathSet.Contains(clicked);
                for (var i = 0; i < _paths.Count; i++)
                {
                    if (!remove || !_paths[i].Equals(clicked))
                    {
                        result.Add(_paths[i].Value);
                    }
                }

                if (!remove)
                {
                    result.Add(clickedPath);
                    // A cmd-click that ADDS drops a fresh anchor there (Finder).
                    SetPivotAndLead(clickedPath);
                }

                return result;
            }

            result.Add(clickedPath);
            // Plain click: new fixed pivot.
            SetPivotAndLead(clickedPath);
            return result;
        }

        /// <summary>
        /// Every path of the order, for select-all, with the pivot at the top
        /// so a following shift-arrow extends predictably.
        /// </summary>
        public List<string> SelectAllPaths(IReadOnlyList<ZepAssetEntry> order)
        {
            var paths = new List<string>(order.Count);
            for (var i = 0; i < order.Count; i++)
            {
                var path = ZepPathHelpers.GetAssetPathAt(order, i);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }

            AnchorAtEnds(paths);
            return paths;
        }

        // Locate the shift-selection's anchor (pivot) and lead in the given
        // flat order. Either lands at -1 when its path isn't in the list
        // (selection carried over from another projection).
        //
        // Through the shared resolver, so an order that indexes its paths
        // (the expanded list, search results, a browse bucket) answers both in
        // O(1)/O(log n). Walking the order instead cost one compare per row of
        // the folder on every arrow key and shift-click (audit 4.2).
        public void ResolveAnchorAndLeadIndices(
            IReadOnlyList<ZepAssetEntry> order,
            out int anchorIndex,
            out int leadIndex)
        {
            anchorIndex = -1;
            leadIndex = -1;
            if (_paths.Count == 0) return;

            anchorIndex = ZepPathHelpers.FindAssetPathIndex(order, ResolvePivotPath());
            leadIndex = ZepPathHelpers.FindAssetPathIndex(order, ResolveLeadPath());
        }

        /// <summary>
        /// The paths from the anchor to <paramref name="targetIndex"/> inclusive,
        /// in walking order, for a keyboard range extension. Keeps the pivot and
        /// moves only the lead to <paramref name="targetPath"/>.
        /// </summary>
        public List<string> ExtendRangeTo(
            IReadOnlyList<ZepAssetEntry> order,
            int anchorIndex,
            int targetIndex,
            string targetPath)
        {
            var next = new List<string>(Math.Abs(targetIndex - anchorIndex) + 1);
            var step = targetIndex >= anchorIndex ? 1 : -1;
            for (var i = anchorIndex; ; i += step)
            {
                var path = ZepPathHelpers.GetAssetPathAt(order, i);
                if (!string.IsNullOrEmpty(path)) next.Add(path);
                if (i == targetIndex) break;
            }

            _lead = AssetPath.From(targetPath);
            return next;
        }

        // Type-ahead: the first asset whose file name starts with prefix
        // (ordinal, case-insensitive). Matches on the path's last segment so
        // lazy entry wrappers never materialize during the scan. Returns -1
        // when nothing matches.
        public static int FindFirstNameMatch(
            IReadOnlyList<ZepAssetEntry> order,
            string prefix,
            out string matchedPath)
        {
            for (var i = 0; i < order.Count; i++)
            {
                var path = ZepPathHelpers.GetAssetPathAt(order, i);
                if (string.IsNullOrEmpty(path)) continue;

                var nameStart = path.LastIndexOf('/') + 1;
                if (path.Length - nameStart < prefix.Length) continue;
                if (string.Compare(path, nameStart, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;

                matchedPath = path;
                return i;
            }

            matchedPath = string.Empty;
            return -1;
        }

        // The pivot a shift-extension anchors on. Prefer the explicit pivot while
        // it is still part of the selection; when it goes stale (selection replaced
        // externally) fall back to the selection's first path — the historical
        // anchor before the pivot field existed.
        private string ResolvePivotPath()
        {
            if (!_pivot.IsEmpty && _pathSet.Contains(_pivot))
            {
                return _pivot.Value;
            }

            return _paths.Count > 0 ? _paths[0].Value : string.Empty;
        }

        // The moving end the cursor sits on. Same stale-fallback rule as the pivot,
        // falling back to the selection's last path.
        private string ResolveLeadPath()
        {
            if (!_lead.IsEmpty && _pathSet.Contains(_lead))
            {
                return _lead.Value;
            }

            return _paths.Count > 0 ? _paths[_paths.Count - 1].Value : string.Empty;
        }

        private List<string> BuildRange(
            IReadOnlyList<ZepAssetEntry> order,
            string clickedPath)
        {
            var result = new List<string>();
            // Extend from the explicit pivot (survives the SetSelection round-trip);
            // if there's no pivot yet (first-ever shift-click), fall back to the top.
            var anchorPath = ResolvePivotPath();
            if (string.IsNullOrWhiteSpace(anchorPath))
            {
                anchorPath = order.Count > 0 ? ZepPathHelpers.GetAssetPathAt(order, 0) : string.Empty;
            }
            if (string.IsNullOrWhiteSpace(anchorPath)) return result;

            // Both ends through the shared resolver: an order that indexes its
            // paths answers each in O(1)/O(log n), and the helper hides the
            // lazy/eager difference (no entry is materialized to compare).
            var anchorIndex = ZepPathHelpers.FindAssetPathIndex(order, anchorPath);
            var clickedIndex = ZepPathHelpers.FindAssetPathIndex(order, clickedPath);
            if (anchorIndex < 0 || clickedIndex < 0) return result;

            var start = Math.Min(anchorIndex, clickedIndex);
            var end = Math.Max(anchorIndex, clickedIndex);
            for (var i = start; i <= end; i++)
            {
                result.Add(ZepPathHelpers.GetAssetPathAt(order, i));
            }

            return result;
        }
    }
}
