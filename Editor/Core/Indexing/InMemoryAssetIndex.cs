using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;

namespace Zep.Core.Indexing
{
    // Filesystem-only membership observed before Unity has supplied identity.
    // Pending entries are keyed by path and deliberately cannot be converted
    // to AssetRecord: only an authoritative Unity record can carry a GUID.
    internal sealed class PendingAssetEntry
    {
        internal PendingAssetEntry(
            string path,
            string name,
            string inferredTypeName,
            bool isFolder,
            string observedFolder,
            long observationGeneration)
        {
            Path = path ?? string.Empty;
            Name = name ?? string.Empty;
            InferredTypeName = isFolder
                ? "Folder"
                : inferredTypeName ?? string.Empty;
            IsFolder = isFolder;
            ObservedFolder = observedFolder ?? string.Empty;
            ObservationGeneration = observationGeneration;
        }

        internal string Path { get; }

        internal string Name { get; }

        internal string InferredTypeName { get; }

        internal bool IsFolder { get; }

        internal string ObservedFolder { get; }

        internal long ObservationGeneration { get; }

        internal bool HasSameFilesystemFacts(DirectFolderEntry entry)
        {
            return string.Equals(Path, entry.Path.Value, StringComparison.Ordinal)
                && string.Equals(Name, entry.Name, StringComparison.Ordinal)
                && string.Equals(
                    InferredTypeName,
                    entry.InferredTypeName,
                    StringComparison.Ordinal)
                && IsFolder == entry.IsFolder;
        }
    }

    internal readonly struct AssetCatalogMove
    {
        public AssetCatalogMove(string oldPath, string newPath)
        {
            OldPath = oldPath ?? string.Empty;
            NewPath = newPath ?? string.Empty;
        }

        internal string OldPath { get; }

        internal string NewPath { get; }
    }

    // One atomic catalog result. Consumers receive before/after records from the
    // canonical owner instead of independently discovering and mutating the same
    // path in the catalog and each focused derived index/cache.
    public sealed class AssetCatalogDelta
    {
        internal AssetCatalogDelta(int previousRecordVersion)
        {
            PreviousRecordVersion = previousRecordVersion;
        }

        private List<AssetRecord> _removedRecords;
        private List<AssetRecord> _upsertedRecords;
        private List<AssetFolderRecord> _removedFolders;
        private List<AssetFolderRecord> _upsertedFolders;
        private List<PendingAssetEntry> _removedPendingEntries;
        private List<PendingAssetEntry> _upsertedPendingEntries;
        private HashSet<string> _affectedParentFolders;

        internal int PreviousRecordVersion { get; }

        // The collections are created on first touch instead of on construction.
        // Most applies in a session change nothing and are never read: every
        // Upsert/Remove/Replace builds a delta, and several callers - the script
        // type reconciliation above all - discard it entirely. Seven eager
        // collections per apply were pure garbage on that path. Reading a barren
        // delta is safe too: every consumer outside this file guards on IsEmpty,
        // and IsEmpty reads the fields rather than the properties.
        public List<AssetRecord> RemovedRecords =>
            _removedRecords ??= new List<AssetRecord>();

        public List<AssetRecord> UpsertedRecords =>
            _upsertedRecords ??= new List<AssetRecord>();

        internal List<AssetFolderRecord> RemovedFolders =>
            _removedFolders ??= new List<AssetFolderRecord>();

        internal List<AssetFolderRecord> UpsertedFolders =>
            _upsertedFolders ??= new List<AssetFolderRecord>();

        internal List<PendingAssetEntry> RemovedPendingEntries =>
            _removedPendingEntries ??= new List<PendingAssetEntry>();

        internal List<PendingAssetEntry> UpsertedPendingEntries =>
            _upsertedPendingEntries ??= new List<PendingAssetEntry>();

        public HashSet<string> AffectedParentFolders =>
            _affectedParentFolders ??=
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool IsEmpty => IsEmptyList(_removedRecords)
            && IsEmptyList(_upsertedRecords)
            && IsEmptyList(_removedFolders)
            && IsEmptyList(_upsertedFolders)
            && IsEmptyList(_removedPendingEntries)
            && IsEmptyList(_upsertedPendingEntries);

        // Test seam for the guarantee above: an apply that changes nothing must
        // leave every collection uncreated, which is otherwise invisible.
        internal bool HasAllocatedCollections => _removedRecords != null
            || _upsertedRecords != null
            || _removedFolders != null
            || _upsertedFolders != null
            || _removedPendingEntries != null
            || _upsertedPendingEntries != null
            || _affectedParentFolders != null;

        private static bool IsEmptyList<T>(List<T> values) =>
            values == null || values.Count == 0;
    }

