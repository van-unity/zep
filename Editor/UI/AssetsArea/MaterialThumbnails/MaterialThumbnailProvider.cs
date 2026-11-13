using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Zep.Core.Application;
using Zep.Core.Indexing;
using Zep.Editor.Bridge.Indexing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zep.Editor.UI.AssetsArea.MaterialThumbnails
{
    // Unreal-style material thumbnails for the assets view. Scope is
    // deliberate: only AssetEntryRenderer.ResolveAssetIcon (the grid/list bind
    // path) consults this provider — Quick Look, the sidebar, and every other
    // surface keep Unity's stock previews. Custom thumbs therefore must NEVER
    // be handed to UnityZepIconRepository's shared cache, which
    // Quick Look also reads.
    //
    // Bind-path contract: TryGet is dictionary-probe cheap and never loads an
    // asset, never renders, never touches the disk. All real work happens on
    // a budgeted EditorApplication.update pump (mirroring the icon
    // repository's preview poll): disk-cache warm starts first — probed by
    // GUID + dependency hash BEFORE the material is ever loaded, so a warm
    // scroll pulls zero materials (and zero of their textures) into memory —
    // and scene renders only on a true miss.
    //
    // Texture ownership: every texture this provider serves was created by it
    // and lives in a bounded LRU with explicit destruction. Destruction is
    // deferred past the ThumbnailsReady rebind so visible tiles swap away
    // from a dying texture before it goes fake-null.
    internal static class MaterialThumbnailProvider
    {
        internal const int ThumbnailSize = 256;
        // 256 thumbs * 256KB = 64 MiB ceiling, comfortably above the ~66-tile
        // worst-case visible grid so scrolling doesn't thrash the cache.
        private const int LruCapacity = 256;
        // Budget discipline for one editor update tick. Renders are the
        // expensive step (camera render + dependency hashing) and carry their
        // own count cap; disk loads are ~memcpy and ride the stopwatch alone.
        private const double TickBudgetMs = 2.0;
        private const int MaxRendersPerTick = 2;
        private const int MaxSyncRendersPerTick = 1;
        private const int MaxItemsPerTick = 16;
        // A shader stuck compiling re-queues as pending; without a cap a
        // never-compiling shader would keep the update hook alive forever
        // (the icon repository's FIFO starvation lesson). ~120 polls ≈ 2s of
        // ticks before falling back to the stock preview for the session.
        private const int MaxShaderCompileAttempts = 120;
        // Async readbacks are pumped manually (edit mode does not reliably
        // complete them on its own), and an IDLE editor may submit no GPU
        // work at all — a request can then linger indefinitely, which shipped
        // as "thumbnails only appear after a reimport wakes the editor up".
        // After this many pending ticks we force one WaitForCompletion per
        // tick: a bounded flush (the render is already done) that guarantees
        // delivery within a fraction of a second even with the editor idle.
        private const int ForceCompleteAfterTicks = 10;

        // Fired when at least one thumbnail landed or was invalidated;
        // ShellView routes it into the same NotifyIconPreviewsReady chain the
        // icon repository uses, so only the active assets feature rebinds.
        internal static event Action ThumbnailsReady;

        // How much thumbnail data may accumulate on disk before session-start
        // pruning deletes the oldest entries (also how deleted materials'
        // orphaned files age out — their GUID is unresolvable post-delete).
        private const long MaxDiskCacheBytes = 256L * 1024 * 1024;

        private struct InFlightReadback
        {
            public string Path;
            public string Guid;
            public string MaterialHash;
            public string[] DepPaths;
            public string[] DepHashes;
            public AsyncGPUReadbackRequest Request;
            public int TicksPending;
            // Set by invalidation while the readback is airborne: the pixels
            // were rendered from pre-change state and must be discarded, or a
            // stale thumb would enter the LRU and block the fresh render for
            // the rest of the session.
            public bool Stale;
        }

        // Used as a stack, drained from the END: a fast scroll over a
        // material-heavy grid queues far more paths than it leaves on screen,
        // and the youngest request is the tile the user is looking at. Items
        // that could not be served this tick (render budget spent, shader
        // still compiling, readback invalidated) go back to the FRONT, so a
        // stuck path is served last and cannot starve the ones behind it.
        private static readonly List<string> Pending = new List<string>();
        private static readonly HashSet<string> PendingSet = new HashSet<string>(StringComparer.Ordinal);
        // Hard bound on the backlog. Dropping the oldest entry costs nothing:
        // TryGet re-queues the path the next time a tile binds it, which is
        // exactly when it is on screen again.
        private const int MaxPending = 512;
        // ThumbnailsReady makes the active assets feature rebind its whole
        // realized window; raising it on every tick that landed a thumb turned
        // a long backlog into a per-frame rebind storm. At most one raise per
        // this many ticks while the backlog is still draining; a drained
        // backlog always raises.
        private const int ThumbnailsReadyEveryTicks = 8;
        private static int _ticksSinceThumbnailsReady;
        private static bool _thumbnailsReadyPending;
        private static readonly Dictionary<string, int> CompileAttempts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Paths that will never produce a custom thumb this session (dead
        // shader, skybox preview type, readback error, compile-attempt cap).
        // The stock preview pipeline serves them instead. Keep dependencies
        // with the verdict so a shader-only repair gets a fresh chance too.
        private static readonly Dictionary<string, string[]> Failed =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly List<InFlightReadback> InFlight = new List<InFlightReadback>();
        // Paths with an airborne readback. Consulted at enqueue/process time
        // so a rebind mid-flight cannot dispatch a duplicate render (which
        // would burn a render slot per tick for the whole readback latency).
        private static readonly HashSet<string> InFlightPaths = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<(Texture2D texture, int tick)> DeferredDestroy = new List<(Texture2D, int)>();
        private static readonly MaterialThumbnailLru Lru =
            new MaterialThumbnailLru(LruCapacity, tex => DeferredDestroy.Add((tex, _tick)));

        private static MaterialThumbnailRenderRig _rig;
        private static Texture2D _syncReadStaging;
        // Reusable 256KB pixel buffer for disk loads (main-thread only): a
        // warm scroll loads many thumbs per second, and a fresh array per
        // load is exactly the kind of GC churn the tick budget exists to
        // prevent. The texture copies the pixels before the next load reuses it.
        private static byte[] _diskPixelBuffer;
        // Set by MarkFailed during a tick: a path transitioning to "stock
        // pipeline owns it" must rebind visible rows just like a landed thumb,
        // or the tile would sit on the mini icon until an unrelated refresh.
        private static bool _anyFailedThisTick;
        private static bool _initialized;
        private static bool _pumpActive;
        private static int _tick;
        private static bool? _pipelineSupported;
        private static string _pipelineTag;

        // Bind-path probe. Cheap by contract: a string compare for
        // non-materials; for materials a dictionary probe plus, on first
        // sight, an enqueue. Returns null when no thumb is ready — the caller
        // falls through to the stock icon pipeline. pendingCustomThumb tells
        // the caller a custom thumb is coming (or cached): it should then
        // show the sync mini icon WITHOUT queueing the stock AssetPreview
        // upgrade, which would load the material and its whole texture set —
        // the exact work the disk cache exists to avoid. When false (not a
        // material, unsupported pipeline, or failed for the session) the
        // stock pipeline owns the path entirely.
        internal static Texture2D TryGet(string assetPath, string typeName, out bool pendingCustomThumb)
        {
            pendingCustomThumb = false;

            if (!string.Equals(typeName, "Material", StringComparison.Ordinal))
            {
                return null;
            }

            // TypeName "Material" implies a .mat source in both index record
            // sources, but the extension check keeps FBX sub-assets and any
            // future source drift out of the render path.
            if (string.IsNullOrEmpty(assetPath)
                || !assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            EnsureInitialized();

            // HDRP needs warm-up renders and has a history of broken preview
            // thumbnails; those projects keep the stock sphere everywhere.
            if (!PipelineSupported())
            {
                return null;
            }

            var cached = Lru.TryGet(assetPath);
            if (cached != null)
            {
                pendingCustomThumb = true;
                return cached;
            }

            if (Failed.ContainsKey(assetPath))
            {
                return null;
            }

            pendingCustomThumb = true;
            if (!InFlightPaths.Contains(assetPath) && PendingSet.Add(assetPath))
            {
                if (Pending.Count >= MaxPending)
                {
                    PendingSet.Remove(Pending[0]);
                    Pending.RemoveAt(0);
                }

                Pending.Add(assetPath);
                EnsurePumpRunning();
            }

            return null;
        }

        // Test seams, paired with Shutdown() below: the pump is driven by
        // EditorApplication.update, which a batchmode test cannot step.
        internal static Func<string, bool> ProcessOverrideForTests;

        internal static void TickForTests()
        {
            Pump();
        }

        // The deferred-destroy queue is fed only by LRU eviction, which in turn
        // needs a real render or disk load — neither is reachable headlessly.
        // This stands in for one evicted texture so the destroy timing is
        // testable.
        internal static void QueueDeferredDestroyForTests(Texture2D texture)
        {
            DeferredDestroy.Add((texture, _tick));
        }

        // Test seam + belt-and-braces: full reset of session state. Also the
        // beforeAssemblyReload / quitting handler — PreviewRenderUtility and
        // the owned textures must be released deterministically (finalizers
        // only log).
        internal static void Shutdown()
        {
            if (InFlight.Count > 0)
            {
                // Flush the GPU pipe before the rig's RT is destroyed.
                AsyncGPUReadback.WaitAllRequests();
                InFlight.Clear();
            }
            InFlightPaths.Clear();

            Pending.Clear();
            PendingSet.Clear();
            _ticksSinceThumbnailsReady = 0;
            _thumbnailsReadyPending = false;
            CompileAttempts.Clear();
            Failed.Clear();
            Lru.Clear();
            for (var i = 0; i < DeferredDestroy.Count; i++)
            {
                if (DeferredDestroy[i].texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(DeferredDestroy[i].texture);
                }
            }
            DeferredDestroy.Clear();
            ReleaseRig();

            if (_pumpActive)
            {
                EditorApplication.update -= Pump;
                _pumpActive = false;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            ZepAssetPostprocessor.Changed += OnAssetChanges;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            RenderPipelineManager.activeRenderPipelineTypeChanged += OnPipelineChanged;

            // One-shot housekeeping off the main thread: sweep crashed-write
            // temp files and bound the disk cache (pure System.IO; the
            // directory path is captured before the thread starts).
            var thumbsDirectory = MaterialThumbnailDiskStore.ThumbsDirectory;
            Task.Run(() => MaterialThumbnailDiskStore.Prune(
                thumbsDirectory, MaxDiskCacheBytes, TimeSpan.FromHours(1)));
        }

        private static void OnPipelineChanged()
        {
            _pipelineSupported = null;
            _pipelineTag = null;
            // Existing thumbs were rendered under the old pipeline; their
            // stored hash carries the pipeline tag, so disk entries re-render
            // naturally. Drop the in-memory ones for the same freshness.
            Lru.Clear();
            Pending.Clear();
            PendingSet.Clear();
            CompileAttempts.Clear();
            Failed.Clear();
            for (var i = 0; i < InFlight.Count; i++)
            {
                var entry = InFlight[i];
                entry.Stale = true;
                InFlight[i] = entry;
            }

            // Even a queue-only or unsupported-pipeline visit claimed icon
            // policy without landing an LRU entry. Rebind through the normal
            // coalesced signal; airborne requests still drain before disposal.
            _thumbnailsReadyPending = true;
            EnsurePumpRunning();
        }

        private static bool PipelineSupported()
        {
            if (!_pipelineSupported.HasValue)
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                _pipelineSupported = pipeline == null
                    || pipeline.GetType().FullName?.Contains("HighDefinition") != true;
                _pipelineTag = pipeline == null ? "builtin" : pipeline.GetType().Name;
            }

            return _pipelineSupported.Value;
        }

        private static void EnsurePumpRunning()
        {
            if (_pumpActive)
            {
                return;
            }

            _pumpActive = true;
            EditorApplication.update += Pump;
        }

        private static void MarkFailed(string path)
        {
            Failed[path] = CollectDependencies(path);
            CompileAttempts.Remove(path);
            _anyFailedThisTick = true;
        }

        private static void Pump()
        {
            _tick++;
            _anyFailedThisTick = false;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Textures evicted/invalidated on an earlier tick have had one
            // ThumbnailsReady rebind to swap away from them; now they can die.
            // The signal is coalesced, so "an earlier tick" is not enough on
            // its own — wait until nothing is owed a raise. A deferred texture
            // can only be added by work that also marks a raise pending, so
            // "nothing owed" means every entry here has already been published
            // as gone.
            //
            // Published is not the same as consumed: the shell answers
            // ThumbnailsReady by deferring its rebind through
            // EditorApplication.delayCall, which can land after the next pump.
            // So give the entry one pump of margin — an entry queued at tick T
            // dies no earlier than the pump after T+1, never on the first pump
            // following the raise.
            var destroyed = 0;
            if (!_thumbnailsReadyPending)
            {
                for (var i = DeferredDestroy.Count - 1; i >= 0; i--)
                {
                    if (DeferredDestroy[i].tick + 1 >= _tick)
                    {
                        continue;
                    }

                    if (DeferredDestroy[i].texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(DeferredDestroy[i].texture);
                    }
                    DeferredDestroy.RemoveAt(i);
                    destroyed++;
                }
            }

            var anyApplied = false;
            var readbacksCompleted = DrainReadbacks(ref anyApplied);

            var renders = 0;
            var diskLoads = 0;
            // Renders dispatched while the asset database is mid-refresh get
            // wrong ambient/reflection (the preview lighting override is
            // silently skipped during EditorApplication.isUpdating) — hold
            // the queue until the import settles.
            if (!EditorApplication.isUpdating && Pending.Count > 0)
            {
                var maxRenders = SystemInfo.supportsAsyncGPUReadback
                    ? MaxRendersPerTick
                    : MaxSyncRendersPerTick;
                var itemBudget = Math.Min(MaxItemsPerTick, Pending.Count);

                while (itemBudget-- > 0
                       && Pending.Count > 0
                       && stopwatch.Elapsed.TotalMilliseconds < TickBudgetMs)
                {
                    var last = Pending.Count - 1;
                    var path = Pending[last];
                    Pending.RemoveAt(last);
                    PendingSet.Remove(path);
                    ProcessPending(path, ref renders, ref diskLoads, maxRenders, ref anyApplied);
                }
            }

            _thumbnailsReadyPending |= anyApplied || _anyFailedThisTick;
            _ticksSinceThumbnailsReady++;
            if (_thumbnailsReadyPending
                && (Pending.Count == 0
                    || _ticksSinceThumbnailsReady >= ThumbnailsReadyEveryTicks))
            {
                _ticksSinceThumbnailsReady = 0;
                _thumbnailsReadyPending = false;
                ThumbnailsReady?.Invoke();
            }

            if (ZepPerfLog.IsEnabled
                && (renders > 0 || diskLoads > 0 || readbacksCompleted > 0 || destroyed > 0))
            {
                ZepPerfLog.Log(
                    $"[Zep/perf] mat-thumbs tick {stopwatch.Elapsed.TotalMilliseconds:F2} ms: " +
                    $"renders={renders} diskLoads={diskLoads} readbacksDone={readbacksCompleted} " +
                    $"destroyed={destroyed} queue={Pending.Count} inFlight={InFlight.Count} lru={Lru.Count}");
            }

            // Self-detach when idle — and release the render rig right here:
            // this is the only spot that observes "the queue just drained"
            // (the hook won't run again to do it later).
            if (Pending.Count == 0 && InFlight.Count == 0 && DeferredDestroy.Count == 0)
            {
                EditorApplication.update -= Pump;
                _pumpActive = false;
                ReleaseRig();
            }
        }

        private static void ProcessPending(
            string path, ref int renders, ref int diskLoads, int maxRenders, ref bool anyApplied)
        {
            if (!PipelineSupported())
            {
                return;
            }

            // Test seam: everything below is the boundary this pump budgets —
            // AssetDatabase resolution, a scene render, a GPU readback — all
            // of which need a live editor and a real material. The backlog
            // policy above (bound, drain order, coalesced signal) is what the
            // headless tests assert, so an override stands in for one item's
            // work and reports whether it produced a thumbnail.
            if (ProcessOverrideForTests != null)
            {
                if (ProcessOverrideForTests(path))
                {
                    anyApplied = true;
                }

                return;
            }

            // Resolved while queued (disk load from an earlier tick, a
            // duplicate enqueue, or an airborne readback) — nothing to do.
            if (Lru.TryGet(path) != null || Failed.ContainsKey(path) || InFlightPaths.Contains(path))
            {
                return;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                // Deleted while queued.
                CompileAttempts.Remove(path);
                return;
            }

            // Disk loads are not separately capped: the caller's stopwatch is
            // the real budget, and skipping a valid disk entry would fall
            // through to a far more expensive scene render of the same thumb.
            if (TryLoadFromDisk(path, guid))
            {
                diskLoads++;
                anyApplied = true;
                return;
            }

            if (renders >= maxRenders)
            {
                Requeue(path);
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                MarkFailed(path);
                return;
            }

            switch (CheckShaderReady(material))
            {
                case ShaderReadiness.Broken:
                    MarkFailed(path);
                    return;
                case ShaderReadiness.Compiling:
                    CompileAttempts.TryGetValue(path, out var attempts);
                    attempts++;
                    if (attempts > MaxShaderCompileAttempts)
                    {
                        MarkFailed(path);
                        return;
                    }
                    CompileAttempts[path] = attempts;
                    Requeue(path);
                    return;
            }

            CompileAttempts.Remove(path);
            renders++;
            RenderMaterial(path, guid, material, ref anyApplied);
        }

        private enum ShaderReadiness
        {
            Ready,
            Compiling,
            Broken,
        }

        // Gate every pass, not just pass 0 — a multi-pass shader with only
        // pass 0 warm still stalls the render. Default editor behavior
        // compiles missing variants SYNCHRONOUSLY inside our tick (async
        // compilation covers only Scene/Game views), so the async
        // CompilePass + re-queue is what keeps first-visit materials from
        // freezing the editor. Never flip ShaderUtil.allowAsyncCompilation
        // globally — that is what would introduce cyan-placeholder captures.
        private static ShaderReadiness CheckShaderReady(Material material)
        {
            var shader = material.shader;
            if (shader == null
                || string.Equals(shader.name, "Hidden/InternalErrorShader", StringComparison.Ordinal))
            {
                return ShaderReadiness.Broken;
            }

            var ready = true;
            var passCount = material.passCount;
            for (var pass = 0; pass < passCount; pass++)
            {
                if (ShaderUtil.IsPassCompiled(material, pass))
                {
                    continue;
                }

                ShaderUtil.CompilePass(material, pass);
                ready = false;
            }

            return ready ? ShaderReadiness.Ready : ShaderReadiness.Compiling;
        }

        private static void RenderMaterial(string path, string guid, Material material, ref bool anyApplied)
        {
            var depPaths = CollectDependencies(path);
            var depHashes = new string[depPaths.Length];
            for (var i = 0; i < depPaths.Length; i++)
            {
                depHashes[i] = AssetDatabase.GetAssetDependencyHash(depPaths[i]).ToString();
            }

            var materialHash = ComputeMaterialHash(path);

            _rig ??= new MaterialThumbnailRenderRig(ThumbnailSize);
            if (!_rig.Render(material))
            {
                // Skybox preview type or otherwise unrenderable here — the
                // stock pipeline owns those looks.
                MarkFailed(path);
                return;
            }

            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var request = AsyncGPUReadback.Request(_rig.Output, 0, TextureFormat.RGBA32);
                request.forcePlayerLoopUpdate = true;
                InFlight.Add(new InFlightReadback
                {
                    Path = path,
                    Guid = guid,
                    MaterialHash = materialHash,
                    DepPaths = depPaths,
                    DepHashes = depHashes,
                    Request = request,
                });
                InFlightPaths.Add(path);
            }
            else
            {
                // OpenGL fallback: synchronous GPU flush, capped at one per
                // tick by the caller's render budget.
                var pixels = ReadOutputSync();
                CompleteThumbnail(path, guid, materialHash, depPaths, depHashes, pixels);
                anyApplied = true;
            }
        }

        private static int DrainReadbacks(ref bool anyApplied)
        {
            var completed = 0;
            var forcedThisTick = false;
            for (var i = InFlight.Count - 1; i >= 0; i--)
            {
                var entry = InFlight[i];
                // Manual pump: edit mode does not tick the player loop, so
                // requests never auto-complete without it.
                entry.Request.Update();

                if (!entry.Request.done)
                {
                    entry.TicksPending++;
                    if (entry.TicksPending >= ForceCompleteAfterTicks && !forcedThisTick)
                    {
                        // One bounded flush per tick keeps the stall budget
                        // flat even when several readbacks are stuck.
                        forcedThisTick = true;
                        entry.Request.WaitForCompletion();
                    }
                    else
                    {
                        InFlight[i] = entry;
                        continue;
                    }
                }

                InFlight.RemoveAt(i);
                InFlightPaths.Remove(entry.Path);

                // Invalidated mid-flight: the pixels are pre-change state.
                // Discard them and re-queue — the request was still pumped to
                // completion, so no GPU resource is orphaned, and the requeue
                // guarantees a fresh render even if no rebind re-requests it.
                if (entry.Stale)
                {
                    Requeue(entry.Path);
                    continue;
                }

                // !done can only survive a WaitForCompletion on a broken
                // driver; treat it like an error rather than letting GetData
                // throw inside the update hook.
                if (entry.Request.hasError || !entry.Request.done)
                {
                    MarkFailed(entry.Path);
                    continue;
                }

                var pixels = entry.Request.GetData<byte>().ToArray();
                CompleteThumbnail(
                    entry.Path, entry.Guid, entry.MaterialHash, entry.DepPaths, entry.DepHashes, pixels);
                completed++;
                anyApplied = true;
            }

            return completed;
        }

        private static void CompleteThumbnail(
            string path, string guid, string materialHash, string[] depPaths, string[] depHashes, byte[] pixels)
        {
            if (pixels == null || pixels.Length != ThumbnailSize * ThumbnailSize * 4)
            {
                MarkFailed(path);
                return;
            }

            EncodeToSrgbIfLinear(pixels);
            MaterialThumbnailPixels.ApplyToneCurve(pixels);
            MaterialThumbnailPixels.CompositeOverBackground(pixels, ThumbnailSize, ThumbnailSize);
            MaterialThumbnailPixels.ApplyRoundedCornerMask(pixels, ThumbnailSize, ThumbnailSize);
            Lru.Add(path, CreateThumbTexture(path, pixels), depPaths);

            // Write-behind off the main thread: pure System.IO on data the
            // main thread no longer touches (the texture copied the pixels in
            // LoadRawTextureData). Worst case on domain reload is a lost or
            // torn temp file — the atomic move means the real entry is either
            // the old one or the complete new one.
            var record = new MaterialThumbnailDiskStore.Record(
                materialHash, depPaths, depHashes, ThumbnailSize, ThumbnailSize, pixels);
            var filePath = MaterialThumbnailDiskStore.PathForGuid(guid);
            Task.Run(() => MaterialThumbnailDiskStore.Write(filePath, record));
        }

        private static bool TryLoadFromDisk(string path, string guid)
        {
            var filePath = MaterialThumbnailDiskStore.PathForGuid(guid);
            _diskPixelBuffer ??= new byte[ThumbnailSize * ThumbnailSize * 4];
            if (!MaterialThumbnailDiskStore.TryRead(filePath, _diskPixelBuffer, out var record))
            {
                return false;
            }

            if (record.Width != ThumbnailSize || record.Height != ThumbnailSize)
            {
                MaterialThumbnailDiskStore.Delete(filePath);
                return false;
            }

            // The material's own dependency hash covers the .mat file, its
            // meta, and the importer — but NOT the contents of referenced
            // textures. The per-dependency hashes stored at render time close
            // that gap: a reimported normal map flips its own hash and the
            // stale thumb re-renders. A deleted dependency degenerates to the
            // zero hash and mismatches too.
            if (!string.Equals(record.MaterialHash, ComputeMaterialHash(path), StringComparison.Ordinal))
            {
                MaterialThumbnailDiskStore.Delete(filePath);
                return false;
            }

            for (var i = 0; i < record.DependencyPaths.Length; i++)
            {
                var current = AssetDatabase.GetAssetDependencyHash(record.DependencyPaths[i]).ToString();
                if (!string.Equals(record.DependencyHashes[i], current, StringComparison.Ordinal))
                {
                    MaterialThumbnailDiskStore.Delete(filePath);
                    return false;
                }
            }

            Lru.Add(path, CreateThumbTexture(path, record.Pixels), record.DependencyPaths);
            return true;
        }

        private static Texture2D CreateThumbTexture(string path, byte[] pixels)
        {
            // linear:false — the pixels are sRGB-encoded (the rig blits
            // through an sRGB target), so the texture must sample as sRGB.
            var texture = new Texture2D(
                ThumbnailSize, ThumbnailSize, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = "MaterialThumb:" + path,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.LoadRawTextureData(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        private static byte[] ReadOutputSync()
        {
            _syncReadStaging ??= new Texture2D(
                ThumbnailSize, ThumbnailSize, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = "MaterialThumbSyncStaging",
                hideFlags = HideFlags.HideAndDontSave,
            };

            var previous = RenderTexture.active;
            RenderTexture.active = _rig.Output;
            _syncReadStaging.ReadPixels(new Rect(0, 0, ThumbnailSize, ThumbnailSize), 0, 0);
            RenderTexture.active = previous;
            return _syncReadStaging.GetRawTextureData<byte>().ToArray();
        }

        // The pixel work lives in Core (MaterialThumbnailPixels). The sRGB encode
        // applies only in Linear color space: a Gamma project's readback is
        // already display-ready.
        internal static void EncodeToSrgbIfLinear(byte[] rgba)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                MaterialThumbnailPixels.EncodeLinearToSrgb(rgba);
            }
        }

        // Bump whenever the rendered LOOK changes (lights, backdrop, camera,
        // color pipeline): the disk cache is keyed on this, so every stale
        // pre-change thumbnail re-renders instead of surviving by hash.
        // v2: brighter rig + bounce light, deterministic CPU sRGB encode.
        // v3: product-shot treatment — rounded-corner tile mask, spotlight
        // backdrop + contact shadow, strong rim, tone curve, 8° pitch.
        // v4: bigger sphere (margin 1.04, ~96% of tile), shadow tucked lower.
        // v5: framing-math fix — camera distance was computed from the
        // bounding-box half-diagonal instead of the sphere radius, rendering
        // the sphere at ~58% of intended size in every prior version.
        private const int LookVersion = 5;

        private static string ComputeMaterialHash(string path)
        {
            PipelineSupported();
            // The pipeline tag makes thumbs rendered under one pipeline stale
            // under another (Built-in vs URP lighting differs visibly).
            return AssetDatabase.GetAssetDependencyHash(path) + "|" + _pipelineTag + "|look" + LookVersion;
        }

        private static string[] CollectDependencies(string path)
        {
            // Recursive: a material variant's look changes when its PARENT's
            // texture reimports, and the parent's textures are not direct
            // dependencies of the child .mat. Transitive closure keeps both
            // the stored dependency hashes and the live invalidation dep sets
            // honest across variant chains.
            var deps = AssetDatabase.GetDependencies(path, recursive: true);
            var count = 0;
            for (var i = 0; i < deps.Length; i++)
            {
                if (!string.Equals(deps[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    deps[count++] = deps[i];
                }
            }

            if (count == deps.Length)
            {
                return deps;
            }

            var trimmed = new string[count];
            Array.Copy(deps, trimmed, count);
            return trimmed;
        }

        // Front of the backlog, because the drain takes from the back: an
        // item that could not be served now must not be retried ahead of the
        // tiles that are actually on screen.
        private static void Requeue(string path)
        {
            if (PipelineSupported() && Pending.Count < MaxPending && PendingSet.Add(path))
            {
                Pending.Insert(0, path);
            }
        }

        // Import batches invalidate live thumbs whose material — or any
        // recorded dependency (texture, shader, parent material) — changed.
        // Rides the package's existing postprocessor event rather than
        // registering a second AssetPostprocessor. Runs inside the import
        // callback, so the cost must not scale with batch size × cache size:
        // the batch becomes one hash set and the LRU is scanned exactly once;
        // disk deletes (pure I/O on paths resolved here) go to a worker
        // thread. Disk entries invalidated only via a dependency are left to
        // the hash check on next load — same outcome, no reverse index.
        private static void OnAssetChanges(ProjectAssetChanges changes)
        {
            var totalChanged = changes.Imported.Length + changes.Deleted.Length
                + changes.Moved.Length + changes.MovedFromPaths.Length;
            if (totalChanged == 0)
            {
                return;
            }

            var changedSet = new HashSet<string>(totalChanged, StringComparer.OrdinalIgnoreCase);
            List<string> diskFilesToDelete = null;
            // Only content changes (Imported) invalidate the GUID-keyed disk thumbnail.
            // Moves/renames keep the GUID and the content, so their disk thumb is still valid
            // — scheduling its deletion destroyed a good cache entry and forced a full scene
            // re-render on the new path. All batches still add to changedSet so the stale
            // old-path LRU entry is dropped and the new path re-binds (warm-loading the
            // surviving disk thumb).
            CollectChanged(changes.Imported, changedSet, ref diskFilesToDelete, scheduleDiskDelete: true);
            CollectChanged(changes.Moved, changedSet, ref diskFilesToDelete, scheduleDiskDelete: false);
            CollectChanged(changes.MovedFromPaths, changedSet, ref diskFilesToDelete, scheduleDiskDelete: false);
            CollectChanged(changes.Deleted, changedSet, ref diskFilesToDelete, scheduleDiskDelete: false);

            var anyRemoved = Lru.RemoveWhereDependsOnAny(changedSet) > 0;
            List<string> failuresToRetry = null;
            foreach (var failed in Failed)
            {
                if (!DependsOnAny(failed.Key, failed.Value, changedSet)) continue;
                failuresToRetry ??= new List<string>();
                failuresToRetry.Add(failed.Key);
            }
            if (failuresToRetry != null)
            {
                for (var i = 0; i < failuresToRetry.Count; i++)
                    Failed.Remove(failuresToRetry[i]);
            }

            // Airborne readbacks rendered from pre-change state: flag them so
            // DrainReadbacks discards the result and re-queues, instead of
            // letting a stale thumb land in the LRU where nothing would ever
            // re-validate it.
            for (var i = 0; i < InFlight.Count; i++)
            {
                var entry = InFlight[i];
                if (entry.Stale || !DependsOnAny(entry.Path, entry.DepPaths, changedSet))
                {
                    continue;
                }

                entry.Stale = true;
                InFlight[i] = entry;
            }

            if (diskFilesToDelete != null)
            {
                var files = diskFilesToDelete;
                Task.Run(() =>
                {
                    for (var i = 0; i < files.Count; i++)
                    {
                        MaterialThumbnailDiskStore.Delete(files[i]);
                    }
                });
            }

            if (anyRemoved || failuresToRetry != null)
            {
                // Visible tiles leave invalidated textures or retry revived
                // failures. Owned textures are destroyed on a later pump tick.
                ThumbnailsReady?.Invoke();
                EnsurePumpRunning();
            }
        }

        private static void CollectChanged(
            string[] paths, HashSet<string> changedSet, ref List<string> diskFilesToDelete, bool scheduleDiskDelete)
        {
            for (var i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                changedSet.Add(path);
                CompileAttempts.Remove(path);

                if (scheduleDiskDelete && path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    // GUID resolution must happen here on the main thread;
                    // only the file deletion is deferred. Deleted materials
                    // resolve to an empty GUID and their orphaned entries age
                    // out via the session-start prune instead.
                    ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid))
                    {
                        diskFilesToDelete ??= new List<string>();
                        diskFilesToDelete.Add(MaterialThumbnailDiskStore.PathForGuid(guid));
                    }
                }
            }
        }

        private static bool DependsOnAny(string path, string[] dependencies, HashSet<string> changedSet)
        {
            if (changedSet.Contains(path))
            {
                return true;
            }

            for (var i = 0; i < dependencies.Length; i++)
            {
                if (changedSet.Contains(dependencies[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ReleaseRig()
        {
            _rig?.Dispose();
            _rig = null;
            if (_syncReadStaging != null)
            {
                UnityEngine.Object.DestroyImmediate(_syncReadStaging);
                _syncReadStaging = null;
            }
        }
    }
}
