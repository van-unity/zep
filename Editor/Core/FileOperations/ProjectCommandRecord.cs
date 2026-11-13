using System.Collections.Generic;
using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    // Serializable snapshot of a reversible command, persisted through
    // SessionState so the undo/redo stacks survive the domain reload that
    // asset operations trigger. Public fields + [Serializable] because Unity's
    // JsonUtility (used by SessionUndoHistoryStorage) only serializes those.
    public enum ProjectCommandKind
    {
        CreateFolder,
        Rename,
        Duplicate,
        Move,
        Delete,
        SetTags,
        CreateAsset,
        SetTagDefinitions
    }

    [System.Serializable]
    public sealed class ProjectCommandTagAssignment
    {
        public string AssetPath;
        public string AssetGuid;
        public List<string> TagIds = new List<string>();
    }

    [System.Serializable]
    public sealed class ProjectCommandTagDefinition
    {
        public string Id;
        public string Name;
        public ZepTagColor Color;
        public bool IsFavorite;
    }

    [System.Serializable]
    public sealed class ProjectCommandGuidTagAssignment
    {
        public string AssetGuid;
        public List<string> TagIds = new List<string>();
    }

    [System.Serializable]
    public sealed class ProjectCommandRecord
    {
        public ProjectCommandKind Kind;
        public string PrimaryPath;
        public string SecondaryPath;
        public string TertiaryPath;
        public string Name;
        public string AssetGuid;
        public bool IsFolder;
        public List<ProjectCommandTagAssignment> DesiredTags = new List<ProjectCommandTagAssignment>();
        public List<ProjectCommandTagAssignment> OriginalTags = new List<ProjectCommandTagAssignment>();
        public List<ProjectCommandTagDefinition> DesiredTagDefinitions = new List<ProjectCommandTagDefinition>();
        public List<ProjectCommandTagDefinition> OriginalTagDefinitions = new List<ProjectCommandTagDefinition>();
        public List<ProjectCommandGuidTagAssignment> DesiredGuidTagAssignments = new List<ProjectCommandGuidTagAssignment>();
        public List<ProjectCommandGuidTagAssignment> OriginalGuidTagAssignments = new List<ProjectCommandGuidTagAssignment>();

        // True when the two assignment lists above hold only the GUIDs whose tag
        // sets the edit actually moved (a rename or recolour moves none) instead
        // of the project's whole assignment table twice. Records written before
        // this field existed deserialize it as false and keep replaying as full
        // snapshots, which is what makes an in-flight session safe to reload.
        public bool AssignmentsAreDiff;

        // The private storage slot owned by this command, if any.
        internal string GetUndoPayloadPath()
        {
            switch (Kind)
            {
                case ProjectCommandKind.Delete:
                case ProjectCommandKind.CreateAsset:
                    return SecondaryPath;
                case ProjectCommandKind.CreateFolder:
                case ProjectCommandKind.Duplicate:
                    return TertiaryPath;
                default:
                    return string.Empty;
            }
        }

        // Rehydrate the concrete command after a domain reload. Counterpart to
        // IReversibleProjectCommand.ToRecord(); each command owns its FromRecord.
        public IReversibleProjectCommand ToCommand()
        {
            switch (Kind)
            {
                case ProjectCommandKind.CreateFolder:
                    return CreateFolderCommand.FromRecord(this);
                case ProjectCommandKind.Rename:
                    return RenameCommand.FromRecord(this);
                case ProjectCommandKind.Duplicate:
                    return DuplicateCommand.FromRecord(this);
                case ProjectCommandKind.Move:
                    return MoveCommand.FromRecord(this);
                case ProjectCommandKind.Delete:
                    return DeleteCommand.FromRecord(this);
                case ProjectCommandKind.SetTags:
                    return SetTagsCommand.FromRecord(this);
                case ProjectCommandKind.CreateAsset:
                    return CreateAssetCommand.FromRecord(this);
                case ProjectCommandKind.SetTagDefinitions:
                    return SetTagDefinitionsCommand.FromRecord(this);
                default:
                    return null;
            }
        }
    }
}
