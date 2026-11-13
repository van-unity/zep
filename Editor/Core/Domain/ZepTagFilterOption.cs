using System;

namespace Zep.Core.Domain
{

public readonly struct ZepTagFilterOption : IEquatable<ZepTagFilterOption>
{
	public ZepTag Tag { get; }

	public int Count { get; }

	public string TagId => Tag.Id;

	public ZepTagFilterOption(ZepTag tag, int count)
	{
		Tag = tag;
		Count = Math.Max(0, count);
	}

	public bool Equals(ZepTagFilterOption other)
	{
		return Tag.Equals(other.Tag) && Count == other.Count;
	}

	public override bool Equals(object obj)
	{
		return obj is ZepTagFilterOption other && Equals(other);
	}

	public override int GetHashCode()
	{
		return (Tag.GetHashCode() * 397) ^ Count;
	}
}

}
