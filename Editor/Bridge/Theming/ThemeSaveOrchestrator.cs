using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Theming;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{

internal sealed class ThemeSaveOrchestrator : IThemeSaveOrchestrator
{
	private const string DefaultThemesAssetPath = "Assets/Editor/Zep/DefaultThemes.asset";

	private readonly ZepThemeRegistry _registry;

	public ThemeSaveOrchestrator(ZepThemeRegistry registry)
	{
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
	}

	public ThemeSaveResult Save(string themeId, string displayName, bool isLight, IReadOnlyDictionary<string, ZepColor> variables, ThemeSaveMode mode)
	{
		if (string.IsNullOrWhiteSpace(themeId))
		{
			return ThemeSaveResult.Fail("Theme id is required.");
		}
		if (variables == null)
		{
			return ThemeSaveResult.Fail("Variables are required.");
		}
		try
		{
			if (mode == ThemeSaveMode.ProjectAsset)
			{
				return SaveProjectAsset(themeId, displayName, isLight, variables);
			}
			return SaveLocal(themeId, displayName, isLight, variables);
		}
		catch (Exception ex)
		{
			Debug.LogError($"[Zep] Theme save failed: {ex}");
			return ThemeSaveResult.Fail(ex.Message);
		}
		finally
		{
			_registry.RefreshRegistry();
			_registry.NotifyThemesChanged();
			ZepThemePreferences.NotifyPersistedThemeChanged();
		}
	}

	public bool DeleteUserTheme(string themeId, ZepThemeSource source)
	{
		if (string.IsNullOrWhiteSpace(themeId))
		{
			return false;
		}
		try
		{
			switch (source)
			{
			case ZepThemeSource.ProjectAsset:
			{
				string assetPath = FindUserAssetPathById(themeId);
				if (string.IsNullOrEmpty(assetPath))
				{
					return false;
				}
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseDeleteAssetCalls);
				return AssetDatabase.DeleteAsset(assetPath);
			}
			case ZepThemeSource.UserPrefs:
			{
				List<ZepThemeDefinition> themes = LoadUserPrefsThemes();
				if (themes.RemoveAll((ZepThemeDefinition theme) => string.Equals(theme.Id, themeId, StringComparison.Ordinal)) == 0)
				{
					return false;
				}
				UserPrefsThemeProvider.Save(themes);
				return true;
			}
			default:
				return false;
			}
		}
		catch (InvalidDataException ex)
		{
			Debug.LogWarning("[Zep] Theme delete failed: " + ex.Message);
			return false;
		}
		finally
		{
			_registry.RefreshRegistry();
			_registry.NotifyThemesChanged();
			ZepThemePreferences.NotifyPersistedThemeChanged();
		}
	}

	public bool ThemeIdExists(string themeId)
	{
		if (string.IsNullOrWhiteSpace(themeId))
		{
			return false;
		}
		foreach (ZepThemeDefinition availableDefinition in _registry.AvailableDefinitions)
		{
			if (string.Equals(availableDefinition.Id, themeId, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	public ThemeDefaultAccent GetDefaultAccentForMode(bool isLight)
	{
		ZepDefaultThemes defaultThemes = LoadOrCreateDefaultThemes();
		string defaultThemeId = defaultThemes.GetDefaultThemeId(isLight);
		ZepThemeDefinition definition = FindDefinitionById(defaultThemeId) ?? FindDefinitionById(isLight ? "ZepLight" : "ZepDark");
		if (definition != null)
		{
			return new ThemeDefaultAccent(ThemeDerivation.ExtractAccent(definition.Variables), definition.Id);
		}
		ZepColor accent = (isLight ? ZepColor.FromBytes(44, 98, 224) : ZepColor.FromBytes(76, 108, 219));
		return new ThemeDefaultAccent(accent, null);
	}

	private ZepThemeDefinition FindDefinitionById(string themeId)
	{
		if (string.IsNullOrEmpty(themeId))
		{
			return null;
		}
		return _registry.AvailableDefinitions.FirstOrDefault((ZepThemeDefinition definition) => string.Equals(definition.Id, themeId, StringComparison.Ordinal));
	}

	private static ZepDefaultThemes LoadOrCreateDefaultThemes()
	{
		ZepDefaultThemes defaultThemes = AssetDatabase.LoadAssetAtPath<ZepDefaultThemes>(DefaultThemesAssetPath);
		if (defaultThemes != null)
		{
			return defaultThemes;
		}
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseFindAssetsCalls);
		string[] foundGuids = AssetDatabase.FindAssets("t:ZepDefaultThemes");
		if (foundGuids != null && foundGuids.Length != 0)
		{
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
			string foundPath = AssetDatabase.GUIDToAssetPath(foundGuids[0]);
			ZepDefaultThemes relocatedThemes = AssetDatabase.LoadAssetAtPath<ZepDefaultThemes>(foundPath);
			if (relocatedThemes != null)
			{
				return relocatedThemes;
			}
		}
		EnsureFolderHierarchy("Assets/Editor/Zep");
		ZepDefaultThemes createdThemes = ScriptableObject.CreateInstance<ZepDefaultThemes>();
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateAssetCalls);
		AssetDatabase.CreateAsset(createdThemes, DefaultThemesAssetPath);
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseSaveAssetsCalls);
		AssetDatabase.SaveAssets();
		return createdThemes;
	}

	private ThemeSaveResult SaveProjectAsset(string themeId, string displayName, bool isLight, IReadOnlyDictionary<string, ZepColor> variables)
	{
		string userThemesFolder = "Assets/Editor/Zep/UserThemes";
		EnsureFolderHierarchy(userThemesFolder);
		string existingAssetPath = FindUserAssetPathById(themeId);
		ZepUserTheme userTheme;
		string assetPath;
		if (!string.IsNullOrEmpty(existingAssetPath))
		{
			userTheme = AssetDatabase.LoadAssetAtPath<ZepUserTheme>(existingAssetPath);
			assetPath = existingAssetPath;
		}
		else
		{
			userTheme = ScriptableObject.CreateInstance<ZepUserTheme>();
			assetPath = userThemesFolder + "/" + themeId + ".asset";
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
			assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateAssetCalls);
			AssetDatabase.CreateAsset(userTheme, assetPath);
		}
		userTheme.Apply(themeId, displayName, isLight, variables);
		EditorUtility.SetDirty(userTheme);
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseSaveAssetsCalls);
		AssetDatabase.SaveAssets();
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseImportAssetCalls);
		AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
		return ThemeSaveResult.Ok(themeId);
	}

	private ThemeSaveResult SaveLocal(string themeId, string displayName, bool isLight, IReadOnlyDictionary<string, ZepColor> variables)
	{
		List<ZepThemeDefinition> themes = LoadUserPrefsThemes();
		int existingIndex = themes.FindIndex((ZepThemeDefinition theme) => string.Equals(theme.Id, themeId, StringComparison.Ordinal));
		ZepThemeDefinition definition = new ZepThemeDefinition(themeId, displayName, isLight, ZepThemeSource.UserPrefs, new Dictionary<string, ZepColor>(variables, StringComparer.Ordinal));
		if (existingIndex >= 0)
		{
			themes[existingIndex] = definition;
		}
		else
		{
			themes.Add(definition);
		}
		UserPrefsThemeProvider.Save(themes);
		return ThemeSaveResult.Ok(themeId);
	}

	private static List<ZepThemeDefinition> LoadUserPrefsThemes()
	{
		UserPrefsThemeProvider provider = new UserPrefsThemeProvider();
		List<ZepThemeDefinition> themes = new List<ZepThemeDefinition>();
		foreach (ZepThemeDefinition theme in provider.GetThemes())
		{
			themes.Add(theme);
		}
		return themes;
	}

	private static string FindUserAssetPathById(string themeId)
	{
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseFindAssetsCalls);
		string[] themeGuids = AssetDatabase.FindAssets("t:ZepUserTheme");
		foreach (string themeGuid in themeGuids)
		{
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
			string assetPath = AssetDatabase.GUIDToAssetPath(themeGuid);
			if (!string.IsNullOrEmpty(assetPath))
			{
				ZepUserTheme userTheme = AssetDatabase.LoadAssetAtPath<ZepUserTheme>(assetPath);
				if (userTheme != null && string.Equals(userTheme.ThemeId, themeId, StringComparison.Ordinal))
				{
					return assetPath;
				}
			}
		}
		return null;
	}

	private static void EnsureFolderHierarchy(string folder)
	{
		if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
		{
			return;
		}
		string[] segments = folder.Split('/');
		string parentFolder = segments[0];
		for (int i = 1; i < segments.Length; i++)
		{
			string childFolder = parentFolder + "/" + segments[i];
			if (!AssetDatabase.IsValidFolder(childFolder))
			{
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateFolderCalls);
				AssetDatabase.CreateFolder(parentFolder, segments[i]);
			}
			parentFolder = childFolder;
		}
	}
}

}
