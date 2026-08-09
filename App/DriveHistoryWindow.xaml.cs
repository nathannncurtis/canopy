using System.Windows;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor;

public partial class DriveHistoryWindow : Window
{
    public DriveHistoryWindow(string rootPath, IReadOnlyList<DriveHistoryPoint> points)
    {
        InitializeComponent();
        _heading.Text = $"{rootPath} free-space history";
        _history.ItemsSource = points.OrderByDescending(point => point.CapturedUtc).Select(point => new Row(
            point.CapturedUtc.ToLocalTime().ToString("g"),
            SizeFormatter.FormatBytes(point.FreeBytes),
            $"{point.FreePercent:F1}%",
            point.ThresholdEvent ? "Threshold" : string.Empty)).ToArray();
        _summary.Text = points.Count == 0
            ? "No retained samples yet."
            : $"{points.Count:N0} retained samples · latest {points[^1].FreePercent:F1}% free";
    }

    sealed record Row(string Captured, string Free, string Percent, string Event);
}
