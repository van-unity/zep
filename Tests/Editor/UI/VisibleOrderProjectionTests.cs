using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using NUnit.Framework;

namespace Zep.Tests.Editor.UI
{
    // The flat visible cell order the list and the grid both read (audit
    // §6.1b). Pure: an order this fixture supplies plus a sub-asset catalog it
    // controls through the catalog's own resolve override — no window, no
    // ListView, no widget.
    //
    // The invariant under test is the one the type exists for: spans sorted by
    // asset index, each carrying the running number of sub cells before it, so
    // that visible↔asset index math is exact in both directions.
    [TestFixture]
    public sealed class VisibleOrderProjectionTests
    {
        private const string EntryTypeName = "ScriptableObject";

        private readonly Dictionary<string, SubAssetDescriptor[]> _subAssetsByPath =
            new Dictionary<string, SubAssetDescriptor[]>(StringComparer.Ordinal);

        [SetUp]
        public void SetUp()
        {
            _subAssetsByPath.Clear();
            SubAssetCatalog.ResetForTests();
            SubAssetCatalog.ResolveOverrideForTests = path =>
                _subAssetsByPath.TryGetValue(path, out var subs)
                    ? subs
                    : Array.Empty<SubAssetDescriptor>();
        }

        [TearDown]
        public void TearDown()
        {
            SubAssetCatalog.ResolveOverrideForTests = null;
            SubAssetCatalog.ResetForTests();
        }

        // Two expansions opened out of order. Every assertion below is a
        // consequence of "spans sorted by AssetIndex, SubsBefore running": the
        // later expansion's own cell has to be pushed down by the earlier one's
        // children, and a main asset after both by all of them.
        [Test]
        public void Spans_AreSortedByAssetIndex_WithRunningSubsBefore()
        {
            var order = Order(10);
            GiveSubAssets(Path(2), 3);
            GiveSubAssets(Path(6), 2);

            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(order);
            // Opened LAST first: the projection, not the caller, owns the order.
            Assert.That(projection.Expand(Path(6)), Is.True);
            Assert.That(projection.Expand(Path(2)), Is.True);

            Assert.That(projection.AssetIndexToVisibleIndex(0), Is.EqualTo(0));
            Assert.That(projection.AssetIndexToVisibleIndex(2), Is.EqualTo(2));
            Assert.That(
                projection.AssetIndexToVisibleIndex(3),
                Is.EqualTo(6),
                "the asset after the first expansion is shifted by its three children");
            Assert.That(
                projection.AssetIndexToVisibleIndex(6),
                Is.EqualTo(9),
                "and the second expansion's own cell by the same three");
            Assert.That(
                projection.AssetIndexToVisibleIndex(7),
                Is.EqualTo(12),
                "the asset after both is shifted by all five children");
        }

        [Test]
        public void VisibleItemCount_IsTheAssetsPlusEverySubCell()
        {
            var order = Order(10);
            GiveSubAssets(Path(2), 3);
            GiveSubAssets(Path(6), 2);

            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(order);
            Assert.That(projection.VisibleItemCount, Is.EqualTo(10));

            projection.Expand(Path(2));
            projection.Expand(Path(6));

            Assert.That(projection.VisibleItemCount, Is.EqualTo(10 + 3 + 2));
            Assert.That(projection.ExpansionCount, Is.EqualTo(2));
        }

        // Both directions of the index math over the whole order, plus the
        // child cells in between: nothing may be skipped or double-counted.
        [Test]
        public void ResolveVisibleIndex_RoundTripsWithAssetIndexToVisibleIndex()
        {
            var order = Order(8);
            GiveSubAssets(Path(1), 2);
            GiveSubAssets(Path(5), 4);

            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(order);
            projection.Expand(Path(1));
            projection.Expand(Path(5));

            for (var assetIndex = 0; assetIndex < 8; assetIndex++)
            {
                var visibleIndex = projection.AssetIndexToVisibleIndex(assetIndex);
                var resolved = projection.ResolveVisibleIndex(visibleIndex, out var sub);
                Assert.That(sub, Is.Null, "a main asset's own cell is never a child cell");
                Assert.That(resolved, Is.EqualTo(assetIndex));
            }

            var parentVisible = projection.AssetIndexToVisibleIndex(5);
            for (var childIndex = 0; childIndex < 4; childIndex++)
            {
                var parent = projection.ResolveVisibleIndex(
                    parentVisible + 1 + childIndex,
                    out var sub,
                    out var subIndex,
                    out var subCount);
                Assert.That(parent, Is.EqualTo(5));
                Assert.That(sub, Is.Not.Null);
                Assert.That(sub.Key, Is.EqualTo(_subAssetsByPath[Path(5)][childIndex].Key));
                Assert.That(subIndex, Is.EqualTo(childIndex));
                Assert.That(subCount, Is.EqualTo(4), "the grid's strip flags read this count");
            }
        }

