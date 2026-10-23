using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Workflow.Models;
using Workflow.Services;
using Workflow.Tests.Fakes;

namespace Workflow.Tests;

/// <remarks>
/// The runner of one desktop job step, against fakes for everything time- and OS-related: the
/// activity monitor, the CPU probe, the process starter, the clock and the foreground yield.
/// </remarks>
public sealed class DesktopJobRunnerTests : IDisposable
{
    private readonly string _root;
    private readonly string _subtask;
    private readonly string _product;
    private readonly FakeUserActivityMonitor _monitor = new();
    private readonly FakeCpuLoadProbe _cpu = new();
    private readonly FakeDesktopJobProcessStarter _starter = new();
    private readonly FakeDesktopJobClock _clock = new();
    private readonly FakeForegroundYield _yield = new();

    public DesktopJobRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-djob-" + Guid.NewGuid().ToString("N"));
        _subtask = Path.Combine(_root, "subtasks", "ST-001-alpha");
        _product = Path.Combine(_root, "product");
        Directory.CreateDirectory(_subtask);
        Directory.CreateDirectory(_product);

        _starter.MarkerFor = launch => Marker(StepIdOf(launch));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ResultPath => Path.Combine(_subtask, DesktopJobFiles.ResultFileName);

    private string Marker(string stepId) => Path.Combine(_root, "marker-" + stepId + ".txt");

    private static string StepIdOf(DesktopJobLaunch launch) =>
        Path.GetFileName(launch.ScriptPath)["desktopjob.".Length..^".ps1".Length];

    private DesktopJobRunner CreateRunner() =>
        new(_monitor, _cpu, _starter, _clock, _yield, monitorInterval: TimeSpan.FromSeconds(30));

    private Task<DesktopJobStepReport> RunAsync(bool skipIdleWait = false) =>
        CreateRunner().RunNextStepAsync(_subtask, _product, skipIdleWait, CancellationToken.None);

