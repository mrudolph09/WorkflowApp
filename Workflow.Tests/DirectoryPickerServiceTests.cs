using System.Reflection;
using Microsoft.Win32;
using Workflow.Services;

namespace Workflow.Tests;

/// <summary>
/// Covers the directory picker's dialog-title contract (requirement 1.2). The picker opens a modal
/// Win32 folder dialog, so the caption cannot be observed by driving the dialog to completion. Two
/// things are observable and together they are the whole contract: the compiled signature - an
/// optional title whose default is the existing working-directory caption, so every current call
/// site keeps that caption while the subtask row can ask for its own - and the dialog the service
/// prepares before showing it, which is where the caption the caller passed has to end up.
/// </summary>
public sealed class DirectoryPickerServiceTests
{
    // Written with an escape rather than a literal umlaut on purpose: a literal here would share
    // this repository's source encoding with the production literal it checks, so a mis-encoded
    // caption would be mis-encoded identically in both places and the comparison would still pass.
    private const string ExpectedDefaultTitle = "Arbeitsverzeichnis ausw\u00e4hlen";

    [Fact]
    public void PickDirectory_OnTheInterface_KeepsTheInitialDirectoryAsItsRequiredFirstArgument()
    {
        var parameters = PickDirectoryOf(typeof(IDirectoryPickerService)).GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("initialDirectory", parameters[0].Name);
        Assert.False(parameters[0].IsOptional);
    }

    [Fact]
    public void PickDirectory_OnTheInterface_TakesAnOptionalTitleDefaultingToTheWorkingDirectoryCaption()
    {
        var title = PickDirectoryOf(typeof(IDirectoryPickerService)).GetParameters()[^1];

        Assert.Equal("title", title.Name);
        Assert.True(title.IsOptional, "the title must be optional so existing call sites are unchanged");
        Assert.Equal(ExpectedDefaultTitle, title.DefaultValue);
    }

    [Fact]
    public void PickDirectory_OnTheService_TakesAnOptionalTitleDefaultingToTheWorkingDirectoryCaption()
    {
        var title = PickDirectoryOf(typeof(DirectoryPickerService)).GetParameters()[^1];

        Assert.Equal("title", title.Name);
        Assert.True(title.IsOptional, "a direct caller of the service must also keep the caption");
        Assert.Equal(ExpectedDefaultTitle, title.DefaultValue);
    }

    [Fact]
    public void PickDirectory_CalledWithoutATitle_ReceivesTheWorkingDirectoryCaption()
    {
        var picker = new RecordingPicker();

        ((IDirectoryPickerService)picker).PickDirectory(null);

        Assert.Equal(ExpectedDefaultTitle, picker.LastTitle);
    }

    [Fact]
    public void PickDirectory_CalledWithATitle_ReceivesThatCaption()
    {
        var picker = new RecordingPicker();

        ((IDirectoryPickerService)picker).PickDirectory(null, "a caller supplied caption");

        Assert.Equal("a caller supplied caption", picker.LastTitle);
    }

    /// <summary>
    /// The signature tests above prove the title parameter exists; this one proves the service
    /// actually uses it. The dialog is only ever shown by <c>PickDirectory</c>, which blocks on a
    /// modal window and so cannot run in a test host, but the service configures the dialog in a
    /// separate private step that can - so the caption the caller asked for is reachable here.
    /// [StaFact] because the dialog is a WPF component and its constructor wants an STA thread.
    /// </summary>
    [StaFact]
    public void PickDirectory_PreparingItsDialog_PutsTheCallerSuppliedCaptionOnIt()
    {
        var dialog = CreateDialogOf(initialDirectory: null, title: "a caller supplied caption");

        Assert.Equal("a caller supplied caption", dialog.Title);
    }

    /// <summary>
    /// The test above proves the dialog-building step honours the caption it is given; this one
    /// proves the public method actually gives it the caller's caption rather than substituting one
    /// of its own. It has to be a separate test: a double that replaces the building step can never
    /// also exercise the real building step. The double throws before returning, so the modal
    /// <c>ShowDialog</c> that follows is never reached and no WPF dialog is ever constructed.
    /// </summary>
    [Fact]
    public void PickDirectory_GivenACaption_ForwardsItToTheStepThatBuildsTheDialog()
    {
        var service = new DirectoryPickerService();
        string? forwarded = null;

        ReplaceDialogFactoryOf(service, (_, title) =>
        {
            forwarded = title;
            throw new DialogWasAboutToBeShown();
        });

        Assert.Throws<DialogWasAboutToBeShown>(
            () => service.PickDirectory(null, "a caller supplied caption"));
        Assert.Equal("a caller supplied caption", forwarded);
    }

    /// <summary>
    /// Swaps the service's dialog-building step for a test double. Fails loudly rather than silently
    /// passing if the field it pins is renamed or retyped.
    /// </summary>
    private static void ReplaceDialogFactoryOf(
        DirectoryPickerService service,
        Func<string?, string, OpenFolderDialog> factory)
    {
        var field = typeof(DirectoryPickerService).GetField(
                "_createDialog",
                BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "DirectoryPickerService has no _createDialog field; what PickDirectory forwards is unobservable again.");

        if (field.FieldType != typeof(Func<string?, string, OpenFolderDialog>))
        {
            throw new InvalidOperationException(
                $"_createDialog is a {field.FieldType}; this test can no longer stand in for it.");
        }

        field.SetValue(service, factory);
    }

    /// <summary>Marks the point just before the modal dialog would be shown, so the test stops there.</summary>
    private sealed class DialogWasAboutToBeShown : Exception
    {
        public DialogWasAboutToBeShown()
        {
        }

        public DialogWasAboutToBeShown(string message)
            : base(message)
        {
        }

        public DialogWasAboutToBeShown(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private static OpenFolderDialog CreateDialogOf(string? initialDirectory, string title)
    {
        var createDialog = typeof(DirectoryPickerService).GetMethod(
                "CreateDialog",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "DirectoryPickerService has no private static CreateDialog; the caption is unobservable again.");

        return (OpenFolderDialog)createDialog.Invoke(null, [initialDirectory, title])!;
    }

    private static MethodInfo PickDirectoryOf(Type type) =>
        type.GetMethod(nameof(IDirectoryPickerService.PickDirectory))
        ?? throw new InvalidOperationException($"{type.Name} has no PickDirectory method.");

    /// <summary>
    /// Deliberately declares the title without a default of its own, so that what these tests
    /// observe is the default the interface contributes at the call site.
    /// </summary>
    private sealed class RecordingPicker : IDirectoryPickerService
    {
        public string? LastTitle { get; private set; }

        public string? PickDirectory(string? initialDirectory, string title)
        {
            LastTitle = title;
            return null;
        }
    }
}
