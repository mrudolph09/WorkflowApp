using Workflow.Services;

namespace Workflow.Tests;

public class BracketedPasteTests
{
    [Fact]
    public void Wrap_SurroundsTheBodyWithTheBracketedPasteSequences()
    {
        var wrapped = BracketedPaste.Wrap("hello");

        Assert.Equal("[200~hello[201~", wrapped);
    }

    [Theory]
    [InlineData("a\r\nb", "a\rb")]
    [InlineData("a\nb", "a\rb")]
    [InlineData("a\rb", "a\rb")]
    [InlineData("a\n\nb", "a\r\rb")]
    public void Wrap_NormalisesEveryNewlineToCarriageReturn(string body, string expectedBody)
    {
        var wrapped = BracketedPaste.Wrap(body);

        Assert.Equal("[200~" + expectedBody + "[201~", wrapped);
    }

    [Fact]
    public void Wrap_HandlesAnEmptyBody()
    {
        Assert.Equal("[200~[201~", BracketedPaste.Wrap(string.Empty));
    }
}
