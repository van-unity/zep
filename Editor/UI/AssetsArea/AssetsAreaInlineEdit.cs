using System;
using Zep.Core.Domain;

namespace Zep.Editor.UI.AssetsArea
{
    // Request payload describing a single inline-edit session (ghost create OR rename).
    // Lives on AssetsBrowserFeature while active; child widgets (List/Grid) consume
    // a lightweight descriptor to know which row is currently editing.
    internal sealed class AssetsAreaInlineEditRequest
    {
        // Sentinel path used for a ghost (create) entry that does not yet exist on disk.
        // The asset-area code prepends a ZepAssetEntry with this Path to the
        // rendered list so the inline TextField has a row to live in.
        internal const string GhostPath = "__zep-inline-ghost__";

        // For rename: the real asset path. For ghost create: GhostPath.
        public string Path { get; set; }

        // Basename shown in the TextField on open (no extension).
        public string InitialBasename { get; set; }

        // Extension with leading dot, or empty for folders. Displayed next to the TextField.
        public string Extension { get; set; }

        // Used by the ghost row renderer to look up an icon. For rename rows we use the
        // existing entry's icon resolution path.
        public string TypeName { get; set; }

        // Optional folder-tint color for the ghost (folder creation).
        public ZepTagColor FolderTintColor { get; set; }

        // Treat as "new-create" (true) vs "rename existing" (false). Drives Escape semantics
        // and how the asset-area widget prepends the ghost row.
        public bool IsGhost { get; set; }

        // Invoked when the user commits the edit (Enter, or focus-out with non-empty value).
        // Argument is the user's final basename (no extension). If empty/whitespace, callers
        // should fall back to InitialBasename.
        public Action<string> OnCommit { get; set; }

        // Invoked on Escape (or when the active edit is cleared without a commit).
        public Action OnCancel { get; set; }
    }

    // Lightweight descriptor pushed down to List/Grid widgets so they know which row is
    // currently in inline-edit mode and how to render it.
    internal readonly struct AssetsAreaInlineEditDescriptor
    {
        internal AssetsAreaInlineEditDescriptor(
            string path,
            string initialBasename,
            string extension,
            bool isGhost)
        {
            Path = path ?? string.Empty;
            InitialBasename = initialBasename ?? string.Empty;
            Extension = extension ?? string.Empty;
            IsGhost = isGhost;
        }

        public string Path { get; }
        public string InitialBasename { get; }
        public string Extension { get; }
        public bool IsGhost { get; }

        public bool IsActive => !string.IsNullOrEmpty(Path);

        public static AssetsAreaInlineEditDescriptor None => default;
    }
}
