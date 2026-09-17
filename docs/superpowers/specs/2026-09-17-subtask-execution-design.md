---
description: "Canonical Superpowers design specification for the Subtask execution mode of the Workflow WPF app (phase 4 'Implementierung')"
summary: "Adds an opt-in 'Subtasks' mode to phase 4. When the checkbox next to the Implementierung indicator is ticked, phase 4 stops running implementation_prompt.md and instead (a) runs create_subtasks.md once in a fresh `yo` session to decompose spec+plan into subtask folders inside a separate Workflows tracking repo, then (b) loops run_subtask.md over each subtask in a fresh session, one at a time, in the order given by an ordered list in {workflow_path}/{TaskName}/result.json. Handshake is two files per step: status.json (payload, written first) then a NON-EMPTY result.json (flag, written .tmp+rename). Disk is the only truth - there is no current-subtask index in the journal; recovery re-reads result.json plus every status.json and continues at the first non-complete subtask. Failed subtasks are skipped and counted; Implementation is journalled Completed only when Failed==0, so 'Continue workflow' re-attempts exactly the failures. A second indicator shows '12 von 24' plus a red 'N fehlgeschlagen'. PhaseStatus stays three-valued and the journal schema stays at version 1 (additive fields SubtasksEnabled/WorkflowDirectory). The workflow repo path is persisted globally in settings.json AND copied into the task journal; it is validated by the presence of task_template. The 'Phase abschliessen' button, CompleteCurrentPhaseCommand and PhaseIndicatorViewModel.IsActive are REMOVED - phases now end only on flag files. 'Task abschliessen' stays and additionally closes the tab. No timeouts anywhere. Section 14 carries the exact required corrections to Workflow/Prompt/create_subtasks.md and run_subtask.md (results.json vs result.json, flag at repo root, {tasktitel} vs {taskbezeichnung}, subtask_path absolute-vs-relative, missing status.json in task_template); those files are NOT changed by this design session and the plan carries them as separately marked tasks."
paths:
  - "./specification.md"
  - "./2026-09-14-workflow-resume-design.md"
  - "../plans/2026-09-17-subtask-execution-plan.md"
  - "../../../Workflow/Prompt/create_subtasks.md"
  - "../../../Workflow/Prompt/run_subtask.md"
  - "../../../Workflow/Prompt/implementation_prompt.md"
  - "../../../Workflow/Services/WorkflowOrchestrator.cs"
  - "../../../Workflow/Models/PhaseReconciliation.cs"
  - "../../../Workflow/ViewModels/TaskTabViewModel.cs"
---

# Workflow — Subtask Execution Mode — Design Specification

**Status:** Approved for planning
**Date:** 2026-09-17
**Repository:** `C:\Users\Marco\Documents\repo\Workflow` (branch `master`)
**Tracking repository:** `C:\Users\Marco\Documents\repo\Workflows` (`https://github.com/mrudolph09/Workflows.git`)
**Base specifications:** `docs/superpowers/specs/specification.md` (BASE), `docs/superpowers/specs/2026-09-14-workflow-resume-design.md` (RESUME)

This document is self-contained. It does not rely on any conversational context. Where it says
"unchanged" it means the behaviour documented in BASE or RESUME continues to apply verbatim.

---

## 1. Purpose

Phase 4 (*Implementierung*) currently feeds one prompt (`implementation_prompt.md`) into one
Claude Code session and waits for `{TaskName}-done.md`. For a task whose implementation plan is
larger than a single agent's context window, that session runs out of context and produces
partial, unverifiable work.

This design adds an **opt-in Subtask mode** to phase 4. When enabled, phase 4 becomes two steps:

1. **Zerlegung (decomposition).** One fresh Claude session reads the approved spec and plan and
   writes a folder per subtask into a *separate tracking repository*, plus an ordered index.
2. **Schleife (loop).** For each subtask, in the order given by the index, the application starts
   a fresh Claude session, feeds it that subtask's description, and waits for that subtask's flag
   file — one subtask per session, so no session ever has to hold the whole plan.

The UI gains a checkbox next to the *Implementierung* indicator and a second indicator showing
`12 von 24` plus, in red, how many subtasks failed.

### 1.1 Why a separate tracking repository

The subtask artefacts (`subtask.md`, `status.json`, `task_plan.md`, `findings.md`, `progress.md`)
are *process* state, not product code. They live in the Workflows repository so that the code
repository's history stays free of them, and so a task's execution state can be reviewed and
shared independently of the code branch. This is the pre-existing purpose of
`https://github.com/mrudolph09/Workflows.git`; this design is the first consumer of it.

---

## 2. Scope

### 2.1 In scope

- A `Subtasks` checkbox and its tooltip, next to the *Implementierung* phase indicator.
- A `Workflow-Verzeichnis` picker row, shown only while the checkbox is ticked, with its own MRU.
- Persistence of the workflow directory: globally in `settings.json`, and per task in the journal.
- A new phase-4 execution path in `WorkflowOrchestrator`: decomposition session, then the subtask
  loop, with per-subtask fresh terminal sessions.
- New on-disk contracts: the task-level `result.json` (ordered index + flag) and the per-subtask
  `status.json` (payload) / `result.json` (flag).
- A second UI indicator showing progress and failures.
- Crash recovery and journal reconciliation for the new mode.
- Startup validation of the two new prompt templates.
- **Removal** of the `Phase abschliessen` button and its command (section 12).
- **Extension** of `Task abschliessen` so it also closes the tab (section 12).
- The *exact required text changes* to `Workflow/Prompt/create_subtasks.md` and
  `Workflow/Prompt/run_subtask.md`, and to `task_template/` in the Workflows repository
  (section 14 and section 15). **These files are not modified by the design session**; the
  implementation plan carries them as separately marked tasks.

### 2.2 Out of scope (YAGNI)

- Parallel execution of subtasks. The order is declared binding by the requirement; a sequential
  loop is the only thing that honours it, and it keeps exactly one live PTY per tab as today.
- Automatic retry of a failed subtask inside one run. A failed subtask is skipped and counted;
  re-attempting it is an explicit user action (*Continue workflow*).
- Any timeout. If a flag file never appears the phase waits, and the live terminal stays usable —
  this is the deliberate replacement for the removed `Phase abschliessen` button.
- Nested subtasks (a subtask spawning subtasks).
- Reading, writing or interpreting `verification.json`, `task.json`, `spec.md`, `plan.md`,
  `review.md`, `task_plan.md`, `findings.md` or `progress.md` in the Workflows repository. The
  application only ever reads `result.json`, `status.json` and `subtask.md`. Everything else in
  that repository is written and read by Claude alone.
- Any git operation against the Workflows repository (clone, pull, commit, push). The user
  manages that repository by hand; the application only reads and writes files inside it.
- A UI for editing, reordering or deleting subtasks.
- Migration of existing tasks. Subtask mode is off by default and a journal written before this
  change reads as "off" (section 8.3).

---

## 3. Verified codebase facts this design rests on

Every statement below was read from the working tree on 2026-09-17, not assumed.

