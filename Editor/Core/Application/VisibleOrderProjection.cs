using System;
using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.Application
{
    // The flat VISIBLE cell order both asset widgets render: every published
    // main asset, with the resolved children of each open sub-asset
    // disclosure spliced in directly after their parent.
    //
    // One instance, owned by AssetCollectionView and handed to both widgets
    // (audit §6.1b). It used to be two byte-similar span implementations, one
    // per widget, which is why an expansion could not survive a list↔grid
    // switch and why the two modes drifted apart on the prune and stale-ping
    // rules. What is left in each widget on top of this is presentation only:
    // the list renders one cell per row, the grid divides the same cell index
    // by its column count and derives its strip-segment flags from the
    // sub-index/sub-count this class reports.
    //
    // Invariant: _spans is sorted by AssetIndex and each span's SubsBefore is
    // the running number of sub cells contributed by earlier spans, so
    // VisibleItemCount == Assets.Count + (sub cells) and the index math is two
    // adds plus one binary search. Every mutator re-establishes it through
    // Position() before returning.
    //
    // Not thread-safe and not meant to be: it is UI state, mutated from the
    // main thread by publishes, chevrons and catalog drains.
    //
    // TSub is the view's sub-asset descriptor. It carries editor objects Core
    // cannot name, so the projection takes the three things it needs from it:
    // the resolved children of a path, the same with a load request on a miss,
    // and a child's key.
    public sealed class VisibleOrderProjection<TSub> where TSub : class
    {
        private readonly Func<string, TSub[]> _getResolved;
        private readonly Func<string, TSub[]> _getOrRequest;
        private readonly Func<TSub, string> _keyOf;

        public VisibleOrderProjection(
            Func<string, TSub[]> getResolved,
            Func<string, TSub[]> getOrRequest,
            Func<TSub, string> keyOf)
        {
            _getResolved = getResolved ?? throw new ArgumentNullException(nameof(getResolved));
            _getOrRequest = getOrRequest ?? throw new ArgumentNullException(nameof(getOrRequest));
            _keyOf = keyOf ?? throw new ArgumentNullException(nameof(keyOf));
        }

        // One open disclosure, projected onto the order. Subs is never null (an
        // unresolved or empty parent contributes no cells but keeps its span,
        // so the chevron stays open). A reference type: the list and the
        // dictionary hold the SAME span, so a remap re-points one object
        // instead of keeping two copies in step.
        private sealed class ExpansionSpan
        {
            internal string ParentPath;
            internal int AssetIndex;
            internal int SubsBefore;
            internal TSub[] Subs;
        }

        private static readonly TSub[] NoSubAssets =
            Array.Empty<TSub>();
        private static readonly IReadOnlyList<ZepAssetEntry> EmptyAssets =
            Array.Empty<ZepAssetEntry>();

        // Sparse: one entry per OPEN disclosure, never one per asset.
        private readonly List<ExpansionSpan> _spans = new List<ExpansionSpan>();
        private readonly Dictionary<string, ExpansionSpan> _spanByPath =
            new Dictionary<string, ExpansionSpan>(StringComparer.Ordinal);
        // Scratch for RefreshResolvedSubs' zero-sub prune, reused so a catalog
        // drain allocates nothing. Handed out read-only and only valid until
        // the next RefreshResolvedSubs call.
        private readonly List<string> _prunedPaths = new List<string>();

        private IReadOnlyList<ZepAssetEntry> _assets = EmptyAssets;
        // The substrate's live open set, as last handed over. Read-only here.
        // Null until the substrate first pushes a non-empty set: a widget
        // driven directly (widget-level tests) then keeps the expansions it
        // opened locally instead of having them reconciled away.
        private IReadOnlyCollection<string> _hostExpandedPaths;
        private int _subCellCount;
        private string _pinnedParentPath = string.Empty;

        // The published order the cells project over. Held, not copied: it is
        // the lazy browse projection for search results and the flattened
        // folder-expansion order in list mode.
        public IReadOnlyList<ZepAssetEntry> Assets => _assets;

        // Cells in the visible order: main assets plus every open expansion's
        // resolved children.
        public int VisibleItemCount => _assets.Count + _subCellCount;

        // How many disclosures are open AND have a cell in the current order.
        public int ExpansionCount => _spans.Count;

        // The parent whose disclosure a pending Inspector ping is holding open
        // while its target is still unresolved. Exempt from the zero-sub prune
        // below, and dropped by SetAssets when its row leaves the order. Set by
        // whichever widget owns the pending ping (only the active view is ever
        // pinged) and cleared when that ping ends.
        public string PinnedParentPath
        {
            get => _pinnedParentPath;
            set => _pinnedParentPath = value ?? string.Empty;
        }

        // -------------------------------------------------------------------
        // Publishing
        // -------------------------------------------------------------------

        // Re-points the projection at a freshly published order. Costs one path
        // lookup per OPEN expansion (usually none): finding the handful of open
        // rows by scanning every path made every publish O(assets) for the rest
        // of the session once a single row had been opened (audit §4.4). Every
        // published order implements IAssetPathLookup, so a lookup is O(1); a
        // bare array falls back to the helper's scan.
        public void SetAssets(IReadOnlyList<ZepAssetEntry> assets)
        {
            _assets = assets ?? EmptyAssets;

            for (var i = _spans.Count - 1; i >= 0; i--)
            {
                var span = _spans[i];
                var assetIndex = FindPathIndex(span.ParentPath);
                if (assetIndex < 0)
                {
                    // The path left the order (navigation, a filter, a delete).
                    // No cell can carry its children any more, so the span goes
                    // rather than being re-checked on every later publish.
                    _spans.RemoveAt(i);
                    _spanByPath.Remove(span.ParentPath);
                    continue;
                }

                span.AssetIndex = assetIndex;
                span.Subs = _getResolved(span.ParentPath) ?? NoSubAssets;
            }

            // A pin names a row: once that row is gone the ping can never
            // complete, and a pin that never clears would keep exempting an
            // empty expansion from the prune for the rest of the session. The
            // widget that owns the ping notices the cleared pin and drops its
            // pending key with it. (Was grid-only before §6.1b.)
            if (_pinnedParentPath.Length > 0 && FindPathIndex(_pinnedParentPath) < 0)
            {
                _pinnedParentPath = string.Empty;
            }

            // A set member whose row was missing from the PREVIOUS order has no
            // span to remap; re-deriving here is what brings it back when the
            // path returns (navigation, a filter cleared).
            ReconcileToHostSet();
            Position();
        }

        // The substrate handing over its canonical open set. Returns whether
        // the cell shape moved, so the caller can skip a rebind that would
        // change nothing.
        public bool SetExpandedPaths(IReadOnlyCollection<string> expandedPaths)
        {
            _hostExpandedPaths = expandedPaths;
            if (!ReconcileToHostSet())
            {
                return false;
            }

            Position();
            return true;
        }

        // Makes the span set equal (host set ∩ current order): the substrate
        // decides membership, this class decides which members have a cell.
        private bool ReconcileToHostSet()
        {
            var host = _hostExpandedPaths;
            if (host == null)
            {
                return false;
            }

            var changed = false;
            for (var i = _spans.Count - 1; i >= 0; i--)
            {
                var span = _spans[i];
                if (HostContains(host, span.ParentPath))
                {
                    continue;
                }

                _spanByPath.Remove(span.ParentPath);
                _spans.RemoveAt(i);
                changed = true;
            }

            foreach (var path in host)
            {
                if (_spanByPath.ContainsKey(path))
                {
                    continue;
                }

                var assetIndex = FindPathIndex(path);
                if (assetIndex < 0)
                {
                    continue;
                }

                // GetOrRequest, not GetResolved: an expansion can be pushed for
                // a parent whose row is nowhere near the visible window, and
                // bind-time requests only cover realized cells.
                AddSpan(path, assetIndex, _getOrRequest(path));
                changed = true;
            }

            return changed;
        }

        // The open set is a handful of paths, so a scan beats materializing a
        // lookup per push — and it keeps the comparison Ordinal, matching the
        // span dictionary's keys.
        private static bool HostContains(IReadOnlyCollection<string> host, string path)
        {
            foreach (var candidate in host)
            {
                if (string.Equals(candidate, path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // -------------------------------------------------------------------
        // Expansion
        // -------------------------------------------------------------------

        public bool IsExpanded(string assetPath)
            => !string.IsNullOrEmpty(assetPath) && _spanByPath.ContainsKey(assetPath);

        // How many child cells follow this parent's cell right now: 0 for a
        // closed parent, and for an open one the catalog has not answered yet.
        public int ResolvedChildCount(string assetPath)
            => !string.IsNullOrEmpty(assetPath) && _spanByPath.TryGetValue(assetPath, out var span)
                ? span.Subs.Length
                : 0;

        // Open one disclosure locally — the programmatic reveal, and the only
        // route a widget driven without a substrate has. Kicks the catalog
        // itself: bind-time GetOrRequest only runs for cells inside the visible
        // window, and the parent may not be realized when the reveal lands.
        // Returns whether the cell shape moved.
        public bool Expand(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            var assetIndex = FindPathIndex(assetPath);
            if (assetIndex < 0)
            {
                return false;
            }

            var subs = _getOrRequest(assetPath) ?? NoSubAssets;
            if (_spanByPath.TryGetValue(assetPath, out var existing))
            {
                if (existing.AssetIndex == assetIndex
                    && ReferenceEquals(existing.Subs, subs))
                {
                    return false;
                }

                existing.AssetIndex = assetIndex;
                existing.Subs = subs;
                Position();
                return true;
            }

            AddSpan(assetPath, assetIndex, subs);
            Position();
            return true;
        }

        public bool Collapse(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)
                || !_spanByPath.TryGetValue(assetPath, out var span))
            {
                return false;
            }

            _spanByPath.Remove(assetPath);
            _spans.Remove(span);
            Position();
            return true;
        }

        private void AddSpan(string assetPath, int assetIndex, TSub[] subs)
        {
            var span = new ExpansionSpan
            {
                ParentPath = assetPath,
                AssetIndex = assetIndex,
                Subs = subs ?? NoSubAssets,
            };
            _spanByPath.Add(assetPath, span);
            _spans.Add(span);
        }

        // Re-establishes the invariant: spans in ascending AssetIndex order
        // with running SubsBefore offsets, and the sub-cell total both
        // VisibleItemCount and every caller's row extent are derived from.
        private void Position()
        {
            _spans.Sort(static (left, right) => left.AssetIndex.CompareTo(right.AssetIndex));
            _subCellCount = 0;
            for (var i = 0; i < _spans.Count; i++)
            {
                var span = _spans[i];
                span.SubsBefore = _subCellCount;
                _subCellCount += span.Subs.Length;
            }
        }

        // -------------------------------------------------------------------
        // Catalog
        // -------------------------------------------------------------------

        // Re-reads the resolved child lists after a catalog batch and reports
        // whether the cell shape moved: an expansion that just resolved (or was
        // invalidated) changes its cell count, and every index below it with it.
        // The asset order cannot change on this path, so spans keep their rows
        // and only their contents are re-read.
        //
        // ONE rule for both modes (§6.1b reconciliation): a disclosure whose
        // parent has RESOLVED to no sub-assets at all is closed, because its
        // chevron would otherwise claim children that do not exist — the grid's
        // rule, which the list lacked. The pinned parent is exempt, so a ping
        // whose target arrives in a later batch still gets its reveal. Returns
        // the paths dropped, for the caller to raise upward so the substrate's
        // set loses them too; the list is reused and valid only until the next
        // call.
        public IReadOnlyList<string> RefreshResolvedSubs(out bool shapeChanged)
        {
            _prunedPaths.Clear();
            shapeChanged = false;
            for (var i = _spans.Count - 1; i >= 0; i--)
            {
                var span = _spans[i];
                var resolved = _getResolved(span.ParentPath);
                if (resolved != null
                    && resolved.Length == 0
                    && !string.Equals(
                        span.ParentPath,
                        _pinnedParentPath,
                        StringComparison.Ordinal))
                {
                    _spans.RemoveAt(i);
                    _spanByPath.Remove(span.ParentPath);
                    _prunedPaths.Add(span.ParentPath);
                    shapeChanged = true;
                    continue;
                }

                var previousLength = span.Subs.Length;
                span.Subs = resolved ?? NoSubAssets;
                shapeChanged |= span.Subs.Length != previousLength;
            }

            if (shapeChanged)
            {
                Position();
            }

            return _prunedPaths;
        }

        // A pending ping holds its parent open even after the catalog answers
        // with a list that lacks the target. Once the parent HAS resolved and
        // the target is not in the answer the ping can never complete, so the
        // pin goes — the caller drops its pending key with it. (Was grid-only
        // before §6.1b.)
        public bool DropPinIfResolvedWithout(string subAssetKey)
        {
            if (_pinnedParentPath.Length == 0 || string.IsNullOrEmpty(subAssetKey))
            {
                return false;
            }

            var resolved = _getResolved(_pinnedParentPath);
            if (resolved == null || IndexOfSub(resolved, subAssetKey) >= 0)
            {
                return false;
            }

            _pinnedParentPath = string.Empty;
            return true;
        }

        // -------------------------------------------------------------------
        // Index math
        // -------------------------------------------------------------------

        // Maps one visible cell back to its main asset. subAsset is non-null
        // (and the return value the parent's asset index) for a child cell,
        // with its position inside the expansion and the expansion's size —
        // the grid's strip-segment flags are derived from those two.
        public int ResolveVisibleIndex(
            int visibleIndex,
            out TSub subAsset,
            out int subAssetIndex,
            out int subAssetCount)
        {
            subAsset = null;
            subAssetIndex = -1;
            subAssetCount = 0;

            // Binary search: the last expansion whose parent's visible index is
            // at or before visibleIndex.
            var found = -1;
            int low = 0, high = _spans.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var candidate = _spans[middle];
                if (candidate.AssetIndex + candidate.SubsBefore <= visibleIndex)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (found < 0)
            {
                return visibleIndex;
            }

            var span = _spans[found];
            var parentVisibleIndex = span.AssetIndex + span.SubsBefore;
            if (visibleIndex == parentVisibleIndex)
            {
                return span.AssetIndex;
            }

            var subOffset = visibleIndex - parentVisibleIndex - 1;
            if (subOffset >= 0 && subOffset < span.Subs.Length)
            {
                subAsset = span.Subs[subOffset];
                subAssetIndex = subOffset;
                subAssetCount = span.Subs.Length;
                return span.AssetIndex;
            }

            // Past this expansion's children: a plain main cell shifted by
            // every child cell at or before it.
            return visibleIndex - (span.SubsBefore + span.Subs.Length);
        }

        public int ResolveVisibleIndex(int visibleIndex, out TSub subAsset)
            => ResolveVisibleIndex(visibleIndex, out subAsset, out _, out _);

        // A main asset's position in the visible order.
        public int AssetIndexToVisibleIndex(int assetIndex)
        {
            var subsBefore = 0;
            for (var i = 0; i < _spans.Count; i++)
            {
                var span = _spans[i];
                if (span.AssetIndex >= assetIndex)
                {
                    break;
                }

                subsBefore = span.SubsBefore + span.Subs.Length;
            }

            return assetIndex + subsBefore;
        }

        // Where the keyboard cursor sits: on the highlighted child cell when a
        // sub-asset is the current selection (it has no path of its own, so the
        // substrate's lead cannot point at it), otherwise on the lead asset's
        // own cell. -1 when neither resolves. The highlight itself stays with
        // the widget that paints it, so it is passed in.
        public int ResolveCursorVisibleIndex(int leadAssetIndex, string highlightedSubAssetKey)
        {
            if (!string.IsNullOrEmpty(highlightedSubAssetKey))
            {
                for (var i = 0; i < _spans.Count; i++)
                {
                    var span = _spans[i];
                    var subIndex = IndexOfSub(span.Subs, highlightedSubAssetKey);
                    if (subIndex >= 0)
                    {
                        return span.AssetIndex + span.SubsBefore + 1 + subIndex;
                    }
                }
            }

            return leadAssetIndex >= 0 ? AssetIndexToVisibleIndex(leadAssetIndex) : -1;
        }

        // The cell a pending sub-asset ping should frame: the child cell itself
        // once the expansion holds it, else the parent's own cell so the reveal
        // at least lands on the right row while the catalog is still working.
        // -1 when the parent is not in the order at all.
        public int ResolvePingVisibleIndex(string parentPath, string subAssetKey)
        {
            if (_spanByPath.TryGetValue(parentPath ?? string.Empty, out var span))
            {
                var parentVisibleIndex = span.AssetIndex + span.SubsBefore;
                var subIndex = IndexOfSub(span.Subs, subAssetKey);
                return subIndex >= 0
                    ? parentVisibleIndex + 1 + subIndex
                    : parentVisibleIndex;
            }

            var assetIndex = FindPathIndex(parentPath);
            return assetIndex >= 0 ? AssetIndexToVisibleIndex(assetIndex) : -1;
        }

        // Path-only lookup: never forces the lazy browse projection to
        // materialize the entries it skips.
        public int FindPathIndex(string path)
            => ZepPathHelpers.FindAssetPathIndex(_assets, path);

        private int IndexOfSub(IReadOnlyList<TSub> subs, string key)
        {
            if (subs == null || string.IsNullOrEmpty(key))
            {
                return -1;
            }

            for (var i = 0; i < subs.Count; i++)
            {
                if (string.Equals(_keyOf(subs[i]), key, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
