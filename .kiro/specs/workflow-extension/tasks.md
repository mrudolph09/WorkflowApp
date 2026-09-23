# Implementation Plan

## 1. Foundation: tracking path, status and ledger contracts

- [x] 1. Establish the disk contract that every other component reads
- [x] 1.1 Compose tracking paths and validate subtask titles
  - Build the path set for a tracking directory plus a task name: task folder, task-level index, subtasks folder, template folder, and per-title description, status and flag locations.
  - Expose every substituted path as an absolute path, including the task folder, so no consumer and no prompt composes a root with a relative fragment.
  - Validate titles before any filesystem access: reject blank and whitespace-only titles, invalid filename characters, directory separators and colon, and the exact names `.` and `..`; accept titles that merely contain `..` inside a longer name.
  - Trim titles before validation.
  - Observable: given a tracking root and a task name, the component returns absolute paths for all six locations, and a title containing a separator is rejected without the filesystem being touched.
  - _Requirements: 2.5, 2.10, 6.1, 6.6_
  - _Boundary: SubtaskPaths_

- [x] 1.2 Define the status, state, snapshot and progress data contracts
  - Model a per-subtask state carrying title, one of pending/complete/failed, and an optional failure reason.
  - Model an ordered snapshot carrying the states plus total, completed and failed counts.
  - Model the progress payload carrying stage, completed, total, failed, the current title, and the ordered state list that delivers failure reasons to the UI.
  - Expose the state list as a read-only collection, honouring the project convention against public mutable list types.
  - Observable: a snapshot built from a known set of states reports counts that match the states, and the progress payload round-trips the reasons.
  - _Requirements: 2.6, 3.7_
  - _Boundary: SubtaskStatus_

- [x] 1.3 Read and normalize the ordered index
  - Parse the task-level index file: optional version absent or at most 1, optional informational task name, and a required non-empty ordered array of titles.
  - Trim entries, then de-duplicate case-insensitively keeping the first occurrence, so the reported total counts unique titles.
  - Keep blank and unsafe entries in the result as failed entries rather than rejecting the whole index; never fall back to directory sorting or alphabetical order.
  - Treat an unreadable or schema-invalid index as an unusable index distinct from an index containing bad entries.
  - Observable: an index listing the same title twice yields one entry, an index with one unsafe title yields all entries with that one marked failed, and a malformed index is reported as unusable.
  - _Requirements: 2.3, 2.10, 2.12, 6.1_
  - _Boundary: SubtaskLedger_

- [x] 1.4 Derive subtask state from tracking evidence
  - Apply the resolved derivation: unsafe title fails; parsed status `complete` completes; parsed status `failed` fails; any other parsed value including `pending` and unrecognized values is pending; unreadable or malformed status fails; absent status is pending whether or not the flag exists; missing or empty description fails.
  - Let a completion status take precedence over a missing description or missing flag.
  - Report a decomposition as reusable when the ordered index is readable, without requiring a description for every entry.
  - Keep the component static, filesystem-only and uncached, with no persisted cursor.
  - Observable: a freshly decomposed tracking folder whose entries all carry `pending` reports zero failures and zero completions, and a folder whose description was deleted after completion still reports that entry complete.
  - _Requirements: 2.3, 2.6, 2.10, 4.4, 4.5, 6.1_
  - _Boundary: SubtaskLedger_

## 2. Configuration, settings and journal persistence

- [x] 2. Capture, validate and persist the subtask configuration
- [x] 2.1 (P) Validate the tracking directory with German messages
  - Report a blank selection, a missing directory, and a directory without the `task_template` marker as three distinct explanatory German messages.
  - Never require a `.git` folder.
  - Expose the result as a validity flag plus message so callers can both gate the start action and raise an error at phase entry.
  - Observable: each of the three invalid cases returns its own German message, and a directory containing `task_template` validates.
  - _Requirements: 1.3_
  - _Boundary: WorkflowDirectoryValidation_

- [x] 2.2 (P) Persist the tracking directory as a separate recent-directory history
  - Add the last-choice value and a recent-directory collection for tracking directories, kept separate from the existing working-directory history.
  - Share one promotion helper with the existing history: normalize, de-duplicate case-insensitively, promote to front, cap the list.
  - Load settings files written before these fields existed without error, using the existing defaults.
  - Observable: selecting the same directory twice leaves one entry at the front of the tracking history, the working-directory history is unaffected, and an older settings file loads with empty tracking history.
  - _Requirements: 1.4, 1.7_
  - _Boundary: AppSettings, SettingsService_

- [x] 2.3 Capture the configuration as an immutable snapshot
  - Represent the configuration as an immutable record of the enabled flag plus the tracking directory, captured exactly once when phase 4 is entered so the pair can never be observed from two different moments.
  - Raise a dedicated user-fixable error when the configuration is enabled but its directory fails validation, carrying the German message; never downgrade an enabled-but-invalid configuration to a normal run.
  - Extend the orchestrator run request with the captured configuration and a progress sink, both defaulted so existing construction sites keep compiling; a null configuration means disabled.
  - Observable: entering phase 4 with subtask mode enabled and a directory lacking the marker raises the error with the German message instead of starting a normal implementation session.
  - _Depends: 2.1_
  - _Requirements: 1.6, 1.8_
  - _Boundary: SubtaskConfiguration_

- [x] 2.4 Persist subtask settings and manual completion in the task journal
  - Add the enabled flag, the tracking directory and a manual-completion marker to the task journal record and its serialized form, all defaulted so the record version is unchanged.
  - Save the settings when a task starts, when the user edits them for an existing task folder, and when the configuration is captured at phase entry, preserving the existing dismissal flag and the store's non-throwing contract.
  - Load journals written before these fields existed as subtask mode disabled.
  - Observable: a task saved with subtask mode enabled reloads with its directory intact, an older journal loads as disabled, and saving subtask settings does not clear dismissal.
  - _Requirements: 1.5, 1.7, 5.4_
  - _Boundary: TaskState, TaskStateStore_

