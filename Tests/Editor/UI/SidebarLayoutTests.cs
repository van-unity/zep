using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Editor.UI;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    public sealed class SidebarLayoutTests
    {
        private const string ExpansionKey = "Zep.FolderTreeExpanded.Assets";
        private const string TaggedPath = "Assets/Editor";
        // The height the pane host is pinned to in the resolved-geometry tests.
        private const float PaneHostHeight = 260f;
        private const string PlainPath = "Assets/Plugins";

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

        [Test]
        public void Sections_AreFavoritesTags_ThenTheTwoTreesAsSiblings_InOneScrollView()
        {
            var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false);
            var scroll = sidebar.Q<ScrollView>("zep-sidebar-scroll");
            Assert.That(scroll, Is.Not.Null);

            Assert.That(sidebar.Children().ToArray(), Is.EqualTo(new VisualElement[] { scroll }));
            var sections = new List<string>();
            foreach (var child in scroll.contentContainer.Children())
            {
                sections.Add(child.name);
            }

            Assert.That(sections, Is.EqualTo(new[]
            {
                "zep-sidebar-favorites",
                "zep-sidebar-tags",
                "zep-folder-tree-assets",
                "zep-folder-tree-packages",
            }), "Favorites, Tags, the Assets tree, the Packages tree — in that order, all siblings");

            var titles = new List<string>();
            foreach (var header in sidebar.Query<SidebarSectionHeader>().ToList())
            {
                titles.Add(header.Q<Label>(className: ZepUiClasses.SidebarSectionLabel).text);
                Assert.That(header.Q<VisualElement>(className: ZepUiClasses.RowIcon), Is.Null,
                    "section headers are labels only, no glyph");
            }

            Assert.That(titles, Is.EqualTo(new[] { "Favorites", "Tags" }),
                "only Favorites and Tags carry a header — the trees are their own roots");
        }

        // Assets and Packages are consecutive roots of one location list, so the
        // Packages tree drops the section gap and continues at the plain row
        // pitch. USS has no sibling combinator, so "follows another section" is
        // a class the composing widget sets.
        [Test]
        public void PackagesTree_IsAttachedToAssets_SoNoSectionGapSitsBetweenThem()
        {
            var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false);
            var assets = sidebar.Q<VisualElement>("zep-folder-tree-assets");
            var packages = sidebar.Q<VisualElement>("zep-folder-tree-packages");

            Assert.That(assets.ClassListContains(ZepUiClasses.SidebarSectionAttached), Is.False,
                "Assets opens its own block under Tags and keeps the section gap");
            Assert.That(packages.ClassListContains(ZepUiClasses.SidebarSectionAttached), Is.True,
                "Packages continues the Assets block, so the gap above it is dropped");
            Assert.That(packages.ClassListContains(ZepUiClasses.SidebarSection), Is.True,
                "attached or not, it is still a section and keeps the right gutter");
        }

        // The no-gap contract in resolved terms: Assets opens a block under
        // Tags and keeps the section gap; Packages continues it at the plain
        // row pitch, like the rows inside Favorites and Tags.
        [UnityTest]
        public IEnumerator PackagesTree_ResolvesToNoTopMargin_WhileAssetsKeepsTheSectionGap()
        {
            var structural = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            var defaults = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss");
            Assert.That(structural, Is.Not.Null, "the structural sheet must be loadable");

            var window = ScriptableObject.CreateInstance<SidebarLayoutTestWindow>();
            window.position = new Rect(100f, 100f, 400f, 500f);
            window.Show();
            try
            {
                var root = new VisualElement();
                root.AddToClassList(ZepUiClasses.Root);
                root.styleSheets.Add(structural);
                root.styleSheets.Add(defaults);
                root.style.flexDirection = FlexDirection.Row;
                root.style.height = PaneHostHeight;

                var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false);
                root.Add(sidebar);
                window.rootVisualElement.Add(root);
                yield return null;
                yield return null;

                var assets = sidebar.Q<VisualElement>("zep-folder-tree-assets");
                var packages = sidebar.Q<VisualElement>("zep-folder-tree-packages");

                Assert.That(assets.resolvedStyle.marginTop, Is.EqualTo(14f).Within(0.5f),
                    "Assets opens a new block under Tags, so it keeps the section gap");
                Assert.That(packages.resolvedStyle.marginTop, Is.EqualTo(0f).Within(0.5f),
                    "Packages continues the Assets block — no gap, so the two roots meet at the "
                        + "plain row pitch the rows inside Favorites and Tags already have");
            }
            finally
            {
                window.Close();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void TagsHeader_Toggle_FoldsTheTagList()
        {
            var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false);
            var header = sidebar.Q<SidebarSectionHeader>("zep-tag-filter-row");
            var list = sidebar.Q<VisualElement>("zep-sidebar-tags-list");
            var toggle = header.Q<Label>(className: ZepUiClasses.SidebarSectionToggle);

            Assert.That(header.IsExpanded, Is.True);
            Assert.That(toggle.text, Is.EqualTo("Hide"));

            header.ToggleExpanded();
            Assert.That(list.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(toggle.text, Is.EqualTo("Show"));

            header.ToggleExpanded();
            Assert.That(list.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(toggle.text, Is.EqualTo("Hide"));
        }

        [UnityTest]
        public IEnumerator FolderTree_PaintsATaggedFoldersIcon_WithItsTagColor_AndFollowsTagChanges()
        {
            var window = ScriptableObject.CreateInstance<SidebarLayoutTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tints = new Dictionary<string, ZepTagColor>(StringComparer.OrdinalIgnoreCase)
                {
                    [TaggedPath] = ZepTagColor.Red,
                };
                var tree = new FolderTreeWidget(
                    "Assets",
                    new UnityZepIconRepository(),
                    ChildrenQuery(BuildFolders()),
                    folderEntryQuery: null,
                    folderTintQuery: path => tints.TryGetValue(path, out var color)
                        ? color
                        : ZepTagColor.None);
                window.rootVisualElement.Add(tree);
                yield return null;

                tree.Apply(TaggedPath, TaggedPath);
                yield return null;
                yield return null;

                var taggedIcon = IconOf(tree, TaggedPath);
                var plainIcon = IconOf(tree, PlainPath);
                Assert.That(taggedIcon.ClassListContains(ZepUiClasses.ThemedFolderIcon), Is.False,
                    "a tagged folder leaves the theme tint for its tag color");
                Assert.That(
                    taggedIcon.style.unityBackgroundImageTintColor.value,
                    Is.EqualTo((Color)ZepTagColorPalette.GetColor(ZepTagColor.Red)),
                    "the tree paints the same tag color Favorites and the assets area do");
                Assert.That(plainIcon.ClassListContains(ZepUiClasses.ThemedFolderIcon), Is.True,
                    "an untagged folder keeps the theme tint");

                // Removing the tag changes nothing Apply diffs on, so the
                // sidebar re-tints realized rows on its flush.
                tints.Remove(TaggedPath);
                tree.RefreshRealizedFolderTints();
                Assert.That(taggedIcon.ClassListContains(ZepUiClasses.ThemedFolderIcon), Is.True,
                    "untagging must put the theme tint back without a rebind");
            }
            finally
            {
                window.Close();
            }
        }

        // Session state can bring a root back collapsed while a deeper folder
        // stays recorded as expanded (the user opened Assets/Editor, then
        // folded Assets, then the domain reloaded). The root's child bucket
        // was never loaded in that state, so it drew no chevron and the tree
        // could not be expanded with the mouse.
        [UnityTest]
        public IEnumerator CollapsedRoot_RestoredFromSessionState_StillShowsItsChevron_AndExpandsOnClick()
        {
            SessionState.SetString(ExpansionKey, "Assets/Editor");
            var window = ScriptableObject.CreateInstance<SidebarLayoutTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tree = new FolderTreeWidget(
                    "Assets",
                    new UnityZepIconRepository(),
                    ChildrenQuery(BuildFolders()));
                window.rootVisualElement.Add(tree);
                yield return null;

                tree.Apply("Assets", "Assets");
                yield return null;
                yield return null;

                Assert.That(tree.VisibleFolderCount, Is.EqualTo(1), "the root came back collapsed");
                var list = tree.Q<ListView>("zep-folder-list-assets");
                var root = list.GetRootElementForIndex(0);
                var chevron = root.Q<Button>("zep-folder-row-disclosure");
                Assert.That(chevron.style.visibility.value, Is.EqualTo(Visibility.Visible),
                    "a collapsed root with children must still offer its chevron");
                Assert.That(chevron.pickingMode, Is.EqualTo(PickingMode.Position));

                using (var evt = new NavigationSubmitEvent { target = chevron })
                {
                    chevron.SendEvent(evt);
                }

                yield return null;
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(3),
                    "activating the chevron expands the root again");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator CollapsedRoot_UnfoldingRestoresItsSavedExpandedDescendantRows()
        {
            const string branch = "Assets/Branch";
            const string inner = "Assets/Branch/Inner";
            const string leaf = "Assets/Branch/Inner/Leaf";
            SessionState.SetString(ExpansionKey, "\n" + branch + "\n" + inner);
            var window = ScriptableObject.CreateInstance<SidebarLayoutTestWindow>();
            window.position = new Rect(100f, 100f, 320f, 450f);
            window.Show();
            try
            {
                var tree = new FolderTreeWidget(
                    "Assets",
                    new UnityZepIconRepository(),
                    ChildrenQuery(new[]
                    {
                        new ZepFolderEntry("Assets", "Assets", 0, false),
                        new ZepFolderEntry("Branch", branch, 1, false),
                        new ZepFolderEntry("Inner", inner, 2, false),
                        new ZepFolderEntry("Leaf", leaf, 3, false),
                        new ZepFolderEntry("Sibling", "Assets/Sibling", 1, false)
                    }));
                window.rootVisualElement.Add(tree);
                yield return null;
                tree.Apply("Assets", "Assets");
                yield return null;
                yield return null;
                Assert.That(tree.VisibleFolderCount, Is.EqualTo(1), "the restored root starts collapsed");
                var list = tree.Q<ListView>("zep-folder-list-assets");
                var root = list.GetRootElementForIndex(0);
                Assert.That(root, Is.Not.Null);
                Assert.That(root.Q<Button>("zep-folder-row-disclosure").style.visibility.value,
                    Is.EqualTo(Visibility.Visible));

                using (var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.RightArrow }))
                {
                    tree.HandleListKeyDown(evt);
                }

                for (var frame = 0; frame < 10; frame++) yield return null;

                // IconOf resolves the actual realized row and fails if it is
                // absent; a count alone would not prove which descendants show.
                Assert.That(IconOf(tree, inner), Is.Not.Null,
                    "saved-expanded Branch must expose its Inner child when the root unfolds");
                Assert.That(IconOf(tree, leaf), Is.Not.Null,
                    "saved-expanded Inner must expose its Leaf child without another navigation");
            }
            finally
            {
                // Detach flushes pending expansion persistence before TearDown
                // restores the SessionState value that preceded this test.
                window.Close();
            }
        }

        // The Packages tree sits below an expanded Assets tree. In a short
        // sidebar it starts fully below the viewport and, correctly, binds no
        // rows. Collapsing Assets pulls it into view without any scroll or
        // viewport resize — the only two signals the tree used to watch — so
        // it stayed blank: a gap where the Packages root should be.
        [UnityTest]
        public IEnumerator PackagesTree_PulledIntoViewByCollapsingAssets_BindsItsRows()
        {
            SessionState.EraseString("Zep.FolderTreeExpanded.Packages");
            var window = ScriptableObject.CreateInstance<SidebarLayoutTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var folders = new List<ZepFolderEntry>
                {
                    new ZepFolderEntry("Assets", "Assets", 0, false),
                    new ZepFolderEntry("Packages", "Packages", 0, false),
                    new ZepFolderEntry("com.x", "Packages/com.x", 1, false),
                };
                for (var i = 0; i < 20; i++)
                {
                    folders.Add(new ZepFolderEntry("F" + i, "Assets/F" + i, 1, false));
                }

                var query = ChildrenQuery(folders);
                var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "zep-sidebar-scroll" };
                scroll.style.height = 120f;
                var assets = new FolderTreeWidget("Assets", new UnityZepIconRepository(), query);
                var packages = new FolderTreeWidget("Packages", new UnityZepIconRepository(), query);
                scroll.Add(assets);
                scroll.Add(packages);
                window.rootVisualElement.Add(scroll);
                yield return null;

                assets.Apply("Assets", "Assets");
                packages.Apply("Assets", "Assets");
                var list = packages.Q<ListView>("zep-folder-list-packages");
                var deadline = EditorApplication.timeSinceStartup + 5d;
                var initialLayoutReady = false;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var viewport = scroll.contentViewport.worldBound;
                    initialLayoutReady = Mathf.Abs(viewport.height - 120f) < 1f
                        && assets.worldBound.height > viewport.height
                        && packages.worldBound.yMin > viewport.yMax
                        && packages.BoundFolderCount == 0;
                    if (initialLayoutReady) break;
                    window.Repaint();
                    yield return null;
                }

                Assert.That(initialLayoutReady, Is.True,
                    "Native layout must place Packages below the 120px viewport and finish its scheduled slice refresh.");
                Assert.That(scroll.scrollOffset.y, Is.Zero);
                Assert.That(assets.VisibleFolderCount, Is.EqualTo(21), "Assets is expanded with 20 children");
                Assert.That(packages.BoundFolderCount, Is.Zero,
                    "fully below the 120px viewport, the Packages tree correctly binds nothing yet");

                using (var evt = KeyDownEvent.GetPooled(
                    new Event { type = EventType.KeyDown, keyCode = KeyCode.LeftArrow }))
                {
                    assets.HandleListKeyDown(evt);
                }

                // Enumerator yields can outrun native layout and scheduled
                // viewport refreshes in batchmode. Repaint until the single
                // collapse brings the realized root into view, without another
                // tree action or an explicit scroll/refresh from this test.
                deadline = EditorApplication.timeSinceStartup + 5d;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var realizedRoot = list.GetRootElementForIndex(0);
                    var viewport = scroll.contentViewport.worldBound;
                    if (packages.BoundFolderCount > 0
                        && realizedRoot != null
                        && realizedRoot.worldBound.height > 0f
                        && realizedRoot.worldBound.yMin >= viewport.yMin - 1f
                        && realizedRoot.worldBound.yMax <= viewport.yMax + 1f)
                    {
                        break;
                    }

                    window.Repaint();
                    yield return null;
                }

                Assert.That(assets.VisibleFolderCount, Is.EqualTo(1), "Assets collapsed to its root");
                Assert.That(packages.BoundFolderCount, Is.GreaterThan(0),
                    "pulled into view, the Packages tree must bind its rows — it used to stay blank");
                var root = list.GetRootElementForIndex(0);
                Assert.That(root, Is.Not.Null, "the Packages root row must be realized");
                Assert.That(root.worldBound.height, Is.GreaterThan(0f));
                var finalViewport = scroll.contentViewport.worldBound;
                Assert.That(root.worldBound.yMin, Is.GreaterThanOrEqualTo(finalViewport.yMin - 1f));
                Assert.That(root.worldBound.yMax, Is.LessThanOrEqualTo(finalViewport.yMax + 1f));
                Assert.That(
                    root.Q<Label>(className: ZepUiClasses.RowPrimaryText).text,
                    Is.EqualTo("Packages"));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator FocusAndKeyboardClasses_FollowFocusAndInput()
        {
            var window = ScriptableObject.CreateInstance<SidebarLayoutTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var root = window.rootVisualElement;
                var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false);
                var outside = new Button { name = "outside" };
                root.Add(sidebar);
                root.Add(outside);
                yield return null;

                Assert.That(sidebar.ClassListContains(ZepUiClasses.SidebarPaneFocused), Is.False,
                    "nothing is focused yet");

                var tree = sidebar.Q<ListView>("zep-folder-list-assets");
                tree.Focus();
                yield return null;
                Assert.That(sidebar.ClassListContains(ZepUiClasses.SidebarPaneFocused), Is.True,
                    "focus inside the sidebar turns the selection accent on");
                Assert.That(sidebar.ClassListContains(ZepUiClasses.SidebarPaneKeyboard), Is.False,
                    "focusing alone is not keyboard navigation");

                using (var key = KeyDownEvent.GetPooled(
                    new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow }))
                {
                    key.target = tree;
                    sidebar.SendEvent(key);
                }

                Assert.That(sidebar.ClassListContains(ZepUiClasses.SidebarPaneKeyboard), Is.True,
                    "a navigation key arms the focus ring");

                using (var press = PointerDownEvent.GetPooled(
                    new Event { type = EventType.MouseDown, button = 0, mousePosition = tree.worldBound.center }))
                {
                    press.target = tree;
                    sidebar.SendEvent(press);
                }

                using (var release = PointerUpEvent.GetPooled(
                    new Event { type = EventType.MouseUp, button = 0, mousePosition = tree.worldBound.center }))
                {
                    release.target = tree;
                    sidebar.SendEvent(release);
                }

                Assert.That(sidebar.ClassListContains(ZepUiClasses.SidebarPaneKeyboard), Is.False,
                    "a pointer press disarms the ring");

                outside.Focus();
                yield return null;
                Assert.That(sidebar.ClassListContains(ZepUiClasses.SidebarPaneFocused), Is.False,
                    "focus leaving the sidebar drops the selection to neutral");
            }
            finally
            {
                window.Close();
            }
        }

        private static VisualElement IconOf(FolderTreeWidget tree, string folderPath)
        {
            var list = tree.Q<ListView>("zep-folder-list-assets");
            var source = list.itemsSource;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] is ZepFolderEntry entry
                    && string.Equals(entry.Path, folderPath, StringComparison.Ordinal))
                {
                    var row = list.GetRootElementForIndex(i);
                    Assert.That(row, Is.Not.Null, "the folder must have a realized row: " + folderPath);
                    return row.Q<VisualElement>("zep-folder-row-icon-image");
                }
            }

            Assert.Fail("no row for " + folderPath);
            return null;
        }

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

        private static List<ZepFolderEntry> BuildFolders()
        {
            return new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Editor", TaggedPath, 1, false),
                new ZepFolderEntry("Plugins", PlainPath, 1, false)
            };
        }

        private sealed class SidebarLayoutTestWindow : EditorWindow
        {
        }
    }
}
