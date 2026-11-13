using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;

namespace Zep.Core.Application
{
    internal enum ExternalChangeApplicationResult
    {
        RequiresFullRebuild,
        NoChanges,
        Changed,
    }

    internal readonly struct ProjectAssetOperationUpdate
    {
        internal ProjectAssetOperationUpdate(string resultPath, string previousPath)
        {
            ResultPath = resultPath;
            PreviousPath = previousPath;
        }

        internal string ResultPath { get; }

        internal string PreviousPath { get; }
    }

    // Translates Unity/path boundary results into one catalog transaction, then
    // fans the resulting delta out to focused derived owners. It no longer owns
    // a second mutation path or decides that project-wide rebuilding is cheaper.
    internal sealed class CrossCacheCoordinator
    {
        private readonly InMemoryAssetIndex _index;
        private readonly AssetSearchService _searchService;
        private readonly IAssetRecordSource _source;
        private readonly IProjectAssetGateway _assetGateway;
        private readonly TagDefinitionStore _tagDefinitions;
        private readonly AssetPresentationCache _presentationCache;
        private readonly TagProjectionService _tagProjection;
        private readonly BrowseProjectionService _browseProjection;
        private readonly FolderEntriesBuilder _folderEntries;
        private readonly Action<AssetCatalogDelta> _onCatalogChanged;

        internal CrossCacheCoordinator(
            InMemoryAssetIndex index,
            AssetSearchService searchService,
            IAssetRecordSource source,
            IProjectAssetGateway assetGateway,
            TagDefinitionStore tagDefinitions,
            AssetPresentationCache presentationCache,
            TagProjectionService tagProjection,
            BrowseProjectionService browseProjection,
            FolderEntriesBuilder folderEntries,
            Action<AssetCatalogDelta> onCatalogChanged = null)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _assetGateway = assetGateway ?? throw new ArgumentNullException(nameof(assetGateway));
            _tagDefinitions = tagDefinitions ?? throw new ArgumentNullException(nameof(tagDefinitions));
            _presentationCache = presentationCache ?? throw new ArgumentNullException(nameof(presentationCache));
            _tagProjection = tagProjection ?? throw new ArgumentNullException(nameof(tagProjection));
            _browseProjection = browseProjection ?? throw new ArgumentNullException(nameof(browseProjection));
            _folderEntries = folderEntries ?? throw new ArgumentNullException(nameof(folderEntries));
            _onCatalogChanged = onCatalogChanged;
        }

        internal AssetCatalogDelta ApplyDirectFolderObservation(
            DirectFolderObservation observation,
            int catalogVersionAtStart)
        {
            var delta = _index.ApplyDirectObservation(
                observation,
                catalogVersionAtStart);
            if (delta.IsEmpty)
            {
                return delta;
            }

            // Search/tag owners intentionally ignore pending entries: filesystem
            // facts become searchable metadata only after Unity promotes them.
            _folderEntries.ApplyCatalogDelta(delta);
            _browseProjection.ApplyCatalogDelta(delta);
            _onCatalogChanged?.Invoke(delta);
            return delta;
        }

        internal void ApplyTagAssignmentsLocally(
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments)
        {
            var tagVersion = _assetGateway.TagVersion;
            if (tagAssignments == null || tagAssignments.Count == 0)
            {
                _tagProjection.ObservedTagVersion = tagVersion;
                return;
            }

            var recordUpserts = new List<AssetRecord>();
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in tagAssignments)
            {
                var path = AssetPathFormat.Normalize(assignment.Key);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var normalizedTagIds = ZepTagIdHelpers.NormalizeTagIds(assignment.Value);
                var isFolder = _index.TryGetFolder(path, out _)
                    || _assetGateway.IsFolder(AssetPath.From(path));
                _tagProjection.ApplyAssignment(path, normalizedTagIds, isFolder);
                parents.Add(ZepPathHelpers.GetParentFolder(path));
                _presentationCache.Remove(path);

                if (!isFolder && _index.TryGetByPath(path, out var record))
                {
                    var tagSearchText = _tagDefinitions.BuildTagSearchText(normalizedTagIds);
                    if (!string.Equals(record.TagSearchText, tagSearchText, StringComparison.Ordinal))
                    {
                        recordUpserts.Add(AssetRecord.WithTagSearchText(record, tagSearchText));
                    }
                }
            }

