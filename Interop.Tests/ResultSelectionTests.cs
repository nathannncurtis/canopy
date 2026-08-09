using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ResultSelectionTests
{
    [Fact]
    public void ReplaceToggleAndRangeFollowDisplayedOrder()
    {
        var selection = new ResultSelectionModel();
        selection.Replace(7); selection.Toggle(9);
        Assert.Equal([7u, 9u], selection.SelectedIndices.Order());
        selection.SelectRange(3, [9, 7, 5, 3, 1]);
        Assert.Equal([3u, 5u, 7u, 9u], selection.SelectedIndices.Order());
        selection.Toggle(5); Assert.DoesNotContain(5u, selection.SelectedIndices);
    }

    [Fact]
    public void SummaryBuildsPathsAndSaturatesTotal()
    {
        ScanResultManaged result = Result(ulong.MaxValue, 10);
        var selection = new ResultSelectionModel(); selection.Replace(1); selection.Toggle(2);
        ResultSelectionSummary summary = selection.Summarize(result);
        Assert.Equal(ulong.MaxValue, summary.TotalBytes); Assert.Equal(1, summary.FileCount); Assert.Equal(1, summary.DirectoryCount);
        Assert.EndsWith(Path.Combine("folder", "file.txt"), summary.Items.Single(item => !item.IsDirectory).Path);
    }

    [Fact]
    public void TextAndCsvExportsEverySelectedRowWithEscaping()
    {
        var selection = new ResultSelectionSummary([
            new(1, "C:\\a,b\\quoted\".txt", 42, false), new(2, "C:\\folder", 10, true)], 52, 1, 1);
        string text = ResultSelectionFormatter.ToText(selection), csv = ResultSelectionFormatter.ToCsv(selection);
        Assert.Contains("\t42\tFile", text); Assert.Contains("\"C:\\a,b\\quoted\"\".txt\",42,File", csv);
        Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void InvalidRangeAnchorFallsBackToSingleSelection()
    {
        var selection = new ResultSelectionModel(); selection.Replace(99); selection.SelectRange(2, [1, 2, 3]);
        Assert.Equal([2u], selection.SelectedIndices);
    }

    [Fact]
    public async Task BatchActionsContinueAfterUnavailableItems()
    {
        var selection = new ResultSelectionSummary([
            new(1, "C:\\ready.txt", 1, false),
            new(2, "C:\\missing & literal.txt", 2, false),
            new(3, "C:\\also-ready", 3, true)], 6, 2, 1);
        var visited = new List<string>();

        ResultSelectionActionOutcome outcome = await ResultSelectionActions.ExecuteAsync(selection, (item, _) =>
        {
            visited.Add(item.Path);
            if (item.NodeIndex == 2) throw new FileNotFoundException("Item is stale.");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.Requested);
        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(3, visited.Count);
        Assert.Equal("C:\\missing & literal.txt", Assert.Single(outcome.Failures).Path);
    }

    [Fact]
    public async Task BatchActionsDelegateFilesAndDirectoriesAndPropagateProgrammingErrors()
    {
        var selection = new ResultSelectionSummary([
            new(1, "C:\\資料\\résumé.txt", 1, false),
            new(2, "C:\\資料\\folder", 2, true)], 3, 1, 1);
        var delegated = new List<(string Path, bool Directory)>();

        ResultSelectionActionOutcome outcome = await ResultSelectionActions.ExecuteAsync(selection, (item, _) =>
        {
            delegated.Add((item.Path, item.IsDirectory));
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Succeeded);
        Assert.Contains(("C:\\資料\\résumé.txt", false), delegated);
        Assert.Contains(("C:\\資料\\folder", true), delegated);
        await Assert.ThrowsAsync<NullReferenceException>(() => ResultSelectionActions.ExecuteAsync(
            selection, (_, _) => throw new NullReferenceException("programming defect"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UnicodeLongPathsSurviveSummaryTextCsvAndExplorerPlanLiterally()
    {
        string leaf = new('界', 270);
        ScanResultManaged result = new()
        {
            Nodes = [
                new ScanNode { Parent = uint.MaxValue, FirstChild = 1, NextSibling = uint.MaxValue, Flags = ScanNodeFlags.Directory },
                new ScanNode { Size = 7, Parent = 0, FirstChild = uint.MaxValue, NextSibling = uint.MaxValue },
            ],
            Names = ["C:\\資料", leaf + ",\"line\r\nbreak.txt"], TotalBytes = 7,
        };
        var model = new ResultSelectionModel(); model.Replace(1);

        ResultSelectionSummary summary = model.Summarize(result);
        string path = Assert.Single(summary.Items).Path;
        Assert.True(path.Length > 260);
        Assert.Contains("資料", ResultSelectionFormatter.ToText(summary));
        string csv = ResultSelectionFormatter.ToCsv(summary);
        string escapedPath = path.Replace("\"", "\"\"");
        Assert.Contains($"\"{escapedPath}\",7,File\r\n", csv);

        ShellLaunchPlan plan = ShellLaunchPlans.SelectInExplorer(path);
        Assert.Equal("explorer.exe", plan.FileName);
        Assert.Equal(["/select,", path], plan.Arguments);
    }

    [Fact]
    public void DroppedPathValidationCanonicalizesMixedUnicodeLongTargetsAndReportsInvalidOnes()
    {
        string longFile = Path.Combine("C:\\資料", new string('é', 270) + ".txt");
        string folder = "C:\\資料\\folder";
        string missing = "C:\\missing";
        var existing = new HashSet<string>([Path.GetFullPath(longFile), Path.GetFullPath(folder)],
            StringComparer.OrdinalIgnoreCase);

        PathTransferPlan plan = PathTransfers.Validate(
            [longFile, folder, longFile.ToUpperInvariant(), missing, "\0invalid"], existing.Contains);

        Assert.Equal(2, plan.Accepted.Count);
        Assert.Contains(Path.GetFullPath(longFile), plan.Accepted);
        Assert.Contains(Path.GetFullPath(folder), plan.Accepted);
        Assert.Equal([missing, "\0invalid"], plan.Rejected);
    }

    [Fact]
    public void DroppedScanDecisionStartsWhenIdleStagesWhenBusyAndRejectsEmptyPlans()
    {
        var mixed = new PathTransferPlan(["C:\\valid", "C:\\also-valid"], ["C:\\missing"]);
        Assert.Equal(DroppedScanAction.StartNow, PathTransfers.DecideScanAction(mixed, scanInProgress: false));
        Assert.Equal(DroppedScanAction.StageUntilIdle, PathTransfers.DecideScanAction(mixed, scanInProgress: true));

        var invalidOnly = new PathTransferPlan([], ["C:\\missing"]);
        Assert.Equal(DroppedScanAction.NoValidTargets,
            PathTransfers.DecideScanAction(invalidOnly, scanInProgress: false));
        Assert.Equal(DroppedScanAction.NoValidTargets,
            PathTransfers.DecideScanAction(invalidOnly, scanInProgress: true));
    }

    static ScanResultManaged Result(ulong folderBytes, ulong fileBytes) => new()
    {
        Nodes = [
            new ScanNode { Size = ulong.MaxValue, Parent = uint.MaxValue, FirstChild = 1, NextSibling = uint.MaxValue, Flags = ScanNodeFlags.Directory },
            new ScanNode { Size = folderBytes, Parent = 0, FirstChild = 2, NextSibling = uint.MaxValue, Flags = ScanNodeFlags.Directory },
            new ScanNode { Size = fileBytes, Parent = 1, FirstChild = uint.MaxValue, NextSibling = uint.MaxValue },
        ], Names = ["C:\\", "folder", "file.txt"], TotalBytes = ulong.MaxValue,
    };
}
