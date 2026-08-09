using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ConsumerCleanupClassifierTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    const string Root = @"C:\";

    public static TheoryData<string, bool, ConsumerCleanupCategory, CleanupRisk, CleanupDisposition> TrueCases => new()
    {
        { @"C:\Users\me\AppData\Local\Temp\old.tmp", false, ConsumerCleanupCategory.TemporaryFile, CleanupRisk.Low, CleanupDisposition.SafeToClean },
        { @"C:\ProgramData\Vendor\Logs\old.log", false, ConsumerCleanupCategory.OldLogFile, CleanupRisk.Medium, CleanupDisposition.ReviewRequired },
        { @"C:\Users\me\AppData\Local\CrashDumps\app.dmp", false, ConsumerCleanupCategory.CrashDump, CleanupRisk.Low, CleanupDisposition.SafeToClean },
        { @"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache\entry", false, ConsumerCleanupCategory.BrowserOrThumbnailCache, CleanupRisk.Low, CleanupDisposition.SafeToClean },
        { @"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db", false, ConsumerCleanupCategory.BrowserOrThumbnailCache, CleanupRisk.Low, CleanupDisposition.SafeToClean },
        { @"C:\Windows\SoftwareDistribution\Download\payload.cab", false, ConsumerCleanupCategory.WindowsUpdateDownload, CleanupRisk.High, CleanupDisposition.UseSystemTool },
        { @"C:\$Recycle.Bin\S-1-5-21\$R123.txt", false, ConsumerCleanupCategory.RecycleBinContent, CleanupRisk.Medium, CleanupDisposition.UseSystemTool },
    };

    [Theory, MemberData(nameof(TrueCases))]
    public void ClassifiesTrueCasesWithRationaleRiskAndDisposition(string path, bool directory,
        ConsumerCleanupCategory category, CleanupRisk risk, CleanupDisposition disposition)
    {
        ConsumerCleanupPreview preview = ConsumerCleanupClassifier.Classify(
            [new(7, path, directory, 42, Now.AddDays(-60))], [Root], Now,
            cancellationToken: TestContext.Current.CancellationToken);
        ConsumerCleanupFinding finding = Assert.Single(preview.Findings);
        Assert.Equal(category, finding.Category); Assert.Equal(risk, finding.Risk);
        Assert.Equal(disposition, finding.Disposition); Assert.NotEmpty(finding.Rationale);
        Assert.Equal((uint)7, finding.NodeIndex); Assert.Equal(42UL, preview.TotalBytes);
        Assert.Equal(42UL, preview.CategoryBytes[category]);
    }

    public static TheoryData<string, bool> NearbyNegatives => new()
    {
        { @"C:\Users\me\AppData\Local\Templates\old.tmp", false },
        { @"C:\ProgramData\Vendor\Catalogs\old.log", false },
        { @"C:\Users\me\Documents\app.dmp", false },
        { @"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\CacheNotes\entry", false },
        { @"C:\Users\me\Pictures\thumbcache_256.db", false },
        { @"C:\Windows\SoftwareDistribution\DataStore\payload.cab", false },
        { @"C:\$Recycle.Bin-archive\S-1-5-21\file", false },
    };

    [Theory, MemberData(nameof(NearbyNegatives))]
    public void RejectsNearbyNamesAndLocations(string path, bool directory)
    {
        Assert.Empty(ConsumerCleanupClassifier.Classify(
            [new(null, path, directory, 10, Now.AddYears(-1))], [Root], Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
    }

    [Fact]
    public void RequiresAgeEvidenceForGenericTemporaryAndLogsButTmpExtensionIsExplicit()
    {
        ConsumerCleanupPreview preview = ConsumerCleanupClassifier.Classify(
        [
            new(null, @"C:\Temp\fresh.bin", false, LastWriteTime: Now),
            new(null, @"C:\Logs\unknown.log", false, LastWriteTime: null),
            new(null, @"C:\Temp\explicit.tmp", false, LastWriteTime: null),
        ], [Root], Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(preview.Findings);
        Assert.EndsWith("explicit.tmp", preview.Findings[0].Path);
    }

    [Fact]
    public void BoundedRootsRejectOutOfScopeEvidenceAfterExtendedPathNormalization()
    {
        Assert.Throws<InvalidDataException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"D:\Temp\x.tmp", false)], [@"C:\Users"], Now,
            cancellationToken: TestContext.Current.CancellationToken));
        ConsumerCleanupFinding finding = Assert.Single(ConsumerCleanupClassifier.Classify(
            [new(null, @"\\?\C:\Users\me\AppData\Local\Temp\x.tmp", false)], [@"C:\Users"], Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
        Assert.StartsWith(@"C:\Users", finding.Path);
    }

    [Fact]
    public void CanonicalBoundsRejectTraversalRelativeDriveAmbiguityAndRootPrefixCollisions()
    {
        Assert.Throws<InvalidDataException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"C:\Allowed\..\Outside\Temp\x.tmp", false)], [@"C:\Allowed"], Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"Temp\x.tmp", false)], [@"C:\Allowed"], Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"C:Temp\x.tmp", false)], [@"C:\Allowed"], Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"C:\AllowedSibling\Temp\x.tmp", false)], [@"C:\Allowed"], Now,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PreservesDriveAndUncRootsWithExactBoundaryChecks()
    {
        Assert.Single(ConsumerCleanupClassifier.Classify(
            [new(null, @"C:\Temp\x.tmp", false)], [@"C:\"], Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
        Assert.Single(ConsumerCleanupClassifier.Classify(
            [new(null, @"\\server\share\Temp\x.tmp", false)], [@"\\server\share\"], Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
        Assert.Throws<InvalidDataException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"\\server\share-other\Temp\x.tmp", false)], [@"\\server\share\"], Now,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NestedFindingsRemainVisibleButTotalsDoNotDoubleCountAndSaturate()
    {
        ConsumerCleanupPreview preview = ConsumerCleanupClassifier.Classify(
        [
            new(null, @"C:\$Recycle.Bin", true, 100),
            new(null, @"C:\$Recycle.Bin\sid\child", false, 80),
            new(null, @"C:\Windows\SoftwareDistribution\Download", true, ulong.MaxValue),
        ], [Root], Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, preview.Findings.Count); Assert.Equal(2, preview.Findings.Count(item => item.IsAggregationRoot));
        Assert.Equal(ulong.MaxValue, preview.TotalBytes);
        Assert.Equal(100UL, preview.CategoryBytes[ConsumerCleanupCategory.RecycleBinContent]);
    }

    [Fact]
    public void CategoryTotalsKeepNestedDifferentCategoriesWhileOverallTotalDeduplicates()
    {
        ConsumerCleanupPreview preview = ConsumerCleanupClassifier.Classify(
        [
            new(null, @"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache", true, 100),
            new(null, @"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache\CrashDumps\app.dmp", false, 80),
        ], [Root], Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(100UL, preview.TotalBytes);
        Assert.Equal(100UL, preview.CategoryBytes[ConsumerCleanupCategory.BrowserOrThumbnailCache]);
        Assert.Equal(80UL, preview.CategoryBytes[ConsumerCleanupCategory.CrashDump]);
    }

    [Fact]
    public void ScanResultPreviewDisclosesTimestampUnavailableExclusions()
    {
        var result = new ScanResultManaged
        {
            Nodes =
            [
                new ScanNode { Parent = uint.MaxValue, Flags = ScanNodeFlags.Directory, Size = 5 },
                new ScanNode { Parent = 0, Flags = ScanNodeFlags.Directory, Size = 5 },
                new ScanNode { Parent = 1, Size = 5 },
            ],
            Names = [@"C:\", "Logs", "old.log"],
        };
        ConsumerCleanupPreview preview = ConsumerCleanupClassifier.ClassifyScanResult(result, Now,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(preview.Findings);
        Assert.Contains(preview.Limitations, limitation => limitation.Contains("timestamps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SafeToCleanCountsAreExplicitAndNoMutationSurfaceExists()
    {
        ConsumerCleanupPreview preview = ConsumerCleanupClassifier.Classify(
        [
            new(null, @"C:\Temp\a.tmp", false),
            new(null, @"C:\Logs\old.log", false, LastWriteTime: Now.AddDays(-31)),
            new(null, @"C:\Windows\SoftwareDistribution\Download\x", false),
        ], [Root], Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, preview.SafeToCleanCount); Assert.Equal(1, preview.ReviewRequiredCount);
        Assert.Equal(1, preview.UseSystemToolCount);
        Assert.DoesNotContain(typeof(ConsumerCleanupClassifier).GetMethods(), method =>
            method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatesOptionsAndHonorsCancellation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsumerCleanupClassifier.Classify([], [Root], Now,
            new ConsumerCleanupOptions { OldLogAge = TimeSpan.FromDays(-1) }, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConsumerCleanupClassifier.Classify(
            [new(null, @"C:\Temp\x.tmp", false)], [Root], Now, cancellationToken: cancellation.Token));
    }
}
