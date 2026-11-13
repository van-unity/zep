using System;
using System.Collections.Generic;

namespace Zep.Core.Theming
{

public sealed class ZepThemeDefinition
{
	public string Id { get; }

	public string DisplayName { get; }

	public bool IsLight { get; }

	public ZepThemeSource Source { get; }

	public IReadOnlyDictionary<string, ZepColor> Variables { get; }

	public ZepThemeDefinition(string id, string displayName, bool isLight, ZepThemeSource source, IReadOnlyDictionary<string, ZepColor> variables)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			throw new ArgumentException("Theme id must be non-empty.", nameof(id));
		}
		if (variables == null)
		{
			throw new ArgumentNullException(nameof(variables));
		}
		Id = id;
		DisplayName = (string.IsNullOrWhiteSpace(displayName) ? id : displayName);
		IsLight = isLight;
		Source = source;
		Variables = variables;
	}
}

}
