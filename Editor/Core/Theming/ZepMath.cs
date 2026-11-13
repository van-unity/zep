namespace Zep.Core.Theming
{

internal static class ZepMath
{
	public static float Clamp01(float v)
	{
		return (v < 0f) ? 0f : ((v > 1f) ? 1f : v);
	}
}

}
