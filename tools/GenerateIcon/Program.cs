using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;

internal static class Program
{
    private static readonly int[] Sizes = [16, 32, 48, 64, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: GenerateIcon <output.ico>");
            return 1;
        }

        var frames = Sizes.Select(RenderPng).ToList();
        WriteIco(args[0], frames);

        Console.WriteLine($"Wrote {args[0]} with {frames.Count} frames.");
        return 0;
    }

    private static byte[] RenderPng(int size)
    {
        // PackIcon is a Control: its visible output comes entirely from a ControlTemplate
        // supplied by its default Style, which WPF never resolves in this Application-less
        // console host (no merged MaterialDesignThemes.Wpf resource dictionary). Rendering
        // the PackIcon itself would therefore produce a fully transparent frame. We still
        // construct a real PackIcon to source the ArmFlex path data from the library (so the
        // geometry stays in sync with MaterialDesignThemes and needs no magic strings), but
        // render a plain Path built from that geometry instead — a Shape draws itself directly
        // and needs no template.
        var icon = new PackIcon { Kind = PackIconKind.ArmFlex };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(icon.Data),
            Fill = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0xC0)), // Indigo 400
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
        };

        path.Measure(new Size(size, size));
        path.Arrange(new Rect(0, 0, size, size));
        path.UpdateLayout();

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(path);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteIco(string path, IReadOnlyList<byte[]> pngFrames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);

        writer.Write((short)0);                     // reserved
        writer.Write((short)1);                     // type: icon
        writer.Write((short)pngFrames.Count);

        var offset = 6 + (16 * pngFrames.Count);

        for (var i = 0; i < pngFrames.Count; i++)
        {
            var size = Sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));   // width, 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // palette size
            writer.Write((byte)0);                          // reserved
            writer.Write((short)1);                         // colour planes
            writer.Write((short)32);                        // bits per pixel
            writer.Write(pngFrames[i].Length);
            writer.Write(offset);
            offset += pngFrames[i].Length;
        }

        foreach (var frame in pngFrames)
        {
            writer.Write(frame);
        }
    }
}
