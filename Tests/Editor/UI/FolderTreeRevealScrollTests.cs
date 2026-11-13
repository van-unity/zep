using System;
using System.Collections;
using System.Collections.Generic;
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
    // The sidebar's reveal-scroll retries for up to ten frames, because the
    // target row's geometry is not final until the grown ListView has been
    // through layout. That window is long enough for an ordinary refresh tick
    // to reshuffle the tree underneath it.
    public sealed class FolderTreeRevealScrollTests
    {
        private const string ExpansionKey = "Zep.FolderTreeExpanded.Assets";
        private const string TargetPath = "Assets/F30";
        private const float ViewportHeight = 100f;

        private string _previousExpansion;

        [SetUp]
        public void SetUp()
        {
            _previousExpansion = SessionState.GetString(ExpansionKey, null);
            SessionState.EraseString(ExpansionKey);
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
        public IEnumerator RevealScroll_WhenFoldersShiftMidRetry_FramesTheRevealedFolder()
        {
            // The tree's own children source, mutated below exactly the way an
            // external folder change mutates the catalog behind it.
            var assetsChildren = BuildChildren(prefix: "F", count: 40);
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                parent => ChildrenOfAssets(parent, assetsChildren));
            var sidebarScroll = new ScrollView();
            sidebarScroll.style.height = ViewportHeight;
            sidebarScroll.Add(tree);

            var window = ScriptableObject.CreateInstance<RevealScrollTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            window.rootVisualElement.Add(sidebarScroll);
            try
            {
                yield return null;

                tree.Apply("Assets", "Assets");
                var deadline = EditorApplication.timeSinceStartup + 5d;
                var initialLayoutReady = false;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var rootRow = RowFor(tree, "Assets");
                    var initialViewport = sidebarScroll.contentViewport.worldBound;
                    initialLayoutReady = rootRow != null
                        && initialViewport.height > 0f
                        && tree.worldBound.height > initialViewport.height
                        && sidebarScroll.verticalScroller.highValue > 0f
                        && rootRow.worldBound.yMin >= initialViewport.yMin - 1f
                        && rootRow.worldBound.yMax <= initialViewport.yMax + 1f;
                    if (initialLayoutReady) break;
                    window.Repaint();
                    yield return null;
                }

                Assert.That(initialLayoutReady, Is.True,
                    "The root must be realized and the outer scroll range laid out before scheduling the reveal.");
                Assert.That(sidebarScroll.scrollOffset.y, Is.Zero);

                // Navigate deep enough that the row starts far below the
                // viewport — this schedules the retry.
                tree.Apply(TargetPath, TargetPath);

                // Same tick, before the retry runs: an external folder change
                // pushes ten rows in above the target. Nothing reschedules the
                // reveal, because neither the current nor the selected folder
                // changed.
                assetsChildren.InsertRange(0, BuildChildren(prefix: "A", count: 10));
                var delta = new AssetCatalogDelta(previousRecordVersion: 0);
                delta.AffectedParentFolders.Add("Assets");
                tree.ApplyCatalogDelta(delta);
                Assert.That(
                    tree.VisibleFolderCount, Is.EqualTo(51),
                    "the ten inserted folders must reach the tree through its own "
                    + "children query — otherwise nothing shifts and the reveal is "
                    + "never asked to follow the folder");

                // Batchmode yields can outrun native panel updates. Repaint
                // until the existing reveal frames its target or the bounded
                // deadline expires, without issuing another reveal or scroll.
                deadline = EditorApplication.timeSinceStartup + 5d;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var revealedRow = RowFor(tree, TargetPath);
                    var currentViewport = sidebarScroll.contentViewport.worldBound;
                    if (revealedRow != null
                        && revealedRow.worldBound.yMin >= currentViewport.yMin - 1f
                        && revealedRow.worldBound.yMax <= currentViewport.yMax + 1f)
                    {
                        break;
                    }

                    window.Repaint();
                    yield return null;
                }

                var row = RowFor(tree, TargetPath);
                Assert.That(row, Is.Not.Null, "the revealed folder must still have a realized row");
                var viewport = sidebarScroll.contentViewport.worldBound;
                Assert.That(
                    row.worldBound.yMin, Is.GreaterThanOrEqualTo(viewport.yMin - 1f),
                    "the reveal must follow the folder, not the row index it had when it was scheduled");
                Assert.That(row.worldBound.yMax, Is.LessThanOrEqualTo(viewport.yMax + 1f));
            }
            finally
            {
                window.Close();
            }
        }

        // 5.24: one keyboard navigation reaches the reveal twice — the widget
        // tells the shell about the new folder, the shell's refresh comes back
        // through Apply and schedules the retry, and the widget then schedules
        // again on its way out. The second loop throws away the first one's
        // attempt budget for the very same row.
        [UnityTest]
        public IEnumerator LazyKeyboardNavigation_SchedulesOneRevealPerMove()
        {
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                LazyChildren);
            var sidebarScroll = new ScrollView();
            sidebarScroll.style.height = ViewportHeight;
            sidebarScroll.Add(tree);

            var window = ScriptableObject.CreateInstance<RevealScrollTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            window.rootVisualElement.Add(sidebarScroll);
            try
            {
                yield return null;

                tree.Apply("Assets/L00", "Assets/L00");

                // What the shell does with a keyboard move: it republishes the
                // new selection back into the tree, synchronously, before the
                // widget has finished handling the key.
                tree.FolderClicked += path => tree.Apply(path, path);
                yield return null;

                var scheduled = FolderTreeWidget.RevealScheduleCountForTests;
                SendTreeKey(tree, KeyCode.DownArrow);

                Assert.That(
                    FolderTreeWidget.RevealScheduleCountForTests - scheduled,
                    Is.EqualTo(1),
                    "one navigation must leave one retry loop, not two");

                scheduled = FolderTreeWidget.RevealScheduleCountForTests;
                SendTreeKey(tree, KeyCode.DownArrow);

                Assert.That(
                    FolderTreeWidget.RevealScheduleCountForTests - scheduled,
                    Is.EqualTo(1),
                    "a move to a different folder still needs its own reveal");
            }
            finally
            {
                window.Close();
            }
        }

        // Root plus ten flat children, materialized on demand: the lazy shape
        // the sidebar runs in production.
        private static IReadOnlyList<ZepFolderEntry> LazyChildren(string parent)
        {
            parent ??= string.Empty;
            if (parent.Length == 0)
            {
                return new[]
                {
                    new ZepFolderEntry("Assets", "Assets", 0, false),
                };
            }

            if (!string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<ZepFolderEntry>();
            }

            var children = new List<ZepFolderEntry>();
            for (var i = 0; i < 10; i++)
            {
                var name = $"L{i:D2}";
                children.Add(new ZepFolderEntry(name, "Assets/" + name, 1, false));
            }

            return children;
        }

        private static void SendTreeKey(FolderTreeWidget tree, KeyCode keyCode)
        {
            using var evt = KeyDownEvent.GetPooled(
                new Event { type = EventType.KeyDown, keyCode = keyCode });
            tree.HandleListKeyDown(evt);
        }

        // 6.10: every navigation that reveals an ancestor used to serialise the
        // WHOLE expansion set into SessionState immediately — ten reveals in one
        // burst wrote it ten times. The set is still updated eagerly; only the
        // write is coalesced, and it must not be lost.
        [Test]
        public void ExpansionSave_AcrossABurstOfReveals_WritesSessionStateOnce()
        {
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(BuildNestedFolders(branches: 10)));

            for (var i = 0; i < 10; i++)
            {
                var revealed = $"Assets/D{i:D2}/Sub";
                tree.Apply(revealed, revealed);
            }

            Assert.That(
                tree.ExpansionStateWriteCount,
                Is.Zero,
                "the burst must leave exactly one write pending, not ten done");

            tree.FlushPendingExpandedState();

            Assert.That(tree.ExpansionStateWriteCount, Is.EqualTo(1));
            var saved = SessionState.GetString(ExpansionKey, string.Empty);
            for (var i = 0; i < 10; i++)
            {
                Assert.That(
                    saved, Does.Contain($"Assets/D{i:D2}"),
                    "the single write must carry every branch the burst expanded");
            }

            tree.FlushPendingExpandedState();
            Assert.That(
                tree.ExpansionStateWriteCount,
                Is.EqualTo(1),
                "a flush with nothing pending must not write");
        }

        // The lazy children query the sidebar runs in production, served from a
        // flat fixture array: the children of a path are the entries whose
        // parent is that path, and the empty key holds the root itself.
        private static Func<string, IReadOnlyList<ZepFolderEntry>> ChildrenQuery(
            IReadOnlyList<ZepFolderEntry> folders)
        {
            var byParent = new Dictionary<string, List<ZepFolderEntry>>(
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                var parent = AssetPathFormat.GetParentFolderPath(folder.Path);
                if (!byParent.TryGetValue(parent, out var children))
                {
                    children = new List<ZepFolderEntry>();
                    byParent[parent] = children;
                }

                children.Add(folder);
            }

            return parent => byParent.TryGetValue(parent ?? string.Empty, out var children)
                ? children
                : (IReadOnlyList<ZepFolderEntry>)Array.Empty<ZepFolderEntry>();
        }

        // Root, ten branches, one child each — navigating into a child reveals
        // (and so expands) its branch.
        private static List<ZepFolderEntry> BuildNestedFolders(int branches)
        {
            var folders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, false)
            };

            for (var i = 0; i < branches; i++)
            {
                var branch = $"Assets/D{i:D2}";
                folders.Add(new ZepFolderEntry($"D{i:D2}", branch, 1, false));
                folders.Add(new ZepFolderEntry("Sub", branch + "/Sub", 2, false));
            }

            return folders;
        }

        private static VisualElement RowFor(FolderTreeWidget tree, string folderPath)
        {
            var list = tree.Q<ListView>("zep-folder-list-assets");
            var source = list.itemsSource;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] is ZepFolderEntry entry
                    && string.Equals(entry.Path, folderPath, System.StringComparison.Ordinal))
                {
                    return list.GetRootElementForIndex(i);
                }
            }

            return null;
        }

        // A flat run of root children. Prepending a second run ahead of it
        // moves the target folder's row index without changing its path —
        // exactly what an external folder add does mid-retry.
        private static List<ZepFolderEntry> BuildChildren(string prefix, int count)
        {
            var folders = new List<ZepFolderEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var name = $"{prefix}{i:D2}";
                folders.Add(new ZepFolderEntry(name, "Assets/" + name, 1, false));
            }

            return folders;
        }

        private static IReadOnlyList<ZepFolderEntry> ChildrenOfAssets(
            string parent,
            List<ZepFolderEntry> assetsChildren)
        {
            parent ??= string.Empty;
            if (parent.Length == 0)
            {
                return new[] { new ZepFolderEntry("Assets", "Assets", 0, false) };
            }

            return string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase)
                ? assetsChildren
                : (IReadOnlyList<ZepFolderEntry>)Array.Empty<ZepFolderEntry>();
        }

        private sealed class RevealScrollTestWindow : EditorWindow
        {
        }
    }
}
