using System;
using System.Collections.Generic;
using Zep.Core.Theming;
using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{

public sealed class ZepUserTheme : ScriptableObject
{
	[Serializable]
	public sealed class ColorEntry
	{
		public string Name;

		public Color Value;
	}

	[SerializeField]
	private string _themeId;

	[SerializeField]
	private string _displayName;

	[SerializeField]
	private bool _isLight;

	[SerializeField]
	private List<ColorEntry> _variables = new List<ColorEntry>();

	public string ThemeId => _themeId;

	public string DisplayName => _displayName;

	public bool IsLight => _isLight;

	public IReadOnlyList<ColorEntry> Variables => _variables;

	public void Apply(string themeId, string displayName, bool isLight, IReadOnlyDictionary<string, ZepColor> variables)
	{
		if (string.IsNullOrWhiteSpace(themeId))
		{
			throw new ArgumentException("Theme id must be non-empty.", nameof(themeId));
		}
		if (variables == null)
		{
			throw new ArgumentNullException(nameof(variables));
		}
		_themeId = themeId;
		_displayName = (string.IsNullOrWhiteSpace(displayName) ? themeId : displayName);
		_isLight = isLight;
		_variables.Clear();
		foreach (KeyValuePair<string, ZepColor> variable in variables)
		{
			_variables.Add(new ColorEntry
			{
				Name = variable.Key,
				Value = variable.Value.ToUnityColor()
			});
		}
	}

	public ZepThemeDefinition ToDefinition()
	{
		Dictionary<string, ZepColor> variables = new Dictionary<string, ZepColor>(_variables.Count, StringComparer.Ordinal);
		foreach (ColorEntry variable in _variables)
		{
			if (!string.IsNullOrEmpty(variable.Name))
			{
				variables[variable.Name] = variable.Value.ToZepColor();
			}
		}
		return new ZepThemeDefinition(_themeId, _displayName, _isLight, ZepThemeSource.ProjectAsset, variables);
	}
}

}
