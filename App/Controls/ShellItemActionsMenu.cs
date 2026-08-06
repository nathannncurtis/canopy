using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SizeMonitor.Helpers;

namespace SizeMonitor.Controls;

public sealed class ShellItemActionFailedEventArgs(
    string action,
    string? itemPath,
    Exception exception) : EventArgs
{
    public string Action { get; } = action;
    public string? ItemPath { get; } = itemPath;
    public Exception Exception { get; } = exception;
}

/// <summary>A reusable context menu for filesystem-backed UI items.</summary>
public sealed class ShellItemActionsMenu : ContextMenu
{
    public static readonly DependencyProperty ItemPathProperty = DependencyProperty.Register(
        nameof(ItemPath),
        typeof(string),
        typeof(ShellItemActionsMenu),
        new PropertyMetadata(null, OnItemPathChanged));

    readonly MenuItem _open;
    readonly MenuItem _containingFolder;
    readonly MenuItem _copyPath;
    readonly MenuItem _properties;
    readonly MenuItem _elevatedTerminal;
    int _availabilityGeneration;

    public ShellItemActionsMenu()
    {
        _open = AddItem("Open", "open", (_, _) => Run("Open", ShellItemActions.Open));
        _containingFolder = AddItem(
            "Open containing folder", "containing-folder",
            (_, _) => Run("Open containing folder", ShellItemActions.OpenContainingFolder));
        _copyPath = AddItem("Copy full path", "copy-path", CopyPathAsync);
        Items.Add(new Separator());
        _properties = AddItem("Properties", "properties", (_, _) => Run("Properties", ShellItemActions.ShowProperties));
        _elevatedTerminal = AddItem(
            "Open elevated terminal here", "elevated-terminal",
            (_, _) => Run("Open elevated terminal", ShellItemActions.OpenElevatedTerminal));

        Opened += (_, _) => RefreshAvailability();
        SetAvailability(false, false);
    }

    public string? ItemPath
    {
        get => (string?)GetValue(ItemPathProperty);
        set => SetValue(ItemPathProperty, value);
    }

    /// <summary>Raised when an enabled action fails, allowing the host to choose its error UI.</summary>
    public event EventHandler<ShellItemActionFailedEventArgs>? ActionFailed;

    public void RefreshAvailability()
    {
        string? path = ItemPath;
        int generation = Interlocked.Increment(ref _availabilityGeneration);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetAvailability(false, false);
            return;
        }

        // Actions validate again at invocation; optimistic state avoids blocking selection changes.
        SetAvailability(true, true);
        _ = ProbeAvailabilityAsync(path, generation);
    }

    async Task ProbeAvailabilityAsync(string path, int generation)
    {
        (bool Exists, bool HasContainingFolder) availability = await Task.Run(() => Probe(path));
        if (generation != Volatile.Read(ref _availabilityGeneration) ||
            !string.Equals(path, ItemPath, StringComparison.Ordinal))
            return;
        SetAvailability(availability.Exists, availability.HasContainingFolder);
    }

    static (bool Exists, bool HasContainingFolder) Probe(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(fullPath);
            bool isFile = (attributes & FileAttributes.Directory) == 0;
            return (true, isFile || Path.GetDirectoryName(fullPath) is { } parent && Directory.Exists(parent));
        }
        catch (UnauthorizedAccessException)
        {
            return (true, true);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return (false, false);
        }
    }

    void SetAvailability(bool exists, bool hasContainingFolder)
    {
        _open.IsEnabled = exists;
        _containingFolder.IsEnabled = hasContainingFolder;
        _copyPath.IsEnabled = exists;
        _properties.IsEnabled = exists;
        _elevatedTerminal.IsEnabled = exists;
    }

    MenuItem AddItem(string header, string automationId, RoutedEventHandler handler)
    {
        var item = new MenuItem
        {
            Header = header,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += handler;
        Items.Add(item);
        return item;
    }

    void Run(string action, Action<string> operation)
    {
        string? path = ItemPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            operation(path);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user dismissed a consent or file-association prompt.
        }
        catch (Exception ex)
        {
            Logger.Error($"Shell action '{action}' failed for '{path}'", ex);
            RefreshAvailability();
            ActionFailed?.Invoke(this, new(action, path, ex));
        }
    }

    async void CopyPathAsync(object sender, RoutedEventArgs e)
    {
        string? path = ItemPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await ShellItemActions.CopyFullPathAsync(path);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user dismissed a shell prompt.
        }
        catch (Exception ex)
        {
            Logger.Error($"Shell action 'Copy full path' failed for '{path}'", ex);
            RefreshAvailability();
            ActionFailed?.Invoke(this, new("Copy full path", path, ex));
        }
    }

    static void OnItemPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var menu = (ShellItemActionsMenu)sender;
        Interlocked.Increment(ref menu._availabilityGeneration);
        menu.SetAvailability(!string.IsNullOrWhiteSpace((string?)args.NewValue),
            !string.IsNullOrWhiteSpace((string?)args.NewValue));
    }
}
