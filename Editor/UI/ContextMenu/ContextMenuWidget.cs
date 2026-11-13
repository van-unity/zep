using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Core.FileOperations;
using Zep.Editor.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI.ContextMenu
{
    // Owns every right-click / + button menu shown by Zep. Menus
    // render through ContextMenuPopupRenderer: the panel's own themed popup
    // windows, which escape the editor window like a native menu does. With no
    // owner element (tests, batch mode) the renderer falls back to GenericMenu.
    //
    // Menu items the panel handles are bound to ContextMenuActions methods at
    // build time via ZepContextMenuItem.OnClick. Items the provider
    // produces that we don't recognise fall through to provider.Execute, which
    // dispatches them through Unity's native menu system.
    internal sealed class ContextMenuWidget
    {
        private readonly UnityAssetsContextMenuProvider _provider;
        private readonly ContextMenuActions _actions;
        // The shell (or any element inside it): the popup borrows its theme and
        // resolves the host window's desktop origin from it.
        private readonly UnityEngine.UIElements.VisualElement _owner;

        internal ContextMenuWidget(
            UnityAssetsContextMenuProvider provider,
            ContextMenuActions actions,
            UnityEngine.UIElements.VisualElement owner = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _owner = owner;
        }

        internal void Show(ZepContextMenuRequest request)
        {

            ShowStandardMenu(request);
        }

        internal void ShowForCreateButton(
            ZepPaneState pane,
            string currentFolderPath,
            Rect plusButtonWorldRect,
            ZepController commandHandler)
        {
            var items = BuildCreateButtonItems(pane, currentFolderPath, commandHandler);
            ContextMenuPopupRenderer.Show(_owner, items, null, plusButtonWorldRect);
        }

        // Split out from ShowForCreateButton so the item list it builds — and
        // each item's hotkey — can be asserted directly in tests without
        // driving ContextMenuPopupRenderer (which needs a live host window).
        internal List<ZepContextMenuItem> BuildCreateButtonItems(
            ZepPaneState pane,
            string currentFolderPath,
            ZepController commandHandler)
        {
            var targetFolder = string.IsNullOrWhiteSpace(currentFolderPath) ? "Assets" : currentFolderPath;
            var canWrite = commandHandler != null && commandHandler.CanWrite(AssetPath.From(targetFolder));

            return new List<ZepContextMenuItem>(4)
            {
                BuildOwnedItem("Zep/Create/Folder", "New Folder", canWrite, string.Empty, () => _actions.PromptCreateFolderAt(pane, targetFolder), hotkey: "%N"),
                BuildOwnedItem("Zep/Create/Script", "New Script", canWrite, string.Empty, () => _actions.PromptCreateScriptAt(pane, targetFolder)),
                BuildOwnedItem("Zep/Create/Scene", "New Scene", canWrite, string.Empty, () => _actions.PromptCreateSceneAt(pane, targetFolder)),
                BuildOwnedItem("Zep/Create/Material", "New Material", canWrite, string.Empty, () => _actions.PromptCreateMaterialAt(pane, targetFolder)),
            };
        }

        // === Internal: standard right-click menu ===
        private void ShowStandardMenu(ZepContextMenuRequest request)
        {
            var contextResolved = TryBuildContextObjects(
                request.ContextPaths,
                out var contextObjects);

            var providerItems = BuildProviderItems(
                request,
                contextResolved,
                contextObjects);

            var menuItems = BuildStandardMenuItems(request, providerItems);
            if (menuItems.Count == 0)
            {
                return;
            }

            ContextMenuPopupRenderer.Show(
                _owner,
                menuItems,
                item => DispatchProviderItem(request, item),
                request.Anchor);
        }

        internal IReadOnlyList<ZepContextMenuItem> BuildProviderItems(
            ZepContextMenuRequest request,
            bool contextResolved,
            IReadOnlyList<Object> contextObjects)
        {
            if (contextResolved && _provider.IsAvailable)
            {
                PrepareProviderSelection(request, contextObjects);
                var providerItems = _provider.BuildAssetsMenu(contextObjects);
                if (!_provider.IsAvailable)
                {
                    // Reflection failed during construction after the provider
                    // had installed the captured Selection. Its owned fallback
                    // uses an empty Unity context, so register and apply that
                    // exact selection before the resulting callback arrives.
                    PrepareProviderSelection(request, Array.Empty<Object>());
                    _provider.ClearContext();
                    return Array.Empty<ZepContextMenuItem>();
                }

                return providerItems;
            }

            PrepareProviderSelection(request, Array.Empty<Object>());
            _provider.ClearContext();
            return Array.Empty<ZepContextMenuItem>();
        }

        internal List<ZepContextMenuItem> BuildStandardMenuItems(
            ZepContextMenuRequest request,
            IReadOnlyList<ZepContextMenuItem> providerItems)
        {
            var targetFolder = ResolveCreateTargetFolder(
                request.ContextPaths,
                request.CreateTargetFolderPath,
                request.CommandHandler);
            // Resolved once per open by the caller that built the request.
            var selectionIsWritable = request.CanModifyAllContextPaths;
            // Create items are gated by the CREATE TARGET's writability (same
            // gate as the + button), not by the selection modify-gate: project
            // roots report not-modifiable (no rename/delete of Assets/) while
            // creating UNDER them is fine. Conflating the two greyed out
            // Create on every empty-space right-click in a root.
            var creationAllowed = request.IsCreationAllowed
                && request.CommandHandler != null
                && request.CommandHandler.CanWrite(AssetPath.From(targetFolder));
            var menuItems = new List<ZepContextMenuItem>(
                WithKnownProviderBindings(
                    providerItems,
                    targetFolder,
                    selectionIsWritable,
                    creationAllowed,
                    request));

            var hasDuplicate = false;
            var deleteIndex = -1;
            for (var i = 0; i < menuItems.Count; i++)
            {
                var commandPath = menuItems[i].NormalizedCommandPath;
                if (string.Equals(commandPath, "Assets/Duplicate", StringComparison.OrdinalIgnoreCase))
                {
                    hasDuplicate = true;
                }
                else if (deleteIndex < 0
                    && string.Equals(commandPath, "Assets/Delete", StringComparison.OrdinalIgnoreCase))
                {
                    deleteIndex = i;
                }
            }

            // Not on a background menu: its target is the browsed folder, and
            // Unity's own background menu offers no Duplicate there; the panel's
            // would reveal the copy in the parent, ejecting the user from the
            // folder they right-clicked in.
            if (request.HasTargets && !hasDuplicate && !request.IsBackground)
            {
                var duplicate = BuildOwnedItem(
                    "Assets/Duplicate",
                    "Duplicate",
                    selectionIsWritable,
                    "⎘",
                    () =>
                    {
                        if (request.AreContextTargetsCurrent())
                        {
                            _actions.DuplicateContextTargets(request);
                        }
                    },
                    hotkey: "%D");
                if (deleteIndex >= 0)
                {
                    menuItems.Insert(deleteIndex, duplicate);
                }
                else
                {
                    menuItems.Add(duplicate);
                }
            }

            AppendCompressItem(menuItems, request);
            AppendFinderTagItems(menuItems, request);
            AppendFavoriteItem(menuItems, request);
            return menuItems;
        }

        // One folder target — a sidebar tree row, or a single folder in the
        // list or grid — offers its Favorites toggle; the label reads the
        // current membership so the item says what it will do.
        private void AppendFavoriteItem(
            List<ZepContextMenuItem> items,
            ZepContextMenuRequest request)
        {
            if (items == null || !_actions.OffersFavorites || request.ContextPaths.Count != 1) return;
            var path = request.ContextPaths[0];
            if (!request.IsFolderTarget(path)) return;

            var isFavorite = _actions.IsFavorite(path);
            if (items.Count > 0 && !items[items.Count - 1].IsSeparator)
            {
                items.Add(ZepContextMenuItem.CreateSeparator());
            }

            items.Add(BuildOwnedItem(
                isFavorite ? "Zep/Favorites/Remove" : "Zep/Favorites/Add",
                isFavorite ? "Remove from Favorites" : "Add to Favorites",
                true,
                "★",
                () => _actions.ToggleFavorite(path)));
        }

        private void DispatchProviderItem(
            ZepContextMenuRequest request,
            ZepContextMenuItem item)
        {
            if (request.CommandHandler == null
                || !request.CommandHandler.IsLivePane(request.Pane)
                || !request.AreContextTargetsCurrent())
            {
                return;
            }

            if (!TryBuildContextObjects(request.ContextPaths, out var capturedObjects))
            {
                PrepareProviderSelection(request, capturedObjects);
                _provider.ClearContext();
                return;
            }

            PrepareProviderSelection(request, capturedObjects);
            _provider.Execute(item, capturedObjects);
        }

        internal string ResolveCreateTargetFolder(
            IReadOnlyList<string> contextPaths,
            string browseTargetFolder,
            ZepController commandHandler = null)
        {
            if (contextPaths != null && contextPaths.Count == 1 && !string.IsNullOrWhiteSpace(contextPaths[0]))
            {
                var single = contextPaths[0];
                if (IndexedPathQueries.IsFolder(commandHandler, single))
                {
                    return single;
                }

                // The Packages mount lists packages, not folder contents you
                // can create into — it is its own (unwritable) target, never
                // a fall-through to the browse target. Without this, browsing
                // Packages lit up Create and would have created into Assets/.
                // CanWrite vetoes the mount (no owning package), matching
                // Unity's stock browser. ("Assets" never reaches here: it is
                // a real folder and returned above.)
                if (AssetPathClassification.IsProjectRootPath(single))
                {
                    return single;
                }

                var parent = System.IO.Path.GetDirectoryName(single);
                return string.IsNullOrWhiteSpace(parent) ? browseTargetFolder : parent.Replace('\\', '/');
            }

            // Multi-selection or no context: create into the folder being
            // browsed, not a blanket "Assets" — a blanket root re-enabled
            // Create while browsing an immutable package (its CanWrite must
            // decide) and created far away from where the user was looking.
            return browseTargetFolder;
        }

        // Returns a new list where items the panel handles have OnClick populated.
        // Items the panel doesn't recognise are returned with OnClick == null so
        // the renderer falls through to provider.Execute via the dispatchUnbound
        // callback. The "Assets/Create/..." rebind routes every Create item through
        // the orchestrator so Unity's built-in Project Browser cannot hijack the
        // rename overlay for a Create action dispatched from our window.
        //
        // selectionIsWritable gates modify-actions (Rename/Duplicate/Delete) so
        // they appear disabled when the selection contains read-only paths
        // (e.g. content under an immutable upm package). Unity's stock validators
        // don't model package immutability, so we apply that filter here.
        // Create items are gated by creationAllowed instead — they write to the
        // create TARGET folder, not to the selection, and a project root is a
        // valid create target even though it can't itself be modified.
        //
        // creationAllowed is false outside Browser mode (Tags):
        // those views have no real creation target, so every "Assets/Create/..."
        // entry — and the Create submenu itself — render disabled regardless of
        // writability.
        internal IReadOnlyList<ZepContextMenuItem> WithKnownProviderBindings(
            IReadOnlyList<ZepContextMenuItem> items,
            string targetFolder,
            bool selectionIsWritable,
            bool creationAllowed,
            ZepContextMenuRequest request)
        {
            if (request.Pane == null)
            {
                throw new ArgumentException("A live pane target is required.", nameof(request));
            }

            if (items == null || items.Count == 0)
            {
                return items ?? Array.Empty<ZepContextMenuItem>();
            }

            var result = new List<ZepContextMenuItem>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                result.Add(RebindItem(
                    items[i],
                    targetFolder,
                    selectionIsWritable,
                    creationAllowed,
                    request));
            }

            return result;
        }

        private ZepContextMenuItem RebindItem(
            ZepContextMenuItem item,
            string targetFolder,
            bool selectionIsWritable,
            bool creationAllowed,
            ZepContextMenuRequest request)
        {
            if (item.HasChildren)
            {
                var reboundChildren = WithKnownProviderBindings(
                    item.Children,
                    targetFolder,
                    selectionIsWritable,
                    creationAllowed,
                    request);
                // A submenu is only useful if it has at least one usable item under
                // it. The provider enables submenus whenever they have any child,
                // which after the writability gate can leave a "Create" parent
                // pointing to an entirely greyed-out child list. Propagate the
                // disabled state up so the submenu itself is greyed, matching
                // Unity's stock Project window behavior.
                var submenuEnabled = item.IsEnabled && HasAnyEnabledChild(reboundChildren);
                if (ReferenceEquals(reboundChildren, item.Children) && submenuEnabled == item.IsEnabled)
                {
                    return item;
                }

                return new ZepContextMenuItem(
                    item.CommandPath,
                    item.NormalizedCommandPath,
                    item.Label,
                    item.Hotkey,
                    submenuEnabled,
                    item.IsChecked,
                    item.IsSeparator,
                    reboundChildren,
                    item.LeadingIcon,
                    item.IconTagColor,
                    item.OnClick,
                    item.IsDestructive);
            }

            var commandPath = item.NormalizedCommandPath;
            if (string.IsNullOrEmpty(commandPath))
            {
                return item;
            }

            Action onClick;
            bool isWriteAction;
            bool isCreateAction = false;
            bool requiresTarget = false;
            switch (commandPath)
            {
                case "Assets/Open":
                    onClick = () => _actions.OpenContextTargets(request);
                    isWriteAction = false;
                    requiresTarget = true;
                    break;
                case "Assets/Rename":
                    onClick = () => _actions.PromptRenameContextTarget(request);
                    isWriteAction = true;
                    requiresTarget = true;
                    break;
                case "Assets/Duplicate":
                    onClick = () => _actions.DuplicateContextTargets(request);
                    isWriteAction = true;
                    requiresTarget = true;
                    break;
                case "Assets/Delete":
                    onClick = () => _actions.DeleteContextTargets(request);
                    isWriteAction = true;
                    requiresTarget = true;
                    break;
                case "Assets/Reveal in Finder":
                case "Assets/Show in Explorer":
                    onClick = () => _actions.RevealContextTargets(request);
                    isWriteAction = false;
                    requiresTarget = true;
                    break;
                case "Assets/Create/Folder":
                    // Not a write action on the SELECTION — creation writes to
                    // targetFolder, whose writability creationAllowed carries.
                    onClick = () => _actions.PromptCreateFolderAt(request.Pane, targetFolder);
                    isWriteAction = false;
                    isCreateAction = true;
                    break;
                default:
                    if (commandPath.StartsWith("Assets/Create/", StringComparison.OrdinalIgnoreCase))
                    {
                        var capturedItem = item;
                        onClick = () => _actions.PromptCreateAsset(
                            request.Pane,
                            capturedItem,
                            targetFolder,
                            request.ContextPaths);
                        isWriteAction = false;
                        isCreateAction = true;
                    }
                    else
                    {
                        return item;
                    }

                    break;
            }

            if (requiresTarget)
            {
                var capturedAction = onClick;
                onClick = () =>
                {
                    if (request.AreContextTargetsCurrent())
                    {
                        capturedAction();
                    }
                };
            }

            var isDelete = string.Equals(commandPath, "Assets/Delete", StringComparison.OrdinalIgnoreCase);
            var enabled = item.IsEnabled
                && (!isWriteAction || selectionIsWritable)
                && (!isCreateAction || creationAllowed)
                && (!requiresTarget || request.HasTargets)
                && (!isDelete || !_actions.IsEditorBusy(out _));
            var label = isDelete && request.ContextPaths.Count > 1
                    ? "Delete Items"
                    : item.Label;

            return new ZepContextMenuItem(
                item.CommandPath,
                item.NormalizedCommandPath,
                label,
                item.Hotkey,
                enabled,
                item.IsChecked,
                item.IsSeparator,
                item.Children,
                item.LeadingIcon,
                item.IconTagColor,
                onClick,
                isDelete || item.IsDestructive);
        }

        // === Menu item assembly ===
        internal void AppendCompressItem(
            List<ZepContextMenuItem> items,
            ZepContextMenuRequest request)
        {
            var contextPaths = request.ContextPaths;
            if (items == null || contextPaths == null || contextPaths.Count == 0)
            {
                return;
            }

            // Compress writes to a user-chosen location outside the project, so
            // the source only needs to be readable. Exclude the project root:
            // compressing the whole project from here is almost certainly a mistake.
            for (var i = 0; i < contextPaths.Count; i++)
            {
                var path = contextPaths[i];
                if (string.IsNullOrWhiteSpace(path)
                    || AssetPathClassification.IsProjectRootPath(path))
                {
                    return;
                }
            }

            var label = contextPaths.Count == 1
                ? "Compress \"" + AssetPathFormat.GetFileName(contextPaths[0]) + "\""
                : "Compress " + contextPaths.Count + " Items";

            if (items.Count > 0 && !items[items.Count - 1].IsSeparator)
            {
                items.Add(ZepContextMenuItem.CreateSeparator());
            }

            items.Add(new ZepContextMenuItem(
                "Zep/Compress",
                "Zep/Compress",
                label,
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                "🗜",
                onClick: () => _actions.CompressContextTargets(request)));
        }

        private void AppendFinderTagItems(
            List<ZepContextMenuItem> items,
            ZepContextMenuRequest request)
        {
            if (items == null) return;
            var canTag = ZepSelectionPredicates.CanTagAllSelectedPaths(request.CommandHandler, request.ContextPaths);
            var tagsItem = ContextMenuTagSubmenuBuilder.Build(request, canTag, _actions);
            if (tagsItem == null) return;
            if (items.Count > 0 && !items[items.Count - 1].IsSeparator)
            {
                items.Add(ZepContextMenuItem.CreateSeparator());
            }

            items.Add(tagsItem);
        }

        private static ZepContextMenuItem BuildOwnedItem(
            string commandPath,
            string label,
            bool isEnabled,
            string icon,
            Action onClick,
            bool isDestructive = false,
            string hotkey = "")
        {
            return new ZepContextMenuItem(
                commandPath,
                commandPath,
                label,
                hotkey,
                isEnabled,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                icon,
                onClick: onClick,
                isDestructive: isDestructive);
        }

        // === Helpers ===
        internal bool TryBuildContextObjects(
            IReadOnlyList<string> contextPaths,
            out IReadOnlyList<Object> contextObjects)
        {
            if (contextPaths == null || contextPaths.Count == 0)
            {
                contextObjects = Array.Empty<Object>();
                return true;
            }

            var objects = new List<Object>(contextPaths.Count);
            // Two paths that resolve to the same object mean the selection was
            // built wrong, and the whole set is refused. Asking the growing
            // result list made that check cost one pass per path, so
            // right-clicking a 5,000-item selection burned 12.5M reference
            // compares before the menu appeared. UnityEngine.Object hashes and
            // compares on its instance id, so a set answers the same question
            // in one probe.
            var seen = new HashSet<Object>();
            for (var i = 0; i < contextPaths.Count; i++)
            {
                var path = contextPaths[i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    contextObjects = Array.Empty<Object>();
                    return false;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null || !seen.Add(asset))
                {
                    contextObjects = Array.Empty<Object>();
                    return false;
                }

                objects.Add(asset);
            }

            contextObjects = objects;
            return true;
        }

        private static void PrepareProviderSelection(
            ZepContextMenuRequest request,
            IReadOnlyList<Object> contextObjects)
        {
            if (request.CommandHandler == null
                || !request.CommandHandler.IsLivePane(request.Pane))
            {
                return;
            }

            var contextCount = contextObjects?.Count ?? 0;

            var currentSelection = Selection.objects;
            if (currentSelection != null && currentSelection.Length == contextCount)
            {
                var isSame = true;
                for (var i = 0; i < contextCount; i++)
                {
                    if (currentSelection[i] != contextObjects[i])
                    {
                        isSame = false;
                        break;
                    }
                }

                if (isSame)
                {
                    return;
                }
            }

            var paths = new List<string>(contextCount);
            for (var i = 0; i < contextCount; i++)
            {
                var path = AssetDatabase.GetAssetPath(contextObjects[i]);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            request.CommandHandler.ExpectPanelSelectionChange(request.Pane, paths);
        }

        private static bool HasAnyEnabledChild(IReadOnlyList<ZepContextMenuItem> items)
        {
            if (items == null) return false;
            for (var i = 0; i < items.Count; i++)
            {
                var child = items[i];
                if (child.IsSeparator) continue;
                if (child.IsEnabled) return true;
            }

            return false;
        }

    }
}
