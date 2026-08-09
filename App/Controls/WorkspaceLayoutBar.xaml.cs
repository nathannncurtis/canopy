using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class WorkspaceLayoutBar : UserControl
{
    public event Action<WorkspacePanelMode>? ModeRequested;
    public event Action? DetachRequested;
    public event Action? FullScreenRequested;
    public WorkspaceLayoutBar() => InitializeComponent();
    public void SetState(bool detached, bool fullScreen)
    {
        _detach.Content = detached ? "Attach treemap" : "Detach treemap";
        _detach.IsEnabled = !fullScreen;
        _fullScreen.Content = fullScreen ? "Exit full screen" : "Full screen";
        AutomationProperties.SetName(_detach, _detach.Content.ToString()!);
        AutomationProperties.SetName(_fullScreen, _fullScreen.Content.ToString()!);
    }
    void OnSplit(object sender, RoutedEventArgs e) => ModeRequested?.Invoke(WorkspacePanelMode.Split);
    void OnTree(object sender, RoutedEventArgs e) => ModeRequested?.Invoke(WorkspacePanelMode.TreeOnly);
    void OnTreemap(object sender, RoutedEventArgs e) => ModeRequested?.Invoke(WorkspacePanelMode.TreemapOnly);
    void OnDetach(object sender, RoutedEventArgs e) => DetachRequested?.Invoke();
    void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();
}
