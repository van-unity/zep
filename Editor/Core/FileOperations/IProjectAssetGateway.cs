using System.Collections.Generic;
using System;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    public sealed class ProjectTagStateUnavailableException : InvalidOperationException
    {
        public ProjectTagStateUnavailableException(string message)
            : base(message)
        {
        }
    }

    public interface IProjectAssetGateway
    {
        bool Exists(AssetPath assetPath);

        bool IsFolder(AssetPath assetPath);

        bool CanWrite(AssetPath assetPath);

        bool CanTag(AssetPath assetPath);

        // Throws ProjectTagStateUnavailableException when a persisted tag
        // transaction cannot be safely recovered before polling assignments.
        int TagVersion { get; }

        bool EnsureFolder(AssetPath folderPath);

        ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName);

        ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath);

        ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath);

        ProjectFileOperationResult Delete(AssetPath assetPath);

        // Private command-history payloads preserve current bytes and Unity identity.
        // Storage slots are opaque and may only be used by the command that owns them.
        ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default);

        ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath);

        ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath);

        // Throws ProjectTagStateUnavailableException when a persisted tag
        // transaction cannot be safely recovered before resolving an identity.
        IReadOnlyList<string> GetTagIds(AssetPath assetPath);

        // Same lookup for a caller that already holds the asset's GUID
        // (AssetRecord.Guid, ZepFolderEntry.Guid). Assignments are
        // stored per GUID, and resolving one from a path reads the sibling
        // .meta off disk — a projection rebuild would pay that once per asset.
        // An empty knownGuid falls through to the path-resolving overload:
        // synthetic folder entries carry no GUID and must still report tags.
        // Throws ProjectTagStateUnavailableException when persisted tag state
        // cannot be safely recovered.
        IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid);

        ProjectFileOperationResult SetTagIds(IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath);

        bool Open(AssetPath assetPath);

        void Reveal(AssetPath assetPath);

        void BeginBatch();

        void EndBatch();

        // Returns the GUID for a project path or a private undo payload path, or
        // empty if there is none. Bridge uses AssetDatabase first and the sibling
        // .meta file as the source of truth while imports are batched/deferred.
        string GetGuid(AssetPath assetPath);

        // Reverse of GetGuid: the asset's CURRENT path for a GUID, or empty when
        // the GUID is unknown. Commands whose target is identified by GUID rather
        // than by location (tag assignments are stored per GUID) use this to
        // follow an asset that was moved or renamed outside the panel.
        string GetPathForGuid(string assetGuid);

        // Resolves the asset path to its absolute on-disk location. Bridge handles
        // both Assets/* (project root + path) and Packages/* (PackageManager-resolved
        // package root + relative path). Returns null/empty when the path cannot be
        // mapped.
        string ResolveAbsolutePath(AssetPath assetPath);
    }

    // Tag definitions live in project settings rather than in asset files, but
    // they still participate in the same persisted command timeline. Keeping
    // this as a narrow capability avoids forcing index-only gateway fakes to
    // implement a settings boundary they never use.
    public interface IProjectTagDefinitionGateway
    {
        ProjectTagStateSnapshot CaptureTagState();

        ProjectFileOperationResult CompareAndSetTagState(
            ProjectTagStateSnapshot expected,
            ProjectTagStateSnapshot desired);
    }

    public interface IProjectTagAssignmentPreparationGateway
    {
        ProjectFileOperationResult PrepareTagAssignmentMutation();
    }

    public sealed class ProjectTagStateSnapshot
    {
        private readonly ZepTag[] _definitions;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _assignmentsByGuid;

        internal ProjectTagStateSnapshot(
            IReadOnlyList<ZepTag> definitions,
            IReadOnlyDictionary<string, IReadOnlyList<string>> assignmentsByGuid)
        {
            _definitions = CopyDefinitions(definitions);
            _assignmentsByGuid = CopyAssignments(assignmentsByGuid);
        }

        internal IReadOnlyList<ZepTag> Definitions => _definitions;

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> AssignmentsByGuid =>
            _assignmentsByGuid;

        internal static bool ContentEquals(
            ProjectTagStateSnapshot left,
            ProjectTagStateSnapshot right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (!DefinitionsEqual(left.Definitions, right.Definitions)) return false;
            if (left.AssignmentsByGuid.Count != right.AssignmentsByGuid.Count) return false;

            foreach (var entry in left.AssignmentsByGuid)
            {
                if (!right.AssignmentsByGuid.TryGetValue(entry.Key, out var rightTagIds)
                    || !TagIdsEqual(entry.Value, rightTagIds))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool DefinitionsEqual(
            IReadOnlyList<ZepTag> left,
            IReadOnlyList<ZepTag> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            for (var i = 0; i < leftCount; i++)
            {
                if (!left[i].Equals(right[i])) return false;
            }

            return true;
        }

        private static ZepTag[] CopyDefinitions(IReadOnlyList<ZepTag> definitions)
        {
            if (definitions == null || definitions.Count == 0)
            {
                return Array.Empty<ZepTag>();
            }

            var copy = new ZepTag[definitions.Count];
            for (var i = 0; i < definitions.Count; i++)
            {
                copy[i] = definitions[i];
            }

            return copy;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> CopyAssignments(
            IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (assignments == null) return copy;

            foreach (var entry in assignments)
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                var tagIds = entry.Value;
                if (tagIds == null || tagIds.Count == 0) continue;
                var tagIdCopy = new string[tagIds.Count];
                for (var i = 0; i < tagIds.Count; i++)
                {
                    tagIdCopy[i] = tagIds[i] ?? string.Empty;
                }

                copy[entry.Key] = tagIdCopy;
            }

            return copy;
        }

        private static bool TagIdsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            for (var i = 0; i < leftCount; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
