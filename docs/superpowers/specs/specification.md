---
description: "Canonical Superpowers design specification for the Workflow WPF (.NET 8) task-orchestration app"
summary: "Workflow is a WPF/.NET 8 MahApps+MaterialDesign app that drives a 4-phase Claude-Code/Codex pipeline (Spezifikation -> Review -> Review umsetzen -> Implementierung) per task tab. Each phase spawns a fresh ConPTY session hosting pwsh, rendered by xterm.js inside WebView2; phase completion is detected from artefact files (existence for 1/2, SHA-256 delta for 3, manual button for 4). Key decisions: task folder lives under the ComboBox-selected working directory; task name is locked after Start workflow; multi-line prompts are injected via bracketed paste; auto-answering is data-driven via autoanswer.rules.json because `yo` defaults to 'No, exit'. REVIEW-RESOLVED 2026-09-12: §4.4.1 pins the analyzer rules that actually fire (IDE0040 on every interface member, CA1002/CA5392/CA1062/CA1508/CA1001/CA2213) and records that UseWPF strips System.IO from implicit usings; CA1031 is never global, CA1003 is; §6.3.1 records that the documented ConPTY sequence does NOT stream output despite the child provably attaching, so Task 8 opens with a blocking spike; §6.4 makes the xterm `ready` handshake awaited; §7.3 adds Gate A (observed launcher readiness) and confirms exit-on-first-non-match is correct (D13) against the old '60 s elapses' wording; §7.5 makes hashing tri-state and watcher failure reportable; §8.1 requires root-aware path normalisation; §9.3 is rebased on the files actually on disk (only `{plan_path}_` remains, plus a NEW defect: a live qdocimporter/eval completion bullet that contradicts §14); §10.3 forwards SelectedContentTemplate; §12.4 forbids disposing a terminal on tab-switch Unloaded. Prompt instructions 8 and 12 stay Not Applicable, but item 12 is now enforced by deleting the bullet and asserting it in tests."
paths:
  - "../plans/implementationplan.md"
  - "../../../Workflow/Workflow.csproj"
  - "../../../Workflow/.editorconfig"
  - "../../../Workflow/.roslyn"
  - "../../../Workflow/Prompt/initial_prompt.md"
  - "../../../Workflow/Prompt/review_prompt.md"
  - "../../../Workflow/Prompt/resolve_review_prompt.md"
  - "../../../Workflow/Prompt/implementation_prompt.md"
---

# Workflow — Design Specification

**Status:** Approved for planning
**Date:** 2026-09-12
**Repository:** `C:\Users\Marco\Documents\repo\Workflow` (not a git repository at time of writing)
**Solution:** `C:\Users\Marco\Documents\repo\Workflow\Workflow.sln`
**Project:** `C:\Users\Marco\Documents\repo\Workflow\Workflow\Workflow.csproj`

---

## 1. Purpose

Workflow is a Windows desktop application that automates a repeatable four-stage AI-assisted
engineering pipeline. For each *Task* the user describes, Workflow drives four consecutive
terminal sessions through Claude Code and Codex, feeding each a prepared prompt, detecting
when the expected artefacts have been produced, and advancing to the next stage automatically —
while leaving the user able to type into the live terminal at any moment.

The four stages, referred to throughout as *phases*:

| # | German label      | Tool           | Produces                                   |
|---|-------------------|----------------|--------------------------------------------|
| 1 | Spezifikation     | `yo`           | `{spec_path}` and `{plan_path}`            |
| 2 | Review            | `codex --yolo` | `{review_path}`                            |
| 3 | Review umsetzen   | `yo`           | updated `{spec_path}` / `{plan_path}`      |
| 4 | Implementierung   | `yo`           | `findings.md`, `task_plan.md`, `progress.md` |

---

## 2. Glossary

German terms appear in the user-facing UI and in the prompt templates; the codebase uses the
English equivalents. This mapping is normative.

| German (UI / prompt variable) | English (code)      | Meaning                                                        |
|-------------------------------|---------------------|----------------------------------------------------------------|
| Taskbezeichnung               | `TaskName`          | Short task name. Also the tab header **and** the folder name.   |
| Taskbeschreibung              | `TaskDescription`   | Multi-line free text describing the task. Fed into prompt 1.    |
| Bezeichnung                   | —                   | Placeholder tab header shown while `TaskName` is empty.         |
| Station / Phase               | `WorkflowPhase`     | One of the four pipeline stages.                                |
| Pfad                          | `WorkingDirectory`  | Directory selected in the ComboBox; the terminal `cd`s here.    |

> **Important:** the supplied prompt templates use `{taskbeschreibung}` in two different senses —
> once correctly (the description text) and once incorrectly (in a *path*, where the
> *Bezeichnung* is meant). Section 9.3 resolves this.

---

## 3. Scope

### 3.1 In scope

- Multi-tab WPF shell; one Task per tab; `+` button to add tabs; close button per tab.
- Per-tab: task name, task description, working-directory ComboBox with directory picker,
  *Start workflow* button, four phase indicators, *Task abschliessen* button.
- Embedded, fully interactive terminal per tab (70 % of tab height, 100 % width).
- Automatic sequencing of the four phases, including auto-answering the launcher's initial
  Yes/No confirmation prompt.
- Prompt template loading with strict variable substitution.
- Artefact detection driving phase transitions.
- Task folder creation and rename (rename only while the task is idle — see §8.4).
- Persistence of the directory MRU list.
- Global *Schließen* button that exits the application.

### 3.2 Out of scope (YAGNI)

| Excluded                                   | Rationale                                                                 |
|--------------------------------------------|---------------------------------------------------------------------------|
| Session restore / reopening tabs on startup | Requirement states a single empty tab at startup. Not asked for.          |
| A DI container (`Microsoft.Extensions.*`)   | ~9 services, one window. A manual composition root is smaller and clearer. |
| Persisting per-task phase state to disk     | Not required; a task is a single sitting. Adds a resume/consistency problem. |
| Scrollback export, terminal search, themes UI | Not required.                                                            |
| Localisation infrastructure                 | UI is German-only by requirement; strings live in XAML.                   |
| Writing eval gates into `QDocImport`        | Unrelated product. See §14.                                                |

---

## 4. Existing-codebase constraints (verified, not assumed)

These were read from disk on 2026-09-12 and are binding.

### 4.1 `Workflow\Workflow.csproj` is missing its package references

The file currently contains only `OutputType`, `TargetFramework` (`net8.0-windows`), `Nullable`,
`ImplicitUsings`, `UseWPF`. However `Workflow\obj\project.assets.json` and
`Workflow\obj\Workflow.csproj.nuget.dgspec.json` prove that at last restore the project
declared, and NuGet resolved:

| Package                        | Version | Note                                                   |
|--------------------------------|---------|--------------------------------------------------------|
| `CommunityToolkit.Mvvm`        | 8.4.2   | direct                                                 |
| `MaterialDesignColors`         | 5.3.2   | direct                                                 |
| `MaterialDesignThemes`         | 5.3.2   | direct                                                 |
| `MaterialDesignThemes.MahApps` | 5.3.2   | direct                                                 |
| `MahApps.Metro`                | 2.4.11  | **transitive only** — must be promoted to direct       |
| `ControlzEx`                   | 4.4.0   | transitive of MahApps                                  |
| `Microsoft.Xaml.Behaviors.Wpf` | 1.1.77  | transitive of MaterialDesignThemes                     |

The packages are present in `C:\Users\Marco\.nuget\packages`. The implementation must restore
these `PackageReference` entries verbatim and pin exact versions.

### 4.2 `Workflow\.roslyn` is an orphan

It is a valid MSBuild `<Project>` fragment but **nothing imports it**, so none of its settings
are currently in force:

```xml
<AnalysisMode>All</AnalysisMode>
<AnalysisLevel>latest-all</AnalysisLevel>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Nullable>enable</Nullable>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);1591</NoWarn>
<AllowUnsafeBlocks>false</AllowUnsafeBlocks>
<ApiCompatEnableRuleAttributesDiagnostic>true</ApiCompatEnableRuleAttributesDiagnostic>
```

The requirement *"Beachte die Einhaltung der Regeln in .editorconfig und .roslyn"* means these
**must** be honoured. The design therefore adds a root `Directory.Build.props` that imports
`.roslyn`. Consequences are analysed in §4.4.

### 4.3 `Workflow\.editorconfig` (`root = true`)

Binding rules that shape the code:

- `dotnet_style_qualification_for_*` = `false:error` — never write `this.`.
- `dotnet_style_require_accessibility_modifiers = always:error` — every member gets an explicit modifier.
- `csharp_style_var_for_built_in_types` / `csharp_style_var_when_type_is_apparent` = `true:error` — use `var`.
- `csharp_style_nullable_reference_type_annotations = true:error`.
- Interfaces must be `I`-prefixed.
- **Private fields must be `_`-prefixed** (`error`).
- `CA1822`, `IDE0051`, `IDE0052`, `IDE0059`, `CA1825`, `CA1806` = `error`.

> **Interaction with `[ObservableProperty]`:** the MVVM Toolkit generator must be applied to a
> *field*, not an auto-property (`MVVMTK0040`). The generator strips a leading underscore, so
> `[ObservableProperty] private string _taskName;` generates the public property `TaskName`.
> This is compatible with the `_`-prefix naming rule. **Do not** attempt partial-property
> syntax: that requires C# 13, and `net8.0-windows` defaults to C# 12.

