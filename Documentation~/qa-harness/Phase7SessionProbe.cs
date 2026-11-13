using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.UI;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

internal static class Phase7SessionProbe
{
    private const string FixtureRoot = "Assets/Phase01Fixture";
    private const string ResultPath =
        "/private/tmp/pp-perf-phase01-results/phase7-session-30m.tsv";
    private const double InteractionSeconds = 1d;
    private const int GcSampleCapacity = 4096;
    private static readonly double SessionSeconds =
        ReadPositiveDouble("ZEP_SESSION_SECONDS", 30d * 60d);
    private static readonly double SampleSeconds =
        Math.Min(60d, SessionSeconds / 30d);

    private static readonly List<string> Rows = new List<string>();
    private static readonly List<ProfilerRecorderSample> FrameGcSamples =
        new List<ProfilerRecorderSample>(GcSampleCapacity);
    private static ZepWindow _window;
    private static ZepPaneState _pane;
    private static ProfilerRecorder _frameGc;
    private static double _startedAt;
    private static double _nextSampleAt;
    private static double _nextInteractionAt;
    private static long _phaseEditorUpdates;
    private static int _phaseGc0Start;
    private static int _phaseGc1Start;
    private static int _phaseGc2Start;
    private static string _phase = string.Empty;
    private static int _interaction;

    public static void Run()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
        ClosePanelWindows();
        _window = ZepWindow.Open();
        ZepControllerHost.Shared.Core.Refresh();
        _pane = ZepControllerHost.SharedViewModel.PrimaryPane;
        _window.Repaint();

        Rows.Add("environment\tunity\t" + Application.unityVersion);
        Rows.Add("environment\tmachine\t" + SystemInfo.deviceModel);
        Rows.Add("environment\tprocessor\t" + SystemInfo.processorType);
        Rows.Add("environment\tmemoryMB\t" + SystemInfo.systemMemorySize);
        Rows.Add("environment\tos\t" + SystemInfo.operatingSystem);
        Rows.Add("environment\tfixtureAssets\t"
            + AssetDatabase.FindAssets(string.Empty, new[] { FixtureRoot }).Length);
        Rows.Add("columns\tminute\tphase\ttotalAllocatedBytes\ttotalReservedBytes"
            + "\tunusedReservedBytes\tmonoUsedBytes\tmonoHeapBytes\tmanagedGcBytes"
            + "\teditorUpdatesSincePhase\tgcSamplesInWindow\tgcBytesInWindow"
            + "\tgcPeakFrameBytes\tgc0SincePhase\tgc1SincePhase\tgc2SincePhase"
            + "\twindowCount");

        _frameGc = ProfilerRecorder.StartNew(
            ProfilerCategory.Memory,
            "GC Allocated In Frame",
            GcSampleCapacity);
        _startedAt = EditorApplication.timeSinceStartup;
        _nextSampleAt = _startedAt;
        _nextInteractionAt = _startedAt;
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        try
        {
            var now = EditorApplication.timeSinceStartup;
            var elapsed = now - _startedAt;
            var phase = ResolvePhase(elapsed);
            if (!string.Equals(_phase, phase, StringComparison.Ordinal))
            {
                _phase = phase;
                _phaseEditorUpdates = 0L;
                _phaseGc0Start = GC.CollectionCount(0);
                _phaseGc1Start = GC.CollectionCount(1);
                _phaseGc2Start = GC.CollectionCount(2);
                DiscardGcSamples();
                if (phase == "idle-closed")
                {
                    ClosePanelWindows();
                    _window = null;
                }
            }

            _phaseEditorUpdates++;

            if (phase == "interactive" && now >= _nextInteractionAt)
            {
                ExercisePanel();
                _nextInteractionAt = now + InteractionSeconds;
            }

            if (now >= _nextSampleAt)
            {
                RecordSample(elapsed);
                _nextSampleAt += SampleSeconds;
            }

            if (elapsed >= SessionSeconds)
            {
                Finish(0, null);
            }
        }
        catch (Exception exception)
        {
            Finish(1, exception);
        }
    }

    private static string ResolvePhase(double elapsedSeconds)
    {
        if (elapsedSeconds < SessionSeconds / 6d)
        {
            return "idle-open-before";
        }

        if (elapsedSeconds < SessionSeconds * 2d / 3d)
        {
            return "interactive";
        }

        if (elapsedSeconds < SessionSeconds * 5d / 6d)
        {
            return "idle-open-after";
        }

        return "idle-closed";
    }

    private static void ExercisePanel()
    {
        if (_window == null || _pane == null)
        {
            return;
        }

        var folder = FixtureRoot + "/F" + (_interaction % 20).ToString("D5");
        _pane.TrySetFolderSelection(AssetPath.From(folder));
        _pane.SetSearchText((_interaction & 1) == 0 ? "asset0000" : string.Empty);
        _window.Repaint();
        _interaction++;
    }

    private static void RecordSample(double elapsedSeconds)
    {
        FrameGcSamples.Clear();
        if (_frameGc.Valid)
        {
            _frameGc.CopyTo(FrameGcSamples, true);
        }

        long gcBytes = 0L;
        long gcPeakFrameBytes = 0L;
        for (var i = 0; i < FrameGcSamples.Count; i++)
        {
            var value = Math.Max(0L, FrameGcSamples[i].Value);
            gcBytes += value;
            gcPeakFrameBytes = Math.Max(gcPeakFrameBytes, value);
        }

        Rows.Add(
            "sample\t" + Math.Floor(elapsedSeconds / 60d).ToString("F0")
            + "\t" + _phase
            + "\t" + Profiler.GetTotalAllocatedMemoryLong()
            + "\t" + Profiler.GetTotalReservedMemoryLong()
            + "\t" + Profiler.GetTotalUnusedReservedMemoryLong()
            + "\t" + Profiler.GetMonoUsedSizeLong()
            + "\t" + Profiler.GetMonoHeapSizeLong()
            + "\t" + GC.GetTotalMemory(false)
            + "\t" + _phaseEditorUpdates
            + "\t" + FrameGcSamples.Count
            + "\t" + gcBytes
            + "\t" + gcPeakFrameBytes
            + "\t" + (GC.CollectionCount(0) - _phaseGc0Start)
            + "\t" + (GC.CollectionCount(1) - _phaseGc1Start)
            + "\t" + (GC.CollectionCount(2) - _phaseGc2Start)
            + "\t" + Resources.FindObjectsOfTypeAll<ZepWindow>().Length);
        File.WriteAllLines(ResultPath, Rows);
    }

    private static void DiscardGcSamples()
    {
        FrameGcSamples.Clear();
        if (_frameGc.Valid)
        {
            _frameGc.CopyTo(FrameGcSamples, true);
            FrameGcSamples.Clear();
        }
    }

    private static void Finish(int exitCode, Exception exception)
    {
        EditorApplication.update -= Tick;
        _frameGc.Dispose();
        if (exception != null)
        {
            Rows.Add("failure\t" + exception);
        }

        File.WriteAllLines(ResultPath, Rows);
        Debug.Log("PHASE7_SESSION_FINISHED exit=" + exitCode + " rows=" + Rows.Count);
        EditorApplication.Exit(exitCode);
    }

    private static void ClosePanelWindows()
    {
        foreach (var window in Resources.FindObjectsOfTypeAll<ZepWindow>())
        {
            window.Close();
        }
    }

    private static double ReadPositiveDouble(string name, double fallback)
    {
        return double.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            && value > 0d
                ? value
                : fallback;
    }
}
