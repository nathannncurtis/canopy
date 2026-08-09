using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public enum SelectionAction { Open, OpenContaining, CopyPaths, CopyText, CopyCsv, Properties, ElevatedTerminal }

public partial class SelectionActionsBar : UserControl
{
    public event Action<SelectionAction>? ActionRequested;

    public SelectionActionsBar() => InitializeComponent();

    public void SetSelection(ResultSelectionSummary? selection)
    {
        int count = selection?.Items.Count ?? 0;
        _summary.Text = count == 0
            ? "No items selected"
            : $"{count:N0} selected · {SizeFormatter.FormatBytes(selection!.TotalBytes)}";
        _open.IsEnabled = _containing.IsEnabled = _paths.IsEnabled =
            _text.IsEnabled = _csv.IsEnabled = _properties.IsEnabled = _terminal.IsEnabled = count > 0;
    }

    void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } &&
            Enum.TryParse(name, out SelectionAction action))
            ActionRequested?.Invoke(action);
    }
}
