using Zep.Core.Application.Ports;
using UnityEditor;

namespace Zep.Editor.Bridge
{

internal sealed class UnityShellReveal : IShellRevealService
{
	public void Reveal(string absolutePath)
	{
		if (!string.IsNullOrEmpty(absolutePath))
		{
			EditorUtility.RevealInFinder(absolutePath);
		}
	}
}

}