| # | Fact | Location |
|---|------|----------|
| F1 | `PhaseCatalog.All` is a fixed four-element list; `PhaseDefinition` carries `Launcher`, `PromptFile` and `CompletionRule`. Phase 4 is `(Implementation, "Implementierung", "yo", "implementation_prompt.md", FilesExist)`. | `Workflow/Models/PhaseCatalog.cs` |
| F2 | `WorkflowOrchestrator.RunPhaseAsync` does exactly: record Active → report → `ManualSignal.Reset()` → (phase 4 only) delete stale done marker → create watcher → `ClearScreen` → `StartSession` → `WaitUntilReadyAsync` → `cd "…"` → launcher → `SettleAndAnswerAsync` → `Render` → `SendPromptAsync` → `await WhenAny(watcher, manualSignal)` → record Completed → report. | `Workflow/Services/WorkflowOrchestrator.cs:90-140` |
| F3 | `ITerminalController.StartSession` is documented as "Disposes any previous session and starts a fresh one", so each phase already gets a fresh PTY without an explicit `DisposeSession` in between. `DisposeSession` is called once, in `RunAsync`'s `finally`. | `Workflow/Services/ITerminalController.cs:29`, `WorkflowOrchestrator.cs:69` |
| F4 | `ArtifactWatcher` with `CompletionRule.FilesExist` requires every watched path to satisfy `info.Exists && info.Length > 0` — **a zero-byte file never satisfies it**. It calls `Directory.CreateDirectory(directory)` on the watched directory, watches non-recursively for `Changed`/`Created`/`Renamed`, debounces, and additionally polls, because `FileSystemWatcher` drops events on OneDrive/network paths. | `Workflow/Services/ArtifactWatcher.cs:47-62,158-171` |
| F5 | `PromptTemplateService.Render` throws `PromptTemplateException` if **any** `{token}` in the file is absent from the supplied dictionary. `ValidateAll` today iterates `PhaseCatalog.All` only, so `create_subtasks.md` and `run_subtask.md` are currently unvalidated at startup. | `Workflow/Services/PromptTemplateService.cs:66-119` |
| F6 | The token regex is `\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}`. A JSON example such as `{\n  "subtask": "ST-002"\n}` does **not** match it (a newline or a quote follows the brace), so the JSON block inside `create_subtasks.md` needs no escaping. | `Workflow/Services/PromptTemplateService.cs:61` |
| F7 | `TaskState` is serialised camelCase with string enums; `TaskStateStore.TryLoad` returns `null` when `dto.Version > TaskState.CurrentVersion`, and tolerates unknown JSON properties. `CurrentVersion` is 1. | `Workflow/Models/TaskState.cs:30`, `Workflow/Services/TaskStateStore.cs:44-66` |
| F8 | `PhaseReconciliation.ArtefactsPresent` demotes a `Completed` Implementation phase when `{TaskName}-done.md` is absent or empty. It takes `TaskPaths` and `WorkflowPhase` only — it cannot currently see the journal's other fields. | `Workflow/Models/PhaseReconciliation.cs:85-91` |
| F9 | `PhaseIndicatorViewModel.IsActive` exists solely to control the `Visibility` of the `Phase abschliessen` button in `PhaseIndicatorView.xaml`, which binds `CompleteCurrentPhaseCommand` through `RelativeSource AncestorType=UserControl, AncestorLevel=2`. | `Workflow/ViewModels/PhaseIndicatorViewModel.cs:30`, `Workflow/Views/PhaseIndicatorView.xaml:25-34` |
| F10 | `TaskTabViewModel.StartWorkflow` does `_ = RunAsync(request, _run.Token)` — the run task is not retained. `RunAsync` catches every exception type the design specifies and never rethrows; it sets `IsRunning = false` in `finally`. | `Workflow/ViewModels/TaskTabViewModel.cs:485-560` |
| F11 | `SettingsService.AddRecentDirectory` normalises through `WorkingDirectoryPath.Normalise`, de-duplicates case-insensitively, promotes to index 0 and caps at 15. `AppSettings` uses `Collection<string>` because CA1002 forbids a public `List<T>`. | `Workflow/Services/SettingsService.cs:37-70`, `Workflow/Models/AppSettings.cs` |
| F12 | `TaskTabView.xaml` renders the four indicators through an `ItemsControl` whose `ItemsPanel` is a `WrapPanel`; the working-directory picker is a `DockPanel` with a right-docked icon button and a non-editable `ComboBox`. | `Workflow/Views/TaskTabView.xaml:52-90` |
| F13 | `Workflow.csproj` already globs `Prompt\**\*.md` with `CopyToOutputDirectory=PreserveNewest`, and `Workflow.Tests.csproj` links the same glob into the test output. No project change is needed for the two new prompt files. | `Workflow/Workflow.csproj:25-28`, `Workflow.Tests/Workflow.Tests.csproj:31-34` |
| F14 | `Workflows/task_template/` contains `task.json, spec.md, plan.md, review.md, task_plan.md, findings.md, progress.md` and `subtasks/ST-001-subtask-template/{subtask.md, task_plan.md, findings.md, progress.md, result.json, verification.json}`. **All files are zero bytes, and there is no `status.json`.** | `C:\Users\Marco\Documents\repo\Workflows`, commit `f3966ae` |
| F15 | `WorkflowOrchestratorTests` constructs the orchestrator with a real `ArtifactWatcherFactory` against temp directories and a `FakeTerminalController`; `FakeTerminalController` records `StartedSessions`, `Sent`, `Pasted`, `ClearCount`, `DisposeCount` and can queue snapshots. | `Workflow.Tests/WorkflowOrchestratorTests.cs:56-70`, `Workflow.Tests/Fakes/FakeTerminalController.cs` |

---

## 4. Glossary

| Term | Meaning |
|------|---------|
| **Workflow-Verzeichnis** / `{workflow_path}` | Absolute path of the checked-out Workflows repository, e.g. `C:\Users\Marco\Documents\repo\Workflows`. Shared by **all** tasks. |
| **Arbeitsverzeichnis** / `{AppDirectory}` | Unchanged: the code repository the terminal `cd`s into. Selected by the existing picker. |
| **Task-Ordner (tracking)** | `{workflow_path}\{TaskName}` — one per task, inside the Workflows repository. Distinct from the task folder inside the Arbeitsverzeichnis, which is unchanged. |
| **Zerlegung** | The single `create_subtasks.md` session that produces the subtask folders and the index. |
| **Subtask** | One folder under `{workflow_path}\{TaskName}\subtasks\`, named `subtask_title` (e.g. `ST-001-taskpaths`). |
| **Index / create-flag** | `{workflow_path}\{TaskName}\result.json`. Holds the binding execution order **and** doubles as the flag that the Zerlegung finished. |
| **Subtask-Flag** | `{workflow_path}\{TaskName}\subtasks\{subtask_title}\result.json`. Signals that this subtask's session has finished. |
| **Ledger** | The derived view of all subtask states, computed from the index plus every `status.json`. Never cached across a write — disk is the truth. |

---

## 5. Path model

### 5.1 Layout

```
{workflow_path}\                              Workflows repo root
  task_template\                              reference only — NEVER read or written by the app
  {TaskName}\                                 task tracking folder
    result.json                               index + create-flag        (app READS, app DELETES)
    findings.md                               task-wide findings          (Claude only)
    subtasks\
      ST-001-foo\
        subtask.md                            task description            (app READS)
        status.json                           payload                     (app READS)
        result.json                           subtask flag                (app READS, app DELETES)
        task_plan.md  findings.md  progress.md                            (Claude only)
      ST-002-bar\
        …
```

The application creates `{TaskName}\` and `{TaskName}\subtasks\` if absent (the watcher does this
implicitly, F4) and deletes only the two `result.json` flag files. It never creates, deletes or
rewrites any other file in this repository.

### 5.2 `Workflow/Models/SubtaskPaths.cs` (new)

The single source of truth for every path inside the Workflows repository, exactly as `TaskPaths`
is for the Arbeitsverzeichnis. **Nothing else may compose these paths by hand.**

```csharp
public sealed class SubtaskPaths
{
    public SubtaskPaths(string workflowDirectory, string taskName);   // both required, non-blank

    public string WorkflowDirectory { get; }   // WorkingDirectoryPath.Normalise(workflowDirectory)
    public string TaskName { get; }            // taskName.Trim()

    public string TaskDirectory { get; }       // {WorkflowDirectory}\{TaskName}
    public string ResultAbsolute { get; }      // {TaskDirectory}\result.json
    public string SubtasksDirectory { get; }   // {TaskDirectory}\subtasks
    public string TemplateDirectory { get; }   // {WorkflowDirectory}\task_template  (validation only)

    /// Value substituted for {subtask_path}. RELATIVE to {workflow_path} — see section 5.3.
    public string SubtaskPathToken { get; }    // "{TaskName}\subtasks"

    public string SubtaskDirectory(string title);   // {SubtasksDirectory}\{title}
    public string SubtaskMarkdown(string title);    // …\subtask.md
    public string SubtaskStatusFile(string title);  // …\status.json
    public string SubtaskResultFile(string title);  // …\result.json

    /// True when the title is a single, safe path segment.
    public static bool IsValidTitle(string? title);
}
```

`IsValidTitle` returns false for null/blank, for any title containing
`Path.GetInvalidFileNameChars()`, `\`, `/` or `:`, and for `.` and `..`. Reason: the titles come
from a JSON file written by an AI agent. A title such as `..\..\Windows` would otherwise let the
loop delete a `result.json` outside the repository. Every consumer calls it before composing a
path; an invalid title is reported as a failed subtask (section 16, E6) and never touches the
file system.

### 5.3 The token contract — why `{subtask_path}` is relative

`run_subtask.md` composes paths as `{workflow_path}\{subtask_path}\{subtask_title}`. For that
composition to resolve, `{subtask_path}` **must be relative to `{workflow_path}`**. The values the
application substitutes are therefore:

| Token | Value | Example |
|-------|-------|---------|
| `{workflow_path}` | `SubtaskPaths.WorkflowDirectory` (absolute) | `C:\Users\Marco\Documents\repo\Workflows` |
| `{tasktitel}` | `TaskPaths.TaskName` | `subtask-mode` |
| `{subtask_path}` | `SubtaskPaths.SubtaskPathToken` (**relative**) | `subtask-mode\subtasks` |
| `{subtask_title}` | the subtask folder name | `ST-001-taskpaths` |
| `{subtask}` | the full text of that subtask's `subtask.md` | *(multi-line)* |

`{workflow_path}\{subtask_path}\{subtask_title}` therefore expands to
`C:\Users\Marco\Documents\repo\Workflows\subtask-mode\subtasks\ST-001-taskpaths` — correct.

`create_subtasks.md` today defines `subtask_path = {task_path}/subtasks` with
`task_path = {workflow_path}/{tasktitel}`, i.e. **absolute**, which would make the composition in
`run_subtask.md` double-prefixed. Section 14.1 carries the required correction. The application's
token values are authoritative; the prompt text is brought into line with them, not the reverse.

`{tasktitel}` is a new token and is *not* a rename of `{taskbezeichnung}`. Both resolve to the
same value (`TaskPaths.TaskName`). `{taskbezeichnung}` is kept because it is already baked into
specs on disk (the same argument BASE makes for the `-review.md`/`_spec.md` naming inconsistency);
`{tasktitel}` is added because `create_subtasks.md` already uses it.

---

## 6. On-disk contracts

The application reads exactly three file shapes. Unknown JSON properties are ignored in all of
them, so Claude may add fields without breaking the application.

### 6.1 `{workflow_path}\{TaskName}\result.json` — index and create-flag

```json
{
  "version": 1,
  "task": "subtask-mode",
  "subtasks": [
    "ST-001-taskpaths",
    "ST-002-ledger",
    "ST-003-orchestrator"
  ]
}
```

| Field | Required | Meaning |
|-------|----------|---------|
| `version` | no | Schema version. Absent or `<= 1` is accepted. `> 1` is rejected with an actionable message rather than misread. |
| `task` | no | Informational. The application does **not** verify it against `TaskName`; a mismatch is not an error because the folder location already identifies the task. |
| `subtasks` | **yes** | The binding execution order. Non-empty array of strings, each a valid title per `SubtaskPaths.IsValidTitle`. |

**There is no fallback to alphabetical directory sorting.** If `subtasks` is missing, empty, not an
array, or contains no valid title, the run fails with `SubtaskConfigurationException` and an
actionable German message naming the expected shape (section 16, E5). Rationale: an implicit
fallback would silently execute subtasks in an order nobody declared, and the requirement states
the order is binding. An explicit, loud failure is cheaper to diagnose than a wrong order.

The file **must be non-empty** (F4). See section 10.3.

### 6.2 `…\subtasks\{subtask_title}\status.json` — payload

Written by the subtask session, per `create_subtasks.md`:

```json
{
  "subtask": "ST-002",
  "status": "complete",
  "failreason": "logic error. clarification needed",
  "openFindings": 1,
  "workflowRelevantFindings": ["F-001"],
  "testsPassed": true,
  "readyForVerification": true
}
```

The application reads **only two fields**:

- `status` — compared case-insensitively against `complete`. Equal → `SubtaskStatus.Complete`.
  Anything else → `SubtaskStatus.Failed`.
- `failreason` — shown in the indicator's tooltip when the subtask failed. Optional.

`openFindings`, `workflowRelevantFindings`, `testsPassed`, `readyForVerification` and `subtask`
are deliberately ignored. They are for the human reading the repository. Reading them would
create a second, undocumented completion rule.

### 6.3 `…\subtasks\{subtask_title}\result.json` — subtask flag

Content is irrelevant to the application, but the file **must be non-empty** (F4). It exists only
to say "this session is finished; `status.json` is now readable". See section 10.

---

## 7. `Workflow/Models/SubtaskLedger.cs` (new)

A static reader, in the same style as `PhaseReconciliation` (pure, file-system-only, no DI, no
caching). It is the **only** code that interprets the three file shapes, so the orchestrator, the
view model and `PhaseReconciliation` can never disagree about what "complete" means.

```csharp
public enum SubtaskStatus { Pending, Complete, Failed }

