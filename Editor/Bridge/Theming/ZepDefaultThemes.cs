using UnityEngine;

namespace Zep.Editor.Bridge.Theming
{

[CreateAssetMenu(menuName = "Zep/Default Themes", fileName = "DefaultThemes")]
public sealed class ZepDefaultThemes : ScriptableObject
{
	[SerializeField]
	private string _defaultLightThemeId = "ZepLight";

	[SerializeField]
	private string _defaultDarkThemeId = "ZepDark";

	public string DefaultLightThemeId => _defaultLightThemeId;

	public string DefaultDarkThemeId => _defaultDarkThemeId;

	public string GetDefaultThemeId(bool isLight)
	{
		return isLight ? _defaultLightThemeId : _defaultDarkThemeId;
	}
}

}