> `.editorconfig` sits inside `Workflow\Workflow\` with `root = true`, so a sibling
> `Workflow\Workflow.Tests\` project does **not** inherit it. The test project is therefore
> governed only by `Directory.Build.props`. Note that this exempts the test project from the
> *style* rules (`IDE****`) but **not** from the CA analyzers, which come from `.roslyn`.

> **`UseWPF=true` shrinks the implicit-using set (verified by probe build, 2026-09-12).**
> `ImplicitUsings=enable` on a WPF project emits only `System`, `System.Collections.Generic`,
> `System.Linq`, `System.Threading` and `System.Threading.Tasks` — **`System.IO` and
> `System.Net.Http` are absent**, unlike a plain `Microsoft.NET.Sdk` project. Both `Workflow`
> and `Workflow.Tests` set `UseWPF=true`, so **every file that touches `Path`, `File`,
> `Directory`, `FileStream`, `FileSystemWatcher` or `IOException` must carry an explicit
> `using System.IO;`** — production code and test code alike. Omitting it is `CS0103`, not a
> warning.

### 4.4 Analyzer fallout — curated `NoWarn` (required, with justification)

`AnalysisMode=All` + `AnalysisLevel=latest-all` + `TreatWarningsAsErrors=true` enables every CA
rule as a build error. Several are wrong for this application. The following suppressions are
**part of the design**, each justified; no other blanket suppression is permitted.

| Rule        | Why suppressed                                                                                                       |
|-------------|----------------------------------------------------------------------------------------------------------------------|
| `CA2007`    | "Consider calling ConfigureAwait" — in WPF, continuations *must* return to the UI thread. Actively harmful here.       |
| `CA1303`    | "Do not pass literals as localized parameters" — UI is German-only by requirement; no resource tables (§3.2).          |
| `SYSLIB1054`| "Use `LibraryImport`" — `LibraryImport` generates `unsafe` marshalling code; `.roslyn` sets `AllowUnsafeBlocks=false`. |
| `CA1031`    | Suppressed **file-scoped** only in the PTY read loop and the top-level dispatcher handler, where a broad catch is the correct resilience boundary. Never globally. |
| `CA1812`    | ViewModels/services instantiated only by the composition root or by XAML are flagged as uninstantiated.               |
| `CA1848`    | "Use LoggerMessage delegates" — no `ILogger` is used (§3.2); rule fires on nothing but noise if logging is added later.|
| `CA1515`    | "Consider making public types internal" — enabled for `WinExe` under `latest-all`. Making every ViewModel/service internal would force `InternalsVisibleTo` for the test project and fight XAML tooling for no benefit. |
| `CA1003`    | "Use generic event handler instances" — CA1003 demands `EventHandler<T>` with `T : EventArgs`. The only two events in the app (`ITerminalSession.OutputReceived`, `.Exited`) carry a raw `ReadOnlyMemory<byte>` payload and an `int` exit code. Wrapping either in an `EventArgs` subclass buys nothing and allocates on the 4 KiB PTY read path. `EventHandler<T>` with a non-`EventArgs` `T` is idiomatic modern C#. |

The **test project** carries its own additional suppressions, because analyzer rules written for
production libraries misfire on test code:

| Rule     | Why suppressed in `Workflow.Tests` only                                                  |
|----------|------------------------------------------------------------------------------------------|
| `CA1707` | "Remove underscores from member names" — test names use `Method_Condition_Expectation`.   |
| `CA1822` | `[Fact]` methods do not touch instance state and would all be forced `static`.            |
| `CA2007` | Test bodies `await` freely; there is no synchronisation context to return to.             |
| `CA1303` | Assertion messages and fixture literals are not localised.                                |
| `CA1861` | Inline constant arrays are the clearest form for table-driven test data.                  |

One targeted, non-blanket suppression lives in code rather than the project file:
`AppSettings` carries `[SuppressMessage("Usage", "CA2227")]` because `System.Text.Json` requires
a settable collection property to populate `RecentDirectories`.

`CS1591` is already suppressed by `.roslyn`. XML doc comments are still written on all public
types and members because `GenerateDocumentationFile` is on and documentation is cheap insurance.

**`CA1031` is never in `NoWarn`.** It is suppressed *only* by a file- or line-scoped
`#pragma warning disable CA1031` at the two places where a broad catch is the correct resilience
boundary (the PTY read loop and the top-level dispatcher handler). A repository-wide entry would
silently disable broad-catch diagnostics for all future code and is forbidden by A2.

### 4.4.1 Analyzer rules that fire on this design — verified, with the required remedy

`AnalysisMode=All` + `AnalysisLevel=latest-all` + `TreatWarningsAsErrors=true` turns the rules
below into **build errors** on the code shapes this design uses. Each was reproduced on
2026-09-12 in a probe project carrying this repository's exact `.roslyn`, `.editorconfig` and
`Directory.Build.props`. They are listed here because they are *design obligations*, not
incidental lint: the remedy is code, not suppression.

| Rule | Fires on | Required remedy (not a suppression) |
|---|---|---|
| `IDE0040` | **Every interface member.** `.editorconfig` sets `dotnet_style_require_accessibility_modifiers = always:error`, which — unlike the .NET default `for_non_interface_members` — requires an explicit modifier on interface members too. | Write `public` on every interface member: `public Task WaitAsync(CancellationToken ct);`. |
| `CA1002` | A public member returning `List<T>`. | Expose `Collection<T>`, `IReadOnlyList<T>` or `IList<T>`. `AppSettings.RecentDirectories` becomes `Collection<string>` (still populated by `System.Text.Json`). |
| `CA5392` | Every `[DllImport]` without a search-path declaration. | Put `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` on the `NativeMethods` class. This is a real hardening fix, not noise: all imports are `kernel32`. |
| `CA1062` | A public method that dereferences a reference-type parameter without a null check. | `ArgumentNullException.ThrowIfNull(x)` / `ArgumentException.ThrowIfNullOrWhiteSpace(x)` as the first statement of every public entry point. |
| `CA1001` | A non-disposable type holding a disposable field (`CancellationTokenSource`, `FileSystemWatcher`, `DispatcherTimer`, `FileStream`, `SemaphoreSlim`). | Implement `IDisposable` on the owner. |
| `CA2213` | A disposable field the owner's `Dispose` does not dispose. | Dispose every owned field, including on the early-return paths. |
| `CA1508` | A null check or comparison the compiler can prove is always true/false — easy to introduce alongside `ArgumentNullException.ThrowIfNull`. | Delete the redundant check; do not add a second guard after a `ThrowIfNull`. |
| `CA1806` | An ignored return value (already `error` via `.editorconfig`). | Consume the result or assign it to `_`. |

Two consequences are binding on every task in the plan:

1. **Each task ends with a 0-warning, 0-error `dotnet build`** (A1/A7). A task is not complete
   while it leaves analyzer errors for a later task to clean up.
2. **A new `NoWarn` entry is a specification change.** If a rule not listed in §4.4 blocks a
   build, fix the code. Only if that is genuinely wrong for this application may §4.4 be amended
   — in this document first, then in `Directory.Build.props`, with the justification comment.

### 4.5 Prompt templates are not copied to the output directory

SDK-style WPF projects do not auto-include `*.md`. `Workflow\Prompt\*.md` currently has no
MSBuild item, so at runtime the folder will be absent from `bin\`. The requirement states
*"Alle Prompts sind als Ressource angelegt und werden ins Ausgabeverzeichnis kopiert"* — this
must be made true with explicit `<Content>` items and `CopyToOutputDirectory=PreserveNewest`.

### 4.6 Environment facts (verified)

| Fact                       | Value                                                                 |
|----------------------------|-----------------------------------------------------------------------|
| `yo`                       | `C:\Windows\yo.bat` → `@claude --dangerously-skip-permissions %*`      |
| `claude`                   | `C:\Users\Marco\.local\bin\claude.exe`                                 |
| `codex`                    | `C:\nvm4w\nodejs\codex.cmd`                                            |
| PowerShell 7               | `C:\Program Files\PowerShell\7\pwsh.exe` (present)                      |
| Windows PowerShell         | `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`             |
| WebView2 Runtime           | `152.0.4191.66` installed                                              |
| .NET SDKs                  | 7.0.203, 7.0.317, 8.0.425, 9.0.302, 10.0.401                           |
| `Microsoft.WindowsDesktop.App` | 8.0.18 / 8.0.31 present                                            |

---

## 5. Architecture

### 5.1 Layer diagram

```
+---------------------------------------------------------------+
|  Views (XAML, zero business logic)                            |
|  MainWindow · TaskTabView · TerminalView · PhaseIndicatorView |
+-------------------------------+-------------------------------+
                                | DataContext / bindings
+-------------------------------v-------------------------------+
|  ViewModels (CommunityToolkit.Mvvm)                           |
|  MainWindowViewModel · TaskTabViewModel · TerminalViewModel   |
|  PhaseIndicatorViewModel                                      |
+-------------------------------+-------------------------------+
                                | interfaces only
+-------------------------------v-------------------------------+
|  Services                                                     |
|  IWorkflowOrchestrator   IPromptTemplateService               |
|  ITaskFolderService      IArtifactWatcher                     |
|  IAutoAnswerService      ISettingsService                     |
|  IDirectoryPickerService ITerminalSessionFactory              |
+-------------------------------+-------------------------------+
                                |
+-------------------------------v-------------------------------+
|  Terminal (ConPTY)                                            |
|  ConPtySession : ITerminalSession                             |
|  NativeMethods · SafePseudoConsoleHandle                      |
|  SafeProcThreadAttributeList                                  |
+---------------------------------------------------------------+
```

Dependency rule: arrows point downward only. Views know ViewModels; ViewModels know service
*interfaces*; services know the terminal abstraction. Nothing below knows anything above.
This is what makes everything except `TerminalView` and `ConPtySession` unit-testable.

### 5.2 File layout

```
C:\Users\Marco\Documents\repo\Workflow\
├─ Directory.Build.props                  (NEW — imports Workflow\.roslyn, sets NoWarn)
├─ Workflow.sln                           (EXISTS — add Workflow.Tests)
├─ docs\superpowers\
│   ├─ specs\specification.md             (this file)
│   └─ plans\implementationplan.md
├─ Workflow\
│   ├─ Workflow.csproj                    (MODIFY)
│   ├─ .editorconfig                      (EXISTS — unchanged)
│   ├─ .roslyn                            (EXISTS — unchanged)
│   ├─ App.xaml / App.xaml.cs             (MODIFY — theme merge + composition root)
│   ├─ AssemblyInfo.cs                    (EXISTS — unchanged)
│   ├─ Assets\
│   │   ├─ workflow.ico                   (NEW — generated, ArmFlex)
│   │   ├─ autoanswer.rules.json          (NEW — Content, PreserveNewest)
│   │   └─ Terminal\                      (NEW — Content, PreserveNewest)
│   │       ├─ terminal.html
│   │       ├─ terminal.js
│   │       ├─ xterm.css
│   │       ├─ xterm.js
│   │       └─ addon-fit.js
│   ├─ Models\
│   │   ├─ WorkflowPhase.cs   PhaseStatus.cs   PhaseDefinition.cs
│   │   ├─ AutoAnswerRule.cs  AutoAnswerRuleSet.cs
│   │   ├─ TaskPaths.cs       AppSettings.cs
│   ├─ Services\  (interface + implementation per file pair)
│   ├─ Terminal\
│   │   ├─ ITerminalSession.cs  ConPtySession.cs
│   │   └─ Native\ NativeMethods.cs  SafePseudoConsoleHandle.cs
│   │            SafeProcThreadAttributeList.cs  NativeStructs.cs
│   ├─ ViewModels\
│   ├─ Views\
│   ├─ Converters\
│   ├─ Behaviors\ RichTextBoxAssist.cs
│   ├─ Styles\ TabControlStyles.xaml  PhaseIndicatorStyles.xaml
│   ├─ verify.ps1                         (NEW — acceptance gate script, §15.3)
│   └─ Prompt\*.md                        (EXISTS — 1 empty, 2 defective; see §9)
└─ Workflow.Tests\                        (NEW — xUnit)
    └─ Workflow.Tests.csproj + test classes
```

### 5.3 Composition root

`App.OnStartup` builds the object graph by hand — no container:

```
SettingsService(appDataPath)
PromptTemplateService(promptDirectory)
AutoAnswerService(rulesPath)          -> reads Assets\autoanswer.rules.json,
                                         overridden by %APPDATA%\Workflow\autoanswer.rules.json
