using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Editor.UI;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // A catalog delta re-queries every LOADED bucket it names, but the sidebar
    // shows a folder only when every one of its ancestors is expanded. A
    // bucket sitting under a collapsed branch therefore holds nothing on
    // screen, and rebuilding the visible projection for it rebinds the whole
    // realized window for rows that cannot have changed (§5.25).
    //
    // The gate is VISIBILITY, not expansion: a visible collapsed folder paints
    // its chevron from its own bucket, so gaining a first child changes its
    // own row even though no descendant is on screen.
    public sealed class FolderTreeCatalogDeltaTests
    {
        private const string ExpansionKey =
            "Zep.FolderTreeExpanded.Assets";
        private const string BranchPath = "Assets/Branch";
        private const string InnerPath = "Assets/Branch/Inner";
        private const string LeafPath = "Assets/Branch/Inner/Leaf";
        private const string SiblingPath = "Assets/Sibling";

        private Dictionary<string, List<ZepFolderEntry>> _children;
        private string _previousExpansion;

        [SetUp]
        public void SetUp()
        {
            _previousExpansion = SessionState.GetString(ExpansionKey, null);
            SessionState.EraseString(ExpansionKey);

            // Assets
            //   Branch
            //     Inner
            //       Leaf
            //   Sibling
            _children = new Dictionary<string, List<ZepFolderEntry>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = Entries(("Assets", "Assets", 0)),
                ["Assets"] = Entries(("Branch", BranchPath, 1), ("Sibling", SiblingPath, 1)),
                [BranchPath] = Entries(("Inner", InnerPath, 2)),
                [InnerPath] = Entries(("Leaf", LeafPath, 3)),
                [LeafPath] = Entries(),
                [SiblingPath] = Entries(),
            };
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousExpansion != null)
            {
                SessionState.SetString(ExpansionKey, _previousExpansion);
            }
            else
            {
                SessionState.EraseString(ExpansionKey);
            }
        }

        [UnityTest]
        public IEnumerator CatalogDelta_UnderAHiddenParent_LeavesTheProjectionAlone()
        {
            var window = CreateWindow();
            try
            {
                var tree = MountTree(window);
                yield return null;

                // Navigating into the leaf loads and expands the whole branch.
                tree.Apply(LeafPath, LeafPath);
                yield return null;
                yield return null;
                Assert.That(
                    tree.VisibleFolderCount, Is.EqualTo(5),
                    "Assets, Branch, Inner, Leaf, Sibling");

                // Collapsing Branch hides Inner while Inner's bucket stays
                // loaded — exactly the shape the delta must not rebuild for.
                tree.Apply(BranchPath, BranchPath);
                SendTreeKey(tree, KeyCode.LeftArrow);
                yield return null;
                Assert.That(
                    tree.VisibleFolderCount, Is.EqualTo(3),
                    "Assets, Branch, Sibling");

                _children[InnerPath].Add(Entry("Leaf2", InnerPath + "/Leaf2", 3));

                var rebuilds = FolderTreeWidget.VisibleProjectionRebuildsForTests;
                var binds = tree.RowBindCount;
                tree.ApplyCatalogDelta(DeltaFor(InnerPath));

                Assert.That(
                    FolderTreeWidget.VisibleProjectionRebuildsForTests - rebuilds,
                    Is.Zero,
                    "no visible row reads a bucket under a collapsed branch");
                Assert.That(
                    tree.RowBindCount - binds, Is.Zero,
                    "the realized window must not be rebound for a hidden change");
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(3));

                // The bucket itself was still refreshed: re-expanding the
                // branch shows the folder the delta added.
                tree.Apply(InnerPath + "/Leaf2", InnerPath + "/Leaf2");
                yield return null;
                Assert.That(
                    tree.VisibleFolderCount, Is.EqualTo(6),
                    "Assets, Branch, Inner, Leaf, Leaf2, Sibling");
                Assert.That(RowFor(tree, InnerPath + "/Leaf2"), Is.Not.Null);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator CatalogDelta_GivingAVisibleCollapsedFolderItsFirstChild_ShowsItsChevron()
        {
            var window = CreateWindow();
            try
            {
                var tree = MountTree(window);
                yield return null;

                tree.Apply(SiblingPath, SiblingPath);
                yield return null;
                yield return null;

                // Right on a childless folder loads its bucket without
                // expanding it: visible, collapsed, loaded, no chevron. The
                // chevron keeps its slot (visibility, not display) so the
                // leaf's icon and name stay on its siblings' columns.
                SendTreeKey(tree, KeyCode.RightArrow);
                yield return null;
                Assert.That(
                    Disclosure(tree, SiblingPath).style.visibility.value,
                    Is.EqualTo(Visibility.Hidden),
                    "a childless folder shows no chevron");

                _children[SiblingPath].Add(Entry("Kid", SiblingPath + "/Kid", 2));

                var rebuilds = FolderTreeWidget.VisibleProjectionRebuildsForTests;
                var binds = tree.RowBindCount;
                tree.ApplyCatalogDelta(DeltaFor(SiblingPath));

                Assert.That(
                    FolderTreeWidget.VisibleProjectionRebuildsForTests - rebuilds,
                    Is.EqualTo(1),
                    "a visible parent is rebuilt even while it is collapsed");
                Assert.That(
                    tree.RowBindCount - binds, Is.GreaterThan(0),
                    "the row has to be rebound for its chevron to appear");
                Assert.That(
                    Disclosure(tree, SiblingPath).style.visibility.value,
                    Is.EqualTo(Visibility.Visible),
                    "gaining a first child must show the chevron");
                Assert.That(
                    tree.VisibleFolderCount, Is.EqualTo(3),
                    "the new child stays hidden under its collapsed parent");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator CatalogDelta_UnderAVisibleExpandedParent_PublishesTheNewRow()
        {
            var window = CreateWindow();
            try
            {
                var tree = MountTree(window);
                yield return null;

                tree.Apply(InnerPath, InnerPath);
                yield return null;
                yield return null;
                Assert.That(
                    tree.VisibleFolderCount, Is.EqualTo(4),
                    "Assets, Branch, Inner, Sibling");

                _children[BranchPath].Add(Entry("Second", BranchPath + "/Second", 2));

                var rebuilds = FolderTreeWidget.VisibleProjectionRebuildsForTests;
                var binds = tree.RowBindCount;
                tree.ApplyCatalogDelta(DeltaFor(BranchPath));

                Assert.That(
                    FolderTreeWidget.VisibleProjectionRebuildsForTests - rebuilds,
                    Is.EqualTo(1));
                Assert.That(tree.RowBindCount - binds, Is.GreaterThan(0));
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(5));
                Assert.That(RowFor(tree, BranchPath + "/Second"), Is.Not.Null);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator CatalogDelta_DuringFirstRowDiscovery_WaitsUntilBindingCompletes()
        {
            var window = CreateWindow();
            var discovered = false;
            var reboundDuringDiscovery = false;
            FolderTreeWidget tree = null;
            try
            {
                tree = new FolderTreeWidget(
                    "Assets",
                    new UnityZepIconRepository(),
                    parent =>
                    {
                        if (parent == BranchPath && !discovered)
                        {
                            discovered = true;
                            var binds = tree.RowBindCount;
                            // The controller publishes a catalog delta synchronously
                            // when its first direct-folder observation finds children.
                            _children["Assets"].Add(Entry("Discovered", "Assets/Discovered", 1));
                            var delta = DeltaFor(BranchPath);
                            delta.AffectedParentFolders.Add("Assets");
                            tree.ApplyCatalogDelta(delta);
                            reboundDuringDiscovery = tree.RowBindCount != binds;
                        }

                        return LazyChildren(parent);
                    });
                tree.Apply("Assets", "Assets");
                window.rootVisualElement.Add(tree);

                for (var frame = 0; frame < 10; frame++)
                {
                    window.Repaint();
                    yield return null;
                }

                Assert.That(discovered, Is.True, "the first bind must discover the collapsed branch");
                Assert.That(reboundDuringDiscovery, Is.False,
                    "a catalog notification must not re-enter Unity's active row-binding pass");
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(4),
                    "the deferred notification must publish the newly discovered sibling");

                tree.Apply(BranchPath, BranchPath);
                SendTreeKey(tree, KeyCode.RightArrow);
                yield return null;
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(5),
                    "the discovered child must remain available when its branch expands");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void RecoveryTree_FirstQueryRemainsRetryable()
        {
            var refuseBranch = true;
            var branchQueries = 0;
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                parent =>
                {
                    if (string.Equals(parent, BranchPath, StringComparison.Ordinal))
                    {
                        branchQueries++;
                        if (refuseBranch)
                        {
                            throw new ProjectTagStateUnavailableException(
                                "folder metadata unavailable");
                        }
                    }

                    return LazyChildren(parent);
                });

            tree.Apply(BranchPath, BranchPath);
            Assert.DoesNotThrow(() => SendTreeKey(tree, KeyCode.RightArrow));
            Assert.That(branchQueries, Is.EqualTo(1));
            Assert.That(tree.VisibleFolderCount, Is.EqualTo(3),
                "a refused branch must not be marked as successfully loaded");

            refuseBranch = false;
            SendTreeKey(tree, KeyCode.RightArrow);

            Assert.That(branchQueries, Is.EqualTo(2));
            Assert.That(tree.VisibleFolderCount, Is.EqualTo(4));

            var emptyQueries = 0;
            var emptyTree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                parent =>
                {
                    if (string.Equals(parent, SiblingPath, StringComparison.Ordinal))
                    {
                        emptyQueries++;
                    }

                    return LazyChildren(parent);
                });
            emptyTree.Apply(SiblingPath, SiblingPath);
            SendTreeKey(emptyTree, KeyCode.RightArrow);
            SendTreeKey(emptyTree, KeyCode.RightArrow);

            Assert.That(emptyQueries, Is.EqualTo(1),
                "a genuine empty result is a successful cached load");
        }

        [Test]
        public void RecoveryTree_DeltaRefusalPreservesBranch()
        {
            var refuseBranch = false;
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                parent =>
                {
                    if (refuseBranch && string.Equals(parent, BranchPath, StringComparison.Ordinal))
                    {
                        throw new ProjectTagStateUnavailableException(
                            "folder metadata unavailable");
                    }

                    return LazyChildren(parent);
                });
            tree.Apply(LeafPath, LeafPath);
            var previousCount = tree.VisibleFolderCount;

            _children[BranchPath] = Entries(
                ("Zulu", BranchPath + "/Zulu", 2),
                ("Alpha", BranchPath + "/Alpha", 2));
            refuseBranch = true;

            Assert.DoesNotThrow(() => tree.ApplyCatalogDelta(DeltaFor(BranchPath)));
            Assert.That(tree.VisibleFolderCount, Is.EqualTo(previousCount));
            Assert.That(tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets").itemsSource,
                Has.Some.Matches<object>(item => item is ZepFolderEntry entry && entry.Path == InnerPath));

            refuseBranch = false;
            tree.ApplyCatalogDelta(DeltaFor(BranchPath));

            var paths = tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                .itemsSource.Cast<ZepFolderEntry>()
                .Select(entry => entry.Path)
                .ToArray();
            Assert.That(paths, Does.Contain(BranchPath + "/Alpha"));
            Assert.That(paths, Does.Contain(BranchPath + "/Zulu"));
            Assert.That(paths, Does.Not.Contain(InnerPath));
            Assert.That(Array.IndexOf(paths, BranchPath + "/Zulu"),
                Is.LessThan(Array.IndexOf(paths, BranchPath + "/Alpha")),
                "a retry must preserve the provider's deliberately unsorted visible order");

            var entryFaultPath = BranchPath + "/Unmaterialized";
            var entryRefusals = 0;
            var entryReturns = 0;
            var refuseEntry = true;
            var recoveredEntry = Entry("Unmaterialized", entryFaultPath, 2);
            var entryTree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                LazyChildren,
                path =>
                {
                    if (string.Equals(path, entryFaultPath, StringComparison.Ordinal))
                    {
                        if (refuseEntry)
                        {
                            entryRefusals++;
                            throw new ProjectTagStateUnavailableException(
                                "entry metadata unavailable");
                        }

                        entryReturns++;
                        return recoveredEntry;
                    }

                    return LazyChildren("Assets").FirstOrDefault(entry => entry.Path == path)
                        ?? LazyChildren(BranchPath).FirstOrDefault(entry => entry.Path == path);
                });

            Assert.DoesNotThrow(() => entryTree.Apply(entryFaultPath, entryFaultPath));
            Assert.That(entryRefusals, Is.GreaterThanOrEqualTo(1));
            Assert.That(entryTree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                    .itemsSource.Cast<ZepFolderEntry>().Select(entry => entry.Path),
                Does.Not.Contain(entryFaultPath),
                "a refused entry must not be cached or become visible");

            refuseEntry = false;
            entryTree.Apply(SiblingPath, SiblingPath);
            entryTree.Apply(entryFaultPath, entryFaultPath);

            Assert.That(entryReturns, Is.GreaterThanOrEqualTo(1),
                "recovery must acquire the target through its entry callback");
            Assert.That(entryTree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                    .itemsSource.Cast<ZepFolderEntry>().Select(entry => entry.Path),
                Does.Contain(entryFaultPath),
                "the same entry must be retried after its metadata becomes available");
        }

        [Test]
        public void RecoveryTree_InvalidationIsAtomic()
        {
            var failure = (Exception)null;
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                parent =>
                {
                    if (failure != null && string.Equals(parent, BranchPath, StringComparison.Ordinal))
                    {
                        throw failure;
                    }

                    return LazyChildren(parent);
                });
            tree.Apply(LeafPath, LeafPath);
            var previousPaths = tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                .itemsSource.Cast<ZepFolderEntry>()
                .Select(entry => entry.Path)
                .ToArray();

            _children[BranchPath] = Entries(("Replacement", BranchPath + "/Replacement", 2));
            failure = new ProjectTagStateUnavailableException(
                "folder metadata unavailable");

            Assert.DoesNotThrow(tree.InvalidateCatalog);
            Assert.That(tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                    .itemsSource.Cast<ZepFolderEntry>().Select(entry => entry.Path),
                Is.EqualTo(previousPaths),
                "a partial replacement must never become visible after a typed refusal");

            failure = null;
            tree.InvalidateCatalog();
            Assert.That(tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                    .itemsSource.Cast<ZepFolderEntry>().Select(entry => entry.Path),
                Does.Contain(BranchPath + "/Replacement"));
            Assert.That(tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                    .itemsSource.Cast<ZepFolderEntry>().Select(entry => entry.Path),
                Does.Not.Contain(InnerPath));

            var unexpected = new InvalidOperationException("unexpected metadata failure");
            failure = unexpected;
            var thrown = Assert.Throws<InvalidOperationException>(tree.InvalidateCatalog);
            Assert.That(thrown, Is.SameAs(unexpected));
            Assert.That(tree.Q<UnityEngine.UIElements.ListView>("zep-folder-list-assets")
                    .itemsSource.Cast<ZepFolderEntry>().Select(entry => entry.Path),
                Does.Contain(BranchPath + "/Replacement"),
                "unrelated failures restore the model before propagating");

            failure = null;
            Assert.DoesNotThrow(tree.InvalidateCatalog,
                "the temporary invalidation mode must reset after either failure kind");
        }

        [UnityTest]
        public IEnumerator RecoveryTreeTint_RefreshRefusalIsAtomic()
        {
            var tints = new Dictionary<string, ZepTagColor>(StringComparer.OrdinalIgnoreCase)
            {
                [BranchPath] = ZepTagColor.Red,
                [SiblingPath] = ZepTagColor.Blue
            };
            var refuseSibling = false;
            var window = CreateWindow();
            try
            {
                var tree = MountTree(window, path =>
                {
                    if (refuseSibling && string.Equals(path, SiblingPath, StringComparison.Ordinal))
                    {
                        throw new ProjectTagStateUnavailableException("folder tint unavailable");
                    }

                    return tints.TryGetValue(path, out var tint) ? tint : ZepTagColor.None;
                });
                yield return null;
                tree.Apply("Assets", "Assets");
                yield return null;

                var branchIcon = RowFor(tree, BranchPath).Q<VisualElement>("zep-folder-row-icon-image");
                var siblingIcon = RowFor(tree, SiblingPath).Q<VisualElement>("zep-folder-row-icon-image");
                var originalBranchTint = branchIcon.style.unityBackgroundImageTintColor.value;
                var originalSiblingTint = siblingIcon.style.unityBackgroundImageTintColor.value;
                tints[BranchPath] = ZepTagColor.Green;
                tints[SiblingPath] = ZepTagColor.Orange;
                refuseSibling = true;

                Assert.DoesNotThrow(tree.RefreshRealizedFolderTints);
                Assert.That(branchIcon.style.unityBackgroundImageTintColor.value, Is.EqualTo(originalBranchTint));
                Assert.That(siblingIcon.style.unityBackgroundImageTintColor.value, Is.EqualTo(originalSiblingTint));

                refuseSibling = false;
                tree.RefreshRealizedFolderTints();
                Assert.That(branchIcon.style.unityBackgroundImageTintColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Green)));
                Assert.That(siblingIcon.style.unityBackgroundImageTintColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Orange)));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator RecoveryTreeTint_RebindingPreservesOnlySamePath()
        {
            var tints = new Dictionary<string, ZepTagColor>(StringComparer.OrdinalIgnoreCase)
            {
                [BranchPath] = ZepTagColor.Red,
                [SiblingPath] = ZepTagColor.Blue
            };
            var refuse = false;
            var window = CreateWindow();
            try
            {
                var tree = MountTree(window, path =>
                {
                    if (refuse)
                    {
                        throw new ProjectTagStateUnavailableException("folder tint unavailable");
                    }

                    return tints.TryGetValue(path, out var tint) ? tint : ZepTagColor.None;
                });
                yield return null;
                tree.Apply("Assets", "Assets");
                yield return null;

                var list = tree.Q<ListView>("zep-folder-list-assets");
                var row = RowFor(tree, BranchPath);
                var icon = row.Q<VisualElement>("zep-folder-row-icon-image");
                var paintedTint = icon.style.unityBackgroundImageTintColor.value;
                refuse = true;

                Assert.DoesNotThrow(() => list.bindItem(row, 1));
                Assert.That(icon.style.unityBackgroundImageTintColor.value, Is.EqualTo(paintedTint));

                Assert.DoesNotThrow(() => list.bindItem(row, 2));
                Assert.That(icon.ClassListContains(ZepUiClasses.ThemedFolderIcon), Is.True);
                Assert.That(icon.style.unityBackgroundImageTintColor.keyword, Is.EqualTo(StyleKeyword.Null));

                var newRow = list.makeItem();
                Assert.DoesNotThrow(() => list.bindItem(newRow, 1));
                var newIcon = newRow.Q<VisualElement>("zep-folder-row-icon-image");
                Assert.That(newIcon.ClassListContains(ZepUiClasses.ThemedFolderIcon), Is.True);
                Assert.That(newIcon.style.unityBackgroundImageTintColor.keyword, Is.EqualTo(StyleKeyword.Null));

                refuse = false;
                list.bindItem(row, 1);
                Assert.That(icon.style.unityBackgroundImageTintColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Red)));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator RecoveryTreeTint_FailureHandlingIsNarrow()
        {
            var unexpected = false;
            var window = CreateWindow();
            try
            {
                var tree = MountTree(window, _ =>
                {
                    if (unexpected)
                    {
                        throw new InvalidOperationException("unexpected tint failure");
                    }

                    return ZepTagColor.Red;
                });
                yield return null;
                tree.Apply("Assets", "Assets");
                yield return null;

                var list = tree.Q<ListView>("zep-folder-list-assets");
                var row = RowFor(tree, BranchPath);
                var icon = row.Q<VisualElement>("zep-folder-row-icon-image");
                var publishedTint = icon.style.unityBackgroundImageTintColor.value;
                unexpected = true;

                Assert.Throws<InvalidOperationException>(() => list.bindItem(row, 1));
                Assert.Throws<InvalidOperationException>(tree.RefreshRealizedFolderTints);
                Assert.That(icon.style.unityBackgroundImageTintColor.value, Is.EqualTo(publishedTint));

                var nullTree = MountTree(window);
                yield return null;
                nullTree.Apply("Assets", "Assets");
                yield return null;
                var nullIcon = RowFor(nullTree, BranchPath).Q<VisualElement>("zep-folder-row-icon-image");
                Assert.That(nullIcon.ClassListContains(ZepUiClasses.ThemedFolderIcon), Is.True);
                Assert.That(nullIcon.style.unityBackgroundImageTintColor.keyword, Is.EqualTo(StyleKeyword.Null));
            }
            finally
            {
                window.Close();
            }
        }

        private FolderTreeWidget MountTree(
            EditorWindow window,
            Func<string, ZepTagColor> folderTintQuery = null)
        {
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                LazyChildren,
                folderTintQuery: folderTintQuery);
            var sidebarScroll = new ScrollView();
            sidebarScroll.style.height = 400f;
            sidebarScroll.Add(tree);
            window.rootVisualElement.Add(sidebarScroll);
            return tree;
        }

        private static CatalogDeltaTestWindow CreateWindow()
        {
            var window = ScriptableObject.CreateInstance<CatalogDeltaTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 500f);
            window.Show();
            return window;
        }

        private IReadOnlyList<ZepFolderEntry> LazyChildren(string parent)
        {
            parent ??= string.Empty;
            return _children.TryGetValue(parent, out var children)
                ? children
                : (IReadOnlyList<ZepFolderEntry>)Array.Empty<ZepFolderEntry>();
        }

        private static AssetCatalogDelta DeltaFor(string parentPath)
        {
            var delta = new AssetCatalogDelta(previousRecordVersion: 0);
            delta.AffectedParentFolders.Add(parentPath);
            return delta;
        }

        private static List<ZepFolderEntry> Entries(
            params (string Name, string Path, int Depth)[] entries)
        {
            var list = new List<ZepFolderEntry>(entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                list.Add(Entry(entries[i].Name, entries[i].Path, entries[i].Depth));
            }

            return list;
        }

        private static ZepFolderEntry Entry(string name, string path, int depth)
        {
            return new ZepFolderEntry(name, path, depth, false);
        }

        private static void SendTreeKey(FolderTreeWidget tree, KeyCode keyCode)
        {
            using var evt = KeyDownEvent.GetPooled(
                new Event { type = EventType.KeyDown, keyCode = keyCode });
            tree.HandleListKeyDown(evt);
        }

        private static Button Disclosure(FolderTreeWidget tree, string folderPath)
        {
            var row = RowFor(tree, folderPath);
            Assert.That(row, Is.Not.Null, "the folder must have a realized row: " + folderPath);
            return row.Q<Button>("zep-folder-row-disclosure");
        }

        private static VisualElement RowFor(FolderTreeWidget tree, string folderPath)
        {
            var list = tree.Q<ListView>("zep-folder-list-assets");
            var source = list.itemsSource;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] is ZepFolderEntry entry
                    && string.Equals(entry.Path, folderPath, StringComparison.Ordinal))
                {
                    return list.GetRootElementForIndex(i);
                }
            }

            return null;
        }

        private sealed class CatalogDeltaTestWindow : EditorWindow
        {
        }
    }
}
