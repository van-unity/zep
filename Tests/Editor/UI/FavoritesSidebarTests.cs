using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.UI;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // The Favorites section as a sidebar citizen: the highlight follows the
    // ORIGIN of a navigation (a favorite click lights the favorite only, the
    // trees neither select nor expand; a tree click or Reveal hands it back),
    // a favorite drag publishes the split payload every sidebar row publishes
    // and reads back as a reorder in its own list, arrows/Enter work on the
    // rows (Delete is refused, as in Finder), the empty hint shows only while
    // there is nothing to show, and the fold survives a new widget.
    public sealed class FavoritesSidebarTests
    {
        private const string EditorPath = "Assets/Editor";
        private const string PluginsPath = "Assets/Plugins";
        private const string AssetsExpansionKey = "Zep.FolderTreeExpanded.Assets";

        private ZepUiControllerRig _rig;
        private string _previousExpansion;

        [SetUp]
        public void SetUp()
        {
            SessionState.EraseBool(FavoritesWidget.ExpandedSessionKey);
            _previousExpansion = SessionState.GetString(AssetsExpansionKey, null);
            SessionState.EraseString(AssetsExpansionKey);
            ZepSplitDragAndDrop.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            SessionState.EraseBool(FavoritesWidget.ExpandedSessionKey);
            if (_previousExpansion != null)
            {
                SessionState.SetString(AssetsExpansionKey, _previousExpansion);
            }
            else
            {
                SessionState.EraseString(AssetsExpansionKey);
            }

            ZepSplitDragAndDrop.Clear();
            _rig?.Dispose();
            _rig = null;
        }

        [UnityTest]
        public IEnumerator FavoriteClick_LightsTheFavoriteOnly_TreeClickAndRevealHandItBack()
        {
            var window = OpenWindow();
            try
            {
                var sidebar = MountSidebar(window, out var navigated);
                sidebar.SetFavoritePaths(new[] { EditorPath });
                sidebar.SetCurrentFolderPath("Assets");
                sidebar.SetSelectedFolderPath("Assets");
                sidebar.Flush();
                yield return null;
                yield return null;

                var favoriteRow = FavoriteRow(sidebar, 0);
                Assert.That(favoriteRow.userData, Is.EqualTo(EditorPath));
                Assert.That(IsSelected(favoriteRow), Is.False, "another folder is selected");
                Assert.That(IsSelected(TreeRow(sidebar, "Assets")), Is.True);

                Press(sidebar, favoriteRow);
                Assert.That(navigated, Is.EqualTo(new[] { EditorPath }), "the click navigates");

                // The shell's refresh after the navigation.
                sidebar.SetCurrentFolderPath(EditorPath);
                sidebar.SetSelectedFolderPath(EditorPath);
                sidebar.Flush();
                yield return null;
                yield return null;

                var treeRow = TreeRow(sidebar, EditorPath);
                Assert.That(IsSelected(favoriteRow), Is.True, "the favorite is the origin: it is lit");
                Assert.That(IsSelected(treeRow), Is.False, "the tree stays put on a favorite click");
                Assert.That(sidebar.SelectedFolderEntry?.Path, Is.EqualTo(EditorPath),
                    "the favorite's folder is the sidebar's selected folder");

                // A refresh with unchanged state keeps the origin.
                sidebar.Flush();
                Assert.That(IsSelected(favoriteRow), Is.True);
                Assert.That(IsSelected(treeRow), Is.False);

                // The tree click on the same folder changes no pane state, so
                // the sidebar repaints the new owner itself.
                Press(sidebar, treeRow);
                Assert.That(navigated, Is.EqualTo(new[] { EditorPath, EditorPath }));
                Assert.That(IsSelected(treeRow), Is.True, "a tree click hands the highlight to the tree");
                Assert.That(IsSelected(favoriteRow), Is.False);

                // Back to the favorite, then Reveal in Tree from its menu.
                Press(sidebar, favoriteRow);
                Assert.That(IsSelected(favoriteRow), Is.True);
                Assert.That(IsSelected(treeRow), Is.False);

                var menu = sidebar.Q<FavoritesWidget>().BuildRowMenuItems(EditorPath);
                Assert.That(menu[0].Label, Is.EqualTo("Reveal in Tree"));
                Assert.That(menu[1].Label, Is.EqualTo("Remove from Favorites"));
                menu[0].OnClick();
                Assert.That(IsSelected(treeRow), Is.True, "Reveal hands the folder to the tree");
                Assert.That(IsSelected(favoriteRow), Is.False);

                // Navigating anywhere else (breadcrumb, history, double-click)
                // drops the anchor: the trees own the next selection.
                Press(sidebar, favoriteRow);
                sidebar.SetCurrentFolderPath(PluginsPath);
                sidebar.SetSelectedFolderPath(PluginsPath);
                sidebar.Flush();
                yield return null;
                Assert.That(IsSelected(favoriteRow), Is.False);
                Assert.That(IsSelected(TreeRow(sidebar, PluginsPath)), Is.True);
            }
            finally
            {
                ReleasePointer(window.rootVisualElement);
                window.Close();
            }
        }

        // After a split drop the workspace focuses the new pane, so the next
        // favorite click first re-activates the primary pane — which raises
        // a sidebar refresh with the OLD selection still in place — and only
        // then navigates. The anchor must survive that intermediate refresh.
        [UnityTest]
        public IEnumerator FavoriteClick_SurvivesARefreshBeforeTheNavigationLands()
        {
            var window = OpenWindow();
            try
            {
                var sidebar = MountSidebar(window, out var navigated);
                sidebar.SetFavoritePaths(new[] { EditorPath, PluginsPath });
                sidebar.SetCurrentFolderPath(PluginsPath);
                sidebar.SetSelectedFolderPath(PluginsPath);
                sidebar.Flush();
                yield return null;
                yield return null;

                var favoriteRow = FavoriteRow(sidebar, 0);
                Press(sidebar, favoriteRow);
                Assert.That(navigated, Is.EqualTo(new[] { EditorPath }));

                // The pane-activation refresh: nothing has moved yet.
                sidebar.Flush();
                Assert.That(IsSelected(favoriteRow), Is.False, "not yet: the selection is still the old folder");
                Assert.That(IsSelected(TreeRow(sidebar, PluginsPath)), Is.True);

                // The navigation refresh.
                sidebar.SetCurrentFolderPath(EditorPath);
                sidebar.SetSelectedFolderPath(EditorPath);
                sidebar.Flush();
                yield return null;
                yield return null;

                Assert.That(IsSelected(favoriteRow), Is.True, "the click's origin survived the intermediate refresh");
                Assert.That(IsSelected(TreeRow(sidebar, EditorPath)), Is.False, "the tree stays put");

                // A refresh that moves the selection somewhere else, before the
                // navigation lands, does drop the pending anchor.
                Press(sidebar, FavoriteRow(sidebar, 1));
                sidebar.SetCurrentFolderPath("Assets");
                sidebar.SetSelectedFolderPath("Assets");
                sidebar.Flush();
                sidebar.SetCurrentFolderPath(PluginsPath);
                sidebar.SetSelectedFolderPath(PluginsPath);
                sidebar.Flush();
                yield return null;
                Assert.That(IsSelected(FavoriteRow(sidebar, 1)), Is.False, "the selection went elsewhere first");
                Assert.That(IsSelected(TreeRow(sidebar, PluginsPath)), Is.True);
            }
            finally
            {
                ReleasePointer(window.rootVisualElement);
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator FavoriteDrag_PublishesTheSplitPayload_AndReordersWhenDroppedBackOnTheList()
        {
            var window = OpenWindow();
            try
            {
                var sidebar = MountSidebar(window, out _);
                (string Path, string Anchor)? move = null;
                sidebar.FavoriteMoveRequested += (path, anchor) => move = (path, anchor);
                sidebar.SetFavoritePaths(new[] { EditorPath, PluginsPath });
                sidebar.SetCurrentFolderPath("Assets");
                sidebar.SetSelectedFolderPath("Assets");
                sidebar.Flush();
                yield return null;
                yield return null;

                var first = FavoriteRow(sidebar, 0);
                var second = FavoriteRow(sidebar, 1);
                // Publish the real favorite payload without starting pointer capture.
                var favorites = sidebar.Q<FavoritesWidget>();
                var snapshot = favorites.SnapshotReorderDragPayload(first);
                Assert.That(snapshot, Is.Not.Null, "a favorite row is a drag source");
                favorites.PrepareFavoriteDrag(snapshot);

                Assert.That(ZepSplitDragAndDrop.TryRead(out var payload), Is.True,
                    "a favorite drag is a split drag, so the workspace split zones accept it");
                Assert.That(payload.Source.RootFolderPath.Value, Is.EqualTo(EditorPath));
                Assert.That(DragAndDrop.paths, Is.Empty, "never an asset drag");
                Assert.That(DragAndDrop.objectReferences, Is.Empty);

                // Back over its own list it is a reorder: the caret shows and
                // the drop moves the favorite after the second row.
                var below = new Vector2(second.worldBound.center.x, second.worldBound.yMax - 2f);
                var relay = typeof(ZepWindowDocking).GetMethod(
                    "RelayContent", BindingFlags.Static | BindingFlags.NonPublic);
                relay.Invoke(null, new object[] { window, below, false });
                var caret = favorites.Q<VisualElement>("zep-sidebar-favorites-insert-line");
                Assert.That(caret.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(favorites.ClassListContains(ZepUiClasses.DropTarget), Is.False,
                    "a reorder is not an add-drop");

                relay.Invoke(null, new object[] { window, below, true });
                Assert.That(move, Is.EqualTo((EditorPath, string.Empty)), "lands at the end");
                Assert.That(caret.style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False,
                    "the list is a terminal route: it clears the split bus");
            }
            finally
            {
                ReleasePointer(window.rootVisualElement);
                ZepWindowDocking.Clear();
                ZepSplitDragAndDrop.Clear();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ArrowKeys_WalkTheFavorites_EnterOpens_DeleteIsRefused()
        {
            var window = OpenWindow();
            try
            {
                var sidebar = MountSidebar(window, out var navigated);
                sidebar.SetFavoritePaths(new[] { EditorPath, PluginsPath });
                sidebar.SetCurrentFolderPath("Assets");
                sidebar.SetSelectedFolderPath("Assets");
                sidebar.Flush();
                yield return null;
                yield return null;

                var first = FavoriteRow(sidebar, 0);
                var second = FavoriteRow(sidebar, 1);
                first.Focus();
                yield return null;

                SendKey(sidebar, first, KeyCode.DownArrow);
                Assert.That(navigated, Is.EqualTo(new[] { PluginsPath }), "Down navigates to the next favorite");
                Assert.That(window.rootVisualElement.focusController.focusedElement, Is.SameAs(second));

                SendKey(sidebar, second, KeyCode.DownArrow);
                Assert.That(navigated.Count, Is.EqualTo(1), "Down on the last favorite is a no-op");

                SendKey(sidebar, second, KeyCode.UpArrow);
                Assert.That(navigated, Is.EqualTo(new[] { PluginsPath, EditorPath }));
                Assert.That(window.rootVisualElement.focusController.focusedElement, Is.SameAs(first));

                Assert.That(sidebar.TryOpenFocusedFavorite(first), Is.True, "Enter opens the focused favorite");
                Assert.That(navigated, Is.EqualTo(new[] { PluginsPath, EditorPath, EditorPath }));

                // Delete: the shell refuses (beeps) on a favorite row and never
                // reaches the folder or the bookmark; the row predicate is what
                // it keys on, and it must not claim tree rows.
                Assert.That(sidebar.IsFavoriteRowTarget(second), Is.True);
                Assert.That(sidebar.IsFavoriteRowTarget(TreeRow(sidebar, "Assets")), Is.False);
                Assert.That(sidebar.TryOpenFocusedFavorite(sidebar), Is.False, "not a favorite row");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void EmptyHint_ShowsOnlyWhileThereAreNoFavorites_AndTheSectionIsExpanded()
        {
            var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false);
            var favorites = sidebar.Q<FavoritesWidget>();
            var hint = favorites.Q<Label>("zep-sidebar-favorites-empty");
            Assert.That(hint.text, Is.EqualTo(FavoritesWidget.EmptyHintText));

            sidebar.SetFavoritePaths(Array.Empty<string>());
            sidebar.Flush();
            Assert.That(hint.style.display.value, Is.EqualTo(DisplayStyle.Flex), "nothing to show: the hint stands in");

            sidebar.SetFavoritePaths(new[] { EditorPath });
            sidebar.Flush();
            Assert.That(hint.style.display.value, Is.EqualTo(DisplayStyle.None), "a favorite replaces the hint");

            sidebar.SetFavoritePaths(Array.Empty<string>());
            sidebar.Flush();
            Assert.That(hint.style.display.value, Is.EqualTo(DisplayStyle.Flex));

            favorites.Q<SidebarSectionHeader>().ToggleExpanded();
            Assert.That(hint.style.display.value, Is.EqualTo(DisplayStyle.None), "a folded section hides its hint");
            favorites.Q<SidebarSectionHeader>().ToggleExpanded();
            Assert.That(hint.style.display.value, Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void HeaderFold_SurvivesANewWidget()
        {
            var first = new FavoritesWidget(new UnityZepIconRepository(), _ => null);
            Assert.That(first.IsExpanded, Is.True, "default is expanded");

            first.Q<SidebarSectionHeader>().ToggleExpanded();
            Assert.That(first.IsExpanded, Is.False);

            var second = new FavoritesWidget(new UnityZepIconRepository(), _ => null);
            Assert.That(second.IsExpanded, Is.False, "the fold is remembered (SessionState)");
            Assert.That(second.Q<VisualElement>("zep-sidebar-favorites-list").style.display.value,
                Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void RecoveryFavorites_QueryRefusalPreservesRows()
        {
            var unavailable = false;
            var entries = new Dictionary<string, ZepFolderEntry>
            {
                [EditorPath] = new ZepFolderEntry("Before editor", EditorPath, 0, true),
                [PluginsPath] = new ZepFolderEntry("Before plugins", PluginsPath, 0, true)
            };
            var favorites = new FavoritesWidget(
                new UnityZepIconRepository(),
                path =>
                {
                    if (unavailable && path == PluginsPath)
                    {
                        throw new ProjectTagStateUnavailableException("unavailable");
                    }

                    return entries.TryGetValue(path, out var entry) ? entry : null;
                });
            favorites.SetFavorites(new[] { EditorPath, PluginsPath });
            favorites.SetSelectedPath(EditorPath);
            var rows = favorites.Q<VisualElement>("zep-sidebar-favorites-list");
            var hint = favorites.Q<Label>("zep-sidebar-favorites-empty");
            var first = rows[0];
            var second = rows[1];
            var firstLabel = ((Label)first[1][0]).text;
            var secondLabel = ((Label)second[1][0]).text;

            entries[EditorPath] = new ZepFolderEntry("After editor", EditorPath, 0, true);
            entries[PluginsPath] = new ZepFolderEntry("After plugins", PluginsPath, 0, true);
            unavailable = true;
            Assert.DoesNotThrow(() => favorites.SetFavorites(new[] { EditorPath, PluginsPath }));
            Assert.That(rows[0], Is.SameAs(first));
            Assert.That(rows[1], Is.SameAs(second));
            Assert.That(((Label)rows[0][1][0]).text, Is.EqualTo(firstLabel));
            Assert.That(((Label)rows[1][1][0]).text, Is.EqualTo(secondLabel));
            Assert.That(favorites.SelectedFolderEntry?.Path, Is.EqualTo(EditorPath));
            Assert.That(hint.style.display.value, Is.EqualTo(DisplayStyle.None));

            unavailable = false;
            favorites.SetFavorites(new[] { "Assets/Missing", PluginsPath, EditorPath });
            Assert.That(rows.childCount, Is.EqualTo(2));
            Assert.That(rows[0].userData, Is.EqualTo(PluginsPath));
            Assert.That(rows[1].userData, Is.EqualTo(EditorPath));
            Assert.That(((Label)rows[0][1][0]).text, Is.EqualTo("After plugins"));
            Assert.That(((Label)rows[1][1][0]).text, Is.EqualTo("After editor"));

            var initiallyUnavailable = true;
            var initiallyEmpty = new FavoritesWidget(
                new UnityZepIconRepository(),
                path =>
                {
                    if (initiallyUnavailable)
                    {
                        throw new ProjectTagStateUnavailableException("unavailable");
                    }

                    return entries.TryGetValue(path, out var entry) ? entry : null;
                });
            var initiallyEmptyRows = initiallyEmpty.Q<VisualElement>("zep-sidebar-favorites-list");
            Assert.DoesNotThrow(() => initiallyEmpty.SetFavorites(new[] { EditorPath }));
            Assert.That(initiallyEmptyRows.childCount, Is.EqualTo(0));
            initiallyUnavailable = false;
            initiallyEmpty.SetFavorites(new[] { EditorPath });
            Assert.That(initiallyEmptyRows.childCount, Is.EqualTo(1));
            Assert.That(initiallyEmptyRows[0].userData, Is.EqualTo(EditorPath));
        }

        [Test]
        public void RecoveryFavorites_TintRefusalIsAtomic()
        {
            var laterTintUnavailable = false;
            var unexpected = false;
            var editorTint = ZepTagColor.Blue;
            var pluginsTint = ZepTagColor.Red;
            var entries = new Dictionary<string, ZepFolderEntry>
            {
                [EditorPath] = new ZepFolderEntry("Before editor", EditorPath, 0, true),
                [PluginsPath] = new ZepFolderEntry("Before plugins", PluginsPath, 0, true)
            };
            var favorites = new FavoritesWidget(
                new UnityZepIconRepository(),
                path => entries[path],
                path =>
                {
                    if (unexpected)
                    {
                        throw new InvalidOperationException("unexpected");
                    }

                    if (laterTintUnavailable && path == PluginsPath)
                    {
                        throw new ProjectTagStateUnavailableException("unavailable");
                    }

                    return path == EditorPath ? editorTint : pluginsTint;
                });
            favorites.SetFavorites(new[] { EditorPath, PluginsPath });
            favorites.SetSelectedPath(EditorPath);
            var rows = favorites.Q<VisualElement>("zep-sidebar-favorites-list");
            var first = rows[0];
            var second = rows[1];
            var firstIcon = rows[0][0][0];
            var secondIcon = rows[1][0][0];
            var expectedFirstTint = (Color)ZepTagColorPalette.GetColor(ZepTagColor.Blue);
            var expectedSecondTint = (Color)ZepTagColorPalette.GetColor(ZepTagColor.Red);
            Assert.That(firstIcon.style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedFirstTint));
            Assert.That(secondIcon.style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedSecondTint));

            entries[EditorPath] = new ZepFolderEntry("After editor", EditorPath, 0, true);
            entries[PluginsPath] = new ZepFolderEntry("After plugins", PluginsPath, 0, true);
            editorTint = ZepTagColor.Green;
            pluginsTint = ZepTagColor.Orange;
            laterTintUnavailable = true;

            Assert.DoesNotThrow(() => favorites.SetFavorites(new[] { EditorPath, PluginsPath }));
            Assert.That(rows[0], Is.SameAs(first));
            Assert.That(rows[1], Is.SameAs(second));
            Assert.That(((Label)rows[0][1][0]).text, Is.EqualTo("Before editor"));
            Assert.That(((Label)rows[1][1][0]).text, Is.EqualTo("Before plugins"));
            Assert.That(rows[0][0][0].style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedFirstTint));
            Assert.That(rows[1][0][0].style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedSecondTint));
            Assert.That(favorites.SelectedFolderEntry?.Path, Is.EqualTo(EditorPath));

            laterTintUnavailable = false;
            favorites.SetFavorites(new[] { EditorPath, PluginsPath });
            Assert.That(((Label)rows[0][1][0]).text, Is.EqualTo("After editor"));
            Assert.That(((Label)rows[1][1][0]).text, Is.EqualTo("After plugins"));
            var expectedCurrentFirstTint = (Color)ZepTagColorPalette.GetColor(ZepTagColor.Green);
            var expectedCurrentSecondTint = (Color)ZepTagColorPalette.GetColor(ZepTagColor.Orange);
            Assert.That(rows[0][0][0].style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedCurrentFirstTint));
            Assert.That(rows[1][0][0].style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedCurrentSecondTint));

            unexpected = true;
            Assert.Throws<InvalidOperationException>(() => favorites.SetFavorites(new[] { EditorPath }));
            Assert.That(rows[0].userData, Is.EqualTo(EditorPath));
            Assert.That(rows[1].userData, Is.EqualTo(PluginsPath));
            Assert.That(((Label)rows[0][1][0]).text, Is.EqualTo("After editor"));
            Assert.That(((Label)rows[1][1][0]).text, Is.EqualTo("After plugins"));
            Assert.That(rows[0][0][0].style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedCurrentFirstTint));
            Assert.That(rows[1][0][0].style.unityBackgroundImageTintColor.value, Is.EqualTo(expectedCurrentSecondTint));
            Assert.That(favorites.SelectedFolderEntry?.Path, Is.EqualTo(EditorPath));
        }

        [UnityTest]
        public IEnumerator RecoveryFavorites_DropRefusalRejectsWholePayload()
        {
            var refused = true;
            var queries = new List<string>();
            var received = new List<IReadOnlyList<string>>();
            var window = OpenWindow();
            try
            {
                var favorites = new FavoritesWidget(
                    new UnityZepIconRepository(),
                    path =>
                    {
                        queries.Add(path);
                        if (refused && path == PluginsPath)
                        {
                            throw new ProjectTagStateUnavailableException("unavailable");
                        }

                        return new ZepFolderEntry(path, path, 0, true);
                    });
                favorites.FavoriteDropReceived += paths => received.Add(new List<string>(paths));
                window.rootVisualElement.Add(favorites);
                yield return null;
                yield return null;

                Assert.That(favorites.panel, Is.Not.Null, "the widget must receive mounted events");
                Assert.That(favorites.worldBound.width, Is.GreaterThan(0f));
                Assert.That(favorites.worldBound.height, Is.GreaterThan(0f));
                var position = favorites.worldBound.center;
                ZepSplitDragAndDrop.Clear();
                DragAndDrop.paths = new[] { EditorPath, PluginsPath };

                Assert.DoesNotThrow(() => SendDrag(window.rootVisualElement, DragUpdatedEvent.GetPooled(new Event
                {
                    type = EventType.DragUpdated, mousePosition = position
                }), favorites));
                Assert.That(queries, Is.EqualTo(new[] { EditorPath, PluginsPath }));
                Assert.That(received, Is.Empty);
                Assert.That(favorites.ClassListContains(ZepUiClasses.DropTarget), Is.False);

                Assert.DoesNotThrow(() => SendDrag(window.rootVisualElement, DragPerformEvent.GetPooled(new Event
                {
                    type = EventType.DragPerform, mousePosition = position
                }), favorites));
                Assert.That(queries, Is.EqualTo(new[] { EditorPath, PluginsPath, EditorPath, PluginsPath }));
                Assert.That(received, Is.Empty);
                Assert.That(favorites.ClassListContains(ZepUiClasses.DropTarget), Is.False);

                refused = false;
                queries.Clear();
                SendDrag(window.rootVisualElement, DragPerformEvent.GetPooled(new Event
                {
                    type = EventType.DragPerform, mousePosition = position
                }), favorites);
                Assert.That(queries, Is.EqualTo(new[] { EditorPath, PluginsPath }));
                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(received[0], Is.EqualTo(new[] { EditorPath, PluginsPath }));

                refused = true;
                queries.Clear();
                received.Clear();
                DragAndDrop.paths = Array.Empty<string>();
                ZepSplitDragAndDrop.Prepare(new ZepSplitDragPayload(
                    ZepPaneSource.RootedFolder(AssetPath.From(PluginsPath)), "Plugins"));
                SendDrag(window.rootVisualElement, DragPerformEvent.GetPooled(new Event
                {
                    type = EventType.DragPerform, mousePosition = position
                }), favorites);
                Assert.That(queries, Is.EqualTo(new[] { PluginsPath }));
                Assert.That(received, Is.Empty);
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.True);

                refused = false;
                queries.Clear();
                SendDrag(window.rootVisualElement, DragPerformEvent.GetPooled(new Event
                {
                    type = EventType.DragPerform, mousePosition = position
                }), favorites);
                Assert.That(queries, Is.EqualTo(new[] { PluginsPath }));
                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(received[0], Is.EqualTo(new[] { PluginsPath }));
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);
            }
            finally
            {
                DragAndDrop.paths = Array.Empty<string>();
                ZepSplitDragAndDrop.Clear();
                window.Close();
            }
        }

        private SidebarWidget MountSidebar(EditorWindow window, out List<string> navigated)
        {
            _rig = ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("editor-tool", EditorPath + "/Tool.cs", "Tool", "MonoScript"),
                    new AssetRecord("plugin-lib", PluginsPath + "/Lib.dll", "Lib", "DefaultAsset")
                },
                new[] { "Assets", EditorPath, PluginsPath });
            var sidebar = new SidebarWidget(new UnityZepIconRepository(), 270f, false, _rig.Controller);
            var log = new List<string>();
            sidebar.FolderSelected += log.Add;
            navigated = log;
            window.rootVisualElement.Add(sidebar);
            return sidebar;
        }

        private static EditorWindow OpenWindow()
        {
            var window = ScriptableObject.CreateInstance<FavoritesSidebarTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 500f);
            window.Show();
            return window;
        }

        private static VisualElement FavoriteRow(SidebarWidget sidebar, int index)
        {
            var list = sidebar.Q<VisualElement>("zep-sidebar-favorites-list");
            Assert.That(list.childCount, Is.GreaterThan(index), "the favorite must have a row");
            return list[index];
        }

        private static VisualElement TreeRow(SidebarWidget sidebar, string folderPath)
        {
            var list = sidebar.Q<ListView>("zep-folder-list-assets");
            var source = list.itemsSource;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] is ZepFolderEntry entry
                    && string.Equals(entry.Path, folderPath, StringComparison.Ordinal))
                {
                    var row = list.GetRootElementForIndex(i);
                    Assert.That(row, Is.Not.Null, "the folder must have a realized row: " + folderPath);
                    return row;
                }
            }

            Assert.Fail("no tree row for " + folderPath);
            return null;
        }

        private static bool IsSelected(VisualElement row)
        {
            return row.ClassListContains(ZepUiClasses.RowSelected);
        }

        // A click is a press and a release on the same spot; the sidebar's
        // press interactions commit on the release.
        private static void Press(VisualElement root, VisualElement target)
        {
            var position = target.worldBound.center;
            // clickCount = 1: the press interactions arm on a single click only.
            SendPointer(root, PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = position
            }), target);
            SendPointer(root, PointerUpEvent.GetPooled(new Event
            {
                type = EventType.MouseUp, button = 0, mousePosition = position
            }), target);
        }

        private static void SendPointer(VisualElement root, EventBase evt, VisualElement target)
        {
            using (evt)
            {
                evt.target = target;
                root.SendEvent(evt);
            }
        }

        private static void SendDrag(VisualElement root, EventBase evt, VisualElement target)
        {
            using (evt)
            {
                evt.target = target;
                root.SendEvent(evt);
            }
        }

        private static void SendKey(VisualElement root, VisualElement target, KeyCode keyCode)
        {
            using (var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = keyCode }))
            {
                evt.target = target;
                root.SendEvent(evt);
            }
        }

        // A dispatched PointerDown sets Unity's global pressed-button state;
        // release it so it cannot leak into later tests.
        private static void ReleasePointer(VisualElement root)
        {
            using (var evt = PointerUpEvent.GetPooled(new Event
            {
                type = EventType.MouseUp, button = 0, mousePosition = root.worldBound.center
            }))
            {
                evt.target = root;
                root.SendEvent(evt);
            }
        }

        private sealed class FavoritesSidebarTestWindow : EditorWindow
        {
        }
    }
}
