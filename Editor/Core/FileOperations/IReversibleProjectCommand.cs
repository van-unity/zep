namespace Zep.Core.FileOperations
{
    public interface IReversibleProjectCommand
    {
        ProjectFileOperationResult Execute(IProjectAssetGateway gateway);

        ProjectFileOperationResult Undo(IProjectAssetGateway gateway);

        // Snapshot the command into a persistable record. Captures both
        // constructor state and post-execute fields (e.g. _currentPath) so the
        // record can be rehydrated into an equivalent command after a domain
        // reload wipes the in-memory history.
        ProjectCommandRecord ToRecord();
    }
}
