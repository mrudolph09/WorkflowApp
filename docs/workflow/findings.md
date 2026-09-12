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

## Issues Encountered

| Issue | Resolution |
|-------|------------|
| Task 1's `Workflow.csproj` (as written in the canonical plan, line ~255) adds `<Content Include="Assets\autoanswer.rules.json">` before that file exists — Task 5 is the task that creates it. An explicit (non-glob) `Content Include` for a missing file is `MSB3030` at build time (`CopyToOutputDirectory` can't copy a file that isn't there), so Task 1's own "0 Warning(s), 0 Error(s)" expectation cannot be met as sequenced. The glob `Assets\Terminal\**\*.*` does NOT have this problem (globs matching 0 files are fine). | Implementation-detail deviation, not architectural: created a minimal placeholder `Workflow\Assets\autoanswer.rules.json` (`{}`) during Task 1 so the build succeeds. Task 5 overwrites it with the real rule set from spec §7.4 as its own TDD deliverable — no change to what Task 5 produces, just an empty file exists one task earlier than planned. |
| Plan Task 1 Step 8a's "policy probe" (a bare interface member, expecting `error IDE0040`) is specified to be added to `Workflow.Tests\PlaceholderTests.cs`. Verified this does NOT fail the build — probe built clean with 0 errors. Root cause (systematic-debugging, not assumed): spec §4.3 itself documents that `Workflow\.editorconfig` has `root = true` and lives inside `Workflow\Workflow\`, so `Workflow.Tests\` (a sibling directory) does **not** inherit it. `dotnet_style_require_accessibility_modifiers = always:error` — the setting that turns a bare interface member into a build error — lives only in that `.editorconfig`, not in `Directory.Build.props`/`.roslyn` (which only sets `AnalysisMode`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, etc., repo-wide). So a probe in `Workflow.Tests` can never demonstrate this specific rule regardless of whether the `.roslyn` import is wired correctly — it is testing the wrong scope. | Implementation-detail correction to the *verification step only* (nothing shipped changes): re-ran the probe as a temporary file inside the `Workflow` project instead (`Workflow\PolicyProbeTemp.cs`, delebted after). Confirmed `error IDE0040` fires there, proving both the `Directory.Build.props` → `.roslyn` import AND the `.editorconfig` scoping are wired correctly. Build is clean (0/0) again after removing the temp probe. No change to `Workflow.Tests\PlaceholderTests.cs` beyond leaving it as originally shipped (no probe residue). |

## Resources

- Spec: `docs/superpowers/specs/specification.md`
- Plan: `docs/superpowers/plans/implementationplan.md`
- Repo root: `C:\Users\Marco\Documents\repo\Workflow`
- Solution: `C:\Users\Marco\Documents\repo\Workflow\Workflow.sln`

---

*Update this file after any discovery, deviation, or resolved ambiguity.*
