using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.State;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Editor.Bridge.Settings;
using Zep.Editor.UI;
using Zep.Editor.UI.Sidebar;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using static Zep.Core.State.ZepSelectionPredicates;

namespace Zep.Editor.UI.ContextMenu
{
    // Concrete handlers for every context-menu item and shortcut action the
    // panel exposes. Owns prompt/dialog choreography, the create-tag color
    // state, and routing into ZepController. ContextMenuWidget
    // binds its menu items directly to these methods at build time; shortcut
    // keys forward via IZepShortcutHost on the shell.
    internal sealed class ContextMenuActions
    {
        private readonly ZepController _commandHandler;
        private readonly ZepCreateOrchestrator _createOrchestrator;
        private readonly ZepPromptOverlayWidget _promptOverlay;
        private readonly ZepState _state;
        private readonly SidebarWidget _sidebar;
        private readonly ZepOverlayPageController _overlayController;
        private readonly Action<ZepPaneState, string> _openFolder;
        // Optional, the way the create orchestrator's is: a refused sidebar
        // rename writes the status bar, but the bar drops a message identical
        // to the one it is showing, so the toast is the channel that reacts
        // to a repeat. The shell passes the same toast.
        private readonly Action<string> _showError;
        // The folder menus' Add/Remove from Favorites, both owned by the shell
        // (the favorites store is shell-side state): membership decides the
        // item's label, the toggle mutates and repaints. Absent (null) means
        // the menus offer no favorites item.
        private readonly Func<string, bool> _isFavorite;
        private readonly Action<string> _toggleFavorite;
        private readonly Func<bool> _isCompiling;
        private readonly Func<bool> _isUpdating;
        private readonly Func<string, string, string, string, bool> _displayDialog;

        // Mutable across the prompt callback's lifetime: the picker writes to
        // it on change, and the submit handler reads it back when the user
        // confirms. Lives here because the prompt+color flow is owned here.
        private ZepTagColor _promptSelectedTagColor = ZepTagColor.Blue;

        internal ContextMenuActions(
            ZepController commandHandler,
            ZepCreateOrchestrator createOrchestrator,
            ZepPromptOverlayWidget promptOverlay,
            ZepState state,
            SidebarWidget sidebar,
            ZepOverlayPageController overlayController,
            Action<ZepPaneState, string> openFolder = null,
            Action<string> showError = null,
            Func<string, bool> isFavorite = null,
            Action<string> toggleFavorite = null,
            Func<bool> isCompiling = null,
            Func<bool> isUpdating = null,
            Func<string, string, string, string, bool> displayDialog = null)
        {
            _commandHandler = commandHandler;
            _createOrchestrator = createOrchestrator;
            _promptOverlay = promptOverlay;
            _state = state;
            _sidebar = sidebar;
            _overlayController = overlayController;
            _openFolder = openFolder;
            _showError = showError;
            _isFavorite = isFavorite;
            _toggleFavorite = toggleFavorite;
            _isCompiling = isCompiling ?? (() => EditorApplication.isCompiling);
            _isUpdating = isUpdating ?? (() => EditorApplication.isUpdating);
            _displayDialog = displayDialog ?? EditorUtility.DisplayDialog;
        }

        internal bool OffersFavorites => _isFavorite != null && _toggleFavorite != null;

        internal bool IsFavorite(string folderPath) => _isFavorite != null && _isFavorite(folderPath);

        internal void ToggleFavorite(string folderPath) => _toggleFavorite?.Invoke(folderPath);

        private List<ZepAssetEntry> SelectedAssetEntries(ZepPaneState pane)
            => LivePane(pane)?.GetSelectedAssetEntries() ?? new List<ZepAssetEntry>();

        private ZepPaneState LivePane(ZepPaneState pane)
            => _state != null && _state.IsLivePane(pane) ? pane : null;

        private ZepPaneState ActivePane => LivePane(_state?.ActivePane);

        private ZepFolderEntry SelectedFolder(ZepPaneState pane)
            => ReferenceEquals(pane, _state?.PrimaryPane) ? _sidebar?.SelectedFolderEntry : null;

        internal void PromptCreateFolder() => PromptCreateFolderAt(ActivePane, null);

        internal void PromptCreateFolderAt(
            ZepPaneState pane,
            string targetFolderOverride)
        {
            if (LivePane(pane) != null)
            {
                _createOrchestrator?.BeginCreateFolder(pane, targetFolderOverride);
            }
        }

