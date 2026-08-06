using System.IO;
using System.Windows;
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
        RefreshAvailability();
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
        bool exists = false;
        bool hasContainingFolder = false;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                bool isFile = File.Exists(fullPath);
                bool isDirectory = Directory.Exists(fullPath);
                exists = isFile || isDirectory;
                hasContainingFolder = exists &&
                    (isFile || Path.GetDirectoryName(fullPath) is { } parent && Directory.Exists(parent));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                exists = false;
            }
        }

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
            Tag = automationId,
        };
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
        catch (Exception ex)
        {
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
        catch (Exception ex)
        {
            RefreshAvailability();
            ActionFailed?.Invoke(this, new("Copy full path", path, ex));
        }
    }

    static void OnItemPathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ShellItemActionsMenu)sender).RefreshAvailability();
}