public sealed record SubtaskState(string Title, SubtaskStatus Status, string? FailReason);

public sealed record SubtaskSnapshot(
    IReadOnlyList<SubtaskState> Subtasks,
    int Total,
    int Completed,
    int Failed);

public static class SubtaskLedger
{
    /// Reads the index and every status.json. Null when the index is absent or unusable.
    public static SubtaskSnapshot? TryRead(SubtaskPaths paths);

    /// True when the index is usable AND every listed subtask folder holds a non-empty subtask.md.
    public static bool IsDecomposed(SubtaskPaths paths);

    /// True when TryRead succeeds and every listed subtask is Complete.
    public static bool AllComplete(SubtaskPaths paths);
}
```

### 7.1 Per-subtask status derivation

Evaluated in this order. `R` = the subtask's `result.json`, `S` = its `status.json`.

| Condition | Result |
|-----------|--------|
| Title fails `IsValidTitle` | `Failed("Ungültiger Subtask-Name …")` |
| `S` exists and parses and `status == "complete"` | `Complete` |
| `S` exists and parses and `status != "complete"` | `Failed(failreason ?? "status = <wert>")` |
| `S` exists but is unreadable or not valid JSON | `Failed("status.json ist unlesbar …")` |
| `S` absent and `R` exists (non-empty) | `Failed("result.json wurde ohne status.json geschrieben.")` |
| the folder's `subtask.md` is absent or empty | `Failed("subtask.md fehlt oder ist leer.")` |
| otherwise | `Pending` |

The `subtask.md` row is evaluated **after** the `Complete` row on purpose: a subtask that already
reported `complete` is finished, and a `subtask.md` deleted afterwards must not demote it back into
the loop. It is the row that makes a listed-but-unwritten subtask count as a *failure* rather than
sit in `Pending` for ever — the loop therefore needs no bookkeeping of its own for that case.

The `S` absent / `R` present row is a protocol violation: `run_subtask.md` requires `status.json`
to be written **first** (section 10.2). Treating it as `Failed` rather than `Pending` is
deliberate — the session *did* finish, so re-running it on the next resume without the user ever
seeing that something went wrong would hide the defect.

### 7.2 Counting

`Total` = `subtasks.Length`. `Completed` = count of `Complete`. `Failed` = count of `Failed`.
`Pending` is not surfaced separately; the indicator shows `Completed von Total` plus `Failed`.

### 7.3 Transient read errors

An `IOException` or `UnauthorizedAccessException` while reading `status.json` is retried by the
caller, not by the ledger (section 9.5). A file that is still unreadable after the retries yields
`Failed`. The ledger itself never throws: every read is wrapped and maps to the table above.

---

## 8. Configuration and persistence

### 8.1 `ISubtaskConfiguration` — reading the checkbox at the right moment

The requirement says the decision is made *when phase 3 finishes*: "Wenn die Phase Review Resolve
abgeschlossen ist, soll die normale Implementierungsphase nur starten wenn subtask checkbox false
ist." The user must therefore be able to tick the box **while phases 1–3 are running** and have it
take effect. A value captured in `WorkflowRunRequest` at `StartWorkflow` time could not do that.

```csharp
public interface ISubtaskConfiguration
{
    bool Enabled { get; }
    string? WorkflowDirectory { get; }
}

public sealed class SubtaskConfiguration : ISubtaskConfiguration
{
    private sealed record Snapshot(bool Enabled, string? Directory);

    private volatile Snapshot _snapshot = new(false, null);

    public void Update(bool enabled, string? directory) => _snapshot = new(enabled, directory);

    public bool Enabled => _snapshot.Enabled;
    public string? WorkflowDirectory => _snapshot.Directory;
}
```

One `volatile` reference to an immutable record, replaced wholesale. The two properties can
therefore never be read from different generations — a torn read where `Enabled` is true but
`Directory` is still the previous value is structurally impossible. `TaskTabViewModel` owns one
instance and calls `Update` from its property setters (UI thread); the orchestrator reads it
**exactly once**, at the top of the Implementation phase, on a pool thread.

### 8.2 `AppSettings` (modified)

```csharp
public Collection<string> RecentDirectories { get; set; } = [];          // unchanged
public string? LastDirectory { get; set; }                               // unchanged
public Collection<string> RecentWorkflowDirectories { get; set; } = [];  // new
public string? LastWorkflowDirectory { get; set; }                       // new
```

`ISettingsService` gains `AddRecentWorkflowDirectory(string directory)` with exactly the semantics
of `AddRecentDirectory` (F11). **DRY:** the two methods must share one private static helper —

```csharp
private static void Promote(Collection<string> list, string directory, int cap);
```

— rather than duplicating the normalise/de-duplicate/insert/cap loop. `settings.json` gains two
properties; old files deserialise with the defaults, and an old build reading a new file ignores
the two unknown properties. No migration.

### 8.3 `TaskState` (modified) — journal schema stays at version 1

```csharp
public bool SubtasksEnabled { get; set; }             // new, default false
public string? WorkflowDirectory { get; set; }        // new, default null
```

`TaskState.CurrentVersion` **stays 1**. Bumping it to 2 would make every already-installed build
return `null` from `TryLoad` for a journal written by the new build (F7), which would hide the task
from recovery entirely. The two fields are purely additive in both directions: a version-1 journal
without them reads as "subtask mode off", which is exactly right for every task created before
this change.

`ITaskStateStore` gains:

```csharp
/// Records the subtask configuration, creating the journal if needed. Never throws.
void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory);
```

It is a separate method rather than extra parameters on `SaveDescription`, because
`SaveDescription`'s documented contract includes clearing the `Dismissed` flag and this write must
not do that.

Written at two moments: when the user changes the checkbox or the directory on a tab that already
has a task folder, and again by the orchestrator at the top of the subtask phase (so a run started
from a stale journal still records what it actually used).

### 8.4 Validation — `Workflow/Models/WorkflowDirectoryValidation.cs` (new)

Mirrors `TaskNameValidation` (a record with `IsValid` and a German `ErrorMessage`).

```csharp
public static WorkflowDirectoryValidation Validate(string? workflowDirectory);
```

| Condition | Message |
|-----------|---------|
| null / whitespace | `Bitte ein Workflow-Verzeichnis auswählen.` |
| directory does not exist | `Das Workflow-Verzeichnis existiert nicht: {path}` |
| no `task_template` subdirectory | `'{path}' sieht nicht wie das ausgecheckte Workflows-Repository aus (der Ordner 'task_template' fehlt).` |
| otherwise | valid |

`task_template` is the marker because it is the one directory the Workflows repository is
guaranteed to contain (F14) and it requires no git tooling to check. A `.git` check was rejected:
the user may legitimately work from an export, and a `.git` folder proves nothing about *which*
repository it is.

---

## 9. Orchestrator

### 9.1 `WorkflowRunRequest` (modified)

```csharp
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress,
    WorkflowPhase StartPhase = WorkflowPhase.Specification,
    ISubtaskConfiguration? Subtasks = null,
    IProgress<SubtaskProgress>? SubtaskProgress = null);
```

Both new parameters are defaulted and positional-compatible, so every existing construction site
and every existing test compiles unchanged. `Subtasks is null` is identical to
`Subtasks.Enabled == false`.

### 9.2 `SubtaskProgress` (new)

```csharp
public enum SubtaskStage { Idle, Decomposing, Running, Finished }

