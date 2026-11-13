using System;

namespace Zep.Core.Domain
{

public readonly struct AssetPath : IEquatable<AssetPath>
{
	public string Value { get; }

	public bool IsEmpty => string.IsNullOrEmpty(Value);

	private AssetPath(string value)
	{
		Value = Normalize(value);
	}

	// From runs on every row bind and selection sweep, and a path is compared and
	// hashed by this normalized form. The old `value.Replace('\\','/').TrimEnd('/')`
	// allocated a char[1] on every call — a single char literal binds to the
	// string.TrimEnd(params char[]) overload, not a dedicated single-char one — plus
	// intermediate strings. This scans once and returns the original instance
	// unchanged in the common case: an asset path already forward-slashed with no
	// trailing slash allocates nothing.
	private static string Normalize(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}

		var end = value.Length;
		while (end > 0 && (value[end - 1] == '/' || value[end - 1] == '\\'))
		{
			end--;
		}

		var hasBackslash = false;
		for (var i = 0; i < end; i++)
		{
			if (value[i] == '\\')
			{
				hasBackslash = true;
				break;
			}
		}

		if (!hasBackslash)
		{
			return end == value.Length ? value : value.Substring(0, end);
		}

		var buffer = new char[end];
		for (var i = 0; i < end; i++)
		{
			var c = value[i];
			buffer[i] = c == '\\' ? '/' : c;
		}

		return new string(buffer);
	}

	public static AssetPath From(string value)
	{
		return new AssetPath(value);
	}

	public override string ToString()
	{
		return Value ?? string.Empty;
	}

	public bool Equals(AssetPath other)
	{
		return string.Equals(Value ?? string.Empty, other.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	public override bool Equals(object obj)
	{
		return obj is AssetPath other && Equals(other);
	}

	public override int GetHashCode()
	{
		return (Value != null) ? StringComparer.OrdinalIgnoreCase.GetHashCode(Value) : 0;
	}

	public static bool operator ==(AssetPath a, AssetPath b)
	{
		return a.Equals(b);
	}

	public static bool operator !=(AssetPath a, AssetPath b)
	{
		return !a.Equals(b);
	}

	public static implicit operator string(AssetPath path)
	{
		return path.Value ?? string.Empty;
	}
}

}
