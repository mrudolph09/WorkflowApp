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
        DoneAbsolute = Path.Combine(TaskDirectory, $"{TaskName}-done.md");
        StateAbsolute = Path.Combine(TaskDirectory, ".workflow-state.json");

        SpecRelative = $"./{TaskName}/{TaskName}_spec.md";
        PlanRelative = $"./{TaskName}/{TaskName}_plan.md";
        ReviewRelative = $"./{TaskName}/{TaskName}-review.md";
        DoneRelative = $"./{TaskName}/{TaskName}-done.md";
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

    /// <summary>Absolute path of the phase-4 completion marker written by the CLI.</summary>
    /// <remarks>
    /// Hyphen, not underscore - it matches <see cref="ReviewAbsolute"/>. The spec and plan
    /// artefacts use underscores; that inconsistency is pre-existing and the token values are
    /// already baked into specs on disk, so it is preserved rather than "fixed".
    /// </remarks>
    public string DoneAbsolute { get; }

    /// <summary>Value substituted for the {done_path} token.</summary>
    public string DoneRelative { get; }

    /// <summary>
    /// Absolute path of the workflow journal. This is the app's own bookkeeping file, not an
    /// artefact any CLI writes, and it is deliberately NOT one of the watched paths.
    /// </summary>
    public string StateAbsolute { get; }
}
