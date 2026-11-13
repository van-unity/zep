using System;
using Zep.Core.Application;
using Zep.Core.Indexing;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Indexing
{
    public sealed class ProjectAssetChangesTests
    {
        private bool _previousPerfLogEnabled;
        private Action<string> _previousPerfLogSink;

        [SetUp]
        public void SetUp()
        {
            _previousPerfLogEnabled = ZepPerfLog.IsEnabled;
            _previousPerfLogSink = ZepPerfLog.Sink;
            ZepPerfLog.Sink = null;
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            ZepPerfLog.IsEnabled = _previousPerfLogEnabled;
            ZepPerfLog.Sink = _previousPerfLogSink;
            ZepPerfLog.Reset();
        }

        // One Unity callback batch reaches Normalize() through ten call sites
        // (postprocessor → journal → syncer → coordinator → concrete-scan record).
        // Normalizing an already-normalized batch can only reproduce the same
        // value, so every pass after the first is pure cost.
        [Test]
        public void Normalize_RepeatedOnItsOwnResult_DoesTheWorkOnce()
        {
            var changes = new ProjectAssetChanges(
                new[] { "Assets/Bulk/a.asset" },
                new[] { "Assets/Bulk/b.asset", "Assets/Bulk/c.asset" },
                new[] { "Assets/Bulk/moved.asset" },
                new[] { "Assets/Bulk/source.asset" });

            var normalized = changes.Normalize();
            for (var i = 0; i < 4; i++)
            {
                normalized = normalized.Normalize();
            }

            Assert.That(
                ZepPerfLog.CaptureSnapshot().GetCount(
                    ZepPerfCounter.AssetChangesNormalizations),
                Is.EqualTo(1),
                "five Normalize() calls over one batch must normalise it exactly once");
            Assert.That(
                normalized.Deleted,
                Is.EqualTo(new[] { "Assets/Bulk/b.asset", "Assets/Bulk/c.asset" }),
                "the short-circuited result must still carry the normalised payload");
            Assert.That(
                normalized.Moved,
                Is.EqualTo(new[] { "Assets/Bulk/moved.asset" }));
            Assert.That(
                normalized.MovedFromPaths,
                Is.EqualTo(new[] { "Assets/Bulk/source.asset" }));
        }

        [Test]
        public void Normalize_OfAnUnsortedBatch_ProducesTheDocumentedArrays()
        {
            // Deliberately unsorted and adversarial: a parent listed AFTER its own
            // child, an exact duplicate, a case-differing duplicate, a sibling whose
            // name merely starts with an accepted root ("Assets/Foobar" under
            // "Assets/Foo" — a prefix but not a path ancestor), a trailing separator,
            // an empty entry, an import that is also a move target, and move pairs
            // whose source depth differs from their input order.
            var changes = new ProjectAssetChanges(
                new[]
                {
                    "Assets/Imp/one.png",
                    "Assets/Moved/target.txt",
                    "assets/imp/one.png",
                    "",
                },
                new[]
                {
                    "Assets/Foo/Bar/deep.txt",
                    "Assets/Foo",
                    "Assets/Foo/Bar",
                    "Assets/Foobar/x.txt",
                    "assets/foo/bar/deep.txt",
                    "Assets/Zed/a.txt",
                    "Assets/Zed/a.txt",
                    "Assets/Alpha/b.txt",
                    "Assets/Beta/c.txt/",
                    "",
                    "Assets/Foo/Bar/Baz/deeper.txt",
                },
                new[]
                {
                    "Assets/Moved/target.txt",
                    "Assets/M2/t2.txt",
                    "Assets/M2/t2.txt",
                    "Assets/M3/t3.txt",
                    "",
                },
                new[]
                {
                    "Assets/Old/deep/src.txt",
                    "Assets/O/s.txt",
                    "Assets/O/s.txt",
                    "Assets/Old/deeper/nested/src3.txt",
                    "Assets/Ignored/src.txt",
                });

            var normalized = changes.Normalize();

            Assert.That(
                normalized.Imported,
                Is.EqualTo(new[] { "Assets/Imp/one.png" }),
                "imports must drop duplicates (case-insensitively) and anything that is also a move target");
            Assert.That(
                normalized.Deleted,
                Is.EqualTo(new[]
                {
                    "Assets/Foo",
                    "Assets/Zed/a.txt",
                    "Assets/Beta/c.txt",
                    "Assets/Alpha/b.txt",
                    "Assets/Foobar/x.txt",
                }),
                "deletes must coalesce under their ancestor and stay in shortest-first acceptance order (neither input nor alphabetical order)");
            Assert.That(
                normalized.Moved,
                Is.EqualTo(new[]
                {
                    "Assets/M3/t3.txt",
                    "Assets/Moved/target.txt",
                    "Assets/M2/t2.txt",
                }),
                "move pairs must be ordered deepest source first, not by input order");
            Assert.That(
                normalized.MovedFromPaths,
                Is.EqualTo(new[]
                {
                    "Assets/Old/deeper/nested/src3.txt",
                    "Assets/Old/deep/src.txt",
                    "Assets/O/s.txt",
                }),
                "sources must stay paired by index with their targets");
        }
    }
}
