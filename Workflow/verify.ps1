#Requires -Version 7
<#
.SYNOPSIS
    Acceptance gate for the Workflow application.
.DESCRIPTION
    Automates criteria A1-A6, V1-V4 and the subtask checks V4a/V4b of the specification. Manual
    steps V5-V40 are listed at the end and are not automated: they need a human watching a live
    terminal.

    This script requires PowerShell 7 (pwsh), not Windows PowerShell 5.1 (powershell).
.PARAMETER WorkflowDirectory
    Root of a checked-out Workflows tracking repository. Optional. When it is supplied, V4b checks
    the shipped task_template against requirement 6.5; when it is not, V4b reports those checks as
    SKIPPED rather than passing them, because the tracking repository is a separate checkout that
    this repository cannot assume is present.
.PARAMETER SkipConPtyTests
    Excludes the two ConPtySessionTests that need a real pseudo-console from V2. A headless session
    has no console, so the PTY yields no bytes and those two fail for environmental reasons, taking
    `dotnet test`'s exit code with them. OFF by default: on a machine with an attached console they
    are real coverage, and a gate that silently drops them is worth less than one that fails loudly.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $WorkflowDirectory = '',
    [switch] $SkipConPtyTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'Workflow.sln'
$failures = @()
$skips = @()

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

# A skipped check is NOT a passed check. Anything reported here was not verified by this run, and
# the summary repeats it so a green exit cannot be mistaken for full coverage.
function Write-Skip {
    param([string] $Message, [string] $HowToRun)
    Write-Host "  SKIP  $Message" -ForegroundColor Yellow
    $script:skips += "$Message -- to check it: $HowToRun"
}

Write-Host 'V1  Build with warnings as errors'
$buildLog = & dotnet build $solution -c $Configuration --nologo 2>&1
Assert-True ($LASTEXITCODE -eq 0) 'dotnet build exits 0'
Assert-True (-not ($buildLog | Select-String -Pattern ': warning ' -Quiet)) 'build produced no warnings'

