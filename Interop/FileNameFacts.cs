namespace SizeMonitor.Interop;

internal static class FileNameFacts
{
    internal static string ExtensionOf(string name)
    {
        // Product convention: a single leading dot denotes a dotfile name, while a
        // later dot still introduces an extension (for example, .config.json).
        if (name.StartsWith('.') && name.IndexOf('.', 1) < 0) return string.Empty;
        string extension = Path.GetExtension(name);
        return extension == "." ? string.Empty : extension;
    }
}
