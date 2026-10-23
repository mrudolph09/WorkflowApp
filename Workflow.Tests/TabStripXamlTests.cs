using System.IO;
using System.Xml.Linq;

namespace Workflow.Tests;

/// <remarks>
/// Workflow_LOAD_AND_PAUSE spec section 5.2: the <c>Task laden</c> chip at the right end of the tab
/// strip and the chip style it shares with <c>Neuer Task</c>. Structural like
/// <see cref="TaskTabViewXamlTests"/>: elements are located by their identifying attribute, so
/// reformatting the file does not break these tests while a lost binding or column does.
/// </remarks>
public class TabStripXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace MaterialDesign = "http://materialdesigninxaml.net/winfx/xaml/themes";

    private const string ChipStyle = "{StaticResource WorkflowTabStripButtonStyle}";

    private static string SourceRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow"));

    private static XDocument Styles() =>
        XDocument.Load(Path.Combine(SourceRoot(), "Styles", "TabControlStyles.xaml"));

    private static XElement NamedButton(XDocument styles, string name) =>
        Assert.Single(
            styles.Descendants(Presentation + "Button"),
            e => string.Equals((string?)e.Attribute(Xaml + "Name"), name, StringComparison.Ordinal));

    [Fact]
    public void TheLoadTaskButton_RunsTheShellsLoadCommandAndReadsTaskLaden()
    {
        // Arrange
        var button = NamedButton(Styles(), "LoadTaskButton");

        // Act
        var text = button.Descendants(Presentation + "TextBlock").Select(e => (string?)e.Attribute("Text"));
        var icons = button.Descendants(MaterialDesign + "PackIcon").Select(e => (string?)e.Attribute("Kind"));

        // Assert
        Assert.Equal("{Binding LoadTaskCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("Vorhandenen Task-Ordner als Tab öffnen", (string?)button.Attribute("ToolTip"));
        Assert.Contains("Task laden", text);
        Assert.Contains("FolderOpen", icons);
    }

    [Fact]
    public void TheLoadTaskButton_SitsInTheRightHandAutoColumnOfTheTabStrip()
    {
        // Arrange
        var button = NamedButton(Styles(), "LoadTaskButton");
        var strip = button.Parent;

        // Act
        var columns = strip?
            .Element(Presentation + "Grid.ColumnDefinitions")?
            .Elements(Presentation + "ColumnDefinition")
            .Select(e => (string?)e.Attribute("Width"))
            .ToList();

        // Assert
        Assert.NotNull(strip);
        Assert.Equal(Presentation + "Grid", strip.Name);
        Assert.Equal("1", (string?)button.Attribute("Grid.Column"));
        Assert.NotNull(columns);
        Assert.Equal(new[] { "*", "Auto" }, columns);

        // The tab headers stay in the first column, so they can never push the chip out of view.
        var headers = Assert.Single(strip.Descendants(Presentation + "TabPanel"));
        var headerRow = headers.Parent;
        Assert.NotNull(headerRow);
        Assert.Same(strip, headerRow.Parent);
        Assert.Null(headerRow.Attribute("Grid.Column"));
    }

    [Fact]
    public void BothTabStripButtons_ShareOneChipStyleDeclaredBeforeTheTabControlStyle()
    {
        // Arrange
        var styles = Styles();
        var add = NamedButton(styles, "AddTabButton");
        var load = NamedButton(styles, "LoadTaskButton");

        // Act
        var keys = styles.Root?
            .Elements(Presentation + "Style")
            .Select(e => (string?)e.Attribute(Xaml + "Key"))
            .ToList();

        // Assert
        Assert.Equal(ChipStyle, (string?)add.Attribute("Style"));
        Assert.Equal(ChipStyle, (string?)load.Attribute("Style"));
        Assert.Equal("{Binding AddTaskTabCommand}", (string?)add.Attribute("Command"));
        Assert.Contains("Neuer Task", add.Descendants(Presentation + "TextBlock").Select(e => (string?)e.Attribute("Text")));

        // A StaticResource must be declared above its first use in the same dictionary.
        Assert.NotNull(keys);
        var chip = keys.IndexOf("WorkflowTabStripButtonStyle");
        Assert.True(chip >= 0 && chip < keys.IndexOf("WorkflowTabControlStyle"), "The chip style must precede WorkflowTabControlStyle.");
    }

    [Fact]
    public void TheChipStyle_KeepsTheFontOfTheImplicitButtonStyle()
    {
        // Arrange
        var chipStyle = Assert.Single(
            Styles().Root!.Elements(Presentation + "Style"),
            e => string.Equals((string?)e.Attribute(Xaml + "Key"), "WorkflowTabStripButtonStyle", StringComparison.Ordinal));

        // Act
        var setters = chipStyle.Elements(Presentation + "Setter")
            .ToDictionary(
                e => (string)e.Attribute("Property")!,
                e => (string?)e.Attribute("Value"),
                StringComparer.Ordinal);

        // Assert
        // "Neuer Task" used to inherit these from the implicit MaterialDesign Button style.
        Assert.Equal("14", setters.GetValueOrDefault("FontSize"));
        Assert.Equal("Medium", setters.GetValueOrDefault("FontWeight"));
    }
}
