---
description: "Canonical Superpowers design specification for workflow-level run tracking, crash recovery ('Continue workflow'), phase-4 completion detection and the prompt auto-submit fix in the Workflow WPF app"
summary: "Adds a per-task journal file W\\T\\.workflow-state.json (atomic write, stores taskDescription + per-phase status + dismissed; deliberately NOT taskName/workingDirectory, which are derived from the folder and its parent). WorkflowOrchestrator becomes the single journal writer and gains a StartPhase so a run can resume mid-pipeline. At startup TaskRecoveryScanner walks RecentDirectories one level deep, offers at most 5 non-dismissed, incomplete tasks newer than 14 days as prefilled tabs whose button reads 'Continue workflow'; closing such a tab sets dismissed. Journal is authoritative for phase 3 only - missing FilesExist artefacts DEMOTE a phase (and everything after it), never promote. Phase 4 stops being CompletionRule.Manual: implementation_prompt.md must write a non-empty {done_path} = W\\T\\T-done.md as its final action, detected by the existing ArtifactWatcher; the orchestrator deletes a stale marker before arming that watcher, and CompletionRule.Manual is removed as dead. Phase 3 becomes CompletionRule.AllContentChanged (BOTH spec and plan), matching resolve_review_prompt.md. Root cause of 'the prompt does not auto-submit': TerminalViewModel.SendPasteAsync waits a fixed 150 ms before the CR, which Ink coalesces into the paste; fix moves the submit dance into WorkflowOrchestrator behind a quiet-gate with an OutputCount-based verify and one retry, tunables in autoanswer.rules.json (version 2). Out of scope, unchanged: the pre-existing §8.3 hazard where a fresh run in a fully populated folder satisfies phase 1 instantly. Revised 2026-09-14 after independent review: the journal is READ through a tolerant DTO (an unknown phase/status name drops that entry, not the file); reconciled demotions are WRITTEN BACK via a new ITaskStateStore.ReplacePhases so they survive a second crash; the demote-never-promote rule is one shared PhaseReconciliation helper used by both the startup scan and the SyncFolder re-arm; the re-arm resets ResumePhase AND all four indicators when the journal is absent or complete; the paste quiet-gate's baseline starts at SendPaste (LastOutputUtc alone is already stale when SettleAndAnswerAsync returns); the 5 s scan ceiling is enforced on the awaiting side over a thread-safe accumulator because Directory.* blocks past any token; the MRU is snapshotted on the UI thread; and the four submit tunables must be added to AutoAnswerService's private RuleSetDto, not only to AutoAnswerRuleSet."
paths:
  - "../plans/2026-09-14-workflow-resume-plan.md"
  - "./specification.md"
  - "../../../Workflow/Services/WorkflowOrchestrator.cs"
  - "../../../Workflow/Services/ArtifactWatcher.cs"
  - "../../../Workflow/Services/SettingsService.cs"
  - "../../../Workflow/Models/TaskPaths.cs"
  - "../../../Workflow/Models/PhaseCatalog.cs"
  - "../../../Workflow/Models/WorkflowPhase.cs"
  - "../../../Workflow/ViewModels/TaskTabViewModel.cs"
  - "../../../Workflow/ViewModels/MainWindowViewModel.cs"
  - "../../../Workflow/ViewModels/TerminalViewModel.cs"
  - "../../../Workflow/Prompt/implementation_prompt.md"
  - "../../../Workflow/Assets/autoanswer.rules.json"
  - "../../../Workflow/verify.ps1"
---

# Workflow — Run Tracking, Crash Recovery and Auto-Submit — Design Specification

**Status:** Approved for planning
**Date:** 2026-09-14
**Repository:** `C:\Users\Marco\Documents\repo\Workflow` (git, branch `master`)
**Supersedes nothing.** This document *extends* `docs/superpowers/specs/specification.md` (the
original application design, referred to below as **BASE**). Where this document and BASE
disagree, this document wins for the areas it covers (§7.1 phase table, §7.2 step 7, §7.5
completion rules, §11 persistence); everything else in BASE is unchanged and still binding.

---

## 1. Purpose

Four connected problems, all observed in production use of the app:

1. **A run cannot be resumed.** If `Workflow.exe` crashes or is closed while a task is
   mid-pipeline, everything about that run is lost. `WorkflowOrchestrator.RunAsync` always
   iterates `PhaseCatalog.All` from index 0, and no per-task state is written anywhere.
2. **There is no workflow-level progress record.** The only progress artefact in the system is
   `progress.md`, and that exists solely inside phase 4, is written by the CLI, not the app, and
   is about *implementation tasks* — not about *which phase of the workflow this task is in*.
3. **Phase 4 has no completion detection at all.** `PhaseCatalog` declares it
   `CompletionRule.Manual`; only the *Task abschliessen* button ends it.
4. **The rendered prompt does not submit itself.** After each phase's prompt is pasted, the user
   must press Enter by hand.

Goal: a task interrupted at any point can be picked up again from the phase it died in, with its
fields prefilled, from a **Continue workflow** button — and each phase ends on its own.

### 1.1 Scope boundary against `progress.md`

`progress.md`, `findings.md` and `task_plan.md` are **planning-with-files** artefacts written by
Claude Code *inside* phase 4, under the instructions in `Workflow\Prompt\implementation_prompt.md`.
They describe implementation tasks. This design does not read them, does not write them, and does
not change their contract. They stay task-related, exactly as required.

The new journal introduced here is a **second, independent monitoring layer** that describes the
*workflow*: which of the four phases have finished. The two never overlap.

---

## 2. Scope

### 2.1 In scope

- A per-task journal file recording workflow phase progress and the task description.
- A startup scan that finds interrupted tasks and offers them as prefilled tabs.
- Resuming a run at an arbitrary phase.
- Automatic completion detection for phase 4.
- Tightening phase 3's completion rule from "either file" to "both files".
- Fixing the prompt auto-submit defect (all four phases — it is one shared code path).

### 2.2 Out of scope (YAGNI)

- **The pre-existing BASE §8.3 hazard**: starting a *fresh* run (phase 1) in a task folder that
  already contains `T_spec.md` and `T_plan.md` satisfies phase 1's watcher immediately. This
  design does not change that behaviour. It is documented in §10.5 as a known limitation, and the
  recovery path in §6 is constructed so that it is never *reached* by a resume.
- Multi-instance coordination. Two copies of `Workflow.exe` writing the same journal is
  last-writer-wins; not detected, not locked.
- Sub-phase granularity. The journal records whole phases, never "half of phase 2".
- A recovery dialog / picker UI. Rejected in favour of prefilled tabs (§6.4).
- Migration of tasks created before this feature. A task folder with no journal is simply not
  discovered (§6.6).
- Any change to `progress.md` / `findings.md` / `task_plan.md` (§1.1).

---

## 3. Verified codebase facts this design rests on

Each row was read from the files on disk, not assumed.

| # | Fact | Evidence |
|---|---|---|
| F1 | `RunAsync` always starts at phase 0. | `Workflow/Services/WorkflowOrchestrator.cs:45` — `foreach (var definition in PhaseCatalog.All)` |
| F2 | Nothing per-task is persisted. `AppSettings` holds only `RecentDirectories` and `LastDirectory`. | `Workflow/Models/AppSettings.cs` |
| F3 | The Taskbeschreibung exists **only** in memory. | `TaskTabViewModel._taskDescription`; no writer anywhere |
| F4 | Phase 4 completion is manual-only. | `Workflow/Models/PhaseCatalog.cs:18` — `CompletionRule.Manual` |
| F5 | Phase 3 completes on *either* file changing. | `PhaseCatalog.cs:16` + `ArtifactWatcher.CheckAndSignal` — `_paths.Any(HasChangedSinceBaseline)` |
| F6 | The submit CR is written after a hard-coded 150 ms. | `Workflow/ViewModels/TerminalViewModel.cs:26` — `SubmitDelay = TimeSpan.FromMilliseconds(150)`; used in `SendPasteAsync` |
| F7 | `ITerminalController` already exposes `OutputCount` and `LastOutputUtc`, both reset by `StartSession`. | `Workflow/Services/ITerminalController.cs` |
| F8 | The orchestrator already owns a quiet-gate loop over those two members. | `WorkflowOrchestrator.SettleAndAnswerAsync` (Gate A / Gate B) |
| F9 | `autoanswer.rules.json` is already the terminal-timing config file, not only a rule list. | It carries `quietPeriodMs`, `settleTimeoutMs`, `maxAnswersPerPhase` |
| F10 | Watchers are driven by `Task.WhenAny(watcher, manualSignal)` for every non-`Manual` rule. | `WorkflowOrchestrator.RunPhaseAsync` |
| F11 | `TaskPaths` is the single source of truth for artefact paths; nothing may compose them by hand. | `Workflow/Models/TaskPaths.cs` class comment |
| F12 | `SettingsService.Save` is the house pattern for durable writes: serialise to `.tmp`, `File.Move(..., overwrite: true)`, swallow `IOException`. | `Workflow/Services/SettingsService.cs` |
| F13 | `MainWindowViewModel`'s constructor calls `AddTaskTab()`, so there is always ≥ 1 tab. | `Workflow/ViewModels/MainWindowViewModel.cs` |
| F14 | `TaskTabViewModel.ApplyProgress` receives every `PhaseProgress` and is already `public` for testing. | `TaskTabViewModel.cs` |
| F15 | `CompleteCurrentPhaseCommand` ("Phase abschliessen") exists on the view model but is **not bound** in `TaskTabView.xaml`. Only `CompleteTaskCommand` is. | `Workflow/Views/TaskTabView.xaml` |
| F16 | `verify.ps1` hard-codes the known prompt-token list in `$known`. | `Workflow/verify.ps1` |
| F17 | Warnings are errors; `CA1002` forbids public `List<T>`; `IDE0040` requires accessibility on every interface member. | `Directory.Build.props`, `Workflow/.roslyn`, BASE §4.4.1 |

