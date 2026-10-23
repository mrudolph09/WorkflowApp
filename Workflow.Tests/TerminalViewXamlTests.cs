using System.IO;
using System.Xml.Linq;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// The gold frame reaches the terminal through one keyed style reference in
/// <c>TerminalView.xaml</c>, which WPF resolves at runtime. A view that lost its frame, a WebView2
/// moved outside it, a lost padding, a swapped visibility converter or the old divider outline
/// creeping back inside the gold would all still compile. These tests read the shipped markup as
/// XML, as <see cref="TaskTabViewXamlTests"/> does for the tab.
/// </para>
/// <para>
/// The frame must change nothing about the terminal itself (SPEC AC7), so the view's element tree
/// is pinned exactly: every element, in order and nesting, with every attribute.
/// </para>
/// </remarks>
public class TerminalViewXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace WebView = "clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf";

    // The view below its root: the resources, then the frame holding the unchanged terminal
    // surface - its padded Border, the WebView2 and the startup error text.
    private static readonly string[] FramedTerminal =
    [
        "UserControl.Resources",
        "  SolidColorBrush Color=#1E1E1E; x:Key=TerminalBackgroundBrush",
        "ContentControl Style={StaticResource TerminalFrameStyle}",
        "  Border Background={StaticResource TerminalBackgroundBrush}; Padding=8",
        "    Grid",
        "      wv2:WebView2 Visibility={Binding IsTerminalAvailable, Converter={StaticResource BooleanToVisibilityConverter}}; x:Name=TerminalWebView",
        "      TextBlock Foreground={DynamicResource MaterialDesignBody}; HorizontalAlignment=Center; Margin=24; Text={Binding TerminalErrorMessage}; "
            + "TextWrapping=Wrap; VerticalAlignment=Center; Visibility={Binding IsTerminalAvailable, Converter={StaticResource InverseBooleanToVisibilityConverter}}",
    ];

    private static string SourceRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow"));

    private static XElement ViewRoot()
    {
        var view = XDocument.Load(Path.Combine(SourceRoot(), "Views", "TerminalView.xaml"));
        Assert.NotNull(view.Root);
        return view.Root;
    }

    // Whitespace inside a markup extension is free; only the tokens matter.
    private static string Squash(string? value) =>
        value is null ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Every element below the root, depth first, as "name attribute=value; ..." with attributes in
    // a fixed order, indented by depth. Comments and whitespace do not count.
    private static List<string> Outline(XElement root)
    {
        var lines = new List<string>();
        foreach (var child in root.Elements())
        {
            Outline(child, 0, lines);
        }

        return lines;
    }

    private static void Outline(XElement element, int depth, List<string> lines)
    {
        var attributes = element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .Select(a => $"{Prefix(a.Name.Namespace)}{a.Name.LocalName}={Squash(a.Value)}")
            .OrderBy(a => a.StartsWith("x:", StringComparison.Ordinal))
            .ThenBy(a => a, StringComparer.Ordinal);
        var line = new string(' ', depth * 2) + Prefix(element.Name.Namespace) + element.Name.LocalName;
        var list = string.Join("; ", attributes);
        lines.Add(list.Length == 0 ? line : line + " " + list);

        foreach (var child in element.Elements())
        {
            Outline(child, depth + 1, lines);
        }
    }

    private static string Prefix(XNamespace ns)
    {
        if (ns == Xaml)
        {
            return "x:";
        }

        return ns == WebView ? "wv2:" : string.Empty;
    }

    [Fact]
    public void TerminalView_Content_IsTheTerminalFrameAroundTheUnchangedTerminal()
    {
        // Arrange
        var root = ViewRoot();

        // Act
        var outline = Outline(root);

        // Assert
        Assert.Equal(FramedTerminal, outline);
    }

    [Theory]
    [InlineData("the frame removed")]
    [InlineData("the padding removed")]
    [InlineData("the background changed")]
    [InlineData("the divider outline back")]
    [InlineData("the terminal visibility inverted")]
    [InlineData("the error text visibility inverted")]
    [InlineData("the terminal moved outside the frame")]
    [InlineData("a second terminal")]
    public void TerminalView_ContentWithOneRegression_IsNotTheFramedTerminal(string regression)
    {
        // Arrange
        var root = ViewRoot();
        var frame = Assert.Single(root.Elements(Presentation + "ContentControl"));
        var border = Assert.Single(frame.Elements(Presentation + "Border"));
        var grid = Assert.Single(border.Elements(Presentation + "Grid"));
        var terminal = Assert.Single(grid.Elements(WebView + "WebView2"));
        var error = Assert.Single(grid.Elements(Presentation + "TextBlock"));

        // Act
        switch (regression)
        {
            case "the frame removed":
                frame.ReplaceWith(border);
                break;
            case "the padding removed":
                border.Attribute("Padding")?.Remove();
                break;
            case "the background changed":
                border.SetAttributeValue("Background", "{DynamicResource MaterialDesignPaper}");
                break;
            case "the divider outline back":
                border.SetAttributeValue("BorderThickness", "1");
                border.SetAttributeValue("BorderBrush", "{DynamicResource MaterialDesignDivider}");
                break;
            case "the terminal visibility inverted":
                terminal.SetAttributeValue("Visibility", "{Binding IsTerminalAvailable, Converter={StaticResource InverseBooleanToVisibilityConverter}}");
                break;
            case "the error text visibility inverted":
                error.SetAttributeValue("Visibility", "{Binding IsTerminalAvailable, Converter={StaticResource BooleanToVisibilityConverter}}");
                break;
            case "the terminal moved outside the frame":
                terminal.Remove();
                root.Add(terminal);
                break;
            case "a second terminal":
                grid.AddFirst(new XElement(terminal));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(regression), regression, "Unknown regression.");
        }

        // Assert
        Assert.NotEqual(FramedTerminal, Outline(root));
    }

    [Fact]
    public void TerminalView_DividerOutline_IsGone()
    {
        // Arrange
        var root = ViewRoot();

        // Act - the gold frame's inner border replaces the old 1 px outline; both together would
        // draw a grey line inside the gold one.
        var outlined = root.DescendantsAndSelf()
            .Where(e => Squash(e.Attribute("BorderBrush")?.Value) == "{DynamicResource MaterialDesignDivider}")
            .ToList();

        // Assert
        Assert.Empty(outlined);
    }
}
