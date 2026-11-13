using System;
using System.Collections.Generic;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.Domain;
using Zep.Core.State;
using static Zep.Core.Application.ZepPathHelpers;

namespace Zep.Core.Application
{
    // WHY: Tag/tint state and the tag-filter projection share a lifecycle —
    // every mutation (assign / clear / rename / delete) touches the same
    // three caches in lockstep. Bundling them removes a class of bugs where
    // the controller forgot to update one cache; the service makes the
    // invariants enforceable from one place.
    //
    // The service deliberately does NOT own the browse cache or projection
    // orchestration. CrossCacheCoordinator is the boundary: it applies local
    // tag state here, updates catalog search text, then refreshes only affected
    // browse parents. A back-reference to the controller would create a second
    // orchestration path.
    internal sealed class TagProjectionService
    {
        private readonly Dictionary<string, IReadOnlyList<ZepTag>> _tagsByPath =
            new Dictionary<string, IReadOnlyList<ZepTag>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ZepTagColor> _folderTintByPath =
            new Dictionary<string, ZepTagColor>(StringComparer.OrdinalIgnoreCase);
        // The raw assigned ids of every TAGGED FOLDER, in assignment order.
        // _tagsByPath cannot stand in: ResolveAssignedTags returns definition
        // order, and the folder tint is "the colour of the most recently
        // assigned tag" — a definition-order recompute would silently repaint
        // multi-tag folders on an unrelated rename. Bounded by tagged folders.
        private readonly Dictionary<string, IReadOnlyList<string>> _assignedTagIdsByFolder =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _tagCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _pathsByTag =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // The tag behind each id in _pathsByTag, so a search token can be
        // answered against the tags actually IN USE (tens of them) instead of
        // against every tagged path. Deliberately not the definition store:
        // an assignment can outlive its definition, and those paths were
        // searchable before this index existed.
        private readonly Dictionary<string, ZepTag> _tagsById =
            new Dictionary<string, ZepTag>(StringComparer.OrdinalIgnoreCase);

        // WHY: FolderMatchesSearch hits this once per folder per search; for a project
        // with hundreds of tagged folders and a per-keystroke search refresh that's a
        // lot of identical string allocations. Invalidated wherever _tagsByPath
        // mutates (RebuildCache / ApplyAssignment / ClearAssignment).
        private readonly Dictionary<string, string> _normalizedTagSearchTextByPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly IProjectAssetGateway _assetGateway;
        private readonly TagDefinitionStore _tagDefinitions;
        private IReadOnlyList<ZepTagFilterOption> _cachedFilterOptions = Array.Empty<ZepTagFilterOption>();

        internal TagProjectionService(IProjectAssetGateway assetGateway, TagDefinitionStore tagDefinitions)
        {
            _assetGateway = assetGateway ?? throw new ArgumentNullException(nameof(assetGateway));
            _tagDefinitions = tagDefinitions ?? throw new ArgumentNullException(nameof(tagDefinitions));
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<ZepTag>> TagsByPath => _tagsByPath;

        internal IReadOnlyList<ZepTagFilterOption> CachedFilterOptions => _cachedFilterOptions;

        internal int ObservedTagVersion { get; set; }

        internal void RebuildCache(IReadOnlyList<AssetRecord> records, IReadOnlyList<ZepFolderEntry> folders)
        {
            var __sw = ZepPerfLog.IsEnabled
                ? System.Diagnostics.Stopwatch.StartNew()
                : null;
            var recordAssignments = new IReadOnlyList<string>[records.Count];
            var folderAssignments = new IReadOnlyList<string>[folders.Count];

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var tagIds = _assetGateway.GetTagIds(
                    AssetPath.From(record.AssetPath),
                    record.Guid);
                recordAssignments[i] = tagIds == null || tagIds.Count == 0
                    ? Array.Empty<string>()
                    : CopyTagIds(tagIds);
            }

            for (var i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                var tagIds = _assetGateway.GetTagIds(
                    AssetPath.From(folder.Path),
                    folder.Guid);
                folderAssignments[i] = tagIds == null || tagIds.Count == 0
                    ? Array.Empty<string>()
                    : CopyTagIds(tagIds);
            }

            _tagsByPath.Clear();
            _folderTintByPath.Clear();
            _assignedTagIdsByFolder.Clear();
            _normalizedTagSearchTextByPath.Clear();
            _tagCounts.Clear();
            _pathsByTag.Clear();
            _tagsById.Clear();

            var tagSettings = _tagDefinitions;

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var tagIds = recordAssignments[i];
                if (tagIds.Count == 0)
                {
                    continue;
                }

                var tags = tagSettings.ResolveAssignedTags(tagIds);
                if (tags.Count > 0)
                {
                    SetResolvedTags(record.AssetPath, tags);
                }
            }

