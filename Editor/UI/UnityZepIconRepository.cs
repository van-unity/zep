using System;
using System.Collections.Generic;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.UI
{
    // Single source of truth for icon lookups across Zep. Every UI surface
    // that renders an asset / folder / type icon goes through this class so icons stay in
    // sync between the files panel, type filter dropdown, sidebar, and any
    // future callsites.
    public sealed class UnityZepIconRepository
    {
        private const string FolderIconPath = "Packages/com.van-unity.zep/Editor/UI/Icons/folder-icon.png";
        private const string FolderEmptyIconPath = "Packages/com.van-unity.zep/Editor/UI/Icons/folder-icon-empty.png";

        // Types whose Unity built-in icon name doesn't match "<TypeName> Icon".
        // Only populate for names we've observed to diverge; unknowns fall
        // through to the sample-asset / reflection chain.
        private static readonly Dictionary<string, string> TypeIconNameOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "MonoScript", "cs Script Icon" },
        };

        // Keep enough previews hot that typical scrolling doesn't evict them
        // mid-view: the largest pinning surface is a grid window plus the
        // Quick Look filmstrip. Unity exposes no getter for the current size,
        // so every constructed repository simply sets this value — there is no
        // "only bump" comparison to make, and a second editor tool that sized
        // the cache higher would be overwritten here.
        private const int PreviewCacheSize = 512;

        private readonly Func<string, string> _sampleAssetPathForType;
        // Asked once per folder row: does the asset index KNOW this folder has
        // no direct children? A pull rather than a pushed copy of a set, so a
        // row bound before the index has read the folder gets the right answer
        // on its next bind instead of waiting for a refresh signal to arrive.
        // Two hash lookups behind it, not an AssetDatabase.FindAssets - the
        // copy this replaced existed to avoid that call, which used to cost 7 s
        // on the first sidebar click into a deep tree on a real project. Null
        // wherever nothing can answer, which reads as "not known empty".
        private readonly Func<string, bool> _isFolderKnownEmpty;
        private readonly Dictionary<string, Texture2D> _typeIconCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        // Per-asset icon cache: GetAssetIcon used to fire 2-3 AssetDatabase /
        // AssetPreview calls per visible row on every list rebind. On a folder
        // of 100 assets with 20 visible rows that's ~60 Unity API calls per
        // navigation, including LoadAssetAtPath<Texture2D> for direct-texture
        // types — measurable cost. The cache is keyed by asset path; entries
        // are invalidated by InvalidateAssetIconCache (full clear from the
        // controller's AssetCatalogChanged event) and per-path by the pending-
        // preview poll when an async preview becomes available.
        // A cached mini can still need an upgrade when its first caller opted
        // out. Keep terminal attempt state with the texture so invalidation,
        // eviction and Unity's destroyed-preview recovery reset both together.
        private readonly Dictionary<string, (Texture2D Icon, bool PreviewUpgradeComplete)> _assetIconCache =
            new Dictionary<string, (Texture2D, bool)>(StringComparer.Ordinal);
        // Hard count bound for _assetIconCache. Browsing alone never evicts
        // (only a full AssetCatalogChanged snapshot clears), so a long session over a huge
        // project would otherwise grow the dictionary without limit. A full
        // clear on overflow is cheap to recover from: the ~20 visible rows
        // re-fetch mini icons on their next bind and re-queue upgrades.
        private const int MaxAssetIconCacheEntries = 8192;
        // Paths queued for an async icon-quality upgrade (rendered preview).
        // PollPendingPreviews drains this on EditorApplication.update with a
        // frame budget so we never block the bind path on disk loads or
        // preview generation. The list is used as a stack drained from the
        // END: a fast scroll queues far more paths than it leaves on screen,
        // and the youngest request is the one the user is looking at. Items
        // that are not ready yet go back to the FRONT, which keeps the
        // starvation guard the FIFO this replaced was built for: the
        // Dictionary before that was drained from the iteration front, so 8
        // stuck not-ready entries starved everything behind them and kept the
        // update hook alive indefinitely.
        private readonly List<string> _pendingPreviews = new List<string>();
        private readonly HashSet<string> _pendingPreviewPaths = new HashSet<string>(StringComparer.Ordinal);
        private bool _previewPollActive;
        // Hard bound on the backlog. Dropping the oldest entry costs nothing:
        // the path re-queues the next time a row binds it, which is exactly
        // when it is on screen again.
        private const int MaxPendingPreviews = 512;
        // PreviewsReady makes the active assets feature rebind the realized
        // rows whose path it names, so raising it on every tick that upgraded
        // an icon turned a long backlog into a per-frame rebind storm. At most
        // one raise per this many ticks while the backlog is still draining; a
        // drained backlog always raises, so nothing stays unpublished.
        private const int PreviewsReadyEveryTicks = 8;
        private int _ticksSincePreviewsReady;
        // The paths whose icon actually changed since the last raise. Reused
        // rather than re-allocated per raise, so a handler may read it only for
        // the duration of the raise — no subscriber keeps the reference. Its
        // emptiness is also the "nothing to publish" flag the raise gates on.
        private readonly List<string> _appliedSincePreviewsReady = new List<string>();

        // Bound the work done per editor update tick so large folder
        // navigations don't stall the editor while previews are still
        // generating. 8 paths/tick × ~60 ticks/s comfortably drains the
        // queue within a second for a typical visible window while
        // staying responsive.
        private const int MaxPreviewUpgradesPerTick = 8;

        public event Action<IReadOnlyList<string>> PreviewsReady;

        public UnityZepIconRepository(
            Func<string, string> sampleAssetPathForType = null,
            Func<string, bool> isFolderKnownEmpty = null)
        {
            _sampleAssetPathForType = sampleAssetPathForType;
            _isFolderKnownEmpty = isFolderKnownEmpty;
            AssetPreview.SetPreviewTextureCacheSize(PreviewCacheSize);
        }

        // allowPreviewUpgrade=false serves the synchronous mini icon without
        // queueing the async AssetPreview upgrade. The assets view passes
        // false for materials the custom thumbnail pipeline is handling:
        // the stock upgrade would LoadAssetAtPath the material (pulling its
        // whole texture set into memory) to render a sphere nobody will see.
        // Every other caller keeps the default and today's behavior.
        public Texture2D GetAssetIcon(string assetPath, string typeName, bool allowPreviewUpgrade = true)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return FallbackAssetIcon();
            }

            if (string.Equals(typeName, "Folder", StringComparison.OrdinalIgnoreCase))
            {
                // Folder icon lookups go through GetFolderIcon's own caching
                // (folder-empty cache + AssetDatabase's internal texture cache);
                // they don't share the asset-icon cache.
                return GetFolderIcon(assetPath);
            }

            // Synchronous response: AssetDatabase.GetCachedIcon. Unity keeps
            // a per-asset mini-icon resolved at import time; reading it is a
            // dictionary lookup. We used to also try LoadAssetAtPath<Texture2D>
            // and LoadAssetAtPath<Object>+AssetPreview.GetAssetPreview here,
            // and *both* hit the disk / asset cache on first bind — on a fresh
            // folder with 20 visible rows that totaled ~600 ms of bind time.
            // The full preview / source-resolution texture now upgrades the
            // cache asynchronously via PollPendingPreviews and the visible
            // rows rebind once it's ready. Matches the IMGUI Project window's
            // behavior: instant first paint, progressively-richer icons.
            // CLR null is a cached absence; Unity fake-null means a texture
            // was destroyed and its preview attempt needs to start again.
            if (!_assetIconCache.TryGetValue(assetPath, out var cachedIcon)
                || (cachedIcon.Icon == null && !ReferenceEquals(cachedIcon.Icon, null)))
            {
                var icon = AssetDatabase.GetCachedIcon(assetPath) as Texture2D;
                if (icon == null)
                {
                    icon = FallbackAssetIcon();
                }
                StoreAssetIcon(assetPath, icon);
                cachedIcon = (icon, false);
            }

            // Queue an async upgrade attempt — swaps in the rendered
            // AssetPreview when Unity finishes generating it. Cheap to enqueue.
            if (allowPreviewUpgrade && !cachedIcon.PreviewUpgradeComplete
                && _pendingPreviewPaths.Add(assetPath))
            {
                if (_pendingPreviews.Count >= MaxPendingPreviews)
                {
                    _pendingPreviewPaths.Remove(_pendingPreviews[0]);
                    _pendingPreviews.RemoveAt(0);
                }

                _pendingPreviews.Add(assetPath);
                EnsurePreviewPollRunning();
            }

            return cachedIcon.Icon;
        }

        internal enum PreviewUpgrade
        {
            Applied,   // higher-quality texture stored — a rebind will show it
            Dropped,   // no preview will ever come; keep the mini icon, stop polling
            Pending,   // Unity is still generating — retry on a later tick
        }

        // Single attempt to upgrade the cached mini-icon to a higher-quality
        // representation. Called from PollPendingPreviews, never from the
        // synchronous bind path.
        private PreviewUpgrade TryUpgradeAssetIcon(string assetPath)
        {
            // Test seam: everything below is the Unity boundary this poll
            // exists to budget (an asset load plus preview generation, which
            // needs a live editor and real assets). The backlog policy above
            // — bound, drain order, coalesced signal — is what the headless
            // tests assert, so an override stands in for one item's work.
            if (UpgradeOverrideForTests != null)
            {
                return UpgradeOverrideForTests(assetPath);
            }

            // Texture-like assets deliberately go through AssetPreview too.
            // An earlier version cached the source-resolution texture itself
            // for them, but the cache's strong reference pinned every visited
            // texture against EditorUtility.UnloadUnusedAssets — browsing an
            // art-heavy project retained GBs of source textures for the whole
            // session. Previews live in Unity's own bounded preview cache
            // (SetPreviewTextureCacheSize); when Unity evicts one, our entry
            // goes fake-null and GetAssetIcon re-fetches.
            //
            // Rendered preview path: must hand AssetPreview an Object, and
            // the preview is generated on a background thread by Unity.
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (obj == null)
            {
                // Asset gone (deleted mid-flight) — drop from queue.
                return PreviewUpgrade.Dropped;
            }

            var preview = AssetPreview.GetAssetPreview(obj);
            if (preview != null)
            {
                StoreAssetIcon(assetPath, preview);
                return PreviewUpgrade.Applied;
            }

            // Not ready yet. AssetPreview.GetAssetPreview will have kicked
            // off the async generation; we keep this path in the queue until
            // either the preview lands or Unity stops loading it.
#if UNITY_6000_5_OR_NEWER
            if (!AssetPreview.IsLoadingAssetPreview(obj.GetEntityId()))
#else
            if (!AssetPreview.IsLoadingAssetPreview(obj.GetInstanceID()))
#endif
            {
                // No preview will ever come (script asset, generic types).
                // Stop polling for this path; the cached mini icon is final.
                return PreviewUpgrade.Dropped;
            }

            return PreviewUpgrade.Pending;
        }

        // Cached folder textures. There are only 2 distinct images across all
        // folder rows (regular, empty); loading them
        // once eliminates a per-bind AssetDatabase.LoadAssetAtPath call on
        // every visible folder row.
        private Texture2D _cachedFolderIcon;
        private Texture2D _cachedFolderEmptyIcon;

        public Texture2D GetFolderIcon(string folderPath)
        {

            return GetFolderGlyph(IsRegularFolderEmpty(folderPath));
        }

        public Texture2D GetFolderGlyph(bool isEmpty)
        {
            if (isEmpty)
            {
                if (_cachedFolderEmptyIcon == null)
                {
                    _cachedFolderEmptyIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(FolderEmptyIconPath);
                }
                if (_cachedFolderEmptyIcon != null) return _cachedFolderEmptyIcon;
            }

            if (_cachedFolderIcon == null)
            {
                _cachedFolderIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(FolderIconPath);
            }
            return _cachedFolderIcon ?? EditorGUIUtility.FindTexture("Folder Icon");
        }

        private bool IsRegularFolderEmpty(string folderPath)
        {
            // Emptiness is a fact, not a default: only an index that KNOWS the
            // folder and knows it has no direct children answers true. With no
            // source to ask, every folder is unknown - and unknown paints the
            // ordinary folder, which is what keeps the argument-less
            // constructions correct rather than merely compiling.
            return _isFolderKnownEmpty?.Invoke(folderPath) ?? false;
        }

        public Texture2D GetTypeIcon(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            if (_typeIconCache.TryGetValue(typeName, out var cached) && cached != null)
            {
                return cached;
            }

            Texture2D icon = null;

            // 1. Sample asset from the project index. Delegates to the same
            //    AssetDatabase.GetCachedIcon call the files panel uses — so the
            //    dropdown icon matches the files panel exactly.
            var samplePath = _sampleAssetPathForType?.Invoke(typeName);
            if (!string.IsNullOrEmpty(samplePath))
            {
                icon = AssetDatabase.GetCachedIcon(samplePath) as Texture2D;
            }

            // 2. Curated override for types with irregular icon names.
            if (icon == null && TypeIconNameOverrides.TryGetValue(typeName, out var overrideName))
            {
                icon = EditorGUIUtility.FindTexture(overrideName);
            }

            // 3. Resolve System.Type by name and ask Unity for its canonical thumbnail.
            Type resolvedType = null;
            if (icon == null)
            {
                resolvedType = ResolveTypeByName(typeName);
                if (resolvedType != null)
                {
                    icon = AssetPreview.GetMiniTypeThumbnail(resolvedType);
                }
            }

            // 4. Pseudo-types (ShaderInclude, AssemblyDefinitionAsset, etc.) that
            //    follow the "<TypeName> Icon" naming convention.
            if (icon == null)
            {
                icon = EditorGUIUtility.FindTexture(typeName + " Icon");
            }

            // 5. Generic placeholder for any concrete type we resolved earlier.
            if (icon == null && resolvedType != null)
            {
                icon = EditorGUIUtility.ObjectContent(null, resolvedType)?.image as Texture2D;
            }

            // Only cache hits. A null right now might become a real icon later
            // (e.g. the first asset of that type is imported), so keep retrying.
            if (icon != null)
            {
                _typeIconCache[typeName] = icon;
            }

            return icon;
        }

        public void InvalidateThemeCache()
        {
            _typeIconCache.Clear();
        }

        public void InvalidateAssetIcon(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                _assetIconCache.Remove(assetPath);
            }
        }

        // Drops the cached GetAssetIcon results. Called by ShellView whenever
        // the asset index rebuilds (renames, deletes, imports). The pending-
        // preview poll does its own per-path invalidation so newly-rendered
        // previews swap in on the next rebind without dropping unrelated cache
        // entries.
        public void InvalidateAssetIconCache()
        {
            _assetIconCache.Clear();
        }

        // Served on every bind of a row whose path has no cached icon (and for
        // the null-path case), so the built-in lookup is memoized. Re-resolved
        // if the texture ever goes fake-null, the same recheck the asset-icon
        // cache uses for Unity-owned textures.
        private static Texture2D _fallbackAssetIcon;

        private static Texture2D FallbackAssetIcon()
        {
            if (_fallbackAssetIcon == null)
            {
                _fallbackAssetIcon = EditorGUIUtility.FindTexture("DefaultAsset Icon");
            }

            return _fallbackAssetIcon;
        }

        private void StoreAssetIcon(string assetPath, Texture2D icon)
        {
            if (_assetIconCache.Count >= MaxAssetIconCacheEntries
                && !_assetIconCache.ContainsKey(assetPath))
            {
                _assetIconCache.Clear();
            }

            _assetIconCache[assetPath] = (icon, false);
        }

        private void EnsurePreviewPollRunning()
        {
            if (_previewPollActive)
            {
                return;
            }

            _previewPollActive = true;
            EditorApplication.update += PollPendingPreviews;
        }

        // The panel that owns this repository is going away (window closed, or
        // rebuilt by CreateGUI). The poll only unsubscribes once the queue
        // drains, so paths whose preview never becomes available would keep the
        // update hook — and this whole repository — alive for the rest of the
        // editor session. Idempotent: a later GetAssetIcon re-arms the poll,
        // which is why the pending set is cleared too (a path still in it would
        // never re-queue, so its upgrade would be lost to a dead poll).
        internal void Teardown()
        {
            EditorApplication.update -= PollPendingPreviews;
            _previewPollActive = false;
            _pendingPreviews.Clear();
            _pendingPreviewPaths.Clear();
            _assetIconCache.Clear();
            _ticksSincePreviewsReady = 0;
            _appliedSincePreviewsReady.Clear();
        }

        // Test seams, paired with the enum above: the poll is driven by
        // EditorApplication.update, which a batchmode test cannot step.
        internal Func<string, PreviewUpgrade> UpgradeOverrideForTests;

        internal void PollPendingPreviewsForTests()
        {
            PollPendingPreviews();
        }

        private void PollPendingPreviews()
        {
            if (_pendingPreviews.Count == 0)
            {
                EditorApplication.update -= PollPendingPreviews;
                _previewPollActive = false;
                return;
            }

            // Perf instrumentation (opt-in via the perf-logging menu toggle):
            // this poll had zero timing before the material-thumbnail work,
            // which made every per-tick budget number an estimate. The
            // stopwatch is only created (and only read) when logging is on.
            var perfStopwatch = ZepPerfLog.IsEnabled
                ? System.Diagnostics.Stopwatch.StartNew()
                : null;

            var budget = Math.Min(MaxPreviewUpgradesPerTick, _pendingPreviews.Count);
            var anyApplied = false;
            for (var i = 0; i < budget; i++)
            {
                var last = _pendingPreviews.Count - 1;
                var path = _pendingPreviews[last];
                _pendingPreviews.RemoveAt(last);
                var upgrade = TryUpgradeAssetIcon(path);
                if (upgrade != PreviewUpgrade.Pending)
                {
                    _pendingPreviewPaths.Remove(path);
                    if (_assetIconCache.TryGetValue(path, out var cachedIcon))
                    {
                        _assetIconCache[path] = (cachedIcon.Icon, true);
                    }

                    if (upgrade == PreviewUpgrade.Applied)
                    {
                        _appliedSincePreviewsReady.Add(path);
                        anyApplied = true;
                    }
                }
                else
                {
                    // Still generating: FRONT of the backlog. The drain
                    // takes from the back, so a stuck path is served last
                    // and cannot starve the paths behind it.
                    _pendingPreviews.Insert(0, path);
                }
            }

            var drained = _pendingPreviews.Count == 0;
            if (drained)
            {
                EditorApplication.update -= PollPendingPreviews;
                _previewPollActive = false;
            }

            if (perfStopwatch != null)
            {
                ZepPerfLog.Log(
                    $"[Zep/perf] preview-poll tick {perfStopwatch.Elapsed.TotalMilliseconds:F2} ms: " +
                    $"drained={budget} applied={anyApplied} queue={_pendingPreviews.Count}");
            }

            // The named rows rebind via ShellView → AssetsAreaController →
            // active feature → the widget's realized rows; the bind-time icon
            // compare picks the upgrade out of _assetIconCache. Only fired
            // when a texture actually changed — Dropped/Pending outcomes
            // would make the rebind a no-op — and then only once per
            // PreviewsReadyEveryTicks ticks, or immediately once the backlog
            // is empty. Upgrades applied in between ride along in the same
            // batch, so nothing is lost, only batched.
            _ticksSincePreviewsReady++;
            if (_appliedSincePreviewsReady.Count == 0
                || (!drained && _ticksSincePreviewsReady < PreviewsReadyEveryTicks))
            {
                return;
            }

            _ticksSincePreviewsReady = 0;
            var handler = PreviewsReady;
            if (handler != null)
            {
                handler(_appliedSincePreviewsReady);
            }

            _appliedSincePreviewsReady.Clear();
        }

        // Memoized wrapper around the AppDomain scan below, NEGATIVE results
        // included. GetTypeIcon deliberately caches only non-null icons ("a
        // null now may become a real icon later"), but that policy also meant
        // every unresolvable type name re-ran FindTypeByName — a GetTypes()
        // sweep over every loaded assembly, each returning a fresh Type[]. The
        // ghost row created by inline-create carries TypeName "Unknown" for any
        // extension without a recipe, and its icon is resolved on every bind,
        // so naming a new asset ran that sweep per keystroke.
        //
        // Safe to memoize where the icon is not: type resolution is a pure
        // function of the loaded assemblies, which cannot change without a
        // domain reload — and a reload discards this repository with it.
        private readonly Dictionary<string, Type> _resolvedTypesByName =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private Type ResolveTypeByName(string typeName)
        {
            if (_resolvedTypesByName.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            var resolved = FindTypeByName(typeName);
            _resolvedTypesByName[typeName] = resolved;
            return resolved;
        }

        private static Type FindTypeByName(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Some types in the assembly failed to load — inspect those
                    // that did load successfully so we still find common Unity types.
                    types = ex.Types;
                }
                catch (System.IO.FileNotFoundException) // tolerated: assembly references a dependency that isn't present (common with conditionally-loaded editor assemblies); skip and keep scanning.
                {
                    continue;
                }
                catch (System.IO.FileLoadException) // tolerated: dependent assembly exists but failed to load (e.g. version/signature mismatch); skip this assembly.
                {
                    continue;
                }
                catch (BadImageFormatException) // tolerated: native or malformed managed assembly surfaced by AppDomain.GetAssemblies; skip rather than crash the icon lookup.
                {
                    continue;
                }
                catch (TypeLoadException) // tolerated: a per-type initializer failed during GetTypes; skip the assembly and keep looking.
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (var i = 0; i < types.Length; i++)
                {
                    var t = types[i];
                    if (t != null && string.Equals(t.Name, typeName, StringComparison.Ordinal))
                    {
                        return t;
                    }
                }
            }

            return null;
        }
    }
}
