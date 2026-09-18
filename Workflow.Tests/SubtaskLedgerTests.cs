using System.IO;
using System.Text;
using Workflow.Models;

namespace Workflow.Tests;

/// <remarks>
/// Two layers. The <c>TryReadIndex</c> tests cover reading and normalising the task-level ordered
/// index (task 1.3). The <c>TryRead</c> / <c>IsDecomposed</c> / <c>AllComplete</c> tests cover the
/// resolved evidence-derivation table of design "Data Models -> Tracking Files" (task 1.4).
/// </remarks>
public sealed class SubtaskLedgerTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

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
        File.WriteAllText(_paths.ResultAbsolute, json, Utf8);

    /// <summary>Writes an index listing exactly the given titles, in the given order.</summary>
    private void WriteIndexOf(params string[] titles) =>
        WriteIndex($$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(titles)}} }""");

    private void WriteStatus(string title, string json)
    {
        Directory.CreateDirectory(_paths.SubtaskDirectory(title));
        File.WriteAllText(_paths.SubtaskStatusFile(title), json, Utf8);
    }

    private void WriteDescription(string title, string content = "Beschreibung des Subtasks.")
    {
        Directory.CreateDirectory(_paths.SubtaskDirectory(title));
        File.WriteAllText(_paths.SubtaskMarkdown(title), content, Utf8);
    }

    /// <summary>Publishes a non-empty session-finished flag. Its contents are never interpreted.</summary>
    private void WriteFlag(string title)
    {
        Directory.CreateDirectory(_paths.SubtaskDirectory(title));
        File.WriteAllText(_paths.SubtaskResultFile(title), """{ "finished": true }""", Utf8);
    }

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

    // =======================================================================================
    // Task 1.4 - deriving each subtask's state from the tracking evidence. The authority is the
    // resolved derivation table in design "Data Models -> Tracking Files":
    //
    //   Unsafe title                                             -> Failed
    //   Parsed status `complete` (case-insensitive)              -> Complete
    //   Parsed status `failed` (case-insensitive)                -> Failed
    //   Any other parsed value, incl. `pending` and unknown ones -> Pending
    //   Status unreadable or malformed                           -> Failed
    //   Status absent, flag absent                               -> Pending
    //   Status absent, non-empty flag present                    -> Pending
    //   Missing or empty description                             -> Failed
    //   ... with a completion status outranking a missing description or a missing flag.
    // =======================================================================================

    // The flagship fixture is asymmetric in every dimension the component evaluates: 6 entries,
    // 3 Complete, 2 Pending, 1 Failed - four distinct numbers, so no pair of swapped counting or
    // classification branches can survive. The declared order is not alphabetical and every title
    // is distinct, so a sorted, reversed or re-grouped result is visible too.
    [Fact]
    public void TryRead_DerivesEveryRowOfTheEvidenceTable()
    {
        WriteIndexOf(
            "ST-004-verify", "ST-001-parse", "ST-006-ship", "ST-003-persist", "ST-002-render", "ST-005-audit");

        // Complete although the description was deleted afterwards and no flag was ever published.
        WriteStatus("ST-004-verify", """{ "status": "complete" }""");

        // A parsed `pending` is "not done yet", never a failure.
        WriteStatus("ST-001-parse", """{ "status": "pending" }""");
        WriteDescription("ST-001-parse");

        // The only real failure in the fixture, and the only entry that carries a payload reason.
        WriteStatus("ST-006-ship", """{ "status": "failed", "failreason": "Build brach ab." }""");
        WriteDescription("ST-006-ship");
        WriteFlag("ST-006-ship");

        WriteStatus("ST-003-persist", """{ "status": "complete", "failreason": "veraltet" }""");
        WriteDescription("ST-003-persist");
        WriteFlag("ST-003-persist");

        // Flag published, status not (yet) there: pending, not failed.
        WriteDescription("ST-002-render");
        WriteFlag("ST-002-render");

        // Case-insensitive decisive value, and an unknown property that must be ignored.
        WriteStatus("ST-005-audit", """{ "STATUS": "COMPLETE", "notes": "ignoriert" }""");
        WriteDescription("ST-005-audit");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(
            ["ST-004-verify", "ST-001-parse", "ST-006-ship", "ST-003-persist", "ST-002-render", "ST-005-audit"],
            snapshot.States.Select(state => state.Title));
        Assert.Equal(
            [
                SubtaskStatus.Complete,
                SubtaskStatus.Pending,
                SubtaskStatus.Failed,
                SubtaskStatus.Complete,
                SubtaskStatus.Pending,
                SubtaskStatus.Complete,
            ],
            snapshot.States.Select(state => state.Status));

        Assert.Equal(6, snapshot.Total);
        Assert.Equal(3, snapshot.Completed);
        Assert.Equal(1, snapshot.Failed);

        // Only the failed entry explains itself; a completed entry never carries a stale reason.
        Assert.Equal("Build brach ab.", snapshot.States[2].FailReason);
        Assert.Null(snapshot.States[0].FailReason);
        Assert.Null(snapshot.States[3].FailReason);
        Assert.Null(snapshot.States[4].FailReason);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("Complete")]
    [InlineData("COMPLETE")]
    [InlineData("cOmPlEtE")]
    public void TryRead_TreatsACompleteStatusAsCompleteRegardlessOfCase(string value)
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus("ST-001-parse", $$"""{ "status": {{System.Text.Json.JsonSerializer.Serialize(value)}} }""");
        WriteDescription("ST-001-parse");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(SubtaskStatus.Complete, Assert.Single(snapshot.States).Status);
        Assert.Equal(1, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("Failed")]
    [InlineData("FAILED")]
    public void TryRead_TreatsAFailedStatusAsFailedRegardlessOfCase(string value)
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus(
            "ST-001-parse",
            $$"""{ "status": {{System.Text.Json.JsonSerializer.Serialize(value)}}, "FailReason": "Testlauf rot." }""");
        WriteDescription("ST-001-parse");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        var state = Assert.Single(snapshot.States);
        Assert.Equal(SubtaskStatus.Failed, state.Status);

        // Requirement 3.7: the payload's own reason is what reaches the tooltip, verbatim.
        Assert.Equal("Testlauf rot.", state.FailReason);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
    }

    [Fact]
    public void TryRead_StillExplainsAFailedStatusThatCarriesNoReason()
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus("ST-001-parse", """{ "status": "failed" }""");
        WriteDescription("ST-001-parse");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        var state = Assert.Single(snapshot.States);
        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.False(string.IsNullOrWhiteSpace(state.FailReason));
    }

    // Design issue 1: the vocabulary is closed on the two decisive values. EVERYTHING else that
    // parses is "not done yet" - never a failure, and never a completion.
    [Theory]
    [InlineData("""{ "status": "pending" }""")]
    [InlineData("""{ "status": "Pending" }""")]
    [InlineData("""{ "status": "running" }""")]
    [InlineData("""{ "status": "in_progress" }""")]
    [InlineData("""{ "status": "done" }""")]
    [InlineData("""{ "status": "success" }""")]
    [InlineData("""{ "status": "completed" }""")]
    [InlineData("""{ "status": "fail" }""")]
    [InlineData("""{ "status": "" }""")]
    [InlineData("""{ "status": 42 }""")]
    [InlineData("""{ "status": null }""")]
    [InlineData("""{ "failreason": "ohne Status" }""")]
    [InlineData("{ }")]
    public void TryRead_TreatsEveryOtherParsedStatusValueAsPending(string payload)
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus("ST-001-parse", payload);
        WriteDescription("ST-001-parse");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        var state = Assert.Single(snapshot.States);
        Assert.Equal(SubtaskStatus.Pending, state.Status);
        Assert.Null(state.FailReason);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
    }

    // Two table rows at once: "status absent, flag absent" and "status absent, non-empty flag
    // present" are BOTH Pending. The flag is the orchestrator's settling trigger, not evidence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryRead_TreatsAnAbsentStatusAsPendingWhetherOrNotTheFlagExists(bool publishFlag)
    {
        WriteIndexOf("ST-001-parse");
        WriteDescription("ST-001-parse");
        if (publishFlag)
        {
            WriteFlag("ST-001-parse");
        }

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(SubtaskStatus.Pending, Assert.Single(snapshot.States).Status);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
    }

    // "Status unreadable or malformed -> Failed". Settling is the caller's job (issue 10); the
    // ledger reports what it sees right now.
    [Theory]
    [InlineData("""{ "status": "complete" """)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""["complete"]""")]
    [InlineData("\"complete\"")]
    [InlineData("nicht einmal JSON")]
    public void TryRead_TreatsAMalformedStatusAsFailed(string payload)
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus("ST-001-parse", payload);
        WriteDescription("ST-001-parse");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        var state = Assert.Single(snapshot.States);
        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.False(string.IsNullOrWhiteSpace(state.FailReason));
        Assert.Equal(1, snapshot.Failed);
    }

    [Fact]
    public void TryRead_TreatsAnUnreadableStatusAsFailed()
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus("ST-001-parse", """{ "status": "complete" }""");
        WriteDescription("ST-001-parse");

        using var exclusive = new FileStream(
            _paths.SubtaskStatusFile("ST-001-parse"), FileMode.Open, FileAccess.Read, FileShare.None);

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        var state = Assert.Single(snapshot.States);
        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.False(string.IsNullOrWhiteSpace(state.FailReason));
    }

    // "Missing or empty description -> Failed" (requirement 2.10, E6) for an UNFINISHED entry.
    [Theory]
    [InlineData("absent-folder")]
    [InlineData("absent-file")]
    [InlineData("empty-file")]
    [InlineData("blank-file")]
    public void TryRead_FailsAnUnfinishedEntryWithoutAUsableDescription(string shape)
    {
        WriteIndexOf("ST-001-parse");
        switch (shape)
        {
            case "absent-file":
                Directory.CreateDirectory(_paths.SubtaskDirectory("ST-001-parse"));
                break;
            case "empty-file":
                WriteDescription("ST-001-parse", string.Empty);
                break;
            case "blank-file":
                WriteDescription("ST-001-parse", " \r\n\t ");
                break;
            default:
                break;
        }

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        var state = Assert.Single(snapshot.States);
        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.False(string.IsNullOrWhiteSpace(state.FailReason));
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
    }

    // The precedence rule, stated once in the design and pinned here in both of its forms:
    // a completion status outranks a deleted description AND a never-published flag (4.4).
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TryRead_LetsACompletionStatusOutrankAMissingDescriptionAndAMissingFlag(
        bool describe, bool publishFlag)
    {
        WriteIndexOf("ST-001-parse");
        WriteStatus("ST-001-parse", """{ "status": "complete" }""");
        if (describe)
        {
            WriteDescription("ST-001-parse");
        }

        if (publishFlag)
        {
            WriteFlag("ST-001-parse");
        }

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(SubtaskStatus.Complete, Assert.Single(snapshot.States).Status);
        Assert.Equal(1, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
    }

    // The task's own observable: a freshly decomposed folder - every status `pending`, every
    // description written, no flag anywhere - reports ZERO failures and ZERO completions, so
    // requirement 3.3's "{K} fehlgeschlagen" never appears for a run that has not started.
    [Fact]
    public void TryRead_LeavesAFreshlyDecomposedFolderAtZeroFailuresAndZeroCompletions()
    {
        WriteIndexOf("ST-003-persist", "ST-001-parse", "ST-004-verify", "ST-002-render", "ST-005-audit");
        foreach (var title in new[]
                 {
                     "ST-003-persist", "ST-001-parse", "ST-004-verify", "ST-002-render", "ST-005-audit",
                 })
        {
            WriteStatus(title, """{ "status": "pending" }""");
            WriteDescription(title);
        }

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot.Total);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
        Assert.All(snapshot.States, state => Assert.Equal(SubtaskStatus.Pending, state.Status));
        Assert.All(snapshot.States, state => Assert.Null(state.FailReason));
        Assert.Equal(
            ["ST-003-persist", "ST-001-parse", "ST-004-verify", "ST-002-render", "ST-005-audit"],
            snapshot.States.Select(state => state.Title));
    }

    // An unsafe title stays Failed with its title-level reason and never reaches the filesystem;
    // the surrounding entries are derived normally. 4 entries, 1 Complete, 1 Failed, 2 Pending.
    [Fact]
    public void TryRead_KeepsAnUnsafeTitleFailedAndDerivesTheOtherEntriesNormally()
    {
        WriteIndex(
            """
            { "subtasks": ["ST-002-render", "..\\..\\Windows", "ST-001-parse", "ST-003-persist"] }
            """);

        WriteStatus("ST-002-render", """{ "status": "complete" }""");
        WriteStatus("ST-001-parse", """{ "status": "pending" }""");
        WriteDescription("ST-001-parse");
        WriteDescription("ST-003-persist");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot.Total);
        Assert.Equal(1, snapshot.Completed);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(
            ["ST-002-render", @"..\..\Windows", "ST-001-parse", "ST-003-persist"],
            snapshot.States.Select(state => state.Title));
        Assert.Equal(
            [SubtaskStatus.Complete, SubtaskStatus.Failed, SubtaskStatus.Pending, SubtaskStatus.Pending],
            snapshot.States.Select(state => state.Status));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.States[1].FailReason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"subtasks\": [] }")]
    [InlineData("{ \"version\": 2, \"subtasks\": [\"ST-001-parse\"] }")]
    public void TryRead_ReportsAnUnusableIndexAsNull(string json)
    {
        WriteIndex(json);

        Assert.Null(SubtaskLedger.TryRead(_paths));
    }

    [Fact]
    public void TryRead_ReportsAnAbsentIndexAsNull() => Assert.Null(SubtaskLedger.TryRead(_paths));

    [Fact]
    public void TryRead_RejectsANullPathSet() =>
        Assert.Throws<ArgumentNullException>(() => SubtaskLedger.TryRead(null!));

    // ---------------------------------------------------------------------------------------
    // IsDecomposed (requirement 2.3, design issue 5): the readable ordered index is the ONLY
    // condition. Descriptions are explicitly not required, so an agent tidying up finished
    // subtasks cannot destroy a curated index.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void IsDecomposed_ReportsReuseWithoutRequiringADescriptionForEveryEntry()
    {
        WriteIndexOf("ST-003-persist", "ST-001-parse", "ST-002-render");

        // Exactly one of three entries has a description, and none has a status or a flag.
        WriteDescription("ST-002-render");

        Assert.True(SubtaskLedger.IsDecomposed(_paths));

        // ... while the per-entry derivation is unforgiving about the very same folders.
        var snapshot = SubtaskLedger.TryRead(_paths);
        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.Total);
        Assert.Equal(2, snapshot.Failed);
        Assert.Equal(0, snapshot.Completed);
    }

    [Fact]
    public void IsDecomposed_ReportsReuseWhenNoSubtaskFolderExistsAtAll()
    {
        WriteIndexOf("ST-001-parse", "ST-002-render");

        Assert.True(SubtaskLedger.IsDecomposed(_paths));
    }

    [Theory]
    [InlineData("""{ "subtasks": ["ST-001-parse"] }""", true)]
    [InlineData("""{ "subtasks": ["..", ""] }""", true)]
    [InlineData("""{ "subtasks": [] }""", false)]
    [InlineData("""{ "task": "demo" }""", false)]
    [InlineData("""{ "version": 2, "subtasks": ["ST-001-parse"] }""", false)]
    [InlineData("""{ "subtasks": ["ST-001-parse" """, false)]
    public void IsDecomposed_FollowsTheIndexAlone(string json, bool expected)
    {
        WriteIndex(json);

        Assert.Equal(expected, SubtaskLedger.IsDecomposed(_paths));
    }

    [Fact]
    public void IsDecomposed_ReportsNoReuseWithoutAnIndex() => Assert.False(SubtaskLedger.IsDecomposed(_paths));

    [Fact]
    public void IsDecomposed_RejectsANullPathSet() =>
        Assert.Throws<ArgumentNullException>(() => SubtaskLedger.IsDecomposed(null!));

    // ---------------------------------------------------------------------------------------
    // AllComplete (design issue 6a): green is Completed == Total on a freshly read snapshot.
    // "No failures" alone is never enough, and an unreadable index never completes (issue 9).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AllComplete_ReportsCompletionWhenEveryEntryCarriesACompleteStatus()
    {
        WriteIndexOf("ST-003-persist", "ST-001-parse", "ST-002-render");
        WriteStatus("ST-003-persist", """{ "status": "complete" }""");
        WriteStatus("ST-001-parse", """{ "status": "COMPLETE" }""");

        // Description deleted, flag never published - the completion status still outranks both.
        WriteStatus("ST-002-render", """{ "status": "Complete" }""");

        Assert.True(SubtaskLedger.AllComplete(_paths));
    }

    [Fact]
    public void AllComplete_RefusesCompletionWhileOneEntryIsStillPending()
    {
        WriteIndexOf("ST-003-persist", "ST-001-parse", "ST-002-render");
        WriteStatus("ST-003-persist", """{ "status": "complete" }""");
        WriteStatus("ST-001-parse", """{ "status": "complete" }""");
        WriteStatus("ST-002-render", """{ "status": "pending" }""");
        WriteDescription("ST-002-render");

        // Zero failures, and still not complete: "no failures" is not the green condition.
        var snapshot = SubtaskLedger.TryRead(_paths);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.Failed);

        Assert.False(SubtaskLedger.AllComplete(_paths));
    }

    [Fact]
    public void AllComplete_RefusesCompletionWhileOneEntryIsFailed()
    {
        WriteIndexOf("ST-003-persist", "ST-001-parse", "ST-002-render");
        WriteStatus("ST-003-persist", """{ "status": "complete" }""");
        WriteStatus("ST-001-parse", """{ "status": "complete" }""");
        WriteStatus("ST-002-render", """{ "status": "failed", "failreason": "Testlauf rot." }""");
        WriteDescription("ST-002-render");

        Assert.False(SubtaskLedger.AllComplete(_paths));
    }

    [Fact]
    public void AllComplete_RefusesCompletionWithoutAReadableIndex()
    {
        // Every folder on disk says complete; without a readable index there is nothing to
        // complete, and requirement 6.1 forbids discovering the entries from the directory.
        WriteStatus("ST-001-parse", """{ "status": "complete" }""");
        WriteStatus("ST-002-render", """{ "status": "complete" }""");

        Assert.False(SubtaskLedger.AllComplete(_paths));

        WriteIndex("""{ "version": 2, "subtasks": ["ST-001-parse", "ST-002-render"] }""");

        Assert.False(SubtaskLedger.AllComplete(_paths));
    }

    [Fact]
    public void AllComplete_RejectsANullPathSet() =>
        Assert.Throws<ArgumentNullException>(() => SubtaskLedger.AllComplete(null!));

    // The ledger is uncached: two reads either side of a change on disk disagree, and nothing
    // persists a cursor between them.
    [Fact]
    public void TryRead_ReReadsTheFilesystemOnEveryCall()
    {
        WriteIndexOf("ST-001-parse", "ST-002-render");
        WriteDescription("ST-001-parse");
        WriteDescription("ST-002-render");

        var before = SubtaskLedger.TryRead(_paths);
        Assert.NotNull(before);
        Assert.Equal(0, before.Completed);
        Assert.False(SubtaskLedger.AllComplete(_paths));

        WriteStatus("ST-001-parse", """{ "status": "complete" }""");
        WriteStatus("ST-002-render", """{ "status": "complete" }""");

        var after = SubtaskLedger.TryRead(_paths);
        Assert.NotNull(after);
        Assert.Equal(2, after.Completed);
        Assert.True(SubtaskLedger.AllComplete(_paths));
    }
}
