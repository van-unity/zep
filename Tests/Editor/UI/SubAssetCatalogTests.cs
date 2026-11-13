using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Editor.UI.AssetsArea;
using NUnit.Framework;

namespace Zep.Tests.Editor.UI
{
    // The sub-asset pump's queue policy. Every miss on a row bind lands in the
    // backlog, and every Changed rebinds the realized window of every attached
    // list and grid — so one fast scroll used to keep the editor rebinding for
    // minutes while loading every scrolled-past asset into a cache that was
    // never bounded. What is asserted here is the policy: how much backlog is
    // kept, which end it is drained from, how often the signal is raised, and
    // how large the cache may grow. The work the pump budgets (a Unity asset
    // load plus preview generation) is replaced by ResolveOverrideForTests —
    // it is the platform boundary, not the behaviour under test.
    public sealed class SubAssetCatalogTests
    {
        // Mirrors the production constants; a test that read them from the
        // class could not fail if they were changed to absurd values.
        private const int MaxPathsPerTick = 4;
        private const int MaxPendingPaths = 512;
        private const int NotifyEveryTicks = 8;
        private const int MaxResolvedEntries = 4096;

        private bool _previousPerfEnabled;
        private Action<string> _previousSink;
        private readonly List<string> _resolvedOrder = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _previousPerfEnabled = ZepPerfLog.IsEnabled;
            _previousSink = ZepPerfLog.Sink;
            ZepPerfLog.Sink = null;
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();

            _resolvedOrder.Clear();
            SubAssetCatalog.ResetForTests();
            SubAssetCatalog.ResolveOverrideForTests = path =>
            {
                _resolvedOrder.Add(path);
                return Array.Empty<SubAssetDescriptor>();
            };
        }

        [TearDown]
        public void TearDown()
        {
            SubAssetCatalog.ResolveOverrideForTests = null;
            SubAssetCatalog.ResetForTests();
            ZepPerfLog.IsEnabled = _previousPerfEnabled;
            ZepPerfLog.Sink = _previousSink;
            ZepPerfLog.Reset();
        }

        private static string PathAt(int index)
        {
            return "Assets/SubAssetPump/Item" + index + ".asset";
        }

        private static void Request(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.That(
                    SubAssetCatalog.GetOrRequest(PathAt(i)),
                    Is.Null,
                    "an unresolved path must report a miss so the row re-asks after the pump runs");
            }
        }

        [Test]
        public void Ticking_ALongBacklog_CoalescesTheChangedSignal()
        {
            const int ticks = 100;
            Request(2000);

            for (var i = 0; i < ticks; i++)
            {
                SubAssetCatalog.TickForTests();
            }

            var raised = ZepPerfLog
                .CaptureSnapshot()
                .GetCount(ZepPerfCounter.SubAssetCatalogChangedRaised);

            // One per tick (100) is what the uncoalesced pump did, and each of
            // those is a full rebind in every attached widget.
            Assert.That(
                raised,
                Is.LessThanOrEqualTo((ticks / NotifyEveryTicks) + 1),
                "a still-draining backlog must batch its rebind signal");
            Assert.That(
                raised,
                Is.GreaterThan(0),
                "resolved rows must still be published while the backlog drains");
        }

        [Test]
        public void Requesting_PastTheBacklogBound_KeepsTheMostRecentRequests()
        {
            const int requested = 2000;
            Request(requested);

            // Enough ticks to empty a full backlog, plus slack: what is drained
            // is exactly what survived the bound.
            for (var i = 0; i < (MaxPendingPaths / MaxPathsPerTick) + 4; i++)
            {
                SubAssetCatalog.TickForTests();
            }

            Assert.That(
                _resolvedOrder.Count,
                Is.EqualTo(MaxPendingPaths),
                "the backlog must be bounded, and everything kept must eventually drain");
            Assert.That(
                _resolvedOrder[0],
                Is.EqualTo(PathAt(requested - 1)),
                "the newest request is the one still on screen, so it is served first");

            var expected = new List<string>(MaxPendingPaths);
            for (var i = requested - MaxPendingPaths; i < requested; i++)
            {
                expected.Add(PathAt(i));
            }

            Assert.That(
                _resolvedOrder,
                Is.EquivalentTo(expected),
                "overflow must drop the OLDEST requests, not the newest");
            Assert.That(
                SubAssetCatalog.GetOrRequest(PathAt(requested - 1)),
                Is.Not.Null,
                "a drained path must answer from the cache on its next bind");
            Assert.That(
                SubAssetCatalog.GetOrRequest(PathAt(0)),
                Is.Null,
                "a dropped path is not cached — it simply re-requests when bound again");
        }

        [Test]
        public void Resolving_MoreThanTheCacheBound_NeverRetainsMoreThanTheBound()
        {
            const int resolutions = 5000;
            var highWater = 0;

            for (var i = 0; i < resolutions; i++)
            {
                Assert.That(SubAssetCatalog.GetOrRequest(PathAt(i)), Is.Null);
                SubAssetCatalog.TickForTests();
                highWater = Math.Max(highWater, SubAssetCatalog.ResolvedCountForTests);
            }

            Assert.That(
                highWater,
                Is.LessThanOrEqualTo(MaxResolvedEntries),
                "the resolved cache must be bounded — browsing alone never evicts it");
            Assert.That(
                highWater,
                Is.GreaterThan(MaxPathsPerTick),
                "the cache must actually hold resolved paths between binds");
            Assert.That(
                _resolvedOrder.Count,
                Is.EqualTo(resolutions),
                "every requested path must still be resolved exactly once");
        }
    }
}
