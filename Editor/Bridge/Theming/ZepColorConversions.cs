using System;
using System.Collections.Generic;
using Zep.Core.Theming;
using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{

public static class ZepColorConversions
{
	public static Color ToUnityColor(this ZepColor c)
	{
		return new Color(c.R, c.G, c.B, c.A);
	}

	public static ZepColor ToZepColor(this Color c)
	{
		return new ZepColor(c.r, c.g, c.b, c.a);
	}

	public static Dictionary<string, ZepColor> ToZepColorDict(this IReadOnlyDictionary<string, Color> source)
	{
		if (source == null)
		{
			throw new ArgumentNullException(nameof(source));
		}
		Dictionary<string, ZepColor> converted = new Dictionary<string, ZepColor>(source.Count, StringComparer.Ordinal);
		foreach (KeyValuePair<string, Color> variable in source)
		{
			converted[variable.Key] = variable.Value.ToZepColor();
		}
		return converted;
	}

	public static Dictionary<string, Color> ToUnityColorDict(this IReadOnlyDictionary<string, ZepColor> source)
	{
		if (source == null)
		{
			throw new ArgumentNullException(nameof(source));
		}
		Dictionary<string, Color> converted = new Dictionary<string, Color>(source.Count, StringComparer.Ordinal);
		foreach (KeyValuePair<string, ZepColor> variable in source)
		{
			converted[variable.Key] = variable.Value.ToUnityColor();
		}
		return converted;
	}
}

}
