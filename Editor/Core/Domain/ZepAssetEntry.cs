using System;
using System.Collections.Generic;

namespace Zep.Core.Domain
{
public sealed class ZepAssetEntry
{
	public string Name { get; }

	public string Path { get; }

	public string TypeName { get; }

	public IReadOnlyList<ZepTag> Tags { get; }

	public ZepTagColor FolderTintColor { get; }

	public bool IsFolder => string.Equals(TypeName, "Folder", StringComparison.OrdinalIgnoreCase);

	public ZepAssetEntry(string name, string path, string typeName, IReadOnlyList<ZepTag> tags = null, ZepTagColor folderTintColor = ZepTagColor.None)
	{
		Name = name ?? string.Empty;
		Path = path ?? string.Empty;
		TypeName = typeName ?? string.Empty;
		Tags = tags ?? Array.Empty<ZepTag>();
		FolderTintColor = folderTintColor;
	}
}

}
