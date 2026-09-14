---
description: "Chronological work log for the workflow-resume implementation: changed files, tests run, validation results, errors"
summary: "Append-only log, one entry per canonical task. Baseline recorded 2026-09-14: Release build 0 warnings; dotnet test 204 pass / 2 pre-existing ConPtySessionTests failures (see findings F-1)."
paths:
  - "./task_plan.md"
  - "./findings.md"
---

# Progress — workflow_resume

## 2026-09-14 — Setup

- Read canonical SPEC (`docs/superpowers/specs/2026-09-14-workflow-resume-design.md`, 1160 lines)
  and the independent review (`docs/superpowers/review/2026-09-14-workflow-resume-review.md`).
- Read canonical PLAN structure (11 tasks) and Task 1–3 + Task 11 + coverage appendix in full.
- Confirmed the working-tree (uncommitted) spec/plan already resolve review deltas D1–D8 → F-3.
- Baseline validation on the pristine tree:
  - `dotnet build Workflow.sln -c Release` → exit 0, **0 Warnung(en), 0 Fehler**.
  - `dotnet test Workflow.sln -c Release` → **204 passed, 2 failed** (ConPtySessionTests, real-PTY,
    pre-existing → findings F-1).
- Created PWF state: `plans/workflow_resume/{task_plan,findings,progress}.md`.

## 2026-09-14 — Tasks 1-4

### Task 1 — `TaskPaths` journal + done marker (commit `ed24583`)
Changed: `Workflow/Models/TaskPaths.cs`, `Workflow.Tests/TaskPathsTests.cs`.
TDD: test `Constructor_DerivesTheJournalAndDoneMarkerPaths` written first; failed with
`CS1061 ... keine Definition fuer "StateAbsolute"`; implemented `DoneAbsolute`, `DoneRelative`,
`StateAbsolute`; **9 passed, 0 failed**.

### Task 2 — the `{done_path}` token (commit `d19c7e1`)
Changed: `Workflow/Services/PromptTemplateService.cs`, `Workflow/Prompt/implementation_prompt.md`,
`Workflow/verify.ps1`, `Workflow.Tests/PromptTemplateServiceTests.cs`.
TDD: `For_MapsDonePathToTheRelativeMarkerPath` failed with
`KeyNotFoundException: The given key 'done_path' was not present`; implemented the token, appended
the "Signalling completion" section, extended `$known` and added the `{done_path}` assertion.
Validation: full suite (minus ConPty) **201 passed, 0 failed**.
Error encountered and fixed: the appended prompt text initially contained a vertical-tab where
`Workflowerify.ps1` belonged (backslash-v mangled through the shell heredoc); repaired and
verified byte-exact before committing.

### Task 3 — phase 3 needs BOTH files (commit `5cf26f0`)
Changed: `Workflow/Models/WorkflowPhase.cs` (+`AllContentChanged`),
`Workflow/Services/ArtifactWatcher.cs` (`_paths.All(HasChangedSinceBaseline)` arm),
`Workflow/Models/PhaseCatalog.cs`, plus the two test files.
TDD: failed with `CS0117 ... keine Definition fuer "AllContentChanged"`; after implementing,
**19 passed, 0 failed**.

### Task 4 — phase 4 done marker; `CompletionRule.Manual` removed (commit `2fcd742`)
Changed: `Workflow/Models/PhaseCatalog.cs`, `Workflow/Models/WorkflowPhase.cs`,
`Workflow/Services/ArtifactWatcher.cs`, `Workflow/Services/WorkflowOrchestrator.cs`,
`Workflow.Tests/{PhaseCatalogTests,ArtifactWatcherTests,WorkflowOrchestratorTests}.cs`.
TDD: 3 failures first (catalogue row, new stale-marker test, and the chain test exposed by D-1).
Implemented `WatchedPaths` -> `[paths.DoneAbsolute]`, `DeleteStaleDoneMarker` before the watcher is
armed, deleted `CompletionRule.Manual` and its watcher early-return, collapsed the completion block
to a single `await await Task.WhenAny(...)`.
Validation: full suite (minus ConPty) **203 passed, 0 failed**, build 0 warnings.
Discovery: D-1 (plan gap, corrected), D-2 (fake is not IDisposable).

## 2026-09-14 — Tasks 5-7

### Task 5 — the journal (commit `aa1f362`)
Created: `Workflow/Models/TaskState.cs`, `Workflow/Services/ITaskStateStore.cs`,
`Workflow/Services/TaskStateStore.cs`, `Workflow.Tests/TaskStateStoreTests.cs`.
TDD: 16 tests written first; failed to compile (`CS0246 TaskStateStore`). Implemented the tolerant
read DTO (`TaskStateDto`/`TaskPhaseStateDto` with `string?` enums + `Enum.TryParse` +
`Enum.IsDefined`), `Normalise` to the four catalogue phases, the atomic tmp+Move `Save`, and
`ReplacePhases` with `stampUpdated: false`.
Validation: `TaskStateStoreTests` **16 passed**; full suite **219 passed, 0 failed**;
`dotnet build -c Release` **0 Warnung(en), 0 Fehler**.

