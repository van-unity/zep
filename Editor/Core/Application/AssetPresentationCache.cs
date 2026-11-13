using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.Application
{
internal sealed class AssetPresentationCache
{
	private readonly struct CachedFileStats
	{
		internal DateTime? ModifiedDate { get; }

		internal long? Size { get; }

		internal CachedFileStats(DateTime? modifiedDate, long? size)
		{
			ModifiedDate = modifiedDate;
			Size = size;
		}
	}

	// Both maps below are keyed by asset path and live for the whole editor
	// session: Clear() only fires on an index rebuild and Remove() only on a
	// per-asset change, so browsing a large project added a key per row/tile
	// bind and never gave one back (~40-60 MB after a 500k walk). Clearing
	// wholesale at the cap costs a cold re-stat of the folder currently on
	// screen — a few hundred entries — which is cheaper than the per-hit
	// bookkeeping an LRU would add to a path that runs on every bind.
	private const int MaxCachedPaths = 8192;

	private readonly Dictionary<string, CachedFileStats> _entries = new Dictionary<string, CachedFileStats>(StringComparer.OrdinalIgnoreCase);

	private readonly IProjectAssetGateway _assetGateway;

	private HashSet<string> _addressableAssetGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	// Path→GUID cache for the addressable check. GetGuid is a native
	// AssetDatabase call and Get() runs once per row/tile bind — without this
	// cache every bind paid the native call even on a stats-cache hit. A GUID
	// only changes when the asset at a path changes, and both invalidation
	// paths (Clear on index rebuild, Remove on per-asset change) already fire
	// exactly then.
	private readonly Dictionary<string, string> _guidByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly Func<string, DateTime> _readFileLastWriteTime;

	private readonly Func<string, DateTime> _readDirectoryLastWriteTime;

	private readonly Func<string, long> _readFileLength;

	internal AssetPresentationCache(IProjectAssetGateway assetGateway)
		: this(assetGateway, File.GetLastWriteTime, Directory.GetLastWriteTime, (string path) => new FileInfo(path).Length)
	{
	}

	internal AssetPresentationCache(IProjectAssetGateway assetGateway, Func<string, DateTime> readFileLastWriteTime, Func<string, DateTime> readDirectoryLastWriteTime, Func<string, long> readFileLength)
	{
		_assetGateway = assetGateway ?? throw new ArgumentNullException(nameof(assetGateway));
		_readFileLastWriteTime = readFileLastWriteTime;
		_readDirectoryLastWriteTime = readDirectoryLastWriteTime;
		_readFileLength = readFileLength;
	}

	internal void Clear()
	{
		_entries.Clear();
		_guidByPath.Clear();
	}

	internal bool HasCachedEntry(string assetPath)
	{
		return _entries.ContainsKey(assetPath);
	}

	internal void Remove(string assetPath)
	{
		if (!string.IsNullOrEmpty(assetPath))
		{
			_entries.Remove(assetPath);
			_guidByPath.Remove(assetPath);
		}
	}

	internal void SetAddressableAssetGuids(HashSet<string> addressableAssetGuids)
	{
		_addressableAssetGuids = addressableAssetGuids ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	internal AssetPresentationData Get(string assetPath, string typeName)
	{
		bool isFolder = ZepPathHelpers.IsFolderTypeName(typeName);
		// Folders are not exempt: an Addressables folder entry makes the
		// folder itself addressable (its GUID is in the expanded set), so the
		// row wears the same mark as the assets it pulls in.
		if (_entries.TryGetValue(assetPath, out var value))
		{
			return new AssetPresentationData(
				value.ModifiedDate,
				value.Size,
				IsAddressableAsset(assetPath));
		}

		// One existence probe per read: resolving the absolute path already has
		// to ask the filesystem whether anything is there, so it answers
		// "file or directory" once and both readers below take that answer.
		// Asking again per reader cost four extra syscalls on every first bind.
		DateTime? modifiedDate = null;
		long? size = null;
		bool failed = false;
		if (TryGetAbsoluteAssetPath(assetPath, out var absolutePath, out var isDirectory))
		{
			modifiedDate = TryReadModifiedDate(absolutePath, isFolder || isDirectory, out failed);
			if (!isFolder && !isDirectory)
			{
				size = TryReadFileSize(absolutePath, out var sizeFailed);
				failed |= sizeFailed;
			}
		}

		if (!failed)
		{
			if (_entries.Count >= MaxCachedPaths)
			{
				_entries.Clear();
			}

			_entries[assetPath] = new CachedFileStats(modifiedDate, size);
		}
		return new AssetPresentationData(
			modifiedDate,
			size,
			IsAddressableAsset(assetPath));
	}

	private bool IsAddressableAsset(string assetPath)
	{
		// No addressables in the project (the common case) — skip the GUID
		// resolution entirely; nothing could match.
		if (_addressableAssetGuids.Count == 0 || string.IsNullOrWhiteSpace(assetPath))
		{
			return false;
		}
		if (!_guidByPath.TryGetValue(assetPath, out var guid))
		{
			if (_guidByPath.Count >= MaxCachedPaths)
			{
				_guidByPath.Clear();
			}

			guid = _assetGateway.GetGuid(AssetPath.From(assetPath)) ?? string.Empty;
			_guidByPath[assetPath] = guid;
		}
		return guid.Length > 0 && _addressableAssetGuids.Contains(guid);
	}

	private DateTime? TryReadModifiedDate(string absolutePath, bool readAsDirectory, out bool failed)
	{
		failed = false;
		try
		{
			DateTime dateTime = readAsDirectory
				? _readDirectoryLastWriteTime(absolutePath)
				: _readFileLastWriteTime(absolutePath);
			return (dateTime == DateTime.MinValue) ? null : new DateTime?(dateTime);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			failed = true;
			return null;
		}
		catch (IOException)
		{
			failed = true;
			return null;
		}
	}

	private long? TryReadFileSize(string absolutePath, out bool failed)
	{
		failed = false;
		try
		{
			return _readFileLength(absolutePath);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			failed = true;
			return null;
		}
		catch (IOException)
		{
			failed = true;
			return null;
		}
	}

	// Resolves the on-disk path AND settles what lives there in the same pass:
	// isDirectory is only meaningful when this returns true.
	private bool TryGetAbsoluteAssetPath(string assetPath, out string absolutePath, out bool isDirectory)
	{
		absolutePath = string.Empty;
		isDirectory = false;
		string normalizedPath = AssetPathFormat.Normalize(assetPath);
		if (string.IsNullOrWhiteSpace(normalizedPath))
		{
			return false;
		}

		string resolvedPath = _assetGateway.ResolveAbsolutePath(AssetPath.From(normalizedPath));
		if (string.IsNullOrEmpty(resolvedPath))
		{
			return false;
		}
		absolutePath = resolvedPath;
		return Probe(absolutePath, out isDirectory);
	}

	private static bool Probe(string absolutePath, out bool isDirectory)
	{
		if (File.Exists(absolutePath))
		{
			isDirectory = false;
			return true;
		}

		isDirectory = Directory.Exists(absolutePath);
		return isDirectory;
	}
}

}