## 3. Prompt and template contract

- [x] 3. Make the application and agent sessions agree on the file protocol
- [x] 3.1 Layer the substitution builders and add the new tokens
  - Add the absolute tracking-root, task-folder and subtasks-folder tokens, the task-title alias that does not remove the existing task-name token, and the per-subtask title and body tokens.
  - Layer the builders so the execution set extends the decomposition set, which extends the existing base set, rather than duplicating token construction.
  - Leave the existing base tokens and their values untouched so the four existing phase prompts render exactly as before.
  - Observable: rendering the decomposition and execution prompts resolves every one of their placeholders, and the existing phase prompts still render unchanged.
  - _Depends: 1.1_
  - _Requirements: 6.4, 6.6_
  - _Boundary: PromptVariables_

- [x] 3.2 Correct the decomposition prompt
  - Replace the brace names the application does not substitute with agent-chosen angle-bracket names, so rendering no longer fails on the task-path token and no longer destroys the per-subtask title by substituting one value everywhere.
  - Use the absolute substituted paths directly instead of deriving a task path inside the prompt.
  - Move the completion flag from the tracking root to the task folder, and state that this file carries the ordered subtask array in execution order with non-empty content.
  - Require an initial per-subtask status example of `pending`, and require atomic temporary-file-then-rename publication for both the status file and the flag.
  - Require safe subtask titles and explain what makes a title unsafe.
  - Observable: the corrected file renders without an unknown-placeholder error, and its instructions name the task folder as the index location.
  - _Depends: 3.1_
  - _Requirements: 6.2, 6.4, 6.6, 6.7_
  - _Boundary: create_subtasks prompt_

- [x] 3.3 Correct the subtask execution prompt
  - Use the absolute substituted paths so the status and flag locations no longer need composing from a root.
  - Move task-wide findings to a global findings file in the task folder, beside the per-subtask findings files, leaving the subtasks folder holding only subtask folders.
  - State the publication order explicitly: the status payload first, then the non-empty flag, each published by atomic temporary-file-then-rename.
  - Remove the instruction to write ordered titles into the per-subtask flag, whose contents the application does not interpret.
  - Observable: the corrected file renders without an unknown-placeholder error, and names the task folder for global findings.
  - _Depends: 3.1_
  - _Requirements: 6.2, 6.4, 6.6, 6.8_
  - _Boundary: run_subtask prompt_

- [x] 3.4 Validate every shipped prompt against its own token set
  - Build a catalog mapping each shipped prompt file to the tokens that file may use: the existing phase files to the base set, the decomposition file and the execution file to their respective supersets.
  - Validate each file against its own set rather than against the union of all known names, and report any shipped prompt file the catalog does not recognize.
  - Surface failures through the existing start gate.
  - Observable: startup validation covers all eight shipped prompt files, a token valid in the execution prompt but used in a phase prompt is reported, and an unrecognized new prompt file is reported rather than silently skipped.
  - _Depends: 3.1, 3.2, 3.3_
  - _Requirements: 6.3, 6.4_
  - _Boundary: PromptTemplateCatalog, PromptTemplateService_

- [x] 3.5 (P) Publish the required tracking-repository template files
  - Add the missing per-subtask status example with a `pending` status.
  - Replace the empty per-subtask flag example and add the missing task-level index example, both with non-empty content, because the application treats an empty file as no flag at all.
  - Leave the existing verification file in place and add no repository README; both are out of scope.
  - Observable: the template folder contains a non-empty status example and non-empty index and flag examples, and a fresh copy of the template satisfies the application's non-empty-file rule.
  - _Requirements: 6.5_
  - _Boundary: External tracking template_

## 4. Orchestration: sessions, decomposition and the ordered loop

- [ ] 4. Run subtasks sequentially in fresh sessions
- [ ] 4.1 Extract the shared session primitive without changing existing behavior
  - Factor the existing phase-run body into one reusable session operation taking the terminal, manual signal, product working directory, launcher, prompt file, substitutions, completion rule, watch locations and cancellation, and reporting whether the manual signal won.
  - Preserve the current order exactly: create the watcher and baseline, clear the screen, start a fresh terminal, wait for readiness, send the explicit directory change, settle and auto-answer, render, paste and submit, then race the watcher against manual completion.
  - Keep the existing auto-answer behavior rather than substituting any replacement logic, and dispose session and watch resources through the existing lifecycle.
  - Observable: the existing orchestrator tests pass unchanged against the refactored code, with no assertion edits.
  - _Requirements: 2.1, 7.2_
  - _Boundary: WorkflowOrchestrator_

- [ ] 4.2 Branch phase 4 into decomposition
  - Keep the existing implementation path untouched when subtask mode is disabled, including its done-marker completion and stale-marker deletion.
  - When enabled, capture and validate the configuration, persist it, report the decomposing stage, and reuse an existing readable index rather than decomposing again.
  - When decomposing, delete only the stale task-level flag and run one decomposition session against the existing spec and plan with the task-specific tracking destination.
  - Surface an unusable index or an invalid directory as the user-fixable error, leaving implementation recoverable.
  - Observable: a task with a readable index starts no decomposition session, a task without one starts exactly one, and a task whose tracking directory has lost its marker shows the German error while remaining recoverable.
  - _Depends: 2.3, 3.4, 4.1_
  - _Requirements: 1.6, 1.8, 2.2, 2.3, 2.9_
  - _Boundary: WorkflowOrchestrator_