### Task 6 — orchestrator resume + journal writes (commit `6d484c8`)
Changed: `Workflow/Services/IWorkflowOrchestrator.cs` (`StartPhase` defaulted positional param),
`Workflow/Services/WorkflowOrchestrator.cs` (`ITaskStateStore` ctor param, `Enum.IsDefined`
validation, `ClearPhasesFrom`, `PhaseCatalog.All.Skip((int)StartPhase)`, store-first
`RecordPhase` pairs), `Workflow/App.xaml.cs` (shared `TaskStateStore` instance).
Created: `Workflow.Tests/Fakes/FakeTaskStateStore.cs`.
TDD: failed to compile (`CS1729` 6-arg ctor, `CS0117 StartPhase`). After implementing,
full suite **222 passed, 0 failed**.

### Task 7 — auto-submit fix (commit `1f5725d`)
Changed: `Workflow/Models/AutoAnswerRule.cs` (+4 defaulted params),
`Workflow/Services/AutoAnswerService.cs` (**the 4 fields on the private `RuleSetDto`** plus the
zero/negative clamp - this is the half that actually makes file values arrive),
`Workflow/Assets/autoanswer.rules.json` (version 2 + the 4 tunables),
`Workflow/Services/ITerminalController.cs` (`SendPasteAsync` -> `void SendPaste`),
`Workflow/ViewModels/TerminalViewModel.cs` (deleted `SubmitDelay`, `CarriageReturn` and the whole
session-identity submit dance), `Workflow/Services/WorkflowOrchestrator.cs` (new
`SendPromptAsync`), `Workflow.Tests/Fakes/FakeTerminalController.cs` (`SendPaste`,
`PasteEchoDelay`, `EmitOutputOnNextCarriageReturn`, `OutputCountAtSend`).
TDD: config tests failed with `CS1061 PasteQuietPeriodMs`; submit tests failed to compile on the
interface change.
Validation: full suite **228 passed, 0 failed**; Release build **0 warnings**.
**Mutation check on the load-bearing D20 fix:** replacing `var quietSince = DateTimeOffset.UtcNow;`
with `terminal.LastOutputUtc` makes
`RunAsync_PasteEchoArrivesAfterSendPasteReturns_DoesNotSubmitBeforeIt` fail (1 failed, 0 passed),
then restored and re-verified green. The guard is genuine, not incidentally passing.
Removed: the obsolete `SubmitsCompletedBeforeNextSession` assertion - the session-identity hazard
it pinned is now structurally impossible (SPEC 9.3).

## 2026-09-14 — Tasks 8-11

### Task 8 — PhaseReconciliation + TaskRecoveryScanner (commit `b8c919e`)
Created: `Workflow/Models/RecoverableTask.cs`, `Workflow/Models/PhaseReconciliation.cs`,
`Workflow/Services/ITaskRecoveryScanner.cs`, `Workflow/Services/TaskRecoveryScanner.cs`,
`Workflow.Tests/TaskRecoveryScannerTests.cs`.
TDD: failed to compile (`CS0246 TaskRecoveryScanner`), then two analyzer errors the plan did not
anticipate (D-3): `CA2000` on the linked CTS and `CA1859` on `EnumerateTaskFolders`, plus `CA1849`
in a test. All three resolved without touching the repo-wide `NoWarn`.
Validation: scanner tests **14 passed**; full suite **242 passed, 0 failed**; Release build
**0 warnings**.
Mutation check (D17): removing the `ReplacePhases` write-back makes
`ScanAsync_Demotion_IsPersistedWithoutTouchingUpdatedUtc` and
`ScanAsync_ADemotedTailIsNotResurrectedByASecondCrash` fail; reverted and re-verified green.

### Task 9 — the resumable tab (commit `cdea6e3`)
Changed: `Workflow/ViewModels/TaskTabViewModel.cs` (`ResumePhase`, `IsRecovered`,
`StartButtonLabel`, `LoadForResume`, `NotifyClosedByUser`, `ApplyJournal`,
`RefreshResumeStateFromJournal` on BOTH `SyncFolder` exits, `ResetPhaseIndicators`,
`SaveDescription` + `StartPhase` in `StartWorkflow`),
`Workflow/Services/TaskTabViewModelFactory.cs`, `Workflow/Views/TaskTabView.xaml`
(`Text="{Binding StartButtonLabel}"`), `Workflow/App.xaml.cs`, and the four test files carrying the
constructor churn.
TDD: failed to compile, then **3 test failures** traced to inconsistent canonical fixtures (D-4) -
journals claiming `Completed` with no artefacts on disk, which the mandated reconciliation
correctly demotes. Fixtures corrected; assertions untouched.
Validation: full suite **252 passed, 0 failed**; Release build **0 warnings**.

