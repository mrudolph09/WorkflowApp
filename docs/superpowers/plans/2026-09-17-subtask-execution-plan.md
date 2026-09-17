---
description: "Canonical Superpowers implementation plan for the Subtask execution mode of the Workflow WPF app"
summary: "18 TDD tasks that add opt-in Subtask mode to phase 4. Order matters: models (SubtaskPaths, SubtaskLedger, WorkflowDirectoryValidation) -> persistence (settings MRU, journal fields, PhaseReconciliation) -> the two prompt-file corrections and the task_template change (tasks 7 and 8, which MUST precede task 9's stricter ValidateAll or Start workflow dies on every tab) -> prompt tokens -> the behaviour-preserving RunSessionAsync extraction -> the subtask run plumbing -> the decomposition step and the loop -> the indicator, the tab wiring and the XAML -> removal of 'Phase abschliessen' and the tab-closing 'Task abschliessen' -> the acceptance gate. Task 7 edits Workflow/Prompt/*.md and task 8 edits the separate Workflows repo; both are marked EXTERNAL and carry their full target text."
paths:
  - "../specs/2026-09-17-subtask-execution-design.md"
  - "../specs/specification.md"
  - "../specs/2026-09-14-workflow-resume-design.md"
---

# Subtask Execution Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give phase 4 (*Implementierung*) an opt-in Subtask mode that decomposes the approved plan into per-subtask Claude sessions, driven entirely by flag files, with live progress and crash recovery.

**Architecture:** A new `SubtaskPaths`/`SubtaskLedger` pair owns every path and every status read inside the separate Workflows tracking repository. `WorkflowOrchestrator` gains a second phase-4 branch that first runs one decomposition session (`create_subtasks.md`) and then loops one fresh session per subtask (`run_subtask.md`), each ended by a non-empty `result.json` flag. Disk is the only source of truth for progress; the journal stores only *whether* subtask mode is on and *which* workflow directory it uses.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF, CommunityToolkit.Mvvm 8.4.2 source generators, MaterialDesignThemes 5.3.2, MahApps.Metro 2.4.11, xunit 2.9.2 + Xunit.StaFact 1.1.11, `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-09-17-subtask-execution-design.md` — read it alongside this plan. Section references below (e.g. "SPEC §7.1") point into it.

---

## Global Constraints

Copied verbatim from the spec and the build configuration. **Every task's requirements implicitly include this section.**

- **Warnings are errors.** `Workflow/.roslyn` sets `TreatWarningsAsErrors=true`, `AnalysisMode=All`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild=true`, `Nullable=enable`, `GenerateDocumentationFile=true`. A build warning fails the build.
- **XML doc comments on every public member**, including every interface member. `IDE0040` requires the explicit `public` modifier on interface members (the existing interfaces all write `public Task …`).
- **No public `List<T>`** (CA1002). Use `Collection<T>` for settable JSON-bound collections and `IReadOnlyList<T>` for returns.
- **`var` is mandatory** where the type is apparent or built-in (`csharp_style_var_*` = `error`).
- **Private fields start with `_`**; accessibility modifiers are always explicit.
- **No broad `catch (Exception)`.** CA1031 is deliberately *not* in the repo-wide `NoWarn`. Catch the specific types (`IOException`, `UnauthorizedAccessException`, `JsonException`, `ArgumentException`).
- **Language and text:** all user-visible strings are **German**. There is no resource table (CA1303 is suppressed repo-wide).
- **File encoding:** UTF-8. Existing C# files carry a BOM; keep whatever the file already has.
- **Test conventions:** xunit; test names are `Method_Condition_Expectation`; tests use real temp directories under `Path.GetTempPath()` with a `Guid.NewGuid().ToString("N")` suffix and clean up in `Dispose`; view-model tests that touch a `Dispatcher` use `[WpfFact]` from `Xunit.StaFact`. `Workflow.Tests.csproj` already suppresses `CA1707;CA1822;CA2007;CA1303;CA1861` for test code.
- **Naming on disk (binding):** the flag file is `result.json` — **singular** — and it must be **non-empty** (`ArtifactWatcher` with `CompletionRule.FilesExist` requires `info.Length > 0`). The create-flag lives at `{workflow_path}\{TaskName}\result.json`, never at the repository root.
- **Token syntax in prompt templates (binding):** `{name}` is reserved for values the *application* substitutes; any name the CLI invents for itself uses `<name>`. The token regex is `\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}`.
- **`{subtask_path}` is relative to `{workflow_path}`** and equals `{TaskName}\subtasks`.
- **Journal schema stays at `TaskState.CurrentVersion = 1`.** New fields are additive only.
- **`PhaseStatus` stays three-valued** (`Pending`, `Active`, `Completed`). No fourth value.
- **No timeouts.** A missing flag file means waiting indefinitely with a live terminal.
- **Commit after every task**, with the trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

### Build and test commands

```bash
dotnet build C:/Users/Marco/Documents/repo/Workflow/Workflow.sln -c Debug
dotnet test  C:/Users/Marco/Documents/repo/Workflow/Workflow.sln -c Debug
dotnet test  C:/Users/Marco/Documents/repo/Workflow/Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskPathsTests"
```

The acceptance gate is `powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\Marco\Documents\repo\Workflow\Workflow\verify.ps1`, which must exit 0.

---

## File Structure

### New production files

| File | Responsibility |
|------|----------------|
| `Workflow/Models/SubtaskPaths.cs` | Every path inside the Workflows repository, plus `IsValidTitle`. The only place these paths are composed. |
| `Workflow/Models/SubtaskStatus.cs` | `SubtaskStatus` enum, `SubtaskState`, `SubtaskSnapshot`, `SubtaskStage`, `SubtaskProgress`. Pure data. |
| `Workflow/Models/SubtaskLedger.cs` | The only code that interprets `result.json`, `status.json` and `subtask.md`. Static, file-system only, never throws. |
| `Workflow/Models/WorkflowDirectoryValidation.cs` | Validates that a directory is the checked-out Workflows repository. |
| `Workflow/Models/PromptTemplateCatalog.cs` | Template file names and the tokens each template may use. |
| `Workflow/Services/ISubtaskConfiguration.cs` | `ISubtaskConfiguration` + the thread-safe `SubtaskConfiguration` holder. |
| `Workflow/Services/SubtaskConfigurationException.cs` | The one exception type the subtask phase raises for user-fixable problems. |
| `Workflow/ViewModels/SubtaskIndicatorViewModel.cs` | Presentation state of the second indicator. |
| `Workflow/Views/SubtaskIndicatorView.xaml` (+ `.xaml.cs`) | The second indicator's visual. |

### Modified production files

| File | Change |
|------|--------|
| `Workflow/Models/AppSettings.cs` | `RecentWorkflowDirectories`, `LastWorkflowDirectory`. |
| `Workflow/Services/ISettingsService.cs`, `SettingsService.cs` | `AddRecentWorkflowDirectory`; the MRU loop extracted into one shared `Promote` helper. |
| `Workflow/Models/TaskState.cs` | `SubtasksEnabled`, `WorkflowDirectory`. |
| `Workflow/Services/ITaskStateStore.cs`, `TaskStateStore.cs` | `SaveSubtaskSettings` + DTO fields. |
| `Workflow/Models/PhaseReconciliation.cs` | Implementation's artefact evidence is the ledger in subtask mode. |
| `Workflow/Services/PromptTemplateService.cs` | Three `PromptVariables` builders; `ValidateAll` over `PromptTemplateCatalog` with per-file token sets. |
| `Workflow/Services/IPromptTemplateService.cs` | Unchanged signature; doc comment updated. |
| `Workflow/Services/IDirectoryPickerService.cs`, `DirectoryPickerService.cs` | Optional `title` parameter. |
| `Workflow/Services/IWorkflowOrchestrator.cs` | `WorkflowRunRequest` gains `Subtasks` and `SubtaskProgress`. |
| `Workflow/Services/WorkflowOrchestrator.cs` | `RunSessionAsync` extraction; `RunSubtaskPhaseAsync`. |
| `Workflow/ViewModels/TaskTabViewModel.cs` | Checkbox, workflow directory, indicator, resume, command changes. |
| `Workflow/ViewModels/PhaseIndicatorViewModel.cs` | `IsActive` removed. |
| `Workflow/Views/PhaseIndicatorView.xaml` | `Phase abschliessen` button removed. |
| `Workflow/Views/TaskTabView.xaml` | Checkbox, indicator, workflow-directory row. |

### External files (separate, explicitly marked tasks)

| File | Task |
|------|------|
| `Workflow/Prompt/create_subtasks.md`, `Workflow/Prompt/run_subtask.md` | Task 7 — **PROMPT CHANGE** |
| `C:\Users\Marco\Documents\repo\Workflows\task_template\**` | Task 8 — **EXTERNAL REPO** |

### New test files

`Workflow.Tests/SubtaskPathsTests.cs`, `SubtaskLedgerTests.cs`, `WorkflowDirectoryValidationTests.cs`, `PromptTemplateCatalogTests.cs`, `SubtaskConfigurationTests.cs`, `SubtaskOrchestratorTests.cs`, `SubtaskIndicatorViewModelTests.cs`; additions to `SettingsServiceTests.cs`, `TaskStateStoreTests.cs`, `PhaseCatalogTests.cs`'s neighbours, `PromptTemplateServiceTests.cs`, `TaskTabViewModelTests.cs`, `WorkflowOrchestratorTests.cs`, `Fakes/FakeTaskStateStore.cs`.

---

## Task Dependency Order

```
1  SubtaskPaths
2  SubtaskLedger            (needs 1)
3  WorkflowDirectoryValidation
4  Settings MRU
5  Journal fields
6  PhaseReconciliation      (needs 2, 5)
7  PROMPT CHANGE            (independent of code; MUST precede 9)
8  EXTERNAL REPO            (independent of code)
9  Prompt tokens + ValidateAll  (needs 1, 7)
10 RunSessionAsync refactor (behaviour-preserving, no new behaviour)
11 Run plumbing             (needs 1)
12 Decomposition step       (needs 2, 9, 10, 11)
13 Subtask loop             (needs 12)
14 SubtaskIndicator VM+View (needs 11)
15 TaskTabViewModel wiring  (needs 2, 3, 4, 5, 11, 14)
16 TaskTabView.xaml         (needs 14, 15)
17 Manual-exit changes      (needs 15)
18 Acceptance gate          (needs all)
```

---

## Task 1: `SubtaskPaths` — the path model for the Workflows repository

**Files:**
- Create: `Workflow/Models/SubtaskPaths.cs`
- Test: `Workflow.Tests/SubtaskPathsTests.cs`

**Interfaces:**
- Consumes: `Workflow.Models.WorkingDirectoryPath.Normalise(string)` — existing, root-aware path normalisation (`Workflow/Models/WorkingDirectoryPath.cs`).
- Produces: `SubtaskPaths` with properties `WorkflowDirectory`, `TaskName`, `TaskDirectory`, `ResultAbsolute`, `SubtasksDirectory`, `TemplateDirectory`, `SubtaskPathToken`; methods `SubtaskDirectory(string)`, `SubtaskMarkdown(string)`, `SubtaskStatusFile(string)`, `SubtaskResultFile(string)`; and `public static bool IsValidTitle(string?)`. Also `public const string TemplateFolderName = "task_template"`.

**Context:** Mirrors the existing `Workflow/Models/TaskPaths.cs` — read that file first. `TaskPaths` is the single source of truth for paths under the *Arbeitsverzeichnis*; `SubtaskPaths` is the single source of truth for paths under the *Workflows tracking repository* (`C:\Users\Marco\Documents\repo\Workflows`). Nothing else may compose those paths by hand. SPEC §5.2.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/SubtaskPathsTests.cs`:

```csharp
using System.IO;
using Workflow.Models;

namespace Workflow.Tests;

public sealed class SubtaskPathsTests
{
    private const string Root = @"C:\repo\Workflows";

    [Fact]
    public void Constructor_ComposesEveryPathFromTheRootAndTheTaskName()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.Equal(Root, paths.WorkflowDirectory);
        Assert.Equal("demo", paths.TaskName);
        Assert.Equal(@"C:\repo\Workflows\demo", paths.TaskDirectory);
        Assert.Equal(@"C:\repo\Workflows\demo\result.json", paths.ResultAbsolute);
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks", paths.SubtasksDirectory);
        Assert.Equal(@"C:\repo\Workflows\task_template", paths.TemplateDirectory);
    }

    [Fact]
    public void SubtaskPathToken_IsRelativeToTheWorkflowDirectory()
    {
        var paths = new SubtaskPaths(Root, "demo");

        // run_subtask.md composes {workflow_path}\{subtask_path}\{subtask_title}. An absolute
        // {subtask_path} would double-prefix every path in that prompt. SPEC section 5.3.
        Assert.Equal(@"demo\subtasks", paths.SubtaskPathToken);
        Assert.Equal(
            paths.SubtaskDirectory("ST-001"),
            Path.Combine(paths.WorkflowDirectory, paths.SubtaskPathToken, "ST-001"));
    }

    [Fact]
    public void SubtaskFileAccessors_PointIntoTheSubtaskFolder()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001", paths.SubtaskDirectory("ST-001"));
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001\subtask.md", paths.SubtaskMarkdown("ST-001"));
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001\status.json", paths.SubtaskStatusFile("ST-001"));
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001\result.json", paths.SubtaskResultFile("ST-001"));
    }

    [Fact]
    public void Constructor_NormalisesTheRootAndTrimsTheTaskName()
    {
        var paths = new SubtaskPaths(@"C:\repo\Workflows\", "  demo  ");

        Assert.Equal(@"C:\repo\Workflows", paths.WorkflowDirectory);
        Assert.Equal("demo", paths.TaskName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankRoot(string? root) =>
        Assert.Throws<ArgumentException>(() => new SubtaskPaths(root!, "demo"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankTaskName(string? taskName) =>
        Assert.Throws<ArgumentException>(() => new SubtaskPaths(Root, taskName!));

    [Theory]
    [InlineData("ST-001-taskpaths")]
    [InlineData("ST_002")]
    [InlineData("a")]
    public void IsValidTitle_AcceptsAPlainSegment(string title) =>
        Assert.True(SubtaskPaths.IsValidTitle(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\..\Windows")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("C:")]
    [InlineData("has:colon")]
    [InlineData("has|pipe")]
    public void IsValidTitle_RejectsAnythingThatCouldEscapeTheRepository(string? title) =>
        Assert.False(SubtaskPaths.IsValidTitle(title));

    [Fact]
    public void SubtaskDirectory_RejectsAnInvalidTitle() =>
        Assert.Throws<ArgumentException>(() => new SubtaskPaths(Root, "demo").SubtaskDirectory(@"..\x"));
}
```

Note: `UseWPF` strips `System.IO` from the implicit usings, which is why the explicit
`using System.IO;` is present (the same reason every production file in this repo has it).

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskPathsTests"`
Expected: compile error, `CS0246: The type or namespace name 'SubtaskPaths' could not be found`.

- [ ] **Step 3: Implement `SubtaskPaths`**

Create `Workflow/Models/SubtaskPaths.cs`:

```csharp
using System.IO;

namespace Workflow.Models;

/// <summary>
/// Every path inside the Workflows tracking repository, derived from that repository's root and a
/// task name. This is the single source of truth for those locations, exactly as
/// <see cref="TaskPaths"/> is for the working directory; nothing else may compose them by hand.
/// </summary>
public sealed class SubtaskPaths
{
    /// <summary>Name of the folder that identifies a checked-out Workflows repository.</summary>
    public const string TemplateFolderName = "task_template";

    private static readonly char[] Separators = ['\\', '/', ':'];

    /// <summary>Creates the path set.</summary>
    /// <param name="workflowDirectory">Root of the Workflows repository. Normalised by <see cref="WorkingDirectoryPath.Normalise"/>.</param>
    /// <param name="taskName">Task name (Taskbezeichnung). Trimmed.</param>
    public SubtaskPaths(string workflowDirectory, string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        WorkflowDirectory = WorkingDirectoryPath.Normalise(workflowDirectory);
        TaskName = taskName.Trim();

        TaskDirectory = Path.Combine(WorkflowDirectory, TaskName);
        ResultAbsolute = Path.Combine(TaskDirectory, "result.json");
        SubtasksDirectory = Path.Combine(TaskDirectory, "subtasks");
        TemplateDirectory = Path.Combine(WorkflowDirectory, TemplateFolderName);

        // RELATIVE on purpose: run_subtask.md composes
        // {workflow_path}\{subtask_path}\{subtask_title}, so an absolute value here would
        // double-prefix every path in that prompt.
        SubtaskPathToken = Path.Combine(TaskName, "subtasks");
    }

    /// <summary>Root of the Workflows repository; also the value of the {workflow_path} token.</summary>
    public string WorkflowDirectory { get; }

    /// <summary>The task name; also the value of the {tasktitel} token.</summary>
    public string TaskName { get; }

    /// <summary>Absolute path of this task's tracking folder.</summary>
    public string TaskDirectory { get; }

    /// <summary>Absolute path of the index and create-flag.</summary>
    public string ResultAbsolute { get; }

    /// <summary>Absolute path of the folder holding one directory per subtask.</summary>
    public string SubtasksDirectory { get; }

    /// <summary>Absolute path of the reference template folder. Read only to validate the root.</summary>
    public string TemplateDirectory { get; }

    /// <summary>Value substituted for the {subtask_path} token, relative to <see cref="WorkflowDirectory"/>.</summary>
    public string SubtaskPathToken { get; }

    /// <summary>Reports whether a subtask title is a single, safe path segment.</summary>
    /// <param name="title">The candidate title, as read from the index.</param>
    /// <returns>True when the title may be used to compose a path.</returns>
    /// <remarks>
    /// The titles come from JSON written by an AI agent and are used to compose paths that the
    /// application deletes files from. A title such as <c>..\..\Windows</c> would otherwise reach
    /// outside the repository.
    /// </remarks>
    public static bool IsValidTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();

        return trimmed is not ("." or "..")
            && trimmed.IndexOfAny(Separators) < 0
            && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Absolute path of one subtask's folder.</summary>
    /// <param name="title">The subtask title. Must satisfy <see cref="IsValidTitle"/>.</param>
    /// <returns>The folder path.</returns>
    /// <exception cref="ArgumentException">The title is not a single, safe path segment.</exception>
    public string SubtaskDirectory(string title)
    {
        if (!IsValidTitle(title))
        {
            throw new ArgumentException($"Ungültiger Subtask-Name: '{title}'.", nameof(title));
        }

        return Path.Combine(SubtasksDirectory, title.Trim());
    }

    /// <summary>Absolute path of one subtask's description file.</summary>
    /// <param name="title">The subtask title.</param>
    /// <returns>The path of subtask.md.</returns>
    public string SubtaskMarkdown(string title) => Path.Combine(SubtaskDirectory(title), "subtask.md");

    /// <summary>Absolute path of one subtask's status payload.</summary>
    /// <param name="title">The subtask title.</param>
    /// <returns>The path of status.json.</returns>
    public string SubtaskStatusFile(string title) => Path.Combine(SubtaskDirectory(title), "status.json");

    /// <summary>Absolute path of one subtask's completion flag.</summary>
    /// <param name="title">The subtask title.</param>
    /// <returns>The path of result.json.</returns>
    public string SubtaskResultFile(string title) => Path.Combine(SubtaskDirectory(title), "result.json");
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskPathsTests"`
Expected: PASS, 0 failed.

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build Workflow.sln -c Debug`
Expected: `0 Warning(s)`, `0 Error(s)`. A warning is a build failure here (`TreatWarningsAsErrors`).

- [ ] **Step 6: Commit**

```bash
git add Workflow/Models/SubtaskPaths.cs Workflow.Tests/SubtaskPathsTests.cs
git commit -m "feat(subtasks): add SubtaskPaths as the path model for the Workflows repo

Mirrors TaskPaths for the separate tracking repository. SubtaskPathToken
is deliberately relative so the composition in run_subtask.md resolves,
and IsValidTitle keeps an AI-written title from composing a path outside
the repository.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `SubtaskLedger` — the only reader of the on-disk contracts

**Files:**
- Create: `Workflow/Models/SubtaskStatus.cs`
- Create: `Workflow/Models/SubtaskLedger.cs`
- Test: `Workflow.Tests/SubtaskLedgerTests.cs`

**Interfaces:**
- Consumes: `SubtaskPaths` (Task 1).
- Produces:
  - `public enum SubtaskStatus { Pending, Complete, Failed }`
  - `public sealed record SubtaskState(string Title, SubtaskStatus Status, string? FailReason)`
  - `public sealed record SubtaskSnapshot(IReadOnlyList<SubtaskState> Subtasks, int Total, int Completed, int Failed)`
  - `public enum SubtaskStage { Idle, Decomposing, Running, Finished }`
  - `public sealed record SubtaskProgress(SubtaskStage Stage, int Completed, int Total, int Failed, string? CurrentTitle)`
  - `public static class SubtaskLedger` with `SubtaskSnapshot? TryRead(SubtaskPaths)`, `bool IsDecomposed(SubtaskPaths)`, `bool AllComplete(SubtaskPaths)`.

**Context:** This is the only code in the application that interprets `result.json`, `status.json`
and `subtask.md`. Tasks 6, 12, 13 and 15 all call it, so one shared definition of "complete" is
what keeps them from disagreeing. Modelled on `Workflow/Models/PhaseReconciliation.cs`: a static
class doing direct file I/O, with every read wrapped so **it never throws**. SPEC §6 and §7.

The file shapes it reads (SPEC §6):

```
{workflow_path}\{TaskName}\result.json
{ "version": 1, "task": "demo", "subtasks": ["ST-001-a", "ST-002-b"] }

{workflow_path}\{TaskName}\subtasks\ST-001-a\status.json
{ "subtask": "ST-001", "status": "complete", "failreason": null, "testsPassed": true }
```

Only `subtasks` (from the index) and `status` / `failreason` (from the payload) are read. Every
other property is ignored on purpose — reading more would create a second, undocumented
completion rule.

**Status derivation table (SPEC §7.1), evaluated top to bottom.** `R` = the subtask's
`result.json`, `S` = its `status.json`.

| Condition | Result |
|-----------|--------|
| title fails `SubtaskPaths.IsValidTitle` | `Failed("Ungültiger Subtask-Name: '<title>'.")` |
| `S` exists, parses, `status` equals `complete` (ignoring case) | `Complete` |
| `S` exists, parses, `status` is anything else | `Failed(failreason ?? $"status = {status}")` |
| `S` exists but is unreadable or invalid JSON | `Failed("status.json ist unlesbar oder kein gültiges JSON.")` |
| `S` absent and `R` exists and is non-empty | `Failed("result.json wurde ohne status.json geschrieben.")` |
| `subtask.md` is absent or empty | `Failed("subtask.md fehlt oder ist leer.")` |
| otherwise | `Pending` |

The `subtask.md` row sits **after** the `Complete` row deliberately: a subtask that already
reported `complete` is finished, and a `subtask.md` deleted afterwards must not demote it back
into the loop.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/SubtaskLedgerTests.cs`:

```csharp
using System.IO;
using Workflow.Models;

namespace Workflow.Tests;

public sealed class SubtaskLedgerTests : IDisposable
{
    private readonly string _root;
    private readonly SubtaskPaths _paths;

    public SubtaskLedgerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new SubtaskPaths(_root, "demo");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteIndex(params string[] titles)
    {
        Directory.CreateDirectory(_paths.TaskDirectory);
        var list = string.Join(", ", titles.Select(t => "\"" + t + "\""));
        File.WriteAllText(_paths.ResultAbsolute, $$"""{"version":1,"task":"demo","subtasks":[{{list}}]}""");
    }

