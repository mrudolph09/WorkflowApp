Implement the approved feature using Superpowers together with planning-with-files.

{subtask}

The plans have already been reviewed and approved. Treat them as the canonical source of truth.
you may not create another branch in git or svn
## Directories

The application substitutes these values. All three paths are **absolute** and complete — do not put
anything in front of them and do not derive one from another:

* `{workflow_path}` — the checked-out Workflows folder. You do not write here directly.
* `{task_path}` — the folder of this task. The task-wide files belong here.
* `{subtask_path}` — the folder that contains nothing but the subtask folders.

`{subtask_title}` is the title of the subtask you are running; the application substitutes it too.
A name in braces is a value the application replaces, a name in angle brackets is one you choose
yourself. Your own subtask folder is therefore `{subtask_path}/{subtask_title}` and you never
compose it from anything else.

## Responsibilities

Use Superpowers for the implementation methodology:

* follow the approved implementation plan
* use subagent-driven development where appropriate
* use test-driven development
* use systematic debugging when failures occur
* perform verification before declaring tasks complete
* perform specification-compliance and code-quality review as required by the Superpowers workflow

Use planning-with-files only as the persistent execution-state layer.

Initialize/use:

* `{subtask_path}/{subtask_title}/task_plan.md` — execution phases and completion state
* `{subtask_path}/{subtask_title}/findings.md` — discoveries made while implementing this subtask
* `{subtask_path}/{subtask_title}/progress.md` — chronological work, changed files, tests, validation results and errors

Findings that concern the whole task rather than this one subtask go into the global
`{task_path}/findings.md`, beside the per-subtask `findings.md` files. `{subtask_path}` holds
subtask folders and nothing else, so never create a file directly in it.

## Source-of-truth hierarchy

When information conflicts, use this priority:

1. Approved Superpowers specification
2. Approved Superpowers implementation plan
3. Current repository state
4. `task_plan.md`
5. `findings.md`
6. `progress.md`
7. Conversational context

`task_plan.md` must track the canonical implementation plan rather than replace it.

Do not independently redesign the feature inside `task_plan.md`.

Create execution phases that reference the corresponding tasks or sections of the canonical Superpowers implementation plan.

For example:

Phase 1
Canonical task: {plan_path}#task-1

Phase 2
Canonical task: {plan_path}#task-2

## Plan deviations

If implementation reveals that the approved plan cannot be followed:

1. Do not silently change architecture.
2. Record the discovery in `findings.md`.
3. Record the blocked/deviation state in `task_plan.md`.
4. Determine whether the deviation is:

   * implementation detail
   * minor plan correction
   * architectural/specification change

Implementation-detail deviations may proceed if they preserve the specification and architecture.

Architectural or specification deviations must be explicitly surfaced before changing the canonical plan.

## Execution

Work through the approved plan task-by-task.

For each task:

1. Re-read the relevant canonical plan section.
2. Update PWF execution state.
3. Implement using the relevant Superpowers skills.
4. Run the specified verification.
5. Record validation results in `progress.md`.
6. Record important discoveries in `findings.md`.
7. Mark the execution phase complete only when the acceptance criteria and verification requirements are actually satisfied.

Do not declare the overall feature complete merely because the code has been written.

Completion requires:

* all canonical plan tasks completed
* every acceptance criterion verified, or - when it needs a human or interactive check - listed as an open item
* every required test that ran passed; a required test that cannot run for an allowed reason (see below) is an open item
* every step of the acceptance gate the plan names (in this repository `Workflow\verify.ps1` and its V steps) is `passed` or `not_run` with an allowed reason; no step is `failed`
* type checking passing
* lint/static analysis passing where applicable
* no unresolved implementation blockers - an open item is not a blocker, a `failed` gate step is
* final implementation reviewed against the canonical specification and plan
* PWF execution state accurately reflecting completion

An open item is never presented as satisfied.

## Gate results and open items

Every step of the acceptance gate gets exactly one result:

* `passed` - with the command and its exit code.
* `failed` - with the command, its exit code and the names of the failing tests.
* `not_run` - with one of the allowed reasons below.

Allowed reasons for `not_run` (this list is exhaustive):

1. the gate itself lists the step as manual or as a human checkpoint
2. the step needs the interactive desktop or UI interaction
3. required hardware, an external system or an approval is not available
4. the named gate script does not exist in the target repository

No other reason is allowed. "Takes too long", "flaky" or "probably fine" is not a reason: run the
step, and if it fails it is `failed`.

* When the gate exits 0 but reports skipped checks (for example `-SkipConPtyTests` or a missing
  `-WorkflowDirectory`), every skipped check is `not_run` and an open item.
* Record every gate run in `{subtask_path}/{subtask_title}/progress.md` with command, exit code and
  result. A step that was `failed` once in this subtask stays `failed` until a later run of the
  same step is `passed`; it is never relabelled `not_run`.
* A `failed` step blocks completion: fix the cause and run the step again. Without a fix the
  subtask finishes `failed`.
