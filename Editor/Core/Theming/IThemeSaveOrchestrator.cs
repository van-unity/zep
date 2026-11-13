using System.Collections.Generic;

namespace Zep.Core.Theming
{

public interface IThemeSaveOrchestrator
{
	ThemeSaveResult Save(string themeId, string displayName, bool isLight, IReadOnlyDictionary<string, ZepColor> variables, ThemeSaveMode mode);

	bool DeleteUserTheme(string themeId, ZepThemeSource source);

	bool ThemeIdExists(string themeId);

	ThemeDefaultAccent GetDefaultAccentForMode(bool isLight);
}

}
