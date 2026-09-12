- Specification: {spec_path}
- Implementation plan: {plan_path}
- Review: {review_path}


Use the existing Superpowers planning methodology to resolve the review.

For every review finding:

1. Verify the finding against the actual repository.
2. Determine whether the finding is valid, partially valid, or invalid.
3. If valid, update the canonical specification and/or implementation plan directly.
4. If partially valid, incorporate only the justified portion.
5. If invalid, do not modify the plan merely to satisfy the reviewer.
6. Resolve contradictions between the specification, implementation plan, and current codebase.
7. Make acceptance criteria and verification steps explicit where the review exposes ambiguity.

Important:

* The Superpowers specification and implementation plan remain the canonical source of truth.
* Do not create a second replacement plan.
* Do not summarize the plan into a handoff.
* Do not implement anything yet.
* Do not initialize planning-with-files yet.
* Preserve good decisions already made unless repository evidence or the review demonstrates a problem.

When finished, report:

* which were rejected
* whether any unresolved human decisions remain

If there are no unresolved decisions, consider the implementation plan frozen and ready for execution.

Both {spec_path} and {plan_path} must be written to, even if only to append a short
`## Review resolution` note recording that no change was required.

Please do not output the whole review or plan changes. adapting the files is sufficient.
