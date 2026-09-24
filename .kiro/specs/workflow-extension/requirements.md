# Requirements Document

## Introduction

Workflow users running implementation plans that exceed one agent session's context need an optional subtask execution mode. Phase 4 decomposes an existing spec and plan into an ordered set of subtasks in a separate tracking repository, then executes each in a fresh session with visible progress and recovery from disk.

**Status: imported draft, validated and fully resolved on 2026-09-18.** The import preserved supported behavior without silently resolving contradictory source statements; the design validation then resolved all twelve recorded conflicts, and the criteria below carry those decisions. English is the specification language; product labels and messages remain German where specified by the sources.

Sources, preserved in place:

- [SPEC](../../../docs/superpowers/specs/2026-09-17-subtask-execution-design.md): sections 1–19; original status “Approved for planning”.
- [PLAN](../../../docs/superpowers/plans/2026-09-17-subtask-execution-plan.md): technical decisions and validation examples, not cc-sdd tasks.

Source A1–A27 and E1–E17 below are provenance labels only. Canonical requirements use numeric IDs.

## Boundary Context

- **In scope:** optional phase-4 decomposition and sequential execution, configuration, progress, file protocol, recovery, prompt validation/corrections, removal of manual phase completion, and closing the tab after manual task completion.
- **Out of scope:** parallel or nested subtasks, automatic in-run retry, execution timeouts, a subtask editor/reorder/delete UI, migrating old tasks, and application git operations against the tracking repository.
- **Adjacent expectations:** the user supplies a tracking directory containing `task_template`; agent sessions produce the described files. Other tracking documents remain agent/human concerns, not application completion evidence. Required template changes are separate-repository work: the missing per-subtask `status.json` and non-empty example `result.json` files are mandatory, while deleting `verification.json` and adding a README are out of scope.
- **Known limitation:** tabs with the same task name and tracking directory share process state; collision prevention is explicitly outside this feature (SPEC E14).

## Requirements

### Requirement 1: Configure optional subtask execution

**Objective:** As a Workflow user, I want to select subtask mode and a tracking directory before implementation begins.

#### Acceptance Criteria

- **1.1** The Workflow application shall show a `Subtasks` checkbox beside `Implementierung`, with tooltip `Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks`. (A1)
- **1.2** While subtask mode is selected, the application shall show a `Workflow-Verzeichnis` recent-directory selector and folder picker; while it is deselected, the application shall hide that row. (A2)
- **1.3** If the selected tracking directory is blank, absent, or lacks `task_template`, the application shall show an explanatory German message and disable starting a workflow with subtask mode enabled. (A3; E1–E2)
- **1.4** When a valid tracking directory is selected, the application shall persist it as the last choice and in a separate recent-directory history and offer it for subtask mode on a new tab. (A4)
- **1.5** When a task starts with subtask mode selected, the application shall persist the mode and tracking directory with that task; when those options change for an existing task, it shall persist the changes. (A5; SPEC 8.3)
- **1.6** While phases 1–3 run, the application shall allow subtask configuration changes and capture the configuration as one immutable snapshot at phase-4 entry; while phase 4 is active, it shall prevent changes to every subtask input, including the folder-picker button. (SPEC 8.1, 11.6; design issue 3 resolved)
- **1.7** When older settings or task records lack subtask fields, the application shall load the existing defaults and treat old tasks as having subtask mode disabled. (E15–E16)
- **1.8** If subtask mode is enabled but its tracking directory is invalid at phase-4 entry, the application shall raise a configuration error, present the German message, and leave implementation recoverable; it shall not fall back to a normal implementation run. (design issue 3 resolved)

### Requirement 2: Decompose and execute in declared order

**Objective:** As a user, I want bounded agent sessions to execute the supplied plan without losing its order.

#### Acceptance Criteria

