using System.Windows;
using System.Windows.Controls;

namespace SizeMonitor.Controls;

public enum ActionableStateKind { Empty, NoResults, PermissionRequired, Offline, Error }

public sealed record ActionableState(ActionableStateKind Kind, string Title, string Message,
    string? PrimaryAction = null, string? SecondaryAction = null, string? Details = null);

public partial class ActionableStateView : UserControl
{
    public event Action? PrimaryActionRequested;
    public event Action? SecondaryActionRequested;

    public ActionableStateView()
    {
        InitializeComponent();
        SetState(new(ActionableStateKind.Empty, "Nothing to show yet",
            "Choose a location and run a scan to get started.", "Choose a location"));
    }

    public ActionableState? State { get; private set; }

    public void SetState(ActionableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _category.Text = state.Kind switch
        {
            ActionableStateKind.Empty => "GET STARTED",
            ActionableStateKind.NoResults => "NO MATCHES",
            ActionableStateKind.PermissionRequired => "PERMISSION NEEDED",
            ActionableStateKind.Offline => "LOCATION UNAVAILABLE",
            _ => "SOMETHING WENT WRONG",
        };
        _title.Text = state.Title;
        _message.Text = state.Message;
        _details.Text = state.Details ?? string.Empty;
        _details.Visibility = string.IsNullOrWhiteSpace(state.Details) ? Visibility.Collapsed : Visibility.Visible;
        ConfigureButton(_primary, state.PrimaryAction);
        ConfigureButton(_secondary, state.SecondaryAction);
    }

    static void ConfigureButton(Button button, string? label)
    {
        button.Content = label ?? string.Empty;
        button.Visibility = string.IsNullOrWhiteSpace(label) ? Visibility.Collapsed : Visibility.Visible;
        if (label is not null) System.Windows.Automation.AutomationProperties.SetName(button, label);
    }

    void OnPrimary(object sender, RoutedEventArgs e) => PrimaryActionRequested?.Invoke();
    void OnSecondary(object sender, RoutedEventArgs e) => SecondaryActionRequested?.Invoke();
}
