using System;
using System.Collections.Generic;
using System.IO;

namespace Zep.Core.FileOperations
{
    internal static class FilesystemEntryOperations
    {
        internal static bool EntryExists(string fullPath, bool isDirectory)
        {
            return isDirectory ? Directory.Exists(fullPath) : File.Exists(fullPath);
        }

        public static void CopyDirectoryRecursive(string sourceFullPath, string targetFullPath)
        {
            Directory.CreateDirectory(targetFullPath);
            foreach (var file in Directory.GetFiles(sourceFullPath))
            {
                ThrowIfReparsePoint(file);
                if (ShouldIgnoreUnityImportEntry(file, isDirectory: false))
                {
                    continue;
                }

                var fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(targetFullPath, fileName), false);
            }

            foreach (var directory in Directory.GetDirectories(sourceFullPath))
            {
                ThrowIfReparsePoint(directory);
                if (ShouldIgnoreUnityImportEntry(directory, isDirectory: true))
                {
                    continue;
                }

                var directoryName = new DirectoryInfo(directory).Name;
                CopyDirectoryRecursive(directory, Path.Combine(targetFullPath, directoryName));
            }
        }

        public static void ValidateImportTree(string sourceFullPath, bool isDirectory)
        {
            ThrowIfReparsePoint(sourceFullPath);
            if (ShouldIgnoreUnityImportEntry(sourceFullPath, isDirectory))
            {
                throw new IOException("The top-level entry is ignored by Unity's asset importer.");
            }

            if (!isDirectory)
            {
                return;
            }

            var pending = new Stack<string>();
            pending.Push(sourceFullPath);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var file in Directory.GetFiles(directory))
                {
                    ThrowIfReparsePoint(file);
                }

                foreach (var childDirectory in Directory.GetDirectories(directory))
                {
                    ThrowIfReparsePoint(childDirectory);
                    if (!ShouldIgnoreUnityImportEntry(childDirectory, isDirectory: true))
                    {
                        pending.Push(childDirectory);
                    }
                }
            }
        }

        public static bool ShouldIgnoreUnityImportEntry(
            string fullPath,
            bool isDirectory)
        {
            var name = Path.GetFileName(fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Hidden) != 0
                || name.StartsWith(".", StringComparison.Ordinal)
                || name.EndsWith("~", StringComparison.Ordinal)
                || string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !isDirectory
                && (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
        }

        // Walks that only READ a tree (projection, archiving) skip links instead
        // of rejecting the whole operation: following one embeds content from
        // outside the walked tree, and a link that points at an ancestor makes
        // the walk recurse until the OS refuses the path. An entry whose
        // attributes cannot be read is reported as a link, because skipping is
        // the safe answer for every caller of this helper.
        internal static bool IsReparsePoint(string fullPath)
        {
            try
            {
                return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void ThrowIfReparsePoint(string fullPath)
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Symbolic links and reparse points are not accepted: " + fullPath);
            }
        }
    }
}
