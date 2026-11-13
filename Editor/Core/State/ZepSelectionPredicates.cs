using System.Collections.Generic;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{

public static class ZepSelectionPredicates
{
	public static List<string> GetSelectedActionPaths(IReadOnlyList<ZepAssetEntry> selectedAssets, ZepFolderEntry selectedFolder)
	{
		List<string> actionPaths = new List<string>();
		if (selectedAssets != null)
		{
			for (int i = 0; i < selectedAssets.Count; i++)
			{
				if (selectedAssets[i] != null && !string.IsNullOrEmpty(selectedAssets[i].Path))
				{
					actionPaths.Add(selectedAssets[i].Path);
				}
			}
		}
		if (actionPaths.Count > 0 || selectedFolder == null || string.IsNullOrEmpty(selectedFolder.Path))
		{
			return actionPaths;
		}
		actionPaths.Add(selectedFolder.Path);
		return actionPaths;
	}

	// Test-only: how many times the modify gate was evaluated. Each evaluation
	// asks the gateway per path (a PackageInfo lookup for package paths), so a
	// menu open must resolve it once and carry the answer.
	internal static int ModifyEvaluationsForTests;

	// Writable AND actually modifiable: the Assets/Packages roots and package
	// roots ("Packages/<name>") report writable (so items can be CREATED under
	// them) but can never be renamed, duplicated, or deleted themselves — the
	// modify affordances must not light up for them, let alone show a
	// confirmation dialog for an operation that is guaranteed to be refused.
	public static bool CanModifyAllSelectedPaths(ZepController commandHandler, IReadOnlyList<string> selectedPaths)
	{
		ModifyEvaluationsForTests++;
		if (!CanWriteAllSelectedPaths(commandHandler, selectedPaths))
		{
			return false;
		}
		for (int i = 0; i < selectedPaths.Count; i++)
		{
			if (AssetPathClassification.IsProjectRootPath(selectedPaths[i])
				|| AssetPathClassification.IsPackageRootPath(selectedPaths[i]))
			{
				return false;
			}
		}
		return true;
	}

	internal static bool CanWriteAllSelectedPaths(ZepController commandHandler, IReadOnlyList<string> selectedPaths)
	{
		if (commandHandler == null || selectedPaths == null || selectedPaths.Count == 0)
		{
			return false;
		}
		for (int i = 0; i < selectedPaths.Count; i++)
		{
			if (!commandHandler.CanWrite(AssetPath.From(selectedPaths[i])))
			{
				return false;
			}
		}
		return true;
	}

	public static bool CanTagAllSelectedPaths(ZepController commandHandler, IReadOnlyList<string> selectedPaths)
	{
		if (commandHandler == null || selectedPaths == null || selectedPaths.Count == 0)
		{
			return false;
		}
		for (int i = 0; i < selectedPaths.Count; i++)
		{
			if (!commandHandler.CanTag(AssetPath.From(selectedPaths[i])))
			{
				return false;
			}
		}
		return true;
	}
}

}
