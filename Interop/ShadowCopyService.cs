using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SizeMonitor.Interop;

public sealed record ShadowCopyInfo(string Id, string DeviceObject, string VolumeName,
    string? DriveRoot, DateTimeOffset CreatedUtc)
{
    public string DisplayName => $"{DriveRoot ?? VolumeName} — {CreatedUtc.LocalDateTime:g}";
}

public sealed class ShadowCopyException(string message, int? nativeCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? NativeCode { get; } = nativeCode;
}

public interface IShadowCopyCommandRunner
{
    Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

public sealed class WindowsShadowCopyService
{
    const string ListScript = """
        $volumes = @{}
        Get-CimInstance Win32_Volume | ForEach-Object { $volumes[$_.DeviceID] = $_.DriveLetter }
        @(Get-CimInstance Win32_ShadowCopy | ForEach-Object {
          [pscustomobject]@{ Id=$_.ID; DeviceObject=$_.DeviceObject; VolumeName=$_.VolumeName;
            DriveRoot=$volumes[$_.VolumeName]; CreatedUtc=$_.InstallDate.ToUniversalTime().ToString('o') }
        }) | ConvertTo-Json -Compress
        """;
    const string CreateScript = """
        $result = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create `
          -Arguments @{ Volume=$env:CANOPY_VSS_VOLUME; Context='ClientAccessible' }
        [pscustomobject]@{ ReturnValue=[int]$result.ReturnValue; ShadowID=$result.ShadowID } | ConvertTo-Json -Compress
        """;
    const string DeleteScript = """
        $snapshot = Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $env:CANOPY_VSS_ID } | Select-Object -First 1
        if ($null -ne $snapshot) { $snapshot | Remove-CimInstance }
        """;

    readonly IShadowCopyCommandRunner _runner;
    readonly HashSet<string> _sessionSnapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    public WindowsShadowCopyService(IShadowCopyCommandRunner? runner = null) =>
        _runner = runner ?? new PowerShellShadowCopyCommandRunner();

    public async Task<IReadOnlyList<ShadowCopyInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string json = await _runner.RunAsync(ListScript,
            new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            IEnumerable<JsonElement> items = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray(),
                JsonValueKind.Object => [document.RootElement],
                _ => throw new JsonException("Expected an object or array."),
            };
            return items.Select(Parse).OrderByDescending(item => item.CreatedUtc).ToArray();
        }
        catch (JsonException ex) { throw new ShadowCopyException("Windows returned invalid shadow-copy metadata.", inner: ex); }
    }

    public async Task<ShadowCopyLease> CreateSessionSnapshotAsync(string localPath,
        CancellationToken cancellationToken = default)
    {
        string root = GetLocalDriveRoot(localPath);
        string json = await _runner.RunAsync(CreateScript,
            new Dictionary<string, string> { ["CANOPY_VSS_VOLUME"] = root }, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            int code = document.RootElement.GetProperty("ReturnValue").GetInt32();
            string? id = document.RootElement.GetProperty("ShadowID").GetString();
            if (code != 0 || string.IsNullOrWhiteSpace(id))
                throw new ShadowCopyException(ExplainCreationFailure(code), code);
            ShadowCopyInfo? created = (await ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (created is null) throw new ShadowCopyException("The snapshot was created, but Windows did not return its metadata.");
            lock (_gate) _sessionSnapshots.Add(created.Id);
            return new ShadowCopyLease(this, created);
        }
        catch (JsonException ex) { throw new ShadowCopyException("Windows returned an invalid snapshot creation result.", inner: ex); }
    }

    internal async ValueTask ReleaseAsync(ShadowCopyInfo shadow)
    {
        lock (_gate)
            if (!_sessionSnapshots.Remove(shadow.Id)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _runner.RunAsync(DeleteScript,
                new Dictionary<string, string> { ["CANOPY_VSS_ID"] = shadow.Id }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ShadowCopyException or OperationCanceledException)
        {
            throw new ShadowCopyException("The scan completed, but its temporary VSS snapshot could not be released. Windows may retain it under system policy.", inner: ex);
        }
    }

    public static string ResolvePath(ShadowCopyInfo shadow, string livePath)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        string root = GetLocalDriveRoot(livePath);
        if (!string.IsNullOrWhiteSpace(shadow.DriveRoot) &&
            !string.Equals(Path.TrimEndingDirectorySeparator(shadow.DriveRoot),
                Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected shadow copy belongs to a different volume.", nameof(shadow));
        string full = Path.GetFullPath(livePath);
        string relative = Path.GetRelativePath(root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            throw new ArgumentException("The path is outside the selected volume.", nameof(livePath));
        return relative == "." ? shadow.DeviceObject + "\\" :
            shadow.DeviceObject.TrimEnd('\\') + "\\" + relative;
    }

    public static string GetLocalDriveRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\", StringComparison.Ordinal) ||
            root.Length < 3 || root[1] != ':')
            throw new ArgumentException("VSS supports local drive paths only.", nameof(path));
        return root;
    }

    static ShadowCopyInfo Parse(JsonElement item)
    {
        string Required(string name) => item.TryGetProperty(name, out JsonElement value) &&
            !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
            throw new JsonException($"Missing {name}.");
        string? drive = item.TryGetProperty("DriveRoot", out JsonElement driveValue) &&
            driveValue.ValueKind == JsonValueKind.String ? driveValue.GetString() : null;
        if (drive is { Length: 2 } && drive[1] == ':') drive += "\\";
        if (!DateTimeOffset.TryParse(Required("CreatedUtc"), out DateTimeOffset created))
            throw new JsonException("CreatedUtc is invalid.");
        return new(Required("Id"), Required("DeviceObject"), Required("VolumeName"), drive,
            created.ToUniversalTime());
    }

    static string ExplainCreationFailure(int code) => code switch
    {
        1 => "Windows denied snapshot access. Run Canopy as administrator and verify that VSS is enabled.",
        8 => "Windows could not create a snapshot because the VSS provider reported an unspecified failure.",
        9 => "Windows timed out while creating the snapshot.",
        10 => "The volume does not support shadow copies.",
        11 => "The requested volume was not found.",
        _ => $"Windows could not create the snapshot (VSS return code {code}).",
    };
}

public sealed class ShadowCopyLease : IAsyncDisposable
{
    WindowsShadowCopyService? _owner;
    internal ShadowCopyLease(WindowsShadowCopyService owner, ShadowCopyInfo snapshot)
    { _owner = owner; Snapshot = snapshot; }
    public ShadowCopyInfo Snapshot { get; }
    public async ValueTask DisposeAsync()
    {
        WindowsShadowCopyService? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null) await owner.ReleaseAsync(Snapshot).ConfigureAwait(false);
    }
}

public sealed class PowerShellShadowCopyCommandRunner : IShadowCopyCommandRunner
{
    public async Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VSS is available only on Windows.");
        var start = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -Command -")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach ((string key, string value) in environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new ShadowCopyException("Could not start the Windows VSS provider command.");
        await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        string stderr = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new ShadowCopyException(string.IsNullOrWhiteSpace(stderr) ?
                "The Windows VSS command failed." : stderr.Trim());
        return await output.ConfigureAwait(false);
    }
}