public sealed record SubtaskProgress(
    SubtaskStage Stage,
    int Completed,
    int Total,
    int Failed,
    string? CurrentTitle);
```

Reported through `IProgress<T>` exactly like `PhaseProgress`, so it marshals to the UI thread by
the same mechanism and the view model needs no dispatcher of its own.

### 9.3 The session primitive — a required refactor

`RunPhaseAsync` currently inlines the whole "drive one CLI session" sequence (F2). The subtask
path needs the same sequence three ways (a phase prompt, the decomposition prompt, a subtask
prompt) with only the watched paths, the prompt file and the variables differing. Duplicating it
would be three copies of the settle/paste/submit logic that BASE §7.3 and §9.3 spent two
revisions getting right.

Extract, and have **all** call sites use it:

```csharp
private async Task<bool> RunSessionAsync(
    ITerminalController terminal,
    ManualPhaseSignal manualSignal,
    string workingDirectory,
    string launcher,
    string promptFile,
    IReadOnlyDictionary<string, string> variables,
    CompletionRule rule,
    string watchDirectory,
    IReadOnlyList<string> watchPaths,
    CancellationToken cancellationToken);
```

It performs, verbatim from today's `RunPhaseAsync` body: create watcher (baseline captured before
anything is typed) → `ClearScreen` → `StartSession(ShellLocator…, workingDirectory)` →
`WaitUntilReadyAsync` → `Send($"cd \"{workingDirectory}\"\r")` → `Send($"{launcher}\r")` →
`SettleAndAnswerAsync` → `Render` → `SendPromptAsync` → `await await WhenAny(watcher.WaitAsync,
manualSignal.WaitAsync)`. It **returns true when the manual signal won the race**, false when the
watcher did.

`RunPhaseAsync` keeps the journal writes, the progress reports, `ManualSignal.Reset()` and the
stale-marker deletion, and delegates the session itself. This is a behaviour-preserving
refactor: the existing `WorkflowOrchestratorTests` must pass unchanged against it.

`workingDirectory` is always `request.Paths.WorkingDirectory` — the **Arbeitsverzeichnis**, for
every phase, the decomposition session and every subtask session. Subtasks change product code,
which lives there. The Workflows repository is reached only through absolute paths inside the
rendered prompt text.

### 9.4 Phase 4 branch

```csharp
private async Task RunPhaseAsync(request, definition, ct)
{
    _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Active);
    request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Active));
    request.ManualSignal.Reset();

    if (definition.Phase == WorkflowPhase.Implementation
        && request.Subtasks is { Enabled: true })
    {
        await RunSubtaskPhaseAsync(request, ct);
        return;
    }

    if (definition.Phase == WorkflowPhase.Implementation)
    {
        DeleteStaleDoneMarker(request.Paths.DoneAbsolute);
    }

    var manual = await RunSessionAsync(… definition.Launcher, definition.PromptFile,
        PromptVariables.For(request.Paths, request.TaskDescription), definition.Completion,
        request.Paths.TaskDirectory, WatchedPaths(request.Paths, definition), ct);

    _ = manual;   // the normal path ends the phase either way, exactly as today
    _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Completed);
    request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
}
```

Note the `Enabled` value is read **here**, at phase-4 entry, satisfying section 8.1.

### 9.5 `RunSubtaskPhaseAsync`

Steps are labelled **S1…S12** and referenced by those labels elsewhere in this document.

```
S1   options   = request.Subtasks                     // Enabled already established
     directory = options.WorkflowDirectory
     if WorkflowDirectoryValidation.Validate(directory) is invalid
         throw new SubtaskConfigurationException(message)

S2   sub = new SubtaskPaths(directory, request.Paths.TaskName)
     _state.SaveSubtaskSettings(request.Paths, true, sub.WorkflowDirectory)
     Report(Decomposing, 0, 0, 0, null)

     // ---- Step 1: Zerlegung ---------------------------------------------
S3   if (!SubtaskLedger.IsDecomposed(sub))
     {
S4       DeleteStaleFlag(sub.ResultAbsolute)
         Directory.CreateDirectory(sub.TaskDirectory)

S5       manual = await RunSessionAsync(
                     launcher:       "yo",
                     promptFile:     PromptTemplateCatalog.CreateSubtasks,
                     variables:      PromptVariables.ForSubtaskCreation(paths, desc, sub),
                     rule:           CompletionRule.FilesExist,
                     watchDirectory: sub.TaskDirectory,
                     watchPaths:     [sub.ResultAbsolute])

         if (manual)                                 // 'Task abschliessen' won the race
         {
             _state.RecordPhase(paths, Implementation, Completed)
             request.Progress.Report(new PhaseProgress(Implementation, Completed))
             return
         }
     }

S6   snapshot = SubtaskLedger.TryRead(sub)
     if (snapshot is null)
         throw new SubtaskConfigurationException(<expected shape of result.json>)
     Report(Running, snapshot.Completed, snapshot.Total, snapshot.Failed, null)

     // ---- Step 2: Schleife ----------------------------------------------
S7   attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
     manual    = false

     while (!manual)
     {
S8       snapshot = SubtaskLedger.TryRead(sub)
         if (snapshot is null) break

         next = snapshot.Subtasks.FirstOrDefault(s =>
                    s.Status != SubtaskStatus.Complete && !attempted.Contains(s.Title))
         if (next is null) break
         attempted.Add(next.Title)

S9       if (!SubtaskPaths.IsValidTitle(next.Title)
             || !NonEmpty(sub.SubtaskMarkdown(next.Title)))
         {
             continue          // already Failed in the ledger (section 7.1) - just skip
         }

S10      DeleteStaleFlag(sub.SubtaskResultFile(next.Title))
         body = ReadAllText(sub.SubtaskMarkdown(next.Title))
         Report(Running, snapshot.Completed, snapshot.Total, snapshot.Failed, next.Title)

         manual = await RunSessionAsync(
                      launcher:       "yo",
                      promptFile:     PromptTemplateCatalog.RunSubtask,
                      variables:      PromptVariables.ForSubtaskRun(paths, desc, sub,
                                                                   next.Title, body),
                      rule:           CompletionRule.FilesExist,
                      watchDirectory: sub.SubtaskDirectory(next.Title),
                      watchPaths:     [sub.SubtaskResultFile(next.Title)])

S11      await SettleStatusAsync(sub, next.Title, ct)          // section 9.6
         snapshot = SubtaskLedger.TryRead(sub) ?? snapshot
         Report(Running, snapshot.Completed, snapshot.Total, snapshot.Failed, null)
     }

S12  final = SubtaskLedger.TryRead(sub)
     Report(Finished, final?.Completed ?? 0, final?.Total ?? 0, final?.Failed ?? 0, null)

     if (manual || (final is not null
                    && final.Failed == 0
                    && final.Completed == final.Total))
     {
         _state.RecordPhase(paths, Implementation, Completed)
         request.Progress.Report(new PhaseProgress(Implementation, Completed))
     }
     // else: the journal keeps Implementation = Active -> the task stays resumable
