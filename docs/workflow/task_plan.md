# Task Plan: Workflow WPF app — Superpowers implementation

This file is the execution-state layer only. The canonical source of truth for
*what* to build is:

- **Specification:** `docs/superpowers/specs/specification.md` (approved)
- **Implementation plan:** `docs/superpowers/plans/implementationplan.md` (approved, 17 TDD tasks)

Do not redesign the feature here. Each phase below is a 1:1 wrapper around one
canonical Task, referencing its line anchor in `implementationplan.md`. Re-read
the canonical section before starting or resuming a phase.

## Goal

Build the Workflow WPF (.NET 8) task-orchestration app exactly as specified in
`specification.md` and `implementationplan.md`, task-by-task, TDD, ending at
0 warnings/0 errors with `Workflow\verify.ps1` passing (spec A1–A9, F1–F21).

## Next Step

Phase 16 is complete (see below). Start Phase 15 (canonical Task 15: XAML
views, `implementationplan.md:7916`) — re-read fresh; this is what makes the
app actually render/run correctly (see findings.md — Task 14's clean build
did not mean the app works yet). Being executed via a dispatched implementer
subagent per the ruling in findings.md.

## Current Phase

Phase 15

## OPEN ITEM carried forward (must be resolved before final completion)

`ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput` and
`Start_EmitsTheLauncherFrameBeforeAnyInput` fail in this agent session
(environmental — see findings.md) and were explicitly deferred to the
user's own manual verification per their decision on 2026-09-12. The
"required tests passing" / "no unresolved implementation blockers"
completion bars in this job's instructions and spec A9/V5 are NOT
fully satisfied until the user confirms these pass in a normal
interactive session (or the app is otherwise shown to stream terminal
output correctly end-to-end). Re-surface this explicitly at Task 17
(verify.ps1) and before declaring the overall feature complete.

## Source-of-truth hierarchy (binding for this execution)

1. Approved specification (`docs/superpowers/specs/specification.md`)
2. Approved implementation plan (`docs/superpowers/plans/implementationplan.md`)
3. Current repository state
4. This file (`task_plan.md`)
5. `findings.md`
6. `progress.md`
7. Conversational context

See `findings.md` for a recorded conflict between the generic task-runner
instructions and this project's canonical spec (qdocimporter/eval), resolved
per this hierarchy.

## Phases

Each phase = one canonical Task. "Canonical task" gives the exact anchor to
re-read before implementing or resuming. Steps are not copied here — see the
canonical section for the full step list, code snippets and verification
commands.

### Phase 1: Build foundation

