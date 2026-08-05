using System.Text;
using System.Xml.Linq;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanReportExporterTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public async Task XmlIsDeterministicEscapedAndLeavesStreamOpen()
    {
        ScanResultManaged result = Example();
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await ScanReportExporter.ExportXmlAsync(result, first, TestContext.Current.CancellationToken);
        await ScanReportExporter.ExportXmlAsync(result, second, TestContext.Current.CancellationToken);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.True(first.CanWrite);
        XDocument document = XDocument.Parse(Encoding.UTF8.GetString(first.ToArray()));
        XElement[] items = document.Descendants("item").ToArray();
        Assert.Equal(["0", "1", "2"], items.Select(item => item.Attribute("index")!.Value));
        Assert.Equal("資料 & <root>", items[0].Element("name")!.Value);
        Assert.Equal(Path.Combine("資料 & <root>", "a\"b.txt"), items[1].Element("path")!.Value);
    }

    [Fact]
    public async Task HtmlIsSelfContainedEncodedAccessibleAndOrdered()
    {
        using var destination = new MemoryStream();

        await ScanReportExporter.ExportHtmlAsync(
            Example(), destination, TestContext.Current.CancellationToken);

        Assert.True(destination.CanWrite);
        string html = Encoding.UTF8.GetString(destination.ToArray());
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<caption>Scanned files and directories</caption>", html);
        Assert.Contains("scope=\"col\"", html);
        Assert.Contains("資料 &amp; &lt;root&gt;", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(html.IndexOf(">0</td>", StringComparison.Ordinal) <
                    html.IndexOf(">1</td>", StringComparison.Ordinal));
        Assert.Contains("<dt>Total bytes</dt><dd>7</dd>", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsMalformedTopology(bool xml)
    {
        ScanResultManaged malformed = Result([File(0, 5)], ["bad"]);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => xml
            ? ScanReportExporter.ExportXmlAsync(malformed, destination, TestContext.Current.CancellationToken)
            : ScanReportExporter.ExportHtmlAsync(malformed, destination, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HonorsCancellation(bool xml)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => xml
            ? ScanReportExporter.ExportXmlAsync(Example(), destination, cancellation.Token)
            : ScanReportExporter.ExportHtmlAsync(Example(), destination, cancellation.Token));
    }

    static ScanResultManaged Example() => Result(
        [Directory(7, None), File(0, 0), File(7, 0)],
        ["資料 & <root>", "a\"b.txt", "é.txt"]);

    static ScanResultManaged Result(ScanNode[] nodes, string[] names) => new()
    {
        Nodes = nodes,
        Names = names,
        TotalBytes = 7,
        FileCount = 2,
        DirCount = 1,
        ElapsedSec = 1.25,
    };

    static ScanNode Directory(ulong size, uint parent) => new()
    {
        Size = size,
        Parent = parent,
        Flags = ScanNodeFlags.Directory,
        FirstChild = None,
        NextSibling = None,
    };

    static ScanNode File(ulong size, uint parent) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = None,
        NextSibling = None,
    };
}
