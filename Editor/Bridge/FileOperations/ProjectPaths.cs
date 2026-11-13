using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace Zep.Editor.Bridge.FileOperations
{
    internal static class RegisteredPackageRoots
    {
        private static RegisteredPackageRootSnapshot _current =
            RegisteredPackageRootSnapshot.IncompleteEmpty;
        // The package list _current was built from. Every projectChanged — i.e.
        // every import batch — called Refresh, which stat-ed 40-90 package roots
        // (GetFullPath + Directory.Exists) to rebuild an identical snapshot
        // (audit 4.9). Package topology changes only when the manifest resolves,
        // so the list itself is the freshness test. Null means "no snapshot of
        // ours to reuse": the initial state, and whatever SetForTests installed.
        private static string[] _currentSignature;
        // Set by Package Manager's registration event (and by a domain reload,
        // through the static initializer). Refresh() used to call
        // PackageInfo.GetAllRegisteredPackages on every projectChanged — every
        // import batch — to discover that nothing had changed; the event is the
        // only thing that can change the answer, so it is what re-arms the query.
        private static bool _registrationChanged = true;

        // Raised on the main thread after a registration event rebuilt the
        // snapshot; the controller host maps it to a project reconciliation so
        // a package added without a domain reload shows up without waiting for
        // an unrelated import.
        internal static event Action Changed;

        internal static int PackageManagerQueriesForTests { get; private set; }

        [UnityEditor.InitializeOnLoadMethod]
        private static void SubscribeToRegistrationEvents()
        {
            UnityEditor.PackageManager.Events.registeredPackages += _ =>
            {
                _registrationChanged = true;
                var before = _current;
                if (!ReferenceEquals(Refresh(), before))
                {
                    Changed?.Invoke();
                }
            };
        }

        internal static void MarkRegistrationChangedForTests()
        {
            _registrationChanged = true;
        }

        internal static RegisteredPackageRootSnapshot Current => _current;

        // Test seam for the Package Manager boundary. Production continues to
        // call Unity directly.
        internal static Func<PackageInfo[]> PackageQueryOverrideForTests;

        internal static RegisteredPackageRootSnapshot Refresh()
        {
            if (_currentSignature != null && !_registrationChanged)
            {
                return _current;
            }

            PackageManagerQueriesForTests++;
            PackageInfo[] packages;
            try
            {
                packages = PackageQueryOverrideForTests != null
                    ? PackageQueryOverrideForTests()
                    : PackageInfo.GetAllRegisteredPackages();
            }
            catch
            {
                // Package Manager can be unavailable during startup/reload. Keep
                // the last valid snapshot; an initial empty snapshot remains
                // explicitly incomplete so callers never publish package absence.
                return _current;
            }

            if (packages == null)
            {
                return _current;
            }

            var signature = BuildSignature(packages);
            if (_currentSignature != null && SignaturesMatch(_currentSignature, signature))
            {
                // The event fired but the list is byte-identical: the query has
                // been answered, so the next Refresh must not ask again.
                _registrationChanged = false;
                return _current;
            }

            var roots = new List<RegisteredPackageRoot>(packages.Length);
            var displayNames = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < packages.Length; i++)
            {
                var package = packages[i];
                if (package == null || package.source == PackageSource.BuiltIn)
                {
                    continue;
                }

                var virtualPrefix = AssetPathFormat.Normalize(package.assetPath);
                if (string.IsNullOrEmpty(virtualPrefix)
                    || string.IsNullOrWhiteSpace(package.resolvedPath))
                {
                    continue;
                }

                var physicalRoot = Path.GetFullPath(package.resolvedPath);
                if (string.IsNullOrEmpty(physicalRoot)
                    || !Directory.Exists(physicalRoot)
                    || !seen.Add(virtualPrefix))
                {
                    continue;
                }

                var displayName = string.IsNullOrEmpty(package.displayName)
                    ? package.name
                    : package.displayName;
                roots.Add(new RegisteredPackageRoot(
                    virtualPrefix,
                    physicalRoot,
                    displayName,
                    isWritable: package.source == PackageSource.Embedded
                        || package.source == PackageSource.Local));
                displayNames[virtualPrefix] = displayName;
            }

            var next = new RegisteredPackageRootSnapshot(roots, isComplete: true);
            PackageDisplayNames.Set(displayNames);
            _current = next;
            // Assigned only here, after a snapshot was actually built from this
            // list, so a Package Manager failure can never mark a stale snapshot
            // fresh.
            _currentSignature = signature;
            _registrationChanged = false;
            return next;
        }

        // Everything a snapshot is derived from: the virtual prefix, the physical
        // root, and the display name published to PackageDisplayNames. Sorted, so
        // a reordering of the same packages is correctly seen as no change.
        private static string[] BuildSignature(PackageInfo[] packages)
        {
            var signature = new string[packages.Length];
            for (var i = 0; i < packages.Length; i++)
            {
                var package = packages[i];
                signature[i] = package == null
                    ? string.Empty
                    : string.Concat(
                        package.name, "\u0000",
                        package.assetPath, "\u0000",
                        package.resolvedPath, "\u0000",
                        package.displayName, "\u0000",
                        package.source.ToString());
            }

            Array.Sort(signature, StringComparer.Ordinal);
            return signature;
        }

        private static bool SignaturesMatch(string[] left, string[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal static void SetForTests(RegisteredPackageRootSnapshot snapshot)
        {
            _current = snapshot ?? RegisteredPackageRootSnapshot.IncompleteEmpty;
            // The installed snapshot was not built from any package list, so the
            // reuse check must not hand it back as if it had been.
            _currentSignature = null;
        }
    }

    // Single owner of asset-path -> physical-path resolution. Package assets do
    // not live under "<projectRoot>/Packages/..." on disk: local (file:) and
    // cached registry packages resolve elsewhere, so a naive project-relative
    // join points at a path that does not exist.
    public static class ProjectPaths
    {
        // Both uses below must agree: one decides whether the path is a package
        // path at all, the other strips the "Packages/<name>" head off it.
        private const string PackagesPathPrefix = "Packages/";

        public static string ToAbsolutePath(string assetPath)
        {
            var normalizedPath = AssetPathFormat.Normalize(assetPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            if (!normalizedPath.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(GetProjectRootAbsolutePath(), normalizedPath);
            }

            var snapshot = RegisteredPackageRoots.Current.IsComplete
                ? RegisteredPackageRoots.Current
                : RegisteredPackageRoots.Refresh();
            if (!snapshot.TryResolvePhysicalPath(normalizedPath, out var physicalPath))
            {
                return Path.Combine(GetProjectRootAbsolutePath(), normalizedPath);
            }

            return physicalPath;
        }

        // Memoised for the editor session. Application.dataPath is fixed once
        // Unity has opened a project — changing it means a different editor
        // process — so the join and the full-path normalisation below can only
        // ever produce one answer. They were being redone on every asset-path
        // resolution: a bulk move of 5,000 items derived the same root 10,000
        // times before the first file was touched.
        private static string _projectRootAbsolutePath;

        public static string GetProjectRootAbsolutePath()
        {
            return _projectRootAbsolutePath
                ?? (_projectRootAbsolutePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
        }
    }
}