        internal void PromptCreateScriptAt(ZepPaneState pane, string targetFolderOverride)
        {
            _createOrchestrator?.BeginCreateAsset(
                pane,
                "Assets/Create/Scripting/MonoBehaviour Script",
                "MonoBehaviour Script",
                targetFolderOverride);
        }

        internal void PromptCreateSceneAt(ZepPaneState pane, string targetFolderOverride)
        {
            _createOrchestrator?.BeginCreateAsset(pane, "Assets/Create/Scene", "Scene", targetFolderOverride);
        }

        internal void PromptCreateMaterialAt(ZepPaneState pane, string targetFolderOverride)
        {
            _createOrchestrator?.BeginCreateAsset(pane, "Assets/Create/Material", "Material", targetFolderOverride);
        }

        internal void PromptCreateAsset(
            ZepPaneState pane,
            ZepContextMenuItem item,
            string targetFolderPath,
            IReadOnlyList<string> sourcePaths = null)
        {
            if (item == null) return;
            _createOrchestrator?.BeginCreateAsset(
                pane,
                item.NormalizedCommandPath,
                item.Label,
                targetFolderPath,
                sourcePaths);
        }

        internal void PromptCreateTag() => PromptCreateTag(ActivePane);

        internal void PromptCreateTag(ZepPaneState pane)
        {
            if (_commandHandler == null || LivePane(pane) == null)
            {
                return;
            }

            _promptSelectedTagColor = ZepTagColor.Blue;
            ShowCreateTagPrompt(pane, string.Empty);
        }

        // Same rule as the sidebar folder rename below: a name that is already
        // taken (or empty once the controller has looked at it) is REFUSED, and
        // the refusal has to be visible and recoverable. The prompt the submit
        // just closed re-opens with what was typed — selected, so one keystroke
        // replaces it — keeping the color that was picked, and the reason goes
        // to the toast as well as the status bar, because the bar drops a
        // message identical to the one it is already showing and a second try
        // at the same name would otherwise say nothing at all.
        // Escape and Cancel close the re-opened prompt exactly as they close the
        // first one, creating nothing.
        private void ShowCreateTagPrompt(ZepPaneState pane, string initialName)
        {
            _promptOverlay.Show(
                "New Tag",
                "Name",
                initialName,
                "Create",
                value =>
                {
                    if (LivePane(pane) == null)
                    {
                        return;
                    }

                    if (CreateTagFromPrompt(value, _promptSelectedTagColor))
                    {
                        return;
                    }

                    _showError?.Invoke(_commandHandler.StatusMessage);
                    ShowCreateTagPrompt(pane, value);
                },
                BuildTagColorPickerExtraContent());
        }

        private VisualElement BuildTagColorPickerExtraContent()
        {
            var container = new VisualElement();

            // Caption stacked above the control, not PopupField's own inline label:
            // BaseField reserves a ~120px label column, which left "Color" stranded
            // far from its dropdown while "Name" above it was a stacked caption over
            // a full-width field — two different form layouts in one small card.
            var caption = new Label("Color");
            caption.AddToClassList(ZepUiClasses.PromptFieldLabel);
            container.Add(caption);

            var availableColors = Enum.GetValues(typeof(ZepTagColor))
                .Cast<ZepTagColor>()
                .Where(static color => color != ZepTagColor.None)
                .ToList();

            var colorField = new PopupField<ZepTagColor>(
                availableColors,
                Math.Max(0, availableColors.IndexOf(_promptSelectedTagColor)),
                static color => color.ToString(),
                static color => color.ToString())
            {
                name = "zep-prompt-popup-field"
            };
            colorField.AddToClassList(ZepUiClasses.PromptPopupField);
            colorField.RegisterValueChangedCallback(evt => _promptSelectedTagColor = evt.newValue);
            container.Add(colorField);
            return container;
        }

        internal bool CreateTagFromPrompt(string tagName, ZepTagColor color)
        {
            return _commandHandler != null && _commandHandler.CreateTag(tagName, color);
        }

        internal void PromptRenameSelected() => PromptRenameSelected(ActivePane);

