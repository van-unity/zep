using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.Application
{
// Package id path ("Packages/com.x") -> friendly display name ("2D Animation"),
// populated by the Bridge from each package's manifest. Lets the sidebar show
// Unity-style package names and sort by them instead of raw ids. The whole map is
// reference-swapped on update, so reads are lock-free.
public static class PackageDisplayNames
{
	private static volatile IReadOnlyDictionary<string, string> _map =
		new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

	public static void Set(IReadOnlyDictionary<string, string> map)
	{
		_map = map ?? new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
	}

	internal static bool TryGetDisplayName(string packageRootPath, out string displayName)
	{
		if (!string.IsNullOrEmpty(packageRootPath))
		{
			return _map.TryGetValue(packageRootPath, out displayName);
		}

		displayName = null;
		return false;
	}

	// Sort key that swaps a package's id segment for its display name, so package
	// roots order like Unity (by display name) while each package's subtree stays
	// contiguous beneath it. Non-package paths sort by their own path unchanged.
	internal static string DisplaySortKey(string path)
	{
		if (string.IsNullOrEmpty(path) || !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
		{
			return path ?? string.Empty;
		}

		var rest = path.Substring("Packages/".Length);
		var slash = rest.IndexOf('/');
		var packageRoot = slash < 0 ? path : "Packages/" + rest.Substring(0, slash);
		if (_map.TryGetValue(packageRoot, out var displayName) && !string.IsNullOrEmpty(displayName))
		{
			var tail = slash < 0 ? string.Empty : rest.Substring(slash);
			return "Packages/" + displayName + tail;
		}

		return path;
	}
}

public static class ZepPathHelpers
{
	public static string GetParentFolder(string assetPath)
	{
		if (string.IsNullOrEmpty(assetPath))
		{
			return string.Empty;
		}
		int lastSeparator = assetPath.LastIndexOf('/');
		return (lastSeparator <= 0) ? assetPath : assetPath.Substring(0, lastSeparator);
	}

	internal static string GetFolderName(string folderPath)
	{
		if (string.Equals(folderPath, "Assets", StringComparison.OrdinalIgnoreCase))
		{
			return "Assets";
		}
		int lastSeparator = folderPath.LastIndexOf('/');
		return (lastSeparator < 0) ? folderPath : folderPath.Substring(lastSeparator + 1);
	}

	// The name a folder is LISTED and SORTED by: a package root shows its
	// manifest display name ("2D Animation"), matching Unity's Project window;
	// everything else shows its folder name. Every producer of a folder entry —
	// the sidebar builder, the full browse rebuild, the incremental bucket
	// rebuild, folder search — and IndexOfPath's binary search over the result
	// must agree on this string, or the listing sorts one way and the lookup
	// searches another (m-11: the incremental "Packages" rebuild used raw ids).
	internal static string GetFolderDisplayName(string folderPath)
	{
		return PackageDisplayNames.TryGetDisplayName(folderPath, out var displayName)
			&& !string.IsNullOrEmpty(displayName)
				? displayName
				: GetFolderName(folderPath);
	}

	internal static int GetFolderDepth(string folderPath)
	{
		if (string.IsNullOrEmpty(folderPath))
		{
			return 0;
		}
		// Plain scan instead of Enumerable.Count(predicate): this runs once per
		// folder entry on every projection rebuild (1.3 k+ folders on a real
		// project), and the LINQ form boxed the string's char enumerator and
		// invoked the predicate delegate per char — a steady per-rebuild
		// allocation the perf profile flagged.
		var depth = 0;
		for (var i = 0; i < folderPath.Length; i++)
		{
			if (folderPath[i] == '/')
			{
				depth++;
			}
		}
		return depth;
	}

	internal static bool IsWithinFolderScope(string assetPath, string folderPath)
	{
		if (string.IsNullOrWhiteSpace(folderPath))
		{
			return true;
		}
		if (string.Equals(assetPath, folderPath, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		// Prefix + '/' checked without the folderPath+"/" concat: this runs per
		// folder entry per search keystroke, so the temporary string was a
		// steady per-keystroke allocation source.
		return assetPath.Length > folderPath.Length
			&& assetPath[folderPath.Length] == '/'
			&& assetPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase);
	}

	// Keeps only the topmost paths of a set: a path that sits inside another path
	// of the same set is dropped. Operations that consume a whole subtree — move,
	// delete — must run on this reduced set. A selection can hold a folder AND
	// items inside it (inline folder expansion in the list view, a folder dragged
	// with its own children), and the parent operation takes the child with it,
	// so the child's own turn fails identity validation and aborts the batch.
	// isFolder, when the caller can answer it, tells the collapse which kept paths are
	// able to contain anything at all; without it every kept path is treated as a
	// possible container, which is the same answer at O(kept) probes per candidate.
	public static List<string> CollapseNestedPaths(
		IReadOnlyList<string> assetPaths,
		Func<string, bool> isFolder = null)
	{
		var collapsed = new List<string>(assetPaths?.Count ?? 0);
		if (assetPaths == null || assetPaths.Count == 0)
		{
			return collapsed;
		}

		// Depth is decided once per path instead of once per comparison.
		var sorted = new List<(int Depth, string Path)>(assetPaths.Count);
		for (var i = 0; i < assetPaths.Count; i++)
		{
			// An empty path contains nothing and is contained by nothing; carrying
			// it would make IsWithinFolderScope's "no scope" answer swallow the set.
			if (!string.IsNullOrEmpty(assetPaths[i]))
			{
				sorted.Add((GetFolderDepth(assetPaths[i]), assetPaths[i]));
			}
		}

		// Shallowest first, so a container is always inspected before its contents.
		sorted.Sort(static (left, right) =>
		{
			return (left.Depth != right.Depth)
				? left.Depth.CompareTo(right.Depth)
				: string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
		});

		var containers = (isFolder == null) ? collapsed : new List<string>();
		for (var i = 0; i < sorted.Count; i++)
		{
			var path = sorted[i].Path;
			// Equal paths sort adjacent, and the containment probe only ever sees kept
			// containers, so the duplicate that the old self-equality leg swallowed is
			// dropped here instead: if its twin was kept it is already in the result,
			// and if its twin was dropped this copy is nested for the same reason.
			if (i > 0 && string.Equals(path, sorted[i - 1].Path, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var isNested = false;
			for (var j = 0; j < containers.Count; j++)
			{
				if (IsWithinFolderScope(path, containers[j]))
				{
					isNested = true;
					break;
				}
			}

			if (isNested)
			{
				continue;
			}

			collapsed.Add(path);
			if (!ReferenceEquals(containers, collapsed) && isFolder(path))
			{
				containers.Add(path);
			}
		}

		return collapsed;
	}

	internal static string ResolveSearchScopePath(ZepSearchScope searchScope, string currentFolderPath)
	{
		return searchScope switch
		{
			ZepSearchScope.All => string.Empty, 
			ZepSearchScope.Packages => "Packages", 
			ZepSearchScope.CurrentFolder => AssetPathFormat.Normalize(currentFolderPath), 
			_ => "Assets", 
		};
	}

	internal static int GetRootOrder(string path)
	{
		if (string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (string.Equals(path, "Packages", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		return 3;
	}

	// Is this change-batch path a FOLDER? Ask the asset boundary, never
	// Directory.Exists: a registry/git package folder has no physical directory
	// at its virtual path (its files live under Library/PackageCache), and a
	// batch of only such folders that fails this test schedules no projection
	// rebuild, leaving the panel stale until the next unrelated event.
	internal static bool IsFolderAssetPath(IProjectAssetGateway assetGateway, string assetPath)
	{
		if (assetGateway == null || string.IsNullOrEmpty(assetPath))
		{
			return false;
		}

		return assetGateway.IsFolder(AssetPath.From(assetPath));
	}

	internal static bool IsFolderTypeName(string typeName)
	{
		return string.Equals(typeName, "Folder", StringComparison.OrdinalIgnoreCase);
	}

	internal static bool MatchesTypeFilter(string entryTypeName, string selectedTypeFilter)
	{
		return string.IsNullOrWhiteSpace(selectedTypeFilter) || string.Equals(entryTypeName ?? string.Empty, selectedTypeFilter, StringComparison.OrdinalIgnoreCase);
	}

	internal static bool FolderExists(IEnumerable<ZepFolderEntry> folders, string currentFolderPath)
	{
		foreach (ZepFolderEntry folder in folders)
		{
			if (string.Equals(folder.Path, currentFolderPath, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static string GetAssetPathAt(IReadOnlyList<ZepAssetEntry> assets, int index)
	{
		if (assets is LazyAssetEntryList lazy)
		{
			return lazy.PathAt(index);
		}

		return assets[index].Path;
	}

	// How many FindAssetPathIndex calls fell through to the linear scan.
	// Read by tests only; nothing branches on it.
	internal static int LinearAssetPathScans;

	public static int FindAssetPathIndex(
		IReadOnlyList<ZepAssetEntry> assets,
		string assetPath)
	{
		if (assets == null || assets.Count == 0 || string.IsNullOrEmpty(assetPath))
		{
			return -1;
		}

		if (assets is IAssetPathLookup lookup)
		{
			return lookup.IndexOfPath(assetPath);
		}

		// Test hook: the orders the UI resolves paths against on a hot path
		// (search results, the expanded list) all implement IAssetPathLookup,
		// so a scan here means one of them regressed to the linear leg.
		LinearAssetPathScans++;
		for (var i = 0; i < assets.Count; i++)
		{
			// OrdinalIgnoreCase, like every other path identity in the panel
			// (AssetPath.Equals, LazyAssetEntryList.IndexOfPath, the selection
			// set). Ordinal here made a ping/reveal whose path differed only in
			// case resolve in a search projection and silently do nothing in a
			// browse projection — no scroll, no highlight (audit 5.8).
			if (string.Equals(
				GetAssetPathAt(assets, i),
				assetPath,
				StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		return -1;
	}

	internal static void SortByDisplayOrder(List<ZepFolderEntry> entries)
	{
		if (entries == null || entries.Count < 2)
		{
			return;
		}

		var decorated = new List<(int RootOrder, string SortKey, ZepFolderEntry Entry)>(entries.Count);
		for (var i = 0; i < entries.Count; i++)
		{
			var entry = entries[i];
			decorated.Add((
				GetRootOrder(entry.Path),
				PackageDisplayNames.DisplaySortKey(entry.Path),
				entry));
		}

		decorated.Sort(static (a, b) =>
			a.RootOrder != b.RootOrder
				? a.RootOrder.CompareTo(b.RootOrder)
				: string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

		for (var i = 0; i < decorated.Count; i++)
		{
			entries[i] = decorated[i].Entry;
		}
	}

	internal static IReadOnlyList<ZepAssetEntry> SortAssetEntries(List<ZepAssetEntry> entries)
	{
		if (entries == null || entries.Count == 0)
		{
			return Array.Empty<ZepAssetEntry>();
		}
		entries.Sort(delegate(ZepAssetEntry left, ZepAssetEntry right)
		{
			int folderOrder = IsFolderTypeName(left.TypeName).CompareTo(IsFolderTypeName(right.TypeName));
			if (folderOrder != 0)
			{
				return -folderOrder;
			}
			int nameOrder = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
			return (nameOrder != 0) ? nameOrder : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
		});
		return entries.ToArray();
	}
}

}
