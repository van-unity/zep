using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.UI.ContextMenu
{
    // Translates a ZepContextMenuItem tree into Unity's GenericMenu and
    // shows it as a context menu. Knows nothing about asset loading, composition,
    // or which items live in the tree — that's the widget's job.
    internal static class ContextMenuGenericMenuRenderer
    {
        internal static void Show(
            IReadOnlyList<ZepContextMenuItem> items,
            Action<ZepContextMenuItem> dispatchUnbound,
            Rect? anchor)
        {
            var menu = new GenericMenu
            {
                allowDuplicateNames = true
            };
            Populate(menu, items, string.Empty, dispatchUnbound);
            if (anchor.HasValue)
            {
                menu.DropDown(anchor.Value);
            }
            else
            {
                menu.ShowAsContext();
            }
        }

        internal static string BuildDisplayLabel(ZepContextMenuItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            var label = (item.Label ?? string.Empty).Replace('/', '∕');
            if (item.IconTagColor.HasValue)
            {
                label = GetTagColorMarker(item.IconTagColor.Value) + " " + label;
            }
            else if (!string.IsNullOrEmpty(item.LeadingIcon))
            {
                label = item.LeadingIcon + " " + label;
            }

            if (!item.HasChildren && !string.IsNullOrWhiteSpace(item.Hotkey))
            {
                label += " " + item.Hotkey.Trim();
            }

            return label;
        }

        private static void Populate(
            GenericMenu menu,
            IReadOnlyList<ZepContextMenuItem> items,
            string pathPrefix,
            Action<ZepContextMenuItem> dispatchUnbound)
        {
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    menu.AddSeparator(pathPrefix);
                    continue;
                }

                // GenericMenu reads '/' in an item path as a submenu separator, so
                // BuildDisplayLabel neutralizes user-provided slashes. It also uses
                // Unicode color circles survive the native popup conversion where
                // GenericMenu drops GUIContent.image tag swatches.
                var label = BuildDisplayLabel(item);
                var content = new GUIContent(pathPrefix + label);

                if (item.HasChildren)
                {
                    // GenericMenu derives submenu headers automatically from item
                    // paths and offers no API for greying out a header. When the
                    // submenu itself is marked disabled (every descendant unusable),
                    // collapse it to a single disabled entry instead of recursing —
                    // otherwise Unity renders the header as enabled and the user
                    // hovers into a submenu of greyed-out leaves.
                    if (!item.IsEnabled)
                    {
                        menu.AddDisabledItem(content, item.IsChecked);
                        continue;
                    }

                    Populate(menu, item.Children, pathPrefix + label + "/", dispatchUnbound);
                    continue;
                }

                if (!item.IsEnabled)
                {
                    menu.AddDisabledItem(content, item.IsChecked);
                    continue;
                }

                var capturedItem = item;
                menu.AddItem(content, item.IsChecked, () =>
                {
                    if (capturedItem.OnClick != null)
                    {
                        capturedItem.OnClick.Invoke();
                    }
                    else
                    {
                        dispatchUnbound?.Invoke(capturedItem);
                    }
                });
            }
        }

        private static string GetTagColorMarker(ZepTagColor color)
        {
            switch (color)
            {
                case ZepTagColor.Gray:
                    return "⚪";
                case ZepTagColor.Green:
                    return "🟢";
                case ZepTagColor.Purple:
                    return "🟣";
                case ZepTagColor.Blue:
                    return "🔵";
                case ZepTagColor.Yellow:
                    return "🟡";
                case ZepTagColor.Red:
                    return "🔴";
                case ZepTagColor.Orange:
                    return "🟠";
                default:
                    return "○";
            }
        }
    }
}
