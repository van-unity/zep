using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.ContextMenu;

namespace Zep.Editor.UI.Sidebar
{
    // Finder-style Favorites section: a "Favorites" header plus one row per
    // bookmarked folder, the first section in the sidebar.
    // The widget renders paths it is handed and raises intents; the shell
    // owns the store. A drop adds folders from either drag vocabulary the
    // panel produces: a sidebar folder row publishes a split-drag payload
    // (no paths), the assets list/grid publish DragAndDrop project paths.
    // Dragging a favorite row publishes that same split payload — the
    // workspace's split zones accept it like any sidebar folder — and this
    // list reads the drag back as a reorder because it started here.
    internal sealed class FavoritesWidget : VisualElement
    {
        internal const string ExpandedSessionKey =
            "Zep.Sidebar.FavoritesExpanded";
        internal const string EmptyHintText = "Drag folders here";

        private readonly UnityZepIconRepository _iconRepository;
        private readonly Func<string, ZepFolderEntry> _folderEntryQuery;
        private readonly Func<string, ZepTagColor> _folderTintQuery;
        private readonly SidebarSectionHeader _header;
        private readonly VisualElement _favoriteList;
        private readonly Label _emptyHint;
        private readonly VisualElement _insertLine;
        private readonly SidebarSplitDragPrepareInteraction _reorderDrag;

        // Recorded favorite paths (store order) and the subset that currently
        // resolves to a visible folder — a favorite whose folder is gone or
        // hidden renders no row but stays recorded, so it comes back with the
        // folder. _resolved is a reused scratch: Flush runs per refresh tick.
        private readonly List<string> _paths = new List<string>();
        private readonly List<ZepFolderEntry> _resolved = new List<ZepFolderEntry>();
        private readonly List<string> _dropScratch = new List<string>();
        private string _selectedPath = string.Empty;

        // True from a favorite row's drag start until the sidebar's next fresh
        // press, a drop back into this list, or detach. The split payload on
        // the drag bus is the same one a tree row publishes; this flag is
        // what tells this list the payload is its own row being moved.
        private bool _reorderDragActive;

        internal event Action<string> FavoriteClicked;
        internal event Action<string> FavoriteRemoveRequested;
        // Hand the folder back to the folder trees: expand to it and frame it.
        internal event Action<string> FavoriteRevealRequested;
        internal event Action<IReadOnlyList<string>> FavoriteDropReceived;
        // (dragged path, path of the favorite it lands before; empty = end).
        internal event Action<string, string> FavoriteMoveRequested;

        internal FavoritesWidget(
            UnityZepIconRepository iconRepository,
            Func<string, ZepFolderEntry> folderEntryQuery,
            Func<string, ZepTagColor> folderTintQuery = null)
        {
            _iconRepository = iconRepository ?? throw new ArgumentNullException(nameof(iconRepository));
            _folderEntryQuery = folderEntryQuery;
            _folderTintQuery = folderTintQuery;

            name = "zep-sidebar-favorites";
            AddToClassList(ZepUiClasses.SidebarSection);
            AddToClassList(ZepUiClasses.SidebarFavorites);

            _header = new SidebarSectionHeader("Favorites", ExpandedSessionKey)
            {
                name = "zep-favorites-header-row"
            };
            _header.ExpandedChanged += _ => ApplyExpandedState();

            _favoriteList = new VisualElement { name = "zep-sidebar-favorites-list" };

            // Row press/click/drag threshold, same interaction as the trees and
            // tags: pointer-down arms, pointer-up commits the click, crossing
            // the threshold starts the drag. Its surface is the list; the
            // context-menu handlers sit ABOVE it on the widget root with
            // TrickleDown, so a right/ctrl-click is consumed before the
            // interaction can arm — the FolderTreeWidget layering.
            _reorderDrag = new SidebarSplitDragPrepareInteraction(
                _favoriteList,
                SnapshotReorderDragPayload,
                CommitReorderSourceClick,
                StartFavoriteDrag);

            // Keyboard on the rows, the folder tree's contract: arrows walk
            // the favorites (each step navigates, as a click does); Enter
            // arrives through the shell's shortcut router, which asks this
            // widget by focused element.
            _favoriteList.RegisterCallback<KeyDownEvent>(OnListKeyDown, TrickleDown.TrickleDown);

            _emptyHint = new Label(EmptyHintText)
            {
                name = "zep-sidebar-favorites-empty",
                pickingMode = PickingMode.Ignore
            };
            _emptyHint.AddToClassList(ZepUiClasses.SidebarFavoritesEmpty);

            // Reorder insertion caret: static box in USS, `top`/display are
            // runtime drag geometry written inline. A sibling of the list (not
            // a child) so RebuildFavoriteRows can keep indexing list children
            // as rows.
            _insertLine = new VisualElement
            {
                name = "zep-sidebar-favorites-insert-line",
                pickingMode = PickingMode.Ignore
            };
            _insertLine.AddToClassList(ZepUiClasses.SidebarFavoritesInsertLine);
            _insertLine.style.display = DisplayStyle.None;

            Add(_header);
            Add(_favoriteList);
            Add(_emptyHint);
            Add(_insertLine);

            RegisterCallback<PointerDownEvent>(OnRowContextMenuPointerDown, TrickleDown.TrickleDown);
            RegisterCallback<MouseDownEvent>(OnRowContextMenuMouseDown, TrickleDown.TrickleDown);

            // Drop target is the whole section (header and hint included), so
            // the very first favorite can land on the header even while the
            // list is empty or collapsed.
            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
            RegisterCallback<DragLeaveEvent>(_ => ClearDragFeedback());
            RegisterCallback<DragExitedEvent>(_ => ClearDragFeedback());

            ApplyExpandedState();
        }

