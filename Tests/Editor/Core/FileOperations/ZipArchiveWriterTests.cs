using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Zep.Core.FileOperations;
using NUnit.Framework;

namespace Zep.Editor.Tests.Core.FileOperations
{
    public sealed class ZipArchiveWriterTests
    {
        private string _workspace;
        private List<string> _symbolicLinks;

        [SetUp]
        public void SetUp()
        {
            _workspace = Path.Combine(Path.GetTempPath(), "zep-zip-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            _symbolicLinks = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            // Unlink before the recursive delete: a link that points at its own
            // parent would otherwise make the cleanup walk the cycle too.
            for (var i = 0; i < _symbolicLinks.Count; i++)
            {
                TryUnlink(_symbolicLinks[i]);
            }

            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, true);
            }
        }

        [Test]
        public void Write_SingleFile_ProducesArchiveWithExpectedEntry()
        {
            var sourcePath = WriteFile("Foo.txt", "hello");
            var archivePath = Path.Combine(_workspace, "out.zip");

            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(sourcePath, "Foo.txt"),
            });

            Assert.That(File.Exists(archivePath), Is.True);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(1));
                Assert.That(archive.GetEntry("Foo.txt"), Is.Not.Null);
                Assert.That(ReadEntry(archive, "Foo.txt"), Is.EqualTo("hello"));
            }
        }

        [Test]
        public void Write_FolderSource_RecursesAndPreservesStructure()
        {
            var folder = Path.Combine(_workspace, "Bag");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
            var nested = Path.Combine(folder, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "b.txt"), "B");

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(folder, "Bag"),
            });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.GetEntry("Bag/"), Is.Not.Null);
                Assert.That(archive.GetEntry("Bag/nested/"), Is.Not.Null);
                Assert.That(ReadEntry(archive, "Bag/a.txt"), Is.EqualTo("A"));
                Assert.That(ReadEntry(archive, "Bag/nested/b.txt"), Is.EqualTo("B"));
            }
        }

        [Test]
        public void Write_MultipleSourcesIncludingCompanion_AllAppearAtArchiveRoot()
        {
            var asset = WriteFile("Image.png", "image-bytes");
            var meta = WriteFile("Image.png.meta", "guid: abc");
            var second = WriteFile("Audio.wav", "audio-bytes");

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(asset, "Image.png"),
                new ZipSourceItem(meta, "Image.png.meta"),
                new ZipSourceItem(second, "Audio.wav"),
            });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(3));
                Assert.That(archive.GetEntry("Image.png"), Is.Not.Null);
                Assert.That(archive.GetEntry("Image.png.meta"), Is.Not.Null);
                Assert.That(archive.GetEntry("Audio.wav"), Is.Not.Null);
            }
        }

        [Test]
        public void Write_MissingSource_ThrowsAndCleansUpPartialArchive()
        {
            var archivePath = Path.Combine(_workspace, "out.zip");
            var missing = Path.Combine(_workspace, "does-not-exist.txt");

            Assert.Throws<FileNotFoundException>(() =>
                ZipArchiveWriter.Write(archivePath, new[]
                {
                    new ZipSourceItem(missing, "ghost.txt"),
                }));

            Assert.That(File.Exists(archivePath), Is.False);
            Assert.That(Directory.GetFileSystemEntries(_workspace), Is.Empty);
        }

        [Test]
        public void Write_MissingSource_PreservesExistingArchiveAndCleansStage()
        {
            var archivePath = WriteFile("out.zip", "previous archive bytes");
            var originalBytes = File.ReadAllBytes(archivePath);

            Assert.Throws<FileNotFoundException>(() => ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(Path.Combine(_workspace, "missing.txt"), "missing.txt"),
            }));

            Assert.That(File.ReadAllBytes(archivePath), Is.EqualTo(originalBytes));
            Assert.That(Directory.GetFiles(_workspace), Is.EqualTo(new[] { archivePath }));
        }

        [Test]
        public void Write_SourceReadFailure_PreservesExistingArchiveAndCleansStage()
        {
            var archivePath = WriteFile("out.zip", "previous archive bytes");
            var originalBytes = File.ReadAllBytes(archivePath);
            var first = WriteFile("first.txt", "written before the read failure");
            var locked = WriteFile("locked.txt", "unreadable while exclusively open");

            using (var lockedStream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => ZipArchiveWriter.Write(archivePath, new[]
                {
                    new ZipSourceItem(first, "first.txt"),
                    new ZipSourceItem(locked, "locked.txt"),
                }));
            }

            Assert.That(File.ReadAllBytes(archivePath), Is.EqualTo(originalBytes));
            Assert.That(
                Directory.GetFiles(_workspace).OrderBy(path => path, StringComparer.Ordinal),
                Is.EqualTo(new[] { archivePath, first, locked }.OrderBy(path => path, StringComparer.Ordinal)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Write_OutputMatchesExplicitSource_IsRefusedWithoutChangingSource(bool useDotSegments)
        {
            var source = WriteFile("source.zip", "original selected source bytes");
            var originalBytes = File.ReadAllBytes(source);
            var output = useDotSegments
                ? Path.Combine(_workspace, "unused", "..", "source.zip")
                : source;

            Assert.Throws<ArgumentException>(() => ZipArchiveWriter.Write(output, new[]
            {
                new ZipSourceItem(source, "source.zip"),
            }));

            Assert.That(File.ReadAllBytes(source), Is.EqualTo(originalBytes));
            Assert.That(Directory.GetFileSystemEntries(_workspace), Is.EqualTo(new[] { source }),
                "an alias must be refused before any output directory or staging file is created");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Write_OutputInsideSourceFolder_ExcludesFinalAndStagingFiles(bool replaceExisting)
        {
            var folder = Path.Combine(_workspace, "Bag");
            var nested = Path.Combine(folder, "Nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
            File.WriteAllText(Path.Combine(nested, "b.txt"), "B");
            var archivePath = Path.Combine(nested, "out.zip");
            if (replaceExisting) File.WriteAllText(archivePath, "previous output");

            ZipArchiveWriter.Write(archivePath, new[] { new ZipSourceItem(folder, "Bag") });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(
                    archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal),
                    Is.EqualTo(new[] { "Bag/", "Bag/Nested/", "Bag/Nested/b.txt", "Bag/a.txt" }));
                Assert.That(ReadEntry(archive, "Bag/a.txt"), Is.EqualTo("A"));
                Assert.That(ReadEntry(archive, "Bag/Nested/b.txt"), Is.EqualTo("B"));
            }

            Assert.That(
                Directory.GetFiles(nested).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal),
                Is.EqualTo(new[] { "b.txt", "out.zip" }));
        }

        [Test]
        public void Write_ExistingDestination_IsReplacedWithCompletedArchive()
        {
            var archivePath = WriteFile("out.zip", "previous output");
            var source = WriteFile("payload.txt", "replacement payload");

            ZipArchiveWriter.Write(archivePath, new[] { new ZipSourceItem(source, "payload.txt") });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(1));
                Assert.That(ReadEntry(archive, "payload.txt"), Is.EqualTo("replacement payload"));
            }

            Assert.That(
                Directory.GetFiles(_workspace).OrderBy(path => path, StringComparer.Ordinal),
                Is.EqualTo(new[] { archivePath, source }.OrderBy(path => path, StringComparer.Ordinal)));
        }

        [Test]
        public void Write_EmptySources_Throws()
        {
            var archivePath = Path.Combine(_workspace, "out.zip");

            Assert.Throws<ArgumentException>(() =>
                ZipArchiveWriter.Write(archivePath, System.Array.Empty<ZipSourceItem>()));
        }

        [Test]
        public void Write_DuplicateEntryNames_DeduplicatedSilently()
        {
            var first = WriteFile("Foo.txt", "first");
            var second = WriteFile("Foo-other.txt", "second");

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(first, "Foo.txt"),
                new ZipSourceItem(second, "Foo.txt"),
            });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.Entries.Count, Is.EqualTo(1));
                Assert.That(ReadEntry(archive, "Foo.txt"), Is.EqualTo("first"));
            }
        }

        // 6.18: the recursion now reads each entry's type and link-ness from the
        // attributes the directory enumeration already carries, instead of a
        // GetAttributes plus a Directory.Exists per entry. The archive it produces
        // must be identical — every entry, and no .meta when they are excluded.
        [Test]
        public void Write_FolderSourceExcludingMetaFiles_ProducesExactlyTheSameEntrySet()
        {
            var folder = Path.Combine(_workspace, "Bag");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
            File.WriteAllText(Path.Combine(folder, "a.txt.meta"), "guid: a");
            var nested = Path.Combine(folder, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "b.txt"), "B");
            File.WriteAllText(Path.Combine(nested, "b.txt.meta"), "guid: b");
            Directory.CreateDirectory(Path.Combine(nested, "deeper"));
            File.WriteAllText(Path.Combine(nested, "deeper", "c.txt"), "C");

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(
                archivePath,
                new[] { new ZipSourceItem(folder, "Bag") },
                includeMetaFiles: false);

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(
                    archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal),
                    Is.EqualTo(new[]
                    {
                        "Bag/",
                        "Bag/a.txt",
                        "Bag/nested/",
                        "Bag/nested/b.txt",
                        "Bag/nested/deeper/",
                        "Bag/nested/deeper/c.txt",
                    }));
                Assert.That(ReadEntry(archive, "Bag/nested/deeper/c.txt"), Is.EqualTo("C"));
            }
        }

        // Folder recursion used to resolve links, so a link pointing outside the
        // compressed folder silently shipped foreign content inside the archive.
        [TestCase(false)]
        [TestCase(true)]
        public void Write_ExplicitLinkedSource_SkipsItAndItsTarget(bool isDirectory)
        {
            var selected = WriteFile("selected.txt", "selected content");
            var foreignFolder = Path.Combine(_workspace, "foreign");
            Directory.CreateDirectory(foreignFolder);
            var foreignFile = Path.Combine(foreignFolder, "foreign.txt");
            File.WriteAllText(foreignFile, "foreign content");
            var link = Path.Combine(_workspace, isDirectory ? "linked-folder" : "linked-file.txt");
            CreateSymbolicLink(link, isDirectory ? foreignFolder : foreignFile);

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(selected, "selected.txt"),
                new ZipSourceItem(link, Path.GetFileName(link)),
            });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.Entries.Select(entry => entry.FullName),
                    Is.EqualTo(new[] { "selected.txt" }));
                Assert.That(ReadEntry(archive, "selected.txt"), Is.EqualTo("selected content"));
            }

            Assert.That(File.GetAttributes(link) & FileAttributes.ReparsePoint, Is.Not.Zero);
            Assert.That(File.ReadAllText(foreignFile), Is.EqualTo("foreign content"));
        }

        [Test]
        public void Write_LinkedMetaCompanion_SkipsItAndKeepsTheAsset()
        {
            var selected = WriteFile("selected.txt", "selected content");
            var foreign = WriteFile("foreign.txt", "foreign content");
            var meta = selected + ".meta";
            CreateSymbolicLink(meta, foreign);

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(selected, "selected.txt"),
                new ZipSourceItem(meta, "selected.txt.meta"),
            });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(archive.Entries.Select(entry => entry.FullName),
                    Is.EqualTo(new[] { "selected.txt" }));
                Assert.That(ReadEntry(archive, "selected.txt"), Is.EqualTo("selected content"));
            }

            Assert.That(File.GetAttributes(meta) & FileAttributes.ReparsePoint, Is.Not.Zero);
            Assert.That(File.ReadAllText(foreign), Is.EqualTo("foreign content"));
        }

        [Test]
        public void Write_FolderContainingLinks_SkipsThemAndTheirTargets()
        {
            var folder = Path.Combine(_workspace, "Bag");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
            var outside = Path.Combine(_workspace, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "foreign.txt"), "F");
            CreateSymbolicLink(Path.Combine(folder, "linked-folder"), outside);
            CreateSymbolicLink(Path.Combine(folder, "linked-file.txt"), Path.Combine(outside, "foreign.txt"));

            var archivePath = Path.Combine(_workspace, "out.zip");
            ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(folder, "Bag"),
            });

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(
                    archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal),
                    Is.EqualTo(new[] { "Bag/", "Bag/a.txt" }));
            }
        }

        [Test]
        public void Write_FolderContainingALinkCycle_CompletesWithoutRecursing()
        {
            var folder = Path.Combine(_workspace, "Loop");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
            CreateSymbolicLink(Path.Combine(folder, "self"), folder);

            var archivePath = Path.Combine(_workspace, "out.zip");
            Assert.DoesNotThrow(() => ZipArchiveWriter.Write(archivePath, new[]
            {
                new ZipSourceItem(folder, "Loop"),
            }));

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Assert.That(
                    archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal),
                    Is.EqualTo(new[] { "Loop/", "Loop/a.txt" }));
            }
        }

        // Unity's editor scripting profile predates Directory.CreateSymbolicLink,
        // so the fixture shells out; Windows has no drop-in equivalent for the
        // unprivileged case, and the guard this covers is platform-independent.
        private void CreateSymbolicLink(string linkPath, string targetPath)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.Ignore("Symbolic-link fixtures are POSIX-only.");
            }

            var startInfo = new ProcessStartInfo(
                "/bin/ln",
                "-s \"" + targetPath + "\" \"" + linkPath + "\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(startInfo))
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero, "ln -s failed: " + error);
            }

            _symbolicLinks.Add(linkPath);
        }

        private static void TryUnlink(string linkPath)
        {
            try
            {
                File.Delete(linkPath);
            }
            catch (Exception)
            {
                // Best effort: the recursive delete below reports anything left.
            }
        }

        private string WriteFile(string name, string contents)
        {
            var path = Path.Combine(_workspace, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private static string ReadEntry(ZipArchive archive, string entryName)
        {
            var entry = archive.GetEntry(entryName);
            Assert.That(entry, Is.Not.Null, "missing entry: " + entryName);
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
