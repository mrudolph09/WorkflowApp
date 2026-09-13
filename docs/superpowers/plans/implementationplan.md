---
description: "Canonical Superpowers implementation plan for the Workflow WPF (.NET 8) task-orchestration app"
summary: "17 TDD tasks building the Workflow app: build foundation (Directory.Build.props importing .roslyn, restored PackageReferences, xUnit test project), domain models, TaskFolderService, strict PromptTemplateService, auto-answer rule engine, ArtifactWatcher, SettingsService, ConPTY native layer, WorkflowOrchestrator, xterm.js/WebView2 terminal host, converters/behaviors, ViewModels, XAML views with a retemplated MahApps TabControl carrying the plus button, ArmFlex .ico, and a verify.ps1 acceptance gate. REVIEW-RESOLVED 2026-09-12: Global Constraints now carry an Analyzer conformance block verified by probe build (UseWPF strips System.IO from implicit usings; IDE0040 fires on every interface member; CA1002/CA5392/CA1062/CA1508/CA1001/CA2213 all fire) and every task must end at 0 warnings; CA1031 removed from global NoWarn, CA1003 added; Task 4 rebased on the prompt files actually on disk (only the stray underscore remains, plus a NEW defect: a live qdocimporter/eval completion bullet that must be deleted) with shipped-template assertions; Task 6 hashes tri-state and fails on a deleted directory; Tasks 2/3/7 share a root-aware WorkingDirectoryPath so C:-backslash survives; Task 8 opens with a BLOCKING ConPTY spike because the documented sequence provably does not stream output (child attaches, zero bytes read) and records what was already ruled out; Task 9 adds Gate A (observed launcher readiness) and an awaited, session-scoped paste; Task 11 awaits the xterm ready handshake and stops disposing the terminal on tab-switch Unloaded; Task 12 wires RichTextBox TextChanged via coercion and writes back with SetCurrentValue; Task 13/14 thread startup errors into every tab, roll a failed rename back, and surface every startup failure family; Task 15 forwards SelectedContentTemplate."
paths:
  - "../specs/specification.md"
  - "../../../Workflow/Workflow.csproj"
  - "../../../Workflow/.roslyn"
  - "../../../Workflow/.editorconfig"
---

# Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a WPF/.NET 8 desktop app that manages Tasks in tabs and drives each Task through a four-phase Claude Code / Codex pipeline in an embedded, fully interactive terminal.

**Architecture:** Strict MVVM with CommunityToolkit.Mvvm source generators over a manual composition root. Each phase spawns a fresh Windows pseudo-console (ConPTY) hosting `pwsh`, whose VT stream is rendered by xterm.js inside a WebView2. Phase completion is detected from artefact files on disk (existence, SHA-256 delta, or a manual button). Everything below the ViewModel layer is behind an interface and unit-tested; only the ConPTY and WebView2 edges are verified by integration/manual steps.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF, CommunityToolkit.Mvvm 8.4.2, MaterialDesignThemes 5.3.2, MaterialDesignColors 5.3.2, MaterialDesignThemes.MahApps 5.3.2, MahApps.Metro 2.4.11, Microsoft.Web.WebView2, xterm.js 5.5.0 + addon-fit 0.10.0, xUnit.

**Spec:** `C:\Users\Marco\Documents\repo\Workflow\docs\superpowers\specs\specification.md`

---

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the spec.

- **Target framework:** `net8.0-windows`. Do **not** retarget. C# language version stays at the `net8.0` default (C# 12).
- **`Directory.Build.props` at the repository root imports `Workflow\.roslyn`.** Its settings are binding: `AnalysisMode=All`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=true`, `Nullable=enable`, `GenerateDocumentationFile=true`, `AllowUnsafeBlocks=false`.
- **No `unsafe` keyword anywhere.** ConPTY interop uses `DllImport` + `IntPtr` + `Marshal` only.
- **`.editorconfig` rules are errors, not suggestions:**
  - never write `this.`;
  - always write an explicit accessibility modifier on every member;
  - use `var` for built-in types and when the type is apparent;
  - interfaces are `I`-prefixed;
  - **private fields are `_`-prefixed**;
  - `CA1822`, `IDE0051`, `IDE0052`, `IDE0059`, `CA1825`, `CA1806` are errors.
- **`[ObservableProperty]` goes on a private field, never on a property** (`MVVMTK0040`). `[ObservableProperty] private string _taskName = string.Empty;` generates the public property `TaskName`.
- **Only these `NoWarn` entries are permitted in `Directory.Build.props`**, each with an inline XML comment giving its justification: `CA2007`, `CA1303`, `SYSLIB1054`, `CA1812`, `CA1848`, `CA1515`, `CA1003`.
- **`CA1031` is NOT one of them.** It is suppressed only by a local `#pragma warning disable CA1031` at the PTY read loop and the top-level dispatcher handler. A repository-wide entry would disable broad-catch diagnostics for all future code (spec §4.4, A7).
- **`Workflow.Tests` additionally suppresses** `CA1707`, `CA1822`, `CA2007`, `CA1303`, `CA1861`.

### Analyzer conformance — non-negotiable, and verified by probe build

The strict policy above turns ordinary-looking code into **build errors**. These were reproduced
on 2026-09-12 in a probe project carrying this repository's exact `.roslyn`, `.editorconfig` and
`Directory.Build.props`. Apply them while writing each file; do not leave them for a later task.

