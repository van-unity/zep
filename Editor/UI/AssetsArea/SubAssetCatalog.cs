using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Editor.Bridge.Indexing;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.UI.AssetsArea
{
    // One visible sub-asset (an asset representation) of a main asset — e.g.
    // the material embedded in a TextMeshPro font asset, or a sprite inside a
    // texture. Sub-assets share their parent's path; Key is the unique
    // view-layer identity (parent path + local file id).
    internal sealed class SubAssetDescriptor
    {
        internal SubAssetDescriptor(
            string parentPath,
            long localId,
#if UNITY_6000_5_OR_NEWER
            EntityId runtimeId,
#else
            int runtimeId,
#endif
            string name,
            string typeName,
            Texture2D icon)
        {
            ParentPath = parentPath;
            LocalId = localId;
            RuntimeId = runtimeId;
            Name = name;
            TypeName = typeName;
            Icon = icon;
            Key = MakeKey(parentPath, localId);
        }

        // The one owner of the key format. Also derived by the ping receiver
        // from a pinged sub-OBJECT, so reveal-by-ping and the catalog agree on
        // a row's identity without sharing descriptor instances.
        internal static string MakeKey(string parentPath, long localId)
            => parentPath + "::" + localId;

        internal string ParentPath { get; }
        internal long LocalId { get; }
        // Fast Resolve() handle — an EntityId on 6.5+ (where the instance-id
        // APIs are obsolete errors), an instance id before. Not stable across
        // domain reloads — Resolve falls back to a LocalId rescan when it goes
        // stale.
#if UNITY_6000_5_OR_NEWER
        internal EntityId RuntimeId { get; }
#else
        internal int RuntimeId { get; }
#endif
        internal string Name { get; }
        internal string TypeName { get; }
        internal Texture2D Icon { get; }
        internal string Key { get; }
    }

    // Project-global sub-asset lookup, resolved lazily off row binds. Rows ask
    // GetOrRequest at bind time: a cache hit answers synchronously; a miss
    // records the path and returns null, and the backlog is drained in small
    // time-sliced batches on EditorApplication.update (the same shape as the
    // icon repository's async preview upgrade) so binding a folder of large
    // assets never loads all their representations in one frame. The backlog
    // is bounded and drained newest-first, because a scroll makes far more
    // requests than it leaves on screen. Changed fires when the backlog
    // drains, every NotifyEveryTicks ticks while it is still draining, and
    // after import-driven invalidation; widgets respond with a visible-window
    // rebind.
    internal static class SubAssetCatalog
    {
        // The visible order both asset widgets render lives in Core; this is the
        // one place it is wired to this catalog and to the descriptor's key.
        internal static VisibleOrderProjection<SubAssetDescriptor> NewVisibleOrder()
        {
            return new VisibleOrderProjection<SubAssetDescriptor>(
                GetResolved,
                GetOrRequest,
                static subAsset => subAsset.Key);
        }

        // Per-tick budget: LoadAllAssetRepresentationsAtPath is a full asset
        // load, so a couple of paths per tick keeps the editor responsive
        // while a freshly opened folder streams its disclosure arrows in.
        private const int MaxPathsPerTick = 4;
        // Hard bound on the request backlog. One fast scroll through a large
        // folder asks for every row it passes; without a cap the pump then
        // works for minutes on paths that left the screen long ago. The
        // youngest requests are the ones still on screen, so the backlog is
        // drained newest-first and overflow drops the OLDEST entry — which
        // costs nothing, because a dropped path re-requests the moment it is
        // bound again (i.e. when it is visible again).
        private const int MaxPendingPaths = 512;
        // Changed makes every attached list and grid widget rebind the
        // realized rows the batch names, so raising it per drained batch
        // turned a long backlog into a per-frame rebind storm. Batching the
        // signal costs at most this many ticks of latency before newly
        // resolved rows appear.
        private const int NotifyEveryTicks = 8;
        // Hard count bound for the resolved cache, mirroring the icon
        // repository's _assetIconCache: browsing alone never evicts (only an
        // import/delete does), so a long session over a big project would
        // otherwise retain a descriptor array — and a pinned mini thumbnail
        // per sub-asset — for every asset ever scrolled past. Clearing on
        // overflow is cheap to recover from for the same reason a dropped
        // pending path is: the visible rows re-request on their next bind.
        private const int MaxResolvedEntries = 4096;

        private static readonly SubAssetDescriptor[] Empty = Array.Empty<SubAssetDescriptor>();
        private static readonly Dictionary<string, SubAssetDescriptor[]> _resolvedByPath =
            new Dictionary<string, SubAssetDescriptor[]>(StringComparer.Ordinal);
        // Used as a stack: requests are appended and Tick takes from the end.
        private static readonly List<string> _pendingPaths = new List<string>();
        private static readonly HashSet<string> _pendingSet = new HashSet<string>(StringComparer.Ordinal);
        // The paths resolved (or invalidated) since the last raise. Reused
        // rather than re-allocated per raise, so a handler may read it only
        // for the duration of the raise — no subscriber keeps the reference.
        private static readonly List<string> _batch = new List<string>();
        private static bool _isTicking;
        private static bool _isPostprocessorHooked;
        private static int _ticksSinceNotify;

        internal static event Action<IReadOnlyList<string>> Changed;

        // Test seam. The queue policy above (bound, drain order, signal
        // coalescing) is the behaviour under test; the work it budgets is a
        // Unity asset load plus preview generation, which needs a live editor
        // and real assets. An override stands in for that boundary call.
        internal static Func<string, SubAssetDescriptor[]> ResolveOverrideForTests;

        // Test seam: the pump is driven by EditorApplication.update, which a
        // batchmode test cannot step. Runs exactly one pump tick.
        internal static void TickForTests()
        {
            Tick();
        }

        // Test seam: the catalog is static, so a test must be able to put it
        // back to its startup state. Leaves the postprocessor hook alone (it
        // is idempotent and cheap); drops the backlog, the cache, and the
        // update subscription.
        internal static void ResetForTests()
        {
            _resolvedByPath.Clear();
            _pendingPaths.Clear();
            _pendingSet.Clear();
            _batch.Clear();
            _ticksSinceNotify = 0;
            if (_isTicking)
            {
                _isTicking = false;
                EditorApplication.update -= Tick;
            }
        }

        internal static int ResolvedCountForTests => _resolvedByPath.Count;

        internal static SubAssetDescriptor[] GetOrRequest(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !AssetPathClassification.IsProjectAssetPath(assetPath))
            {
                return Empty;
            }

            if (_resolvedByPath.TryGetValue(assetPath, out var resolved))
            {
                return resolved;
            }

            EnsurePostprocessorHooked();
            if (_pendingSet.Add(assetPath))
            {
                if (_pendingPaths.Count >= MaxPendingPaths)
                {
                    _pendingSet.Remove(_pendingPaths[0]);
                    _pendingPaths.RemoveAt(0);
                }

                _pendingPaths.Add(assetPath);
                EnsureTicking();
            }

            return null;
        }

        // Cache-only read for the expansion projection: an expanded row whose
        // sub-assets were invalidated mid-session simply collapses until the
        // next bind re-requests the path.
        internal static SubAssetDescriptor[] GetResolved(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                && _resolvedByPath.TryGetValue(assetPath, out var resolved)
                ? resolved
                : null;
        }

        // The live sub-object for a descriptor. The cached runtime handle
        // answers in one call; after a domain reload or reimport it can go
        // stale, so fall back to re-scanning the parent's representations by
        // LocalId.
        internal static UnityEngine.Object Resolve(SubAssetDescriptor descriptor)
        {
            if (descriptor == null)
            {
                return null;
            }

            // EntityIdToObject is the resolver from 6.3 (InstanceIDToObject
            // warns there and is an error on 6.5); before 6.3 the EntityId type
            // does not exist at all.
#if UNITY_6000_5_OR_NEWER
            var cached = EditorUtility.EntityIdToObject(descriptor.RuntimeId);
#elif UNITY_6000_3_OR_NEWER
            var cached = EditorUtility.EntityIdToObject((EntityId)descriptor.RuntimeId);
#else
            var cached = EditorUtility.InstanceIDToObject(descriptor.RuntimeId);
#endif
            if (cached != null)
            {
                return cached;
            }

            var representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(descriptor.ParentPath);
            for (var i = 0; i < representations.Length; i++)
            {
                var candidate = representations[i];
                if (candidate != null
                    && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out _, out long localId)
                    && localId == descriptor.LocalId)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void EnsureTicking()
        {
            if (_isTicking)
            {
                return;
            }

            _isTicking = true;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            var processed = 0;
            while (_pendingPaths.Count > 0 && processed < MaxPathsPerTick)
            {
                // From the end: the newest request is the one most likely to
                // be on screen right now.
                var last = _pendingPaths.Count - 1;
                var path = _pendingPaths[last];
                _pendingPaths.RemoveAt(last);
                _pendingSet.Remove(path);
                StoreResolved(path, ResolvePath(path));
                _batch.Add(path);
                processed++;
            }

            var drained = _pendingPaths.Count == 0;
            if (drained)
            {
                _isTicking = false;
                EditorApplication.update -= Tick;
            }

            if (processed == 0)
            {
                return;
            }

            // Coalesced: rows that resolved during the batched ticks appear on
            // the next raise. Draining always raises, so the last batch is
            // never left unpublished.
            if (!drained && ++_ticksSinceNotify < NotifyEveryTicks)
            {
                return;
            }

            _ticksSinceNotify = 0;
            Zep.Core.Application.ZepPerfLog.Count(
                Zep.Core.Application.ZepPerfCounter
                    .SubAssetCatalogChangedRaised);
            RaiseChanged();
        }

        // One raise per accumulated batch. The batch is cleared after the
        // handlers have run, which is also what makes the reused list safe.
        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
            {
                handler(_batch);
            }

            _batch.Clear();
        }

        private static void StoreResolved(string assetPath, SubAssetDescriptor[] descriptors)
        {
            if (_resolvedByPath.Count >= MaxResolvedEntries
                && !_resolvedByPath.ContainsKey(assetPath))
            {
                _resolvedByPath.Clear();
            }

            _resolvedByPath[assetPath] = descriptors;
        }

        private static SubAssetDescriptor[] ResolvePath(string assetPath)
        {
            if (ResolveOverrideForTests != null)
            {
                return ResolveOverrideForTests(assetPath) ?? Empty;
            }

            var representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
            if (representations == null || representations.Length == 0)
            {
                return Empty;
            }

            var result = new List<SubAssetDescriptor>(representations.Length);
            for (var i = 0; i < representations.Length; i++)
            {
                var obj = representations[i];
                if (obj == null)
                {
                    continue;
                }

                // A representation without a stable local file id (never
                // observed for imported assets) falls back to its runtime id
                // so the row still gets a unique, if session-local, key.
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out long localId))
                {
#if UNITY_6000_5_OR_NEWER
                    localId = (long)EntityId.ToULong(obj.GetEntityId());
#else
                    localId = obj.GetInstanceID();
#endif
                }

                result.Add(new SubAssetDescriptor(
                    assetPath,
                    localId,
#if UNITY_6000_5_OR_NEWER
                    obj.GetEntityId(),
#else
                    obj.GetInstanceID(),
#endif
                    obj.name,
                    obj.GetType().Name,
                    AssetPreview.GetMiniThumbnail(obj)));
            }

            return result.Count == 0 ? Empty : result.ToArray();
        }

        private static void EnsurePostprocessorHooked()
        {
            if (_isPostprocessorHooked)
            {
                return;
            }

            _isPostprocessorHooked = true;
            ZepAssetPostprocessor.Changed += OnProjectAssetsChanged;
        }

        private static void OnProjectAssetsChanged(ProjectAssetChanges changes)
        {
            // The invalidated paths ARE the batch: a row whose cached
            // sub-asset list just went away is exactly the row that has to
            // rebind (its disclosure arrow may be gone).
            RemovePaths(changes.Imported);
            RemovePaths(changes.Deleted);
            RemovePaths(changes.Moved);
            RemovePaths(changes.MovedFromPaths);
            if (_batch.Count > 0)
            {
                RaiseChanged();
            }
        }

        private static void RemovePaths(string[] paths)
        {
            if (paths == null)
            {
                return;
            }

            for (var i = 0; i < paths.Length; i++)
            {
                if (_resolvedByPath.Remove(paths[i]))
                {
                    _batch.Add(paths[i]);
                }
            }
        }
    }
}
