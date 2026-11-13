using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.State;

namespace Zep.Core.Application
{
internal sealed class TagMutationOrchestrator
{
	private readonly IProjectAssetGateway _assetGateway;

	private readonly ProjectFileCommandService _fileCommands;

	private readonly CrossCacheCoordinator _crossCache;

	private readonly TagDefinitionStore _tagDefinitions;

	private readonly Action<string> _setStatus;

	private readonly Action _applyProjection;

	private readonly Action _reconcileTagAssignments;

	private readonly Action<ProjectTagStateUnavailableException> _reportTagStateUnavailable;

	internal TagMutationOrchestrator(IProjectAssetGateway assetGateway, ProjectFileCommandService fileCommands, CrossCacheCoordinator crossCache, TagDefinitionStore tagDefinitions, Action<string> setStatus, Action applyProjection, Action reconcileTagAssignments, Action<ProjectTagStateUnavailableException> reportTagStateUnavailable)
	{
		_assetGateway = assetGateway ?? throw new ArgumentNullException(nameof(assetGateway));
		_fileCommands = fileCommands ?? throw new ArgumentNullException(nameof(fileCommands));
		_crossCache = crossCache ?? throw new ArgumentNullException(nameof(crossCache));
		_tagDefinitions = tagDefinitions ?? throw new ArgumentNullException(nameof(tagDefinitions));
		_setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
		_applyProjection = applyProjection ?? throw new ArgumentNullException(nameof(applyProjection));
		_reconcileTagAssignments = reconcileTagAssignments ?? throw new ArgumentNullException(nameof(reconcileTagAssignments));
		_reportTagStateUnavailable = reportTagStateUnavailable ?? throw new ArgumentNullException(nameof(reportTagStateUnavailable));
	}

	internal bool ApplyMutation(IReadOnlyList<string> assetPaths, string tagId, bool shouldAdd)
	{
		if (string.IsNullOrWhiteSpace(tagId))
		{
			_setStatus("Choose a tag first.");
			_applyProjection();
			return false;
		}
		int taggableSelectionCount;
		ProjectFileOperationResult preparationFailure;
		Dictionary<string, IReadOnlyList<string>> changedAssignments = BuildAssignments(assetPaths, (IReadOnlyList<string> existingTags) => shouldAdd ? ZepTagIdHelpers.AddTagId(existingTags, tagId) : ZepTagIdHelpers.RemoveTagId(existingTags, tagId), out taggableSelectionCount, out preparationFailure);
		if (preparationFailure != null)
		{
			return HandleResult(preparationFailure, changedAssignments, string.Empty);
		}
		if (taggableSelectionCount == 0)
		{
			_setStatus("No taggable items selected.");
			_applyProjection();
			return false;
		}
		if (changedAssignments.Count == 0)
		{
			_setStatus(shouldAdd ? "Selection already has that tag." : "Selection does not have that tag.");
			_applyProjection();
			return false;
		}
		string tagDisplayName = ResolveTagDisplayName(tagId, _tagDefinitions);
		string successMessage = (shouldAdd ? ("Added tag " + tagDisplayName) : ("Removed tag " + tagDisplayName));
		ProjectFileOperationResult result = _fileCommands.SetTags(changedAssignments);
		return HandleResult(result, changedAssignments, successMessage);
	}

	internal bool ClearAll(IReadOnlyList<string> assetPaths)
	{
		int taggableSelectionCount;
		ProjectFileOperationResult preparationFailure;
		Dictionary<string, IReadOnlyList<string>> changedAssignments = BuildAssignments(assetPaths, (IReadOnlyList<string> existingTags) => Array.Empty<string>(), out taggableSelectionCount, out preparationFailure);
		if (preparationFailure != null)
		{
			return HandleResult(preparationFailure, changedAssignments, string.Empty);
		}
		if (taggableSelectionCount == 0)
		{
			_setStatus("No taggable items selected.");
			_applyProjection();
			return false;
		}
		if (changedAssignments.Count == 0)
		{
			_setStatus("Selection already has no tags.");
			_applyProjection();
			return false;
		}
		ProjectFileOperationResult result = _fileCommands.SetTags(changedAssignments);
		return HandleResult(result, changedAssignments, (changedAssignments.Count == 1) ? "Removed tags" : "Removed tags from items");
	}

	internal bool ApplyUndoRedoResult(ProjectFileOperationResult result, string successMessage)
	{
		return HandleResult(result, result.TagAssignments, successMessage);
	}

	private Dictionary<string, IReadOnlyList<string>> BuildAssignments(IReadOnlyList<string> assetPaths, Func<IReadOnlyList<string>, IReadOnlyList<string>> transform, out int taggableSelectionCount, out ProjectFileOperationResult preparationFailure)
	{
		taggableSelectionCount = 0;
		preparationFailure = null;
		Dictionary<string, IReadOnlyList<string>> changedAssignments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		if (assetPaths == null || transform == null)
		{
			return changedAssignments;
		}
		var preparationComplete = false;
		HashSet<string> seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < assetPaths.Count; i++)
		{
			string normalizedPath = AssetPathFormat.Normalize(assetPaths[i]);
			if (!string.IsNullOrWhiteSpace(normalizedPath) && seenPaths.Add(normalizedPath) && _assetGateway.CanTag(AssetPath.From(normalizedPath)))
			{
				taggableSelectionCount++;
				try
				{
					if (!preparationComplete)
					{
						var timelinePreparation = _fileCommands.PrepareRecordedMutation();
						if (!timelinePreparation.Succeeded)
						{
							preparationFailure = timelinePreparation;
							return changedAssignments;
						}

						var assignmentPreparation = _fileCommands.PrepareTagAssignmentsForMutation();
						if (!assignmentPreparation.Succeeded)
						{
							preparationFailure = assignmentPreparation;
							return changedAssignments;
						}

						_reconcileTagAssignments();
						preparationComplete = true;
					}

					IReadOnlyList<string> tagIds = _assetGateway.GetTagIds(AssetPath.From(normalizedPath));
					IReadOnlyList<string> transformedTagIds = transform(tagIds) ?? Array.Empty<string>();
					if (!ZepTagIdHelpers.AreTagIdsEqual(tagIds, transformedTagIds))
					{
						changedAssignments[normalizedPath] = transformedTagIds.ToArray();
					}
				}
				catch (ProjectTagStateUnavailableException exception)
				{
					_reportTagStateUnavailable(exception);
					preparationFailure = ProjectFileOperationResult.Failure(exception.Message);
					return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
				}
			}
		}
		return changedAssignments;
	}

	private static string ResolveTagDisplayName(string tagId, TagDefinitionStore tagDefinitions)
	{
		ZepTag tag;
		return tagDefinitions.TryGetTag(tagId, out tag) ? tag.Name : tagId;
	}

	private bool HandleResult(ProjectFileOperationResult result, IReadOnlyDictionary<string, IReadOnlyList<string>> tagAssignments, string successMessage)
	{
		if (!result.Succeeded)
		{
			_setStatus(result.ErrorMessage);
			_applyProjection();
			return false;
		}

		try
		{
			_crossCache.ApplyTagAssignmentsLocally(tagAssignments);
		}
		catch (ProjectTagStateUnavailableException exception)
		{
			_reportTagStateUnavailable(exception);
			return true;
		}

		_setStatus(successMessage);
		_applyProjection();
		return true;
	}
}

}