            if (recordUpserts.Count > 0)
            {
                var delta = _index.ApplyFileChanges(
                    recordUpserts,
                    Array.Empty<string>());
                _searchService.ApplyCatalogDelta(delta);
            }

            _tagProjection.RebuildFilterOptions();
            _browseProjection.RefreshParentFolders(parents);
            _tagProjection.ObservedTagVersion = tagVersion;
        }

        internal void ApplyIncrementalOperationUpdate(
            string resultPath,
            string previousPath)
        {
            ApplyIncrementalOperationUpdates(new[]
            {
                new ProjectAssetOperationUpdate(resultPath, previousPath),
            });
        }

        internal void ApplyIncrementalOperationUpdates(
            IReadOnlyList<ProjectAssetOperationUpdate> updates)
        {
            if (updates == null || updates.Count == 0)
            {
                return;
            }

            if (updates.Count > 1)
            {
                ZepPerfLog.Count(ZepPerfCounter.BulkCatalogApplications);
            }
            var removed = new List<string>();
            var moves = new List<AssetCatalogMove>();
            var records = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<string, AssetFolderRecord>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < updates.Count; i++)
            {
                var resultPath = AssetPathFormat.Normalize(updates[i].ResultPath);
                var previousPath = AssetPathFormat.Normalize(updates[i].PreviousPath);
                var resultIsProjectPath = AssetPathClassification.IsProjectAssetPath(resultPath)
                    && !Path.IsPathRooted(resultPath);
                var previousIsProjectPath = AssetPathClassification.IsProjectAssetPath(previousPath)
                    && !Path.IsPathRooted(previousPath);
                var isCreate = resultIsProjectPath
                    && string.Equals(
                        previousPath,
                        ZepPathHelpers.GetParentFolder(resultPath),
                        StringComparison.OrdinalIgnoreCase);

                if (previousIsProjectPath && !isCreate)
                {
                    if (resultIsProjectPath)
                    {
                        moves.Add(new AssetCatalogMove(previousPath, resultPath));
                    }
                    else
                    {
                        removed.Add(previousPath);
                    }
                }

                if (resultIsProjectPath)
                {
                    AddPathObservation(
                        resultPath,
                        records,
                        folders,
                        previousIsProjectPath
                            && _index.TryGetFolder(previousPath, out _),
                        out _);
                }
            }

            ApplyCatalogChange(
                removed,
                moves,
                new List<AssetRecord>(records.Values),
                new List<AssetFolderRecord>(folders.Values));
        }

        internal bool ApplyExternalChanges(ProjectAssetChanges changes)
        {
            var result = ApplyExternalChangesDetailed(changes, out var unresolvedPaths);
            return unresolvedPaths.Count == 0
                && (result == ExternalChangeApplicationResult.NoChanges
                    || result == ExternalChangeApplicationResult.Changed);
        }

        // Applies everything the source boundary could resolve and reports the
        // paths it could not, which the caller turns into authoritative recovery.
        //
        // WHY the partial commit is safe: the applied subset is COMPLETE FOR ITS
        // OWN PATHS. A resolved import carries the record the source built for it,
        // a deletion needs no resolution at all, and a resolved move carries both
        // ends — so every path in the transaction lands in exactly the state the
        // batch describes. An unresolved path contributes nothing: its move is not
        // recorded either, so the catalog keeps the stale entry rather than losing
        // it, and reconciliation is what corrects it. (The older rule discarded the
        // whole batch, which cost a full project reconciliation for one package
        // folder Unity reported at a virtual path.)
        internal ExternalChangeApplicationResult ApplyExternalChangesDetailed(
            ProjectAssetChanges changes,
            out IReadOnlyList<string> unresolvedPaths)
        {
            changes = changes.Normalize();
            var records = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<string, AssetFolderRecord>(StringComparer.OrdinalIgnoreCase);
            List<string> unresolved = null;

            for (var i = 0; i < changes.Imported.Length; i++)
            {
                AddPathObservation(
                    changes.Imported[i],
                    records,
                    folders,
                    false,
                    out var resolved);
                if (!resolved)
                {
                    (unresolved ??= new List<string>()).Add(changes.Imported[i]);
                }
            }

            var moves = new List<AssetCatalogMove>(changes.Moved.Length);
            for (var i = 0; i < changes.Moved.Length; i++)
            {
                var movedFrom = i < changes.MovedFromPaths.Length
                    ? changes.MovedFromPaths[i]
                    : string.Empty;
                AddPathObservation(
                    changes.Moved[i],
                    records,
                    folders,
                    _index.TryGetFolder(movedFrom, out _),
                    out var resolved);
                if (resolved)
                {
                    moves.Add(new AssetCatalogMove(movedFrom, changes.Moved[i]));
                    continue;
                }

                // Both ends belong to recovery: the destination could not be
                // built, so removing the source would delete a row nothing can
                // put back.
                (unresolved ??= new List<string>()).Add(changes.Moved[i]);
                if (!string.IsNullOrEmpty(movedFrom))
                {
                    unresolved.Add(movedFrom);
                }
            }

            unresolvedPaths = unresolved ?? (IReadOnlyList<string>)Array.Empty<string>();
            var delta = ApplyCatalogChange(
                changes.Deleted,
                moves,
                new List<AssetRecord>(records.Values),
                new List<AssetFolderRecord>(folders.Values));
            if (delta != null && !delta.IsEmpty)
            {
                return ExternalChangeApplicationResult.Changed;
            }

            return ExternalChangeApplicationResult.NoChanges;
        }

        private void AddPathObservation(
            string path,
            Dictionary<string, AssetRecord> records,
            Dictionary<string, AssetFolderRecord> folders,
            bool assumeFolder,
            out bool resolved)
        {
            resolved = false;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var typedPath = AssetPath.From(path);
            var record = _source.BuildRecord(typedPath);
            if (record != null)
            {
                records[record.AssetPath] = record;
                resolved = true;
                return;
            }

            var folder = _source.BuildFolderRecord(typedPath);
            if (folder == null && (assumeFolder || IsFolderPath(path)))
            {
                folder = new AssetFolderRecord(typedPath);
            }

            if (folder != null)
            {
                folders[folder.FolderPath.Value] = folder;
                resolved = true;
            }
        }

        private AssetCatalogDelta ApplyCatalogChange(
            IReadOnlyList<string> removed,
            IReadOnlyList<AssetCatalogMove> moves,
            IReadOnlyList<AssetRecord> records,
            IReadOnlyList<AssetFolderRecord> folders)
        {
            var delta = _index.Apply(removed, moves, records, folders);
            PublishIndexPatch(delta);
            return delta;
        }

        // The publication half of ApplyCatalogChange, reachable by a second
        // caller. A change applied to the index somewhere ELSE still has to
        // reach the derived caches and the view: the post-reload type
        // reconciliation rebuilds its slices straight into the index (Bridge)
        // and hands the resulting delta here, so each slice shows up without
        // waiting for a projection-cache rebuild at the end of the operation.
        // Deliberately not a second copy of the list below — a copy that drifts
        // would silently change the external-change path too.
        internal void PublishIndexPatch(AssetCatalogDelta delta)
        {
            if (delta == null || delta.IsEmpty)
            {
                return;
            }

            _searchService.ApplyCatalogDelta(delta);
            _tagProjection.ApplyCatalogDelta(delta);
            _folderEntries.ApplyCatalogDelta(delta);
            _browseProjection.ApplyCatalogDelta(delta);
            InvalidatePresentation(delta);
            _onCatalogChanged?.Invoke(delta);
        }

        private void InvalidatePresentation(AssetCatalogDelta delta)
        {
            for (var i = 0; i < delta.RemovedRecords.Count; i++)
            {
                _presentationCache.Remove(delta.RemovedRecords[i].AssetPath);
            }

            for (var i = 0; i < delta.UpsertedRecords.Count; i++)
            {
                _presentationCache.Remove(delta.UpsertedRecords[i].AssetPath);
            }

            for (var i = 0; i < delta.RemovedFolders.Count; i++)
            {
                _presentationCache.Remove(delta.RemovedFolders[i].FolderPath.Value);
            }

            for (var i = 0; i < delta.UpsertedFolders.Count; i++)
            {
                _presentationCache.Remove(delta.UpsertedFolders[i].FolderPath.Value);
            }

            foreach (var parentFolder in delta.AffectedParentFolders)
            {
                _presentationCache.Remove(parentFolder);
            }
        }

        private bool IsFolderPath(string assetPath)
        {
            return ZepPathHelpers.IsFolderAssetPath(
                _assetGateway,
                assetPath);
        }
    }
}
