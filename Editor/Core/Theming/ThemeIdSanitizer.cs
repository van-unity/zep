using System.Text;

namespace Zep.Core.Theming
{

public static class ThemeIdSanitizer
{
	public static string SanitizeToId(string displayName)
	{
		if (string.IsNullOrWhiteSpace(displayName))
		{
			return string.Empty;
		}
		StringBuilder id = new StringBuilder(displayName.Length);
		bool lastWasSeparator = false;
		string trimmedName = displayName.Trim();
		foreach (char character in trimmedName)
		{
			if ((character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9'))
			{
				id.Append(char.ToLowerInvariant(character));
				lastWasSeparator = false;
			}
			else if (!lastWasSeparator && id.Length > 0)
			{
				id.Append('-');
				lastWasSeparator = true;
			}
		}
		while (id.Length > 0 && id[id.Length - 1] == '-')
		{
			id.Length--;
		}
		if (id.Length == 0)
		{
			return string.Empty;
		}
		return "user-" + id.ToString();
	}
}

}