TaskFolderService()
ArtifactWatcherFactory()                -> creates one IArtifactWatcher per phase
DirectoryPickerService()
ConPtyTerminalSessionFactory()
TaskTabViewModelFactory(all of the above)
MainWindowViewModel(TaskTabViewModelFactory, SettingsService)
```

`MainWindow.DataContext = mainViewModel`. `StartupUri` is removed from `App.xaml`; the window is
constructed in code so the DataContext can be injected.

---

## 6. Terminal subsystem

### 6.1 Why ConPTY

`yo` (Claude Code) and `codex` are full-screen TUIs built on Ink. They use the alternate screen
buffer, absolute cursor addressing and SGR colour. Redirected pipes would put them in a degraded
non-interactive mode and the output would be unreadable if naively appended. A Windows
pseudo-console (`CreatePseudoConsole`, Windows 10 1809+) is therefore mandatory.

### 6.2 `ITerminalSession`

```csharp
public interface ITerminalSession : IDisposable
{
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;            // exit code
    public bool IsRunning { get; }
    public void Start(string executable, string arguments, string workingDirectory, int columns, int rows);
    public void Write(ReadOnlySpan<byte> data);
    public void Resize(int columns, int rows);
}
```

Every member carries an explicit `public`: `.editorconfig` sets
`dotnet_style_require_accessibility_modifiers = always`, which makes a bare interface member an
`IDE0040` build error (§4.4.1). This applies to **every** interface in the codebase.

### 6.3 `ConPtySession` — native call sequence

P/Invoke uses `DllImport` with `IntPtr` + `Marshal` throughout. No `unsafe` block is needed
anywhere, which satisfies `AllowUnsafeBlocks=false`.

1. `CreatePipe(out ptyInRead, out appWrite, IntPtr.Zero, 0)`
2. `CreatePipe(out appRead, out ptyOutWrite, IntPtr.Zero, 0)`
3. `CreatePseudoConsole(new Coord(cols, rows), ptyInRead, ptyOutWrite, 0, out hPC)` — returns an
   `HRESULT`; pass to `Marshal.ThrowExceptionForHR`.
4. **Close `ptyInRead` and `ptyOutWrite` in this process.** The pseudo-console owns duplicates.
   Failing to close them means the read loop never sees EOF.
5. `InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size)` to size the list,
   `Marshal.AllocHGlobal(size)`, then call again to initialise.
6. `UpdateProcThreadAttribute(list, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)`
7. `CreateProcess(null, commandLine, …, dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, lpCurrentDirectory: workingDirectory, ref startupInfoEx, out processInfo)`
8. `DeleteProcThreadAttributeList` + `FreeHGlobal`; close `hThread`.

Constants: `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016`,
`EXTENDED_STARTUPINFO_PRESENT = 0x00080000`, `CREATE_UNICODE_ENVIRONMENT = 0x00000400`.

**Read loop.** A long-running `Task` reads `appRead` into a 4 KiB buffer and raises
`OutputReceived` with the raw bytes. Bytes are *never* decoded to `string` in C# — a UTF-8
sequence can straddle a read boundary and xterm.js decodes UTF-8 itself.

**Shutdown order** (getting this wrong hangs the app on exit):
1. Kill the child process tree: `Process.GetProcessById(processInfo.dwProcessId).Kill(entireProcessTree: true)`.
2. `ClosePseudoConsole(hPC)` — it can block until the attached client exits, hence step 1 first.
3. Dispose `appWrite`, then `appRead`; the read loop observes EOF and completes.
4. Await the read task with a 2 s timeout.

`SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid` and
`SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid` own steps 2 and 5/8.

### 6.3.1 The ConPTY edge is UNVALIDATED and must be spiked first — binding

The call sequence above is derived from the Windows documentation and the `microsoft/terminal`
sample. **It has not been shown to stream output on this machine, and a materialised version of
it does not.** On 2026-09-12 the sequence was built verbatim as a standalone console app and run
both under a real Windows console host and under a redirected shell. Findings:

- `CreatePipe`, `CreatePseudoConsole`, `InitializeProcThreadAttributeList`,
  `UpdateProcThreadAttribute` and `CreateProcess` **all return success**, and the attribute-list
  buffer was byte-dumped and confirmed to contain `0x00020016`, `cbSize = 8` and the `HPCON`.
  `STARTUPINFOEX.cb` is 112 and `lpAttributeList` points at that buffer.
- The child **is** correctly attached to the pseudo-console: launching
  `cmd.exe /c mode con` inside a PTY created at 137 × 41 makes the child report
  `Zeilen: 41 / Spalten: 137`. Attachment is therefore *not* the defect.
- **Zero bytes are ever read from the output pipe** for the child's own output —
  with `pwsh -NoLogo -NoExit` and with `cmd.exe /c echo`.
- Output appears *only* after an explicit `ResizePseudoConsole` (115–126 bytes), which proves the
  output pipe itself is wired correctly and readable.

Ruled out by experiment, so the spike must **not** re-test these: closing the PTY-side handles
immediately vs. after a 250 ms delay vs. after `CreateProcess`; starting the read loop before vs.
after `CreateProcess`; `string` vs. `StringBuilder` for `lpCommandLine`; passing
`lpApplicationName`; `CREATE_UNICODE_ENVIRONMENT` on/off; `bInheritHandles` true/false;
`FileStream` vs. a raw `ReadFile` P/Invoke on the read handle; `cmd.exe` vs. `pwsh.exe`.

**Consequence for the plan:** Task 8 opens with a blocking spike step. No later task may be
started until a minimal spike demonstrably streams a command's output out of the pseudo-console,
and the *proven* sequence — not the one above — is what gets written into `ConPtySession`. If the
spike changes the call order, handle ownership or read strategy, §6.3 is updated to match before
the code is committed. Remaining untested candidates worth trying first: opening the read handle
for overlapped I/O and reading asynchronously; `PSEUDOCONSOLE_INHERIT_CURSOR` (`dwFlags = 1`,
which did change behaviour in the probe); and creating the pipes with inheritable
`SECURITY_ATTRIBUTES` rather than `NULL`.

### 6.4 Hosting: WebView2 + xterm.js

`TerminalView.xaml` hosts a `Microsoft.Web.WebView2.Wpf.WebView2`. After
`EnsureCoreWebView2Async`:

```csharp
core.SetVirtualHostNameToFolderMapping(
    "workflow.terminal",
    Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"),
    CoreWebView2HostResourceAccessKind.DenyCors);