F15 is recorded because it is adjacent to this work and would otherwise look like a regression
introduced here. **It is left as-is**; fixing it is not in scope.

---

## 4. Architecture

Three new units, each with one purpose, plus changes to four existing ones.

```
                         ┌──────────────────────────┐
   App.OnStartup ───────▶│   TaskRecoveryScanner    │ reads a SNAPSHOT of
                         │  (new, ITaskRecovery-    │ RecentDirectories, one level
                         │      Scanner)            │ deep, returns ≤5
                         │  uses PhaseReconciliation│ RecoverableTask
                         └────────────┬─────────────┘
                                      │ RecoverableTask[]
                                      ▼
   ┌──────────────────────┐   ┌──────────────────────┐
   │ MainWindowViewModel  │──▶│  TaskTabViewModel    │  LoadForResume(...)
   │  (adds recovered     │   │  ResumePhase,        │  StartButtonLabel
   │   tabs, dismiss on   │   │  IsRecovered         │
   │   CloseTab)          │   └──────────┬───────────┘
   └──────────────────────┘              │ WorkflowRunRequest{ StartPhase }
                                         ▼
                         ┌──────────────────────────┐
                         │   WorkflowOrchestrator   │──┐ RecordPhase(...)
                         │  Skip((int)StartPhase)   │  │
                         └────────────┬─────────────┘  │
                                      │                ▼
                          IArtifactWatcher     ┌──────────────────┐
                          (FilesExist /        │  TaskStateStore  │  W\T\.workflow-state.json
                           AllContentChanged)  │ (new, ITaskState-│  atomic tmp + Move
                                               │      Store)      │
                                               └──────────────────┘
```

**New files**

| File | Purpose |
|---|---|
| `Workflow/Models/TaskState.cs` | `TaskState`, `TaskPhaseState` — the journal's shape. |
| `Workflow/Models/RecoverableTask.cs` | `RecoverableTask` — one scan hit: paths + state + resume phase. |
| `Workflow/Models/PhaseReconciliation.cs` | The demote-never-promote rule (§6.3) and the resume-phase lookup, shared by the scanner and the tab's re-arm path (§6.3.2). |
| `Workflow/Services/ITaskStateStore.cs` | Journal read/write contract. |
| `Workflow/Services/TaskStateStore.cs` | Implementation. |
| `Workflow/Services/ITaskRecoveryScanner.cs` | Startup scan contract. |
| `Workflow/Services/TaskRecoveryScanner.cs` | Implementation. |

**Changed files**

`Workflow/Models/TaskPaths.cs`, `Workflow/Models/PhaseCatalog.cs`,
`Workflow/Models/AutoAnswerRule.cs`, `Workflow/Services/AutoAnswerService.cs`,
`Workflow/Models/WorkflowPhase.cs`, `Workflow/Services/ArtifactWatcher.cs`,
`Workflow/Services/PromptTemplateService.cs` (`PromptVariables`),
`Workflow/Services/IWorkflowOrchestrator.cs`, `Workflow/Services/WorkflowOrchestrator.cs`,
`Workflow/Services/ITerminalController.cs`, `Workflow/Services/TaskTabViewModelFactory.cs`,
`Workflow/ViewModels/TerminalViewModel.cs`, `Workflow/ViewModels/TaskTabViewModel.cs`,
`Workflow/ViewModels/MainWindowViewModel.cs`, `Workflow/Views/TaskTabView.xaml`,
`Workflow/App.xaml.cs`, `Workflow/Prompt/implementation_prompt.md`,
`Workflow/Assets/autoanswer.rules.json`, `Workflow/verify.ps1`,
`Workflow.Tests/Fakes/FakeTerminalController.cs`.

---

## 5. The journal

### 5.1 Location and name

`{TaskDirectory}\.workflow-state.json` — i.e. `W\T\.workflow-state.json` for working directory
`W` and Taskbezeichnung `T`. Exposed as a new `TaskPaths.StateAbsolute` property (F11: nothing
composes this path by hand).

Rationale for putting it *in the task folder* rather than in `%APPDATA%`:

- It is the only place that cannot go stale. Renaming or moving the task folder — including a
  rename through `TaskFolderService.Rename`, which `Directory.Move`s the whole folder — carries
  the journal with it automatically.
- It keeps the workflow state next to the artefacts it describes.
- No index to reconcile.

The cost is a directory scan at startup (§6.2), which is bounded.

**The leading dot is cosmetic only.** Windows does not treat it specially; the file is not
hidden. It is chosen so the journal sorts away from `T_spec.md` / `T_plan.md` / `T-review.md` and
reads as machine-owned.

### 5.2 Shape

```json
{
  "version": 1,
  "taskDescription": "…the Taskbeschreibung, verbatim…",
  "createdUtc": "2026-09-14T09:12:03.4410000Z",
  "updatedUtc": "2026-09-14T10:41:55.1200000Z",
  "dismissed": false,
  "phases": [
    { "phase": "Specification",  "status": "Completed", "completedUtc": "2026-09-14T09:44:10.0000000Z" },
    { "phase": "Review",         "status": "Completed", "completedUtc": "2026-09-14T10:02:31.0000000Z" },
    { "phase": "ResolveReview",  "status": "Active",    "completedUtc": null },
    { "phase": "Implementation", "status": "Pending",   "completedUtc": null }
  ]
}
```

C# model (`Workflow/Models/TaskState.cs`):

```csharp
public sealed record TaskPhaseState(WorkflowPhase Phase, PhaseStatus Status, DateTimeOffset? CompletedUtc);

public sealed class TaskState
{
    public int Version { get; set; } = TaskState.CurrentVersion;
    public string TaskDescription { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public bool Dismissed { get; set; }
    public Collection<TaskPhaseState> Phases { get; set; } = [];
}
```

`Collection<T>`, not `List<T>`: F17 — `CA1002` is an error in this repository, and
`System.Text.Json` round-trips `Collection<T>` without a converter. This mirrors
`AppSettings.RecentDirectories` exactly, including the `[SuppressMessage("Usage", "CA2227", …)]`
that a settable collection property requires.

Enums are serialised **as strings** (`JsonStringEnumConverter`), so a future reordering of
`WorkflowPhase` cannot silently reinterpret an old journal.

**The write path and the read path are not symmetric.** `TaskState` above is what the store
*writes*. It is not what the store *reads* into: `JsonStringEnumConverter` throws `JsonException`
on a string it cannot map to an enum member, which would turn one hand-edited `"phase": "Nonsense"`
into "the whole journal is invalid" and defeat R3 (§10.1). Reading therefore goes through a
tolerant DTO whose `phase` and `status` are plain `string?`; entries that do not map to a
`WorkflowPhase` / `PhaseStatus` member are dropped individually, and everything around them
survives. Normalisation (§5.4) then rebuilds the full four-element array. Only a document that is
not valid JSON at all, or whose `version` is too new, makes `TryLoad` return `null`.

### 5.3 What is deliberately *not* stored

- **`taskName`** — derived from `Path.GetFileName(taskDirectory)`.
- **`workingDirectory`** — derived from `Path.GetDirectoryName(taskDirectory)`.

Storing either would create a second source of truth that goes wrong the moment the folder is
renamed or moved outside the app. Deriving them makes rename and move correct for free, which is
the same reasoning `TaskPaths` already applies (F11).

### 5.4 `ITaskStateStore`

```csharp
public interface ITaskStateStore
{
    public TaskState? TryLoad(TaskPaths paths);
    public void SaveDescription(TaskPaths paths, string taskDescription);
    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status);
    public void ReplacePhases(TaskPaths paths, IReadOnlyList<TaskPhaseState> phases);
    public void SetDismissed(TaskPaths paths, bool dismissed);
}
```

Every mutating method is load-modify-save internally and stamps `UpdatedUtc` — except
`ReplacePhases`, which deliberately does not (see its row below). Five narrow,
intention-revealing methods rather than a raw `Save(TaskState)` — the callers never need to
assemble a whole `TaskState`, and keeping assembly inside the store means `Phases` is always the
complete four-element array in catalogue order.

`public` on every interface member: F17 — `IDE0040` is an error here (BASE §4.4.1), and every
existing interface in `Workflow/Services` is written that way.

**Behaviour contract**