        // The cursor sits on the highlighted child cell when there is one — a
        // sub-asset holds no path, so the substrate's lead cannot point at it.
        [Test]
        public void ResolveCursorVisibleIndex_PrefersTheHighlightedChildCell()
        {
            var order = Order(6);
            GiveSubAssets(Path(1), 3);

            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(order);
            projection.Expand(Path(1));
            var highlighted = _subAssetsByPath[Path(1)][2].Key;

            Assert.That(
                projection.ResolveCursorVisibleIndex(-1, highlighted),
                Is.EqualTo(1 + 1 + 2));
            Assert.That(
                projection.ResolveCursorVisibleIndex(4, highlighted),
                Is.EqualTo(1 + 1 + 2),
                "the highlight wins over the lead while it resolves");
            Assert.That(projection.ResolveCursorVisibleIndex(4, null), Is.EqualTo(4 + 3));
            Assert.That(projection.ResolveCursorVisibleIndex(-1, null), Is.EqualTo(-1));
        }

        // A publish whose order no longer contains the open row: there is no
        // cell left to hang the children under, so the span goes rather than
        // being re-checked on every later publish (audit §4.4).
        [Test]
        public void SetAssets_DropsASpanWhoseParentLeftTheOrder()
        {
            GiveSubAssets(Path(1), 2);
            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(Order(4));
            projection.Expand(Path(1));
            Assume.That(projection.VisibleItemCount, Is.EqualTo(6));

            projection.SetAssets(Order(4, "Elsewhere"));

            Assert.That(projection.IsExpanded(Path(1)), Is.False);
            Assert.That(projection.ExpansionCount, Is.Zero);
            Assert.That(projection.VisibleItemCount, Is.EqualTo(4));
        }

        // ONE rule for both modes (§6.1b): a disclosure whose parent resolved
        // to no sub-assets at all is closed — its chevron would otherwise claim
        // children that do not exist — UNLESS a pending ping has pinned it, in
        // which case it stays open so a target arriving in a later batch still
        // gets its reveal.
        [Test]
        public void RefreshResolvedSubs_PrunesAZeroSubExpansion_ButNotThePinnedOne()
        {
            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(Order(6));
            // Neither path has sub-assets: both RESOLVE to an empty list, which
            // is what separates "answered with nothing" from "not answered yet".
            GiveSubAssets(Path(1), 0);
            GiveSubAssets(Path(4), 0);
            projection.Expand(Path(1));
            projection.Expand(Path(4));
            projection.PinnedParentPath = Path(4);
            Assume.That(projection.ExpansionCount, Is.EqualTo(2));

            var pruned = projection.RefreshResolvedSubs(out var shapeChanged);

            Assert.That(shapeChanged, Is.True);
            Assert.That(pruned, Is.EquivalentTo(new[] { Path(1) }));
            Assert.That(projection.IsExpanded(Path(1)), Is.False);
            Assert.That(
                projection.IsExpanded(Path(4)),
                Is.True,
                "the pinned parent survives a zero-sub resolve");
        }

        // A pin names a row: once that row leaves the order the ping can never
        // complete, so the pin is released and the widget drops its pending key
        // with it. (Was grid-only before §6.1b; the list now gets it too.)
        [Test]
        public void SetAssets_ReleasesAPinWhoseRowLeftTheOrder()
        {
            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(Order(4));
            projection.PinnedParentPath = Path(1);

            projection.SetAssets(Order(4));
            Assume.That(projection.PinnedParentPath, Is.EqualTo(Path(1)));

            projection.SetAssets(Order(4, "Elsewhere"));

            Assert.That(projection.PinnedParentPath, Is.Empty);
        }

