using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;
using Zep.Editor.UI;
using Zep.Editor.UI.TopBar;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.AssetsArea.Substrate;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // Choosing a search result reveals its containing folder in both the assets
    // view and the sidebar. Dismissing search alone never navigates.
    public sealed class ZepSearchExitSidebarSelectionTests
    {
        private const string AssetsTreeExpansionKey = "Zep.FolderTreeExpanded.Assets";
        private const string PackagesTreeExpansionKey = "Zep.FolderTreeExpanded.Packages";
        private string _previousAssetsTreeExpansion;
        private string _previousPackagesTreeExpansion;
        private EditorWindow _searchHost;

        [SetUp]
        public void SetUp()
        {
            _previousAssetsTreeExpansion = SessionState.GetString(AssetsTreeExpansionKey, null);
            _previousPackagesTreeExpansion = SessionState.GetString(PackagesTreeExpansionKey, null);
            SessionState.EraseString(AssetsTreeExpansionKey);
            SessionState.EraseString(PackagesTreeExpansionKey);
        }

        [TearDown]
        public void TearDown()
        {
            if (_searchHost != null) _searchHost.Close();
            if (_previousAssetsTreeExpansion != null)
            {
                SessionState.SetString(AssetsTreeExpansionKey, _previousAssetsTreeExpansion);
            }
            else
            {
                SessionState.EraseString(AssetsTreeExpansionKey);
            }

            if (_previousPackagesTreeExpansion != null)
            {
                SessionState.SetString(PackagesTreeExpansionKey, _previousPackagesTreeExpansion);
            }
            else
            {
                SessionState.EraseString(PackagesTreeExpansionKey);
            }
        }

        // The shell's result-selection handler, driven straight through with
        // nothing else running: the controller is here only because the sidebar
        // trees hydrate through its folder queries (§6.6b removed the pushed
        // folder snapshot), and its scheduler is deliberately never pumped, so
        // every assertion below is the work the handler did synchronously.
        [Test]
        public void SearchResultSelection_SelectsContainingFolderInSidebar()
        {
            using (var rig = BuildControllerRig())
            {
                var pane = rig.State.PrimaryPane;
                var shell = rig.BuildShell();

                // User browses in Assets (root), so the sidebar starts selected there.
                InvokeShellMethod(shell, "OnFolderPaneFolderSelected", "Assets");
                Assert.That(pane.SelectedFolderPath.Value, Is.EqualTo("Assets"));

                // Type a search, then pick a result that lives in a folder the
                // sidebar has never revealed (Icons sits two collapsed levels deep).
                OpenSearch(shell);
                InvokeShellMethod(shell, "OnSearchTextChanged", "Play");

                // Choosing the result closes the dialog and reveals it synchronously.
                SelectFirstResult(shell);

                Assert.That(pane.SearchText, Is.Empty);
                Assert.That(
                    pane.SelectedAssetPaths,
                    Is.EqualTo(new[] { AssetPath.From("Assets/Art/UI/Icons/Play.png") }),
                    "The chosen result must be selected in the assets view.");
                Assert.That(
                    pane.CurrentFolderPath.Value,
                    Is.EqualTo("Assets/Art/UI/Icons"),
                    "Choosing the result must navigate to its containing folder.");
                Assert.That(
                    pane.SelectedFolderPath.Value,
                    Is.EqualTo("Assets/Art/UI/Icons"),
                    "The pane must record the containing folder as the sidebar selection.");

                var selected = shell.SelectedSidebarFolder;
                Assert.That(selected, Is.Not.Null, "Sidebar tree must have a selected row after choosing a result.");
                Assert.That(
                    selected.Path,
                    Is.EqualTo("Assets/Art/UI/Icons"),
                    "Sidebar tree must select the folder containing the picked result.");
            }
        }

        // Same scenario, but through the REAL controller stack (projection,
        // reconciliation, selection sync) so a production-only regression in
        // the reveal → sidebar path cannot hide behind the standalone shell.
        [Test]
        public void SearchResultSelection_FullControllerStack_SelectsContainingFolderInSidebar()
        {
            using (var rig = BuildControllerRig())
            {
                var pane = rig.State.PrimaryPane;
                var shell = rig.BuildShell();

                InvokeShellMethod(shell, "OnFolderPaneFolderSelected", "Assets/Audio");
                rig.Scheduler.RunAll();
                Assert.That(pane.SelectedFolderPath.Value, Is.EqualTo("Assets/Audio"));

                OpenSearch(shell);
                InvokeShellMethod(shell, "OnSearchTextChanged", "Play");
                rig.Scheduler.RunAll();

                SelectFirstResult(shell);
                rig.Scheduler.RunAll();

                Assert.That(pane.SearchText, Is.Empty);
                Assert.That(
                    pane.SelectedAssetPaths,
                    Is.EqualTo(new[] { AssetPath.From("Assets/Art/UI/Icons/Play.png") }),
                    "The chosen result must be selected in the assets view.");
                Assert.That(
                    pane.CurrentFolderPath.Value,
                    Is.EqualTo("Assets/Art/UI/Icons"),
                    "Choosing the result must navigate to its containing folder.");
                Assert.That(
                    pane.SelectedFolderPath.Value,
                    Is.EqualTo("Assets/Art/UI/Icons"),
                    "The pane must record the containing folder as the sidebar selection.");

                var selected = shell.SelectedSidebarFolder;
                Assert.That(selected, Is.Not.Null, "Sidebar tree must have a selected row after choosing a result.");
                Assert.That(
                    selected.Path,
                    Is.EqualTo("Assets/Art/UI/Icons"),
                    "Sidebar tree must select the folder containing the picked result.");
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SearchResultDoubleClick_List_RightClickOpensAssetContextMenu()
            => AssertSearchResultContextMenu(AssetViewMode.List);

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SearchResultDoubleClick_Grid_RightClickOpensAssetContextMenu()
            => AssertSearchResultContextMenu(AssetViewMode.Grid);

        private System.Collections.IEnumerator AssertSearchResultContextMenu(AssetViewMode mode)
        {
            using var rig = BuildControllerRig();
            var shell = rig.BuildShell();
            try
            {
                _searchHost = UnityEngine.ScriptableObject.CreateInstance<SidebarSelectionTestWindow>();
                _searchHost.position = new UnityEngine.Rect(100f, 100f, 900f, 600f);
                _searchHost.rootVisualElement.Add(shell);
                _searchHost.Show();
                _searchHost.Focus();
                for (var frame = 0; frame < 3; frame++) yield return null;
                shell.Q<ZepPaneView>().ApplyPresentation(mode, 100);
                ((IZepShortcutHost)shell).FocusSearch();
                InvokeShellMethod(shell, "OnSearchTextChanged", "Play");
                rig.Scheduler.RunAll();
                for (var frame = 0; frame < 5; frame++) yield return null;

                var searchWindow = (ZepSearchWindow)typeof(ZepShellView)
                    .GetField("_searchWindow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
                Assert.That(searchWindow, Is.Not.Null);
                var result = searchWindow.rootVisualElement.Q(className: ZepUiClasses.SearchSuggestionRow);
                Assert.That(result, Is.Not.Null);
                var point = result.worldBound.center;
                searchWindow.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseUp, button = 0, mousePosition = point
                });
                searchWindow.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseDown, button = 0, clickCount = 1, mousePosition = point
                });
                searchWindow.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseUp, button = 0, mousePosition = point
                });
                Assert.That(searchWindow != null, Is.True, "a single click must leave search open");
                searchWindow.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseDown, button = 0, clickCount = 2, mousePosition = point
                });
                // A window closed on mouse-down cannot receive the release.
                // Send it only while the search window still exists, as native input would.
                if (searchWindow != null)
                    searchWindow.SendEvent(new UnityEngine.Event
                    {
                        type = UnityEngine.EventType.MouseUp, button = 0, mousePosition = point
                    });
                Assert.That(searchWindow == null, Is.True, "double-click must dismiss search and reveal the result");
                rig.Scheduler.RunAll();
                for (var frame = 0; frame < 5; frame++) yield return null;

                const string path = "Assets/Art/UI/Icons/Play.png";
                Assert.That(rig.State.PrimaryPane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From(path) }));
                var browser = shell.Q<AssetsBrowserFeature>();
                var collection = browser.Q<AssetCollectionView>();
                var asset = collection.Query<VisualElement>().ToList().FirstOrDefault(element =>
                    collection.TryGetAssetEntryFromTarget(element, out var entry) && entry.Path == path);
                Assert.That(asset, Is.Not.Null, "the search result must be realized in the Assets view");
                var contextPaths = new List<string>();
                browser.ContextMenuRequested += (_, entry) => contextPaths.Add(entry?.Path);
                point = asset.worldBound.center;
                _searchHost.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseDown, button = 1, clickCount = 1, mousePosition = point
                });
                _searchHost.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseUp, button = 1, mousePosition = point
                });
                Assert.That(contextPaths, Is.EqualTo(new[] { path }),
                    "right-click must request the revealed asset's menu without deselecting it");
                Assert.That(rig.State.PrimaryPane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From(path) }));
                if (!UnityEngine.Application.isBatchMode)
                {
                    for (var frame = 0; frame < 5; frame++) yield return null;
                    Assert.That(ContextMenuPopupRenderer.IsOpen, Is.True,
                        "the asset menu must remain open after the right-click is released");
                }
            }
            finally
            {
                ContextMenuPopupRenderer.CloseCurrent(refocusHost: false);
                _searchHost.SendEvent(new UnityEngine.Event
                {
                    type = UnityEngine.EventType.MouseUp, button = 0, mousePosition = UnityEngine.Vector2.zero
                });
                shell.Teardown();
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SearchResultReveal_List_ScrollsSelectedAssetIntoViewport()
            => AssertSearchResultScroll(AssetViewMode.List);

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SearchResultReveal_Grid_ScrollsSelectedAssetIntoViewport()
            => AssertSearchResultScroll(AssetViewMode.Grid);

        private System.Collections.IEnumerator AssertSearchResultScroll(AssetViewMode mode)
        {
            var records = Enumerable.Range(0, 240).Select(index => new AssetRecord(
                "entry-" + index, $"Assets/Large/Entry{index:D3}.asset", $"Entry{index:D3}", "ScriptableObject"));
            using var rig = ZepUiControllerRig.Build(records,
                new[] { "Assets", "Assets/Before", "Assets/Large", "Packages" });
            var shell = rig.BuildShell();
            try
            {
                InvokeShellMethod(shell, "OnFolderPaneFolderSelected", "Assets/Before");
                rig.Scheduler.RunAll();
                _searchHost = UnityEngine.ScriptableObject.CreateInstance<SidebarSelectionTestWindow>();
                _searchHost.position = new UnityEngine.Rect(100f, 100f, 900f, 400f);
                _searchHost.rootVisualElement.Add(shell);
                _searchHost.Show();
                _searchHost.Focus();
                var paneView = shell.Q<ZepPaneView>();
                paneView.ApplyPresentation(mode, 100);
                for (var frame = 0; frame < 5; frame++) yield return null;

                foreach (var name in new[] { "Entry220", "Entry005" })
                {
                    ((IZepShortcutHost)shell).FocusSearch();
                    InvokeShellMethod(shell, "OnSearchTextChanged", name);
                    SelectFirstResult(shell);
                    rig.Scheduler.RunAll();
                    yield return new UnityEngine.WaitForSecondsRealtime(0.2f);

                    var path = "Assets/Large/" + name + ".asset";
                    Assert.That(rig.State.PrimaryPane.CurrentFolderPath.Value, Is.EqualTo("Assets/Large"));
                    Assert.That(rig.State.PrimaryPane.SelectedAssetPaths, Is.EqualTo(new[] { AssetPath.From(path) }));
                    var collection = paneView.Q<AssetCollectionView>();
                    var scroll = collection.ResolveActiveScrollView();
                    if (name == "Entry220")
                        Assert.That(scroll.scrollOffset.y, Is.GreaterThan(0f), "reveal must scroll beyond the initial viewport");
                    var asset = collection.Query<VisualElement>().ToList().FirstOrDefault(element =>
                        collection.TryGetAssetEntryFromTarget(element, out var entry) && entry.Path == path);
                    Assert.That(asset, Is.Not.Null, "the selected asset must be realized after reveal");
                    var viewport = scroll.contentViewport.worldBound;
                    Assert.That(asset.worldBound.yMin, Is.GreaterThanOrEqualTo(viewport.yMin - 1f),
                        $"{path}: bounds {asset.worldBound}, viewport {viewport}, scroll {scroll.scrollOffset}");
                    Assert.That(asset.worldBound.yMax, Is.LessThanOrEqualTo(viewport.yMax + 1f),
                        "the selected asset must fit inside the Assets viewport after layout settles");
                }
            }
            finally
            {
                shell.Teardown();
            }
        }

        // Attached-window variant: real panel, real layout, real ListView
        // virtualization. Mirrors the exact Apply() transition the sidebar
        // receives when a search is cancelled after picking a result in a
        // folder hidden under collapsed ancestors, and asserts the revealed
        // row is actually REALIZED and PAINTED selected — a regression here is
        // invisible to the headless tests.
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SearchCancelTransition_InAttachedWindow_PaintsRevealedFolderSelection()
        {
            var folders = new[]
            {
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Art", "Assets/Art", 1, false),
                new ZepFolderEntry("UI", "Assets/Art/UI", 2, false),
                new ZepFolderEntry("Icons", "Assets/Art/UI/Icons", 3, false),
                new ZepFolderEntry("Audio", "Assets/Audio", 1, false)
            };
            var tree = new Sidebar.FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(folders));
            var window = UnityEngine.ScriptableObject.CreateInstance<SidebarSelectionTestWindow>();
            try
            {
                window.position = new UnityEngine.Rect(40f, 40f, 360f, 300f);
                window.Show();
                window.rootVisualElement.Add(tree);

                // Pre-search state: the user browses Assets/Audio; only the
                // root is expanded, so Assets/Art/UI/Icons has no row yet.
                tree.Apply("Assets/Audio", "Assets/Audio");
                yield return null;

                var list = tree.Q<ListView>();
                Assert.That(list, Is.Not.Null);
                Assert.That(
                    tree.SelectedFolderEntry?.Path,
                    Is.EqualTo("Assets/Audio"));

                // Search cancel: the shell reveals the picked result's folder,
                // so current + selected jump to the deep Icons folder in one
                // Apply — ancestors must expand and the new row must paint.
                tree.Apply("Assets/Art/UI/Icons", "Assets/Art/UI/Icons");
                yield return null;
                yield return null;

                var selected = tree.SelectedFolderEntry;
                Assert.That(selected, Is.Not.Null, "Sidebar tree must have a selected row after search cancel.");
                Assert.That(selected.Path, Is.EqualTo("Assets/Art/UI/Icons"));

                // The tree owns selection by path, so the paint is the class it
                // writes per bind — the ListView itself holds no selection.
                var row = RowFor(list, "Assets/Art/UI/Icons");
                Assert.That(row, Is.Not.Null, "Selected sidebar row must be realized by the ListView.");
                Assert.That(
                    row.ClassListContains(ZepUiClasses.RowSelected),
                    Is.True,
                    "Sidebar row must be painted with the selected-row class.");
            }
            finally
            {
                window.Close();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // The framing half of the contract: in a real project the revealed
        // folder usually sits far outside the sidebar viewport (often under
        // ancestors the same Apply just expanded). Selection without framing
        // reads as "nothing got selected", so the ancestor ScrollView — the
        // sidebar's only scroll owner — must scroll the row into view.
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SearchCancelTransition_TallTree_ScrollsRevealedFolderIntoView()
        {
            var folders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, false)
            };
            for (var i = 0; i < 40; i++)
            {
                var name = "Filler" + i.ToString("D2");
                folders.Add(new ZepFolderEntry(name, "Assets/" + name, 1, false));
            }

            folders.Add(new ZepFolderEntry("Art", "Assets/Art", 1, false));
            folders.Add(new ZepFolderEntry("UI", "Assets/Art/UI", 2, false));
            folders.Add(new ZepFolderEntry("Icons", "Assets/Art/UI/Icons", 3, false));

            var tree = new Sidebar.FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(folders));
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            var window = UnityEngine.ScriptableObject.CreateInstance<SidebarSelectionTestWindow>();
            try
            {
                window.position = new UnityEngine.Rect(40f, 40f, 360f, 300f);
                window.Show();
                window.rootVisualElement.Add(scroll);
                scroll.Add(tree);

                tree.Apply("Assets", "Assets");
                var deadline = EditorApplication.timeSinceStartup + 5d;
                var initialLayoutReady = false;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var rootRow = RowFor(tree.Q<ListView>(), "Assets");
                    var initialViewport = scroll.contentViewport.worldBound;
                    initialLayoutReady = rootRow != null
                        && initialViewport.height > 0f
                        && tree.worldBound.height > initialViewport.height
                        && scroll.verticalScroller.highValue > 0f
                        && rootRow.worldBound.yMin >= initialViewport.yMin - 1f
                        && rootRow.worldBound.yMax <= initialViewport.yMax + 1f;
                    if (initialLayoutReady) break;
                    window.Repaint();
                    yield return null;
                }

                Assert.That(initialLayoutReady, Is.True,
                    "The tall tree must have a realized root and a laid-out outer scroll range before reveal.");
                Assert.That(scroll.scrollOffset.y, Is.Zero);

                tree.Apply("Assets/Art/UI/Icons", "Assets/Art/UI/Icons");
                deadline = EditorApplication.timeSinceStartup + 5d;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var revealedRow = RowFor(tree.Q<ListView>(), "Assets/Art/UI/Icons");
                    var currentViewport = scroll.contentViewport.worldBound;
                    if (revealedRow != null
                        && scroll.scrollOffset.y > 0f
                        && revealedRow.worldBound.yMin >= currentViewport.yMin - 1f
                        && revealedRow.worldBound.yMax <= currentViewport.yMax + 1f)
                    {
                        break;
                    }

                    window.Repaint();
                    yield return null;
                }

                var list = tree.Q<ListView>();
                Assert.That(
                    tree.SelectedFolderEntry?.Path,
                    Is.EqualTo("Assets/Art/UI/Icons"));
                var row = RowFor(list, "Assets/Art/UI/Icons");
                Assert.That(row, Is.Not.Null, "Revealed sidebar row must be realized.");
                Assert.That(
                    row.ClassListContains(ZepUiClasses.RowSelected),
                    Is.True,
                    "Revealed sidebar row must be painted selected.");
                Assert.That(
                    scroll.scrollOffset.y,
                    Is.GreaterThan(0f),
                    "Sidebar must scroll toward the revealed folder.");

                var viewport = scroll.contentViewport.worldBound;
                var rowBound = row.worldBound;
                Assert.That(rowBound.yMin, Is.GreaterThanOrEqualTo(viewport.yMin - 1f));
                Assert.That(
                    rowBound.yMax,
                    Is.LessThanOrEqualTo(viewport.yMax + 1f),
                    "Revealed sidebar row must end up inside the sidebar viewport.");
            }
            finally
            {
                window.Close();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        private void OpenSearch(ZepShellView shell)
        {
            _searchHost = UnityEngine.ScriptableObject.CreateInstance<SidebarSelectionTestWindow>();
            _searchHost.Show();
            _searchHost.rootVisualElement.Add(shell);
            ((IZepShortcutHost)shell).FocusSearch();
        }

        private static void SelectFirstResult(ZepShellView shell)
        {
            var overlay = (SearchBarWidget)typeof(ZepShellView)
                .GetField("_searchOverlay", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
            typeof(SearchBarWidget).GetMethod("OpenSuggestion", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(overlay, new object[] { 0 });
            Assert.That(overlay.IsVisible, Is.False);
        }

        private static void InvokeShellMethod(ZepShellView shell, string methodName, params object[] args)
        {
            var method = typeof(ZepShellView).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Expected private method '{methodName}'.");
            method.Invoke(shell, args);
        }

        private sealed class SidebarSelectionTestWindow : EditorWindow
        {
        }

        // The lazy children query the sidebar runs in production, served from
        // the flat fixture array: the children of a path are the entries whose
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

        // The realized row for a folder path: the tree selects by path, so the
        // row is found by path rather than through a native selected index.
        private static VisualElement RowFor(ListView list, string folderPath)
        {
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

        // Real controller over fake data: Assets/Art/UI/Icons/Play.png is the
        // search target two collapsed levels below the Assets root. The stack
        // itself lives in ZepUiControllerRig (shared with the shell
        // tests); only the fixture is local.
        private static ZepUiControllerRig BuildControllerRig()
        {
            return ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("hero-guid", "Assets/Art/Hero.png", "Hero", "Texture2D"),
                    new AssetRecord("icon-guid", "Assets/Art/UI/Icons/Play.png", "Play", "Texture2D"),
                    new AssetRecord("theme-guid", "Assets/Audio/Theme.wav", "Theme", "AudioClip")
                },
                new[]
                {
                    "Assets",
                    "Assets/Art",
                    "Assets/Art/UI",
                    "Assets/Art/UI/Icons",
                    "Assets/Audio",
                    "Packages"
                });
        }
    }
}
