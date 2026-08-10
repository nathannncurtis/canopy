using System.Text.Json;
using System.Text.Json.Serialization;

namespace SizeMonitor.Interop;

public static class RecoveryVaultPathResolver
{
    public static string Resolve(string targetPath, string? localAppData = null, string? userName = null)
    {
        string targetRoot = Path.GetPathRoot(Path.GetFullPath(targetPath)) ?? throw new ArgumentException("Target has no volume.");
        string local = Path.GetFullPath(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (string.Equals(Path.GetPathRoot(local), targetRoot, StringComparison.OrdinalIgnoreCase)) return Path.Combine(local, "Canopy", "Recovery");
        string user = userName ?? Environment.UserName;
        string safe = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user)))[..16];
        return Path.Combine(targetRoot, ".CanopyRecovery", safe);
    }
}

public sealed record RecoveryVaultManifest(int Version, string OriginalPath, string StoredPath,
    CleanupTargetSnapshot Original, CleanupTargetSnapshot Stored, DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

public sealed class RecoveryVaultBackend(
    ICleanupFileSystem fileSystem,
    ICleanupMutationBackend fallback,
    Func<string, string> vaultRootForPath,
    TimeSpan? retention = null,
    Func<string, string, CancellationToken, Task>? manifestWriter = null) : ICleanupMutationBackend
{
    readonly TimeSpan _retention = retention ?? TimeSpan.FromDays(30);
    const long MaximumManifestBytes = 64 * 1024;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async ValueTask<CleanupMutationResult> MutateAsync(CleanupTargetSnapshot target,
        CleanupMutationMode disposition, bool recursive, CancellationToken token)
    {
        if (disposition != CleanupMutationMode.Recoverable)
            return await fallback.MutateAsync(target, disposition, recursive, token);
        token.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(vaultRootForPath(target.CanonicalPath));
        if (!string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(target.CanonicalPath), StringComparison.OrdinalIgnoreCase))
            return new(false, 0, "App-owned recovery storage must be on the same volume.");
        Directory.CreateDirectory(root);
        string id = Guid.NewGuid().ToString("N");
        string stored = Path.Combine(root, id + ".payload");
        string manifestPath = Path.Combine(root, id + ".recovery.json");
        if (target.Type == CleanupTargetType.Directory) Directory.Move(target.CanonicalPath, stored);
        else File.Move(target.CanonicalPath, stored);
        try
        {
            CleanupTargetSnapshot storedSnapshot = fileSystem.Capture(stored);
            var manifest = new RecoveryVaultManifest(1, target.CanonicalPath, stored, target,
                storedSnapshot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + _retention);
            string temporary = manifestPath + ".tmp";
            if (manifestWriter is null) await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Json), token);
            else await manifestWriter(temporary, JsonSerializer.Serialize(manifest, Json), token);
            File.Move(temporary, manifestPath);
            return new(true, 0, "Moved to Canopy app-owned recovery; this does not immediately free disk space.", manifestPath);
        }
        catch
        {
            if (!File.Exists(target.CanonicalPath) && !Directory.Exists(target.CanonicalPath))
            {
                if (target.Type == CleanupTargetType.Directory) Directory.Move(stored, target.CanonicalPath);
                else File.Move(stored, target.CanonicalPath);
            }
            throw;
        }
    }

    public async ValueTask<bool> UndoAsync(string token, CancellationToken cancellationToken)
    {
        string manifestPath = Path.GetFullPath(token);
        if (new FileInfo(manifestPath).Length > MaximumManifestBytes) throw new InvalidDataException("Recovery manifest is too large.");
        RecoveryVaultManifest manifest = JsonSerializer.Deserialize<RecoveryVaultManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken), Json) ?? throw new InvalidDataException("Recovery manifest is empty.");
        if (manifest.Version != 1 || DateTimeOffset.UtcNow > manifest.ExpiresUtc) return false;
        string expectedRoot = Path.GetFullPath(vaultRootForPath(manifest.OriginalPath));
        if (CleanupSafetyPolicy.Evaluate(manifest.OriginalPath, manifest.Original.Size, ulong.MaxValue).IsBlocked ||
            !SameVolume(expectedRoot, manifest.OriginalPath) || !IsBelow(manifestPath, expectedRoot) ||
            !IsBelow(manifest.StoredPath, expectedRoot) || !Correlates(manifestPath, manifest.StoredPath)) return false;
        if (File.Exists(manifest.OriginalPath) || Directory.Exists(manifest.OriginalPath)) return false;
        CleanupTargetSnapshot current = fileSystem.Capture(manifest.StoredPath);
        if (current != manifest.Stored) return false;
        string? parent = Path.GetDirectoryName(manifest.OriginalPath); if (parent is not null) Directory.CreateDirectory(parent);
        if (manifest.Original.Type == CleanupTargetType.Directory) Directory.Move(manifest.StoredPath, manifest.OriginalPath);
        else File.Move(manifest.StoredPath, manifest.OriginalPath);
        CleanupTargetSnapshot restored = fileSystem.Capture(manifest.OriginalPath);
        if (restored.Identity != manifest.Original.Identity) throw new IOException("Restored item identity could not be verified.");
        File.Delete(manifestPath);
        return true;
    }

    public int RemoveExpired(string representativePath, DateTimeOffset now)
    {
        string root = Path.GetFullPath(vaultRootForPath(representativePath));
        if (!Directory.Exists(root)) return 0;
        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.recovery.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (new FileInfo(path).Length > MaximumManifestBytes) continue;
                RecoveryVaultManifest? manifest = JsonSerializer.Deserialize<RecoveryVaultManifest>(File.ReadAllText(path), Json);
                if (manifest is null || manifest.Version != 1 || manifest.ExpiresUtc > now || !IsBelow(manifest.StoredPath, root) ||
                    !SameVolume(root, manifest.OriginalPath) || !Correlates(path, manifest.StoredPath) ||
                    CleanupSafetyPolicy.Evaluate(manifest.OriginalPath, manifest.Original.Size, ulong.MaxValue).IsBlocked) continue;
                CleanupTargetSnapshot current = fileSystem.Capture(manifest.StoredPath);
                if (current != manifest.Stored || current.Type == CleanupTargetType.Directory && current.IsReparsePoint) continue;
                if (manifest.Stored.Type == CleanupTargetType.Directory) DeleteTreeWithoutFollowingReparsePoints(manifest.StoredPath);
                else File.Delete(manifest.StoredPath);
                File.Delete(path); removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return removed;
    }

    static bool IsBelow(string path, string root)
    {
        string candidate = Path.GetFullPath(path), normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return candidate.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    static bool SameVolume(string left, string right) => string.Equals(Path.GetPathRoot(Path.GetFullPath(left)), Path.GetPathRoot(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    static bool Correlates(string manifest, string payload)
    {
        string manifestName = Path.GetFileName(manifest); const string suffix = ".recovery.json";
        if (!manifestName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        string id = manifestName[..^suffix.Length];
        return Guid.TryParseExact(id, "N", out _) && string.Equals(Path.GetFileName(payload), id + ".payload", StringComparison.OrdinalIgnoreCase);
    }
    static void DeleteTreeWithoutFollowingReparsePoints(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteTreeWithoutFollowingReparsePoints(entry);
            else if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(entry, false);
            else File.Delete(entry);
        }
        Directory.Delete(directory, false);
    }
}
