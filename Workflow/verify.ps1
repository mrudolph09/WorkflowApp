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
