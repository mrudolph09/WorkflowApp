using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// The gold terminal frame is pure markup (<c>Styles/TerminalFrameStyles.xaml</c>), and markup fails
/// at runtime rather than at compile time: a mistyped path, a wrong cut or a missing attribute
/// builds cleanly and only shows up on screen. These tests lay the style out and render it
/// OFFSCREEN - Measure/Arrange and <see cref="RenderTargetBitmap"/> on the test's own STA thread,
/// no window - so they observe what a user would see without driving the desktop.
/// </para>
/// <para>
/// Every test loads its own copy of the compiled dictionary and never touches
/// <see cref="Application.Current"/>: <see cref="ShippedMarkupLoadTests"/> owns the one
/// <see cref="Application"/> of this process, and its resources belong to that test's thread.
/// </para>
/// <para>
/// Offscreen there is no <see cref="PresentationSource"/>, so layout would round at the machine's
/// system DPI whatever the test asks for. Every test therefore pins the DPI of its frame with
/// <see cref="VisualTreeHelper.SetRootDpi"/> and asserts it took effect, so each case lays out AND
/// renders at the DPI it names, on any machine.
/// </para>
/// </remarks>
public class TerminalFrameStyleTests
{
    // The frame's fixed thickness: the SVG's 176 x 136 border at scale 1/4.
    private const double SideWidth = 44;
    private const double BandHeight = 34;

    private static readonly Color TerminalColour = Color.FromRgb(0x1E, 0x1E, 0x1E);

