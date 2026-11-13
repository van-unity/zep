using System.IO;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    public static class AssetPathFormat
    {
        // Single owner of path normalization: AssetPath.From applies the
        // non-allocating slash/trim rule. The other normalize copies (here and in
        // ZepAddressableUtility) delegate here so the rule lives in one place.
        public static string Normalize(string assetPath)
        {
            return AssetPath.From(assetPath).Value;
        }

        public static string GetParentFolder(string assetPath)
        {
            var normalized = Normalize(assetPath);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            var lastSlashIndex = normalized.LastIndexOf('/');
            return lastSlashIndex <= 0 ? string.Empty : normalized.Substring(0, lastSlashIndex);
        }

        public static string GetParentFolderPath(string folderPath)
        {
            var normalized = Normalize(folderPath);
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                return string.Empty;
            }

            return normalized.Substring(0, lastSlash);
        }

        public static string GetFileName(string assetPath)
        {
            return Path.GetFileName(Normalize(assetPath));
        }

        internal static string GetNameWithoutExtension(string assetPath)
        {
            var normalized = Normalize(assetPath);
            return Path.GetFileNameWithoutExtension(normalized);
        }

        public static string GetExtension(string assetPath)
        {
            return Path.GetExtension(Normalize(assetPath));
        }

        internal static string Combine(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
            {
                return Normalize(right);
            }

            if (string.IsNullOrEmpty(right))
            {
                return Normalize(left);
            }

            return Normalize(left + "/" + right);
        }

        internal static string ChangeName(string assetPath, string newName, bool isFolder)
        {
            var parentFolder = GetParentFolder(assetPath);
            var extension = isFolder ? string.Empty : GetExtension(assetPath);
            return Combine(parentFolder, newName + extension);
        }
    }
}
