namespace DeleteMan.ViewModels.Tests;

public class PaletteAndPathsTests
{
    [Fact]
    public void Palette_constants_match_the_original_stylesheet()
    {
        Assert.Equal("#3b82f6", Palette.Accent.ToHex());
        Assert.Equal("#64748b", Palette.Files.ToHex());
        Assert.Equal("#4b5563", Palette.Other.ToHex());
        Assert.Equal("#52525b", Palette.Small.ToHex());
        Assert.Equal("#7f1d1d", Palette.Error.ToHex());
        Assert.Equal("#3a3f48", Palette.Hatch.ToHex());
        Assert.Equal("#2a2e36", Palette.DriveTrack.ToHex());
    }

    [Fact]
    public void Rgb_hex_and_hsl_conversions()
    {
        Assert.Equal("#ff0000", new Rgb(255, 0, 0).ToHex());
        Assert.Equal(new Rgb(255, 0, 0), Rgb.FromHsl(0, 1.0, 0.5));
        Assert.Equal(new Rgb(0, 255, 0), Rgb.FromHsl(120, 1.0, 0.5));
        Assert.Equal(new Rgb(0, 0, 255), Rgb.FromHsl(240, 1.0, 0.5));
        Assert.Equal(new Rgb(128, 128, 128), Rgb.FromHsl(37, 0.0, 0.502));
        Assert.Equal(new Rgb(0, 0, 0), Rgb.FromHsl(300, 0.55, 0.0));
    }

    /// <summary>FNV-1a 32-bit over the lower-cased name, hue = hash mod 360 (standard test vectors).</summary>
    [Theory]
    [InlineData("", 61)]           // offset basis 2166136261 % 360
    [InlineData("a", 340)]         // 0xe40c292c
    [InlineData("A", 340)]         // case-insensitive
    [InlineData("foobar", 160)]    // 0xbf9cf968
    [InlineData("FooBar", 160)]
    public void Hue_is_fnv1a_of_the_lowercase_name_mod_360(string name, int expected)
    {
        Assert.Equal(expected, Palette.HueFor(name));
    }

    [Fact]
    public void Color_for_a_name_is_hsl_55_42_of_its_hue_and_deterministic()
    {
        var c1 = Palette.ColorFor("Users");
        var c2 = Palette.ColorFor("users");
        Assert.Equal(c1, c2);
        Assert.Equal(Rgb.FromHsl(Palette.HueFor("Users"), 0.55, 0.42), c1);
        Assert.NotEqual(Palette.ColorFor("a"), Palette.ColorFor("foobar"));
    }

    [Theory]
    [InlineData(@"C:\", "a", @"C:\a")]
    [InlineData(@"C:\x", "a", @"C:\x\a")]
    [InlineData(@"C:\x\", "a.bin", @"C:\x\a.bin")]
    public void Join_path_adds_exactly_one_separator(string dir, string name, string expected)
    {
        Assert.Equal(expected, Paths.JoinPath(dir, name));
    }

    [Theory]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"c:\windows\", true)]
    [InlineData(@"C:\Program Files", true)]
    [InlineData(@"C:\Program Files (x86)", true)]
    [InlineData(@"C:\ProgramData", true)]
    [InlineData(@"C:\Users\steve", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Users\steve\Downloads", false)]
    [InlineData(@"D:\Windows", false)]
    [InlineData(@"C:\Windowsx", false)]
    [InlineData(@"C:\Windows\System32", false)]
    public void System_roots_and_user_profiles_are_recognised(string path, bool expected)
    {
        Assert.Equal(expected, Paths.IsSystemRoot(path));
    }
}
