using System.Globalization;

namespace DeleteMan.ViewModels;

/// <summary>An sRGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Lowercase <c>#rrggbb</c>.</summary>
    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

    /// <summary>CSS hsl(): hue in degrees, saturation and lightness 0..1.</summary>
    public static Rgb FromHsl(double h, double s, double l)
    {
        var a = s * Math.Min(l, 1 - l);
        byte F(int n)
        {
            var k = (n + h / 30) % 12;
            if (k < 0)
            {
                k += 12;
            }
            var v = l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
            return (byte)Math.Clamp(Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);
        }
        return new Rgb(F(0), F(8), F(4));
    }
}

/// <summary>Fixed colours of the original stylesheet and the per-name colour.</summary>
public static class Palette
{
    public static readonly Rgb Accent = new(0x3b, 0x82, 0xf6);
    public static readonly Rgb Files = new(0x64, 0x74, 0x8b);
    public static readonly Rgb Other = new(0x4b, 0x55, 0x63);
    public static readonly Rgb Small = new(0x52, 0x52, 0x5b);
    public static readonly Rgb Error = new(0x7f, 0x1d, 0x1d);
    public static readonly Rgb Hatch = new(0x3a, 0x3f, 0x48);
    public static readonly Rgb DriveTrack = new(0x2a, 0x2e, 0x36);

    /// <summary>FNV-1a (32-bit) over the lowercase name's UTF-16 code units, mod 360.</summary>
    public static int HueFor(string name)
    {
        var h = 2166136261u;
        foreach (var c in name.ToLowerInvariant())
        {
            h ^= c;
            h = unchecked(h * 16777619u);
        }
        return (int)(h % 360);
    }

    /// <summary>Deterministic colour from a name: hsl(hue, 55 %, 42 %).</summary>
    public static Rgb ColorFor(string name) => Rgb.FromHsl(HueFor(name), 0.55, 0.42);
}
