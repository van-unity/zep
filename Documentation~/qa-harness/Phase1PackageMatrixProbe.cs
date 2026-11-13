using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.FileOperations;
using UnityEditor;
using UnityEngine;

internal static class Phase1PackageMatrixProbe
{
    private static readonly string[] PackageRoots =
    {
        "Packages/com.van-unity.phase1.embedded",
        "Packages/com.van-unity.phase1.local",
    };

    public static void Run()
    {
        var rows = new List<string>
        {
            "unity\tpackageKind\tautoRefresh\tapi\tpassed\tdetail",
        };
        foreach (var autoRefresh in new[] { true, false })
        {
            SetAutoRefresh(autoRefresh);
            try
            {
                for (var i = 0; i < PackageRoots.Length; i++)
                {
                    ProbePackage(
                        PackageRoots[i],
                        i == 0 ? "embedded" : "local",
                        autoRefresh,
                        rows);
                }
            }
            finally
            {
                SetAutoRefresh(true);
            }
        }

        var outputPath = "/private/tmp/pp-phase1-package-matrix-"
            + Application.unityVersion.Replace('.', '-') + ".tsv";
        File.WriteAllLines(outputPath, rows.ToArray());
        Debug.Log("PHASE1_PACKAGE_MATRIX " + outputPath);
    }

    public static void RunFolderCreateWindowGate()
    {
        var rows = new List<string>
        {
            "unity\tprojectWindow\tpassed\tcreateFolder\tfullSync\trebuild\tpublication",
        };
        var projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser,UnityEditor");
        CloseProjectBrowsers(projectBrowserType);
        var host = ZepControllerHost.Shared;
        host.Core.Refresh();
        ProbeFolderCreate("closed", host, rows);

        if (projectBrowserType != null)
        {
            EditorWindow.GetWindow(projectBrowserType).Show();
        }
        ProbeFolderCreate("open", host, rows);
        CloseProjectBrowsers(projectBrowserType);

        var outputPath = "/private/tmp/pp-phase1-folder-window-gate-"
            + Application.unityVersion.Replace('.', '-') + ".tsv";
        File.WriteAllLines(outputPath, rows.ToArray());
        Debug.Log("PHASE1_FOLDER_WINDOW_GATE " + outputPath);
    }

    private static void ProbeFolderCreate(
        string projectWindowState,
        ZepControllerHost host,
        List<string> rows)
    {
        var folderName = "Phase1WindowGate" + projectWindowState
            + DateTime.UtcNow.Ticks;
        ZepPerfLog.IsEnabled = true;
        ZepPerfLog.Reset();
        var succeeded = host.Core.CreateFolder(
            ZepControllerHost.SharedViewModel.PrimaryPane,
            AssetPath.From("Assets"),
            folderName);
        var snapshot = ZepPerfLog.CaptureSnapshot();
        var createCalls = snapshot.GetCount(
            ZepPerfCounter.AssetDatabaseCreateFolderCalls);
        var syncCalls = snapshot.GetCount(
            ZepPerfCounter.FullSynchronizerRuns);
        var rebuildCalls = snapshot.GetCount(
            ZepPerfCounter.RebuildProjectionCacheCalls);
        var publications = snapshot.GetCount(
            ZepPerfCounter.ProjectionPublications);
        var passed = succeeded
            && createCalls == 1
            && syncCalls == 0
            && rebuildCalls == 0
            && publications == 1;
        rows.Add(Application.unityVersion + "\t" + projectWindowState + "\t"
            + passed + "\t" + createCalls + "\t" + syncCalls + "\t"
            + rebuildCalls + "\t" + publications);
        ZepPerfLog.IsEnabled = false;
        AssetDatabase.DeleteAsset("Assets/" + folderName);
    }

    private static void CloseProjectBrowsers(Type projectBrowserType)
    {
        if (projectBrowserType == null)
        {
            return;
        }

        var windows = Resources.FindObjectsOfTypeAll(projectBrowserType);
        for (var i = 0; i < windows.Length; i++)
        {
            if (windows[i] is EditorWindow window)
            {
                window.Close();
            }
        }
    }

    private static void ProbePackage(
        string packageRoot,
        string packageKind,
        bool autoRefresh,
        List<string> rows)
    {
        var suffix = autoRefresh ? "on" : "off";
        var probeRoot = packageRoot + "/Phase1Probe" + suffix;
        AssetDatabase.DeleteAsset(probeRoot);
        var gateway = new UnityProjectAssetGateway();
        Record(rows, packageKind, suffix, "writable-policy",
            gateway.CanWrite(AssetPath.From(packageRoot)), string.Empty);

        Phase1PackageMatrixPostprocessor.Reset();
        var createdGuid = AssetDatabase.CreateFolder(packageRoot, "Phase1Probe" + suffix);
        var createFolderPassed = !string.IsNullOrEmpty(createdGuid)
            && AssetDatabase.IsValidFolder(probeRoot);
        Record(rows, packageKind, suffix, "CreateFolder",
            createFolderPassed, createdGuid);
        if (!createFolderPassed)
        {
            return;
        }

        var nativePath = probeRoot + "/Native.anim";
        AssetDatabase.CreateAsset(new AnimationClip(), nativePath);
        Record(rows, packageKind, suffix, "CreateAsset",
            AssetDatabase.LoadAssetAtPath<AnimationClip>(nativePath) != null,
            AssetDatabase.AssetPathToGUID(nativePath));

        var textPath = probeRoot + "/CaseName.txt";
        File.WriteAllText(ProjectPaths.ToAbsolutePath(textPath), "package matrix");
        AssetDatabase.ImportAsset(textPath, ImportAssetOptions.Default);
        Record(rows, packageKind, suffix, "ImportAsset",
            !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(textPath)),
            textPath);