        internal void PromptRenameSelected(ZepPaneState pane)
        {
            if (_commandHandler == null || _createOrchestrator == null || LivePane(pane) == null)
            {
                return;
            }

            // Rename the primary (first) selected asset. A multi-selection renames
            // the first — it must NOT fall through to the sidebar folder, or macOS
            // Return with 2+ assets selected would rename the enclosing browsed
            // folder. Only a genuinely empty asset selection uses the folder branch.
            var selectedAssets = SelectedAssetEntries(pane);
            if (selectedAssets.Count >= 1)
            {
                var selectedAsset = selectedAssets[0];
                var isFolderAsset = (_state != null && _state.IsFolderAssetEntry(selectedAsset));
                _createOrchestrator.BeginRename(pane, selectedAsset.Path, isFolderAsset);
                return;
            }

            var selectedFolder = SelectedFolder(pane);
            if (selectedFolder == null)
            {
                return;
            }

            _createOrchestrator.BeginRename(pane, selectedFolder.Path, true);
        }

        internal void PromptRenameContextTarget(ZepContextMenuRequest request)
        {
            if (_commandHandler == null
                || LivePane(request.Pane) == null
                || request.ContextPaths.Count == 0
                || !request.AreContextTargetsCurrent())
            {
                return;
            }

            var targetPath = request.ContextPaths[0];
            var isFolder = request.IsFolderTarget(targetPath);
            // A background target is the folder being browsed, which has no
            // row in its own listing: the inline editor would arm a session
            // no row binds until the user climbs to the parent, where the
            // folder's row would then open in edit mode unasked. Same prompt
            // as the sidebar, for the same reason.
            if (request.IsSidebarFolder || request.IsBackground)
            {
                if (_promptOverlay == null || !isFolder)
                {
                    return;
                }

                PromptSidebarFolderRename(
                    request,
                    targetPath,
                    Path.GetFileName(targetPath) ?? string.Empty);
                return;
            }

            _createOrchestrator?.BeginRename(
                request.Pane,
                targetPath,
                isFolder,
                request.AreContextTargetsCurrent);
        }

        // The sidebar's folder rename runs through the prompt overlay, not the
        // inline editor, and it follows the same rule: a name that is already
        // taken is REFUSED, never merged, and the refusal has to be visible
        // and recoverable. The prompt the submit just closed re-opens with
        // what was typed — selected, so one keystroke replaces it — and the
        // reason goes to the toast as well as the status bar
        // (ZepCreateOrchestrator.CommitRename is the inline twin).
        // Escape and Cancel close the re-opened prompt exactly as they close
        // the first one, renaming nothing.
        private void PromptSidebarFolderRename(
            ZepContextMenuRequest request,
            string targetPath,
            string initialName)
        {
            _promptOverlay.Show(
                "Rename Folder",
                "Name",
                initialName,
                "Rename",
                newName =>
                {
                    if (LivePane(request.Pane) == null
                        || !request.AreContextTargetsCurrent())
                    {
                        return;
                    }

                    if (_commandHandler.Rename(
                            request.Pane,
                            AssetPath.From(targetPath),
                            newName))
                    {
                        return;
                    }

                    _showError?.Invoke(_commandHandler.StatusMessage);
                    PromptSidebarFolderRename(request, targetPath, newName);
                });
        }

        internal void DuplicateSelected() => DuplicateSelected(ActivePane);

        internal void DuplicateSelected(ZepPaneState pane)
        {
            // No selected-folder fallback (same as DeleteSelected): with nothing
            // selected, Cmd+D must be a no-op, not silently duplicate the browsed
            // folder. A deliberate folder duplicate arrives as a selected folder row.
            DuplicateTargets(
                pane,
                GetSelectedActionPaths(SelectedAssetEntries(pane), null));
        }

        internal void DuplicateContextTargets(ZepContextMenuRequest request)
        {
            if (!request.AreContextTargetsCurrent())
            {
                return;
            }

            DuplicateTargets(request.Pane, request.ContextPaths);
        }

