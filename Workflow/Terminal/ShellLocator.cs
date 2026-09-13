using System.IO;

namespace Workflow.Terminal;

/// <summary>Finds the shell that hosts the pseudo-console.</summary>
public static class ShellLocator
{
    /// <summary>Arguments passed to the shell: no banner, and it stays open after a command.</summary>
    public static string ShellArguments => "-NoLogo -NoExit";

    /// <summary>Returns PowerShell 7 when installed, otherwise Windows PowerShell.</summary>
    /// <returns>Full path to the shell executable.</returns>
    /// <exception cref="FileNotFoundException">Neither shell is present.</exception>
    public static string FindShellExecutable()
    {
        var pwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");

        if (File.Exists(pwsh))
        {
            return pwsh;
        }

        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        throw new FileNotFoundException(
            "Weder pwsh.exe noch powershell.exe wurden gefunden. Workflow benötigt eine PowerShell.");
    }
}
