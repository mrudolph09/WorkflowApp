using System.IO;

namespace Workflow.Tests;

public class TerminalAssetTests
{
    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal", name);

    [Theory]
    [InlineData("terminal.html")]
    [InlineData("terminal.js")]
    [InlineData("xterm.js")]
    [InlineData("xterm.css")]
    [InlineData("addon-fit.js")]
    public void Asset_IsCopiedToTheOutputDirectory(string name)
    {
        var path = AssetPath(name);

        Assert.True(File.Exists(path), $"Missing terminal asset: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Empty terminal asset: {path}");
    }

    [Fact]
    public void TerminalHtml_ReferencesTheVendoredScriptsAndNoExternalHost()
    {
        var html = File.ReadAllText(AssetPath("terminal.html"));

        Assert.Contains("src=\"xterm.js\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"addon-fit.js\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"terminal.js\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerminalJs_ImplementsTheAgreedMessageProtocol()
    {
        var js = File.ReadAllText(AssetPath("terminal.js"));

        Assert.Contains("window.wfSnapshot", js, StringComparison.Ordinal);
        Assert.Contains("'ready'", js, StringComparison.Ordinal);
        Assert.Contains("'in'", js, StringComparison.Ordinal);
        Assert.Contains("'resize'", js, StringComparison.Ordinal);
        Assert.Contains("'out'", js, StringComparison.Ordinal);
        Assert.Contains("'clear'", js, StringComparison.Ordinal);
    }
}
