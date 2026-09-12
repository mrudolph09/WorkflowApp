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

Start Phase 2 (canonical Task 2: Domain models and `TaskPaths`,
`implementationplan.md:388`) — re-read that section fresh before implementing.

## Current Phase

Phase 2

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
- **Status:** pending

### Phase 3: `TaskFolderService`

- Canonical task: `docs/superpowers/plans/implementationplan.md:749` (## Task 3)
- **Status:** pending

### Phase 4: `PromptTemplateService` + repair the 4 prompt templates

- Canonical task: `docs/superpowers/plans/implementationplan.md:1141` (## Task 4)
- Note: spec §9.3(d)/§14 — remove the `qdocimporter/eval` bullet from
  `implementation_prompt.md`, replace with the `verify.ps1` gate. See findings.md.
- **Status:** pending

### Phase 5: Auto-answer rule engine

- Canonical task: `docs/superpowers/plans/implementationplan.md:1738` (## Task 5)
- **Status:** pending

### Phase 6: `ArtifactWatcher`

- Canonical task: `docs/superpowers/plans/implementationplan.md:2298` (## Task 6)
- **Status:** pending

### Phase 7: `SettingsService`

- Canonical task: `docs/superpowers/plans/implementationplan.md:2909` (## Task 7)
- **Status:** pending

### Phase 8: ConPTY terminal session (opens with a BLOCKING spike — spec §6.3.1)

- Canonical task: `docs/superpowers/plans/implementationplan.md:3274` (## Task 8)
- No later task may start until the spike demonstrably streams output; if the
  proven sequence differs from spec §6.3, update the spec section before
  committing `ConPtySession` code (this is an allowed implementation-detail
  deviation per the plan review notes — record the actual sequence found).
- **Status:** pending

### Phase 9: `WorkflowOrchestrator`

- Canonical task: `docs/superpowers/plans/implementationplan.md:4240` (## Task 9)
- **Status:** pending

### Phase 10: Terminal web assets (xterm.js vendoring)

- Canonical task: `docs/superpowers/plans/implementationplan.md:5116` (## Task 10)
- **Status:** pending

### Phase 11: WebView2 terminal host

- Canonical task: `docs/superpowers/plans/implementationplan.md:5366` (## Task 11)
- **Status:** pending

### Phase 12: Converters, behaviours and styles

- Canonical task: `docs/superpowers/plans/implementationplan.md:5979` (## Task 12)
- **Status:** pending

### Phase 13: `PhaseIndicatorViewModel` and `TaskTabViewModel`

- Canonical task: `docs/superpowers/plans/implementationplan.md:6476` (## Task 13)
- **Status:** pending

### Phase 14: Shell view model, composition root and theme

- Canonical task: `docs/superpowers/plans/implementationplan.md:7371` (## Task 14)
- **Status:** pending

### Phase 15: XAML views

- Canonical task: `docs/superpowers/plans/implementationplan.md:7916` (## Task 15)
- **Status:** pending

### Phase 16: ArmFlex application icon

- Canonical task: `docs/superpowers/plans/implementationplan.md:8328` (## Task 16)
- **Status:** pending

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