        // shapeChanged is what decides between a full re-projection and a
        // path-scoped rebind (T19), so it must be false for a batch that moved
        // no count at all.
        [Test]
        public void RefreshResolvedSubs_ReportsShapeChanged_OnlyWhenACountMoved()
        {
            GiveSubAssets(Path(1), 2);
            var projection = SubAssetCatalog.NewVisibleOrder();
            projection.SetAssets(Order(5));
            projection.Expand(Path(1));
            Assume.That(projection.VisibleItemCount, Is.EqualTo(7));

            projection.RefreshResolvedSubs(out var unchanged);
            Assert.That(unchanged, Is.False, "nothing resolved between the two calls");

            // The parent gained a third child (a reimport): the count moved, so
            // every cell below it moved with it.
            GiveSubAssets(Path(1), 3);
            SubAssetCatalog.ResetForTests();
            ResolveNow(Path(1));

            projection.RefreshResolvedSubs(out var changed);

            Assert.That(changed, Is.True);
            Assert.That(projection.VisibleItemCount, Is.EqualTo(8));
            Assert.That(projection.AssetIndexToVisibleIndex(2), Is.EqualTo(5));
        }

        // -------------------------------------------------------------------
        // Fixture
        // -------------------------------------------------------------------

        private static string Path(int index) => $"Assets/Order{index:D2}.asset";

        // Resolves the path through the catalog and caches the descriptors the
        // override supplies, so Expand() sees a resolved list rather than the
        // null a first request returns.
        private void GiveSubAssets(string parentPath, int count)
        {
            var subs = new SubAssetDescriptor[count];
            for (var i = 0; i < count; i++)
            {
                subs[i] = new SubAssetDescriptor(
                    parentPath,
                    i + 1,
                    runtimeId: default,
                    name: "Sub" + i,
                    typeName: EntryTypeName,
                    icon: null);
            }

            _subAssetsByPath[parentPath] = subs;
            ResolveNow(parentPath);
        }

        private static void ResolveNow(string parentPath)
        {
            for (var attempt = 0;
                attempt < 8 && SubAssetCatalog.GetOrRequest(parentPath) == null;
                attempt++)
            {
                SubAssetCatalog.TickForTests();
            }

            Assert.That(
                SubAssetCatalog.GetResolved(parentPath),
                Is.Not.Null,
                "the fixture's catalog override never resolved " + parentPath);
        }

        private static FakeOrder Order(int count, string prefix = null)
        {
            var entries = new ZepAssetEntry[count];
            for (var i = 0; i < count; i++)
            {
                var path = prefix == null
                    ? Path(i)
                    : $"Assets/{prefix}{i:D2}.asset";
                entries[i] = new ZepAssetEntry(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    path,
                    EntryTypeName);
            }

            return new FakeOrder(entries);
        }

        // The published-order shape the projection is written against: indexed
        // by position, answering path lookups by index rather than by scan.
        private sealed class FakeOrder
            : IReadOnlyList<ZepAssetEntry>, IAssetPathLookup
        {
            private readonly ZepAssetEntry[] _entries;
            private readonly Dictionary<string, int> _indexByPath;

            internal FakeOrder(ZepAssetEntry[] entries)
            {
                _entries = entries;
                _indexByPath = new Dictionary<string, int>(
                    entries.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < entries.Length; i++)
                {
                    _indexByPath[entries[i].Path] = i;
                }
            }

            public int IndexOfPath(string assetPath)
                => !string.IsNullOrEmpty(assetPath)
                    && _indexByPath.TryGetValue(assetPath, out var index)
                        ? index
                        : -1;

            public int Count => _entries.Length;
            public ZepAssetEntry this[int index] => _entries[index];

            public IEnumerator<ZepAssetEntry> GetEnumerator()
            {
                for (var i = 0; i < _entries.Length; i++)
                {
                    yield return _entries[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => GetEnumerator();
        }
    }
}
