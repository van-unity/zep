using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zep.Core.Application;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.UI
{
    // WHY: Single owner of all drop-operation routing and the filesystem/prefab
    // helpers it needs. Constructor takes Func/Action callbacks for shell state
    // (selection update, variant-name prompt) instead of a back-reference to
    // the shell; keeps the coupling unidirectional.
    internal sealed class AssetDropOperations
    {
        internal enum PrefabDropSaveMode
        {
            VariantOrNew = 0,
            OriginalFromInstance = 1,
            Cancelled = 2
        }

        // Test-only record of what the pane views actually routed here. A drop
        // that reaches this owner has no other observable in a headless rig:
        // the move itself needs a real AssetDatabase. Recorded before the
        // liveness/mode gate so it measures routing, not the decision.
        internal static int FolderDropsReceivedForTests;
        internal static ZepPaneState LastFolderDropPaneForTests;
        internal static string LastFolderDropDestinationForTests;

        private readonly ZepController _commandHandler;
        private readonly ZepState _viewModel;
        private readonly Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>> _assetSelectionAction;
        private readonly Action<string, Action<string>> _promptVariantName;
        private readonly Action<string> _showError;
        private readonly Func<string, string, ProjectMoveConflictResolution> _askMoveConflict;
        // The move rules themselves live in Core; this class routes drops to them.
        private readonly FolderMoveDrop _moveDrop;

        // showError and askMoveConflict are optional so the drop routes that
        // move nothing can be driven without them; the shell passes the toast,
        // and the dialog falls back to the real one below. The dialog is a Func
        // rather than a call so a headless test can answer it, the same shape
        // promptVariantName already uses.
        internal AssetDropOperations(
            ZepController commandHandler,
            ZepState viewModel,
            Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>> assetSelectionAction,
            Action<string, Action<string>> promptVariantName,
            Action<string> showError = null,
            Func<string, string, ProjectMoveConflictResolution> askMoveConflict = null)
        {
            _commandHandler = commandHandler;
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _assetSelectionAction = assetSelectionAction;
            _promptVariantName = promptVariantName;
            _showError = showError;
            _askMoveConflict = askMoveConflict ?? AskMoveConflictWithDialog;
            _moveDrop = new FolderMoveDrop(
                commandHandler,
                viewModel,
                AssetDatabase.IsValidFolder,
                showError,
                _askMoveConflict);
        }

        // Returns whether the tag was newly applied to at least one asset.
        // ZepController.AddTag filters out non-taggable items and
        // assets that already carry the tag, so a drop where every item is
        // already tagged returns false (no duplicate, nothing to confirm).
        internal bool HandleTagDrop(string tagId, IReadOnlyList<string> projectPaths)
        {
            if (_commandHandler == null || string.IsNullOrWhiteSpace(tagId)
                || projectPaths == null || projectPaths.Count == 0)
            {
                return false;
            }

            var assetPaths = new AssetPath[projectPaths.Count];
            for (var i = 0; i < projectPaths.Count; i++)
            {
                assetPaths[i] = AssetPath.From(projectPaths[i]);
            }

            return _commandHandler.AddTag(assetPaths, tagId);
        }

        // A pane drop uses its browsing destination; a tag-filter result list
        // has no writable current-folder target.
        internal void HandleFolderDrop(
            ZepPaneState pane,
            string targetFolderPath,
            IReadOnlyList<string> projectPaths)
        {
            FolderDropsReceivedForTests++;
            LastFolderDropPaneForTests = pane;
            LastFolderDropDestinationForTests = targetFolderPath;
            if (!_viewModel.IsLivePane(pane) || pane.ActiveMode != AssetsPaneMode.Browser)
            {
                return;
            }

            _moveDrop.MoveDroppedPathsToFolder(pane, targetFolderPath, projectPaths);
        }

        // A sidebar row names its destination independently of the pane mode.
        internal void HandleSidebarFolderDrop(
            ZepPaneState pane,
            string targetFolderPath,
            IReadOnlyList<string> projectPaths)
        {
            _moveDrop.MoveDroppedPathsToFolder(pane, targetFolderPath, projectPaths);
        }

        internal void HandleExternalImport(
            ZepPaneState pane,
            string targetFolderPath,
            IReadOnlyList<string> externalPaths)
        {
            if (!_viewModel.IsLivePane(pane) || pane.ActiveMode != AssetsPaneMode.Browser)
            {
                return;
            }

            ImportDroppedExternalPaths(pane, targetFolderPath, externalPaths);
        }

        internal void HandleSidebarExternalImport(
            ZepPaneState pane,
            string targetFolderPath,
            IReadOnlyList<string> externalPaths)
        {
            ImportDroppedExternalPaths(pane, targetFolderPath, externalPaths);
        }

        private void ImportDroppedExternalPaths(
            ZepPaneState pane,
            string targetFolderPath,
            IReadOnlyList<string> externalPaths)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return;
            }

            // A dropped .unitypackage launches Unity's interactive Import Package
            // dialog instead of being copied in. That doesn't write into the drop
            // target, so route packages before the destination write checks — a
            // package dropped on a read-only target (e.g. a Packages folder) still
            // imports. Only the non-package remainder is copied into the folder.
            var remaining = ExternalAssetImporter.ImportDroppedUnityPackages(externalPaths);
            if (remaining.Count == 0)
            {
                return;
            }

            var dest = AssetPathFormat.Normalize(targetFolderPath);
            if (_commandHandler == null || string.IsNullOrEmpty(dest))
            {
                return;
            }

            // Split off the write check so the refusal is reported: the tree's
            // external-drag leg advertises Copy unconditionally, so an import
            // onto a read-only folder is accepted and would otherwise vanish.
            if (!_commandHandler.CanWrite(AssetPath.From(dest)))
            {
                _showError?.Invoke("Can't import into '" + dest + "' — it is read-only.");
                return;
            }

            if (!AssetDatabase.IsValidFolder(dest))
            {
                return;
            }

            ImportExternalPathsToFolder(pane, remaining, dest);
        }

        internal void HandleSceneObjectDrop(
            ZepPaneState pane,
            IReadOnlyList<GameObject> sceneObjects,
            string destinationFolderOverride)
        {
            if (!_viewModel.IsLivePane(pane) || pane.ActiveMode != AssetsPaneMode.Browser)
            {
                return;
            }

            // Honor the folder row the GameObject was dropped on; only fall back to the
            // browsed/selected folder when the drop didn't land on a valid folder (matching
            // how asset-move and external-import drops on the same row already behave).
            var destinationFolderPath =
                !string.IsNullOrEmpty(destinationFolderOverride) && AssetDatabase.IsValidFolder(destinationFolderOverride)
                    ? destinationFolderOverride
                    : ResolvePrefabDropDestinationFolder(pane);
            if (_commandHandler == null)
            {
                return;
            }

            // Same refusal as the asset-move and external-import legs: the drop
            // was accepted on a folder the gateway will not let us write to, so
            // name it instead of discarding the GameObject in silence.
            if (!_commandHandler.CanWrite(AssetPath.From(destinationFolderPath)))
            {
                if (!string.IsNullOrEmpty(destinationFolderPath))
                {
                    _showError?.Invoke(
                        "Can't create a prefab in '" + destinationFolderPath + "' — it is read-only.");
                }

                return;
            }

            if (!AssetDatabase.IsValidFolder(destinationFolderPath))
            {
                return;
            }

            var saveMode = ResolvePrefabDropSaveMode(sceneObjects);
            if (saveMode == PrefabDropSaveMode.Cancelled)
            {
                return;
            }

            if (TryGetVariantNamePromptContext(sceneObjects, saveMode, out var defaultVariantName)
                && _promptVariantName != null)
            {
                // WHY: Defensive copy of the list; the prompt is async-by-callback
                // and the original list reference may be mutated by the time the
                // user submits.
                var capturedSceneObjects = new List<GameObject>(sceneObjects);
                var capturedDest = destinationFolderPath;
                _promptVariantName(defaultVariantName,
                    customName => CreatePrefabsFromDragDrop(
                        pane,
                        capturedSceneObjects,
                        capturedDest,
                        saveMode,
                        customName));
                return;
            }

            CreatePrefabsFromDragDrop(
                pane,
                sceneObjects,
                destinationFolderPath,
                saveMode,
                null);
        }

        // Escape selects the middle Cancel button; Replace uses the alt slot.
        private static ProjectMoveConflictResolution AskMoveConflictWithDialog(
            string title,
            string message)
        {
            switch (EditorUtility.DisplayDialogComplex(
                title, message, "Keep Both", "Cancel", "Replace"))
            {
                case 0:
                    return ProjectMoveConflictResolution.KeepBoth;
                case 2:
                    return ProjectMoveConflictResolution.Replace;
                default:
                    return ProjectMoveConflictResolution.Skip;
            }
        }

        private void ImportExternalPathsToFolder(
            ZepPaneState pane,
            IReadOnlyList<string> externalPaths,
            string destinationFolderPath)
        {
            if (!_viewModel.IsLivePane(pane)
                || externalPaths == null
                || externalPaths.Count == 0
                || string.IsNullOrEmpty(destinationFolderPath))
            {
                return;
            }

            var result = ExternalAssetImportResult.Empty;
            _commandHandler.RunCatalogBatch(() =>
            {
                result = ExternalAssetImporter.Import(
                    externalPaths,
                    destinationFolderPath);
                for (var i = 0; i < result.ImportedAssetPaths.Count; i++)
                {
                    _commandHandler.AcknowledgeAssetPath(
                        AssetPath.From(result.ImportedAssetPaths[i]));
                    if (!_commandHandler.RecordCreatedAsset(
                        AssetPath.From(result.ImportedAssetPaths[i])))
                    {
                        Debug.LogWarning("Zep imported '"
                            + result.ImportedAssetPaths[i]
                            + "', but Unity did not provide a stable identity for Undo.");
                        break;
                    }
                }
            });
            var importedAssetPaths = result.ImportedAssetPaths;
            if (importedAssetPaths.Count == 0)
            {
                LogExternalImportFailures(result.Failures);
                return;
            }

            LogExternalImportFailures(result.Failures);

            var importedAssetPathTyped = new AssetPath[importedAssetPaths.Count];
            for (var i = 0; i < importedAssetPaths.Count; i++)
            {
                importedAssetPathTyped[i] = AssetPath.From(importedAssetPaths[i]);
            }

            if (_viewModel.IsLivePane(pane))
            {
                pane.SetSelectedAssetPaths(importedAssetPathTyped);
                _assetSelectionAction?.Invoke(pane, pane.GetSelectedAssetEntries());
            }
        }

        private static string ResolvePrefabDropDestinationFolder(ZepPaneState pane)
        {
            var folderPath = AssetPathFormat.Normalize(pane.CurrentFolderPath);
            if (string.IsNullOrEmpty(folderPath))
            {
                folderPath = AssetPathFormat.Normalize(pane.SelectedFolderPath);
            }

            return AssetDatabase.IsValidFolder(folderPath) ? folderPath : string.Empty;
        }

        private void CreatePrefabsFromDragDrop(
            ZepPaneState pane,
            IReadOnlyList<GameObject> sceneObjects,
            string destinationFolderPath,
            PrefabDropSaveMode saveMode,
            string firstCustomName)
        {
            if (!_viewModel.IsLivePane(pane)
                || sceneObjects == null
                || sceneObjects.Count == 0
                || string.IsNullOrWhiteSpace(destinationFolderPath))
            {
                return;
            }

            var createdPaths = new List<string>(sceneObjects.Count);
            for (var i = 0; i < sceneObjects.Count; i++)
            {
                var sceneObject = sceneObjects[i];
                if (sceneObject == null)
                {
                    continue;
                }

                var baseName = i == 0 && !string.IsNullOrWhiteSpace(firstCustomName)
                    ? firstCustomName
                    : GetDefaultDroppedPrefabName(sceneObject, saveMode);
                var prefabName = SanitizePrefabAssetName(baseName);
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
                var prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{destinationFolderPath}/{prefabName}.prefab");
                try
                {
                    var created = saveMode == PrefabDropSaveMode.OriginalFromInstance
                        ? TrySaveOriginalPrefab(sceneObject, prefabPath)
                        : TrySavePrefab(sceneObject, prefabPath);
                    if (created)
                    {
                        createdPaths.Add(prefabPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Zep] Prefab save failed for '{sceneObject.name}': {ex.Message}");
                }
            }

            if (createdPaths.Count > 0)
            {
                _commandHandler.RunBulk(() =>
                {
                    for (var i = 0; i < createdPaths.Count; i++)
                    {
                        if (!_commandHandler.RecordCreatedAsset(AssetPath.From(createdPaths[i])))
                        {
                            Debug.LogWarning("Zep created '" + createdPaths[i]
                                + "', but Unity did not provide a stable identity for Undo.");
                            break;
                        }
                    }
                });

                if (_viewModel.IsLivePane(pane))
                {
                    var selectedPaths = new AssetPath[createdPaths.Count];
                    for (var i = 0; i < createdPaths.Count; i++)
                    {
                        selectedPaths[i] = AssetPath.From(createdPaths[i]);
                    }

                    pane.SetSelectedAssetPaths(selectedPaths);
                    _assetSelectionAction?.Invoke(pane, pane.GetSelectedAssetEntries());
                }
            }
        }

        private static void LogExternalImportFailures(
            IReadOnlyList<(string sourcePath, string reason)> failures)
        {
            if (failures == null)
            {
                return;
            }

            for (var i = 0; i < failures.Count; i++)
            {
                Debug.LogWarning(
                    "[Zep] Failed to import '" + failures[i].sourcePath
                    + "': " + failures[i].reason);
            }
        }

        internal static string SanitizePrefabAssetName(string sourceName)
        {
            var normalized = string.IsNullOrWhiteSpace(sourceName) ? "GameObject" : sourceName.Trim();
            var invalidCharacters = Path.GetInvalidFileNameChars();
            for (var index = 0; index < invalidCharacters.Length; index++)
            {
                normalized = normalized.Replace(invalidCharacters[index], '_');
            }

            return string.IsNullOrWhiteSpace(normalized) ? "GameObject" : normalized;
        }

        internal static PrefabDropSaveMode ResolvePrefabDropSaveMode(IReadOnlyList<GameObject> sceneObjects)
        {
            var hasPrefabInstanceRoot = false;
            for (var i = 0; i < sceneObjects.Count; i++)
            {
                if (!IsPrefabInstanceRoot(sceneObjects[i]))
                {
                    continue;
                }

                hasPrefabInstanceRoot = true;
                break;
            }

            if (!hasPrefabInstanceRoot)
            {
                return PrefabDropSaveMode.VariantOrNew;
            }

            var sourcePrefabName = GetFirstPrefabSourceName(sceneObjects);
            var message = string.IsNullOrWhiteSpace(sourcePrefabName)
                ? "Would you like to create a new original Prefab or a Prefab Variant?"
                : $"Would you like to create a new original Prefab or a variant from the existing Prefab '{sourcePrefabName}'?";

            var choice = EditorUtility.DisplayDialogComplex(
                "Create Prefab or Variant?",
                message,
                "Original Prefab",
                "Prefab Variant",
                "Cancel");

            switch (choice)
            {
                case 0:
                    return PrefabDropSaveMode.OriginalFromInstance;
                case 1:
                    return PrefabDropSaveMode.VariantOrNew;
                default:
                    return PrefabDropSaveMode.Cancelled;
            }
        }

        internal static bool TryGetVariantNamePromptContext(
            IReadOnlyList<GameObject> sceneObjects,
            PrefabDropSaveMode saveMode,
            out string defaultVariantName)
        {
            defaultVariantName = string.Empty;
            if (saveMode != PrefabDropSaveMode.VariantOrNew
                || sceneObjects == null
                || sceneObjects.Count != 1
                || sceneObjects[0] == null
                || !IsPrefabInstanceRoot(sceneObjects[0]))
            {
                return false;
            }

            defaultVariantName = GetDefaultDroppedPrefabName(sceneObjects[0], saveMode);
            return !string.IsNullOrWhiteSpace(defaultVariantName);
        }

        internal static string GetDefaultDroppedPrefabName(GameObject sceneObject, PrefabDropSaveMode saveMode)
        {
            if (sceneObject == null)
            {
                return "GameObject";
            }

            if (saveMode == PrefabDropSaveMode.VariantOrNew
                && TryGetPrefabSourceName(sceneObject, out var sourcePrefabName))
            {
                return $"{sourcePrefabName} Variant";
            }

            return sceneObject.name;
        }

        internal static string GetFirstPrefabSourceName(IReadOnlyList<GameObject> sceneObjects)
        {
            if (sceneObjects == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < sceneObjects.Count; i++)
            {
                var sceneObject = sceneObjects[i];
                if (!IsPrefabInstanceRoot(sceneObject))
                {
                    continue;
                }

                if (TryGetPrefabSourceName(sceneObject, out var sourceName))
                {
                    return sourceName;
                }
            }

            return string.Empty;
        }

        internal static bool TryGetPrefabSourceName(GameObject sceneObject, out string sourcePrefabName)
        {
            sourcePrefabName = string.Empty;
            if (!IsPrefabInstanceRoot(sceneObject))
            {
                return false;
            }

            var sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(sceneObject);
            if (sourceObject != null && !string.IsNullOrWhiteSpace(sourceObject.name))
            {
                sourcePrefabName = sourceObject.name.Trim();
                return true;
            }

            var sourceAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sceneObject);
            if (!string.IsNullOrWhiteSpace(sourceAssetPath))
            {
                var nameFromPath = Path.GetFileNameWithoutExtension(sourceAssetPath);
                if (!string.IsNullOrWhiteSpace(nameFromPath))
                {
                    sourcePrefabName = nameFromPath.Trim();
                    return true;
                }
            }

            return false;
        }

        internal static bool IsPrefabInstanceRoot(GameObject sceneObject)
        {
            if (sceneObject == null || !PrefabUtility.IsAnyPrefabInstanceRoot(sceneObject))
            {
                return false;
            }

            return PrefabUtility.GetPrefabInstanceStatus(sceneObject) != PrefabInstanceStatus.NotAPrefab;
        }

        internal static bool TrySavePrefab(GameObject sceneObject, string prefabPath)
        {
            return PrefabUtility.SaveAsPrefabAssetAndConnect(sceneObject, prefabPath, InteractionMode.UserAction) != null;
        }

        internal static bool TrySaveOriginalPrefab(GameObject sceneObject, string prefabPath)
        {
            if (IsPrefabInstanceRoot(sceneObject))
            {
                PrefabUtility.UnpackPrefabInstance(sceneObject, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            }

            return PrefabUtility.SaveAsPrefabAssetAndConnect(sceneObject, prefabPath, InteractionMode.UserAction) != null;
        }

        internal static string GetPathName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash < 0 ? normalized : normalized.Substring(lastSlash + 1);
        }
    }
}