### Task 10 — startup wiring (commit `046033a`)
Changed: `Workflow/ViewModels/MainWindowViewModel.cs` (`ITaskRecoveryScanner`, `InitialiseAsync`,
`NotifyClosedByUser` in `CloseTab` only), `Workflow/App.xaml.cs` (scanner construction, fire-and-
forget `InitialiseAsync` after `window.Show()`), `Workflow.Tests/MainWindowViewModelTests.cs`.
TDD: failed to compile (`CS7036`), plus the pre-existing `Create(...)` helper needed the new
argument (D-5).
Validation: full suite **254 passed, 0 failed**.

### Task 11 — acceptance gate (commit `f6c93af`)
Changed: `Workflow/verify.ps1` (header to V5-V21, manual steps V12-V21 appended).

**Step 2 - the full gate**, run in a real console
(`Start-Process cmd.exe -> pwsh -File Workflow\verify.ps1`):

```
PASS  dotnet build exits 0
PASS  build produced no warnings
FAIL  dotnet test exits 0        <- Fehler: 1, erfolgreich: 260, gesamt: 261
PASS  (10x) present and non-empty: ... output manifest
PASS  (all) prompt-token checks, including {done_path}
PASS  implementation_prompt.md uses {done_path}
FAILED: 1 check(s)                exit 1
```

The single failure is `ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput`. **Proven
pre-existing**: the pre-implementation commit `781d6a6` was checked out into a git worktree and the
same test run in the same real console, failing identically (`Fehler: 1, erfolgreich: 6`). The
worktree was then removed. See findings F-1. Not worked around.

**Step 3 - review-driven acceptance criteria.** All 11 named tests (A14-A18 plus SPEC 5.2, 9.3 and
the two 6.2 contract tests) run together: **11 passed, 0 failed**.

**Step 4 - the running app.** `Workflow.exe` (Release) was launched: the window appeared
(`MainWindowTitle = 'Workflow'`) with the startup recovery scan wired in, and it closed cleanly on
`CloseMainWindow()`.

### Final state
- 11 commits, `ed24583`..`f6c93af`, one per canonical task.
- Release build: **0 warnings, 0 errors**. No entry added to the repository-wide `NoWarn`; the one
  suppression is a local `#pragma` with a justification (D-3).
- Tests: **260 passed, 1 failed** (pre-existing, F-1). Baseline before this work was 204 passing;
  this feature added 57 tests.

## 2026-09-14 — Final specification-compliance review

An independent reviewer (subagent, no shell access; verified by reading the final state of every
relevant source file) checked the implementation against all 12 load-bearing requirements of the
canonical spec.

**Result: no defects at or above the confidence bar. All 12 CONFIRMED-CORRECT**, each with
file:line evidence:

| # | Requirement | Evidence |
|---|---|---|
| 1 | Tolerant read DTO (5.2/5.4, D19) | TaskStateStore.cs:155-168, 173 |
| 2 | ReplacePhases no-op + no UpdatedUtc stamp (5.4) | TaskStateStore.cs:114-129 |
| 3 | MRU snapshot, awaiting-side deadline, None token, deferred CTS dispose (6.2, D21/D22) | TaskRecoveryScanner.cs:48, 66, 77, 86-90 |
| 4 | One shared PhaseReconciliation, both callers persist, ResolveReview never demoted (6.3.x, D17/D18) | PhaseReconciliation.cs:89; TaskRecoveryScanner.cs:188-191; TaskTabViewModel.cs:254-257 |
| 5 | Re-arm resets both, on both SyncFolder exits, skipped when IsRecovered (6.6, R15) | TaskTabViewModel.cs:244-247, 261-269, 429, 440 |
| 6 | Enum.IsDefined, Skip, tail cleared before the loop (7.2/7.4, D24) | WorkflowOrchestrator.cs:48-51, 58, 62, 73-89 |
| 7 | Store before Progress.Report (7.3) | WorkflowOrchestrator.cs:98-99, 149-150 |
| 8 | Phase 4 FilesExist, stale marker deleted before the watcher, Manual gone (8.1/8.3) | WorkflowOrchestrator.cs:105-108, 111; WorkflowPhase.cs:33-43 |
| 9 | Quiet gate starts at the paste, OutputCount verify, bounded retry (9.3, D20) | WorkflowOrchestrator.cs:194-224, 226-242 |
| 10 | Four tunables on the private RuleSetDto, <=0 normalised (9.4, D23) | AutoAnswerService.cs:106-115, 118-146 |
| 11 | LoadForResume order, Active downgraded to Pending (6.5) | TaskTabViewModel.cs:151-189, 229 |
| 12 | Dismissal in CloseTab only (6.7) | MainWindowViewModel.cs:114 vs 80-90 |

Nothing in the change set contradicts the specification or silently alters the architecture beyond
what SPEC section 13's decision log already records.