    /// <summary>Writes desktopjob.json and one script per step.</summary>
    private void WriteRequest(Guid requestId, params (string Id, int TimeoutMinutes)[] steps)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = 2,
            requestId,
            idleMinutes = 15,
            steps = steps.Select(step => new
            {
                id = step.Id,
                script = $"desktopjob.{step.Id}.ps1",
                timeoutMinutes = step.TimeoutMinutes,
                marker = Marker(step.Id),
            }),
        });

        File.WriteAllText(Path.Combine(_subtask, DesktopJobFiles.RequestFileName), json);

        foreach (var step in steps)
        {
            var script = Path.Combine(_subtask, $"desktopjob.{step.Id}.ps1");
            if (!File.Exists(script))
            {
                File.WriteAllText(script, $"Write-Output '{step.Id}'");
            }
        }
    }

    private void WriteRawRequest(string json) =>
        File.WriteAllText(Path.Combine(_subtask, DesktopJobFiles.RequestFileName), json);

    private JsonElement ReadResult()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResultPath));
        return document.RootElement.Clone();
    }

    private static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // --- Preconditions: nothing starts outside the idle window -------------------------------

    [Fact]
    public async Task NotIdleLongEnough_StartsNothing()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.FromMinutes(3), false, false);

        var report = await RunAsync();

        Assert.Null(report.Outcome);
        Assert.Empty(_starter.Launches);
        Assert.Empty(_cpu.Windows);
        Assert.False(File.Exists(ResultPath));
    }

    [Fact]
    public async Task LockedSession_StartsNothing()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.FromHours(2), IsLocked: true, false);

        var report = await RunAsync();

        Assert.Null(report.Outcome);
        Assert.Empty(_starter.Launches);
    }

    [Fact]
    public async Task RunningScreenSaver_StartsNothing()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.FromHours(2), false, IsScreenSaverRunning: true);

        var report = await RunAsync();

        Assert.Null(report.Outcome);
        Assert.Empty(_starter.Launches);
    }

    [Fact]
    public async Task BusyMachine_AbortsBusyWithoutStartingAndWithoutCountingAnAttempt()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _cpu.Load = 25;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.AbortedBusy, report.Outcome);
        Assert.False(report.CountsAsAttempt);
        Assert.Empty(_starter.Launches);
        Assert.Equal([TimeSpan.FromSeconds(60)], _cpu.Windows);
        Assert.Empty(_yield.Calls);
        Assert.Equal(DesktopJobOutcome.AbortedBusy, ReadResult().GetProperty("steps")[0].GetProperty("outcome").GetString());
    }

    // --- Desktop-Job jetzt starten: starts right away, lock, screen saver and input watch kept ----

    [Fact]
    public async Task SkipIdleWait_StartsTheStepRightAwayWithoutMeasuringTheCpu()
    {
        // Arrange - the user has just clicked: no idle time at all.
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.Zero, false, false);

        // Act
        var report = await RunAsync(skipIdleWait: true);

        // Assert
        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Single(_starter.Launches);
        Assert.Empty(_cpu.Windows);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SkipIdleWait_StillStartsNothingOnALockedOrScreenSavedDesktop(bool locked, bool screenSaver)
    {
        // Arrange
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.Zero, locked, screenSaver);

        // Act
        var report = await RunAsync(skipIdleWait: true);

        // Assert
        Assert.Null(report.Outcome);
        Assert.Empty(_starter.Launches);
        Assert.Empty(_cpu.Windows);
        Assert.False(File.Exists(ResultPath));
    }

    [Fact]
    public async Task SkipIdleWait_RealInputDuringTheRun_StillAbortsAndKillsTheProcess()
    {
        // Arrange
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.Zero, false, false);
        _starter.ExitCode = null;
        _starter.OnStart = _ => _monitor.LastWatch!.ReportMouse(flags: 0);

        // Act
        var report = await RunAsync(skipIdleWait: true);

        // Assert
        Assert.Equal(DesktopJobOutcome.AbortedUserInput, report.Outcome);
        Assert.True(report.CountsAsAttempt);
        Assert.True(_starter.Processes.Single().Killed);
    }

    [Fact]
    public async Task SkipIdleWait_ABusyMachine_StartsWithoutMeasuring()
    {
        // Arrange - the user asked for the start: the CPU guard is the automatic start's alone.
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.Zero, false, false);
        _cpu.Load = DesktopJobRunner.BusyThresholdPercent;

        // Act
        var report = await RunAsync(skipIdleWait: true);

        // Assert
        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Single(_starter.Launches);
        Assert.Empty(_cpu.Windows);
    }

    [Fact]
    public async Task SkipIdleWait_AfterAScriptFailure_StillLaunchesNothing()
    {
        // Arrange
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;
        await RunAsync();
        _monitor.Activity = new UserActivity(TimeSpan.Zero, false, false);

        // Act
        var second = await RunAsync(skipIdleWait: true);

        // Assert
        Assert.Equal(DesktopJobOutcome.ScriptFailed, second.Outcome);
        Assert.Single(_starter.Launches);
        Assert.Equal(1, ReadResult().GetProperty("steps")[0].GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task SkipIdleWait_InputRightBeforeTheStart_StillStarts()
    {
        // Arrange - the mouse moved seconds ago (the click itself); only input after the watch starts counts.
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.Activity = new UserActivity(TimeSpan.FromSeconds(2), false, false);

        // Act
        var report = await RunAsync(skipIdleWait: true);

        // Assert
        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Single(_starter.Launches);
    }

    // --- Watching the running step -----------------------------------------------------------

    [Fact]
    public async Task RealInputDuringTheRun_AbortsAndKillsTheProcess()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = null;
        _starter.OnStart = _ => _monitor.LastWatch!.ReportMouse(flags: 0);

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.AbortedUserInput, report.Outcome);
        Assert.True(report.CountsAsAttempt);
        Assert.True(_starter.Processes.Single().Killed);
        Assert.True(_starter.Processes.Single().Disposed);
        Assert.True(_monitor.LastWatch!.Disposed);
        Assert.Equal(["minimise", "restore"], _yield.Calls);
    }

    [Fact]
    public async Task InjectedInputDuringTheRun_IsIgnored()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.OnStart = _ =>
        {
            _monitor.LastWatch!.ReportKeyboard(LowLevelInputFlags.KeyboardInjected);
            _monitor.LastWatch.ReportMouse(LowLevelInputFlags.MouseInjected);
        };

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.False(_starter.Processes.Single().Killed);
    }

    [Fact]
    public async Task ALockDuringTheRun_AbortsLockedWithoutCountingAnAttempt()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = null;
        _starter.OnStart = _ => _monitor.Activity = _monitor.Activity with { IsLocked = true };

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.AbortedLocked, report.Outcome);
        Assert.False(report.CountsAsAttempt);
        Assert.True(_starter.Processes.Single().Killed);
    }

    [Fact]
    public async Task AStepThatNeverExits_TimesOutAtExactlyItsLimit()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 2));
        _starter.ExitCode = null;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Timeout, report.Outcome);
        Assert.True(_starter.Processes.Single().Killed);

        var step = ReadResult().GetProperty("steps")[0];
        var started = step.GetProperty("startedUtc").GetDateTimeOffset();
        var ended = step.GetProperty("endedUtc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromMinutes(2), ended - started);

        // Four 30 s polls reach the limit; a fifth would mean it was overshot.
        Assert.Equal(4, _clock.Delays.Count(delay => delay == TimeSpan.FromSeconds(30)));
    }

    // --- Outcomes and the result file --------------------------------------------------------

    [Fact]
    public async Task ExitZeroAndMarker_Completes()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.True(report.JobComplete);

        var launch = _starter.Launches.Single();
        Assert.Equal(Path.Combine(_subtask, "desktopjob.serie.ps1"), launch.ScriptPath);
        Assert.Equal(_product, launch.WorkingDirectory);
        Assert.Equal(Path.Combine(_subtask, "desktopjob.serie.log"), launch.LogPath);

        var step = ReadResult().GetProperty("steps")[0];
        Assert.Equal(0, step.GetProperty("exitCode").GetInt32());
        Assert.True(step.GetProperty("markerFound").GetBoolean());
        Assert.Equal(1, step.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task NonZeroExit_IsScriptFailed()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.ScriptFailed, report.Outcome);
        Assert.True(report.CountsAsAttempt);
        Assert.False(report.JobComplete);
        Assert.Equal(3, ReadResult().GetProperty("steps")[0].GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task ExitZeroWithoutMarker_IsScriptFailed()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.CreateMarker = false;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.ScriptFailed, report.Outcome);
        Assert.False(ReadResult().GetProperty("steps")[0].GetProperty("markerFound").GetBoolean());
    }

    [Fact]
    public async Task ALaunchFailure_IsStartFailedWithAReason()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.StartFailure = FakeDesktopJobProcessStarter.NotFound();

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.StartFailed, report.Outcome);
        Assert.True(report.CountsAsAttempt);
        Assert.Contains("pwsh", report.Reason, StringComparison.Ordinal);
        Assert.Equal(["minimise", "restore"], _yield.Calls);
    }

    [Fact]
    public async Task TheResult_IsWrittenAtomicallyWithTheRequestAndScriptHashes()
    {
        var requestId = Guid.NewGuid();
        WriteRequest(requestId, ("serie", 45));

        await RunAsync();

        var request = DesktopJobFiles.ReadRequest(_subtask);
        Assert.NotNull(request.Request);

        var result = ReadResult();
        Assert.Equal(requestId, result.GetProperty("requestId").GetGuid());
        Assert.Equal(DesktopJobFiles.ConfigSha256(request.Request), result.GetProperty("configSha256").GetString());
        Assert.Equal(
            Sha256Of(Path.Combine(_subtask, "desktopjob.serie.ps1")),
            result.GetProperty("steps")[0].GetProperty("scriptSha256").GetString());
        Assert.Empty(Directory.GetFiles(_subtask, "*.tmp"));
        Assert.True(DesktopJobFiles.IsResultReady(_subtask));
    }

    [Fact]
    public async Task EveryAttempt_AppendsAHeaderToTheStepLog()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));

        // Real input is re-runnable by design; a failed script is not (the runner guard).
        _starter.ExitCode = null;
        _starter.OnStart = _ => _monitor.LastWatch!.ReportMouse(flags: 0);

        await RunAsync();
        await RunAsync();

        var log = await File.ReadAllTextAsync(Path.Combine(_subtask, "desktopjob.serie.log"));
        Assert.Equal(2, log.Split("=== Desktop-Job serie", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, ReadResult().GetProperty("steps")[0].GetProperty("attempts").GetInt32());
    }

    // --- Step selection ----------------------------------------------------------------------

    [Fact]
    public async Task ACompletedFirstStepOfTheSameRequest_ResumesAtStepTwo()
    {
        WriteRequest(Guid.NewGuid(), ("eins", 45), ("zwei", 45));

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.False(first.JobComplete);
        Assert.True(second.JobComplete);
        Assert.Equal(["eins", "zwei"], _starter.Launches.Select(StepIdOf));
        Assert.Equal(2, ReadResult().GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task ACompletedJob_RunsNothingMore()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        await RunAsync();

        var again = await RunAsync();

        Assert.True(again.JobComplete);
        Assert.Single(_starter.Launches);
    }

    [Fact]
    public async Task TheSameScriptUnderANewRequestId_RunsAgain()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        await RunAsync();

        WriteRequest(Guid.NewGuid(), ("serie", 45));
        Assert.False(DesktopJobFiles.IsResultReady(_subtask));

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Equal(2, _starter.Launches.Count);
        Assert.Equal(1, ReadResult().GetProperty("steps")[0].GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task AConfigOnlyChange_RunsAgain()
    {
        var requestId = Guid.NewGuid();
        WriteRequest(requestId, ("serie", 45));
        await RunAsync();

        WriteRequest(requestId, ("serie", 50));
        Assert.False(DesktopJobFiles.IsResultReady(_subtask));

        await RunAsync();

        Assert.Equal(2, _starter.Launches.Count);
    }

    [Fact]
    public async Task AChangedScript_RunsItsStepAgain()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        await RunAsync();

        await File.WriteAllTextAsync(Path.Combine(_subtask, "desktopjob.serie.ps1"), "Write-Output 'neu'");
        Assert.False(DesktopJobFiles.IsResultReady(_subtask));

        await RunAsync();

        Assert.Equal(2, _starter.Launches.Count);
    }

    [Fact]
    public async Task AnOldResultCopiedBack_IsIgnored()
    {
        // A complete result for another request, as a restore from backup would leave it.
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        await RunAsync();
        var old = await File.ReadAllTextAsync(ResultPath);

        WriteRequest(Guid.NewGuid(), ("serie", 45));
        await File.WriteAllTextAsync(ResultPath, old);

        Assert.False(DesktopJobFiles.IsResultReady(_subtask));

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Equal(2, _starter.Launches.Count);
    }

    // --- Invalid requests ----------------------------------------------------------------------

    [Fact]
    public async Task AMissingRequest_IsStartFailedWithAReason()
    {
        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.StartFailed, report.Outcome);
        Assert.True(report.CountsAsAttempt);
        Assert.Contains(DesktopJobFiles.RequestFileName, report.Reason, StringComparison.Ordinal);
        Assert.Empty(_starter.Launches);
    }

    [Theory]
    [InlineData("""{ "version": 1, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "version")]
    [InlineData("""{ "version": 2, "requestId": "keine-guid", "idleMinutes": 15, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "requestId")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 121, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "idleMinutes")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 0, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "idleMinutes")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [] }""", "steps")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "A B", "script": "a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "A B")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" }, { "id": "a", "script": "b.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "doppelt")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "a", "script": "..\\a.ps1", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "script")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "a", "script": "..", "timeoutMinutes": 5, "marker": "C:\\m" } ] }""", "script")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 241, "marker": "C:\\m" } ] }""", "timeoutMinutes")]
    [InlineData("""{ "version": 2, "requestId": "6f0c1f5e-8f7d-4c43-9f43-3c8a8a2a0e11", "idleMinutes": 15, "steps": [ { "id": "a", "script": "a.ps1", "timeoutMinutes": 5, "marker": "relativ.txt" } ] }""", "marker")]
    [InlineData("""{ "version": 2, "requestId": """, "JSON")]
    public async Task AnInvalidRequest_IsStartFailedWithAReasonAndStartsNothing(string json, string named)
    {
        WriteRawRequest(json);

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.StartFailed, report.Outcome);
        Assert.Contains(named, report.Reason, StringComparison.Ordinal);
        Assert.Empty(_starter.Launches);
        Assert.Empty(_cpu.Windows);
        Assert.Equal(report.Reason, ReadResult().GetProperty("requestError").GetString());
        Assert.False(DesktopJobFiles.IsResultReady(_subtask));
    }

    [Fact]
    public async Task ElevenSteps_AreInvalid()
    {
        WriteRequest(Guid.NewGuid(), [.. Enumerable.Range(1, 11).Select(i => ($"s{i}", 5))]);

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.StartFailed, report.Outcome);
        Assert.Contains("steps", report.Reason, StringComparison.Ordinal);
    }

    // --- Review findings: ordering, fail-closed preconditions, trusted results -----------------

    [Fact]
    public async Task TheResult_IsWrittenOnlyAfterTheProcessTreeIsTornDownAndDisposed()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        var seen = new List<string>();
        string OnDisk() => $"{File.Exists(ResultPath)}/{File.Exists(DesktopJobFiles.AuthoritativeResultPath(_subtask))}";
        _starter.OnStart = process =>
        {
            process.OnTerminate = () => seen.Add("terminate " + OnDisk());
            process.OnDispose = () => seen.Add("dispose " + OnDisk());
        };

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Equal(["terminate False/False", "dispose False/False"], seen);
        Assert.True(File.Exists(ResultPath));
    }

    [Fact]
    public async Task AContainmentFailure_IsStartFailedWithItsReason()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.StartFailure = new DesktopJobStartException("Job-Objekt-Zuordnung fehlgeschlagen (Win32 5)");

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.StartFailed, report.Outcome);
        Assert.Equal("Job-Objekt-Zuordnung fehlgeschlagen (Win32 5)", report.Reason);
    }

    [Fact]
    public async Task AnUnavailableInputWatch_FailsClosedWithoutStartingAnything()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _monitor.WatchUnavailable = true;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.StartFailed, report.Outcome);
        Assert.Equal("Eingabeüberwachung nicht verfügbar", report.Reason);
        Assert.Empty(_starter.Launches);
        Assert.Empty(_yield.Calls);
    }

    [Fact]
    public async Task AStaleMarker_IsDeletedBeforeTheRunSoAnExitZeroWithoutANewOneFails()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        await File.WriteAllTextAsync(Marker("serie"), "aus einem alten Lauf");
        _starter.CreateMarker = false;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.ScriptFailed, report.Outcome);
        Assert.False(File.Exists(Marker("serie")));
        Assert.False(ReadResult().GetProperty("steps")[0].GetProperty("markerFound").GetBoolean());
        Assert.Contains(Marker("serie"), await File.ReadAllTextAsync(Path.Combine(_subtask, "desktopjob.serie.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForgedFolderCopyWithMatchingHashes_NeverMakesTheJobReady()
    {
        var requestId = Guid.NewGuid();
        WriteRequest(requestId, ("serie", 45));
        var request = DesktopJobFiles.ReadRequest(_subtask).Request!;
        await File.WriteAllTextAsync(ResultPath, JsonSerializer.Serialize(new
        {
            requestId = requestId.ToString("D"),
            configSha256 = DesktopJobFiles.ConfigSha256(request),
            steps = new[]
            {
                new
                {
                    stepId = "serie",
                    scriptSha256 = Sha256Of(Path.Combine(_subtask, "desktopjob.serie.ps1")),
                    outcome = DesktopJobOutcome.Completed,
                    exitCode = 0,
                    markerFound = true,
                    startedUtc = DateTimeOffset.UtcNow,
                    endedUtc = DateTimeOffset.UtcNow,
                    attempts = 1,
                },
            },
        }));

        Assert.False(DesktopJobFiles.IsResultReady(_subtask));

        // Nor does it let the runner skip the step: only its own result counts.
        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Completed, report.Outcome);
        Assert.Single(_starter.Launches);
        Assert.True(File.Exists(DesktopJobFiles.AuthoritativeResultPath(_subtask)));
        Assert.True(DesktopJobFiles.IsResultReady(_subtask));
    }

    [Fact]
    public async Task RealInputWhileTheWindowIsMinimised_AbortsBeforePwshStarts()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _yield.OnMinimise = () => _monitor.LastWatch!.ReportKeyboard(flags: 0);

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.AbortedUserInput, report.Outcome);
        Assert.True(report.CountsAsAttempt);
        Assert.Empty(_starter.Launches);
        Assert.True(_monitor.LastWatch!.Disposed);
        Assert.Equal(["minimise", "restore"], _yield.Calls);
    }

    [Fact]
    public async Task TheIdleRecheckAfterTheCpuMeasurement_RunsWhileTheInputWatchIsAlreadyActive()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        var watchesAtRead = new List<int>();
        _monitor.OnRead = () => watchesAtRead.Add(_monitor.WatchesStarted);

        await RunAsync();

        Assert.Equal([0, 1], watchesAtRead);
    }

    [Fact]
    public async Task AnUnknownCpuLoad_AbortsBusyWithItsReasonAndWithoutCountingAnAttempt()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _cpu.Unavailable = true;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.AbortedBusy, report.Outcome);
        Assert.Equal("CPU-Last unbekannt", report.Reason);
        Assert.False(report.CountsAsAttempt);
        Assert.Empty(_starter.Launches);
    }

    // --- WF_REPAIR: the job verdict ----------------------------------------------------------

    [Theory]
    [InlineData(DesktopJobOutcome.Timeout, true)]
    [InlineData(DesktopJobOutcome.ScriptFailed, true)]
    [InlineData(DesktopJobOutcome.Completed, false)]
    [InlineData(DesktopJobOutcome.AbortedUserInput, false)]
    [InlineData(DesktopJobOutcome.AbortedLocked, false)]
    [InlineData(DesktopJobOutcome.AbortedBusy, false)]
    [InlineData(DesktopJobOutcome.StartFailed, false)]
    [InlineData(null, false)]
    public void NeedsRepair_IsTrueOnlyForAStepThatRanAndFailed(string? outcome, bool expected) =>
        Assert.Equal(expected, DesktopJobOutcome.NeedsRepair(outcome));

    [Fact]
    public async Task Readiness_AfterAScriptFailure_NeedsRepair()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.ScriptFailed, report.Outcome);
        Assert.Equal(DesktopJobReadiness.NeedsRepair, DesktopJobFiles.Readiness(_subtask));
        Assert.False(DesktopJobFiles.IsResultReady(_subtask));
    }

    [Fact]
    public async Task Readiness_AfterATimeout_NeedsRepair()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 2));
        _starter.ExitCode = null;

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.Timeout, report.Outcome);
        Assert.Equal(DesktopJobReadiness.NeedsRepair, DesktopJobFiles.Readiness(_subtask));
    }

    [Fact]
    public async Task Readiness_AfterRealInput_StaysWaiting()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = null;
        _starter.OnStart = _ => _monitor.LastWatch!.ReportMouse(flags: 0);

        var report = await RunAsync();

        Assert.Equal(DesktopJobOutcome.AbortedUserInput, report.Outcome);
        Assert.Equal(DesktopJobReadiness.Waiting, DesktopJobFiles.Readiness(_subtask));
    }

    [Fact]
    public async Task Readiness_AfterALaunchFailure_StaysWaiting()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.StartFailure = FakeDesktopJobProcessStarter.NotFound();

        await RunAsync();

        Assert.Equal(DesktopJobReadiness.Waiting, DesktopJobFiles.Readiness(_subtask));
    }

    [Fact]
    public async Task Readiness_AfterTheFailedScriptWasEdited_StaysWaiting()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;
        await RunAsync();

        await File.WriteAllTextAsync(Path.Combine(_subtask, "desktopjob.serie.ps1"), "Write-Output 'repariert'");

        Assert.Equal(DesktopJobReadiness.Waiting, DesktopJobFiles.Readiness(_subtask));
    }

    [Fact]
    public async Task Readiness_UnderANewRequestId_StaysWaiting()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;
        await RunAsync();

        WriteRequest(Guid.NewGuid(), ("serie", 60));

        Assert.Equal(DesktopJobReadiness.Waiting, DesktopJobFiles.Readiness(_subtask));
    }

    [Fact]
    public async Task Readiness_WhenStepOneCompletedAndStepTwoFailed_NeedsRepair()
    {
        WriteRequest(Guid.NewGuid(), ("eins", 45), ("zwei", 45));

        // Step two exits 0 without its marker: script_failed.
        _starter.MarkerFor = launch => StepIdOf(launch) == "eins" ? Marker("eins") : null;

        await RunAsync();
        var second = await RunAsync();

        Assert.Equal("zwei", second.StepId);
        Assert.Equal(DesktopJobOutcome.ScriptFailed, second.Outcome);
        Assert.Equal(DesktopJobReadiness.NeedsRepair, DesktopJobFiles.Readiness(_subtask));
    }

    [Fact]
    public void Readiness_WithoutARequest_IsWaiting() =>
        Assert.Equal(DesktopJobReadiness.Waiting, DesktopJobFiles.Readiness(_subtask));

    [Fact]
    public async Task Readiness_WhenEveryStepCompleted_IsDelivered()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));

        await RunAsync();

        Assert.Equal(DesktopJobReadiness.Delivered, DesktopJobFiles.Readiness(_subtask));
        Assert.True(DesktopJobFiles.IsResultReady(_subtask));
    }

    // The runner guard (spec 4.3.1): a step that failed with its current script never runs again.

    [Fact]
    public async Task RunNextStep_AfterAScriptFailure_LaunchesNothingAndReportsTheFailureAgain()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;
        await RunAsync();

        var second = await RunAsync();

        Assert.Single(_starter.Launches);
        var log = await File.ReadAllTextAsync(Path.Combine(_subtask, "desktopjob.serie.log"));
        Assert.Equal(1, log.Split("=== Desktop-Job serie", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, ReadResult().GetProperty("steps")[0].GetProperty("attempts").GetInt32());
        Assert.Equal("serie", second.StepId);
        Assert.Equal(DesktopJobOutcome.ScriptFailed, second.Outcome);
        Assert.False(second.JobComplete);
    }

    [Fact]
    public async Task RunNextStep_AfterAScriptFailureWithABusyCpu_KeepsTheFailureAndMeasuresNothing()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;
        await RunAsync();
        var measurements = _cpu.Windows.Count;
        _cpu.Load = DesktopJobRunner.BusyThresholdPercent + 50;

        var second = await RunAsync();

        Assert.Equal(DesktopJobOutcome.ScriptFailed, second.Outcome);
        Assert.Equal(DesktopJobOutcome.ScriptFailed, ReadResult().GetProperty("steps")[0].GetProperty("outcome").GetString());
        Assert.Equal(measurements, _cpu.Windows.Count);
        Assert.Single(_starter.Launches);
    }

    [Fact]
    public async Task RunNextStep_AfterTheFailedScriptWasEdited_LaunchesTheStepAgain()
    {
        WriteRequest(Guid.NewGuid(), ("serie", 45));
        _starter.ExitCode = 3;
        await RunAsync();

        await File.WriteAllTextAsync(Path.Combine(_subtask, "desktopjob.serie.ps1"), "Write-Output 'repariert'");
        await RunAsync();

        Assert.Equal(2, _starter.Launches.Count);
    }

    [Fact]
    public void TheMsgStructure_HasTheWindowsLayoutIncludingLPrivate()
    {
        var msg = typeof(DesktopJobRunner).Assembly.GetType("Workflow.Services.Native.Msg", throwOnError: true)!;

        // x64: hwnd 8, message 4 + 4 padding, wParam 8, lParam 8, time 4, pt 8, lPrivate 4 = 48.
        // x86: 4 + 4 + 4 + 4 + 4 + 8 + 4 = 32.
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, System.Runtime.InteropServices.Marshal.SizeOf(msg));
        Assert.Equal(IntPtr.Size == 8 ? 44 : 28, (int)System.Runtime.InteropServices.Marshal.OffsetOf(msg, "lPrivate"));
    }

    // --- The low-level input filter the Win32 hook uses ---------------------------------------

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0x10u, true)]
    [InlineData(0x12u, true)]
    [InlineData(0x80u, false)]
    public void KeyboardFlags_DistinguishInjectedFromRealInput(uint flags, bool injected) =>
        Assert.Equal(injected, LowLevelInputFlags.IsInjectedKeyboard(flags));

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0x01u, true)]
    [InlineData(0x03u, true)]
    [InlineData(0x02u, false)]
    public void MouseFlags_DistinguishInjectedFromRealInput(uint flags, bool injected) =>
        Assert.Equal(injected, LowLevelInputFlags.IsInjectedMouse(flags));
}
