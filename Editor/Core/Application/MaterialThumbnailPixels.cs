using System;

namespace Zep.Core.Application
{
    /// <summary>
    /// The CPU half of a material thumbnail: everything done to the rendered
    /// RGBA32 pixels between the GPU readback and the texture upload. Changing
    /// any output here changes the look, so the provider's look version has to
    /// be bumped with it.
    /// </summary>
    public static class MaterialThumbnailPixels
    {
        // UnityEngine.Mathf's own definitions, so the pixel code below computes
        // exactly what it did when it called Unity: every operand passes through
        // the same float parameters and the same System.Math calls.
        private static class Mathf
        {
            internal static float Abs(float f) => Math.Abs(f);

            internal static float Max(float a, float b) => a > b ? a : b;

            internal static float Sqrt(float f) => (float)Math.Sqrt(f);

            internal static float Pow(float f, float p) => (float)Math.Pow(f, p);

            internal static float Exp(float power) => (float)Math.Exp(power);

            internal static int RoundToInt(float f) => (int)Math.Round(f);

            internal static int Clamp(int value, int min, int max) =>
                value < min ? min : value > max ? max : value;

            internal static float Clamp01(float value) =>
                value < 0f ? 0f : value > 1f ? 1f : value;

            // The sRGB transfer function as the engine evaluates it.
            internal static float LinearToGammaSpace(float value)
            {
                if (value <= 0f)
                {
                    return 0f;
                }

                if (value <= 0.0031308f)
                {
                    return 12.92f * value;
                }

                if (value < 1f)
                {
                    return 1.055f * Pow(value, 0.4166667f) - 0.055f;
                }

                return Pow(value, 0.45454545f);
            }
        }

        private static byte[] _backgroundRgb;

        // The rig's readback target is linear-typed (readbacks from sRGB
        // textures apply backend-dependent conversions — the source of the
        // first in-editor pass's dark thumbnails), so in Linear color space
        // the encode to display-sRGB happens here, deterministically, via a
        // 256-entry LUT. The caller skips it for Gamma projects: their pixels
        // are already display-ready. Alpha is coverage, never encoded.
        private static byte[] _srgbEncodeLut;

        public static void EncodeLinearToSrgb(byte[] rgba)
        {
            if (_srgbEncodeLut == null)
            {
                var lut = new byte[256];
                for (var i = 0; i < 256; i++)
                {
                    lut[i] = (byte)Mathf.Clamp(
                        Mathf.RoundToInt(Mathf.LinearToGammaSpace(i / 255f) * 255f), 0, 255);
                }
                _srgbEncodeLut = lut;
            }

            for (var i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = _srgbEncodeLut[rgba[i]];
                rgba[i + 1] = _srgbEncodeLut[rgba[i + 1]];
                rgba[i + 2] = _srgbEncodeLut[rgba[i + 2]];
            }
        }

        // Mild filmic-style lift + contrast on the rendered pixels (applied
        // after the sRGB encode, before the backdrop composite) — the "it
        // went through a tonemapper" polish that separates a product shot
        // from a raw render. Endpoints pin to 0/255; alpha is coverage and
        // stays untouched.
        private static byte[] _toneCurveLut;

        public static void ApplyToneCurve(byte[] rgba)
        {
            if (_toneCurveLut == null)
            {
                var lut = new byte[256];
                for (var i = 0; i < 256; i++)
                {
                    var x = Mathf.Pow(i / 255f, 0.92f);
                    x = (x - 0.5f) * 1.08f + 0.5f;
                    lut[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(x * 255f), 0, 255);
                }
                _toneCurveLut = lut;
            }

            for (var i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = _toneCurveLut[rgba[i]];
                rgba[i + 1] = _toneCurveLut[rgba[i + 1]];
                rgba[i + 2] = _toneCurveLut[rgba[i + 2]];
            }
        }

        // Anti-aliased rounded corners baked into the final alpha so every
        // thumbnail reads as a designed tile instead of a raw square render.
        // The icon elements draw backgroundImage with straight alpha, so the
        // theme background shows through the corners on any theme. Skipped
        // below 64px where the mask geometry degenerates (real thumbs are
        // always ThumbnailSize; small sizes only occur in tests).
        private static byte[] _cornerMask;

