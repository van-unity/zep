using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI
{
    // Drives all "inline" create + rename flows — the Unity-default Project-window UX
    // where the asset appears in the files panel with its default name preselected,
    // and the user either presses Enter to accept or types a new name and presses Enter.
    //
    // Flow invariants:
    //   - For create: NOTHING is written to disk until the user commits. The ghost row
    //     is a UI-only placeholder hosted by AssetsBrowserFeature.
    //   - For rename: the existing asset stays untouched until commit.
    //   - Escape cancels; no disk changes, no ghost leftover.
    //   - When we don't have a local recipe for a menu item, we fall back to Unity's
    //     ExecuteMenuItem + cancel its built-in rename + AssetDatabase.RenameAsset so
    //     the user never sees Unity's own Project-window rename overlay.
    internal sealed class ZepCreateOrchestrator
    {
        private readonly ZepState _panelState;
        private readonly ZepController _commandHandler;
        private readonly Func<ZepPaneState, AssetsBrowserFeature> _resolveBrowser;
        private readonly Action<ZepPaneState, string> _onAssetLanded;
        private readonly Action _restorePanelFocus;
        private readonly Action<string> _showError;

        // showError is optional so the create flows can be driven without it;
        // the shell passes the toast. A refused rename writes the status bar
        // too, but the bar drops a message identical to the one it is already
        // showing, so a second attempt at the same taken name said nothing —
        // the toast is the channel that reacts every time (the same one the
        // drop conflict is given).
        internal ZepCreateOrchestrator(
            ZepState panelState,
            ZepController commandHandler,
            Func<ZepPaneState, AssetsBrowserFeature> resolveBrowser,
            Action<ZepPaneState, string> onAssetLanded,
            Action restorePanelFocus,
            Action<string> showError = null)
        {
            _panelState = panelState ?? throw new ArgumentNullException(nameof(panelState));
            _commandHandler = commandHandler;
            _resolveBrowser = resolveBrowser ?? throw new ArgumentNullException(nameof(resolveBrowser));
            _onAssetLanded = onAssetLanded;
            _restorePanelFocus = restorePanelFocus;
            _showError = showError;
        }

        // -----------------------------------------------------------------------
        // Entry points
        // -----------------------------------------------------------------------

        internal void BeginCreateFolder(
            ZepPaneState pane,
            string targetFolderOverride = null)
        {
            var assetsArea = ResolveWritableBrowser(pane);
            if (assetsArea == null) return;

            var targetFolder = NormalizeTargetFolder(
                targetFolderOverride
                ?? pane.ResolveCreateTargetFolderPath(_panelState.IsFolderAssetEntry));
            if (!EnsureWritable(targetFolder)) return;

            var defaultBasename = GenerateUniqueBasename(targetFolder, "New Folder", string.Empty);

            var request = new AssetsAreaInlineEditRequest
            {
                Path = AssetsAreaInlineEditRequest.GhostPath,
                InitialBasename = defaultBasename,
                Extension = string.Empty,
                TypeName = "Folder",
                IsGhost = true,
            };
            request.OnCommit = typed => CommitCreateFolder(
                pane,
                targetFolder,
                defaultBasename,
                typed);
            request.OnCancel = () => { };

            assetsArea.BeginInlineEdit(request);
        }

        internal void BeginCreateAsset(
            ZepPaneState pane,
            string menuPath,
            string label,
            string targetFolderOverride = null,
            IReadOnlyList<string> sourcePaths = null)
        {
            if (string.IsNullOrWhiteSpace(menuPath)) return;

            var assetsArea = ResolveWritableBrowser(pane);
            if (assetsArea == null) return;

            var targetFolder = NormalizeTargetFolder(
                targetFolderOverride
                ?? pane.ResolveCreateTargetFolderPath(_panelState.IsFolderAssetEntry));
            if (!EnsureWritable(targetFolder)) return;

            var hasRecipe = ZepCreateAssetSupport.TryGetDefaultFileName(menuPath, out var defaultFileName);
            string defaultBasename;
            string extension;

            if (hasRecipe && !string.IsNullOrWhiteSpace(defaultFileName))
            {
                defaultBasename = Path.GetFileNameWithoutExtension(defaultFileName) ?? string.Empty;
                extension = Path.GetExtension(defaultFileName) ?? string.Empty;
            }
            else
            {
                defaultBasename = BuildFallbackBasename(label, menuPath);
                extension = string.Empty;
            }

            defaultBasename = GenerateUniqueBasename(targetFolder, defaultBasename, extension);
            var typeName = ResolveTypeNameForExtension(extension);

            var request = new AssetsAreaInlineEditRequest
            {
                Path = AssetsAreaInlineEditRequest.GhostPath,
                InitialBasename = defaultBasename,
                Extension = extension,
                TypeName = typeName,
                IsGhost = true,
            };
            request.OnCommit = typed => CommitCreateAsset(
                pane,
                menuPath,
                targetFolder,
                defaultBasename,
                typed,
                hasRecipe,
                sourcePaths);
            request.OnCancel = () => { };

            assetsArea.BeginInlineEdit(request);
        }

        // typedBasenameOverride is the text the field opens with when the
        // session is being RE-opened after a refused commit: the user's own
        // typing, not the name on disk. The name on disk stays the rename's
        // origin (the no-op check and the empty-input fallback below), so the
        // two are kept apart.
        internal void BeginRename(
            ZepPaneState pane,
            string assetPath,
            bool isFolder,
            Func<bool> targetIsCurrent = null,
            string typedBasenameOverride = null)
        {
            var assetsArea = ResolveWritableBrowser(pane);
            if (assetsArea == null) return;
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            if (!EnsureWritable(assetPath)) return;

            string basename;
            string extension;
            if (isFolder)
            {
                basename = Path.GetFileName(assetPath) ?? string.Empty;
                extension = string.Empty;
            }
            else
            {
                basename = Path.GetFileNameWithoutExtension(assetPath) ?? string.Empty;
                extension = Path.GetExtension(assetPath) ?? string.Empty;
            }

            var request = new AssetsAreaInlineEditRequest
            {
                Path = assetPath,
                InitialBasename = string.IsNullOrEmpty(typedBasenameOverride)
                    ? basename
                    : typedBasenameOverride,
                Extension = extension,
                TypeName = string.Empty,
                IsGhost = false,
            };
            request.OnCommit = typed =>
            {
                if (targetIsCurrent == null || targetIsCurrent())
                {
                    CommitRename(
                        pane,
                        assetPath,
                        basename,
                        typed,
                        isFolder,
                        targetIsCurrent);
                }
            };
            request.OnCancel = () => { };

            assetsArea.BeginInlineEdit(request);
        }

        // -----------------------------------------------------------------------
        // Commit handlers
        // -----------------------------------------------------------------------

        private void CommitCreateFolder(
            ZepPaneState pane,
            string targetFolder,
            string defaultName,
            string typedName)
        {
            if (!_panelState.IsLivePane(pane)) return;
            var finalName = SanitizeBasename(typedName, defaultName);
            if (string.IsNullOrWhiteSpace(finalName)) return;
            if (_commandHandler == null) return;

            // Uniquify BEFORE creating so we know the folder's REAL landed path. If the typed
            // name collides with an existing sibling, the create lands it at "<name> 1", but
            // we were selecting/persisting the un-suffixed "<name>" — a folder that doesn't
            // exist. Passing the already-unique name keeps both in agreement.
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(targetFolder.TrimEnd('/') + "/" + finalName);
            var uniqueName = string.IsNullOrEmpty(uniquePath) ? finalName : Path.GetFileName(uniquePath);

            if (!_commandHandler.CreateFolder(pane, AssetPath.From(targetFolder), uniqueName)) return;

            StageAssetLanding(
                pane,
                string.IsNullOrEmpty(uniquePath)
                    ? targetFolder.TrimEnd('/') + "/" + uniqueName
                    : uniquePath);
        }

        private void CommitCreateAsset(
            ZepPaneState pane,
            string menuPath,
            string targetFolder,
            string defaultBasename,
            string typedBasename,
            bool hasRecipe,
            IReadOnlyList<string> sourcePaths)
        {
            if (!_panelState.IsLivePane(pane)) return;
            var finalBasename = SanitizeBasename(typedBasename, defaultBasename);
            if (string.IsNullOrWhiteSpace(finalBasename)) return;

            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] create commit menuPath={menuPath} hasRecipe={hasRecipe} target={targetFolder} name={finalBasename}");
            if (hasRecipe)
            {
                if (ZepCreateAssetSupport.TryCreateNamedAsset(
                    menuPath,
                    targetFolder,
                    finalBasename,
                    out var createdPath,
                    out var errorMessage))
                {
                    // Stage the reveal before recording Undo. A script import may not have
                    // flushed its .meta GUID yet; RecordCreatedAsset then applies its safe
                    // history-failure refresh, which can start a domain reload immediately.
                    // SessionState must already contain the path when that happens.
                    StageAssetLanding(pane, createdPath);

                    // Record the create in the panel's reversible history before publishing,
                    // so Cmd+Z removes this asset instead of reverting an unrelated prior op
                    // (F5). Recipe creation writes directly to disk and doesn't go through a
                    // command, so it has to be recorded explicitly here.
                    if (_commandHandler != null
                        && !_commandHandler.RecordCreatedAsset(AssetPath.From(createdPath)))
                    {
                        Debug.LogWarning("Zep created '" + createdPath
                            + "', but Unity did not provide a stable identity for Undo.");
                    }
                    _commandHandler?.AcknowledgeAssetPath(AssetPath.From(createdPath));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    Debug.LogWarning(errorMessage);
                }

                _showError?.Invoke(string.IsNullOrWhiteSpace(errorMessage)
                    ? $"Could not create '{finalBasename}'."
                    : errorMessage);
                return;
            }

            // No-recipe fallback — execute Unity's menu item, cancel its built-in
            // rename overlay (if Unity's own Project window is open), then rename the
            // newly-created asset to the user's chosen basename so our panel stays
            // the single source of truth for the rename UX.
            ExecuteUnityMenuItemFallback(
                pane,
                menuPath,
                targetFolder,
                finalBasename,
                sourcePaths);
        }

        private void CommitRename(
            ZepPaneState pane,
            string originalPath,
            string originalBasename,
            string typedBasename,
            bool isFolder,
            Func<bool> targetIsCurrent)
        {
            if (!_panelState.IsLivePane(pane)) return;
            var finalBasename = SanitizeBasename(typedBasename, originalBasename);
            if (string.IsNullOrWhiteSpace(finalBasename)) return;
            if (string.Equals(finalBasename, originalBasename, StringComparison.Ordinal)) return;
            if (_commandHandler == null) return;
            if (_commandHandler.Rename(pane, AssetPath.From(originalPath), finalBasename)) return;

            // A name that is already taken is REFUSED, never merged (Finder's
            // rule), and the refusal has to be visible and recoverable: the
            // session the confirm just closed re-opens on the same item with
            // what the user typed — selected, so one keystroke replaces it —
            // and the reason goes to the toast as well as the status bar.
            _showError?.Invoke(_commandHandler.StatusMessage);
            BeginRename(pane, originalPath, isFolder, targetIsCurrent, finalBasename);
        }

        // -----------------------------------------------------------------------
        // No-recipe fallback — ExecuteMenuItem + rename
        // -----------------------------------------------------------------------

        private void ExecuteUnityMenuItemFallback(
            ZepPaneState pane,
            string menuPath,
            string targetFolder,
            string finalBasename,
            IReadOnlyList<string> sourcePaths)
        {
            // Unity menu items under "Assets/Create/..." use various internal machinery
            // (ProjectWindowUtil.CreateAsset, CreateScriptAssetFromTemplateFile, direct
            // AssetDatabase writes, custom CreateAssetMenu handlers, ...). They don't
            // uniformly respect Selection.activeObject as the target folder, nor do they
            // uniformly update Selection.activeObject to point at the created asset.
            //
            // Subscribe before ExecuteMenuItem so callbacks raised inside the call are
            // not lost. Direct-child snapshots of the target/source folders prove
            // which observed paths are new without an O(project) GUID diff. If native
            // mapping is unavailable, the old GUID diff remains a counted, warned
            // compatibility fallback rather than guessing from Selection.
            var priorSelection = Selection.objects;
            var folderObject = AssetDatabase.LoadAssetAtPath<Object>(targetFolder);

            // Selection-derived create items ("Create Prefab Variant", "Create Material
            // Variant", ...) read the SOURCE asset from the current selection to know
            // what to build a variant of. Selecting the target FOLDER — the right choice
            // for a blank create so ProjectWindowUtil drops the new asset in the right
            // place — destroys that source, so the item becomes disabled and creates
            // nothing (the ghost then commits with no asset and appears to vanish).
            // When the create was invoked on concrete source asset(s), select THEM: the
            // scoped capture + MoveAsset below still relocates whatever gets created into
            // targetFolder under the user's chosen name, so blank creates are unaffected
            // (a create next to a selected sibling lands in that sibling's folder, which
            // equals targetFolder), while variant creates finally get their base.
            var sourceObjects = LoadSourceObjects(sourcePaths);
            if (sourceObjects.Length > 0)
            {
                SetUnitySelectionFromPanel(pane, sourceObjects);
            }
            else if (folderObject != null)
            {
                SetUnitySelectionFromPanel(pane, new[] { folderObject });
            }

            // A Prefab/Material Variant lands next to its SOURCE. Watch that source
            // folder as well as the requested target; the roots below are used only if
            // native folder inspection fails and the warned GUID fallback is required.
            var searchRoots = BuildSearchRoots(targetFolder, sourcePaths);
            var capture = new ZepCreateFallbackCapture(
                BuildDiscoveryFolders(targetFolder, sourcePaths),
                searchRoots);

            if (!EditorApplication.ExecuteMenuItem(menuPath))
            {
                capture.Dispose();
                SetUnitySelectionFromPanel(pane, priorSelection);
                _showError?.Invoke($"Could not create '{finalBasename}'.");
                return;
            }

            if (_panelState.IsLivePane(pane)
                && !ReferenceEquals(_panelState.ActivePane, pane))
            {
                _panelState.ActivatePane(pane);
            }

            // ExecuteMenuItem ran the real "Assets/Create/..." item. When Unity's
            // own Project window is open, its create machinery calls Focus() on the
            // ProjectBrowser to start the built-in rename — stealing focus away from
            // our panel. We cancel that rename AND take focus back on the same tick so
            // the ProjectBrowser never visibly comes forward.
            UnityProjectBrowserRenameInterop.CancelActiveRename();
            _restorePanelFocus?.Invoke();

            // Resolve immediately first. Script-like fallback items can request a
            // domain reload after ExecuteMenuItem returns; an on-disk direct-child
            // result must be renamed, staged, and recorded before that reload starts.
            if (TryFinishUnityMenuItemFallback(
                    pane,
                    targetFolder,
                    finalBasename,
                    capture,
                    finalAttempt: false))
            {
                capture.Dispose();
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (!_panelState.IsLivePane(pane))
                    {
                        return;
                    }

                    UnityProjectBrowserRenameInterop.CancelActiveRename();
                    _restorePanelFocus?.Invoke();

                    TryFinishUnityMenuItemFallback(
                        pane,
                        targetFolder,
                        finalBasename,
                        capture,
                        finalAttempt: true);
                }
                finally
                {
                    capture.Dispose();
                }
            };
        }

        private bool TryFinishUnityMenuItemFallback(
            ZepPaneState pane,
            string targetFolder,
            string finalBasename,
            ZepCreateFallbackCapture capture,
            bool finalAttempt)
        {
            var selectedPath = Selection.activeObject == null
                ? string.Empty
                : AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!capture.TryResolve(selectedPath, finalAttempt, out var createdPath))
            {
                // Only the FINAL attempt reports: the immediate try (finalAttempt: false)
                // failing is expected whenever the asset lands on the delayCall tick, and
                // reporting there would toast once for a create that actually succeeds a
                // moment later — an outright double-report once the retry also fails.
                if (finalAttempt)
                {
                    _showError?.Invoke($"Could not create '{finalBasename}'.");
                }

                return false;
            }

            // Build the desired path in OUR target folder. GenerateUniqueAssetPath
            // guards against clashes if the user chose a name that already exists.
            var extension = Path.GetExtension(createdPath) ?? string.Empty;
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
            var desiredPath = AssetDatabase.GenerateUniqueAssetPath(
                targetFolder.TrimEnd('/') + "/" + finalBasename + extension);

            var finalPath = createdPath;
            if (!string.Equals(createdPath, desiredPath, StringComparison.Ordinal))
            {
                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseMoveAssetCalls);
                var error = AssetDatabase.MoveAsset(createdPath, desiredPath);
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning(
                        $"Zep: failed to relocate '{createdPath}' to '{desiredPath}': {error}");
                }
                else
                {
                    finalPath = desiredPath;
                }
            }

            RewriteCreatedScriptType(createdPath, finalPath);

            // Persist the reveal path before history work that can start a reload.
            StageAssetLanding(pane, finalPath);

            // ExecuteMenuItem may have registered Unity's own create record. The
            // panel owns the exact-payload inverse, so remove that duplicate native
            // record before adding the panel cursor marker.
            var recordedByPanel = _commandHandler != null
                && _commandHandler.RecordCreatedAsset(AssetPath.From(finalPath));
            var createdObject = AssetDatabase.LoadMainAssetAtPath(finalPath);
            if (recordedByPanel && createdObject != null)
            {
                Undo.ClearUndo(createdObject);
            }

            return true;
        }

        private static void RewriteCreatedScriptType(
            string originalPath,
            string finalPath)
        {
            if (!string.Equals(
                    Path.GetExtension(finalPath),
                    ".cs",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var originalTypeName = Path.GetFileNameWithoutExtension(originalPath);

            // The menu item already wrote a VALID default identifier. The final file name
            // is only a file name, so it has to be reduced to an identifier before it can
            // replace that type name — and when no identifier can be derived the rewrite is
            // skipped entirely, leaving the valid default in place. Stamping the raw name
            // would turn a benign name/class mismatch into a file that does not compile.
            var finalTypeName = ScriptTemplateText.DeriveScriptIdentifier(
                Path.GetFileNameWithoutExtension(finalPath));
            if (string.IsNullOrEmpty(finalTypeName)
                || string.Equals(originalTypeName, finalTypeName, StringComparison.Ordinal))
            {
                return;
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var absolutePath = Path.Combine(
                projectRoot,
                finalPath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                var content = File.ReadAllText(absolutePath);
                var rewritten = ScriptTemplateText.ReplaceScriptIdentifier(
                    content,
                    originalTypeName,
                    finalTypeName);
                if (string.Equals(content, rewritten, StringComparison.Ordinal))
                {
                    return;
                }

                File.WriteAllText(absolutePath, rewritten, new UTF8Encoding(false));
                ZepPerfLog.Count(
                    ZepPerfCounter.AssetDatabaseImportAssetCalls);
                AssetDatabase.ImportAsset(
                    finalPath,
                    ImportAssetOptions.ForceSynchronousImport
                    | ImportAssetOptions.ForceUpdate);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Zep renamed '{originalPath}' to '{finalPath}', "
                    + $"but could not update its script type: {exception.Message}");
            }
        }

        // Load the main asset object for each source path the create was invoked on.
        // These are the right-clicked asset(s) — the base a variant-style create needs
        // selected. Skips paths that no longer resolve; returns an empty array when
        // there was no source context (the + button, empty-space right-click), which
        // keeps the target-folder selection path intact for blank creates.
        private static Object[] LoadSourceObjects(IReadOnlyList<string> sourcePaths)
        {
            if (sourcePaths == null || sourcePaths.Count == 0)
            {
                return Array.Empty<Object>();
            }

            var objects = new List<Object>(sourcePaths.Count);
            for (var i = 0; i < sourcePaths.Count; i++)
            {
                var path = sourcePaths[i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset != null && !objects.Contains(asset))
                {
                    objects.Add(asset);
                }
            }

            return objects.Count == 0 ? Array.Empty<Object>() : objects.ToArray();
        }

        // Search roots for the before/after GUID diff. "Assets" always; plus the
        // specific package root of the target folder / sources when a create targets a
        // writable embedded/local package, so a variant created under Packages/<pkg>/ is
        // seen by the diff. Deliberately NOT a blanket "Packages" — that would scan all
        // of PackageCache on every create.
        private static string[] BuildSearchRoots(string targetFolder, IReadOnlyList<string> sourcePaths)
        {
            var roots = new List<string> { "Assets" };
            AddPackageRoot(roots, targetFolder);
            if (sourcePaths != null)
            {
                for (var i = 0; i < sourcePaths.Count; i++)
                {
                    AddPackageRoot(roots, sourcePaths[i]);
                }
            }

            return roots.ToArray();
        }

        private static string[] BuildDiscoveryFolders(
            string targetFolder,
            IReadOnlyList<string> sourcePaths)
        {
            var folders = new List<string>();
            AddFolder(folders, targetFolder);
            if (sourcePaths != null)
            {
                for (var i = 0; i < sourcePaths.Count; i++)
                {
                    var normalized = AssetPathFormat.Normalize(sourcePaths[i]);
                    AddFolder(folders, Path.GetDirectoryName(normalized)?.Replace('\\', '/'));
                }
            }

            return folders.ToArray();
        }

        private static void AddFolder(List<string> folders, string path)
        {
            var normalized = AssetPathFormat.Normalize(path);
            if (!string.IsNullOrEmpty(normalized)
                && !folders.Exists(existing => string.Equals(
                    existing,
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
            {
                folders.Add(normalized);
            }
        }

        // If assetPath lives under Packages/, add its package root ("Packages/<pkg>").
        private static void AddPackageRoot(List<string> roots, string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            var normalized = assetPath.Replace('\\', '/');
            if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return;

            var segments = normalized.Split('/');
            if (segments.Length < 2 || string.IsNullOrEmpty(segments[1])) return;

            var root = segments[0] + "/" + segments[1];
            if (!roots.Contains(root)) roots.Add(root);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private bool EnsureWritable(string assetPath)
        {
            if (_commandHandler == null) return true;
            return _commandHandler.CanWrite(AssetPath.From(assetPath));
        }

        private void StageAssetLanding(ZepPaneState pane, string path)
        {
            if (_panelState.IsLivePane(pane) && !string.IsNullOrWhiteSpace(path))
            {
                _onAssetLanded?.Invoke(pane, path);
            }
        }

        private AssetsBrowserFeature ResolveWritableBrowser(ZepPaneState pane)
        {
            return _panelState.IsLivePane(pane)
                && pane.ActiveMode == AssetsPaneMode.Browser
                    ? _resolveBrowser(pane)
                    : null;
        }

        private void SetUnitySelectionFromPanel(
            ZepPaneState pane,
            Object[] objects)
        {
            var nextSelection = objects ?? Array.Empty<Object>();
            var currentSelection = Selection.objects;
            if (currentSelection != null && currentSelection.Length == nextSelection.Length)
            {
                var isSame = true;
                for (var i = 0; i < nextSelection.Length; i++)
                {
                    if (currentSelection[i] != nextSelection[i])
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

            var paths = new List<string>(nextSelection.Length);
            for (var i = 0; i < nextSelection.Length; i++)
            {
                var path = AssetDatabase.GetAssetPath(nextSelection[i]);
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            _commandHandler?.ExpectPanelSelectionChange(pane, paths);
            Selection.objects = nextSelection;
        }

        private static string SanitizeBasename(string typed, string fallback)
        {
            var value = (typed ?? string.Empty).Trim();
            if (value.Length == 0) value = fallback ?? string.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var buffer = new char[value.Length];
            var count = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (Array.IndexOf(invalid, c) >= 0) continue;
                buffer[count++] = c;
            }
            return new string(buffer, 0, count).Trim();
        }

        private static string NormalizeTargetFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Assets";
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            return string.IsNullOrWhiteSpace(normalized) ? "Assets" : normalized;
        }

        private static string GenerateUniqueBasename(string targetFolder, string defaultBasename, string extension)
        {
            var ext = extension ?? string.Empty;
            var candidate = targetFolder.TrimEnd('/') + "/" + defaultBasename + ext;
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
            var unique = AssetDatabase.GenerateUniqueAssetPath(candidate);
            if (string.IsNullOrEmpty(unique)) return defaultBasename;

            var fileName = Path.GetFileName(unique);
            if (ext.Length > 0 && fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - ext.Length);
            }

            return Path.GetFileNameWithoutExtension(fileName);
        }

        // Minimal extension -> Unity type-name map used purely so the ghost row's icon
        // picks a reasonable placeholder. The authoritative extension map lives in
        // Zep.Core.Application.AssetExtensionTypeMap; we keep a
        // compact local copy here restricted to the types we create via menu items.
        private static readonly Dictionary<string, string> GhostIconTypeNameByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".cs", "MonoScript" },
                { ".shader", "Shader" },
                { ".compute", "ComputeShader" },
                { ".raytrace", "RayTracingShader" },
                { ".asmdef", "AssemblyDefinitionAsset" },
                { ".asmref", "AssemblyDefinitionReferenceAsset" },
                { ".mat", "Material" },
                { ".physicMaterial", "PhysicsMaterial" },
                { ".physicsMaterial2D", "PhysicsMaterial2D" },
                { ".asset", "ScriptableObject" },
                { ".unity", "SceneAsset" },
                { ".prefab", "Prefab" },
                { ".anim", "AnimationClip" },
                { ".controller", "AnimatorController" },
                { ".overrideController", "AnimatorOverrideController" },
                { ".mask", "AvatarMask" },
                { ".playable", "PlayableAsset" },
                { ".signal", "SignalAsset" },
                { ".txt", "TextAsset" },
                { ".json", "TextAsset" },
                { ".xml", "TextAsset" },
                { ".html", "TextAsset" },
                { ".css", "TextAsset" },
                { ".md", "TextAsset" },
                { ".uss", "StyleSheet" },
                { ".uxml", "VisualTreeAsset" },
            };

        private static string ResolveTypeNameForExtension(string extensionWithDot)
        {
            if (string.IsNullOrEmpty(extensionWithDot)) return string.Empty;
            return GhostIconTypeNameByExtension.TryGetValue(extensionWithDot, out var typeName)
                ? typeName
                : "Unknown";
        }

        private static string BuildFallbackBasename(string label, string menuPath)
        {
            var trimmed = (label ?? string.Empty).Trim();
            if (trimmed.Length == 0 && !string.IsNullOrWhiteSpace(menuPath))
            {
                var slash = menuPath.LastIndexOf('/');
                trimmed = slash >= 0 && slash < menuPath.Length - 1
                    ? menuPath.Substring(slash + 1)
                    : menuPath;
            }

            if (trimmed.Length == 0) return "New Asset";
            return trimmed.StartsWith("New ", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : "New " + trimmed;
        }

        // -----------------------------------------------------------------------
        // Reflection helper — cancel Unity's built-in ProjectBrowser rename so it
        // can't race us to take focus after ExecuteMenuItem creates an asset.
        // -----------------------------------------------------------------------

        private static class UnityProjectBrowserRenameInterop
        {
            private static readonly Type ProjectBrowserType =
                Type.GetType("UnityEditor.ProjectBrowser, UnityEditor");

            private static readonly MethodInfo EndRenamingNoArg = ProjectBrowserType?.GetMethod(
                "EndRenaming",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);

            private static readonly MethodInfo EndRenamingBool = ProjectBrowserType?.GetMethod(
                "EndRenaming",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(bool) },
                null);

            internal static void CancelActiveRename()
            {
                if (ProjectBrowserType == null) return;

                Object[] openBrowsers;
                try
                {
                    openBrowsers = Resources.FindObjectsOfTypeAll(ProjectBrowserType);
                }
                catch
                {
                    return;
                }

                if (openBrowsers == null || openBrowsers.Length == 0) return;

                for (var i = 0; i < openBrowsers.Length; i++)
                {
                    var browser = openBrowsers[i];
                    if (browser == null) continue;
                    TryInvoke(browser, EndRenamingBool, new object[] { true });
                    TryInvoke(browser, EndRenamingNoArg, Array.Empty<object>());
                }
            }

            private static void TryInvoke(object target, MethodInfo method, object[] args)
            {
                if (method == null || target == null) return;
                try
                {
                    method.Invoke(target, args);
                }
                catch
                {
                    // Best-effort cancellation — swallow version-specific failures.
                }
            }
        }
    }

    // Owns the short subscription/snapshot lifetime for one third-party create.
    // It is deliberately scoped to this UI boundary: the state has no meaning
    // after the next editor tick and must never become another catalog.
    internal sealed class ZepCreateFallbackCapture : IDisposable
    {
        private readonly Dictionary<string, HashSet<string>> _directEntriesBefore =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _observedPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] _searchRoots;
        private readonly HashSet<string> _projectGuidsBefore;
        private bool _disposed;

        internal ZepCreateFallbackCapture(
            IReadOnlyList<string> discoveryFolders,
            string[] searchRoots)
        {
            _searchRoots = searchRoots ?? Array.Empty<string>();
            if (TrySnapshotDirectEntries(discoveryFolders))
            {
                ZepPerfLog.Count(
                    ZepPerfCounter.CreateFallbackScopedCaptures);
            }
            else
            {
                _directEntriesBefore.Clear();
                _projectGuidsBefore = SnapshotProjectAssetGuids(_searchRoots);
                ZepPerfLog.Count(
                    ZepPerfCounter.CreateFallbackProjectWideGuidDiffs);
                Debug.LogWarning(
                    "[Zep] Could not inspect the target/source folders for a create menu item; "
                    + "using the project-wide GUID compatibility fallback, which scans every asset "
                    + "under the search roots; on large projects this can take seconds.");
            }

            ZepAssetPostprocessor.Changed += RecordChanges;
        }

        internal void RecordChanges(ProjectAssetChanges changes)
        {
            AddObserved(changes.Imported);
            AddObserved(changes.Moved);
        }

        internal bool TryResolve(
            string selectedPath,
            bool finalAttempt,
            out string createdPath)
        {
            createdPath = string.Empty;
            var candidates = _projectGuidsBefore == null
                ? FindScopedCandidates()
                : finalAttempt
                    ? FindProjectWideCandidates()
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0)
            {
                return false;
            }

            var normalizedSelection = AssetPathFormat.Normalize(selectedPath);
            if (!string.IsNullOrEmpty(normalizedSelection)
                && candidates.Contains(normalizedSelection))
            {
                createdPath = normalizedSelection;
                return true;
            }

            if (candidates.Count == 1)
            {
                foreach (var candidate in candidates)
                {
                    createdPath = candidate;
                }
                return true;
            }

            if (finalAttempt)
            {
                ZepPerfLog.Count(
                    ZepPerfCounter.CreateFallbackAmbiguousResults);
                Debug.LogWarning(
                    "[Zep] The create menu item produced multiple candidate assets and did not "
                    + "identify one through Selection. The assets were left at Unity's chosen locations.");
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ZepAssetPostprocessor.Changed -= RecordChanges;
        }

        private bool TrySnapshotDirectEntries(IReadOnlyList<string> discoveryFolders)
        {
            if (discoveryFolders == null || discoveryFolders.Count == 0)
            {
                return false;
            }

            try
            {
                for (var i = 0; i < discoveryFolders.Count; i++)
                {
                    var folder = AssetPathFormat.Normalize(discoveryFolders[i]);
                    var absoluteFolder = ProjectPaths.ToAbsolutePath(folder);
                    if (string.IsNullOrEmpty(folder)
                        || string.IsNullOrEmpty(absoluteFolder)
                        || !Directory.Exists(absoluteFolder))
                    {
                        return false;
                    }

                    _directEntriesBefore[folder] = SnapshotDirectEntries(
                        folder,
                        absoluteFolder);
                }

                return _directEntriesBefore.Count > 0;
            }
            catch (Exception ex) when (ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException)
            {
                return false;
            }
        }

        private HashSet<string> FindScopedCandidates()
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pair in _directEntriesBefore)
                {
                    var folder = pair.Key;
                    var absoluteFolder = ProjectPaths.ToAbsolutePath(folder);
                    var current = SnapshotDirectEntries(folder, absoluteFolder);
                    foreach (var path in current)
                    {
                        if (!pair.Value.Contains(path) && IsFile(path))
                        {
                            candidates.Add(path);
                        }
                    }
                }

                foreach (var observedPath in _observedPaths)
                {
                    if (IsProvenNewObservedPath(observedPath) && IsFile(observedPath))
                    {
                        candidates.Add(observedPath);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return candidates;
        }

        private HashSet<string> FindProjectWideCandidates()
        {
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseFindAssetsCalls);
            var afterGuids = AssetDatabase.FindAssets(string.Empty, _searchRoots);
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (afterGuids == null)
            {
                return candidates;
            }

            for (var i = 0; i < afterGuids.Length; i++)
            {
                var guid = afterGuids[i];
                if (string.IsNullOrEmpty(guid) || _projectGuidsBefore.Contains(guid))
                {
                    continue;
                }

                ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
                var path = AssetPathFormat.Normalize(AssetDatabase.GUIDToAssetPath(guid));
                if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                {
                    candidates.Add(path);
                }
            }

            return candidates;
        }

        private bool IsProvenNewObservedPath(string observedPath)
        {
            foreach (var pair in _directEntriesBefore)
            {
                var folder = pair.Key;
                if (observedPath.Length <= folder.Length
                    || !observedPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    || observedPath[folder.Length] != '/')
                {
                    continue;
                }

                var relative = observedPath.Substring(folder.Length + 1);
                var slash = relative.IndexOf('/');
                var directName = slash < 0 ? relative : relative.Substring(0, slash);
                var directPath = folder + "/" + directName;
                if (!pair.Value.Contains(directPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static HashSet<string> SnapshotDirectEntries(
            string folder,
            string absoluteFolder)
        {
            var entries = Directory.GetFileSystemEntries(
                absoluteFolder,
                "*",
                SearchOption.TopDirectoryOnly);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < entries.Length; i++)
            {
                var name = Path.GetFileName(entries[i]);
                if (!string.IsNullOrEmpty(name)
                    && !name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(folder.TrimEnd('/') + "/" + name);
                }
            }

            return paths;
        }

        private static HashSet<string> SnapshotProjectAssetGuids(string[] roots)
        {
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseFindAssetsCalls);
            var guids = AssetDatabase.FindAssets(string.Empty, roots);
            return guids == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(guids, StringComparer.Ordinal);
        }

        private void AddObserved(IReadOnlyList<string> paths)
        {
            if (paths == null)
            {
                return;
            }

            for (var i = 0; i < paths.Count; i++)
            {
                var path = AssetPathFormat.Normalize(paths[i]);
                if (!string.IsNullOrEmpty(path))
                {
                    _observedPaths.Add(path);
                }
            }
        }

        private static bool IsFile(string assetPath)
        {
            var absolutePath = ProjectPaths.ToAbsolutePath(assetPath);
            return !string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath);
        }
    }
}
