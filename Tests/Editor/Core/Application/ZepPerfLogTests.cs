using System;
using System.IO;
using System.Linq;
using Zep.Core.Application;
using NUnit.Framework;

namespace Zep.Editor.Tests.Core.Application
{
    public sealed class ZepPerfLogTests
    {
        [Test]
        public void PackageRemoval_StopsLoggingAndReleasesTheDiagnosticFile()
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Log("package-removal-file-release-probe");
                ZepPerfLog.StopFileLoggingForPackageRemoval();
                Assert.That(ZepPerfLog.IsEnabled, Is.False);
                using var exclusive = new FileStream(ZepPerfLog.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.That(exclusive.Length, Is.GreaterThan(0));
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
            }
        }

        [Test]
        public void Measurements_PastTheRingCapacity_KeepOnlyTheMostRecentWindow()
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                var overflow = ZepPerfLog.MeasurementCapacity + 25;
                for (var i = 0; i < overflow; i++)
                {
                    using (ZepPerfLog.Measure(
                        "ring-probe-" + i,
                        ZepPerfStage.UnityBoundary))
                    {
                    }
                }

                var measurements = ZepPerfLog.CaptureSnapshot().Measurements;
                Assert.That(
                    measurements.Count,
                    Is.EqualTo(ZepPerfLog.MeasurementCapacity),
                    "the measurement log must stay bounded while the perf toggle is on");
                Assert.That(
                    measurements[0].Operation,
                    Is.EqualTo("ring-probe-25"),
                    "the oldest surviving entry must be the first one still inside the window");
                Assert.That(
                    measurements[measurements.Count - 1].Operation,
                    Is.EqualTo("ring-probe-" + (overflow - 1)),
                    "the newest entry must be last: readers filter a chronological window");
                Assert.That(
                    measurements.Select(entry => entry.Operation).Distinct().Count(),
                    Is.EqualTo(ZepPerfLog.MeasurementCapacity),
                    "wrapping must overwrite the oldest slot, never duplicate a slot");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
            }
        }

        // The audit's scaling work asserts on these ten counters, so the enum
        // ordinal → counter-array plumbing has to be proven for each of them
        // individually: a member appended past the array's length, or a
        // snapshot that returned a shared/stale row, would make every later
        // assertion silently read zero. Distinct increment counts per counter
        // (1..10) distinguish "each counter has its own slot" from "they all
        // land in the same slot", which equal counts would not.
        [Test]
        public void Count_ForTheAuditCounters_IsReadBackPerCounterFromTheSnapshot()
        {
            var counters = new[]
            {
                ZepPerfCounter.MetaFileGuidReads,
                ZepPerfCounter.TagPresentationRebuilds,
                ZepPerfCounter.FolderCatalogRemerges,
                ZepPerfCounter.AssetChangesNormalizations,
                ZepPerfCounter.SearchAlphabeticalResorts,
                ZepPerfCounter.SubAssetCatalogChangedRaised,
                ZepPerfCounter.UndoHistorySaves,
                ZepPerfCounter.PaneChangedDispatches,
            };

            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                for (var i = 0; i < counters.Length; i++)
                {
                    for (var increment = 0; increment <= i; increment++)
                    {
                        ZepPerfLog.Count(counters[i]);
                    }
                }

                var snapshot = ZepPerfLog.CaptureSnapshot();
                for (var i = 0; i < counters.Length; i++)
                {
                    Assert.That(
                        snapshot.GetCount(counters[i]),
                        Is.EqualTo(i + 1),
                        counters[i] + " must own its own counter slot");
                }

                ZepPerfLog.Reset();
                var cleared = ZepPerfLog.CaptureSnapshot();
                for (var i = 0; i < counters.Length; i++)
                {
                    Assert.That(
                        cleared.GetCount(counters[i]),
                        Is.Zero,
                        counters[i] + " must be cleared by Reset so a test's SetUp starts from zero");
                }
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
            }
        }

        [Test]
        public void Reset_EmptiesTheRingSoLaterSnapshotsSeeOnlyNewMeasurements()
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();
                for (var i = 0; i < ZepPerfLog.MeasurementCapacity + 3; i++)
                {
                    using (ZepPerfLog.Measure(
                        "before-reset",
                        ZepPerfStage.UnityBoundary))
                    {
                    }
                }

                ZepPerfLog.Reset();
                Assert.That(
                    ZepPerfLog.CaptureSnapshot().Measurements,
                    Is.Empty);

                using (ZepPerfLog.Measure(
                    "after-reset",
                    ZepPerfStage.UnityBoundary))
                {
                }

                var measurements = ZepPerfLog.CaptureSnapshot().Measurements;
                Assert.That(measurements.Count, Is.EqualTo(1));
                Assert.That(measurements[0].Operation, Is.EqualTo("after-reset"));
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
            }
        }

        // The writer is buffered — a measured path emits thousands of lines per
        // user action and a flush per line was the slowest thing on it — so the
        // log is only guaranteed to be on disk at the three points that end a
        // window: Reset, turning the toggle off, and domain unload. A line
        // written before the toggle went off has to be readable afterwards, or
        // the tail of every session would be the part that never lands.
        [Test]
        public void Log_WhileBuffered_IsOnDiskAfterTheToggleGoesOff()
        {
            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            var marker = "flush-probe-" + Guid.NewGuid().ToString("N");
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Log(marker);

                ZepPerfLog.IsEnabled = false;

                Assert.That(
                    ReadLogFile(),
                    Does.Contain(marker),
                    "disabling the toggle must flush what was already logged");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
            }
        }

        // The writer holds the file open with FileShare.ReadWrite.
        private static string ReadLogFile()
        {
            using (var stream = new FileStream(
                ZepPerfLog.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
