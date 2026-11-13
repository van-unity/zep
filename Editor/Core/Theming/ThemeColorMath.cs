namespace Zep.Core.Theming
{

internal static class ThemeColorMath
{
	// WCAG 2.x relative luminance. The channel values are sRGB and MUST be
	// linearised first; summing them raw (as this did until 2026-07-29) inflates
	// mid-tones badly enough to flip decisions — ContrastingForeground was
	// handing back the LOWER-contrast label on saturated accents.
	public static float RelativeLuminance(ZepColor c)
	{
		return 0.2126f * ToLinear(c.R) + 0.7152f * ToLinear(c.G) + 0.0722f * ToLinear(c.B);
	}

	private static float ToLinear(float channel)
	{
		var v = ZepMath.Clamp01(channel);
		return (v <= 0.03928f)
			? v / 12.92f
			: System.MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
	}

	public static ZepColor Lighten(ZepColor c, float amount)
	{
		return ZepColor.Lerp(c, ZepColor.White, ZepMath.Clamp01(amount));
	}

	public static ZepColor Darken(ZepColor c, float amount)
	{
		return ZepColor.Lerp(c, ZepColor.Black, ZepMath.Clamp01(amount));
	}

	public static ZepColor WithAlpha(ZepColor c, float alpha)
	{
		return new ZepColor(c.R, c.G, c.B, ZepMath.Clamp01(alpha));
	}

	public static ZepColor Mix(ZepColor a, ZepColor b, float t)
	{
		return ZepColor.Lerp(a, b, ZepMath.Clamp01(t));
	}

	// Pick whichever label actually measures better against this background,
	// rather than guessing from a luminance threshold. A mid-luminance accent
	// sits near the pivot where the guess is least reliable, and that is exactly
	// where button labels live.
	public static ZepColor ContrastingForeground(ZepColor background)
	{
		var dark = new ZepColor(0.05f, 0.06f, 0.08f, 1f);
		var light = new ZepColor(0.98f, 0.985f, 1f, 1f);
		return (ContrastRatio(dark, background) >= ContrastRatio(light, background)) ? dark : light;
	}

	public static float ContrastRatio(ZepColor a, ZepColor b)
	{
		float luminanceA = RelativeLuminance(a) + 0.05f;
		float luminanceB = RelativeLuminance(b) + 0.05f;
		return (luminanceA > luminanceB) ? (luminanceA / luminanceB) : (luminanceB / luminanceA);
	}
}

}
