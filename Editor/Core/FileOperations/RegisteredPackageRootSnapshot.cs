using System;
using System.Collections.Generic;
using System.IO;

namespace Zep.Core.FileOperations
{
    public readonly struct RegisteredPackageRoot
    {
        public RegisteredPackageRoot(
            string virtualPrefix,
            string physicalRoot,
            string displayName,
            bool isWritable = false)
        {
            VirtualPrefix = virtualPrefix ?? string.Empty;
            PhysicalRoot = physicalRoot ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            IsWritable = isWritable;
        }

        internal string VirtualPrefix { get; }

        internal string PhysicalRoot { get; }

        internal string DisplayName { get; }

        // Embedded and Local (file:) packages hold the user's own editable
        // files; Registry, Git and built-in packages are a shared cache. Sampled
        // with the root so CanWrite is a prefix lookup instead of a
        // PackageInfo.FindForAssetPath call per asked path.
        public bool IsWritable { get; }
    }

    // Immutable pure-data package mapping. Package Manager is sampled once on
    // the main thread; filesystem workers and every path consumer read this
    // snapshot and never infer project membership from Library/PackageCache.
    public sealed class RegisteredPackageRootSnapshot
    {
        public static readonly RegisteredPackageRootSnapshot IncompleteEmpty =
            new RegisteredPackageRootSnapshot(
                Array.Empty<RegisteredPackageRoot>(),
                isComplete: false);

        private readonly RegisteredPackageRoot[] _roots;

        public RegisteredPackageRootSnapshot(
            IReadOnlyList<RegisteredPackageRoot> roots,
            bool isComplete)
        {
            _roots = roots == null
                ? Array.Empty<RegisteredPackageRoot>()
                : Copy(roots);
            IsComplete = isComplete;
        }

        internal IReadOnlyList<RegisteredPackageRoot> Roots => _roots;

        public bool IsComplete { get; }

        // The registered package whose virtual prefix covers assetPath — the
        // package itself ("Packages/com.x") or anything below it.
        public bool TryFindRoot(string assetPath, out RegisteredPackageRoot root)
        {
            root = default;
            if (string.IsNullOrEmpty(assetPath)
                || !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var i = 0; i < _roots.Length; i++)
            {
                var candidate = _roots[i];
                if (string.Equals(
                        assetPath,
                        candidate.VirtualPrefix,
                        StringComparison.OrdinalIgnoreCase)
                    || (assetPath.Length > candidate.VirtualPrefix.Length
                        && assetPath[candidate.VirtualPrefix.Length] == '/'
                        && assetPath.StartsWith(
                            candidate.VirtualPrefix,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    root = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool TryResolvePhysicalPath(
            string assetPath,
            out string physicalPath)
        {
            physicalPath = string.Empty;
            if (!TryFindRoot(assetPath, out var root))
            {
                return false;
            }

            physicalPath = assetPath.Length == root.VirtualPrefix.Length
                ? root.PhysicalRoot
                : Path.Combine(
                    root.PhysicalRoot,
                    assetPath.Substring(root.VirtualPrefix.Length + 1));
            return true;
        }

        private static RegisteredPackageRoot[] Copy(
            IReadOnlyList<RegisteredPackageRoot> roots)
        {
            var copy = new RegisteredPackageRoot[roots.Count];
            for (var i = 0; i < roots.Count; i++)
            {
                copy[i] = roots[i];
            }

            return copy;
        }
    }
}
