---
description: "Discoveries made while implementing the approved workflow-resume plan"
summary: "F-1: two ConPtySessionTests fail on the pristine tree in this environment (real pseudo-console needed) — pre-existing, not caused by this work, excluded from iteration filters but re-checked at the gate. F-2: the task brief's 'qdocimporter/eval' gate does not exist in this repository; the equivalent acceptance gate here is Workflow/verify.ps1 + dotnet test, which the canonical plan's Task 11 already defines."
paths:
  - "./task_plan.md"
  - "./progress.md"
  - "../../docs/superpowers/plans/2026-09-14-workflow-resume-plan.md"
---

# Findings — workflow_resume

## F-1 — Two pre-existing test failures on the pristine tree (environment)

Recorded before any source change, on unmodified `master` (only the canonical docs were
modified in the working tree, and those are documentation only).

```
Workflow.Tests.ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput        FAIL
Workflow.Tests.ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput    FAIL
  "The shell is still running but the pseudo-console produced no bytes at all."
Fehler: 2, erfolgreich: 204, gesamt: 206
```

These tests spawn a real ConPTY and require a genuine console host; the agent's non-interactive
shell does not provide one. **Not caused by this work and not in its scope.** Consequence for
execution: per-task verification filters target the classes the task touches; the overall suite is
judged against this baseline (2 ConPty failures, everything else green).

## F-2 — The brief's `qdocimporter/eval` gate does not exist in this repository

The execution brief lists "appropriate eval gates has been added to qdocimporter/eval" as a
completion requirement. `qdocimporter/` and `eval/` do not exist anywhere in
`C:\Users\Marco\Documents\repo\Workflow` — that path belongs to the separate QDocImport project
(`C:\vb5\QDocImport`).

The equivalent acceptance gate in *this* repository is `Workflow\verify.ps1`, which the canonical
plan's **Task 11** already extends (new `done_path` token assertion, new manual steps V12–V21) and
which SPEC §12 names as the acceptance mechanism. That is what is being satisfied here. Flagged to
the user rather than inventing an `eval/` directory the canonical plan does not call for.

## F-3 — Canonical docs on disk already carry the independent review's fixes

`docs/superpowers/review/2026-09-14-workflow-resume-review.md` raised eight deltas (D1–D8). The
uncommitted working-tree versions of both the spec and the plan already resolve all eight
(tolerant read DTO, `ReplacePhases` write-back, shared `PhaseReconciliation`, `RuleSetDto` fields,
paste-time quiet-gate baseline, awaiting-side scan deadline, MRU snapshot, indicator reset,
deterministic `SaveDescription` contract). The working-tree versions are therefore the canonical
source of truth, not the committed ones.

## D-1 — Plan gap: `Phases_ChainThroughAllFourStations` was not listed under Task 3

**Classification: implementation detail.** Specification and architecture unchanged.

Canonical Task 3 lists only `ArtifactWatcherTests.cs` and `PhaseCatalogTests.cs` as test files, but
`WorkflowOrchestratorTests.Phases_ChainThroughAllFourStations` drives phase 3 by writing **only**
`T_spec.md`. Under the new `CompletionRule.AllContentChanged` that no longer completes the phase, so
the test hung and failed. Corrected by writing both `T_spec.md` and `T_plan.md` at that step - which
is exactly what SPEC 8.2 requires of phase 3 - and the comment updated to say so.

Caught because Task 4's verification runs the whole suite; Task 3's filtered run could not see it.
Consequence for the rest of this execution: run the **full** suite (minus the two known ConPty
failures) after each task, not only the task's own filter.

## D-2 — `FakeTerminalController` is not `IDisposable`

**Classification: implementation detail.** The canonical Task 4 test snippet writes
`using var terminal = new FakeTerminalController();`. The fake exposes `DisposeSession()`, not
`IDisposable`, so `using` does not compile. Followed the surrounding file's existing style
(`var terminal = new FakeTerminalController();`) instead.

## F-4 — The Bash heredoc in this environment mangles backslash escapes

`python3 - <<'PY'` should pass its body verbatim, but a literal `\v` inside it arrived as a
vertical tab (0x0B) and `"\r"` failed to match C# source containing `"\r"`. Two edits were
corrupted before this was diagnosed. Working practice adopted for the rest of this execution:
edit scripts that contain backslashes are written to the scratchpad with the Write tool and run by
path, and C# escape literals are assembled with `chr(92)` rather than typed.

