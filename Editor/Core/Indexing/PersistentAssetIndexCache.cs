using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Domain;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// Serialises the asset index to <c>Library/com.van-unity.zep/index.v1.bin</c>
    /// so that editor restarts and domain reloads can skip the full asset rescan. The
    /// cache stores a structural signature and Unity's artifact-dependency revision
    /// alongside the records. A structural mismatch normally makes the snapshot
    /// unusable. When Unity journaled every concrete changed path before a domain
    /// reload, the snapshot remains a safe seed after those paths are replayed. An
    /// artifact mismatch can then reconcile in the background without a cold overlay.
    ///
    /// File format (version 11):
    ///   magic:        ASCII "BCPI"                             (4 bytes)
    ///   version:      byte 11                                  (1 byte)
    ///   snapshotId:   16-byte cache snapshot identity         (16 bytes)
    ///   signature:    int64 structural signature               (8 bytes)
    ///   tagTicks:     assignment then definition UTC ticks    (16 bytes)
    ///   artifactRev:  uint32 Unity artifact revision           (4 bytes)
    ///   count:        int32 record count                       (4 bytes)
    ///   records[...]: for each, 5 length-prefixed UTF-8 strings
    ///                   guid, assetPath, name, typeName, tagSearchText
    ///                 followed by an int32 Unity-label count and that many
    ///                 length-prefixed UTF-8 label strings
    ///                 followed by an int32 base-type count and that many names
    ///   folderCount:  int32 folder record count                (4 bytes)
    ///   folders[...]: path + guid strings, followed by an int32 Unity-label
    ///                 count and that many label strings
    ///
    /// <para>The folder list was added in v2 so cache-hit panel opens can skip the
    /// recursive `Directory.GetDirectories` walk that <see cref="IAssetRecordSource.GetFolderRecords"/>
    /// would otherwise do — the signature already guarantees the folder set on disk
    /// hasn't changed since the cache was written. v1 caches are treated as invalid
    /// and rebuilt.</para>
    ///
    /// <para>Atomic writes: we always write to a temp path first then rename over the
    /// real file so a crash mid-write cannot leave a half-written cache.</para>
    /// </summary>
    public static class PersistentAssetIndexCache
    {

        /// <summary>
        /// Optional test hook for the Unity-label snapshot the walk sources consume.
        /// Production queries Unity's native label index and resolves labels only
        /// for the GUIDs it returns.
        /// </summary>
        internal static Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> UnityLabelSnapshotOverride;

        /// <summary>
        /// Optional test hook for the authoritative GUID-to-concrete-type snapshot.
        /// </summary>
        internal static Func<IReadOnlyDictionary<string, AssetTypeMetadata>> AuthoritativeTypeSnapshotOverride;

        /// <summary>
        /// Optional test hook for Unity's project-wide artifact dependency revision.
        /// Production reads Unity's <c>AssetDatabase.GlobalArtifactDependencyVersion</c>.
        /// </summary>
        internal static Func<uint> ArtifactDependencyVersionOverride;

        /// <summary>
        /// Optional test hook for the final atomic commit of one journal batch file.
        /// The production path moves the fully written temporary file to its name.
        /// </summary>
        internal static Action<string, string> PendingChangesCommitOverride;

        /// <summary>
        /// Optional test hook for retiring one batch file a cache save covers.
        /// </summary>
        internal static Action<string> PendingChangesRetireOverride;

        /// <summary>
        /// Optional test hook invoked after cache rows have been deserialized but
        /// before the snapshot's identity is confirmed against the file on disk.
        /// </summary>
        internal static Action CacheDeserializedOverride;

        /// <summary>
        /// Optional test hook for the authoritative-fallback marker commit.
        /// </summary>
        internal static Action<string, string> AuthoritativeFallbackCommitOverride;

        /// <summary>
        /// Optional test hook for the dirty-marker commit.
        /// </summary>
        internal static Action<string, string> DirtyMarkerCommitOverride;

        /// <summary>
        /// Optional test hook invoked on entry to every pending-change batch merge.
        /// It exists so a test can prove that a batch which is already past the
        /// journal's entry bound never reaches the quadratic merge at all.
        /// </summary>
        internal static Action PendingChangeBatchMergeObserverForTests;

        /// <summary>
        /// Optional test hook invoked once per staged snapshot serialization, on
        /// whichever thread performs it. It exists so a test can prove the record
        /// loop — the whole cost of a save — does not run on the main thread.
        /// </summary>
        internal static Action SnapshotSerializationObserverForTests;

        // The synchronizer hydrates on the caller's thread, not on the background
        // worker, while a test has replaced one of the snapshots the worker would
        // capture: the deterministic path is what a test driving the cache through
        // overrides is asking for.
        public static bool HasTestOverrides =>
            UnityLabelSnapshotOverride != null
            || ArtifactDependencyVersionOverride != null;

        internal static bool HasAuthoritativeTypeSnapshotOverride =>
            AuthoritativeTypeSnapshotOverride != null;

        // ASCII "BCPI". Held as raw bytes so reads and writes can avoid the per-call
        // char[] allocation that BinaryWriter.Write(char[]) / BinaryReader.ReadChars
        // would force.
        private static readonly byte[] MagicBytes = { (byte)'B', (byte)'C', (byte)'P', (byte)'I' };
        // v2 adds the folder-path list after records. v1 caches are unreadable here
        // and trigger a full rebuild on next launch.
        // v3: the index now includes registry/git/file: package records (previously only
        // Assets + packages physically under <project>/Packages/); v2 caches rebuild once.
        // v4: records persist Unity labels.
        // v5: folders persist GUIDs/Unity labels, and old generic `.asset` type rows rebuild.
        // v6: records persist real CLR base types, and Unity's artifact revision validates
        // GUID/type/label changes without an O(number-of-.asset) cache-hit scan.
        // v7: stores that revision separately from structural state so script recompiles
        // can reuse the saved snapshot while reconciliation runs without a cold-scan overlay.
        // v8: adds a unique snapshot identity so a load cannot adopt bytes from an
        // opened file that was atomically replaced at the cache path during hydration.
        // v9: inferred Unity types persist their CLR ancestry, so older snapshots rebuild.
        // v10: records persist the independently captured tag-state timestamps.
        // v11: the walks stopped descending into directories Unity imports as one asset,
        // so a v10 snapshot's bundle-internal records — which have no GUID and can never
        // be rebuilt — must not survive into this build. One cold rebuild, once.
        // v12: the structural filesystem signature is gone. A snapshot is loaded
        // whenever it is readable; what happened after it was written is the journal's
        // and the markers' business, decided after the load. The header instead
        // records the journal generation the snapshot covers, which is what a save
        // retires and what keeps generations monotonic across retirements.
        private const byte Version = 12;
        private const long MissingTagStateTimestamp = -1L;
        private const long UnavailableTagStateTimestamp = -2L;
        private const string TagAssignmentsPath =
            "ProjectSettings/Zep.TagAssignments.json";
        private const string TagDefinitionsPath =
            "ProjectSettings/ZepTagSettings.asset";
        private const int CacheSnapshotIdByteCount = 16;
        private const int MaxRecordCount = 10_000_000;
        private const int MaxFolderCount = 1_000_000;
        private const int MaxLabelCountPerRecord = 1_000_000;
        private const int MaxBaseTypeCountPerRecord = 1_024;
        public const int MaxPendingChangeBatchCount = 64;
        public const int MaxPendingChangeEntryCount = 1_024;
        public const int MaxPendingChangePathBytes = 128 * 1_024;
        private const string BatchFileExtension = ".json";

        internal static string CacheDirectory => Path.Combine("Library", "com.van-unity.zep");

        private static readonly object DirectoryCreationGate = new object();
        private static bool _writesStopped;

        public static void StopWritesForPackageRemoval()
        {
            lock (DirectoryCreationGate)
            {
                _writesStopped = true;
            }
        }

        // Background verification can finish after the host is disposed. Once
        // cleanup starts, no late marker or save may recreate the cache folder.
        private static void CreateCacheDirectory(string path)
        {
            lock (DirectoryCreationGate)
            {
                if (_writesStopped)
                {
                    throw new IOException("Zep cache writes stopped for package removal.");
                }
                Directory.CreateDirectory(path);
            }
        }

        internal static string CacheFilePath => Path.Combine(CacheDirectory, "index.v1.bin");

        internal static string DirtyMarkerPath => Path.Combine(
            CacheDirectory,
            "index.v1.dirty");

        // One file per journaled batch, named by its generation. An append creates
        // a new file and touches no other, so a Unity callback can never contend
        // with a save that is retiring the files it covers: the save deletes the
        // generations at or below the one its snapshot includes, and a batch that
        // arrived meanwhile is simply a higher number that survives.
        internal static string PendingChangesDirectory => Path.Combine(
            CacheDirectory,
            "pending-changes");

        // Exact replay was abandoned — the journal overflowed or could not be
        // written — so the next load verifies the snapshot against a walk. Cleared
        // by the save that walk ends in.
        internal static string PendingChangesAuthoritativeFallbackPath => Path.Combine(
            CacheDirectory,
            "pending-changes.v1.authoritative");

        // The single-file journal and its write marker of earlier builds. Nothing
        // reads them any more; they are deleted where they are met so a leftover
        // cannot be mistaken for state.
        private static string LegacyPendingChangesJournalPath => Path.Combine(
            CacheDirectory,
            "pending-changes.v1.json");

        private static string LegacyPendingChangesWriteMarkerPath => Path.Combine(
            CacheDirectory,
            "pending-changes.v1.writing");

        // Every dirty-marker write stages its own temp file. A worker-side MarkDirty and a
        // main-thread one used to stage through the same path, so one writer could commit
        // the other's token and then report failure for a marker that was in fact written.
        private static int _dirtyMarkerTempSequence;
        // Same for the snapshot: two synchronizer instances in one process stage
        // their snapshots at the same time, and one shared temp name would let each
        // corrupt the other's bytes before its commit.
        private static int _snapshotTempSequence;

        public const long UntrustedJournalGeneration = long.MinValue;

        internal readonly struct RecoveryState
        {
            public RecoveryState(
                long journalGeneration,
                string authoritativeFallbackToken,
                string dirtyToken,
                long assignmentTimestamp,
                long definitionTimestamp,
                bool requiresVerification = false,
                bool hasMalformedJournalEvidence = false)
            {
                JournalGeneration = journalGeneration;
                AuthoritativeFallbackToken = authoritativeFallbackToken;
                DirtyToken = dirtyToken;
                AssignmentTimestamp = assignmentTimestamp;
                DefinitionTimestamp = definitionTimestamp;
                RequiresVerification = requiresVerification;
                HasMalformedJournalEvidence = hasMalformedJournalEvidence;
            }

            public long JournalGeneration { get; }

            public string AuthoritativeFallbackToken { get; }

            public string DirtyToken { get; }

            public long AssignmentTimestamp { get; }

            public long DefinitionTimestamp { get; }

            // The snapshot plus the journal are not a complete account of the
            // project: a marker, an unreadable journal, a dirty token the journal
            // does not cover, or tag files whose times cannot be read. The loaded
            // snapshot is still published; a background walk then reconciles it.
            public bool RequiresVerification { get; }

            public bool HasMalformedJournalEvidence { get; }
        }

        // Plain data: a batch file is read and written through JsonText, not
        // Unity's serializer, so it needs no [Serializable] shape. One file holds
        // one document of this shape; the in-memory journal concatenates them.
        private sealed class PendingChangeJournal
        {
            public long Generation;
            public PendingChangeBatch Changes = new PendingChangeBatch();

            // Chronological batches are required when two mutations reuse the same
            // path.
            public List<PendingChangeBatch> Batches = new List<PendingChangeBatch>();
        }

        private sealed class PendingChangeBatch
        {
            public string[] Imported = Array.Empty<string>();
            public string[] Deleted = Array.Empty<string>();
            public string[] Moved = Array.Empty<string>();
            public string[] MovedFromPaths = Array.Empty<string>();
        }

        /// <summary>
        /// Loads the snapshot on disk. False only when there is nothing to load: the
        /// file is missing, carries another magic or format version, or is damaged.
        /// Everything that used to refuse a load — a moved journal, a marker, a tag
        /// edit, a changed artifact revision — is reported through the out values
        /// and decided by the caller AFTER the load, never instead of it.
        /// </summary>
        internal static bool TryLoad(out List<AssetRecord> records, out List<AssetFolderRecord> folderRecords)
        {
            return TryLoad(
                CaptureArtifactDependencyVersion(),
                CaptureRecoveryState(),
                out records,
                out folderRecords,
                out _,
                out _,
                out _);
        }

        internal static bool TryLoad(
            uint expectedArtifactRevision,
            RecoveryState recoveryState,
            out List<AssetRecord> records,
            out List<AssetFolderRecord> folderRecords,
            out bool artifactRevisionChanged,
            out bool tagStateChanged,
            out string cacheSnapshotId)
        {
            records = null;
            folderRecords = null;
            artifactRevisionChanged = false;
            tagStateChanged = false;
            cacheSnapshotId = null;
            if (!File.Exists(CacheFilePath))
            {
                return false;
            }

            try
            {
                var __sw_deser = System.Diagnostics.Stopwatch.StartNew();
                // The snapshot is read field-by-field through a BinaryReader: at 500k
                // records that is millions of small reads, and the framework default
                // is a 4 KB buffer.
                using (var fs = new FileStream(
                    CacheFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1 << 20))
                using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    if (br.ReadByte() != MagicBytes[0] ||
                        br.ReadByte() != MagicBytes[1] ||
                        br.ReadByte() != MagicBytes[2] ||
                        br.ReadByte() != MagicBytes[3])
                    {
                        return DiscardCorruptCache("bad magic");
                    }

                    var version = br.ReadByte();
                    if (version != Version)
                    {
                        return false;
                    }

                    var snapshotIdBytes = br.ReadBytes(CacheSnapshotIdByteCount);
                    if (snapshotIdBytes.Length != CacheSnapshotIdByteCount)
                    {
                        return DiscardCorruptCache("truncated snapshot identity");
                    }
                    var loadedSnapshotId = Convert.ToBase64String(snapshotIdBytes);

                    var storedAssignmentTimestamp = br.ReadInt64();
                    var storedDefinitionTimestamp = br.ReadInt64();
                    var storedArtifactRevision = br.ReadUInt32();
                    // The covered journal generation: a floor for appends and a
                    // guard for commits, not a load decision.
                    br.ReadInt64();

                    var count = br.ReadInt32();
                    if (count < 0 || count > MaxRecordCount)
                    {
                        return DiscardCorruptCache($"record count {count} out of range");
                    }

                    var result = new List<AssetRecord>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var guid = br.ReadString();
                        var path = br.ReadString();
                        var name = br.ReadString();
                        var typeName = br.ReadString();
                        var tagSearchText = br.ReadString();
                        var labelCount = br.ReadInt32();

                        if (string.IsNullOrWhiteSpace(guid)
                            || labelCount < 0
                            || labelCount > MaxLabelCountPerRecord)
                        {
                            // Corrupt record; bail out and force a rebuild.
                            return DiscardCorruptCache($"record {i} has an empty guid or a label count out of range");
                        }

                        var unityLabels = new string[labelCount];
                        for (var labelIndex = 0; labelIndex < labelCount; labelIndex++)
                        {
                            unityLabels[labelIndex] = br.ReadString();
                            if (string.IsNullOrWhiteSpace(unityLabels[labelIndex]))
                            {
                                return DiscardCorruptCache($"record {i} has an empty label");
                            }
                        }

                        var baseTypeCount = br.ReadInt32();
                        if (baseTypeCount < 0 || baseTypeCount > MaxBaseTypeCountPerRecord)
                        {
                            return DiscardCorruptCache($"record {i} base-type count {baseTypeCount} out of range");
                        }

                        var baseTypeNames = new string[baseTypeCount];
                        for (var baseTypeIndex = 0; baseTypeIndex < baseTypeCount; baseTypeIndex++)
                        {
                            baseTypeNames[baseTypeIndex] = br.ReadString();
                            if (string.IsNullOrWhiteSpace(baseTypeNames[baseTypeIndex]))
                            {
                                return DiscardCorruptCache($"record {i} has an empty base-type name");
                            }
                        }

                        result.Add(new AssetRecord(
                            guid,
                            path,
                            name,
                            typeName,
                            tagSearchText,
                            unityLabels,
                            baseTypeNames));
                    }

                    var folderCount = br.ReadInt32();
                    if (folderCount < 0 || folderCount > MaxFolderCount)
                    {
                        return DiscardCorruptCache($"folder count {folderCount} out of range");
                    }

                    var folders = new List<AssetFolderRecord>(folderCount);
                    for (var i = 0; i < folderCount; i++)
                    {
                        var folderPath = br.ReadString();
                        var folderGuid = br.ReadString();
                        var folderLabelCount = br.ReadInt32();
                        // Normalize first: whitespace and slash-only paths survive an
                        // IsNullOrEmpty test but normalize to empty, and AssetFolderRecord
                        // throws on those. Force a rebuild rather than either exposing a
                        // bogus entry to the projection or letting the throw escape into
                        // the deferred hydration callback.
                        var normalizedFolderPath = AssetPath.From(folderPath);
                        if (normalizedFolderPath.IsEmpty)
                        {
                            return DiscardCorruptCache($"folder record {i} has an empty path");
                        }

                        if (folderLabelCount < 0 || folderLabelCount > MaxLabelCountPerRecord)
                        {
                            return DiscardCorruptCache($"folder record {i} label count {folderLabelCount} out of range");
                        }

                        var folderLabels = new string[folderLabelCount];
                        for (var labelIndex = 0; labelIndex < folderLabelCount; labelIndex++)
                        {
                            folderLabels[labelIndex] = br.ReadString();
                            if (string.IsNullOrWhiteSpace(folderLabels[labelIndex]))
                            {
                                return DiscardCorruptCache($"folder record {i} has an empty label");
                            }
                        }

                        folders.Add(new AssetFolderRecord(
                            normalizedFolderPath,
                            folderGuid,
                            folderLabels));
                    }

                    CacheDeserializedOverride?.Invoke();
                    // The one refusal that is not about the bytes read: the file at
                    // the cache path is no longer the one these rows came from (a
                    // save replaced it mid-read), so the rows are a torn account.
                    if (!CacheSnapshotMatchesCurrentPath(loadedSnapshotId))
                    {
                        return false;
                    }

                    records = result;
                    folderRecords = folders;
                    cacheSnapshotId = loadedSnapshotId;
                    artifactRevisionChanged =
                        storedArtifactRevision != expectedArtifactRevision;
                    tagStateChanged =
                        storedAssignmentTimestamp != recoveryState.AssignmentTimestamp
                        || storedDefinitionTimestamp != recoveryState.DefinitionTimestamp;
                    __sw_deser.Stop();
                    if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf]     PersistentCache.deserialize n={result.Count} folders={folders.Count} ms={__sw_deser.Elapsed.TotalMilliseconds:F2}");
                    return true;
                }
            }
            catch (IOException)
            {
                // Catches EndOfStreamException (subclass of IOException) as well, which
                // shows up if the file was truncated mid-write before our atomic rename
                // landed.
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException ex)
            {
                // Backstop for a corrupt field that only the record constructors can
                // reject. TryLoad's contract is "false on any failure" — a throw here
                // would escape the deferred hydration callback and leave the panel
                // empty until the next domain reload.
                return DiscardCorruptCache($"record rejected ({ex.Message})");
            }
        }

        // One line per discarded cache: a silent discard makes a rebuild-every-launch
        // loop impossible to diagnose. Reserved for damaged content — a missing file,
        // an old version, or a signature mismatch is normal staleness and stays quiet.
        private static bool DiscardCorruptCache(string reason)
        {
            LogWarning(
                $"[Zep] Discarding corrupt index cache at {CacheFilePath} ({reason}); rebuilding from disk.");
            return false;
        }

        // Whether the file at the cache path is still the snapshot the caller
        // loaded. A save from another synchronizer instance can replace it while
        // the loaded rows are being published; the rows are then a torn account.
        internal static bool IsCacheSnapshotCurrent(string cacheSnapshotId)
        {
            return CacheSnapshotMatchesCurrentPath(cacheSnapshotId);
        }

        private static bool CacheSnapshotMatchesCurrentPath(
            string expectedSnapshotId)
        {
            if (string.IsNullOrEmpty(expectedSnapshotId))
            {
                return false;
            }

            try
            {
                using (var fs = new FileStream(
                    CacheFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    if (br.ReadByte() != MagicBytes[0]
                        || br.ReadByte() != MagicBytes[1]
                        || br.ReadByte() != MagicBytes[2]
                        || br.ReadByte() != MagicBytes[3]
                        || br.ReadByte() != Version)
                    {
                        return false;
                    }

                    var currentId = br.ReadBytes(CacheSnapshotIdByteCount);
                    return currentId.Length == CacheSnapshotIdByteCount
                        && string.Equals(
                            Convert.ToBase64String(currentId),
                            expectedSnapshotId,
                            StringComparison.Ordinal);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The journal generation the snapshot on disk covers: 0 when there is no
        // snapshot of this version, untrusted when the file cannot be read. It is
        // the floor of the journal generation. A save retires the batch files it
        // covers; without the floor the next batch would start again at one, below
        // what the snapshot already holds, and the "disk is ahead" guard in the
        // commit would refuse every later save.
        private static long ReadSnapshotCoveredJournalGeneration()
        {
            try
            {
                if (!File.Exists(CacheFilePath))
                {
                    return 0;
                }

                using (var fs = new FileStream(
                    CacheFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    if (br.ReadByte() != MagicBytes[0]
                        || br.ReadByte() != MagicBytes[1]
                        || br.ReadByte() != MagicBytes[2]
                        || br.ReadByte() != MagicBytes[3]
                        || br.ReadByte() != Version)
                    {
                        return 0;
                    }

                    if (br.ReadBytes(CacheSnapshotIdByteCount).Length != CacheSnapshotIdByteCount)
                    {
                        return 0;
                    }

                    br.ReadInt64();
                    br.ReadInt64();
                    br.ReadUInt32();
                    return br.ReadInt64();
                }
            }
            catch (IOException)
            {
                return UntrustedJournalGeneration;
            }
            catch (UnauthorizedAccessException)
            {
                return UntrustedJournalGeneration;
            }
        }

        /// <summary>
        /// Writes the current index contents to disk, replacing any existing cache
        /// atomically. Returns false on IO errors; the caller should treat this as
        /// non-fatal (the cache just won't help on next launch). The snapshot is
        /// recorded as covering the journal generation current at the call, so the
        /// records must include every journaled batch.
        /// </summary>
        internal static bool Save(IReadOnlyCollection<AssetRecord> records, IReadOnlyList<AssetFolderRecord> folderRecords)
        {
            return Save(
                records,
                folderRecords,
                CaptureArtifactDependencyVersion(),
                CaptureRecoveryState());
        }

        internal static bool Save(
            IReadOnlyCollection<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folderRecords,
            uint artifactDependencyVersion)
        {
            return Save(
                records,
                folderRecords,
                artifactDependencyVersion,
                CaptureRecoveryState());
        }

        internal static bool Save(
            IReadOnlyCollection<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folderRecords,
            uint artifactDependencyVersion,
            RecoveryState recoveryState)
        {
            return Save(
                records,
                folderRecords,
                artifactDependencyVersion,
                recoveryState,
                canCommit: null);
        }

        internal static bool Save(
            IReadOnlyCollection<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folderRecords,
            uint artifactDependencyVersion,
            RecoveryState recoveryState,
            Func<bool> canCommit)
        {
            if (!TryBeginSnapshotSave(
                records,
                folderRecords,
                artifactDependencyVersion,
                recoveryState,
                canCommit,
                out var attempt))
            {
                return false;
            }

            try
            {
                WriteStagedSnapshot(attempt);
            }
            catch (IOException)
            {
                AbandonSnapshotSave(attempt);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                AbandonSnapshotSave(attempt);
                return false;
            }

            return TryCommitSnapshotSave(attempt);
        }

        /// <summary>
        /// One in-flight snapshot save: the captured record graph, the recovery
        /// state it covers and its own staged temp file, so
        /// <see cref="TryBeginSnapshotSave"/>, <see cref="WriteStagedSnapshot"/> and
        /// <see cref="TryCommitSnapshotSave"/> can run on different threads and editor
        /// frames without that state living in a static a later save would trample.
        /// The captured record and folder lists are the caller's snapshot of an
        /// immutable record graph, so the serialising thread may read them freely.
        /// </summary>
        internal sealed class SnapshotSaveAttempt
        {
            internal SnapshotSaveAttempt(
                IReadOnlyCollection<AssetRecord> records,
                IReadOnlyList<AssetFolderRecord> folderRecords,
                uint artifactDependencyVersion,
                RecoveryState recoveryState,
                long coveredJournalGeneration,
                bool discardsJournal,
                Func<bool> canCommit,
                string tempPath)
            {
                Records = records;
                FolderRecords = folderRecords;
                ArtifactDependencyVersion = artifactDependencyVersion;
                Recovery = recoveryState;
                CoveredJournalGeneration = coveredJournalGeneration;
                DiscardsJournal = discardsJournal;
                CanCommit = canCommit;
                TempPath = tempPath;
            }

            internal IReadOnlyCollection<AssetRecord> Records { get; }

            internal IReadOnlyList<AssetFolderRecord> FolderRecords { get; }

            public uint ArtifactDependencyVersion { get; }

            internal RecoveryState Recovery { get; }

            // The highest journal generation the records include: written to the
            // header, and the bound of what the commit retires.
            internal long CoveredJournalGeneration { get; }

            // True when the journal could not be read at capture: the records are
            // then a walk's, which saw everything, and the commit discards every
            // batch file rather than retiring by generation.
            internal bool DiscardsJournal { get; }

            internal Func<bool> CanCommit { get; }

            internal string TempPath { get; }
        }

        /// <summary>
        /// Main-thread phase one: validate the arguments and stage the temp path.
        /// The caller must finish the attempt with <see cref="TryCommitSnapshotSave"/>
        /// or <see cref="AbandonSnapshotSave"/>, or its temp file is left behind.
        /// </summary>
        internal static bool TryBeginSnapshotSave(
            IReadOnlyCollection<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folderRecords,
            uint artifactDependencyVersion,
            RecoveryState recoveryState,
            Func<bool> canCommit,
            out SnapshotSaveAttempt attempt)
        {
            attempt = null;
            if (records == null || (canCommit != null && !canCommit()))
            {
                return false;
            }

            foreach (var record in records)
            {
                if (record == null)
                {
                    return false;
                }
            }

            if (folderRecords != null)
            {
                for (var i = 0; i < folderRecords.Count; i++)
                {
                    if (folderRecords[i] == null || folderRecords[i].FolderPath.IsEmpty)
                    {
                        return false;
                    }
                }
            }

            var coveredGeneration = recoveryState.JournalGeneration;
            var discardsJournal = coveredGeneration == UntrustedJournalGeneration;
            if (discardsJournal)
            {
                coveredGeneration = ReadSnapshotCoveredJournalGeneration();
                if (coveredGeneration == UntrustedJournalGeneration)
                {
                    return false;
                }
            }

            try
            {
                CreateCacheDirectory(CacheDirectory);
                attempt = new SnapshotSaveAttempt(
                    records,
                    folderRecords,
                    artifactDependencyVersion,
                    recoveryState,
                    coveredGeneration,
                    discardsJournal,
                    canCommit,
                    CacheFilePath
                        + "."
                        + Interlocked.Increment(ref _snapshotTempSequence)
                            .ToString(CultureInfo.InvariantCulture)
                        + ".tmp");
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Phase two, thread-agnostic: serialise the captured snapshot into the staged
        /// temp file. This is the whole cost of a save — one BinaryWriter call per record
        /// field — and it touches no recovery state, so a worker thread may run it.
        /// Throws the same IO exceptions a caller of <see cref="Save"/> already handles.
        /// </summary>
        internal static void WriteStagedSnapshot(SnapshotSaveAttempt attempt)
        {
            SnapshotSerializationObserverForTests?.Invoke();

            // Symmetric with the load stream: every record field is one small
            // BinaryWriter call, so the default 4 KB buffer turns the snapshot
            // into thousands of write syscalls.
            using (var fs = new FileStream(
                attempt.TempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1 << 20))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                bw.Write(MagicBytes);
                bw.Write(Version);
                bw.Write(Guid.NewGuid().ToByteArray());
                bw.Write(attempt.Recovery.AssignmentTimestamp);
                bw.Write(attempt.Recovery.DefinitionTimestamp);
                bw.Write(attempt.ArtifactDependencyVersion);
                bw.Write(attempt.CoveredJournalGeneration);
                bw.Write(attempt.Records.Count);

                foreach (var record in attempt.Records)
                {
                    bw.Write(record.Guid ?? string.Empty);
                    bw.Write(record.AssetPath ?? string.Empty);
                    bw.Write(record.Name ?? string.Empty);
                    bw.Write(record.TypeName ?? string.Empty);
                    bw.Write(record.TagSearchText ?? string.Empty);
                    bw.Write(record.UnityLabels.Count);
                    for (var labelIndex = 0; labelIndex < record.UnityLabels.Count; labelIndex++)
                    {
                        bw.Write(record.UnityLabels[labelIndex]);
                    }

                    bw.Write(record.BaseTypeNames.Count);
                    for (var baseTypeIndex = 0; baseTypeIndex < record.BaseTypeNames.Count; baseTypeIndex++)
                    {
                        bw.Write(record.BaseTypeNames[baseTypeIndex]);
                    }
                }

                var folderRecords = attempt.FolderRecords;
                var folderCount = folderRecords?.Count ?? 0;
                bw.Write(folderCount);
                if (folderRecords != null)
                {
                    for (var i = 0; i < folderRecords.Count; i++)
                    {
                        var folder = folderRecords[i];
                        bw.Write(folder.FolderPath.Value);
                        bw.Write(folder.Guid ?? string.Empty);
                        var labelCount = folder.UnityLabels.Count;
                        bw.Write(labelCount);
                        for (var labelIndex = 0; labelIndex < labelCount; labelIndex++)
                        {
                            bw.Write(folder.UnityLabels[labelIndex]);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Main-thread phase three: swap the snapshot in atomically, retire the
        /// journal generations it covers and clear the markers it captured. A marker
        /// written since the capture stays, and means the next load verifies — that
        /// is the marker's job, not a failure of this save. False leaves the previous
        /// snapshot in place, except after a retirement that could not complete,
        /// where the new snapshot is committed and the dirty marker records that its
        /// journal must be distrusted.
        /// </summary>
        internal static bool TryCommitSnapshotSave(SnapshotSaveAttempt attempt)
        {
            if (attempt == null)
            {
                return false;
            }

            try
            {
                if (attempt.CanCommit != null && !attempt.CanCommit())
                {
                    return false;
                }

                // Another save landed a snapshot covering MORE of the journal while
                // this one was staged (a second synchronizer instance in the
                // process). Replacing it would lose the batches only it holds; this
                // attempt yields and nothing on disk changes.
                var diskCoveredGeneration = ReadSnapshotCoveredJournalGeneration();
                if (diskCoveredGeneration == UntrustedJournalGeneration
                    || diskCoveredGeneration > attempt.CoveredJournalGeneration)
                {
                    return false;
                }

                // Atomic replace: File.Replace swaps the temp file in for the existing cache
                // as one operation, so a crash can't land in the delete-then-move window
                // where the old cache is already gone but the new one isn't in place yet.
                // When no cache exists there's nothing to replace, so a plain move — itself
                // atomic — suffices.
                if (File.Exists(CacheFilePath))
                {
                    File.Replace(attempt.TempPath, CacheFilePath, null);
                }
                else
                {
                    File.Move(attempt.TempPath, CacheFilePath);
                }

                var retired = attempt.DiscardsJournal
                    ? DiscardPendingChangeJournal()
                    : TryRetirePendingChanges(attempt.CoveredJournalGeneration);
                if (!retired)
                {
                    MarkDirty();
                    return false;
                }

                TryClearAuthoritativeFallback(attempt.Recovery.AuthoritativeFallbackToken);
                TryClearDirtyMarker(attempt.Recovery.DirtyToken);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                // A committed snapshot has already been moved out of the staged path,
                // so this only removes the staging of an aborted commit.
                DeleteStagedFile(attempt.TempPath);
            }
        }

        /// <summary>
        /// Gives up an attempt whose staged write never completed: the temp file goes
        /// and the cache and recovery state are left exactly as the begin phase found
        /// them. Matches the IO-failure legs of a synchronous <see cref="Save"/>, which
        /// do not mark dirty — the caller decides that.
        /// </summary>
        internal static void AbandonSnapshotSave(SnapshotSaveAttempt attempt)
        {
            if (attempt == null)
            {
                return;
            }

            DeleteStagedFile(attempt.TempPath);
        }

        internal static void Invalidate()
        {
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    File.Delete(CacheFilePath);
                }
            }
            catch
            {
                // Swallow: invalidation is best-effort. Without a snapshot the next
                // load walks the project.
            }
        }

        internal static bool MarkDirty()
        {
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] mark-dirty untrusted thread={Thread.CurrentThread.ManagedThreadId}\n{Environment.StackTrace}");
            return WriteDirtyToken(
                "untrusted:" + Guid.NewGuid().ToString("N"));
        }

        private static string MarkMalformedJournalDirty(string json)
        {
            using (var sha256 = SHA256.Create())
            {
                var digest = BitConverter.ToString(
                    sha256.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
                var prefix = "malformed-journal:" + digest + ":";
                var existing = ReadRecoveryToken(DirtyMarkerPath, out var readable);
                if (readable
                    && existing != null
                    && existing.StartsWith(prefix, StringComparison.Ordinal)
                    && existing.Length > prefix.Length)
                {
                    return existing;
                }

                var token = prefix + Guid.NewGuid().ToString("N");
                return WriteDirtyToken(token) ? token : null;
            }
        }

        internal static bool MarkDirtyForPendingChanges()
        {
            if (File.Exists(PendingChangesAuthoritativeFallbackPath))
            {
                return MarkDirty();
            }

            var generation = GetPendingChangeJournalGeneration();
            return generation == UntrustedJournalGeneration
                ? MarkDirty()
                : WriteDirtyToken(
                    "journal:"
                    + generation.ToString(CultureInfo.InvariantCulture)
                    + ":"
                    + Guid.NewGuid().ToString("N"));
        }

        private static bool WriteDirtyToken(string token)
        {
            // The background cache-validation worker marks the cache dirty on the same
            // paths the main thread does, so the staging file must be private to this
            // write: sharing one name let two writers overwrite each other's staged
            // token and made the winner report failure for a marker it had committed.
            var tempPath = DirtyMarkerPath
                + "."
                + Interlocked.Increment(ref _dirtyMarkerTempSequence)
                    .ToString(CultureInfo.InvariantCulture)
                + ".tmp";
            try
            {
                CreateCacheDirectory(CacheDirectory);
                File.WriteAllText(
                    tempPath,
                    token,
                    Encoding.ASCII);
                if (DirtyMarkerCommitOverride != null)
                {
                    DirtyMarkerCommitOverride(tempPath, DirtyMarkerPath);
                }
                else if (File.Exists(DirtyMarkerPath))
                {
                    File.Replace(tempPath, DirtyMarkerPath, null);
                }
                else
                {
                    File.Move(tempPath, DirtyMarkerPath);
                }

                return true;
            }
            catch (IOException)
            {
                // A missing marker cannot make the snapshot authoritative: its
                // existing signature still rejects ordinary observed changes.
                DeleteStagedFile(tempPath);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                DeleteStagedFile(tempPath);
                return false;
            }
        }

        // A unique staging name cannot be cleaned up by the next writer, so each failed
        // write removes its own leftover rather than accumulating files in Library/.
        private static void DeleteStagedFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static void RecordPendingChanges(ProjectAssetChanges changes)
        {
            changes = changes.Normalize();
            if (changes.IsEmpty)
            {
                return;
            }

            if (File.Exists(PendingChangesAuthoritativeFallbackPath))
            {
                // Exact replay is already abandoned. Refreshing the token records that
                // this callback happened after any save that may currently be
                // preparing to clear the marker, without growing the journal.
                if (!MarkAuthoritativeFallback())
                {
                    Invalidate();
                }
                return;
            }

            try
            {
                var journal = ReadPendingChangeJournal(out var journalIsTrustworthy);
                if (!journalIsTrustworthy)
                {
                    throw new InvalidDataException(
                        "the existing pending-change journal could not be read");
                }

                // Compaction is what costs: it emits one batch per moved pair and
                // merges them pairwise, so a 5,000-move callback pays a quadratic
                // intersect scan before anything decides the result is too large to
                // keep. Compaction can only coalesce entries, never invent them, so a
                // raw total already past the bound cannot compact under it. The
                // outcome is the fallback marker either way: the next load verifies
                // the snapshot against a walk, which observes the final state.
                var exceedsEntryBoundBeforeCompaction =
                    CountRawEntries(journal) + CountRawEntries(changes)
                    > MaxPendingChangeEntryCount;
                var compacted = exceedsEntryBoundBeforeCompaction
                    ? null
                    : CompactPendingChanges(journal, changes);
                if (exceedsEntryBoundBeforeCompaction
                    || RequiresAuthoritativeFallback(compacted))
                {
                    if (!MarkAuthoritativeFallback())
                    {
                        throw new IOException(
                            "the authoritative fallback marker could not be persisted");
                    }

                    DiscardPendingChangeJournal();
                    return;
                }

                // The compacted journal — every earlier file's batches merged with
                // this callback's — is written as ONE new file under the next
                // generation, and the files it folded are deleted after it lands.
                // Storage therefore tracks final paths, not callback history, and a
                // read costs one file. A save that captured an earlier generation
                // finds nothing at or below it to retire and leaves the folded
                // file, whose batches then replay once more on a snapshot that
                // already holds them: a re-import of a present file, a delete of
                // an absent one, a move of a source that is gone — all no-ops.
                var folded = BatchFilePaths();
                WritePendingChangeJournalFile(journal.Generation + 1, compacted);
                for (var i = 0; i < folded.Count; i++)
                {
                    DeleteStagedFile(folded[i]);
                }
            }
            catch (Exception ex)
            {
                // Journaling is recovery state. Its failure must make the next load
                // verify, but must not prevent the live subscriber from applying the
                // same concrete callback now.
                if (!MarkAuthoritativeFallback())
                {
                    // If neither exact replay nor the marker can be made durable,
                    // removing the cache is the only fail-closed outcome.
                    Invalidate();
                }
                LogWarning(
                    $"[Zep] Could not persist reload asset changes "
                    + $"({ex.GetType().Name}: {ex.Message}); the next reload will verify the index.");
            }
        }

        internal static IReadOnlyList<ProjectAssetChanges> CapturePendingChanges()
        {
            return CapturePendingChanges(out _);
        }

        internal static IReadOnlyList<ProjectAssetChanges> CapturePendingChanges(
            out long generation)
        {
            var result = CapturePendingChangesWithRecovery(out var recoveryState);
            generation = recoveryState.RequiresVerification
                ? UntrustedJournalGeneration
                : recoveryState.JournalGeneration;
            return result;
        }

        /// <summary>
        /// The batches recorded since the last snapshot, compacted and in order, with
        /// the state the caller's save will cover. An empty list with
        /// <see cref="RecoveryState.RequiresVerification"/> set means the journal is
        /// not a complete account; the caller still loads its snapshot and verifies.
        /// </summary>
        internal static IReadOnlyList<ProjectAssetChanges> CapturePendingChangesWithRecovery(
            out RecoveryState recoveryState)
        {
            if (File.Exists(PendingChangesAuthoritativeFallbackPath))
            {
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log("[Zep/perf] recovery-capture reason=fallback-marker");
                recoveryState = CaptureRecoveryState(requiresVerification: true);
                return Array.Empty<ProjectAssetChanges>();
            }

            var journal = ReadPendingChangeJournal(out var isTrustworthy);
            if (!isTrustworthy)
            {
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log("[Zep/perf] recovery-capture reason=untrusted-journal");
                recoveryState = CaptureRecoveryState(requiresVerification: true);
                return Array.Empty<ProjectAssetChanges>();
            }

            var compacted = CompactPendingChanges(journal, default);
            var result = new List<ProjectAssetChanges>(compacted.Count);
            for (var i = 0; i < compacted.Count; i++)
            {
                var normalized = ToProjectAssetChanges(compacted[i]).Normalize();
                if (!normalized.IsEmpty)
                {
                    result.Add(normalized);
                }
            }

            recoveryState = CaptureRecoveryState();
            if (!recoveryState.RequiresVerification
                && recoveryState.JournalGeneration != journal.Generation)
            {
                // A batch file appeared or was retired between the read and the
                // capture: the list is not the account the state describes.
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] recovery-capture reason=generation-drift journalGen={journal.Generation} stateGen={recoveryState.JournalGeneration}");
                recoveryState = CaptureRecoveryState(requiresVerification: true);
            }

            return result;
        }

        internal static void ClearPendingChanges()
        {
            try
            {
                DiscardPendingChangeJournal();
                DeleteStagedFile(PendingChangesAuthoritativeFallbackPath);
                DeleteStagedFile(PendingChangesAuthoritativeFallbackPath + ".tmp");

                // Dirty-marker staging files carry a per-write sequence number, so the
                // sweep has to match the family rather than one fixed name.
                if (Directory.Exists(CacheDirectory))
                {
                    var dirtyTempPaths = Directory.GetFiles(
                        CacheDirectory,
                        Path.GetFileName(DirtyMarkerPath) + ".*.tmp");
                    for (var i = 0; i < dirtyTempPaths.Length; i++)
                    {
                        File.Delete(dirtyTempPaths[i]);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static List<PendingChangeBatch> CompactPendingChanges(
            PendingChangeJournal journal,
            ProjectAssetChanges appended)
        {
            var batches = new List<PendingChangeBatch>();

            void Append(ProjectAssetChanges changes)
            {
                changes = changes.Normalize();
                for (var i = 0; i < changes.Moved.Length; i++)
                {
                    var source = i < changes.MovedFromPaths.Length
                        ? changes.MovedFromPaths[i]
                        : string.Empty;
                    var target = changes.Moved[i];
                    if (string.IsNullOrEmpty(source))
                    {
                        AppendBatch(new PendingChangeBatch
                        {
                            Imported = new[] { target },
                        });
                        continue;
                    }

                    AppendBatch(new PendingChangeBatch
                    {
                        Moved = new[] { target },
                        MovedFromPaths = new[] { source },
                    });
                }

                if (changes.Deleted.Length > 0)
                {
                    AppendBatch(new PendingChangeBatch
                    {
                        Deleted = changes.Deleted,
                    });
                }

                if (changes.Imported.Length > 0)
                {
                    AppendBatch(new PendingChangeBatch
                    {
                        Imported = changes.Imported,
                    });
                }
            }

            void AppendBatch(PendingChangeBatch next)
            {
                if (batches.Count > 0
                    && TryMergePendingChangeBatches(
                        batches[batches.Count - 1],
                        next,
                        out var merged))
                {
                    batches[batches.Count - 1] = merged;
                }
                else
                {
                    batches.Add(next);
                }
            }

            if (journal?.Changes != null)
            {
                Append(ToProjectAssetChanges(journal.Changes));
            }

            if (journal?.Batches != null)
            {
                for (var i = 0; i < journal.Batches.Count; i++)
                {
                    if (journal.Batches[i] != null)
                    {
                        Append(ToProjectAssetChanges(journal.Batches[i]));
                    }
                }
            }

            if (appended.Imported != null
                || appended.Deleted != null
                || appended.Moved != null
                || appended.MovedFromPaths != null)
            {
                Append(appended);
            }

            return batches;
        }

        private static int CountRawEntries(ProjectAssetChanges changes)
        {
            return (changes.Imported?.Length ?? 0)
                + (changes.Deleted?.Length ?? 0)
                + (changes.Moved?.Length ?? 0)
                + (changes.MovedFromPaths?.Length ?? 0);
        }

        private static int CountRawEntries(PendingChangeBatch batch)
        {
            if (batch == null)
            {
                return 0;
            }

            return (batch.Imported?.Length ?? 0)
                + (batch.Deleted?.Length ?? 0)
                + (batch.Moved?.Length ?? 0)
                + (batch.MovedFromPaths?.Length ?? 0);
        }

        private static int CountRawEntries(PendingChangeJournal journal)
        {
            if (journal == null)
            {
                return 0;
            }

            var count = CountRawEntries(journal.Changes);
            if (journal.Batches == null)
            {
                return count;
            }

            for (var i = 0; i < journal.Batches.Count; i++)
            {
                count += CountRawEntries(journal.Batches[i]);
            }

            return count;
        }

        private static bool RequiresAuthoritativeFallback(
            IReadOnlyList<PendingChangeBatch> batches)
        {
            if (batches.Count > MaxPendingChangeBatchCount)
            {
                return true;
            }

            var entryCount = 0;
            var pathBytes = 0;
            for (var i = 0; i < batches.Count; i++)
            {
                var changes = ToProjectAssetChanges(batches[i]).Normalize();
                entryCount += changes.Imported.Length
                    + changes.Deleted.Length
                    + changes.Moved.Length
                    + changes.MovedFromPaths.Length;
                if (entryCount > MaxPendingChangeEntryCount)
                {
                    return true;
                }

                pathBytes += CountUtf8Bytes(changes.Imported)
                    + CountUtf8Bytes(changes.Deleted)
                    + CountUtf8Bytes(changes.Moved)
                    + CountUtf8Bytes(changes.MovedFromPaths);
                if (pathBytes > MaxPendingChangePathBytes)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountUtf8Bytes(IReadOnlyList<string> paths)
        {
            var count = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                count += Encoding.UTF8.GetByteCount(paths[i] ?? string.Empty);
            }

            return count;
        }

        private static bool TryMergePendingChangeBatches(
            PendingChangeBatch previous,
            PendingChangeBatch next,
            out PendingChangeBatch merged)
        {
            PendingChangeBatchMergeObserverForTests?.Invoke();
            merged = null;
            var previousChanges = ToProjectAssetChanges(previous).Normalize();
            var nextChanges = ToProjectAssetChanges(next).Normalize();

            if (IsMoveOnly(previousChanges) && IsMoveOnly(nextChanges))
            {
                var previousSource = previousChanges.MovedFromPaths[0];
                var previousTarget = previousChanges.Moved[0];
                var nextSource = nextChanges.MovedFromPaths[0];
                var nextTarget = nextChanges.Moved[0];
                if (string.Equals(
                    previousTarget,
                    nextSource,
                    StringComparison.OrdinalIgnoreCase))
                {
                    merged = ToPendingChangeBatch(new ProjectAssetChanges(
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        new[] { nextTarget },
                        new[] { previousSource }));
                    return true;
                }

                if (!MovePathsIntersect(previousChanges, nextChanges))
                {
                    merged = ToPendingChangeBatch(new ProjectAssetChanges(
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        new[] { previousTarget, nextTarget },
                        new[] { previousSource, nextSource }));
                    return true;
                }

                return false;
            }

            if (IsImportOnly(previousChanges) && IsDeleteOnly(nextChanges)
                && AllCoveredByRoots(previousChanges.Imported, nextChanges.Deleted))
            {
                merged = ToPendingChangeBatch(new ProjectAssetChanges(
                    Array.Empty<string>(),
                    nextChanges.Deleted,
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                return true;
            }

            var previousLastPhase = GetLastPhase(previousChanges);
            var nextFirstPhase = GetFirstPhase(nextChanges);
            if (previousLastPhase <= nextFirstPhase
                && (previousChanges.Moved.Length == 0
                    || nextChanges.Moved.Length == 0
                    || !MovePathsIntersect(previousChanges, nextChanges)))
            {
                merged = ToPendingChangeBatch(new ProjectAssetChanges(
                    Combine(previousChanges.Imported, nextChanges.Imported),
                    Combine(previousChanges.Deleted, nextChanges.Deleted),
                    Combine(previousChanges.Moved, nextChanges.Moved),
                    Combine(previousChanges.MovedFromPaths, nextChanges.MovedFromPaths))
                    .Normalize());
                return true;
            }

            return false;
        }

        private static bool IsMoveOnly(ProjectAssetChanges changes)
            => changes.Moved.Length == 1
                && changes.Deleted.Length == 0
                && changes.Imported.Length == 0;

        private static bool IsDeleteOnly(ProjectAssetChanges changes)
            => changes.Deleted.Length > 0
                && changes.Moved.Length == 0
                && changes.Imported.Length == 0;

        private static bool IsImportOnly(ProjectAssetChanges changes)
            => changes.Imported.Length > 0
                && changes.Moved.Length == 0
                && changes.Deleted.Length == 0;

        private static int GetFirstPhase(ProjectAssetChanges changes)
        {
            if (changes.Moved.Length > 0)
            {
                return 0;
            }

            return changes.Deleted.Length > 0 ? 1 : 2;
        }

        private static int GetLastPhase(ProjectAssetChanges changes)
        {
            if (changes.Imported.Length > 0)
            {
                return 2;
            }

            return changes.Deleted.Length > 0 ? 1 : 0;
        }

        private static bool MovePathsIntersect(
            ProjectAssetChanges left,
            ProjectAssetChanges right)
        {
            for (var leftIndex = 0; leftIndex < left.Moved.Length; leftIndex++)
            {
                for (var rightIndex = 0; rightIndex < right.Moved.Length; rightIndex++)
                {
                    if (PathsIntersect(
                            left.MovedFromPaths[leftIndex],
                            right.MovedFromPaths[rightIndex])
                        || PathsIntersect(
                            left.MovedFromPaths[leftIndex],
                            right.Moved[rightIndex])
                        || PathsIntersect(
                            left.Moved[leftIndex],
                            right.MovedFromPaths[rightIndex])
                        || PathsIntersect(
                            left.Moved[leftIndex],
                            right.Moved[rightIndex]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool PathsIntersect(string left, string right)
            => IsAtOrBelow(left, right) || IsAtOrBelow(right, left);

        private static bool IsAtOrBelow(string path, string root)
            => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || (path.Length > root.Length
                    && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && path[root.Length] == '/');

        private static bool AllCoveredByRoots(
            IReadOnlyList<string> paths,
            IReadOnlyList<string> roots)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var covered = false;
                for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
                {
                    if (IsAtOrBelow(paths[i], roots[rootIndex]))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] Combine(string[] left, string[] right)
        {
            var combined = new string[left.Length + right.Length];
            Array.Copy(left, combined, left.Length);
            Array.Copy(right, 0, combined, left.Length, right.Length);
            return combined;
        }

        private static PendingChangeBatch ToPendingChangeBatch(
            ProjectAssetChanges changes)
        {
            changes = changes.Normalize();
            return new PendingChangeBatch
            {
                Imported = changes.Imported,
                Deleted = changes.Deleted,
                Moved = changes.Moved,
                MovedFromPaths = changes.MovedFromPaths,
            };
        }

        private static ProjectAssetChanges ToProjectAssetChanges(
            PendingChangeBatch batch)
        {
            return batch == null
                ? default
                : new ProjectAssetChanges(
                    batch.Imported,
                    batch.Deleted,
                    batch.Moved,
                    batch.MovedFromPaths);
        }

        private static long GetPendingChangeJournalGeneration()
        {
            return GetPendingChangeJournalGeneration(out _);
        }

        private static long GetPendingChangeJournalGeneration(
            out string malformedJournalToken)
        {
            var journal = ReadPendingChangeJournal(
                out var isTrustworthy,
                out malformedJournalToken);
            return isTrustworthy
                ? journal.Generation
                : UntrustedJournalGeneration;
        }

        internal static RecoveryState CaptureRecoveryState(
            bool requiresVerification = false)
        {
            var journalGeneration = GetPendingChangeJournalGeneration(
                out var malformedJournalToken);
            var fallbackToken = ReadRecoveryToken(
                PendingChangesAuthoritativeFallbackPath,
                out var fallbackReadable);
            var dirtyToken = ReadRecoveryToken(
                DirtyMarkerPath,
                out var dirtyReadable);
            var assignmentTimestamp = CaptureTagStateTimestamp(TagAssignmentsPath);
            var definitionTimestamp = CaptureTagStateTimestamp(TagDefinitionsPath);
            var malformedJournalEvidence = journalGeneration == UntrustedJournalGeneration
                && malformedJournalToken != null
                && string.Equals(
                    malformedJournalToken,
                    dirtyToken,
                    StringComparison.Ordinal);
            var dirtyCoveredByJournal = dirtyToken == null
                || IsDirtyTokenForJournal(dirtyToken, journalGeneration);
            return new RecoveryState(
                journalGeneration,
                fallbackToken,
                dirtyToken,
                assignmentTimestamp,
                definitionTimestamp,
                requiresVerification
                    || !fallbackReadable
                    || !dirtyReadable
                    || !IsValidTagStateTimestamp(assignmentTimestamp)
                    || !IsValidTagStateTimestamp(definitionTimestamp)
                    || journalGeneration == UntrustedJournalGeneration
                    || fallbackToken != null
                    || !dirtyCoveredByJournal,
                malformedJournalEvidence);
        }

        private static long CaptureTagStateTimestamp(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    return UnavailableTagStateTimestamp;
                }

                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (FileNotFoundException)
            {
                return MissingTagStateTimestamp;
            }
            catch (DirectoryNotFoundException)
            {
                return MissingTagStateTimestamp;
            }
            catch (IOException)
            {
                return UnavailableTagStateTimestamp;
            }
            catch (UnauthorizedAccessException)
            {
                return UnavailableTagStateTimestamp;
            }
        }

        private static bool IsValidTagStateTimestamp(long timestamp)
        {
            return timestamp >= MissingTagStateTimestamp;
        }

        private static string ReadRecoveryToken(string path, out bool readable)
        {
            try
            {
                readable = true;
                return File.Exists(path)
                    ? File.ReadAllText(path, Encoding.ASCII)
                    : null;
            }
            catch (IOException)
            {
                readable = false;
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
                return null;
            }
        }

        // A dirty token names the journal generation it was written against, and
        // is covered while the journal — or the snapshot's floor — reaches that far.
        private static bool IsDirtyTokenForJournal(
            string dirtyToken,
            long journalGeneration)
        {
            const string prefix = "journal:";
            if (string.IsNullOrEmpty(dirtyToken)
                || journalGeneration == UntrustedJournalGeneration
                || !dirtyToken.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var end = dirtyToken.IndexOf(':', prefix.Length);
            return end > prefix.Length
                && end + 1 < dirtyToken.Length
                && long.TryParse(
                    dirtyToken.Substring(prefix.Length, end - prefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var tokenGeneration)
                && tokenGeneration <= journalGeneration;
        }

        // Retires every batch file at or below the generation a committed snapshot
        // covers. Files above it are batches that arrived while the snapshot was
        // being written; they are exactly what the next load replays on top of it.
        private static bool TryRetirePendingChanges(long coveredGeneration)
        {
            try
            {
                if (!Directory.Exists(PendingChangesDirectory))
                {
                    return true;
                }

                var files = Directory.GetFiles(
                    PendingChangesDirectory,
                    "*" + BatchFileExtension);
                for (var i = 0; i < files.Length; i++)
                {
                    if (!TryParseBatchGeneration(files[i], out var generation)
                        || generation > coveredGeneration)
                    {
                        continue;
                    }

                    if (PendingChangesRetireOverride != null)
                    {
                        PendingChangesRetireOverride(files[i]);
                    }
                    else
                    {
                        File.Delete(files[i]);
                    }
                }

                files = Directory.GetFiles(
                    PendingChangesDirectory,
                    "*" + BatchFileExtension);
                for (var i = 0; i < files.Length; i++)
                {
                    if (TryParseBatchGeneration(files[i], out var generation)
                        && generation <= coveredGeneration)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryParseBatchGeneration(string path, out long generation)
        {
            return long.TryParse(
                Path.GetFileNameWithoutExtension(path),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out generation);
        }

        private static string BatchFilePath(long generation)
        {
            return Path.Combine(
                PendingChangesDirectory,
                generation.ToString("D19", CultureInfo.InvariantCulture) + BatchFileExtension);
        }

        private static PendingChangeJournal ReadPendingChangeJournal(
            out bool isTrustworthy)
        {
            return ReadPendingChangeJournal(out isTrustworthy, out _);
        }

        // The journal as one document: every batch file's batches in generation
        // order, and the generation the next append continues from — the highest
        // file, or the snapshot's floor when no file is above it. A file that
        // cannot be read or parsed makes the whole journal untrusted; a malformed
        // one is also recorded in the dirty marker, so repeated observation of the
        // same damage is stable.
        private static PendingChangeJournal ReadPendingChangeJournal(
            out bool isTrustworthy,
            out string malformedJournalToken)
        {
            isTrustworthy = true;
            malformedJournalToken = null;
            var journal = new PendingChangeJournal();
            var floor = ReadSnapshotCoveredJournalGeneration();
            if (floor == UntrustedJournalGeneration)
            {
                isTrustworthy = false;
                return journal;
            }

            journal.Generation = floor;
            string[] files;
            try
            {
                if (!Directory.Exists(PendingChangesDirectory))
                {
                    return journal;
                }

                files = Directory.GetFiles(
                    PendingChangesDirectory,
                    "*" + BatchFileExtension);
            }
            catch (IOException)
            {
                isTrustworthy = false;
                return new PendingChangeJournal();
            }
            catch (UnauthorizedAccessException)
            {
                isTrustworthy = false;
                return new PendingChangeJournal();
            }

            var generations = new List<(long Generation, string Path)>(files.Length);
            for (var i = 0; i < files.Length; i++)
            {
                if (TryParseBatchGeneration(files[i], out var generation))
                {
                    generations.Add((generation, files[i]));
                }
            }

            generations.Sort((left, right) => left.Generation.CompareTo(right.Generation));
            for (var i = 0; i < generations.Count; i++)
            {
                string json;
                try
                {
                    json = File.ReadAllText(generations[i].Path, Encoding.UTF8);
                }
                catch (IOException)
                {
                    isTrustworthy = false;
                    return new PendingChangeJournal();
                }
                catch (UnauthorizedAccessException)
                {
                    isTrustworthy = false;
                    return new PendingChangeJournal();
                }

                PendingChangeJournal document;
                try
                {
                    document = ParsePendingChangeJournal(json);
                }
                catch (FormatException)
                {
                    malformedJournalToken = MarkMalformedJournalDirty(json);
                    isTrustworthy = false;
                    return new PendingChangeJournal();
                }

                if (document.Changes != null && CountRawEntries(document.Changes) > 0)
                {
                    journal.Batches.Add(document.Changes);
                }

                if (document.Batches != null)
                {
                    for (var batchIndex = 0; batchIndex < document.Batches.Count; batchIndex++)
                    {
                        if (document.Batches[batchIndex] != null)
                        {
                            journal.Batches.Add(document.Batches[batchIndex]);
                        }
                    }
                }

                if (generations[i].Generation > journal.Generation)
                {
                    journal.Generation = generations[i].Generation;
                }
            }

            return journal;
        }

        // Same document shape Unity's serializer wrote, so a journal left on disk
        // by an older package version still reads: {"Generation":n,"Changes":
        // {batch},"Batches":[{batch},...]} where a batch is four string arrays.
        // Unknown members are ignored and absent ones read as empty, exactly as
        // before; a root that is not an object is malformed (as it was before).
        private static PendingChangeJournal ParsePendingChangeJournal(string json)
        {
            var root = JsonText.Parse(json);
            var journal = new PendingChangeJournal();
            if (root == null)
            {
                return journal;
            }

            if (!(root is Dictionary<string, object>))
            {
                throw new FormatException("The pending-change journal root is not an object.");
            }

            if (JsonText.TryGetMember(root, "Generation", out var generation)
                && JsonText.TryGetInt64(generation, out var parsedGeneration))
            {
                journal.Generation = parsedGeneration;
            }

            if (JsonText.TryGetMember(root, "Changes", out var changes)
                && changes is Dictionary<string, object>)
            {
                journal.Changes = ParsePendingChangeBatch(changes);
            }

            if (JsonText.TryGetMember(root, "Batches", out var batches)
                && batches is List<object> batchList)
            {
                for (var i = 0; i < batchList.Count; i++)
                {
                    journal.Batches.Add(
                        batchList[i] is Dictionary<string, object>
                            ? ParsePendingChangeBatch(batchList[i])
                            : null);
                }
            }

            return journal;
        }

        private static PendingChangeBatch ParsePendingChangeBatch(object batch)
        {
            return new PendingChangeBatch
            {
                Imported = JsonText.ReadStringArray(batch, "Imported"),
                Deleted = JsonText.ReadStringArray(batch, "Deleted"),
                Moved = JsonText.ReadStringArray(batch, "Moved"),
                MovedFromPaths = JsonText.ReadStringArray(batch, "MovedFromPaths"),
            };
        }

        private static string SerializePendingChangeJournal(PendingChangeJournal journal)
        {
            var builder = new StringBuilder(256);
            builder.Append("{\"Generation\":")
                .Append(journal.Generation.ToString(CultureInfo.InvariantCulture))
                .Append(",\"Changes\":");
            AppendPendingChangeBatch(builder, journal.Changes);
            builder.Append(",\"Batches\":[");
            var batches = journal.Batches;
            for (var i = 0; i < (batches?.Count ?? 0); i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                AppendPendingChangeBatch(builder, batches[i]);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static void AppendPendingChangeBatch(StringBuilder builder, PendingChangeBatch batch)
        {
            batch ??= new PendingChangeBatch();
            builder.Append("{\"Imported\":");
            JsonText.AppendStringArray(builder, batch.Imported ?? Array.Empty<string>());
            builder.Append(",\"Deleted\":");
            JsonText.AppendStringArray(builder, batch.Deleted ?? Array.Empty<string>());
            builder.Append(",\"Moved\":");
            JsonText.AppendStringArray(builder, batch.Moved ?? Array.Empty<string>());
            builder.Append(",\"MovedFromPaths\":");
            JsonText.AppendStringArray(builder, batch.MovedFromPaths ?? Array.Empty<string>());
            builder.Append('}');
        }

        private static List<string> BatchFilePaths()
        {
            var paths = new List<string>();
            if (Directory.Exists(PendingChangesDirectory))
            {
                paths.AddRange(Directory.GetFiles(
                    PendingChangesDirectory,
                    "*" + BatchFileExtension));
            }

            return paths;
        }

        private static void WritePendingChangeJournalFile(
            long generation,
            List<PendingChangeBatch> batches)
        {
            CreateCacheDirectory(PendingChangesDirectory);
            var path = BatchFilePath(generation);
            var tempPath = path + ".tmp";
            DeleteStagedFile(tempPath);
            var journal = new PendingChangeJournal { Generation = generation };
            if (batches.Count == 1)
            {
                journal.Changes = batches[0];
            }
            else
            {
                journal.Batches = batches;
            }

            File.WriteAllText(
                tempPath,
                SerializePendingChangeJournal(journal),
                Encoding.UTF8);

            if (PendingChangesCommitOverride != null)
            {
                PendingChangesCommitOverride(tempPath, path);
                return;
            }

            // A move onto an existing name fails: two writers claiming one
            // generation is the one race left, and its failure takes the fallback
            // marker like any other write failure.
            File.Move(tempPath, path);
        }

        // Removes every batch file, the legacy single-file journal and its write
        // marker. True when the directory holds no batch afterwards.
        private static bool DiscardPendingChangeJournal()
        {
            try
            {
                DeleteStagedFile(LegacyPendingChangesJournalPath);
                DeleteStagedFile(LegacyPendingChangesJournalPath + ".tmp");
                DeleteStagedFile(LegacyPendingChangesWriteMarkerPath);
                if (!Directory.Exists(PendingChangesDirectory))
                {
                    return true;
                }

                var files = Directory.GetFiles(PendingChangesDirectory);
                for (var i = 0; i < files.Length; i++)
                {
                    File.Delete(files[i]);
                }

                return Directory.GetFiles(
                    PendingChangesDirectory,
                    "*" + BatchFileExtension).Length == 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool MarkAuthoritativeFallback()
        {
            try
            {
                CreateCacheDirectory(CacheDirectory);
                var tempPath = PendingChangesAuthoritativeFallbackPath + ".tmp";
                File.WriteAllText(
                    tempPath,
                    Guid.NewGuid().ToString("N"),
                    Encoding.ASCII);
                if (AuthoritativeFallbackCommitOverride != null)
                {
                    AuthoritativeFallbackCommitOverride(
                        tempPath,
                        PendingChangesAuthoritativeFallbackPath);
                }
                else if (File.Exists(PendingChangesAuthoritativeFallbackPath))
                {
                    File.Replace(
                        tempPath,
                        PendingChangesAuthoritativeFallbackPath,
                        null);
                }
                else
                {
                    File.Move(tempPath, PendingChangesAuthoritativeFallbackPath);
                }

                MarkDirty();
                return true;
            }
            catch (IOException)
            {
                return MarkDirty();
            }
            catch (UnauthorizedAccessException)
            {
                return MarkDirty();
            }
        }

        private static bool TryClearAuthoritativeFallback(string expectedToken)
        {
            try
            {
                if (!File.Exists(PendingChangesAuthoritativeFallbackPath))
                {
                    return expectedToken == null;
                }

                if (string.IsNullOrEmpty(expectedToken)
                    || !string.Equals(
                        File.ReadAllText(
                            PendingChangesAuthoritativeFallbackPath,
                            Encoding.ASCII),
                        expectedToken,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                File.Delete(PendingChangesAuthoritativeFallbackPath);
                return !File.Exists(PendingChangesAuthoritativeFallbackPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryClearDirtyMarker(string expectedToken)
        {
            try
            {
                if (!File.Exists(DirtyMarkerPath))
                {
                    return expectedToken == null;
                }

                if (string.IsNullOrEmpty(expectedToken)
                    || !string.Equals(
                        File.ReadAllText(DirtyMarkerPath, Encoding.ASCII),
                        expectedToken,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                File.Delete(DirtyMarkerPath);
                return !File.Exists(DirtyMarkerPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The cache's only Unity dependencies: three AssetDatabase queries and the
        // console. Core cannot reference Unity, so the Bridge supplies them once per
        // domain, before its first call into this class.
        private static Func<uint> _artifactDependencyVersion;
        private static Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> _unityLabelSnapshot;
        private static Func<IReadOnlyDictionary<string, AssetTypeMetadata>> _authoritativeTypeSnapshot;
        private static Action<string> _logWarning;

        public static void BindUnity(
            Func<uint> artifactDependencyVersion,
            Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> unityLabelSnapshot,
            Func<IReadOnlyDictionary<string, AssetTypeMetadata>> authoritativeTypeSnapshot,
            Action<string> logWarning)
        {
            _artifactDependencyVersion = artifactDependencyVersion
                ?? throw new ArgumentNullException(nameof(artifactDependencyVersion));
            _unityLabelSnapshot = unityLabelSnapshot
                ?? throw new ArgumentNullException(nameof(unityLabelSnapshot));
            _authoritativeTypeSnapshot = authoritativeTypeSnapshot
                ?? throw new ArgumentNullException(nameof(authoritativeTypeSnapshot));
            _logWarning = logWarning
                ?? throw new ArgumentNullException(nameof(logWarning));
        }

        internal static uint CaptureArtifactDependencyVersion()
        {
            return ArtifactDependencyVersionOverride != null
                ? ArtifactDependencyVersionOverride()
                : RequireUnityBinding(_artifactDependencyVersion)();
        }

        internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureUnityLabelSnapshot()
        {
            return UnityLabelSnapshotOverride != null
                ? UnityLabelSnapshotOverride()
                : RequireUnityBinding(_unityLabelSnapshot)();
        }

        internal static IReadOnlyDictionary<string, AssetTypeMetadata> CaptureAuthoritativeTypeSnapshot()
        {
            return AuthoritativeTypeSnapshotOverride != null
                ? AuthoritativeTypeSnapshotOverride()
                : RequireUnityBinding(_authoritativeTypeSnapshot)();
        }

        private static void LogWarning(string message)
        {
            RequireUnityBinding(_logWarning)(message);
        }

        private static T RequireUnityBinding<T>(T binding) where T : class
        {
            return binding ?? throw new InvalidOperationException(
                "PersistentAssetIndexCache.BindUnity must run before the cache is used.");
        }
    }
}
