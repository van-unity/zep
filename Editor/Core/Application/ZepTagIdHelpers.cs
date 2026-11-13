using System;
using System.Collections.Generic;

namespace Zep.Core.Application
{

internal static class ZepTagIdHelpers
{
	internal static IReadOnlyList<string> AddTagId(IReadOnlyList<string> existingTagIds, string tagId)
	{
		string normalizedTagId = NormalizeTagId(tagId);
		List<string> tagIds = new List<string>();
		HashSet<string> seenTagIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (existingTagIds != null)
		{
			for (int i = 0; i < existingTagIds.Count; i++)
			{
				string normalizedExistingTagId = NormalizeTagId(existingTagIds[i]);
				if (!string.IsNullOrWhiteSpace(normalizedExistingTagId) && seenTagIds.Add(normalizedExistingTagId))
				{
					tagIds.Add(normalizedExistingTagId);
				}
			}
		}
		if (!string.IsNullOrWhiteSpace(normalizedTagId) && seenTagIds.Add(normalizedTagId))
		{
			tagIds.Add(normalizedTagId);
		}
		return tagIds.ToArray();
	}

	internal static IReadOnlyList<string> RemoveTagId(IReadOnlyList<string> existingTagIds, string tagId)
	{
		string normalizedTagId = NormalizeTagId(tagId);
		if (string.IsNullOrWhiteSpace(normalizedTagId) || existingTagIds == null || existingTagIds.Count == 0)
		{
			return Array.Empty<string>();
		}
		List<string> tagIds = new List<string>(existingTagIds.Count);
		HashSet<string> seenTagIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < existingTagIds.Count; i++)
		{
			string normalizedExistingTagId = NormalizeTagId(existingTagIds[i]);
			if (string.IsNullOrWhiteSpace(normalizedExistingTagId)
				|| string.Equals(normalizedExistingTagId, normalizedTagId, StringComparison.OrdinalIgnoreCase)
				|| !seenTagIds.Add(normalizedExistingTagId))
			{
				continue;
			}
			tagIds.Add(normalizedExistingTagId);
		}
		return tagIds.ToArray();
	}

	internal static bool AreTagIdsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
	{
		int leftCount = left?.Count ?? 0;
		int rightCount = right?.Count ?? 0;
		if (leftCount != rightCount)
		{
			return false;
		}
		for (int i = 0; i < leftCount; i++)
		{
			if (!string.Equals(NormalizeTagId(left[i]), NormalizeTagId(right[i]), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	internal static string NormalizeTagId(string tagId)
	{
		return string.IsNullOrWhiteSpace(tagId) ? string.Empty : tagId.Trim().ToLowerInvariant();
	}

	internal static IReadOnlyList<string> NormalizeTagIds(IReadOnlyList<string> tagIds)
	{
		if (tagIds == null || tagIds.Count == 0)
		{
			return Array.Empty<string>();
		}
		List<string> normalizedTagIds = new List<string>(tagIds.Count);
		HashSet<string> seenTagIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < tagIds.Count; i++)
		{
			string normalizedTagId = NormalizeTagId(tagIds[i]);
			if (!string.IsNullOrWhiteSpace(normalizedTagId) && seenTagIds.Add(normalizedTagId))
			{
				normalizedTagIds.Add(normalizedTagId);
			}
		}
		return normalizedTagIds;
	}
}

}
