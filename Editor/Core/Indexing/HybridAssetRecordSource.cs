using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.Application;
using Zep.Core.State;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// Synchronous-path record source that combines a native filesystem walk with
    /// Unity's in-memory guid hashtable:
    ///
    ///   \u2022 We bypass <c>AssetDatabase.GetAllAssetPaths()</c> (Unity has to format
    ///     and copy a fresh string array for each call) - native enumeration is cheaper.
    ///   \u2022 We infer built-in types from extension and query Unity only for
    ///     ambiguous `.asset`/custom formats, preserving concrete user types.
    ///   \u2022 We ask Unity's in-memory guid and label indexes for authoritative
    ///     metadata. Direct <c>.meta</c> parsing remains the fallback when the label
    ///     index is unavailable.
    ///
    /// <para>This source is <see cref="IsThreadSafe"/> = false because it calls Unity
    /// APIs. The controller pairs it with <see cref="NativeFileSystemAssetRecordSource"/>
    /// for the asynchronous startup path so the editor still stays interactive on
    /// first-ever launch when the persistent cache is empty.</para>
    /// </summary>
    public sealed class HybridAssetRecordSource : IAssetRecordSource
    {
        private IReadOnlyList<AssetFolderRecord> _cachedFolderRecords = Array.Empty<AssetFolderRecord>();
        private TagDefinitionStore _preparedTagSettings;
        private TagAssignmentStore _preparedTagStore;
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _preparedUnityLabelsByGuid;
        private IReadOnlyDictionary<string, AssetTypeMetadata> _preparedTypesByGuid;
        // Null is the explicit no-prepared-scan marker. Prepared roots are
        // consumed once so stale Package Manager data cannot escape a failed
        // subsequent preparation.
        private IReadOnlyList<(string VirtualPrefix, string PhysicalRoot)> _preparedPackageRoots;
        private bool _hasEnumeratedFolderSnapshot;

        public bool IsThreadSafe => false;

        public void PrepareForEnumeration(
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
        {
            _hasEnumeratedFolderSnapshot = false;
            _preparedPackageRoots = null;
            _preparedTagSettings = null;
            _preparedTagStore = null;
            _preparedUnityLabelsByGuid = null;
            _preparedTypesByGuid = null;
            if (!AssetRecordSourceHost.Current.TryRecoverTagState(out var error))
            {
                throw new ProjectTagStateUnavailableException(error);
            }

            AssetRecordSourceHost.Current.PrepareInferredTypeMetadata();
            var tagSettings = AssetRecordSourceHost.Current.TagDefinitions;
            var tagStore = AssetRecordSourceHost.Current.TagAssignments;
            tagSettings.EnsureDefaults();
            tagStore.ReloadIfFileChanged();
            var labels = unityLabelsByGuid
                ?? AssetRecordSourceHost.Current.BuildUnityLabelSnapshot();
            var types = typesByGuid
                ?? AssetRecordSourceHost.Current.BuildAuthoritativeTypeSnapshot();
            var packageRoots = GetRegisteredPackageRoots();

            _preparedTagSettings = tagSettings;
            _preparedTagStore = tagStore;
            _preparedUnityLabelsByGuid = labels;
            _preparedTypesByGuid = types;
            _preparedPackageRoots = packageRoots;
        }

        public IEnumerable<AssetRecord> GetRecords()
        {
            _hasEnumeratedFolderSnapshot = false;
            var packageRoots = _preparedPackageRoots;
            _preparedPackageRoots = null;
            if (!AssetRecordSourceHost.Current.TryRecoverTagState(out var error))
            {
                throw new ProjectTagStateUnavailableException(error);
            }

            var tagSettings = _preparedTagSettings ?? AssetRecordSourceHost.Current.TagDefinitions;
            var tagStore = _preparedTagStore ?? AssetRecordSourceHost.Current.TagAssignments;
            var unityLabelsByGuid = _preparedUnityLabelsByGuid;
            var typesByGuid = _preparedTypesByGuid;
            if (_preparedTagSettings == null)
            {
                tagSettings.EnsureDefaults();
                tagStore.ReloadIfFileChanged();
                unityLabelsByGuid = AssetRecordSourceHost.Current.BuildUnityLabelSnapshot();
                typesByGuid = AssetRecordSourceHost.Current.BuildAuthoritativeTypeSnapshot();
            }

            if (packageRoots == null)
            {
                AssetRecordSourceHost.Current.PrepareInferredTypeMetadata();
                packageRoots = GetRegisteredPackageRoots();
            }

            // Phase 1 (single-threaded): walk the directory tree and collect candidate
            // asset paths + folder paths.
            var folders = new List<string>();
            var candidatePaths = new List<string>(64);
            foreach (var root in GetRoots())
            {
                NativeFileSystemAssetRecordSource.CollectCandidatesAndFolders(
                    root,
                    candidatePaths,
                    folders);
            }

            foreach (var package in packageRoots)
            {
                CollectPackageCandidatesAndFolders(package.PhysicalRoot, package.VirtualPrefix, candidatePaths, folders);
            }

            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]     HybridAssetRecordSource.GetRecords candidates={candidatePaths.Count} folders={folders.Count} packageRoots={packageRoots.Count}");

            if (candidatePaths.Count == 0)
            {
                _cachedFolderRecords = BuildFolderRecords(folders, unityLabelsByGuid);
                _hasEnumeratedFolderSnapshot = true;
                return Array.Empty<AssetRecord>();
            }

            // Phase 2 (single-threaded, must be main-thread): resolve guids via Unity's
            // in-memory hashtable. AssetDatabase.AssetPathToGUID is main-thread only,
            // so this loop cannot be parallelised. It is, however, very cheap (~5\u00b5s
            // per call: pure dict lookup, no I/O).
            var pathGuidPairs = new (string Path, string PhysicalPath, string Guid, AssetTypeMetadata Type)[candidatePaths.Count];
            var validCount = 0;
            for (var i = 0; i < candidatePaths.Count; i++)
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                var guid = AssetRecordSourceHost.Current.AssetPathToGuid(candidatePaths[i]);
                if (!string.IsNullOrEmpty(guid))
                {
                    pathGuidPairs[validCount++] = (
                        candidatePaths[i],
                        unityLabelsByGuid == null
                            ? ResolvePhysicalAssetPath(candidatePaths[i], packageRoots)
                            : null,
                        guid,
                        typesByGuid == null
                            ? AssetRecordSourceHost.Current.ResolveMainThreadTypeMetadata(candidatePaths[i])
                            : AssetRecordSourceHost.Current.ResolveTypeMetadata(
                                candidatePaths[i],
                                guid,
                                typesByGuid));
                }
            }

            // Phase 3: project the main-thread label snapshot onto record ordinals. If
            // Unity's label index was temporarily unavailable, fall back to parallel
            // physical .meta scans without calling Unity APIs from workers.
            var unityLabelsByOrdinal = new IReadOnlyList<string>[validCount];
            if (unityLabelsByGuid != null)
            {
                for (var i = 0; i < validCount; i++)
                {
                    unityLabelsByOrdinal[i] = unityLabelsByGuid.TryGetValue(
                        pathGuidPairs[i].Guid,
                        out var unityLabels)
                            ? unityLabels
                            : Array.Empty<string>();
                }
            }
            else
            {
                Parallel.For(
                    0,
                    validCount,
                    i => UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                        pathGuidPairs[i].PhysicalPath + ".meta",
                        out unityLabelsByOrdinal[i]));
            }

            // Phase 4 (parallel): construct AssetRecords. This is the heaviest phase
            // (string normalisation, BuildNormalizedSearchText) and is pure C# - no
            // Unity APIs after the guid is known - so it parallelises cleanly. The tag
            // dictionary reads inside CreateRecord are concurrent-safe as long as no
            // tag mutations are running, which is true during a cold scan.
            //
            // Empirically, parallel construction only beats serial above ~13K assets
            // on this workload (Parallel.For setup, ConcurrentBag merge, and per-thread
            // string allocation overhead dominate below that). We pivot at 16K with a
            // safety margin; below the threshold the serial path is strictly faster.
            const int ParallelThreshold = 16000;
            if (validCount < ParallelThreshold)
            {
                var serialRecords = new List<AssetRecord>(validCount);
                for (var i = 0; i < validCount; i++)
                {
                    var pair = pathGuidPairs[i];
                    serialRecords.Add(CreateRecord(
                        pair.Path,
                        pair.Guid,
                        pair.Type,
                        tagSettings,
                        tagStore,
                        unityLabelsByOrdinal[i]));
                }

                _cachedFolderRecords = BuildFolderRecords(folders, unityLabelsByGuid);
                _hasEnumeratedFolderSnapshot = true;
                return serialRecords;
            }

            var records = new List<AssetRecord>(validCount);
            var partials = new ConcurrentBag<List<AssetRecord>>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };

            Parallel.For(
                0,
                validCount,
                parallelOptions,
                () => new List<AssetRecord>(validCount / Math.Max(1, parallelOptions.MaxDegreeOfParallelism) + 1),
                (i, _, local) =>
                {
                    var pair = pathGuidPairs[i];
                    local.Add(CreateRecord(
                        pair.Path,
                        pair.Guid,
                        pair.Type,
                        tagSettings,
                        tagStore,
                        unityLabelsByOrdinal[i]));
                    return local;
                },
                local => partials.Add(local));

            foreach (var partial in partials)
            {
                records.AddRange(partial);
            }

            _cachedFolderRecords = BuildFolderRecords(folders, unityLabelsByGuid);
            _hasEnumeratedFolderSnapshot = true;

            return records;
        }

        public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
        {
            if (_hasEnumeratedFolderSnapshot)
            {
                _hasEnumeratedFolderSnapshot = false;
                return _cachedFolderRecords;
            }

            // IMPORTANT: walk independently of GetRecords. The persistent cache hydrates
            // the asset index without calling GetRecords, so _cachedFolderRecords would
            // otherwise stay empty and empty folders would vanish from the panel. The
            // walk is O(#folders) which is small (hundreds to low thousands even on a
            // 50k-asset project) and does no per-file I/O.
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            var packageRoots = GetRegisteredPackageRoots();
            var folders = new List<string>();
            foreach (var root in GetRoots())
            {
                NativeFileSystemAssetRecordSource.CollectFoldersOnly(root, folders);
            }

            foreach (var package in packageRoots)
            {
                CollectPackageFoldersOnly(package.PhysicalRoot, package.VirtualPrefix, folders);
            }

            _cachedFolderRecords = BuildFolderRecords(
                folders,
                AssetRecordSourceHost.Current.BuildUnityLabelSnapshot());
            __sw.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]     HybridAssetRecordSource.GetFolderRecords folderCount={folders.Count} packageRoots={packageRoots.Count} ms={__sw.Elapsed.TotalMilliseconds:F2}");
            return _cachedFolderRecords;
        }

        private static IReadOnlyList<AssetFolderRecord> BuildFolderRecords(
            IReadOnlyList<string> rawPaths,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid)
        {
            var converted = new AssetFolderRecord[rawPaths.Count];
            for (var i = 0; i < rawPaths.Count; i++)
            {
                converted[i] = AssetRecordSourceHost.Current.BuildFolderRecord(
                    rawPaths[i],
                    unityLabelsByGuid);
            }

            return converted;
        }

        public AssetRecord BuildRecord(AssetPath assetPath)
        {
            if (assetPath.IsEmpty)
            {
                return null;
            }

            string raw = assetPath;

            if (raw.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (AssetRecordSourceHost.Current.IsValidFolder(raw))
            {
                return null;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            var guid = AssetRecordSourceHost.Current.AssetPathToGuid(raw);
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            if (!AssetRecordSourceHost.Current.TryRecoverTagState(out var error))
            {
                throw new ProjectTagStateUnavailableException(error);
            }

            var tagSettings = AssetRecordSourceHost.Current.TagDefinitions;
            var tagStore = AssetRecordSourceHost.Current.TagAssignments;
            tagSettings.EnsureDefaults();
            tagStore.ReloadIfFileChanged();

            var physicalPath = ResolvePhysicalAssetPath(raw, ResolvedRoots);
            IReadOnlyList<string> unityLabels;
            try
            {
                UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    physicalPath + ".meta",
                    out unityLabels);
            }
            catch (IOException)
            {
                return AssetRecordSourceHost.Current.BuildRecordFromAssetDatabase(assetPath);
            }
            catch (UnauthorizedAccessException)
            {
                return AssetRecordSourceHost.Current.BuildRecordFromAssetDatabase(assetPath);
            }

            return CreateRecord(
                raw,
                guid,
                AssetRecordSourceHost.Current.ResolveIncrementalTypeMetadata(raw),
                tagSettings,
                tagStore,
                unityLabels);
        }

        public AssetFolderRecord BuildFolderRecord(AssetPath folderPath)
        {
            if (folderPath.IsEmpty)
            {
                return null;
            }

            string raw = folderPath;
            if (!AssetRecordSourceHost.Current.IsValidFolder(raw))
            {
                return null;
            }

            return AssetRecordSourceHost.Current.BuildFolderRecord(raw);
        }

        // Assets only. Packages are enumerated separately via GetRegisteredPackageRoots;
        // listing the physical Packages/ folder here would miss every registry/git/file:
        // package, whose files live in Library/PackageCache or at an external path rather
        // than under <project>/Packages/.
        private static IEnumerable<string> GetRoots()
        {
            if (Directory.Exists("Assets"))
            {
                yield return "Assets";
            }
        }

        // Compatibility view for existing source/tests. The state owner is
        // RegisteredPackageRoots; this property never scans PackageCache or
        // retains a second package-membership snapshot.
        internal static IReadOnlyList<(string VirtualPrefix, string PhysicalRoot)> ResolvedRoots
        {
            get => ToPairs(AssetRecordSourceHost.Current.PackageRoots);
            set
            {
                var roots = new List<RegisteredPackageRoot>(value?.Count ?? 0);
                if (value != null)
                {
                    for (var i = 0; i < value.Count; i++)
                    {
                        roots.Add(new RegisteredPackageRoot(
                            value[i].VirtualPrefix,
                            value[i].PhysicalRoot,
                            value[i].VirtualPrefix));
                    }
                }

                AssetRecordSourceHost.Current.PackageRoots =
                    new RegisteredPackageRootSnapshot(roots, isComplete: true);
            }
        }

        internal static List<(string VirtualPrefix, string PhysicalRoot)> GetRegisteredPackageRoots()
        {
            var snapshot = AssetRecordSourceHost.Current.RefreshPackageRoots();
            if (!snapshot.IsComplete)
            {
                throw new InvalidOperationException(
                    "Package metadata was unavailable; authoritative enumeration requires a complete package mapping.");
            }

            var result = new List<(string VirtualPrefix, string PhysicalRoot)>(ToPairs(snapshot));
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]     GetRegisteredPackageRoots packages={result.Count}");
            return result;
        }

        // Per-path resolution asks for these pairs (ResolvePhysicalAssetPath), so an
        // import of 5,000 files used to build 5,000 identical lists. Snapshots are
        // immutable and swapped as one reference, so identity is a sound cache key.
        // The pair is held in one object and published with a single reference write:
        // the readers are the parallel scan threads, which must never observe a list
        // that belongs to a different snapshot.
        private static Tuple<RegisteredPackageRootSnapshot,
            List<(string VirtualPrefix, string PhysicalRoot)>> _pairsCache;

        private static List<(string VirtualPrefix, string PhysicalRoot)> ToPairs(
            RegisteredPackageRootSnapshot snapshot)
        {
            var cached = Volatile.Read(ref _pairsCache);
            if (cached != null && ReferenceEquals(cached.Item1, snapshot))
            {
                return cached.Item2;
            }

            var pairs = BuildPairs(snapshot);
            Volatile.Write(
                ref _pairsCache,
                Tuple.Create(snapshot, pairs));
            return pairs;
        }

        private static List<(string VirtualPrefix, string PhysicalRoot)> BuildPairs(
            RegisteredPackageRootSnapshot snapshot)
        {
            var roots = snapshot?.Roots;
            var result = new List<(string VirtualPrefix, string PhysicalRoot)>(
                roots?.Count ?? 0);
            if (roots != null)
            {
                for (var i = 0; i < roots.Count; i++)
                {
                    result.Add((roots[i].VirtualPrefix, roots[i].PhysicalRoot));
                }
            }

            return result;
        }

        private static void CollectPackageFoldersOnly(string physicalRoot, string virtualPrefix, List<string> folderPaths)
        {
            // The package root folder itself ("Packages/com.x"). CollectFoldersOnly only
            // yields the root's descendants, but the sidebar tree needs this node as the
            // direct child of "Packages" — without it the parent->children map has no
            // children under "Packages" and the whole Packages tree renders empty.
            folderPaths.Add(virtualPrefix);
            var physicalFolders = new List<string>();
            NativeFileSystemAssetRecordSource.CollectFoldersOnly(physicalRoot, physicalFolders);
            for (var i = 0; i < physicalFolders.Count; i++)
            {
                folderPaths.Add(RemapToVirtual(physicalFolders[i], physicalRoot, virtualPrefix));
            }
        }

        private static void CollectPackageCandidatesAndFolders(string physicalRoot, string virtualPrefix, List<string> candidatePaths, List<string> folderPaths)
        {
            // The package root folder itself (see CollectPackageFoldersOnly) — the direct
            // child of "Packages" the tree needs as the package's top node.
            folderPaths.Add(virtualPrefix);
            var physicalCandidates = new List<string>();
            var physicalFolders = new List<string>();
            NativeFileSystemAssetRecordSource.CollectCandidatesAndFolders(
                physicalRoot,
                physicalCandidates,
                physicalFolders);
            for (var i = 0; i < physicalFolders.Count; i++)
            {
                folderPaths.Add(RemapToVirtual(physicalFolders[i], physicalRoot, virtualPrefix));
            }
            for (var i = 0; i < physicalCandidates.Count; i++)
            {
                candidatePaths.Add(RemapToVirtual(physicalCandidates[i], physicalRoot, virtualPrefix));
            }
        }

        // The collectors emit forward-slashed paths that start with physicalRoot (which is
        // normalized: forward slashes, no trailing slash), so swapping that prefix for the
        // package's virtual prefix yields the Packages/<name>/... path Unity's guid
        // hashtable (AssetPathToGUID) and the rest of the panel expect.
        internal static string RemapToVirtual(string physicalPath, string physicalRoot, string virtualPrefix)
        {
            if (physicalPath.Length <= physicalRoot.Length)
            {
                return virtualPrefix;
            }

            return virtualPrefix + physicalPath.Substring(physicalRoot.Length);
        }

        internal static string ResolvePhysicalAssetPath(
            string assetPath,
            IReadOnlyList<(string VirtualPrefix, string PhysicalRoot)> packageRoots)
        {
            if (string.IsNullOrEmpty(assetPath)
                || !assetPath.StartsWith("Packages/", StringComparison.Ordinal)
                || packageRoots == null)
            {
                return assetPath;
            }

            for (var i = 0; i < packageRoots.Count; i++)
            {
                var package = packageRoots[i];
                if (string.Equals(assetPath, package.VirtualPrefix, StringComparison.Ordinal))
                {
                    return package.PhysicalRoot;
                }

                if (assetPath.Length > package.VirtualPrefix.Length
                    && assetPath.StartsWith(package.VirtualPrefix, StringComparison.Ordinal)
                    && assetPath[package.VirtualPrefix.Length] == '/')
                {
                    return package.PhysicalRoot + assetPath.Substring(package.VirtualPrefix.Length);
                }
            }

            // Embedded packages are already reachable at their virtual path. Returning the
            // original also makes a not-yet-resolved package degrade to no labels instead of
            // blocking the record itself.
            return assetPath;
        }

        private static AssetRecord CreateRecord(
            string assetPath,
            string guid,
            AssetTypeMetadata type,
            TagDefinitionStore tagSettings,
            TagAssignmentStore tagStore,
            IReadOnlyList<string> unityLabels)
        {
            var name = Path.GetFileNameWithoutExtension(assetPath);
            if (string.IsNullOrEmpty(name))
            {
                name = Path.GetFileName(assetPath);
            }

            var tagSearchText = tagSettings.BuildTagSearchText(tagStore.GetTagIds(guid));
            return new AssetRecord(
                guid,
                assetPath,
                name,
                type.TypeName,
                tagSearchText,
                unityLabels,
                type.BaseTypeNames);
        }
    }
}
