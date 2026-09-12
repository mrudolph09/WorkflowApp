using Workflow.Services;

namespace Workflow.Tests;

public class EscapeDecoderTests
{
    [Theory]
    [InlineData(@"2\r", "2\r")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"\e[B", "[B")]
    [InlineData(@"back\\slash", @"back\slash")]
    [InlineData(@"A", "A")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Decode_HandlesSupportedEscapes(string raw, string expected)
    {
        Assert.Equal(expected, EscapeDecoder.Decode(raw));
    }

    [Fact]
    public void Decode_LeavesAnUnknownEscapeIntact()
    {
        Assert.Equal(@"\q", EscapeDecoder.Decode(@"\q"));
    }

    [Fact]
    public void Decode_LeavesATrailingBackslashIntact()
    {
        Assert.Equal(@"abc\", EscapeDecoder.Decode(@"abc\"));
    }

    [Fact]
    public void Decode_LeavesAMalformedUnicodeEscapeIntact()
    {
        Assert.Equal(@"\u00", EscapeDecoder.Decode(@"\u00"));
    }
}
