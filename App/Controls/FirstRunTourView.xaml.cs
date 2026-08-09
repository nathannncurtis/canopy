using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SizeMonitor.Controls;

public partial class FirstRunTourView : UserControl
{
    static readonly (string Title, string Body)[] Steps =
    [
        ("Choose what to scan", "Enter one or more local, mapped, or network paths. Scan options let you exclude sensitive or irrelevant data before enumeration begins."),
        ("Understand the numbers", "Canopy reports allocated size—the disk space reserved for a file. Logical size can differ because of compression, sparse files, and filesystem cluster rounding."),
        ("Elevation is optional", "Administrator access can enable faster MFT enumeration on NTFS and reach protected locations. Ordinary directory scanning remains available without elevation."),
        ("Your data stays local", "Scans run on this computer. Names and paths can still be sensitive, so review snapshots, exports, diagnostics, and screenshots before sharing them."),
        ("Explore and recover", "Use the analysis tabs to inspect results. Empty, partial, and failed states include a concrete next action; Help explains the underlying concepts."),
    ];
    int _index;
    public event Action? Completed;
    public event Action? Dismissed;
    public FirstRunTourView() { InitializeComponent(); Loaded += (_, _) => Keyboard.Focus(this); PreviewKeyDown += OnPreviewKeyDown; ShowStep(); }
    public void Restart() { _index = 0; ShowStep(); Keyboard.Focus(this); }
    void ShowStep() { _stepLabel.Text = $"Step {_index + 1} of {Steps.Length}"; _title.Text = Steps[_index].Title; _body.Text = Steps[_index].Body; _back.IsEnabled = _index > 0; _next.Content = _index == Steps.Length - 1 ? "Finish" : "Next"; }
    void OnBack(object sender, RoutedEventArgs e) { if (_index > 0) { _index--; ShowStep(); } }
    void OnNext(object sender, RoutedEventArgs e) { if (_index == Steps.Length - 1) Completed?.Invoke(); else { _index++; ShowStep(); } }
    void OnSkip(object sender, RoutedEventArgs e) => Dismissed?.Invoke();
    void OnPreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Left) OnBack(this, new()); else if (e.Key == Key.Right) OnNext(this, new()); else if (e.Key == Key.Enter && _index == Steps.Length - 1) Completed?.Invoke(); else if (e.Key == Key.Escape) Dismissed?.Invoke(); else return; e.Handled = true; }
}
