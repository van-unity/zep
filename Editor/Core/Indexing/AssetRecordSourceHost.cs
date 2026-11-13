using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// What the filesystem record sources need from the Unity editor: the facts
    /// only the AssetDatabase and the Package Manager hold, and the project's
    /// two tag stores. Implemented in the Bridge. The label and type snapshots,
    /// the type resolvers that take a snapshot, and the package roots are read
    /// from scan worker threads; everything else is main thread only.
    /// </summary>
    public interface IAssetRecordSourceEditorHost
    {
        /// <summary>Finishes an interrupted tag change; false leaves the tag stores untrusted.</summary>
        bool TryRecoverTagState(out string error);

        void PrepareInferredTypeMetadata();

        TagDefinitionStore TagDefinitions { get; }

        TagAssignmentStore TagAssignments { get; }

        IReadOnlyDictionary<string, IReadOnlyList<string>> BuildUnityLabelSnapshot();

        IReadOnlyDictionary<string, AssetTypeMetadata> BuildAuthoritativeTypeSnapshot();

        AssetTypeMetadata ResolveTypeMetadata(
            string assetPath,
            string guid,
            IReadOnlyDictionary<string, AssetTypeMetadata> authoritativeTypesByGuid);

        AssetTypeMetadata ResolveIncrementalTypeMetadata(string assetPath);

        AssetTypeMetadata ResolveMainThreadTypeMetadata(string assetPath);

        /// <summary>A folder record from the AssetDatabase's GUID; labels come from the snapshot when one is given.</summary>
        AssetFolderRecord BuildFolderRecord(
            string folderPath,
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null);

        /// <summary>The record the AssetDatabase-backed source builds, for paths the filesystem cannot answer.</summary>
        AssetRecord BuildRecordFromAssetDatabase(AssetPath assetPath);

        AssetFolderRecord BuildFolderRecordFromAssetDatabase(AssetPath folderPath);

        string AssetPathToGuid(string assetPath);

        bool IsValidFolder(string assetPath);

        string ToAbsolutePath(string assetPath);

        /// <summary>The current package mapping. Assigning replaces it; only tests do.</summary>
        RegisteredPackageRootSnapshot PackageRoots { get; set; }

        /// <summary>Samples the Package Manager again and returns the result.</summary>
        RegisteredPackageRootSnapshot RefreshPackageRoots();
    }

    /// <summary>The one editor host, bound by the Bridge once per domain.</summary>
    public static class AssetRecordSourceHost
    {
        private static IAssetRecordSourceEditorHost _current;

        public static void Bind(IAssetRecordSourceEditorHost host)
        {
            _current = host ?? throw new ArgumentNullException(nameof(host));
        }

        internal static IAssetRecordSourceEditorHost Current =>
            _current ?? throw new InvalidOperationException(
                "AssetRecordSourceHost.Bind must run before a record source is used.");
    }
}
