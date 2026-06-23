using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Roma.Host;

// Derives a dark-theme variant of a light SVG by recoloring its fill/stroke colors, using ILSpy's
// exact ThemeManager.AdjustForDarkTheme math (RGB -> HSL, invert lightness with l = 1 - l^1.2,
// desaturate intense colors, HSL -> RGB). Used at runtime to materialize dark icons on the fly; no
// dark SVG assets are committed — the light SVG is the only source of truth.
internal static class RomaDarkSvg
{
    private static readonly Regex HexColor = new(@"#([0-9a-fA-F]{6})\b", RegexOptions.Compiled);

    public static string Recolor(string svg) => HexColor.Replace(svg, static m =>
    {
        var hex = m.Groups[1].Value;
        byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
        byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
        byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
        var (nr, ng, nb) = AdjustForDarkTheme(r, g, b);
        return $"#{nr:x2}{ng:x2}{nb:x2}";
    });

    // ILSpy's ThemeManager.AdjustForDarkTheme(Color) on raw RGB. Shared by the SVG icon recolor and
    // the syntax-highlighting dark adaptation (ThemeManager.GetColorForDarkTheme).
    public static (byte r, byte g, byte b) AdjustRgb(byte r, byte g, byte b) => AdjustForDarkTheme(r, g, b);

    private static (byte r, byte g, byte b) AdjustForDarkTheme(byte r, byte g, byte b)
    {
        var (h, s, l) = RgbToHsl(r, g, b);
        l = 1f - MathF.Pow(l, 1.2f);
        if (s > 0.75f && l < 0.75f)
        {
            s *= 0.75f;
            l *= 1.2f;
        }
        return HslToRgb(h, s, l);
    }

    private static (float h, float s, float l) RgbToHsl(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float l = (max + min) / 2f;
        if (max == min)
            return (0f, 0f, l);

        float d = max - min;
        float s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        float h = max == rf ? (gf - bf) / d + (gf < bf ? 6f : 0f)
                : max == gf ? (bf - rf) / d + 2f
                : (rf - gf) / d + 4f;
        return (h * 60f, s, l);
    }

    private static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
    {
        var c = (1f - Math.Abs(2f * l - 1f)) * s;
        h = h % 360f / 60f;
        var x = c * (1f - Math.Abs(h % 2f - 1f));
        var (r1, g1, b1) = (int)Math.Floor(h) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        var m = l - c / 2f;
        return (Clamp(r1 + m), Clamp(g1 + m), Clamp(b1 + m));
    }

    private static byte Clamp(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