```

Key invariants:

- **`attempted` is per run.** Every subtask is attempted at most once per run, so a subtask that
  keeps failing cannot spin the loop. A *resumed* run starts with an empty `attempted` set and
  therefore re-attempts the failures — which is exactly the requested recovery behaviour.
- **The loop re-reads the ledger every iteration** (S8), so a `status.json` that Claude
  updated for a *later* subtask, or a subtask the user completed by hand, is picked up.
- **S3 makes the Zerlegung idempotent.** A resumed run whose index already exists and whose
  every listed folder holds a non-empty `subtask.md` skips decomposition entirely and goes
  straight to the loop.
- **S4 deletes the stale index** before a *fresh* decomposition, for exactly the reason
  `DeleteStaleDoneMarker` exists (BASE §8.3): a leftover `result.json` from a previous run of the
  same task name would satisfy the watcher in milliseconds. It is not deleted on the resume path,
  where it is the evidence.

### 9.6 `SettleStatusAsync` — the payload read

`run_subtask.md` requires `status.json` to be written *before* `result.json`, so by the time the
watcher fires the payload is on disk. That ordering is a prompt instruction, not a file-system
guarantee, and `status.json` has no atomic-write rule today (section 14.2 proposes one). To keep
a half-written file from being scored as a failure:

```
read status.json; if it is present and parses → return
otherwise wait 200 ms and retry, at most 5 times (≈1 s total)
```

After the retries the ledger's table (section 7.1) applies unchanged. One second is negligible
against a subtask session and removes the entire class of "the JSON was mid-flush" false failures.

### 9.7 `SubtaskConfigurationException` (new)

`public sealed class SubtaskConfigurationException : Exception` with the three standard
constructors (CA1032). Thrown only for conditions the user can fix: invalid workflow directory,
unusable index. Caught by `TaskTabViewModel.RunAsync`'s existing catch-chain (F10) and surfaced as
`ValidationMessage`; the phase is left `Active` in the journal, so *Continue workflow* retries.

---

## 10. Handshake analysis

This section is the explicit review of whether the flag-file protocol is consistent and sensible,
and records the improvements it produced.

### 10.1 Why two files per step, not one

`status.json` carries a payload; `result.json` carries nothing. Splitting them means the flag can
be published atomically (rename) while the payload is written with ordinary, possibly slow, I/O.
A single file would force the watcher to distinguish "written" from "completely written" —
precisely the race BASE §7.5 already made the artefact hashing tri-state to avoid. **The two-file
handshake is correct and is kept.**

### 10.2 Ordering

Required order per step, and the reason:

1. `status.json` (payload) — so it is readable the instant the flag appears.
2. `result.json.tmp` → rename to `result.json` (flag) — so the watcher's `Renamed`/`Created`
   event corresponds to a complete file.

`run_subtask.md` already states the order ("First Update the status.json … Then Create a
results.json"). `create_subtasks.md` already states the `.tmp`+rename rule for its own flag.
**Improvement proposed (14.2):** state the `.tmp`+rename rule for the *subtask* flag too, and for
`status.json`, so the rule is uniform instead of stated in one of three places.

### 10.3 The flag must be non-empty — a real defect found

`ArtifactWatcher` with `FilesExist` requires `info.Length > 0` (F4). Neither prompt says the flag
file must have content, and `task_template` ships `result.json` as a **zero-byte** file (F14). A
literal reading of today's instructions therefore produces a flag the application would wait on
for ever.

Two options were weighed. Adding a `FileExistsEvenIfEmpty` completion rule would fork the watcher
for one caller and weaken the guarantee everywhere it is used. Requiring content costs one
sentence in each prompt and keeps one rule. **Decision: the flag file must be non-empty**;
sections 14.1, 14.2 and 15 carry the corresponding text and template changes.

### 10.4 The create-flag must not live at the repository root

`create_subtasks.md` currently writes `{workflow_path}/results.json`. `{workflow_path}` is the
repository root, shared by every task. Consequences: a second task's watcher is satisfied
instantly by the first task's leftover file; and deleting the stale flag before a run would clobber
another task's live one. **Decision: the flag is `{workflow_path}\{TaskName}\result.json`** — one
per task, colocated with that task's subtasks, deletable without affecting anything else.

### 10.5 Naming

`results.json` (plural) appears in the instruction text of both prompts; `result.json` (singular)
appears in the `.tmp`-rename paragraph of `create_subtasks.md` and is what `task_template` ships.
**Decision: `result.json`, singular, everywhere.** It matches the template that already exists in
the tracking repository, so no committed artefact has to be renamed.

### 10.6 Watching, not polling, is already solved

No new watching mechanism is needed. `ArtifactWatcher` already combines `FileSystemWatcher` with a
poll (F4), which is what makes this reliable on a OneDrive-backed or network path — and
`C:\Users\Marco\Documents\` is exactly the kind of location where `FileSystemWatcher` alone drops
events. Each step creates its own watcher over its own directory and disposes it when the step
ends, so at most one watcher per tab is live.

### 10.7 What the application deliberately does not watch

`findings.md`, `progress.md`, `task_plan.md` and `verification.json` are never watched or read.
Watching them would create additional completion rules that no prompt establishes, and
`verification.json` in particular has no writer specified anywhere. Section 15 proposes removing
it from the template rather than inventing a meaning for it.

---

## 11. User interface

### 11.1 Placement

`TaskTabView.xaml` — the existing `ItemsControl` over `Phases` and the new controls go into one
outer `WrapPanel`, so the checkbox and the subtask indicator sit *next to* the *Implementierung*
indicator and wrap with it on a narrow window:

```xml
<WrapPanel Margin="0,20,0,0" Orientation="Horizontal">

    <ItemsControl ItemsSource="{Binding Phases}">
        <!-- unchanged: inner WrapPanel + PhaseIndicatorView item template -->
    </ItemsControl>

    <CheckBox Margin="0,0,24,0"
              VerticalAlignment="Top"
              Content="Subtasks"
              IsChecked="{Binding SubtasksEnabled, Mode=TwoWay}"
              IsEnabled="{Binding IsSubtaskConfigurationEditable}"
              ToolTip="Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks" />

    <StackPanel Orientation="Horizontal"
                VerticalAlignment="Top"
                Visibility="{Binding SubtasksEnabled,
                                     Converter={StaticResource BooleanToVisibilityConverter}}">
        <views:SubtaskIndicatorView DataContext="{Binding Subtasks}" />
    </StackPanel>

</WrapPanel>
```

The wrapping `StackPanel` keeps the tab's `DataContext` for the `Visibility` binding while the
inner view receives `Subtasks` — binding both on one element is not possible.

The tooltip text is verbatim as requested: *"Wenn die Aufgabe das Kontextfenster eines Agents
übersteigt, aktiviere Subtasks"*.

### 11.2 The Workflow-Verzeichnis row

Placed directly below the existing Arbeitsverzeichnis `DockPanel`, visible only while the checkbox
is ticked, and structurally identical to it (F12) so the two read as one family:

```xml
<DockPanel Margin="0,12,0,0" LastChildFill="True"
           Visibility="{Binding SubtasksEnabled,
                                Converter={StaticResource BooleanToVisibilityConverter}}">
    <Button DockPanel.Dock="Right" Margin="8,0,0,0"
            ToolTip="Workflow-Verzeichnis auswählen"
            Style="{StaticResource MaterialDesignIconButton}"
            Command="{Binding BrowseWorkflowDirectoryCommand}">
        <materialDesign:PackIcon Kind="FolderOpen" />
    </Button>

    <ComboBox materialDesign:HintAssist.Hint="Workflow-Verzeichnis"
              IsEditable="False"
              ItemsSource="{Binding RecentWorkflowDirectories}"
              SelectedItem="{Binding WorkflowDirectory, Mode=TwoWay}" />
</DockPanel>
```

plus a `TextBlock` bound to `WorkflowDirectoryMessage` using the existing
`MaterialDesignValidationErrorBrush`.

`RecentWorkflowDirectories` is maintained by the same never-`Clear()` reconciliation
`SyncRecentDirectories` already uses — for the reason documented on that method: clearing an
`ItemsSource` while `SelectedItem` is bound `TwoWay` makes the `Selector` write `null` back into
the bound property.

`IDirectoryPickerService.PickDirectory` is reused as-is. Its dialog title is hard-coded to
*"Arbeitsverzeichnis auswählen"*; the interface gains an optional `title` parameter with that
string as its default, so no existing call site changes.

### 11.3 `SubtaskIndicatorViewModel` (new)

```csharp
public sealed partial class SubtaskIndicatorViewModel : ObservableObject
{
    [ObservableProperty] private SubtaskStage _stage = SubtaskStage.Idle;
    [ObservableProperty] private int _completed;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private string? _currentTitle;

    public PhaseStatus Status { get; }      // see table
    public string CountText { get; }
    public bool HasFailures => Failed > 0;
    public string FailureText { get; }      // "1 fehlgeschlagen" / "N fehlgeschlagen"
    public string? Tooltip { get; }         // current subtask, or the failure reasons

    public void Apply(SubtaskProgress progress);
    public void Reset();
}
```

Each `[ObservableProperty]` carries `[NotifyPropertyChangedFor]` for the derived members it
affects.

| `Stage` | `Status` (drives icon colour) | `CountText` |
|---------|------------------------------|-------------|
| `Idle` | `Pending` (grey) | `Noch nicht begonnen` |
| `Decomposing` | `Active` (yellow) | `Zerlegung läuft…` |
| `Running` | `Active` (yellow) | `{Completed} von {Total}` |
| `Finished`, `Failed == 0`, `Total > 0` | `Completed` (green) | `{Completed} von {Total}` |
| `Finished`, otherwise | `Active` (yellow) | `{Completed} von {Total}` |

### 11.4 `SubtaskIndicatorView.xaml` (new)

A `UserControl` mirroring `PhaseIndicatorView`: a `PackIcon` with
`Kind="FormatListChecks"` whose `Foreground` uses the existing `PhaseStatusToBrushConverter` over
`Status`, the label `Subtasks`, `CountText` beneath it, and a `TextBlock` bound to `FailureText`
with `Foreground="{DynamicResource MaterialDesignValidationErrorBrush}"` and
`Visibility` driven by `HasFailures` through the existing `BooleanToVisibilityConverter`.

No new converter is introduced.

### 11.5 The Implementierung indicator's colour

Unchanged mechanism: it renders `PhaseStatus` through the existing converters. What changes is
*when* the orchestrator records `Completed` (section 9.5, S12) — only when every subtask is
complete. So the *Implementierung* icon is grey before phase 4, yellow while subtasks run or while
failures remain, and green when all subtasks are complete. That is exactly the requested
"in Arbeit, fertig, oder noch nicht begonnen", and it needs no fourth `PhaseStatus` value, no
change to the two converters, no change to the journal schema and no change to
`ApplyJournal`'s Active→Pending mapping.

### 11.6 `TaskTabViewModel` additions

```csharp
[ObservableProperty] private bool _subtasksEnabled;
[ObservableProperty] private string? _workflowDirectory;
[ObservableProperty] private string? _workflowDirectoryMessage;

public ObservableCollection<string> RecentWorkflowDirectories { get; }
public SubtaskIndicatorViewModel Subtasks { get; } = new();
public bool IsSubtaskConfigurationEditable { get; }   // false once phase 4 is Active