| Method | Contract |
|---|---|
| `TryLoad` | Returns `null` for: file absent, unreadable (`IOException` / `UnauthorizedAccessException`), not valid JSON (`JsonException`), or `Version` greater than `CurrentVersion`. Never throws. A `Phases` array that is missing, short, out of order or holds an unknown phase **or status name** is normalised to the full four-element catalogue order: unmappable entries are dropped one by one (§5.2, tolerant DTO), missing ones default to `Pending`. An unknown phase name therefore costs that one entry, never the whole journal. |
| `SaveDescription` | Creates the journal if absent (`CreatedUtc` = now, all four phases `Pending`); otherwise overwrites only `TaskDescription`. Also clears `Dismissed` — the user has explicitly started/continued this task. |
| `RecordPhase` | Creates the journal if absent. Sets that phase's `Status`; sets `CompletedUtc` when and only when `status == PhaseStatus.Completed`, clears it otherwise. |
| `ReplacePhases` | Overwrites the whole `Phases` array with the supplied one, normalised. **No-op when the journal is absent** — there is nothing to correct. It leaves `UpdatedUtc` untouched: the only caller is the reconciliation of §6.3, which records what the disk already said rather than new progress, and bumping the timestamp would keep a task inside the 14-day window (§6.2) purely because an artefact was deleted. This is the one durable write that exists so that a reconciliation survives a *second* crash (§6.3). |
| `SetDismissed` | No-op when the journal is absent. |

**Write durability.** Exactly the `SettingsService.Save` pattern (F12): serialise to
`{path}.tmp`, `File.Move(tmp, path, overwrite: true)`, `catch (IOException)` and
`catch (UnauthorizedAccessException)` swallowed, `.tmp` best-effort deleted in `finally`.
A journal write that fails must never take down a live workflow run — the run continues and only
recovery is degraded. This is the same trade-off the app already makes for settings.

**Interaction with `ArtifactWatcher`.** The journal lives inside the directory the watcher
monitors (`FileSystemWatcher` on `TaskDirectory`, `NotifyFilters.LastWrite | FileName | Size`).
Writing it therefore raises watcher events. This is harmless: `CheckAndSignal` re-evaluates the
actual rule against the actual artefact paths and the journal is not one of them. It costs one
extra debounced re-check per phase transition.

---

## 6. Recovery

### 6.1 `RecoverableTask`

```csharp
public sealed record RecoverableTask(TaskPaths Paths, TaskState State, WorkflowPhase ResumePhase);
```

### 6.2 The scan

`TaskRecoveryScanner.ScanAsync(CancellationToken)` returns `IReadOnlyList<RecoverableTask>`:

0. Copy `ISettingsService.Settings.RecentDirectories` **synchronously, on the calling thread**,
   before any worker starts. The caller is `MainWindowViewModel.InitialiseAsync`, i.e. the
   dispatcher, and `SettingsService.AddRecentDirectory` mutates that very `Collection<string>`
   from the UI thread — the blank tab is already visible and usable while the scan runs (§6.4).
   Enumerating the live collection on a worker thread would fault the scan with
   `InvalidOperationException` the moment the user picks a directory. The worker only ever sees
   the immutable snapshot.
1. For each entry of that snapshot, in order:
   - Skip if `!Directory.Exists(entry)`.
   - Enumerate its **immediate** subdirectories (`Directory.EnumerateDirectories`,
     `SearchOption.TopDirectoryOnly`), stopping after **2 000** entries for that directory.
   - For each subdirectory, build `new TaskPaths(entry, folderName)` and `TryLoad` it. A `null`
     result means "not one of ours" — skip silently.
2. De-duplicate by `TaskPaths.TaskDirectory`, `StringComparison.OrdinalIgnoreCase`.
3. Drop any task where `State.Dismissed` is true.
4. Drop any task where `UpdatedUtc < DateTimeOffset.UtcNow - 14 days`. An `UpdatedUtc` in the
   future (clock skew, or a file copied from another machine) is treated as recent, not dropped.
   **This filter runs before reconciliation**, so a journal old enough to be ignored is never
   written to.
5. Reconcile against disk and compute `ResumePhase` (§6.3), persisting the reconciliation when it
   demoted anything (§6.3.1). Drop any task where every phase is `Completed`.
6. Order by `UpdatedUtc` descending, `Take(5)`. Ordering and capping happen on the *snapshot*
   returned at the deadline, not inside the worker, so a partial result is still the most recently
   updated five of what was found.

**Bounding.** The whole scan runs off the UI thread and is bounded by a **5-second** deadline;
whatever has been collected when the deadline passes is returned rather than nothing. Together
with the 2 000-subdirectory cap this makes a slow or enormous MRU entry — a network share, a
OneDrive folder, a repository root with thousands of directories — an inconvenience rather than a
hang. The same class of failure is already acknowledged for `FileSystemWatcher` in BASE §7.5.

**The deadline is enforced on the awaiting side, not only inside the worker.** A cancellation
token cannot interrupt `Directory.Exists` or the first `MoveNext` of `Directory.EnumerateDirectories`:
both block inside Win32/SMB, and a disconnected share can hold them for far longer than five
seconds. `ScanAsync` therefore starts the enumeration on a worker that appends each hit to a
thread-safe collection, and returns a **snapshot of that collection** as soon as either the worker
finishes or the deadline expires. A worker still stuck in a blocking call is abandoned, not
awaited: it holds no disposable resource, it only ever appends, and nothing reads the collection
after the snapshot is taken. The linked `CancellationTokenSource` is disposed from the worker's
continuation, so an abandoned worker never observes a disposed token.

This is what makes the 5-second ceiling a real guarantee rather than a best-effort one. It is also
why the ceiling is a *ceiling on `ScanAsync`*, not a promise that the worker thread has stopped.

**Caller cancellation** behaves the same way as the deadline: the promised partial list is
returned. In particular the worker is started with `CancellationToken.None`, because handing the
caller's token to `Task.Run` would make an already-cancelled caller produce a *cancelled task*
instead of the empty list the contract promises.

**Exception policy.** `IOException`, `UnauthorizedAccessException` and
`DirectoryNotFoundException` from an individual directory skip that directory and continue. The
scan never throws; a failing scan must not prevent the application from starting.

Constants (`14` days, `5` tasks, `2 000` subdirectories, `5` seconds) are `internal const`
fields on `TaskRecoveryScanner`, injected through the constructor so tests can shorten them.
They are **not** user settings — YAGNI until asked for.

### 6.3 Resume phase, and the reconciliation rule

`ResumePhase` is the first phase in `PhaseCatalog.All` order whose status is not `Completed`.

Before that is computed, the journal is reconciled against disk. The rule is asymmetric and
deliberately so:

> **Disk evidence may demote a phase. It may never promote one.**

Concretely, for each phase whose completion rule is `FilesExist`:

| Phase | Required artefacts |
|---|---|
| Specification | `SpecAbsolute` **and** `PlanAbsolute` exist and are non-empty |
| Review | `ReviewAbsolute` exists and is non-empty |
| Implementation | `DoneAbsolute` exists and is non-empty |

If the journal says `Completed` but the artefacts are gone, that phase is demoted to `Pending`
**and so is every phase after it**. Phase 3 (`ResolveReview`) is never demoted by this rule —
its completion is baseline-relative and leaves no disk trace, so the journal is the only
evidence there is.

Why demotion-only: promoting from disk would resurrect the BASE §8.3 hazard inside recovery — a
folder that happens to contain a spec and a plan would be reported as "phase 1 done" for a task
that never ran. Demotion is safe because it can only ever cause *more* work to be re-run, never
less, and it prevents resuming into a phase whose inputs have been deleted.

**Consequence, and it is load-bearing:** because a phase is only `Completed` when its artefacts
are present, a resume never starts a `FilesExist` phase whose watcher is already satisfied. Resume
into phase 1 happens only when spec *or* plan is missing; into phase 2 only when the review is
missing; into phase 4 only when the done marker is missing. The §8.3 instant-completion hazard is
therefore unreachable on the recovery path, which is why §2.2 can leave it out of scope.

#### 6.3.1 The demotion must be persisted

Reconciling only the in-memory `TaskState` handed to the recovered tab is not enough, and the
reason is the *"and every phase after it"* half of the rule.

Worked example. The journal records phases 1–3 `Completed`. `T_plan.md` is deleted outside the
app. Recovery demotes phases 1–4 in memory and resumes at phase 1. Phase 1 runs and the
orchestrator writes `RecordPhase(Specification, Completed)` — a load-modify-save that touches
*only* that one entry. The on-disk journal now reads: phase 1 `Completed` (new), phases 2 and 3
`Completed` (**stale, never cleared**). Crash again, and the next startup reconciles a journal
whose phase-2 artefact (`T-review.md`) is present, finds nothing to demote, and resumes at phase 4
— silently skipping phases 2 and 3. The rule the design calls load-bearing has been lost across
one restart.

Therefore: **when reconciliation demotes anything, the reconciled array is written back
immediately**, through `ITaskStateStore.ReplacePhases` (§5.4). The write happens once, at scan
time, before the task is offered; it is idempotent (a second scan finds nothing left to demote and
writes nothing) and it leaves `UpdatedUtc` alone so a deleted artefact cannot extend the 14-day
window. Like every other journal write it is best-effort: a failure degrades recovery and never
takes down the app (§10.2 W1).

