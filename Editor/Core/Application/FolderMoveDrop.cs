using System;
using System.Collections.Generic;
using System.Text;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;

namespace Zep.Core.Application
{
    /// <summary>
    /// What a drop of project items onto a folder does: which of the dragged
    /// paths can move, what to ask when names collide, and the one status line
    /// that reports the whole drop. The same no-op rules decide whether a folder
    /// highlights during the drag.
    /// </summary>
    public sealed class FolderMoveDrop
    {
        private readonly ZepController _commandHandler;
        private readonly ZepState _viewModel;
        private readonly Func<string, bool> _isValidFolder;
        private readonly Action<string> _showError;
        private readonly Func<string, string, ProjectMoveConflictResolution> _askMoveConflict;

        /// <param name="isValidFolder">Whether the editor knows the path as a folder; asked for paths the index cannot answer.</param>
        /// <param name="showError">Optional; receives each refusal the user has to act on.</param>
        /// <param name="askMoveConflict">Receives a title and message when names collide and returns the one decision for the whole drop.</param>
        public FolderMoveDrop(
            ZepController commandHandler,
            ZepState viewModel,
            Func<string, bool> isValidFolder,
            Action<string> showError,
            Func<string, string, ProjectMoveConflictResolution> askMoveConflict)
        {
            _commandHandler = commandHandler;
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _isValidFolder = isValidFolder ?? throw new ArgumentNullException(nameof(isValidFolder));
            _showError = showError;
            _askMoveConflict = askMoveConflict ?? throw new ArgumentNullException(nameof(askMoveConflict));
        }

        private bool IsFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            return _commandHandler != null && _commandHandler.TryGetIndexedPathKind(assetPath, out var isFolder)
                ? isFolder
                : _isValidFolder(assetPath);
        }

        public void MoveDroppedPathsToFolder(
            ZepPaneState pane,
            string targetFolderPath,
            IReadOnlyList<string> projectPaths)
        {
            if (!_viewModel.IsLivePane(pane))
            {
                return;
            }

            var dest = AssetPathFormat.Normalize(targetFolderPath);
            if (!IsValidMoveDestination(dest))
            {
                // The hover promised Move and the drop was accepted, so a
                // read-only destination has to say so — silence reads as a
                // broken panel, which is exactly the bug HandleSidebarFolderDrop
                // already fixed on the mode axis. A missing or non-folder
                // destination stays silent: the drop never named a place to
                // write, so there is nothing to tell the user to act on.
                if (!string.IsNullOrEmpty(dest)
                    && _commandHandler != null
                    && !_commandHandler.CanWrite(AssetPath.From(dest)))
                {
                    _showError?.Invoke("Can't move into '" + dest + "' — it is read-only.");
                }

                return;
            }

            var movable = FilterMovablePaths(projectPaths, dest);
            if (movable.Count > 0)
            {
                // A partly movable set moves what it can and reports through
                // ReportMoveOutcome; adding a refusal on top would say two
                // things about one drop.
                MoveSelectedPaths(pane, movable, dest);
                return;
            }

            ReportUnwritableSources(projectPaths);
        }

        // FilterMovablePaths empties for two very different reasons and only one
        // of them is a refusal: the no-op rules (onto its own parent, onto
        // itself, into its own subtree) were already refused by the hover, so
        // repeating them here would nag, while a read-only source was accepted
        // and then dropped on the floor. Re-asking the gateway is cheap because
        // this runs only on the branch where nothing moved.
        private void ReportUnwritableSources(IReadOnlyList<string> projectPaths)
        {
            if (_showError == null || _commandHandler == null || projectPaths == null)
            {
                return;
            }

            var refusedName = string.Empty;
            var refused = 0;
            for (var i = 0; i < projectPaths.Count; i++)
            {
                var sourcePath = projectPaths[i];
                if (string.IsNullOrEmpty(sourcePath)
                    || _commandHandler.CanWrite(AssetPath.From(sourcePath)))
                {
                    continue;
                }

                refused++;
                if (refused == 1)
                {
                    refusedName = AssetPathFormat.GetFileName(sourcePath);
                }
            }

            if (refused == 0)
            {
                return;
            }

            _showError(refused == 1
                ? "Can't move '" + refusedName + "' — it is read-only."
                : "Can't move '" + refusedName + "' and " + (refused - 1)
                  + " more — they are read-only.");
        }

