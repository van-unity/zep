using System;
using System.Collections;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Theming;
using Zep.Editor.UI;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // The selected folder row paints one of three states, keyed on classes
    // the sidebar keeps on its pane: the shared accent fill while keyboard
    // focus is inside the sidebar, a neutral fill while it is elsewhere, and
    // the accent ring on top during keyboard navigation. None of them carries
    // the asset list's outline or bold.
    //
    // History that shapes the assertions: the row carried NO fill at all until
    // 2026-07-29, because the selection rules were written against a per-item
    // wrapper and a NON-reorderable ListView has none — Unity mounts the
    // unity-list-view__item classes straight onto the element makeItem
    // returns. Only the RESOLVED paint catches that class of mistake, so these
    // tests mount the real widget under the real stylesheets and read
    // resolvedStyle.
    public sealed class FolderTreeSelectionPaintTests
    {
        private const string ExpansionKey = "Zep.FolderTreeExpanded.Assets";
        private const string SelectedPath = "Assets/Editor";
        private const string UnselectedPath = "Assets/Plugins";

        // ZepTheme_Defaults.uss: --zep-bg-item-selected-accent, whose
        // RGB is the fallback accent and whose alpha is the one every theme and
        // ThemeDerivation share.
        private static readonly Color ExpectedAccent =
            new Color(70f / 255f, 110f / 255f, 210f / 255f, ThemeDerivation.SelectedAccentAlpha);
        // --zep-bg-item-hover in the same table: the neutral fill an unfocused
        // sidebar selection drops to (the macOS inactive-selection grey).
        private static readonly Color ExpectedNeutral = new Color(1f, 1f, 1f, 0.05f);
        // --zep-border-item-selected-accent: the keyboard ring.
        private static readonly Color ExpectedRing =
            new Color(70f / 255f, 110f / 255f, 210f / 255f, 0.6f);

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
        public IEnumerator SelectedFolderRow_WhileTheSidebarHasFocus_PaintsTheThemeSelectionAccent()
        {
            var window = ScriptableObject.CreateInstance<SelectionPaintTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window, sidebarFocused: true);
                yield return null;

                tree.Apply(SelectedPath, SelectedPath);
                yield return null;
                yield return null;

                var selected = RowFor(tree, SelectedPath);
                Assert.That(selected, Is.Not.Null, "the selected folder must have a realized row");

                // Pins the structural premise the fix rests on: the painted
                // element IS the folder row, not a wrapper around it, and the
                // paint is keyed on the class the widget itself writes per bind
                // (.zep-row-selected) rather than on ListView's native
                // :checked state. If a future Unity version (or turning
                // reordering on) reintroduces a wrapper, this fails here rather
                // than silently un-painting the selection.
                Assert.That(selected.ClassListContains("zep-folder-row"), Is.True,
                    "the ListView item must BE the folder row — a non-reorderable ListView has no "
                    + "per-item wrapper, and the selection rules are keyed on that");
                Assert.That(
                    selected.ClassListContains(ZepUiClasses.RowSelected), Is.True,
                    "the selected row must carry the class the surviving selection rule is keyed on");

                var painted = selected.resolvedStyle.backgroundColor;
                Assert.That(painted.a, Is.EqualTo(ExpectedAccent.a).Within(0.01f),
                    "the selected folder row must paint the translucent selection accent, "
                    + $"not {(painted.a <= 0.01f ? "nothing" : "some other opacity")}");
                Assert.That(painted.r, Is.EqualTo(ExpectedAccent.r).Within(0.01f));
                Assert.That(painted.g, Is.EqualTo(ExpectedAccent.g).Within(0.01f));
                Assert.That(painted.b, Is.EqualTo(ExpectedAccent.b).Within(0.01f),
                    "the accent must come from --zep-bg-item-selected-accent, not the editor's default selection blue");

                // A fill, not a box: the outline belongs to keyboard navigation
                // (below) and the bold was the asset list's habit.
                Assert.That(selected.resolvedStyle.borderLeftColor.a, Is.LessThanOrEqualTo(0.01f),
                    "a mouse selection draws no outline");
                var label = selected.Q<Label>(className: ZepUiClasses.RowPrimaryText);
                Assert.That(label.resolvedStyle.unityFontStyleAndWeight, Is.EqualTo(FontStyle.Normal),
                    "the selected name must not switch to bold (it shifts the text width)");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SelectedFolderRow_WhileFocusIsElsewhere_DropsToTheNeutralFill()
        {
            var window = ScriptableObject.CreateInstance<SelectionPaintTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window, sidebarFocused: false);
                yield return null;

                tree.Apply(SelectedPath, SelectedPath);
                yield return null;
                yield return null;

                var selected = RowFor(tree, SelectedPath);
                Assert.That(selected, Is.Not.Null, "the selected folder must have a realized row");
                var painted = selected.resolvedStyle.backgroundColor;
                Assert.That(painted.a, Is.EqualTo(ExpectedNeutral.a).Within(0.01f),
                    "an unfocused sidebar keeps its selection visible, but neutral");
                Assert.That(painted.r, Is.EqualTo(ExpectedNeutral.r).Within(0.01f));
                Assert.That(painted.g, Is.EqualTo(ExpectedNeutral.g).Within(0.01f));
                Assert.That(painted.b, Is.EqualTo(ExpectedNeutral.b).Within(0.01f),
                    "the neutral fill is --zep-bg-item-hover, not the accent");
                Assert.That(selected.resolvedStyle.borderLeftColor.a, Is.LessThanOrEqualTo(0.01f));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SelectedFolderRow_DuringKeyboardNavigation_AddsTheAccentRing()
        {
            var window = ScriptableObject.CreateInstance<SelectionPaintTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window, sidebarFocused: true);
                var host = tree.parent;
                host.AddToClassList(ZepUiClasses.SidebarPaneKeyboard);
                // The tree marks itself when focus is inside it (FocusIn);
                // that class is exercised through real focus in
                // SidebarLayoutTests, so here it is set directly.
                tree.AddToClassList(ZepUiClasses.FolderTreeFocused);
                yield return null;

                tree.Apply(SelectedPath, SelectedPath);
                yield return null;
                yield return null;

                var selected = RowFor(tree, SelectedPath);
                Assert.That(selected, Is.Not.Null, "the selected folder must have a realized row");
                Assert.That(selected.resolvedStyle.backgroundColor.a,
                    Is.EqualTo(ExpectedAccent.a).Within(0.01f),
                    "the ring sits on top of the focused accent fill");
                var ring = selected.resolvedStyle.borderLeftColor;
                Assert.That(ring.a, Is.EqualTo(ExpectedRing.a).Within(0.01f),
                    "keyboard navigation draws the accent ring on the keyboard cursor");
                Assert.That(ring.r, Is.EqualTo(ExpectedRing.r).Within(0.01f));
                Assert.That(ring.b, Is.EqualTo(ExpectedRing.b).Within(0.01f));
                Assert.That(selected.resolvedStyle.borderLeftWidth, Is.EqualTo(1f).Within(0.01f),
                    "the 1px border is always laid out so the ring never moves the row");

                var other = RowFor(tree, UnselectedPath);
                Assert.That(other.resolvedStyle.borderLeftColor.a, Is.LessThanOrEqualTo(0.01f),
                    "only the keyboard cursor carries the ring");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator UnselectedFolderRow_PaintsNothing()
        {
            var window = ScriptableObject.CreateInstance<SelectionPaintTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window, sidebarFocused: true);
                yield return null;

                tree.Apply(SelectedPath, SelectedPath);
                yield return null;
                yield return null;

                var other = RowFor(tree, UnselectedPath);
                Assert.That(other, Is.Not.Null, "the sibling folder must have a realized row");
                Assert.That(other.resolvedStyle.backgroundColor.a, Is.LessThanOrEqualTo(0.01f),
                    "only the selected row may carry the accent — a fill on every row is the "
                    + "same cascade mistake in the other direction");
            }
            finally
            {
                window.Close();
            }
        }

        // §6.6b: the tree has no native list selection any more, so the
        // ID-keyed `.unity-list-view__item:checked` rules that used to paint the
        // sidebar selection can never match. The accent fill must resolve
        // through the rule keyed on the class BindFolderRow writes.
        [UnityTest]
        public IEnumerator SelectedFolderRow_ResolvesTheAccentThroughTheRowClass_WithNoNativeListSelection()
        {
            var window = ScriptableObject.CreateInstance<SelectionPaintTestWindow>();
            window.position = new Rect(100f, 100f, 300f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window, sidebarFocused: true);
                yield return null;

                tree.Apply(SelectedPath, SelectedPath);
                yield return null;
                yield return null;

                var list = tree.Q<ListView>("zep-folder-list-assets");
                Assert.That(
                    list.selectionType, Is.EqualTo(SelectionType.None),
                    "the tree owns selection by project path; the ListView must hold none");
                Assert.That(
                    list.selectedIndex, Is.EqualTo(-1),
                    "with no native selection there is no :checked row for a rule to key on, "
                    + "so the paint below can only come from the class-keyed rule");

                var selected = RowFor(tree, SelectedPath);
                Assert.That(selected, Is.Not.Null, "the selected folder must have a realized row");
                Assert.That(
                    selected.ClassListContains(ZepUiClasses.RowSelected), Is.True);

                var painted = selected.resolvedStyle.backgroundColor;
                Assert.That(painted.a, Is.EqualTo(ExpectedAccent.a).Within(0.01f),
                    ".zep-row-selected must still carry the accent fill");
                Assert.That(painted.r, Is.EqualTo(ExpectedAccent.r).Within(0.01f));
                Assert.That(painted.g, Is.EqualTo(ExpectedAccent.g).Within(0.01f));
                Assert.That(painted.b, Is.EqualTo(ExpectedAccent.b).Within(0.01f),
                    "the accent must come from --zep-bg-item-selected-accent");
            }
            finally
            {
                window.Close();
            }
        }

        // The window root stands in for the panel root: the two sheets the shell
        // attaches, plus the class the Defaults variable table is scoped to. A
        // host element stands in for the sidebar pane, carrying the class the
        // selection rules are scoped to and, optionally, the focused state
        // SidebarWidget would keep on it.
        private static FolderTreeWidget MountTree(EditorWindow window, bool sidebarFocused)
        {
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            AttachSheet(root, ZepUiClasses.PanelStyleSheetPath);
            AttachSheet(root, ZepUiClasses.DefaultsStyleSheetPath);

            var host = new VisualElement();
            host.AddToClassList(ZepUiClasses.SidebarPane);
            host.EnableInClassList(ZepUiClasses.SidebarPaneFocused, sidebarFocused);
            root.Add(host);

            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(BuildFolders()));
            host.Add(tree);
            return tree;
        }

        // The lazy children query the sidebar runs in production, served from
        // the same flat fixture: the children of a path are the entries whose
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

        private static void AttachSheet(VisualElement root, string path)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            Assert.That(sheet, Is.Not.Null, $"stylesheet missing: {path}");
            root.styleSheets.Add(sheet);
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

        private static List<ZepFolderEntry> BuildFolders()
        {
            return new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Editor", SelectedPath, 1, false),
                new ZepFolderEntry("Plugins", UnselectedPath, 1, false)
            };
        }

        private sealed class SelectionPaintTestWindow : EditorWindow
        {
        }
    }
}