[RelayCommand] private void BrowseWorkflowDirectory();
```

- `OnWorkflowDirectoryChanged` normalises through `WorkingDirectoryPath.Normalise` (returning
  early on a changed value, exactly as `OnWorkingDirectoryChanged` does, to keep the `Selector`
  from writing `null` back), then updates the MRU, saves settings, revalidates, pushes into
  `SubtaskConfiguration.Update`, and persists to the journal when a task folder is known.
- `OnSubtasksEnabledChanged` revalidates and pushes into `SubtaskConfiguration.Update`. When it is
  switched **on** and `WorkflowDirectory` is null, `LastWorkflowDirectory` is offered as the
  prefill; if that is absent or invalid, `WorkflowDirectoryMessage` explains what to pick.
- `CanStartWorkflow()` gains `&& (!SubtasksEnabled || WorkflowDirectoryMessage is null)`. So the
  orchestrator can never be handed "enabled but no valid directory" through the normal path;
  section 9.5 S1 is the defence in depth for a directory deleted mid-run.
- `ApplyProgress` keeps maintaining `_activePhase` (still needed by `CanCompleteTask` and by
  `IsSubtaskConfigurationEditable`) and now also raises `IsSubtaskConfigurationEditable`.
- A new `ApplySubtaskProgress(SubtaskProgress)` forwards to `Subtasks.Apply`. It is `public` for
  the same reason `ApplyProgress` is: the tests drive it directly.
- `LoadForResume` additionally restores `SubtasksEnabled` and `WorkflowDirectory` from
  `task.State` (inside the existing `_suppressFolderSync` block) and, when both are set, seeds the
  indicator from `SubtaskLedger.TryRead` so a recovered tab immediately shows
  `21 von 24` + `3 fehlgeschlagen`.

---

## 12. Removal of `Phase abschliessen`; `Task abschliessen` closes the tab

### 12.1 Removal

Phases now end **only** on their flag/artefact condition. Removed:

- the `Button` in `Workflow/Views/PhaseIndicatorView.xaml` (the control becomes just the icon and
  the label);
- `TaskTabViewModel.CompleteCurrentPhase`, `CanCompleteCurrentPhase` and the generated
  `CompleteCurrentPhaseCommand`, and the `NotifyCanExecuteChanged` call for it in `ApplyProgress`;
- `PhaseIndicatorViewModel.IsActive` and the `[NotifyPropertyChangedFor]` that feeds it — F9
  establishes the button was its only consumer.

`ManualPhaseSignal` **stays**: `Task abschliessen` still uses it, and `RunSessionAsync` still races
it, which is what lets a run end promptly instead of only on cancellation.

**Consequence, stated plainly:** there is no longer any way to end phases 1–3 by hand. If a CLI
never writes its artefact, that phase waits indefinitely. This is the user's explicit decision
("es wird nur noch mit flag dateien gearbeitet"), and it is survivable because the terminal stays
live and interactive throughout — the user can drive the CLI to produce the file. No timeout is
introduced (section 2.2).

### 12.2 `Task abschliessen` also closes the tab

```csharp
private Task? _runTask;     // set in StartWorkflow: _runTask = RunAsync(request, _run.Token);

[RelayCommand(CanExecute = nameof(CanCompleteTask))]
private async Task CompleteTaskAsync()
{
    _manualSignal.Signal();

    var run = _runTask;
    if (run is not null)
    {
        await run;          // RunAsync catches everything and never rethrows (F10)
    }

    CloseRequested?.Invoke(this, EventArgs.Empty);
}
```

The generated command is still named `CompleteTaskCommand` (the MVVM Toolkit strips the `Async`
suffix), so `TaskTabViewModelTests.CompleteTask_IsOnlyEnabledDuringTheImplementationPhase`
compiles and passes unchanged; the type becomes `IAsyncRelayCommand`, which still exposes
`CanExecute` and `NotifyCanExecuteChanged`.

Awaiting the run **before** raising `CloseRequested` matters: `CloseTab` disposes the tab, which
cancels the run's `CancellationTokenSource`. Closing first would turn the orderly
"manual signal → record `Completed` → return" into an `OperationCanceledException` and the journal
would keep Implementation `Active` — the task would then be offered for recovery even though the
user just declared it finished.

`CloseRequested` routes into `MainWindowViewModel.CloseTab`, which calls `NotifyClosedByUser()` and
therefore sets `Dismissed = true`. That is correct here: the user has declared the task complete.

`CanCompleteTask` is unchanged (`IsRunning && _activePhase == WorkflowPhase.Implementation`). A tab
that is not running is closed with the tab's existing close button.

---

## 13. Recovery and reconciliation

### 13.1 Disk is the truth

No current-subtask index is stored anywhere. On resume the orchestrator re-reads `result.json` and
every `status.json` and continues at the first subtask that is not `Complete` (section 9.5, S8). There is therefore no second source of truth that could drift from the files.

A subtask whose `status.json` says `complete` but whose `result.json` is missing — the app died in
the window between the two writes — is treated as complete and not re-run. The work *was* done;
re-running it would be more dangerous than skipping it.

### 13.2 `PhaseReconciliation` (modified) — a regression this design must prevent

`ArtefactsPresent` currently demotes a `Completed` Implementation phase when `-done.md` is absent
(F8). In subtask mode that file is **never written**, because `implementation_prompt.md` — the only
thing that writes it — never runs. Without a change, every startup scan and every re-arm would
demote a perfectly finished subtask task back to Pending and offer it for recovery for ever.

`ArtefactsPresent` takes the `TaskState` as well, and Implementation becomes:

```csharp
WorkflowPhase.Implementation =>
    state.SubtasksEnabled && !string.IsNullOrWhiteSpace(state.WorkflowDirectory)
        ? SubtasksAllComplete(state.WorkflowDirectory, paths.TaskName)
        : NonEmpty(paths.DoneAbsolute),