core.Navigate("https://workflow.terminal/terminal.html");
```

Each tab gets its **own** `WebView2` but they share one `CoreWebView2Environment` created once
with a user-data folder under `%LOCALAPPDATA%\Workflow\WebView2`. Creating an environment per
control is a documented source of failures.

**Message protocol** (JSON over `postMessage`, base64 for all binary payloads):

| Direction | Message                                   | Meaning                                    |
|-----------|-------------------------------------------|--------------------------------------------|
| C# → JS   | `{"type":"out","b64":"…"}`                | PTY bytes to render (`term.write(bytes)`)  |
| C# → JS   | `{"type":"clear"}`                        | reset the terminal between phases          |
| JS → C#   | `{"type":"ready"}`                        | xterm constructed **and the message listener installed**; C# may start the PTY |
| JS → C#   | `{"type":"in","b64":"…"}`                 | user keystrokes → `session.Write`          |
| JS → C#   | `{"type":"resize","cols":n,"rows":n}`     | `FitAddon` result → `ResizePseudoConsole`  |

`PostWebMessageAsString` is used rather than `ExecuteScriptAsync` for the high-frequency output
path: it avoids JavaScript source escaping and is markedly cheaper. Output is **coalesced on a
16 ms timer** so a burst of PTY reads becomes one message.

**The `ready` handshake is awaited, not merely observed.** `ready` is the *precondition* for
starting a PTY, so it is part of the terminal's readiness contract:

- `AttachAsync` does not return until either `ready` has arrived or the WebView2 failed; a
  `ready` timeout (10 s) is treated as a terminal failure and surfaces the §12.1 message.
- `IsTerminalAvailable` becomes `true` only **after** `ready`, never merely after `Navigate`.
- `StartSession`, `ClearScreen` and the first `SnapshotAsync` are all gated on the same
  completion.

Without this gate a fast *Start workflow* sends `clear`, PTY bytes and a snapshot request before
`terminal.js` has installed its `message` listener. Those bytes are dropped silently, and the
launcher's startup screen — the exact screen the auto-answer rules must match (§7.3) — is the one
that gets lost.

**Screen snapshots** for auto-answer use the low-frequency path
`ExecuteScriptAsync("window.wfSnapshot(60)")`, which returns the last 60 rendered rows:

```js
window.wfSnapshot = function (n) {
  const b = term.buffer.active;
  const start = Math.max(0, b.length - n);
  const out = [];
  for (let i = start; i < b.length; i++) {
    const line = b.getLine(i);
    if (line) out.push(line.translateToString(true));
  }
  return out.join('\n');
};
```

Reading the *rendered buffer* rather than the raw byte stream is deliberate: it means the
auto-answer rules match what a human sees, not a soup of escape sequences.

**Vendored assets.** `xterm.js`, `xterm.css` (`@xterm/xterm` v5.5.0) and `addon-fit.js`
(`@xterm/addon-fit` v0.10.0) are committed under `Assets\Terminal\`. No CDN: the WebView2 is
navigated to a virtual host and must work offline.

### 6.5 Writing a prompt into a TUI — bracketed paste

Prompt bodies are multi-line. Sending a raw `\n` or `\r` into Claude Code's input box submits
the partial text. The correct mechanism is bracketed paste:

```
ESC [ 2 0 0 ~   <body, all newlines normalised to CR>   ESC [ 2 0 1 ~
```

then, as a **separate** write after a short delay (150 ms), a single `CR` to submit. Both
Claude Code and Codex enable bracketed-paste mode (`DECSET 2004`) in their TUIs.

**The delayed submit is bound to the session that received the paste.** `SendPaste` returns a
`Task` that the orchestrator awaits, and the delay is cancelled when the session it targeted is
replaced or disposed. A fire-and-forget `Task.Delay(150)` that then calls `Send("
")` on
whatever `_session` happens to be current is a real defect, not a theoretical one: §8.3 allows a
task folder to be reused, so a phase whose artefacts already exist on disk can complete inside
those 150 ms, and the orchestrator will have started the next phase's session. The stray `CR`
would then land in a fresh launcher — accepting whatever option it has preselected, which for
`yo` is *"No, exit"* (§7.4).

---

## 7. Workflow orchestration

### 7.1 Phase model

```csharp
public enum WorkflowPhase { Specification = 0, Review = 1, ResolveReview = 2, Implementation = 3 }
public enum PhaseStatus  { Pending, Active, Completed }
```

`PhaseDefinition` is an immutable record describing one phase; the four instances live in a
static `PhaseCatalog`:

| Phase           | `DisplayName`     | `Launcher`      | `PromptFile`                 | `Completion`        |
|-----------------|-------------------|-----------------|------------------------------|---------------------|
| Specification   | Spezifikation     | `yo`            | `initial_prompt.md`          | `FilesExist(spec, plan)` |
| Review          | Review            | `codex --yolo`  | `review_prompt.md`           | `FilesExist(review)`     |
| ResolveReview   | Review umsetzen   | `yo`            | `resolve_review_prompt.md`   | `AnyContentChanged(spec, plan)` |
| Implementation  | Implementierung   | `yo`            | `implementation_prompt.md`   | `Manual`                 |

### 7.2 Per-phase sequence (`WorkflowOrchestrator.RunPhaseAsync`)

1. Raise `PhaseChanged(phase, Active)`.
2. Snapshot completion state: for `ResolveReview`, compute and store SHA-256 of `spec_path` and
   `plan_path` *before* anything runs.
3. **Await terminal readiness** (§6.4): the WebView2 has navigated *and* `terminal.js` has sent
   `ready`. Then create a fresh `ITerminalSession` and start `pwsh.exe -NoLogo -NoExit`
   (fall back to `powershell.exe -NoLogo -NoExit` if `pwsh.exe` is absent), working directory =
   `WorkingDirectory`.
4. Send `cd "{WorkingDirectory}"` + CR. Always quoted — paths contain spaces and umlauts.
5. Send `{Launcher}` + CR.
6. **Settle-and-answer loop** (§7.3).
7. Render the phase's prompt (§9) and inject it by bracketed paste (§6.5). The injection is
   awaited, including its trailing submit `CR`, before step 8 begins.
8. Start the `IArtifactWatcher` for this phase and `await` its completion signal, the user's
   manual *Phase abschliessen* / *Task abschliessen* click, or cancellation.
9. Raise `PhaseChanged(phase, Completed)`. **The session is left running and visible** — the user
   may still read it. It is disposed when the *next* phase's session starts, when the tab closes,
   or at application exit.

Phases 1→2→3→4 chain automatically. Phase 4 ends only on *Task abschliessen*.

### 7.3 Settle-and-answer loop

The launcher prints a confirmation question before accepting input. Handling:

```
loop:
  # Gate A - the launcher must have produced something before "quiet" means anything.
  wait until launcherHasRendered                            (see below)
  # Gate B - the screen must then stop changing.
  wait until (now - lastOutputTimestamp) >= QuietPeriodMs   (default 1500)
  text := await wfSnapshot(60)
  rule := AutoAnswerService.Match(text, alreadyFiredRuleIds)
  if rule is null            -> exit loop        # settled, nothing to answer
  if answersFired >= MaxAnswersPerPhase (default 5) -> exit loop
  session.Write(rule.Send)
  alreadyFiredRuleIds += rule.Id
  answersFired++
  reset lastOutputTimestamp
```

**Gate A — observed launcher readiness (load-bearing).** `lastOutputTimestamp` must **not** be
seeded with a value that makes the terminal look quiet before the launcher has drawn anything.
Two concrete requirements:

- `LastOutputUtc` is reset to "now" by `StartSession`, and the loop additionally requires at
  least one `OutputReceived` **after** the launcher command was written, plus a non-empty
  snapshot, before Gate B is evaluated.
- Seeding `LastOutputUtc` with `DateTimeOffset.MinValue` and relying on the quiet period alone is
  a defect: on a fresh session `now - MinValue` is enormous, so the very first iteration
  considers the terminal settled, snapshots an **empty** xterm buffer, matches no rule, and sends
  the prompt before `yo`/`codex` has rendered its confirmation. The prompt is then swallowed by
  the still-blocking dialog and the phase stalls.
- Gate A is itself bounded by `SettleTimeoutMs`; if the launcher never draws, the loop falls
  through to the timeout path below.

**Exit on first non-match is correct and deliberate.** Once the screen is genuinely quiet and no
rule matches, the launcher is at its input prompt and the prompt is sent immediately. The loop
does **not** wait out `SettleTimeoutMs` in that case: doing so would add a minute to *every*
phase of *every* task, which is the normal path, not the exceptional one.

`SettleTimeoutMs` (default 60 000) is therefore a **ceiling on the whole loop**, reached only
when Gate A never opens or when rules keep matching. On reaching it the loop exits and the prompt
is sent anyway; the user can always intervene by typing. §12.3 and V7 are written against this
reading.

Each rule fires **at most once per phase** (tracked by `Id`), which prevents an infinite
answer/redraw cycle when a rule's pattern remains visible on screen after being answered.

### 7.4 Auto-answer rules — the primary fragility

`yo` expands to `claude --dangerously-skip-permissions`, whose bypass-permissions warning offers
`1. No, exit` / `2. Yes, I accept` with **"No" preselected**. A naive "send Enter" would answer
*No* and the whole pipeline would die silently. Because upstream CLI wording changes without
notice, the rules are **data, not code**.

`Assets\autoanswer.rules.json` (Content, `PreserveNewest`); an optional
`%APPDATA%\Workflow\autoanswer.rules.json` replaces it wholesale if present:

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
      "send": "2\r",
      "description": "claude --dangerously-skip-permissions warning; option 2 = 'Yes, I accept'. Default selection is 'No, exit', so Enter alone is wrong."
    },
    {
      "id": "claude-trust-folder",
      "pattern": "(?is)do you trust the files in this folder",
      "send": "\r",
      "description": "Trust dialog; option 1 'Yes, proceed' is preselected."
    },
    {
      "id": "codex-yolo-warning",
      "pattern": "(?is)(--yolo|full auto|auto-approve).{0,200}(continue|proceed|accept)",
      "send": "\r",
      "description": "codex --yolo confirmation; affirmative option is preselected."
    },
    {
      "id": "generic-yes-no",
      "pattern": "(?is)\\(y/n\\)\\s*$",
      "send": "y\r",
      "description": "Plain readline-style yes/no fallback."
    }
  ]
}
```

Rules are evaluated **in order**; the first match wins. `pattern` is a .NET regex compiled with
`RegexOptions.Compiled` and a 250 ms `matchTimeout` (guards against catastrophic backtracking in
a user-edited file). `send` supports the C-style escapes `\r`, `\n`, `\t`, `\e`, `\\`, and
`\u####`.

### 7.5 Artefact detection (`ArtifactWatcher`)

Two rules, both implemented by the same watcher:

- **`FilesExist(paths…)`** — signals when every path exists *and* has length > 0.
- **`AnyContentChanged(paths…)`** — signals when the SHA-256 of any path differs from the
  baseline captured at phase start (a file that did not exist at baseline and now exists also
  counts as changed).

Implementation: a `FileSystemWatcher` on the task directory
(`NotifyFilters.LastWrite | FileName | Size`, `IncludeSubdirectories = false`) **plus** a 1 s
`PeriodicTimer` poll. The poll is not redundant belt-and-braces — `FileSystemWatcher` is known
to drop events on virtualised, network and OneDrive-backed directories, and a missed event here
stalls the pipeline permanently.

Every candidate signal is debounced by 750 ms and then re-verified by actually reading the file,
because an editor may create a zero-byte file before writing it.

**Hashing is tri-state — "unreadable" is not "changed".** `ComputeHash` returns one of three
outcomes, and `AnyContentChanged` treats them differently:

| Outcome | Meaning | Effect on `AnyContentChanged` |
|---|---|---|
| `Hash(value)` | The file was read end to end. | Compared against the baseline; differing ⇒ changed. |
| `Missing` | `File.Exists` is false. | Differs from a `Hash` baseline ⇒ changed. A `Missing` baseline that is still `Missing` ⇒ unchanged. |
| `Unreadable` | `IOException` / `UnauthorizedAccessException` — another process holds the file. | **Never** reports a change. The path is retried on the next poll. |

Collapsing `Unreadable` into `null` and comparing it with a non-null baseline reports a content
change for a file whose bytes have not moved. During phase 3 the spec and the plan are exactly
the files an editor or the running CLI is most likely to have open, so this would advance the
workflow on a transient lock. Baselines captured as `Unreadable` are re-captured on the first
poll that succeeds, so a lock held at phase start does not permanently arm the watcher either.

**Watcher failure is reported, not absorbed.** `IArtifactWatcher` completes with either
*satisfied* or *failed*:

- `FileSystemWatcher.Error` is subscribed; an error (buffer overflow, or the directory going
  away) fails the watcher.
- The poll additionally checks `Directory.Exists(directory)`. Once the watched directory has
  disappeared, the watcher fails rather than continuing to poll a condition that can never hold.
- A failed watcher surfaces a German message through the orchestrator and **stops the phase**
  instead of leaving the indicator yellow forever (§12.2).

### 7.6 Cancellation

Each `TaskTabViewModel` owns a `CancellationTokenSource`. Closing the tab, clicking
*Task abschliessen*, or application shutdown cancels it, which disposes the live session
(§6.3 shutdown order) and stops all watchers.

---

## 8. Task folder management

### 8.1 Paths (`TaskPaths`, a computed record)

Given `WorkingDirectory = W` and `TaskName = N`:

| Member                 | Value                             | Used as                          |
|------------------------|-----------------------------------|----------------------------------|
| `TaskDirectory`        | `W\N`                             | folder created on disk           |
| `SpecAbsolute`         | `W\N\N_spec.md`                   | artefact watching                |
| `PlanAbsolute`         | `W\N\N_plan.md`                   | artefact watching                |
| `ReviewAbsolute`       | `W\N\N-review.md`                 | artefact watching                |
| `SpecRelative`         | `./N/N_spec.md`                   | `{spec_path}` in prompts         |
| `PlanRelative`         | `./N/N_plan.md`                   | `{plan_path}` in prompts         |
| `ReviewRelative`       | `./N/N-review.md`                 | `{review_path}` in prompts       |

Relative forms use forward slashes and a leading `./`, matching the notation in the requirement
and in `review_prompt.md`. They are relative to `W`, which is where the CLI runs after `cd`.

**Normalisation of `W` must be root-aware.** A trailing separator is stripped with
`Path.TrimEndingDirectorySeparator`, **not** `TrimEnd(Path.DirectorySeparatorChar,
Path.AltDirectorySeparatorChar)`. The naive trim turns a perfectly valid picker result `C:\`
into `C:`, which Windows reads as the *drive-relative* current directory — so `W\N` becomes
`C:N` (a path next to whatever `C:`'s working directory happens to be), the emitted
`cd "C:"` does not change to the drive root, and the MRU entry persists the broken form. The same
rule binds `TaskPaths`, `TaskFolderService.Validate` and `SettingsService.AddRecentDirectory`;
all three currently share the defective trim, so all three carry a drive-root test.

**`{AppDirectory}` resolves to `W`**, the selected working directory — *not* to the folder
containing `Workflow.exe`. `implementation_prompt.md` writes
`{AppDirectory}\{taskbezeichnung}\task_plan.md`, which therefore lands in `TaskDirectory`,
alongside every other artefact. This resolves the contradiction between the requirement text
(`cd {pfad}` then relative `./`) and the template's `{AppDirectory}`.

### 8.2 Name validation (`TaskFolderService.Validate`)

A name is valid when **all** hold:

- non-empty after trimming;
- contains no character from `Path.GetInvalidFileNameChars()` (this includes `\ / : * ? " < > |`);
- does not end with `.` or a space (Windows silently strips these, breaking round-tripping);
- is not a reserved device name, case-insensitively, with or without extension:
  `CON PRN AUX NUL COM1..COM9 LPT1..LPT9`;
