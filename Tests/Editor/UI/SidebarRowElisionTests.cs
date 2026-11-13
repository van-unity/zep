using System;
using System.Collections;
using System.Collections.Generic;
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
    // The asset list rows have always elided a too-long name and put the full
    // path in a tooltip; the sidebar rows did neither, because the shared
    // .zep-row-primary primitive declared only a font-size and a
    // colour and ONLY the asset-row delta added nowrap + ellipsis. In a narrow
    // sidebar that made a long folder or tag name wrap the label open behind a
    // fixed 25px row pitch — unreadable, and hovering it revealed nothing.
    //
    // Grepping the sheet cannot prove the cascade reaches the sidebar label, so
    // these tests mount the real widgets under the real stylesheets in a narrow
    // window and read the resolved layout.
    public sealed class SidebarRowElisionTests
    {
        private const string ExpansionKey = "Zep.FolderTreeExpanded.Assets";

        // Far wider than the ~170px window below at the tree's 11px font, so
        // without nowrap the label wraps to several lines.
        private const string LongName =
            "LongFolderNameThatIsFarTooWideForAnyNarrowZepSidebar";

        private const string LongFolderPath = "Assets/" + LongName;
        private const string ShortFolderPath = "Assets/UI";

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
        public IEnumerator FolderRow_WithATooLongName_ElidesOnOneLineInsteadOfWrappingOrSpilling()
        {
            var window = ScriptableObject.CreateInstance<SidebarElisionTestWindow>();
            window.position = new Rect(100f, 100f, 170f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window);
                yield return null;

                tree.Apply("Assets", "Assets");
                yield return null;
                yield return null;

                var longRow = RowFor(tree, LongFolderPath);
                var shortRow = RowFor(tree, ShortFolderPath);
                Assert.That(longRow, Is.Not.Null, "the long-named folder must have a realized row");
                Assert.That(shortRow, Is.Not.Null, "the short-named folder must have a realized row");

                var longLabel = longRow.Q<Label>(className: ZepUiClasses.RowPrimaryText);
                var shortLabel = shortRow.Q<Label>(className: ZepUiClasses.RowPrimaryText);
                Assert.That(longLabel, Is.Not.Null);
                Assert.That(shortLabel, Is.Not.Null);
                Assert.That(longLabel.text, Is.EqualTo(LongName), "the row must carry the whole name");

                // If layout never resolved in this batchmode window the two
                // heights would both be 0 and the comparison below would pass
                // for the wrong reason.
                Assert.That(shortLabel.resolvedStyle.height, Is.GreaterThan(1f),
                    "layout did not resolve for the bare tree — the probe cannot measure elision");
                Assert.That(longLabel.resolvedStyle.width, Is.LessThan(170f),
                    "the label must be constrained by the narrow sidebar, not by its text");

                Assert.That(longLabel.resolvedStyle.height,
                    Is.EqualTo(shortLabel.resolvedStyle.height).Within(0.5f),
                    "a too-long folder name must stay on ONE line (" + longLabel.resolvedStyle.height
                        + "px vs " + shortLabel.resolvedStyle.height + "px for a short name) — "
                        + "wrapping it opens the label behind the tree's fixed row pitch");
                Assert.That(longLabel.resolvedStyle.whiteSpace, Is.EqualTo(WhiteSpace.NoWrap),
                    "the shared row primitive must forbid wrapping");
                Assert.That(longLabel.resolvedStyle.textOverflow, Is.EqualTo(TextOverflow.Ellipsis),
                    "the clipped name must end in an ellipsis, not a hard cut");
            }
            finally
            {
                window.Close();
            }
        }

        // The other half of the contract, in its own case so it is proven on its
        // own: once a name can be clipped, hover has to be able to give it back.
        [UnityTest]
        public IEnumerator FolderRow_CarriesItsFullPathAsATooltip()
        {
            var window = ScriptableObject.CreateInstance<SidebarElisionTestWindow>();
            window.position = new Rect(100f, 100f, 170f, 400f);
            window.Show();
            try
            {
                var tree = MountTree(window);
                yield return null;

                tree.Apply("Assets", "Assets");
                yield return null;
                yield return null;

                var longRow = RowFor(tree, LongFolderPath);
                var shortRow = RowFor(tree, ShortFolderPath);
                Assert.That(longRow, Is.Not.Null, "the long-named folder must have a realized row");
                Assert.That(longRow.tooltip, Is.EqualTo(LongFolderPath),
                    "an elided folder name is only recoverable by hover, so the row carries its path");
                Assert.That(shortRow.tooltip, Is.EqualTo(ShortFolderPath),
                    "every folder row carries its own path — the tooltip is written per bind, "
                        + "not left over from whichever entry the recycled row showed before");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator TagRow_WithATooLongName_ElidesButKeepsTheCountBadgeVisible()
        {
            var window = ScriptableObject.CreateInstance<SidebarElisionTestWindow>();
            window.position = new Rect(100f, 100f, 170f, 400f);
            window.Show();
            try
            {
                var root = MountRoot(window);
                var tags = new TagsFilterWidget();
                root.Add(tags);
                tags.SetTags(new List<ZepTagFilterOption>
                {
                    new ZepTagFilterOption(
                        new ZepTag("long", LongName, ZepTagColor.Blue, true), 12),
                    new ZepTagFilterOption(
                        new ZepTag("short", "UI", ZepTagColor.Green, true), 3)
                });
                yield return null;
                yield return null;

                var longRow = tags.Q<VisualElement>("zep-sidebar-tags-row-long");
                var shortRow = tags.Q<VisualElement>("zep-sidebar-tags-row-short");
                Assert.That(longRow, Is.Not.Null, "the long-named tag must have a row");
                Assert.That(shortRow, Is.Not.Null, "the short-named tag must have a row");

                var longLabel = longRow.Q<Label>(className: ZepUiClasses.RowPrimaryText);
                var shortLabel = shortRow.Q<Label>(className: ZepUiClasses.RowPrimaryText);
                var badge = longRow.Q<Label>(className: ZepUiClasses.RowChip);
                Assert.That(longLabel, Is.Not.Null);
                Assert.That(badge, Is.Not.Null, "the tag row must keep its count badge");

                Assert.That(shortLabel.resolvedStyle.height, Is.GreaterThan(1f),
                    "layout did not resolve for the bare tags widget — the probe cannot measure elision");
                Assert.That(longLabel.resolvedStyle.height,
                    Is.EqualTo(shortLabel.resolvedStyle.height).Within(0.5f),
                    "a too-long tag name must stay on ONE line (" + longLabel.resolvedStyle.height
                        + "px vs " + shortLabel.resolvedStyle.height + "px for a short name)");
                Assert.That(longLabel.resolvedStyle.whiteSpace, Is.EqualTo(WhiteSpace.NoWrap));
                Assert.That(longLabel.resolvedStyle.textOverflow, Is.EqualTo(TextOverflow.Ellipsis));

                Assert.That(badge.text, Is.EqualTo("12"));
                Assert.That(badge.resolvedStyle.width, Is.GreaterThan(0f),
                    "only the name may clip: the count badge stays laid out beside it");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator TagRow_CarriesItsFullNameAsATooltip()
        {
            var window = ScriptableObject.CreateInstance<SidebarElisionTestWindow>();
            window.position = new Rect(100f, 100f, 170f, 400f);
            window.Show();
            try
            {
                var root = MountRoot(window);
                var tags = new TagsFilterWidget();
                root.Add(tags);
                tags.SetTags(new List<ZepTagFilterOption>
                {
                    new ZepTagFilterOption(
                        new ZepTag("long", LongName, ZepTagColor.Blue, true), 12),
                    new ZepTagFilterOption(
                        new ZepTag("blank", "   ", ZepTagColor.Green, true), 3)
                });
                yield return null;
                yield return null;

                var longRow = tags.Q<VisualElement>("zep-sidebar-tags-row-long");
                var blankRow = tags.Q<VisualElement>("zep-sidebar-tags-row-blank");
                Assert.That(longRow, Is.Not.Null, "the long-named tag must have a row");
                Assert.That(longRow.tooltip, Is.EqualTo(LongName),
                    "an elided tag name is only recoverable by hover");
                Assert.That(blankRow, Is.Not.Null, "the blank-named tag still has a row");
                Assert.That(blankRow.tooltip, Is.Null.Or.Empty,
                    "a whitespace name must leave the tooltip empty rather than pop an empty box");
            }
            finally
            {
                window.Close();
            }
        }

        // The elision moved onto the shared primitive and the duplicate
        // properties were deleted from the asset-row delta; the asset row must
        // still elide, and must still keep its own 12px font.
        [UnityTest]
        public IEnumerator AssetRowLabel_KeepsItsElisionAfterTheDeltaLostTheDuplicates()
        {
            var window = ScriptableObject.CreateInstance<SidebarElisionTestWindow>();
            window.position = new Rect(100f, 100f, 170f, 400f);
            window.Show();
            try
            {
                var root = MountRoot(window);
                var row = new VisualElement();
                row.AddToClassList(ZepUiClasses.AssetRow);
                var cell = new VisualElement();
                cell.AddToClassList(ZepUiClasses.AssetNameCell);
                var label = new Label(LongName);
                label.AddToClassList(ZepUiClasses.RowPrimaryText);
                cell.Add(label);
                row.Add(cell);
                root.Add(row);
                yield return null;
                yield return null;

                Assert.That(label.resolvedStyle.whiteSpace, Is.EqualTo(WhiteSpace.NoWrap),
                    "the asset row must not have lost nowrap when the duplicate was deleted");
                Assert.That(label.resolvedStyle.textOverflow, Is.EqualTo(TextOverflow.Ellipsis),
                    "the asset row must not have lost the ellipsis when the duplicate was deleted");
                Assert.That(label.resolvedStyle.fontSize, Is.EqualTo(12f).Within(0.01f),
                    "the asset-row delta still owns the asset label's own font size");
            }
            finally
            {
                window.Close();
            }
        }

        private static FolderTreeWidget MountTree(EditorWindow window)
        {
            var root = MountRoot(window);
            var tree = new FolderTreeWidget(
                "Assets",
                new UnityZepIconRepository(),
                ChildrenQuery(BuildFolders()));
            root.Add(tree);
            return tree;
        }

        // The window root stands in for the panel root: the two sheets the
        // shell attaches, plus the class the Defaults variable table is scoped
        // to.
        private static VisualElement MountRoot(EditorWindow window)
        {
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            AttachSheet(root, ZepUiClasses.PanelStyleSheetPath);
            AttachSheet(root, ZepUiClasses.DefaultsStyleSheetPath);
            return root;
        }

        private static void AttachSheet(VisualElement root, string path)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            Assert.That(sheet, Is.Not.Null, $"stylesheet missing: {path}");
            root.styleSheets.Add(sheet);
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

        private static List<ZepFolderEntry> BuildFolders()
        {
            return new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry(LongName, LongFolderPath, 1, false),
                new ZepFolderEntry("UI", ShortFolderPath, 1, false)
            };
        }

        private sealed class SidebarElisionTestWindow : EditorWindow
        {
        }
    }
}