- [ ] 4.3 Execute eligible subtasks in index order, one attempt per run
  - Re-read the ledger each iteration and keep a case-insensitive attempted-title set scoped to the single run.
  - Choose the first entry that is not complete and not yet attempted this run, so an entry left failed or pending by an earlier run is retried exactly once per run, and mark it attempted before starting work.
  - Skip entries with unsafe titles or missing descriptions as failures without accessing anything outside the tracking repository, delete that entry's stale flag, then run one fresh session in the product working directory supplying the full description and resolved paths.
  - Never retry an entry already attempted within the same run, and never impose an execution timeout while a session is alive.
  - Observable: given an index of three entries with the middle one already complete, exactly two sessions run in index order; a run over an index whose only entry already failed starts one session, and a second pass within that same run starts none.
  - _Depends: 4.2_
  - _Requirements: 2.4, 2.5, 2.7, 2.8, 2.10_
  - _Boundary: WorkflowOrchestrator_

- [ ] 4.4 Settle the status payload and publish progress
  - After a session publishes its flag, re-read the status until both the read and the parse succeed, retrying at 200 millisecond intervals up to five times, then treat the entry as failed.
  - Publish refreshed progress after each subtask, carrying stage, counts, current title and the ordered states that deliver failure reasons.
  - Keep this bounded settling distinct from session waiting, which has no timeout.
  - Observable: a status file that is unparseable on first read but valid on the third attempt yields the parsed state rather than a failure, and progress published after each subtask reports the failure reason text from the status payload.
  - _Depends: 4.3_
  - _Requirements: 2.6, 2.11, 3.2, 3.7_
  - _Boundary: WorkflowOrchestrator_

- [ ] 4.5 Decide automatic completion from fresh evidence only
  - Require a freshly read, non-null final snapshot in which the total is greater than zero and every entry is complete before recording implementation as completed; never fall back to an earlier in-memory snapshot.
  - Leave implementation active and recoverable when the final read fails or any entry is not complete, so the task is offered again.
  - Observable: a run whose entries all completed records completion, and a run whose final index read fails leaves the phase active even though the last known snapshot was all-complete.
  - _Depends: 4.4_
  - _Requirements: 3.5, 3.6_
  - _Boundary: WorkflowOrchestrator_

## 5. Recovery from authoritative disk state

- [ ] 5. Restore interrupted subtask work
- [ ] 5.1 Reconcile completion from ledger evidence
  - Pass the task record into artifact reconciliation so it can tell a subtask task from a normal one.
  - For a subtask task, honour a recorded manual completion without consulting the ledger; otherwise keep completion when the ledger shows every entry complete.
  - Never fall back to the normal done-marker for a subtask task, and keep the recorded phase state rather than demoting it when the stored path is blank or malformed or the index cannot be read.
  - Leave normal-mode reconciliation, the recoverable-task record and the recovery scanner unchanged.
  - Observable: a completed subtask task with no done-marker survives a restart without demotion, a subtask task with a blank stored path is not demoted, and a normal task with no done-marker is still demoted as before.
  - _Depends: 1.4, 2.4_
  - _Requirements: 4.3, 4.4, 4.6, 5.4_
  - _Boundary: PhaseReconciliation_

- [ ] 5.2 Restore the tab's configuration and progress
  - Restore the stored mode and tracking directory when resuming a task, suppressing the normal working-folder synchronization that would otherwise overwrite them.
  - Seed the subtask indicator from the ledger so counts and failure reasons reflect disk immediately, deriving progress without any persisted cursor.
  - Continue an interrupted task by skipping completed entries and attempting the rest in index order.
  - Ordering caveat: this task seeds the subtask indicator, so 6.1 must be built first even though it carries a later number; 6.1 is parallel-capable and may be pulled ahead of this task.
  - Observable: relaunching after an interrupted run shows the restored directory and the disk-derived counts before any session starts, and continuing runs only the entries that are not complete.
  - _Depends: 5.1, 6.1_
  - _Requirements: 4.1, 4.2, 4.5_
  - _Boundary: TaskTabViewModel_

## 6. Presentation: configuration, progress and task completion

- [ ] 6. Surface configuration and progress in the task tab
- [ ] 6.1 (P) Present subtask progress and failure reasons
  - Show a second indicator labelled for subtasks whenever subtask mode is selected.
  - Show the decomposition message while decomposing and the completed-of-total count while running, updating after each subtask, and add the failed count in the error colour when failures exist.
  - Colour the indicator grey before phase 4, yellow while running, and green only when the total is greater than zero and every entry is complete.
  - Offer each failed entry's title and reason in the tooltip, from the states carried in the progress payload.
  - Reuse the existing status-to-brush and visibility converters; add none.
  - Observable: a decomposed but unstarted task shows zero of N with no failures and a non-green indicator, and a run with one failure shows the failed count and its reason text in the tooltip.
  - _Depends: 1.2_
  - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.7_
  - _Boundary: SubtaskIndicatorViewModel, SubtaskIndicatorView_

- [ ] 6.2 (P) Offer the directory picker with a caller-supplied title
  - Add an optional dialog-title argument to the directory picker, keeping the existing working-directory caption as the default so current callers are unchanged.
  - Update the interface implementers and test doubles even though the argument is optional.
  - Observable: the existing caller still shows the working-directory caption, and the subtask row can request its own caption.
  - _Requirements: 1.2_
  - _Boundary: DirectoryPickerService_