    public sealed class InMemoryAssetIndex
    {
        private readonly Dictionary<string, AssetRecord> _records =
            new Dictionary<string, AssetRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _guidByPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssetFolderRecord> _foldersByPath =
            new Dictionary<string, AssetFolderRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _fileGuidsByParent =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _folderPathsByParent =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingAssetEntry> _pendingByPath =
            new Dictionary<string, PendingAssetEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _pendingPathsByParent =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _typeCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _guidsByType =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Guids of the records whose main-object type the extension map does not by
        // itself determine: an `.asset` or an unmapped extension (both infer to an
        // ambiguous placeholder), or a record already carrying an importer-resolved
        // type. These are the only records a script reload can retype, and they are
        // a small minority of a real catalog, so keeping them addressable is what
        // lets the reconciliation skip the rest instead of walking everything.
        private readonly HashSet<string> _typeAmbiguousGuids =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _directChildCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _nonEmptyFolderPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Folders a COMPLETE direct observation reported and this index ACCEPTED:
        // a read of the filesystem the catalog holds no record for yet. They
        // answer ContainsVisibleFolder, so folder existence has one owner instead
        // of a second opinion parked beside it. The rule is freshness, not
        // precedence - an entry is retired the moment newer evidence arrives, and
        // newer evidence is exactly five things: Replace (the snapshot supersedes
        // every observation older than it), Clear, a delete of the path or an
        // ancestor, a move of the path, and a re-observation that comes back
        // incomplete. Only directly browsed folders land here, so it holds tens of
        // entries, not thousands.
        private readonly HashSet<string> _observedFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // How many pending KEYS RemovePendingPathOrSubtree has looked at. The
        // removal is otherwise indistinguishable from the full-scan it replaced -
        // same entries removed, same delta - so this counter is the only way a
        // test can tell that unrelated folders' keys were never enumerated.
        private long _pendingRemovalKeyProbes;

        private int _version;
        private int _recordVersion;
        private int _filterableFolderCount;
        private long _directObservationGeneration;

        internal int Count => _records.Count;

        internal int FolderCount => _foldersByPath.Count;

        internal int PendingCount => _pendingByPath.Count;

        internal long PendingRemovalKeyProbes => _pendingRemovalKeyProbes;

        internal int FilterableFolderCount => _filterableFolderCount;

        internal int Version => _version;

        internal int RecordVersion => _recordVersion;

        internal IReadOnlyDictionary<string, int> TypeCounts => _typeCounts;

        internal IReadOnlyCollection<string> NonEmptyFolderPaths => _nonEmptyFolderPaths;

        internal bool IsFolderNonEmpty(string folderPath)
        {
            return !string.IsNullOrEmpty(folderPath)
                && _nonEmptyFolderPaths.Contains(folderPath);
        }

        internal IReadOnlyCollection<string> ObservedFolders => _observedFolders;

        internal bool IsObservedFolder(string folderPath)
        {
            return !string.IsNullOrEmpty(folderPath)
                && _observedFolders.Contains(folderPath);
        }

        // Emptiness is a FACT, not a default: the answer is true only where the
        // index actually knows the folder and knows its direct-child count is
        // zero. "Known" is deliberately narrower than ContainsVisibleFolder,
        // which also answers for the Assets/Packages roots and for a pending
        // folder entry the index has not listed yet - those are folders it knows
        // EXIST, not folders whose contents it has read, and answering true for
        // them is exactly the reload-window defect this replaces. Observed
        // children are counted: ApplyDirectObservation links each one through
        // AddChild(_pendingPathsByParent, ...), which is what maintains
        // _nonEmptyFolderPaths.
        internal bool IsFolderKnownEmpty(string folderPath)
        {
            return !string.IsNullOrEmpty(folderPath)
                && (_foldersByPath.ContainsKey(folderPath)
                    || _observedFolders.Contains(folderPath))
                && !_nonEmptyFolderPaths.Contains(folderPath);
        }

