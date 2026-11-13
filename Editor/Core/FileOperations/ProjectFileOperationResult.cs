using System;
using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    public sealed class ProjectFileOperationResult
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyTagAssignments
            = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        private ProjectFileOperationResult(
            bool succeeded,
            string assetPath,
            string previousPath,
            string errorMessage,
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments,
            bool tagDefinitionsChanged,
            bool tagAssignmentSnapshotChanged = false)
        {
            Succeeded = succeeded;
            AssetPath = assetPath ?? string.Empty;
            PreviousPath = previousPath ?? string.Empty;
            ErrorMessage = errorMessage ?? string.Empty;
            TagAssignments = CopyTagAssignments(tagAssignments);
            TagDefinitionsChanged = tagDefinitionsChanged;
            TagAssignmentSnapshotChanged = tagAssignmentSnapshotChanged;
        }

        public bool Succeeded { get; }

        internal string AssetPath { get; }

        internal string PreviousPath { get; }

        public string ErrorMessage { get; }

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> TagAssignments { get; }

        internal bool HasTagAssignments => TagAssignments.Count > 0;

        internal bool TagDefinitionsChanged { get; }

        internal bool TagAssignmentSnapshotChanged { get; }

        internal ProjectFileOperationResult WithTagAssignments(IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments)
        {
            return new ProjectFileOperationResult(
                Succeeded,
                AssetPath,
                PreviousPath,
                ErrorMessage,
                tagAssignments,
                TagDefinitionsChanged,
                TagAssignmentSnapshotChanged);
        }

        internal ProjectFileOperationResult WithTagAssignmentSnapshotChanged()
        {
            return new ProjectFileOperationResult(
                Succeeded,
                AssetPath,
                PreviousPath,
                ErrorMessage,
                TagAssignments,
                TagDefinitionsChanged,
                true);
        }

        public static ProjectFileOperationResult Success(
            string assetPath,
            string previousPath,
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments = null,
            bool tagDefinitionsChanged = false)
        {
            return new ProjectFileOperationResult(
                true,
                assetPath,
                previousPath,
                string.Empty,
                tagAssignments,
                tagDefinitionsChanged);
        }

        public static ProjectFileOperationResult Failure(
            string errorMessage,
            string assetPath = "",
            string previousPath = "",
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments = null,
            bool tagDefinitionsChanged = false)
        {
            return new ProjectFileOperationResult(
                false,
                assetPath,
                previousPath,
                errorMessage,
                tagAssignments,
                tagDefinitionsChanged);
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> CopyTagAssignments(
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments)
        {
            if (tagAssignments == null || tagAssignments.Count == 0)
            {
                return EmptyTagAssignments;
            }

            var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tagAssignments)
            {
                copy[entry.Key ?? string.Empty] = CopyTagIds(entry.Value);
            }

            return copy;
        }

        private static IReadOnlyList<string> CopyTagIds(IReadOnlyList<string> tagIds)
        {
            if (tagIds == null || tagIds.Count == 0)
            {
                return Array.Empty<string>();
            }

            var copy = new string[tagIds.Count];
            for (var i = 0; i < tagIds.Count; i++)
            {
                copy[i] = tagIds[i];
            }

            return copy;
        }
    }
}
