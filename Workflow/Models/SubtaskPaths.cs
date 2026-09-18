using System.IO;

namespace Workflow.Models;

/// <summary>
/// Every path inside the Workflows tracking repository, derived from that repository's root and a
/// task name. This is the single source of truth for those locations, exactly as
/// <see cref="TaskPaths"/> is for the working directory; nothing else may compose them by hand.
/// </summary>
/// <remarks>
/// Every value this type exposes is an <em>absolute</em> path, including <see cref="TaskDirectory"/>
/// and <see cref="SubtaskPathToken"/>. Requirement 6.6 (design issue 11) forbids a substituted token
/// that a prompt or a consumer would have to prefix with a root: composing a root with a relative
/// fragment is what produced the duplicated-prefix defect in the source prompts.
/// </remarks>
public sealed class SubtaskPaths
{
    /// <summary>Name of the folder that identifies a checked-out Workflows tracking repository.</summary>
    public const string TemplateFolderName = "task_template";

    /// <summary>
    /// Characters that would turn a title into more than one path segment. On Windows these are
    /// already part of <see cref="Path.GetInvalidFileNameChars"/>; they are listed separately so the
    /// containment guarantee does not depend on the platform's invalid-character set.
    /// </summary>
    private static readonly char[] Separators = ['\\', '/', ':'];

    /// <summary>Creates the path set.</summary>
    /// <param name="workflowDirectory">Root of the Workflows tracking repository. Normalised by <see cref="WorkingDirectoryPath.Normalise"/>.</param>
    /// <param name="taskName">Task name (Taskbezeichnung). Trimmed.</param>
    /// <exception cref="ArgumentException">Either argument is null, empty or whitespace.</exception>
    public SubtaskPaths(string workflowDirectory, string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        WorkflowDirectory = WorkingDirectoryPath.Normalise(workflowDirectory);
        TaskName = taskName.Trim();

        TaskDirectory = Path.Combine(WorkflowDirectory, TaskName);
        ResultAbsolute = Path.Combine(TaskDirectory, "result.json");
        SubtasksDirectory = Path.Combine(TaskDirectory, "subtasks");
        TemplateDirectory = Path.Combine(WorkflowDirectory, TemplateFolderName);
        SubtaskPathToken = SubtasksDirectory;
    }

    /// <summary>Root of the tracking repository; also the value of the {workflow_path} token.</summary>
    public string WorkflowDirectory { get; }

    /// <summary>The task name; also the value of the {tasktitel} token and the tracking folder name.</summary>
    public string TaskName { get; }

    /// <summary>Absolute path of this task's tracking folder; also the value of the {task_path} token.</summary>
    public string TaskDirectory { get; }

    /// <summary>Absolute path of the task-level ordered index, which doubles as the decomposition flag.</summary>
    public string ResultAbsolute { get; }

    /// <summary>Absolute path of the folder holding one directory per subtask.</summary>
    public string SubtasksDirectory { get; }

    /// <summary>
    /// Absolute path of the reference template folder. Read only to recognise a checked-out
    /// tracking repository; the application never writes into it.
    /// </summary>
    public string TemplateDirectory { get; }

    /// <summary>Value substituted for the {subtask_path} token: the absolute subtasks directory.</summary>
    /// <remarks>
    /// Absolute on purpose (requirement 6.6, design issue 11). The prompts previously composed
    /// <c>{workflow_path}\{subtask_path}\...</c>, which double-prefixed every path; the only
    /// composition left to an agent is appending a subtask title it chose itself.
    /// </remarks>
    public string SubtaskPathToken { get; }

    /// <summary>Reports whether a subtask title is a single, safe path segment.</summary>
    /// <param name="title">The candidate title, as read from the index. Trimmed before validation.</param>
    /// <returns>True when the title may be used to compose a path.</returns>
    /// <remarks>
    /// <para>
    /// Titles come from JSON written by an agent and are used to compose paths the application reads
    /// and deletes files from, so a title such as <c>..\..\Windows</c> must never reach the
    /// filesystem. This check is pure: it performs no filesystem access at all, which is what lets
    /// every caller reject an unsafe entry before touching the disk (requirement 2.10).
    /// </para>
    /// <para>
    /// Only the exact names <c>.</c> and <c>..</c> are rejected as dot-names. A title that merely
    /// contains two dots inside a longer name, for example <c>ST-003-retry..fallback</c>, is an
    /// ordinary folder name and stays valid.
    /// </para>
    /// </remarks>
    public static bool IsValidTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();

        return trimmed is not ("." or "..")
            && trimmed.IndexOfAny(Separators) < 0
            && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Absolute path of one subtask's folder.</summary>
    /// <param name="title">The subtask title. Must satisfy <see cref="IsValidTitle"/>.</param>
    /// <returns>The folder path, always inside <see cref="SubtasksDirectory"/>.</returns>
    /// <exception cref="ArgumentException">The title is not a single, safe path segment.</exception>
    public string SubtaskDirectory(string title)
    {
        if (!IsValidTitle(title))
        {
            throw new ArgumentException($"Ungültiger Subtask-Name: '{title}'.", nameof(title));
        }

        return Path.Combine(SubtasksDirectory, title.Trim());
    }

    /// <summary>Absolute path of one subtask's description file.</summary>
    /// <param name="title">The subtask title. Must satisfy <see cref="IsValidTitle"/>.</param>
    /// <returns>The path of that subtask's <c>subtask.md</c>.</returns>
    /// <exception cref="ArgumentException">The title is not a single, safe path segment.</exception>
    public string SubtaskMarkdown(string title) => Path.Combine(SubtaskDirectory(title), "subtask.md");

    /// <summary>Absolute path of one subtask's status payload.</summary>
    /// <param name="title">The subtask title. Must satisfy <see cref="IsValidTitle"/>.</param>
    /// <returns>The path of that subtask's <c>status.json</c>.</returns>
    /// <exception cref="ArgumentException">The title is not a single, safe path segment.</exception>
    public string SubtaskStatusFile(string title) => Path.Combine(SubtaskDirectory(title), "status.json");

    /// <summary>Absolute path of one subtask's completion flag.</summary>
    /// <param name="title">The subtask title. Must satisfy <see cref="IsValidTitle"/>.</param>
    /// <returns>The path of that subtask's <c>result.json</c>.</returns>
    /// <exception cref="ArgumentException">The title is not a single, safe path segment.</exception>
    public string SubtaskResultFile(string title) => Path.Combine(SubtaskDirectory(title), "result.json");
}