        internal string GetAnyAssetPathForType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)
                || !_guidsByType.TryGetValue(typeName, out var guids))
            {
                return null;
            }

            foreach (var guid in guids)
            {
                if (_records.TryGetValue(guid, out var record))
                {
                    return record.AssetPath;
                }
            }

            return null;
        }

        internal bool Contains(string guid)
        {
            return !string.IsNullOrEmpty(guid) && _records.ContainsKey(guid);
        }

        internal bool TryGet(string guid, out AssetRecord record)
        {
            if (string.IsNullOrEmpty(guid))
            {
                record = null;
                return false;
            }

            return _records.TryGetValue(guid, out record);
        }

        internal bool TryGetByPath(string assetPath, out AssetRecord record)
        {
            if (string.IsNullOrEmpty(assetPath)
                || !_guidByPath.TryGetValue(assetPath, out var guid))
            {
                record = null;
                return false;
            }

            return _records.TryGetValue(guid, out record);
        }

        internal bool TryGetFolder(string folderPath, out AssetFolderRecord record)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                record = null;
                return false;
            }

            return _foldersByPath.TryGetValue(folderPath, out record);
        }

        internal bool ContainsVisibleFolder(string folderPath)
        {
            return !string.IsNullOrEmpty(folderPath)
                && (_foldersByPath.ContainsKey(folderPath)
                    || _observedFolders.Contains(folderPath)
                    || (_pendingByPath.TryGetValue(folderPath, out var pending)
                        && pending.IsFolder)
                    || string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(folderPath, "Packages", StringComparison.OrdinalIgnoreCase));
        }

        internal bool TryGetPending(string path, out PendingAssetEntry pending)
        {
            return _pendingByPath.TryGetValue(path ?? string.Empty, out pending);
        }

        internal void Upsert(AssetRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            Apply(
                Array.Empty<string>(),
                Array.Empty<AssetCatalogMove>(),
                new[] { record },
                Array.Empty<AssetFolderRecord>());
        }

        internal bool Remove(string guid)
        {
            if (string.IsNullOrEmpty(guid) || !_records.TryGetValue(guid, out var record))
            {
                return false;
            }

            return !Apply(
                new[] { record.AssetPath },
                Array.Empty<AssetCatalogMove>(),
                Array.Empty<AssetRecord>(),
                Array.Empty<AssetFolderRecord>()).IsEmpty;
        }

        internal void Clear()
        {
            // Above the guard, not inside the block below it: an observation of an
            // EMPTY folder produces no record, no folder and no pending entry, so
            // the guard would see an "already empty" catalog and let the entry
            // survive its own Clear. It is retired without touching the versions
            // because registering it did not move them either (its delta is
            // empty), and the guard's whole job is to keep a no-op Clear from
            // advancing what every derived cache rebuilds on.
            _observedFolders.Clear();

            if (_records.Count == 0
                && _foldersByPath.Count == 0
                && _pendingByPath.Count == 0)
            {
                return;
            }

            _records.Clear();
            _guidByPath.Clear();
            _foldersByPath.Clear();
            _fileGuidsByParent.Clear();
            _folderPathsByParent.Clear();
            _pendingByPath.Clear();
            _pendingPathsByParent.Clear();
            _typeCounts.Clear();
            _guidsByType.Clear();
            _typeAmbiguousGuids.Clear();
            _directChildCounts.Clear();
            _nonEmptyFolderPaths.Clear();
            _filterableFolderCount = 0;
            // The observation generation counts within the lifetime of the catalog
            // it stamps, so it restarts with the catalog. Safe because the ONLY
            // reader compares it against PendingAssetEntry.ObservationGeneration
            // (ApplyDirectObservation's completeness prune) and every pending entry
            // that could carry a pre-Clear stamp was just dropped - so nothing can
            // observe a generation from before this point. Were that ever to stop
            // being true, an entry stamped higher than the restarted counter would
            // survive the prune forever, which is what the Clear-then-reobserve
            // test pins.
            _directObservationGeneration = 0;
            _recordVersion++;
            _version++;
        }

        internal IEnumerable<AssetRecord> Enumerate() => _records.Values;

        internal IEnumerable<AssetFolderRecord> EnumerateFolders() => _foldersByPath.Values;

        internal IEnumerable<AssetRecord> EnumerateDirectFiles(string parentFolder)
        {
            if (string.IsNullOrEmpty(parentFolder)
                || !_fileGuidsByParent.TryGetValue(parentFolder, out var guids))
            {
                yield break;
            }

            foreach (var guid in guids)
            {
                if (_records.TryGetValue(guid, out var record))
                {
                    yield return record;
                }
            }
        }

        internal IEnumerable<AssetFolderRecord> EnumerateDirectFolders(string parentFolder)
        {
            if (string.IsNullOrEmpty(parentFolder)
                || !_folderPathsByParent.TryGetValue(parentFolder, out var paths))
            {
                yield break;
            }

            foreach (var path in paths)
            {
                if (_foldersByPath.TryGetValue(path, out var folder))
                {
                    yield return folder;
                }
            }
        }

        internal IEnumerable<PendingAssetEntry> EnumerateDirectPending(
            string parentFolder)
        {
            if (string.IsNullOrEmpty(parentFolder)
                || !_pendingPathsByParent.TryGetValue(parentFolder, out var paths))
            {
                yield break;
            }

            foreach (var path in paths)
            {
                if (_pendingByPath.TryGetValue(path, out var pending))
                {
                    yield return pending;
                }
            }
        }

        internal AssetCatalogDelta ApplyDirectObservation(
            DirectFolderObservation observation,
            int catalogVersionAtStart)
        {
            var delta = new AssetCatalogDelta(_recordVersion);
            if (observation == null
                || catalogVersionAtStart != _version
                || observation.FolderPath.IsEmpty)
            {
                return delta;
            }

            var observedFolder = observation.FolderPath.Value;
            // Below the early return above, so only an observation the index
            // ACCEPTED is recorded: an entry built from a rejected observation
            // would claim the folder exists while none of that observation's
            // entries were applied, and the pane would be kept over contents
            // nothing published. A complete observation is the newest read of the
            // filesystem for this path; an incomplete one is the index learning
            // the folder is gone, and retires what an earlier one recorded.
            if (observation.IsComplete)
            {
                _observedFolders.Add(observedFolder);
            }
            else
            {
                _observedFolders.Remove(observedFolder);
            }

            var generation = ++_directObservationGeneration;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < observation.Entries.Count; i++)
            {
                var entry = observation.Entries[i];
                var path = entry.Path.Value;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                // Bound rather than discarded: the containment check already pays
                // for this Substring, so the removals below can reuse it instead of
                // deriving the same parent again. Keeping the derived string (not
                // observedFolder, which only has to match case-insensitively) means
                // the delta records exactly the spelling it recorded before.
                var parentFolder = ZepPathHelpers.GetParentFolder(path);
                if (!string.Equals(
                        parentFolder,
                        observedFolder,
                        StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(path))
                {
                    continue;
                }

                if (_guidByPath.ContainsKey(path) || _foldersByPath.ContainsKey(path))
                {
                    RemovePendingCore(path, delta, parentFolder);
                    continue;
                }

                if (_pendingByPath.TryGetValue(path, out var existing)
                    && existing.HasSameFilesystemFacts(entry))
                {
                    // Refresh provenance without publishing a visual change.
                    _pendingByPath[path] = new PendingAssetEntry(
                        path,
                        entry.Name,
                        entry.InferredTypeName,
                        entry.IsFolder,
                        observedFolder,
                        generation);
                    continue;
                }

                RemovePendingCore(path, delta, parentFolder);
                var pending = new PendingAssetEntry(
                    path,
                    entry.Name,
                    entry.InferredTypeName,
                    entry.IsFolder,
                    observedFolder,
                    generation);
                _pendingByPath[path] = pending;
                AddChild(_pendingPathsByParent, observedFolder, path);
                delta.UpsertedPendingEntries.Add(pending);
                delta.AffectedParentFolders.Add(observedFolder);
            }

            if (observation.IsComplete
                && _pendingPathsByParent.TryGetValue(observedFolder, out var pendingPaths))
            {
                var snapshot = new List<string>(pendingPaths);
                for (var i = 0; i < snapshot.Count; i++)
                {
                    var path = snapshot[i];
                    if (!seen.Contains(path)
                        && _pendingByPath.TryGetValue(path, out var pending)
                        && string.Equals(
                            pending.ObservedFolder,
                            observedFolder,
                            StringComparison.OrdinalIgnoreCase)
                        && pending.ObservationGeneration <= generation)
                    {
                        RemovePendingCore(path, delta);
                    }
                }
            }

            if (!delta.IsEmpty)
            {
                _version++;
            }

            return delta;
        }

        internal AssetCatalogDelta ApplyFileChanges(
            IReadOnlyList<AssetRecord> upserts,
            IReadOnlyList<string> removedPaths)
        {
            return Apply(
                removedPaths,
                Array.Empty<AssetCatalogMove>(),
                upserts,
                Array.Empty<AssetFolderRecord>());
        }

        internal AssetCatalogDelta Apply(
            IReadOnlyList<string> removedRoots,
            IReadOnlyList<AssetCatalogMove> moves,
            IReadOnlyList<AssetRecord> recordUpserts,
            IReadOnlyList<AssetFolderRecord> folderUpserts)
        {
            var delta = new AssetCatalogDelta(_recordVersion);

            if (moves != null)
            {
                for (var i = 0; i < moves.Count; i++)
                {
                    ApplyMove(moves[i], delta);
                }
            }

            if (removedRoots != null)
            {
                for (var i = 0; i < removedRoots.Count; i++)
                {
                    RemovePathOrSubtree(removedRoots[i], delta);
                }
            }

            if (folderUpserts != null)
            {
                for (var i = 0; i < folderUpserts.Count; i++)
                {
                    UpsertFolderCore(folderUpserts[i], delta);
                }
            }

            if (recordUpserts != null)
            {
                for (var i = 0; i < recordUpserts.Count; i++)
                {
                    UpsertRecordCore(recordUpserts[i], delta);
                }
            }

            if (!delta.IsEmpty)
            {
                if (delta.RemovedRecords.Count > 0 || delta.UpsertedRecords.Count > 0)
                {
                    _recordVersion++;
                }

                _version++;
            }

            return delta;
        }

        internal void Replace(
            IReadOnlyList<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folders)
        {
            // Replace publishes a whole snapshot, and two of its callers routinely
            // publish the snapshot the index already holds: cache hydration after a
            // domain reload, and an authoritative pass that found nothing new. The
            // versions are how every derived cache decides whether to rebuild, so
            // they must describe the CONTENT, not the act of republishing it — the
            // comparison runs against the live maps before they are cleared.
            var previousRecordCount = _records.Count;
            var previousFolderCount = _foldersByPath.Count;
            var recordsMatchedCurrent = RecordsMatchCurrent(records);
            var foldersMatchedCurrent = FoldersMatchCurrent(folders);
            _records.Clear();
            _guidByPath.Clear();
            _foldersByPath.Clear();
            _fileGuidsByParent.Clear();
            _folderPathsByParent.Clear();
            _pendingPathsByParent.Clear();
            _typeCounts.Clear();
            _guidsByType.Clear();
            _typeAmbiguousGuids.Clear();
            _directChildCounts.Clear();
            _nonEmptyFolderPaths.Clear();
            // Every observation is older than the snapshot being published here,
            // so none of them is evidence any more. Note the asymmetry with the
            // pending entries below, which are deliberately re-linked and kept: a
            // pending entry is filesystem evidence about a CHILD ASSET the
            // snapshot may not have imported yet, while an observed-folder entry
            // is a second opinion about folder existence that the snapshot has
            // just settled.
            _observedFolders.Clear();
            _filterableFolderCount = 0;

            var delta = new AssetCatalogDelta(_recordVersion);
            if (folders != null)
            {
                for (var i = 0; i < folders.Count; i++)
                {
                    UpsertFolderCore(folders[i], delta);
                }
            }

            if (records != null)
            {
                for (var i = 0; i < records.Count; i++)
                {
                    UpsertRecordCore(records[i], delta);
                }
            }

            foreach (var pending in _pendingByPath.Values)
            {
                AddChild(
                    _pendingPathsByParent,
                    ZepPathHelpers.GetParentFolder(pending.Path),
                    pending.Path);
            }

            // The pre-clear probe proved every incoming entry equals one the index
            // already held and that the two inputs are the same SIZE as the maps
            // were. Duplicates in the input would satisfy both and still leave an
            // old entry uncovered, so the rebuilt counts close it: equal counts on
            // both sides plus a per-entry match is set equality.
            var recordsChanged = !recordsMatchedCurrent
                || _records.Count != previousRecordCount;
            var foldersChanged = !foldersMatchedCurrent
                || _foldersByPath.Count != previousFolderCount;
            if (recordsChanged)
            {
                _recordVersion++;
            }

            if (recordsChanged || foldersChanged)
            {
                _version++;
            }
        }

        // True when `records` names exactly the record set the index holds right
        // now, field for field (AssetRecord.Equals covers guid, path, name, type,
        // tag search text, labels and base types). Cheap enough to pay on every
        // Replace: one dictionary probe per incoming record, against a rebuild
        // that re-inserts all of them into eleven maps.
        private bool RecordsMatchCurrent(IReadOnlyList<AssetRecord> records)
        {
            var count = records?.Count ?? 0;
            if (count != _records.Count)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var record = records[i];
                if (record == null
                    || string.IsNullOrEmpty(record.Guid)
                    || !_records.TryGetValue(record.Guid, out var existing)
                    || !existing.Equals(record))
                {
                    return false;
                }
            }

            return true;
        }

        // The same field-for-field folder comparison UpsertFolderCore uses to
        // decide that an upsert is a no-op — including the ordinal path compare,
        // which the case-insensitive map key cannot make: a folder whose only
        // change is its casing IS a change to everything that renders it.
        private bool FoldersMatchCurrent(IReadOnlyList<AssetFolderRecord> folders)
        {
            var count = folders?.Count ?? 0;
            if (count != _foldersByPath.Count)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var folder = folders[i];
                if (folder == null
                    || folder.FolderPath.IsEmpty
                    || !_foldersByPath.TryGetValue(
                        folder.FolderPath.Value,
                        out var existing)
                    || !FolderRecordsEqual(existing, folder))
                {
                    return false;
                }
            }

            return true;
        }

        internal void ReplaceFolders(IReadOnlyList<AssetFolderRecord> folders)
        {
            var replacement = new Dictionary<string, AssetFolderRecord>(StringComparer.OrdinalIgnoreCase);
            if (folders != null)
            {
                for (var i = 0; i < folders.Count; i++)
                {
                    var folder = folders[i];
                    if (folder != null && !folder.FolderPath.IsEmpty)
                    {
                        replacement[folder.FolderPath.Value] = folder;
                    }
                }
            }

            EnsureFolderAndAncestors(replacement, "Assets");
            EnsureFolderAndAncestors(replacement, "Packages");
            // The keys of _fileGuidsByParent are exactly the parent folders that hold at least
            // one record (AddChild creates the bucket, RemoveChild drops it when it empties), so
            // walking them synthesises the same ancestors as walking every record — at folder
            // count instead of asset count.
            foreach (var parentFolder in _fileGuidsByParent.Keys)
            {
                EnsureFolderAndAncestors(replacement, parentFolder);
            }

            var removed = new List<string>();
            foreach (var existing in _foldersByPath.Keys)
            {
                if (!replacement.ContainsKey(existing))
                {
                    removed.Add(existing);
                }
            }

            var delta = new AssetCatalogDelta(_recordVersion);
            removed.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            for (var i = 0; i < removed.Count; i++)
            {
                RemoveFolderOnly(removed[i], delta);
            }

            foreach (var folder in replacement.Values)
            {
                UpsertFolderCore(folder, delta);
            }

            if (!delta.IsEmpty)
            {
                _version++;
            }
        }

        private void ApplyMove(AssetCatalogMove move, AssetCatalogDelta delta)
        {
            var oldPath = move.OldPath;
            var newPath = move.NewPath;
            if (string.IsNullOrEmpty(oldPath)
                || string.IsNullOrEmpty(newPath)
                || string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                return;
            }

            RemovePendingPathOrSubtree(oldPath, delta);
            // The OLD path: an observation describes where the folder was, and
            // the destination comes back through a fresh observation rather than
            // being carried over as an unverified one.
            RemoveObservedFolderOrSubtree(oldPath);

            if (_foldersByPath.ContainsKey(oldPath))
            {
                MoveFolderSubtree(oldPath, newPath, delta);
                return;
            }

            if (_guidByPath.TryGetValue(oldPath, out var guid)
                && _records.TryGetValue(guid, out var record))
            {
                RemoveRecordCore(record, delta);
            }
        }

        private void MoveFolderSubtree(
            string oldRoot,
            string newRoot,
            AssetCatalogDelta delta)
        {
            var folderPaths = new List<string>();
            var records = new List<AssetRecord>();
            var pending = new Queue<string>();
            pending.Enqueue(oldRoot);
            while (pending.Count > 0)
            {
                var folderPath = pending.Dequeue();
                if (!_foldersByPath.ContainsKey(folderPath))
                {
                    continue;
                }

                folderPaths.Add(folderPath);
                if (_fileGuidsByParent.TryGetValue(folderPath, out var fileGuids))
                {
                    foreach (var guid in fileGuids)
                    {
                        if (_records.TryGetValue(guid, out var record))
                        {
                            records.Add(record);
                        }
                    }
                }

                if (_folderPathsByParent.TryGetValue(folderPath, out var childFolders))
                {
                    foreach (var childFolder in childFolders)
                    {
                        pending.Enqueue(childFolder);
                    }
                }
            }

            for (var i = 0; i < records.Count; i++)
            {
                RemoveRecordCore(records[i], delta);
            }

            folderPaths.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            var movedFolders = new List<AssetFolderRecord>(folderPaths.Count);
            for (var i = 0; i < folderPaths.Count; i++)
            {
                var path = folderPaths[i];
                var folder = _foldersByPath[path];
                movedFolders.Add(new AssetFolderRecord(
                    AssetPath.From(RewritePath(path, oldRoot, newRoot)),
                    folder.Guid,
                    folder.UnityLabels));
                RemoveFolderOnly(path, delta);
            }

            movedFolders.Sort(static (left, right) =>
                left.FolderPath.Value.Length.CompareTo(right.FolderPath.Value.Length));
            for (var i = 0; i < movedFolders.Count; i++)
            {
                UpsertFolderCore(movedFolders[i], delta);
            }

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var movedPath = RewritePath(record.AssetPath, oldRoot, newRoot);
                UpsertRecordCore(AssetRecord.WithAssetPath(record, movedPath), delta);
            }
        }

        private void RemovePathOrSubtree(string path, AssetCatalogDelta delta)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            RemovePendingPathOrSubtree(path, delta);
            RemoveObservedFolderOrSubtree(path);

            if (_guidByPath.TryGetValue(path, out var guid)
                && _records.TryGetValue(guid, out var record))
            {
                RemoveRecordCore(record, delta);
                return;
            }

            if (_foldersByPath.ContainsKey(path))
            {
                RemoveFolderSubtree(path, delta);
            }
        }

        private void RemoveFolderSubtree(string rootPath, AssetCatalogDelta delta)
        {
            var folderPaths = new List<string>();
            var pending = new Queue<string>();
            pending.Enqueue(rootPath);
            while (pending.Count > 0)
            {
                var folderPath = pending.Dequeue();
                if (!_foldersByPath.ContainsKey(folderPath))
                {
                    continue;
                }

                folderPaths.Add(folderPath);
                if (_fileGuidsByParent.TryGetValue(folderPath, out var fileGuids))
                {
                    var snapshot = new List<string>(fileGuids);
                    for (var i = 0; i < snapshot.Count; i++)
                    {
                        if (_records.TryGetValue(snapshot[i], out var record))
                        {
                            // The bucket key IS every one of these records' parent.
                            RemoveRecordCore(record, delta, folderPath);
                        }
                    }
                }

                if (_folderPathsByParent.TryGetValue(folderPath, out var childFolders))
                {
                    foreach (var childFolder in childFolders)
                    {
                        pending.Enqueue(childFolder);
                    }
                }
            }

            folderPaths.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            for (var i = 0; i < folderPaths.Count; i++)
            {
                RemoveFolderOnly(folderPaths[i], delta);
            }
        }

        private bool UpsertRecordCore(AssetRecord record, AssetCatalogDelta delta)
        {
            if (record == null)
            {
                return false;
            }

            // One parent per record, computed here and passed down. GetParentFolder
            // is a Substring, and this routine used to pay for it up to three times
            // for the same path: once inside RemovePendingCore, once inside the
            // RemoveRecordCore that evicts a record displaced from this very path,
            // and once for its own bucket bookkeeping. Hoisting it above the no-op
            // short-circuit costs that (uncommon) early return one Substring it did
            // not pay before, and saves one or two on every path that actually
            // mutates - including the cold-browse case, where every incoming record
            // lands on a pending entry.
            // The evicted SAME-GUID record is not covered: a move changes the path,
            // so its parent is a different one and must be derived from its own.
            var parent = ZepPathHelpers.GetParentFolder(record.AssetPath);
            RemovePendingCore(record.AssetPath, delta, parent);

            var hasExistingGuid = _records.TryGetValue(record.Guid, out var existingRecord);
            string displacedGuid = null;
            var hasDisplacedPathRecord = !string.IsNullOrEmpty(record.AssetPath)
                && _guidByPath.TryGetValue(record.AssetPath, out displacedGuid)
                && !string.Equals(displacedGuid, record.Guid, StringComparison.Ordinal);
            if (hasExistingGuid && existingRecord.Equals(record) && !hasDisplacedPathRecord)
            {
                return false;
            }

            if (hasDisplacedPathRecord
                && _records.TryGetValue(displacedGuid, out var displacedRecord))
            {
                // Displaced BY PATH, so it hangs off the same parent bucket.
                RemoveRecordCore(displacedRecord, delta, parent);
            }

            if (hasExistingGuid)
            {
                RemoveRecordCore(existingRecord, delta);
            }

            _records[record.Guid] = record;
            _guidByPath[record.AssetPath] = record.Guid;
            AddChild(_fileGuidsByParent, parent, record.Guid);
            IncrementType(record.TypeName);
            AddTypeMember(record.TypeName, record.Guid);
            if (!HasExtensionDeterminedType(record))
            {
                _typeAmbiguousGuids.Add(record.Guid);
            }

            delta.UpsertedRecords.Add(record);
            delta.AffectedParentFolders.Add(parent);
            return true;
        }

        // `knownParentFolder`: the caller's already-computed parent of
        // record.AssetPath, or null to derive it here. Callers that walk a parent
        // bucket (or that just evicted a record displaced from the path they are
        // about to write) already hold the string; deriving it again is a Substring
        // per record, which is the whole cost of a folder-subtree delete.
        private void RemoveRecordCore(
            AssetRecord record,
            AssetCatalogDelta delta,
            string knownParentFolder = null)
        {
            if (record == null || !_records.Remove(record.Guid))
            {
                return;
            }

            if (_guidByPath.TryGetValue(record.AssetPath, out var mappedGuid)
                && string.Equals(mappedGuid, record.Guid, StringComparison.Ordinal))
            {
                _guidByPath.Remove(record.AssetPath);
            }

            var parent = knownParentFolder
                ?? ZepPathHelpers.GetParentFolder(record.AssetPath);
            RemoveChild(_fileGuidsByParent, parent, record.Guid);
            DecrementType(record.TypeName);
            RemoveTypeMember(record.TypeName, record.Guid);
            _typeAmbiguousGuids.Remove(record.Guid);
            delta.RemovedRecords.Add(record);
            delta.AffectedParentFolders.Add(parent);
        }

        private bool UpsertFolderCore(AssetFolderRecord folder, AssetCatalogDelta delta)
        {
            if (folder == null || folder.FolderPath.IsEmpty)
            {
                return false;
            }

            var path = folder.FolderPath.Value;
            RemovePendingCore(path, delta);
            if (_foldersByPath.TryGetValue(path, out var existing))
            {
                if (FolderRecordsEqual(existing, folder))
                {
                    return false;
                }

                _foldersByPath[path] = folder;
                delta.RemovedFolders.Add(existing);
                delta.UpsertedFolders.Add(folder);
                delta.AffectedParentFolders.Add(
                    ZepPathHelpers.GetParentFolder(path));
                return true;
            }

            _foldersByPath[path] = folder;
            if (ZepPathHelpers.GetFolderDepth(path) > 0)
            {
                _filterableFolderCount++;
            }
            var parent = ZepPathHelpers.GetParentFolder(path);
            if (!string.Equals(parent, path, StringComparison.OrdinalIgnoreCase))
            {
                AddChild(_folderPathsByParent, parent, path);
                delta.AffectedParentFolders.Add(parent);
            }

            delta.UpsertedFolders.Add(folder);
            return true;
        }

        private void RemoveFolderOnly(string path, AssetCatalogDelta delta)
        {
            if (string.IsNullOrEmpty(path)
                || !_foldersByPath.TryGetValue(path, out var folder))
            {
                return;
            }

            _foldersByPath.Remove(path);
            if (ZepPathHelpers.GetFolderDepth(path) > 0)
            {
                _filterableFolderCount--;
            }
            var parent = ZepPathHelpers.GetParentFolder(path);
            if (!string.Equals(parent, path, StringComparison.OrdinalIgnoreCase))
            {
                RemoveChild(_folderPathsByParent, parent, path);
                delta.AffectedParentFolders.Add(parent);
            }

            _fileGuidsByParent.Remove(path);
            _folderPathsByParent.Remove(path);
            _directChildCounts.Remove(path);
            _nonEmptyFolderPaths.Remove(path);
            delta.RemovedFolders.Add(folder);
        }

        private void RemovePendingPathOrSubtree(
            string rootPath,
            AssetCatalogDelta delta)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                return;
            }

            // Called for every removed and every moved path, so the common case -
            // nothing pending at all - must cost nothing.
            if (_pendingByPath.Count == 0)
            {
                return;
            }

            _pendingRemovalKeyProbes++;
            RemovePendingCore(rootPath, delta);

            // Walk parent buckets instead of scanning every pending key.
            // Canonical and pending folder children provide topology edges;
            // directly observed folders may have no such ancestor chain yet.
            var queue = new Queue<string>();
            queue.Enqueue(rootPath);
            // A restored pane can observe a deep folder before its ancestors
            // have imported. Its pending bucket has no folder edge leading to
            // it, and Replace preserves that bucket even after retiring the
            // observation. Seed these parent buckets as well as walking the
            // topology; only affected buckets' pending keys are enumerated.
            foreach (var parent in _pendingPathsByParent.Keys)
            {
                if (ZepPathHelpers.IsWithinFolderScope(parent, rootPath))
                {
                    queue.Enqueue(parent);
                }
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.Count > 0)
            {
                var folderPath = queue.Dequeue();
                if (!visited.Add(folderPath))
                {
                    continue;
                }

                if (_pendingPathsByParent.TryGetValue(folderPath, out var pendingChildren))
                {
                    var snapshot = new List<string>(pendingChildren);
                    for (var i = 0; i < snapshot.Count; i++)
                    {
                        _pendingRemovalKeyProbes++;
                        queue.Enqueue(snapshot[i]);
                        // Same: these are the children keyed under folderPath.
                        RemovePendingCore(snapshot[i], delta, folderPath);
                    }
                }

                if (_folderPathsByParent.TryGetValue(folderPath, out var childFolders))
                {
                    foreach (var childFolder in childFolders)
                    {
                        queue.Enqueue(childFolder);
                    }
                }
            }
        }

        // Retires a directly observed folder and every observation beneath it.
        // Deliberately NOT folded into RemovePendingPathOrSubtree above, which
        // returns early when nothing is pending: an observed EMPTY folder leaves
        // exactly no pending entry behind, so the entry would survive its own
        // deletion. A linear pass is right here - the set holds only the folders
        // the panel browsed directly, tens of entries at most - and the count
        // check keeps the common case (nothing observed) free, the same way the
        // pending removal keeps its own.
        private void RemoveObservedFolderOrSubtree(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || _observedFolders.Count == 0)
            {
                return;
            }

            _observedFolders.Remove(rootPath);
            var descendantPrefix = rootPath + "/";
            _observedFolders.RemoveWhere(observed => observed.StartsWith(
                descendantPrefix,
                StringComparison.OrdinalIgnoreCase));
        }

        // `knownParentFolder`: as RemoveRecordCore's. Callers that do NOT already
        // hold it pass null deliberately - the derivation sits after the "nothing
        // pending here" early return, so the common case still costs nothing.
        private void RemovePendingCore(
            string path,
            AssetCatalogDelta delta,
            string knownParentFolder = null)
        {
            if (string.IsNullOrEmpty(path)
                || !_pendingByPath.TryGetValue(path, out var pending))
            {
                return;
            }

            _pendingByPath.Remove(path);
            var parent = knownParentFolder
                ?? ZepPathHelpers.GetParentFolder(path);
            RemoveChild(_pendingPathsByParent, parent, path);
            delta.RemovedPendingEntries.Add(pending);
            delta.AffectedParentFolders.Add(parent);
        }

        private void AddChild(
            Dictionary<string, HashSet<string>> childrenByParent,
            string parent,
            string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child))
            {
                return;
            }

            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                children = new HashSet<string>(childrenByParent == _fileGuidsByParent
                    ? StringComparer.Ordinal
                    : StringComparer.OrdinalIgnoreCase);
                childrenByParent[parent] = children;
            }

            if (children.Add(child))
            {
                _directChildCounts.TryGetValue(parent, out var count);
                _directChildCounts[parent] = count + 1;
                _nonEmptyFolderPaths.Add(parent);
            }
        }

        private void RemoveChild(
            Dictionary<string, HashSet<string>> childrenByParent,
            string parent,
            string child)
        {
            if (string.IsNullOrEmpty(parent)
                || string.IsNullOrEmpty(child)
                || !childrenByParent.TryGetValue(parent, out var children)
                || !children.Remove(child))
            {
                return;
            }

            if (children.Count == 0)
            {
                childrenByParent.Remove(parent);
            }

            if (_directChildCounts.TryGetValue(parent, out var count))
            {
                count--;
                if (count <= 0)
                {
                    _directChildCounts.Remove(parent);
                    _nonEmptyFolderPaths.Remove(parent);
                }
                else
                {
                    _directChildCounts[parent] = count;
                }
            }
        }

        private void IncrementType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return;
            }

            _typeCounts.TryGetValue(typeName, out var count);
            _typeCounts[typeName] = count + 1;
        }

        private void DecrementType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)
                || !_typeCounts.TryGetValue(typeName, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _typeCounts.Remove(typeName);
            }
            else
            {
                _typeCounts[typeName] = count - 1;
            }
        }

        /// <summary>
        /// The records a script reload can retype, so a caller need not look at the
        /// rest of the catalog. Two groups: every record whose type the extension map
        /// does not determine on its own (see <see cref="HasExtensionDeterminedType"/>),
        /// plus every record in <paramref name="additionalTypeNames"/> — the buckets a
        /// caller must ask for by name because a ScriptedImporter has claimed an
        /// extension the map already maps, which leaves those records looking
        /// extension-determined from here. This is a candidate set, not a decision: the
        /// caller still applies its own predicate to each one.
        /// </summary>
        internal void CollectTypeReconciliationCandidates(
            IReadOnlyCollection<string> additionalTypeNames,
            List<AssetRecord> destination)
        {
            if (destination == null)
            {
                return;
            }

            foreach (var guid in _typeAmbiguousGuids)
            {
                if (_records.TryGetValue(guid, out var record))
                {
                    destination.Add(record);
                }
            }

            if (additionalTypeNames == null || additionalTypeNames.Count == 0)
            {
                return;
            }

            foreach (var typeName in additionalTypeNames)
            {
                if (string.IsNullOrEmpty(typeName)
                    || !_guidsByType.TryGetValue(typeName, out var guids))
                {
                    continue;
                }

                foreach (var guid in guids)
                {
                    if (!_typeAmbiguousGuids.Contains(guid)
                        && _records.TryGetValue(guid, out var record))
                    {
                        destination.Add(record);
                    }
                }
            }
        }

        // True when the record's type follows from its extension alone and nothing
        // has overridden it: the extension maps to a concrete type, the record still
        // carries that type, and no importer supplied a base-type chain for it. Such
        // a record cannot change type because scripts recompiled.
        private static bool HasExtensionDeterminedType(AssetRecord record)
        {
            if (record.BaseTypeNames.Count > 0)
            {
                return false;
            }

            var inferred = AssetExtensionTypeMap.GetTypeNameForPath(record.AssetPath);
            return !string.Equals(
                    inferred,
                    AssetExtensionTypeMap.ScriptableObjectTypeName,
                    StringComparison.Ordinal)
                && !string.Equals(
                    inferred,
                    AssetExtensionTypeMap.UnknownTypeName,
                    StringComparison.Ordinal)
                && string.Equals(inferred, record.TypeName, StringComparison.Ordinal);
        }

        private void AddTypeMember(string typeName, string guid)
        {
            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrEmpty(guid))
            {
                return;
            }

            if (!_guidsByType.TryGetValue(typeName, out var guids))
            {
                guids = new HashSet<string>(StringComparer.Ordinal);
                _guidsByType[typeName] = guids;
            }

            guids.Add(guid);
        }

        private void RemoveTypeMember(string typeName, string guid)
        {
            if (string.IsNullOrWhiteSpace(typeName)
                || string.IsNullOrEmpty(guid)
                || !_guidsByType.TryGetValue(typeName, out var guids))
            {
                return;
            }

            guids.Remove(guid);
            if (guids.Count == 0)
            {
                _guidsByType.Remove(typeName);
            }
        }

        private static void EnsureFolderAndAncestors(
            Dictionary<string, AssetFolderRecord> folders,
            string folderPath)
        {
            while (!string.IsNullOrEmpty(folderPath))
            {
                if (!folders.ContainsKey(folderPath))
                {
                    folders[folderPath] = new AssetFolderRecord(AssetPath.From(folderPath));
                }

                if (string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(folderPath, "Packages", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var parent = ZepPathHelpers.GetParentFolder(folderPath);
                if (string.Equals(parent, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                folderPath = parent;
            }
        }

        private static bool FolderRecordsEqual(
            AssetFolderRecord left,
            AssetFolderRecord right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null
                || !string.Equals(
                    left.FolderPath.Value,
                    right.FolderPath.Value,
                    StringComparison.Ordinal)
                || !string.Equals(left.Guid, right.Guid, StringComparison.Ordinal)
                || left.UnityLabels.Count != right.UnityLabels.Count)
            {
                return false;
            }

            for (var i = 0; i < left.UnityLabels.Count; i++)
            {
                if (!string.Equals(
                    left.UnityLabels[i],
                    right.UnityLabels[i],
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string RewritePath(string path, string oldRoot, string newRoot)
        {
            return newRoot + path.Substring(oldRoot.Length);
        }
    }
}
