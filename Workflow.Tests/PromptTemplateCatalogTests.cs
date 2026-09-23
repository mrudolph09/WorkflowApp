using System.IO;
using System.Text;
using Workflow.Services;

namespace Workflow.Tests;

/// <summary>
/// Requirement 6.3 / 6.4: startup validation checks every shipped prompt template against
/// <em>its own</em> supported substitutions, not against the union of all known names, and reports
/// a shipped file the catalog does not recognize instead of silently skipping it.
/// </summary>
/// <remarks>
/// The fixture is asymmetric in the dimension the component evaluates - the (file -&gt; token set)
/// pair. Each of the three catalog layers gets a distinguishing token that is illegal one layer
/// down: <c>done_path</c> for the base layer, <c>tasktitel</c> for the decomposition layer and
/// <c>subtask_title</c>/<c>subtask</c> for the execution layer. Replacing the per-file lookup with
/// a union of all names, or swapping two entries' sets, therefore has to fail this class.
/// </remarks>
public sealed class PromptTemplateCatalogTests : IDisposable
{
    private const string BaseToken = "done_path";
    private const string CreationToken = "tasktitel";
    private const string RunToken = "subtask_title";

    private readonly string _dir;

    public PromptTemplateCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string ShippedPromptDirectory => Path.Combine(AppContext.BaseDirectory, "Prompt");

    /// <summary>The eight files requirement 6.3 enumerates, with a token only their own layer allows.</summary>
    private static Dictionary<string, string> DefaultContents() => new(StringComparer.Ordinal)
    {
        ["initial_prompt.md"] = "Phase eins {" + BaseToken + "}",
        ["review_prompt.md"] = "Phase zwei {" + BaseToken + "}",
        ["resolve_review_prompt.md"] = "Phase drei {" + BaseToken + "}",
        ["implementation_prompt.md"] = "Phase vier {" + BaseToken + "}",
        ["create_subtasks.md"] = "Zerlegung {" + CreationToken + "} {task_path}",
        ["counter_prompt.md"] = "Zaehler {" + CreationToken + "}",
        ["evidence_gate.md"] = "Nachweis {" + CreationToken + "}",
        ["run_subtask.md"] = "Ausfuehrung {" + RunToken + "} {subtask}",
    };

    private void Write(string name, string content) =>
        File.WriteAllText(
            Path.Combine(_dir, name),
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private void WriteAllEight()
    {
        foreach (var (name, content) in DefaultContents())
        {
            Write(name, content);
        }
    }

    /// <summary>Every <c>.md</c> file the <c>Prompt\**\*.md</c> glob actually ships.</summary>
    public static TheoryData<string> ShippedPromptFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(ShippedPromptDirectory, "*.md", SearchOption.AllDirectories))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    /// <summary>
    /// A file valid in its own layer must stay valid; this is the control for every negative case
    /// below, so a catalog that rejected everything could not masquerade as a passing suite.
    /// </summary>
    [Fact]
    public void ValidateAll_AcceptsEveryShippedFileAgainstItsOwnTokenSet()
    {
        WriteAllEight();
        var service = new PromptTemplateService(_dir);

        Assert.Empty(service.ValidateAll());
    }

