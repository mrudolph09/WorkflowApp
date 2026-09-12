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
