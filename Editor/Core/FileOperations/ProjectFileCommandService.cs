using System;
using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    public sealed class ProjectFileCommandService
    {
        private readonly IProjectAssetGateway _gateway;
        private readonly ProjectCommandHistory _history;
        private Func<bool> _prepareForExecution = () => true;
        private Func<bool> _prepareForAppliedCommand = () => true;

        public ProjectFileCommandService(IProjectAssetGateway gateway)
            : this(gateway, new ProjectCommandHistory())
        {
        }

        // Production wiring injects a persistence-backed history (Bridge owns
        // the backing lists + save callback) so the undo stack survives the
        // domain reload triggered by reimport after asset operations.
        public ProjectFileCommandService(IProjectAssetGateway gateway, ProjectCommandHistory history)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _history.SetPayloadCleanup(path => _gateway.DeleteUndoPayload(AssetPath.From(path)));
        }

        internal bool CanUndo => _history.CanUndo;

        internal bool CanRedo => _history.CanRedo;

        internal ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName)
        {
            var preparationFailure = PrepareNewCommand();
            if (preparationFailure != null) return preparationFailure;

            if (!_gateway.CanWrite(parentFolderPath))
            {
                return ProjectFileOperationResult.Failure("Folders can only be created inside Assets.");
            }

            if (string.IsNullOrWhiteSpace(folderName))
            {
                return ProjectFileOperationResult.Failure("Folder name must be provided.");
            }

            return Execute(new CreateFolderCommand(parentFolderPath, folderName));
        }

        internal ProjectFileOperationResult Rename(AssetPath assetPath, string newName)
        {
            var preparationFailure = PrepareNewCommand();
            if (preparationFailure != null) return preparationFailure;

            if (!_gateway.CanWrite(assetPath))
            {
                return ProjectFileOperationResult.Failure("This item is read-only.", assetPath);
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                return ProjectFileOperationResult.Failure("A new name must be provided.", assetPath);
            }

            var trimmedName = newName.Trim();
            if (!IsUsableFileName(trimmedName))
            {
                return ProjectFileOperationResult.Failure(NameRejectedMessage, assetPath);
            }

            // A rename is a move to a sibling name, and it had the same silent
            // hole a drop did: nothing probed the destination, so a name that
            // was already taken reached AssetDatabase.MoveAsset, failed there,
            // and left Unity's raw error in the status bar — which the bar's
            // identical-message dedupe then swallowed on every retry. Refuse it
            // here, in the same words the move conflict uses and before a
            // command is recorded, so the caller can put the reason to the user
            // and keep them in the edit (ZepCreateOrchestrator).
            //
            // A case-only rename of the item itself ("foo.mat" -> "Foo.mat") is
            // NOT a collision: IsOccupiedMoveDestination settles source ==
            // destination before it asks the gateway, so a case-insensitive
            // volume answering "yes, that file exists" (it is the same file)
            // never reaches the refusal.
            var isFolder = _gateway.IsFolder(assetPath);
            var destinationPath = AssetPath.From(
                AssetPathFormat.ChangeName(assetPath, trimmedName, isFolder));
            if (IsOccupiedMoveDestination(_gateway, assetPath, destinationPath))
            {
                return ProjectFileOperationResult.Failure(
                    "'" + AssetPathFormat.GetFileName(destinationPath) + "' already exists in "
                    + AssetPathFormat.GetParentFolder(assetPath) + ".",
                    assetPath);
            }

            // identityVerified: the GUID below is read from this same gateway
            // and handed straight to Execute, so the command's own forward
            // Validate would only re-read the .meta to compare it with itself.
            return Execute(new RenameCommand(
                assetPath,
                trimmedName,
                isFolder,
                _gateway.GetGuid(assetPath),
                identityVerified: true));
        }

        internal ProjectFileOperationResult Duplicate(AssetPath assetPath)
        {
            var preparationFailure = PrepareNewCommand();
            if (preparationFailure != null) return preparationFailure;

            if (!_gateway.CanWrite(assetPath))
            {
                return ProjectFileOperationResult.Failure("This item is read-only.", assetPath);
            }

            return Execute(new DuplicateCommand(assetPath));
        }

        // destinationFileName is the name the item takes in the destination
        // folder; empty keeps its own. Only the Keep Both resolution of a move
        // conflict passes one.
        internal ProjectFileOperationResult Move(
            AssetPath assetPath,
            AssetPath destinationFolderPath,
            string destinationFileName = null)
        {
            var preparationFailure = PrepareNewCommand();
            if (preparationFailure != null) return preparationFailure;

            if (!_gateway.CanWrite(assetPath) || !_gateway.CanWrite(destinationFolderPath))
            {
                return ProjectFileOperationResult.Failure("Items can only be moved within Assets.", assetPath);
            }

            if (!_gateway.IsFolder(destinationFolderPath))
            {
                return ProjectFileOperationResult.Failure("Destination folder does not exist.", destinationFolderPath);
            }

            // Nothing probed the destination before, so a drop onto a folder
            // that already held a same-named item reached AssetDatabase.MoveAsset,
            // failed there, and reported Unity's raw error for 1.9s — with the
            // status bar's identical-message dedupe making every repeat attempt
            // silent. Refuse it here instead, in the panel's own words and
            // before a command is recorded, so the caller can offer the user a
            // resolution (ZepController.MoveWithConflictResolution).
            var fileName = string.IsNullOrEmpty(destinationFileName)
                ? AssetPathFormat.GetFileName(assetPath)
                : destinationFileName;
            var destinationPath = AssetPath.From(
                AssetPathFormat.Combine(destinationFolderPath, fileName));
            if (IsOccupiedMoveDestination(_gateway, assetPath, destinationPath))
            {
                return ProjectFileOperationResult.Failure(
                    "'" + fileName + "' already exists in " + destinationFolderPath + ".",
                    assetPath);
            }

            return Execute(new MoveCommand(
                assetPath,
                destinationFolderPath,
                _gateway.GetGuid(assetPath),
                identityVerified: true,
                destinationFileName: destinationFileName));
        }

        // Whether a move of sourcePath to destinationPath would land on
        // something that is already there — folders included, since the gateway
        // reports those too. This is the rule AssetDatabase.MoveAsset trips on,
        // asked before the move instead of read off its error afterwards.
        //
        // An item is never in its own way: dropping something back into the
        // folder it already sits in makes the destination its own path, and
        // that is a no-op for the gateway to absorb, not a conflict to put to
        // the user. Paths compare the way they compare everywhere else in the
        // panel, so a folder spelt with different case is still the same folder.
        internal static bool IsOccupiedMoveDestination(
            IProjectAssetGateway gateway,
            AssetPath sourcePath,
            AssetPath destinationPath)
        {
            return !string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
                && gateway.Exists(destinationPath);
        }

        internal ProjectFileOperationResult Delete(AssetPath assetPath)
        {
            var preparationFailure = PrepareNewCommand();
            if (preparationFailure != null) return preparationFailure;

            if (AssetPathClassification.IsProjectRootPath(assetPath))
            {
                return ProjectFileOperationResult.Failure("Project root folders cannot be deleted.", assetPath);
            }

            if (AssetPathClassification.IsPackageRootPath(assetPath))
            {
                return ProjectFileOperationResult.Failure("Package roots cannot be deleted. Remove the package via the Package Manager.", assetPath);
            }

            if (!_gateway.CanWrite(assetPath))
            {
                return ProjectFileOperationResult.Failure("This item is read-only.", assetPath);
            }

            return Execute(new DeleteCommand(
                assetPath, _gateway.GetGuid(assetPath), identityVerified: true));
        }



        internal ProjectFileOperationResult SetTags(IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByAssetPath)
        {
            var preparationFailure = PrepareNewCommand();
            if (preparationFailure != null) return preparationFailure;

            if (tagIdsByAssetPath == null || tagIdsByAssetPath.Count == 0)
            {
                return ProjectFileOperationResult.Failure("Choose at least one item to tag.");
            }

            var assignmentPreparation = PrepareTagAssignmentsForMutation();
            if (!assignmentPreparation.Succeeded) return assignmentPreparation;

            var desiredTagIdsByAssetPath = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var originalTagIdsByAssetPath = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var assetGuidsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tagIdsByAssetPath)
            {
                var assetPath = AssetPath.From(entry.Key);
                if (!_gateway.CanTag(assetPath))
                {
                    return ProjectFileOperationResult.Failure("This item cannot be tagged.", assetPath);
                }

                var currentTagIds = _gateway.GetTagIds(assetPath);
                if (AreTagSetsEqual(currentTagIds, entry.Value))
                {
                    continue;
                }

                desiredTagIdsByAssetPath[assetPath] = CopyTagIds(entry.Value);
                originalTagIdsByAssetPath[assetPath] = CopyTagIds(currentTagIds);
                assetGuidsByPath[assetPath] = _gateway.GetGuid(assetPath);
            }

            if (desiredTagIdsByAssetPath.Count == 0)
            {
                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }

            return Execute(new SetTagsCommand(
                desiredTagIdsByAssetPath,
                originalTagIdsByAssetPath,
                assetGuidsByPath));
        }

        // Records an asset that was already created on disk by the UI recipe flow into the
        // undo history so Undo removes it (to private undo storage) and Redo restores it. Unlike the
        // other entry points this does NOT run the command's Execute — the asset already
        // exists and must not be re-created — so it pushes the command as already-executed.
        internal ProjectFileOperationResult RecordCreatedAsset(AssetPath createdPath)
        {
            var preparationFailure = PrepareTimeline(_prepareForAppliedCommand);
            if (preparationFailure != null)
            {
                _history.Invalidate();
                return preparationFailure;
            }

            if (createdPath.IsEmpty)
            {
                _history.Invalidate();
                return ProjectFileOperationResult.Failure("No asset was created.");
            }

            if (!_gateway.Exists(createdPath))
            {
                // The entry landed on disk but Unity does not import it — a
                // hidden file (".env", "Foo~") in an external drop is the normal
                // case, and Unity's own semantics keep it out of the asset
                // database. There is no asset to undo and nothing inconsistent
                // about the history, so record nothing and report no failure:
                // invalidating here wiped both stacks plus the native timeline
                // and cut the caller's record loop short, so every file dropped
                // after the hidden one also lost its undo record.
                return ProjectFileOperationResult.Success(createdPath, string.Empty);
            }

            var assetGuid = _gateway.GetGuid(createdPath);
            if (string.IsNullOrWhiteSpace(assetGuid))
            {
                // The caller has already committed a filesystem mutation. If it
                // cannot be represented safely, clear the prior timeline so the
                // next Cmd+Z can never fall through to an unrelated older action.
                _history.Invalidate();
                return ProjectFileOperationResult.Failure(
                    "The created asset could not be identified for Undo.", createdPath);
            }

            _history.PushExecuted(new CreateAssetCommand(createdPath, assetGuid));
            return ProjectFileOperationResult.Success(createdPath, string.Empty);
        }

        // The controller prepares the native timeline before it previews a tag
        // edit. The gateway then commits definitions + GUID assignments together
        // through the composite command below.
        internal ProjectFileOperationResult PrepareRecordedMutation()
        {
            return PrepareNewCommand()
                ?? ProjectFileOperationResult.Success(string.Empty, string.Empty);
        }

        internal ProjectFileOperationResult ChangeTagDefinitionsAfterPreparation(
            IReadOnlyList<ZepTag> expectedDefinitions,
            IReadOnlyList<ZepTag> desiredDefinitions,
            bool removeUndefinedAssignments,
            string assignmentTagIdToRemove = null)
        {
            if (!(_gateway is IProjectTagDefinitionGateway definitionGateway))
            {
                return ProjectFileOperationResult.Failure(
                    "Tag definitions cannot be changed in this project context.");
            }

            ProjectTagStateSnapshot original;
            try
            {
                original = definitionGateway.CaptureTagState();
            }
            catch (Exception ex)
            {
                // A pending/corrupt settings transaction means the current tag
                // state cannot be compared safely. Cut the older branch so the
                // next global Undo can never fall through this failed mutation.
                _history.Invalidate();
                return ProjectFileOperationResult.Failure(ex.Message);
            }
            if (!ProjectTagStateSnapshot.DefinitionsEqual(
                    original.Definitions,
                    expectedDefinitions))
            {
                return ProjectFileOperationResult.Failure(
                    "Tag definitions changed before this operation could be applied.");
            }

            var normalizedTagIdToRemove = string.IsNullOrWhiteSpace(assignmentTagIdToRemove)
                ? string.Empty
                : FindSingleRemovedDefinitionId(original.Definitions, desiredDefinitions);
            var desiredAssignments = removeUndefinedAssignments
                ? FilterTagAssignments(
                    original.AssignmentsByGuid,
                    tagId => ContainsTagDefinition(desiredDefinitions, tagId))
                : string.IsNullOrEmpty(normalizedTagIdToRemove)
                    ? original.AssignmentsByGuid
                    : FilterTagAssignments(
                        original.AssignmentsByGuid,
                        tagId => !string.Equals(
                            tagId,
                            normalizedTagIdToRemove,
                            StringComparison.OrdinalIgnoreCase));
            var desired = new ProjectTagStateSnapshot(
                desiredDefinitions,
                desiredAssignments);
            if (ProjectTagStateSnapshot.ContentEquals(original, desired))
            {
                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }

            var result = Execute(new SetTagDefinitionsCommand(desired, original));
            if (!result.Succeeded)
            {
                // A CAS mismatch means an external settings edit already cut a
                // branch; an I/O failure may have reached a compensated boundary.
                // Either way, older markers must not be allowed to fall through.
                _history.Invalidate();
            }

            return result;
        }

        internal ProjectFileOperationResult Undo()
        {
            if (!_history.CanUndo)
            {
                return ProjectFileOperationResult.Failure("Nothing to undo.");
            }

            // Keep the record on its source stack while filesystem work runs.
            // If an import triggers a domain reload mid-operation, persisted
            // history still describes the last known complete state.
            var command = _history.PeekUndo();
            if (command == null)
            {
                // A record that cannot rehydrate (its payload no longer forms a
                // valid command) is dead, not retryable. Dropping it here keeps
                // the failure attributable; dereferencing it threw an NRE that
                // the bridge could only surface as a whole-timeline reset.
                _history.DiscardUndo();
                return ProjectFileOperationResult.Failure(
                    "A recorded operation could not be restored and was dropped from history.");
            }

            var result = command.Undo(_gateway);
            if (result.Succeeded)
            {
                _history.CompleteUndo(command);
            }
            else
            {
                _history.DiscardUndo();
            }
            // A failed undo is dropped from history. Re-pushing it wedges the
            // stack when the command can never succeed again (e.g. its saved
            // payload is unavailable) and re-runs its side effects on
            // every subsequent Cmd+Z.

            return result;
        }

        internal ProjectFileOperationResult Redo()
        {
            if (!_history.CanRedo)
            {
                return ProjectFileOperationResult.Failure("Nothing to redo.");
            }

            var command = _history.PeekRedo();
            if (command == null)
            {
                // Mirrors Undo: a dead record is dropped, not dereferenced.
                _history.DiscardRedo();
                return ProjectFileOperationResult.Failure(
                    "A recorded operation could not be restored and was dropped from history.");
            }

            var result = command.Execute(_gateway);
            if (result.Succeeded)
            {
                _history.CompleteRedo(command);
            }
            else
            {
                _history.DiscardRedo();
            }
            // Mirrors Undo: a failed redo is dropped rather than wedging the stack.

            return result;
        }

        // The kind of the record the next Undo()/Redo() will replay, or null when
        // that stack is empty. Read BEFORE the replay: a completed one has moved
        // to the opposite stack, and the result alone cannot say what it was.
        internal ProjectCommandKind? PeekUndoKind() => _history.PeekUndoKind();

        internal ProjectCommandKind? PeekRedoKind() => _history.PeekRedoKind();

        internal void BeginExecutionGroup() => _history.BeginExecutionGroup();

        internal void EndExecutionGroup() => _history.EndExecutionGroup();

        internal bool ClearRedo() => _history.ClearRedo();

        internal void InvalidateHistory() => _history.Invalidate();

        // prepareForAppliedCommand gates the record of a change the caller has
        // already made (RecordCreatedAsset). It is asked separately because the
        // change itself can close the new-command gate: importing a new script
        // starts a compile. Without one, both share the new-command gate.
        public void SetExecutionPreparation(
            Func<bool> prepareForExecution,
            Func<bool> prepareForAppliedCommand = null)
        {
            _prepareForExecution = prepareForExecution ?? (() => true);
            _prepareForAppliedCommand = prepareForAppliedCommand ?? _prepareForExecution;
        }

        private ProjectFileOperationResult Execute(IReversibleProjectCommand command)
        {
            var result = command.Execute(_gateway);
            if (result.Succeeded)
            {
                _history.PushExecuted(command);
            }

            return result;
        }

        // A rename takes a NAME, not a path. "Assets/A/B.png" renamed to
        // "sub/child" used to create the folder and move the asset into it under
        // a record typed Rename, whose inverse left the folder behind (audit
        // 5.6); "." and ".." only failed further down by accident. Reserved
        // device names and trailing dots/spaces are rejected on every platform
        // rather than only where the filesystem happens to refuse them, so a
        // project stays portable and the refusal is the same everywhere.
        private const string NameRejectedMessage =
            "That name cannot be used. A name cannot contain \"/\" or \"\\\", "
            + "cannot be \".\" or \"..\", cannot end with a dot, "
            + "and cannot be a reserved system name such as CON or COM1.";

        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static bool IsUsableFileName(string name)
        {
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                return false;
            }

            if (string.Equals(name, ".", StringComparison.Ordinal)
                || string.Equals(name, "..", StringComparison.Ordinal))
            {
                return false;
            }

            // Trailing spaces never reach here — the caller trims them, which is
            // the long-standing forgiving behaviour and stays. A trailing dot is
            // not whitespace: Windows silently strips it, so "Foo." and "Foo"
            // would be the same file on one platform and two on another.
            if (name[name.Length - 1] == '.')
            {
                return false;
            }

            // Windows resolves the device by the stem, so "con.txt" is the
            // console just as "CON" is.
            var dot = name.IndexOf('.');
            var stem = dot < 0 ? name : name.Substring(0, dot);
            for (var i = 0; i < ReservedDeviceNames.Length; i++)
            {
                if (string.Equals(stem, ReservedDeviceNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private ProjectFileOperationResult PrepareNewCommand()
        {
            return PrepareTimeline(_prepareForExecution);
        }

        private static ProjectFileOperationResult PrepareTimeline(Func<bool> prepare)
        {
            try
            {
                return prepare()
                    ? null
                    : ProjectFileOperationResult.Failure(
                        "Wait for Unity to finish the pending Undo or Redo operation.");
            }
            catch (Exception ex)
            {
                return ProjectFileOperationResult.Failure(
                    "The Unity Undo timeline could not be prepared: " + ex.Message);
            }
        }

        internal ProjectFileOperationResult PrepareTagAssignmentsForMutation()
        {
            try
            {
                if (_gateway is IProjectTagAssignmentPreparationGateway preparationGateway)
                {
                    return preparationGateway.PrepareTagAssignmentMutation();
                }

                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                return ProjectFileOperationResult.Failure(ex.Message);
            }
        }

        private static bool AreTagSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (var i = 0; i < leftCount; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<string> CopyTagIds(IReadOnlyList<string> tagIds)
        {
            if (tagIds == null || tagIds.Count == 0)
            {
                return Array.Empty<string>();
            }

            var copy = new string[tagIds.Count];
            for (var i = 0; i < tagIds.Count; i++)
            {
                copy[i] = tagIds[i];
            }

            return copy;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> FilterTagAssignments(
            IReadOnlyDictionary<string, IReadOnlyList<string>> assignmentsByGuid,
            Func<string, bool> keepTagId)
        {
            var filtered = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (assignmentsByGuid == null || keepTagId == null)
            {
                return filtered;
            }

            foreach (var entry in assignmentsByGuid)
            {
                var retained = new List<string>();
                if (entry.Value != null)
                {
                    for (var i = 0; i < entry.Value.Count; i++)
                    {
                        var tagId = entry.Value[i];
                        if (keepTagId(tagId))
                        {
                            retained.Add(tagId);
                        }
                    }
                }

                if (retained.Count > 0)
                {
                    filtered[entry.Key] = retained.ToArray();
                }
            }

            return filtered;
        }

        private static string FindSingleRemovedDefinitionId(
            IReadOnlyList<ZepTag> originalDefinitions,
            IReadOnlyList<ZepTag> desiredDefinitions)
        {
            string removedId = null;
            for (var i = 0; i < originalDefinitions.Count; i++)
            {
                var candidate = originalDefinitions[i].Id;
                if (ContainsTagDefinition(desiredDefinitions, candidate))
                {
                    continue;
                }

                if (removedId != null)
                {
                    return string.Empty;
                }

                removedId = candidate;
            }

            return removedId ?? string.Empty;
        }

        private static bool ContainsTagDefinition(
            IReadOnlyList<ZepTag> definitions,
            string tagId)
        {
            if (definitions == null || string.IsNullOrWhiteSpace(tagId))
            {
                return false;
            }

            for (var i = 0; i < definitions.Count; i++)
            {
                if (string.Equals(
                    definitions[i].Id,
                    tagId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