    private static ResourceDictionary LoadFrameDictionary() =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri("/Workflow;component/Styles/TerminalFrameStyles.xaml", UriKind.Relative));

    // The shipped dictionary parsed from its source file with one attribute deleted: the mutant a
    // seam test must be able to tell apart from the real style.
    private static ResourceDictionary LoadFrameDictionaryWithout(string attribute)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow", "Styles", "TerminalFrameStyles.xaml"));
        var markup = File.ReadAllText(path);
        var mutant = markup.Replace(attribute, string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(markup, mutant);
        return (ResourceDictionary)XamlReader.Parse(mutant);
    }

    private static ContentControl ArrangedFrame(
        ResourceDictionary dictionary, UIElement content, double width, double height, double dpi, double offset)
    {
        var frame = new ContentControl { Style = (Style)dictionary["TerminalFrameStyle"], Content = content };

        // SetRootDpi reaches only the visuals that exist when it is called, so build the tree
        // (template, content presenter, content) first, then pin the DPI and lay the whole tree
        // out again: every layout rounding then happens at this DPI.
        LayOut(frame, width, height, offset);
        VisualTreeHelper.SetRootDpi(frame, new DpiScale(dpi / 96, dpi / 96));
        InvalidateLayout(frame);
        LayOut(frame, width, height, offset);
        return frame;
    }

    private static void LayOut(UIElement element, double width, double height, double offset)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(offset, offset, width, height));
        element.UpdateLayout();
    }

    private static void InvalidateLayout(DependencyObject node)
    {
        if (node is UIElement element)
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            InvalidateLayout(VisualTreeHelper.GetChild(node, i));
        }
    }

    // Where the content ends up in the rendered bitmap: its place in the frame plus the frame's
    // own offset, which RenderTargetBitmap renders too.
    private static Rect BoundsInBitmap(ContentControl frame, FrameworkElement content)
    {
        var origin = content.TranslatePoint(new Point(0, 0), frame);
        var offset = VisualTreeHelper.GetOffset(frame);
        return new Rect(origin.X + offset.X, origin.Y + offset.Y, content.ActualWidth, content.ActualHeight);
    }

    // The opening's four edges - left, top, right, bottom - in device pixels.
    private static double[] OpeningEdgesInDevicePixels(ContentControl frame, FrameworkElement content, double dpi)
    {
        var origin = content.TranslatePoint(new Point(0, 0), frame);
        return [.. new[] { origin.X, origin.Y, origin.X + content.ActualWidth, origin.Y + content.ActualHeight }.Select(dip => dip * dpi / 96)];
    }

    private static bool IsWholePixel(double pixels) => Math.Abs(pixels - Math.Round(pixels)) < 1e-6;

    [StaFact]
    public void TerminalFrameDrawing_Bounds_AreExactlyTheSvgViewBox()
    {
        // Arrange
        var dictionary = LoadFrameDictionary();

        // Act
        var drawing = (Drawing)dictionary["TerminalFrameDrawing"];

        // Assert - every slice's Viewbox is written in SVG coordinates, which only holds while the
        // invisible backing rectangle pins the bounds to the viewBox.
        Assert.Equal(new Rect(0, 0, 1000, 700), drawing.Bounds);
    }

    [StaTheory]
    [InlineData(1236, 420, 96)]
    [InlineData(1076, 300, 120)]
    [InlineData(1896, 900, 144)]
    [InlineData(1077, 301, 168)]
    public void TerminalFrameStyle_ArrangedAtAnyDpi_PlacesTheContentInTheOpeningOnWholeDevicePixels(double width, double height, double dpi)
    {
        // Arrange
        var content = new Border();

        // Act
        var frame = ArrangedFrame(LoadFrameDictionary(), content, width, height, dpi, 0);
        var origin = content.TranslatePoint(new Point(0, 0), frame);
        var devicePixel = 96 / dpi;

        // Assert - the layout really ran at this DPI...
        Assert.Equal(dpi, VisualTreeHelper.GetDpi(frame).PixelsPerInchX);
        Assert.Equal(dpi, VisualTreeHelper.GetDpi(content).PixelsPerInchX);

        // ...the content fills the opening, give or take the one device pixel layout rounding may
        // move an edge by...
        Assert.Equal(SideWidth, origin.X, devicePixel);
        Assert.Equal(BandHeight, origin.Y, devicePixel);
        Assert.Equal(width - (2 * SideWidth), content.ActualWidth, devicePixel);
        Assert.Equal(height - (2 * BandHeight), content.ActualHeight, devicePixel);

        // ...and every edge of it lies on a whole device pixel (UseLayoutRounding), so the terminal
        // is never drawn half a pixel into the gold.
        var edges = OpeningEdgesInDevicePixels(frame, content, dpi);
        Assert.True(edges.All(IsWholePixel), $"Opening edges at {string.Join(", ", edges)} device pixels.");
    }

    [StaTheory]
    [InlineData(120, 90)]
    [InlineData(60, 40)]
    public void TerminalFrameStyle_ArrangedSmallerThanItsOrnaments_ClipsWithoutThrowing(double width, double height)
    {
        // Arrange
        var content = new Border();

        // Act - smaller than the fixed pieces (190 x 133 DIP): the stretch pieces collapse to
        // nothing and the fixed ones are clipped. Unreachable through MainWindow's minimum size,
        // but it must never throw.
        var frame = ArrangedFrame(LoadFrameDictionary(), content, width, height, 96, 0);
        _ = new RenderedFrame(frame, width, height, 96);

        // Assert
        Assert.Equal(Math.Max(0, width - (2 * SideWidth)), content.ActualWidth, 1.0);
        Assert.Equal(Math.Max(0, height - (2 * BandHeight)), content.ActualHeight, 1.0);
    }

    [StaFact]
    public void TerminalFrameStyle_Frame_IsNeitherFocusableNorATabStop()
    {
        // Arrange
        var dictionary = LoadFrameDictionary();

        // Act
        var frame = ArrangedFrame(dictionary, new Border(), 1236, 420, 96, 0);

        // Assert
        Assert.False(frame.Focusable);
        Assert.False(frame.IsTabStop);
    }

    [StaTheory]
    [InlineData(1077, 301, 96)]
    [InlineData(1897, 900, 96)]
    [InlineData(3790, 1950, 96)]
    [InlineData(1077, 301, 120)]
    [InlineData(1236, 420, 120)]
    [InlineData(1077, 301, 144)]
    [InlineData(1236, 420, 144)]
    [InlineData(1077, 301, 168)]
    [InlineData(1236, 420, 168)]
    public void TerminalFrameStyle_RenderedAtItsLayoutDpi_ShowsNoSeamAtAnyCut(double width, double height, double dpi)
    {
        // Arrange - a monitor at 100, 125, 150 or 175 % scaling; 1897 x 900 and 3790 x 1950 are a
        // maximised window on a full-HD and on a 4K monitor.
        var content = new Border { Background = new SolidColorBrush(TerminalColour) };
        var frame = ArrangedFrame(LoadFrameDictionary(), content, width, height, dpi, 0);

        // Act
        var rendered = new RenderedFrame(frame, width, height, dpi);

        // Assert
        var seams = SeamsAtTheCuts(rendered, width, height);
        seams.AddRange(SeamsAtTheOpening(rendered, BoundsInBitmap(frame, content)));
        Assert.True(seams.Count == 0, $"{seams.Count} seam pixels, first: {string.Join(", ", seams.Take(6))}");
    }

    [StaTheory]
    [InlineData(1077, 301, 96, 0.3)]
    [InlineData(1236, 420, 120, 0.3)]
    [InlineData(1236, 420, 144, 0.25)]
    [InlineData(1077, 301, 168, 0.5)]
    public void TerminalFrameStyle_RenderedOffTheDevicePixelGrid_ShowsNoSeamAtAnyCut(double width, double height, double dpi, double offset)
    {
        // Arrange - the frame starts a fraction of a pixel off the device grid, as it does when an
        // ancestor without layout rounding places it there.
        var content = new Border { Background = new SolidColorBrush(TerminalColour) };
        var frame = ArrangedFrame(LoadFrameDictionary(), content, width, height, dpi, offset);

        // Act
        var rendered = new RenderedFrame(frame, width, height, dpi);

        // Assert
        var seams = SeamsAtTheCuts(rendered, width, height);
        seams.AddRange(SeamsAtTheOpening(rendered, BoundsInBitmap(frame, content)));
        Assert.True(seams.Count == 0, $"{seams.Count} seam pixels, first: {string.Join(", ", seams.Take(6))}");
    }

    [StaTheory]
    [InlineData(1077, 301, 96, 0.3)]
    [InlineData(1236, 420, 120, 0.3)]
    [InlineData(1236, 420, 144, 0.25)]
    [InlineData(1077, 301, 168, 0.5)]
    public void TerminalFrameStyle_WithoutSnapsToDevicePixels_ShowsSeamsOffTheDevicePixelGrid(double width, double height, double dpi, double offset)
    {
        // Arrange - the style exactly as shipped, minus SnapsToDevicePixels: proves that attribute
        // is what keeps the cases above seamless, and that the seam scan can see a seam at all.
        var content = new Border { Background = new SolidColorBrush(TerminalColour) };
        var frame = ArrangedFrame(LoadFrameDictionaryWithout("<Setter Property=\"SnapsToDevicePixels\" Value=\"True\" />"), content, width, height, dpi, offset);

        // Act
        var rendered = new RenderedFrame(frame, width, height, dpi);

        // Assert
        Assert.NotEmpty(SeamsAtTheCuts(rendered, width, height));
    }

    [StaTheory]
    [InlineData(1076, 300, 120)]
    [InlineData(1077, 301, 168)]
    public void TerminalFrameStyle_WithoutUseLayoutRounding_PutsTheOpeningBetweenDevicePixels(double width, double height, double dpi)
    {
        // Arrange - the style exactly as shipped, minus UseLayoutRounding: proves that attribute is
        // what keeps the opening on whole device pixels at 125 and 175 % scaling.
        var content = new Border();
        var frame = ArrangedFrame(LoadFrameDictionaryWithout(" UseLayoutRounding=\"True\""), content, width, height, dpi, 0);

        // Act
        var edges = OpeningEdgesInDevicePixels(frame, content, dpi);

        // Assert
        Assert.False(edges.All(IsWholePixel), $"Opening edges at {string.Join(", ", edges)} device pixels.");
    }

    [StaFact]
    public void TerminalFrameStyle_Rendered_PaintsGoldOnTheFrameAndLeavesTheOpeningToTheContent()
    {
        // Arrange
        var frame = ArrangedFrame(LoadFrameDictionary(), new Border { Background = new SolidColorBrush(TerminalColour) }, 1236, 420, 96, 0);

        // Act
        var rendered = new RenderedFrame(frame, 1236, 420, 96);
        var outside = rendered.At(1, 1);
        var ornament = rendered.At(25, 18);
        var opening = rendered.At(618, 210);

        // Assert
        // Outside the frame's curved silhouette nothing is drawn: the tab surface shows through.
        Assert.Equal(0, outside.A);

        // The top-left corner rosette (SVG 100,72) is opaque gold.
        Assert.Equal(255, ornament.A);
        Assert.True(ornament.R > ornament.G && ornament.G > ornament.B, $"Expected gold at the corner ornament, found {ornament}.");

        // The opening belongs to the content: nothing of the frame is drawn over it.
        Assert.Equal(TerminalColour, opening);
    }

    [StaFact]
    public void TerminalFrameStyle_TwoFramesFromOneDictionary_BothRenderTheOrnament()
    {
        // Arrange - every tab has its own frame, and all of them share the one drawing.
        var dictionary = LoadFrameDictionary();
        var first = ArrangedFrame(dictionary, new Border(), 1236, 420, 96, 0);
        var second = ArrangedFrame(dictionary, new Border(), 1236, 420, 96, 0);

        // Act
        var firstRendered = new RenderedFrame(first, 1236, 420, 96);
        var secondRendered = new RenderedFrame(second, 1236, 420, 96);

        // Assert
        Assert.Equal(255, secondRendered.At(25, 18).A);
        Assert.Equal(firstRendered.At(25, 18), secondRendered.At(25, 18));
        Assert.Equal(firstRendered.At(600, 17), secondRendered.At(600, 17));
    }

    // Every cut between two pieces, and every joint between two bands, probed where the artwork is
    // solid on both sides of it: any pixel there that is not fully opaque is the background showing
    // through a seam. Alpha is the exact signal - the frame is rendered over nothing, so a seam
    // cannot hide behind a colour that happens to match.
    private static List<string> SeamsAtTheCuts(RenderedFrame rendered, double width, double height)
    {
        var seams = new List<string>();

        // Top and bottom bands: the solid moulding at y 14..22 DIP, from x 60 to width - 60 - across
        // all four cuts of each band (65, both ends of the 60-DIP centre piece, width - 65).
        for (var x = rendered.PixelOf(60); x <= rendered.PixelOf(width - 60); x++)
        {
            for (var y = rendered.PixelOf(14); y <= rendered.PixelOf(22); y++)
            {
                AddIfNotOpaque(seams, rendered, x, y);
                AddIfNotOpaque(seams, rendered, x, rendered.Height - 1 - y);
            }
        }

        // Left and right bands: the solid moulding at x 17..32 DIP, from y 30 to height - 30 -
        // across the joints with the top and bottom bands (y 34, height - 34) and both cuts of the
        // fixed side-ornament piece (centre +-32.5). Only the ornament's waist (centre +-30) is
        // skipped: there the moulding curves inward and the artwork itself shows the background.
        var centre = height / 2;
        for (var y = rendered.PixelOf(30); y <= rendered.PixelOf(height - 30); y++)
        {
            var dipY = y * 96 / rendered.Dpi;
            if (dipY > centre - 30 && dipY < centre + 30)
            {
                continue;
            }

            for (var x = rendered.PixelOf(17); x <= rendered.PixelOf(32); x++)
            {
                AddIfNotOpaque(seams, rendered, x, y);
                AddIfNotOpaque(seams, rendered, rendered.Width - 1 - x, y);
            }
        }

        return seams;
    }

    // The four edges of the opening are cuts too: the content must begin exactly where the frame
    // ends, so its outermost ring of device pixels is the content colour and nothing else.
    private static List<string> SeamsAtTheOpening(RenderedFrame rendered, Rect content)
    {
        var seams = new List<string>();
        var left = rendered.PixelOf(content.Left);
        var top = rendered.PixelOf(content.Top);
        var right = rendered.PixelOf(content.Right) - 1;
        var bottom = rendered.PixelOf(content.Bottom) - 1;

        for (var x = left; x <= right; x++)
        {
            AddIfNotContent(seams, rendered, x, top);
            AddIfNotContent(seams, rendered, x, bottom);
        }

        for (var y = top; y <= bottom; y++)
        {
            AddIfNotContent(seams, rendered, left, y);
            AddIfNotContent(seams, rendered, right, y);
        }

        return seams;
    }

    private static void AddIfNotOpaque(List<string> seams, RenderedFrame rendered, int x, int y)
    {
        var alpha = rendered.At(x, y).A;
        if (alpha != 255)
        {
            seams.Add($"({x},{y}) alpha {alpha}");
        }
    }

    private static void AddIfNotContent(List<string> seams, RenderedFrame rendered, int x, int y)
    {
        var colour = rendered.At(x, y);
        if (colour != TerminalColour)
        {
            seams.Add($"({x},{y}) {colour} at the opening");
        }
    }

    /// <summary>A visual rendered offscreen into a 32-bit premultiplied bitmap.</summary>
    private sealed class RenderedFrame
    {
        private readonly byte[] _pixels;

        public RenderedFrame(Visual visual, double width, double height, double dpi)
        {
            Dpi = dpi;
            Width = (int)Math.Round(width * dpi / 96);
            Height = (int)Math.Round(height * dpi / 96);

            var bitmap = new RenderTargetBitmap(Width, Height, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            _pixels = new byte[Width * Height * 4];
            bitmap.CopyPixels(_pixels, Width * 4, 0);
        }

        public double Dpi { get; }

        public int Width { get; }

        public int Height { get; }

        public int PixelOf(double dip) => (int)Math.Round(dip * Dpi / 96);

        // Pbgra32 stores each pixel as B, G, R, A.
        public Color At(int x, int y)
        {
            var i = ((y * Width) + x) * 4;
            return Color.FromArgb(_pixels[i + 3], _pixels[i + 2], _pixels[i + 1], _pixels[i]);
        }
    }
}
