using UnityEditor;

namespace Zep.Editor.Bridge.Settings
{

// NOTE: original FilePath args were unrecoverable (decompiler could not decode
// them and no .asset survived). Reconstructed to a per-project location.
[FilePath("ProjectSettings/ZepSettings.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class ZepSettings : ScriptableSingleton<ZepSettings>
{
	public bool AutoRefreshIndex = true;

	public bool ShowStatusBar = true;

	public bool ShowFoldersOnlyInTree = true;

	public bool UseNativeFileSystemAssetSource = true;

	public bool EnablePersistentIndexCache = true;

	// Recovered seam: default for the compress prompt. When on, .zip archives keep
	// sibling/nested .meta files so they re-import into Unity with guids intact.
	public bool IncludeMetaFilesInArchive = true;

	// Recovered seam: when on, compressing shows the .meta prompt; when off it
	// skips straight to the archive using IncludeMetaFilesInArchive.
	public bool PromptForMetaOnCompress = true;

	public void SaveSettings()
	{
		Save(true);
	}
}

}
