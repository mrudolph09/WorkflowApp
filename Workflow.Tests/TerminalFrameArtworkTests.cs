using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// <c>Styles/TerminalFrameStyles.xaml</c> is a hand translation of the supplied artwork,
/// <c>Assets/terminal-frame.svg</c>, whose SHA-256 the specification pins. A slip in that
/// translation - a dropped ornament, one leaf too few, a wrong colour stop, a lost mirror - still
/// parses and still renders gold, so neither the layout nor the pixel tests notice it. These tests
/// read both files as XML and compare them shape by shape under the specification's translation
/// rules (section 6.2), and they check that the style cuts the drawing exactly where the
/// specification says (section 6.4).
/// </para>
/// <para>
/// Each shape is reduced to one line - geometry, fill, stroke, placement - built the same way from
/// the SVG and from the XAML, so a difference names the shape and the property that differs.
/// </para>
/// </remarks>
public partial class TerminalFrameArtworkTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The one shape the translation adds (SPEC 6.2): the invisible rectangle that pins the bounds.
    private const string BackingRectangle = "rect 0,0,1000,700 0 0 | fill #00FFFFFF | stroke none | at 1,0,0,1,0,0";

    private static string SourceRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow"));

    private static XDocument Artwork() => XDocument.Load(Path.Combine(SourceRoot(), "Assets", "terminal-frame.svg"));

    private static XDocument Translation() => XDocument.Load(Path.Combine(SourceRoot(), "Styles", "TerminalFrameStyles.xaml"));

    [Fact]
    public void TerminalFrameDrawing_ComparedWithTheArtwork_TranslatesEveryShapeAndGradient()
    {
        // Arrange
        var artwork = Artwork();
        var translation = Translation();

        // Act
        var differences = Differences(artwork, translation);

        // Assert - the SVG really was read (43 shapes, 4 gradients), and the XAML matches it.
        Assert.Equal(43, ArtworkShapes(artwork).Count);
        Assert.Equal(4, ArtworkGradients(artwork).Count);
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(8)));
    }

    [Theory]
    [InlineData("the bottom centre ornament missing")]
    [InlineData("a side ornament missing")]
    [InlineData("a leaf missing")]
    [InlineData("a corner ornament not mirrored")]
    [InlineData("a gradient stop recoloured")]
    [InlineData("a fill rule lost")]
    [InlineData("a miter limit lost")]
    [InlineData("a stroke opacity lost")]
    public void TerminalFrameDrawing_TranslationWithOneSlip_DiffersFromTheArtwork(string slip)
    {
        // Arrange
        var translation = Translation();
        Introduce(slip, translation);

        // Act
        var differences = Differences(Artwork(), translation);

        // Assert
        Assert.NotEmpty(differences);
    }

    [Fact]
    public void TerminalFrameStyle_Template_CutsTheDrawingWhereTheSpecificationSays()
    {
        // Arrange
        var template = Assert.Single(Translation().Descendants(Presentation + "ControlTemplate"));

        // Act
        var layout = DescribeTemplate(template);

        // Assert - SPEC 6.3/6.4: the root grid, then each band with its sizes and its slices
        // (SVG x,y,width,height), then the opening. Every fixed size is its SVG length / 4.
        Assert.Equal(
            new[]
            {
                "Grid UseLayoutRounding=True | columns 44,*,44 | rows 34,*,34",
                "Grid Grid.ColumnSpan=3; Grid.Row=0; IsHitTestVisible=False | columns 65,*,60,*,65 | slices 0,0,260,136 260,0,120,136 380,0,240,136 620,0,120,136 740,0,260,136",
                "Grid Grid.ColumnSpan=3; Grid.Row=2; IsHitTestVisible=False | columns 65,*,60,*,65 | slices 0,564,260,136 260,564,120,136 380,564,240,136 620,564,120,136 740,564,260,136",
                "Grid Grid.Column=0; Grid.Row=1; IsHitTestVisible=False | rows *,65,* | slices 0,136,176,84 0,220,176,260 0,480,176,84",
                "Grid Grid.Column=2; Grid.Row=1; IsHitTestVisible=False | rows *,65,* | slices 824,136,176,84 824,220,176,260 824,480,176,84",
                "ContentPresenter Grid.Column=1; Grid.Row=1",
            },
            layout);
    }

    private static List<string> Differences(XDocument artwork, XDocument translation)
    {
        var differences = new List<string>();
        Compare("gradient", ArtworkGradients(artwork), TranslationGradients(translation), differences);
        Compare("shape", [BackingRectangle, .. ArtworkShapes(artwork)], TranslationShapes(translation), differences);
        return differences;
    }

    private static void Compare(string what, List<string> expected, List<string> actual, List<string> differences)
    {
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var svg = i < expected.Count ? expected[i] : "<none>";
            var xaml = i < actual.Count ? actual[i] : "<none>";
            if (!string.Equals(svg, xaml, StringComparison.Ordinal))
            {
                differences.Add($"{what} {i}: SVG '{svg}' / XAML '{xaml}'");
            }
        }
    }

    // ---- The SVG -----------------------------------------------------------------------------

    private static List<string> ArtworkShapes(XDocument artwork)
    {
        var shapes = new List<string>();
        foreach (var element in artwork.Descendants().Where(e => !e.Ancestors(Svg + "defs").Any()))
        {
            var geometry = element.Name.LocalName switch
            {
                "path" => "path " + Squash(element.Attribute("d")?.Value),
                "rect" => $"rect {Number(element, "x")},{Number(element, "y")},{Number(element, "width")},{Number(element, "height")} "
                    + $"{Number(element, "rx", element.Attribute("ry")?.Value ?? "0")} {Number(element, "ry", element.Attribute("rx")?.Value ?? "0")}",
                "ellipse" => $"ellipse {Number(element, "cx")},{Number(element, "cy")} {Number(element, "rx")} {Number(element, "ry")}",
                _ => null,
            };
            if (geometry is null)
            {
                continue;
            }

            var opacity = double.Parse(element.Attribute("opacity")?.Value ?? "1", CultureInfo.InvariantCulture);
            var fill = SvgPaint(Inherited(element, "fill") ?? "#000000", opacity);
            var stroke = SvgPaint(Inherited(element, "stroke") ?? "none", opacity);
            var width = Inherited(element, "stroke-width") ?? "1";
            var miter = Inherited(element, "stroke-miterlimit") ?? "4";
            shapes.Add(Describe(geometry, fill, stroke, width, miter, SvgTransform(element)));
        }

        return shapes;
    }

    private static List<string> ArtworkGradients(XDocument artwork) =>
        artwork.Descendants(Svg + "defs").Elements()
            .Select(g => $"{g.Name.LocalName} {g.Attribute("id")?.Value} "
                + (g.Name.LocalName == "linearGradient"
                    ? $"{Fraction(g.Attribute("x1")?.Value ?? "0")},{Fraction(g.Attribute("y1")?.Value ?? "0")} {Fraction(g.Attribute("x2")?.Value ?? "1")},{Fraction(g.Attribute("y2")?.Value ?? "0")}"
                    : string.Join(" ", g.Attributes().Where(a => a.Name.LocalName != "id").Select(a => $"{a.Name.LocalName}={a.Value}")))
                + " | " + string.Join(" ", g.Elements(Svg + "stop").Select(s => $"{Fraction(s.Attribute("offset")?.Value ?? "0")} {Colour(s.Attribute("stop-color")?.Value ?? "#000000", 1)}")))
            .ToList();

    // SVG presentation attributes are inherited from the enclosing <g> (the leaves use that).
    private static string? Inherited(XElement element, string attribute) =>
        element.AncestorsAndSelf().Select(e => e.Attribute(attribute)?.Value).FirstOrDefault(v => v is not null);

    private static string SvgPaint(string paint, double opacity)
    {
        if (paint == "none")
        {
            return "none";
        }

        var gradient = GradientReference().Match(paint);
        if (gradient.Success)
        {
            return opacity == 1 ? "gradient " + gradient.Groups[1].Value : $"gradient {gradient.Groups[1].Value} at opacity {opacity}";
        }

        // Opacity on a single paint is folded into its alpha (SPEC 6.2).
        return Colour(paint, opacity);
    }

    // SVG applies the rightmost operation first; nested groups apply the innermost first.
    private static string SvgTransform(XElement element)
    {
        var matrix = Matrix.Identity;
        foreach (var group in element.Ancestors(Svg + "g"))
        {
            var operations = TransformOperation().Matches(group.Attribute("transform")?.Value ?? string.Empty);
            for (var i = operations.Count - 1; i >= 0; i--)
            {
                var values = operations[i].Groups[2].Value
                    .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
                    .ToArray();
                var operation = operations[i].Groups[1].Value == "translate"
                    ? new Matrix(1, 0, 0, 1, values[0], values.Length > 1 ? values[1] : 0)
                    : new Matrix(values[0], 0, 0, values.Length > 1 ? values[1] : values[0], 0, 0);
                matrix *= operation;
            }
        }

        return Describe(matrix);
    }

    // ---- The XAML ----------------------------------------------------------------------------

    private static List<string> TranslationShapes(XDocument translation)
    {
        var keyed = Keyed(translation);
        var shapes = new List<string>();
        Collect(keyed["TerminalFrameDrawing"], Matrix.Identity, keyed, shapes);
        return shapes;
    }

    private static List<string> TranslationGradients(XDocument translation)
    {
        Assert.NotNull(translation.Root);
        return translation.Root.Elements()
            .Where(e => e.Name.LocalName.EndsWith("GradientBrush", StringComparison.Ordinal))
            .Select(g => (g.Name.LocalName == "LinearGradientBrush" ? "linearGradient " : "radialGradient ")
                + GradientId(g.Attribute(Xaml + "Key")?.Value) + " "
                + (g.Name.LocalName == "LinearGradientBrush"
                    ? $"{PointOf(g.Attribute("StartPoint")?.Value ?? "0,0")} {PointOf(g.Attribute("EndPoint")?.Value ?? "1,1")}"
                    : string.Join(" ", g.Attributes().Where(a => a.Name != Xaml + "Key").Select(a => $"{a.Name.LocalName}={a.Value}")))
                + " | " + string.Join(" ", g.Elements(Presentation + "GradientStop").Select(s => $"{Fraction(s.Attribute("Offset")?.Value ?? "0")} {Colour(s.Attribute("Color")?.Value ?? "#000000", 1)}")))
            .ToList();
    }

    private static Dictionary<string, XElement> Keyed(XDocument translation)
    {
        Assert.NotNull(translation.Root);
        return translation.Root.Elements()
            .Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => e.Attribute(Xaml + "Key")!.Value, StringComparer.Ordinal); // filtered to elements that have a key just above
    }

    private static void Collect(XElement drawing, Matrix transform, Dictionary<string, XElement> keyed, List<string> shapes)
    {
        switch (drawing.Name.LocalName)
        {
            case "GeometryDrawing":
                shapes.Add(DescribeGeometryDrawing(drawing, transform));
                break;
            case "StaticResource":
                Collect(keyed[drawing.Attribute("ResourceKey")?.Value ?? string.Empty], transform, keyed, shapes);
                break;
            case "DrawingGroup":
                var own = drawing.Attribute("Transform") is { } attribute ? Matrix.Parse(attribute.Value) : Matrix.Identity;
                var children = drawing.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
                    .Concat(drawing.Elements(Presentation + "DrawingGroup.Children").Elements(Presentation + "DrawingCollection").Elements());
                foreach (var child in children)
                {
                    // The group's own transform applies before any outer one.
                    Collect(child, own * transform, keyed, shapes);
                }

                break;
            default:
                shapes.Add("unexpected " + drawing.Name.LocalName);
                break;
        }
    }

    private static string DescribeGeometryDrawing(XElement drawing, Matrix transform)
    {
        var fill = XamlPaint(drawing.Attribute("Brush")?.Value);
        var geometry = drawing.Attribute("Geometry")?.Value;
        string shape;
        if (geometry is not null)
        {
            // F1 selects nonzero filling, SVG's default; without it WPF fills even-odd.
            var nonzero = geometry.StartsWith("F1 ", StringComparison.Ordinal);
            shape = "path " + Squash(nonzero ? geometry[3..] : geometry);
        }
        else
        {
            var element = drawing.Element(Presentation + "GeometryDrawing.Geometry")?.Elements().FirstOrDefault();
            shape = element?.Name.LocalName switch
            {
                "EllipseGeometry" => $"ellipse {PointOf(element.Attribute("Center")?.Value ?? "0,0")} {Number(element, "RadiusX")} {Number(element, "RadiusY")}",
                "RectangleGeometry" => $"rect {RectOf(element.Attribute("Rect")?.Value ?? string.Empty)} {Number(element, "RadiusX", "0")} {Number(element, "RadiusY", "0")}",
                _ => "unexpected geometry " + element?.Name.LocalName,
            };
        }

        var pen = drawing.Element(Presentation + "GeometryDrawing.Pen")?.Element(Presentation + "Pen");
        return Describe(
            shape,
            fill,
            pen is null ? "none" : XamlPaint(pen.Attribute("Brush")?.Value),
            pen?.Attribute("Thickness")?.Value ?? "1",
            pen?.Attribute("MiterLimit")?.Value ?? "10",
            Describe(transform))
            + (fill != "none" && geometry is not null && !geometry.StartsWith("F1 ", StringComparison.Ordinal) ? " | even-odd" : string.Empty);
    }

    private static string XamlPaint(string? brush)
    {
        if (brush is null)
        {
            return "none";
        }

        var key = ResourceReference().Match(brush);
        return key.Success ? "gradient " + GradientId(key.Groups[1].Value) : Colour(brush, 1);
    }

    // TerminalFrameGoldHorizontalBrush <-> goldHorizontal (SPEC 6.2).
    private static string GradientId(string? key)
    {
        var name = (key ?? string.Empty).Replace("TerminalFrame", string.Empty, StringComparison.Ordinal).Replace("Brush", string.Empty, StringComparison.Ordinal);
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    // ---- The template ------------------------------------------------------------------------

    private static List<string> DescribeTemplate(XElement template)
    {
        var root = Assert.Single(Content(template));
        var lines = new List<string> { DescribeGrid(root, slices: false) };
        lines.AddRange(Content(root).Select(e => e.Name.LocalName == "Grid" ? DescribeGrid(e, slices: true) : Attributes(e)));
        return lines;
    }

    private static string DescribeGrid(XElement grid, bool slices)
    {
        var line = Attributes(grid);
        var columns = grid.Element(Presentation + "Grid.ColumnDefinitions")?.Elements().Select(c => c.Attribute("Width")?.Value ?? "*").ToList();
        var rows = grid.Element(Presentation + "Grid.RowDefinitions")?.Elements().Select(r => r.Attribute("Height")?.Value ?? "*").ToList();
        if (columns is not null)
        {
            line += " | columns " + string.Join(",", columns);
        }

        if (rows is not null)
        {
            line += " | rows " + string.Join(",", rows);
        }

        return slices ? line + " | slices " + string.Join(" ", Content(grid).Select(Slice)) : line;
    }

    // A slice is a Rectangle that carries nothing but its grid position and a DrawingBrush of the
    // whole drawing, stretched over an absolute viewbox. Anything else is spelled out, so it fails.
    private static string Slice(XElement rectangle)
    {
        var brush = rectangle.Element(Presentation + "Rectangle.Fill")?.Element(Presentation + "DrawingBrush");
        var position = rectangle.Attributes().All(a => a.Name.LocalName is "Grid.Column" or "Grid.Row");
        var settings = brush is null
            ? "no brush"
            : $"{Squash(brush.Attribute("Drawing")?.Value)} {brush.Attribute("ViewboxUnits")?.Value} {brush.Attribute("Stretch")?.Value}";
        return rectangle.Name.LocalName == "Rectangle" && position && settings == "{StaticResource TerminalFrameDrawing} Absolute Fill"
            ? brush!.Attribute("Viewbox")?.Value ?? "no viewbox" // brush is not null: settings would read "no brush"
            : $"[{Attributes(rectangle)} {settings}]";
    }

    private static IEnumerable<XElement> Content(XElement element) =>
        element.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));

    private static string Attributes(XElement element)
    {
        var attributes = element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .Select(a => $"{a.Name.LocalName}={Squash(a.Value)}");
        var list = string.Join("; ", attributes);
        return list.Length == 0 ? element.Name.LocalName : element.Name.LocalName + " " + list;
    }

    // ---- Mutations for the negative test -----------------------------------------------------

    private static void Introduce(string slip, XDocument translation)
    {
        XElement Only(Func<XElement, bool> predicate)
        {
            var matches = translation.Descendants().Where(predicate).ToList();
            Assert.True(matches.Count == 1, $"'{slip}': expected one element to change, found {matches.Count}.");
            return matches[0];
        }

        bool Is(XElement e, string name, string attribute, string value) =>
            e.Name.LocalName == name && e.Attribute(attribute)?.Value == value;

        switch (slip)
        {
            case "the bottom centre ornament missing":
                Only(e => Is(e, "DrawingGroup", "Transform", "1,0,0,-1,500,648")).Remove();
                break;
            case "a side ornament missing":
                Only(e => Is(e, "DrawingGroup", "Transform", "-1,0,0,1,918,350")).Remove();
                break;
            case "a leaf missing":
                Only(e => e.Attribute("Geometry")?.Value.StartsWith("F1 M 183 632", StringComparison.Ordinal) == true).Remove();
                break;
            case "a corner ornament not mirrored":
                Only(e => Is(e, "DrawingGroup", "Transform", "-1,0,0,-1,900,627")).SetAttributeValue("Transform", "1,0,0,-1,900,627");
                break;
            case "a gradient stop recoloured":
                Only(e => Is(e, "GradientStop", "Color", "#F7D36B")).SetAttributeValue("Color", "#F7D36C");
                break;
            case "a fill rule lost":
                var top = Only(e => e.Attribute("Geometry")?.Value.StartsWith("F1 M 75 25 C 190 5", StringComparison.Ordinal) == true);
                top.SetAttributeValue("Geometry", top.Attribute("Geometry")?.Value[3..]);
                break;
            case "a miter limit lost":
                Only(e => Is(e, "Pen", "Brush", "#5D2906")).Attribute("MiterLimit")?.Remove();
                break;
            case "a stroke opacity lost":
                Only(e => Is(e, "Pen", "Brush", "#BFFFE68B")).SetAttributeValue("Brush", "#FFE68B");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slip), slip, "Unknown slip.");
        }
    }

    // ---- Normalisation -----------------------------------------------------------------------

    private static string Describe(string geometry, string fill, string stroke, string width, string miter, string transform) =>
        stroke == "none"
            ? $"{geometry} | fill {fill} | stroke none | at {transform}"
            : $"{geometry} | fill {fill} | stroke {stroke} width {Format(width)} miter {Format(miter)} | at {transform}";

    private static string Describe(Matrix m) =>
        string.Create(CultureInfo.InvariantCulture, $"{m.M11},{m.M12},{m.M21},{m.M22},{m.OffsetX},{m.OffsetY}");

    // Whitespace inside path data and markup extensions is free; only the tokens matter.
    private static string Squash(string? value) =>
        value is null ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Number(XElement element, string attribute, string fallback = "")
    {
        var value = element.Attribute(attribute)?.Value ?? fallback;
        return Format(value);
    }

    private static string Format(string number) =>
        double.Parse(number, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);

    // "8%" and "0.08" are the same offset.
    private static string Fraction(string value) =>
        value.EndsWith('%')
            ? (double.Parse(value[..^1], CultureInfo.InvariantCulture) / 100).ToString("R", CultureInfo.InvariantCulture)
            : Format(value);

    private static string PointOf(string value) =>
        Point.Parse(value).ToString(CultureInfo.InvariantCulture);

    private static string RectOf(string value) =>
        Rect.Parse(value).ToString(CultureInfo.InvariantCulture);

    private static string Colour(string value, double opacity)
    {
        var colour = (Color)ColorConverter.ConvertFromString(value);
        colour.A = (byte)Math.Round(colour.A * opacity);
        return colour.ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^url\(#([^)]+)\)$")]
    private static partial Regex GradientReference();

    [GeneratedRegex(@"(translate|scale)\(([^)]*)\)")]
    private static partial Regex TransformOperation();

    [GeneratedRegex(@"^\{StaticResource\s+(\S+)\}$")]
    private static partial Regex ResourceReference();
}
