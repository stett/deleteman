namespace DeleteMan.ViewModels.Tests;

/// <summary>Port of format.ts: binary units with Explorer's labels, en-US grouping, m:ss durations.</summary>
public class FormatTests
{
    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(512UL, "512 B")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1024UL, "1.0 KB")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(100UL * 1024, "100 KB")]
    [InlineData(158UL * 1024 * 1024, "158 MB")]
    [InlineData(952UL * 1024 * 1024 * 1024, "952 GB")]
    [InlineData(4068192022771UL, "3.7 TB")]
    [InlineData(2UL * 1024 * 1024 * 1024 * 1024 * 1024, "2.0 PB")]
    public void Bytes_pick_the_most_fitting_binary_unit(ulong n, string expected)
    {
        Assert.Equal(expected, Format.Bytes(n));
    }

    [Fact]
    public void Bytes_drop_the_decimal_from_100_upwards()
    {
        Assert.Equal("99.9 KB", Format.Bytes((ulong)(99.9 * 1024)));
        Assert.Equal("100 KB", Format.Bytes(100 * 1024));
        Assert.Equal("1000 KB", Format.Bytes(1000 * 1024));
    }

    [Theory]
    [InlineData(0UL, "0")]
    [InlineData(999UL, "999")]
    [InlineData(1000UL, "1,000")]
    [InlineData(1234567UL, "1,234,567")]
    public void Count_groups_thousands_with_commas(ulong n, string expected)
    {
        Assert.Equal(expected, Format.Count(n));
    }

    [Theory]
    [InlineData(0UL, "file", "0 files")]
    [InlineData(1UL, "file", "1 file")]
    [InlineData(2UL, "folder", "2 folders")]
    [InlineData(1234UL, "file", "1,234 files")]
    [InlineData(1UL, "more folder", "1 more folder")]
    public void Plural_appends_s_except_for_exactly_one(ulong n, string word, string expected)
    {
        Assert.Equal(expected, Format.Plural(n, word));
    }

    [Theory]
    [InlineData(0UL, "0:00")]
    [InlineData(999UL, "0:00")]
    [InlineData(1000UL, "0:01")]
    [InlineData(65_000UL, "1:05")]
    [InlineData(600_000UL, "10:00")]
    [InlineData(3_600_000UL, "1:00:00")]
    [InlineData(3_725_000UL, "1:02:05")]
    [InlineData(36_000_000UL, "10:00:00")]
    public void Duration_is_m_ss_or_h_mm_ss(ulong ms, string expected)
    {
        Assert.Equal(expected, Format.Duration(ms));
    }

    [Theory]
    [InlineData(0.0, "0 %")]
    [InlineData(0.456, "46 %")]
    [InlineData(0.995, "100 %")]
    [InlineData(1.0, "100 %")]
    public void Percent_rounds_to_whole_numbers_with_a_space(double fraction, string expected)
    {
        Assert.Equal(expected, Format.Percent(fraction));
    }
}
