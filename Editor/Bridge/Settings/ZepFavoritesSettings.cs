using Zep.Core.Indexing;
using System.Collections.Generic;
using System.IO;
using Zep.Core.State;
using Zep.Editor.Bridge.Indexing;
using UnityEditor;

namespace Zep.Editor.Bridge.Settings
{

// Per-user home of the sidebar's Favorites section — favorites are a
// personal shortcut list, as in Finder and Explorer, so they live in Unity's
// per-user project folder rather than in the version-controlled
// ProjectSettings. Same shape as ZepTagSettings: the singleton owns
// the serialized list, the Core store owns the invariants and mutates that
// list in place.
[FilePath("UserSettings/ZepFavoritesSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class ZepFavoritesSettings : ScriptableSingleton<ZepFavoritesSettings>
{
	// Where favorites lived before they became per-user. Read once, by the
	// first access that finds no per-user file, so a user keeps the favorites
	// they had. The legacy file is left in place: removing a shared file is
	// the project owner's call, and a teammate who has not migrated yet still
	// imports from it.
	internal const string LegacyProjectAssetPath = "ProjectSettings/ZepFavoritesSettings.asset";

	public List<string> FavoriteFolderPaths = new List<string>();

	private FavoriteFolderStore _store;

	public FavoriteFolderStore Store => _store ?? (_store = CreateStore());

	public void SaveSettings()
	{
		Save(true);
	}

	private FavoriteFolderStore CreateStore()
	{
		if (FavoriteFolderPaths.Count == 0
			&& !File.Exists(GetFilePath())
			&& TryImportLegacyFavorites(LegacyProjectAssetPath, FavoriteFolderPaths))
		{
			SaveSettings();
		}

		return new FavoriteFolderStore(FavoriteFolderPaths, SaveSettings);
	}

	// Reads the `FavoriteFolderPaths` sequence out of a pre-per-user favorites
	// asset. The file is this class's own text-serialized YAML, so the block
	// is read directly rather than deserialized: deserializing would construct
	// a second ScriptableSingleton instance, which Unity reports as an error.
	// Returns false when there is no such file or it lists no paths; the
	// store sanitizes whatever was read on first contact, exactly as it does
	// for the live file.
	internal static bool TryImportLegacyFavorites(string legacyAssetPath, List<string> into)
	{
		if (string.IsNullOrEmpty(legacyAssetPath) || into == null || !File.Exists(legacyAssetPath))
		{
			return false;
		}

		var imported = false;
		var inPathsBlock = false;
		foreach (var rawLine in File.ReadLines(legacyAssetPath))
		{
			var line = rawLine.TrimEnd();
			if (!inPathsBlock)
			{
				inPathsBlock = line.TrimStart().StartsWith("FavoriteFolderPaths:", System.StringComparison.Ordinal);
				continue;
			}

			var item = line.TrimStart();
			if (!item.StartsWith("- ", System.StringComparison.Ordinal))
			{
				break;
			}

			var value = UnityMetaFileReader.DecodeYamlScalar(
				item.Substring(2).Trim());

			if (value.Length > 0)
			{
				into.Add(value);
				imported = true;
			}
		}

		return imported;
	}
}

}
