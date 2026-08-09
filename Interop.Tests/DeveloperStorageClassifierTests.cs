using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class DeveloperStorageClassifierTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, bool, DeveloperStorageCategory> Categories => new()
    {
        { @"C:\Downloads\setup.msi", false, DeveloperStorageCategory.OldInstaller },
        { @"C:\Users\me\AppData\Local\npm-cache\content", true, DeveloperStorageCategory.PackageManagerCache },
        { @"C:\src\app\.vs\cache", true, DeveloperStorageCategory.IdeOrCompilerCache },
        { @"C:\src\app\obj", true, DeveloperStorageCategory.StaleBuildOutput },
        { @"C:\src\web\node_modules\react", true, DeveloperStorageCategory.NodeModules },
        { @"C:\src\py\.venv\Lib", true, DeveloperStorageCategory.PythonEnvironmentOrCache },
        { @"C:\Users\me\.nuget\packages\x", true, DeveloperStorageCategory.LanguagePackageCache },
        { @"C:\ProgramData\docker\windowsfilter\layer", true, DeveloperStorageCategory.ContainerStorage },
    };

    [Theory]
    [MemberData(nameof(Categories))]
    public void ClassifiesEachSelectedStorageCategory(string path, bool directory, DeveloperStorageCategory expected)
    {
        DeveloperStoragePreview preview = DeveloperStorageClassifier.Classify(
            [new(path, directory, 42, Now - TimeSpan.FromDays(100))], Now,
            cancellationToken: TestContext.Current.CancellationToken);
        DeveloperStorageFinding finding = Assert.Single(preview.Findings);

        Assert.Equal(expected, finding.Category);
        Assert.Equal(42UL, finding.Size);
        Assert.NotEmpty(finding.Evidence);
        Assert.NotEmpty(finding.Family);
        Assert.Equal(42UL, preview.CategoryBytes[expected]);
        Assert.Equal(42UL, preview.TotalBytes);
    }

    [Fact]
    public void RequiresAgeEvidenceForInstallersAndBuildOutputs()
    {
        StoragePathEvidence[] items =
        [
            new(@"C:\Downloads\setup.msi", false, LastWriteTime: null),
            new(@"C:\Downloads\fresh.msix", false, LastWriteTime: Now - TimeSpan.FromDays(89)),
            new(@"C:\src\app\obj", true, LastWriteTime: Now - TimeSpan.FromDays(29)),
            new(@"C:\src\app\bin", true, LastWriteTime: null),
        ];

        Assert.Empty(DeveloperStorageClassifier.Classify(items, Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
    }

    [Fact]
    public void AgeThresholdsAreInclusiveAndConfigurable()
    {
        var options = new DeveloperStorageOptions
        {
            OldInstallerAge = TimeSpan.FromDays(7),
            StaleBuildOutputAge = TimeSpan.FromDays(2),
        };
        StoragePathEvidence[] items =
        [
            new(@"C:\Downloads\setup.appxbundle", false, LastWriteTime: Now - TimeSpan.FromDays(7)),
            new(@"C:\src\build", true, LastWriteTime: Now - TimeSpan.FromDays(2)),
        ];

        Assert.Equal(2, DeveloperStorageClassifier.Classify(items, Now, options,
            TestContext.Current.CancellationToken).Findings.Count);
    }

    [Theory]
    [InlineData(@"C:\src\node_modules_backup")]
    [InlineData(@"C:\src\myobj")]
    [InlineData(@"C:\src\venv-notes")]
    [InlineData(@"C:\Users\me\.cargo\registry-copy")]
    [InlineData(@"C:\ProgramData\docker-docs\windowsfilter")]
    [InlineData(@"C:\Downloads\setup.exe")]
    public void DoesNotMatchPartialOrUnsupportedNames(string path)
    {
        Assert.Empty(DeveloperStorageClassifier.Classify(
            [new(path, true, LastWriteTime: Now - TimeSpan.FromDays(365))], Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);
    }

    [Fact]
    public void NormalizesExtendedAndForwardSlashPathsWithoutTruncation()
    {
        DeveloperStorageFinding[] findings = DeveloperStorageClassifier.Classify(
        [
            new(@"\\?\C:\src\web\node_modules\pkg", true),
            new("C:/Users/me/.gradle/caches/modules", true),
            new(@"\\?\UNC\server\share\.m2\repository\artifact", true),
        ], Now, cancellationToken: TestContext.Current.CancellationToken).Findings.ToArray();

        Assert.Equal(3, findings.Length);
        Assert.Contains(findings, item => item.Path == @"C:\src\web\node_modules\pkg");
        Assert.Contains(findings, item => item.Path == @"C:\Users\me\.gradle\caches\modules");
        Assert.Contains(findings, item => item.Path == @"\\server\share\.m2\repository\artifact");
    }

    [Fact]
    public void ChoosesTheMostSpecificAndSafestCategoryOrder()
    {
        DeveloperStorageFinding finding = Assert.Single(DeveloperStorageClassifier.Classify(
            [new(@"C:\ProgramData\docker\windowsfilter\node_modules", true)], Now,
            cancellationToken: TestContext.Current.CancellationToken).Findings);

        Assert.Equal(DeveloperStorageCategory.ContainerStorage, finding.Category);
        Assert.Equal(CleanupRisk.High, finding.Risk);
    }

    public static TheoryData<string, string> Ecosystems => new()
    {
        { @"C:\cache\npm-cache\x", "npm-cache" },
        { @"C:\cache\yarn\cache\x", "yarn/cache" },
        { @"C:\cache\pnpm\store\x", "pnpm/store" },
        { @"C:\cache\pip\cache\x", "pip/cache" },
        { @"C:\Users\me\.cargo\registry\x", ".cargo/registry" },
        { @"C:\Users\me\.nuget\packages\x", ".nuget/packages" },
        { @"C:\Users\me\.m2\repository\x", ".m2/repository" },
        { @"C:\Users\me\.gradle\caches\x", ".gradle/caches" },
        { @"C:\Users\me\go\pkg\mod\x", "go/pkg/mod" },
    };

    [Theory]
    [MemberData(nameof(Ecosystems))]
    public void IdentifiesSpecificPackageEcosystem(string path, string family)
    {
        DeveloperStorageFinding finding = Assert.Single(
            DeveloperStorageClassifier.Classify([new(path, true)], Now,
                cancellationToken: TestContext.Current.CancellationToken).Findings);

        Assert.Equal(family, finding.Family);
    }

    [Fact]
    public void NestedMatchesRemainVisibleButAreNotDoubleCounted()
    {
        DeveloperStoragePreview preview = DeveloperStorageClassifier.Classify(
        [
            new(@"C:\src\node_modules", true, 100),
            new(@"C:\src\node_modules\pkg", true, 60),
            new(@"C:\other\node_modules", true, ulong.MaxValue),
        ], Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, preview.Findings.Count);
        Assert.Equal(2, preview.Findings.Count(item => item.IsAggregationRoot));
        Assert.Equal(ulong.MaxValue, preview.TotalBytes);
        Assert.Equal(ulong.MaxValue, preview.CategoryBytes[DeveloperStorageCategory.NodeModules]);
    }

    [Fact]
    public void RejectsInvalidInputAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => DeveloperStorageClassifier.Classify([new(" ", true)], Now,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeveloperStorageClassifier.Classify([], Now,
            new DeveloperStorageOptions { OldInstallerAge = TimeSpan.FromDays(-1) },
            TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DeveloperStorageClassifier.Classify(
            [new(@"C:\src\node_modules", true)], Now, cancellationToken: cancellation.Token));
    }
}
