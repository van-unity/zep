using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.UI;
using UnityEditor;
using UnityEngine;

// Copy this file into Assets/Editor in the project being measured, then run:
//
//   Unity -batchmode -projectPath "$PROJECT" \
//     -executeMethod FolderNavigationParityProbe.Run \
//     -logFile /tmp/zep-folder-navigation.log -forgetProjectPath
//
// Required environment variables:
//   ZEP_NAV_FOLDER_A / ZEP_NAV_FOLDER_B — two imported project folders to alternate.
// Optional:
//   ZEP_NAV_RESULT — TSV destination (default: /tmp/zep-folder-navigation.tsv).
//
// The probe opens and initializes both real windows, warms both paths, then
// alternates 51 synchronous folder switches through ZepPaneState and
// ProjectBrowser.ShowFolderContents. ZepPerfLog stays disabled during
// samples so structured diagnostic logging cannot inflate only the custom path.
internal static class FolderNavigationParityProbe
{
    private const int Warmups = 12;
    private const int Samples = 51;
    private const double MaximumP95GapMilliseconds = 2d;
    private static readonly string ResultPath =
        Environment.GetEnvironmentVariable("ZEP_NAV_RESULT")
        ?? "/tmp/zep-folder-navigation.tsv";
    private static EditorWindow _browser;
    private static MethodInfo _showFolder;
    private static ZepPaneState _pane;
    private static string _folderA;
    private static string _folderB;
    private static int _updates;

    public static void Run()
    {
        try
        {
            _folderA = RequireImportedFolder("ZEP_NAV_FOLDER_A");
            _folderB = RequireImportedFolder("ZEP_NAV_FOLDER_B");
            if (string.Equals(_folderA, _folderB, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("ZEP_NAV_FOLDER_A and ZEP_NAV_FOLDER_B must differ.");
            }

            var projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser, UnityEditor")
                ?? throw new TypeLoadException("UnityEditor.ProjectBrowser was not found.");
            _browser = EditorWindow.GetWindow(projectBrowserType, false, "Project", false);
            _showFolder = ResolveShowFolderContents(projectBrowserType);
            var panel = ZepWindow.Open();
            var host = ZepControllerHost.Shared;
            host.Core.Refresh();
            _pane = ZepControllerHost.SharedViewModel.PrimaryPane;
            _browser.Show();
            panel.Repaint();
            _browser.Repaint();
            EditorApplication.update += WaitForWindowInitialization;
        }
        catch (Exception exception)
        {
            FinishFailure(exception);
        }
    }

    private static void WaitForWindowInitialization()
    {
        if (++_updates < 12)
        {
            return;
        }

        EditorApplication.update -= WaitForWindowInitialization;
        try
        {
            Measure();
        }
        catch (Exception exception)
        {
            FinishFailure(exception);
        }
    }

    private static void Measure()
    {
        var folderAId = AssetDatabase.LoadMainAssetAtPath(_folderA).GetInstanceID();
        var folderBId = AssetDatabase.LoadMainAssetAtPath(_folderB).GetInstanceID();
        for (var i = 0; i < Warmups; i++)
        {
            _pane.TrySetFolderSelection(AssetPath.From((i & 1) == 0 ? _folderA : _folderB));
            InvokeShowFolder(_showFolder, _browser, (i & 1) == 0 ? folderAId : folderBId);
        }

        var panelSamples = new List<double>(Samples);
        var builtInSamples = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var panelFolder = (i & 1) == 0 ? _folderA : _folderB;
            var builtInFolderId = (i & 1) == 0 ? folderBId : folderAId;

            var stopwatch = Stopwatch.StartNew();
            _pane.TrySetFolderSelection(AssetPath.From(panelFolder));
            stopwatch.Stop();
            panelSamples.Add(stopwatch.Elapsed.TotalMilliseconds);

            stopwatch.Restart();
            InvokeShowFolder(_showFolder, _browser, builtInFolderId);
            stopwatch.Stop();
            builtInSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        panelSamples.Sort();
        builtInSamples.Sort();
        var panelP50 = Percentile(panelSamples, 0.50);
        var panelP95 = Percentile(panelSamples, 0.95);
        var builtInP50 = Percentile(builtInSamples, 0.50);
        var builtInP95 = Percentile(builtInSamples, 0.95);
        var p95Gap = panelP95 - builtInP95;
        var passed = p95Gap <= MaximumP95GapMilliseconds;
        var rows = new[]
        {
            "unity\t" + Application.unityVersion,
            "folderA\t" + _folderA,
            "folderB\t" + _folderB,
            "samples\t" + Samples,
            "panelP50Ms\t" + panelP50.ToString("F3"),
            "panelP95Ms\t" + panelP95.ToString("F3"),
            "builtinP50Ms\t" + builtInP50.ToString("F3"),
            "builtinP95Ms\t" + builtInP95.ToString("F3"),
            "p50GapMs\t" + (panelP50 - builtInP50).ToString("F3"),
            "p95GapMs\t" + p95Gap.ToString("F3"),
            "maximumP95GapMs\t" + MaximumP95GapMilliseconds.ToString("F3"),
            "result\t" + (passed ? "PASS" : "FAIL"),
        };
        File.WriteAllLines(ResultPath, rows);
        UnityEngine.Debug.Log(
            "FOLDER_NAVIGATION_PARITY_FINISHED\n" + string.Join("\n", rows));
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private static string RequireImportedFolder(string environmentVariable)
    {
        var path = AssetPath.From(Environment.GetEnvironmentVariable(environmentVariable)).Value;
        if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
        {
            throw new ArgumentException(
                environmentVariable + " must name an imported Assets/Packages folder.");
        }

        return path;
    }

    private static MethodInfo ResolveShowFolderContents(Type type)
    {
        var method = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (candidate.Name != "ShowFolderContents")
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length >= 1
                    && (parameters[0].ParameterType == typeof(int)
                        || parameters[0].ParameterType.Name == "EntityId");
            });
        return method
            ?? throw new MissingMethodException(type.FullName, "ShowFolderContents");
    }

    private static void InvokeShowFolder(
        MethodInfo method,
        EditorWindow browser,
        int instanceId)
    {
        var parameters = method.GetParameters();
        var arguments = new object[parameters.Length];
        var instanceIdType = parameters[0].ParameterType;
        if (instanceIdType == typeof(int))
        {
            arguments[0] = instanceId;
        }
        else
        {
            var conversion = instanceIdType.GetMethods(
                    BindingFlags.Static | BindingFlags.Public)
                .Single(candidate => candidate.Name == "op_Implicit"
                    && candidate.ReturnType == instanceIdType
                    && candidate.GetParameters().Length == 1
                    && candidate.GetParameters()[0].ParameterType == typeof(int));
            arguments[0] = conversion.Invoke(null, new object[] { instanceId });
        }

        for (var i = 1; i < parameters.Length; i++)
        {
            arguments[i] = parameters[i].ParameterType == typeof(bool)
                ? (object)false
                : parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : null;
        }

        method.Invoke(browser, arguments);
    }

    private static double Percentile(
        IReadOnlyList<double> sorted,
        double percentile)
    {
        var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
    }

    private static void FinishFailure(Exception exception)
    {
        EditorApplication.update -= WaitForWindowInitialization;
        File.WriteAllText(ResultPath, exception.ToString());
        UnityEngine.Debug.LogException(exception);
        EditorApplication.Exit(1);
    }
}