- [ ] 6.3 Bind the subtask configuration controls
  - Add the subtask checkbox beside the implementation phase with the specified German tooltip, and show the tracking-directory row with its recent-directory selector and folder picker only while subtask mode is selected.
  - Show the validation message and prevent starting a workflow while subtask mode is enabled and the directory is invalid.
  - Preserve the existing never-clear collection reconciliation so a two-way selection is not reset to null.
  - Place the subtask indicator in the tab beside the existing phase indicator and bind its visibility to subtask mode, so this view owns the indicator's placement while 6.1 owns its content.
  - Lock every subtask input while phase 4 is active, including the folder-picker button, and allow changes during phases 1 to 3.
  - Observable: clearing the marker from the selected directory shows the German message and disables the start action, the subtask indicator appears only while subtask mode is selected, and every subtask control is disabled once phase 4 begins.
  - _Depends: 2.1, 2.2, 6.1, 6.2_
  - _Requirements: 1.1, 1.2, 1.3, 1.6, 3.1_
  - _Boundary: TaskTabViewModel, TaskTabView_

- [ ] 6.4 Wire live progress from the run into the indicator
  - Have the tab create the progress sink it passes into the run request and apply each published payload to the indicator that 6.3 placed.
  - Marshal updates onto the UI thread through the existing dispatch mechanism so counts and reasons update while the run continues.
  - Reset the indicator when a run starts and when subtask mode is switched off.
  - Observable: during a run the displayed count advances after each subtask without user interaction, and a failure's reason becomes visible in the tooltip while the run is still going.
  - _Depends: 4.4, 6.1, 6.3_
  - _Requirements: 3.2, 3.7_
  - _Boundary: TaskTabViewModel_

- [ ] 6.5 Remove the phase completion action
  - Remove the phase-completion command, its can-execute logic, its generated notification, the phase button and the phase indicator's active flag, keeping the active-phase tracking used for task completion and editing state.
  - Let phases 1 to 3 advance only on their artifact conditions, leaving the terminal interactive when no artifact arrives.
  - Observable: no phase indicator exposes a completion button, and a phase with no arriving artifact leaves its terminal interactive instead of offering manual advancement.
  - _Requirements: 5.1, 5.2_
  - _Boundary: PhaseIndicatorViewModel, PhaseIndicatorView_

- [ ] 6.6 Complete and close the task explicitly
  - Make task completion signal the run, await its end and only then request the tab close, keeping the existing dismissal behavior on close.
  - Record the completion as an explicit manual override in the journal so recovery can distinguish it from automatic all-complete completion.
  - Keep the action enabled only while implementation is running.
  - Observable: invoking task completion during a subtask run closes the tab only after the running session has ended, and the journal afterwards shows the manual-override marker.
  - _Depends: 2.4, 4.5, 6.5_
  - _Requirements: 5.3, 5.4_
  - _Boundary: TaskTabViewModel_

## 7. Validation and regression gates

- [ ] 7. Prove the extension preserves the existing workflow
- [ ] 7.1 (P) Cover the path, index and ledger contracts
  - Write these tests from the resolved contracts rather than reusing the source plan's examples, whose fixtures concatenate unescaped JSON for backslash titles and whose expectations predate the resolved decisions; build path fixtures so backslashes survive as valid JSON.
  - Cover every row of the state derivation table, including unrecognized status values landing as pending and a flag without a status landing as pending.
  - Cover index parsing: de-duplication keeping the first occurrence, blank and unsafe entries failing individually without invalidating the index, the exact dot-names being rejected while a title merely containing two dots is accepted, and a malformed index being reported unusable.
  - Cover reuse reporting as available when descriptions are missing, and completion surviving a deleted description.
  - Observable: the listed cases pass against the implemented behavior and the derivation table has a test per row.
  - _Depends: 1.4_
  - _Requirements: 7.2_
  - _Boundary: Workflow.Tests, SubtaskPaths, SubtaskLedger_

- [ ] 7.2 (P) Cover configuration, persistence and reconciliation
  - Cover the three German validation messages, history promotion, case-insensitive de-duplication and capping, and that the working-directory history is unaffected.
  - Cover journal and settings records written before the new fields load with safe defaults, dismissal is preserved, and an enabled-but-invalid configuration raises the error rather than falling back to a normal run.
  - Cover reconciliation: no done-marker demotion for a completed subtask task, no demotion on a blank or unreadable stored path, manual completion surviving a restart, and normal-mode demotion still occurring.
  - Observable: the listed cases pass against the implemented behavior.
  - _Depends: 2.4, 5.1_
  - _Requirements: 7.2_
  - _Boundary: Workflow.Tests, WorkflowDirectoryValidation, SettingsService, TaskStateStore, PhaseReconciliation_

- [ ] 7.3 Cover orchestration and presentation
  - Cover one fresh session per eligible entry in declared order, the rendered description reaching the session, stale flag deletion, one attempt per run, and a second run retrying an entry the previous run failed.
  - Cover settling across the retry interval with a status that only parses on a later attempt, and completion requiring a fresh non-null final snapshot so a lost final index leaves the phase active.
  - Cover progress reaching the indicator, including failure reasons in the tooltip and restored counts after resume, awaiting asynchronous progress rather than inspecting it synchronously.
  - Cover the views and resources still loading with the phase command removed, and the tab closing only after the run has ended.
  - Observable: the listed cases pass against the implemented behavior, and the pre-existing orchestrator assertions are unchanged apart from those naming the removed phase-completion command.
  - _Depends: 4.5, 5.2, 6.6_
  - _Requirements: 7.2_
  - _Boundary: Workflow.Tests, WorkflowOrchestrator, TaskTabViewModel, SubtaskIndicatorViewModel_

- [ ] 7.4 Extend the acceptance script with subtask checks
  - Add checks for the corrected prompts, the per-file token validation and the tracking contract to the existing verification script, preserving its current checks.
  - Observable: the script exits successfully on the implemented branch and fails when a prompt file reintroduces an unsupported placeholder.
  - _Depends: 3.4, 7.3_
  - _Requirements: 7.3_
  - _Boundary: verify.ps1_

