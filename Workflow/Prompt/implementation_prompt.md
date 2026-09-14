Implement the approved feature using Superpowers together with planning-with-files.

Spezifikation:
{spec_path}

Implementierungsplan:
{plan_path}

The plans have already been reviewed and approved. Treat them as the canonical source of truth.

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

* `{AppDirectory}\{taskbezeichnung}\task_plan.md` — execution phases and completion state
* `{AppDirectory}\{taskbezeichnung}\findings.md` — discoveries made while implementing
* `{AppDirectory}\{taskbezeichnung}\progress.md` — chronological work, changed files, tests, validation results and errors

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
Canonical task: {AppDirectory}\{taskbezeichnung}\...md#task-1

Phase 2
Canonical task: {AppDirectory}\{taskbezeichnung}\...md#task-2

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

When, and only when, every condition under "Completion" above is satisfied, write a short
completion report to {done_path} as the very last action of this session. The file must not be
empty: one or two sentences naming what was implemented and the result of `Workflow\verify.ps1`
is enough.

The application watches for this file. Until it exists, the workflow is considered unfinished
and will offer to resume this task the next time it starts.
