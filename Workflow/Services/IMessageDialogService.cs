namespace Workflow.Services;

/// <summary>Shows a modal message box.</summary>
/// <remarks>
/// A seam of the same shape as <see cref="IDirectoryPickerService"/>: a message box is modal, so a
/// view model that showed one directly would block the test host.
/// </remarks>
public interface IMessageDialogService
{
    /// <summary>Shows an error with a single OK button and returns once it is dismissed.</summary>
    /// <param name="message">The German text to show.</param>
    public void ShowError(string message);
}
