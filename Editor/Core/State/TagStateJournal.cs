using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    /// <summary>
    /// Changes tag definitions and tag assignments together. They live in two
    /// files, so a change is journaled first: an interruption before the commit
    /// flag is written rolls both back to the original state, one after it rolls
    /// both forward to the desired state. <see cref="TryRecover"/> finishes
    /// whichever applies and must succeed before either store is trusted.
    /// </summary>
    public sealed class TagStateJournal
    {
        private sealed class PersistedTagStateTransaction
        {
            public bool CommitDesired;
            // Set when the edit left every assignment where it was (a rename, a
            // recolour, a favourite toggle). Both states below then carry an
            // EMPTY assignment list instead of the project's whole table twice,
            // and recovery leaves the assignment store alone: there is nothing
            // to roll forward or back. Journals written before this field
            // existed read it as false and still carry both full tables.
            public bool AssignmentsUnchanged;
            public PersistedTagState Original = new PersistedTagState();
            public PersistedTagState Desired = new PersistedTagState();
        }

        private sealed class PersistedTagState
        {
            public List<PersistedTagDefinition> Definitions = new List<PersistedTagDefinition>();
            public List<PersistedGuidTagAssignment> Assignments = new List<PersistedGuidTagAssignment>();
        }

        private sealed class PersistedTagDefinition
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public ZepTagColor Color;
            public bool IsFavorite;
        }

        private sealed class PersistedGuidTagAssignment
        {
            public string AssetGuid = string.Empty;
            public List<string> TagIds = new List<string>();
        }

        private readonly Func<string> _journalPath;
        private readonly Func<TagDefinitionStore> _definitions;
        private readonly Func<TagAssignmentStore> _assignments;
        private readonly Action<string> _logWarning;
        private bool _pending = true;
        private bool _active;

        internal Action<string> CheckpointForTests { get; set; }

        /// <summary>
        /// The path and both stores are read on every use rather than captured:
        /// each is created lazily by the editor, and the first journal call can
        /// come before either exists.
        /// </summary>
        public TagStateJournal(
            Func<string> journalPath,
            Func<TagDefinitionStore> definitions,
            Func<TagAssignmentStore> assignments,
            Action<string> logWarning)
        {
            _journalPath = journalPath ?? throw new ArgumentNullException(nameof(journalPath));
            _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
            _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>
        /// False until a recovery pass has found no journal on disk, and again
        /// while or after a change writes one.
        /// </summary>
        public bool IsSettled => !_pending && !_active;

        /// <summary>Makes <see cref="IsSettled"/> false until the next successful <see cref="TryRecover"/>.</summary>
        public void RequireRecovery()
        {
            _pending = true;
        }

        public ProjectTagStateSnapshot Capture()
        {
            if (!TryRecover(out var recoveryError))
            {
                throw new InvalidOperationException(recoveryError);
            }

            var assignmentStore = _assignments();
            assignmentStore.ReloadFromDisk();
            return new ProjectTagStateSnapshot(
                _definitions().GetOrderedTags(),
                assignmentStore.GetAllTagIdsByGuid());
        }

        public ProjectFileOperationResult CompareAndSet(
            ProjectTagStateSnapshot expected,
            ProjectTagStateSnapshot desired)
        {
            if (expected == null || desired == null)
            {
                return ProjectFileOperationResult.Failure("The tag definition snapshot is missing.");
            }

            ProjectTagStateSnapshot current;
            try
            {
                current = Capture();
            }
            catch (Exception ex)
            {
                return ProjectFileOperationResult.Failure(ex.Message);
            }

            if (!ProjectTagStateSnapshot.ContentEquals(current, expected))
            {
                return ProjectFileOperationResult.Failure(
                    "Tags changed outside this operation, so Undo did not overwrite them.");
            }

            if (ProjectTagStateSnapshot.ContentEquals(current, desired))
            {
                return ProjectFileOperationResult.Success(string.Empty, string.Empty);
            }

            var assignmentStore = _assignments();
            var definitionsChanged = !ProjectTagStateSnapshot.DefinitionsEqual(
                current.Definitions,
                desired.Definitions);
            var assignmentsChanged = !AssignmentsEqual(
                current.AssignmentsByGuid,
                desired.AssignmentsByGuid);
            var transaction = new PersistedTagStateTransaction
            {
                AssignmentsUnchanged = !assignmentsChanged,
                Original = ToPersistedTagState(current, assignmentsChanged),
                Desired = ToPersistedTagState(desired, assignmentsChanged)
            };
            // The journal has to mean what it says after a round trip. When the
            // assignments are deliberately absent, the definitions ARE the whole
            // recorded content, so that is what is verified.
            if (!TryFromPersistedTagState(transaction.Desired, out var validatedDesired)
                || !(assignmentsChanged
                    ? ProjectTagStateSnapshot.ContentEquals(validatedDesired, desired)
                    : ProjectTagStateSnapshot.DefinitionsEqual(
                        validatedDesired.Definitions,
                        desired.Definitions)))
            {
                return ProjectFileOperationResult.Failure(
                    "The requested tag definition snapshot is invalid.");
            }

            try
            {
                _pending = true;
                _active = true;
                try
                {
                    Write(transaction);
                    CheckpointForTests?.Invoke("prepared");

                    if (assignmentsChanged)
                    {
                        assignmentStore.ReplaceAll(desired.AssignmentsByGuid);
                    }
                    CheckpointForTests?.Invoke("assignments-applied");

                    if (definitionsChanged)
                    {
                        _definitions().ReplaceAll(desired.Definitions);
                    }
                    CheckpointForTests?.Invoke("definitions-applied");

                    // The journal starts in rollback mode. Only flip it after both backing
                    // stores are durable; an editor/process interruption before this atomic
                    // replace restores Original, while one after it finishes Desired.
                    transaction.CommitDesired = true;
                    Write(transaction);
                    TryDelete();

                    return ProjectFileOperationResult.Success(
                        string.Empty,
                        string.Empty,
                        tagDefinitionsChanged: true);
                }
                finally
                {
                    _active = false;
                }
            }
            catch (Exception ex)
            {
                if (!TryRecover(out var recoveryError))
                {
                    return ProjectFileOperationResult.Failure(
                        ex.Message + " Tag settings recovery is pending: " + recoveryError);
                }

                return ProjectFileOperationResult.Failure(ex.Message);
            }
        }

        public bool TryRecover(out string error)
        {
            if (_active)
            {
                error = "A tag transaction is in progress.";
                return false;
            }

            var path = _journalPath();
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                _pending = false;
                error = string.Empty;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                _pending = false;
                error = string.Empty;
                return true;
            }

            try
            {
                CheckpointForTests?.Invoke("before-recovery");
                var transaction = string.IsNullOrWhiteSpace(json)
                    ? null
                    : Deserialize(json);
                if (transaction == null
                    || !TryFromPersistedTagState(transaction.Original, out var original)
                    || !TryFromPersistedTagState(transaction.Desired, out var desired))
                {
                    error = "The pending tag settings transaction is corrupt.";
                    return false;
                }

                var target = transaction.CommitDesired ? desired : original;
                var settingsStore = _definitions();
                var assignmentStore = _assignments();
                assignmentStore.ReloadFromDisk();

                if (!ProjectTagStateSnapshot.DefinitionsEqual(
                        settingsStore.GetOrderedTags(),
                        target.Definitions))
                {
                    settingsStore.ReplaceAll(target.Definitions);
                }

                // A journal that recorded no assignment change has nothing to
                // reconcile: reading and rewriting the whole table here would
                // REPLACE the live assignments with an empty one.
                if (!transaction.AssignmentsUnchanged)
                {
                    var currentAssignments = assignmentStore.GetAllTagIdsByGuid();
                    if (!AssignmentsEqual(currentAssignments, target.AssignmentsByGuid))
                    {
                        assignmentStore.ReplaceAll(target.AssignmentsByGuid);
                    }
                }

                Delete();
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static PersistedTagState ToPersistedTagState(
            ProjectTagStateSnapshot snapshot,
            bool includeAssignments = true)
        {
            var persisted = new PersistedTagState();
            for (var i = 0; i < snapshot.Definitions.Count; i++)
            {
                var definition = snapshot.Definitions[i];
                persisted.Definitions.Add(new PersistedTagDefinition
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    Color = definition.Color,
                    IsFavorite = definition.IsFavorite
                });
            }

            if (!includeAssignments)
            {
                return persisted;
            }

            foreach (var assignment in snapshot.AssignmentsByGuid)
            {
                var persistedAssignment = new PersistedGuidTagAssignment
                {
                    AssetGuid = assignment.Key
                };
                for (var i = 0; i < assignment.Value.Count; i++)
                {
                    persistedAssignment.TagIds.Add(assignment.Value[i]);
                }

                persisted.Assignments.Add(persistedAssignment);
            }

            persisted.Assignments.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.AssetGuid, right.AssetGuid));
            return persisted;
        }

        private static bool TryFromPersistedTagState(
            PersistedTagState persisted,
            out ProjectTagStateSnapshot snapshot)
        {
            snapshot = null;
            if (persisted?.Definitions == null
                || persisted.Definitions.Count == 0
                || persisted.Assignments == null)
            {
                return false;
            }

            var definitions = new ZepTag[persisted.Definitions.Count];
            var definitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < persisted.Definitions.Count; i++)
            {
                var definition = persisted.Definitions[i];
                if (definition == null
                    || string.IsNullOrWhiteSpace(definition.Id)
                    || !string.Equals(definition.Id, definition.Id.Trim(), StringComparison.Ordinal)
                    || !definitionIds.Add(definition.Id)
                    || string.IsNullOrWhiteSpace(definition.Name)
                    || !string.Equals(definition.Name, definition.Name.Trim(), StringComparison.Ordinal)
                    || definition.Color == ZepTagColor.None
                    || !Enum.IsDefined(typeof(ZepTagColor), definition.Color))
                {
                    return false;
                }

                definitions[i] = new ZepTag(
                    definition.Id,
                    definition.Name,
                    definition.Color,
                    definition.IsFavorite);
            }

            var assignments = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < persisted.Assignments.Count; i++)
            {
                var assignment = persisted.Assignments[i];
                if (assignment == null
                    || string.IsNullOrWhiteSpace(assignment.AssetGuid)
                    || !string.Equals(
                        assignment.AssetGuid,
                        assignment.AssetGuid.Trim().ToLowerInvariant(),
                        StringComparison.Ordinal)
                    || assignment.TagIds == null
                    || assignment.TagIds.Count == 0
                    || assignments.ContainsKey(assignment.AssetGuid))
                {
                    return false;
                }

                var tagIds = new string[assignment.TagIds.Count];
                var seenTagIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var tagIndex = 0; tagIndex < assignment.TagIds.Count; tagIndex++)
                {
                    var tagId = assignment.TagIds[tagIndex];
                    if (string.IsNullOrWhiteSpace(tagId)
                        || !string.Equals(tagId, tagId.Trim().ToLowerInvariant(), StringComparison.Ordinal)
                        || !seenTagIds.Add(tagId))
                    {
                        return false;
                    }

                    tagIds[tagIndex] = tagId;
                }

                assignments[assignment.AssetGuid] = tagIds;
            }

            snapshot = new ProjectTagStateSnapshot(definitions, assignments);
            return true;
        }

        private void Write(PersistedTagStateTransaction transaction)
        {
            _pending = true;
            var path = _journalPath();
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory ?? string.Empty);
            var tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, Serialize(transaction));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private void Delete()
        {
            CheckpointForTests?.Invoke("before-journal-delete");
            File.Delete(_journalPath());
            _pending = false;
        }

        private void TryDelete()
        {
            try
            {
                Delete();
            }
            catch (Exception ex)
            {
                // A committed journal is idempotent. Leaving it in place lets the next
                // capture finish cleanup without turning a successful settings change
                // into a command-history failure.
                _logWarning("[Zep] Could not clean the tag settings transaction: "
                    + ex.Message);
            }
        }

        // The member names and the numeric tag colour are the file format: they
        // match what earlier package versions wrote, so a journal left behind by
        // one version is recovered by the other.
        private static string Serialize(PersistedTagStateTransaction transaction)
        {
            var builder = new StringBuilder();
            builder.Append("{\n    \"CommitDesired\": ").Append(transaction.CommitDesired ? "true" : "false");
            builder.Append(",\n    \"AssignmentsUnchanged\": ").Append(transaction.AssignmentsUnchanged ? "true" : "false");
            builder.Append(",\n    \"Original\": ");
            AppendState(builder, transaction.Original);
            builder.Append(",\n    \"Desired\": ");
            AppendState(builder, transaction.Desired);
            builder.Append("\n}");
            return builder.ToString();
        }

        private static void AppendState(StringBuilder builder, PersistedTagState state)
        {
            builder.Append("{\n        \"Definitions\": [");
            for (var i = 0; i < state.Definitions.Count; i++)
            {
                var definition = state.Definitions[i];
                builder.Append(i == 0 ? "\n            " : ",\n            ");
                builder.Append("{\"Id\": ");
                JsonText.AppendString(builder, definition.Id);
                builder.Append(", \"Name\": ");
                JsonText.AppendString(builder, definition.Name);
                builder.Append(", \"Color\": ").Append((int)definition.Color);
                builder.Append(", \"IsFavorite\": ").Append(definition.IsFavorite ? "true" : "false");
                builder.Append('}');
            }

            builder.Append(state.Definitions.Count == 0 ? "]" : "\n        ]");
            builder.Append(",\n        \"Assignments\": [");
            for (var i = 0; i < state.Assignments.Count; i++)
            {
                var assignment = state.Assignments[i];
                builder.Append(i == 0 ? "\n            " : ",\n            ");
                builder.Append("{\"AssetGuid\": ");
                JsonText.AppendString(builder, assignment.AssetGuid);
                builder.Append(", \"TagIds\": ");
                JsonText.AppendStringArray(builder, assignment.TagIds);
                builder.Append('}');
            }

            builder.Append(state.Assignments.Count == 0 ? "]" : "\n        ]");
            builder.Append("\n    }");
        }

        // A member that is absent or of the wrong kind keeps its default, which is
        // what the previous reader did; the validation above then rejects the
        // state. Text that is not JSON throws, and recovery reports it.
        private static PersistedTagStateTransaction Deserialize(string json)
        {
            var root = JsonText.Parse(json);
            if (JsonText.AsObject(root) == null)
            {
                return null;
            }

            var transaction = new PersistedTagStateTransaction
            {
                CommitDesired = ReadBool(root, "CommitDesired"),
                AssignmentsUnchanged = ReadBool(root, "AssignmentsUnchanged")
            };
            ReadState(root, "Original", transaction.Original);
            ReadState(root, "Desired", transaction.Desired);
            return transaction;
        }

        private static void ReadState(object root, string name, PersistedTagState state)
        {
            if (!JsonText.TryGetMember(root, name, out var stateValue))
            {
                return;
            }

            if (JsonText.TryGetMember(stateValue, "Definitions", out var definitionsValue)
                && JsonText.AsArray(definitionsValue) is List<object> definitions)
            {
                for (var i = 0; i < definitions.Count; i++)
                {
                    var item = definitions[i];
                    if (JsonText.AsObject(item) == null)
                    {
                        state.Definitions.Add(null);
                        continue;
                    }

                    var definition = new PersistedTagDefinition
                    {
                        Id = ReadString(item, "Id"),
                        Name = ReadString(item, "Name"),
                        IsFavorite = ReadBool(item, "IsFavorite")
                    };
                    if (JsonText.TryGetMember(item, "Color", out var colorValue)
                        && JsonText.TryGetInt64(colorValue, out var color)
                        && color >= int.MinValue
                        && color <= int.MaxValue)
                    {
                        definition.Color = (ZepTagColor)(int)color;
                    }

                    state.Definitions.Add(definition);
                }
            }

            if (JsonText.TryGetMember(stateValue, "Assignments", out var assignmentsValue)
                && JsonText.AsArray(assignmentsValue) is List<object> assignments)
            {
                for (var i = 0; i < assignments.Count; i++)
                {
                    var item = assignments[i];
                    if (JsonText.AsObject(item) == null)
                    {
                        state.Assignments.Add(null);
                        continue;
                    }

                    var assignment = new PersistedGuidTagAssignment
                    {
                        AssetGuid = ReadString(item, "AssetGuid")
                    };
                    assignment.TagIds.AddRange(JsonText.ReadStringArray(item, "TagIds"));
                    state.Assignments.Add(assignment);
                }
            }
        }

        private static bool ReadBool(object obj, string name)
        {
            return JsonText.TryGetMember(obj, name, out var value) && value is bool flag && flag;
        }

        private static string ReadString(object obj, string name)
        {
            return JsonText.TryGetMember(obj, name, out var value)
                ? JsonText.AsString(value) ?? string.Empty
                : string.Empty;
        }

        private static bool AssignmentsEqual(
            IReadOnlyDictionary<string, IReadOnlyList<string>> left,
            IReadOnlyDictionary<string, IReadOnlyList<string>> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out var rightTagIds)
                    || entry.Value.Count != rightTagIds.Count)
                {
                    return false;
                }

                for (var i = 0; i < entry.Value.Count; i++)
                {
                    if (!string.Equals(entry.Value[i], rightTagIds[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