        internal bool IsExpanded => _header.IsExpanded;

        // The favorite currently painted as selected, resolved to its folder;
        // null while the sidebar highlight belongs to another section.
        internal ZepFolderEntry SelectedFolderEntry
        {
            get
            {
                for (var i = 0; i < _resolved.Count; i++)
                {
                    if (string.Equals(_resolved[i].Path, _selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return _resolved[i];
                    }
                }

                return null;
            }
        }

        internal void SetFavorites(IReadOnlyList<string> paths)
        {
            _paths.Clear();
            if (paths != null)
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    _paths.Add(paths[i]);
                }
            }

            RebuildFavoriteRows();
        }

        internal void SetSelectedPath(string path)
        {
            var normalized = path ?? string.Empty;
            if (string.Equals(_selectedPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedPath = normalized;
            ApplySelectedClass();
        }

        internal bool IsRowTarget(VisualElement target)
        {
            return TryGetFavoritePathAtTarget(target, out _);
        }

        // The keyboard legs the shell's shortcut router routes here by focused
        // element: Enter opens the focused favorite, the menu key shows its
        // menu. Delete is deliberately not one of them (Finder): a bookmark
        // leaves the list only through its menu.
        internal bool TryActivateRowAt(VisualElement target)
        {
            if (!TryGetFavoritePathAtTarget(target, out var path))
            {
                return false;
            }

            FavoriteClicked?.Invoke(path);
            return true;
        }

        internal bool TryShowRowMenuAt(VisualElement target, Rect anchor)
        {
            if (!TryGetFavoritePathAtTarget(target, out var path))
            {
                return false;
            }

            ShowRowMenu(path, anchor);
            return true;
        }

        private void OnListKeyDown(KeyDownEvent evt)
        {
            if (evt.actionKey || evt.altKey || evt.ctrlKey || evt.shiftKey)
            {
                return;
            }

            var rowCount = _favoriteList.childCount;
            if (rowCount == 0)
            {
                return;
            }

            // The keyboard cursor is the focused row; with focus elsewhere in
            // the list (a rebuilt row), the selected favorite stands in.
            var current = TryGetFavoritePathAtTarget(evt.target as VisualElement, out var focusedPath)
                ? IndexOfRow(focusedPath)
                : IndexOfRow(_selectedPath);
            int target;
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow:
                    target = current < 0 ? rowCount - 1 : current - 1;
                    break;
                case KeyCode.DownArrow:
                    target = current < 0 ? 0 : current + 1;
                    break;
                case KeyCode.Home:
                    target = 0;
                    break;
                case KeyCode.End:
                    target = rowCount - 1;
                    break;
                default:
                    return;
            }

            if (target < 0 || target >= rowCount)
            {
                return;
            }

            var row = _favoriteList[target];
            row.Focus();
            if (row.userData is string path)
            {
                FavoriteClicked?.Invoke(path);
            }

            evt.StopPropagation();
        }

        private int IndexOfRow(string path)
        {
            for (var i = 0; i < _favoriteList.childCount; i++)
            {
                if (_favoriteList[i].userData is string candidate
                    && string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (TryReadReorderPath(out _))
            {
                SetDragOver(false);
                ShowInsertLineAt(ComputeInsertRowIndex(evt.mousePosition));
                DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                evt.StopPropagation();
                return;
            }

            HideInsertLine();
            if (!TryCollectDroppedFolders(_dropScratch))
            {
                SetDragOver(false);
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
            SetDragOver(true);
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            SetDragOver(false);
            if (TryReadReorderPath(out var reorderPath))
            {
                var insertIndex = ComputeInsertRowIndex(evt.mousePosition);
                HideInsertLine();
                DragAndDrop.AcceptDrag();
                // Terminal route for this drag: clear the shared split bus the
                // way the workspace does after a split drop.
                ZepSplitDragAndDrop.Clear();
                _reorderDragActive = false;
                var anchorPath = insertIndex < _favoriteList.childCount
                    ? _favoriteList[insertIndex].userData as string ?? string.Empty
                    : string.Empty;
                FavoriteMoveRequested?.Invoke(reorderPath, anchorPath);
                evt.StopPropagation();
                return;
            }

            if (!TryCollectDroppedFolders(_dropScratch))
            {
                return;
            }

            var wasSplitDrag = ZepSplitDragAndDrop.TryRead(out _);
            DragAndDrop.AcceptDrag();
            if (wasSplitDrag)
            {
                // The split bus is a terminal-route contract: whoever consumes
                // the payload clears it so a later unrelated drag cannot read
                // stale state.
                ZepSplitDragAndDrop.Clear();
            }

            FavoriteDropReceived?.Invoke(new List<string>(_dropScratch));
            evt.StopPropagation();
        }

        private bool TryCollectDroppedFolders(List<string> result)
        {
            result.Clear();
            try
            {
                if (ZepSplitDragAndDrop.TryRead(out var payload))
                {
                    if (payload.Source.Kind == ZepPaneSourceKind.Folder
                        && IsFavoritableFolder(payload.Source.RootFolderPath.Value))
                    {
                        result.Add(payload.Source.RootFolderPath.Value);
                    }

                    return result.Count > 0;
                }

                var paths = EditorDragAndDropPaths.ReadProjectAssetPaths();
                for (var i = 0; i < paths.Count; i++)
                {
                    if (IsFavoritableFolder(paths[i]))
                    {
                        result.Add(paths[i]);
                    }
                }

                return result.Count > 0;
            }
            catch (ProjectTagStateUnavailableException)
            {
                result.Clear();
                return false;
            }
        }

        private bool IsFavoritableFolder(string path)
        {
            if (_folderEntryQuery == null)
            {
                // No index to ask (tests, standalone use): fall back to the
                // path-shape gate a rooted split source already guarantees.
                return AssetPathClassification.IsProjectAssetPath(
                    AssetPathFormat.Normalize(path));
            }

            var entry = _folderEntryQuery(path);
            return entry != null;
        }

        private void SetDragOver(bool dragOver)
        {
            EnableInClassList(ZepUiClasses.DropTarget, dragOver);
        }

        private void ApplyExpandedState()
        {
            var expanded = _header.IsExpanded;
            _favoriteList.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _emptyHint.style.display = expanded && _resolved.Count == 0
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        // Runs every sidebar Flush tick; per-property diffs keep it cheap for
        // the typical handful of favorites (each resolve is a normalized
        // dictionary lookup, and icon/name writes only happen on change).
        private void RebuildFavoriteRows()
        {
            var resolved = new List<ZepFolderEntry>();
            var tints = new List<ZepTagColor>();
            try
            {
                for (var i = 0; i < _paths.Count; i++)
                {
                    var entry = ResolveEntry(_paths[i]);
                    if (entry == null)
                    {
                        continue;
                    }

                    resolved.Add(entry);
                    tints.Add(_folderTintQuery?.Invoke(entry.Path) ?? ZepTagColor.None);
                }
            }
            catch (ProjectTagStateUnavailableException)
            {
                return;
            }

            _resolved.Clear();
            _resolved.AddRange(resolved);
            for (var i = 0; i < _resolved.Count; i++)
            {
                VisualElement row;
                if (i < _favoriteList.childCount)
                {
                    row = _favoriteList[i];
                }
                else
                {
                    row = CreateFavoriteRow();
                    _favoriteList.Add(row);
                }

                UpdateFavoriteRow(row, _resolved[i], tints[i]);
            }

            while (_favoriteList.childCount > _resolved.Count)
            {
                _favoriteList.RemoveAt(_favoriteList.childCount - 1);
            }

            ApplyExpandedState();
        }

        private ZepFolderEntry ResolveEntry(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (_folderEntryQuery != null)
            {
                var entry = _folderEntryQuery(path);
                return entry != null ? entry : null;
            }

            var normalized = AssetPathFormat.Normalize(path);
            return normalized.Length == 0
                ? null
                : new ZepFolderEntry(
                    AssetPathFormat.GetFileName(normalized),
                    normalized,
                    0,
                    true);
        }

        private VisualElement CreateFavoriteRow()
        {
            // Focusable so a click keeps keyboard focus inside the sidebar —
            // the selection fill reads focus from the pane (SidebarWidget) —
            // and so the row is the keyboard cursor for the legs above.
            var row = new VisualElement { focusable = true };
            row.AddToClassList(ZepUiClasses.FolderRow);
            row.AddToClassList(ZepUiClasses.SidebarRow);

            var iconContainer = new VisualElement();
            iconContainer.AddToClassList(ZepUiClasses.RowIcon);
            var iconImage = new VisualElement();
            iconImage.AddToClassList(ZepUiClasses.RowIconImage);
            iconImage.AddToClassList(ZepUiClasses.ThemedFolderIcon);
            iconContainer.Add(iconImage);
            row.Add(iconContainer);

            var content = new VisualElement();
            content.AddToClassList(ZepUiClasses.RowContent);
            var label = new Label();
            label.AddToClassList(ZepUiClasses.RowPrimaryText);
            content.Add(label);
            row.Add(content);

            // No click handler here: left clicks are committed by the drag
            // interaction on pointer-up (after the gesture is known not to be
            // a drag), and context triggers are consumed by the widget-root
            // handlers before either can react.
            return row;
        }

        private void UpdateFavoriteRow(
            VisualElement row,
            ZepFolderEntry entry,
            ZepTagColor tint)
        {
            row.userData = entry.Path;
            row.tooltip = entry.Path;

            var iconImage = row[0][0];
            var label = (Label)row[1][0];

            var texture = _iconRepository.GetFolderIcon(entry.Path);
            var current = iconImage.style.backgroundImage;
            if (current.keyword == StyleKeyword.Null || current.value.texture != texture)
            {
                iconImage.style.backgroundImage = texture != null
                    ? new StyleBackground(texture)
                    : new StyleBackground();
            }

            // The tint was acquired with the entry before publication, so a
            // recovery refusal cannot leave a partially refreshed row set.
            AssetEntryRenderer.ApplyFolderIconTint(iconImage, isFolder: true, tint);

            if (label.text != entry.Name)
            {
                label.text = entry.Name;
            }

            row.EnableInClassList(
                ZepUiClasses.RowSelected,
                string.Equals(_selectedPath, entry.Path, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplySelectedClass()
        {
            foreach (var child in _favoriteList.Children())
            {
                if (child.userData is not string path)
                {
                    continue;
                }

                child.EnableInClassList(
                    ZepUiClasses.RowSelected,
                    string.Equals(_selectedPath, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        // The actions a favorite row offers beyond navigation: hand the folder
        // to the tree (the trees stay put on a favorite click, so this is how
        // the folder's place in the hierarchy is shown on demand) and drop the
        // bookmark. Same themed popup as every other menu in the panel.
        private void ShowRowMenu(string path, Rect anchor)
        {
            ContextMenuPopupRenderer.Show(this, BuildRowMenuItems(path), null, anchor);
        }

        // Split out so the items — and what they raise — can be asserted
        // without driving the popup renderer (which needs a live host window).
        internal IReadOnlyList<ZepContextMenuItem> BuildRowMenuItems(string path)
        {
            return new[]
            {
                BuildMenuItem(
                    "Zep/Favorites/Reveal",
                    "Reveal in Tree",
                    () => FavoriteRevealRequested?.Invoke(path)),
                BuildMenuItem(
                    "Zep/Favorites/Remove",
                    "Remove from Favorites",
                    () => FavoriteRemoveRequested?.Invoke(path)),
            };
        }

        private static ZepContextMenuItem BuildMenuItem(string commandPath, string label, Action onClick)
        {
            return new ZepContextMenuItem(
                commandPath,
                commandPath,
                label,
                string.Empty,
                true,
                false,
                false,
                Array.Empty<ZepContextMenuItem>(),
                onClick: onClick);
        }

        private void OnRowContextMenuPointerDown(PointerDownEvent evt)
        {
            if (!ZepContextMenuInput.IsPointerTrigger(evt)
                || !TryGetFavoritePathAtTarget(evt.target as VisualElement, out var path))
            {
                return;
            }

            ShowRowMenu(path, ZepContextMenuInput.PointerAnchor(evt));
            ZepContextMenuInput.Consume(evt);
        }

        private static void OnRowContextMenuMouseDown(MouseDownEvent evt)
        {
            if (ZepContextMenuInput.IsMouseTrigger(evt))
            {
                ZepContextMenuInput.Consume(evt);
            }
        }

        private bool TryGetFavoritePathAtTarget(VisualElement target, out string path)
        {
            path = string.Empty;
            var current = target;
            while (current != null && current != this)
            {
                if (current.userData is string candidate && candidate.Length > 0)
                {
                    path = candidate;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        internal ZepSplitDragPayload SnapshotReorderDragPayload(VisualElement target)
        {
            if (!TryGetFavoritePathAtTarget(target, out var path))
            {
                return null;
            }

            for (var i = 0; i < _resolved.Count; i++)
            {
                var entry = _resolved[i];
                if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return new ZepSplitDragPayload(
                        ZepPaneSource.RootedFolder(AssetPath.From(entry.Path)),
                        entry.Name);
                }
            }

            return null;
        }

        // Fresh-press / detach hygiene, alongside ZepSplitDragAndDrop
        // .Clear(): an abandoned favorite drag must not read as a reorder later.
        internal void CancelReorderDragPreparation()
        {
            _reorderDrag.Cancel();
            _reorderDragActive = false;
        }

        private void CommitReorderSourceClick(ZepSplitDragPayload payload)
        {
            var path = payload.Source.RootFolderPath.Value;
            for (var i = 0; i < _resolved.Count; i++)
            {
                if (string.Equals(_resolved[i].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    FavoriteClicked?.Invoke(path);
                    return;
                }
            }
        }

        private void StartFavoriteDrag(ZepSplitDragPayload payload)
        {
            PrepareFavoriteDrag(payload);
            if (!ZepWindowDocking.Start(payload.Source, payload.DisplayName, this))
                DragAndDrop.StartDrag(payload.DisplayName);
        }

        // The bus half of a favorite drag, without Unity's StartDrag (which
        // needs a live GUI event): what the split zones and this list read.
        internal void PrepareFavoriteDrag(ZepSplitDragPayload payload)
        {
            _reorderDragActive = true;
            ZepSplitDragAndDrop.Prepare(payload);
        }

        private bool TryReadReorderPath(out string path)
        {
            path = null;
            if (!_reorderDragActive || !ZepSplitDragAndDrop.TryRead(out var payload))
            {
                return false;
            }

            // A stale flag (favorite dropped outside any target, then an
            // external/asset drag arrives without a PrepareStartDrag in this
            // process) must not hijack a payload-carrying drag: a favorite
            // drag never publishes paths.
            var draggedPaths = DragAndDrop.paths;
            if (draggedPaths != null && draggedPaths.Length > 0)
            {
                return false;
            }

            path = payload.Source.RootFolderPath.Value;
            return true;
        }

        // Insertion point from the pointer's position over the rows: the gap
        // after every row whose midpoint the pointer has passed.
        private int ComputeInsertRowIndex(Vector2 panelPosition)
        {
            var localY = _favoriteList.WorldToLocal(panelPosition).y;
            var index = 0;
            for (var i = 0; i < _favoriteList.childCount; i++)
            {
                if (localY > _favoriteList[i].layout.center.y)
                {
                    index = i + 1;
                }
            }

            return index;
        }

        private void ShowInsertLineAt(int insertIndex)
        {
            var rowCount = _favoriteList.childCount;
            if (rowCount == 0)
            {
                HideInsertLine();
                return;
            }

            // Rows sit at a fixed pitch with no gap; the 2px caret straddles
            // the boundary between two rows (or the list's edges at the ends).
            var y = insertIndex < rowCount
                ? _favoriteList[insertIndex].layout.yMin - 1f
                : _favoriteList[rowCount - 1].layout.yMax - 1f;
            _insertLine.style.top = Mathf.Max(0f, _favoriteList.layout.yMin + y);
            _insertLine.style.display = DisplayStyle.Flex;
        }

        private void HideInsertLine()
        {
            _insertLine.style.display = DisplayStyle.None;
        }

        private void ClearDragFeedback()
        {
            SetDragOver(false);
            HideInsertLine();
        }
    }
}
