using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Workflow.Behaviors;

namespace Workflow.Tests;

public class RichTextBoxAssistTests
{
    private static string ReadDocument(RichTextBox box) =>
        new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    [StaFact]
    public void SettingPlainText_FillsTheDocument()
    {
        var box = new RichTextBox();

        RichTextBoxAssist.SetPlainText(box, "hello");

        Assert.Equal("hello", ReadDocument(box));
    }

    [StaFact]
    public void SettingPlainText_PreservesMultipleLines()
    {
        var box = new RichTextBox();

        RichTextBoxAssist.SetPlainText(box, "one\ntwo\nthree");

        Assert.Equal(3, box.Document.Blocks.Count);
        Assert.Contains("one", ReadDocument(box), StringComparison.Ordinal);
        Assert.Contains("two", ReadDocument(box), StringComparison.Ordinal);
        Assert.Contains("three", ReadDocument(box), StringComparison.Ordinal);
    }

    // The defect this pins: setting PlainText to string.Empty is setting it to its own DEFAULT,
    // so the property-changed callback does not fire. If TextChanged were wired only from there,
    // this test would read "" and phase 1 would render an empty {taskbeschreibung}.
    [StaFact]
    public void EditingTheDocument_PushesPlainTextBack()
    {
        var box = new RichTextBox();
        RichTextBoxAssist.SetPlainText(box, string.Empty);

        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run("typed")));

        Assert.Equal("typed", RichTextBoxAssist.GetPlainText(box));
    }

    [StaFact]
    public void EditingTheDocument_ReachesAViewModelThroughARealTwoWayBinding()
    {
        // The end-to-end path that actually matters: TaskTabViewModel.TaskDescription starts
        // empty, the user types, and the prompt renderer must see the text. Exercising the
        // attached property alone would not catch a binding that SetValue had detached.
        var source = new TextHolder();
        var box = new RichTextBox();

        BindingOperations.SetBinding(
            box,
            RichTextBoxAssist.PlainTextProperty,
            new Binding(nameof(TextHolder.Text))
            {
                Source = source,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });

        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run("typed")));

        Assert.Equal("typed", source.Text);

        // And the binding survives the write-back, so the reverse direction still works.
        source.Text = "from the view model";
        Assert.Equal("from the view model", ReadDocument(box));
    }

    private sealed class TextHolder : INotifyPropertyChanged
    {
        private string _text = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Text
        {
            get => _text;
            set
            {
                if (string.Equals(_text, value, StringComparison.Ordinal))
                {
                    return;
                }

                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }

    [StaFact]
    public void SettingTheSameValueTwice_DoesNotRecurse()
    {
        var box = new RichTextBox();

        RichTextBoxAssist.SetPlainText(box, "stable");
        RichTextBoxAssist.SetPlainText(box, "stable");

        Assert.Equal("stable", ReadDocument(box));
    }

    [StaFact]
    public void SettingNull_ClearsTheDocument()
    {
        var box = new RichTextBox();
        RichTextBoxAssist.SetPlainText(box, "something");

        RichTextBoxAssist.SetPlainText(box, null);

        Assert.Equal(string.Empty, ReadDocument(box));
    }
}
