using DeleteMan.ViewModels;
using Windows.UI;

namespace DeleteMan.App.Shell;

/// <summary>Conversions from view-model colours to WinUI colours.</summary>
internal static class RgbExtensions
{
    /// <summary>The opaque <see cref="Color"/> of <paramref name="rgb"/>.</summary>
    public static Color ToColor(this Rgb rgb) => Color.FromArgb(0xFF, rgb.R, rgb.G, rgb.B);
}
