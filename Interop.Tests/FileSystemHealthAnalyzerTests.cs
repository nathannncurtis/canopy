using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class FileSystemHealthAnalyzerTests
{
    static readonly string Root = Path.GetFullPath(@"C:\health");
    static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SymbolicLinkOutcome.Valid, false)]
    [InlineData(SymbolicLinkOutcome.Dangling, true)]
    [InlineData(SymbolicLinkOutcome.Inaccessible, true)]
    [InlineData(SymbolicLinkOutcome.Cyclic, true)]
    public async Task ModelsEverySymbolicLinkOutcome(SymbolicLinkOutcome outcome, bool expectedFinding)
    {
        FileSystemHealthAnalysis result = await Analyzer([Item("link", attributes: FileAttributes.ReparsePoint,
            linkTarget: @"C:\target", linkOutcome: outcome)]).AnalyzeAsync([Root],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expectedFinding, result.Findings.Any(item => item.Kind == FileSystemHealthKind.BrokenSymbolicLink));
        if (expectedFinding) Assert.Equal(outcome, Assert.Single(result.Findings).LinkOutcome);
    }

    [Theory]
    [InlineData(ShortcutOutcome.Resolvable, false)]
    [InlineData(ShortcutOutcome.Missing, true)]
    [InlineData(ShortcutOutcome.NetworkUnavailable, false)]
    [InlineData(ShortcutOutcome.Unsupported, false)]
    [InlineData(ShortcutOutcome.Malformed, false)]
    public async Task ClassifiesShortcutOutcomesWithoutFalseOrphans(ShortcutOutcome outcome, bool orphaned)
    {
        FileSystemHealthAnalysis result = await Analyzer([Item("sample.lnk", shortcutTarget: @"C:\target", shortcutOutcome: outcome)])
            .AnalyzeAsync([Root], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(outcome, Assert.Single(result.Snapshot.Items).Value.ShortcutOutcome);
        Assert.Equal(orphaned, result.Findings.Any(item => item.Kind == FileSystemHealthKind.OrphanedShortcut));
    }

    [Fact]
    public async Task ReportsOnlyDeepestDirectoryPerTree()
    {
        FileSystemObservation[] items =
        [
            Item(Path.Combine("a"), directory: true),
            Item(Path.Combine("a", "b"), directory: true),
            Item(Path.Combine("a", "b", "c"), directory: true),
            Item(Path.Combine("a", "b", "c", "file")),
        ];
        FileSystemHealthAnalysis result = await Analyzer(items).AnalyzeAsync([Root], options:
            new FileSystemHealthOptions { DeepHierarchyThreshold = 1, PathLengthThreshold = 10_000 },
            cancellationToken: TestContext.Current.CancellationToken);
        FileSystemHealthFinding finding = Assert.Single(result.Findings,
            item => item.Kind == FileSystemHealthKind.DeepHierarchy);
        Assert.EndsWith(Path.Combine("a", "b", "c"), finding.Path);
        Assert.Contains("depth 3", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LongPathUsesStrictBoundaryAndNormalizesExtendedPrefix()
    {
        string normal = Path.Combine(Root, "boundary.txt");
        string extended = @"\\?\" + normal;
        int boundary = normal.Length;
        FileSystemHealthAnalysis exact = await Analyzer([AbsoluteItem(normal)]).AnalyzeAsync([Root], options:
            new FileSystemHealthOptions { PathLengthThreshold = boundary }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(exact.Findings, item => item.Kind == FileSystemHealthKind.LongPath);
        FileSystemHealthAnalysis over = await Analyzer([AbsoluteItem(extended)]).AnalyzeAsync([Root], options:
            new FileSystemHealthOptions { PathLengthThreshold = boundary - 1 }, cancellationToken: TestContext.Current.CancellationToken);
        FileSystemHealthFinding finding = Assert.Single(over.Findings, item => item.Kind == FileSystemHealthKind.LongPath);
        Assert.Equal(normal, finding.Path);
    }

    [Theory]
    [InlineData("trailing ", WindowsNameProblem.TrailingSpace)]
    [InlineData("trailing.", WindowsNameProblem.TrailingPeriod)]
    [InlineData("control\u0001", WindowsNameProblem.ControlCharacter)]
    [InlineData("bad?.txt", WindowsNameProblem.InvalidCharacter)]
    [InlineData("COM1.log", WindowsNameProblem.ReservedDeviceName)]
    public async Task ReportsReasonSpecificTroublesomeNames(string name, WindowsNameProblem reason)
    {
        FileSystemHealthAnalysis result = await Analyzer([Item(name)]).AnalyzeAsync([Root],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(reason, Assert.Single(result.Findings,
            item => item.Kind == FileSystemHealthKind.TroublesomeWindowsName).NameProblem);
    }

    [Fact]
    public async Task OldFileSupportsSelectedTimestampAndStrictTimezoneNormalizedBeforeDate()
    {
        DateTimeOffset boundary = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FileSystemObservation olderCreation = Item("old.bin", creation: boundary.AddMinutes(-1), access: Now);
        FileSystemObservation exactDifferentOffset = Item("exact.bin",
            creation: boundary.ToOffset(TimeSpan.FromHours(-8)), access: Now);
        var options = new FileSystemHealthOptions
        {
            OldFileTimestamp = FileAgeTimestamp.Creation, OldFileBeforeUtc = boundary,
            PathLengthThreshold = 10_000, DeepHierarchyThreshold = 100,
        };
        FileSystemHealthAnalysis result = await Analyzer([olderCreation, exactDifferentOffset])
            .AnalyzeAsync([Root], options: options, cancellationToken: TestContext.Current.CancellationToken);
        FileSystemHealthFinding finding = Assert.Single(result.Findings,
            item => item.Kind == FileSystemHealthKind.VeryOldFile);
        Assert.EndsWith("old.bin", finding.Path);
    }

    [Fact]
    public async Task RapidGrowthUsesDeltaOrRateAndRanksLargestDeltaFirstWithoutRenameFalsePositive()
    {
        string large = Full("large.db"), fast = Full("fast.db"), renamed = Full("renamed.db");
        var baseline = Snapshot(Now.AddHours(-1),
            ItemAbsolute(large, size: 100), ItemAbsolute(fast, size: 100), ItemAbsolute(Full("old-name.db"), size: 1));
        var options = new FileSystemHealthOptions
        {
            RapidGrowthMinimumBytes = 500, RapidGrowthMinimumBytesPerHour = 50,
            PathLengthThreshold = 10_000, DeepHierarchyThreshold = 100,
        };
        FileSystemHealthAnalysis result = await Analyzer([
            ItemAbsolute(large, size: 700), ItemAbsolute(fast, size: 180), ItemAbsolute(renamed, size: 10_000)])
            .AnalyzeAsync([Root], baseline, options, TestContext.Current.CancellationToken);
        FileSystemHealthFinding[] growth = result.Findings.Where(item => item.Kind == FileSystemHealthKind.RapidGrowth).ToArray();
        Assert.Equal([large, fast], growth.Select(item => item.Path));
        Assert.Equal([(ulong)600, (ulong)80], growth.Select(item => item.GrowthBytes));
        Assert.DoesNotContain(growth, item => item.Path == renamed);
    }

    [Fact]
    public async Task RejectsOutOfRootDuplicatesInvalidOptionsAndHonorsCancellation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Analyzer([AbsoluteItem(@"D:\outside")])
            .AnalyzeAsync([Root], cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => Analyzer([Item("same"), Item("SAME")])
            .AnalyzeAsync([Root], cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Analyzer([]).AnalyzeAsync([Root],
            options: new FileSystemHealthOptions { RapidGrowthMinimumBytesPerHour = double.NaN },
            cancellationToken: TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Analyzer([])
            .AnalyzeAsync([Root], cancellationToken: cancellation.Token));
    }

    static FileSystemHealthSnapshot Snapshot(DateTimeOffset captured, params FileSystemObservation[] items) =>
        new(captured, items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase));
    static string Full(string relative) => Path.Combine(Root, relative);
    static FileSystemObservation Item(string relative, bool directory = false, ulong size = 0,
        DateTimeOffset? modified = null, FileAttributes attributes = FileAttributes.Normal,
        string? linkTarget = null, SymbolicLinkOutcome linkOutcome = SymbolicLinkOutcome.None,
        string? shortcutTarget = null, ShortcutOutcome shortcutOutcome = ShortcutOutcome.None,
        DateTimeOffset? creation = null, DateTimeOffset? access = null) =>
        ItemAbsolute(Full(relative), directory, size, modified, attributes, linkTarget, linkOutcome,
            shortcutTarget, shortcutOutcome, creation, access);
    static FileSystemObservation AbsoluteItem(string path) => ItemAbsolute(path);
    static FileSystemObservation ItemAbsolute(string path, bool directory = false, ulong size = 0,
        DateTimeOffset? modified = null, FileAttributes attributes = FileAttributes.Normal,
        string? linkTarget = null, SymbolicLinkOutcome linkOutcome = SymbolicLinkOutcome.None,
        string? shortcutTarget = null, ShortcutOutcome shortcutOutcome = ShortcutOutcome.None,
        DateTimeOffset? creation = null, DateTimeOffset? access = null) =>
        new(path, directory, size, modified ?? Now, attributes, linkTarget, linkOutcome,
            shortcutTarget, shortcutOutcome, creation, access);
    static FileSystemHealthAnalyzer Analyzer(IReadOnlyList<FileSystemObservation> items) =>
        new(new FakeSource(items), new FakeTimeProvider(Now));
    sealed class FakeSource(IReadOnlyList<FileSystemObservation> items) : IFileSystemHealthSource
    {
        public Task<IReadOnlyList<FileSystemObservation>> ObserveAsync(IReadOnlyList<string> roots, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(items); }
    }
    sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}
