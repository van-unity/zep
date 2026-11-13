using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.TestInfrastructure;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    // Tag assignments are stored per GUID, and resolving a GUID from a path
    // reads the sibling .meta off disk. Every tag-projection rebuild used to
    // pay that once per asset and once per folder, so a project-wide rebuild
    // was a project-wide disk-I/O loop. These run against the REAL gateway so
    // the .meta read is a real read, counted where it happens
    // (UnityMetaFileReader.ReadGuidFromMetaFile).
    public sealed class UnityProjectAssetGatewayTagLookupTests
    {
        private const int AssetCount = 5;

        private UnityProjectAssetGateway _gateway;
        private ProjectTagStateSnapshot _originalTagState;
        private bool _previousPerfEnabled;
        private System.Action<string> _previousSink;

        // CaptureTagState / CompareAndSetTagState are explicit interface
        // implementations on the gateway, so they need an interface reference.
        private IProjectTagDefinitionGateway TagGateway => _gateway;

        [SetUp]
        public void SetUp()
        {
            _gateway = new UnityProjectAssetGateway();
            _originalTagState = TagGateway.CaptureTagState();
            _previousPerfEnabled = ZepPerfLog.IsEnabled;
            _previousSink = ZepPerfLog.Sink;
            ZepPerfLog.Sink = null;
            ZepPerfLog.IsEnabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            ZepPerfLog.IsEnabled = _previousPerfEnabled;
            ZepPerfLog.Sink = _previousSink;
            ZepPerfLog.Reset();
            var current = TagGateway.CaptureTagState();
            var restore = TagGateway.CompareAndSetTagState(current, _originalTagState);
            Assert.That(restore.Succeeded, Is.True, restore.ErrorMessage);
        }

        [Test]
        public void TagProjectionRebuild_WithTheGuidAlreadyOnTheRecord_ReadsNoMetaFiles()
        {
            using var scope = new TempAssetScope();
            var records = new List<AssetRecord>(AssetCount);
            for (var i = 0; i < AssetCount; i++)
            {
                var assetPath = scope.CreateTextAsset("Tagged" + i + ".txt", "content-" + i);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                Assert.That(guid, Is.Not.Empty, "the fixture asset must be in the AssetDatabase");
                records.Add(new AssetRecord(
                    guid,
                    assetPath,
                    Path.GetFileNameWithoutExtension(assetPath),
                    "TextAsset"));
            }

            // Red baseline, measured in this same run rather than asserted from
            // memory: the path-only overload still costs one .meta read each.
            ZepPerfLog.Reset();
            for (var i = 0; i < records.Count; i++)
            {
                _gateway.GetTagIds(AssetPath.From(records[i].AssetPath));
            }

            Assert.That(
                ZepPerfLog.CaptureSnapshot().GetCount(
                    ZepPerfCounter.MetaFileGuidReads),
                Is.EqualTo(AssetCount),
                "precondition: resolving a tag lookup from a path reads one .meta per asset");

            var tagProjection = new TagProjectionService(
                _gateway,
                new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }));
            ZepPerfLog.Reset();

            tagProjection.RebuildCache(
                records,
                new[]
                {
                    new ZepFolderEntry(
                        Path.GetFileName(scope.FolderPath),
                        scope.FolderPath,
                        1,
                        false,
                        guid: AssetDatabase.AssetPathToGUID(scope.FolderPath)),
                });

            Assert.That(
                ZepPerfLog.CaptureSnapshot().GetCount(
                    ZepPerfCounter.MetaFileGuidReads),
                Is.Zero,
                "a rebuild must reuse the GUID the record and the folder entry already carry");
        }

        // A synthetic folder entry (a package root, a synthetic row) carries no
        // GUID. Falling through to the path overload is what keeps its tags
        // visible; returning "no tags" for an empty GUID would drop them.
        [Test]
        public void GetTagIds_WithAnEmptyKnownGuid_FallsBackToThePathLookup()
        {
            using var scope = new TempAssetScope();
            var assetPath = scope.CreateTextAsset("Falls Through.txt", "content");
            var typedPath = AssetPath.From(assetPath);
            var assigned = _gateway.SetTagIds(
                new Dictionary<AssetPath, IReadOnlyList<string>>
                {
                    { typedPath, new[] { "tag-fallthrough" } },
                });
            Assert.That(assigned.Succeeded, Is.True, assigned.ErrorMessage);

            Assert.That(
                _gateway.GetTagIds(typedPath, string.Empty),
                Is.EqualTo(new[] { "tag-fallthrough" }));
            Assert.That(
                _gateway.GetTagIds(typedPath, "   "),
                Is.EqualTo(new[] { "tag-fallthrough" }));
            Assert.That(
                _gateway.GetTagIds(typedPath, AssetDatabase.AssetPathToGUID(assetPath)),
                Is.EqualTo(new[] { "tag-fallthrough" }));
        }
    }
}
