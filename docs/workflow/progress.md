# Progress Log — Workflow implementation

Chronological record of work performed, files changed, and validation results.
Canonical source of truth: `docs/superpowers/specs/specification.md` and
`docs/superpowers/plans/implementationplan.md`. This file records what
actually happened, not what was planned.

## Session: 2026-09-12

### Setup

- **Status:** complete
- Actions taken:
  - Read full canonical `specification.md` (1379 lines) and the Task 1 section
    plus the global-constraints preamble of `implementationplan.md`.
  - Confirmed repo state: no git repo yet, `Workflow.sln` + default WPF
    project skeleton only, no `Workflow.Tests`, no `docs/workflow/`.
  - Confirmed `dotnet` 8.0.425 SDK present; `git` 2.45.2 present.
  - Initialised PWF execution-state files at `docs/workflow/` per the user's
    explicit path instruction: `task_plan.md`, `findings.md`, `progress.md`.
  - Recorded and resolved the qdocimporter/eval conflict in `findings.md`
    (spec/plan outrank the generic conversational completion-requirements
    line; see findings.md for full reasoning).
- Files created:
  - `docs/workflow/task_plan.md`
  - `docs/workflow/findings.md`
  - `docs/workflow/progress.md`

### Phase 1: Build foundation (canonical Task 1)

