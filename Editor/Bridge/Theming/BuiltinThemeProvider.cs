using System.Collections.Generic;
using System.IO;
using Zep.Core.Theming;

namespace Zep.Editor.Bridge.Theming
{

internal sealed class BuiltinThemeProvider : IZepThemeProvider
{
	public IEnumerable<ZepThemeDefinition> GetThemes()
	{
		foreach (BuiltinThemeFile themeFile in BuiltinThemeFiles.Enumerate())
		{
			string uss;
			try
			{
				uss = File.ReadAllText(themeFile.AbsolutePath);
			}
			catch
			{
				continue;
			}
			Dictionary<string, ZepColor> variables = UssVariableParser.Parse(uss);
			yield return new ZepThemeDefinition(themeFile.Id, themeFile.DisplayName, themeFile.IsLight, ZepThemeSource.Builtin, variables);
		}
	}
}

}
