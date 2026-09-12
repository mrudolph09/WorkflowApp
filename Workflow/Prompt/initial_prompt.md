We need to design and plan the following feature/task:

{taskbeschreibung}

Use the Superpowers workflow for the planning phase.

For this phase:

1. Use Superpowers brainstorming to investigate the existing codebase, clarify the requirements, identify constraints, and evaluate reasonable implementation approaches.
2. Produce the canonical design/specification using the Superpowers conventions.
3. After the design is sufficiently resolved, use Superpowers writing-plans to produce the detailed implementation plan.
4. Inspect the actual codebase before making architectural assumptions.
5. Reference exact files, relevant symbols, existing patterns, tests, APIs, and architectural constraints wherever possible.
6. Include explicit acceptance criteria and verification steps.
7. Prefer existing project conventions over introducing new abstractions.
9. Apply YAGNI and DRY; call out unnecessary complexity.
10. Identify migrations, compatibility concerns, failure modes, edge cases, and likely regressions.
11. Make each implementation task sufficiently self-contained that an engineer with no conversational context could execute it from the plan alone.

Important workflow rules:

* Do NOT implement the feature yet.
* Do NOT initialize planning-with-files yet.
* Do NOT create task_plan.md, findings.md, or progress.md yet.
* The Superpowers specification and implementation plan are the canonical source of truth.
* Do not rely on conversational context for information that belongs in the specification or implementation plan.
* Stop after the specification and implementation plan are complete.

create the specification/design in {spec_path}
create the implementation plan in {plan_path}

Please output only 'DONE' when you finished the specification and implementation plan.