    /// <summary>
    /// Requirement 6.4. Each row uses a token that is legal <em>somewhere</em> in the catalog but
    /// not in the file that carries it, so a union of all known names would accept every row.
    /// </summary>
    [Theory]
    // Run-set and creation-set tokens in the four base-layer phase prompts.
    [InlineData("initial_prompt.md", "subtask")]
    [InlineData("review_prompt.md", CreationToken)]
    [InlineData("resolve_review_prompt.md", RunToken)]
    [InlineData("implementation_prompt.md", "workflow_path")]
    // Run-set tokens in the three decomposition-layer prompts.
    [InlineData("create_subtasks.md", "subtask")]
    [InlineData("counter_prompt.md", RunToken)]
    [InlineData("evidence_gate.md", "subtask")]
    // The execution prompt is the widest layer, so only a name in no set at all is illegal there.
    [InlineData("run_subtask.md", "kein_bekannter_name")]
    public void ValidateAll_ReportsATokenThatIsNotInThatFilesOwnSet(string fileName, string illegalToken)
    {
        WriteAllEight();
        Write(fileName, "Text mit {" + illegalToken + "} darin");
        var service = new PromptTemplateService(_dir);

        var errors = service.ValidateAll();

        var error = Assert.Single(errors);
        Assert.Contains(fileName, error, StringComparison.Ordinal);
        Assert.Contains(illegalToken, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Requirement 6.3's "report any shipped file the catalog does not recognize": a new prompt
    /// file added to the glob is an error in its own right, never a silently skipped file.
    /// </summary>
    [Fact]
    public void ValidateAll_ReportsAPromptFileTheCatalogDoesNotRecognize()
    {
        WriteAllEight();
        Write("neuer_prompt.md", "Ein neuer Prompt ohne Katalogeintrag {" + BaseToken + "}");
        var service = new PromptTemplateService(_dir);

        var errors = service.ValidateAll();

        var error = Assert.Single(errors);
        Assert.Contains("neuer_prompt.md", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chosen rule, stated as a test: "unrecognized" is a file present on disk with no catalog
    /// entry. The mirror image - a catalog entry with no file on disk - is the "not found" case
    /// <c>Render</c> already raises, so a directory that legitimately supplies only some templates
    /// produces no spurious startup errors.
    /// </summary>
    [Fact]
    public void ValidateAll_DoesNotReportACatalogEntryThatHasNoFileOnDisk()
    {
        foreach (var name in new[]
                 {
                     "initial_prompt.md", "review_prompt.md",
                     "resolve_review_prompt.md", "implementation_prompt.md",
                 })
        {
            Write(name, DefaultContents()[name]);
        }

        var service = new PromptTemplateService(_dir);

        Assert.Empty(service.ValidateAll());
    }

    /// <summary>The empty-file check must reach the four files that were never validated before.</summary>
    [Theory]
    [InlineData("create_subtasks.md")]
    [InlineData("counter_prompt.md")]
    [InlineData("evidence_gate.md")]
    [InlineData("run_subtask.md")]
    public void ValidateAll_ReportsAnEmptyFileAmongTheNewlyCoveredTemplates(string fileName)
    {
        WriteAllEight();
        Write(fileName, "   \r\n  ");
        var service = new PromptTemplateService(_dir);

        var error = Assert.Single(service.ValidateAll());

        Assert.Contains(fileName, error, StringComparison.Ordinal);
    }

    /// <summary>Requirement 6.3 counts the shipped set; a ninth file must reach the catalog too.</summary>
    [Fact]
    public void ShippedPromptDirectory_ShipsExactlyTheEightFilesRequirement63Enumerates()
    {
        var shipped = Directory
            .GetFiles(ShippedPromptDirectory, "*.md", SearchOption.AllDirectories)
            .Select(p => Path.GetFileName(p))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            DefaultContents().Keys.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            shipped);
    }

    /// <summary>
    /// Coverage proof driven by the shipped directory itself rather than by a hand-written list:
    /// every file the glob ships is actually read and token-checked by <c>ValidateAll</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedPromptFiles))]
    public void ValidateAll_ChecksEveryFileTheShippedPromptDirectoryContains(string fileName)
    {
        foreach (var path in Directory.GetFiles(ShippedPromptDirectory, "*.md", SearchOption.AllDirectories))
        {
            File.Copy(path, Path.Combine(_dir, Path.GetFileName(path)));
        }

        File.AppendAllText(Path.Combine(_dir, fileName), "\n{zzz_unbekannter_token}\n");
        var service = new PromptTemplateService(_dir);

        var error = Assert.Single(service.ValidateAll());

        Assert.Contains(fileName, error, StringComparison.Ordinal);
        Assert.Contains("zzz_unbekannter_token", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mapping itself, pinned per file. Swapping two entries' sets fails here as well as in
    /// the behavioural cases above, and the base entries are asserted to be the base set - not a
    /// widened one that would legalise <c>{tasktitel}</c> in a phase prompt.
    /// </summary>
    [Theory]
    [InlineData("initial_prompt.md", "base")]
    [InlineData("review_prompt.md", "base")]
    [InlineData("resolve_review_prompt.md", "base")]
    [InlineData("implementation_prompt.md", "base")]
    [InlineData("create_subtasks.md", "creation")]
    [InlineData("counter_prompt.md", "creation")]
    [InlineData("evidence_gate.md", "creation")]
    [InlineData("run_subtask.md", "run")]
    public void AllowedTokens_MapsEachShippedFileToItsOwnLayer(string fileName, string layer)
    {
        var expected = layer switch
        {
            "base" => PromptVariables.KnownNames,
            "creation" => PromptVariables.SubtaskCreationNames,
            "run" => PromptVariables.SubtaskRunNames,
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };

        Assert.True(PromptTemplateCatalog.AllowedTokens.TryGetValue(fileName, out var actual));
        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            actual!.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>The catalog covers the shipped set exactly - no entry too few, none too many.</summary>
    [Fact]
    public void AllowedTokens_CoversExactlyTheShippedPromptFiles()
    {
        var shipped = Directory
            .GetFiles(ShippedPromptDirectory, "*.md", SearchOption.AllDirectories)
            .Select(p => Path.GetFileName(p))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            shipped,
            PromptTemplateCatalog.AllowedTokens.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }
}
