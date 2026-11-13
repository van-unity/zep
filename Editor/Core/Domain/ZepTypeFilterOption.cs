using System;

namespace Zep.Core.Domain
{

public readonly struct ZepTypeFilterOption : IEquatable<ZepTypeFilterOption>
{
	public string TypeName { get; }

	public int Count { get; }

	public ZepTypeFilterOption(string typeName, int count)
	{
		TypeName = typeName ?? string.Empty;
		Count = Math.Max(0, count);
	}

	public bool Equals(ZepTypeFilterOption other)
	{
		return string.Equals(TypeName, other.TypeName, StringComparison.Ordinal) && Count == other.Count;
	}

	public override bool Equals(object obj)
	{
		return obj is ZepTypeFilterOption other && Equals(other);
	}

	public override int GetHashCode()
	{
		return (((TypeName != null) ? TypeName.GetHashCode() : 0) * 397) ^ Count;
	}
}

}