- [ ] 7.5 Run the full automated gate
  - Build and test the solution, then run the verification script, keeping pre-existing assertions unchanged except those referencing the removed phase-completion command.
  - Observable: the build reports no warnings or errors, all tests pass, and the verification script exits zero.
  - _Depends: 7.4_
  - _Requirements: 7.1, 7.2, 7.3_

## Deferred

- Requirement 7.3 also calls for the source manual walkthrough. Its steps contradicted one another before the design validation and are only now consistent; the walkthrough is user-executed rather than a coding task, so it is tracked outside this plan and should be run once task 7.5 passes.

## Implementation Notes

- 1.1: `SubtaskPaths.SubtaskPathToken` is the **absolute** subtasks directory, per the resolved decision for issue 11 in design.md and requirement 6.6. `docs/superpowers/plans/2026-09-17-subtask-execution-plan.md` specifies it relative and is **superseded** — do not treat it as the contract. Tasks 3.2 and 3.3 must therefore not prefix `{subtask_path}` with `{workflow_path}`.
- 1.1: `IsValidTitle` accepts `...` and `trailing..` because requirement 2.10 rejects only the exact dot-names `.` and `..`. Windows path resolution collapses both (`...` resolves to the subtasks directory itself, `trailing..` to `trailing`), so two distinct index titles can resolve to one on-disk location. This stays inside the tracking repository, so it is spec-compliant — but task 1.3 (de-duplication, 2.12) and task 4.3 (stale flag deletion) should account for it.
- Baseline for the whole run: `ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput` and `ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput` fail in a headless session because the pseudo-console yields no bytes. They are pre-existing and environmental — never count them as regressions.
- 1.2: test fixtures in this spec must be asymmetric in every dimension the component evaluates. A 2/2/1 fixture let a mutant swapping the `Completed` and `Failed` counters pass all 13 tests; the reviewer proved it by mutation rather than inspection. Where a component derives N counters, give the fixture N pairwise distinct numbers, and pin any ignored state structurally (`Total == Completed + Failed + pending`). Verify a fix by re-running the mutation and showing the suite now fails.
- 1.2: `SubtaskSnapshot` and `SubtaskProgress` are records whose generated equality compares `States` by reference, so two snapshots read from identical files never compare equal. Do not diff snapshots or progress payloads to suppress redundant UI updates — compare counts or elements. Documented in the type's `<remarks>`.
- 1.3: **RULING — the ledger de-duplicates by title string, never by resolved path.** `ST-003-x..` and `ST-003-x` are different titles that Windows resolves to the same folder, but 2.12 and design `### Paths and Ledger` both define de-duplication over titles ("the same title", ordinal-ignore-case, keep first). Collapsing them would drop an entry the index listed and make `Total` disagree with the index; marking one `Failed` would invent a failure category 2.10 does not list (it names blank, unsafe and missing-description only). Containment holds: `..` is rejected by `IsValidTitle`, and `...`/`....` collapse *inward* to the subtasks directory. **Task 1.4 inherits this contract unchanged and must not dedup by resolved path either.** The real exposure is task 4.3's stale-flag deletion, where deleting entry A's flag can clear entry B's — decide it there, and if it proves unacceptable the fix is a spec amendment to 2.10/2.12, not a quiet change in the ledger.
- 1.3: `TryReadIndex` is a fourth public member beyond the three `### Paths and Ledger` names, needed because there is no `InternalsVisibleTo` and 1.3's observable would otherwise be unobservable. **Directive for 1.4**: either make it a private helper behind `TryRead`/`IsDecomposed`/`AllComplete`, or consciously retain it with its `<summary>` stating it is a deliberate addition. It must not drift into the final public surface unremarked.
- 1.3: a JSON `null`, number or object element in `subtasks` maps to the empty title, so `["", null, 42]` collapses under de-duplication into ONE `Failed` entry with `Total == 1`. No subtask is lost and 2.12 is literally satisfied, but requirement 3.3's `{K} fehlgeschlagen` under-reports a multi-defect index. Relevant if 4.4 or 6.1 ever surface the failure count as a diagnostic.
- 3.2: the corrected `create_subtasks.md` uses `{spec_path}`, `{plan_path}`, `{workflow_path}`, `{tasktitel}`, `{task_path}` and `{subtask_path}` only; the per-subtask name is `<subtask_title>`, which the token regex cannot see. Forward slashes throughout, matching requirement 6.7's literal `{task_path}/result.json` and the file's prior style. `Workflow.Tests/ShippedPromptContractTests.cs` pins the protocol against the **shipped** file in the test output directory, separator-insensitively; task 3.3 extends that class for `run_subtask.md`.
- 3.2: **`Workflow/verify.ps1` V4 is the same defect requirement 6.3 describes, in the shell gate.** It validates every `Prompt\*.md` against the flat base token list, so it already failed before this task for `create_subtasks.md`, `run_subtask.md` **and** for `counter_prompt.md` and `evidence_gate.md`, both of which use `{tasktitel}`. That is outside 3.2's boundary; **task 3.4 (or 18) must give `verify.ps1` the same per-file catalog as `PromptTemplateService`**, and must note that the two code-unreferenced prompts need `tasktitel`, which is a decomposition-set token, not a base one.
- 1.4: settling retries are **the caller's**, not the ledger's. `### Paths and Ledger`, `## Error Handling` E7 and resolved decision 10 all assign the 200 ms x5 retry to the application, and requirement 2.11 says "the application shall re-read". The ledger reports a locked or half-written `status.json` as Failed immediately. **Task 4.4 owns the retry loop** — do not add one to `SubtaskLedger`.
- 1.4: `TryReadIndex` stays public as a deliberate fourth member, justified in its `<summary>`. Folding it away would have forced rewriting committed 1.3 assertions, which requirement 7.2 forbids: those tests assert `Pending` for entries with no folder staged, which `TryRead` derives as `Failed`. Revisit only once orchestration lands and only if no production caller needs the raw ordered titles.
- Regression gate, general: do not compare "baseline + new tests == new total" — VSTest counts executed Theory rows, not test methods, so the arithmetic misleads. Compare `total - focused filter of the touched component` instead; it must be identical before and after. Non-ledger cases have stayed at exactly 331 through tasks 1.3 and 1.4.
- 2.1: the preserved source spec `docs/superpowers/specs/2026-09-17-subtask-execution-design.md` §8.4 carries a **literal German message table**. `requirements.md` demotes that document to provenance, so it does not bind — but consult it before inventing user-facing text, and record any deliberate divergence. The noun for the selected folder is `Workflow-Verzeichnis` (requirement 1.2's label); the thing it must *be* is the `Workflows-Repository`. A coined `Workflows-Verzeichnis` slipped into the marker message and was corrected before commit.
- Reviewer directive, general: mechanical gates cannot see whether a user-facing string is *right*. For any task producing UI text, check whether design.md fixes the wording, compare terminology against the existing views and requirements, and judge whether the message tells the user what to do. A test asserting the exact string cements a wrong term rather than catching it.
- 2.2: `SettingsService.Load` turns a `JsonException` or `IOException` into a fresh `AppSettings`, so a **malformed test fixture is indistinguishable from a successful load of an empty file**. A back-compat test that only asserts "the new list is empty" would pass without ever entering the load path. Assert a pre-existing field's *value* as well — the fallback cannot produce one. This applies to every later task that fixtures a settings or journal file.
- 2.2: `AppSettings.RecentWorkflowDirectories` is set to `null` by an explicit `"RecentWorkflowDirectories": null` in the JSON, because System.Text.Json assigns literal null over the property initializer. Pre-existing behaviour, identical for `RecentDirectories`, so not a regression — but a consumer that dereferences it without a guard would NRE. Relevant to task 6.3 when it binds the collection.
- 2.3: **RULING for task 4.2 — call `SubtaskConfiguration.CaptureValidated(source)`, not `Capture()` followed by a separate validation.** design.md names only `Capture()`, but `### Session and Loop` specifies the branch as "captures its configuration once, validates it, saves it", and `CaptureValidated` is exactly that sequence behind one entry point. The mechanical argument decides it: validating a *second read* instead of the captured value is caught by exactly one test, and that test only exists because capture and validate live behind one function. Splitting them at 4.2's call site makes the re-read defect reachable again where no test from 2.3 guards it.
- 2.3: once-only capture is compile-enforced **downstream** of the capture point — `WorkflowRunRequest.Subtasks` is typed `SubtaskConfiguration?`, so the orchestrator is never handed a live `ISubtaskConfiguration` it could re-read (a mutation swapping the type fails with CS1503, not with a failing test). **Above** the capture point it is convention: nothing stops a caller invoking `Capture` twice. design.md's `Revalidation Triggers` lists "configuration capture", so 4.2's call site must be re-checked against requirement 1.6 when it lands — no test in 2.3 can cover it.
- 2.3: `EnsureUsable`'s `?? UnknownReasonMessage` fallback is **compiler-mandated**, not optional defensive coding: removing it fails the build with CS8604, because `WorkflowDirectoryValidation.ErrorMessage` is `string?` while the exception parameter is non-nullable. Do not "clean it up".
- 2.4: **RULING for task 6.6 — call `ITaskStateStore.SetImplementationCompletedManually(paths, true)`.** The design fixes the journal *field* `ImplementationCompletedManually` but names no store method, so this one is an invention. It belongs here rather than in 6.6: 6.6 is boundary-locked to `TaskTabViewModel`, a view model cannot write the journal, and 2.4 is the only task positioned to own a store method. Its semantics mirror `SetDismissed` — load, return silently when there is no journal, mutate one field, save — so it corrects an existing journal and never invents one.
- 2.4: the tracking directory is stored **verbatim**, never trimmed or blank-nulled. Design issue 9 requires reconciliation (task 5.1) to *recognise* a blank, malformed or unreadable stored path and surface it; normalising in the store would erase that distinction.
- Store-wide, pre-existing, not a defect of any task in this plan: `SaveDescription`, `RecordPhase`, `SaveSubtaskSettings` and `SetImplementationCompletedManually` all use `TryLoad(paths) ?? CreateEmpty()`. `TryLoad` returns null both for an absent journal *and* for one that exists but is corrupt or carries a future version, so a write against a corrupt journal overwrites it with an empty record and discards the prior `Dismissed` and phase list. A dismissal inside an unparseable file is unrecoverable anyway, and fixing this would be out-of-boundary drift for a feature task — but if journal robustness is ever taken up, this is the place.
- 3.1: **BINDING TOKEN RULING for tasks 3.2 and 3.3.** design.md contradicts itself; the reviewer settled it from the requirements, which outrank a summary row.
  - `{task_path}` is a **real, absolute, app-substituted brace token in BOTH prompts.** Requirement 6.7 itself spells it in brace form ("the task-local `{task_path}/result.json`"), 6.6 mandates it by name, and design lines 178/191/194 agree. Resolved-Decisions row 11's "`{task_path}`/`{subtask_title}` stop being brace tokens" describes only the *defective current occurrences* in `create_subtasks.md` — it is contradicted by 6.7, by its own cross-referenced section, and by its own sentence ending "and `task_path` becomes a real absolute token".
  - `subtask_title` is **context-dependent, and the two prompts differ.** Task **3.2** must write `<subtask_title>` in **angle** form and must NOT introduce `{subtask_title}` into `create_subtasks.md`: at decomposition the titles do not exist yet, the agent invents them, and design line 180 gives the rationale verbatim ("written `<subtask_title>` in `<>` form because the application does not substitute it"). Task **3.3** must write `{subtask_title}` in **brace** form in `run_subtask.md`: the app is running one known subtask whose title it holds, and design line 178 states "execution adds `subtask_title` and `subtask`". Requirement 6.4 is the governing rule and cuts exactly along this line. The token set already encodes it — `subtask_title` is in `SubtaskRunNames`, not `SubtaskCreationNames`.
  - **Second design defect found by the reviewer, carry it into 3.3:** design line 195 writes the run-prompt status path as `{subtask_path}\<subtask_title>\status.json`, angle form *in the execution prompt*, contradicting line 178. Ruling: **brace form** there. Line 195 is a carry-over of the create-prompt convention. Leaving it angle would force the agent to re-derive a value the application already supplies, which is the mischief 6.6's rationale clause forbids.
- 3.1: requirement 6.6's "every substituted path token shall be absolute" is **over-broad as literally worded**. Its own trailing citation scopes it — design issue 11 enumerates exactly three tokens (`workflow_path`, `task_path`, `subtask_path`). The base `spec_path`/`plan_path`/`review_path`/`done_path` stay working-directory-**relative**: making them absolute would break 3.1's own "render exactly as before" criterion and the pre-existing `PromptTemplateServiceTests.For_MapsDonePathToTheRelativeMarkerPath`. Spec-hygiene note only, no change required.
- 3.1: `KnownNames` now means the **base** set, not "every token the app knows". No union property was added, deliberately — design requires that the union must not replace per-file checks. **Task 3.4** maps the four phase files to `KnownNames`, `create_subtasks.md` to `SubtaskCreationNames`, `run_subtask.md` to `SubtaskRunNames`. `ValidateAll` still covers only `PhaseCatalog.All`, so requirements 6.3 and 6.4 are **not** yet satisfied — that is wholly 3.4's.
- 3.3: `run_subtask.md` stays **English** while `create_subtasks.md` is German; the task corrected the protocol, not the language. Its canonical-task pointer now targets `{plan_path}#task-N`, **not** the tracking repo: the block's own lead-in names the approved implementation plan (rank 2 of the prompt's source-of-truth hierarchy), the subtask description is already inlined via `{subtask}`, and the old `{workflow_path}\{subtask_path}\{subtask_title}\...md` named a file that does not exist in a subtask folder. A working-directory-**relative** base token is correct there - `WorkflowOrchestrator.cs:133` sends `cd "{WorkingDirectory}"` before the prompt, and Implementation Note 3.1 scopes 6.6's absolute clause to `workflow_path`/`task_path`/`subtask_path` only.
- 3.3: **an ordering assertion over prompt text must be proved by the inverting mutation, not by inspection.** The first attempt compared `IndexOf("status.json.tmp")` against `IndexOf("result.json.tmp")`, but both names first occur on one single line of the atomic-publication list, so it pinned that list's incidental word order. Inverting requirement 6.2's normative order statement - and deleting it outright - left all 537 tests green. The fix pins the **bare** names, whose first occurrence is the normative sentence itself. Before trusting any positional assertion, `grep -n` where the compared strings actually first appear, then run the inversion and require it to fail.
- 3.3: **knowingly accepted residual.** Deleting the order paragraph (rather than inverting it) still passes, because the first bare names then fall to the section headings `### 1. The status payload` / `### 2. The flag`, which remain in order; likewise swapping those two sections while the sentence stands. Both degrade emphasis without inverting the contract, so no test chases them. Non-blocking nit left in place: `RunSubtaskPrompt_StatesThePublicationOrderBeforeItDescribesEitherFile` claims more than its body asserts - `..._NamesTheStatusPayloadBeforeTheFlag` would match it.
- Reviewer technique, general: when an **untracked** file has been overwritten, `git diff` shows nothing - but a stale build output often preserves the prior version. The pre-edit `run_subtask.md` survived in `Workflow\bin\Release\net8.0-windows\Prompt\` because only Debug was ever rebuilt, which let the reviewer re-derive the RED phase independently instead of trusting the report. Mutating a prompt for proof requires editing the **source** and rebuilding (the `Prompt\**\*.md` Content glob), then restoring byte-identically and proving it by hash, since `git diff` cannot.
- Regression gate update: after 3.3 the suite is **538** total with `ShippedPromptContractTests` at **25**; the untouched remainder is still exactly **513**. That is the figure 3.4 must reproduce, with its own focused filter substituted.
- 3.4: `counter_prompt.md` and `evidence_gate.md` are mapped to `SubtaskCreationNames`, the **narrowest existing layer** that admits `{tasktitel}` - the only token either file contains. A widened base set would legalise `{tasktitel}` in the four phase prompts and destroy the 6.4 guard; `SubtaskRunNames` would additionally legalise `{subtask}`/`{subtask_title}` in files with no subtask context. design.md names no set for these two, so this is a signed-off invention. A dedicated single-token set would be marginally stricter, but neither file has a `Render` call site anywhere, so no substitution dictionary defines what they support, and a fourth layer for two unreferenced files is worse coupling. Possible future tightening, not a defect.
- 3.4: **RULING - `ValidateAll` reports a file on disk with no catalog entry, and deliberately NOT the mirror case** (a catalog entry with no file on disk), which stays `ReadTemplate`'s not-found error at point of use. design.md says "any *file* it does not recognize", which is this direction. Reporting the mirror would break the committed `PromptTemplateServiceTests.ValidateAll_ReportsEmptyAndUnknownTokenTemplates`, whose fixture writes only four files - and rewriting a committed test to fit a new rule is what requirement 7.2 forbids. Residual (a shipped file vanishing is not reported by `ValidateAll`) is covered by `Render`/`ReadTemplate` throwing at point of use, by two catalog tests asserting against the real output directory, and by `verify.ps1`'s per-entry "catalogued prompt ships" assertions.
- 3.4: the four phase entries are **derived from `PhaseCatalog.All`**, not hardcoded, so adding a fifth phase cannot ship an uncatalogued prompt. `ShippedTemplateFiles()` enumerates with `SearchOption.AllDirectories` and keys on `Path.GetRelativePath`, mirroring the `Prompt\**\*.md` Content glob, so a `.md` dropped in a subdirectory yields a key no entry matches and is reported rather than missed. `verify.ps1` mirrors this (`-Recurse -File` plus a relative key) so the two gates agree on subdirectory behaviour.
- 3.4: **the prompt catalog now exists twice** - `PromptTemplateCatalog.Build()` in C# and `$promptCatalog` in `verify.ps1` - two hand-maintained lists in two languages with nothing mechanically tying them together. `verify.ps1` already duplicated the base token list before this task, so this extends a pre-existing pattern. The drift is asymmetric in the safe direction: the C# side auto-extends from `PhaseCatalog.All` while the PowerShell catalog fails loudly on the new file, so a gate breaks visibly instead of silently under-checking. Eliminating it (a generated JSON manifest, or a `--list-prompt-catalog` switch) was out of boundary.
- Standing project note, NOT a defect of 3.4: `verify.ps1` exits 1 in a headless session because V2 shells out to `dotnet test`, which is non-zero from the two environmental `ConPtySessionTests`. V1, V3 and V4 pass. The corrected `run_subtask.md` tells agents the gate must exit 0, which is therefore unreachable headless. If this is ever addressed, **V2 should exclude `ConPtySessionTests`** - the prompts should not change.
- 3.4: `PromptTemplateService.ShippedTemplateFiles()` guards only `Directory.Exists`; an `UnauthorizedAccessException` or `IOException` from `Directory.EnumerateFiles` itself would escape `ValidateAll` as an unhandled startup exception instead of a German start-gate message, unlike every other failure in that method. Exposure is remote (the directory is inside the app's own install). Recorded for whoever next touches it.
- Regression gate update: after 3.4 the suite is **571** total; the focused filter `PromptTemplateServiceTests|PromptVariablesTests|PromptTemplateCatalog` is **74**; the untouched remainder is still exactly **497**.
- 3.5: the template files carry **concrete example values, never prompt tokens**. Nothing substitutes into a tracking-repository data file, so `{tasktitel}` would ship as a literal folder name - and a literal `<subtask_title>` is worse: `<` and `>` are in `Path.GetInvalidFileNameChars()`, so `SubtaskPaths.IsValidTitle` rejects it and `SubtaskLedger.Classify` would emit `Unsicherer Subtask-Name`. Braces are *not* invalid characters, so `{tasktitel}` would pass validation and silently become a real folder - the more insidious of the two. Tokens belong in prompts, concrete names in templates.
- 3.5: the index lists `ST-001-subtask-template`, the one folder that exists, and this is **forced, not stylistic**: `TryRead` composes every path from the listed title and there is no directory listing or alphabetical fallback (requirement 6.1), so any other name gives a fresh copy an entry whose folder is `Absent` and which fails for a fabricated reason.
- 3.5: the per-subtask flag example uses the **plan-of-record's neutral payload** (`note` + `summary`), not a `status` claim. The first attempt copied `run_subtask.md`'s completion example, which shipped a flag saying `complete` beside a `status.json` saying `pending` - mechanically harmless (nothing reads the flag's contents; `SubtaskResultFile` has no production consumer outside its own definition) but didactically wrong, and a template is read by people. Non-emptiness is the whole contract; the payload should not assert a state.
- 3.5: the Observable's "a fresh copy satisfies the non-empty-file rule" is met **only for the three files 6.5 names**, and that is the correct reading. A fresh copy still derives `Failed` for its single entry (`Keine Beschreibung für 'ST-001-subtask-template'.`) because `subtask.md` stays 0 bytes - a **description**-path outcome under requirement 2.10, which 6.5 does not cover. Filling `subtask.md` would have been scope creep.
- **CARRY-FORWARD INTO 4.2 - load-bearing ordering constraint.** Requirement 6.5 now puts a non-empty task-level `result.json` in the template, so a task folder produced by copying `task_template` - which is the template's entire purpose - reports `IsDecomposed == true` with one entry that immediately derives `Failed`. **4.2 must delete the stale task-level flag BEFORE it evaluates the reuse-an-existing-index check, not after**, or a hand-copied template skips decomposition altogether and reports 0/1 failed. This is not an application hazard today (the app never copies the template: no `File.Copy` anywhere in `Workflow/`, `SubtaskPaths.TemplateDirectory` has no production consumer, and `WorkflowDirectoryValidation` only probes the folder's existence as a marker) - the exposure is entirely through human or agent copies. `DeleteStaleDoneMarker` covers only `DoneAbsolute` and does not help.
- 3.5 verification technique: the ledger's derivation was confirmed by compiling the **real** `SubtaskLedger.cs`/`SubtaskPaths.cs`/`SubtaskStatus.cs` in a throwaway scratchpad console project against a temp copy of the template, then deleting it. No test in this repo may hardcode the external tracking path, and no scratch artefact may be left in either repo.
