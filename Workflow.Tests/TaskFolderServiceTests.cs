using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskFolderServiceTests : IDisposable
{
    private readonly string _root;
    private readonly TaskFolderService _service = new();

    public TaskFolderServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Regression: a drive root must stay a root. WorkingDirectoryPath.Normalise keeps "C:\";
    // a plain TrimEnd would make the combined path "C:name" and mis-measure the length budget.
    [Fact]
    public void Validate_AcceptsANameUnderADriveRoot()
    {
        var result = _service.Validate("wf-root-test", @"C:\");

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Rename_ComposesPathsUnderADriveRootWithoutGoingDriveRelative()
    {
        // Exercised through TaskPaths so no folder is created on C:\ by the test run.
        var paths = new TaskPaths(@"C:\", "wf-root-test");

        Assert.Equal(@"C:\wf-root-test", paths.TaskDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyName(string? name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void Validate_RejectsInvalidFileNameCharacters(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("task.")]
    [InlineData("task ")]
    public void Validate_RejectsTrailingDotOrSpace(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("lpt3.txt")]
    public void Validate_RejectsReservedDeviceNames(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Fact]
    public void Validate_RejectsWhenCombinedPathExceeds240Characters()
    {
        var name = new string('x', 240);

        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("my-task")]
    [InlineData("Größe prüfen")]
    [InlineData("task 1")]
    [InlineData("COM0")]
    [InlineData("CONSOLE")]
    public void Validate_AcceptsUsableNames(string name)
    {
        var result = _service.Validate(name, _root);

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsMissingWorkingDirectory()
    {
        Assert.False(_service.Validate("my-task", null).IsValid);
        Assert.False(_service.Validate("my-task", Path.Combine(_root, "does-not-exist")).IsValid);
    }

    [Fact]
    public void EnsureCreated_CreatesTheFolder()
    {
        var paths = new TaskPaths(_root, "my-task");

        _service.EnsureCreated(paths);

        Assert.True(Directory.Exists(paths.TaskDirectory));
    }

    [Fact]
    public void DirectoryAlreadyExisted_IsTrueOnlyWhenTheFolderIsThereBeforehand()
    {
        var paths = new TaskPaths(_root, "my-task");

        Assert.False(_service.DirectoryAlreadyExisted(paths));

        _service.EnsureCreated(paths);

        Assert.True(_service.DirectoryAlreadyExisted(paths));
    }

    [Fact]
    public void EnsureCreated_IsIdempotentAndKeepsExistingContent()
    {
        var paths = new TaskPaths(_root, "my-task");
        _service.EnsureCreated(paths);
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "keep.txt"), "keep");

        _service.EnsureCreated(paths);

        Assert.True(File.Exists(Path.Combine(paths.TaskDirectory, "keep.txt")));
    }

    [Fact]
    public void Rename_MovesTheFolder()
    {
        _service.EnsureCreated(new TaskPaths(_root, "old"));

        _service.Rename(_root, "old", "new");

        Assert.False(Directory.Exists(Path.Combine(_root, "old")));
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Fact]
    public void Rename_HandlesCaseOnlyChange()
    {
        _service.EnsureCreated(new TaskPaths(_root, "task"));

        _service.Rename(_root, "task", "Task");

        var actual = Path.GetFileName(Directory.GetDirectories(_root).Single());
        Assert.Equal("Task", actual);
    }

    [Fact]
    public void Rename_IsANoOpWhenTheSourceDoesNotExist()
    {
        _service.Rename(_root, "missing", "other");

        Assert.Empty(Directory.GetDirectories(_root));
    }
}
