Implement the approved feature using Superpowers together with planning-with-files.

{subtask}

The plans have already been reviewed and approved. Treat them as the canonical source of truth.

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
* acceptance criteria satisfied
* required tests passing
* the acceptance gate `Workflow\verify.ps1` exits 0
* type checking passing
* lint/static analysis passing where applicable
* no unresolved implementation blockers
* final implementation reviewed against the canonical specification and plan
* PWF execution state accurately reflecting completion

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
  "readyForVerification": true
}
```

The application reads only `status` and `failreason` from this file. `status` counts as success
solely with the value `complete`; use `"failed"` when the subtask could not be finished, and then
state the cause in `failreason` as one short sentence — that text is what the user sees. Any other
value, including the initial `pending`, means "not finished yet".

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
