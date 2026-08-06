using SizeMonitor.Interop;
using System.Globalization;
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
    [InlineData("1,2,3 MB")]
    public void RejectsInvalidSizes(string? text) =>
        Assert.False(ByteSizeParser.TryParse(text, out _));

    [Fact]
    public void ParseThrowsForInvalidInput() =>
        Assert.Throws<FormatException>(() => ByteSizeParser.Parse("large"));

    [Fact]
    public void ParsesGroupingAccordingToEnglishCulture()
    {
        using var culture = new CultureScope("en-US");

        Assert.Equal(1_000ul, ByteSizeParser.Parse("1,000 B"));
    }

    [Fact]
    public void GermanGroupingIsNotReinterpretedAsInvariantDecimal()
    {
        using var culture = new CultureScope("de-DE");

        Assert.Equal(1_000_000_000ul, ByteSizeParser.Parse("1.000 MB"));
        Assert.False(ByteSizeParser.TryParse("1.5 MiB", out _));
        Assert.Equal(1_572_864ul, ByteSizeParser.Parse("1,5 MiB"));
    }

    sealed class CultureScope : IDisposable
    {
        readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
