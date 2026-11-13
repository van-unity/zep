using System;
using System.Collections.Generic;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Editor.UI;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI.ContextMenu
{
    internal sealed class ZepContextMenuItem
    {
        public ZepContextMenuItem(
            string commandPath,
            string normalizedCommandPath,
            string label,
            string hotkey,
            bool isEnabled,
            bool isChecked,
            bool isSeparator,
            IReadOnlyList<ZepContextMenuItem> children,
            string leadingIcon = "",
            ZepTagColor? iconTagColor = null,
            Action onClick = null,
            bool isDestructive = false)
        {
            CommandPath = commandPath ?? string.Empty;
            NormalizedCommandPath = normalizedCommandPath ?? string.Empty;
            Label = label ?? string.Empty;
            Hotkey = hotkey ?? string.Empty;
            IsEnabled = isEnabled;
            IsChecked = isChecked;
            IsSeparator = isSeparator;
            Children = children ?? Array.Empty<ZepContextMenuItem>();
            LeadingIcon = leadingIcon ?? string.Empty;
            IconTagColor = iconTagColor;
            OnClick = onClick;
            IsDestructive = isDestructive;
        }

        public string CommandPath { get; }

        public string NormalizedCommandPath { get; }

        public string Label { get; }

        public string Hotkey { get; }

        public bool IsEnabled { get; }

        public bool IsChecked { get; }

        public bool IsSeparator { get; }

        public IReadOnlyList<ZepContextMenuItem> Children { get; }

        public string LeadingIcon { get; }

        // Typed color cue for tag items. GenericMenu does not forward
        // GUIContent.image to its native popup, so the renderer exposes this as
        // accessible text instead of allocating a texture the user cannot see.
        public ZepTagColor? IconTagColor { get; }

        public bool HasChildren => Children.Count > 0;

        // Delete-class actions; the popup renderer paints these in the danger
        // colours on hover so the eye catches them before the click lands.
        public bool IsDestructive { get; }

        // Null for long-tail Unity menu items; those fall through to
        // UnityAssetsContextMenuProvider.Execute in InvokeItem.
        internal Action OnClick { get; }

        public static ZepContextMenuItem CreateSeparator()
        {
            return new ZepContextMenuItem(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                false,
                true,
                Array.Empty<ZepContextMenuItem>());
        }
    }

    internal sealed class UnityAssetsContextMenuProvider
    {
        private const string AssetsMenuPath = "Assets";
        private static bool _missingApiLogged;
        private static bool _reflectionFailureLogged;
        private bool _disabled;
        private static readonly Type MenuType = Type.GetType("UnityEditor.Menu, UnityEditor");
        private static readonly Type ScriptingMenuItemType = Type.GetType("UnityEditor.ScriptingMenuItem, UnityEditor");
        private static readonly MethodInfo GetMenuItemsMethod = MenuType?.GetMethod(
            "GetMenuItems",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(bool), typeof(bool) },
            null);
        private static readonly MethodInfo GetEnabledMethod = MenuType?.GetMethod(
            "GetEnabled",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        private static readonly MethodInfo GetEnabledWithContextMethod = MenuType?.GetMethod(
            "GetEnabledWithContext",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(Object[]) },
            null);
        private static readonly MethodInfo GetCheckedMethod = MenuType?.GetMethod(
            "GetChecked",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        private static readonly MethodInfo GetHotkeyMethod = MenuType?.GetMethod(
            "GetHotkey",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        private static readonly MethodInfo UpdateContextMenuMethod = MenuType?.GetMethod(
            "UpdateContextMenu",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(Object[]), typeof(int) },
            null);
        private static readonly MethodInfo FindHotkeyStartIndexMethod = MenuType?.GetMethod(
            "FindHotkeyStartIndex",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        private static readonly PropertyInfo MenuItemPathProperty = ScriptingMenuItemType?.GetProperty("path", BindingFlags.Instance | BindingFlags.Public);
        private static readonly PropertyInfo MenuItemSeparatorProperty = ScriptingMenuItemType?.GetProperty("isSeparator", BindingFlags.Instance | BindingFlags.Public);

        public bool IsAvailable => !_disabled
            && GetMenuItemsMethod != null
            && MenuItemPathProperty != null
            && MenuItemSeparatorProperty != null;

        public IReadOnlyList<ZepContextMenuItem> BuildAssetsMenu(IReadOnlyList<Object> contextObjects)
        {
            var contextArray = GetCleanContextObjects(contextObjects);
            PrepareContext(contextArray);
            if (!IsAvailable)
            {
                LogUnavailableOnce();
                return Array.Empty<ZepContextMenuItem>();
            }

            try
            {
                if (_disabled || !TryGetRawMenuItems(AssetsMenuPath, out var rawItems))
                {
                    return Array.Empty<ZepContextMenuItem>();
                }

                return BuildMenuLevel(
                    AssetsMenuPath,
                    rawItems,
                    contextArray,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                DisableAfterReflectionFailure("Assets menu construction", ex);
                return Array.Empty<ZepContextMenuItem>();
            }
        }

        public bool Execute(ZepContextMenuItem item, IReadOnlyList<Object> contextObjects)
        {
            if (item == null || item.IsSeparator || item.HasChildren)
            {
                return false;
            }

            var contextArray = GetCleanContextObjects(contextObjects);
            PrepareContext(contextArray);
            if (!IsAvailable)
            {
                LogUnavailableOnce();
                return false;
            }

            if (!string.IsNullOrEmpty(item.CommandPath) && EditorApplication.ExecuteMenuItem(item.CommandPath))
            {
                return true;
            }

            return !string.IsNullOrEmpty(item.NormalizedCommandPath)
                && EditorApplication.ExecuteMenuItem(item.NormalizedCommandPath);
        }

        internal void ClearContext()
        {
            PrepareContext(Array.Empty<Object>());
        }

        private void LogUnavailableOnce()
        {
            if (_disabled || _missingApiLogged)
            {
                return;
            }

            _missingApiLogged = true;
            UnityEngine.Debug.LogWarning(
                "ContextMenu: UnityEditor.Menu reflection API is unavailable in Unity "
                + UnityEngine.Application.unityVersion
                + "; Unity-owned Assets commands are omitted, while Zep commands remain available.");
        }

        private void DisableAfterReflectionFailure(string operation, Exception exception)
        {
            _disabled = true;
            if (_reflectionFailureLogged)
            {
                return;
            }

            _reflectionFailureLogged = true;
            var cause = exception is TargetInvocationException invocation
                && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;
            UnityEngine.Debug.LogWarning(
                "ContextMenu: UnityEditor.Menu reflection failed during "
                + operation
                + " in Unity "
                + UnityEngine.Application.unityVersion
                + " ("
                + cause.GetType().Name
                + ": "
                + cause.Message
                + "). Unity-owned Assets commands are disabled for this provider; Zep commands remain available.");
        }

        internal static string NormalizeMenuCommandPath(string commandPath)
        {
            if (string.IsNullOrWhiteSpace(commandPath))
            {
                return string.Empty;
            }

            var segments = commandPath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = StripHotkeyMarkup(segments[i]);
            }

            return string.Join("/", segments);
        }

        private static Object[] GetCleanContextObjects(IReadOnlyList<Object> contextObjects)
        {
            if (contextObjects == null)
            {
                return Array.Empty<Object>();
            }
            var list = new List<Object>(contextObjects.Count);
            var seen = new HashSet<Object>();
            for (var i = 0; i < contextObjects.Count; i++)
            {
                var obj = contextObjects[i];
                if (obj != null && seen.Add(obj))
                {
                    list.Add(obj);
                }
            }
            return list.ToArray();
        }

        private IReadOnlyList<ZepContextMenuItem> BuildMenuLevel(
            string menuPath,
            IReadOnlyList<RawMenuItem> allRawItems,
            Object[] contextObjects,
            ISet<string> visitedMenuPaths)
        {
            if (string.IsNullOrWhiteSpace(menuPath) || visitedMenuPaths.Contains(menuPath))
            {
                return Array.Empty<ZepContextMenuItem>();
            }

            visitedMenuPaths.Add(menuPath);
            try
            {
                var builtItems = new List<ZepContextMenuItem>();
                var emittedChildPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < allRawItems.Count; i++)
                {
                    if (_disabled)
                    {
                        return Array.Empty<ZepContextMenuItem>();
                    }

                    var rawItem = allRawItems[i];
                    var rawPath = rawItem.Path;
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    if (!rawPath.StartsWith(menuPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (rawPath.Length == menuPath.Length)
                    {
                        continue;
                    }

                    if (rawPath[menuPath.Length] != '/')
                    {
                        continue;
                    }

                    if (rawItem.IsSeparator)
                    {
                        AppendSeparatorIfNeeded(builtItems);
                        continue;
                    }

                    var childPath = GetImmediateChildMenuPath(menuPath, rawPath);
                    if (string.IsNullOrWhiteSpace(childPath) || !emittedChildPaths.Add(childPath))
                    {
                        continue;
                    }

                    var childItems = BuildMenuLevel(childPath, allRawItems, contextObjects, visitedMenuPaths);
                    var isSubmenu = childItems.Count > 0;
                    
                    var enabled = false;
                    if (isSubmenu)
                    {
                        for (var j = 0; j < childItems.Count; j++)
                        {
                            var child = childItems[j];
                            if (child.IsEnabled || child.HasChildren)
                            {
                                enabled = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        enabled = GetEnabled(childPath, contextObjects);
                    }

                    var checkedState = !isSubmenu && GetChecked(childPath);
                    var hotkey = isSubmenu ? string.Empty : GetHotkey(childPath);
                    var icon = isSubmenu ? string.Empty : GetMenuIcon(childPath);

                    builtItems.Add(new ZepContextMenuItem(
                        childPath,
                        NormalizeMenuCommandPath(childPath),
                        ExtractDisplayName(childPath),
                        hotkey,
                        enabled,
                        checkedState,
                        false,
                        childItems,
                        icon));
                }

                TrimSeparators(builtItems);
                return builtItems.Count == 0 ? Array.Empty<ZepContextMenuItem>() : (IReadOnlyList<ZepContextMenuItem>)builtItems;
            }
            finally
            {
                visitedMenuPaths.Remove(menuPath);
            }
        }

        private static string GetMenuIcon(string commandPath)
        {
            if (string.IsNullOrWhiteSpace(commandPath)) return string.Empty;
            if (commandPath.EndsWith("/Open")) return "↗";
            if (commandPath.EndsWith("/Delete")) return "✖";
            if (commandPath.EndsWith("/Rename")) return "✎";
            if (commandPath.EndsWith("/Duplicate")) return "⎘";
            if (commandPath.EndsWith("/Show in Explorer") || commandPath.EndsWith("/Reveal in Finder")) return "👁";
            if (commandPath.Contains("/Create/")) return "＋";
            return string.Empty;
        }

        private bool TryGetRawMenuItems(
            string menuPath,
            out List<RawMenuItem> items)
        {
            try
            {
                var raw = GetMenuItemsMethod?.Invoke(null, new object[] { menuPath, true, false }) as Array;
                if (raw == null || raw.Length == 0)
                {
                    items = new List<RawMenuItem>();
                    return true;
                }

                items = new List<RawMenuItem>(raw.Length);
                for (var i = 0; i < raw.Length; i++)
                {
                    var rawItem = raw.GetValue(i);
                    if (rawItem == null)
                    {
                        continue;
                    }

                    var path = MenuItemPathProperty?.GetValue(rawItem) as string;
                    var isSeparator = (bool?)MenuItemSeparatorProperty?.GetValue(rawItem) ?? false;
                    items.Add(new RawMenuItem(path, isSeparator));
                }

                return true;
            }
            catch (Exception ex)
            {
                DisableAfterReflectionFailure("Assets menu extraction", ex);
                items = new List<RawMenuItem>();
                return false;
            }
        }

        private void PrepareContext(Object[] contextArray)
        {
            // Validators and unbound menu commands read Unity's global selection.
            // Replacement is atomic even for an empty context so an unresolved or
            // background request cannot inherit a previous editor selection.
            Selection.objects = contextArray;
            try
            {
                if (contextArray.Length > 0)
                {
                    Unsupported.PrepareObjectContextMenu(contextArray[0], 0);
                }

                UpdateContextMenuMethod?.Invoke(null, new object[] { contextArray, 0 });
            }
            catch (Exception ex)
            {
                DisableAfterReflectionFailure("context preparation", ex);
            }
        }

        private bool GetEnabled(string menuPath, Object[] contextObjects)
        {
            try
            {
                // Already non-null and de-duplicated: BuildAssetsMenu sanitizes
                // once per open through GetCleanContextObjects and hands the
                // same array down every level. Re-filtering here allocated a
                // fresh array per leaf item for an identical result.
                var contextArray = contextObjects ?? Array.Empty<Object>();

                if (GetEnabledWithContextMethod != null)
                {
                    return (bool?)GetEnabledWithContextMethod.Invoke(null, new object[] { menuPath, contextArray }) ?? false;
                }

                return (bool?)GetEnabledMethod?.Invoke(null, new object[] { menuPath }) ?? false;
            }
            catch (Exception ex)
            {
                DisableAfterReflectionFailure("command validation", ex);
                return false;
            }
        }

        private bool GetChecked(string menuPath)
        {
            try
            {
                return (bool?)GetCheckedMethod?.Invoke(null, new object[] { menuPath }) ?? false;
            }
            catch (Exception ex)
            {
                DisableAfterReflectionFailure("checked-state lookup", ex);
                return false;
            }
        }

        private string GetHotkey(string menuPath)
        {
            try
            {
                return GetHotkeyMethod?.Invoke(null, new object[] { menuPath }) as string ?? string.Empty;
            }
            catch (Exception ex)
            {
                DisableAfterReflectionFailure("hotkey lookup", ex);
                return string.Empty;
            }
        }

        private static string ExtractDisplayName(string commandPath)
        {
            if (string.IsNullOrWhiteSpace(commandPath))
            {
                return string.Empty;
            }

            var lastSlashIndex = commandPath.LastIndexOf('/');
            var segment = lastSlashIndex >= 0
                ? commandPath.Substring(lastSlashIndex + 1)
                : commandPath;
            return StripHotkeyMarkup(segment);
        }

        private static string StripHotkeyMarkup(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            try
            {
                if (FindHotkeyStartIndexMethod != null)
                {
                    var index = (int?)FindHotkeyStartIndexMethod.Invoke(null, new object[] { text }) ?? -1;
                    if (index >= 0 && index < text.Length)
                    {
                        return text.Substring(0, index).TrimEnd();
                    }
                }
            }
            catch (TargetInvocationException)
            {
                // Fall back to the heuristic below if reflection fails.
            }

            var heuristicIndex = text.IndexOf(" _", StringComparison.Ordinal);
            if (heuristicIndex >= 0)
            {
                return text.Substring(0, heuristicIndex).TrimEnd();
            }

            return text.Trim();
        }

        private static string GetImmediateChildMenuPath(string parentPath, string rawPath)
        {
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(rawPath))
            {
                return string.Empty;
            }

            if (!rawPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase))
            {
                return rawPath;
            }

            var relativePath = rawPath.Length == parentPath.Length
                ? string.Empty
                : rawPath.Substring(parentPath.Length).TrimStart('/');
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return rawPath;
            }

            var separatorIndex = relativePath.IndexOf('/');
            if (separatorIndex < 0)
            {
                return parentPath + "/" + relativePath;
            }

            return parentPath + "/" + relativePath.Substring(0, separatorIndex);
        }

        private static void AppendSeparatorIfNeeded(List<ZepContextMenuItem> items)
        {
            if (items.Count == 0 || items[items.Count - 1].IsSeparator)
            {
                return;
            }

            items.Add(ZepContextMenuItem.CreateSeparator());
        }

        private static void TrimSeparators(List<ZepContextMenuItem> items)
        {
            while (items.Count > 0 && items[0].IsSeparator)
            {
                items.RemoveAt(0);
            }

            while (items.Count > 0 && items[items.Count - 1].IsSeparator)
            {
                items.RemoveAt(items.Count - 1);
            }
        }

        private readonly struct RawMenuItem
        {
            internal RawMenuItem(string path, bool isSeparator)
            {
                Path = path ?? string.Empty;
                IsSeparator = isSeparator;
            }

            internal string Path { get; }

            internal bool IsSeparator { get; }
        }
    }
}
