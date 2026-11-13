using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.Application;
using Zep.Core.State;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// An <see cref="IAssetRecordSource"/> that enumerates assets directly from the
    /// filesystem, bypassing <c>AssetDatabase.GetAllAssetPaths</c> and per-asset
    /// <c>AssetPathToGUID</c> / <c>GetMainAssetTypeAtPath</c> calls.
    ///
    /// Strategy:
    ///   \u2022 Recursively walk <c>Assets/</c> plus every immediate subdirectory of
    ///     <c>Packages/</c> that contains a <c>package.json</c>.
    ///   \u2022 Skip <c>.meta</c> files and hidden files.
    ///   \u2022 Read each sibling <c>.meta</c> header for its guid. Unity labels are
    ///     prepared from Unity's label index on the main thread, with direct YAML parsing
    ///     retained for incremental records and label-index failures.
    ///   \u2022 Infer built-in types from their extension, then project a main-thread
    ///     concrete-type snapshot onto ambiguous `.asset` and custom importer formats.
    ///
    /// Limitations:
    ///   \u2022 Assets that live outside the filesystem (e.g. virtual AssetDatabase v2
    ///     entries) are not visible.  The existing
    ///     <see cref="AssetDatabaseAssetRecordSource"/> remains available as a
    ///     correctness oracle and can be switched back on via a setting.
    /// </summary>
    public sealed class NativeFileSystemAssetRecordSource : IAssetRecordSource
    {
        private IReadOnlyList<AssetFolderRecord> _cachedFolderRecords = Array.Empty<AssetFolderRecord>();
        private IReadOnlyDictionary<string, string> _preparedTagSearchTextByGuid;
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _preparedUnityLabelsByGuid;
        private IReadOnlyDictionary<string, AssetTypeMetadata> _preparedTypesByGuid;
        private IReadOnlyDictionary<string, AssetFolderRecord> _preparedPackageRootsByPath;
        // Null distinguishes no prepared enumeration from a valid complete empty mapping.
        private IReadOnlyList<(string VirtualPrefix, string PhysicalRoot)> _preparedPackageRoots;
        private bool _hasEnumeratedFolderSnapshot;

        // Filesystem enumeration and .meta parsing are pure System.IO; no Unity APIs are
        // touched during GetRecords(), so the synchronizer can dispatch it to a worker
        // thread safely provided PrepareForEnumeration() is called on the main thread
        // first.
        public bool IsThreadSafe => true;

        public void PrepareForEnumeration(
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
        {
            _hasEnumeratedFolderSnapshot = false;
            _preparedPackageRoots = null;
            _preparedPackageRootsByPath = null;
            _preparedTagSearchTextByGuid = null;
            _preparedUnityLabelsByGuid = null;
            _preparedTypesByGuid = null;
            if (!AssetRecordSourceHost.Current.TryRecoverTagState(out var error))
            {
                throw new ProjectTagStateUnavailableException(error);
            }

            AssetRecordSourceHost.Current.PrepareInferredTypeMetadata();
            // ScriptableSingleton.instance and EnsureDefaults touch Unity state; resolve
            // them here on the main thread so the background enumeration only reads
            // already-materialised references.
            var tagSettings = AssetRecordSourceHost.Current.TagDefinitions;
            var tagStore = AssetRecordSourceHost.Current.TagAssignments;
            tagSettings.EnsureDefaults();
            tagStore.ReloadIfFileChanged();
            var tagSearchText = BuildTagSearchTextSnapshot(tagSettings, tagStore);
            var labels = unityLabelsByGuid
                ?? AssetRecordSourceHost.Current.BuildUnityLabelSnapshot();
            var packageRoots = HybridAssetRecordSource.GetRegisteredPackageRoots();
            var packageRootsByPath = BuildAuthoritativePackageRootSnapshot(labels, packageRoots);
            var types = typesByGuid
                ?? AssetRecordSourceHost.Current.BuildAuthoritativeTypeSnapshot();
            if (types == null)
            {
                throw new InvalidOperationException(
                    "Unity type metadata was unavailable; background enumeration cannot infer custom asset types safely.");
            }

            _preparedTagSearchTextByGuid = tagSearchText;
            _preparedUnityLabelsByGuid = labels;
            _preparedPackageRootsByPath = packageRootsByPath;
            _preparedTypesByGuid = types;
            _preparedPackageRoots = packageRoots;
        }

        public IEnumerable<AssetRecord> GetRecords()
        {
            _hasEnumeratedFolderSnapshot = false;
            var packageRoots = _preparedPackageRoots;
            var packageRootsByPath = _preparedPackageRootsByPath;
            _preparedPackageRoots = null;
            _preparedPackageRootsByPath = null;
            var tagSearchTextByGuid = _preparedTagSearchTextByGuid;
            var unityLabelsByGuid = _preparedUnityLabelsByGuid;
            var typesByGuid = _preparedTypesByGuid;
            if (packageRoots == null)
            {
                // Direct-call path (not via SynchronizeAsync) must acquire a
                // complete current package mapping before traversal.
                if (!AssetRecordSourceHost.Current.TryRecoverTagState(out var error))
                {
                    throw new ProjectTagStateUnavailableException(error);
                }

                AssetRecordSourceHost.Current.PrepareInferredTypeMetadata();
                var tagSettings = AssetRecordSourceHost.Current.TagDefinitions;
                var tagStore = AssetRecordSourceHost.Current.TagAssignments;
                tagSettings.EnsureDefaults();
                tagStore.ReloadIfFileChanged();
                tagSearchTextByGuid = BuildTagSearchTextSnapshot(tagSettings, tagStore);
                unityLabelsByGuid = AssetRecordSourceHost.Current.BuildUnityLabelSnapshot();
                packageRoots = HybridAssetRecordSource.GetRegisteredPackageRoots();
                packageRootsByPath = BuildAuthoritativePackageRootSnapshot(
                    unityLabelsByGuid,
                    packageRoots);
                typesByGuid = AssetRecordSourceHost.Current.BuildAuthoritativeTypeSnapshot();
                if (typesByGuid == null)
                {
                    throw new InvalidOperationException(
                        "Unity type metadata was unavailable; background enumeration cannot infer custom asset types safely.");
                }
            }

            // Pass 1 (single-threaded): walk the directory tree and collect candidate
            // asset paths + folder paths. This is cheap (no per-file I/O beyond
            // EnumerateFiles).  We deliberately separate it from pass 2 so we can hand
            // a flat work list to Parallel.ForEach.
            var folders = new List<string>();
            // A real project reaches six figures of assets; starting at 64 made the
            // cold scan regrow this list ~17 times, each regrowth a large-object-heap
            // copy. 16k costs 128 KB up front and lands most projects in one block.
            var candidatePaths = new List<string>(1 << 14);
            var walkedPackageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in GetRoots())
            {
                if (root.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    folders.Add(root);
                    walkedPackageRoots.Add(root);
                }

                CollectCandidatesAndFolders(root, candidatePaths, folders);
            }

            // Package files live under Library/PackageCache (or an external file: path), so
            // their virtual "Packages/com.x/..." path is not a filesystem path. Collect them
            // as (physical, virtual) pairs — read each .meta from the physical file but record
            // it under the virtual path — so the panel browses packages like Unity. The
            // package set was resolved on the main thread (Package Manager is main-thread only).
            var packageCandidates = new List<(string Physical, string Virtual)>();
            foreach (var package in packageRoots)
            {
                // Embedded packages (a real Packages/<name>/package.json on disk) are already
                // walked by GetRoots above; re-walking them here via their resolved physical
                // path would emit a SECOND record per asset (same virtual path, same guid) and
                // bloat the persisted cache. Only non-embedded packages (registry/git/file:
                // living outside the project) need this pass. Check the roots actually walked
                // above rather than probing a virtual path that Package Manager may resolve.
                if (walkedPackageRoots.Contains(package.VirtualPrefix))
                {
                    continue;
                }

                var packageFiles = new List<string>();
                var packageFolders = new List<string>();
                CollectCandidatesAndFolders(package.PhysicalRoot, packageFiles, packageFolders);
                folders.Add(package.VirtualPrefix);
                for (var i = 0; i < packageFiles.Count; i++)
                {
                    packageCandidates.Add((packageFiles[i], HybridAssetRecordSource.RemapToVirtual(packageFiles[i], package.PhysicalRoot, package.VirtualPrefix)));
                }

                for (var i = 0; i < packageFolders.Count; i++)
                {
                    folders.Add(HybridAssetRecordSource.RemapToVirtual(
                        packageFolders[i],
                        package.PhysicalRoot,
                        package.VirtualPrefix));
                }
            }

            var folderRecords = BuildFolderRecords(
                folders,
                unityLabelsByGuid,
                packageRootsByPath,
                packageRoots);

            if (candidatePaths.Count == 0 && packageCandidates.Count == 0)
            {
                _cachedFolderRecords = folderRecords;
                _hasEnumeratedFolderSnapshot = true;
                return Array.Empty<AssetRecord>();
            }

            // Pass 2 (parallel): for each candidate, read its .meta file and build the
            // record. .meta reads are independent, so workers do not contend; thread-
            // local lists eliminate the need to lock on a shared output.
            //
            // Empirically the .meta read dominates this phase (~60% I/O, ~40% record
            // construction). On 8-core hardware this gives 4-6x throughput vs the
            // previous serial walk and is the single biggest cold-start lever.
            var records = new List<AssetRecord>(candidatePaths.Count);
            var partials = new ConcurrentBag<List<AssetRecord>>();

            // Cap parallelism at processor count; more workers do not help disk I/O
            // and only add scheduling overhead.
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };

            Parallel.ForEach(
                candidatePaths,
                parallelOptions,
                () => new List<AssetRecord>(candidatePaths.Count / Math.Max(1, parallelOptions.MaxDegreeOfParallelism) + 1),
                (path, _, local) =>
                {
                    IReadOnlyList<string> unityLabels = Array.Empty<string>();
                    var guid = unityLabelsByGuid == null
                        ? UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(path + ".meta", out unityLabels)
                        : UnityMetaFileReader.ReadGuidFromMetaFile(path + ".meta");
                    if (!string.IsNullOrEmpty(guid))
                    {
                        if (unityLabelsByGuid != null
                            && !unityLabelsByGuid.TryGetValue(guid, out unityLabels))
                        {
                            unityLabels = Array.Empty<string>();
                        }

                        local.Add(CreateRecord(
                            path,
                            guid,
                            AssetRecordSourceHost.Current.ResolveTypeMetadata(path, guid, typesByGuid),
                            tagSearchTextByGuid,
                            unityLabels));
                    }

                    return local;
                },
                local => partials.Add(local));

            foreach (var partial in partials)
            {
                records.AddRange(partial);
            }

            // Package records: read each file's .meta from its physical path, store the
            // record under its virtual Packages/... path. Serial — this runs on the
            // background scan thread and the result is persisted to the index cache.
            for (var i = 0; i < packageCandidates.Count; i++)
            {
                IReadOnlyList<string> unityLabels = Array.Empty<string>();
                var guid = unityLabelsByGuid == null
                    ? UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                        packageCandidates[i].Physical + ".meta",
                        out unityLabels)
                    : UnityMetaFileReader.ReadGuidFromMetaFile(packageCandidates[i].Physical + ".meta");
                if (!string.IsNullOrEmpty(guid))
                {
                    if (unityLabelsByGuid != null
                        && !unityLabelsByGuid.TryGetValue(guid, out unityLabels))
                    {
                        unityLabels = Array.Empty<string>();
                    }

                    records.Add(CreateRecord(
                        packageCandidates[i].Virtual,
                        guid,
                        AssetRecordSourceHost.Current.ResolveTypeMetadata(
                            packageCandidates[i].Virtual,
                            guid,
                            typesByGuid),
                        tagSearchTextByGuid,
                        unityLabels));
                }
            }

            _cachedFolderRecords = folderRecords;
            _hasEnumeratedFolderSnapshot = true;

            return records;
        }

        // The one candidate-and-folder walk in the package. HybridAssetRecordSource
        // calls it for its own roots and for every package root, so the single-asset
        // rule below cannot drift between sources.
        internal static void CollectCandidatesAndFolders(string rootFolder, List<string> candidatePaths, List<string> folderPaths)
        {
            var stack = new Stack<string>();
            stack.Push(rootFolder);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                EnsureTraversableDirectory(current);
                var subdirs = Directory.GetDirectories(current);
                var files = Directory.GetFiles(current);

                for (var i = 0; i < files.Length; i++)
                {
                    var raw = files[i];
                    if (string.IsNullOrEmpty(raw))
                    {
                        continue;
                    }

                    var path = raw.Replace('\\', '/');
                    if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var fileName = Path.GetFileName(path);
                    if (fileName.Length == 0 || fileName[0] == '.')
                    {
                        continue;
                    }

                    if (fileName.EndsWith("~", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    candidatePaths.Add(path);
                }

                for (var i = 0; i < subdirs.Length; i++)
                {
                    var dir = subdirs[i].Replace('\\', '/');
                    var name = Path.GetFileName(dir);
                    if (name.Length == 0 || name[0] == '.' || name.EndsWith("~", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    // Unity imports this directory as ONE asset, so it is a candidate,
                    // never a folder: recording it as both would let the panel navigate
                    // into an asset (InMemoryAssetIndex.ContainsVisibleFolder answers from
                    // the folder records), and descending would index internals that carry
                    // no GUID at all.
                    if (AssetPathClassification.IsUnitySingleAssetDirectory(name))
                    {
                        candidatePaths.Add(dir);
                        continue;
                    }

                    folderPaths.Add(dir);
                    stack.Push(dir);
                }
            }
        }

        private static void EnsureTraversableDirectory(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException exception)
            {
                throw new DirectoryNotFoundException(path, exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked directory cannot be traversed: " + path);
            }
        }

        public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
        {
            if (_hasEnumeratedFolderSnapshot)
            {
                _hasEnumeratedFolderSnapshot = false;
                return _cachedFolderRecords;
            }

            // Walk independently of GetRecords so empty folders remain visible even on
            // persistent-cache-hit startup paths (where GetRecords is not called at all).
            // The walk is O(#folders) with no per-file I/O, which is cheap.
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            var packageRoots = HybridAssetRecordSource.GetRegisteredPackageRoots();
            var folders = new List<string>();
            var walkedPackageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in GetRoots())
            {
                if (root.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    folders.Add(root);
                    walkedPackageRoots.Add(root);
                }

                CollectFoldersOnly(root, folders);
            }

            foreach (var package in packageRoots)
            {
                if (walkedPackageRoots.Contains(package.VirtualPrefix))
                {
                    continue;
                }

                var physicalFolders = new List<string>();
                CollectFoldersOnly(package.PhysicalRoot, physicalFolders);
                folders.Add(package.VirtualPrefix);
                for (var i = 0; i < physicalFolders.Count; i++)
                {
                    folders.Add(HybridAssetRecordSource.RemapToVirtual(
                        physicalFolders[i],
                        package.PhysicalRoot,
                        package.VirtualPrefix));
                }
            }

            var unityLabelsByGuid = AssetRecordSourceHost.Current.BuildUnityLabelSnapshot();
            _cachedFolderRecords = BuildFolderRecords(
                folders,
                unityLabelsByGuid,
                BuildAuthoritativePackageRootSnapshot(unityLabelsByGuid, packageRoots),
                packageRoots);
            __sw.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]     NativeFileSystemAssetRecordSource.GetFolderRecords folderCount={folders.Count} ms={__sw.Elapsed.TotalMilliseconds:F2}");
            return _cachedFolderRecords;
        }

        private static IReadOnlyList<AssetFolderRecord> BuildFolderRecords(
            IReadOnlyList<string> virtualPaths,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyDictionary<string, AssetFolderRecord> authoritativePackageRootsByPath,
            IReadOnlyList<(string VirtualPrefix, string PhysicalRoot)> packageRoots)
        {
            var records = new AssetFolderRecord[virtualPaths.Count];
            for (var i = 0; i < virtualPaths.Count; i++)
            {
                var virtualPath = virtualPaths[i];
                if (authoritativePackageRootsByPath != null
                    && authoritativePackageRootsByPath.TryGetValue(
                        virtualPath,
                        out var authoritativePackageRoot))
                {
                    records[i] = authoritativePackageRoot;
                    continue;
                }

                var physicalPath = HybridAssetRecordSource.ResolvePhysicalAssetPath(
                    virtualPath,
                    packageRoots);
                var guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    physicalPath + ".meta",
                    out var unityLabels);
                if (unityLabelsByGuid != null
                    && !string.IsNullOrEmpty(guid))
                {
                    unityLabels = unityLabelsByGuid.TryGetValue(guid, out var resolvedLabels)
                        ? resolvedLabels
                        : Array.Empty<string>();
                }

                records[i] = new AssetFolderRecord(
                    AssetPath.From(virtualPath),
                    guid,
                    unityLabels);
            }

            return records;
        }

        private static IReadOnlyDictionary<string, AssetFolderRecord> BuildAuthoritativePackageRootSnapshot(
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid,
            IReadOnlyList<(string VirtualPrefix, string PhysicalRoot)> packageRoots)
        {
            var recordsByPath = new Dictionary<string, AssetFolderRecord>(
                packageRoots.Count,
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < packageRoots.Count; i++)
            {
                var virtualPath = packageRoots[i].VirtualPrefix;
                recordsByPath[virtualPath] =
                    AssetRecordSourceHost.Current.BuildFolderRecord(
                        virtualPath,
                        unityLabelsByGuid);
            }

            return recordsByPath;
        }

        // The one folders-only walk in the package; see CollectCandidatesAndFolders.
        internal static void CollectFoldersOnly(string rootFolder, List<string> folderPaths)
        {
            var stack = new Stack<string>();
            stack.Push(rootFolder);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                EnsureTraversableDirectory(current);
                var subdirs = Directory.GetDirectories(current);

                for (var i = 0; i < subdirs.Length; i++)
                {
                    var dir = subdirs[i].Replace('\\', '/');
                    var name = Path.GetFileName(dir);
                    if (name.Length == 0 || name[0] == '.' || name.EndsWith("~", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    // See CollectCandidatesAndFolders: a single-asset directory is an
                    // asset, so this walk neither records it as a folder nor enters it.
                    if (AssetPathClassification.IsUnitySingleAssetDirectory(name))
                    {
                        continue;
                    }

                    folderPaths.Add(dir);
                    stack.Push(dir);
                }
            }
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

            var physicalPath = HybridAssetRecordSource.ResolvePhysicalAssetPath(
                raw,
                HybridAssetRecordSource.ResolvedRoots);
            // A directory Unity imports as one asset has no FILE at its own path; its
            // GUID lives in the .meta beside it, which the read below opens as an
            // ordinary file. Any other path with nothing on disk is still not an asset.
            if (!File.Exists(physicalPath)
                && !AssetPathClassification.IsUnitySingleAssetDirectory(Path.GetFileName(raw)))
            {
                return null;
            }

            string guid;
            IReadOnlyList<string> unityLabels;
            try
            {
                guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
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
            if (string.IsNullOrEmpty(guid))
            {
                // The .meta file may not have been generated yet (fresh import). Fall back
                // to Unity which will import-on-demand and synthesise the guid.
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                guid = AssetRecordSourceHost.Current.AssetPathToGuid(raw);
                if (string.IsNullOrEmpty(guid))
                {
                    return null;
                }
            }

            if (!AssetRecordSourceHost.Current.TryRecoverTagState(out var error))
            {
                throw new ProjectTagStateUnavailableException(error);
            }

            var tagSettings = AssetRecordSourceHost.Current.TagDefinitions;
            var tagStore = AssetRecordSourceHost.Current.TagAssignments;
            tagSettings.EnsureDefaults();
            tagStore.ReloadIfFileChanged();

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

            var physicalPath = HybridAssetRecordSource.ResolvePhysicalAssetPath(
                raw,
                HybridAssetRecordSource.ResolvedRoots);
            if (!Directory.Exists(physicalPath))
            {
                return null;
            }

            string guid;
            IReadOnlyList<string> unityLabels;
            try
            {
                guid = UnityMetaFileReader.ReadGuidAndLabelsFromMetaFile(
                    physicalPath + ".meta",
                    out unityLabels);
            }
            catch (IOException)
            {
                return AssetRecordSourceHost.Current.BuildFolderRecordFromAssetDatabase(folderPath);
            }
            catch (UnauthorizedAccessException)
            {
                return AssetRecordSourceHost.Current.BuildFolderRecordFromAssetDatabase(folderPath);
            }
            if (string.IsNullOrEmpty(guid))
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                guid = AssetRecordSourceHost.Current.AssetPathToGuid(raw);
            }

            return new AssetFolderRecord(folderPath, guid, unityLabels);
        }

        private static IEnumerable<string> GetRoots()
        {
            if (Directory.Exists("Assets"))
            {
                yield return "Assets";
            }

            if (!Directory.Exists("Packages"))
            {
                yield break;
            }

            // Only yield package folders that contain a package.json - this avoids
            // recursing into the packages-lock.json / manifest and into folders that
            // Unity isn't treating as packages.
            foreach (var dir in Directory.EnumerateDirectories("Packages"))
            {
                var normalized = dir.Replace('\\', '/');
                if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (File.Exists(normalized + "/package.json"))
                {
                    yield return normalized;
                }
            }
        }

        private static AssetRecord CreateRecord(
            string assetPath,
            string guid,
            AssetTypeMetadata type,
            TagDefinitionStore tagSettings,
            TagAssignmentStore tagStore,
            IReadOnlyList<string> unityLabels)
        {
            return CreateRecord(
                assetPath,
                guid,
                type,
                tagSettings.BuildTagSearchText(tagStore.GetTagIds(guid)),
                unityLabels);
        }

        private static AssetRecord CreateRecord(
            string assetPath,
            string guid,
            AssetTypeMetadata type,
            IReadOnlyDictionary<string, string> tagSearchTextByGuid,
            IReadOnlyList<string> unityLabels)
        {
            var tagSearchText = tagSearchTextByGuid.TryGetValue(guid, out var resolved)
                ? resolved
                : string.Empty;
            return CreateRecord(assetPath, guid, type, tagSearchText, unityLabels);
        }

        private static AssetRecord CreateRecord(
            string assetPath,
            string guid,
            AssetTypeMetadata type,
            string tagSearchText,
            IReadOnlyList<string> unityLabels)
        {
            var fileName = Path.GetFileName(assetPath);
            var name = Path.GetFileNameWithoutExtension(assetPath);
            if (string.IsNullOrEmpty(name))
            {
                name = fileName;
            }

            return new AssetRecord(
                guid,
                assetPath,
                name,
                type.TypeName,
                tagSearchText,
                unityLabels,
                type.BaseTypeNames);
        }

        private static IReadOnlyDictionary<string, string> BuildTagSearchTextSnapshot(
            TagDefinitionStore tagSettings,
            TagAssignmentStore tagStore)
        {
            var assignments = tagStore.GetAllTagIdsByGuid();
            var snapshot = new Dictionary<string, string>(
                assignments.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in assignments)
            {
                snapshot[assignment.Key] = tagSettings.BuildTagSearchText(assignment.Value);
            }

            return snapshot;
        }
    }

    public sealed class NativeDirectFolderAssetSource : IDirectFolderAssetSource
    {
        public DirectFolderObservation Observe(AssetPath folderPath)
        {
            if (folderPath.IsEmpty)
            {
                throw new ArgumentException("A folder path is required.", nameof(folderPath));
            }

            if (string.Equals(
                    folderPath.Value,
                    "Packages",
                    StringComparison.OrdinalIgnoreCase))
            {
                var packageSnapshot = AssetRecordSourceHost.Current.RefreshPackageRoots();
                var packageEntries = new List<DirectFolderEntry>(
                    packageSnapshot.Roots.Count);
                for (var i = 0; i < packageSnapshot.Roots.Count; i++)
                {
                    var packageRoot = packageSnapshot.Roots[i];
                    packageEntries.Add(new DirectFolderEntry(
                        AssetPath.From(packageRoot.VirtualPrefix),
                        isFolder: true,
                        string.IsNullOrEmpty(packageRoot.DisplayName)
                            ? Path.GetFileName(packageRoot.VirtualPrefix)
                            : packageRoot.DisplayName,
                        "Folder"));
                }

                packageEntries.Sort(static (left, right) => string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase));
                return new DirectFolderObservation(
                    folderPath,
                    packageEntries,
                    packageSnapshot.IsComplete);
            }

            if (folderPath.Value.StartsWith(
                    "Packages/",
                    StringComparison.OrdinalIgnoreCase)
                && !AssetRecordSourceHost.Current.PackageRoots.IsComplete)
            {
                AssetRecordSourceHost.Current.RefreshPackageRoots();
            }

            var physicalFolder = AssetRecordSourceHost.Current.ToAbsolutePath(folderPath.Value);
            if (string.IsNullOrEmpty(physicalFolder)
                || !Directory.Exists(physicalFolder))
            {
                return new DirectFolderObservation(
                    folderPath,
                    Array.Empty<DirectFolderEntry>(),
                    isComplete: false);
            }

            try
            {
                var entries = new List<DirectFolderEntry>();
                var directories = Directory.GetDirectories(physicalFolder);
                for (var i = 0; i < directories.Length; i++)
                {
                    var physicalPath = directories[i];
                    if (!IsVisibleDirectEntry(physicalPath, isDirectory: true))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(physicalPath);
                    if (AssetPathClassification.IsUnitySingleAssetDirectory(name))
                    {
                        // Unity imports this directory as one asset, so the first paint
                        // must already say "asset", and must derive the display name
                        // exactly as the file branch below does — the hydrated record
                        // strips the suffix the same way, and a name that disagrees
                        // relabels the row in the very window this is meant to keep quiet.
                        var singleAssetPath = folderPath.Value + "/" + name;
                        var singleAssetName = Path.GetFileNameWithoutExtension(name);
                        entries.Add(new DirectFolderEntry(
                            AssetPath.From(singleAssetPath),
                            isFolder: false,
                            string.IsNullOrEmpty(singleAssetName) ? name : singleAssetName,
                            AssetExtensionTypeMap.GetTypeNameForPath(singleAssetPath)));
                        continue;
                    }

                    entries.Add(new DirectFolderEntry(
                        AssetPath.From(folderPath.Value + "/" + name),
                        isFolder: true,
                        name,
                        "Folder"));
                }

                var files = Directory.GetFiles(physicalFolder);
                for (var i = 0; i < files.Length; i++)
                {
                    var physicalPath = files[i];
                    if (!IsVisibleDirectEntry(physicalPath, isDirectory: false))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(physicalPath);
                    var virtualPath = folderPath.Value + "/" + fileName;
                    var displayName = Path.GetFileNameWithoutExtension(fileName);
                    entries.Add(new DirectFolderEntry(
                        AssetPath.From(virtualPath),
                        isFolder: false,
                        string.IsNullOrEmpty(displayName) ? fileName : displayName,
                        AssetExtensionTypeMap.GetTypeNameForPath(virtualPath)));
                }

                entries.Sort(static (left, right) =>
                {
                    if (left.IsFolder != right.IsFolder)
                    {
                        return left.IsFolder ? -1 : 1;
                    }

                    var byName = string.Compare(
                        left.Name,
                        right.Name,
                        StringComparison.OrdinalIgnoreCase);
                    return byName != 0
                        ? byName
                        : string.Compare(
                            left.Path.Value,
                            right.Path.Value,
                            StringComparison.OrdinalIgnoreCase);
                });
                return new DirectFolderObservation(
                    folderPath,
                    entries,
                    isComplete: true);
            }
            catch (IOException)
            {
                return new DirectFolderObservation(
                    folderPath,
                    Array.Empty<DirectFolderEntry>(),
                    isComplete: false);
            }
            catch (UnauthorizedAccessException)
            {
                return new DirectFolderObservation(
                    folderPath,
                    Array.Empty<DirectFolderEntry>(),
                    isComplete: false);
            }
        }

        private static bool IsVisibleDirectEntry(string physicalPath, bool isDirectory)
        {
            var attributes = File.GetAttributes(physicalPath);
            return (attributes & FileAttributes.ReparsePoint) == 0
                && !FilesystemEntryOperations.ShouldIgnoreUnityImportEntry(
                    physicalPath,
                    isDirectory);
        }
    }
}