        private void MoveSelectedPaths(
            ZepPaneState pane,
            IReadOnlyList<string> selectedPaths,
            string destinationFolderPath)
        {
            if (_commandHandler == null
                || selectedPaths == null
                || !_viewModel.IsLivePane(pane))
            {
                return;
            }

            var destination = AssetPath.From(destinationFolderPath);
            var sources = new AssetPath[selectedPaths.Count];
            for (var i = 0; i < sources.Length; i++)
            {
                sources[i] = AssetPath.From(selectedPaths[i]);
            }

            // Ask BEFORE moving anything. A name collision used to reach
            // AssetDatabase.MoveAsset, fail there, and leave the user with
            // Unity's raw error for 1.9s — which the status bar's
            // identical-message dedupe then swallowed on every retry, so the
            // drop looked like it did nothing. One dialog covers the whole
            // drop: what to do about a name clash is one decision, not one per
            // item.
            var collisions = _commandHandler.FindMoveCollisions(sources, destination);
            var resolution = ProjectMoveConflictResolution.KeepBoth;
            if (collisions.Count > 0)
            {
                resolution = _askMoveConflict(
                    MoveConflictTitle,
                    BuildMoveConflictMessage(collisions, destinationFolderPath));
            }

            var succeeded = 0;
            var failed = 0;
            var firstFailureReason = string.Empty;
            _commandHandler.RunBulk(() =>
            {
                for (var i = 0; i < sources.Length; i++)
                {
                    // Continue, don't break: one item failing (a read-only
                    // source) must not silently abandon the rest of the drop. Each move is
                    // independent, so move everything that can move. An item with
                    // nothing in its way moves normally whatever the resolution is.
                    if (_commandHandler.MoveWithConflictResolution(
                        pane, sources[i], destination, resolution))
                    {
                        succeeded++;
                        continue;
                    }

                    failed++;
                    if (failed == 1)
                    {
                        firstFailureReason = _commandHandler.StatusMessage;
                    }

                    // The status bar drops a message identical to the one it is
                    // already showing, so a second attempt at the same failure
                    // said nothing at all. The toast is the channel that still
                    // reacts to a repeat.
                    _showError?.Invoke(_commandHandler.StatusMessage);
                }
            });

            ReportMoveOutcome(
                succeeded,
                failed,
                selectedPaths.Count,
                collisions,
                resolution,
                destinationFolderPath,
                firstFailureReason);
        }

        // One line for the whole drop. Each item's own status overwrote the
        // previous one, so a partly failed drop showed whichever message
        // happened to come last — often a success line hiding several failures.
        private void ReportMoveOutcome(
            int succeeded,
            int failed,
            int total,
            IReadOnlyList<AssetPath> collisions,
            ProjectMoveConflictResolution resolution,
            string destinationFolderPath,
            string firstFailureReason)
        {
            // Skip reports success with nothing done — it is the user declining
            // an item — so the moves are the successes minus the collisions
            // that were declined. One resolution covers them all, so the count
            // comes straight from the collision list.
            var skipped = resolution == ProjectMoveConflictResolution.Skip ? collisions.Count : 0;
            var moved = succeeded - skipped;

            // Cancelling a one-item drop would otherwise be exactly the silence
            // this change is about: nothing moved, nothing failed, nothing said.
            if (total == 1 && skipped == 1)
            {
                _commandHandler.ShowStatusMessage(
                    "Skipped '" + AssetPathFormat.GetFileName(collisions[0])
                    + "' — already exists in " + destinationFolderPath);
                return;
            }

            // A real failure outranks the conflict outcome: its reason is the
            // part the user has to act on. A single-item drop keeps its plain
            // reason — there is nothing to aggregate and the bare message reads
            // better.
            if (failed > 0)
            {
                if (total > 1)
                {
                    _commandHandler.ShowStatusMessage(
                        $"Moved {moved} of {total} — {failed} failed; first: {firstFailureReason}");
                }

                return;
            }

            // A single item that resolved cleanly keeps the move's own status.
            if (collisions.Count == 0 || total == 1)
            {
                return;
            }

            _commandHandler.ShowStatusMessage(
                $"Moved {moved} of {total} — {collisions.Count} {DescribeResolution(resolution)}");
        }

