using System.Text;
using System.Text.Json;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanResultExporterTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public async Task CsvReconstructsPathsAndEscapesSpecialCharacters()
    {
        await using var output = new MemoryStream();

        await ScanResultExporter.ExportCsvAsync(Result(), output, TestContext.Current.CancellationToken);

        string csv = Encoding.UTF8.GetString(output.ToArray());
        string separator = Path.DirectorySeparatorChar.ToString();
        Assert.Equal(
            "index,path,name,size,flags\n" +
            "0,root,root,30,1\n" +
            $"1,\"root{separator}folder, \"\"α\"\"\",\"folder, \"\"α\"\"\",20,1\n" +
            $"2,\"root{separator}folder, \"\"α\"\"{separator}résumé.txt\",résumé.txt,20,0\n",
            csv);
    }

    [Fact]
    public async Task JsonIsDeterministicAndContainsFullPaths()
    {
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();

        await ScanResultExporter.ExportJsonAsync(Result(), first, TestContext.Current.CancellationToken);
        await ScanResultExporter.ExportJsonAsync(Result(), second, TestContext.Current.CancellationToken);

        Assert.Equal(first.ToArray(), second.ToArray());
        using JsonDocument document = JsonDocument.Parse(first.ToArray());
        JsonElement root = document.RootElement;
        Assert.Equal(30UL, root.GetProperty("totalBytes").GetUInt64());
        JsonElement[] nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(3, nodes.Length);
        Assert.Equal(
            $"root{Path.DirectorySeparatorChar}folder, \"α\"{Path.DirectorySeparatorChar}résumé.txt",
            nodes[2].GetProperty("path").GetString());
        Assert.Equal("résumé.txt", nodes[2].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsMalformedParentTopology(bool cycle)
    {
        ScanResultManaged result = Result();
        ScanNode node = result.Nodes[1];
        node.Parent = cycle ? 2u : 99u;
        result.Nodes[1] = node;
        if (cycle)
        {
            node = result.Nodes[2];
            node.Parent = 1;
            result.Nodes[2] = node;
        }

        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ScanResultExporter.ExportJsonAsync(result, output, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HonorsPreCanceledTokenWithoutWriting(bool json)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var output = new MemoryStream();

        Task export = json
            ? ScanResultExporter.ExportJsonAsync(Result(), output, cancellation.Token)
            : ScanResultExporter.ExportCsvAsync(Result(), output, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Equal(0, output.Length);
    }

    static ScanResultManaged Result() => new()
    {
        Nodes =
        [
            Node(30, None, ScanNodeFlags.Directory),
            Node(20, 0, ScanNodeFlags.Directory),
            Node(20, 1, 0),
        ],
        Names = ["root", "folder, \"α\"", "résumé.txt"],
        TotalBytes = 30,
        FileCount = 1,
        DirCount = 2,
        ElapsedSec = 1.25,
    };

    static ScanNode Node(ulong size, uint parent, uint flags) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = None,
        NextSibling = None,
        Flags = flags,
    };
}
