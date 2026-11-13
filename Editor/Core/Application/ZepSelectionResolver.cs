using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.Application
{

internal static class ZepSelectionResolver
{
	internal static bool AreSelectedAssetPathsEqual(IReadOnlyList<string> currentSelection, IReadOnlyList<string> incomingSelection)
	{
		int currentCount = currentSelection?.Count ?? 0;
		int incomingCount = incomingSelection?.Count ?? 0;
		if (currentCount != incomingCount)
		{
			return false;
		}
		for (int i = 0; i < currentCount; i++)
		{
			if (!string.Equals(currentSelection[i], incomingSelection[i], StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	internal static string ResolveSelectionFolderPath(IReadOnlyList<string> selectedAssetPaths, IProjectAssetGateway gateway)
	{
		if (selectedAssetPaths == null || selectedAssetPaths.Count == 0)
		{
			return "Assets";
		}
		if (selectedAssetPaths.Count == 1 && gateway.IsFolder(AssetPath.From(selectedAssetPaths[0])))
		{
			return AssetPathFormat.Normalize(selectedAssetPaths[0]);
		}
		string commonFolder = string.Empty;
		for (int i = 0; i < selectedAssetPaths.Count; i++)
		{
			string selectionScopeFolder = GetSelectionScopeFolder(selectedAssetPaths[i], gateway);
			if (string.IsNullOrEmpty(commonFolder))
			{
				commonFolder = selectionScopeFolder;
				continue;
			}
			commonFolder = GetCommonFolderPath(commonFolder, selectionScopeFolder);
			if (string.IsNullOrEmpty(commonFolder))
			{
				return "Assets";
			}
		}
		return string.IsNullOrEmpty(commonFolder) ? "Assets" : commonFolder;
	}

	private static string GetSelectionScopeFolder(string assetPath, IProjectAssetGateway gateway)
	{
		string normalizedPath = AssetPathFormat.Normalize(assetPath);
		if (!gateway.IsFolder(AssetPath.From(normalizedPath)))
		{
			return ZepPathHelpers.GetParentFolder(normalizedPath);
		}
		if (string.Equals(normalizedPath, "Assets", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedPath, "Packages", StringComparison.OrdinalIgnoreCase))
		{
			return normalizedPath;
		}
		return GetAncestorFolder(normalizedPath);
	}

	private static string GetCommonFolderPath(string left, string right)
	{
		string candidate = AssetPathFormat.Normalize(left);
		string other = AssetPathFormat.Normalize(right);
		while (!string.IsNullOrEmpty(candidate))
		{
			if (string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase) || other.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase))
			{
				return candidate;
			}
			candidate = GetAncestorFolder(candidate);
		}
		return string.Empty;
	}

	private static string GetAncestorFolder(string folderPath)
	{
		string normalizedPath = AssetPathFormat.Normalize(folderPath);
		int lastSeparator = normalizedPath.LastIndexOf('/');
		return (lastSeparator <= 0) ? string.Empty : normalizedPath.Substring(0, lastSeparator);
	}
}

}
