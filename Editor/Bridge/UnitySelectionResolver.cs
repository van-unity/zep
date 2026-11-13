using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
// Recovered seam: the decompiler emitted bare `Object` (ambiguous with
// System.Object). All uses here are Unity objects.
using Object = UnityEngine.Object;

namespace Zep.Editor.Bridge
{

internal static class UnitySelectionResolver
{
	internal static IReadOnlyList<string> GetSelectedAssetPathsFromEditorSelection()
	{
		List<string> selectedAssetPaths = new List<string>();
		HashSet<string> knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		AddSelectionAssetPath(Selection.activeObject, selectedAssetPaths, knownPaths);
		Object[] objects = Selection.objects;
		for (int i = 0; i < objects.Length; i++)
		{
			AddSelectionAssetPath(objects[i], selectedAssetPaths, knownPaths);
		}
		return selectedAssetPaths;
	}

	private static void AddSelectionAssetPath(Object selectedObject, List<string> selectedAssetPaths, HashSet<string> knownPaths)
	{
		if (!(selectedObject == (Object)null))
		{
			string assetPath = AssetDatabase.GetAssetPath(selectedObject);
			if (!string.IsNullOrWhiteSpace(assetPath) && knownPaths.Add(assetPath))
			{
				selectedAssetPaths.Add(assetPath);
			}
		}
	}
}

}
