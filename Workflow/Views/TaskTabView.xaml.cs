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
    private readonly DispatcherTimer _copiedFeedback = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <summary>Creates the view.</summary>
    public TaskTabView()
    {
        InitializeComponent();
        _copiedFeedback.Tick += OnCopiedFeedbackElapsed;
        SizeChanged += OnSizeChanged;
    }

    // The tab host stretches this view to the tab, so its height never depends on the form and
    // capping the form here cannot feed back into the next layout pass.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FormArea.MaxHeight = e.NewSize.Height / 2;
    }

    private void CopyTaskDescription_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TaskDescriptionBox.Text);
        }
        catch (ExternalException)
        {
            // Another process still holds the clipboard after WPF's own retries. No check mark,
            // so the user sees that nothing was copied and can simply click again.
            return;
        }

        CopyIcon.Kind = PackIconKind.Check;
        _copiedFeedback.Stop();
        _copiedFeedback.Start();
    }

    private void OnCopiedFeedbackElapsed(object? sender, EventArgs e)
    {
        _copiedFeedback.Stop();
        CopyIcon.Kind = PackIconKind.ContentCopy;
    }
}
