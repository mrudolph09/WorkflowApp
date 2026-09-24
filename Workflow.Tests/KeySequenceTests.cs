using Workflow.Services;

namespace Workflow.Tests;

public class KeySequenceTests
{
    [Fact]
    public void Split_KeepsAnEscapeSequenceTogetherAndSeparatesThePlainKeys()
    {
        var keys = KeySequence.Split("[B\r");

        Assert.Equal(["[B", "\r"], keys);
    }

    [Fact]
    public void Split_SeparatesEveryPlainCharacter()
    {
        Assert.Equal(["2", "\r"], KeySequence.Split("2\r"));
        Assert.Equal(["y", "\r"], KeySequence.Split("y\r"));
    }

    [Fact]
    public void Split_HandlesSs3AndBareEscape()
    {
        Assert.Equal(["OB", "\r"], KeySequence.Split("OB\r"));
        Assert.Equal([""], KeySequence.Split(""));
    }

    [Fact]
    public void Split_OfEmptyIsEmpty()
    {
        Assert.Empty(KeySequence.Split(string.Empty));
    }
}