        public static void ApplyRoundedCornerMask(byte[] rgba, int width, int height)
        {
            if (width != height || width < 64)
            {
                return;
            }

            if (_cornerMask == null || _cornerMask.Length != width * height)
            {
                var mask = new byte[width * height];
                var radius = width * 0.14f;
                var half = width * 0.5f;
                var inner = half - radius;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var qx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - inner, 0f);
                        var qy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - inner, 0f);
                        var dist = Mathf.Sqrt(qx * qx + qy * qy) - radius;
                        // ~1px anti-aliased edge.
                        var coverage = Mathf.Clamp01(0.5f - dist);
                        mask[y * width + x] = (byte)(coverage * 255f);
                    }
                }
                _cornerMask = mask;
            }

            for (var i = 0; i < _cornerMask.Length; i++)
            {
                var m = _cornerMask[i];
                if (m != 255)
                {
                    rgba[i * 4 + 3] = (byte)(rgba[i * 4 + 3] * m / 255);
                }
            }
        }

        // The camera clears to transparent black; the studio backdrop is
        // composited here on the CPU instead of drawn as scene geometry, so
        // there is no cross-pipeline background-shader to break. Standard
        // alpha blending writes premultiplied-by-alpha color against a black
        // clear, so "src + bg * (1 - a)" is the correct over-operator for
        // both opaque and transparent materials.
        public static void CompositeOverBackground(byte[] rgba, int width, int height)
        {
            var background = EnsureBackground(width, height);
            var pixelCount = width * height;
            for (var i = 0; i < pixelCount; i++)
            {
                var offset = i * 4;
                var alpha = rgba[offset + 3];
                if (alpha == 255)
                {
                    continue;
                }

                var inverse = 255 - alpha;
                var bgOffset = i * 3;
                rgba[offset] = (byte)Math.Min(255, rgba[offset] + background[bgOffset] * inverse / 255);
                rgba[offset + 1] = (byte)Math.Min(255, rgba[offset + 1] + background[bgOffset + 1] * inverse / 255);
                rgba[offset + 2] = (byte)Math.Min(255, rgba[offset + 2] + background[bgOffset + 2] * inverse / 255);
                rgba[offset + 3] = 255;
            }
        }

        // Studio product-shot backdrop, precomputed once as packed RGB
        // triplets: a cool-slate spotlight falloff centered behind the upper
        // half of the sphere, plus a soft elliptical contact shadow along the
        // bottom sliver the camera framing leaves free — the shadow peeking
        // out under the sphere is what makes it read as sitting in a studio
        // instead of floating in space. Row 0 is the bottom in texture space.
        private static byte[] EnsureBackground(int width, int height)
        {
            if (_backgroundRgb != null && _backgroundRgb.Length == width * height * 3)
            {
                return _backgroundRgb;
            }

            var background = new byte[width * height * 3];
            for (var y = 0; y < height; y++)
            {
                var v = height <= 1 ? 0f : (float)y / (height - 1);
                for (var x = 0; x < width; x++)
                {
                    var u = width <= 1 ? 0f : (float)x / (width - 1);

                    // Spotlight: brightest slightly above center, falling to
                    // deep slate at the corners.
                    var sx = u - 0.5f;
                    var sy = (v - 0.62f) * 1.1f;
                    var spot = Mathf.Clamp01(1f - Mathf.Sqrt(sx * sx + sy * sy) * 1.35f);
                    var tone = 26f + 78f * spot * spot;

                    // Contact shadow: tight ellipse hugging the sphere's
                    // resting point — tucked low and slightly darker so the
                    // thin visible rim still grounds the near-edge-to-edge
                    // sphere framing.
                    var ex = sx / 0.30f;
                    var ey = (v - 0.032f) / 0.04f;
                    var falloff = ex * ex + ey * ey;
                    if (falloff < 6f)
                    {
                        tone *= 1f - 0.6f * Mathf.Exp(-falloff);
                    }

                    var offset = (y * width + x) * 3;
                    background[offset] = (byte)(tone * 0.94f);
                    background[offset + 1] = (byte)tone;
                    background[offset + 2] = (byte)(tone * 1.14f);
                }
            }

            _backgroundRgb = background;
            return background;
        }
    }
}
