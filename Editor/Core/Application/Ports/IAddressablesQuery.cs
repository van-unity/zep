using System;
using System.Collections.Generic;
using Zep.Core.Indexing;

namespace Zep.Core.Application.Ports
{

public interface IAddressablesQuery
{
	// Whether the Addressables editor package is present in this project. It
	// can only change through a package add/remove, which reloads the domain,
	// so callers may read it once at construction.
	bool IsInstalled { get; }

	// Raised on the editor thread whenever the project's Addressables settings
	// change (an entry added, removed, moved, or a group edited), so the
	// captured entry set can be re-read.
	event Action Changed;

	AddressablesCapture Capture();

	HashSet<string> Expand(
		AddressablesCapture capture,
		IReadOnlyList<AssetRecord> records);
}

public sealed class AddressablesCapture
{
	public AddressablesCapture(
		IReadOnlyList<string> directGuids,
		IReadOnlyList<string> folderPaths,
		bool isComplete)
	{
		DirectGuids = directGuids ?? System.Array.Empty<string>();
		FolderPaths = folderPaths ?? System.Array.Empty<string>();
		IsComplete = isComplete;
	}

	internal IReadOnlyList<string> DirectGuids { get; }

	internal IReadOnlyList<string> FolderPaths { get; }

	internal bool IsComplete { get; }

	// The GUIDs that wear the Addressables mark: every direct entry, plus every
	// indexed asset and folder inside an addressable folder.
	public static HashSet<string> Expand(
		AddressablesCapture capture,
		IReadOnlyList<AssetRecord> records)
	{
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (capture == null || !capture.IsComplete)
		{
			// Unknown reflection shapes fail closed: never infer positive badges.
			return result;
		}

		for (var i = 0; i < capture.DirectGuids.Count; i++)
		{
			var guid = capture.DirectGuids[i];
			if (!string.IsNullOrWhiteSpace(guid))
			{
				result.Add(guid);
			}
		}

		ExpandAddressableFolders(records, capture.FolderPaths, result);
		return result;
	}

	private static void ExpandAddressableFolders(IReadOnlyList<AssetRecord> records, IReadOnlyList<string> addressableFolderPaths, HashSet<string> addressableGuids)
	{
		if (records == null || records.Count == 0 || addressableFolderPaths == null || addressableFolderPaths.Count == 0)
		{
			return;
		}
		if (addressableFolderPaths.Count == 0)
		{
			return;
		}
		for (int i = 0; i < records.Count; i++)
		{
			AssetRecord assetRecord = records[i];
			if (!string.IsNullOrWhiteSpace(assetRecord?.Guid) && !string.IsNullOrWhiteSpace(assetRecord.AssetPath) && IsWithinAnyFolderScope(assetRecord.AssetPath, addressableFolderPaths))
			{
				addressableGuids.Add(assetRecord.Guid);
			}
		}
	}

	private static bool IsWithinAnyFolderScope(string assetPath, IReadOnlyList<string> folderPaths)
	{
		if (string.IsNullOrWhiteSpace(assetPath) || folderPaths == null)
		{
			return false;
		}
		for (int i = 0; i < folderPaths.Count; i++)
		{
			string folderPath = folderPaths[i];
			if (!string.IsNullOrWhiteSpace(folderPath) && (string.Equals(assetPath, folderPath, StringComparison.OrdinalIgnoreCase) || assetPath.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}
		}
		return false;
	}
}

}
