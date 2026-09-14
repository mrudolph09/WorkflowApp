---
description: "Canonical Superpowers design specification for workflow-level run tracking, crash recovery ('Continue workflow'), phase-4 completion detection and the prompt auto-submit fix in the Workflow WPF app"
summary: "Adds a per-task journal file W\\T\\.workflow-state.json (atomic write, stores taskDescription + per-phase status + dismissed; deliberately NOT taskName/workingDirectory, which are derived from the folder and its parent). WorkflowOrchestrator becomes the single journal writer and gains a StartPhase so a run can resume mid-pipeline. At startup TaskRecoveryScanner walks RecentDirectories one level deep, offers at most 5 non-dismissed, incomplete tasks newer than 14 days as prefilled tabs whose button reads 'Continue workflow'; closing such a tab sets dismissed. Journal is authoritative for phase 3 only - missing FilesExist artefacts DEMOTE a phase (and everything after it), never promote. Phase 4 stops being CompletionRule.Manual: implementation_prompt.md must write a non-empty {done_path} = W\\T\\T-done.md as its final action, detected by the existing ArtifactWatcher; the orchestrator deletes a stale marker before arming that watcher, and CompletionRule.Manual is removed as dead. Phase 3 becomes CompletionRule.AllContentChanged (BOTH spec and plan), matching resolve_review_prompt.md. Root cause of 'the prompt does not auto-submit': TerminalViewModel.SendPasteAsync waits a fixed 150 ms before the CR, which Ink coalesces into the paste; fix moves the submit dance into WorkflowOrchestrator behind a quiet-gate with an OutputCount-based verify and one retry, tunables in autoanswer.rules.json (version 2). Out of scope, unchanged: the pre-existing §8.3 hazard where a fresh run in a fully populated folder satisfies phase 1 instantly."
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
   App.OnStartup ───────▶│   TaskRecoveryScanner    │ reads RecentDirectories,
                         │  (new, ITaskRecovery-    │ one level deep, returns
                         │      Scanner)            │ ≤5 RecoverableTask
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
| `Workflow/Services/ITaskStateStore.cs` | Journal read/write contract. |
| `Workflow/Services/TaskStateStore.cs` | Implementation. |
| `Workflow/Services/ITaskRecoveryScanner.cs` | Startup scan contract. |
| `Workflow/Services/TaskRecoveryScanner.cs` | Implementation. |

**Changed files**

`Workflow/Models/TaskPaths.cs`, `Workflow/Models/PhaseCatalog.cs`,
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
    public void SetDismissed(TaskPaths paths, bool dismissed);
}
```

Every mutating method is load-modify-save internally and stamps `UpdatedUtc`. Four narrow,
intention-revealing methods rather than a raw `Save(TaskState)` — the callers never need to
assemble a whole `TaskState`, and keeping assembly inside the store means `Phases` is always the
complete four-element array in catalogue order.

`public` on every interface member: F17 — `IDE0040` is an error here (BASE §4.4.1), and every
existing interface in `Workflow/Services` is written that way.

**Behaviour contract**

| Method | Contract |
|---|---|
| `TryLoad` | Returns `null` for: file absent, unreadable (`IOException` / `UnauthorizedAccessException`), invalid JSON (`JsonException`), or `Version` greater than `CurrentVersion`. Never throws. A `Phases` array that is missing, short, out of order or holds unknown phases is normalised to the full four-element catalogue order, unknown entries dropped, missing ones defaulted to `Pending`. |
| `SaveDescription` | Creates the journal if absent (`CreatedUtc` = now, all four phases `Pending`); otherwise overwrites only `TaskDescription`. Also clears `Dismissed` — the user has explicitly started/continued this task. |
| `RecordPhase` | Creates the journal if absent. Sets that phase's `Status`; sets `CompletedUtc` when and only when `status == PhaseStatus.Completed`, clears it otherwise. |
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

1. For each entry of `ISettingsService.Settings.RecentDirectories`, in order:
   - Skip if `!Directory.Exists(entry)`.
   - Enumerate its **immediate** subdirectories (`Directory.EnumerateDirectories`,
     `SearchOption.TopDirectoryOnly`), stopping after **2 000** entries for that directory.
   - For each subdirectory, build `new TaskPaths(entry, folderName)` and `TryLoad` it. A `null`
     result means "not one of ours" — skip silently.
2. De-duplicate by `TaskPaths.TaskDirectory`, `StringComparison.OrdinalIgnoreCase`.
3. Drop any task where `State.Dismissed` is true.
4. Compute `ResumePhase` (§6.3). Drop any task where every phase is `Completed`.
5. Drop any task where `UpdatedUtc < DateTimeOffset.UtcNow - 14 days`. An `UpdatedUtc` in the
   future (clock skew, or a file copied from another machine) is treated as recent, not dropped.
6. Order by `UpdatedUtc` descending, `Take(5)`.

**Bounding.** The whole scan runs off the UI thread and is cancelled after **5 seconds**; on
cancellation whatever has been collected so far is returned rather than nothing. Together with
the 2 000-subdirectory cap this makes a slow or enormous MRU entry — a network share, a OneDrive
folder, a repository root with thousands of directories — an inconvenience rather than a hang.
The same class of failure is already acknowledged for `FileSystemWatcher` in BASE §7.5.

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
`_stateStore.TryLoad(paths)` after the folder has been resolved. If a journal is found that is
not fully complete, `ResumePhase` and the indicators are set from it exactly as in §6.5 steps 6–7
(but **not** `IsNameLocked`: the user is still typing). If no journal is found, or it is complete,
`ResumePhase` is reset to `null` and the indicators to `Pending`.

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

# Gate: let the paste drain. Same two gates as SettleAndAnswerAsync.
wait until (now - terminal.LastOutputUtc) >= PasteQuietPeriodMs,
     bounded by PasteSettleTimeoutMs

for attempt in 1 .. MaxSubmitAttempts:          # default 2
    before := terminal.OutputCount
    terminal.Send("\r")
    wait SubmitVerifyMs                          # default 1500
    if terminal.OutputCount != before: return    # accepted
# fell through: proceed anyway; the watcher and the manual button still apply
```

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

