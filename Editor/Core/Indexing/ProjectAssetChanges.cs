using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.FileOperations;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// Payload describing a batch of filesystem changes reported by Unity's
    /// <c>AssetPostprocessor.OnPostprocessAllAssets</c> callback. All four arrays are
    /// guaranteed non-null (empty arrays when there are no changes of that kind); the
    /// <see cref="Moved"/> and <see cref="MovedFromPaths"/> arrays are the same length,
    /// paired by index. <see cref="DidDomainReload"/> identifies the mandatory callback
    /// Unity emits after reloading managed assemblies.
    /// </summary>
    public readonly struct ProjectAssetChanges
    {
        public readonly string[] Imported;
        public readonly string[] Deleted;
        public readonly string[] Moved;
        public readonly string[] MovedFromPaths;
        internal readonly bool DidDomainReload;

        // Set only by the private constructor Normalize() builds its result with.
        // One postprocessor batch reaches Normalize() through ten call sites; the
        // work is deterministic, so re-running it can only reproduce the same value.
        private readonly bool _isNormalized;

        public ProjectAssetChanges(
            string[] imported,
            string[] deleted,
            string[] moved,
            string[] movedFromPaths)
            : this(imported, deleted, moved, movedFromPaths, false)
        {
        }

        public ProjectAssetChanges(
            string[] imported,
            string[] deleted,
            string[] moved,
            string[] movedFromPaths,
            bool didDomainReload)
            : this(imported, deleted, moved, movedFromPaths, didDomainReload, false)
        {
        }

        private ProjectAssetChanges(
            string[] imported,
            string[] deleted,
            string[] moved,
            string[] movedFromPaths,
            bool didDomainReload,
            bool isNormalized)
        {
            Imported = imported ?? Array.Empty<string>();
            Deleted = deleted ?? Array.Empty<string>();
            Moved = moved ?? Array.Empty<string>();
            MovedFromPaths = movedFromPaths ?? Array.Empty<string>();
            DidDomainReload = didDomainReload;
            _isNormalized = isNormalized;
        }

        internal int TotalChanges => Imported.Length + Deleted.Length + Moved.Length;

        public bool IsEmpty => TotalChanges == 0;

        public ProjectAssetChanges Normalize()
        {
            if (_isNormalized)
            {
                return this;
            }

            // Counted only when there is something to normalise: an empty batch
            // costs nothing and would drown the signal this counter exists for
            // (the same batch being normalised repeatedly across the ten call
            // sites that each re-run it).
            if (TotalChanges > 0)
            {
                ZepPerfLog.Count(
                    ZepPerfCounter.AssetChangesNormalizations);
            }

            var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedMoved = new List<string>(Moved.Length);
            var normalizedMovedFrom = new List<string>(Moved.Length);
            var movePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Moved.Length; i++)
            {
                var target = NormalizePath(Moved[i]);
                var source = i < MovedFromPaths.Length
                    ? NormalizePath(MovedFromPaths[i])
                    : string.Empty;
                if (string.IsNullOrEmpty(target))
                {
                    continue;
                }

                var pairKey = source + "\n" + target;
                if (!movePairs.Add(pairKey))
                {
                    continue;
                }

                normalizedMoved.Add(target);
                normalizedMovedFrom.Add(source);
                movedTargets.Add(target);
            }

            var moveOrder = new int[normalizedMoved.Count];
            for (var i = 0; i < moveOrder.Length; i++)
            {
                moveOrder[i] = i;
            }
            Array.Sort(moveOrder, (left, right) =>
                normalizedMovedFrom[right].Length.CompareTo(
                    normalizedMovedFrom[left].Length));
            var orderedMoved = new string[moveOrder.Length];
            var orderedMovedFrom = new string[moveOrder.Length];
            for (var i = 0; i < moveOrder.Length; i++)
            {
                orderedMoved[i] = normalizedMoved[moveOrder[i]];
                orderedMovedFrom[i] = normalizedMovedFrom[moveOrder[i]];
            }

            return new ProjectAssetChanges(
                NormalizePaths(Imported, movedTargets),
                CoalesceRoots(NormalizePaths(Deleted, null)),
                orderedMoved,
                orderedMovedFrom,
                DidDomainReload,
                true);
        }

        private static string[] CoalesceRoots(string[] paths)
        {
            if (paths == null || paths.Length < 2)
            {
                return paths ?? Array.Empty<string>();
            }

            // Shortest first, so every already-accepted root is at most as long as the
            // candidate: a candidate is covered exactly when itself or one of its
            // ancestors is an accepted root. Walking the ancestor chain against a set
            // is O(depth) per path; scanning the accepted list was O(k) per path and
            // sibling deletes never cover each other, so k grew to the whole batch.
            Array.Sort(paths, static (left, right) => left.Length.CompareTo(right.Length));
            var roots = new List<string>(paths.Length);
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                if (IsCoveredByAcceptedRoot(path, accepted))
                {
                    continue;
                }

                roots.Add(path);
                accepted.Add(path);
            }

            return roots.ToArray();
        }

        private static bool IsCoveredByAcceptedRoot(string path, HashSet<string> accepted)
        {
            if (accepted.Contains(path))
            {
                return true;
            }

            var separatorIndex = path.LastIndexOf('/');
            while (separatorIndex > 0)
            {
                if (accepted.Contains(path.Substring(0, separatorIndex)))
                {
                    return true;
                }

                separatorIndex = path.LastIndexOf('/', separatorIndex - 1);
            }

            return false;
        }

        private static string[] NormalizePaths(
            IReadOnlyList<string> paths,
            HashSet<string> excluded)
        {
            if (paths == null || paths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>(paths.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                var path = NormalizePath(paths[i]);
                if (!string.IsNullOrEmpty(path)
                    && (excluded == null || !excluded.Contains(path))
                    && seen.Add(path))
                {
                    result.Add(path);
                }
            }

            return result.ToArray();
        }

        private static string NormalizePath(string path)
            => AssetPathFormat.Normalize(path);
    }
}