- Canonical task: `docs/superpowers/plans/implementationplan.md:149` (## Task 1)
- [x] git init + .gitignore
- [x] `Directory.Build.props` importing `Workflow\.roslyn`
- [x] Restore `Workflow.csproj` PackageReferences (pinned versions) + Content items
- [x] Create `Workflow.Tests` xUnit project, add to solution
- [x] `dotnet build` → 0 warnings/0 errors; probe that IDE0040 actually fires then remove probe
      (corrected probe location — see findings.md deviation)
- [x] `dotnet test` → 1 passed
- [x] Commit
- **Status:** complete

### Phase 2: Domain models and `TaskPaths`

- Canonical task: `docs/superpowers/plans/implementationplan.md:388` (## Task 2)
- [x] TDD: wrote failing tests (TaskPathsTests, PhaseCatalogTests,
      WorkingDirectoryPathTests — the latter's test content was not in the
      plan text, authored directly against the documented Normalise contract)
- [x] Verified RED (CS0234/CS0246 — Workflow.Models did not exist)
- [x] Implemented WorkflowPhase/PhaseStatus/CompletionRule enums,
      PhaseDefinition, PhaseCatalog, WorkingDirectoryPath, TaskPaths
- [x] Verified GREEN — 19/19 tests pass, solution builds 0 warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 3: `TaskFolderService`

- Canonical task: `docs/superpowers/plans/implementationplan.md:749` (## Task 3)
- [x] TDD red-green: TaskFolderServiceTests written, verified RED
      (CS0234/CS0246), implemented TaskNameValidation/ITaskFolderService/
      TaskFolderService, verified GREEN
- [x] Full suite 56/56 passed; build 0 warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 4: `PromptTemplateService` + repair the 4 prompt templates

- Canonical task: `docs/superpowers/plans/implementationplan.md:1141` (## Task 4)
- [x] Re-read all 4 shipped prompt files fresh before editing (task's own
      requirement) — confirmed they matched the plan's documented state
      exactly, including the live `qdocimporter/eval` line
- [x] review_prompt.md: dropped stray article ("produce a" -> "produce")
- [x] resolve_review_prompt.md: appended the `## Review resolution` guarantee
      paragraph before the closing line
- [x] implementation_prompt.md: removed stray `{plan_path}_` underscore;
      replaced the `qdocimporter/eval` bullet with the `verify.ps1` gate
      (spec §9.3(d)/§14 — see findings.md resolved-conflict entry)
- [x] Confirmed only the 6 known tokens remain across all 4 templates (grep)
- [x] TDD red-green: PromptTemplateServiceTests written, verified RED
      (CS0246), implemented PromptTemplateException/IPromptTemplateService/
      PromptTemplateService/PromptVariables, added Content item to
      Workflow.Tests.csproj so shipped templates reach the test host,
      verified GREEN (15/15 filtered, incl. the A8 shipped-template
      assertions and the qdocimport-absence assertion)
- [x] Full suite 71/71 passed; build 0 warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 5: Auto-answer rule engine

- Canonical task: `docs/superpowers/plans/implementationplan.md:1738` (## Task 5)
- [x] TDD red-green: EscapeDecoderTests + AutoAnswerServiceTests written,
      verified RED (CS0103/CS0246), implemented AutoAnswerRule/RuleSet,
      EscapeDecoder, IAutoAnswerService/AutoAnswerService, replaced the
      Task-1 `{}` placeholder with the real shipped rule set, added Content
      item to Workflow.Tests.csproj, verified GREEN
- [x] Full suite 92/92 passed; build 0 warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 6: `ArtifactWatcher`

- Canonical task: `docs/superpowers/plans/implementationplan.md:2298` (## Task 6)
- [x] TDD red-green: ArtifactWatcherTests written, verified RED (CS0246),
      implemented IArtifactWatcher/IArtifactWatcherFactory/ArtifactWatcher/
      ArtifactWatchException/FileHashResult
- [x] Fixed 2 real defects surfaced by the analyzer/test run (not present in
      canonical plan text) — see findings.md: (1) CA2025/CA2000 on 4 test
      methods' `using`+stored-Task pattern, fixed via try/finally disposal
      after await, no NoWarn added; (2) a test-assertion bug
      (`Assert.ThrowsAsync<OperationCanceledException>` vs the actual
      `TaskCanceledException` subtype) inconsistent with every other
      cancellation assertion in the same file — aligned to `ThrowsAnyAsync`
- [x] Full suite 104/104 passed (stable across 2 runs); build 0 warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 7: `SettingsService`

- Canonical task: `docs/superpowers/plans/implementationplan.md:2909` (## Task 7)
- [x] TDD red-green: SettingsServiceTests written, verified RED (CS0246),
      implemented AppSettings/ISettingsService/SettingsService verbatim
- [x] Full suite 116/116 passed; build 0 warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 8: ConPTY terminal session (opens with a BLOCKING spike — spec §6.3.1)

- Canonical task: `docs/superpowers/plans/implementationplan.md:3274` (## Task 8)
- No later task may start until the spike demonstrably streams output; if the
  proven sequence differs from spec §6.3, update the spec section before
  committing `ConPtySession` code (this is an allowed implementation-detail
  deviation per the plan review notes — record the actual sequence found).
- [x] Ran the Step 0 spike (standalone console app, outside the solution).
      Reproduced the spec's documented "zero bytes" finding exactly, tried
      the explicit-resize hypothesis (matches spec's 115-126 byte number
      independently), tried 2 of 3 "untested candidates" (INHERIT_CURSOR:
      regression/hang; inheritable pipe security attributes: no change).
- [x] Diagnosed likely root cause: this agent's shell tools execute in a
      disconnected Windows session (session 1), distinct from the machine's
      real interactive desktop (session 3) — see findings.md for full
      evidence chain.
- **User decision (2026-09-12):** implement `ConPtySession` exactly as
  documented, run everything, report the 2 streaming tests' actual result
  honestly, defer their final confirmation to the user's own manual
  verification. See findings.md.
- [x] Implemented native layer verbatim per plan Steps 3-6 (NativeStructs,
      NativeMethods, SafePseudoConsoleHandle, SafeProcThreadAttributeList,
      ShellLocator) + Step 7 (ITerminalSession/ITerminalSessionFactory,
      ConPtySession, ConPtySessionFactory)
- [x] Fixed 3 more real defects found during the build (not in canonical plan
      text) — see findings.md: `[DefaultDllImportSearchPaths]` on a class is
      `CS0592` (moved to assembly level in AssemblyInfo.cs); `CA1806` on the
      discarded `ResizePseudoConsole` HRESULT; `CA2000`
      (undisposed SemaphoreSlim) + `CA1508` (false-positive dead-code check
      across an async event-handler race) in `ConPtySessionTests.cs`.
- [x] 7/7 non-streaming ConPtySession + ShellLocator tests pass (start/stop/
      dispose/resize/double-start-throws/write-before-start-throws) —
      confirms the native P/Invoke layer, safe handles and process lifecycle
      are all correct.
- [x] The 2 streaming tests fail here exactly as the Step 0 spike predicted
      (same diagnostic message), consistent with the environmental
      diagnosis. Deferred to user per their decision — see OPEN ITEM above.
- [x] No orphan pwsh/powershell processes left behind (Step 9) — verified via
      Get-CimInstance parent/command-line inspection.
- [x] Full solution build 0 warnings/0 errors; full suite 123/125 (2 known,
      deferred failures)
- [x] Commit
- **Status:** complete (native layer + all non-streaming behavior); 2 tests
  deferred to user manual verification — see OPEN ITEM

### Phase 9: `WorkflowOrchestrator`

- Canonical task: `docs/superpowers/plans/implementationplan.md:4240` (## Task 9)
- [x] TDD red-green: BracketedPasteTests/FakeTerminalController/
      WorkflowOrchestratorTests written, verified RED (CS0246), implemented
      BracketedPaste/PhaseProgress/ITerminalController/ManualPhaseSignal/
      IWorkflowOrchestrator/WorkflowOrchestrator verbatim
- [x] Fixed 2 real defects in the plan's own verbatim code (findings.md):
      CA1002 (FakeTerminalController's public List<T> properties) and
      CA1865 (EndsWith(string) vs EndsWith(char))
- [x] Fixed 6 real test bugs (systematic-debugging) in
      WorkflowOrchestratorTests.cs itself — missing `ReadyGate.SetResult()`
      and/or `EmitOutput()` called before `StartSession`'s reset wiped it out
      — see findings.md
- [x] 16/16 pass, stable across 2 runs (timing-sensitive); full suite
      139/141 (2 known deferred ConPTY streaming failures); build 0/0
- [x] Commit
- **Status:** complete

### Phase 10: Terminal web assets (xterm.js vendoring)

- Canonical task: `docs/superpowers/plans/implementationplan.md:5116` (## Task 10)
- [x] Vendored @xterm/xterm 5.5.0 + @xterm/addon-fit 0.10.0 via npm pack/tar,
      copied only xterm.js/xterm.css/addon-fit.js, deleted tarballs/package
      dirs — verified exactly 5 files remain
- [x] Wrote terminal.html + terminal.js per the message protocol
- [x] TDD: TerminalAssetTests written and passed immediately (7/7) — see
      findings.md re: skipping the plan's redundant Step 5 (MSBuild already
      propagates Workflow.csproj's Content items transitively)
- [x] Full suite 146/148 (2 known deferred failures); build 0/0
- [x] Commit
- **Status:** complete

### Phase 11: WebView2 terminal host

- Canonical task: `docs/superpowers/plans/implementationplan.md:5366` (## Task 11)
- No automated tests for this task (manual only, deferred to Task 17 per the
  plan itself — WebView2 needs a message pump and a real browser process).
- [x] Implemented IWebViewEnvironmentProvider/WebViewEnvironmentProvider,
      TerminalViewModel (ITerminalController impl), TerminalView.xaml(.cs)
      verbatim per the plan
- [x] Fixed 5 real analyzer defects (findings.md): CS8602, CA1508 (double-
      checked-locking blind spot), CA1001, CA2213 x2 (Interlocked.Exchange
      dispose-tracing blind spot)
- [x] Build succeeded immediately (0/0) even referencing Task 12's
      not-yet-existing converters — clarified in findings.md why (XAML
      StaticResource keys are a runtime, not build-time, concern)
- [x] Full suite 146/148 (2 known deferred failures); build 0/0
- [x] Commit
- **Status:** complete (manual verification steps V5-V8 deferred to Task 17,
  per the plan's own instruction)

### Phase 12: Converters, behaviours and styles

- Canonical task: `docs/superpowers/plans/implementationplan.md:5979` (## Task 12)
- [x] Added `Xunit.StaFact` 1.1.11 package reference for `[StaFact]`
- [x] TDD red-green: ConverterTests + RichTextBoxAssistTests written,
      verified RED (CS0234), implemented PhaseStatusToBrushConverter/
      PhaseStatusToIconKindConverter/InverseBooleanToVisibilityConverter/
      RichTextBoxAssist verbatim, verified GREEN (18/18, matches plan exactly)
- [x] Full suite 164/166 (2 known deferred failures); build 0/0
- [x] Commit
- **Status:** complete
- Note: this task's own text creates no styles files (TabControlStyles.xaml/
  PhaseIndicatorStyles.xaml) despite its title — only converters + behavior.
  Watch for where those actually get created (likely Task 15).

### Phase 13: `PhaseIndicatorViewModel` and `TaskTabViewModel`

- Canonical task: `docs/superpowers/plans/implementationplan.md:6476` (## Task 13)
- [x] TDD red-green: TaskTabViewModelTests written, verified RED (CS0246),
      implemented IDirectoryPickerService/DirectoryPickerService/
      PhaseIndicatorViewModel/TaskTabViewModel verbatim
- [x] Fixed 2 real analyzer issues (CA1062 ctor null-check, CA2000 x2 in
      test helper) and 1 real logic defect (SyncFolder clobbering the
      startup-error ValidationMessage) — see findings.md
- [x] 24/24 pass (matches plan exactly); full suite 188/190 (2 known
      deferred failures); build 0/0
- [x] Commit
- **Status:** complete

### Phase 14: Shell view model, composition root and theme

- Canonical task: `docs/superpowers/plans/implementationplan.md:7371` (## Task 14)
- [x] TDD red-green: MainWindowViewModelTests written, verified RED (CS0246),
      implemented ITaskTabViewModelFactory/TaskTabViewModelFactory/
      MainWindowViewModel verbatim, rewrote App.xaml (MahApps + MaterialDesign
      merge order, dark theme) and App.xaml.cs (manual composition root: nine
      services, one window, startup-error dialog, unhandled-exception
      boundary) verbatim
- [x] Fixed 2 real analyzer issues (CA2000 x2, same established narrow-pragma
      pattern as Tasks 11/13) — see findings.md
- [x] Confirmed the plan's own Step 7 caution (build should fail naming
      `Styles\TabControlStyles.xaml`/`Views\MainWindow.xaml`, missing until
      Task 15) does NOT apply here — both are runtime pack-URI/StaticResource
      resolutions, not build-time checks; `Views\MainWindow.xaml(.cs)` already
      exist as the untouched default scaffold — see findings.md
- [x] 10/10 new tests pass (matches plan's Step 7 exactly); full suite
      198/200 (2 known deferred ConPTY streaming failures); build 0
      warnings/0 errors
- [x] Commit
- **Status:** complete

### Phase 15: XAML views

- Canonical task: `docs/superpowers/plans/implementationplan.md:7916` (## Task 15)
- **Status:** pending

### Phase 16: ArmFlex application icon

- Canonical task: `docs/superpowers/plans/implementationplan.md:8328` (## Task 16)
- Executed out of order, before Phase 15, via a dispatched implementer
  subagent — Phase 15's own Step 6 names this exact dependency
  (`Workflow\Assets\workflow.ico` must exist for `<ApplicationIcon>` to build)
- [x] Implemented `tools\GenerateIcon` (generator project, NOT added to
      `Workflow.sln`) + `Workflow.Tests\IconTests.cs` verbatim per the plan
- [x] Found and fixed 1 real defect in the plan's own verbatim
      `Program.cs`: rendering `PackIcon` (a `Control`) directly via
      `RenderTargetBitmap` in a bare console `Main` produces fully
      transparent frames (`Style`/`Template` never resolve outside an
      `Application` with the theme merged) — see findings.md ruling. Fixed
      by sourcing geometry from a real `PackIcon.Data` string but rendering
      a `System.Windows.Shapes.Path` (`Geometry.Parse`) instead of the
      Control itself.
- [x] Task review (dispatched subagent): Spec ✅ compliant, Task quality
      Approved. Independently re-verified the fix (decoded all 6 committed
      PNG frames: 58-68% non-transparent coverage, correct Indigo-400 fill
      color) and re-ran `IconTests` (2/2). 3 Minor findings, none
      blocking, recorded below.
- [x] 2/2 IconTests pass; full suite 199/202 (2 known deferred ConPTY +
      1 confirmed-flaky `WorkflowOrchestratorTests.Phases_ChainThroughAllFourStations`,
      passes 10/10 in isolation, unrelated to this change); build 0/0
- [x] Commit (f7efbbb)
- **Status:** complete
- **Deferred minors (for final whole-branch review):**
  1. The flaky orchestrator test above — pre-existing, load-related, not
     a regression from this task.
  2. Step 6's visual taskbar/title-bar check not yet meaningful — the icon
     isn't wired into `Workflow.csproj` until Phase 15.
  3. Unconfirmed low-risk nuance: rendering via `Path`+`Stretch="Uniform"`
     may normalize scaling slightly differently than `PackIcon`'s real
     `ControlTemplate` would (could not decompile the library to confirm);
     measured coverage looks proportionate, not mis-scaled.

### Phase 17: Acceptance gate (`verify.ps1`)

- Canonical task: `docs/superpowers/plans/implementationplan.md:8531` (## Task 17)
- Overall completion requires all A1–A9 / F1–F21 acceptance criteria (spec §15)
  satisfied, not merely that code has been written.
- **Status:** pending

## Key Questions

1. Resolved: does the "qdocimporter/eval" completion line in the generic
   task-runner instructions apply here? No — see findings.md, per
   source-of-truth hierarchy the canonical spec (§14/§9.3d) governs and that
   line is explicitly deleted from the shipped prompt template.

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| Task_plan phases map 1:1 to canonical plan Tasks 1-17, no independent redesign | Per the user's explicit instruction: task_plan.md tracks the canonical plan, does not replace it |
| Do NOT add eval gates to `qdocimporter/eval` | Spec §14/§9.3(d)/§3.2 declare this explicitly out of scope and require deleting that exact bullet from `implementation_prompt.md`; spec outranks the generic instruction per source-of-truth hierarchy |

## Errors Encountered

| Error | Attempt | Resolution |
|-------|---------|------------|
| (none yet) | | |

## Notes

- Repository was not a git repository at task start (confirmed via `git status`
  → "fatal: not a git repository"). Task 1 Step 1 initialises it.
- `dotnet` SDKs present: 5.0.411, 7.0.203, 7.0.317, 8.0.425, 9.0.302, 10.0.401.
  Target is `net8.0-windows` → SDK 8.0.425 covers it.
- Update phase status as work progresses: `pending` → `in_progress` → `complete`.
- Re-read the canonical Task section before resuming any phase — do not rely
  on memory of it from earlier in the session.