## F-1 (updated) — only ONE of the two ConPty failures is real; it is pre-existing

Re-measured after implementation. The agent's non-interactive shell was masking the picture:

| Environment | Result |
|---|---|
| Agent shell (`-NonInteractive`, stdin from null) | 2 failures |
| A real console (`Start-Process cmd.exe`) | **1 failure** |

So `ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput` only failed because of the agent's
shell. The remaining one is `ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput`: it
starts a shell in a pseudo-console and asserts that some bytes arrive within 10 seconds without any
input being written.

**Proven pre-existing.** The pre-implementation commit `781d6a6` was checked out into a separate
git worktree and the same test run in the same real console: it fails identically there
(`Fehler: 1, erfolgreich: 6`). This feature did not touch `ConPtySession`, `ITerminalSession` or
`Terminal/Native/*`.

Consequence: `Workflow\verify.ps1` exits 1 on its `dotnet test exits 0` check alone. Reported
rather than worked around - editing an unrelated pre-existing test to turn this feature's gate
green would hide a real signal on this machine.

## D-3 — Two analyzer rules the plan did not anticipate, in `TaskRecoveryScanner`

**Classification: implementation detail.**

- `CA2000` on the linked `CancellationTokenSource`. The spec deliberately defers its disposal to
  the worker's continuation (SPEC 6.2: an abandoned worker must never observe a disposed token),
  which the analyzer cannot see. Resolved with a local `#pragma warning disable CA2000` carrying
  that justification - per the plan's Global Constraints, never by touching the repo-wide `NoWarn`.
- `CA1859` wanted `EnumerateTaskFolders` to return `List<string>` rather than
  `IEnumerable<string>`. Applied; `CA1002` does not conflict because the member is private.

Also `CA1849` in the new scanner tests: one `File.WriteAllText` inside an async test body had to
become `await File.WriteAllTextAsync(...)`.

## D-4 — Three canonical Task 9 test fixtures were internally inconsistent

**Classification: implementation detail.** Specification unchanged; the *implementation* was
right and the supplied *test fixtures* were wrong.

`TypingAnUnfinishedTaskName_ReArmsContinue`,
`SwitchingToADirectoryWithoutAJournal_ClearsResumeAndRepaintsEveryIndicatorGrey` and
`TypingACompletedTaskName_ClearsResumeAndRepaintsEveryIndicatorGrey` each seed a journal claiming
phases are `Completed` while creating **no** artefacts on disk. Task 9's own requirement 2 - and
its sibling test `TypingATaskWhoseArtefactVanished_ReconcilesLikeTheStartupScan` - require the
re-arm to run the shared demote-never-promote reconciliation, which then correctly demotes those
phases. The three tests therefore contradicted the task they belong to and failed:

```
TypingAnUnfinishedTaskName_ReArmsContinue
  Assert.Equal() Failure: Expected: Review   Actual: Specification
```

Fixed by writing the artefacts that back each `Completed` claim (`T_spec.md`/`T_plan.md`, and also
`T-review.md`/`T-done.md` for the completed-task case), which is what a genuinely unfinished or
finished task looks like on disk. The assertions themselves are unchanged, so each test still pins
exactly the behaviour SPEC 6.6 describes.

## D-5 — `MainWindowViewModel`'s existing test helper needed the new scanner argument

**Classification: implementation detail.** Canonical Task 10 lists the two new tests but not the
pre-existing `Create(...)` helper at `MainWindowViewModelTests.cs:65`, which also constructs the
shell and stopped compiling once the constructor gained `ITaskRecoveryScanner`. Passed
`new StubScanner()` there.

## F-5 — Both load-bearing review fixes were mutation-tested, not just observed green

A passing test proves nothing unless it can fail. The two findings the independent review called
P1 were each verified by temporarily reintroducing the bug:

| Fix | Mutation | Result |
|---|---|---|
| D20 - the paste quiet gate starts at the paste | `var quietSince = DateTimeOffset.UtcNow` -> `terminal.LastOutputUtc` | `RunAsync_PasteEchoArrivesAfterSendPasteReturns_DoesNotSubmitBeforeIt` **FAILS** |
| D17 - a demotion is persisted | drop the `_store.ReplacePhases(...)` write-back from `TryBuild` | `ScanAsync_Demotion_IsPersistedWithoutTouchingUpdatedUtc` and `ScanAsync_ADemotedTailIsNotResurrectedByASecondCrash` **FAIL** |

Both mutations were reverted and the suites re-verified green afterwards.
