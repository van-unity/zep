using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.FileOperations;
using Zep.Core.Theming;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{

internal sealed class UserPrefsThemeProvider : IZepThemeProvider
{
	[Serializable]
	private sealed class UserPrefsThemeBlob
	{
		public List<UserPrefsTheme> themes = new List<UserPrefsTheme>();
	}

	[Serializable]
	private sealed class UserPrefsTheme
	{
		public string id;

		public string displayName;

		public bool isLight;

		public List<UserPrefsThemeEntry> variables = new List<UserPrefsThemeEntry>();
	}

	[Serializable]
	private sealed class UserPrefsThemeEntry
	{
		public string name;

		public Color value;
	}

	public const string EditorPrefsKey = "Zep.Editor.UI.UserPrefsThemes";

	public IEnumerable<ZepThemeDefinition> GetThemes()
	{
		if (!EditorPrefs.HasKey(EditorPrefsKey))
		{
			yield break;
		}
		string json = EditorPrefs.GetString(EditorPrefsKey, string.Empty);
		if (string.IsNullOrWhiteSpace(json))
		{
			yield break;
		}
		UserPrefsThemeBlob blob;
		bool hasThemeCollection;
		try
		{
			var document = JsonText.Parse(json);
			hasThemeCollection = JsonText.TryGetMember(document, "themes", out var themes)
				&& JsonText.AsArray(themes) != null;
			blob = JsonUtility.FromJson<UserPrefsThemeBlob>(json);
		}
		catch (Exception ex)
		{
			throw new InvalidDataException("Local theme storage is malformed.", ex);
		}
		if (!hasThemeCollection || blob?.themes == null)
		{
			throw new InvalidDataException("Local theme storage must contain a theme collection.");
		}
		HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (UserPrefsTheme theme in blob.themes)
		{
			if (theme == null || string.IsNullOrWhiteSpace(theme.id))
			{
				throw new InvalidDataException("Local theme storage contains a theme without an id.");
			}
			if (!seenIds.Add(theme.id))
			{
				throw new InvalidDataException("Local theme storage contains duplicate theme ids.");
			}
			Dictionary<string, ZepColor> variables = new Dictionary<string, ZepColor>(StringComparer.Ordinal);
			if (theme.variables != null)
			{
				foreach (UserPrefsThemeEntry entry in theme.variables)
				{
					if (entry != null && !string.IsNullOrEmpty(entry.name))
					{
						variables[entry.name] = entry.value.ToZepColor();
					}
				}
			}
			yield return new ZepThemeDefinition(theme.id, string.IsNullOrWhiteSpace(theme.displayName) ? theme.id : theme.displayName, theme.isLight, ZepThemeSource.UserPrefs, variables);
		}
	}

	public static void Save(IEnumerable<ZepThemeDefinition> definitions)
	{
		if (definitions == null)
		{
			throw new ArgumentNullException(nameof(definitions));
		}
		UserPrefsThemeBlob blob = new UserPrefsThemeBlob();
		foreach (ZepThemeDefinition definition in definitions)
		{
			if (definition == null)
			{
				continue;
			}
			UserPrefsTheme serializedTheme = new UserPrefsTheme
			{
				id = definition.Id,
				displayName = definition.DisplayName,
				isLight = definition.IsLight,
				variables = new List<UserPrefsThemeEntry>()
			};
			foreach (KeyValuePair<string, ZepColor> variable in definition.Variables)
			{
				serializedTheme.variables.Add(new UserPrefsThemeEntry
				{
					name = variable.Key,
					value = variable.Value.ToUnityColor()
				});
			}
			blob.themes.Add(serializedTheme);
		}
		EditorPrefs.SetString(EditorPrefsKey, JsonUtility.ToJson(blob));
	}

	public static void Clear()
	{
		EditorPrefs.DeleteKey(EditorPrefsKey);
	}
}

}
