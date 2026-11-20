namespace DeleteMan.Engine.Tests;

/// <summary>Port of the winfs.rs WidePath tests.</summary>
public class WidePathTests
{
    [Fact]
    public void Components_build_prefixed_paths()
    {
        var root = WidePath.FromComponents("C:");
        Assert.Equal(@"C:\", root.Display);
        Assert.True(root.IsVolumeRoot);

        var deep = WidePath.FromComponents("C:", "Users", "x");
        Assert.Equal(@"C:\Users\x", deep.Display);
        Assert.Equal(@"\\?\C:\Users\x", deep.Wide);
        Assert.False(deep.IsVolumeRoot);

        Assert.Equal(@"C:\Users", root.Join("Users").Display);
        Assert.Equal(@"C:\Users\x\y", deep.Join("y").Display);
        Assert.Equal(@"\\?\C:\Users\x\y", deep.Join("y").Wide);
    }

    [Fact]
    public void Display_roundtrip()
    {
        var p = WidePath.FromDisplay(@"D:\some\dir\");
        Assert.Equal(@"D:\some\dir", p.Display);
        var q = WidePath.FromDisplay(@"\\?\D:\some\dir");
        Assert.Equal(p, q);
        Assert.Equal(p.GetHashCode(), q.GetHashCode());
        Assert.Equal(@"C:\", WidePath.FromDisplay(@"C:\").Display);
        Assert.True(WidePath.FromDisplay(@"C:\").IsVolumeRoot);
        Assert.Equal(@"\\?\C:\", WidePath.FromDisplay(@"C:\").Wide);
    }

    [Fact]
    public void ToString_is_the_display_form()
    {
        Assert.Equal(@"E:\a\b", WidePath.FromDisplay(@"E:\a\b").ToString());
    }

    [Fact]
    public void Join_accepts_spans_and_keeps_exact_names()
    {
        var p = WidePath.FromDisplay(@"C:\x");
        ReadOnlySpan<char> name = "Ünïcødé name.txt".AsSpan();
        Assert.Equal(@"C:\x\Ünïcødé name.txt", p.Join(name).Display);
    }
}
