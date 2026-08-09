using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public void SemanticVersionsOrderPrereleasesBeforeRelease()
    {
        Assert.True(SemanticVersion.Parse("1.2.3-beta.2").CompareTo(SemanticVersion.Parse("1.2.3")) < 0);
        Assert.True(SemanticVersion.Parse("1.2.3-beta.10").CompareTo(SemanticVersion.Parse("1.2.3-beta.2")) > 0);
    }

    [Fact]
    public async Task StableChannelIgnoresPrerelease()
    {
        using var client = Client("""{"version":"2.0.0-beta.1","downloadUrl":"https://updates.example/canopy.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        var service = new ApplicationUpdateService(client, new("https://updates.example/manifest.json"));
        UpdateCheckResult result = await service.CheckAsync(SemanticVersion.Parse("1.0.0"), UpdateChannel.Stable, TestContext.Current.CancellationToken);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task RejectsNonHttpsDownload()
    {
        using var client = Client("""{"version":"2.0.0","downloadUrl":"http://updates.example/canopy.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        var service = new ApplicationUpdateService(client, new("https://updates.example/manifest.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(SemanticVersion.Parse("1.0.0"), UpdateChannel.Stable, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifiesSignedManifestAndDownloadedHash()
    {
        byte[] package = Encoding.UTF8.GetBytes("verified installer bytes");
        string sha = Convert.ToHexString(SHA256.HashData(package));
        using RSA signer = RSA.Create(2048);
        string payload = $"2.0.0\nhttps://updates.example/canopy.exe\n{sha}";
        string signature = Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        string manifest = $$"""{"version":"2.0.0","downloadUrl":"https://updates.example/canopy.exe","sha256":"{{sha}}","signature":"{{signature}}"}""";
        using var client = new HttpClient(new RoutingHandler(manifest, package));
        var service = new ApplicationUpdateService(client, new("https://updates.example/manifest.json"), manifestSigningKey: signer);
        ApplicationUpdate update = Assert.IsType<ApplicationUpdate>((await service.CheckAsync(SemanticVersion.Parse("1.0.0"), UpdateChannel.Stable, TestContext.Current.CancellationToken)).Update);
        string directory = Path.Combine(Path.GetTempPath(), "canopy-update-test-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "canopy.exe");
        try { await service.DownloadAsync(update, destination, TestContext.Current.CancellationToken); Assert.Equal(package, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DeletesPartialFileWhenHashDoesNotMatch()
    {
        using var client = new HttpClient(new RoutingHandler(
            """{"version":"2.0.0","downloadUrl":"https://updates.example/canopy.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            Encoding.UTF8.GetBytes("wrong")));
        var service = new ApplicationUpdateService(client, new("https://updates.example/manifest.json"));
        ApplicationUpdate update = Assert.IsType<ApplicationUpdate>((await service.CheckAsync(SemanticVersion.Parse("1.0.0"), UpdateChannel.Stable, TestContext.Current.CancellationToken)).Update);
        string directory = Path.Combine(Path.GetTempPath(), "canopy-update-test-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "canopy.exe");
        try { await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadAsync(update, destination, TestContext.Current.CancellationToken)); Assert.False(File.Exists(destination)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static HttpClient Client(string manifest) => new(new RoutingHandler(manifest, []));

    sealed class RoutingHandler(string manifest, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                    ? new StringContent(manifest, Encoding.UTF8, "application/json")
                    : new ByteArrayContent(package),
            };
            return Task.FromResult(response);
        }
    }
}
