using System.Collections.Generic;
using Zep.Core.State;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.Settings
{

// NOTE: original FilePath args were unrecoverable (decompiler could not decode
// them and no .asset survived). Reconstructed to a per-project location.
[FilePath("ProjectSettings/ZepTagSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class ZepTagSettings : ScriptableSingleton<ZepTagSettings>
{
	public List<TagDefinitionRecord> TagDefinitions = new List<TagDefinitionRecord>();

	private TagDefinitionStore _store;

	public TagDefinitionStore Store => _store ?? (_store = new TagDefinitionStore(
		TagDefinitions,
		SaveSettings,
		ReportTagDefinitionProblem));

	public void SaveSettings()
	{
		Save(true);
	}

	// The store drops corrupt definitions instead of repairing them, and it
	// does not write the pruned view back — so the warning has to name the file
	// the user must fix.
	private static void ReportTagDefinitionProblem(string message)
	{
		Debug.LogWarning($"[Zep] {message} The file was left unchanged: {GetFilePath()}");
	}
}

}