```

`SubtasksAllComplete` constructs `SubtaskPaths` inside a `try`/`catch (ArgumentException)` and
returns **true** on a malformed stored path — consistent with `NonEmpty`'s policy of not demoting
on an error it cannot interpret (demotion must be evidence-driven, never doubt-driven).

`Reconcile` already receives the state, so only the private helper's signature changes.

### 13.3 What the tab shows after recovery

`ApplyJournal` maps a recorded `Active` to `Pending` (BASE/RESUME behaviour, unchanged), so a task
interrupted mid-loop shows *Implementierung* grey with the *Subtasks* indicator seeded from the
ledger, e.g. `21 von 24` and `3 fehlgeschlagen`, and `Continue workflow` as the button label. On
Continue, `StartPhase` is Implementation, the Zerlegung is skipped (index present) and the loop
re-attempts exactly the non-complete subtasks.

### 13.4 `RecoverableTask` and `TaskRecoveryScanner`

Unchanged. `RecoverableTask` already carries the whole `TaskState`, which now includes the two new
fields, and the scanner's own logic does not look at Implementation's artefacts directly — it goes
through `PhaseReconciliation`.

---

## 14. Required prompt-template changes (proposals — NOT applied by this design session)

The design session was instructed not to modify anything under `Workflow\Prompt\`. The texts below
are the binding target state; the implementation plan carries them as separate, explicitly marked
**prompt-change tasks**. Until they are applied, subtask mode cannot work end to end, so those
tasks are sequenced before the first end-to-end verification.

The application's token values (section 5.3) are authoritative. These changes bring the prompts
into line with them.

### 14.1 `Workflow/Prompt/create_subtasks.md`

| # | Current text | Required change | Why |
|---|--------------|-----------------|-----|
| P0 | `task_path = {workflow_path}/{tasktitel}` and `{subtask_path}/{subtask_title}` — i.e. the file uses `{task_path}` and `{subtask_title}` in **brace** syntax | Every name the *application* does not substitute must use **angle brackets**: `<task_path>`, `<subtask_title>`. Only `{workflow_path}`, `{tasktitel}`, `{subtask_path}`, `{spec_path}` and `{plan_path}` may keep braces in this file. | Two separate defects. (a) `{task_path}` is in no token set, so `PromptTemplateService.Render` throws `PromptTemplateException` and — once 14.3 lands — `ValidateAll` disables `Start workflow` on every tab at startup. (b) `{subtask_title}` in this file is a name **Claude invents per subtask**; substituting one value for it would destroy the instruction. Angle brackets are invisible to the token regex (F6), so the two kinds of name stop competing for one syntax. |
| P1 | `subtask_path = {task_path}/subtasks` (absolute, derived from `task_path`) | State that `{subtask_path}` is **relative to `{workflow_path}`** and equals `{tasktitel}/subtasks`. Keep `<task_path> = {workflow_path}/{tasktitel}` as prose only. | `run_subtask.md` composes `{workflow_path}\{subtask_path}\…`; an absolute `{subtask_path}` double-prefixes (section 5.3). |
| P2 | `erzeugst du die flagdatei {workflow_path}/results.json` | `{workflow_path}/{tasktitel}/result.json` | Repository root is shared by all tasks (10.4); singular name (10.5). |
| P3 | `results.json` / `result.json` used interchangeably | `result.json` everywhere | 10.5 |
| P4 | Flag file content unspecified; `task_template` ships it empty | The flag must be **non-empty** and must contain the ordered index of section 6.1 (`version`, `task`, `subtasks`) | A zero-byte file never satisfies `FilesExist` (10.3); the ordered list is the binding execution order (6.1). |
| P5 | `status.json` example present, but writing it is not stated as mandatory | State that **every** subtask folder must contain a `status.json`, initially with `"status": "pending"` and the subtask's own title. Write the example over several lines so no `{name}` pattern is formed (F6). | The ledger needs a file to read before a subtask runs; without it a not-yet-started subtask is indistinguishable from a missing folder. |
| P6 | `.tmp` + rename described for the flag | Keep, and state it applies to `status.json` too | Uniform rule (10.2). |
| P7 | — | Add: subtask folder names must be plain names without `\`, `/`, `:` or `..` | `SubtaskPaths.IsValidTitle` rejects anything else (5.2). |

`{tasktitel}` is supplied by the application, so the file needs no change for that token.
The embedded JSON example needs no escaping (F6), because every `{` in it is followed by a newline
or a quote rather than by an identifier.

**Sequencing consequence:** because of P0, `ValidateAll` (section 14.3) would reject the *shipped*
`create_subtasks.md` and disable `Start workflow` on every tab. The prompt corrections of 14.1 and
14.2 must therefore be applied **before** the stricter validation of 14.3 is switched on. The
implementation plan orders the tasks accordingly.

### 14.2 `Workflow/Prompt/run_subtask.md`

| # | Current text | Required change | Why |
|---|--------------|-----------------|-----|
| P8 | `{workflow_path}\{subtask_path}\{subtask_title}\…` | Unchanged — but add one line stating `{subtask_path}` is relative to `{workflow_path}` | Makes the contract explicit where it is consumed. |
| P9 | `Create a results.json file as flag file in {workflow_path}\{subtask_path}\{subtask_title}\` | `result.json`, written `.tmp` + rename, **non-empty** | 10.2, 10.3, 10.5 |
| P10 | `First Update the {subtask_path}/{subtask_title}/status.json` | Prefix with `{workflow_path}\`, and require the same `.tmp` + rename | The bare `{subtask_path}` here is relative and would resolve against the CLI's cwd (the Arbeitsverzeichnis), writing the file into the wrong repository. |
| P11 | `status.json` fields unspecified | State `status` must be exactly `complete` on success, and that any other value plus a `failreason` marks a failure | Section 6.2 is the only thing the app reads. |
| P12 | `the acceptance gate `Workflow\verify.ps1` exits 0` | Leave as is | `Workflow/verify.ps1` exists in this repository (verified). |

### 14.3 `PromptTemplateCatalog` and startup validation

```csharp
public static class PromptTemplateCatalog
{
    public const string CreateSubtasks = "create_subtasks.md";
    public const string RunSubtask     = "run_subtask.md";

    /// File name -> the tokens that file is allowed to use.
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedTokens { get; }
}
```

`PromptTemplateService.ValidateAll` iterates this map (the four phase templates plus the two new
ones) instead of `PhaseCatalog.All`, and checks each file against **its own** allowed set rather
than the union. That turns "`{subtask}` accidentally used in `initial_prompt.md`" from a runtime
`PromptTemplateException` mid-phase into a startup error, and it extends the existing F17 gate
(BASE §9.4) — a broken `create_subtasks.md` now disables `Start workflow` on every tab with the
message already shown for the other templates.

Allowed sets:

| Template | Allowed tokens |
|----------|----------------|
| `initial_prompt.md`, `review_prompt.md`, `resolve_review_prompt.md`, `implementation_prompt.md` | `taskbezeichnung`, `taskbeschreibung`, `AppDirectory`, `spec_path`, `plan_path`, `review_path`, `done_path` |
| `create_subtasks.md` | the above **plus** `workflow_path`, `tasktitel`, `subtask_path` |
| `run_subtask.md` | the above **plus** `subtask_title`, `subtask` |

### 14.4 `PromptVariables` (modified)

```csharp
public static Dictionary<string, string> For(TaskPaths paths, string taskDescription);          // unchanged
public static Dictionary<string, string> ForSubtaskCreation(TaskPaths, string, SubtaskPaths);
public static Dictionary<string, string> ForSubtaskRun(TaskPaths, string, SubtaskPaths,
                                                       string subtaskTitle, string subtaskBody);
```

`KnownNames` becomes the union of all sets and is used by `ValidateAll` only. The three builders
must be layered (`ForSubtaskRun` calls `ForSubtaskCreation` calls `For`) so the seven base tokens
have exactly one definition.

---

## 15. Required `task_template` changes in the Workflows repository (proposal)

`C:\Users\Marco\Documents\repo\Workflows` is a separate repository and is **not** modified by this
design session either. Required:

| # | Change | Why |
|---|--------|-----|
| T1 | Add `task_template/subtasks/ST-001-subtask-template/status.json` | The file the application reads does not exist in the template (F14). |
| T2 | Give `task_template/result.json` (new, at task level) and the subtask `result.json` non-empty placeholder content, or document that the template's zero-byte files are illustrative only | A copied zero-byte flag never satisfies the watcher (10.3). |
| T3 | Consider deleting `verification.json` | Nothing writes or reads it (10.7). Keeping an unused file invites a future reader to assume it is part of the handshake. |
| T4 | Add a short `README.md` at the repository root describing the layout of section 5.1 and the contracts of section 6 | The repository is the integration surface between the app and Claude; today nothing in it states the contract. |

T1 is required for the feature; T2–T4 are recommended. The plan marks them accordingly.

---

## 16. Failure modes and edge cases

| # | Situation | Behaviour |
|---|-----------|-----------|
| E1 | Checkbox ticked, no workflow directory chosen | `CanStartWorkflow` is false; the row explains what to pick. |
| E2 | Chosen directory is not the Workflows repo (`task_template` absent) | Validation message; `Start workflow` stays disabled. |
| E3 | Workflow directory deleted between Start and phase 4 | `SubtaskConfigurationException` at 9.5 S1 → `ValidationMessage`, `IsRunning` false, Implementation stays `Active` → resumable. |
| E4 | Zerlegung writes the flag to the old location (repo root) | The watcher on `{TaskName}\` never fires; the phase waits, terminal stays usable. Removed by the 14.1/P2 change. |
| E5 | `result.json` present but no usable `subtasks` array | `SubtaskConfigurationException` naming the expected shape; phase stays `Active`. |
| E6 | A title in `subtasks` is invalid (`..`, separators) or its `subtask.md` is missing/empty | That subtask is reported `Failed` and **skipped**; the loop continues. No file outside the repository is ever touched. |
| E7 | `status.json` missing or unparsable after the flag appeared | Re-read 5 × 200 ms (9.6), then `Failed` with an explanatory reason. |
| E8 | Subtask session dies without writing anything | The loop waits on that subtask's flag indefinitely. Deliberate (2.2); the terminal is live and the user can finish it by hand. |
| E9 | A subtask keeps failing | Attempted at most once per run (`attempted`); counted as failed; the loop finishes the rest. `Continue workflow` re-attempts it. |
| E10 | All subtasks complete | Implementation recorded `Completed`, both icons green. |
| E11 | Some subtasks failed | Implementation stays `Active` in the journal; icon yellow; indicator shows the red failure count; the task remains offered for recovery. |
| E12 | User presses `Task abschliessen` mid-loop | The loop's `WhenAny` returns "manual"; the phase is recorded `Completed`; the run ends; the tab closes (12.2). |
| E13 | Leftover `result.json` from an earlier run of the same task name | Deleted before a fresh Zerlegung (9.5 S4) and before each subtask attempt (S10). On the resume path the index is intentionally kept. |
| E14 | Two tabs with the same task name and the same workflow directory | They share `{workflow_path}\{TaskName}` and will interfere. Pre-existing class of problem (two tabs already share the task folder in the Arbeitsverzeichnis); out of scope, documented. |
| E15 | Journal written by an older build | `SubtasksEnabled` reads false, `WorkflowDirectory` null → normal phase 4, exactly as before. |
| E16 | `settings.json` written by an older build | The two new properties default to empty/null. |
| E17 | Workflow directory on a OneDrive/network path where `FileSystemWatcher` drops events | Covered by `ArtifactWatcher`'s poll (F4, 10.6). |

### 16.1 Regression risks to watch

1. **`PhaseReconciliation` demoting subtask-mode tasks for ever** — section 13.2. The highest-value
   test in the whole change.
2. **Removing `CompleteCurrentPhaseCommand` while `PhaseIndicatorView.xaml` still binds it** — a
   WPF binding failure is silent at runtime. Both files change in the same task, and the XAML-load
   test must cover it.
3. **`RunSessionAsync` extraction altering phase 1–3 behaviour** — the settle/paste/submit logic is
   the most defect-prone part of the codebase (BASE §7.3, §9.3, and the Codex `readyPattern`
   lesson). The existing `WorkflowOrchestratorTests` must pass **unchanged**, with no edits to
   their assertions; if an assertion needs changing, the refactor is wrong.
4. **`ValidateAll` becoming stricter** — it now validates two more files and uses per-file token
   sets. A prompt file that was silently tolerated before can now disable `Start workflow` on
   every tab. Intended, but it must be verified against the shipped prompt files.
5. **`WorkflowRunRequest` gaining parameters** — they must be defaulted and appended, or every
   existing test construction site breaks.

---

## 17. Acceptance criteria

Each is independently verifiable.

**Configuration**

- A1 A `Subtasks` checkbox is rendered next to the *Implementierung* indicator, with the tooltip
  *"Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks"*.
- A2 Ticking it reveals a Workflow-Verzeichnis ComboBox plus folder button; unticking hides them.
- A3 A directory without `task_template` produces a German validation message and keeps
  `Start workflow` disabled.
- A4 A valid choice is written to `settings.json` (`LastWorkflowDirectory` +
  `RecentWorkflowDirectories`) and is preselected in the next new tab.
- A5 Starting a run with the box ticked writes `subtasksEnabled: true` and `workflowDirectory`
  into the task's `.workflow-state.json`.

**Execution**

- A6 With the box unticked, phase 4 is byte-for-byte the behaviour of today: `implementation_prompt.md`
  and the `-done.md` watcher.
- A7 With the box ticked, phase 4 first starts one `yo` session whose pasted prompt is the rendered
  `create_subtasks.md`, in which `{workflow_path}`, `{tasktitel}` and `{subtask_path}` are
  substituted and `{subtask_path}` is relative.
- A8 That step is skipped when `{workflow_path}\{TaskName}\result.json` already lists subtasks whose
  folders all hold a non-empty `subtask.md`.
- A9 After the index appears, the app starts one fresh session per subtask, in the order of the
  `subtasks` array, each with `ClearScreen`, `StartSession`, `cd "<Arbeitsverzeichnis>"`, `yo`, and
  the rendered `run_subtask.md`.
- A10 `{subtask}` is substituted with the full text of that subtask's `subtask.md`, and
  `{workflow_path}\{subtask_path}\{subtask_title}` resolves to that subtask's folder.
- A11 A subtask ends when its `result.json` becomes non-empty; the app then reads its `status.json`.
- A12 A subtask whose status is not `complete` is counted as failed, is **not** retried within the
  run, and the loop proceeds to the next subtask.
- A13 Each subtask is attempted at most once per run.
- A14 When every subtask is `complete`, Implementation is recorded `Completed` and its icon is green.
- A15 When at least one subtask failed, Implementation is **not** recorded `Completed`, its icon
  stays yellow, and the task is still offered for recovery.

**Display**

- A16 A second indicator labelled `Subtasks` appears while the box is ticked.
- A17 It shows `Zerlegung läuft…` during decomposition and `{N} von {M}` during the loop, updating
  after each subtask.
- A18 With failures it additionally shows `{K} fehlgeschlagen` in the error colour.
- A19 Its icon is grey before phase 4, yellow while running, green only when all subtasks completed.

**Recovery**

- A20 Restarting the app mid-loop offers the task, restores the checkbox and the directory, and
  seeds the indicator from disk.
- A21 *Continue workflow* skips completed subtasks and re-attempts non-complete ones, in order.
- A22 A subtask-mode task with all subtasks complete is **not** demoted by the startup scan even
  though `-done.md` does not exist.

**Removal / manual exit**

- A23 The `Phase abschliessen` button no longer exists in any phase indicator, and
  `CompleteCurrentPhaseCommand` no longer exists on `TaskTabViewModel`.
- A24 `Task abschliessen` ends the run and then closes the tab.

**Quality gate**

- A25 `dotnet build` produces no warnings (the solution treats analyzer findings as build errors).
- A26 `dotnet test` passes. No pre-existing test assertion is modified; the only permitted edits
  to existing tests are additions and the removal of anything referencing `CompleteCurrentPhaseCommand`
  (a grep of `Workflow.Tests/` on 2026-09-17 found no such reference, so no edit is expected).
- A27 `Workflow\verify.ps1` exits 0.

---

## 18. Verification steps

### 18.1 Automated

```powershell
dotnet build  C:\Users\Marco\Documents\repo\Workflow\Workflow.sln -c Debug
dotnet test   C:\Users\Marco\Documents\repo\Workflow\Workflow.sln -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\Marco\Documents\repo\Workflow\Workflow\verify.ps1
```

New test files, following the existing conventions (xunit, `Method_Condition_Expectation` names,
real temp directories, `Xunit.StaFact` `[WpfFact]` for view-model tests that touch a dispatcher):

| File | Covers |
|------|--------|
| `Workflow.Tests/SubtaskPathsTests.cs` | composition, normalisation, `SubtaskPathToken` relativity, `IsValidTitle` rejections |
| `Workflow.Tests/SubtaskLedgerTests.cs` | every row of the 7.1 table, counting, `IsDecomposed`, `AllComplete`, unreadable/absent files |
| `Workflow.Tests/SubtaskOrchestratorTests.cs` | decomposition prompt + flag, skip-when-decomposed, ordering, per-subtask session shape, failure skipping, one-attempt-per-run, completion rule, manual-signal break, `SubtaskConfigurationException` cases |
| `Workflow.Tests/SubtaskIndicatorViewModelTests.cs` | the `Stage`→`Status`/`CountText` table, `HasFailures`, `FailureText` pluralisation |
| `Workflow.Tests/WorkflowDirectoryValidationTests.cs` | the four validation rows |
| additions to `PhaseReconciliationTests` / `TaskRecoveryScannerTests` | A22 — no demotion in subtask mode; demotion still correct in normal mode |
| additions to `PromptTemplateServiceTests` | per-file allowed-token validation; the three variable builders; the shipped prompt files validate clean |
| additions to `SettingsServiceTests` | workflow-directory MRU promotion, cap, normalisation, round-trip |
| additions to `TaskTabViewModelTests` | A1–A5, `IsSubtaskConfigurationEditable`, `CanStartWorkflow` gating, `LoadForResume` restoring the two fields, `CompleteTask` closing the tab |
| `Workflow.Tests/AppResourceTests.cs` (existing) | the XAML still loads after the button removal |

### 18.2 Manual end-to-end

Prerequisite: the section 14 prompt changes and T1 have been applied.

1. Launch the app. New tab; name `subtask-probe`; a short Taskbeschreibung; pick an Arbeitsverzeichnis.
2. Tick `Subtasks`; pick `C:\Users\Marco\Documents\repo\Workflows`. Confirm no validation message.
3. `Start workflow`. Let phases 1–3 run (or seed the artefacts by hand and use `Continue workflow`).
4. At phase 4, confirm the terminal starts `yo` and receives the `create_subtasks.md` text with the
   Workflows path substituted, and that the indicator reads `Zerlegung läuft…`.
5. Confirm `{Workflows}\subtask-probe\result.json` appears, non-empty, with an ordered `subtasks`
   array, and that folders exist for every entry.
6. Confirm the indicator flips to `0 von N` and a **new** session starts for the first subtask.
7. In a second subtask folder, hand-write `status.json` with `"status": "failed"` and a non-empty
   `result.json` before the loop reaches it; confirm the loop skips it and the indicator shows
   `… fehlgeschlagen` in red.
8. Kill the app mid-loop. Restart. Confirm the task is offered, the checkbox and directory are
   restored, the counter matches the files on disk, and `Continue workflow` resumes at the first
   non-complete subtask without repeating a completed one.
9. Let the remaining subtasks complete; fix the seeded failure by setting its status to `complete`
   and re-running. Confirm both icons turn green.
10. Confirm no `Phase abschliessen` button exists anywhere, and that `Task abschliessen` closes the
    tab.

---

## 19. Decision log

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Every session (decomposition and subtasks) runs in the **Arbeitsverzeichnis**, not the Workflows repo, and still sends an explicit `cd`. | Subtasks change product code. The tracking repo is reached through absolute paths in the prompt. |
| D2 | `result.json`, singular, per task and per subtask; never at the repository root. | 10.4, 10.5. |
| D3 | The flag file must be non-empty. | `FilesExist` requires `Length > 0` (F4); the alternative forks the watcher. 10.3. |
| D4 | Failed subtasks are skipped and counted; no in-run retry. | User decision; the order is binding, and an auto-retry hides a real failure. |
| D5 | Implementation is recorded `Completed` only when `Failed == 0`. | Keeps `PhaseStatus` three-valued, keeps the journal schema, and makes "Continue re-attempts the failures" fall out of the existing resume machinery. |
| D6 | No fourth `PhaseStatus` value. | Would touch both converters, the journal's string-enum serialisation and `PhaseReconciliation` — all tested, none of it needing to change otherwise. |
| D7 | Disk is the only truth; no current-subtask index in the journal. | One source of truth cannot drift. |
| D8 | `{subtask_path}` is relative to `{workflow_path}`. | The only way `{workflow_path}\{subtask_path}\{subtask_title}` resolves. 5.3. |
| D9 | Strict ordered index, no alphabetical fallback. | A silent fallback would execute a binding order nobody declared. 6.1. |
| D10 | Journal schema stays at version 1. | A bump would make installed builds hide new journals entirely (F7). |
| D11 | Workflow path stored globally **and** in the task journal. | Global for convenience; per-task so a resume uses the folder the run actually used. |
| D12 | Validation marker is `task_template`. | Present by construction (F14), needs no git tooling, and `.git` proves nothing about which repo it is. |
| D13 | `ISubtaskConfiguration` read once at phase-4 entry, not captured at Start. | The requirement decides at the phase-3→4 boundary; a user must be able to tick the box while phases 1–3 run. |
| D14 | `RunSessionAsync` extracted and used by **all** phases. | Three copies of the settle/paste/submit logic is the defect the refactor prevents; DRY. |
| D15 | `Phase abschliessen` removed, `ManualPhaseSignal` kept. | User decision. The signal still backs `Task abschliessen` and still ends a run promptly. |
| D16 | `Task abschliessen` awaits the run before closing the tab. | Closing first cancels the run and leaves the journal `Active`. 12.2. |
| D17 | No timeouts anywhere. | User decision; the live terminal is the escape hatch. |
| D18 | The app reads only `result.json`, `status.json` and `subtask.md`. | Every other file would become an undocumented completion rule. 10.7. |
| D19 | Prompt and `task_template` corrections are proposals carried by the plan, not changes made now. | Explicit instruction for this session. Sections 14 and 15 are the binding target texts. |
| D20 | Subtask titles are validated as single safe path segments. | The titles come from AI-written JSON and are used to compose paths that the app deletes files from. 5.2. |
| D21 | In prompt templates, `{name}` is reserved for values the **application** substitutes; names the CLI invents for itself use `<name>`. | `create_subtasks.md` uses `{task_path}` (which no token set defines) and `{subtask_title}` (a name Claude picks per subtask). One syntax for two meanings makes the first a startup error and the second an instruction the renderer would destroy. 14.1/P0. |
| D22 | The prompt corrections (14.1, 14.2) are sequenced **before** the stricter `ValidateAll` (14.3). | Switching validation on first would disable `Start workflow` on every tab until the prompts are fixed. |
