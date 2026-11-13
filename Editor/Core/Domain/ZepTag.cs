using System;

namespace Zep.Core.Domain
{

public readonly struct ZepTag : IEquatable<ZepTag>
{
	public string Id { get; }

	public string Name { get; }

	public ZepTagColor Color { get; }

	public bool IsFavorite { get; }

	public ZepTag(string id, string name, ZepTagColor color, bool isFavorite = false)
	{
		Id = id ?? string.Empty;
		Name = name ?? string.Empty;
		Color = color;
		IsFavorite = isFavorite;
	}

	public bool Equals(ZepTag other)
	{
		return string.Equals(Id, other.Id, StringComparison.Ordinal) && string.Equals(Name, other.Name, StringComparison.Ordinal) && Color == other.Color && IsFavorite == other.IsFavorite;
	}

	public override bool Equals(object obj)
	{
		return obj is ZepTag other && Equals(other);
	}

	public override int GetHashCode()
	{
		int hash = ((Id != null) ? Id.GetHashCode() : 0);
		hash = (hash * 397) ^ ((Name != null) ? Name.GetHashCode() : 0);
		hash = (hash * 397) ^ (int)Color;
		return (hash * 397) ^ IsFavorite.GetHashCode();
	}
}

}
