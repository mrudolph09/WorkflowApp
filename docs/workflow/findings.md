# Findings & Discoveries — Workflow implementation

Execution-state layer only. Treat this as a log of what was discovered while
implementing the canonical spec/plan — not a place to redesign anything.

## Requirements (source: canonical spec, restated for quick recall)

- WPF/.NET 8 app, MahApps + MaterialDesign, multi-tab, one Task per tab.
- Each Task drives a 4-phase pipeline (Spezifikation → Review → Review
  umsetzen → Implementierung) through `yo` / `codex --yolo` in a ConPTY
  session rendered via xterm.js in WebView2.
- Full acceptance criteria: spec §15 (A1–A9 build/static-analysis, F1–F21
  functional, V1–V11 verification steps).

## Research Findings

- Repo state at task start: `Workflow.sln` + `Workflow\` project skeleton
  (default WPF template: App.xaml, MainWindow.xaml only). No git repo yet
  (`git status` → fatal: not a git repository). No `Workflow.Tests` project.
  No `docs/workflow/` prior to this session.
- `dotnet --list-sdks`: 5.0.411, 7.0.203, 7.0.317, 8.0.425, 9.0.302, 10.0.401
  all present under `C:\Program Files\dotnet\sdk`. `net8.0-windows` target is
  covered by 8.0.425.
- `mdmeta` (the CLAUDE.md-mandated markdown-header tool) is not on PATH in
  Git Bash (`bash: mdmeta: command not found`) but resolves as a function in
  PowerShell. Use the PowerShell tool for `mdmeta`, not Bash.
- Implementation plan line anchors for the 17 canonical tasks (verified via
  `grep -n "^## Task"` against `docs/superpowers/plans/implementationplan.md`):
  Task 1:149, Task 2:388, Task 3:749, Task 4:1141, Task 5:1738, Task 6:2298,
  Task 7:2909, Task 8:3274, Task 9:4240, Task 10:5116, Task 11:5366,
  Task 12:5979, Task 13:6476, Task 14:7371, Task 15:7916, Task 16:8328,
  Task 17:8531. Total file length 8813 lines.

## Technical Decisions

| Decision | Rationale |
|----------|-----------|
| PWF files live at `docs/workflow/{task_plan,findings,progress}.md`, not the skill's default `.planning/<id>/` layout | User's explicit instruction names these exact paths; user instructions outrank a skill's generic defaults per the harness rule that user instructions override skill behavior where they conflict |
| No eval-gate work will be done against `qdocimporter/eval` (VB6/.NET Framework 4.8 project at `C:\vb5\QDocImport`) | See "Resolved conflict" below |

## Resolved conflict: "qdocimporter/eval" completion requirement

The generic task-runner instructions given for this job (conversational
context, lowest priority in the source-of-truth hierarchy) list as a
completion requirement: *"appropriate eval gates has been added to
qdocimporter/eval"*. This is boilerplate copied from
`Workflow\Prompt\implementation_prompt.md`'s own `Completion requires:` list —
the same template this app renders and pastes into its own phase-4 CLI agent.

The **canonical, approved specification** (`specification.md` §3.2, §14,
§9.3(d)) explicitly:
- Lists "Writing eval gates into `QDocImport`" under out-of-scope (§3.2), with
  rationale "Unrelated product. See §14."
- §14 declares this item **Not Applicable** to the Workflow app: `..\QDocImport\Eval`
  does not exist relative to this repo; the real directory
  (`C:\vb5\QDocImport\QDocImport\Eval`) belongs to an unrelated, regulated
  .NET Framework 4.8 / NUnit medical-data importer that shares nothing with
  this codebase.
- §9.3(d) requires the bullet `* appropriate eval gates has been added to
  qdocimporter/eval` to be **deleted** from `Workflow\Prompt\implementation_prompt.md`
  and replaced with `* the acceptance gate \`Workflow\verify.ps1\` exits 0`, and
  requires a test (`PromptTemplateServiceTests`, spec A8) asserting the shipped
  template contains no case-insensitive `qdocimport` reference.
- The canonical implementation plan's Task 4 (line 1141) implements exactly
  this edit as one of its four required prompt-file changes.

**Resolution, per the source-of-truth hierarchy in `task_plan.md`:** the
approved specification and plan (priorities 1–2) outrank the generic
conversational instruction (priority 7). This implementation will:
- NOT touch `C:\vb5\QDocImport` or its `Eval` directory in any way.
- Remove the `qdocimporter/eval` bullet from `implementation_prompt.md` and
  replace it with the `Workflow\verify.ps1` gate, exactly as spec §9.3(d)
  requires, in Phase 4 / canonical Task 4.
