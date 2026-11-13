#if UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.TestInfrastructure
{
    // Headless (-batchmode) test runs still create real Cocoa windows for
    // every EditorWindow a test shows, and macOS brings the Unity process to
    // the foreground each time, stealing focus from whatever the user is
    // doing. Marking the process "prohibited" (never activatable, no Dock
    // presence) keeps the windows off the user's screen while layout, repaint
    // and element focus keep working. Measured on 6000.3.10f1 / macOS 26.5:
    // the "accessory" policy is refused and still steals focus; "prohibited"
    // is accepted and the process never becomes frontmost.
    [InitializeOnLoad]
    internal static class BatchmodeFocusGuard
    {
        private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
        private const long ActivationPolicyProhibited = 2;

        [DllImport(ObjCRuntime)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjCRuntime)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendReturningObject(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static extern bool SendReturningBool(IntPtr receiver, IntPtr selector, long argument);

        static BatchmodeFocusGuard()
        {
            if (!Application.isBatchMode)
            {
                return;
            }

            try
            {
                IntPtr application = SendReturningObject(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                bool accepted = SendReturningBool(application, sel_registerName("setActivationPolicy:"), ActivationPolicyProhibited);
                if (!accepted)
                {
                    Debug.LogWarning("[BatchmodeFocusGuard] macOS refused the prohibited activation policy; test windows may steal focus.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[BatchmodeFocusGuard] Could not change the activation policy: " + exception.Message);
            }
        }
    }
}
#endif