            for (var i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                var tagIds = folderAssignments[i];
                if (tagIds.Count == 0)
                {
                    continue;
                }

                if (!_tagsByPath.ContainsKey(folder.Path))
                {
                    var tags = tagSettings.ResolveAssignedTags(tagIds);
                    if (tags.Count > 0)
                    {
                        SetResolvedTags(folder.Path, tags);
                    }
                }

                _assignedTagIdsByFolder[folder.Path] = tagIds;
                var tintColor = ResolveMostRecentTagColor(tagIds, tagSettings);
                if (tintColor != ZepTagColor.None)
                {
                    _folderTintByPath[folder.Path] = tintColor;
                }
            }
            if (__sw != null)
            {
                __sw.Stop();
                ZepPerfLog.Log($"[Zep/perf]     TagProjectionService.RebuildCache records={records.Count} folders={folders.Count} taggedPaths={_tagsByPath.Count} folderTints={_folderTintByPath.Count} ms={__sw.Elapsed.TotalMilliseconds:F2}");
            }
        }

        // Re-resolves the tagged paths after a tag DEFINITION mutation and returns
        // them, so the caller can refresh exactly those records and folder buckets.
        //
        // WHY this is the complete affected set: a definition edit changes how an
        // assignment RESOLVES, it never creates one — and ResolveAssignedTags
        // fabricates a chip for an id that has no definition, so every path
        // carrying any assignment is already a key here. Deleting a definition is
        // the one case that also touches the store, and it can only REMOVE the
        // deleted id from paths that already carry it: that is what
        // <paramref name="unassignedTagIds"/> names. Nothing outside these keys
        // can change, so the record catalog is never enumerated.
        //
        // A replay that could ADD assignments (undo of a definition delete, which
        // restores the whole tag snapshot) is NOT this method's case; the caller
        // keeps the full RefreshTagPresentationCache for it.
        internal IReadOnlyList<string> RefreshResolvedTagsForTaggedPaths(
            IReadOnlyCollection<string> unassignedTagIds)
        {
            var affectedPaths = new List<string>(_tagsByPath.Count);
            foreach (var pair in _tagsByPath)
            {
                affectedPaths.Add(pair.Key);
            }

            var assignedIds = new List<string>();
            for (var i = 0; i < affectedPaths.Count; i++)
            {
                var path = affectedPaths[i];
                if (!_tagsByPath.TryGetValue(path, out var existingTags))
                {
                    continue;
                }

                assignedIds.Clear();
                for (var t = 0; t < existingTags.Count; t++)
                {
                    var id = existingTags[t].Id;
                    if (!IsUnassigned(unassignedTagIds, id))
                    {
                        assignedIds.Add(id);
                    }
                }

                SetResolvedTags(
                    path,
                    assignedIds.Count == 0
                        ? Array.Empty<ZepTag>()
                        : _tagDefinitions.ResolveAssignedTags(assignedIds));
                RefreshFolderTint(path, unassignedTagIds);
            }

            _normalizedTagSearchTextByPath.Clear();
            return affectedPaths;
        }