        private void DuplicateTargets(
            ZepPaneState pane,
            IReadOnlyList<string> targetPaths)
        {
            if (_commandHandler == null
                || targetPaths == null
                || targetPaths.Count == 0
                || LivePane(pane) == null)
            {
                return;
            }

            var duplicated = 0;
            var failed = 0;
            var firstFailureReason = string.Empty;
            _commandHandler.RunBulk(() =>
            {
                for (var i = 0; i < targetPaths.Count; i++)
                {
                    // Continue, don't break: one item failing (a read-only source,
                    // an unwritable folder) must not silently abandon the rest of
                    // the selection. Each duplicate is independent, so duplicate
                    // everything that can be duplicated.
                    if (_commandHandler.Duplicate(pane, AssetPath.From(targetPaths[i])))
                    {
                        duplicated++;
                        continue;
                    }

                    failed++;
                    if (failed == 1)
                    {
                        firstFailureReason = _commandHandler.StatusMessage;
                    }
                }
            });

            // Each item's own status overwrote the previous one, so a partly
            // failed batch showed whichever message happened to come last.
            // Report the batch once.
            if (failed > 0)
            {
                var summary =
                    $"Duplicated {duplicated} of {targetPaths.Count} — {failed} failed; first: {firstFailureReason}";
                _commandHandler.ShowStatusMessage(summary);
                // Retrying the same batch fails the same way, and the status bar
                // drops a message identical to the one it is showing, so the
                // toast is the channel that reacts to the repeat.
                _showError?.Invoke(summary);
            }
        }

        internal void CompressSelected(
            ZepPaneState pane,
            IReadOnlyList<string> contextPaths)
        {
            CompressSelected(pane, contextPaths, null);
        }

        internal void CompressContextTargets(ZepContextMenuRequest request)
        {
            if (!request.AreContextTargetsCurrent())
            {
                return;
            }

            CompressSelected(
                request.Pane,
                request.ContextPaths,
                request.AreContextTargetsCurrent);
        }

        private void CompressSelected(
            ZepPaneState pane,
            IReadOnlyList<string> contextPaths,
            Func<bool> targetsAreCurrent)
        {
            if (_commandHandler == null || LivePane(pane) == null)
            {
                return;
            }

            // No selected-folder fallback (same as DeleteSelected): an empty
            // selection must be a no-op, not compress the browsed folder.
            var paths = (contextPaths != null && contextPaths.Count > 0)
                ? contextPaths
                : GetSelectedActionPaths(SelectedAssetEntries(pane), null);
            if (paths == null || paths.Count == 0)
            {
                return;
            }

            var settings = ZepSettings.instance;
            var includeMetaDefault = settings != null && settings.IncludeMetaFilesInArchive;
            var shouldPrompt = settings == null || settings.PromptForMetaOnCompress;

            // Setting off (or no themed overlay available) → skip the prompt and
            // compress straight away with the saved default.
            if (!shouldPrompt || _promptOverlay == null)
            {
                CompressToChosenArchive(pane, paths, includeMetaDefault, targetsAreCurrent);
                return;
            }

            var includeMetaToggle = new Toggle("Include .meta files")
            {
                name = "zep-compress-meta-toggle",
                value = includeMetaDefault
            };
            includeMetaToggle.AddToClassList(ZepUiClasses.PromptToggle);
            includeMetaToggle.tooltip =
                "Keep the sibling/nested .meta files so the archive re-imports into Unity with its guids and import settings intact.";

            var message = paths.Count == 1
                ? $"Compress “{Path.GetFileName(paths[0])}” into a .zip archive."
                : $"Compress {paths.Count} items into a .zip archive.";

            // The themed overlay surfaces the toggle; we read its value back when
            // the user confirms, then fall through to the native save panel.
            _promptOverlay.ShowConfirm(
                "Compress to ZIP",
                message,
                "Compress",
                () => CompressToChosenArchive(
                    pane,
                    paths,
                    includeMetaToggle.value,
                    targetsAreCurrent),
                includeMetaToggle);
        }