#### 6.3.2 One reconciliation, two callers

The rule is needed in two places — the startup scan (§6.2) and the re-arm path (§6.6), which
reads the same journals from a fresh tab. Implementing it twice would let the two disagree, and an
unreconciled §6.6 would resume straight into a phase whose inputs are missing, which is exactly
what §6.3 exists to prevent.

It is therefore **one shared, pure helper** over `(TaskPaths, TaskState)` —
`PhaseReconciliation.Reconcile`, returning whether anything was demoted, plus
`PhaseReconciliation.FirstIncomplete` for the resume phase — used by both callers, each of which
persists via `ReplacePhases` when the helper reports a demotion. It lives next to the models
because it is a rule about the journal and the artefacts, not about scanning.

### 6.4 Startup wiring

`MainWindowViewModel` gains an `ITaskRecoveryScanner` and an async `InitialiseAsync()`:

1. The constructor keeps its current behaviour: one blank tab (F13 — the "always ≥ 1 tab"
   invariant and the "new task" entry point are both preserved).
2. `App.OnStartup` shows the window, then fires `_ = _shell.InitialiseAsync()`.
3. `InitialiseAsync` awaits `ScanAsync`, then on the dispatcher appends one tab per
   `RecoverableTask` via `ITaskTabViewModelFactory.Create()` + `LoadForResume(...)`.
4. If at least one tab was recovered, `SelectedTab` becomes the **first recovered** tab (the most
   recently updated task), so the user sees the recovery rather than an empty form.

Recovered tabs are appended *after* the blank tab, so the blank "new task" tab stays at index 0.

The scan is deliberately **not** awaited before `window.Show()`: a slow MRU entry must never
delay the window appearing. Tabs materialise a moment later.

### 6.5 The recovered tab

`TaskTabViewModel` gains:

```csharp
public WorkflowPhase? ResumePhase { get; private set; }      // null ⇒ fresh start
public bool IsRecovered { get; private set; }                // true ⇒ came from the scanner
public string StartButtonLabel => ResumePhase is null ? "Start workflow" : "Continue workflow";
```

`StartButtonLabel` is notified whenever `ResumePhase` changes. `TaskTabView.xaml`'s existing
button binds its `TextBlock.Text` to `StartButtonLabel` instead of the literal
`"Start workflow"`. **One button, one command** — a second button plus visibility converters
would duplicate `CanStartWorkflow` and the `IsRunning` interlock for no gain.

`LoadForResume(RecoverableTask task)` prefills, in this order:

1. Set `_suppressFolderSync = true` for the whole method. Without it, assigning `TaskName` and
   `WorkingDirectory` schedules the debounced folder sync, whose `SyncFolder` would see
   `_folderOnDisk == null` with a different `TaskName` and could attempt a create — and, worse,
   a later keystroke could trigger a rename of a folder that already holds artefacts.
2. `WorkingDirectory = task.Paths.WorkingDirectory` (already normalised by `TaskPaths`). This
   still promotes the directory in the MRU, which is correct.
3. `TaskDescription = task.State.TaskDescription`.
4. `TaskName = task.Paths.TaskName`; then `_folderOnDisk = task.Paths.TaskName` so the folder and
   the field agree and no rename can ever be attempted.
5. `IsNameLocked = true`. The folder exists and holds artefacts whose names embed the task name
   (`T_spec.md`, `T_plan.md`, `T-review.md`, `T-done.md`); renaming it would orphan every path
   already written into the spec and plan. Locking removes the failure mode instead of handling
   it — the same argument BASE §8.4 makes for a running pipeline.
6. Paint the indicators: for each entry in `task.State.Phases`, `Phases[i].Status` = its
   status, except that any `Active` is downgraded to `Pending` (an `Active` in the journal means
   "the app died while this phase was running", which is exactly what is about to be re-run).
7. `ResumePhase = task.ResumePhase`; `IsRecovered = true`.
8. `_suppressFolderSync = false`, then run one validation-only pass so `ValidationMessage` is
   correct (the startup-error gate of BASE §9.4 still applies and still wins).

### 6.6 Re-arming a dismissed task

`SyncFolder()` — which already runs, debounced, whenever the name or directory changes and
already calls `_folders.DirectoryAlreadyExisted(paths)` — additionally calls
`_stateStore.TryLoad(paths)` after the folder has been resolved, then reconciles it against disk
with the shared helper of §6.3.2 and persists the result if anything was demoted. One of exactly
two outcomes follows:

| Journal | `ResumePhase` | The four indicators |
|---|---|---|
| Found, and some phase is not `Completed` | that phase | painted from the journal, `Active` shown as `Pending`, exactly as §6.5 step 6 |
| Absent, **or** every phase `Completed` | `null` | **all four reset to `Pending`** |

`IsNameLocked` is **not** set on this path — the user is still typing.

The re-read happens on **both** of `SyncFolder`'s successful exits, the rename branch included:
that branch `return`s before the create branch is reached, so a single call at the end of the
method would leave the postcondition holding on one path and not the other. On the rename path the
answer is normally unchanged — `TaskFolderService.Rename` moves the whole directory and the journal
travels with it (§5.1) — which is exactly why re-reading there is safe as well as uniform. The
postcondition worth naming is:

> After `SyncFolder` returns successfully, `ResumePhase` and the four indicators reflect the
> journal in the folder that is now on disk.

Note what follows from that: retyping only the *name* renames the folder and carries the journal
along, so the resume state legitimately survives. The reset cases are reached by changing the
*working directory*, or by typing a name into a tab that has not yet created a folder.

Both halves of the reset matter, and each has a concrete failure it prevents:

- **Absent journal.** Without the reset, a tab that a moment ago showed task A's green phase 1
  keeps showing it after the user types task B's name. The indicators would then be claiming that
  a phase of task B is finished.
- **Complete journal.** Without the reset, the button correctly flips back to *Start workflow*
  while all four indicators stay green — an about-to-start run painted as already finished.

**This path is skipped entirely when `IsRecovered` is true.** A recovered tab's resume state was
established by `LoadForResume` and must not be recomputed: `TaskTabViewModel.StartWorkflow` calls
`SyncFolder()` directly (bypassing the `IsNameLocked` guard in `ScheduleFolderSync`, which is how
R12 recreates a folder deleted between scan and *Continue*), and a recompute there would read
`ResumePhase` back out of the journal microseconds before it is passed to the orchestrator as
`StartPhase`, and would repaint indicators the orchestrator is about to drive.

This is what makes "closing dismisses it" non-destructive: typing the task's name and directory
into a fresh tab re-reads the journal, and the button reads *Continue workflow* again. It also
covers a task created before this feature shipped once it has been run once (§2.2).

It has an intended side effect worth stating: typing a name that collides with an existing
unfinished task folder flips the tab into Continue mode. That is the correct reading — the
existing `InfoMessage` already tells the user *"Ordner existiert bereits und wird
weiterverwendet."*

### 6.7 Dismissal

`MainWindowViewModel.CloseTab(tab)` calls `tab.NotifyClosedByUser()` before disposing. That
method calls `_stateStore.SetDismissed(paths, true)` when — and only when — `IsRecovered` is true
and a valid `TaskPaths` can be built.

`ShutdownAll()` does **not**: closing the application is not a statement about any task.
This distinction is the whole reason the dismissal lives in `CloseTab` rather than in `Dispose`.

---

## 7. Resuming a run

### 7.1 `WorkflowRunRequest`

```csharp
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress,
    WorkflowPhase StartPhase = WorkflowPhase.Specification);
```

A **defaulted positional parameter**: positional records support defaults, so every existing
construction site — production and `WorkflowOrchestratorTests` — keeps compiling unchanged.

### 7.2 `RunAsync`

```csharp
foreach (var definition in PhaseCatalog.All.Skip((int)request.StartPhase))
{
    await RunPhaseAsync(request, definition, cancellationToken);
}
```

`StartPhase` is validated on entry: a value outside the enum throws
`ArgumentOutOfRangeException`, consistent with the `ArgumentNullException.ThrowIfNull(request)`
already there.

Phases *before* `StartPhase` are never reported and never touched. Their green indicators come
from `LoadForResume` (§6.5 step 6), not from the orchestrator — the orchestrator reports only
what it actually runs.

### 7.3 Journal writes

`WorkflowOrchestrator`'s constructor takes an `ITaskStateStore`. In `RunPhaseAsync`, each
existing `request.Progress.Report(...)` is paired with a store write, store first:

```csharp
_state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Active);
request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Active));
…
_state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Completed);
request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
```

Store first, deliberately: `Progress.Report` marshals to the UI thread, so a busy dispatcher must
not be able to delay the durable write. The orchestrator is the right owner because it is the
only component that *knows* a phase completed, it runs regardless of UI state, and it is already
covered by `WorkflowOrchestratorTests`.

`TaskTabViewModel.StartWorkflow` writes the description before launching the run:

```csharp
_stateStore.SaveDescription(paths, TaskDescription);
```

so a crash one second after *Start workflow* still recovers a tab with its description intact.

