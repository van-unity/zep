using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Zep.Core.Theming
{

public static class UssVariableParser
{
	private static readonly Regex VarPattern = new Regex("(--zep-[a-z0-9-]+)\\s*:\\s*([^;]+);", RegexOptions.Compiled);

	private static readonly Regex RgbPattern = new Regex("^rgba?\\(\\s*(?<r>\\d+)\\s*,\\s*(?<g>\\d+)\\s*,\\s*(?<b>\\d+)\\s*(?:,\\s*(?<a>[0-9.]+)\\s*)?\\)$", RegexOptions.Compiled);

	public static Dictionary<string, ZepColor> Parse(string ussText)
	{
		Dictionary<string, ZepColor> variables = new Dictionary<string, ZepColor>(StringComparer.Ordinal);
		if (string.IsNullOrEmpty(ussText))
		{
			return variables;
		}
		foreach (Match declaration in VarPattern.Matches(ussText))
		{
			string variableName = declaration.Groups[1].Value;
			string declaredValue = declaration.Groups[2].Value.Trim();
			if (TryParseColor(declaredValue, out var color))
			{
				variables[variableName] = color;
			}
		}
		return variables;
	}

	internal static bool TryParseColor(string text, out ZepColor color)
	{
		color = default(ZepColor);
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		text = text.Trim();
		if (string.Equals(text, "white", StringComparison.OrdinalIgnoreCase))
		{
			color = new ZepColor(1f, 1f, 1f, 1f);
			return true;
		}
		Match match = RgbPattern.Match(text);
		if (match.Success)
		{
			if (!byte.TryParse(match.Groups["r"].Value, out var red))
			{
				return false;
			}
			if (!byte.TryParse(match.Groups["g"].Value, out var green))
			{
				return false;
			}
			if (!byte.TryParse(match.Groups["b"].Value, out var blue))
			{
				return false;
			}
			float alpha = 1f;
			if (match.Groups["a"].Success && match.Groups["a"].Length > 0 && !float.TryParse(match.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
			{
				return false;
			}
			color = new ZepColor((float)(int)red / 255f, (float)(int)green / 255f, (float)(int)blue / 255f, ZepMath.Clamp01(alpha));
			return true;
		}
		if (text.StartsWith("#", StringComparison.Ordinal))
		{
			return TryParseHex(text, out color);
		}
		return false;
	}

	private static bool TryParseHex(string text, out ZepColor color)
	{
		color = default(ZepColor);
		string digits = text.Substring(1);
		if (digits.Length == 3)
		{
			if (!TryHex(digits[0], out var red))
			{
				return false;
			}
			if (!TryHex(digits[1], out var green))
			{
				return false;
			}
			if (!TryHex(digits[2], out var blue))
			{
				return false;
			}
			color = new ZepColor((float)(red * 17) / 255f, (float)(green * 17) / 255f, (float)(blue * 17) / 255f, 1f);
			return true;
		}
		if (digits.Length == 6 || digits.Length == 8)
		{
			if (!byte.TryParse(digits.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red))
			{
				return false;
			}
			if (!byte.TryParse(digits.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green))
			{
				return false;
			}
			if (!byte.TryParse(digits.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
			{
				return false;
			}
			int alpha = 255;
			if (digits.Length == 8)
			{
				if (!byte.TryParse(digits.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedAlpha))
				{
					return false;
				}
				alpha = parsedAlpha;
			}
			color = new ZepColor((float)(int)red / 255f, (float)(int)green / 255f, (float)(int)blue / 255f, (float)alpha / 255f);
			return true;
		}
		return false;
	}

	private static bool TryHex(char c, out int value)
	{
		if (c >= '0' && c <= '9')
		{
			value = c - 48;
			return true;
		}
		if (c >= 'a' && c <= 'f')
		{
			value = 10 + (c - 97);
			return true;
		}
		if (c >= 'A' && c <= 'F')
		{
			value = 10 + (c - 65);
			return true;
		}
		value = 0;
		return false;
	}
}

}
