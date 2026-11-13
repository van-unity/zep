using System;
using Zep.Core.State;
using UnityEditor;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    internal sealed class ZepSplitDragPayload
    {
        internal ZepSplitDragPayload(
            ZepPaneSource source,
            string displayName)
        {
            if (!source.IsAuxiliary)
            {
                throw new ArgumentException(
                    "A split drag requires an auxiliary pane source.",
                    nameof(source));
            }

            Source = source;
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? "Split View"
                : displayName.Trim();
        }

        internal ZepPaneSource Source { get; }

        internal string DisplayName { get; }
    }

    // The single Unity DragAndDrop boundary for sidebar-to-split gestures.
    // Split sources deliberately publish neither project paths nor objects, so
    // the existing asset move/import handlers cannot mistake them for assets.
    internal static class ZepSplitDragAndDrop
    {
        private const string GenericDataKey =
            "Zep.SidebarSplitDragPayload";

        internal static void Start(ZepSplitDragPayload payload, VisualElement origin)
        {
            Prepare(payload);
            if (!ZepWindowDocking.Start(payload.Source, payload.DisplayName, origin))
                DragAndDrop.StartDrag(payload.DisplayName);
        }

        internal static void Prepare(ZepSplitDragPayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.SetGenericData(GenericDataKey, payload);
        }

        internal static bool TryRead(out ZepSplitDragPayload payload)
        {
            payload = DragAndDrop.GetGenericData(GenericDataKey)
                as ZepSplitDragPayload;
            return payload != null;
        }

        internal static void Clear()
        {
            DragAndDrop.SetGenericData(GenericDataKey, null);
        }
    }
}