### 7.4 A run clears its own tail first

Immediately after validating `StartPhase`, and before the loop, `RunAsync` rewrites the journal so
that `StartPhase` and every phase after it read `Pending` with no `completedUtc`
(`ITaskStateStore.ReplacePhases`, §5.4). Phases *before* `StartPhase` are left exactly as they are.

The invariant this buys is worth stating plainly:

> **The journal never claims a phase is complete that the current run has not run.**

Two reachable situations need it, and neither is covered by §6.3.1 — that rule corrects a journal
against *disk*, this one corrects it against *what is about to happen*:

1. **Re-running a finished task.** All four phases are `Completed` on disk. The user types the
   name (§6.6 correctly resets the button and the indicators), presses *Start workflow*, and the
   app dies during phase 1. Without this rewrite the journal reads phase 1 `Active`, phases 2–4
   `Completed`, and the next startup offers a recovered tab with **phases 2, 3 and 4 painted
   green** for work that has not been done — contradicting §6.5 step 6 and A6.
2. **A crash in the gap between two phases.** `RecordPhase(N, Completed)` and
   `RecordPhase(N+1, Active)` are two writes. A crash in between, with a stale `Completed` already
   sitting on phase N+1, lets the next recovery compute `ResumePhase` past it. The window is
   microseconds wide, and closing it costs one write per run.

`ReplacePhases` does not stamp `UpdatedUtc`, which is right here too: `SaveDescription` has just
stamped it (§7.3) and the first `RecordPhase` is about to stamp it again.

---

## 8. Completion detection

### 8.1 New phase table (supersedes BASE §7.1)

| Phase | DisplayName | Launcher | PromptFile | Completion | Watched paths |
|---|---|---|---|---|---|
| Specification | Spezifikation | `yo` | `initial_prompt.md` | `FilesExist` | `SpecAbsolute`, `PlanAbsolute` |
| Review | Review | `codex --yolo` | `review_prompt.md` | `FilesExist` | `ReviewAbsolute` |
| ResolveReview | Review umsetzen | `yo` | `resolve_review_prompt.md` | **`AllContentChanged`** | `SpecAbsolute`, `PlanAbsolute` |
| Implementation | Implementierung | `yo` | `implementation_prompt.md` | **`FilesExist`** | **`DoneAbsolute`** |

Changes from BASE in bold. `WorkflowOrchestrator.WatchedPaths` gains
`WorkflowPhase.Implementation => [paths.DoneAbsolute]`, replacing the `_ => []` fallthrough.

### 8.2 Phase 3 — `AllContentChanged`

New member on `CompletionRule`; one new arm in `ArtifactWatcher.CheckAndSignal`:

```csharp
CompletionRule.AllContentChanged => _paths.All(HasChangedSinceBaseline),
```

Nothing else in `ArtifactWatcher` changes — the tri-state hashing of BASE §7.5 (`Hash` /
`Missing` / `Unreadable`, where `Unreadable` never reports a change and an `Unreadable` baseline
is re-captured on first success) is exactly as correct for `All` as it is for `Any`, and is more
important here: with `All`, a transient lock on one file simply defers the signal instead of
mis-firing it.

This matches the contract `resolve_review_prompt.md` already states: *"Both {spec_path} and
{plan_path} must be written to, even if only to append a short `## Review resolution` note
recording that no change was required."*

**Accepted risk:** if the CLI ignores that instruction for one of the two files, phase 3 does not
end on its own. The *Task abschliessen* button (F10 — `Task.WhenAny(watcher, manualSignal)`)
remains the escape hatch, and BASE §12.3 already covers orchestration stalls.

### 8.3 Phase 4 — the done marker

`TaskPaths` gains, alongside the existing three artefacts:

```csharp
DoneAbsolute = Path.Combine(TaskDirectory, $"{TaskName}-done.md");
DoneRelative = $"./{TaskName}/{TaskName}-done.md";
```

Note the **hyphen**, matching `T-review.md`; `T_spec.md` and `T_plan.md` use underscores. That
inconsistency is pre-existing and is preserved rather than "fixed", because the token values are
baked into specs and plans already on disk.

`PromptVariables.KnownNames` and `PromptVariables.For` gain `done_path` →
`paths.DoneRelative`. `verify.ps1`'s `$known` array gains `'done_path'` (F16), plus a new
assertion that `implementation_prompt.md` actually contains `{done_path}`.

`implementation_prompt.md` gains a final section requiring the marker to be written **last**,
non-empty (`FilesExist` demands length > 0), and only once every completion condition already
listed in that file is genuinely met:

```markdown
## Signalling completion

When, and only when, every condition under "Completion" above is satisfied, write a short
completion report to {done_path} as the very last action of this session. The file must not be
empty: one or two sentences naming what was implemented and the result of `Workflow\verify.ps1`
is enough.

The application watches for this file. Until it exists, the workflow is considered unfinished
and will offer to resume this task the next time it starts.
```

Why a marker file rather than a sentinel string in the terminal output:

- **It is persistent.** A sentinel exists only while the app is alive. If the process dies in the
  second between Claude Code printing it and the app reacting, recovery could never learn that
  phase 4 was finished — which is precisely the failure this whole design exists to remove.
- It reuses `ArtifactWatcher` verbatim. No ANSI stripping, no rolling window over the PTY byte
  stream, no risk of the token scrolling out of the 60-line snapshot window.
- It gives §6.3's reconciliation rule something to check.

**Stale-marker guard.** Immediately before creating the watcher for `WorkflowPhase.Implementation`
— and therefore before the baseline of any rule is taken — the orchestrator deletes
`DoneAbsolute` if it exists (`IOException` / `UnauthorizedAccessException` swallowed; a marker
that cannot be deleted degrades to instant completion, which the manual button already covers).
Without this, re-running a finished task would blow through phase 4 in milliseconds.

**`CompletionRule.Manual` is removed.** After this change nothing uses it. Its removal deletes
the `if (_rule == CompletionRule.Manual) return;` early-exit in the `ArtifactWatcher` constructor
and the `_ => false` arm in `CheckAndSignal`, and simplifies `RunPhaseAsync` to a single
`Task.WhenAny(watcher.WaitAsync(…), request.ManualSignal.WaitAsync(…))` for every phase. Leaving
a dead enum member that one future reader will assume is reachable is worse than the diff.

---

## 9. The auto-submit defect

### 9.1 Symptom and diagnosis

Symptom: after each phase's prompt is pasted into `yo` / `codex`, nothing happens until the user
presses Enter.

Current code (F6), `TerminalViewModel.SendPasteAsync`: write the bracketed-paste block, wait a
fixed **150 ms**, write `\r`.

Leading hypothesis: Claude Code's TUI is Ink-based and coalesces a paste — it buffers incoming
bytes and commits them to its input box only after a short idle gap. A CR arriving 150 ms after
`ESC[201~`, while a multi-kilobyte prompt is still being drained, lands *inside* that window and
is taken as a literal newline in the pasted body rather than as submit. The user's later Enter
then submits the already-complete buffer — which is exactly the observed behaviour.

The fix below is **cause-agnostic on purpose**: it also repairs the case where the launcher is
merely slow, or where a future CLI version changes its paste handling. Nothing in it depends on
the hypothesis being right.

### 9.2 Scope: all four phases

`SendPasteAsync` is called once per phase from `RunPhaseAsync`, through one code path. There is
no phase-specific paste logic today and none is added. The fix therefore applies to
Spezifikation, Review, Review umsetzen and Implementierung identically — which answers the
"please check for the other phases as well" part of the request: their prompts were failing to
submit for the same reason, and are fixed by the same change.

### 9.3 The fix

**Move the submit dance from `TerminalViewModel` into `WorkflowOrchestrator.**

`ITerminalController` replaces

```csharp
Task SendPasteAsync(string body, CancellationToken cancellationToken);
```

with

```csharp
public void SendPaste(string body);   // writes BracketedPaste.Wrap(body). Nothing else.
```

`WorkflowOrchestrator` gains `SendPromptAsync`, placed directly after `SettleAndAnswerAsync` in
the phase sequence:

```
terminal.SendPaste(renderedPrompt)
pasteSentUtc := now                              # <-- the gate's initial baseline

# Gate: let the paste drain. The baseline starts at the paste itself, and any output
# arriving afterwards pushes it forward.
wait until (now - max(pasteSentUtc, terminal.LastOutputUtc)) >= PasteQuietPeriodMs,
     bounded by PasteSettleTimeoutMs

for attempt in 1 .. MaxSubmitAttempts:          # default 2
    before := terminal.OutputCount
    terminal.Send("\r")
    wait SubmitVerifyMs                          # default 1500
    if terminal.OutputCount != before: return    # accepted
