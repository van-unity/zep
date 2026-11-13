using System;
using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.ContextMenu
{
    internal enum ZepContextMenuTargetKind
    {
        Selection,
        Background,
        SidebarFolder
    }

    // Bundles the per-call data needed to build a right-click menu. Replaces a
    // row of widget setters that callers had to invoke before each Show — the
    // type makes it impossible to forget a piece of context.
    internal readonly struct ZepContextMenuRequest
    {
        internal ZepContextMenuRequest(
            ZepPaneState pane,
            ZepController commandHandler,
            IReadOnlyList<string> contextPaths,
            string createTargetFolderPath,
            IReadOnlyList<ZepTagFilterOption> availableTags,
            IReadOnlyDictionary<string, ICollection<string>> assignedTagsByPath,
            IReadOnlyList<ZepAssetEntry> selectedAssets,
            ZepFolderEntry selectedFolder,
            bool isOpenFolderEnabled,
            bool isDeleteEnabled,
            bool isCreationAllowed,
            bool canModifyAllContextPaths,
            ZepContextMenuTargetKind targetKind = ZepContextMenuTargetKind.Selection,
            Rect? anchor = null)
        {
            Pane = pane ?? throw new ArgumentNullException(nameof(pane));
            CommandHandler = commandHandler;
            ContextPaths = Copy(contextPaths);
            ContextAssetGuids = CaptureAssetGuids(ContextPaths);
            CreateTargetFolderPath = string.IsNullOrWhiteSpace(createTargetFolderPath)
                ? "Assets"
                : createTargetFolderPath;
            AvailableTags = Copy(availableTags);
            AssignedTagsByPath = Copy(assignedTagsByPath);
            SelectedAssets = Copy(selectedAssets);
            SelectedFolder = selectedFolder;
            IsOpenFolderEnabled = isOpenFolderEnabled;
            IsDeleteEnabled = isDeleteEnabled;
            IsCreationAllowed = isCreationAllowed;
            CanModifyAllContextPaths = canModifyAllContextPaths;
            TargetKind = targetKind;
            Anchor = anchor;
        }

        internal ZepPaneState Pane { get; }

        internal ZepController CommandHandler { get; }

        internal IReadOnlyList<string> ContextPaths { get; }

        private IReadOnlyList<string> ContextAssetGuids { get; }

        // The browse-context create target: state.ResolveCreateTargetFolderPath,
        // the single authority all three create surfaces resolve through — the
        // toolbar + button (ShowCreateMenu), Cmd+N, and this right-click menu — so
        // they target the SAME folder for identical state. The menu falls back to
        // it when the clicked context doesn't pin a more specific target
        // (multi-selection, empty context). Never null or empty.
        internal string CreateTargetFolderPath { get; }

        internal IReadOnlyList<ZepTagFilterOption> AvailableTags { get; }

        internal IReadOnlyDictionary<string, ICollection<string>> AssignedTagsByPath { get; }

        internal IReadOnlyList<ZepAssetEntry> SelectedAssets { get; }

        internal ZepFolderEntry SelectedFolder { get; }

        internal bool IsOpenFolderEnabled { get; }

        internal bool IsDeleteEnabled { get; }

        // False when the current view (Tags) doesn't have a
        // real creation target — Create / New X menu items render disabled in
        // those modes. Only Browser mode treats the current folder as a
        // writable destination.
        internal bool IsCreationAllowed { get; }

        // ZepSelectionPredicates.CanModifyAllSelectedPaths over
        // ContextPaths, resolved once by the caller that opens the menu: it
        // costs a gateway CanWrite (a PackageInfo lookup for package paths) per
        // path, and the shell already needs the answer for IsDeleteEnabled.
        // Every consumer of the modify gate for THIS menu reads it from here.
        internal bool CanModifyAllContextPaths { get; }

        internal ZepContextMenuTargetKind TargetKind { get; }

        internal Rect? Anchor { get; }

        internal bool HasTargets => ContextPaths.Count > 0;

        internal bool IsBackground => TargetKind == ZepContextMenuTargetKind.Background;

        internal bool IsSidebarFolder => TargetKind == ZepContextMenuTargetKind.SidebarFolder;

        internal bool IsFolderTarget(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (SelectedFolder != null
                && string.Equals(SelectedFolder.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (var i = 0; i < SelectedAssets.Count; i++)
            {
                var asset = SelectedAssets[i];
                if (asset != null
                    && asset.IsFolder
                    && string.Equals(asset.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Native menus and themed confirmation prompts can stay open while the
        // project changes underneath them. Paths alone are not identities: an
        // asset can disappear and a different asset can be imported at the same
        // path before the callback runs. Validate the whole captured set before
        // any action so multi-target operations fail atomically.
        internal bool AreContextTargetsCurrent()
        {
            if (ContextPaths.Count != ContextAssetGuids.Count)
            {
                return false;
            }

            for (var i = 0; i < ContextPaths.Count; i++)
            {
                var path = ContextPaths[i];

                var capturedGuid = ContextAssetGuids[i];
                if (string.IsNullOrWhiteSpace(path)
                    || string.IsNullOrWhiteSpace(capturedGuid)
                    || !string.Equals(
                        CountAndGetGuid(path),
                        capturedGuid,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        CountAndGetPath(capturedGuid),
                        path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<T>();
            }

            var copy = new T[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                copy[i] = source[i];
            }

            return copy;
        }

        // Captured eagerly, at menu-open time, and deliberately so. Deferring
        // the capture to the first AreContextTargetsCurrent() call would make
        // the check compare a GUID read microseconds earlier against itself:
        // every reader of these values is inside a menu callback that runs
        // AFTER the user picked an item, which is exactly the window the
        // capture exists to span. The cost is one AssetPathToGUID per context
        // path; the alternative is a staleness check that can never fail.
        // Deliberately Unity, not the index: the staleness check in the menu
        // callbacks compares this capture against AssetDatabase's live answer,
        // so both sides must come from the same oracle. An index-sourced capture
        // would make a merely stale index look like a moved asset and refuse
        // the action (the rig tests with fake guids proved exactly that).
        private static IReadOnlyList<string> CaptureAssetGuids(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var guids = new string[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                guids[i] = string.IsNullOrWhiteSpace(path)
                        ? string.Empty
                        : CountAndGetGuid(path);
            }

            return guids;
        }

        private static IReadOnlyDictionary<string, ICollection<string>> Copy(
            IReadOnlyDictionary<string, ICollection<string>> source)
        {
            if (source == null)
            {
                return null;
            }

            var copy = new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in source)
            {
                var values = entry.Value;
                if (values == null || values.Count == 0)
                {
                    copy[entry.Key] = Array.Empty<string>();
                    continue;
                }

                var valueCopy = new string[values.Count];
                values.CopyTo(valueCopy, 0);
                copy[entry.Key] = Array.AsReadOnly(valueCopy);
            }

            return copy;
        }

        private static string CountAndGetGuid(string assetPath)
        {
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            return AssetDatabase.AssetPathToGUID(assetPath);
        }

        private static string CountAndGetPath(string guid)
        {
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
            return AssetDatabase.GUIDToAssetPath(guid);
        }
    }

    internal static class ZepContextMenuInput
    {
        internal static bool IsMouseTrigger(MouseDownEvent evt)
        {
            return evt != null && IsMouseTrigger(evt.button, evt.modifiers, Application.platform);
        }

        internal static bool IsPointerTrigger(PointerDownEvent evt)
        {
            return evt != null && IsMouseTrigger(evt.button, evt.modifiers, Application.platform);
        }

        internal static bool IsMouseTrigger(
            int button,
            EventModifiers modifiers,
            RuntimePlatform platform)
        {
            if (button == 1)
            {
                return true;
            }

            return button == 0
                && platform == RuntimePlatform.OSXEditor
                && (modifiers & EventModifiers.Control) != 0;
        }

        internal static bool IsKeyboardTrigger(KeyDownEvent evt)
        {
            return evt != null && IsKeyboardTrigger(evt.keyCode, evt.modifiers);
        }

        internal static bool IsKeyboardTrigger(KeyCode keyCode, EventModifiers modifiers)
        {
            return keyCode == KeyCode.Menu
                || (keyCode == KeyCode.F10 && (modifiers & EventModifiers.Shift) != 0);
        }

        internal static Rect PointerAnchor(PointerDownEvent evt)
        {
            return evt == null
                ? default
                : new Rect(evt.position.x, evt.position.y, 0f, 0f);
        }

        internal static void Consume(EventBase evt)
        {
            if (evt == null)
            {
                return;
            }

            evt.StopImmediatePropagation();
        }

        internal static Rect KeyboardAnchor(VisualElement target)
        {
            if (target == null)
            {
                return default;
            }

            var bounds = target.worldBound;
            return new Rect(bounds.center, Vector2.zero);
        }
    }
}
