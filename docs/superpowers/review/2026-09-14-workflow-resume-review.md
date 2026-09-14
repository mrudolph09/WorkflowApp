# Delta critique — workflow resume implementation plan

**Reviewed:** `docs/superpowers/plans/2026-09-14-workflow-resume-plan.md` against the approved design and the current source tree.  
**Verdict:** Correct the deltas below before executing the plan. This document intentionally does not restate the design or plan.

## Findings

### D1 — [P1] Persist reconciliation demotions

The scanner demotes phases only on the `TaskState` instance returned to the recovered tab (`TaskRecoveryScanner`, plan lines 2022–2048); it never writes those demotions back to the journal. This loses the load-bearing “and every phase after it” rule from SPEC §6.3 across a second crash. For example, if phase 1–3 were recorded complete, the plan artefact disappears, recovery demotes phase 1–4 in memory, and the resumed phase 1 completes before the app crashes again, the on-disk journal now has phase 1–3 complete again. The next startup therefore skips phases 2 and 3. Add an atomic store operation that persists the reconciled phase tail (or clear the tail durably before the resumed run starts), plus a regression test covering this two-crash sequence.

### D2 — [P1] Make unknown phase names deserializable before normalisation

Task 5 configures `JsonStringEnumConverter`, deserializes directly into `TaskState`, and only then calls `Normalise` (plan lines 834–864). An entry such as `"phase": "Nonsense"` throws `JsonException` during deserialization, so `TryLoad` returns `null`; the normaliser at lines 936–958 never sees the entry. Consequently the supplied `TryLoad_ShortOrReorderedPhaseArray_NormalisesToCatalogueOrder` test cannot pass and SPEC §5.4/R3 is not implemented. Parse phase entries through a tolerant DTO/custom converter so unknown phases can be dropped individually while valid entries survive.

### D3 — [P1] Wire the new submit settings through `RuleSetDto`

Task 7 extends `AutoAnswerRuleSet` and describes normalising a `set` (plan lines 1338–1368), but current `AutoAnswerService.Load` deserializes `RuleSetDto` and constructs the rule set from that DTO; there is no deserialized `set` or fallback rule set in the current method. The plan never adds the four properties to `RuleSetDto` or passes them to the constructor, so version-2 JSON values are ignored and `RuleSet_FileSetsTheSubmitFields_UsesThem` fails. Amend the plan with the DTO fields, constructor mapping, and concrete missing/invalid-file behavior before the normalisation step.

### D4 — [P1] Start the quiet gate at paste time

`SendPromptAsync` records no post-paste baseline and checks only `LastOutputUtc` (plan lines 1553–1568). The preceding launcher settle loop normally returns only after output has already been quiet, so the first iteration can observe that old timestamp and send `\r` immediately, before the asynchronous PTY echo from `SendPaste` arrives—the same early-submit failure this task is meant to remove. Seed the quiet gate at the time the paste is sent (and let later output move that baseline forward), rather than accepting pre-paste silence; add a test where paste output arrives asynchronously after `SendPaste` returns.

### D5 — [P2] Make the five-second scan ceiling real

The proposed timeout token is checked only around synchronous enumeration (plan lines 1931–1981). `Directory.Exists` or `Directory.EnumerateDirectories(...).ToList()` can block inside Windows/network I/O and cannot observe that token, so a disconnected share can keep `ScanAsync` incomplete well beyond five seconds and no partial result is returned as required by SPEC §6.2. Structure the public operation so it returns a snapshot at the deadline even if a worker remains blocked, or revise the specification and acceptance claim; also ensure caller cancellation returns the contractually promised partial list rather than a canceled `Task.Run` task.

### D6 — [P2] Snapshot the MRU before scanning it on a worker

`Scan` enumerates the live `Settings.RecentDirectories` collection on the background thread (plan line 1938), while the already-visible blank tab can call `AddRecentDirectory` on the UI thread. If the user changes the directory during a slow startup scan, the collection can be modified mid-enumeration, faulting the fire-and-forget recovery task with `InvalidOperationException`. Copy the MRU on the UI thread before `Task.Run` (or synchronize settings access) and scan the immutable snapshot.

### D7 — [P2] Reset indicators when re-arming finds no unfinished state

`RefreshResumeStateFromJournal` only clears `ResumePhase` when the journal is absent and paints the journal before discovering that it is complete (plan lines 2360–2374). Reusing a tab after a previous journal therefore leaves stale green/yellow indicators, and a complete journal leaves all indicators green even though the button switches to a fresh `Start workflow`. SPEC §6.6 explicitly requires both cases to reset the indicators to `Pending`; add that reset and tests for switching from a recovered task to a journal-free or completed task.

### D8 — [P2] Remove the contradictory TaskStateStore test instruction

`SaveDescription_TaskDirectoryMissing_DoesNotThrow` asserts that no journal exists after the save (plan lines 705–713), while the prescribed implementation creates the task directory and journal. Line 1011 then tells the implementer to change the assertion only after it fails, despite the task claiming the suite should pass. Choose the intended behavior up front—consistent with the supplied implementation, assert that the journal was recreated—so the TDD step has one deterministic contract.

## Review boundary

This critique covers only plan-to-spec/current-source deltas. Behaviors explicitly accepted by the approved specification, including the fresh-run populated-folder hazard and stale done-marker delete failure, are not reopened here. No implementation or runtime tests were run because the reviewed commit is documentation-only.
