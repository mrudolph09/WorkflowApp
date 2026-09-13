using System.IO;

namespace Workflow.Tests;

public class IconTests
{
    private static string IconPath() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow", "Assets", "workflow.ico");

    [Fact]
    public void Icon_ExistsAndHasSixFrames()
    {
        var path = Path.GetFullPath(IconPath());

        Assert.True(File.Exists(path), $"Missing application icon: {path}");

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadInt16());   // reserved
        Assert.Equal(1, reader.ReadInt16());   // type: icon
        Assert.Equal(6, reader.ReadInt16());   // frame count
    }

    [Fact]
    public void Icon_IsLargerThanAPlaceholder()
    {
        var path = Path.GetFullPath(IconPath());

        Assert.True(new FileInfo(path).Length > 4096, "The icon looks like a placeholder.");
    }
}
