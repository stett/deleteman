namespace DeleteMan.Engine.Tests;

public class DrivesTests
{
    [Fact]
    public void Enumerate_includes_the_system_drive_with_sane_numbers()
    {
        var drives = Drives.Enumerate();
        Assert.NotEmpty(drives);
        var system = Path.GetPathRoot(Environment.SystemDirectory)![0].ToString().ToUpperInvariant();
        var c = drives.Single(d => d.Letter == system);
        Assert.Equal(DriveKind.Fixed, c.Kind);
        Assert.False(string.IsNullOrEmpty(c.Fs));
        Assert.True(c.Total > 0);
        Assert.True(c.Free <= c.Total);
        Assert.All(drives, d => Assert.Matches("^[A-Z]$", d.Letter));
        Assert.All(drives, d => Assert.True(d.Total > 0));
    }

    [Fact]
    public void Drive_cache_serves_a_snapshot_and_refreshes_on_invalidate()
    {
        using var cache = DriveCache.Start();
        var first = cache.Get();
        Assert.NotEmpty(first);
        cache.Invalidate();
        Thread.Sleep(400);
        var second = cache.Get();
        Assert.Equal(first.Select(d => d.Letter), second.Select(d => d.Letter));
    }
}
