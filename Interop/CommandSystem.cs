using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SizeMonitor.Interop;

public sealed record AppCommandDefinition(string Id, string Title, string Category,
    string Description, string? DefaultShortcut = null, IReadOnlyList<string>? Keywords = null);

public sealed record AppCommandMatch(AppCommandDefinition Command, string? EffectiveShortcut, int Score);

public static class CanopyCommandIds
{
    public const string ScanStart = "scan.start";
    public const string ScanCancel = "scan.cancel";
    public const string NavigationBack = "navigation.back";
    public const string NavigationForward = "navigation.forward";
    public const string FavoritesToggle = "favorites.toggle";
    public const string SavedSearchSave = "search.save";
    public const string HelpOpen = "help.open";
    public const string PaletteOpen = "palette.open";
}

public sealed class AppCommandRegistry
{
    readonly Dictionary<string, RegisteredCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> _shortcutOverrides = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AppCommandDefinition> Commands => _commands.Values.Select(x => x.Definition)
        .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Register(AppCommandDefinition definition, Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(execute);
        ValidateDefinition(definition);
        if (!_commands.TryAdd(definition.Id, new(definition, execute, canExecute ?? (() => true))))
            throw new ArgumentException($"Command '{definition.Id}' is already registered.", nameof(definition));
    }

    public bool CanExecute(string id) => _commands.TryGetValue(id, out RegisteredCommand? command) && command.CanExecute();

    public async Task<bool> ExecuteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_commands.TryGetValue(id, out RegisteredCommand? command) || !command.CanExecute()) return false;
        cancellationToken.ThrowIfCancellationRequested();
        await command.Execute(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<AppCommandMatch> Search(string? query, int maximum = 50)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        string[] terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _commands.Values.Select(item => Match(item.Definition, terms))
            .Where(match => match is not null && CanExecute(match.Command.Id)).Select(match => match!)
            .OrderByDescending(match => match.Score).ThenBy(match => match.Command.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maximum).ToArray();
    }

    public void SetShortcut(string id, string? shortcut)
    {
        if (!_commands.ContainsKey(id)) throw new KeyNotFoundException($"Unknown command '{id}'.");
        if (!string.IsNullOrWhiteSpace(shortcut)) ValidateShortcut(shortcut);
        string? normalized = string.IsNullOrWhiteSpace(shortcut) ? null : NormalizeShortcut(shortcut);
        if (normalized is not null)
        {
            string? collision = _commands.Keys.FirstOrDefault(other => !other.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetShortcut(other), normalized, StringComparison.OrdinalIgnoreCase));
            if (collision is not null) throw new ArgumentException($"Shortcut '{normalized}' is already assigned to '{collision}'.");
        }
        _shortcutOverrides[id] = normalized;
    }

    public string? GetShortcut(string id)
    {
        if (!_commands.TryGetValue(id, out RegisteredCommand? command)) return null;
        return _shortcutOverrides.TryGetValue(id, out string? value) ? value : command.Definition.DefaultShortcut;
    }

    public string? FindCommandByShortcut(string shortcut)
    {
        string normalized = NormalizeShortcut(shortcut);
        return _commands.Keys.FirstOrDefault(id => string.Equals(GetShortcut(id), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, string?> GetOverrides() =>
        new Dictionary<string, string?>(_shortcutOverrides, StringComparer.OrdinalIgnoreCase);

    public void ApplyOverrides(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        foreach ((string id, string? shortcut) in overrides)
            if (_commands.ContainsKey(id)) SetShortcut(id, shortcut);
    }

    public string GenerateShortcutHelp()
    {
        var text = new StringBuilder("# Keyboard shortcuts\n\n");
        foreach (IGrouping<string, AppCommandDefinition> category in Commands.GroupBy(x => x.Category))
        {
            text.Append("## ").Append(category.Key).Append("\n\n| Command | Shortcut | Description |\n|---|---|---|\n");
            foreach (AppCommandDefinition command in category)
                text.Append("| ").Append(command.Title).Append(" | ").Append(GetShortcut(command.Id) ?? "—")
                    .Append(" | ").Append(command.Description.Replace("|", "\\|", StringComparison.Ordinal)).Append(" |\n");
            text.Append('\n');
        }
        return text.ToString();
    }

    AppCommandMatch? Match(AppCommandDefinition command, string[] terms)
    {
        if (terms.Length == 0) return new(command, GetShortcut(command.Id), 0);
        string haystack = string.Join(' ', command.Title, command.Category, command.Description,
            command.Id, string.Join(' ', command.Keywords ?? []));
        int score = 0;
        foreach (string term in terms)
        {
            int position = haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (position < 0) return null;
            score += command.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 100 : position < command.Title.Length ? 50 : 10;
        }
        return new(command, GetShortcut(command.Id), score);
    }

    static void ValidateDefinition(AppCommandDefinition value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || !Regex.IsMatch(value.Id, "^[a-z][a-z0-9.-]{1,79}$"))
            throw new ArgumentException("Command ID is invalid.");
        if (string.IsNullOrWhiteSpace(value.Title) || string.IsNullOrWhiteSpace(value.Category) || string.IsNullOrWhiteSpace(value.Description))
            throw new ArgumentException("Command title, category, and description are required.");
        if (value.DefaultShortcut is not null) ValidateShortcut(value.DefaultShortcut);
    }
    static void ValidateShortcut(string value)
    {
        if (!Regex.IsMatch(NormalizeShortcut(value), "^(?:(?:Ctrl|Alt|Shift|Win)\\+)*(?:[A-Z0-9]|F(?:[1-9]|1[0-2])|Left|Right|Up|Down|Enter|Escape|Delete|Space)$", RegexOptions.IgnoreCase))
            throw new ArgumentException($"Shortcut '{value}' is invalid.");
    }
    static string NormalizeShortcut(string value) => string.Join('+', value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    sealed record RegisteredCommand(AppCommandDefinition Definition, Func<CancellationToken, Task> Execute, Func<bool> CanExecute);
}

public sealed class ShortcutOverrideStore(string filePath)
{
    const int Version = 1;
    readonly string _path = Path.GetFullPath(filePath);
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyDictionary<string, string?>> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, string?>();
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            Document? document;
            try { document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: token).ConfigureAwait(false); }
            catch (JsonException ex) { throw new InvalidDataException("Shortcut settings are invalid.", ex); }
            if (document is null || document.Version != Version || document.Overrides is null)
                throw new InvalidDataException("Shortcut settings schema is unsupported.");
            return new Dictionary<string, string?>(document.Overrides, StringComparer.OrdinalIgnoreCase);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string?> overrides, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var ordered = overrides.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Value);
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, new Document(Version, ordered), cancellationToken: token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                File.Move(temporary, _path, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        finally { _gate.Release(); }
    }
    sealed record Document(int Version, Dictionary<string, string?> Overrides);
}