Write-Host 'V2  Tests'
$testArguments = @('test', $solution, '-c', $Configuration, '--nologo')
if ($SkipConPtyTests) {
    # Exactly the two tests that need a live pseudo-console, not the whole class: the other five
    # ConPtySessionTests pass headlessly and excluding them too would give up real coverage.
    $testArguments += @(
        '--filter',
        'FullyQualifiedName!~ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput&FullyQualifiedName!~ConPtySessionTests.Start_EmitsTheLauncherFrameBeforeAnyInput')
    Write-Skip 'ConPtySessionTests.Start_RunsACommandAndStreamsItsOutput and .Start_EmitsTheLauncherFrameBeforeAnyInput excluded from V2' `
        'run without -SkipConPtyTests on a machine with an attached console'
}
& dotnet @testArguments | Out-Host
Assert-True ($LASTEXITCODE -eq 0) 'dotnet test exits 0'

# Requirement 6.3/6.4: every shipped prompt is validated against ITS OWN token set, mirroring
# PromptTemplateCatalog.AllowedTokens. A single flat list would accept {subtask} inside a phase
# prompt and, before this catalog existed, rejected {tasktitel} in the two code-unreferenced
# prompts. Keep the three layers and the eleven entries below in step with
# Workflow\Services\PromptTemplateCatalog.cs and Workflow\Services\PromptTemplateService.cs; V4
# now checks that agreement mechanically instead of trusting it.
# Defined here rather than in V4 because V3 derives its prompt manifest from the same catalog.
$promptDir = Join-Path $repo 'Workflow\Prompt'
$baseTokens = @('taskbezeichnung', 'taskbeschreibung', 'AppDirectory', 'spec_path', 'plan_path', 'review_path', 'simplified_path', 'done_path')
$creationTokens = $baseTokens + @('workflow_path', 'tasktitel', 'task_path', 'subtask_path')
$runTokens = $creationTokens + @('subtask_title', 'subtask')

$promptCatalog = [ordered] @{
    'initial_prompt.md'        = $baseTokens
    'simplifier_prompt.md'     = $baseTokens
    'review_prompt.md'         = $baseTokens
    'resolve_review_prompt.md' = $baseTokens
    'implementation_prompt.md' = $baseTokens
    'create_subtasks.md'       = $creationTokens
    'counter_prompt.md'        = $creationTokens
    'evidence_gate.md'         = $creationTokens
    'run_subtask.md'           = $runTokens
    'create_decisions.md'      = $creationTokens
    'no_human.md'              = @()
}

Write-Host 'V3  Output directory manifest'
$out = Join-Path $repo "Workflow\bin\$Configuration\net8.0-windows"
# The prompt entries are derived from $promptCatalog instead of being listed a second time. A
# hand-written copy had already drifted: it named only the four phase prompts, so the four subtask
# templates could be catalogued and token-checked in source yet fail to reach bin\ without any
# check noticing.
$required = @($promptCatalog.Keys | ForEach-Object { "Prompt\$_" }) + @(
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
# The catalog itself is defined above V3, which shares it.
foreach ($name in $promptCatalog.Keys) {
    Assert-True (Test-Path (Join-Path $promptDir $name)) "catalogued prompt ships: $name"
}

foreach ($file in Get-ChildItem -Path $promptDir -Filter '*.md' -Recurse -File) {
    $relative = $file.FullName.Substring($promptDir.Length).TrimStart('\', '/')
    $content = Get-Content -Raw -Path $file.FullName
    Assert-True ($content.Trim().Length -gt 0) "non-empty: $relative"

    if (-not $promptCatalog.Contains($relative)) {
        Assert-True $false "prompt catalog recognizes shipped file: $relative"
        continue
    }

    $allowed = $promptCatalog[$relative]
    $tokens = [regex]::Matches($content, '\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}') |
        ForEach-Object { $_.Groups['n'].Value } |
        Sort-Object -Unique

    foreach ($token in $tokens) {
        Assert-True ($allowed -contains $token) "token {$token} allowed in $relative"
    }
}

Assert-True (-not (Select-String -Path (Join-Path $promptDir 'implementation_prompt.md') -Pattern '\{plan_path\}_' -Quiet)) `
    'implementation_prompt.md has no stray {plan_path}_'
Assert-True (Select-String -Path (Join-Path $promptDir 'review_prompt.md') -Pattern '\{review_path\}' -Quiet) `
    'review_prompt.md uses {review_path}'
Assert-True (Select-String -Path (Join-Path $promptDir 'implementation_prompt.md') -Pattern '\{done_path\}' -Quiet) `
    'implementation_prompt.md uses {done_path}'

# The catalog above exists twice - here and in C# - with nothing mechanically tying the two lists
# together, which is a recorded risk rather than a theoretical one. These checks tie them: a prompt
# file or a substituted token added on one side and forgotten on the other now breaks a gate
# loudly, instead of leaving this script silently validating against a stale set.
$phaseSource = Get-Content -Raw -Path (Join-Path $repo 'Workflow\Models\PhaseCatalog.cs')
$catalogSource = Get-Content -Raw -Path (Join-Path $repo 'Workflow\Services\PromptTemplateCatalog.cs')
$variableSource = Get-Content -Raw -Path (Join-Path $repo 'Workflow\Services\PromptTemplateService.cs')

$csharpPrompts = [regex]::Matches("$phaseSource`n$catalogSource", '"(?<f>[A-Za-z0-9_.-]+\.md)"') |
    ForEach-Object { $_.Groups['f'].Value } |
    Sort-Object -Unique
$promptDrift = Compare-Object -ReferenceObject @($promptCatalog.Keys) -DifferenceObject @($csharpPrompts)
if ($promptDrift) {
    $detail = ($promptDrift | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', '
    Assert-True $false "prompt catalog agrees with PhaseCatalog + PromptTemplateCatalog ($detail)"
}
else {
    Assert-True $true 'prompt catalog agrees with PhaseCatalog + PromptTemplateCatalog'
}

# Every token PromptVariables actually substitutes, read from its dictionary writes. The lookbehind
# skips Regex `Groups["name"]`, which is not a substitution.
$substituted = [regex]::Matches($variableSource, '(?<!Groups)\["(?<t>[A-Za-z_][A-Za-z0-9_]*)"\]') |
    ForEach-Object { $_.Groups['t'].Value } |
    Sort-Object -Unique
$tokenDrift = Compare-Object -ReferenceObject @($runTokens | Sort-Object -Unique) -DifferenceObject @($substituted)
if ($tokenDrift) {
    $detail = ($tokenDrift | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', '
    Assert-True $false "token layers agree with the tokens PromptTemplateService substitutes ($detail)"
}
else {
    Assert-True $true 'token layers agree with the tokens PromptTemplateService substitutes'
}

Write-Host 'V4a  Subtask prompt contract'
# Requirements 6.2, 6.4, 6.6, 6.7 and 6.8. V4 proves each prompt uses only tokens it is ALLOWED to
# use; it cannot prove the prompt still says the right thing. Every correction tasks 3.2 and 3.3
# made could be undone without introducing a single unknown token, so it needs its own checks.

# A catalogued prompt that is MISSING is already reported by V4 above. Reading it unguarded would
# then throw under $ErrorActionPreference = 'Stop' and end the run before the FAILED summary: the
# gate would still exit non-zero, but would never say what failed. An absent or empty file becomes
# an empty string instead, so the checks below fail loudly and the summary still prints.
function Read-PromptText {
    param([string] $FileName)
    $path = Join-Path $promptDir $FileName
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $raw = Get-Content -Raw -Path $path
    if ($null -eq $raw) { return '' }
    return $raw
}

$creationPrompt = Read-PromptText 'create_subtasks.md'
$runPrompt = Read-PromptText 'run_subtask.md'
$subtaskPrompts = [ordered] @{
    'create_subtasks.md' = $creationPrompt
    'run_subtask.md'     = $runPrompt
}

foreach ($name in $subtaskPrompts.Keys) {
    $text = $subtaskPrompts[$name]

    # 6.6: every substituted path token is absolute, so one must never be prefixed with another.
    # The source prompts wrote {workflow_path}\{subtask_path}\..., which double-prefixed every path.
    Assert-True (-not ($text -match '\{(?:workflow_path|task_path)\}[\\/]\s*\{(?:task_path|subtask_path)\}')) `
        "no duplicated path prefix in $name"

    # 6.4: the completion flag is task-local and singular. A plural name misses the file the
    # application reads; a flag under the shared tracking root reports the NEXT task complete at once.
    Assert-True (-not ($text -match 'results\.json')) "no plural flag name in $name"
    Assert-True (-not ($text -match '\{workflow_path\}[\\/]result\.json')) "no root-level flag in $name"

    # 6.2: both files are published atomically through a temporary file, or the application reads a
    # half-written one.
    Assert-True ($text -match 'status\.json\.tmp') "$name requires atomic status.json publication"
    Assert-True ($text -match 'result\.json\.tmp') "$name requires atomic result.json publication"

    # 6.2: and the status payload is published BEFORE the flag, because the application reacts to
    # the flag alone. Anchored on the RULE, not on order of first mention: first mention cannot
    # fail for create_subtasks.md, whose file listing names status.json long before the rule, so an
    # inverted rule would pass such a check. The two prompts are written in different languages, so
    # the ordering keyword is accepted in either one, but the shape is the same in both and the
    # match may not cross a blank line: the rule has to say it inside a single paragraph.
    $span = '(?:[^\r\n]|\r?\n(?!\s*\r?\n))*?'
    $orderRule = '(?i)\b(?:zuerst|first)\b' + $span + 'status\.json(?!\.tmp)' +
        $span + '\b(?:danach|then)\b' + $span + 'result\.json(?!\.tmp)'
    Assert-True ($text -match $orderRule) `
        "$name states in one paragraph that status.json is published before result.json"

    # The non-empty rule (6.5): a 0-byte flag is read as no flag at all.
    Assert-True ($text -match '(?i)0\s*byte') "$name states the 0-byte flag rule"

    # 6.8: task-wide findings live in the task folder, so {subtask_path} holds subtask folders only.
    Assert-True ($text -match '\{task_path\}[\\/]findings\.md') `
        "$name sends task-wide findings to {task_path}/findings.md"
}

# 6.7: the creation prompt publishes the ordered index as the task-local flag, and shows `pending`
# as the initial status rather than the `complete` example the source prompt carried.
Assert-True ($creationPrompt -match '\{task_path\}[\\/]result\.json') `
    'create_subtasks.md publishes {task_path}/result.json'
Assert-True ($creationPrompt -match '"subtasks"') `
    'create_subtasks.md names the mandatory ordered "subtasks" field'
Assert-True ($creationPrompt -match '"status"\s*:\s*"pending"') `
    'create_subtasks.md shows the initial status pending'

# 6.6: the only composition left to an agent is appending a subtask name to {subtask_path}. Angle
# brackets mark the name the agent chooses, braces the values the application replaces.
Assert-True ($creationPrompt -match '\{subtask_path\}[\\/]<subtask_title>') `
    'create_subtasks.md composes only {subtask_path}/<subtask_title>'
Assert-True ($runPrompt -match '\{subtask_path\}[\\/]\{subtask_title\}') `
    'run_subtask.md composes only {subtask_path}/{subtask_title}'

Write-Host 'V4b  Tracking contract'
# Requirements 6.1 and 6.5. The tracking repository is a SEPARATE checkout, so only two things can
# be asserted honestly from here: the application-side names that define the contract, which do live
# in this repository, and the shipped template - the latter only when a tracking directory is
# actually supplied. A template check that "passed" whenever that repository is absent would be
# worse than no check at all.
$subtaskPathsSource = Get-Content -Raw -Path (Join-Path $repo 'Workflow\Models\SubtaskPaths.cs')

# Every tracking file name the prompts above teach must be a name SubtaskPaths actually composes.
# Renaming one on either side without the other is the failure this guards; nothing else connects
# the prose of the prompts to the C# path set.
foreach ($literal in @('"task_template"', '"subtasks"', '"subtask.md"', '"status.json"', '"result.json"')) {
    Assert-True ($subtaskPathsSource.Contains($literal)) "SubtaskPaths composes $literal"
}
Assert-True ($subtaskPathsSource -match 'TemplateFolderName\s*=\s*"task_template"') `
    'SubtaskPaths.TemplateFolderName is task_template'

$exampleSubtask = 'ST-001-subtask-template'
if ([string]::IsNullOrWhiteSpace($WorkflowDirectory)) {
    Write-Skip 'tracking-repository template (requirement 6.5) not checked' `
        'pass -WorkflowDirectory <root of the checked-out Workflows repository>'
}
else {
    $template = Join-Path $WorkflowDirectory 'task_template'
    Assert-True (Test-Path -Path $template -PathType Container) `
        "tracking template folder exists: $template"

    # 6.5: the three files whose absence or emptiness teaches the wrong contract. The application's
    # non-empty-file rule reads a 0-byte example as no flag at all.
    foreach ($relative in @('result.json', "subtasks\$exampleSubtask\status.json", "subtasks\$exampleSubtask\result.json")) {
        $path = Join-Path $template $relative
        Assert-True ((Test-Path -Path $path -PathType Leaf) -and ((Get-Item $path).Length -gt 0)) `
            "template file present and non-empty: task_template\$relative"
    }

    $statusPath = Join-Path $template "subtasks\$exampleSubtask\status.json"
    Assert-True ((Test-Path -Path $statusPath -PathType Leaf) -and
        ((Get-Content -Raw -Path $statusPath) -match '"status"\s*:\s*"pending"')) `
        'template status.json shows the initial status pending'

    # 6.1: order comes from the index, never from a directory listing, so the one folder that exists
    # must be the one the index names.
    $index = $null
    try {
        $index = Get-Content -Raw -Path (Join-Path $template 'result.json') | ConvertFrom-Json
    }
    catch {
        $index = $null
    }
    Assert-True ($null -ne $index -and $null -ne $index.subtasks -and @($index.subtasks) -contains $exampleSubtask) `
        "template result.json lists $exampleSubtask in its ordered subtasks array"
}

Write-Host ''
if ($skips.Count -gt 0) {
    Write-Host "SKIPPED: $($skips.Count) check group(s) - NOT verified by this run" -ForegroundColor Yellow
    $skips | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host ''
}

if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures.Count) check(s)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

if ($skips.Count -gt 0) {
    Write-Host 'All automated checks that were run passed; see SKIPPED above.' -ForegroundColor Green
}
else {
    Write-Host 'All automated checks passed.' -ForegroundColor Green
}
Write-Host ''
Write-Host 'Remaining manual steps (V5-V40) - see specification section 15.3 (V22: workflow_border\workflow_border_spec.md section 11.2; V23-V27: Workflow_LOAD_AND_PAUSE\Workflow_LOAD_AND_PAUSE_spec.md section 11.2; V24 and V28-V32: Workflow_LoadFix\Workflow_LoadFix_spec.md section 11.2; V33: Vereinfachen phase; V34: WF_REPAIR\WF_REPAIR_spec.md section 4.7; V35: WF_MESSAGE\WF_MESSAGE_spec.md section 9; V36: WF_TABZUVIEL\WF_TABZUVIEL_spec.md section 9; V37: WF_DESKTOPJOBBUTTON\WF_DESKTOPJOBBUTTON_spec.md section 4.8; V38: WF_QUEUE\WF_QUEUE_spec.md section 9; V39: WF_REVIEWENTER\WF_REVIEWENTER_spec.md section 9; V40: open gate items, see the step text):'
Write-Host '  V5  Run the full five-phase pipeline against a scratch directory.'
Write-Host '  V6  Kill claude.exe mid-Spezifikation; the app must stay responsive and must not advance.'
Write-Host '  V7  Break the auto-answer pattern in %APPDATA%\Workflow\autoanswer.rules.json;'
Write-Host '      no answer is sent; the prompt is still pasted PROMPTLY once the screen goes quiet'
Write-Host '      (NOT after 60s - the ceiling is not a mandatory wait; spec 7.3 / D13), terminal usable.'
Write-Host '  V8  After Schliessen, Task Manager shows no orphan pwsh/node/claude/codex.'
Write-Host '  V9  Covered by dotnet test: a throwing orchestrator must surface a message, not an'
Write-Host '      unobserved task exception.'
Write-Host '  V10 Two tabs, both running: switch between them three times; both must keep streaming.'
Write-Host '  V11 Pick a drive root (C:\) as the working directory; the folder must land at C:\<name>.'
Write-Host '  V12 Run a task to the end. .workflow-state.json must hold five Completed phases,'
Write-Host '      plausible timestamps and the Taskbeschreibung verbatim.'
Write-Host '  V13 Kill Workflow.exe while Review is yellow, restart: a prefilled tab appears with'
Write-Host '      "Continue workflow"; clicking it re-sends the REVIEW prompt and does not re-run Spezifikation.'
Write-Host '  V14 During Review umsetzen, touch only T_spec.md - the phase must stay yellow. Then touch'
Write-Host '      T_plan.md - it must go green.'
Write-Host '  V15 Implementierung must turn green on its own once T-done.md exists and is non-empty.'
Write-Host '  V16 Every phase must submit its prompt without the user pressing Enter.'
Write-Host '  V17 Delete T_plan.md from a task whose journal says Spezifikation is complete, restart:'
Write-Host '      the recovered tab must show Spezifikation grey and resume at Spezifikation.'
Write-Host '  V18 Put a disconnected network share in the directory MRU: the window must still'
Write-Host '      appear promptly, the app must stay usable, and the recovered tabs for the'
Write-Host '      reachable MRU entries must still appear within roughly five seconds.'
Write-Host '  V19 Pick a different working directory in a "Neuer Task" tab while the startup scan is'
Write-Host '      still running: nothing may fault and no error label may appear. Then switch a'
Write-Host '      tab showing a green Spezifikation to a directory where that name has no journal:'
Write-Host '      all five indicators must go grey.'
Write-Host '  V20 Type a finished task name into a fresh tab: all five indicators grey and the'
Write-Host '      button reading "Start workflow".'
Write-Host '  V21 Re-run that finished task and kill Workflow.exe during Spezifikation: the recovered'
Write-Host '      tab must show all five indicators grey, not the later phases green.'
Write-Host '  V22 Every tab''s terminal sits inside the gold frame: corners and ornaments keep their'
Write-Host '      shape from minimum to maximised size, no hairline shows at any cut or along the'
Write-Host '      opening at this monitor''s scaling, no grey outline inside the gold, and typing,'
Write-Host '      selection, scrolling and the auto-answers work as before.'
Write-Host '  V23 Click "Task laden" (top right, under the version number) and pick <repo>\workflow_border:'
Write-Host '      a new tab opens with Spezifikation, Review and Review umsetzen green, Vereinfachen skipped, Implementierung grey, "Continue workflow" and a read-only'
Write-Host '      name. Pick it again: no second tab, the open one is selected. Load a finished task (five'
Write-Host '      green, info line) and a subtask task (checkbox, directory, counts). "Neuer Task" looks as'
Write-Host '      before. Overflow: at the minimum width (1100) open tabs until the headers no longer fit -'
Write-Host '      the "Task laden" chip stays fully visible, uncovered and clickable; then maximise and'
Write-Host '      check again.'
Write-Host '  V24 "Task laden" on C:\: one error dialog, no tab added. (A folder without a journal is now'
Write-Host '      loaded and gets one - see V28.)'
Write-Host '  V25 Press Pause during Spezifikation: Play appears; it turns green, Vereinfachen does not start,'
Write-Host '      the terminal stays usable. Press Play: Vereinfachen starts at once. Pause then Play while'
Write-Host '      Vereinfachen runs: Review follows without a hold.'
Write-Host '  V26 (tab) Hold a scratch task''s run after Spezifikation, then close its tab: no error message;'
Write-Host '      Task Manager shows no pwsh/node/claude/codex left from that tab.'
Write-Host '  V27 (application) Hold a run after Spezifikation, then close the application: no orphan'
Write-Host '      pwsh/node/claude/codex; after a restart the task is offered with "Continue workflow"'
Write-Host '      at Vereinfachen.'
Write-Host '  V28 Copy a task folder holding <T>_spec.md and <T>_plan.md, delete its .workflow-state.json'
Write-Host '      and "Task laden" it: no dialog; Spezifikation green, "Continue workflow", info line'
Write-Host '      "Neue .workflow-state.json angelegt. Nächste Phase: Vereinfachen."; the journal exists afterwards.'
Write-Host '  V29 On a BYTE COPY of ...\Modul\QService\VSDM2 - never the real folder (copy and hash commands:'
Write-Host '      Workflow_LoadFix\Workflow_LoadFix_spec.md section 11.2): "Task laden" on the copy; the Ja/Nein'
Write-Host '      question names VSDM2_spec.md, VSDM2_plan.md; Ja, pick the -design.md file, then the other:'
Write-Host '      both renamed, Spezifikation green, resume Vereinfachen; Subtasks ticked and both start buttons'
Write-Host '      disabled until a Workflow-Verzeichnis is chosen. Close the tab: the three hash checks print'
Write-Host '      True (the real VSDM2 is unchanged). On a fresh copy cancel the first file dialog: nothing'
Write-Host '      renamed, no tab, the journal bytes unchanged. Delete the copies.'
Write-Host '  V30 On the V28 tab press "Implementierung beginnen": Vereinfachen, Review and Review umsetzen show the'
Write-Host '      blue-grey skip icon, Implementierung turns yellow, the implementation prompt is sent; with'
Write-Host '      Subtasks ticked and a valid directory the decomposition session starts instead.'
Write-Host '  V31 "Continue workflow" on a scratch task at Review; while Codex works, note the PIDs of the'
Write-Host '      review session under THIS Workflow window (process-tree commands: spec section 11.2), then'
Write-Host '      press "Implementierung beginnen": none of the noted PIDs is still running, Implementierung starts'
Write-Host '      under new PIDs, and .workflow-state.json reads "Skipped" for Simplifier, Review and ResolveReview.'
Write-Host '  V32 Kill Workflow.exe during the Implementierung of V30 and restart: the recovered tab shows Spezifikation'
Write-Host '      green, Vereinfachen, Review and Review umsetzen with the skip icon, and "Continue workflow" resumes Implementierung.'
Write-Host '  V33 Run a scratch task past Spezifikation: Vereinfachen turns yellow, a Claude session gets the'
Write-Host '      simplifier prompt, spec and plan are edited in place (or stay byte-identical when the report'
Write-Host '      says "No simplification required"), <T>-simplified.md is written last, the phase turns green'
Write-Host '      and Review starts. The task folder diff holds only <T>_spec.md, <T>_plan.md and'
Write-Host '      <T>-simplified.md. A task loaded from before this phase existed shows Vereinfachen with the'
Write-Host '      skip icon and keeps its later phases.'
Write-Host '  V34 On a scratch task with Subtasks ticked, let a subtask request a one-step desktop job whose script exits 1.'
Write-Host '      Once the job has run (one desktopjob.<id>.log header only), a new session starts for that subtask without a'
Write-Host '      second job run; it renames desktopjob.result.json to desktopjob.result.<requestId>.consumed.json and writes'
Write-Host '      a desktopjob.json with a new requestId.'
Write-Host '  V35 On a scratch task with Subtasks ticked, let a subtask defer to a one-step desktop job with'
Write-Host '      idleMinutes 1 whose script sleeps 5 minutes. Stay away until the step starts, then move the mouse:'
Write-Host '      the step ends aborted_user_input. While you keep working, the status line reads'
Write-Host '      "... min nötig; letzter Versuch: aborted_user_input nach <N> min, 1 Versuch)." (hover shows the'
Write-Host '      whole line if it is trimmed). A subtask that never ran a job shows the line without the note.'
Write-Host '  V36 Start with no unfinished task: the tab strip shows only "Neuer Task" and "Task laden", no tab.'
Write-Host '      Press "Neuer Task": one tab "(Bezeichnung)" opens and is selected; close it: no tab again.'
Write-Host '      Start with unfinished tasks: exactly those tabs, the first selected, no blank tab beside them.'
Write-Host '      With no tab open, press "Task laden": the folder dialog starts in the last working directory.'
Write-Host '  V37 On a scratch task with Subtasks ticked, let a subtask request a one-step desktop job (idleMinutes 15)'
Write-Host '      whose script waits 30 s and creates its marker. While "wartet auf Leerlauf" shows, the button "Desktop-Job jetzt starten"'
Write-Host '      is enabled (disabled while paused). Press it and move the mouse within the next minute: the status returns to'
Write-Host '      "wartet auf Leerlauf", nothing is recorded and the button is enabled again. Press it again and take the hands off:'
Write-Host '      after about a minute the status reads "Desktop-Job läuft", the window minimises, the job completes and the'
Write-Host '      verification session starts. Repeat, keep still for the minute, then move the mouse: the job ends aborted_user_input.'
Write-Host '  V38 Open two scratch tasks A and B as tabs. In B''s "Queue" box the list shows A (and every other tab), never B.'
Write-Host '      Pick A; start A and let it reach Implementierung, then press "Task abschliessen" on A (or let A finish):'
Write-Host '      B starts by itself and its Queue box is empty. Queue B after A again, then close A''s tab instead: B''s box'
Write-Host '      clears and its info line reads "Queue aufgehoben: ''A'' wurde geschlossen.". With a third tab C in the same'
Write-Host '      working directory, queue B and C both after A and finish A: only B starts, C''s box shows B and its info'
Write-Host '      line says it waits for B. The clear button (x) empties the box; while B runs the box is disabled; the'
Write-Host '      bottom bar still fits at a narrow window width.'
Write-Host '  V39 Run a scratch task to the Review phase five times (or re-run Review via "Task laden"). Each time Codex must'
Write-Host '      start working on the review prompt by itself, within about 15 s of the prompt appearing in its input -'
Write-Host '      nobody presses Enter. If the yellow line "Das automatische Absenden des Prompts konnte nicht bestätigt'
Write-Host '      werden..." appears, note the time and the last entries of %USERPROFILE%\.codex\history.jsonl.'
Write-Host '  V40 Load a finished scratch task whose T-done.md has a "## Offene Punkte" section with two bullets: an orange'
Write-Host '      icon appears right after "Implementierung". Clicking it opens a popup above it listing both items under'
Write-Host '      "Implementierung"; "Alles kopieren" puts the same text on the clipboard and its icon returns from the'
Write-Host '      check mark after about 1.5 s. A click beside the popup closes it and the icon is unpressed again. With'
Write-Host '      "- keine" as the only bullet there is no icon. A subtask task whose completed status.json carries'
Write-Host '      "openGateItems" lists them under the subtask title; "openGateItems": 42 shows the read-error icon.'
exit 0
