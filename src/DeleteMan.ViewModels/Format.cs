// Display formatting (port of format.ts). Sizes use binary units with the labels Windows Explorer uses.

using System.Globalization;

namespace DeleteMan.ViewModels;

public static class Format
{
    private const ulong Kb = 1024;

    /// <summary>Largest first, so the first unit a value reaches is the one it is shown in.</summary>
    private static readonly (string Label, ulong Size)[] Units =
    [
        ("PB", 1UL << 50),
        ("TB", 1UL << 40),
        ("GB", 1UL << 30),
        ("MB", 1UL << 20),
        ("KB", 1UL << 10),
    ];

    /// <summary>Picks the most fitting binary unit (e.g. 3.7 TB, 952 GB, 158 MB, 512 B).</summary>
    public static string Bytes(ulong n)
    {
        if (n < Kb)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " B";
        }
        var unit = Units[^1];
        foreach (var u in Units)
        {
            if (n >= u.Size)
            {
                unit = u;
                break;
            }
        }
        var v = (double)n / unit.Size;
        // One decimal for readability, dropped once the number is big enough not to need it.
        return $"{ToFixed(v, v >= 100 ? 0 : 1)} {unit.Label}";
    }

    /// <summary>en-US thousands grouping.</summary>
    public static string Count(ulong n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"3 files", "1 file".</summary>
    public static string Plural(ulong n, string word) => $"{Count(n)} {(n == 1 ? word : word + "s")}";

    /// <summary>m:ss or h:mm:ss.</summary>
    public static string Duration(ulong ms)
    {
        var total = ms / 1000;
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = (total % 60).ToString("00", CultureInfo.InvariantCulture);
        return h > 0
            ? $"{h.ToString(CultureInfo.InvariantCulture)}:{m.ToString("00", CultureInfo.InvariantCulture)}:{s}"
            : $"{m.ToString(CultureInfo.InvariantCulture)}:{s}";
    }

    /// <summary>Fraction 0..1 to "45 %".</summary>
    public static string Percent(double fraction) =>
        JsRound(fraction * 100).ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>Number.prototype.toFixed for non-negative values: exact-value rounding, ties up.</summary>
    private static string ToFixed(double v, int digits) =>
        v.ToString(digits == 0 ? "F0" : "F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Math.round: halves round towards positive infinity.</summary>
    private static double JsRound(double v) => Math.Floor(v + 0.5);
}