* Reason 2 applies only when a desktop job (see "Desktop jobs") cannot do the step either, for
  example because a human has to watch the result. Work a desktop job can do is requested as one,
  never recorded as an open item.
* A `not_run` step does not block completion. It becomes an open item in the `openGateItems` array
  of `status.json`: `"<step id>: <what a human has to do or check, and what they should observe>"`.
  A subtask with open items is still `complete`.

## Unattended run

There is no human in the loop. The application runs every subtask unattended, possibly overnight.

* Never ask. `AskUserQuestion` is disabled; text questions in the terminal are never answered.
* Gate decisions, approvals and every other question meant for a human are answered in advance in
  `{task_path}/decisions.md`. For these questions that file outranks the specification, the plan and
  `subtask.md`. Cite the entry ID (`D-NNN`) wherever you apply a decision.
* No matching entry: finish with `status = "failed"` and
  `failreason = "Entscheidung fehlt in decisions.md: <question>"`. Do not guess.
* Do not check predecessor subtasks yourself beyond what `subtask.md` prescribes: the application starts this
  session only after every dependency of this subtask is `complete`.

## Desktop jobs

You never start a desktop application, a UI test or anything else that needs the interactive desktop. When the
subtask needs that, request a **desktop job**; the application runs it when the user is idle and nothing else runs.

1. Write one PowerShell 7 script per step into `{subtask_path}/{subtask_title}/`, named `desktopjob.<id>.ps1`
   (`<id>` matches `^[a-z0-9_-]{1,32}$`). A step succeeds only with exit code 0 **and** an existing marker file,
   so write the marker as the step's last action.
2. Then write `desktopjob.json` (atomically, last):

   ```json
   {
     "version": 2,
     "requestId": "<new GUID>",
     "idleMinutes": 15,
     "steps": [
       { "id": "<id>", "script": "desktopjob.<id>.ps1", "timeoutMinutes": 45, "marker": "<absolute path>" }
     ]
   }
   ```

   Steps run strictly in order; an aborted step is repeated later from that step on. `timeoutMinutes` (1-240) must
   cover the step's worst case with margin; split long work into several steps.
3. Write `status.json` with `"status": "deferred"` and `"failreason": "Desktop-Job wartet auf Leerlauffenster"`,
   then the flag `result.json`, and end the session.

When the job has run, the application starts a fresh session for this subtask. It finds
`desktopjob.result.json` (per step: `outcome`, `exitCode`, `markerFound`, attempts) and `desktopjob.<id>.log`.

* Every step `completed` and the delivery checks pass: `complete`.
* Otherwise: rename `desktopjob.result.json` to `desktopjob.result.<requestId>.consumed.json`, write a **new**
  request with a **new** `requestId` containing only the steps to repeat, and finish `deferred` again. A result
  whose `requestId` differs from the current request is never valid evidence. After a `timeout`, give that step a
  larger `timeoutMinutes` or a changed script (for example split into several steps): the application does not
  start the same script with the same `timeoutMinutes` again in this run.

## Status values

`status.json` accepts `complete` (success), `failed` (failure, reason in `failreason`), `deferred` (desktop job
requested), `blocked` (a prerequisite outside this subtask is missing), `pending` and `in_progress`. Do not use
`waiting_for_human` or `waiting_for_user`: no human will answer.

## Signalling completion

Two files in `{subtask_path}/{subtask_title}/` report this subtask, and they are published in this
order: **first** the status payload `status.json`, **then** the flag `result.json`. The application
reacts to the flag alone and reads the payload only afterwards, so a flag written first exposes a
half-finished or missing payload.

### 1. The status payload

Write the outcome into `{subtask_path}/{subtask_title}/status.json`:

```json
{
  "subtask": "{subtask_title}",
  "status": "complete",
  "failreason": null,
  "openFindings": 0,
  "workflowRelevantFindings": [],
  "testsPassed": true,
  "readyForVerification": true,
  "openGateItems": []
}
```

The application reads only `status`, `failreason` and `openGateItems` from this file.
`openGateItems` is mandatory: a JSON array of strings, one per open item (see "Gate results and
open items"), and `[]` when nothing is open. The application lists these items to the user; a
value that is not an array of strings is shown as unreadable. `status` counts as success
solely with the value `complete`; use `"failed"` when the subtask could not be finished, and then
state the cause in `failreason` as one short sentence — that text is what the user sees. `deferred`
and `blocked` mean what "Status values" above says; any other value, including the initial
`pending`, means "not finished yet".

### 2. The flag

Then write `{subtask_path}/{subtask_title}/result.json`. It is only a flag: the application does not
interpret its contents, it merely requires the file to exist and **must not be empty** — a file of
0 byte is recognised as no flag at all. A minimal payload is enough:

```json
{
  "subtask": "{subtask_title}",
  "status": "complete"
}
```

### Atomic publication

Never write either file in place. Publish both atomically through a temporary file:

1. write to `status.json.tmp` respectively `result.json.tmp`,
2. finish writing and close the file,
3. rename it to `status.json` respectively `result.json`.

Otherwise the application reads a half-written file.