# fell through: proceed anyway; the watcher and the manual button still apply
```

**Why the baseline must start at the paste and not at `LastOutputUtc` alone.** This is the whole
gate. `SettleAndAnswerAsync` runs immediately before, and its normal exit - the `rule is null`
branch - is reached *only after* Gate B has observed `now - LastOutputUtc >= QuietPeriodMs`
(1500 ms by default, nearly twice `pasteQuietPeriodMs`). So at the moment `SendPromptAsync` is
entered, `LastOutputUtc` is **already stale by more than the quiet period**. A gate consulting only
`LastOutputUtc` would find its very first iteration satisfied by that *pre-paste* silence and write
the CR at once - before the asynchronous PTY echo of the paste has even arrived, which is precisely
the early-submit failure this change exists to remove. Seeding the baseline at `SendPaste` makes
the gate wait for `pasteQuietPeriodMs` of genuine *post-paste* silence; later output still pushes
the baseline forward, so a slow launcher is handled by the same expression.

The cost is one `pasteQuietPeriodMs` per phase - 800 ms, four times per task. That is the price of
the guarantee and it is deliberately paid.

**Why `OutputCount` is the verification signal** rather than a snapshot diff: the CR is only sent
*after* the screen has gone quiet, so by construction nothing else is producing output at that
moment. Any new output after the CR therefore means the launcher acted on it. A snapshot
comparison would additionally have to distinguish a blinking cursor and spinner frames from real
progress, and `OutputCount` is already on the interface (F7) and already maintained by both the
production controller and `FakeTerminalController`.

A false negative costs one extra CR. Sending a stray CR into an empty Claude Code input box is a
no-op; this is the cheap direction to be wrong in, and `MaxSubmitAttempts` bounds it at two.

**Why the orchestrator and not the view model:** the orchestrator already owns the quiet-gate
logic (F8) and the timing configuration (F9). Putting the second quiet-gate next to the first
keeps one component responsible for "talk to the launcher and wait", and makes the whole sequence
testable through `FakeTerminalController` with no WebView2 in the loop.

**The session-identity guard disappears, and that is safe.** `SendPasteAsync` currently captures
`_session`, links `_sessionLifetime`, and re-checks `ReferenceEquals(_session, target)` before
writing the CR — all because of a detached delay that could deliver a CR into the *next* phase's
launcher (BASE §6.5). After this change nothing is fire-and-forget: `SendPromptAsync` is awaited
inline inside `RunPhaseAsync`, so the orchestrator cannot advance to the next phase while a
submit is pending, and the phase's `CancellationToken` covers tab close and shutdown. The hazard
BASE §6.5 describes is structurally gone rather than guarded against.

`TerminalViewModel` keeps `_sessionLifetime` — `DisposeSession` still uses it — but `SubmitDelay`
and the `SendPasteAsync` body are deleted.

### 9.4 Configuration

`Assets\autoanswer.rules.json` goes to `"version": 2` and gains four fields; `AutoAnswerRuleSet`
gains the matching positional parameters with defaults so an existing `%APPDATA%` override file
at version 1 still deserialises:

| Field | Default | Meaning |
|---|---|---|
| `pasteQuietPeriodMs` | 800 | Terminal silence after the paste block that counts as "drained". |
| `pasteSettleTimeoutMs` | 15000 | Ceiling on waiting for that silence. |
| `submitVerifyMs` | 1500 | How long to wait for output proving the CR was accepted. |
| `maxSubmitAttempts` | 2 | Total CRs written, including the first. |

**The values reach the record through `RuleSetDto`, so the DTO is what must change.**
`AutoAnswerService.Load` does not deserialise an `AutoAnswerRuleSet`; it deserialises a private
`RuleSetDto` and then *constructs* the record positionally. Positional defaults on the record are
therefore invisible to deserialisation, and any field absent from the DTO is silently dropped
however correct the record looks. All four fields must appear on `RuleSetDto` - with the shipped
defaults as their property initialisers - **and** be passed to the `AutoAnswerRuleSet` constructor.

That is also the whole migration story: the DTO's initialisers mean a `version: 1` override file
which omits the fields deserialises to the shipped defaults. No migration, no user action.

On top of that, a value of zero or less - an explicit `0`, or a typo - is normalised to the shipped
default before the set is returned, because each of the four is a duration or an attempt count
where zero is meaningless.

**A missing or malformed *shipped* rules file keeps throwing, by design.** `Load` has no fallback
path today and none is added: `File.ReadAllText` propagates, `JsonSerializer.Deserialize`
propagates `JsonException`, and a `null` document becomes `InvalidOperationException`.
`Assetsutoanswer.rules.json` is a build artefact copied into the output directory, so its
absence or corruption is a broken deployment, not a user state. The *override* file is the
tolerant one, and always was: it is consulted only when `File.Exists` says so, and a malformed
override is out of scope exactly as it is today.

---

## 10. Failure modes and edge cases

### 10.1 Recovery

| # | Situation | Behaviour |
|---|---|---|
| R1 | Journal is corrupt / truncated / hand-edited into invalid JSON. | `TryLoad` returns `null`; the task is not offered. No crash, no message. |
| R2 | Journal `Version` > `CurrentVersion` (downgrade after an upgrade). | `TryLoad` returns `null`. Refusing to guess is better than misreading a future schema. |
| R3 | `Phases` array is short, reordered, or holds an unknown phase or status name. | Normalised to the four catalogue phases in order; unmappable entries dropped individually (the rest of the journal still loads); missing ones defaulted to `Pending`. |
| R4 | An MRU directory is gone, or on a disconnected share. | Skipped; scan continues. |
| R5 | The scan exceeds 5 s - including because a worker is blocked inside `Directory.Exists` or `Directory.EnumerateDirectories` on a dead share. | `ScanAsync` returns a snapshot of what has been collected at the deadline; a still-blocked worker is abandoned, not awaited (§6.2). |
| R6 | A working directory holds thousands of subfolders. | Enumeration stops at 2 000 for that directory. |
| R7 | More than 5 interrupted tasks qualify. | The 5 most recently updated are offered. The rest keep their journals and are reachable by typing the name (§6.6). |
| R8 | `updatedUtc` lies in the future. | Treated as recent. |
| R9 | The task folder was renamed outside the app. | Works: name and directory are derived from the folder (§5.3). |
| R10 | The journal says `Completed` but the artefact was deleted. | Demoted, with every later phase (§6.3). |
| R11 | The same task is reachable from two MRU entries. | De-duplicated by full path, case-insensitively. |
| R12 | A recovered task's folder is deleted between scan and Continue. | `SyncFolder`/`EnsureCreated` recreates the folder; the resumed phase re-runs against an empty folder, and §6.3's demotion already prevented resuming past a missing artefact in the normal case. |
| R13 | The app crashes a *second* time, after a demoted phase has been re-run. | The demotion was written back at scan time (§6.3.1), so the stale `Completed` entries for the later phases are gone and the second recovery does not skip them. |
| R14 | The user types the name of a task whose journal says a phase is complete but whose artefact is missing. | Reconciled by the same rule as the scan (§6.3.2) before `ResumePhase` is set, so the re-arm path cannot resume into a phase whose inputs are gone either. |
| R15 | A tab that already shows one task's indicators is pointed at a different working directory, or at a completed task. | Both `ResumePhase` and all four indicators are reset to `null`/`Pending` (§6.6). Retyping only the *name* renames the folder and carries the journal with it, so there the resume state correctly survives. |
| R16 | The user picks a working directory while the startup scan is still running. | The scan reads a snapshot of the MRU taken on the UI thread before the worker started (§6.2), so `AddRecentDirectory` cannot fault it. |

### 10.2 Journal writes

| # | Situation | Behaviour |
|---|---|---|
| W1 | The journal cannot be written (read-only folder, lock, no permission). | Swallowed. The run continues; only recovery is degraded. Same trade-off as `SettingsService` (F12). |
| W2 | Two app instances run the same task. | Last writer wins. Not detected. Out of scope (§2.2). |
| W3 | The journal write wakes the `FileSystemWatcher`. | Harmless — one extra debounced re-check of the real rule (§5.4). |
| W4 | A hand-edited journal holds an unknown `phase` or `status` name. | That entry alone is dropped; the rest of the journal survives and is normalised (§5.2, §5.4). |
| W5 | The write-back of a reconciliation fails. | Swallowed like every other journal write. Recovery is correct for this session and degrades to the R13 behaviour if the app crashes again. |

### 10.3 Completion detection

| # | Situation | Behaviour |
|---|---|---|
| C1 | The CLI never writes `T-done.md`. | Phase 4 does not end on its own; *Task abschliessen* still ends it (F10). |
| C2 | `T-done.md` exists from a previous run. | Deleted immediately before the phase-4 watcher is created (§8.3). |
| C3 | `T-done.md` is created empty and filled later. | `FilesExist` requires length > 0, and the 750 ms debounce plus re-read already guard the create-then-write pattern (BASE §7.5). |
| C4 | Phase 3 touches only one of spec/plan. | Phase 3 does not end on its own; manual button applies (§8.2). |
| C5 | One of spec/plan is locked during phase 3. | `Unreadable` never counts as changed; retried on the next poll (BASE §7.5). |

### 10.4 Auto-submit

| # | Situation | Behaviour |
|---|---|---|
| S1 | The launcher produces output continuously (a spinner) after the paste. | The paste quiet-gate times out at `pasteSettleTimeoutMs`; the CR is sent anyway. |
| S2 | The CR is accepted but produces no output within `submitVerifyMs`. | A second CR is sent. Harmless in an empty input box. |
| S3 | Both attempts fail. | The prompt sits in the input box; the terminal is live and the user can press Enter, exactly as today. No regression. |
| S4 | A `%APPDATA%` override at `version: 1` lacks the new fields. | The `RuleSetDto` initialisers supply the shipped defaults (§9.4). |
| S5 | A field is present but set to `0` or a negative number. | Normalised to the shipped default (§9.4). |
| S6 | The PTY echo of the paste arrives only after `SendPaste` has returned. | The quiet gate's baseline starts at the paste, so the CR is not written until `pasteQuietPeriodMs` of silence has followed the echo (§9.3). |

### 10.5 Known limitation carried forward

Starting a **fresh** run (`StartPhase == Specification`) in a folder that already contains
`T_spec.md` and `T_plan.md` satisfies phase 1's watcher immediately and the pipeline chains
onward. This is BASE §8.3 and is unchanged. It is not reachable from the recovery path (§6.3).

---

## 11. Testing strategy

All tests are xUnit in `Workflow.Tests`, matching the existing files' style (temp directory per
fixture, `IDisposable` cleanup, fakes in `Workflow.Tests/Fakes`).

**New — `TaskStateStoreTests`**
- Round-trip: `SaveDescription` then `TryLoad` returns the description and four `Pending` phases.
- `RecordPhase(Completed)` sets `CompletedUtc`; `RecordPhase(Active)` clears it.
- `SaveDescription` clears `Dismissed`.
- Missing file → `null`. Invalid JSON → `null`. `Version = 99` → `null`.
- A short / reordered / unknown-phase `Phases` array normalises to four phases in catalogue order,
  and the *valid* entries around an unknown one survive (this is the test that fails if the
  tolerant read DTO of §5.2 is skipped).
- An unknown `status` name is dropped the same way, and that phase defaults to `Pending`.
- A write into a read-only directory does not throw.
- `SaveDescription` into a task directory that has been deleted **recreates** the directory and the
  journal - `Save` calls `Directory.CreateDirectory`, so the contract is "does not throw *and*
  re-creates", not "does nothing".
- `ReplacePhases` overwrites the whole array, normalises it, leaves `UpdatedUtc` unchanged, and is
  a no-op when there is no journal.
- No `.tmp` file is left behind after a successful save.

`PhaseReconciliation` (§6.3.2) has no test class of its own: it is exercised through
`TaskRecoveryScannerTests` (the scan path) and `TaskTabViewModelTests` (the re-arm path), which is
where its two callers live and where a divergence between them would actually bite.

**New — `TaskRecoveryScannerTests`**
- Finds a task; ignores subfolders with no journal.
- Excludes dismissed, excludes all-complete.
- Excludes a journal older than the age window; includes one dated in the future.
- Caps at 5 and orders by `UpdatedUtc` descending.
- De-duplicates a task reachable from two MRU entries.
- A non-existent MRU entry does not throw.
- Demotion: journal says phase 1 `Completed` but `T_plan.md` is missing ⇒ `ResumePhase ==
  Specification` and phases 2–4 are `Pending`.
- No demotion for phase 3 when spec and plan are present.
- **The demotion is persisted (§6.3.1).** Reading the journal back off disk straight after the scan
  shows the demoted phases as `Pending`, and `UpdatedUtc` is unchanged.
- **The two-crash regression.** Journal says phases 1–3 `Completed` with `T-review.md` present;
  delete `T_plan.md`; scan (⇒ resume at phase 1); simulate the resumed phase 1 finishing
  (`RecordPhase(Specification, Completed)` plus the spec and plan on disk); scan again. The second
  `ResumePhase` must be `Review`, **not** `Implementation`. This test fails against an
  in-memory-only reconciliation and is the reason §6.3.1 exists.
- A scan whose budget has already expired, and a scan given an already-cancelled token, each
  return a list without throwing (the §6.2 partial-result contract).
- The MRU is snapshotted: mutating `Settings.RecentDirectories` while the returned task is being
  awaited does not fault the scan.

**Changed — `ArtifactWatcherTests`**
- `AllContentChanged`: changing one of two paths does **not** signal; changing both does.
- `AllContentChanged` with an unreadable second file does not signal.

**Changed — `WorkflowOrchestratorTests`**
- `StartPhase = ResolveReview` runs exactly phases 3 and 4 — assert on the `PhaseProgress`
  sequence and on `FakeTerminalController.StartedSessions.Count`.
- `ITaskStateStore` receives `Active`/`Completed` for each executed phase, in order, and nothing
  for skipped phases (fake store recording calls).
- **The tail is cleared first (§7.4).** With every phase `Completed` in a seeded journal and
  `StartPhase = ResolveReview`, phases 1–2 keep their `Completed` entries while phases 3–4 are
  `Pending`/`Active` — phase 4 must not still read `Completed`.
- A pre-existing `T-done.md` is deleted before phase 4 waits.
- Submit: with `FakeTerminalController` emitting no output after the CR, exactly
  `maxSubmitAttempts` CRs are written; with output emitted after the first CR, exactly one is.
- **The paste baseline (§9.3).** With the fake emitting its paste echo only *after* `SendPaste`
  has returned, no CR may be written before that echo has arrived and `pasteQuietPeriodMs` has
  elapsed since it. This is the test that fails if the gate reads `LastOutputUtc` alone.
- `SendPaste` is called once per phase, before the CR.

**Changed — `TaskTabViewModelTests`**
- `LoadForResume` prefills name, description, directory; locks the name; paints indicators;
  downgrades a journal `Active` to `Pending`; flips `StartButtonLabel` to `"Continue workflow"`.
- `LoadForResume` performs no folder rename (assert against a fake `ITaskFolderService`).
- `StartWorkflow` on a recovered tab passes `StartPhase == ResumePhase`.
- `StartWorkflow` calls `SaveDescription` before the run.
- Typing an existing unfinished task's name into a fresh tab sets `ResumePhase` (§6.6).
- Pointing that tab at a **different working directory** where the name has no journal resets
  `ResumePhase` to `null` **and all four indicators to `Pending`** (§6.6, R15). (Retyping only the
  name renames the folder and carries its journal along, so that case correctly keeps the state.)
- Retyping it to a **completed** task's name resets both the same way, even though every journal
  entry says `Completed`.
- Typing the name of a task whose journal says phase 1 `Completed` while `T_plan.md` is missing
  yields `ResumePhase == Specification` - the re-arm path reconciles too (§6.3.2, R14).
- `NotifyClosedByUser` dismisses only when `IsRecovered`.

**Changed — `MainWindowViewModelTests`**
- `InitialiseAsync` appends one tab per scanner result and selects the first recovered tab.
- `CloseTab` dismisses a recovered tab; `ShutdownAll` dismisses nothing.

**Changed — `AutoAnswerServiceTests`**
- A version-1 file that omits the four submit fields yields the shipped defaults (this is the test
  that fails if the fields are added to `AutoAnswerRuleSet` but not to `RuleSetDto`, §9.4).
- A version-2 file that sets them yields exactly those values.
- A field set to `0` falls back to the shipped default.

**Changed — `PhaseCatalogTests`, `TaskPathsTests`, `PromptTemplateServiceTests`, `PlaceholderTests`**
- The new completion rules; `DoneAbsolute`/`DoneRelative`; the `done_path` token; and (existing
  convention) that every prompt template's tokens are all known.

**Changed — `verify.ps1`**
- `'done_path'` added to `$known`.
- New assertion: `implementation_prompt.md` contains `{done_path}`.

---

## 12. Acceptance criteria

### 12.1 Build and static analysis

- **A1** `dotnet build Workflow.sln -c Release` exits 0 with **zero** warnings (warnings are
  errors; `Directory.Build.props` + `Workflow/.roslyn`).
- **A2** No new entry is added to the repository-wide `NoWarn` list. Any required suppression is
  a local `#pragma` with a justification comment, per BASE §4.4.
