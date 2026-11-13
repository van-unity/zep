using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    // Paths are locations, not identities. Every inverse path mutation validates
    // Unity's GUID first so an out-of-band delete/recreate cannot make Undo move,
    // rename, or tag a different asset that happens to occupy the old path.
    internal static class ProjectCommandIdentity
    {
        internal static ProjectFileOperationResult Validate(
            IProjectAssetGateway gateway,
            AssetPath assetPath,
            string expectedGuid)
        {
            if (assetPath.IsEmpty || string.IsNullOrWhiteSpace(expectedGuid))
            {
                return ProjectFileOperationResult.Failure(
                    "The original item can no longer be identified safely.", assetPath);
            }

            var currentGuid = gateway.GetGuid(assetPath);
            if (!string.Equals(currentGuid, expectedGuid, System.StringComparison.OrdinalIgnoreCase))
            {
                return ProjectFileOperationResult.Failure(
                    "The item at this path has changed since the operation was recorded.", assetPath);
            }

            return null;
        }
    }

    internal sealed class CreateFolderCommand : IReversibleProjectCommand
    {
        private readonly AssetPath _parentFolderPath;
        private readonly string _folderName;
        private AssetPath _createdPath;
        private AssetPath _payloadPath;
        private string _assetGuid;

        internal CreateFolderCommand(AssetPath parentFolderPath, string folderName)
        {
            _parentFolderPath = parentFolderPath;
            _folderName = folderName ?? string.Empty;
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.CreateFolder,
                PrimaryPath = _parentFolderPath,
                Name = _folderName,
                SecondaryPath = _createdPath,
                TertiaryPath = _payloadPath,
                AssetGuid = _assetGuid,
            };
        }

        internal static CreateFolderCommand FromRecord(ProjectCommandRecord record)
        {
            var command = new CreateFolderCommand(AssetPath.From(record.PrimaryPath), record.Name);
            command._createdPath = AssetPath.From(record.SecondaryPath);
            command._payloadPath = AssetPath.From(record.TertiaryPath);
            command._assetGuid = record.AssetGuid;
            return command;
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            if (!_payloadPath.IsEmpty)
            {
                var identityFailure = ProjectCommandIdentity.Validate(gateway, _payloadPath, _assetGuid);
                if (identityFailure != null)
                {
                    return identityFailure;
                }

                return gateway.RestoreUndoPayload(_payloadPath, _createdPath);
            }

            var result = gateway.CreateFolder(_parentFolderPath, _folderName);
            if (result.Succeeded)
            {
                _createdPath = AssetPath.From(result.AssetPath);
                _assetGuid = gateway.GetGuid(_createdPath);
                if (string.IsNullOrWhiteSpace(_assetGuid))
                {
                    var deleteRollback = gateway.Delete(_createdPath);
                    if (deleteRollback.Succeeded || !gateway.Exists(_createdPath))
                    {
                        return ProjectFileOperationResult.Failure(
                            "Unity could not identify the new folder, so its creation was rolled back.",
                            _createdPath);
                    }

                    // Rollback failed. Preserve a marker rather
                    // than letting Cmd+Z fall through to an older command; its
                    // identity check will fail closed and reset the timeline.
                }
            }

            return result;
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            var identityFailure = ProjectCommandIdentity.Validate(gateway, _createdPath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            // Preserve the current payload: the folder may have gained content
            // outside the panel's command history (Finder, Unity's own Project
            // window) since it was created.
            var result = _payloadPath.IsEmpty
                ? gateway.StoreUndoPayload(_createdPath)
                : gateway.StoreUndoPayload(_createdPath, _payloadPath);
            if (result.Succeeded)
            {
                _payloadPath = AssetPath.From(result.AssetPath);
            }

            return result;
        }
    }

    // Makes a recipe-created asset undoable. The asset itself is written to disk out-of-band
    // by the UI create flow (ZepCreateAssetSupport uses templates / shaders /
    // reflection that live above the Core gateway), so the initial creation is RECORDED via
    // ProjectCommandHistory.PushExecuted rather than run through Execute — there is nothing to
    // re-create. This is DeleteCommand in reverse: Undo moves the asset to private history storage;
    // Execute runs only on REDO and restores it from private history storage, so redo brings back the exact
    // asset (content + .meta + GUID preserved), never a fresh recipe run.
    internal sealed class CreateAssetCommand : IReversibleProjectCommand
    {
        private readonly AssetPath _createdPath;
        private readonly string _assetGuid;
        private AssetPath _payloadPath;

        internal CreateAssetCommand(AssetPath createdPath, string assetGuid)
        {
            _createdPath = createdPath;
            _assetGuid = assetGuid ?? string.Empty;
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.CreateAsset,
                PrimaryPath = _createdPath,
                SecondaryPath = _payloadPath,
                AssetGuid = _assetGuid,
            };
        }

        internal static CreateAssetCommand FromRecord(ProjectCommandRecord record)
        {
            var command = new CreateAssetCommand(AssetPath.From(record.PrimaryPath), record.AssetGuid);
            command._payloadPath = AssetPath.From(record.SecondaryPath);
            return command;
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            if (_createdPath.IsEmpty)
            {
                return ProjectFileOperationResult.Failure("No created asset to restore.");
            }

            // Execute runs only on REDO (the initial create is recorded via PushExecuted).
            // An out-of-band replacement at the same path is not the asset this command
            // created. Treat that as a branch collision instead of silently claiming the
            // redo succeeded and stranding the exact payload in history storage.
            if (gateway.Exists(_createdPath))
            {
                return ProjectFileOperationResult.Failure(
                    "An item already exists at the created asset's original path.", _createdPath);
            }

            if (_payloadPath.IsEmpty)
            {
                return ProjectFileOperationResult.Failure(
                    "The created asset is no longer available to restore.", _createdPath);
            }

            var identityFailure = ProjectCommandIdentity.Validate(gateway, _payloadPath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            var parent = AssetPath.From(AssetPathFormat.GetParentFolder(_createdPath));
            if (!parent.IsEmpty)
            {
                gateway.EnsureFolder(parent);
            }

            return gateway.RestoreUndoPayload(_payloadPath, _createdPath);
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            if (_createdPath.IsEmpty)
            {
                return ProjectFileOperationResult.Failure("No created asset to remove.");
            }

            var identityFailure = ProjectCommandIdentity.Validate(gateway, _createdPath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            // Preserve the current payload: the asset may have been edited since it was created
            // (mirrors CreateFolderCommand / DuplicateCommand undo).
            var result = _payloadPath.IsEmpty
                ? gateway.StoreUndoPayload(_createdPath)
                : gateway.StoreUndoPayload(_createdPath, _payloadPath);
            if (result.Succeeded)
            {
                _payloadPath = AssetPath.From(result.AssetPath);
            }

            return result;
        }
    }

    internal sealed class RenameCommand : IReversibleProjectCommand
    {
        private readonly AssetPath _originalPath;
        private readonly string _newName;
        private readonly bool _isFolder;
        private readonly string _assetGuid;
        private AssetPath _currentPath;
        // Set only by ProjectFileCommandService, which reads the GUID from the
        // same gateway immediately before constructing the command. Nothing
        // runs in between, so the first forward Execute would re-read the
        // .meta file just to compare a value with itself. Consumed on that
        // first Execute: a redo of this instance, and every rehydrated
        // (FromRecord) replay, validates exactly as before — by then the
        // project has had time to change under the recorded path. Ignored when
        // the GUID is empty, so "can no longer be identified safely" is still
        // reported where it was.
        private bool _identityVerified;

        internal RenameCommand(
            AssetPath originalPath,
            string newName,
            bool isFolder,
            string assetGuid,
            bool identityVerified = false)
        {
            _originalPath = originalPath;
            _newName = newName ?? string.Empty;
            _isFolder = isFolder;
            _assetGuid = assetGuid ?? string.Empty;
            _currentPath = _originalPath;
            _identityVerified = identityVerified && !string.IsNullOrWhiteSpace(_assetGuid);
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.Rename,
                PrimaryPath = _originalPath,
                Name = _newName,
                IsFolder = _isFolder,
                SecondaryPath = _currentPath,
                AssetGuid = _assetGuid,
            };
        }

        internal static RenameCommand FromRecord(ProjectCommandRecord record)
        {
            var command = new RenameCommand(
                AssetPath.From(record.PrimaryPath),
                record.Name,
                record.IsFolder,
                record.AssetGuid);
            command._currentPath = AssetPath.From(record.SecondaryPath);
            return command;
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            var alreadyVerified = _identityVerified;
            _identityVerified = false;
            if (!alreadyVerified)
            {
                var identityFailure = ProjectCommandIdentity.Validate(gateway, _originalPath, _assetGuid);
                if (identityFailure != null)
                {
                    return identityFailure;
                }
            }

            var destinationPath = AssetPath.From(AssetPathFormat.ChangeName(_originalPath, _newName, _isFolder));
            var result = gateway.Move(_originalPath, destinationPath, false);
            if (result.Succeeded)
            {
                _currentPath = AssetPath.From(result.AssetPath);
            }

            return result;
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            var identityFailure = ProjectCommandIdentity.Validate(gateway, _currentPath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            var result = gateway.Move(_currentPath, _originalPath, false);
            if (result.Succeeded)
            {
                _currentPath = _originalPath;
            }

            return result;
        }
    }

    internal sealed class DuplicateCommand : IReversibleProjectCommand
    {
        private readonly AssetPath _sourcePath;
        private AssetPath _duplicatePath;
        private AssetPath _payloadPath;
        private string _assetGuid;

        internal DuplicateCommand(AssetPath sourcePath)
        {
            _sourcePath = sourcePath;
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.Duplicate,
                PrimaryPath = _sourcePath,
                SecondaryPath = _duplicatePath,
                TertiaryPath = _payloadPath,
                AssetGuid = _assetGuid,
            };
        }

        internal static DuplicateCommand FromRecord(ProjectCommandRecord record)
        {
            var command = new DuplicateCommand(AssetPath.From(record.PrimaryPath));
            command._duplicatePath = AssetPath.From(record.SecondaryPath);
            command._payloadPath = AssetPath.From(record.TertiaryPath);
            command._assetGuid = record.AssetGuid;
            return command;
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            if (!_payloadPath.IsEmpty)
            {
                var identityFailure = ProjectCommandIdentity.Validate(gateway, _payloadPath, _assetGuid);
                if (identityFailure != null)
                {
                    return identityFailure;
                }

                return gateway.RestoreUndoPayload(_payloadPath, _duplicatePath);
            }

            var destinationPath = AssetPath.From(AssetPathFormat.Combine(
                AssetPathFormat.GetParentFolder(_sourcePath),
                AssetPathFormat.GetFileName(_sourcePath)));

            var result = gateway.Copy(_sourcePath, destinationPath, true);
            if (!result.Succeeded)
            {
                return result;
            }

            _duplicatePath = AssetPath.From(result.AssetPath);
            _assetGuid = gateway.GetGuid(_duplicatePath);
            if (string.IsNullOrWhiteSpace(_assetGuid))
            {
                var deleteRollback = gateway.Delete(_duplicatePath);
                if (deleteRollback.Succeeded || !gateway.Exists(_duplicatePath))
                {
                    return ProjectFileOperationResult.Failure(
                        "Unity could not identify the duplicate, so its creation was rolled back.",
                        _duplicatePath);
                }

                // Rollback failed; keep the command marker so a
                // later Undo cannot silently target the operation below it.
            }

            // A duplicate is purely additive: the source stays put and the new
            // asset never occupied a previous location. gateway.Copy surfaces the
            // source in the result's previous-path slot (mirroring Move), but the
            // incremental projection update treats a non-empty previous path as a
            // vacated location and evicts it from the index + browse projection —
            // which would make the original vanish from the panel after a copy.
            // Report no previous path so only the new asset is inserted.
            return ProjectFileOperationResult.Success(result.AssetPath, string.Empty, result.TagAssignments);
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            var identityFailure = ProjectCommandIdentity.Validate(gateway, _duplicatePath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            // Preserve the current payload: the duplicate may have been edited
            // since it was created.
            var result = _payloadPath.IsEmpty
                ? gateway.StoreUndoPayload(_duplicatePath)
                : gateway.StoreUndoPayload(_duplicatePath, _payloadPath);
            if (result.Succeeded)
            {
                _payloadPath = AssetPath.From(result.AssetPath);
            }

            return result;
        }
    }

    internal sealed class MoveCommand : IReversibleProjectCommand
    {
        private readonly AssetPath _originalPath;
        private readonly AssetPath _destinationFolderPath;
        private readonly string _assetGuid;
        private AssetPath _currentPath;
        // Set only by ProjectFileCommandService, which reads the GUID from the
        // same gateway immediately before constructing the command. Nothing
        // runs in between, so the first forward Execute would re-read the
        // .meta file just to compare a value with itself. Consumed on that
        // first Execute: a redo of this instance, and every rehydrated
        // (FromRecord) replay, validates exactly as before — by then the
        // project has had time to change under the recorded path. Ignored when
        // the GUID is empty, so "can no longer be identified safely" is still
        // reported where it was.
        private bool _identityVerified;
        // The name the asset takes in the destination folder, or empty to keep
        // its own. Only the Keep Both resolution of a move conflict sets one
        // ("Foo 1.mat" beside the "Foo.mat" already there); the undo below is
        // unaffected either way, because it moves _currentPath — wherever the
        // forward move actually put it — back to _originalPath.
        private readonly string _destinationFileName;

        internal MoveCommand(
            AssetPath originalPath,
            AssetPath destinationFolderPath,
            string assetGuid,
            bool identityVerified = false,
            string destinationFileName = null)
        {
            _originalPath = originalPath;
            _destinationFolderPath = destinationFolderPath;
            _assetGuid = assetGuid ?? string.Empty;
            _currentPath = _originalPath;
            _identityVerified = identityVerified && !string.IsNullOrWhiteSpace(_assetGuid);
            _destinationFileName = destinationFileName ?? string.Empty;
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.Move,
                PrimaryPath = _originalPath,
                SecondaryPath = _destinationFolderPath,
                TertiaryPath = _currentPath,
                AssetGuid = _assetGuid,
                // Records written before this field carried a move's name
                // deserialize it as empty, which is the "keep the source name"
                // case they were written for.
                Name = _destinationFileName,
            };
        }

        internal static MoveCommand FromRecord(ProjectCommandRecord record)
        {
            var command = new MoveCommand(
                AssetPath.From(record.PrimaryPath),
                AssetPath.From(record.SecondaryPath),
                record.AssetGuid,
                destinationFileName: record.Name);
            command._currentPath = AssetPath.From(record.TertiaryPath);
            return command;
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            var alreadyVerified = _identityVerified;
            _identityVerified = false;
            if (!alreadyVerified)
            {
                var identityFailure = ProjectCommandIdentity.Validate(gateway, _originalPath, _assetGuid);
                if (identityFailure != null)
                {
                    return identityFailure;
                }
            }

            var destinationPath = AssetPath.From(AssetPathFormat.Combine(
                _destinationFolderPath,
                string.IsNullOrEmpty(_destinationFileName)
                    ? AssetPathFormat.GetFileName(_originalPath)
                    : _destinationFileName));

            var result = gateway.Move(_originalPath, destinationPath, false);
            if (result.Succeeded)
            {
                _currentPath = AssetPath.From(result.AssetPath);
            }

            return result;
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            var identityFailure = ProjectCommandIdentity.Validate(gateway, _currentPath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            var result = gateway.Move(_currentPath, _originalPath, false);
            if (result.Succeeded)
            {
                _currentPath = _originalPath;
            }

            return result;
        }
    }

    internal sealed class SetTagsCommand : IReversibleProjectCommand
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _desiredTagIdsByAssetPath;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _originalTagIdsByAssetPath;
        private readonly IReadOnlyDictionary<string, string> _assetGuidsByPath;

        internal SetTagsCommand(
            IReadOnlyDictionary<string, IReadOnlyList<string>> desiredTagIdsByAssetPath,
            IReadOnlyDictionary<string, IReadOnlyList<string>> originalTagIdsByAssetPath,
            IReadOnlyDictionary<string, string> assetGuidsByPath)
        {
            _desiredTagIdsByAssetPath = Copy(desiredTagIdsByAssetPath);
            _originalTagIdsByAssetPath = Copy(originalTagIdsByAssetPath);
            _assetGuidsByPath = CopyGuids(assetGuidsByPath);
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.SetTags,
                DesiredTags = ToAssignmentList(_desiredTagIdsByAssetPath, _assetGuidsByPath),
                OriginalTags = ToAssignmentList(_originalTagIdsByAssetPath, _assetGuidsByPath),
            };
        }

        internal static SetTagsCommand FromRecord(ProjectCommandRecord record)
        {
            return new SetTagsCommand(
                FromAssignmentList(record.DesiredTags),
                FromAssignmentList(record.OriginalTags),
                FromIdentityList(record.DesiredTags));
        }

        private static List<ProjectCommandTagAssignment> ToAssignmentList(
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByAssetPath,
            IReadOnlyDictionary<string, string> assetGuidsByPath)
        {
            var assignments = new List<ProjectCommandTagAssignment>(tagIdsByAssetPath?.Count ?? 0);
            if (tagIdsByAssetPath == null) return assignments;
            foreach (var entry in tagIdsByAssetPath)
            {
                assetGuidsByPath.TryGetValue(entry.Key, out var assetGuid);
                var assignment = new ProjectCommandTagAssignment
                {
                    AssetPath = entry.Key,
                    AssetGuid = assetGuid ?? string.Empty,
                };
                if (entry.Value != null)
                {
                    for (var i = 0; i < entry.Value.Count; i++)
                    {
                        assignment.TagIds.Add(entry.Value[i]);
                    }
                }
                assignments.Add(assignment);
            }
            return assignments;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> FromAssignmentList(
            List<ProjectCommandTagAssignment> assignments)
        {
            var dict = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);
            if (assignments == null) return dict;
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                if (assignment == null || string.IsNullOrEmpty(assignment.AssetPath)) continue;
                dict[assignment.AssetPath] = assignment.TagIds != null
                    ? (IReadOnlyList<string>)assignment.TagIds.ToArray()
                    : System.Array.Empty<string>();
            }
            return dict;
        }

        private static IReadOnlyDictionary<string, string> FromIdentityList(
            List<ProjectCommandTagAssignment> assignments)
        {
            var identities = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            if (assignments == null) return identities;
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                if (assignment == null || string.IsNullOrEmpty(assignment.AssetPath)) continue;
                identities[assignment.AssetPath] = assignment.AssetGuid ?? string.Empty;
            }

            return identities;
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            return ApplyAtCurrentPaths(gateway, _desiredTagIdsByAssetPath);
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            return ApplyAtCurrentPaths(gateway, _originalTagIdsByAssetPath);
        }

        // Unlike Rename/Move/Delete, this command has no positional meaning:
        // a tag assignment is stored against the asset's GUID, so an asset that
        // was relocated outside the panel is still unambiguously the same target.
        // Validating the RECORDED path made an ordinary folder rename in Unity's
        // own Project window refuse the undo, which then reset the whole panel
        // timeline. Follow the GUID instead, and fall back to the recorded path
        // when it resolves to nothing so a genuine delete-and-recreate at the
        // same path is still refused by the identity check below.
        private ProjectFileOperationResult ApplyAtCurrentPaths(
            IProjectAssetGateway gateway,
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByRecordedPath)
        {
            var tagIdsByCurrentPath =
                new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tagIdsByRecordedPath)
            {
                _assetGuidsByPath.TryGetValue(entry.Key, out var assetGuid);
                var currentPath = ResolveCurrentPath(gateway, entry.Key, assetGuid);
                var failure = ProjectCommandIdentity.Validate(
                    gateway,
                    AssetPath.From(currentPath),
                    assetGuid);
                if (failure != null)
                {
                    return failure;
                }

                tagIdsByCurrentPath[currentPath] = entry.Value;
            }

            return gateway.SetTagIds(ToAssetPathKeyed(tagIdsByCurrentPath))
                .WithTagAssignments(tagIdsByCurrentPath);
        }

        private static string ResolveCurrentPath(
            IProjectAssetGateway gateway,
            string recordedPath,
            string assetGuid)
        {
            if (string.IsNullOrWhiteSpace(assetGuid))
            {
                return recordedPath;
            }

            var currentPath = gateway.GetPathForGuid(assetGuid);
            return string.IsNullOrWhiteSpace(currentPath)
                ? recordedPath
                : AssetPathFormat.Normalize(currentPath);
        }

        private static IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> ToAssetPathKeyed(
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByAssetPath)
        {
            var keyed = new Dictionary<AssetPath, IReadOnlyList<string>>(tagIdsByAssetPath.Count);
            foreach (var entry in tagIdsByAssetPath)
            {
                keyed[AssetPath.From(entry.Key)] = entry.Value;
            }

            return keyed;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> Copy(
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByAssetPath)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);
            if (tagIdsByAssetPath == null)
            {
                return copy;
            }

            foreach (var entry in tagIdsByAssetPath)
            {
                copy[AssetPathFormat.Normalize(entry.Key)] = CopyTagIds(entry.Value);
            }

            return copy;
        }

        private static IReadOnlyDictionary<string, string> CopyGuids(
            IReadOnlyDictionary<string, string> assetGuidsByPath)
        {
            var copy = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            if (assetGuidsByPath == null)
            {
                return copy;
            }

            foreach (var entry in assetGuidsByPath)
            {
                copy[AssetPathFormat.Normalize(entry.Key)] = entry.Value ?? string.Empty;
            }

            return copy;
        }

        private static string[] CopyTagIds(IReadOnlyList<string> tagIds)
        {
            if (tagIds == null || tagIds.Count == 0)
            {
                return System.Array.Empty<string>();
            }

            var copy = new string[tagIds.Count];
            for (var i = 0; i < tagIds.Count; i++)
            {
                copy[i] = tagIds[i];
            }

            return copy;
        }
    }

    internal sealed class SetTagDefinitionsCommand : IReversibleProjectCommand
    {
        private readonly ProjectTagStateSnapshot _desired;
        private readonly ProjectTagStateSnapshot _original;
        // Set only on a command rehydrated from a persisted record: the two
        // snapshots then hold just the GUIDs the edit moved, and the full states
        // have to be reconstituted against the live store before either is
        // applied. A command built from a live edit always holds full states.
        private readonly bool _assignmentsAreDiff;

        internal SetTagDefinitionsCommand(
            ProjectTagStateSnapshot desired,
            ProjectTagStateSnapshot original)
            : this(desired, original, false)
        {
        }

        private SetTagDefinitionsCommand(
            ProjectTagStateSnapshot desired,
            ProjectTagStateSnapshot original,
            bool assignmentsAreDiff)
        {
            if (desired == null) throw new System.ArgumentNullException(nameof(desired));
            if (original == null) throw new System.ArgumentNullException(nameof(original));
            _desired = new ProjectTagStateSnapshot(
                desired.Definitions,
                desired.AssignmentsByGuid);
            _original = new ProjectTagStateSnapshot(
                original.Definitions,
                original.AssignmentsByGuid);
            _assignmentsAreDiff = assignmentsAreDiff;
        }

        public ProjectCommandRecord ToRecord()
        {
            // A tag rename or recolour leaves every assignment where it was, and
            // recording the whole table twice put ~2A entries into a payload that
            // every later save re-serialises and every domain reload re-parses.
            // Only the GUIDs whose tag sets differ can matter to a replay; the
            // rest are reconstituted from the live store in Apply. Diffing an
            // already-diffed pair is idempotent (every key in it differs by
            // construction), so re-recording an undone command stays correct.
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.SetTagDefinitions,
                DesiredTagDefinitions = ToDefinitionRecordList(_desired.Definitions),
                OriginalTagDefinitions = ToDefinitionRecordList(_original.Definitions),
                AssignmentsAreDiff = true,
                DesiredGuidTagAssignments = ToChangedAssignmentRecordList(
                    _desired.AssignmentsByGuid,
                    _original.AssignmentsByGuid),
                OriginalGuidTagAssignments = ToChangedAssignmentRecordList(
                    _original.AssignmentsByGuid,
                    _desired.AssignmentsByGuid)
            };
        }

        internal static SetTagDefinitionsCommand FromRecord(ProjectCommandRecord record)
        {
            if (!TryFromDefinitionRecordList(record.DesiredTagDefinitions, out var desiredDefinitions)
                || !TryFromDefinitionRecordList(record.OriginalTagDefinitions, out var originalDefinitions)
                || !TryFromAssignmentRecordList(record.DesiredGuidTagAssignments, out var desiredAssignments)
                || !TryFromAssignmentRecordList(record.OriginalGuidTagAssignments, out var originalAssignments))
            {
                return null;
            }

            return new SetTagDefinitionsCommand(
                new ProjectTagStateSnapshot(desiredDefinitions, desiredAssignments),
                new ProjectTagStateSnapshot(originalDefinitions, originalAssignments),
                record.AssignmentsAreDiff);
        }

        public ProjectFileOperationResult Execute(IProjectAssetGateway gateway)
        {
            return Apply(gateway, _original, _desired);
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            return Apply(gateway, _desired, _original);
        }

        private ProjectFileOperationResult Apply(
            IProjectAssetGateway gateway,
            ProjectTagStateSnapshot expected,
            ProjectTagStateSnapshot desired)
        {
            if (!(gateway is IProjectTagDefinitionGateway definitionGateway))
            {
                return ProjectFileOperationResult.Failure(
                    "Tag definitions cannot be changed in this project context.");
            }

            if (_assignmentsAreDiff)
            {
                ProjectTagStateSnapshot live;
                try
                {
                    live = definitionGateway.CaptureTagState();
                }
                catch (System.Exception ex)
                {
                    return ProjectFileOperationResult.Failure(ex.Message);
                }

                // Entries outside the diff were identical in both recorded
                // states, so the live values ARE both states' values. That is
                // also why an unrelated tag click no longer blocks this undo,
                // while a change to a GUID the edit did touch still fails the
                // compare-and-set below.
                var changedGuids = CollectChangedGuids(expected, desired);
                expected = MergeChangedAssignmentsOntoLiveState(expected, live, changedGuids);
                desired = MergeChangedAssignmentsOntoLiveState(desired, live, changedGuids);
            }

            var result = definitionGateway.CompareAndSetTagState(expected, desired);
            if (!result.Succeeded)
            {
                return result;
            }

            if (expected.AssignmentsByGuid.Count != desired.AssignmentsByGuid.Count)
            {
                return result.WithTagAssignmentSnapshotChanged();
            }

            foreach (var entry in expected.AssignmentsByGuid)
            {
                if (!desired.AssignmentsByGuid.TryGetValue(entry.Key, out var desiredTagIds)
                    || !TagIdsEqual(entry.Value, desiredTagIds))
                {
                    return result.WithTagAssignmentSnapshotChanged();
                }
            }

            return result;
        }

        // The recorded diff drops a GUID whose tag set became empty (a snapshot
        // cannot hold an empty assignment), so the set of GUIDs the edit touched
        // is the union of both sides' keys - a GUID reaches a diff only because
        // its tag set differs, so it is non-empty on at least one side.
        private static HashSet<string> CollectChangedGuids(
            ProjectTagStateSnapshot expected,
            ProjectTagStateSnapshot desired)
        {
            var changedGuids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in expected.AssignmentsByGuid)
            {
                changedGuids.Add(entry.Key);
            }

            foreach (var entry in desired.AssignmentsByGuid)
            {
                changedGuids.Add(entry.Key);
            }

            return changedGuids;
        }

        private static ProjectTagStateSnapshot MergeChangedAssignmentsOntoLiveState(
            ProjectTagStateSnapshot side,
            ProjectTagStateSnapshot live,
            HashSet<string> changedGuids)
        {
            var merged = new Dictionary<string, IReadOnlyList<string>>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in live.AssignmentsByGuid)
            {
                // A touched GUID absent from this side had no tags in this state.
                if (!changedGuids.Contains(entry.Key))
                {
                    merged[entry.Key] = entry.Value;
                }
            }

            foreach (var entry in side.AssignmentsByGuid)
            {
                merged[entry.Key] = entry.Value;
            }

            return new ProjectTagStateSnapshot(side.Definitions, merged);
        }

        private static List<ProjectCommandTagDefinition> ToDefinitionRecordList(
            IReadOnlyList<ZepTag> tags)
        {
            var records = new List<ProjectCommandTagDefinition>(tags?.Count ?? 0);
            if (tags == null)
            {
                return records;
            }

            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                records.Add(new ProjectCommandTagDefinition
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Color = tag.Color,
                    IsFavorite = tag.IsFavorite
                });
            }

            return records;
        }

        private static bool TryFromDefinitionRecordList(
            List<ProjectCommandTagDefinition> records,
            out ZepTag[] tags)
        {
            if (records == null || records.Count == 0)
            {
                tags = null;
                return false;
            }

            tags = new ZepTag[records.Count];
            var seenIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record == null
                    || string.IsNullOrWhiteSpace(record.Id)
                    || !string.Equals(record.Id, record.Id.Trim(), System.StringComparison.Ordinal)
                    || !seenIds.Add(record.Id)
                    || string.IsNullOrWhiteSpace(record.Name)
                    || !string.Equals(record.Name, record.Name.Trim(), System.StringComparison.Ordinal)
                    || record.Color == ZepTagColor.None
                    || !System.Enum.IsDefined(typeof(ZepTagColor), record.Color))
                {
                    tags = null;
                    return false;
                }

                tags[i] = new ZepTag(
                    record.Id,
                    record.Name,
                    record.Color,
                    record.IsFavorite);
            }

            return true;
        }

        // This side's entries for the GUIDs whose tag sets differ between the two
        // states. A GUID missing from `other` differs by definition (it has no
        // tags there), and one missing HERE is carried by the other side's list.
        private static List<ProjectCommandGuidTagAssignment> ToChangedAssignmentRecordList(
            IReadOnlyDictionary<string, IReadOnlyList<string>> assignments,
            IReadOnlyDictionary<string, IReadOnlyList<string>> other)
        {
            var records = new List<ProjectCommandGuidTagAssignment>();
            if (assignments == null) return records;
            foreach (var entry in assignments)
            {
                if (other != null
                    && other.TryGetValue(entry.Key, out var otherTagIds)
                    && TagIdsEqual(entry.Value, otherTagIds))
                {
                    continue;
                }

                var record = new ProjectCommandGuidTagAssignment
                {
                    AssetGuid = entry.Key
                };
                if (entry.Value != null)
                {
                    for (var i = 0; i < entry.Value.Count; i++)
                    {
                        record.TagIds.Add(entry.Value[i]);
                    }
                }

                records.Add(record);
            }

            return records;
        }

        private static bool TagIdsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            for (var i = 0; i < leftCount; i++)
            {
                if (!string.Equals(left[i], right[i], System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryFromAssignmentRecordList(
            List<ProjectCommandGuidTagAssignment> records,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> assignments)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);
            assignments = copy;
            if (records == null) return false;
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record == null
                    || string.IsNullOrWhiteSpace(record.AssetGuid)
                    || copy.ContainsKey(record.AssetGuid))
                {
                    assignments = null;
                    return false;
                }

                copy[record.AssetGuid] = record.TagIds != null
                    ? (IReadOnlyList<string>)record.TagIds.ToArray()
                    : System.Array.Empty<string>();
            }

            return true;
        }
    }
}
