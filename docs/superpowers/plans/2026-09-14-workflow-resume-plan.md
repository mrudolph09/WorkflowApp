---
description: "Canonical Superpowers implementation plan for workflow run tracking, crash recovery ('Continue workflow'), phase-4 completion detection and the prompt auto-submit fix"
summary: "Eleven TDD tasks, in dependency order: (1) TaskPaths gains StateAbsolute/DoneAbsolute/DoneRelative; (2) the {done_path} prompt token plus the implementation_prompt.md completion-signal section and verify.ps1; (3) CompletionRule.AllContentChanged for phase 3; (4) phase 4 becomes FilesExist(DoneAbsolute), CompletionRule.Manual is deleted, stale marker removed before arming the watcher; (5) TaskState model + TaskStateStore (atomic tmp+Move, never throws, normalises the Phases array); (6) WorkflowRunRequest.StartPhase + journal writes from the orchestrator; (7) the auto-submit fix - ITerminalController.SendPaste replaces SendPasteAsync, the submit dance moves into WorkflowOrchestrator.SendPromptAsync behind a quiet gate with an OutputCount verify and one retry, tunables in autoanswer.rules.json v2 normalised on load; (8) TaskRecoveryScanner with the demote-never-promote reconciliation; (9) TaskTabViewModel ResumePhase/IsRecovered/StartButtonLabel/LoadForResume/NotifyClosedByUser; (10) MainWindowViewModel.InitialiseAsync + CloseTab dismissal + App.xaml.cs and TaskTabView.xaml wiring; (11) the full acceptance gate. Signature churn is enumerated per task: TaskTabViewModel's constructor gains ITaskStateStore after settings, which touches four test files."
paths:
  - "../specs/2026-09-14-workflow-resume-design.md"
  - "../specs/specification.md"
  - "../../../Workflow/verify.ps1"
---

# Workflow Run Tracking, Crash Recovery and Auto-Submit — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an interrupted Workflow run resumable from the phase it died in, give phases 3 and 4 real completion detection, and make every phase's prompt submit itself.

**Architecture:** A per-task journal file `W\T\.workflow-state.json` is written by `WorkflowOrchestrator` at every phase transition; at startup `TaskRecoveryScanner` walks the directory MRU one level deep and offers unfinished tasks as prefilled tabs whose button reads *Continue workflow*, which re-enters the pipeline via a new `WorkflowRunRequest.StartPhase`. Phase 4 gains a `{done_path}` marker file so it completes without a click, phase 3 requires *both* spec and plan to change, and the prompt's submitting carriage return moves out of `TerminalViewModel` into the orchestrator behind a quiet gate with an `OutputCount`-based verification and one retry.

**Tech Stack:** C# 12, .NET 8 (`net8.0-windows`), WPF, CommunityToolkit.Mvvm 8.4.2, MahApps.Metro 2.4.11, MaterialDesignThemes 5.3.2, WebView2 1.0.3351.48, `System.Text.Json` from the shared framework, xUnit 2.9.2 + Xunit.StaFact 1.1.11.

**Spec:** `docs/superpowers/specs/2026-09-14-workflow-resume-design.md` (referred to below as **SPEC**). It extends `docs/superpowers/specs/specification.md` (**BASE**). Read SPEC before starting; it carries the rationale this plan does not repeat.

## Global Constraints

