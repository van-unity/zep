using System;

namespace Zep.Core.Theming
{

public readonly struct ZepColor : IEquatable<ZepColor>
{
	public float R { get; }

	public float G { get; }

	public float B { get; }

	public float A { get; }

	internal static ZepColor White => new ZepColor(1f, 1f, 1f, 1f);

	internal static ZepColor Black => new ZepColor(0f, 0f, 0f, 1f);

	public ZepColor(float r, float g, float b, float a)
	{
		R = r;
		G = g;
		B = b;
		A = a;
	}

	public ZepColor(float r, float g, float b)
		: this(r, g, b, 1f)
	{
	}

	public static ZepColor FromBytes(byte r, byte g, byte b, byte a = byte.MaxValue)
	{
		return new ZepColor((float)(int)r / 255f, (float)(int)g / 255f, (float)(int)b / 255f, (float)(int)a / 255f);
	}

	internal static ZepColor Lerp(ZepColor a, ZepColor b, float t)
	{
		t = ZepMath.Clamp01(t);
		return new ZepColor(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
	}

	internal static ZepColor FromHsv(float h, float s, float v, float a = 1f)
	{
		s = ZepMath.Clamp01(s);
		v = ZepMath.Clamp01(v);
		if (s <= 0f)
		{
			return new ZepColor(v, v, v, a);
		}
		h -= (float)Math.Floor(h);
		float scaledHue = h * 6f;
		int sector = (int)Math.Floor(scaledHue);
		float sectorFraction = scaledHue - (float)sector;
		float lowest = v * (1f - s);
		float falling = v * (1f - s * sectorFraction);
		float rising = v * (1f - s * (1f - sectorFraction));
		return sector switch
		{
			0 => new ZepColor(v, rising, lowest, a), 
			1 => new ZepColor(falling, v, lowest, a), 
			2 => new ZepColor(lowest, v, rising, a), 
			3 => new ZepColor(lowest, falling, v, a), 
			4 => new ZepColor(rising, lowest, v, a), 
			_ => new ZepColor(v, lowest, falling, a), 
		};
	}

	internal static void ToHsv(ZepColor color, out float h, out float s, out float v)
	{
		float r = color.R;
		float g = color.G;
		float b = color.B;
		float maxChannel = ((!(r > g)) ? ((g > b) ? g : b) : ((r > b) ? r : b));
		float minChannel = ((!(r < g)) ? ((g < b) ? g : b) : ((r < b) ? r : b));
		v = maxChannel;
		float chroma = maxChannel - minChannel;
		if (chroma <= 0f || maxChannel <= 0f)
		{
			h = 0f;
			s = 0f;
			return;
		}
		s = chroma / maxChannel;
		float hue = ((r >= maxChannel) ? ((g - b) / chroma) : ((!(g >= maxChannel)) ? (4f + (r - g) / chroma) : (2f + (b - r) / chroma)));
		hue /= 6f;
		if (hue < 0f)
		{
			hue += 1f;
		}
		h = hue;
	}

	public bool Equals(ZepColor other)
	{
		return R == other.R && G == other.G && B == other.B && A == other.A;
	}

	public override bool Equals(object obj)
	{
		return obj is ZepColor other && Equals(other);
	}

	public override int GetHashCode()
	{
		int hash = 17;
		hash = hash * 31 + R.GetHashCode();
		hash = hash * 31 + G.GetHashCode();
		hash = hash * 31 + B.GetHashCode();
		return hash * 31 + A.GetHashCode();
	}

	public static bool operator ==(ZepColor a, ZepColor b)
	{
		return a.Equals(b);
	}

	public static bool operator !=(ZepColor a, ZepColor b)
	{
		return !a.Equals(b);
	}
}

}
