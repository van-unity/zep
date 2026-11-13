using System;
using Zep.Core.FileOperations;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Zep.Core.Application;
using Zep.Editor.Bridge.FileOperations;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Zep.Tests.Editor.Bridge.FileOperations
{
    // The destination resolution both external-import paths share. A Local
    // (file:) package's files are NOT under "<projectRoot>/Packages/<name>" on
    // disk, so the physical destination of a drop into one has to come from the
    // package resolver, never from a literal project-root join.
    public sealed class ProjectPathsPackageDestinationTests
    {
        [Test]
        public void ToAbsolutePath_InsideALocalPackage_ResolvesToThePackagesRealLocation()
        {
            // This package is itself the fixture: the test rig references it as a
            // "file:" dependency, which is exactly the shape under test.
            var package = PackageInfo.FindForAssembly(typeof(ProjectPaths).Assembly);
            Assert.That(package, Is.Not.Null, "the package under test must be resolvable");

            var projectRoot = ProjectPaths.GetProjectRootAbsolutePath().Replace('\\', '/');
            var resolved = package.resolvedPath.Replace('\\', '/');
            if (resolved.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                Assert.Ignore("package is embedded under the project root; nothing to distinguish here");
            }

            // The virtual form the panel hands the importers.
            var folderAssetPath = "Packages/" + package.name + "/Editor";
            var absolute = ProjectPaths.ToAbsolutePath(folderAssetPath).Replace('\\', '/');

            Assert.That(absolute, Is.EqualTo(resolved + "/Editor"));
            Assert.That(Directory.Exists(absolute), Is.True);
            Assert.That(
                absolute,
                Is.Not.EqualTo(projectRoot + "/" + folderAssetPath),
                "a Local package resolves outside the project, so the literal join is a different path");
        }
    }

    // Classification behind dropped-package routing. Copying and the actual
    // AssetDatabase.ImportPackage call are Unity boundaries exercised in-editor;
    // the extension rule that decides "import package vs copy in" is pinned here.
    public sealed class ExternalAssetImporterTests
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int symlink(string target, string linkPath);

        private string _externalRoot;
        private string _destinationAssetPath;

        [SetUp]
        public void SetUp()
        {
            _externalRoot = Path.Combine(
                Path.GetTempPath(),
                "ZepExternalImportTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_externalRoot);
            _destinationAssetPath = "Assets/ZepExternalImportTests-"
                + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_destinationAssetPath));
        }

        [TearDown]
        public void TearDown()
        {
            ExternalAssetImporter.CheckpointForTests = null;
            ExternalAssetImporter.ExistenceProbeForTests = null;
            ZepPerfLog.IsEnabled = false;
            ZepPerfLog.Reset();
            AssetDatabase.DeleteAsset(_destinationAssetPath);
            if (Directory.Exists(_externalRoot))
            {
                Directory.Delete(_externalRoot, recursive: true);
            }
        }

        [TestCase("/Users/x/Downloads/Foo.unitypackage")]
        [TestCase("/Users/x/Downloads/Foo.UNITYPACKAGE")]
        [TestCase("/Users/x/Downloads/Foo.UnityPackage")]
        [TestCase("C:/Assets/pack.unitypackage")]
        [TestCase("pack.unitypackage")]
        public void IsUnityPackage_MatchesPackageFilesRegardlessOfCase(string path)
        {
            Assert.That(ExternalAssetImporter.IsUnityPackage(path), Is.True);
        }

        [TestCase("/Users/x/Downloads/image.png")]
        [TestCase("/Users/x/Downloads/Foo.unitypackage.bak")]
        [TestCase("/Users/x/Downloads/Foo.unitypackage.zip")]
        [TestCase("/Users/x/some.unitypackage/child.png")]
        [TestCase("/Users/x/folder")]
        [TestCase("")]
        [TestCase(null)]
        public void IsUnityPackage_RejectsNonPackagePaths(string path)
        {
            Assert.That(ExternalAssetImporter.IsUnityPackage(path), Is.False);
        }

        [Test]
        public void Import_Directory_StagesBeforeOneTargetedImportAndAppliesIgnorePolicy()
        {
            var payload = Path.Combine(_externalRoot, "Payload");
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(Path.Combine(payload, "Nested"));
            Directory.CreateDirectory(Path.Combine(payload, "cvs"));
            Directory.CreateDirectory(Path.Combine(payload, "Ignored~"));
            File.WriteAllText(Path.Combine(payload, "Keep.txt"), "keep");
            File.WriteAllText(Path.Combine(payload, "Nested", "KeepNested.txt"), "keep");
            File.WriteAllText(Path.Combine(payload, ".Dot.txt"), "ignored");
            File.WriteAllText(Path.Combine(payload, "Temp.tmp"), "ignored");
            File.WriteAllText(Path.Combine(payload, "Keep.txt.meta"), "source sidecar");
            File.WriteAllText(Path.Combine(payload, "cvs", "Ignored.txt"), "ignored");
            File.WriteAllText(Path.Combine(payload, "Ignored~", "Ignored.txt"), "ignored");
            var hidden = Path.Combine(payload, "AttributeHidden.txt");
            File.WriteAllText(hidden, "ignored");
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();
            var result = ExternalAssetImporter.Import(
                new[] { payload },
                _destinationAssetPath);
            var snapshot = ZepPerfLog.CaptureSnapshot();

            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.ImportedAssetPaths.Count, Is.EqualTo(1));
            var importedFullPath = ProjectPaths.ToAbsolutePath(result.ImportedAssetPaths[0]);
            Assert.That(File.Exists(Path.Combine(importedFullPath, "Keep.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(importedFullPath, "Nested", "KeepNested.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(importedFullPath, ".Dot.txt")), Is.False);
            Assert.That(File.Exists(Path.Combine(importedFullPath, "Temp.tmp")), Is.False);
            Assert.That(
                File.ReadAllText(Path.Combine(importedFullPath, "Keep.txt.meta")),
                Does.Not.Contain("source sidecar"),
                "Unity may generate a new sidecar, but the source sidecar must never be copied");
            Assert.That(Directory.Exists(Path.Combine(importedFullPath, "cvs")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(importedFullPath, "Ignored~")), Is.False);
            Assert.That(File.Exists(Path.Combine(importedFullPath, "AttributeHidden.txt")), Is.False);
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseStartAssetEditingCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseStopAssetEditingCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseImportAssetCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseGlobalRefreshCalls), Is.Zero);
        }

        [Test]
        public void Import_Collisions_AreAllocatedAcrossTheWholePreflightSet()
        {
            var firstDirectory = Path.Combine(_externalRoot, "First");
            var secondDirectory = Path.Combine(_externalRoot, "Second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var first = Path.Combine(firstDirectory, "Same.txt");
            var second = Path.Combine(secondDirectory, "Same.txt");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");

            var result = ExternalAssetImporter.Import(
                new[] { first, second },
                _destinationAssetPath);

            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.ImportedAssetPaths, Is.EqualTo(new[]
            {
                _destinationAssetPath + "/Same.txt",
                _destinationAssetPath + "/Same 1.txt",
            }));
        }

        [Test]
        public void Import_CommitFailure_RollsBackThatTopLevelAndReportsPartialSuccess()
        {
            var failed = Path.Combine(_externalRoot, "Fail.txt");
            var succeeded = Path.Combine(_externalRoot, "Succeed.txt");
            File.WriteAllText(failed, "fail");
            File.WriteAllText(succeeded, "succeed");
            ExternalAssetImporter.CheckpointForTests = checkpoint =>
            {
                if (checkpoint.StartsWith("before-commit:", StringComparison.Ordinal)
                    && checkpoint.EndsWith("Fail.txt", StringComparison.Ordinal))
                {
                    throw new IOException("simulated commit failure");
                }
            };

            var result = ExternalAssetImporter.Import(
                new[] { failed, succeeded },
                _destinationAssetPath);

            Assert.That(result.ImportedAssetPaths, Is.EqualTo(new[]
            {
                _destinationAssetPath + "/Succeed.txt",
            }));
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(result.Failures[0].sourcePath, Is.EqualTo(failed));
            Assert.That(File.Exists(ProjectPaths.ToAbsolutePath(
                _destinationAssetPath + "/Fail.txt")), Is.False);
            Assert.That(
                Directory.GetFileSystemEntries(ProjectPaths.ToAbsolutePath(_destinationAssetPath),
                    ".zep-import-*~"),
                Is.Empty);
        }

        [Test]
        public void Import_TopLevelMetaSidecar_IsRejectedBeforeWrites()
        {
            var sourceMeta = Path.Combine(_externalRoot, "Foreign.asset.meta");
            File.WriteAllText(sourceMeta, "foreign sidecar");

            var result = ExternalAssetImporter.Import(
                new[] { sourceMeta },
                _destinationAssetPath);

            Assert.That(result.ImportedAssetPaths, Is.Empty);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(Directory.GetFileSystemEntries(
                ProjectPaths.ToAbsolutePath(_destinationAssetPath)), Is.Empty);
        }