- **A3** `dotnet test Workflow.sln -c Release` exits 0.
- **A4** `Workflow\verify.ps1` exits 0.

### 12.2 Functional

- **A5** Running a task to the end writes `W\T\.workflow-state.json` with all four phases
  `Completed` and a `completedUtc` on each.
- **A6** Killing `Workflow.exe` during phase 2 and restarting it opens a tab titled `T` with the
  Taskbeschreibung restored, phase 1 green, phases 2–4 grey, and the button reading
  **Continue workflow**.
- **A7** Clicking *Continue workflow* in A6 starts a session, re-sends the **phase 2** prompt, and
  does not re-run phase 1.
- **A8** Phase 4 turns green on its own once `T-done.md` exists and is non-empty, with no click.
- **A9** Phase 3 does **not** advance when only `T_spec.md` changes; it advances when both
  `T_spec.md` and `T_plan.md` have changed since phase start.
- **A10** In every phase, the pasted prompt is submitted without the user pressing Enter.
- **A11** Closing a recovered tab and restarting the app does not re-offer that task; typing its
  name and directory into a fresh tab restores the **Continue workflow** button.
- **A12** A completed task is never offered for recovery.
- **A13** Startup with no interrupted tasks behaves exactly as today: one blank tab, selected.
- **A14** A demotion survives a second crash: with phases 1–3 recorded `Completed` and `T_plan.md`
  deleted, recovery resumes at phase 1; once phase 1 has completed again, a further restart resumes
  at phase **2**, not phase 4 (§6.3.1, R13).