    private void WriteSubtask(string title, string? status = null, string? failReason = null, bool markdown = true, bool flag = false)
    {
        Directory.CreateDirectory(_paths.SubtaskDirectory(title));

        if (markdown)
        {
            File.WriteAllText(_paths.SubtaskMarkdown(title), "# " + title + "\n\nBeschreibung.");
        }

        if (status is not null)
        {
            var reason = failReason is null ? "null" : "\"" + failReason + "\"";
            File.WriteAllText(
                _paths.SubtaskStatusFile(title),
                $$"""{"subtask":"{{title}}","status":"{{status}}","failreason":{{reason}}}""");
        }

        if (flag)
        {
            File.WriteAllText(_paths.SubtaskResultFile(title), "done");
        }
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenTheIndexIsAbsent() =>
        Assert.Null(SubtaskLedger.TryRead(_paths));

    [Fact]
    public void TryRead_ReturnsNull_WhenTheIndexIsNotValidJson()
    {
        Directory.CreateDirectory(_paths.TaskDirectory);
        File.WriteAllText(_paths.ResultAbsolute, "{ not json");

        Assert.Null(SubtaskLedger.TryRead(_paths));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenTheSubtasksArrayIsMissingOrEmpty()
    {
        Directory.CreateDirectory(_paths.TaskDirectory);
        File.WriteAllText(_paths.ResultAbsolute, """{"version":1,"task":"demo"}""");
        Assert.Null(SubtaskLedger.TryRead(_paths));

        File.WriteAllText(_paths.ResultAbsolute, """{"version":1,"subtasks":[]}""");
        Assert.Null(SubtaskLedger.TryRead(_paths));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenTheIndexDeclaresANewerSchema()
    {
        Directory.CreateDirectory(_paths.TaskDirectory);
        File.WriteAllText(_paths.ResultAbsolute, """{"version":2,"subtasks":["ST-001"]}""");

        Assert.Null(SubtaskLedger.TryRead(_paths));
    }

    [Fact]
    public void TryRead_PreservesTheDeclaredOrder()
    {
        WriteIndex("ST-003-c", "ST-001-a", "ST-002-b");
        WriteSubtask("ST-003-c");
        WriteSubtask("ST-001-a");
        WriteSubtask("ST-002-b");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(["ST-003-c", "ST-001-a", "ST-002-b"], snapshot.Subtasks.Select(s => s.Title));
    }

    [Fact]
    public void TryRead_CountsCompleteAndFailed()
    {
        WriteIndex("a", "b", "c", "d");
        WriteSubtask("a", status: "complete");
        WriteSubtask("b", status: "failed", failReason: "logic error");
        WriteSubtask("c");                                  // pending
        WriteSubtask("d", status: "complete");

        var snapshot = SubtaskLedger.TryRead(_paths);

        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot.Total);
        Assert.Equal(2, snapshot.Completed);
        Assert.Equal(1, snapshot.Failed);
    }

    [Fact]
    public void TryRead_TreatsCompleteCaseInsensitively()
    {
        WriteIndex("a");
        WriteSubtask("a", status: "COMPLETE");

        Assert.Equal(SubtaskStatus.Complete, SubtaskLedger.TryRead(_paths)!.Subtasks[0].Status);
    }

    [Fact]
    public void TryRead_CarriesTheFailReason()
    {
        WriteIndex("a");
        WriteSubtask("a", status: "failed", failReason: "clarification needed");

        var state = SubtaskLedger.TryRead(_paths)!.Subtasks[0];

        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.Equal("clarification needed", state.FailReason);
    }

    [Fact]
    public void TryRead_FallsBackToTheStatusValue_WhenNoFailReasonIsGiven()
    {
        WriteIndex("a");
        WriteSubtask("a", status: "blocked");

        Assert.Contains("blocked", SubtaskLedger.TryRead(_paths)!.Subtasks[0].FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_ReportsAnUnreadableStatusFileAsFailed()
    {
        WriteIndex("a");
        WriteSubtask("a");
        File.WriteAllText(_paths.SubtaskStatusFile("a"), "{ not json");

        Assert.Equal(SubtaskStatus.Failed, SubtaskLedger.TryRead(_paths)!.Subtasks[0].Status);
    }

    [Fact]
    public void TryRead_ReportsAFlagWithoutAStatusFileAsFailed()
    {
        WriteIndex("a");
        WriteSubtask("a", flag: true);              // result.json but no status.json

        var state = SubtaskLedger.TryRead(_paths)!.Subtasks[0];

        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.Contains("status.json", state.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_ReportsAMissingSubtaskMarkdownAsFailed()
    {
        WriteIndex("a");
        WriteSubtask("a", markdown: false);

        var state = SubtaskLedger.TryRead(_paths)!.Subtasks[0];

        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.Contains("subtask.md", state.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_DoesNotDemoteACompleteSubtaskWhoseMarkdownWasDeleted()
    {
        WriteIndex("a");
        WriteSubtask("a", status: "complete", markdown: false);

        Assert.Equal(SubtaskStatus.Complete, SubtaskLedger.TryRead(_paths)!.Subtasks[0].Status);
    }

    [Fact]
    public void TryRead_ReportsAnUnsafeTitleAsFailedWithoutTouchingTheFileSystem()
    {
        WriteIndex(@"..\..\Windows");

        var state = SubtaskLedger.TryRead(_paths)!.Subtasks[0];

        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.Contains("Ungültiger Subtask-Name", state.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_ReportsAnUntouchedSubtaskAsPending()
    {
        WriteIndex("a");
        WriteSubtask("a");

        Assert.Equal(SubtaskStatus.Pending, SubtaskLedger.TryRead(_paths)!.Subtasks[0].Status);
    }

    [Fact]
    public void IsDecomposed_IsTrue_OnlyWhenEveryListedSubtaskHasANonEmptyMarkdown()
    {
        WriteIndex("a", "b");
        WriteSubtask("a");
        Assert.False(SubtaskLedger.IsDecomposed(_paths));

        WriteSubtask("b");
        Assert.True(SubtaskLedger.IsDecomposed(_paths));
    }

    [Fact]
    public void IsDecomposed_IsFalse_WhenThereIsNoIndex() =>
        Assert.False(SubtaskLedger.IsDecomposed(_paths));

    [Fact]
    public void AllComplete_IsTrue_OnlyWhenEverySubtaskIsComplete()
    {
        WriteIndex("a", "b");
        WriteSubtask("a", status: "complete");
        WriteSubtask("b");
        Assert.False(SubtaskLedger.AllComplete(_paths));

        WriteSubtask("b", status: "complete");
        Assert.True(SubtaskLedger.AllComplete(_paths));
    }

    [Fact]
    public void AllComplete_IsFalse_WhenThereIsNoIndex() =>
        Assert.False(SubtaskLedger.AllComplete(_paths));
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskLedgerTests"`
Expected: compile error, `CS0103`/`CS0246` for `SubtaskLedger` and `SubtaskStatus`.

- [ ] **Step 3: Implement the data types**

Create `Workflow/Models/SubtaskStatus.cs`:

```csharp
namespace Workflow.Models;

/// <summary>What the ledger knows about one subtask.</summary>
public enum SubtaskStatus
{
    /// <summary>Not started, or started and not yet finished.</summary>
    Pending,

    /// <summary>The subtask reported status = complete.</summary>
    Complete,

    /// <summary>The subtask reported any other status, or violated the handshake.</summary>
    Failed,
}

/// <summary>How far the subtask phase has got. Drives the second indicator.</summary>
public enum SubtaskStage
{
    /// <summary>The subtask phase has not begun.</summary>
    Idle,

    /// <summary>The create_subtasks.md session is running.</summary>
    Decomposing,

    /// <summary>The per-subtask loop is running.</summary>
    Running,

    /// <summary>The loop has attempted every subtask.</summary>
    Finished,
}

/// <summary>One subtask's derived state.</summary>
/// <param name="Title">The subtask folder name, as listed in the index.</param>
/// <param name="Status">The derived status.</param>
/// <param name="FailReason">Why it failed, or null.</param>
public sealed record SubtaskState(string Title, SubtaskStatus Status, string? FailReason);

/// <summary>The whole ledger at one moment, in the index's declared order.</summary>
/// <param name="Subtasks">Every listed subtask, in execution order.</param>
/// <param name="Total">Number of listed subtasks.</param>
/// <param name="Completed">How many are <see cref="SubtaskStatus.Complete"/>.</param>
/// <param name="Failed">How many are <see cref="SubtaskStatus.Failed"/>.</param>
public sealed record SubtaskSnapshot(
    IReadOnlyList<SubtaskState> Subtasks,
    int Total,
    int Completed,
    int Failed);

/// <summary>A subtask-phase progress change, reported to the UI.</summary>
/// <param name="Stage">How far the phase has got.</param>
/// <param name="Completed">How many subtasks are complete.</param>
/// <param name="Total">How many subtasks there are.</param>
/// <param name="Failed">How many subtasks failed.</param>
/// <param name="CurrentTitle">The subtask being executed, or null.</param>
public sealed record SubtaskProgress(
    SubtaskStage Stage,
    int Completed,
    int Total,
    int Failed,
    string? CurrentTitle);
```

- [ ] **Step 4: Implement `SubtaskLedger`**

Create `Workflow/Models/SubtaskLedger.cs`:

```csharp
using System.IO;
using System.Text.Json;

namespace Workflow.Models;

/// <summary>
/// The only code that interprets the Workflows repository's on-disk contracts: the task-level
/// index (result.json), each subtask's payload (status.json) and each subtask's description
/// (subtask.md).
/// </summary>
/// <remarks>
/// One shared reader rather than a private helper on each caller: the orchestrator, the tab and
/// <see cref="PhaseReconciliation"/> all need "is this subtask complete?", and three copies would
/// be free to disagree about it. Modelled on <see cref="PhaseReconciliation"/>: static, direct
/// file I/O, and no method ever throws.
/// </remarks>
public static class SubtaskLedger
{
    /// <summary>The index schema version this build understands.</summary>
    public const int SupportedIndexVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads the index and derives every subtask's status.</summary>
    /// <param name="paths">The task's path set inside the Workflows repository.</param>
    /// <returns>
    /// The snapshot, in the index's declared order; null when the index is absent, unreadable,
    /// not valid JSON, written by a newer schema, or carries no usable subtask list.
    /// </returns>
    public static SubtaskSnapshot? TryRead(SubtaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var titles = ReadIndex(paths.ResultAbsolute);
        if (titles is null || titles.Count == 0)
        {
            return null;
        }

        var states = new List<SubtaskState>(titles.Count);
        var completed = 0;
        var failed = 0;

        foreach (var title in titles)
        {
            var state = DeriveState(paths, title);
            states.Add(state);

            if (state.Status == SubtaskStatus.Complete)
            {
                completed++;
            }
            else if (state.Status == SubtaskStatus.Failed)
            {
                failed++;
            }
        }

        return new SubtaskSnapshot(states, titles.Count, completed, failed);
    }

    /// <summary>Reports whether the decomposition step has already produced usable output.</summary>
    /// <param name="paths">The task's path set inside the Workflows repository.</param>
    /// <returns>True when the index is usable and every listed subtask has a non-empty subtask.md.</returns>
    public static bool IsDecomposed(SubtaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var titles = ReadIndex(paths.ResultAbsolute);
        if (titles is null || titles.Count == 0)
        {
            return false;
        }

        return titles.All(title =>
            SubtaskPaths.IsValidTitle(title) && NonEmpty(paths.SubtaskMarkdown(title)));
    }

    /// <summary>Reports whether every listed subtask is complete.</summary>
    /// <param name="paths">The task's path set inside the Workflows repository.</param>
    /// <returns>True when the index is usable and no subtask is anything but complete.</returns>
    public static bool AllComplete(SubtaskPaths paths)
    {
        var snapshot = TryRead(paths);
        return snapshot is not null && snapshot.Completed == snapshot.Total;
    }

    private static SubtaskState DeriveState(SubtaskPaths paths, string title)
    {
        if (!SubtaskPaths.IsValidTitle(title))
        {
            return new SubtaskState(title, SubtaskStatus.Failed, $"Ungültiger Subtask-Name: '{title}'.");
        }

        var statusPath = paths.SubtaskStatusFile(title);

        if (File.Exists(statusPath))
        {
            var payload = ReadStatus(statusPath);

            if (payload is null)
            {
                return new SubtaskState(
                    title, SubtaskStatus.Failed, "status.json ist unlesbar oder kein gültiges JSON.");
            }

            if (string.Equals(payload.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                return new SubtaskState(title, SubtaskStatus.Complete, null);
            }

            var reason = string.IsNullOrWhiteSpace(payload.FailReason)
                ? $"status = {payload.Status ?? "(leer)"}"
                : payload.FailReason;

            return new SubtaskState(title, SubtaskStatus.Failed, reason);
        }

        if (NonEmpty(paths.SubtaskResultFile(title)))
        {
            return new SubtaskState(
                title, SubtaskStatus.Failed, "result.json wurde ohne status.json geschrieben.");
        }

        if (!NonEmpty(paths.SubtaskMarkdown(title)))
        {
            return new SubtaskState(title, SubtaskStatus.Failed, "subtask.md fehlt oder ist leer.");
        }

        return new SubtaskState(title, SubtaskStatus.Pending, null);
    }

    private static IReadOnlyList<string>? ReadIndex(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<IndexDto>(File.ReadAllText(path), ReadOptions);

            if (dto is null || dto.Version > SupportedIndexVersion)
            {
                return null;
            }

            return dto.Subtasks?
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim())
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static StatusDto? ReadStatus(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<StatusDto>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool NonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Only the fields the application is allowed to read. Everything else in these files -
    // openFindings, workflowRelevantFindings, testsPassed, readyForVerification, task - is for the
    // human reading the repository; deserialising it here would invite a second completion rule.
    private sealed class IndexDto
    {
        public int Version { get; set; }

        public List<string?>? Subtasks { get; set; }
    }

    private sealed class StatusDto
    {
        public string? Status { get; set; }

        public string? FailReason { get; set; }
    }
}
```

`PropertyNameCaseInsensitive = true` is what makes the JSON key `failreason` bind to
`FailReason`. `List<string?>` and `List<string>` are private nested DTO members, so CA1002 (no
public `List<T>`) does not apply.

- [ ] **Step 5: Run the tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskLedgerTests"`
Expected: PASS, 0 failed.

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build Workflow.sln -c Debug`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add Workflow/Models/SubtaskStatus.cs Workflow/Models/SubtaskLedger.cs Workflow.Tests/SubtaskLedgerTests.cs
git commit -m "feat(subtasks): add the ledger that reads result.json and status.json

One shared reader for the Workflows repository's on-disk contracts, so
the orchestrator, the tab and PhaseReconciliation cannot disagree about
what 'complete' means. Reads only 'subtasks' from the index and 'status'
plus 'failreason' from the payload; every other field is deliberately
ignored. Never throws.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `WorkflowDirectoryValidation` — is this really the Workflows repository?

**Files:**
- Create: `Workflow/Models/WorkflowDirectoryValidation.cs`
- Test: `Workflow.Tests/WorkflowDirectoryValidationTests.cs`

**Interfaces:**
- Consumes: `SubtaskPaths.TemplateFolderName` (Task 1).
- Produces: `public sealed record WorkflowDirectoryValidation(bool IsValid, string? ErrorMessage)` with `public static WorkflowDirectoryValidation Validate(string? workflowDirectory)` and `public static WorkflowDirectoryValidation Valid { get; }`.

**Context:** Mirrors `Workflow/Models/TaskNameValidation.cs` — read it first and copy its shape.
The marker is the `task_template` folder, because it is the one directory the Workflows repository
is guaranteed to contain and checking it needs no git tooling. A `.git` check was rejected: it
proves nothing about *which* repository the folder is. SPEC §8.4.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/WorkflowDirectoryValidationTests.cs`:

```csharp
using System.IO;
using Workflow.Models;

namespace Workflow.Tests;

public sealed class WorkflowDirectoryValidationTests : IDisposable
{
    private readonly string _root;

    public WorkflowDirectoryValidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-dirval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsABlankPath(string? path)
    {
        var result = WorkflowDirectoryValidation.Validate(path);

        Assert.False(result.IsValid);
        Assert.Equal("Bitte ein Workflow-Verzeichnis auswählen.", result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsAMissingDirectory()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var result = WorkflowDirectoryValidation.Validate(missing);

        Assert.False(result.IsValid);
        Assert.Contains("existiert nicht", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(missing, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsADirectoryWithoutTheTaskTemplateFolder()
    {
        var result = WorkflowDirectoryValidation.Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains("task_template", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsACheckedOutWorkflowsRepository()
    {
        Directory.CreateDirectory(Path.Combine(_root, "task_template"));

        var result = WorkflowDirectoryValidation.Validate(_root);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Validate_AcceptsATrailingSeparator()
    {
        Directory.CreateDirectory(Path.Combine(_root, "task_template"));

        Assert.True(WorkflowDirectoryValidation.Validate(_root + Path.DirectorySeparatorChar).IsValid);
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~WorkflowDirectoryValidationTests"`
Expected: compile error, `CS0246: WorkflowDirectoryValidation`.

- [ ] **Step 3: Implement the validation**

Create `Workflow/Models/WorkflowDirectoryValidation.cs`:

```csharp
using System.IO;

namespace Workflow.Models;

/// <summary>Whether a chosen directory can be used as the Workflows tracking repository.</summary>
/// <param name="IsValid">True when the directory may be used.</param>
/// <param name="ErrorMessage">German explanation when it may not, otherwise null.</param>
public sealed record WorkflowDirectoryValidation(bool IsValid, string? ErrorMessage)
{
    /// <summary>The successful result.</summary>
    public static WorkflowDirectoryValidation Valid { get; } = new(true, null);

    /// <summary>Checks a candidate workflow directory.</summary>
    /// <param name="workflowDirectory">The path the user picked, or null.</param>
    /// <returns>A validation result carrying a German message on failure.</returns>
    /// <remarks>
    /// The marker is the <c>task_template</c> folder: it is the one directory the Workflows
    /// repository is guaranteed to contain, and checking it needs no git tooling. A <c>.git</c>
    /// check would prove the folder is *a* repository, not that it is *this* one.
    /// </remarks>
    public static WorkflowDirectoryValidation Validate(string? workflowDirectory)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory))
        {
            return new WorkflowDirectoryValidation(false, "Bitte ein Workflow-Verzeichnis auswählen.");
        }

        var normalised = WorkingDirectoryPath.Normalise(workflowDirectory);

        if (!Directory.Exists(normalised))
        {
            return new WorkflowDirectoryValidation(
                false, $"Das Workflow-Verzeichnis existiert nicht: {normalised}");
        }

        if (!Directory.Exists(Path.Combine(normalised, SubtaskPaths.TemplateFolderName)))
        {
            return new WorkflowDirectoryValidation(
                false,
                $"'{normalised}' sieht nicht wie das ausgecheckte Workflows-Repository aus "
                + $"(der Ordner '{SubtaskPaths.TemplateFolderName}' fehlt).");
        }

        return Valid;
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~WorkflowDirectoryValidationTests"`
Expected: PASS.

- [ ] **Step 5: Build**

Run: `dotnet build Workflow.sln -c Debug` — Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add Workflow/Models/WorkflowDirectoryValidation.cs Workflow.Tests/WorkflowDirectoryValidationTests.cs
git commit -m "feat(subtasks): validate the chosen workflow directory

Uses the task_template folder as the marker for a checked-out Workflows
repository: guaranteed to be there, and no git tooling needed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Persist the workflow directory in `settings.json`

**Files:**
- Modify: `Workflow/Models/AppSettings.cs`
- Modify: `Workflow/Services/ISettingsService.cs`
- Modify: `Workflow/Services/SettingsService.cs`
- Test: `Workflow.Tests/SettingsServiceTests.cs` (add cases)

**Interfaces:**
- Produces: `AppSettings.RecentWorkflowDirectories` (`Collection<string>`), `AppSettings.LastWorkflowDirectory` (`string?`), and `ISettingsService.AddRecentWorkflowDirectory(string directory)`.

**Context:** `SettingsService.AddRecentDirectory` already normalises through
`WorkingDirectoryPath.Normalise`, removes case-insensitive duplicates, inserts at index 0 and caps
at `MaxRecentDirectories = 15`. The new method needs *exactly* that behaviour for a second list.
**DRY: extract the loop into one private static helper and make both methods call it** — do not
copy it. SPEC §8.2.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/SettingsServiceTests.cs` (keep the existing fixture fields and helpers):

```csharp
    [Fact]
    public void AddRecentWorkflowDirectory_PromotesToTheFrontAndSetsLast()
    {
        var service = new SettingsService(_path);

        service.AddRecentWorkflowDirectory(@"C:\repo\Workflows");
        service.AddRecentWorkflowDirectory(@"C:\other");
        service.AddRecentWorkflowDirectory(@"C:\repo\Workflows");

        Assert.Equal([@"C:\repo\Workflows", @"C:\other"], service.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\repo\Workflows", service.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void AddRecentWorkflowDirectory_IsCaseInsensitiveAndRootAware()
    {
        var service = new SettingsService(_path);

        service.AddRecentWorkflowDirectory(@"C:\repo\Workflows");
        service.AddRecentWorkflowDirectory(@"c:\REPO\workflows\");

        Assert.Single(service.Settings.RecentWorkflowDirectories);
    }

    [Fact]
    public void AddRecentWorkflowDirectory_IgnoresBlankInput()
    {
        var service = new SettingsService(_path);

        service.AddRecentWorkflowDirectory("   ");

        Assert.Empty(service.Settings.RecentWorkflowDirectories);
        Assert.Null(service.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void AddRecentWorkflowDirectory_DoesNotDisturbTheWorkingDirectoryList()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\code");
        service.AddRecentWorkflowDirectory(@"C:\repo\Workflows");

        Assert.Equal([@"C:\code"], service.Settings.RecentDirectories);
        Assert.Equal(@"C:\code", service.Settings.LastDirectory);
        Assert.Equal([@"C:\repo\Workflows"], service.Settings.RecentWorkflowDirectories);
    }

    [Fact]
    public void Save_RoundTripsTheWorkflowDirectories()
    {
        var first = new SettingsService(_path);
        first.AddRecentWorkflowDirectory(@"C:\repo\Workflows");
        first.Save();

        var second = new SettingsService(_path);

        Assert.Equal([@"C:\repo\Workflows"], second.Settings.RecentWorkflowDirectories);
        Assert.Equal(@"C:\repo\Workflows", second.Settings.LastWorkflowDirectory);
    }

    [Fact]
    public void Load_TreatsAPreExistingFileWithoutTheNewFieldsAsEmpty()
    {
        // A settings.json written by a build from before subtask mode existed.
        File.WriteAllText(_path, """{"recentDirectories":["C:\\code"],"lastDirectory":"C:\\code"}""");

        var service = new SettingsService(_path);

        Assert.Equal([@"C:\code"], service.Settings.RecentDirectories);
        Assert.Empty(service.Settings.RecentWorkflowDirectories);
        Assert.Null(service.Settings.LastWorkflowDirectory);
    }
```

If `SettingsServiceTests` does not already have a `_path` field pointing at a temp
`settings.json`, mirror whatever the existing fixture does — do not introduce a second pattern.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SettingsServiceTests"`
Expected: compile error — `AddRecentWorkflowDirectory` and `RecentWorkflowDirectories` do not exist.

- [ ] **Step 3: Add the two settings properties**

In `Workflow/Models/AppSettings.cs`, after `LastDirectory`:

```csharp
    /// <summary>Workflow (tracking-repository) directories the user has picked, most recent first.</summary>
    /// <remarks>
    /// Separate from <see cref="RecentDirectories"/>: the two lists hold different kinds of path
    /// and a user who picks one must not see the other's history offered.
    /// </remarks>
    public Collection<string> RecentWorkflowDirectories { get; set; } = [];

    /// <summary>The workflow directory preselected when subtask mode is switched on, or null.</summary>
    public string? LastWorkflowDirectory { get; set; }
```

- [ ] **Step 4: Declare the new service method**

In `Workflow/Services/ISettingsService.cs`, after `AddRecentDirectory`:

```csharp
    /// <summary>Adds or promotes a workflow directory in its MRU list and records it as the last used one.</summary>
    /// <param name="directory">Absolute directory path. Empty values are ignored.</param>
    public void AddRecentWorkflowDirectory(string directory);
```

- [ ] **Step 5: Implement, extracting the shared promotion helper**

In `Workflow/Services/SettingsService.cs`, replace the body of `AddRecentDirectory` and add the
new method plus the helper:

```csharp
    /// <inheritdoc />
    public void AddRecentDirectory(string directory)
    {
        var normalised = Promote(Settings.RecentDirectories, directory);

        if (normalised is not null)
        {
            Settings.LastDirectory = normalised;
        }
    }

    /// <inheritdoc />
    public void AddRecentWorkflowDirectory(string directory)
    {
        var normalised = Promote(Settings.RecentWorkflowDirectories, directory);

        if (normalised is not null)
        {
            Settings.LastWorkflowDirectory = normalised;
        }
    }

    // One implementation for both MRU lists. Copying it would let the two drift apart on the
    // details that matter here: root-aware normalisation, case-insensitive de-duplication and the
    // cap. Collection<T> has no RemoveAll/RemoveRange (see AppSettings), hence the index loop.
    private static string? Promote(Collection<string> list, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        // Root-aware (see WorkingDirectoryPath): persisting "C:" instead of "C:\" would make the
        // restored MRU entry drive-relative on the next launch.
        var normalised = WorkingDirectoryPath.Normalise(directory);

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], normalised, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
            }
        }

        list.Insert(0, normalised);

        while (list.Count > MaxRecentDirectories)
        {
            list.RemoveAt(list.Count - 1);
        }

        return normalised;
    }
```

`Collection<string>` needs `using System.Collections.ObjectModel;` at the top of
`SettingsService.cs` — add it if it is not already there.

- [ ] **Step 6: Run the settings tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SettingsServiceTests"`
Expected: PASS, including every pre-existing case (the refactor must not change `AddRecentDirectory`).

- [ ] **Step 7: Build**

Run: `dotnet build Workflow.sln -c Debug` — Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Commit**

```bash
git add Workflow/Models/AppSettings.cs Workflow/Services/ISettingsService.cs Workflow/Services/SettingsService.cs Workflow.Tests/SettingsServiceTests.cs
git commit -m "feat(subtasks): remember the workflow directory in settings.json

Second MRU list plus LastWorkflowDirectory, with the promotion loop
extracted so both lists share one implementation of normalisation,
de-duplication and the cap. Additive: an older settings.json loads with
the new fields empty.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: The journal remembers the subtask configuration

**Files:**
- Modify: `Workflow/Models/TaskState.cs`
- Modify: `Workflow/Services/ITaskStateStore.cs`
- Modify: `Workflow/Services/TaskStateStore.cs`
- Modify: `Workflow.Tests/Fakes/FakeTaskStateStore.cs`
- Test: `Workflow.Tests/TaskStateStoreTests.cs` (add cases)

**Interfaces:**
- Produces: `TaskState.SubtasksEnabled` (`bool`), `TaskState.WorkflowDirectory` (`string?`), and `ITaskStateStore.SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory)`.

**Context:** `.workflow-state.json` is the per-task journal at `TaskPaths.StateAbsolute`. It is
written camelCase with string enums, and `TryLoad` returns `null` when `dto.Version >
TaskState.CurrentVersion`.

**`TaskState.CurrentVersion` MUST STAY 1.** Bumping it to 2 would make every already-installed
build return `null` for a journal written by the new build, which hides the task from recovery
entirely. The two new fields are additive in both directions: a version-1 journal without them
reads as "subtask mode off", which is correct for every task created before this change. SPEC §8.3.

`SaveSubtaskSettings` is a separate method rather than extra parameters on `SaveDescription`,
because `SaveDescription`'s documented contract includes clearing `Dismissed` and this write must
not do that.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/TaskStateStoreTests.cs` (reuse the existing fixture's `_paths`):

```csharp
    [Fact]
    public void SaveSubtaskSettings_CreatesTheJournalAndPersistsBothFields()
    {
        var store = new TaskStateStore();

        store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\repo\Workflows");

        var state = store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.True(state.SubtasksEnabled);
        Assert.Equal(@"C:\repo\Workflows", state.WorkflowDirectory);
    }

    [Fact]
    public void SaveSubtaskSettings_DoesNotClearTheDismissedFlag()
    {
        var store = new TaskStateStore();
        store.SaveDescription(_paths, "Beschreibung");
        store.SetDismissed(_paths, dismissed: true);

        store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\repo\Workflows");

        Assert.True(store.TryLoad(_paths)!.Dismissed);
    }

    [Fact]
    public void SaveSubtaskSettings_KeepsTheDescriptionAndThePhases()
    {
        var store = new TaskStateStore();
        store.SaveDescription(_paths, "Beschreibung");
        store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);

        store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\repo\Workflows");

        var state = store.TryLoad(_paths)!;

        Assert.Equal("Beschreibung", state.TaskDescription);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Review].Status);
    }

    [Fact]
    public void SaveSubtaskSettings_CanSwitchSubtaskModeOffAgain()
    {
        var store = new TaskStateStore();
        store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\repo\Workflows");

        store.SaveSubtaskSettings(_paths, enabled: false, workflowDirectory: null);

        var state = store.TryLoad(_paths)!;

        Assert.False(state.SubtasksEnabled);
        Assert.Null(state.WorkflowDirectory);
    }

    [Fact]
    public void TryLoad_ReadsAJournalWrittenBeforeSubtaskModeExistedAsDisabled()
    {
        Directory.CreateDirectory(_paths.TaskDirectory);
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "alt",
          "createdUtc": "2026-09-01T10:00:00+00:00",
          "updatedUtc": "2026-09-01T10:00:00+00:00",
          "dismissed": false,
          "phases": []
        }
        """);

        var state = new TaskStateStore().TryLoad(_paths);

        Assert.NotNull(state);
        Assert.False(state.SubtasksEnabled);
        Assert.Null(state.WorkflowDirectory);
    }

    [Fact]
    public void CurrentVersion_StaysAtOne() =>
        // Bumping it would make every installed build read a new journal as null and hide the
        // task from recovery. The new fields are additive instead. SPEC section 8.3 / D10.
        Assert.Equal(1, TaskState.CurrentVersion);
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~TaskStateStoreTests"`
Expected: compile error — `SaveSubtaskSettings` and `SubtasksEnabled` do not exist.

- [ ] **Step 3: Add the two journal fields**

In `Workflow/Models/TaskState.cs`, after the `Dismissed` property:

```csharp
    /// <summary>True when phase 4 runs as a subtask loop instead of one implementation session.</summary>
    public bool SubtasksEnabled { get; set; }

    /// <summary>
    /// Root of the Workflows tracking repository this task's subtasks live in, or null.
    /// </summary>
    /// <remarks>
    /// Stored per task even though <c>AppSettings.LastWorkflowDirectory</c> also holds a value:
    /// the global setting is only a prefill, and a resume must use the directory the run actually
    /// used rather than whatever the user picked most recently.
    /// </remarks>
    public string? WorkflowDirectory { get; set; }
```

- [ ] **Step 4: Declare the store method**

In `Workflow/Services/ITaskStateStore.cs`, after `SaveDescription`:

```csharp
    /// <summary>
    /// Records the subtask configuration, creating the journal if needed. Unlike
    /// <see cref="SaveDescription"/> this does NOT clear the dismissed flag.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="enabled">Whether phase 4 runs as a subtask loop.</param>
    /// <param name="workflowDirectory">Root of the Workflows repository, or null.</param>
    public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory);
```

- [ ] **Step 5: Implement it in `TaskStateStore`**

Add after `SaveDescription` in `Workflow/Services/TaskStateStore.cs`:

```csharp
    /// <inheritdoc />
    public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths) ?? CreateEmpty();
        state.SubtasksEnabled = enabled;
        state.WorkflowDirectory = workflowDirectory;
        Save(paths, state);
    }
```

And extend the read DTO at the bottom of the same file — inside `private sealed class TaskStateDto`,
after `Dismissed`:

```csharp
        public bool SubtasksEnabled { get; set; }

        public string? WorkflowDirectory { get; set; }
```

And carry them across in `TryLoad`'s projection, after `Dismissed = dto.Dismissed,`:

```csharp
                SubtasksEnabled = dto.SubtasksEnabled,
                WorkflowDirectory = dto.WorkflowDirectory,
```

- [ ] **Step 6: Implement it in the fake**

In `Workflow.Tests/Fakes/FakeTaskStateStore.cs`, add a recording collection next to the existing
ones and the method:

```csharp
    public Collection<(bool Enabled, string? Directory)> SubtaskSettings { get; } = [];
```

```csharp
    public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);

        SubtaskSettings.Add((enabled, workflowDirectory));

        var state = TryLoad(paths) ?? NewState();
        state.SubtasksEnabled = enabled;
        state.WorkflowDirectory = workflowDirectory;
        _states[paths.TaskDirectory] = state;
    }
```

- [ ] **Step 7: Run the tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~TaskStateStoreTests"`
Expected: PASS.

- [ ] **Step 8: Build and run the full suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass.

- [ ] **Step 9: Commit**

```bash
git add Workflow/Models/TaskState.cs Workflow/Services/ITaskStateStore.cs Workflow/Services/TaskStateStore.cs Workflow.Tests/Fakes/FakeTaskStateStore.cs Workflow.Tests/TaskStateStoreTests.cs
git commit -m "feat(subtasks): journal whether subtask mode is on and which repo it uses

Additive fields, schema version deliberately left at 1 so an installed
build does not read a new journal as null and hide the task from
recovery. SaveSubtaskSettings is separate from SaveDescription because it
must not clear the dismissed flag.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `PhaseReconciliation` stops demoting finished subtask tasks

**Files:**
- Modify: `Workflow/Models/PhaseReconciliation.cs`
- Test: `Workflow.Tests/PhaseReconciliationSubtaskTests.cs` (create)

**Interfaces:**
- Consumes: `SubtaskLedger.AllComplete(SubtaskPaths)` (Task 2), `TaskState.SubtasksEnabled` / `TaskState.WorkflowDirectory` (Task 5).
- Produces: no signature change to the public API (`Reconcile(TaskPaths, TaskState)` and `FirstIncomplete(TaskState)` keep their shapes); only the private `ArtefactsPresent` helper gains the state parameter.

**Context — this is the highest-value fix in the plan.** `PhaseReconciliation.ArtefactsPresent`
currently decides Implementation is still complete by checking that `{TaskName}-done.md` exists and
is non-empty. In subtask mode that file is **never written**, because `implementation_prompt.md` —
the only thing that writes it — never runs. Without this change, the startup scan and the re-arm
path in `TaskTabViewModel.SyncFolder` would demote a perfectly finished subtask task back to
Pending on every single launch and offer it for recovery for ever. SPEC §13.2.

The demote-never-promote rule is unchanged: disk evidence may demote a phase, never promote one.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/PhaseReconciliationSubtaskTests.cs`:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using Workflow.Models;

namespace Workflow.Tests;

public sealed class PhaseReconciliationSubtaskTests : IDisposable
{
    private readonly string _root;
    private readonly TaskPaths _paths;
    private readonly SubtaskPaths _subtasks;

    public PhaseReconciliationSubtaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-recon-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(_root, "ws");
        var workflows = Path.Combine(_root, "Workflows");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workflows, "task_template"));

        _paths = new TaskPaths(workspace, "demo");
        _subtasks = new SubtaskPaths(workflows, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TaskState CompletedThroughImplementation(bool subtasks) => new()
    {
        TaskDescription = "Beschreibung",
        SubtasksEnabled = subtasks,
        WorkflowDirectory = subtasks ? _subtasks.WorkflowDirectory : null,
        Phases = new Collection<TaskPhaseState>(
        [
            new(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow),
            new(WorkflowPhase.Review, PhaseStatus.Completed, DateTimeOffset.UtcNow),
            new(WorkflowPhase.ResolveReview, PhaseStatus.Completed, DateTimeOffset.UtcNow),
            new(WorkflowPhase.Implementation, PhaseStatus.Completed, DateTimeOffset.UtcNow),
        ]),
    };

    private void WriteUpstreamArtefacts()
    {
        File.WriteAllText(_paths.SpecAbsolute, "spec");
        File.WriteAllText(_paths.PlanAbsolute, "plan");
        File.WriteAllText(_paths.ReviewAbsolute, "review");
    }

    private void WriteSubtask(string title, bool complete)
    {
        Directory.CreateDirectory(_subtasks.SubtaskDirectory(title));
        File.WriteAllText(_subtasks.SubtaskMarkdown(title), "# " + title);
        File.WriteAllText(
            _subtasks.SubtaskStatusFile(title),
            complete ? """{"status":"complete"}""" : """{"status":"pending"}""");
    }

    private void WriteIndex(params string[] titles)
    {
        Directory.CreateDirectory(_subtasks.TaskDirectory);
        var list = string.Join(", ", titles.Select(t => "\"" + t + "\""));
        File.WriteAllText(_subtasks.ResultAbsolute, $$"""{"version":1,"subtasks":[{{list}}]}""");
    }

    [Fact]
    public void Reconcile_DoesNotDemoteASubtaskTaskWhoseSubtasksAreAllComplete()
    {
        WriteUpstreamArtefacts();
        WriteIndex("a", "b");
        WriteSubtask("a", complete: true);
        WriteSubtask("b", complete: true);

        var state = CompletedThroughImplementation(subtasks: true);

        // No {TaskName}-done.md exists, and must not be required.
        Assert.False(File.Exists(_paths.DoneAbsolute));
        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Implementation].Status);
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_DemotesASubtaskTaskThatStillHasIncompleteSubtasks()
    {
        WriteUpstreamArtefacts();
        WriteIndex("a", "b");
        WriteSubtask("a", complete: true);
        WriteSubtask("b", complete: false);

        var state = CompletedThroughImplementation(subtasks: true);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Implementation].Status);
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_DemotesASubtaskTaskWhoseIndexIsGone()
    {
        WriteUpstreamArtefacts();
        var state = CompletedThroughImplementation(subtasks: true);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public void Reconcile_DoesNotDemoteWhenTheStoredWorkflowDirectoryIsUnusable()
    {
        WriteUpstreamArtefacts();

        var state = CompletedThroughImplementation(subtasks: true);
        state.WorkflowDirectory = "   ";

        // Doubt is not evidence: a configuration we cannot interpret must not cause a demotion.
        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public void Reconcile_StillRequiresTheDoneMarkerWhenSubtaskModeIsOff()
    {
        WriteUpstreamArtefacts();

        var state = CompletedThroughImplementation(subtasks: false);
        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Implementation].Status);

        File.WriteAllText(_paths.DoneAbsolute, "fertig");
        var second = CompletedThroughImplementation(subtasks: false);
        Assert.False(PhaseReconciliation.Reconcile(_paths, second));
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~PhaseReconciliationSubtaskTests"`
Expected: `Reconcile_DoesNotDemoteASubtaskTaskWhoseSubtasksAreAllComplete` FAILS —
`Assert.False() Failure` — because the current rule demands `-done.md`.

- [ ] **Step 3: Thread the state into the artefact check**

In `Workflow/Models/PhaseReconciliation.cs`, change the `Reconcile` call site from
`!ArtefactsPresent(paths, entry.Phase)` to `!ArtefactsPresent(paths, state, entry.Phase)` and
replace the helper:

```csharp
    // ResolveReview is never demoted: its completion is baseline-relative and leaves no trace on
    // disk, so the journal is the only evidence there is.
    private static bool ArtefactsPresent(TaskPaths paths, TaskState state, WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Specification => NonEmpty(paths.SpecAbsolute) && NonEmpty(paths.PlanAbsolute),
        WorkflowPhase.Review => NonEmpty(paths.ReviewAbsolute),
        WorkflowPhase.Implementation => ImplementationPresent(paths, state),
        _ => true,
    };

    // In subtask mode {TaskName}-done.md is NEVER written: implementation_prompt.md, the only
    // thing that writes it, does not run. Requiring it here would demote every finished subtask
    // task on every launch and offer it for recovery for ever (SPEC section 13.2).
    private static bool ImplementationPresent(TaskPaths paths, TaskState state)
    {
        if (!state.SubtasksEnabled || string.IsNullOrWhiteSpace(state.WorkflowDirectory))
        {
            return NonEmpty(paths.DoneAbsolute);
        }

        try
        {
            return SubtaskLedger.AllComplete(new SubtaskPaths(state.WorkflowDirectory, paths.TaskName));
        }
        catch (ArgumentException)
        {
            // A stored path we cannot interpret tells us nothing. Same policy as NonEmpty's
            // transient-error branches: demotion must be evidence-driven, never doubt-driven.
            return true;
        }
    }
```

- [ ] **Step 4: Run the new tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~PhaseReconciliationSubtaskTests"`
Expected: PASS.

- [ ] **Step 5: Run the whole suite — the pre-existing recovery tests must be untouched**

Run: `dotnet test Workflow.sln -c Debug`
Expected: all pass, including `TaskRecoveryScannerTests`, with **no edits to their assertions**.
If one needs changing, the change above is wrong — the non-subtask branch must behave exactly as
before.

- [ ] **Step 6: Commit**

```bash
git add Workflow/Models/PhaseReconciliation.cs Workflow.Tests/PhaseReconciliationSubtaskTests.cs
git commit -m "fix(subtasks): reconcile phase 4 against the ledger in subtask mode

In subtask mode {TaskName}-done.md is never written, so the existing
artefact check would demote every finished subtask task on every launch
and offer it for recovery for ever. The evidence becomes the subtask
ledger instead. Demote-never-promote is unchanged, and an
uninterpretable stored path does not demote.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: **PROMPT CHANGE** — correct `create_subtasks.md` and `run_subtask.md`

> **This task edits files under `Workflow/Prompt/`.** The design session was instructed not to
> touch them; this task is the sanctioned place to do it. **It must run before Task 9**, which
> switches on stricter startup validation that the current files would fail.

**Files:**
- Modify: `Workflow/Prompt/create_subtasks.md`
- Modify: `Workflow/Prompt/run_subtask.md`
- Test: `Workflow.Tests/PromptTemplateServiceTests.cs` (add cases)

**Interfaces:**
- Produces: two templates whose only brace-tokens are ones the application substitutes, and whose
  file-name and location instructions match `SubtaskPaths` (Task 1) and `SubtaskLedger` (Task 2).

**Context — what is wrong today (SPEC §14):**

| Defect | Consequence |
|--------|-------------|
| `create_subtasks.md` uses `{task_path}` | No token set defines it. `PromptTemplateService.Render` throws `PromptTemplateException` mid-phase, and after Task 9 it disables `Start workflow` on every tab at startup. |
| `create_subtasks.md` uses `{subtask_title}` | In that file the name is one **Claude invents per subtask**. Substituting one value for it destroys the instruction. |
| `subtask_path` is defined absolute | `run_subtask.md` composes `{workflow_path}\{subtask_path}\{subtask_title}`, which then double-prefixes. |
| Flag written to `{workflow_path}/results.json` | The repository root is shared by every task: a leftover flag satisfies the next task's watcher instantly. |
| `results.json` vs `result.json` | Two names for one file. |
| Flag content unspecified | `CompletionRule.FilesExist` requires `Length > 0`; a zero-byte flag is waited on for ever. |
| `status.json` not mandatory, no atomic-write rule | The ledger has nothing to read for a not-yet-started subtask, and can read a half-flushed file. |
| `run_subtask.md` writes `{subtask_path}/{subtask_title}/status.json` without `{workflow_path}\` | That relative path resolves against the CLI's cwd, i.e. the **code** repository, so the file lands in the wrong repo. |

**The convention being established:** `{name}` is reserved for values the **application**
substitutes; every name the CLI invents for itself uses `<name>`. The token regex is
`\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}`, so angle brackets are invisible to it.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/PromptTemplateServiceTests.cs`:

```csharp
    private static readonly string ShippedPromptDirectory =
        Path.Combine(AppContext.BaseDirectory, "Prompt");

    private static readonly HashSet<string> CreateSubtasksTokens = new(StringComparer.Ordinal)
    {
        "taskbezeichnung", "taskbeschreibung", "AppDirectory",
        "spec_path", "plan_path", "review_path", "done_path",
        "workflow_path", "tasktitel", "subtask_path",
    };

    private static readonly HashSet<string> RunSubtaskTokens = new(StringComparer.Ordinal)
    {
        "taskbezeichnung", "taskbeschreibung", "AppDirectory",
        "spec_path", "plan_path", "review_path", "done_path",
        "workflow_path", "tasktitel", "subtask_path", "subtask_title", "subtask",
    };

    private static IEnumerable<string> TokensIn(string fileName) =>
        System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(Path.Combine(ShippedPromptDirectory, fileName)),
                     @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal);

    [Fact]
    public void ShippedCreateSubtasks_UsesOnlyTokensTheApplicationSubstitutes() =>
        Assert.Empty(TokensIn("create_subtasks.md").Where(t => !CreateSubtasksTokens.Contains(t)));

    [Fact]
    public void ShippedRunSubtask_UsesOnlyTokensTheApplicationSubstitutes() =>
        Assert.Empty(TokensIn("run_subtask.md").Where(t => !RunSubtaskTokens.Contains(t)));

    [Fact]
    public void ShippedCreateSubtasks_DoesNotClaimSubtaskTitleAsAnApplicationToken() =>
        // In this file the subtask title is a name Claude invents per subtask, so it must use the
        // <angle-bracket> form. Substituting a single value for it would destroy the instruction.
        Assert.DoesNotContain("subtask_title", TokensIn("create_subtasks.md"), StringComparer.Ordinal);

    [Theory]
    [InlineData("create_subtasks.md")]
    [InlineData("run_subtask.md")]
    public void ShippedSubtaskPrompts_NeverSayResultsJson(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(ShippedPromptDirectory, fileName));

        Assert.DoesNotContain("results.json", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedCreateSubtasks_PutsTheFlagInTheTaskFolderNotTheRepositoryRoot()
    {
        var text = File.ReadAllText(Path.Combine(ShippedPromptDirectory, "create_subtasks.md"));

        Assert.Contains("{workflow_path}/{tasktitel}/result.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{workflow_path}/result.json", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create_subtasks.md")]
    [InlineData("run_subtask.md")]
    public void ShippedSubtaskPrompts_RequireANonEmptyFlagFile(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(ShippedPromptDirectory, fileName));

        // A zero-byte file never satisfies CompletionRule.FilesExist.
        Assert.Contains("darf nicht leer sein", text, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~PromptTemplateServiceTests"`
Expected: FAIL — `ShippedCreateSubtasks_UsesOnlyTokensTheApplicationSubstitutes` reports
`task_path`, and the `results.json` / flag-location / non-empty cases fail too.

- [ ] **Step 3: Rewrite `Workflow/Prompt/create_subtasks.md`**

Replace the whole file with:

````markdown
Lies diese beiden Dokumente vollständig:

- {spec_path}
- {plan_path}

Zerlege den vorhandenen Implementierungsplan in kleine, einzeln ausführbare Aufgaben für
voneinander unabhängige Claude-Code-Sessions.

## Verzeichnisse

Die Anwendung setzt diese Werte ein:

- `{workflow_path}` — absoluter Pfad des ausgecheckten Workflows-Repositorys
- `{tasktitel}` — der Name dieses Tasks
- `{subtask_path}` — **relativ zu `{workflow_path}`**, immer `{tasktitel}\subtasks`

Daraus ergibt sich:

- `<task_path>` = `{workflow_path}/{tasktitel}`
- Subtask-Ordner = `{workflow_path}/{subtask_path}/<subtask_title>`

`<task_path>` und `<subtask_title>` sind Namen, die du selbst vergibst. Sie stehen absichtlich in
spitzen Klammern: geschweifte Klammern sind für Werte reserviert, die die Anwendung ersetzt.

`<subtask_title>` muss ein einfacher Ordnername sein — ohne `\`, `/`, `:` und ohne `..`.
Empfohlen ist `ST-001-kurzer-titel`, fortlaufend nummeriert in Ausführungsreihenfolge.

## Was du erzeugst

Für jede Aufgabe einen Ordner `{workflow_path}/{subtask_path}/<subtask_title>` mit:

- `subtask.md` — die vollständige Aufgabenbeschreibung. Darf nicht leer sein.
- `findings.md` — Probleme, die dir bei dieser Aufgabe auffallen.
- `status.json` — der Statusbericht. Lege ihn sofort mit `"status": "pending"` an:

```json
{
  "subtask": "<subtask_title>",
  "status": "pending",
  "failreason": null,
  "openFindings": 0,
  "workflowRelevantFindings": [],
  "testsPassed": false,
  "readyForVerification": false
}
```

Die WPF-Anwendung liest aus dieser Datei ausschließlich `status` und `failreason`.
`status` gilt nur dann als Erfolg, wenn der Wert exakt `complete` lautet. Jeder andere Wert zählt
als Fehlschlag; die Anwendung zeigt dann zum Beispiel „3 subtasks failed!“ an und überspringt die
Aufgabe.

Aufgabenübergreifende Erkenntnisse gehören nach `{workflow_path}/<task_path>/findings.md`.

## Anforderungen an eine Subtask

Eine Subtask muss:

- genau ein zusammenhängendes, überprüfbares Ergebnis besitzen,
- in einer einzelnen frischen Claude-Code-Session ausführbar sein,
- alle benötigten Planstellen, Dateien, Entscheidungen und Randbedingungen selbst enthalten,
- ohne Kenntnis vorheriger Chatverläufe verständlich sein,
- Voraussetzungen und Abhängigkeiten explizit nennen,
- konkrete Implementierungsschritte enthalten,
- konkrete Tests und Abschlusskriterien enthalten,
- ausdrücklich nennen, was nicht verändert werden darf.

Eine Subtask darf nicht:

- mehrere unabhängige Komponenten oder Verantwortungsgrenzen vermischen,
- Implementierung, systemweite Integration, Betriebsumstellung und Gesamtabnahme in einer Aufgabe
  bündeln,
- stillschweigend Informationen aus den Quelldokumenten voraussetzen,
- neue Architekturentscheidungen erfinden.

Wenn eine Aufgabe voraussichtlich mehr als eine Claude-Code-Session benötigt, teile sie weiter auf.
Bevorzuge kleine Aufgaben gegenüber großen Aufgaben.

Verwende dieses Format für jede `subtask.md`:

# ST-XXX: Titel

## Ziel
## Warum diese Aufgabe separat ist
## Voraussetzungen
## Abhängigkeiten
## Verbindliche Quellen
## Betroffene Dateien
## Nicht betroffen
## Implementierungsschritte
## Tests und Verifikation
## Abschlusskriterien
## Übergabe an Folgeaufgaben
## Prompt für die ausführende Claude-Code-Session

## Review

Führe nach der Erzeugung einen unabhängigen Reviewdurchgang mit einem frischen Subagenten durch.
Prüfe:

1. vollständige Abdeckung beider Quelldokumente,
2. keine verlorenen Constraints oder Abnahmekriterien,
3. keine übergroßen Aufgaben,
4. keine zyklischen oder versteckten Abhängigkeiten,
5. keine überlappende Datei- oder Komponentenverantwortung,
6. jede Aufgabe ist aus ihrem Markdown allein ausführbar.

Korrigiere gefundene Probleme und aktualisiere anschließend `findings.md`.

Verändere die beiden Quelldokumente nicht. Implementiere keinen Produktivcode.

## Abschluss — die Flag-Datei

Wenn alle Subtask-Verzeichnisse und -Dateien erzeugt sind, schreibst du als **letzte** Aktion die
Flag-Datei `{workflow_path}/{tasktitel}/result.json`.

Sie enthält die verbindliche Ausführungsreihenfolge und **darf nicht leer sein** — eine Datei mit
0 Byte erkennt die Anwendung nicht:

```json
{
  "version": 1,
  "task": "{tasktitel}",
  "subtasks": [
    "ST-001-erster-titel",
    "ST-002-zweiter-titel"
  ]
}
```

Das Feld `subtasks` ist Pflicht und muss jeden erzeugten Ordnernamen genau einmal enthalten, in der
Reihenfolge, in der die Aufgaben abgearbeitet werden sollen. Fehlt es, ist es leer oder kein Array,
bricht die Anwendung die Phase mit einer Fehlermeldung ab.

Schreibe `result.json` und jede `status.json` niemals direkt, sondern atomar:

1. in `result.json.tmp` schreiben,
2. Schreiben abschließen, Datei schließen,
3. nach `result.json` umbenennen.

Die WPF-Anwendung reagiert ausschließlich auf die fertige `result.json`.
````

- [ ] **Step 4: Rewrite the signalling section of `Workflow/Prompt/run_subtask.md`**

Keep the file's existing body. Replace **only** the path list under "Initialize/use:" and the
whole "Signalling completion" section.

Replace the three bullets under `Initialize/use:` with:

````markdown
`{subtask_path}` ist **relativ zu `{workflow_path}`**. Alle Pfade unten sind daher absolut.

* `{workflow_path}\{subtask_path}\{subtask_title}\task_plan.md` — execution phases and completion state
* `{workflow_path}\{subtask_path}\{subtask_title}\findings.md` — discoveries made while implementing
* `{workflow_path}\{subtask_path}\{subtask_title}\progress.md` — chronological work, changed files, tests, validation results and errors

If you find problems relating to the whole task and not just this subtask, use
`{workflow_path}\{subtask_path}\findings.md` as well.
````

Replace the whole `## Signalling completion` section with:

````markdown
## Signalling completion

Two files, in this order, as the last two actions of this session.

**1. Write the status payload** to `{workflow_path}\{subtask_path}\{subtask_title}\status.json`:

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

`status` must be exactly `complete` when every condition under "Completion" above is satisfied.
If it is not, write any other value together with a short `failreason` — the application counts
that as a failed subtask, shows it in red and moves on to the next one. Do not leave the file
unwritten: a missing `status.json` is itself reported as a failure.

**2. Then write the flag file** `{workflow_path}\{subtask_path}\{subtask_title}\result.json`.
It **darf nicht leer sein** — one or two sentences naming what was implemented and the result of
`Workflow\verify.ps1` is enough. The application watches for this file and reads `status.json`
only after it appears.

Write both files atomically: write `<name>.tmp`, finish and close it, then rename it to the final
name. The application reacts only to the finished file.
````

- [ ] **Step 5: Run the prompt tests and verify they pass**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~PromptTemplateServiceTests"`
Expected: PASS. The build step matters — `Prompt\**\*.md` is copied to the test output with
`PreserveNewest`, so a stale copy would be tested otherwise.

- [ ] **Step 6: Verify by hand that no stray brace-token remains**

Run:
```bash
grep -oE '\{[A-Za-z_][A-Za-z0-9_]*\}' Workflow/Prompt/create_subtasks.md | sort -u
grep -oE '\{[A-Za-z_][A-Za-z0-9_]*\}' Workflow/Prompt/run_subtask.md | sort -u
```
Expected for `create_subtasks.md`: exactly `{plan_path} {spec_path} {subtask_path} {tasktitel} {workflow_path}`.
Expected for `run_subtask.md`: those plus `{subtask} {subtask_title}` and any of
`{AppDirectory} {taskbezeichnung}` the untouched body still uses. **No `{task_path}`.**

- [ ] **Step 7: Commit**

```bash
git add Workflow/Prompt/create_subtasks.md Workflow/Prompt/run_subtask.md Workflow.Tests/PromptTemplateServiceTests.cs
git commit -m "fix(prompts): make the subtask templates match the flag-file contract

{name} is now reserved for values the application substitutes; names the
CLI invents for itself use <name>. Removes {task_path} (defined by no
token set) and stops claiming {subtask_title} in create_subtasks.md,
where it is a name Claude picks per subtask.

Also: one flag name (result.json), per task rather than at the shared
repository root, required to be non-empty because FilesExist ignores a
zero-byte file; an ordered 'subtasks' index as the binding execution
order; status.json mandatory and atomically written; and the status path
in run_subtask.md prefixed with {workflow_path} so it stops landing in
the code repository.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: **EXTERNAL REPO** — bring `task_template` in line with the contract

> **This task edits a different repository:** `C:\Users\Marco\Documents\repo\Workflows`
> (`https://github.com/mrudolph09/Workflows.git`, branch `main`). Commit there, not in the
> Workflow repository. Nothing in the application build depends on it, so it can run any time
> before the manual end-to-end verification in Task 18.

**Files (all inside the Workflows repository):**
- Create: `task_template/subtasks/ST-001-subtask-template/status.json`
- Modify: `task_template/subtasks/ST-001-subtask-template/result.json`
- Create: `task_template/result.json`
- Delete: `task_template/subtasks/ST-001-subtask-template/verification.json`
- Create: `README.md`

**Context:** every file in `task_template/` is currently zero bytes, and there is **no**
`status.json` — the one file the application reads per subtask. `verification.json` exists but
nothing writes or reads it. SPEC §15.

The template is a reference for humans; the application never copies it. But a zero-byte
`result.json` copied by hand would never satisfy `CompletionRule.FilesExist`, so the placeholders
get real content.

- [ ] **Step 1: Add the missing per-subtask status file**

`task_template/subtasks/ST-001-subtask-template/status.json`:

```json
{
  "subtask": "ST-001-subtask-template",
  "status": "pending",
  "failreason": null,
  "openFindings": 0,
  "workflowRelevantFindings": [],
  "testsPassed": false,
  "readyForVerification": false
}
```

- [ ] **Step 2: Give the per-subtask flag real content**

`task_template/subtasks/ST-001-subtask-template/result.json`:

```json
{
  "note": "Flag-Datei. Wird von der WPF-App ueberwacht und darf nicht leer sein.",
  "summary": "Kurzbericht der ausfuehrenden Session."
}
```

- [ ] **Step 3: Add the task-level index template**

`task_template/result.json`:

```json
{
  "version": 1,
  "task": "task_template",
  "subtasks": [
    "ST-001-subtask-template"
  ]
}
```

- [ ] **Step 4: Delete the unused file**

```bash
git -C C:/Users/Marco/Documents/repo/Workflows rm task_template/subtasks/ST-001-subtask-template/verification.json
```

Nothing writes or reads it. Leaving it invites a future reader to assume it is part of the
handshake.

- [ ] **Step 5: Document the contract at the repository root**

`README.md`:

````markdown
# Workflows

Tracking repository for the Workflow WPF app (`C:\Users\Marco\Documents\repo\Workflow`).
It holds **process state only** — no product code.

## Layout

```
<task-name>/
  result.json                  index + create-flag   (app READS, app DELETES)
  findings.md                  task-wide findings    (Claude only)
  subtasks/
    ST-001-<title>/
      subtask.md               task description      (app READS)
      status.json              payload               (app READS)
      result.json              subtask flag          (app READS, app DELETES)
      task_plan.md findings.md progress.md           (Claude only)
task_template/                 reference; the app never reads or writes it
```

## Contract

The app reads exactly three files: `result.json`, `status.json` and `subtask.md`.

**`<task-name>/result.json`** — the binding execution order, and the flag that says the
decomposition finished. `subtasks` is required, non-empty, and each entry is a plain folder name
(no `\`, `/`, `:`, `..`).

**`<task-name>/subtasks/<title>/status.json`** — only `status` and `failreason` are read.
`status` counts as success only when it is exactly `complete`.

**`<task-name>/subtasks/<title>/result.json`** — the flag. Content is free, but the file
**must not be empty**: the app's watcher ignores a zero-byte file.

**Ordering:** write `status.json` first, then the flag. Write both atomically
(`<name>.tmp` → rename), so the watcher never sees a half-written file.
````

- [ ] **Step 6: Commit in the Workflows repository**

```bash
cd C:/Users/Marco/Documents/repo/Workflows
git add -A
git commit -m "chore(template): match the flag-file contract the WPF app reads

Adds the missing per-subtask status.json, gives the flag files non-empty
placeholder content (a zero-byte flag never satisfies the app's watcher),
adds the task-level result.json index, drops the unused
verification.json, and documents the contract in a README.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 7: Confirm the repository still validates as a workflow directory**

Run: `ls C:/Users/Marco/Documents/repo/Workflows/task_template`
Expected: the folder still exists — `WorkflowDirectoryValidation` uses it as the marker.

---

## Task 9: Prompt tokens for the subtask templates, and per-file startup validation

**Files:**
- Create: `Workflow/Models/PromptTemplateCatalog.cs`
- Modify: `Workflow/Services/PromptTemplateService.cs`
- Test: `Workflow.Tests/PromptTemplateCatalogTests.cs` (create), `Workflow.Tests/PromptTemplateServiceTests.cs` (add cases)

**Depends on:** Task 1 (`SubtaskPaths`) and **Task 7** — the shipped templates must already be
corrected, otherwise the stricter `ValidateAll` reports an error at startup and disables
`Start workflow` on every tab.

**Interfaces:**
- Produces:
  - `PromptTemplateCatalog.CreateSubtasks` = `"create_subtasks.md"`, `PromptTemplateCatalog.RunSubtask` = `"run_subtask.md"`, `PromptTemplateCatalog.AllowedTokens` (`IReadOnlyDictionary<string, IReadOnlyCollection<string>>`).
  - `PromptVariables.ForSubtaskCreation(TaskPaths paths, string taskDescription, SubtaskPaths subtasks)`
  - `PromptVariables.ForSubtaskRun(TaskPaths paths, string taskDescription, SubtaskPaths subtasks, string subtaskTitle, string subtaskBody)`
- Consumes: the existing `PromptVariables.For(TaskPaths, string)` — unchanged, seven tokens.

**Context:** `PromptTemplateService.ValidateAll` currently iterates `PhaseCatalog.All`, so the two
subtask templates are never checked at startup. Turning that into a per-file token table extends
the existing F17 gate (BASE §9.4): a broken template disables `Start workflow` on every tab with
the message the startup dialog already shows. Checking each file against **its own** allowed set
rather than the union also catches `{subtask}` accidentally used in `initial_prompt.md`. SPEC §14.3,
§14.4.

Token sets:

| Template | Allowed tokens |
|----------|----------------|
| the four phase templates | `taskbezeichnung`, `taskbeschreibung`, `AppDirectory`, `spec_path`, `plan_path`, `review_path`, `done_path` |
| `create_subtasks.md` | the above + `workflow_path`, `tasktitel`, `subtask_path` |
| `run_subtask.md` | the above + `subtask_title`, `subtask` |

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/PromptTemplateCatalogTests.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Tests;

public sealed class PromptTemplateCatalogTests
{
    [Fact]
    public void AllowedTokens_CoversEveryPhaseTemplateAndBothSubtaskTemplates()
    {
        foreach (var definition in PhaseCatalog.All)
        {
            Assert.True(PromptTemplateCatalog.AllowedTokens.ContainsKey(definition.PromptFile));
        }

        Assert.True(PromptTemplateCatalog.AllowedTokens.ContainsKey(PromptTemplateCatalog.CreateSubtasks));
        Assert.True(PromptTemplateCatalog.AllowedTokens.ContainsKey(PromptTemplateCatalog.RunSubtask));
        Assert.Equal(6, PromptTemplateCatalog.AllowedTokens.Count);
    }

    [Fact]
    public void PhaseTemplates_MayNotUseSubtaskTokens()
    {
        var allowed = PromptTemplateCatalog.AllowedTokens["initial_prompt.md"];

        Assert.DoesNotContain("subtask", allowed, StringComparer.Ordinal);
        Assert.DoesNotContain("workflow_path", allowed, StringComparer.Ordinal);
    }

    [Fact]
    public void CreateSubtasks_MayNotUseTheSubtaskTitleToken() =>
        // In that file the title is a name Claude invents per subtask.
        Assert.DoesNotContain(
            "subtask_title", PromptTemplateCatalog.AllowedTokens[PromptTemplateCatalog.CreateSubtasks],
            StringComparer.Ordinal);

    [Fact]
    public void RunSubtask_MayUseTheSubtaskBodyAndTitleTokens()
    {
        var allowed = PromptTemplateCatalog.AllowedTokens[PromptTemplateCatalog.RunSubtask];

        Assert.Contains("subtask", allowed, StringComparer.Ordinal);
        Assert.Contains("subtask_title", allowed, StringComparer.Ordinal);
        Assert.Contains("workflow_path", allowed, StringComparer.Ordinal);
    }

    [Fact]
    public void KnownNames_IsTheUnionOfEveryTemplatesAllowedTokens()
    {
        var union = PromptTemplateCatalog.AllowedTokens.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal);

        Assert.Equal(
            union.OrderBy(n => n, StringComparer.Ordinal),
            PromptVariables.KnownNames.OrderBy(n => n, StringComparer.Ordinal));
    }
}
```

Append to `Workflow.Tests/PromptTemplateServiceTests.cs`:

```csharp
    [Fact]
    public void ForSubtaskCreation_SuppliesTheWorkflowTokensAndKeepsTheBaseSeven()
    {
        var paths = new TaskPaths(@"C:\code", "demo");
        var subtasks = new SubtaskPaths(@"C:\repo\Workflows", "demo");

        var variables = PromptVariables.ForSubtaskCreation(paths, "Beschreibung", subtasks);

        Assert.Equal(@"C:\repo\Workflows", variables["workflow_path"]);
        Assert.Equal("demo", variables["tasktitel"]);
        Assert.Equal(@"demo\subtasks", variables["subtask_path"]);
        Assert.Equal("Beschreibung", variables["taskbeschreibung"]);
        Assert.Equal(paths.SpecRelative, variables["spec_path"]);
        Assert.Equal(paths.PlanRelative, variables["plan_path"]);
        Assert.False(variables.ContainsKey("subtask"));
        Assert.False(variables.ContainsKey("subtask_title"));
    }

    [Fact]
    public void ForSubtaskRun_AddsTheSubtaskTitleAndBody()
    {
        var paths = new TaskPaths(@"C:\code", "demo");
        var subtasks = new SubtaskPaths(@"C:\repo\Workflows", "demo");

        var variables = PromptVariables.ForSubtaskRun(
            paths, "Beschreibung", subtasks, "ST-001-a", "# ST-001\n\nTu dies.");

        Assert.Equal("ST-001-a", variables["subtask_title"]);
        Assert.Equal("# ST-001\n\nTu dies.", variables["subtask"]);
        Assert.Equal(@"demo\subtasks", variables["subtask_path"]);
    }

    [Fact]
    public void ForSubtaskRun_ComposesTheSubtaskFolderFromTheThreeTokens()
    {
        var subtasks = new SubtaskPaths(@"C:\repo\Workflows", "demo");
        var variables = PromptVariables.ForSubtaskRun(
            new TaskPaths(@"C:\code", "demo"), "d", subtasks, "ST-001-a", "body");

        var composed = Path.Combine(
            variables["workflow_path"], variables["subtask_path"], variables["subtask_title"]);

        Assert.Equal(subtasks.SubtaskDirectory("ST-001-a"), composed);
    }

    [Fact]
    public void ValidateAll_ReportsAnUnknownTokenInASubtaskTemplate()
    {
        var directory = CreateTemplateDirectory();
        File.WriteAllText(Path.Combine(directory, PromptTemplateCatalog.CreateSubtasks), "{nonsense}");

        var errors = new PromptTemplateService(directory).ValidateAll();

        Assert.Contains(errors, e => e.Contains("create_subtasks.md", StringComparison.Ordinal)
                                     && e.Contains("{nonsense}", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAll_ReportsASubtaskTokenUsedInAPhaseTemplate()
    {
        var directory = CreateTemplateDirectory();
        File.WriteAllText(Path.Combine(directory, "initial_prompt.md"), "{subtask}");

        var errors = new PromptTemplateService(directory).ValidateAll();

        Assert.Contains(errors, e => e.Contains("initial_prompt.md", StringComparison.Ordinal)
                                     && e.Contains("{subtask}", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAll_ReportsAMissingSubtaskTemplate()
    {
        var directory = CreateTemplateDirectory();
        File.Delete(Path.Combine(directory, PromptTemplateCatalog.RunSubtask));

        var errors = new PromptTemplateService(directory).ValidateAll();

        Assert.Contains(errors, e => e.Contains("run_subtask.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAll_AcceptsTheShippedTemplates() =>
        Assert.Empty(new PromptTemplateService(ShippedPromptDirectory).ValidateAll());

    // Writes one minimal, valid file per template into a fresh temp directory.
    private string CreateTemplateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wf-tpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);

        foreach (var fileName in PromptTemplateCatalog.AllowedTokens.Keys)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "PROMPT {taskbeschreibung}");
        }

        return directory;
    }
```

Add `private readonly List<string> _temporaryDirectories = [];` to the fixture and delete each of
them in `Dispose` (add `IDisposable` to the class if it is not already there; follow whatever the
existing fixture does).

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~PromptTemplate"`
Expected: compile error — `PromptTemplateCatalog`, `ForSubtaskCreation`, `ForSubtaskRun` do not exist.

- [ ] **Step 3: Create the catalogue**

Create `Workflow/Models/PromptTemplateCatalog.cs`:

```csharp
namespace Workflow.Models;

/// <summary>
/// Every prompt template the application ships, and the tokens each one is allowed to use.
/// </summary>
/// <remarks>
/// Per file rather than one global set: a phase template that used {subtask} would otherwise pass
/// startup validation and then throw a PromptTemplateException in the middle of a run.
/// </remarks>
public static class PromptTemplateCatalog
{
    /// <summary>The template that decomposes spec and plan into subtasks.</summary>
    public const string CreateSubtasks = "create_subtasks.md";

    /// <summary>The template that executes one subtask.</summary>
    public const string RunSubtask = "run_subtask.md";

    private static readonly string[] PhaseTokens =
    [
        "taskbezeichnung",
        "taskbeschreibung",
        "AppDirectory",
        "spec_path",
        "plan_path",
        "review_path",
        "done_path",
    ];

    private static readonly string[] CreateSubtaskTokens =
        [.. PhaseTokens, "workflow_path", "tasktitel", "subtask_path"];

    private static readonly string[] RunSubtaskTokens =
        [.. CreateSubtaskTokens, "subtask_title", "subtask"];

    /// <summary>Template file name to the token names that file may use, without braces.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedTokens { get; } =
        Build();

    private static Dictionary<string, IReadOnlyCollection<string>> Build()
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in PhaseCatalog.All)
        {
            map[definition.PromptFile] = PhaseTokens;
        }

        map[CreateSubtasks] = CreateSubtaskTokens;
        map[RunSubtask] = RunSubtaskTokens;

        return map;
    }
}
```

- [ ] **Step 4: Add the two variable builders**

In `Workflow/Services/PromptTemplateService.cs`, replace `PromptVariables.KnownNames` with a
union derived from the catalogue, and add the two builders. The builders **layer** so the seven
base tokens have exactly one definition:

```csharp
    /// <summary>Token names, without braces. The union over every template.</summary>
    public static IReadOnlyCollection<string> KnownNames { get; } =
        new HashSet<string>(
            PromptTemplateCatalog.AllowedTokens.Values.SelectMany(tokens => tokens),
            StringComparer.Ordinal);
```

```csharp
    /// <summary>Builds the substitution dictionary for the decomposition prompt.</summary>
    /// <param name="paths">The task's path set in the working directory.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <param name="subtasks">The task's path set in the Workflows repository.</param>
    /// <returns>The seven base tokens plus workflow_path, tasktitel and subtask_path.</returns>
    public static Dictionary<string, string> ForSubtaskCreation(
        TaskPaths paths, string taskDescription, SubtaskPaths subtasks)
    {
        ArgumentNullException.ThrowIfNull(subtasks);

        var variables = For(paths, taskDescription);

        variables["workflow_path"] = subtasks.WorkflowDirectory;
        variables["tasktitel"] = subtasks.TaskName;

        // RELATIVE: run_subtask.md composes {workflow_path}\{subtask_path}\{subtask_title}.
        variables["subtask_path"] = subtasks.SubtaskPathToken;

        return variables;
    }

    /// <summary>Builds the substitution dictionary for one subtask's prompt.</summary>
    /// <param name="paths">The task's path set in the working directory.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <param name="subtasks">The task's path set in the Workflows repository.</param>
    /// <param name="subtaskTitle">The subtask's folder name.</param>
    /// <param name="subtaskBody">The full text of that subtask's subtask.md.</param>
    /// <returns>Everything <see cref="ForSubtaskCreation"/> supplies plus subtask_title and subtask.</returns>
    public static Dictionary<string, string> ForSubtaskRun(
        TaskPaths paths,
        string taskDescription,
        SubtaskPaths subtasks,
        string subtaskTitle,
        string subtaskBody)
    {
        var variables = ForSubtaskCreation(paths, taskDescription, subtasks);

        variables["subtask_title"] = subtaskTitle ?? string.Empty;
        variables["subtask"] = subtaskBody ?? string.Empty;

        return variables;
    }
```

`PromptVariables` needs `using System.Linq;` only if it is not already covered by implicit usings
(it is, `ImplicitUsings=enable`).

- [ ] **Step 5: Make `ValidateAll` iterate the catalogue with per-file sets**

Replace the body of `PromptTemplateService.ValidateAll`:

```csharp
    /// <inheritdoc />
    public IReadOnlyList<string> ValidateAll()
    {
        var errors = new List<string>();

        foreach (var (fileName, allowed) in PromptTemplateCatalog.AllowedTokens)
        {
            try
            {
                var text = ReadTemplate(fileName);

                var unknown = TokenRegex()
                    .Matches(text)
                    .Select(m => m.Groups["name"].Value)
                    .Where(name => !allowed.Contains(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (unknown.Count > 0)
                {
                    var names = string.Join(", ", unknown.Select(n => "{" + n + "}"));
                    errors.Add($"Die Prompt-Datei '{fileName}' enthält unbekannte Platzhalter: {names}.");
                }
            }
            catch (PromptTemplateException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors;
    }
```

`allowed.Contains(name)` on an `IReadOnlyCollection<string>` resolves to `Enumerable.Contains`,
which uses the default ordinal comparer for `string` — correct here, token names are
case-sensitive.

Update the doc comment on `IPromptTemplateService.ValidateAll` from "every phase template" to
"every template in <see cref="PromptTemplateCatalog"/>".

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~PromptTemplate"`
Expected: PASS — including `ValidateAll_AcceptsTheShippedTemplates`, which only passes because
Task 7 has already corrected the files.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test Workflow.sln -c Debug`
Expected: all pass. `MainWindowViewModelTests` asserts on startup errors; if one now fails, the
shipped templates still violate their token sets — go back to Task 7, do not weaken the sets.

- [ ] **Step 8: Commit**

```bash
git add Workflow/Models/PromptTemplateCatalog.cs Workflow/Services/PromptTemplateService.cs Workflow/Services/IPromptTemplateService.cs Workflow.Tests/PromptTemplateCatalogTests.cs Workflow.Tests/PromptTemplateServiceTests.cs
git commit -m "feat(subtasks): validate every template against its own token set

ValidateAll now iterates PromptTemplateCatalog instead of PhaseCatalog,
so the two subtask templates are covered by the existing startup gate,
and a phase template that used {subtask} is caught at startup rather
than mid-run. Adds the two layered variable builders; the seven base
tokens keep exactly one definition.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Extract `RunSessionAsync` — behaviour-preserving refactor

**Files:**
- Modify: `Workflow/Services/WorkflowOrchestrator.cs`
- Test: `Workflow.Tests/WorkflowOrchestratorTests.cs` — **assertions must not change**

**Interfaces:**
- Produces (private): `private async Task<bool> RunSessionAsync(ITerminalController terminal, ManualPhaseSignal manualSignal, string workingDirectory, string launcher, string promptFile, IReadOnlyDictionary<string, string> variables, CompletionRule rule, string watchDirectory, IReadOnlyList<string> watchPaths, CancellationToken cancellationToken)` returning **true when the manual signal won the race**, false when the watcher did.

**Context:** `RunPhaseAsync` currently inlines the whole "drive one CLI session" sequence. Tasks 12
and 13 need that sequence two more ways (the decomposition prompt, a subtask prompt). Copying it
would produce three copies of the settle/paste/submit logic — the most defect-prone code in this
repository, rebuilt across BASE §7.3 and §9.3 and the Codex `readyPattern` incident. SPEC §9.3.

**This task adds no new behaviour.** Its acceptance criterion is that
`Workflow.Tests/WorkflowOrchestratorTests.cs` passes **with no edits to any assertion**. If an
assertion has to change, the refactor is wrong.

Read `Workflow/Services/WorkflowOrchestrator.cs:90-140` before starting. The sequence to move is,
in order: create watcher → `ClearScreen` → `StartSession` → `WaitUntilReadyAsync` → `cd` →
launcher → `SettleAndAnswerAsync` → `Render` → `SendPromptAsync` → `await await WhenAny(watcher,
manualSignal)`.

- [ ] **Step 1: Record the current behaviour as the baseline**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: PASS. Note the number of passing tests; it must be identical at the end.

- [ ] **Step 2: Add the extracted method**

Add to `WorkflowOrchestrator`, below `RunPhaseAsync`:

```csharp
    /// <summary>
    /// Drives one CLI session from a clean screen to its completion condition.
    /// </summary>
    /// <returns>True when the manual signal ended the wait, false when the watcher did.</returns>
    /// <remarks>
    /// One implementation for all three kinds of session (a phase, the decomposition, one
    /// subtask). The settle/paste/submit sequence below is the part of this application that has
    /// been hardest to get right; a second copy of it would be free to drift.
    /// </remarks>
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
        CancellationToken cancellationToken)
    {
        // The baseline for AnyContentChanged must be captured before the CLI can touch anything.
        using var watcher = _watchers.Create(rule, watchDirectory, watchPaths, _debounce, _pollInterval);

        terminal.ClearScreen();
        terminal.StartSession(
            ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, workingDirectory);

        // The page must be listening before anything is written to it, or `clear`, the first PTY
        // bytes and the first snapshot are all dropped - and the launcher's startup screen is
        // exactly what the auto-answer rules need to match (spec section 6.4).
        await terminal.WaitUntilReadyAsync(cancellationToken);

        // Always quoted: working directories contain spaces and umlauts.
        terminal.Send($"cd \"{workingDirectory}\"\r");
        terminal.Send($"{launcher}\r");

        await SettleAndAnswerAsync(terminal, cancellationToken);

        await SendPromptAsync(terminal, _prompts.Render(promptFile, variables), cancellationToken);

        // The doubled await unwraps Task<Task> so a watcher failure surfaces as
        // ArtifactWatchException instead of being silently dropped.
        var manual = manualSignal.WaitAsync(cancellationToken);
        var finished = await Task.WhenAny(watcher.WaitAsync(cancellationToken), manual);
        await finished;

        return ReferenceEquals(finished, manual);
    }
```

- [ ] **Step 3: Rewrite `RunPhaseAsync` to call it**

Replace everything in `RunPhaseAsync` from `// A marker left by a previous run…` to the end with:

```csharp
        // A marker left by a previous run of this task would satisfy the phase-4 watcher in
        // milliseconds. Deleting it before the baseline is taken removes the failure mode
        // instead of handling it (SPEC section 8.3).
        if (definition.Phase == WorkflowPhase.Implementation)
        {
            DeleteStaleDoneMarker(request.Paths.DoneAbsolute);
        }

        // Every phase has an artefact condition; the manual signal is the escape hatch for all
        // four, not a completion rule of its own. Either way the phase is finished here.
        _ = await RunSessionAsync(
            request.Terminal,
            request.ManualSignal,
            request.Paths.WorkingDirectory,
            definition.Launcher,
            definition.PromptFile,
            PromptVariables.For(request.Paths, request.TaskDescription),
            definition.Completion,
            request.Paths.TaskDirectory,
            WatchedPaths(request.Paths, definition),
            cancellationToken);

        _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Completed);
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
```

The three lines above it — `RecordPhase(Active)`, `Progress.Report(Active)`,
`ManualSignal.Reset()` — stay exactly where they are.

- [ ] **Step 4: Run the orchestrator tests — unchanged assertions**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: the same number of passing tests as Step 1, with **zero edits** to the test file.

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add Workflow/Services/WorkflowOrchestrator.cs
git commit -m "refactor(orchestrator): extract RunSessionAsync from RunPhaseAsync

One implementation of 'drive one CLI session from a clean screen to its
completion condition', so the subtask decomposition and the subtask loop
do not each get a copy of the settle/paste/submit sequence. Pure
refactor: WorkflowOrchestratorTests passes with no edits.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 11: The subtask run plumbing — configuration, progress and the exception

**Files:**
- Create: `Workflow/Services/ISubtaskConfiguration.cs`
- Create: `Workflow/Services/SubtaskConfigurationException.cs`
- Modify: `Workflow/Services/IWorkflowOrchestrator.cs`
- Test: `Workflow.Tests/SubtaskConfigurationTests.cs` (create)

**Interfaces:**
- Produces:
  - `public interface ISubtaskConfiguration { public bool Enabled { get; } public string? WorkflowDirectory { get; } }`
  - `public sealed class SubtaskConfiguration : ISubtaskConfiguration` with `public void Update(bool enabled, string? directory)`
  - `public sealed class SubtaskConfigurationException : Exception` (three standard constructors)
  - `WorkflowRunRequest` with two new **defaulted, appended** parameters: `ISubtaskConfiguration? Subtasks = null, IProgress<SubtaskProgress>? SubtaskProgress = null`
- Consumes: `SubtaskProgress` (Task 2).

**Context:** The requirement decides at the phase-3 → phase-4 boundary: *"Wenn die Phase Review
Resolve abgeschlossen ist, soll die normale Implementierungsphase nur starten wenn subtask
checkbox false ist."* The user must therefore be able to tick the box **while phases 1–3 are
running**. A value captured into `WorkflowRunRequest` at `StartWorkflow` time could not do that,
so the request carries a live *reader* instead, which the orchestrator reads exactly once, at the
top of the Implementation phase. SPEC §8.1, §9.1.

The two new record parameters **must be appended and defaulted**, so every existing construction
site — including all of `WorkflowOrchestratorTests` and `TaskTabViewModelTests` — compiles
unchanged.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/SubtaskConfigurationTests.cs`:

```csharp
using Workflow.Models;
using Workflow.Services;
using Workflow.Tests.Fakes;

namespace Workflow.Tests;

public sealed class SubtaskConfigurationTests
{
    [Fact]
    public void NewInstance_IsDisabledWithNoDirectory()
    {
        var configuration = new SubtaskConfiguration();

        Assert.False(configuration.Enabled);
        Assert.Null(configuration.WorkflowDirectory);
    }

    [Fact]
    public void Update_PublishesBothValuesTogether()
    {
        var configuration = new SubtaskConfiguration();

        configuration.Update(enabled: true, directory: @"C:\repo\Workflows");

        Assert.True(configuration.Enabled);
        Assert.Equal(@"C:\repo\Workflows", configuration.WorkflowDirectory);
    }

    [Fact]
    public void Update_CanSwitchBackOff()
    {
        var configuration = new SubtaskConfiguration();
        configuration.Update(enabled: true, directory: @"C:\repo\Workflows");

        configuration.Update(enabled: false, directory: null);

        Assert.False(configuration.Enabled);
        Assert.Null(configuration.WorkflowDirectory);
    }

    [Fact]
    public async Task Update_IsNeverObservedHalfApplied()
    {
        // Enabled and WorkflowDirectory are published as one immutable snapshot, so a reader can
        // never see Enabled=true paired with the previous generation's directory.
        var configuration = new SubtaskConfiguration();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                configuration.Update(true, @"C:\a");
                configuration.Update(false, null);
            }
        });

        while (!cts.IsCancellationRequested)
        {
            var enabled = configuration.Enabled;
            var directory = configuration.WorkflowDirectory;

            Assert.Equal(enabled, directory is not null);
        }

        await writer;
    }

    [Fact]
    public void WorkflowRunRequest_DefaultsToNoSubtaskConfiguration()
    {
        var request = new WorkflowRunRequest(
            new TaskPaths(@"C:\code", "demo"),
            "Beschreibung",
            new FakeTerminalController(),
            new ManualPhaseSignal(),
            new Progress<PhaseProgress>(_ => { }));

        Assert.Null(request.Subtasks);
        Assert.Null(request.SubtaskProgress);
        Assert.Equal(WorkflowPhase.Specification, request.StartPhase);
    }

    [Fact]
    public void SubtaskConfigurationException_CarriesItsMessage()
    {
        var exception = new SubtaskConfigurationException("Das Workflow-Verzeichnis fehlt.");

        Assert.Equal("Das Workflow-Verzeichnis fehlt.", exception.Message);
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskConfigurationTests"`
Expected: compile error, `CS0246: SubtaskConfiguration`.

- [ ] **Step 3: Create the configuration holder**

Create `Workflow/Services/ISubtaskConfiguration.cs`:

```csharp
namespace Workflow.Services;

/// <summary>
/// The subtask settings as they stand right now. Read by the orchestrator exactly once, at the
/// top of the Implementation phase.
/// </summary>
/// <remarks>
/// A live reader rather than a value captured into <c>WorkflowRunRequest</c>: the decision is made
/// at the phase-3 to phase-4 boundary, so the user must be able to tick the checkbox while the
/// earlier phases are still running.
/// </remarks>
public interface ISubtaskConfiguration
{
    /// <summary>True when phase 4 runs as a subtask loop.</summary>
    public bool Enabled { get; }

    /// <summary>Root of the Workflows tracking repository, or null.</summary>
    public string? WorkflowDirectory { get; }
}

/// <inheritdoc cref="ISubtaskConfiguration" />
public sealed class SubtaskConfiguration : ISubtaskConfiguration
{
    private volatile Snapshot _snapshot = new(false, null);

    /// <inheritdoc />
    public bool Enabled => _snapshot.Enabled;

    /// <inheritdoc />
    public string? WorkflowDirectory => _snapshot.Directory;

    /// <summary>Publishes a new configuration. Called from the UI thread.</summary>
    /// <param name="enabled">Whether phase 4 runs as a subtask loop.</param>
    /// <param name="directory">Root of the Workflows repository, or null.</param>
    /// <remarks>
    /// One volatile reference to an immutable record, replaced wholesale, so a reader on a pool
    /// thread can never see <see cref="Enabled"/> from one generation paired with
    /// <see cref="WorkflowDirectory"/> from another.
    /// </remarks>
    public void Update(bool enabled, string? directory) => _snapshot = new Snapshot(enabled, directory);

    private sealed record Snapshot(bool Enabled, string? Directory);
}
```

- [ ] **Step 4: Create the exception**

Create `Workflow/Services/SubtaskConfigurationException.cs` (mirror
`Workflow/Services/PromptTemplateException.cs`, which already has the CA1032 constructor set):

```csharp
namespace Workflow.Services;

/// <summary>
/// The subtask phase cannot start or continue because of something the user can fix: an invalid
/// workflow directory, or an index the decomposition did not produce in a usable shape.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="ArtifactWatchException"/>: this is a configuration
/// problem, reported into the tab's validation message, after which the phase stays Active in the
/// journal so 'Continue workflow' retries it.
/// </remarks>
public sealed class SubtaskConfigurationException : Exception
{
    /// <summary>Creates the exception with the default message.</summary>
    public SubtaskConfigurationException()
        : base("Die Subtask-Konfiguration ist unvollständig.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">German message shown to the user.</param>
    public SubtaskConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">German message shown to the user.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SubtaskConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 5: Extend `WorkflowRunRequest`**

In `Workflow/Services/IWorkflowOrchestrator.cs`, append two parameters and their doc comments:

```csharp
/// <param name="Subtasks">
/// Live subtask settings, read once at the top of the Implementation phase. Null means the same
/// as disabled. Appended and defaulted so existing construction sites are unaffected.
/// </param>
/// <param name="SubtaskProgress">Receives every subtask-phase progress change, or null.</param>
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

Keep the existing `<param>` documentation for the first six parameters exactly as it is.

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskConfigurationTests"`
Expected: PASS.

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass with no edits to existing test files — that is
what proves the record parameters were appended and defaulted correctly.

- [ ] **Step 8: Commit**

```bash
git add Workflow/Services/ISubtaskConfiguration.cs Workflow/Services/SubtaskConfigurationException.cs Workflow/Services/IWorkflowOrchestrator.cs Workflow.Tests/SubtaskConfigurationTests.cs
git commit -m "feat(subtasks): carry live subtask settings into the run request

The checkbox decides at the phase-3 to phase-4 boundary, so the request
carries a live reader rather than a value captured at Start. One volatile
reference to an immutable snapshot keeps Enabled and WorkflowDirectory
from ever being read from different generations. Both new record
parameters are appended and defaulted, so no existing call site changes.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 12: The decomposition step — phase 4 branches into subtask mode

**Files:**
- Modify: `Workflow/Services/WorkflowOrchestrator.cs`
- Test: `Workflow.Tests/SubtaskOrchestratorTests.cs` (create)

**Depends on:** Tasks 1, 2, 9, 10, 11.

**Interfaces:**
- Produces (private): `private async Task RunSubtaskPhaseAsync(WorkflowRunRequest request, CancellationToken cancellationToken)`, plus the helpers `ReportSubtasks`, `DeleteStaleFlag` and `SettleStatusAsync` used again by Task 13.
- Consumes: `RunSessionAsync` (Task 10), `SubtaskLedger.IsDecomposed`/`TryRead` (Task 2), `PromptVariables.ForSubtaskCreation` (Task 9), `WorkflowDirectoryValidation.Validate` (Task 3), `SubtaskConfigurationException` (Task 11).

**Context:** SPEC §9.4 and §9.5 steps **S1–S6**. This task implements the branch and the
decomposition session only; Task 13 adds the loop. After this task, phase 4 in subtask mode runs
`create_subtasks.md`, waits for the index, and then ends the phase — the loop is a stub that Task
13 fills in.

Two deletions matter and both have precedent in `DeleteStaleDoneMarker`:
- The stale index is deleted **only** on the fresh-decomposition path (S4). On a resume the index
  is the evidence and must survive.
- `IsDecomposed` makes the decomposition idempotent: a resumed run whose index lists subtasks that
  all have a non-empty `subtask.md` skips the session entirely.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/SubtaskOrchestratorTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;
using Workflow.Tests.Fakes;

namespace Workflow.Tests;

public sealed class SubtaskOrchestratorTests : IDisposable
{
    private readonly string _root;
    private readonly string _promptDir;
    private readonly string _rulesPath;
    private readonly TaskPaths _paths;
    private readonly SubtaskPaths _subtasks;
    private readonly FakeTaskStateStore _state = new();
    private readonly List<PhaseProgress> _phaseProgress = [];
    private readonly List<SubtaskProgress> _subtaskProgress = [];
    private readonly SubtaskConfiguration _configuration = new();

    public SubtaskOrchestratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-sub-" + Guid.NewGuid().ToString("N"));
        _promptDir = Path.Combine(_root, "Prompt");
        Directory.CreateDirectory(_promptDir);

        foreach (var definition in PhaseCatalog.All)
        {
            File.WriteAllText(
                Path.Combine(_promptDir, definition.PromptFile),
                $"PROMPT {definition.Phase} {{taskbeschreibung}}");
        }

        File.WriteAllText(
            Path.Combine(_promptDir, PromptTemplateCatalog.CreateSubtasks),
            "CREATE {workflow_path} | {tasktitel} | {subtask_path} | {spec_path} | {plan_path}");

        File.WriteAllText(
            Path.Combine(_promptDir, PromptTemplateCatalog.RunSubtask),
            "RUN {workflow_path} | {subtask_path} | {subtask_title} | {subtask}");

        _rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(_rulesPath, """
        {
          "version": 2, "quietPeriodMs": 10, "settleTimeoutMs": 2000, "maxAnswersPerPhase": 3,
          "pasteQuietPeriodMs": 10, "pasteSettleTimeoutMs": 300, "submitVerifyMs": 30,
          "maxSubmitAttempts": 2, "rules": []
        }
        """);

        var workspace = Path.Combine(_root, "ws");
        var workflows = Path.Combine(_root, "Workflows");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workflows, "task_template"));

        _paths = new TaskPaths(workspace, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
        _subtasks = new SubtaskPaths(workflows, "demo");

        _configuration.Update(enabled: true, directory: workflows);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WorkflowOrchestrator CreateOrchestrator() => new(
        new PromptTemplateService(_promptDir),
        new AutoAnswerService(_rulesPath, overridePath: null),
        new ArtifactWatcherFactory(),
        _state,
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40));

    private WorkflowRunRequest CreateRequest(FakeTerminalController terminal, ManualPhaseSignal signal) =>
        new(_paths,
            "Beschreibung",
            terminal,
            signal,
            new Progress<PhaseProgress>(p => { lock (_phaseProgress) { _phaseProgress.Add(p); } }),
            WorkflowPhase.Implementation,
            _configuration,
            new Progress<SubtaskProgress>(p => { lock (_subtaskProgress) { _subtaskProgress.Add(p); } }));

    private static FakeTerminalController ReadyTerminal()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "? for shortcuts";
        return terminal;
    }

    private void WriteIndex(params string[] titles)
    {
        Directory.CreateDirectory(_subtasks.TaskDirectory);
        var list = string.Join(", ", titles.Select(t => "\"" + t + "\""));
        File.WriteAllText(_subtasks.ResultAbsolute, $$"""{"version":1,"subtasks":[{{list}}]}""");
    }

    private void WriteSubtaskFolder(string title, string? status = null)
    {
        Directory.CreateDirectory(_subtasks.SubtaskDirectory(title));
        File.WriteAllText(_subtasks.SubtaskMarkdown(title), "# " + title + "\n\nTu dies.");

        if (status is not null)
        {
            File.WriteAllText(_subtasks.SubtaskStatusFile(title), $$"""{"status":"{{status}}"}""");
        }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }

    [Fact]
    public async Task SubtaskMode_PastesTheDecompositionPromptWithTheWorkflowTokens()
    {
        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Equal($"cd \"{_paths.WorkingDirectory}\"\r", terminal.Sent[0]);
        Assert.Equal("yo\r", terminal.Sent[1]);
        Assert.Contains("CREATE", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(_subtasks.WorkflowDirectory, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(@"demo\subtasks", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT Implementation", terminal.Pasted[0], StringComparison.Ordinal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SubtaskMode_RunsInTheWorkingDirectoryNotTheWorkflowDirectory()
    {
        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.StartedSessions.Count >= 1, cts.Token);

        // Subtasks change product code, which lives in the working directory. The Workflows repo
        // is reached only through absolute paths inside the prompt.
        Assert.Equal(_paths.WorkingDirectory, terminal.StartedSessions[0]);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SubtaskMode_ReportsTheDecomposingStage()
    {
        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => _subtaskProgress.Any(p => p.Stage == SubtaskStage.Decomposing), cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SubtaskMode_PersistsTheConfigurationIntoTheJournal()
    {
        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => _state.SubtaskSettings.Count >= 1, cts.Token);

        Assert.True(_state.SubtaskSettings[0].Enabled);
        Assert.Equal(_subtasks.WorkflowDirectory, _state.SubtaskSettings[0].Directory);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SubtaskMode_DeletesAStaleIndexBeforeDecomposing()
    {
        // A leftover index from a previous run of the same task name would satisfy the watcher in
        // milliseconds - the same failure mode DeleteStaleDoneMarker exists for.
        Directory.CreateDirectory(_subtasks.TaskDirectory);
        File.WriteAllText(_subtasks.ResultAbsolute, """{"version":1,"subtasks":["ghost"]}""");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // "ghost" has no subtask.md, so IsDecomposed is false and the index is deleted.
        Assert.False(File.Exists(_subtasks.ResultAbsolute));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SubtaskMode_SkipsDecomposition_WhenTheIndexIsAlreadyUsable()
    {
        WriteIndex("ST-001-a");
        WriteSubtaskFolder("ST-001-a");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // The first prompt is the subtask, not the decomposition.
        Assert.DoesNotContain("CREATE", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.True(File.Exists(_subtasks.ResultAbsolute));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SubtaskMode_FailsWithAnActionableMessage_WhenTheWorkflowDirectoryIsInvalid()
    {
        _configuration.Update(enabled: true, directory: Path.Combine(_root, "not-a-repo"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var failure = await Assert.ThrowsAsync<SubtaskConfigurationException>(
            () => CreateOrchestrator().RunAsync(CreateRequest(ReadyTerminal(), new ManualPhaseSignal()), cts.Token));

        Assert.Contains("Workflow-Verzeichnis", failure.Message, StringComparison.Ordinal);

        // The phase must stay Active so 'Continue workflow' can retry it.
        Assert.DoesNotContain(
            (WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);
    }

    [Fact]
    public async Task SubtaskMode_FailsWithAnActionableMessage_WhenTheIndexHasNoUsableList()
    {
        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // The session "finishes" by writing a flag with no subtasks array.
        File.WriteAllText(_subtasks.ResultAbsolute, """{"version":1,"note":"vergessen"}""");

        var failure = await Assert.ThrowsAsync<SubtaskConfigurationException>(() => run);

        Assert.Contains("result.json", failure.Message, StringComparison.Ordinal);
        Assert.Contains("subtasks", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtaskMode_DoesNotRequireTheDoneMarker()
    {
        WriteIndex("ST-001-a");
        WriteSubtaskFolder("ST-001-a", status: "complete");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        Assert.False(File.Exists(_paths.DoneAbsolute));
        Assert.Contains((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);
    }

    [Fact]
    public async Task NormalMode_IsUnaffected()
    {
        _configuration.Update(enabled: false, directory: null);

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Contains("PROMPT Implementation", terminal.Pasted[0], StringComparison.Ordinal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
```

`SubtaskMode_DoesNotRequireTheDoneMarker` needs the loop from Task 13 to terminate. Until Task 13
lands, expect it to hang until its 20 s token fires — mark it `[Fact(Skip = "Task 13")]` in this
task and **remove the Skip in Task 13's Step 1**.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskOrchestratorTests"`
Expected: FAIL — the decomposition prompt is never pasted; `terminal.Pasted[0]` is
`PROMPT Implementation`.

- [ ] **Step 3: Branch phase 4**

In `WorkflowOrchestrator.RunPhaseAsync`, immediately after `request.ManualSignal.Reset();`:

```csharp
        if (definition.Phase == WorkflowPhase.Implementation && request.Subtasks is { Enabled: true })
        {
            // The checkbox is read HERE, at the phase-3 to phase-4 boundary, not at Start, so
            // ticking it while the earlier phases run still takes effect (SPEC section 8.1).
            await RunSubtaskPhaseAsync(request, cancellationToken);
            return;
        }
```

- [ ] **Step 4: Implement the decomposition step and its helpers**

Add to `WorkflowOrchestrator` (`using System.IO;` and `using Workflow.Models;` are already there):

```csharp
    private async Task RunSubtaskPhaseAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        // S1 - configuration.
        var directory = request.Subtasks?.WorkflowDirectory;
        var validation = WorkflowDirectoryValidation.Validate(directory);

        if (!validation.IsValid)
        {
            throw new SubtaskConfigurationException(validation.ErrorMessage!);
        }

        // S2 - paths, journal, first report.
        var subtasks = new SubtaskPaths(directory!, request.Paths.TaskName);
        _state.SaveSubtaskSettings(request.Paths, enabled: true, subtasks.WorkflowDirectory);
        ReportSubtasks(request, SubtaskStage.Decomposing, null, null);

        // S3 - the decomposition is idempotent: a resumed run whose index already lists subtasks
        // that all have a subtask.md skips the session entirely.
        if (!SubtaskLedger.IsDecomposed(subtasks))
        {
            // S4 - a leftover index from a previous run of this task name would satisfy the
            // watcher in milliseconds. Not deleted on the resume path above, where it IS the
            // evidence (SPEC section 9.5, S4).
            DeleteStaleFlag(subtasks.ResultAbsolute);
            Directory.CreateDirectory(subtasks.TaskDirectory);

            // S5.
            var manual = await RunSessionAsync(
                request.Terminal,
                request.ManualSignal,
                request.Paths.WorkingDirectory,
                SubtaskLauncher,
                PromptTemplateCatalog.CreateSubtasks,
                PromptVariables.ForSubtaskCreation(request.Paths, request.TaskDescription, subtasks),
                CompletionRule.FilesExist,
                subtasks.TaskDirectory,
                [subtasks.ResultAbsolute],
                cancellationToken);

            if (manual)
            {
                CompleteImplementation(request);
                return;
            }
        }

        // S6 - the index must now be usable. There is deliberately no alphabetical fallback: a
        // silent one would execute an order nobody declared (SPEC section 6.1, D9).
        var snapshot = SubtaskLedger.TryRead(subtasks)
            ?? throw new SubtaskConfigurationException(
                $"'{subtasks.ResultAbsolute}' enthält keine verwertbare Reihenfolge. Erwartet wird "
                + "ein JSON-Objekt mit einem nicht leeren Array 'subtasks', das die Ordnernamen in "
                + "Ausführungsreihenfolge auflistet.");

        ReportSubtasks(request, SubtaskStage.Running, snapshot, null);

        await RunSubtaskLoopAsync(request, subtasks, snapshot, cancellationToken);
    }

    // Filled in by Task 13. Until then the phase ends as soon as the index exists.
    private Task RunSubtaskLoopAsync(
        WorkflowRunRequest request,
        SubtaskPaths subtasks,
        SubtaskSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = subtasks;

        ReportSubtasks(request, SubtaskStage.Finished, snapshot, null);

        if (snapshot.Failed == 0 && snapshot.Completed == snapshot.Total)
        {
            CompleteImplementation(request);
        }

        return Task.CompletedTask;
    }

    private void CompleteImplementation(WorkflowRunRequest request)
    {
        _state.RecordPhase(request.Paths, WorkflowPhase.Implementation, PhaseStatus.Completed);
        request.Progress.Report(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Completed));
    }

    private static void ReportSubtasks(
        WorkflowRunRequest request, SubtaskStage stage, SubtaskSnapshot? snapshot, string? currentTitle) =>
        request.SubtaskProgress?.Report(new SubtaskProgress(
            stage,
            snapshot?.Completed ?? 0,
            snapshot?.Total ?? 0,
            snapshot?.Failed ?? 0,
            currentTitle));

    private static void DeleteStaleFlag(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held open by another process. The step then completes immediately; the user can
            // still drive the session by hand in the live terminal.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only or no permission. Same fallback as above.
        }
    }
```

Add the launcher constant next to `SnapshotLines` at the top of the class:

```csharp
    /// <summary>Launcher used for the decomposition session and for every subtask session.</summary>
    private const string SubtaskLauncher = "yo";
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskOrchestratorTests"`
Expected: PASS except the skipped `SubtaskMode_DoesNotRequireTheDoneMarker`.

- [ ] **Step 6: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; `WorkflowOrchestratorTests` still passes untouched.

- [ ] **Step 7: Commit**

```bash
git add Workflow/Services/WorkflowOrchestrator.cs Workflow.Tests/SubtaskOrchestratorTests.cs
git commit -m "feat(subtasks): branch phase 4 into the decomposition step

Reads the checkbox at the phase-3 to phase-4 boundary, validates the
workflow directory, journals the configuration and runs one
create_subtasks.md session watched on {workflow_path}/{TaskName}/result.json.
Idempotent on resume, and the stale index is deleted only when a fresh
decomposition is about to run. No alphabetical fallback: an unusable
index fails loudly with the expected shape in the message.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 13: The subtask loop

**Files:**
- Modify: `Workflow/Services/WorkflowOrchestrator.cs` (replace the `RunSubtaskLoopAsync` stub)
- Test: `Workflow.Tests/SubtaskOrchestratorTests.cs` (add cases, un-skip one)

**Depends on:** Task 12.

**Interfaces:**
- Produces (private): the real `RunSubtaskLoopAsync`, plus `SettleStatusAsync`.
- Consumes: `RunSessionAsync` (Task 10), `PromptVariables.ForSubtaskRun` (Task 9), `SubtaskLedger.TryRead` (Task 2), `SubtaskPaths.IsValidTitle` (Task 1).

**Context:** SPEC §9.5 steps **S7–S12** and §9.6. The invariants, each of which has a test below:

1. **Order is binding.** The next subtask is the first entry of the index whose status is not
   `Complete`.
2. **At most one attempt per subtask per run** (`attempted`). A subtask that keeps failing cannot
   spin the loop; a *resumed* run starts with an empty set and re-attempts the failures, which is
   the requested recovery behaviour.
3. **The ledger is re-read every iteration**, so a `status.json` the user fixed by hand, or one
   Claude updated for a later subtask, is picked up.
4. **A failed or unusable subtask is skipped, not retried.**
5. **Implementation is recorded `Completed` only when nothing failed.** Otherwise the phase stays
   `Active` in the journal and the task remains resumable — this is what makes
   *Continue workflow* re-attempt exactly the failures without a fourth `PhaseStatus` value.
6. **A manual signal ends the whole phase** and records it `Completed`.

`SettleStatusAsync` exists because `status.json` is written before the flag by prompt instruction,
not by a file-system guarantee. Five reads 200 ms apart cost nothing against a subtask session and
remove the whole class of "the JSON was mid-flush" false failures.

- [ ] **Step 1: Un-skip the pending test and add the loop tests**

Remove `(Skip = "Task 13")` from `SubtaskMode_DoesNotRequireTheDoneMarker`, then append to
`Workflow.Tests/SubtaskOrchestratorTests.cs`:

```csharp
    // Answers each subtask session by writing its status.json and then its flag, in the order
    // run_subtask.md requires. Runs on a background task so the orchestrator can be awaited.
    private Task AnswerSubtasksAsync(
        FakeTerminalController terminal,
        IReadOnlyDictionary<string, string> statusByTitle,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var answered = 0;

            while (!cancellationToken.IsCancellationRequested && answered < statusByTitle.Count)
            {
                string? pasted;
                lock (terminal.Pasted)
                {
                    pasted = terminal.Pasted.Count > answered ? terminal.Pasted[answered] : null;
                }

                if (pasted is null || !pasted.StartsWith("RUN", StringComparison.Ordinal))
                {
                    await Task.Delay(20, cancellationToken);
                    continue;
                }

                var title = statusByTitle.Keys.FirstOrDefault(
                    t => pasted.Contains("| " + t + " |", StringComparison.Ordinal));

                if (title is null)
                {
                    await Task.Delay(20, cancellationToken);
                    continue;
                }

                File.WriteAllText(
                    _subtasks.SubtaskStatusFile(title),
                    $$"""{"status":"{{statusByTitle[title]}}","failreason":"probe"}""");
                File.WriteAllText(_subtasks.SubtaskResultFile(title), "fertig");

                answered++;
            }
        }, cancellationToken);

    [Fact]
    public async Task Loop_RunsEverySubtaskInTheDeclaredOrderInItsOwnSession()
    {
        WriteIndex("ST-003-c", "ST-001-a", "ST-002-b");
        WriteSubtaskFolder("ST-003-c");
        WriteSubtaskFolder("ST-001-a");
        WriteSubtaskFolder("ST-002-b");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(terminal, new Dictionary<string, string>
        {
            ["ST-003-c"] = "complete",
            ["ST-001-a"] = "complete",
            ["ST-002-b"] = "complete",
        }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        // Index order, not alphabetical order.
        Assert.Equal(3, terminal.Pasted.Count);
        Assert.Contains("| ST-003-c |", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains("| ST-001-a |", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.Contains("| ST-002-b |", terminal.Pasted[2], StringComparison.Ordinal);

        // One fresh session per subtask, each cd'ing into the working directory.
        Assert.Equal(3, terminal.StartedSessions.Count);
        Assert.All(terminal.StartedSessions, d => Assert.Equal(_paths.WorkingDirectory, d));
        Assert.Equal(3, terminal.ClearCount);
    }

    [Fact]
    public async Task Loop_SubstitutesTheSubtaskBodyFromItsMarkdown()
    {
        WriteIndex("ST-001-a");
        Directory.CreateDirectory(_subtasks.SubtaskDirectory("ST-001-a"));
        File.WriteAllText(_subtasks.SubtaskMarkdown("ST-001-a"), "# ST-001\n\nEindeutiger Text.");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(
            terminal, new Dictionary<string, string> { ["ST-001-a"] = "complete" }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        Assert.Contains("Eindeutiger Text.", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(_subtasks.WorkflowDirectory, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(@"demo\subtasks", terminal.Pasted[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loop_SkipsAlreadyCompletedSubtasks()
    {
        WriteIndex("ST-001-a", "ST-002-b");
        WriteSubtaskFolder("ST-001-a", status: "complete");
        WriteSubtaskFolder("ST-002-b");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(
            terminal, new Dictionary<string, string> { ["ST-002-b"] = "complete" }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        Assert.Single(terminal.Pasted);
        Assert.Contains("| ST-002-b |", terminal.Pasted[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loop_SkipsAFailedSubtaskAndCarriesOn()
    {
        WriteIndex("ST-001-a", "ST-002-b");
        WriteSubtaskFolder("ST-001-a");
        WriteSubtaskFolder("ST-002-b");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(terminal, new Dictionary<string, string>
        {
            ["ST-001-a"] = "failed",
            ["ST-002-b"] = "complete",
        }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        // Attempted once each; the failure did not stop the loop and was not retried.
        Assert.Equal(2, terminal.Pasted.Count);

        var last = _subtaskProgress[^1];
        Assert.Equal(SubtaskStage.Finished, last.Stage);
        Assert.Equal(2, last.Total);
        Assert.Equal(1, last.Completed);
        Assert.Equal(1, last.Failed);
    }

    [Fact]
    public async Task Loop_LeavesImplementationActive_WhenASubtaskFailed()
    {
        WriteIndex("ST-001-a");
        WriteSubtaskFolder("ST-001-a");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(
            terminal, new Dictionary<string, string> { ["ST-001-a"] = "failed" }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        // Not Completed: that is what makes 'Continue workflow' re-attempt exactly the failures,
        // without needing a fourth PhaseStatus value.
        Assert.DoesNotContain((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);
        Assert.Contains((WorkflowPhase.Implementation, PhaseStatus.Active), _state.Recorded);
    }

    [Fact]
    public async Task Loop_RecordsImplementationCompleted_WhenEverySubtaskSucceeded()
    {
        WriteIndex("ST-001-a", "ST-002-b");
        WriteSubtaskFolder("ST-001-a");
        WriteSubtaskFolder("ST-002-b");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(terminal, new Dictionary<string, string>
        {
            ["ST-001-a"] = "complete",
            ["ST-002-b"] = "complete",
        }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        Assert.Contains((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);
        Assert.Contains(_phaseProgress,
            p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task Loop_AttemptsEachSubtaskAtMostOncePerRun()
    {
        // The session never writes anything, so the ledger keeps saying Pending. Without the
        // per-run 'attempted' set the loop would pick the same subtask for ever.
        WriteIndex("ST-001-a");
        WriteSubtaskFolder("ST-001-a");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        await Task.Delay(500, cts.Token);

        Assert.Single(terminal.Pasted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Loop_SkipsASubtaskWhoseMarkdownIsMissing()
    {
        WriteIndex("ST-001-a", "ST-002-b");
        Directory.CreateDirectory(_subtasks.SubtaskDirectory("ST-001-a"));   // no subtask.md
        WriteSubtaskFolder("ST-002-b");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(
            terminal, new Dictionary<string, string> { ["ST-002-b"] = "complete" }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        Assert.Single(terminal.Pasted);
        Assert.Contains("| ST-002-b |", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Equal(1, _subtaskProgress[^1].Failed);
    }

    [Fact]
    public async Task Loop_SkipsAnUnsafeTitleWithoutTouchingTheFileSystem()
    {
        WriteIndex(@"..\..\evil", "ST-001-a");
        WriteSubtaskFolder("ST-001-a");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var answering = AnswerSubtasksAsync(
            terminal, new Dictionary<string, string> { ["ST-001-a"] = "complete" }, cts.Token);

        await CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);
        await answering;

        Assert.Single(terminal.Pasted);
        Assert.Equal(1, _subtaskProgress[^1].Failed);
    }

    [Fact]
    public async Task Loop_DeletesAStaleSubtaskFlagBeforeStartingThatSubtask()
    {
        WriteIndex("ST-001-a");
        WriteSubtaskFolder("ST-001-a");
        File.WriteAllText(_subtasks.SubtaskResultFile("ST-001-a"), "leftover");

        // A leftover flag without a status.json reads as Failed, so the subtask IS attempted; the
        // flag must be deleted first or the session would "finish" the instant it starts.
        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.False(File.Exists(_subtasks.SubtaskResultFile("ST-001-a")));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Loop_EndsTheWholePhase_WhenTheManualSignalFires()
    {
        WriteIndex("ST-001-a", "ST-002-b");
        WriteSubtaskFolder("ST-001-a");
        WriteSubtaskFolder("ST-002-b");

        var terminal = ReadyTerminal();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        signal.Signal();

        await run;

        Assert.Single(terminal.Pasted);
        Assert.Contains((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);
    }

    [Fact]
    public async Task Loop_ReportsTheCurrentSubtaskWhileItRuns()
    {
        WriteIndex("ST-001-a");
        WriteSubtaskFolder("ST-001-a");

        var terminal = ReadyTerminal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, new ManualPhaseSignal()), cts.Token);

        await WaitUntil(() => _subtaskProgress.Any(p => p.CurrentTitle == "ST-001-a"), cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskOrchestratorTests"`
Expected: the new `Loop_*` tests FAIL — the stub never starts a subtask session, so
`terminal.Pasted` stays empty and the waits time out.

- [ ] **Step 3: Replace the stub with the real loop**

In `Workflow/Services/WorkflowOrchestrator.cs`, first add the two retry constants next to
`SnapshotLines` and `SubtaskLauncher` at the top of the class. They are added **here**, not in
Task 12, because `.editorconfig` sets `IDE0052` (unused private field) to `error` — declaring them
one task early would fail that task's build:

```csharp
    /// <summary>How long to wait between re-reads of a subtask's status.json.</summary>
    private static readonly TimeSpan StatusRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How many times to re-read a subtask's status.json before scoring it.</summary>
    private const int StatusRetryAttempts = 5;
```

Then replace the whole `RunSubtaskLoopAsync` stub:

```csharp
    private async Task RunSubtaskLoopAsync(
        WorkflowRunRequest request,
        SubtaskPaths subtasks,
        SubtaskSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // S7 - one attempt per subtask per RUN. A subtask that keeps failing cannot spin the
        // loop; a resumed run starts with an empty set and therefore re-attempts the failures,
        // which is exactly the requested recovery behaviour (SPEC section 9.5).
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manual = false;

        while (!manual)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // S8 - re-read every iteration, so a status.json fixed by hand is picked up.
            var current = SubtaskLedger.TryRead(subtasks);
            if (current is null)
            {
                break;
            }

            snapshot = current;

            var next = snapshot.Subtasks.FirstOrDefault(
                state => state.Status != SubtaskStatus.Complete && !attempted.Contains(state.Title));

            if (next is null)
            {
                break;
            }

            attempted.Add(next.Title);

            // S9 - an unsafe title or a missing description is already Failed in the ledger, so
            // skipping is all that is needed; no bookkeeping of our own.
            if (!SubtaskPaths.IsValidTitle(next.Title) || !NonEmptyFile(subtasks.SubtaskMarkdown(next.Title)))
            {
                continue;
            }

            // S10 - a leftover flag from an earlier attempt would satisfy the watcher in
            // milliseconds, exactly as a stale done marker would.
            DeleteStaleFlag(subtasks.SubtaskResultFile(next.Title));

            var body = ReadSubtaskBody(subtasks.SubtaskMarkdown(next.Title));
            if (body is null)
            {
                continue;
            }

            ReportSubtasks(request, SubtaskStage.Running, snapshot, next.Title);

            manual = await RunSessionAsync(
                request.Terminal,
                request.ManualSignal,
                request.Paths.WorkingDirectory,
                SubtaskLauncher,
                PromptTemplateCatalog.RunSubtask,
                PromptVariables.ForSubtaskRun(
                    request.Paths, request.TaskDescription, subtasks, next.Title, body),
                CompletionRule.FilesExist,
                subtasks.SubtaskDirectory(next.Title),
                [subtasks.SubtaskResultFile(next.Title)],
                cancellationToken);

            // S11 - the payload is written before the flag by prompt instruction, not by a
            // file-system guarantee; give a half-flushed file a moment before scoring it.
            await SettleStatusAsync(subtasks, next.Title, cancellationToken);

            snapshot = SubtaskLedger.TryRead(subtasks) ?? snapshot;
            ReportSubtasks(request, SubtaskStage.Running, snapshot, null);
        }

        // S12.
        var final = SubtaskLedger.TryRead(subtasks) ?? snapshot;
        ReportSubtasks(request, SubtaskStage.Finished, final, null);

        if (manual || (final.Failed == 0 && final.Completed == final.Total))
        {
            CompleteImplementation(request);
        }

        // Otherwise the journal keeps Implementation = Active, so the task stays resumable and
        // 'Continue workflow' re-attempts exactly the subtasks that are not complete.
    }

    private async Task SettleStatusAsync(
        SubtaskPaths subtasks, string title, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < StatusRetryAttempts; attempt++)
        {
            if (File.Exists(subtasks.SubtaskStatusFile(title))
                && SubtaskLedger.TryRead(subtasks) is { } snapshot
                && snapshot.Subtasks.Any(state =>
                    string.Equals(state.Title, title, StringComparison.OrdinalIgnoreCase)
                    && state.Status != SubtaskStatus.Pending))
            {
                return;
            }

            await Task.Delay(StatusRetryDelay, cancellationToken);
        }
    }

    private static string? ReadSubtaskBody(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool NonEmptyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
```

- [ ] **Step 4: Run the subtask tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskOrchestratorTests"`
Expected: PASS, including the previously skipped `SubtaskMode_DoesNotRequireTheDoneMarker`.

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; every pre-existing test still passes untouched.

- [ ] **Step 6: Commit**

```bash
git add Workflow/Services/WorkflowOrchestrator.cs Workflow.Tests/SubtaskOrchestratorTests.cs
git commit -m "feat(subtasks): run one Claude session per subtask, in index order

One fresh session per subtask, ended by that subtask's non-empty
result.json, with the payload read afterwards. Each subtask is attempted
at most once per run, failures are skipped and counted, and Implementation
is recorded Completed only when none failed - so Continue workflow
re-attempts exactly the failures without a fourth PhaseStatus value.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 14: The `Subtasks` indicator — view model and view

**Files:**
- Create: `Workflow/ViewModels/SubtaskIndicatorViewModel.cs`
- Create: `Workflow/Views/SubtaskIndicatorView.xaml`
- Create: `Workflow/Views/SubtaskIndicatorView.xaml.cs`
- Test: `Workflow.Tests/SubtaskIndicatorViewModelTests.cs` (create)

**Depends on:** Task 2 (`SubtaskProgress`, `SubtaskStage`), Task 11.

**Interfaces:**
- Produces: `SubtaskIndicatorViewModel` with observable `Stage`, `Completed`, `Total`, `Failed`, `CurrentTitle`; derived `Status` (`PhaseStatus`), `CountText`, `HasFailures`, `FailureText`, `Tooltip`; and methods `Apply(SubtaskProgress progress)` and `Reset()`.

**Context:** Mirrors `Workflow/ViewModels/PhaseIndicatorViewModel.cs` and
`Workflow/Views/PhaseIndicatorView.xaml` — read both first. It reuses the existing
`PhaseStatusToBrushConverter` by exposing a `PhaseStatus`, so **no new converter is introduced**.
SPEC §11.3, §11.4.

The `Stage` → `Status` / `CountText` table:

| `Stage` | `Status` (icon colour) | `CountText` |
|---------|------------------------|-------------|
| `Idle` | `Pending` (grey) | `Noch nicht begonnen` |
| `Decomposing` | `Active` (yellow) | `Zerlegung läuft…` |
| `Running` | `Active` (yellow) | `{Completed} von {Total}` |
| `Finished` and `Failed == 0` and `Total > 0` | `Completed` (green) | `{Completed} von {Total}` |
| `Finished` otherwise | `Active` (yellow) | `{Completed} von {Total}` |

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/SubtaskIndicatorViewModelTests.cs`:

```csharp
using Workflow.Models;
using Workflow.ViewModels;

namespace Workflow.Tests;

public sealed class SubtaskIndicatorViewModelTests
{
    private static SubtaskIndicatorViewModel Applied(
        SubtaskStage stage, int completed = 0, int total = 0, int failed = 0, string? current = null)
    {
        var vm = new SubtaskIndicatorViewModel();
        vm.Apply(new SubtaskProgress(stage, completed, total, failed, current));
        return vm;
    }

    [Fact]
    public void NewInstance_IsIdleAndGrey()
    {
        var vm = new SubtaskIndicatorViewModel();

        Assert.Equal(SubtaskStage.Idle, vm.Stage);
        Assert.Equal(PhaseStatus.Pending, vm.Status);
        Assert.Equal("Noch nicht begonnen", vm.CountText);
        Assert.False(vm.HasFailures);
    }

    [Fact]
    public void Decomposing_IsYellowAndSaysSo()
    {
        var vm = Applied(SubtaskStage.Decomposing);

        Assert.Equal(PhaseStatus.Active, vm.Status);
        Assert.Equal("Zerlegung läuft…", vm.CountText);
    }

    [Fact]
    public void Running_ShowsTheCountAndStaysYellow()
    {
        var vm = Applied(SubtaskStage.Running, completed: 12, total: 24);

        Assert.Equal(PhaseStatus.Active, vm.Status);
        Assert.Equal("12 von 24", vm.CountText);
    }

    [Fact]
    public void Finished_IsGreen_OnlyWhenNothingFailed()
    {
        Assert.Equal(PhaseStatus.Completed, Applied(SubtaskStage.Finished, 24, 24).Status);
        Assert.Equal(PhaseStatus.Active, Applied(SubtaskStage.Finished, 21, 24, failed: 3).Status);
    }

    [Fact]
    public void Finished_IsNotGreen_WhenThereAreNoSubtasksAtAll() =>
        Assert.Equal(PhaseStatus.Active, Applied(SubtaskStage.Finished).Status);

    [Fact]
    public void FailureText_IsSingularForOneFailure()
    {
        Assert.True(Applied(SubtaskStage.Finished, 23, 24, failed: 1).HasFailures);
        Assert.Equal("1 fehlgeschlagen", Applied(SubtaskStage.Finished, 23, 24, failed: 1).FailureText);
        Assert.Equal("3 fehlgeschlagen", Applied(SubtaskStage.Finished, 21, 24, failed: 3).FailureText);
    }

    [Fact]
    public void HasFailures_IsFalseWithoutFailures() =>
        Assert.False(Applied(SubtaskStage.Running, 3, 24).HasFailures);

    [Fact]
    public void Tooltip_NamesTheRunningSubtask() =>
        Assert.Contains(
            "ST-007-x",
            Applied(SubtaskStage.Running, 6, 24, current: "ST-007-x").Tooltip,
            StringComparison.Ordinal);

    [Fact]
    public void Apply_RaisesPropertyChangedForTheDerivedMembers()
    {
        var vm = new SubtaskIndicatorViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Apply(new SubtaskProgress(SubtaskStage.Running, 1, 4, 1, "ST-002"));

        Assert.Contains(nameof(SubtaskIndicatorViewModel.Status), changed);
        Assert.Contains(nameof(SubtaskIndicatorViewModel.CountText), changed);
        Assert.Contains(nameof(SubtaskIndicatorViewModel.HasFailures), changed);
        Assert.Contains(nameof(SubtaskIndicatorViewModel.FailureText), changed);
    }

    [Fact]
    public void Reset_ReturnsToIdle()
    {
        var vm = Applied(SubtaskStage.Finished, 24, 24, failed: 2, current: "ST-024");

        vm.Reset();

        Assert.Equal(SubtaskStage.Idle, vm.Stage);
        Assert.Equal(0, vm.Total);
        Assert.Equal(0, vm.Completed);
        Assert.Equal(0, vm.Failed);
        Assert.Null(vm.CurrentTitle);
        Assert.Equal(PhaseStatus.Pending, vm.Status);
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskIndicatorViewModelTests"`
Expected: compile error, `CS0246: SubtaskIndicatorViewModel`.

- [ ] **Step 3: Implement the view model**

Create `Workflow/ViewModels/SubtaskIndicatorViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Workflow.Models;

namespace Workflow.ViewModels;

/// <summary>
/// The second indicator next to 'Implementierung': how many subtasks are done, and how many
/// failed.
/// </summary>
public sealed partial class SubtaskIndicatorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private SubtaskStage _stage = SubtaskStage.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(HasFailures))]
    [NotifyPropertyChangedFor(nameof(FailureText))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private int _failed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _currentTitle;

    /// <summary>German label shown next to the icon.</summary>
    public static string DisplayName => "Subtasks";

    /// <summary>
    /// The icon colour, expressed as a phase status so the existing
    /// <c>PhaseStatusToBrushConverter</c> can be reused unchanged.
    /// </summary>
    /// <remarks>
    /// Green only when the loop has attempted everything and nothing failed. That is the same
    /// condition under which the orchestrator records the Implementation phase as Completed, so
    /// the two indicators can never disagree.
    /// </remarks>
    public PhaseStatus Status => Stage switch
    {
        SubtaskStage.Idle => PhaseStatus.Pending,
        SubtaskStage.Finished when Failed == 0 && Total > 0 => PhaseStatus.Completed,
        _ => PhaseStatus.Active,
    };

    /// <summary>The progress line, for example '12 von 24'.</summary>
    public string CountText => Stage switch
    {
        SubtaskStage.Idle => "Noch nicht begonnen",
        SubtaskStage.Decomposing => "Zerlegung läuft…",
        _ => string.Format(CultureInfo.CurrentCulture, "{0} von {1}", Completed, Total),
    };

    /// <summary>True when at least one subtask failed; shows the red line.</summary>
    public bool HasFailures => Failed > 0;

    /// <summary>The red line, for example '3 fehlgeschlagen'.</summary>
    public string FailureText =>
        string.Format(CultureInfo.CurrentCulture, "{0} fehlgeschlagen", Failed);

    /// <summary>What the indicator explains on hover.</summary>
    public string Tooltip => CurrentTitle is not null
        ? $"Aktueller Subtask: {CurrentTitle}"
        : HasFailures
            ? "Fehlgeschlagene Subtasks werden übersprungen. 'Continue workflow' versucht sie erneut."
            : "Fortschritt der Subtask-Abarbeitung";

    /// <summary>Applies one progress report from the orchestrator.</summary>
    /// <param name="progress">The reported change.</param>
    public void Apply(SubtaskProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        Stage = progress.Stage;
        Completed = progress.Completed;
        Total = progress.Total;
        Failed = progress.Failed;
        CurrentTitle = progress.CurrentTitle;
    }

    /// <summary>Returns the indicator to its untouched state.</summary>
    public void Reset() => Apply(new SubtaskProgress(SubtaskStage.Idle, 0, 0, 0, null));
}
```

- [ ] **Step 4: Run the view-model tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~SubtaskIndicatorViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Create the view**

Create `Workflow/Views/SubtaskIndicatorView.xaml`:

```xml
<UserControl x:Class="Workflow.Views.SubtaskIndicatorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:vm="clr-namespace:Workflow.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:SubtaskIndicatorViewModel}"
             mc:Ignorable="d">
    <StackPanel Margin="0,0,24,0"
                HorizontalAlignment="Left"
                ToolTip="{Binding Tooltip}">
        <StackPanel Orientation="Horizontal">
            <materialDesign:PackIcon Width="22"
                                     Height="22"
                                     VerticalAlignment="Center"
                                     Kind="FormatListChecks"
                                     Foreground="{Binding Status, Converter={StaticResource PhaseStatusToBrushConverter}}" />

            <TextBlock Margin="8,0,0,0"
                       VerticalAlignment="Center"
                       Text="Subtasks"
                       Foreground="{Binding Status, Converter={StaticResource PhaseStatusToBrushConverter}}" />
        </StackPanel>

        <TextBlock Margin="30,2,0,0"
                   FontSize="11"
                   Opacity="0.85"
                   Text="{Binding CountText}" />

        <TextBlock Margin="30,0,0,0"
                   FontSize="11"
                   Foreground="{DynamicResource MaterialDesignValidationErrorBrush}"
                   Text="{Binding FailureText}"
                   Visibility="{Binding HasFailures, Converter={StaticResource BooleanToVisibilityConverter}}" />
    </StackPanel>
</UserControl>
```

Create `Workflow/Views/SubtaskIndicatorView.xaml.cs` (copy the shape of
`Workflow/Views/PhaseIndicatorView.xaml.cs`):

```csharp
using System.Windows.Controls;

namespace Workflow.Views;

/// <summary>Code-behind for <see cref="SubtaskIndicatorView"/>.</summary>
public partial class SubtaskIndicatorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SubtaskIndicatorView() => InitializeComponent();
}
```

`PhaseStatusToBrushConverter` and `BooleanToVisibilityConverter` are already declared in
`App.xaml`'s resources (that is how `PhaseIndicatorView.xaml` reaches them) — confirm by opening
`Workflow/App.xaml`; if `PhaseStatusToBrushConverter` is keyed there, nothing needs adding.

- [ ] **Step 6: Assert the view loads**

Add to `Workflow.Tests/AppResourceTests.cs`, following the pattern that file already uses for
`PhaseIndicatorView`:

```csharp
    [WpfFact]
    public void SubtaskIndicatorView_LoadsWithItsViewModel()
    {
        var view = new Workflow.Views.SubtaskIndicatorView
        {
            DataContext = new Workflow.ViewModels.SubtaskIndicatorViewModel(),
        };

        Assert.NotNull(view.Content);
    }
```

If `AppResourceTests` merges the application resource dictionaries before instantiating a view,
do the same here — copy that setup rather than inventing a new one.

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass. A `PackIconKind` that does not exist is a
**compile** error, so a successful build proves `FormatListChecks` is valid in
MaterialDesignThemes 5.3.2.

- [ ] **Step 8: Commit**

```bash
git add Workflow/ViewModels/SubtaskIndicatorViewModel.cs Workflow/Views/SubtaskIndicatorView.xaml Workflow/Views/SubtaskIndicatorView.xaml.cs Workflow.Tests/SubtaskIndicatorViewModelTests.cs Workflow.Tests/AppResourceTests.cs
git commit -m "feat(subtasks): add the Subtasks progress indicator

Shows '12 von 24' plus a red failure count. Exposes a PhaseStatus so the
existing brush converter is reused unchanged, and goes green under
exactly the condition that makes the orchestrator record the
Implementation phase as Completed - so the two indicators cannot
disagree.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 15: Wire the checkbox, the workflow directory and the indicator into the tab

**Files:**
- Modify: `Workflow/ViewModels/TaskTabViewModel.cs`
- Modify: `Workflow/Services/IDirectoryPickerService.cs`
- Modify: `Workflow/Services/DirectoryPickerService.cs`
- Test: `Workflow.Tests/TaskTabViewModelTests.cs` (add cases)

**Depends on:** Tasks 2, 3, 4, 5, 11, 14.

**Interfaces:**
- Produces on `TaskTabViewModel`: `SubtasksEnabled` (`bool`), `WorkflowDirectory` (`string?`), `WorkflowDirectoryMessage` (`string?`), `RecentWorkflowDirectories` (`ObservableCollection<string>`), `Subtasks` (`SubtaskIndicatorViewModel`), `IsSubtaskConfigurationEditable` (`bool`), `BrowseWorkflowDirectoryCommand`, `ApplySubtaskProgress(SubtaskProgress)`.
- Produces on `IDirectoryPickerService`: `string? PickDirectory(string? initialDirectory, string title = "Arbeitsverzeichnis auswählen")` — the default keeps every existing call site compiling.

**Context:** SPEC §11.2 and §11.6. Read `Workflow/ViewModels/TaskTabViewModel.cs` first,
especially `OnWorkingDirectoryChanged`, `SyncRecentDirectories`, `CanStartWorkflow`,
`StartWorkflow`, `LoadForResume` and `ApplyProgress`.

Two existing hazards this task must respect, both already documented in that file:
- `OnWorkingDirectoryChanged` **returns early** after re-assigning a normalised value. Without
  that, the `ComboBox`'s `SelectedItem` binding writes `null` straight back. The workflow
  directory setter must do the same.
- `SyncRecentDirectories` never calls `Clear()`, for the same reason. The workflow list needs the
  same treatment, so **extract that reconciliation into a shared private static helper** rather
  than writing a second copy.

`CanStartWorkflow` gains `&& (!SubtasksEnabled || WorkflowDirectoryMessage is null)`, so the
orchestrator is never handed "enabled but no valid directory" through the normal path. Task 12's
`S1` check is the defence in depth for a directory deleted mid-run.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/TaskTabViewModelTests.cs`. `Create(...)` is the existing factory helper
in that file; extend it with an optional picker parameter if it does not already take one.

```csharp
    private static string CreateWorkflowRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "task_template"));
        return root;
    }

    [WpfFact]
    public void SubtasksEnabled_DefaultsToOff()
    {
        using var vm = Create();

        Assert.False(vm.SubtasksEnabled);
        Assert.Equal(SubtaskStage.Idle, vm.Subtasks.Stage);
    }

    [WpfFact]
    public void EnablingSubtasks_WithoutAValidDirectory_BlocksStartAndExplainsWhy()
    {
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";

        vm.SubtasksEnabled = true;

        Assert.NotNull(vm.WorkflowDirectoryMessage);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    [WpfFact]
    public void EnablingSubtasks_WithAValidDirectory_AllowsStart()
    {
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";

        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = CreateWorkflowRepo();

        Assert.Null(vm.WorkflowDirectoryMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [WpfFact]
    public void PickingAFolderWithoutTaskTemplate_IsRejected()
    {
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";
        vm.SubtasksEnabled = true;

        var notARepo = Path.Combine(Path.GetTempPath(), "wf-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notARepo);

        vm.WorkflowDirectory = notARepo;

        Assert.Contains("task_template", vm.WorkflowDirectoryMessage, StringComparison.Ordinal);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    [WpfFact]
    public void WorkflowDirectory_IsNormalisedAndRemembered()
    {
        var repo = CreateWorkflowRepo();
        using var vm = Create();

        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = repo + Path.DirectorySeparatorChar;

        Assert.Equal(repo, vm.WorkflowDirectory);
        Assert.Contains(repo, vm.RecentWorkflowDirectories);
        Assert.Equal(repo, _settings.Settings.LastWorkflowDirectory);
    }

    [WpfFact]
    public void EnablingSubtasks_PrefillsTheLastUsedWorkflowDirectory()
    {
        var repo = CreateWorkflowRepo();
        _settings.AddRecentWorkflowDirectory(repo);

        using var vm = Create();
        vm.SubtasksEnabled = true;

        Assert.Equal(repo, vm.WorkflowDirectory);
        Assert.Null(vm.WorkflowDirectoryMessage);
    }

    [WpfFact]
    public void DisablingSubtasks_ClearsTheValidationMessage()
    {
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";
        vm.SubtasksEnabled = true;
        Assert.NotNull(vm.WorkflowDirectoryMessage);

        vm.SubtasksEnabled = false;

        Assert.Null(vm.WorkflowDirectoryMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [WpfFact]
    public void BrowseWorkflowDirectory_UsesThePickerAndSetsTheProperty()
    {
        var repo = CreateWorkflowRepo();
        using var vm = Create(picker: new StubDirectoryPicker(repo));

        vm.SubtasksEnabled = true;
        vm.BrowseWorkflowDirectoryCommand.Execute(null);

        Assert.Equal(repo, vm.WorkflowDirectory);
    }

    [WpfFact]
    public void StartWorkflow_HandsTheOrchestratorTheLiveSubtaskConfiguration()
    {
        var repo = CreateWorkflowRepo();
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);

        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = repo;
        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.Subtasks);
        Assert.True(orchestrator.Request.Subtasks.Enabled);
        Assert.Equal(repo, orchestrator.Request.Subtasks.WorkflowDirectory);
        Assert.NotNull(orchestrator.Request.SubtaskProgress);
    }

    [WpfFact]
    public void TickingTheCheckboxDuringARun_IsVisibleToTheOrchestrator()
    {
        // The requirement decides at the phase-3 to phase-4 boundary, so a mid-run tick must
        // reach the running orchestrator through the live reader.
        var repo = CreateWorkflowRepo();
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);

        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";
        vm.StartWorkflowCommand.Execute(null);

        Assert.False(orchestrator.Request!.Subtasks!.Enabled);

        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = repo;

        Assert.True(orchestrator.Request.Subtasks.Enabled);
        Assert.Equal(repo, orchestrator.Request.Subtasks.WorkflowDirectory);
    }

    [WpfFact]
    public void ApplySubtaskProgress_ForwardsToTheIndicator()
    {
        using var vm = Create();

        vm.ApplySubtaskProgress(new SubtaskProgress(SubtaskStage.Running, 12, 24, 3, "ST-013"));

        Assert.Equal("12 von 24", vm.Subtasks.CountText);
        Assert.Equal("3 fehlgeschlagen", vm.Subtasks.FailureText);
    }

    [WpfFact]
    public void IsSubtaskConfigurationEditable_IsFalseOnceImplementationIsActive()
    {
        using var vm = Create();
        Assert.True(vm.IsSubtaskConfigurationEditable);

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Review, PhaseStatus.Active));
        Assert.True(vm.IsSubtaskConfigurationEditable);

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));
        Assert.False(vm.IsSubtaskConfigurationEditable);
    }

    [WpfFact]
    public void LoadForResume_RestoresTheSubtaskConfigurationAndSeedsTheIndicator()
    {
        var repo = CreateWorkflowRepo();
        var subtasks = new SubtaskPaths(repo, "demo");
        Directory.CreateDirectory(subtasks.TaskDirectory);
        File.WriteAllText(subtasks.ResultAbsolute, """{"version":1,"subtasks":["a","b","c"]}""");

        foreach (var (title, status) in new[] { ("a", "complete"), ("b", "failed"), ("c", null) })
        {
            Directory.CreateDirectory(subtasks.SubtaskDirectory(title));
            File.WriteAllText(subtasks.SubtaskMarkdown(title), "# " + title);
            if (status is not null)
            {
                File.WriteAllText(subtasks.SubtaskStatusFile(title), $$"""{"status":"{{status}}"}""");
            }
        }

        var paths = new TaskPaths(_workspace, "demo");
        var state = new TaskState
        {
            TaskDescription = "Beschreibung",
            SubtasksEnabled = true,
            WorkflowDirectory = repo,
        };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        using var vm = Create();
        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.True(vm.SubtasksEnabled);
        Assert.Equal(repo, vm.WorkflowDirectory);
        Assert.Equal("1 von 3", vm.Subtasks.CountText);
        Assert.Equal("1 fehlgeschlagen", vm.Subtasks.FailureText);
    }

    private sealed class CapturingOrchestrator : IWorkflowOrchestrator
    {
        public WorkflowRunRequest? Request { get; private set; }

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
```

`RecoverableTask`'s constructor shape must match the record in `Workflow/Models/RecoverableTask.cs`
— open it and use its actual parameter order. `_workspace` and `_settings` are the fixture's
existing fields; if they are named differently, use the existing names.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: compile error — `SubtasksEnabled`, `WorkflowDirectory`, `Subtasks` do not exist.

- [ ] **Step 3: Give the picker a title**

In `Workflow/Services/IDirectoryPickerService.cs`:

```csharp
    /// <summary>Asks the user for a directory.</summary>
    /// <param name="initialDirectory">Directory to start in, or null.</param>
    /// <param name="title">Dialog caption.</param>
    /// <returns>The chosen directory, or null when cancelled.</returns>
    public string? PickDirectory(string? initialDirectory, string title = "Arbeitsverzeichnis auswählen");
```

In `Workflow/Services/DirectoryPickerService.cs`, take the parameter and use it:

```csharp
    /// <inheritdoc />
    public string? PickDirectory(string? initialDirectory, string title = "Arbeitsverzeichnis auswählen")
    {
        // OpenFolderDialog is the .NET 8 WPF folder browser; no Windows Forms reference needed.
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        ...
```

The default keeps `BrowseDirectory` and every test stub compiling unchanged.

- [ ] **Step 4: Add the fields, properties and command to `TaskTabViewModel`**

Add the field next to the other readonly fields:

```csharp
    private readonly SubtaskConfiguration _subtaskConfiguration = new();
```

Add the observable properties next to the existing ones:

```csharp
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private bool _subtasksEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _workflowDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _workflowDirectoryMessage;
```

In the constructor, after `RecentDirectories` is built:

```csharp
        RecentWorkflowDirectories =
            new ObservableCollection<string>(settings.Settings.RecentWorkflowDirectories);
```

Add the public members:

```csharp
    /// <summary>Workflow (tracking-repository) directories offered in the ComboBox.</summary>
    public ObservableCollection<string> RecentWorkflowDirectories { get; }

    /// <summary>The 'Subtasks' indicator shown next to 'Implementierung'.</summary>
    public SubtaskIndicatorViewModel Subtasks { get; } = new();

    /// <summary>
    /// False once the Implementation phase has begun: by then the run has already read the
    /// configuration and changing it could only mislead.
    /// </summary>
    public bool IsSubtaskConfigurationEditable => _activePhase != WorkflowPhase.Implementation;

    /// <summary>Applies a subtask progress change from the orchestrator. Public for testing.</summary>
    /// <param name="progress">The reported change.</param>
    public void ApplySubtaskProgress(SubtaskProgress progress) => Subtasks.Apply(progress);

    [RelayCommand]
    private void BrowseWorkflowDirectory()
    {
        var chosen = _picker.PickDirectory(WorkflowDirectory, "Workflow-Verzeichnis auswählen");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            WorkflowDirectory = chosen;
        }
    }
```

- [ ] **Step 5: Add the two property-changed handlers**

```csharp
    partial void OnSubtasksEnabledChanged(bool value)
    {
        // Offer the last used repository rather than making the user browse for it every time.
        if (value && string.IsNullOrWhiteSpace(WorkflowDirectory)
            && !string.IsNullOrWhiteSpace(_settings.Settings.LastWorkflowDirectory))
        {
            WorkflowDirectory = _settings.Settings.LastWorkflowDirectory;
            return;     // the setter below re-validates and publishes
        }

        RefreshSubtaskConfiguration();
    }

    partial void OnWorkflowDirectoryChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RefreshSubtaskConfiguration();
            return;
        }

        // Same hazard as OnWorkingDirectoryChanged: the MRU stores the normalised form, so a raw
        // picker result would match no ComboBox item and the Selector would push null back in.
        var normalised = WorkingDirectoryPath.Normalise(value);
        if (!string.Equals(normalised, value, StringComparison.Ordinal))
        {
            WorkflowDirectory = normalised;
            return;
        }

        _settings.AddRecentWorkflowDirectory(normalised);
        _settings.Save();
        SyncCollection(RecentWorkflowDirectories, _settings.Settings.RecentWorkflowDirectories);

        RefreshSubtaskConfiguration();
    }

    // Validates, publishes to the live reader the orchestrator holds, and persists to the journal
    // when a task folder is already known.
    private void RefreshSubtaskConfiguration()
    {
        WorkflowDirectoryMessage = SubtasksEnabled
            ? WorkflowDirectoryValidation.Validate(WorkflowDirectory).ErrorMessage
            : null;

        var usable = SubtasksEnabled && WorkflowDirectoryMessage is null;
        _subtaskConfiguration.Update(usable, usable ? WorkflowDirectory : null);

        if (!SubtasksEnabled)
        {
            Subtasks.Reset();
        }

        OnPropertyChanged(nameof(IsSubtaskConfigurationEditable));

        if (_folderOnDisk is not null && !string.IsNullOrWhiteSpace(WorkingDirectory)
            && !string.IsNullOrWhiteSpace(TaskName))
        {
            _stateStore.SaveSubtaskSettings(
                new TaskPaths(WorkingDirectory, TaskName), usable, usable ? WorkflowDirectory : null);
        }
    }
```

- [ ] **Step 6: Share the MRU reconciliation and extend the start gate**

Replace `SyncRecentDirectories` with a call to a shared helper, keeping its comment verbatim:

```csharp
    // Deliberately never Clear()s. TaskTabView.xaml binds these collections to a ComboBox's
    // ItemsSource while SelectedItem is bound TwoWay to a property, so emptying one makes the
    // Selector drop the selection and write null back into that property. Re-adding the entries
    // afterwards does not restore the selection: the box goes blank, validation reports a missing
    // directory and 'Start workflow' dies the moment a second directory is picked.
    private static void SyncCollection(ObservableCollection<string> target, IList<string> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var at = target.IndexOf(desired[i]);

            if (at < 0)
            {
                target.Insert(i, desired[i]);
            }
            else if (at != i)
            {
                target.Move(at, i);
            }
        }
    }

    private void SyncRecentDirectories() =>
        SyncCollection(RecentDirectories, _settings.Settings.RecentDirectories);
```

Extend `CanStartWorkflow`:

```csharp
        && (!SubtasksEnabled || WorkflowDirectoryMessage is null)
```

- [ ] **Step 7: Pass the configuration into the run and forward progress**

In `StartWorkflow`, replace the request construction:

```csharp
        var request = new WorkflowRunRequest(
            paths,
            TaskDescription,
            Terminal,
            _manualSignal,
            new Progress<PhaseProgress>(ApplyProgress),
            ResumePhase ?? WorkflowPhase.Specification,
            _subtaskConfiguration,
            new Progress<SubtaskProgress>(ApplySubtaskProgress));
```

Immediately before it, persist the configuration alongside the description:

```csharp
        _stateStore.SaveSubtaskSettings(
            paths, _subtaskConfiguration.Enabled, _subtaskConfiguration.WorkflowDirectory);
```

In `ApplyProgress`, after the existing `NotifyCanExecuteChanged` calls:

```csharp
        OnPropertyChanged(nameof(IsSubtaskConfigurationEditable));
```

Add `SubtaskConfigurationException` to `RunAsync`'s catch chain, next to `ArtifactWatchException`:

```csharp
        catch (SubtaskConfigurationException ex)
        {
            // The workflow directory or the generated index is unusable. The phase stays Active
            // in the journal, so 'Continue workflow' retries once the user has fixed it.
            ValidationMessage = ex.Message;
        }
```

- [ ] **Step 8: Restore the configuration on resume**

In `LoadForResume`, inside the existing `_suppressFolderSync` block, after `TaskName` is assigned:

```csharp
            SubtasksEnabled = task.State.SubtasksEnabled;

            if (task.State.SubtasksEnabled && !string.IsNullOrWhiteSpace(task.State.WorkflowDirectory))
            {
                WorkflowDirectory = task.State.WorkflowDirectory;
                SeedSubtaskIndicator(task.Paths.TaskName, task.State.WorkflowDirectory);
            }
```

and add:

```csharp
    // Disk is the truth: a recovered tab must show what the files say, not a remembered count.
    private void SeedSubtaskIndicator(string taskName, string workflowDirectory)
    {
        try
        {
            var snapshot = SubtaskLedger.TryRead(new SubtaskPaths(workflowDirectory, taskName));

            if (snapshot is not null)
            {
                Subtasks.Apply(new SubtaskProgress(
                    SubtaskStage.Running, snapshot.Completed, snapshot.Total, snapshot.Failed, null));
            }
        }
        catch (ArgumentException)
        {
            // A stored path we cannot interpret. The indicator simply stays at Idle.
        }
    }
```

- [ ] **Step 9: Run the tab tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: PASS, including every pre-existing case.

- [ ] **Step 10: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass.

- [ ] **Step 11: Commit**

```bash
git add Workflow/ViewModels/TaskTabViewModel.cs Workflow/Services/IDirectoryPickerService.cs Workflow/Services/DirectoryPickerService.cs Workflow.Tests/TaskTabViewModelTests.cs
git commit -m "feat(subtasks): wire the checkbox, directory and indicator into the tab

Start workflow is blocked while subtask mode is on without a valid
Workflows repository, so the orchestrator never sees enabled-without-
directory. The configuration is published into the live reader the run
holds, so ticking the box during phases 1-3 still takes effect. A
recovered tab restores both fields and seeds the counter from disk.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 16: The tab's XAML — checkbox next to the indicator, and the directory row

**Files:**
- Modify: `Workflow/Views/TaskTabView.xaml`
- Test: `Workflow.Tests/AppResourceTests.cs` (add a case)

**Depends on:** Tasks 14, 15.

**Interfaces:**
- Consumes: `TaskTabViewModel.SubtasksEnabled`, `WorkflowDirectory`, `WorkflowDirectoryMessage`, `RecentWorkflowDirectories`, `Subtasks`, `IsSubtaskConfigurationEditable`, `BrowseWorkflowDirectoryCommand` (Task 15); `SubtaskIndicatorView` (Task 14).
- Produces: no new public API.

**Context:** SPEC §11.1, §11.2. The requirement is that the checkbox sits *next to* the
*Implementierung* indicator, so the existing `ItemsControl` over `Phases` and the new controls go
into **one outer `WrapPanel`** — they then wrap together on a narrow window.

- [ ] **Step 1: Write the failing test**

Append to `Workflow.Tests/AppResourceTests.cs`, following that file's existing pattern for loading
a view with a real view model:

```csharp
    [WpfFact]
    public void TaskTabView_LoadsAndExposesTheSubtaskControls()
    {
        using var vm = CreateTaskTabViewModel();     // reuse the helper this file already has
        var view = new Workflow.Views.TaskTabView { DataContext = vm };

        view.Measure(new System.Windows.Size(1200, 900));

        var checkBox = FindVisualChild<System.Windows.Controls.CheckBox>(view);
        Assert.NotNull(checkBox);
        Assert.Equal("Subtasks", checkBox.Content);
        Assert.Equal(
            "Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks",
            checkBox.ToolTip);
    }
```

If `AppResourceTests` has no `FindVisualChild` helper, add one:

```csharp
    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
```

If the file has no `CreateTaskTabViewModel` helper, copy the `Create()` factory from
`TaskTabViewModelTests` rather than inventing a different construction.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~AppResourceTests"`
Expected: FAIL — `Assert.NotNull(checkBox)` fails, there is no `CheckBox` in the tab.

- [ ] **Step 3: Add the workflow-directory row**

In `Workflow/Views/TaskTabView.xaml`, immediately after the existing Arbeitsverzeichnis
`DockPanel` and before the `Start workflow` button:

```xml
                <DockPanel Margin="0,12,0,0" LastChildFill="True"
                           Visibility="{Binding SubtasksEnabled,
                                                Converter={StaticResource BooleanToVisibilityConverter}}">
                    <Button DockPanel.Dock="Right"
                            Margin="8,0,0,0"
                            ToolTip="Workflow-Verzeichnis auswählen"
                            Style="{StaticResource MaterialDesignIconButton}"
                            Command="{Binding BrowseWorkflowDirectoryCommand}">
                        <materialDesign:PackIcon Kind="FolderOpen" />
                    </Button>

                    <ComboBox materialDesign:HintAssist.Hint="Workflow-Verzeichnis"
                              IsEditable="False"
                              IsEnabled="{Binding IsSubtaskConfigurationEditable}"
                              ItemsSource="{Binding RecentWorkflowDirectories}"
                              SelectedItem="{Binding WorkflowDirectory, Mode=TwoWay}" />
                </DockPanel>

                <TextBlock Margin="4,4,0,0"
                           Foreground="{DynamicResource MaterialDesignValidationErrorBrush}"
                           TextWrapping="Wrap"
                           Text="{Binding WorkflowDirectoryMessage}"
                           Visibility="{Binding SubtasksEnabled,
                                                Converter={StaticResource BooleanToVisibilityConverter}}" />
```

- [ ] **Step 4: Put the indicators, the checkbox and the subtask indicator in one row**

Replace the existing `<ItemsControl Margin="0,20,0,0" ItemsSource="{Binding Phases}"> … </ItemsControl>`
block with:

```xml
                <WrapPanel Margin="0,20,0,0" Orientation="Horizontal">

                    <ItemsControl ItemsSource="{Binding Phases}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <WrapPanel Orientation="Horizontal" />
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <views:PhaseIndicatorView />
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>

                    <CheckBox Margin="0,0,24,0"
                              VerticalAlignment="Top"
                              Content="Subtasks"
                              IsChecked="{Binding SubtasksEnabled, Mode=TwoWay}"
                              IsEnabled="{Binding IsSubtaskConfigurationEditable}"
                              ToolTip="Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks" />

                    <!-- The wrapping StackPanel keeps the tab's DataContext for the Visibility
                         binding while the inner view receives Subtasks. Binding both on one
                         element is not possible. -->
                    <StackPanel Orientation="Horizontal"
                                VerticalAlignment="Top"
                                Visibility="{Binding SubtasksEnabled,
                                                     Converter={StaticResource BooleanToVisibilityConverter}}">
                        <views:SubtaskIndicatorView DataContext="{Binding Subtasks}" />
                    </StackPanel>

                </WrapPanel>
```

- [ ] **Step 5: Run the resource test and verify it passes**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~AppResourceTests"`
Expected: PASS.

- [ ] **Step 6: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass.

- [ ] **Step 7: Look at it**

Run: `dotnet run --project Workflow/Workflow.csproj`
Confirm by eye: the four phase indicators, then the `Subtasks` checkbox, on one row; ticking it
reveals the Workflow-Verzeichnis row and the second indicator reading `Noch nicht begonnen`;
unticking hides both. Close the window.

- [ ] **Step 8: Commit**

```bash
git add Workflow/Views/TaskTabView.xaml Workflow.Tests/AppResourceTests.cs
git commit -m "feat(subtasks): show the Subtasks checkbox and indicator on the tab

The phase indicators, the checkbox and the subtask indicator share one
WrapPanel, so the checkbox sits next to 'Implementierung' and the row
wraps as a unit. The Workflow-Verzeichnis picker appears only while the
box is ticked.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 17: Remove `Phase abschliessen`; `Task abschliessen` closes its tab

**Files:**
- Modify: `Workflow/Views/PhaseIndicatorView.xaml`
- Modify: `Workflow/ViewModels/PhaseIndicatorViewModel.cs`
- Modify: `Workflow/ViewModels/TaskTabViewModel.cs`
- Test: `Workflow.Tests/TaskTabViewModelTests.cs` (add cases)

**Depends on:** Task 15.

**Interfaces:**
- Removes: `TaskTabViewModel.CompleteCurrentPhase`, `CanCompleteCurrentPhase`, the generated `CompleteCurrentPhaseCommand`, and `PhaseIndicatorViewModel.IsActive`.
- Changes: `CompleteTask` becomes `private async Task CompleteTaskAsync()`. The MVVM Toolkit strips the `Async` suffix, so the generated member is still called `CompleteTaskCommand`; its type becomes `IAsyncRelayCommand`, which still exposes `CanExecute` and `NotifyCanExecuteChanged`.
- Adds (private): `private Task? _runTask;`

**Context:** SPEC §12. Phases now end **only** on their flag/artefact condition. `ManualPhaseSignal`
stays — `Task abschliessen` still uses it, and `RunSessionAsync` still races it, which is what lets
a run end promptly rather than only on cancellation.

**State this plainly when reviewing:** there is no longer any way to end phases 1–3 by hand. If a
CLI never writes its artefact, that phase waits indefinitely. That is the intended design; the
terminal stays live and interactive, so the user drives the CLI to produce the file. No timeout is
introduced.

Awaiting the run **before** raising `CloseRequested` is load-bearing: `CloseTab` disposes the tab,
which cancels the run's `CancellationTokenSource`. Closing first would turn the orderly
"manual signal → record `Completed` → return" into an `OperationCanceledException`, the journal
would keep Implementation `Active`, and the task would be offered for recovery even though the
user just declared it finished.

A grep on 2026-09-17 found **no** test referencing `CompleteCurrentPhaseCommand`, so no existing
assertion should need deleting. If one appears, delete that single test — do not keep the command.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/TaskTabViewModelTests.cs`:

```csharp
    [WpfFact]
    public void CompleteCurrentPhaseCommand_NoLongerExists() =>
        // Phases end only on flag files now.
        Assert.Null(typeof(TaskTabViewModel).GetProperty("CompleteCurrentPhaseCommand"));

    [WpfFact]
    public void PhaseIndicatorViewModel_NoLongerExposesIsActive() =>
        Assert.Null(typeof(PhaseIndicatorViewModel).GetProperty("IsActive"));

    [WpfFact]
    public async Task CompleteTask_SignalsTheRunAndThenRequestsClose()
    {
        using var vm = Create(orchestrator: new SignalAwareOrchestrator());

        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";
        vm.StartWorkflowCommand.Execute(null);
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        Assert.True(vm.CompleteTaskCommand.CanExecute(null));
        await vm.CompleteTaskCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.False(vm.IsRunning);
    }

    // Ends its run only when the manual signal fires, so the test proves CompleteTask awaits the
    // run rather than closing the tab out from under it.
    private sealed class SignalAwareOrchestrator : IWorkflowOrchestrator
    {
        public async Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            await request.ManualSignal.WaitAsync(cancellationToken);
        }
    }
```

`CompleteTaskCommand.ExecuteAsync` requires `vm.CompleteTaskCommand` to be an
`IAsyncRelayCommand`; add `using CommunityToolkit.Mvvm.Input;` to the test file if it is not
already there.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: `CompleteCurrentPhaseCommand_NoLongerExists` and
`PhaseIndicatorViewModel_NoLongerExposesIsActive` FAIL; `CompleteTask_SignalsTheRunAndThenRequestsClose`
fails to compile (`ExecuteAsync` is not on `IRelayCommand`).

- [ ] **Step 3: Remove the button from the phase indicator view**

In `Workflow/Views/PhaseIndicatorView.xaml`, delete the whole `<Button …>` element. The control's
content becomes only the horizontal `StackPanel` with the icon and the label; the outer
`StackPanel` can stay so the layout margins are unchanged.

- [ ] **Step 4: Remove `IsActive`**

In `Workflow/ViewModels/PhaseIndicatorViewModel.cs`, delete the `IsActive` property and the
`[NotifyPropertyChangedFor(nameof(IsActive))]` attribute on `_status`. The field keeps
`[ObservableProperty]`.

- [ ] **Step 5: Remove the phase-completion command**

In `Workflow/ViewModels/TaskTabViewModel.cs`, delete:

```csharp
    private bool CanCompleteCurrentPhase() => IsRunning && _activePhase is not null;

    [RelayCommand(CanExecute = nameof(CanCompleteCurrentPhase))]
    private void CompleteCurrentPhase() => _manualSignal.Signal();
```

and the line `CompleteCurrentPhaseCommand.NotifyCanExecuteChanged();` in `ApplyProgress`.

`_activePhase` **stays** — `CanCompleteTask` and `IsSubtaskConfigurationEditable` both use it.

- [ ] **Step 6: Make `Task abschliessen` await the run and close the tab**

Add the field next to `_run`:

```csharp
    private Task? _runTask;
```

In `StartWorkflow`, replace `_ = RunAsync(request, _run.Token);` with:

```csharp
        _runTask = RunAsync(request, _run.Token);
```

Replace the command:

```csharp
    private bool CanCompleteTask() => IsRunning && _activePhase == WorkflowPhase.Implementation;

    [RelayCommand(CanExecute = nameof(CanCompleteTask))]
    private async Task CompleteTaskAsync()
    {
        _manualSignal.Signal();

        // Awaiting BEFORE closing is load-bearing: CloseTab disposes this tab, which cancels the
        // run. Closing first would turn 'signal -> record Completed -> return' into an
        // OperationCanceledException, the journal would keep Implementation Active, and the task
        // would be offered for recovery even though the user just declared it finished.
        // RunAsync catches every exception it can produce and never rethrows.
        var run = _runTask;
        if (run is not null)
        {
            await run;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
```

`Dispose` needs no change: it already cancels `_run`, and an awaited-to-completion `_runTask`
holds nothing that needs disposing.

- [ ] **Step 7: Run the tab tests and verify they pass**

Run: `dotnet test Workflow.sln -c Debug --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: PASS.

- [ ] **Step 8: Build and run the whole suite**

Run: `dotnet build Workflow.sln -c Debug && dotnet test Workflow.sln -c Debug`
Expected: `0 Warning(s) 0 Error(s)`; all tests pass. A leftover binding to
`CompleteCurrentPhaseCommand` would be a *silent* runtime binding failure, not a build error —
which is why Step 3 and Step 5 must be done together and `AppResourceTests` must still pass.

- [ ] **Step 9: Commit**

```bash
git add Workflow/Views/PhaseIndicatorView.xaml Workflow/ViewModels/PhaseIndicatorViewModel.cs Workflow/ViewModels/TaskTabViewModel.cs Workflow.Tests/TaskTabViewModelTests.cs
git commit -m "feat(subtasks): phases end on flag files only; Task abschliessen closes the tab

Removes the 'Phase abschliessen' button, its command and the IsActive
property that existed only to show it. ManualPhaseSignal stays: 'Task
abschliessen' still uses it, and it now awaits the run to completion
before requesting the close, so the journal records the phase as
Completed instead of being cancelled out from under it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 18: Acceptance gate — verify every criterion end to end

**Files:**
- Modify: `Workflow/verify.ps1` (add the new checks, keeping the existing ones)
- Test: none new; this task runs everything.

**Depends on:** every previous task, including Task 7 (**PROMPT CHANGE**) and Task 8
(**EXTERNAL REPO**), which must both be done before the manual walkthrough.

**Context:** `Workflow/verify.ps1` is the acceptance gate named in `implementation_prompt.md` and
`run_subtask.md`; it must exit 0. Read it before editing and follow its existing check style.

- [ ] **Step 1: Extend `verify.ps1` with the structural checks**

Add, in the style the file already uses (each check writes a line and sets a failure flag):

```powershell
# --- Subtask mode: structural guarantees ------------------------------------

# The four phase templates plus the two subtask templates must all be present in the output.
$requiredPrompts = @(
    'initial_prompt.md', 'review_prompt.md', 'resolve_review_prompt.md',
    'implementation_prompt.md', 'create_subtasks.md', 'run_subtask.md')

foreach ($name in $requiredPrompts) {
    $path = Join-Path $PSScriptRoot "Prompt\$name"
    if (-not (Test-Path $path)) { Fail "Prompt-Datei fehlt: $name" }
    elseif ((Get-Item $path).Length -eq 0) { Fail "Prompt-Datei ist leer: $name" }
}

# {task_path} was never a token the application substitutes.
$createText = Get-Content (Join-Path $PSScriptRoot 'Prompt\create_subtasks.md') -Raw
if ($createText -match '\{task_path\}') { Fail 'create_subtasks.md verwendet noch {task_path}.' }
if ($createText -match '\{subtask_title\}') { Fail 'create_subtasks.md beansprucht {subtask_title} als App-Token.' }

# One flag name, in the task folder, never at the shared repository root.
foreach ($name in @('create_subtasks.md', 'run_subtask.md')) {
    $text = Get-Content (Join-Path $PSScriptRoot "Prompt\$name") -Raw
    if ($text -match 'results\.json') { Fail "$name nennt noch results.json (Plural)." }
}
if ($createText -notmatch '\{workflow_path\}/\{tasktitel\}/result\.json') {
    Fail 'create_subtasks.md legt die Flag-Datei nicht im Task-Ordner an.'
}

# The removed manual exit must be gone from both the view and the view model.
$indicatorXaml = Get-Content (Join-Path $PSScriptRoot 'Views\PhaseIndicatorView.xaml') -Raw
if ($indicatorXaml -match 'Phase abschliessen') { Fail "'Phase abschliessen' ist noch im XAML." }
if ($indicatorXaml -match 'CompleteCurrentPhaseCommand') { Fail 'CompleteCurrentPhaseCommand wird noch gebunden.' }

$tabViewModel = Get-Content (Join-Path $PSScriptRoot 'ViewModels\TaskTabViewModel.cs') -Raw
if ($tabViewModel -match 'CompleteCurrentPhase') { Fail 'CompleteCurrentPhase existiert noch im ViewModel.' }
```

If `verify.ps1` uses a different failure helper than `Fail`, use whatever it defines.

- [ ] **Step 2: Run the full automated gate**

```bash
dotnet build C:/Users/Marco/Documents/repo/Workflow/Workflow.sln -c Debug
dotnet test  C:/Users/Marco/Documents/repo/Workflow/Workflow.sln -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/Marco/Documents/repo/Workflow/Workflow/verify.ps1
```

Expected: `0 Warning(s) 0 Error(s)`; every test passes; `verify.ps1` exits 0 (`echo $?` prints 0).

- [ ] **Step 3: Confirm the shipped prompts carry only their own tokens**

```bash
grep -oE '\{[A-Za-z_][A-Za-z0-9_]*\}' Workflow/Prompt/create_subtasks.md | sort -u
grep -oE '\{[A-Za-z_][A-Za-z0-9_]*\}' Workflow/Prompt/run_subtask.md | sort -u
```

Expected: `create_subtasks.md` shows only `{plan_path} {spec_path} {subtask_path} {tasktitel}
{workflow_path}`; `run_subtask.md` shows those plus `{subtask} {subtask_title}` and whatever of
`{AppDirectory} {taskbezeichnung}` its untouched body uses. **No `{task_path}`.**

- [ ] **Step 4: Manual end-to-end walkthrough**

Prerequisite: Task 7 and Task 8 are committed.

```bash
dotnet run --project Workflow/Workflow.csproj
```

Work through this list and tick each line. The acceptance-criterion IDs are from SPEC §17.

1. **A1** New tab. The `Subtasks` checkbox sits next to the *Implementierung* indicator; hovering
   shows *"Wenn die Aufgabe das Kontextfenster eines Agents übersteigt, aktiviere Subtasks"*.
2. **A2** Tick it — the Workflow-Verzeichnis row appears. Untick — it disappears.
3. **A3** Pick a folder without `task_template`. A German message appears and `Start workflow`
   is disabled.
4. **A2/A4** Pick `C:\Users\Marco\Documents\repo\Workflows`. The message clears. Close and reopen
   the app; a new tab preselects that directory once the box is ticked.
5. Name the task `subtask-probe`, write a short Taskbeschreibung, pick an Arbeitsverzeichnis.
6. **A5** `Start workflow`. Open `<Arbeitsverzeichnis>\subtask-probe\.workflow-state.json` and
   confirm `"subtasksEnabled": true` and the `"workflowDirectory"` value.
7. Let phases 1–3 run, or seed `subtask-probe_spec.md`, `subtask-probe_plan.md` and
   `subtask-probe-review.md` by hand and use `Continue workflow`.
8. **A7/A17** At phase 4 the indicator reads `Zerlegung läuft…` and the terminal starts `yo` and
   receives the `create_subtasks.md` text with the Workflows path substituted.
9. **A7** Confirm `{Workflows}\subtask-probe\result.json` appears, is non-empty, and has an ordered
   `subtasks` array with a folder for every entry.
10. **A9/A17** The indicator flips to `0 von N` and a **new** session starts for the first subtask
    (the terminal clears, `cd` is re-sent, `yo` starts again).
11. **A10** In the pasted text, confirm the subtask's own `subtask.md` content appears and that
    the composed path `{Workflows}\subtask-probe\subtasks\<title>` is correct — no doubled
    `subtask-probe\subtasks\subtask-probe\subtasks`.
12. **A12/A18** Before the loop reaches the *second* subtask, hand-write into its folder a
    `status.json` with `"status": "failed"` and a non-empty `result.json`. Confirm the loop skips
    it and the indicator shows `… fehlgeschlagen` in red.
13. **A20** Kill the app mid-loop (close the window). Restart. Confirm the task is offered, the
    checkbox and directory are restored, and the counter matches the files on disk.
14. **A21** `Continue workflow`. Confirm it resumes at the first non-complete subtask and does
    **not** re-run a completed one, and that the decomposition session does **not** run again.
15. **A15** Let the run finish with the seeded failure still present. Confirm *Implementierung*
    stays yellow and the task is still offered for recovery on the next launch.
16. **A14/A19** Set the seeded failure's `status.json` to `"status": "complete"`, run
    `Continue workflow` once more, and confirm both icons turn green and
    `.workflow-state.json` records Implementation as `Completed`.
17. **A22** Restart the app. Confirm the finished task is **not** offered for recovery, even
    though `subtask-probe-done.md` does not exist.
18. **A23** Confirm no `Phase abschliessen` button exists under any phase indicator.
19. **A24** On a running task in phase 4, press `Task abschliessen`. Confirm the run ends and the
    tab closes.
20. **A6** Finally, run one task with the checkbox **unticked** and confirm phase 4 behaves exactly
    as before: `implementation_prompt.md` is pasted and the phase ends on
    `<task>-done.md`.

- [ ] **Step 5: Commit**

```bash
git add Workflow/verify.ps1
git commit -m "chore(verify): gate the subtask-mode structural guarantees

Checks that both subtask templates ship and are non-empty, that
{task_path} and the {subtask_title} claim are gone from
create_subtasks.md, that no prompt still says results.json, that the flag
lives in the task folder rather than the repository root, and that the
removed 'Phase abschliessen' binding is really gone - the one failure in
this change that would otherwise be silent at runtime.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Appendix: spec-to-task coverage

| SPEC section | Task |
|--------------|------|
| §5.2 `SubtaskPaths`, §5.3 token contract | 1, 9 |
| §6.1 index, §6.2 status payload, §6.3 subtask flag | 2, 7 |
| §7 `SubtaskLedger` and the derivation table | 2 |
| §8.1 `ISubtaskConfiguration` | 11 |
| §8.2 settings | 4 |
| §8.3 journal fields, version stays 1 | 5 |
| §8.4 `WorkflowDirectoryValidation` | 3 |
| §9.1 `WorkflowRunRequest`, §9.2 `SubtaskProgress` | 11, 2 |
| §9.3 `RunSessionAsync` extraction | 10 |
| §9.4 phase-4 branch, §9.5 S1–S6 | 12 |
| §9.5 S7–S12, §9.6 `SettleStatusAsync` | 13 |
| §9.7 `SubtaskConfigurationException` | 11, 15 |
| §10 handshake analysis (non-empty flag, task-folder location, ordering, naming) | 7, 8, 18 |
| §11.1 placement, §11.2 directory row | 16 |
| §11.3/§11.4 indicator | 14 |
| §11.5 Implementierung colour | 13 (completion rule), 14 |
| §11.6 tab view model | 15 |
| §12.1 remove `Phase abschliessen`, §12.2 tab-closing `Task abschliessen` | 17 |
| §13.1 disk-is-truth recovery | 13 |
| §13.2 `PhaseReconciliation` | 6 |
| §13.3 recovered tab display | 15 |
| §14.1/§14.2 prompt corrections | 7 |
| §14.3 `PromptTemplateCatalog` + `ValidateAll`, §14.4 variable builders | 9 |
| §15 `task_template` changes | 8 |
| §16 failure modes E1–E17 | 2, 3, 12, 13, 15 |
| §17 acceptance criteria A1–A27 | 18 |
| §18 verification steps | 18 |

Every SPEC section maps to at least one task. The E-rows of §16 are covered as follows: E1/E2 by
Task 15's validation tests, E3/E5 by Task 12's `SubtaskConfigurationException` tests, E4 by Task 7
and Task 18's grep, E6/E7 by Task 2's derivation tests and Task 13's skip tests, E8 by design
(no timeout, asserted by the absence of one), E9 by Task 13's one-attempt test, E10/E11 by Task
13's completion tests, E12 by Task 13's manual-signal test and Task 17, E13 by Task 12's and
Task 13's stale-flag tests, E14 documented only, E15/E16 by Task 5's and Task 4's
backward-compatibility tests, E17 by the pre-existing `ArtifactWatcher` poll.
