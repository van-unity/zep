using System;

namespace Zep.Core.Domain
{

public readonly struct ZepSearchScopeOption : IEquatable<ZepSearchScopeOption>
{
	public ZepSearchScope Scope { get; }

	public string Label { get; }

	public ZepSearchScopeOption(ZepSearchScope scope, string label)
	{
		Scope = scope;
		Label = label ?? string.Empty;
	}

	public bool Equals(ZepSearchScopeOption other)
	{
		return Scope == other.Scope && string.Equals(Label, other.Label, StringComparison.Ordinal);
	}

	public override bool Equals(object obj)
	{
		return obj is ZepSearchScopeOption other && Equals(other);
	}

	public override int GetHashCode()
	{
		return ((int)Scope * 397) ^ ((Label != null) ? Label.GetHashCode() : 0);
	}
}

}
