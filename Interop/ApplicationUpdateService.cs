using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SizeMonitor.Interop;

public enum UpdateChannel { Stable, Preview }

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease = null)
    : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Version is required.");
        string coreAndPre = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        string[] split = coreAndPre.Split('-', 2);
        string[] numbers = split[0].Split('.');
        if (numbers.Length is < 2 or > 3 || !int.TryParse(numbers[0], out int major) ||
            !int.TryParse(numbers[1], out int minor) ||
            (numbers.Length == 3 && !int.TryParse(numbers[2], out _)))
            throw new FormatException($"Invalid semantic version: {value}");
        int patch = numbers.Length == 3 ? int.Parse(numbers[2]) : 0;
        if (major < 0 || minor < 0 || patch < 0) throw new FormatException($"Invalid semantic version: {value}");
        string? prerelease = split.Length == 2 ? split[1] : null;
        if (prerelease is { Length: 0 }) throw new FormatException($"Invalid semantic version: {value}");
        return new(major, minor, patch, prerelease);
    }

    public int CompareTo(SemanticVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.'); string[] b = right.Split('.');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1; if (i >= b.Length) return 1;
            bool an = int.TryParse(a[i], out int ai), bn = int.TryParse(b[i], out int bi);
            int comparison = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : $"-{Prerelease}");
}

public sealed record ApplicationUpdate(Uri DownloadUri, SemanticVersion Version, string Sha256,
    string? ReleaseNotes, string? Signature);

public sealed record UpdateCheckResult(ApplicationUpdate? Update, SemanticVersion CurrentVersion,
    string Message);

public sealed class ApplicationUpdateService(HttpClient client, Uri manifestUri,
    TimeSpan? timeout = null, RSA? manifestSigningKey = null)
{
    readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<UpdateCheckResult> CheckAsync(SemanticVersion current, UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        RequireHttps(manifestUri, "manifest");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        using HttpResponseMessage response = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 256 * 1024) throw new InvalidDataException("Update manifest is too large.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        UpdateManifest? manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, timeoutCts.Token);
        if (manifest is null) throw new InvalidDataException("Update manifest is empty.");
        SemanticVersion version = SemanticVersion.Parse(manifest.Version ?? "");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? downloadUri))
            throw new InvalidDataException("Update download URL is invalid.");
        RequireHttps(downloadUri, "download");
        string sha = NormalizeSha256(manifest.Sha256);
        if (channel == UpdateChannel.Stable && version.Prerelease is not null)
            return new(null, current, "No stable update is available.");
        VerifyManifestSignature(manifest, version, downloadUri, sha);
        return version.CompareTo(current) > 0
            ? new(new(downloadUri, version, sha, manifest.ReleaseNotes, manifest.Signature), current, $"Canopy {version} is available.")
            : new(null, current, "Canopy is up to date.");
    }

    public async Task DownloadAsync(ApplicationUpdate update, string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        RequireHttps(update.DownloadUri, "download");
        string expected = NormalizeSha256(update.Sha256);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        using HttpResponseMessage response = await client.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();
        string fullDestination = Path.GetFullPath(destination);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Destination directory is required.", nameof(destination));
        Directory.CreateDirectory(directory);
        string temporary = fullDestination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using Stream input = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await input.CopyToAsync(output, timeoutCts.Token);
            string actual;
            await using (var verify = File.OpenRead(temporary))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, timeoutCts.Token));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
                throw new CryptographicException("Downloaded update SHA-256 does not match the signed manifest.");
            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    void VerifyManifestSignature(UpdateManifest manifest, SemanticVersion version, Uri downloadUri, string sha)
    {
        if (manifestSigningKey is null) return;
        if (string.IsNullOrWhiteSpace(manifest.Signature)) throw new CryptographicException("Update manifest signature is missing.");
        byte[] payload = Encoding.UTF8.GetBytes($"{version}\n{downloadUri.AbsoluteUri}\n{sha}");
        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException ex) { throw new CryptographicException("Update manifest signature is invalid.", ex); }
        if (!manifestSigningKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Update manifest signature verification failed.");
    }

    static string NormalizeSha256(string? value)
    {
        string normalized = (value ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Update SHA-256 is invalid.");
        return normalized;
    }

    static void RequireHttps(Uri uri, string purpose)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The update {purpose} URL must use HTTPS.");
    }

    sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? Signature { get; set; }
    }
}
