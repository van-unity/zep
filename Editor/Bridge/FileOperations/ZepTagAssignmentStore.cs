using System;
using System.IO;
using Zep.Core.State;
using UnityEngine;

namespace Zep.Editor.Bridge.FileOperations
{
    // The project's one TagAssignmentStore, wired to its Unity-side inputs: the
    // ProjectSettings file path and the console for problem reports. The store's
    // rules and file format live in Core (TagAssignmentStore); nothing here
    // decides anything about assignments.
    internal static class ZepTagAssignmentStore
    {
        private static readonly Lazy<TagAssignmentStore> LazyInstance =
            new Lazy<TagAssignmentStore>(static () => new TagAssignmentStore(
                GetDefaultFilePath(),
                static message => Debug.LogWarning("[Zep] " + message)));

        public static TagAssignmentStore instance => LazyInstance.Value;

        private static string GetDefaultFilePath()
        {
            return Path.Combine(
                ProjectPaths.GetProjectRootAbsolutePath(),
                "ProjectSettings",
                "Zep.TagAssignments.json");
        }
    }
}
