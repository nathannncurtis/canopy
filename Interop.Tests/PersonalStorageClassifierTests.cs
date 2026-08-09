using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class PersonalStorageClassifierTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    static readonly PersonalStorageContext Context = new() { AllowedRoots = [@"C:\"] };

    public static TheoryData<PersonalStorageEvidence, PersonalStorageCategory, string> TrueCases => new()
    {
        { E(@"C:\VMs\server.vhdx", size: 200), PersonalStorageCategory.VirtualMachineDisk, "Virtual disk" },
        { E(@"C:\SteamLibrary\steamapps\common\Game", directory: true, size: 300), PersonalStorageCategory.GameLibraryContent, "Steam" },
        { E(@"C:\Users\me\Downloads\old.iso", size: 400, modified: Now.AddDays(-91)), PersonalStorageCategory.OldDownload, "Downloads" },
        { E(@"C:\Users\me\AppData\Local\Microsoft\Windows\INetCache\Content.Outlook\mail.bin", size: 101), PersonalStorageCategory.MessagingAttachmentCache, "Outlook" },
        { E(@"C:\Users\me\AppData\Roaming\Apple Computer\MobileSync\Backup\device", directory: true, size: 500, modified: Now.AddDays(-181)), PersonalStorageCategory.OldDeviceBackup, "Apple MobileSync" },
    };

    [Theory, MemberData(nameof(TrueCases))]
    public void FindsEachArtifactWithReasonSizeAndFamily(PersonalStorageEvidence evidence,
        PersonalStorageCategory category, string family)
    {
        PersonalStorageFinding finding = Assert.Single(PersonalStorageClassifier.Classify(
            [evidence], Context, Now, new PersonalStorageOptions { LargeAttachmentMinimumBytes = 100 },
            TestContext.Current.CancellationToken).Findings);
        Assert.Equal(category, finding.Category); Assert.Equal(family, finding.Family);
        Assert.Equal(evidence.Size, finding.Size); Assert.NotEmpty(finding.Rationale);
        Assert.NotEqual(CleanupRisk.Low, finding.Risk);
    }

    public static TheoryData<PersonalStorageEvidence> NearbyNegatives => new()
    {
        { E(@"C:\Documents\server.vhdx.txt", size: 1000) },
        { E(@"C:\SteamLibrary\steamapps-common\Game", directory: true, size: 1000) },
        { E(@"C:\Users\me\DownloadsBackup\old.iso", size: 1000, modified: Now.AddYears(-1)) },
        { E(@"C:\Users\me\AppData\Local\Microsoft\Windows\INetCache\Content.OutlookNotes\mail.bin", size: 1000) },
        { E(@"C:\Users\me\AppData\Local\Slack\Cache\small.bin", size: 99) },
        { E(@"C:\Users\me\AppData\Roaming\Apple Computer\MobileSync\BackupNotes\device", directory: true, size: 1000, modified: Now.AddYears(-1)) },
    };

    [Theory, MemberData(nameof(NearbyNegatives))]
    public void RejectsNearbyUserDataAndPartialMarkers(PersonalStorageEvidence evidence)
    {
        Assert.Empty(PersonalStorageClassifier.Classify([evidence], Context, Now,
            new PersonalStorageOptions { LargeAttachmentMinimumBytes = 100 },
            TestContext.Current.CancellationToken).Findings);
    }

    [Fact]
    public void OrphanedAppDataRequiresCompleteInventoryAgeAndExactTopLevelDirectory()
    {
        var context = new PersonalStorageContext
        {
            AllowedRoots = [@"C:\Users\me\AppData"], InstalledApplicationInventoryComplete = true,
            InstalledApplicationIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Live App" },
        };
        PersonalStorageEvidence[] items =
        [
            E(@"C:\Users\me\AppData\Local\GoneApp", true, 500, Now.AddDays(-181)),
            E(@"C:\Users\me\AppData\Local\Live App", true, 500, Now.AddYears(-1)),
            E(@"C:\Users\me\AppData\Local\GoneApp\child", true, 200, Now.AddYears(-1)),
            E(@"C:\Users\me\AppData\Local\FreshApp", true, 200, Now.AddDays(-1)),
        ];
        PersonalStorageFinding finding = Assert.Single(PersonalStorageClassifier.Classify(items, context, Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
        Assert.Equal(PersonalStorageCategory.OrphanedApplicationData, finding.Category);
        Assert.EndsWith("GoneApp", finding.Path); Assert.Contains("inventory", finding.Rationale);
        Assert.Equal(CleanupRisk.High, finding.Risk);

        Assert.Empty(PersonalStorageClassifier.Classify([items[0]], context with
        { InstalledApplicationInventoryComplete = false }, Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
    }

    [Fact]
    public void OldCategoriesRequireTimestampAndUseInclusiveThreshold()
    {
        var options = new PersonalStorageOptions
        { OldDownloadAge = TimeSpan.FromDays(7), OldDeviceBackupAge = TimeSpan.FromDays(14) };
        PersonalStoragePreview preview = PersonalStorageClassifier.Classify(
        [
            E(@"C:\Downloads\unknown.zip", modified: null),
            E(@"C:\Downloads\boundary.zip", modified: Now.AddDays(-7)),
            E(@"C:\Android\backup\fresh", true, modified: Now.AddDays(-13)),
            E(@"C:\Android\backup\boundary", true, modified: Now.AddDays(-14)),
        ], Context, Now, options, TestContext.Current.CancellationToken);
        Assert.Equal(2, preview.Findings.Count);
        Assert.Contains(preview.Findings, item => item.Path.EndsWith("boundary.zip"));
        Assert.Contains(preview.Findings, item => item.Path.EndsWith("boundary"));
    }

    [Fact]
    public void CanonicalBoundsRejectTraversalRelativeAmbiguousAndRootPrefixPaths()
    {
        var context = new PersonalStorageContext { AllowedRoots = [@"C:\Allowed"] };
        Assert.Throws<InvalidDataException>(() => PersonalStorageClassifier.Classify(
            [E(@"C:\Allowed\..\Outside\disk.vhdx")], context, Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => PersonalStorageClassifier.Classify(
            [new(null, @"relative\disk.vhdx", false)], context, Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => PersonalStorageClassifier.Classify(
            [new(null, @"C:disk.vhdx", false)], context, Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => PersonalStorageClassifier.Classify(
            [E(@"C:\AllowedElsewhere\disk.vhdx")], context, Now,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PreservesExtendedAndUncRoots()
    {
        PersonalStorageFinding drive = Assert.Single(PersonalStorageClassifier.Classify(
            [new(null, @"\\?\C:\VMs\disk.vhdx", false, 10)], Context, Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
        Assert.Equal(@"C:\VMs\disk.vhdx", drive.Path);
        PersonalStorageFinding unc = Assert.Single(PersonalStorageClassifier.Classify(
            [new(null, @"\\server\share\VMs\disk.vmdk", false, 10)],
            new PersonalStorageContext { AllowedRoots = [@"\\server\share\"] }, Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
        Assert.StartsWith(@"\\server\share", unc.Path);
    }

    [Fact]
    public void NestedFindingsDoNotDoubleCountOverallButRetainCategoryTotals()
    {
        PersonalStoragePreview preview = PersonalStorageClassifier.Classify(
        [
            E(@"C:\SteamLibrary\steamapps\common\Game", true, 100),
            E(@"C:\SteamLibrary\steamapps\common\Game\disk.vhdx", false, 80),
            E(@"C:\VMs\huge.vhdx", false, ulong.MaxValue),
        ], Context, Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, preview.Findings.Count); Assert.Equal(2, preview.Findings.Count(item => item.IsAggregationRoot));
        Assert.Equal(ulong.MaxValue, preview.TotalBytes);
        Assert.Equal(100UL, preview.CategoryBytes[PersonalStorageCategory.GameLibraryContent]);
        Assert.Equal(ulong.MaxValue, preview.CategoryBytes[PersonalStorageCategory.VirtualMachineDisk]);
    }

    [Fact]
    public void ScanPreviewExplainsUnavailableTimestampAndInventoryEvidence()
    {
        var result = new ScanResultManaged
        {
            Nodes = [new ScanNode { Parent = uint.MaxValue, Flags = ScanNodeFlags.Directory, Size = 10 }, new ScanNode { Parent = 0, Size = 10 }],
            Names = [@"C:\", "disk.vhdx"],
        };
        PersonalStoragePreview preview = PersonalStorageClassifier.ClassifyScanResult(result, Now,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(preview.Findings); Assert.Equal(2, preview.Limitations.Count);
        Assert.Contains(preview.Limitations, value => value.Contains("timestamps", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Limitations, value => value.Contains("inventory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoDeletionSurfaceAndCancellationOptionsAreEnforced()
    {
        Assert.DoesNotContain(typeof(PersonalStorageClassifier).GetMethods(), method =>
            method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentOutOfRangeException>(() => PersonalStorageClassifier.Classify([], Context, Now,
            new PersonalStorageOptions { OldDownloadAge = TimeSpan.FromDays(-1) }, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PersonalStorageClassifier.Classify(
            [E(@"C:\VMs\x.vhdx")], Context, Now, cancellationToken: cancellation.Token));
    }

    static PersonalStorageEvidence E(string path, bool directory = false, ulong size = 0,
        DateTimeOffset? modified = null) => new(null, path, directory, size, modified);
}
