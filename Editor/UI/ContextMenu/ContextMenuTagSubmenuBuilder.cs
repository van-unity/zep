using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI;
using UnityEngine;

namespace Zep.Editor.UI.ContextMenu
{
    internal static class ContextMenuTagSubmenuBuilder
    {
        // Cap the favourites cluster so the user sees a tight set above the
        // overflow separator; beyond this the menu starts to feel like a list.
        private const int FavouriteTagDisplayLimit = 7;

        internal static ZepContextMenuItem Build(
            ZepContextMenuRequest request,
            bool canTag,
            ContextMenuActions actions)
        {
            return Build(request, canTag, actions, Application.platform);
        }

        // Platform is a parameter (not read from Application.platform directly)
        // so the ⌃0/⌃1…⌃7 favourite-tag hotkeys this builder advertises — the
        // same Finder-tag chords ZepShortcutController dispatches from,
        // macOS-only — can be exercised deterministically for every platform in
        // tests without needing to run on that OS. Production always goes
        // through the three-argument overload above.
        internal static ZepContextMenuItem Build(
            ZepContextMenuRequest request,
            bool canTag,
            ContextMenuActions actions,
            RuntimePlatform platform)
        {
            if (request.AssignedTagsByPath == null)
            {
                return null;
            }

            var contextPaths = request.ContextPaths;
            for (var i = 0; i < contextPaths.Count; i++)
            {
                if (!request.AssignedTagsByPath.ContainsKey(contextPaths[i]))
                {
                    return null;
                }
            }

            var availableTags = request.AvailableTags;
            var actionPaths = contextPaths;
            // Ask the shortcut controller's own platform gate rather than
            // re-deriving which platforms have the Finder-tag chords.
            var tagHotkeysAvailable = ZepShortcutController.IsFinderTagShortcutPlatform(platform);

            var commonTagIds = canTag
                ? GetCommonAssignedTagIds(request)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTagIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tagItems = new List<ZepContextMenuItem>();

            if (contextPaths.Count > 0)
            {
                AppendTagChildren(
                    tagItems,
                    availableTags.Where(static option => option.Tag.IsFavorite).Take(FavouriteTagDisplayLimit),
                    seenTagIds,
                    commonTagIds,
                    canTag,
                    request,
                    actions,
                    assignFavouriteHotkeys: tagHotkeysAvailable);

                var overflow = availableTags.Where(option => seenTagIds.Add(option.TagId)).ToArray();
                if (overflow.Length > 0)
                {
                    if (tagItems.Count > 0)
                    {
                        tagItems.Add(ZepContextMenuItem.CreateSeparator());
                    }

                    AppendTagChildren(
                        tagItems,
                        overflow,
                        null,
                        commonTagIds,
                        canTag,
                        request,
                        actions);
                }
            }

            if (tagItems.Count > 0)
            {
                tagItems.Add(ZepContextMenuItem.CreateSeparator());
            }

            tagItems.Add(new ZepContextMenuItem(
                "Zep/Tags/New",
                "Zep/Tags/New",
                "New Tag…",
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                "+",
                onClick: () => actions.PromptCreateTag(request.Pane)));
            tagItems.Add(new ZepContextMenuItem(
                "Zep/Tags/Clear",
                "Zep/Tags/Clear",
                "Remove All Tags",
                tagHotkeysAvailable ? "^0" : string.Empty,
                canTag,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                "⌫",
                onClick: () =>
                {
                    if (request.AreContextTargetsCurrent())
                    {
                        actions.ClearTags(request.Pane, actionPaths);
                    }
                }));
            tagItems.Add(new ZepContextMenuItem(
                "Zep/Tags/Manage",
                "Zep/Tags/Manage",
                "Manage Tags…",
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                "⚙",
                onClick: actions.OpenTagSettings));

            return new ZepContextMenuItem(
                "Zep/Tags",
                "Zep/Tags",
                "Tags",
                string.Empty,
                true,
                false,
                false,
                tagItems,
                iconTagColor: null);
        }

        private static void AppendTagChildren(
            List<ZepContextMenuItem> items,
            IEnumerable<ZepTagFilterOption> options,
            ISet<string> seenTagIds,
            ISet<string> commonTagIds,
            bool canTag,
            ZepContextMenuRequest request,
            ContextMenuActions actions,
            bool assignFavouriteHotkeys = false)
        {
            if (items == null || options == null)
            {
                return;
            }

            // Position in this exact sequence (favourite tags, in the same order
            // as they were filtered/capped by the caller), matching how
            // ZepShortcutController.TryHandleFinderTagShortcut resolves
            // shortcutIndex - 1 against its own favourites-Take(7) array — so
            // shortcut N always lands on the row that reads ⌃N.
            var position = 0;
            foreach (var option in options)
            {
                position++;
                if (string.IsNullOrWhiteSpace(option.TagId))
                {
                    continue;
                }

                if (seenTagIds != null && !seenTagIds.Add(option.TagId))
                {
                    continue;
                }

                var tagId = option.TagId;
                var isAssigned = commonTagIds != null && commonTagIds.Contains(tagId);
                var hotkey = assignFavouriteHotkeys && position <= FavouriteTagDisplayLimit
                    ? "^" + position
                    : string.Empty;
                items.Add(new ZepContextMenuItem(
                    "Zep/Tags/Toggle/" + tagId,
                    "Zep/Tags/Toggle/" + tagId,
                    option.Tag.Name,
                    hotkey,
                    canTag,
                    isAssigned,
                    false,
                    Array.Empty<ZepContextMenuItem>(),
                    iconTagColor: option.Tag.Color,
                    onClick: isAssigned
                        ? (Action)(() =>
                        {
                            if (request.AreContextTargetsCurrent())
                            {
                                actions.UnassignTag(request.Pane, request.ContextPaths, tagId);
                            }
                        })
                        : () =>
                        {
                            if (request.AreContextTargetsCurrent())
                            {
                                actions.AssignTag(request.Pane, request.ContextPaths, tagId);
                            }
                        }));
            }
        }

        private static HashSet<string> GetCommonAssignedTagIds(ZepContextMenuRequest request)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assignedTagsByPath = request.AssignedTagsByPath;
            var contextPaths = request.ContextPaths;
            if (assignedTagsByPath == null || contextPaths.Count == 0)
            {
                return result;
            }

            var isFirst = true;
            for (var i = 0; i < contextPaths.Count; i++)
            {
                if (!assignedTagsByPath.TryGetValue(contextPaths[i], out var tags) || tags == null)
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (isFirst)
                {
                    foreach (var id in tags)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            result.Add(id);
                        }
                    }

                    isFirst = false;
                    continue;
                }

                result.IntersectWith(tags);
            }

            return result;
        }
    }
}
