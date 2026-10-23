@echo off
setlocal

set "SRC=%~dp0Workflow\bin\Release\net8.0-windows"
set "DST=C:\Program Files\Workflow"

rem Second pass, started elevated by the first one below: the build is done, only copy.
if /i "%~1"=="--copy" goto copy

rem Always build first, without admin rights, so a deploy can never ship a stale Release build.
echo Baue Release ...
dotnet build "%~dp0Workflow\Workflow.csproj" -c Release -nologo -v:minimal
if errorlevel 1 (
    echo Fehler beim Bauen.
    pause
    exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
    echo Administratorrechte werden angefordert...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '--copy' -Verb RunAs"
    exit /b
)

:copy
rem A running Workflow locks its files, and robocopy would fail on them.
tasklist /fi "imagename eq Workflow.exe" /nh | find /i "Workflow.exe" >nul
if not errorlevel 1 (
    echo Workflow laeuft noch. Bitte schliessen und dann eine Taste druecken.
    pause >nul
    goto copy
)

echo Kopiere "%SRC%" nach "%DST%" ...
robocopy "%SRC%" "%DST%" /E /R:2 /W:2 /NP /NFL /NDL

if errorlevel 8 (
    echo Fehler beim Kopieren, Robocopy-Code %errorlevel%.
    pause
    exit /b 1
)

echo Fertig.
pause
exit /b 0
