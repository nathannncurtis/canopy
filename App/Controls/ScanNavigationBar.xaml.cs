using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ScanNavigationBar : UserControl
{
    ScanNavigation? _navigation;

    public event Action<uint>? NodeActivated;

    public ScanNavigationBar()
    {
        InitializeComponent();
        SetNavigation(null);
        AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnNavigationKeyDown));
    }

    public ScanNavigation? Navigation => _navigation;

    public void SetNavigation(ScanNavigation? navigation)
    {
        _navigation = navigation;
        IsEnabled = navigation is not null;
        Refresh();
    }

    public bool NavigateTo(uint nodeIndex)
    {
        if (_navigation is null) return false;
        bool changed = _navigation.Navigate(nodeIndex);
        Refresh();
        return changed;
    }

    public bool GoBack() => Move(navigation => navigation.GoBack());

    public bool GoForward() => Move(navigation => navigation.GoForward());

    void OnBack(object sender, RoutedEventArgs e) => GoBack();

    void OnForward(object sender, RoutedEventArgs e) => GoForward();

    void OnGo(object sender, RoutedEventArgs e) => NavigateToTypedPath();

    void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NavigateToTypedPath();
        e.Handled = true;
    }

    void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        if (e.SystemKey == Key.Left)
        {
            Move(navigation => navigation.GoBack());
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Right)
        {
            Move(navigation => navigation.GoForward());
            e.Handled = true;
        }
    }

    void NavigateToTypedPath()
    {
        if (_navigation is null) return;
        string requestedPath = _pathBox.Text;
        if (!_navigation.Index.TryFind(requestedPath, out uint node))
        {
            bool ambiguous = _navigation.Index.IsAmbiguous(requestedPath);
            _status.Text = ambiguous ? "Path matches more than one scanned item." : "Path was not found in this scan.";
            _pathBox.ToolTip = ambiguous
                ? $"'{requestedPath}' is ambiguous. Select the intended item in the tree."
                : $"'{requestedPath}' was not found. The current location has not changed.";
            RaiseStatusChanged();
            _pathBox.Focus();
            _pathBox.SelectAll();
            return;
        }

        if (_navigation.Navigate(node))
            ActivateCurrent();
        else
            Refresh();
    }

    bool Move(Func<ScanNavigation, bool> action)
    {
        if (_navigation is not null && action(_navigation))
        {
            ActivateCurrent();
            return true;
        }
        return false;
    }

    void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (_navigation is null || sender is not Button { Tag: uint node }) return;
        if (_navigation.Navigate(node))
            ActivateCurrent();
        else
            Refresh();
    }

    void ActivateCurrent()
    {
        bool restoreFocus = IsKeyboardFocusWithin;
        Refresh();
        if (restoreFocus) _pathBox.Focus();
        NodeActivated?.Invoke(_navigation!.Current);
    }

    void RaiseStatusChanged()
    {
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(_status)
            ?? UIElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void Refresh()
    {
        _breadcrumbs.Children.Clear();
        _status.Text = string.Empty;
        _pathBox.ToolTip = "Type a scanned path and press Enter";

        if (_navigation is null)
        {
            _pathBox.Text = string.Empty;
            _backButton.IsEnabled = false;
            _forwardButton.IsEnabled = false;
            _goButton.IsEnabled = false;
            return;
        }

        _pathBox.Text = _navigation.CurrentPath;
        _backButton.IsEnabled = _navigation.CanGoBack;
        _forwardButton.IsEnabled = _navigation.CanGoForward;
        _goButton.IsEnabled = true;

        IReadOnlyList<ScanBreadcrumb> crumbs = _navigation.Breadcrumbs;
        for (int i = 0; i < crumbs.Count; i++)
        {
            ScanBreadcrumb crumb = crumbs[i];
            if (i > 0)
            {
                _breadcrumbs.Children.Add(new TextBlock
                {
                    Text = "›",
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var button = new Button
            {
                Content = crumb.Name,
                Tag = crumb.NodeIndex,
                ToolTip = $"Go to {crumb.Path}",
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = crumb.NodeIndex != _navigation.Current,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"Go to {crumb.Name}");
            button.Click += OnBreadcrumbClick;
            _breadcrumbs.Children.Add(button);
        }
    }
}