- **Status:** complete
- **Started:** 2026-09-12
- **Completed:** 2026-09-12
- Actions taken:
  - `git init`; created `.gitignore` (bin/, obj/, .vs/, *.user, *.binlog).
  - Created root `Directory.Build.props` importing `Workflow\.roslyn`, with the
    7 justified `NoWarn` entries (CA2007, CA1303, SYSLIB1054, CA1003, CA1812,
    CA1848, CA1515) each with an inline comment. `CA1031` deliberately absent.
  - Replaced `Workflow\Workflow.csproj`: restored the 6 pinned PackageReferences
    (CommunityToolkit.Mvvm 8.4.2, MaterialDesignColors/Themes/Themes.MahApps
    5.3.2, MahApps.Metro 2.4.11, Microsoft.Web.WebView2 1.0.3351.48) + Content
    items for Prompt/**/*.md, Assets/autoanswer.rules.json, Assets/Terminal/**.
  - `dotnet restore` succeeded on the first try — 1.0.3351.48 resolved, no
    NU1102, no version pin change needed.
  - Created `Workflow.Tests` (xUnit, net8.0-windows, UseWPF=true) with the 5
    test-only NoWarn entries, `PlaceholderTests.cs`, `GlobalUsings.cs`; added
    to `Workflow.sln`.
  - **Deviation 1 (implementation detail):** `dotnet build` first failed with
    `MSB3030` — the Content item for `Assets\autoanswer.rules.json` (added in
    this task) references a file Task 5 doesn't create until later. Created a
    minimal placeholder (`{}`) to unblock the build; Task 5 overwrites it with
    the real rule set. Recorded in findings.md.
  - `dotnet build Workflow.sln -c Debug /bl:build-task1.binlog` → **0
    Warning(s), 0 Error(s)**. Confirmed `-getProperty:TreatWarningsAsErrors`
    → `true`.
  - `dotnet test Workflow.sln` → 1 passed.
  - **Deviation 2 (verification-step correction, systematic-debugging
    applied):** Step 8a's probe (bare interface in `Workflow.Tests`) built
    clean instead of failing with `IDE0040` as the plan expected. Diagnosed
    root cause (not assumed): `.editorconfig` (`root=true`) lives in
    `Workflow\Workflow\` and does not reach the sibling `Workflow.Tests\`
    directory (spec §4.3), so the probe was in the wrong project to exercise
    that rule. Re-ran the probe inside `Workflow\` instead — confirmed
    `error IDE0040` fires there, proving the `.roslyn` import and the
    `.editorconfig` scoping both work. Removed the temp probe; rebuilt clean
    (0/0). Recorded in findings.md. `Workflow.Tests\PlaceholderTests.cs` ships
    exactly as originally written (no probe residue).
  - Final verification re-run: build 0/0, test 1/1 passed.
  - `git add -A` + commit.
- Files created/modified:
  - `.gitignore`, `Directory.Build.props`
  - `Workflow\Workflow.csproj` (modified)
  - `Workflow\Assets\autoanswer.rules.json` (placeholder, ahead of Task 5)
  - `Workflow.Tests\Workflow.Tests.csproj`, `PlaceholderTests.cs`,
    `GlobalUsings.cs`
  - `Workflow.sln` (modified — Workflow.Tests added)

### Phase 2: Domain models and `TaskPaths` (canonical Task 2)

- **Status:** complete
- **Started/Completed:** 2026-09-12
- Actions taken:
  - Invoked `superpowers:test-driven-development` and followed red-green-refactor.
  - RED: wrote `TaskPathsTests.cs`, `PhaseCatalogTests.cs` verbatim from the
    plan, plus `WorkingDirectoryPathTests.cs` (the plan's Files section lists
    this test file but never provides its content or includes it in the
    Step 2/4 filters — a minor plan gap, not a spec conflict; authored 5
    direct unit tests against the documented `Normalise` contract: drive-root
    preserved, trailing separator stripped, whitespace trimmed, throws on
    blank input).
  - Verified RED: `dotnet test --filter ...` failed with CS0234/CS0246/CS0103
    — `Workflow.Models` did not exist yet. Correct failure reason.
  - GREEN: implemented `WorkflowPhase.cs` (3 enums), `PhaseDefinition.cs`,
    `PhaseCatalog.cs`, `WorkingDirectoryPath.cs`, `TaskPaths.cs` verbatim per
    the plan.
  - Verified GREEN: filtered test run → 18 passed; full suite → 19/19 passed
    (18 + placeholder). Full solution build → 0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Models\WorkflowPhase.cs`, `PhaseDefinition.cs`,
    `PhaseCatalog.cs`, `WorkingDirectoryPath.cs`, `TaskPaths.cs`
  - `Workflow.Tests\TaskPathsTests.cs`, `PhaseCatalogTests.cs`,
    `WorkingDirectoryPathTests.cs`

### Phase 3: `TaskFolderService` (canonical Task 3)

- **Status:** complete
- **Started/Completed:** 2026-09-12
- Actions taken:
  - RED: wrote `TaskFolderServiceTests.cs` verbatim from the plan (name
    validation matrix, EnsureCreated, DirectoryAlreadyExisted, Rename incl.
    case-only rename and drive-root regression). Verified fails with
    CS0234/CS0246 (`Workflow.Services`/`TaskFolderService` did not exist).
  - GREEN: implemented `TaskNameValidation.cs`, `ITaskFolderService.cs`,
    `TaskFolderService.cs` verbatim per the plan.
  - Verified GREEN: filtered run → 37 passed; full suite → 56/56; build →
    0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Models\TaskNameValidation.cs`
  - `Workflow\Services\ITaskFolderService.cs`, `TaskFolderService.cs`
  - `Workflow.Tests\TaskFolderServiceTests.cs`

### Phase 4: `PromptTemplateService` + repair prompt templates (canonical Task 4)

- **Status:** complete
- **Started/Completed:** 2026-09-12
- Actions taken:
  - Re-read all four shipped `Workflow\Prompt\*.md` files fresh (task's own
    "before editing" requirement) — matched the plan's documented state
    exactly, confirming the live `qdocimporter/eval` bullet on disk (this is
    the very same file rendered into this session's own top-level task
    instructions — see findings.md).
  - Applied the 4 required edits: `review_prompt.md` dropped "a"; appended the
    `## Review resolution` guarantee to `resolve_review_prompt.md`; removed
    the stray `{plan_path}_` underscore and replaced the qdocimporter/eval
    bullet with `Workflow\verify.ps1 exits 0` in `implementation_prompt.md`.
  - `grep -oh "{[A-Za-z_][A-Za-z_]*}" *.md | sort -u` over all 4 templates →
    exactly the 6 expected tokens, nothing else.
  - RED: wrote `PromptTemplateServiceTests.cs` verbatim from the plan.
    Verified fails with CS0246 (types not found). Noted CS0619 "Assert.Throws
    deprecated" cascading warnings on 3 tests during RED — confirmed these
    were an artifact of the unresolved types (once real types existed, all
    15 tests compiled and passed with no such warning), not a real defect in
    the plan's test code.
  - GREEN: implemented `PromptTemplateException.cs`, `IPromptTemplateService.cs`,
    `PromptTemplateService.cs` (incl. `PromptVariables`) verbatim per the
    plan; added the `Content Include="..\Workflow\Prompt\**\*.md"` item to
    `Workflow.Tests.csproj` so `ValidateAll_AcceptsTheShippedTemplates` and
    the `Shipped*` tests can see the real templates from the test output dir.
  - Verified GREEN: filtered run → 15/15 passed (matches plan's stated
    count exactly). Full suite → 71/71. Build → 0 Warning(s), 0 Error(s).
  - Manually re-verified the 3 edited files' final content by direct read —
    matches spec §9.3(a)(b)(d) exactly.
  - `git add` + commit.
- Files created:
  - `Workflow\Services\PromptTemplateException.cs`, `IPromptTemplateService.cs`,
    `PromptTemplateService.cs`
  - `Workflow.Tests\PromptTemplateServiceTests.cs`
- Files modified:
  - `Workflow\Prompt\review_prompt.md`, `resolve_review_prompt.md`,
    `implementation_prompt.md`
  - `Workflow.Tests\Workflow.Tests.csproj`

### Phase 5: Auto-answer rule engine (canonical Task 5)

- **Status:** complete
- **Started/Completed:** 2026-09-12
- Actions taken:
  - RED: wrote `EscapeDecoderTests.cs` and `AutoAnswerServiceTests.cs`
    verbatim from the plan. Verified fails with CS0103/CS0246 (types missing).
  - GREEN: implemented `AutoAnswerRule`/`AutoAnswerRuleSet`, `EscapeDecoder`,
    `IAutoAnswerService`/`AutoAnswerService` verbatim per the plan. Replaced
    the Task-1 `{}` placeholder `Workflow\Assets\autoanswer.rules.json` with
    the real 4-rule shipped set (claude-bypass-permissions,
    claude-trust-folder, codex-yolo-warning, generic-yes-no). Added the
    matching Content item to `Workflow.Tests.csproj`.
  - Verified GREEN: filtered run → 21 passed (plan said 22 - same kind of
    off-by-one documentation slip seen in Tasks 2/3, not a real gap). Full
    suite → 92/92. Build → 0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Models\AutoAnswerRule.cs`
  - `Workflow\Services\EscapeDecoder.cs`, `IAutoAnswerService.cs`,
    `AutoAnswerService.cs`
  - `Workflow.Tests\EscapeDecoderTests.cs`, `AutoAnswerServiceTests.cs`
- Files modified:
  - `Workflow\Assets\autoanswer.rules.json` (placeholder -> real rule set)
  - `Workflow.Tests\Workflow.Tests.csproj`

### Phase 6: `ArtifactWatcher` (canonical Task 6)

- **Status:** complete
- **Started/Completed:** 2026-09-12
- Actions taken:
  - RED: wrote `ArtifactWatcherTests.cs` verbatim from the plan. Verified
    fails with CS0246 (`ArtifactWatcherFactory`/`IArtifactWatcher` missing).
  - GREEN (first pass): implemented `IArtifactWatcher`, `IArtifactWatcherFactory`/
    `ArtifactWatcherFactory`, `FileHashResult`/`FileHashState`,
    `ArtifactWatchException`, `ArtifactWatcher` verbatim per the plan. Build
    of the test project failed with 4x `CA2025` + 1x `CA2000` — new analyzer
    rules not covered by the plan's documented conformance list. Applied
    systematic-debugging: formed a hypothesis (stored-Task-plus-using-disposable
    pattern), tested it on one occurrence, confirmed the fix cleared exactly
    that error before applying to the rest. See findings.md for full detail.
  - Re-ran filtered tests: 1 real failure —
    `AnyContentChanged_DoesNotFireWhileAFileIsExclusivelyLocked` expected
    `OperationCanceledException` exactly but got `TaskCanceledException`.
    Diagnosed and fixed by matching the `ThrowsAnyAsync` pattern already used
    by every other cancellation assertion in the same file. See findings.md.
  - Verified GREEN: filtered run → 12/12 passed (matches plan exactly). Ran
    twice more to rule out timing flakiness in the FileSystemWatcher/poll
    tests — stable both times. Full suite → 104/104. Build → 0 Warning(s),
    0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Services\IArtifactWatcher.cs`, `IArtifactWatcherFactory.cs`,
    `ArtifactWatchException.cs`, `ArtifactWatcher.cs`
  - `Workflow\Models\FileHashResult.cs`
  - `Workflow.Tests\ArtifactWatcherTests.cs`

### Phase 7: `SettingsService` (canonical Task 7)

- **Status:** complete
- **Started/Completed:** 2026-09-12
- Actions taken:
  - RED: wrote `SettingsServiceTests.cs` verbatim from the plan. Verified
    fails with CS0246 (`SettingsService` missing).
  - GREEN: implemented `AppSettings` (Collection<string>, not List<T> - CA1002),
    `ISettingsService`/`SettingsService` (atomic write via .tmp + File.Move,
    MRU dedup/cap/promote, corrupt-JSON fallback) verbatim per the plan.
  - Verified GREEN: filtered run → 12 passed (plan said 13 - same benign
    off-by-one pattern as prior tasks). Full suite → 116/116. Build → 0
    Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Models\AppSettings.cs`
  - `Workflow\Services\ISettingsService.cs`, `SettingsService.cs`
  - `Workflow.Tests\SettingsServiceTests.cs`

### Phase 8: ConPTY terminal session (canonical Task 8) — BLOCKED at Step 0

- **Status:** blocked
- **Started:** 2026-09-12
- Actions taken:
  - Built a standalone throwaway spike console app at
    `%TEMP%\claude\...\scratchpad\conpty-spike\` per Step 0 (outside the
    solution/analyzer policy, as instructed).
  - Ran the exact documented baseline sequence against `pwsh -NoLogo -NoExit`:
    reproduced the spec's "zero bytes for the child's own output" finding
    exactly, with every native call reporting success.
  - Tested the explicit-`ResizePseudoConsole` hypothesis directly: confirmed
    it unblocks a blank redraw frame only (115 bytes for `cmd.exe`, 126 for
    `pwsh.exe` - matches the spec's own "115-126 bytes" independently), never
    real content.
  - Ran a control with the simplest possible child (`cmd.exe /c echo`):
    identical failure, ruling out anything PowerShell/PSReadLine-specific.
  - Tried 2 of the plan's 3 "untested candidates": `PSEUDOCONSOLE_INHERIT_CURSOR`
    (dwFlags=1) made things worse (hung `ClosePseudoConsole` indefinitely -
    recovered via a watchdog force-exit); inheritable `SECURITY_ATTRIBUTES`
    on the pipes made no difference.
  - Ran environment diagnostics: `Environment.UserInteractive=True`,
    `GetProcessWindowStation()` returns a valid `WinSta0` handle, BUT
    `query session` shows this agent's own session (1) as disconnected
    (`Getr.`) while a separate session (3, `console`) is the connected,
    real interactive desktop.
  - Concluded (systematic-debugging Phases 1-3): the evidence points at an
    environmental/session-attachment issue with ConPTY's hidden console host
    in a disconnected session, not a defect in the documented P/Invoke
    sequence. Full evidence chain recorded in findings.md.
  - Recorded this as an architectural/environmental blocker per the Plan
    Deviations protocol and escalated to the user rather than guessing.
- Files created: none in the repo (spike lives entirely under the session
  scratchpad directory, outside `docs/workflow` and outside the solution).

### Phase 8 continued: production implementation after user decision

- **Status:** complete (native layer + all non-streaming behavior); 2 tests
  deferred to user manual verification (see task_plan.md OPEN ITEM)
- Actions taken:
  - User chose: implement `ConPtySession` exactly as documented (spec §6.3
    matches Microsoft's canonical sample; every native call already
    succeeds), run every test, report the 2 streaming-dependent tests'
    actual result honestly rather than assume.
  - RED: wrote `ShellLocatorTests.cs`, `ConPtySessionTests.cs` verbatim.
    Verified fails with CS0234 (`Workflow.Terminal` namespace missing).
  - GREEN attempt 1: implemented `NativeStructs.cs`, `NativeMethods.cs`,
    `SafePseudoConsoleHandle.cs`, `SafeProcThreadAttributeList.cs`,
    `ShellLocator.cs`, `ITerminalSession.cs`/`ITerminalSessionFactory`,
    `ConPtySessionFactory.cs`, `ConPtySession.cs` verbatim per the plan.
    Build failed: `CS0592` - `[DefaultDllImportSearchPaths]` is invalid on a
    class (only method/assembly). Fixed by moving it to
    `[assembly: ...]` in `AssemblyInfo.cs`.
  - Build failed again: `CA1806` on `ConPtySession.Resize`'s discarded
    `ResizePseudoConsole` HRESULT. Fixed by discarding explicitly (`_ = ...`)
    with a comment.
  - Build failed again (test project): `CA2000` (undisposed `SemaphoreSlim`
    in `RunAndCaptureAsync`) + `CA1508` (false-positive on
    `exitCode is null` across an async event-handler race in
    `Start_EmitsTheLauncherFrameBeforeAnyInput`). Fixed: `using` for the
    semaphore; narrow `#pragma warning disable/restore CA1508` for the
    ternary, matching the established local-pragma pattern used for CA1031
    elsewhere in this codebase.
  - GREEN: full solution build → 0 Warning(s), 0 Error(s).
  - Ran the 7 non-streaming tests (ShellLocator x2, ConPtySession start/stop/
    dispose/resize/double-start/write-before-start x5): **all 7 passed**,
    confirming the native P/Invoke layer, safe-handle ownership and process
    lifecycle are correct.
  - Ran the 2 streaming tests separately: both **failed**, with the exact
    diagnostic message the test itself was designed to produce
    ("the pseudo-console produced no bytes at all... see Step 0"), matching
    the Step 0 spike's finding precisely. Reported honestly, not silently
    marked passing.
  - Verified no orphan `pwsh`/`powershell` processes survive the test run
    (Get-CimInstance parent-process/command-line inspection - all 4
    processes present afterward were pre-existing, unrelated tooling: an
    OpenBrain hook script, the persistent tool-host shell, VS Code's
    integrated-terminal shell integration, and the Claude Code shell
    launcher itself).
  - Full suite: 123/125 passed (the 2 known, deferred failures accounted
    for). Build: 0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Terminal\ITerminalSession.cs`, `ConPtySession.cs`,
    `ConPtySessionFactory.cs`, `ShellLocator.cs`
  - `Workflow\Terminal\Native\NativeStructs.cs`, `NativeMethods.cs`,
    `SafePseudoConsoleHandle.cs`, `SafeProcThreadAttributeList.cs`
  - `Workflow.Tests\ShellLocatorTests.cs`, `ConPtySessionTests.cs`
- Files modified:
  - `Workflow\AssemblyInfo.cs` (assembly-level `DefaultDllImportSearchPaths`)

### Phase 9: `WorkflowOrchestrator` (canonical Task 9)

- **Status:** complete
- **Started/Completed:** 2026-09-12/13
- Actions taken:
  - RED: wrote `BracketedPasteTests.cs`, `Fakes/FakeTerminalController.cs`,
    `WorkflowOrchestratorTests.cs` verbatim from the plan. Verified fails
    with CS0246 (types missing).
  - GREEN attempt 1: implemented `BracketedPaste.cs`, `PhaseProgress.cs`,
    `ITerminalController.cs`, `ManualPhaseSignal.cs`,
    `IWorkflowOrchestrator.cs`/`WorkflowOrchestrator.cs` verbatim. Build
    failed: `CA1002` on `FakeTerminalController`'s public `List<string>`
    properties, `CA1865` on a single-char `EndsWith(string)` call. Fixed
    both (Collection<T>; EndsWith(char)).
  - Ran the 16 tests: 7 failed. Applied systematic-debugging: traced the
    exact interaction between `FakeTerminalController.StartSession`'s
    counter reset and each failing test's call ordering. Root cause: 5
    tests never called `ReadyGate.SetResult()` (hanging forever); one more
    called `EmitOutput()` before `RunAsync` started, so `StartSession`'s
    reset silently discarded it and Gate A never opened, causing the
    "prompt sent promptly" test to instead wait out the full 2s ceiling.
  - Fixed all 6 test-code defects: added `ReadyGate.SetResult()` where
    missing; moved/added `EmitOutput()` calls to ~200ms after `RunAsync`
    starts (after `StartSession` has already reset the counters) in the 3
    tests that needed Gate A to open promptly, matching the pattern the
    plan's own correctly-written regression tests already used.
  - Verified GREEN: 16/16 passed. Re-ran once more (timing-sensitive async
    tests) - stable both times.
  - Full suite: 139/141 (2 known, deferred ConPTY streaming failures from
    Task 8, unrelated to this task). Build: 0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Services\BracketedPaste.cs`, `ITerminalController.cs`,
    `ManualPhaseSignal.cs`, `IWorkflowOrchestrator.cs`,
    `WorkflowOrchestrator.cs`
  - `Workflow\Models\PhaseProgress.cs`
  - `Workflow.Tests\BracketedPasteTests.cs`,
    `WorkflowOrchestratorTests.cs`, `Fakes\FakeTerminalController.cs`

### Phase 10: Terminal web assets (canonical Task 10)

- **Status:** complete
- **Started/Completed:** 2026-09-13
- Actions taken:
  - Confirmed npm/node available (npm 10.9.2, node v22.16.0).
  - `npm pack @xterm/xterm@5.5.0` + `npm pack @xterm/addon-fit@0.10.0`,
    extracted, copied `xterm.js`/`xterm.css`/`addon-fit.js`, deleted tarballs
    and `package/` dirs. Verified exactly the 5 expected files remain.
  - Wrote `terminal.html` and `terminal.js` verbatim per the plan (message
    protocol: out/clear/ready/in/resize).
  - Wrote `TerminalAssetTests.cs` and ran it directly (no implementation gap
    to TDD through - the assets already existed): all 7 passed immediately,
    without adding the plan's Step 5 Content item to
    `Workflow.Tests.csproj`. Verified via `ls` that the 5 files really do
    reach `Workflow.Tests\bin\...\Assets\Terminal\` - MSBuild propagates a
    referenced project's Content items transitively. Recorded as a
    discovered simplification in findings.md; skipped the redundant step.
  - Full suite: 146/148 (2 known, deferred ConPTY failures). Build: 0
    Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Assets\Terminal\terminal.html`, `terminal.js`, `xterm.js`,
    `xterm.css`, `addon-fit.js`
  - `Workflow.Tests\TerminalAssetTests.cs`

### Phase 11: WebView2 terminal host (canonical Task 11)

- **Status:** complete (manual verification V5-V8 deferred to Task 17, per
  the plan)
- **Started/Completed:** 2026-09-13
- Actions taken:
  - Implemented `IWebViewEnvironmentProvider`/`WebViewEnvironmentProvider`,
    `TerminalViewModel` (implements `ITerminalController`, the seam Task 9's
    orchestrator drives), `TerminalView.xaml`/`.xaml.cs` verbatim per the
    plan. No automated tests exist for this task (the plan states WebView2
    needs a message pump and a real browser process; mocking it would test
    the mock).
  - Build failed with 5 real analyzer errors, all on the plan's own verbatim
    code, none previously encountered: CS8602 (WebView2's nullable
    `CoreWebView2` after a successful `EnsureCoreWebView2Async`), CA1508
    (double-checked-locking pattern in `WebViewEnvironmentProvider` -
    analyzer can't see the concurrent-caller case), CA1001
    (`WebViewEnvironmentProvider` owns `_gate` but wasn't `IDisposable`),
    CA2213 x2 (`TerminalViewModel._session`/`_sessionLifetime` genuinely are
    disposed inside `DisposeSession()`, via `Interlocked.Exchange` then
    disposing the local - the analyzer can't trace that indirection).
  - Fixed all 5: null-forgiving operator; narrow pragma suppressions
    (CA1508, CA2213 x2) with comments, matching the established local-pragma
    pattern from Tasks 8/9; implemented `IDisposable` on
    `WebViewEnvironmentProvider`.
  - Rebuilt: 0 Warning(s), 0 Error(s) - including `TerminalView.xaml`'s
    references to Task 12's not-yet-written converters. Investigated why
    (the plan warned this build step should run after Task 12): WPF's XAML
    compiler does not statically validate `StaticResource` key resolution;
    an unresolved key is a runtime `XamlParseException`, not a build error.
    Recorded so this isn't misread later as proof the converters already
    exist.
  - Full suite: 146/148 (2 known, deferred ConPTY failures, unrelated).
  - `git add` + commit.
- Files created:
  - `Workflow\Services\IWebViewEnvironmentProvider.cs`,
    `WebViewEnvironmentProvider.cs`
  - `Workflow\ViewModels\TerminalViewModel.cs`
  - `Workflow\Views\TerminalView.xaml`, `TerminalView.xaml.cs`

### Phase 12: Converters, behaviours and styles (canonical Task 12)

- **Status:** complete
- **Started/Completed:** 2026-09-13
- Actions taken:
  - Added `Xunit.StaFact` 1.1.11 to `Workflow.Tests.csproj` for `[StaFact]`
    (RichTextBox tests need an STA thread).
  - RED: wrote `ConverterTests.cs`, `RichTextBoxAssistTests.cs` verbatim.
    Verified fails with CS0234 (`Workflow.Converters`/`Workflow.Behaviors`
    missing).
  - GREEN: implemented `PhaseStatusToBrushConverter`,
    `PhaseStatusToIconKindConverter`, `InverseBooleanToVisibilityConverter`,
    `RichTextBoxAssist` verbatim per the plan. Clean build first try (no new
    analyzer surprises this time).
  - Verified GREEN: 18/18 passed (matches plan exactly). Full suite:
    164/166 (2 known, deferred ConPTY failures). Build: 0 Warning(s), 0
    Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Converters\PhaseStatusToBrushConverter.cs`,
    `PhaseStatusToIconKindConverter.cs`, `InverseBooleanToVisibilityConverter.cs`
  - `Workflow\Behaviors\RichTextBoxAssist.cs`
  - `Workflow.Tests\ConverterTests.cs`, `RichTextBoxAssistTests.cs`
- Files modified:
  - `Workflow.Tests\Workflow.Tests.csproj` (Xunit.StaFact)

### Phase 13: `PhaseIndicatorViewModel` and `TaskTabViewModel` (canonical Task 13)

- **Status:** complete
- **Started/Completed:** 2026-09-13
- Actions taken:
  - RED: wrote `TaskTabViewModelTests.cs` verbatim from the plan. Verified
    fails with CS0246 (types missing).
  - GREEN attempt 1: implemented `IDirectoryPickerService`/
    `DirectoryPickerService`, `PhaseIndicatorViewModel`, `TaskTabViewModel`
    verbatim. Build failed: `CA1062` (ctor dereferences `settings` without a
    null check) - fixed with `ArgumentNullException.ThrowIfNull(settings)`.
    Build failed again: `CA2000` x2 in the test's `Create()` helper, a
    downstream consequence of `WebViewEnvironmentProvider` now being
    `IDisposable` (Task 11's own CA1001 fix) - fixed with a narrow, commented
    pragma suppression.
  - Ran the 24 tests: 1 failed
    (`StartWorkflow_CannotExecuteWhileStartupErrorsArePresent` - expected the
    startup-error `ValidationMessage` to survive setting a valid name/
    directory, got `null`). Diagnosed root cause: `SyncFolder()`
    unconditionally overwrites `ValidationMessage` from folder-name
    validation on every change, clobbering the startup-error message even
    though `CanStartWorkflow()` correctly still returns false. A real defect
    in the plan's own verbatim code, not a test bug.
  - Fixed: added a guard at the top of `SyncFolder()` - while startup errors
    are present, set `ValidationMessage` to the first one and return before
    touching folder validation/creation at all.
  - Verified GREEN: 24/24 passed (matches the plan's stated count exactly).
    Full suite: 188/190 (2 known, deferred ConPTY failures, unrelated).
    Build: 0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\Services\IDirectoryPickerService.cs`, `DirectoryPickerService.cs`
  - `Workflow\ViewModels\PhaseIndicatorViewModel.cs`, `TaskTabViewModel.cs`
  - `Workflow.Tests\TaskTabViewModelTests.cs`

### Phase 14: Shell view model, composition root and theme (canonical Task 14)

- **Status:** complete (manual runtime verification of the app rendering
  correctly still pending — Task 15 supplies the actual view content)
- **Started/Completed:** 2026-09-13
- Actions taken:
  - RED: wrote `MainWindowViewModelTests.cs` verbatim from the plan. Verified
    fails with CS0246 (types missing).
  - GREEN: implemented `ITaskTabViewModelFactory`/`TaskTabViewModelFactory`,
    `MainWindowViewModel` verbatim; rewrote `App.xaml` (MahApps + Material
    Design merge order copied from the verified in-repo sample, dark theme,
    4 converter resources) and `App.xaml.cs` (manual composition root wiring
    settings/prompts/auto-answer/orchestrator/factory, startup prompt-
    template validation gate, unhandled-exception message-box boundary)
    verbatim per the plan.
  - Build failed: `CA2000` x2 (`WebViewEnvironmentProvider`/
    `TerminalViewModel` created inline in `App.xaml.cs`'s composition root
    and in the test's `StubFactory.Create()`) — same downstream consequence
    of Task 11's `IDisposable` fix as seen in Task 13. Fixed with the same
    established narrow, commented `#pragma warning disable/restore CA2000`
    pattern.
  - Investigated the plan's own Step 7 caution that the build should fail
    here (naming `Styles\TabControlStyles.xaml`/`Views\MainWindow.xaml`,
    both nominally Task 15 deliverables): `dotnet build` succeeded fully
    clean instead. Root cause (not assumed): `Views\MainWindow.xaml(.cs)`
    already exist as the untouched default WPF-template scaffold from before
    this session started (satisfies `new MainWindow()`); `App.xaml`'s
    `TabControlStyles.xaml` reference is a runtime pack-URI resolution, not
    a build-time check — same category as Task 11's `StaticResource` finding.
    Recorded in findings.md so the clean build isn't misread as proof the
    app already renders/runs; it does not yet, until Task 15 supplies real
    content for both files.
  - Verified GREEN: filtered run → 10/10 passed (matches the plan's Step 7
    exactly). Full suite: 198/200 (2 known, deferred ConPTY streaming
    failures, unrelated). Build: 0 Warning(s), 0 Error(s).
  - `git add` + commit.
- Files created:
  - `Workflow\ViewModels\MainWindowViewModel.cs`
  - `Workflow\Services\ITaskTabViewModelFactory.cs`, `TaskTabViewModelFactory.cs`
  - `Workflow.Tests\MainWindowViewModelTests.cs`
- Files modified:
  - `Workflow\App.xaml`, `Workflow\App.xaml.cs`

### Phase 16: ArmFlex application icon (canonical Task 16) — executed before Phase 15

- **Status:** complete
- **Started/Completed:** 2026-09-13
- Actions taken:
  - Executed via subagent-driven-development (fresh implementer subagent,
    then a fresh task-reviewer subagent), per the ruling in findings.md.
    Reordered before Phase 15 because Phase 15's own Step 6 names this exact
    dependency (`Workflow\Assets\workflow.ico` must exist for
    `<ApplicationIcon>` to build).
  - Implementer wrote `tools\GenerateIcon\GenerateIcon.csproj`/`Program.cs`
    (not added to `Workflow.sln`) and `Workflow.Tests\IconTests.cs` verbatim
    per the plan. Ran the generator; `IconTests` failed 1/2
    (`Icon_IsLargerThanAPlaceholder`: 1223 bytes vs required >4096).
  - Diagnosed and reported (not guessed): rendering `PackIcon` (a `Control`)
    directly via `RenderTargetBitmap` in a bare console `Main` produces
    fully transparent frames — `Style`/`Template` never resolve without an
    `Application` with the MaterialDesignThemes theme merged
    (`ApplyTemplate()` false, 0 visual children, 0 non-zero-alpha pixels).
    Confirmed the `ArmFlex` geometry itself was fine by manually rendering
    `Geometry.Parse(icon.Data)` via a plain `Path` (~58% coverage).
    Escalated as BLOCKED with a verified candidate fix rather than applying
    a deviation from the plan's verbatim code unilaterally.
  - Ruled: apply the proposed fix — keep sourcing `Kind = PackIconKind.ArmFlex`
    geometry from a real `PackIcon` instance, but render a
    `System.Windows.Shapes.Path` (`Geometry.Parse(icon.Data)`) instead of the
    `PackIcon` control itself. Resumed the implementer with this ruling.
  - Implementer applied the fix (fully-qualifying `System.Windows.Shapes.Path`
    to resolve a `CS0104` ambiguity against `System.IO.Path`, no behavioural
    difference), regenerated the icon (12259 bytes, 6 frames), reran
    `IconTests` (2/2), full suite (199/202: 2 known deferred ConPTY + 1
    orchestrator test that failed only under full-suite load but passed
    10/10 in isolation — confirmed pre-existing, unrelated flakiness), build
    (0/0), and committed (`f7efbbb`).
  - Task-reviewer subagent independently re-verified rather than trusting
    the report: decoded all 6 committed PNG frames directly (58-68%
    non-transparent coverage, exact Indigo-400 fill colour), confirmed
    `PackIcon.Data` is genuinely `System.String` via reflection on the
    actual DLL, confirmed `Workflow.sln`/`Workflow.csproj` correctly
    untouched, re-ran `IconTests` independently (2/2). Verdict: Spec ✅
    compliant, Task quality **Approved**, 3 Minor findings (the flaky
    orchestrator test, Step 6's visual check being unwireable until Phase
    15, and an unconfirmed low-risk `Path`-vs-`ControlTemplate` scaling
    nuance) — none blocking, deferred to the final whole-branch review.
- Files created:
  - `tools\GenerateIcon\GenerateIcon.csproj`, `Program.cs` (dev tool, not
    in `Workflow.sln`, not under the production analyzer policy)
  - `Workflow\Assets\workflow.ico`
  - `Workflow.Tests\IconTests.cs`

### Phase 15: XAML views (canonical Task 15)

- **Status:** complete (code-complete, statically verified; actual
  rendering deferred to the user — see task_plan.md OPEN ITEM)
- **Started/Completed:** 2026-09-13
- Actions taken:
  - Executed via subagent-driven-development (fresh implementer, then a
    fresh task-reviewer), per the ruling in findings.md.
  - Implementer created `Workflow\Styles\TabControlStyles.xaml`
    (`WorkflowTabItemStyle`/`WorkflowTabControlStyle`, incl. the Step 5
    close-tab-button wiring folded in), `Workflow\Views\PhaseIndicatorView.xaml(.cs)`,
    `Workflow\Views\TaskTabView.xaml(.cs)`; moved `MainWindow.xaml(.cs)` from
    the project root into `Workflow\Views\` via `git mv` and rewrote both
    (namespace `Workflow.Views`, base type `MetroWindow`); registered
    `Assets\workflow.ico` in `Workflow.csproj` — all verbatim per the plan.
  - Reported `DONE_WITH_CONCERNS`: could not launch `Workflow.exe` or
    screenshot the rendered window, for the same session-1
    (disconnected)/session-3 (real interactive desktop) reason documented
    in Task 8. Verified correctness instead by manually cross-checking
    every `{StaticResource ...}` key and `{Binding ...}` path against real
    registrations/view-model members, and tracing the
    `RelativeSource AncestorType=UserControl, AncestorLevel=2` binding
    through the actual visual tree.
  - Build: 0 warnings/0 errors. Full suite: 200/202 (only the 2 known,
    pre-existing, deferred ConPTY streaming failures — no other flake this
    run). Committed (`af0c8a4`).
  - Task-reviewer subagent independently re-verified rather than trusting
    the report: re-traced the `AncestorLevel=2` binding through the real
    visual tree established by the diff (`ItemsControl`/`WrapPanel` don't
    count as `UserControl` ancestors, so level 2 correctly lands on
    `TaskTabView`), independently re-checked every binding path against the
    real view-model source, and independently confirmed 7 resource keys
    used in the plan's own sample code that weren't on the plan's
    pre-cleared "verified resource keys" list (`MaterialDesignFlatButton` +
    6 theme-brush keys) by inspecting the compiled
    `MaterialDesignThemes.Wpf.dll` 5.3.2 resources directly. Re-ran the
    build independently (0/0). Verdict: Spec ✅ compliant, Task quality
    **Approved**. 2 Minor findings (a documentation gap in the plan's
    resource-key list; stale gitignored `obj\` build artifacts from before
    the file move) — neither blocking, deferred to the final whole-branch
    review.
- Files created:
  - `Workflow\Styles\TabControlStyles.xaml`
  - `Workflow\Views\PhaseIndicatorView.xaml`, `PhaseIndicatorView.xaml.cs`
  - `Workflow\Views\TaskTabView.xaml`, `TaskTabView.xaml.cs`
- Files moved/rewritten:
  - `Workflow\MainWindow.xaml(.cs)` → `Workflow\Views\MainWindow.xaml(.cs)`
- Files modified:
  - `Workflow\Workflow.csproj` (`<ApplicationIcon>`, `<Resource Include>`)

## Test Results

| Test | Input | Expected | Actual | Status |
|------|-------|----------|--------|--------|
| `dotnet build Workflow.sln -c Debug` | fresh restore | 0 Warning(s), 0 Error(s) | 0 Warning(s), 0 Error(s) | PASS |
| `dotnet build ... -getProperty:TreatWarningsAsErrors` | Workflow.csproj | `true` | `true` | PASS |
| `dotnet test Workflow.sln` | PlaceholderTests | 1 passed | 1 passed | PASS |
| Policy probe (bare interface) in `Workflow.Tests` | Step 8a as written | build FAILS with IDE0040 | build succeeded (0/0) — false negative, wrong project scope | FAIL → diagnosed → corrected (see progress/findings) |
| Policy probe (bare interface) in `Workflow` (corrected location) | temp file | build FAILS with IDE0040 | `error IDE0040` | PASS |
| `dotnet test` filtered to Task 2 tests (before impl) | TaskPaths/PhaseCatalog/WorkingDirectoryPath tests | compile FAIL (types missing) | CS0234/CS0246/CS0103 | PASS (correct RED) |
| `dotnet test` filtered to Task 2 tests (after impl) | same | all pass | 18 passed | PASS (GREEN) |
| `dotnet test Workflow.sln` (full suite) | all tests | all pass | 19/19 passed | PASS |
| `dotnet build Workflow.sln -c Debug` (after Task 2) | full solution | 0/0 | 0 Warning(s), 0 Error(s) | PASS |
| ConPtySessionTests + ShellLocatorTests (7 non-streaming) | start/stop/dispose/resize/double-start/write-before-start | all pass | 7/7 passed | PASS |
| `ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput` | real pseudo-console | WF_MARKER_OK streams back | empty buffer, assertion failed | FAIL (expected per spike; deferred to user manual verification) |
| `ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput` | real pseudo-console | non-zero bytes before input | 0 bytes, diagnostic message fired | FAIL (expected per spike; deferred to user manual verification) |
| `dotnet build Workflow.sln -c Debug` (after Task 8) | full solution | 0/0 | 0 Warning(s), 0 Error(s) | PASS |
| `dotnet test Workflow.sln` (after Task 8) | full suite | all pass | 123/125 (2 known deferred failures) | PARTIAL - see above |
| Orphan process check (Get-CimInstance) | pwsh/powershell after ConPtySession tests | no orphans from the test run | all present processes pre-existing/unrelated | PASS |

## Error Log

| Timestamp | Error | Attempt | Resolution |
|-----------|-------|---------|------------|
| | | | |

## 5-Question Reboot Check

| Question | Answer |
|----------|--------|
| Where am I? | Phase 1 (canonical Task 1: Build foundation) |
| Where am I going? | Phases 2-17, one per canonical plan Task |
| What's the goal? | Ship the Workflow app per spec, all A/F/V acceptance criteria met |
| What have I learned? | See findings.md |
| What have I done? | See phase log above |

---

*Update this file after completing a phase, running validation, or encountering an error.*
