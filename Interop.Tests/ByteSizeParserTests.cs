using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ByteSizeParserTests
{
    [Theory]
    [InlineData("0", 0ul)]
    [InlineData("42 B", 42ul)]
    [InlineData("1 KB", 1_000ul)]
    [InlineData("1 KiB", 1_024ul)]
    [InlineData("1.5 MiB", 1_572_864ul)]
    [InlineData("2 gb", 2_000_000_000ul)]
    public void ParsesCommonSizes(string text, ulong expected) =>
        Assert.Equal(expected, ByteSizeParser.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1 MB")]
    [InlineData("12 elephants")]
    [InlineData("999999999999999999999999999 PB")]
    public void RejectsInvalidSizes(string? text) =>
        Assert.False(ByteSizeParser.TryParse(text, out _));

    [Fact]
    public void ParseThrowsForInvalidInput() =>
        Assert.Throws<FormatException>(() => ByteSizeParser.Parse("large"));
}
