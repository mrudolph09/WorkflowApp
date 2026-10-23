using System.IO;
using System.Runtime.CompilerServices;
using Workflow.Models;

namespace Workflow.Tests;

/// <summary>
/// Aims <see cref="AppStorage.Root"/> at a temporary folder for the whole test run, so no test ever
/// writes approvals or desktop job results into the real %LOCALAPPDATA%\Workflow. Every test uses its
/// own temporary task and subtask folders, and storage is keyed by their paths, so tests running in
/// parallel never share a file.
/// </summary>
internal static class TestAppStorage
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "wf-appstorage-" + Guid.NewGuid().ToString("N"));

#pragma warning disable CA2255 // A test assembly is not a library: this is exactly the one-time setup a module initializer is for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        AppStorage.Root = Root;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp folder is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        };
    }
}
