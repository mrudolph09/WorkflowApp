using System.IO;
using System.Xml.Linq;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 6.3 owns the placement and the binding surface of the subtask controls in
/// <c>TaskTabView.xaml</c>. A binding is resolved at runtime, so a wrong or missing one compiles
/// perfectly cleanly and only shows up as a dead control in the running application - the same gap
/// <see cref="AppResourceTests"/> closes for pack URIs. These tests read the shipped markup as XML
/// and assert the structure requirements 1.1, 1.2, 1.3, 1.6 and 3.1 ask for.
/// </para>
/// <para>
/// The tests are structural rather than textual: they locate elements by their identifying
/// attribute and then assert the other attributes on that element, so reformatting the file does
/// not break them while a control losing its lock or its visibility does.
/// </para>
/// </remarks>
public class TaskTabViewXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Views = "clr-namespace:Workflow.Views";
    private static readonly XNamespace MaterialDesign = "http://materialdesigninxaml.net/winfx/xaml/themes";

    private const string EditableLock = "{Binding IsSubtaskConfigurationEditable}";
    private const string SubtaskVisibility =
        "{Binding SubtasksEnabled, Converter={StaticResource BooleanToVisibilityConverter}}";

    private static string SourceRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow"));

    private static XDocument View() =>
        XDocument.Load(Path.Combine(SourceRoot(), "Views", "TaskTabView.xaml"));

    private static string? Attribute(XElement element, XName name) => element.Attribute(name)?.Value;

    // Whitespace inside a markup extension is free; only the tokens matter.
    private static string Squash(string? value) =>
        value is null ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AssertBinding(string expected, string? actual, string what) =>
        Assert.True(
            string.Equals(Squash(expected), Squash(actual), StringComparison.Ordinal),
            $"{what}: expected '{expected}', found '{actual ?? "<none>"}'.");

    private static XElement Single(XDocument view, XName element, Func<XElement, bool> predicate, string what)
    {
        var matches = view.Descendants(element).Where(predicate).ToList();
        Assert.True(matches.Count == 1, $"Expected exactly one {what} in TaskTabView.xaml, found {matches.Count}.");
        return matches[0];
    }

    private static XElement SubtaskCheckBox(XDocument view) =>
        Single(view, Presentation + "CheckBox", e => Attribute(e, "Content") == "Subtasks", "'Subtasks' CheckBox");

    private static XElement TrackingComboBox(XDocument view) =>
        Single(
            view,
            Presentation + "ComboBox",
            e => Attribute(e, MaterialDesign + "HintAssist.Hint") == "Workflow-Verzeichnis",
            "'Workflow-Verzeichnis' ComboBox");

    private static XElement TrackingPickerButton(XDocument view) =>
        Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding BrowseWorkflowDirectoryCommand}",
            "tracking-directory folder-picker Button");

    private static XElement IndicatorView(XDocument view) =>
        Single(view, Views + "SubtaskIndicatorView", _ => true, "SubtaskIndicatorView");

    // Requirement 1.1
    [Fact]
    public void TheSubtaskCheckBox_CarriesTheSpecifiedGermanTooltipAndIsBoundTwoWay()
    {
        var checkBox = SubtaskCheckBox(View());

        Assert.Equal(
            "Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks",
            Attribute(checkBox, "ToolTip"));

        AssertBinding("{Binding SubtasksEnabled, Mode=TwoWay}", Attribute(checkBox, "IsChecked"), "CheckBox.IsChecked");
    }

    // Requirement 1.1: 'beside Implementierung' - the checkbox must share the phase indicators'
    // wrapping panel rather than sit in its own row somewhere else on the tab.
    [Fact]
    public void TheSubtaskCheckBox_SitsBesideThePhaseIndicators()
    {
        var view = View();
        var checkBox = SubtaskCheckBox(view);

        var phases = Single(
            view,
            Presentation + "ItemsControl",
            e => Attribute(e, "ItemsSource") == "{Binding Phases}",
            "phase ItemsControl");

        Assert.Same(phases.Parent, checkBox.Parent);
        Assert.Equal(Presentation + "WrapPanel", checkBox.Parent!.Name);
    }

    // Requirement 3.1: this view owns the indicator's placement and visibility; task 6.1 owns its
    // content. The indicator sits in the same panel as the phase indicators.
    [Fact]
    public void TheSubtaskIndicator_IsPlacedBesideThePhaseIndicatorsAndShownOnlyInSubtaskMode()
    {
        var view = View();
        var indicator = IndicatorView(view);

        AssertBinding(
            "{Binding SubtaskIndicator}",
            Attribute(indicator, "DataContext"),
            "SubtaskIndicatorView.DataContext");

        // The wrapper keeps the tab's DataContext for the Visibility binding while the inner view
        // receives the indicator view model; binding both on one element is not possible.
        var host = indicator.Parent;
        Assert.NotNull(host);
        AssertBinding(SubtaskVisibility, Attribute(host, "Visibility"), "subtask indicator host Visibility");

        var phases = Single(
            view,
            Presentation + "ItemsControl",
            e => Attribute(e, "ItemsSource") == "{Binding Phases}",
            "phase ItemsControl");

        Assert.Same(phases.Parent, host.Parent);
    }

    // Requirement 1.2
    [Fact]
    public void TheTrackingDirectoryRow_IsBoundToItsOwnHistoryAndShownOnlyInSubtaskMode()
    {
        var view = View();
        var combo = TrackingComboBox(view);

        AssertBinding(
            "{Binding RecentWorkflowDirectories}",
            Attribute(combo, "ItemsSource"),
            "tracking ComboBox.ItemsSource");

        AssertBinding(
            "{Binding WorkflowDirectory, Mode=TwoWay}",
            Attribute(combo, "SelectedItem"),
            "tracking ComboBox.SelectedItem");

        var row = combo.Parent;
        Assert.NotNull(row);
        AssertBinding(SubtaskVisibility, Attribute(row, "Visibility"), "tracking row Visibility");

        // The folder picker belongs to the same row, so hiding the row hides both controls.
        Assert.Same(row, TrackingPickerButton(view).Parent);

        // Task 6.2's title parameter exists for exactly this caption.
        Assert.Equal("Workflow-Verzeichnis auswählen", Attribute(TrackingPickerButton(view), "ToolTip"));
    }

    // Requirement 1.3: the blocking message has its own surface and is not written into the
    // working-directory message, which SyncFolder overwrites on every keystroke.
    [Fact]
    public void TheTrackingDirectoryMessage_HasItsOwnErrorTextBlock()
    {
        var view = View();

        var message = Single(
            view,
            Presentation + "TextBlock",
            e => Attribute(e, "Text") == "{Binding WorkflowDirectoryMessage}",
            "WorkflowDirectoryMessage TextBlock");

        Assert.Equal(
            "{DynamicResource MaterialDesignValidationErrorBrush}",
            Attribute(message, "Foreground"));

        AssertBinding(SubtaskVisibility, Attribute(message, "Visibility"), "tracking message Visibility");
    }

    // Requirement 1.6 and design "Resolved Decisions" issue 3: the lock covers every input,
    // including the folder-picker button.
    [Fact]
    public void EverySubtaskInput_IsLockedByTheSamePhaseFourFlag()
    {
        var view = View();

        AssertBinding(EditableLock, Attribute(SubtaskCheckBox(view), "IsEnabled"), "CheckBox.IsEnabled");
        AssertBinding(EditableLock, Attribute(TrackingComboBox(view), "IsEnabled"), "tracking ComboBox.IsEnabled");
        AssertBinding(EditableLock, Attribute(TrackingPickerButton(view), "IsEnabled"), "picker Button.IsEnabled");
    }

    // Design, 'Presentation and Recovery': reuse the registered converters, add none.
    [Fact]
    public void TheSubtaskBindings_AddNoConverter()
    {
        var app = File.ReadAllText(Path.Combine(SourceRoot(), "App.xaml"));

        var declared = app
            .Split('\n')
            .Where(line => line.Contains("x:Key=\"", StringComparison.Ordinal)
                        && line.Contains("Converter", StringComparison.Ordinal))
            .Count();

        Assert.Equal(4, declared);
    }
}