#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        [Test]
        public void Import_SymbolicLinkInsideDirectory_RejectsTheWholeTopLevel()
        {
            var payload = Path.Combine(_externalRoot, "LinkedPayload");
            Directory.CreateDirectory(payload);
            var target = Path.Combine(_externalRoot, "Target.txt");
            File.WriteAllText(target, "target");
            var link = Path.Combine(payload, "Link.txt");
            Assert.That(symlink(target, link), Is.Zero,
                "the test fixture must be able to create a symbolic link");

            var result = ExternalAssetImporter.Import(
                new[] { payload },
                _destinationAssetPath);

            Assert.That(result.ImportedAssetPaths, Is.Empty);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(Directory.Exists(ProjectPaths.ToAbsolutePath(
                _destinationAssetPath + "/LinkedPayload")), Is.False);
        }
#endif

        [Test]
        public void Import_ReadOnlyBuiltInPackage_IsRejectedBeforeWrites()
        {
            var source = Path.Combine(_externalRoot, "ReadOnly.txt");
            File.WriteAllText(source, "read only package probe");

            var result = ExternalAssetImporter.Import(
                new[] { source },
                "Packages/com.unity.modules.ui");

            Assert.That(result.ImportedAssetPaths, Is.Empty);
            Assert.That(result.Failures.Count, Is.EqualTo(1));
            Assert.That(result.Failures[0].reason, Does.Contain("not a writable"));
        }

        // Dropping many files that share a name is the shape that used to cost
        // a full suffix walk each: 500 files onto a folder already holding 201
        // of them re-probed everything already taken, ~125k probes of two
        // syscalls each. The high-water mark makes it one probe per file after
        // the first walk, and the NAMES must stay exactly what they were.
        [Test]
        public void Import_ManySameNamedFiles_ProbesEachTakenSuffixOnceAndNamesThemInOrder()
        {
            const int existingCount = 201; // "a.txt" plus "a 1.txt" .. "a 200.txt"
            const int dropCount = 500;
            var destinationFullPath = Path.Combine(
                ProjectPaths.GetProjectRootAbsolutePath(),
                _destinationAssetPath);
            File.WriteAllText(Path.Combine(destinationFullPath, "a.txt"), "existing");
            for (var i = 1; i < existingCount; i++)
            {
                File.WriteAllText(Path.Combine(destinationFullPath, "a " + i + ".txt"), "existing");
            }

            var sources = new string[dropCount];
            for (var i = 0; i < dropCount; i++)
            {
                var sourceDirectory = Path.Combine(_externalRoot, "src" + i.ToString("D4"));
                Directory.CreateDirectory(sourceDirectory);
                sources[i] = Path.Combine(sourceDirectory, "a.txt");
                File.WriteAllText(sources[i], "dropped " + i);
            }

            var probeCount = 0;
            ExternalAssetImporter.ExistenceProbeForTests = () => probeCount++;
            try
            {
                var result = ExternalAssetImporter.Import(sources, _destinationAssetPath);

                Assert.That(result.Failures, Is.Empty);
                Assert.That(result.ImportedAssetPaths.Count, Is.EqualTo(dropCount));
                Assert.That(
                    result.ImportedAssetPaths[0],
                    Is.EqualTo(_destinationAssetPath + "/a 201.txt"),
                    "the first free suffix after the existing run");
                Assert.That(
                    result.ImportedAssetPaths[dropCount - 1],
                    Is.EqualTo(_destinationAssetPath + "/a 700.txt"),
                    "names must keep running on from there, one per dropped file");
                Assert.That(
                    probeCount, Is.LessThanOrEqualTo(1200),
                    "each taken suffix may be probed once for the whole drop, not once per file");
            }
            finally
            {
                ExternalAssetImporter.ExistenceProbeForTests = null;
            }
        }

        [Test]
        public void Import_OneThousandTopLevelFiles_UsesOneBalancedTargetedGroup()
        {
            const int fileCount = 1000;
            var sources = new string[fileCount];
            for (var i = 0; i < fileCount; i++)
            {
                sources[i] = Path.Combine(
                    _externalRoot,
                    "File" + i.ToString("D4") + ".txt");
                File.WriteAllText(sources[i], "bulk " + i);
            }

            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();
            var result = ExternalAssetImporter.Import(
                sources,
                _destinationAssetPath);
            var snapshot = ZepPerfLog.CaptureSnapshot();

            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.ImportedAssetPaths.Count, Is.EqualTo(fileCount));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseStartAssetEditingCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseStopAssetEditingCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseImportAssetCalls), Is.EqualTo(fileCount));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseGlobalRefreshCalls), Is.Zero);
        }
    }
}