- **`using System.IO;` is required in every file that touches `Path`, `File`, `Directory`,
  `FileStream`, `FileSystemWatcher` or `IOException` — in the test project too.**
  `UseWPF=true` emits a *reduced* implicit-using set: only `System`,
  `System.Collections.Generic`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`.
  `System.IO` and `System.Net.Http` are **not** implicit. Omitting the using is `CS0103`.
- **Every interface member carries an explicit `public`.** `.editorconfig` sets
  `dotnet_style_require_accessibility_modifiers = always`, not the .NET default
  `for_non_interface_members`, so `Task WaitAsync(CancellationToken ct);` is an `IDE0040` error.
  Write `public Task WaitAsync(CancellationToken ct);`. This applies to **every** interface block
  in this plan; the snippets below show the corrected form.
- **`CA1002`** — no public member returns `List<T>`. Use `Collection<T>` or `IReadOnlyList<T>`.
- **`CA5392`** — every `[DllImport]` needs a search path. Put
  `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` on the `NativeMethods` class.
- **`CA1062`** — every public method validates its reference-type parameters first
  (`ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace`).
- **`CA1508`** — do not add a redundant null check after a `ThrowIfNull`; the analyzer proves it
  dead and errors.
- **`CA1001` / `CA2213`** — a type holding a `CancellationTokenSource`, `FileSystemWatcher`,
  `DispatcherTimer`, `FileStream` or `SemaphoreSlim` implements `IDisposable` **and** disposes
  every one of those fields, including on early-return paths.
- **`CA1806`** — never discard a return value implicitly.

**A task is not done while the build is not clean.** Each task's verification step runs
`dotnet build Workflow.sln -c Debug` and requires *0 Warning(s), 0 Error(s)*. If a rule not
listed in the `NoWarn` set blocks you, **fix the code**. Adding a `NoWarn` entry is a
specification change: amend spec §4.4 first, then `Directory.Build.props`, with the
justification comment.
- **Pin exact package versions.** No floating ranges.
- **PTY bytes are never decoded to `string` in C#.** They travel as `byte[]` to xterm.js, which decodes UTF-8 itself.
- **UI strings are German.** Code identifiers, comments and tests are English.
- **Prompt variables — the entire contract.** Only these six tokens may appear in `Prompt\*.md`: `{taskbezeichnung}`, `{taskbeschreibung}`, `{AppDirectory}`, `{spec_path}`, `{plan_path}`, `{review_path}`.
- **`{AppDirectory}` resolves to the selected working directory**, not to the folder containing `Workflow.exe`.
- **Task folder is `{WorkingDirectory}\{TaskName}`.**
- The repository is **not** a git repository yet. Task 1 initialises it; every later task ends in a commit.

---

## Useful `.agents` skills

Requested explicitly by the task description. Skills live under
`C:\vb5\QuincyNET\Quincy_App\Modul\AMTS_Medikationsplan\.agents\skills`; the routing index is
`SKILL-ROUTING-INDEX.md` in that folder. Open a leaf's `SKILL.md` before relying on it — its
`USE FOR` / `DO NOT USE FOR` overrides the index.

| Skill | Use it in | Why |
|---|---|---|
| `dotnet/skills/dotnet-pinvoke` | **Task 8** | The single highest-risk task. Covers `DllImport` signatures, marshalling, `SafeHandle` ownership and native-boundary leaks — exactly the ConPTY work. |
| `dotnet-msbuild/skills/directory-build-organization` | **Task 1** | How to lay out `Directory.Build.props` and import a shared fragment such as `.roslyn`. |
| `dotnet-msbuild/skills/msbuild-antipatterns` | **Task 1** | BAD→GOOD patterns for the `.csproj` edits (Content globs, `CopyToOutputDirectory`). |
| `dotnet-msbuild/skills/binlog-generation` + `binlog-failure-analysis` | **Task 1, Task 17** | `TreatWarningsAsErrors` + `AnalysisMode=All` produces large analyzer failure walls; capture `/bl:` and replay instead of scrolling console output. |
| `dotnet-upgrade/skills/migrate-nullable-references` | **Tasks 12, 13, 14** | `Nullable=enable` with `CS86xx` as errors; the skill covers the WPF `IValueConverter` boundary specifically, which is Task 12. |
| `dotnet-diag/skills/analyzing-dotnet-performance` | **Tasks 8, 11** | Async/allocation anti-patterns in the PTY read loop and the 16 ms output-coalescing path. |
| `dotnet-test/skills/run-tests` | **every task** | Detects VSTest vs. Microsoft.Testing.Platform and gets `dotnet test` filters right. |
| `dotnet-test/skills/writing-mstest-tests` | reference only | This plan uses xUnit; read only if the team later switches frameworks. |
| `mp-test/skills/writing-mp-flaui-tests` | optional follow-up | The in-house pattern for FlaUI UI tests of a WPF app (`IClassFixture`, `WaitHelper`, `ScreenshotHelper`). Out of scope here; the manual steps in Task 17 cover the UI. |
| `amts-medikationsplan-dotnet` (Claude skill) | reference only | Routes to the leaf skills above. |
| `wpf-design` (Claude skill) | **Tasks 15, 16** | The edit→build→screenshot→self-review visual loop. |
| `context7-mcp` (Claude skill) | **Tasks 5, 10, 11, 15** | Current MaterialDesign / MahApps / xterm.js / WebView2 API docs. Do not guess API surface. |

Not relevant, do not load: `dotnet-maui/*`, `dotnet-data/*`, `dotnet-ai/*`, `dotnet-upgrade/skills/migrate-dotnet8-to-dotnet9` (we stay on .NET 8), `dotnet-diag/skills/android-tombstone-symbolication`, `dotnet-diag/skills/clr-activation-debugging`.

---

## File Structure

| Path (relative to `C:\Users\Marco\Documents\repo\Workflow`) | Responsibility | Task |
|---|---|---|
| `Directory.Build.props` | Imports `.roslyn`; the only place `NoWarn` lives. | 1 |
| `.gitignore` | `bin/`, `obj/`, `.vs/`, `*.user`. | 1 |
| `Workflow\Workflow.csproj` | Package refs, Content items, `ApplicationIcon`. | 1, 16 |
| `Workflow.Tests\Workflow.Tests.csproj` | xUnit test project. | 1 |
| `Workflow\Models\WorkflowPhase.cs` | Phase + status + completion-rule enums. | 2 |
| `Workflow\Models\PhaseDefinition.cs` | One phase's static description. | 2 |
| `Workflow\Models\PhaseCatalog.cs` | The four `PhaseDefinition` instances. | 2 |
| `Workflow\Models\TaskPaths.cs` | Every derived path for one task. Single source of truth. | 2 |
| `Workflow\Services\ITaskFolderService.cs` / `TaskFolderService.cs` | Name validation, folder create/rename. | 3 |
| `Workflow\Services\IPromptTemplateService.cs` / `PromptTemplateService.cs` | Strict `{token}` rendering + startup validation. | 4 |
| `Workflow\Prompt\*.md` | The four templates (three are repaired). | 4 |
| `Workflow\Models\AutoAnswerRule.cs` | Rule + rule-set records. | 5 |
| `Workflow\Services\EscapeDecoder.cs` | `\r \n \t \e \\ \u####` decoding. | 5 |
| `Workflow\Services\IAutoAnswerService.cs` / `AutoAnswerService.cs` | Loads rules, first-match-wins. | 5 |
| `Workflow\Assets\autoanswer.rules.json` | The shipped default rules. | 5 |
| `Workflow\Services\IArtifactWatcher.cs` / `ArtifactWatcher.cs` / `ArtifactWatcherFactory.cs` | Watcher + poll fallback + debounce. | 6 |
| `Workflow\Models\AppSettings.cs`, `Workflow\Services\ISettingsService.cs` / `SettingsService.cs` | Directory MRU persistence. | 7 |
| `Workflow\Terminal\ITerminalSession.cs`, `ConPtySession.cs`, `Native\*.cs` | ConPTY. | 8 |
| `Workflow\Services\ITerminalController.cs` | The orchestrator's only view of a terminal. | 9 |
| `Workflow\Services\ManualPhaseSignal.cs` | Button-driven phase completion. | 9 |
| `Workflow\Services\IWorkflowOrchestrator.cs` / `WorkflowOrchestrator.cs` | The four-phase state machine. | 9 |
| `Workflow\Assets\Terminal\*` | `terminal.html`, `terminal.js`, vendored xterm. | 10 |
| `Workflow\ViewModels\TerminalViewModel.cs`, `Workflow\Views\TerminalView.xaml(.cs)` | WebView2 host; implements `ITerminalController`. | 11 |
| `Workflow\Converters\*.cs`, `Workflow\Behaviors\RichTextBoxAssist.cs` | View-layer glue. | 12 |
| `Workflow\ViewModels\PhaseIndicatorViewModel.cs`, `TaskTabViewModel.cs` | Per-tab state. | 13 |
| `Workflow\ViewModels\MainWindowViewModel.cs`, `Workflow\Services\TaskTabViewModelFactory.cs`, `Workflow\App.xaml(.cs)` | Shell + composition root + theme. | 14 |
| `Workflow\Views\MainWindow.xaml(.cs)`, `TaskTabView.xaml(.cs)`, `PhaseIndicatorView.xaml(.cs)`, `Workflow\Styles\TabControlStyles.xaml` | XAML. | 15 |
| `Workflow\Assets\workflow.ico`, `tools\GenerateIcon\*` | ArmFlex application icon. | 16 |
| `Workflow\verify.ps1` | Automated acceptance gate. | 17 |

---

## Task 1: Build foundation

**Files:**
- Create: `Directory.Build.props`, `.gitignore`, `Workflow.Tests\Workflow.Tests.csproj`, `Workflow.Tests\PlaceholderTests.cs`
- Modify: `Workflow\Workflow.csproj`, `Workflow.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: a solution that builds clean under `TreatWarningsAsErrors` and runs `dotnet test`.

- [ ] **Step 1: Initialise git and ignore build output**

```bash
cd "C:/Users/Marco/Documents/repo/Workflow"
git init
```

Create `.gitignore`:

```gitignore
bin/
obj/
.vs/
*.user
*.binlog
```

- [ ] **Step 2: Create `Directory.Build.props` at the repository root**

`.roslyn` is a valid MSBuild fragment that nothing imports. This wires it up and is the only
place `NoWarn` is allowed to live.

```xml
<Project>

  <!-- Workflow\.roslyn holds the analyzer / nullable / warnings-as-errors policy.
       It is a plain MSBuild fragment and is imported here so it actually takes effect. -->
  <Import Project="$(MSBuildThisFileDirectory)Workflow\.roslyn" />

  <PropertyGroup>
    <LangVersion>12.0</LangVersion>
    <UseArtifactsOutput>false</UseArtifactsOutput>
  </PropertyGroup>

  <PropertyGroup>
    <!-- CA2007: ConfigureAwait(false) is wrong in WPF - continuations must resume on the UI thread. -->
    <NoWarn>$(NoWarn);CA2007</NoWarn>
    <!-- CA1303: the UI is German-only by requirement; there is no resource table to localise into. -->
    <NoWarn>$(NoWarn);CA1303</NoWarn>
    <!-- SYSLIB1054: LibraryImport emits unsafe marshalling code; .roslyn sets AllowUnsafeBlocks=false. -->
    <NoWarn>$(NoWarn);SYSLIB1054</NoWarn>
    <!-- CA1003: CA1003 demands EventHandler<T> with T : EventArgs. The only two events in the
         app carry a raw ReadOnlyMemory<byte> PTY chunk and an int exit code; wrapping either in
         an EventArgs subclass allocates on the 4 KiB read path and buys nothing. -->
    <NoWarn>$(NoWarn);CA1003</NoWarn>
    <!-- NOTE: CA1031 is deliberately NOT listed. A broad catch is correct in exactly two places
         (the PTY read loop and the top-level dispatcher handler) and is suppressed there with a
         local #pragma. A repository-wide entry would silently disable the diagnostic for all
         future code - see spec section 4.4 and acceptance criterion A2/A7. -->
    <!-- CA1812: ViewModels and services are instantiated by the composition root or by XAML,
         which the analyzer cannot see. -->
    <NoWarn>$(NoWarn);CA1812</NoWarn>
    <!-- CA1848: no ILogger is used in this application. -->
    <NoWarn>$(NoWarn);CA1848</NoWarn>
    <!-- CA1515: making every ViewModel/service internal would force InternalsVisibleTo for the
         test project and fight XAML tooling, for no benefit in a single-assembly desktop app. -->
    <NoWarn>$(NoWarn);CA1515</NoWarn>
  </PropertyGroup>

</Project>
```

- [ ] **Step 3: Restore the package references in `Workflow\Workflow.csproj`**

`obj\project.assets.json` proves these four were declared at the last restore; `MahApps.Metro`
was only transitive and is promoted to direct because the XAML uses its namespace directly.
Replace the whole file with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <RootNamespace>Workflow</RootNamespace>
    <AssemblyName>Workflow</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="MaterialDesignColors" Version="5.3.2" />
    <PackageReference Include="MaterialDesignThemes" Version="5.3.2" />
    <PackageReference Include="MaterialDesignThemes.MahApps" Version="5.3.2" />
    <PackageReference Include="MahApps.Metro" Version="2.4.11" />
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.3351.48" />
  </ItemGroup>

  <ItemGroup>
    <!-- SDK-style WPF projects do not auto-include *.md or Assets/**, so without these items the
         prompt templates and terminal assets are missing from bin\ at runtime. -->
    <Content Include="Prompt\**\*.md">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include="Assets\autoanswer.rules.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include="Assets\Terminal\**\*.*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Verify the exact WebView2 version resolves**

Run: `dotnet restore Workflow\Workflow.csproj`
Expected: success. If NU1102 reports that `1.0.3351.48` does not exist, run
`dotnet package search Microsoft.Web.WebView2 --exact-match --take 1` and pin the newest stable
version it reports, then re-run restore. Record the pinned version in the commit message.

- [ ] **Step 5: Create the test project**

`Workflow.Tests\Workflow.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!-- Analyzer rules written for production libraries misfire on test code. -->
    <!-- CA1707: test names use Method_Condition_Expectation. -->
    <!-- CA1822: [Fact] methods would all be forced static. -->
    <!-- CA2007: no synchronisation context to return to in a test host. -->
    <!-- CA1303: assertion messages and fixture literals are not localised. -->
    <!-- CA1861: inline constant arrays are the clearest form for table-driven data. -->
    <NoWarn>$(NoWarn);CA1707;CA1822;CA2007;CA1303;CA1861</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Workflow\Workflow.csproj" />
  </ItemGroup>

</Project>
```

`Workflow.Tests\PlaceholderTests.cs`:

```csharp
namespace Workflow.Tests;

public class PlaceholderTests
{
    [Fact]
    public void TestHostRuns()
    {
        Assert.True(true);
    }
}
```

Add `global using Xunit;` by creating `Workflow.Tests\GlobalUsings.cs`:

```csharp
global using Xunit;
```

- [ ] **Step 6: Add the test project to the solution**

Run: `dotnet sln Workflow.sln add Workflow.Tests\Workflow.Tests.csproj`

- [ ] **Step 7: Build and confirm the analyzer policy is actually active**

Run: `dotnet build Workflow.sln -c Debug /bl:build-task1.binlog`
Expected: **Build succeeded, 0 Warning(s), 0 Error(s)**.

If it fails, the failure is the point of this step — `.roslyn` is now in force. Read the binlog
(`dotnet-msbuild/skills/binlog-failure-analysis`). Fix real issues in code. Do **not** widen
`NoWarn` beyond the seven entries listed in Step 2 without amending the spec's §4.4 first.

> **Expect the rules in *Analyzer conformance* above to fire from Task 2 onwards.** They were
> reproduced against this exact configuration, so they are not hypothetical. The most common by
> far is `IDE0040` on interface members and `CS0103` on `Path`/`File`/`Directory` where
> `using System.IO;` is missing — both are consequences of settings this repository already had
> before this plan existed.

Sanity check that the import worked:

Run: `dotnet build Workflow\Workflow.csproj -getProperty:TreatWarningsAsErrors`
Expected output: `true`

- [ ] **Step 8: Run the tests**

Run: `dotnet test Workflow.sln`
Expected: 1 passed.

- [ ] **Step 8a: Prove the analyzer policy bites (guards against a silent no-op import)**

A `Directory.Build.props` that fails to import `.roslyn` produces a green build for the wrong
reason, and every later task would then be written against a policy that is not actually on.
Temporarily add to `Workflow.Tests\PlaceholderTests.cs`:

```csharp
public interface IPolicyProbe
{
    void Ping();
}
```

Run: `dotnet build Workflow.sln -c Debug`
Expected: **FAIL** with `error IDE0040`. If it builds, the import is not working — fix
`Directory.Build.props` before going further. Then delete the probe interface and re-run
Step 7 to confirm the build is green again.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "build: wire .roslyn via Directory.Build.props, restore package refs, add test project

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Domain models and `TaskPaths`

**Files:**
- Create: `Workflow\Models\WorkflowPhase.cs`, `Workflow\Models\PhaseDefinition.cs`, `Workflow\Models\PhaseCatalog.cs`, `Workflow\Models\TaskPaths.cs`, `Workflow\Models\WorkingDirectoryPath.cs`
- Test: `Workflow.Tests\TaskPathsTests.cs`, `Workflow.Tests\PhaseCatalogTests.cs`, `Workflow.Tests\WorkingDirectoryPathTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum WorkflowPhase { Specification = 0, Review = 1, ResolveReview = 2, Implementation = 3 }`
  - `enum PhaseStatus { Pending, Active, Completed }`
  - `enum CompletionRule { FilesExist, AnyContentChanged, Manual }`
  - `sealed record PhaseDefinition(WorkflowPhase Phase, string DisplayName, string Launcher, string PromptFile, CompletionRule Completion)`
  - `static class PhaseCatalog { static IReadOnlyList<PhaseDefinition> All { get; } ; static PhaseDefinition For(WorkflowPhase phase) }`
  - `sealed class TaskPaths` with `WorkingDirectory`, `TaskName`, `TaskDirectory`, `SpecAbsolute`, `PlanAbsolute`, `ReviewAbsolute`, `SpecRelative`, `PlanRelative`, `ReviewRelative` — all `string`.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\TaskPathsTests.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Tests;

public class TaskPathsTests
{
    // A drive root is a legal directory-picker result. Normalising it with
    // TrimEnd(Path.DirectorySeparatorChar) yields "C:", which Windows reads as the DRIVE-RELATIVE
    // current directory, so Path.Combine("C:", "my-task") is "C:my-task" - a different folder.
    [Fact]
    public void TaskDirectory_PreservesADriveRoot()
    {
        var paths = new TaskPaths(@"C:\", "my-task");

        Assert.Equal(@"C:\", paths.WorkingDirectory);
        Assert.Equal(@"C:\my-task", paths.TaskDirectory);
        Assert.Equal(@"C:\my-task\my-task_spec.md", paths.SpecAbsolute);
    }

    [Fact]
    public void TaskDirectory_StripsATrailingSeparatorFromANonRootDirectory()
    {
        var paths = new TaskPaths(@"C:\src\demo\", "my-task");

        Assert.Equal(@"C:\src\demo", paths.WorkingDirectory);
        Assert.Equal(@"C:\src\demo\my-task", paths.TaskDirectory);
    }

    [Fact]
    public void TaskDirectory_IsWorkingDirectoryPlusTaskName()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        Assert.Equal(@"C:\src\demo\my-task", paths.TaskDirectory);
    }

    [Fact]
    public void AbsolutePaths_UseTaskNameForBothFolderAndFile()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        Assert.Equal(@"C:\src\demo\my-task\my-task_spec.md", paths.SpecAbsolute);
        Assert.Equal(@"C:\src\demo\my-task\my-task_plan.md", paths.PlanAbsolute);
        Assert.Equal(@"C:\src\demo\my-task\my-task-review.md", paths.ReviewAbsolute);
    }

    [Fact]
    public void RelativePaths_UseForwardSlashesAndLeadingDot()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        Assert.Equal("./my-task/my-task_spec.md", paths.SpecRelative);
        Assert.Equal("./my-task/my-task_plan.md", paths.PlanRelative);
        Assert.Equal("./my-task/my-task-review.md", paths.ReviewRelative);
    }

    [Fact]
    public void WorkingDirectory_TrailingSeparatorIsStripped()
    {
        var paths = new TaskPaths(@"C:\src\demo\", "my-task");

        Assert.Equal(@"C:\src\demo", paths.WorkingDirectory);
        Assert.Equal(@"C:\src\demo\my-task", paths.TaskDirectory);
    }

    [Fact]
    public void Names_WithSpacesAndUmlauts_AreCarriedThroughVerbatim()
    {
        var paths = new TaskPaths(@"C:\src\demo", "Größe prüfen");

        Assert.Equal(@"C:\src\demo\Größe prüfen\Größe prüfen_spec.md", paths.SpecAbsolute);
        Assert.Equal("./Größe prüfen/Größe prüfen_spec.md", paths.SpecRelative);
    }

    [Fact]
    public void TaskName_IsTrimmed()
    {
        var paths = new TaskPaths(@"C:\src\demo", "  my-task  ");

        Assert.Equal("my-task", paths.TaskName);
    }
}
```

`Workflow.Tests\PhaseCatalogTests.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Tests;

public class PhaseCatalogTests
{
    [Fact]
    public void All_HasFourPhasesInPipelineOrder()
    {
        var all = PhaseCatalog.All;

        Assert.Equal(4, all.Count);
        Assert.Equal(WorkflowPhase.Specification, all[0].Phase);
        Assert.Equal(WorkflowPhase.Review, all[1].Phase);
        Assert.Equal(WorkflowPhase.ResolveReview, all[2].Phase);
        Assert.Equal(WorkflowPhase.Implementation, all[3].Phase);
    }

    [Theory]
    [InlineData(WorkflowPhase.Specification, "Spezifikation", "yo", "initial_prompt.md", CompletionRule.FilesExist)]
    [InlineData(WorkflowPhase.Review, "Review", "codex --yolo", "review_prompt.md", CompletionRule.FilesExist)]
    [InlineData(WorkflowPhase.ResolveReview, "Review umsetzen", "yo", "resolve_review_prompt.md", CompletionRule.AnyContentChanged)]
    [InlineData(WorkflowPhase.Implementation, "Implementierung", "yo", "implementation_prompt.md", CompletionRule.Manual)]
    public void For_ReturnsTheSpecifiedDefinition(
        WorkflowPhase phase, string displayName, string launcher, string promptFile, CompletionRule rule)
    {
        var definition = PhaseCatalog.For(phase);

        Assert.Equal(displayName, definition.DisplayName);
        Assert.Equal(launcher, definition.Launcher);
        Assert.Equal(promptFile, definition.PromptFile);
        Assert.Equal(rule, definition.Completion);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskPathsTests|FullyQualifiedName~PhaseCatalogTests"`
Expected: FAIL — `CS0246: The type or namespace name 'TaskPaths' could not be found`.

- [ ] **Step 3: Write the implementation**

`Workflow\Models\WorkflowPhase.cs`:

```csharp
namespace Workflow.Models;

/// <summary>One of the four stations of the workflow pipeline.</summary>
public enum WorkflowPhase
{
    /// <summary>Station 1 - Spezifikation.</summary>
    Specification = 0,

    /// <summary>Station 2 - Review.</summary>
    Review = 1,

    /// <summary>Station 3 - Review umsetzen.</summary>
    ResolveReview = 2,

    /// <summary>Station 4 - Implementierung.</summary>
    Implementation = 3,
}

/// <summary>Visual state of a single phase indicator.</summary>
public enum PhaseStatus
{
    /// <summary>Grey - the phase has not started.</summary>
    Pending,

    /// <summary>Yellow - the phase is running.</summary>
    Active,

    /// <summary>Green - the phase finished.</summary>
    Completed,
}

/// <summary>How the orchestrator decides that a phase is finished.</summary>
public enum CompletionRule
{
    /// <summary>Every watched path exists and is non-empty.</summary>
    FilesExist,

    /// <summary>At least one watched path differs from its baseline hash.</summary>
    AnyContentChanged,

    /// <summary>Only the user can end the phase.</summary>
    Manual,
}
```

`Workflow\Models\PhaseDefinition.cs`:

```csharp
namespace Workflow.Models;

/// <summary>Static description of one workflow phase.</summary>
/// <param name="Phase">The phase this definition describes.</param>
/// <param name="DisplayName">German label shown on the phase indicator.</param>
/// <param name="Launcher">Command typed into the shell to start the CLI.</param>
/// <param name="PromptFile">File name inside the Prompt directory.</param>
/// <param name="Completion">How the phase is detected as finished.</param>
public sealed record PhaseDefinition(
    WorkflowPhase Phase,
    string DisplayName,
    string Launcher,
    string PromptFile,
    CompletionRule Completion);
```

`Workflow\Models\PhaseCatalog.cs`:

```csharp
namespace Workflow.Models;

/// <summary>The four phase definitions, in pipeline order.</summary>
public static class PhaseCatalog
{
    /// <summary>All phases, ordered Specification -> Review -> ResolveReview -> Implementation.</summary>
    public static IReadOnlyList<PhaseDefinition> All { get; } =
    [
        new PhaseDefinition(
            WorkflowPhase.Specification, "Spezifikation", "yo", "initial_prompt.md", CompletionRule.FilesExist),
        new PhaseDefinition(
            WorkflowPhase.Review, "Review", "codex --yolo", "review_prompt.md", CompletionRule.FilesExist),
        new PhaseDefinition(
            WorkflowPhase.ResolveReview, "Review umsetzen", "yo", "resolve_review_prompt.md", CompletionRule.AnyContentChanged),
        new PhaseDefinition(
            WorkflowPhase.Implementation, "Implementierung", "yo", "implementation_prompt.md", CompletionRule.Manual),
    ];

    /// <summary>Looks up a single phase definition.</summary>
    /// <param name="phase">The phase to look up.</param>
    /// <returns>The matching definition.</returns>
    public static PhaseDefinition For(WorkflowPhase phase) => All[(int)phase];
}
```

`Workflow\Models\WorkingDirectoryPath.cs` — the single normalisation rule, shared by
`TaskPaths`, `TaskFolderService` and `SettingsService` so the three cannot drift apart:

```csharp
using System.IO;

namespace Workflow.Models;

/// <summary>Normalises a user-selected working-directory path.</summary>
public static class WorkingDirectoryPath
{
    /// <summary>
    /// Trims surrounding whitespace and a trailing directory separator, but never turns a drive
    /// root into a drive-relative path.
    /// </summary>
    /// <param name="directory">Raw path, typically straight from the folder picker.</param>
    /// <returns>The normalised path.</returns>
    /// <remarks>
    /// <c>TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)</c> is wrong here:
    /// it turns <c>C:\</c> into <c>C:</c>, which Windows resolves against the drive's current
    /// directory. <c>Path.Combine("C:", "task")</c> is then <c>C:task</c>, and the emitted
    /// <c>cd "C:"</c> does not change to the root. <c>Path.TrimEndingDirectorySeparator</c>
    /// leaves roots alone by design.
    /// </remarks>
    public static string Normalise(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return Path.TrimEndingDirectorySeparator(directory.Trim());
    }
}
```

> `using System.IO;` above is not optional: `UseWPF=true` drops `System.IO` from the
> implicit-using set, so `Path` would otherwise be `CS0103` (see *Analyzer conformance*).

`Workflow\Models\TaskPaths.cs`:

```csharp
using System.IO;

namespace Workflow.Models;

/// <summary>
/// Every path derived from a working directory and a task name. This is the single source of
/// truth for artefact locations; nothing else may compose these paths by hand.
/// </summary>
public sealed class TaskPaths
{
    /// <summary>Creates the path set.</summary>
    /// <param name="workingDirectory">Directory the terminal changes into. Normalised by <see cref="WorkingDirectoryPath.Normalise"/>.</param>
    /// <param name="taskName">Task name (Taskbezeichnung). Trimmed.</param>
    public TaskPaths(string workingDirectory, string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        WorkingDirectory = WorkingDirectoryPath.Normalise(workingDirectory);
        TaskName = taskName.Trim();

        TaskDirectory = Path.Combine(WorkingDirectory, TaskName);
        SpecAbsolute = Path.Combine(TaskDirectory, $"{TaskName}_spec.md");
        PlanAbsolute = Path.Combine(TaskDirectory, $"{TaskName}_plan.md");
        ReviewAbsolute = Path.Combine(TaskDirectory, $"{TaskName}-review.md");

        SpecRelative = $"./{TaskName}/{TaskName}_spec.md";
        PlanRelative = $"./{TaskName}/{TaskName}_plan.md";
        ReviewRelative = $"./{TaskName}/{TaskName}-review.md";
    }

    /// <summary>Directory the terminal changes into; also the value of the {AppDirectory} token.</summary>
    public string WorkingDirectory { get; }

    /// <summary>The task name (Taskbezeichnung); also the tab header and the folder name.</summary>
    public string TaskName { get; }

    /// <summary>Absolute path of the task folder.</summary>
    public string TaskDirectory { get; }

    /// <summary>Absolute path of the specification artefact.</summary>
    public string SpecAbsolute { get; }

    /// <summary>Absolute path of the implementation-plan artefact.</summary>
    public string PlanAbsolute { get; }

    /// <summary>Absolute path of the review artefact.</summary>
    public string ReviewAbsolute { get; }

    /// <summary>Value substituted for the {spec_path} token.</summary>
    public string SpecRelative { get; }

    /// <summary>Value substituted for the {plan_path} token.</summary>
    public string PlanRelative { get; }

    /// <summary>Value substituted for the {review_path} token.</summary>
    public string ReviewRelative { get; }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskPathsTests|FullyQualifiedName~PhaseCatalogTests"`
Expected: PASS — 12 passed.

- [ ] **Step 5: Commit**

```bash
git add Workflow/Models Workflow.Tests/TaskPathsTests.cs Workflow.Tests/PhaseCatalogTests.cs
git commit -m "feat(models): add phase enums, PhaseCatalog and TaskPaths

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `TaskFolderService`

**Files:**
- Create: `Workflow\Services\ITaskFolderService.cs`, `Workflow\Services\TaskFolderService.cs`, `Workflow\Models\TaskNameValidation.cs`
- Test: `Workflow.Tests\TaskFolderServiceTests.cs`

**Interfaces:**
- Consumes: `TaskPaths` (Task 2).
- Produces:
  - `sealed record TaskNameValidation(bool IsValid, string? ErrorMessage)` with `static TaskNameValidation Ok` and `static TaskNameValidation Error(string message)`.
  - `interface ITaskFolderService` with
    `TaskNameValidation Validate(string? taskName, string? workingDirectory)`,
    `void EnsureCreated(TaskPaths paths)`,
    `bool DirectoryAlreadyExisted(TaskPaths paths)`,
    `void Rename(string workingDirectory, string oldName, string newName)`.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\TaskFolderServiceTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskFolderServiceTests : IDisposable
{
    private readonly string _root;
    private readonly TaskFolderService _service = new();

    public TaskFolderServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Regression: a drive root must stay a root. WorkingDirectoryPath.Normalise keeps "C:\";
    // a plain TrimEnd would make the combined path "C:name" and mis-measure the length budget.
    [Fact]
    public void Validate_AcceptsANameUnderADriveRoot()
    {
        var result = _service.Validate("wf-root-test", @"C:\");

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Rename_ComposesPathsUnderADriveRootWithoutGoingDriveRelative()
    {
        // Exercised through TaskPaths so no folder is created on C:\ by the test run.
        var paths = new TaskPaths(@"C:\", "wf-root-test");

        Assert.Equal(@"C:\wf-root-test", paths.TaskDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyName(string? name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void Validate_RejectsInvalidFileNameCharacters(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("task.")]
    [InlineData("task ")]
    public void Validate_RejectsTrailingDotOrSpace(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("lpt3.txt")]
    public void Validate_RejectsReservedDeviceNames(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Fact]
    public void Validate_RejectsWhenCombinedPathExceeds240Characters()
    {
        var name = new string('x', 240);

        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("my-task")]
    [InlineData("Größe prüfen")]
    [InlineData("task 1")]
    [InlineData("COM0")]
    [InlineData("CONSOLE")]
    public void Validate_AcceptsUsableNames(string name)
    {
        var result = _service.Validate(name, _root);

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsMissingWorkingDirectory()
    {
        Assert.False(_service.Validate("my-task", null).IsValid);
        Assert.False(_service.Validate("my-task", Path.Combine(_root, "does-not-exist")).IsValid);
    }

    [Fact]
    public void EnsureCreated_CreatesTheFolder()
    {
        var paths = new TaskPaths(_root, "my-task");

        _service.EnsureCreated(paths);

        Assert.True(Directory.Exists(paths.TaskDirectory));
    }

    [Fact]
    public void DirectoryAlreadyExisted_IsTrueOnlyWhenTheFolderIsThereBeforehand()
    {
        var paths = new TaskPaths(_root, "my-task");

        Assert.False(_service.DirectoryAlreadyExisted(paths));

        _service.EnsureCreated(paths);

        Assert.True(_service.DirectoryAlreadyExisted(paths));
    }

    [Fact]
    public void EnsureCreated_IsIdempotentAndKeepsExistingContent()
    {
        var paths = new TaskPaths(_root, "my-task");
        _service.EnsureCreated(paths);
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "keep.txt"), "keep");

        _service.EnsureCreated(paths);

        Assert.True(File.Exists(Path.Combine(paths.TaskDirectory, "keep.txt")));
    }

    [Fact]
    public void Rename_MovesTheFolder()
    {
        _service.EnsureCreated(new TaskPaths(_root, "old"));

        _service.Rename(_root, "old", "new");

        Assert.False(Directory.Exists(Path.Combine(_root, "old")));
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Fact]
    public void Rename_HandlesCaseOnlyChange()
    {
        _service.EnsureCreated(new TaskPaths(_root, "task"));

        _service.Rename(_root, "task", "Task");

        var actual = Path.GetFileName(Directory.GetDirectories(_root).Single());
        Assert.Equal("Task", actual);
    }

    [Fact]
    public void Rename_IsANoOpWhenTheSourceDoesNotExist()
    {
        _service.Rename(_root, "missing", "other");

        Assert.Empty(Directory.GetDirectories(_root));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskFolderServiceTests"`
Expected: FAIL — `CS0246: The type or namespace name 'TaskFolderService' could not be found`.

- [ ] **Step 3: Write the implementation**

`Workflow\Models\TaskNameValidation.cs`:

```csharp
namespace Workflow.Models;

/// <summary>Result of validating a task name.</summary>
/// <param name="IsValid">True when the name can be used as a folder name.</param>
/// <param name="ErrorMessage">German message to show the user, or null when valid.</param>
public sealed record TaskNameValidation(bool IsValid, string? ErrorMessage)
{
    /// <summary>The successful result.</summary>
    public static TaskNameValidation Ok { get; } = new(true, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">German message to show the user.</param>
    /// <returns>A failed validation result.</returns>
    public static TaskNameValidation Error(string message) => new(false, message);
}
```

`Workflow\Services\ITaskFolderService.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>Creates, renames and validates the folder that holds a task's artefacts.</summary>
public interface ITaskFolderService
{
    /// <summary>Checks whether a task name can be used as a folder name.</summary>
    /// <param name="taskName">The proposed task name.</param>
    /// <param name="workingDirectory">The selected working directory.</param>
    /// <returns>A validation result carrying a German message on failure.</returns>
    public TaskNameValidation Validate(string? taskName, string? workingDirectory);

    /// <summary>Creates the task folder if it does not exist. Existing content is preserved.</summary>
    /// <param name="paths">The task's path set.</param>
    public void EnsureCreated(TaskPaths paths);

    /// <summary>Reports whether the task folder is already present on disk.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>True when the folder exists.</returns>
    public bool DirectoryAlreadyExisted(TaskPaths paths);

    /// <summary>Renames the task folder. A no-op when the source folder is absent.</summary>
    /// <param name="workingDirectory">Parent directory of both names.</param>
    /// <param name="oldName">Current folder name.</param>
    /// <param name="newName">Desired folder name.</param>
    public void Rename(string workingDirectory, string oldName, string newName);
}
```

`Workflow\Services\TaskFolderService.cs`:

```csharp
using System.IO;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskFolderService" />
public sealed class TaskFolderService : ITaskFolderService
{
    private const int MaxCombinedPathLength = 240;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <inheritdoc />
    public TaskNameValidation Validate(string? taskName, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return TaskNameValidation.Error("Bitte ein gültiges Arbeitsverzeichnis auswählen.");
        }

        if (string.IsNullOrWhiteSpace(taskName))
        {
            return TaskNameValidation.Error("Die Taskbezeichnung darf nicht leer sein.");
        }

        var trimmed = taskName.Trim();

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return TaskNameValidation.Error(@"Die Taskbezeichnung darf keines der Zeichen \ / : * ? "" < > | enthalten.");
        }

        // Windows silently strips a trailing dot or space, so the folder would not round-trip.
        if (taskName.EndsWith('.') || taskName.EndsWith(' '))
        {
            return TaskNameValidation.Error("Die Taskbezeichnung darf nicht mit einem Punkt oder Leerzeichen enden.");
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(trimmed);
        if (ReservedNames.Contains(withoutExtension))
        {
            return TaskNameValidation.Error($"'{trimmed}' ist ein reservierter Windows-Gerätename.");
        }

        // Root-aware: WorkingDirectoryPath.Normalise keeps "C:\" intact. A plain TrimEnd would
        // produce "C:", making the combined path drive-relative and the length check meaningless.
        var combined = Path.Combine(WorkingDirectoryPath.Normalise(workingDirectory), trimmed);

        if (combined.Length > MaxCombinedPathLength)
        {
            return TaskNameValidation.Error(
                $"Der Pfad wird zu lang ({combined.Length} Zeichen, erlaubt sind {MaxCombinedPathLength}).");
        }

        return TaskNameValidation.Ok;
    }

    /// <inheritdoc />
    public void EnsureCreated(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(paths.TaskDirectory);
    }

    /// <inheritdoc />
    public bool DirectoryAlreadyExisted(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Directory.Exists(paths.TaskDirectory);
    }

    /// <inheritdoc />
    public void Rename(string workingDirectory, string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var root = WorkingDirectoryPath.Normalise(workingDirectory);
        var source = Path.Combine(root, oldName.Trim());
        var target = Path.Combine(root, newName.Trim());

        if (!Directory.Exists(source) || string.Equals(source, target, StringComparison.Ordinal))
        {
            return;
        }

        // Windows treats a case-only rename as a no-op, so route it through a temporary name.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.Move(source, temporary);
            Directory.Move(temporary, target);
            return;
        }

        Directory.Move(source, target);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskFolderServiceTests"`
Expected: PASS — 30 passed.

- [ ] **Step 5: Commit**

```bash
git add Workflow/Services Workflow/Models/TaskNameValidation.cs Workflow.Tests/TaskFolderServiceTests.cs
git commit -m "feat(services): add TaskFolderService with Windows name validation

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `PromptTemplateService` and repairing the three defective templates

**Files:**
- Create: `Workflow\Services\IPromptTemplateService.cs`, `Workflow\Services\PromptTemplateService.cs`, `Workflow\Services\PromptTemplateException.cs`
- Modify: `Workflow\Prompt\review_prompt.md`, `Workflow\Prompt\resolve_review_prompt.md`, `Workflow\Prompt\implementation_prompt.md`
- Test: `Workflow.Tests\PromptTemplateServiceTests.cs`

**Interfaces:**
- Consumes: `TaskPaths` (Task 2), `PhaseCatalog` (Task 2).
- Produces:
  - `sealed class PromptTemplateException : Exception`
  - `interface IPromptTemplateService` with
    `string Render(string fileName, IReadOnlyDictionary<string, string> variables)` and
    `IReadOnlyList<string> ValidateAll()` (empty list means every shipped template is usable).
  - `static class PromptVariables` exposing `IReadOnlyCollection<string> KnownNames` and
    `static Dictionary<string, string> For(TaskPaths paths, string taskDescription)`.

> **RE-VERIFIED 2026-09-12 against the bytes on disk.** This task was originally written from an
> earlier reading of the prompt folder. Two of the three defects it set out to fix have since been
> repaired upstream, and a fourth was found. Sizes now: `initial_prompt.md` 1819 B,
> `review_prompt.md` 312 B, `resolve_review_prompt.md` **1425 B (not 0)**,
> `implementation_prompt.md` 3383 B. Apply **only the delta below** — a wholesale overwrite would
> throw away content that is newer and better than what this plan originally proposed. Spec §9.3
> carries the same list.
>
> **Before editing, re-read all four files.** If what you find differs from the description here,
> the files win and this task is corrected, not the files.

- [ ] **Step 1: `review_prompt.md` — one cosmetic word**

The original defect (the output path built from `{taskbeschreibung}`) is **already fixed on
disk**. The file now reads:

```
/review produce a {review_path} as delta critique for following plans:

- Specification: {spec_path}
- Implementation plan: {plan_path}

Crucially, you shouldn't create another handoff document that attempts to summarize everything. 
Please do not output the whole review. writing the file is sufficient.
```

The path already comes from the single source of truth (`TaskPaths`), which is what the spec
required. The only edit is to drop the stray article on line 1 so the sentence reads correctly
once `{review_path}` is substituted:

- from: `/review produce a {review_path} as delta critique for following plans:`
- to:   `/review produce {review_path} as delta critique for following plans:`

**Change nothing else in this file.** The trailing two lines are deliberate.

- [ ] **Step 2: `resolve_review_prompt.md` — append one paragraph**

The file is **not** 0 bytes. It contains a working phase-3 prompt that opens with the three
artefact paths and instructs the resolver to verify every finding against the actual repository
and classify it valid / partially valid / invalid. **Keep all of it.** That behaviour is better
than the replacement this plan originally proposed, and overwriting it would be a regression.

Append exactly this, immediately before the closing `Please do not output ...` line:

```markdown
Both {spec_path} and {plan_path} must be written to, even if only to append a short
`## Review resolution` note recording that no change was required.
```

The paragraph is load-bearing, not politeness: phase 3 completes on a **content change** to the
spec or the plan (Task 6). As the file stands it ends by asking for a *report* of what was
rejected — so a run that rejects every finding writes nothing, and the phase never advances.
*Phase abschliessen* (Task 13) is the backstop, but the guarantee belongs in the prompt.

- [ ] **Step 3: `implementation_prompt.md` — two edits**

**3a — remove the stray underscore (still present).** Line 8 reads `{plan_path}_`, which renders
as a literal `_` in the prompt. The block

```
Implementierungsplan:
{plan_path}_
```

must become

```
Implementierungsplan:
{plan_path}
```

**3b — remove the live QDocImport instruction (new; this is what makes the N/A decision real).**
Under `Completion requires:` the file still contains:

```
* appropriate eval gates has been added to qdocimporter/eval
```

Specification §14 and §3.2 declare QDocImport eval gates **out of scope**, but this file is
rendered and pasted into the phase-4 agent — so as shipped, the product orders the implementer to
modify a separate, regulated .NET Framework 4.8 codebase and forbids reporting completion until
it has. A declaration in the spec that a shipped prompt contradicts is not a decision, it is a
comment.

Replace that one bullet with the gate this application actually has:

```
* the acceptance gate `Workflow\verify.ps1` exits 0
```

Leave every other bullet in the list untouched.


- [ ] **Step 4: Confirm only known tokens remain in all four templates**

Run:

```bash
cd "C:/Users/Marco/Documents/repo/Workflow/Workflow/Prompt"
grep -oh "{[A-Za-z_][A-Za-z_]*}" *.md | sort -u
```

Expected output — exactly these six lines and nothing else:

```
{AppDirectory}
{plan_path}
{review_path}
{spec_path}
{taskbeschreibung}
{taskbezeichnung}
```

- [ ] **Step 5: Write the failing tests**

`Workflow.Tests\PromptTemplateServiceTests.cs`:

```csharp
using System.IO;
using System.Text;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class PromptTemplateServiceTests : IDisposable
{
    private readonly string _dir;

    public PromptTemplateServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-prompts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void WriteTemplate(string name, string content, Encoding? encoding = null)
    {
        File.WriteAllText(
            Path.Combine(_dir, name),
            content,
            encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Dictionary<string, string> Variables() => new(StringComparer.Ordinal)
    {
        ["taskbezeichnung"] = "my-task",
        ["taskbeschreibung"] = "Beschreibung",
        ["AppDirectory"] = @"C:\src\demo",
        ["spec_path"] = "./my-task/my-task_spec.md",
        ["plan_path"] = "./my-task/my-task_plan.md",
        ["review_path"] = "./my-task/my-task-review.md",
    };

    [Fact]
    public void Render_SubstitutesEveryKnownToken()
    {
        WriteTemplate("t.md", "A {taskbezeichnung} B {taskbeschreibung} C {AppDirectory} D {spec_path} E {plan_path} F {review_path}");
        var service = new PromptTemplateService(_dir);

        var result = service.Render("t.md", Variables());

        Assert.Equal(
            @"A my-task B Beschreibung C C:\src\demo D ./my-task/my-task_spec.md E ./my-task/my-task_plan.md F ./my-task/my-task-review.md",
            result);
    }

    [Fact]
    public void Render_SubstitutesRepeatedTokens()
    {
        WriteTemplate("t.md", "{spec_path} and again {spec_path}");
        var service = new PromptTemplateService(_dir);

        Assert.Equal(
            "./my-task/my-task_spec.md and again ./my-task/my-task_spec.md",
            service.Render("t.md", Variables()));
    }

    [Fact]
    public void Render_PreservesMultiLineDescriptions()
    {
        WriteTemplate("t.md", "{taskbeschreibung}");
        var service = new PromptTemplateService(_dir);
        var variables = Variables();
        variables["taskbeschreibung"] = "Zeile eins\nZeile zwei";

        Assert.Equal("Zeile eins\nZeile zwei", service.Render("t.md", variables));
    }

    [Fact]
    public void Render_ThrowsAndNamesTheUnresolvedToken()
    {
        WriteTemplate("t.md", "hello {unknown_token} world");
        var service = new PromptTemplateService(_dir);

        var ex = Assert.Throws<PromptTemplateException>(() => service.Render("t.md", Variables()));

        Assert.Contains("unknown_token", ex.Message, StringComparison.Ordinal);
        Assert.Contains("t.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ThrowsOnEmptyTemplate()
    {
        WriteTemplate("t.md", "   \r\n  ");
        var service = new PromptTemplateService(_dir);

        Assert.Throws<PromptTemplateException>(() => service.Render("t.md", Variables()));
    }

    [Fact]
    public void Render_ThrowsOnMissingFile()
    {
        var service = new PromptTemplateService(_dir);

        Assert.Throws<PromptTemplateException>(() => service.Render("nope.md", Variables()));
    }

    [Fact]
    public void Render_ToleratesAByteOrderMark()
    {
        WriteTemplate("t.md", "X {spec_path}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var service = new PromptTemplateService(_dir);

        var result = service.Render("t.md", Variables());

        Assert.Equal("X ./my-task/my-task_spec.md", result);
        Assert.DoesNotContain('\uFEFF', result);
    }

    [Fact]
    public void ValidateAll_ReportsEmptyAndUnknownTokenTemplates()
    {
        WriteTemplate("initial_prompt.md", "ok {spec_path}");
        WriteTemplate("review_prompt.md", "");
        WriteTemplate("resolve_review_prompt.md", "ok {plan_path}");
        WriteTemplate("implementation_prompt.md", "bad {nope}");
        var service = new PromptTemplateService(_dir);

        var errors = service.ValidateAll();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("review_prompt.md", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAll_AcceptsTheShippedTemplates()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Prompt");
        var service = new PromptTemplateService(shipped);

        Assert.Empty(service.ValidateAll());
    }

    // --- Assertions over the SHIPPED templates (acceptance criterion A8). -------------------
    // Steps 1-3 of this task are one-off edits; without these, a regression in either direction
    // is only discovered by a stalled pipeline at run time.

    private static string Shipped(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompt", name));

    [Fact]
    public void ShippedImplementationPrompt_HasNoStrayUnderscoreAfterThePlanToken()
    {
        Assert.DoesNotContain("{plan_path}_", Shipped("implementation_prompt.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedImplementationPrompt_DoesNotOrderWorkInQDocImport()
    {
        // Spec section 14 / 3.2: QDocImport eval gates are out of scope. The prompt is pasted
        // into the phase-4 agent, so the instruction has to be gone from the file, not merely
        // declared Not Applicable in the specification.
        Assert.DoesNotContain("qdocimport", Shipped("implementation_prompt.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedImplementationPrompt_PointsAtTheLocalAcceptanceGate()
    {
        Assert.Contains("verify.ps1", Shipped("implementation_prompt.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedResolveReviewPrompt_GuaranteesADetectableWrite()
    {
        // Phase 3 completes on a content change; a resolver that rejects every finding must
        // still write something or the phase never advances.
        var text = Shipped("resolve_review_prompt.md");

        Assert.NotEmpty(text.Trim());
        Assert.Contains("## Review resolution", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedReviewPrompt_BuildsItsOutputPathFromTheReviewPathToken()
    {
        var text = Shipped("review_prompt.md");

        Assert.Contains("{review_path}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{taskbeschreibung}-review", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptVariables_ForBuildsTheCompleteDictionary()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        var variables = PromptVariables.For(paths, "beschreibung");

        Assert.Equal(PromptVariables.KnownNames.Count, variables.Count);
        Assert.Equal("my-task", variables["taskbezeichnung"]);
        Assert.Equal("beschreibung", variables["taskbeschreibung"]);
        Assert.Equal(@"C:\src\demo", variables["AppDirectory"]);
        Assert.Equal("./my-task/my-task-review.md", variables["review_path"]);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~PromptTemplateServiceTests"`
Expected: FAIL — `CS0246: The type or namespace name 'PromptTemplateService' could not be found`.

- [ ] **Step 7: Write the implementation**

`Workflow\Services\PromptTemplateException.cs`:

```csharp
namespace Workflow.Services;

/// <summary>Raised when a prompt template is missing, empty, or contains an unknown token.</summary>
public sealed class PromptTemplateException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PromptTemplateException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Description of the problem, naming the file.</param>
    public PromptTemplateException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">Description of the problem, naming the file.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PromptTemplateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`Workflow\Services\IPromptTemplateService.cs`:

```csharp
namespace Workflow.Services;

/// <summary>Loads prompt templates and substitutes their variables.</summary>
public interface IPromptTemplateService
{
    /// <summary>Renders one template.</summary>
    /// <param name="fileName">File name inside the prompt directory, for example "initial_prompt.md".</param>
    /// <param name="variables">Values keyed by token name without braces.</param>
    /// <returns>The rendered prompt text.</returns>
    /// <exception cref="PromptTemplateException">
    /// The file is missing or empty, or a token remained unresolved.
    /// </exception>
    public string Render(string fileName, IReadOnlyDictionary<string, string> variables);

    /// <summary>Checks every phase template at application start.</summary>
    /// <returns>German error messages; an empty list means all templates are usable.</returns>
    public IReadOnlyList<string> ValidateAll();
}
```

`Workflow\Services\PromptTemplateService.cs`:

```csharp
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Workflow.Models;

namespace Workflow.Services;

/// <summary>The complete set of tokens a prompt template may use.</summary>
public static class PromptVariables
{
    /// <summary>Token names, without braces.</summary>
    public static IReadOnlyCollection<string> KnownNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "taskbezeichnung",
        "taskbeschreibung",
        "AppDirectory",
        "spec_path",
        "plan_path",
        "review_path",
    };

    /// <summary>Builds the substitution dictionary for one task.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <returns>A dictionary covering every known token.</returns>
    public static Dictionary<string, string> For(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["taskbezeichnung"] = paths.TaskName,
            ["taskbeschreibung"] = taskDescription ?? string.Empty,
            ["AppDirectory"] = paths.WorkingDirectory,
            ["spec_path"] = paths.SpecRelative,
            ["plan_path"] = paths.PlanRelative,
            ["review_path"] = paths.ReviewRelative,
        };
    }
}

/// <inheritdoc cref="IPromptTemplateService" />
public sealed partial class PromptTemplateService : IPromptTemplateService
{
    private readonly string _promptDirectory;

    /// <summary>Creates the service.</summary>
    /// <param name="promptDirectory">Directory holding the *.md templates.</param>
    public PromptTemplateService(string promptDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptDirectory);

        _promptDirectory = promptDirectory;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <inheritdoc />
    public string Render(string fileName, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(variables);

        var text = ReadTemplate(fileName);

        var unresolved = new List<string>();
        var rendered = TokenRegex().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (variables.TryGetValue(name, out var value))
            {
                return value;
            }

            unresolved.Add(name);
            return match.Value;
        });

        if (unresolved.Count > 0)
        {
            var names = string.Join(", ", unresolved.Distinct(StringComparer.Ordinal).Select(n => "{" + n + "}"));
            throw new PromptTemplateException(
                $"Die Prompt-Datei '{fileName}' enthält unbekannte Platzhalter: {names}.");
        }

        return rendered;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ValidateAll()
    {
        var errors = new List<string>();

        foreach (var definition in PhaseCatalog.All)
        {
            try
            {
                var text = ReadTemplate(definition.PromptFile);

                var unknown = TokenRegex()
                    .Matches(text)
                    .Select(m => m.Groups["name"].Value)
                    .Where(n => !PromptVariables.KnownNames.Contains(n))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (unknown.Count > 0)
                {
                    var names = string.Join(", ", unknown.Select(n => "{" + n + "}"));
                    errors.Add($"Die Prompt-Datei '{definition.PromptFile}' enthält unbekannte Platzhalter: {names}.");
                }
            }
            catch (PromptTemplateException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors;
    }

    private string ReadTemplate(string fileName)
    {
        var path = Path.Combine(_promptDirectory, fileName);

        if (!File.Exists(path))
        {
            throw new PromptTemplateException($"Die Prompt-Datei '{fileName}' wurde nicht gefunden: {path}");
        }

        string text;
        try
        {
            // detectEncodingFromByteOrderMarks strips a UTF-8 BOM instead of leaking U+FEFF into the prompt.
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            throw new PromptTemplateException($"Die Prompt-Datei '{fileName}' konnte nicht gelesen werden.", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new PromptTemplateException($"Die Prompt-Datei '{fileName}' ist leer.");
        }

        return text;
    }
}
```

- [ ] **Step 8: Make the shipped templates visible to the test host**

`ValidateAll_AcceptsTheShippedTemplates` reads `AppContext.BaseDirectory\Prompt`, which is the
*test* output directory. Add this `ItemGroup` to `Workflow.Tests\Workflow.Tests.csproj`:

```xml
  <ItemGroup>
    <Content Include="..\Workflow\Prompt\**\*.md" LinkBase="Prompt">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~PromptTemplateServiceTests"`
Expected: PASS — 15 passed. The `Shipped*` tests and `ValidateAll_AcceptsTheShippedTemplates` passing prove Steps 1–3
were applied correctly.

- [ ] **Step 10: Commit**

```
git add -A
git commit -m "feat(prompts): strict template renderer; repair three defective templates"
```

Commit message body to include:

```
resolve_review_prompt.md was empty, implementation_prompt.md had a stray {plan_path}_,
and review_prompt.md built its output path from {taskbeschreibung} instead of the
Bezeichnung. Paths now come from TaskPaths via the {review_path} token.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

---

## Task 5: Auto-answer rule engine

**Files:**
- Create: `Workflow\Models\AutoAnswerRule.cs`, `Workflow\Services\EscapeDecoder.cs`, `Workflow\Services\IAutoAnswerService.cs`, `Workflow\Services\AutoAnswerService.cs`, `Workflow\Assets\autoanswer.rules.json`
- Test: `Workflow.Tests\EscapeDecoderTests.cs`, `Workflow.Tests\AutoAnswerServiceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed record AutoAnswerRule(string Id, string Pattern, string Send, string Description)`
  - `sealed record AutoAnswerRuleSet(int Version, int QuietPeriodMs, int SettleTimeoutMs, int MaxAnswersPerPhase, IReadOnlyList<AutoAnswerRule> Rules)`
  - `static class EscapeDecoder` with `static string Decode(string raw)`
  - `interface IAutoAnswerService` with `AutoAnswerRuleSet RuleSet { get; }` and
    `AutoAnswerRule? Match(string screenText, IReadOnlySet<string> alreadyFiredRuleIds)`

> **Why this is data and not code.** `yo` expands to `claude --dangerously-skip-permissions`,
> whose bypass warning offers `1. No, exit` / `2. Yes, I accept` with **"No" preselected**. A
> naive "send Enter" answers *No* and the pipeline dies silently. Upstream wording changes
> without notice, so the rules live in JSON that a user can edit in `%APPDATA%` without a rebuild.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\EscapeDecoderTests.cs`:

```csharp
using Workflow.Services;

namespace Workflow.Tests;

public class EscapeDecoderTests
{
    [Theory]
    [InlineData(@"2\r", "2\r")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"\e[B", "\u001b[B")]
    [InlineData(@"back\\slash", @"back\slash")]
    [InlineData(@"\u0041", "A")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Decode_HandlesSupportedEscapes(string raw, string expected)
    {
        Assert.Equal(expected, EscapeDecoder.Decode(raw));
    }

    [Fact]
    public void Decode_LeavesAnUnknownEscapeIntact()
    {
        Assert.Equal(@"\q", EscapeDecoder.Decode(@"\q"));
    }

    [Fact]
    public void Decode_LeavesATrailingBackslashIntact()
    {
        Assert.Equal(@"abc\", EscapeDecoder.Decode(@"abc\"));
    }

    [Fact]
    public void Decode_LeavesAMalformedUnicodeEscapeIntact()
    {
        Assert.Equal(@"\u00", EscapeDecoder.Decode(@"\u00"));
    }
}
```

`Workflow.Tests\AutoAnswerServiceTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class AutoAnswerServiceTests : IDisposable
{
    private readonly string _dir;

    public AutoAnswerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteRules(string json)
    {
        var path = Path.Combine(_dir, "autoanswer.rules.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TwoRules = """
    {
      "version": 1,
      "quietPeriodMs": 1500,
      "settleTimeoutMs": 60000,
      "maxAnswersPerPhase": 5,
      "rules": [
        { "id": "first",  "pattern": "(?is)alpha", "send": "1\\r", "description": "a" },
        { "id": "second", "pattern": "(?is)beta",  "send": "2\\r", "description": "b" }
      ]
    }
    """;

    [Fact]
    public void RuleSet_IsLoadedFromJson()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Equal(1, service.RuleSet.Version);
        Assert.Equal(1500, service.RuleSet.QuietPeriodMs);
        Assert.Equal(60000, service.RuleSet.SettleTimeoutMs);
        Assert.Equal(5, service.RuleSet.MaxAnswersPerPhase);
        Assert.Equal(2, service.RuleSet.Rules.Count);
    }

    [Fact]
    public void Match_ReturnsTheFirstMatchingRuleInFileOrder()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        var rule = service.Match("... alpha and beta ...", new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("first", rule!.Id);
    }

    [Fact]
    public void Match_SkipsRulesThatAlreadyFired()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        var rule = service.Match("... alpha and beta ...", new HashSet<string>(StringComparer.Ordinal) { "first" });

        Assert.NotNull(rule);
        Assert.Equal("second", rule!.Id);
    }

    [Fact]
    public void Match_ReturnsNullWhenNothingMatches()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Null(service.Match("gamma", new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Match_ReturnsNullForEmptyScreenText()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Null(service.Match(string.Empty, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Match_IgnoresARuleWithAMalformedRegexInsteadOfThrowing()
    {
        var json = """
        {
          "version": 1, "quietPeriodMs": 1, "settleTimeoutMs": 1, "maxAnswersPerPhase": 1,
          "rules": [
            { "id": "broken", "pattern": "([unclosed", "send": "x", "description": "" },
            { "id": "good",   "pattern": "hello",     "send": "y", "description": "" }
          ]
        }
        """;
        var service = new AutoAnswerService(WriteRules(json), overridePath: null);

        var rule = service.Match("hello", new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("good", rule!.Id);
    }

    [Fact]
    public void OverrideFile_ReplacesTheShippedRulesWholesale()
    {
        var shipped = WriteRules(TwoRules);
        var overridePath = Path.Combine(_dir, "override.json");
        File.WriteAllText(overridePath, """
        {
          "version": 2, "quietPeriodMs": 100, "settleTimeoutMs": 200, "maxAnswersPerPhase": 9,
          "rules": [ { "id": "only", "pattern": "zeta", "send": "z", "description": "" } ]
        }
        """);

        var service = new AutoAnswerService(shipped, overridePath);

        Assert.Equal(2, service.RuleSet.Version);
        Assert.Single(service.RuleSet.Rules);
        Assert.Equal("only", service.RuleSet.Rules[0].Id);
    }

    [Fact]
    public void Send_IsEscapeDecodedWhenTheRuleIsRead()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Equal("1\r", service.RuleSet.Rules[0].Send);
    }

    [Fact]
    public void ShippedRules_AnswerTheClaudeBypassWarningWithTwoNotEnter()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var screen = "WARNING: Claude Code running in Bypass Permissions mode\n  1. No, exit\n> 2. Yes, I accept";
        var rule = service.Match(screen, new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("claude-bypass-permissions", rule!.Id);
        Assert.Equal("2\r", rule.Send);
    }

    [Fact]
    public void ShippedRules_AnswerTheTrustFolderDialogWithEnter()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var rule = service.Match("Do you trust the files in this folder?", new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("claude-trust-folder", rule!.Id);
        Assert.Equal("\r", rule.Send);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~EscapeDecoderTests|FullyQualifiedName~AutoAnswerServiceTests"`
Expected: FAIL — `CS0246: The type or namespace name 'EscapeDecoder' could not be found`.

- [ ] **Step 3: Create `Workflow\Assets\autoanswer.rules.json`**

```json
{
  "version": 1,
  "quietPeriodMs": 1500,
  "settleTimeoutMs": 60000,
  "maxAnswersPerPhase": 5,
  "rules": [
    {
      "id": "claude-bypass-permissions",
      "pattern": "(?is)bypass permissions mode",
      "send": "2\\r",
      "description": "claude --dangerously-skip-permissions warning. Option 2 is 'Yes, I accept'. The default selection is 'No, exit', so sending Enter alone would abort the run."
    },
    {
      "id": "claude-trust-folder",
      "pattern": "(?is)do you trust the files in this folder",
      "send": "\\r",
      "description": "Trust dialog. Option 1 'Yes, proceed' is preselected, so Enter is correct."
    },
    {
      "id": "codex-yolo-warning",
      "pattern": "(?is)(--yolo|full auto|auto-approve).{0,200}(continue|proceed|accept)",
      "send": "\\r",
      "description": "codex --yolo confirmation. The affirmative option is preselected."
    },
    {
      "id": "generic-yes-no",
      "pattern": "(?is)\\(y/n\\)\\s*$",
      "send": "y\\r",
      "description": "Plain readline-style yes/no fallback."
    }
  ]
}
```

- [ ] **Step 4: Write the implementation**

`Workflow\Models\AutoAnswerRule.cs`:

```csharp
namespace Workflow.Models;

/// <summary>One screen-text pattern and the key sequence to send when it matches.</summary>
/// <param name="Id">Stable identifier; a rule fires at most once per phase.</param>
/// <param name="Pattern">.NET regular expression matched against the rendered screen text.</param>
/// <param name="Send">Key sequence to write to the pseudo-console, already escape-decoded.</param>
/// <param name="Description">Why this rule exists and why this key sequence is correct.</param>
public sealed record AutoAnswerRule(string Id, string Pattern, string Send, string Description);

/// <summary>The complete auto-answer configuration.</summary>
/// <param name="Version">Schema version of the file.</param>
/// <param name="QuietPeriodMs">Milliseconds of terminal silence that count as "settled".</param>
/// <param name="SettleTimeoutMs">Hard ceiling for the whole settle-and-answer loop.</param>
/// <param name="MaxAnswersPerPhase">Maximum number of automatic answers in one phase.</param>
/// <param name="Rules">Rules in evaluation order; the first match wins.</param>
public sealed record AutoAnswerRuleSet(
    int Version,
    int QuietPeriodMs,
    int SettleTimeoutMs,
    int MaxAnswersPerPhase,
    IReadOnlyList<AutoAnswerRule> Rules);
```

`Workflow\Services\EscapeDecoder.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Workflow.Services;

/// <summary>Decodes the C-style escapes allowed in a rule's "send" value.</summary>
public static class EscapeDecoder
{
    /// <summary>Decodes \r \n \t \e \\ and \uXXXX. Unknown escapes are left verbatim.</summary>
    /// <param name="raw">The raw value from the rules file.</param>
    /// <returns>The decoded key sequence.</returns>
    public static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains('\\', StringComparison.Ordinal))
        {
            return raw ?? string.Empty;
        }

        var builder = new StringBuilder(raw.Length);

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i == raw.Length - 1)
            {
                builder.Append(raw[i]);
                continue;
            }

            var next = raw[i + 1];
            switch (next)
            {
                case 'r': builder.Append('\r'); i++; break;
                case 'n': builder.Append('\n'); i++; break;
                case 't': builder.Append('\t'); i++; break;
                case 'e': builder.Append('\u001b'); i++; break;
                case '\\': builder.Append('\\'); i++; break;
                case 'u' when i + 5 < raw.Length
                              && ushort.TryParse(
                                  raw.AsSpan(i + 2, 4),
                                  NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture,
                                  out var code):
                    builder.Append((char)code);
                    i += 5;
                    break;
                default:
                    builder.Append(raw[i]);
                    break;
            }
        }

        return builder.ToString();
    }
}
```

`Workflow\Services\IAutoAnswerService.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>Matches rendered terminal text against the configured auto-answer rules.</summary>
public interface IAutoAnswerService
{
    /// <summary>The loaded configuration.</summary>
    public AutoAnswerRuleSet RuleSet { get; }

    /// <summary>Finds the first rule that matches and has not fired yet in this phase.</summary>
    /// <param name="screenText">The last rendered rows of the terminal.</param>
    /// <param name="alreadyFiredRuleIds">Rule identifiers already used in this phase.</param>
    /// <returns>The matching rule, or null.</returns>
    public AutoAnswerRule? Match(string screenText, IReadOnlySet<string> alreadyFiredRuleIds);
}
```

`Workflow\Services\AutoAnswerService.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="IAutoAnswerService" />
public sealed class AutoAnswerService : IAutoAnswerService
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<(AutoAnswerRule Rule, Regex? Regex)> _compiled = [];

    /// <summary>Creates the service.</summary>
    /// <param name="shippedRulesPath">Path to Assets\autoanswer.rules.json in the output directory.</param>
    /// <param name="overridePath">Optional user override; when it exists it replaces the shipped set.</param>
    public AutoAnswerService(string shippedRulesPath, string? overridePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shippedRulesPath);

        var path = !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
            ? overridePath
            : shippedRulesPath;

        RuleSet = Load(path);

        foreach (var rule in RuleSet.Rules)
        {
            Regex? regex = null;
            try
            {
                regex = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                // A user-edited pattern must not take the application down; the rule is skipped.
            }

            _compiled.Add((rule, regex));
        }
    }

    /// <inheritdoc />
    public AutoAnswerRuleSet RuleSet { get; }

    /// <inheritdoc />
    public AutoAnswerRule? Match(string screenText, IReadOnlySet<string> alreadyFiredRuleIds)
    {
        ArgumentNullException.ThrowIfNull(alreadyFiredRuleIds);

        if (string.IsNullOrEmpty(screenText))
        {
            return null;
        }

        foreach (var (rule, regex) in _compiled)
        {
            if (regex is null || alreadyFiredRuleIds.Contains(rule.Id))
            {
                continue;
            }

            try
            {
                if (regex.IsMatch(screenText))
                {
                    return rule;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Catastrophic backtracking in a user-edited pattern; treat as no match.
            }
        }

        return null;
    }

    private static AutoAnswerRuleSet Load(string path)
    {
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<RuleSetDto>(json, JsonOptions)
                  ?? throw new InvalidOperationException($"Die Regeldatei '{path}' ist leer oder ungültig.");

        var rules = (dto.Rules ?? [])
            .Select(r => new AutoAnswerRule(
                r.Id ?? string.Empty,
                r.Pattern ?? string.Empty,
                EscapeDecoder.Decode(r.Send ?? string.Empty),
                r.Description ?? string.Empty))
            .Where(r => r.Id.Length > 0 && r.Pattern.Length > 0)
            .ToList();

        return new AutoAnswerRuleSet(
            dto.Version,
            dto.QuietPeriodMs,
            dto.SettleTimeoutMs,
            dto.MaxAnswersPerPhase,
            rules);
    }

    private sealed class RuleSetDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("quietPeriodMs")]
        public int QuietPeriodMs { get; set; } = 1500;

        [JsonPropertyName("settleTimeoutMs")]
        public int SettleTimeoutMs { get; set; } = 60000;

        [JsonPropertyName("maxAnswersPerPhase")]
        public int MaxAnswersPerPhase { get; set; } = 5;

        [JsonPropertyName("rules")]
        public List<RuleDto>? Rules { get; set; }
    }

    private sealed class RuleDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }

        [JsonPropertyName("send")]
        public string? Send { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 5: Make the shipped rules visible to the test host**

Add to the `Content` `ItemGroup` in `Workflow.Tests\Workflow.Tests.csproj`:

```xml
    <Content Include="..\Workflow\Assets\autoanswer.rules.json" LinkBase="Assets">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~EscapeDecoderTests|FullyQualifiedName~AutoAnswerServiceTests"`
Expected: PASS — 22 passed.

- [ ] **Step 7: Commit**

```
git add -A
git commit -m "feat(autoanswer): data-driven rule engine with escape decoding"
```

---

## Task 6: `ArtifactWatcher`

**Files:**
- Create: `Workflow\Services\IArtifactWatcher.cs`, `Workflow\Services\ArtifactWatcher.cs`, `Workflow\Services\ArtifactWatchException.cs`, `Workflow\Models\FileHashResult.cs`, `Workflow\Services\IArtifactWatcherFactory.cs` (holds both `IArtifactWatcherFactory` and `ArtifactWatcherFactory`)
- Test: `Workflow.Tests\ArtifactWatcherTests.cs`

**Interfaces:**
- Consumes: `CompletionRule` (Task 2).
- Produces:
  - `interface IArtifactWatcher : IDisposable` with `Task WaitAsync(CancellationToken cancellationToken)` — completing normally means *satisfied*; a watcher failure (see below) faults the task with `ArtifactWatchException`
  - `sealed class ArtifactWatchException : Exception` carrying a German message naming the directory
  - `readonly record struct FileHashResult(FileHashState State, string? Hash)` with `enum FileHashState { Hash, Missing, Unreadable }`
  - `interface IArtifactWatcherFactory` with
    `IArtifactWatcher Create(CompletionRule rule, string directory, IReadOnlyList<string> absolutePaths, TimeSpan debounce, TimeSpan pollInterval)`

> **Why a poll runs alongside the watcher.** `FileSystemWatcher` drops events on OneDrive-backed,
> network and virtualised directories. A missed event here stalls the pipeline permanently, so the
> 1 s poll is not belt-and-braces — it is the reliability guarantee on the app's critical path.

> **Hashing is tri-state: "unreadable" is not "changed" (spec §7.5).** Returning `null` both for
> a missing file and for a transient `IOException` and then comparing it against a non-null
> baseline reports a *content change* for a file whose bytes have not moved. During phase 3 the
> spec and the plan are exactly the files an editor or the running CLI is most likely to have
> open, so a momentary exclusive lock would advance the workflow. `ComputeHash` therefore returns
> `FileHashResult`, and `HasChangedSinceBaseline` never reports a change for `Unreadable` —
> it waits for the next poll. A baseline captured as `Unreadable` is re-captured on the first poll
> that succeeds, so a lock held at phase start does not permanently arm the watcher either.

> **A watcher that can no longer succeed must fail, not spin (spec §12.2).** If the watched
> directory is deleted mid-run, the current design keeps polling a condition that can never hold
> and the phase sits at *Active* forever. `FileSystemWatcher.Error` is subscribed and the poll
> checks `Directory.Exists`; either one faults `WaitAsync` with `ArtifactWatchException`, which
> the orchestrator surfaces and the tab reports.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\ArtifactWatcherTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class ArtifactWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir;
    private readonly ArtifactWatcherFactory _factory = new();

    public ArtifactWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private IArtifactWatcher Create(CompletionRule rule, params string[] names) =>
        _factory.Create(rule, _dir, names.Select(P).ToList(), Debounce, Poll);

    private static async Task<bool> CompletesAsync(IArtifactWatcher watcher)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await watcher.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [Fact]
    public async Task AnyContentChanged_DoesNotFireWhileAFileIsExclusivelyLocked()
    {
        await File.WriteAllTextAsync(P("spec.md"), "original");
        using var watcher = Create(CompletionRule.AnyContentChanged, "spec.md");

        // An editor (or the running CLI) holds the file. The bytes have not changed.
        using (new FileStream(P("spec.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
        }
    }

    [Fact]
    public async Task AnyContentChanged_StillFiresAfterTheLockIsReleasedAndTheFileChanges()
    {
        await File.WriteAllTextAsync(P("spec.md"), "original");
        using var watcher = Create(CompletionRule.AnyContentChanged, "spec.md");

        using (new FileStream(P("spec.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Task.Delay(300);
        }

        var waiting = CompletesAsync(watcher);
        await File.WriteAllTextAsync(P("spec.md"), "edited");

        Assert.True(await waiting);
    }

    [Fact]
    public async Task DeletingTheWatchedDirectory_FailsTheWatcherInsteadOfPollingForever()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md");
        var waiting = watcher.WaitAsync(new CancellationTokenSource(Timeout).Token);

        Directory.Delete(_dir, recursive: true);

        var error = await Assert.ThrowsAsync<ArtifactWatchException>(() => waiting);
        Assert.Contains(_dir, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilesExist_CompletesWhenAllPathsBecomeNonEmpty()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md", "b.md");
        var waiting = CompletesAsync(watcher);

        await File.WriteAllTextAsync(P("a.md"), "content");
        await File.WriteAllTextAsync(P("b.md"), "content");

        Assert.True(await waiting);
    }

    [Fact]
    public async Task FilesExist_CompletesImmediatelyWhenFilesAreAlreadyThere()
    {
        await File.WriteAllTextAsync(P("a.md"), "content");
        using var watcher = Create(CompletionRule.FilesExist, "a.md");

        Assert.True(await CompletesAsync(watcher));
    }

    [Fact]
    public async Task FilesExist_DoesNotCompleteWhileOnePathIsMissing()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md", "b.md");
        await File.WriteAllTextAsync(P("a.md"), "content");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task FilesExist_TreatsAZeroByteFileAsNotReady()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md");
        await File.WriteAllTextAsync(P("a.md"), string.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task AnyContentChanged_CompletesWhenContentActuallyChanges()
    {
        await File.WriteAllTextAsync(P("a.md"), "before");
        using var watcher = Create(CompletionRule.AnyContentChanged, "a.md");
        var waiting = CompletesAsync(watcher);

        await Task.Delay(150);
        await File.WriteAllTextAsync(P("a.md"), "after");

        Assert.True(await waiting);
    }

    [Fact]
    public async Task AnyContentChanged_IgnoresARewriteOfIdenticalContent()
    {
        await File.WriteAllTextAsync(P("a.md"), "same");
        using var watcher = Create(CompletionRule.AnyContentChanged, "a.md");

        await Task.Delay(150);
        await File.WriteAllTextAsync(P("a.md"), "same");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task AnyContentChanged_CompletesForAFileThatDidNotExistAtBaseline()
    {
        using var watcher = Create(CompletionRule.AnyContentChanged, "a.md");
        var waiting = CompletesAsync(watcher);

        await Task.Delay(150);
        await File.WriteAllTextAsync(P("a.md"), "created");

        Assert.True(await waiting);
    }

    [Fact]
    public async Task Manual_NeverCompletesOnItsOwn()
    {
        using var watcher = Create(CompletionRule.Manual, "a.md");
        await File.WriteAllTextAsync(P("a.md"), "content");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task WaitAsync_IsIdempotentAfterCompletion()
    {
        await File.WriteAllTextAsync(P("a.md"), "content");
        using var watcher = Create(CompletionRule.FilesExist, "a.md");

        Assert.True(await CompletesAsync(watcher));
        Assert.True(await CompletesAsync(watcher));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ArtifactWatcherTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ArtifactWatcherFactory' could not be found`.

- [ ] **Step 3: Write the implementation**

`Workflow\Services\IArtifactWatcher.cs`:

```csharp
namespace Workflow.Services;

/// <summary>Signals when a phase's artefact condition is satisfied.</summary>
public interface IArtifactWatcher : IDisposable
{
    /// <summary>Waits until the condition holds.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the artefacts satisfy the rule.</returns>
    public Task WaitAsync(CancellationToken cancellationToken);
}
```

`Workflow\Services\IArtifactWatcherFactory.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>Creates one artefact watcher per phase.</summary>
public interface IArtifactWatcherFactory
{
    /// <summary>Creates a watcher.</summary>
    /// <param name="rule">Completion rule to apply.</param>
    /// <param name="directory">Directory to watch.</param>
    /// <param name="absolutePaths">Artefact paths the rule applies to.</param>
    /// <param name="debounce">Delay applied after a change notification before re-checking.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    /// <returns>A watcher that has already captured its baseline.</returns>
    public IArtifactWatcher Create(
        CompletionRule rule,
        string directory,
        IReadOnlyList<string> absolutePaths,
        TimeSpan debounce,
        TimeSpan pollInterval);
}

/// <inheritdoc cref="IArtifactWatcherFactory" />
public sealed class ArtifactWatcherFactory : IArtifactWatcherFactory
{
    /// <inheritdoc />
    public IArtifactWatcher Create(
        CompletionRule rule,
        string directory,
        IReadOnlyList<string> absolutePaths,
        TimeSpan debounce,
        TimeSpan pollInterval) =>
        new ArtifactWatcher(rule, directory, absolutePaths, debounce, pollInterval);
}
```

`Workflow\Models\FileHashResult.cs` — the tri-state read result:

```csharp
namespace Workflow.Models;

/// <summary>How a hash attempt ended.</summary>
public enum FileHashState
{
    /// <summary>The file was read end to end and <see cref="FileHashResult.Hash"/> is set.</summary>
    Hash,

    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The file exists but could not be read right now (another process holds it).</summary>
    Unreadable,
}

/// <summary>The outcome of hashing one artefact.</summary>
/// <param name="State">How the attempt ended.</param>
/// <param name="Hash">The SHA-256 hex string when <paramref name="State"/> is <see cref="FileHashState.Hash"/>; otherwise null.</param>
public readonly record struct FileHashResult(FileHashState State, string? Hash);
```

`Workflow\Services\ArtifactWatchException.cs`:

```csharp
namespace Workflow.Services;

/// <summary>Raised when an artefact watcher can no longer observe its directory.</summary>
public sealed class ArtifactWatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ArtifactWatchException()
    {
    }

    /// <summary>Creates the exception with a German message naming the directory.</summary>
    /// <param name="message">The message shown to the user.</param>
    public ArtifactWatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ArtifactWatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`Workflow\Services\ArtifactWatcher.cs`:

```csharp
using System.IO;
using System.Security.Cryptography;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="IArtifactWatcher" />
public sealed class ArtifactWatcher : IArtifactWatcher
{
    private readonly CompletionRule _rule;
    private readonly IReadOnlyList<string> _paths;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<string, FileHashResult> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileSystemWatcher? _watcher;

    private int _disposed;

    /// <summary>Creates the watcher and captures the baseline hashes.</summary>
    /// <param name="rule">Completion rule to apply.</param>
    /// <param name="directory">Directory to watch.</param>
    /// <param name="absolutePaths">Artefact paths the rule applies to.</param>
    /// <param name="debounce">Delay applied after a change notification before re-checking.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    public ArtifactWatcher(
        CompletionRule rule,
        string directory,
        IReadOnlyList<string> absolutePaths,
        TimeSpan debounce,
        TimeSpan pollInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(absolutePaths);

        _rule = rule;
        _paths = absolutePaths;
        _debounce = debounce;
        _pollInterval = pollInterval;
        _directory = directory;

        foreach (var path in _paths)
        {
            _baseline[path] = ComputeHash(path);
        }

        if (_rule == CompletionRule.Manual)
        {
            return;
        }

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        _watcher.Error += OnWatcherError;

        _ = Task.Run(() => PollAsync(_lifetime.Token), _lifetime.Token);

        // The condition may already hold before any event arrives.
        CheckAndSignal();
    }

    /// <inheritdoc />
    public Task WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileSystemEvent;
            _watcher.Created -= OnFileSystemEvent;
            _watcher.Renamed -= OnFileSystemEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _lifetime.Dispose();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        Fail($"Die Ueberwachung von '{_directory}' ist fehlgeschlagen: {e.GetException().Message}");

    private void Fail(string message)
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        _completion.TrySetException(new ArtifactWatchException(message));
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, _lifetime.Token);
                CheckAndSignal();
            }
            catch (OperationCanceledException)
            {
                // Watcher disposed while debouncing.
            }
        });

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        // FileSystemWatcher drops events on OneDrive/network/virtualised paths; a missed event
        // would stall the pipeline permanently, so the condition is also polled.
        using var timer = new PeriodicTimer(_pollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                CheckAndSignal();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void CheckAndSignal()
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        // A watcher whose directory has gone can never be satisfied. Failing is the documented
        // behaviour (spec section 12.2); continuing to poll would leave the phase Active forever.
        if (_watcher is not null && !Directory.Exists(_directory))
        {
            Fail($"Das Arbeitsverzeichnis '{_directory}' existiert nicht mehr. Die Phase wurde abgebrochen.");
            return;
        }

        var satisfied = _rule switch
        {
            CompletionRule.FilesExist => _paths.All(IsPresentAndNonEmpty),
            CompletionRule.AnyContentChanged => _paths.Any(HasChangedSinceBaseline),
            _ => false,
        };

        if (satisfied)
        {
            _completion.TrySetResult();
        }
    }

    private static bool IsPresentAndNonEmpty(string path)
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
    }

    private bool HasChangedSinceBaseline(string path)
    {
        var current = ComputeHash(path);
        var baseline = _baseline.GetValueOrDefault(path, new FileHashResult(FileHashState.Missing, null));

        // A file we could not read tells us nothing. Reporting a change here would advance the
        // workflow because an editor happened to hold a lock for a few milliseconds.
        if (current.State == FileHashState.Unreadable)
        {
            return false;
        }

        // The baseline itself may have been unreadable at phase start. Re-capture it now that the
        // file can be read, and do not count that first successful read as a change.
        if (baseline.State == FileHashState.Unreadable)
        {
            _baseline[path] = current;
            return false;
        }

        if (current.State != baseline.State)
        {
            return true;
        }

        return current.State == FileHashState.Hash
            && !string.Equals(current.Hash, baseline.Hash, StringComparison.Ordinal);
    }

    private static FileHashResult ComputeHash(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FileHashResult(FileHashState.Missing, null);
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return new FileHashResult(FileHashState.Hash, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (IOException)
        {
            // The writer still holds the file exclusively; the next poll will see it.
            return new FileHashResult(FileHashState.Unreadable, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileHashResult(FileHashState.Unreadable, null);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ArtifactWatcherTests"`
Expected: PASS — 12 passed.

> `DeletingTheWatchedDirectory_...` deletes `_dir` itself, so `Dispose` must tolerate an already
> removed directory — it already guards with `Directory.Exists`.

- [ ] **Step 5: Commit**

```
git add -A
git commit -m "feat(services): artefact watcher with FileSystemWatcher plus poll fallback"
```

---

## Task 7: `SettingsService`

**Files:**
- Create: `Workflow\Models\AppSettings.cs`, `Workflow\Services\ISettingsService.cs`, `Workflow\Services\SettingsService.cs`
- Test: `Workflow.Tests\SettingsServiceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed class AppSettings` with `Collection<string> RecentDirectories { get; set; }` and `string? LastDirectory { get; set; }` — `Collection<T>`, not `List<T>`: `CA1002` makes a public `List<T>` a build error, and `System.Text.Json` populates `Collection<T>` just as happily
  - `interface ISettingsService` with `AppSettings Settings { get; }`, `void AddRecentDirectory(string directory)`, `void Save()`, `static string DefaultPath { get; }` (on the implementation, not the interface)

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\SettingsServiceTests.cs`:

```csharp
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void NewFile_StartsWithEmptyDefaults()
    {
        var service = new SettingsService(_path);

        Assert.Empty(service.Settings.RecentDirectories);
        Assert.Null(service.Settings.LastDirectory);
    }

    [Fact]
    public void AddRecentDirectory_PutsTheNewestFirstAndSetsLastDirectory()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\one");
        service.AddRecentDirectory(@"C:\two");

        Assert.Equal([@"C:\two", @"C:\one"], service.Settings.RecentDirectories);
        Assert.Equal(@"C:\two", service.Settings.LastDirectory);
    }

    [Fact]
    public void AddRecentDirectory_DeduplicatesCaseInsensitivelyAndPromotes()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\one");
        service.AddRecentDirectory(@"C:\two");
        service.AddRecentDirectory(@"c:\ONE");

        Assert.Equal(2, service.Settings.RecentDirectories.Count);
        Assert.Equal(@"c:\ONE", service.Settings.RecentDirectories[0]);
    }

    [Fact]
    public void AddRecentDirectory_StripsATrailingSeparator()
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(@"C:\one\");

        Assert.Equal(@"C:\one", service.Settings.RecentDirectories[0]);
    }

    [Fact]
    public void AddRecentDirectory_CapsTheListAtFifteenEntries()
    {
        var service = new SettingsService(_path);

        for (var i = 0; i < 20; i++)
        {
            service.AddRecentDirectory($@"C:\dir{i}");
        }

        Assert.Equal(15, service.Settings.RecentDirectories.Count);
        Assert.Equal(@"C:\dir19", service.Settings.RecentDirectories[0]);
        Assert.Equal(@"C:\dir5", service.Settings.RecentDirectories[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRecentDirectory_IgnoresEmptyInput(string? value)
    {
        var service = new SettingsService(_path);

        service.AddRecentDirectory(value!);

        Assert.Empty(service.Settings.RecentDirectories);
    }

    [Fact]
    public void Save_RoundTrips()
    {
        var first = new SettingsService(_path);
        first.AddRecentDirectory(@"C:\one");
        first.Save();

        var second = new SettingsService(_path);

        Assert.Equal([@"C:\one"], second.Settings.RecentDirectories);
        Assert.Equal(@"C:\one", second.Settings.LastDirectory);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var service = new SettingsService(_path);
        service.AddRecentDirectory(@"C:\one");

        service.Save();

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");

        var service = new SettingsService(_path);

        Assert.Empty(service.Settings.RecentDirectories);
    }

    [Fact]
    public void Save_CreatesTheDirectoryWhenItIsMissing()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");
        var service = new SettingsService(nested);
        service.AddRecentDirectory(@"C:\one");

        service.Save();

        Assert.True(File.Exists(nested));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~SettingsServiceTests"`
Expected: FAIL — `CS0246: The type or namespace name 'SettingsService' could not be found`.

- [ ] **Step 3: Write the implementation**

`Workflow\Models\AppSettings.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Workflow.Models;

/// <summary>Persisted application settings.</summary>
[SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "System.Text.Json requires a settable collection property to populate this list.")]
public sealed class AppSettings
{
    /// <summary>Working directories the user has picked, most recent first.</summary>
    /// <remarks>
    /// <c>Collection&lt;string&gt;</c> rather than <c>List&lt;string&gt;</c>: CA1002 turns a
    /// public <c>List&lt;T&gt;</c> into a build error under AnalysisMode=All, and
    /// System.Text.Json round-trips <c>Collection&lt;T&gt;</c> without any converter.
    /// </remarks>
    public Collection<string> RecentDirectories { get; set; } = [];

    /// <summary>The directory preselected for a new tab, or null.</summary>
    public string? LastDirectory { get; set; }
}
```

> `Collection<T>` has no `RemoveAll` / `RemoveRange`. The MRU maintenance below therefore removes
> matches by index and trims the tail in a loop; both are three lines and are covered by the
> existing de-duplication and cap tests.

`Workflow\Services\ISettingsService.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>Loads and persists the directory MRU list.</summary>
public interface ISettingsService
{
    /// <summary>The in-memory settings.</summary>
    public AppSettings Settings { get; }

    /// <summary>Adds or promotes a directory in the MRU list and records it as the last used one.</summary>
    /// <param name="directory">Absolute directory path. Empty values are ignored.</param>
    public void AddRecentDirectory(string directory);

    /// <summary>Writes the settings to disk atomically.</summary>
    public void Save();
}
```

`Workflow\Services\SettingsService.cs`:

```csharp
using System.IO;
using System.Text.Json;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ISettingsService" />
public sealed class SettingsService : ISettingsService
{
    private const int MaxRecentDirectories = 15;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Creates the service and loads the file if it exists.</summary>
    /// <param name="path">Full path of settings.json.</param>
    public SettingsService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        Settings = Load(path);
    }

    /// <summary>The default settings location, %APPDATA%\Workflow\settings.json.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Workflow",
        "settings.json");

    /// <inheritdoc />
    public AppSettings Settings { get; }

    /// <inheritdoc />
    public void AddRecentDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        // Root-aware (see WorkingDirectoryPath): persisting "C:" instead of "C:\" would make the
        // restored MRU entry drive-relative on the next launch.
        var normalised = WorkingDirectoryPath.Normalise(directory);

        // Collection<T> has no RemoveAll/RemoveRange (see AppSettings); remove by index instead.
        for (var i = Settings.RecentDirectories.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Settings.RecentDirectories[i], normalised, StringComparison.OrdinalIgnoreCase))
            {
                Settings.RecentDirectories.RemoveAt(i);
            }
        }

        Settings.RecentDirectories.Insert(0, normalised);

        while (Settings.RecentDirectories.Count > MaxRecentDirectories)
        {
            Settings.RecentDirectories.RemoveAt(Settings.RecentDirectories.Count - 1);
        }

        Settings.LastDirectory = normalised;
    }

    /// <inheritdoc />
    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Settings are a convenience; losing them must never take the application down.
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Nothing further can be done.
                }
            }
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~SettingsServiceTests"`
Expected: PASS — 13 passed.

- [ ] **Step 5: Commit**

```
git add -A
git commit -m "feat(services): settings service with atomic write and MRU directory list"
```

---

## Task 8: ConPTY terminal session

> **REQUIRED READING before starting:** the `dotnet/skills/dotnet-pinvoke` skill at
> `C:\vb5\QuincyNET\Quincy_App\Modul\AMTS_Medikationsplan\.agents\skills\dotnet\skills\dotnet-pinvoke\SKILL.md`.
> This is the highest-risk task in the plan; handle ownership and shutdown ordering are where it
> goes wrong.

**Files:**
- Create: `Workflow\Terminal\ITerminalSession.cs`, `Workflow\Terminal\ITerminalSessionFactory.cs`, `Workflow\Terminal\ConPtySession.cs`, `Workflow\Terminal\ConPtySessionFactory.cs`, `Workflow\Terminal\ShellLocator.cs`, `Workflow\Terminal\Native\NativeMethods.cs`, `Workflow\Terminal\Native\NativeStructs.cs`, `Workflow\Terminal\Native\SafePseudoConsoleHandle.cs`, `Workflow\Terminal\Native\SafeProcThreadAttributeList.cs`
- Test: `Workflow.Tests\ConPtySessionTests.cs`, `Workflow.Tests\ShellLocatorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface ITerminalSession : IDisposable` with
    `event EventHandler<ReadOnlyMemory<byte>>? OutputReceived`,
    `event EventHandler<int>? Exited`,
    `bool IsRunning { get; }`,
    `void Start(string executable, string arguments, string workingDirectory, int columns, int rows)`,
    `void Write(ReadOnlySpan<byte> data)`,
    `void Resize(int columns, int rows)`
  - `interface ITerminalSessionFactory` with `ITerminalSession Create()`
  - `static class ShellLocator` with `static string FindShellExecutable()` and
    `static string ShellArguments { get; }`

**Constraints specific to this task:**
- `AllowUnsafeBlocks` is `false`. Use `DllImport` + `IntPtr` + `Marshal` only. Do not reach for `LibraryImport` — it emits `unsafe` marshalling code.
- PTY bytes are raised as `byte[]`; they are **never** decoded to `string` here. A UTF-8 sequence can straddle a read boundary.
- `CA5392`: `NativeMethods` carries `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`, otherwise every `[DllImport]` is a build error.
- `using System.IO;` is required in **every** file here that names `FileStream`, `Path` or `IOException` — including `ConPtySessionTests.cs` (see *Analyzer conformance*).

---

### ⛔ Step 0 (BLOCKING): prove the ConPTY wiring in a throwaway spike before writing any of Step 3–7

**Do not skip this and do not treat the code in Steps 3–7 as validated.** The sequence below is
derived from the Windows documentation and the `microsoft/terminal` sample. On 2026-09-12 it was
materialised verbatim as a standalone `net8.0` console app and run on this machine, in a real
Windows console host *and* under a redirected shell. **It does not stream output.** Every phase of
this application sits on this edge, so it is proven first or not at all (spec §6.3.1).

**What the probe established — do not spend time re-deriving it:**

| Observation | Result |
|---|---|
| `CreatePipe` ×2, `CreatePseudoConsole`, `InitializeProcThreadAttributeList` ×2, `UpdateProcThreadAttribute`, `CreateProcess` | **all return success**, `GetLastPInvokeError() == 0` |
| Attribute-list buffer, byte-dumped after `UpdateProcThreadAttribute` | contains `16 00 02 00` (`0x00020016`), `cbSize = 8`, and the `HPCON` — **correctly populated** |
| `STARTUPINFOEX` marshalled bytes | `cb = 112`, `lpAttributeList` = the buffer — **correct** |
| `ResizePseudoConsole(hpc, …)` | `S_OK` — the `HPCON` is real |
| Child attachment: `cmd.exe /c mode con` inside a PTY created at **137 × 41** | child reports `Zeilen: 41 / Spalten: 137` — **the child IS attached**; attachment is not the defect |
| Bytes read from the output pipe for the child's own output (`pwsh -NoLogo -NoExit`, and `cmd.exe /c echo`) | **0** |
| Bytes read after an explicit `ResizePseudoConsole` | 115–126 — **the output pipe is wired and readable** |

**Already ruled out by experiment — do not re-test these:** closing the PTY-side handles
immediately vs. after 250 ms vs. after `CreateProcess`; starting the read loop before vs. after
`CreateProcess`; `string` vs. `StringBuilder` for `lpCommandLine`; passing `lpApplicationName`;
`CREATE_UNICODE_ENVIRONMENT` on/off; `bInheritHandles` true/false; `FileStream` vs. a raw
`ReadFile` P/Invoke on the read handle; `cmd.exe` vs. `pwsh.exe`.

**Untested candidates, in the order worth trying:**

1. Open the output handle for **overlapped** I/O and read asynchronously
   (`FileStream(handle, FileAccess.Read, bufferSize, isAsync: true)` requires the handle to have
   been created with `FILE_FLAG_OVERLAPPED`, so the pipes must come from `CreateNamedPipe`
   rather than the anonymous `CreatePipe`). This is what a production ConPTY host does.
2. `PSEUDOCONSOLE_INHERIT_CURSOR` (`dwFlags = 1`) — it measurably changed behaviour in the probe.
3. Create the pipes with an inheritable `SECURITY_ATTRIBUTES` instead of `NULL`.

**Exit criteria for Step 0 — all three must hold before Step 1:**

- [ ] A minimal spike app writes `Write-Output 'WF_MARKER_OK'` into a pseudo-console hosting
      `pwsh -NoLogo -NoExit` and reads `WF_MARKER_OK` back out of the output pipe within 10 s.
- [ ] The same spike shows the launcher's own first frame (a non-zero byte count **before** any
      input is written), because the settle-and-answer loop in Task 9 depends on it.
- [ ] The proven call order, handle ownership and read strategy are written back into **this
      task** and into **spec §6.3 / §6.3.1**, replacing whatever they say now, *before* any
      `ConPtySession` code is committed.

Keep the spike under `Workflow.Tests` as `ConPtyProbe` or delete it once Step 8 passes — but the
plan and spec must record what actually worked. If the spike cannot be made to work, stop and
escalate: the WebView2/xterm.js hosting decision (D1) rests on ConPTY being available, and no
amount of work in Tasks 9–17 is useful without it.

---

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\ShellLocatorTests.cs`:

```csharp
using System.IO;
using Workflow.Terminal;

namespace Workflow.Tests;

public class ShellLocatorTests
{
    [Fact]
    public void FindShellExecutable_ReturnsAnExistingExecutable()
    {
        var shell = ShellLocator.FindShellExecutable();

        Assert.True(File.Exists(shell), $"Shell not found at {shell}");
        Assert.EndsWith(".exe", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShellArguments_SuppressTheLogoAndKeepTheShellOpen()
    {
        Assert.Contains("-NoLogo", ShellLocator.ShellArguments, StringComparison.Ordinal);
        Assert.Contains("-NoExit", ShellLocator.ShellArguments, StringComparison.Ordinal);
    }
}
```

`Workflow.Tests\ConPtySessionTests.cs` — these are integration tests against a real
pseudo-console. They are fast (< 5 s each) and are the only meaningful way to verify the
native layer.

```csharp
using System.IO;          // REQUIRED: UseWPF=true drops System.IO from the implicit usings,
                          // so Path.GetTempPath() below is CS0103 without it.
using System.Text;
using Workflow.Terminal;

namespace Workflow.Tests;

public sealed class ConPtySessionTests
{
    private static async Task<string> RunAndCaptureAsync(string command, TimeSpan timeout)
    {
        using var session = new ConPtySession();
        var buffer = new StringBuilder();
        var gate = new SemaphoreSlim(0, 1);

        session.OutputReceived += (_, bytes) =>
        {
            lock (buffer)
            {
                buffer.Append(Encoding.UTF8.GetString(bytes.Span));
            }

            if (gate.CurrentCount == 0)
            {
                gate.Release();
            }
        };

        session.Start(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            Path.GetTempPath(),
            columns: 120,
            rows: 30);

        session.Write(Encoding.UTF8.GetBytes(command + "\r"));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await gate.WaitAsync(TimeSpan.FromMilliseconds(200));
            lock (buffer)
            {
                if (buffer.ToString().Contains("WF_MARKER_OK", StringComparison.Ordinal))
                {
                    return buffer.ToString();
                }
            }
        }

        lock (buffer)
        {
            return buffer.ToString();
        }
    }

    [Fact]
    public async Task Start_RunsACommandAndStreamsItsOutput()
    {
        var output = await RunAndCaptureAsync("Write-Output 'WF_MARKER_OK'", TimeSpan.FromSeconds(20));

        Assert.Contains("WF_MARKER_OK", output, StringComparison.Ordinal);
    }

    // Diagnostic companion to the test above. When the pseudo-console is mis-wired the plain
    // assertion only says "expected WF_MARKER_OK, got \"\"", which is exactly the unhelpful
    // signal the Step 0 probe had to work around. This one distinguishes the three cases:
    // the shell died, the shell lived but emitted nothing, or the shell emitted the wrong text.
    [Fact]
    public async Task Start_EmitsTheLauncherFrameBeforeAnyInput()
    {
        using var session = new ConPtySession();
        var bytes = 0;
        var exitCode = (int?)null;

        session.OutputReceived += (_, chunk) => Interlocked.Add(ref bytes, chunk.Length);
        session.Exited += (_, code) => exitCode = code;

        session.Start(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            Path.GetTempPath(),
            columns: 120,
            rows: 30);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref bytes) == 0)
        {
            await Task.Delay(100);
        }

        Assert.True(
            Volatile.Read(ref bytes) > 0,
            exitCode is null
                ? "The shell is still running but the pseudo-console produced no bytes at all. "
                  + "The PTY is attached to the wrong pipe or is not being read - see Step 0."
                : $"The shell exited with code {exitCode} before producing any output.");
    }

    [Fact]
    public void IsRunning_IsFalseBeforeStartAndTrueAfter()
    {
        using var session = new ConPtySession();

        Assert.False(session.IsRunning);

        session.Start(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            Path.GetTempPath(),
            columns: 80,
            rows: 24);

        Assert.True(session.IsRunning);
    }

    [Fact]
    public void Start_TwiceOnTheSameSessionThrows()
    {
        using var session = new ConPtySession();
        session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24);

        Assert.Throws<InvalidOperationException>(
            () => session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24));
    }

    [Fact]
    public void Resize_DoesNotThrowOnALiveSession()
    {
        using var session = new ConPtySession();
        session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24);

        session.Resize(140, 40);
    }

    [Fact]
    public void Dispose_CompletesPromptlyAndIsIdempotent()
    {
        var session = new ConPtySession();
        session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24);

        var started = DateTime.UtcNow;
        session.Dispose();
        session.Dispose();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "Dispose took too long.");
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void Write_BeforeStartThrows()
    {
        using var session = new ConPtySession();

        Assert.Throws<InvalidOperationException>(() => session.Write("x"u8));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ConPtySessionTests|FullyQualifiedName~ShellLocatorTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ConPtySession' could not be found`.

- [ ] **Step 3: Write the native structs**

`Workflow\Terminal\Native\NativeStructs.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Workflow.Terminal.Native;

/// <summary>Windows COORD structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Coord
{
    /// <summary>Column count.</summary>
    public short X;

    /// <summary>Row count.</summary>
    public short Y;
}

/// <summary>Windows STARTUPINFOW structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    public int cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

/// <summary>Windows STARTUPINFOEXW structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    public StartupInfo StartupInfo;
    public IntPtr lpAttributeList;
}

/// <summary>Windows PROCESS_INFORMATION structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}
```

- [ ] **Step 4: Write the P/Invoke declarations**

`Workflow\Terminal\Native\NativeMethods.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Workflow.Terminal.Native;

/// <summary>kernel32 entry points needed for a pseudo-console.</summary>
// CA5392: without an explicit search path every DllImport below is a build error. Everything
// here is kernel32, so System32 is both correct and the hardened choice.
[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
internal static class NativeMethods
{
    /// <summary>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.</summary>
    internal const int ProcThreadAttributePseudoConsole = 0x00020016;

    /// <summary>EXTENDED_STARTUPINFO_PRESENT.</summary>
    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>CREATE_UNICODE_ENVIRONMENT.</summary>
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>E_NOTIMPL, returned by CreatePseudoConsole on Windows older than 10 1809.</summary>
    internal const int ENotImpl = unchecked((int)0x80004001);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(
        Coord size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
```

- [ ] **Step 5: Write the safe handles**

`Workflow\Terminal\Native\SafePseudoConsoleHandle.cs`:

```csharp
using Microsoft.Win32.SafeHandles;

namespace Workflow.Terminal.Native;

/// <summary>Owns the HPCON returned by CreatePseudoConsole.</summary>
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates the handle wrapper.</summary>
    /// <param name="handle">The raw HPCON.</param>
    internal SafePseudoConsoleHandle(IntPtr handle)
        : base(ownsHandle: true) => SetHandle(handle);

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}
```

`Workflow\Terminal\Native\SafeProcThreadAttributeList.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Workflow.Terminal.Native;

/// <summary>Owns the unmanaged PROC_THREAD_ATTRIBUTE_LIST used to attach the pseudo-console.</summary>
internal sealed class SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeProcThreadAttributeList(IntPtr handle)
        : base(ownsHandle: true) => SetHandle(handle);

    /// <summary>Allocates a one-entry list and attaches the pseudo-console to it.</summary>
    /// <param name="pseudoConsole">The pseudo-console handle to attach.</param>
    /// <returns>The initialised attribute list.</returns>
    internal static SafeProcThreadAttributeList Create(SafePseudoConsoleHandle pseudoConsole)
    {
        var size = IntPtr.Zero;

        // First call always fails; it reports the required size through `size`.
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var buffer = Marshal.AllocHGlobal(size);
        var list = new SafeProcThreadAttributeList(buffer);

        if (!NativeMethods.InitializeProcThreadAttributeList(buffer, 1, 0, ref size))
        {
            list.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList ist fehlgeschlagen.");
        }

        list._initialised = true;

        if (!NativeMethods.UpdateProcThreadAttribute(
                buffer,
                0,
                NativeMethods.ProcThreadAttributePseudoConsole,
                pseudoConsole.DangerousGetHandle(),
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            list.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute ist fehlgeschlagen.");
        }

        return list;
    }

    private bool _initialised;

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        if (_initialised)
        {
            NativeMethods.DeleteProcThreadAttributeList(handle);
        }

        Marshal.FreeHGlobal(handle);
        return true;
    }
}
```

- [ ] **Step 6: Write `ShellLocator`**

`Workflow\Terminal\ShellLocator.cs`:

```csharp
using System.IO;

namespace Workflow.Terminal;

/// <summary>Finds the shell that hosts the pseudo-console.</summary>
public static class ShellLocator
{
    /// <summary>Arguments passed to the shell: no banner, and it stays open after a command.</summary>
    public static string ShellArguments => "-NoLogo -NoExit";

    /// <summary>Returns PowerShell 7 when installed, otherwise Windows PowerShell.</summary>
    /// <returns>Full path to the shell executable.</returns>
    /// <exception cref="FileNotFoundException">Neither shell is present.</exception>
    public static string FindShellExecutable()
    {
        var pwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");

        if (File.Exists(pwsh))
        {
            return pwsh;
        }

        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        throw new FileNotFoundException(
            "Weder pwsh.exe noch powershell.exe wurden gefunden. Workflow benötigt eine PowerShell.");
    }
}
```

- [ ] **Step 7: Write `ConPtySession`**

`Workflow\Terminal\ITerminalSession.cs`:

```csharp
namespace Workflow.Terminal;

/// <summary>An interactive shell running inside a Windows pseudo-console.</summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>Raised for every chunk of raw bytes read from the pseudo-console.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>Raised once when the hosted process ends, carrying its exit code.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>True between a successful Start and Dispose.</summary>
    public bool IsRunning { get; }

    /// <summary>Starts the shell.</summary>
    /// <param name="executable">Full path to the shell executable.</param>
    /// <param name="arguments">Command-line arguments for the shell.</param>
    /// <param name="workingDirectory">Initial current directory.</param>
    /// <param name="columns">Initial pseudo-console width.</param>
    /// <param name="rows">Initial pseudo-console height.</param>
    public void Start(string executable, string arguments, string workingDirectory, int columns, int rows);

    /// <summary>Writes bytes to the pseudo-console input.</summary>
    /// <param name="data">Raw bytes, normally UTF-8 encoded keystrokes.</param>
    public void Write(ReadOnlySpan<byte> data);

    /// <summary>Resizes the pseudo-console.</summary>
    /// <param name="columns">New width in character cells.</param>
    /// <param name="rows">New height in character cells.</param>
    public void Resize(int columns, int rows);
}

/// <summary>Creates terminal sessions.</summary>
public interface ITerminalSessionFactory
{
    /// <summary>Creates a new, unstarted session.</summary>
    /// <returns>The session.</returns>
    public ITerminalSession Create();
}
```

`Workflow\Terminal\ConPtySessionFactory.cs`:

```csharp
namespace Workflow.Terminal;

/// <inheritdoc cref="ITerminalSessionFactory" />
public sealed class ConPtySessionFactory : ITerminalSessionFactory
{
    /// <inheritdoc />
    public ITerminalSession Create() => new ConPtySession();
}
```

`Workflow\Terminal\ConPtySession.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Workflow.Terminal.Native;

namespace Workflow.Terminal;

/// <inheritdoc cref="ITerminalSession" />
public sealed class ConPtySession : ITerminalSession
{
    private const int ReadBufferSize = 4096;

    private readonly CancellationTokenSource _lifetime = new();

    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeProcThreadAttributeList? _attributes;
    private SafeFileHandle? _appWrite;
    private SafeFileHandle? _appRead;
    private FileStream? _input;
    private FileStream? _output;
    private Task? _readLoop;
    private int _processId;
    private IntPtr _processHandle = IntPtr.Zero;
    private int _disposed;

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<int>? Exited;

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public void Start(string executable, string arguments, string workingDirectory, int columns, int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        if (IsRunning)
        {
            throw new InvalidOperationException("Diese Terminal-Sitzung läuft bereits.");
        }

        // Two pipes: the PTY ends are handed to CreatePseudoConsole, the app ends stay here.
        if (!NativeMethods.CreatePipe(out var ptyInRead, out var appWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) ist fehlgeschlagen.");
        }

        if (!NativeMethods.CreatePipe(out var appRead, out var ptyOutWrite, IntPtr.Zero, 0))
        {
            ptyInRead.Dispose();
            appWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) ist fehlgeschlagen.");
        }

        _appWrite = appWrite;
        _appRead = appRead;

        var size = new Coord { X = (short)columns, Y = (short)rows };
        var hr = NativeMethods.CreatePseudoConsole(size, ptyInRead, ptyOutWrite, 0, out var hpc);

        // The pseudo-console duplicated these; keeping our copies open would prevent EOF.
        ptyInRead.Dispose();
        ptyOutWrite.Dispose();

        if (hr == NativeMethods.ENotImpl)
        {
            throw new PlatformNotSupportedException(
                "Diese Windows-Version unterstützt keine Pseudo-Konsole. Workflow benötigt Windows 10 Version 1809 oder neuer.");
        }

        Marshal.ThrowExceptionForHR(hr);

        _pseudoConsole = new SafePseudoConsoleHandle(hpc);
        _attributes = SafeProcThreadAttributeList.Create(_pseudoConsole);

        var startupInfo = default(StartupInfoEx);
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        startupInfo.lpAttributeList = _attributes.DangerousGetHandle();

        var commandLine = $"\"{executable}\" {arguments}";

        if (!NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            var error = Marshal.GetLastWin32Error();
            Cleanup();
            throw new Win32Exception(error, $"Der Shell-Prozess konnte nicht gestartet werden: {commandLine}");
        }

        NativeMethods.CloseHandle(processInformation.hThread);
        _processHandle = processInformation.hProcess;
        _processId = processInformation.dwProcessId;

        _input = new FileStream(_appWrite, FileAccess.Write, bufferSize: 1, isAsync: false);
        _output = new FileStream(_appRead, FileAccess.Read, bufferSize: ReadBufferSize, isAsync: false);

        IsRunning = true;
        _readLoop = Task.Run(() => ReadLoop(_lifetime.Token), CancellationToken.None);
        _ = Task.Run(WaitForExit, CancellationToken.None);
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        var stream = _input ?? throw new InvalidOperationException("Die Terminal-Sitzung wurde noch nicht gestartet.");

        if (data.IsEmpty)
        {
            return;
        }

        try
        {
            stream.Write(data);
            stream.Flush();
        }
        catch (IOException)
        {
            // The shell has gone away; the Exited event reports it.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently.
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        if (_pseudoConsole is null || _pseudoConsole.IsInvalid || _pseudoConsole.IsClosed)
        {
            return;
        }

        var size = new Coord { X = (short)columns, Y = (short)rows };
        NativeMethods.ResizePseudoConsole(_pseudoConsole.DangerousGetHandle(), size);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IsRunning = false;
        _lifetime.Cancel();

        // Order matters. ClosePseudoConsole can block until the attached client exits, so the
        // process tree is killed first; the pipes are closed last so the read loop sees EOF.
        KillProcessTree();
        _pseudoConsole?.Dispose();
        _attributes?.Dispose();

        try
        {
            _input?.Dispose();
        }
        catch (IOException)
        {
            // Already broken.
        }

        try
        {
            _output?.Dispose();
        }
        catch (IOException)
        {
            // Already broken.
        }

        try
        {
            _readLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop faulted during teardown; nothing to do.
        }

        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        _lifetime.Dispose();
    }

    private void Cleanup()
    {
        _attributes?.Dispose();
        _pseudoConsole?.Dispose();
        _appWrite?.Dispose();
        _appRead?.Dispose();
        _attributes = null;
        _pseudoConsole = null;
        _appWrite = null;
        _appRead = null;
    }

    private void KillProcessTree()
    {
        if (_processId == 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(_processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Access denied during shutdown; nothing further can be done.
        }
    }

    private void WaitForExit()
    {
        try
        {
            using var process = Process.GetProcessById(_processId);
            process.WaitForExit();
            IsRunning = false;
            Exited?.Invoke(this, process.ExitCode);
        }
        catch (ArgumentException)
        {
            IsRunning = false;
        }
        catch (InvalidOperationException)
        {
            IsRunning = false;
        }
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var stream = _output;
        if (stream is null)
        {
            return;
        }

        var buffer = new byte[ReadBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            int count;
            try
            {
                count = stream.Read(buffer, 0, buffer.Length);
            }
#pragma warning disable CA1031 // The read loop is a resilience boundary: no failure here may crash the app.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            if (count <= 0)
            {
                return;
            }

            // Raw bytes only. Decoding here would split multi-byte UTF-8 across reads.
            var chunk = new byte[count];
            Array.Copy(buffer, chunk, count);
            OutputReceived?.Invoke(this, chunk);
        }
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ConPtySessionTests|FullyQualifiedName~ShellLocatorTests"`
Expected: PASS — 9 passed.

If `Start_RunsACommandAndStreamsItsOutput` times out with an empty buffer, **do not guess** — the
handle-close ordering has already been ruled out as the cause (Step 0). Read what
`Start_EmitsTheLauncherFrameBeforeAnyInput` reports first: a non-null exit code means the shell
died and the problem is `CreateProcess`; a null exit code with zero bytes means the child is alive
but its output is not reaching the read handle, which is the failure Step 0 exists to solve. Go
back to Step 0 rather than editing Steps 3–7 speculatively.

- [ ] **Step 9: Confirm no orphan processes are left behind**

Run: `powershell -NoProfile -Command "Get-Process pwsh,powershell -ErrorAction SilentlyContinue | Select-Object Id,StartTime"`
Expected: no process whose `StartTime` falls inside the test run.

- [ ] **Step 10: Commit**

```
git add -A
git commit -m "feat(terminal): ConPTY session with safe handle ownership and ordered shutdown"
```

---

## Task 9: `WorkflowOrchestrator`

**Files:**
- Create: `Workflow\Services\ITerminalController.cs`, `Workflow\Services\ManualPhaseSignal.cs`, `Workflow\Models\PhaseProgress.cs`, `Workflow\Services\IWorkflowOrchestrator.cs`, `Workflow\Services\WorkflowOrchestrator.cs`, `Workflow\Services\BracketedPaste.cs`
- Test: `Workflow.Tests\BracketedPasteTests.cs`, `Workflow.Tests\WorkflowOrchestratorTests.cs`, `Workflow.Tests\Fakes\FakeTerminalController.cs`

**Interfaces:**
- Consumes: `PhaseCatalog`, `TaskPaths`, `CompletionRule` (Task 2); `IPromptTemplateService`, `PromptVariables` (Task 4); `IAutoAnswerService` (Task 5); `IArtifactWatcherFactory` (Task 6).
- Produces:
  - `static class BracketedPaste` with `static string Wrap(string body)`
  - `sealed record PhaseProgress(WorkflowPhase Phase, PhaseStatus Status)`
  - `interface ITerminalController` (the orchestrator's *only* view of a terminal)
  - `sealed class ManualPhaseSignal` with `void Signal()`, `Task WaitAsync(CancellationToken)`, `void Reset()`
  - `sealed record WorkflowRunRequest(TaskPaths Paths, string TaskDescription, ITerminalController Terminal, ManualPhaseSignal ManualSignal, IProgress<PhaseProgress> Progress)`
  - `interface IWorkflowOrchestrator` with `Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)`

> `ITerminalController` is the seam that makes the whole state machine testable. `TerminalViewModel`
> (Task 11) implements it over WebView2 + `ConPtySession`; the tests implement it with a fake.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\BracketedPasteTests.cs`:

```csharp
using Workflow.Services;

namespace Workflow.Tests;

public class BracketedPasteTests
{
    [Fact]
    public void Wrap_SurroundsTheBodyWithTheBracketedPasteSequences()
    {
        var wrapped = BracketedPaste.Wrap("hello");

        Assert.Equal("\u001b[200~hello\u001b[201~", wrapped);
    }

    [Theory]
    [InlineData("a\r\nb", "a\rb")]
    [InlineData("a\nb", "a\rb")]
    [InlineData("a\rb", "a\rb")]
    [InlineData("a\n\nb", "a\r\rb")]
    public void Wrap_NormalisesEveryNewlineToCarriageReturn(string body, string expectedBody)
    {
        var wrapped = BracketedPaste.Wrap(body);

        Assert.Equal("\u001b[200~" + expectedBody + "\u001b[201~", wrapped);
    }

    [Fact]
    public void Wrap_HandlesAnEmptyBody()
    {
        Assert.Equal("\u001b[200~\u001b[201~", BracketedPaste.Wrap(string.Empty));
    }
}
```

`Workflow.Tests\Fakes\FakeTerminalController.cs`:

```csharp
using Workflow.Services;

namespace Workflow.Tests.Fakes;

/// <summary>Records everything the orchestrator does to a terminal.</summary>
public sealed class FakeTerminalController : ITerminalController
{
    private readonly Queue<string> _snapshots = new();

    public List<string> StartedSessions { get; } = [];

    public List<string> Sent { get; } = [];

    public List<string> Pasted { get; } = [];

    public int ClearCount { get; private set; }

    public int DisposeCount { get; private set; }

    public DateTimeOffset LastOutputUtc { get; private set; } = DateTimeOffset.UtcNow;

    public long OutputCount { get; private set; }

    /// <summary>Set false to simulate a WebView2 page that is slow to report `ready`.</summary>
    public TaskCompletionSource ReadyGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        ReadyGate.Task.WaitAsync(cancellationToken);

    /// <summary>Queues the text that the next SnapshotAsync call returns.</summary>
    public void QueueSnapshot(string text) => _snapshots.Enqueue(text);

    /// <summary>Simulates a chunk of launcher output arriving on the live session.</summary>
    public void EmitOutput()
    {
        OutputCount++;
        LastOutputUtc = DateTimeOffset.UtcNow;
    }

    public void StartSession(string executable, string arguments, string workingDirectory)
    {
        StartedSessions.Add(workingDirectory);

        // Mirrors the production contract: a fresh session has produced nothing yet, and its
        // clock starts now - never at DateTimeOffset.MinValue.
        OutputCount = 0;
        LastOutputUtc = DateTimeOffset.UtcNow;
    }

    public void ClearScreen() => ClearCount++;

    public Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.Count > 0 ? _snapshots.Dequeue() : string.Empty);

    public void Send(string text) => Sent.Add(text);

    /// <summary>How many pastes had their submitting CR written before a new session started.</summary>
    public int SubmitsCompletedBeforeNextSession { get; private set; }

    public Task SendPasteAsync(string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Pasted.Add(body);
        Sent.Add("\r");
        SubmitsCompletedBeforeNextSession++;
        return Task.CompletedTask;
    }

    public void DisposeSession() => DisposeCount++;
}
```

`Workflow.Tests\WorkflowOrchestratorTests.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Text;
using Workflow.Models;
using Workflow.Services;
using Workflow.Tests.Fakes;

namespace Workflow.Tests;

public sealed class WorkflowOrchestratorTests : IDisposable
{
    private readonly string _root;
    private readonly string _promptDir;
    private readonly string _rulesPath;
    private readonly TaskPaths _paths;
    private readonly List<PhaseProgress> _progress = [];

    public WorkflowOrchestratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-orch-" + Guid.NewGuid().ToString("N"));
        _promptDir = Path.Combine(_root, "Prompt");
        Directory.CreateDirectory(_promptDir);

        foreach (var definition in PhaseCatalog.All)
        {
            File.WriteAllText(
                Path.Combine(_promptDir, definition.PromptFile),
                $"PROMPT {definition.Phase} {{taskbeschreibung}} {{spec_path}} {{plan_path}} {{review_path}}");
        }

        _rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(_rulesPath, """
        {
          "version": 1, "quietPeriodMs": 10, "settleTimeoutMs": 2000, "maxAnswersPerPhase": 3,
          "rules": [ { "id": "bypass", "pattern": "(?is)bypass", "send": "2\\r", "description": "" } ]
        }
        """);

        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        _paths = new TaskPaths(workspace, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
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
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40));

    private WorkflowRunRequest CreateRequest(FakeTerminalController terminal, ManualPhaseSignal signal) =>
        new(_paths, "Beschreibung", terminal, signal, new Progress<PhaseProgress>(p =>
        {
            lock (_progress)
            {
                _progress.Add(p);
            }
        }));

    private void WriteArtifacts(params string[] paths)
    {
        foreach (var path in paths)
        {
            File.WriteAllText(path, "content-" + Guid.NewGuid().ToString("N"));
        }
    }

    [Fact]
    public async Task Phase1_WritesCdThenLauncherThenPastesThePrompt()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Equal($"cd \"{_paths.WorkingDirectory}\"\r", terminal.Sent[0]);
        Assert.Equal("yo\r", terminal.Sent[1]);
        Assert.Contains("PROMPT Specification", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains("Beschreibung", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(_paths.SpecRelative, terminal.Pasted[0], StringComparison.Ordinal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_SendsTheMatchingAutoAnswerBeforeThePrompt()
    {
        var terminal = new FakeTerminalController();
        terminal.QueueSnapshot("WARNING: Bypass Permissions mode\n 1. No, exit\n 2. Yes, I accept");
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Equal("2\r", terminal.Sent[2]);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [Fact]
    public async Task ThePromptIsNotSentBeforeTheLauncherHasRendered()
    {
        // Production race: on a fresh session nothing has been drawn yet. A loop that judges
        // quietness alone would snapshot an empty buffer, match nothing, and paste the prompt
        // into the still-blocking confirmation dialog.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // The launcher has produced nothing: no snapshot is queued and OutputCount stays 0.
        await Task.Delay(400, cts.Token);
        Assert.Empty(terminal.Pasted);

        // Now the launcher paints its bypass warning.
        terminal.QueueSnapshot("bypass permissions mode");
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        Assert.Contains("2\r", terminal.Sent);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task NothingIsWrittenToTheTerminalBeforeItReportsReady()
    {
        // The WebView2 page installs its message listener on `ready`. Anything written earlier
        // is silently dropped, including the startup screen the auto-answer rules need.
        var terminal = new FakeTerminalController();   // ReadyGate deliberately left uncompleted
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await Task.Delay(400, cts.Token);
        Assert.Empty(terminal.Sent);
        Assert.Empty(terminal.Pasted);

        terminal.ReadyGate.SetResult();
        terminal.QueueSnapshot("bypass permissions mode");
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ANonMatchingRuleSetSendsThePromptPromptlyInsteadOfWaitingOutTheCeiling()
    {
        // Decision D13: exit-on-first-non-match is the NORMAL path. settleTimeoutMs is a ceiling,
        // not a mandatory wait - otherwise every phase of every task would cost an extra minute.
        // The fixture sets settleTimeoutMs to 2000 ms and quietPeriodMs to 10 ms.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.QueueSnapshot("PS C:\\ws>");          // launcher is at its input prompt
        terminal.EmitOutput();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var started = Stopwatch.StartNew();
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        started.Stop();

        Assert.DoesNotContain(terminal.Sent, s => s == "2\r");
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(2),
            $"The prompt took {started.Elapsed} - the loop waited out settleTimeoutMs instead of exiting on the first quiet snapshot.");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task TheSubmitCarriageReturnBelongsToTheSessionThatReceivedThePaste()
    {
        // A reused task folder can satisfy phase 1's watcher immediately (spec section 8.3), so
        // the orchestrator may start phase 2 within the paste's submit delay. The CR must not
        // land in the new launcher, where it would accept `yo`'s preselected "No, exit".
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);   // phase 1 completes at once

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        terminal.QueueSnapshot("PS>");
        terminal.EmitOutput();
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Every paste is fully written - including its CR - before the next session starts.
        Assert.Equal(terminal.Pasted.Count, terminal.SubmitsCompletedBeforeNextSession);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }


    [Fact]
    public async Task AnAutoAnswerRuleFiresAtMostOncePerPhase()
    {
        var terminal = new FakeTerminalController();
        terminal.QueueSnapshot("bypass");
        terminal.QueueSnapshot("bypass");
        terminal.QueueSnapshot("bypass");
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Single(terminal.Sent, s => s == "2\r");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Phases_ChainThroughAllFourStations()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Phase 1 -> spec and plan appear.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // Phase 2 -> review appears.
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);
        Assert.Equal("codex --yolo\r", terminal.Sent.Last(s => s.EndsWith("\r", StringComparison.Ordinal) && s.Contains("codex", StringComparison.Ordinal)));
        WriteArtifacts(_paths.ReviewAbsolute);

        // Phase 3 -> spec content changes.
        await WaitUntil(() => terminal.StartedSessions.Count >= 3, cts.Token);
        await Task.Delay(80, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute);

        // Phase 4 -> waits for the manual signal.
        await WaitUntil(() => terminal.StartedSessions.Count >= 4, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.False(run.IsCompleted);

        signal.Signal();
        await run;

        Assert.Equal(4, terminal.StartedSessions.Count);
        Assert.Equal(4, terminal.Pasted.Count);

        lock (_progress)
        {
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Specification && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Review && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.ResolveReview && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);
        }
    }

    [Fact]
    public async Task ManualSignal_AlsoEndsANonManualPhase()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        signal.Signal();

        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Cancellation_DisposesTheSession()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource();

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, CancellationToken.None);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(terminal.DisposeCount >= 1);
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~BracketedPasteTests|FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: FAIL — `CS0246: The type or namespace name 'BracketedPaste' could not be found`.

- [ ] **Step 3: Write the supporting types**

`Workflow\Services\BracketedPaste.cs`:

```csharp
namespace Workflow.Services;

/// <summary>Builds a bracketed-paste block (DECSET 2004).</summary>
public static class BracketedPaste
{
    private const string Begin = "\u001b[200~";
    private const string End = "\u001b[201~";

    /// <summary>
    /// Wraps multi-line text so a TUI input box treats it as a single paste instead of
    /// submitting on the first newline.
    /// </summary>
    /// <param name="body">The text to paste. All newlines are normalised to CR.</param>
    /// <returns>The bracketed-paste block. The submitting CR is sent separately.</returns>
    public static string Wrap(string body)
    {
        var normalised = (body ?? string.Empty)
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r');

        return Begin + normalised + End;
    }
}
```

`Workflow\Models\PhaseProgress.cs`:

```csharp
namespace Workflow.Models;

/// <summary>A phase's status change, reported to the UI.</summary>
/// <param name="Phase">The phase whose status changed.</param>
/// <param name="Status">The new status.</param>
public sealed record PhaseProgress(WorkflowPhase Phase, PhaseStatus Status);
```

`Workflow\Services\ITerminalController.cs`:

```csharp
namespace Workflow.Services;

/// <summary>
/// Everything the orchestrator is allowed to do to a terminal. Implemented by TerminalViewModel
/// over WebView2 plus ConPtySession, and by a fake in the tests.
/// </summary>
public interface ITerminalController
{
    /// <summary>UTC timestamp of the most recent byte received from the pseudo-console.</summary>
    /// <summary>UTC timestamp of the most recent chunk of PTY output.</summary>
    /// <remarks>
    /// Reset to "now" by <see cref="StartSession"/>. It must never be left at
    /// <see cref="DateTimeOffset.MinValue"/> on a live session: the settle loop would then read
    /// an enormous idle time on its very first iteration, snapshot an empty screen and send the
    /// prompt before the launcher had drawn anything (spec section 7.3, Gate A).
    /// </remarks>
    public DateTimeOffset LastOutputUtc { get; }

    /// <summary>Number of output chunks received on the current session; 0 until the first one.</summary>
    /// <remarks>Gate A uses this to tell "nothing has happened yet" from "the screen is quiet".</remarks>
    public long OutputCount { get; }

    /// <summary>Completes once the terminal page is constructed and listening (spec section 6.4).</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the terminal may be driven.</returns>
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken);

    /// <summary>Disposes any previous session and starts a fresh one.</summary>
    /// <param name="executable">Shell executable.</param>
    /// <param name="arguments">Shell arguments.</param>
    /// <param name="workingDirectory">Initial current directory.</param>
    public void StartSession(string executable, string arguments, string workingDirectory);

    /// <summary>Clears the rendered terminal, used between phases.</summary>
    public void ClearScreen();

    /// <summary>Reads the last rendered rows of the terminal.</summary>
    /// <param name="lines">How many rows to read from the end of the buffer.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rendered text, rows joined by newline.</returns>
    public Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken);

    /// <summary>Writes UTF-8 encoded text to the pseudo-console.</summary>
    /// <param name="text">Text to send verbatim, including any control characters.</param>
    public void Send(string text);

    /// <summary>Sends a bracketed-paste block followed, after a short delay, by a submitting CR.</summary>
    /// <param name="body">The prompt text.</param>
    /// <summary>
    /// Writes a bracketed-paste block and then, after a short delay, the submitting CR.
    /// </summary>
    /// <param name="body">The prompt text.</param>
    /// <param name="cancellationToken">Cancels the pending submit.</param>
    /// <returns>A task that completes once the submitting CR has been written.</returns>
    /// <remarks>
    /// Awaitable and session-scoped on purpose. A fire-and-forget delay that later calls
    /// <c>Send(CR)</c> on whatever session is current can deliver the carriage return into the
    /// *next* phase's launcher - reachable whenever a reused task folder already satisfies a watcher
    /// (spec section 8.3) - where it would accept `yo`'s preselected "No, exit".
    /// </remarks>
    public Task SendPasteAsync(string body, CancellationToken cancellationToken);

    /// <summary>Disposes the current session, killing its process tree.</summary>
    public void DisposeSession();
}
```

`Workflow\Services\ManualPhaseSignal.cs`:

```csharp
namespace Workflow.Services;

/// <summary>
/// Lets the user end the current phase from the UI. This is the escape hatch that prevents a
/// permanent stall when a phase's artefact condition never becomes true.
/// </summary>
public sealed class ManualPhaseSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ends the phase that is currently waiting.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            _source.TrySetResult();
        }
    }

    /// <summary>Waits for the user to signal.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when Signal is called.</returns>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate)
        {
            task = _source.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Rearms the signal for the next phase.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
```

- [ ] **Step 4: Write the orchestrator**

`Workflow\Services\IWorkflowOrchestrator.cs`:

```csharp
using Workflow.Models;

namespace Workflow.Services;

/// <summary>One workflow run: the parameters that do not change between phases.</summary>
/// <param name="Paths">The task's path set.</param>
/// <param name="TaskDescription">Plain-text task description fed into prompt 1.</param>
/// <param name="Terminal">The terminal this run drives.</param>
/// <param name="ManualSignal">Signal raised by the 'Phase abschliessen' / 'Task abschliessen' buttons.</param>
/// <param name="Progress">Receives every phase status change.</param>
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress);

/// <summary>Drives a task through the four phases.</summary>
public interface IWorkflowOrchestrator
{
    /// <summary>Runs all four phases in order.</summary>
    /// <param name="request">Run parameters.</param>
    /// <param name="cancellationToken">Cancels the run and disposes the session.</param>
    /// <returns>A task that completes after the implementation phase is signalled.</returns>
    public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken);
}
```

`Workflow\Services\WorkflowOrchestrator.cs`:

```csharp
using System.Diagnostics;
using Workflow.Models;
using Workflow.Terminal;

namespace Workflow.Services;

/// <inheritdoc cref="IWorkflowOrchestrator" />
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private const int SnapshotLines = 60;

    private readonly IPromptTemplateService _prompts;
    private readonly IAutoAnswerService _autoAnswer;
    private readonly IArtifactWatcherFactory _watchers;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="prompts">Prompt renderer.</param>
    /// <param name="autoAnswer">Auto-answer rule engine.</param>
    /// <param name="watchers">Artefact watcher factory.</param>
    /// <param name="debounce">Debounce applied to file-system notifications.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    public WorkflowOrchestrator(
        IPromptTemplateService prompts,
        IAutoAnswerService autoAnswer,
        IArtifactWatcherFactory watchers,
        TimeSpan debounce,
        TimeSpan pollInterval)
    {
        _prompts = prompts;
        _autoAnswer = autoAnswer;
        _watchers = watchers;
        _debounce = debounce;
        _pollInterval = pollInterval;
    }

    /// <inheritdoc />
    public async Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            foreach (var definition in PhaseCatalog.All)
            {
                await RunPhaseAsync(request, definition, cancellationToken);
            }
        }
        finally
        {
            request.Terminal.DisposeSession();
        }
    }

    private async Task RunPhaseAsync(
        WorkflowRunRequest request,
        PhaseDefinition definition,
        CancellationToken cancellationToken)
    {
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Active));
        request.ManualSignal.Reset();

        // The baseline for AnyContentChanged must be captured before the CLI can touch anything.
        using var watcher = _watchers.Create(
            definition.Completion,
            request.Paths.TaskDirectory,
            WatchedPaths(request.Paths, definition),
            _debounce,
            _pollInterval);

        request.Terminal.ClearScreen();
        request.Terminal.StartSession(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            request.Paths.WorkingDirectory);

        // The page must be listening before anything is written to it, or `clear`, the first
        // PTY bytes and the first snapshot are all dropped - and the launcher's startup screen
        // is exactly what the auto-answer rules need to match (spec section 6.4).
        await request.Terminal.WaitUntilReadyAsync(cancellationToken);

        // Always quoted: working directories contain spaces and umlauts.
        request.Terminal.Send($"cd \"{request.Paths.WorkingDirectory}\"\r");
        request.Terminal.Send($"{definition.Launcher}\r");

        await SettleAndAnswerAsync(request.Terminal, cancellationToken);

        var variables = PromptVariables.For(request.Paths, request.TaskDescription);
        // Awaited, including the submitting CR, and scoped to THIS session: a detached delay
        // can deliver the CR into the next phase’s launcher whenever a reused task folder
        // already satisfies a watcher (spec sections 6.5 and 8.3).
        await request.Terminal.SendPasteAsync(
            _prompts.Render(definition.PromptFile, variables),
            cancellationToken);

        var completion = definition.Completion == CompletionRule.Manual
            ? request.ManualSignal.WaitAsync(cancellationToken)
            : await Task.WhenAny(
                watcher.WaitAsync(cancellationToken),
                request.ManualSignal.WaitAsync(cancellationToken));

        await completion;

        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
    }

    // Keyed on the phase, not the completion rule: phases 1 and 3 share the FilesExist/
    // AnyContentChanged rules but phase 2 watches a different artefact.
    private static IReadOnlyList<string> WatchedPaths(TaskPaths paths, PhaseDefinition definition) =>
        definition.Phase switch
        {
            WorkflowPhase.Specification => [paths.SpecAbsolute, paths.PlanAbsolute],
            WorkflowPhase.Review => [paths.ReviewAbsolute],
            WorkflowPhase.ResolveReview => [paths.SpecAbsolute, paths.PlanAbsolute],
            _ => [],
        };

    private async Task SettleAndAnswerAsync(ITerminalController terminal, CancellationToken cancellationToken)
    {
        var configuration = _autoAnswer.RuleSet;
        var fired = new HashSet<string>(StringComparer.Ordinal);
        var quietPeriod = TimeSpan.FromMilliseconds(configuration.QuietPeriodMs);
        var overall = Stopwatch.StartNew();

        while (overall.Elapsed < TimeSpan.FromMilliseconds(configuration.SettleTimeoutMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // --- Gate A: the launcher must actually have rendered something. ----------------
            // Without this, the first iteration of a fresh session finds an "idle" terminal
            // (nothing has been written yet), snapshots an EMPTY xterm buffer, matches no rule,
            // and sends the prompt straight into the still-blocking bypass-permissions dialog -
            // whose default option is "No, exit". Quietness only means anything once the
            // launcher has drawn. See spec section 7.3.
            if (terminal.OutputCount == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                continue;
            }

            // --- Gate B: the screen must then stop changing. --------------------------------
            if (DateTimeOffset.UtcNow - terminal.LastOutputUtc < quietPeriod)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);

            // A quiet terminal that still renders nothing has not finished painting. Treat it as
            // "not ready yet", not as "settled with nothing to answer".
            if (string.IsNullOrWhiteSpace(screen))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var rule = _autoAnswer.Match(screen, fired);

            // Settled with nothing left to answer: send the prompt NOW. Waiting out
            // SettleTimeoutMs here would add a minute to every phase of every task - this is the
            // normal exit from the loop, not the exceptional one (spec section 7.3, D13).
            if (rule is null || fired.Count >= configuration.MaxAnswersPerPhase)
            {
                return;
            }

            terminal.Send(rule.Send);
            fired.Add(rule.Id);

            await Task.Delay(quietPeriod, cancellationToken);
        }

        // Ceiling reached: the launcher never rendered, or rules kept matching. The prompt is
        // sent anyway and the user can intervene in the live terminal.
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~BracketedPasteTests|FullyQualifiedName~WorkflowOrchestratorTests"`
Expected: PASS — 16 passed.

> Existing orchestrator tests must be updated for the new contract: each now has to call
> `terminal.ReadyGate.SetResult()` and `terminal.EmitOutput()` (and queue a non-empty snapshot)
> before the orchestrator writes anything. That is the point — the old tests passed against a
> fake that queued its warning *before* startup and so never exercised the production race that
> Gate A exists to close.

- [ ] **Step 6: Commit**

```
git add -A
git commit -m "feat(orchestrator): four-phase state machine with settle-and-answer loop"
```

---

## Task 10: Terminal web assets

**Files:**
- Create: `Workflow\Assets\Terminal\terminal.html`, `Workflow\Assets\Terminal\terminal.js`, `Workflow\Assets\Terminal\xterm.js`, `Workflow\Assets\Terminal\xterm.css`, `Workflow\Assets\Terminal\addon-fit.js`
- Test: `Workflow.Tests\TerminalAssetTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a page reachable at `https://workflow.terminal/terminal.html` that exposes
  `window.wfSnapshot(n) -> string` and speaks the message protocol below.

**Message protocol (normative — Task 11 depends on it exactly):**

| Direction | Message | Meaning |
|---|---|---|
| C# → JS | `{"type":"out","b64":"..."}` | base64 PTY bytes to render |
| C# → JS | `{"type":"clear"}` | reset the terminal |
| JS → C# | `{"type":"ready"}` | xterm constructed; C# may start the PTY |
| JS → C# | `{"type":"in","b64":"..."}` | base64 UTF-8 keystrokes |
| JS → C# | `{"type":"resize","cols":n,"rows":n}` | FitAddon result |

- [ ] **Step 1: Vendor xterm.js**

Assets are vendored, not CDN-loaded: the WebView2 is navigated to a virtual host and must work
offline (the CSP of a virtual host also blocks external scripts).

```bash
cd "C:/Users/Marco/Documents/repo/Workflow/Workflow/Assets/Terminal"
npm pack @xterm/xterm@5.5.0
npm pack @xterm/addon-fit@0.10.0
tar -xzf xterm-xterm-5.5.0.tgz
tar -xzf xterm-addon-fit-0.10.0.tgz
cp package/lib/xterm.js ./xterm.js
cp package/css/xterm.css ./xterm.css
```

The two archives both unpack to `package/`, so extract and copy one at a time. `addon-fit`'s
bundle is `package/lib/addon-fit.js`. Delete the `.tgz` files and the `package/` folders when
done; only the five files listed under **Files** may remain.

Verify: `ls` shows exactly `addon-fit.js`, `terminal.html`, `terminal.js`, `xterm.css`, `xterm.js`.

- [ ] **Step 2: Write `terminal.html`**

```html
<!DOCTYPE html>
<html lang="de">
<head>
  <meta charset="utf-8" />
  <title>Workflow Terminal</title>
  <link rel="stylesheet" href="xterm.css" />
  <style>
    html, body { margin: 0; padding: 0; height: 100%; background: #1e1e1e; overflow: hidden; }
    #terminal { width: 100%; height: 100%; }
  </style>
</head>
<body>
  <div id="terminal"></div>
  <script src="xterm.js"></script>
  <script src="addon-fit.js"></script>
  <script src="terminal.js"></script>
</body>
</html>
```

- [ ] **Step 3: Write `terminal.js`**

```javascript
(function () {
  'use strict';

  var encoder = new TextEncoder();
  var decoder = new TextDecoder();

  var term = new Terminal({
    allowProposedApi: true,
    convertEol: false,
    cursorBlink: true,
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 13,
    scrollback: 5000,
    theme: {
      background: '#1e1e1e',
      foreground: '#e0e0e0',
      cursor: '#7986cb',
      selectionBackground: '#3949ab'
    }
  });

  var fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.open(document.getElementById('terminal'));

  function post(message) {
    window.chrome.webview.postMessage(JSON.stringify(message));
  }

  function toBase64(bytes) {
    var binary = '';
    for (var i = 0; i < bytes.length; i++) {
      binary += String.fromCharCode(bytes[i]);
    }
    return window.btoa(binary);
  }

  function fromBase64(b64) {
    var binary = window.atob(b64);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  }

  // Keystrokes travel as UTF-8 bytes so the C# side never has to guess an encoding.
  term.onData(function (data) {
    post({ type: 'in', b64: toBase64(encoder.encode(data)) });
  });

  term.onResize(function (size) {
    post({ type: 'resize', cols: size.cols, rows: size.rows });
  });

  var resizeTimer = null;
  function scheduleFit() {
    if (resizeTimer !== null) {
      window.clearTimeout(resizeTimer);
    }
    // Debounced: the pty must be able to answer one resize before the next arrives.
    resizeTimer = window.setTimeout(function () {
      resizeTimer = null;
      try {
        fitAddon.fit();
      } catch (e) {
        // The element is not laid out yet; the next resize will retry.
      }
    }, 80);
  }

  window.addEventListener('resize', scheduleFit);

  window.chrome.webview.addEventListener('message', function (event) {
    var message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;

    if (message.type === 'out') {
      term.write(fromBase64(message.b64));
    } else if (message.type === 'clear') {
      term.reset();
    }
  });

  // Reads the rendered screen, not the raw byte stream: the auto-answer rules must match what a
  // human sees, not a soup of escape sequences.
  window.wfSnapshot = function (n) {
    var buffer = term.buffer.active;
    var start = Math.max(0, buffer.length - n);
    var lines = [];
    for (var i = start; i < buffer.length; i++) {
      var line = buffer.getLine(i);
      if (line) {
        lines.push(line.translateToString(true));
      }
    }
    return lines.join('\n');
  };

  scheduleFit();
  post({ type: 'ready' });
}());
```

- [ ] **Step 4: Write the asset presence test**

`Workflow.Tests\TerminalAssetTests.cs`:

```csharp
using System.IO;

namespace Workflow.Tests;

public class TerminalAssetTests
{
    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal", name);

    [Theory]
    [InlineData("terminal.html")]
    [InlineData("terminal.js")]
    [InlineData("xterm.js")]
    [InlineData("xterm.css")]
    [InlineData("addon-fit.js")]
    public void Asset_IsCopiedToTheOutputDirectory(string name)
    {
        var path = AssetPath(name);

        Assert.True(File.Exists(path), $"Missing terminal asset: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Empty terminal asset: {path}");
    }

    [Fact]
    public void TerminalHtml_ReferencesTheVendoredScriptsAndNoExternalHost()
    {
        var html = File.ReadAllText(AssetPath("terminal.html"));

        Assert.Contains("src=\"xterm.js\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"addon-fit.js\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"terminal.js\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerminalJs_ImplementsTheAgreedMessageProtocol()
    {
        var js = File.ReadAllText(AssetPath("terminal.js"));

        Assert.Contains("window.wfSnapshot", js, StringComparison.Ordinal);
        Assert.Contains("'ready'", js, StringComparison.Ordinal);
        Assert.Contains("'in'", js, StringComparison.Ordinal);
        Assert.Contains("'resize'", js, StringComparison.Ordinal);
        Assert.Contains("'out'", js, StringComparison.Ordinal);
        Assert.Contains("'clear'", js, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 5: Copy the assets into the test output**

Add to the `Content` `ItemGroup` in `Workflow.Tests\Workflow.Tests.csproj`:

```xml
    <Content Include="..\Workflow\Assets\Terminal\**\*.*" LinkBase="Assets\Terminal">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TerminalAssetTests"`
Expected: PASS — 7 passed.

- [ ] **Step 7: Commit**

```
git add -A
git commit -m "feat(terminal): vendor xterm.js 5.5.0 and add the WebView2 terminal page"
```

---

## Task 11: WebView2 terminal host

**Files:**
- Create: `Workflow\Services\IWebViewEnvironmentProvider.cs`, `Workflow\Services\WebViewEnvironmentProvider.cs`, `Workflow\ViewModels\TerminalViewModel.cs`, `Workflow\Views\TerminalView.xaml`, `Workflow\Views\TerminalView.xaml.cs`
- Test: manual (see Step 6). WebView2 needs a message pump and a real browser process; mocking it would test the mock.

**Interfaces:**
- Consumes: `ITerminalSession`, `ITerminalSessionFactory` (Task 8); `ITerminalController`, `BracketedPaste` (Task 9); the JS message protocol (Task 10).
- Produces:
  - `interface IWebViewEnvironmentProvider` with `Task<CoreWebView2Environment> GetAsync()`
  - `sealed partial class TerminalViewModel : ObservableObject, ITerminalController, IDisposable` with
    `string InputText { get; set; }`, `IAsyncRelayCommand SendInputCommand`,
    `bool IsTerminalAvailable { get; }`, `string? TerminalErrorMessage { get; }`,
    and `Task AttachAsync(WebView2 webView)`
  - `TerminalView` (a `UserControl`) that calls `AttachAsync` on load

**Constraints specific to this task:**
- **One `CoreWebView2Environment` for the whole application.** Creating one per control is a documented source of `WebView2` initialisation failures because they fight over the user-data folder.
- **Output uses `PostWebMessageAsString`, never `ExecuteScriptAsync`.** The output path is high-frequency; script-source escaping would dominate the cost. `ExecuteScriptAsync` is used only for the low-frequency `wfSnapshot` call.
- **Coalesce output on a 16 ms timer.** A burst of PTY reads must become one message.

- [ ] **Step 1: Write the shared environment provider**

`Workflow\Services\IWebViewEnvironmentProvider.cs`:

```csharp
using Microsoft.Web.WebView2.Core;

namespace Workflow.Services;

/// <summary>Supplies the single CoreWebView2Environment shared by every terminal.</summary>
public interface IWebViewEnvironmentProvider
{
    /// <summary>Creates the environment on first call and returns the same instance afterwards.</summary>
    /// <returns>The shared environment.</returns>
    public Task<CoreWebView2Environment> GetAsync();
}
```

`Workflow\Services\WebViewEnvironmentProvider.cs`:

```csharp
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Workflow.Services;

/// <inheritdoc cref="IWebViewEnvironmentProvider" />
public sealed class WebViewEnvironmentProvider : IWebViewEnvironmentProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreWebView2Environment? _environment;

    /// <inheritdoc />
    public async Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await _gate.WaitAsync();
        try
        {
            // One environment for the whole process: multiple environments fight over the
            // user-data folder and WebView2 initialisation then fails intermittently.
            _environment ??= await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Workflow",
                    "WebView2"),
                options: null);

            return _environment;
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

- [ ] **Step 2: Write `TerminalViewModel`**

`Workflow\ViewModels\TerminalViewModel.cs`:

```csharp
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Workflow.Services;
using Workflow.Terminal;

namespace Workflow.ViewModels;

/// <summary>Hosts one xterm.js terminal in a WebView2 and drives one pseudo-console.</summary>
public sealed partial class TerminalViewModel : ObservableObject, ITerminalController, IDisposable
{
    private const string VirtualHost = "workflow.terminal";
    private const string CarriageReturn = "\r";
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    private readonly IWebViewEnvironmentProvider _environmentProvider;
    private readonly ITerminalSessionFactory _sessionFactory;
    private readonly Dispatcher _dispatcher;
    private readonly List<byte> _pending = [];
    private readonly object _pendingGate = new();

    private DispatcherTimer? _flushTimer;
    private WebView2? _webView;
    private ITerminalSession? _session;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Cancels a pending paste submit when the session it belonged to is replaced or disposed.
    private CancellationTokenSource _sessionLifetime = new();
    private long _outputCount;
    private int _columns = 120;
    private int _rows = 30;
    private int _disposed;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isTerminalAvailable;

    [ObservableProperty]
    private string? _terminalErrorMessage;

    /// <summary>Creates the view model.</summary>
    /// <param name="environmentProvider">Shared WebView2 environment.</param>
    /// <param name="sessionFactory">Creates pseudo-console sessions.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    public TerminalViewModel(
        IWebViewEnvironmentProvider environmentProvider,
        ITerminalSessionFactory sessionFactory,
        Dispatcher dispatcher)
    {
        _environmentProvider = environmentProvider;
        _sessionFactory = sessionFactory;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Seeded to "now", never to <see cref="DateTimeOffset.MinValue"/>. With MinValue the settle
    /// loop would compute an enormous idle time on its first iteration, decide the terminal was
    /// quiet before the launcher had drawn anything, snapshot an empty buffer and send the prompt
    /// into a still-blocking confirmation dialog (spec section 7.3, Gate A).
    /// </remarks>
    public DateTimeOffset LastOutputUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public long OutputCount => Interlocked.Read(ref _outputCount);

    /// <inheritdoc />
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    /// <summary>Initialises the WebView2 and navigates it to the terminal page.</summary>
    /// <param name="webView">The control from TerminalView.xaml.</param>
    public async Task AttachAsync(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);

        // Re-selecting a tab raises Loaded again on a still-attached view model (spec section
        // 12.4). Re-navigating here would blank a live terminal and start a second flush timer.
        if (ReferenceEquals(_webView, webView) && webView.CoreWebView2 is not null)
        {
            return;
        }

        _webView = webView;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var environment = await _environmentProvider.GetAsync();
            await webView.EnsureCoreWebView2Async(environment);

            var core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"),
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.WebMessageReceived += OnWebMessageReceived;
            core.Navigate($"https://{VirtualHost}/terminal.html");

            _flushTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = CoalesceInterval,
            };
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            // `ready` is a PRECONDITION, not a notification. Until terminal.js has installed its
            // message listener, `clear`, PTY output and snapshot requests are all dropped - and
            // the launcher startup screen is exactly what the auto-answer rules must match.
            // IsTerminalAvailable therefore flips AFTER the handshake, never after Navigate.
            await _ready.Task.WaitAsync(ReadyTimeout);

            IsTerminalAvailable = true;
        }
        catch (TimeoutException)
        {
            IsTerminalAvailable = false;
            TerminalErrorMessage =
                "Die Terminal-Oberflaeche hat sich nicht innerhalb von 10 Sekunden gemeldet. " +
                "Bitte den Tab schliessen und neu oeffnen.";
            _ready.TrySetResult();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            IsTerminalAvailable = false;
            TerminalErrorMessage =
                "Die WebView2-Laufzeit wurde nicht gefunden. Bitte den 'Evergreen WebView2 Runtime' " +
                "von https://developer.microsoft.com/microsoft-edge/webview2/ installieren.";
            _ready.TrySetResult();
        }
        catch (InvalidOperationException ex)
        {
            IsTerminalAvailable = false;
            TerminalErrorMessage = $"Das Terminal konnte nicht initialisiert werden: {ex.Message}";
            _ready.TrySetResult();
        }
    }

    /// <inheritdoc />
    public void StartSession(string executable, string arguments, string workingDirectory)
    {
        DisposeSession();

        // A fresh session has produced nothing yet, and its idle clock starts now. Both are read
        // by the settle loop's Gate A.
        Interlocked.Exchange(ref _outputCount, 0);
        LastOutputUtc = DateTimeOffset.UtcNow;

        var session = _sessionFactory.Create();
        session.OutputReceived += OnOutputReceived;
        session.Start(executable, arguments, workingDirectory, _columns, _rows);
        _session = session;
    }

    /// <inheritdoc />
    public void ClearScreen() => PostToPage(new { type = "clear" });

    /// <inheritdoc />
    public async Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken)
    {
        if (_webView?.CoreWebView2 is null)
        {
            return string.Empty;
        }

        // ExecuteScriptAsync is only used here: snapshots are low-frequency, output is not.
        var json = await _dispatcher.InvokeAsync(
            () => _webView.CoreWebView2.ExecuteScriptAsync($"window.wfSnapshot({lines})"),
            DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap();

        if (string.IsNullOrEmpty(json) || json == "null")
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public void Send(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _session?.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <inheritdoc />
    public async Task SendPasteAsync(string body, CancellationToken cancellationToken)
    {
        // Capture the session this paste belongs to. The orchestrator can advance inside the
        // submit delay whenever a reused task folder already satisfies a watcher (spec section
        // 8.3); the carriage return must then be dropped rather than written into the NEXT
        // launcher, where it would accept the preselected "No, exit".
        var target = _session;
        if (target is null)
        {
            return;
        }

        Write(target, BracketedPaste.Wrap(body));

        using var scope =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionLifetime.Token);

        try
        {
            // A separate write: inside the paste block the TUI input box would treat the
            // carriage return as a literal newline instead of as submit.
            await Task.Delay(SubmitDelay, scope.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The session was replaced while we waited. Dropping the submit is the point.
            return;
        }

        if (!ReferenceEquals(_session, target))
        {
            return;
        }

        Write(target, CarriageReturn);
    }

    private static void Write(ITerminalSession session, string text) =>
        session.Write(Encoding.UTF8.GetBytes(text));

    /// <inheritdoc />
    public void DisposeSession()
    {
        // Cancel any paste submit still waiting out its delay for the outgoing session, so its
        // carriage return can never reach the session that replaces it (spec section 6.5).
        var lifetime = Interlocked.Exchange(ref _sessionLifetime, new CancellationTokenSource());
        lifetime.Cancel();
        lifetime.Dispose();

        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= OnOutputReceived;
        session.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_flushTimer is not null)
        {
            _flushTimer.Tick -= OnFlushTick;
            _flushTimer.Stop();
            _flushTimer = null;
        }

        DisposeSession();

        if (_webView?.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }

        _webView?.Dispose();
        _webView = null;
    }

    [RelayCommand]
    private void SendInput()
    {
        if (string.IsNullOrEmpty(InputText))
        {
            return;
        }

        Send(InputText + "\r");
        InputText = string.Empty;
    }

    private void OnOutputReceived(object? sender, ReadOnlyMemory<byte> bytes)
    {
        LastOutputUtc = DateTimeOffset.UtcNow;

        // Gate A in the settle loop needs to tell "the launcher has not drawn yet" apart from
        // "the screen has gone quiet"; a counter is the cheapest way to say so.
        Interlocked.Increment(ref _outputCount);

        lock (_pendingGate)
        {
            _pending.AddRange(bytes.Span);
        }
    }

    private void OnFlushTick(object? sender, EventArgs e)
    {
        byte[] payload;

        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            payload = [.. _pending];
            _pending.Clear();
        }

        PostToPage(new { type = "out", b64 = Convert.ToBase64String(payload) });
    }

    private void PostToPage(object message)
    {
        if (_webView?.CoreWebView2 is null)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(e.TryGetWebMessageAsString());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "ready":
                    // Non-nullable: a null-conditional here would be dead code (CA1508).
                    _ready.TrySetResult();
                    break;

                case "in":
                    if (document.RootElement.TryGetProperty("b64", out var input))
                    {
                        _session?.Write(Convert.FromBase64String(input.GetString() ?? string.Empty));
                    }

                    break;

                case "resize":
                    if (document.RootElement.TryGetProperty("cols", out var cols) &&
                        document.RootElement.TryGetProperty("rows", out var rows))
                    {
                        _columns = Math.Max(1, cols.GetInt32());
                        _rows = Math.Max(1, rows.GetInt32());
                        _session?.Resize(_columns, _rows);
                    }

                    break;

                default:
                    break;
            }
        }
    }
}
```

- [ ] **Step 3: Write `TerminalView.xaml`**

```xml
<UserControl x:Class="Workflow.Views.TerminalView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
             xmlns:vm="clr-namespace:Workflow.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:TerminalViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <wv2:WebView2 x:Name="TerminalWebView"
                      Grid.Row="0"
                      Visibility="{Binding IsTerminalAvailable, Converter={StaticResource BooleanToVisibilityConverter}}" />

        <TextBlock Grid.Row="0"
                   Margin="24"
                   TextWrapping="Wrap"
                   VerticalAlignment="Center"
                   HorizontalAlignment="Center"
                   Foreground="{DynamicResource MaterialDesignBody}"
                   Text="{Binding TerminalErrorMessage}"
                   Visibility="{Binding IsTerminalAvailable, Converter={StaticResource InverseBooleanToVisibilityConverter}}" />

        <DockPanel Grid.Row="1" Margin="0,8,0,0" LastChildFill="True">
            <Button DockPanel.Dock="Right"
                    Margin="8,0,0,0"
                    Style="{StaticResource MaterialDesignIconButton}"
                    ToolTip="An das Terminal senden"
                    Command="{Binding SendInputCommand}">
                <materialDesign:PackIcon Kind="Send" />
            </Button>

            <TextBox materialDesign:HintAssist.Hint="Eingabe an das Terminal … (Enter zum Senden)"
                     Style="{StaticResource MaterialDesignOutlinedTextBox}"
                     Text="{Binding InputText, UpdateSourceTrigger=PropertyChanged}">
                <TextBox.InputBindings>
                    <KeyBinding Key="Return" Command="{Binding SendInputCommand}" />
                </TextBox.InputBindings>
            </TextBox>
        </DockPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 4: Write `TerminalView.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using Workflow.ViewModels;

namespace Workflow.Views;

/// <summary>Hosts the xterm.js terminal and the single-line input box.</summary>
public partial class TerminalView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Idempotent: a TabControl hosts the selected item in ONE content host, so re-selecting
        // this tab raises Loaded again on a view model that is already attached and running.
        if (DataContext is TerminalViewModel viewModel)
        {
            await viewModel.AttachAsync(TerminalWebView);
        }
    }
}
```

> **There is deliberately no `Unloaded` handler.** Disposing the view model there would kill a
> *running* task's pseudo-console and WebView2 the moment the user selected a different tab,
> because a `TabControl` unloads the previously selected content on every switch. Re-selecting the
> tab would then call `AttachAsync` on a disposed view model. That breaks the core promise of F2
> and F16 — tabs are independent and keep running — for the single most ordinary interaction in
> the app. Ownership belongs to `MainWindowViewModel.CloseTab` (which disposes the tab it removed)
> and `ShutdownAll`; see spec §12.4 and F19.

> `async void` is correct for a WPF event handler and is the only place in this codebase where it
> is allowed.

> `AttachAsync` must therefore be idempotent: if `_webView` is already this control and the
> WebView2 is initialised, it returns immediately instead of navigating a second time, creating a
> second flush timer, or resetting the `ready` gate on a live terminal.

- [ ] **Step 5: Build**

Run: `dotnet build Workflow.sln -c Debug`
Expected: 0 warnings, 0 errors. `BooleanToVisibilityConverter` and
`InverseBooleanToVisibilityConverter` are registered in Task 12; until then the XAML will not
resolve them, so run this step **after** Task 12 if you are executing out of order.

- [ ] **Step 6: Manual verification (deferred to Task 17)**

The terminal cannot be exercised until `MainWindow` hosts it. Record here that verification steps
V5–V8 in Task 17 cover this file. Do not claim the terminal works before then.

- [ ] **Step 7: Commit**

```
git add -A
git commit -m "feat(terminal): WebView2 host implementing ITerminalController"
```

---

## Task 12: Converters, behaviours and styles

**Files:**
- Create: `Workflow\Converters\PhaseStatusToBrushConverter.cs`, `Workflow\Converters\PhaseStatusToIconKindConverter.cs`, `Workflow\Converters\InverseBooleanToVisibilityConverter.cs`, `Workflow\Behaviors\RichTextBoxAssist.cs`
- Test: `Workflow.Tests\ConverterTests.cs`, `Workflow.Tests\RichTextBoxAssistTests.cs`

**Interfaces:**
- Consumes: `PhaseStatus` (Task 2).
- Produces:
  - `PhaseStatusToBrushConverter : IValueConverter` — `Pending` → `#9E9E9E`, `Active` → `#FBC02D`, `Completed` → `#43A047`
  - `PhaseStatusToIconKindConverter : IValueConverter` — `Pending` → `PackIconKind.CircleOutline`, `Active` → `PackIconKind.ProgressClock`, `Completed` → `PackIconKind.CheckCircle`
  - `InverseBooleanToVisibilityConverter : IValueConverter`
  - `static class RichTextBoxAssist` with attached property `PlainText` (two-way, `BindsTwoWayByDefault`)

**Constraints specific to this task:**
- `Nullable=enable` plus `TreatWarningsAsErrors` means an `IValueConverter` must be declared exactly as `object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)`. Any non-nullable parameter is a `CS8767` build error, because WPF passes `null` and `DependencyProperty.UnsetValue`.
- The `dotnet-upgrade/skills/migrate-nullable-references` skill covers this boundary specifically.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\ConverterTests.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Workflow.Converters;
using Workflow.Models;

namespace Workflow.Tests;

public class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(PhaseStatus.Pending, "#FF9E9E9E")]
    [InlineData(PhaseStatus.Active, "#FFFBC02D")]
    [InlineData(PhaseStatus.Completed, "#FF43A047")]
    public void PhaseStatusToBrush_MapsEveryStatus(PhaseStatus status, string expected)
    {
        var converter = new PhaseStatusToBrushConverter();

        var brush = Assert.IsType<SolidColorBrush>(converter.Convert(status, typeof(Brush), null, Culture));

        Assert.Equal(expected, brush.Color.ToString(Culture));
    }

    [Fact]
    public void PhaseStatusToBrush_FallsBackToGreyForNullAndUnset()
    {
        var converter = new PhaseStatusToBrushConverter();

        var fromNull = Assert.IsType<SolidColorBrush>(converter.Convert(null, typeof(Brush), null, Culture));
        var fromUnset = Assert.IsType<SolidColorBrush>(
            converter.Convert(DependencyProperty.UnsetValue, typeof(Brush), null, Culture));

        Assert.Equal("#FF9E9E9E", fromNull.Color.ToString(Culture));
        Assert.Equal("#FF9E9E9E", fromUnset.Color.ToString(Culture));
    }

    [Theory]
    [InlineData(PhaseStatus.Pending, PackIconKind.CircleOutline)]
    [InlineData(PhaseStatus.Active, PackIconKind.ProgressClock)]
    [InlineData(PhaseStatus.Completed, PackIconKind.CheckCircle)]
    public void PhaseStatusToIconKind_MapsEveryStatus(PhaseStatus status, PackIconKind expected)
    {
        var converter = new PhaseStatusToIconKindConverter();

        Assert.Equal(expected, converter.Convert(status, typeof(PackIconKind), null, Culture));
    }

    [Fact]
    public void PhaseStatusToIconKind_FallsBackForNull()
    {
        var converter = new PhaseStatusToIconKindConverter();

        Assert.Equal(PackIconKind.CircleOutline, converter.Convert(null, typeof(PackIconKind), null, Culture));
    }

    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    public void InverseBooleanToVisibility_Inverts(bool value, Visibility expected)
    {
        var converter = new InverseBooleanToVisibilityConverter();

        Assert.Equal(expected, converter.Convert(value, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void InverseBooleanToVisibility_TreatsNullAsFalse()
    {
        var converter = new InverseBooleanToVisibilityConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(null, typeof(Visibility), null, Culture));
    }

    [Fact]
    public void ConvertBack_IsNotSupportedOnAnyConverter()
    {
        Assert.Throws<NotSupportedException>(
            () => new PhaseStatusToBrushConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotSupportedException>(
            () => new PhaseStatusToIconKindConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotSupportedException>(
            () => new InverseBooleanToVisibilityConverter().ConvertBack(null, typeof(object), null, Culture));
    }
}
```

`Workflow.Tests\RichTextBoxAssistTests.cs`:

```csharp
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Workflow.Behaviors;

namespace Workflow.Tests;

public class RichTextBoxAssistTests
{
    private static string ReadDocument(RichTextBox box) =>
        new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    [StaFact]
    public void SettingPlainText_FillsTheDocument()
    {
        var box = new RichTextBox();

        RichTextBoxAssist.SetPlainText(box, "hello");

        Assert.Equal("hello", ReadDocument(box));
    }

    [StaFact]
    public void SettingPlainText_PreservesMultipleLines()
    {
        var box = new RichTextBox();

        RichTextBoxAssist.SetPlainText(box, "one\ntwo\nthree");

        Assert.Equal(3, box.Document.Blocks.Count);
        Assert.Contains("one", ReadDocument(box), StringComparison.Ordinal);
        Assert.Contains("two", ReadDocument(box), StringComparison.Ordinal);
        Assert.Contains("three", ReadDocument(box), StringComparison.Ordinal);
    }

    // The defect this pins: setting PlainText to string.Empty is setting it to its own DEFAULT,
    // so the property-changed callback does not fire. If TextChanged were wired only from there,
    // this test would read "" and phase 1 would render an empty {taskbeschreibung}.
    [StaFact]
    public void EditingTheDocument_PushesPlainTextBack()
    {
        var box = new RichTextBox();
        RichTextBoxAssist.SetPlainText(box, string.Empty);

        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run("typed")));

        Assert.Equal("typed", RichTextBoxAssist.GetPlainText(box));
    }

    [StaFact]
    public void EditingTheDocument_ReachesAViewModelThroughARealTwoWayBinding()
    {
        // The end-to-end path that actually matters: TaskTabViewModel.TaskDescription starts
        // empty, the user types, and the prompt renderer must see the text. Exercising the
        // attached property alone would not catch a binding that SetValue had detached.
        var source = new TextHolder();
        var box = new RichTextBox();

        BindingOperations.SetBinding(
            box,
            RichTextBoxAssist.PlainTextProperty,
            new Binding(nameof(TextHolder.Text))
            {
                Source = source,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });

        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run("typed")));

        Assert.Equal("typed", source.Text);

        // And the binding survives the write-back, so the reverse direction still works.
        source.Text = "from the view model";
        Assert.Equal("from the view model", ReadDocument(box));
    }

    private sealed class TextHolder : INotifyPropertyChanged
    {
        private string _text = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Text
        {
            get => _text;
            set
            {
                if (string.Equals(_text, value, StringComparison.Ordinal))
                {
                    return;
                }

                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }

    [StaFact]
    public void SettingTheSameValueTwice_DoesNotRecurse()
    {
        var box = new RichTextBox();

        RichTextBoxAssist.SetPlainText(box, "stable");
        RichTextBoxAssist.SetPlainText(box, "stable");

        Assert.Equal("stable", ReadDocument(box));
    }

    [StaFact]
    public void SettingNull_ClearsTheDocument()
    {
        var box = new RichTextBox();
        RichTextBoxAssist.SetPlainText(box, "something");

        RichTextBoxAssist.SetPlainText(box, null);

        Assert.Equal(string.Empty, ReadDocument(box));
    }
}
```

`[StaFact]` comes from `Xunit.StaFact`. Add to `Workflow.Tests\Workflow.Tests.csproj`:

```xml
    <PackageReference Include="Xunit.StaFact" Version="1.1.11" />
```

and add `global using Xunit;` is already present — `StaFact` lives in the same `Xunit` namespace.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ConverterTests|FullyQualifiedName~RichTextBoxAssistTests"`
Expected: FAIL — `CS0246: The type or namespace name 'PhaseStatusToBrushConverter' could not be found`.

- [ ] **Step 3: Write the converters**

`Workflow\Converters\PhaseStatusToBrushConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Workflow.Models;

namespace Workflow.Converters;

/// <summary>Maps a phase status to its indicator colour: grey, yellow, green.</summary>
public sealed class PhaseStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Pending = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush Active = Freeze(Color.FromRgb(0xFB, 0xC0, 0x2D));
    private static readonly SolidColorBrush Completed = Freeze(Color.FromRgb(0x43, 0xA0, 0x47));

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PhaseStatus.Active => Active,
            PhaseStatus.Completed => Completed,
            _ => Pending,
        };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
```

`Workflow\Converters\PhaseStatusToIconKindConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;
using Workflow.Models;

namespace Workflow.Converters;

/// <summary>Maps a phase status to its Material Design icon.</summary>
public sealed class PhaseStatusToIconKindConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PhaseStatus.Active => PackIconKind.ProgressClock,
            PhaseStatus.Completed => PackIconKind.CheckCircle,
            _ => PackIconKind.CircleOutline,
        };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

`Workflow\Converters\InverseBooleanToVisibilityConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Workflow.Converters;

/// <summary>True collapses the element; false or null shows it.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 4: Write `RichTextBoxAssist`**

`Workflow\Behaviors\RichTextBoxAssist.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Workflow.Behaviors;

/// <summary>
/// Two-way binding between a RichTextBox's FlowDocument and a plain string. The requirement asks
/// for a rich text field; the prompt consumes plain text, so formatting is flattened at this
/// boundary. That is the point: pasting from Word, Notion or a browser yields clean text.
/// </summary>
public static class RichTextBoxAssist
{
    /// <summary>Identifies the PlainText attached property.</summary>
    public static readonly DependencyProperty PlainTextProperty =
        DependencyProperty.RegisterAttached(
            "PlainText",
            typeof(string),
            typeof(RichTextBoxAssist),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPlainTextChanged,
                CoercePlainText));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating", typeof(bool), typeof(RichTextBoxAssist), new PropertyMetadata(false));

    /// <summary>Reads the plain text of a RichTextBox.</summary>
    /// <param name="element">The RichTextBox.</param>
    /// <returns>The current plain text.</returns>
    public static string GetPlainText(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return (string)element.GetValue(PlainTextProperty);
    }

    /// <summary>Writes the plain text of a RichTextBox.</summary>
    /// <param name="element">The RichTextBox.</param>
    /// <param name="value">The text to show.</param>
    public static void SetPlainText(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);

        element.SetValue(PlainTextProperty, value ?? string.Empty);
    }

    // A property-changed callback only runs when the EFFECTIVE VALUE CHANGES. The common case -
    // a fresh tab whose TaskDescription is "" - binds this property to the same value as its own
    // default, so OnPlainTextChanged never fires. Wiring TextChanged only from there would leave
    // the document -> source direction unconnected and silently drop everything the user types,
    // leaving phase 1 to render initial_prompt.md with an empty {taskbeschreibung}.
    // The coercion callback runs on every set and on binding attachment, change or not.
    private static object? CoercePlainText(DependencyObject d, object? baseValue)
    {
        if (d is RichTextBox box)
        {
            Attach(box);
        }

        return baseValue ?? string.Empty;
    }

    private static void OnPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box || (bool)box.GetValue(IsUpdatingProperty))
        {
            return;
        }

        Attach(box);

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            var text = e.NewValue as string ?? string.Empty;
            box.Document.Blocks.Clear();

            if (text.Length > 0)
            {
                foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    box.Document.Blocks.Add(new Paragraph(new Run(line)));
                }
            }
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }

    private static void Attach(RichTextBox box)
    {
        box.TextChanged -= OnTextChanged;
        box.TextChanged += OnTextChanged;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not RichTextBox box || (bool)box.GetValue(IsUpdatingProperty))
        {
            return;
        }

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            var range = new TextRange(box.Document.ContentStart, box.Document.ContentEnd);

            // SetCurrentValue, not SetValue: SetValue writes a LOCAL value, which outranks a
            // binding and detaches a one-way one. SetCurrentValue changes the effective value
            // and leaves the binding in place - exactly what a two-way assist needs.
            box.SetCurrentValue(PlainTextProperty, range.Text.TrimEnd('\r', '\n'));
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
```

> **Why the coercion callback and not just `OnPlainTextChanged`.** A dependency-property changed
> callback fires only when the effective value actually changes. Binding `TaskDescription` — `""`
> on a fresh tab — to a property whose default is already `string.Empty` is *not* a change, so
> `OnPlainTextChanged` never runs, `Attach` never runs, and every keystroke is dropped. The
> coercion callback runs on every set and on binding attachment regardless of the value, so the
> document → source direction is wired in all cases. `Attach` is idempotent (it detaches before
> it attaches), so running it more often costs nothing.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~ConverterTests|FullyQualifiedName~RichTextBoxAssistTests"`
Expected: PASS — 18 passed.

- [ ] **Step 6: Commit**

```
git add -A
git commit -m "feat(view): phase converters and RichTextBox plain-text attached property"
```

---

## Task 13: `PhaseIndicatorViewModel` and `TaskTabViewModel`

**Files:**
- Create: `Workflow\Services\IDirectoryPickerService.cs`, `Workflow\Services\DirectoryPickerService.cs`, `Workflow\ViewModels\PhaseIndicatorViewModel.cs`, `Workflow\ViewModels\TaskTabViewModel.cs`
- Test: `Workflow.Tests\TaskTabViewModelTests.cs`

**Interfaces:**
- Consumes: `TaskPaths`, `PhaseCatalog`, `PhaseProgress`, `WorkflowPhase`, `PhaseStatus` (Task 2); `ITaskFolderService` (Task 3); `IWorkflowOrchestrator`, `ManualPhaseSignal`, `ITerminalController`, `WorkflowRunRequest` (Task 9); `ISettingsService` (Task 7); `IDirectoryPickerService` (this task).
- Produces:
  - `interface IDirectoryPickerService` with `string? PickDirectory(string? initialDirectory)`
  - `sealed partial class PhaseIndicatorViewModel : ObservableObject` with `WorkflowPhase Phase`, `string DisplayName`, `PhaseStatus Status`, `bool IsActive`
  - `sealed partial class TaskTabViewModel : ObservableObject, IDisposable` with
    `string TaskName`, `string TaskDescription`, `string? WorkingDirectory`, `string Header`,
    `bool IsNameLocked`, `bool IsRunning`, `string? ValidationMessage`, `string? InfoMessage`,
    `ObservableCollection<PhaseIndicatorViewModel> Phases`,
    `ObservableCollection<string> RecentDirectories`,
    `TerminalViewModel Terminal`,
    commands `StartWorkflowCommand`, `BrowseDirectoryCommand`, `CompleteCurrentPhaseCommand`, `CompleteTaskCommand`, `CloseCommand`,
    and `event EventHandler? CloseRequested`

**Constraints specific to this task:**
- `[ObservableProperty]` goes on `_`-prefixed private fields (`.editorconfig` + `MVVMTK0040`).
- `[NotifyPropertyChangedFor]` and `[NotifyCanExecuteChangedFor]` do the dependent-property plumbing; do not hand-write `OnPropertyChanged` calls.
- Folder create/rename is debounced 500 ms so every keystroke does not hit the disk.

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\TaskTabViewModelTests.cs`:

```csharp
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.ViewModels;

namespace Workflow.Tests;

public sealed class TaskTabViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public TaskTabViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab-" + Guid.NewGuid().ToString("N"));
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

    private TaskTabViewModel Create(
        IReadOnlyList<string>? startupErrors = null,
        IWorkflowOrchestrator? orchestrator = null,
        ITaskFolderService? folders = null)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            folders ?? new TaskFolderService(),
            orchestrator ?? new StubOrchestrator(),
            _settings,
            new StubDirectoryPicker(null),
            terminal,
            folderDebounce: TimeSpan.Zero,
            startupErrors ?? []);
    }

    private sealed class StubOrchestrator : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class ThrowingOrchestrator(Exception failure) : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }

    /// <summary>A folder service whose rename always fails, as a locked or colliding one would.</summary>
    private sealed class FailingRenameFolderService : ITaskFolderService
    {
        private readonly TaskFolderService _inner = new();

        public TaskNameValidation Validate(string? taskName, string? workingDirectory) =>
            _inner.Validate(taskName, workingDirectory);

        public void EnsureCreated(TaskPaths paths) => _inner.EnsureCreated(paths);

        public bool DirectoryAlreadyExisted(TaskPaths paths) => _inner.DirectoryAlreadyExisted(paths);

        public void Rename(string workingDirectory, string oldName, string newName) =>
            throw new IOException("Der Ordner wird von einem anderen Prozess verwendet.");
    }

    private sealed class StubDirectoryPicker(string? result) : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory) => result;
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [StaFact]
    public void StartWorkflow_CannotExecuteWhileStartupErrorsArePresent()
    {
        // F17: the startup dialog can be dismissed, and '+' opens tabs afterwards. The gate has
        // to live on the command, not only on MainWindowViewModel.
        using var vm = Create(startupErrors: ["review_prompt.md ist leer."]);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
        Assert.Equal("review_prompt.md ist leer.", vm.ValidationMessage);
    }

    [StaFact]
    public void StartWorkflow_CanExecuteWhenThereAreNoStartupErrors()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void AFailedRename_RollsTheNameBackToTheFolderOnDisk()
    {
        // Spec 8.3 / 12.2. Without the rollback, TaskName, the tab header and every path in
        // TaskPaths name a folder that does not exist, and the user cannot tell which is real.
        using var vm = Create(folders: new FailingRenameFolderService());
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";                       // created on disk

        vm.TaskName = "demo-renamed";               // rename throws

        Assert.Equal("demo", vm.TaskName);
        Assert.Equal("demo", vm.Header);
        Assert.NotNull(vm.ValidationMessage);
        Assert.True(Directory.Exists(Path.Combine(_root, "demo")));
        Assert.False(Directory.Exists(Path.Combine(_root, "demo-renamed")));
    }

    [StaTheory]
    [MemberData(nameof(StartupFailures))]
    public async Task AThrowingOrchestrator_SurfacesAMessageInsteadOfAnUnobservedTaskException(Exception failure)
    {
        // StartWorkflow does not await the run, so anything not caught in RunAsync becomes an
        // unobserved task exception while the UI silently resets IsRunning (spec 12.1).
        var unobserved = 0;
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            using var vm = Create(orchestrator: new ThrowingOrchestrator(failure));
            vm.WorkingDirectory = _root;
            vm.TaskName = "demo";

            vm.StartWorkflowCommand.Execute(null);

            // Let the faulted task be observed and the finally block run.
            for (var i = 0; i < 50 && vm.IsRunning; i++)
            {
                await Task.Delay(20);
            }

            Assert.False(vm.IsRunning);
            Assert.NotNull(vm.ValidationMessage);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    public static TheoryData<Exception> StartupFailures() =>
    [
        new FileNotFoundException("Weder pwsh.exe noch powershell.exe wurden gefunden."),
        new PlatformNotSupportedException("Diese Windows-Version unterstuetzt keine Pseudo-Konsole."),
        new Win32Exception(2, "Der Shell-Prozess konnte nicht gestartet werden."),
        new IOException("Das Arbeitsverzeichnis wurde geloescht."),
        new UnauthorizedAccessException("Kein Zugriff."),
        new InvalidOperationException("WebView2 konnte nicht initialisiert werden."),
        new ArtifactWatchException("Das Arbeitsverzeichnis existiert nicht mehr."),
    ];


    [StaFact]
    public void Header_FallsBackToTheGermanPlaceholder()
    {
        using var vm = Create();

        Assert.Equal("(Bezeichnung)", vm.Header);

        vm.TaskName = "demo";

        Assert.Equal("demo", vm.Header);
    }

    [StaFact]
    public void Header_TreatsWhitespaceAsEmpty()
    {
        using var vm = Create();

        vm.TaskName = "   ";

        Assert.Equal("(Bezeichnung)", vm.Header);
    }

    [StaFact]
    public void Phases_AreTheFourStationsInOrderAndStartPending()
    {
        using var vm = Create();

        Assert.Equal(4, vm.Phases.Count);
        Assert.Equal("Spezifikation", vm.Phases[0].DisplayName);
        Assert.Equal("Review", vm.Phases[1].DisplayName);
        Assert.Equal("Review umsetzen", vm.Phases[2].DisplayName);
        Assert.Equal("Implementierung", vm.Phases[3].DisplayName);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [StaFact]
    public void StartWorkflow_IsDisabledWithoutANameOrADirectory()
    {
        using var vm = Create();

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        vm.WorkingDirectory = _root;
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        vm.TaskName = "demo";
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void StartWorkflow_IsDisabledForAnInvalidName()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;

        vm.TaskName = "bad:name";

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
        Assert.NotNull(vm.ValidationMessage);
    }

    [StaFact]
    public void SettingAValidName_CreatesTheFolder()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;

        vm.TaskName = "demo";

        Assert.True(Directory.Exists(Path.Combine(_root, "demo")));
    }

    [StaFact]
    public void RenamingWhileIdle_RenamesTheFolder()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "old";

        vm.TaskName = "new";

        Assert.False(Directory.Exists(Path.Combine(_root, "old")));
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
    }

    [StaFact]
    public void ReusingAnExistingFolder_SetsAnInfoMessage()
    {
        Directory.CreateDirectory(Path.Combine(_root, "demo"));
        using var vm = Create();
        vm.WorkingDirectory = _root;

        vm.TaskName = "demo";

        Assert.NotNull(vm.InfoMessage);
    }

    [StaFact]
    public void StartWorkflow_LocksTheNameAndMarksTheRunAsActive()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        vm.StartWorkflowCommand.Execute(null);

        Assert.True(vm.IsNameLocked);
        Assert.True(vm.IsRunning);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void RenamingAfterStart_DoesNotTouchTheFolder()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        vm.StartWorkflowCommand.Execute(null);

        vm.TaskName = "renamed";

        Assert.True(Directory.Exists(Path.Combine(_root, "demo")));
        Assert.False(Directory.Exists(Path.Combine(_root, "renamed")));
    }

    [StaFact]
    public void CompleteTask_IsOnlyEnabledDuringTheImplementationPhase()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        vm.StartWorkflowCommand.Execute(null);

        Assert.False(vm.CompleteTaskCommand.CanExecute(null));

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        Assert.True(vm.CompleteTaskCommand.CanExecute(null));
    }

    [StaFact]
    public void ApplyProgress_DrivesTheIndicatorColours()
    {
        using var vm = Create();

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Specification, PhaseStatus.Active));
        Assert.Equal(PhaseStatus.Active, vm.Phases[0].Status);

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Specification, PhaseStatus.Completed));
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Review, PhaseStatus.Active));

        Assert.Equal(PhaseStatus.Completed, vm.Phases[0].Status);
        Assert.Equal(PhaseStatus.Active, vm.Phases[1].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[2].Status);
    }

    [StaFact]
    public void SelectingADirectory_AddsItToTheSharedRecentList()
    {
        using var vm = Create();

        vm.WorkingDirectory = _root;

        Assert.Contains(_root, _settings.Settings.RecentDirectories);
        Assert.Contains(_root, vm.RecentDirectories);
    }

    [StaFact]
    public void CloseCommand_RaisesCloseRequested()
    {
        using var vm = Create();
        var raised = false;
        vm.CloseRequested += (_, _) => raised = true;

        vm.CloseCommand.Execute(null);

        Assert.True(raised);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: FAIL — `CS0246: The type or namespace name 'TaskTabViewModel' could not be found`.

- [ ] **Step 3: Write the directory picker**

`Workflow\Services\IDirectoryPickerService.cs`:

```csharp
namespace Workflow.Services;

/// <summary>Shows the folder-browser dialog.</summary>
public interface IDirectoryPickerService
{
    /// <summary>Asks the user for a directory.</summary>
    /// <param name="initialDirectory">Directory to start in, or null.</param>
    /// <returns>The chosen directory, or null when cancelled.</returns>
    public string? PickDirectory(string? initialDirectory);
}
```

`Workflow\Services\DirectoryPickerService.cs`:

```csharp
using System.IO;
using Microsoft.Win32;

namespace Workflow.Services;

/// <inheritdoc cref="IDirectoryPickerService" />
public sealed class DirectoryPickerService : IDirectoryPickerService
{
    /// <inheritdoc />
    public string? PickDirectory(string? initialDirectory)
    {
        // OpenFolderDialog is the .NET 8 WPF folder browser; no Windows Forms reference needed.
        var dialog = new OpenFolderDialog
        {
            Title = "Arbeitsverzeichnis auswählen",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
```

- [ ] **Step 4: Write `PhaseIndicatorViewModel`**

`Workflow\ViewModels\PhaseIndicatorViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Workflow.Models;

namespace Workflow.ViewModels;

/// <summary>One of the four grey/yellow/green station indicators.</summary>
public sealed partial class PhaseIndicatorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private PhaseStatus _status = PhaseStatus.Pending;

    /// <summary>Creates the indicator from a phase definition.</summary>
    /// <param name="definition">The phase this indicator represents.</param>
    public PhaseIndicatorViewModel(PhaseDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Phase = definition.Phase;
        DisplayName = definition.DisplayName;
    }

    /// <summary>The phase this indicator represents.</summary>
    public WorkflowPhase Phase { get; }

    /// <summary>German label shown under the icon.</summary>
    public string DisplayName { get; }

    /// <summary>True while this phase is running; shows the 'Phase abschliessen' button.</summary>
    public bool IsActive => Status == PhaseStatus.Active;
}
```

- [ ] **Step 5: Write `TaskTabViewModel`**

`Workflow\ViewModels\TaskTabViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.ViewModels;

/// <summary>One Task, one tab: metadata, the four indicators, and the terminal.</summary>
public sealed partial class TaskTabViewModel : ObservableObject, IDisposable
{
    private readonly ITaskFolderService _folders;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly ISettingsService _settings;
    private readonly IDirectoryPickerService _picker;
    private readonly ManualPhaseSignal _manualSignal = new();
    private readonly TimeSpan _folderDebounce;

    private readonly IReadOnlyList<string> _startupErrors;

    private CancellationTokenSource? _run;
    private CancellationTokenSource? _folderDebounceSource;
    private string? _folderOnDisk;
    private WorkflowPhase? _activePhase;
    private bool _suppressFolderSync;
    private int _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string _taskName = string.Empty;

    [ObservableProperty]
    private string _taskDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _workingDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isNameLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _validationMessage;

    [ObservableProperty]
    private string? _infoMessage;

    /// <summary>Creates the tab.</summary>
    /// <param name="folders">Task-folder service.</param>
    /// <param name="orchestrator">The four-phase state machine.</param>
    /// <param name="settings">Shared settings, used for the directory MRU.</param>
    /// <param name="picker">Folder-browser dialog.</param>
    /// <param name="terminal">This tab's terminal.</param>
    /// <param name="folderDebounce">Delay before a name change touches the disk.</param>
    /// <param name="startupErrors">
    /// Prompt-template problems found at startup. While this list is non-empty
    /// <c>StartWorkflowCommand</c> cannot execute on this tab (F17, spec section 9.4).
    /// </param>
    public TaskTabViewModel(
        ITaskFolderService folders,
        IWorkflowOrchestrator orchestrator,
        ISettingsService settings,
        IDirectoryPickerService picker,
        TerminalViewModel terminal,
        TimeSpan folderDebounce,
        IReadOnlyList<string> startupErrors)
    {
        ArgumentNullException.ThrowIfNull(startupErrors);

        _folders = folders;
        _orchestrator = orchestrator;
        _settings = settings;
        _picker = picker;
        _folderDebounce = folderDebounce;
        _startupErrors = startupErrors;

        if (startupErrors.Count > 0)
        {
            // The modal at startup can be dismissed; the tab keeps saying why Start is dead.
            ValidationMessage = startupErrors[0];
        }

        Terminal = terminal;
        Phases = new ObservableCollection<PhaseIndicatorViewModel>(
            PhaseCatalog.All.Select(d => new PhaseIndicatorViewModel(d)));
        RecentDirectories = new ObservableCollection<string>(settings.Settings.RecentDirectories);

        if (!string.IsNullOrWhiteSpace(settings.Settings.LastDirectory)
            && Directory.Exists(settings.Settings.LastDirectory))
        {
            WorkingDirectory = settings.Settings.LastDirectory;
        }
    }

    /// <summary>Raised when the tab's close button is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The tab header: the task name, or '(Bezeichnung)' while it is empty.</summary>
    public string Header => string.IsNullOrWhiteSpace(TaskName) ? "(Bezeichnung)" : TaskName.Trim();

    /// <summary>The four station indicators.</summary>
    public ObservableCollection<PhaseIndicatorViewModel> Phases { get; }

    /// <summary>Working directories offered in the ComboBox.</summary>
    public ObservableCollection<string> RecentDirectories { get; }

    /// <summary>This tab's terminal.</summary>
    public TerminalViewModel Terminal { get; }

    /// <summary>Applies a phase status change from the orchestrator. Public for testing.</summary>
    /// <param name="progress">The reported change.</param>
    public void ApplyProgress(PhaseProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var indicator = Phases.First(p => p.Phase == progress.Phase);
        indicator.Status = progress.Status;

        _activePhase = progress.Status == PhaseStatus.Active ? progress.Phase : null;

        CompleteTaskCommand.NotifyCanExecuteChanged();
        CompleteCurrentPhaseCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _run?.Cancel();
        _run?.Dispose();
        _folderDebounceSource?.Cancel();
        _folderDebounceSource?.Dispose();
        Terminal.Dispose();
    }

    partial void OnTaskNameChanged(string value) => ScheduleFolderSync();

    partial void OnWorkingDirectoryChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _settings.AddRecentDirectory(value);
        _settings.Save();

        RecentDirectories.Clear();
        foreach (var directory in _settings.Settings.RecentDirectories)
        {
            RecentDirectories.Add(directory);
        }

        _folderOnDisk = null;
        ScheduleFolderSync();
    }

    private void ScheduleFolderSync()
    {
        // The name is frozen once the pipeline starts: a running CLI holds the directory and
        // Directory.Move would throw. Locking removes the failure mode instead of handling it.
        if (IsNameLocked)
        {
            return;
        }

        // Set while RollBackNameToDisk restores TaskName after a failed rename; without it the
        // restoring assignment would schedule another sync and retry the rename in a loop.
        if (_suppressFolderSync)
        {
            return;
        }

        _folderDebounceSource?.Cancel();
        _folderDebounceSource?.Dispose();
        _folderDebounceSource = new CancellationTokenSource();
        var token = _folderDebounceSource.Token;

        if (_folderDebounce <= TimeSpan.Zero)
        {
            SyncFolder();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_folderDebounce, token);
                SyncFolder();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later keystroke.
            }
        }, token);
    }

    private void SyncFolder()
    {
        var validation = _folders.Validate(TaskName, WorkingDirectory);
        ValidationMessage = validation.IsValid ? null : validation.ErrorMessage;

        if (!validation.IsValid || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return;
        }

        var paths = new TaskPaths(WorkingDirectory, TaskName);

        try
        {
            if (_folderOnDisk is not null && !string.Equals(_folderOnDisk, paths.TaskName, StringComparison.Ordinal))
            {
                _folders.Rename(WorkingDirectory, _folderOnDisk, paths.TaskName);
                _folderOnDisk = paths.TaskName;
                InfoMessage = null;
                return;
            }

            InfoMessage = _folders.DirectoryAlreadyExisted(paths)
                ? "Ordner existiert bereits und wird weiterverwendet."
                : null;

            _folders.EnsureCreated(paths);
            _folderOnDisk = paths.TaskName;
        }
        catch (IOException ex)
        {
            ValidationMessage = $"Der Ordner konnte nicht angelegt oder umbenannt werden: {ex.Message}";
            RollBackNameToDisk();
        }
        catch (UnauthorizedAccessException ex)
        {
            ValidationMessage = $"Kein Zugriff auf das Arbeitsverzeichnis: {ex.Message}";
            RollBackNameToDisk();
        }
    }

    /// <summary>
    /// Restores <see cref="TaskName"/> to the folder that actually exists on disk after a failed
    /// rename (spec sections 8.3 and 12.2).
    /// </summary>
    /// <remarks>
    /// Leaving TaskName and the on-disk folder disagreeing is worse than the failure itself: the
    /// tab header, every path in TaskPaths and the {taskbezeichnung} token would all name a
    /// folder that does not exist, and the user has no way to tell which one is authoritative.
    /// The guard stops the restoring assignment from re-entering the debounced folder sync -
    /// which would retry the rename that just failed, in a loop.
    /// </remarks>
    private void RollBackNameToDisk()
    {
        if (_folderOnDisk is null || string.Equals(TaskName, _folderOnDisk, StringComparison.Ordinal))
        {
            return;
        }

        _suppressFolderSync = true;
        try
        {
            TaskName = _folderOnDisk;
        }
        finally
        {
            _suppressFolderSync = false;
        }
    }

    private bool CanStartWorkflow() =>
        !IsRunning
        // F17 / spec section 9.4: a prompt-template problem disables Start on EVERY tab,
        // including tabs opened with '+' after the startup dialog was dismissed. Holding the
        // flag only on MainWindowViewModel leaves the button executable once the modal is gone.
        && _startupErrors.Count == 0
        && ValidationMessage is null
        && !string.IsNullOrWhiteSpace(TaskName)
        && !string.IsNullOrWhiteSpace(WorkingDirectory);

    [RelayCommand(CanExecute = nameof(CanStartWorkflow))]
    private void StartWorkflow()
    {
        SyncFolder();
        if (!CanStartWorkflow() || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return;
        }

        IsNameLocked = true;
        IsRunning = true;

        var paths = new TaskPaths(WorkingDirectory, TaskName);
        _run = new CancellationTokenSource();

        var request = new WorkflowRunRequest(
            paths,
            TaskDescription,
            Terminal,
            _manualSignal,
            new Progress<PhaseProgress>(ApplyProgress));

        _ = RunAsync(request, _run.Token);
    }

    // StartWorkflow deliberately does not await this task, so every failure it can produce has to
    // be caught HERE. Anything that escapes becomes an unobserved task exception while the UI
    // merely flips IsRunning back to false and says nothing - which contradicts every
    // "actionable message" row in spec section 12.1. The list below is exhaustive for the code
    // paths this design specifies; a catch-all is deliberately NOT added, because an unexpected
    // exception type should surface during development rather than be swallowed into a label.
    private async Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.RunAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Tab closed or application shutting down. Never reported as an error.
        }
        catch (PromptTemplateException ex)
        {
            ValidationMessage = ex.Message;
        }
        catch (ArtifactWatchException ex)
        {
            // The watched directory disappeared or the watcher failed (spec section 12.2).
            ValidationMessage = ex.Message;
        }
        catch (FileNotFoundException ex)
        {
            // ShellLocator found neither pwsh.exe nor powershell.exe.
            ValidationMessage = ex.Message;
        }
        catch (PlatformNotSupportedException ex)
        {
            // CreatePseudoConsole returned E_NOTIMPL - Windows older than 10 1809.
            ValidationMessage = ex.Message;
        }
        catch (Win32Exception ex)
        {
            // CreatePipe or CreateProcess failed.
            ValidationMessage = $"Die Terminal-Sitzung konnte nicht gestartet werden: {ex.Message}";
        }
        catch (IOException ex)
        {
            ValidationMessage = $"Dateisystemfehler waehrend des Workflows: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ValidationMessage = $"Kein Zugriff auf das Arbeitsverzeichnis: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            // WebView2 initialisation, or Start called twice on one session.
            ValidationMessage = $"Das Terminal konnte nicht gestartet werden: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void BrowseDirectory()
    {
        var chosen = _picker.PickDirectory(WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            WorkingDirectory = chosen;
        }
    }

    private bool CanCompleteCurrentPhase() => IsRunning && _activePhase is not null;

    [RelayCommand(CanExecute = nameof(CanCompleteCurrentPhase))]
    private void CompleteCurrentPhase() => _manualSignal.Signal();

    private bool CanCompleteTask() => IsRunning && _activePhase == WorkflowPhase.Implementation;

    [RelayCommand(CanExecute = nameof(CanCompleteTask))]
    private void CompleteTask() => _manualSignal.Signal();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~TaskTabViewModelTests"`
Expected: PASS — 24 passed (17 facts + the 7 `StartupFailures` theory cases).

- [ ] **Step 7: Commit**

```
git add -A
git commit -m "feat(viewmodels): task tab with phase indicators and debounced folder sync"
```

---

## Task 14: Shell view model, composition root and theme

**Files:**
- Create: `Workflow\ViewModels\MainWindowViewModel.cs`, `Workflow\Services\ITaskTabViewModelFactory.cs`, `Workflow\Services\TaskTabViewModelFactory.cs`
- Modify: `Workflow\App.xaml`, `Workflow\App.xaml.cs`
- Test: `Workflow.Tests\MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3–13.
- Produces:
  - `interface ITaskTabViewModelFactory` with `TaskTabViewModel Create()`
  - `sealed partial class MainWindowViewModel : ObservableObject` with
    `ObservableCollection<TaskTabViewModel> Tabs`, `TaskTabViewModel? SelectedTab`,
    `IReadOnlyList<string> StartupErrors`, `bool HasStartupErrors`,
    commands `AddTaskTabCommand`, `CloseTabCommand` (parameter `TaskTabViewModel`), `CloseApplicationCommand`,
    and `void ShutdownAll()`

- [ ] **Step 1: Write the failing tests**

`Workflow.Tests\MainWindowViewModelTests.cs`:

```csharp
using System.IO;
using System.Windows.Threading;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.ViewModels;

namespace Workflow.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public MainWindowViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-main-" + Guid.NewGuid().ToString("N"));
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

    private sealed class StubFactory(SettingsService settings, IReadOnlyList<string> startupErrors)
        : ITaskTabViewModelFactory
    {
        public TaskTabViewModel Create() => new(
            new TaskFolderService(),
            new StubOrchestrator(),
            settings,
            new StubPicker(),
            new TerminalViewModel(
                new WebViewEnvironmentProvider(),
                new ConPtySessionFactory(),
                Dispatcher.CurrentDispatcher),
            TimeSpan.Zero,
            startupErrors);

        private sealed class StubOrchestrator : IWorkflowOrchestrator
        {
            public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
                Task.Delay(Timeout.Infinite, cancellationToken);
        }

        private sealed class StubPicker : IDirectoryPickerService
        {
            public string? PickDirectory(string? initialDirectory) => null;
        }
    }

    private MainWindowViewModel Create(IReadOnlyList<string>? startupErrors = null) =>
        new(new StubFactory(_settings, startupErrors ?? []), startupErrors ?? []);

    // --- Regressions pinned by the review -------------------------------------------------

    [StaFact]
    public void StartupErrors_DisableStartOnTheInitialTab()
    {
        var vm = Create(["review_prompt.md ist leer."]);

        Assert.True(vm.HasStartupErrors);
        Assert.False(vm.Tabs[0].StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void StartupErrors_DisableStartOnTabsOpenedAfterTheDialogWasDismissed()
    {
        // F17: the modal is dismissible and '+' keeps working, so the gate must travel with the
        // factory rather than living only on this view model.
        var vm = Create(["review_prompt.md ist leer."]);

        vm.AddTaskTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.All(vm.Tabs, tab => Assert.False(tab.StartWorkflowCommand.CanExecute(null)));
    }

    [StaFact]
    public void Startup_OpensExactlyOneTabAndSelectsIt()
    {
        var vm = Create();

        Assert.Single(vm.Tabs);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        Assert.Equal("(Bezeichnung)", vm.Tabs[0].Header);
    }

    [StaFact]
    public void AddTaskTab_AppendsAndSelectsTheNewTab()
    {
        var vm = Create();

        vm.AddTaskTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
    }

    [StaFact]
    public void CloseTab_RemovesAndDisposesIt()
    {
        var vm = Create();
        vm.AddTaskTabCommand.Execute(null);
        var first = vm.Tabs[0];

        vm.CloseTabCommand.Execute(first);

        Assert.Single(vm.Tabs);
        Assert.DoesNotContain(first, vm.Tabs);
    }

    [StaFact]
    public void ClosingTheLastTab_OpensAFreshOne()
    {
        var vm = Create();

        vm.CloseTabCommand.Execute(vm.Tabs[0]);

        Assert.Single(vm.Tabs);
        Assert.Equal("(Bezeichnung)", vm.Tabs[0].Header);
    }

    [StaFact]
    public void ATabsCloseRequest_ClosesIt()
    {
        var vm = Create();
        vm.AddTaskTabCommand.Execute(null);
        var second = vm.Tabs[1];

        second.CloseCommand.Execute(null);

        Assert.Single(vm.Tabs);
    }

    [StaFact]
    public void StartupErrors_AreSurfaced()
    {
        var vm = Create(["Die Prompt-Datei 'review_prompt.md' ist leer."]);

        Assert.True(vm.HasStartupErrors);
        Assert.Single(vm.StartupErrors);
    }

    [StaFact]
    public void NoStartupErrors_MeansHasStartupErrorsIsFalse()
    {
        Assert.False(Create().HasStartupErrors);
    }

    [StaFact]
    public void ShutdownAll_DisposesEveryTab()
    {
        var vm = Create();
        vm.AddTaskTabCommand.Execute(null);

        vm.ShutdownAll();

        Assert.Empty(vm.Tabs);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: FAIL — `CS0246: The type or namespace name 'MainWindowViewModel' could not be found`.

- [ ] **Step 3: Write the factory**

`Workflow\Services\ITaskTabViewModelFactory.cs`:

```csharp
using Workflow.ViewModels;

namespace Workflow.Services;

/// <summary>Builds a fully wired tab view model, one per tab.</summary>
public interface ITaskTabViewModelFactory
{
    /// <summary>Creates a new tab.</summary>
    /// <returns>The tab view model.</returns>
    public TaskTabViewModel Create();
}
```

`Workflow\Services\TaskTabViewModelFactory.cs`:

```csharp
using System.Windows.Threading;
using Workflow.Terminal;
using Workflow.ViewModels;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskTabViewModelFactory" />
public sealed class TaskTabViewModelFactory : ITaskTabViewModelFactory
{
    private static readonly TimeSpan FolderDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ITaskFolderService _folders;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly ISettingsService _settings;
    private readonly IDirectoryPickerService _picker;
    private readonly IWebViewEnvironmentProvider _environment;
    private readonly ITerminalSessionFactory _sessions;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyList<string> _startupErrors;

    /// <summary>Creates the factory.</summary>
    /// <param name="folders">Task-folder service.</param>
    /// <param name="orchestrator">The four-phase state machine.</param>
    /// <param name="settings">Shared settings.</param>
    /// <param name="picker">Folder-browser dialog.</param>
    /// <param name="environment">Shared WebView2 environment.</param>
    /// <param name="sessions">Pseudo-console session factory.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="startupErrors">
    /// Prompt-template problems found by <c>PromptTemplateService.ValidateAll()</c>. The factory
    /// carries them so that EVERY tab it makes - including tabs opened with '+' long after the
    /// startup dialog was dismissed - has Start disabled (F17, spec section 9.4).
    /// </param>
    public TaskTabViewModelFactory(
        ITaskFolderService folders,
        IWorkflowOrchestrator orchestrator,
        ISettingsService settings,
        IDirectoryPickerService picker,
        IWebViewEnvironmentProvider environment,
        ITerminalSessionFactory sessions,
        Dispatcher dispatcher,
        IReadOnlyList<string> startupErrors)
    {
        ArgumentNullException.ThrowIfNull(startupErrors);

        _folders = folders;
        _orchestrator = orchestrator;
        _settings = settings;
        _picker = picker;
        _environment = environment;
        _sessions = sessions;
        _dispatcher = dispatcher;
        _startupErrors = startupErrors;
    }

    /// <inheritdoc />
    public TaskTabViewModel Create() => new(
        _folders,
        _orchestrator,
        _settings,
        _picker,
        new TerminalViewModel(_environment, _sessions, _dispatcher),
        FolderDebounce,
        _startupErrors);
}
```

- [ ] **Step 4: Write `MainWindowViewModel`**

`Workflow\ViewModels\MainWindowViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Workflow.Services;

namespace Workflow.ViewModels;

/// <summary>The shell: the tab collection and the global buttons.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ITaskTabViewModelFactory _factory;

    [ObservableProperty]
    private TaskTabViewModel? _selectedTab;

    /// <summary>Creates the shell view model and opens the initial tab.</summary>
    /// <param name="factory">Creates tab view models.</param>
    /// <param name="startupErrors">Prompt-template validation errors, if any.</param>
    public MainWindowViewModel(ITaskTabViewModelFactory factory, IReadOnlyList<string> startupErrors)
    {
        _factory = factory;
        StartupErrors = startupErrors;

        AddTaskTab();
    }

    /// <summary>The open tabs; there is always at least one.</summary>
    public ObservableCollection<TaskTabViewModel> Tabs { get; } = [];

    /// <summary>Prompt-template problems found at startup. Blocks 'Start workflow' when non-empty.</summary>
    public IReadOnlyList<string> StartupErrors { get; }

    /// <summary>True when a prompt template is missing, empty or has an unknown token.</summary>
    public bool HasStartupErrors => StartupErrors.Count > 0;

    /// <summary>Cancels and disposes every tab. Called from 'Schließen' and from Window.Closing.</summary>
    public void ShutdownAll()
    {
        foreach (var tab in Tabs.ToList())
        {
            tab.CloseRequested -= OnTabCloseRequested;
            tab.Dispose();
        }

        Tabs.Clear();
        SelectedTab = null;
    }

    [RelayCommand]
    private void AddTaskTab()
    {
        var tab = _factory.Create();
        tab.CloseRequested += OnTabCloseRequested;
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(TaskTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        tab.CloseRequested -= OnTabCloseRequested;
        Tabs.Remove(tab);
        tab.Dispose();

        if (Tabs.Count == 0)
        {
            AddTaskTab();
            return;
        }

        SelectedTab ??= Tabs[^1];
    }

    [RelayCommand]
    private void CloseApplication()
    {
        ShutdownAll();
        Application.Current?.Shutdown();
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is TaskTabViewModel tab)
        {
            CloseTab(tab);
        }
    }
}
```

- [ ] **Step 5: Rewrite `App.xaml`**

The merge order is load-bearing: changing it makes MahApps controls render unstyled. `StartupUri`
is removed so the window can be constructed with an injected `DataContext`.

> **Do not re-copy the dictionary paths from the in-repo sample**
> `C:\Users\Marco\Documents\repo\wpf\MaterialDesignInXaml.Examples\MahApps\MahApps.Basic\App.xaml`.
> The *order* there is right, but that sample pins `MaterialDesignThemes.MahApps` 0.1.5 on
> netcoreapp3.1, and its `MaterialDesignTheme.Defaults.xaml` path does not exist in the 5.3.2
> packages this project pins — it crashes the app at startup. Use `MaterialDesign2.Defaults.xaml`
> (corrected 2026-09-13; see spec §10.1).

```xml
<Application x:Class="Workflow.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:converters="clr-namespace:Workflow.Converters">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>

                <!-- MahApps -->
                <ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml" />
                <ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml" />

                <!-- Material Design. NOTE (corrected 2026-09-13): MaterialDesignThemes 5.x has
                     no MaterialDesignTheme.Defaults.xaml - it was split into
                     MaterialDesign2/MaterialDesign3 variants. Using the old v4 path here
                     crashes the app at startup with XamlParseException. See spec 10.1. -->
                <ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml" />

                <!-- Colour theme: dark, because the app is dominated by a terminal. -->
                <materialDesign:MahAppsBundledTheme BaseTheme="Dark" PrimaryColor="Indigo" SecondaryColor="Cyan" />

                <!-- Material Design: MahApps compatibility -->
                <ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.MahApps;component/Themes/MaterialDesignTheme.MahApps.Defaults.xaml" />

                <!-- Application styles -->
                <ResourceDictionary Source="pack://application:,,,/Workflow;component/Styles/TabControlStyles.xaml" />

            </ResourceDictionary.MergedDictionaries>

            <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
            <converters:InverseBooleanToVisibilityConverter x:Key="InverseBooleanToVisibilityConverter" />
            <converters:PhaseStatusToBrushConverter x:Key="PhaseStatusToBrushConverter" />
            <converters:PhaseStatusToIconKindConverter x:Key="PhaseStatusToIconKindConverter" />

        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 6: Write the composition root in `App.xaml.cs`**

A manual composition root, not a container: nine services and one window. Constructor injection
alone gives full testability; a container would add a package and indirection for no gain.

```csharp
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.ViewModels;
using Workflow.Views;

namespace Workflow;

/// <summary>Application entry point and composition root.</summary>
public partial class App : Application
{
    private MainWindowViewModel? _shell;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Workflow");

        var settings = new SettingsService(SettingsService.DefaultPath);
        var prompts = new PromptTemplateService(Path.Combine(AppContext.BaseDirectory, "Prompt"));
        var autoAnswer = new AutoAnswerService(
            Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json"),
            Path.Combine(appData, "autoanswer.rules.json"));

        var orchestrator = new WorkflowOrchestrator(
            prompts,
            autoAnswer,
            new ArtifactWatcherFactory(),
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromSeconds(1));

        // Turns a silent runtime FileNotFoundException into an actionable startup message.
        // Validated BEFORE the factory is built: the factory hands the result to every tab it
        // makes, so Start stays disabled even on tabs opened after the dialog is dismissed.
        var startupErrors = prompts.ValidateAll();

        var factory = new TaskTabViewModelFactory(
            new TaskFolderService(),
            orchestrator,
            settings,
            new DirectoryPickerService(),
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher,
            startupErrors);

        _shell = new MainWindowViewModel(factory, startupErrors);

        var window = new MainWindow { DataContext = _shell };
        window.Closing += (_, _) => _shell.ShutdownAll();
        MainWindow = window;
        window.Show();

        if (startupErrors.Count > 0)
        {
            MessageBox.Show(
                window,
                "Die Prompt-Vorlagen sind fehlerhaft:\n\n" + string.Join("\n", startupErrors),
                "Workflow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.ShutdownAll();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Top-level resilience boundary: a background failure must not kill a live workflow run.
        MessageBox.Show(
            MainWindow,
            "Ein unerwarteter Fehler ist aufgetreten:\n\n" + e.Exception.Message,
            "Workflow",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: PASS — 10 passed.

`App.xaml` will not compile until `Styles\TabControlStyles.xaml` and `Views\MainWindow.xaml`
exist (Task 15). If you are executing strictly in order, expect a build error here naming those
two files; complete Task 15 and re-run.

- [ ] **Step 8: Commit**

```
git add -A
git commit -m "feat(shell): main window view model, manual composition root, theme merge"
```

---

## Task 15: XAML views

> **Useful skill:** `wpf-design` — the edit → build → screenshot → self-review loop. Use it to
> iterate on spacing and colour without pinging the user every cycle.

**Files:**
- Create: `Workflow\Styles\TabControlStyles.xaml`, `Workflow\Views\PhaseIndicatorView.xaml(.cs)`, `Workflow\Views\TaskTabView.xaml(.cs)`
- Modify: `Workflow\Views\MainWindow.xaml(.cs)` (move the existing `MainWindow.xaml` / `MainWindow.xaml.cs` from the project root into `Views\` and change the namespace to `Workflow.Views`)
- Test: manual (Task 17, V5)

**Interfaces:**
- Consumes: `MainWindowViewModel`, `TaskTabViewModel`, `PhaseIndicatorViewModel`, `TerminalViewModel` (Tasks 11, 13, 14); the converters registered in `App.xaml` (Task 14).
- Produces: `WorkflowTabControlStyle` (a `TabControl` style whose template carries the `+` button).

**Verified resource keys — do not invent others.** All confirmed present in the pinned packages:
`MahApps.Styles.TabItem`, `mah:TabControlHelper.CloseButtonEnabled`,
`mah:TabControlHelper.CloseTabCommand`, `mah:TabControlHelper.Underlined`,
`mah:TransitioningContentControl`, `MaterialDesignRaisedButton`, `MaterialDesignIconButton`,
`MaterialDesignFloatingActionMiniButton`, `MaterialDesignOutlinedTextBox`,
`materialDesign:HintAssist.Hint`, `PackIconKind.ArmFlex`.

- [ ] **Step 1: Write `Workflow\Styles\TabControlStyles.xaml`**

MahApps 2.4.11 ships `MetroAnimatedSingleRowTabControl`, but its default template has no insertion
point for an extra header-row button and the `+` must sit *beside* the headers. The template below
docks a `TabPanel` and the `+` button in one `DockPanel`, keeps MahApps item styling via
`MahApps.Styles.TabItem`, and preserves the animated content transition with
`mah:TransitioningContentControl`.

Rejected alternatives: a sentinel `+` `TabItem` (breaks `ItemsSource` binding and needs selection
interception) and an absolutely-positioned overlay button (cannot track the last tab's right edge).

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:mah="http://metro.mahapps.com/winfx/xaml/controls"
                    xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes">

    <Style x:Key="WorkflowTabItemStyle" TargetType="TabItem" BasedOn="{StaticResource MahApps.Styles.TabItem}">
        <Setter Property="mah:TabControlHelper.CloseButtonEnabled" Value="True" />
        <Setter Property="mah:TabControlHelper.Underlined" Value="TabItems" />
    </Style>

    <Style x:Key="WorkflowTabControlStyle" TargetType="TabControl">
        <Setter Property="Background" Value="{DynamicResource MaterialDesignPaper}" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="ItemContainerStyle" Value="{StaticResource WorkflowTabItemStyle}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TabControl">
                    <DockPanel LastChildFill="True">

                        <DockPanel DockPanel.Dock="Top" LastChildFill="False" Margin="8,4,8,0">
                            <TabPanel x:Name="HeaderPanel"
                                      DockPanel.Dock="Left"
                                      IsItemsHost="True"
                                      Panel.ZIndex="1"
                                      KeyboardNavigation.TabIndex="1" />

                            <Button x:Name="AddTabButton"
                                    DockPanel.Dock="Left"
                                    Width="28"
                                    Height="28"
                                    Margin="8,0,0,0"
                                    VerticalAlignment="Center"
                                    ToolTip="Neuen Task öffnen"
                                    Style="{StaticResource MaterialDesignFloatingActionMiniButton}"
                                    Command="{Binding AddTaskTabCommand}">
                                <materialDesign:PackIcon Kind="Plus" Width="18" Height="18" />
                            </Button>
                        </DockPanel>

                        <!-- The stock TabControl template uses ContentSource="SelectedContent",
                             which is shorthand for binding Content, ContentTemplate,
                             ContentTemplateSelector AND ContentStringFormat. TransitioningContentControl
                             is a ContentControl, so ContentSource is not available and the
                             bindings must be written out. Binding Content alone leaves the host
                             with no template for a TaskTabViewModel, and WPF falls back to
                             ToString() - the window then shows the type name instead of the task
                             UI supplied via TabControl.ContentTemplate. See spec 10.3 / F18. -->
                        <mah:TransitioningContentControl x:Name="PART_SelectedContentHost"
                                                         Transition="Left"
                                                         Content="{TemplateBinding SelectedContent}"
                                                         ContentTemplate="{TemplateBinding SelectedContentTemplate}"
                                                         ContentTemplateSelector="{TemplateBinding SelectedContentTemplateSelector}"
                                                         ContentStringFormat="{TemplateBinding SelectedContentStringFormat}"
                                                         Margin="8" />
                    </DockPanel>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>
```

- [ ] **Step 2: Write `Workflow\Views\PhaseIndicatorView.xaml`**

```xml
<UserControl x:Class="Workflow.Views.PhaseIndicatorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:vm="clr-namespace:Workflow.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:PhaseIndicatorViewModel}"
             mc:Ignorable="d">
    <StackPanel Margin="0,0,24,0" HorizontalAlignment="Left">
        <StackPanel Orientation="Horizontal">
            <materialDesign:PackIcon Width="22"
                                     Height="22"
                                     VerticalAlignment="Center"
                                     Kind="{Binding Status, Converter={StaticResource PhaseStatusToIconKindConverter}}"
                                     Foreground="{Binding Status, Converter={StaticResource PhaseStatusToBrushConverter}}" />

            <TextBlock Margin="8,0,0,0"
                       VerticalAlignment="Center"
                       Text="{Binding DisplayName}"
                       Foreground="{Binding Status, Converter={StaticResource PhaseStatusToBrushConverter}}" />
        </StackPanel>

        <Button Margin="30,2,0,0"
                Padding="0"
                HorizontalAlignment="Left"
                Style="{StaticResource MaterialDesignFlatButton}"
                FontSize="10"
                Content="Phase abschliessen"
                ToolTip="Diese Phase manuell beenden, falls die Artefakte nicht erkannt werden"
                Visibility="{Binding IsActive, Converter={StaticResource BooleanToVisibilityConverter}}"
                Command="{Binding DataContext.CompleteCurrentPhaseCommand,
                                  RelativeSource={RelativeSource AncestorType=UserControl, AncestorLevel=2}}" />
    </StackPanel>
</UserControl>
```

`Workflow\Views\PhaseIndicatorView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace Workflow.Views;

/// <summary>One grey/yellow/green station indicator.</summary>
public partial class PhaseIndicatorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PhaseIndicatorView() => InitializeComponent();
}
```

- [ ] **Step 3: Write `Workflow\Views\TaskTabView.xaml`**

The `0.3*` / `0.7*` split is the stated requirement. Row 0 is wrapped in a `ScrollViewer` because
its required contents are roughly 470 px tall while 30 % of the minimum window height is ~228 px —
without it the lower controls would be unreachable.

```xml
<UserControl x:Class="Workflow.Views.TaskTabView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:behaviors="clr-namespace:Workflow.Behaviors"
             xmlns:views="clr-namespace:Workflow.Views"
             xmlns:vm="clr-namespace:Workflow.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:TaskTabViewModel}"
             mc:Ignorable="d">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="0.3*" />
            <RowDefinition Height="0.7*" />
        </Grid.RowDefinitions>

        <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto" Padding="0,0,12,0">
            <StackPanel>

                <TextBox materialDesign:HintAssist.Hint="Task"
                         Style="{StaticResource MaterialDesignOutlinedTextBox}"
                         IsReadOnly="{Binding IsNameLocked}"
                         Text="{Binding TaskName, UpdateSourceTrigger=PropertyChanged}" />

                <TextBlock Margin="4,4,0,0"
                           Foreground="{DynamicResource MaterialDesignValidationErrorBrush}"
                           TextWrapping="Wrap"
                           Text="{Binding ValidationMessage}" />

                <TextBlock Margin="4,0,0,0"
                           Opacity="0.75"
                           TextWrapping="Wrap"
                           Text="{Binding InfoMessage}" />

                <TextBlock Margin="0,12,0,4" Text="Taskbeschreibung" />

                <RichTextBox Height="200"
                             VerticalScrollBarVisibility="Auto"
                             AcceptsReturn="True"
                             BorderThickness="1"
                             BorderBrush="{DynamicResource MaterialDesignDivider}"
                             behaviors:RichTextBoxAssist.PlainText="{Binding TaskDescription, Mode=TwoWay}" />

                <DockPanel Margin="0,16,0,0" LastChildFill="True">
                    <Button DockPanel.Dock="Right"
                            Margin="8,0,0,0"
                            ToolTip="Verzeichnis auswählen"
                            Style="{StaticResource MaterialDesignIconButton}"
                            Command="{Binding BrowseDirectoryCommand}">
                        <materialDesign:PackIcon Kind="FolderOpen" />
                    </Button>

                    <ComboBox materialDesign:HintAssist.Hint="Arbeitsverzeichnis"
                              IsEditable="False"
                              ItemsSource="{Binding RecentDirectories}"
                              SelectedItem="{Binding WorkingDirectory, Mode=TwoWay}" />
                </DockPanel>

                <Button Margin="0,16,0,0"
                        HorizontalAlignment="Left"
                        Style="{StaticResource MaterialDesignRaisedButton}"
                        Command="{Binding StartWorkflowCommand}">
                    <StackPanel Orientation="Horizontal">
                        <materialDesign:PackIcon Kind="Play" VerticalAlignment="Center" />
                        <TextBlock Margin="8,0,0,0" Text="Start workflow" />
                    </StackPanel>
                </Button>

                <ItemsControl Margin="0,20,0,0" ItemsSource="{Binding Phases}">
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

                <Button Margin="0,16,0,8"
                        HorizontalAlignment="Left"
                        Style="{StaticResource MaterialDesignRaisedButton}"
                        Content="Task abschliessen"
                        Command="{Binding CompleteTaskCommand}" />

            </StackPanel>
        </ScrollViewer>

        <views:TerminalView Grid.Row="1" Margin="0,8,0,0" DataContext="{Binding Terminal}" />
    </Grid>
</UserControl>
```

`Workflow\Views\TaskTabView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace Workflow.Views;

/// <summary>One Task: 30 % metadata above, 70 % terminal below.</summary>
public partial class TaskTabView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TaskTabView() => InitializeComponent();
}
```

- [ ] **Step 4: Move and rewrite `MainWindow`**

Move `Workflow\MainWindow.xaml` and `Workflow\MainWindow.xaml.cs` into `Workflow\Views\`, then
replace their contents.

`Workflow\Views\MainWindow.xaml`:

```xml
<mah:MetroWindow x:Class="Workflow.Views.MainWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                 xmlns:mah="http://metro.mahapps.com/winfx/xaml/controls"
                 xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
                 xmlns:views="clr-namespace:Workflow.Views"
                 xmlns:vm="clr-namespace:Workflow.ViewModels"
                 d:DataContext="{d:DesignInstance Type=vm:MainWindowViewModel}"
                 mc:Ignorable="d"
                 Title="Workflow"
                 Icon="pack://application:,,,/Assets/workflow.ico"
                 Height="860"
                 Width="1280"
                 MinHeight="760"
                 MinWidth="1100"
                 Background="{DynamicResource MaterialDesignPaper}"
                 TextElement.Foreground="{DynamicResource MaterialDesignBody}"
                 FontFamily="{DynamicResource MaterialDesignFont}">

    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Row 0: the strong-arm icon, larger, top right. -->
        <materialDesign:PackIcon Grid.Row="0"
                                 Kind="ArmFlex"
                                 Width="56"
                                 Height="56"
                                 Margin="0,0,8,0"
                                 HorizontalAlignment="Right"
                                 VerticalAlignment="Top"
                                 Foreground="{DynamicResource PrimaryHueMidBrush}"
                                 ToolTip="Workflow" />

        <TextBlock Grid.Row="0"
                   Margin="4,8,0,0"
                   HorizontalAlignment="Left"
                   VerticalAlignment="Center"
                   FontSize="22"
                   Text="Workflow" />

        <TabControl Grid.Row="1"
                    Margin="0,12,0,0"
                    Style="{StaticResource WorkflowTabControlStyle}"
                    ItemsSource="{Binding Tabs}"
                    SelectedItem="{Binding SelectedTab, Mode=TwoWay}">
            <TabControl.ItemTemplate>
                <DataTemplate DataType="{x:Type vm:TaskTabViewModel}">
                    <TextBlock Text="{Binding Header}" MaxWidth="220" TextTrimming="CharacterEllipsis" />
                </DataTemplate>
            </TabControl.ItemTemplate>
            <TabControl.ContentTemplate>
                <DataTemplate DataType="{x:Type vm:TaskTabViewModel}">
                    <views:TaskTabView />
                </DataTemplate>
            </TabControl.ContentTemplate>
        </TabControl>

        <Button Grid.Row="2"
                Margin="0,12,0,0"
                HorizontalAlignment="Right"
                Style="{StaticResource MaterialDesignRaisedButton}"
                Content="Schließen"
                Command="{Binding CloseApplicationCommand}" />
    </Grid>
</mah:MetroWindow>
```

`Workflow\Views\MainWindow.xaml.cs`:

```csharp
using MahApps.Metro.Controls;

namespace Workflow.Views;

/// <summary>The application shell.</summary>
public partial class MainWindow : MetroWindow
{
    /// <summary>Creates the window.</summary>
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 5: Wire the tab close button**

`mah:TabControlHelper.CloseTabCommand` is an attached property on the `TabItem`. Add it to
`WorkflowTabItemStyle` in `Workflow\Styles\TabControlStyles.xaml`, binding through the
`TabControl` to the shell command:

```xml
        <Setter Property="mah:TabControlHelper.CloseTabCommand"
                Value="{Binding DataContext.CloseTabCommand,
                                RelativeSource={RelativeSource AncestorType=TabControl}}" />
        <Setter Property="mah:TabControlHelper.CloseTabCommandParameter" Value="{Binding}" />
```

- [ ] **Step 6: Register the icon and build**

Add to `Workflow\Workflow.csproj`:

```xml
    <ApplicationIcon>Assets\workflow.ico</ApplicationIcon>
```

and inside the existing `Content` `ItemGroup`:

```xml
    <Resource Include="Assets\workflow.ico" />
```

The icon file itself is produced in Task 16. Until then the build fails with
`MSB3771: The icon file ... is not valid`; create a 1×1 placeholder or complete Task 16 first.

Run: `dotnet build Workflow.sln -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test Workflow.sln`
Expected: all tests pass.

- [ ] **Step 8: Commit**

```
git add -A
git commit -m "feat(views): MetroWindow shell, tab template with + button, 30/70 task view"
```

---

## Task 16: ArmFlex application icon

**Files:**
- Create: `tools\GenerateIcon\GenerateIcon.csproj`, `tools\GenerateIcon\Program.cs`, `Workflow\Assets\workflow.ico`
- Test: `Workflow.Tests\IconTests.cs`

**Interfaces:**
- Consumes: `MaterialDesignThemes.Wpf.PackIcon`.
- Produces: `Workflow\Assets\workflow.ico` containing 16, 32, 48, 64, 128 and 256 px frames.

**Why a generator rather than a hand-pasted path.** The icon must be the same `ArmFlex` glyph the
window shows. Hard-coding path data would silently drift from the library and is easy to get
wrong; rendering the real `PackIcon` control keeps the two in sync and needs no magic strings.
`PackIconKind.ArmFlex` is confirmed present in `MaterialDesignThemes.Wpf` 5.3.2.

- [ ] **Step 1: Create the generator project**

`tools\GenerateIcon\GenerateIcon.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!-- A one-shot developer tool; the production analyzer policy is not useful here. -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AnalysisMode>Default</AnalysisMode>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MaterialDesignThemes" Version="5.3.2" />
  </ItemGroup>

</Project>
```

Do **not** add this project to `Workflow.sln` — it is a developer tool, not part of the product.

- [ ] **Step 2: Write the generator**

`tools\GenerateIcon\Program.cs`:

```csharp
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;

internal static class Program
{
    private static readonly int[] Sizes = [16, 32, 48, 64, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: GenerateIcon <output.ico>");
            return 1;
        }

        var frames = Sizes.Select(RenderPng).ToList();
        WriteIco(args[0], frames);

        Console.WriteLine($"Wrote {args[0]} with {frames.Count} frames.");
        return 0;
    }

    private static byte[] RenderPng(int size)
    {
        var icon = new PackIcon
        {
            Kind = PackIconKind.ArmFlex,
            Width = size,
            Height = size,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0xC0)), // Indigo 400
        };

        icon.Measure(new Size(size, size));
        icon.Arrange(new Rect(0, 0, size, size));
        icon.UpdateLayout();

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(icon);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteIco(string path, IReadOnlyList<byte[]> pngFrames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);

        writer.Write((short)0);                     // reserved
        writer.Write((short)1);                     // type: icon
        writer.Write((short)pngFrames.Count);

        var offset = 6 + (16 * pngFrames.Count);

        for (var i = 0; i < pngFrames.Count; i++)
        {
            var size = Sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));   // width, 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // palette size
            writer.Write((byte)0);                          // reserved
            writer.Write((short)1);                         // colour planes
            writer.Write((short)32);                        // bits per pixel
            writer.Write(pngFrames[i].Length);
            writer.Write(offset);
            offset += pngFrames[i].Length;
        }

        foreach (var frame in pngFrames)
        {
            writer.Write(frame);
        }
    }
}
```

- [ ] **Step 3: Generate the icon**

```bash
cd "C:/Users/Marco/Documents/repo/Workflow"
dotnet run --project tools/GenerateIcon/GenerateIcon.csproj -- Workflow/Assets/workflow.ico
```

Expected output: `Wrote Workflow/Assets/workflow.ico with 6 frames.`

- [ ] **Step 4: Write the test**

`Workflow.Tests\IconTests.cs`:

```csharp
using System.IO;

namespace Workflow.Tests;

public class IconTests
{
    private static string IconPath() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow", "Assets", "workflow.ico");

    [Fact]
    public void Icon_ExistsAndHasSixFrames()
    {
        var path = Path.GetFullPath(IconPath());

        Assert.True(File.Exists(path), $"Missing application icon: {path}");

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadInt16());   // reserved
        Assert.Equal(1, reader.ReadInt16());   // type: icon
        Assert.Equal(6, reader.ReadInt16());   // frame count
    }

    [Fact]
    public void Icon_IsLargerThanAPlaceholder()
    {
        var path = Path.GetFullPath(IconPath());

        Assert.True(new FileInfo(path).Length > 4096, "The icon looks like a placeholder.");
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Workflow.sln --filter "FullyQualifiedName~IconTests"`
Expected: PASS — 2 passed.

- [ ] **Step 6: Verify the icon visually**

Run: `dotnet build Workflow.sln -c Debug` then launch
`Workflow\bin\Debug\net8.0-windows\Workflow.exe`.
Expected: a flexed-arm glyph in the taskbar, in the window title bar, and at 56 px in the
top-right of the window content.

- [ ] **Step 7: Commit**

```
git add -A
git commit -m "feat(assets): generate the ArmFlex application icon from PackIcon"
```

---

## Task 17: Acceptance gate

**Files:**
- Create: `Workflow\verify.ps1`
- Test: the script itself plus the manual steps below.

**Interfaces:**
- Consumes: everything.
- Produces: a single command that decides whether the feature is done.

- [ ] **Step 1: Write `Workflow\verify.ps1`**

```powershell
#Requires -Version 7
<#
.SYNOPSIS
    Acceptance gate for the Workflow application.
.DESCRIPTION
    Automates criteria A1-A6 and V1-V4 of the specification. Manual steps V5-V8 are listed at
    the end and are not automated: they need a human watching a live terminal.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'Workflow.sln'
$failures = @()

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if ($Condition) {
        Write-Host "  PASS  $Message"
    }
    else {
        Write-Host "  FAIL  $Message" -ForegroundColor Red
        $script:failures += $Message
    }
}

Write-Host 'V1  Build with warnings as errors'
$buildLog = & dotnet build $solution -c $Configuration --nologo 2>&1
Assert-True ($LASTEXITCODE -eq 0) 'dotnet build exits 0'
Assert-True (-not ($buildLog | Select-String -Pattern ': warning ' -Quiet)) 'build produced no warnings'

Write-Host 'V2  Tests'
& dotnet test $solution -c $Configuration --nologo | Out-Host
Assert-True ($LASTEXITCODE -eq 0) 'dotnet test exits 0'

Write-Host 'V3  Output directory manifest'
$out = Join-Path $repo "Workflow\bin\$Configuration\net8.0-windows"
$required = @(
    'Prompt\initial_prompt.md',
    'Prompt\review_prompt.md',
    'Prompt\resolve_review_prompt.md',
    'Prompt\implementation_prompt.md',
    'Assets\autoanswer.rules.json',
    'Assets\Terminal\terminal.html',
    'Assets\Terminal\terminal.js',
    'Assets\Terminal\xterm.js',
    'Assets\Terminal\xterm.css',
    'Assets\Terminal\addon-fit.js'
)
foreach ($relative in $required) {
    $path = Join-Path $out $relative
    Assert-True ((Test-Path $path) -and ((Get-Item $path).Length -gt 0)) "present and non-empty: $relative"
}

Write-Host 'V4  Prompt templates'
$promptDir = Join-Path $repo 'Workflow\Prompt'
$known = @('taskbezeichnung', 'taskbeschreibung', 'AppDirectory', 'spec_path', 'plan_path', 'review_path')

foreach ($file in Get-ChildItem -Path $promptDir -Filter '*.md') {
    $content = Get-Content -Raw -Path $file.FullName
    Assert-True ($content.Trim().Length -gt 0) "non-empty: $($file.Name)"

    $tokens = [regex]::Matches($content, '\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}') |
        ForEach-Object { $_.Groups['n'].Value } |
        Sort-Object -Unique

    foreach ($token in $tokens) {
        Assert-True ($known -contains $token) "known token {$token} in $($file.Name)"
    }
}

Assert-True (-not (Select-String -Path (Join-Path $promptDir 'implementation_prompt.md') -Pattern '\{plan_path\}_' -Quiet)) `
    'implementation_prompt.md has no stray {plan_path}_'
Assert-True (Select-String -Path (Join-Path $promptDir 'review_prompt.md') -Pattern '\{review_path\}' -Quiet) `
    'review_prompt.md uses {review_path}'

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures.Count) check(s)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'All automated checks passed.' -ForegroundColor Green
Write-Host ''
Write-Host 'Remaining manual steps (V5-V11) - see specification section 15.3:'
Write-Host '  V5  Run the full four-phase pipeline against a scratch directory.'
Write-Host '  V6  Kill claude.exe mid-phase-1; the app must stay responsive and must not advance.'
Write-Host '  V7  Break the auto-answer pattern in %APPDATA%\Workflow\autoanswer.rules.json;'
Write-Host '      no answer is sent; the prompt is still pasted PROMPTLY once the screen goes quiet'
Write-Host '      (NOT after 60s - the ceiling is not a mandatory wait; spec 7.3 / D13), terminal usable.'
Write-Host '  V8  After Schliessen, Task Manager shows no orphan pwsh/node/claude/codex.'
Write-Host '  V9  Covered by dotnet test: a throwing orchestrator must surface a message, not an'
Write-Host '      unobserved task exception.'
Write-Host '  V10 Two tabs, both running: switch between them three times; both must keep streaming.'
Write-Host '  V11 Pick a drive root (C:\) as the working directory; the folder must land at C:\<name>.'
exit 0
```

- [ ] **Step 2: Run the gate**

Run: `pwsh -NoProfile -File Workflow\verify.ps1`
Expected: `All automated checks passed.` and exit code 0.

- [ ] **Step 3: Manual verification V5 — the full pipeline**

1. Create a scratch directory, for example `C:\temp\wf-demo`.
2. Launch `Workflow\bin\Release\net8.0-windows\Workflow.exe`.
3. Confirm **F1**: exactly one tab, header `(Bezeichnung)`, a `+` button beside it.
4. Click `+`; confirm **F2**: a second, independent tab with its own terminal. Close it again.
5. Confirm **F3**: the flexed-arm icon at 56 px top-right and in the taskbar.
6. Type `demo-task` into **Task**; confirm **F4**: the header updates live and
   `C:\temp\wf-demo\demo-task` appears on disk.
7. Rename to `demo-task-2`; confirm **F5**: the folder is renamed.
8. Pick `C:\temp\wf-demo` with the folder button; restart the app and confirm **F6**: the path is
   still in the ComboBox.
9. Enter a task description, click **Start workflow**. Confirm **F7**: the terminal shows
   `cd "C:\temp\wf-demo"`, then `yo`, then the bypass-permissions prompt answered with `2`, then
   the rendered prompt pasted as one block.
10. Confirm **F8**: typing in the input field below the terminal reaches the CLI.
11. Let phase 1 finish. Confirm **F9**: indicator 1 green, indicator 2 yellow, `codex --yolo` runs.
12. Confirm **F10**, **F11**, **F12**, **F14** as the pipeline advances.
13. Click **Task abschliessen**; confirm **F13**: the task folder holds `demo-task-2_spec.md`,
    `demo-task-2_plan.md`, `demo-task-2-review.md`, `findings.md`, `task_plan.md`, `progress.md`.

- [ ] **Step 4: Manual verification V6 — external kill**

Start a run, then from another shell:

```powershell
Get-Process claude -ErrorAction SilentlyContinue | Stop-Process -Force
```

Expected: the app stays responsive, the phase does **not** turn green, and the indicator stays
yellow. `Phase abschliessen` remains available.

- [ ] **Step 5: Manual verification V7 — broken auto-answer rule**

```powershell
$dir = Join-Path $env:APPDATA 'Workflow'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item "Workflow\bin\Release\net8.0-windows\Assets\autoanswer.rules.json" "$dir\autoanswer.rules.json"
(Get-Content "$dir\autoanswer.rules.json") -replace 'bypass permissions mode', 'zzz-never-matches' |
    Set-Content "$dir\autoanswer.rules.json"
```

Expected: no automatic answer is sent; once the launcher's screen goes quiet the prompt is
pasted **promptly** — on the order of the quiet period, **not** after 60 s — and the terminal
stays fully usable so the confirmation can be answered by hand. Delete the override file
afterwards.

> The 60 s value is a *ceiling* on the loop, not a mandatory wait (spec §7.3, decision D13).
> Waiting it out on every non-match would add a minute to every phase of every task while
> changing nothing about the outcome: the prompt would land in the same screen either way. The
> ceiling itself is exercised by `WorkflowOrchestratorTests`, not by a human sitting through it.

- [ ] **Step 6: Manual verification V8 — no orphans**

Close the app with **Schließen**, then:

```powershell
Get-Process pwsh,powershell,node,claude,codex -ErrorAction SilentlyContinue |
    Select-Object Name,Id,StartTime
```

Expected: nothing started during the session survives. Repeat with the window's X button; **F15**
and the `Window.Closing` path must behave identically.

- [ ] **Step 7: Manual verification — startup gate (F17)**

Empty one prompt file in the output directory and relaunch:

```powershell
Clear-Content "Workflow\bin\Release\net8.0-windows\Prompt\review_prompt.md"
```

Expected: a German error dialog naming `review_prompt.md`, and **Start workflow** disabled on
every tab. Restore the file with `dotnet build` afterwards.

- [ ] **Step 8: Manual verification V10 — tab switching must not kill a run (F19)**

1. Open two tabs, give each a task name and a working directory, and start a workflow in both.
2. Select tab B, then tab A, then tab B again — three switches.
3. Expected: **both** terminals are still rendering, both indicator sets still advance, and Task
   Manager shows both `pwsh` process trees alive throughout.
4. Expected: re-selecting a tab does not re-navigate its WebView2 or blank its scrollback, and no
   exception appears in the debugger output.

This is the regression a `TerminalView.Unloaded` handler calling `viewModel.Dispose()` would
cause: a `TabControl` unloads the previously selected content on every switch, so merely looking
at another task would kill the first one's pseudo-console (spec §12.4).

- [ ] **Step 9: Manual verification V11 — drive-root working directory**

1. Pick `C:\` (or any drive root) with the folder button and enter the task name `wf-root-test`.
2. Expected: the folder is created at `C:\wf-root-test`, **not** beside the application as
   `C:wf-root-test`.
3. Start the workflow and confirm the terminal shows `cd "C:\"`.
4. Restart the app and confirm the drive root is still in the ComboBox in that same form.
5. Delete `C:\wf-root-test` afterwards.

- [ ] **Step 10: Verification V9 — failure surfacing (automated, part of `dotnet test`)**

`TaskTabViewModelTests.AThrowingOrchestrator_SurfacesAMessageInsteadOfAnUnobservedTaskException`
covers every failure family in spec §12.1. Confirm it is present and green: it is the only
automated guard that the fire-and-forget `StartWorkflowCommand` cannot swallow a startup failure
into a silently reset `IsRunning`.

- [ ] **Step 11: Commit**

```
git add -A
git commit -m "chore: add verify.ps1 acceptance gate"
```

---

## Definition of done

The feature is complete when **all** of the following hold. Do not report completion otherwise.

- [ ] `pwsh -NoProfile -File Workflow\verify.ps1` exits 0.
- [ ] Manual steps V5–V11 and F17 have been executed and observed, not assumed.
- [ ] **Task 8 Step 0 was actually run**, a spike streamed output out of a real pseudo-console,
      and the proven sequence was written back into Task 8 and spec §6.3/§6.3.1 before any
      `ConPtySession` code was committed (A9).
- [ ] `Directory.Build.props` imports `Workflow\.roslyn`, and
      `dotnet build Workflow\Workflow.csproj -getProperty:TreatWarningsAsErrors` prints `true`.
- [ ] `NoWarn` contains only `CA2007`, `CA1303`, `SYSLIB1054`, `CA1812`, `CA1848`, `CA1515`,
      `CA1003` (plus the test-project set), each with a justification comment.
- [ ] **`CA1031` is not in `NoWarn`**: searching `Directory.Build.props` for it finds nothing, and
      the only occurrences in the tree are the two local `#pragma warning disable CA1031` sites
      (the PTY read loop and the top-level dispatcher handler).
- [ ] Every task ended at **0 warnings, 0 errors** — no analyzer debt was carried forward (A7).
- [ ] The shipped prompt templates are asserted by tests, not just edited once: no `{plan_path}_`,
      no `qdocimport` reference, `## Review resolution` present (A8).
- [ ] No `unsafe` keyword anywhere; `AllowUnsafeBlocks` is still `false`.
- [ ] Every task above is committed.

---

## Notes on prompt instructions 8 and 12

The task description was produced by filling `{taskbeschreibung}` into
`Workflow\Prompt\initial_prompt.md`. Items 8 and 12 of its numbered list are verbatim boilerplate
from that template and have **no task in this plan**. This is deliberate; specification §14
carries the full justification.

- **Item 8 — `C:\quincy\PRFMODUL\XKV20263\Doku\MeldungenKVDT.xml`.** The file exists (72.3 KB,
  ISO-8859-1) and is a KVDT *Prüflauf* message catalogue (`KVDT-FDATE`, `KVDT-FEHL`,
  `KVDT-R-FK0132`, …) for German medical-billing validation. Workflow handles no billing, no KVDT
  data and no patient data. Nothing to do.
- **Item 12 — eval gates in `..\QDocImport\Eval`.** That path does not exist relative to this
  repository; the real directory is `C:\vb5\QDocImport\QDocImport\Eval`, part of a separate
  .NET Framework 4.8 / NUnit importer whose eval engine validates KBV/KVDT imports. Adding
  Workflow assertions there would put unrelated checks into a separate regulated product. The
  intent — "a machine-checkable gate that runs after everything" — is served by `verify.ps1`
  (Task 17) and the `Workflow.Tests` suite.

  **This one is not purely a note — it has a task.** `Workflow\Prompt\implementation_prompt.md`
  still lists `* appropriate eval gates has been added to qdocimporter/eval` under
  `Completion requires:`, and that file is rendered and pasted into the phase-4 agent. Until the
  bullet is removed, the shipped product contradicts this decision and tells the implementation
  agent it may not report completion without modifying QDocImport. **Task 4 Step 3b deletes it
  and replaces it with the `verify.ps1` gate; `PromptTemplateServiceTests` then asserts that no
  `qdocimport` reference survives** (acceptance criterion A8). A declaration that the shipped
  prompt contradicts is a comment, not a decision.