        private void CompressToChosenArchive(
            ZepPaneState pane,
            IReadOnlyList<string> paths,
            bool includeMeta,
            Func<bool> targetsAreCurrent)
        {
            if (LivePane(pane) == null
                || (targetsAreCurrent != null && !targetsAreCurrent()))
            {
                return;
            }

            var defaultName = paths.Count == 1
                ? Path.GetFileName(paths[0]) + ".zip"
                : "Archive.zip";
            var archiveAbsolutePath = EditorUtility.SaveFilePanel(
                "Compress to ZIP",
                string.Empty,
                defaultName,
                "zip");
            if (string.IsNullOrEmpty(archiveAbsolutePath))
            {
                return;
            }

            if (targetsAreCurrent != null && !targetsAreCurrent())
            {
                return;
            }

            var typedPaths = new AssetPath[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                typedPaths[i] = AssetPath.From(paths[i]);
            }

            // Deflate is synchronous and a folder of a few thousand assets locks
            // the editor for seconds with no sign of why. The bar is not
            // cancelable: the writer reports no progress to interrupt at, and the
            // callback would have to be threaded through ZepController.
            // Compress first — a Cancel button nothing services would be worse
            // than none. (The writer already deletes a partial archive on any
            // failure, so the cancel leg is only about reaching it.)
            EditorUtility.DisplayProgressBar(
                "Compress to ZIP",
                paths.Count == 1
                    ? "Compressing “" + Path.GetFileName(paths[0]) + "”…"
                    : "Compressing " + paths.Count + " items…",
                0f);
            try
            {
                _commandHandler.Compress(typedPaths, archiveAbsolutePath, includeMeta);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        internal void DeleteSelected() => DeleteSelected(ActivePane);

        internal void DeleteSelected(ZepPaneState pane)
        {
            if (LivePane(pane) == null)
            {
                return;
            }

            var selectedAssets = SelectedAssetEntries(pane);
            var selectedFolder = SelectedFolder(pane);
            DeleteTargets(
                pane,
                GetSelectedActionPaths(selectedAssets, null),
                selectedAssets,
                selectedFolder,
                selectNeighbor: true);
        }

        // Keyboard delete aimed at the sidebar folder tree: same targets and
        // routing as the sidebar row's context-menu Delete. The folder is not
        // part of the pane's asset projection, so no neighbor-select.
        internal void DeleteSidebarFolder(ZepPaneState pane, ZepFolderEntry folder)
        {
            if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
            {
                return;
            }

            DeleteTargets(
                pane,
                new[] { folder.Path },
                Array.Empty<ZepAssetEntry>(),
                folder,
                selectNeighbor: false);
        }

        internal void DeleteContextTargets(ZepContextMenuRequest request)
        {
            if (!request.AreContextTargetsCurrent())
            {
                return;
            }

            DeleteTargets(
                request.Pane,
                request.ContextPaths,
                request.SelectedAssets,
                request.SelectedFolder,
                selectNeighbor: !request.IsSidebarFolder,
                targetsAreCurrent: request.AreContextTargetsCurrent);
        }

        private void DeleteTargets(
            ZepPaneState pane,
            IReadOnlyList<string> targetPaths,
            IReadOnlyList<ZepAssetEntry> selectedAssets,
            ZepFolderEntry selectedFolder,
            bool selectNeighbor,
            Func<bool> targetsAreCurrent = null)
        {
            if (LivePane(pane) == null
                || (targetsAreCurrent != null && !targetsAreCurrent()))
            {
                return;
            }

            var selectedPaths = targetPaths ?? Array.Empty<string>();
            if (selectedPaths.Count == 0)
            {
                return;
            }

            if (_commandHandler == null)
            {
                return;
            }

            // Never offer the confirmation dialog for the Assets/Packages roots
            // or a package root: these can be selected as folder rows but the
            // delete is guaranteed to be refused downstream, so asking the user
            // is a lie.
            var deletablePaths = new List<string>(selectedPaths.Count);
            for (var i = 0; i < selectedPaths.Count; i++)
            {
                if (!AssetPathClassification.IsProjectRootPath(selectedPaths[i])
                    && !AssetPathClassification.IsPackageRootPath(selectedPaths[i]))
                {
                    deletablePaths.Add(selectedPaths[i]);
                }
            }

            if (deletablePaths.Count == 0)
            {
                const string refusal = "Project root and package folders cannot be deleted.";
                _commandHandler.ShowStatusMessage(refusal);
                // Pressing Delete on the roots again must say so again; the
                // status bar drops the identical repeat, the toast does not.
                _showError?.Invoke(refusal);
                return;
            }

            selectedPaths = deletablePaths;

            if (RefuseDeleteWhileEditorBusy())
            {
                return;
            }

            var dialogTitle = selectedPaths.Count == 1 ? "Delete Item" : "Delete Items";
            string dialogMessage;
            if (selectedPaths.Count == 1)
            {
                var itemName = Path.GetFileName(selectedPaths[0]);
                if (string.IsNullOrWhiteSpace(itemName))
                {
                    itemName = selectedPaths[0];
                }

                dialogMessage = $"Delete '{itemName}'?\n\nThis can be undone.";
            }
            else
            {
                dialogMessage = $"Delete {selectedPaths.Count} selected items?\n\nThis can be undone.";
            }

            var confirmed = _displayDialog(
                dialogTitle,
                dialogMessage,
                "Delete",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            // Unity can become busy while the modal dialog is open. A confirmed
            // delete must still be executable when control returns to the panel.
            if (RefuseDeleteWhileEditorBusy())
            {
                return;
            }

            var typedPaths = new AssetPath[selectedPaths.Count];
            for (var i = 0; i < selectedPaths.Count; i++)
            {
                typedPaths[i] = AssetPath.From(selectedPaths[i]);
            }

            if (targetsAreCurrent != null && !targetsAreCurrent())
            {
                return;
            }

            if (selectNeighbor)
            {
                // Asset-list deletion moves the selection to an adjacent
                // survivor, so repeated keyboard deletion remains useful.
                _commandHandler.DeleteAssetsSelectingNeighbor(pane, typedPaths);
                return;
            }

            // A sidebar target is not part of the pane's asset projection, so
            // selecting an "adjacent" asset after deletion would mutate an
            // unrelated selection. Keep the pane selection untouched.
            //
            // Deleting a folder removes its children with it, so a separately
            // selected descendant would then fail identity validation against an
            // item that is already gone — collapse to the outermost targets, the
            // same rule the asset-list delete applies.
            var sidebarPaths = ZepPathHelpers.CollapseNestedPaths(selectedPaths);

            // One bulk group: outside it each folder delete applied its own
            // catalog update, published its own projection and issued its own
            // global AssetDatabase.Refresh from the file operation, so three
            // sidebar folders paid three of each.
            _commandHandler.RunBulk(() =>
            {
                for (var i = 0; i < sidebarPaths.Count; i++)
                {
                    _commandHandler.Delete(pane, AssetPath.From(sidebarPaths[i]));
                }
            });
        }

        private bool RefuseDeleteWhileEditorBusy()
        {
            if (!IsEditorBusy(out var busyMessage))
            {
                return false;
            }

            var message = "Nothing was deleted. " + busyMessage;
            _commandHandler.ShowStatusMessage(message);
            _showError?.Invoke(message);
            return true;
        }

        internal void RevealSelected(ZepPaneState pane)
        {
            // No selected-folder fallback (same as DeleteSelected): an empty
            // selection must be a no-op, not reveal the browsed folder.
            RevealTargets(pane, GetSelectedActionPaths(SelectedAssetEntries(pane), null));
        }

        internal void RevealContextTargets(ZepContextMenuRequest request)
        {
            RevealTargets(request.Pane, request.ContextPaths);
        }

        private void RevealTargets(
            ZepPaneState pane,
            IReadOnlyList<string> targetPaths)
        {
            if (_commandHandler != null
                && LivePane(pane) != null
                && targetPaths != null
                && targetPaths.Count > 0)
            {
                _commandHandler.Reveal(AssetPath.From(targetPaths[0]));
            }
        }

        internal void OpenSelected() => OpenSelected(ActivePane);

        internal void OpenSelected(ZepPaneState pane)
        {
            if (LivePane(pane) == null)
            {
                return;
            }

            var selectedAssets = SelectedAssetEntries(pane);
            if (selectedAssets.Count == 0)
            {
                return;
            }

            // A lone folder navigates into it; anything else — a single file or a
            // multi-selection — opens every selected non-folder asset (Finder/Unity
            // open the whole selection). Folders in a multi-selection are skipped:
            // the panel can't browse into more than one at once.
            if (selectedAssets.Count == 1 && _state != null && _state.IsFolderAssetEntry(selectedAssets[0]))
            {
                _openFolder?.Invoke(pane, selectedAssets[0].Path);
                return;
            }

            for (var i = 0; i < selectedAssets.Count; i++)
            {
                var selectedAsset = selectedAssets[i];
                if (selectedAsset == null || string.IsNullOrWhiteSpace(selectedAsset.Path))
                {
                    continue;
                }

                if (_state != null && _state.IsFolderAssetEntry(selectedAsset))
                {
                    continue;
                }

                _commandHandler?.OpenAsset(AssetPath.From(selectedAsset.Path));
            }
        }

        internal void OpenContextTargets(ZepContextMenuRequest request)
        {
            if (LivePane(request.Pane) == null || request.ContextPaths.Count == 0)
            {
                return;
            }

            if (request.ContextPaths.Count == 1
                && request.IsFolderTarget(request.ContextPaths[0]))
            {
                _openFolder?.Invoke(request.Pane, request.ContextPaths[0]);
                return;
            }

            for (var i = 0; i < request.ContextPaths.Count; i++)
            {
                var path = request.ContextPaths[i];
                if (string.IsNullOrWhiteSpace(path) || request.IsFolderTarget(path))
                {
                    continue;
                }

                _commandHandler?.OpenAsset(AssetPath.From(path));
            }
        }

        internal void RefreshRequested()
        {
            _commandHandler?.Refresh();
        }

        // Returns false when the panel declined to act, so the caller leaves the
        // keystroke unconsumed for Unity's own Edit > Undo.
        internal bool UndoRequested()
        {
            if (_commandHandler == null) return false;
            if (IsEditorBusy(out var busyMessage))
            {
                _commandHandler.ShowStatusMessage(busyMessage);
                // A second press while still compiling produces the same
                // message, which the status bar drops as an identical repeat -
                // so the chord looked dead. The toast reacts to every raise.
                _showError?.Invoke(busyMessage);
                return false;
            }
            var pane = ActivePane;
            if (pane == null) return false;
            // The panel participates in Unity's global timeline. Performing the
            // native operation first lets an Inspector/scene edit interleave
            // correctly; the session bridge replays a panel command only when
            // Unity's serialized panel cursor actually changes.
            _commandHandler.CaptureUndoRedoPane(pane);
            try
            {
                Undo.PerformUndo();
            }
            finally
            {
                EditorApplication.delayCall += () => _commandHandler.ReleaseUndoRedoPane(pane);
            }

            return true;
        }

        internal bool RedoRequested()
        {
            if (_commandHandler == null) return false;
            if (IsEditorBusy(out var busyMessage))
            {
                _commandHandler.ShowStatusMessage(busyMessage);
                // A second press while still compiling produces the same
                // message, which the status bar drops as an identical repeat -
                // so the chord looked dead. The toast reacts to every raise.
                _showError?.Invoke(busyMessage);
                return false;
            }
            var pane = ActivePane;
            if (pane == null) return false;
            _commandHandler.CaptureUndoRedoPane(pane);
            try
            {
                Undo.PerformRedo();
            }
            finally
            {
                EditorApplication.delayCall += () => _commandHandler.ReleaseUndoRedoPane(pane);
            }

            return true;
        }

        // Calling AssetDatabase.MoveAsset (and friends) while the editor is
        // mid-compile or mid-reimport invalidates the in-flight work and
        // triggers an extra cycle — looks to the user like the shortcut did
        // nothing, then "Nothing to undo" on the next press because we already
        // popped the stack. Decline the shortcut with a clear message instead;
        // the user can retry once the editor settles.
        internal bool IsEditorBusy(out string message)
        {
            if (_isCompiling())
            {
                message = "Wait for the compile to finish.";
                return true;
            }
            if (_isUpdating())
            {
                message = "Wait for the editor to finish updating.";
                return true;
            }
            message = null;
            return false;
        }

        internal void OpenSettingsPage()
        {
            if (_overlayController != null) _overlayController.OpenSettings();
            else ZepWindow.OpenSettings(tags: false);
        }

        internal void OpenTagSettings()
        {
            if (_overlayController != null) _overlayController.OpenSettingsToTags();
            else ZepWindow.OpenSettings(tags: true);
        }

        internal void AssignTag(
            ZepPaneState pane,
            IReadOnlyList<string> paths,
            string tagId)
        {
            if (_commandHandler == null
                || LivePane(pane) == null
                || paths == null
                || paths.Count == 0)
            {
                return;
            }

            if (_commandHandler.AddTag(paths.Select(AssetPath.From).ToList(), tagId))
            {
                _sidebar?.AnimateTagHighlight(tagId);
            }
        }

        internal void UnassignTag(
            ZepPaneState pane,
            IReadOnlyList<string> paths,
            string tagId)
        {
            if (_commandHandler == null
                || LivePane(pane) == null
                || paths == null
                || paths.Count == 0)
            {
                return;
            }

            _commandHandler.RemoveTag(paths.Select(AssetPath.From).ToList(), tagId);
        }

        internal void ClearTags(
            ZepPaneState pane,
            IReadOnlyList<string> paths)
        {
            if (_commandHandler == null
                || LivePane(pane) == null
                || paths == null
                || paths.Count == 0)
            {
                return;
            }

            _commandHandler.ClearTags(paths.Select(AssetPath.From).ToList());
        }

    }
}