- **2.1** When phase 4 starts with subtask mode disabled, the application shall retain the existing implementation prompt and task done-marker completion behavior. (A6)
- **2.2** When phase 4 starts with subtask mode enabled and decomposition is not reusable, the application shall start a fresh decomposition session using the existing spec and plan and the task-specific tracking destination. (A7)
- **2.3** When a readable ordered index exists, the application shall reuse that decomposition regardless of individual missing subtask descriptions; a folder without a usable description shall fail only its own entry. (A8; design issue 5 resolved)
- **2.4** When executing subtasks, the application shall follow the index order and use one fresh session at a time in the product working directory. (A9)
- **2.5** When starting a subtask session, the application shall supply that subtask's full description and correctly resolved tracking paths. (A10)
- **2.6** When a session publishes its non-empty completion flag, the application shall read the associated status payload to determine the outcome. (A11)
- **2.7** If an attempted subtask reports a non-complete outcome, the application shall count the failure and proceed without retrying that subtask in the same run; a subtask left failed or pending by an earlier run shall be eligible for exactly one fresh attempt in each later run. (A12–A13; design issue 2 resolved)
- **2.8** While a session has not published its completion flag, the application shall keep waiting with a live interactive terminal and no execution timeout. (E4, E8)
- **2.9** If the tracking directory becomes invalid or decomposition produces an unusable index, the application shall show an actionable German error and leave implementation recoverable. (E3, E5)
- **2.10** If a listed title is blank or unsafe, the application shall report and skip that entry without accessing paths outside the tracking repository; if an unfinished entry has no usable description, it shall report that failure and continue with other entries. No individual bad entry shall invalidate the index. Only the exact names `.` and `..` shall be rejected as unsafe dot-names. (E6; design issues 4 and 5 resolved)
- **2.11** After a session publishes its completion flag, the application shall re-read the status payload until the read and the parse both succeed, retrying at 200 ms up to five times, and shall treat exhausted retries as a failure of that subtask. (SPEC 9.6; design issue 10 resolved)
- **2.12** When the index lists the same title more than once, the application shall de-duplicate case-insensitively at parse time, keeping the first occurrence, so that the reported total counts unique titles. (design issue 4 resolved)

### Requirement 3: Display progress and completion

**Objective:** As a user, I want to distinguish completed work, active work, and failures.

#### Acceptance Criteria

- **3.1** While subtask mode is selected, the application shall show a second indicator labelled `Subtasks`. (A16)
- **3.2** When decomposition is running, the indicator shall show `Zerlegung läuft…`; while the loop runs, it shall show completed and total counts as `{N} von {M}`, updating after each subtask. (A17)
- **3.3** When failures are present, the indicator shall additionally show `{K} fehlgeschlagen` in the error colour; a subtask whose status is `pending` or any other unrecognized value shall count as pending, not failed, so a freshly decomposed index shows no failures. (A18; design issue 1 resolved)
- **3.4** The subtask indicator shall be grey before phase 4, yellow while running, and green only when the total is greater than zero and every subtask is complete; the absence of failures alone shall not turn it green. (A19; design issue 6a resolved)
- **3.5** When automatic execution establishes that every subtask is complete, the application shall record implementation as completed and show both indicators green. (A14)
- **3.6** If automatic execution finishes with failures, the application shall leave implementation incomplete, show its indicator yellow, and retain the task for recovery. (A15)
- **3.7** When a subtask has a failure reason, the application shall carry the ordered subtask states in its progress payload and make each failed entry's title and reason available in the indicator tooltip, for both live progress and progress restored on recovery. (SPEC 6.2, 11.3; design issue 7 resolved)

### Requirement 4: Recover from authoritative disk state

**Objective:** As a user, I want to continue interrupted work without repeating completed subtasks.

#### Acceptance Criteria

- **4.1** When the application restarts after an interrupted subtask run, it shall offer the task for recovery, restore the task's mode and tracking directory, and derive progress from disk. (A20)
- **4.2** When the user continues a recoverable subtask task, the application shall skip completed subtasks and attempt every other subtask, failed or pending, once each in index order. (A21; design issues 2 and 5 resolved)
- **4.3** When all subtasks of a subtask-mode task are complete, recovery reconciliation shall not demote its completed implementation merely because the normal task done-marker is absent. (A22)
- **4.4** When a status payload records completion but its session flag is absent, the application shall treat that subtask as complete on recovery. (SPEC 13.1)
- **4.5** The application shall derive subtask progress from tracking files without persisting a separate current-subtask cursor. (SPEC 13.1)
- **4.6** If the stored tracking path is blank or malformed, or the final index cannot be read, the application shall retain the recorded phase state and surface the condition, and shall neither complete the task from stale evidence nor demote it via the normal done-marker. (design issue 9 resolved)

### Requirement 5: Complete phases and close tasks

**Objective:** As a user, I want artifact-driven phase completion and an explicit task completion action.

#### Acceptance Criteria

