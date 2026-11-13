using UnityEditor;
using UnityEngine;

namespace Zep.Editor.InternalBridge
{
    // Unity 6.3+ delivers an object ping (clicking an asset reference in the
    // Inspector, EditorGUIUtility.PingObject) only to EditorWindows implementing
    // the INTERNAL UnityEditor.IFramableContainer. No public hook exists, and an
    // ordinary assembly cannot name the interface.
    //
    // UnityEditor grants its internals to assemblies named
    // Unity.InternalAPIEditorBridge.001-.024, so this one-class assembly carries
    // that name and lets the real, docked panel window implement the interface
    // itself. The alternative — a hidden window that implements it — is never
    // docked, and Unity's maximize logs "Invalid editor window" for every
    // EditorWindow without a host view.
    //
    // .024 is the last number Unity grants; the low numbers are taken by Unity's
    // own packages (2D uses .001), and two assemblies with one name do not compile.
    // Compiled only where the interface exists (asmdef define constraint).
    public abstract class PingFramableEditorWindow : EditorWindow, IFramableContainer
    {
        void IFramableContainer.FrameObject(EntityId instanceID, bool ping)
        {
            OnFrameObject(instanceID, ping);
        }

        protected abstract void OnFrameObject(EntityId instanceId, bool ping);
    }
}
