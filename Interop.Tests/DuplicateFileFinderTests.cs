using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class DuplicateFileFinderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "canopy-duplicates-" + Guid.NewGuid().ToString("N"));

    public DuplicateFileFinderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task FindsExactDuplicatesButRejectsSameSizeDifferentContent()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        byte[] duplicate = Encoding.UTF8.GetBytes("same unicode payload 世界");
        await File.WriteAllBytesAsync(Path.Combine(_root, "one.bin"), duplicate, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(nested, "two.bin"), duplicate, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "different.bin"),
            Enumerable.Repeat((byte)'x', duplicate.Length).ToArray(),
            TestContext.Current.CancellationToken);

        DuplicateFileGroup group = Assert.Single(await DuplicateFileFinder.FindAsync(
            [_root], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(duplicate.Length, group.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(duplicate)), group.Sha256);
        Assert.Equal(2, group.Paths.Count);
        Assert.DoesNotContain(group.Paths, path => path.EndsWith("different.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverlappingRootsDoNotDuplicateAPath()
    {
        string path = Path.Combine(_root, "one.txt");
        await File.WriteAllTextAsync(path, "content", TestContext.Current.CancellationToken);

        IReadOnlyList<DuplicateFileGroup> groups = await DuplicateFileFinder.FindAsync(
            [_root, path], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task HardLinksCountAsOnePhysicalFile()
    {
        string original = Path.Combine(_root, "original.bin");
        string link = Path.Combine(_root, "hard-link.bin");
        string copy = Path.Combine(_root, "copy.bin");
        await File.WriteAllTextAsync(original, "identical", TestContext.Current.CancellationToken);
        Assert.True(CreateHardLink(link, original, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        File.Copy(original, copy);

        DuplicateFileGroup group = Assert.Single(await DuplicateFileFinder.FindAsync(
            [_root], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, group.Paths.Count);
        Assert.Contains(copy, group.Paths);
        Assert.Equal(1, group.Paths.Count(path =>
            path.Equals(original, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(link, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HonorsMinimumSizeAndCancellation()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "b"), "x", TestContext.Current.CancellationToken);
        IReadOnlyList<DuplicateFileGroup> groups = await DuplicateFileFinder.FindAsync(
            [_root], new DuplicateFileOptions { MinimumSize = 2 }, TestContext.Current.CancellationToken);
        Assert.Empty(groups);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DuplicateFileFinder.FindAsync([_root], cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