- **5.1** The application shall remove the `Phase abschliessen` action from all phase indicators. (A23)
- **5.2** While phases 1–3 run, the application shall advance only on their artifact conditions, with the terminal remaining interactive if no artifact arrives. (SPEC 12.1)
- **5.3** When the user invokes `Task abschliessen` during implementation, the application shall signal the run, await its end, record the completion as an explicit manual override distinguishable from automatic completion, and then close the task tab. (A24; design issue 6b resolved)
- **5.4** When a subtask-mode task was completed manually, recovery shall honour that completion without consulting the ledger, and the indicator shall continue to show the real subtask counts rather than a green all-complete state. (design issues 6b and 9 resolved)

### Requirement 6: Maintain the tracking and prompt contracts

**Objective:** As an operator, I want the application and agent sessions to agree on completion evidence.

#### Acceptance Criteria

- **6.1** The application shall use a task-specific ordered index and per-subtask status and completion files, without inferring order from directory sorting or reading unrelated tracking documents as completion evidence. (SPEC 5–7, 10)
- **6.2** When agents publish a subtask outcome, the prompt contract shall require the status payload before a non-empty completion flag, with atomic temporary-file replacement for both. (SPEC 14)
- **6.3** When the application starts, it shall validate every shipped prompt template — currently eight files, including the code-unreferenced `counter_prompt.md` and `evidence_gate.md` — against its own supported substitutions, report any shipped file the catalog does not recognize, and surface invalid templates through the existing start gate. (SPEC 14.3; validation survey 2026-09-18)
- **6.4** The subtask prompts shall use task-local singular flag names and distinguish application substitutions from names chosen by the agent. (SPEC 14.1–14.2)
- **6.5** The tracking repository template shall provide the missing per-subtask status example and non-empty example `result.json` files at both task and subtask level, because the application's non-empty-file rule reads the present 0-byte example as no flag at all. Deleting `verification.json` and adding a tracking-repository README remain out of scope. (SPEC 15 T1; design issue 8 resolved)
- **6.6** Every substituted path token shall be absolute, including a `task_path` token, so that no prompt composes a path from a root plus a relative fragment or re-derives a path the application already supplies. (design issue 11 resolved)
- **6.7** The creation prompt shall publish its ordered index as the task-local `{task_path}/result.json` carrying the ordered subtask array, and shall show an initial status example of `pending`. (design issues 1 and 11 resolved)
- **6.8** Task-wide findings shall be written to a global `findings.md` in the task folder, beside the per-subtask `findings.md` files, so that the `subtasks` directory contains only subtask folders. (SPEC 5.1; design issue 11 resolved)

### Requirement 7: Preserve regression and acceptance gates

**Objective:** As a maintainer, I want evidence that this extension preserves the existing workflow.

#### Acceptance Criteria

- **7.1** When implementation is validated, the solution shall build without warnings or errors. (A25)
- **7.2** When implementation is validated, all tests shall pass without changing pre-existing assertions, except removing assertions for the removed phase-completion command. (A26)
- **7.3** When implementation is accepted, the existing verification script shall exit successfully with the additional subtask checks, and the source manual walkthrough shall be completed after contradictory expectations are resolved. (A27; SPEC 18; PLAN 18)

## Review Status and Open Decisions

The draft maps all source acceptance criteria A1–A27. The technical design recorded twelve unresolved source conflicts as numbered review issues.

**Validated and resolved 2026-09-18.** Design validation returned NO-GO for task generation with three critical clusters. Those were resolved first (issues 1, 2, 3, 5, 6, 9, 10 — status vocabulary, failure eligibility, configuration capture and the editing lock, damaged descriptions, completion authority and the green condition, final evidence, and the settling predicate), and a second pass resolved the remainder (issues 4, 7, 8, 11, 12 — index and title policy, failure-reason transport, external template scope, prompt path and publication contracts, and the discarded validation examples).

All twelve resolutions are recorded in `design.md` under **Resolved Decisions** and are reflected above in criteria 1.6, 1.8, 2.3, 2.7, 2.10, 2.11, 2.12, 3.3, 3.4, 3.7, 4.2, 4.6, 5.3, 5.4, 6.3, 6.5, 6.6, 6.7 and 6.8.

Two codebase corrections from the validation survey are folded in: the prompt directory ships **eight** templates rather than six, and the external `result.json` examples are empty files that the application's non-empty-file rule reads as absent.

No open conflict remains. Residual risks carried knowingly into implementation are listed in `design.md` under **Open Questions / Risks**.
