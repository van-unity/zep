using Zep.Core.Domain;

namespace Zep.Core.FileOperations
{
    internal sealed class DeleteCommand : IReversibleProjectCommand
    {
        private readonly AssetPath _originalPath;
        private readonly string _assetGuid;
        private AssetPath _payloadPath;
        // Set only by ProjectFileCommandService, which reads the GUID from the
        // same gateway immediately before constructing the command. Nothing
        // runs in between, so the first forward Execute would re-read the
        // .meta file just to compare a value with itself. Consumed on that
        // first Execute: a redo of this instance, and every rehydrated
        // (FromRecord) replay, validates exactly as before. Ignored when the
        // GUID is empty, so "can no longer be identified safely" is still
        // reported where it was.
        private bool _identityVerified;

        internal DeleteCommand(
            AssetPath originalPath,
            string assetGuid,
            bool identityVerified = false)
        {
            _originalPath = originalPath;
            _assetGuid = assetGuid ?? string.Empty;
            _identityVerified = identityVerified && !string.IsNullOrWhiteSpace(_assetGuid);
        }

        public ProjectCommandRecord ToRecord()
        {
            return new ProjectCommandRecord
            {
                Kind = ProjectCommandKind.Delete,
                PrimaryPath = _originalPath,
                SecondaryPath = _payloadPath,
                AssetGuid = _assetGuid,
            };
        }

        internal static DeleteCommand FromRecord(ProjectCommandRecord record)
        {
            var command = new DeleteCommand(AssetPath.From(record.PrimaryPath), record.AssetGuid);
            command._payloadPath = AssetPath.From(record.SecondaryPath);
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

            var result = _payloadPath.IsEmpty
                ? gateway.StoreUndoPayload(_originalPath)
                : gateway.StoreUndoPayload(_originalPath, _payloadPath);
            if (result.Succeeded)
            {
                _payloadPath = AssetPath.From(result.AssetPath);
            }

            return result;
        }

        public ProjectFileOperationResult Undo(IProjectAssetGateway gateway)
        {
            var identityFailure = ProjectCommandIdentity.Validate(gateway, _payloadPath, _assetGuid);
            if (identityFailure != null)
            {
                return identityFailure;
            }

            var originalParent = AssetPath.From(AssetPathFormat.GetParentFolder(_originalPath));
            if (!originalParent.IsEmpty)
            {
                gateway.EnsureFolder(originalParent);
            }

            return gateway.RestoreUndoPayload(_payloadPath, _originalPath);
        }
    }
}
