using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Workflow.Behaviors;

/// <summary>
/// Two-way binding between a RichTextBox's FlowDocument and a plain string. The requirement asks
/// for a rich text field; the prompt consumes plain text, so formatting is flattened at this
/// boundary. That is the point: pasting from Word, Notion or a browser yields clean text.
/// </summary>
public static class RichTextBoxAssist
{
    /// <summary>Identifies the PlainText attached property.</summary>
    public static readonly DependencyProperty PlainTextProperty =
        DependencyProperty.RegisterAttached(
            "PlainText",
            typeof(string),
            typeof(RichTextBoxAssist),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPlainTextChanged,
                CoercePlainText));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating", typeof(bool), typeof(RichTextBoxAssist), new PropertyMetadata(false));

    /// <summary>Reads the plain text of a RichTextBox.</summary>
    /// <param name="element">The RichTextBox.</param>
    /// <returns>The current plain text.</returns>
    public static string GetPlainText(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (string)element.GetValue(PlainTextProperty);
    }

    /// <summary>Writes the plain text of a RichTextBox.</summary>
    /// <param name="element">The RichTextBox.</param>
    /// <param name="value">The text to show.</param>
    public static void SetPlainText(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(PlainTextProperty, value ?? string.Empty);
    }

    // A property-changed callback only runs when the EFFECTIVE VALUE CHANGES. The common case -
    // a fresh tab whose TaskDescription is "" - binds this property to the same value as its own
    // default, so OnPlainTextChanged never fires. Wiring TextChanged only from there would leave
    // the document -> source direction unconnected and silently drop everything the user types,
    // leaving phase 1 to render initial_prompt.md with an empty {taskbeschreibung}.
    // The coercion callback runs on every set and on binding attachment, change or not.
    private static object? CoercePlainText(DependencyObject d, object? baseValue)
    {
        if (d is RichTextBox box)
        {
            Attach(box);
        }

        return baseValue ?? string.Empty;
    }

    private static void OnPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box || (bool)box.GetValue(IsUpdatingProperty))
        {
            return;
        }

        Attach(box);

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            var text = e.NewValue as string ?? string.Empty;
            box.Document.Blocks.Clear();

            if (text.Length > 0)
            {
                foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    box.Document.Blocks.Add(new Paragraph(new Run(line)));
                }
            }
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }

    private static void Attach(RichTextBox box)
    {
        box.TextChanged -= OnTextChanged;
        box.TextChanged += OnTextChanged;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not RichTextBox box || (bool)box.GetValue(IsUpdatingProperty))
        {
            return;
        }

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            var range = new TextRange(box.Document.ContentStart, box.Document.ContentEnd);

            // SetCurrentValue, not SetValue: SetValue writes a LOCAL value, which outranks a
            // binding and detaches a one-way one. SetCurrentValue changes the effective value
            // and leaves the binding in place - exactly what a two-way assist needs.
            box.SetCurrentValue(PlainTextProperty, range.Text.TrimEnd('\r', '\n'));
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