- `W\N` is ≤ 240 characters (leaves headroom under `MAX_PATH` for the artefact filenames).

Invalid names disable *Start workflow* and surface an inline German validation message. No
folder is created for an invalid name.

### 8.3 Creation and rename

- The folder is created lazily: on the first debounced (500 ms) valid non-empty name, and again
  on rename.
- Rename uses `Directory.Move(old, new)`. A case-only rename (`foo` → `Foo`) is performed via a
  two-step move through a temporary name, because Windows treats it as a no-op otherwise.
- **A failed rename rolls `TaskName` back to the name that is actually on disk.** On
  `IOException` (collision or lock) or `UnauthorizedAccessException` the message is shown *and*
  `TaskName` is restored from the tracked on-disk name, under a re-entrancy guard so the
  restoring assignment does not re-trigger the debounced folder sync. Leaving `TaskName` and the
  on-disk folder disagreeing is worse than the failure itself: the tab header, the artefact paths
  in `TaskPaths`, and the `{taskbezeichnung}` token would all name a folder that does not exist,
  and the user has no way to tell which is authoritative.
- If `W\N` already exists, it is **reused, not overwritten**, and an informational message is
  shown ("Ordner existiert bereits und wird weiterverwendet"). Prior artefacts in it may cause
  phase 1 to complete instantly; that is acceptable and visible.

### 8.4 Name is locked after *Start workflow*

Once phase 1 starts, `TaskName` becomes read-only and no further folder rename is attempted.

Rationale: the running `pwsh` has its current directory inside `W` and `yo` may have opened
files inside `W\N`; `Directory.Move` would throw `IOException` (`ERROR_SHARING_VIOLATION`).
Locking removes the failure mode entirely rather than handling it. The tab header keeps
displaying the locked name for the rest of the run.

---

## 9. Prompt templates

### 9.1 Renderer (`PromptTemplateService`)

- Loads from `{AppContext.BaseDirectory}\Prompt\{fileName}` with UTF-8 (BOM-tolerant).
- Substitutes `{name}` tokens from a supplied dictionary.
- **Strict:** any `{token}` left unresolved throws `PromptTemplateException` naming the token and
  the file. Silent partial substitution would ship a broken prompt to the CLI, which is far worse
  than failing fast.
- Empty or whitespace-only template → `PromptTemplateException`.
- Normalises all line endings to `\r` for bracketed paste (§6.5) at the *injection* boundary, not
  in the renderer — the renderer returns normal text so it is readable in tests.

### 9.2 Variable set (the whole contract)

| Token                | Value                                        |
|----------------------|----------------------------------------------|
| `{taskbezeichnung}`  | `TaskName`                                   |
| `{taskbeschreibung}` | `TaskDescription` (plain text)               |
| `{AppDirectory}`     | `WorkingDirectory` (absolute, no trailing \) |
| `{spec_path}`        | `TaskPaths.SpecRelative`                     |
| `{plan_path}`        | `TaskPaths.PlanRelative`                     |
| `{review_path}`      | `TaskPaths.ReviewRelative`                   |

Any token outside this set is an error.

### 9.3 Defects in the supplied templates — re-verified against the files on disk