`AutoAnswerService` already tolerates an override file that omits fields (`PropertyNameCaseInsensitive`,
missing members left at their default). A `version: 1` override therefore picks up the new
defaults; no migration, no user action.

---

## 10. Failure modes and edge cases

### 10.1 Recovery

| # | Situation | Behaviour |
|---|---|---|
| R1 | Journal is corrupt / truncated / hand-edited into invalid JSON. | `TryLoad` returns `null`; the task is not offered. No crash, no message. |
| R2 | Journal `Version` > `CurrentVersion` (downgrade after an upgrade). | `TryLoad` returns `null`. Refusing to guess is better than misreading a future schema. |
| R3 | `Phases` array is short, reordered, or holds an unknown phase name. | Normalised to the four catalogue phases in order; unknown dropped; missing defaulted to `Pending`. |
| R4 | An MRU directory is gone, or on a disconnected share. | Skipped; scan continues. |
| R5 | The scan exceeds 5 s. | Cancelled; partial results are used. |
| R6 | A working directory holds thousands of subfolders. | Enumeration stops at 2 000 for that directory. |
| R7 | More than 5 interrupted tasks qualify. | The 5 most recently updated are offered. The rest keep their journals and are reachable by typing the name (§6.6). |
| R8 | `updatedUtc` lies in the future. | Treated as recent. |
| R9 | The task folder was renamed outside the app. | Works: name and directory are derived from the folder (§5.3). |
| R10 | The journal says `Completed` but the artefact was deleted. | Demoted, with every later phase (§6.3). |
| R11 | The same task is reachable from two MRU entries. | De-duplicated by full path, case-insensitively. |
| R12 | A recovered task's folder is deleted between scan and Continue. | `SyncFolder`/`EnsureCreated` recreates the folder; the resumed phase re-runs against an empty folder, and §6.3's demotion already prevented resuming past a missing artefact in the normal case. |

### 10.2 Journal writes

| # | Situation | Behaviour |
|---|---|---|
| W1 | The journal cannot be written (read-only folder, lock, no permission). | Swallowed. The run continues; only recovery is degraded. Same trade-off as `SettingsService` (F12). |
| W2 | Two app instances run the same task. | Last writer wins. Not detected. Out of scope (§2.2). |
| W3 | The journal write wakes the `FileSystemWatcher`. | Harmless — one extra debounced re-check of the real rule (§5.4). |

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
| S4 | A `%APPDATA%` override at `version: 1` lacks the new fields. | Defaults apply (§9.4). |

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
- A short / reordered / unknown-phase `Phases` array normalises to four phases in catalogue order.
- A write into a read-only directory does not throw.
- No `.tmp` file is left behind after a successful save.

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

**Changed — `ArtifactWatcherTests`**
- `AllContentChanged`: changing one of two paths does **not** signal; changing both does.
- `AllContentChanged` with an unreadable second file does not signal.

**Changed — `WorkflowOrchestratorTests`**
- `StartPhase = ResolveReview` runs exactly phases 3 and 4 — assert on the `PhaseProgress`
  sequence and on `FakeTerminalController.StartedSessions.Count`.
- `ITaskStateStore` receives `Active`/`Completed` for each executed phase, in order, and nothing
  for skipped phases (fake store recording calls).
- A pre-existing `T-done.md` is deleted before phase 4 waits.
- Submit: with `FakeTerminalController` emitting no output after the CR, exactly
  `maxSubmitAttempts` CRs are written; with output emitted after the first CR, exactly one is.
- `SendPaste` is called once per phase, before the CR.

**Changed — `TaskTabViewModelTests`**
- `LoadForResume` prefills name, description, directory; locks the name; paints indicators;
  downgrades a journal `Active` to `Pending`; flips `StartButtonLabel` to `"Continue workflow"`.
- `LoadForResume` performs no folder rename (assert against a fake `ITaskFolderService`).
- `StartWorkflow` on a recovered tab passes `StartPhase == ResumePhase`.
- `StartWorkflow` calls `SaveDescription` before the run.
- Typing an existing unfinished task's name into a fresh tab sets `ResumePhase` (§6.6).
- `NotifyClosedByUser` dismisses only when `IsRecovered`.

**Changed — `MainWindowViewModelTests`**
- `InitialiseAsync` appends one tab per scanner result and selects the first recovered tab.
- `CloseTab` dismisses a recovered tab; `ShutdownAll` dismisses nothing.

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

### 12.3 Verification steps

Automated by `verify.ps1` / `dotnet test`: A1–A5, A9, A11–A13 (view-model level), plus every
test listed in §11.

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
  still appear promptly and the app must be usable.

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
