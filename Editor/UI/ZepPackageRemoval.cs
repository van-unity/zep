using Zep.Core.Indexing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Zep.Core.FileOperations;
using Zep.Core.Application;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI.AssetsArea.MaterialThumbnails;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Zep.Editor.UI
{
    // Owns the removal decision and the exact inventory shared by its preview
    // and deletion. Package Manager still owns removing the package itself.
    [InitializeOnLoad]
    internal static class ZepPackageRemoval
    {
        internal const string PackageName = "com.van-unity.zep";
        internal const string MenuPath = "Tools/Zep/Remove Zep and all data...";

        private static CleanupPlan _approvedPlan;
        private static RemoveRequest _request;

        // Native dialogs are a Unity boundary and cannot be answered in batchmode.
        internal static Func<string, string, string, string, bool> DialogForTests;

        internal sealed class CleanupPlan
        {
            internal readonly string ProjectRoot;
            internal readonly List<string> Files = new List<string>();
            internal readonly List<string> Directories = new List<string>();
            internal readonly List<string> PreferenceKeys = new List<string>();
            internal readonly string DiagnosticLogPath;
            internal string Description;

            internal CleanupPlan(string projectRoot, string diagnosticLogPath)
            {
                ProjectRoot = Path.GetFullPath(projectRoot);
                DiagnosticLogPath = diagnosticLogPath;
            }
        }

        static ZepPackageRemoval()
        {
            Events.registeringPackages += OnRegisteringPackages;
            Events.registeredPackages += _ =>
            {
                if (_approvedPlan != null && _request == null)
                {
                    _approvedPlan = null;
                    ReportProblem("Unity kept the package registered, so its data was kept too.");
                }
            };
        }

        [MenuItem(MenuPath)]
        private static void RemovePackageAndData()
        {
            try
            {
                RemoveWithConfirmation(CaptureCurrentProject(), () =>
                {
                    _request = Client.Remove(PackageName);
                    EditorApplication.update += PollRemovalRequest;
                });
            }
            catch (Exception ex)
            {
                ReportProblem("Could not start removal. Package data was kept. " + ex.Message);
            }
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool CanRemovePackageAndData()
        {
            if (_request != null || _approvedPlan != null)
            {
                return false;
            }
            var package = PackageInfo.FindForAssembly(typeof(ZepPackageRemoval).Assembly);
            // Unity's Remove API cannot remove embedded packages or a dependency
            // still required by another package. Do not delete data on those paths.
            return package != null && package.isDirectDependency
                && package.source != PackageSource.Embedded;
        }

        internal static bool RemoveWithConfirmation(CleanupPlan plan, Action requestRemoval)
        {
            if (!Confirm(plan, dedicated: true))
            {
                return false;
            }

            _approvedPlan = plan;
            try
            {
                // Deletion waits for registeringPackages, so a failed request
                // cannot erase data for a package that is still installed.
                requestRemoval();
                return true;
            }
            catch
            {
                _approvedPlan = null;
                throw;
            }
        }

        private static void PollRemovalRequest()
        {
            if (_request == null || !_request.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollRemovalRequest;
            if (_request.Status == StatusCode.Failure)
            {
                _approvedPlan = null;
                ReportProblem("Package removal failed. Package data was kept. " + _request.Error?.message);
            }
            // A successful UPM request may precede package registration. Keep
            // consent until registeringPackages consumes it, not just until
            // the resolver's request completes.
            _request = null;
        }

        private static void OnRegisteringPackages(PackageRegistrationEventArgs args)
        {
            HandleRemovedPackages(
                args.removed.Select(package => package.name),
                CaptureCurrentProject,
                PrepareForCleanup);
        }

        internal static void HandleRemovedPackages(
            IEnumerable<string> removedNames,
            Func<CleanupPlan> capture,
            Action prepareForCleanup)
        {
            // Updates live in changedFrom/changedTo, not removed. Neither an
            // update nor another package's removal should prompt or delete.
            if (!removedNames.Contains(PackageName, StringComparer.Ordinal))
            {
                return;
            }

            try
            {
                var plan = capture();
                var alreadyConfirmed = _approvedPlan != null
                    && !plan.Files.Except(_approvedPlan.Files).Any()
                    && !plan.Directories.Except(_approvedPlan.Directories).Any();
                _approvedPlan = null;
                if (!alreadyConfirmed && !Confirm(plan, dedicated: false))
                {
                    return;
                }

                prepareForCleanup();
                var failures = DeleteData(plan);
                if (failures.Count > 0)
                {
                    ReportProblem("Package removal is continuing, but these data could not be deleted:\n"
                        + string.Join("\n", failures));
                }
            }
            catch (Exception ex)
            {
                _approvedPlan = null;
                ReportProblem("Package removal is continuing. Cleanup could not finish: " + ex.Message);
            }
        }

        private static bool Confirm(CleanupPlan plan, bool dedicated)
        {
            var title = dedicated ? "Remove Zep and all data?" : "Delete Zep data?";
            var message = (dedicated
                ? "Remove Zep and permanently delete the following data?\n\n"
                : "Unity is removing Zep. Permanently delete the following data?\n\n")
                + "Package: Packages/" + PackageName + " (removed by Unity)\n\n"
                + plan.Description
                + "\nNormal project assets remain."
                + (dedicated ? string.Empty : "\nKeeping data does not cancel package removal.");
            var accept = dedicated ? "Remove and delete all data" : "Delete all package data";
            var decline = dedicated ? "Cancel" : "Keep data";
            return DialogForTests != null
                ? DialogForTests(title, message, accept, decline)
                : EditorUtility.DisplayDialog(title, message, accept, decline);
        }

        private static CleanupPlan CaptureCurrentProject()
        {
            var themePaths = AssetDatabase.FindAssets("t:ZepUserTheme t:ZepDefaultThemes")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal));
            var keys = new[]
            {
                ZepThemePreferences.ThemeStringPreferenceKey,
                ZepThemePreferences.ThemeLegacyPreferenceKey,
                ZepThemePreferences.AssetViewModePreferenceKey,
                ZepThemePreferences.AssetGridItemSizePreferenceKey,
                ZepThemePreferences.FolderPaneWidthPreferenceKey,
                ZepThemePreferences.SidebarVisiblePreferenceKey,
                UserPrefsThemeProvider.EditorPrefsKey,
                "Zep.QuickLook.Width",
                "Zep.QuickLook.Height",
                "Zep.PerfLoggingToConsole",
                "Zep.SearchWasUsed." + Application.dataPath,
                ZepSearchShortcut.PreferenceKey,
                TopBar.SearchBarWidget.HistoryPreferenceKey
            };
            return Capture(
                ProjectPaths.GetProjectRootAbsolutePath(), themePaths, keys, ZepPerfLog.FilePath);
        }

        internal static CleanupPlan Capture(
            string projectRoot,
            IEnumerable<string> themePaths,
            IEnumerable<string> preferenceKeys,
            string diagnosticLogPath)
        {
            var plan = new CleanupPlan(projectRoot, diagnosticLogPath);
            var description = new StringBuilder();
            description.AppendLine("Project data (existing files and package-owned directories):");
            var settings = new[]
            {
                "ProjectSettings/ZepSettings.asset",
                "ProjectSettings/ZepTagSettings.asset",
                "ProjectSettings/Zep.TagAssignments.json",
                "ProjectSettings/Zep.TagAssignments.json.tmp",
                "ProjectSettings/ZepFavoritesSettings.asset",
                "UserSettings/ZepFavoritesSettings.asset"
            };
            foreach (var path in settings.Concat(themePaths).Distinct(StringComparer.Ordinal))
            {
                var absolutePath = Path.GetFullPath(Path.Combine(plan.ProjectRoot, path));
                CheckProjectPath(plan, absolutePath);
                if (File.Exists(absolutePath))
                {
                    plan.Files.Add(absolutePath);
                    description.AppendLine("• " + path);
                }
                if (File.Exists(absolutePath + ".meta"))
                {
                    CheckProjectPath(plan, absolutePath + ".meta");
                    plan.Files.Add(absolutePath + ".meta");
                    description.AppendLine("• " + path + ".meta");
                }
            }

            foreach (var path in new[]
            {
                "Library/com.van-unity.zep",
                "Library/Zep"
            })
            {
                var absolutePath = Path.Combine(plan.ProjectRoot, path);
                CheckProjectPath(plan, absolutePath);
                // Teardown can wait for queued writers that create a known scope.
                plan.Directories.Add(absolutePath);
                if (Directory.Exists(absolutePath))
                {
                    var count = CountFilesSafely(plan, absolutePath);
                    description.AppendLine($"• {path}/ — all contents ({count} files)");
                }
                else
                {
                    description.AppendLine($"• {path}/ — all contents if created before cleanup (currently absent)");
                }
            }
            description.AppendLine("• Assets/Editor/Zep/UserThemes/ and Assets/Editor/Zep/, with .meta files, if empty");
            description.AppendLine("\nSaved Zep preferences and local themes:");
            description.AppendLine("These are shared across Unity projects on this computer, except the search-use flag and recent search queries.");
            foreach (var key in preferenceKeys.Distinct(StringComparer.Ordinal))
            {
                plan.PreferenceKeys.Add(key);
                if (EditorPrefs.HasKey(key))
                {
                    description.AppendLine("• " + key);
                }
            }
            // Teardown may save a previously unset preference (Quick Look size,
            // for example). The named preference category covers these too.
            description.AppendLine("• Zep session state (navigation, sidebar folds and Undo history)");
            if (File.Exists(diagnosticLogPath))
            {
                description.AppendLine("• Shared diagnostic log: " + diagnosticLogPath);
            }
            plan.Description = description.ToString();
            return plan;
        }

        private static void PrepareForCleanup()
        {
            // Close before deleting: window teardown and controller disposal can
            // finish queued work. Stop write-behind before deleting its folders.
            foreach (var window in Resources.FindObjectsOfTypeAll<ZepQuickLookWindow>())
            {
                window.Close();
            }
            foreach (var window in Resources.FindObjectsOfTypeAll<ZepWindow>())
            {
                window.Close();
            }
            foreach (var window in Resources.FindObjectsOfTypeAll<ZepPaneWindow>())
            {
                window.Close();
            }
            MaterialThumbnailProvider.Shutdown();
            MaterialThumbnailDiskStore.StopWritesForPackageRemoval();
            PersistentAssetIndexCache.StopWritesForPackageRemoval();
            ZepControllerHost.StopForPackageRemoval();
            ZepPerfLog.StopFileLoggingForPackageRemoval();
            EraseSessionState();
        }

        internal static List<string> DeleteData(CleanupPlan plan)
        {
            var failures = new List<string>();
            foreach (var path in plan.Files)
            {
                TryDelete(path, () =>
                {
                    CheckProjectPath(plan, path);
                    File.Delete(path);
                }, failures);
            }
            foreach (var path in plan.Directories)
            {
                TryDelete(path, () =>
                {
                    CheckProjectPath(plan, path);
                    if (Directory.Exists(path))
                    {
                        CountFilesSafely(plan, path);
                        Directory.Delete(path, recursive: true);
                    }
                }, failures);
            }
            foreach (var relativePath in new[] { "Assets/Editor/Zep/UserThemes", "Assets/Editor/Zep" })
            {
                var path = Path.Combine(plan.ProjectRoot, relativePath);
                TryDelete(path, () =>
                {
                    CheckProjectPath(plan, path);
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        CheckProjectPath(plan, path + ".meta");
                        Directory.Delete(path);
                        File.Delete(path + ".meta");
                    }
                }, failures);
            }
            foreach (var key in plan.PreferenceKeys)
            {
                TryDelete(key, () => EditorPrefs.DeleteKey(key), failures);
            }
            TryDelete(plan.DiagnosticLogPath, () => File.Delete(plan.DiagnosticLogPath), failures);
            return failures;
        }

        private static void TryDelete(string path, Action delete, List<string> failures)
        {
            try
            {
                delete();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                failures.Add(path + ": " + ex.Message);
            }
        }

        private static int CountFilesSafely(CleanupPlan plan, string path)
        {
            var count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                CheckProjectPath(plan, entry);
                count += Directory.Exists(entry) ? CountFilesSafely(plan, entry) : 1;
            }
            return count;
        }

        private static void CheckProjectPath(CleanupPlan plan, string path)
        {
            var projectRoot = plan.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar);
            var prefix = projectRoot + Path.DirectorySeparatorChar;
            var absolutePath = Path.GetFullPath(path);
            if (!absolutePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new IOException("Cleanup path is outside the project: " + path);
            }
            // Refuse links, including an ancestor swapped since the preview.
            // Never follow a cache link into unrelated project or user data.
            for (var current = absolutePath; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Cleanup refused a symbolic link: " + current);
                }
                if (current == projectRoot)
                {
                    break;
                }
            }
        }

        private static void EraseSessionState()
        {
            foreach (var key in new[]
            {
                ZepShellView.PendingCreateSelectionSessionKey,
                "Zep.FolderTreeExpanded.Assets",
                "Zep.FolderTreeExpanded.Packages",
                SessionUndoHistoryStorage.DefaultStateKey,
                UndoPayloadStore.SessionKey
            })
            {
                SessionState.EraseString(key);
            }
            foreach (var key in new[]
            {
                "Zep.Sidebar.TagsExpanded",
                "Zep.Sidebar.FavoritesExpanded",
                "Zep.VerifiedThisProcess"
            })
            {
                SessionState.EraseBool(key);
            }
#if UNITY_6000_5_OR_NEWER
            SessionState.EraseEntityId(SessionUndoHistoryStorage.DefaultStateKey + ".CursorInstanceId");
#else
            SessionState.EraseInt(SessionUndoHistoryStorage.DefaultStateKey + ".CursorInstanceId");
#endif
        }

        private static void ReportProblem(string message)
        {
            Debug.LogWarning("[Zep] " + message);
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog("Zep removal", message, "OK");
            }
        }
    }
}
