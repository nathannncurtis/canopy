using System.Text.Json;

namespace SizeMonitor.Interop;

public sealed record ScanFilterPreset(string Name, ScanQuery Query);

public sealed class ScanFilterPresetStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    readonly string _filePath;
    readonly SemaphoreSlim _gate = new(1, 1);

    public ScanFilterPresetStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<IReadOnlyList<ScanFilterPreset>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScanFilterPreset>> UpsertAsync(
        ScanFilterPreset preset,
        CancellationToken cancellationToken = default)
    {
        ScanFilterPreset normalized = Normalize(preset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var presets = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int existing = presets.FindIndex(item =>
                string.Equals(item.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) presets[existing] = normalized;
            else presets.Add(normalized);
            presets.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            await SaveCoreAsync(presets, cancellationToken).ConfigureAwait(false);
            return presets;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Removed, IReadOnlyList<ScanFilterPreset> Presets)> DeleteAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var presets = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int removed = presets.RemoveAll(item =>
                string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                await SaveCoreAsync(presets, cancellationToken).ConfigureAwait(false);
            return (removed > 0, presets);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<IReadOnlyList<ScanFilterPreset>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            List<ScanFilterPreset>? presets = await JsonSerializer.DeserializeAsync<List<ScanFilterPreset>>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            var canonical = new Dictionary<string, ScanFilterPreset>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ScanFilterPreset preset in presets ?? [])
                {
                    ScanFilterPreset normalized = Normalize(preset);
                    canonical[normalized.Name] = normalized;
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"Filter preset file is invalid: {_filePath}", ex);
            }
            return canonical.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Filter preset file is invalid: {_filePath}", ex);
        }
    }

    async Task SaveCoreAsync(
        IReadOnlyCollection<ScanFilterPreset> presets,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, presets, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    static ScanFilterPreset Normalize(ScanFilterPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(preset.Query);
        string name = preset.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
            throw new ArgumentException("Preset names must contain 1 to 100 characters.", nameof(preset));
        return preset with { Name = name };
    }
}
