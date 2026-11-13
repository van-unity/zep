using System;
using System.Collections.Generic;

namespace Zep.Core.FileOperations
{
    public static class AssetPathClassification
    {
        public static bool IsWritablePath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            return assetPath.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                || assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPackagesPath(string assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath)
                && (assetPath.Equals("Packages", StringComparison.OrdinalIgnoreCase)
                    || assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsProjectRootPath(string assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath)
                && (assetPath.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                    || assetPath.Equals("Packages", StringComparison.OrdinalIgnoreCase));
        }

        // "Packages/<name>" with nothing deeper. A package root is a
        // manifest-level entity: deleting it would physically relocate the
        // package source (external for file: packages) while the manifest still
        // points at it — removal belongs to the Package Manager.
        public static bool IsPackageRootPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)
                || !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return assetPath.IndexOf('/', "Packages/".Length) < 0
                && assetPath.Length > "Packages/".Length;
        }

        public static bool IsProjectAssetPath(string assetPath)
        {
            return IsWritablePath(assetPath) || IsPackagesPath(assetPath);
        }

        // A macOS plugin bundle and its relatives are directories on disk that Unity
        // imports as ONE asset: AssetDatabase.IsValidFolder is false for them, the
        // GUID lives in <name>.meta beside the directory, and nothing inside carries
        // a GUID of its own. Walks therefore record such a directory as a leaf asset
        // instead of descending into it. Suffix set verified against the AssetDatabase
        // on 6000.3.10f1 (all four: IsValidFolder=False, non-empty GUID, DefaultAsset).
        internal static bool IsUnitySingleAssetDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
            {
                return false;
            }

            return directoryName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                || directoryName.EndsWith(".framework", StringComparison.OrdinalIgnoreCase)
                || directoryName.EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase)
                || directoryName.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ContainsAssetPath(IReadOnlyList<string> assetPaths, string assetPath)
        {
            if (assetPaths == null) return false;
            for (var i = 0; i < assetPaths.Count; i++)
            {
                if (string.Equals(assetPaths[i], assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
