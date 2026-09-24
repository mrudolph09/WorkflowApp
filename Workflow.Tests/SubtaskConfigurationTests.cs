using System.IO;
using System.Reflection;
using Workflow.Models;
using Workflow.Services;
using Workflow.Tests.Fakes;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Requirements 1.6 and 1.8, and design "Components and Interfaces -&gt; Configuration and
/// Persistence" (issue 3 resolved): the subtask configuration crosses the phase-4 boundary as
/// <em>one immutable snapshot</em>, never as a live object with independent getters. The tests
/// below pin the two properties that decision exists for.
/// </para>
/// <para>
/// First, the pair can never be observed from two different moments: the live source is read
/// exactly once, at capture, and what travels afterwards is an immutable record that cannot be
/// re-read. Read counters on the fake source are therefore assertions about the design, not
/// incidental detail - a second read of the directory (for example to validate it from the source
/// instead of from the captured value) is exactly the defect the snapshot exists to prevent.
/// </para>
/// <para>
/// Second, an enabled configuration whose directory fails validation is an <em>error</em>, never a
/// fallback: it raises <see cref="SubtaskConfigurationException"/> carrying the German message from
/// <see cref="WorkflowDirectoryValidation"/>. Silently downgrading it to a normal full-context
/// implementation run would run unbounded work against the product working directory, contradicting
/// requirement 1.3, so the "it throws" assertions are load-bearing.
/// </para>
/// </remarks>
public sealed class SubtaskConfigurationTests : IDisposable
{
    private const string BlankMessage = "Bitte ein Workflow-Verzeichnis auswählen.";

    private readonly string _root;

    public SubtaskConfigurationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-subcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Capture_TakesTheEnabledFlagAndTheDirectoryFromTheLiveSource()
    {
        var tracking = CreateTrackingRoot();
        var source = new LiveSource(enabled: true, workflowDirectory: tracking);

        var captured = SubtaskConfiguration.Capture(source);

        Assert.True(captured.Enabled);
        Assert.Equal(tracking, captured.WorkflowDirectory);
    }

    [Fact]
    public void Capture_KeepsADisabledFlagBesideAConfiguredDirectory()
    {
        // Asymmetric on purpose: disabled, yet a directory is still selected. A capture that
        // derived one member from the other - or dropped the directory when the flag is off -
        // would pass a both-true fixture and fail here.
        var tracking = CreateTrackingRoot();
        var source = new LiveSource(enabled: false, workflowDirectory: tracking);

        var captured = SubtaskConfiguration.Capture(source);

        Assert.False(captured.Enabled);
        Assert.Equal(tracking, captured.WorkflowDirectory);
    }

    [Fact]
    public void Capture_ReadsEachLiveMemberExactlyOnce()
    {
        var source = new LiveSource(enabled: true, workflowDirectory: CreateTrackingRoot());

        _ = SubtaskConfiguration.Capture(source);

        Assert.Equal(1, source.EnabledReads);
        Assert.Equal(1, source.DirectoryReads);
    }

    [Fact]
    public void Capture_IgnoresEveryChangeTheSourceMakesAfterTheSnapshotWasTaken()
    {
        var tracking = CreateTrackingRoot();
        var source = new LiveSource(enabled: true, workflowDirectory: tracking);

        var captured = SubtaskConfiguration.Capture(source);

        // The user toggles the checkbox and picks a different folder while phase 4 runs.
        source.Enabled = false;
        source.Directory = CreateUnmarkedDirectory();

        Assert.True(captured.Enabled);
        Assert.Equal(tracking, captured.WorkflowDirectory);
    }

    [Fact]
    public void Capture_RejectsANullSource()
    {
        Assert.Throws<ArgumentNullException>(() => SubtaskConfiguration.Capture(null!));
    }

    [Fact]
    public void CaptureValidated_StillReadsEachLiveMemberExactlyOnce()
    {
        // Validation must run against the captured directory, not against a second read of the
        // live source - that second read is precisely the two-moment observation requirement 1.6
        // forbids.
        var source = new LiveSource(enabled: true, workflowDirectory: CreateTrackingRoot());

        _ = SubtaskConfiguration.CaptureValidated(source);

        Assert.Equal(1, source.EnabledReads);
        Assert.Equal(1, source.DirectoryReads);
    }

    [Fact]
    public void CaptureValidated_ReturnsTheEnabledSnapshotWhenTheDirectoryIsATrackingRepository()
    {
        var tracking = CreateTrackingRoot();
        var source = new LiveSource(enabled: true, workflowDirectory: tracking);

        var captured = SubtaskConfiguration.CaptureValidated(source);

        Assert.True(captured.Enabled);
        Assert.Equal(tracking, captured.WorkflowDirectory);
    }

