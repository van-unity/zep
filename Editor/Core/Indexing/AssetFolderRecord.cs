using System;
using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// The searchable Unity metadata that belongs to a project folder. Folder records
    /// stay separate from <see cref="AssetRecord"/> so folders never enter the file
    /// index while still retaining their stable Unity identity and labels.
    /// </summary>
    public sealed class AssetFolderRecord
    {
        public AssetFolderRecord(
            AssetPath folderPath,
            string guid = "",
            IReadOnlyList<string> unityLabels = null)
        {
            if (folderPath.IsEmpty)
            {
                throw new ArgumentException("Folder path must be provided.", nameof(folderPath));
            }

            FolderPath = folderPath;
            Guid = guid ?? string.Empty;
            UnityLabels = CopyNonEmpty(unityLabels);
        }

        internal AssetPath FolderPath { get; }

        // Synthetic roots and package folders without a readable .meta legitimately
        // have no GUID, so absence is represented by an empty string.
        internal string Guid { get; }

        internal IReadOnlyList<string> UnityLabels { get; }

        private static string[] CopyNonEmpty(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return Array.Empty<string>();
            }

            var count = 0;
            for (var i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    count++;
                }
            }

            if (count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[count];
            var writeIndex = 0;
            for (var i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    result[writeIndex++] = values[i].Trim();
                }
            }

            return result;
        }
    }
}