- **A15** Pointing a tab at a working directory where the task name has no journal, or typing a
  completed task's name into a fresh tab, resets both the button caption *and* all four indicators
  (§6.6, R15).
- **A16** Typing the name of a task whose journal claims a phase is complete while its artefact is
  missing yields the same resume phase the startup scan would (§6.3.2, R14).
- **A17** A `version: 1` `%APPDATA%` override file that omits the four submit fields produces the
  shipped defaults, and a `version: 2` file that sets them produces those values (§9.4).
- **A18** Re-running a task whose journal is fully `Completed` and killing the app during phase 1
  recovers a tab with **all four** indicators grey - the journal never claims a phase the current
  run has not run (§7.4).

### 12.3 Verification steps

**Automated** by `verify.ps1` / `dotnet test`: A1–A5, A9, A11–A17, plus every test listed in §11.
A6–A8 and A10 are automated only at view-model / fake-terminal level (there is no real CLI in the
test run); their end-to-end confirmation is V13, V15 and V16.

**Explicitly not automatable, and why.** The §6.2 deadline is verified by the two contract tests
(expired budget, cancelled token) rather than by a genuinely blocked worker: blocking
`Directory.*` behaviour on a dead SMB share cannot be produced from a unit test without a
file-system abstraction, which this design does not introduce. The remaining evidence for that
claim is **V18**, and the claim being made is narrow: *`ScanAsync` returns within the deadline*,
not *the worker thread has stopped*.

Manual, against the real CLIs (extends BASE §15.3's V5–V11):

- **V12** Run a task to the end in a scratch directory. Inspect `.workflow-state.json`: four
  `Completed` phases, plausible timestamps, the Taskbeschreibung verbatim.
- **V13** Start a task; once phase 2 is yellow, kill `Workflow.exe` from Task Manager. Restart.
  A prefilled tab appears with *Continue workflow*. Click it: the phase-2 prompt is re-sent and
  phase 1 is not re-run.
- **V14** During phase 3, touch only `T_spec.md`. The phase must stay yellow. Then touch
  `T_plan.md`: it must go green.
- **V15** Let phase 4 run to the point where Claude Code writes `T-done.md`. Phase 4 must turn
  green without pressing *Task abschliessen*.
- **V16** Watch all four phases: each prompt must submit itself. Confirm in the terminal that at
  most two carriage returns were needed.
- **V17** Delete `T_plan.md` from a task whose journal says phase 1 is complete, then restart.
  The recovered tab must show phase 1 grey and resume at phase 1.
- **V18** Put a working directory on a disconnected network share into the MRU. The window must
  still appear promptly, the app must stay usable, and the recovered tabs for the *reachable* MRU
  entries must still appear - within roughly the 5-second deadline, not after the share's SMB
  timeout.
- **V19** With a recovered tab open, pick a different working directory in the still-blank tab
  while the scan is running. Nothing may fault and no error label may appear (R16). Then, in a
  tab showing an unfinished task's green phase 1, switch the working directory to one where that
  name has no journal: all four indicators must go grey (A15).
- **V20** Type the name of a finished task into a fresh tab. All four indicators must read grey and
  the button must read *Start workflow* (A15).
- **V21** Re-run that finished task and kill `Workflow.exe` during phase 1. The recovered tab must
  show all four indicators grey, not phases 2-4 green (A18).

---

## 13. Decision log

| # | Decision | Rejected alternative | Why |
|---|---|---|---|
| D1 | Journal file inside the task folder. | Central registry in `%APPDATA%`. | The registry goes stale the moment a folder is renamed or deleted outside the app; the journal travels with the folder. |
| D2 | A journal at all. | Derive phase state from artefacts on disk. | Phase 3's completion is baseline-relative and leaves no disk trace, and the Taskbeschreibung is nowhere on disk (F3). Derivation cannot work. |
| D3 | The orchestrator writes the journal. | `TaskTabViewModel.ApplyProgress` writes it (F14). | Ties durability to the UI layer and is uncoverable by `WorkflowOrchestratorTests`. |
| D4 | Task name and working directory are derived, not stored. | Store them. | A second source of truth that breaks on rename/move. |
| D5 | Disk evidence may demote a phase, never promote it. | Trust disk for phases 1/2/4. | Promotion resurrects the BASE §8.3 hazard inside recovery; demotion can only cause more work, never less. |
| D6 | A prefilled tab per recovered task, newest 5, 14-day window. | A recovery dialog with checkboxes; or unlimited tabs. | The dialog adds a window and a view model for a choice the user rarely needs; unlimited tabs means a wall of tabs on every launch. |
| D7 | Closing a recovered tab sets `dismissed`, re-armable by typing the name. | Never dismiss; or ask on close. | Never dismissing nags for two weeks; a modal on an ordinary close is disproportionate. Re-arming keeps it non-destructive. |
| D8 | Phase 4 detected by a marker file. | A sentinel string in the terminal output. | A sentinel is ephemeral — a crash immediately after it is printed leaves recovery unable to know phase 4 finished, defeating the feature. The file also reuses `ArtifactWatcher` unchanged. |
| D9 | Delete a stale `T-done.md` before arming the phase-4 watcher. | Live with it. | Re-running a finished task would otherwise complete phase 4 in milliseconds. |
| D10 | Remove `CompletionRule.Manual`. | Keep it. | Dead after D8; a future reader would assume it is reachable. |
| D11 | Phase 3 requires **both** files. | Keep `Any`. | Matches the user's requirement and the prompt's own stated contract; removes the advance-while-the-second-file-is-still-being-written window. |
| D12 | The submit dance moves into the orchestrator. | Fix the delay in `TerminalViewModel`. | The orchestrator already owns the quiet-gate and the timing config; keeping both gates together makes the sequence testable without WebView2 and removes the session-identity guard entirely. |
| D13 | `OutputCount` verifies submission. | Snapshot diff before/after the CR. | The CR is sent only when the screen is quiet, so any output means acceptance. A snapshot diff must distinguish spinner frames from progress, for no extra information. |
| D14 | Timing tunables live in `autoanswer.rules.json`. | A new config file; or constants. | That file is already the terminal-timing config (F9); a second file would split one concern in two. |
| D15 | One button whose label changes. | A separate "Continue workflow" button. | A second button duplicates `CanStartWorkflow` and the `IsRunning` interlock. |
| D16 | The startup scan is not awaited before the window shows. | Scan synchronously at startup. | A slow or disconnected MRU entry would delay the window. |
| D17 | A reconciled demotion is written back to the journal (§6.3.1). | Reconcile in memory only. | `RecordPhase` is per-phase, so a resumed run leaves the demoted *later* phases marked `Completed` on disk. One more crash and recovery skips them - losing exactly the "and every phase after it" rule the demotion exists to enforce. |
| D18 | The reconciliation is one shared helper used by the scan and by the re-arm (§6.3.2). | Implement it in the scanner only. | The re-arm path (§6.6) reads the same journals from a fresh tab. Unreconciled, it resumes into a phase whose inputs are missing, contradicting §6.3's load-bearing consequence. |
| D19 | The journal is *read* through a tolerant DTO, not directly into `TaskState` (§5.2). | Deserialise `TaskState` with `JsonStringEnumConverter`. | That converter throws on an unmappable enum string, so one hand-edited phase name would make `TryLoad` return `null` and hide the whole task. R3 requires dropping the bad entry, not the journal. |
| D20 | The paste quiet-gate's baseline starts at `SendPaste` (§9.3). | Read `LastOutputUtc` alone. | The preceding settle loop exits *because* the screen has been quiet for `quietPeriodMs`, so a `LastOutputUtc`-only gate is already satisfied on entry and fires the CR before the paste echo - reproducing the very bug being fixed. |
| D21 | The five-second scan ceiling is enforced on the awaiting side (§6.2). | Rely on the cancellation token inside the worker. | `Directory.Exists` and the first `MoveNext` of `EnumerateDirectories` block inside Win32/SMB and cannot observe a token, so the token alone makes the ceiling a claim rather than a guarantee. |
| D22 | The MRU is snapshotted on the UI thread before the worker starts (§6.2). | Enumerate `Settings.RecentDirectories` on the worker. | The blank tab is visible and usable during the scan, and `AddRecentDirectory` mutates that same `Collection<string>` from the UI thread - a directory pick mid-scan would fault the scan with `InvalidOperationException`. |
| D23 | The four submit tunables are added to `RuleSetDto` as well as to `AutoAnswerRuleSet` (§9.4). | Add them to the record and rely on its positional defaults. | `Load` constructs the record from the DTO; a field absent from the DTO can never be read from the file at all. |
| D24 | A run clears the journal from `StartPhase` onward before it begins (§7.4). | Let `RecordPhase` overwrite entries as the run reaches them. | `RecordPhase` writes one entry at a time, so re-running a finished task leaves later phases marked `Completed` until the run gets to them - a crash in phase 1 then recovers a tab with phases 2-4 painted green. |
