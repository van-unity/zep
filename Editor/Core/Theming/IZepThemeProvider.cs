using System.Collections.Generic;

namespace Zep.Core.Theming
{

public interface IZepThemeProvider
{
	IEnumerable<ZepThemeDefinition> GetThemes();
}

}
