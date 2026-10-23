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

    // --- Pause and Play (Workflow_LOAD_AND_PAUSE spec section 6.5) ----------------------------

    private static XElement PauseButton(XDocument view) =>
        Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding PauseCommand}",
            "Pause Button");

    private static XElement PlayButton(XDocument view) =>
        Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding UnpauseCommand}",
            "Play Button");

    // 'neben der Subtasks-Checkbox': both buttons share one host that directly follows the checkbox
    // inside the bottom bar's WrapPanel, Pause first.
    [Fact]
    public void ThePauseControls_SitDirectlyAfterTheSubtaskCheckBox()
    {
        // Arrange
        var view = View();
        var checkBox = SubtaskCheckBox(view);
        var pause = PauseButton(view);
        var play = PlayButton(view);

        // Act
        var host = pause.Parent;

        // Assert
        Assert.NotNull(host);
        Assert.Same(host, play.Parent);
        Assert.Same(checkBox.Parent, host.Parent);
        Assert.Same(host, checkBox.ElementsAfterSelf().First());
        Assert.Same(play, pause.ElementsAfterSelf().First());
    }

    [Fact]
    public void ThePlayButton_IsShownOnlyWhilePausedAndThePauseButtonAlways()
    {
        // Arrange
        var view = View();

        // Act
        var playVisibility = Attribute(PlayButton(view), "Visibility");
        var pauseVisibility = Attribute(PauseButton(view), "Visibility");

        // Assert
        AssertBinding(
            "{Binding IsPaused, Converter={StaticResource BooleanToVisibilityConverter}}",
            playVisibility,
            "Play Button.Visibility");
        Assert.Null(pauseVisibility);
    }

    [Fact]
    public void ThePauseControls_CarryTheirIconsAndGermanTooltips()
    {
        // Arrange
        var view = View();
        var pause = PauseButton(view);
        var play = PlayButton(view);

        // Act
        var pauseIcon = Assert.Single(pause.Descendants(MaterialDesign + "PackIcon"));
        var playIcon = Assert.Single(play.Descendants(MaterialDesign + "PackIcon"));

        // Assert
        Assert.Equal("Pause", Attribute(pauseIcon, "Kind"));
        Assert.Equal("Play", Attribute(playIcon, "Kind"));
        Assert.Equal(
            "Pausieren: Die laufende Phase bzw. der laufende Subtask läuft zu Ende, der nächste Schritt startet erst nach Fortsetzen",
            Attribute(pause, "ToolTip"));
        Assert.Equal("Fortsetzen: Der nächste Schritt startet wieder automatisch", Attribute(play, "ToolTip"));

        // Pause is disabled while paused; its tooltip must still explain the state.
        Assert.Equal("True", Attribute(pause, "ToolTipService.ShowOnDisabled"));
    }

    // Once phase 4 has written its done file there is nothing to continue: the Start button gives
    // way to a 'Task abschliessen' button in its place, and the manual-override button of the same
    // name steps aside so the bar does not show the caption twice.
    [Fact]
    public void AFinishedTask_SwapsTheStartButtonForTaskAbschliessen()
    {
        // Arrange
        var view = View();
        var start = Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding StartWorkflowCommand}",
            "Start Button");
        var finish = Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding CloseCommand}",
            "finished-task Button");
        var completeTask = Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding CompleteTaskCommand}",
            "manual-override Button");

        // Act
        var caption = Assert.Single(finish.Descendants(Presentation + "TextBlock"));

        // Assert
        Assert.Equal("Task abschliessen", Attribute(caption, "Text"));
        Assert.Equal(Attribute(start, "DockPanel.Dock"), Attribute(finish, "DockPanel.Dock"));
        AssertBinding(
            "{Binding IsTaskFinished, Converter={StaticResource InverseBooleanToVisibilityConverter}}",
            Attribute(start, "Visibility"),
            "Start Button.Visibility");
        AssertBinding(
            "{Binding IsTaskFinished, Converter={StaticResource BooleanToVisibilityConverter}}",
            Attribute(finish, "Visibility"),
            "finished-task Button.Visibility");
        AssertBinding(
            "{Binding IsTaskFinished, Converter={StaticResource InverseBooleanToVisibilityConverter}}",
            Attribute(completeTask, "Visibility"),
            "manual-override Button.Visibility");
    }

    // WF_QUEUE spec 4.5: the Queue box sits directly right of Start - declared right after the
    // finished-task button and docked left - and hides with Start once the task is finished.
    [Fact]
    public void TheQueueComboBox_SitsRightOfStartAndHidesWithIt()
    {
        // Arrange
        var view = View();
        var finish = Single(view, Presentation + "Button", e => Attribute(e, "Command") == "{Binding CloseCommand}", "finished-task Button");
        var queue = Single(view, Presentation + "ComboBox", e => Attribute(e, "ItemsSource") == "{Binding QueueCandidates}", "Queue ComboBox");

        // Act
        var next = finish.ElementsAfterSelf().First();

        // Assert
        Assert.Same(queue, next);
        Assert.Equal("Left", Attribute(queue, "DockPanel.Dock"));
        Assert.Equal("Queue", Attribute(queue, MaterialDesign + "HintAssist.Hint"));
        Assert.Equal("True", Attribute(queue, MaterialDesign + "TextFieldAssist.HasClearButton"));
        Assert.Equal("Header", Attribute(queue, "DisplayMemberPath"));
        Assert.Equal("False", Attribute(queue, "IsEditable"));
        Assert.Equal("{StaticResource MaterialDesignOutlinedComboBox}", Attribute(queue, "Style"));
        AssertBinding("{Binding QueuedAfter, Mode=TwoWay}", Attribute(queue, "SelectedItem"), "Queue ComboBox.SelectedItem");
        AssertBinding("{Binding IsQueueEditable}", Attribute(queue, "IsEnabled"), "Queue ComboBox.IsEnabled");
        AssertBinding(
            "{Binding IsTaskFinished, Converter={StaticResource InverseBooleanToVisibilityConverter}}",
            Attribute(queue, "Visibility"),
            "Queue ComboBox.Visibility");
        Assert.Equal("True", Attribute(queue, "ToolTipService.ShowOnDisabled"));
        Assert.Equal(
            "Startet den Workflow dieses Tasks automatisch, sobald der gewählte Task fertig ist (Implementierung grün). Zur Auswahl stehen die anderen geöffneten Tasks, die noch nicht fertig sind.",
            Attribute(queue, "ToolTip"));
    }

    // Workflow_LoadFix spec 8.5: 'Implementierung beginnen' sits directly beside the manual-override
    // 'Task abschliessen' (declared after it, so it docks to its left) and steps aside with it once
    // the task is finished.
    [Fact]
    public void TheStartImplementationButton_SitsBesideTaskAbschliessenAndHidesWithIt()
    {
        // Arrange
        var view = View();
        var completeTask = Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding CompleteTaskCommand}",
            "manual-override Button");
        var startImplementation = Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding StartImplementationCommand}",
            "'Implementierung beginnen' Button");

        // Act
        var next = completeTask.ElementsAfterSelf().First();

        // Assert
        Assert.Same(startImplementation, next);
        Assert.Equal("Implementierung beginnen", Attribute(startImplementation, "Content"));
        Assert.Equal("Right", Attribute(startImplementation, "DockPanel.Dock"));
        Assert.Equal("{StaticResource MaterialDesignRaisedButton}", Attribute(startImplementation, "Style"));
        AssertBinding(
            "{Binding IsTaskFinished, Converter={StaticResource InverseBooleanToVisibilityConverter}}",
            Attribute(startImplementation, "Visibility"),
            "'Implementierung beginnen' Button.Visibility");
        Assert.Equal("True", Attribute(startImplementation, "ToolTipService.ShowOnDisabled"));
        Assert.Equal(
            "Vereinfachen, Review und Review umsetzen überspringen und sofort mit der Implementierung beginnen. Eine laufende Phase wird abgebrochen. Mit Subtasks, wenn 'Subtasks' ausgewählt ist.",
            Attribute(startImplementation, "ToolTip"));
    }

    // --- Desktop-Job jetzt starten (WF_DESKTOPJOBBUTTON spec section 4.7) ---

    [Fact]
    public void TheStartDesktopJobNowButton_SitsAfterTheSubtaskIndicatorInItsSubtaskOnlyWrapper()
    {
        // Arrange
        var view = View();
        var indicator = IndicatorView(view);
        var button = Single(
            view,
            Presentation + "Button",
            e => Attribute(e, "Command") == "{Binding StartDesktopJobNowCommand}",
            "'Desktop-Job jetzt starten' Button");

        // Act
        var next = indicator.ElementsAfterSelf().First();

        // Assert
        Assert.Same(button, next);
        Assert.Equal("Desktop-Job jetzt starten", Attribute(button, "Content"));
        Assert.Equal("{StaticResource MaterialDesignOutlinedButton}", Attribute(button, "Style"));
        Assert.Equal("True", Attribute(button, "ToolTipService.ShowOnDisabled"));
        Assert.Equal(
            "Startet den wartenden Desktop-Job sofort, ohne die Leerlaufzeit abzuwarten. Danach Maus und Tastatur nicht berühren: jede Maus- oder Tastatureingabe und jede Sperre des Arbeitsplatzes bricht den Job ab. Nur aktiv, solange ein Desktop-Job auf Leerlauf wartet und nicht pausiert ist.",
            Attribute(button, "ToolTip"));
        Assert.Null(Attribute(button, "Visibility"));
        AssertBinding(
            "{Binding SubtasksEnabled, Converter={StaticResource BooleanToVisibilityConverter}}",
            Attribute(indicator.Parent!, "Visibility"),
            "subtask wrapper Visibility");
    }

    // --- Open gate items: the icon beside Implementierung and its overview ---------------------

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement OpenGateItemsToggle(XDocument view) =>
        Single(
            view,
            Presentation + "ToggleButton",
            e => Attribute(e, Xaml + "Name") == "OpenGateItemsToggle",
            "open-gate-items ToggleButton");

    [Fact]
    public void TheOpenGateItemsToggle_FollowsThePhaseIndicatorsAndShowsOnlyWithItems()
    {
        // Arrange
        var view = View();
        var phases = Single(
            view,
            Presentation + "ItemsControl",
            e => Attribute(e, "ItemsSource") == "{Binding Phases}",
            "phase ItemsControl");
        var toggle = OpenGateItemsToggle(view);

        // Act
        var next = phases.ElementsAfterSelf().First();

        // Assert - Implementierung is the last phase, so the element right after the phases sits beside it.
        Assert.Same(toggle.Parent, next);
        AssertBinding(
            "{Binding HasOpenGateItems, Converter={StaticResource BooleanToVisibilityConverter}}",
            Attribute(next, "Visibility"),
            "open-gate-items wrapper Visibility");
        AssertBinding("{Binding RefreshOpenGateItemsOnOpenCommand}", Attribute(toggle, "Command"), "ToggleButton.Command");
    }

    [Fact]
    public void TheOpenGateItemsPopup_IsTiedToTheToggleAndShowsReadOnlyCopyableText()
    {
        // Arrange
        var view = View();

        // Act
        var popup = Single(view, Presentation + "Popup", e => Attribute(e, Xaml + "Name") == "OpenGateItemsPopup", "open-gate-items Popup");
        var text = Single(view, Presentation + "TextBox", e => Attribute(e, Xaml + "Name") == "OpenGateItemsBox", "open-gate-items TextBox");
        var copy = Single(view, Presentation + "Button", e => Attribute(e, "Click") == "CopyOpenGateItems_Click", "'Alles kopieren' Button");

        // Assert
        AssertBinding("{Binding IsChecked, ElementName=OpenGateItemsToggle, Mode=TwoWay}", Attribute(popup, "IsOpen"), "Popup.IsOpen");
        Assert.Equal("False", Attribute(popup, "StaysOpen"));
        AssertBinding("{Binding OpenGateItemsText, Mode=OneWay}", Attribute(text, "Text"), "TextBox.Text");
        Assert.Equal("True", Attribute(text, "IsReadOnly"));
        Assert.Contains(copy.Descendants(Presentation + "TextBlock"), t => Attribute(t, "Text") == "Alles kopieren");
        Assert.Contains(popup.Descendants(), e => e == text);
        Assert.Contains(popup.Descendants(), e => e == copy);
    }
}
