using System.IO;
using System.Text;
using Workflow.Models;

namespace Workflow.Tests;

/// <remarks>
/// Task 1.3 only: reading and normalising the task-level ordered index. Deriving per-subtask state
/// from status payloads, descriptions and flags is task 1.4 and is not exercised here.
/// </remarks>
public sealed class SubtaskLedgerTests : IDisposable
{
    private readonly string _root;
    private readonly SubtaskPaths _paths;

    public SubtaskLedgerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private void WriteIndex(string json) =>
        File.WriteAllText(_paths.ResultAbsolute, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    // ---------------------------------------------------------------------------------------
    // Unusable index (requirement 2.9 / design "Tracking Files"): the whole file cannot be used,
    // which is a strictly different outcome from an index that merely contains bad entries.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TryReadIndex_ReportsAnAbsentIndexAsUnusable() => Assert.Null(SubtaskLedger.TryReadIndex(_paths));

    [Fact]
    public void TryReadIndex_ReportsAMalformedIndexAsUnusable()
    {
        WriteIndex("{ \"subtasks\": [\"ST-001-parse\"");

        Assert.Null(SubtaskLedger.TryReadIndex(_paths));
    }

    [Fact]
    public void TryReadIndex_ReportsAnEmptyFileAsUnusable()
    {
        WriteIndex(string.Empty);

        Assert.Null(SubtaskLedger.TryReadIndex(_paths));
    }

    [Fact]
    public void TryReadIndex_ReportsANonObjectRootAsUnusable()
    {
        WriteIndex("[\"ST-001-parse\", \"ST-002-render\"]");

        Assert.Null(SubtaskLedger.TryReadIndex(_paths));
    }

    // The array is REQUIRED and NON-EMPTY. Neither case may fall back to listing the subtasks
    // directory or to alphabetical order (requirement 6.1).
    [Theory]
    [InlineData("{ \"task\": \"demo\" }")]
    [InlineData("{ \"subtasks\": [] }")]
    [InlineData("{ \"subtasks\": null }")]
    [InlineData("{ \"subtasks\": \"ST-001-parse\" }")]
    [InlineData("{ \"subtasks\": { \"0\": \"ST-001-parse\" } }")]
    public void TryReadIndex_ReportsASchemaInvalidIndexAsUnusable(string json)
    {
        WriteIndex(json);

        Assert.Null(SubtaskLedger.TryReadIndex(_paths));
    }

    // "optional version absent or at most 1": 1 and absent are usable, 2 is not. Asymmetric on
    // purpose - an implementation that ignored the version entirely passes the first two rows and
    // fails the third, and one that rejected version 1 fails the second.
    [Theory]
    [InlineData("{ \"subtasks\": [\"ST-001-parse\"] }", true)]
    [InlineData("{ \"version\": 1, \"subtasks\": [\"ST-001-parse\"] }", true)]
    [InlineData("{ \"version\": 2, \"subtasks\": [\"ST-001-parse\"] }", false)]
    [InlineData("{ \"version\": 17, \"subtasks\": [\"ST-001-parse\"] }", false)]
    [InlineData("{ \"version\": \"1\", \"subtasks\": [\"ST-001-parse\"] }", false)]
    public void TryReadIndex_AcceptsOnlyAVersionOfAtMostOne(string json, bool usable)
    {
        WriteIndex(json);

        var entries = SubtaskLedger.TryReadIndex(_paths);

        if (usable)
        {
            Assert.NotNull(entries);
            Assert.Equal("ST-001-parse", Assert.Single(entries).Title);
        }
        else
        {
            Assert.Null(entries);
        }
    }

    [Fact]
    public void TryReadIndex_ReportsAnUnreadableIndexAsUnusable()
    {
        WriteIndex("{ \"subtasks\": [\"ST-001-parse\"] }");

        using var exclusive = new FileStream(
            _paths.ResultAbsolute, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(SubtaskLedger.TryReadIndex(_paths));
    }

    // ---------------------------------------------------------------------------------------
    // Order (requirement 6.1): the declared order is the execution order. No directory sorting,
    // no alphabetical fallback. The fixture is deliberately NOT in alphabetical order and its
    // titles are all distinct, so a sorted or reversed result is caught.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TryReadIndex_KeepsTheDeclaredOrderAndNeverSortsIt()
    {
        WriteIndex(
            """
            {
              "version": 1,
              "task": "informational, must not influence the entries",
              "subtasks": ["ST-004-verify", "ST-001-parse", "ST-003-persist", "ST-002-render", "ST-005-ship"]
            }
            """);

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(
            ["ST-004-verify", "ST-001-parse", "ST-003-persist", "ST-002-render", "ST-005-ship"],
            entries.Select(entry => entry.Title));
        Assert.All(entries, entry => Assert.Equal(SubtaskStatus.Pending, entry.Status));
        Assert.All(entries, entry => Assert.Null(entry.FailReason));
    }

    [Fact]
    public void TryReadIndex_ReadsThePropertyNamesCaseInsensitively()
    {
        WriteIndex("{ \"Version\": 1, \"Task\": \"demo\", \"Subtasks\": [\"ST-001-parse\", \"ST-002-render\"] }");

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(["ST-001-parse", "ST-002-render"], entries.Select(entry => entry.Title));
    }

    // ---------------------------------------------------------------------------------------
    // Requirement 2.12 / design issue 4: trim FIRST, then de-duplicate ordinal-ignore-case,
    // keeping the FIRST occurrence, so Total counts unique titles.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TryReadIndex_TrimsEntriesBeforeDeduplicatingAndKeepsTheFirstOccurrence()
    {
        // Every detail here is load-bearing:
        //  - "  ST-002-render  " and "ST-002-RENDER" collide only if entries are trimmed BEFORE
        //    comparison and the comparison ignores case;
        //  - the surviving title must be the FIRST occurrence's trimmed text, "ST-002-render" in
        //    lower case, which a keep-the-last implementation cannot produce;
        //  - the survivor must stay in the FIRST occurrence's slot, which is position 0 here while
        //    the duplicate sits at position 2, so a keep-the-last implementation that also moves
        //    the entry is caught by the order assertion as well;
        //  - the order is not alphabetical, so a sorting implementation is caught too.
        WriteIndex(
            """
            {
              "subtasks": [
                "  ST-002-render  ",
                "ST-004-verify",
                "ST-002-RENDER",
                "ST-001-parse",
                "\tST-004-Verify\t",
                "ST-003-persist"
              ]
            }
            """);

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(
            ["ST-002-render", "ST-004-verify", "ST-001-parse", "ST-003-persist"],
            entries.Select(entry => entry.Title));
    }

    // Requirement 2.12: the total the snapshot reports is the number of UNIQUE titles, not the
    // number of lines in the file. 7 listed, 3 unique - three distinct numbers (7, 3, 0 failures)
    // so a total taken from the raw array length is caught.
    [Fact]
    public void TryReadIndex_ReportsATotalOfUniqueTitlesOnly()
    {
        WriteIndex(
            """
            {
              "subtasks": ["ST-003-persist", "ST-001-parse", "ST-003-persist", "ST-002-render",
                           "ST-001-PARSE", "ST-003-PERSIST", "st-002-render"]
            }
            """);

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);

        var snapshot = new SubtaskSnapshot(entries);

        Assert.Equal(3, snapshot.Total);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(["ST-003-persist", "ST-001-parse", "ST-002-render"], snapshot.States.Select(s => s.Title));
    }

    // ---------------------------------------------------------------------------------------
    // Requirement 2.10 / design issue 4: a bad entry fails THAT ENTRY ONLY. It is kept in the
    // result as a Failed state carrying a reason; it is neither dropped nor does it invalidate
    // the index.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TryReadIndex_KeepsAnUnsafeEntryAsAFailedEntryAndAllOtherEntriesUsable()
    {
        // Asymmetric on every dimension this step evaluates: 5 entries, 1 Failed, 4 Pending, and
        // the failed entry sits at position 2 - neither first nor last - so an implementation that
        // dropped it, or that failed the wrong slot, changes the sequence visibly.
        WriteIndex(
            """
            {
              "subtasks": ["ST-004-verify", "ST-001-parse", "..\\..\\Windows", "ST-003-persist", "ST-002-render"]
            }
            """);

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(5, entries.Count);
        Assert.Equal(
            ["ST-004-verify", "ST-001-parse", @"..\..\Windows", "ST-003-persist", "ST-002-render"],
            entries.Select(entry => entry.Title));
        Assert.Equal(
            [
                SubtaskStatus.Pending,
                SubtaskStatus.Pending,
                SubtaskStatus.Failed,
                SubtaskStatus.Pending,
                SubtaskStatus.Pending,
            ],
            entries.Select(entry => entry.Status));

        Assert.False(string.IsNullOrWhiteSpace(entries[2].FailReason));
        Assert.Null(entries[0].FailReason);
        Assert.Null(entries[4].FailReason);
    }

    // The two exact dot-names are unsafe; a title that merely CONTAINS dots is an ordinary folder
    // name (requirement 2.10, carried over from task 1.1).
    [Theory]
    [InlineData(".", SubtaskStatus.Failed)]
    [InlineData("..", SubtaskStatus.Failed)]
    [InlineData("...", SubtaskStatus.Pending)]
    [InlineData("ST-003-retry..fallback", SubtaskStatus.Pending)]
    [InlineData("ST-003:retry", SubtaskStatus.Failed)]
    [InlineData("ST-003/retry", SubtaskStatus.Failed)]
    [InlineData("ST-003|retry", SubtaskStatus.Failed)]
    public void TryReadIndex_JudgesEachTitleExactlyAsTheTitleValidatorDoes(string title, SubtaskStatus expected)
    {
        WriteIndex($$"""{ "subtasks": ["ST-001-parse", {{System.Text.Json.JsonSerializer.Serialize(title)}}] }""");

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal(SubtaskStatus.Pending, entries[0].Status);
        Assert.Equal(expected, entries[1].Status);
    }

    // A blank, whitespace-only or JSON-null entry is kept as a Failed entry too: 4 entries listed,
    // 3 usable, 1 failed - all three numbers distinct.
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    [InlineData("42")]
    public void TryReadIndex_KeepsABlankEntryAsAFailedEntryWithoutInvalidatingTheIndex(string element)
    {
        WriteIndex($$"""{ "subtasks": ["ST-002-render", {{element}}, "ST-001-parse", "ST-003-persist"] }""");

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(4, entries.Count);

        var snapshot = new SubtaskSnapshot(entries);
        Assert.Equal(4, snapshot.Total);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);

        Assert.Equal(SubtaskStatus.Failed, entries[1].Status);
        Assert.False(string.IsNullOrWhiteSpace(entries[1].FailReason));
        Assert.Equal(
            ["ST-002-render", "ST-001-parse", "ST-003-persist"],
            entries.Where(entry => entry.Status == SubtaskStatus.Pending).Select(entry => entry.Title));
    }

    // An index made up exclusively of bad entries is still a READABLE index: it is not the same
    // outcome as an unusable one, and the caller must be able to tell the two apart.
    [Fact]
    public void TryReadIndex_StaysUsableWhenEveryEntryIsBad()
    {
        WriteIndex("""{ "subtasks": ["..", "ST-001/parse"] }""");

        var entries = SubtaskLedger.TryReadIndex(_paths);

        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(SubtaskStatus.Failed, entry.Status));
    }

    [Fact]
    public void TryReadIndex_RejectsANullPathSet() =>
        Assert.Throws<ArgumentNullException>(() => SubtaskLedger.TryReadIndex(null!));
}
