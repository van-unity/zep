using System;

namespace Zep.Core.State
{

public readonly struct NavigationEntry : IEquatable<NavigationEntry>
{
	internal static readonly NavigationEntry None = default(NavigationEntry);

	public string FolderPath { get; }

	public string TagId { get; }

	// The ceiling a tag pane was browsing under when it recorded this folder;
	// null for every other pane. Re-entering the entry through history restores
	// that ceiling rather than promoting the folder to one of its own.
	public string BrowseRootPath { get; }

	internal bool IsFolder => FolderPath != null;

	internal bool IsTag => TagId != null;

	public bool IsEmpty => FolderPath == null && TagId == null;

	private NavigationEntry(string folderPath, string tagId, string browseRootPath)
	{
		FolderPath = folderPath;
		TagId = tagId;
		BrowseRootPath = browseRootPath;
	}

	public static NavigationEntry Folder(string folderPath, string browseRootPath = null)
	{
		if (string.IsNullOrWhiteSpace(folderPath))
		{
			throw new ArgumentException("Folder path must not be null or whitespace.", nameof(folderPath));
		}
		return new NavigationEntry(
			folderPath,
			null,
			string.IsNullOrWhiteSpace(browseRootPath) ? null : browseRootPath);
	}

	public static NavigationEntry Tag(string tagId)
	{
		if (string.IsNullOrWhiteSpace(tagId))
		{
			throw new ArgumentException("Tag id must not be null or whitespace.", nameof(tagId));
		}
		return new NavigationEntry(null, tagId, null);
	}

	public bool Equals(NavigationEntry other)
	{
		return string.Equals(FolderPath, other.FolderPath, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(TagId, other.TagId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(BrowseRootPath, other.BrowseRootPath, StringComparison.OrdinalIgnoreCase);
	}

	public override bool Equals(object obj)
	{
		return obj is NavigationEntry other && Equals(other);
	}

	public override int GetHashCode()
	{
		int hash = ((FolderPath != null) ? StringComparer.OrdinalIgnoreCase.GetHashCode(FolderPath) : 0);
		hash = (hash * 397) ^ ((TagId != null) ? StringComparer.OrdinalIgnoreCase.GetHashCode(TagId) : 0);
		return (hash * 397) ^ ((BrowseRootPath != null) ? StringComparer.OrdinalIgnoreCase.GetHashCode(BrowseRootPath) : 0);
	}

	public static bool operator ==(NavigationEntry left, NavigationEntry right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(NavigationEntry left, NavigationEntry right)
	{
		return !left.Equals(right);
	}
}

}