        var casePath = probeRoot + "/casename.txt";
        var caseError = AssetDatabase.MoveAsset(textPath, casePath);
        Record(rows, packageKind, suffix, "CaseOnlyRename",
            string.IsNullOrEmpty(caseError)
                && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(casePath)),
            caseError);

        var destinationGuid = AssetDatabase.CreateFolder(probeRoot, "Destination");
        var movedPath = probeRoot + "/Destination/casename.txt";
        var moveError = AssetDatabase.MoveAsset(casePath, movedPath);
        Record(rows, packageKind, suffix, "MoveAsset",
            !string.IsNullOrEmpty(destinationGuid)
                && string.IsNullOrEmpty(moveError)
                && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(movedPath)),
            moveError);

        var copyPath = probeRoot + "/NativeCopy.anim";
        Record(rows, packageKind, suffix, "CopyAsset",
            AssetDatabase.CopyAsset(nativePath, copyPath)
                && AssetDatabase.LoadAssetAtPath<AnimationClip>(copyPath) != null,
            copyPath);
        Record(rows, packageKind, suffix, "DeleteAsset",
            AssetDatabase.DeleteAsset(copyPath)
                && AssetDatabase.LoadMainAssetAtPath(copyPath) == null,
            copyPath);

        var externalSource = Path.Combine(
            Path.GetTempPath(),
            "Phase1PackageImport-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(externalSource, "external import");
        try
        {
            var importResult = ExternalAssetImporter.Import(
                new[] { externalSource },
                probeRoot);
            Record(rows, packageKind, suffix, "ExternalTargetedImport",
                importResult.Failures.Count == 0
                    && importResult.ImportedAssetPaths.Count == 1
                    && !string.IsNullOrEmpty(
                        AssetDatabase.AssetPathToGUID(importResult.ImportedAssetPaths[0])),
                importResult.Failures.Count == 0
                    ? string.Empty
                    : importResult.Failures[0].reason);
        }
        finally
        {
            if (File.Exists(externalSource))
            {
                File.Delete(externalSource);
            }
        }

        var commands = new ProjectFileCommandService(gateway);
        var undoFolderName = "UndoFolder" + suffix;
        var undoFolderPath = packageRoot + "/" + undoFolderName;
        AssetDatabase.DeleteAsset(undoFolderPath);
        var commandResult = commands.CreateFolder(
            AssetPath.From(packageRoot),
            undoFolderName);
        var undoResult = commands.Undo();
        var redoResult = commands.Redo();
        Record(rows, packageKind, suffix, "UndoRedo",
            commandResult.Succeeded
                && undoResult.Succeeded
                && redoResult.Succeeded
                && AssetDatabase.IsValidFolder(undoFolderPath),
            commandResult.ErrorMessage + "|" + undoResult.ErrorMessage
                + "|" + redoResult.ErrorMessage);

        Record(rows, packageKind, suffix, "PostprocessorCallback",
            Phase1PackageMatrixPostprocessor.CallbackCount > 0,
            Phase1PackageMatrixPostprocessor.CallbackCount.ToString());
        AssetDatabase.DeleteAsset(probeRoot);
        AssetDatabase.DeleteAsset(undoFolderPath);
    }

    private static void SetAutoRefresh(bool enabled)
    {
        var method = typeof(AssetDatabase).GetMethod(
            enabled ? "AllowAutoRefresh" : "DisallowAutoRefresh",
            BindingFlags.Public | BindingFlags.Static);
        method?.Invoke(null, null);
    }

    private static void Record(
        List<string> rows,
        string packageKind,
        string autoRefresh,
        string api,
        bool passed,
        string detail)
    {
        rows.Add(Application.unityVersion + "\t" + packageKind + "\t"
            + autoRefresh + "\t" + api + "\t" + passed + "\t"
            + (detail ?? string.Empty).Replace('\t', ' ').Replace('\n', ' '));
    }
}

internal sealed class Phase1PackageMatrixPostprocessor : AssetPostprocessor
{
    internal static int CallbackCount;

    internal static void Reset()
    {
        CallbackCount = 0;
    }

    private static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        CallbackCount++;
    }
}
