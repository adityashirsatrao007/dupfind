using DupFind.ViewModels;

namespace DupFind.ViewModels.Tests;

public sealed class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024 * 5, "5 MB")]
    public void Bytes_uses_the_largest_unit_that_fits(long bytes, string expected) =>
        Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Bytes_keeps_the_sign_for_negative_values() =>
        Assert.Equal("-1 KB", Format.Bytes(-1024));

    [Fact]
    public void Bytes_does_not_switch_to_exponents_for_large_volumes() =>
        Assert.Equal("1 TB", Format.Bytes(1024L * 1024 * 1024 * 1024));
}
