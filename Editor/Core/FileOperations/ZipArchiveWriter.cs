using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Zep.Core.FileOperations
{
    internal readonly struct ZipSourceItem
    {
        public ZipSourceItem(string absolutePath, string entryName)
        {
            AbsolutePath = absolutePath ?? string.Empty;
            EntryName = NormalizeEntryName(entryName);
        }

        public string AbsolutePath { get; }

        public string EntryName { get; }

        private static string NormalizeEntryName(string entryName)
        {
            if (string.IsNullOrEmpty(entryName))
            {
                return string.Empty;
            }

            return entryName.Replace('\\', '/').TrimStart('/');
        }
    }

    // Pure C# zip writer. Knows nothing about Unity, AssetDatabase, or asset
    // paths — callers resolve everything to absolute file system paths and the
    // entry name they want inside the archive. Each source may be a file or a
    // folder; folders are recursed and every nested file is included.
    //
    // The one asset-shaped concession is <paramref name="includeMetaFiles"/>:
    // when false, files ending in <c>.meta</c> are dropped from both explicit
    // sources and recursed folder contents. This lives here because folder
    // recursion happens inside the writer, so it is the only place that can
    // enforce "no .meta anywhere in the archive" — the alternative is making
    // every caller pre-walk folders, duplicating this recursion.
    internal static class ZipArchiveWriter
    {
        public static void Write(
            string archiveAbsolutePath,
            IReadOnlyList<ZipSourceItem> sources,
            bool includeMetaFiles = true)
        {
            if (string.IsNullOrEmpty(archiveAbsolutePath))
            {
                throw new ArgumentException("Archive destination path is required.", nameof(archiveAbsolutePath));
            }

            if (sources == null || sources.Count == 0)
            {
                throw new ArgumentException("At least one source is required.", nameof(sources));
            }

            var archiveFullPath = Path.GetFullPath(archiveAbsolutePath);
            for (var i = 0; i < sources.Count; i++)
            {
                if (!string.IsNullOrEmpty(sources[i].AbsolutePath)
                    && string.Equals(
                        Path.GetFullPath(sources[i].AbsolutePath),
                        archiveFullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Archive destination cannot also be a source.", nameof(archiveAbsolutePath));
                }
            }

            var archiveParent = Path.GetDirectoryName(archiveFullPath);
            Directory.CreateDirectory(archiveParent);
            var temporaryArchivePath = Path.Combine(
                archiveParent,
                "." + Path.GetFileName(archiveFullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                archiveFullPath,
                temporaryArchivePath,
            };
            var ownsTemporaryArchive = false;
            try
            {
                using (var stream = new FileStream(temporaryArchivePath, FileMode.CreateNew, FileAccess.Write))
                {
                    ownsTemporaryArchive = true;
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 0; i < sources.Count; i++)
                        {
                            WriteSource(archive, sources[i], seenEntries, includeMetaFiles, excludedPaths);
                        }
                    }
                }

                // The archive must close successfully before the previous output
                // is replaced. A sibling stage keeps the final move on one volume.
                if (File.Exists(archiveFullPath))
                {
                    File.Replace(temporaryArchivePath, archiveFullPath, null);
                }
                else
                {
                    File.Move(temporaryArchivePath, archiveFullPath);
                }
            }
            catch
            {
                if (ownsTemporaryArchive && File.Exists(temporaryArchivePath))
                {
                    try
                    {
                        File.Delete(temporaryArchivePath);
                    }
                    catch
                    {
                        // Best-effort cleanup of the partial archive — surface the original exception.
                    }
                }

                throw;
            }
        }

        private static void WriteSource(
            ZipArchive archive,
            ZipSourceItem source,
            HashSet<string> seenEntries,
            bool includeMetaFiles,
            HashSet<string> excludedPaths)
        {
            if (string.IsNullOrEmpty(source.AbsolutePath) || string.IsNullOrEmpty(source.EntryName))
            {
                throw new ArgumentException("Each source must have a path and an entry name.");
            }

            if (Directory.Exists(source.AbsolutePath))
            {
                if (FilesystemEntryOperations.IsReparsePoint(source.AbsolutePath))
                {
                    return;
                }

                AddFolder(archive, source.AbsolutePath, source.EntryName, seenEntries, includeMetaFiles, excludedPaths);
                return;
            }

            if (File.Exists(source.AbsolutePath))
            {
                // Explicit selections and companion metadata obey the same link
                // policy as entries discovered by folder recursion.
                if (FilesystemEntryOperations.IsReparsePoint(source.AbsolutePath))
                {
                    return;
                }

                if (!includeMetaFiles && IsMetaFile(source.AbsolutePath))
                {
                    return;
                }

                AddFile(archive, source.AbsolutePath, source.EntryName, seenEntries);
                return;
            }

            throw new FileNotFoundException("Source path does not exist.", source.AbsolutePath);
        }

        private static void AddFolder(
            ZipArchive archive,
            string folderAbsolute,
            string entryRoot,
            HashSet<string> seenEntries,
            bool includeMetaFiles,
            HashSet<string> excludedPaths)
        {
            AddDirectoryEntry(archive, entryRoot, seenEntries);

            // Recursed one level at a time rather than with SearchOption.AllDirectories
            // because that overload resolves directory links: a link pointing outside
            // the folder silently embeds foreign content in the archive, and one
            // pointing at an ancestor duplicates the subtree at absurd depths until
            // the OS refuses the path. Skipping links matches the import walks.
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(folderAbsolute);
            while (pendingDirectories.Count > 0)
            {
                var directory = new DirectoryInfo(pendingDirectories.Pop());
                foreach (var info in directory.EnumerateFileSystemInfos())
                {
                    if (excludedPaths.Contains(info.FullName))
                    {
                        continue;
                    }

                    // The enumeration already carries each entry's attributes, so
                    // "is it a link" and "is it a directory" are answered from that
                    // one read instead of a File.GetAttributes plus a
                    // Directory.Exists per entry. An entry whose attributes cannot
                    // be read is skipped, exactly as the shared reparse probe does.
                    FileAttributes attributes;
                    try
                    {
                        attributes = info.Attributes;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var path = info.FullName;
                    var relative = Path.GetRelativePath(folderAbsolute, path).Replace('\\', '/');
                    var entryName = entryRoot + "/" + relative;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        AddDirectoryEntry(archive, entryName, seenEntries);
                        pendingDirectories.Push(path);
                    }
                    else if (includeMetaFiles || !IsMetaFile(path))
                    {
                        AddFile(archive, path, entryName, seenEntries);
                    }
                }
            }
        }

        private static bool IsMetaFile(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddFile(ZipArchive archive, string absolutePath, string entryName, HashSet<string> seenEntries)
        {
            if (!seenEntries.Add(entryName))
            {
                return;
            }

            archive.CreateEntryFromFile(absolutePath, entryName, CompressionLevel.Optimal);
        }

        private static void AddDirectoryEntry(ZipArchive archive, string entryName, HashSet<string> seenEntries)
        {
            var withSlash = entryName.EndsWith("/", StringComparison.Ordinal) ? entryName : entryName + "/";
            if (!seenEntries.Add(withSlash))
            {
                return;
            }

            archive.CreateEntry(withSlash);
        }
    }
}