- Treat `Workflow\verify.ps1` (spec §15.3, plan Task 17) as *the* eval/acceptance
  gate for this application, satisfying the intent behind the generic
  instruction ("a machine-checkable gate that runs after everything and
  verifies the outcome") without coupling two unrelated codebases.

This is not an architectural deviation from the canonical plan — it is the
canonical plan's own explicit, already-approved instruction. No further user
confirmation is being requested for this specific point; it is recorded here
for auditability per the "Plan deviations" protocol.

## Task 8 spike (spec §6.3.1 / plan D15) — BLOCKING finding, escalated to user

**Status: spike run to completion; root cause identified as environmental, not
fixable by changing the ConPTY call sequence. Escalating per the plan's own
"if the spike cannot be made to work, stop and escalate" instruction.**

### What was done

A standalone `net8.0` console app was built at
`%TEMP%\claude\...\scratchpad\conpty-spike\` (outside the solution, per Step 0)
implementing the exact documented call sequence (CreatePipe x2 →
CreatePseudoConsole → InitializeProcThreadAttributeList →
UpdateProcThreadAttribute → CreateProcess → synchronous FileStream.Read loop).

**Baseline run** (`pwsh -NoLogo -NoExit`, matching `ConPtySessionTests`):
reproduced the spec's own documented finding byte-for-byte: all native calls
return success, the child *is* running (its own prompt appears — see below),
but **zero bytes** are ever read from the output pipe for the child's actual
output.

**Explicit-resize hypothesis** (spec §6.3.1: "Output appears only after an
explicit ResizePseudoConsole (115–126 bytes)"): tested directly — calling
`ResizePseudoConsole` after `CreateProcess` does unblock the read, but only
for a **blank redraw frame** (measured 115 bytes for `cmd.exe`, 126 for
`pwsh.exe` — this exact 115–126 range matches the spec's own number
independently). The frame is `<ESC>[?25l<ESC>[2J<ESC>[m<ESC>[H` + blank lines
+ a title-set sequence — i.e. the PTY's own virtual screen buffer is
genuinely empty of the child's real output, not merely un-flushed.

**Simplest-possible-child control** (`cmd.exe /c echo HELLO_FROM_CMD`, no
PSReadLine/interactivity involved at all): identical failure. Rules out
anything PowerShell-specific.

**The most load-bearing observation:** in every run, the child's own prompt
or output text (`PS C:\...\>`, `HELLO_FROM_CMD`, `WF_MARKER_OK`) appeared
**directly in the spike's own captured stdout**, as plain unescaped text,
with no `[reader]` log prefix — i.e. it never went through the `appRead`
pipe at all. It is reaching some other, real, rendering console.

**Environment diagnostics run from inside the spike / the shell hosting it:**
- `Environment.UserInteractive = True`; `GetProcessWindowStation()` returns a
  valid handle named `WinSta0` (the normal interactive window station).
- `[Console]::WindowWidth` from the shell hosting these tool calls throws
  "Das Handle ist ungültig" (invalid handle), and `IsOutputRedirected` /
  `IsInputRedirected` are both `True`.
- **`query session` shows this automation session (session 1) as `Getr.`
  (disconnected), while a separate session (3, named `console`) is `Verb.`
  (connected)** — i.e. there IS a normal interactive desktop session on this
  machine, but the session this agent's shell tools execute in is a
  *different*, disconnected one.

### Untested candidates from the plan — outcome

1. **Overlapped I/O + named pipes** — not attempted (largest rewrite; given
   the evidence below points at session/desktop attachment rather than the
   pipe mechanism, this was deprioritised, not ruled impossible).
2. **`PSEUDOCONSOLE_INHERIT_CURSOR` (dwFlags=1)** — tried. Made things
   *worse*: `ClosePseudoConsole` then hung indefinitely (had to add a 25s
   watchdog + force-kill to recover). A regression, not a fix.
3. **Inheritable `SECURITY_ATTRIBUTES` on the pipes instead of NULL** —
   tried. No change in behaviour at all versus the baseline.

### Root cause assessment (systematic-debugging Phase 1-3 applied)

The evidence is consistent with: **ConPTY's underlying console-subsystem
plumbing (the hidden `OpenConsole.exe`/`conhost.exe` host it spins up) does
not render/stream correctly when the calling process runs in a disconnected
Windows session**, which is exactly the kind of session this agent's shell
tools run under on this machine (session 1, disconnected, vs. the real
interactive desktop at session 3). This is an environmental condition of
*how this spike was executed*, not a defect in the documented P/Invoke
sequence — the sequence matches Microsoft's own `CreatePseudoConsole` sample
and every individual API call reports success with correctly-populated
data (attribute list bytes, HRESULTs), exactly as the spec's original probe
also found.

This has not been proven with 100% certainty (the true fix — running the
identical spike from an actual interactive session 3 process — is not
something this agent can do from here), but three independent lines of
evidence converge on it: (a) the exact byte-range match with the spec's own
number, (b) the plain-text leak of child output bypassing the pipe entirely,
and (c) the concrete, verifiable session-disconnection fact from `query
session`.

### Why this blocks the plan as written

Spec A9 requires: "The ConPTY spike (§6.3.1) has been run and its result
recorded... `ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput`
passes against a real pseudo-console." Plan D15 / Task 8 Step 0 requires all
three exit criteria (marker round-trip, launcher-frame-before-input, proven
sequence written back) before Step 1 may proceed. If this agent's execution
context cannot itself validate streaming — regardless of which of the
remaining candidates is tried — then the automated `dotnet test` run for
`ConPtySessionTests` in *this* environment cannot be expected to pass either,
even once real production code is written, because the underlying session
constraint applies equally to `dotnet test`'s test-host process.

This is an **architectural/environmental** blocker per the "Plan deviations"
protocol (not an implementation detail), because it affects whether Task 8's
own stated acceptance gate is achievable from this session at all - escalated
to the user rather than silently choosing a path.

### User decision (2026-09-12)

Presented 4 options. User chose: **implement `ConPtySession` exactly as
documented in spec §6.3 / plan Task 8 (it matches Microsoft's canonical
sample and every individual native call already reports success here), run
every other Task 8 test to green, and treat the two tests that require real
sustained streaming
(`Start_RunsACommandAndStreamsItsOutput`, `Start_EmitsTheLauncherFrameBeforeAnyInput`)
as written-but-to-be-verified-by-the-user** in a normal interactive session
(or by running the shipped app). Proceeding on this basis: Task 8's code is
implemented verbatim per the plan; the two streaming-dependent tests are run
and their actual result here is reported honestly (not assumed), and flagged
for the user's own manual confirmation per spec V5/A9 rather than treated as
a hard gate this session can self-certify.

| Task 8: `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` on the `NativeMethods` class (exactly as the plan's snippet has it) fails with `error CS0592` — the attribute is only valid on a method or an assembly, never a class. A real defect in the plan's own code, not something the analyzer-conformance section anticipated. | Moved the attribute to assembly scope: `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` in `Workflow\AssemblyInfo.cs`, with a comment explaining why (satisfies CA5392 for every `[DllImport]` in the assembly, since all P/Invoke here is kernel32). Removed the invalid class-level attribute, kept an explanatory comment in `NativeMethods.cs` pointing at `AssemblyInfo.cs`. |
| Task 8: `ConPtySession.Resize` calls `NativeMethods.ResizePseudoConsole(...)` without consuming its HRESULT return value -> `error CA1806` (already `error` via `.editorconfig`, not just `.roslyn`). | Discarded the result explicitly (`_ = NativeMethods.ResizePseudoConsole(...)`) with a comment: a resize failure isn't fatal, the terminal just keeps its previous size. |
| Task 8 (`ConPtySessionTests.cs`, verbatim plan code): `RunAndCaptureAsync` creates `new SemaphoreSlim(0, 1)` without `using` -> real `CA2000` (never disposed). `Start_EmitsTheLauncherFrameBeforeAnyInput` has `exitCode is null ? ... : ...` where CA1508 statically proves `exitCode` "always null", because its static, single-threaded dataflow analysis cannot see that the `Exited` event's lambda assigns it concurrently, from a different thread, which is exactly the race the message exists to report. | `SemaphoreSlim`: added `using`. `CA1508`: narrow `#pragma warning disable/restore CA1508` around just that `Assert.True` call, with a comment explaining the analyzer's blind spot - consistent with how `CA1031` is already suppressed elsewhere in this codebase (local pragma, never a project-wide `NoWarn`). |
| Task 9 (`WorkflowOrchestratorTests.cs`, verbatim plan code): 6 of 10 tests failed. Diagnosed root cause via systematic-debugging: `FakeTerminalController.StartSession` resets `OutputCount`/`LastOutputUtc` to a fresh baseline (correctly mirroring the production contract), but 5 tests never called `terminal.ReadyGate.SetResult()` at all (hanging `WaitUntilReadyAsync` forever) and/or called `terminal.EmitOutput()` *before* `RunAsync`/`StartSession` ran, so the reset silently wiped it out and Gate A never opened (`ANonMatchingRuleSetSendsThePromptPromptlyInsteadOfWaitingOutTheCeiling` then waited out the full `settleTimeoutMs` ceiling instead of exiting promptly). The plan's own note right after Step 5 claims "existing orchestrator tests must be updated for the new contract" - this update was incomplete for `Phase1_WritesCdThenLauncherThenPastesThePrompt`, `SettleLoop_SendsTheMatchingAutoAnswerBeforeThePrompt`, `ANonMatchingRuleSetSendsThePromptPromptlyInsteadOfWaitingOutTheCeiling`, `AnAutoAnswerRuleFiresAtMostOncePerPhase`, `Phases_ChainThroughAllFourStations`, `ManualSignal_AlsoEndsANonManualPhase`, `Cancellation_DisposesTheSession` - only the 4 explicitly-new "regression" tests had it applied correctly. | Added `terminal.ReadyGate.SetResult();` to all 6 affected tests; for the 3 that additionally needed Gate A to open promptly (`SettleLoop_...`, `ANonMatchingRuleSet...`, `AnAutoAnswerRuleFiresAtMostOncePerPhase`), moved/added the `EmitOutput()` call to fire ~200ms *after* `RunAsync` starts (after `StartSession` has already run and reset the counters), matching the pattern the plan's own correctly-written regression tests already use. Re-ran twice to rule out timing flakiness given these are all delay-based async tests - stable both times (16/16). |
| Task 9: `FakeTerminalController.StartedSessions`/`Sent`/`Pasted` (public `List<string>` properties, verbatim plan code) -> `CA1002`. `WorkflowOrchestratorTests.Phases_ChainThroughAllFourStations` uses `s.EndsWith("\r", StringComparison.Ordinal)` on a single-char string -> `CA1865` (use the `char` overload). Neither rule is new to the analyzer-conformance list, but neither had been hit by a `List<string>`-returning test double or a single-char `EndsWith(string)` call before this task. | `List<string>` -> `Collection<string>` (same fix as `AppSettings.RecentDirectories` in Task 7). `EndsWith("\r", ...)` -> `EndsWith('\r')`. |

| Task 10: the plan's Step 5 has `Workflow.Tests.csproj` declare its own `<Content Include="..\Workflow\Assets\Terminal\**\*.*" LinkBase="Assets\Terminal">`, duplicating what `Workflow.csproj` already declares (Task 1). Verified this is unnecessary: `TerminalAssetTests` (all 7) passed with 0 changes to `Workflow.Tests.csproj`, and `ls Workflow.Tests\bin\...\Assets\Terminal\` confirmed all 5 files really do land there. | Root cause: MSBuild's SDK-style project system copies a *referenced* project's `Content` items (`CopyToOutputDirectory=PreserveNewest`) transitively into every project that consumes it via `ProjectReference` — `Workflow.Tests` already references `Workflow.csproj`, so its Task-1 Content items (Prompt, autoanswer.rules.json, Assets\Terminal) all propagate automatically. Skipped the plan's Step 5 as redundant (implementation-detail deviation, not architectural — the assets already reach the test host); adding a second, identically-targeted `Content` item risks an MSBuild duplicate-output-item warning, which would be a build error under this project's `TreatWarningsAsErrors` policy. |

## Issues Encountered

| Issue | Resolution |
|-------|------------|
| Task 1's `Workflow.csproj` (as written in the canonical plan, line ~255) adds `<Content Include="Assets\autoanswer.rules.json">` before that file exists — Task 5 is the task that creates it. An explicit (non-glob) `Content Include` for a missing file is `MSB3030` at build time (`CopyToOutputDirectory` can't copy a file that isn't there), so Task 1's own "0 Warning(s), 0 Error(s)" expectation cannot be met as sequenced. The glob `Assets\Terminal\**\*.*` does NOT have this problem (globs matching 0 files are fine). | Implementation-detail deviation, not architectural: created a minimal placeholder `Workflow\Assets\autoanswer.rules.json` (`{}`) during Task 1 so the build succeeds. Task 5 overwrites it with the real rule set from spec §7.4 as its own TDD deliverable — no change to what Task 5 produces, just an empty file exists one task earlier than planned. |
| Task 6 (`ArtifactWatcherTests`): 4 test methods matching the pattern `using var watcher = Create(...); var waiting = CompletesAsync(watcher); <do something>; Assert.True(await waiting);` triggered `error CA2025` ("Ensure that tasks that use IDisposable instances are completed before the instances are disposed"). Root cause (verified, not assumed): CA2025's static analysis flags any case where a Task obtained from a call involving a `using`-scoped `IDisposable` is stored in a variable rather than awaited in the same statement, regardless of whether a later `await` in the same method actually makes it safe — it does not do flow analysis that far. One test (`DeletingTheWatchedDirectory_...`) separately hit a genuine `CA2000` (a `new CancellationTokenSource(Timeout)` passed inline was never disposed). Neither rule is in the plan's or spec's documented `NoWarn`/analyzer-conformance lists (§4.4/§4.4.1) — a new blanket suppression would be a spec change per A7, and was not warranted since both are real, fixable code issues. | Fixed in test code only (no production/API change, no NoWarn added): replaced `using var watcher = ...` with plain `var watcher = ...` + `try { ... } finally { watcher.Dispose(); }` so disposal is textually and provably after the `await waiting` — confirmed by experiment (fixed one occurrence, rebuilt, confirmed only that error cleared) before applying to the other 3. Gave the inline `CancellationTokenSource` in `DeletingTheWatchedDirectory_...` its own `using` variable. All 4 CA2025 + the 1 CA2000 cleared; 0 warnings/0 errors. |
| Task 6 (`ArtifactWatcherTests.AnyContentChanged_DoesNotFireWhileAFileIsExclusivelyLocked`): failed with `Assert.Throws() Failure: Exception type was not an exact match — Expected: OperationCanceledException, Actual: TaskCanceledException`. Root cause: `Task.WaitAsync(CancellationToken)` throws `TaskCanceledException` (a subtype of `OperationCanceledException`) on cancellation; xUnit's `Assert.ThrowsAsync<T>` requires an *exact* type match, unlike `Assert.ThrowsAnyAsync<T>`. Every *other* cancellation assertion in this exact same test file already correctly used `ThrowsAnyAsync` — this one test was the sole, inconsistent outlier in the plan's own verbatim test code. | Changed this one assertion from `Assert.ThrowsAsync<OperationCanceledException>` to `Assert.ThrowsAnyAsync<OperationCanceledException>`, matching the established pattern used by every sibling test in the file. Re-ran: 12/12 passed, stable across 2 consecutive runs (these tests depend on real filesystem/timer timing). |
| Plan Task 1 Step 8a's "policy probe" (a bare interface member, expecting `error IDE0040`) is specified to be added to `Workflow.Tests\PlaceholderTests.cs`. Verified this does NOT fail the build — probe built clean with 0 errors. Root cause (systematic-debugging, not assumed): spec §4.3 itself documents that `Workflow\.editorconfig` has `root = true` and lives inside `Workflow\Workflow\`, so `Workflow.Tests\` (a sibling directory) does **not** inherit it. `dotnet_style_require_accessibility_modifiers = always:error` — the setting that turns a bare interface member into a build error — lives only in that `.editorconfig`, not in `Directory.Build.props`/`.roslyn` (which only sets `AnalysisMode`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, etc., repo-wide). So a probe in `Workflow.Tests` can never demonstrate this specific rule regardless of whether the `.roslyn` import is wired correctly — it is testing the wrong scope. | Implementation-detail correction to the *verification step only* (nothing shipped changes): re-ran the probe as a temporary file inside the `Workflow` project instead (`Workflow\PolicyProbeTemp.cs`, delebted after). Confirmed `error IDE0040` fires there, proving both the `Directory.Build.props` → `.roslyn` import AND the `.editorconfig` scoping are wired correctly. Build is clean (0/0) again after removing the temp probe. No change to `Workflow.Tests\PlaceholderTests.cs` beyond leaving it as originally shipped (no probe residue). |

## Resources

- Spec: `docs/superpowers/specs/specification.md`
- Plan: `docs/superpowers/plans/implementationplan.md`
- Repo root: `C:\Users\Marco\Documents\repo\Workflow`
- Solution: `C:\Users\Marco\Documents\repo\Workflow\Workflow.sln`

---

*Update this file after any discovery, deviation, or resolved ambiguity.*
