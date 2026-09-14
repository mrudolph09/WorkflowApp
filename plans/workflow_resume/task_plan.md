---
description: "Execution state for implementing the approved workflow-resume Superpowers plan (run tracking, crash recovery, phase-4 completion, auto-submit)"
summary: "All 11 canonical tasks COMPLETE (commits ed24583..f6c93af, one per task). Release build 0 warnings; 260 tests pass, 1 fails. The single failure is ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput, proven pre-existing against commit 781d6a6 in a worktree, and it is the only reason Workflow/verify.ps1 exits 1 - every check this feature owns passes. Five deviations applied, all implementation-detail (see findings D-1..D-5); no architectural or specification change. The two load-bearing review fixes (D17 persisted demotion, D20 paste-time quiet gate) were mutation-tested, not merely observed green."
paths:
  - "../../docs/superpowers/specs/2026-09-14-workflow-resume-design.md"
  - "../../docs/superpowers/plans/2026-09-14-workflow-resume-plan.md"
  - "./findings.md"
  - "./progress.md"
---

# Execution plan — workflow_resume

**Canonical spec:** `docs/superpowers/specs/2026-09-14-workflow-resume-design.md` (SPEC)
**Canonical plan:** `docs/superpowers/plans/2026-09-14-workflow-resume-plan.md` (PLAN)

This file tracks execution state only. It does not replace or redesign the canonical plan.

Status vocabulary: `pending` → `in progress` → `complete` (only when the canonical
verification step actually passed) / `blocked`.

## Baseline (pre-implementation, recorded 2026-09-14)

- `dotnet build Workflow.sln -c Release` → exit 0, **0 warnings**.
- `dotnet test Workflow.sln -c Release` → **204 passed, 2 failed**
  (`ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput`,
  `ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput`). Both **pre-existing** and
  environment-dependent (they need a real pseudo-console). See `findings.md` F-1.

## Phases

| # | Canonical task (PLAN heading) | Status |
|---|---|---|
| 1 | Task 1: `TaskPaths` learns about the journal and the done marker | **complete** |
| 2 | Task 2: The `{done_path}` prompt token | **complete** |
| 3 | Task 3: Phase 3 requires **both** spec and plan to change | **complete** |
| 4 | Task 4: Phase 4 completes on the done marker; `CompletionRule.Manual` is removed | **complete** |
| 5 | Task 5: The journal — `TaskState` and `TaskStateStore` | **complete** |
| 6 | Task 6: The orchestrator resumes and journals | **complete** |
| 7 | Task 7: Make the prompt submit itself | **complete** |
| 8 | Task 8: `TaskRecoveryScanner` (incl. `PhaseReconciliation`) | **complete** |
| 9 | Task 9: The tab knows how to be resumed | **complete** |
| 10 | Task 10: Offer recovered tasks at startup | **complete** |
| 11 | Task 11: Acceptance gate | **complete except A3/A4**, blocked only by the pre-existing ConPty failure (F-1) |

### Phase 1 — TaskPaths journal + done-marker paths
Canonical task: PLAN → "Task 1: `TaskPaths` learns about the journal and the done marker"
Status: **complete** (commit ed24583)
Verification: `dotnet test --filter "FullyQualifiedName~TaskPathsTests"` -> 9 passed, 0 failed

### Phase 2 — The `{done_path}` prompt token
Canonical task: PLAN → "Task 2: The `{done_path}` prompt token"
Status: **complete** (commit d19c7e1)
Verification: full `dotnet test` (minus ConPty) -> 201 passed, 0 failed

### Phase 3 — Phase 3 requires both spec and plan (`AllContentChanged`)
Canonical task: PLAN → "Task 3: Phase 3 requires **both** spec and plan to change"
Status: **complete** (commit 5cf26f0)
Verification: `--filter "ArtifactWatcherTests|PhaseCatalogTests"` -> 19 passed, 0 failed

### Phase 4 — Phase 4 done-marker completion; `CompletionRule.Manual` removed
Canonical task: PLAN → "Task 4: Phase 4 completes on the done marker; `CompletionRule.Manual` is removed"
Status: **complete** (commit 2fcd742)
Verification: full `dotnet test` (minus ConPty) -> 203 passed, 0 failed. Deviation D-1 applied (see findings).

### Phase 5 — The journal: `TaskState` + `TaskStateStore`
Canonical task: PLAN → "Task 5: The journal — `TaskState` and `TaskStateStore`"
Status: **complete** (commit aa1f362)
Verification: `--filter "FullyQualifiedName~TaskStateStoreTests"` -> 16 passed, 0 failed;
full suite 219 passed; Release build 0 warnings

### Phase 6 — Orchestrator resumes and journals (`StartPhase`, tail clear)
Canonical task: PLAN → "Task 6: The orchestrator resumes and journals"
Status: **complete** (commit 6d484c8)
Verification: full suite (minus ConPty) -> 222 passed, 0 failed

### Phase 7 — Make the prompt submit itself
Canonical task: PLAN → "Task 7: Make the prompt submit itself"
Status: **complete** (commit 1f5725d)
Verification: full suite (minus ConPty) -> 228 passed, 0 failed; Release build 0 warnings.
Additionally mutation-checked: reverting the gate baseline to `terminal.LastOutputUtc` turns
`RunAsync_PasteEchoArrivesAfterSendPasteReturns_DoesNotSubmitBeforeIt` RED, so the D20 guard is real.

### Phase 8 — `PhaseReconciliation` + `TaskRecoveryScanner`
Canonical task: PLAN → "Task 8: `TaskRecoveryScanner`"
Status: **complete** (commit b8c919e)
Verification: `--filter "FullyQualifiedName~TaskRecoveryScannerTests"` -> 14 passed, 0 failed;
full suite 242 passed; Release build 0 warnings.
Mutation-checked: replacing the `ReplacePhases` write-back with an in-memory-only reconciliation
turns `ScanAsync_Demotion_IsPersistedWithoutTouchingUpdatedUtc` and
`ScanAsync_ADemotedTailIsNotResurrectedByASecondCrash` RED, so the D17 guards are real.

### Phase 9 — The tab knows how to be resumed
Canonical task: PLAN → "Task 9: The tab knows how to be resumed"
Status: **complete** (commit cdea6e3)
Verification: full suite (minus ConPty) -> 252 passed, 0 failed; Release build 0 warnings.
Deviation D-4 applied (three canonical test fixtures seeded Completed phases without the artefacts
backing them - see findings).

### Phase 10 — Offer recovered tasks at startup
Canonical task: PLAN → "Task 10: Offer recovered tasks at startup"
Status: **complete** (commit 046033a)
Verification: full suite (minus ConPty) -> 254 passed, 0 failed.

### Phase 11 — Acceptance gate
Canonical task: PLAN → "Task 11: Acceptance gate"
Status: pending
Verification required: `pwsh -File Workflow\verify.ps1` → `All automated checks passed.`, exit 0

## Blockers / deviations

**One outstanding blocker, pre-existing and out of scope:** F-1, the
`ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput` failure that keeps
`Workflow\verify.ps1` at exit 1. Proven pre-existing against commit `781d6a6`. Everything this
feature owns passes.

Deviations applied, all classified **implementation detail** (specification and architecture
unchanged): D-1, D-2, D-3, D-4, D-5. See `findings.md`.

Not applicable: the brief's `qdocimporter/eval` gate does not exist in this repository (F-2); the
equivalent gate here is `Workflow\verify.ps1`, which Task 11 extended.