- **Warnings are errors.** `Directory.Build.props` imports `Workflow/.roslyn`. `dotnet build` must produce zero warnings.
- **Never add to the repository-wide `NoWarn`** in `Directory.Build.props`. If a suppression is unavoidable, use a local `#pragma warning disable <ID>` with a comment saying why. `CA1031` in particular must stay enabled globally (BASE §4.4).
- **The analyzer policy applies to `Workflow.Tests` too.** `Directory.Build.props` sits at the repository root, so `AnalysisMode=All` + `TreatWarningsAsErrors` cover both projects; the test project only adds `CA1707;CA1822;CA2007;CA1303;CA1861` to `NoWarn`. In particular **`CA1062`** is live in test code: any `public` method that dereferences a parameter needs `ArgumentNullException.ThrowIfNull(...)`. This applies to new fakes.
- **`IDE0040`**: every interface member must carry an explicit `public` modifier. Match the existing files in `Workflow/Services`.
- **`CA1002`**: no `public` member may expose `List<T>`. Use `Collection<T>` (as `AppSettings.RecentDirectories` does) or `IReadOnlyList<T>`.
- **`CA2227`**: a settable collection property needs `[SuppressMessage("Usage", "CA2227", Justification = "System.Text.Json requires a settable collection property to populate this list.")]` — copy the attribute from `Workflow/Models/AppSettings.cs`.
- **XML doc comments are required** on every public type and member in the `Workflow` project (`GenerateDocumentationFile` is on there; it is off in `Workflow.Tests`). Missing docs are build errors.
- **`UseWPF=true` strips `System.IO` from implicit usings.** Any file touching `Path`, `File` or `Directory` needs an explicit `using System.IO;`.
- **All user-facing strings are German**, without umlauts in new message text where the existing code avoids them (see `ArtifactWatcher`'s `"Die Ueberwachung …"`). UI labels that already exist in English (`"Start workflow"`) stay English.
- **Artefact paths may only come from `TaskPaths`.** Never compose `W\T\<something>.md` by hand anywhere else.
- **Durable writes use the `SettingsService.Save` pattern**: serialise to `<path>.tmp`, `File.Move(tmp, path, overwrite: true)`, swallow `IOException` and `UnauthorizedAccessException`, best-effort delete the `.tmp` in `finally`.
- **Test naming:** `Method_Condition_Expectation`. Fixtures create a temp directory under `Path.GetTempPath()` named `wf-<area>-<guid:N>` and delete it in `Dispose()`.
- **Commit after every task.** End each commit message with:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Full gate:** `pwsh -File Workflow\verify.ps1` must exit 0 at the end of Task 11.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `Workflow/Models/TaskState.cs` | `TaskState` + `TaskPhaseState` — the journal's shape and its schema version. |
| `Workflow/Models/RecoverableTask.cs` | `RecoverableTask` — one scan hit: paths, state, resume phase. |
| `Workflow/Services/ITaskStateStore.cs` | Journal read/write contract. |
| `Workflow/Services/TaskStateStore.cs` | Journal implementation: atomic write, never throws, normalises `Phases`. |
| `Workflow/Services/ITaskRecoveryScanner.cs` | Startup scan contract. |
| `Workflow/Services/TaskRecoveryScanner.cs` | Scan, filter, reconcile, cap. |
| `Workflow.Tests/TaskStateStoreTests.cs` | Journal round-trip, corruption, normalisation, durability. |
| `Workflow.Tests/TaskRecoveryScannerTests.cs` | Filtering, ordering, capping, demotion. |
| `Workflow.Tests/Fakes/FakeTaskStateStore.cs` | Records `RecordPhase` / `SaveDescription` / `SetDismissed` calls. |

**Modified**

`Workflow/Models/TaskPaths.cs`, `Workflow/Models/WorkflowPhase.cs`, `Workflow/Models/PhaseCatalog.cs`, `Workflow/Models/AutoAnswerRule.cs`, `Workflow/Services/ArtifactWatcher.cs`, `Workflow/Services/AutoAnswerService.cs`, `Workflow/Services/PromptTemplateService.cs`, `Workflow/Services/ITerminalController.cs`, `Workflow/Services/IWorkflowOrchestrator.cs`, `Workflow/Services/WorkflowOrchestrator.cs`, `Workflow/Services/TaskTabViewModelFactory.cs`, `Workflow/ViewModels/TerminalViewModel.cs`, `Workflow/ViewModels/TaskTabViewModel.cs`, `Workflow/ViewModels/MainWindowViewModel.cs`, `Workflow/Views/TaskTabView.xaml`, `Workflow/App.xaml.cs`, `Workflow/Prompt/implementation_prompt.md`, `Workflow/Assets/autoanswer.rules.json`, `Workflow/verify.ps1`, and the test files named per task.

---

## Task 1: `TaskPaths` learns about the journal and the done marker

**Files:**
- Modify: `Workflow/Models/TaskPaths.cs`
- Test: `Workflow.Tests/TaskPathsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `TaskPaths.StateAbsolute` (`string`), `TaskPaths.DoneAbsolute` (`string`), `TaskPaths.DoneRelative` (`string`). Every later task uses these; nothing composes those paths by hand.

- [ ] **Step 1: Write the failing test**

Append to `Workflow.Tests/TaskPathsTests.cs`:

```csharp
    [Fact]
    public void Constructor_DerivesTheJournalAndDoneMarkerPaths()
    {
        var paths = new TaskPaths(@"C:\work", "Feature X");

        Assert.Equal(@"C:\work\Feature X\.workflow-state.json", paths.StateAbsolute);
        Assert.Equal(@"C:\work\Feature X\Feature X-done.md", paths.DoneAbsolute);
        Assert.Equal("./Feature X/Feature X-done.md", paths.DoneRelative);
    }
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskPathsTests"`
Expected: FAIL — `'TaskPaths' does not contain a definition for 'StateAbsolute'`.

- [ ] **Step 3: Implement**

In `Workflow/Models/TaskPaths.cs`, inside the constructor after the `ReviewAbsolute` assignment:

```csharp
        DoneAbsolute = Path.Combine(TaskDirectory, $"{TaskName}-done.md");
        StateAbsolute = Path.Combine(TaskDirectory, ".workflow-state.json");
```

and after `ReviewRelative`:

```csharp
        DoneRelative = $"./{TaskName}/{TaskName}-done.md";
```

Then add the three properties next to the existing ones, each with an XML doc comment:

```csharp
    /// <summary>Absolute path of the phase-4 completion marker written by the CLI.</summary>
    /// <remarks>
    /// Hyphen, not underscore - it matches <see cref="ReviewAbsolute"/>. The spec and plan
    /// artefacts use underscores; that inconsistency is pre-existing and the token values are
    /// already baked into specs on disk, so it is preserved rather than "fixed".
    /// </remarks>
    public string DoneAbsolute { get; }

    /// <summary>Value substituted for the {done_path} token.</summary>
    public string DoneRelative { get; }

    /// <summary>
    /// Absolute path of the workflow journal. This is the app's own bookkeeping file, not an
    /// artefact any CLI writes, and it is deliberately NOT one of the watched paths.
    /// </summary>
    public string StateAbsolute { get; }
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskPathsTests"`
Expected: PASS, all tests in the class.

- [ ] **Step 5: Commit**

```bash
git add Workflow/Models/TaskPaths.cs Workflow.Tests/TaskPathsTests.cs
git commit -m "feat(paths): derive journal and done-marker paths from TaskPaths

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: The `{done_path}` prompt token

Phase 4 still ends manually after this task. This task only makes the token legal and puts it in the prompt, so that `PromptTemplateService.ValidateAll()` keeps passing when Task 4 starts watching for the file.

**Files:**
- Modify: `Workflow/Services/PromptTemplateService.cs` (`PromptVariables`, lines 8–46)
- Modify: `Workflow/Prompt/implementation_prompt.md` (append a section)
- Modify: `Workflow/verify.ps1` (`$known`, ~line 62)
- Test: `Workflow.Tests/PromptTemplateServiceTests.cs`

**Interfaces:**
- Consumes: `TaskPaths.DoneRelative` (Task 1).
- Produces: the token name `"done_path"` in `PromptVariables.KnownNames`, and `PromptVariables.For(...)["done_path"] == paths.DoneRelative`.

- [ ] **Step 1: Write the failing test**

Append to `Workflow.Tests/PromptTemplateServiceTests.cs`:

```csharp
    [Fact]
    public void For_MapsDonePathToTheRelativeMarkerPath()
    {
        var paths = new TaskPaths(@"C:\work", "demo");

        var variables = PromptVariables.For(paths, "beschreibung");

        Assert.Equal("./demo/demo-done.md", variables["done_path"]);
        Assert.Contains("done_path", PromptVariables.KnownNames);
    }
```

(The file already has `using Workflow.Models;` and `using Workflow.Services;` — check and add them if not.)

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~PromptTemplateServiceTests"`
Expected: FAIL — `KeyNotFoundException: The given key 'done_path' was not present`.

- [ ] **Step 3: Implement the token**

In `Workflow/Services/PromptTemplateService.cs`, add `"done_path",` to the `KnownNames` initialiser after `"review_path",`, and add to the dictionary returned by `For`:

```csharp
            ["done_path"] = paths.DoneRelative,
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~PromptTemplateServiceTests"`
Expected: PASS.

- [ ] **Step 5: Add the completion-signal section to the prompt**

Append verbatim to the end of `Workflow/Prompt/implementation_prompt.md`:

```markdown

## Signalling completion

When, and only when, every condition under "Completion" above is satisfied, write a short
completion report to {done_path} as the very last action of this session. The file must not be
empty: one or two sentences naming what was implemented and the result of `Workflow\verify.ps1`
is enough.

The application watches for this file. Until it exists, the workflow is considered unfinished
and will offer to resume this task the next time it starts.
```

- [ ] **Step 6: Teach `verify.ps1` the new token**

In `Workflow/verify.ps1`, change the `$known` line to:

```powershell
$known = @('taskbezeichnung', 'taskbeschreibung', 'AppDirectory', 'spec_path', 'plan_path', 'review_path', 'done_path')
```

and add this assertion immediately after the existing `review_prompt.md uses {review_path}` one:

```powershell
Assert-True (Select-String -Path (Join-Path $promptDir 'implementation_prompt.md') -Pattern '\{done_path\}' -Quiet) `
    'implementation_prompt.md uses {done_path}'
```

- [ ] **Step 7: Run the whole suite and confirm nothing regressed**

Run: `dotnet test Workflow.sln`
Expected: PASS. `PlaceholderTests` and `PromptTemplateServiceTests` both validate every template's tokens against `KnownNames`, so a typo in the prompt file shows up here.

- [ ] **Step 8: Commit**

```bash
git add Workflow/Services/PromptTemplateService.cs Workflow/Prompt/implementation_prompt.md Workflow/verify.ps1 Workflow.Tests/PromptTemplateServiceTests.cs
git commit -m "feat(prompts): add the {done_path} token and the completion-signal section

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Phase 3 requires **both** spec and plan to change

**Files:**
- Modify: `Workflow/Models/WorkflowPhase.cs` (the `CompletionRule` enum at the bottom)
- Modify: `Workflow/Services/ArtifactWatcher.cs` (`CheckAndSignal`, ~line 158)
- Modify: `Workflow/Models/PhaseCatalog.cs:15-16`
- Test: `Workflow.Tests/ArtifactWatcherTests.cs`, `Workflow.Tests/PhaseCatalogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CompletionRule.AllContentChanged`.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/ArtifactWatcherTests.cs`:

```csharp
    [Fact]
    public async Task AllContentChanged_OneOfTwoFilesChanged_DoesNotComplete()
    {
        await File.WriteAllTextAsync(P("spec.md"), "one");
        await File.WriteAllTextAsync(P("plan.md"), "two");

        using var watcher = Create(CompletionRule.AllContentChanged, "spec.md", "plan.md");
        await File.WriteAllTextAsync(P("spec.md"), "one changed");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task AllContentChanged_BothFilesChanged_Completes()
    {
        await File.WriteAllTextAsync(P("spec.md"), "one");
        await File.WriteAllTextAsync(P("plan.md"), "two");

        using var watcher = Create(CompletionRule.AllContentChanged, "spec.md", "plan.md");
        await File.WriteAllTextAsync(P("spec.md"), "one changed");
        await File.WriteAllTextAsync(P("plan.md"), "two changed");

        Assert.True(await CompletesAsync(watcher));
    }
```

And in `Workflow.Tests/PhaseCatalogTests.cs`, change the `ResolveReview` row of the `[Theory]` to:

```csharp
    [InlineData(WorkflowPhase.ResolveReview, "Review umsetzen", "yo", "resolve_review_prompt.md", CompletionRule.AllContentChanged)]
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ArtifactWatcherTests|FullyQualifiedName~PhaseCatalogTests"`
Expected: FAIL — `'CompletionRule' does not contain a definition for 'AllContentChanged'`.

- [ ] **Step 3: Implement**

In `Workflow/Models/WorkflowPhase.cs`, add to `CompletionRule` after `AnyContentChanged`:

```csharp
    /// <summary>Every watched path differs from its baseline hash.</summary>
    AllContentChanged,
```

In `Workflow/Services/ArtifactWatcher.cs`, add an arm to the `satisfied` switch in `CheckAndSignal`:

```csharp
            CompletionRule.AllContentChanged => _paths.All(HasChangedSinceBaseline),
```

In `Workflow/Models/PhaseCatalog.cs`, change the `ResolveReview` entry's last argument from `CompletionRule.AnyContentChanged` to `CompletionRule.AllContentChanged`.

Do **not** touch `HasChangedSinceBaseline` or `ComputeHash`. The tri-state hashing of BASE §7.5 is already correct for this rule and matters more here: an `Unreadable` file defers the signal instead of mis-firing it.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ArtifactWatcherTests|FullyQualifiedName~PhaseCatalogTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Workflow/Models/WorkflowPhase.cs Workflow/Models/PhaseCatalog.cs Workflow/Services/ArtifactWatcher.cs Workflow.Tests/ArtifactWatcherTests.cs Workflow.Tests/PhaseCatalogTests.cs
git commit -m "feat(phases): phase 3 completes only when BOTH spec and plan changed

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Phase 4 completes on the done marker; `CompletionRule.Manual` is removed

**Files:**
- Modify: `Workflow/Models/PhaseCatalog.cs:17-18`
- Modify: `Workflow/Models/WorkflowPhase.cs` (delete `CompletionRule.Manual`)
- Modify: `Workflow/Services/ArtifactWatcher.cs:48-52` and the `satisfied` switch
- Modify: `Workflow/Services/WorkflowOrchestrator.cs` (`WatchedPaths`, `RunPhaseAsync`)
- Test: `Workflow.Tests/PhaseCatalogTests.cs`, `Workflow.Tests/ArtifactWatcherTests.cs`, `Workflow.Tests/WorkflowOrchestratorTests.cs`

**Interfaces:**
- Consumes: `TaskPaths.DoneAbsolute` (Task 1).
- Produces: phase 4 watched path = `[paths.DoneAbsolute]`; the guarantee that a stale `T-done.md` is deleted before phase 4 arms its watcher.

- [ ] **Step 1: Write the failing tests**

In `Workflow.Tests/PhaseCatalogTests.cs`, change the `Implementation` row to:

```csharp
    [InlineData(WorkflowPhase.Implementation, "Implementierung", "yo", "implementation_prompt.md", CompletionRule.FilesExist)]
```

In `Workflow.Tests/ArtifactWatcherTests.cs`, **delete** the whole `Manual_NeverCompletesOnItsOwn` test (lines ~205–213) — the rule it covers no longer exists.

In `Workflow.Tests/WorkflowOrchestratorTests.cs`, add:

```csharp
    [Fact]
    public async Task RunAsync_DeletesAStaleDoneMarkerBeforePhaseFourWaits()
    {
        await File.WriteAllTextAsync(_paths.DoneAbsolute, "left over from a previous run");

        using var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        using var cts = new CancellationTokenSource();
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Satisfy phases 1-3 so the run reaches phase 4.
        await File.WriteAllTextAsync(_paths.SpecAbsolute, "spec");
        await File.WriteAllTextAsync(_paths.PlanAbsolute, "plan");
        await WaitForPhaseAsync(WorkflowPhase.Review);
        await File.WriteAllTextAsync(_paths.ReviewAbsolute, "review");
        await WaitForPhaseAsync(WorkflowPhase.ResolveReview);
        await File.WriteAllTextAsync(_paths.SpecAbsolute, "spec v2");
        await File.WriteAllTextAsync(_paths.PlanAbsolute, "plan v2");
        await WaitForPhaseAsync(WorkflowPhase.Implementation);

        // The stale marker must be gone, so phase 4 is still waiting.
        Assert.False(File.Exists(_paths.DoneAbsolute));
        Assert.DoesNotContain(
            _progress, p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private async Task WaitForPhaseAsync(WorkflowPhase phase)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_progress.Any(p => p.Phase == phase && p.Status == PhaseStatus.Active))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Phase {phase} never became active. Seen: {string.Join(", ", _progress)}");
    }
```

If `WorkflowOrchestratorTests` already has an equivalent wait helper, reuse it instead of adding a second one.

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~PhaseCatalogTests|FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: FAIL — the catalogue still says `Manual`, and the stale marker is still on disk.

- [ ] **Step 3: Flip phase 4 to `FilesExist` and watch the marker**

`Workflow/Models/PhaseCatalog.cs` — change the `Implementation` entry's last argument to `CompletionRule.FilesExist`.

`Workflow/Services/WorkflowOrchestrator.cs` — in `WatchedPaths`, replace the `_ => []` arm:

```csharp
            WorkflowPhase.Implementation => [paths.DoneAbsolute],
            _ => [],
```

Keep the trailing `_ => []` so the switch stays exhaustive for the compiler.

- [ ] **Step 4: Delete the stale marker before arming the watcher**

Still in `WorkflowOrchestrator.cs`, in `RunPhaseAsync`, **before** the `using var watcher = _watchers.Create(...)` line:

```csharp
        // A marker left by a previous run of this task would satisfy the phase-4 watcher in
        // milliseconds. Deleting it before the baseline is taken removes the failure mode
        // instead of handling it (SPEC section 8.3).
        if (definition.Phase == WorkflowPhase.Implementation)
        {
            DeleteStaleDoneMarker(request.Paths.DoneAbsolute);
        }
```

and add the helper to the class:

```csharp
    private static void DeleteStaleDoneMarker(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held open by another process. The phase then completes immediately; the user can
            // still drive the session by hand and 'Task abschliessen' remains available.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only or no permission. Same fallback as above.
        }
    }
```

`File.Delete` does not throw when the file is absent, so no `File.Exists` check is needed. Add `using System.IO;` to the file (`UseWPF` strips it from implicit usings).

- [ ] **Step 5: Remove `CompletionRule.Manual`**

`Workflow/Models/WorkflowPhase.cs` — delete the `Manual` member and its doc comment.

`Workflow/Services/ArtifactWatcher.cs` — delete the early return in the constructor:

```csharp
        if (_rule == CompletionRule.Manual)
        {
            return;
        }
```

and delete the `_ => false,` arm from the `satisfied` switch, leaving:

```csharp
        var satisfied = _rule switch
        {
            CompletionRule.FilesExist => _paths.All(IsPresentAndNonEmpty),
            CompletionRule.AnyContentChanged => _paths.Any(HasChangedSinceBaseline),
            CompletionRule.AllContentChanged => _paths.All(HasChangedSinceBaseline),
            _ => false,
        };
```

Keep the final `_ => false` — without it the switch is not exhaustive over an `enum` and the compiler warns, which is an error here.

`Workflow/Services/WorkflowOrchestrator.cs` — replace the conditional completion block:

```csharp
        var completion = definition.Completion == CompletionRule.Manual
            ? request.ManualSignal.WaitAsync(cancellationToken)
            : await Task.WhenAny(
                watcher.WaitAsync(cancellationToken),
                request.ManualSignal.WaitAsync(cancellationToken));

        await completion;
```

with:

```csharp
        // Every phase now has an artefact condition; the manual signal is the escape hatch for
        // all four, not a completion rule of its own.
        await await Task.WhenAny(
            watcher.WaitAsync(cancellationToken),
            request.ManualSignal.WaitAsync(cancellationToken));
```

The doubled `await` is intentional: `Task.WhenAny` returns `Task<Task>`, and the inner task must be awaited so a watcher failure surfaces as `ArtifactWatchException` rather than being silently dropped. Confirm this matches the existing behaviour — the old code awaited the result of `Task.WhenAny` and then `await completion`, which is the same thing.

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln`
Expected: PASS. If `ArtifactWatcherTests` still references `CompletionRule.Manual` anywhere, the build fails — remove those references.

- [ ] **Step 7: Commit**

```bash
git add Workflow/Models/PhaseCatalog.cs Workflow/Models/WorkflowPhase.cs Workflow/Services/ArtifactWatcher.cs Workflow/Services/WorkflowOrchestrator.cs Workflow.Tests/PhaseCatalogTests.cs Workflow.Tests/ArtifactWatcherTests.cs Workflow.Tests/WorkflowOrchestratorTests.cs
git commit -m "feat(phases): detect phase 4 via the done marker; drop CompletionRule.Manual

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: The journal — `TaskState` and `TaskStateStore`

**Files:**
- Create: `Workflow/Models/TaskState.cs`
- Create: `Workflow/Services/ITaskStateStore.cs`
- Create: `Workflow/Services/TaskStateStore.cs`
- Create: `Workflow.Tests/TaskStateStoreTests.cs`

**Interfaces:**
- Consumes: `TaskPaths.StateAbsolute`, `TaskPaths.TaskDirectory` (Task 1).
- Produces:
  - `public sealed record TaskPhaseState(WorkflowPhase Phase, PhaseStatus Status, DateTimeOffset? CompletedUtc);`
  - `public sealed class TaskState` with `int Version`, `string TaskDescription`, `DateTimeOffset CreatedUtc`, `DateTimeOffset UpdatedUtc`, `bool Dismissed`, `Collection<TaskPhaseState> Phases`, and `public const int CurrentVersion = 1;`
  - `public interface ITaskStateStore` with `TaskState? TryLoad(TaskPaths paths)`, `void SaveDescription(TaskPaths paths, string taskDescription)`, `void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status)`, `void SetDismissed(TaskPaths paths, bool dismissed)`.

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/TaskStateStoreTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskStateStoreTests : IDisposable
{
    private readonly string _root;
    private readonly TaskPaths _paths;
    private readonly TaskStateStore _store = new();

    public TaskStateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TaskPaths(_root, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NoJournal_ReturnsNull() => Assert.Null(_store.TryLoad(_paths));

    [Fact]
    public void SaveDescription_CreatesAJournalWithFourPendingPhases()
    {
        _store.SaveDescription(_paths, "eine Beschreibung");

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal("eine Beschreibung", state.TaskDescription);
        Assert.Equal(4, state.Phases.Count);
        Assert.All(state.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
        Assert.Equal(
            new[]
            {
                WorkflowPhase.Specification, WorkflowPhase.Review,
                WorkflowPhase.ResolveReview, WorkflowPhase.Implementation,
            },
            state.Phases.Select(p => p.Phase));
    }

    [Fact]
    public void RecordPhase_Completed_StampsCompletedUtc()
    {
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);

        var review = _store.TryLoad(_paths)!.Phases.Single(p => p.Phase == WorkflowPhase.Review);

        Assert.Equal(PhaseStatus.Completed, review.Status);
        Assert.NotNull(review.CompletedUtc);
    }

    [Fact]
    public void RecordPhase_Active_ClearsCompletedUtc()
    {
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Active);

        var review = _store.TryLoad(_paths)!.Phases.Single(p => p.Phase == WorkflowPhase.Review);

        Assert.Equal(PhaseStatus.Active, review.Status);
        Assert.Null(review.CompletedUtc);
    }

    [Fact]
    public void RecordPhase_NoJournalYet_CreatesOne()
    {
        _store.RecordPhase(_paths, WorkflowPhase.Specification, PhaseStatus.Active);

        Assert.NotNull(_store.TryLoad(_paths));
    }

    [Fact]
    public void SetDismissed_ThenSaveDescription_ClearsTheFlag()
    {
        _store.SaveDescription(_paths, "d");
        _store.SetDismissed(_paths, dismissed: true);
        Assert.True(_store.TryLoad(_paths)!.Dismissed);

        _store.SaveDescription(_paths, "d");
        Assert.False(_store.TryLoad(_paths)!.Dismissed);
    }

    [Fact]
    public void SetDismissed_NoJournal_IsANoOp()
    {
        _store.SetDismissed(_paths, dismissed: true);

        Assert.False(File.Exists(_paths.StateAbsolute));
    }

    [Fact]
    public void TryLoad_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(_paths.StateAbsolute, "{ this is not json");

        Assert.Null(_store.TryLoad(_paths));
    }

    [Fact]
    public void TryLoad_NewerSchemaVersion_ReturnsNull()
    {
        File.WriteAllText(_paths.StateAbsolute, """{ "version": 99, "phases": [] }""");

        Assert.Null(_store.TryLoad(_paths));
    }

    [Fact]
    public void TryLoad_ShortOrReorderedPhaseArray_NormalisesToCatalogueOrder()
    {
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "d",
          "phases": [
            { "phase": "Review", "status": "Completed", "completedUtc": "2026-09-14T09:00:00Z" },
            { "phase": "Nonsense", "status": "Completed", "completedUtc": null }
          ]
        }
        """);

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal(4, state.Phases.Count);
        Assert.Equal(WorkflowPhase.Specification, state.Phases[0].Phase);
        Assert.Equal(PhaseStatus.Pending, state.Phases[0].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[1].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[3].Status);
    }

    [Fact]
    public void SaveDescription_LeavesNoTemporaryFileBehind()
    {
        _store.SaveDescription(_paths, "d");

        Assert.Empty(Directory.GetFiles(_paths.TaskDirectory, "*.tmp"));
    }

    [Fact]
    public void SaveDescription_TaskDirectoryMissing_DoesNotThrow()
    {
        Directory.Delete(_paths.TaskDirectory, recursive: true);

        _store.SaveDescription(_paths, "d");

        Assert.Null(_store.TryLoad(_paths));
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskStateStoreTests"`
Expected: FAIL to compile — `TaskStateStore` does not exist.

- [ ] **Step 3: Write the model**

Create `Workflow/Models/TaskState.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Workflow.Models;

/// <summary>One phase's recorded outcome inside the workflow journal.</summary>
/// <param name="Phase">The phase this entry describes.</param>
/// <param name="Status">The phase's last recorded status.</param>
/// <param name="CompletedUtc">When the phase finished, or null while it has not.</param>
public sealed record TaskPhaseState(WorkflowPhase Phase, PhaseStatus Status, DateTimeOffset? CompletedUtc);

/// <summary>
/// The workflow journal: what the application knows about one task's progress through the four
/// phases. Written to <see cref="TaskPaths.StateAbsolute"/>.
/// </summary>
/// <remarks>
/// It deliberately stores neither the task name nor the working directory. Both are derived from
/// the folder the journal sits in and that folder's parent, so renaming or moving a task folder -
/// including through <c>TaskFolderService.Rename</c>, which moves the whole directory - stays
/// correct without any migration.
/// </remarks>
[SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "System.Text.Json requires a settable collection property to populate this list.")]
public sealed class TaskState
{
    /// <summary>The schema version this build writes and is able to read.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version of the file on disk.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The Taskbeschreibung, verbatim. The only field that cannot be derived.</summary>
    public string TaskDescription { get; set; } = string.Empty;

    /// <summary>When the journal was first written.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the journal was last written. Drives the recovery age window.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>True when the user closed the recovered tab instead of continuing it.</summary>
    public bool Dismissed { get; set; }

    /// <summary>The four phases, always in <see cref="PhaseCatalog"/> order.</summary>
    public Collection<TaskPhaseState> Phases { get; set; } = [];
}
```

- [ ] **Step 4: Write the interface**

Create `Workflow/Services/ITaskStateStore.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>Reads and writes one task's workflow journal. No method ever throws.</summary>
public interface ITaskStateStore
{
    /// <summary>Reads the journal.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>
    /// The journal, normalised to four phases in catalogue order; null when the file is absent,
    /// unreadable, not valid JSON, or written by a newer schema version.
    /// </returns>
    public TaskState? TryLoad(TaskPaths paths);

    /// <summary>
    /// Records the Taskbeschreibung, creating the journal if needed, and clears the dismissed
    /// flag - the user has explicitly started or continued this task.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="taskDescription">The description to persist.</param>
    public void SaveDescription(TaskPaths paths, string taskDescription);

    /// <summary>Records one phase's status, creating the journal if needed.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="phase">The phase whose status changed.</param>
    /// <param name="status">The new status.</param>
    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status);

    /// <summary>Sets or clears the dismissed flag. A no-op when there is no journal.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="dismissed">The new value.</param>
    public void SetDismissed(TaskPaths paths, bool dismissed);
}
```

- [ ] **Step 5: Write the implementation**

Create `Workflow/Services/TaskStateStore.cs`:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskStateStore" />
public sealed class TaskStateStore : ITaskStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Strings, not ordinals: reordering WorkflowPhase must never silently reinterpret an
        // existing journal.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public TaskState? TryLoad(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            if (!File.Exists(paths.StateAbsolute))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<TaskState>(
                File.ReadAllText(paths.StateAbsolute), JsonOptions);

            if (state is null || state.Version > TaskState.CurrentVersion)
            {
                return null;
            }

            state.Phases = Normalise(state.Phases);
            return state;
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

    /// <inheritdoc />
    public void SaveDescription(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths) ?? CreateEmpty();
        state.TaskDescription = taskDescription ?? string.Empty;
        state.Dismissed = false;
        Save(paths, state);
    }

    /// <inheritdoc />
    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths) ?? CreateEmpty();
        var index = (int)phase;

        if (index < 0 || index >= state.Phases.Count)
        {
            return;
        }

        state.Phases[index] = new TaskPhaseState(
            phase,
            status,
            status == PhaseStatus.Completed ? DateTimeOffset.UtcNow : null);

        Save(paths, state);
    }

    /// <inheritdoc />
    public void SetDismissed(TaskPaths paths, bool dismissed)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths);
        if (state is null)
        {
            return;
        }

        state.Dismissed = dismissed;
        Save(paths, state);
    }

    private static TaskState CreateEmpty() => new()
    {
        CreatedUtc = DateTimeOffset.UtcNow,
        Phases = Normalise([]),
    };

    // A journal may be hand-edited, truncated, or written by an older build. Everything
    // downstream indexes Phases by (int)WorkflowPhase, so the array is rebuilt to exactly the
    // four catalogue phases in order before anyone sees it.
    private static Collection<TaskPhaseState> Normalise(IEnumerable<TaskPhaseState>? existing)
    {
        var byPhase = new Dictionary<WorkflowPhase, TaskPhaseState>();

        foreach (var entry in existing ?? [])
        {
            if (entry is not null && Enum.IsDefined(entry.Phase))
            {
                byPhase[entry.Phase] = entry;
            }
        }

        var result = new Collection<TaskPhaseState>();

        foreach (var definition in PhaseCatalog.All)
        {
            result.Add(byPhase.TryGetValue(definition.Phase, out var found)
                ? found
                : new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        return result;
    }

    // Mirrors SettingsService.Save: a journal write that fails must never take down a live
    // workflow run. Only recovery is degraded.
    private static void Save(TaskPaths paths, TaskState state)
    {
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        if (state.CreatedUtc == default)
        {
            state.CreatedUtc = state.UpdatedUtc;
        }

        var temporary = paths.StateAbsolute + ".tmp";

        try
        {
            Directory.CreateDirectory(paths.TaskDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, paths.StateAbsolute, overwrite: true);
        }
        catch (IOException)
        {
            // See the remark above.
        }
        catch (UnauthorizedAccessException)
        {
            // See the remark above.
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing further can be done.
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing further can be done.
        }
    }
}
```

Note on `SaveDescription_TaskDirectoryMissing_DoesNotThrow`: `Save` calls `Directory.CreateDirectory`, so the directory is recreated and `TryLoad` will find a journal. If the test as written fails on that last assertion, change it to `Assert.NotNull(_store.TryLoad(_paths));` — the behaviour under test is "does not throw", and recreating the folder is the right outcome.

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskStateStoreTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Workflow/Models/TaskState.cs Workflow/Services/ITaskStateStore.cs Workflow/Services/TaskStateStore.cs Workflow.Tests/TaskStateStoreTests.cs
git commit -m "feat(state): add the per-task workflow journal and its store

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: The orchestrator resumes and journals

**Files:**
- Modify: `Workflow/Services/IWorkflowOrchestrator.cs` (the `WorkflowRunRequest` record)
- Modify: `Workflow/Services/WorkflowOrchestrator.cs` (constructor, `RunAsync`, `RunPhaseAsync`)
- Modify: `Workflow/App.xaml.cs:32-37`
- Create: `Workflow.Tests/Fakes/FakeTaskStateStore.cs`
- Test: `Workflow.Tests/WorkflowOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ITaskStateStore` (Task 5).
- Produces:
  - `WorkflowRunRequest(..., WorkflowPhase StartPhase = WorkflowPhase.Specification)`
  - `WorkflowOrchestrator(IPromptTemplateService prompts, IAutoAnswerService autoAnswer, IArtifactWatcherFactory watchers, ITaskStateStore state, TimeSpan debounce, TimeSpan pollInterval)` — `state` inserted **before** the two `TimeSpan`s.

- [ ] **Step 1: Write the fake**

Create `Workflow.Tests/Fakes/FakeTaskStateStore.cs`:

```csharp
using System.Collections.ObjectModel;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests.Fakes;

/// <summary>An in-memory journal that records every call the orchestrator makes.</summary>
public sealed class FakeTaskStateStore : ITaskStateStore
{
    private readonly Dictionary<string, TaskState> _states = new(StringComparer.OrdinalIgnoreCase);

    public Collection<(WorkflowPhase Phase, PhaseStatus Status)> Recorded { get; } = [];

    public Collection<string> Descriptions { get; } = [];

    public Collection<(string Directory, bool Dismissed)> Dismissals { get; } = [];

    // Every method guards its argument. AnalysisMode=All applies to this project too, so CA1062
    // is an error wherever a public method dereferences a parameter it did not null-check.
    public TaskState? TryLoad(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return _states.TryGetValue(paths.TaskDirectory, out var state) ? state : null;
    }

    public void SaveDescription(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Descriptions.Add(taskDescription);
        var state = TryLoad(paths) ?? NewState();
        state.TaskDescription = taskDescription;
        state.Dismissed = false;
        _states[paths.TaskDirectory] = state;
    }

    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Recorded.Add((phase, status));
        var state = TryLoad(paths) ?? NewState();
        state.Phases[(int)phase] = new TaskPhaseState(
            phase, status, status == PhaseStatus.Completed ? DateTimeOffset.UtcNow : null);
        _states[paths.TaskDirectory] = state;
    }

    public void SetDismissed(TaskPaths paths, bool dismissed)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Dismissals.Add((paths.TaskDirectory, dismissed));
        var state = TryLoad(paths);
        if (state is not null)
        {
            state.Dismissed = dismissed;
        }
    }

    /// <summary>Seeds a journal so a test can exercise the recovery paths.</summary>
    public void Seed(TaskPaths paths, TaskState state)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _states[paths.TaskDirectory] = state;
    }

    private static TaskState NewState()
    {
        var state = new TaskState { CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };

        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        return state;
    }
}
```

- [ ] **Step 2: Write the failing tests**

In `Workflow.Tests/WorkflowOrchestratorTests.cs`, add a field `private readonly FakeTaskStateStore _state = new();`, pass it into `CreateOrchestrator()` (Step 4 changes that helper), then add:

```csharp
    [Fact]
    public async Task RunAsync_StartPhaseResolveReview_SkipsPhasesOneAndTwo()
    {
        using var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        var request = CreateRequest(terminal, signal) with { StartPhase = WorkflowPhase.ResolveReview };

        using var cts = new CancellationTokenSource();
        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.ResolveReview);

        Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Specification);
        Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Review);
        Assert.Single(terminal.StartedSessions);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_RecordsEveryExecutedPhaseInTheJournal()
    {
        using var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        using var cts = new CancellationTokenSource();
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await File.WriteAllTextAsync(_paths.SpecAbsolute, "spec");
        await File.WriteAllTextAsync(_paths.PlanAbsolute, "plan");
        await WaitForPhaseAsync(WorkflowPhase.Review);

        Assert.Equal(
            new[]
            {
                (WorkflowPhase.Specification, PhaseStatus.Active),
                (WorkflowPhase.Specification, PhaseStatus.Completed),
                (WorkflowPhase.Review, PhaseStatus.Active),
            },
            _state.Recorded.Take(3));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: FAIL to compile — `WorkflowRunRequest` has no `StartPhase`, and the orchestrator constructor takes no store.

- [ ] **Step 4: Implement**

In `Workflow/Services/IWorkflowOrchestrator.cs`, add the parameter and its doc line:

```csharp
/// <param name="StartPhase">
/// The phase the run begins at. Everything before it is skipped and never reported - a resumed
/// run's earlier indicators are painted by the tab from the journal, not by the orchestrator.
/// A defaulted positional parameter, so existing construction sites are unaffected.
/// </param>
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress,
    WorkflowPhase StartPhase = WorkflowPhase.Specification);
```

In `Workflow/Services/WorkflowOrchestrator.cs`:

- add `private readonly ITaskStateStore _state;`, the constructor parameter `ITaskStateStore state` placed immediately after `IArtifactWatcherFactory watchers`, its `<param>` doc line, and `_state = state;`
- in `RunAsync`, after `ArgumentNullException.ThrowIfNull(request);`:

```csharp
        if (!Enum.IsDefined(request.StartPhase))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
```

- change the loop to `foreach (var definition in PhaseCatalog.All.Skip((int)request.StartPhase))`
- in `RunPhaseAsync`, pair each progress report with a journal write, **store first** (`Progress.Report` marshals to the UI thread; a busy dispatcher must not delay the durable write):

```csharp
        _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Active);
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Active));
```

and at the end of the method:

```csharp
        _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Completed);
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
```

In `Workflow/App.xaml.cs`, hoist a shared store above the orchestrator and pass it in — Task 9 needs the same instance:

```csharp
        var stateStore = new TaskStateStore();

        var orchestrator = new WorkflowOrchestrator(
            prompts,
            autoAnswer,
            new ArtifactWatcherFactory(),
            stateStore,
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromSeconds(1));
```

Update `CreateOrchestrator()` in `WorkflowOrchestratorTests` to pass `_state` in the same position.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Workflow/Services/IWorkflowOrchestrator.cs Workflow/Services/WorkflowOrchestrator.cs Workflow/App.xaml.cs Workflow.Tests/Fakes/FakeTaskStateStore.cs Workflow.Tests/WorkflowOrchestratorTests.cs
git commit -m "feat(orchestrator): add StartPhase and journal every executed phase

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Make the prompt submit itself

**Files:**
- Modify: `Workflow/Models/AutoAnswerRule.cs` (`AutoAnswerRuleSet`)
- Modify: `Workflow/Services/AutoAnswerService.cs` (`Load` — normalise the new fields)
- Modify: `Workflow/Assets/autoanswer.rules.json`
- Modify: `Workflow/Services/ITerminalController.cs` (replace `SendPasteAsync` with `SendPaste`)
- Modify: `Workflow/ViewModels/TerminalViewModel.cs:26, 218-258`
- Modify: `Workflow/Services/WorkflowOrchestrator.cs` (`RunPhaseAsync`, new `SendPromptAsync`)
- Modify: `Workflow.Tests/Fakes/FakeTerminalController.cs:60-68`
- Test: `Workflow.Tests/AutoAnswerServiceTests.cs`, `Workflow.Tests/WorkflowOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ITerminalController.OutputCount`, `ITerminalController.LastOutputUtc` (existing).
- Produces:
  - `ITerminalController.SendPaste(string body)` — writes `BracketedPaste.Wrap(body)` and nothing else.
  - `AutoAnswerRuleSet` gains `PasteQuietPeriodMs`, `PasteSettleTimeoutMs`, `SubmitVerifyMs`, `MaxSubmitAttempts`, all defaulted.

- [ ] **Step 1: Write the failing config test**

Append to `Workflow.Tests/AutoAnswerServiceTests.cs`. The fixture already has a `private string WriteRules(string json)` helper that writes into its own temp directory and returns the path — use it:

```csharp
    [Fact]
    public void RuleSet_FileOmitsTheSubmitFields_UsesTheDefaults()
    {
        var path = WriteRules("""
        {
          "version": 1, "quietPeriodMs": 1500, "settleTimeoutMs": 60000,
          "maxAnswersPerPhase": 5, "rules": []
        }
        """);

        var set = new AutoAnswerService(path, overridePath: null).RuleSet;

        Assert.Equal(800, set.PasteQuietPeriodMs);
        Assert.Equal(15000, set.PasteSettleTimeoutMs);
        Assert.Equal(1500, set.SubmitVerifyMs);
        Assert.Equal(2, set.MaxSubmitAttempts);
    }

    [Fact]
    public void RuleSet_FileSetsTheSubmitFields_UsesThem()
    {
        var path = WriteRules("""
        {
          "version": 2, "quietPeriodMs": 1500, "settleTimeoutMs": 60000, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 10, "pasteSettleTimeoutMs": 200,
          "submitVerifyMs": 20, "maxSubmitAttempts": 3,
          "rules": []
        }
        """);

        var set = new AutoAnswerService(path, overridePath: null).RuleSet;

        Assert.Equal(10, set.PasteQuietPeriodMs);
        Assert.Equal(3, set.MaxSubmitAttempts);
    }
```

Note that `WriteRules` always writes the same file name inside the fixture's own temp directory. xUnit constructs a fresh fixture per `[Fact]`, so the two tests cannot collide.

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~AutoAnswerServiceTests"`
Expected: FAIL to compile — `AutoAnswerRuleSet` has no `PasteQuietPeriodMs`.

- [ ] **Step 3: Extend the rule set and normalise it on load**

In `Workflow/Models/AutoAnswerRule.cs`, extend the record, appending the four parameters **after** `Rules` with defaults, and add their `<param>` doc lines:

```csharp
public sealed record AutoAnswerRuleSet(
    int Version,
    int QuietPeriodMs,
    int SettleTimeoutMs,
    int MaxAnswersPerPhase,
    IReadOnlyList<AutoAnswerRule> Rules,
    int PasteQuietPeriodMs = 800,
    int PasteSettleTimeoutMs = 15000,
    int SubmitVerifyMs = 1500,
    int MaxSubmitAttempts = 2);
```

In `Workflow/Services/AutoAnswerService.cs`, at the end of `Load` (just before it returns the deserialised set), normalise. Do **not** rely on `System.Text.Json` honouring positional-parameter defaults for absent properties — normalising also protects against a user typing `0`:

```csharp
        // A value of zero or less is either an absent property or a user typo; either way the
        // shipped default is what the orchestrator needs. This makes the version-1 override files
        // already in %APPDATA% work with no migration.
        return set with
        {
            PasteQuietPeriodMs = set.PasteQuietPeriodMs > 0 ? set.PasteQuietPeriodMs : 800,
            PasteSettleTimeoutMs = set.PasteSettleTimeoutMs > 0 ? set.PasteSettleTimeoutMs : 15000,
            SubmitVerifyMs = set.SubmitVerifyMs > 0 ? set.SubmitVerifyMs : 1500,
            MaxSubmitAttempts = set.MaxSubmitAttempts > 0 ? set.MaxSubmitAttempts : 2,
        };
```

Apply the same normalisation to the fallback set the method returns when the file is missing or invalid, so every code path yields usable values.

In `Workflow/Assets/autoanswer.rules.json`, bump the version and add the fields above `"rules"`:

```json
  "version": 2,
  "quietPeriodMs": 1500,
  "settleTimeoutMs": 60000,
  "maxAnswersPerPhase": 5,
  "pasteQuietPeriodMs": 800,
  "pasteSettleTimeoutMs": 15000,
  "submitVerifyMs": 1500,
  "maxSubmitAttempts": 2,
```

- [ ] **Step 4: Run the config tests and confirm they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~AutoAnswerServiceTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing submit tests**

In `Workflow.Tests/WorkflowOrchestratorTests.cs`, point the fixture's rules file at short timings by adding the four fields to the JSON written in the constructor:

```json
"pasteQuietPeriodMs": 10, "pasteSettleTimeoutMs": 300, "submitVerifyMs": 30, "maxSubmitAttempts": 2,
```

Then add:

```csharp
    [Fact]
    public async Task RunAsync_LauncherProducesNoOutputAfterTheCr_SendsASecondCr()
    {
        using var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        using var cts = new CancellationTokenSource();
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.Specification);
        await WaitUntilAsync(() => terminal.Pasted.Count == 1 && terminal.Sent.Count(s => s == "\r") == 2);

        Assert.Equal(2, terminal.Sent.Count(s => s == "\r"));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_LauncherRespondsToTheCr_SendsOnlyOne()
    {
        using var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();
        terminal.EmitOutputOnNextCarriageReturn = true;

        using var cts = new CancellationTokenSource();
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.Specification);
        await WaitUntilAsync(() => terminal.Pasted.Count == 1);
        await Task.Delay(200);

        Assert.Equal(1, terminal.Sent.Count(s => s == "\r"));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was never met.");
    }
```

Note: the `cd "…"` and launcher writes also land in `terminal.Sent`, which is why these tests count `"\r"` entries specifically rather than total sends — the orchestrator sends the launcher line as one string ending in `\r`, not as a bare `"\r"`.

- [ ] **Step 6: Update the fake terminal**

In `Workflow.Tests/Fakes/FakeTerminalController.cs`, replace `SendPasteAsync` and its `SubmitsCompletedBeforeNextSession` counter with:

```csharp
    /// <summary>When true, a bare carriage return produces output, as a live launcher would.</summary>
    public bool EmitOutputOnNextCarriageReturn { get; set; }

    public void SendPaste(string body) => Pasted.Add(body);
```

and change `Send` to:

```csharp
    public void Send(string text)
    {
        Sent.Add(text);

        if (EmitOutputOnNextCarriageReturn && text == "\r")
        {
            EmitOutput();
        }
    }
```

Delete any assertion on `SubmitsCompletedBeforeNextSession` elsewhere in the test project; the hazard it pinned is removed structurally by this task (SPEC §9.3).

- [ ] **Step 7: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: FAIL to compile — `ITerminalController` still declares `SendPasteAsync`.

- [ ] **Step 8: Change the terminal contract**

In `Workflow/Services/ITerminalController.cs`, replace the whole `SendPasteAsync` member with:

```csharp
    /// <summary>Writes a bracketed-paste block. The submitting carriage return is NOT sent.</summary>
    /// <param name="body">The prompt text.</param>
    /// <remarks>
    /// Submitting is the orchestrator's job (SPEC section 9.3): it owns the quiet gate and the
    /// timing configuration, and awaiting the whole sequence inline is what makes a stray
    /// carriage return unable to reach the next phase's launcher.
    /// </remarks>
    public void SendPaste(string body);
```

In `Workflow/ViewModels/TerminalViewModel.cs`:

- delete the `SubmitDelay` field (line 26) and the whole `SendPasteAsync` method (lines ~218–258)
- delete the now-unused `CarriageReturn` constant if nothing else references it
- add:

```csharp
    /// <inheritdoc />
    public void SendPaste(string body)
    {
        var target = _session;
        if (target is null)
        {
            return;
        }

        Write(target, BracketedPaste.Wrap(body));
    }
```

Keep `_sessionLifetime` and everything in `DisposeSession` — that field is still used there.

- [ ] **Step 9: Implement the submit dance in the orchestrator**

In `Workflow/Services/WorkflowOrchestrator.cs`, replace the `await request.Terminal.SendPasteAsync(...)` call in `RunPhaseAsync` with:

```csharp
        await SendPromptAsync(
            request.Terminal,
            _prompts.Render(definition.PromptFile, variables),
            cancellationToken);
```

and add the method:

```csharp
    // The prompt used to be submitted by writing the paste block and then a carriage return after
    // a fixed 150 ms. Claude Code's TUI coalesces a paste, so a CR arriving while a multi-kilobyte
    // prompt is still draining is taken as a literal newline instead of as submit - which is why
    // the user had to press Enter by hand in every phase. Waiting for the screen to go quiet and
    // then verifying that the CR produced output removes the dependency on any fixed delay.
    private async Task SendPromptAsync(
        ITerminalController terminal,
        string prompt,
        CancellationToken cancellationToken)
    {
        var configuration = _autoAnswer.RuleSet;

        terminal.SendPaste(prompt);

        var quietPeriod = TimeSpan.FromMilliseconds(configuration.PasteQuietPeriodMs);
        var ceiling = Stopwatch.StartNew();

        while (ceiling.Elapsed < TimeSpan.FromMilliseconds(configuration.PasteSettleTimeoutMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow - terminal.LastOutputUtc >= quietPeriod)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        for (var attempt = 0; attempt < configuration.MaxSubmitAttempts; attempt++)
        {
            // The carriage return is only ever written once the screen is quiet, so by
            // construction nothing else is producing output: any new chunk means the launcher
            // acted on it. That makes OutputCount a cheaper and sharper signal than diffing two
            // snapshots, which would have to tell spinner frames from real progress.
            var before = terminal.OutputCount;

            terminal.Send("\r");

            await Task.Delay(TimeSpan.FromMilliseconds(configuration.SubmitVerifyMs), cancellationToken);

            if (terminal.OutputCount != before)
            {
                return;
            }
        }

        // Both attempts produced nothing. The prompt is sitting in the input box and the terminal
        // is live: the user can press Enter, exactly as before this fix. Better than blocking.
    }
```

`Stopwatch` is already imported at the top of the file.

- [ ] **Step 10: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add Workflow/Models/AutoAnswerRule.cs Workflow/Services/AutoAnswerService.cs Workflow/Assets/autoanswer.rules.json Workflow/Services/ITerminalController.cs Workflow/ViewModels/TerminalViewModel.cs Workflow/Services/WorkflowOrchestrator.cs Workflow.Tests/Fakes/FakeTerminalController.cs Workflow.Tests/AutoAnswerServiceTests.cs Workflow.Tests/WorkflowOrchestratorTests.cs
git commit -m "fix(terminal): submit the pasted prompt after a quiet gate, verified and retried

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: `TaskRecoveryScanner`

**Files:**
- Create: `Workflow/Models/RecoverableTask.cs`
- Create: `Workflow/Services/ITaskRecoveryScanner.cs`
- Create: `Workflow/Services/TaskRecoveryScanner.cs`
- Create: `Workflow.Tests/TaskRecoveryScannerTests.cs`

**Interfaces:**
- Consumes: `ITaskStateStore` (Task 5), `ISettingsService` (existing), `TaskPaths` (Task 1).
- Produces:
  - `public sealed record RecoverableTask(TaskPaths Paths, TaskState State, WorkflowPhase ResumePhase);`
  - `public interface ITaskRecoveryScanner { public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken); }`
  - `TaskRecoveryScanner(ISettingsService settings, ITaskStateStore store, TimeSpan maxAge, int maxTasks, int maxSubdirectoriesPerRoot, TimeSpan scanTimeout)`

- [ ] **Step 1: Write the failing tests**

Create `Workflow.Tests/TaskRecoveryScannerTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskRecoveryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;
    private readonly TaskStateStore _store = new();

    public TaskRecoveryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string NewWorkspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        _settings.AddRecentDirectory(path);
        return path;
    }

    private TaskPaths SeedTask(
        string workspace,
        string taskName,
        params (WorkflowPhase Phase, PhaseStatus Status)[] statuses)
    {
        var paths = new TaskPaths(workspace, taskName);
        Directory.CreateDirectory(paths.TaskDirectory);
        _store.SaveDescription(paths, $"Beschreibung von {taskName}");

        foreach (var (phase, status) in statuses)
        {
            _store.RecordPhase(paths, phase, status);

            if (status == PhaseStatus.Completed)
            {
                WriteArtefactsFor(paths, phase);
            }
        }

        return paths;
    }

    private static void WriteArtefactsFor(TaskPaths paths, WorkflowPhase phase)
    {
        switch (phase)
        {
            case WorkflowPhase.Specification:
                File.WriteAllText(paths.SpecAbsolute, "spec");
                File.WriteAllText(paths.PlanAbsolute, "plan");
                break;
            case WorkflowPhase.Review:
                File.WriteAllText(paths.ReviewAbsolute, "review");
                break;
            case WorkflowPhase.Implementation:
                File.WriteAllText(paths.DoneAbsolute, "done");
                break;
            default:
                break;
        }
    }

    private TaskRecoveryScanner Create() => new(
        _settings,
        _store,
        maxAge: TimeSpan.FromDays(14),
        maxTasks: 5,
        maxSubdirectoriesPerRoot: 2000,
        scanTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ScanAsync_UnfinishedTask_IsOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));

        var found = await Create().ScanAsync(CancellationToken.None);

        var task = Assert.Single(found);
        Assert.Equal("alpha", task.Paths.TaskName);
        Assert.Equal(WorkflowPhase.Review, task.ResumePhase);
        Assert.Equal("Beschreibung von alpha", task.State.TaskDescription);
    }

    [Fact]
    public async Task ScanAsync_FolderWithoutAJournal_IsIgnored()
    {
        var workspace = NewWorkspace("ws");
        Directory.CreateDirectory(Path.Combine(workspace, "not-a-task"));

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_AllPhasesCompleted_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(
            workspace,
            "done",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed),
            (WorkflowPhase.Implementation, PhaseStatus.Completed));

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_DismissedTask_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha");
        _store.SetDismissed(paths, dismissed: true);

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_JournalOlderThanTheWindow_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(workspace, "alpha");

        var scanner = new TaskRecoveryScanner(
            _settings, _store, TimeSpan.Zero, 5, 2000, TimeSpan.FromSeconds(5));

        await Task.Delay(20);
        Assert.Empty(await scanner.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_MoreThanTheCap_ReturnsTheMostRecentlyUpdated()
    {
        var workspace = NewWorkspace("ws");
        for (var i = 0; i < 7; i++)
        {
            SeedTask(workspace, $"task{i}");
            await Task.Delay(15);
        }

        var found = await new TaskRecoveryScanner(
            _settings, _store, TimeSpan.FromDays(14), 3, 2000, TimeSpan.FromSeconds(5))
            .ScanAsync(CancellationToken.None);

        Assert.Equal(3, found.Count);
        Assert.Equal("task6", found[0].Paths.TaskName);
        Assert.Equal("task4", found[2].Paths.TaskName);
    }

    [Fact]
    public async Task ScanAsync_MissingRecentDirectory_DoesNotThrow()
    {
        var workspace = NewWorkspace("gone");
        Directory.Delete(workspace, recursive: true);

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_CompletedPhaseWhoseArtefactVanished_IsDemotedWithEverythingAfterIt()
    {
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        File.Delete(paths.PlanAbsolute);

        var task = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        Assert.Equal(WorkflowPhase.Specification, task.ResumePhase);
        Assert.All(task.State.Phases, p => Assert.NotEqual(PhaseStatus.Completed, p.Status));
    }

    [Fact]
    public async Task ScanAsync_PhaseThreeCompleted_IsNeverDemotedByDiskEvidence()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        var task = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        Assert.Equal(WorkflowPhase.Implementation, task.ResumePhase);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskRecoveryScannerTests"`
Expected: FAIL to compile — `TaskRecoveryScanner` does not exist.

- [ ] **Step 3: Write the model and interface**

Create `Workflow/Models/RecoverableTask.cs`:

```csharp
namespace Workflow.Models;

/// <summary>One interrupted task found by the startup scan.</summary>
/// <param name="Paths">The task's path set, derived from the folder that held the journal.</param>
/// <param name="State">The journal, already reconciled against the artefacts on disk.</param>
/// <param name="ResumePhase">The first phase that is not yet completed.</param>
public sealed record RecoverableTask(TaskPaths Paths, TaskState State, WorkflowPhase ResumePhase);
```

Create `Workflow/Services/ITaskRecoveryScanner.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>Finds tasks that were interrupted, so they can be offered as prefilled tabs.</summary>
public interface ITaskRecoveryScanner
{
    /// <summary>Scans the directory MRU for unfinished tasks.</summary>
    /// <param name="cancellationToken">Cancels the scan; partial results are still returned.</param>
    /// <returns>
    /// At most <c>maxTasks</c> tasks, most recently updated first. Never throws: a scan that
    /// fails must not stop the application from starting.
    /// </returns>
    public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Write the scanner**

Create `Workflow/Services/TaskRecoveryScanner.cs`:

```csharp
using System.IO;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskRecoveryScanner" />
public sealed class TaskRecoveryScanner : ITaskRecoveryScanner
{
    private readonly ISettingsService _settings;
    private readonly ITaskStateStore _store;
    private readonly TimeSpan _maxAge;
    private readonly int _maxTasks;
    private readonly int _maxSubdirectoriesPerRoot;
    private readonly TimeSpan _scanTimeout;

    /// <summary>Creates the scanner.</summary>
    /// <param name="settings">Supplies the directory MRU to scan.</param>
    /// <param name="store">Reads each candidate folder's journal.</param>
    /// <param name="maxAge">How stale a journal may be and still be offered.</param>
    /// <param name="maxTasks">Upper bound on the tabs the scan may cause.</param>
    /// <param name="maxSubdirectoriesPerRoot">Enumeration cap per MRU entry.</param>
    /// <param name="scanTimeout">Ceiling on the whole scan; partial results are kept.</param>
    public TaskRecoveryScanner(
        ISettingsService settings,
        ITaskStateStore store,
        TimeSpan maxAge,
        int maxTasks,
        int maxSubdirectoriesPerRoot,
        TimeSpan scanTimeout)
    {
        _settings = settings;
        _store = store;
        _maxAge = maxAge;
        _maxTasks = maxTasks;
        _maxSubdirectoriesPerRoot = maxSubdirectoriesPerRoot;
        _scanTimeout = scanTimeout;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private IReadOnlyList<RecoverableTask> Scan(CancellationToken cancellationToken)
    {
        // A slow or disconnected MRU entry must be an inconvenience, not a hang: whatever has
        // been collected when the ceiling is reached is what the user is offered.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_scanTimeout);

        var found = new List<RecoverableTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTimeOffset.UtcNow - _maxAge;

        foreach (var root in _settings.Settings.RecentDirectories)
        {
            if (timeout.IsCancellationRequested)
            {
                break;
            }

            foreach (var directory in EnumerateTaskFolders(root, timeout.Token))
            {
                if (!seen.Add(directory))
                {
                    continue;
                }

                var candidate = TryBuild(root, directory, cutoff);
                if (candidate is not null)
                {
                    found.Add(candidate);
                }
            }
        }

        return found
            .OrderByDescending(t => t.State.UpdatedUtc)
            .Take(_maxTasks)
            .ToList();
    }

    private IEnumerable<string> EnumerateTaskFolders(string root, CancellationToken cancellationToken)
    {
        List<string> directories;

        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            directories = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .TakeWhile(_ => !cancellationToken.IsCancellationRequested)
                .Take(_maxSubdirectoriesPerRoot)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return directories;
    }

    private RecoverableTask? TryBuild(string root, string directory, DateTimeOffset cutoff)
    {
        TaskPaths paths;

        try
        {
            paths = new TaskPaths(root, Path.GetFileName(directory));
        }
        catch (ArgumentException)
        {
            // An unusable folder name is simply not one of ours.
            return null;
        }

        var state = _store.TryLoad(paths);
        if (state is null || state.Dismissed)
        {
            return null;
        }

        // Clock skew, or a folder copied from another machine: treat the future as recent rather
        // than hiding a task the user can see is unfinished.
        if (state.UpdatedUtc < cutoff)
        {
            return null;
        }

        Reconcile(paths, state);

        var resume = state.Phases.FirstOrDefault(p => p.Status != PhaseStatus.Completed);
        return resume is null ? null : new RecoverableTask(paths, state, resume.Phase);
    }

    // Disk evidence may DEMOTE a phase; it may never promote one. Promotion would resurrect the
    // hazard where a folder that merely happens to hold a spec and a plan is reported as
    // "phase 1 done" for a task that never ran. Phase 3 is never demoted: its completion is
    // baseline-relative and leaves no trace on disk, so the journal is the only evidence.
    private static void Reconcile(TaskPaths paths, TaskState state)
    {
        var demoteFromHere = false;

        for (var i = 0; i < state.Phases.Count; i++)
        {
            var entry = state.Phases[i];

            if (!demoteFromHere && entry.Status == PhaseStatus.Completed && !ArtefactsPresent(paths, entry.Phase))
            {
                demoteFromHere = true;
            }

            if (demoteFromHere)
            {
                state.Phases[i] = new TaskPhaseState(entry.Phase, PhaseStatus.Pending, null);
            }
        }
    }

    private static bool ArtefactsPresent(TaskPaths paths, WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Specification => NonEmpty(paths.SpecAbsolute) && NonEmpty(paths.PlanAbsolute),
        WorkflowPhase.Review => NonEmpty(paths.ReviewAbsolute),
        WorkflowPhase.Implementation => NonEmpty(paths.DoneAbsolute),
        // ResolveReview leaves no disk trace; see the remark on Reconcile.
        _ => true,
    };

    private static bool NonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            // Unreadable is not the same as absent; do not demote on a transient lock.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskRecoveryScannerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Workflow/Models/RecoverableTask.cs Workflow/Services/ITaskRecoveryScanner.cs Workflow/Services/TaskRecoveryScanner.cs Workflow.Tests/TaskRecoveryScannerTests.cs
git commit -m "feat(recovery): scan the directory MRU for interrupted tasks

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: The tab knows how to be resumed

**Files:**
- Modify: `Workflow/ViewModels/TaskTabViewModel.cs`
- Modify: `Workflow/Services/TaskTabViewModelFactory.cs`
- Modify: `Workflow/Views/TaskTabView.xaml` (the Start button's `TextBlock`)
- Test: `Workflow.Tests/TaskTabViewModelTests.cs`
- Also update (constructor churn): `Workflow.Tests/TaskTabDirectoryComboBoxTests.cs:47,74`, `Workflow.Tests/TaskTabDebounceThreadingTests.cs:48`, `Workflow.Tests/MainWindowViewModelTests.cs` (`StubFactory.Create`)

**Interfaces:**
- Consumes: `ITaskStateStore` (Task 5), `RecoverableTask` (Task 8), `WorkflowRunRequest.StartPhase` (Task 6).
- Produces on `TaskTabViewModel`:
  - `public WorkflowPhase? ResumePhase { get; }`
  - `public bool IsRecovered { get; }`
  - `public string StartButtonLabel { get; }`
  - `public void LoadForResume(RecoverableTask task)`
  - `public void NotifyClosedByUser()`
  - constructor: `TaskTabViewModel(ITaskFolderService folders, IWorkflowOrchestrator orchestrator, ISettingsService settings, ITaskStateStore stateStore, IDirectoryPickerService picker, TerminalViewModel terminal, TimeSpan folderDebounce, IReadOnlyList<string> startupErrors)` — `stateStore` inserted after `settings`.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/TaskTabViewModelTests.cs` (and add `using Workflow.Tests.Fakes;`). Change the fixture's `Create` helper to accept and pass an `ITaskStateStore`, defaulting to a new `FakeTaskStateStore`, exposing the instance to the test:

```csharp
    [Fact]
    public void LoadForResume_PrefillsEveryFieldAndFlipsTheButton()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = new TaskState { TaskDescription = "die Beschreibung", UpdatedUtc = DateTimeOffset.UtcNow };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Active, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Review));

        Assert.Equal("alpha", vm.TaskName);
        Assert.Equal("die Beschreibung", vm.TaskDescription);
        Assert.Equal(_root, vm.WorkingDirectory);
        Assert.True(vm.IsNameLocked);
        Assert.True(vm.IsRecovered);
        Assert.Equal(WorkflowPhase.Review, vm.ResumePhase);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[0].Status);

        // An Active in the journal means the app died mid-phase: it is about to be re-run.
        Assert.Equal(PhaseStatus.Pending, vm.Phases[1].Status);
    }

    [Fact]
    public void StartButtonLabel_FreshTab_SaysStartWorkflow()
    {
        using var vm = Create();

        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.Null(vm.ResumePhase);
        Assert.False(vm.IsRecovered);
    }

    [Fact]
    public void StartWorkflow_OnARecoveredTab_RunsFromTheResumePhase()
    {
        var store = new FakeTaskStateStore();
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator, stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.ResolveReview));
        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.ResolveReview, orchestrator.Request.StartPhase);
        Assert.Contains("d", store.Descriptions);
    }

    [Fact]
    public void NotifyClosedByUser_RecoveredTab_Dismisses()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = new TaskState { UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Specification));
        vm.NotifyClosedByUser();

        Assert.Contains(store.Dismissals, d => d.Dismissed);
    }

    [Fact]
    public void NotifyClosedByUser_FreshTab_DoesNothing()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        vm.NotifyClosedByUser();

        Assert.Empty(store.Dismissals);
    }

    [Fact]
    public void TypingAnUnfinishedTaskName_ReArmsContinue()
    {
        var store = new FakeTaskStateStore();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        store.Seed(paths, state);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        Assert.Equal(WorkflowPhase.Review, vm.ResumePhase);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
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

`TypingAnUnfinishedTaskName_ReArmsContinue` relies on the fixture's `folderDebounce: TimeSpan.Zero`, which makes `SyncFolder` run synchronously.

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: FAIL to compile — no `LoadForResume`, no `stateStore` parameter.

- [ ] **Step 3: Add the constructor dependency**

In `Workflow/ViewModels/TaskTabViewModel.cs`, add `private readonly ITaskStateStore _stateStore;`, insert the `ITaskStateStore stateStore` parameter after `ISettingsService settings` (with its `<param>` doc line), `ArgumentNullException.ThrowIfNull(stateStore);`, and `_stateStore = stateStore;`.

In `Workflow/Services/TaskTabViewModelFactory.cs`, add the matching field, constructor parameter (same position, after `ISettingsService settings`) and doc line, and pass it through in `Create()`.

Update the four test construction sites listed under **Files** to pass `new FakeTaskStateStore()` (or `new TaskStateStore()`) in the new position.

- [ ] **Step 4: Add the resume state**

In `Workflow/ViewModels/TaskTabViewModel.cs`:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonLabel))]
    private WorkflowPhase? _resumePhase;

    [ObservableProperty]
    private bool _isRecovered;
```

and next to `Header`:

```csharp
    /// <summary>The Start button's caption: 'Continue workflow' once a resume point is known.</summary>
    public string StartButtonLabel => ResumePhase is null ? "Start workflow" : "Continue workflow";
```

- [ ] **Step 5: Implement `LoadForResume`**

```csharp
    /// <summary>Prefills this tab from an interrupted task found by the startup scan.</summary>
    /// <param name="task">The task to restore.</param>
    public void LoadForResume(RecoverableTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Every assignment below would otherwise schedule the debounced folder sync, which could
        // try to create - or worse, RENAME - a folder that already holds this task's artefacts.
        _suppressFolderSync = true;

        try
        {
            WorkingDirectory = task.Paths.WorkingDirectory;
            TaskDescription = task.State.TaskDescription;
            TaskName = task.Paths.TaskName;

            // The folder and the field now agree, so no rename can ever be attempted.
            _folderOnDisk = task.Paths.TaskName;

            // The artefact names embed the task name (T_spec.md, T_plan.md, T-review.md,
            // T-done.md) and the paths are already written into the spec and plan on disk.
            // Renaming would orphan all of them, so the name is frozen - the same argument
            // BASE section 8.4 makes for a running pipeline.
            IsNameLocked = true;

            ApplyJournal(task.State);
            ResumePhase = task.ResumePhase;
            IsRecovered = true;
        }
        finally
        {
            _suppressFolderSync = false;
        }

        // The startup prompt-template gate still wins over everything (BASE section 9.4).
        ValidationMessage = _startupErrors.Count > 0
            ? _startupErrors[0]
            : _folders.Validate(TaskName, WorkingDirectory) is { IsValid: false } invalid
                ? invalid.ErrorMessage
                : null;
    }

    // An Active entry means the application died while that phase was running. It is about to be
    // re-run, so it is shown grey, not yellow.
    private void ApplyJournal(TaskState state)
    {
        foreach (var entry in state.Phases)
        {
            var indicator = Phases.FirstOrDefault(p => p.Phase == entry.Phase);
            if (indicator is not null)
            {
                indicator.Status = entry.Status == PhaseStatus.Active ? PhaseStatus.Pending : entry.Status;
            }
        }
    }
```

- [ ] **Step 6: Re-arm Continue from `SyncFolder`, and dismiss on close**

At the end of `SyncFolder()`, after the successful `_folders.EnsureCreated(paths); _folderOnDisk = paths.TaskName;` lines, add:

```csharp
            // Typing the name of an unfinished task - including one the user dismissed by closing
            // its recovered tab - must bring back 'Continue workflow'. This is what makes the
            // dismissal non-destructive.
            RefreshResumeStateFromJournal(paths);
```

and add:

```csharp
    private void RefreshResumeStateFromJournal(TaskPaths paths)
    {
        var state = _stateStore.TryLoad(paths);

        if (state is null)
        {
            ResumePhase = null;
            return;
        }

        ApplyJournal(state);

        var next = state.Phases.FirstOrDefault(p => p.Status != PhaseStatus.Completed);
        ResumePhase = next?.Phase;
    }
```

Then add the close hook:

```csharp
    /// <summary>
    /// Called when the user closes this tab, as opposed to the application shutting down.
    /// A recovered task is then not offered again until it is continued (SPEC section 6.7).
    /// </summary>
    public void NotifyClosedByUser()
    {
        if (!IsRecovered || string.IsNullOrWhiteSpace(WorkingDirectory) || string.IsNullOrWhiteSpace(TaskName))
        {
            return;
        }

        _stateStore.SetDismissed(new TaskPaths(WorkingDirectory, TaskName), dismissed: true);
    }
```

- [ ] **Step 7: Pass `StartPhase` and persist the description on start**

In `StartWorkflow()`, after `var paths = new TaskPaths(WorkingDirectory, TaskName);`:

```csharp
        // Persist the description BEFORE the run: a crash one second from now must still recover
        // a tab with its Taskbeschreibung intact.
        _stateStore.SaveDescription(paths, TaskDescription);
```

and change the request construction to:

```csharp
        var request = new WorkflowRunRequest(
            paths,
            TaskDescription,
            Terminal,
            _manualSignal,
            new Progress<PhaseProgress>(ApplyProgress),
            ResumePhase ?? WorkflowPhase.Specification);
```

- [ ] **Step 8: Bind the button caption**

In `Workflow/Views/TaskTabView.xaml`, change the Start button's inner `TextBlock` from

```xml
<TextBlock Margin="8,0,0,0" Text="Start workflow" />
```

to

```xml
<TextBlock Margin="8,0,0,0" Text="{Binding StartButtonLabel}" />
```

- [ ] **Step 9: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add Workflow/ViewModels/TaskTabViewModel.cs Workflow/Services/TaskTabViewModelFactory.cs Workflow/Views/TaskTabView.xaml Workflow.Tests/TaskTabViewModelTests.cs Workflow.Tests/TaskTabDirectoryComboBoxTests.cs Workflow.Tests/TaskTabDebounceThreadingTests.cs Workflow.Tests/MainWindowViewModelTests.cs
git commit -m "feat(tab): prefill and resume an interrupted task from its journal

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Offer recovered tasks at startup

**Files:**
- Modify: `Workflow/ViewModels/MainWindowViewModel.cs`
- Modify: `Workflow/App.xaml.cs`
- Test: `Workflow.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `ITaskRecoveryScanner` (Task 8), `TaskTabViewModel.LoadForResume` / `NotifyClosedByUser` (Task 9).
- Produces: `MainWindowViewModel(ITaskTabViewModelFactory factory, ITaskRecoveryScanner scanner, IReadOnlyList<string> startupErrors)` and `public Task InitialiseAsync()`.

- [ ] **Step 1: Write the failing tests**

Append to `Workflow.Tests/MainWindowViewModelTests.cs`:

```csharp
    private sealed class StubScanner(params RecoverableTask[] tasks) : ITaskRecoveryScanner
    {
        public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecoverableTask>>(tasks);
    }

    private RecoverableTask MakeRecoverable(string name)
    {
        var paths = new TaskPaths(_root, name);
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = new TaskState { TaskDescription = $"d-{name}", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        return new RecoverableTask(paths, state, WorkflowPhase.Specification);
    }

    [Fact]
    public async Task InitialiseAsync_AppendsOneTabPerRecoveredTaskAndSelectsTheFirst()
    {
        var shell = new MainWindowViewModel(
            new StubFactory(_settings, []),
            new StubScanner(MakeRecoverable("alpha"), MakeRecoverable("beta")),
            []);

        await shell.InitialiseAsync();

        Assert.Equal(3, shell.Tabs.Count);          // the blank tab plus two recovered ones
        Assert.Equal("alpha", shell.Tabs[1].TaskName);
        Assert.Equal("beta", shell.Tabs[2].TaskName);
        Assert.Same(shell.Tabs[1], shell.SelectedTab);

        shell.ShutdownAll();
    }

    [Fact]
    public async Task InitialiseAsync_NothingToRecover_LeavesTheBlankTabSelected()
    {
        var shell = new MainWindowViewModel(new StubFactory(_settings, []), new StubScanner(), []);

        await shell.InitialiseAsync();

        Assert.Single(shell.Tabs);
        Assert.Same(shell.Tabs[0], shell.SelectedTab);

        shell.ShutdownAll();
    }
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: FAIL to compile — the constructor takes two arguments and there is no `InitialiseAsync`.

- [ ] **Step 3: Implement**

In `Workflow/ViewModels/MainWindowViewModel.cs`:

- add `private readonly ITaskRecoveryScanner _scanner;`, the constructor parameter `ITaskRecoveryScanner scanner` between `factory` and `startupErrors` (with its doc line), and `_scanner = scanner;`
- add:

```csharp
    /// <summary>
    /// Offers every interrupted task the scan found as a prefilled tab. Deliberately not awaited
    /// before the window is shown: a slow or disconnected MRU entry must not delay startup.
    /// </summary>
    /// <returns>A task that completes once the recovered tabs have been added.</returns>
    public async Task InitialiseAsync()
    {
        IReadOnlyList<RecoverableTask> recovered;

        try
        {
            recovered = await _scanner.ScanAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TaskTabViewModel? first = null;

        foreach (var task in recovered)
        {
            var tab = _factory.Create();
            tab.CloseRequested += OnTabCloseRequested;
            tab.LoadForResume(task);
            Tabs.Add(tab);
            first ??= tab;
        }

        if (first is not null)
        {
            SelectedTab = first;
        }
    }
```

- in `CloseTab`, immediately before `tab.Dispose();`:

```csharp
        // Only a user-initiated close dismisses a recovered task. ShutdownAll must not: closing
        // the application is not a statement about any task.
        tab.NotifyClosedByUser();
```

Add `using Workflow.Models;` to the file.

In `Workflow/App.xaml.cs`, build the scanner from the store hoisted in Task 6 and kick the scan off after `window.Show()`:

```csharp
        var scanner = new TaskRecoveryScanner(
            settings,
            stateStore,
            maxAge: TimeSpan.FromDays(14),
            maxTasks: 5,
            maxSubdirectoriesPerRoot: 2000,
            scanTimeout: TimeSpan.FromSeconds(5));

        _shell = new MainWindowViewModel(factory, scanner, startupErrors);
```

and after `window.Show();`:

```csharp
        // Not awaited: the window must appear immediately even when an MRU entry is a
        // disconnected network share. The recovered tabs materialise a moment later.
        _ = _shell.InitialiseAsync();
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test Workflow.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Workflow/ViewModels/MainWindowViewModel.cs Workflow/App.xaml.cs Workflow.Tests/MainWindowViewModelTests.cs
git commit -m "feat(shell): offer interrupted tasks as prefilled tabs at startup

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 11: Acceptance gate

**Files:**
- Modify: `Workflow/verify.ps1` (the trailing manual-step list)

- [ ] **Step 1: Add the new manual steps**

In `Workflow/verify.ps1`, append to the `Remaining manual steps` block and change its header to `(V5-V18)`:

```powershell
Write-Host '  V12 Run a task to the end. .workflow-state.json must hold four Completed phases,'
Write-Host '      plausible timestamps and the Taskbeschreibung verbatim.'
Write-Host '  V13 Kill Workflow.exe while phase 2 is yellow, restart: a prefilled tab appears with'
Write-Host '      "Continue workflow"; clicking it re-sends the PHASE 2 prompt and does not re-run phase 1.'
Write-Host '  V14 During phase 3, touch only T_spec.md - the phase must stay yellow. Then touch'
Write-Host '      T_plan.md - it must go green.'
Write-Host '  V15 Phase 4 must turn green on its own once T-done.md exists and is non-empty.'
Write-Host '  V16 Every phase must submit its prompt without the user pressing Enter.'
Write-Host '  V17 Delete T_plan.md from a task whose journal says phase 1 is complete, restart:'
Write-Host '      the recovered tab must show phase 1 grey and resume at phase 1.'
Write-Host '  V18 Put a disconnected network share in the directory MRU: the window must still'
Write-Host '      appear promptly and the app must stay usable.'
```

- [ ] **Step 2: Run the full gate**

Run: `pwsh -File Workflow\verify.ps1`
Expected: `All automated checks passed.` and exit code 0. Specifically confirm:
- `dotnet build` produced **no** warnings (they are errors, so a warning fails the build outright).
- Every prompt token is known — this is where a `{done_path}` typo surfaces.

- [ ] **Step 3: Sanity-check the running app**

Run: `dotnet run --project Workflow\Workflow.csproj -c Debug`
Expected: the window appears immediately, one blank tab, the button reads **Start workflow**, and no recovery tabs appear when no journal exists anywhere in the MRU.

- [ ] **Step 4: Commit**

```bash
git add Workflow/verify.ps1
git commit -m "chore(verify): document the recovery and auto-submit manual steps

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Appendix: spec-to-task coverage

| SPEC section | Task |
|---|---|
| §5.1 journal location, `StateAbsolute` | 1 |
| §5.2 journal shape, `TaskState` / `TaskPhaseState` | 5 |
| §5.3 name and directory are derived | 5 (model remark), 8 (`TryBuild`) |
| §5.4 `ITaskStateStore` contract and durability | 5 |
| §6.1 `RecoverableTask` | 8 |
| §6.2 the scan, bounding, exception policy | 8 |
| §6.3 resume phase + demote-never-promote | 8 |
| §6.4 startup wiring | 10 |
| §6.5 the recovered tab, `LoadForResume` | 9 |
| §6.6 re-arming from `SyncFolder` | 9 |
| §6.7 dismissal on `CloseTab`, not `ShutdownAll` | 9 (view model), 10 (shell) |
| §7.1 `WorkflowRunRequest.StartPhase` | 6 |
| §7.2 `RunAsync` skip | 6 |
| §7.3 journal writes, store-first | 6, 9 (`SaveDescription`) |
| §8.1 new phase table | 3, 4 |
| §8.2 `AllContentChanged` | 3 |
| §8.3 done marker, `{done_path}`, stale-marker guard, drop `Manual` | 1, 2, 4 |
| §9.1–9.3 the submit fix | 7 |
| §9.4 configuration | 7 |
| §10 failure modes | 5 (R1–R3, W1), 8 (R4–R11), 4 (C2), 3 (C4), 7 (S1–S4) |
| §11 testing strategy | every task's test step |
| §12 acceptance criteria | 11 |
