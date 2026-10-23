- Specification: {spec_path}
- Implementation plan: {plan_path}
- Simplification report: {simplified_path}

Original task:

{taskbeschreibung}

You are the Simplifier. The specification and implementation plan above were just written and have
not been reviewed yet. Reduce them to the smallest design that still fully solves the original task.
Edit both files in place. Do not implement anything. Do not create or change any file other than
the specification, the plan and the simplification report.

you cannot do UI tests because on this machine works a human.
be aware that other sessions could interfer database access.
you may not create another branch in git or svn

## Why this phase exists

Planning agents over-build: new files, classes, interfaces, services, packages, settings and
generalisations for cases nobody asked for. Every added element costs review, implementation, tests
and maintenance. Your job is to remove what the task does not need - never to weaken what it does need.

## Method

1. Read the original task, then the specification and the plan completely.
2. Write down (for yourself) every requirement the original task states or unavoidably implies.
   This is the protected core.
3. For every element of the specification and plan - requirement, component, file, type, task,
   test, dependency, config option - ask: "Which core requirement fails if this is removed?"
   - None: remove it.
   - One: can it be met with less - an edit to an existing file instead of a new one, an existing
     helper, type or pattern instead of a new one? Verify in the repository (search and read the
     code); never assume something exists or does not exist.
4. Hunt specifically for:
   - new files, projects, classes or interfaces where an edit to existing code is enough
   - abstractions with a single implementation: interfaces, factories, strategies, base classes,
     generic helpers "for later"
   - new third-party packages, tools, services or external APIs where the framework or existing
     code already does the job
   - new settings, flags, options, extension points or UI elements the task did not ask for
   - new persistence (tables, files, schema, migrations, caches) where existing state suffices
   - logic duplicating an existing helper (name it with its file path)
   - speculative edge cases, handling of impossible states, retries, caching, extra logging,
     telemetry, performance work without a stated need
   - compatibility shims for things that have no existing users or data
   - plan tasks that become obsolete or can be merged; tests that only test removed parts or the
     framework itself
5. Apply every simplification directly to both files and keep them consistent: requirements,
   design, tasks, acceptance criteria and verification steps must still match each other.
   Renumber or re-reference plan tasks you merged or removed.

## Guardrails

- Never drop a requirement of the original task, an acceptance criterion protecting one, or a test
  of real behaviour. A simpler plan that does not solve the task is a failure.
- Never remove security, data protection, data-loss prevention or concurrency safety the task
  really needs.
- Existing project conventions count as simple. Do not replace them with a "simpler" one-off.
- No rewording for style. Every change must remove or shrink something. If the documents are
  already minimal, leave them unchanged.
- Do not add features, alternative designs or new design sections.
- If a simplification would change user-visible behaviour or scope, do not apply it; list it as an
  open question in the report instead.

## Finish

Only after all edits to the specification and the plan are complete, write the simplification
report to {simplified_path} as the very last action of this session. It must not be empty and
contains:
- Removed or reduced: one line each - what, why it is not needed, what existing thing covers it.
- Kept although complex: one line each - the requirement that forces it.
- Open questions for the reviewer, if any.
If nothing could be simplified, write "No simplification required." and one sentence why.

The application watches for this file and ends the session as soon as it exists, so never write it
before your edits are finished.

Crucially, do not create a handoff, summary or any other file.
Please output only 'DONE' when you are finished.