    [Fact]
    public void CaptureValidated_RaisesTheGermanMarkerMessageWhenEnabledAndTheMarkerIsMissing()
    {
        var unmarked = CreateUnmarkedDirectory();
        var source = new LiveSource(enabled: true, workflowDirectory: unmarked);

        var error = Assert.Throws<SubtaskConfigurationException>(
            () => SubtaskConfiguration.CaptureValidated(source));

        Assert.Equal(MarkerMessage(unmarked), error.ValidationMessage);
        Assert.Equal(MarkerMessage(unmarked), error.Message);
    }

    [Fact]
    public void CaptureValidated_RaisesTheGermanBlankMessageWhenEnabledWithoutADirectory()
    {
        var source = new LiveSource(enabled: true, workflowDirectory: "   ");

        var error = Assert.Throws<SubtaskConfigurationException>(
            () => SubtaskConfiguration.CaptureValidated(source));

        Assert.Equal(BlankMessage, error.ValidationMessage);
    }

    [Fact]
    public void CaptureValidated_RaisesTheGermanMissingDirectoryMessageWhenEnabledAndTheFolderIsGone()
    {
        var absent = Path.Combine(_root, "weg");
        var source = new LiveSource(enabled: true, workflowDirectory: absent);

        var error = Assert.Throws<SubtaskConfigurationException>(
            () => SubtaskConfiguration.CaptureValidated(source));

        Assert.Equal(WorkflowDirectoryValidation.Validate(absent).ErrorMessage, error.ValidationMessage);
        Assert.Contains("existiert nicht", error.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureValidated_NeverDowngradesAnEnabledButInvalidConfigurationToANormalRun()
    {
        // The tempting implementation returns SubtaskConfiguration.Disabled here so phase 4 falls
        // through to the normal implementation session. Requirement 1.8 forbids exactly that, so
        // this test asserts on the returned value of a call that must not return at all.
        var source = new LiveSource(enabled: true, workflowDirectory: CreateUnmarkedDirectory());

        SubtaskConfiguration? returned = null;
        var threw = false;
        try
        {
            returned = SubtaskConfiguration.CaptureValidated(source);
        }
        catch (SubtaskConfigurationException)
        {
            threw = true;
        }

        Assert.True(threw, "An enabled configuration with an invalid directory must raise, not fall back.");
        Assert.Null(returned);
    }

    [Fact]
    public void CaptureValidated_AcceptsADisabledConfigurationEvenWhenItsDirectoryIsUnusable()
    {
        // Disabled is not a validated state: the directory is irrelevant, and the leftover value is
        // carried through untouched so phase-4 persistence records what the user actually selected.
        var absent = Path.Combine(_root, "nicht-da");
        var source = new LiveSource(enabled: false, workflowDirectory: absent);

        var captured = SubtaskConfiguration.CaptureValidated(source);

        Assert.False(captured.Enabled);
        Assert.Equal(absent, captured.WorkflowDirectory);
    }

    [Fact]
    public void EnsureUsable_RaisesForAnEnabledSnapshotWhoseDirectoryLostItsMarker()
    {
        // The marker can disappear between the start gate and phase-4 entry; requirement 1.8
        // re-runs the check on the captured snapshot itself.
        var tracking = CreateTrackingRoot();
        var configuration = new SubtaskConfiguration(true, tracking);
        Directory.Delete(Path.Combine(tracking, SubtaskPaths.TemplateFolderName), recursive: true);

        var error = Assert.Throws<SubtaskConfigurationException>(configuration.EnsureUsable);

        Assert.Equal(MarkerMessage(tracking), error.ValidationMessage);
    }

    [Fact]
    public void EnsureUsable_AcceptsADisabledSnapshotWithoutTouchingTheFilesystem()
    {
        var configuration = new SubtaskConfiguration(false, Path.Combine(_root, "existiert-nicht"));

        configuration.EnsureUsable();
    }

    [Fact]
    public void IsEnabled_TreatsANullConfigurationAsDisabled()
    {
        Assert.False(SubtaskConfiguration.IsEnabled(null));
    }

    [Fact]
    public void IsEnabled_FollowsTheCapturedFlagForANonNullConfiguration()
    {
        var tracking = CreateTrackingRoot();

        Assert.True(SubtaskConfiguration.IsEnabled(new SubtaskConfiguration(true, tracking)));
        Assert.False(SubtaskConfiguration.IsEnabled(new SubtaskConfiguration(false, tracking)));
    }

    [Fact]
    public void Disabled_IsTheOffSnapshotWithNoDirectory()
    {
        Assert.False(SubtaskConfiguration.Disabled.Enabled);
        Assert.Null(SubtaskConfiguration.Disabled.WorkflowDirectory);
    }

    [Fact]
    public void SubtaskConfiguration_ExposesNoPublicSetterOnAnyMember()
    {
        // Structural, not incidental: a mutable snapshot would let the pair drift apart after
        // capture, which is the whole failure mode requirement 1.6 rules out.
        var mutable = typeof(SubtaskConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true } setter && !IsInitOnly(setter))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(mutable);
        Assert.Empty(typeof(SubtaskConfiguration).GetFields(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void SubtaskConfigurationException_CarriesTheStandardConstructorsAndItsValidationMessage()
    {
        var withMessage = new SubtaskConfigurationException("Fehler");
        Assert.Equal("Fehler", withMessage.ValidationMessage);

        var inner = new InvalidOperationException("innen");
        var wrapped = new SubtaskConfigurationException("Fehler", inner);
        Assert.Equal("Fehler", wrapped.ValidationMessage);
        Assert.Same(inner, wrapped.InnerException);

        Assert.NotNull(new SubtaskConfigurationException().ValidationMessage);
    }

    [Fact]
    public void WorkflowRunRequest_DefaultsTheCapturedConfigurationAndProgressSinkToNull()
    {
        // The compile-compatibility guarantee, asserted rather than argued: the five original
        // positional arguments still construct a complete request, and a request built that way
        // means "subtask mode disabled".
        var request = new WorkflowRunRequest(
            new TaskPaths(_root, "Task"),
            "Beschreibung",
            new FakeTerminalController(),
            new ManualPhaseSignal(),
            new Progress<PhaseProgress>(_ => { }));

        Assert.Equal(WorkflowPhase.Specification, request.StartPhase);
        Assert.Null(request.Subtasks);
        Assert.Null(request.SubtaskProgress);
        Assert.False(SubtaskConfiguration.IsEnabled(request.Subtasks?.Value));
    }

    [Fact]
    public void WorkflowRunRequest_CarriesTheDeferredCaptureAndTheSubtaskProgressSink()
    {
        var tracking = CreateTrackingRoot();
        var deferred = SubtaskConfiguration.Deferred(new LiveSource(enabled: true, workflowDirectory: tracking));
        var sink = new Progress<SubtaskProgress>(_ => { });

        var request = new WorkflowRunRequest(
            new TaskPaths(_root, "Task"),
            "Beschreibung",
            new FakeTerminalController(),
            new ManualPhaseSignal(),
            new Progress<PhaseProgress>(_ => { }),
            WorkflowPhase.Implementation,
            deferred,
            sink);

        Assert.Same(deferred, request.Subtasks);
        Assert.Same(sink, request.SubtaskProgress);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);

        Assert.True(request.Subtasks!.Value.Enabled);
        Assert.Equal(tracking, request.Subtasks.Value.WorkflowDirectory);
    }

    [Fact]
    public void WorkflowRunRequest_CarriesTheSnapshotAndNeverTheLiveCaptureSource()
    {
        // The structural reason a two-moment read is impossible downstream: no member of the run
        // request can hand the orchestrator anything it could re-read.
        //
        // Task 2.5 turned the member into a DEFERRED capture, so that the one read happens at
        // phase-4 entry rather than at run entry (requirement 1.6). The invariant is unchanged and
        // still compile-enforced: Lazy<T> evaluates its factory at most once and afterwards serves
        // the same immutable record forever, so what crosses the boundary is still a snapshot, not
        // something re-readable. The scan below is correspondingly STRICTER than the one task 2.3
        // wrote - it looks through generic arguments too, so a Lazy<ISubtaskConfiguration>, which
        // would hand the orchestrator the live source after all, is rejected as well.
        var subtasks = typeof(WorkflowRunRequest).GetProperty("Subtasks");
        Assert.NotNull(subtasks);
        Assert.Equal(typeof(Lazy<SubtaskConfiguration>), subtasks.PropertyType);

        var live = typeof(WorkflowRunRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => CanReachTheLiveSource(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(live);
    }

    [Fact]
    public void Deferred_TakesNoSnapshotUntilItIsForced()
    {
        // Requirement 1.6 puts the capture at phase-4 ENTRY, so merely building the run request
        // must read nothing. That is also what leaves the whole of phases 1-3 free to be edited.
        var source = new LiveSource(enabled: true, workflowDirectory: CreateTrackingRoot());

        var request = new WorkflowRunRequest(
            new TaskPaths(_root, "Task"),
            "Beschreibung",
            new FakeTerminalController(),
            new ManualPhaseSignal(),
            new Progress<PhaseProgress>(_ => { }),
            WorkflowPhase.Specification,
            SubtaskConfiguration.Deferred(source));

        Assert.False(request.Subtasks!.IsValueCreated);
        Assert.Equal(0, source.EnabledReads);
        Assert.Equal(0, source.DirectoryReads);
    }

    [Fact]
    public void Deferred_ReadsEachMemberExactlyOnceHoweverOftenItIsForced()
    {
        // The orchestrator touches the member twice - once in the phase-4 guard, once to use the
        // record - so deferring the capture only preserves task 2.3's invariant if the second
        // access cannot produce a second pair. Lazy<T> makes that a property of the type rather
        // than of convention: a plain Func<SubtaskConfiguration> would fail this test.
        var tracking = CreateTrackingRoot();
        var source = new LiveSource(enabled: true, workflowDirectory: tracking);
        var deferred = SubtaskConfiguration.Deferred(source);

        var first = deferred.Value;
        var second = deferred.Value;
        var third = deferred.Value;

        Assert.Same(first, second);
        Assert.Same(second, third);
        Assert.Equal(1, source.EnabledReads);
        Assert.Equal(1, source.DirectoryReads);
    }

    [Fact]
    public void Deferred_FollowsAnEditMadeBeforeTheCaptureAndIgnoresOneMadeAfterIt()
    {
        // Both halves of requirement 1.6 against one fixture: an edit made while phases 1-3 run
        // reaches the run, and from the capture onwards the pair is frozen.
        var tracking = CreateTrackingRoot();
        var source = new LiveSource(enabled: false, workflowDirectory: null);
        var deferred = SubtaskConfiguration.Deferred(source);

        source.Enabled = true;
        source.Directory = tracking;

        var captured = deferred.Value;

        Assert.True(captured.Enabled);
        Assert.Equal(tracking, captured.WorkflowDirectory);

        source.Enabled = false;
        source.Directory = null;

        Assert.Same(captured, deferred.Value);
        Assert.True(deferred.Value.Enabled);
        Assert.Equal(tracking, deferred.Value.WorkflowDirectory);
    }

    [Fact]
    public void Deferred_RejectsANullSourceWhereTheRequestIsBuilt_NotInsideTheRun()
    {
        // The factory now runs on the orchestrator's thread, inside a run nobody awaits at the
        // call site, so a null source must fail here - while the stack still names the caller -
        // rather than surfacing later as a run failure.
        Assert.Throws<ArgumentNullException>(() => SubtaskConfiguration.Deferred(null!));
    }

    /// <summary>
    /// Whether a run-request member could hand the orchestrator the live, re-readable source -
    /// either directly or wrapped in a generic such as <see cref="Lazy{T}"/>.
    /// </summary>
    private static bool CanReachTheLiveSource(Type type) =>
        typeof(ISubtaskConfiguration).IsAssignableFrom(type)
        || Array.Exists(type.GetGenericArguments(), CanReachTheLiveSource);

    private static string MarkerMessage(string directory) =>
        $"Das Verzeichnis '{directory}' enthält keinen Ordner 'task_template' und ist daher nicht das ausgecheckte Workflows-Repository.";

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    /// <summary>A tracking root that validates: it carries the template marker.</summary>
    private string CreateTrackingRoot()
    {
        var root = Path.Combine(_root, "Workflows");
        Directory.CreateDirectory(Path.Combine(root, SubtaskPaths.TemplateFolderName));
        return root;
    }

    /// <summary>A directory that exists but is not a tracking repository - no marker folder.</summary>
    private string CreateUnmarkedDirectory()
    {
        var directory = Path.Combine(_root, "NurEinOrdner");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The live, editable state the tab owns. It counts reads so a test can prove the capture reads
    /// each member once, and it can be mutated afterwards so a test can prove the snapshot does not
    /// follow it.
    /// </summary>
    private sealed class LiveSource : ISubtaskConfiguration
    {
        public LiveSource(bool enabled, string? workflowDirectory)
        {
            Enabled = enabled;
            Directory = workflowDirectory;
        }

        public bool Enabled { get; set; }

        public string? Directory { get; set; }

        public int EnabledReads { get; private set; }

        public int DirectoryReads { get; private set; }

        public bool SubtasksEnabled
        {
            get
            {
                EnabledReads++;
                return Enabled;
            }
        }

        public string? WorkflowDirectory
        {
            get
            {
                DirectoryReads++;
                return Directory;
            }
        }
    }
}
