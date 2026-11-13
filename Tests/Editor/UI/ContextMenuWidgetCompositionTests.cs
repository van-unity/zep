using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.UI.Tests
{
    public sealed class ContextMenuWidgetCompositionTests
    {
        // ---------------------------------------------------------------------------
        // Shared infrastructure
        // ---------------------------------------------------------------------------

        private static ContextMenuActions NullActions() =>
            new ContextMenuActions(null, null, null, null, null, null);

        // VisualElement ctor is safe to call in EditMode tests (no EditorWindow or
        // scene required).
        private static ContextMenuWidget MakeWidget()
        {
            var provider = new UnityAssetsContextMenuProvider();
            var actions = NullActions();
            return new ContextMenuWidget(provider, actions);
        }

        private static ZepPaneState MakePane() =>
            new ZepState().PrimaryPane;

        private static ZepContextMenuRequest MakeRequest(
            IReadOnlyList<string> contextPaths = null,
            ZepPaneState pane = null)
        {
            contextPaths = contextPaths ?? new[] { "Assets/Foo.cs" };
            var assignedTags = new Dictionary<string, ICollection<string>>(
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < contextPaths.Count; i++)
            {
                assignedTags[contextPaths[i]] = Array.Empty<string>();
            }

            return new ZepContextMenuRequest(
                pane ?? MakePane(),
                null,
                contextPaths,
                "Assets",
                Array.Empty<ZepTagFilterOption>(),
                assignedTags,
                Array.Empty<ZepAssetEntry>(),
                null,
                isOpenFolderEnabled: true,
                isDeleteEnabled: true,
                isCreationAllowed: true,
                canModifyAllContextPaths: false);
        }

        // ---------------------------------------------------------------------------
        // Add / Remove from Favorites
        // ---------------------------------------------------------------------------

        private static ZepContextMenuRequest MakeFolderRequest(string folderPath)
        {
            var folder = new ZepFolderEntry(
                AssetPathFormat.GetFileName(folderPath), folderPath, 1, false);
            return new ZepContextMenuRequest(
                MakePane(),
                null,
                new[] { folderPath },
                folderPath,
                Array.Empty<ZepTagFilterOption>(),
                new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderPath] = Array.Empty<string>()
                },
                Array.Empty<ZepAssetEntry>(),
                folder,
                isOpenFolderEnabled: true,
                isDeleteEnabled: true,
                isCreationAllowed: true,
                canModifyAllContextPaths: true,
                targetKind: ZepContextMenuTargetKind.SidebarFolder);
        }

        private static ContextMenuWidget MakeWidgetWithFavorites(HashSet<string> favorites)
        {
            var actions = new ContextMenuActions(
                null, null, null, null, null, null,
                isFavorite: favorites.Contains,
                toggleFavorite: path =>
                {
                    if (!favorites.Remove(path))
                    {
                        favorites.Add(path);
                    }
                });
            return new ContextMenuWidget(new UnityAssetsContextMenuProvider(), actions);
        }

        private static ZepContextMenuItem FavoritesItem(IReadOnlyList<ZepContextMenuItem> items)
        {
            ZepContextMenuItem found = null;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].NormalizedCommandPath.StartsWith("Zep/Favorites/", StringComparison.Ordinal))
                {
                    Assert.That(found, Is.Null, "one favorites item at most");
                    found = items[i];
                }
            }

            return found;
        }

        [Test]
        public void FolderTarget_OffersAddToFavorites_AndTheLabelFollowsMembership()
        {
            const string folderPath = "Assets/Art";
            var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var widget = MakeWidgetWithFavorites(favorites);

            var add = FavoritesItem(widget.BuildStandardMenuItems(
                MakeFolderRequest(folderPath), Array.Empty<ZepContextMenuItem>()));
            Assert.That(add, Is.Not.Null, "a single folder target offers its favorites toggle");
            Assert.That(add.Label, Is.EqualTo("Add to Favorites"));
            Assert.That(add.NormalizedCommandPath, Is.EqualTo("Zep/Favorites/Add"));
            Assert.That(add.IsEnabled, Is.True);

            GetOnClick(add)();
            Assert.That(favorites, Is.EquivalentTo(new[] { folderPath }));

            var remove = FavoritesItem(widget.BuildStandardMenuItems(
                MakeFolderRequest(folderPath), Array.Empty<ZepContextMenuItem>()));
            Assert.That(remove.Label, Is.EqualTo("Remove from Favorites"));
            Assert.That(remove.NormalizedCommandPath, Is.EqualTo("Zep/Favorites/Remove"));

            GetOnClick(remove)();
            Assert.That(favorites, Is.Empty);
        }

        [Test]
        public void FileTarget_OffersNoFavoritesItem()
        {
            var widget = MakeWidgetWithFavorites(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var items = widget.BuildStandardMenuItems(
                MakeRequest(new[] { "Assets/Foo.cs" }), Array.Empty<ZepContextMenuItem>());

            Assert.That(FavoritesItem(items), Is.Null, "only folders can be favorites");
        }

        [Test]
        public void WithoutFavoritesDelegates_OffersNoFavoritesItem()
        {
            var widget = MakeWidget();

            var items = widget.BuildStandardMenuItems(
                MakeFolderRequest("Assets/Art"), Array.Empty<ZepContextMenuItem>());

            Assert.That(FavoritesItem(items), Is.Null);
        }

        private static Action GetOnClick(ZepContextMenuItem item)
        {
            // OnClick is internal — accessible directly from this assembly via InternalsVisibleTo.
            return item.OnClick;
        }

        private static ZepContextMenuItem Item(string normalizedCommandPath, string label = "Label")
        {
            return new ZepContextMenuItem(
                normalizedCommandPath,
                normalizedCommandPath,
                label,
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>());
        }

        // ---------------------------------------------------------------------------
        // B. AppendCompressItem
        // ---------------------------------------------------------------------------

        [Test]
        public void AppendCompressItem_SingleNonRootPath_AppendsCompressItemWithFileName()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem>();
            widget.AppendCompressItem(items, MakeRequest(new[] { "Assets/Foo.cs" }));

            // Items were empty before call, so no leading separator rule fires.
            // Should append exactly one item: the Compress item itself.
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(items[0].Label, Is.EqualTo("Compress \"Foo.cs\""));
        }

        [Test]
        public void AppendCompressItem_SingleNonRootPathWithExistingItems_AppendsSeparatorThenCompressItem()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Open", "Open") };
            widget.AppendCompressItem(items, MakeRequest(new[] { "Assets/Foo.cs" }));

            // Pre-existing non-separator item triggers separator insertion first.
            Assert.That(items.Count, Is.EqualTo(3));
            Assert.That(items[1].IsSeparator, Is.True);
            Assert.That(items[2].Label, Is.EqualTo("Compress \"Foo.cs\""));
        }

        [Test]
        public void AppendCompressItem_MultiPath_LabelIsCompressNItems()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem>();
            widget.AppendCompressItem(items, MakeRequest(new[] { "Assets/A.cs", "Assets/B.cs", "Assets/C.cs" }));

            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(items[0].Label, Is.EqualTo("Compress 3 Items"));
        }

        [Test]
        public void AppendCompressItem_ProjectRootAssets_NoItemAppended()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem>();
            widget.AppendCompressItem(items, MakeRequest(new[] { "Assets" }));

            Assert.That(items.Count, Is.EqualTo(0));
        }

        [Test]
        public void AppendCompressItem_ProjectRootPackages_NoItemAppended()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem>();
            widget.AppendCompressItem(items, MakeRequest(new[] { "Packages" }));

            Assert.That(items.Count, Is.EqualTo(0));
        }

        [Test]
        public void AppendCompressItem_EmptyContextPaths_NoItemAppended()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem>();
            widget.AppendCompressItem(items, MakeRequest(Array.Empty<string>()));

            Assert.That(items.Count, Is.EqualTo(0));
        }

        private static IReadOnlyList<string> GetNonSeparatorLabels(List<ZepContextMenuItem> items)
        {
            var labels = new List<string>();
            foreach (var item in items)
            {
                if (!item.IsSeparator)
                {
                    labels.Add(item.Label);
                }
            }

            return labels;
        }

        private static ZepContextMenuItem FindItem(List<ZepContextMenuItem> items, string label)
        {
            foreach (var item in items)
            {
                if (item.Label == label)
                {
                    return item;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------------------
        // D. WithKnownProviderBindings
        // ---------------------------------------------------------------------------

        [Test]
        public void WithKnownProviderBindings_AssetsOpen_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Open") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsRename_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Rename") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDuplicate_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Duplicate") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void BuildStandardMenuItems_ProviderOmitsDuplicate_AddsOneOwnedDuplicateBeforeDelete()
        {
            var widget = MakeWidget();

            var result = widget.BuildStandardMenuItems(
                MakeRequest(),
                new[] { Item("Assets/Delete", "Delete") });

            var duplicateItems = result.FindAll(item =>
                string.Equals(
                    item.NormalizedCommandPath,
                    "Assets/Duplicate",
                    StringComparison.OrdinalIgnoreCase));
            Assert.That(duplicateItems, Has.Count.EqualTo(1));
            Assert.That(GetOnClick(duplicateItems[0]), Is.Not.Null);
            Assert.That(
                result.IndexOf(duplicateItems[0]),
                Is.LessThan(result.FindIndex(item => item.NormalizedCommandPath == "Assets/Delete")));
        }

        [Test]
        public void BuildStandardMenuItems_ProviderSuppliesDuplicate_DoesNotAddAnother()
        {
            var widget = MakeWidget();

            var result = widget.BuildStandardMenuItems(
                MakeRequest(),
                new[]
                {
                    Item("Assets/Duplicate", "Duplicate"),
                    Item("Assets/Delete", "Delete")
                });

            Assert.That(
                result.FindAll(item => item.NormalizedCommandPath == "Assets/Duplicate"),
                Has.Count.EqualTo(1));
        }

        // ---------------------------------------------------------------------------
        // F-015: the panel-built Duplicate and New Folder items advertise the
        // Cmd/Ctrl bindings ZepShortcutController actually dispatches
        // (evt.actionKey + D / N); items the panel has no binding for stay empty.
        // ---------------------------------------------------------------------------

        [Test]
        public void BuildStandardMenuItems_OwnedDuplicate_HotkeyFormatsToCmdOrCtrlD()
        {
            var widget = MakeWidget();

            var result = widget.BuildStandardMenuItems(
                MakeRequest(),
                new[] { Item("Assets/Delete", "Delete") });

            var duplicate = result.Find(item => item.NormalizedCommandPath == "Assets/Duplicate");
            Assert.That(duplicate, Is.Not.Null);
            Assert.That(ContextMenuHotkeyText.Format(duplicate.Hotkey, macStyle: true), Is.EqualTo("⌘D"));
            Assert.That(ContextMenuHotkeyText.Format(duplicate.Hotkey, macStyle: false), Is.EqualTo("Ctrl+D"));
        }

        [Test]
        public void BuildCreateButtonItems_NewFolder_HotkeyFormatsToCmdOrCtrlN()
        {
            var widget = MakeWidget();

            var items = widget.BuildCreateButtonItems(MakePane(), "Assets", null);

            var newFolder = items.Find(item => item.Label == "New Folder");
            Assert.That(newFolder, Is.Not.Null);
            Assert.That(ContextMenuHotkeyText.Format(newFolder.Hotkey, macStyle: true), Is.EqualTo("⌘N"));
            Assert.That(ContextMenuHotkeyText.Format(newFolder.Hotkey, macStyle: false), Is.EqualTo("Ctrl+N"));
        }

        [Test]
        public void BuildCreateButtonItems_NewScriptSceneMaterial_CarryNoHotkey()
        {
            var widget = MakeWidget();

            var items = widget.BuildCreateButtonItems(MakePane(), "Assets", null);

            Assert.That(items.Find(item => item.Label == "New Script").Hotkey, Is.Empty);
            Assert.That(items.Find(item => item.Label == "New Scene").Hotkey, Is.Empty);
            Assert.That(items.Find(item => item.Label == "New Material").Hotkey, Is.Empty);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDelete_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Delete") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDelete_MultipleTargets_UsesPluralLabel()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Delete", "Delete") };

            var result = widget.WithKnownProviderBindings(
                items,
                "Assets",
                true,
                true,
                MakeRequest(new[] { "Assets/A.cs", "Assets/B.cs" }));

            Assert.That(result[0].Label, Is.EqualTo("Delete Items"));
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDelete_OneTarget_KeepsSingularLabel()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Delete", "Delete") };

            var result = widget.WithKnownProviderBindings(
                items,
                "Assets",
                true,
                true,
                MakeRequest(new[] { "Assets/A.cs" }));

            Assert.That(result[0].Label, Is.EqualTo("Delete"));
        }

        [Test]
        public void WithKnownProviderBindings_AssetsRevealInFinder_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Reveal in Finder") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsShowInExplorer_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Show in Explorer") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsCreateFolder_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Create/Folder") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsCreateMaterial_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Create/Material") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_DeepAssetsCreatePath_OnClickIsNotNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Create/Some Vendor/Foo") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Not.Null);
        }

        [Test]
        public void WithKnownProviderBindings_UnknownPath_OnClickIsNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/SomethingElse") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Null);
        }

        [Test]
        public void WithKnownProviderBindings_EmptyCommandPath_OnClickIsNull()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item(string.Empty) };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(GetOnClick(result[0]), Is.Null);
        }

        [Test]
        public void WithKnownProviderBindings_ParentWithChildAssetsOpenPath_ReboundChildHasOnClick()
        {
            var widget = MakeWidget();
            var child = Item("Assets/Open", "Open");
            var parent = new ZepContextMenuItem(
                "Assets",
                "Assets",
                "Assets",
                string.Empty,
                true,
                false,
                false,
                new[] { child });
            var items = new List<ZepContextMenuItem> { parent };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(result[0].Children[0].OnClick, Is.Not.Null,
                "Recursive walk should have bound OnClick on the child");
        }

        [Test]
        public void WithKnownProviderBindings_LeafItemWithUnmatchablePath_ReturnedItemReferenceIsOriginal()
        {
            var widget = MakeWidget();
            // For leaf items (HasChildren == false) with no matching command path,
            // RebindItem falls through the switch default and returns the original item
            // reference unchanged. The outer list is always newly allocated, but the
            // item inside should be the exact same object.
            var unknown = Item("Assets/SomethingElse");
            var inputList = new List<ZepContextMenuItem> { unknown };

            var result = widget.WithKnownProviderBindings(inputList, "Assets", true, true, MakeRequest());

            // The returned list is a new allocation, but the item inside is the same object.
            Assert.That(ReferenceEquals(result[0], unknown), Is.True,
                "RebindItem should return the original item reference for unmatched leaf items");
        }

        // ---------------------------------------------------------------------------
        // E. WithKnownProviderBindings — write-action gating against selectionIsWritable
        // ---------------------------------------------------------------------------

        [Test]
        public void WithKnownProviderBindings_AssetsDelete_SelectionNotWritable_IsEnabledFalse()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Delete") };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False, "Delete must be disabled when selection contains read-only paths");
            Assert.That(GetOnClick(result[0]), Is.Not.Null, "OnClick should still be bound even when disabled");
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDelete_SelectionWritable_IsEnabledTrue()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Delete") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.True, "Delete stays enabled when selection is writable");
        }

        [Test]
        public void WithKnownProviderBindings_AssetsRename_SelectionNotWritable_IsEnabledFalse()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Rename") };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDuplicate_SelectionNotWritable_IsEnabledFalse()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Duplicate") };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsCreateFolder_CreationNotAllowed_IsEnabledFalse()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Create/Folder") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, false, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsCreateMaterial_CreationNotAllowed_IsEnabledFalse()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Create/Material") };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, false, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False);
        }

        [Test]
        public void WithKnownProviderBindings_CreateFolder_RootSelectionNotModifiable_CreationAllowed_IsEnabledTrue()
        {
            var widget = MakeWidget();
            // Empty-space right-click while browsing Assets/: the context path is
            // the project root, which the modify-gate reports as not writable
            // (no rename/delete of Assets/) — but creating UNDER it is fine.
            // Regression test: conflating the two grayed out Create entirely.
            var items = new List<ZepContextMenuItem> { Item("Assets/Create/Folder") };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.True,
                "Create must follow the create-target gate, not the selection modify-gate");
        }

        [Test]
        public void WithKnownProviderBindings_AssetsOpen_SelectionNotWritable_IsEnabledTrue()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Open") };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            // Open is a read-only action — must stay enabled even on read-only paths.
            Assert.That(result[0].IsEnabled, Is.True);
        }

        [Test]
        public void WithKnownProviderBindings_AssetsRevealInFinder_SelectionNotWritable_IsEnabledTrue()
        {
            var widget = MakeWidget();
            var items = new List<ZepContextMenuItem> { Item("Assets/Reveal in Finder") };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.True);
        }

        [Test]
        public void WithKnownProviderBindings_UnknownPath_SelectionNotWritable_IsEnabledUnchanged()
        {
            var widget = MakeWidget();
            // Items the panel doesn't recognise are returned unchanged — Option A's
            // accepted gap. Provider-supplied IsEnabled survives the rebind.
            var unknown = Item("Assets/SomethingElse");
            var items = new List<ZepContextMenuItem> { unknown };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.True, "Unrecognised items pass through with provider's IsEnabled intact");
        }

        [Test]
        public void WithKnownProviderBindings_CreateSubmenu_CreationNotAllowed_ParentBecomesDisabled()
        {
            var widget = MakeWidget();
            // Provider hands us a "Create" parent enabled because it has children;
            // every child is a create-action that the gate disables. Result: the
            // parent should also report disabled, so users see a single greyed
            // "Create" item rather than a hoverable submenu of greyed leaves.
            var parent = new ZepContextMenuItem(
                "Assets/Create", "Assets/Create", "Create", string.Empty,
                isEnabled: true, isChecked: false, isSeparator: false,
                children: new[]
                {
                    Item("Assets/Create/Folder", "Folder"),
                    Item("Assets/Create/Material", "Material"),
                    Item("Assets/Create/Scene", "Scene"),
                });
            var items = new List<ZepContextMenuItem> { parent };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, false, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False, "Create parent must be disabled when all its children are disabled");
            foreach (var child in result[0].Children)
            {
                Assert.That(child.IsEnabled, Is.False, "All Create children must be disabled");
            }
        }

        [Test]
        public void WithKnownProviderBindings_CreateSubmenu_SelectionWritable_ParentStaysEnabled()
        {
            var widget = MakeWidget();
            var parent = new ZepContextMenuItem(
                "Assets/Create", "Assets/Create", "Create", string.Empty,
                isEnabled: true, isChecked: false, isSeparator: false,
                children: new[] { Item("Assets/Create/Folder", "Folder") });
            var items = new List<ZepContextMenuItem> { parent };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.True);
            Assert.That(result[0].Children[0].IsEnabled, Is.True);
        }

        [Test]
        public void WithKnownProviderBindings_SubmenuWithOneNonWriteChild_ParentStaysEnabledWhenSelectionNotWritable()
        {
            var widget = MakeWidget();
            // Mixed children: a write-action (Delete) and a read-only action (Open).
            // Open stays enabled even when the selection isn't writable, so the
            // parent must stay enabled too.
            var parent = new ZepContextMenuItem(
                "Assets", "Assets", "Assets", string.Empty,
                isEnabled: true, isChecked: false, isSeparator: false,
                children: new[]
                {
                    Item("Assets/Delete", "Delete"),
                    Item("Assets/Open", "Open"),
                });
            var items = new List<ZepContextMenuItem> { parent };

            var result = widget.WithKnownProviderBindings(items, "Assets", false, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.True, "Parent stays enabled because Open is still usable");
            Assert.That(result[0].Children[0].IsEnabled, Is.False, "Delete is disabled");
            Assert.That(result[0].Children[1].IsEnabled, Is.True, "Open stays enabled");
        }

        [Test]
        public void WithKnownProviderBindings_NestedSubmenu_DisabledLeavesPropagateUpThroughGrandparent()
        {
            var widget = MakeWidget();
            // Create > 3rdParty > [Material, Texture] — all create-actions. After
            // the gate, every leaf is disabled, the 3rdParty submenu becomes
            // disabled, and Create — whose only child is now the disabled
            // 3rdParty — also becomes disabled.
            var thirdParty = new ZepContextMenuItem(
                "Assets/Create/3rdParty", "Assets/Create/3rdParty", "3rdParty", string.Empty,
                isEnabled: true, isChecked: false, isSeparator: false,
                children: new[]
                {
                    Item("Assets/Create/3rdParty/Material", "Material"),
                    Item("Assets/Create/3rdParty/Texture", "Texture"),
                });
            var create = new ZepContextMenuItem(
                "Assets/Create", "Assets/Create", "Create", string.Empty,
                isEnabled: true, isChecked: false, isSeparator: false,
                children: new[] { thirdParty });
            var items = new List<ZepContextMenuItem> { create };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, false, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False, "Create disabled");
            Assert.That(result[0].Children[0].IsEnabled, Is.False, "3rdParty disabled");
        }

        [Test]
        public void WithKnownProviderBindings_AssetsDelete_StartedDisabled_StaysDisabledRegardlessOfWritability()
        {
            var widget = MakeWidget();
            // If the provider already disabled an item (e.g. an empty selection that
            // makes Delete invalid), the writability gate must not re-enable it.
            var disabled = new ZepContextMenuItem(
                "Assets/Delete", "Assets/Delete", "Delete", string.Empty,
                false, false, false, Array.Empty<ZepContextMenuItem>());
            var items = new List<ZepContextMenuItem> { disabled };

            var result = widget.WithKnownProviderBindings(items, "Assets", true, true, MakeRequest());

            Assert.That(result[0].IsEnabled, Is.False);
        }

        // ---------------------------------------------------------------------------
        // F. ResolveCreateTargetFolder — where Create writes to
        // ---------------------------------------------------------------------------

        [Test]
        public void ResolveCreateTargetFolder_PackagesMount_ReturnsTheMountNotAssets()
        {
            var widget = MakeWidget();

            // Regression: the mount is not a folder asset, and the parent-path
            // fallback used to silently retarget it to "Assets" — lighting up
            // Create while browsing Packages (Unity's own browser grays it)
            // and creating into Assets/. The mount must stay the target so
            // CanWrite can veto it.
            var target = widget.ResolveCreateTargetFolder(new[] { "Packages" }, "Assets");

            Assert.That(target, Is.EqualTo("Packages"));
        }

        [Test]
        public void ResolveCreateTargetFolder_AssetsRoot_ReturnsAssets()
        {
            var widget = MakeWidget();

            var target = widget.ResolveCreateTargetFolder(new[] { "Assets" }, "Assets");

            Assert.That(target, Is.EqualTo("Assets"));
        }

        [Test]
        public void ResolveCreateTargetFolder_NonFolderAssetPath_ReturnsItsParent()
        {
            var widget = MakeWidget();

            var target = widget.ResolveCreateTargetFolder(new[] { "Assets/Foo/bar.mat" }, "Assets");

            Assert.That(target, Is.EqualTo("Assets/Foo"));
        }

        [Test]
        public void ResolveCreateTargetFolder_MultiSelection_ReturnsTheBrowseTargetNotAssets()
        {
            var widget = MakeWidget();

            // Regression: multi-select right-click while browsing an immutable
            // package used the blanket "Assets" fallback — writable, so Create
            // lit up and created far away from the browsed folder. The browse
            // target keeps CanWrite in charge (registry package → disabled,
            // embedded package / Assets subfolder → enabled, in place).
            var target = widget.ResolveCreateTargetFolder(
                new[] { "Packages/com.unity.x/Runtime/a.cs", "Packages/com.unity.x/Runtime/b.cs" },
                "Packages/com.unity.x");

            Assert.That(target, Is.EqualTo("Packages/com.unity.x"));
        }

        // ---------------------------------------------------------------------------
        // Context-object build and captured identities (audit 4.30)
        // ---------------------------------------------------------------------------

        [Test]
        public void TryBuildContextObjects_WithTheSameAssetTwice_RefusesTheWholeSet()
        {
            // The dedup is what keeps a malformed selection from acting twice
            // on one asset. It moved from a scan of the growing result list to
            // a set probe; the answer must not change.
            var suffix = Guid.NewGuid().ToString("N");
            var path = "Assets/ContextObjectsDedup_" + suffix + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), path);
            AssetDatabase.SaveAssets();
            try
            {
                var widget = MakeWidget();

                Assert.That(
                    widget.TryBuildContextObjects(new[] { path }, out var single),
                    Is.True);
                Assert.That(single.Count, Is.EqualTo(1));

                Assert.That(
                    widget.TryBuildContextObjects(new[] { path, path }, out var duplicated),
                    Is.False,
                    "a path resolving to an already-collected object refuses the set");
                Assert.That(duplicated, Is.Empty);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void ContextAssetGuids_AreCapturedWhenTheMenuOpens_NotWhenAnActionRuns()
        {
            // Audit 4.30 proposed making this capture lazy. It cannot be: every
            // reader of the captured GUIDs is a menu callback that runs after
            // the user picked an item, so a capture on first read would compare
            // the project against itself and AreContextTargetsCurrent could
            // never refuse anything. This test is the guard — it fails under a
            // lazy capture and passes under the eager one.
            var suffix = Guid.NewGuid().ToString("N");
            var keptPath = "Assets/ContextCaptureKept_" + suffix + ".asset";
            var targetPath = "Assets/ContextCaptureTarget_" + suffix + ".asset";
            var replacementPath = "Assets/ContextCaptureReplacement_" + suffix + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), keptPath);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), targetPath);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), replacementPath);
            AssetDatabase.SaveAssets();

            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                MakeRequest(new[] { keptPath, targetPath });

                Assert.That(
                    ZepPerfLog.CaptureSnapshot()
                        .GetCount(ZepPerfCounter.AssetDatabaseGuidQueries),
                    Is.EqualTo(2),
                    "one identity read per context path, taken while the menu opens");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
                AssetDatabase.DeleteAsset(keptPath);
                AssetDatabase.DeleteAsset(targetPath);
                AssetDatabase.DeleteAsset(replacementPath);
            }
        }

        [Test]
        public void ContextTargets_ReplacedBeforeAnyActionRuns_AreRefusedByTheFirstCheck()
        {
            // The other half of the same guard, asserted on its own so it fails
            // independently: every reader of the captured identities is a menu
            // callback that runs after the user picked an item, so a capture
            // deferred to the first read would compare the project against
            // itself and this check could never refuse anything.
            var suffix = Guid.NewGuid().ToString("N");
            var keptPath = "Assets/ContextRefuseKept_" + suffix + ".asset";
            var targetPath = "Assets/ContextRefuseTarget_" + suffix + ".asset";
            var replacementPath = "Assets/ContextRefuseReplacement_" + suffix + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), keptPath);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), targetPath);
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContextMenuCompositionMarker>(), replacementPath);
            AssetDatabase.SaveAssets();
            try
            {
                var request = MakeRequest(new[] { keptPath, targetPath });

                // The project changes while the menu is open, before ANY action
                // has run — so before any lazy capture could have happened.
                Assert.That(AssetDatabase.DeleteAsset(targetPath), Is.True);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.MoveAsset(replacementPath, targetPath), Is.Empty);
                AssetDatabase.SaveAssets();

                Assert.That(
                    request.AreContextTargetsCurrent(),
                    Is.False,
                    "the FIRST staleness check must already refuse a replaced target");
            }
            finally
            {
                AssetDatabase.DeleteAsset(keptPath);
                AssetDatabase.DeleteAsset(targetPath);
                AssetDatabase.DeleteAsset(replacementPath);
            }
        }

        private sealed class ContextMenuCompositionMarker : ScriptableObject
        {
        }
    }
}
