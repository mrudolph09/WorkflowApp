using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace Workflow.Views;

/// <summary>
/// One Task: the form above, as tall as its content but at most half the tab, and the terminal
/// filling the rest below.
/// </summary>
public partial class TaskTabView : UserControl
{
    // One timer per copy icon, so the feedback of one button never resets the other's icon.
    private readonly Dictionary<PackIcon, DispatcherTimer> _copiedFeedback = [];

    /// <summary>Creates the view.</summary>
    public TaskTabView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    // The tab host stretches this view to the tab, so its height never depends on the form and
    // capping the form here cannot feed back into the next layout pass.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FormArea.MaxHeight = e.NewSize.Height / 2;
    }

    private void CopyTaskDescription_Click(object sender, RoutedEventArgs e) =>
        CopyWithFeedback(TaskDescriptionBox.Text, CopyIcon);

    private void CopyOpenGateItems_Click(object sender, RoutedEventArgs e) =>
        CopyWithFeedback(OpenGateItemsBox.Text, OpenGateItemsCopyIcon);

    private void CopyWithFeedback(string text, PackIcon icon)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another process still holds the clipboard after WPF's own retries. No check mark,
            // so the user sees that nothing was copied and can simply click again.
            return;
        }

        if (!_copiedFeedback.TryGetValue(icon, out var timer))
        {
            // Created on the first click, while the icon still shows its resting kind.
            var resting = icon.Kind;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                _copiedFeedback[icon].Stop();
                icon.Kind = resting;
            };
            _copiedFeedback[icon] = timer;
        }

        icon.Kind = PackIconKind.Check;
        timer.Stop();
        timer.Start();
    }
}
