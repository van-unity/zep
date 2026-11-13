using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using System.Collections.Generic;
using System;

namespace Zep.Editor.TestInfrastructure
{
    // For fixtures whose subject needs a gateway to be constructed but never
    // calls it: every member refuses, so a test that starts depending on the
    // gateway fails loudly instead of passing against an accidental default.
    internal sealed class UnusedGateway : IProjectAssetGateway
    {
        public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default) => throw new NotSupportedException();
        public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) => throw new NotSupportedException();
        public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) => throw new NotSupportedException();
        public int TagVersion => 0;

        public bool Exists(AssetPath assetPath) => throw new NotSupportedException();

        public bool IsFolder(AssetPath assetPath) => throw new NotSupportedException();

        public bool CanWrite(AssetPath assetPath) => throw new NotSupportedException();

        public bool CanTag(AssetPath assetPath) => throw new NotSupportedException();

        public bool EnsureFolder(AssetPath folderPath) => throw new NotSupportedException();

        public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName) =>
            throw new NotSupportedException();

        public ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
            throw new NotSupportedException();

        public ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
            throw new NotSupportedException();

        public ProjectFileOperationResult Delete(AssetPath assetPath) => throw new NotSupportedException();

        public IReadOnlyList<string> GetTagIds(AssetPath assetPath) => throw new NotSupportedException();

        public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) =>
            throw new NotSupportedException();

        public ProjectFileOperationResult SetTagIds(
            IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath) =>
            throw new NotSupportedException();

        public bool Open(AssetPath assetPath) => throw new NotSupportedException();

        public void Reveal(AssetPath assetPath) => throw new NotSupportedException();

        public void BeginBatch() => throw new NotSupportedException();

        public void EndBatch() => throw new NotSupportedException();

        public string GetGuid(AssetPath assetPath) => throw new NotSupportedException();

        public string GetPathForGuid(string assetGuid) => throw new NotSupportedException();

        public string ResolveAbsolutePath(AssetPath assetPath) => throw new NotSupportedException();
    }
}
