using System.IO;
using System.Text;
using Workflow.Models;

namespace Workflow.Tests;

/// <remarks>
/// The two sources of open gate items: the <c>## Offene Punkte</c> section of a done file and the
/// <c>openGateItems</c> array of a completed subtask's <c>status.json</c>. Every unreadable or
/// malformed source must surface as a read error, never as "nothing open".
/// </remarks>
public sealed class OpenGateItemsTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;
    private readonly string _donePath;
    private readonly SubtaskPaths _paths;

    public OpenGateItemsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-opengate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _donePath = Path.Combine(_root, "demo-done.md");
        _paths = new SubtaskPaths(_root, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteDone(string content) => File.WriteAllText(_donePath, content, Utf8);

    private void WriteIndexOf(params string[] titles) =>
        File.WriteAllText(
            _paths.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(titles)}} }""",
            Utf8);

    private void WriteStatus(string title, string json)
    {
        Directory.CreateDirectory(_paths.SubtaskDirectory(title));
        File.WriteAllText(_paths.SubtaskMarkdown(title), "Beschreibung", Utf8);
        File.WriteAllText(_paths.SubtaskStatusFile(title), json, Utf8);
    }

    // --- done file -----------------------------------------------------------------------------

    [Fact]
    public void FromDoneFile_SectionWithTwoBullets_ReturnsTwoImplementationItems()
    {
        // Arrange
        WriteDone("Umgesetzt.\n\n## Gate-Ergebnis\n- V1: passed - exit 0\n\n## Offene Punkte\n- V40: Popup prüfen\n- V41: Kopieren prüfen\n");

        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        Assert.Equal(
            [
                new OpenGateItem(OpenGateItems.ImplementationSource, "V40: Popup prüfen", false),
                new OpenGateItem(OpenGateItems.ImplementationSource, "V41: Kopieren prüfen", false),
            ],
            report.Items);
        Assert.False(report.HasReadErrors);
    }

    [Fact]
    public void FromDoneFile_SectionSaysKeine_ReturnsNoItems()
    {
        // Arrange
        WriteDone("Umgesetzt.\r\n\r\n## Offene Punkte\r\n- keine\r\n");

        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void FromDoneFile_FileAbsent_ReturnsNoItems()
    {
        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void FromDoneFile_NonBlankFileWithoutSection_ReturnsAReadError()
    {
        // Arrange - a done file written before the section existed cannot prove that nothing is open.
        WriteDone("Umgesetzt; verify.ps1 exit 0.");

        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        var item = Assert.Single(report.Items);
        Assert.True(item.IsReadError);
        Assert.Equal(OpenGateItems.ImplementationSource, item.Source);
    }

    [Fact]
    public void FromDoneFile_NextHeadingAfterSection_StopsReadingThere()
    {
        // Arrange
        WriteDone("## Offene Punkte\n- V40: offen\n\n## Anhang\n- kein offener Punkt\n");

        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        Assert.Equal("V40: offen", Assert.Single(report.Items).Text);
    }

    [Fact]
    public void FromDoneFile_FrontMatterAndWrappedBullet_ReadsOneJoinedItem()
    {
        // Arrange
        WriteDone("---\ndescription: \"done\"\nsummary: \"x\"\n---\n\nUmgesetzt.\n\n## Offene Punkte\n- V40: Popup\n  oberhalb prüfen\n");

        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        Assert.Equal("V40: Popup oberhalb prüfen", Assert.Single(report.Items).Text);
    }

    [Fact]
    public void FromDoneFile_FileLockedExclusively_ReturnsAReadError()
    {
        // Arrange
        WriteDone("## Offene Punkte\n- V40: offen\n");
        using var lockHolder = new FileStream(_donePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var report = OpenGateItems.FromDoneFile(_donePath);

        // Assert
        Assert.True(Assert.Single(report.Items).IsReadError);
    }

    // --- subtasks ------------------------------------------------------------------------------

    [Fact]
    public void FromSubtasks_TwoCompletedSubtasks_ReturnsTheirItemsInIndexOrder()
    {
        // Arrange - index order deliberately differs from alphabetical order.
        WriteIndexOf("ST-002-zwei", "ST-001-eins");
        WriteStatus("ST-002-zwei", """{ "status": "complete", "openGateItems": ["V40: Popup prüfen"] }""");
        WriteStatus("ST-001-eins", """{ "status": "complete", "openGateItems": ["V41: Gerät anschliessen", "  "] }""");

        // Act
        var report = OpenGateItems.FromSubtasks(_paths);

        // Assert
        Assert.Equal(
            [
                new OpenGateItem("ST-002-zwei", "V40: Popup prüfen", false),
                new OpenGateItem("ST-001-eins", "V41: Gerät anschliessen", false),
            ],
            report.Items);
    }

    [Theory]
    [InlineData("""{ "status": "complete", "openGateItems": 42 }""")]
    [InlineData("""{ "status": "complete", "openGateItems": ["V40", 7] }""")]
    public void FromSubtasks_ItemsAreNotAnArrayOfStrings_ReturnsAReadErrorForTheTitle(string json)
    {
        // Arrange - the ledger still calls this subtask complete; the items must not vanish silently.
        WriteIndexOf("ST-001-eins");
        WriteStatus("ST-001-eins", json);

        // Act
        var report = OpenGateItems.FromSubtasks(_paths);

        // Assert
        var item = Assert.Single(report.Items);
        Assert.True(item.IsReadError);
        Assert.Equal("ST-001-eins", item.Source);
    }

    [Fact]
    public void FromSubtasks_CompleteWithoutTheField_ReturnsNoItems()
    {
        // Arrange - a payload written before the field existed.
        WriteIndexOf("ST-001-eins");
        WriteStatus("ST-001-eins", """{ "status": "complete" }""");

        // Act
        var report = OpenGateItems.FromSubtasks(_paths);

        // Assert
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void FromSubtasks_SubtaskNotComplete_ReturnsNoItems()
    {
        // Arrange
        WriteIndexOf("ST-001-eins");
        WriteStatus("ST-001-eins", """{ "status": "failed", "failreason": "rot", "openGateItems": ["V40"] }""");

        // Act
        var report = OpenGateItems.FromSubtasks(_paths);

        // Assert
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void FromSubtasks_StatusLockedThenReleased_ReportsAReadErrorThenTheItems()
    {
        // Arrange
        WriteIndexOf("ST-001-eins");
        WriteStatus("ST-001-eins", """{ "status": "complete", "openGateItems": ["V40: offen"] }""");
        var lockHolder = new FileStream(_paths.SubtaskStatusFile("ST-001-eins"), FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        var whileLocked = OpenGateItems.FromSubtasks(_paths);
        lockHolder.Dispose();
        var afterRelease = OpenGateItems.FromSubtasks(_paths);

        // Assert - the ledger reads a locked payload as a failure, so completeness must not be taken
        // from it: the lock is a read error, and the retry finds the items.
        var locked = Assert.Single(whileLocked.Items);
        Assert.True(locked.IsReadError);
        Assert.Equal("ST-001-eins", locked.Source);
        Assert.Equal("V40: offen", Assert.Single(afterRelease.Items).Text);
    }

    [Fact]
    public void FromSubtasks_IndexMissing_ReturnsNoItems()
    {
        // Act
        var report = OpenGateItems.FromSubtasks(_paths);

        // Assert
        Assert.True(report.IsEmpty);
    }

    // --- format --------------------------------------------------------------------------------

    [Fact]
    public void Format_ItemsFromTwoSources_GroupsThemUnderTheirSource()
    {
        // Arrange
        var report = new OpenGateReport(
        [
            new OpenGateItem(OpenGateItems.ImplementationSource, "V40: Popup prüfen", false),
            new OpenGateItem("ST-001-eins", "Statusdatei fehlerhaft", true),
            new OpenGateItem(OpenGateItems.ImplementationSource, "V41: Kopieren prüfen", false),
        ]);

        // Act
        var text = OpenGateItems.Format("demo", report).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Assert
        Assert.Equal(
            "Offene Punkte – demo\n\nImplementierung\n- V40: Popup prüfen\n- V41: Kopieren prüfen\n\nST-001-eins\n- [nicht lesbar] Statusdatei fehlerhaft",
            text);
    }

    [Fact]
    public void Format_EmptyReport_ReturnsAnEmptyString()
    {
        // Act
        var text = OpenGateItems.Format("demo", OpenGateReport.Empty);

        // Assert
        Assert.Equal(string.Empty, text);
    }
}
