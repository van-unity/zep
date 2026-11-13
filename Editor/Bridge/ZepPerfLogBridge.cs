using Zep.Core.Application;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge
{
    // Wires ZepPerfLog to Unity at editor startup: routes the sink to
    // Debug.Log and seeds IsEnabled from an EditorPrefs-backed menu toggle so
    // the logs only appear when a developer opts in.
    [InitializeOnLoad]
    internal static class ZepPerfLogBridge
    {
        private const string PerfLoggingPrefKey = "Zep.PerfLoggingToConsole";
        private const string PerfLoggingMenuPath = "Tools/Zep/Perf Logging";

        static ZepPerfLogBridge()
        {
            ZepPerfLog.IsEnabled = EditorPrefs.GetBool(PerfLoggingPrefKey, false);
            ZepPerfLog.Sink = Debug.Log;
        }

        [MenuItem(PerfLoggingMenuPath)]
        private static void Toggle()
        {
            var next = !EditorPrefs.GetBool(PerfLoggingPrefKey, false);
            EditorPrefs.SetBool(PerfLoggingPrefKey, next);
            ZepPerfLog.IsEnabled = next;
            Debug.Log($"[Zep] perf logging: {(next ? "ON" : "OFF")}");
        }

        [MenuItem(PerfLoggingMenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(PerfLoggingMenuPath, EditorPrefs.GetBool(PerfLoggingPrefKey, false));
            return true;
        }
    }
}
