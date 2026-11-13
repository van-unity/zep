using System;
using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.Indexing
{
    public readonly struct DirectFolderEntry
    {
        public DirectFolderEntry(
            AssetPath path,
            bool isFolder,
            string name,
            string inferredTypeName)
        {
            if (path.IsEmpty)
            {
                throw new ArgumentException("A direct-folder entry path is required.", nameof(path));
            }

            Path = path;
            IsFolder = isFolder;
            Name = name ?? string.Empty;
            InferredTypeName = isFolder ? "Folder" : inferredTypeName ?? string.Empty;
        }

        public AssetPath Path { get; }

        public bool IsFolder { get; }

        public string Name { get; }

        internal string InferredTypeName { get; }
    }

    public sealed class DirectFolderObservation
    {
        public DirectFolderObservation(
            AssetPath folderPath,
            IReadOnlyList<DirectFolderEntry> entries,
            bool isComplete)
        {
            if (folderPath.IsEmpty)
            {
                throw new ArgumentException("An observed folder path is required.", nameof(folderPath));
            }

            FolderPath = folderPath;
            Entries = entries ?? Array.Empty<DirectFolderEntry>();
            IsComplete = isComplete;
        }

        internal AssetPath FolderPath { get; }

        internal IReadOnlyList<DirectFolderEntry> Entries { get; }

        internal bool IsComplete { get; }
    }

    // Filesystem boundary for one bounded browse. Implementations enumerate only
    // direct children and return filesystem facts; they never manufacture Unity
    // GUIDs or authoritative importer metadata.
    public interface IDirectFolderAssetSource
    {
        DirectFolderObservation Observe(AssetPath folderPath);
    }

    public interface IAssetRecordSource
    {
        /// <summary>
        /// True when <see cref="GetRecords"/> can be safely invoked on a worker thread
        /// after <see cref="PrepareForEnumeration"/>. Incremental single-path builders
        /// run on the main thread and are not covered by this flag. The synchronizer uses
        /// it to decide whether the cold rebuild can run in the background while the
        /// editor stays interactive.
        /// </summary>
        bool IsThreadSafe { get; }

        /// <summary>
        /// Called on the main thread before a (possibly background) call to
        /// <see cref="GetRecords"/> so sources can warm any ScriptableSingleton or
        /// Unity-API-backed state they will read during enumeration. When supplied,
        /// <paramref name="unityLabelsByGuid"/> is the exact label snapshot already
        /// used by the persistent-cache signature; reusing it keeps both views atomic
        /// and avoids another Unity label-index pass. <paramref name="typesByGuid"/>
        /// carries authoritative concrete types and real base-class names for formats
        /// whose main object cannot be proven from the extension alone.
        /// Implementations may ignore either snapshot.
        /// </summary>
        void PrepareForEnumeration(
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null);

        IEnumerable<AssetRecord> GetRecords();

        /// <summary>
        /// Returns the folder snapshot produced by the most recent <see cref="GetRecords"/>
        /// call when one is waiting to be consumed; otherwise reads the current folders.
        /// Pairing both snapshots keeps one full index rebuild internally consistent while
        /// still allowing later incremental projection rebuilds to observe live changes.
        /// </summary>
        IReadOnlyList<AssetFolderRecord> GetFolderRecords();

        /// <summary>
        /// Build (or rebuild) a single <see cref="AssetRecord"/> for the given asset path.
        /// Returns null if the path is not a valid, indexable asset. Used by incremental
        /// update paths (AssetPostprocessor) that know exactly which paths changed and do
        /// not want to pay the cost of a full <see cref="GetRecords"/> sweep.
        /// </summary>
        AssetRecord BuildRecord(AssetPath assetPath);

        /// <summary>
        /// Builds the current searchable metadata for one folder, or returns null when
        /// the path is not a valid project folder. Used by incremental imports so label
        /// and GUID changes do not require a full index rebuild.
        /// </summary>
        AssetFolderRecord BuildFolderRecord(AssetPath folderPath);
    }
}