        private void RefreshFolderTint(
            string folderPath,
            IReadOnlyCollection<string> unassignedTagIds)
        {
            if (!_assignedTagIdsByFolder.TryGetValue(folderPath, out var assignedIds))
            {
                return;
            }

            // Assignment order is the tint rule, so the stored raw ids are
            // filtered in place rather than re-derived from the resolved tags.
            var remainingIds = new List<string>(assignedIds.Count);
            for (var i = 0; i < assignedIds.Count; i++)
            {
                if (!IsUnassigned(unassignedTagIds, assignedIds[i]))
                {
                    remainingIds.Add(assignedIds[i]);
                }
            }

            if (remainingIds.Count == 0)
            {
                _assignedTagIdsByFolder.Remove(folderPath);
                _folderTintByPath.Remove(folderPath);
                return;
            }

            _assignedTagIdsByFolder[folderPath] = remainingIds;
            var tintColor = ResolveMostRecentTagColor(remainingIds, _tagDefinitions);
            if (tintColor != ZepTagColor.None)
            {
                _folderTintByPath[folderPath] = tintColor;
            }
            else
            {
                _folderTintByPath.Remove(folderPath);
            }
        }

        private static bool IsUnassigned(
            IReadOnlyCollection<string> unassignedTagIds,
            string tagId)
        {
            if (unassignedTagIds == null || unassignedTagIds.Count == 0)
            {
                return false;
            }

            foreach (var unassigned in unassignedTagIds)
            {
                if (string.Equals(unassigned, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<string> CopyTagIds(IReadOnlyList<string> tagIds)
        {
            var copy = new string[tagIds.Count];
            for (var i = 0; i < tagIds.Count; i++)
            {
                copy[i] = tagIds[i];
            }

            return copy;
        }

        internal void RebuildFilterOptions()
        {
            var __sw = ZepPerfLog.IsEnabled
                ? System.Diagnostics.Stopwatch.StartNew()
                : null;
            _cachedFilterOptions = BuildTagFilterOptions(_tagCounts, _tagDefinitions);
            if (__sw != null)
            {
                __sw.Stop();
                ZepPerfLog.Log($"[Zep/perf]     TagProjectionService.RebuildFilterOptions options={_cachedFilterOptions.Count} ms={__sw.Elapsed.TotalMilliseconds:F2}");
            }
        }

        internal IReadOnlyList<ZepTag> GetTagsForPath(string assetPath)
        {
            return _tagsByPath.TryGetValue(assetPath, out var tags)
                ? tags
                : Array.Empty<ZepTag>();
        }

        // Pending first-paint entries are deliberately absent from the catalog.
        // Resolve their presentation through the same gateway/definition boundary
        // without mutating any authoritative tag projection state.
        internal void ResolveUnindexedPresentation(
            string assetPath,
            bool isFolder,
            out IReadOnlyList<ZepTag> tags,
            out ZepTagColor folderTintColor)
        {
            tags = Array.Empty<ZepTag>();
            folderTintColor = ZepTagColor.None;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            var tagIds = _assetGateway.GetTagIds(AssetPath.From(assetPath));
            if (tagIds == null || tagIds.Count == 0)
            {
                return;
            }

            tags = _tagDefinitions.ResolveAssignedTags(tagIds);
            if (isFolder)
            {
                folderTintColor = ResolveMostRecentTagColor(tagIds, _tagDefinitions);
            }
        }

        internal ZepTagColor GetFolderTintColorForPath(string assetPath, string typeName)
        {
            if (!IsFolderTypeName(typeName) || string.IsNullOrWhiteSpace(assetPath))
            {
                return ZepTagColor.None;
            }

            return _folderTintByPath.TryGetValue(assetPath, out var color)
                ? color
                : ZepTagColor.None;
        }

        internal bool MatchesTagFilter(string assetPath, string selectedTagFilterId)
        {
            if (string.IsNullOrWhiteSpace(selectedTagFilterId))
            {
                return true;
            }

            if (!_tagsByPath.TryGetValue(assetPath, out var tags) || tags == null)
            {
                return false;
            }

            for (var i = 0; i < tags.Count; i++)
            {
                if (string.Equals(tags[i].Id, selectedTagFilterId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal IEnumerable<string> EnumeratePathsForTag(string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId)
                || !_pathsByTag.TryGetValue(tagId, out var paths))
            {
                yield break;
            }

            foreach (var path in paths)
            {
                yield return path;
            }
        }

        // Adds only paths whose current Finder-tag text can satisfy a positive
        // folder-search term. FolderSearchIndex unions these with its metadata
        // postings for unqualified terms, preserving the grammar rule that a
        // plain word may match either a folder name/label or a Zep tag.
        // Work follows tagged paths, never the complete folder catalog.
        internal void AppendPathsMatchingSearchToken(
            SearchToken token,
            ISet<string> destination)
        {
            if (destination == null || !IsTagSearchableToken(token))
            {
                return;
            }

            // The tens of tags in use decide this, not the tens of thousands
            // of tagged paths: a token can only reach a path through one of
            // the path's tags, and _pathsByTag is that relation, already
            // inverted (AdjustTagMembership keeps both).
            foreach (var pair in _tagsById)
            {
                if (!TagMatchesSearchToken(pair.Value, token)
                    || !_pathsByTag.TryGetValue(pair.Key, out var paths))
                {
                    continue;
                }

                foreach (var path in paths)
                {
                    destination.Add(path);
                }
            }
        }

        // Whether any path carries a tag this token can satisfy. Lets a caller
        // skip allocating the union set for the ordinary case: a search word
        // that is nobody's tag name or colour.
        internal bool HasPathsMatchingSearchToken(SearchToken token)
        {
            if (!IsTagSearchableToken(token))
            {
                return false;
            }

            foreach (var pair in _tagsById)
            {
                if (TagMatchesSearchToken(pair.Value, token)
                    && _pathsByTag.TryGetValue(pair.Key, out var paths)
                    && paths.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTagSearchableToken(SearchToken token)
        {
            return (token.Field == SearchField.Any || token.Field == SearchField.Tag)
                && (token.Field != SearchField.Any
                    || token.MatchMode != SearchMatchMode.Exact);
        }

        // The same two fields GetNormalizedTagSearchText writes per path, read
        // off the definition instead. The per-path text joins them with '\n'
        // and search tokens never contain one, so a match there is always a
        // match within a single field.
        private static bool TagMatchesSearchToken(ZepTag tag, SearchToken token)
        {
            if (TagFieldMatchesSearchToken(tag.Name, token))
            {
                return true;
            }

            var color = tag.Color.ToString();
            return !string.Equals(
                    color,
                    ZepTagColor.None.ToString(),
                    StringComparison.Ordinal)
                && TagFieldMatchesSearchToken(color, token);
        }

        private static bool TagFieldMatchesSearchToken(string value, SearchToken token)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = AssetRecord.NormalizeText(value);
            return token.MatchMode == SearchMatchMode.Exact
                ? string.Equals(normalized, token.Value, StringComparison.Ordinal)
                : normalized.IndexOf(token.Value, StringComparison.Ordinal) >= 0;
        }

        internal string ResolveSelectedTagFilterId(string selectedTagFilterId)
        {
            if (string.IsNullOrWhiteSpace(selectedTagFilterId))
            {
                return string.Empty;
            }

            for (var i = 0; i < _cachedFilterOptions.Count; i++)
            {
                if (string.Equals(_cachedFilterOptions[i].TagId, selectedTagFilterId, StringComparison.OrdinalIgnoreCase))
                {
                    return _cachedFilterOptions[i].TagId;
                }
            }

            return string.Empty;
        }

        // WHY: Controller calls this per asset path inside ApplyTagAssignmentsLocally.
        // Empty/null normalizedTagIds clears both caches (handled in ClearAssignment).
        internal void ApplyAssignment(string assetPath, IReadOnlyList<string> normalizedTagIds, bool isFolder)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            if (normalizedTagIds == null || normalizedTagIds.Count == 0)
            {
                ClearAssignment(assetPath);
                return;
            }

            var tagSettings = _tagDefinitions;
            SetResolvedTags(
                assetPath,
                tagSettings.ResolveAssignedTags(normalizedTagIds));
            _normalizedTagSearchTextByPath.Remove(assetPath);

            if (isFolder)
            {
                _assignedTagIdsByFolder[assetPath] = CopyTagIds(normalizedTagIds);
                var tintColor = ResolveMostRecentTagColor(normalizedTagIds, tagSettings);
                if (tintColor != ZepTagColor.None)
                {
                    _folderTintByPath[assetPath] = tintColor;
                }
                else
                {
                    _folderTintByPath.Remove(assetPath);
                }
            }
        }

        internal void ClearAssignment(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            if (_tagsByPath.TryGetValue(assetPath, out var existingTags))
            {
                AdjustTagMembership(assetPath, existingTags, -1);
                _tagsByPath.Remove(assetPath);
            }
            _folderTintByPath.Remove(assetPath);
            _assignedTagIdsByFolder.Remove(assetPath);
            _normalizedTagSearchTextByPath.Remove(assetPath);
        }

        internal void ApplyCatalogDelta(AssetCatalogDelta delta)
        {
            if (delta == null || delta.IsEmpty)
            {
                return;
            }

            var recordAssignments = new IReadOnlyList<string>[delta.UpsertedRecords.Count];
            var folderAssignments = new IReadOnlyList<string>[delta.UpsertedFolders.Count];

            // Acquire every assignment before changing any local state. A gateway
            // refusal then leaves the complete previous projection available to the
            // caller while recovery is pending.
            for (var i = 0; i < delta.UpsertedRecords.Count; i++)
            {
                recordAssignments[i] = CaptureAssignment(
                    delta.UpsertedRecords[i].AssetPath);
            }

            for (var i = 0; i < delta.UpsertedFolders.Count; i++)
            {
                folderAssignments[i] = CaptureAssignment(
                    delta.UpsertedFolders[i].FolderPath.Value);
            }

            for (var i = 0; i < delta.RemovedRecords.Count; i++)
            {
                ClearAssignment(delta.RemovedRecords[i].AssetPath);
            }

            for (var i = 0; i < delta.RemovedFolders.Count; i++)
            {
                ClearAssignment(delta.RemovedFolders[i].FolderPath.Value);
            }

            for (var i = 0; i < delta.UpsertedRecords.Count; i++)
            {
                RefreshAssignment(
                    delta.UpsertedRecords[i].AssetPath,
                    recordAssignments[i],
                    isFolder: false);
            }

            for (var i = 0; i < delta.UpsertedFolders.Count; i++)
            {
                RefreshAssignment(
                    delta.UpsertedFolders[i].FolderPath.Value,
                    folderAssignments[i],
                    isFolder: true);
            }

            RebuildFilterOptions();
        }

        private IReadOnlyList<string> CaptureAssignment(string assetPath)
        {
            var tagIds = _assetGateway.GetTagIds(AssetPath.From(assetPath));
            return tagIds == null || tagIds.Count == 0
                ? Array.Empty<string>()
                : CopyTagIds(tagIds);
        }

        private void RefreshAssignment(
            string assetPath,
            IReadOnlyList<string> tagIds,
            bool isFolder)
        {
            if (tagIds == null || tagIds.Count == 0)
            {
                ClearAssignment(assetPath);
                return;
            }

            ApplyAssignment(assetPath, tagIds, isFolder);
        }

        private void SetResolvedTags(
            string assetPath,
            IReadOnlyList<ZepTag> tags)
        {
            if (_tagsByPath.TryGetValue(assetPath, out var existingTags))
            {
                AdjustTagMembership(assetPath, existingTags, -1);
            }

            if (tags == null || tags.Count == 0)
            {
                _tagsByPath.Remove(assetPath);
                return;
            }

            _tagsByPath[assetPath] = tags;
            AdjustTagMembership(assetPath, tags, 1);
        }

        private void AdjustTagMembership(
            string assetPath,
            IReadOnlyList<ZepTag> tags,
            int delta)
        {
            if (tags == null)
            {
                return;
            }

            for (var i = 0; i < tags.Count; i++)
            {
                var id = tags[i].Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                _tagCounts.TryGetValue(id, out var count);
                count += delta;
                if (count <= 0)
                {
                    _tagCounts.Remove(id);
                }
                else
                {
                    _tagCounts[id] = count;
                }

                if (delta > 0)
                {
                    if (!_pathsByTag.TryGetValue(id, out var paths))
                    {
                        paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _pathsByTag[id] = paths;
                    }

                    paths.Add(assetPath);
                    // Last writer wins, matching the per-path text this
                    // replaces: it also read whatever name the most recent
                    // assignment resolved.
                    _tagsById[id] = tags[i];
                }
                else if (_pathsByTag.TryGetValue(id, out var existingPaths))
                {
                    existingPaths.Remove(assetPath);
                    if (existingPaths.Count == 0)
                    {
                        _pathsByTag.Remove(id);
                        _tagsById.Remove(id);
                    }
                }
            }
        }

        internal bool FolderMatchesSearch(ZepFolderEntry folder, ParsedSearchQuery searchQuery)
        {
            if (searchQuery == null || searchQuery.IsEmpty)
            {
                return true;
            }

            // ZepFolderEntry precomputes these at construction exactly
            // for this loop — lowercasing here allocated two strings per folder
            // per keystroke.
            var name = folder.NormalizedName;
            var path = folder.NormalizedPath;
            var normalizedTagSearchText = GetNormalizedTagSearchText(folder.Path);
            return SearchQuery.Matches(
                SearchDocument.CreateNormalizedFolder(
                    name,
                    path,
                    folder.NormalizedGuid,
                    normalizedTagSearchText,
                    folder.NormalizedUnityLabels,
                    folder.NormalizedUnityLabelSearchText),
                searchQuery);
        }

        private string GetNormalizedTagSearchText(string assetPath)
        {
            if (_normalizedTagSearchTextByPath.TryGetValue(assetPath, out var cached))
            {
                return cached;
            }

            if (!_tagsByPath.TryGetValue(assetPath, out var tags) || tags == null || tags.Count == 0)
            {
                _normalizedTagSearchTextByPath[assetPath] = string.Empty;
                return string.Empty;
            }

            var parts = new List<string>(tags.Count * 2);
            for (var i = 0; i < tags.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(tags[i].Name))
                {
                    parts.Add(tags[i].Name);
                }

                var color = tags[i].Color.ToString();
                if (!string.IsNullOrWhiteSpace(color)
                    && !string.Equals(
                        color,
                        ZepTagColor.None.ToString(),
                        StringComparison.Ordinal))
                {
                    parts.Add(color);
                }
            }

            var built = AssetRecord.NormalizeText(string.Join("\n", parts));
            _normalizedTagSearchTextByPath[assetPath] = built;
            return built;
        }

        private static ZepTagColor ResolveMostRecentTagColor(
            IReadOnlyList<string> rawTagIds,
            TagDefinitionStore tagSettings)
        {
            if (rawTagIds == null || rawTagIds.Count == 0 || tagSettings == null)
            {
                return ZepTagColor.None;
            }

            for (var i = rawTagIds.Count - 1; i >= 0; i--)
            {
                if (tagSettings.TryGetTag(rawTagIds[i], out var tag))
                {
                    return tag.Color;
                }
            }

            return ZepTagColor.None;
        }

        private static IReadOnlyList<ZepTagFilterOption> BuildTagFilterOptions(
            IReadOnlyDictionary<string, int> tagCounts,
            TagDefinitionStore tagDefinitions)
        {
            var orderedOptions = new List<ZepTagFilterOption>();
            var orderedTags = tagDefinitions.GetOrderedTags();
            for (var i = 0; i < orderedTags.Count; i++)
            {
                var orderedTag = orderedTags[i];
                tagCounts.TryGetValue(orderedTag.Id, out var count);
                // Only surface a tag when it's a favorite or actually in use. Without this,
                // every non-favorite definition reappeared in the sidebar the moment ANY
                // asset anywhere got tagged (tagsByPath becomes non-empty) — inconsistent with
                // the tagsByPath-empty branch above, which shows favorites only.
                if (!orderedTag.IsFavorite && count == 0)
                {
                    continue;
                }

                orderedOptions.Add(new ZepTagFilterOption(orderedTag, count));
            }

            return orderedOptions;
        }
    }
}