> **Re-read on 2026-09-12, after this specification was first drafted.** Two of the three defects
> originally recorded here had already been repaired in `Workflow\Prompt\` by the time of the
> re-read, and a fourth, more serious one was found. The list below is what the bytes on disk
> actually say. Sizes: `initial_prompt.md` 1819 B, `review_prompt.md` 312 B,
> `resolve_review_prompt.md` 1425 B, `implementation_prompt.md` 3383 B.

The `Prompt\*.md` files are project resources under our control, so they are corrected rather
than worked around. Only the *remaining delta* is applied — a wholesale overwrite would discard
working content that is newer than this document.

**(a) `resolve_review_prompt.md` — RESOLVED ON DISK, one addition still required.**
The file is no longer 0 bytes. It contains a usable phase-3 prompt that opens with the three
artefact paths and requires the resolver to verify each finding against the repository and
classify it valid / partially valid / invalid. **That content is kept as is** — it is better than
the version this specification originally proposed, because repository verification and
partially-valid handling are exactly what a review-resolution phase needs.

One thing is missing and must be **appended**, not substituted:

```markdown
Both {spec_path} and {plan_path} must be written to, even if only to append a short
`## Review resolution` note recording that no change was required.
```

> This paragraph is load-bearing, not politeness: phase 3 completes on a **content change** to
> spec or plan (§7.5). The file as it stands ends by asking for a *report* of what was rejected —
> a run in which every finding is rejected therefore writes nothing, and the phase never
> advances. The manual *Phase abschliessen* button (§10.6) is the backstop, but the guarantee
> belongs in the prompt.

**(b) `implementation_prompt.md` line 8 reads `{plan_path}_` — STILL PRESENT.**
A stray trailing underscore after the token; rendering leaves a literal `_` in the prompt. Remove
the underscore, nothing else on that line.

**(c) `review_prompt.md` output path — RESOLVED ON DISK.**
The first line no longer builds the path from `{taskbeschreibung}`. It now reads:

```
/review produce a {review_path} as delta critique for following plans:
```

The path already comes from the single source of truth (`TaskPaths`), which is what this
specification required. The only remaining edit is cosmetic — drop the stray article so the line
reads `/review produce {review_path} as delta critique for following plans:`. **Do not rewrite
the rest of the file**; its closing two lines ("you shouldn't create another handoff document…",
"Please do not output the whole review…") are deliberate and are kept verbatim.

**(d) `implementation_prompt.md` still orders the implementer into QDocImport — NEW, must be removed.**
Under `Completion requires:` the file lists:

```
* appropriate eval gates has been added to qdocimporter/eval
```

This directly contradicts §14 and §3.2, which declare QDocImport eval gates **out of scope** for
this application. Left in place, phase 4 instructs the implementation agent to modify a separate,
regulated .NET Framework product that shares nothing with Workflow — and the agent cannot report
completion without doing so, or without silently ignoring a stated completion requirement.

The bullet is **deleted** and replaced by the gate this application actually has:

```
* the acceptance gate `Workflow\verify.ps1` exits 0
```

This is the same intent — "a machine-checkable gate that runs after everything and verifies the
outcome" — pointed at the right repository (§14, item 12).

**Scope of the Task 4 edits.** Exactly four edits are made to the prompt files: append one
paragraph to `resolve_review_prompt.md`; remove one `_` from `implementation_prompt.md`; replace
one bullet in `implementation_prompt.md`; remove one word from `review_prompt.md`. Nothing else
in `Prompt\*.md` is rewritten. The shipped files are then asserted by tests (§15.1 A8), so a
future regression in either direction is caught by the build rather than by a stalled pipeline.

### 9.4 Startup validation gate

At application start, `PromptTemplateService.ValidateAll()` verifies that all four files exist,
are non-empty, and contain only tokens from §9.2. Failure shows a blocking German error dialog
naming the file and the offending token, and disables *Start workflow* on every tab. This turns
§4.5's silent runtime `FileNotFoundException` into an actionable message.

**The gate is enforced per tab, not only by the dialog.** The dialog can be dismissed, and new
tabs are created after startup, so the flag has to reach the command itself:

- The composition root passes the `ValidateAll()` result into `TaskTabViewModelFactory`, and from
  there into every `TaskTabViewModel` — including tabs opened later with `+`.
- `TaskTabViewModel.CanStartWorkflow()` returns `false` while that list is non-empty. Holding
  `StartupErrors` only on `MainWindowViewModel` leaves every tab's button executable once the
  modal is closed, which fails F17.
- The tab additionally shows the first error inline, so a user who dismissed the dialog can still
  see why *Start workflow* is dead.

---

## 10. User interface

### 10.1 Theme

`App.xaml` merges dictionaries in this exact order — the order is load-bearing:

```xml
<ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml" />
<ResourceDictionary Source="pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml" />
<ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml" />
<materialDesign:MahAppsBundledTheme BaseTheme="Dark" PrimaryColor="Indigo" SecondaryColor="Cyan" />
<ResourceDictionary Source="pack://application:,,,/MaterialDesignThemes.MahApps;component/Themes/MaterialDesignTheme.MahApps.Defaults.xaml" />
```

**Corrected 2026-09-13 (was a startup-crashing defect).** The order above was originally copied
from `C:\Users\Marco\Documents\repo\wpf\MaterialDesignInXaml.Examples\MahApps\MahApps.Basic\App.xaml`,
which pins `MaterialDesignThemes.MahApps` **0.1.5** on **netcoreapp3.1**. That sample's third
line reads `Themes/MaterialDesignTheme.Defaults.xaml`, which **does not exist** in the version
this project pins (`MaterialDesignThemes` **5.3.2**): v5 split it into `MaterialDesign2.Defaults.xaml`
and `MaterialDesign3.Defaults.xaml`, and requires one of them. Verified by enumerating the real
`MaterialDesignThemes.Wpf.g.resources` manifest (89 entries: `themes/materialdesign2.defaults.baml`
and `themes/materialdesign3.defaults.baml` are present, `themes/materialdesigntheme.defaults.baml`
is not). `MaterialDesign2` is the correct choice here: the app's style keys
(`MaterialDesignRaisedButton`, `MaterialDesignOutlinedTextBox`, `MaterialDesignFlatButton`, …)
are Material Design 2 styles, and the `MaterialDesignThemes.MahApps` compatibility dictionary
targets the same. The other three pack URIs above were each verified against their assembly's
manifest and are correct. `Workflow.Tests\AppResourceTests` now pins all of this.

Dark base theme: the app is dominated by a terminal, and a dark surface avoids a jarring light
frame around a dark console. The xterm theme in `terminal.js` is set to match
(`background: '#1E1E1E'`, `foreground: '#E0E0E0'`, `cursor: '#7986CB'` — Indigo 300).

### 10.2 `MainWindow` (`mah:MetroWindow`)

```
┌──────────────────────────────────────────────────────────────────────┐
│ Workflow                                                  [ARM 56px] │  ← PackIcon ArmFlex,
├──────────────────────────────────────────────────────────────────────┤    top-right overlay
│ ┌────────────┬────────────┬─────┐                                    │
│ │ MyTask   × │ (Bezeichn… │  +  │   ← TabPanel + AddTab button       │
│ ├────────────┴────────────┴─────┴──────────────────────────────────┐ │
│ │                                                                  │ │
│ │                        TaskTabView                               │ │
│ │                                                                  │ │
│ └──────────────────────────────────────────────────────────────────┘ │
│                                                        [ Schließen ] │
└──────────────────────────────────────────────────────────────────────┘
```

- Minimum size 1100 × 760. Below that the 30 %/70 % split stops being usable.
- The ArmFlex icon is a `materialDesign:PackIcon Kind="ArmFlex"` at 56 × 56, placed in a `Grid`
  overlay with `HorizontalAlignment="Right" VerticalAlignment="Top"` inside the content area
  (not in `RightWindowCommands`, which is size-constrained by the title bar).
  `PackIconKind.ArmFlex` and `ArmFlexOutline` are both confirmed present in
  `MaterialDesignThemes.Wpf` 5.3.2.
- *Schließen* is a `MaterialDesignRaisedButton` docked bottom-right, bound to
  `MainWindowViewModel.CloseApplicationCommand` (`Application.Current.Shutdown()` after
  cancelling every tab).

### 10.3 The `+` button and the TabControl

MahApps 2.4.11 exposes `MetroAnimatedSingleRowTabControl`, but its default template has no
insertion point for an extra header-row button, and the `+` must sit *beside* the tab headers.

Design: a plain `TabControl` with a custom `ControlTemplate` whose header row is a `DockPanel`
containing `<TabPanel IsItemsHost="True"/>` docked left and the `+` `Button` docked left after
it. MahApps look is preserved by styling the items with `MahApps.Styles.TabItem` (verified
present) plus `mah:TabControlHelper.CloseButtonEnabled` and `mah:TabControlHelper.CloseTabCommand`
(both verified present). The content host is wrapped in `mah:TransitioningContentControl`
(verified present) so the animated content transition of the MahApps tab control is retained.

Trade-off, accepted: a hand-written `ControlTemplate` for the TabControl shell. Rejected
alternatives — a sentinel "+" `TabItem` (breaks `ItemsSource` binding and needs selection
interception) and an absolutely-positioned overlay button (cannot track the last tab's right
edge).

The `+` button: `Style="{StaticResource MaterialDesignFloatingActionMiniButton}"`, 28 × 28,
content `<materialDesign:PackIcon Kind="Plus"/>`, `ToolTip="Neuen Task öffnen"`, bound to
`MainWindowViewModel.AddTaskTabCommand`.

**The replacement template must forward the selected content's template, not just its content.**
The stock `TabControl` template uses `<ContentPresenter ContentSource="SelectedContent"/>`, which
is shorthand for binding `Content`, `ContentTemplate`, `ContentTemplateSelector` *and*
`ContentStringFormat` to the matching `SelectedContent*` properties. `TaskTabView` is supplied
through `TabControl.ContentTemplate`, which the control surfaces as `SelectedContentTemplate`.

`mah:TransitioningContentControl` is a `ContentControl`, so `ContentSource` is not available on
it and the bindings must be written out:

```xml
<mah:TransitioningContentControl x:Name="PART_SelectedContentHost"
                                 Transition="Left"
                                 Content="{TemplateBinding SelectedContent}"
                                 ContentTemplate="{TemplateBinding SelectedContentTemplate}"
                                 ContentTemplateSelector="{TemplateBinding SelectedContentTemplateSelector}"
                                 ContentStringFormat="{TemplateBinding SelectedContentStringFormat}"
                                 Margin="8" />
```

Binding `Content` alone leaves the host with no template for a `TaskTabViewModel`, so WPF falls
back to `ToString()` and the window shows the type name where the task UI should be. F18 asserts
that a `TaskTabView` is present in the visual tree after startup.

Tab header template: `TextBlock` bound to `TaskTabViewModel.Header`, which is
`string.IsNullOrWhiteSpace(TaskName) ? "(Bezeichnung)" : TaskName` — implemented as a
`[NotifyPropertyChangedFor(nameof(Header))]` on `TaskName`.

### 10.4 `TaskTabView` — the 30 / 70 split

`Grid` with `RowDefinitions = "0.3*, 0.7*"`, exactly as required.

**Row 0 (30 %)** is a `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`) wrapping a
`StackPanel`. The scroller is necessary, not decorative: the required contents are ≈ 470 px tall
while 30 % of the minimum window height is ≈ 228 px.

| Order | Control | Details |
|---|---|---|
| 1 | `TextBox` | Header/hint `Task`. `Style="{StaticResource MaterialDesignOutlinedTextBox}"`, `materialDesign:HintAssist.Hint="Task"`. Two-way bound to `TaskName`, `UpdateSourceTrigger=PropertyChanged`. `IsReadOnly="{Binding IsNameLocked}"`. |
| 2 | `RichTextBox` | `Height="200"`, `VerticalScrollBarVisibility="Auto"`, `AcceptsReturn="True"`. Bound to `TaskDescription` via `Behaviors\RichTextBoxAssist.PlainText` (§10.5). |
| 3 | `ComboBox` + `Button` | ComboBox `ItemsSource="{Binding RecentDirectories}"`, `SelectedItem="{Binding WorkingDirectory}"`, hint `Arbeitsverzeichnis`. Button `PackIcon Kind="FolderOpen"`, `MaterialDesignIconButton`, opens the picker. |
| 4 | `Button` | `Start workflow`, `MaterialDesignRaisedButton`, `PackIcon Kind="Play"`. `CanExecute` = name valid **and** directory selected **and** phase is Idle. |
| 5 | `ItemsControl` | Four `PhaseIndicatorView`s, horizontal. |
| 6 | `Button` | `Task abschliessen`, enabled only while phase 4 is Active. |

**Row 1 (70 %)** is `TerminalView`: a `Grid` with `RowDefinitions="*,Auto"` — `WebView2` fills the
star row; a `DockPanel` beneath holds a single-line `TextBox` (hint
`Eingabe an das Terminal … (Enter zum Senden)`) and a send `Button`. Enter in the TextBox, or the
button, writes the text plus `CR` to the PTY and clears the box. This satisfies the explicit
requirement for an *Eingabefeld*; typing directly into the terminal surface also works.

### 10.5 `RichTextBoxAssist.PlainText`

`RichTextBox` has no bindable `Text` dependency property. A small attached property provides
two-way binding between the `FlowDocument` and a plain `string`, with a re-entrancy guard.

Two mechanics are load-bearing and are easy to get wrong:

1. **`TextChanged` is wired independently of any value change.** A dependency-property changed
   callback only runs when the *effective value changes*. The common case — a fresh tab whose
   `TaskDescription` is `""` — binds the property to the same value as its default, so the
   callback never fires. If the handler is attached only from inside that callback, the
   document→source direction is never wired and everything the user types is silently dropped,
   leaving phase 1 to render `initial_prompt.md` with an empty `{taskbeschreibung}`. The handler
   is therefore attached from the property's `OnAttached`/coerce path, or from the control's
   `Loaded`, so it exists regardless of the initial value.
2. **The source is updated through the binding, not over it.** The reverse direction writes with
   `SetCurrentValue` (or `BindingOperations.GetBindingExpression(...)?.UpdateSource()`), never
   `SetValue`. `SetValue` sets a *local* value, which outranks a binding and silently detaches a
   one-way binding; `SetCurrentValue` changes the effective value while leaving the binding in
   place, which is exactly the intent here.

The round-trip is covered by a test that binds a real view-model property two-way, starts from
`""`, simulates typing, and asserts the view model received the text (§13).

The requirement asks for a *rich textfield*. The prompt consumes plain text only, so formatting
is discarded at the boundary — but `RichTextBox` is still the right control: it tolerates a
paste from Word/Notion/a browser (the overwhelmingly common way a task description arrives)
without dumping markup into the prompt, which a plain `TextBox` would not. Cost is roughly
40 lines and it is directly unit-testable.

### 10.6 Phase indicators

Four pills, left to right: `Spezifikation`, `Review`, `Review umsetzen`, `Implementierung`.

| `PhaseStatus` | Colour               | `PackIconKind`  |
|---------------|----------------------|-----------------|
| `Pending`     | `#9E9E9E` grey       | `CircleOutline` |
| `Active`      | `#FBC02D` yellow     | `ProgressClock` |
| `Completed`   | `#43A047` green      | `CheckCircle`   |

Implemented with `PhaseStatusToBrushConverter` and `PhaseStatusToIconKindConverter`
(`IValueConverter`). Both converters are nullable-annotated (`object? value`, `object? parameter`,
returning `object?`) — WPF passes `null`/`DependencyProperty.UnsetValue` and `Nullable=enable`
makes the signature mismatch a build error otherwise.

While a phase is `Active`, a small `Phase abschliessen` text button appears on that indicator,
bound to `TaskTabViewModel.CompleteCurrentPhaseCommand`. This is the manual escape hatch for the
stall described in §12.3 and costs one command.

---

## 11. Persistence

`%APPDATA%\Workflow\settings.json`:

```json
{
  "recentDirectories": ["C:\\src\\foo", "C:\\src\\bar"],
  "lastDirectory": "C:\\src\\foo"
}
```

- Most-recent-first, de-duplicated case-insensitively, capped at 15 entries.
- Written atomically: serialise to `settings.json.tmp`, then `File.Move(tmp, target, overwrite: true)`.
- A corrupt or unreadable file is replaced with defaults; it is never allowed to crash startup.
- `System.Text.Json` from the .NET 8 shared framework is used. The transitive
  `System.Text.Json 4.7.2` from `MahApps.Metro` must **not** be referenced directly.

New tabs pre-select `lastDirectory` when it still exists on disk.

---

## 12. Failure modes and edge cases

### 12.1 Environment

| Failure | Detection | Behaviour |
|---|---|---|
| WebView2 runtime missing | `EnsureCoreWebView2Async` throws | German dialog with the download URL; the tab shows a placeholder instead of a terminal. Other tabs keep working. |
| ConPTY unavailable (Windows < 10 1809) | `CreatePseudoConsole` returns `E_NOTIMPL` | Startup check at first session; actionable message naming the minimum Windows version. |
| Neither `pwsh.exe` nor `powershell.exe` found | `CreateProcess` fails `ERROR_FILE_NOT_FOUND` | Actionable message; *Start workflow* stays disabled. |
| `yo` / `codex` not on `PATH` | The shell prints "not recognized"; no artefacts appear | Phase never completes. The user sees it in the live terminal and can cancel. Documented, not auto-detected — the terminal is the correct diagnostic surface. |

**The workflow run is started fire-and-forget, so its failures must be caught at the boundary.**
`StartWorkflowCommand` discards the `Task` returned by `RunAsync`; anything it throws that is not
caught becomes an unobserved task exception, while the UI merely flips `IsRunning` back to
`false` and says nothing. That contradicts every "actionable message" row above. The wrapper
therefore catches, and surfaces as an inline German message, at least:

| Exception | Thrown by |
|---|---|
| `OperationCanceledException` | Cancellation — **re-thrown/ignored, never reported as an error.** |
| `PromptTemplateException` | `PromptTemplateService.Render` |
| `FileNotFoundException` | `ShellLocator.FindShellExecutable` (neither shell present) |
| `PlatformNotSupportedException` | `CreatePseudoConsole` returning `E_NOTIMPL` |
| `Win32Exception` | `CreatePipe` / `CreateProcess` failing |
| `IOException`, `UnauthorizedAccessException` | task-folder creation, artefact hashing, watcher setup |
| `InvalidOperationException` | WebView2 initialisation, double `Start` on a session |

A catch-all is **not** added here: an unexpected exception type should surface as a crash during
development rather than be swallowed into a label. The list above is exhaustive for the code
paths this design specifies, and `verify.ps1`'s V9 exercises a throwing terminal.

### 12.2 Filesystem

| Failure | Behaviour |
|---|---|
| Invalid task name | Inline validation; *Start workflow* disabled; no folder created (§8.2). |
| Task folder already exists | Reused with an informational message (§8.3). |
| `Directory.Move` denied | Cannot occur during a run — the name is locked (§8.4). While idle, the exception is caught, a Snackbar shown, and `TaskName` rolled back to the on-disk value under a re-entrancy guard (§8.3). |
| Path > 240 chars | Rejected by validation (§8.2). |
| Working directory deleted mid-run | `FileSystemWatcher.Error` fires **and** the 1 s poll observes `Directory.Exists == false`. Either one fails the watcher (§7.5); `RunPhaseAsync` propagates the failure, the indicator leaves *Active*, and a German message names the missing directory. The phase must not sit in a poll loop waiting for a condition that can no longer hold. |

### 12.3 Orchestration stalls

This is where the design is most exposed, so each stall has a named exit.

| Stall | Cause | Exit |
|---|---|---|
| Auto-answer never matches | Upstream CLI changed its wording | The launcher renders (Gate A), the screen goes quiet, no rule matches, and the prompt is sent on that first quiet snapshot — **not** after 60 s. `settleTimeoutMs` is the ceiling for the case where the launcher never renders at all. Either way the prompt is sent and the terminal stays typeable, so the user can answer by hand. Rules are editable in `%APPDATA%` without a rebuild (§7.4). |
| Auto-answer answers *No* | A rule sends `\r` where the affirmative is not preselected | The default rule set sends `2` explicitly for the `yo` bypass warning. Documented in each rule's `description`. |
| Phase 3 never completes | Model resolved everything by rejection and wrote nothing | `resolve_review_prompt.md` mandates a `## Review resolution` note (§9.3a); plus *Phase abschliessen* (§10.6). |
| Phase 1/2 completes instantly | Artefacts left over from a previous run in a reused folder | Visible in the indicators; the user can delete the folder. Accepted — auto-deleting a user's directory is worse. |
| Prompt submitted early | A raw newline inside the prompt body | Bracketed paste (§6.5) with newlines normalised to CR. |
| Prompt sent into an unrendered screen | `LastOutputUtc` seeded to `DateTimeOffset.MinValue`, so the first iteration thinks the terminal is quiet | Gate A in §7.3: `StartSession` resets `LastOutputUtc`, and the loop requires output *and* a non-empty snapshot after the launcher command before evaluating quietness. |
| Submit `CR` lands in the next phase | `SendPaste` fires a detached 150 ms timer while the orchestrator advances on a pre-existing artefact (§8.3) | The paste, including its submit, is awaited and scoped to the session that received it (§6.5). |

### 12.4 Lifetime and resources

- One PTY, one child process tree, and one WebView2 per tab. Closing a tab must run the §6.3
  shutdown sequence; otherwise `pwsh` and `node` (Claude Code) survive as orphans.
- **Disposal is bound to tab removal and application exit — never to `Unloaded`.** A `TabControl`
  hosts the selected item in a single content host, so selecting a different tab unloads the
  previous tab's visual tree. Disposing `TerminalViewModel` from `TerminalView.OnUnloaded` would
  therefore kill a *running* task's PTY and WebView2 the moment the user looks at another tab,
  and re-selecting it would call `AttachAsync` on a disposed view model. That breaks the core
  promise of F2/F16 — tabs are independent and keep running — for the most ordinary interaction
  in the app. The owners are `MainWindowViewModel.CloseTab` (which disposes the tab it removed)
  and `ShutdownAll`. `TerminalView.OnLoaded` must be idempotent so re-selecting a tab re-attaches
  the *same* live session rather than starting a second one.
- `Application.Current.Shutdown()` from *Schließen* cancels every tab's token **and waits** for
  disposal before exiting, with a 5 s cap.
- `Window.Closing` performs the same work, so the X button and *Schließen* behave identically.
- The 16 ms output-coalescing timer must be stopped on dispose or it keeps the WebView2 alive.

### 12.5 Likely regressions to guard

1. Adding a `PackageReference` without pinning a version → a future restore silently upgrades
   MaterialDesign and breaks resource keys. Pin exact versions.
2. Touching the `App.xaml` merge order → MahApps controls render unstyled (§10.1).
3. Putting `[ObservableProperty]` on a property instead of a field → `MVVMTK0040`, build error.
4. Decoding PTY bytes to `string` in C# → mojibake on multi-byte boundaries (§6.3).
5. Closing `appRead`/`appWrite` before `ClosePseudoConsole` → hang on exit (§6.3).
6. Forgetting `<Content>` items for `Prompt\*.md` or `Assets\**` → runtime `FileNotFoundException`
   and a blank terminal (§4.5).

---

## 13. Testing strategy

`Workflow.Tests` — xUnit, `net8.0-windows`, `UseWPF=true` (needed for the `RichTextBoxAssist`
and converter tests), added to `Workflow.sln`. xUnit is chosen for consistency with the
in-house WPF UI-test skill `mp-test/skills/writing-mp-flaui-tests`, which is `IClassFixture`-based.

Everything below the ViewModel layer is deterministic and tested. `ConPtySession` and
`TerminalView` are covered by the manual verification script in §15.3 instead — a real
pseudo-console and a real WebView2 are not unit-testable in a meaningful way, and mocking them
would test the mock.

| Suite | Covers |
|---|---|
| `PromptTemplateServiceTests` | Substitution of every token; unresolved token throws and names the token; empty file throws; BOM tolerated; CRLF/LF handled; `ValidateAll` accepts the shipped four files. |
| `TaskPathsTests` | All six path members for names with spaces and umlauts; relative forms use `./` and forward slashes; `{AppDirectory}` = working directory. |
| `TaskFolderServiceTests` | Validation matrix (empty, invalid chars, trailing dot/space, `CON`, `COM1`, `lpt3.txt`, > 240 chars); create; rename; case-only rename; reuse of an existing folder. |
| `ArtifactWatcherTests` | `FilesExist` fires only when all exist and are non-empty; zero-byte file does not fire; `AnyContentChanged` ignores a touch that does not change content; fires on a real edit; fires for a file absent at baseline; debounce collapses a burst. |
| `AutoAnswerServiceTests` | Loads the shipped JSON; first-match-wins ordering; a rule fires at most once per phase; escape decoding (`\r`, `\e`, `\u0041`); `maxAnswersPerPhase` respected; a malformed regex is reported, not thrown at match time. |
| `WorkflowOrchestratorTests` | With a fake `ITerminalSession` and fake watcher: the exact write sequence per phase (`cd "…"` → launcher → answers → bracketed-paste prompt); phases chain 1→2→3→4; phase 4 waits for the manual signal; cancellation disposes the session. |
| `SettingsServiceTests` | Round-trip; MRU ordering, de-dup (case-insensitive), 15-item cap; corrupt JSON falls back to defaults; atomic write leaves no `.tmp`. |
| `ConverterTests` | Both phase converters for all three statuses, plus `null` and `DependencyProperty.UnsetValue`. |
| `RichTextBoxAssistTests` | Plain-text round-trip; multi-line preserved; no infinite re-entrancy. |
| `TaskTabViewModelTests` | `Header` falls back to `(Bezeichnung)`; `StartWorkflowCommand.CanExecute` matrix; `IsNameLocked` after start; `CompleteTaskCommand` only in phase 4; **`CanExecute` is `false` while startup errors are present**; **a forced `Directory.Move` failure rolls `TaskName` back to the on-disk name without re-entering the folder sync**; **a throwing orchestrator surfaces each failure family from §12.1 as a message instead of an unobserved task exception**. |
| `MainWindowViewModelTests` | Startup errors reach every tab, including one opened with `+` afterwards; `CloseTab` disposes exactly the removed tab; `ShutdownAll` disposes all. |

Additions to the suites above, one per defect this specification now pins down:

| Suite | Added case |
|---|---|
| `TaskPathsTests`, `TaskFolderServiceTests`, `SettingsServiceTests` | A drive root (`C:\`) survives normalisation: the task directory is `C:\N`, never `C:N` (§8.1). |
| `ArtifactWatcherTests` | A file held under an exclusive lock does **not** report a content change; the watcher recovers and fires once the lock is released. Deleting the watched directory fails the watcher instead of polling forever. |
| `PromptTemplateServiceTests` | The four **shipped** templates are asserted, not just synthetic ones: `resolve_review_prompt.md` contains the `## Review resolution` guarantee; `implementation_prompt.md` contains no `{plan_path}_` and no `qdocimporter` reference; `review_prompt.md` builds its output path from `{review_path}`. |
| `WorkflowOrchestratorTests` | The prompt is not sent until the fake terminal has reported launcher output *and* a non-empty snapshot; a snapshot that is initially empty and only later shows the bypass warning still gets answered with `2`; the submit `CR` is cancelled when the session is replaced before the delay elapses; a non-matching rule set sends the prompt on the first quiet snapshot (well under `settleTimeoutMs`) rather than waiting out the ceiling. |
| `RichTextBoxAssistTests` | Binding an **empty** string first (the default value, so no property-changed callback fires) still wires `TextChanged`, and a subsequent edit reaches the source through a real two-way `Binding` to a view model. |

---

## 14. Prompt instructions 8 and 12 — Not Applicable

The task description was produced by filling `{taskbeschreibung}` into
`Workflow\Prompt\initial_prompt.md`. Items 8 and 12 of the numbered list are **verbatim
boilerplate from that template**, not requirements of this application. They are addressed
explicitly rather than silently dropped.

**Item 8 — billing-file validity for doctorly, `C:\quincy\PRFMODUL\XKV20263\Doku\MeldungenKVDT.xml`.**
The file exists (72.3 KB, ISO-8859-1). It is a KVDT *Prüflauf* message catalogue: `<meldung>`
entries such as `KVDT-FDATE`, `KVDT-FEHL`, `KVDT-R-FK0132`, each with `typ`, `text` and
`maxcount`, used to render validation messages for German medical billing submissions.
**Not applicable.** Workflow handles no KVDT data, no billing, no patient data and no
doctorly/Quincy code path. No requirement, test or acceptance criterion derives from it.

**Item 12 — eval gates for QDocImporter in `..\QDocImport\Eval`.**
`..\QDocImport\Eval` relative to this repository resolves to
`C:\Users\Marco\Documents\repo\QDocImport\Eval`, which does not exist. The real directory is
`C:\vb5\QDocImport\QDocImport\Eval` — part of a .NET Framework 4.8 / NUnit medical-data importer
whose eval engine (`EvalRunner`, `EvalScorer`, `CheckCatalog`, `InternalKbvValidatorClient`,
`checks.json`) validates KBV/KVDT imports. **Not applicable.** Adding Workflow gates to that
project would put unrelated assertions into a separate, regulated product and couple two
codebases that share nothing.

The *intent* behind item 12 — "a machine-checkable gate that runs after everything and verifies
the outcome" — is honoured locally by the acceptance gates in §15, executed by
`Workflow.Tests` and `verify.ps1`.

**Declaring it N/A is not enough — the instruction is still live in a shipped file.**
`Workflow\Prompt\implementation_prompt.md` lists
`* appropriate eval gates has been added to qdocimporter/eval` under `Completion requires:`.
That file is rendered and pasted into the phase-4 agent, so as long as the bullet is there, this
section's decision is contradicted by the product itself: the agent is told it may not report
completion until it has modified QDocImport. §9.3(d) therefore replaces the bullet with
`* the acceptance gate `Workflow\verify.ps1` exits 0`, and a test over the shipped template
asserts that no `qdocimporter` reference survives (§15.1 A8). This is the only way the N/A
decision is enforceable rather than advisory.

---

## 15. Acceptance criteria

### 15.1 Build and static analysis

| # | Criterion |
|---|---|
| A1 | `dotnet build Workflow.sln -c Release` succeeds with **0 warnings and 0 errors**, with `Directory.Build.props` importing `.roslyn` (`TreatWarningsAsErrors=true`, `AnalysisMode=All`). |
| A2 | Every `NoWarn` entry is one of those listed in §4.4 and carries an inline XML comment giving its justification. No other blanket suppression exists. |
| A3 | `dotnet build` produces no `MVVMTK*` diagnostics. |
| A4 | No `unsafe` keyword anywhere; `AllowUnsafeBlocks` stays `false`. |
| A5 | `dotnet test Workflow.sln` — all tests green. |
| A6 | `bin\Release\net8.0-windows\` contains `Prompt\` with all four `.md` files, `Assets\autoanswer.rules.json`, and `Assets\Terminal\` with all five files. |
| A7 | **Every task in the implementation plan ends at 0 warnings / 0 errors.** A task that leaves analyzer errors for a later task is not complete. `CA1031` appears **only** as a `#pragma warning disable CA1031` at the PTY read loop and the top-level dispatcher handler — `grep -r "CA1031" Directory.Build.props` returns nothing. |
| A8 | The shipped prompt templates are asserted by `PromptTemplateServiceTests`, not merely edited once: `implementation_prompt.md` contains no `{plan_path}_` and no case-insensitive match for `qdocimport`; `review_prompt.md` renders its output path from `{review_path}`; `resolve_review_prompt.md` is non-empty and contains `## Review resolution`. |
| A9 | The ConPTY spike (§6.3.1) has been run and its result recorded in the plan before any `ConPtySession` code is committed. `ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput` passes against a real pseudo-console, and the diagnostic test reports the child's exit code and first-byte latency on failure. |

### 15.2 Functional

| # | Criterion |
|---|---|
| F1 | On startup exactly one tab is open, header `(Bezeichnung)`, with a `+` button beside it. |
| F2 | `+` opens an additional independent tab; each tab has its own terminal and phase state. |
| F3 | The ArmFlex icon is visible at 56 px in the top-right of the main window, and is the window/taskbar icon. |
| F4 | Typing a task name updates the tab header live and creates `{WorkingDirectory}\{TaskName}` on disk. |
| F5 | Renaming while idle renames the folder; after *Start workflow* the name field is read-only. |
| F6 | The directory picker adds the chosen path to the ComboBox, and it is still there after an app restart. |
| F7 | *Start workflow* opens a live PTY, `cd`s to the selected path, launches `yo`, answers the bypass-permissions prompt with **`2`**, and pastes the rendered `initial_prompt.md`. |
| F8 | The user can type into the terminal input field at any time during any phase and the keystrokes reach the CLI. |
| F9 | When `{spec_path}` and `{plan_path}` both exist and are non-empty, indicator 1 turns green, indicator 2 turns yellow, and a new session runs `codex --yolo`. |
| F10 | When `{review_path}` appears, indicator 2 turns green and phase 3 starts with `yo`. |
| F11 | When spec or plan content changes during phase 3, indicator 3 turns green and phase 4 starts with `yo`. |
| F12 | Phase 4 stays active until *Task abschliessen*. |
| F13 | After *Task abschliessen*, `{TaskDirectory}` contains `{N}_spec.md`, `{N}_plan.md`, `{N}-review.md`, `findings.md`, `task_plan.md`, `progress.md`. |
| F14 | Indicators are grey before, yellow during, green after — for all four phases. |
| F15 | *Schließen* exits the application; no `pwsh`, `claude`, `node` or `codex` process survives. |
| F16 | Closing a single tab kills only that tab's process tree. |
| F17 | With a deliberately emptied prompt file, startup shows the §9.4 error naming that file, and *Start workflow* is disabled **on every tab, including one opened with `+` after the dialog was dismissed**. |
| F18 | The window shows a rendered `TaskTabView` after startup — not the string `Workflow.ViewModels.TaskTabViewModel`. The custom `TabControl` template forwards `SelectedContentTemplate` (§10.3). |
| F19 | With two tabs, both mid-run: selecting tab B and then tab A again leaves **both** PTYs alive and both terminals rendering. No process is killed and no `AttachAsync` runs against a disposed view model. Only closing a tab (or *Schließen*) kills a process tree. |
| F20 | Phase 1 does not paste its prompt until the launcher has actually rendered. With a launcher that takes 5 s to draw its first frame, the bypass-permissions prompt is still answered with `2` and the prompt still arrives afterwards. |
| F21 | Deleting `{TaskDirectory}` mid-phase surfaces a German error naming the directory and leaves the phase; the indicator does not stay yellow indefinitely. |

### 15.3 Verification steps (manual, scripted where possible)

`Workflow\verify.ps1` automates V1–V4:

1. **V1** `dotnet build Workflow.sln -c Release /warnaserror` → exit 0; assert `warning` count 0.
2. **V2** `dotnet test Workflow.sln -c Release` → exit 0.
3. **V3** Assert the output-directory manifest from A6.
4. **V4** Assert `Prompt\resolve_review_prompt.md` length > 0 and that
   `Select-String '\{[a-zA-Z_]+\}' Prompt\*.md` yields only tokens from §9.2.
5. **V5** (manual) Launch; create task `demo-task`; pick a scratch directory; run the full
   pipeline; confirm F7–F14 against the live terminal.
6. **V6** (manual) Kill `claude.exe` externally mid-phase-1; confirm the app stays responsive and
   the phase does not falsely complete.
7. **V7** (manual) Edit `%APPDATA%\Workflow\autoanswer.rules.json` to a non-matching pattern;
   confirm that **no automatic answer is sent**, that the prompt is pasted once the screen goes
   quiet — promptly, on the order of the quiet period, **not** after 60 s (§7.3) — and that the
   terminal stays fully usable so the confirmation can be answered by hand. The 60 s ceiling is
   exercised separately by `WorkflowOrchestratorTests` with a controllable clock; it is not a
   manual step, because sitting through it proves nothing a unit test cannot.
8. **V8** (manual) Task Manager: after *Schließen*, no orphan `pwsh`/`node`/`claude`/`codex`.
9. **V9** (scripted) A fake terminal controller that throws each failure family from §12.1 in
   turn produces an inline German message and resets `IsRunning`, with no unobserved task
   exception (`TaskScheduler.UnobservedTaskException` is asserted not to fire).
10. **V10** (manual) Open two tabs, start a workflow in each, switch back and forth three times.
    Confirm F19: both terminals keep streaming and no process dies.
11. **V11** (manual) Select a drive root (for example `C:\`) in the directory picker with the
    task name `wf-root-test`. Confirm the folder is created at `C:\wf-root-test`, that the
    terminal shows `cd "C:\"`, and that the entry survives a restart. Delete the folder
    afterwards.

---

## 16. Decision log

| # | Decision | Rejected alternative | Why |
|---|---|---|---|
| D1 | ConPTY + xterm.js in WebView2 | In-process C# VT emulator | xterm.js is the VT implementation VS Code ships; writing 1000–1500 lines of emulator for an Ink TUI is a fidelity gamble. WebView2 runtime is already installed. |
| D2 | Task folder under the selected working directory | Next to `Workflow.exe`; `%APPDATA%` | Matches `cd {pfad}` plus relative `./{task}/` in the prompts; `{AppDirectory}` is defined as the working directory to remove the contradiction. |
| D3 | Author `resolve_review_prompt.md` in this spec | Let the user supply it | Phase 3 is otherwise undefined. The file also carries the guarantee that makes phase-3 completion detectable. |
| D4 | Lock the task name after start | Retry-and-roll-back on rename failure | Removes an entire failure class instead of handling it; a running CLI holds the directory. |
| D5 | Manual composition root | `Microsoft.Extensions.DependencyInjection` | Nine services, one window. Constructor injection alone gives full testability; a container adds a package and indirection for no gain. |
| D6 | Data-driven auto-answer rules | Hard-coded regexes | Upstream CLI wording changes without notice; a JSON file in `%APPDATA%` fixes a broken pipeline without a rebuild. This is the app's most fragile coupling. |
| D7 | Bracketed paste for prompt injection | Line-by-line writes | A bare newline submits the partial prompt in an Ink input box. |
| D8 | `FileSystemWatcher` **plus** 1 s polling | Watcher alone | A dropped event on a OneDrive/network/virtualised path stalls the pipeline permanently; the poll is cheap insurance on the app's critical path. |
| D9 | `RichTextBox` + plain-text attached property | Multi-line `TextBox` | The requirement says *rich textfield*, and it correctly flattens pasted formatted text — the common input path. ~40 testable lines. |
| D10 | Custom `ControlTemplate` for the TabControl | Sentinel `+` TabItem; overlay button | Only way to put `+` beside the `TabPanel` while keeping `ItemsSource` binding clean. MahApps item styling and `TransitioningContentControl` are retained. |
| D11 | No persistence of per-task phase state | Resume-on-restart | Not required (§3.2); would add a state-vs-disk consistency problem with no asked-for benefit. |
| D12 | Instructions 8 and 12 documented as N/A **and the live instruction removed from `implementation_prompt.md`** | Silently dropping them; writing gates into QDocImport; leaving the bullet in place while declaring it N/A | They are template boilerplate; acting on them would couple this app to an unrelated regulated product (§14). A declaration that the shipped prompt still contradicts is not a decision, it is a comment. |
| D13 | The settle loop exits on the first quiet snapshot with no matching rule | Waiting out `settleTimeoutMs` whenever no rule matches | The no-match case is the *normal* end of the loop, reached in every phase once the launcher is at its input prompt. Waiting 60 s there would add four minutes to every task while fixing nothing — the prompt would still be sent into the same screen (§7.3, §12.3). |
| D14 | The prompt templates are edited as a minimal delta against the files on disk | Overwriting them with the versions drafted in §9.3 | Two of the three original defects were repaired upstream after this document was first drafted, and the current `resolve_review_prompt.md` is better than the proposed replacement. A wholesale overwrite would be a regression (§9.3). |
| D15 | Task 8 opens with a blocking ConPTY spike | Treating the documented call sequence as executable | The sequence as written does not stream output on this machine, reproducibly, despite every native call succeeding and the child provably attaching (§6.3.1). Every phase depends on this edge. |
