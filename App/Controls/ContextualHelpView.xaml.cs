using System.Windows.Controls;

namespace SizeMonitor.Controls;

public enum HelpTopic { Size, Elevation, Privacy, States }

public partial class ContextualHelpView : UserControl
{
    public ContextualHelpView()
    {
        InitializeComponent();
        _topics.SelectedIndex = 0;
    }

    public HelpTopic SelectedTopic { get; private set; } = HelpTopic.Size;
    public event Action<HelpTopic>? TopicChanged;

    public void ShowTopic(HelpTopic topic)
    {
        foreach (ListBoxItem item in _topics.Items)
            if (string.Equals(item.Tag?.ToString(), topic.ToString(), StringComparison.Ordinal))
            {
                item.IsSelected = true;
                if (item.Content is Expander expander) expander.IsExpanded = true;
                item.BringIntoView();
                return;
            }
        throw new ArgumentOutOfRangeException(nameof(topic));
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_topics.SelectedItem is not ListBoxItem item ||
            !Enum.TryParse(item.Tag?.ToString(), out HelpTopic topic)) return;
        SelectedTopic = topic;
        if (item.Content is Expander expander) expander.IsExpanded = true;
        TopicChanged?.Invoke(topic);
    }
}
