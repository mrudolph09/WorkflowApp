Implement the approved feature using Superpowers together with planning-with-files.

Spezifikation:
{spec_path}

Implementierungsplan:
{plan_path}

The plans have already been reviewed and approved. Treat them as the canonical source of truth.
you may not create another branch in git or svn

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
* Record every gate run in `progress.md` with command, exit code and result. A step that was
  `failed` once in this task stays `failed` until a later run of the same step is `passed`; it is
  never relabelled `not_run`.
* A `failed` step blocks completion: fix the cause and run the step again. Without a fix there is
  no completion report.
* A `not_run` step does not block completion. It becomes an open item:
  `<step id>: <what a human has to do or check, and what they should observe>`.

## Signalling completion

When, and only when, every condition under "Completion" above is satisfied, write a completion
report to {done_path} as the very last action of this session. Open items do not hold the report
back: a task that is implemented and reviewed gets its report even when gate steps are `not_run`.

The report uses exactly this layout; the application reads the `## Offene Punkte` section:

```markdown
<one or two sentences naming what was implemented>

## Gate-Ergebnis
- <step id>: passed|not_run - <command and exit code, or the allowed reason>

## Offene Punkte
- <step id>: <what a human has to do or check>
```

The `## Offene Punkte` heading is mandatory. When nothing is open, the only line below it is
`- keine`.

The application watches for this file. Until it exists, the workflow is considered unfinished
and will offer to resume this task the next time it starts.
