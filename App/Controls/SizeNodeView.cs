namespace SizeMonitor.Controls;

using SizeMonitor.Interop;

public sealed class SizeNodeView : System.ComponentModel.INotifyPropertyChanged
{
    bool _isMarked;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public uint               Index      { get; init; }
    public string             Name       { get; init; } = string.Empty;
    public ulong              Size       { get; init; }
    public bool               IsDir      { get; init; }
    public ulong              ParentSize { get; init; }
    public List<SizeNodeView> Children   { get; } = new();
    public bool IsMarked
    {
        get => _isMarked;
        set { if (_isMarked == value) return; _isMarked = value; PropertyChanged?.Invoke(this, new(nameof(IsMarked))); }
    }

    // Fraction of parent's size, clamped [0,1]. Used for the size bar width.
    public double SizeBarFraction => ParentSize > 0
        ? Math.Min(1.0, (double)Size / ParentSize)
        : 0.0;

    public string SizeText => Helpers.SizeFormatter.FormatBytes(Size);
}