        private static string DescribeResolution(ProjectMoveConflictResolution resolution)
        {
            switch (resolution)
            {
                case ProjectMoveConflictResolution.Replace:
                    return "replaced";
                case ProjectMoveConflictResolution.Skip:
                    return "skipped";
                default:
                    return "kept as copies";
            }
        }

        public const string MoveConflictTitle = "Move Conflict";

        // Enough names to recognise the drop without turning the dialog into a
        // list view; the rest are counted.
        private const int MoveConflictNamesShown = 5;

        // Names, not paths: every colliding item is in the same destination
        // folder and its own folder is not what the user has to weigh up.
        internal static string BuildMoveConflictMessage(
            IReadOnlyList<AssetPath> collisions,
            string destinationFolderPath)
        {
            var message = new StringBuilder();
            if (collisions.Count == 1)
            {
                message.Append('\'')
                    .Append(AssetPathFormat.GetFileName(collisions[0]))
                    .Append("' already exists in ")
                    .Append(destinationFolderPath)
                    .Append('.');
            }
            else
            {
                message.Append(collisions.Count)
                    .Append(" items already exist in ")
                    .Append(destinationFolderPath)
                    .Append(':');
                var shown = Math.Min(collisions.Count, MoveConflictNamesShown);
                for (var i = 0; i < shown; i++)
                {
                    message.Append('\n').Append(AssetPathFormat.GetFileName(collisions[i]));
                }

                if (collisions.Count > shown)
                {
                    message.Append("\n…and ").Append(collisions.Count - shown).Append(" more");
                }
            }

            return message
                .Append("\n\nReplace moves the existing item")
                .Append(collisions.Count == 1 ? string.Empty : "s")
                .Append(" aside; you can undo it.")
                .ToString();
        }

        private bool IsValidMoveDestination(string normalizedFolderPath)
        {
            return !string.IsNullOrEmpty(normalizedFolderPath)
                && _commandHandler != null
                && _commandHandler.CanWrite(AssetPath.From(normalizedFolderPath))
                && _isValidFolder(normalizedFolderPath);
        }

        // IsMeaningfulMoveTarget mirrors the per-item no-op rules below, and the
        // test that guards that mirror runs this real filter rather than a second
        // copy of the rules.
        internal List<string> FilterMovablePaths(IReadOnlyList<string> paths, string destinationFolderPath)
        {
            if (paths == null || paths.Count == 0 || _commandHandler == null)
            {
                return new List<string>();
            }

            var movable = new List<string>(paths.Count);
            // Folder-ness is already read once per candidate below; remembering it here
            // lets the nesting collapse probe only the paths that can actually contain
            // something, without asking AssetDatabase a second time.
            var movableFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < paths.Count; i++)
            {
                var sourcePath = paths[i];
                if (!_commandHandler.CanWrite(AssetPath.From(sourcePath)))
                {
                    continue;
                }

                if (string.Equals(sourcePath, destinationFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourceParentPath = AssetPathFormat.GetParentFolderPath(sourcePath);
                if (string.Equals(sourceParentPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourceIsFolder = IsFolder(sourcePath);
                if (sourceIsFolder && IsPathSameOrChild(destinationFolderPath, sourcePath))
                {
                    continue;
                }

                movable.Add(sourcePath);
                if (sourceIsFolder)
                {
                    movableFolders.Add(sourcePath);
                }
            }

            return ZepPathHelpers.CollapseNestedPaths(movable, movableFolders.Contains);
        }

        // Drag-over drop-target highlighting: should this folder light up for
        // the dragged set? Mirrors FilterMovablePaths' per-item no-op rules —
        // an item dropped on itself, on its current parent, or a folder
        // dropped into its own subtree moves nothing — so a folder highlights
        // only when at least one dragged path would actually move into it.
        public static bool IsMeaningfulMoveTarget(string folderPath, IReadOnlyList<string> draggedPaths)
        {
            if (string.IsNullOrEmpty(folderPath) || draggedPaths == null)
            {
                return false;
            }

            for (var i = 0; i < draggedPaths.Count; i++)
            {
                var sourcePath = draggedPaths[i];
                if (string.IsNullOrEmpty(sourcePath)
                    || string.Equals(sourcePath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(
                    AssetPathFormat.GetParentFolderPath(sourcePath),
                    folderPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsPathSameOrChild(folderPath, sourcePath))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        internal static bool IsPathSameOrChild(string path, string parentPath)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parentPath))
            {
                return false;
            }

            if (string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return path.StartsWith(parentPath + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
